using System.Globalization;
using System.Text.Json;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services.PowerPlatform;

public sealed record PowerPlatformFlag(string Label, string Severity);

/// <summary>An owner object id resolved through Graph. Found = false: no user with that id.</summary>
public sealed record PowerPlatformOwner(string Id, string? Name, string? Upn, bool? Enabled, bool Found);

public sealed record PowerPlatformEnvironment(
    string Id, string Name, string? Type, string? Region, bool? IsManaged, int Apps, int Flows, int Agents);

public sealed record PowerPlatformApp(
    string Id, string Name, string EnvironmentId, string EnvironmentName, string? OwnerId, string? Owner,
    DateTimeOffset? Created, DateTimeOffset? Modified, int? SharedUsers, int? SharedGroups, bool? BypassConsent,
    bool Quarantined, IReadOnlyList<string> Connectors, IReadOnlyList<PowerPlatformFlag> Flags);

public sealed record PowerPlatformFlow(
    string Id, string Name, string EnvironmentId, string EnvironmentName, string? OwnerId, string? Owner,
    DateTimeOffset? Created, DateTimeOffset? Modified, string? Trigger, IReadOnlyList<string> Connectors,
    IReadOnlyList<PowerPlatformFlag> Flags);

public sealed record PowerPlatformAgent(
    string Id, string Name, string EnvironmentId, string EnvironmentName, string? OwnerId, string? Owner,
    DateTimeOffset? Created, DateTimeOffset? Published, string? CreatedIn, string? Authentication,
    IReadOnlyList<string> Channels, int SharedUsers, int SharedGroups, bool SharedWithTenant, bool Quarantined,
    IReadOnlyList<string> Connectors, IReadOnlyList<PowerPlatformFlag> Flags);

public sealed record PowerPlatformSummary(
    int Environments, int Apps, int Flows, int Agents, int NeedALook, int ConsentBypassed, int HighRiskConnector,
    int AgentsWithoutSignIn, int SharedWithEveryone, int OwnerGone);

public sealed record PowerPlatformInventoryResult(
    DateTimeOffset ReadAt, IReadOnlyList<PowerPlatformEnvironment> Environments, IReadOnlyList<PowerPlatformApp> Apps,
    IReadOnlyList<PowerPlatformFlow> Flows, IReadOnlyList<PowerPlatformAgent> Agents, PowerPlatformSummary Summary,
    bool AppDetails, bool OwnersResolved, IReadOnlyList<string> Notes);

/// <summary>
/// What the browser read, with the signed-in person's own Power Platform access (the
/// Vigil365 server never calls the Power Platform API): the inventory rows, and for each
/// environment the apps-as-administrator page with sharing and consent detail.
/// </summary>
public sealed class PowerPlatformEvaluateRequest
{
    /// <summary>Rows of the PowerPlatformResources inventory table, as Microsoft returned them.</summary>
    public List<JsonElement> Resources { get; set; } = [];
    /// <summary>False when the inventory stopped before its last page.</summary>
    public bool ResourcesComplete { get; set; } = true;
    public List<PowerPlatformAppDetails> AppDetails { get; set; } = [];
    /// <summary>Environments with apps whose detail wasn't read (the per-read cap).</summary>
    public int AppDetailsSkipped { get; set; }
    /// <summary>Why app detail wasn't read at all, if it wasn't (for example the account isn't a Power Platform admin).</summary>
    public string? AppDetailsProblem { get; set; }
}

public sealed class PowerPlatformAppDetails
{
    public string EnvironmentId { get; set; } = "";
    public List<JsonElement> Items { get; set; } = [];
    public bool Complete { get; set; } = true;
    public string? Error { get; set; }
}

