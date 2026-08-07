using Microsoft.EntityFrameworkCore;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

public static class RecommendationsEngine
{
    public static async Task<List<SecurityRecommendation>> GetRecommendationsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var list = new List<SecurityRecommendation>();

        var mfaMissing = await db.SecurityAlerts.AsNoTracking().CountAsync(a => a.AlertType == "MfaStatus" && !a.IsResolved, ct);
        list.Add(new SecurityRecommendation
        {
            Id = "rec-mfa-registration",
            Category = "Identity",
            Title = "Enforce Multi-Factor Authentication Registration",
            Severity = mfaMissing > 10 ? "critical" : mfaMissing > 0 ? "high" : "low",
            AffectedCount = mfaMissing,
            WhyItMatters = "Accounts without MFA are 99.9% more susceptible to automated password spray, credential stuffing, and phishing attacks.",
            RemediationSteps = new List<string>
            {
                "Navigate to Microsoft Entra ID -> Authentication methods -> Registration campaign.",
                "Enable Microsoft Authenticator push notifications as the default method.",
                "Target non-compliant user accounts and enforce a 14-day grace period for enrollment."
            },
            PortalBladeName = "Microsoft Entra ID — Authentication Methods",
            PortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/AuthenticationMethodsMenuBlade/~/AdminAuthMethods"
        });

        var riskyUsers = await db.SecurityAlerts.AsNoTracking().CountAsync(a => a.AlertType == "RiskyUser" && !a.IsResolved, ct);
        list.Add(new SecurityRecommendation
        {
            Id = "rec-risky-users",
            Category = "Identity",
            Title = "Investigate & Remediate High-Risk Accounts",
            Severity = riskyUsers > 0 ? "critical" : "low",
            AffectedCount = riskyUsers,
            WhyItMatters = "Identity Protection has detected anomalous behavior indicating active credential compromise or impossible travel.",
            RemediationSteps = new List<string>
            {
                "Open the user profile in Entra ID Risky Users and review detection triggers.",
                "Trigger 'Confirm user compromised' to immediately revoke active refresh tokens.",
                "Require secure self-service password reset (SSPR) with MFA verification."
            },
            PortalBladeName = "Microsoft Entra ID — Risky Users",
            PortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskyUsersBlade"
        });

        var nonCompliantDevices = await db.SecurityAlerts.AsNoTracking().CountAsync(a => a.AlertType == "NonCompliantDevice" && !a.IsResolved, ct);
        list.Add(new SecurityRecommendation
        {
            Id = "rec-device-compliance",
            Category = "Devices",
            Title = "Quarantine Non-Compliant & Unpatched Endpoints",
            Severity = nonCompliantDevices > 5 ? "high" : nonCompliantDevices > 0 ? "medium" : "low",
            AffectedCount = nonCompliantDevices,
            WhyItMatters = "Endpoints failing compliance check-ins may lack critical OS security patches, BitLocker encryption, or active EDR agents.",
            RemediationSteps = new List<string>
            {
                "Filter Intune Device Compliance list for non-compliant hardware.",
                "Verify device encryption status and Antimalware signature updates.",
                "Link Conditional Access device policies to block access from non-compliant devices."
            },
            PortalBladeName = "Microsoft Intune — Device Compliance",
            PortalDeepLink = "https://intune.microsoft.com/#view/Microsoft_Intune_DeviceSettings/DevicesMenu/~/compliance"
        });

        var emailMalware = await db.SecurityAlerts.AsNoTracking().CountAsync(a => a.Service == M365ServiceArea.ExchangeOnline && !a.IsResolved, ct);
        list.Add(new SecurityRecommendation
        {
            Id = "rec-email-quarantine",
            Category = "Email & Collaboration",
            Title = "Review Malicious Email & Quarantine Detections",
            Severity = emailMalware > 0 ? "high" : "low",
            AffectedCount = emailMalware,
            WhyItMatters = "Phishing and malware payloads targeting employee mailboxes can lead to ransomware execution and business email compromise (BEC).",
            RemediationSteps = new List<string>
            {
                "Access the Microsoft Defender Quarantine portal.",
                "Inspect header details and sender domain reputation for quarantined payloads.",
                "Submit false-negatives or malicious attachments to Microsoft Threat Explorer."
            },
            PortalBladeName = "Microsoft Defender Portal — Quarantine",
            PortalDeepLink = "https://security.microsoft.com/quarantine"
        });

