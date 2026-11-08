using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class GuestSignInActivityTests
{
    [Fact]
    public void The_non_premium_refusal_means_the_licence_is_missing() =>
        Assert.True(GuestSignInActivity.NeedsPremium("""403 Forbidden: {"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant"}}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("""403 Forbidden: {"error":{"code":"Authorization_RequestDenied"}}""")]
    public void Any_other_failure_says_nothing_about_the_licence(string? error) =>
        Assert.False(GuestSignInActivity.NeedsPremium(error));

    [Fact]
    public void Only_the_fallback_leaves_out_sign_in_activity()
    {
        Assert.Contains("signInActivity", GuestSignInActivity.WithActivityPath);
        Assert.DoesNotContain("signInActivity", GuestSignInActivity.WithoutActivityPath);
    }
}
