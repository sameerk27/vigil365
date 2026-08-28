using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class BaselineDriftTests
{
    private static TenantBaseline Base() => new()
    {
        Id = 1,
        CapturedAt = DateTimeOffset.UtcNow.AddDays(-14),
        CapturedBy = "s.kumar@contoso.com",
        SecureScorePct = 40.3, MfaCoveragePct = 71.0,
        RiskyUsersCount = 1, NonCompliantDevicesCount = 0,
        CriticalAlertsCount = 0, HighAlertsCount = 2, ComplianceIssuesCount = 3,
    };

    private static TrendSnapshot Snap(double secure = 40.3, double mfa = 71.0, int risky = 1,
        int nonCompliant = 0, int crit = 0, int high = 2, int compliance = 3) => new()
    {
        SecureScorePct = secure, MfaCoveragePct = mfa, RiskyUsersCount = risky,
        NonCompliantDevicesCount = nonCompliant, CriticalAlertsCount = crit,
        HighAlertsCount = high, ComplianceIssuesCount = compliance,
    };

    private static DriftRow Row(IReadOnlyList<DriftRow> rows, string metric) => rows.First(r => r.Metric == metric);

    [Fact]
    public void Unchanged_metrics_are_held_at_baseline()
    {
        var rows = BaselineDrift.Compute(Base(), Snap());
        Assert.All(rows, r => Assert.Equal("0", r.Drift));
        Assert.All(rows, r => Assert.Equal("good", r.Tone));
    }

    [Fact]
    public void Null_latest_snapshot_reports_no_drift()
    {
        // No snapshot yet → current equals baseline, so nothing drifted (never fabricated).
        var rows = BaselineDrift.Compute(Base(), null);
        Assert.All(rows, r => Assert.Equal("0", r.Drift));
    }

    [Fact]
    public void Secure_score_drop_is_a_warning()
    {
        var rows = BaselineDrift.Compute(Base(), Snap(secure: 38.2));
        var r = Row(rows, "Secure Score");
        Assert.Equal("warn", r.Tone);
        Assert.Contains("2.1", r.Drift);
        Assert.True(r.Delta < 0);
    }

    [Fact]
    public void More_risky_users_is_a_warning_but_fewer_is_good()
    {
        Assert.Equal("warn", Row(BaselineDrift.Compute(Base(), Snap(risky: 3)), "Risky users").Tone);
        Assert.Equal("good", Row(BaselineDrift.Compute(Base(), Snap(risky: 0)), "Risky users").Tone);
    }

    [Fact]
    public void Improving_secure_score_is_good()
    {
        Assert.Equal("good", Row(BaselineDrift.Compute(Base(), Snap(secure: 45.0)), "Secure Score").Tone);
    }

    [Fact]
    public void CaptureFrom_copies_the_snapshot_values()
    {
        var target = new TenantBaseline { Id = 1 };
        var snap = Snap(secure: 38.2, mfa: 67.7, risky: 3);
        var now = DateTimeOffset.UtcNow;
        BaselineDrift.CaptureFrom(target, snap, "admin@contoso.com", now);
        Assert.Equal(now, target.CapturedAt);
        Assert.Equal("admin@contoso.com", target.CapturedBy);
        Assert.Equal(38.2, target.SecureScorePct);
        Assert.Equal(67.7, target.MfaCoveragePct);
        Assert.Equal(3, target.RiskyUsersCount);
    }
}
