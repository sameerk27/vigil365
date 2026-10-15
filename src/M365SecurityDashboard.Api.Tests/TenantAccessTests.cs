using System.Security.Claims;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace M365SecurityDashboard.Api.Tests;

public sealed class TenantAccessTests
{
    private static ClaimsPrincipal User(string email, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly Guid A = TestTenancy.TenantA, B = TestTenancy.TenantB, C = TestTenancy.TenantC;

    private static async Task<(Data.AppDbContext db, TenantAccess access, IMemoryCache cache)> SetupAsync()
    {
        var db = TestAppDbContextFactory.Create();
        db.ClientTenants.AddRange(
            new ClientTenant { Id = A, Name = "A", CreatedAt = DateTimeOffset.UtcNow },
            new ClientTenant { Id = B, Name = "B", CreatedAt = DateTimeOffset.UtcNow },
            new ClientTenant { Id = C, Name = "C (inactive)", IsActive = false, CreatedAt = DateTimeOffset.UtcNow });
        db.UserTenantAssignments.AddRange(
            new UserTenantAssignment { UserEmail = "analyst@msp.test", TenantId = A, AssignedAt = DateTimeOffset.UtcNow },
            new UserTenantAssignment { UserEmail = "analyst@msp.test", TenantId = C, AssignedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        return (db, new TenantAccess(db, cache), cache);
    }

    [Fact]
    public async Task Admin_sees_every_active_tenant_and_no_inactive_one()
    {
        var (_, access, _) = await SetupAsync();
        var tenants = await access.PermittedTenantsAsync(User("admin@msp.test", AppRoles.Admin), CancellationToken.None);
        Assert.Equal(new[] { "A", "B" }, tenants.Select(t => t.Name));
    }

    [Fact]
    public async Task Non_admin_sees_only_assigned_active_tenants()
    {
        var (_, access, _) = await SetupAsync();
        var tenants = await access.PermittedTenantsAsync(User("analyst@msp.test", AppRoles.Analyst), CancellationToken.None);
        Assert.Equal(new[] { "A" }, tenants.Select(t => t.Name)); // C is assigned but inactive
        Assert.True(await access.CanSelectAsync(User("analyst@msp.test", AppRoles.Analyst), A, CancellationToken.None));
        Assert.False(await access.CanSelectAsync(User("analyst@msp.test", AppRoles.Analyst), B, CancellationToken.None));
        Assert.False(await access.CanSelectAsync(User("analyst@msp.test", AppRoles.Analyst), C, CancellationToken.None));
    }

    [Fact]
    public async Task Unassigned_non_admin_sees_nothing()
    {
        // Access is granted, never assumed: a new Viewer in an MSP install sees no client.
        var (_, access, _) = await SetupAsync();
        Assert.Empty(await access.PermittedTenantsAsync(User("new@msp.test", AppRoles.Viewer), CancellationToken.None));
    }

    [Fact]
    public async Task Assignment_changes_take_effect_after_invalidation()
    {
        var (db, access, cache) = await SetupAsync();
        var viewer = User("viewer@msp.test", AppRoles.Viewer);
        Assert.Empty(await access.PermittedTenantsAsync(viewer, CancellationToken.None));

        db.UserTenantAssignments.Add(new UserTenantAssignment { UserEmail = "viewer@msp.test", TenantId = B, AssignedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Assert.Empty(await access.PermittedTenantsAsync(viewer, CancellationToken.None)); // cached

        TenantAccess.Invalidate(cache, "viewer@msp.test");
        Assert.Equal(new[] { "B" }, (await access.PermittedTenantsAsync(viewer, CancellationToken.None)).Select(t => t.Name));
    }

    [Fact]
    public async Task Email_match_is_case_insensitive_for_the_cache_but_exact_for_the_row()
    {
        var (_, access, _) = await SetupAsync();
        var upper = User("ANALYST@msp.test", AppRoles.Analyst);
        // AuthHelpers lower-cases the claim, so the assignment row (stored lower-case) matches.
        Assert.Equal(new[] { "A" }, (await access.PermittedTenantsAsync(upper, CancellationToken.None)).Select(t => t.Name));
    }
}
