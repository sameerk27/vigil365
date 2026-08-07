using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The one-open-alert-per-policy contract: while a policy stays breached the
/// existing open alert is updated in place; duplicates from the old
/// fire-every-cycle behaviour are collapsed; a new row only appears after the
/// previous alert reached a terminal state.
/// </summary>
public class AlertEvaluatorSingleOpenAlertTests
{
    private const string Metric = "riskyUsersCount";

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static AlertEvaluator BuildEvaluator(AppDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AlertingOptions { AutoResolveDebounceCycles = 2 });
        var sender = new NotificationSender(
            new NullHttpClientFactory(),
            new SecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
            NullLogger<NotificationSender>.Instance);
        return new AlertEvaluator(db, sender, options, NullLogger<AlertEvaluator>.Instance);
    }

    private static AlertPolicy Policy() => new()
    {
        Id = Guid.NewGuid(), Name = "Risky Users", Enabled = true, Category = "identity",
        Metric = Metric, Threshold = 1, Severity = "high", Condition = "Risky users ≥ 1",
        SuppressionMinutes = 60, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private static void SeedRiskyUsers(AppDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
            db.SecurityAlerts.Add(new SecurityAlert
            {
                AlertType = "RiskyUser", Severity = AlertSeverity.High, Service = M365ServiceArea.EntraId,
                Title = $"risky-{i}", DetectedAt = DateTimeOffset.UtcNow, IsResolved = false,
            });
        db.SaveChanges();
    }

    [Fact]
    public async Task SecondEvaluation_UpdatesOpenAlertInPlace_NoNewRow()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(Policy());
        SeedRiskyUsers(db, 2);
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);

        var fired1 = await evaluator.EvaluateAsync(CancellationToken.None);
        Assert.Equal(1, fired1);

        SeedRiskyUsers(db, 1); // metric rises to 3 while still breached
        var fired2 = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, fired2); // no new row
        var alerts = await db.TriggeredAlerts.ToListAsync();
        Assert.Single(alerts);
        Assert.Equal(3, alerts[0].MetricValue); // updated in place
        Assert.NotNull(alerts[0].LastEvaluatedAt);
    }

    [Fact]
    public async Task LegacyDuplicates_AreCollapsedToOne()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = Policy();
        db.AlertPolicies.Add(policy);
        SeedRiskyUsers(db, 2);
        // Three legacy open rows for the same policy (old behaviour).
        for (var i = 0; i < 3; i++)
            db.TriggeredAlerts.Add(new TriggeredAlert
            {
                Id = Guid.NewGuid(), PolicyId = policy.Id, PolicyName = policy.Name,
                Severity = "high", Category = "identity", Condition = policy.Condition,
                MetricValue = 2, Threshold = 1, Status = "new",
                TriggeredAt = DateTimeOffset.UtcNow.AddHours(-i - 1),
            });
        await db.SaveChangesAsync();

        await BuildEvaluator(db).EvaluateAsync(CancellationToken.None);

        var open = await db.TriggeredAlerts.Where(t => t.Status == "new").ToListAsync();
        Assert.Single(open); // newest kept
        var retired = await db.TriggeredAlerts.CountAsync(t => t.Status == "auto_resolved");
        Assert.Equal(2, retired);
    }

    [Fact]
    public async Task AfterResolution_StillBreached_FiresFreshAlert()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(Policy());
        SeedRiskyUsers(db, 2);
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);

        await evaluator.EvaluateAsync(CancellationToken.None);
        var first = await db.TriggeredAlerts.SingleAsync();
        first.Status = "resolved";
        await db.SaveChangesAsync();

        var fired = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, fired); // breach persists after resolution → new alert
        Assert.Equal(2, await db.TriggeredAlerts.CountAsync());
    }
}
