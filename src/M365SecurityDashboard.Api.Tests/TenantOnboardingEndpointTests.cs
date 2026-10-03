using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The onboarding endpoints that write a client's Entra tenant id: the anonymous
/// /consented callback (trusts only its signed, one-time state), /test and the
/// Admin edits. None may ever file the MSP's own tenant, or another client's,
/// under a client.
/// </summary>
public sealed class TenantOnboardingEndpointTests
{
    private const string InstallTenant = "11111111-1111-1111-1111-111111111111";
    private const string ClientEntraId = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid Contoso = TestTenancy.TenantA;

    private static async Task<EndpointHarness> HarnessAsync(params ClientTenant[] rows)
    {
        var h = new EndpointHarness(app => app.MapTenantEndpoints(), EditionMode.Msp,
            g => { g.TenantId = InstallTenant; g.ClientId = "msp-app"; g.ClientSecret = "msp-secret"; });
        await using var db = h.Db();
        db.ClientTenants.AddRange(rows);
        await db.SaveChangesAsync();
        return h;
    }

    private static ClientTenant Row(Guid id, string name) => new() { Id = id, Name = name, CreatedAt = DateTimeOffset.UtcNow };

    private static async Task<ClientTenant> ReloadAsync(EndpointHarness h, Guid id)
    {
        await using var db = h.Db();
        return await db.ClientTenants.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private static Task<(int Status, string Body)> CallbackAsync(EndpointHarness h, string state, string rest)
        => h.SendAsync("GET", "/consented", query: $"?state={Uri.EscapeDataString(state)}&{rest}");

    /// <summary>The state of the consent link the onboarding dialog hands out for this client.</summary>
    private static async Task<string> StateAsync(EndpointHarness h, Guid id)
    {
        var (status, body) = await h.SendAsync("GET", "/api/tenants/{id:guid}/consent-url",
            query: "?redirectUri=https%3A%2F%2Fvigil.msp.test%2Fconsented", routeValues: new { id });
        Assert.Equal(200, status);
        var url = new Uri(JsonDocument.Parse(body).RootElement.GetProperty("url").GetString()!);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query)["state"].ToString();
    }

    private static ClientTenant Consented(Guid id, string name, string entraId)
    {
        var row = Row(id, name);
        row.MicrosoftTenantId = entraId;
        return row;
    }

    [Fact]
    public async Task Forged_unsigned_state_changes_nothing()
    {
        await using var h = await HarnessAsync(Row(ClientTenant.DefaultId, "Default"));
        var forged = $"{ClientTenant.DefaultId:N}.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var (_, body) = await CallbackAsync(h, forged, $"admin_consent=True&tenant={ClientEntraId}");
        Assert.Contains("invalid or has expired", body);
        await CallbackAsync(h, forged, "error=access_denied&error_description=attacker+text");

        var row = await ReloadAsync(h, ClientTenant.DefaultId);
        Assert.Null(row.MicrosoftTenantId);
        Assert.Null(row.ConsentGrantedAt);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task Signed_state_records_the_client_tenant_and_consent()
    {
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));
        var state = await StateAsync(h, Contoso);

        var (status, body) = await CallbackAsync(h, state, $"admin_consent=True&tenant={ClientEntraId.ToUpperInvariant()}");

        Assert.Equal(200, status);
        Assert.Contains("Contoso is connected", body);
        var row = await ReloadAsync(h, Contoso);
        Assert.Equal(ClientEntraId, row.MicrosoftTenantId);
        Assert.NotNull(row.ConsentGrantedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-tenant")]
    [InlineData("contoso.onmicrosoft.com")]
    public async Task Tenant_parameter_must_be_a_tenant_id(string tenant)
    {
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));
        var state = await StateAsync(h, Contoso);

        var (_, body) = await CallbackAsync(h, state, $"admin_consent=True&tenant={Uri.EscapeDataString(tenant)}");

