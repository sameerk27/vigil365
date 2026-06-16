using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using M365SecurityDashboard.Api.Models;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

public sealed class GraphApiClient
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    private readonly HttpClient _http;
    private readonly GraphOptions _options;
    private readonly ClientSecretCredential _credential;

    public GraphApiClient(HttpClient http, IOptions<GraphOptions> options)
    {
        _http = http;
        _options = options.Value;
        _credential = new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
    }

    public async Task<IReadOnlyList<JsonElement>> GetCollectionAsync(string path, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var next = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{_options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

        var isFirstPage = true;
        while (!string.IsNullOrWhiteSpace(next))
        {
            string? nextForIteration = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.TryAddWithoutValidation("User-Agent", "M365SecurityDashboard/1.0");
                var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(15);
                    await Task.Delay(retryAfter, ct);
                    nextForIteration = next; // retry same URL
                }
                else if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (!isFirstPage) break;
                    throw new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode}: {body}", null, response.StatusCode);
                }
                else
                {
                    isFirstPage = false;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in value.EnumerateArray())
                            items.Add(item.Clone());
                    }
                    else
                    {
                        items.Add(document.RootElement.Clone());
                    }

                    nextForIteration = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                        ? nextLink.GetString()
                        : null;
                }
            }
            catch when (!isFirstPage) { break; } // pagination failure — return what we have
            next = nextForIteration;
        }

        return items;
    }

    public async Task<IReadOnlyList<JsonElement>> GetSinglePageAsync(string path, CancellationToken ct)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{_options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "M365SecurityDashboard/1.0");
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode}: {body}", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var items = new List<JsonElement>();
        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                items.Add(item.Clone());
        }
        else
        {
            items.Add(document.RootElement.Clone());
        }
        return items;
    }
}
