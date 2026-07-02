using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class RoleClaimsTransformationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MemoryCache _cache;
    private readonly RoleClaimsTransformation _transformer;

    public RoleClaimsTransformationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _transformer = new RoleClaimsTransformation(_db, _cache);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TransformAsync_UnauthenticatedPrincipal_ReturnsUnchanged()
    {
        var identity = new ClaimsIdentity(); // not authenticated
        var principal = new ClaimsPrincipal(identity);

        var result = await _transformer.TransformAsync(principal);

        Assert.Empty(result.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public async Task TransformAsync_PrincipalAlreadyHasRoleClaim_ReturnsUnchanged()
    {
        var identity = new ClaimsIdentity("TestAuthType");
        identity.AddClaim(new Claim(ClaimTypes.Role, AppRoles.Admin));
        var principal = new ClaimsPrincipal(identity);

        var result = await _transformer.TransformAsync(principal);

        var roles = result.FindAll(ClaimTypes.Role).ToList();
        Assert.Single(roles);
        Assert.Equal(AppRoles.Admin, roles[0].Value);
    }

    [Fact]
    public async Task TransformAsync_UserInDatabase_AddsRoleClaimFromDb()
    {
        var email = "analyst@contoso.com";
        _db.AppUsers.Add(new AppUser { Email = email, Role = AppRoles.Analyst, CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var identity = new ClaimsIdentity("TestAuthType");
        identity.AddClaim(new Claim("preferred_username", email));
        var principal = new ClaimsPrincipal(identity);

        var result = await _transformer.TransformAsync(principal);

        var roleClaim = result.FindFirst(ClaimTypes.Role);
        Assert.NotNull(roleClaim);
        Assert.Equal(AppRoles.Analyst, roleClaim!.Value);
    }

    [Fact]
    public async Task TransformAsync_UserNotInDatabase_AddsViewerRoleClaim()
    {
        var email = "newuser@contoso.com";
        var identity = new ClaimsIdentity("TestAuthType");
        identity.AddClaim(new Claim("preferred_username", email));
        var principal = new ClaimsPrincipal(identity);

        var result = await _transformer.TransformAsync(principal);

        var roleClaim = result.FindFirst(ClaimTypes.Role);
        Assert.NotNull(roleClaim);
        Assert.Equal(AppRoles.Viewer, roleClaim!.Value);
    }

    [Fact]
    public async Task TransformAsync_SecondCall_ServesRoleFromCacheNotDb()
    {
        var email = "cached@contoso.com";
        _db.AppUsers.Add(new AppUser { Email = email, Role = AppRoles.Analyst, CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        ClaimsPrincipal MakePrincipal()
        {
            var identity = new ClaimsIdentity("TestAuthType");
            identity.AddClaim(new Claim("preferred_username", email));
            return new ClaimsPrincipal(identity);
        }

        await _transformer.TransformAsync(MakePrincipal());

        // Change the role in the DB without evicting — the cached value must win
        // until the TTL expires or an admin endpoint evicts the key.
        var user = await _db.AppUsers.SingleAsync(u => u.Email == email);
        user.Role = AppRoles.Admin;
        await _db.SaveChangesAsync();

        var result = await _transformer.TransformAsync(MakePrincipal());
        Assert.Equal(AppRoles.Analyst, result.FindFirst(ClaimTypes.Role)!.Value);

        // After eviction (what the role-change endpoint does) the new role applies.
        _cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
        var refreshed = await _transformer.TransformAsync(MakePrincipal());
        Assert.Equal(AppRoles.Admin, refreshed.FindFirst(ClaimTypes.Role)!.Value);
    }
}
