using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AuditTrailOutlivesTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEntries_ClientTenants_TenantId",
                table: "AuditEntries");

            migrationBuilder.AddColumn<int>(
                name: "HashVersion",
                table: "AuditEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HashVersion",
                table: "AuditEntries");

            // Entries of a purged client name a ClientTenant that no longer exists,
            // and the foreign key below would refuse them. Kept as MSP-level entries
            // rather than deleted: a rollback must not destroy audit evidence.
            migrationBuilder.Sql(
                "UPDATE \"AuditEntries\" a SET \"TenantId\" = NULL WHERE a.\"TenantId\" IS NOT NULL " +
                "AND NOT EXISTS (SELECT 1 FROM \"ClientTenants\" c WHERE c.\"Id\" = a.\"TenantId\");");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEntries_ClientTenants_TenantId",
                table: "AuditEntries",
                column: "TenantId",
                principalTable: "ClientTenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
