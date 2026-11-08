using M365SecurityDashboard.Api.Services.Common;

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

    /// <summary>
    /// Right after a client's admin grants consent, Graph answers 401
    /// <c>Authorization_IdentityNotFound</c> ("the identity of the calling
    /// application could not be established") until the app's new service principal
    /// has replicated — seconds to a few minutes. It is not an expired secret, and it
    /// clears by itself, so callers wait and retry rather than report a failure.
    /// </summary>
    public static bool IsNotYetProvisioned(string? rawMessage)
        => (rawMessage ?? "").Contains("Authorization_IdentityNotFound", StringComparison.OrdinalIgnoreCase);

    public const string NotYetProvisionedMessage =
        "Microsoft has not finished setting up the Vigil365 app in this tenant yet. This normally clears within a few " +
        "minutes of the admin consent, and Vigil365 keeps trying. If it is still failing after about 10 minutes, the app " +
        "was not consented in this tenant (or its enterprise application was deleted) — run the consent again.";

    /// <summary>Required permission for a named collector source, if known.</summary>
    public static string? PermissionFor(string? sourceName)
        => sourceName is not null && PermissionBySource.TryGetValue(sourceName, out var p) ? p : null;

    /// <summary>Every (source, required permission) pair — the authoritative list
    /// behind the in-app permissions reference.</summary>
    public static IReadOnlyList<(string Source, string Permission)> AllRequirements()
        => PermissionBySource.Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Rewrites an exception message into an actionable sentence. Falls back to
    /// the trimmed original when the failure is not one we recognise — never
    /// hide detail we cannot improve on.
    /// </summary>
    public static string Describe(string? rawMessage, string? sourceName = null, int maxLength = 300)
    {
        var raw = rawMessage ?? "";
        return Classify(raw) switch
        {
            Failure.NotProvisioned => NotYetProvisionedMessage,
            Failure.PermissionDenied => PermissionFor(sourceName) is { } perm
                ? $"Permission denied: grant the {perm} application permission to the Vigil365 app registration and click 'Grant admin consent' in Entra."
                : "Permission denied by Microsoft Graph. The app registration is missing an application permission for this data; check the required permissions and grant admin consent.",
            Failure.Throttled => "Microsoft Graph throttled this request (429). Vigil365 backs off and retries automatically; no action needed unless it persists.",
            Failure.BadCredentials => "Microsoft Graph rejected the credentials (401). The client secret or certificate is expired or incorrect — re-check the Graph configuration in Setup.",
            Failure.NotFound => "Microsoft Graph returned 404 for this data. The feature may not be licensed or enabled in this tenant.",
            _ => TextLimits.Truncate(raw, maxLength),
        };
    }

    /// <summary>
    /// Endpoint-facing variant: returns a hint only for failures we recognise,
    /// otherwise null so the caller keeps its own generic message. Never echoes
    /// the raw exception, which could carry internal detail into an HTTP response.
    /// </summary>
    public static string? DescribeOrNull(string? rawMessage, string? requiredPermission = null) => Classify(rawMessage ?? "") switch
    {
        Failure.NotProvisioned => NotYetProvisionedMessage,
        Failure.PermissionDenied => requiredPermission is null
            ? "Permission denied by Microsoft Graph. The Vigil365 app registration is missing an application permission for this data — check the required permissions and grant admin consent in Entra."
            : $"Permission denied: grant the {requiredPermission} application permission to the Vigil365 app registration and click 'Grant admin consent' in Entra.",
        Failure.Throttled => "Microsoft Graph is throttling requests (429). This usually clears on its own — try again shortly.",
        Failure.BadCredentials => "Microsoft Graph rejected the credentials (401). The client secret or certificate may be expired — re-check the Graph configuration in Setup.",
        _ => null,
    };

    /// <summary>
    /// Microsoft's own error sentence from a Graph failure ("The period value 'RL30' is not
    /// supported…"), for endpoints that would otherwise only say "an error occurred". Only
    /// the error's message field, never the raw response, request ids or our own exception
    /// text. Reports wrap a second error document inside the message; the innermost wins.
    /// </summary>
    public static string? GraphMessageOrNull(string? rawMessage, int maxLength = 300)
    {
        var raw = rawMessage ?? "";
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        string? message = null;
        var json = raw[start..];
        for (var depth = 0; depth < 3 && json is not null; depth++)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("error", out var error)
                    || !error.TryGetProperty("message", out var m)
                    || m.ValueKind != System.Text.Json.JsonValueKind.String) break;
                message = m.GetString();
                json = message is not null && message.TrimStart().StartsWith('{') ? message : null;
            }
            catch (System.Text.Json.JsonException) { break; }
        }
        return string.IsNullOrWhiteSpace(message) || message.TrimStart().StartsWith('{') ? null : TextLimits.Truncate(message.Trim(), maxLength);
    }

    private enum Failure { NotProvisioned, PermissionDenied, Throttled, BadCredentials, NotFound, Unknown }

    // Both describers use this one ladder. Not-yet-provisioned comes first: its raw text carries
    // request-id GUIDs that can contain "403" or "429".
    private static Failure Classify(string raw) =>
        IsNotYetProvisioned(raw) ? Failure.NotProvisioned
        : IsPermissionDenied(raw) ? Failure.PermissionDenied
        : raw.Contains("429") || raw.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ? Failure.Throttled
        : raw.Contains("401") || raw.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("invalid_client", StringComparison.OrdinalIgnoreCase) ? Failure.BadCredentials
        : raw.Contains("404") || raw.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ? Failure.NotFound
        : Failure.Unknown;

    private static bool IsPermissionDenied(string raw)
        => raw.Contains("403")
        || raw.Contains("accessDenied", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase);

}
