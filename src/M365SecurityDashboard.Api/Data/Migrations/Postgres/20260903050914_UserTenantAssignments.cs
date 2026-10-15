using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class UserTenantAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserTenantAssignments",
                columns: table => new
                {
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTenantAssignments", x => new { x.UserEmail, x.TenantId });
                    table.ForeignKey(
                        name: "FK_UserTenantAssignments_AppUsers_UserEmail",
                        column: x => x.UserEmail,
                        principalTable: "AppUsers",
                        principalColumn: "Email",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserTenantAssignments_ClientTenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "ClientTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserTenantAssignments_TenantId",
                table: "UserTenantAssignments",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTenantAssignments");
        }
    }
}
