using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Which client tenants a non-Admin staff member may see. Admins see every
/// tenant and have no rows here. A user with no rows in a multi-tenant install
/// can select nothing and therefore sees no tenant data — access is granted,
/// never assumed. In a single-tenant install assignments are irrelevant: the
/// sole tenant applies to everyone.
///
/// Operator-level (global) data: it describes staff, not a client.
/// </summary>
public sealed class UserTenantAssignment
{
    [MaxLength(320)]
    public required string UserEmail { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    [MaxLength(320)]
    public string? AssignedBy { get; set; }
}
