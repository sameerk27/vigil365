using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class DataRetentionWorkerTests
{
    private static SecurityAlert Alert(bool resolved, int ageDays) => new()
    {
        ExternalId = Guid.NewGuid().ToString(),
        AlertType = "Test",
        Service = M365ServiceArea.DefenderXdr,
        Severity = AlertSeverity.Medium,
        Title = "t",
        DetectedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        IsResolved = resolved,
    };

    [Fact]
    public async Task PruneAsync_DeletesOnlyOldResolvedAlerts()
    {
        using var db = TestAppDbContextFactory.Create();
        db.SecurityAlerts.Add(Alert(resolved: true, ageDays: 120));   // pruned
        db.SecurityAlerts.Add(Alert(resolved: true, ageDays: 10));    // kept: recent
        db.SecurityAlerts.Add(Alert(resolved: false, ageDays: 400));  // kept: still open
        await db.SaveChangesAsync();

        var summary = await DataRetentionWorker.PruneAsync(
            db, new RetentionOptions { ResolvedAlertsDays = 90 }, CancellationToken.None);

        Assert.Equal(1, summary.ResolvedAlerts);
        Assert.Equal(2, await db.SecurityAlerts.CountAsync());
        Assert.True(await db.SecurityAlerts.AnyAsync(a => !a.IsResolved));
    }

    [Fact]
    public async Task PruneAsync_ZeroDays_DisablesPruning()
    {
        using var db = TestAppDbContextFactory.Create();
        db.SecurityAlerts.Add(Alert(resolved: true, ageDays: 1000));
        db.AuditEntries.Add(new AuditEntry { Timestamp = DateTimeOffset.UtcNow.AddDays(-1000), Action = "x", ActorEmail = "a", TargetType = "t" });
        await db.SaveChangesAsync();

        var summary = await DataRetentionWorker.PruneAsync(
            db, new RetentionOptions { ResolvedAlertsDays = 0, AuditEntriesDays = 0 }, CancellationToken.None);

        Assert.Equal(0, summary.TotalDeleted);
        Assert.Equal(1, await db.SecurityAlerts.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task PruneAsync_KeepsOpenTriggeredAlertsRegardlessOfAge()
    {
        using var db = TestAppDbContextFactory.Create();
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyName = "p", Severity = "high",
            Category = "identity", Condition = "c", Status = "new",
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-500),
        });
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyName = "p2", Severity = "high",
            Category = "identity", Condition = "c", Status = "resolved",
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-500),
        });
        await db.SaveChangesAsync();

        var summary = await DataRetentionWorker.PruneAsync(
            db, new RetentionOptions { TriggeredAlertsDays = 180 }, CancellationToken.None);

        Assert.Equal(1, summary.TriggeredAlerts);
        var remaining = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal("new", remaining.Status);
    }
}