        var staleDevices = await db.SecurityAlerts.AsNoTracking().CountAsync(a => a.AlertType == "StaleDevice" && !a.IsResolved, ct);
        list.Add(new SecurityRecommendation
        {
            Id = "rec-stale-devices",
            Category = "Devices",
            Title = "Prune Stale Endpoints (> 7 Days Inactive)",
            Severity = staleDevices > 10 ? "medium" : "low",
            AffectedCount = staleDevices,
            WhyItMatters = "Orphaned computer accounts and stale endpoints inflate licensing costs and present unmanaged attack surface.",
            RemediationSteps = new List<string>
            {
                "Identify devices with last check-in timestamp older than 7 days.",
                "Retire or wipe inactive corporate endpoints no longer in active employee custody.",
                "Purge stale hardware records from Entra ID device directory."
            },
            PortalBladeName = "Microsoft Intune — All Devices",
            PortalDeepLink = "https://intune.microsoft.com/#view/Microsoft_Intune_DeviceSettings/DevicesMenu/~/allDevices"
        });

        var highCriticalAlerts = await db.SecurityAlerts.AsNoTracking().CountAsync(a => !a.IsResolved && (a.Severity == AlertSeverity.High || a.Severity == AlertSeverity.Critical), ct);
        list.Add(new SecurityRecommendation
        {
            Id = "rec-high-severity-incidents",
            Category = "Infrastructure",
            Title = "Triage Unresolved High & Critical Incidents",
            Severity = highCriticalAlerts > 0 ? "critical" : "low",
            AffectedCount = highCriticalAlerts,
            WhyItMatters = "Unattended high and critical severity alerts indicate potential active intrusion or lateral movement across M365 services.",
            RemediationSteps = new List<string>
            {
                "Sort active alerts by severity descending in Incident Command.",
                "Assign primary incident handler to investigate root cause.",
                "Acknowledge alert status to initiate automated containment workflows."
            },
            PortalBladeName = "Microsoft Defender Portal — Incidents",
            PortalDeepLink = "https://security.microsoft.com/incidents"
        });

