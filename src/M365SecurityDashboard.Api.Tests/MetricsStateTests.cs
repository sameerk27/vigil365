using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class MetricsStateTests
{
    [Fact]
    public void No_samples_reports_null()
    {
        var s = new MetricsState();
        Assert.Null(s.EvaluationP95());
        Assert.Null(s.LastEvaluationMs);
        Assert.Equal(0, s.EvaluationSampleCount());
    }

    [Fact]
    public void Records_last_and_counts_samples()
    {
        var s = new MetricsState();
        s.RecordEvaluation(10);
        s.RecordEvaluation(25);
        Assert.Equal(25, s.LastEvaluationMs);
        Assert.Equal(2, s.EvaluationSampleCount());
    }

    [Fact]
    public void P95_picks_the_high_percentile()
    {
        var s = new MetricsState();
        for (int i = 1; i <= 100; i++) s.RecordEvaluation(i); // 1..100
        // 95th percentile of 1..100 is 95.
        Assert.Equal(95, s.EvaluationP95());
    }

    [Fact]
    public void Window_is_bounded_to_the_most_recent_samples()
    {
        var s = new MetricsState();
        for (int i = 0; i < 250; i++) s.RecordEvaluation(i);
        // Only the last 100 are kept, so the sample count never exceeds the cap.
        Assert.Equal(100, s.EvaluationSampleCount());
        Assert.Equal(249, s.LastEvaluationMs);
    }
}
