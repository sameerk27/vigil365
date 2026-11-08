using System.Text.Json;
using static M365SecurityDashboard.Api.Services.Common.JsonRead;

namespace M365SecurityDashboard.Api.Services.Posture;

/// <summary>A read from Graph: the value, or why it couldn't be read.</summary>
public sealed record Reading<T>(T? Value, string? Error)
{
    public static Reading<T> Ok(T value) => new(value, null);
    public static Reading<T> Failed(string error) => new(default, error);
    public bool Read => Error is null;
}

/// <summary>A licence the tenant owns and the service plans it provisions.</summary>
public sealed record OwnedSku(string PartNumber, string Name, int Enabled, int Consumed, IReadOnlyList<string> ServicePlans);

/// <summary>Everything posture reads from one tenant, in one pass. Built by <see cref="PostureCollector"/>.</summary>
public sealed record PostureSnapshot
{
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.UtcNow;
    public string? OrganizationName { get; init; }
    public required Reading<IReadOnlyList<OwnedSku>> Skus { get; init; }
    public required Reading<IReadOnlyList<JsonElement>> ConditionalAccess { get; init; }
    public required Reading<bool> SecurityDefaultsEnabled { get; init; }
    /// <summary>Member users registered for MFA, and member users in the report.</summary>
    public required Reading<(int Registered, int Total)> MfaRegistration { get; init; }
    public required Reading<int> IntuneCompliancePolicies { get; init; }
    /// <summary>SharePoint's sharingCapability: disabled, existingExternalUserSharingOnly, externalUserSharingOnly or externalUserAndGuestSharing.</summary>
    public required Reading<string> SharingCapability { get; init; }
    public required Reading<int> SimulationsLast90Days { get; init; }
    public required Reading<(double Current, double Max)> SecureScore { get; init; }
}

public enum PostureStatus { Pass, Fail, LicensedNotConfigured, LicensedLowAdoption, NotLicensed, Unknown }

public sealed record PostureFinding(
    PostureCheck Check,
    PostureStatus Status,
    string Detail,
    string? LicensedBy,
    IReadOnlyList<string> UnlockedBy,
    string ReadFrom);

public sealed record PostureSummary(int Total, int Pass, int Fail, int LicensedNotConfigured, int LicensedLowAdoption, int NotLicensed, int Unknown);

public sealed record UpgradeGroup(IReadOnlyList<string> UnlockedBy, IReadOnlyList<PostureFinding> Findings);

public sealed record PostureResult(
    DateTimeOffset GeneratedAt,
    string? OrganizationName,
    IReadOnlyList<OwnedSku> Licences,
    IReadOnlyList<string> IncludedFeatures,
    PostureSummary Summary,
    IReadOnlyList<PostureFinding> Findings,
    IReadOnlyList<UpgradeGroup> Upgrades,
    (int Pass, int Total) Scuba,
    (double Current, double Max)? SecureScore,
    string? Error);

/// <summary>
/// Turns a <see cref="PostureSnapshot"/> into findings: first "does a licence the tenant
/// owns include the feature?", then, only if it does, "is it set up?". Pure: the
/// licence data comes from the catalog and the tenant's own subscribedSkus, so every
/// status is unit-tested without Graph.
/// </summary>
public static class PostureEvaluator
{
    public const string ExchangeNotConnected =
        "Read with Exchange Online PowerShell, which Vigil365 doesn't connect to yet.";

    public static PostureResult Evaluate(PostureSnapshot s, PostureCatalog catalog)
    {
        if (s.Skus.Value is not { } skus)
        {
            var reason = $"The tenant's licences couldn't be read: {s.Skus.Error}";
            var unknown = catalog.Checks.Select(c => new PostureFinding(c, PostureStatus.Unknown, reason, null, [], ReadFrom(c))).ToList();
            return Result(s, catalog, [], [], unknown, reason);
        }

        var owned = skus.Where(k => k.Enabled > 0).ToList();
        var findings = catalog.Checks.Select(c => Evaluate(c, catalog.Feature(c.Feature), owned, s, catalog)).ToList();
        var included = catalog.Features
            .Where(f => !f.IsFree && LicensedBy(f, owned) is not null)
            .Select(f => f.Name).ToList();
        return Result(s, catalog, owned, included, findings, null);
    }

