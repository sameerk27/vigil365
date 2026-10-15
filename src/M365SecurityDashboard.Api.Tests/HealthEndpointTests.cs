using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// /health is anonymous and install-wide: its collection is the newest run of any
/// client. It also says how old a run may be before it is stale, so a signed-in
/// view can judge one client's own last run by the same window (ui-5).
/// </summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Collection_reports_the_staleness_window_it_judged_freshness_by()
    {
        await using var h = new EndpointHarness(app => app.MapAuthHealthEndpoints(), graph: g => g.CollectionIntervalMinutes = 15);
        await using (var db = h.Db(TestTenancy.TenantA))
        {
            db.CollectionRuns.Add(new CollectionRun { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), Status = CollectionStatus.Completed });
            await db.SaveChangesAsync();
        }

        var (status, body) = await h.SendAsync("GET", "/health");

        Assert.Equal(200, status);
        var collection = JsonDocument.Parse(body).RootElement.GetProperty("checks").GetProperty("collection");
        Assert.True(collection.GetProperty("fresh").GetBoolean());
        Assert.Equal(30, collection.GetProperty("staleAfterMinutes").GetInt32());
    }
}
