using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// JSON logs preserve correlation IDs and structured fields for Docker,
// journald, Splunk, or Sentinel. Files roll daily and at a size limit so logs
// remain useful without consuming the host disk indefinitely.
var configuredLogPath = builder.Configuration["Logging:File:Path"] ?? "logs/vigil365-.json";
var logPath = Path.GetFullPath(configuredLogPath, AppContext.BaseDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
var retainedLogFiles = Math.Max(1, builder.Configuration.GetValue("Logging:File:RetainedFileCountLimit", 14));
var maxLogFileBytes = Math.Max(1_048_576, builder.Configuration.GetValue("Logging:File:FileSizeLimitBytes", 10 * 1024 * 1024));

builder.Host.UseSerilog((context, _, logger) => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Vigil365")
    .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteTo.File(new RenderedCompactJsonFormatter(), logPath,
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: maxLogFileBytes,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: retainedLogFiles,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1)));
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("Graph"));
builder.Services.Configure<AlertingOptions>(builder.Configuration.GetSection("Alerting"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));

// ── Authentication & Authorization ──────────────────────────────────────────────
// Validates Entra ID Bearer tokens. The SPA (MSAL) acquires a token for the
// scope api://{clientId}/access_as_user, so the token audience is api://{clientId}.
// AzureAd:Audience in config must match that, or validation fails with 401.
// Role claims ("Admin"/"Analyst"/"Viewer") come from Entra ID App Roles.
builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration, "AzureAd");
// Attaches each user's in-app role (from AppUsers table) as a role claim after
// token validation. Scoped so it can use the request-scoped AppDbContext.
// Roles are memory-cached (short TTL) so hot paths skip the per-request DB lookup.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddAuthorization(options =>
{
    // Role claims come from RoleClaimsTransformation (AppUsers table). Analyst
    // actions are also allowed for Admins. Viewer needs no policy — the fallback
    // (any authenticated user) covers read access.
    options.AddPolicy("RequireAdmin", p => p.RequireAuthenticatedUser().RequireRole(AppRoles.Admin));
    options.AddPolicy("RequireAnalyst", p => p.RequireAuthenticatedUser().RequireRole(AppRoles.Admin, AppRoles.Analyst));
    // Deny-by-default: every endpoint requires a validated token unless it opts
    // out with AllowAnonymous (/health, /api/auth/config, SPA fallback).
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddHttpClient<GraphApiClient>();
builder.Services.AddHttpClient();

// Cross-platform secret encryption key ring. Persisted to disk so secrets survive
// restarts; in Docker, mount DataProtection:KeyPath as a volume.
var keyPath = builder.Configuration["DataProtection:KeyPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "keys");
Directory.CreateDirectory(keyPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .SetApplicationName("Vigil365");
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<GraphCollector>();
builder.Services.AddScoped<NotificationSender>();
builder.Services.AddScoped<DigestBuilder>();
builder.Services.AddSingleton<DigestPdfRenderer>();
builder.Services.AddScoped<EntityProfileBuilder>();
builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<PolicyBacktester>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddHostedService<GraphCollectionWorker>();
builder.Services.AddHostedService<DataRetentionWorker>();
builder.Services.AddHostedService<ReportScheduleWorker>();
builder.Services.AddHostedService<NotificationDigestWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
if (builder.Environment.IsDevelopment()) builder.Services.AddSwaggerGen();
// CORS origins are config-driven so real deployments (custom hostnames, reverse
// proxies) work without a rebuild; localhost defaults cover dev out of the box.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5000", "http://localhost:5173"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS"));
});

// Basic abuse protection: per-client fixed-window limiter on the API. Generous
// enough for the SPA's parallel dashboard fan-out, tight enough to blunt scraping
// or brute-force attempts. 429s include Retry-After via the default handler.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Versioned schema via EF migrations. Wait for the database server — in
    // Docker the SQL container may still be starting. Retry for up to ~60s.
    //
    // Installs created before migrations existed (EnsureCreated + raw DDL) are
    // BASELINED: their schema is first brought fully up to the current model by
    // the idempotent legacy DDL, then InitialCreate is recorded as applied
    // without running. Newer migrations then apply normally on every start.
    var dbLog = app.Services.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            if (db.Database.CanConnect() && !db.Database.GetAppliedMigrations().Any())
            {
                var isLegacyDb = db.Database
                    .SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'[SecurityAlerts]', N'U') IS NOT NULL THEN 1 ELSE 0 END AS [Value]")
                    .AsEnumerable().First() == 1;
                if (isLegacyDb)
                {
                    // Older installs may be missing later idempotent patches —
                    // apply them all so the DB matches the model we baseline to.
                    db.Database.ExecuteSqlRaw(AlertingSchema.EnsureTablesSql);
                    var baseline = db.Database.GetMigrations().First();
                    db.Database.ExecuteSqlRaw("""
                        IF OBJECT_ID(N'[__EFMigrationsHistory]', N'U') IS NULL
                        CREATE TABLE [__EFMigrationsHistory] (
                            [MigrationId] nvarchar(150) NOT NULL CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY,
                            [ProductVersion] nvarchar(32) NOT NULL);
                        """);
                    db.Database.ExecuteSql($"""
                        IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {baseline})
                        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({baseline}, '8.0.11');
                        """);
                    dbLog.LogInformation("Baselined pre-migration database at {Migration}.", baseline);
                }
            }
            db.Database.Migrate();
            break;
        }
        catch (Exception ex) when (attempt < 30)
        {
            dbLog.LogWarning("Database not ready (attempt {Attempt}): {Message}. Retrying in 2s…", attempt, ex.Message);
            Thread.Sleep(2000);
        }
    }

    AlertingSchema.SeedDefaultPolicies(db);

    // Apply Graph credentials saved via the setup wizard over the GraphOptions
    // singleton. Because IOptions<GraphOptions>.Value is a singleton, mutating it
    // here makes every consumer (and IsConfigured()) see the wizard-entered values
    // without any config file. DB values win over appsettings when present.
    // Loaded BEFORE any demo seeding so a configured install never gets sample data.
    var graphOpts = scope.ServiceProvider.GetRequiredService<IOptions<GraphOptions>>().Value;
    var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
    var saved = db.GraphConfig.FirstOrDefault(g => g.Id == 1);
    if (saved is not null && !string.IsNullOrWhiteSpace(saved.TenantId))
    {
        graphOpts.TenantId = saved.TenantId;
        graphOpts.ClientId = saved.ClientId;
        var secret = protector.Unprotect(saved.ClientSecret);
        if (!string.IsNullOrWhiteSpace(secret)) graphOpts.ClientSecret = secret;
    }

    if (graphOpts.IsConfigured())
    {
        // One-time cleanup: purge demo/sample alerts (identified by the seed
        // ExternalId prefixes) so they never commingle with real tenant data.
        // Installs that seeded before configuring Graph carry these forever
        // otherwise — the collector never matches their ExternalIds.
        var purged = db.SecurityAlerts
            .Where(a => a.ExternalId != null && (
                a.ExternalId.StartsWith("def-crit-") || a.ExternalId.StartsWith("def-high-") ||
                a.ExternalId.StartsWith("def-med-") || a.ExternalId.StartsWith("entra-risk-") ||
                a.ExternalId.StartsWith("entra-signin-") || a.ExternalId.StartsWith("intune-nc-") ||
                a.ExternalId.StartsWith("intune-nia-") || a.ExternalId.StartsWith("mfa-ok-") ||
                a.ExternalId.StartsWith("mfa-miss-")))
            .ExecuteDelete();
        if (purged > 0)
            dbLog.LogInformation("Purged {Count} demo/sample alerts now that Graph is configured.", purged);
    }
    else if (builder.Configuration.GetValue("Seed:DemoData", false) && !db.SecurityAlerts.Any())
    {
        db.CollectionRuns.Add(new CollectionRun
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-14),
            Status = CollectionStatus.Completed,
            AlertsUpserted = 14
        });

        // Defender XDR Alerts
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-crit-1", AlertType = "ImpossibleTravel", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Critical, Title = "Impossible travel detected for executive user", Description = "User signed in from two distant geographical locations within 45 minutes.", UserPrincipalName = "sarah.connor@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-1), LastUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-crit-2", AlertType = "SuspiciousExecution", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Critical, Title = "Suspicious PowerShell command execution detected", Description = "Encoded command executed to dump process memory.", DeviceName = "SEC-WORKSTATION-04", DetectedAt = DateTimeOffset.UtcNow.AddHours(-2), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-crit-3", AlertType = "DataExfiltration", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Critical, Title = "Mass SharePoint file exfiltration observed", Description = "Over 1,500 sensitive files downloaded by user in 10 minutes.", UserPrincipalName = "alexw@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-3), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-2) });
        
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-high-1", AlertType = "MailboxPersistence", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.High, Title = "Malicious inbox forwarding rule created", Description = "Rule created to forward incoming finance emails to external domain.", UserPrincipalName = "finance.lead@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-4), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-3) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-high-2", AlertType = "PhishingCampaign", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.High, Title = "Credential harvesting phishing campaign blocked", Description = "Multiple inbound phishing messages intercepted by Defender for Office 365.", DetectedAt = DateTimeOffset.UtcNow.AddHours(-5), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-4) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-high-3", AlertType = "AnomalousGrant", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.High, Title = "Anomalous OAuth app consent grant", Description = "User granted Mail.Read permissions to unverified multi-tenant application.", UserPrincipalName = "john.doe@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-6), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-5) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-high-4", AlertType = "BruteForce", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.High, Title = "Potential password spray attack against tenant", Description = "Over 300 failed login attempts across 45 user accounts from single AS.", DetectedAt = DateTimeOffset.UtcNow.AddHours(-8), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-7) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-high-5", AlertType = "UnfamiliarSignIn", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.High, Title = "Sign-in from unfamiliar properties", Description = "First time sign-in from new OS and ISP.", UserPrincipalName = "jane.smith@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-9), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-8) });

        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-med-1", AlertType = "SuspiciousExtension", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Medium, Title = "Suspicious browser extension installed", DeviceName = "DEV-LAPTOP-12", DetectedAt = DateTimeOffset.UtcNow.AddHours(-10), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-9) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-med-2", AlertType = "LegacyAuth", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Medium, Title = "Legacy authentication protocol detected", UserPrincipalName = "old.svc@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-11), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-10) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-med-3", AlertType = "NetworkScan", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Medium, Title = "Internal port scanning activity detected", DeviceName = "FIN-PC-09", DetectedAt = DateTimeOffset.UtcNow.AddHours(-12), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-11) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "def-med-4", AlertType = "AutoInvestigate", Service = M365ServiceArea.DefenderXdr, Severity = AlertSeverity.Medium, Title = "Automated investigation pending approval", DeviceName = "HR-TABLET-03", DetectedAt = DateTimeOffset.UtcNow.AddHours(-14), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-13) });

        // Entra ID Alerts
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "entra-risk-1", AlertType = "RiskyUser", Service = M365ServiceArea.EntraId, Severity = AlertSeverity.High, Title = "Risky user detected: Leaked credentials", UserPrincipalName = "sarah.connor@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-2), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "entra-signin-1", AlertType = "RiskySignIn", Service = M365ServiceArea.EntraId, Severity = AlertSeverity.Medium, Title = "Sign-in from anonymous VPN proxy", UserPrincipalName = "alexw@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-3), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-2) });

        // Intune Alerts
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "intune-nc-1", AlertType = "NonCompliantDevice", Service = M365ServiceArea.Intune, Severity = AlertSeverity.Medium, Title = "Non-compliant device: BitLocker encryption inactive", DeviceName = "DEV-LAPTOP-12", UserPrincipalName = "john.doe@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-4), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-3) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "intune-nc-2", AlertType = "NonCompliantDevice", Service = M365ServiceArea.Intune, Severity = AlertSeverity.Medium, Title = "Non-compliant device: Minimum OS build requirement failed", DeviceName = "HR-TABLET-03", UserPrincipalName = "jane.smith@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-6), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-5) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "intune-nc-3", AlertType = "NonCompliantDevice", Service = M365ServiceArea.Intune, Severity = AlertSeverity.Medium, Title = "Non-compliant device: Real-time protection disabled", DeviceName = "FIN-PC-09", UserPrincipalName = "finance.lead@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddHours(-8), LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-7) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "intune-nia-1", AlertType = "DeviceNotCheckedIn", Service = M365ServiceArea.Intune, Severity = AlertSeverity.Low, Title = "Device not checked in for 14 days", DeviceName = "OLD-LAPTOP-01", DetectedAt = DateTimeOffset.UtcNow.AddDays(-2), LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        db.SecurityAlerts.Add(new SecurityAlert { ExternalId = "intune-nia-2", AlertType = "DeviceNotCheckedIn", Service = M365ServiceArea.Intune, Severity = AlertSeverity.Low, Title = "Device not checked in for 21 days", DeviceName = "TEMP-DESKTOP-02", DetectedAt = DateTimeOffset.UtcNow.AddDays(-3), LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-2) });

        // MFA Status Alerts (242 registered, 15 missing)
        for (int i = 0; i < 242; i++)
        {
            db.SecurityAlerts.Add(new SecurityAlert { ExternalId = $"mfa-ok-{i}", AlertType = "MfaStatus", Service = M365ServiceArea.EntraId, Severity = AlertSeverity.Informational, Title = "MFA Registered", UserPrincipalName = $"user{i}@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddDays(-1), LastUpdatedAt = DateTimeOffset.UtcNow, IsResolved = true });
        }
        for (int i = 0; i < 15; i++)
        {
            db.SecurityAlerts.Add(new SecurityAlert { ExternalId = $"mfa-miss-{i}", AlertType = "MfaStatus", Service = M365ServiceArea.EntraId, Severity = AlertSeverity.Medium, Title = "User missing MFA registration", UserPrincipalName = $"nomfa{i}@vigil365.local", DetectedAt = DateTimeOffset.UtcNow.AddDays(-1), LastUpdatedAt = DateTimeOffset.UtcNow, IsResolved = false });
        }

        db.SaveChanges();
    }
}

