namespace M365SecurityDashboard.Api.Models;

/// <summary>Relational database engines Vigil365 can run on.</summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server (Express, Standard, Azure SQL). The default.</summary>
    SqlServer,

    /// <summary>PostgreSQL 14+.</summary>
    Postgres,
}

/// <summary>
/// Selects the database engine. Bound from the "Database" config section. The
/// connection string itself stays in ConnectionStrings:DefaultConnection so the
/// existing installers, Docker compose and docs keep working unchanged; only the
/// engine is new, and it defaults to SQL Server so every current install boots
/// exactly as before without touching config.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

    /// <summary>
    /// /health reports a size warning above this. Default 8 GiB: SQL Server Express
    /// stops accepting writes at 10 GB, and an MSP install must hear about that
    /// before it happens. Set 0 to disable.
    /// </summary>
    public long SizeWarningBytes { get; set; } = 8L * 1024 * 1024 * 1024;
}
