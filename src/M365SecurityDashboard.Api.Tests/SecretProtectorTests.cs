using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class SecretProtectorTests
{
    private readonly SecretProtector _protector;

    public SecretProtectorTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _protector = new SecretProtector(provider, NullLogger<SecretProtector>.Instance);
    }

    [Fact]
    public void Protect_Unprotect_RoundTrip_ReturnsOriginalPlaintext()
    {
        // Arrange
        var secret = "SuperSecretPassword123!";

        // Act
        var encrypted = _protector.Protect(secret);
        var decrypted = _protector.Unprotect(encrypted);

        // Assert
        Assert.NotNull(encrypted);
        Assert.StartsWith("dp:", encrypted!);
        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, decrypted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmpty_ReturnsInput(string? input)
    {
        Assert.Equal(input, _protector.Protect(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unprotect_NullOrEmpty_ReturnsInput(string? input)
    {
        Assert.Equal(input, _protector.Unprotect(input));
    }

    [Fact]
    public void Protect_AlreadyProtectedDpPrefix_ReturnsUnchanged()
    {
        var alreadyEncrypted = "dp:someencryptedpayload";
        Assert.Equal(alreadyEncrypted, _protector.Protect(alreadyEncrypted));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unprotect_MalformedDpPayload_ReturnsNullAndDoesNotThrow()
    {
        var malformed = "dp:thisisnotavalidbase64orprotectedpayload";
        var result = _protector.Unprotect(malformed);
        Assert.Null(result);
    }
}
