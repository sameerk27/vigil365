using Microsoft.EntityFrameworkCore;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class GraphCollectorAlertCountTests : IDisposable
{
    private readonly AppDbContext _db;

    public GraphCollectorAlertCountTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task AlertCounting_CountsCriticalAndHighAcrossAllServices()
    {
        // Add critical alerts across multiple services
        _db.SecurityAlerts.Add(new SecurityAlert { Id = 1, Title = "A1", Severity = AlertSeverity.Critical, Service = M365ServiceArea.DefenderXdr, IsResolved = false, DetectedAt = DateTimeOffset.UtcNow });
        _db.SecurityAlerts.Add(new SecurityAlert { Id = 2, Title = "A2", Severity = AlertSeverity.Critical, Service = M365ServiceArea.EntraId, IsResolved = false, DetectedAt = DateTimeOffset.UtcNow });
        _db.SecurityAlerts.Add(new SecurityAlert { Id = 3, Title = "A3", Severity = AlertSeverity.Critical, Service = M365ServiceArea.Intune, IsResolved = false, DetectedAt = DateTimeOffset.UtcNow });
        
        // Add high alerts across multiple services
        _db.SecurityAlerts.Add(new SecurityAlert { Id = 4, Title = "A4", Severity = AlertSeverity.High, Service = M365ServiceArea.DefenderXdr, IsResolved = false, DetectedAt = DateTimeOffset.UtcNow });
        _db.SecurityAlerts.Add(new SecurityAlert { Id = 5, Title = "A5", Severity = AlertSeverity.High, Service = M365ServiceArea.ExchangeOnline, IsResolved = false, DetectedAt = DateTimeOffset.UtcNow });
        
        // Add a resolved alert (should not be counted)
        _db.SecurityAlerts.Add(new SecurityAlert { Id = 6, Title = "A6", Severity = AlertSeverity.Critical, Service = M365ServiceArea.DefenderXdr, IsResolved = true, DetectedAt = DateTimeOffset.UtcNow });

        await _db.SaveChangesAsync();

        var open = _db.SecurityAlerts.Where(a => !a.IsResolved);
        
        var criticalAlertsCount = await open.CountAsync(a => a.Severity == AlertSeverity.Critical);
        var highAlertsCount = await open.CountAsync(a => a.Severity == AlertSeverity.High);

        Assert.Equal(3, criticalAlertsCount);
        Assert.Equal(2, highAlertsCount);
    }
}
