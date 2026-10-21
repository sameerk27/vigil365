using System.Text.Json;

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
            "caMfaAllUsers" => CaRule(s, p => Grants(p, "mfa") || HasAuthStrength(p), "requires MFA for all users"),
            "caBlockLegacyAuth" => CaRule(s, p => Grants(p, "block") && ClientApps(p).Contains("exchangeActiveSync") && ClientApps(p).Contains("other"), "blocks legacy authentication for all users"),
            "mfaEnforced" => MfaEnforced(s),
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

    // ── Conditional Access ────────────────────────────────────────────────────

    private static (PostureStatus, string) CaRule(PostureSnapshot s, Func<JsonElement, bool> grants, string what)
    {
        if (s.ConditionalAccess.Value is not { } policies) return (PostureStatus.Unknown, $"Couldn't read Conditional Access: {s.ConditionalAccess.Error}");
        if (policies.Count == 0) return (PostureStatus.LicensedNotConfigured, "No Conditional Access policies exist.");
        var matching = policies.Where(p => AllUsers(p) && grants(p)).ToList();
        if (matching.Any(p => State(p) == "enabled"))
            return (PostureStatus.Pass, $"Enabled policy {Quote(matching.First(p => State(p) == "enabled"))} {what}.");
        if (matching.Count > 0)
            return (PostureStatus.Fail, $"Policy {Quote(matching[0])} {what}, but it is {Describe(State(matching[0]))}.");
        return (PostureStatus.Fail, $"{policies.Count} Conditional Access {(policies.Count == 1 ? "policy exists" : "policies exist")}, but none {what}.");
    }

    private static (PostureStatus, string) MfaEnforced(PostureSnapshot s)
    {
        if (s.SecurityDefaultsEnabled.Value) return (PostureStatus.Pass, "Security Defaults are on.");
        if (s.ConditionalAccess.Value is { } policies
            && policies.Any(p => State(p) == "enabled" && AllUsers(p) && (Grants(p, "mfa") || HasAuthStrength(p))))
            return (PostureStatus.Pass, "Security Defaults are off and a Conditional Access policy requires MFA for all users.");
        if (!s.SecurityDefaultsEnabled.Read && !s.ConditionalAccess.Read)
            return (PostureStatus.Unknown, $"Couldn't read Security Defaults or Conditional Access: {s.SecurityDefaultsEnabled.Error}");
        return (PostureStatus.Fail, "Security Defaults are off and no enabled Conditional Access policy requires MFA for all users.");
    }

    private static (PostureStatus, string) HighRisk(PostureSnapshot s)
    {
        if (s.ConditionalAccess.Value is not { } policies) return (PostureStatus.Unknown, $"Couldn't read Conditional Access: {s.ConditionalAccess.Error}");
        bool Blocks(string riskProperty) => policies.Any(p => State(p) == "enabled" && AllUsers(p) && Grants(p, "block") && RiskLevels(p, riskProperty).Contains("high"));
        var users = Blocks("userRiskLevels");
        var signIns = Blocks("signInRiskLevels");
        if (users && signIns) return (PostureStatus.Pass, "Enabled policies block high-risk users and high-risk sign-ins.");
        if (!users && !signIns) return (PostureStatus.LicensedNotConfigured, "No enabled policy blocks high-risk users or high-risk sign-ins.");
        return (PostureStatus.Fail, users ? "High-risk users are blocked, but high-risk sign-ins aren't." : "High-risk sign-ins are blocked, but high-risk users aren't.");
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
        p.TryGetProperty("grantControls", out var g) && g.ValueKind == JsonValueKind.Object
        && g.TryGetProperty("authenticationStrength", out var a) && a.ValueKind == JsonValueKind.Object;

    private static IReadOnlyList<string> ClientApps(JsonElement p) => Strings(p, "conditions", "clientAppTypes");

    private static IReadOnlyList<string> RiskLevels(JsonElement p, string property) => Strings(p, "conditions", property);

    private static IReadOnlyList<string> Strings(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return [];
        }
        return e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
    }
}
