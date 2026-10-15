using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Data.Tenancy;

/// <summary>
/// The explicit tenancy classification of every entity. An entity that is not
/// listed here is a bug, and a test fails when one appears: an unclassified
/// entity is an isolation hole waiting to be found in production.
///
/// The interfaces on the entities are what the context enforces; this list is
/// the reviewable statement of intent that the tests hold the model to.
/// </summary>
public static class TenantClassification
{
    /// <summary>Belongs to exactly one tenant. Filtered, stamped, guarded.</summary>
    public static readonly IReadOnlySet<Type> Scoped = new HashSet<Type>
    {
        typeof(SecurityAlert),
        typeof(CollectionRun),
        typeof(TriggeredAlert),
        typeof(NotificationLog),
        typeof(TrendSnapshot),
        typeof(AlertNote),
        typeof(SuppressionRule),
        typeof(AuditEvent),
        typeof(TenantBaseline),
        typeof(TenantNotificationRouting),
        typeof(AlertPolicyTenantOverride),
    };

    /// <summary>MSP-wide default (null) or per-tenant override.</summary>
    public static readonly IReadOnlySet<Type> Optional = new HashSet<Type>
    {
        typeof(AlertPolicy),
        typeof(NotificationSettings),
        typeof(ReportSchedule),
        typeof(MetricsCounters),
        // MSP-level actions record null; tenant-scoped actions record the tenant.
        // The hash chain stays global (see AuditLogger), and the entries outlive
        // a purged client (no foreign key, see AppDbContext).
        typeof(AuditEntry),
    };

    /// <summary>Belongs to the operator, not to any client. Never filtered.</summary>
    public static readonly IReadOnlySet<Type> Global = new HashSet<Type>
    {
        typeof(ClientTenant),
        typeof(AppUser),
        typeof(UserTenantAssignment),
        typeof(ApiToken),
        // Single-tenant Graph credentials. Superseded per tenant in Phase 5.
        typeof(GraphConfig),
    };
}
