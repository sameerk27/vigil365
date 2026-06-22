using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Focused tests for the auto-resolve path in <see cref="AlertEvaluator"/>.
/// Covers streak increment / reset, transition to <c>auto_resolved</c>,
/// terminal-state skip, debounce-of-one, no notification dispatch on
/// resolution, and preservation of user-set fields.
/// </summary>
public class AlertEvaluatorAutoResolveTests
{
    private const string RiskyUsersMetric = "riskyUsersCount";
    private const int Threshold = 3;

    private static AlertEvaluator BuildEvaluator(AppDbContext db, int autoResolveDebounceCycles = 2)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AlertingOptions
        {
            AutoResolveDebounceCycles = autoResolveDebounceCycles,
        });

        // The auto-resolve loop never calls DispatchAsync; we still need a
        // real NotificationSender because AlertEvaluator takes it in its
        // primary constructor. All channels are disabled below, so any
        // accidental dispatch attempt writes no NotificationLog rows.
        var sender = new NotificationSender(
            new NullHttpClientFactory(),
            new SecretProtector(NullLogger<SecretProtector>.Instance),
            NullLogger<NotificationSender>.Instance);

        return new AlertEvaluator(
            db,
            sender,
            options,
            NullLogger<AlertEvaluator>.Instance);
    }

    private static AlertPolicy RiskyUsersPolicy(int threshold = Threshold) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Risky Users",
        Enabled = true,
        Category = "identity",
        Metric = RiskyUsersMetric,
        Threshold = threshold,
        Severity = "high",
        Condition = "Risky users ≥ 1",
        SuppressionMinutes = 60,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private static void SeedOpenRiskyUsers(AppDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.SecurityAlerts.Add(new SecurityAlert
            {
                AlertType = "RiskyUser",
                Severity = AlertSeverity.High,
                Service = M365ServiceArea.EntraId,
                Title = $"risky-{i}",
                DetectedAt = DateTimeOffset.UtcNow.AddHours(-i),
                IsResolved = false,
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task Streak_IncrementsOnBelowThresholdObservation()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            Severity = policy.Severity,
            Category = policy.Category,
            Condition = policy.Condition,
            MetricValue = 5,
            Threshold = policy.Threshold,
            TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = "new",
        });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 0); // metric = 0, below threshold

        var evaluator = BuildEvaluator(db);
        await evaluator.EvaluateAsync(CancellationToken.None);

        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal(1, alert.BelowThresholdStreakCount);
        Assert.Equal("new", alert.Status);
        Assert.NotNull(alert.LastEvaluatedAt);
    }

    [Fact]
    public async Task AutoResolve_AfterNConsecutiveBelow()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            Severity = policy.Severity,
            Category = policy.Category,
            Condition = policy.Condition,
            MetricValue = 5,
            Threshold = policy.Threshold,
            TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = "new",
        });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 0); // metric = 0, below threshold

        var evaluator = BuildEvaluator(db, autoResolveDebounceCycles: 2);

        // First evaluation: streak goes from 0 to 1, status stays "new".
        await evaluator.EvaluateAsync(CancellationToken.None);
        var afterFirst = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal(1, afterFirst.BelowThresholdStreakCount);
        Assert.Equal("new", afterFirst.Status);

        // Second consecutive below-threshold evaluation: streak hits 2, auto-resolves.
        await evaluator.EvaluateAsync(CancellationToken.None);
        var afterSecond = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal("auto_resolved", afterSecond.Status);
        Assert.Equal(2, afterSecond.BelowThresholdStreakCount);

        // Resolution is silent: no NotificationLog rows for this alert.
        Assert.Empty(await db.NotificationLogs.Where(l => l.TriggeredAlertId == afterSecond.Id).ToListAsync());
    }

    [Fact]
    public async Task Streak_ResetsOnAboveThreshold()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            Severity = policy.Severity,
            Category = policy.Category,
            Condition = policy.Condition,
            MetricValue = 5,
            Threshold = policy.Threshold,
            TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = "new",
            BelowThresholdStreakCount = 1, // already had one below observation
        });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 5); // metric = 5, above threshold

        var evaluator = BuildEvaluator(db);
        await evaluator.EvaluateAsync(CancellationToken.None);

        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal(0, alert.BelowThresholdStreakCount);
        Assert.Equal("new", alert.Status);
    }

    [Fact]
    public async Task AutoResolve_DebounceOneWithCyclesEqualsOne()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            Severity = policy.Severity,
            Category = policy.Category,
            Condition = policy.Condition,
            MetricValue = 5,
            Threshold = policy.Threshold,
            TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = "new",
        });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 0);

        var evaluator = BuildEvaluator(db, autoResolveDebounceCycles: 1);
        await evaluator.EvaluateAsync(CancellationToken.None);

        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal("auto_resolved", alert.Status);
    }

    [Fact]
    public async Task AutoResolve_SkipsTerminalStates()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);

        var resolvedId = Guid.NewGuid();
        var autoResolvedId = Guid.NewGuid();
        db.TriggeredAlerts.AddRange(
            new TriggeredAlert
            {
                Id = resolvedId,
                PolicyId = policy.Id,
                PolicyName = policy.Name,
                Severity = policy.Severity,
                Category = policy.Category,
                Condition = policy.Condition,
                MetricValue = 5,
                Threshold = policy.Threshold,
                TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                Status = "resolved",
                BelowThresholdStreakCount = 1,
            },
            new TriggeredAlert
            {
                Id = autoResolvedId,
                PolicyId = policy.Id,
                PolicyName = policy.Name,
                Severity = policy.Severity,
                Category = policy.Category,
                Condition = policy.Condition,
                MetricValue = 5,
                Threshold = policy.Threshold,
                TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                Status = "auto_resolved",
                BelowThresholdStreakCount = 2,
            });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 0);

        var evaluator = BuildEvaluator(db);
        await evaluator.EvaluateAsync(CancellationToken.None);

        // The auto-resolve loop filters out terminal-state alerts, so it
        // does not touch their streak counter, their status, or stamp a
        // LastEvaluatedAt on them. A terminal state cannot re-open.
        var resolvedRow = await db.TriggeredAlerts.AsNoTracking().SingleAsync(a => a.Id == resolvedId);
        Assert.Equal("resolved", resolvedRow.Status);
        Assert.Equal(1, resolvedRow.BelowThresholdStreakCount);
        Assert.Null(resolvedRow.LastEvaluatedAt);

        var autoResolvedRow = await db.TriggeredAlerts.AsNoTracking().SingleAsync(a => a.Id == autoResolvedId);
        Assert.Equal("auto_resolved", autoResolvedRow.Status);
        Assert.Equal(2, autoResolvedRow.BelowThresholdStreakCount);
        Assert.Null(autoResolvedRow.LastEvaluatedAt);
    }

    [Fact]
    public async Task AutoResolve_DoesNotDispatchNotification()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);

        // Pre-seed a notification settings row with a Teams URL that would
        // fail to deliver. If the auto-resolve path accidentally calls
        // DispatchAsync, the failure would be logged here.
        db.NotificationSettings.Add(new NotificationSettings
        {
            Id = 1,
            TeamsEnabled = true,
            TeamsWebhookUrl = "http://127.0.0.1:1/this-port-is-closed",
            EmailEnabled = false,
            WebhookEnabled = false,
            SmtpPort = 587,
            MinSeverity = "low",
        });

        var alertId = Guid.NewGuid();
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = alertId,
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            Severity = policy.Severity,
            Category = policy.Category,
            Condition = policy.Condition,
            MetricValue = 5,
            Threshold = policy.Threshold,
            TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = "new",
        });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 0);

        var evaluator = BuildEvaluator(db, autoResolveDebounceCycles: 1);
        await evaluator.EvaluateAsync(CancellationToken.None);

        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal("auto_resolved", alert.Status);
        // No log entry for this alert: auto-resolve is silent.
        Assert.Empty(await db.NotificationLogs.Where(l => l.TriggeredAlertId == alert.Id).ToListAsync());
    }

    [Fact]
    public async Task AutoResolve_PreservesAcknowledgedAt()
    {
        using var db = TestAppDbContextFactory.Create();
        var policy = RiskyUsersPolicy();
        db.AlertPolicies.Add(policy);
        var ackAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        db.TriggeredAlerts.Add(new TriggeredAlert
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            Severity = policy.Severity,
            Category = policy.Category,
            Condition = policy.Condition,
            MetricValue = 5,
            Threshold = policy.Threshold,
            TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = "acknowledged",
            AcknowledgedAt = ackAt,
            AcknowledgedBy = "dashboard",
        });
        await db.SaveChangesAsync();
        SeedOpenRiskyUsers(db, count: 0);

        var evaluator = BuildEvaluator(db, autoResolveDebounceCycles: 1);
        await evaluator.EvaluateAsync(CancellationToken.None);

        var alert = await db.TriggeredAlerts.SingleAsync();
        Assert.Equal("auto_resolved", alert.Status);
        Assert.Equal(ackAt, alert.AcknowledgedAt);
        Assert.Equal("dashboard", alert.AcknowledgedBy);
    }

    /// <summary>No-op <see cref="IHttpClientFactory"/> for tests that never make HTTP calls.</summary>
    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
