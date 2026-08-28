using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

public sealed record PromRow(string Metric, string Value, string Meaning);

public sealed record SystemMetrics(
    double CollectorUptimePct,
    int RunsWindow,
    int? GraphCallsLastRun,
    long GraphCallsTotal,
    long GraphThrottledTotal,
    int? EvalP95Ms,
    int? EvalLastMs,
    int EvalSamples,
    long? DbSizeBytes,
    int ActiveAlerts,
    int PoliciesEnabled,
    int RetentionDays,
    int? LastRunDurationMs,
    IReadOnlyList<int> ThrottleTrend,
    IReadOnlyList<PromRow> Prometheus);

/// <summary>
/// Gathers the real system/operational metrics shown on the Metrics tab. Every
/// value is measured or queried — collection-run history, in-process Graph and
/// evaluation counters, and a live database-size query. Nothing is fabricated;
/// counters that reset on restart are labelled as such.
/// </summary>
public sealed class MetricsService(
    AppDbContext db,
    GraphMetrics graphMetrics,
    MetricsState metricsState,
    IOptions<RetentionOptions> retention,
    ILogger<MetricsService> logger)
{
    public async Task<SystemMetrics> GatherAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var windowRuns = await db.CollectionRuns.AsNoTracking()
            .Where(r => r.StartedAt >= since && r.CompletedAt != null)
            .Select(r => new { r.Status })
            .ToListAsync(ct);
        int evaluated = windowRuns.Count;
        int completed = windowRuns.Count(r => r.Status == CollectionStatus.Completed);
        double uptime = evaluated > 0 ? Math.Round(completed * 100.0 / evaluated, 1) : 0;

        var recent = await db.CollectionRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt).Take(14)
            .Select(r => new { r.StartedAt, r.CompletedAt, r.GraphRequestCount, r.GraphThrottleCount })
            .ToListAsync(ct);
        var latest = recent.FirstOrDefault();
        int? lastDurationMs = latest?.CompletedAt is { } done
            ? (int)Math.Max(0, (done - latest.StartedAt).TotalMilliseconds)
            : null;
        // Oldest-first so a sparkline reads left-to-right through time.
        var throttleTrend = recent.AsEnumerable().Reverse().Select(r => r.GraphThrottleCount).ToList();

        int runsTotal = await db.CollectionRuns.AsNoTracking().CountAsync(ct);
        int failuresTotal = await db.CollectionRuns.AsNoTracking().CountAsync(r => r.Status == CollectionStatus.Failed, ct);
        int activeAlerts = await db.TriggeredAlerts.AsNoTracking()
            .CountAsync(a => a.Status != "resolved" && a.Status != "auto_resolved", ct);
        int policiesEnabled = await db.AlertPolicies.AsNoTracking().CountAsync(p => p.Enabled, ct);

        long graphReq = graphMetrics.Requests;
        long graphThrottled = graphMetrics.Throttled;
        int? evalP95 = metricsState.EvaluationP95();
        int? evalLast = metricsState.LastEvaluationMs;
        long? dbBytes = await QueryDatabaseSizeBytesAsync(ct);

        var prometheus = new List<PromRow>
        {
            new("vigil365_collection_runs_total", runsTotal.ToString(), "Collection runs recorded"),
            new("vigil365_collection_failures_total", failuresTotal.ToString(), "Runs that ended failed"),
            new("vigil365_collection_duration_seconds", lastDurationMs is { } ms ? (ms / 1000.0).ToString("0.0") : "—", "Duration of the last run"),
            new("vigil365_graph_requests_total", graphReq.ToString(), "Graph API calls since service start"),
            new("vigil365_graph_throttled_total", graphThrottled.ToString(), "Graph 429 responses since service start"),
            new("vigil365_alerts_active", activeAlerts.ToString(), "Triggered alerts currently unresolved"),
            new("vigil365_policies_enabled", policiesEnabled.ToString(), "Alert policies enabled"),
            new("vigil365_eval_latency_ms", evalLast?.ToString() ?? "—", "Last policy-evaluation duration"),
        };

        return new SystemMetrics(
            CollectorUptimePct: uptime,
            RunsWindow: evaluated,
            GraphCallsLastRun: latest?.GraphRequestCount,
            GraphCallsTotal: graphReq,
            GraphThrottledTotal: graphThrottled,
            EvalP95Ms: evalP95,
            EvalLastMs: evalLast,
            EvalSamples: metricsState.EvaluationSampleCount(),
            DbSizeBytes: dbBytes,
            ActiveAlerts: activeAlerts,
            PoliciesEnabled: policiesEnabled,
            RetentionDays: retention.Value.TriggeredAlertsDays,
            LastRunDurationMs: lastDurationMs,
            ThrottleTrend: throttleTrend,
            Prometheus: prometheus);
    }

    /// <summary>Live database size in bytes from SQL Server system views. Null if unavailable.</summary>
    private async Task<long?> QueryDatabaseSizeBytesAsync(CancellationToken ct)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // size is in 8-KB pages; data + log files for the current database.
            cmd.CommandText = "SELECT CAST(ISNULL(SUM(CAST(size AS bigint)), 0) * 8 * 1024 AS bigint) FROM sys.database_files WHERE type IN (0, 1);";
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? l : result is not null && long.TryParse(result.ToString(), out var p) ? p : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Database size query failed — reporting size as unavailable");
            return null;
        }
    }
}
