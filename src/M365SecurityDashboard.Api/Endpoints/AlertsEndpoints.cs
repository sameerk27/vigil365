using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Alert Center: the collected-alert inventory, policy CRUD/import/export/backtest, suppression rules, the triggered-alert workflow, workbench triage, and analyst notes.</summary>
public static class AlertsEndpoints
{
    public static void MapAlertsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/alerts", async (
            AppDbContext db,
            string? search,
            AlertSeverity? severity,
            M365ServiceArea? service,
            bool? resolved,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            page = page < 1 ? 1 : page;
            pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;

            var query = db.SecurityAlerts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(a =>
                    a.Title.Contains(search) ||
                    (a.UserPrincipalName != null && a.UserPrincipalName.Contains(search)) ||
                    (a.DeviceName != null && a.DeviceName.Contains(search)) ||
                    (a.ExternalId != null && a.ExternalId.Contains(search)));
            }
            if (severity.HasValue) query = query.Where(a => a.Severity == severity.Value);
            if (service.HasValue) query = query.Where(a => a.Service == service.Value);
            if (resolved.HasValue) query = query.Where(a => a.IsResolved == resolved.Value);

            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(a => a.DetectedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return Results.Ok(new { total, page, pageSize, items });
        });

        app.MapGet("/api/alert-coverage", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await RecommendationsEngine.GetAlertCoverageAsync(db, ct)));

        app.MapPost("/api/alert-coverage/enable/{id}", async (AppDbContext db, string id, AuditLogger audit, CancellationToken ct) =>
        {
            var policy = await RecommendationsEngine.EnableCoverageRuleAsync(db, id, ct);
            if (policy == null) return Results.BadRequest(new { error = "Rule not found or cannot be enabled via API." });
            await audit.WriteAsync("coverage.enable", "policy", policy.Id.ToString(), $"Enabled baseline coverage rule {policy.Name}", ct);
            return Results.Ok(await RecommendationsEngine.GetAlertCoverageAsync(db, ct));
        }).RequireAuthorization("RequireAnalyst");

        // ─────────────────────────────────────────────────────────────────────────────
        // Alert Center — server-side policies, triggered alerts, notifications
        // ─────────────────────────────────────────────────────────────────────────────

        // Policies CRUD
        app.MapGet("/api/alert-policies", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AlertPolicies.OrderByDescending(p => p.CreatedAt).ToListAsync(ct)));

        app.MapPost("/api/alert-policies", async (AppDbContext db, AlertPolicy input, AuditLogger audit, CancellationToken ct) =>
        {
            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.TriggerCount = 0;
            if (input.SuppressionMinutes <= 0) input.SuppressionMinutes = 60;
            if (input.WindowMinutes <= 0) input.WindowMinutes = 60;
            if (input.BaselineMultiplier <= 0) input.BaselineMultiplier = 3.0;
            if (input.BaselineDays <= 0) input.BaselineDays = 30;
            db.AlertPolicies.Add(input);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("policy.create", "policy", input.Id.ToString(), $"Created policy {input.Name} ({input.Category})", ct);
            return Results.Ok(input);
        }).RequireAuthorization("RequireAnalyst");

        app.MapPut("/api/alert-policies/{id:guid}", async (AppDbContext db, Guid id, AlertPolicy input, AuditLogger audit, CancellationToken ct) =>
        {
            var p = await db.AlertPolicies.FindAsync([id], ct);
            if (p is null) return Results.NotFound();
            p.Name = input.Name;
            p.Enabled = input.Enabled;
            p.Category = input.Category;
            p.Condition = input.Condition;
            p.Kind = string.IsNullOrWhiteSpace(input.Kind) ? "metric" : input.Kind;
            p.Metric = input.Metric;
            p.ActivityPattern = input.ActivityPattern;
            p.WindowMinutes = input.WindowMinutes <= 0 ? 60 : input.WindowMinutes;
            p.BaselineMultiplier = input.BaselineMultiplier <= 0 ? 3.0 : input.BaselineMultiplier;
            p.BaselineDays = input.BaselineDays <= 0 ? 30 : input.BaselineDays;
            p.Threshold = input.Threshold;
            p.Severity = input.Severity;
            p.NotifyEmail = input.NotifyEmail;
            p.SuppressionMinutes = input.SuppressionMinutes <= 0 ? 60 : input.SuppressionMinutes;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("policy.update", "policy", id.ToString(), $"Updated policy {p.Name}", ct);
            return Results.Ok(p);
        }).RequireAuthorization("RequireAnalyst");

        app.MapDelete("/api/alert-policies/{id:guid}", async (AppDbContext db, Guid id, AuditLogger audit, CancellationToken ct) =>
        {
            var p = await db.AlertPolicies.FindAsync([id], ct);
            if (p is null) return Results.NotFound();
            db.AlertPolicies.Remove(p);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("policy.delete", "policy", id.ToString(), $"Deleted policy {p.Name}", ct);
            return Results.NoContent();
        }).RequireAuthorization("RequireAnalyst");

        // Export the policy set as a portable pack (JSON). Recipients are stripped
        // unless explicitly requested — packs get shared, and NotifyEmail is an
        // internal address.
        app.MapGet("/api/alert-policies/export", async (
            AppDbContext db, bool? includeRecipients, AuditLogger audit, CancellationToken ct) =>
        {
            var withRecipients = includeRecipients ?? false;
            var policies = await db.AlertPolicies.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            var pack = new PolicyPack.Pack(
                PolicyPack.CurrentVersion,
                DateTimeOffset.UtcNow,
                $"Vigil365 {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}",
                withRecipients,
                policies.Select(p => PolicyPack.ToPack(p, withRecipients)).ToList());

            await audit.WriteAsync("policy.export", "policy", "*",
                $"Exported {pack.Policies.Count} policies (recipients {(withRecipients ? "included" : "stripped")})", ct);
            return Results.Ok(pack);
        }).RequireAuthorization("RequireAnalyst");

        // Import a policy pack. Matches existing policies by name (ids differ across
        // installs). mode=skip keeps existing policies untouched; mode=update overwrites
        // them in place, preserving their id and trigger history. Every entry is
        // validated first — an invalid policy is reported, never coerced into place.
        app.MapPost("/api/alert-policies/import", async (
            AppDbContext db, PolicyPack.Pack pack, string? mode, AuditLogger audit, CancellationToken ct) =>
        {
            if (pack is null || pack.Policies is null)
                return Results.BadRequest(new { error = "Not a valid policy pack." });

            if (pack.PackVersion > PolicyPack.CurrentVersion)
                return Results.BadRequest(new { error = $"This pack was made by a newer Vigil365 (pack version {pack.PackVersion}; this install supports {PolicyPack.CurrentVersion})." });

            var update = string.Equals(mode, "update", StringComparison.OrdinalIgnoreCase);
            var existing = await db.AlertPolicies.ToListAsync(ct);

            var imported = new List<string>();
            var updated = new List<string>();
            var skipped = new List<string>();
            var rejected = new List<object>();

            foreach (var entry in pack.Policies)
            {
                var error = PolicyPack.Validate(entry);
                if (error is not null)
                {
                    rejected.Add(new { name = entry?.Name ?? "(unnamed)", error });
                    continue;
                }

                var match = existing.FirstOrDefault(p =>
                    string.Equals(p.Name, entry.Name.Trim(), StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    if (!update) { skipped.Add(match.Name); continue; }
                    PolicyPack.ApplyTo(match, entry);
                    updated.Add(match.Name);
                }
                else
                {
                    var created = PolicyPack.ToEntity(entry);
                    db.AlertPolicies.Add(created);
                    existing.Add(created);   // a pack with duplicate names must not create both
                    imported.Add(created.Name);
                }
            }

            if (imported.Count > 0 || updated.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync("policy.import", "policy", "*",
                    $"Imported {imported.Count}, updated {updated.Count}, skipped {skipped.Count}, rejected {rejected.Count}", ct);
            }

            return Results.Ok(new
            {
                importedCount = imported.Count,
                updatedCount = updated.Count,
                skippedCount = skipped.Count,
                rejectedCount = rejected.Count,
                imported,
                updated,
                skipped,
                rejected,
            });
        }).RequireAuthorization("RequireAnalyst");

        // Policy dry-run: "if this policy had been enabled, how often would it have
        // fired?" Accepts an unsaved draft so a threshold can be tested before it is
        // committed. Read-only — never writes alerts or touches policy state.
        app.MapPost("/api/alert-policies/backtest", async (
            AlertPolicy draft, PolicyBacktester backtester, IOptions<GraphOptions> graph,
            int? days, CancellationToken ct) =>
        {
            if (draft.Threshold < 1)
                return Results.BadRequest(new { error = "Threshold must be at least 1." });

            var interval = TimeSpan.FromMinutes(Math.Max(1, graph.Value.CollectionIntervalMinutes));
            var result = await backtester.RunAsync(draft, days ?? 30, interval, ct);
            return Results.Ok(result);
        }).RequireAuthorization("RequireAnalyst");

        // ── Suppression rules ───────────────────────────────────────────────────────
        // Standing rules that stop known-noisy alerts being raised at all. Mutations are
        // Admin-only and audited: suppressing an alert class is a security decision.
        app.MapGet("/api/suppression-rules", async (AppDbContext db, CancellationToken ct) =>
        {
            var rules = await db.SuppressionRules.AsNoTracking()
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct);
            // Join policy names so the UI does not have to resolve GUIDs itself.
            var names = await db.AlertPolicies.AsNoTracking()
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(rules.Select(r => new
            {
                r.Id, r.PolicyId,
                policyName = r.PolicyId is Guid pid && names.TryGetValue(pid, out var n) ? n : null,
                r.EntityPattern, r.Reason, r.ExpiresAt, r.Enabled,
                r.CreatedAt, r.CreatedBy, r.SuppressedCount, r.LastSuppressedAt,
                expired = r.ExpiresAt is not null && r.ExpiresAt <= now,
            }));
        }).RequireAuthorization("RequireAnalyst");

        app.MapPost("/api/suppression-rules", async (
            SuppressionRuleRequest input, AppDbContext db, AuditLogger audit,
            System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Reason))
                return Results.BadRequest(new { error = "A reason is required — an unexplained suppression cannot be reviewed later." });
            if (input.PolicyId is null && string.IsNullOrWhiteSpace(input.EntityPattern))
                return Results.BadRequest(new { error = "Scope the rule to a policy, an entity pattern, or both. A rule with neither would suppress every alert." });

            var rule = new SuppressionRule
            {
                PolicyId = input.PolicyId,
                EntityPattern = string.IsNullOrWhiteSpace(input.EntityPattern) ? null : input.EntityPattern.Trim(),
                Reason = input.Reason.Trim(),
                ExpiresAt = input.ExpiresAt,
                Enabled = input.Enabled ?? true,
                CreatedBy = caller.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? caller.Identity?.Name,
            };
            db.SuppressionRules.Add(rule);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("suppression.create", "suppression", rule.Id.ToString(),
                $"Suppression added (policy={rule.PolicyId?.ToString() ?? "any"}, entity={rule.EntityPattern ?? "any"}): {rule.Reason}", ct);
            return Results.Ok(rule);
        }).RequireAuthorization("RequireAdmin");

        app.MapPut("/api/suppression-rules/{id:guid}", async (
            Guid id, SuppressionRuleRequest input, AppDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var rule = await db.SuppressionRules.FindAsync([id], ct);
            if (rule is null) return Results.NotFound();

            if (input.Reason is not null) rule.Reason = input.Reason.Trim();
            if (input.Enabled is not null) rule.Enabled = input.Enabled.Value;
            rule.ExpiresAt = input.ExpiresAt;
            if (input.EntityPattern is not null)
                rule.EntityPattern = string.IsNullOrWhiteSpace(input.EntityPattern) ? null : input.EntityPattern.Trim();

            if (rule.PolicyId is null && string.IsNullOrWhiteSpace(rule.EntityPattern))
                return Results.BadRequest(new { error = "A rule must stay scoped to a policy or an entity pattern." });

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("suppression.update", "suppression", id.ToString(),
                $"Suppression updated (enabled={rule.Enabled})", ct);
            return Results.Ok(rule);
        }).RequireAuthorization("RequireAdmin");

        app.MapDelete("/api/suppression-rules/{id:guid}", async (
            Guid id, AppDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var rule = await db.SuppressionRules.FindAsync([id], ct);
            if (rule is null) return Results.NotFound();
            db.SuppressionRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("suppression.delete", "suppression", id.ToString(),
                $"Suppression removed: {rule.Reason}", ct);
            return Results.NoContent();
        }).RequireAuthorization("RequireAdmin");

        // Triggered alerts
        app.MapGet("/api/triggered-alerts", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.TriggeredAlerts.OrderByDescending(t => t.TriggeredAt).Take(500).ToListAsync(ct)));

        app.MapPost("/api/triggered-alerts/{id:guid}/acknowledge", async (
            AppDbContext db, Guid id, System.Security.Claims.ClaimsPrincipal caller, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.TriggeredAlerts.FindAsync([id], ct);
            if (t is null) return Results.NotFound();
            t.Status = "acknowledged";
            t.AcknowledgedAt = DateTimeOffset.UtcNow;
            t.AcknowledgedBy = AuthHelpers.GetEmail(caller);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.acknowledge", "triggered_alert", id.ToString(), $"Acknowledged alert for policy {t.PolicyName}", ct);
            return Results.Ok(t);
        }).RequireAuthorization("RequireAnalyst");

        // Alert-ops metrics: MTTA, MTTR, resolution rate, analyst workload over a window.
        // Reads timestamps already captured on the triggered-alert workflow.
        app.MapGet("/api/triggered-alerts/metrics", async (AppDbContext db, int? days, CancellationToken ct) =>
        {
            var windowDays = days is > 0 and <= 365 ? days.Value : 30;
            var since = DateTimeOffset.UtcNow.AddDays(-windowDays);
            var rows = await db.TriggeredAlerts.AsNoTracking()
                .Where(t => t.TriggeredAt >= since)
                .ToListAsync(ct);
            return Results.Ok(new { windowDays, metrics = AlertMetrics.Compute(rows) });
        }).RequireAuthorization("RequireAnalyst");

        app.MapPost("/api/triggered-alerts/{id:guid}/resolve", async (AppDbContext db, Guid id, AuditLogger audit, System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var t = await db.TriggeredAlerts.FindAsync([id], ct);
            if (t is null) return Results.NotFound();
            t.Status = "resolved";
            t.ResolvedAt = DateTimeOffset.UtcNow;
            t.ResolvedBy = caller.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                ?? caller.Identity?.Name ?? "dashboard";
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.resolve", "triggered_alert", id.ToString(), $"Resolved alert for policy {t.PolicyName}", ct);
            return Results.Ok(t);
        }).RequireAuthorization("RequireAnalyst");

        // Per-alert snooze. Body: { "until": "2026-06-22T18:00:00Z" } or { "durationHours": 4|24|168 }.
        // Until wins if both are supplied; durationHours defaults to 24 if neither is supplied.
        app.MapPost("/api/triggered-alerts/{id:guid}/snooze", async (
            AppDbContext db, Guid id, SnoozeRequest input, System.Security.Claims.ClaimsPrincipal caller, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.TriggeredAlerts.FindAsync([id], ct);
            if (t is null) return Results.NotFound();
            if (t.Status is "resolved" or "auto_resolved")
                return Results.BadRequest(new { error = "Cannot snooze a terminal alert." });

            var until = input.Until
                ?? (input.DurationHours is { } h ? DateTimeOffset.UtcNow.AddHours(Math.Clamp(h, 1, 8760)) : DateTimeOffset.UtcNow.AddHours(24));
            t.SnoozedUntil = until;
            t.SnoozedBy = AuthHelpers.GetEmail(caller);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.snooze", "triggered_alert", id.ToString(), $"Snoozed alert for policy {t.PolicyName} until {until:u}", ct);
            return Results.Ok(t);
        }).RequireAuthorization("RequireAnalyst");

        // Reopen a triggered alert (undo for acknowledge/resolve). Returns it to "new".
        app.MapPost("/api/triggered-alerts/{id:guid}/reopen", async (
            AppDbContext db, Guid id, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.TriggeredAlerts.FindAsync([id], ct);
            if (t is null) return Results.NotFound();
            var was = t.Status;
            t.Status = "new";
            t.AcknowledgedAt = null;
            t.AcknowledgedBy = null;
            t.BelowThresholdStreakCount = 0;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.reopen", "triggered_alert", id.ToString(), $"reopened (was {was})", ct);
            return Results.Ok(t);
        }).RequireAuthorization("RequireAnalyst");

        app.MapPost("/api/triggered-alerts/{id:guid}/unsnooze", async (
            AppDbContext db, Guid id, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.TriggeredAlerts.FindAsync([id], ct);
            if (t is null) return Results.NotFound();
            t.SnoozedUntil = null;
            t.SnoozedBy = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.unsnooze", "triggered_alert", id.ToString(), $"Unsnoozed alert for policy {t.PolicyName}", ct);
            return Results.Ok(t);
        }).RequireAuthorization("RequireAnalyst");

        // ─────────────────────────────────────────────────────────────────────────────
        // Alert workbench — local triage state. Never writes to Microsoft 365.
        // ─────────────────────────────────────────────────────────────────────────────

        // Assign / set disposition on a collected M365 security alert.
        app.MapPost("/api/alerts/{id:long}/workbench", async (
            long id, WorkbenchRequest input, AppDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var alert = await db.SecurityAlerts.FindAsync([id], ct);
            if (alert is null) return Results.NotFound();

            if (input.Disposition is not null)
            {
                var d = input.Disposition.Trim().ToLowerInvariant();
                if (d != "" && d != "reviewed" && d != "escalated" && d != "false_positive")
                    return Results.BadRequest(new { error = "Disposition must be reviewed, escalated, false_positive, or empty to clear." });
                alert.Disposition = d == "" ? null : d;
                await audit.WriteAsync("alert.disposition", "alert", id.ToString(), $"disposition set to {(alert.Disposition ?? "none")}", ct);
            }
            if (input.AssignedTo is not null)
            {
                alert.AssignedTo = string.IsNullOrWhiteSpace(input.AssignedTo) ? null : input.AssignedTo.Trim().ToLowerInvariant();
                await audit.WriteAsync("alert.assign", "alert", id.ToString(), alert.AssignedTo is null ? "unassigned" : $"assigned to {alert.AssignedTo}", ct);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(alert);
        }).RequireAuthorization("RequireAnalyst");

        // Assign a triggered policy alert.
        app.MapPost("/api/triggered-alerts/{id:guid}/assign", async (
            Guid id, WorkbenchRequest input, AppDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.TriggeredAlerts.FindAsync([id], ct);
            if (t is null) return Results.NotFound();
            t.AssignedTo = string.IsNullOrWhiteSpace(input.AssignedTo) ? null : input.AssignedTo!.Trim().ToLowerInvariant();
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.assign", "triggered_alert", id.ToString(),
                t.AssignedTo is null ? "unassigned" : $"assigned to {t.AssignedTo}", ct);
            return Results.Ok(t);
        }).RequireAuthorization("RequireAnalyst");

        // Analyst notes — append-only, on either alert kind.
        app.MapGet("/api/alert-notes/{kind}/{targetId}", async (
            string kind, string targetId, AppDbContext db, CancellationToken ct) =>
        {
            if (kind != "security" && kind != "policy") return Results.BadRequest(new { error = "Kind must be security or policy." });
            return Results.Ok(await db.AlertNotes.AsNoTracking()
                .Where(n => n.TargetKind == kind && n.TargetId == targetId)
                .OrderBy(n => n.CreatedAt)
                .ToListAsync(ct));
        });

        app.MapPost("/api/alert-notes/{kind}/{targetId}", async (
            string kind, string targetId, NoteRequest input, AppDbContext db, AuditLogger audit,
            System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
        {
            if (kind != "security" && kind != "policy") return Results.BadRequest(new { error = "Kind must be security or policy." });
            var text = (input.Text ?? "").Trim();
            if (text.Length == 0) return Results.BadRequest(new { error = "Note text is required." });
            if (text.Length > 2000) text = text[..2000];

            var note = new AlertNote
            {
                TargetKind = kind,
                TargetId = targetId,
                Author = AuthHelpers.GetEmail(caller),
                Text = text,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.AlertNotes.Add(note);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("alert.note", kind == "security" ? "alert" : "triggered_alert", targetId, "note added", ct);
            return Results.Ok(note);
        }).RequireAuthorization("RequireAnalyst");

        // Manually run an evaluation pass (used by the dashboard "refresh" + on-demand check).
        // Analyst+: evaluation dispatches real notifications, so it must not be open to abuse.
        app.MapPost("/api/alert-policies/evaluate", async (AlertEvaluator evaluator, CancellationToken ct) =>
        {
            var fired = await evaluator.EvaluateAsync(ct);
            return Results.Ok(new { fired });
        }).RequireAuthorization("RequireAnalyst");
    }
}
