namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Durable cumulative metric counters that must survive a service restart —
/// unlike the in-process <c>GraphMetrics</c> counters, which reset. Each
/// collection run adds its real per-run Graph delta here, and each evaluation
/// increments the evaluation count, so the "_total" metrics keep climbing across
/// restarts. Singleton row (Id = 1). Every value is a measured accumulation,
/// never fabricated.
/// </summary>
public sealed class MetricsCounters
{
    public int Id { get; set; } = 1;

    /// <summary>Total Graph API requests across all collection runs, all-time.</summary>
    public long GraphRequestsTotal { get; set; }

    /// <summary>Total Graph 429 (throttle) responses across all collection runs, all-time.</summary>
    public long GraphThrottledTotal { get; set; }

    /// <summary>Total policy evaluations run, all-time.</summary>
    public long EvaluationsTotal { get; set; }
}
