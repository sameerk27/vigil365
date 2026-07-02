using Xunit;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

public class RecommendationsEngineTests
{
    [Fact]
    public async Task GetRecommendationsAsync_ReturnsAllGuidanceItemsWithCorrectAffectedCounts()
    {
        using var db = TestAppDbContextFactory.Create();
        db.SecurityAlerts.Add(new SecurityAlert { AlertType = "MfaStatus", Title = "MFA Missing", Severity = AlertSeverity.High, IsResolved = false });
        db.SecurityAlerts.Add(new SecurityAlert { AlertType = "RiskyUser", Title = "Risky User", Severity = AlertSeverity.Critical, IsResolved = false });
        await db.SaveChangesAsync();

        var recs = await RecommendationsEngine.GetRecommendationsAsync(db);

        Assert.NotNull(recs);
        Assert.True(recs.Count >= 6);

        var mfaRec = recs.FirstOrDefault(r => r.Id == "rec-mfa-registration");
        Assert.NotNull(mfaRec);
        Assert.Equal(1, mfaRec.AffectedCount);
        Assert.Contains("https://entra.microsoft.com", mfaRec.PortalDeepLink);

        var riskyRec = recs.FirstOrDefault(r => r.Id == "rec-risky-users");
        Assert.NotNull(riskyRec);
        Assert.Equal(1, riskyRec.AffectedCount);
        Assert.Equal("critical", riskyRec.Severity);
    }

    [Fact]
    public async Task GetAlertCoverageAsync_Evaluates20BaselineRulesCorrectly()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(new AlertPolicy { Name = "Critical Security Alerts", Enabled = true });
        await db.SaveChangesAsync();

        var scorecard = await RecommendationsEngine.GetAlertCoverageAsync(db);

        Assert.NotNull(scorecard);
        Assert.Equal(20, scorecard.TotalRules);

        var critRule = scorecard.Rules.FirstOrDefault(r => r.Title == "Critical Security Alerts");
        Assert.NotNull(critRule);
        Assert.True(critRule.IsActive);

        var mfaRule = scorecard.Rules.FirstOrDefault(r => r.Title == "MFA Not Registered");
        Assert.NotNull(mfaRule);
        Assert.False(mfaRule.IsActive);
    }

    [Fact]
    public async Task EnableCoverageRuleAsync_CreatesNewAlertPolicyInDb()
    {
        using var db = TestAppDbContextFactory.Create();

        var policy = await RecommendationsEngine.EnableCoverageRuleAsync(db, "base-02"); // MFA Not Registered

        Assert.NotNull(policy);
        Assert.Equal("MFA Not Registered", policy.Name);
        Assert.True(policy.Enabled);

        var saved = db.AlertPolicies.FirstOrDefault(p => p.Name == "MFA Not Registered");
        Assert.NotNull(saved);
        Assert.True(saved.Enabled);

        var scorecard = await RecommendationsEngine.GetAlertCoverageAsync(db);
        Assert.True(scorecard.Rules.First(r => r.Id == "base-02").IsActive);
    }
}
