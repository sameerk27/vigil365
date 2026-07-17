using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Ticks hourly and dispatches any enabled <see cref="ReportSchedule"/> whose next
/// run is due. Delivery reuses the SMTP configuration in NotificationSettings.
/// </summary>
public sealed class ReportScheduleWorker(
    IServiceProvider services,
    ILogger<ReportScheduleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTimeOffset.UtcNow;
                var due = (await db.ReportSchedules.ToListAsync(stoppingToken)).Where(s => s.IsDue(now)).ToList();
                foreach (var schedule in due)
                {
                    var (ok, status) = await DispatchAsync(scope.ServiceProvider, db, schedule, stoppingToken);
                    schedule.LastRunAt = now;
                    schedule.LastRunStatus = status;
                    logger.Log(ok ? LogLevel.Information : LogLevel.Warning,
                        "Report '{Name}' dispatch: {Status}", schedule.Name, status);
                }
                if (due.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Report schedule tick failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Builds and sends one report. Shared by the worker and the manual "run now"
    /// endpoint. Does not persist the schedule — the caller records LastRun*.
    /// </summary>
    public static async Task<(bool ok, string status)> DispatchAsync(
        IServiceProvider sp, AppDbContext db, ReportSchedule schedule, CancellationToken ct)
    {
        var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct);
        if (cfg == null || !cfg.EmailEnabled || string.IsNullOrWhiteSpace(cfg.SmtpHost))
            return (false, "failed: SMTP email is not configured");

        var recipients = SplitRecipients(schedule.Recipients);
        if (recipients.Count == 0)
            return (false, "failed: no recipients");

        var window = schedule.Cadence switch { "daily" => 1, "monthly" => 30, _ => 7 };
        var builder = sp.GetRequiredService<DigestBuilder>();
        var digest = await builder.BuildAsync(window, ct);

        var sender = sp.GetRequiredService<NotificationSender>();
        var csvName = $"vigil365-digest-{digest.GeneratedAt:yyyyMMdd}.csv";
        var (ok, error) = await sender.SendReportEmailAsync(
            cfg, recipients, digest.Subject, digest.HtmlBody,
            schedule.IncludeCsv ? digest.Csv : null, csvName, ct);

        return ok
            ? (true, $"sent to {recipients.Count} recipient{(recipients.Count == 1 ? "" : "s")}")
            : (false, $"failed: {error}");
    }

    public static List<string> SplitRecipients(string? raw) =>
        (raw ?? "")
        .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
