using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class TenantCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "ClientTenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "ClientTenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientSecret",
                table: "ClientTenants",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsentGrantedAt",
                table: "ClientTenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCollectionAt",
                table: "ClientTenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCollectionStatus",
                table: "ClientTenants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ClientTenants",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoginInstance",
                table: "ClientTenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ClientTenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                columns: new[] { "BaseUrl", "ClientId", "ClientSecret", "ConsentGrantedAt", "LastCollectionAt", "LastCollectionStatus", "LastError", "LoginInstance" },
                values: new object[] { null, null, null, null, null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "ClientSecret",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "ConsentGrantedAt",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "LastCollectionAt",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "LastCollectionStatus",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ClientTenants");

            migrationBuilder.DropColumn(
                name: "LoginInstance",
                table: "ClientTenants");
        }
    }
}
