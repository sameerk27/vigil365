using System.Text.Json;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services.PowerPlatform;

public sealed record PowerPlatformFlag(string Label, string Severity);

public sealed record PowerPlatformEnvironment(
    string Id, string Name, string? Type, string? State, string? Region, bool HasDataverse,
    DateTimeOffset? Created, int Apps, int Flows, string? Problem);

public sealed record PowerPlatformApp(
    string Id, string Name, string EnvironmentId, string EnvironmentName, string? Owner,
    DateTimeOffset? Created, DateTimeOffset? Modified, int SharedUsers, int SharedGroups,
    bool BypassConsent, IReadOnlyList<string> Connectors, IReadOnlyList<PowerPlatformFlag> Flags);

public sealed record PowerPlatformFlow(
    string Id, string Name, string EnvironmentId, string EnvironmentName, string State,
    DateTimeOffset? Created, DateTimeOffset? Modified, IReadOnlyList<string> Connectors,
    IReadOnlyList<PowerPlatformFlag> Flags);

public sealed record PowerPlatformSummary(
    int Environments, int EnvironmentsRead, int EnvironmentsSkipped, int Apps, int Flows,
    int ConsentBypassed, int HighRiskConnector, int WidelyShared, int CustomConnector);

public sealed record PowerPlatformInventoryResult(
    string Status, string? Reason, DateTimeOffset ReadAt,
    IReadOnlyList<PowerPlatformEnvironment> Environments, IReadOnlyList<PowerPlatformApp> Apps,
    IReadOnlyList<PowerPlatformFlow> Flows, PowerPlatformSummary Summary)
{
    /// <summary>Everything read.</summary>
    public const string Ready = "Ready";
    /// <summary>Read, but some environments or pages couldn't be.</summary>
    public const string Partial = "Partial";
    /// <summary>The tenant hasn't given Vigil365 the Power Platform reader role.</summary>
    public const string NotEnabled = "NotEnabled";
    /// <summary>The environment list itself failed for another reason.</summary>
    public const string Error = "Error";
}

