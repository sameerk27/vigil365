using M365SecurityDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Test helper that returns a fresh in-memory <see cref="AppDbContext"/>
/// for each call. Each test gets its own database (named by a unique Guid)
/// so state cannot leak between tests.
/// </summary>
internal static class TestAppDbContextFactory
{
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
