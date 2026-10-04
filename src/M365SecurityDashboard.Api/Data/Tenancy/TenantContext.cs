namespace M365SecurityDashboard.Api.Data.Tenancy;

/// <summary>
/// The ambient "which tenant is this work for" that <see cref="AppDbContext"/>
/// reads at query and save time. Scoped: one per request or per worker pass.
/// </summary>
public interface ITenantContext
{
    /// <summary>The active tenant, or null when none has been resolved.</summary>
    Guid? Current { get; }
}

/// <summary>
/// Mutable, scoped implementation. The request middleware or a worker's
/// per-tenant loop sets it exactly once; nothing downstream reassigns it.
/// </summary>
public sealed class TenantContext(Guid? initial = null) : ITenantContext
{
    public Guid? Current { get; private set; } = initial;

    public void Set(Guid tenantId)
    {
        if (Current is not null && Current != tenantId)
            throw new InvalidOperationException(
                $"Tenant context already set to {Current}; refusing to switch to {tenantId} within one scope. Create a new scope per tenant.");
        Current = tenantId;
    }
}

/// <summary>What a context constructed with no tenant context gets: nothing, so scoped reads fail closed.</summary>
public sealed class NoTenantContext : ITenantContext
{
    public static readonly NoTenantContext Instance = new();
    public Guid? Current => null;
}

/// <summary>
/// Thrown when tenant-scoped data is read or written with no current tenant.
/// The request middleware maps it to 400; in a worker it is a bug.
/// </summary>
public sealed class TenantRequiredException(string operation)
    : InvalidOperationException($"No tenant selected for {operation}. Tenant-scoped data cannot be accessed without a tenant context.");

/// <summary>Thrown when a write would land in, or move a row into, a tenant other than the current one.</summary>
public sealed class CrossTenantWriteException(string entity, Guid attempted, Guid current)
    : InvalidOperationException($"Refusing to write {entity} for tenant {attempted} while the current tenant is {current}.");
