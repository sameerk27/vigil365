using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// The shared shape of a Graph-backed dashboard section. "Not configured" and "failed" both return the
/// section's empty shape plus configured/error, so one failing section degrades on its own and never
/// 500s the page. The section body returns its payload without configured/error; they are merged in here.
/// </summary>
internal static class GraphSection
{
    public const string GenericError = "An error occurred. Check server logs for details.";

    /// <param name="empty">The section's payload when there is nothing to show.</param>
    /// <param name="requiredPermission">Named in the error when Graph refuses access; null when the
    /// section's calls need more than one permission and naming one would mislead.</param>
    public static Task<IResult> RunAsync(
        ILogger logger, TenantGraphCredentials creds, IServiceProvider services,
        object empty, string? requiredPermission, Func<GraphApiClient, Task<object>> body, CancellationToken ct) =>
        RunCoreAsync(logger, creds.IsConfiguredAsync, services.GetRequiredService<GraphApiClient>,
            services.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web),
            empty, requiredPermission, body, ct);

    internal static async Task<IResult> RunCoreAsync<TClient>(
        ILogger logger, Func<CancellationToken, Task<bool>> isConfigured, Func<TClient> client, JsonSerializerOptions json,
        object empty, string? requiredPermission, Func<TClient, Task<object>> body, CancellationToken ct)
    {
        if (!await isConfigured(ct)) return Shape(empty, json, configured: false, error: null);
        try
        {
            return Shape(await body(client()), json, configured: true, error: null);
        }
        // A cancelled request has nobody to answer. An HttpClient timeout is also an
        // OperationCanceledException, but with ct not cancelled, so it still gets the error payload.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Dashboard endpoint error");
            return Shape(empty, json, configured: true,
                error: GraphErrorHint.DescribeOrNull(ex.Message, requiredPermission) ?? GenericError);
        }
    }

    // Serialised with the app's own options so enums and naming match every other response.
    private static IResult Shape(object payload, JsonSerializerOptions json, bool configured, string? error)
    {
        var o = JsonSerializer.SerializeToNode(payload, payload.GetType(), json) as JsonObject ?? new JsonObject();
        o["configured"] = configured;
        if (error is not null) o["error"] = error;
        return Results.Ok(o);
    }
}
