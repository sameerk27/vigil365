using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Adds to the one install-wide <see cref="MetricsCounters"/> row. Clients
/// collect and evaluate in parallel, each in its own context, so a
/// read-increment-write would lose updates and two first runs would both insert
/// Id 1. On a relational database the increment is one atomic UPDATE against the
/// row seeded at startup (<see cref="EnsureRow"/>).
/// </summary>
public static class MetricsCounterStore
{
    /// <summary>Startup: create the row before any client collects.</summary>
    public static void EnsureRow(AppDbContext db)
    {
        if (db.MetricsCounters.Any(c => c.Id == 1)) return;
        db.MetricsCounters.Add(new MetricsCounters { Id = 1 });
        db.SaveChanges();
    }

    /// <summary>
    /// Relational: applied at once, atomically. Otherwise (the in-memory test
    /// provider), or if the row is somehow missing, the tracked row is changed
    /// and the caller's SaveChanges persists it.
    /// </summary>
    public static async Task AddAsync(AppDbContext db, long graphRequests, long graphThrottled, long evaluations, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var updated = await db.MetricsCounters.Where(c => c.Id == 1).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.GraphRequestsTotal, c => c.GraphRequestsTotal + graphRequests)
                .SetProperty(c => c.GraphThrottledTotal, c => c.GraphThrottledTotal + graphThrottled)
                .SetProperty(c => c.EvaluationsTotal, c => c.EvaluationsTotal + evaluations), ct);
            if (updated > 0) return;
        }

        var counters = await db.MetricsCounters.FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (counters is null) { counters = new MetricsCounters { Id = 1 }; db.MetricsCounters.Add(counters); }
        counters.GraphRequestsTotal += graphRequests;
        counters.GraphThrottledTotal += graphThrottled;
        counters.EvaluationsTotal += evaluations;
    }
}
