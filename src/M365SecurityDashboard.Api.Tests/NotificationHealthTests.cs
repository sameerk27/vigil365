using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class NotificationHealthTests
{
    private static NotificationLog Log(string channel, bool ok, int minutesAgo, string? error = null) => new()
    {
        Channel = channel, Success = ok, Error = error,
        SentAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void Compute_CountsConsecutiveFailuresSinceLastSuccess()
    {
        var logs = new[]
        {
            Log("webhook", ok: false, minutesAgo: 1, error: "500"),
            Log("webhook", ok: false, minutesAgo: 2),
            Log("webhook", ok: true, minutesAgo: 3),   // stops the streak
            Log("webhook", ok: false, minutesAgo: 4),
        };

        var health = Assert.Single(NotificationHealth.Compute(logs));
        Assert.Equal("webhook", health.Channel);
        Assert.Equal(2, health.ConsecutiveFailures);
        Assert.False(health.Healthy);
        Assert.Equal("500", health.LastError);
        Assert.NotNull(health.LastSuccessAt);
    }

    [Fact]
    public void Compute_RecentSuccessMeansHealthy()
    {
        var logs = new[] { Log("teams", ok: true, minutesAgo: 1), Log("teams", ok: false, minutesAgo: 2) };
        var health = Assert.Single(NotificationHealth.Compute(logs));
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.True(health.Healthy);
        Assert.Null(health.LastError);
    }

    [Fact]
    public void FailingChannels_RespectsThreshold()
    {
        var logs = new[]
        {
            Log("email", ok: false, minutesAgo: 1),
            Log("email", ok: false, minutesAgo: 2),
            Log("teams", ok: false, minutesAgo: 1),
        };

        Assert.Single(NotificationHealth.FailingChannels(logs, threshold: 2));   // only email
        Assert.Equal(2, NotificationHealth.FailingChannels(logs, threshold: 1).Count); // both
        Assert.Empty(NotificationHealth.FailingChannels(logs, threshold: 3));
    }
}
