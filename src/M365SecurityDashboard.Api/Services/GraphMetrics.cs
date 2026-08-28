using System.Threading;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// In-process counters for real Microsoft Graph traffic — every HTTP request the
/// GraphApiClient makes and every 429 (throttle) it receives. Thread-safe.
///
/// These are genuine measured counts, never fabricated. They are cumulative since
/// the service last started, so the metrics endpoint reports them as such; the
/// per-run delta is snapshotted by the collector and persisted on each
/// CollectionRun for accurate per-run and trend reporting.
/// </summary>
public sealed class GraphMetrics
{
    private long _requests;
    private long _throttled;

    public long Requests => Interlocked.Read(ref _requests);
    public long Throttled => Interlocked.Read(ref _throttled);

    public void RecordRequest() => Interlocked.Increment(ref _requests);
    public void RecordThrottle() => Interlocked.Increment(ref _throttled);

    /// <summary>A point-in-time reading, for computing a per-run delta.</summary>
    public (long Requests, long Throttled) Snapshot() => (Requests, Throttled);
}
