using System.Net;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The client address the audit log records (and the rate limiter keys on) comes
/// from X-Forwarded-For only when the connection is from a trusted proxy.
/// </summary>
public sealed class ForwardedHeadersSetupTests
{
    private static async Task<IPAddress?> ClientAddressAsync(string remote, Dictionary<string, string?>? config = null)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersSetup.Configure(options, new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
        IPAddress? seen = null;
        var middleware = new ForwardedHeadersMiddleware(ctx => { seen = ctx.Connection.RemoteIpAddress; return Task.CompletedTask; },
            NullLoggerFactory.Instance, Options.Create(options));

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
        await middleware.Invoke(http);
        return seen;
    }

    [Fact]
    public async Task A_caller_on_the_internet_cannot_choose_its_recorded_address()
        => Assert.Equal(IPAddress.Parse("203.0.113.9"), await ClientAddressAsync("203.0.113.9"));

    [Fact]
    public async Task A_proxy_on_the_same_host_is_trusted_by_default()
        => Assert.Equal(IPAddress.Parse("198.51.100.7"), await ClientAddressAsync("127.0.0.1"));

    [Fact]
    public async Task A_proxy_elsewhere_is_trusted_only_once_configured()
    {
        Assert.Equal(IPAddress.Parse("172.18.0.5"), await ClientAddressAsync("172.18.0.5"));
        Assert.Equal(IPAddress.Parse("198.51.100.7"), await ClientAddressAsync("172.18.0.5",
            new() { ["ForwardedHeaders:KnownNetworks:0"] = "172.18.0.0/16" }));
        Assert.Equal(IPAddress.Parse("198.51.100.7"), await ClientAddressAsync("10.1.2.3",
            new() { ["ForwardedHeaders:KnownProxies:0"] = "10.1.2.3" }));
    }

    [Fact]
    public void A_misspelt_proxy_setting_fails_loudly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks:0"] = "172.18.0.0" }).Build();
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.Configure(new ForwardedHeadersOptions(), config));
    }
}
