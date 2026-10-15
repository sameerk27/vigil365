using System.Reflection;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Guards the multi-engine seam. None of these tests open a connection: building
/// an EF model only needs the provider assembly, so the provider-specific
/// spellings in <see cref="AppDbContext.OnModelCreating"/> and the migration
/// attribution rules can be verified on any CI runner without a database.
/// Real-database parity runs live in the Testcontainers suite (MSP plan §1.5).
/// </summary>
public sealed class DatabaseProviderTests
{
    private static AppDbContext SqlServerContext()
    {
        var o = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseProviderSetup.Configure(o, DatabaseProvider.SqlServer, "Server=unused;Database=unused;Trusted_Connection=True");
        return new AppDbContext(o.Options);
    }

    private static PostgresAppDbContext PostgresContext()
    {
        var o = new DbContextOptionsBuilder<PostgresAppDbContext>();
        DatabaseProviderSetup.Configure(o, DatabaseProvider.Postgres, "Host=unused;Database=unused;Username=u;Password=p");
        return new PostgresAppDbContext(o.Options);
    }

    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider,
        }).Build();

    // ── Provider selection ────────────────────────────────────────────────────

    [Fact]
    public void Missing_provider_defaults_to_SqlServer_so_existing_installs_are_unaffected()
        => Assert.Equal(DatabaseProvider.SqlServer, DatabaseProviderSetup.ReadProvider(Config(null)));

    [Theory]
    [InlineData("SqlServer", DatabaseProvider.SqlServer)]
    [InlineData("sqlserver", DatabaseProvider.SqlServer)]
    [InlineData("mssql", DatabaseProvider.SqlServer)]
    [InlineData("Postgres", DatabaseProvider.Postgres)]
    [InlineData("postgresql", DatabaseProvider.Postgres)]
    [InlineData("PostgreSQL", DatabaseProvider.Postgres)]
    [InlineData("npgsql", DatabaseProvider.Postgres)]
    [InlineData("pg", DatabaseProvider.Postgres)]
    public void Provider_names_people_actually_type_are_accepted(string raw, DatabaseProvider expected)
        => Assert.Equal(expected, DatabaseProviderSetup.ReadProvider(Config(raw)));

    [Fact]
    public void Unknown_provider_is_a_hard_error_not_a_silent_SqlServer_fallback()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseProviderSetup.ReadProvider(Config("oracle")));
        Assert.Contains("oracle", ex.Message);
        Assert.Contains("SqlServer, Postgres", ex.Message);
    }

    // ── Model spellings per engine ────────────────────────────────────────────

    [Fact]
    public void SqlServer_model_keeps_bracket_filter_and_nvarchar_max_so_its_snapshot_does_not_move()
    {
        using var db = SqlServerContext();
        var alert = db.Model.FindEntityType(typeof(SecurityAlert))!;

        var unique = alert.GetIndexes().Single(i => i.IsUnique);
        Assert.Equal("[ExternalId] IS NOT NULL", unique.GetFilter());
        Assert.Equal("nvarchar(max)", alert.FindProperty(nameof(SecurityAlert.RawJson))!.GetColumnType());
        Assert.Equal("nvarchar(max)", db.Model.FindEntityType(typeof(AuditEvent))!.FindProperty(nameof(AuditEvent.RawJson))!.GetColumnType());
        Assert.Equal("nvarchar(max)", db.Model.FindEntityType(typeof(CollectionRun))!.FindProperty(nameof(CollectionRun.SourceFailureDetails))!.GetColumnType());
    }

    [Fact]
    public void Postgres_model_uses_quoted_filter_and_text()
    {
        using var db = PostgresContext();
        var alert = db.Model.FindEntityType(typeof(SecurityAlert))!;

        var unique = alert.GetIndexes().Single(i => i.IsUnique);
        Assert.Equal("\"ExternalId\" IS NOT NULL", unique.GetFilter());
        Assert.Equal("text", alert.FindProperty(nameof(SecurityAlert.RawJson))!.GetColumnType());
        Assert.Equal("text", db.Model.FindEntityType(typeof(AuditEvent))!.FindProperty(nameof(AuditEvent.RawJson))!.GetColumnType());
        Assert.Equal("text", db.Model.FindEntityType(typeof(CollectionRun))!.FindProperty(nameof(CollectionRun.SourceFailureDetails))!.GetColumnType());
    }

    [Fact]
    public void Both_engines_expose_the_same_entities_with_the_same_columns()
    {
        // The two contexts must describe one model. If a future change adds an
        // entity or column under a provider branch, this catches it before the
        // migration sets silently diverge.
        using var sql = SqlServerContext();
        using var pg = PostgresContext();

        static IEnumerable<string> Shape(IModel m) => m.GetEntityTypes()
            .OrderBy(e => e.Name)
            .SelectMany(e => e.GetProperties().Select(p => $"{e.ClrType.Name}.{p.Name}:{p.ClrType.Name}:{(p.IsNullable ? "null" : "req")}"));

        Assert.Equal(Shape(sql.Model), Shape(pg.Model));
    }

    [Fact]
    public void Every_DateTimeOffset_maps_to_timestamptz_on_Postgres()
    {
        // Npgsql only round-trips DateTimeOffset as timestamptz, and only with a
        // zero offset. The first half is a mapping guarantee we can assert here;
        // the zero-offset half is enforced at runtime by Npgsql throwing, which
        // is the desired behaviour (see MSP plan §1.1).
        using var pg = PostgresContext();
        var offenders = pg.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => Nullable.GetUnderlyingType(p.ClrType) == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset))
            .Where(p => p.GetColumnType() != "timestamp with time zone")
            .Select(p => $"{p.DeclaringType.ClrType.Name}.{p.Name} -> {p.GetColumnType()}")
            .ToList();
        Assert.Empty(offenders);
    }

    // ── Migration attribution ─────────────────────────────────────────────────

    [Fact]
    public void Migrations_are_attributed_to_exactly_one_engine_each_and_neither_set_is_empty()
    {
        // EF matches migrations to a context by exact [DbContext] type. A Postgres
        // migration accidentally attributed to AppDbContext would be applied to
        // SQL Server on the next start of every install. This makes that a test
        // failure instead of an incident.
        var migrations = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Type: t, Ctx: t.GetCustomAttribute<DbContextAttribute>()?.ContextType))
            .ToList();

        Assert.NotEmpty(migrations);
        Assert.All(migrations, m => Assert.NotNull(m.Ctx));

        var sqlServer = migrations.Where(m => m.Ctx == typeof(AppDbContext)).ToList();
        var postgres = migrations.Where(m => m.Ctx == typeof(PostgresAppDbContext)).ToList();

        Assert.NotEmpty(sqlServer);
        Assert.NotEmpty(postgres);
        Assert.Equal(migrations.Count, sqlServer.Count + postgres.Count);

        // Directory discipline: the engine is readable from the namespace, so a
        // reviewer can tell at a glance which set a migration belongs to.
        Assert.All(sqlServer, m => Assert.Equal("M365SecurityDashboard.Api.Data.Migrations", m.Type.Namespace));
        Assert.All(postgres, m => Assert.Equal("M365SecurityDashboard.Api.Data.Migrations.Postgres", m.Type.Namespace));
    }

    [Fact]
    public void Each_context_resolves_only_its_own_migration_set()
    {
        using var sql = SqlServerContext();
        using var pg = PostgresContext();

        var sqlIds = sql.Database.GetMigrations().ToList();
        var pgIds = pg.Database.GetMigrations().ToList();

        Assert.NotEmpty(sqlIds);
        Assert.NotEmpty(pgIds);
        Assert.Empty(sqlIds.Intersect(pgIds));
        // The SQL Server history is pinned by every production install and must
        // still begin at the original baseline.
        Assert.StartsWith("20260704083927_InitialCreate", sqlIds.First());
    }

    // ── Raw-SQL seam ──────────────────────────────────────────────────────────

    [Fact]
    public void Database_size_query_is_engine_specific_and_reports_the_live_engine()
    {
        using var sql = SqlServerContext();
        using var pg = PostgresContext();

        Assert.Equal(DatabaseProvider.SqlServer, DatabaseProviderSetup.ProviderOf(sql));
        Assert.Equal(DatabaseProvider.Postgres, DatabaseProviderSetup.ProviderOf(pg));

        Assert.Contains("sys.database_files", DatabaseProviderSetup.DatabaseSizeSql(DatabaseProvider.SqlServer));
        Assert.Contains("pg_database_size", DatabaseProviderSetup.DatabaseSizeSql(DatabaseProvider.Postgres));
    }

    [Fact]
    public void InMemory_test_provider_is_treated_as_SqlServer_shaped()
    {
        // The rest of the suite runs on the in-memory provider. It must keep
        // taking the SQL Server branch so existing tests see exactly the model
        // they always did.
        using var db = TestAppDbContextFactory.Create();
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseProviderSetup.ProviderOf(db));
    }
}
