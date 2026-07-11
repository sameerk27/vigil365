using System.Text.Json;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

public sealed class GraphCollector(
    AppDbContext db,
    GraphApiClient graph,
    IOptions<GraphOptions> options,
    ILogger<GraphCollector> logger)
{
    private readonly GraphOptions _options = options.Value;

    // One collection at a time per process: the manual endpoint and the background
    // worker would otherwise race the (Service, AlertType, ExternalId) unique index.
    private static readonly SemaphoreSlim CollectionGate = new(1, 1);

    public async Task<CollectionRun> CollectAsync(CancellationToken ct)
    {
        if (!await CollectionGate.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("A collection run is already in progress.");
        try
        {
            return await CollectCoreAsync(ct);
        }
        finally
        {
            CollectionGate.Release();
        }
    }

    private async Task<CollectionRun> CollectCoreAsync(CancellationToken ct)
    {
        var run = new CollectionRun { StartedAt = DateTimeOffset.UtcNow, Status = CollectionStatus.Started };
        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var sources = BuildSources();
            var failures = new List<object>();
            foreach (var source in sources)
            {
                try
                {
                    var rows = await graph.GetCollectionAsync(source.Path, ct);
                    foreach (var row in rows)
                    {
                        await UpsertAlertAsync(source.Map(row), ct);
                        run.AlertsUpserted++;
                    }
                }
                catch (Exception ex)
                {
                    run.SourceFailures++;
                    failures.Add(new { source = source.Name, error = Trim(ex.Message, 300) });
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
                failures.Add(new { source = "Tenant audit events", error = Trim(ex.Message, 300) });
                logger.LogWarning(ex, "Tenant audit event collection failed");
            }

            run.SourceFailureDetails = failures.Count > 0 ? JsonSerializer.Serialize(failures) : null;
            run.Status = run.SourceFailures == sources.Count ? CollectionStatus.Failed : CollectionStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;

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
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

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

        var incomingIds = rows.Select(r => Get(r, "id")).Where(id => id != null).Cast<string>().ToList();
        var known = (await db.AuditEvents
            .Where(e => e.Source == "directoryAudit" && incomingIds.Contains(e.ExternalId))
            .Select(e => e.ExternalId)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var r in rows)
        {
            var id = Get(r, "id");
            if (id is null || !known.Add(id)) continue; // dedupe within batch + store

            string? targetName = null;
            if (r.TryGetProperty("targetResources", out var targets) && targets.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in targets.EnumerateArray())
                {
                    targetName = Get(t, "displayName") ?? Get(t, "userPrincipalName");
                    if (targetName != null) break;
                }
            }

            db.AuditEvents.Add(new AuditEvent
            {
                ExternalId = id,
                Source = "directoryAudit",
                Activity = Trim(Get(r, "activityDisplayName") ?? "Unknown activity", 200),
                Category = TrimOrNull(Get(r, "category"), 80),
                ActorUpn = TrimOrNull(Get(r, "initiatedBy", "user", "userPrincipalName"), 320),
                ActorApp = TrimOrNull(Get(r, "initiatedBy", "app", "displayName"), 200),
                TargetName = TrimOrNull(targetName, 320),
                Result = TrimOrNull(Get(r, "result"), 20),
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
            new("Risky users", "/v1.0/identityProtection/riskyUsers?$top=50", MapRiskyUser),
            new("Risky sign-ins", WithFilter("/v1.0/auditLogs/signIns?$top=50", $"riskState ne 'none' and createdDateTime ge {signInCutoff}"), MapRiskySignIn),
            new("Failed sign-ins", WithFilter("/v1.0/auditLogs/signIns?$top=50", $"status/errorCode ne 0 and createdDateTime ge {signInCutoff}"), MapFailedSignIn),
            new("MFA registration", "/v1.0/reports/authenticationMethods/userRegistrationDetails?$top=100", MapMfaStatus),
            new("Non-compliant devices", WithFilter("/v1.0/deviceManagement/managedDevices?$top=50", "complianceState ne 'compliant'"), MapNonCompliantDevice),
            new("Devices not checked in", WithFilter("/v1.0/deviceManagement/managedDevices?$top=50", $"lastSyncDateTime lt {deviceCutoff}"), MapDeviceNotCheckedIn),
            new("Defender incidents", "/v1.0/security/incidents?$top=50", MapDefenderIncident),
            new("Defender alerts", "/v1.0/security/alerts_v2?$top=50", MapDefenderAlert),
            new("Malware detections", WithFilter("/v1.0/security/alerts_v2?$top=50", "category eq 'Malware'"), MapMalwareDetection),
            new("Quarantined messages", _options.ExchangeQuarantinePath, MapQuarantinedMessage),
            new("Mail flow issues", _options.MailFlowIssuesPath, MapMailFlowIssue),
            new("Service health issues", WithFilter("/v1.0/admin/serviceAnnouncement/issues?$top=50", "isResolved eq false"), MapServiceHealth)
        ];
    }

    private static string WithFilter(string path, string filter) => $"{path}&$filter={Uri.EscapeDataString(filter)}";

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

    private static SecurityAlert MapRiskyUser(JsonElement e) => Alert(e, M365ServiceArea.EntraId, "RiskyUser",
        Get(e, "id"), SeverityFromRisk(Get(e, "riskLevel")), $"Risky user: {Get(e, "userPrincipalName") ?? Get(e, "id")}",
        Get(e, "riskState"), Get(e, "userPrincipalName"), null, GetDate(e, "riskLastUpdatedDateTime"));

    private static SecurityAlert MapRiskySignIn(JsonElement e) => Alert(e, M365ServiceArea.EntraId, "RiskySignIn",
        Get(e, "id"), SeverityFromRisk(Get(e, "riskLevelAggregated")), $"Risky sign-in: {Get(e, "userPrincipalName")}",
        Get(e, "riskState"), Get(e, "userPrincipalName"), Get(e, "deviceDetail", "displayName"), GetDate(e, "createdDateTime"));

    private static SecurityAlert MapFailedSignIn(JsonElement e) => Alert(e, M365ServiceArea.EntraId, "FailedSignIn",
        Get(e, "id"), AlertSeverity.Medium, $"Failed sign-in: {Get(e, "userPrincipalName")}",
        Get(e, "status", "failureReason"), Get(e, "userPrincipalName"), Get(e, "deviceDetail", "displayName"), GetDate(e, "createdDateTime"));

    private static SecurityAlert MapMfaStatus(JsonElement e)
    {
        var registered = GetBool(e, "isMfaRegistered");
        return Alert(e, M365ServiceArea.EntraId, "MfaStatus", Get(e, "id") ?? Get(e, "userPrincipalName"),
            registered ? AlertSeverity.Informational : AlertSeverity.High,
            registered ? $"MFA registered: {Get(e, "userPrincipalName")}" : $"MFA missing: {Get(e, "userPrincipalName")}",
            Get(e, "defaultMfaMethod"), Get(e, "userPrincipalName"), null, DateTimeOffset.UtcNow,
            isResolved: registered);
    }

    private static SecurityAlert MapNonCompliantDevice(JsonElement e) => Alert(e, M365ServiceArea.Intune, "NonCompliantDevice",
        Get(e, "id"), AlertSeverity.High, $"Non-compliant device: {Get(e, "deviceName")}",
        Get(e, "complianceState"), Get(e, "userPrincipalName"), Get(e, "deviceName"), GetDate(e, "lastSyncDateTime"));

    private static SecurityAlert MapDeviceNotCheckedIn(JsonElement e) => Alert(e, M365ServiceArea.Intune, "DeviceNotCheckedIn",
        Get(e, "id"), AlertSeverity.Medium, $"Device not checked in: {Get(e, "deviceName")}",
        $"Last sync: {Get(e, "lastSyncDateTime")}", Get(e, "userPrincipalName"), Get(e, "deviceName"), GetDate(e, "lastSyncDateTime"));

    private static SecurityAlert MapDefenderIncident(JsonElement e) => Alert(e, M365ServiceArea.DefenderXdr, "Incident",
        Get(e, "id"), SeverityFromString(Get(e, "severity")), $"Defender incident: {Get(e, "displayName") ?? Get(e, "id")}",
        Get(e, "status"), null, null, GetDate(e, "createdDateTime"), Get(e, "incidentWebUrl"), IsClosed(Get(e, "status")));

    private static SecurityAlert MapDefenderAlert(JsonElement e) => Alert(e, M365ServiceArea.DefenderXdr, "Alert",
        Get(e, "id"), SeverityFromString(Get(e, "severity")), $"Defender alert: {Get(e, "title") ?? Get(e, "id")}",
        Get(e, "description"), null, null, GetDate(e, "createdDateTime"), Get(e, "alertWebUrl"), IsClosed(Get(e, "status")));

    private static SecurityAlert MapMalwareDetection(JsonElement e) => Alert(e, M365ServiceArea.DefenderXdr, "MalwareDetection",
        Get(e, "id"), SeverityFromString(Get(e, "severity")), $"Malware detection: {Get(e, "title") ?? Get(e, "id")}",
        Get(e, "description"), null, null, GetDate(e, "createdDateTime"), Get(e, "alertWebUrl"), IsClosed(Get(e, "status")));

    private static SecurityAlert MapQuarantinedMessage(JsonElement e) => Alert(e, M365ServiceArea.ExchangeOnline, "QuarantinedMessage",
        Get(e, "id"), SeverityFromString(Get(e, "severity")), $"Quarantined message: {Get(e, "title") ?? Get(e, "subject") ?? Get(e, "id")}",
        Get(e, "description"), Get(e, "recipientEmailAddress"), null, GetDate(e, "createdDateTime"), Get(e, "alertWebUrl"), IsClosed(Get(e, "status")));

    private static SecurityAlert MapMailFlowIssue(JsonElement e) => Alert(e, M365ServiceArea.ExchangeOnline, "MailFlowIssue",
        Get(e, "id"), AlertSeverity.High, $"Exchange issue: {Get(e, "title") ?? Get(e, "id")}",
        Get(e, "impactDescription"), null, null, GetDate(e, "startDateTime"), Get(e, "details", "url"), GetBool(e, "isResolved"));

    private static SecurityAlert MapServiceHealth(JsonElement e) => Alert(e, M365ServiceArea.ServiceHealth, "ServiceHealthIssue",
        Get(e, "id"), SeverityFromClassification(Get(e, "classification")), $"{Get(e, "service")}: {Get(e, "title") ?? Get(e, "id")}",
        Get(e, "impactDescription"), null, null, GetDate(e, "startDateTime"), null, GetBool(e, "isResolved"));

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

    private static string? Get(JsonElement e, params string[] path)
    {
        var current = e;
        foreach (var part in path)
        {
            // TryGetProperty throws on non-object elements (e.g. an array),
            // so guard the kind before traversing into nested paths.
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(part, out current)) return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset GetDate(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Get(e, name), out var date) ? date : DateTimeOffset.UtcNow;

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

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? TrimOrNull(string? s, int max) => s is null ? null : Trim(s, max);

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
        if (_options.IsConfigured())
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
        if (_options.IsConfigured())
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

    private sealed record GraphSource(string Name, string Path, Func<JsonElement, SecurityAlert> Map);
}