// Enforce TLS outside Development. The app should be reached over HTTPS — either
// Kestrel with a certificate, or a reverse proxy terminating TLS. When a proxy
// (or Docker) handles TLS and forwards plain HTTP to the app, set
// Security:RequireHttps=false to avoid in-app redirect loops; the proxy enforces HTTPS.
var requireHttps = builder.Configuration.GetValue("Security:RequireHttps", !app.Environment.IsDevelopment());
if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Correlation id: honour an inbound X-Correlation-Id (from a proxy or caller),
// otherwise generate one. Echoed on the response and pushed as a logging scope
// so every log line for the request can be tied together across services.
app.Use(async (ctx, next) =>
{
    var correlationId = ctx.Request.Headers["X-Correlation-Id"].ToString();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 64)
        correlationId = Guid.NewGuid().ToString("N")[..16];
    ctx.TraceIdentifier = correlationId;
    ctx.Response.Headers["X-Correlation-Id"] = correlationId;

    var loggerFactory = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
    var reqLogger = loggerFactory.CreateLogger("Vigil365.Request");
    using (reqLogger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
        // One structured line per API request; static assets and /health probes
        // (which fire every few seconds from orchestrators) stay quiet.
        if (ctx.Request.Path.StartsWithSegments("/api"))
            reqLogger.LogInformation("{Method} {Path} => {StatusCode}",
                ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode);
    }
});

