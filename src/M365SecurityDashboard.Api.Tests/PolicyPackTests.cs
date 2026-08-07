using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Policy packs cross install boundaries, so the contract matters: runtime state
/// must not travel, recipients must not leak by default, and an invalid entry
/// must be rejected rather than coerced into the evaluator.
/// </summary>
public class PolicyPackTests
{
    private static AlertPolicy Sample() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Privileged role assigned",
        Enabled = true,
        Category = "identity",
        Condition = "Activity \"Add member to role\" >= 1 in 60m",
        Kind = "activity",
        Metric = "",
        ActivityPattern = "Add member to role",
        WindowMinutes = 60,
        BaselineMultiplier = 3.0,
        BaselineDays = 30,
        Threshold = 1,
        Severity = "high",
        SuppressionMinutes = 60,
        NotifyEmail = "soc@contoso.com",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
        LastTriggered = DateTimeOffset.UtcNow.AddDays(-2),
        TriggerCount = 47,
    };

    [Fact]
    public void ToPack_StripsRecipientByDefault()
    {
        // Packs get shared; an internal address must not ride along silently.
        Assert.Null(PolicyPack.ToPack(Sample(), includeRecipients: false).NotifyEmail);
    }

    [Fact]
    public void ToPack_IncludesRecipientWhenExplicitlyRequested()
        => Assert.Equal("soc@contoso.com", PolicyPack.ToPack(Sample(), includeRecipients: true).NotifyEmail);

    [Fact]
    public void ToEntity_ResetsRuntimeState_NotCarriedFromAnotherInstall()
    {
        var entity = PolicyPack.ToEntity(PolicyPack.ToPack(Sample(), false));
        Assert.Equal(0, entity.TriggerCount);      // never fired *here*
        Assert.Null(entity.LastTriggered);
        Assert.NotEqual(Guid.Empty, entity.Id);
    }

    [Fact]
    public void RoundTrip_PreservesEveryBehaviouralField()
    {
        var original = Sample();
        var restored = PolicyPack.ToEntity(PolicyPack.ToPack(original, includeRecipients: true));

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Enabled, restored.Enabled);
        Assert.Equal(original.Category, restored.Category);
        Assert.Equal(original.Kind, restored.Kind);
        Assert.Equal(original.Metric, restored.Metric);
        Assert.Equal(original.ActivityPattern, restored.ActivityPattern);
        Assert.Equal(original.WindowMinutes, restored.WindowMinutes);
        Assert.Equal(original.BaselineMultiplier, restored.BaselineMultiplier);
        Assert.Equal(original.BaselineDays, restored.BaselineDays);
        Assert.Equal(original.Threshold, restored.Threshold);
        Assert.Equal(original.Severity, restored.Severity);
        Assert.Equal(original.SuppressionMinutes, restored.SuppressionMinutes);
        Assert.Equal(original.NotifyEmail, restored.NotifyEmail);
    }

    [Fact]
    public void Validate_AcceptsAWellFormedPolicy()
        => Assert.Null(PolicyPack.Validate(PolicyPack.ToPack(Sample(), false)));

    [Fact]
    public void Validate_RejectsActivityPolicyWithNoPattern()
    {
        // Would sit in the evaluator matching nothing while looking protective.
        var p = PolicyPack.ToPack(Sample(), false) with { ActivityPattern = "  " };
        Assert.Contains("activity pattern", PolicyPack.Validate(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsMetricPolicyWithNoMetric()
    {
        var p = PolicyPack.ToPack(Sample(), false) with { Kind = "metric", Metric = "" };
        Assert.Contains("metric", PolicyPack.Validate(p), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_RejectsNonPositiveThreshold(int threshold)
    {
        var p = PolicyPack.ToPack(Sample(), false) with { Threshold = threshold };
        Assert.Contains("Threshold", PolicyPack.Validate(p));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsMissingName(string name)
    {
        var p = PolicyPack.ToPack(Sample(), false) with { Name = name };
        Assert.Contains("Name", PolicyPack.Validate(p));
    }

    [Fact]
    public void Validate_RejectsUnknownKind()
    {
        var p = PolicyPack.ToPack(Sample(), false) with { Kind = "telepathy" };
        Assert.Contains("Kind", PolicyPack.Validate(p));
    }

    [Fact]
    public void Validate_RejectsUnknownSeverity()
    {
        var p = PolicyPack.ToPack(Sample(), false) with { Severity = "catastrophic" };
        Assert.Contains("Severity", PolicyPack.Validate(p));
    }

    [Fact]
    public void Validate_RejectsNullEntry() => Assert.NotNull(PolicyPack.Validate(null));

    [Fact]
    public void ApplyTo_PreservesIdentityAndHistory()
    {
        var target = Sample();
        var originalId = target.Id;
        var originalCount = target.TriggerCount;

        PolicyPack.ApplyTo(target, PolicyPack.ToPack(Sample(), false) with { Threshold = 9, Severity = "low" });

        Assert.Equal(originalId, target.Id);            // same policy, updated in place
        Assert.Equal(originalCount, target.TriggerCount);
        Assert.Equal(9, target.Threshold);
        Assert.Equal("low", target.Severity);
    }

    [Fact]
    public void ApplyTo_StrippedPackDoesNotClearLocalRecipient()
    {
        // Importing a shared pack must not silently break local alert routing.
        var target = Sample();
        PolicyPack.ApplyTo(target, PolicyPack.ToPack(Sample(), includeRecipients: false));
        Assert.Equal("soc@contoso.com", target.NotifyEmail);
    }

    [Fact]
    public void ApplyTo_PackWithRecipientOverwritesLocal()
    {
        var target = Sample();
        PolicyPack.ApplyTo(target, PolicyPack.ToPack(Sample(), true) with { NotifyEmail = "new@contoso.com" });
        Assert.Equal("new@contoso.com", target.NotifyEmail);
    }

    // ── Real-world shapes ────────────────────────────────────────────────────
    // Caught by validating the live tenant's 21 policies: 18 were rejected
    // because validation checked tuning fields irrelevant to the policy's kind.
    // Baseline columns default to 0 on every non-anomaly policy, and policies
    // predating the Kind column carry "".

    [Fact]
    public void Validate_AcceptsMetricPolicyWithZeroBaselineFields()
    {
        // Baseline settings only mean anything for anomaly policies.
        var p = PolicyPack.ToPack(Sample(), false) with
        {
            Kind = "metric", Metric = "riskyUsersCount", ActivityPattern = null,
            BaselineDays = 0, BaselineMultiplier = 0, WindowMinutes = 0,
        };
        Assert.Null(PolicyPack.Validate(p));
    }

    [Fact]
    public void Validate_AcceptsLegacyPolicyWithEmptyKind()
    {
        // The evaluator reads a blank Kind as "metric"; so must the pack.
        var p = PolicyPack.ToPack(Sample(), false) with
        {
            Kind = "", Metric = "criticalAlertCount", ActivityPattern = null,
            BaselineDays = 0, BaselineMultiplier = 0, WindowMinutes = 0,
        };
        Assert.Null(PolicyPack.Validate(p));
        Assert.Equal("metric", PolicyPack.ToEntity(p).Kind);
    }

    [Fact]
    public void ToEntity_CoercesUnsetTuningFieldsToTheSameDefaultsAsPolicyCreate()
    {
        var p = PolicyPack.ToPack(Sample(), false) with
        {
            Kind = "metric", Metric = "riskyUsersCount", ActivityPattern = null,
            WindowMinutes = 0, BaselineDays = 0, BaselineMultiplier = 0, SuppressionMinutes = -1,
        };
        var e = PolicyPack.ToEntity(p);
        Assert.Equal(60, e.WindowMinutes);
        Assert.Equal(30, e.BaselineDays);
        Assert.Equal(3.0, e.BaselineMultiplier);
        Assert.Equal(60, e.SuppressionMinutes);
    }

    [Fact]
    public void ToEntity_NormalisesKindAndSeverityCasing()
    {
        var p = PolicyPack.ToPack(Sample(), false) with { Kind = "ACTIVITY", Severity = "HIGH" };
        var e = PolicyPack.ToEntity(p);
        Assert.Equal("activity", e.Kind);
        Assert.Equal("high", e.Severity);
    }
}
