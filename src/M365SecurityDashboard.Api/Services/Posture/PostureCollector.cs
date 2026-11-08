using System.Text.Json;
using static M365SecurityDashboard.Api.Services.Common.JsonRead;

namespace M365SecurityDashboard.Api.Services.Posture;

/// <summary>
/// Reads what licence posture needs from Microsoft Graph for the current tenant. Every
/// read is a GET and stands alone: one that fails (a permission not granted, a workload
/// not licensed) becomes an error on that reading, so its checks say Unknown with the
/// reason while the rest still evaluate.
/// </summary>
public sealed class PostureCollector(GraphApiClient graph, ILogger<PostureCollector> logger)
{
    public async Task<PostureSnapshot> CollectAsync(PostureCatalog catalog, CancellationToken ct)
    {
        // First on its own: it also resolves the tenant's credential, which GraphApiClient
        // caches without a lock, so the reads below share one credential and token cache.
        var organization = await TryAsync("organization", async () =>
        {
            var org = (await graph.GetSinglePageAsync("/v1.0/organization?$select=displayName", ct)).FirstOrDefault();
            return Str(org, "displayName");
        }, null);

        // The rest are independent, and the page waits for all of them: in parallel.
        var skus = TryAsync("subscribedSkus", async () =>
        {
            // A missing page may hold a licence the tenant owns, and its checks would then say
            // "needs another licence". A part read is no answer.
            var read = await graph.ReadCollectionAsync("/v1.0/subscribedSkus", ct);
            if (!read.Complete) throw new PartialReadException("Only some of the tenant's licences could be read; try again later.");
            return Skus(read.Items, catalog);
        }, "Directory.Read.All");
        var conditionalAccess = TryAsync<IReadOnlyList<JsonElement>>("conditional access", async () =>
        {
            var read = await graph.ReadCollectionAsync("/v1.0/identity/conditionalAccess/policies", ct);
            // The missing page may hold the one policy that passes, so a part read is no answer.
            if (!read.Complete) throw new PartialReadException("Only some Conditional Access policies could be read; try again later.");
            return read.Items;
        }, "Policy.Read.All");
        var securityDefaults = TryAsync("security defaults", async () =>
        {
            var policy = (await graph.GetSinglePageAsync("/v1.0/policies/identitySecurityDefaultsEnforcementPolicy", ct)).FirstOrDefault();
            return policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("isEnabled", out var e) && e.ValueKind == JsonValueKind.True;
        }, "Policy.Read.All");
        var mfa = TryAsync("MFA registration", async () =>
        {
            var read = await graph.ReadCollectionAsync("/v1.0/reports/authenticationMethods/userRegistrationDetails?$select=isMfaRegistered,userType", ct);
            if (!read.Complete) throw new PartialReadException("The MFA registration report could only be read in part; try again later.");
            var members = read.Items.Where(u => !(u.TryGetProperty("userType", out var t) && string.Equals(t.GetString(), "guest", StringComparison.OrdinalIgnoreCase))).ToList();
            return (members.Count(u => u.TryGetProperty("isMfaRegistered", out var r) && r.ValueKind == JsonValueKind.True), members.Count);
        }, "AuditLog.Read.All");
        var intune = TryAsync("Intune compliance policies", async () =>
        {
            // Zero policies fails the check, so a short count would understate the tenant.
            var read = await graph.ReadCollectionAsync("/v1.0/deviceManagement/deviceCompliancePolicies?$select=id", ct);
            if (!read.Complete) throw new PartialReadException("Only some Intune compliance policies could be read; try again later.");
            return read.Items.Count;
        }, "DeviceManagementConfiguration.Read.All");
        var sharing = TryAsync("SharePoint sharing", async () =>
        {
            var settings = (await graph.GetSinglePageAsync("/v1.0/admin/sharepoint/settings", ct)).FirstOrDefault();
            return Str(settings, "sharingCapability") ?? "";
        }, "SharePointTenantSettings.Read.All");
        var simulations = TryAsync("attack simulations", async () =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-90);
            var sims = await graph.GetCollectionAsync("/v1.0/security/attackSimulation/simulations?$top=50", ct);
            return sims.Count(x => Date(x, "launchDateTime") is { } d ? d >= since : Date(x, "createdDateTime") is { } c && c >= since);
        }, "AttackSimulation.ReadWrite.All");
        var secureScore = TryAsync("Secure Score", async () =>
        {
            // One page: the latest score is the first row, and following nextLink would page
            // through the score history one day at a time (as GraphCollector reads it).
            var score = (await graph.GetSinglePageAsync("/v1.0/security/secureScores?$top=1", ct)).FirstOrDefault();
            return (Number(score, "currentScore") ?? 0, Number(score, "maxScore") ?? 0);
        }, "SecurityEvents.Read.All");

        await Task.WhenAll(skus, conditionalAccess, securityDefaults, mfa, intune, sharing, simulations, secureScore);

        return new PostureSnapshot
        {
            ReadAt = DateTimeOffset.UtcNow,
            OrganizationName = organization.Value,
            Skus = await skus,
            ConditionalAccess = await conditionalAccess,
            SecurityDefaultsEnabled = await securityDefaults,
            MfaRegistration = await mfa,
            IntuneCompliancePolicies = await intune,
            SharingCapability = await sharing,
            SimulationsLast90Days = await simulations,
            SecureScore = await secureScore,
        };
    }

    private static IReadOnlyList<OwnedSku> Skus(IReadOnlyList<JsonElement> rows, PostureCatalog catalog) => rows
        .Where(r => !(r.TryGetProperty("capabilityStatus", out var c) && c.GetString() is "Suspended" or "LockedOut" or "Deleted"))
        .Select(r =>
        {
            var part = Str(r, "skuPartNumber") ?? "";
            var enabled = r.TryGetProperty("prepaidUnits", out var pu) && pu.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;
            var consumed = r.TryGetProperty("consumedUnits", out var cu) && cu.ValueKind == JsonValueKind.Number ? cu.GetInt32() : 0;
            var plans = r.TryGetProperty("servicePlans", out var sp) && sp.ValueKind == JsonValueKind.Array
                ? sp.EnumerateArray()
                    .Where(x => !(x.TryGetProperty("provisioningStatus", out var st) && string.Equals(st.GetString(), "Disabled", StringComparison.OrdinalIgnoreCase)))
                    .Select(x => Str(x, "servicePlanName") ?? "")
                    .Where(n => n.Length > 0).ToList()
                : [];
            return new OwnedSku(part, catalog.ProductName(part), enabled, consumed, plans);
        })
        .ToList();

    private async Task<Reading<T>> TryAsync<T>(string what, Func<Task<T>> read, string? permission)
    {
        try
        {
            return Reading<T>.Ok(await read());
        }
        catch (PartialReadException ex)
        {
            // Our own sentence, not a Graph error: GraphErrorHint would turn it into "see the server log".
            logger.LogInformation("Posture: {What} could only be read in part", what);
            return Reading<T>.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Posture: {What} couldn't be read", what);
            var reason = GraphErrorHint.DescribeOrNull(ex.Message, permission)
                ?? (GraphErrorHint.GraphMessageOrNull(ex.Message) is { } said ? $"Microsoft Graph said: {said}" : "the request failed; see the server log.");
            return Reading<T>.Failed(reason);
        }
    }

    /// <summary>A read that returned some pages but not all: its reading fails with this message as the reason.</summary>
    private sealed class PartialReadException(string message) : Exception(message);
}
