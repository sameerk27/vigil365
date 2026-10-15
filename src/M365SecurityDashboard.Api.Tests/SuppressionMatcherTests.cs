using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Suppression decides which alerts are never raised. A bug here hides real
/// security alerts silently, so the matching semantics are locked down hard.
/// </summary>
public class SuppressionMatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PolicyA = Guid.NewGuid();
    private static readonly Guid PolicyB = Guid.NewGuid();

    private static string Entities(params string[] upns)
        => "[" + string.Join(",", upns.Select(u => $"{{\"userPrincipalName\":\"{u}\"}}")) + "]";

    private static SuppressionRule Rule(
        Guid? policyId = null, string? pattern = null, bool enabled = true,
        DateTimeOffset? expires = null)
        => new() { PolicyId = policyId, EntityPattern = pattern, Enabled = enabled, ExpiresAt = expires, Reason = "test" };

    // ── Pattern matching ────────────────────────────────────────────────────
    [Theory]
    [InlineData("svc-backup@x.com", "svc-backup@x.com", true)]   // exact
    [InlineData("SVC-BACKUP@X.COM", "svc-backup@x.com", true)]   // case-insensitive
    [InlineData("svc-*", "svc-backup@x.com", true)]              // prefix
    [InlineData("svc-*", "user@x.com", false)]
    [InlineData("*@contractors.com", "bob@contractors.com", true)] // suffix
    [InlineData("*@contractors.com", "bob@staff.com", false)]
    [InlineData("*backup*", "svc-backup@x.com", true)]           // contains
    [InlineData("*backup*", "svc-restore@x.com", false)]
    [InlineData("*", "anything", true)]
    public void EntityMatches_HandlesWildcards(string pattern, string entity, bool expected)
        => Assert.Equal(expected, SuppressionMatcher.EntityMatches(pattern, entity));

    [Fact]
    public void EntityMatches_NoPatternMeansNoRestriction()
        => Assert.True(SuppressionMatcher.EntityMatches(null, "anyone@x.com"));

    [Fact]
    public void EntityMatches_PatternWithNoEntityDoesNotMatch()
        => Assert.False(SuppressionMatcher.EntityMatches("svc-*", null));

    // ── Entity extraction ───────────────────────────────────────────────────
    [Fact]
    public void ExtractEntities_ReadsCamelCaseKeys()
    {
        var json = "[{\"userPrincipalName\":\"a@x.com\"},{\"deviceName\":\"LAPTOP-1\"},{\"targetName\":\"Group A\"}]";
        var got = SuppressionMatcher.ExtractEntities(json);
        Assert.Equal(["a@x.com", "LAPTOP-1", "Group A"], got);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"notAnArray\":true}")]
    public void ExtractEntities_ToleratesBadInput(string? json)
        => Assert.Empty(SuppressionMatcher.ExtractEntities(json));

    // ── Rule matching ───────────────────────────────────────────────────────
    [Fact]
    public void FindMatch_PolicyWideRuleSuppressesThatPolicyOnly()
    {
        var rules = new[] { Rule(policyId: PolicyA) };
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyA, null, Now));
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyB, null, Now));
    }

    [Fact]
    public void FindMatch_EntityRuleAppliesAcrossPolicies()
    {
        var rules = new[] { Rule(pattern: "svc-*") };
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("svc-backup@x.com"), Now));
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyB, Entities("svc-backup@x.com"), Now));
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("real.user@x.com"), Now));
    }

    [Fact]
    public void FindMatch_PolicyAndEntityMustBothMatch()
    {
        var rules = new[] { Rule(policyId: PolicyA, pattern: "svc-*") };
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("svc-1@x.com"), Now));
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyB, Entities("svc-1@x.com"), Now));   // wrong policy
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("real@x.com"), Now));    // wrong entity
    }

    [Fact]
    public void FindMatch_EntityRuleNeverHidesTheOtherEntitiesInTheSameAlert()
    {
        // One alert lists every risky user. The service account being in it
        // must not hide the real user who became risky alongside it.
        var rules = new[] { Rule(pattern: "svc-*") };
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("real@x.com", "svc-1@x.com"), Now));
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("svc-1@x.com", "svc-2@x.com"), Now));
    }

    [Fact]
    public void FindMatch_SuppressesWhenEveryEntityIsCoveredByOneRuleOrAnother()
    {
        var rules = new[] { Rule(pattern: "svc-*"), Rule(pattern: "*@contractors.x.com") };
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("svc-1@x.com", "bob@contractors.x.com"), Now));
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyA, Entities("svc-1@x.com", "bob@contractors.x.com", "real@x.com"), Now));
    }

    [Fact]
    public void FindMatch_AnEntityMatchesOnAnyOfItsIdentifiers()
    {
        // A device row names its device and its user; an entity with no
        // identifier at all can never be covered, so it keeps the alert raised.
        var rules = new[] { Rule(pattern: "SVC-PC*") };
        Assert.NotNull(SuppressionMatcher.FindMatch(rules, PolicyA, """[{"userPrincipalName":"real@x.com","deviceName":"svc-pc-01"}]""", Now));
        Assert.Null(SuppressionMatcher.FindMatch(rules, PolicyA, """[{"deviceName":"svc-pc-01"},{"title":"Defender incident"}]""", Now));
        // Activity entities carry the target, so target-based rules match.
        Assert.NotNull(SuppressionMatcher.FindMatch([Rule(pattern: "Break-glass*")], PolicyA,
            """[{"userPrincipalName":"admin@x.com","targetName":"Break-glass account"}]""", Now));
    }

    [Fact]
    public void FindMatch_IgnoresDisabledAndExpiredRules()
    {
        Assert.Null(SuppressionMatcher.FindMatch([Rule(policyId: PolicyA, enabled: false)], PolicyA, null, Now));
        Assert.Null(SuppressionMatcher.FindMatch([Rule(policyId: PolicyA, expires: Now.AddMinutes(-1))], PolicyA, null, Now));
        Assert.NotNull(SuppressionMatcher.FindMatch([Rule(policyId: PolicyA, expires: Now.AddMinutes(1))], PolicyA, null, Now));
    }

    [Fact]
    public void FindMatch_UnscopedRuleNeverSuppressesEverything()
    {
        // A rule with neither policy nor entity would mute the whole product.
        // The API rejects it, and the matcher refuses it as defence in depth.
        Assert.Null(SuppressionMatcher.FindMatch([Rule()], PolicyA, Entities("a@x.com"), Now));
    }

    [Fact]
    public void FindMatch_EntityRuleDoesNotSuppressAlertWithNoEntities()
    {
        // A metric policy with no affected entities must not be silenced by an
        // entity-scoped rule — otherwise "suppress svc-*" would hide tenant-wide alerts.
        Assert.Null(SuppressionMatcher.FindMatch([Rule(pattern: "svc-*")], PolicyA, null, Now));
    }
}