    private static PostureResult Result(PostureSnapshot s, PostureCatalog catalog, IReadOnlyList<OwnedSku> owned,
        IReadOnlyList<string> included, IReadOnlyList<PostureFinding> findings, string? error)
    {
        int Count(PostureStatus st) => findings.Count(f => f.Status == st);
        var summary = new PostureSummary(findings.Count, Count(PostureStatus.Pass), Count(PostureStatus.Fail),
            Count(PostureStatus.LicensedNotConfigured), Count(PostureStatus.LicensedLowAdoption), Count(PostureStatus.NotLicensed), Count(PostureStatus.Unknown));

        var upgrades = findings.Where(f => f.Status == PostureStatus.NotLicensed)
            .GroupBy(f => string.Join("|", f.UnlockedBy))
            .Select(g => new UpgradeGroup(g.First().UnlockedBy, g.ToList()))
            .ToList();

        var scuba = findings.Where(f => f.Check.Baselines.Scuba.Count > 0).ToList();
        return new PostureResult(s.ReadAt, s.OrganizationName, owned, included, summary, findings, upgrades,
            (scuba.Count(f => f.Status == PostureStatus.Pass), scuba.Count),
            s.SecureScore.Read ? s.SecureScore.Value : null, error);
    }

    private static PostureFinding Evaluate(PostureCheck check, PostureFeature feature, IReadOnlyList<OwnedSku> owned, PostureSnapshot s, PostureCatalog catalog)
    {
        var licensedBy = LicensedBy(feature, owned);
        if (licensedBy is null)
        {
            var unlockedBy = catalog.Skus
                .Where(k => k.ServicePlans.Intersect(feature.RequiresAnyServicePlan, StringComparer.OrdinalIgnoreCase).Any())
                .OrderBy(k => k.ServicePlans.Count) // the add-on before the suite that includes it
                .Select(k => k.Name).ToList();
            return new(check, PostureStatus.NotLicensed, $"Needs {feature.Name}. No licence in this tenant includes it.", null, unlockedBy, ReadFrom(check));
        }

        if (check.Reads == "exchange")
            return new(check, PostureStatus.Unknown, ExchangeNotConnected, licensedBy, [], ReadFrom(check));

        var (status, detail) = check.Rule switch
        {
            "caMfaAllUsers" => CaRule(s, CaTarget.Mfa, "requires MFA for all users"),
            "caBlockLegacyAuth" => CaRule(s, CaTarget.LegacyAuth, "blocks legacy authentication for all users"),
            "mfaEnforced" => MfaEnforced(s, ConditionalAccessLicensed(owned, catalog)),
            "mfaRegistration" => MfaRegistration(s, check.Threshold ?? 95),
            "caBlockHighRisk" => HighRisk(s),
            "pimInUse" => (PostureStatus.Unknown, "Needs RoleManagement.Read.Directory to read PIM assignments. Vigil365 doesn't request it yet."),
            "intuneCompliancePolicy" => Count(s.IntuneCompliancePolicies, "device compliance policy", "device compliance policies"),
            "sharepointSharing" => Sharing(s),
            "attackSimulation" => Count(s.SimulationsLast90Days, "simulation in the last 90 days", "simulations in the last 90 days"),
            _ => (PostureStatus.Unknown, $"No rule '{check.Rule}' in this version of Vigil365."),
        };
        return new(check, status, detail, licensedBy, [], ReadFrom(check));
    }

