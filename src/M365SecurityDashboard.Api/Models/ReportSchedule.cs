using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A recurring scheduled report. Today the only report type is the weekly
/// executive digest (posture + trends + top alerts), delivered by email over
/// the existing SMTP configuration. The <see cref="Services.ReportScheduleWorker"/>
/// ticks hourly and dispatches any schedule whose next run is due.
/// </summary>
public sealed class ReportSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(120)]
    public string Name { get; set; } = "Weekly executive digest";

    /// <summary>Report content type. Currently only "exec-digest".</summary>
    [MaxLength(40)]
    public string ReportType { get; set; } = "exec-digest";

    /// <summary>daily | weekly | monthly</summary>
    [MaxLength(20)]
    public string Cadence { get; set; } = "weekly";

    /// <summary>Day of week to send on for weekly cadence (0=Sunday … 6=Saturday). Ignored for daily.</summary>
    public int DayOfWeek { get; set; } = 1; // Monday

    /// <summary>Day of month to send on for monthly cadence (1–28). Ignored otherwise.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Hour of day (UTC, 0–23) at which the report should be sent.</summary>
    public int HourUtc { get; set; } = 7;

    /// <summary>Comma/semicolon-separated recipient addresses.</summary>
    [MaxLength(2000)]
    public string Recipients { get; set; } = "";

    /// <summary>Whether to attach a CSV summary alongside the HTML body.</summary>
    public bool IncludeCsv { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Outcome of the last dispatch: "sent", "failed: …", or null if never run.</summary>
    [MaxLength(400)]
    public string? LastRunStatus { get; set; }

    [MaxLength(120)]
    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Computes the next UTC send time strictly after <paramref name="after"/>,
    /// honoring cadence + configured day/hour. Pure function so it is unit-testable.
    /// </summary>
    public DateTimeOffset NextRunAfter(DateTimeOffset after)
    {
        var hour = Math.Clamp(HourUtc, 0, 23);
        // Start from the candidate day at the target hour, then walk forward until
        // it both matches the cadence's day constraint and is strictly after `after`.
        var candidate = new DateTimeOffset(after.Year, after.Month, after.Day, hour, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 400; i++)
        {
            var day = candidate.AddDays(i);
            if (day <= after) continue;
            switch (Cadence)
            {
                case "daily":
                    return day;
                case "monthly":
                    if (day.Day == Math.Clamp(DayOfMonth, 1, 28)) return day;
                    break;
                default: // weekly
                    if ((int)day.DayOfWeek == ((DayOfWeek % 7) + 7) % 7) return day;
                    break;
            }
        }
        // Unreachable for valid cadences, but keep a safe fallback.
        return candidate.AddDays(1);
    }

    /// <summary>Whether this schedule is due to run at <paramref name="now"/>, given its last run.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (!Enabled) return false;
        // Anchor the "next run" calculation on the later of last-run or creation so a
        // freshly created schedule doesn't immediately fire for a time earlier today.
        var anchor = LastRunAt ?? CreatedAt.AddSeconds(-1);
        return NextRunAfter(anchor) <= now;
    }
}
