using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Evaluates all enabled <see cref="AlertPolicy"/> rows against the latest
/// collected data and persists new <see cref="TriggeredAlert"/> rows. Runs
/// server-side after every collection cycle so alerts fire without a browser.
/// </summary>
public sealed class AlertEvaluator(
    AppDbContext db,
    NotificationSender sender,
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

        if (fired > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Alert evaluation fired {Count} new alert(s)", fired);
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