// Security headers must run before static files so the SPA shell and bundled
// assets receive the same browser protections as API responses.
var azureAdInstance = app.Configuration["AzureAd:Instance"]?.TrimEnd('/') ?? "https://login.microsoftonline.com";
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' {azureAdInstance} wss:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self';";
    ctx.Response.Headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ── Endpoint modules ────────────────────────────────────────────────────────
// Each module is a plain extension method over WebApplication, so the global
// deny-by-default FallbackPolicy still applies to everything it registers.
// These must all come before the /api catch-all and the SPA fallback below.
app.MapAuthHealthEndpoints();
app.MapSetupEndpoints();
app.MapDashboardEndpoints();
app.MapAdminEndpoints();
app.MapAlertsEndpoints();
app.MapNotificationsEndpoints();
app.MapReportsEndpoints();
app.MapIntegrationsEndpoints();
app.MapPlatformEndpoints();

app.Map("/api/{**rest}", (HttpContext ctx) =>
{
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    return Results.Json(new { error = "No such API endpoint.", path = ctx.Request.Path.Value },
        statusCode: StatusCodes.Status404NotFound);
}).AllowAnonymous();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/// <summary>Body shape for POST /api/triggered-alerts/{id}/snooze.</summary>
public sealed record SnoozeRequest(DateTimeOffset? Until, int? DurationHours);

