using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// MSP review F7: one dead client must show on the health probe, and the probe is
/// anonymous, so it reports a count and never the client's name.
/// </summary>
public sealed class HealthClientsTests
{
    [Fact]
    public async Task A_failing_client_is_counted_and_its_name_never_appears()
    {
        await using var h = new EndpointHarness(app => app.MapAuthHealthEndpoints(), graph: g => g.CollectionIntervalMinutes = 15);
        await using (var db = h.Db(TestTenancy.TenantA))
        {
            db.ClientTenants.Add(new ClientTenant
            {
                Id = TestTenancy.TenantA, Name = "Secret client", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow, LastCollectionStatus = "Failed",
            });
            await db.SaveChangesAsync();
        }

        var (status, body) = await h.SendAsync("GET", "/health");

        Assert.Equal(200, status);
        Assert.DoesNotContain("Secret client", body);
        var clients = JsonDocument.Parse(body).RootElement.GetProperty("checks").GetProperty("clients");
        Assert.Equal(1, clients.GetProperty("failing").GetInt32());
    }
}
