using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Delivers triggered-alert notifications over Microsoft Teams / Slack incoming
/// webhooks, SMTP email, and a generic JSON webhook. Every attempt is logged.
/// </summary>
public sealed class NotificationSender(
    IHttpClientFactory httpFactory,
    SecretProtector protector,
    ILogger<NotificationSender> logger)
{
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["informational"] = 0, ["low"] = 1, ["medium"] = 2, ["high"] = 3, ["critical"] = 4,
    };

    private static int Rank(string? sev) => SeverityRank.TryGetValue(sev ?? "low", out var r) ? r : 1;

    /// <summary>Dispatch all configured channels for a single triggered alert.</summary>
    public async Task DispatchAsync(AppDbContext db, NotificationSettings cfg, TriggeredAlert alert, CancellationToken ct)
    {
        if (Rank(alert.Severity) < Rank(cfg.MinSeverity))
            return; // below the configured minimum severity — skip

        // Sensitive fields are stored DPAPI-encrypted at rest — decrypt for use only.
        var teamsUrl = protector.Unprotect(cfg.TeamsWebhookUrl);
        var webhookUrl = protector.Unprotect(cfg.WebhookUrl);
        var smtpPassword = protector.Unprotect(cfg.SmtpPassword);

        if (cfg.TeamsEnabled && !string.IsNullOrWhiteSpace(teamsUrl))
            await SendTeamsAsync(db, teamsUrl!, alert, ct);

        if (cfg.WebhookEnabled && !string.IsNullOrWhiteSpace(webhookUrl))
            await SendWebhookAsync(db, webhookUrl!, alert, ct);

        if (cfg.EmailEnabled && !string.IsNullOrWhiteSpace(cfg.SmtpHost))
        {
            var to = alert.Status == "new"
                ? (FirstNonEmpty(cfg.DefaultRecipient) ?? cfg.FromAddress)
                : cfg.DefaultRecipient;
            if (!string.IsNullOrWhiteSpace(to))
                await SendEmailAsync(db, cfg, smtpPassword, to!, alert, ct);
        }
    }

    private static string? FirstNonEmpty(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Sends a one-off access-notification ("invite") email to a pre-provisioned user,
    /// reusing the configured SMTP settings. These are internal tenant users who already
    /// have Microsoft accounts — this is a courtesy notice + sign-in link, not an account
    /// creation. Returns (ok, error) rather than writing a NotificationLog row (those are
    /// keyed to triggered alerts).
    /// </summary>
    public async Task<(bool ok, string? error)> SendInviteEmailAsync(
        NotificationSettings cfg, string toEmail, string role, string dashboardUrl, CancellationToken ct)
    {
        if (!cfg.EmailEnabled || string.IsNullOrWhiteSpace(cfg.SmtpHost))
            return (false, "SMTP email is not configured. Set it up in Settings → Notifications first.");

        var smtpPassword = protector.Unprotect(cfg.SmtpPassword);
        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(cfg.FromAddress ?? cfg.SmtpUsername ?? "vigil365@localhost"),
                Subject = "You've been granted access to Vigil365",
                IsBodyHtml = true,
                Body = $"""
                    <div style="font-family:Segoe UI,Arial,sans-serif;max-width:480px">
                      <h2 style="color:#2563eb;margin:0 0 8px">Vigil365 — Access Granted</h2>
                      <p style="margin:0 0 12px;color:#475569">
                        You've been granted <b>{WebUtility.HtmlEncode(role)}</b> access to the
                        Vigil365 Microsoft 365 security dashboard.
                      </p>
                      <p style="margin:0 0 16px;color:#475569">
                        Sign in with your Microsoft 365 account to get started:
                      </p>
                      <p style="margin:0 0 20px">
                        <a href="{WebUtility.HtmlEncode(dashboardUrl)}"
                           style="display:inline-block;background:#2563eb;color:#fff;text-decoration:none;
                                  padding:10px 20px;border-radius:8px;font-weight:600">Open Vigil365</a>
                      </p>
                      <p style="margin:0;color:#94a3b8;font-size:12px">
                        Access is restricted to your organisation. If you didn't expect this, you can ignore this email.
                      </p>
                    </div>
                    """,
            };
            msg.To.Add(toEmail);

            using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
            {
                EnableSsl = cfg.SmtpUseSsl,
                Credentials = string.IsNullOrWhiteSpace(cfg.SmtpUsername)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(cfg.SmtpUsername, smtpPassword),
            };
            await client.SendMailAsync(msg, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invite email to {Email} failed", toEmail);
            return (false, ex.Message);
        }
    }

    private static string SevColor(string sev) => sev?.ToLowerInvariant() switch
    {
        "critical" => "dc2626",
        "high" => "ea580c",
        "medium" => "d97706",
        "low" => "2563eb",
        _ => "6b7280",
    };

    private async Task SendTeamsAsync(AppDbContext db, string url, TriggeredAlert a, CancellationToken ct)
    {
        var cardColor = a.Severity.ToLowerInvariant() switch
        {
            "critical" or "high" => "Attention",
            "medium" => "Warning",
            "low" => "Accent",
            _ => "Default"
        };

        // Modern Adaptive Card structure compatible with Teams Workflows / Incoming Webhooks
        var cardPayload = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = $"🛡️ Vigil365: {a.PolicyName}",
                                weight = "Bolder",
                                size = "Medium",
                                color = cardColor
                            },
                            new
                            {
                                type = "FactSet",
                                facts = new[]
                                {
                                    new { title = "Severity", value = a.Severity.ToUpperInvariant() },
                                    new { title = "Condition", value = a.Condition },
                                    new { title = "Observed Value", value = a.MetricValue.ToString() },
                                    new { title = "Threshold", value = a.Threshold.ToString() },
                                    new { title = "Category", value = a.Category },
                                    new { title = "Triggered At", value = a.TriggeredAt.ToString("u") }
                                }
                            }
                        }
                    }
                }
            }
        };

        await PostJsonAsync(db, "teams", url, JsonSerializer.Serialize(cardPayload), a, ct);
    }

    private async Task SendWebhookAsync(AppDbContext db, string url, TriggeredAlert a, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            source = "Vigil365",
            id = a.Id,
            policyName = a.PolicyName,
            severity = a.Severity,
            category = a.Category,
            condition = a.Condition,
            metricValue = a.MetricValue,
            threshold = a.Threshold,
            triggeredAt = a.TriggeredAt,
            status = a.Status,
        });
        await PostJsonAsync(db, "webhook", url, payload, a, ct);
    }

    private async Task PostJsonAsync(AppDbContext db, string channel, string url, string json, TriggeredAlert a, CancellationToken ct)
    {
        var log = new NotificationLog { TriggeredAlertId = a.Id, PolicyName = a.PolicyName, Channel = channel, Target = Truncate(url, 120) };
        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url, content, ct);
            log.Success = resp.IsSuccessStatusCode;
            if (!resp.IsSuccessStatusCode)
                log.Error = Truncate($"{(int)resp.StatusCode} {resp.ReasonPhrase}", 1000);
        }
        catch (Exception ex)
        {
            log.Success = false;
            log.Error = Truncate(ex.Message, 1000);
            logger.LogWarning(ex, "Notification {Channel} failed for policy {Policy}", channel, a.PolicyName);
        }
        db.NotificationLogs.Add(log);
    }

    private async Task SendEmailAsync(AppDbContext db, NotificationSettings cfg, string? smtpPassword, string to, TriggeredAlert a, CancellationToken ct)
    {
        var log = new NotificationLog { TriggeredAlertId = a.Id, PolicyName = a.PolicyName, Channel = "email", Target = Truncate(to, 120) };
        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(cfg.FromAddress ?? cfg.SmtpUsername ?? "vigil365@localhost"),
                Subject = $"[Vigil365] {a.Severity.ToUpperInvariant()} — {a.PolicyName}",
                IsBodyHtml = true,
                Body = $"""
                    <div style="font-family:Segoe UI,Arial,sans-serif">
                      <h2 style="color:#{SevColor(a.Severity)};margin:0 0 8px">{WebUtility.HtmlEncode(a.PolicyName)}</h2>
                      <p style="margin:0 0 12px;color:#475569">{WebUtility.HtmlEncode(a.Condition)}</p>
                      <table style="border-collapse:collapse;font-size:14px">
                        <tr><td style="padding:4px 12px 4px 0;color:#64748b">Severity</td><td><b>{a.Severity}</b></td></tr>
                        <tr><td style="padding:4px 12px 4px 0;color:#64748b">Observed</td><td>{a.MetricValue}</td></tr>
                        <tr><td style="padding:4px 12px 4px 0;color:#64748b">Threshold</td><td>{a.Threshold}</td></tr>
                        <tr><td style="padding:4px 12px 4px 0;color:#64748b">Category</td><td>{a.Category}</td></tr>
                        <tr><td style="padding:4px 12px 4px 0;color:#64748b">Triggered</td><td>{a.TriggeredAt:u}</td></tr>
                      </table>
                    </div>
                    """,
            };
            msg.To.Add(to);

            using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
            {
                EnableSsl = cfg.SmtpUseSsl,
                Credentials = string.IsNullOrWhiteSpace(cfg.SmtpUsername)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(cfg.SmtpUsername, smtpPassword),
            };
            await client.SendMailAsync(msg, ct);
            log.Success = true;
        }
        catch (Exception ex)
        {
            log.Success = false;
            log.Error = Truncate(ex.Message, 1000);
            logger.LogWarning(ex, "Email notification failed for policy {Policy}", a.PolicyName);
        }
        db.NotificationLogs.Add(log);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
