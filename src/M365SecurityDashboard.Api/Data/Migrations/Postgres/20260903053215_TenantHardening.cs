using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class TenantHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrandAccentColor",
                table: "ClientTenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrandName",
                table: "ClientTenants",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificatePassword",
                table: "ClientTenants",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificatePath",
                table: "ClientTenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateThumbprint",
                table: "ClientTenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "ClientTenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCollectionAfter",
                table: "ClientTenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ClientTenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                columns: new[] { "BrandAccentColor", "BrandName", "CertificatePassword", "CertificatePath", "CertificateThumbprint", "ConsecutiveFailures", "NextCollectionAfter" },
                values: new object[] { null, null, null, null, null, 0, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrandAccentColor",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "BrandName",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "CertificatePassword",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "CertificatePath",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "CertificateThumbprint",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "NextCollectionAfter",
                table: "ClientTenants");
        }
    }
}
