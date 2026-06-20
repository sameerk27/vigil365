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

        if (cfg.TeamsEnabled && !string.IsNullOrWhiteSpace(cfg.TeamsWebhookUrl))
            await SendTeamsAsync(db, cfg.TeamsWebhookUrl!, alert, ct);

        if (cfg.WebhookEnabled && !string.IsNullOrWhiteSpace(cfg.WebhookUrl))
            await SendWebhookAsync(db, cfg.WebhookUrl!, alert, ct);

        if (cfg.EmailEnabled && !string.IsNullOrWhiteSpace(cfg.SmtpHost))
        {
            var to = alert.Status == "new"
                ? (FirstNonEmpty(cfg.DefaultRecipient) ?? cfg.FromAddress)
                : cfg.DefaultRecipient;
            if (!string.IsNullOrWhiteSpace(to))
                await SendEmailAsync(db, cfg, to!, alert, ct);
        }
    }

    private static string? FirstNonEmpty(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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
        // MessageCard format — works for both Teams incoming webhooks and (loosely) Slack.
        var card = new
        {
            @type = "MessageCard",
            @context = "http://schema.org/extensions",
            themeColor = SevColor(a.Severity),
            summary = $"Vigil365 alert: {a.PolicyName}",
            title = $"🛡️ {a.PolicyName}",
            sections = new[]
            {
                new
                {
                    activityTitle = $"Severity: {a.Severity.ToUpperInvariant()}",
                    facts = new[]
                    {
                        new { name = "Condition", value = a.Condition },
                        new { name = "Observed value", value = a.MetricValue.ToString() },
                        new { name = "Threshold", value = a.Threshold.ToString() },
                        new { name = "Category", value = a.Category },
                        new { name = "Triggered", value = a.TriggeredAt.ToString("u") },
                    },
                    markdown = true,
                },
            },
        };
        await PostJsonAsync(db, "teams", url, JsonSerializer.Serialize(card), a, ct);
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

    private async Task SendEmailAsync(AppDbContext db, NotificationSettings cfg, string to, TriggeredAlert a, CancellationToken ct)
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
                    : new NetworkCredential(cfg.SmtpUsername, cfg.SmtpPassword),
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