/// <summary>
/// Turns the Power Platform inventory into Vigil365's view: environments, canvas apps,
/// cloud flows and Copilot Studio agents, flagged where they need a look. Pure; the
/// endpoint resolves owners through Graph first.
///
/// Field names follow Microsoft's inventory schema (learn.microsoft.com/power-platform/
/// admin/inventory-schema and microsoft-copilot-studio/admin-agent-inventory) and the
/// "Get AdminApps" response, read case-insensitively so a casing change costs nothing.
/// </summary>
public static class PowerPlatformEvaluator
{
    public const string EnvironmentType = "microsoft.powerplatform/environments";
    public const string CanvasAppType = "microsoft.powerapps/canvasapps";
    public const string CloudFlowType = "microsoft.powerautomate/cloudflows";
    public const string AgentType = "microsoft.copilotstudio/agents";
    public static readonly string[] InventoryTypes = [EnvironmentType, CanvasAppType, CloudFlowType, AgentType];

    public const string ConsentBypassedFlag = "Consent prompt bypassed";
    public const string CustomConnectorFlag = "Custom connector";
    public const string GatewayFlag = "On-premises data gateway";
    public const string HighRiskPrefix = "High-risk connector: ";
    public const string SharedWithGroupFlag = "Shared with a group";
    public const string QuarantinedFlag = "Quarantined";
    public const string NoSignInFlag = "No sign-in required";
    public const string NoSignInDraftFlag = "No sign-in required (draft)";
    public const string WholeOrganisationFlag = "Shared with the whole organisation";
    public const string MakerConnectionFlag = "Tools use the maker's connection";
    public const string WebSearchFlag = "Uses web search";
    public const string OwnerGoneFlag = "Owner not in the directory";
    public const string OwnerDisabledFlag = "Owner's account is disabled";
    public const string CreatorGoneFlag = "Creator not in the directory";
    public const string CreatorDisabledFlag = "Creator's account is disabled";

    /// <summary>Owner object ids worth resolving: every owner of an app or agent, and every flow creator.</summary>
    public static IReadOnlyList<string> OwnerIds(PowerPlatformEvaluateRequest request) => request.Resources
        .Where(r => Type(r) is CanvasAppType or CloudFlowType or AgentType)
        .Select(r => Str(r, "properties", "ownerId") ?? Str(r, "properties", "createdBy"))
        .Where(id => id is not null && Guid.TryParse(id, out _))
        .Select(id => id!.ToLowerInvariant())
        .Distinct()
        .ToList();

    public static PowerPlatformInventoryResult Evaluate(
        PowerPlatformEvaluateRequest request, GraphOptions o, IReadOnlyDictionary<string, PowerPlatformOwner>? owners, DateTimeOffset now)
    {
        var notes = new List<string>();
        var envRows = request.Resources.Where(r => Type(r) == EnvironmentType).ToList();
        var envNames = envRows
            .Select(r => (Id: Str(r, "name") ?? "", Name: Str(r, "properties", "displayName") ?? Str(r, "name") ?? ""))
            .Where(e => e.Id.Length > 0)
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        string EnvName(string id) => envNames.TryGetValue(id, out var n) ? n : id;

        // The apps-as-administrator detail, keyed by app id (the inventory's Item ID).
        var details = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in request.AppDetails)
        {
            foreach (var a in page.Items)
                if (Str(a, "name") is { } appId) details[appId] = a;
            if (page.Error is not null) notes.Add($"{EnvName(page.EnvironmentId)}: app sharing and consent detail couldn't be read ({page.Error}).");
            else if (!page.Complete) notes.Add($"{EnvName(page.EnvironmentId)}: only part of the app detail could be read.");
        }
        if (request.AppDetailsProblem is { } problem) notes.Add($"App sharing and consent detail wasn't read: {problem}");
        if (request.AppDetailsSkipped > 0)
            notes.Add($"App sharing and consent detail was read for the first {o.PowerPlatformMaxEnvironments} environments with apps; {request.AppDetailsSkipped} more weren't.");
        if (!request.ResourcesComplete) notes.Add("Microsoft's inventory stopped before its last page, so some items are missing.");

        var apps = request.Resources.Where(r => Type(r) == CanvasAppType)
            .Select(r => ParseApp(r, details, EnvName, owners, o)).Where(a => a.Id.Length > 0).ToList();
        var flows = request.Resources.Where(r => Type(r) == CloudFlowType)
            .Select(r => ParseFlow(r, EnvName, owners, o)).Where(f => f.Id.Length > 0).ToList();
        var agents = request.Resources.Where(r => Type(r) == AgentType)
            .Select(r => ParseAgent(r, EnvName, owners, o)).Where(a => a.Id.Length > 0).ToList();

