using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Notification channel settings, test dispatch, delivery log, and per-channel delivery health.</summary>
public static class NotificationsEndpoints
{
    public sealed record RoutingUpdate(bool NotifyMsp, bool NotifyClient, string? RecipientEmail, string? TeamsWebhookUrl, string? WebhookUrl, string? MinSeverity);

    public static void MapNotificationsEndpoints(this WebApplication app)
    {
        // Notification settings (single row). Password is write-only — never returned.
        app.MapGet("/api/notification-settings", async (AppDbContext db, SecretProtector protector, CancellationToken ct) =>
        {
            var s = await db.InstallSettingsAsync(ct) ?? new NotificationSettings { Id = 1 };
            return Results.Ok(new
            {
                s.TeamsEnabled, TeamsWebhookUrl = protector.Unprotect(s.TeamsWebhookUrl),
                s.EmailEnabled, s.SmtpHost, s.SmtpPort, s.SmtpUseSsl, s.SmtpUsername,
                hasSmtpPassword = !string.IsNullOrEmpty(s.SmtpPassword),
                s.FromAddress, s.DefaultRecipient,
                s.WebhookEnabled, WebhookUrl = protector.Unprotect(s.WebhookUrl),
                hasWebhookSigningSecret = !string.IsNullOrEmpty(s.WebhookSigningSecret),
                s.MinSeverity,
                s.TeamsDigest, s.EmailDigest, s.WebhookDigest, s.DigestFrequency, s.DigestHourUtc, s.FailureAlertThreshold,
                s.MspDigestEnabled, s.MspDigestHourUtc, s.LastMspDigestAt,
            });
        }).RequireAuthorization("RequireAdmin");

        app.MapPut("/api/notification-settings", async (AppDbContext db, SecretProtector protector, AuditLogger audit, NotificationSettings input, CancellationToken ct) =>
        {
            var s = await db.InstallSettingsAsync(ct);
            // Id is store-generated; setting it makes EF include it in the INSERT
            // and SQL Server rejects that against an identity column.
            if (s is null) { s = new NotificationSettings(); db.NotificationSettings.Add(s); }
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
            s.DigestFrequency = string.Equals(input.DigestFrequency?.Trim(), "weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "daily";
            s.DigestHourUtc = Math.Clamp(input.DigestHourUtc, 0, 23);
            s.FailureAlertThreshold = input.FailureAlertThreshold <= 0 ? 3 : input.FailureAlertThreshold;
            s.MspDigestEnabled = input.MspDigestEnabled;
            s.MspDigestHourUtc = Math.Clamp(input.MspDigestHourUtc, 0, 23);
            await db.SaveChangesAsync(ct);
            await audit.WriteMspAsync("settings.update", "settings", "notifications", "notification settings updated", ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("RequireAdmin");

        // ── Per-client routing (MSP): where THIS tenant's alerts go, layered over
        //    the install-wide settings above. Read: Analyst. Write: Admin. ──
        app.MapGet("/api/notification-routing", async (AppDbContext db, SecretProtector protector, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct) =>
        {
            var r = await db.TenantNotificationRoutings.AsNoTracking().FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                exists = r is not null,
                notifyMsp = r?.NotifyMsp ?? true,
                notifyClient = r?.NotifyClient ?? false,
                recipientEmail = r?.RecipientEmail,
                // A Teams incoming-webhook URL lets whoever holds it post into the
                // client's channel: only the Admins who edit it get it back.
                teamsWebhookUrl = user.IsInRole(AppRoles.Admin) ? protector.Unprotect(r?.TeamsWebhookUrl) : null,
                hasTeamsWebhookUrl = !string.IsNullOrEmpty(r?.TeamsWebhookUrl),
                hasWebhookUrl = !string.IsNullOrEmpty(r?.WebhookUrl),
                minSeverity = r?.MinSeverity,
                lastDigestAt = r?.LastDigestAt,
            });
        }).RequireAuthorization("RequireAnalyst");

        app.MapPut("/api/notification-routing", async (AppDbContext db, SecretProtector protector, AuditLogger audit, RoutingUpdate input, CancellationToken ct) =>
        {
            var r = await db.TenantNotificationRoutings.FirstOrDefaultAsync(ct);
            if (r is null) { r = new TenantNotificationRouting(); db.TenantNotificationRoutings.Add(r); }
            r.NotifyMsp = input.NotifyMsp;
            r.NotifyClient = input.NotifyClient;
            r.RecipientEmail = string.IsNullOrWhiteSpace(input.RecipientEmail) ? null : input.RecipientEmail.Trim();
            r.TeamsWebhookUrl = string.IsNullOrWhiteSpace(input.TeamsWebhookUrl) ? null : protector.Protect(input.TeamsWebhookUrl.Trim());
            if (input.WebhookUrl is not null) // null = keep; "" = clear
                r.WebhookUrl = string.IsNullOrWhiteSpace(input.WebhookUrl) ? null : protector.Protect(input.WebhookUrl.Trim());
            r.MinSeverity = string.IsNullOrWhiteSpace(input.MinSeverity) ? null : input.MinSeverity.Trim().ToLowerInvariant();
            if (!r.NotifyMsp && !r.NotifyClient)
                return Results.BadRequest(new { ok = false, message = "Alerts must go somewhere: enable the MSP, the client, or both." });
            if (!r.NotifyMsp && r.RecipientEmail is null && r.TeamsWebhookUrl is null && r.WebhookUrl is null)
                return Results.BadRequest(new { ok = false, message = "Alerts must go somewhere: with the MSP left out, give the client an email address, a Teams webhook or a webhook." });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("settings.routing", "settings", "routing", $"msp={r.NotifyMsp} client={r.NotifyClient}", ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("RequireAdmin");

        // Send a test notification through all enabled channels
        app.MapPost("/api/notification-settings/test", async (AppDbContext db, NotificationSender sender, CancellationToken ct) =>
        {
            var cfg = await db.InstallSettingsAsync(ct);
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
