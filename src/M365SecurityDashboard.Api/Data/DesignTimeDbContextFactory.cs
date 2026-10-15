using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// Used only by the `dotnet ef` design-time tools. Prevents the tools from
/// booting the real Program (which would run DB retries and seeding just to
/// scaffold a migration). The connection strings are never opened during
/// `migrations add` — they only anchor the provider.
///
/// One factory per context type, because each context owns one engine's
/// migration set. Scaffold with:
///   SQL Server:  dotnet ef migrations add Name --context AppDbContext --output-dir Data/Migrations
///   Postgres:    dotnet ef migrations add Name --context PostgresAppDbContext --output-dir Data/Migrations/Postgres
/// Every model change needs BOTH, and scripts/check-migrations.ps1 fails CI if
/// either set has drifted from the model.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseProviderSetup.Configure(options, DatabaseProvider.SqlServer,
            "Server=.\\SQLEXPRESS;Database=M365SecurityDashboard;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True");
        return new AppDbContext(options.Options);
    }
}

public sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresAppDbContext>();
        DatabaseProviderSetup.Configure(options, DatabaseProvider.Postgres,
            "Host=localhost;Database=vigil365;Username=vigil365;Password=design-time-only");
        return new PostgresAppDbContext(options.Options);
    }
}
