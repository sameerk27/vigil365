using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Which reverse proxies may tell Vigil365 the client's address (X-Forwarded-For).
/// That address is recorded in the audit log and keys the rate limiter, so it is
/// taken from the header only when the connection itself comes from a trusted
/// proxy: loopback by default (a proxy on the same host, as the README's reverse
/// proxy example and enterprise-install.sh set up), plus any listed in
/// ForwardedHeaders:KnownProxies (IP addresses) or ForwardedHeaders:KnownNetworks
/// (CIDR, e.g. a Docker network's 172.18.0.0/16). Anyone else's header is ignored.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    public static void Configure(ForwardedHeadersOptions options, IConfiguration config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

        foreach (var value in config.GetSection($"{SectionName}:KnownProxies").Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(value.Trim(), out var proxy))
                throw new InvalidOperationException($"{SectionName}:KnownProxies: '{value}' is not an IP address.");
            options.KnownProxies.Add(proxy);
        }

        foreach (var value in config.GetSection($"{SectionName}:KnownNetworks").Get<string[]>() ?? [])
        {
            var parts = value.Trim().Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var length)
                || length < 0 || length > (prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
                throw new InvalidOperationException($"{SectionName}:KnownNetworks: '{value}' is not a network in CIDR form (e.g. 172.18.0.0/16).");
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
        }
    }
}