    /// <summary>The first owned licence that includes the feature, or "Every Microsoft 365 tenant" for a free one.</summary>
    private static string? LicensedBy(PostureFeature feature, IReadOnlyList<OwnedSku> owned)
    {
        if (feature.IsFree) return feature.Name;
        return owned.FirstOrDefault(k => k.ServicePlans.Intersect(feature.RequiresAnyServicePlan, StringComparer.OrdinalIgnoreCase).Any())?.Name;
    }

    private static string ReadFrom(PostureCheck c) => c.Reads == "exchange" ? "Exchange Online PowerShell" : "Microsoft Graph";

    /// <summary>Whether a licence the tenant owns includes Conditional Access. If the catalog lacks the feature, assume it does, so a failed read stays Unknown.</summary>
    private static bool ConditionalAccessLicensed(IReadOnlyList<OwnedSku> owned, PostureCatalog catalog) =>
        catalog.Features.FirstOrDefault(f => f.Id == "conditional-access") is not { } ca || LicensedBy(ca, owned) is not null;

    // ── Conditional Access ────────────────────────────────────────────────────
    //
    // "All" in includeUsers isn't enough for a pass. Microsoft applies a policy only when every
    // one of its conditions matches, so an all-users policy scoped to some apps, to risky
    // sign-ins, or to some locations or platforms, or one that accepts a compliant device
    // instead of MFA (grant operator OR), leaves ordinary sign-ins out. A policy counts only
    // when nothing but its excluded accounts is left out. Those are listed, not failed:
    // Microsoft recommends excluding emergency access accounts from every policy. Each policy
    // is judged on its own; several that only add up together don't pass.

    private enum CaTarget { Mfa, LegacyAuth, UserRisk, SignInRisk }

    private static (PostureStatus, string) CaRule(PostureSnapshot s, CaTarget target, string what) =>
        s.ConditionalAccess.Value is { } policies
            ? Coverage(policies, target, what)
            : (PostureStatus.Unknown, $"Couldn't read Conditional Access: {s.ConditionalAccess.Error}");

    /// <summary>Pass if an enabled policy covers the target for every sign-in; otherwise the nearest miss, in words.</summary>
    private static (PostureStatus Status, string Detail) Coverage(IReadOnlyList<JsonElement> policies, CaTarget target, string what)
    {
        if (policies.Count == 0) return (PostureStatus.LicensedNotConfigured, "No Conditional Access policies exist.");
        var aimed = policies.Where(p => Aims(p, target)).Select(p => (Policy: p, Limits: Limits(p, target))).ToList();
        var covering = aimed.Where(a => a.Limits.Count == 0).Select(a => a.Policy).ToList();

        if (covering.Where(p => State(p) == "enabled").ToList() is [var on, ..])
            return (PostureStatus.Pass, $"Enabled policy {Quote(on)} {what}.{ExclusionNote(on)}");
        if (covering is [var off, ..])
            return (PostureStatus.Fail, $"Policy {Quote(off)} {what}, but it is {Describe(State(off))}.");
        if (aimed.Where(a => State(a.Policy) == "enabled").ToList() is [var part, ..])
            return (PostureStatus.Fail, $"Enabled policy {Quote(part.Policy)} is for all users, but {Join(part.Limits, "and")}.");
        return (PostureStatus.Fail, $"{policies.Count} Conditional Access {(policies.Count == 1 ? "policy exists" : "policies exist")}, but none {what}.");
    }

