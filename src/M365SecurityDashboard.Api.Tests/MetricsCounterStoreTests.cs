using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The install-wide counters row. The no-lost-increment guarantee under parallel
/// clients needs real SQL (Relational/RelationalParityTests); this covers the
/// startup seed and the fallback path the in-memory provider takes.
/// </summary>
public sealed class MetricsCounterStoreTests
{
    [Fact]
    public async Task The_row_is_seeded_once_and_increments_accumulate()
    {
        using var db = TestAppDbContextFactory.Create();
        MetricsCounterStore.EnsureRow(db);
        MetricsCounterStore.EnsureRow(db); // startup runs every time the service starts

        await MetricsCounterStore.AddAsync(db, graphRequests: 5, graphThrottled: 1, evaluations: 0, CancellationToken.None);
        await MetricsCounterStore.AddAsync(db, graphRequests: 0, graphThrottled: 0, evaluations: 1, CancellationToken.None);
        await db.SaveChangesAsync();

        var row = await db.MetricsCounters.AsNoTracking().SingleAsync();
        Assert.Equal((1, 5L, 1L, 1L), (row.Id, row.GraphRequestsTotal, row.GraphThrottledTotal, row.EvaluationsTotal));
        Assert.Null(row.TenantId); // install-wide
    }
}
