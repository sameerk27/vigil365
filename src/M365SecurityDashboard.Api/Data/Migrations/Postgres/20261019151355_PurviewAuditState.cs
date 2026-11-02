using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class PurviewAuditState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurviewAuditStates",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Feed = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PendingQueryId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PendingFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingSubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Watermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastEventCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurviewAuditStates", x => new { x.TenantId, x.Feed });
                    table.ForeignKey(
                        name: "FK_PurviewAuditStates_ClientTenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "ClientTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurviewAuditStates_TenantId",
                table: "PurviewAuditStates",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurviewAuditStates");
        }
    }
}