    private static (PostureStatus, string) MfaEnforced(PostureSnapshot s, bool conditionalAccessLicensed)
    {
        var defaults = s.SecurityDefaultsEnabled;
        if (defaults.Read && defaults.Value) return (PostureStatus.Pass, "Security Defaults are on.");

        // An enabled policy enforces MFA whatever Security Defaults say, so it settles the check
        // even when that read failed. A failed read is never taken to mean "off".
        (PostureStatus Status, string Detail)? ca = s.ConditionalAccess.Value is { } policies
            ? Coverage(policies, CaTarget.Mfa, "requires MFA for all users")
            : null;
        if (ca is { Status: PostureStatus.Pass } pass) return (PostureStatus.Pass, pass.Detail);
        if (!defaults.Read)
            return (PostureStatus.Unknown, $"Couldn't read Security Defaults: {defaults.Error} "
                + (ca is { } read ? read.Detail : $"Conditional Access couldn't be read either: {s.ConditionalAccess.Error}"));
        if (ca is not { } policiesRead)
            return conditionalAccessLicensed
                ? (PostureStatus.Unknown, $"Security Defaults are off, and Conditional Access couldn't be read: {s.ConditionalAccess.Error}")
                : (PostureStatus.Fail, "Security Defaults are off, and no licence in this tenant includes Conditional Access.");
        return (PostureStatus.Fail, $"Security Defaults are off. {policiesRead.Detail}");
    }

    private static (PostureStatus, string) HighRisk(PostureSnapshot s)
    {
        if (s.ConditionalAccess.Value is not { } policies) return (PostureStatus.Unknown, $"Couldn't read Conditional Access: {s.ConditionalAccess.Error}");
        var users = Coverage(policies, CaTarget.UserRisk, "blocks high-risk users");
        var signIns = Coverage(policies, CaTarget.SignInRisk, "blocks high-risk sign-ins");
        if (users.Status == PostureStatus.Pass && signIns.Status == PostureStatus.Pass)
            return (PostureStatus.Pass, $"{users.Detail} {signIns.Detail}");
        if (!policies.Any(p => Aims(p, CaTarget.UserRisk) || Aims(p, CaTarget.SignInRisk)))
            return (PostureStatus.LicensedNotConfigured, "No policy blocks high-risk users or high-risk sign-ins.");
        return (PostureStatus.Fail, $"{users.Detail} {signIns.Detail}");
    }

    /// <summary>The policy is meant for the target: it names all users and has the grant (and, for risk, the condition) that make it that kind of policy.</summary>
    private static bool Aims(JsonElement p, CaTarget target) => AllUsers(p) && target switch
    {
        CaTarget.Mfa => Grants(p, "mfa") || HasAuthStrength(p),
        CaTarget.LegacyAuth => Grants(p, "block") && Has(ClientApps(p), "exchangeActiveSync") && Has(ClientApps(p), "other"),
        CaTarget.UserRisk => Grants(p, "block") && Has(RiskLevels(p, "userRiskLevels"), "high"),
        CaTarget.SignInRisk => Grants(p, "block") && Has(RiskLevels(p, "signInRiskLevels"), "high"),
        _ => false,
    };

    private static readonly HashSet<string> KnownConditions = new(StringComparer.OrdinalIgnoreCase)
        { "users", "applications", "clientAppTypes", "userRiskLevels", "signInRiskLevels", "locations", "platforms", "devices" };

