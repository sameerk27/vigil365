using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class AlertMetricsTests
{
    private static TriggeredAlert Alert(
        string status, DateTimeOffset triggered,
        DateTimeOffset? ack = null, DateTimeOffset? resolved = null, string? assignee = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            TriggeredAt = triggered,
            AcknowledgedAt = ack,
            ResolvedAt = resolved,
            AssignedTo = assignee,
        };

    [Fact]
    public void Compute_Empty_IsAllZeroNoNaN()
    {
        var r = AlertMetrics.Compute([]);
        Assert.Equal(0, r.Total);
        Assert.Equal(0, r.ResolutionRatePct);
        Assert.Null(r.MttaMinutes);   // no samples -> null, never NaN
        Assert.Null(r.MttrMinutes);
        Assert.Empty(r.ByAssignee);
    }

    [Fact]
    public void Compute_MttaAndMttr_AverageOnlyRealSamples()
    {
        var t = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var alerts = new[]
        {
            // acked after 10 min, resolved after 30 min
            Alert("resolved", t, ack: t.AddMinutes(10), resolved: t.AddMinutes(30)),
            // acked after 20 min, resolved after 50 min
            Alert("resolved", t, ack: t.AddMinutes(20), resolved: t.AddMinutes(50)),
            // still open, never acked -> excluded from both averages
            Alert("new", t),
        };
        var r = AlertMetrics.Compute(alerts);
        Assert.Equal(15, r.MttaMinutes);  // (10+20)/2
        Assert.Equal(40, r.MttrMinutes);  // (30+50)/2
    }

    [Fact]
    public void Compute_ResolutionRate_CountsManualAndAuto()
    {
        var t = DateTimeOffset.UtcNow.AddHours(-1);
        var alerts = new[]
        {
            Alert("resolved", t, resolved: t.AddMinutes(5)),
            Alert("auto_resolved", t, resolved: t.AddMinutes(5)),
            Alert("new", t),
            Alert("acknowledged", t, ack: t.AddMinutes(1)),
        };
        var r = AlertMetrics.Compute(alerts);
        Assert.Equal(4, r.Total);
        Assert.Equal(1, r.Resolved);
        Assert.Equal(1, r.AutoResolved);
        Assert.Equal(2, r.Open);
        Assert.Equal(50.0, r.ResolutionRatePct); // 2 of 4
    }

    [Fact]
    public void Compute_IgnoresNegativeDurations()
    {
        // A resolve timestamp before the trigger (clock skew / bad data) must not
        // pull the average negative.
        var t = DateTimeOffset.UtcNow;
        var r = AlertMetrics.Compute([Alert("resolved", t, resolved: t.AddMinutes(-10))]);
        Assert.Null(r.MttrMinutes);
    }

    [Fact]
    public void Compute_ByAssignee_SplitsOpenVsResolved_AutoResolvedHasNoAssigneeLoad()
    {
        var t = DateTimeOffset.UtcNow.AddHours(-2);
        var alerts = new[]
        {
            Alert("new", t, assignee: "ana@x.com"),
            Alert("acknowledged", t, ack: t.AddMinutes(2), assignee: "ana@x.com"),
            Alert("resolved", t, resolved: t.AddMinutes(9), assignee: "ana@x.com"),
            Alert("auto_resolved", t, resolved: t.AddMinutes(9)), // unassigned
        };
        var r = AlertMetrics.Compute(alerts);
        var ana = Assert.Single(r.ByAssignee);
        Assert.Equal("ana@x.com", ana.Assignee);
        Assert.Equal(2, ana.Open);          // new + acknowledged
        Assert.Equal(1, ana.Acknowledged);  // the acknowledged-and-open one
        Assert.Equal(1, ana.Resolved);
    }
}
