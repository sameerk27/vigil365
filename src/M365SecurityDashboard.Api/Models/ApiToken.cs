using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Hashed bearer token for machine integrations such as SIEM collectors.
/// The raw token is returned once at creation time and is never stored.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string Prefix { get; set; } = "";

    [MaxLength(128)]
    public string TokenHash { get; set; } = "";

    [MaxLength(400)]
    public string Scopes { get; set; } = "alerts:read,health:read";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(320)]
    public string? CreatedBy { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
