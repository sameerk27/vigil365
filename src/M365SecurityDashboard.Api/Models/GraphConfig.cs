namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Graph credentials entered at runtime via the first-run setup wizard, so an
/// installer never has to hand-edit appsettings. Single row (Id = 1). The client
/// secret is stored DPAPI-encrypted at rest (via SecretProtector); when present,
/// these values are applied over the GraphOptions singleton at startup.
/// </summary>
public sealed class GraphConfig
{
    public int Id { get; set; } = 1;
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>DPAPI-encrypted client secret. Never returned by the API.</summary>
    public string? ClientSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
