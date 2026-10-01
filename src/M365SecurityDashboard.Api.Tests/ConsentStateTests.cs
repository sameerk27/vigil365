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
    private static SecretProtector Protector() =>
        new(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);

    [Fact]
    public void Round_trips_the_tenant_id()
    {
        var p = Protector();
        var id = Guid.NewGuid();
        Assert.Equal(id, ConsentState.Decode(p, ConsentState.Encode(p, id)));
    }

    [Fact]
    public void Rejects_null_empty_and_garbage()
    {
        var p = Protector();
        Assert.Null(ConsentState.Decode(p, null));
        Assert.Null(ConsentState.Decode(p, ""));
        Assert.Null(ConsentState.Decode(p, "not-a-real-token"));
    }

    [Fact]
    public void Rejects_a_token_signed_by_a_different_key()
    {
        var token = ConsentState.Encode(Protector(), Guid.NewGuid());
        // A different key ring must not be able to read (or trust) it.
        Assert.Null(ConsentState.Decode(Protector(), token));
    }

    [Fact]
    public void Rejects_a_tampered_token()
    {
        var p = Protector();
        var token = ConsentState.Encode(p, Guid.NewGuid());
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");
        Assert.Null(ConsentState.Decode(p, tampered));
    }
}
