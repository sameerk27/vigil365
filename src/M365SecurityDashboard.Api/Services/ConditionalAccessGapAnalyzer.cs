using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Analyzes Conditional Access policies for common coverage gaps: no tenant-wide
/// MFA, legacy authentication left unblocked, unenforced (report-only/disabled)
/// policies, and MFA exemptions. Pure functions over a parsed policy view so the
/// findings logic is unit-testable without Graph.
/// </summary>
public static class ConditionalAccessGapAnalyzer
{
    /// <summary>Flattened view of the CA policy fields the analysis needs.</summary>
    public sealed record CaPolicyView(
        string Name, string State, bool RequiresMfa, bool Blocks,
        bool IncludesAllUsers, bool IncludesAllApps,
        int ExcludedUsers, int ExcludedGroups, IReadOnlyList<string> ClientAppTypes);

    public sealed record Finding(string Severity, string Title, string Detail, string Recommendation);

    private static readonly string[] LegacyClientTypes = ["exchangeActiveSync", "other"];

    private static bool IsEnabled(CaPolicyView p) => string.Equals(p.State, "enabled", StringComparison.OrdinalIgnoreCase);
    private static bool IsReportOnly(CaPolicyView p) => string.Equals(p.State, "enabledForReportingButNotEnforced", StringComparison.OrdinalIgnoreCase);
    private static bool IsDisabled(CaPolicyView p) => string.Equals(p.State, "disabled", StringComparison.OrdinalIgnoreCase);

    private static bool TargetsLegacyAuth(CaPolicyView p) =>
        p.ClientAppTypes.Any(c => LegacyClientTypes.Contains(c, StringComparer.OrdinalIgnoreCase));

