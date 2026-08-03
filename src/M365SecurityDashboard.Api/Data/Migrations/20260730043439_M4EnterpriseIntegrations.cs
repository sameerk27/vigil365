using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class M4EnterpriseIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludePdf",
                table: "ReportSchedules",
                type: "bit",
                nullable: false,
                // Adding the non-null column backfills existing schedules. Their
                // email digest should gain the new executive PDF by default.
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSigningSecret",
                table: "NotificationSettings",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "IncludePdf",
                table: "ReportSchedules");

            migrationBuilder.DropColumn(
                name: "WebhookSigningSecret",
                table: "NotificationSettings");
        }
    }
}
