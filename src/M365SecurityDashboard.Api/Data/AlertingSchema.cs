using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// LEGACY BRIDGE + seed data. The schema is now owned by EF migrations
/// (Data/Migrations); this idempotent DDL runs exactly once — when a
/// pre-migration database is baselined at startup — to bring any older install
/// up to the model the InitialCreate migration describes. Do not add new
/// schema changes here; add a migration instead.
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
            [Notified] bit NOT NULL,
            [AffectedEntities] nvarchar(max) NULL
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

        IF COL_LENGTH(N'[TriggeredAlerts]', 'SnoozedUntil') IS NULL
        ALTER TABLE [TriggeredAlerts] ADD [SnoozedUntil] datetimeoffset NULL;

        IF COL_LENGTH(N'[TriggeredAlerts]', 'SnoozedBy') IS NULL
        ALTER TABLE [TriggeredAlerts] ADD [SnoozedBy] nvarchar(120) NULL;

        IF COL_LENGTH(N'[TriggeredAlerts]', 'BelowThresholdStreakCount') IS NULL
        ALTER TABLE [TriggeredAlerts] ADD [BelowThresholdStreakCount] int NOT NULL CONSTRAINT [DF_TriggeredAlerts_Streak] DEFAULT 0;

        IF COL_LENGTH(N'[TriggeredAlerts]', 'LastEvaluatedAt') IS NULL
        ALTER TABLE [TriggeredAlerts] ADD [LastEvaluatedAt] datetimeoffset NULL;

        IF COL_LENGTH(N'[TriggeredAlerts]', 'AffectedEntities') IS NULL
        ALTER TABLE [TriggeredAlerts] ADD [AffectedEntities] nvarchar(max) NULL;

        IF OBJECT_ID(N'[TrendSnapshots]', N'U') IS NULL
        BEGIN
            CREATE TABLE [TrendSnapshots] (
                [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                [CapturedAt] datetimeoffset NOT NULL,
                [RiskyUsersCount] int NOT NULL,
                [MfaCoveragePct] float NOT NULL,
                [NonCompliantDevicesCount] int NOT NULL,
                [CriticalAlertsCount] int NOT NULL,
                [HighAlertsCount] int NOT NULL,
                [SecureScorePct] float NOT NULL,
                [ComplianceIssuesCount] int NOT NULL
            );
            CREATE INDEX [IX_TrendSnapshots_CapturedAt] ON [TrendSnapshots] ([CapturedAt]);
        END

        IF OBJECT_ID(N'[AppUsers]', N'U') IS NULL
        CREATE TABLE [AppUsers] (
            [Email] nvarchar(320) NOT NULL PRIMARY KEY,
            [DisplayName] nvarchar(200) NULL,
            [Role] nvarchar(20) NOT NULL,
            [CreatedAt] datetimeoffset NOT NULL,
            [LastSeenAt] datetimeoffset NOT NULL
        );

        IF OBJECT_ID(N'[AuditEntries]', N'U') IS NULL
        CREATE TABLE [AuditEntries] (
            [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [Timestamp] datetimeoffset NOT NULL,
            [ActorEmail] nvarchar(320) NOT NULL,
            [Action] nvarchar(60) NOT NULL,
            [TargetType] nvarchar(40) NOT NULL,
            [TargetId] nvarchar(320) NULL,
            [Details] nvarchar(500) NULL
        );
        IF OBJECT_ID(N'[AuditEntries]', N'U') IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditEntries_Timestamp')
        CREATE INDEX [IX_AuditEntries_Timestamp] ON [AuditEntries]([Timestamp]);

        IF COL_LENGTH(N'[AuditEntries]', 'IpAddress') IS NULL
        ALTER TABLE [AuditEntries] ADD [IpAddress] nvarchar(45) NULL;

        IF COL_LENGTH(N'[AuditEntries]', 'UserAgent') IS NULL
        ALTER TABLE [AuditEntries] ADD [UserAgent] nvarchar(300) NULL;

        IF COL_LENGTH(N'[AuditEntries]', 'PrevHash') IS NULL
        ALTER TABLE [AuditEntries] ADD [PrevHash] nvarchar(64) NULL;

        IF COL_LENGTH(N'[AuditEntries]', 'EntryHash') IS NULL
        ALTER TABLE [AuditEntries] ADD [EntryHash] nvarchar(64) NULL;

        IF OBJECT_ID(N'[GraphConfig]', N'U') IS NULL
        CREATE TABLE [GraphConfig] (
            [Id] int NOT NULL PRIMARY KEY,
            [TenantId] nvarchar(100) NOT NULL,
            [ClientId] nvarchar(100) NOT NULL,
            [ClientSecret] nvarchar(1024) NULL,
            [UpdatedAt] datetimeoffset NOT NULL
        );
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

    /// <summary>
    /// Activity-based starter pack: alerts on WHAT HAPPENED in the tenant
    /// (directory-audit activities), not on metric counts. Pattern supports *
    /// as wildcard against Graph activityDisplayName.
    /// </summary>
    private static readonly (string Name, string Category, string Pattern, string Severity)[] ActivityDefaults =
    [
        ("Privileged role assignment",        "identity",   "Add member to role",                              "critical"),
        ("Eligible role assignment (PIM)",    "identity",   "Add eligible member to role",                     "high"),
        ("App consent granted",               "identity",   "Consent to application",                          "high"),
        ("Application credential added",      "identity",   "*Certificates and secrets management*",           "high"),
        ("Conditional Access policy changed", "identity",   "*conditional access policy",                      "high"),
        ("Federation settings changed",       "identity",   "Set federation settings on domain",               "critical"),
        ("New application registered",        "identity",   "Add application",                                 "medium"),
        ("Service principal added",           "identity",   "Add service principal",                           "medium"),
        ("User deleted",                      "identity",   "Delete user",                                     "medium"),
        ("Admin password reset",              "identity",   "Reset user password",                             "medium"),
        ("Account disabled",                  "identity",   "Disable account",                                 "medium"),
    ];

    public static void SeedDefaultPolicies(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        if (!db.AlertPolicies.Any())
        {
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
        }

        // Seed the activity pack independently so existing installs (which
        // already have metric policies) still receive it once.
        if (!db.AlertPolicies.Any(p => p.Kind == "activity"))
        {
            foreach (var a in ActivityDefaults)
            {
                db.AlertPolicies.Add(new AlertPolicy
                {
                    Id = Guid.NewGuid(),
                    Name = a.Name,
                    Enabled = true,
                    Kind = "activity",
                    Category = a.Category,
                    Metric = "",
                    ActivityPattern = a.Pattern,
                    WindowMinutes = 60,
                    Threshold = 1,
                    Severity = a.Severity,
                    Condition = $"Activity \"{a.Pattern}\" ≥ 1 in 60m",
                    SuppressionMinutes = 60,
                    CreatedAt = now,
                    TriggerCount = 0,
                });
            }
        }

        if (!db.NotificationSettings.Any())
            db.NotificationSettings.Add(new NotificationSettings { Id = 1 });
        db.SaveChanges();
    }
}
