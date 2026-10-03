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
///   no recipient = the MSP's From mailbox only while the MSP is notified; otherwise no email
/// A policy's NotifyEmail stands in for the MSP default recipient for that policy's alerts.
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
        var (install, routing) = await db.NotificationInputsAsync(ct);
        return Apply(install, routing);
    }

    /// <summary>The inputs to <see cref="Apply"/> for the current tenant: the install-wide row and this tenant's routing. Detached.</summary>
    public static async Task<(NotificationSettings Install, TenantNotificationRouting? Routing)> NotificationInputsAsync(this AppDbContext db, CancellationToken ct)
    {
        var install = await db.NotificationSettings.AsNoTracking().Where(s => s.TenantId == null).OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
                      ?? new NotificationSettings { Id = 1 };
        var routing = db.CurrentTenantIdOrNull is null
            ? null
            : await db.TenantNotificationRoutings.AsNoTracking().FirstOrDefaultAsync(ct);
        return (install, routing);
    }

    /// <param name="policyRecipient">A policy's NotifyEmail (or this client's override of it); stands in for the MSP's default recipient.</param>
    public static NotificationSettings Apply(NotificationSettings install, TenantNotificationRouting? routing, string? policyRecipient = null)
    {
        var cfg = Clone(install);
        var mspRecipient = string.IsNullOrWhiteSpace(policyRecipient) ? install.DefaultRecipient : policyRecipient.Trim();
        cfg.DefaultRecipient = mspRecipient;
        if (routing is null) return cfg;

        var recipients = new List<string>();
        if (routing.NotifyMsp && !string.IsNullOrWhiteSpace(mspRecipient)) recipients.Add(mspRecipient.Trim());
        if (routing.NotifyClient && !string.IsNullOrWhiteSpace(routing.RecipientEmail)) recipients.Add(routing.RecipientEmail.Trim());
        // MailMessage.To.Add accepts a comma-separated list, and SendReportEmailAsync
        // splits on the same character, so one string serves both paths.
        cfg.DefaultRecipient = recipients.Count == 0 ? null : string.Join(",", recipients.Distinct(StringComparer.OrdinalIgnoreCase));
        // The From mailbox is the MSP's: a client routed away from the MSP never falls back to it.
        cfg.FromAddressFallback = routing.NotifyMsp;

        cfg.TeamsWebhookUrl = routing.NotifyClient && !string.IsNullOrWhiteSpace(routing.TeamsWebhookUrl) ? routing.TeamsWebhookUrl
            : routing.NotifyMsp ? install.TeamsWebhookUrl : null;
        var clientWebhook = routing.NotifyClient && !string.IsNullOrWhiteSpace(routing.WebhookUrl);
        cfg.WebhookUrl = clientWebhook ? routing.WebhookUrl
            : routing.NotifyMsp ? install.WebhookUrl : null;
        // The MSP's secret signs only the MSP's webhook: a client's endpoint holding
        // MSP-signed payloads could replay them to the MSP's own receiver.
        if (clientWebhook || !routing.NotifyMsp) cfg.WebhookSigningSecret = null;

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
