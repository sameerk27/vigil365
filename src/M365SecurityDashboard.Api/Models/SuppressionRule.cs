using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A standing rule that stops an alert from being raised at all — the answer to
/// "this service account trips this policy every night and we know about it".
///
/// Distinct from snooze, which silences one already-raised alert for a while.
/// A suppression is a durable statement about a class of alerts, so it is
/// audited, attributable (who and why), and optionally time-bounded.
///
/// Scope is the intersection of the fields that are set:
///   PolicyId set, EntityPattern null  -> the whole policy is suppressed
///   both set                          -> only that policy, for matching entities
///   PolicyId null, EntityPattern set  -> that entity, across every policy
/// </summary>
public class SuppressionRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Policy this rule applies to. Null = all policies.</summary>
    public Guid? PolicyId { get; set; }

    /// <summary>
    /// Case-insensitive match against an affected entity (UPN or device name).
    /// Supports a single leading and/or trailing '*' wildcard, e.g.
    /// "svc-*", "*@contractors.example.com". Null = no entity restriction.
    /// </summary>
    [MaxLength(320)]
    public string? EntityPattern { get; set; }

    /// <summary>Why this suppression exists. Required — an unexplained
    /// suppression is indistinguishable from a bug six months later.</summary>
    [MaxLength(500)]
    public string Reason { get; set; } = "";

    /// <summary>When the rule stops applying. Null = indefinite.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(320)]
    public string? CreatedBy { get; set; }

    /// <summary>Count of alerts this rule has prevented — makes an over-broad
    /// rule visible instead of silently swallowing everything.</summary>
    public int SuppressedCount { get; set; }

    public DateTimeOffset? LastSuppressedAt { get; set; }
}
