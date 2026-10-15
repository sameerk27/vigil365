using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>MSP_V12_PLAN.md U6: the cross-client open-alert queue.</summary>
public sealed class CrossClientQueueTests
{
    private static readonly Guid A = TestTenancy.TenantA, B = TestTenancy.TenantB, C = TestTenancy.TenantC;

    private static AppDbContext Open(string dbName, Guid? tenant) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
        tenant is Guid t ? TestTenancy.For(t) : TestTenancy.None());

    private static TriggeredAlert Alert(string name, string severity, int minutesAgo, string status = "new") => new()
    {
        Id = Guid.NewGuid(), PolicyName = name, Severity = severity, Category = "identity", Condition = "x",
        TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), Status = status,
    };

    private static async Task<(string db, List<ClientTenant> tenants)> SeedAsync()
    {
        var db = Guid.NewGuid().ToString();
        var tenants = new List<ClientTenant>
        {
            new() { Id = A, Name = "Contoso", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = B, Name = "Fabrikam", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = C, Name = "Secret client", CreatedAt = DateTimeOffset.UtcNow },
        };
        await using (var g = Open(db, null)) { g.ClientTenants.AddRange(tenants); await g.SaveChangesAsync(); }
        await using (var a = Open(db, A))
        {
            a.TriggeredAlerts.AddRange(Alert("A-low-new", "low", 1), Alert("A-critical-old", "critical", 300), Alert("A-resolved", "critical", 2, "resolved"));
            await a.SaveChangesAsync();
        }
        await using (var b = Open(db, B)) { b.TriggeredAlerts.Add(Alert("B-high", "high", 5, "acknowledged")); await b.SaveChangesAsync(); }
        await using (var c = Open(db, C)) { c.TriggeredAlerts.Add(Alert("C-critical", "critical", 1)); await c.SaveChangesAsync(); }
        return (db, tenants);
    }

    private static TenantRollupService Service(AppDbContext db) => new(db, new TenantGraphCredentials(
        db, TestTenancy.None(), Options.Create(new GraphOptions()),
        new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
        Options.Create(new EditionOptions { Mode = EditionMode.Msp })));

    [Fact]
    public async Task Lists_open_alerts_of_permitted_clients_most_severe_first_with_client_names()
    {
        var (dbName, tenants) = await SeedAsync();
        await using var db = Open(dbName, null);
        var permitted = tenants.Where(t => t.Id != C).ToList(); // not allowed to see C

        var queue = await Service(db).OpenAlertsAcrossAsync(permitted, 100, CancellationToken.None);

        Assert.Equal(new[] { "A-critical-old", "B-high", "A-low-new" }, queue.Select(q => q.PolicyName));
        Assert.Equal(new[] { "Contoso", "Fabrikam", "Contoso" }, queue.Select(q => q.TenantName));
        Assert.DoesNotContain(queue, q => q.TenantId == C);          // never a client you can't see
        Assert.DoesNotContain(queue, q => q.PolicyName == "A-resolved"); // only open alerts
    }

    [Fact]
    public async Task No_permitted_clients_means_an_empty_queue()
    {
        var (dbName, _) = await SeedAsync();
        await using var db = Open(dbName, null);
        Assert.Empty(await Service(db).OpenAlertsAcrossAsync([], 100, CancellationToken.None));
    }

    [Fact]
    public async Task Respects_the_limit()
    {
        var (dbName, tenants) = await SeedAsync();
        await using var db = Open(dbName, null);
        var queue = await Service(db).OpenAlertsAcrossAsync(tenants, 2, CancellationToken.None);
        Assert.Equal(2, queue.Count);
        Assert.All(queue, q => Assert.Equal("critical", q.Severity));
    }

    [Fact]
    public async Task An_older_critical_alert_is_kept_however_many_newer_low_ones_there_are()
    {
        var (dbName, tenants) = await SeedAsync();
        await using (var b = Open(dbName, B))
        {
            // Far more newer low alerts than the limit (and any over-fetch of it).
            for (var i = 0; i < 10; i++) b.TriggeredAlerts.Add(Alert($"B-low-{i}", "Low", minutesAgo: 0));
            await b.SaveChangesAsync();
        }
        await using var db = Open(dbName, null);

        var queue = await Service(db).OpenAlertsAcrossAsync(tenants.Where(t => t.Id != C).ToList(), 1, CancellationToken.None);

        Assert.Equal("A-critical-old", Assert.Single(queue).PolicyName);
    }
}
