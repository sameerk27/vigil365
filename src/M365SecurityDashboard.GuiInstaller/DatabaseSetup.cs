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

        /// <param name="connectionString">The connection string the service will use.</param>
        /// <param name="serviceAccount">The Windows service account (LOCAL SERVICE) — only
        /// relevant when the service authenticates to SQL with Windows/Trusted auth.</param>
        public static void GrantServiceAccess(string connectionString, string serviceAccount, Action<string> log)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var database = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("The connection string does not name a database.");

            // How does the SERVICE authenticate to SQL at runtime? If the operator
            // supplied a SQL login (User ID + Password), the service connects with
            // those credentials — typical for a remote SQL Server — and there is no
            // Windows "LOCAL SERVICE" login to create. Only local Trusted_Connection
            // installs need the Windows login. Getting this wrong is what broke every
            // remote-SQL install: the old code forced IntegratedSecurity=true here,
            // throwing away the operator's SQL credentials and then failing to create
            // a local-service Windows login on a machine that isn't the SQL host.
            bool sqlAuth = !builder.IntegratedSecurity && !string.IsNullOrWhiteSpace(builder.UserID);

            // Admin connection to master — the application database may not exist yet,
            // and login/database creation are server-level. CRUCIALLY, preserve the
            // operator's own authentication (SQL creds or Trusted) rather than forcing
            // one, so a remote SQL Server is reached with the credentials given.
            var adminBuilder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                TrustServerCertificate = true,
                ConnectTimeout = 15,
            };
            var adminConnection = adminBuilder.ConnectionString;

            using var conn = new SqlConnection(adminConnection);
            conn.Open();

            // The account that must end up owning the database:
            //  - SQL auth  → the operator's SQL login (it already exists as a login).
            //  - Trusted   → the Windows service account, whose login we must create.
            string dbAccount = sqlAuth ? builder.UserID! : serviceAccount;

            if (!sqlAuth)
            {
                // QUOTENAME rather than raw concatenation, and sp_executesql rather
                // than EXEC(): EXEC() accepts only string literals and variables
                // concatenated together, so a function call inside it is a syntax
                // error. Building the statement into a variable first combines them.
                Execute(conn, SqlCreateLogin, serviceAccount);
                log($"SQL login for {serviceAccount} is present.");
            }
            else
            {
                log($"Using the SQL login '{builder.UserID}' from the connection string — no Windows login needed.");
            }

            // EF applies migrations on startup, which needs the database to exist.
            // Creating it here rather than granting the service dbcreator keeps the
            // service account's rights scoped to this one database.
            Execute(conn, SqlCreateDatabase, dbAccount, database);
            log($"Database {database} is present.");

            var dbConnection = new SqlConnectionStringBuilder(adminConnection) { InitialCatalog = database }.ConnectionString;
            using var dbConn = new SqlConnection(dbConnection);
            dbConn.Open();

            // db_owner because migrations create and alter tables. Narrower roles
            // cannot apply a schema change, and this install owns the database
            // outright. SqlGrantDbOwner creates the database USER for the login if
            // absent, so it works whether the login is the Windows service account
            // or the operator's SQL login.
            Execute(dbConn, SqlGrantDbOwner, dbAccount);
            log($"{dbAccount} can now read and write {database}.");
        }

        /// <summary>
        /// SERVERPROPERTY('EngineEdition') of the target SQL Server (4 = Express), read
        /// over master so the application database need not exist yet. Null if the
        /// server can't be reached — the grant step that follows reports that properly.
        /// </summary>
        public static int? SqlEngineEdition(string connectionString)
        {
            try
            {
                var b = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", TrustServerCertificate = true, ConnectTimeout = 15 };
                using var conn = new SqlConnection(b.ConnectionString);
                conn.Open();
                using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", conn);
                return cmd.ExecuteScalar() is int i ? i : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// PostgreSQL (MSP mode, bring your own server): make sure the service can run
        /// migrations. Creates the database if it's missing and the login may, and
        /// checks the login can create tables in schema public — PostgreSQL 15 stopped
        /// granting that to everyone, and without it the service would die on its
        /// first migration with an error far from the cause. No Windows login is
        /// involved: the service uses the credentials in the connection string.
        /// </summary>
        public static void PreparePostgres(string connectionString, Action<string> log)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            var database = builder.Database;
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("The PostgreSQL connection string does not name a database (Database=...).");
            if (string.IsNullOrWhiteSpace(builder.Username))
                throw new InvalidOperationException("The PostgreSQL connection string needs a user (Username=...) — the service connects as that user.");
            builder.Timeout = 15;

            try
            {
                using var probe = new Npgsql.NpgsqlConnection(builder.ConnectionString);
                probe.Open();
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "3D000") // database does not exist
            {
                var admin = new Npgsql.NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };
                using var conn = new Npgsql.NpgsqlConnection(admin.ConnectionString);
                conn.Open();
                using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", conn);
                try { create.ExecuteNonQuery(); }
                catch (Npgsql.PostgresException cex) when (cex.SqlState == "42501")
                {
                    throw new InvalidOperationException(
                        $"Database '{database}' does not exist and '{builder.Username}' is not allowed to create it. " +
                        "Create it (owned by that user) and run Setup again.", cex);
                }
                log($"Created PostgreSQL database {database}.");
            }

            using var db = new Npgsql.NpgsqlConnection(builder.ConnectionString);
            db.Open();
            using var check = new Npgsql.NpgsqlCommand("SELECT has_schema_privilege(current_user, 'public', 'CREATE')", db);
            if (check.ExecuteScalar() is not true)
                throw new InvalidOperationException(
                    $"'{builder.Username}' cannot create tables in schema public of '{database}'. " +
                    $"Grant it (GRANT CREATE ON SCHEMA public TO \"{builder.Username}\") or make that user the database owner, then run Setup again.");
            log($"PostgreSQL database {database} is ready; {builder.Username} can apply migrations.");
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
