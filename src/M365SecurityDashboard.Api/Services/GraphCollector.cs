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

    public async Task<CollectionRun> CollectAsync(CancellationToken ct)
    {
        var run = new CollectionRun { StartedAt = DateTimeOffset.UtcNow, Status = CollectionStatus.Started };
        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var sources = BuildSources();
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
                    logger.LogWarning(ex, "Graph source {SourceName} failed", source.Name);
                }
            }

            run.Status = run.SourceFailures == sources.Count ? CollectionStatus.Failed : CollectionStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
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

    private sealed record GraphSource(string Name, string Path, Func<JsonElement, SecurityAlert> Map);
}
