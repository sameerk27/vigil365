using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// One monitored Microsoft 365 tenant. The root of tenant isolation: every
/// tenant-scoped row carries this row's <see cref="Id"/>, and the global query
/// filter in <c>AppDbContext</c> confines each request or worker pass to one of
/// these at a time.
///
/// A single-tenant (Edition 1) install has exactly one row, the well-known
/// <see cref="DefaultId"/>, created by the tenancy migration and resolved
/// automatically for every request. An MSP install has one row per client.
/// Per-tenant Graph credentials and onboarding state land here in Phase 5.
/// </summary>
public sealed class ClientTenant
{
    /// <summary>
    /// The tenant every pre-tenancy install is migrated into, and the one a
    /// single-tenant install keeps using. Fixed so the migration can reference
    /// it as a column default on both engines without identity/sequence games.
    /// </summary>
    public static readonly Guid DefaultId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name chosen by the operator ("Contoso Ltd").</summary>
    [MaxLength(200)]
    public string Name { get; set; } = "";

    /// <summary>The Entra tenant GUID, once known. Null until onboarding records it.</summary>
    [MaxLength(64)]
    public string? MicrosoftTenantId { get; set; }

    /// <summary>Inactive tenants are skipped by every worker and cannot be selected.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // ── Own Graph credentials (a multi-tenant app registration the client's admin
    //    consented to). Null = use the install-wide credentials, if they belong to
    //    this tenant. See TenantGraphCredentials for the resolution rules. ──
    [MaxLength(64)]
    public string? ClientId { get; set; }

    /// <summary>Protected at rest via SecretProtector. Never returned by the API.</summary>
    [MaxLength(2048)]
    public string? ClientSecret { get; set; }

    /// <summary>Sovereign-cloud login authority override; null = install default.</summary>
    [MaxLength(200)]
    public string? LoginInstance { get; set; }

    /// <summary>Sovereign-cloud Graph base URL override; null = install default.</summary>
    [MaxLength(200)]
    public string? BaseUrl { get; set; }

    // ── Certificate auth (preferred over the secret when set) ──
    [MaxLength(64)]
    public string? CertificateThumbprint { get; set; }

    [MaxLength(500)]
    public string? CertificatePath { get; set; }

    /// <summary>Protected at rest.</summary>
    [MaxLength(2048)]
    public string? CertificatePassword { get; set; }

    public bool HasOwnCertificate =>
        !string.IsNullOrWhiteSpace(CertificateThumbprint) || !string.IsNullOrWhiteSpace(CertificatePath);

    public bool HasOwnCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && (!string.IsNullOrWhiteSpace(ClientSecret) || HasOwnCertificate);

    // ── Collection backoff (see CollectionBackoff) ──
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextCollectionAfter { get; set; }

    // ── White-label reporting: shown instead of "Vigil365" on this client's reports ──
    [MaxLength(120)]
    public string? BrandName { get; set; }

    /// <summary>CSS colour, e.g. #1d4ed8. Null = Vigil365 default.</summary>
    [MaxLength(20)]
    public string? BrandAccentColor { get; set; }

    // ── Connection health, written by the tenants API's /test and by the collector ──
    public DateTimeOffset? ConsentGrantedAt { get; set; }
    public DateTimeOffset? LastCollectionAt { get; set; }

    [MaxLength(40)]
    public string? LastCollectionStatus { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
}

/// <summary>
/// A row that belongs to exactly one tenant. Reads are confined to the current
/// tenant by a global query filter; writes are stamped with, and checked
/// against, the current tenant in SaveChanges. Reading or writing with no
/// current tenant is an error, never "all tenants".
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

/// <summary>
/// A row that is either an MSP-wide default (<c>TenantId == null</c>) or a
/// per-tenant override. Reads return the current tenant's rows plus the
/// defaults; with no current tenant, only the defaults. Never auto-stamped —
/// a null written stays null, because null means "the default".
/// </summary>
public interface ITenantOptional
{
    Guid? TenantId { get; set; }
}
