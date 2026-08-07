using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The backtest number is what an analyst uses to pick a threshold. If it
/// over-counts, every policy looks unusably noisy; if it under-counts, a noisy
/// policy looks safe. These lock the episode semantics.
/// </summary>
public class BacktestMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    [Fact]
    public void CountEpisodes_SustainedBreachIsOneEpisode_NotOnePerCycle()
    {
        // The live evaluator keeps one open alert per policy, so a continuous
        // breach must read as a single fire — this is the whole point.
        var events = Enumerable.Range(0, 200).Select(i => T0.AddMinutes(i * 5)).ToList();
        var r = BacktestMath.CountEpisodes(events, T0, T0.AddHours(12), Window, Step, threshold: 3);
        Assert.Equal(1, r.Episodes);
    }

    [Fact]
    public void CountEpisodes_SeparateBurstsAreSeparateEpisodes()
    {
        // Two bursts far enough apart that the window empties between them.
        var events = new List<DateTimeOffset>
        {
            T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(3),
            T0.AddHours(8), T0.AddHours(8).AddMinutes(1), T0.AddHours(8).AddMinutes(2),
        };
        var r = BacktestMath.CountEpisodes(events, T0, T0.AddHours(12), Window, Step, threshold: 3);
        Assert.Equal(2, r.Episodes);
    }

    [Fact]
    public void CountEpisodes_BelowThresholdNeverFires()
    {
        var events = new List<DateTimeOffset> { T0.AddMinutes(1), T0.AddMinutes(2) };
        var r = BacktestMath.CountEpisodes(events, T0, T0.AddHours(6), Window, Step, threshold: 5);
        Assert.Equal(0, r.Episodes);
        Assert.Equal(2, r.MaxValue); // still reports what was actually seen
    }

    [Fact]
    public void CountEpisodes_NoEventsIsZeroNotCrash()
    {
        var r = BacktestMath.CountEpisodes([], T0, T0.AddDays(30), Window, Step, threshold: 1);
        Assert.Equal(0, r.Episodes);
        Assert.Equal(0, r.MaxValue);
        Assert.Empty(r.FiredAt);
    }

    [Fact]
    public void CountEpisodes_ThresholdIsInclusive_MatchingTheEvaluator()
    {
        // Evaluator fires when value >= threshold (it skips when value < threshold).
        var events = new List<DateTimeOffset> { T0.AddMinutes(1), T0.AddMinutes(2) };
        var r = BacktestMath.CountEpisodes(events, T0, T0.AddMinutes(30), Window, Step, threshold: 2);
        Assert.Equal(1, r.Episodes);
    }

    [Fact]
    public void CountEpisodes_ReportsWhenItWouldHaveFired()
    {
        var events = new List<DateTimeOffset> { T0.AddHours(5), T0.AddHours(5).AddMinutes(1) };
        var r = BacktestMath.CountEpisodes(events, T0, T0.AddHours(12), Window, Step, threshold: 2);
        var fired = Assert.Single(r.FiredAt);
        Assert.InRange(fired, T0.AddHours(5), T0.AddHours(6));
    }

    [Fact]
    public void CountEpisodesFromSeries_CountsRisingEdgesOnly()
    {
        var series = new List<(DateTimeOffset, int)>
        {
            (T0, 1), (T0.AddHours(1), 9), (T0.AddHours(2), 9),   // one rise, stays up
            (T0.AddHours(3), 0),                                   // recovers
            (T0.AddHours(4), 7),                                   // rises again
        };
        var r = BacktestMath.CountEpisodesFromSeries(series, threshold: 5);
        Assert.Equal(2, r.Episodes);
        Assert.Equal(9, r.MaxValue);
    }

    [Fact]
    public void CountEpisodesFromSeries_SortsUnorderedInput()
    {
        var series = new List<(DateTimeOffset, int)>
        {
            (T0.AddHours(4), 7), (T0, 1), (T0.AddHours(1), 9), (T0.AddHours(3), 0), (T0.AddHours(2), 9),
        };
        Assert.Equal(2, BacktestMath.CountEpisodesFromSeries(series, threshold: 5).Episodes);
    }

    [Fact]
    public void CountEpisodesFromSeries_EmptyIsZero()
        => Assert.Equal(0, BacktestMath.CountEpisodesFromSeries([], threshold: 1).Episodes);
}