        return list;
    }

    private static readonly AlertBaselineRule[] BaselineCatalog =
    [
        new() { Id = "base-01", Title = "Critical Security Alerts", Category = "identity", Severity = "critical", RuleType = "Vigil365", Metric = "criticalAlertCount", DefaultThreshold = 1, Description = "Fires when open critical security alerts reach or exceed 1." },
        new() { Id = "base-02", Title = "MFA Not Registered", Category = "identity", Severity = "high", RuleType = "Vigil365", Metric = "mfaMissingCount", DefaultThreshold = 5, Description = "Fires when users missing MFA registration reach or exceed 5." },
        new() { Id = "base-03", Title = "Risky Users Detected", Category = "identity", Severity = "high", RuleType = "Vigil365", Metric = "riskyUsersCount", DefaultThreshold = 1, Description = "Fires when active high-risk users are detected in Entra ID." },
        new() { Id = "base-04", Title = "Non-Compliant Devices", Category = "devices", Severity = "medium", RuleType = "Vigil365", Metric = "nonCompliantCount", DefaultThreshold = 1, Description = "Fires when endpoints fail Intune compliance checks." },
        new() { Id = "base-05", Title = "Stale Devices", Category = "devices", Severity = "low", RuleType = "Vigil365", Metric = "staleDeviceCount", DefaultThreshold = 1, Description = "Fires when devices have not checked in for more than 7 days." },
        new() { Id = "base-06", Title = "High Priority Alerts", Category = "identity", Severity = "high", RuleType = "Vigil365", Metric = "highAlertCount", DefaultThreshold = 3, Description = "Fires when open high-severity alerts exceed threshold." },
        new() { Id = "base-07", Title = "Service Health Advisory", Category = "infrastructure", Severity = "medium", RuleType = "Vigil365", Metric = "serviceIssueCount", DefaultThreshold = 1, Description = "Fires when Microsoft 365 service degrades or outages occur." },
        new() { Id = "base-08", Title = "Mass File Deletion Spike", Category = "data protection", Severity = "high", RuleType = "Vigil365", Metric = "massDeletionCount", DefaultThreshold = 10, Description = "Fires when bulk file removal detected across SharePoint/OneDrive." },
        new() { Id = "base-09", Title = "Sudden Risky Sign-In Spike", Category = "identity", Severity = "high", RuleType = "Vigil365", Metric = "riskySignInCount", DefaultThreshold = 5, Description = "Fires when anomalous sign-in failures surge within 24 hours." },
        new() { Id = "base-10", Title = "Email Malware Quarantine Surge", Category = "email", Severity = "high", RuleType = "Vigil365", Metric = "malwareQuarantineCount", DefaultThreshold = 3, Description = "Fires when inbound malicious attachments exceed normal volume." },
        
        new() { Id = "base-11", Title = "Privileged Role Assignment Elevation", Category = "identity", Severity = "critical", RuleType = "NativeM365", Description = "Detects whenever Global Admin or Security Admin role is assigned outside PIM workflow.", NativePortalBlade = "Microsoft Entra ID — Roles & Admins", NativePortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RolesManagementMenuBlade/~/AllRoles" },
        new() { Id = "base-12", Title = "Suspicious Mailbox Forwarding Rule", Category = "email", Severity = "high", RuleType = "NativeM365", Description = "Detects creation of inbox rules forwarding mail to external personal domains.", NativePortalBlade = "Microsoft Purview — Alert Policies", NativePortalDeepLink = "https://purview.microsoft.com/alertpolicies" },
        new() { Id = "base-13", Title = "Impossible Travel Sign-In Activity", Category = "identity", Severity = "high", RuleType = "NativeM365", Description = "Detects successful authentications from geographically distant IPs in physically impossible time window.", NativePortalBlade = "Microsoft Entra ID — Identity Protection", NativePortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/IdentityProtectionMenuBlade/~/RiskyUsers" },
        new() { Id = "base-14", Title = "New OAuth Application Consent Grant", Category = "identity", Severity = "medium", RuleType = "NativeM365", Description = "Alerts when third-party applications request delegated mailbox or directory permissions.", NativePortalBlade = "Microsoft Entra ID — Enterprise Apps", NativePortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/StartboardApplicationsMenuBlade/~/AppAppsPreview" },
        new() { Id = "base-15", Title = "Admin MFA Disabled or Modified", Category = "identity", Severity = "critical", RuleType = "NativeM365", Description = "Detects any modification or bypass exception added to Conditional Access MFA rules.", NativePortalBlade = "Microsoft Entra ID — Audit Logs", NativePortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/AuditLogs" },
        new() { Id = "base-16", Title = "Emergency Break-Glass Account Sign-In", Category = "identity", Severity = "critical", RuleType = "NativeM365", Description = "Alerts immediately if designated break-glass emergency administrative account authenticates.", NativePortalBlade = "Microsoft Entra ID — Sign-in Logs", NativePortalDeepLink = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/SignIns" },
        new() { Id = "base-17", Title = "Bulk DLP Policy Violation", Category = "data protection", Severity = "high", RuleType = "NativeM365", Description = "Triggers when sensitive credit card or PII data exfiltration attempts exceed threshold.", NativePortalBlade = "Microsoft Purview — DLP", NativePortalDeepLink = "https://purview.microsoft.com/dataloss-prevention" },
        new() { Id = "base-18", Title = "Unusual External OneDrive Oversharing", Category = "data protection", Severity = "medium", RuleType = "NativeM365", Description = "Monitors spikes in anonymous guest links generated across SharePoint document libraries.", NativePortalBlade = "SharePoint Admin Center", NativePortalDeepLink = "https://admin.microsoft.com/sharepoint" },
        new() { Id = "base-19", Title = "Intune Compliance Policy Modifications", Category = "devices", Severity = "high", RuleType = "NativeM365", Description = "Alerts on unauthorized weakening of device password or BitLocker encryption requirements.", NativePortalBlade = "Microsoft Intune — Audit Logs", NativePortalDeepLink = "https://intune.microsoft.com/#view/Microsoft_Intune_DeviceSettings/TenantAdminMenu/~/auditLogs" },
        new() { Id = "base-20", Title = "Exchange Online High-Volume Outbound Spam", Category = "email", Severity = "high", RuleType = "NativeM365", Description = "Detects compromised internal employee mailboxes sending outbound bulk spam.", NativePortalBlade = "Microsoft Defender Portal — Antispam", NativePortalDeepLink = "https://security.microsoft.com/antispam" }
    ];

    public static async Task<AlertCoverageScorecard> GetAlertCoverageAsync(AppDbContext db, CancellationToken ct = default)
    {
        var existingPolicies = await db.AlertPolicies.AsNoTracking().ToListAsync(ct);
        var rules = new List<AlertBaselineRule>();

        foreach (var rule in BaselineCatalog)
        {
            var clone = new AlertBaselineRule
            {
                Id = rule.Id,
                Title = rule.Title,
                Category = rule.Category,
                Severity = rule.Severity,
                Description = rule.Description,
                RuleType = rule.RuleType,
                Metric = rule.Metric,
                DefaultThreshold = rule.DefaultThreshold,
                NativePortalBlade = rule.NativePortalBlade,
                NativePortalDeepLink = rule.NativePortalDeepLink
            };

            if (clone.RuleType == "Vigil365")
            {
                clone.IsActive = existingPolicies.Any(p => p.Name.Equals(clone.Title, StringComparison.OrdinalIgnoreCase) && p.Enabled);
            }
            else
            {
                // Native rules default to monitored assuming tenant has baseline Defender enabled
                clone.IsActive = true;
            }

            rules.Add(clone);
        }

        var activeCount = rules.Count(r => r.IsActive);
        var total = rules.Count;
        var pct = total > 0 ? (int)Math.Round((double)activeCount / total * 100) : 0;

        return new AlertCoverageScorecard
        {
            TotalRules = total,
            ActiveRules = activeCount,
            CoveragePercentage = pct,
            Rules = rules
        };
    }

    public static async Task<AlertPolicy?> EnableCoverageRuleAsync(AppDbContext db, string ruleId, CancellationToken ct = default)
    {
        var target = BaselineCatalog.FirstOrDefault(r => r.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        if (target == null || target.RuleType != "Vigil365") return null;

        var existing = await db.AlertPolicies.FirstOrDefaultAsync(p => p.Name.Equals(target.Title, StringComparison.OrdinalIgnoreCase), ct);
        if (existing != null)
        {
            existing.Enabled = true;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var newPolicy = new AlertPolicy
        {
            Id = Guid.NewGuid(),
            Name = target.Title,
            Enabled = true,
            Category = target.Category,
            Metric = target.Metric,
            Threshold = target.DefaultThreshold,
            Severity = target.Severity,
            Condition = target.Description,
            SuppressionMinutes = 60,
            CreatedAt = DateTimeOffset.UtcNow,
            TriggerCount = 0
        };

        db.AlertPolicies.Add(newPolicy);
        await db.SaveChangesAsync(ct);
        return newPolicy;
    }
}
