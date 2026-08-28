using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantBaselines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CapturedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RiskyUsersCount = table.Column<int>(type: "int", nullable: false),
                    MfaCoveragePct = table.Column<double>(type: "float", nullable: false),
                    NonCompliantDevicesCount = table.Column<int>(type: "int", nullable: false),
                    CriticalAlertsCount = table.Column<int>(type: "int", nullable: false),
                    HighAlertsCount = table.Column<int>(type: "int", nullable: false),
                    SecureScorePct = table.Column<double>(type: "float", nullable: false),
                    ComplianceIssuesCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBaselines", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantBaselines");
        }
    }
}
