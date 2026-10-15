using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Where one client tenant's alerts go. Layered over the install-wide
/// <see cref="NotificationSettings"/> (the MSP's SMTP server, default channels,
/// default recipient) at dispatch time — see <c>NotificationRouting</c>:
///
///   • the MSP's default recipient and channels (<see cref="NotifyMsp"/>),
///   • and/or the client's own recipient / Teams / webhook (<see cref="NotifyClient"/>),
///   • with an optional per-client minimum severity.
///
/// Also holds the per-tenant digest and failure-alert timestamps, because those
/// must advance per tenant — one shared timestamp would let the first tenant's
/// digest suppress every other tenant's in the same hourly pass.
///
/// Tenant-scoped, one row per tenant (keyed by the tenant), created on demand.
/// </summary>
public sealed class TenantNotificationRouting : ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>Send this client's alerts to the MSP's default recipient/channels. Default on.</summary>
    public bool NotifyMsp { get; set; } = true;

    /// <summary>Send this client's alerts to the client's own recipient/channels below.</summary>
    public bool NotifyClient { get; set; }

    [MaxLength(1000)]
    public string? RecipientEmail { get; set; }

    /// <summary>Protected at rest. Null = use the MSP's Teams webhook (if NotifyMsp).</summary>
    [MaxLength(2048)]
    public string? TeamsWebhookUrl { get; set; }

    /// <summary>Protected at rest. Null = use the MSP's webhook (if NotifyMsp).</summary>
    [MaxLength(2048)]
    public string? WebhookUrl { get; set; }

    /// <summary>Null = the install-wide minimum severity.</summary>
    [MaxLength(20)]
    public string? MinSeverity { get; set; }

    public DateTimeOffset? LastDigestAt { get; set; }
    public DateTimeOffset? LastFailureAlertAt { get; set; }
}

/// <summary>
/// A client-specific adjustment to an MSP-wide default alert policy: switch it
/// off for this client, or change its threshold or notification address,
/// without touching the shared policy. Tenant-scoped; keyed by (tenant, policy).
/// Tenant-specific policies (a policy row with a TenantId) need no override —
/// they already belong to one client.
/// </summary>
public sealed class AlertPolicyTenantOverride : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid PolicyId { get; set; }

    /// <summary>Null = inherit. False switches the default policy off for this client.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Null = inherit the default policy's threshold.</summary>
    public int? Threshold { get; set; }

    /// <summary>Null = inherit.</summary>
    [MaxLength(320)]
    public string? NotifyEmail { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    [MaxLength(320)]
    public string? UpdatedBy { get; set; }
}
