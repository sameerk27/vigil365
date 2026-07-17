using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnomalyAlertPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaselineDays",
                table: "AlertPolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "BaselineMultiplier",
                table: "AlertPolicies",
                type: "float",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineDays",
                table: "AlertPolicies");

            migrationBuilder.DropColumn(
                name: "BaselineMultiplier",
                table: "AlertPolicies");
        }
    }
}
