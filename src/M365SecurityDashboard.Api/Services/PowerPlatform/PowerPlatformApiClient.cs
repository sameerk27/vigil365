using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services.PowerPlatform;

/// <summary>
/// Reads the Power Platform API (api.powerplatform.com) as the current tenant, with the
/// same app and credentials as Microsoft Graph. GET only: there is no other method in
/// this file, and a test fails if one appears.
///
/// Microsoft grants an app access here through a Power Platform role, not a Graph
/// permission: a Power Platform administrator gives the app the read-only "Power
/// Platform reader" role (preview). Until then every call is refused and
/// <see cref="PowerPlatformApiException.NotEnabled"/> says so.
/// </summary>
public sealed class PowerPlatformApiClient
{
    public const string ApiVersion = "2024-10-01";

    private readonly HttpClient _http;
    private readonly TenantGraphCredentials _credentials;
    private GraphOptions? _options;
    private TokenCredential? _credential;

    public PowerPlatformApiClient(HttpClient http, TenantGraphCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    /// <summary>Tests: fixed options and token, no credential resolution. Not visible to DI.</summary>
    internal PowerPlatformApiClient(HttpClient http, GraphOptions options, TokenCredential credential)
        : this(http, credentials: null!)
    {
        _options = options;
        _credential = credential;
    }

    /// <summary>A paged read. Complete is false when a later page failed or the page cap was reached.</summary>
    public sealed record Collection(IReadOnlyList<JsonElement> Items, bool Complete);

    private async Task<(Uri BaseUri, TokenCredential Credential)> EnsureAsync(CancellationToken ct)
    {
        if (_options is null || _credential is null)
        {
            var o = await _credentials.ResolveAsync(ct);
            if (!o.IsConfigured())
                throw new InvalidOperationException("Graph credentials are not configured for this tenant.");
            _credential = GraphApiClient.BuildCredential(o);
            _options = o;
        }
        // The base URL is an install-wide setting, never a per-client one.
        var baseUrl = _credentials is null ? _options.PowerPlatformApiBaseUrl : _credentials.Global.PowerPlatformApiBaseUrl;
        return (new Uri(baseUrl.TrimEnd('/') + "/"), _credential);
    }

    /// <summary>
    /// Every page of a collection, following the next link while it stays on the Power
    /// Platform API host (the bearer token is never sent anywhere else). 204 No Content
    /// is an empty collection. A failure on the first page throws.
    /// </summary>
    public async Task<Collection> ReadCollectionAsync(string path, int maxPages, CancellationToken ct)
    {
        var (baseUri, credential) = await EnsureAsync(ct);
        var items = new List<JsonElement>();
        string? next = new Uri(baseUri, path.TrimStart('/')).ToString();
        var pages = 0;

        while (next is not null)
        {
            if (pages == maxPages) return new Collection(items, false);
            JsonDocument? document;
            try
            {
                document = await GetAsync(next, baseUri, credential, ct);
            }
            catch (Exception ex) when (pages > 0 && ex is not OperationCanceledException)
            {
                return new Collection(items, false); // keep the pages already read
            }
            pages++;
            if (document is null) break; // 204: nothing here

            using (document)
            {
                var root = document.RootElement;
                var rows = root.ValueKind == JsonValueKind.Array ? root
                    : root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v
                    : default;
                if (rows.ValueKind == JsonValueKind.Array)
                    foreach (var item in rows.EnumerateArray()) items.Add(item.Clone());

                next = NextLink(root, baseUri);
            }
        }
        return new Collection(items, true);
    }

    private async Task<JsonDocument?> GetAsync(string url, Uri baseUri, TokenCredential credential, CancellationToken ct)
    {
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([new Uri(baseUri, ".default").ToString()]), ct);
        }
        catch (AuthenticationFailedException ex) when (ex.Message.Contains("AADSTS500011", StringComparison.Ordinal))
        {
            // The Power Platform API has no service principal in this tenant yet.
            throw new PowerPlatformApiException(null, ex.Message, PowerPlatformApiException.Reason.ApiNotInTenant);
        }

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "M365SecurityDashboard/1.0");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var reason = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? PowerPlatformApiException.Reason.Refused
                    : PowerPlatformApiException.Reason.Failed;
                throw new PowerPlatformApiException((int)response.StatusCode, Summarise(body), reason);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    /// <summary>The next page, if the response names one on the same host. Microsoft spells it three ways across these APIs.</summary>
    internal static string? NextLink(JsonElement root, Uri baseUri)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "@odata.nextLink", "@odata.nextlink", "nextLink" })
        {
            if (root.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.String
                && Uri.TryCreate(n.GetString(), UriKind.Absolute, out var uri))
            {
                return uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                    ? uri.ToString()
                    : null;
            }
        }
        return null;
    }

    /// <summary>Microsoft's own error message when the body has one, else a short excerpt.</summary>
    private static string Summarise(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
                if (e.ValueKind == JsonValueKind.String) return e.GetString()!;
            }
            if (doc.RootElement.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String) return top.GetString()!;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}

public sealed class PowerPlatformApiException(int? status, string message, PowerPlatformApiException.Reason why)
    : Exception(message)
{
    public enum Reason
    {
        /// <summary>401 or 403: the app has no Power Platform role in this tenant.</summary>
        Refused,
        /// <summary>AADSTS500011: the Power Platform API isn't registered in this tenant.</summary>
        ApiNotInTenant,
        /// <summary>Anything else.</summary>
        Failed,
    }

    public int? Status { get; } = status;
    public Reason Why { get; } = why;

    /// <summary>True when the tenant hasn't opted in yet, as opposed to a real failure.</summary>
    public bool NotEnabled => Why is Reason.Refused or Reason.ApiNotInTenant;
}
