using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Singleton row (Id = 1) holding notification delivery configuration.
/// </summary>
public sealed class NotificationSettings
{
    public int Id { get; set; } = 1;

    // ── Microsoft Teams / Slack incoming webhook ──
    public bool TeamsEnabled { get; set; }

    [MaxLength(2048)]
    public string? TeamsWebhookUrl { get; set; }

    // ── Email (SMTP) ──
    public bool EmailEnabled { get; set; }

    [MaxLength(256)]
    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;

    [MaxLength(256)]
    public string? SmtpUsername { get; set; }

    [MaxLength(512)]
    public string? SmtpPassword { get; set; }

    [MaxLength(320)]
    public string? FromAddress { get; set; }

    [MaxLength(320)]
    public string? DefaultRecipient { get; set; }

    // ── Generic webhook (SIEM / Power Automate / custom) ──
    public bool WebhookEnabled { get; set; }

    [MaxLength(2048)]
    public string? WebhookUrl { get; set; }

    /// <summary>Optional DPAPI-protected HMAC secret used to sign SIEM webhook payloads.</summary>
    [MaxLength(512)]
    public string? WebhookSigningSecret { get; set; }

    /// <summary>Only send notifications at or above this severity (low|medium|high|critical).</summary>
    [MaxLength(20)]
    public string MinSeverity { get; set; } = "low";

    // ── Digest mode (daily/weekly rollup) ──
    // When a channel's digest flag is on, individual alerts are NOT sent instantly
    // on that channel; instead they are batched into a single rollup sent at
    // DigestHourUtc by the NotificationDigestWorker.
    public bool TeamsDigest { get; set; }
    public bool EmailDigest { get; set; }
    public bool WebhookDigest { get; set; }

    /// <summary>Frequency of the digest: "daily" or "weekly" (sent on Monday).</summary>
    [MaxLength(20)]
    public string DigestFrequency { get; set; } = "daily";

    /// <summary>Hour of day (UTC, 0–23) at which the digest rollup is sent.</summary>
    public int DigestHourUtc { get; set; } = 8;

    /// <summary>Watermark: alerts triggered after this instant are pending inclusion in the next digest.</summary>
    public DateTimeOffset? LastDigestAt { get; set; }

    // ── Delivery-failure alerting ──
    /// <summary>Raise a delivery-failure alert once a channel reaches this many consecutive failed attempts.</summary>
    public int FailureAlertThreshold { get; set; } = 3;

    /// <summary>When the last delivery-failure alert was raised (debounce so we don't re-alert every cycle).</summary>
    public DateTimeOffset? LastFailureAlertAt { get; set; }
}
