using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CollectionRunMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GraphRequestCount",
                table: "CollectionRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GraphThrottleCount",
                table: "CollectionRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GraphRequestCount",
                table: "CollectionRuns");

            migrationBuilder.DropColumn(
                name: "GraphThrottleCount",
                table: "CollectionRuns");
        }
    }
}
