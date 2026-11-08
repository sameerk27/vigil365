using System.Text.Json;
using System.Text.Json.Nodes;
using M365SecurityDashboard.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Every Graph-backed dashboard section answers through GraphSection. The SPA reads one flat object
/// with configured/error beside the section's own fields, so the shape is locked here.
/// </summary>
public class GraphSectionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly object Empty = new { total = 0, alerts = Array.Empty<object>() };

    private static async Task<JsonObject> Run(bool configured, Func<string, Task<object>> body, string? perm = null,
        CancellationToken ct = default)
    {
        var result = await GraphSection.RunCoreAsync(NullLogger.Instance, _ => Task.FromResult(configured), () => "client",
            Web, Empty, perm, body, ct);
        return Assert.IsType<JsonObject>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }

    [Fact]
    public async Task Not_configured_returns_the_empty_shape_without_calling_graph()
    {
        var called = false;
        var o = await Run(false, _ => { called = true; return Task.FromResult<object>(new { }); });
        Assert.False(called);
        Assert.False((bool)o["configured"]!);
        Assert.Equal(0, (int)o["total"]!);
        Assert.Empty(o["alerts"]!.AsArray());
        Assert.False(o.ContainsKey("error"));
    }

    [Fact]
    public async Task Success_merges_configured_into_the_payload()
    {
        var o = await Run(true, c => Task.FromResult<object>(new { total = 2, client = c, bySeverity = new Dictionary<string, int> { ["high"] = 2 } }));
        Assert.True((bool)o["configured"]!);
        Assert.Equal(2, (int)o["total"]!);
        Assert.Equal("client", (string)o["client"]!);
        Assert.Equal(2, (int)o["bySeverity"]!["high"]!);
        Assert.False(o.ContainsKey("error"));
    }

    [Fact]
    public async Task A_payload_can_carry_its_own_error()
    {
        var o = await Run(true, _ => Task.FromResult<object>(new { total = 0, error = "Licence missing." }));
        Assert.True((bool)o["configured"]!);
        Assert.Equal("Licence missing.", (string)o["error"]!);
    }

    [Fact]
    public async Task A_failure_returns_the_empty_shape_and_names_the_permission()
    {
        var o = await Run(true, _ => throw new HttpRequestException("403 Forbidden"), perm: "SecurityAlert.Read.All");
        Assert.True((bool)o["configured"]!);
        Assert.Equal(0, (int)o["total"]!);
        Assert.Contains("SecurityAlert.Read.All", (string)o["error"]!);
    }

    [Fact]
    public async Task An_unrecognised_failure_gives_the_generic_message_not_the_raw_exception()
    {
        var o = await Run(true, _ => throw new InvalidOperationException("internal detail"));
        Assert.Equal(GraphSection.GenericError, (string)o["error"]!);
    }

    [Fact]
    public async Task A_timeout_is_answered_but_a_cancelled_request_is_not()
    {
        var timedOut = await Run(true, _ => throw new TaskCanceledException("timeout"));
        Assert.Equal(GraphSection.GenericError, (string)timedOut["error"]!);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Run(true, _ => throw new OperationCanceledException(cts.Token), ct: cts.Token));
    }
}
