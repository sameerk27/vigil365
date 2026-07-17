using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestHourUtc",
                table: "NotificationSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EmailDigest",
                table: "NotificationSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailureAlertThreshold",
                table: "NotificationSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDigestAt",
                table: "NotificationSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastFailureAlertAt",
                table: "NotificationSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TeamsDigest",
                table: "NotificationSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WebhookDigest",
                table: "NotificationSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestHourUtc",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "EmailDigest",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "FailureAlertThreshold",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "LastDigestAt",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "LastFailureAlertAt",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TeamsDigest",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WebhookDigest",
                table: "NotificationSettings");
        }
    }
}
