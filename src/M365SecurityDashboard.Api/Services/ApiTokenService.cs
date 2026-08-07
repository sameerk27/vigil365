using System.Security.Cryptography;
using System.Text;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

public sealed class ApiTokenService(AppDbContext db)
{
    private const string TokenPrefix = "vig_";
    private const int TokenBytes = 32;

    public static (ApiToken row, string rawToken) Create(string name, string scopes, string? createdBy, DateTimeOffset? expiresAt)
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        var secret = Base64Url(bytes);
        var raw = TokenPrefix + secret;
        var prefix = raw[..Math.Min(12, raw.Length)];
        return (new ApiToken
        {
            Name = string.IsNullOrWhiteSpace(name) ? "SIEM integration" : name.Trim(),
            Prefix = prefix,
            TokenHash = Hash(raw),
            Scopes = NormalizeScopes(scopes),
            CreatedBy = createdBy,
            ExpiresAt = expiresAt,
        }, raw);
    }

    public async Task<ApiToken?> ValidateAsync(string? rawToken, string requiredScope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || !rawToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;

        var hash = Hash(rawToken.Trim());
        var now = DateTimeOffset.UtcNow;
        var token = await db.ApiTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now), ct);
        if (token is null || !HasScope(token.Scopes, requiredScope))
            return null;

        token.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        return token;
    }

    public static bool HasScope(string? scopes, string requiredScope)
    {
        var set = (scopes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return set.Any(s => s.Equals(requiredScope, StringComparison.OrdinalIgnoreCase) || s.Equals("*", StringComparison.Ordinal));
    }

    public static string Hash(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }

    public static string NormalizeScopes(string? scopes)
    {
        var normalized = (scopes ?? "alerts:read,health:read")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? "alerts:read,health:read" : string.Join(",", normalized);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
