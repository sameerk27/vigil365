using System.Text.Json;
using System.Text.Json.Nodes;
using M365SecurityDashboard.Api.Services.Posture;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Shared posture test data. Policy JSON follows the Graph v1.0 conditionalAccessPolicy shape
/// as documented (every condition present, unused ones null or empty); it hasn't been compared
/// with a live tenant's response. Licences use the part numbers and service plan names from
/// Microsoft's licensing CSV, as in Data/Posture/entitlements.json.
/// </summary>
internal static class PostureFixtures
{
    public static readonly OwnedSku EntraP2 = new("AAD_PREMIUM_P2", "Microsoft Entra ID P2", 10, 5, ["AAD_PREMIUM", "AAD_PREMIUM_P2"]);
    public static readonly OwnedSku ExchangeOnly = new("EXCHANGESTANDARD", "Exchange Online (Plan 1)", 10, 5, ["EXCHANGE_S_STANDARD"]);
    public static readonly OwnedSku E5 = new("SPE_E5", "Microsoft 365 E5", 50, 40,
        ["AAD_PREMIUM", "AAD_PREMIUM_P2", "INTUNE_A", "SHAREPOINTENTERPRISE", "ATP_ENTERPRISE", "THREAT_INTELLIGENCE", "EXCHANGE_S_ENTERPRISE"]);

    public static JsonObject Policy(string name = "Require MFA for all users", string grant = "mfa") => JsonNode.Parse($$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "displayName": {{JsonSerializer.Serialize(name)}},
          "state": "enabled",
          "conditions": {
            "users": {
              "includeUsers": ["All"], "excludeUsers": [], "includeGroups": [], "excludeGroups": [],
              "includeRoles": [], "excludeRoles": [], "includeGuestsOrExternalUsers": null, "excludeGuestsOrExternalUsers": null
            },
            "applications": {
              "includeApplications": ["All"], "excludeApplications": [], "includeUserActions": [],
              "includeAuthenticationContextClassReferences": [], "applicationFilter": null
            },
            "clientAppTypes": ["all"],
            "userRiskLevels": [],
            "signInRiskLevels": [],
            "servicePrincipalRiskLevels": [],
            "insiderRiskLevels": null,
            "locations": null,
            "platforms": null,
            "devices": null,
            "clientApplications": null,
            "authenticationFlows": null
          },
          "grantControls": {
            "operator": "OR", "builtInControls": ["{{grant}}"], "customAuthenticationFactors": [], "termsOfUse": [],
            "authenticationStrength": null
          },
          "sessionControls": null
        }
        """)!.AsObject();

    public static JsonObject LegacyBlock()
    {
        var p = Policy("Block legacy authentication", grant: "block");
        Conditions(p)["clientAppTypes"] = Strings("exchangeActiveSync", "other");
        return p;
    }

    public static JsonObject RiskBlock(string name, params string[] riskProperties)
    {
        var p = Policy(name, grant: "block");
        foreach (var property in riskProperties) Conditions(p)[property] = Strings("high");
        return p;
    }

    public static JsonObject Conditions(JsonObject p) => p["conditions"]!.AsObject();
    public static JsonObject Users(JsonObject p) => Conditions(p)["users"]!.AsObject();
    public static JsonObject Apps(JsonObject p) => Conditions(p)["applications"]!.AsObject();
    public static JsonObject GrantControls(JsonObject p) => p["grantControls"]!.AsObject();
    public static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>
    /// A tenant with Entra ID P2 unless other licences are given. Null policies or Security
    /// Defaults: that read failed. Every other reading failed; set it with `with`.
    /// </summary>
    public static PostureSnapshot Snapshot(IEnumerable<JsonObject>? policies, bool? securityDefaults = false, params OwnedSku[] skus) => new()
    {
        ReadAt = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
        Skus = Reading<IReadOnlyList<OwnedSku>>.Ok(skus.Length > 0 ? skus : [EntraP2]),
        ConditionalAccess = policies is null
            ? Reading<IReadOnlyList<JsonElement>>.Failed("Permission denied.")
            : Reading<IReadOnlyList<JsonElement>>.Ok(policies.Select(p => JsonSerializer.Deserialize<JsonElement>(p.ToJsonString())).ToList()),
        SecurityDefaultsEnabled = securityDefaults is { } on ? Reading<bool>.Ok(on) : Reading<bool>.Failed("Permission denied."),
        MfaRegistration = Reading<(int, int)>.Failed("not read"),
        IntuneCompliancePolicies = Reading<int>.Failed("not read"),
        SharingCapability = Reading<string>.Failed("not read"),
        SimulationsLast90Days = Reading<int>.Failed("not read"),
        SecureScore = Reading<(double, double)>.Failed("not read"),
    };

    public static PostureResult Evaluate(PostureSnapshot s) => PostureEvaluator.Evaluate(s, PostureCatalog.Default);

    public static PostureFinding Check(PostureSnapshot s, string id) => Evaluate(s).Findings.Single(f => f.Check.Id == id);
}
