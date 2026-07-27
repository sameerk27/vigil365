namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The counting maths behind policy backtesting, kept pure so it can be tested
/// without a database.
///
/// The critical subtlety: the live evaluator keeps ONE open alert per policy and
/// only raises a new one after the previous reached a terminal state. So the
/// honest answer to "how often would this have fired" is the number of *episodes*
/// — transitions from below-threshold to at-or-above — not the number of
/// evaluation cycles that were above it. A single sustained 30-day breach is one
/// alert, not 2,880.
/// </summary>
public static class BacktestMath
{
    public sealed record Outcome(int Episodes, int MaxValue, IReadOnlyList<DateTimeOffset> FiredAt);

    /// <summary>Max firing timestamps returned; enough to be useful, bounded for payload size.</summary>
    private const int MaxSamples = 50;

    /// <summary>
    /// Replays a rolling-window count (activity policies) across a time range.
    /// <paramref name="eventTimes"/> must be sorted ascending.
    /// </summary>
    public static Outcome CountEpisodes(
        IReadOnlyList<DateTimeOffset> eventTimes,
        DateTimeOffset from, DateTimeOffset to,
        TimeSpan window, TimeSpan step, int threshold)
    {
        if (step <= TimeSpan.Zero) step = TimeSpan.FromMinutes(15);
        if (threshold < 1) threshold = 1;

        var episodes = 0;
        var maxValue = 0;
        var firedAt = new List<DateTimeOffset>();
        var wasAbove = false;

        for (var t = from; t <= to; t += step)
        {
            var windowStart = t - window;
            // eventTimes is sorted, so the window count is the gap between two
            // binary-search boundaries rather than a rescan per step.
            var lo = LowerBound(eventTimes, windowStart);
            var hi = UpperBound(eventTimes, t);
            var value = hi - lo;

            if (value > maxValue) maxValue = value;

            var isAbove = value >= threshold;
            if (isAbove && !wasAbove)
            {
                episodes++;
                if (firedAt.Count < MaxSamples) firedAt.Add(t);
            }
            wasAbove = isAbove;
        }

        return new Outcome(episodes, maxValue, firedAt);
    }

    /// <summary>
    /// Replays a sampled series (metric policies backed by trend snapshots).
    /// Each point is an observed value at a point in time; episodes are counted
    /// the same rising-edge way.
    /// </summary>
    public static Outcome CountEpisodesFromSeries(
        IReadOnlyList<(DateTimeOffset At, int Value)> series, int threshold)
    {
        if (threshold < 1) threshold = 1;

        var episodes = 0;
        var maxValue = 0;
        var firedAt = new List<DateTimeOffset>();
        var wasAbove = false;

        foreach (var (at, value) in series.OrderBy(p => p.At))
        {
            if (value > maxValue) maxValue = value;
            var isAbove = value >= threshold;
            if (isAbove && !wasAbove)
            {
                episodes++;
                if (firedAt.Count < MaxSamples) firedAt.Add(at);
            }
            wasAbove = isAbove;
        }

        return new Outcome(episodes, maxValue, firedAt);
    }

    /// <summary>First index with value >= target.</summary>
    private static int LowerBound(IReadOnlyList<DateTimeOffset> sorted, DateTimeOffset target)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid] < target) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>First index with value > target.</summary>
    private static int UpperBound(IReadOnlyList<DateTimeOffset> sorted, DateTimeOffset target)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid] <= target) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
