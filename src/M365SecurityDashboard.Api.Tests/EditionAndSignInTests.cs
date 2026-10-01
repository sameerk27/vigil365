using System.Security.Claims;
using M365SecurityDashboard.Api.Models;
using Microsoft.Extensions.Configuration;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// MSP mode makes the app registration multi-tenant, so a client tenant's users
/// can obtain a token for it. These pin the rule that only the install's own
/// tenant may sign in, and that the edition defaults to Single.
/// </summary>
public sealed class EditionAndSignInTests
{
    private const string Mine = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private static ClaimsPrincipal WithTid(string claimType, string tid)
        => new(new ClaimsIdentity([new Claim(claimType, tid)], "Bearer"));

    [Fact]
    public void Own_tenant_token_is_allowed()
        => Assert.True(SignInTenantPin.IsAllowed(WithTid("tid", Mine), Mine));

    [Fact]
    public void Long_form_tenant_claim_is_recognised()
        => Assert.True(SignInTenantPin.IsAllowed(
            WithTid("http://schemas.microsoft.com/identity/claims/tenantid", Mine.ToUpperInvariant()), Mine));

    [Fact]
    public void Client_tenant_token_is_rejected()
        => Assert.False(SignInTenantPin.IsAllowed(WithTid("tid", Client), Mine));

    [Fact]
    public void Token_without_a_tenant_claim_is_rejected()
        => Assert.False(SignInTenantPin.IsAllowed(new ClaimsPrincipal(new ClaimsIdentity([], "Bearer")), Mine));

    [Fact]
    public void Missing_configured_tenant_rejects_everything()
    {
        Assert.False(SignInTenantPin.IsAllowed(WithTid("tid", Mine), null));
        Assert.False(SignInTenantPin.IsAllowed(WithTid("tid", Mine), "  "));
        Assert.False(SignInTenantPin.IsAllowed(null, Mine));
    }

    [Theory]
    [InlineData(null, EditionMode.Single)]
    [InlineData("Single", EditionMode.Single)]
    [InlineData("Msp", EditionMode.Msp)]
    [InlineData("msp", EditionMode.Msp)]
    public void Edition_binds_and_defaults_to_single(string? configured, EditionMode expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Edition:Mode"] = configured,
        }).Build();
        var options = config.GetSection(EditionOptions.SectionName).Get<EditionOptions>() ?? new EditionOptions();
        Assert.Equal(expected, options.Mode);
        Assert.Equal(expected == EditionMode.Msp, options.IsMsp);
    }
}
