using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// The single place that knows which database engine is in use. Everything
/// provider-specific — DI registration, design-time scaffolding, the one raw
/// query that has no LINQ equivalent — routes through here so that adding a
/// third engine is a matter of extending these switches, not hunting for
/// scattered <c>UseSqlServer</c> calls.
///
/// Deliberately tiny. If something else looks like it needs a per-provider
/// branch, that is a sign the query should be rewritten in LINQ instead.
/// </summary>
public static class DatabaseProviderSetup
{
    /// <summary>
    /// Reads Database:Provider (defaulting to SQL Server) and registers
    /// <see cref="AppDbContext"/> against the matching engine. Postgres is
    /// served by the <see cref="PostgresAppDbContext"/> subclass purely so EF
    /// can find its own migration set; every consumer still injects
    /// <see cref="AppDbContext"/> and is unaware of the engine.
    /// </summary>
    public static IServiceCollection AddVigilDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = ReadProvider(configuration);
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        switch (provider)
        {
            case DatabaseProvider.Postgres:
                services.AddDbContext<AppDbContext, PostgresAppDbContext>(o => Configure(o, provider, connectionString));
                break;
            default:
                services.AddDbContext<AppDbContext>(o => Configure(o, provider, connectionString));
                break;
        }
        return services;
    }

    public static DatabaseProvider ReadProvider(IConfiguration configuration)
    {
        var raw = configuration[$"{DatabaseOptions.SectionName}:Provider"];
        if (string.IsNullOrWhiteSpace(raw)) return DatabaseProvider.SqlServer;
        if (Enum.TryParse<DatabaseProvider>(raw, ignoreCase: true, out var parsed)) return parsed;
        // Accept the names people actually type. "PostgreSQL" / "npgsql" / "pg"
        // are all clearly Postgres; anything else is a hard error, because
        // silently falling back to SQL Server against a Postgres connection
        // string would produce a confusing login failure far from the cause.
        return raw.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "npgsql" or "pg" => DatabaseProvider.Postgres,
            "mssql" or "sql" => DatabaseProvider.SqlServer,
            _ => throw new InvalidOperationException(
                $"Unknown Database:Provider '{raw}'. Supported values: SqlServer, Postgres."),
        };
    }

    /// <summary>Applies the engine to an options builder. Shared by DI and design time.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, DatabaseProvider provider, string? connectionString)
        => provider switch
        {
            DatabaseProvider.Postgres => options.UseNpgsql(connectionString),
            _ => options.UseSqlServer(connectionString),
        };

    /// <summary>
    /// Total on-disk size of the current database in bytes. Both are system-catalog
    /// queries with no portable equivalent, which is why this lives here rather
    /// than in <c>MetricsService</c>. SQL Server sums data + log files in 8-KB
    /// pages; Postgres asks the catalog directly.
    /// </summary>
    public static string DatabaseSizeSql(DatabaseProvider provider)
        => provider switch
        {
            DatabaseProvider.Postgres => "SELECT pg_database_size(current_database());",
            _ => "SELECT CAST(ISNULL(SUM(CAST(size AS bigint)), 0) * 8 * 1024 AS bigint) FROM sys.database_files WHERE type IN (0, 1);",
        };

    /// <summary>Live database size in bytes via the engine's catalog. Null if unavailable.</summary>
    public static async Task<long?> QueryDatabaseSizeBytesAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = DatabaseSizeSql(ProviderOf(db));
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? l : result is not null && long.TryParse(result.ToString(), out var p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Which engine a live context is actually talking to.</summary>
    public static DatabaseProvider ProviderOf(DbContext db)
        => db.Database.IsNpgsql() ? DatabaseProvider.Postgres : DatabaseProvider.SqlServer;
}
