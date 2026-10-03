using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// A scheduled digest is one organisation's security posture. In MSP mode a
/// schedule therefore belongs to the client it was made for, and is only ever
/// built and sent in that client; one with no client is never sent (it would be
/// visible in every client's pass and go out with whichever ran first).
/// </summary>
public sealed class ReportScheduleScopeTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA, Fabrikam = TestTenancy.TenantB;
    private const string NoSmtp = "failed: SMTP email is not configured";

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, "admin@msp.test"), new Claim(ClaimTypes.Role, AppRoles.Admin)], "test"));

    private static ReportSchedule Due(string name, Guid? tenant) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenant, Name = name, Cadence = "daily", HourUtc = 0,
        Recipients = "ciso@client.test", CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
    };

    // ── Endpoints ──────────────────────────────────────────────────────────────

    // DigestBuilder is registered (never resolved) so the preview endpoint's
    // parameter is not taken for a request body.
    private static EndpointHarness Harness(EditionMode mode = EditionMode.Msp)
        => new(app => app.MapReportsEndpoints(), mode, services: s => s.AddScoped<DigestBuilder>());

    [Fact]
    public async Task In_msp_mode_a_new_schedule_belongs_to_the_selected_client()
    {
        await using var h = Harness();
        var (status, _) = await h.SendAsync("POST", "/api/report-schedules", Admin(), Contoso, body: new ReportSchedule { Name = "Weekly", Recipients = "ciso@contoso.test" });

        Assert.Equal(200, status);
        await using var db = h.Db(Contoso);
        Assert.Equal(Contoso, (await db.ReportSchedules.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task In_msp_mode_a_schedule_cannot_be_made_with_no_client_selected()
    {
        await using var h = Harness();
        var (status, body) = await h.SendAsync("POST", "/api/report-schedules", Admin(), tenant: null, body: new ReportSchedule { Name = "Weekly", Recipients = "ciso@contoso.test" });

        Assert.Equal(400, status);
        Assert.Contains("Select a client", body);
        await using var db = h.Db();
        Assert.False(await db.CrossTenant<ReportSchedule>().AnyAsync());
    }

    [Fact]
    public async Task Single_mode_schedules_stay_install_wide()
    {
        await using var h = Harness(EditionMode.Single);
        var (status, _) = await h.SendAsync("POST", "/api/report-schedules", Admin(), Contoso, body: new ReportSchedule { Name = "Weekly", Recipients = "ciso@org.test" });

        Assert.Equal(200, status);
        await using var db = h.Db(Contoso);
        Assert.Null((await db.ReportSchedules.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task In_msp_mode_run_now_refuses_a_schedule_with_no_client()
    {
        await using var h = Harness();
        var unassigned = Due("Made before MSP mode", null);
        await using (var db = h.Db(Contoso)) { db.ReportSchedules.Add(unassigned); await db.SaveChangesAsync(); }

        var (status, _) = await h.SendAsync("POST", "/api/report-schedules/{id:guid}/run-now", Admin(), Contoso, routeValues: new { id = unassigned.Id });

        Assert.Equal(400, status);
        await using var check = h.Db(Contoso);
        Assert.Null((await check.ReportSchedules.SingleAsync()).LastRunAt); // nothing was sent
    }

    // ── Worker ─────────────────────────────────────────────────────────────────

    private static (ServiceProvider Services, DbContextOptions<AppDbContext> Db) Install(params Guid[] tenants)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var services = new ServiceCollection()
            .AddScoped<TenantContext>()
            .AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>())
            .AddScoped(sp => new AppDbContext(options, sp.GetRequiredService<ITenantContext>()))
            .BuildServiceProvider();
        using var db = new AppDbContext(options, TestTenancy.None());
        foreach (var (t, i) in tenants.Select((t, i) => (t, i)))
            db.ClientTenants.Add(new ClientTenant { Id = t, Name = $"Client {i}", IsActive = true, CreatedAt = DateTimeOffset.UtcNow.AddDays(-10 + i) });
        db.SaveChanges();
        return (services, options);
    }

    private static async Task<Dictionary<string, string?>> StatusesAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options, TestTenancy.None());
        return await db.CrossTenant<ReportSchedule>().AsNoTracking().ToDictionaryAsync(s => s.Name, s => s.LastRunStatus);
    }

    [Fact]
    public async Task In_msp_mode_the_worker_sends_each_clients_schedules_and_never_one_with_no_client()
    {
        var (services, options) = Install(Contoso, Fabrikam);
        await using var _ = services;
        await using (var a = new AppDbContext(options, TestTenancy.For(Contoso))) { a.ReportSchedules.AddRange(Due("unassigned", null), Due("contoso", Contoso)); await a.SaveChangesAsync(); }
        await using (var b = new AppDbContext(options, TestTenancy.For(Fabrikam))) { b.ReportSchedules.Add(Due("fabrikam", Fabrikam)); await b.SaveChangesAsync(); }

        await ReportScheduleWorker.RunOnceAsync(services, msp: true, NullLogger.Instance, CancellationToken.None);

        var status = await StatusesAsync(options);
        // Dispatched (no SMTP here, so it records why it could not send) ...
        Assert.Equal(NoSmtp, status["contoso"]);
        Assert.Equal(NoSmtp, status["fabrikam"]);
        // ... but never with some client's digest, and the Reports page says why.
        Assert.Equal(ReportScheduleWorker.UnassignedStatus, status["unassigned"]);
    }

    [Fact]
    public async Task In_single_mode_the_install_wide_schedule_is_still_sent()
    {
        var (services, options) = Install(Contoso);
        await using var _ = services;
        await using (var a = new AppDbContext(options, TestTenancy.For(Contoso))) { a.ReportSchedules.Add(Due("weekly", null)); await a.SaveChangesAsync(); }

        await ReportScheduleWorker.RunOnceAsync(services, msp: false, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(NoSmtp, (await StatusesAsync(options))["weekly"]);
    }
}
