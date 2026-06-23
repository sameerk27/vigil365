using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Writes append-only audit entries for security-relevant actions. Resolves the
/// acting user from the current request's validated token. Failures to write an
/// audit row must never block the underlying action, but are logged.
/// </summary>
public sealed class AuditLogger(
    AppDbContext db,
    IHttpContextAccessor httpContext,
    ILogger<AuditLogger> logger)
{
    /// <summary>
    /// Record an action. Saves immediately so the entry survives even if the caller
    /// does not call SaveChanges. Swallows persistence errors (logging them) so an
    /// audit failure never breaks the user's action.
    /// </summary>
    public async Task WriteAsync(string action, string targetType, string? targetId, string? details, CancellationToken ct)
    {
        try
        {
            var actor = httpContext.HttpContext?.User is ClaimsPrincipal p ? AuthHelpers.GetEmail(p) : "";
            db.AuditEntries.Add(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                ActorEmail = string.IsNullOrEmpty(actor) ? "system" : actor,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Details = details,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit entry {Action} on {TargetType} {TargetId}", action, targetType, targetId);
        }
    }
}
