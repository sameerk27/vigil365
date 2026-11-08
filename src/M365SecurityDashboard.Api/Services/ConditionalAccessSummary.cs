using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>One row of the Conditional Access table: who, what and how, in words.</summary>
public sealed record CaPolicySummary(string Name, string State, string InclUsers, string ExclUsers, string Apps, string[] Controls);

/// <summary>
/// Summarises a Conditional Access policy for the table: every kind of target (users, groups, roles,
/// guests), apps or a user action or an authentication context, and every control that applies
/// (built-in controls, an authentication strength, the session controls that are on).
/// </summary>
public static class ConditionalAccessSummary
{
    private static readonly Dictionary<string, string> UserActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["urn:user:registersecurityinfo"] = "Register security info",
        ["urn:user:registerdevice"] = "Register or join devices",
    };

    private static readonly (string Key, string Label)[] SessionControls =
    [
        ("signInFrequency", "Sign-in frequency"), ("persistentBrowser", "Persistent browser"),
        ("applicationEnforcedRestrictions", "App enforced restrictions"), ("cloudAppSecurity", "Conditional Access App Control"),
        ("continuousAccessEvaluation", "Continuous access evaluation"),
    ];

    public static CaPolicySummary Summarize(JsonElement p)
    {
        var name = Str(p, "displayName") ?? "Unnamed";
        var state = Str(p, "state") ?? "unknown";
        var users = Obj(Obj(p, "conditions"), "users");
        var applications = Obj(Obj(p, "conditions"), "applications");

        var incUsers = Strings(users, "includeUsers");
        var scope = new List<string>();
        if (incUsers.Contains("All")) scope.Add("All Users");
        else
        {
            var named = incUsers.Count(u => u != "GuestsOrExternalUsers");
            if (named > 0) scope.Add(Plural(named, "user"));
            var groups = Strings(users, "includeGroups").Count;
            if (groups > 0) scope.Add(Plural(groups, "group"));
            var roles = Strings(users, "includeRoles").Count;
            if (roles > 0) scope.Add(Plural(roles, "role"));
            if (incUsers.Contains("GuestsOrExternalUsers") || Obj(users, "includeGuestsOrExternalUsers") is not null) scope.Add("guests");
        }
        var inclUsers = scope.Count > 0 ? string.Join(", ", scope) : "No users";
        var excluded = Strings(users, "excludeUsers").Count + Strings(users, "excludeGroups").Count + Strings(users, "excludeRoles").Count;
        var exclUsers = excluded > 0 ? $"{excluded} excluded" : "None";

        var incApps = Strings(applications, "includeApplications");
        var actions = Strings(applications, "includeUserActions");
        var contexts = Strings(applications, "includeAuthenticationContextClassReferences");
        var apps = incApps.Contains("All") ? "All Apps"
            : incApps.Contains("Office365") ? (incApps.Count > 1 ? $"Office 365 + {Plural(incApps.Count - 1, "app")}" : "Office 365")
            : incApps.Count > 0 ? Plural(incApps.Count, "app")
            : actions.Count > 0 ? string.Join(", ", actions.Select(a => UserActions.GetValueOrDefault(a, a)))
            : contexts.Count > 0 ? Plural(contexts.Count, "auth context") : "No apps";

        var grant = Obj(p, "grantControls");
        var controls = new List<string>(Strings(grant, "builtInControls"));
        var strength = Str(Obj(grant, "authenticationStrength"), "displayName");
        if (strength is not null) controls.Add($"Auth strength: {strength}");
        var session = Obj(p, "sessionControls");
        foreach (var (key, label) in SessionControls)
        {
            var c = Obj(session, key);
            if (c is JsonElement e && !(e.TryGetProperty("isEnabled", out var on) && on.ValueKind == JsonValueKind.False)) controls.Add(label);
        }
        // Unlike the other session controls, Graph sends this one as a plain true or false.
        if (session is JsonElement sc && sc.TryGetProperty("disableResilienceDefaults", out var off) && off.ValueKind == JsonValueKind.True) controls.Add("Resilience defaults off");
        return new CaPolicySummary(name, state, inclUsers, exclUsers, apps, controls.ToArray());
    }

    private static string Plural(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";

    private static JsonElement? Obj(JsonElement? e, string prop) =>
        e is JsonElement o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? e, string prop) =>
        e is JsonElement o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement? e, string prop) =>
        e is JsonElement o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0 && x != "None").ToList()
            : [];
}
