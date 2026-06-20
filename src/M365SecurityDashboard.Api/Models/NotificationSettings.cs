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

    /// <summary>Only send notifications at or above this severity (low|medium|high|critical).</summary>
    [MaxLength(20)]
    public string MinSeverity { get; set; } = "low";
}
