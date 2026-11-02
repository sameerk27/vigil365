using System.Collections.Concurrent;
using System.Text.Json;

namespace M365SecurityDashboard.Api.Services.PowerPlatform;

/// <summary>
/// Names the owners in the Power Platform inventory, which gives object ids only, and
/// tells which have left (no such user) or are disabled. Graph GET with the app's
/// existing Directory.Read.All, 15 ids per request (the most an "in" filter takes).
/// </summary>
public sealed class PowerPlatformOwners(GraphApiClient graph, ILogger<PowerPlatformOwners> logger)
{
    public const int MaxOwners = 600;
    private const int BatchSize = 15;
    private const int Parallelism = 4;

    /// <summary>The owners by lower-case id, or null when Graph couldn't be read (no owner flags then).</summary>
    public async Task<IReadOnlyDictionary<string, PowerPlatformOwner>?> ResolveAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => Guid.TryParse(i, out _)).Select(i => i.ToLowerInvariant()).Distinct().Take(MaxOwners).ToList();
        if (wanted.Count == 0) return new Dictionary<string, PowerPlatformOwner>();

        var found = new ConcurrentDictionary<string, PowerPlatformOwner>();
        try
        {
            await ReadAsync("/v1.0/users", "id,displayName,userPrincipalName,accountEnabled", wanted, found, ct);
            // Flows and agents can be created by a service principal (an automation account or
            // an application user). Those aren't users, but they haven't left either.
            var rest = wanted.Where(i => !found.ContainsKey(i)).ToList();
            if (rest.Count > 0) await ReadAsync("/v1.0/servicePrincipals", "id,displayName,accountEnabled", rest, found, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Includes HttpClient's own timeout (a TaskCanceledException): owner names are
            // optional, so a slow Graph costs the names, not the whole evaluation.
            logger.LogInformation(ex, "Power Platform inventory: owners could not be read from Graph");
            return null;
        }

        // Asked for and not returned: no user or service principal with that id (left, or deleted).
        return wanted.ToDictionary(i => i, i => found.TryGetValue(i, out var o) ? o : new PowerPlatformOwner(i, null, null, null, Found: false));
    }

    private async Task ReadAsync(string path, string select, IReadOnlyList<string> ids,
        ConcurrentDictionary<string, PowerPlatformOwner> found, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(ids.Chunk(BatchSize).Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Ids are GUIDs (checked by the caller), so they can't break out of the filter.
                var filter = string.Join(",", batch.Select(i => $"'{i}'"));
                var items = await graph.GetCollectionAsync($"{path}?$filter=id in ({filter})&$select={select}", ct);
                foreach (var u in items)
                {
                    if (!u.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id) continue;
                    bool? enabled = u.TryGetProperty("accountEnabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? en.ValueKind == JsonValueKind.True : null;
                    found[id.ToLowerInvariant()] = new PowerPlatformOwner(id,
                        u.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                        u.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null,
                        enabled, Found: true);
                }
            }
            finally { gate.Release(); }
        }));
    }
}
