using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M365SecurityDashboard.Api.Data.Migrations
{
    /// <summary>
    /// Removes IDENTITY from the two singleton configuration tables.
    ///
    /// NotificationSettings and GraphConfig each hold exactly one row with a
    /// fixed key of 1, which the model supplies. InitialCreate made those keys
    /// identity columns by EF convention, so the explicit key was rejected with
    /// "Cannot insert explicit value for identity column". NotificationSettings is
    /// seeded during startup, so a fresh install crashed before it ever served a
    /// request; GraphConfig would have failed the first time anyone saved Graph
    /// credentials on the setup page.
    ///
    /// Written by hand because the scaffolded AlterColumn emits a plain
    /// ALTER COLUMN, and SQL Server rejects that with "To change the IDENTITY
    /// property of a column, the column needs to be dropped and recreated."
    ///
    /// The rebuild adds a plain int column, copies the values across, drops the
    /// identity column and renames — rather than recreating the table from a
    /// column list, which would silently rot as later migrations add columns.
    /// </summary>
    public partial class SingletonKeysNotIdentity : Migration
    {
        private const string DropIdentity = """
            IF EXISTS (SELECT 1 FROM sys.identity_columns
                       WHERE OBJECT_NAME(object_id) = '{0}' AND name = 'Id')
            BEGIN
                ALTER TABLE [{0}] ADD [Id_tmp] int NULL;
                EXEC('UPDATE [{0}] SET [Id_tmp] = [Id]');
                ALTER TABLE [{0}] DROP CONSTRAINT [PK_{0}];
                ALTER TABLE [{0}] DROP COLUMN [Id];
                EXEC sp_rename '{0}.Id_tmp', 'Id', 'COLUMN';
                ALTER TABLE [{0}] ALTER COLUMN [Id] int NOT NULL;
                ALTER TABLE [{0}] ADD CONSTRAINT [PK_{0}] PRIMARY KEY ([Id]);
            END
            """;

        private const string AddIdentity = """
            IF NOT EXISTS (SELECT 1 FROM sys.identity_columns
                           WHERE OBJECT_NAME(object_id) = '{0}' AND name = 'Id')
            BEGIN
                ALTER TABLE [{0}] DROP CONSTRAINT [PK_{0}];
                ALTER TABLE [{0}] DROP COLUMN [Id];
                -- Renumbers rather than preserving the old keys. These tables hold
                -- a single row, so that row becomes 1 again.
                ALTER TABLE [{0}] ADD [Id] int IDENTITY(1,1) NOT NULL;
                ALTER TABLE [{0}] ADD CONSTRAINT [PK_{0}] PRIMARY KEY ([Id]);
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(string.Format(DropIdentity, "NotificationSettings"));
            migrationBuilder.Sql(string.Format(DropIdentity, "GraphConfig"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(string.Format(AddIdentity, "GraphConfig"));
            migrationBuilder.Sql(string.Format(AddIdentity, "NotificationSettings"));
        }
    }
}
