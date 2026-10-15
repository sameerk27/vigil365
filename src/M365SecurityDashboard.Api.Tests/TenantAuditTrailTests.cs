using System.Security.Claims;
using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The switcher sends the selected client on every call, admin ones included.
/// An audit entry must still name the client the action concerned (or none, for
/// an MSP-level action), and offboarding a client must neither lose its audit
/// trail nor go unrecorded.
/// </summary>
public sealed class TenantAuditTrailTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA, Fabrikam = TestTenancy.TenantB;

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, "admin@msp.test"), new Claim(ClaimTypes.Role, AppRoles.Admin)], "test"));

    private static async Task<EndpointHarness> HarnessAsync()
    {
        var h = new EndpointHarness(app => app.MapTenantEndpoints());
        await using var db = h.Db();
        db.ClientTenants.AddRange(
            new ClientTenant { Id = Contoso, Name = "Contoso", CreatedAt = DateTimeOffset.UtcNow },
            new ClientTenant { Id = Fabrikam, Name = "Fabrikam", CreatedAt = DateTimeOffset.UtcNow });
        db.AppUsers.Add(new AppUser { Email = "analyst@msp.test", Role = AppRoles.Analyst });
        await db.SaveChangesAsync();
        return h;
    }

    private static async Task WriteAsAsync(EndpointHarness h, Guid? tenant, string action)
    {
        await using var scope = h.Scope(tenant);
        await scope.ServiceProvider.GetRequiredService<AuditLogger>().WriteAsync(action, "thing", null, null, CancellationToken.None);
    }

    private static async Task<List<AuditEntry>> ChainAsync(EndpointHarness h)
    {
        await using var db = h.Db();
        return await db.CrossTenant<AuditEntry>().AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    [Fact]
    public async Task Purging_a_client_keeps_its_audit_trail_records_the_purge_against_it_and_the_chain_still_verifies()
    {
        await using var h = await HarnessAsync();
        await WriteAsAsync(h, Fabrikam, "alert.resolve");
        await WriteAsAsync(h, null, "user.add");
        await WriteAsAsync(h, Contoso, "alert.acknowledge");
        await WriteAsAsync(h, Fabrikam, "alert.snooze");

        // Purged from the Clients page while Contoso is the selected client.
        var (status, _) = await h.SendAsync("DELETE", "/api/tenants/{id:guid}", Admin(), Contoso, query: "?purge=true", routeValues: new { id = Fabrikam });

        Assert.Equal(200, status);
        var chain = await ChainAsync(h);
        Assert.Equal(new[] { "alert.resolve", "alert.snooze" }, chain.Where(e => e.TenantId == Fabrikam && e.Action != "tenant.purge").Select(e => e.Action));
        var purge = chain.Single(e => e.Action == "tenant.purge");
        Assert.Equal(Fabrikam, purge.TenantId);
        Assert.Equal("Fabrikam", purge.Details);
        Assert.True(AuditLogger.VerifyChain(chain).Valid);
    }

    [Fact]
    public async Task Client_actions_are_recorded_against_that_client_and_msp_actions_against_none()
    {
        await using var h = await HarnessAsync();

        await h.SendAsync("PUT", "/api/tenants/{id:guid}", Admin(), Contoso, routeValues: new { id = Fabrikam },
            body: new TenantEndpoints.TenantUpsert("Fabrikam Ltd", null, null, true));
        var (created, body) = await h.SendAsync("POST", "/api/tenants/", Admin(), Contoso, body: new TenantEndpoints.TenantUpsert("Northwind", null, null, true));
        await h.SendAsync("PUT", "/api/tenants/assignments/{email}", Admin(), Contoso, routeValues: new { email = "analyst@msp.test" },
            body: new TenantEndpoints.AssignmentsUpdate([Fabrikam]));

        Assert.Equal(201, created);
        var northwind = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        var chain = await ChainAsync(h);
        Assert.Equal(Fabrikam, chain.Single(e => e.Action == "tenant.update").TenantId);
        Assert.Equal(northwind, chain.Single(e => e.Action == "tenant.create").TenantId);
        Assert.Null(chain.Single(e => e.Action == "tenant.assign").TenantId);
        Assert.True(AuditLogger.VerifyChain(chain).Valid);
    }

    [Fact]
    public async Task The_msp_audit_log_shows_a_purged_clients_entries_as_a_removed_client_not_as_msp_level()
    {
        // NotificationSender: registered (never resolved) so the user-invite
        // endpoints' parameters are not taken for request bodies.
        await using var h = new EndpointHarness(app => app.MapAdminEndpoints(), services: s => s.AddScoped<NotificationSender>());
        await using (var db = h.Db())
        {
            db.ClientTenants.Add(new ClientTenant { Id = Contoso, Name = "Contoso", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await WriteAsAsync(h, Fabrikam, "alert.resolve"); // Fabrikam has since been purged
        await WriteAsAsync(h, Contoso, "alert.acknowledge");
        await WriteAsAsync(h, null, "user.add");

        var (status, body) = await h.SendAsync("GET", "/api/admin/audit-log", Admin(), Contoso);

        Assert.Equal(200, status);
        var names = JsonDocument.Parse(body).RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("action").GetString()!, e => e.GetProperty("tenantName").GetString());
        Assert.Equal("Removed client", names["alert.resolve"]);
        Assert.Equal("Contoso", names["alert.acknowledge"]);
        Assert.Null(names["user.add"]);
    }
}