    /// <summary>What an all-users policy still leaves out, in words. Empty: it covers every sign-in apart from its excluded accounts.</summary>
    private static List<string> Limits(JsonElement p, CaTarget target)
    {
        var limits = new List<string>();
        if (!Has(Strings(p, "conditions", "applications", "includeApplications"), "All"))
            limits.Add("it doesn't cover all cloud apps");
        else if (Strings(p, "conditions", "applications", "excludeApplications").Count is > 0 and var apps)
            limits.Add($"it excludes {Plural(apps, "cloud app", "cloud apps")}");

        // Two risk conditions on one policy must both match, so a block on high user risk that
        // also sets sign-in risk only fires when both are high.
        if (RiskLevels(p, "userRiskLevels").Count > 0 && target != CaTarget.UserRisk)
            limits.Add(target == CaTarget.SignInRisk ? "it only blocks when the user is risky too" : "it applies only to risky users");
        if (RiskLevels(p, "signInRiskLevels").Count > 0 && target != CaTarget.SignInRisk)
            limits.Add(target == CaTarget.UserRisk ? "it only blocks when the sign-in is risky too" : "it applies only to risky sign-ins");

        // Legacy clients can't do MFA (that's what the legacy block is for), so browsers plus
        // modern apps cover every client the other checks are about.
        var clients = ClientApps(p);
        if (target != CaTarget.LegacyAuth && clients.Count > 0 && !Has(clients, "all")
            && !(Has(clients, "browser") && Has(clients, "mobileAppsAndDesktopClients")))
            limits.Add("it doesn't cover every client app");

        if (Strings(p, "conditions", "locations", "excludeLocations").Count > 0
            || Strings(p, "conditions", "locations", "includeLocations") is { Count: > 0 } locations && !Has(locations, "All"))
            limits.Add("it depends on where the sign-in comes from");
        if (Strings(p, "conditions", "platforms", "excludePlatforms").Count > 0
            || Strings(p, "conditions", "platforms", "includePlatforms") is { Count: > 0 } platforms && !Has(platforms, "all"))
            limits.Add("it covers only some device platforms");
        if (Meaningful(Node(p, "conditions", "devices", "deviceFilter", "rule")))
            limits.Add("it has a device filter");

        // A condition this code doesn't know (a rarer one, or one Microsoft adds later) may
        // narrow the policy too, so it counts against it rather than being ignored.
        if (Node(p, "conditions") is { ValueKind: JsonValueKind.Object } conditions)
            foreach (var c in conditions.EnumerateObject())
                if (!KnownConditions.Contains(c.Name) && !c.Name.StartsWith('@') && Meaningful(c.Value))
                    limits.Add($"it has an extra condition ({c.Name})");

        if (target == CaTarget.Mfa && MfaAlternatives(p) is { Count: > 0 } alternatives)
            limits.Add($"it accepts {Join(alternatives, "or")} instead of MFA");
        return limits;
    }

    /// <summary>Grant controls that satisfy the policy instead of MFA. None when the operator is AND.</summary>
    private static List<string> MfaAlternatives(JsonElement p)
    {
        // A missing operator is treated as OR, so a policy that can't be read in full never passes.
        if (Node(p, "grantControls", "operator") is { ValueKind: JsonValueKind.String } op
            && string.Equals(op.GetString(), "AND", StringComparison.OrdinalIgnoreCase)) return [];
        var others = Strings(p, "grantControls", "builtInControls")
            .Where(c => !string.Equals(c, "mfa", StringComparison.OrdinalIgnoreCase))
            .Select(c => c switch
            {
                "compliantDevice" => "a compliant device",
                "domainJoinedDevice" => "a hybrid-joined device",
                "approvedApplication" => "an approved client app",
                "compliantApplication" => "an app protection policy",
                "passwordChange" => "a password change",
                _ => c,
            })
            .ToList();
        if (Strings(p, "grantControls", "termsOfUse").Count > 0) others.Add("terms of use");
        if (Strings(p, "grantControls", "customAuthenticationFactors").Count > 0) others.Add("a custom control");
        return others;
    }

    /// <summary>" It excludes 2 users and 1 group; check each exclusion is deliberate.", or "" when it excludes nobody.</summary>
    private static string ExclusionNote(JsonElement p)
    {
        var parts = new List<string>();
        void Add(string field, string one, string many)
        {
            if (Strings(p, "conditions", "users", field).Count is > 0 and var n) parts.Add(Plural(n, one, many));
        }
        Add("excludeUsers", "user", "users");
        Add("excludeGroups", "group", "groups");
        Add("excludeRoles", "role", "roles");
        if (Meaningful(Node(p, "conditions", "users", "excludeGuestsOrExternalUsers"))) parts.Add("guests or external users");
        return parts.Count == 0 ? "" : $" It excludes {Join(parts, "and")}; check each exclusion is deliberate.";
    }

    // ── Other workloads ───────────────────────────────────────────────────────

