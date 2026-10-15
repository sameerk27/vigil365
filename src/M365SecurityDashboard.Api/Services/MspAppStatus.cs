using System.Reflection;
using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The Graph application permissions Vigil365 requests, read from the embedded
/// graph-permissions.json — the same file the installer embeds and register-app.ps1
/// reads, so the three can never disagree again.
/// </summary>
public static class GraphPermissionList
{
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    public sealed record Permission(string Name, string Feature);

    private static readonly Lazy<(Permission[] Required, Permission[] Optional)> Data = new(Load);

    public static IReadOnlyList<Permission> Required => Data.Value.Required;
    public static IReadOnlyList<Permission> Optional => Data.Value.Optional;

    private static (Permission[], Permission[]) Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vigil365.graph-permissions.json")
            ?? throw new InvalidOperationException("graph-permissions.json is not embedded in the API assembly.");
        using var doc = JsonDocument.Parse(stream);
        Permission[] Read(string key) => doc.RootElement.GetProperty(key).EnumerateArray()
            .Select(p => new Permission(p.GetProperty("name").GetString()!, p.GetProperty("feature").GetString()!))
            .ToArray();
        return (Read("required"), Read("optional"));
    }
}

/// <summary>
/// Is the install's app registration ready to onboard MSP clients? Pure
/// evaluation of the app object and the Graph service principal's app roles, as
/// returned by Graph — kept separate from the HTTP call so it is unit-testable.
/// </summary>
public static class MspAppStatus
{
    public sealed record Result(
        bool Readable,
        bool? MultiTenant,
        bool? ConsentRedirectRegistered,
        IReadOnlyList<string>? MissingPermissions,
        string ExpectedRedirect,
        string? Reason)
    {
        public bool Ready => Readable && MultiTenant == true && ConsentRedirectRegistered == true && MissingPermissions is { Count: 0 };
    }

    public static Result Unreadable(string expectedRedirect, string reason)
        => new(false, null, null, null, expectedRedirect, reason);

    /// <param name="application">Graph <c>application</c> object (signInAudience, web, requiredResourceAccess).</param>
    /// <param name="graphServicePrincipal">Graph's own service principal (appRoles) to map permission names to ids.</param>
    public static Result Evaluate(JsonElement application, JsonElement graphServicePrincipal, IEnumerable<string> requiredPermissions, string expectedRedirect)
    {
        var audience = application.TryGetProperty("signInAudience", out var a) ? a.GetString() : null;
        var multiTenant = audience is "AzureADMultipleOrgs" or "AzureADandPersonalMicrosoftAccount";

        var redirects = application.TryGetProperty("web", out var web) && web.TryGetProperty("redirectUris", out var uris) && uris.ValueKind == JsonValueKind.Array
            ? uris.EnumerateArray().Select(u => (u.GetString() ?? "").TrimEnd('/')).ToList()
            : [];
        var hasRedirect = redirects.Any(r => string.Equals(r, expectedRedirect.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        var roleIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (graphServicePrincipal.TryGetProperty("appRoles", out var roles) && roles.ValueKind == JsonValueKind.Array)
            foreach (var r in roles.EnumerateArray())
                if (r.TryGetProperty("value", out var v) && r.TryGetProperty("id", out var id) && v.GetString() is { } name && id.GetString() is { } rid)
                    roleIdByName[name] = rid;

        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (application.TryGetProperty("requiredResourceAccess", out var rra) && rra.ValueKind == JsonValueKind.Array)
            foreach (var resource in rra.EnumerateArray())
                if (resource.TryGetProperty("resourceAppId", out var appId) && string.Equals(appId.GetString(), GraphPermissionList.GraphAppId, StringComparison.OrdinalIgnoreCase)
                    && resource.TryGetProperty("resourceAccess", out var access) && access.ValueKind == JsonValueKind.Array)
                    foreach (var item in access.EnumerateArray())
                        if (item.TryGetProperty("type", out var t) && t.GetString() == "Role" && item.TryGetProperty("id", out var iid) && iid.GetString() is { } s)
                            requested.Add(s);

        var missing = requiredPermissions
            .Where(name => !roleIdByName.TryGetValue(name, out var rid) || !requested.Contains(rid))
            .ToList();

        return new Result(true, multiTenant, hasRedirect, missing, expectedRedirect, null);
    }
}