        var envs = envRows.Select(r =>
        {
            var id = Str(r, "name") ?? "";
            return new PowerPlatformEnvironment(
                Id: id,
                Name: EnvName(id),
                Type: Str(r, "properties", "environmentType"),
                Region: Str(r, "location"),
                IsManaged: Any(r, "properties", "isManaged") is { ValueKind: JsonValueKind.True or JsonValueKind.False } m ? m.ValueKind == JsonValueKind.True : null,
                Apps: apps.Count(a => Same(a.EnvironmentId, id)),
                Flows: flows.Count(f => Same(f.EnvironmentId, id)),
                Agents: agents.Count(a => Same(a.EnvironmentId, id)));
        }).Where(e => e.Id.Length > 0).OrderByDescending(e => e.Apps + e.Flows + e.Agents).ThenBy(e => e.Name).ToList();

        bool Look(IReadOnlyList<PowerPlatformFlag> f) => f.Any(x => x.Severity is "high" or "medium");
        bool HighRisk(IReadOnlyList<PowerPlatformFlag> f) => f.Any(x => x.Label.StartsWith(HighRiskPrefix, StringComparison.Ordinal));
        bool Gone(IReadOnlyList<PowerPlatformFlag> f) => f.Any(x => x.Label is OwnerGoneFlag or OwnerDisabledFlag or CreatorGoneFlag or CreatorDisabledFlag);

        var summary = new PowerPlatformSummary(
            Environments: envs.Count,
            Apps: apps.Count,
            Flows: flows.Count,
            Agents: agents.Count,
            NeedALook: apps.Count(a => Look(a.Flags)) + flows.Count(f => Look(f.Flags)) + agents.Count(a => Look(a.Flags)),
            ConsentBypassed: apps.Count(a => a.BypassConsent == true),
            HighRiskConnector: apps.Count(a => HighRisk(a.Flags)) + flows.Count(f => HighRisk(f.Flags)) + agents.Count(a => HighRisk(a.Flags)),
            AgentsWithoutSignIn: agents.Count(a => a.Flags.Any(f => f.Label == NoSignInFlag)),
            SharedWithEveryone: agents.Count(a => a.SharedWithTenant),
            OwnerGone: apps.Count(a => Gone(a.Flags)) + flows.Count(f => Gone(f.Flags)) + agents.Count(a => Gone(a.Flags)));

