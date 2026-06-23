using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Evaluates all enabled <see cref="AlertPolicy"/> rows against the latest
/// collected data and persists new <see cref="TriggeredAlert"/> rows. Runs
/// server-side after every collection cycle so alerts fire without a browser.
/// Also auto-resolves stale alerts whose underlying metric has recovered.
/// </summary>
public sealed class AlertEvaluator(
    AppDbContext db,
    NotificationSender sender,
    IOptions<AlertingOptions> options,
    ILogger<AlertEvaluator> logger)
{
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var policies = await db.AlertPolicies.Where(p => p.Enabled).ToListAsync(ct);
        if (policies.Count == 0) return 0;

        var metrics = await ComputeMetricsAsync(ct);
        var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct)
                  ?? new NotificationSettings { Id = 1 };

        var now = DateTimeOffset.UtcNow;
        var fired = 0;

        // Map PolicyId -> Metric key so the auto-resolve loop below can look up
        // each open alert's current metric without re-querying the policy table.
        var policyMetricById = policies.ToDictionary(p => p.Id, p => p.Metric);

        foreach (var policy in policies)
        {
            var value = metrics.GetValueOrDefault(policy.Metric, 0);
            if (value < policy.Threshold) continue;

            // Suppress re-notification while a recent "new" alert for this policy exists.
            var window = now.AddMinutes(-Math.Max(1, policy.SuppressionMinutes));
            var recent = await db.TriggeredAlerts.AnyAsync(
                t => t.PolicyId == policy.Id && t.Status == "new" && t.TriggeredAt >= window, ct);
            if (recent) continue;

            var alert = new TriggeredAlert
            {
                Id = Guid.NewGuid(),
                PolicyId = policy.Id,
                PolicyName = policy.Name,
                Severity = policy.Severity,
                Category = policy.Category,
                Condition = policy.Condition,
                MetricValue = value,
                Threshold = policy.Threshold,
                TriggeredAt = now,
                Status = "new",
            };
            db.TriggeredAlerts.Add(alert);

            policy.LastTriggered = now;
            policy.TriggerCount++;
            fired++;

            try
            {
                await sender.DispatchAsync(db, cfg, alert, ct);
                alert.Notified = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification dispatch failed for {Policy}", policy.Name);
            }
        }

        // Auto-resolve: scan non-terminal alerts and update streak counters.
        // Resolves silently — no notification dispatch, no NotificationLog write.
        var streakTarget = Math.Max(1, options.Value.AutoResolveDebounceCycles);
        var openAlerts = await db.TriggeredAlerts
            .Where(t => t.Status != "resolved" && t.Status != "auto_resolved")
            .ToListAsync(ct);
        var autoResolved = 0;
        foreach (var alert in openAlerts)
        {
            if (!policyMetricById.TryGetValue(alert.PolicyId, out var metricKey)) continue;
            var current = metrics.GetValueOrDefault(metricKey, 0);

            if (current < alert.Threshold)
            {
                alert.BelowThresholdStreakCount++;
                if (alert.BelowThresholdStreakCount >= streakTarget)
                {
                    alert.Status = "auto_resolved";
                    autoResolved++;
                }
            }
            else if (alert.BelowThresholdStreakCount != 0)
            {
                alert.BelowThresholdStreakCount = 0;
            }
            alert.LastEvaluatedAt = now;
        }

        if (fired > 0 || autoResolved > 0)
        {
            await db.SaveChangesAsync(ct);
            if (fired > 0)
                logger.LogInformation("Alert evaluation fired {Count} new alert(s)", fired);
            if (autoResolved > 0)
                logger.LogInformation("Auto-resolved {Count} alert(s) after metric recovery", autoResolved);
        }
        return fired;
    }

    /// <summary>Compute the metric values the policy engine watches.</summary>
    public async Task<Dictionary<string, int>> ComputeMetricsAsync(CancellationToken ct)
    {
        var open = db.SecurityAlerts.Where(a => !a.IsResolved);

        var criticalAlertCount = await open.CountAsync(a => a.Severity == AlertSeverity.Critical, ct);
        var highAlertCount = await open.CountAsync(a => a.Severity == AlertSeverity.High, ct);
        var riskyUsersCount = await open.CountAsync(a => a.AlertType == "RiskyUser", ct);
        var mfaMissingCount = await open.CountAsync(a => a.AlertType == "MfaStatus", ct);
        var nonCompliantCount = await open.CountAsync(a => a.AlertType == "NonCompliantDevice", ct);
        var staleDeviceCount = await open.CountAsync(a => a.AlertType == "DeviceNotCheckedIn", ct);
        var failedSignInCount = await open.CountAsync(a => a.AlertType == "FailedSignIn", ct);
        var serviceIssueCount = await open.CountAsync(a => a.Service == M365ServiceArea.ServiceHealth, ct);
        var alertCount = await open.CountAsync(ct);

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["criticalAlertCount"] = criticalAlertCount,
            ["highAlertCount"] = highAlertCount,
            ["riskyUsersCount"] = riskyUsersCount,
            ["mfaMissingCount"] = mfaMissingCount,
            ["nonCompliantCount"] = nonCompliantCount,
            ["staleDeviceCount"] = staleDeviceCount,
            ["failedSignInCount"] = failedSignInCount,
            ["serviceIssueCount"] = serviceIssueCount,
            ["expiredLicenseCount"] = 0,
            ["alertCount"] = alertCount,
        };
    }
}
