using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class NotificationDigestTests
{
    private static DateTimeOffset At(int hour) => new(2026, 7, 17, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldSendDigest_OnlyAtConfiguredHourWithADigestChannel()
    {
        var cfg = new NotificationSettings { EmailDigest = true, DigestHourUtc = 8 };
        Assert.True(ShouldAt(cfg, 8));
        Assert.False(ShouldAt(cfg, 7));
        Assert.False(ShouldAt(new NotificationSettings { DigestHourUtc = 8 }, 8)); // no digest channel enabled
    }

    [Fact]
    public void ShouldSendDigest_OncePerDay()
    {
        var cfg = new NotificationSettings { TeamsDigest = true, DigestHourUtc = 8, LastDigestAt = At(8) };
        Assert.False(NotificationDigestWorker.ShouldSendDigest(cfg, At(8))); // already sent today
        // Next day at the digest hour → due again.
        Assert.True(NotificationDigestWorker.ShouldSendDigest(cfg, At(8).AddDays(1)));
    }

    [Fact]
    public void PendingForDigest_FiltersBySeverityAndSortsMostSevereFirst()
    {
        var alerts = new[]
        {
            new TriggeredAlert { PolicyName = "low", Severity = "low", TriggeredAt = At(1) },
            new TriggeredAlert { PolicyName = "crit", Severity = "critical", TriggeredAt = At(2) },
            new TriggeredAlert { PolicyName = "med", Severity = "medium", TriggeredAt = At(3) },
        };

        var pending = NotificationDigestWorker.PendingForDigest(alerts, minSeverity: "medium");
        Assert.Equal(2, pending.Count);
        Assert.Equal("crit", pending[0].PolicyName);   // critical ranked first
        Assert.DoesNotContain(pending, a => a.PolicyName == "low");
    }

    private static bool ShouldAt(NotificationSettings cfg, int hour) =>
        NotificationDigestWorker.ShouldSendDigest(cfg, At(hour));
}
