using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>The Admin client list counts open alerts the way the rollup and the queue do.</summary>
public sealed class TenantListEndpointTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA;

    [Fact]
    public async Task Open_alerts_are_new_or_acknowledged_never_auto_resolved()
    {
        await using var h = new EndpointHarness(app => app.MapTenantEndpoints());
        await using (var db = h.Db())
        {
            db.ClientTenants.Add(new ClientTenant { Id = Contoso, Name = "Contoso", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var db = h.Db(Contoso))
        {
            foreach (var alertStatus in new[] { "new", "acknowledged", "auto_resolved", "auto_resolved", "resolved" })
                db.TriggeredAlerts.Add(new TriggeredAlert
                {
                    Id = Guid.NewGuid(), PolicyName = "p", Severity = "high", Category = "identity", Condition = "x",
                    TriggeredAt = DateTimeOffset.UtcNow, Status = alertStatus,
                });
            await db.SaveChangesAsync();
        }

        var (status, body) = await h.SendAsync("GET", "/api/tenants/");

        Assert.Equal(200, status);
        var row = JsonDocument.Parse(body).RootElement.EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("openAlerts").GetInt32());
    }
}
