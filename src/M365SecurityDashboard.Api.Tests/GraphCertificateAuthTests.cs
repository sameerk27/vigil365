using Azure.Identity;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Certificate-auth contract: cert config is preferred over the client secret,
/// the secret remains a working fallback, and IsConfigured accepts either.
/// </summary>
public class GraphCertificateAuthTests
{
    private static GraphOptions Base() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
    };

    [Fact]
    public void IsConfigured_SecretOnly_True()
    {
        var o = Base(); o.ClientSecret = "s3cret";
        Assert.True(o.IsConfigured());
        Assert.True(o.HasSecret());
        Assert.False(o.HasCertificate());
    }

    [Fact]
    public void IsConfigured_CertificateOnly_True()
    {
        var o = Base(); o.CertificateThumbprint = "AABBCCDDEEFF";
        Assert.True(o.IsConfigured());
        Assert.True(o.HasCertificate());
        Assert.False(o.HasSecret());
    }

    [Fact]
    public void IsConfigured_NoCredential_False()
    {
        var o = Base();
        Assert.False(o.IsConfigured());
    }

    [Fact]
    public void IsConfigured_PlaceholderSecret_NotACredential()
    {
        var o = Base(); o.ClientSecret = "YOUR_APP_CLIENT_SECRET";
        Assert.False(o.IsConfigured());
    }

    [Fact]
    public void BuildCredential_SecretOnly_UsesClientSecretCredential()
    {
        var o = Base(); o.ClientSecret = "s3cret";
        Assert.IsType<ClientSecretCredential>(GraphApiClient.BuildCredential(o));
    }

    [Fact]
    public void BuildCredential_MissingPfxFile_ThrowsClearError()
    {
        var o = Base(); o.CertificatePath = @"Z:\does\not\exist.pfx";
        var ex = Assert.Throws<InvalidOperationException>(() => GraphApiClient.BuildCredential(o));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void BuildCredential_UnknownThumbprint_ThrowsClearError()
    {
        var o = Base(); o.CertificateThumbprint = "0000000000000000000000000000000000000000";
        var ex = Assert.Throws<InvalidOperationException>(() => GraphApiClient.BuildCredential(o));
        Assert.Contains("thumbprint", ex.Message);
    }

    [Fact]
    public void BuildCredential_PfxFile_UsesCertificateCredential()
    {
        // Create a throwaway self-signed cert, export to PFX, load it back.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=vigil365-test", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfxPath = Path.Combine(Path.GetTempPath(), $"vigil365-test-{Guid.NewGuid():N}.pfx");
        try
        {
            File.WriteAllBytes(pfxPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw"));
            var o = Base(); o.CertificatePath = pfxPath; o.CertificatePassword = "pw";
            Assert.IsType<ClientCertificateCredential>(GraphApiClient.BuildCredential(o));
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }

    [Fact]
    public void BuildCredential_CertificatePreferredOverSecret()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=vigil365-test", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfxPath = Path.Combine(Path.GetTempPath(), $"vigil365-test-{Guid.NewGuid():N}.pfx");
        try
        {
            File.WriteAllBytes(pfxPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw"));
            var o = Base();
            o.ClientSecret = "s3cret";                 // both configured
            o.CertificatePath = pfxPath; o.CertificatePassword = "pw";
            Assert.IsType<ClientCertificateCredential>(GraphApiClient.BuildCredential(o)); // cert wins
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }
}
