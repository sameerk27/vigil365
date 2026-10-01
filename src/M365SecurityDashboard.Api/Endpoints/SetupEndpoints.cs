using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>First-run setup: onboarding checklist, Graph permission reference, and the admin-only Graph credential wizard.</summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app)
    {
        // First-run / setup progress. Drives the onboarding checklist so a fresh install
        // gets "do these things" instead of a dashboard full of empty cards. Analyst-
        // readable; every signal comes from state the app already persists.
        app.MapGet("/api/setup/status", async (AppDbContext db, TenantGraphCredentials graphCreds, CancellationToken ct) =>
        {
            var graphConfigured = await graphCreds.IsConfiguredAsync(ct);

            var lastRun = await db.CollectionRuns.AsNoTracking()
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(ct);
            var hasCollected = lastRun is { Status: CollectionStatus.Completed };

            // A permission gap shows up as a source failure on an otherwise-complete run.
            var permissionGaps = lastRun?.SourceFailures ?? 0;

            var cfg = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var notificationsConfigured =
                (cfg?.EmailEnabled == true && !string.IsNullOrWhiteSpace(cfg.SmtpHost)) ||
                (cfg?.TeamsEnabled == true) ||
                (cfg?.WebhookEnabled == true);

            var policyCount = await db.AlertPolicies.CountAsync(p => p.Enabled, ct);
            var userCount = await db.AppUsers.CountAsync(ct);

            var steps = new[]
            {
                new { key = "graph", label = "Connect Microsoft Graph", done = graphConfigured,
                      hint = "Complete the setup wizard with your tenant and app registration.", page = "setup" },
                new { key = "collection", label = "Run the first collection", done = hasCollected,
                      hint = "Collect alerts from your tenant so the dashboard has data.", page = "overview" },
                new { key = "permissions", label = "Grant all required permissions", done = graphConfigured && permissionGaps == 0,
                      hint = "One or more Graph sources were denied. Open Collection Runs to see which permission is missing.", page = "alertcenter" },
                new { key = "notifications", label = "Set up a notification channel", done = notificationsConfigured,
                      hint = "Add email (SMTP), Teams, or a webhook so alerts reach you.", page = "alertcenter" },
                new { key = "policies", label = "Enable alert policies", done = policyCount > 0,
                      hint = "Enable at least one alert policy so Vigil365 raises alerts.", page = "alertcenter" },
                new { key = "users", label = "Invite your team", done = userCount > 1,
                      hint = "Add analysts and viewers so you are not the only account.", page = "users" },
            };

            return Results.Ok(new
            {
                complete = steps.All(s => s.done),
                completedCount = steps.Count(s => s.done),
                totalCount = steps.Length,
                steps,
            });
        }).RequireAuthorization("RequireAnalyst");

        // Live permissions reference: every collector source, its required Graph
        // application permission, and whether the last run could actually read it.
        // Turns "which permission do I need?" into a page instead of a support ticket.
        app.MapGet("/api/setup/permissions", async (AppDbContext db, CancellationToken ct) =>
        {
            var lastRun = await db.CollectionRuns.AsNoTracking()
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(ct);

            // Sources that failed on the most recent run — most commonly a permission gap.
            var failedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (lastRun?.SourceFailureDetails is { } details)
            {
                try
                {
                    foreach (var f in JsonSerializer.Deserialize<List<Dictionary<string, string>>>(details) ?? [])
                        if (f.TryGetValue("source", out var s)) failedSources.Add(s);
                }
                catch { /* malformed detail — treat as no known failures */ }
            }

            var items = GraphErrorHint.AllRequirements()
                .GroupBy(r => r.Permission)
                .Select(g => new
                {
                    permission = g.Key,
                    features = g.Select(r => r.Source).OrderBy(s => s).ToArray(),
                    // "granted" is a best-effort inference from the last run: a source that
                    // failed is almost certainly missing its permission; one that ran is fine.
                    // Null when there is no run yet to judge from.
                    status = lastRun is null ? "unknown"
                        : g.Any(r => failedSources.Contains(r.Source)) ? "missing" : "granted",
                })
                .OrderBy(x => x.permission)
                .ToList();

            return Results.Ok(new { hasRun = lastRun is not null, permissions = items });
        }).RequireAuthorization("RequireAnalyst");

        // ── First-run setup wizard (Admin only) ──────────────────────────────────────────
        // Lets an Admin enter Graph credentials in the browser instead of editing JSON.
        // Current config status + non-secret values (never returns the secret).
        app.MapGet("/api/setup/graph", (IOptions<GraphOptions> opts) =>
        {
            var o = opts.Value;
            return Results.Ok(new
            {
                configured = o.IsConfigured(),
                tenantId = o.IsConfigured() ? o.TenantId : "",
                clientId = o.IsConfigured() ? o.ClientId : "",
                hasSecret = o.HasSecret(),
                hasCertificate = o.HasCertificate(),
                loginInstance = o.LoginInstance,
                baseUrl = o.BaseUrl,
                // Certificate wins when both are present — mirrors GraphApiClient.BuildCredential.
                authMode = o.HasCertificate() ? "certificate" : o.HasSecret() ? "secret" : "none",
            });
        }).RequireAuthorization("RequireAdmin");

        // Save + apply Graph credentials, then test the connection. Persists encrypted and
        // mutates the live GraphOptions singleton so collection works without a restart.
        app.MapPost("/api/setup/graph", async (
            GraphSetupRequest input, AppDbContext db, SecretProtector protector, AuditLogger audit,
            IOptions<GraphOptions> opts, IServiceProvider services, CancellationToken ct) =>
        {
            var tenantId = (input.TenantId ?? "").Trim();
            var clientId = (input.ClientId ?? "").Trim();
            var clientSecret = (input.ClientSecret ?? "").Trim();
            var loginInstance = (input.LoginInstance ?? "").Trim();
            var baseUrl = (input.BaseUrl ?? "").Trim();
            if (tenantId == "" || clientId == "")
                return Results.BadRequest(new { error = "Tenant ID and Client ID are required." });

            // Single-row table, so match on "the row" rather than on Id == 1: the
            // Id is store-generated, and assigning it made EF send it in the
            // INSERT, which an identity column rejects.
            var row = await db.GraphConfig.OrderBy(g => g.Id).FirstOrDefaultAsync(ct);
            if (row is null) { row = new GraphConfig(); db.GraphConfig.Add(row); }
            row.TenantId = tenantId;
            row.ClientId = clientId;
            // Keep the existing secret if the field was left blank (e.g. editing tenant only).
            if (clientSecret != "") row.ClientSecret = protector.Protect(clientSecret);
            if (loginInstance != "") row.LoginInstance = loginInstance;
            if (baseUrl != "") row.BaseUrl = baseUrl;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Apply over the live singleton so new GraphApiClient instances use it immediately.
            var o = opts.Value;
            o.TenantId = tenantId;
            o.ClientId = clientId;
            if (clientSecret != "") o.ClientSecret = clientSecret;
            if (loginInstance != "") o.LoginInstance = loginInstance;
            if (baseUrl != "") o.BaseUrl = baseUrl;

            await audit.WriteAsync("setup.graph", "settings", "graph", "Graph credentials updated", ct);

            // Test the connection with a fresh client (reads the just-mutated options).
            string? testError = null;
            try
            {
                var graph = services.GetRequiredService<GraphApiClient>();
                await graph.GetSinglePageAsync("/v1.0/organization", ct);
            }
            catch (Exception ex) { testError = ex.Message; }

            return Results.Ok(new { saved = true, testOk = testError is null, testError });
        }).RequireAuthorization("RequireAdmin");

        // Auto-create the multi-tenant MSP app registration (Entra) via the Azure CLI
        // and store it as the install-wide Graph credentials, so an MSP never has to
        // hand-register an app in the portal. Runs register-app.ps1 -MultiTenant -Json
        // as the signed-in operator's az session. Admin-only, audited, and defensive:
        // it does nothing if the CLI or the script is absent, and never logs the secret.
        app.MapPost("/api/setup/register-msp-app", async (
            HttpContext ctx, AppDbContext db, SecretProtector protector, AuditLogger audit,
            IOptions<GraphOptions> opts, IConfiguration config, IWebHostEnvironment env,
            MspAppRegisterRequest? input, CancellationToken ct) =>
        {
            var script = ResolveRegisterScript(env.ContentRootPath);
            if (script is null)
                return Results.BadRequest(new { ok = false, message = "register-app.ps1 was not found next to the app. This automated registration is available from a source/dev checkout; on a packaged install, register the multi-tenant app with register-app.ps1 -MultiTenant and paste its client id/secret in Setup." });

            var baseUrl = (input?.RedirectUri ?? config["Auth:RedirectUri"]
                ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
            var displayName = string.IsNullOrWhiteSpace(input?.DisplayName) ? "Vigil365 MSP" : input!.DisplayName!.Trim();

            var (exitCode, stdout, stderr) = await RunPwshAsync(script,
                ["-MultiTenant", "-Json", "-RedirectUri", baseUrl, "-DisplayName", displayName], ct);

            if (exitCode != 0)
            {
                var hint = (stderr + stdout).Contains("az login", StringComparison.OrdinalIgnoreCase) || (stderr + stdout).Contains("not signed in", StringComparison.OrdinalIgnoreCase)
                    ? "Run 'az login' in a terminal on this machine as a user who can create app registrations, then try again."
                    : (stderr + stdout).Contains("not found", StringComparison.OrdinalIgnoreCase)
                        ? "Azure CLI (az) was not found. Install it, run 'az login', then try again."
                        : null;
                return Results.BadRequest(new { ok = false, message = "App registration failed.", detail = Tail(stderr.Length > 0 ? stderr : stdout, 600), hint });
            }

            var jsonLine = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{") && l.Contains("clientId"));
            if (jsonLine is null)
                return Results.BadRequest(new { ok = false, message = "The registration script did not return the expected result.", detail = Tail(stdout, 600) });

            MspAppResult? parsed;
            try { parsed = JsonSerializer.Deserialize<MspAppResult>(jsonLine, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return Results.BadRequest(new { ok = false, message = "Could not parse the registration result." }); }
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.ClientId) || string.IsNullOrWhiteSpace(parsed.TenantId))
                return Results.BadRequest(new { ok = false, message = "The registration result was incomplete." });

            var row = await db.GraphConfig.OrderBy(g => g.Id).FirstOrDefaultAsync(ct);
            if (row is null) { row = new GraphConfig(); db.GraphConfig.Add(row); }
            row.TenantId = parsed.TenantId;
            row.ClientId = parsed.ClientId;
            if (!string.IsNullOrWhiteSpace(parsed.ClientSecret)) row.ClientSecret = protector.Protect(parsed.ClientSecret);
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var o = opts.Value; // apply live so consent-url uses it immediately
            o.TenantId = parsed.TenantId; o.ClientId = parsed.ClientId;
            if (!string.IsNullOrWhiteSpace(parsed.ClientSecret)) o.ClientSecret = parsed.ClientSecret!;

            await audit.WriteAsync("setup.register_msp_app", "settings", "graph",
                $"multi-tenant MSP app {parsed.ClientId} registered in tenant {parsed.TenantId}", ct);
            return Results.Ok(new { ok = true, clientId = parsed.ClientId, tenantId = parsed.TenantId, redirectUri = $"{baseUrl}/consented" });
        }).RequireAuthorization("RequireAdmin");
    }

    /// <summary>register-app.ps1 lives at the repo root in a dev checkout; look up from ContentRoot.</summary>
    private static string? ResolveRegisterScript(string contentRoot)
    {
        var dir = new DirectoryInfo(contentRoot);
        for (var i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "register-app.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPwshAsync(string script, string[] scriptArgs, CancellationToken ct)
    {
        foreach (var shell in new[] { "pwsh", "powershell" })
        {
            var psi = new System.Diagnostics.ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", script }) psi.ArgumentList.Add(a);
            foreach (var a in scriptArgs) psi.ArgumentList.Add(a);

            System.Diagnostics.Process? p;
            try { p = System.Diagnostics.Process.Start(psi); }
            catch { continue; } // this shell isn't on PATH — try the next
            if (p is null) continue;

            using var _ = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token).Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return (-1, await outTask, "Registration timed out."); }
            return (p.ExitCode, await outTask, await errTask);
        }
        return (-1, "", "PowerShell (pwsh/powershell) was not found to run the registration script.");
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private sealed record MspAppResult(string? TenantId, string? ClientId, string? ClientSecret, string? RedirectUri, bool MultiTenant);
}
