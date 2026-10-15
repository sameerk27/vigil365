using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Data.Tenancy;

public static class TenantBootstrap
{
    /// <summary>
    /// Startup: make sure the install has a tenant to work in. The tenancy
    /// migration seeds the default tenant, so this is a backstop for a database
    /// whose seed row was deleted. Returns the sole active tenant when there is
    /// exactly one — the single-tenant case the rest of startup (demo seeding,
    /// demo purge) is allowed to run in — and null for an MSP install, where
    /// those single-tenant conveniences do not apply.
    /// </summary>
    public static Guid? EnsureTenant(AppDbContext db, ILogger logger)
    {
        if (!db.ClientTenants.Any())
        {
            var graphTenant = db.GraphConfig.OrderBy(g => g.Id).Select(g => g.TenantId).FirstOrDefault();
            db.ClientTenants.Add(new ClientTenant
            {
                Id = ClientTenant.DefaultId,
                Name = "Default",
                MicrosoftTenantId = string.IsNullOrWhiteSpace(graphTenant) ? null : graphTenant,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
            logger.LogInformation("Created the default tenant {TenantId}.", ClientTenant.DefaultId);
        }

        var active = db.ClientTenants.Where(t => t.IsActive).Select(t => t.Id).Take(2).ToList();
        return active.Count == 1 ? active[0] : null;
    }
}
