using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Data.Tenancy;

/// <summary>
/// The one way a background worker touches tenant data: one DI scope per
/// active tenant, with the scope's <see cref="TenantContext"/> set before any
/// service in it is resolved. A worker that resolved an <c>AppDbContext</c>
/// from its own root scope would have no tenant and fail closed — by design.
///
/// Each tenant's pass is isolated: an exception in one is logged and the others
/// continue, so a single client with expired consent or a throttled Graph cannot
/// stall every other client's collection, digest, report or prune.
///
/// <paramref name="maxParallel"/> tenants run at once (default 1: strictly
/// sequential, the safe choice for anything that is not collection), and
/// starts are spaced by <paramref name="stagger"/> so a burst of clients does
/// not hit Graph in the same second.
/// </summary>
public static class TenantIterator
{
    public static async Task ForEachActiveTenantAsync(
        IServiceProvider services,
        ILogger logger,
        string activity,
        Func<IServiceProvider, ClientTenant, CancellationToken, Task> body,
        CancellationToken ct,
        int maxParallel = 1,
        TimeSpan? stagger = null,
        Func<ClientTenant, bool>? include = null)
    {
        List<ClientTenant> tenants;
        using (var listing = services.CreateScope())
        {
            var db = listing.ServiceProvider.GetRequiredService<AppDbContext>();
            tenants = await db.ClientTenants.AsNoTracking()
                .Where(t => t.IsActive).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        }
        if (include is not null) tenants = tenants.Where(include).ToList();

        if (tenants.Count == 0)
        {
            logger.LogWarning("{Activity}: no active tenants — nothing to do.", activity);
            return;
        }

        var parallel = Math.Max(1, maxParallel);
        var gap = stagger ?? TimeSpan.Zero;
        using var slots = new SemaphoreSlim(parallel, parallel);
        var tasks = new List<Task>(tenants.Count);

        for (var i = 0; i < tenants.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tenant = tenants[i];
            await slots.WaitAsync(ct);
            if (i > 0 && gap > TimeSpan.Zero && parallel > 1)
            {
                try { await Task.Delay(gap, ct); }
                catch (OperationCanceledException) { slots.Release(); throw; }
            }

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await RunOneAsync(services, logger, activity, body, tenant, ct);
                }
                finally
                {
                    slots.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task RunOneAsync(
        IServiceProvider services, ILogger logger, string activity,
        Func<IServiceProvider, ClientTenant, CancellationToken, Task> body,
        ClientTenant tenant, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.Id);
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenant.Id, ["TenantName"] = tenant.Name });
        try
        {
            await body(scope.ServiceProvider, tenant, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Activity} failed for tenant {TenantName} ({TenantId}); continuing with the next tenant.",
                activity, tenant.Name, tenant.Id);
        }
    }
}
