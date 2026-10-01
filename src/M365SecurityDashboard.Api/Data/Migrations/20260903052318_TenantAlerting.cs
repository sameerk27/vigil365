using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantAlerting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastMspDigestAt",
                table: "NotificationSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MspDigestEnabled",
                table: "NotificationSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MspDigestHourUtc",
                table: "NotificationSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ApiTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertPolicyTenantOverrides",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: true),
                    Threshold = table.Column<int>(type: "int", nullable: true),
                    NotifyEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertPolicyTenantOverrides", x => new { x.TenantId, x.PolicyId });
                    table.ForeignKey(
                        name: "FK_AlertPolicyTenantOverrides_AlertPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "AlertPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlertPolicyTenantOverrides_ClientTenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "ClientTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantNotificationRoutings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotifyMsp = table.Column<bool>(type: "bit", nullable: false),
                    NotifyClient = table.Column<bool>(type: "bit", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TeamsWebhookUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    WebhookUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    MinSeverity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastDigestAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAlertAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantNotificationRoutings", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_TenantNotificationRoutings_ClientTenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "ClientTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TenantId",
                table: "ApiTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertPolicyTenantOverrides_PolicyId",
                table: "AlertPolicyTenantOverrides",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertPolicyTenantOverrides_TenantId",
                table: "AlertPolicyTenantOverrides",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantNotificationRoutings_TenantId",
                table: "TenantNotificationRoutings",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiTokens_ClientTenants_TenantId",
                table: "ApiTokens",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiTokens_ClientTenants_TenantId",
                table: "ApiTokens");

            migrationBuilder.DropTable(
                name: "AlertPolicyTenantOverrides");

            migrationBuilder.DropTable(
                name: "TenantNotificationRoutings");

            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_TenantId",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "LastMspDigestAt",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "MspDigestEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "MspDigestHourUtc",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ApiTokens");
        }
    }
}
