using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// Used only by the `dotnet ef` design-time tools. Prevents the tools from
/// booting the real Program (which would run DB retries and seeding just to
/// scaffold a migration). The connection string is never opened during
/// `migrations add` — it only anchors the provider.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.\\SQLEXPRESS;Database=M365SecurityDashboard;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True")
            .Options;
        return new AppDbContext(options);
    }
}
