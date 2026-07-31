using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// API tokens authenticate SIEM pulls without a browser session, so they are a
/// standing credential. These lock the properties that matter: the secret is
/// never stored, tokens are unpredictable, and scope is actually enforced.
/// </summary>
public class ApiTokenServiceTests
{
    [Fact]
    public void Create_StoresOnlyAHash_NeverTheSecret()
    {
        var (row, raw) = ApiTokenService.Create("SIEM", "alerts:read", "admin@contoso.com", null);

        Assert.NotEqual(raw, row.TokenHash);
        Assert.DoesNotContain(raw, row.TokenHash);
        // A stolen database must not yield working tokens.
        Assert.Equal(ApiTokenService.Hash(raw), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length); // SHA-256 hex
    }

    [Fact]
    public void Create_PrefixIsShownableAndMatchesTheToken()
    {
        var (row, raw) = ApiTokenService.Create("SIEM", "alerts:read", null, null);
        // The prefix is what the UI lists to identify a token; it must be a real
        // prefix of the secret but far too short to be usable on its own.
        Assert.StartsWith(row.Prefix, raw);
        Assert.True(row.Prefix.Length <= 12);
        Assert.True(raw.Length > row.Prefix.Length + 20);
    }

    [Fact]
    public void Create_TokensAreUnpredictable()
    {
        var tokens = Enumerable.Range(0, 200)
            .Select(_ => ApiTokenService.Create("t", "alerts:read", null, null).rawToken)
            .ToList();
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.StartsWith("vig_", t));
    }

    [Fact]
    public void Hash_IsStableAndDistinct()
    {
        Assert.Equal(ApiTokenService.Hash("vig_abc"), ApiTokenService.Hash("vig_abc"));
        Assert.NotEqual(ApiTokenService.Hash("vig_abc"), ApiTokenService.Hash("vig_abd"));
    }

    [Theory]
    [InlineData("alerts:read", "alerts:read", true)]
    [InlineData("alerts:read,health:read", "health:read", true)]
    [InlineData("alerts:read", "health:read", false)]
    [InlineData("", "alerts:read", false)]
    [InlineData(null, "alerts:read", false)]
    public void HasScope_EnforcesTheRequestedScope(string? granted, string required, bool expected)
        => Assert.Equal(expected, ApiTokenService.HasScope(granted, required));

    [Fact]
    public void HasScope_WildcardGrantsEverything()
        => Assert.True(ApiTokenService.HasScope("*", "anything:read"));

    [Fact]
    public void HasScope_IsCaseInsensitiveOnTheScopeName_ButWildcardIsExact()
    {
        Assert.True(ApiTokenService.HasScope("Alerts:Read", "alerts:read"));
        // A literal "*" is the wildcard; a scope that merely contains one is not.
        Assert.False(ApiTokenService.HasScope("alerts:*", "health:read"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeScopes_FallsBackToTheReadOnlyDefault(string? input)
        => Assert.Equal("alerts:read,health:read", ApiTokenService.NormalizeScopes(input));

    [Fact]
    public void NormalizeScopes_TrimsAndDeduplicates()
        => Assert.Equal("alerts:read,health:read",
            ApiTokenService.NormalizeScopes(" alerts:read , health:read ,alerts:read "));

    [Fact]
    public void Create_UsesAFallbackNameRatherThanStoringBlank()
        => Assert.Equal("SIEM integration", ApiTokenService.Create("   ", "alerts:read", null, null).row.Name);

    [Fact]
    public void Create_RecordsExpiryAndCreator()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var (row, _) = ApiTokenService.Create("SIEM", "alerts:read", "admin@contoso.com", expires);
        Assert.Equal(expires, row.ExpiresAt);
        Assert.Equal("admin@contoso.com", row.CreatedBy);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.LastUsedAt);
    }
}
