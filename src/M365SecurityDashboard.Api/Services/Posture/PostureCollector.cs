using System.Text.Json;

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
        var organization = await TryAsync("organization", async () =>
        {
            var org = (await graph.GetCollectionAsync("/v1.0/organization?$select=displayName", ct)).FirstOrDefault();
            return org.ValueKind == JsonValueKind.Object && org.TryGetProperty("displayName", out var n) ? n.GetString() : null;
        }, null);

        return new PostureSnapshot
        {
            ReadAt = DateTimeOffset.UtcNow,
            OrganizationName = organization.Value,
            Skus = await TryAsync("subscribedSkus", async () => Skus(await graph.GetCollectionAsync("/v1.0/subscribedSkus", ct), catalog), "Directory.Read.All"),
            ConditionalAccess = await TryAsync<IReadOnlyList<JsonElement>>("conditional access",
                async () => await graph.GetCollectionAsync("/v1.0/identity/conditionalAccess/policies", ct), "Policy.Read.All"),
            SecurityDefaultsEnabled = await TryAsync("security defaults", async () =>
            {
                var policy = (await graph.GetSinglePageAsync("/v1.0/policies/identitySecurityDefaultsEnforcementPolicy", ct)).FirstOrDefault();
                return policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("isEnabled", out var e) && e.ValueKind == JsonValueKind.True;
            }, "Policy.Read.All"),
            MfaRegistration = await TryAsync("MFA registration", async () =>
            {
                var read = await graph.ReadCollectionAsync("/v1.0/reports/authenticationMethods/userRegistrationDetails?$select=isMfaRegistered,userType", ct);
                if (!read.Complete) throw new InvalidOperationException("The MFA registration report could only be read in part.");
                var members = read.Items.Where(u => !(u.TryGetProperty("userType", out var t) && string.Equals(t.GetString(), "guest", StringComparison.OrdinalIgnoreCase))).ToList();
                return (members.Count(u => u.TryGetProperty("isMfaRegistered", out var r) && r.ValueKind == JsonValueKind.True), members.Count);
            }, "AuditLog.Read.All"),
            IntuneCompliancePolicies = await TryAsync("Intune compliance policies",
                async () => (await graph.GetCollectionAsync("/v1.0/deviceManagement/deviceCompliancePolicies?$select=id", ct)).Count,
                "DeviceManagementConfiguration.Read.All"),
            SharingCapability = await TryAsync("SharePoint sharing", async () =>
            {
                var settings = (await graph.GetSinglePageAsync("/v1.0/admin/sharepoint/settings", ct)).FirstOrDefault();
                return settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("sharingCapability", out var c) ? c.GetString() ?? "" : "";
            }, "SharePointTenantSettings.Read.All"),
            SimulationsLast90Days = await TryAsync("attack simulations", async () =>
            {
                var since = DateTimeOffset.UtcNow.AddDays(-90);
                var sims = await graph.GetCollectionAsync("/v1.0/security/attackSimulation/simulations?$top=50", ct);
                return sims.Count(x => Date(x, "launchDateTime") is { } d ? d >= since : Date(x, "createdDateTime") is { } c && c >= since);
            }, "AttackSimulation.ReadWrite.All"),
            SecureScore = await TryAsync("Secure Score", async () =>
            {
                var score = (await graph.GetCollectionAsync("/v1.0/security/secureScores?$top=1", ct)).FirstOrDefault();
                return (Number(score, "currentScore"), Number(score, "maxScore"));
            }, "SecurityEvents.Read.All"),
        };
    }

    private static IReadOnlyList<OwnedSku> Skus(IReadOnlyList<JsonElement> rows, PostureCatalog catalog) => rows
        .Where(r => !(r.TryGetProperty("capabilityStatus", out var c) && c.GetString() is "Suspended" or "LockedOut" or "Deleted"))
        .Select(r =>
        {
            var part = r.TryGetProperty("skuPartNumber", out var p) ? p.GetString() ?? "" : "";
            var enabled = r.TryGetProperty("prepaidUnits", out var pu) && pu.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;
            var consumed = r.TryGetProperty("consumedUnits", out var cu) && cu.ValueKind == JsonValueKind.Number ? cu.GetInt32() : 0;
            var plans = r.TryGetProperty("servicePlans", out var sp) && sp.ValueKind == JsonValueKind.Array
                ? sp.EnumerateArray()
                    .Where(x => !(x.TryGetProperty("provisioningStatus", out var st) && string.Equals(st.GetString(), "Disabled", StringComparison.OrdinalIgnoreCase)))
                    .Select(x => x.TryGetProperty("servicePlanName", out var n) ? n.GetString() ?? "" : "")
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Posture: {What} couldn't be read", what);
            var reason = GraphErrorHint.DescribeOrNull(ex.Message, permission)
                ?? (GraphErrorHint.GraphMessageOrNull(ex.Message) is { } said ? $"Microsoft Graph said: {said}" : "the request failed; see the server log.");
            return Reading<T>.Failed(reason);
        }
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static double Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
