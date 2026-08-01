using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Admin-only endpoints: in-app user management and the tamper-evident audit log.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // ── User management (Admin only) ─────────────────────────────────────────────────
        // Roles are managed entirely in-app — no Entra App Roles, no Graph write permission.
        app.MapGet("/api/admin/users", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AppUsers.OrderBy(u => u.Email).ToListAsync(ct)))
            .RequireAuthorization("RequireAdmin");

        // Pre-provision (invite) a user by email + role before they ever sign in.
        // LastSeenAt = DateTimeOffset.MinValue marks "invited, not yet signed in".
        app.MapPost("/api/admin/users", async (
            AddUserRequest input, AppDbContext db, NotificationSender sender, AuditLogger audit, IConfiguration config, CancellationToken ct) =>
        {
            var email = (input.Email ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(email) || !email.Contains('@'))
                return Results.BadRequest(new { error = "A valid email address is required." });

            if (!AppRoles.IsValid(input.Role))
                return Results.BadRequest(new { error = "Invalid role. Must be Admin, Analyst, or Viewer." });

            if (await db.AppUsers.AnyAsync(u => u.Email == email, ct))
                return Results.Conflict(new { error = $"A user with email '{email}' already exists." });

            var user = new AppUser
            {
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim(),
                Role = input.Role,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.MinValue
            };
            db.AppUsers.Add(user);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("user.add", "user", email, $"added with role {user.Role}", ct);

            string? inviteError = null;
            if (input.SendInvite)
            {
                var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings { Id = 1 };
                var url = config["Auth:RedirectUri"] ?? "http://localhost:5000";
                var (ok, error) = await sender.SendInviteEmailAsync(cfg, email, user.Role, url, ct);
                if (!ok) inviteError = error;
            }
            return Results.Ok(new { user, inviteSent = input.SendInvite && inviteError is null, inviteError });
        }).RequireAuthorization("RequireAdmin");

        // (Re)send the access-notification email to a pre-provisioned/existing user.
        app.MapPost("/api/admin/users/{email}/invite", async (
            string email, AppDbContext db, NotificationSender sender, AuditLogger audit, IConfiguration config, CancellationToken ct) =>
        {
            email = email.Trim().ToLowerInvariant();
            var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null) return Results.NotFound();

            var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings { Id = 1 };
            var url = config["Auth:RedirectUri"] ?? "http://localhost:5000";
            var (ok, error) = await sender.SendInviteEmailAsync(cfg, email, user.Role, url, ct);
            if (ok) await audit.WriteAsync("user.invite", "user", email, "invite email sent", ct);
            return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
        }).RequireAuthorization("RequireAdmin");

        app.MapPut("/api/admin/users/{email}/role", async (
            string email, RoleChangeRequest input, AppDbContext db, AuditLogger audit,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
            System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
        {
            if (!AppRoles.IsValid(input.Role))
                return Results.BadRequest(new { error = "Invalid role. Must be Admin, Analyst, or Viewer." });

            email = email.Trim().ToLowerInvariant();
            var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null) return Results.NotFound();

            // Lockout guard: don't allow demoting the last remaining Admin.
            if (user.Role == AppRoles.Admin && input.Role != AppRoles.Admin)
            {
                var adminCount = await db.AppUsers.CountAsync(u => u.Role == AppRoles.Admin, ct);
                if (adminCount <= 1)
                    return Results.BadRequest(new { error = "Cannot demote the last Admin. Promote another user to Admin first." });
            }

            var oldRole = user.Role;
            user.Role = input.Role;
            await db.SaveChangesAsync(ct);
            cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
            await audit.WriteAsync("user.role_change", "user", email, $"role {oldRole} -> {input.Role}", ct);
            return Results.Ok(user);
        }).RequireAuthorization("RequireAdmin");

        app.MapDelete("/api/admin/users/{email}", async (
            string email, AppDbContext db, AuditLogger audit,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
            System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
        {
            email = email.Trim().ToLowerInvariant();
            var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null) return Results.NotFound();

            // Don't allow removing yourself or the last Admin.
            if (email == AuthHelpers.GetEmail(caller))
                return Results.BadRequest(new { error = "You cannot remove your own account." });
            if (user.Role == AppRoles.Admin && await db.AppUsers.CountAsync(u => u.Role == AppRoles.Admin, ct) <= 1)
                return Results.BadRequest(new { error = "Cannot remove the last Admin." });

            var removedRole = user.Role;
            db.AppUsers.Remove(user);
            await db.SaveChangesAsync(ct);
            cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
            await audit.WriteAsync("user.remove", "user", email, $"removed (was {removedRole})", ct);
            return Results.NoContent();
        }).RequireAuthorization("RequireAdmin");

        // Audit trail of security-relevant actions (Admin only).
        app.MapGet("/api/admin/audit-log", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AuditEntries.AsNoTracking().OrderByDescending(a => a.Timestamp).Take(200).ToListAsync(ct)))
            .RequireAuthorization("RequireAdmin");

        // Full audit trail as CSV (Admin only). The export itself is audited.
        app.MapGet("/api/admin/audit-log/export", async (AppDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var entries = await db.AuditEntries.AsNoTracking()
                .OrderBy(a => a.Id)
                .Take(100_000)
                .ToListAsync(ct);

            // Shared encoder: RFC-4180 quoting + formula-injection guard. Audit actor
            // names and details are tenant-controlled, so this export is a live vector.
            static string Csv(string? v) => CsvSanitizer.Field(v);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Id,TimestampUtc,ActorEmail,Action,TargetType,TargetId,Details,IpAddress,UserAgent,PrevHash,EntryHash");
            foreach (var e in entries)
                sb.AppendLine(string.Join(',',
                    e.Id,
                    e.Timestamp.UtcDateTime.ToString("O"),
                    Csv(e.ActorEmail), Csv(e.Action), Csv(e.TargetType), Csv(e.TargetId),
                    Csv(e.Details), Csv(e.IpAddress), Csv(e.UserAgent), Csv(e.PrevHash), Csv(e.EntryHash)));

            await audit.WriteAsync("audit.export", "audit_log", null, $"exported {entries.Count} entries as CSV", ct);
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
                "text/csv",
                $"vigil365-audit-log-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        }).RequireAuthorization("RequireAdmin");

        // Verify the tamper-evident hash chain (Admin only). Recomputes every entry's
        // hash and checks the PrevHash linkage in Id order. Entries written before the
        // hash chain existed (EntryHash NULL) are counted as "legacy" and skipped —
        // verification starts from the first hashed entry.
        app.MapGet("/api/admin/audit-log/verify", async (AppDbContext db, CancellationToken ct) =>
        {
            var entries = await db.AuditEntries.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);

            var legacy = 0; var checked_ = 0;
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
                if (AuditLogger.ComputeHash(e) != e.EntryHash && firstBrokenId is null)
                    firstBrokenId = e.Id;

                expectedPrev = e.EntryHash;
                chainStarted = true;
                checked_++;
            }

            return Results.Ok(new
            {
                valid = firstBrokenId is null,
                total = entries.Count,
                verified = checked_,
                legacyUnhashed = legacy,
                firstBrokenId,
                verifiedAt = DateTimeOffset.UtcNow
            });
        }).RequireAuthorization("RequireAdmin");
    }
}
