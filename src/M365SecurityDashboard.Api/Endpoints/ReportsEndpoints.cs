using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Scheduled executive-digest reports: schedule CRUD, immediate run-now dispatch, and live digest preview.</summary>
public static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this WebApplication app)
    {
        // ── Scheduled reports (executive digest) ───────────────────────────────────

        app.MapGet("/api/report-schedules", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ReportSchedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)))
            .RequireAuthorization("RequireAnalyst");

        // Live preview of the digest HTML/CSV without sending anything.
        app.MapGet("/api/reports/exec-digest/preview", async (DigestBuilder builder, int? windowDays, CancellationToken ct) =>
        {
            var digest = await builder.BuildAsync(windowDays ?? 7, ct);
            return Results.Ok(new { digest.Subject, digest.HtmlBody, digest.Csv, digest.GeneratedAt, digest.HasData, digest.Metrics, digest.TopAlerts });
        }).RequireAuthorization("RequireAnalyst");

        app.MapPost("/api/report-schedules", async (AppDbContext db, AuditLogger audit, System.Security.Claims.ClaimsPrincipal user, ReportSchedule input,
            Data.Tenancy.ITenantContext tenant, IOptions<EditionOptions> edition, CancellationToken ct) =>
        {
            // MSP mode: a digest is one client's posture, so a schedule belongs to
            // the selected client and the worker builds it in that client only.
            // Single mode: the install-wide (null) schedule, as before.
            if (edition.Value.IsMsp && tenant.Current is null)
                return Results.BadRequest(new { error = "Select a client first: in MSP mode each report schedule belongs to one client." });
            var s = new ReportSchedule
            {
                TenantId = edition.Value.IsMsp ? tenant.Current : null,
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(input.Name) ? "Weekly executive digest" : input.Name.Trim(),
                ReportType = "exec-digest",
                Cadence = input.Cadence is "daily" or "weekly" or "monthly" ? input.Cadence : "weekly",
                DayOfWeek = Math.Clamp(input.DayOfWeek, 0, 6),
                DayOfMonth = Math.Clamp(input.DayOfMonth, 1, 28),
                HourUtc = Math.Clamp(input.HourUtc, 0, 23),
                Recipients = input.Recipients ?? "",
                IncludeCsv = input.IncludeCsv,
                IncludePdf = input.IncludePdf,
                Enabled = input.Enabled,
                CreatedBy = user.Identity?.Name,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ReportSchedules.Add(s);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("report.schedule.create", "report", s.Id.ToString(), s.Name, ct);
            return Results.Ok(s);
        }).RequireAuthorization("RequireAdmin");

        app.MapPut("/api/report-schedules/{id:guid}", async (AppDbContext db, AuditLogger audit, Guid id, ReportSchedule input, CancellationToken ct) =>
        {
            var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            s.Name = string.IsNullOrWhiteSpace(input.Name) ? s.Name : input.Name.Trim();
            s.Cadence = input.Cadence is "daily" or "weekly" or "monthly" ? input.Cadence : s.Cadence;
            s.DayOfWeek = Math.Clamp(input.DayOfWeek, 0, 6);
            s.DayOfMonth = Math.Clamp(input.DayOfMonth, 1, 28);
            s.HourUtc = Math.Clamp(input.HourUtc, 0, 23);
            s.Recipients = input.Recipients ?? "";
            s.IncludeCsv = input.IncludeCsv;
            s.IncludePdf = input.IncludePdf;
            s.Enabled = input.Enabled;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("report.schedule.update", "report", s.Id.ToString(), s.Name, ct);
            return Results.Ok(s);
        }).RequireAuthorization("RequireAdmin");

        app.MapDelete("/api/report-schedules/{id:guid}", async (AppDbContext db, AuditLogger audit, Guid id, CancellationToken ct) =>
        {
            var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            db.ReportSchedules.Remove(s);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("report.schedule.delete", "report", id.ToString(), s.Name, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("RequireAdmin");

        // Send this report immediately, regardless of cadence.
        app.MapPost("/api/report-schedules/{id:guid}/run-now", async (IServiceProvider sp, AppDbContext db, AuditLogger audit, Guid id, IOptions<EditionOptions> edition, CancellationToken ct) =>
        {
            var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            // An MSP-mode schedule with no client (made before the install became an
            // MSP install) has recipients of no particular client: sending it the
            // selected client's digest could hand one client's posture to another's
            // people. The worker skips it too.
            if (edition.Value.IsMsp && s.TenantId is null)
                return Results.BadRequest(new { ok = false, status = ReportScheduleWorker.UnassignedStatus });
            var (ok, status) = await ReportScheduleWorker.DispatchAsync(sp, db, s, ct);
            s.LastRunAt = DateTimeOffset.UtcNow;
            s.LastRunStatus = status;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("report.schedule.run", "report", id.ToString(), status, ct);
            return Results.Ok(new { ok, status });
        }).RequireAuthorization("RequireAdmin");
    }
}
