using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Writes append-only audit entries for security-relevant actions. Resolves the
/// acting user from the current request's validated token, captures the client
/// IP + User-Agent, and chains each entry to the previous one with a SHA-256
/// hash so tampering (edit or delete of any historical row) is detectable via
/// the /api/admin/audit-log/verify endpoint. Failures to write an audit row
/// must never block the underlying action, but are logged.
/// </summary>
public sealed class AuditLogger(
    AppDbContext db,
    IHttpContextAccessor httpContext,
    ILogger<AuditLogger> logger)
{
    // Serialises hash-chain writes within this process so two concurrent actions
    // don't both link to the same predecessor (which would fork the chain).
    private static readonly SemaphoreSlim ChainLock = new(1, 1);

    /// <summary>
    /// Record an action. Saves immediately so the entry survives even if the caller
    /// does not call SaveChanges. Swallows persistence errors (logging them) so an
    /// audit failure never breaks the user's action.
    /// </summary>
    public async Task WriteAsync(string action, string targetType, string? targetId, string? details, CancellationToken ct)
    {
        try
        {
            var ctx = httpContext.HttpContext;
            var actor = ctx?.User is ClaimsPrincipal p ? AuthHelpers.GetEmail(p) : "";

            var entry = new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                ActorEmail = string.IsNullOrEmpty(actor) ? "system" : actor,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Details = details,
                IpAddress = GetClientIp(ctx),
                UserAgent = Truncate(ctx?.Request.Headers.UserAgent.ToString(), 300),
            };

            await ChainLock.WaitAsync(ct);
            try
            {
                var prevHash = await db.AuditEntries.AsNoTracking()
                    .OrderByDescending(a => a.Id)
                    .Select(a => a.EntryHash)
                    .FirstOrDefaultAsync(ct);
                entry.PrevHash = prevHash;
                entry.EntryHash = ComputeHash(entry);

                db.AuditEntries.Add(entry);
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                ChainLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit entry {Action} on {TargetType} {TargetId}", action, targetType, targetId);
        }
    }

    /// <summary>
    /// Canonical hash of an entry's content + its predecessor's hash. Must stay
    /// stable across releases — changing the format invalidates existing chains.
    /// </summary>
    public static string ComputeHash(AuditEntry e)
    {
        // Unit-separator delimiter so shifted field boundaries always change the hash.
        var canonical = string.Join('\u001f',
            e.PrevHash ?? "",
            e.Timestamp.UtcDateTime.ToString("O"),
            e.ActorEmail,
            e.Action,
            e.TargetType,
            e.TargetId ?? "",
            e.Details ?? "",
            e.IpAddress ?? "",
            e.UserAgent ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>First X-Forwarded-For hop when behind a proxy, else the socket address.</summary>
    private static string? GetClientIp(HttpContext? ctx)
    {
        if (ctx is null) return null;
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length > 0) return Truncate(first, 45);
        }
        return ctx.Connection.RemoteIpAddress?.ToString();
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
