using System;
using Microsoft.Data.SqlClient;

namespace M365SecurityDashboard.GuiInstaller
{
    /// <summary>
    /// Gives the Windows service account access to the database.
    ///
    /// This is not optional plumbing. The service runs as LOCAL SERVICE and its
    /// connection string uses Trusted_Connection, so SQL sees a login named
    /// "NT AUTHORITY\LOCAL SERVICE". A fresh SQL Express install grants sysadmin
    /// to BUILTIN\ADMINISTRATORS and nothing else, so that login does not exist —
    /// the service starts, fails to open a connection, and the whole install looks
    /// broken for a reason that never appears in the installer's own log.
    ///
    /// The installer itself runs elevated, so it connects as a local administrator
    /// (already sysadmin) and creates the login the service will need.
    /// </summary>
    internal static class DatabaseSetup
    {
        // The statements live here as constants rather than inline so their syntax
        // can be verified against a real server without executing them (SET
        // PARSEONLY). An earlier version used EXEC('...' + QUOTENAME(@account)),
        // which is a syntax error — EXEC() concatenates only literals and
        // variables, never function calls — and nothing caught it until an install
        // failed in front of a user.
        internal const string SqlCreateLogin = """
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @account)
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@account) + N' FROM WINDOWS';
                EXEC sp_executesql @sql;
            END
            """;

        internal const string SqlCreateDatabase = """
            IF DB_ID(@db) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@db);
                EXEC sp_executesql @sql;
            END
            """;

        internal const string SqlGrantDbOwner = """
            DECLARE @sql nvarchar(max);
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @account)
            BEGIN
                SET @sql = N'CREATE USER ' + QUOTENAME(@account) + N' FOR LOGIN ' + QUOTENAME(@account);
                EXEC sp_executesql @sql;
            END
            SET @sql = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@account);
            EXEC sp_executesql @sql;
            """;

        public static void GrantServiceAccess(string connectionString, string account, Action<string> log)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var database = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("The connection string does not name a database.");

            // Connect to master: the application database may not exist yet, and
            // creating logins is a server-level operation regardless.
            var adminConnection = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                ConnectTimeout = 15
            }.ConnectionString;

            using var conn = new SqlConnection(adminConnection);
            conn.Open();

            // QUOTENAME rather than raw concatenation, and sp_executesql rather
            // than EXEC(): EXEC() accepts only string literals and variables
            // concatenated together, so a function call inside it is a syntax
            // error ("Incorrect syntax near 'QUOTENAME'"). Building the statement
            // into a variable first is what makes the two combine.
            Execute(conn, SqlCreateLogin, account);
            log($"SQL login for {account} is present.");

            // EF applies migrations on startup, which needs the database to exist.
            // Creating it here rather than granting the service dbcreator keeps the
            // service account's rights scoped to this one database.
            Execute(conn, SqlCreateDatabase, account, database);
            log($"Database {database} is present.");

            var dbConnection = new SqlConnectionStringBuilder(adminConnection) { InitialCatalog = database }.ConnectionString;
            using var dbConn = new SqlConnection(dbConnection);
            dbConn.Open();

            // db_owner because migrations create and alter tables. Narrower roles
            // cannot apply a schema change, and this install owns the database
            // outright.
            Execute(dbConn, SqlGrantDbOwner, account);
            log($"{account} can now read and write {database}.");
        }

        private static void Execute(SqlConnection conn, string sql, string account, string? database = null)
        {
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@account", account);
            if (database != null) cmd.Parameters.AddWithValue("@db", database);
            cmd.ExecuteNonQuery();
        }
    }
}
