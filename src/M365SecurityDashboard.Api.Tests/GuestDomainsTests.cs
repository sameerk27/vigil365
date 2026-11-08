using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// A guest's UPN is <c>ana_contoso.com#EXT#@tenant.onmicrosoft.com</c>: the part after #EXT# is always
/// the host tenant, so grouping by it put every guest under the tenant's own domain.
/// </summary>
public class GuestDomainsTests
{
    [Theory]
    [InlineData("ana@contoso.com", "ana_contoso.com#EXT#@tenant.onmicrosoft.com", "contoso.com")]
    [InlineData(null, "luis_Fabrikam.es#EXT#@tenant.onmicrosoft.com", "fabrikam.es")]
    [InlineData("", "eva_mail.contoso.com#EXT#@tenant.onmicrosoft.com", "mail.contoso.com")]
    [InlineData(null, "partner@northwind.com", "northwind.com")]
    public void A_guest_is_counted_under_their_own_domain(string? mail, string upn, string expected) =>
        Assert.Equal(expected, GuestDomains.Of(mail, upn));

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "no-at-sign")]
    [InlineData(null, "nounderscore#EXT#@tenant.onmicrosoft.com")]
    public void Without_a_domain_nothing_is_counted(string? mail, string? upn) =>
        Assert.Null(GuestDomains.Of(mail, upn));
}