    private static (PostureStatus, string) MfaRegistration(PostureSnapshot s, int threshold)
    {
        if (!s.MfaRegistration.Read) return (PostureStatus.Unknown, $"Couldn't read MFA registration: {s.MfaRegistration.Error}");
        var r = s.MfaRegistration.Value;
        if (r.Total <= 0) return (PostureStatus.Unknown, "The MFA registration report has no users yet.");
        var pct = (int)Math.Floor(r.Registered * 100.0 / r.Total);
        var missing = r.Total - r.Registered;
        return pct >= threshold
            ? (PostureStatus.Pass, $"{pct}% of member users are registered for MFA.")
            : (PostureStatus.Fail, $"{pct}% of member users are registered for MFA (target {threshold}%); {missing} {(missing == 1 ? "isn't" : "aren't")}.");
    }

    private static (PostureStatus, string) Count(Reading<int> reading, string one, string many)
    {
        if (!reading.Read) return (PostureStatus.Unknown, $"Couldn't read: {reading.Error}");
        return reading.Value > 0
            ? (PostureStatus.Pass, $"{reading.Value} {(reading.Value == 1 ? one : many)}.")
            : (PostureStatus.LicensedNotConfigured, $"No {many}.");
    }

    private static (PostureStatus, string) Sharing(PostureSnapshot s)
    {
        if (s.SharingCapability.Value is not { } level) return (PostureStatus.Unknown, $"Couldn't read SharePoint sharing: {s.SharingCapability.Error}");
        return level switch
        {
            "disabled" => (PostureStatus.Pass, "Sharing is limited to people in the organisation."),
            "existingExternalUserSharingOnly" => (PostureStatus.Pass, "Sharing is limited to existing guests."),
            "externalUserSharingOnly" => (PostureStatus.Fail, "New and existing guests can be invited."),
            "externalUserAndGuestSharing" => (PostureStatus.Fail, "Anyone links are allowed: files can be shared with no sign-in."),
            _ => (PostureStatus.Unknown, $"Unrecognised sharing level '{level}'."),
        };
    }

    // ── Conditional Access policy helpers ─────────────────────────────────────

    private static string State(JsonElement p) => p.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";

    private static string Describe(string state) => state switch
    {
        "enabledForReportingButNotEnforced" => "in report-only mode",
        "disabled" => "turned off",
        _ => state,
    };

    private static string Quote(JsonElement p) =>
        p.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String ? $"“{n.GetString()}”" : "(unnamed)";

    private static bool AllUsers(JsonElement p) =>
        Strings(p, "conditions", "users", "includeUsers").Contains("All", StringComparer.OrdinalIgnoreCase);

    private static bool Grants(JsonElement p, string control) =>
        Strings(p, "grantControls", "builtInControls").Contains(control, StringComparer.OrdinalIgnoreCase);

    private static bool HasAuthStrength(JsonElement p) =>
        Node(p, "grantControls", "authenticationStrength").ValueKind == JsonValueKind.Object;

    private static IReadOnlyList<string> ClientApps(JsonElement p) => Strings(p, "conditions", "clientAppTypes");

    private static IReadOnlyList<string> RiskLevels(JsonElement p, string property) => Strings(p, "conditions", property);

    private static bool Has(IReadOnlyList<string> values, string value) => values.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static JsonElement Node(JsonElement e, params string[] path) => Get(e, path) ?? default;
    /// <summary>Whether a condition says anything: null, empty, "none" and Microsoft's "unknownFutureValue" placeholder don't.</summary>
    private static bool Meaningful(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() is { Length: > 0 } t
            && !t.Equals("none", StringComparison.OrdinalIgnoreCase) && !t.Equals("unknownFutureValue", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Array => v.EnumerateArray().Any(Meaningful),
        JsonValueKind.Object => v.EnumerateObject().Any(x => !x.Name.StartsWith('@') && Meaningful(x.Value)),
        JsonValueKind.True or JsonValueKind.Number => true,
        _ => false,
    };

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>"a", "a and b", "a, b and c" (or "or").</summary>
    private static string Join(IReadOnlyList<string> items, string last) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} {last} {items[^1]}";
}
