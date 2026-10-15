using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
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
///
/// Each entry records the client it concerned. The switcher sends the selected
/// client on every call, so an endpoint that acts on the install (users, tokens,
/// install-wide settings) uses <see cref="WriteMspAsync"/>, and one that acts on
/// a named client (onboarding, purge) uses <see cref="WriteForTenantAsync"/>.
/// </summary>
public sealed class AuditLogger(
    AppDbContext db,
    ITenantContext tenant,
    IHttpContextAccessor httpContext,
    ILogger<AuditLogger> logger,
    IServiceScopeFactory? scopes = null)
{
    /// <summary>The canonical form new entries are hashed with (see <see cref="ComputeHash"/>).</summary>
    public const int CurrentHashVersion = 1;

    // Serialises hash-chain writes within this process so two concurrent actions
    // don't both link to the same predecessor (which would fork the chain).
    private static readonly SemaphoreSlim ChainLock = new(1, 1);

    /// <summary>
    /// Record an action on the selected client's data (alerts, suppressions,
    /// routing...), against that client. Saves immediately so the entry survives
    /// even if the caller does not call SaveChanges. Swallows persistence errors
    /// (logging them) so an audit failure never breaks the user's action.
    /// </summary>
    public Task WriteAsync(string action, string targetType, string? targetId, string? details, CancellationToken ct)
        => WriteForTenantAsync(tenant.Current, action, targetType, targetId, details, ct);

    /// <summary>Record an MSP-level action (users, API tokens, install-wide settings) against no client, whichever one is selected.</summary>
    public Task WriteMspAsync(string action, string targetType, string? targetId, string? details, CancellationToken ct)
        => WriteForTenantAsync(null, action, targetType, targetId, details, ct);

    /// <summary>
    /// Record an action against the client it concerned (null = MSP-level),
    /// whichever client is selected: onboarding or purging client Y while X is
    /// selected is Y's history, not X's.
    /// </summary>
    public async Task WriteForTenantAsync(Guid? tenantId, string action, string targetType, string? targetId, string? details, CancellationToken ct)
    {
        try
        {
            var ctx = httpContext.HttpContext;
            var actor = ctx?.User is ClaimsPrincipal p ? AuthHelpers.GetEmail(p) : "";

            var entry = new AuditEntry
            {
                TenantId = tenantId,
                HashVersion = CurrentHashVersion,
                // Whole microseconds: PostgreSQL timestamptz stores microseconds, so a
                // 100-ns tick here would be rounded on save and the recomputed hash would
                // no longer match — every entry would read as tampered on Postgres.
                Timestamp = TruncateToMicroseconds(DateTimeOffset.UtcNow),
                ActorEmail = string.IsNullOrEmpty(actor) ? "system" : actor,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Details = details,
                IpAddress = GetClientIp(ctx),
                UserAgent = Truncate(ctx?.Request.Headers.UserAgent.ToString(), 300),
            };

            if (tenantId is Guid t && t != tenant.Current)
            {
                // The write guard admits a client's row only in that client's
                // context, so record it through a scope set to that client.
                if (scopes is null)
                    throw new InvalidOperationException("Recording an entry for another client needs a service scope.");
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().Set(t);
                await AppendAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), entry, ct);
            }
            else
            {
                await AppendAsync(db, entry, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit entry {Action} on {TargetType} {TargetId}", action, targetType, targetId);
        }
    }

    private static async Task AppendAsync(AppDbContext target, AuditEntry entry, CancellationToken ct)
    {
        await ChainLock.WaitAsync(ct);
        try
        {
            // The chain is one global sequence across every tenant, so the
            // predecessor is the newest entry of any tenant — deliberately
            // cross-tenant. A per-tenant chain would break the moment a
            // suspended tenant's rows were pruned.
            var prevHash = await target.CrossTenant<AuditEntry>().AsNoTracking()
                .OrderByDescending(a => a.Id)
                .Select(a => a.EntryHash)
                .FirstOrDefaultAsync(ct);
            entry.PrevHash = prevHash;
            entry.EntryHash = ComputeHash(entry);

            target.AuditEntries.Add(entry);
            await target.SaveChangesAsync(ct);
        }
        finally
        {
            ChainLock.Release();
        }
    }

    /// <summary>Drops sub-microsecond ticks so the stored value round-trips exactly on every engine.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset t)
        => new(t.Ticks - t.Ticks % 10, t.Offset);

    /// <summary>
    /// Canonical hash of an entry's content + its predecessor's hash, in the form
    /// its <see cref="AuditEntry.HashVersion"/> names. Each form must stay stable
    /// across releases — changing one invalidates the chains written with it.
    /// </summary>
    public static string ComputeHash(AuditEntry e)
    {
        var fields = new List<string>
        {
            e.PrevHash ?? "",
            e.Timestamp.UtcDateTime.ToString("O"),
            e.ActorEmail,
            e.Action,
            e.TargetType,
            e.TargetId ?? "",
            e.Details ?? "",
            e.IpAddress ?? "",
            e.UserAgent ?? "",
        };
        // Version 1 also binds the entry to its client, so moving an entry to
        // another client (or to MSP-level) breaks the chain. Version 0 entries
        // keep the original form, so chains written before it still verify.
        if (e.HashVersion != 0)
        {
            fields.Add(e.HashVersion.ToString(CultureInfo.InvariantCulture));
            fields.Add(e.TenantId?.ToString("D") ?? "");
        }
        // Unit-separator delimiter so shifted field boundaries always change the hash.
        var canonical = string.Join('\u001f', fields);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public sealed record ChainVerification(bool Valid, int Total, int Verified, int LegacyUnhashed, long? FirstBrokenId);

    /// <summary>
    /// Recomputes every entry's hash and checks the PrevHash linkage, given the
    /// whole chain (every tenant) in Id order. Entries written before the chain
    /// existed (EntryHash null) are counted as legacy and skipped. The chain
    /// starts at the first hashed entry, so a pruned prefix leaves it valid.
    /// </summary>
    public static ChainVerification VerifyChain(IReadOnlyList<AuditEntry> entries)
    {
        var legacy = 0; var verified = 0;
        long? firstBrokenId = null;
        string? expectedPrev = null; var chainStarted = false;

        foreach (var e in entries)
        {
            if (e.EntryHash is null) // pre-hash-chain row
            {
                legacy++;
                if (chainStarted && firstBrokenId is null) firstBrokenId = e.Id; // gap inside the chain
                continue;
            }

            if (chainStarted && e.PrevHash != expectedPrev && firstBrokenId is null)
                firstBrokenId = e.Id;
            if (ComputeHash(e) != e.EntryHash && firstBrokenId is null)
                firstBrokenId = e.Id;

            expectedPrev = e.EntryHash;
            chainStarted = true;
            verified++;
        }

        return new ChainVerification(firstBrokenId is null, entries.Count, verified, legacy, firstBrokenId);
    }

    /// <summary>
    /// The connection's remote address. Behind a reverse proxy it trusts, the
    /// forwarded-headers middleware (Program.cs) has already replaced it with the
    /// client's; anyone else's X-Forwarded-For is ignored, so a caller cannot
    /// choose the address their actions are recorded with.
    /// </summary>
    private static string? GetClientIp(HttpContext? ctx) => Truncate(ctx?.Connection.RemoteIpAddress?.ToString(), 45);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
