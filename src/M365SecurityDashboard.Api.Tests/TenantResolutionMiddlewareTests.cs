using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Which tenant an API request runs in. Every direct API call goes through this,
/// so it — not the UI's client gate — is what stops a user reading a client they
/// are not assigned to. The stand-in endpoint reads tenant-scoped data, so "no
/// tenant" surfaces as the 400 a real endpoint gives.
/// </summary>
public sealed class TenantResolutionMiddlewareTests
{
    private static readonly Guid A = TestTenancy.TenantA, B = TestTenancy.TenantB, C = TestTenancy.TenantC;

    private static ClaimsPrincipal User(string email, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Role, role)], "test"));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private sealed record Outcome(int Status, Guid? Tenant);
    private static Outcome Selected(Guid tenant) => new(StatusCodes.Status200OK, tenant);
    private static Outcome Refused(int status) => new(status, null);

    private sealed class Fixture(EditionMode mode = EditionMode.Msp)
    {
        private readonly AppDbContext _db = TestAppDbContextFactory.Create();

        public Fixture Tenant(Guid id, bool active = true)
        {
            _db.ClientTenants.Add(new ClientTenant { Id = id, Name = id.ToString()[..8], IsActive = active, CreatedAt = DateTimeOffset.UtcNow });
            _db.SaveChanges();
            return this;
        }

        public Fixture Assign(string email, Guid tenant)
        {
            _db.UserTenantAssignments.Add(new UserTenantAssignment { UserEmail = email, TenantId = tenant, AssignedAt = DateTimeOffset.UtcNow });
            _db.SaveChanges();
            return this;
        }

        /// <summary>A SIEM token, restricted to one client or install-wide; returns the raw token.</summary>
        public string Token(Guid? restrictTo)
        {
            var (row, raw) = ApiTokenService.Create("SIEM", "alerts:read", null, null);
            row.TenantId = restrictTo;
            _db.ApiTokens.Add(row);
            _db.SaveChanges();
            return raw;
        }

        public async Task<Outcome> SendAsync(ClaimsPrincipal user, string? header = null, string path = "/api/dashboard", string? apiKey = null)
        {
            var tenant = new TenantContext();
            Guid? seen = null;
            var middleware = new TenantResolutionMiddleware(_ =>
            {
                seen = tenant.Current ?? throw new TenantRequiredException("the test endpoint");
                return Task.CompletedTask;
            });

            var ctx = new DefaultHttpContext { User = user };
            ctx.Request.Path = path;
            ctx.Response.Body = new MemoryStream();
            if (header is not null) ctx.Request.Headers[TenantResolutionMiddleware.HeaderName] = header;
            if (apiKey is not null) ctx.Request.Headers["X-Api-Key"] = apiKey;

            var cache = new MemoryCache(new MemoryCacheOptions());
            await middleware.InvokeAsync(ctx, tenant, _db, cache, new TenantAccess(_db, cache), new ApiTokenService(_db),
                Options.Create(new EditionOptions { Mode = mode }));
            return new Outcome(ctx.Response.StatusCode, seen);
        }
    }

    // ── No header: the sole-active-tenant fallback ──

    [Fact]
    public async Task Single_mode_sole_tenant_is_used_for_every_signed_in_user()
    {
        // Every Edition 1 install: one tenant, no assignments, Viewers included.
        var f = new Fixture(EditionMode.Single).Tenant(A);
        Assert.Equal(Selected(A), await f.SendAsync(User("viewer@org.test", AppRoles.Viewer)));
        Assert.Equal(Selected(A), await f.SendAsync(User("admin@org.test", AppRoles.Admin)));
    }

    [Theory]
    [InlineData(AppRoles.Viewer)]
    [InlineData(AppRoles.Analyst)]
    public async Task Msp_sole_active_client_is_not_given_to_an_unassigned_user(string role)
    {
        // Default deactivated after onboarding the first client; a never-provisioned
        // Viewer or an Analyst with no assignments must not read (or act on) it.
        var f = new Fixture().Tenant(A).Tenant(B, active: false).Assign("v@msp.test", B);
        Assert.Equal(Refused(StatusCodes.Status400BadRequest), await f.SendAsync(User("new@msp.test", role)));
        Assert.Equal(Refused(StatusCodes.Status400BadRequest), await f.SendAsync(User("v@msp.test", role)));
    }

    [Fact]
    public async Task Msp_sole_active_client_is_used_for_an_admin_and_for_a_user_assigned_to_it()
    {
        var f = new Fixture().Tenant(A).Assign("v@msp.test", A);
        Assert.Equal(Selected(A), await f.SendAsync(User("admin@msp.test", AppRoles.Admin)));
        Assert.Equal(Selected(A), await f.SendAsync(User("v@msp.test", AppRoles.Viewer)));
    }

    [Fact]
    public async Task Msp_user_permitted_one_of_several_clients_gets_that_one()
    {
        var f = new Fixture().Tenant(A).Tenant(B).Assign("v@msp.test", B);
        Assert.Equal(Selected(B), await f.SendAsync(User("v@msp.test", AppRoles.Viewer)));
    }

    [Fact]
    public async Task Several_clients_and_no_header_leave_the_choice_to_the_caller()
    {
        var f = new Fixture().Tenant(A).Tenant(B).Assign("v@msp.test", A).Assign("v@msp.test", B);
        Assert.Equal(Refused(StatusCodes.Status400BadRequest), await f.SendAsync(User("admin@msp.test", AppRoles.Admin)));
        Assert.Equal(Refused(StatusCodes.Status400BadRequest), await f.SendAsync(User("v@msp.test", AppRoles.Viewer)));
        Assert.Equal(Refused(StatusCodes.Status400BadRequest), await f.SendAsync(User("new@msp.test", AppRoles.Viewer)));
    }

    // ── X-Vigil-Tenant: the tenant switcher ──

    [Fact]
    public async Task Header_naming_a_permitted_client_selects_it()
    {
        var f = new Fixture().Tenant(A).Tenant(B).Assign("v@msp.test", A).Assign("v@msp.test", B);
        Assert.Equal(Selected(B), await f.SendAsync(User("v@msp.test", AppRoles.Viewer), B.ToString()));
        Assert.Equal(Selected(A), await f.SendAsync(User("admin@msp.test", AppRoles.Admin), A.ToString()));
    }

    [Fact]
    public async Task Header_naming_an_unassigned_inactive_or_unknown_client_is_forbidden()
    {
        var f = new Fixture().Tenant(A).Tenant(B).Tenant(C, active: false).Assign("v@msp.test", A).Assign("v@msp.test", C);
        var viewer = User("v@msp.test", AppRoles.Viewer);
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(viewer, B.ToString()));            // not theirs
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(viewer, C.ToString()));            // theirs, but deactivated
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(viewer, Guid.NewGuid().ToString())); // does not exist
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(User("admin@msp.test", AppRoles.Admin), C.ToString()));
    }

    [Fact]
    public async Task Header_that_is_not_a_tenant_id_is_a_bad_request_and_needs_sign_in()
    {
        var f = new Fixture().Tenant(A).Tenant(B);
        Assert.Equal(Refused(StatusCodes.Status400BadRequest), await f.SendAsync(User("admin@msp.test", AppRoles.Admin), "contoso"));
        Assert.Equal(Refused(StatusCodes.Status401Unauthorized), await f.SendAsync(Anonymous, A.ToString()));
    }

    // ── SIEM API tokens (no signed-in user) ──

    [Fact]
    public async Task Client_restricted_token_is_pinned_to_its_client()
    {
        var f = new Fixture().Tenant(A).Tenant(B);
        var raw = f.Token(restrictTo: B);
        Assert.Equal(Selected(B), await f.SendAsync(Anonymous, path: "/api/siem/alerts", apiKey: raw));
        Assert.Equal(Selected(B), await f.SendAsync(Anonymous, B.ToString(), "/api/siem/alerts", raw));
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(Anonymous, A.ToString(), "/api/siem/alerts", raw));
    }

    [Fact]
    public async Task Client_restricted_token_stops_working_when_its_client_is_deactivated()
    {
        var f = new Fixture().Tenant(A).Tenant(B, active: false);
        var raw = f.Token(restrictTo: B);
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(Anonymous, path: "/api/siem/alerts", apiKey: raw));
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(Anonymous, B.ToString(), "/api/siem/alerts", raw));
    }

    [Fact]
    public async Task Install_wide_token_may_name_any_active_client_or_use_the_sole_one()
    {
        var f = new Fixture().Tenant(A).Tenant(B).Tenant(C, active: false);
        var raw = f.Token(restrictTo: null);
        Assert.Equal(Selected(B), await f.SendAsync(Anonymous, B.ToString(), "/api/siem/alerts", raw));
        Assert.Equal(Refused(StatusCodes.Status403Forbidden), await f.SendAsync(Anonymous, C.ToString(), "/api/siem/alerts", raw));

        var sole = new Fixture().Tenant(A);
        Assert.Equal(Selected(A), await sole.SendAsync(Anonymous, path: "/api/siem/alerts", apiKey: sole.Token(restrictTo: null)));
    }
}
