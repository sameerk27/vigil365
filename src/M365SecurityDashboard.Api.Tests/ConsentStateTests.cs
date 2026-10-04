using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The signed <c>state</c> that ties an anonymous /consented callback back to the
/// exact tenant being onboarded. It must round-trip, reject tampering, and expire —
/// it is the only thing the unauthenticated landing page can trust.
/// </summary>
public sealed class ConsentStateTests
{
    private static IDataProtectionProvider Keys() => new EphemeralDataProtectionProvider();

    [Fact]
    public void Round_trips_the_tenant_id_and_nonce()
    {
        var p = Keys();
        var id = Guid.NewGuid();
        var nonce = ConsentState.NewNonce();
        Assert.Equal((id, nonce), ConsentState.Decode(p, ConsentState.Encode(p, id, nonce)));
    }

    [Fact]
    public void Only_the_row_s_current_nonce_is_accepted()
    {
        var nonce = ConsentState.NewNonce();
        Assert.True(ConsentState.IsCurrent(nonce, nonce));
        Assert.False(ConsentState.IsCurrent(null, nonce));             // spent
        Assert.False(ConsentState.IsCurrent(ConsentState.NewNonce(), nonce));
        Assert.NotEqual(nonce, ConsentState.NewNonce());
    }

    [Fact]
    public void Rejects_null_empty_and_garbage()
    {
        var p = Keys();
        Assert.Null(ConsentState.Decode(p, null));
        Assert.Null(ConsentState.Decode(p, ""));
        Assert.Null(ConsentState.Decode(p, "not-a-real-token"));
    }

    [Fact]
    public void Rejects_an_unsigned_hand_made_state()
    {
        // The payload shape an attacker would guess, for the well-known Default
        // client, freshly "issued". Unsigned text must never be trusted.
        var p = Keys();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Null(ConsentState.Decode(p, $"{ClientTenant.DefaultId:N}.{now}"));
        Assert.Null(ConsentState.Decode(p, $"{ClientTenant.DefaultId:N}"));
        Assert.Null(ConsentState.Decode(p, $"dp:{ClientTenant.DefaultId:N}.{now}"));
    }

    [Fact]
    public void Rejects_a_value_protected_for_another_purpose()
    {
        // A stored secret (SecretProtector's purpose) is not a consent state, even
        // though it is signed by the same key ring.
        var p = Keys();
        var secret = new SecretProtector(p, NullLogger<SecretProtector>.Instance).Protect(ClientTenant.DefaultId.ToString("N"))!;
        Assert.Null(ConsentState.Decode(p, secret));
        Assert.Null(ConsentState.Decode(p, secret["dp:".Length..]));
    }

    [Fact]
    public void Rejects_a_token_signed_by_a_different_key()
    {
        var token = ConsentState.Encode(Keys(), Guid.NewGuid(), "n");
        // A different key ring must not be able to read (or trust) it.
        Assert.Null(ConsentState.Decode(Keys(), token));
    }

    [Fact]
    public void Rejects_a_tampered_token()
    {
        var p = Keys();
        var token = ConsentState.Encode(p, Guid.NewGuid(), "n");
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");
        Assert.Null(ConsentState.Decode(p, tampered));
    }

    [Fact]
    public void Expires_after_thirty_minutes()
    {
        var p = Keys();
        var id = Guid.NewGuid();
        Assert.Equal(id, ConsentState.Decode(p, ConsentState.Encode(p, id, "n", DateTimeOffset.UtcNow.AddMinutes(-29)))?.TenantId);
        Assert.Null(ConsentState.Decode(p, ConsentState.Encode(p, id, "n", DateTimeOffset.UtcNow.AddMinutes(-31))));
    }
}
