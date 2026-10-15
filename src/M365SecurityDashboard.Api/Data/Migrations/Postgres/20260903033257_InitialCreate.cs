using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Author = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertNotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Condition = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Metric = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ActivityPattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    BaselineMultiplier = table.Column<double>(type: "double precision", nullable: false),
                    BaselineDays = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NotifyEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SuppressionMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTriggered = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggerCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Email);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PrevHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EntryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Activity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ActorUpn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ActorApp = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RawJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CollectionRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AlertsUpserted = table.Column<int>(type: "integer", nullable: false),
                    SourceFailures = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    GraphRequestCount = table.Column<int>(type: "integer", nullable: false),
                    GraphThrottleCount = table.Column<int>(type: "integer", nullable: false),
                    SourceFailureDetails = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GraphConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    ClientSecret = table.Column<string>(type: "text", nullable: true),
                    LoginInstance = table.Column<string>(type: "text", nullable: true),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetricsCounters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    GraphRequestsTotal = table.Column<long>(type: "bigint", nullable: false),
                    GraphThrottledTotal = table.Column<long>(type: "bigint", nullable: false),
                    EvaluationsTotal = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricsCounters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TriggeredAlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Target = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    TeamsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TeamsWebhookUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SmtpPassword = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    DefaultRecipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    WebhookEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WebhookUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    WebhookSigningSecret = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MinSeverity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TeamsDigest = table.Column<bool>(type: "boolean", nullable: false),
                    EmailDigest = table.Column<bool>(type: "boolean", nullable: false),
                    WebhookDigest = table.Column<bool>(type: "boolean", nullable: false),
                    DigestFrequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DigestHourUtc = table.Column<int>(type: "integer", nullable: false),
                    LastDigestAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureAlertThreshold = table.Column<int>(type: "integer", nullable: false),
                    LastFailureAlertAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ReportType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Cadence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    Recipients = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IncludeCsv = table.Column<bool>(type: "boolean", nullable: false),
                    IncludePdf = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunStatus = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AlertType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Service = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PortalUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    AssignedTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Disposition = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SuppressionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityPattern = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SuppressedCount = table.Column<int>(type: "integer", nullable: false),
                    LastSuppressedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuppressionRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantBaselines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CapturedBy = table.Column<string>(type: "text", nullable: true),
                    RiskyUsersCount = table.Column<int>(type: "integer", nullable: false),
                    MfaCoveragePct = table.Column<double>(type: "double precision", nullable: false),
                    NonCompliantDevicesCount = table.Column<int>(type: "integer", nullable: false),
                    CriticalAlertsCount = table.Column<int>(type: "integer", nullable: false),
                    HighAlertsCount = table.Column<int>(type: "integer", nullable: false),
                    SecureScorePct = table.Column<double>(type: "double precision", nullable: false),
                    ComplianceIssuesCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBaselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrendSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RiskyUsersCount = table.Column<int>(type: "integer", nullable: false),
                    MfaCoveragePct = table.Column<double>(type: "double precision", nullable: false),
                    NonCompliantDevicesCount = table.Column<int>(type: "integer", nullable: false),
                    CriticalAlertsCount = table.Column<int>(type: "integer", nullable: false),
                    HighAlertsCount = table.Column<int>(type: "integer", nullable: false),
                    SecureScorePct = table.Column<double>(type: "double precision", nullable: false),
                    ComplianceIssuesCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrendSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriggeredAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Condition = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MetricValue = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<int>(type: "integer", nullable: false),
                    TriggeredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Notified = table.Column<bool>(type: "boolean", nullable: false),
                    SnoozedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SnoozedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    BelowThresholdStreakCount = table.Column<int>(type: "integer", nullable: false),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AffectedEntities = table.Column<string>(type: "text", nullable: true),
                    AssignedTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggeredAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertNotes_TargetKind_TargetId",
                table: "AlertNotes",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertPolicies_Enabled",
                table: "AlertPolicies",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_Prefix",
                table: "ApiTokens",
                column: "Prefix");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_RevokedAt",
                table: "ApiTokens",
                column: "RevokedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                table: "ApiTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Timestamp",
                table: "AuditEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Activity",
                table: "AuditEvents",
                column: "Activity");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAt",
                table: "AuditEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Source_ExternalId",
                table: "AuditEvents",
                columns: new[] { "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRuns_StartedAt",
                table: "CollectionRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_SentAt",
                table: "NotificationLogs",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_DetectedAt",
                table: "SecurityAlerts",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_Service_AlertType_ExternalId",
                table: "SecurityAlerts",
                columns: new[] { "Service", "AlertType", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_Service_Severity_IsResolved",
                table: "SecurityAlerts",
                columns: new[] { "Service", "Severity", "IsResolved" });

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionRules_Enabled",
                table: "SuppressionRules",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionRules_PolicyId",
                table: "SuppressionRules",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_TrendSnapshots_CapturedAt",
                table: "TrendSnapshots",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TriggeredAlerts_PolicyId",
                table: "TriggeredAlerts",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_TriggeredAlerts_Status",
                table: "TriggeredAlerts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TriggeredAlerts_TriggeredAt",
                table: "TriggeredAlerts",
                column: "TriggeredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertNotes");

            migrationBuilder.DropTable(
                name: "AlertPolicies");

            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "CollectionRuns");

            migrationBuilder.DropTable(
                name: "GraphConfig");

            migrationBuilder.DropTable(
                name: "MetricsCounters");

            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "NotificationSettings");

            migrationBuilder.DropTable(
                name: "ReportSchedules");

            migrationBuilder.DropTable(
                name: "SecurityAlerts");

            migrationBuilder.DropTable(
                name: "SuppressionRules");

            migrationBuilder.DropTable(
                name: "TenantBaselines");

            migrationBuilder.DropTable(
                name: "TrendSnapshots");

            migrationBuilder.DropTable(
                name: "TriggeredAlerts");
        }
    }
}
