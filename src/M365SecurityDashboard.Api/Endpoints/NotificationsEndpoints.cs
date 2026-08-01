using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Notification channel settings, test dispatch, delivery log, and per-channel delivery health.</summary>
public static class NotificationsEndpoints
{
    public static void MapNotificationsEndpoints(this WebApplication app)
    {
        // Notification settings (single row). Password is write-only — never returned.
        app.MapGet("/api/notification-settings", async (AppDbContext db, SecretProtector protector, CancellationToken ct) =>
        {
            var s = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings { Id = 1 };
            return Results.Ok(new
            {
                s.TeamsEnabled, TeamsWebhookUrl = protector.Unprotect(s.TeamsWebhookUrl),
                s.EmailEnabled, s.SmtpHost, s.SmtpPort, s.SmtpUseSsl, s.SmtpUsername,
                hasSmtpPassword = !string.IsNullOrEmpty(s.SmtpPassword),
                s.FromAddress, s.DefaultRecipient,
                s.WebhookEnabled, WebhookUrl = protector.Unprotect(s.WebhookUrl),
                hasWebhookSigningSecret = !string.IsNullOrEmpty(s.WebhookSigningSecret),
                s.MinSeverity,
                s.TeamsDigest, s.EmailDigest, s.WebhookDigest, s.DigestHourUtc, s.FailureAlertThreshold,
            });
        }).RequireAuthorization("RequireAdmin");

        app.MapPut("/api/notification-settings", async (AppDbContext db, SecretProtector protector, AuditLogger audit, NotificationSettings input, CancellationToken ct) =>
        {
            var s = await db.NotificationSettings.FirstOrDefaultAsync(ct);
            if (s is null) { s = new NotificationSettings { Id = 1 }; db.NotificationSettings.Add(s); }
            s.TeamsEnabled = input.TeamsEnabled;
            s.TeamsWebhookUrl = protector.Protect(input.TeamsWebhookUrl);
            s.EmailEnabled = input.EmailEnabled;
            s.SmtpHost = input.SmtpHost;
            s.SmtpPort = input.SmtpPort <= 0 ? 587 : input.SmtpPort;
            s.SmtpUseSsl = input.SmtpUseSsl;
            s.SmtpUsername = input.SmtpUsername;
            if (!string.IsNullOrEmpty(input.SmtpPassword)) s.SmtpPassword = protector.Protect(input.SmtpPassword); // keep existing if blank
            s.FromAddress = input.FromAddress;
            s.DefaultRecipient = input.DefaultRecipient;
            s.WebhookEnabled = input.WebhookEnabled;
            s.WebhookUrl = protector.Protect(input.WebhookUrl);
            if (!string.IsNullOrWhiteSpace(input.WebhookSigningSecret))
                s.WebhookSigningSecret = protector.Protect(input.WebhookSigningSecret);
            s.MinSeverity = string.IsNullOrWhiteSpace(input.MinSeverity) ? "low" : input.MinSeverity;
            s.TeamsDigest = input.TeamsDigest;
            s.EmailDigest = input.EmailDigest;
            s.WebhookDigest = input.WebhookDigest;
            s.DigestHourUtc = Math.Clamp(input.DigestHourUtc, 0, 23);
            s.FailureAlertThreshold = input.FailureAlertThreshold <= 0 ? 3 : input.FailureAlertThreshold;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("settings.update", "settings", "notifications", "notification settings updated", ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("RequireAdmin");

        // Send a test notification through all enabled channels
        app.MapPost("/api/notification-settings/test", async (AppDbContext db, NotificationSender sender, CancellationToken ct) =>
        {
            var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct);
            if (cfg is null) return Results.Ok(new { ok = false, message = "No settings configured" });
            var test = new TriggeredAlert
            {
                Id = Guid.NewGuid(),
                PolicyName = "Test Notification",
                Severity = "high",
                Category = "test",
                Condition = "Manual test from Vigil365 settings",
                MetricValue = 1,
                Threshold = 1,
                TriggeredAt = DateTimeOffset.UtcNow,
                Status = "new",
            };
            await sender.DispatchAsync(db, cfg, test, ct);
            await db.SaveChangesAsync(ct);
            var logs = await db.NotificationLogs.Where(l => l.TriggeredAlertId == test.Id).ToListAsync(ct);
            return Results.Ok(new { ok = logs.Any(l => l.Success), results = logs.Select(l => new { l.Channel, l.Success, l.Error }) });
        }).RequireAuthorization("RequireAdmin");

        // Notification delivery history
        app.MapGet("/api/notification-log", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.NotificationLogs.OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct)))
            .RequireAuthorization("RequireAnalyst");

        // Per-channel delivery health (consecutive failures, last success/error).
        app.MapGet("/api/notification-health", async (AppDbContext db, CancellationToken ct) =>
        {
            var cfg = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var recent = await db.NotificationLogs.AsNoTracking().OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct);
            var health = NotificationHealth.Compute(recent);
            var threshold = cfg?.FailureAlertThreshold ?? 3;
            return Results.Ok(new { threshold, channels = health, anyFailing = health.Any(h => h.ConsecutiveFailures >= threshold) });
        }).RequireAuthorization("RequireAnalyst");
    }
}
