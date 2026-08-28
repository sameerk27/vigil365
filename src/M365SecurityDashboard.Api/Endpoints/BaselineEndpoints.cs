using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// Tenant baseline capture and drift. The baseline is a frozen copy of the
/// newest TrendSnapshot; drift is the latest snapshot measured against it. All
/// numbers are real collected data — nothing is fabricated.
/// </summary>
public static class BaselineEndpoints
{
    public static void MapBaselineEndpoints(this WebApplication app)
    {
        // Current baseline + drift against the latest snapshot.
        app.MapGet("/api/baseline", async (AppDbContext db, CancellationToken ct) =>
        {
            var baseline = await db.TenantBaselines.AsNoTracking().FirstOrDefaultAsync(b => b.Id == 1, ct);
            var latest = await db.TrendSnapshots.AsNoTracking().OrderByDescending(s => s.CapturedAt).FirstOrDefaultAsync(ct);

            if (baseline is null || baseline.CapturedAt is null)
            {
                // Never captured — honest empty, plus whether a capture is even possible yet.
                return Results.Ok(new { captured = (object?)null, canCapture = latest is not null, drift = Array.Empty<object>() });
            }

            var drift = BaselineDrift.Compute(baseline, latest);
            return Results.Ok(new
            {
                captured = new { at = baseline.CapturedAt, by = baseline.CapturedBy },
                canCapture = latest is not null,
                driftedCount = drift.Count(d => d.Tone == "warn"),
                drift = drift.Select(d => new { d.Metric, d.Baseline, d.Current, d.Drift, d.Tone }),
            });
        }).RequireAuthorization("RequireAnalyst");

        // Capture the newest snapshot as the baseline. Admin-only, audited.
        app.MapPost("/api/baseline/capture", async (AppDbContext db, AuditLogger audit, ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var latest = await db.TrendSnapshots.OrderByDescending(s => s.CapturedAt).FirstOrDefaultAsync(ct);
            if (latest is null)
                return Results.BadRequest(new { ok = false, message = "No trend snapshots collected yet — run a collection first." });

            var baseline = await db.TenantBaselines.FirstOrDefaultAsync(b => b.Id == 1, ct);
            if (baseline is null) { baseline = new TenantBaseline { Id = 1 }; db.TenantBaselines.Add(baseline); }

            var by = caller.FindFirst(ClaimTypes.Email)?.Value ?? caller.Identity?.Name ?? "system";
            BaselineDrift.CaptureFrom(baseline, latest, by, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("baseline.capture", "baseline", "1", $"tenant baseline captured from snapshot {latest.CapturedAt:u}", ct);
            return Results.Ok(new { ok = true, capturedAt = baseline.CapturedAt, capturedBy = baseline.CapturedBy });
        }).RequireAuthorization("RequireAdmin");
    }
}