    /// <summary>Produces the gap findings, most severe first.</summary>
    public static List<Finding> Analyze(IReadOnlyList<CaPolicyView> policies)
    {
        var findings = new List<Finding>();

        if (policies.Count == 0)
        {
            findings.Add(new Finding("critical", "No Conditional Access policies configured",
                "The tenant has no Conditional Access policies at all.",
                "Create a baseline that requires MFA for all users and blocks legacy authentication."));
            return findings;
        }

        var enabled = policies.Where(IsEnabled).ToList();

        // 1. Tenant-wide MFA baseline.
        var hasBaselineMfa = enabled.Any(p => p.RequiresMfa && p.IncludesAllUsers && p.IncludesAllApps);
        if (!hasBaselineMfa)
        {
            var partial = enabled.Any(p => p.RequiresMfa);
            findings.Add(new Finding("critical", "No tenant-wide MFA policy",
                partial
                    ? "MFA is required by some enabled policies, but none covers all users and all apps."
                    : "No enabled policy requires multi-factor authentication.",
                "Add an enabled policy requiring MFA for All users and All cloud apps (exclude only break-glass accounts)."));
        }

        // 2. Legacy authentication.
        var blocksLegacy = enabled.Any(p => p.Blocks && TargetsLegacyAuth(p));
        if (!blocksLegacy)
        {
            findings.Add(new Finding("high", "Legacy authentication is not blocked",
                "No enabled policy blocks legacy authentication clients (which cannot enforce MFA).",
                "Add an enabled policy that blocks the 'Exchange ActiveSync' and 'Other clients' legacy client app types."));
        }

        // 3. MFA exemptions on the enforced MFA policies.
        foreach (var p in enabled.Where(p => p.RequiresMfa && (p.ExcludedUsers > 0 || p.ExcludedGroups > 0)))
        {
            var bits = new List<string>();
            if (p.ExcludedUsers > 0) bits.Add($"{p.ExcludedUsers} user{(p.ExcludedUsers == 1 ? "" : "s")}");
            if (p.ExcludedGroups > 0) bits.Add($"{p.ExcludedGroups} group{(p.ExcludedGroups == 1 ? "" : "s")}");
            findings.Add(new Finding("medium", $"MFA exemptions on \"{p.Name}\"",
                $"This MFA policy exempts {string.Join(" and ", bits)} — those identities can sign in without MFA.",
                "Confirm every exclusion is a documented break-glass account; remove the rest."));
        }

        // 4. Report-only policies (configured but not enforcing).
        var reportOnly = policies.Where(IsReportOnly).ToList();
        if (reportOnly.Count > 0)
        {
            findings.Add(new Finding("medium", $"{reportOnly.Count} policy(ies) are report-only",
                $"Report-only policies do not enforce controls: {string.Join(", ", reportOnly.Take(5).Select(p => $"\"{p.Name}\""))}{(reportOnly.Count > 5 ? "…" : "")}.",
                "Review report-only impact, then switch to On to start enforcing."));
        }

        // 5. Disabled policies.
        var disabled = policies.Where(IsDisabled).ToList();
        if (disabled.Count > 0)
        {
            findings.Add(new Finding("low", $"{disabled.Count} policy(ies) are disabled",
                $"Disabled policies provide no protection: {string.Join(", ", disabled.Take(5).Select(p => $"\"{p.Name}\""))}{(disabled.Count > 5 ? "…" : "")}.",
                "Enable them if intended, or delete stale policies to reduce confusion."));
        }

        var order = new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3 };
        return findings.OrderBy(f => order.GetValueOrDefault(f.Severity, 4)).ToList();
    }

    /// <summary>Parses a raw Graph conditionalAccess policy element into the analysis view.</summary>
    public static CaPolicyView Parse(JsonElement p)
    {
        var name = p.TryGetProperty("displayName", out var n) ? n.GetString() ?? "Unnamed" : "Unnamed";
        var state = p.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";

        bool includesAllUsers = false, includesAllApps = false;
        int exclUsers = 0, exclGroups = 0;
        var clientAppTypes = new List<string>();

        if (p.TryGetProperty("conditions", out var cond) && cond.ValueKind == JsonValueKind.Object)
        {
            if (cond.TryGetProperty("users", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("includeUsers", out var inc) && inc.ValueKind == JsonValueKind.Array)
                    includesAllUsers = inc.EnumerateArray().Any(x => string.Equals(x.GetString(), "All", StringComparison.OrdinalIgnoreCase));
                if (u.TryGetProperty("excludeUsers", out var exU) && exU.ValueKind == JsonValueKind.Array)
                    exclUsers = exU.GetArrayLength();
                if (u.TryGetProperty("excludeGroups", out var exG) && exG.ValueKind == JsonValueKind.Array)
                    exclGroups = exG.GetArrayLength();
            }
            if (cond.TryGetProperty("applications", out var ap) && ap.ValueKind == JsonValueKind.Object &&
                ap.TryGetProperty("includeApplications", out var incA) && incA.ValueKind == JsonValueKind.Array)
                includesAllApps = incA.EnumerateArray().Any(x => string.Equals(x.GetString(), "All", StringComparison.OrdinalIgnoreCase));
            if (cond.TryGetProperty("clientAppTypes", out var cat) && cat.ValueKind == JsonValueKind.Array)
                clientAppTypes.AddRange(cat.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
        }

        bool requiresMfa = false, blocks = false;
        if (p.TryGetProperty("grantControls", out var gc) && gc.ValueKind == JsonValueKind.Object &&
            gc.TryGetProperty("builtInControls", out var bic) && bic.ValueKind == JsonValueKind.Array)
        {
            var controls = bic.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            requiresMfa = controls.Contains("mfa", StringComparer.OrdinalIgnoreCase);
            blocks = controls.Contains("block", StringComparer.OrdinalIgnoreCase);
        }

        return new CaPolicyView(name, state, requiresMfa, blocks, includesAllUsers, includesAllApps, exclUsers, exclGroups, clientAppTypes);
    }
}
