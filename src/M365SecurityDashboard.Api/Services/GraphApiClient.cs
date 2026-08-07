using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using M365SecurityDashboard.Api.Models;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

public sealed class GraphApiClient
{
    private readonly HttpClient _http;
    private readonly GraphOptions _options;
    private readonly TokenCredential _credential;

    public GraphApiClient(HttpClient http, IOptions<GraphOptions> options)
    {
        _http = http;
        _options = options.Value;
        _credential = BuildCredential(_options);
    }

    /// <summary>
    /// Certificate auth is preferred when configured (no long-lived secret to
    /// store or rotate); the client secret remains the fallback so existing
    /// installs keep working during migration.
    /// </summary>
    public static TokenCredential BuildCredential(GraphOptions o)
    {
        var authOptions = new ClientSecretCredentialOptions();
        var certOptions = new ClientCertificateCredentialOptions();

        if (!string.IsNullOrWhiteSpace(o.LoginInstance))
        {
            try 
            {
                var uri = new Uri(o.LoginInstance);
                authOptions.AuthorityHost = uri;
                certOptions.AuthorityHost = uri;
            }
            catch { /* fallback to default if malformed */ }
        }

        if (o.HasCertificate())
            return new ClientCertificateCredential(o.TenantId, o.ClientId, LoadCertificate(o), certOptions);
        return new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret, authOptions);
    }

    public static X509Certificate2 LoadCertificate(GraphOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.CertificateThumbprint))
        {
            var thumb = o.CertificateThumbprint.Replace(" ", "").ToUpperInvariant();
            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                // A store that cannot be opened is skipped, not fatal. On Linux
                // (the Docker deployment) LocalMachine\My does not exist and
                // Open() throws CryptographicException — which otherwise escapes
                // as a cryptic error instead of the clear "not found" below, and
                // masks a certificate that IS present in the other store.
                try
                {
                    using var store = new X509Store(StoreName.My, location);
                    store.Open(OpenFlags.ReadOnly);
                    var match = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false);
                    if (match.Count > 0) return match[0];
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                             or PlatformNotSupportedException
                                             or UnauthorizedAccessException)
                {
                    // store unavailable on this platform/host — try the next one
                }
            }
            throw new InvalidOperationException(
                $"Certificate with thumbprint '{thumb}' was not found in CurrentUser\\My or LocalMachine\\My.");
        }

        if (!File.Exists(o.CertificatePath))
            throw new InvalidOperationException($"Certificate file not found: '{o.CertificatePath}'.");
        return string.IsNullOrEmpty(o.CertificatePassword)
            ? new X509Certificate2(o.CertificatePath)
            : new X509Certificate2(o.CertificatePath, o.CertificatePassword);
    }

    public async Task<IReadOnlyList<JsonElement>> GetCollectionAsync(string path, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var next = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{_options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

        var isFirstPage = true;
        var throttleRetries = 0;
        const int maxThrottleRetries = 3; // a persistently throttling tenant must fail, not hang forever
        while (!string.IsNullOrWhiteSpace(next))
        {
            string? nextForIteration = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.TryAddWithoutValidation("User-Agent", "M365SecurityDashboard/1.0");
                var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { $"{_options.BaseUrl.TrimEnd('/')}/.default" }), ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (++throttleRetries > maxThrottleRetries)
                    {
                        if (!isFirstPage) break; // keep the pages we already have
                        throw new HttpRequestException(
                            $"Graph throttled the request {maxThrottleRetries} times in a row (429). Try again later.",
                            null, response.StatusCode);
                    }
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
                    throttleRetries = 0; // budget is per page, not per collection
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
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { $"{_options.BaseUrl.TrimEnd('/')}/.default" }), ct);
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
