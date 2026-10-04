using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Tenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TenantBaselines",
                table: "TenantBaselines");

            migrationBuilder.DropIndex(
                name: "IX_SecurityAlerts_Service_AlertType_ExternalId",
                table: "SecurityAlerts");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Source_ExternalId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "TenantBaselines");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TriggeredAlerts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TrendSnapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TenantBaselines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SuppressionRules",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SecurityAlerts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ReportSchedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "NotificationSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "NotificationLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "MetricsCounters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "CollectionRuns",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AuditEvents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AuditEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AlertPolicies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AlertNotes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001") /* ClientTenant.DefaultId: every pre-tenancy row belongs to the default tenant */);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TenantBaselines",
                table: "TenantBaselines",
                column: "TenantId");

            migrationBuilder.CreateTable(
                name: "ClientTenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MicrosoftTenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientTenants", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ClientTenants",
                columns: new[] { "Id", "CreatedAt", "IsActive", "MicrosoftTenantId", "Name", "Notes" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "Default", null });

            migrationBuilder.CreateIndex(
                name: "IX_TriggeredAlerts_TenantId",
                table: "TriggeredAlerts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TrendSnapshots_TenantId",
                table: "TrendSnapshots",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBaselines_TenantId",
                table: "TenantBaselines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionRules_TenantId",
                table: "SuppressionRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_TenantId",
                table: "SecurityAlerts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_TenantId_Service_AlertType_ExternalId",
                table: "SecurityAlerts",
                columns: new[] { "TenantId", "Service", "AlertType", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_TenantId",
                table: "ReportSchedules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_TenantId",
                table: "NotificationSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_TenantId",
                table: "NotificationLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricsCounters_TenantId",
                table: "MetricsCounters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRuns_TenantId",
                table: "CollectionRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId",
                table: "AuditEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_Source_ExternalId",
                table: "AuditEvents",
                columns: new[] { "TenantId", "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TenantId",
                table: "AuditEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertPolicies_TenantId",
                table: "AlertPolicies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertNotes_TenantId",
                table: "AlertNotes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTenants_IsActive",
                table: "ClientTenants",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ClientTenants_MicrosoftTenantId",
                table: "ClientTenants",
                column: "MicrosoftTenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertNotes_ClientTenants_TenantId",
                table: "AlertNotes",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertPolicies_ClientTenants_TenantId",
                table: "AlertPolicies",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEntries_ClientTenants_TenantId",
                table: "AuditEntries",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_ClientTenants_TenantId",
                table: "AuditEvents",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CollectionRuns_ClientTenants_TenantId",
                table: "CollectionRuns",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MetricsCounters_ClientTenants_TenantId",
                table: "MetricsCounters",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_ClientTenants_TenantId",
                table: "NotificationLogs",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationSettings_ClientTenants_TenantId",
                table: "NotificationSettings",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReportSchedules_ClientTenants_TenantId",
                table: "ReportSchedules",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SecurityAlerts_ClientTenants_TenantId",
                table: "SecurityAlerts",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SuppressionRules_ClientTenants_TenantId",
                table: "SuppressionRules",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantBaselines_ClientTenants_TenantId",
                table: "TenantBaselines",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TrendSnapshots_ClientTenants_TenantId",
                table: "TrendSnapshots",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TriggeredAlerts_ClientTenants_TenantId",
                table: "TriggeredAlerts",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlertNotes_ClientTenants_TenantId",
                table: "AlertNotes");

            migrationBuilder.DropForeignKey(
                name: "FK_AlertPolicies_ClientTenants_TenantId",
                table: "AlertPolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditEntries_ClientTenants_TenantId",
                table: "AuditEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_ClientTenants_TenantId",
                table: "AuditEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_CollectionRuns_ClientTenants_TenantId",
                table: "CollectionRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_MetricsCounters_ClientTenants_TenantId",
                table: "MetricsCounters");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLogs_ClientTenants_TenantId",
                table: "NotificationLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationSettings_ClientTenants_TenantId",
                table: "NotificationSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_ReportSchedules_ClientTenants_TenantId",
                table: "ReportSchedules");

            migrationBuilder.DropForeignKey(
                name: "FK_SecurityAlerts_ClientTenants_TenantId",
                table: "SecurityAlerts");

            migrationBuilder.DropForeignKey(
                name: "FK_SuppressionRules_ClientTenants_TenantId",
                table: "SuppressionRules");

            migrationBuilder.DropForeignKey(
                name: "FK_TenantBaselines_ClientTenants_TenantId",
                table: "TenantBaselines");

            migrationBuilder.DropForeignKey(
                name: "FK_TrendSnapshots_ClientTenants_TenantId",
                table: "TrendSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_TriggeredAlerts_ClientTenants_TenantId",
                table: "TriggeredAlerts");

            migrationBuilder.DropTable(
                name: "ClientTenants");

            migrationBuilder.DropIndex(
                name: "IX_TriggeredAlerts_TenantId",
                table: "TriggeredAlerts");

            migrationBuilder.DropIndex(
                name: "IX_TrendSnapshots_TenantId",
                table: "TrendSnapshots");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TenantBaselines",
                table: "TenantBaselines");

            migrationBuilder.DropIndex(
                name: "IX_TenantBaselines_TenantId",
                table: "TenantBaselines");

            migrationBuilder.DropIndex(
                name: "IX_SuppressionRules_TenantId",
                table: "SuppressionRules");

            migrationBuilder.DropIndex(
                name: "IX_SecurityAlerts_TenantId",
                table: "SecurityAlerts");

            migrationBuilder.DropIndex(
                name: "IX_SecurityAlerts_TenantId_Service_AlertType_ExternalId",
                table: "SecurityAlerts");

            migrationBuilder.DropIndex(
                name: "IX_ReportSchedules_TenantId",
                table: "ReportSchedules");

            migrationBuilder.DropIndex(
                name: "IX_NotificationSettings_TenantId",
                table: "NotificationSettings");

            migrationBuilder.DropIndex(
                name: "IX_NotificationLogs_TenantId",
                table: "NotificationLogs");

            migrationBuilder.DropIndex(
                name: "IX_MetricsCounters_TenantId",
                table: "MetricsCounters");

            migrationBuilder.DropIndex(
                name: "IX_CollectionRuns_TenantId",
                table: "CollectionRuns");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TenantId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TenantId_Source_ExternalId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_TenantId",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AlertPolicies_TenantId",
                table: "AlertPolicies");

            migrationBuilder.DropIndex(
                name: "IX_AlertNotes_TenantId",
                table: "AlertNotes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TriggeredAlerts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TrendSnapshots");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TenantBaselines");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SuppressionRules");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SecurityAlerts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ReportSchedules");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "MetricsCounters");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "CollectionRuns");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AlertPolicies");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AlertNotes");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "TenantBaselines",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TenantBaselines",
                table: "TenantBaselines",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlerts_Service_AlertType_ExternalId",
                table: "SecurityAlerts",
                columns: new[] { "Service", "AlertType", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Source_ExternalId",
                table: "AuditEvents",
                columns: new[] { "Source", "ExternalId" },
                unique: true);
        }
    }
}