/// <summary>
/// The tenant's Power Platform environments, canvas apps and cloud flows, with the
/// settings that make an app or flow risky. Read on demand (the page asks; nothing
/// collects in the background) and cached per tenant for a few minutes.
///
/// Sources, all GET on api.powerplatform.com, api-version 2024-10-01:
///   environmentmanagement/environments                      every environment
///   powerapps/environments/{id}/apps                        apps, as administrator
///   powerautomate/environments/{id}/cloudFlows              flows (Dataverse environments only)
/// The flow response's field names aren't in the REST reference; they follow the
/// .NET SDK's CloudFlow model and are read case-insensitively. Check them on a live
/// tenant (plan section 6).
/// </summary>
public sealed class PowerPlatformInventoryService(
    PowerPlatformApiClient client,
    ITenantContext tenant,
    IMemoryCache cache,
    IOptions<GraphOptions> options,
    ILogger<PowerPlatformInventoryService> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private const int AppPageSize = 250;
    private const int MaxPages = 40;
    private const int Parallelism = 4;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public async Task<PowerPlatformInventoryResult> GetAsync(bool refresh, CancellationToken ct)
    {
        var key = $"power-platform-inventory:{tenant.Current?.ToString() ?? "install"}";
        if (!refresh && cache.TryGetValue(key, out PowerPlatformInventoryResult? cached) && cached is not null) return cached;
        var result = await ReadAsync(ct);
        cache.Set(key, result, CacheFor);
        return result;
    }

    public async Task<PowerPlatformInventoryResult> ReadAsync(CancellationToken ct)
    {
        var o = options.Value;
        PowerPlatformApiClient.Collection envRead;
        try
        {
            envRead = await client.ReadCollectionAsync($"environmentmanagement/environments?api-version={PowerPlatformApiClient.ApiVersion}", MaxPages, ct);
        }
        catch (PowerPlatformApiException ex) when (ex.NotEnabled)
        {
            logger.LogInformation("Power Platform inventory: not enabled for this tenant ({Why}, {Status})", ex.Why, ex.Status);
            return Empty(PowerPlatformInventoryResult.NotEnabled, ex.Why == PowerPlatformApiException.Reason.ApiNotInTenant ? "ApiNotInTenant" : "RoleMissing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Power Platform inventory: the environment list could not be read");
            return Empty(PowerPlatformInventoryResult.Error, ex is PowerPlatformApiException p ? $"Power Platform API said: {p.Message}" : "The Power Platform API request failed; see the server log.");
        }

        var all = envRead.Items.Select(ParseEnvironment).Where(e => e.Id.Length > 0).ToList();
        var toRead = all.Take(Math.Max(1, o.PowerPlatformMaxEnvironments)).ToList();
        var skipped = all.Count - toRead.Count;

        var results = new (PowerPlatformEnvironment Env, List<PowerPlatformApp> Apps, List<PowerPlatformFlow> Flows)[toRead.Count];
        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(toRead.Select(async (env, i) =>
        {
            await gate.WaitAsync(ct);
            try { results[i] = await ReadEnvironmentAsync(env, o, ct); }
            finally { gate.Release(); }
        }));

        var envs = results.Select(r => r.Env).ToList();
        var apps = results.SelectMany(r => r.Apps).ToList();
        var flows = results.SelectMany(r => r.Flows).ToList();
        var partial = !envRead.Complete || skipped > 0 || envs.Any(e => e.Problem is not null);

        return new PowerPlatformInventoryResult(
            partial ? PowerPlatformInventoryResult.Partial : PowerPlatformInventoryResult.Ready,
            null, Now, envs, apps, flows,
            new PowerPlatformSummary(
                Environments: all.Count,
                EnvironmentsRead: envs.Count(e => e.Problem is null),
                EnvironmentsSkipped: skipped,
                Apps: apps.Count,
                Flows: flows.Count,
                ConsentBypassed: apps.Count(a => a.BypassConsent),
                HighRiskConnector: apps.Count(a => a.Flags.Any(IsHighRiskFlag)) + flows.Count(f => f.Flags.Any(IsHighRiskFlag)),
                WidelyShared: apps.Count(a => a.SharedUsers >= o.PowerPlatformWideSharingUsers),
                CustomConnector: apps.Count(a => a.Flags.Any(f => f.Label == CustomConnectorFlag))));
    }

    private async Task<(PowerPlatformEnvironment, List<PowerPlatformApp>, List<PowerPlatformFlow>)> ReadEnvironmentAsync(
        PowerPlatformEnvironment env, GraphOptions o, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(env.Id);
        var problems = new List<string>();
        var apps = new List<PowerPlatformApp>();
        var flows = new List<PowerPlatformFlow>();

        try
        {
            var read = await client.ReadCollectionAsync($"powerapps/environments/{id}/apps?api-version={PowerPlatformApiClient.ApiVersion}&$top={AppPageSize}", MaxPages, ct);
            apps.AddRange(read.Items.Select(a => ParseApp(a, env, o)).Where(a => a.Id.Length > 0));
            if (!read.Complete) problems.Add("only part of the app list could be read");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Power Platform inventory: apps in environment {Environment} could not be read", env.Id);
            problems.Add($"apps couldn't be read ({Short(ex)})");
        }

        // Cloud flows live in Dataverse; an environment without it answers 404.
        if (env.HasDataverse)
        {
            try
            {
                var read = await client.ReadCollectionAsync($"powerautomate/environments/{id}/cloudFlows?api-version={PowerPlatformApiClient.ApiVersion}", MaxPages, ct);
                flows.AddRange(read.Items.Select(f => ParseFlow(f, env, o)).Where(f => f.Id.Length > 0));
                if (!read.Complete) problems.Add("only part of the flow list could be read");
            }
            catch (PowerPlatformApiException ex) when (ex.Status == 404) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Power Platform inventory: flows in environment {Environment} could not be read", env.Id);
                problems.Add($"flows couldn't be read ({Short(ex)})");
            }
        }

        return (env with { Apps = apps.Count, Flows = flows.Count, Problem = problems.Count == 0 ? null : string.Join("; ", problems) }, apps, flows);
    }

    private PowerPlatformInventoryResult Empty(string status, string reason) =>
        new(status, reason, Now, [], [], [], new PowerPlatformSummary(0, 0, 0, 0, 0, 0, 0, 0, 0));

    private static string Short(Exception ex) => ex switch
    {
        PowerPlatformApiException { Status: 401 or 403 } => "access refused",
        PowerPlatformApiException { Status: { } s } => $"error {s}",
        _ => "request failed",
    };

    // ── Parsing (pure; tests call these directly) ───────────────────────────────

    public const string ConsentBypassedFlag = "Consent prompt bypassed";
    public const string CustomConnectorFlag = "Custom connector";
    public const string GatewayFlag = "On-premises data gateway";
    public const string HighRiskPrefix = "High-risk connector: ";
    public const string SharedWithGroupFlag = "Shared with a group";
    public const string SuspendedFlag = "Suspended";

    private static bool IsHighRiskFlag(PowerPlatformFlag f) => f.Label.StartsWith(HighRiskPrefix, StringComparison.Ordinal);

    public static PowerPlatformEnvironment ParseEnvironment(JsonElement e) => new(
        Id: Str(e, "id") ?? Str(e, "name") ?? "",
        Name: Str(e, "displayName") ?? Str(e, "properties", "displayName") ?? Str(e, "id") ?? "",
        Type: Str(e, "type"),
        State: Str(e, "state"),
        Region: Str(e, "azureRegion") ?? Str(e, "geo"),
        HasDataverse: !string.IsNullOrWhiteSpace(Str(e, "dataverseId")),
        Created: Date(e, "createdDateTime"),
        Apps: 0, Flows: 0, Problem: null);

    public static PowerPlatformApp ParseApp(JsonElement a, PowerPlatformEnvironment env, GraphOptions o)
    {
        var p = a.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object ? props : default;
        var connectors = new List<string>();
        var custom = false;
        var gateway = false;
        var connectorBypass = false;
        foreach (var c in Items(p, "connectionReferences"))
        {
            var name = Str(c, "displayName") ?? Tail(Str(c, "id"));
            if (!string.IsNullOrWhiteSpace(name) && !connectors.Contains(name, StringComparer.OrdinalIgnoreCase)) connectors.Add(name);
            custom |= Bool(c, "isCustomApiConnection");
            gateway |= Bool(c, "isOnPremiseConnection");
            connectorBypass |= Bool(c, "bypassConsent");
        }
        var bypass = Bool(p, "bypassConsent") || connectorBypass;
        var users = Int(p, "sharedUsersCount");
        var groups = Int(p, "sharedGroupsCount");

        var flags = new List<PowerPlatformFlag>();
        if (bypass) flags.Add(new(ConsentBypassedFlag, "high"));
        flags.AddRange(HighRisk(connectors, o));
        if (custom) flags.Add(new(CustomConnectorFlag, "medium"));
        if (gateway) flags.Add(new(GatewayFlag, "medium"));
        if (users >= o.PowerPlatformWideSharingUsers) flags.Add(new($"Shared with {users} people", "medium"));
        if (groups > 0) flags.Add(new(SharedWithGroupFlag, "low"));

        return new PowerPlatformApp(
            Id: Str(a, "name") ?? Str(a, "id") ?? "",
            Name: Str(p, "displayName") ?? Str(a, "name") ?? "",
            EnvironmentId: env.Id, EnvironmentName: env.Name,
            Owner: Str(p, "owner", "displayName") ?? Str(p, "owner", "userPrincipalName") ?? Str(p, "owner", "email"),
            Created: Date(p, "createdTime"), Modified: Date(p, "lastModifiedTime"),
            SharedUsers: users, SharedGroups: groups, BypassConsent: bypass,
            Connectors: connectors, Flags: flags);
    }

    public static PowerPlatformFlow ParseFlow(JsonElement f, PowerPlatformEnvironment env, GraphOptions o)
    {
        var connectors = FlowConnectors(Any(f, "definition") ?? Any(f, "clientData") ?? Any(f, "clientdata"));
        var state = FlowState(Any(f, "stateCode") ?? Any(f, "statecode"));
        var flags = HighRisk(connectors, o).ToList();
        if (state == "Suspended") flags.Add(new(SuspendedFlag, "low"));
        return new PowerPlatformFlow(
            Id: Str(f, "workflowId") ?? Str(f, "workflowid") ?? Str(f, "resourceId") ?? "",
            Name: Str(f, "name") ?? Str(f, "displayName") ?? "",
            EnvironmentId: env.Id, EnvironmentName: env.Name, State: state,
            Created: Date(f, "createdOn") ?? Date(f, "createdon"), Modified: Date(f, "modifiedOn") ?? Date(f, "modifiedon"),
            Connectors: connectors, Flags: flags);
    }

    /// <summary>Microsoft's WorkflowStateCode: 0 Draft (off), 1 Published (on), 2 Suspended.</summary>
    internal static string FlowState(JsonElement? v) => v switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i switch { 0 => "Off", 1 => "On", 2 => "Suspended", _ => "Unknown" },
        { ValueKind: JsonValueKind.String } s => s.GetString()?.ToLowerInvariant() switch
        {
            "draft" or "0" => "Off",
            "published" or "activated" or "1" => "On",
            "suspended" or "2" => "Suspended",
            _ => "Unknown",
        },
        _ => "Unknown",
    };

    /// <summary>
    /// Connector names from a flow's definition (the workflow's client data): the keys and
    /// api names under properties.connectionReferences. The definition may arrive as a JSON
    /// string or an object. Anything unexpected yields no connectors rather than an error.
    /// </summary>
    internal static IReadOnlyList<string> FlowConnectors(JsonElement? definition)
    {
        if (definition is not { } d) return [];
        JsonDocument? parsed = null;
        try
        {
            var root = d;
            if (d.ValueKind == JsonValueKind.String)
            {
                var text = d.GetString();
                if (string.IsNullOrWhiteSpace(text)) return [];
                parsed = JsonDocument.Parse(text);
                root = parsed.RootElement;
            }
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object
                || !props.TryGetProperty("connectionReferences", out var refs) || refs.ValueKind != JsonValueKind.Object)
                return [];

            var names = new List<string>();
            foreach (var r in refs.EnumerateObject())
            {
                var name = Str(r.Value, "api", "displayName") ?? Str(r.Value, "displayName") ?? Str(r.Value, "api", "name") ?? r.Name;
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            return names;
        }
        catch (JsonException) { return []; }
        finally { parsed?.Dispose(); }
    }

    /// <summary>The configured high-risk list (case-insensitive; a trailing * is a prefix match), as flags.</summary>
    internal static IEnumerable<PowerPlatformFlag> HighRisk(IEnumerable<string> connectors, GraphOptions o)
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

    // ── JSON helpers: case-insensitive, tolerant of missing or odd values ──────

    private static JsonElement? Any(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static JsonElement? Path(JsonElement e, params string[] path)
    {
        JsonElement? cur = e;
        foreach (var part in path)
        {
            if (cur is not { } c) return null;
            cur = Any(c, part);
        }
        return cur;
    }

    private static string? Str(JsonElement e, params string[] path) =>
        Path(e, path) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        Any(e, name) is { } v && (v.ValueKind == JsonValueKind.True
            || v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    private static int Int(JsonElement e, string name) =>
        Any(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : 0;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>An array's items, or an object's values (Microsoft has used both for connection references).</summary>
    private static IEnumerable<JsonElement> Items(JsonElement e, string name) => Any(e, name) switch
    {
        { ValueKind: JsonValueKind.Array } arr => arr.EnumerateArray().ToList(),
        { ValueKind: JsonValueKind.Object } obj => obj.EnumerateObject().Select(p => p.Value).ToList(),
        _ => [],
    };

    private static string? Tail(string? id) => id is null ? null : id[(id.LastIndexOf('/') + 1)..];
}
