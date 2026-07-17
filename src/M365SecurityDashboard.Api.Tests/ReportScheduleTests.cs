using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class ReportScheduleTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h) => new(y, mo, d, h, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NextRunAfter_Weekly_LandsOnConfiguredDayAndHour()
    {
        // Monday (DayOfWeek=1) at 07:00 UTC.
        var s = new ReportSchedule { Cadence = "weekly", DayOfWeek = 1, HourUtc = 7 };
        // 2026-07-15 is a Wednesday; next Monday is 2026-07-20.
        var next = s.NextRunAfter(Utc(2026, 7, 15, 12));
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(Utc(2026, 7, 20, 7), next);
    }

    [Fact]
    public void NextRunAfter_Daily_IsStrictlyAfterAnchor()
    {
        var s = new ReportSchedule { Cadence = "daily", HourUtc = 6 };
        // Already past 06:00 on the anchor day → rolls to next day.
        var next = s.NextRunAfter(Utc(2026, 7, 15, 9));
        Assert.Equal(Utc(2026, 7, 16, 6), next);
    }

    [Fact]
    public void NextRunAfter_Monthly_UsesDayOfMonth()
    {
        var s = new ReportSchedule { Cadence = "monthly", DayOfMonth = 1, HourUtc = 8 };
        var next = s.NextRunAfter(Utc(2026, 7, 15, 8));
        Assert.Equal(Utc(2026, 8, 1, 8), next);
    }

    [Fact]
    public void IsDue_FiresOncePastScheduledTime_ThenNotAgainUntilNextPeriod()
    {
        var s = new ReportSchedule
        {
            Cadence = "weekly", DayOfWeek = 1, HourUtc = 7, Enabled = true,
            CreatedAt = Utc(2026, 7, 13, 0), // Monday 00:00 — before that day's 07:00 slot
        };

        Assert.False(s.IsDue(Utc(2026, 7, 13, 6)));  // before the 07:00 slot
        Assert.True(s.IsDue(Utc(2026, 7, 13, 8)));   // after it → due

        // After a run, not due again until the following Monday.
        s.LastRunAt = Utc(2026, 7, 13, 8);
        Assert.False(s.IsDue(Utc(2026, 7, 15, 12)));
        Assert.True(s.IsDue(Utc(2026, 7, 20, 8)));
    }

    [Fact]
    public void IsDue_DisabledScheduleNeverFires()
    {
        var s = new ReportSchedule { Cadence = "daily", HourUtc = 0, Enabled = false, CreatedAt = Utc(2026, 7, 1, 0) };
        Assert.False(s.IsDue(Utc(2026, 12, 1, 12)));
    }

    [Fact]
    public void SplitRecipients_HandlesMixedSeparatorsAndDedupes()
    {
        var list = ReportScheduleWorker.SplitRecipients("a@x.com, b@x.com; a@x.com\n c@x.com");
        Assert.Equal(new[] { "a@x.com", "b@x.com", "c@x.com" }, list);
    }
}
