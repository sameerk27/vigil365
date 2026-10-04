using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>Phase 8: backoff, parallel tenant iteration, per-tenant certificate auth.</summary>
public sealed class CollectionHardeningTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Cap = TimeSpan.FromHours(4);
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0)]      // no failures: retry immediately
    [InlineData(1, 30)]     // 15m × 2
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]    // 15m × 16 = 4h = cap
    [InlineData(5, 240)]    // capped
    [InlineData(40, 240)]   // absurd counts do not overflow
    public void Backoff_doubles_per_failure_and_caps(int failures, int expectedMinutes)
        => Assert.Equal(Now.AddMinutes(expectedMinutes), CollectionBackoff.NextAttempt(Now, failures, Interval, Cap));

    [Fact]
    public void Due_when_no_next_attempt_or_when_reached()
    {
        Assert.True(CollectionBackoff.IsDue(Now, null));
        Assert.True(CollectionBackoff.IsDue(Now, Now));
        Assert.False(CollectionBackoff.IsDue(Now, Now.AddMinutes(1)));
    }

    [Fact]
    public void Own_certificate_credentials_count_and_are_applied()
    {
        var protector = new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);
        using var db = TestAppDbContextFactory.Create();
        var creds = new TenantGraphCredentials(db, TestTenancy.For(TestTenancy.Default),
            Options.Create(new GraphOptions { TenantId = "x", ClientId = "install", ClientSecret = "s" }), protector,
            Options.Create(new EditionOptions()));

        var tenant = new ClientTenant
        {
            Name = "Cert", MicrosoftTenantId = "22222222-2222-2222-2222-222222222222", ClientId = "own",
            CertificateThumbprint = "AB CD ef", // no secret at all
        };
        Assert.True(tenant.HasOwnCredentials);
        var o = creds.Resolve(tenant);
        Assert.True(o.IsConfigured());
        Assert.True(o.HasCertificate());
        Assert.Equal("ABCDEF", o.CertificateThumbprint);
        Assert.Equal("", o.ClientSecret);
        Assert.Equal("own", o.ClientId);

        var withPfx = new ClientTenant { Name = "Pfx", MicrosoftTenantId = "3", ClientId = "own", CertificatePath = "/certs/c.pfx", CertificatePassword = protector.Protect("pw") };
        var p = creds.Resolve(withPfx);
        Assert.Equal("/certs/c.pfx", p.CertificatePath);
        Assert.Equal("pw", p.CertificatePassword);
    }

    private static ServiceProvider BuildServices(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Iterator_runs_tenants_in_parallel_each_in_its_own_scope_and_isolates_failures()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildServices(dbName);
        using (var seed = sp.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 6; i++)
                db.ClientTenants.Add(new ClientTenant { Name = "T" + i, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
            db.ClientTenants.Add(new ClientTenant { Name = "inactive", IsActive = false, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var seen = new System.Collections.Concurrent.ConcurrentBag<(Guid tenant, Guid? contextTenant)>();
        var concurrent = 0; var peak = 0;
        var starts = new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();

        await TenantIterator.ForEachActiveTenantAsync(sp, NullLogger.Instance, "test", async (scope, tenant, ct) =>
        {
            starts.Enqueue(DateTimeOffset.UtcNow);
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            var ctx = scope.GetRequiredService<ITenantContext>();
            seen.Add((tenant.Id, ctx.Current));
            await Task.Delay(150, ct);
            Interlocked.Decrement(ref concurrent);
            if (tenant.Name == "T2") throw new InvalidOperationException("boom"); // isolated
        }, CancellationToken.None, maxParallel: 3, stagger: TimeSpan.Zero);

        Assert.Equal(6, seen.Count);                       // inactive skipped; failure did not stop the loop
        Assert.All(seen, s => Assert.Equal(s.tenant, s.contextTenant)); // each scope's context was its own tenant
        Assert.True(peak >= 2 && peak <= 3, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task Iterator_with_parallelism_one_is_strictly_sequential()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildServices(dbName);
        using (var seed = sp.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 4; i++) db.ClientTenants.Add(new ClientTenant { Name = "T" + i, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
            await db.SaveChangesAsync();
        }
        var concurrent = 0; var peak = 0;
        await TenantIterator.ForEachActiveTenantAsync(sp, NullLogger.Instance, "test", async (_, _, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrent);
        }, CancellationToken.None, maxParallel: 1, stagger: TimeSpan.Zero);
        Assert.Equal(1, peak);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = target) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
