using System.Security.Claims;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// MSP mode: an install-wide policy (TenantId null) runs for every client, so an
/// Analyst assigned to one client must not be able to create, change, delete or
/// import one — the same reason switching one off for a client is Admin-only.
/// A client's own policies keep the Analyst rules; single mode is unchanged.
/// </summary>
public sealed class PolicyScopeAuthorizationTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA;

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, $"{role.ToLowerInvariant()}@msp.test"), new Claim(ClaimTypes.Role, role)], "test"));

    private static AlertPolicy Policy(string name, Guid? tenant, int threshold = 1) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenant, Name = name, Enabled = true, Category = "identity",
        Metric = "riskyUsersCount", Threshold = threshold, Severity = "high", Condition = "Risky users >= 1",
    };

    private static async Task<(EndpointHarness H, AlertPolicy Shared, AlertPolicy Own)> HarnessAsync(EditionMode mode = EditionMode.Msp)
    {
        // Services of this module's other endpoints: registered so their
        // parameters are not taken for request bodies; never resolved here.
        var h = new EndpointHarness(app => app.MapAlertsEndpoints(), mode, services: s =>
        {
            s.AddScoped<AlertEvaluator>();
            s.AddScoped<PolicyBacktester>();
        });
        var shared = Policy("Risky users (every client)", null);
        var own = Policy("Risky users (Contoso)", Contoso);
        await using var db = h.Db(Contoso);
        db.AlertPolicies.AddRange(shared, own);
        await db.SaveChangesAsync();
        return (h, shared, own);
    }

    private static async Task<AlertPolicy?> ReloadAsync(EndpointHarness h, Guid id)
    {
        await using var db = h.Db(Contoso);
        return await db.AlertPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
    }

    [Fact]
    public async Task An_analyst_cannot_change_or_delete_an_install_wide_policy()
    {
        var (h, shared, _) = await HarnessAsync();
        await using var _h = h;
        var analyst = User(AppRoles.Analyst);

        var edited = Policy(shared.Name, null, threshold: 100_000); edited.Enabled = false;
        var (put, _) = await h.SendAsync("PUT", "/api/alert-policies/{id:guid}", analyst, Contoso, routeValues: new { id = shared.Id }, body: edited);
        var (delete, _) = await h.SendAsync("DELETE", "/api/alert-policies/{id:guid}", analyst, Contoso, routeValues: new { id = shared.Id });

        Assert.Equal(403, put);
        Assert.Equal(403, delete);
        var row = await ReloadAsync(h, shared.Id);
        Assert.NotNull(row);
        Assert.True(row.Enabled);
        Assert.Equal(1, row.Threshold);
    }

    [Fact]
    public async Task An_analyst_cannot_create_an_install_wide_policy_but_can_create_one_for_their_client()
    {
        var (h, _, _) = await HarnessAsync();
        await using var _h = h;
        var analyst = User(AppRoles.Analyst);

        var (shared, _) = await h.SendAsync("POST", "/api/alert-policies", analyst, Contoso, body: Policy("New for everyone", null));
        var (own, _) = await h.SendAsync("POST", "/api/alert-policies", analyst, Contoso, query: "?scope=tenant", body: Policy("New for Contoso", null));

        Assert.Equal(403, shared);
        Assert.Equal(200, own);
        await using var db = h.Db(Contoso);
        Assert.False(await db.AlertPolicies.AnyAsync(p => p.Name == "New for everyone"));
        Assert.Equal(Contoso, (await db.AlertPolicies.SingleAsync(p => p.Name == "New for Contoso")).TenantId);
    }

    [Fact]
    public async Task An_analyst_keeps_full_control_of_their_clients_own_policies()
    {
        var (h, _, own) = await HarnessAsync();
        await using var _h = h;
        var analyst = User(AppRoles.Analyst);

        // (DELETE is not run here: it clears overrides with ExecuteDelete, which the
        // in-memory provider lacks. Its check is the same line as PUT's.)
        var (put, _) = await h.SendAsync("PUT", "/api/alert-policies/{id:guid}", analyst, Contoso, routeValues: new { id = own.Id }, body: Policy(own.Name, Contoso, threshold: 7));
        Assert.Equal(200, put);
        Assert.Equal(7, (await ReloadAsync(h, own.Id))!.Threshold);
    }

    [Fact]
    public async Task An_analyst_cannot_import_a_pack_that_would_write_install_wide_policies()
    {
        var (h, shared, _) = await HarnessAsync();
        await using var _h = h;
        var analyst = User(AppRoles.Analyst);
        var pack = new PolicyPack.Pack(PolicyPack.CurrentVersion, DateTimeOffset.UtcNow, "test", false,
        [
            PolicyPack.ToPack(Policy("Imported for everyone", null), false),
            PolicyPack.ToPack(Policy(shared.Name, null, threshold: 50), false),
        ]);

        var (status, _) = await h.SendAsync("POST", "/api/alert-policies/import", analyst, Contoso, query: "?mode=update", body: pack);

        Assert.Equal(403, status);
        await using var db = h.Db(Contoso);
        Assert.False(await db.AlertPolicies.AnyAsync(p => p.Name == "Imported for everyone")); // nothing half-applied
        Assert.Equal(1, (await ReloadAsync(h, shared.Id))!.Threshold);
    }

    [Fact]
    public async Task An_analyst_cannot_switch_on_install_wide_coverage()
    {
        var (h, _, _) = await HarnessAsync();
        await using var _h = h;

        var (status, _) = await h.SendAsync("POST", "/api/alert-coverage/enable/{id}", User(AppRoles.Analyst), Contoso, routeValues: new { id = "base-02" });

        Assert.Equal(403, status);
        await using var db = h.Db(Contoso);
        Assert.False(await db.AlertPolicies.AnyAsync(p => p.Name == "MFA Not Registered"));
    }

    [Fact]
    public async Task An_admin_may_change_install_wide_policies()
    {
        var (h, shared, _) = await HarnessAsync();
        await using var _h = h;

        var (put, _) = await h.SendAsync("PUT", "/api/alert-policies/{id:guid}", User(AppRoles.Admin), Contoso, routeValues: new { id = shared.Id }, body: Policy(shared.Name, null, threshold: 4));
        var (coverage, _) = await h.SendAsync("POST", "/api/alert-coverage/enable/{id}", User(AppRoles.Admin), Contoso, routeValues: new { id = "base-02" });

        Assert.Equal(200, put);
        Assert.Equal(4, (await ReloadAsync(h, shared.Id))!.Threshold);
        Assert.Equal(200, coverage);
    }

    [Fact]
    public async Task Single_mode_is_unchanged_an_analyst_edits_the_organisations_policies()
    {
        var (h, shared, _) = await HarnessAsync(EditionMode.Single);
        await using var _h = h;

        var (put, _) = await h.SendAsync("PUT", "/api/alert-policies/{id:guid}", User(AppRoles.Analyst), Contoso, routeValues: new { id = shared.Id }, body: Policy(shared.Name, null, threshold: 4));

        Assert.Equal(200, put);
        Assert.Equal(4, (await ReloadAsync(h, shared.Id))!.Threshold);
    }

    [Fact]
    public async Task Changes_to_an_install_wide_policy_are_audited_as_msp_level_not_against_the_selected_client()
    {
        var (h, shared, own) = await HarnessAsync();
        await using var _h = h;

        await h.SendAsync("PUT", "/api/alert-policies/{id:guid}", User(AppRoles.Admin), Contoso, routeValues: new { id = shared.Id }, body: Policy(shared.Name, null, threshold: 4));
        await h.SendAsync("PUT", "/api/alert-policies/{id:guid}", User(AppRoles.Admin), Contoso, routeValues: new { id = own.Id }, body: Policy(own.Name, Contoso, threshold: 4));

        await using var db = h.Db(Contoso);
        var entries = await db.AuditEntries.AsNoTracking().Where(a => a.Action == "policy.update").ToListAsync();
        Assert.Null(entries.Single(a => a.TargetId == shared.Id.ToString()).TenantId);
        Assert.Equal(Contoso, entries.Single(a => a.TargetId == own.Id.ToString()).TenantId);
    }

    [Fact]
    public async Task A_client_override_keeps_its_address_when_only_on_off_or_threshold_is_sent()
    {
        // The override controls send enabled/threshold only; that must not wipe
        // the address the alerts for this client go to. "" still clears it.
        var (h, shared, _) = await HarnessAsync();
        await using var _h = h;
        var admin = User(AppRoles.Admin);
        const string Route = "/api/alert-policies/{id:guid}/tenant-override";

        await h.SendAsync("PUT", Route, admin, Contoso, routeValues: new { id = shared.Id }, body: new { threshold = 5, notifyEmail = "it-lead@contoso.test" });
        var (off, _) = await h.SendAsync("PUT", Route, admin, Contoso, routeValues: new { id = shared.Id }, body: new { enabled = false, threshold = (int?)null });
        Assert.Equal(200, off);
        async Task<AlertPolicyTenantOverride> OverrideAsync()
        {
            await using var db = h.Db(Contoso);
            return await db.AlertPolicyTenantOverrides.AsNoTracking().SingleAsync();
        }
        var kept = await OverrideAsync();
        Assert.Equal("it-lead@contoso.test", kept.NotifyEmail);
        Assert.False(kept.Enabled);

        await h.SendAsync("PUT", Route, admin, Contoso, routeValues: new { id = shared.Id }, body: new { enabled = false, notifyEmail = "" });
        Assert.Null((await OverrideAsync()).NotifyEmail);
    }
}
