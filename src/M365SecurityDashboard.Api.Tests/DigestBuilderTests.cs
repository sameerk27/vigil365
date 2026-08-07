using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class DigestBuilderTests
{
    [Fact]
    public async Task BuildAsync_NoData_ReportsEmptyButValidBody()
    {
        using var db = TestAppDbContextFactory.Create();
        var digest = await new DigestBuilder(db).BuildAsync(7, CancellationToken.None);

        Assert.False(digest.HasData);
        Assert.Empty(digest.Metrics);
        Assert.Empty(digest.TopAlerts);
        Assert.Contains("Weekly Security Digest", digest.HtmlBody);
        Assert.Contains("No posture snapshots", digest.HtmlBody);
    }

    [Fact]
    public async Task BuildAsync_ComputesWeekOverWeekDeltasAndTopAlerts()
    {
        using var db = TestAppDbContextFactory.Create();
        db.TrendSnapshots.Add(new TrendSnapshot { CapturedAt = DateTimeOffset.UtcNow.AddDays(-8), SecureScorePct = 40, CriticalAlertsCount = 2 });
        db.TrendSnapshots.Add(new TrendSnapshot { CapturedAt = DateTimeOffset.UtcNow, SecureScorePct = 45, CriticalAlertsCount = 5 });
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "Low thing", Severity = "low", Status = "new", TriggeredAt = DateTimeOffset.UtcNow });
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "Critical thing", Severity = "critical", Status = "new", TriggeredAt = DateTimeOffset.UtcNow });
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "Resolved thing", Severity = "high", Status = "resolved", TriggeredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var digest = await new DigestBuilder(db).BuildAsync(7, CancellationToken.None);

        Assert.True(digest.HasData);
        var score = Assert.Single(digest.Metrics, m => m.Label == "Secure Score");
        Assert.Equal(5, score.Delta); // 45 − 40

        // Only open alerts, critical ranked first, resolved excluded.
        Assert.Equal(2, digest.TopAlerts.Count);
        Assert.Equal("critical", digest.TopAlerts[0].Severity);
        Assert.DoesNotContain(digest.TopAlerts, a => a.PolicyName == "Resolved thing");
    }

    [Fact]
    public async Task BuildAsync_ExcludesCurrentlySnoozedAlertsFromTopList()
    {
        using var db = TestAppDbContextFactory.Create();
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "snoozed", Severity = "critical", Status = "new", TriggeredAt = DateTimeOffset.UtcNow, SnoozedUntil = DateTimeOffset.UtcNow.AddHours(4) });
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "active", Severity = "high", Status = "new", TriggeredAt = DateTimeOffset.UtcNow });
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "snooze-expired", Severity = "medium", Status = "new", TriggeredAt = DateTimeOffset.UtcNow, SnoozedUntil = DateTimeOffset.UtcNow.AddHours(-1) });
        await db.SaveChangesAsync();

        var digest = await new DigestBuilder(db).BuildAsync(7, CancellationToken.None);

        Assert.DoesNotContain(digest.TopAlerts, a => a.PolicyName == "snoozed"); // still snoozed → excluded
        Assert.Contains(digest.TopAlerts, a => a.PolicyName == "active");
        Assert.Contains(digest.TopAlerts, a => a.PolicyName == "snooze-expired"); // snooze lapsed → included
    }

    [Fact]
    public async Task BuildAsync_CsvEscapesCommasInFields()
    {
        using var db = TestAppDbContextFactory.Create();
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyName = "Spike, sudden", Severity = "high", Condition = "count > 3", Status = "new", TriggeredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var digest = await new DigestBuilder(db).BuildAsync(7, CancellationToken.None);
        Assert.NotNull(digest.Csv);
        Assert.Contains("\"Spike, sudden\"", digest.Csv);
    }
}
