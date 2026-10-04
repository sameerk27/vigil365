using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Checks every 15 minutes and dispatches any enabled <see cref="ReportSchedule"/> whose next
/// run is due. Delivery reuses the SMTP configuration in NotificationSettings.
/// </summary>
public sealed class ReportScheduleWorker(
    IServiceProvider services,
    IOptions<EditionOptions> edition,
    ILogger<ReportScheduleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    // A scheduled 07:00 UTC executive digest should not arrive close to 08:00.
    // This remains inexpensive because only due schedules build a digest.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>What an MSP-mode schedule with no client records instead of being sent.</summary>
    public const string UnassignedStatus = "skipped: not assigned to a client. Delete it and create it again with the client selected.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(services, edition.Value.IsMsp, logger, stoppingToken);
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
    /// One tick: each active tenant's due schedules, each digest built in that
    /// tenant's own scope. In MSP mode a schedule belongs to one client, and one
    /// with no client is never sent: visible in every client's pass, it would go
    /// out with whichever client's digest ran first.
    /// </summary>
    public static async Task RunOnceAsync(IServiceProvider services, bool msp, ILogger logger, CancellationToken stoppingToken)
    {
        if (msp) await SkipUnassignedAsync(services, logger, stoppingToken);

        await Data.Tenancy.TenantIterator.ForEachActiveTenantAsync(services, logger, "Scheduled reports", async (sp, tenant, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            // Single mode: the install-wide schedules and the tenant's own. MSP mode: the client's own only.
            var due = (await db.ReportSchedules.Where(s => !msp || s.TenantId == tenant.Id).ToListAsync(ct))
                .Where(s => s.IsDue(now)).ToList();
            foreach (var schedule in due)
            {
                var (ok, status) = await DispatchAsync(sp, db, schedule, ct);
                schedule.LastRunAt = now;
                schedule.LastRunStatus = status;
                logger.Log(ok ? LogLevel.Information : LogLevel.Warning,
                    "Report '{Name}' dispatch: {Status}", schedule.Name, status);
            }
            if (due.Count > 0) await db.SaveChangesAsync(ct);
        }, stoppingToken);
    }

    /// <summary>
    /// MSP mode: marks each due schedule that has no client as skipped, so the
    /// Reports page says why it was not sent and this warns once per period,
    /// not every tick. Runs with no tenant: these rows belong to none.
    /// </summary>
    private static async Task SkipUnassignedAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var due = (await db.ReportSchedules.Where(s => s.TenantId == null).ToListAsync(ct))
            .Where(s => s.IsDue(now)).ToList();
        foreach (var schedule in due)
        {
            schedule.LastRunAt = now;
            schedule.LastRunStatus = UnassignedStatus;
            logger.LogWarning("Report '{Name}' was not sent: in MSP mode each schedule belongs to one client, and this one has none. Delete it and create it again with the client selected.",
                schedule.Name);
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds and sends one report. Shared by the worker and the manual "run now"
    /// endpoint. Does not persist the schedule — the caller records LastRun*.
    /// </summary>
    public static async Task<(bool ok, string status)> DispatchAsync(
        IServiceProvider sp, AppDbContext db, ReportSchedule schedule, CancellationToken ct)
    {
        var cfg = await db.EffectiveNotificationSettingsAsync(ct);
        if (!cfg.EmailEnabled || string.IsNullOrWhiteSpace(cfg.SmtpHost))
            return (false, "failed: SMTP email is not configured");

        var recipients = SplitRecipients(schedule.Recipients);
        if (recipients.Count == 0)
            return (false, "failed: no recipients");

        var window = schedule.Cadence switch { "daily" => 1, "monthly" => 30, _ => 7 };
        var builder = sp.GetRequiredService<DigestBuilder>();
        var digest = await builder.BuildAsync(window, ct);
        var attachments = new List<NotificationSender.ReportAttachment>();
        if (schedule.IncludeCsv && !string.IsNullOrEmpty(digest.Csv))
            attachments.Add(new NotificationSender.ReportAttachment(
                $"{Slug(digest.Brand)}-digest-{digest.GeneratedAt:yyyyMMdd}.csv",
                "text/csv",
                System.Text.Encoding.UTF8.GetBytes(digest.Csv)));
        if (schedule.IncludePdf)
        {
            var pdf = sp.GetRequiredService<DigestPdfRenderer>().Render(digest);
            attachments.Add(new NotificationSender.ReportAttachment(
                $"{Slug(digest.Brand)}-exec-digest-{digest.GeneratedAt:yyyyMMdd}.pdf",
                "application/pdf",
                pdf));
        }

        var sender = sp.GetRequiredService<NotificationSender>();
        var (ok, error) = await sender.SendReportEmailAsync(
            cfg, recipients, digest.Subject, digest.HtmlBody,
            attachments, ct);

        return ok
            ? (true, $"sent to {recipients.Count} recipient{(recipients.Count == 1 ? "" : "s")}")
            : (false, $"failed: {error}");
    }

    private static string Slug(string brand)
    {
        var s = new string(brand.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(s) ? "vigil365" : s;
    }

    public static List<string> SplitRecipients(string? raw) =>
        (raw ?? "")
        .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
