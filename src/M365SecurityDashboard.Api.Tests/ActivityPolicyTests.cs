using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Activity-based policies: fire when matching tenant audit events occur within
/// the sliding window; wildcard patterns; events outside the window are ignored;
/// affected entities carry the actor/target and serialize camelCase for the UI.
/// </summary>
public class ActivityPolicyTests
{
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

    private static AlertPolicy ActivityPolicy(string pattern, int threshold = 1, int windowMinutes = 60) => new()
    {
        Id = Guid.NewGuid(), Name = $"Watch: {pattern}", Enabled = true, Kind = "activity",
        Category = "identity", ActivityPattern = pattern, WindowMinutes = windowMinutes,
        Threshold = threshold, Severity = "high", Condition = $"Activity \"{pattern}\" ≥ {threshold} in {windowMinutes}m",
        SuppressionMinutes = 60, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private static AlertPolicy AnomalyPolicy(string metric, int threshold = 10, double multiplier = 3, int baselineDays = 30) => new()
    {
        Id = Guid.NewGuid(), Name = $"Spike: {metric}", Enabled = true, Kind = "anomaly",
        Category = "identity", Metric = metric, Threshold = threshold,
        BaselineMultiplier = multiplier, BaselineDays = baselineDays,
        Severity = "high", Condition = $"{metric} ≥ {threshold} and ≥ {multiplier}× {baselineDays}d baseline",
        SuppressionMinutes = 60, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private static void AddEvent(AppDbContext db, string activity, DateTimeOffset when, string? actor = "admin@contoso.com", string? target = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ExternalId = Guid.NewGuid().ToString(), Source = "directoryAudit",
            Activity = activity, ActorUpn = actor, TargetName = target,
            Result = "success", OccurredAt = when, CollectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static void AddTrend(AppDbContext db, DateTimeOffset capturedAt, int failedSignInProxy)
    {
        db.TrendSnapshots.Add(new TrendSnapshot
        {
            CapturedAt = capturedAt,
            HighAlertsCount = failedSignInProxy,
            CriticalAlertsCount = failedSignInProxy,
            RiskyUsersCount = failedSignInProxy,
            NonCompliantDevicesCount = failedSignInProxy,
            ComplianceIssuesCount = failedSignInProxy,
            MfaCoveragePct = 95,
            SecureScorePct = 70,
        });
    }

    [Fact]
    public async Task Fires_WhenMatchingEventInWindow()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(ActivityPolicy("Add member to role"));
        AddEvent(db, "Add member to role", DateTimeOffset.UtcNow.AddMinutes(-5), target: "Global Administrator");
        await db.SaveChangesAsync();

        var fired = await BuildEvaluator(db).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, fired);
        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal(1, alert.MetricValue);
        // camelCase entities — actor + activity→target title reach the UI.
        Assert.Contains("\"userPrincipalName\":\"admin@contoso.com\"", alert.AffectedEntities);
        Assert.Contains("Global Administrator", alert.AffectedEntities);
    }

    [Fact]
    public async Task Ignores_EventsOutsideWindow()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(ActivityPolicy("Add member to role", windowMinutes: 60));
        AddEvent(db, "Add member to role", DateTimeOffset.UtcNow.AddHours(-3)); // stale
        await db.SaveChangesAsync();

        var fired = await BuildEvaluator(db).EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, fired);
        Assert.Empty(await db.TriggeredAlerts.ToListAsync());
    }

    [Fact]
    public async Task WildcardPattern_MatchesVariants()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(ActivityPolicy("*conditional access policy", threshold: 2));
        AddEvent(db, "Add conditional access policy", DateTimeOffset.UtcNow.AddMinutes(-10));
        AddEvent(db, "Update conditional access policy", DateTimeOffset.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var fired = await BuildEvaluator(db).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, fired);
        Assert.Equal(2, (await db.TriggeredAlerts.SingleAsync()).MetricValue);
    }

    [Fact]
    public async Task OpenActivityAlert_UpdatesInPlace_AndAutoResolvesWhenQuiet()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(ActivityPolicy("Consent to application"));
        AddEvent(db, "Consent to application", DateTimeOffset.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();
        var evaluator = BuildEvaluator(db);

        Assert.Equal(1, await evaluator.EvaluateAsync(CancellationToken.None));
        AddEvent(db, "Consent to application", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(0, await evaluator.EvaluateAsync(CancellationToken.None)); // updated in place
        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal(2, alert.MetricValue);

        // Events age out of the window → value drops below threshold → the
        // debounce (2 cycles) auto-resolves the alert.
        foreach (var e in db.AuditEvents) e.OccurredAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();
        await evaluator.EvaluateAsync(CancellationToken.None);
        await evaluator.EvaluateAsync(CancellationToken.None);
        Assert.Equal("auto_resolved", (await db.TriggeredAlerts.SingleAsync()).Status);
    }

    [Fact]
    public async Task AnomalyPolicy_Fires_WhenLatestTrendSpikesAboveBaselineAndFloor()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(AnomalyPolicy("highAlertCount", threshold: 10, multiplier: 3, baselineDays: 30));
        var now = DateTimeOffset.UtcNow;
        AddTrend(db, now.AddDays(-10), 4);
        AddTrend(db, now.AddDays(-8), 5);
        AddTrend(db, now.AddDays(-6), 3);
        AddTrend(db, now.AddMinutes(-5), 20);
        await db.SaveChangesAsync();

        var fired = await BuildEvaluator(db).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, fired);
        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal(20, alert.MetricValue);
        Assert.Contains("highAlertCount", alert.AffectedEntities);
        Assert.Contains("baselineAverage", alert.AffectedEntities);
    }

    [Fact]
    public async Task AnomalyPolicy_DoesNotFire_WhenLatestDoesNotClearBaselineMultiplier()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(AnomalyPolicy("highAlertCount", threshold: 10, multiplier: 3, baselineDays: 30));
        var now = DateTimeOffset.UtcNow;
        AddTrend(db, now.AddDays(-10), 8);
        AddTrend(db, now.AddDays(-8), 9);
        AddTrend(db, now.AddDays(-6), 10);
        AddTrend(db, now.AddMinutes(-5), 20);
        await db.SaveChangesAsync();

        var fired = await BuildEvaluator(db).EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, fired);
        Assert.Empty(await db.TriggeredAlerts.ToListAsync());
    }
}
