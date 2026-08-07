using System.Text.Json;
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

        // Standing suppression rules, loaded once per cycle. Tracked (not
        // AsNoTracking) so hit counters persist with the rest of this cycle's save.
        var suppressions = await db.SuppressionRules
            .Where(s => s.Enabled && (s.ExpiresAt == null || s.ExpiresAt > now))
            .ToListAsync(ct);

        // Map PolicyId -> policy so the auto-resolve loop below can recompute
        // each open alert's current value without re-querying the policy table.
        var policyById = policies.ToDictionary(p => p.Id);

        var collapsed = 0;
        var suppressed = 0;
        foreach (var policy in policies)
        {
            var value = await ComputePolicyValueAsync(policy, metrics, now, ct);
            if (value < policy.Threshold) continue;

            // One open alert per policy: while the breach persists, the existing
            // open alert is updated in place (current value, affected entities,
            // last-evaluated time) instead of stacking a new row every cycle.
            // A new row — and a new notification — only happens after the previous
            // alert reached a terminal state (resolved / auto-resolved).
            var openForPolicy = await db.TriggeredAlerts
                .Where(t => t.PolicyId == policy.Id && t.Status != "resolved" && t.Status != "auto_resolved")
                .OrderByDescending(t => t.TriggeredAt)
                .ToListAsync(ct);
            if (openForPolicy.Count > 0)
            {
                var keeper = openForPolicy[0];
                keeper.MetricValue = value;
                keeper.Threshold = policy.Threshold;
                keeper.LastEvaluatedAt = now;
                keeper.AffectedEntities = await GetAffectedEntitiesJsonAsync(policy, now, ct);
                // Collapse duplicates accumulated by the old fire-every-cycle
                // behaviour — keep the newest, retire the rest silently.
                foreach (var dup in openForPolicy.Skip(1))
                {
                    dup.Status = "auto_resolved";
                    dup.ResolvedAt ??= now;
                    dup.ResolvedBy ??= "system";
                    collapsed++;
                }
                continue;
            }

            var affectedEntitiesJson = await GetAffectedEntitiesJsonAsync(policy, now, ct);

            // Standing suppression: stop the alert being raised at all (no row,
            // no notification). Checked here rather than at display time so a
            // known-noisy condition costs nothing downstream. The counter makes
            // an over-broad rule visible instead of silently swallowing alerts.
            var suppressedBy = SuppressionMatcher.FindMatch(suppressions, policy.Id, affectedEntitiesJson, now);
            if (suppressedBy is not null)
            {
                suppressedBy.SuppressedCount++;
                suppressedBy.LastSuppressedAt = now;
                suppressed++;
                logger.LogInformation(
                    "Alert for policy {Policy} suppressed by rule {RuleId} ({Reason})",
                    policy.Name, suppressedBy.Id, suppressedBy.Reason);
                continue;
            }

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
                AffectedEntities = affectedEntitiesJson
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
            if (!policyById.TryGetValue(alert.PolicyId, out var alertPolicy)) continue;
            var current = await ComputePolicyValueAsync(alertPolicy, metrics, now, ct);

            if (current < alert.Threshold)
            {
                alert.BelowThresholdStreakCount++;
                if (alert.BelowThresholdStreakCount >= streakTarget)
                {
                    alert.Status = "auto_resolved";
                    alert.ResolvedAt = now;
                    alert.ResolvedBy = "system";
                    autoResolved++;
                }
            }
            else if (alert.BelowThresholdStreakCount != 0)
            {
                alert.BelowThresholdStreakCount = 0;
            }
            alert.LastEvaluatedAt = now;
        }

        // Always save — in-place updates to open alerts happen even when nothing fired.
        await db.SaveChangesAsync(ct);
        if (fired > 0)
            logger.LogInformation("Alert evaluation fired {Count} new alert(s)", fired);
        if (autoResolved > 0)
            logger.LogInformation("Auto-resolved {Count} alert(s) after metric recovery", autoResolved);
        if (collapsed > 0)
            logger.LogInformation("Collapsed {Count} duplicate open alert(s) into one per policy", collapsed);
        if (suppressed > 0)
            logger.LogInformation("Suppressed {Count} alert(s) via standing suppression rules", suppressed);
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

    // The UI parses these camelCase — the default (PascalCase) serialization was
    // why entity rows rendered as "System / N/A" regardless of real data.
    private static readonly JsonSerializerOptions EntityJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Current value for a policy: metric lookup, activity-window count, or anomaly spike value.</summary>
    public async Task<int> ComputePolicyValueAsync(
        AlertPolicy policy, Dictionary<string, int> metrics, DateTimeOffset now, CancellationToken ct)
    {
        if (policy.Kind.Equals("anomaly", StringComparison.OrdinalIgnoreCase))
            return await ComputeAnomalyPolicyValueAsync(policy, now, ct);

        if (!policy.Kind.Equals("activity", StringComparison.OrdinalIgnoreCase))
            return metrics.GetValueOrDefault(policy.Metric, 0);

        var pattern = (policy.ActivityPattern ?? "").Trim();
        if (pattern.Length == 0) return 0;
        var like = pattern.Replace("*", "%");
        var since = now.AddMinutes(-Math.Max(1, policy.WindowMinutes));
        return await db.AuditEvents.CountAsync(
            e => e.OccurredAt >= since && EF.Functions.Like(e.Activity, like), ct);
    }

    private async Task<string?> GetAffectedEntitiesJsonAsync(AlertPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        if (policy.Kind.Equals("activity", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = (policy.ActivityPattern ?? "").Trim();
            if (pattern.Length == 0) return null;
            var like = pattern.Replace("*", "%");
            var since = now.AddMinutes(-Math.Max(1, policy.WindowMinutes));
            var events = await db.AuditEvents
                .Where(e => e.OccurredAt >= since && EF.Functions.Like(e.Activity, like))
                .OrderByDescending(e => e.OccurredAt)
                .Take(50)
                .Select(e => new
                {
                    e.Id,
                    UserPrincipalName = e.ActorUpn ?? e.ActorApp,
                    DeviceName = (string?)null,
                    Title = e.TargetName != null ? e.Activity + " → " + e.TargetName : e.Activity,
                    PortalUrl = (string?)null,
                    DetectedAt = e.OccurredAt,
                    e.ExternalId,
                })
                .ToListAsync(ct);
            return events.Count == 0 ? null : JsonSerializer.Serialize(events, EntityJson);
        }
        if (policy.Kind.Equals("anomaly", StringComparison.OrdinalIgnoreCase))
        {
            var details = await GetAnomalyDetailsAsync(policy, now, ct);
            return details is null ? null : JsonSerializer.Serialize(new[] { details }, EntityJson);
        }
        return await GetMetricEntitiesJsonAsync(policy.Metric, ct);
    }

    private async Task<int> ComputeAnomalyPolicyValueAsync(AlertPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        var details = await GetAnomalyDetailsAsync(policy, now, ct);
        return details?.CurrentValueRounded ?? 0;
    }

    private async Task<AnomalyDetails?> GetAnomalyDetailsAsync(AlertPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        var metric = (policy.Metric ?? "").Trim();
        if (metric.Length == 0) return null;

        var latest = await db.TrendSnapshots
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is null) return null;

        var baselineDays = Math.Max(1, policy.BaselineDays);
        var baselineStart = now.AddDays(-baselineDays);
        var baselineEnd = now.AddHours(-24);
        if (baselineEnd <= baselineStart) return null;

        var baselineSnapshots = await db.TrendSnapshots
            .Where(s => s.CapturedAt >= baselineStart && s.CapturedAt < baselineEnd)
            .ToListAsync(ct);
        if (baselineSnapshots.Count == 0) return null;

        var current = GetTrendValue(latest, metric);
        var baselineAverage = baselineSnapshots.Average(s => GetTrendValue(s, metric));
        var baselineFloor = Math.Max(1.0, baselineAverage);
        var multiplier = policy.BaselineMultiplier <= 0 ? 3.0 : policy.BaselineMultiplier;
        var requiredByBaseline = baselineFloor * multiplier;
        var absoluteThreshold = Math.Max(1, policy.Threshold);

        if (current < absoluteThreshold || current < requiredByBaseline) return null;

        return new AnomalyDetails(
            metric,
            Math.Round(current, 2),
            (int)Math.Round(current, MidpointRounding.AwayFromZero),
            Math.Round(baselineAverage, 2),
            multiplier,
            baselineDays,
            latest.CapturedAt,
            $"Anomalous {metric}: {current:0.##} vs {baselineAverage:0.##} baseline");
    }

    private static double GetTrendValue(TrendSnapshot snapshot, string metricKey) =>
        metricKey.ToLowerInvariant() switch
        {
            "riskyuserscount" => snapshot.RiskyUsersCount,
            "mfacoveragepct" => snapshot.MfaCoveragePct,
            "noncompliantcount" or "noncompliantdevicescount" => snapshot.NonCompliantDevicesCount,
            "criticalalertcount" or "criticalalertscount" => snapshot.CriticalAlertsCount,
            "highalertcount" or "highalertscount" => snapshot.HighAlertsCount,
            "securescorepct" => snapshot.SecureScorePct,
            "complianceissuescount" => snapshot.ComplianceIssuesCount,
            _ => 0
        };

    private sealed record AnomalyDetails(
        string Metric,
        double CurrentValue,
        int CurrentValueRounded,
        double BaselineAverage,
        double BaselineMultiplier,
        int BaselineDays,
        DateTimeOffset DetectedAt,
        string Title);

    private async Task<string?> GetMetricEntitiesJsonAsync(string metricKey, CancellationToken ct)
    {
        var open = db.SecurityAlerts.Where(a => !a.IsResolved);
        IQueryable<SecurityAlert> query = metricKey.ToLowerInvariant() switch
        {
            "criticalalertcount" => open.Where(a => a.Severity == AlertSeverity.Critical),
            "highalertcount" => open.Where(a => a.Severity == AlertSeverity.High),
            "riskyuserscount" => open.Where(a => a.AlertType == "RiskyUser"),
            "mfamissingcount" => open.Where(a => a.AlertType == "MfaStatus"),
            "noncompliantcount" => open.Where(a => a.AlertType == "NonCompliantDevice"),
            "staledevicecount" => open.Where(a => a.AlertType == "DeviceNotCheckedIn"),
            "failedsignincount" => open.Where(a => a.AlertType == "FailedSignIn"),
            "serviceissuecount" => open.Where(a => a.Service == M365ServiceArea.ServiceHealth),
            "alertcount" => open,
            _ => null
        } ?? open.Where(a => false);

        var entities = await query
            .OrderByDescending(a => a.DetectedAt)
            .Select(a => new
            {
                a.Id,
                a.UserPrincipalName,
                a.DeviceName,
                a.Title,
                a.PortalUrl,
                a.DetectedAt,
                a.ExternalId
            })
            .ToListAsync(ct);

        if (entities.Count == 0) return null;
        return JsonSerializer.Serialize(entities, EntityJson);
    }
}
