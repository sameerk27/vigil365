using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Whose Graph credentials a tenant uses. The rules keep a single-tenant install
/// working unchanged, reach a consented MSP client through the shared MSP app in
/// that client's own tenant, and make it impossible for an MSP client to be
/// collected from the MSP's tenant or with another client's credentials.
/// </summary>
public sealed class TenantGraphCredentialsTests
{
    private static readonly SecretProtector Protector =
        new(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);

    private static GraphOptions Global(string tenantId = "11111111-1111-1111-1111-111111111111") => new()
    {
        TenantId = tenantId, ClientId = "global-client", ClientSecret = "global-secret",
        CollectionIntervalMinutes = 7, SignInLookbackHours = 48, BaseUrl = "https://graph.microsoft.com",
    };

    private static TenantGraphCredentials Sut(GraphOptions global, EditionMode mode = EditionMode.Single)
    {
        using var db = TestAppDbContextFactory.Create();
        return new TenantGraphCredentials(db, TestTenancy.For(TestTenancy.Default), Options.Create(global), Protector, Edition(mode));
    }

    private static IOptions<EditionOptions> Edition(EditionMode mode = EditionMode.Single) => Options.Create(new EditionOptions { Mode = mode });

    [Fact]
    public void Single_mode_tenant_with_no_entra_id_uses_install_wide_credentials()
    {
        // Every upgraded single-tenant install: the default tenant has no Entra id recorded yet.
        var o = Sut(Global()).Resolve(new ClientTenant { Name = "Default" });
        Assert.True(o.IsConfigured());
        Assert.Equal("global-client", o.ClientId);
        Assert.Equal("global-secret", o.ClientSecret);
    }

    [Fact]
    public void Tenant_whose_entra_id_matches_the_install_credentials_uses_them()
    {
        var o = Sut(Global("11111111-1111-1111-1111-111111111111"))
            .Resolve(new ClientTenant { Name = "Same", MicrosoftTenantId = "11111111-1111-1111-1111-111111111111" });
        Assert.True(o.IsConfigured());
        Assert.Equal("global-client", o.ClientId);
    }

    [Fact]
    public void Single_mode_tenant_with_a_different_entra_id_and_no_own_credentials_is_unconfigured()
    {
        // A single-organisation install's credentials reach only its own tenant.
        var o = Sut(Global()).Resolve(new ClientTenant { Name = "Other", MicrosoftTenantId = "22222222-2222-2222-2222-222222222222" });
        Assert.False(o.IsConfigured());
        Assert.Equal("", o.ClientId);
        Assert.Equal("", o.ClientSecret);
        // Non-credential settings still come from the install.
        Assert.Equal(7, o.CollectionIntervalMinutes);
        Assert.Equal(48, o.SignInLookbackHours);
    }

    // ── MSP mode: the install's app is the shared multi-tenant MSP app ──

    [Fact]
    public void Msp_client_with_no_entra_id_is_unconfigured_never_the_msps_own_tenant()
    {
        // Added but not consented yet. Falling back to the install's credentials
        // would collect the MSP's own tenant and file it under this client.
        var o = Sut(Global(), EditionMode.Msp).Resolve(new ClientTenant { Name = "Contoso" });
        Assert.False(o.IsConfigured());
        Assert.Equal("", o.TenantId);
        Assert.Equal("", o.ClientId);
        Assert.Equal("", o.ClientSecret);
        Assert.Equal(7, o.CollectionIntervalMinutes);
    }

    [Fact]
    public void Msp_default_tenant_with_no_entra_id_is_the_install_own_tenant()
    {
        var o = Sut(Global(), EditionMode.Msp).Resolve(new ClientTenant { Id = ClientTenant.DefaultId, Name = "Default" });
        Assert.True(o.IsConfigured());
        Assert.Equal("11111111-1111-1111-1111-111111111111", o.TenantId);
        Assert.Equal("global-client", o.ClientId);
    }

    [Fact]
    public void Msp_client_whose_entra_id_is_the_install_tenant_uses_the_install_credentials()
    {
        var o = Sut(Global(), EditionMode.Msp)
            .Resolve(new ClientTenant { Name = "Us", MicrosoftTenantId = "11111111-1111-1111-1111-111111111111" });
        Assert.True(o.IsConfigured());
        Assert.Equal("11111111-1111-1111-1111-111111111111", o.TenantId);
        Assert.Equal("global-client", o.ClientId);
    }