        Assert.DoesNotContain("is connected", body);
        var row = await ReloadAsync(h, Contoso);
        Assert.Null(row.MicrosoftTenantId);
        Assert.Null(row.ConsentGrantedAt);
    }

    [Fact]
    public async Task A_client_never_takes_the_msps_own_tenant_from_the_callback()
    {
        // The tenant parameter is not signed, and the MSP's own tenant id is public:
        // a client recorded with it would be collected from the MSP's tenant.
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));
        var state = await StateAsync(h, Contoso);

        var (_, body) = await CallbackAsync(h, state, $"admin_consent=True&tenant={InstallTenant}");

        Assert.Contains("own Microsoft tenant", body);
        var row = await ReloadAsync(h, Contoso);
        Assert.Null(row.MicrosoftTenantId);
        Assert.Null(row.ConsentGrantedAt);
        Assert.NotNull(row.LastError);
    }

    [Fact]
    public async Task The_install_own_row_may_record_the_install_tenant()
    {
        await using var h = await HarnessAsync(Row(ClientTenant.DefaultId, "Default"));
        var state = await StateAsync(h, ClientTenant.DefaultId);

        var (_, body) = await CallbackAsync(h, state, $"admin_consent=True&tenant={InstallTenant}");

        Assert.Contains("is connected", body);
        Assert.Equal(InstallTenant, (await ReloadAsync(h, ClientTenant.DefaultId)).MicrosoftTenantId);
    }

    [Fact]
    public async Task Testing_an_unconsented_client_never_reaches_the_msps_tenant()
    {
        // Before consent records the client's Entra id there are no credentials for
        // it — /test must fail rather than call Graph as the MSP and adopt its id.
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));

        var (status, body) = await h.SendAsync("POST", "/api/tenants/{id:guid}/test", routeValues: new { id = Contoso });

        Assert.Equal(400, status);
        Assert.Contains("No Graph credentials apply", body);
        Assert.Null((await ReloadAsync(h, Contoso)).MicrosoftTenantId);
    }

    [Fact]
    public async Task Consented_client_on_the_shared_app_is_reported_as_configured()
    {
        var consented = Row(Contoso, "Contoso");
        consented.MicrosoftTenantId = ClientEntraId;
        await using var h = await HarnessAsync(Row(ClientTenant.DefaultId, "Default"), consented, Row(TestTenancy.TenantB, "Fabrikam"));

        var (_, body) = await h.SendAsync("GET", "/api/tenants/{id:guid}", routeValues: new { id = Contoso });
        Assert.Contains("\"configured\":true", body);
        (_, body) = await h.SendAsync("GET", "/api/tenants/{id:guid}", routeValues: new { id = TestTenancy.TenantB });
        Assert.Contains("\"configured\":false", body); // not consented: never the MSP's own tenant
        (_, body) = await h.SendAsync("GET", "/api/tenants/{id:guid}", routeValues: new { id = ClientTenant.DefaultId });
        Assert.Contains("\"configured\":true", body);  // the install's own tenant
    }

    [Fact]
    public async Task A_consent_link_works_once()
    {
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));
        var state = await StateAsync(h, Contoso);
        var reopened = await StateAsync(h, Contoso); // reopening the dialog must not break the link already sent

        Assert.Contains("is connected", (await CallbackAsync(h, state, $"admin_consent=True&tenant={ClientEntraId}")).Body);
        var consentedAt = (await ReloadAsync(h, Contoso)).ConsentGrantedAt;

        // A replay inside the 30 minutes changes nothing, not even the error shown to staff.
        var (_, body) = await CallbackAsync(h, state, "error=access_denied&error_description=replayed");
        Assert.Contains("already been used", body);
        Assert.Contains("already been used", (await CallbackAsync(h, state, $"admin_consent=True&tenant={ClientEntraId}")).Body);
        var row = await ReloadAsync(h, Contoso);
        Assert.Null(row.LastError);
        Assert.Equal(consentedAt, row.ConsentGrantedAt);

        Assert.Contains("already been used", (await CallbackAsync(h, reopened, $"admin_consent=True&tenant={ClientEntraId}")).Body);

        // Re-consent (e.g. after a new permission) takes a new link, which works.
        Assert.Contains("is connected", (await CallbackAsync(h, await StateAsync(h, Contoso), $"admin_consent=True&tenant={ClientEntraId}")).Body);
    }

    [Fact]
    public async Task A_declined_consent_can_be_retried_with_the_same_link()
    {
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));
        var state = await StateAsync(h, Contoso);

        Assert.Contains("not granted", (await CallbackAsync(h, state, "error=access_denied")).Body);
        Assert.Contains("is connected", (await CallbackAsync(h, state, $"admin_consent=True&tenant={ClientEntraId}")).Body);
        Assert.Equal(ClientEntraId, (await ReloadAsync(h, Contoso)).MicrosoftTenantId);
    }

    [Fact]
    public async Task A_signed_state_without_the_row_s_nonce_is_refused()
    {
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"));
        await StateAsync(h, Contoso);
        var other = ConsentState.Encode(h.DataProtection, Contoso, ConsentState.NewNonce());

        Assert.Contains("already been used", (await CallbackAsync(h, other, $"admin_consent=True&tenant={ClientEntraId}")).Body);
        Assert.Null((await ReloadAsync(h, Contoso)).MicrosoftTenantId);
    }

    [Fact]
    public async Task A_client_never_takes_another_client_s_tenant_from_the_callback()
    {
        // Only the state is signed. Contoso's own admin edits tenant= to Fabrikam's
        // (public) Entra id before Contoso has consented: the shared app, which
        // Fabrikam did consent to, would then collect Fabrikam's data as Contoso's.
        const string FabrikamEntraId = "33333333-3333-3333-3333-333333333333";
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"), Consented(TestTenancy.TenantB, "Fabrikam", FabrikamEntraId));
        var state = await StateAsync(h, Contoso);

        var (_, body) = await CallbackAsync(h, state, $"admin_consent=True&tenant={FabrikamEntraId.ToUpperInvariant()}");

        Assert.DoesNotContain("is connected", body);
        Assert.DoesNotContain("Fabrikam", body); // the anonymous page names no other client
        var row = await ReloadAsync(h, Contoso);
        Assert.Null(row.MicrosoftTenantId);
        Assert.Null(row.ConsentGrantedAt);
        Assert.Contains("another client", row.LastError);
        // The link was not spent: the right admin can still use it.
        Assert.Contains("is connected", (await CallbackAsync(h, state, $"admin_consent=True&tenant={ClientEntraId}")).Body);
    }

    [Fact]
    public async Task An_admin_cannot_give_two_clients_the_same_tenant()
    {
        const string FabrikamEntraId = "33333333-3333-3333-3333-333333333333";
        await using var h = await HarnessAsync(Row(Contoso, "Contoso"), Consented(TestTenancy.TenantB, "Fabrikam", FabrikamEntraId));

        var (status, body) = await h.SendAsync("PUT", "/api/tenants/{id:guid}", routeValues: new { id = Contoso },
            body: new TenantEndpoints.TenantUpsert("Contoso", FabrikamEntraId.ToUpperInvariant(), null, null));
        Assert.Equal(409, status);
        Assert.Contains("Fabrikam", body);
        Assert.Null((await ReloadAsync(h, Contoso)).MicrosoftTenantId);

        (status, _) = await h.SendAsync("POST", "/api/tenants/", body: new TenantEndpoints.TenantUpsert("Fabrikam again", FabrikamEntraId, null, null));
        Assert.Equal(409, status);

        // Editing a client without changing its tenant is unaffected.
        (status, _) = await h.SendAsync("PUT", "/api/tenants/{id:guid}", routeValues: new { id = TestTenancy.TenantB },
            body: new TenantEndpoints.TenantUpsert("Fabrikam Inc", FabrikamEntraId, null, null));
        Assert.Equal(200, status);
    }
}
