using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class RoleClaimsTransformationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly RoleClaimsTransformation _transformer;

    public RoleClaimsTransformationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _transformer = new RoleClaimsTransformation(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
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
}
