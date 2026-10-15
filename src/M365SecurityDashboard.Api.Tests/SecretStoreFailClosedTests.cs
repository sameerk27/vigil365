using System.Security.Cryptography;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// MSP review F1: a client secret must never be stored unencrypted, and a secret the key
/// ring can no longer read must be reported on the client rather than silently dropped.
/// </summary>
public sealed class SecretStoreFailClosedTests
{
    /// <summary>A key ring that cannot be written to or read from.</summary>
    private sealed class BrokenKeyRing : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => throw new CryptographicException("key ring not writable");
        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException("no matching key");
    }

    private static readonly SecretProtector Protector =
        new(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);

    [Fact]
    public void A_secret_that_cannot_be_encrypted_is_refused_and_never_returned_in_plain_text()
    {
        var sut = new SecretProtector(new BrokenKeyRing(), NullLogger<SecretProtector>.Instance);

        var ex = Assert.Throws<InvalidOperationException>(() => sut.Protect("s3cret-value"));

        Assert.Contains("nothing was saved", ex.Message);
        Assert.DoesNotContain("s3cret-value", ex.Message);
    }

    [Fact]
    public void A_stored_secret_from_another_key_ring_is_reported_as_unreadable()
    {
        var other = new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);
        var row = new ClientTenant { ClientId = "app-id", ClientSecret = other.Protect("client-secret") };

        Assert.True(Sut().OwnSecretUnreadable(row));
    }

    [Fact]
    public void A_secret_this_key_ring_can_read_is_not_unreadable()
    {
        var row = new ClientTenant { ClientId = "app-id", ClientSecret = Protector.Protect("client-secret") };

        Assert.False(Sut().OwnSecretUnreadable(row));
    }

    [Fact]
    public void A_client_with_a_certificate_has_no_secret_to_be_unreadable()
    {
        var row = new ClientTenant { ClientId = "app-id", ClientSecret = null, CertificateThumbprint = "ABCDEF0123456789" };

        Assert.False(Sut().OwnSecretUnreadable(row));
    }

    private static TenantGraphCredentials Sut()
    {
        using var db = TestAppDbContextFactory.Create();
        return new TenantGraphCredentials(db, TestTenancy.For(TestTenancy.Default), Options.Create(new GraphOptions()),
            Protector, Options.Create(new EditionOptions { Mode = EditionMode.Msp }));
    }
}
