using System.Text.Json;
using M365SecurityDashboard.Api.Services;
using Xunit;
using CaView = M365SecurityDashboard.Api.Services.ConditionalAccessGapAnalyzer.CaPolicyView;

namespace M365SecurityDashboard.Api.Tests;

public class ConditionalAccessGapAnalyzerTests
{
    private static CaView Policy(string name = "P", string state = "enabled", bool mfa = false, bool block = false,
        bool allUsers = false, bool allApps = false, int exU = 0, int exG = 0, string[]? clients = null)
        => new(name, state, mfa, block, allUsers, allApps, exU, exG, clients ?? ["all"]);

    [Fact]
    public void Analyze_NoPolicies_IsCritical()
    {
        var f = ConditionalAccessGapAnalyzer.Analyze([]);
        var only = Assert.Single(f);
        Assert.Equal("critical", only.Severity);
        Assert.Contains("no Conditional Access", only.Detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_HealthyBaseline_NoMfaOrLegacyFinding()
    {
        var policies = new[]
        {
            Policy(name: "Require MFA", mfa: true, allUsers: true, allApps: true),
            Policy(name: "Block legacy", block: true, allUsers: true, allApps: true, clients: ["exchangeActiveSync", "other"]),
        };
        var f = ConditionalAccessGapAnalyzer.Analyze(policies);
        Assert.DoesNotContain(f, x => x.Title.Contains("MFA policy"));
        Assert.DoesNotContain(f, x => x.Title.Contains("Legacy"));
        Assert.Empty(f); // healthy
    }

    [Fact]
    public void Analyze_MfaExistsButNotAllUsers_FlagsBaselineGap()
    {
        var f = ConditionalAccessGapAnalyzer.Analyze([Policy(mfa: true, allUsers: false, allApps: true)]);
        var mfa = Assert.Single(f, x => x.Title.Contains("No tenant-wide MFA"));
        Assert.Equal("critical", mfa.Severity);
        Assert.Contains("some enabled policies", mfa.Detail);
    }

    [Fact]
    public void Analyze_LegacyAuthOnlyBlockedByDisabledPolicy_StillFlags()
    {
        var policies = new[]
        {
            Policy(name: "MFA", mfa: true, allUsers: true, allApps: true),
            Policy(name: "Legacy", state: "disabled", block: true, clients: ["other"]),
        };
        var f = ConditionalAccessGapAnalyzer.Analyze(policies);
        Assert.Contains(f, x => x.Title.Contains("Legacy") && x.Severity == "high");
    }

    [Fact]
    public void Analyze_MfaExclusionsAndReportOnly_AreReported()
    {
        var policies = new[]
        {
            Policy(name: "MFA", mfa: true, allUsers: true, allApps: true, exU: 2, exG: 1),
            Policy(name: "Block legacy", block: true, clients: ["other"]),
            Policy(name: "Pilot", state: "enabledForReportingButNotEnforced", mfa: true),
        };
        var f = ConditionalAccessGapAnalyzer.Analyze(policies);
        var excl = Assert.Single(f, x => x.Title.Contains("MFA exemptions"));
        Assert.Contains("2 users and 1 group", excl.Detail);
        Assert.Contains(f, x => x.Title.Contains("report-only"));
    }

    [Fact]
    public void Parse_ExtractsMfaAllUsersAllAppsAndExclusions()
    {
        var json = JsonDocument.Parse("""
        {
          "displayName": "Baseline",
          "state": "enabled",
          "conditions": {
            "users": { "includeUsers": ["All"], "excludeUsers": ["a","b"], "excludeGroups": ["g1"] },
            "applications": { "includeApplications": ["All"] },
            "clientAppTypes": ["exchangeActiveSync","other"]
          },
          "grantControls": { "builtInControls": ["mfa"] }
        }
        """).RootElement;

        var v = ConditionalAccessGapAnalyzer.Parse(json);
        Assert.True(v.RequiresMfa);
        Assert.True(v.IncludesAllUsers);
        Assert.True(v.IncludesAllApps);
        Assert.Equal(2, v.ExcludedUsers);
        Assert.Equal(1, v.ExcludedGroups);
        Assert.Contains("other", v.ClientAppTypes);
    }
}
