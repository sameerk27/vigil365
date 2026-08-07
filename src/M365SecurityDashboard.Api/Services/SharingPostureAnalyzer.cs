using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Analyzes tenant SharePoint/OneDrive sharing settings (Graph
/// /v1.0/admin/sharepoint/settings) for risky external-sharing posture.
/// Pure functions over a flattened view so the findings logic is unit-testable
/// without Graph. Requires SharePointTenantSettings.Read.All.
/// </summary>
public static class SharingPostureAnalyzer
{
    /// <summary>Flattened view of the sharing-relevant tenant settings.</summary>
    public sealed record SharingView(
        string? SharingCapability,            // disabled | externalUserSharingOnly | externalUserAndGuestSharing | existingExternalUserSharingOnly
        string? OneDriveSharingCapability,
        string? DefaultSharingLinkType,        // none | direct | internal | anonymousAccess
        int? AnonymousLinkExpirationDays,      // null/0 = links never expire
        bool ResharingByExternalUsersEnabled,
        IReadOnlyList<string> AllowedDomains,  // sharing allow-list (empty = unrestricted)
        IReadOnlyList<string> BlockedDomains);

    public sealed record Finding(string Severity, string Title, string Detail, string Recommendation);

    private static bool IsAnyoneSharing(string? cap) =>
        string.Equals(cap, "externalUserAndGuestSharing", StringComparison.OrdinalIgnoreCase);

    private static bool IsExternalSharing(string? cap) =>
        IsAnyoneSharing(cap) || string.Equals(cap, "externalUserSharingOnly", StringComparison.OrdinalIgnoreCase)
        || string.Equals(cap, "existingExternalUserSharingOnly", StringComparison.OrdinalIgnoreCase);

    /// <summary>Produces the posture findings, most severe first.</summary>
    public static List<Finding> Analyze(SharingView v)
    {
        var findings = new List<Finding>();

        // 1. "Anyone" links tenant-wide (anonymous, unauthenticated access).
        if (IsAnyoneSharing(v.SharingCapability))
        {
            findings.Add(new Finding("high", "\"Anyone\" links are enabled tenant-wide",
                "SharePoint allows anonymous sharing links — files can be opened by anyone with the URL, no sign-in, no audit trail of who accessed them.",
                "Restrict sharing to 'New and existing guests' unless anonymous links are a documented business need."));

            // 1b. Anonymous links that never expire — only meaningful when Anyone links exist.
            if (v.AnonymousLinkExpirationDays is null or <= 0)
            {
                findings.Add(new Finding("medium", "Anonymous links never expire",
                    "No expiration is enforced on 'Anyone' links — a link shared once remains valid forever.",
                    "Set an anonymous-link expiration (30 days or less is typical)."));
            }
        }

        // 2. Default link type is anonymous — every casual share becomes an Anyone link.
        if (string.Equals(v.DefaultSharingLinkType, "anonymousAccess", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding("high", "Default sharing link is \"Anyone\"",
                "The default link type users get when sharing is an anonymous link, so the riskiest option is also the path of least resistance.",
                "Set the default sharing link to 'Only people in your organization' or 'Specific people'."));
        }

        // 3. External resharing.
        if (v.ResharingByExternalUsersEnabled && IsExternalSharing(v.SharingCapability))
        {
            findings.Add(new Finding("medium", "External users can re-share content",
                "Guests can share items onward to other external users, extending access beyond the original recipient.",
                "Disable resharing by external users so shares stay limited to who your users chose."));
        }

        // 4. No domain restrictions while external sharing is on.
        if (IsExternalSharing(v.SharingCapability) && v.AllowedDomains.Count == 0 && v.BlockedDomains.Count == 0)
        {
            findings.Add(new Finding("low", "External sharing has no domain restrictions",
                "Sharing is open to any external domain — including consumer and competitor domains.",
                "Consider an allow-list of partner domains (or a block-list) to bound external collaboration."));
        }

        // 5. OneDrive looser than SharePoint (per-service drift).
        if (IsAnyoneSharing(v.OneDriveSharingCapability) && !IsAnyoneSharing(v.SharingCapability))
        {
            findings.Add(new Finding("medium", "OneDrive sharing is looser than SharePoint",
                "OneDrive allows 'Anyone' links while SharePoint does not — personal storage is the least-governed surface.",
                "Align OneDrive's sharing capability with (or make it stricter than) SharePoint's."));
        }

        var order = new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3 };
        return findings.OrderBy(f => order.GetValueOrDefault(f.Severity, 4)).ToList();
    }

    /// <summary>Parses the raw Graph sharepoint settings object into the analysis view.</summary>
    public static SharingView Parse(JsonElement e)
    {
        static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static List<string> Arr(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [];

        int? expiry = null;
        if (e.TryGetProperty("sharingLinkExpirationInDays", out var exp) && exp.ValueKind == JsonValueKind.Number)
            expiry = exp.GetInt32();

        var reshare = e.TryGetProperty("isResharingByExternalUsersEnabled", out var rs) && rs.ValueKind == JsonValueKind.True;

        return new SharingView(
            SharingCapability: Str(e, "sharingCapability"),
            OneDriveSharingCapability: Str(e, "oneDriveSharingCapability") ?? Str(e, "sharingCapability"),
            DefaultSharingLinkType: Str(e, "sharingDefaultLinkType") ?? Str(e, "defaultSharingLinkType"),
            AnonymousLinkExpirationDays: expiry,
            ResharingByExternalUsersEnabled: reshare,
            AllowedDomains: Arr(e, "sharingAllowedDomainList"),
            BlockedDomains: Arr(e, "sharingBlockedDomainList"));
    }
}
