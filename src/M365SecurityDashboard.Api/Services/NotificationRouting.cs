using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Resolves the notification settings in effect for the current tenant: the
/// install-wide row (the MSP's SMTP server, channels and default recipient) with
/// the tenant's <see cref="TenantNotificationRouting"/> layered on top. The
/// result is a detached copy — never save it; the install-wide row is edited
/// only through the notification-settings API.
///
/// Routing rules (pure, tested in NotificationRoutingTests):
///   recipients = (NotifyMsp ? MSP default recipient) + (NotifyClient ? client recipient)
///   Teams/webhook = client's own URL if NotifyClient and set; else the MSP's if NotifyMsp; else none
///   minimum severity = client's if set; else the MSP's
/// A tenant with no routing row behaves exactly as before: everything to the MSP.
/// </summary>
public static class NotificationRouting
{
    /// <summary>The install-wide settings row (TenantId null), tracked, for editing.</summary>
    public static Task<NotificationSettings?> InstallSettingsAsync(this AppDbContext db, CancellationToken ct)
        => db.NotificationSettings.Where(s => s.TenantId == null).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

    /// <summary>The settings to dispatch with for the current tenant. Detached; do not save.</summary>
    public static async Task<NotificationSettings> EffectiveNotificationSettingsAsync(this AppDbContext db, CancellationToken ct)
    {
        var install = await db.NotificationSettings.AsNoTracking().Where(s => s.TenantId == null).OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
                      ?? new NotificationSettings { Id = 1 };
        var routing = db.CurrentTenantIdOrNull is null
            ? null
            : await db.TenantNotificationRoutings.AsNoTracking().FirstOrDefaultAsync(ct);
        return Apply(install, routing);
    }

    public static NotificationSettings Apply(NotificationSettings install, TenantNotificationRouting? routing)
    {
        var cfg = Clone(install);
        if (routing is null) return cfg;

        var recipients = new List<string>();
        if (routing.NotifyMsp && !string.IsNullOrWhiteSpace(install.DefaultRecipient)) recipients.Add(install.DefaultRecipient.Trim());
        if (routing.NotifyClient && !string.IsNullOrWhiteSpace(routing.RecipientEmail)) recipients.Add(routing.RecipientEmail.Trim());
        // MailMessage.To.Add accepts a comma-separated list, and SendReportEmailAsync
        // splits on the same character, so one string serves both paths.
        cfg.DefaultRecipient = recipients.Count == 0 ? null : string.Join(",", recipients.Distinct(StringComparer.OrdinalIgnoreCase));

        cfg.TeamsWebhookUrl = routing.NotifyClient && !string.IsNullOrWhiteSpace(routing.TeamsWebhookUrl) ? routing.TeamsWebhookUrl
            : routing.NotifyMsp ? install.TeamsWebhookUrl : null;
        cfg.WebhookUrl = routing.NotifyClient && !string.IsNullOrWhiteSpace(routing.WebhookUrl) ? routing.WebhookUrl
            : routing.NotifyMsp ? install.WebhookUrl : null;
        if (!routing.NotifyMsp) cfg.WebhookSigningSecret = null; // the MSP's secret signs only the MSP's webhook

        if (!string.IsNullOrWhiteSpace(routing.MinSeverity)) cfg.MinSeverity = routing.MinSeverity;

        // Per-tenant digest/failure state lives on the routing row.
        cfg.LastDigestAt = routing.LastDigestAt;
        cfg.LastFailureAlertAt = routing.LastFailureAlertAt;
        return cfg;
    }

    private static NotificationSettings Clone(NotificationSettings s) => new()
    {
        Id = s.Id, TenantId = s.TenantId,
        TeamsEnabled = s.TeamsEnabled, TeamsWebhookUrl = s.TeamsWebhookUrl,
        EmailEnabled = s.EmailEnabled, SmtpHost = s.SmtpHost, SmtpPort = s.SmtpPort, SmtpUseSsl = s.SmtpUseSsl,
        SmtpUsername = s.SmtpUsername, SmtpPassword = s.SmtpPassword, FromAddress = s.FromAddress, DefaultRecipient = s.DefaultRecipient,
        WebhookEnabled = s.WebhookEnabled, WebhookUrl = s.WebhookUrl, WebhookSigningSecret = s.WebhookSigningSecret,
        MinSeverity = s.MinSeverity, TeamsDigest = s.TeamsDigest, EmailDigest = s.EmailDigest, WebhookDigest = s.WebhookDigest,
        DigestFrequency = s.DigestFrequency, DigestHourUtc = s.DigestHourUtc, LastDigestAt = s.LastDigestAt,
        FailureAlertThreshold = s.FailureAlertThreshold, LastFailureAlertAt = s.LastFailureAlertAt,
        MspDigestEnabled = s.MspDigestEnabled, MspDigestHourUtc = s.MspDigestHourUtc, LastMspDigestAt = s.LastMspDigestAt,
    };
}
