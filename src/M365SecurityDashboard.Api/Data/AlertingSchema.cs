using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// Idempotent DDL + seed data for the server-side alerting tables. Kept separate
/// so installs created before the alerting feature get the new tables without a
/// full EF migration (the app uses EnsureCreated, which never alters an existing DB).
/// </summary>
public static class AlertingSchema
{
    public const string EnsureTablesSql = """
        IF OBJECT_ID(N'[AlertPolicies]', N'U') IS NULL
        CREATE TABLE [AlertPolicies] (
            [Id] uniqueidentifier NOT NULL PRIMARY KEY,
            [Name] nvarchar(200) NOT NULL,
            [Enabled] bit NOT NULL,
            [Category] nvarchar(40) NOT NULL,
            [Condition] nvarchar(300) NOT NULL,
            [Metric] nvarchar(60) NOT NULL,
            [Threshold] int NOT NULL,
            [Severity] nvarchar(20) NOT NULL,
            [NotifyEmail] nvarchar(320) NULL,
            [SuppressionMinutes] int NOT NULL,
            [CreatedAt] datetimeoffset NOT NULL,
            [LastTriggered] datetimeoffset NULL,
            [TriggerCount] int NOT NULL
        );

        IF OBJECT_ID(N'[TriggeredAlerts]', N'U') IS NULL
        CREATE TABLE [TriggeredAlerts] (
            [Id] uniqueidentifier NOT NULL PRIMARY KEY,
            [PolicyId] uniqueidentifier NOT NULL,
            [PolicyName] nvarchar(200) NOT NULL,
            [Severity] nvarchar(20) NOT NULL,
            [Category] nvarchar(40) NOT NULL,
            [Condition] nvarchar(300) NOT NULL,
            [MetricValue] int NOT NULL,
            [Threshold] int NOT NULL,
            [TriggeredAt] datetimeoffset NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [AcknowledgedAt] datetimeoffset NULL,
            [AcknowledgedBy] nvarchar(120) NULL,
            [Notified] bit NOT NULL
        );

        IF OBJECT_ID(N'[NotificationSettings]', N'U') IS NULL
        CREATE TABLE [NotificationSettings] (
            [Id] int NOT NULL PRIMARY KEY,
            [TeamsEnabled] bit NOT NULL,
            [TeamsWebhookUrl] nvarchar(2048) NULL,
            [EmailEnabled] bit NOT NULL,
            [SmtpHost] nvarchar(256) NULL,
            [SmtpPort] int NOT NULL,
            [SmtpUseSsl] bit NOT NULL,
            [SmtpUsername] nvarchar(256) NULL,
            [SmtpPassword] nvarchar(512) NULL,
            [FromAddress] nvarchar(320) NULL,
            [DefaultRecipient] nvarchar(320) NULL,
            [WebhookEnabled] bit NOT NULL,
            [WebhookUrl] nvarchar(2048) NULL,
            [MinSeverity] nvarchar(20) NOT NULL
        );

        IF OBJECT_ID(N'[NotificationLogs]', N'U') IS NULL
        CREATE TABLE [NotificationLogs] (
            [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [TriggeredAlertId] uniqueidentifier NOT NULL,
            [PolicyName] nvarchar(200) NOT NULL,
            [Channel] nvarchar(20) NOT NULL,
            [Target] nvarchar(320) NULL,
            [Success] bit NOT NULL,
            [Error] nvarchar(1000) NULL,
            [SentAt] datetimeoffset NOT NULL
        );

        IF COL_LENGTH(N'[CollectionRuns]', 'SourceFailureDetails') IS NULL
        ALTER TABLE [CollectionRuns] ADD [SourceFailureDetails] nvarchar(max) NULL;
        """;

    private static readonly (string Name, string Category, string Metric, int Threshold, string Severity, string Condition)[] Defaults =
    [
        ("Critical Security Alerts",    "identity", "criticalAlertCount", 1, "critical", "Open critical security alerts ≥ 1"),
        ("MFA Not Registered",          "identity", "mfaMissingCount",    5, "high",     "Users missing MFA ≥ 5"),
        ("Risky Users Detected",        "identity", "riskyUsersCount",    1, "high",     "Risky users ≥ 1"),
        ("Non-Compliant Devices",       "devices",  "nonCompliantCount",  1, "medium",   "Non-compliant devices ≥ 1"),
        ("Stale Devices",               "devices",  "staleDeviceCount",   1, "low",      "Devices not checked in ≥ 1"),
        ("High Priority Alerts",        "identity", "highAlertCount",     3, "high",     "Open high-severity alerts ≥ 3"),
        ("Service Health Advisory",     "identity", "serviceIssueCount",  1, "medium",   "Active M365 service issues ≥ 1"),
    ];

    public static void SeedDefaultPolicies(AppDbContext db)
    {
        if (db.AlertPolicies.Any()) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var d in Defaults)
        {
            db.AlertPolicies.Add(new AlertPolicy
            {
                Id = Guid.NewGuid(),
                Name = d.Name,
                Enabled = true,
                Category = d.Category,
                Metric = d.Metric,
                Threshold = d.Threshold,
                Severity = d.Severity,
                Condition = d.Condition,
                SuppressionMinutes = 60,
                CreatedAt = now,
                TriggerCount = 0,
            });
        }
        if (!db.NotificationSettings.Any())
            db.NotificationSettings.Add(new NotificationSettings { Id = 1 });
        db.SaveChanges();
    }
}
