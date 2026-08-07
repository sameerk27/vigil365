using System.Text.Json;
using M365SecurityDashboard.Api.Services;
using Xunit;
using View = M365SecurityDashboard.Api.Services.SharingPostureAnalyzer.SharingView;

namespace M365SecurityDashboard.Api.Tests;

public class SharingPostureAnalyzerTests
{
    private static View Settings(
        string? cap = "externalUserSharingOnly", string? oneDrive = null, string? defaultLink = "internal",
        int? expiry = 30, bool reshare = false, string[]? allowed = null, string[]? blocked = null)
        => new(cap, oneDrive ?? cap, defaultLink, expiry, reshare, allowed ?? ["partner.com"], blocked ?? []);

    [Fact]
    public void Analyze_HealthyGuestSharing_NoFindings()
    {
        var f = SharingPostureAnalyzer.Analyze(Settings());
        Assert.Empty(f);
    }

    [Fact]
    public void Analyze_AnyoneLinks_FlagsHigh_AndNeverExpiringLinks()
    {
        var f = SharingPostureAnalyzer.Analyze(Settings(cap: "externalUserAndGuestSharing", expiry: null));
        Assert.Contains(f, x => x.Severity == "high" && x.Title.Contains("\"Anyone\" links"));
        Assert.Contains(f, x => x.Severity == "medium" && x.Title.Contains("never expire"));
        // Ranked most severe first.
        Assert.Equal("high", f[0].Severity);
    }

    [Fact]
    public void Analyze_ExpiryFinding_OnlyWhenAnyoneLinksEnabled()
    {
        // Guests-only sharing with no expiry set: anonymous-expiry finding must NOT fire.
        var f = SharingPostureAnalyzer.Analyze(Settings(cap: "externalUserSharingOnly", expiry: null));
        Assert.DoesNotContain(f, x => x.Title.Contains("never expire"));
    }

    [Fact]
    public void Analyze_AnonymousDefaultLink_FlagsHigh()
    {
        var f = SharingPostureAnalyzer.Analyze(Settings(defaultLink: "anonymousAccess"));
        Assert.Contains(f, x => x.Severity == "high" && x.Title.Contains("Default sharing link"));
    }

    [Fact]
    public void Analyze_ExternalResharing_And_NoDomainRestrictions()
    {
        var f = SharingPostureAnalyzer.Analyze(Settings(reshare: true, allowed: [], blocked: []));
        Assert.Contains(f, x => x.Title.Contains("re-share"));
        Assert.Contains(f, x => x.Title.Contains("domain restrictions"));
    }

    [Fact]
    public void Analyze_OneDriveLooserThanSharePoint_Flags()
    {
        var f = SharingPostureAnalyzer.Analyze(Settings(cap: "externalUserSharingOnly", oneDrive: "externalUserAndGuestSharing"));
        Assert.Contains(f, x => x.Title.Contains("OneDrive"));
    }

    [Fact]
    public void Analyze_SharingDisabled_IsClean()
    {
        var f = SharingPostureAnalyzer.Analyze(Settings(cap: "disabled", allowed: [], blocked: [], expiry: null, reshare: true));
        Assert.Empty(f); // resharing/domains are irrelevant when external sharing is off
    }

    [Fact]
    public void Parse_ExtractsFieldsFromGraphShape()
    {
        var json = JsonDocument.Parse("""
        {
          "sharingCapability": "externalUserAndGuestSharing",
          "sharingDefaultLinkType": "anonymousAccess",
          "sharingLinkExpirationInDays": 14,
          "isResharingByExternalUsersEnabled": true,
          "sharingAllowedDomainList": ["contoso.com"],
          "sharingBlockedDomainList": []
        }
        """).RootElement;

        var v = SharingPostureAnalyzer.Parse(json);
        Assert.Equal("externalUserAndGuestSharing", v.SharingCapability);
        Assert.Equal("anonymousAccess", v.DefaultSharingLinkType);
        Assert.Equal(14, v.AnonymousLinkExpirationDays);
        Assert.True(v.ResharingByExternalUsersEnabled);
        Assert.Single(v.AllowedDomains);
    }
}
