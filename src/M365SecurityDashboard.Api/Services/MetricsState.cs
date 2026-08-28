namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// In-process record of real policy-evaluation timings. The AlertEvaluator times
/// each run and reports it here; the metrics endpoint derives the last value and a
/// p95 over a rolling window of recent evaluations. Measured, never fabricated;
/// resets on service restart.
/// </summary>
public sealed class MetricsState
{
    private const int MaxSamples = 100;
    private readonly object _lock = new();
    private readonly Queue<int> _evalMs = new();

    public int? LastEvaluationMs { get; private set; }

    public void RecordEvaluation(int ms)
    {
        lock (_lock)
        {
            LastEvaluationMs = ms;
            _evalMs.Enqueue(ms);
            while (_evalMs.Count > MaxSamples) _evalMs.Dequeue();
        }
    }

    /// <summary>95th-percentile evaluation time over the rolling window, or null if no data.</summary>
    public int? EvaluationP95()
    {
        lock (_lock)
        {
            if (_evalMs.Count == 0) return null;
            var sorted = _evalMs.OrderBy(x => x).ToArray();
            int idx = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }
    }

    public int EvaluationSampleCount()
    {
        lock (_lock) { return _evalMs.Count; }
    }
}
