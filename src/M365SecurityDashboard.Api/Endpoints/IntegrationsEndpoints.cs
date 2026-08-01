using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Machine-to-machine integration endpoints: API token management and the token-authenticated SIEM export feeds.</summary>
public static class IntegrationsEndpoints
{
    public static void MapIntegrationsEndpoints(this WebApplication app)
    {
        // API tokens for SIEM/read-only machine integrations. The raw token is returned
        // once on create; only a SHA-256 hash is stored.
        app.MapGet("/api/api-tokens", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ApiTokens.AsNoTracking()
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new { t.Id, t.Name, t.Prefix, t.Scopes, t.CreatedAt, t.CreatedBy, t.ExpiresAt, t.LastUsedAt, t.RevokedAt })
                .ToListAsync(ct)))
            .RequireAuthorization("RequireAdmin");

        app.MapPost("/api/api-tokens", async (
            AppDbContext db, AuditLogger audit, System.Security.Claims.ClaimsPrincipal user,
            ApiTokenCreateRequest input, CancellationToken ct) =>
        {
            var (row, rawToken) = ApiTokenService.Create(
                input.Name ?? "SIEM integration",
                input.Scopes ?? "alerts:read,health:read",
                AuthHelpers.GetEmail(user),
                input.ExpiresAt);
            db.ApiTokens.Add(row);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("api_token.create", "api_token", row.Id.ToString(), row.Name, ct);
            return Results.Ok(new { row.Id, row.Name, row.Prefix, row.Scopes, row.CreatedAt, row.ExpiresAt, token = rawToken });
        }).RequireAuthorization("RequireAdmin");

        app.MapPost("/api/api-tokens/{id:guid}/revoke", async (AppDbContext db, AuditLogger audit, Guid id, CancellationToken ct) =>
        {
            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (token is null) return Results.NotFound();
            token.RevokedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("api_token.revoke", "api_token", id.ToString(), token.Name, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("RequireAdmin");

        // SIEM export endpoints use API-token auth, not the browser's Entra delegated token.
        app.MapGet("/api/siem/alerts", async (HttpContext ctx, ApiTokenService tokens, AppDbContext db, CancellationToken ct) =>
        {
            var token = await tokens.ValidateAsync(ReadApiToken(ctx), "alerts:read", ct);
            if (token is null) return Results.Unauthorized();
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            if (DateTimeOffset.TryParse(ctx.Request.Query["since"], out var parsed)) since = parsed;
            var alerts = await db.TriggeredAlerts.AsNoTracking()
                .Where(a => a.TriggeredAt >= since)
                .OrderByDescending(a => a.TriggeredAt)
                .Take(1000)
                .Select(a => new
                {
                    a.Id, a.PolicyId, a.PolicyName, a.Severity, a.Category, a.Condition,
                    a.MetricValue, a.Threshold, a.TriggeredAt, a.Status, a.AffectedEntities,
                    source = "Vigil365"
                })
                .ToListAsync(ct);
            return Results.Ok(new { generatedAt = DateTimeOffset.UtcNow, count = alerts.Count, alerts });
        }).AllowAnonymous();

        app.MapGet("/api/siem/health", async (HttpContext ctx, ApiTokenService tokens, AppDbContext db, CancellationToken ct) =>
        {
            var token = await tokens.ValidateAsync(ReadApiToken(ctx), "health:read", ct);
            if (token is null) return Results.Unauthorized();
            var latestRun = await db.CollectionRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            var notificationHealth = NotificationHealth.Compute(await db.NotificationLogs.AsNoTracking().OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct));
            return Results.Ok(new
            {
                generatedAt = DateTimeOffset.UtcNow,
                latestRun,
                notificationChannels = notificationHealth,
                openTriggeredAlerts = await db.TriggeredAlerts.AsNoTracking().CountAsync(a => a.Status == "new" || a.Status == "acknowledged", ct)
            });
        }).AllowAnonymous();
    }

    private static string? ReadApiToken(HttpContext ctx)
    {
        var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
        var auth = ctx.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;
    }
}