/// <summary>Body shape for PUT /api/admin/users/{email}/role.</summary>
public sealed record RoleChangeRequest(string Role);

/// <summary>Body shape for POST/PUT /api/suppression-rules.</summary>
public sealed record SuppressionRuleRequest(
    Guid? PolicyId, string? EntityPattern, string? Reason,
    DateTimeOffset? ExpiresAt, bool? Enabled);

/// <summary>Body shape for POST /api/setup/graph (first-run wizard).</summary>
public sealed record GraphSetupRequest(string TenantId, string ClientId, string? ClientSecret, string? LoginInstance, string? BaseUrl);

/// <summary>Body shape for POST /api/admin/users (pre-provision a user).</summary>
public sealed record AddUserRequest(string Email, string Role, string? DisplayName, bool SendInvite = false);

/// <summary>Body shape for POST /api/api-tokens.</summary>
public sealed record ApiTokenCreateRequest(string? Name, string? Scopes, DateTimeOffset? ExpiresAt);

/// <summary>Body shape for the workbench endpoints (assign / disposition).</summary>
public sealed record WorkbenchRequest(string? AssignedTo, string? Disposition);

/// <summary>Body shape for POST /api/alert-notes/{kind}/{targetId}.</summary>
public sealed record NoteRequest(string Text);
