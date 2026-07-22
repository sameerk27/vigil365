namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Turns a raw Graph failure into something an administrator can act on.
///
/// The default rendering of a denied call is a wall of JSON —
/// <c>403 Forbidden: {"error":{"code":"accessDenied","message":"Caller does not
/// have required permissions for this API",...}}</c> — which tells the reader
/// neither which permission is missing nor where to grant it. Nearly every
/// collector failure in practice is one un-consented application permission.
/// </summary>
public static class GraphErrorHint
{
    /// <summary>
    /// Application permission required by each collector source / Graph path
    /// fragment. Keyed by the source name used in <c>GraphCollector.BuildSources</c>
    /// so the hint survives Graph changing its error text.
    /// </summary>
    private static readonly Dictionary<string, string> PermissionBySource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Risky users"]                 = "IdentityRiskyUser.Read.All",
        ["Risky sign-ins"]              = "AuditLog.Read.All",
        ["Failed sign-ins"]             = "AuditLog.Read.All",
        ["MFA registration"]            = "AuditLog.Read.All and Reports.Read.All",
        ["Non-compliant devices"]       = "DeviceManagementManagedDevices.Read.All",
        ["Devices not checked in"]      = "DeviceManagementManagedDevices.Read.All",
        ["Defender incidents"]          = "SecurityIncident.Read.All",
        ["Defender alerts"]             = "SecurityAlert.Read.All",
        ["Malware detections"]          = "SecurityAlert.Read.All",
        ["Quarantined messages"]        = "SecurityEvents.Read.All",
        ["Mail flow issues"]            = "ServiceHealth.Read.All",
        ["Service health issues"]       = "ServiceHealth.Read.All",
        ["SharePoint sharing posture"]  = "SharePointTenantSettings.Read.All",
        ["Tenant audit events"]         = "AuditLog.Read.All",
    };

    /// <summary>Required permission for a named collector source, if known.</summary>
    public static string? PermissionFor(string? sourceName)
        => sourceName is not null && PermissionBySource.TryGetValue(sourceName, out var p) ? p : null;

    /// <summary>
    /// Rewrites an exception message into an actionable sentence. Falls back to
    /// the trimmed original when the failure is not one we recognise — never
    /// hide detail we cannot improve on.
    /// </summary>
    public static string Describe(string? rawMessage, string? sourceName = null, int maxLength = 300)
    {
        var raw = rawMessage ?? "";

        if (IsPermissionDenied(raw))
        {
            var perm = PermissionFor(sourceName);
            return perm is null
                ? "Permission denied by Microsoft Graph. The app registration is missing an application permission for this data; check the required permissions and grant admin consent."
                : $"Permission denied: grant the {perm} application permission to the Vigil365 app registration and click 'Grant admin consent' in Entra.";
        }

        if (raw.Contains("429") || raw.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Graph throttled this request (429). Vigil365 backs off and retries automatically; no action needed unless it persists.";

        if (raw.Contains("401") || raw.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Graph rejected the credentials (401). The client secret or certificate is expired or incorrect — re-check the Graph configuration in Setup.";

        if (raw.Contains("404") || raw.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Graph returned 404 for this data. The feature may not be licensed or enabled in this tenant.";

        return Trim(raw, maxLength);
    }

    /// <summary>
    /// Endpoint-facing variant: returns a hint only for failures we recognise,
    /// otherwise null so the caller keeps its own generic message. Never echoes
    /// the raw exception, which could carry internal detail into an HTTP response.
    /// </summary>
    public static string? DescribeOrNull(string? rawMessage, string? requiredPermission = null)
    {
        var raw = rawMessage ?? "";

        if (IsPermissionDenied(raw))
            return requiredPermission is null
                ? "Permission denied by Microsoft Graph. The Vigil365 app registration is missing an application permission for this data — check the required permissions and grant admin consent in Entra."
                : $"Permission denied: grant the {requiredPermission} application permission to the Vigil365 app registration and click 'Grant admin consent' in Entra.";

        if (raw.Contains("429") || raw.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Graph is throttling requests (429). This usually clears on its own — try again shortly.";

        if (raw.Contains("401") || raw.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Graph rejected the credentials (401). The client secret or certificate may be expired — re-check the Graph configuration in Setup.";

        return null;
    }

    private static bool IsPermissionDenied(string raw)
        => raw.Contains("403")
        || raw.Contains("accessDenied", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