        return new PowerPlatformInventoryResult(now, envs, apps, flows, agents, summary,
            AppDetails: request.AppDetails.Count > 0, OwnersResolved: owners is not null, notes);
    }

    // ── Resources ───────────────────────────────────────────────────────────────

    internal static PowerPlatformApp ParseApp(JsonElement r, IReadOnlyDictionary<string, JsonElement> details,
        Func<string, string> envName, IReadOnlyDictionary<string, PowerPlatformOwner>? owners, GraphOptions o)
    {
        var id = Str(r, "name") ?? "";
        var envId = Str(r, "properties", "environmentId") ?? "";
        var connectors = InventoryConnectors(r);
        var flags = new List<PowerPlatformFlag>();

        int? users = null, groups = null;
        bool? bypass = null;
        string? ownerName = null;
        if (details.TryGetValue(id, out var d))
        {
            var p = Any(d, "properties") ?? default;
            var custom = false;
            var gateway = false;
            var connectorBypass = false;
            foreach (var c in Items(p, "connectionReferences"))
            {
                var name = Str(c, "displayName");
                if (name is not null && !connectors.Contains(name, StringComparer.OrdinalIgnoreCase)) connectors.Add(name);
                custom |= Bool(c, "isCustomApiConnection");
                gateway |= Bool(c, "isOnPremiseConnection");
                connectorBypass |= Bool(c, "bypassConsent");
            }
            bypass = Bool(p, "bypassConsent") || connectorBypass;
            // Missing stays unknown (null), not "shared with nobody".
            users = IntOrNull(p, "sharedUsersCount");
            groups = IntOrNull(p, "sharedGroupsCount");
            ownerName = Str(p, "owner", "displayName") ?? Str(p, "owner", "userPrincipalName");
            if (bypass == true) flags.Add(new(ConsentBypassedFlag, "high"));
            flags.AddRange(HighRiskFlags(connectors, o));
            if (custom) flags.Add(new(CustomConnectorFlag, "medium"));
            if (gateway) flags.Add(new(GatewayFlag, "medium"));
            if (users >= o.PowerPlatformWideSharingUsers) flags.Add(new($"Shared with {users} people", "medium"));
            if (groups > 0) flags.Add(new(SharedWithGroupFlag, "low"));
        }
        else flags.AddRange(HighRiskFlags(connectors, o));

        var quarantined = Bool(Any(r, "properties") ?? default, "isQuarantined");
        if (quarantined) flags.Add(new(QuarantinedFlag, "low"));

        var ownerId = Str(r, "properties", "ownerId");
        var owner = Owner(ownerId, owners);
        flags.AddRange(OwnerFlags(owner, OwnerGoneFlag, OwnerDisabledFlag));

        return new PowerPlatformApp(id, Str(r, "properties", "displayName") ?? id, envId, envName(envId),
            ownerId, owner?.Name ?? ownerName, Date(r, "createdAt"), Date(r, "lastModifiedAt"),
            users, groups, bypass, quarantined, connectors, flags);
    }

    internal static PowerPlatformFlow ParseFlow(JsonElement r, Func<string, string> envName,
        IReadOnlyDictionary<string, PowerPlatformOwner>? owners, GraphOptions o)
    {
        var id = Str(r, "name") ?? "";
        var envId = Str(r, "properties", "environmentId") ?? "";
        var connectors = InventoryConnectors(r);
        var trigger = Str(r, "properties", "trigger");
        var flags = HighRiskFlags(trigger is null || connectors.Contains(trigger, StringComparer.OrdinalIgnoreCase) ? connectors : [.. connectors, trigger], o).ToList();
        // Microsoft: for cloud flows the owner field holds the creator and doesn't follow ownership changes.
        var ownerId = Str(r, "properties", "ownerId") ?? Str(r, "properties", "createdBy");
        var owner = Owner(ownerId, owners);
        flags.AddRange(OwnerFlags(owner, CreatorGoneFlag, CreatorDisabledFlag));
        return new PowerPlatformFlow(id, Str(r, "properties", "displayName") ?? id, envId, envName(envId),
            ownerId, owner?.Name, Date(r, "createdAt"), Date(r, "lastModifiedAt"), trigger, connectors, flags);
    }

    internal static PowerPlatformAgent ParseAgent(JsonElement r, Func<string, string> envName,
        IReadOnlyDictionary<string, PowerPlatformOwner>? owners, GraphOptions o)
    {
        var id = Str(r, "name") ?? "";
        var envId = Str(r, "properties", "environmentId") ?? "";
        var p = Any(r, "properties") ?? default;
        var connectors = InventoryConnectors(r);
        var published = Date(r, "lastPublishedAt");
        var auth = Str(p, "authentication");
        var viewers = Any(p, "sharedWithViewers") ?? default;
        var tenantWide = Bool(viewers, "entireTenant");
        var quarantined = Bool(p, "isQuarantined");

        var flags = new List<PowerPlatformFlag>();
        if (string.Equals(auth, "None", StringComparison.OrdinalIgnoreCase))
            flags.Add(published is null ? new(NoSignInDraftFlag, "low") : new(NoSignInFlag, "high"));
        flags.AddRange(HighRiskFlags(connectors, o));
        if (tenantWide) flags.Add(new(WholeOrganisationFlag, "medium"));
        // A tool on the maker's connection acts for every user with the maker's access.
        var makerConnection = Items(p, "powerPlatformConnectors")
            .SelectMany(c => Items(c, "operations"))
            .Any(op => string.Equals(Str(op, "connectionProvider"), "Maker", StringComparison.OrdinalIgnoreCase)
                       && Any(op, "isEnabled") is not { ValueKind: JsonValueKind.False });
        if (makerConnection) flags.Add(new(MakerConnectionFlag, "medium"));
        if (Bool(p, "isWebSearchEnabledForKnowledge")) flags.Add(new(WebSearchFlag, "low"));
        if (quarantined) flags.Add(new(QuarantinedFlag, "low"));

        var ownerId = Str(p, "ownerId") ?? Str(p, "createdBy");
        var owner = Owner(ownerId, owners);
        flags.AddRange(OwnerFlags(owner, OwnerGoneFlag, OwnerDisabledFlag));

        var channels = Items(p, "channels").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList();
        return new PowerPlatformAgent(id, Str(p, "displayName") ?? id, envId, envName(envId), ownerId, owner?.Name,
            Date(r, "createdAt"), published, Str(p, "createdIn"), auth, channels,
            Int(viewers, "userCount"), Int(viewers, "groupCount"), tenantWide, quarantined, connectors, flags);
    }

    private static PowerPlatformOwner? Owner(string? id, IReadOnlyDictionary<string, PowerPlatformOwner>? owners) =>
        id is not null && owners is not null && owners.TryGetValue(id.ToLowerInvariant(), out var o) ? o : null;

    private static IEnumerable<PowerPlatformFlag> OwnerFlags(PowerPlatformOwner? owner, string gone, string disabled)
    {
        if (owner is null) yield break;
        if (!owner.Found) yield return new(gone, "medium");
        else if (owner.Enabled == false) yield return new(disabled, "medium");
    }

    /// <summary>Connector ids from the inventory's powerPlatformConnectors (for example shared_sharepointonline).</summary>
    internal static List<string> InventoryConnectors(JsonElement r)
    {
        var names = new List<string>();
        foreach (var c in Items(Any(r, "properties") ?? default, "powerPlatformConnectors"))
            if (Str(c, "connectorId") is { } id && !names.Contains(id, StringComparer.OrdinalIgnoreCase)) names.Add(id);
        return names;
    }

    /// <summary>
    /// The configured high-risk list, matched against connector display names and ids
    /// ("shared_" dropped), ignoring case; a trailing * is a prefix match.
    /// </summary>
    internal static IEnumerable<PowerPlatformFlag> HighRiskFlags(IEnumerable<string> connectors, GraphOptions o)
    {
        foreach (var c in connectors)
        {
            var name = c.StartsWith("shared_", StringComparison.OrdinalIgnoreCase) ? c["shared_".Length..] : c;
            if (o.PowerPlatformHighRiskConnectors.Any(pattern => Matches(name, pattern)))
                yield return new PowerPlatformFlag(HighRiskPrefix + c, "high");
        }
    }

    private static bool Matches(string name, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        return pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── JSON helpers: case-insensitive, tolerant of missing or odd values ──────

    private static string? Type(JsonElement r) => Str(r, "type")?.ToLowerInvariant();

    private static JsonElement? Any(JsonElement e, params string[] path)
    {
        JsonElement cur = e;
        foreach (var part in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            JsonElement? next = null;
            foreach (var p in cur.EnumerateObject())
                if (string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase)) { next = p.Value; break; }
            if (next is not { } n) return null;
            cur = n;
        }
        return cur;
    }

    private static string? Str(JsonElement e, params string[] path) =>
        Any(e, path) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        Any(e, name) is { } v && (v.ValueKind == JsonValueKind.True
            || v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    private static int Int(JsonElement e, string name) => IntOrNull(e, name) ?? 0;

    private static int? IntOrNull(JsonElement e, string name) =>
        Any(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;

    /// <summary>A date from the resource's properties (the inventory) or its top level.</summary>
    private static DateTimeOffset? Date(JsonElement r, string name) =>
        (Str(r, "properties", name) ?? Str(r, name)) is { } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>An array's items, or an object's values (Microsoft has used both for connection references).</summary>
    private static IEnumerable<JsonElement> Items(JsonElement e, string name) => Any(e, name) switch
    {
        { ValueKind: JsonValueKind.Array } arr => arr.EnumerateArray().ToList(),
        { ValueKind: JsonValueKind.Object } obj => obj.EnumerateObject().Select(p => p.Value).ToList(),
        _ => [],
    };
}
