using System.Text.Json;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static M365SecurityDashboard.Api.Services.Common.JsonRead;
using M365SecurityDashboard.Api.Services.Common;

namespace M365SecurityDashboard.Api.Services;

public sealed class GraphCollector(
    AppDbContext db,
    GraphApiClient graph,
    IOptions<GraphOptions> options,
    TenantGraphCredentials credentials,
    GraphMetrics graphMetrics,
    ILogger<GraphCollector> logger,
    PurviewAuditCollector? purviewAudit = null)
{
    // Install-wide, non-credential settings (feed paths, lookbacks). Whether Graph
    // is reachable for the *current tenant* is a per-tenant question: `credentials`.
    private readonly GraphOptions _options = options.Value;

    // One collection at a time PER TENANT: the manual endpoint and the background
    // worker would otherwise race the (TenantId, Service, AlertType, ExternalId)
    // unique index. Different tenants may collect concurrently (TenantIterator).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public async Task<CollectionRun> CollectAsync(CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(db.CurrentTenantIdOrNull ?? Guid.Empty, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
            throw new CollectionBusyException();
        try
        {
            return await CollectCoreAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CollectionRun> CollectCoreAsync(CancellationToken ct)
    {
        var run = new CollectionRun { StartedAt = DateTimeOffset.UtcNow, Status = CollectionStatus.Started };
        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Real per-run Graph traffic = the delta of the in-process counters across
        // this run. Snapshotted here and settled on the run at completion below.
        var (startReq, startThrottle) = graphMetrics.Snapshot();

        try
        {
            var sources = BuildSources();
            var failures = new List<(string Source, string Error)>();
            foreach (var source in sources)
            {
                try
                {
                    var read = await graph.ReadCollectionAsync(source.Path, ct);
                    var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in read.Items)
                    {
                        var alert = source.Map(row);
                        if (alert.ExternalId is not null) returned.Add(alert.ExternalId);
                        await UpsertAlertAsync(alert, ct);
                        run.AlertsUpserted++;
                    }
                    // Most feeds are filtered (non-compliant, unresolved, inside the
                    // lookback), so a row that stops coming back has cleared. Only a
                    // complete read proves that; a truncated one proves nothing.
                    if (read.Complete) await ResolveMissingAsync(source, returned, ct);
                }
                catch (Exception ex)
                {
                    run.SourceFailures++;
                    // Store an actionable sentence, not the raw Graph JSON — this
                    // string is what the Collection Health card and Runs view show.
                    failures.Add((source.Name, GraphErrorHint.Describe(ex.Message, source.Name)));
                    logger.LogWarning(ex, "Graph source {SourceName} failed", source.Name);
                }
            }

            // Tenant audit events feed the activity-based alert policies.
            // Failures here count as a source failure but never sink the run.
            try
            {
                await CollectAuditEventsAsync(ct);
            }
            catch (Exception ex)
            {
                run.SourceFailures++;
                failures.Add(("Tenant audit events", GraphErrorHint.Describe(ex.Message, "Tenant audit events")));
                logger.LogWarning(ex, "Tenant audit event collection failed");
            }

            // Copilot agent and Power Platform activity from the Purview audit log. Its
            // permission is optional and it keeps its own hourly cadence and status
            // (PurviewAuditState), so it is never one of this run's source failures:
            // a tenant that hasn't granted it must not read as partly connected.
            if (purviewAudit is not null)
            {
                try
                {
                    await purviewAudit.CollectAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Purview audit collection failed");
                }
            }

            run.SourceFailureDetails = failures.Count > 0
                ? JsonSerializer.Serialize(failures.Select(f => new { source = f.Source, error = f.Error }))
                : null;
            // Nothing came back at all (revoked consent, expired secret): a failed
            // collection, with the reason, so the worker backs off and the Clients
            // view shows it. The audit-event pull counts as one of the sources.
            if (run.SourceFailures >= sources.Count + 1)
            {
                run.Status = CollectionStatus.Failed;
                run.Error = $"Every Graph source failed. {failures[0].Source}: {failures[0].Error}";
            }
            else
            {
                run.Status = CollectionStatus.Completed;
            }
            run.CompletedAt = DateTimeOffset.UtcNow;
            var (endReq, endThrottle) = graphMetrics.Snapshot();
            run.GraphRequestCount = (int)(endReq - startReq);
            run.GraphThrottleCount = (int)(endThrottle - startThrottle);
            await AccumulateCountersAsync(run.GraphRequestCount, run.GraphThrottleCount, ct);

            if (run.Status != CollectionStatus.Failed)
            {
                await CaptureTrendSnapshotAsync(ct);
            }

            await db.SaveChangesAsync(ct);
            return run;
        }
        catch (Exception ex)
        {
            run.Status = CollectionStatus.Failed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Error = ex.Message;
            var (endReq, endThrottle) = graphMetrics.Snapshot();
            run.GraphRequestCount = (int)(endReq - startReq);
            run.GraphThrottleCount = (int)(endThrottle - startThrottle);
            await AccumulateCountersAsync(run.GraphRequestCount, run.GraphThrottleCount, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Fold this run's real Graph delta into the durable cumulative counters
    /// (persisted, so the "_total" metrics survive a service restart).
    /// </summary>
    private Task AccumulateCountersAsync(int graphRequests, int graphThrottled, CancellationToken ct)
        => MetricsCounterStore.AddAsync(db, graphRequests, graphThrottled, evaluations: 0, ct);

    /// <summary>
    /// Incrementally pull Entra directory-audit records into the AuditEvents
    /// store — the raw material for activity-based alert policies. Watermarked
    /// on the newest stored event (with a 10-minute overlap for late arrivals);
    /// first run looks back 24h. Records are deduped on (Source, ExternalId).
    /// </summary>
    private async Task CollectAuditEventsAsync(CancellationToken ct)
    {
        var newest = await db.AuditEvents
            .Where(e => e.Source == "directoryAudit")
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
        var since = (newest?.AddMinutes(-10) ?? DateTimeOffset.UtcNow.AddHours(-24))
            .UtcDateTime.ToString("O");

        var rows = await graph.GetCollectionAsync(
            WithFilter("/v1.0/auditLogs/directoryAudits?$top=200", $"activityDateTime ge {since}"), ct);
        if (rows.Count == 0) return;

        var incomingIds = rows.Select(r => Text(r, "id")).Where(id => id != null).Cast<string>().ToList();
        var known = (await db.AuditEvents
            .Where(e => e.Source == "directoryAudit" && incomingIds.Contains(e.ExternalId))
            .Select(e => e.ExternalId)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var r in rows)
        {
            var id = Text(r, "id");
            if (id is null || !known.Add(id)) continue; // dedupe within batch + store

            string? targetName = null;
            if (r.TryGetProperty("targetResources", out var targets) && targets.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in targets.EnumerateArray())
                {
                    targetName = Text(t, "displayName") ?? Text(t, "userPrincipalName");
                    if (targetName != null) break;
                }
            }

            db.AuditEvents.Add(new AuditEvent
            {
                ExternalId = id,
                Source = "directoryAudit",
                Activity = TextLimits.Truncate(Text(r, "activityDisplayName") ?? "Unknown activity", 200),
                Category = TextLimits.TruncateOrNull(Text(r, "category"), 80),
                ActorUpn = TextLimits.TruncateOrNull(Text(r, "initiatedBy", "user", "userPrincipalName"), 320),
                ActorApp = TextLimits.TruncateOrNull(Text(r, "initiatedBy", "app", "displayName"), 200),
                TargetName = TextLimits.TruncateOrNull(targetName, 320),
                Result = TextLimits.TruncateOrNull(Text(r, "result"), 20),
                OccurredAt = GetDate(r, "activityDateTime"),
                CollectedAt = DateTimeOffset.UtcNow,
                RawJson = r.GetRawText(),
            });
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Collected {Count} new tenant audit events", added);
        }
    }

    private List<GraphSource> BuildSources()
    {
        var signInCutoff = DateTimeOffset.UtcNow.AddHours(-_options.SignInLookbackHours).UtcDateTime.ToString("O");
        var deviceCutoff = DateTimeOffset.UtcNow.AddDays(-_options.DevicesNotCheckedInDays).UtcDateTime.ToString("O");

        return
        [
            new("Risky users", "/v1.0/identityProtection/riskyUsers?$top=50", M365ServiceArea.EntraId, "RiskyUser", MapRiskyUser),
            new("Risky sign-ins", WithFilter("/v1.0/auditLogs/signIns?$top=50", $"riskState ne 'none' and createdDateTime ge {signInCutoff}"), M365ServiceArea.EntraId, "RiskySignIn", MapRiskySignIn),
            new("Failed sign-ins", WithFilter("/v1.0/auditLogs/signIns?$top=50", $"status/errorCode ne 0 and createdDateTime ge {signInCutoff}"), M365ServiceArea.EntraId, "FailedSignIn", MapFailedSignIn),
            new("MFA registration", "/v1.0/reports/authenticationMethods/userRegistrationDetails?$top=100", M365ServiceArea.EntraId, "MfaStatus", MapMfaStatus),
            new("Non-compliant devices", WithFilter("/v1.0/deviceManagement/managedDevices?$top=50", "complianceState ne 'compliant'"), M365ServiceArea.Intune, "NonCompliantDevice", MapNonCompliantDevice),
            new("Devices not checked in", WithFilter("/v1.0/deviceManagement/managedDevices?$top=50", $"lastSyncDateTime lt {deviceCutoff}"), M365ServiceArea.Intune, "DeviceNotCheckedIn", MapDeviceNotCheckedIn),
            new("Defender incidents", "/v1.0/security/incidents?$top=50", M365ServiceArea.DefenderXdr, "Incident", MapDefenderIncident),
            new("Defender alerts", "/v1.0/security/alerts_v2?$top=50", M365ServiceArea.DefenderXdr, "Alert", MapDefenderAlert),
            new("Malware detections", WithFilter("/v1.0/security/alerts_v2?$top=50", "category eq 'Malware'"), M365ServiceArea.DefenderXdr, "MalwareDetection", MapMalwareDetection),
            new("Quarantined messages", _options.ExchangeQuarantinePath, M365ServiceArea.ExchangeOnline, "QuarantinedMessage", MapQuarantinedMessage),
            new("Mail flow issues", _options.MailFlowIssuesPath, M365ServiceArea.ExchangeOnline, "MailFlowIssue", MapMailFlowIssue),
            new("Service health issues", WithFilter("/v1.0/admin/serviceAnnouncement/issues?$top=50", "isResolved eq false"), M365ServiceArea.ServiceHealth, "ServiceHealthIssue", MapServiceHealth),
            // Single-object endpoint: GetCollectionAsync yields the settings object once.
            // Requires SharePointTenantSettings.Read.All (counts as a source failure if missing).
            new("SharePoint sharing posture", "/v1.0/admin/sharepoint/settings", M365ServiceArea.SharePoint, "SharingPosture", MapSharingPosture)
        ];
    }

    private static string WithFilter(string path, string filter) => $"{path}&$filter={Uri.EscapeDataString(filter)}";

    /// <summary>
    /// After a complete read of one source: its open alerts that did not come
    /// back are resolved (the device became compliant, the advisory closed, the
    /// sign-in left the lookback window). Rows the run just upserted are the
    /// same tracked instances, so they are skipped by the returned-id check.
    /// </summary>
    private async Task ResolveMissingAsync(GraphSource source, HashSet<string> returned, CancellationToken ct)
    {
        var open = await db.SecurityAlerts
            .Where(a => a.Service == source.Service && a.AlertType == source.AlertType && !a.IsResolved && a.ExternalId != null)
            .ToListAsync(ct);
        foreach (var alert in open.Where(a => !returned.Contains(a.ExternalId!)))
        {
            alert.IsResolved = true;
            alert.LastUpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task UpsertAlertAsync(SecurityAlert next, CancellationToken ct)
    {
        var current = next.ExternalId is null
            ? null
            : await db.SecurityAlerts.FirstOrDefaultAsync(a =>
                a.Service == next.Service && a.AlertType == next.AlertType && a.ExternalId == next.ExternalId, ct);

        if (current is null)
        {
            db.SecurityAlerts.Add(next);
            return;
        }

        current.Severity = next.Severity;
        current.Title = next.Title;
        current.Description = next.Description;
        current.UserPrincipalName = next.UserPrincipalName;
        current.DeviceName = next.DeviceName;
        current.PortalUrl = next.PortalUrl;
        current.LastUpdatedAt = next.LastUpdatedAt;
        current.IsResolved = next.IsResolved;
        current.RawJson = next.RawJson;
    }

    // riskyUsers returns every user who was ever at risk; riskState says whether still is.
    private static SecurityAlert MapRiskyUser(JsonElement e) => Alert(e, M365ServiceArea.EntraId, "RiskyUser",
        Text(e, "id"), SeverityFromRisk(Text(e, "riskLevel")), $"Risky user: {Text(e, "userPrincipalName") ?? Text(e, "id")}",
        Text(e, "riskState"), Text(e, "userPrincipalName"), null, GetDate(e, "riskLastUpdatedDateTime"),
        isResolved: IsRiskClosed(Text(e, "riskState")));

    private static SecurityAlert MapRiskySignIn(JsonElement e) => Alert(e, M365ServiceArea.EntraId, "RiskySignIn",
        Text(e, "id"), SeverityFromRisk(Text(e, "riskLevelAggregated")), $"Risky sign-in: {Text(e, "userPrincipalName")}",
        Text(e, "riskState"), Text(e, "userPrincipalName"), Text(e, "deviceDetail", "displayName"), GetDate(e, "createdDateTime"),
        isResolved: IsRiskClosed(Text(e, "riskState")));

    private static SecurityAlert MapFailedSignIn(JsonElement e) => Alert(e, M365ServiceArea.EntraId, "FailedSignIn",
        Text(e, "id"), AlertSeverity.Medium, $"Failed sign-in: {Text(e, "userPrincipalName")}",
        Text(e, "status", "failureReason"), Text(e, "userPrincipalName"), Text(e, "deviceDetail", "displayName"), GetDate(e, "createdDateTime"));

    private static SecurityAlert MapMfaStatus(JsonElement e)
    {
        var registered = IsTrue(e, "isMfaRegistered");
        return Alert(e, M365ServiceArea.EntraId, "MfaStatus", Text(e, "id") ?? Text(e, "userPrincipalName"),
            registered ? AlertSeverity.Informational : AlertSeverity.High,
            registered ? $"MFA registered: {Text(e, "userPrincipalName")}" : $"MFA missing: {Text(e, "userPrincipalName")}",
            Text(e, "defaultMfaMethod"), Text(e, "userPrincipalName"), null, DateTimeOffset.UtcNow,
            isResolved: registered);
    }

    private static SecurityAlert MapNonCompliantDevice(JsonElement e) => Alert(e, M365ServiceArea.Intune, "NonCompliantDevice",
        Text(e, "id"), AlertSeverity.High, $"Non-compliant device: {Text(e, "deviceName")}",
        Text(e, "complianceState"), Text(e, "userPrincipalName"), Text(e, "deviceName"), GetDate(e, "lastSyncDateTime"));

    private static SecurityAlert MapDeviceNotCheckedIn(JsonElement e) => Alert(e, M365ServiceArea.Intune, "DeviceNotCheckedIn",
        Text(e, "id"), AlertSeverity.Medium, $"Device not checked in: {Text(e, "deviceName")}",
        $"Last sync: {Text(e, "lastSyncDateTime")}", Text(e, "userPrincipalName"), Text(e, "deviceName"), GetDate(e, "lastSyncDateTime"));

    private static SecurityAlert MapDefenderIncident(JsonElement e) => Alert(e, M365ServiceArea.DefenderXdr, "Incident",
        Text(e, "id"), SeverityFromString(Text(e, "severity")), $"Defender incident: {Text(e, "displayName") ?? Text(e, "id")}",
        Text(e, "status"), null, null, GetDate(e, "createdDateTime"), Text(e, "incidentWebUrl"), IsClosed(Text(e, "status")));

    private static SecurityAlert MapDefenderAlert(JsonElement e) => Alert(e, M365ServiceArea.DefenderXdr, "Alert",
        Text(e, "id"), SeverityFromString(Text(e, "severity")), $"Defender alert: {Text(e, "title") ?? Text(e, "id")}",
        Text(e, "description"), null, null, GetDate(e, "createdDateTime"), Text(e, "alertWebUrl"), IsClosed(Text(e, "status")));

    private static SecurityAlert MapMalwareDetection(JsonElement e) => Alert(e, M365ServiceArea.DefenderXdr, "MalwareDetection",
        Text(e, "id"), SeverityFromString(Text(e, "severity")), $"Malware detection: {Text(e, "title") ?? Text(e, "id")}",
        Text(e, "description"), null, null, GetDate(e, "createdDateTime"), Text(e, "alertWebUrl"), IsClosed(Text(e, "status")));

    private static SecurityAlert MapQuarantinedMessage(JsonElement e) => Alert(e, M365ServiceArea.ExchangeOnline, "QuarantinedMessage",
        Text(e, "id"), SeverityFromString(Text(e, "severity")), $"Quarantined message: {Text(e, "title") ?? Text(e, "subject") ?? Text(e, "id")}",
        Text(e, "description"), Text(e, "recipientEmailAddress"), null, GetDate(e, "createdDateTime"), Text(e, "alertWebUrl"), IsClosed(Text(e, "status")));

    private static SecurityAlert MapMailFlowIssue(JsonElement e) => Alert(e, M365ServiceArea.ExchangeOnline, "MailFlowIssue",
        Text(e, "id"), AlertSeverity.High, $"Exchange issue: {Text(e, "title") ?? Text(e, "id")}",
        Text(e, "impactDescription"), null, null, GetDate(e, "startDateTime"), Text(e, "details", "url"), IsTrue(e, "isResolved"));

    /// <summary>
    /// One tenant-wide posture alert (fixed ExternalId so it upserts in place):
    /// open at the worst finding's severity while the sharing posture is risky,
    /// auto-resolved once the findings clear.
    /// </summary>
    private static SecurityAlert MapSharingPosture(JsonElement e)
    {
        var findings = SharingPostureAnalyzer.Analyze(SharingPostureAnalyzer.Parse(e));
        var worst = findings.FirstOrDefault();
        var clean = worst is null;
        return Alert(e, M365ServiceArea.SharePoint, "SharingPosture", "tenant-sharing-settings",
            clean ? AlertSeverity.Informational : SeverityFromString(worst!.Severity),
            clean ? "SharePoint/OneDrive sharing posture is healthy"
                  : $"Risky sharing posture: {worst!.Title}",
            clean ? "No risky external-sharing settings detected."
                  : string.Join(" | ", findings.Select(f => f.Title)),
            null, null, DateTimeOffset.UtcNow, null, isResolved: clean);
    }

    private static SecurityAlert MapServiceHealth(JsonElement e) => Alert(e, M365ServiceArea.ServiceHealth, "ServiceHealthIssue",
        Text(e, "id"), SeverityFromClassification(Text(e, "classification")), $"{Text(e, "service")}: {Text(e, "title") ?? Text(e, "id")}",
        Text(e, "impactDescription"), null, null, GetDate(e, "startDateTime"), null, IsTrue(e, "isResolved"));

    private static SecurityAlert Alert(JsonElement raw, M365ServiceArea service, string type, string? externalId,
        AlertSeverity severity, string title, string? description, string? user, string? device, DateTimeOffset detected,
        string? url = null, bool isResolved = false) => new()
    {
        Service = service,
        AlertType = type,
        ExternalId = externalId,
        Severity = severity,
        Title = title,
        Description = description,
        UserPrincipalName = user,
        DeviceName = device,
        PortalUrl = url,
        DetectedAt = detected,
        LastUpdatedAt = DateTimeOffset.UtcNow,
        IsResolved = isResolved,
        RawJson = raw.GetRawText()
    };

    private static DateTimeOffset GetDate(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Text(e, name), out var date) ? date : DateTimeOffset.UtcNow;

    private static AlertSeverity SeverityFromRisk(string? value) => value?.ToLowerInvariant() switch
    {
        "high" => AlertSeverity.High,
        "medium" => AlertSeverity.Medium,
        "low" => AlertSeverity.Low,
        _ => AlertSeverity.Informational
    };

    private static AlertSeverity SeverityFromString(string? value) => value?.ToLowerInvariant() switch
    {
        "critical" => AlertSeverity.Critical,
        "high" => AlertSeverity.High,
        "medium" => AlertSeverity.Medium,
        "low" => AlertSeverity.Low,
        "informational" => AlertSeverity.Informational,
        _ => AlertSeverity.Medium
    };

    private static AlertSeverity SeverityFromClassification(string? value) => value?.ToLowerInvariant() switch
    {
        "incident" => AlertSeverity.High,
        "advisory" => AlertSeverity.Medium,
        _ => AlertSeverity.Low
    };

    private static bool IsClosed(string? value) =>
        value?.Equals("resolved", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Equals("closed", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Identity Protection riskState values that mean the risk is over.</summary>
    private static bool IsRiskClosed(string? riskState) => riskState?.ToLowerInvariant() is
        "none" or "remediated" or "dismissed" or "confirmedsafe";


    private async Task CaptureTrendSnapshotAsync(CancellationToken ct)
    {
        var open = db.SecurityAlerts.Where(a => !a.IsResolved);
        var riskyUsersCount = await open.CountAsync(a => a.AlertType == "RiskyUser", ct);
        var mfaMissingCount = await open.CountAsync(a => a.AlertType == "MfaStatus", ct);
        var nonCompliantCount = await open.CountAsync(a => a.AlertType == "NonCompliantDevice", ct);
        
        // Track open Critical & High alerts across all services (matching AlertEvaluator and Overview KPIs)
        var criticalAlertsCount = await open.CountAsync(a => a.Severity == AlertSeverity.Critical, ct);
        var highAlertsCount = await open.CountAsync(a => a.Severity == AlertSeverity.High, ct);
        
        // Microsoft Purview best practice: Track compliance operations findings (Quarantine, Mail flow, DLP)
        var complianceIssuesCount = await open.CountAsync(a => a.Service == M365ServiceArea.ExchangeOnline, ct);

        // Calculate Secure Score
        double secureScorePct = 0;
        var graphConfigured = await credentials.IsConfiguredAsync(ct);
        if (graphConfigured)
        {
            try
            {
                // Single page only — GetCollectionAsync would follow @odata.nextLink
                // through the ENTIRE score history one item at a time.
                var items = await graph.GetSinglePageAsync("/v1.0/security/secureScores?$top=1", ct);
                if (items.Count > 0)
                {
                    var latest = items[0];
                    var cs = latest.TryGetProperty("currentScore", out var cVal) && cVal.ValueKind == JsonValueKind.Number ? cVal.GetDouble() : 0;
                    var ms = latest.TryGetProperty("maxScore", out var mVal) && mVal.ValueKind == JsonValueKind.Number ? mVal.GetDouble() : 100;
                    if (ms == 0) ms = 100;
                    secureScorePct = Math.Round(cs / ms * 100, 1);
                }
            }
            catch { /* ignore */ }
        }

        // Calculate MFA Coverage Pct
        double mfaCoveragePct = 0;
        if (graphConfigured)
        {
            try
            {
                var reg = await graph.GetCollectionAsync("/v1.0/reports/authenticationMethods/userRegistrationDetails", ct);
                var mfaTotal = reg.Count;
                if (mfaTotal > 0)
                {
                    var mfaRegistered = reg.Count(r => r.TryGetProperty("isMfaRegistered", out var p) && p.ValueKind == JsonValueKind.True);
                    mfaCoveragePct = Math.Round((double)mfaRegistered / mfaTotal * 100, 1);
                }
            }
            catch { /* ignore */ }
        }

        var snapshot = new TrendSnapshot
        {
            Id = Guid.NewGuid(),
            CapturedAt = DateTimeOffset.UtcNow,
            RiskyUsersCount = riskyUsersCount,
            MfaCoveragePct = mfaCoveragePct,
            NonCompliantDevicesCount = nonCompliantCount,
            CriticalAlertsCount = criticalAlertsCount,
            HighAlertsCount = highAlertsCount,
            SecureScorePct = secureScorePct,
            ComplianceIssuesCount = complianceIssuesCount
        };

        db.TrendSnapshots.Add(snapshot);
    }

    /// <param name="Service">With <paramref name="AlertType"/>, the alerts this source owns (what its Map produces).</param>
    private sealed record GraphSource(string Name, string Path, M365ServiceArea Service, string AlertType, Func<JsonElement, SecurityAlert> Map);
}
