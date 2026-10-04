using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace M365SecurityDashboard.Api.Tests.Relational;

/// <summary>
/// Provides a real SQL Server and a real PostgreSQL for the whole test run, and
/// hands out a freshly-migrated, uniquely-named database on either engine per
/// test. This is the harness the in-memory provider cannot be: in-memory
/// generates no SQL, so it proves nothing about index filters, column types,
/// identity columns, batch deletes or the raw system-catalog query — and, once
/// tenant isolation lands, nothing about global query filters. Everything that
/// must hold on both engines runs through here.
///
/// Where the engines come from, per engine, in order:
///   1. VIGIL365_TEST_SQLSERVER / VIGIL365_TEST_POSTGRES — a connection string to
///      a server you already have (a developer's local SQL Express, say). The
///      login needs CREATE DATABASE. Databases are named vigil_test_* and dropped
///      at the end of the run.
///   2. Otherwise a Testcontainers Docker container.
///   3. Otherwise that engine is unavailable and its tests skip with the reason.
///
/// Set VIGIL365_REQUIRE_DB_TESTS=1 (CI does) to turn an unavailable engine into a
/// failure, so a broken Docker setup on the build agent cannot pass silently by
/// skipping the only tests that exercise a real database.
/// </summary>
public sealed class RelationalEngines : IAsyncLifetime
{
    public const string RequireEnvVar = "VIGIL365_REQUIRE_DB_TESTS";
    public const string SqlServerEnvVar = "VIGIL365_TEST_SQLSERVER";
    public const string PostgresEnvVar = "VIGIL365_TEST_POSTGRES";

    private sealed class Engine
    {
        public string? ConnectionString;
        public string? UnavailableReason;
        public IAsyncDisposable? Container;
        public readonly List<string> CreatedDatabases = [];
    }

    private readonly Dictionary<DatabaseProvider, Engine> _engines = new()
    {
        [DatabaseProvider.SqlServer] = new(),
        [DatabaseProvider.Postgres] = new(),
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            Start(DatabaseProvider.SqlServer, SqlServerEnvVar, () => new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()),
            Start(DatabaseProvider.Postgres, PostgresEnvVar, () => new PostgreSqlBuilder("postgres:16-alpine").Build()));

        var required = Environment.GetEnvironmentVariable(RequireEnvVar) == "1";
        var missing = _engines.Where(e => e.Value.UnavailableReason is not null).ToList();
        if (required && missing.Count > 0)
            throw new InvalidOperationException(
                $"{RequireEnvVar}=1 but these engines are unavailable: " +
                string.Join("; ", missing.Select(m => $"{m.Key}: {m.Value.UnavailableReason}")));
    }

    private async Task Start<T>(DatabaseProvider provider, string envVar, Func<T> buildContainer)
        where T : class, IAsyncDisposable
    {
        var engine = _engines[provider];
        var external = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(external))
        {
            engine.ConnectionString = external;
            return;
        }

        try
        {
            var container = buildContainer();
            engine.Container = container;
            switch (container)
            {
                case MsSqlContainer m:
                    await m.StartAsync();
                    engine.ConnectionString = m.GetConnectionString();
                    break;
                case PostgreSqlContainer p:
                    await p.StartAsync();
                    engine.ConnectionString = p.GetConnectionString();
                    break;
            }
        }
        catch (Exception ex)
        {
            engine.UnavailableReason =
                $"{provider} unavailable — no Docker and {envVar} not set ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})";
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var (provider, engine) in _engines)
        {
            if (engine.ConnectionString is null) continue;
            // Containers vanish anyway; an external server would otherwise fill up
            // with vigil_test_* databases, one per test per run.
            foreach (var name in engine.CreatedDatabases)
            {
                try
                {
                    await using var db = Create(provider, name);
                    await db.Database.EnsureDeletedAsync();
                }
                catch
                {
                    // Best effort: a leftover test database is untidy, not a failure.
                }
            }
            if (engine.Container is not null) await engine.Container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test if this engine is unavailable.</summary>
    public void RequireAvailable(DatabaseProvider provider)
        => Skip.If(_engines[provider].UnavailableReason is not null, _engines[provider].UnavailableReason);

    /// <summary>
    /// A brand-new database on the given engine with every migration applied.
    /// Each call gets its own database so tests cannot see each other's rows.
    /// Migrate() creates the database on both providers when it does not exist.
    /// </summary>
    public async Task<AppDbContext> CreateMigratedDatabaseAsync(DatabaseProvider provider, CancellationToken ct = default)
    {
        RequireAvailable(provider);
        var name = "vigil_test_" + Guid.NewGuid().ToString("N")[..12];
        _engines[provider].CreatedDatabases.Add(name);
        var db = Create(provider, name);
        await db.Database.MigrateAsync(ct);
        return db;
    }

    /// <summary>
    /// Contexts from here run as the seeded default tenant, the same one every
    /// single-tenant install uses, so parity tests can write scoped rows without
    /// ceremony. Isolation tests construct their own per-tenant contexts.
    /// </summary>
    private AppDbContext Create(DatabaseProvider provider, string databaseName)
    {
        var baseCs = _engines[provider].ConnectionString
            ?? throw new InvalidOperationException(_engines[provider].UnavailableReason);
        var tenant = TestTenancy.For(ClientTenant.DefaultId);

        switch (provider)
        {
            case DatabaseProvider.Postgres:
            {
                var cs = new Npgsql.NpgsqlConnectionStringBuilder(baseCs) { Database = databaseName };
                var o = new DbContextOptionsBuilder<PostgresAppDbContext>();
                DatabaseProviderSetup.Configure(o, provider, cs.ConnectionString);
                return new PostgresAppDbContext(o.Options, tenant);
            }
            default:
            {
                var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseCs) { InitialCatalog = databaseName };
                var o = new DbContextOptionsBuilder<AppDbContext>();
                DatabaseProviderSetup.Configure(o, provider, cs.ConnectionString);
                return new AppDbContext(o.Options, tenant);
            }
        }
    }

    /// <summary>The engines every parity test runs against. xUnit MemberData shape.</summary>
    public static IEnumerable<object[]> All =>
        Enum.GetValues<DatabaseProvider>().Select(p => new object[] { p });
}

[CollectionDefinition(Name)]
public sealed class RelationalEnginesCollection : ICollectionFixture<RelationalEngines>
{
    public const string Name = "Relational engines";
}