    [Fact]
    public void Msp_consented_client_uses_the_shared_app_in_its_own_tenant()
    {
        // One-go onboarding: no per-client credentials; consent recorded the client's Entra id.
        var global = Global();
        global.CertificateThumbprint = "ABCDEF";
        var o = Sut(global, EditionMode.Msp).Resolve(new ClientTenant
        {
            Name = "Contoso", MicrosoftTenantId = "22222222-2222-2222-2222-222222222222", ConsentGrantedAt = DateTimeOffset.UtcNow,
        });
        Assert.True(o.IsConfigured());
        Assert.Equal("22222222-2222-2222-2222-222222222222", o.TenantId); // the client's tenant, never the MSP's
        Assert.Equal("global-client", o.ClientId);
        Assert.Equal("global-secret", o.ClientSecret);
        Assert.Equal("ABCDEF", o.CertificateThumbprint); // the shared app's own credential
        Assert.Equal(7, o.CollectionIntervalMinutes);
    }

    [Theory]
    [InlineData(EditionMode.Single)]
    [InlineData(EditionMode.Msp)]
    public void Own_credentials_win_and_the_secret_is_unprotected(EditionMode mode)
    {
        var o = Sut(Global(), mode).Resolve(new ClientTenant
        {
            Name = "Own",
            MicrosoftTenantId = "22222222-2222-2222-2222-222222222222",
            ClientId = "own-client",
            ClientSecret = Protector.Protect("own-secret"),
            LoginInstance = "https://login.microsoftonline.us",
            BaseUrl = "https://graph.microsoft.us",
        });
        Assert.True(o.IsConfigured());
        Assert.Equal("22222222-2222-2222-2222-222222222222", o.TenantId);
        Assert.Equal("own-client", o.ClientId);
        Assert.Equal("own-secret", o.ClientSecret);
        Assert.Equal("https://login.microsoftonline.us", o.LoginInstance);
        Assert.Equal("https://graph.microsoft.us", o.BaseUrl);
        Assert.Equal("", o.CertificateThumbprint); // an install-wide certificate never leaks into a client
    }

    [Fact]
    public void Own_credentials_override_even_when_the_install_is_unconfigured()
    {
        var unconfigured = new GraphOptions { TenantId = "YOUR_TENANT_ID", ClientId = "YOUR_APP_CLIENT_ID", ClientSecret = "YOUR_APP_CLIENT_SECRET" };
        var o = Sut(unconfigured).Resolve(new ClientTenant
        {
            Name = "Own", MicrosoftTenantId = "22222222-2222-2222-2222-222222222222",
            ClientId = "own-client", ClientSecret = Protector.Protect("own-secret"),
        });
        Assert.True(o.IsConfigured());
    }

    [Fact]
    public async Task No_tenant_context_resolves_to_the_install_credentials()
    {
        // /health and startup run without a tenant; they see the install-wide state.
        using var db = TestAppDbContextFactory.Create();
        var sut = new TenantGraphCredentials(db, TestTenancy.None(), Options.Create(Global()), Protector, Edition());
        Assert.True(await sut.IsConfiguredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Resolution_is_cached_within_a_scope()
    {
        using var db = TestAppDbContextFactory.Create();
        db.ClientTenants.Add(new ClientTenant { Id = TestTenancy.Default, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var sut = new TenantGraphCredentials(db, TestTenancy.For(TestTenancy.Default), Options.Create(Global()), Protector, Edition());

        var first = await sut.ResolveAsync(CancellationToken.None);
        var second = await sut.ResolveAsync(CancellationToken.None);
        Assert.Same(first, second);
    }

    [Fact]
    public void Resolving_never_mutates_the_shared_install_options()
    {
        var global = Global();
        _ = Sut(global).Resolve(new ClientTenant { Name = "Other", MicrosoftTenantId = "22222222-2222-2222-2222-222222222222" });
        _ = Sut(global, EditionMode.Msp).Resolve(new ClientTenant { Name = "Shared", MicrosoftTenantId = "33333333-3333-3333-3333-333333333333" });
        _ = Sut(global, EditionMode.Msp).Resolve(new ClientTenant { Name = "Unconsented" });
        Assert.Equal("11111111-1111-1111-1111-111111111111", global.TenantId);
        Assert.Equal("global-client", global.ClientId);
        Assert.Equal("global-secret", global.ClientSecret);
    }

    [Fact]
    public void Install_tenant_match_is_case_insensitive_and_never_blank()
    {
        var sut = Sut(Global());
        Assert.True(sut.IsInstallTenant("11111111-1111-1111-1111-111111111111"));
        Assert.True(sut.IsInstallTenant(" 11111111-1111-1111-1111-111111111111 ".ToUpperInvariant()));
        Assert.False(sut.IsInstallTenant("22222222-2222-2222-2222-222222222222"));
        Assert.False(sut.IsInstallTenant(""));
        Assert.False(sut.IsInstallTenant(null));
        Assert.False(Sut(Global("")).IsInstallTenant(""));
    }
}
