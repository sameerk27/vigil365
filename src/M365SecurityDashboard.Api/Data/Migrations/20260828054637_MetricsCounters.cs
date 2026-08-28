using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetricsCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricsCounters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    GraphRequestsTotal = table.Column<long>(type: "bigint", nullable: false),
                    GraphThrottledTotal = table.Column<long>(type: "bigint", nullable: false),
                    EvaluationsTotal = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricsCounters", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricsCounters");
        }
    }
}
