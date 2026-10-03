using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Runs an endpoint module's real handlers in-process, without a server: the
/// module is mapped on a WebApplication backed by an in-memory database, and a
/// request goes straight to one endpoint's RequestDelegate. Authentication,
/// authorization policies and the tenant middleware are not in the path — the
/// test supplies the user and the tenant, and checks the handler's own decisions.
/// </summary>
internal sealed class EndpointHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly DbContextOptions<AppDbContext> _db = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    public IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    /// <param name="services">Extra registrations. A module's other endpoints only need their services registered (never resolved) so their parameters are not read as request bodies.</param>
    public EndpointHarness(Action<WebApplication> map, EditionMode mode = EditionMode.Msp, Action<GraphOptions>? graph = null,
        Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var s = builder.Services;
        s.AddScoped<TenantContext>();
        s.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        s.AddScoped(sp => new AppDbContext(_db, sp.GetRequiredService<ITenantContext>()));
        s.AddSingleton(DataProtection);
        s.AddSingleton<SecretProtector>();
        s.AddMemoryCache();
        s.AddHttpContextAccessor();
        s.AddScoped<AuditLogger>();
        s.AddScoped<TenantAccess>();
        s.AddScoped<TenantGraphCredentials>();
        s.AddScoped<TenantRollupService>();
        s.Configure<EditionOptions>(o => o.Mode = mode);
        s.Configure(graph ?? (_ => { }));
        services?.Invoke(s);
        _app = builder.Build();
        map(_app);
    }

    public T Get<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    /// <summary>A context on the same database, for seeding and asserting.</summary>
    public AppDbContext Db(Guid? tenant = null) => new(_db, tenant is Guid t ? TestTenancy.For(t) : TestTenancy.None());

    /// <summary>A scope with the given tenant selected, for running services (e.g. AuditLogger) as a request would.</summary>
    public AsyncServiceScope Scope(Guid? tenant = null)
    {
        var scope = _app.Services.CreateAsyncScope();
        if (tenant is Guid t) scope.ServiceProvider.GetRequiredService<TenantContext>().Set(t);
        return scope;
    }

    /// <param name="pattern">The route as mapped, e.g. "/api/tenants/{id:guid}/test".</param>
    /// <param name="body">Sent as the JSON request body.</param>
    public async Task<(int Status, string Body)> SendAsync(string method, string pattern, ClaimsPrincipal? user = null,
        Guid? tenant = null, string? query = null, object? routeValues = null, object? body = null)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        await using var scope = _app.Services.CreateAsyncScope();
        if (tenant is Guid t) scope.ServiceProvider.GetRequiredService<TenantContext>().Set(t);
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        ctx.Request.Method = method;
        if (query is not null) ctx.Request.QueryString = new QueryString(query);
        if (routeValues is not null) // routing hands handlers strings
            foreach (var (key, value) in new RouteValueDictionary(routeValues))
                ctx.Request.RouteValues[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        if (body is not null)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, body.GetType());
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = json.Length;
            ctx.Request.Body = new MemoryStream(json);
            ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody()); // a server sets this; minimal APIs read no body without it
        }
        ctx.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
