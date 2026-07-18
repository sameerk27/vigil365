using M365SecurityDashboard.Api.Data;
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
builder.Services.AddScoped<EntityProfileBuilder>();
builder.Services.AddScoped<AlertEvaluator>();
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
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' https://login.microsoftonline.com wss:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self';";
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

// Health endpoint for monitoring / orchestration (Docker HEALTHCHECK, k8s probes,
// uptime monitors). Reports DB connectivity, Graph configuration, and freshness of
// the last collection run. No Graph call is made — probes fire frequently and must
// stay cheap. 200 = healthy/degraded (app can serve traffic), 503 = DB unreachable.
app.MapGet("/health", async (AppDbContext db, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    var dbOk = false;
    string? dbError = null;
    object? lastCollection = null;
    var collectionFresh = (bool?)null;

    try
    {
        dbOk = await db.Database.CanConnectAsync(ct);
        if (dbOk)
        {
            var lastRun = await db.CollectionRuns.AsNoTracking()
                .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            if (lastRun is not null)
            {
                var staleAfter = TimeSpan.FromMinutes(Math.Max(options.Value.CollectionIntervalMinutes, 1) * 2);
                collectionFresh = DateTimeOffset.UtcNow - lastRun.StartedAt <= staleAfter;
                lastCollection = new
                {
                    startedAt = lastRun.StartedAt,
                    status = lastRun.Status.ToString(),
                    alertsUpserted = lastRun.AlertsUpserted,
                    fresh = collectionFresh
                };
            }
        }
    }
    catch (Exception ex) { dbError = ex.Message; }

    var graphConfigured = options.Value.IsConfigured();
    var status = !dbOk ? "unhealthy"
        : !graphConfigured || collectionFresh == false ? "degraded"
        : "healthy";

    var body = new
    {
        status,
        version = typeof(Program).Assembly.GetName().Version?.ToString(3),
        checks = new
        {
            database = new { ok = dbOk, error = dbError },
            graph = new { configured = graphConfigured },
            collection = lastCollection
        },
        checkedAt = DateTimeOffset.UtcNow
    };
    return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

// Public endpoint — returns only the non-secret config needed to initialise MSAL in the browser.
// The login identity comes from AzureAd (set in appsettings.Production.json / user secrets);
// fall back to Graph for older single-section setups.
app.MapGet("/api/auth/config", (IConfiguration config) =>
{
    string Pick(string azureAdKey, string graphKey)
    {
        var v = config[azureAdKey];
        if (!string.IsNullOrWhiteSpace(v) && !v.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)) return v;
        var g = config[graphKey];
        return (!string.IsNullOrWhiteSpace(g) && !g.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)) ? g : "";
    }
    return Results.Ok(new
    {
        clientId = Pick("AzureAd:ClientId", "Graph:ClientId"),
        tenantId = Pick("AzureAd:TenantId", "Graph:TenantId"),
        redirectUri = config["Auth:RedirectUri"] ?? "http://localhost:5173"
    });
}).AllowAnonymous();

// Returns the signed-in user's identity and role, and upserts their AppUsers row.
// Bootstrap: if Auth:BootstrapAdminEmail is configured, only that email becomes
// Admin on first sign-in; otherwise the first user to ever sign in becomes Admin.
// Everyone else defaults to Viewer until an Admin promotes them.
app.MapGet("/api/auth/me", async (
    System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, IConfiguration config,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache, AuditLogger audit, CancellationToken ct) =>
{
    var email = AuthHelpers.GetEmail(principal);
    var displayName = AuthHelpers.GetDisplayName(principal);
    if (string.IsNullOrEmpty(email)) return Results.BadRequest(new { error = "Token has no email claim." });

    var now = DateTimeOffset.UtcNow;
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
    var isFirstSignIn = false;
    var isNewSession = false;
    if (user is null)
    {
        var bootstrapEmail = (config["Auth:BootstrapAdminEmail"] ?? "").Trim().ToLowerInvariant();
        string role;
        if (!string.IsNullOrEmpty(bootstrapEmail))
            role = email == bootstrapEmail ? AppRoles.Admin : AppRoles.Viewer;
        else
            role = await db.AppUsers.AnyAsync(ct) ? AppRoles.Viewer : AppRoles.Admin;

        user = new AppUser { Email = email, DisplayName = displayName, Role = role, CreatedAt = now, LastSeenAt = now };
        db.AppUsers.Add(user);
        isFirstSignIn = true;
        // The claims transformation may have cached the default Viewer role for
        // this email before the row existed — evict so the real role applies now.
        cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
    }
    else
    {
        // Treat a gap of > 1h since the last request as a new sign-in session so
        // the audit trail covers sign-ins without logging every page load.
        isNewSession = now - user.LastSeenAt > TimeSpan.FromHours(1);
        user.LastSeenAt = now;
        if (!string.IsNullOrEmpty(displayName)) user.DisplayName = displayName;
    }
    await db.SaveChangesAsync(ct);

    if (isFirstSignIn)
        await audit.WriteAsync("auth.first_signin", "user", email, $"first sign-in, role {user.Role}", ct);
    else if (isNewSession)
        await audit.WriteAsync("auth.signin", "user", email, $"signed in as {user.Role}", ct);

    return Results.Ok(new { name = user.DisplayName ?? "", email = user.Email, role = user.Role });
});

// ── User management (Admin only) ─────────────────────────────────────────────────
// Roles are managed entirely in-app — no Entra App Roles, no Graph write permission.
app.MapGet("/api/admin/users", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.AppUsers.OrderBy(u => u.Email).ToListAsync(ct)))
    .RequireAuthorization("RequireAdmin");

// Pre-provision (invite) a user by email + role before they ever sign in.
// LastSeenAt = DateTimeOffset.MinValue marks "invited, not yet signed in".
app.MapPost("/api/admin/users", async (
    AddUserRequest input, AppDbContext db, NotificationSender sender, AuditLogger audit, IConfiguration config, CancellationToken ct) =>
{
    var email = (input.Email ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        return Results.BadRequest(new { error = "A valid email address is required." });

    if (!AppRoles.IsValid(input.Role))
        return Results.BadRequest(new { error = "Invalid role. Must be Admin, Analyst, or Viewer." });

    if (await db.AppUsers.AnyAsync(u => u.Email == email, ct))
        return Results.Conflict(new { error = $"A user with email '{email}' already exists." });

    var user = new AppUser
    {
        Email = email,
        DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim(),
        Role = input.Role,
        CreatedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.MinValue
    };
    db.AppUsers.Add(user);
    await db.SaveChangesAsync(ct);
    await audit.WriteAsync("user.add", "user", email, $"added with role {user.Role}", ct);

    string? inviteError = null;
    if (input.SendInvite)
    {
        var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings { Id = 1 };
        var url = config["Auth:RedirectUri"] ?? "http://localhost:5000";
        var (ok, error) = await sender.SendInviteEmailAsync(cfg, email, user.Role, url, ct);
        if (!ok) inviteError = error;
    }
    return Results.Ok(new { user, inviteSent = input.SendInvite && inviteError is null, inviteError });
}).RequireAuthorization("RequireAdmin");

// (Re)send the access-notification email to a pre-provisioned/existing user.
app.MapPost("/api/admin/users/{email}/invite", async (
    string email, AppDbContext db, NotificationSender sender, AuditLogger audit, IConfiguration config, CancellationToken ct) =>
{
    email = email.Trim().ToLowerInvariant();
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
    if (user is null) return Results.NotFound();

    var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings { Id = 1 };
    var url = config["Auth:RedirectUri"] ?? "http://localhost:5000";
    var (ok, error) = await sender.SendInviteEmailAsync(cfg, email, user.Role, url, ct);
    if (ok) await audit.WriteAsync("user.invite", "user", email, "invite email sent", ct);
    return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
}).RequireAuthorization("RequireAdmin");

app.MapPut("/api/admin/users/{email}/role", async (
    string email, RoleChangeRequest input, AppDbContext db, AuditLogger audit,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
{
    if (!AppRoles.IsValid(input.Role))
        return Results.BadRequest(new { error = "Invalid role. Must be Admin, Analyst, or Viewer." });

    email = email.Trim().ToLowerInvariant();
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
    if (user is null) return Results.NotFound();

    // Lockout guard: don't allow demoting the last remaining Admin.
    if (user.Role == AppRoles.Admin && input.Role != AppRoles.Admin)
    {
        var adminCount = await db.AppUsers.CountAsync(u => u.Role == AppRoles.Admin, ct);
        if (adminCount <= 1)
            return Results.BadRequest(new { error = "Cannot demote the last Admin. Promote another user to Admin first." });
    }

    var oldRole = user.Role;
    user.Role = input.Role;
    await db.SaveChangesAsync(ct);
    cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
    await audit.WriteAsync("user.role_change", "user", email, $"role {oldRole} -> {input.Role}", ct);
    return Results.Ok(user);
}).RequireAuthorization("RequireAdmin");

app.MapDelete("/api/admin/users/{email}", async (
    string email, AppDbContext db, AuditLogger audit,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    System.Security.Claims.ClaimsPrincipal caller, CancellationToken ct) =>
{
    email = email.Trim().ToLowerInvariant();
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
    if (user is null) return Results.NotFound();

    // Don't allow removing yourself or the last Admin.
    if (email == AuthHelpers.GetEmail(caller))
        return Results.BadRequest(new { error = "You cannot remove your own account." });
    if (user.Role == AppRoles.Admin && await db.AppUsers.CountAsync(u => u.Role == AppRoles.Admin, ct) <= 1)
        return Results.BadRequest(new { error = "Cannot remove the last Admin." });

    var removedRole = user.Role;
    db.AppUsers.Remove(user);
    await db.SaveChangesAsync(ct);
    cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
    await audit.WriteAsync("user.remove", "user", email, $"removed (was {removedRole})", ct);
    return Results.NoContent();
}).RequireAuthorization("RequireAdmin");

// Audit trail of security-relevant actions (Admin only).
app.MapGet("/api/admin/audit-log", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.AuditEntries.AsNoTracking().OrderByDescending(a => a.Timestamp).Take(200).ToListAsync(ct)))
    .RequireAuthorization("RequireAdmin");

// Full audit trail as CSV (Admin only). The export itself is audited.
app.MapGet("/api/admin/audit-log/export", async (AppDbContext db, AuditLogger audit, CancellationToken ct) =>
{
    var entries = await db.AuditEntries.AsNoTracking()
        .OrderBy(a => a.Id)
        .Take(100_000)
        .ToListAsync(ct);

    static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Id,TimestampUtc,ActorEmail,Action,TargetType,TargetId,Details,IpAddress,UserAgent,PrevHash,EntryHash");
    foreach (var e in entries)
        sb.AppendLine(string.Join(',',
            e.Id,
            e.Timestamp.UtcDateTime.ToString("O"),
            Csv(e.ActorEmail), Csv(e.Action), Csv(e.TargetType), Csv(e.TargetId),
            Csv(e.Details), Csv(e.IpAddress), Csv(e.UserAgent), Csv(e.PrevHash), Csv(e.EntryHash)));

    await audit.WriteAsync("audit.export", "audit_log", null, $"exported {entries.Count} entries as CSV", ct);
    return Results.File(
        System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
        "text/csv",
        $"vigil365-audit-log-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
}).RequireAuthorization("RequireAdmin");

// Verify the tamper-evident hash chain (Admin only). Recomputes every entry's
// hash and checks the PrevHash linkage in Id order. Entries written before the
// hash chain existed (EntryHash NULL) are counted as "legacy" and skipped —
// verification starts from the first hashed entry.
app.MapGet("/api/admin/audit-log/verify", async (AppDbContext db, CancellationToken ct) =>
{
    var entries = await db.AuditEntries.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);

    var legacy = 0; var checked_ = 0;
    long? firstBrokenId = null;
    string? expectedPrev = null; var chainStarted = false;

    foreach (var e in entries)
    {
        if (e.EntryHash is null) // pre-hash-chain row
        {
            legacy++;
            if (chainStarted && firstBrokenId is null) firstBrokenId = e.Id; // gap inside the chain
            continue;
        }

        if (chainStarted && e.PrevHash != expectedPrev && firstBrokenId is null)
            firstBrokenId = e.Id;
        if (AuditLogger.ComputeHash(e) != e.EntryHash && firstBrokenId is null)
            firstBrokenId = e.Id;

        expectedPrev = e.EntryHash;
        chainStarted = true;
        checked_++;
    }

    return Results.Ok(new
    {
        valid = firstBrokenId is null,
        total = entries.Count,
        verified = checked_,
        legacyUnhashed = legacy,
        firstBrokenId,
        verifiedAt = DateTimeOffset.UtcNow
    });
}).RequireAuthorization("RequireAdmin");

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
    if (tenantId == "" || clientId == "")
        return Results.BadRequest(new { error = "Tenant ID and Client ID are required." });

    var row = await db.GraphConfig.FirstOrDefaultAsync(g => g.Id == 1, ct);
    if (row is null) { row = new GraphConfig { Id = 1 }; db.GraphConfig.Add(row); }
    row.TenantId = tenantId;
    row.ClientId = clientId;
    // Keep the existing secret if the field was left blank (e.g. editing tenant only).
    if (clientSecret != "") row.ClientSecret = protector.Protect(clientSecret);
    row.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    // Apply over the live singleton so new GraphApiClient instances use it immediately.
    var o = opts.Value;
    o.TenantId = tenantId;
    o.ClientId = clientId;
    if (clientSecret != "") o.ClientSecret = clientSecret;

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

app.MapGet("/api/dashboard/overview", async (AppDbContext db, CancellationToken ct) =>
{
    var since = DateTimeOffset.UtcNow.AddDays(-30);
    // Service-health advisories are availability noise, not security signal —
    // they get their own count and never inflate the alert KPIs.
    var alerts = db.SecurityAlerts.AsNoTracking()
        .Where(a => !a.IsResolved && a.Service != M365ServiceArea.ServiceHealth);
    var totalActive = await alerts.CountAsync(ct);
    var high = await alerts.CountAsync(a => a.Severity == AlertSeverity.High || a.Severity == AlertSeverity.Critical, ct);
    var critical = await alerts.CountAsync(a => a.Severity == AlertSeverity.Critical, ct);
    var advisories = await db.SecurityAlerts.AsNoTracking()
        .CountAsync(a => !a.IsResolved && a.Service == M365ServiceArea.ServiceHealth, ct);
    var lastRun = await db.CollectionRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);

    var byService = await alerts
        .GroupBy(a => a.Service)
        .Select(g => new { service = g.Key.ToString(), count = g.Count() })
        .OrderByDescending(x => x.count)
        .ToListAsync(ct);

    var trends = await db.SecurityAlerts.AsNoTracking()
        .Where(a => a.DetectedAt >= since && a.Service != M365ServiceArea.ServiceHealth)
        .GroupBy(a => new { Date = a.DetectedAt.Date, a.Severity })
        .Select(g => new { date = g.Key.Date, severity = g.Key.Severity.ToString(), count = g.Count() })
        .OrderBy(x => x.date)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        totalActive,
        highPriority = high,
        criticalCount = critical,
        serviceAdvisories = advisories,
        lastRun,
        byService,
        trends,
        generatedAt = DateTimeOffset.UtcNow
    });
});

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

app.MapGet("/api/collector/runs", async (AppDbContext db, CancellationToken ct) =>
    await db.CollectionRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct));

app.MapPost("/api/collector/run", async (
    IServiceProvider services,
    Microsoft.Extensions.Options.IOptions<GraphOptions> options,
    CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.BadRequest(new { error = "Microsoft Graph is not configured. Complete the setup wizard first." });

    var collector = services.GetRequiredService<GraphCollector>();
    try
    {
        var run = await collector.CollectAsync(ct);
        return Results.Ok(run);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress"))
    {
        return Results.Conflict(new { error = "A collection run is already in progress." });
    }
}).RequireAuthorization("RequireAnalyst");

// ── New dashboard endpoints ────────────────────────────────────────────────

// Secure Score trend (direct Graph call)
app.MapGet("/api/dashboard/securescore", async (
    IServiceProvider services, IOptions<GraphOptions> options, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, currentScore = 0.0, maxScore = 100.0, percentage = 0.0, trend = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync("/v1.0/security/secureScores?$top=30", ct);
        if (items.Count == 0)
            return Results.Ok(new { configured = true, currentScore = 0.0, maxScore = 100.0, percentage = 0.0, trend = Array.Empty<object>() });

        var latest = items[0];
        var currentScore = latest.TryGetProperty("currentScore", out var cs) && cs.ValueKind == JsonValueKind.Number ? cs.GetDouble() : 0;
        var maxScore = latest.TryGetProperty("maxScore", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : 100;
        if (maxScore == 0) maxScore = 100;
        var percentage = Math.Round(currentScore / maxScore * 100, 1);

        var trend = items.Select(s =>
        {
            var sc = s.TryGetProperty("currentScore", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 0;
            var mx = s.TryGetProperty("maxScore", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetDouble() : 100;
            var dt = s.TryGetProperty("createdDateTime", out var dv) ? dv.GetString() : null;
            return new { date = dt != null && dt.Length >= 10 ? dt[..10] : dt, score = sc, maxScore = mx == 0 ? 100 : mx };
        }).Where(x => x.date != null).OrderBy(x => x.date).ToList();

        return Results.Ok(new { configured = true, currentScore, maxScore, percentage, trend });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to retrieve secure score trend from Graph.");
        return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", currentScore = 0.0, maxScore = 100.0, percentage = 0.0, trend = Array.Empty<object>() });
    }
});

app.MapGet("/api/dashboard/trends", async (AppDbContext db, CancellationToken ct) =>
{
    var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
    var snapshots = await db.TrendSnapshots.AsNoTracking()
        .Where(t => t.CapturedAt >= cutoff)
        .OrderBy(t => t.CapturedAt)
        .Select(t => new
        {
            t.Id,
            CapturedAt = t.CapturedAt.ToString("o"),
            t.RiskyUsersCount,
            t.MfaCoveragePct,
            t.NonCompliantDevicesCount,
            t.CriticalAlertsCount,
            t.HighAlertsCount,
            t.SecureScorePct,
            t.ComplianceIssuesCount
        })
        .ToListAsync(ct);

    return Results.Ok(snapshots);
});

// Identity summary: MFA from DB + guests & admin activity from Graph
app.MapGet("/api/dashboard/identity", async (
    AppDbContext db, IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    // MFA stats from already-collected alerts
    var mfaAlerts = await db.SecurityAlerts.AsNoTracking()
        .Where(a => a.AlertType == "MfaStatus").ToListAsync(ct);
    var mfaRegistered = mfaAlerts.Count(a => a.IsResolved);
    var mfaTotal = mfaAlerts.Count;
    var mfaPct = mfaTotal > 0 ? Math.Round((double)mfaRegistered / mfaTotal * 100, 1) : 0.0;

    // Sign-in summary from DB
    var since24h = DateTimeOffset.UtcNow.AddHours(-24);
    var signInAlerts = await db.SecurityAlerts.AsNoTracking()
        .Where(a => (a.AlertType == "RiskySignIn" || a.AlertType == "FailedSignIn") && a.DetectedAt >= since24h)
        .ToListAsync(ct);
    var foreignSignIns = signInAlerts.Where(a => a.AlertType == "RiskySignIn")
        .OrderByDescending(a => a.DetectedAt).Take(5)
        .Select(a => new { title = a.Title, userPrincipalName = a.UserPrincipalName, detectedAt = a.DetectedAt })
        .ToList();

    // Risky users from DB
    var riskyUsers = await db.SecurityAlerts.AsNoTracking()
        .CountAsync(a => a.AlertType == "RiskyUser" && !a.IsResolved, ct);

    // Guest accounts and admin activity from Graph (best-effort, time-boxed).
    // These are live Graph calls; under throttling they could otherwise stack
    // up 15s retry backoffs and hang the whole request. Cap them so the page
    // always returns the (fast) DB-backed data within a few seconds.
    int guestTotal = 0;
    object[] recentActivity = [];
    if (options.Value.IsConfigured())
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var gct = budget.Token;

        try
        {
            var guests = await graph.GetCollectionAsync(
                "/v1.0/users?$filter=userType eq 'Guest'&$select=id,displayName,userPrincipalName&$top=200", gct);
            guestTotal = guests.Count;
        }
        catch { /* permission not granted, or budget elapsed – skip */ }

        try
        {
            // Single page only — we want the latest 10, not the entire audit
            // history. GetCollectionAsync would follow @odata.nextLink through
            // every page (thousands of records).
            var audits = await graph.GetSinglePageAsync(
                "/v1.0/auditLogs/directoryAudits?$top=10&$orderby=activityDateTime desc", gct);
            recentActivity = audits.Select(a => (object)new
            {
                activityDateTime = a.TryGetProperty("activityDateTime", out var dt) ? dt.GetString() : null,
                activityDisplayName = a.TryGetProperty("activityDisplayName", out var n) ? n.GetString() : null,
                initiatedByUser = a.TryGetProperty("initiatedBy", out var ib) &&
                                  ib.TryGetProperty("user", out var u) &&
                                  u.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null,
                result = a.TryGetProperty("result", out var r) ? r.GetString() : null
            }).ToArray();
        }
        catch { /* permission not granted, or budget elapsed – skip */ }
    }

    return Results.Ok(new
    {
        configured = true,
        mfa = new { registered = mfaRegistered, total = mfaTotal, percentage = mfaPct },
        guests = new { total = guestTotal, active = guestTotal },
        riskyUsers,
        signIns = new
        {
            total = signInAlerts.Count,
            failed = signInAlerts.Count(a => a.AlertType == "FailedSignIn"),
            risky = signInAlerts.Count(a => a.AlertType == "RiskySignIn"),
            foreign = foreignSignIns.Count
        },
        foreignSignIns,
        recentAdminActivity = recentActivity
    });
});

// Device compliance summary from DB
app.MapGet("/api/dashboard/devices", async (
    AppDbContext db, IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    var deviceAlerts = await db.SecurityAlerts.AsNoTracking()
        .Where(a => a.Service == M365ServiceArea.Intune && !a.IsResolved).ToListAsync(ct);

    var nonCompliant = deviceAlerts.Count(a => a.AlertType == "NonCompliantDevice");
    var notCheckedIn = deviceAlerts.Count(a => a.AlertType == "DeviceNotCheckedIn");

    // Try to get total device count from Graph
    int totalDevices = 120;
    if (options.Value.IsConfigured())
    {
        try
        {
            var graph = services.GetRequiredService<GraphApiClient>();
            var all = await graph.GetCollectionAsync(
                "/v1.0/deviceManagement/managedDevices?$select=id&$top=500", ct);
            totalDevices = all.Count;
        }
        catch { /* skip */ }
    }

    var nonCompliantDevices = deviceAlerts
        .Where(a => a.AlertType == "NonCompliantDevice")
        .OrderByDescending(a => a.LastUpdatedAt).Take(5)
        .Select(a => new { a.DeviceName, a.UserPrincipalName, a.Description, a.LastUpdatedAt })
        .ToList();

    double compliancePct = totalDevices > 0 && totalDevices > nonCompliant
        ? Math.Round((double)(totalDevices - nonCompliant) / totalDevices * 100, 1) : 0;

    return Results.Ok(new { nonCompliant, notCheckedIn, totalDevices, compliancePct, nonCompliantDevices });
});

// Service health summary from DB
app.MapGet("/api/dashboard/servicehealth", async (AppDbContext db, CancellationToken ct) =>
{
    var issues = await db.SecurityAlerts.AsNoTracking()
        .Where(a => a.Service == M365ServiceArea.ServiceHealth && !a.IsResolved)
        .OrderByDescending(a => a.DetectedAt).ToListAsync(ct);

    return Results.Ok(new
    {
        total = issues.Count,
        issues = issues.Select(i => new
        {
            title = i.Title,
            description = i.Description,
            severity = i.Severity.ToString(),
            detectedAt = i.DetectedAt,
            portalUrl = i.PortalUrl
        })
    });
});

// ── Enterprise feature endpoints ──────────────────────────────────────────────

// License usage (subscribedSkus)
app.MapGet("/api/dashboard/licenses", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, skus = Array.Empty<object>(), totalPurchased = 0, totalConsumed = 0 });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var skus = await graph.GetCollectionAsync("/v1.0/subscribedSkus", ct);
        var result = skus.Select(s =>
        {
            var name = s.TryGetProperty("skuPartNumber", out var n) ? n.GetString() : "Unknown";
            var consumed = s.TryGetProperty("consumedUnits", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var purchased = s.TryGetProperty("prepaidUnits", out var p) &&
                            p.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;
            return new { name, consumed, purchased, available = Math.Max(0, purchased - consumed) };
        }).Where(s => s.purchased > 0).ToList();
        return Results.Ok(new { configured = true, skus = result, totalPurchased = result.Sum(s => s.purchased), totalConsumed = result.Sum(s => s.consumed) });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", skus = Array.Empty<object>(), totalPurchased = 0, totalConsumed = 0 }); }
});

// Inactive users (last sign-in > 90 days)
app.MapGet("/api/dashboard/inactive-users", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, inactive90Count = 0, neverSignedInCount = 0, totalUsers = 0, inactive90 = Array.Empty<object>(), neverSignedIn = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var users = await graph.GetCollectionAsync(
            "/v1.0/users?$select=id,displayName,userPrincipalName,signInActivity,accountEnabled,assignedLicenses&$top=200", ct);
        var threshold90 = DateTimeOffset.UtcNow.AddDays(-90);
        var result = users.Select(u =>
        {
            var upn = u.TryGetProperty("userPrincipalName", out var p) ? p.GetString() : null;
            var name = u.TryGetProperty("displayName", out var d) ? d.GetString() : null;
            var enabled = !u.TryGetProperty("accountEnabled", out var ae) || ae.GetBoolean();
            DateTimeOffset? lastSignIn = null;
            if (u.TryGetProperty("signInActivity", out var sia) && sia.ValueKind == JsonValueKind.Object &&
                sia.TryGetProperty("lastSignInDateTime", out var lsd) && lsd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(lsd.GetString(), out var dt)) lastSignIn = dt;
            var hasLicense = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array && al.GetArrayLength() > 0;
            var daysSince = lastSignIn.HasValue ? (int)(DateTimeOffset.UtcNow - lastSignIn.Value).TotalDays : -1;
            return new { upn, name, enabled, lastSignIn, hasLicense, daysSince };
        }).Where(u => u.upn != null && !u.upn.Contains("#EXT#") && u.enabled).ToList();

        var inactive90 = result.Where(u => u.lastSignIn == null || u.lastSignIn < threshold90).OrderBy(u => u.lastSignIn).Take(20).ToList();
        var neverSignedIn = result.Where(u => u.lastSignIn == null).Take(20).ToList();
        return Results.Ok(new { configured = true, inactive90Count = inactive90.Count, neverSignedInCount = neverSignedIn.Count, totalUsers = result.Count, inactive90, neverSignedIn });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", inactive90Count = 0, neverSignedInCount = 0, totalUsers = 0, inactive90 = Array.Empty<object>(), neverSignedIn = Array.Empty<object>() }); }
});

// Password expiry
app.MapGet("/api/dashboard/password-expiry", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, expiringSoonCount = 0, expiredCount = 0, neverExpiresCount = 0, totalUsers = 0, expiringSoon = Array.Empty<object>(), expired = Array.Empty<object>(), neverExpire = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var users = await graph.GetCollectionAsync(
            "/v1.0/users?$select=id,displayName,userPrincipalName,passwordPolicies,lastPasswordChangeDateTime,accountEnabled&$top=200", ct);
        var now = DateTimeOffset.UtcNow;
        var result = users.Select(u =>
        {
            var upn = u.TryGetProperty("userPrincipalName", out var p) ? p.GetString() : null;
            var name = u.TryGetProperty("displayName", out var d) ? d.GetString() : null;
            var enabled = !u.TryGetProperty("accountEnabled", out var ae) || ae.GetBoolean();
            var policies = u.TryGetProperty("passwordPolicies", out var pp) ? pp.GetString() : null;
            var neverExpires = policies != null && policies.Contains("DisablePasswordExpiration");
            DateTimeOffset? lastChanged = null;
            if (u.TryGetProperty("lastPasswordChangeDateTime", out var lcd) && lcd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(lcd.GetString(), out var dt)) lastChanged = dt;
            var daysSinceChange = lastChanged.HasValue ? (int)(now - lastChanged.Value).TotalDays : -1;
            var daysUntilExpiry = neverExpires || daysSinceChange < 0 ? -1 : 90 - daysSinceChange;
            return new { upn, name, enabled, neverExpires, lastChanged, daysSinceChange, daysUntilExpiry };
        }).Where(u => u.upn != null && !u.upn.Contains("#EXT#") && u.enabled).ToList();

        var expiringSoon = result.Where(u => !u.neverExpires && u.daysUntilExpiry >= 0 && u.daysUntilExpiry <= 14).OrderBy(u => u.daysUntilExpiry).Take(20).ToList();
        var expired = result.Where(u => !u.neverExpires && u.daysUntilExpiry < 0 && u.lastChanged.HasValue).Take(20).ToList();
        var neverExpire = result.Where(u => u.neverExpires).Take(10).ToList();
        return Results.Ok(new { configured = true, expiringSoonCount = expiringSoon.Count, expiredCount = expired.Count, neverExpiresCount = neverExpire.Count, totalUsers = result.Count, expiringSoon, expired, neverExpire });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", expiringSoonCount = 0, expiredCount = 0, neverExpiresCount = 0, totalUsers = 0, expiringSoon = Array.Empty<object>(), expired = Array.Empty<object>(), neverExpire = Array.Empty<object>() }); }
});

// Conditional Access policies
app.MapGet("/api/dashboard/conditional-access", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, enabled = 0, disabled = 0, reportOnly = 0, policies = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var policies = await graph.GetCollectionAsync("/v1.0/identity/conditionalAccess/policies", ct);
        var result = policies.Select(p =>
        {
            var name = p.TryGetProperty("displayName", out var n) ? n.GetString() : "Unnamed";
            var state = p.TryGetProperty("state", out var s) ? s.GetString() : "unknown";
            var inclUsers = "All Users"; var exclUsers = "None"; var apps = "All Apps";
            if (p.TryGetProperty("conditions", out var cond))
            {
                if (cond.TryGetProperty("users", out var u))
                {
                    if (u.TryGetProperty("includeUsers", out var inc) && inc.ValueKind == JsonValueKind.Array)
                        inclUsers = inc.EnumerateArray().Select(x => x.GetString()).FirstOrDefault() == "All" ? "All Users" : $"{inc.GetArrayLength()} users";
                    if (u.TryGetProperty("excludeUsers", out var exc) && exc.ValueKind == JsonValueKind.Array && exc.GetArrayLength() > 0)
                        exclUsers = $"{exc.GetArrayLength()} excluded";
                    if (u.TryGetProperty("includeGroups", out var grp) && grp.ValueKind == JsonValueKind.Array && grp.GetArrayLength() > 0 && inclUsers == "All Users")
                        inclUsers = $"{grp.GetArrayLength()} groups";
                }
                if (cond.TryGetProperty("applications", out var ap) && ap.TryGetProperty("includeApplications", out var incA) && incA.ValueKind == JsonValueKind.Array)
                    apps = incA.EnumerateArray().Select(x => x.GetString()).FirstOrDefault() == "All" ? "All Apps" : $"{incA.GetArrayLength()} apps";
            }
            var controls = new List<string>();
            if (p.TryGetProperty("grantControls", out var gc) && gc.ValueKind == JsonValueKind.Object &&
                gc.TryGetProperty("builtInControls", out var bic) && bic.ValueKind == JsonValueKind.Array)
                controls.AddRange(bic.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            return new { name, state, inclUsers, exclUsers, apps, controls = controls.ToArray() };
        }).ToList();
        return Results.Ok(new { configured = true, enabled = result.Count(p => p.state == "enabled"), disabled = result.Count(p => p.state == "disabled"), reportOnly = result.Count(p => p.state == "enabledForReportingButNotEnforced"), policies = result });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", enabled = 0, disabled = 0, reportOnly = 0, policies = Array.Empty<object>() }); }
});

// Admin audit log
app.MapGet("/api/dashboard/audit-log", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, failures = 0, events = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var audits = await graph.GetSinglePageAsync(
            "/v1.0/auditLogs/directoryAudits?$top=50&$orderby=activityDateTime desc", ct);
        var events = audits.Select(a => new
        {
            activityDateTime = a.TryGetProperty("activityDateTime", out var dt) ? dt.GetString() : null,
            activityDisplayName = a.TryGetProperty("activityDisplayName", out var n) ? n.GetString() : null,
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            result = a.TryGetProperty("result", out var r) ? r.GetString() : null,
            resultReason = a.TryGetProperty("resultReason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null,
            initiatedByUser = a.TryGetProperty("initiatedBy", out var ib) && ib.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null,
            targetResources = a.TryGetProperty("targetResources", out var tr) && tr.ValueKind == JsonValueKind.Array
                ? tr.EnumerateArray().Take(2).Select(t => t.TryGetProperty("displayName", out var dn) ? dn.GetString() : null).OfType<string>().ToArray()
                : Array.Empty<string>()
        }).ToList();
        return Results.Ok(new { configured = true, total = events.Count, failures = events.Count(e => e.result == "failure"), events });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, failures = 0, events = Array.Empty<object>() }); }
});

// Sign-in locations
app.MapGet("/api/dashboard/signin-locations", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, countries = 0, failures = 0, byCountry = Array.Empty<object>(), recent = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        // Single page only — the latest 100 sign-ins for the location map.
        // GetCollectionAsync would paginate through the entire sign-in history.
        var signIns = await graph.GetSinglePageAsync(
            "/v1.0/auditLogs/signIns?$top=100&$select=location,userPrincipalName,createdDateTime,status,appDisplayName&$orderby=createdDateTime desc", ct);
        var result = signIns.Select(s =>
        {
            var upn = s.TryGetProperty("userPrincipalName", out var p) ? p.GetString() : null;
            var appName = s.TryGetProperty("appDisplayName", out var a) ? a.GetString() : null;
            var created = s.TryGetProperty("createdDateTime", out var cd) ? cd.GetString() : null;
            string? city = null, country = null;
            if (s.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
            {
                if (loc.TryGetProperty("city", out var cv)) city = cv.GetString();
                if (loc.TryGetProperty("countryOrRegion", out var cov)) country = cov.GetString();
            }
            var success = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object &&
                          st.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.Number && ec.GetInt32() == 0;
            return new { upn, app = appName, created, city, country, success };
        }).ToList();
        var byCountry = result.Where(s => s.country != null)
            .GroupBy(s => s.country!)
            .Select(g => new { country = g.Key, count = g.Count(), failures = g.Count(s => !s.success) })
            .OrderByDescending(g => g.count).Take(15).ToList();
        return Results.Ok(new { configured = true, total = result.Count, countries = byCountry.Count, failures = result.Count(s => !s.success), byCountry, recent = result.Take(20).ToList() });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, countries = 0, failures = 0, byCountry = Array.Empty<object>(), recent = Array.Empty<object>() }); }
});

// Unified Defender alerts (alerts_v2 — all products)
app.MapGet("/api/dashboard/defender-alerts", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, bySeverity = new Dictionary<string, int>(), bySource = new Dictionary<string, int>(), alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=100&$filter=status ne 'resolved'&$orderby=createdDateTime desc", ct);

        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            description = a.TryGetProperty("description", out var d) ? d.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            classification = a.TryGetProperty("classification", out var cl) ? cl.GetString() : null,
            serviceSource = a.TryGetProperty("serviceSource", out var ss) ? ss.GetString() : null,
            detectionSource = a.TryGetProperty("detectionSource", out var ds) ? ds.GetString() : null,
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            lastUpdateDateTime = a.TryGetProperty("lastUpdateDateTime", out var lu) ? lu.GetString() : null,
            assignedTo = a.TryGetProperty("assignedTo", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
            incidentId = a.TryGetProperty("incidentId", out var inc) ? inc.GetString() : null,
            mitreTechniques = a.TryGetProperty("mitreTechniques", out var mt) && mt.ValueKind == JsonValueKind.Array
                ? mt.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : Array.Empty<string>(),
            recommendedActions = a.TryGetProperty("recommendedActions", out var ra) && ra.ValueKind == JsonValueKind.String ? ra.GetString() : null,
            actorDisplayName = a.TryGetProperty("actorDisplayName", out var actor) && actor.ValueKind == JsonValueKind.String ? actor.GetString() : null,
            threatDisplayName = a.TryGetProperty("threatDisplayName", out var threat) && threat.ValueKind == JsonValueKind.String ? threat.GetString() : null,
        }).ToList();

        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var bySource = alerts.GroupBy(a => a.serviceSource ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, bySeverity, bySource, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// Security incidents (grouped correlated alerts)
app.MapGet("/api/dashboard/security-incidents", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, bySeverity = new Dictionary<string, int>(), incidents = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/incidents?$top=50&$filter=status eq 'active'&$orderby=createdDateTime desc", ct);

        var incidents = items.Select(i => new
        {
            id = i.TryGetProperty("id", out var id) ? id.GetString() : null,
            displayName = i.TryGetProperty("displayName", out var n) ? n.GetString() : null,
            severity = i.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = i.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            classification = i.TryGetProperty("classification", out var cl) ? cl.GetString() : null,
            createdDateTime = i.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            lastUpdateDateTime = i.TryGetProperty("lastUpdateDateTime", out var lu) ? lu.GetString() : null,
            assignedTo = i.TryGetProperty("assignedTo", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null,
            incidentWebUrl = i.TryGetProperty("incidentWebUrl", out var url) ? url.GetString() : null,
            customTags = i.TryGetProperty("customTags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : Array.Empty<string>(),
            description = i.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString() : null,
            recommendedActions = i.TryGetProperty("recommendedActions", out var ra) && ra.ValueKind == JsonValueKind.String ? ra.GetString() : null,
        }).ToList();

        var bySeverity = incidents.GroupBy(i => i.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = incidents.Count, bySeverity, incidents });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, incidents = Array.Empty<object>() }); }
});

// Privileged roles
app.MapGet("/api/dashboard/privileged-roles", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, roles = Array.Empty<object>(), totalPrivilegedUsers = 0 });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var highPriv = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Global Administrator", "Security Administrator", "Compliance Administrator",
            "SharePoint Administrator", "Exchange Administrator", "User Administrator",
            "Privileged Role Administrator", "Global Reader", "Billing Administrator"
        };
        var directoryRoles = await graph.GetCollectionAsync("/v1.0/directoryRoles", ct);
        var roles = new List<object>();
        var totalPrivilegedUsers = 0;
        foreach (var role in directoryRoles)
        {
            var roleName = role.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            if (roleName == null || !highPriv.Contains(roleName)) continue;
            var roleId = role.TryGetProperty("id", out var id) ? id.GetString() : null;
            var members = new List<object>();
            try
            {
                if (roleId != null)
                {
                    var memberItems = await graph.GetCollectionAsync($"/v1.0/directoryRoles/{roleId}/members?$select=displayName,userPrincipalName", ct);
                    members = memberItems.Select(m => (object)new
                    {
                        displayName = m.TryGetProperty("displayName", out var md) ? md.GetString() : null,
                        userPrincipalName = m.TryGetProperty("userPrincipalName", out var mu) ? mu.GetString() : null
                    }).ToList();
                }
            }
            catch { /* 403 or per-role failure — leave members empty */ }
            totalPrivilegedUsers += members.Count;
            roles.Add(new { roleId, roleName, memberCount = members.Count, members });
        }
        return Results.Ok(new { configured = true, roles, totalPrivilegedUsers });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", roles = Array.Empty<object>(), totalPrivilegedUsers = 0 }); }
});

// DLP alerts
app.MapGet("/api/dashboard/dlp-alerts", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=50&$orderby=createdDateTime desc&$filter=category eq 'DataLossPrevention'", ct);
        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            serviceSource = a.TryGetProperty("serviceSource", out var ss) ? ss.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
        }).ToList();
        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var bySource = alerts.GroupBy(a => a.serviceSource ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, bySeverity, bySource, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// MDE vulnerabilities / endpoint alerts
app.MapGet("/api/dashboard/mde-vulnerabilities", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=50&$filter=serviceSource eq 'microsoftDefenderForEndpoint'&$orderby=createdDateTime desc", ct);
        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
            mitreTechniques = a.TryGetProperty("mitreTechniques", out var mt) && mt.ValueKind == JsonValueKind.Array
                ? mt.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : Array.Empty<string>(),
        }).ToList();
        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var byCategory = alerts.GroupBy(a => a.category ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, bySeverity, byCategory, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// PIM role activations
app.MapGet("/api/dashboard/pim", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, activations = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/roleManagement/directory/roleAssignments?$top=20&$expand=roleDefinition($select=displayName)", ct);
        var activations = items.Select(a =>
        {
            string? principalDisplayName = null, principalUpn = null, roleName = null;
            if (a.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("displayName", out var pd)) principalDisplayName = pd.GetString();
                if (p.TryGetProperty("userPrincipalName", out var pu)) principalUpn = pu.GetString();
            }
            if (a.TryGetProperty("roleDefinition", out var rd) && rd.ValueKind == JsonValueKind.Object &&
                rd.TryGetProperty("displayName", out var rdn)) roleName = rdn.GetString();
            return new
            {
                id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
                action = "Assigned",
                status = "Active",
                createdDateTime = (string?)null,
                justification = (string?)null,
                principalDisplayName,
                principalUpn,
                roleName
            };
        }).ToList();
        return Results.Ok(new { configured = true, total = activations.Count, activations });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, activations = Array.Empty<object>() }); }
});

// Email protection (Defender for Office 365)
app.MapGet("/api/dashboard/email-protection", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=50&$filter=serviceSource eq 'microsoftDefenderForOffice365'&$orderby=createdDateTime desc", ct);
        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
        }).ToList();
        var byCategory = alerts.GroupBy(a => a.category ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, byCategory, bySeverity, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// Purview sensitivity labels
app.MapGet("/api/dashboard/purview", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, labelCount = 0, labels = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetSinglePageAsync("https://graph.microsoft.com/beta/security/informationProtection/sensitivityLabels", ct);
        var labels = items.Select(l => new
        {
            id = l.TryGetProperty("id", out var id) ? id.GetString() : null,
            name = l.TryGetProperty("name", out var n) ? n.GetString() : null,
            description = l.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            color = l.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            sensitivity = l.TryGetProperty("sensitivity", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
            isActive = l.TryGetProperty("isActive", out var ia) && (ia.ValueKind == JsonValueKind.True || ia.ValueKind == JsonValueKind.False) && ia.GetBoolean(),
        }).ToList();
        return Results.Ok(new { configured = true, labelCount = labels.Count, labels });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", labelCount = 0, labels = Array.Empty<object>() }); }
});

// MDI alerts (Defender for Identity — on-prem AD lateral movement, credential theft)
app.MapGet("/api/dashboard/mdi-alerts", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=50&$filter=serviceSource eq 'microsoftDefenderForIdentity'&$orderby=createdDateTime desc", ct);
        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
            mitreTechniques = a.TryGetProperty("mitreTechniques", out var mt) && mt.ValueKind == JsonValueKind.Array
                ? mt.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : Array.Empty<string>(),
        }).ToList();
        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var byCategory = alerts.GroupBy(a => a.category ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, bySeverity, byCategory, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// MCAS alerts (Defender for Cloud Apps — SaaS anomalies, impossible travel, mass download)
app.MapGet("/api/dashboard/mcas-alerts", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=50&$filter=serviceSource eq 'microsoftDefenderForCloudApps'&$orderby=createdDateTime desc", ct);
        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
        }).ToList();
        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var byCategory = alerts.GroupBy(a => a.category ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, bySeverity, byCategory, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// Insider Risk Management (Purview IRM — data exfiltration, departing employees)
app.MapGet("/api/dashboard/insider-risk", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/alerts_v2?$top=50&$filter=serviceSource eq 'microsoftPurviewInsiderRiskManagement'&$orderby=createdDateTime desc", ct);
        var alerts = items.Select(a => new
        {
            id = a.TryGetProperty("id", out var id) ? id.GetString() : null,
            title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
            severity = a.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = a.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            category = a.TryGetProperty("category", out var cat) ? cat.GetString() : null,
            createdDateTime = a.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            alertWebUrl = a.TryGetProperty("alertWebUrl", out var url) ? url.GetString() : null,
        }).ToList();
        var bySeverity = alerts.GroupBy(a => a.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = alerts.Count, bySeverity, alerts });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, alerts = Array.Empty<object>() }); }
});

// Entra ID Risk Detections (25+ specific detection types: leaked creds, password spray, nation-state IPs)
app.MapGet("/api/dashboard/risk-detections", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, detections = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetSinglePageAsync(
            "/v1.0/identityProtection/riskDetections?$top=50", ct);
        var detections = items.Select(d =>
        {
            string? city = null, country = null;
            if (d.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
            {
                if (loc.TryGetProperty("city", out var cv)) city = cv.GetString();
                if (loc.TryGetProperty("countryOrRegion", out var cov)) country = cov.GetString();
            }
            return new
            {
                id = d.TryGetProperty("id", out var id) ? id.GetString() : null,
                riskEventType = d.TryGetProperty("riskEventType", out var ret) ? ret.GetString() : null,
                riskLevel = d.TryGetProperty("riskLevel", out var rl) ? rl.GetString() : "unknown",
                riskState = d.TryGetProperty("riskState", out var rs) ? rs.GetString() : "unknown",
                userDisplayName = d.TryGetProperty("userDisplayName", out var udn) ? udn.GetString() : null,
                userPrincipalName = d.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null,
                lastUpdatedDateTime = d.TryGetProperty("lastUpdatedDateTime", out var lu) ? lu.GetString() : null,
                activityDateTime = d.TryGetProperty("activityDateTime", out var ad) ? ad.GetString() : null,
                ipAddress = d.TryGetProperty("ipAddress", out var ip) && ip.ValueKind == JsonValueKind.String ? ip.GetString() : null,
                city, country
            };
        }).ToList();
        var byType = detections.GroupBy(d => d.riskEventType ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var byLevel = detections.GroupBy(d => d.riskLevel ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = detections.Count, byType, byLevel, detections });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, detections = Array.Empty<object>() }); }
});

// MDI Identity Sensor Health Issues (requires IdentityBaseline.Read.All)
app.MapGet("/api/dashboard/identity-health", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, issues = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync("/v1.0/security/identities/healthIssues", ct);
        var issues = items.Select(i => new
        {
            id = i.TryGetProperty("id", out var id) ? id.GetString() : null,
            displayName = i.TryGetProperty("displayName", out var n) ? n.GetString() : null,
            issueType = i.TryGetProperty("issueType", out var it) ? it.GetString() : null,
            severity = i.TryGetProperty("severity", out var s) ? s.GetString() : "unknown",
            status = i.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
            description = i.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            recommendations = i.TryGetProperty("recommendations", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            createdDateTime = i.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
            domainNames = i.TryGetProperty("domainNames", out var dn) && dn.ValueKind == JsonValueKind.Array
                ? dn.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : Array.Empty<string>(),
            sensorDNSNames = i.TryGetProperty("sensorDNSNames", out var sdn) && sdn.ValueKind == JsonValueKind.Array
                ? sdn.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : Array.Empty<string>(),
        }).ToList();
        var bySeverity = issues.GroupBy(i => i.severity ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new { configured = true, total = issues.Count, bySeverity, issues });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, issues = Array.Empty<object>() }); }
});

// Attack Simulation & Training (requires AttackSimulation.ReadWrite.All)
app.MapGet("/api/dashboard/attack-simulation", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
{
    if (!options.Value.IsConfigured())
        return Results.Ok(new { configured = false, total = 0, simulations = Array.Empty<object>() });
    try
    {
        var graph = services.GetRequiredService<GraphApiClient>();
        var items = await graph.GetCollectionAsync(
            "/v1.0/security/attackSimulation/simulations?$top=20", ct);
        var simulations = items.Select(s =>
        {
            int targeted = 0, clicked = 0, didNotClick = 0; double compromisedRate = 0;
            if (s.TryGetProperty("report", out var rpt) && rpt.ValueKind == JsonValueKind.Object)
            {
                if (rpt.TryGetProperty("numberOfUsersTargeted", out var nut) && nut.ValueKind == JsonValueKind.Number) targeted = nut.GetInt32();
                if (rpt.TryGetProperty("simulationEventsContent", out var sec) && sec.ValueKind == JsonValueKind.Object)
                {
                    if (sec.TryGetProperty("compromisedRate", out var cr2) && cr2.ValueKind == JsonValueKind.Number) compromisedRate = cr2.GetDouble();
                    if (sec.TryGetProperty("clickedPhishingLinkCount", out var cpl) && cpl.ValueKind == JsonValueKind.Number) clicked = cpl.GetInt32();
                    if (sec.TryGetProperty("didNotClickLinkCount", out var dnc) && dnc.ValueKind == JsonValueKind.Number) didNotClick = dnc.GetInt32();
                }
            }
            return new
            {
                id = s.TryGetProperty("id", out var id) ? id.GetString() : null,
                displayName = s.TryGetProperty("displayName", out var n) ? n.GetString() : null,
                attackType = s.TryGetProperty("attackType", out var at) ? at.GetString() : null,
                status = s.TryGetProperty("status", out var st) ? st.GetString() : "unknown",
                createdDateTime = s.TryGetProperty("createdDateTime", out var cr) ? cr.GetString() : null,
                completionDateTime = s.TryGetProperty("completionDateTime", out var cd) && cd.ValueKind == JsonValueKind.String ? cd.GetString() : null,
                numberOfUsersTargeted = targeted,
                compromisedRate,
                clickedPhishingLinkCount = clicked,
                didNotClickLinkCount = didNotClick,
            };
        }).ToList();
        var totalTargeted = simulations.Sum(s => s.numberOfUsersTargeted);
        var avgCompromiseRate = simulations.Count > 0
            ? Math.Round(simulations.Average(s => s.compromisedRate), 1) : 0.0;
        return Results.Ok(new { configured = true, total = simulations.Count, totalTargeted, avgCompromiseRate, simulations });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Dashboard endpoint error"); return Results.Ok(new { configured = true, error = "An error occurred. Check server logs for details.", total = 0, simulations = Array.Empty<object>() }); }
});

// ─────────────────────────────────────────────────────────────────────────────
// Section A: Enterprise Security Recommendations & Alert Coverage Gap Analysis
// ─────────────────────────────────────────────────────────────────────────────

app.MapGet("/api/recommendations", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await RecommendationsEngine.GetRecommendationsAsync(db, ct)));

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

// ── Tenant audit events (activity feed backing activity-based policies) ─────
app.MapGet("/api/audit-events", async (
    AppDbContext db, string? search, string? activity, int days = 7,
    int page = 1, int pageSize = 50, CancellationToken ct = default) =>
{
    page = page < 1 ? 1 : page;
    pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;
    var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));

    var q = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAt >= since);
    if (!string.IsNullOrWhiteSpace(activity))
        q = q.Where(e => EF.Functions.Like(e.Activity, activity.Replace("*", "%")));
    if (!string.IsNullOrWhiteSpace(search))
        q = q.Where(e =>
            e.Activity.Contains(search) ||
            (e.ActorUpn != null && e.ActorUpn.Contains(search)) ||
            (e.TargetName != null && e.TargetName.Contains(search)));

    var total = await q.CountAsync(ct);
    var items = await q.OrderByDescending(e => e.OccurredAt)
        .Skip((page - 1) * pageSize).Take(pageSize)
        .Select(e => new { e.Id, e.Activity, e.Category, e.ActorUpn, e.ActorApp, e.TargetName, e.Result, e.OccurredAt })
        .ToListAsync(ct);
    return Results.Ok(new { total, page, pageSize, items });
});

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

app.MapPost("/api/triggered-alerts/{id:guid}/resolve", async (AppDbContext db, Guid id, AuditLogger audit, CancellationToken ct) =>
{
    var t = await db.TriggeredAlerts.FindAsync([id], ct);
    if (t is null) return Results.NotFound();
    t.Status = "resolved";
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

// Notification settings (single row). Password is write-only — never returned.
app.MapGet("/api/notification-settings", async (AppDbContext db, SecretProtector protector, CancellationToken ct) =>
{
    var s = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings { Id = 1 };
    return Results.Ok(new
    {
        s.TeamsEnabled, TeamsWebhookUrl = protector.Unprotect(s.TeamsWebhookUrl),
        s.EmailEnabled, s.SmtpHost, s.SmtpPort, s.SmtpUseSsl, s.SmtpUsername,
        hasSmtpPassword = !string.IsNullOrEmpty(s.SmtpPassword),
        s.FromAddress, s.DefaultRecipient,
        s.WebhookEnabled, WebhookUrl = protector.Unprotect(s.WebhookUrl),
        s.MinSeverity,
        s.TeamsDigest, s.EmailDigest, s.WebhookDigest, s.DigestHourUtc, s.FailureAlertThreshold,
    });
}).RequireAuthorization("RequireAdmin");

app.MapPut("/api/notification-settings", async (AppDbContext db, SecretProtector protector, AuditLogger audit, NotificationSettings input, CancellationToken ct) =>
{
    var s = await db.NotificationSettings.FirstOrDefaultAsync(ct);
    if (s is null) { s = new NotificationSettings { Id = 1 }; db.NotificationSettings.Add(s); }
    s.TeamsEnabled = input.TeamsEnabled;
    s.TeamsWebhookUrl = protector.Protect(input.TeamsWebhookUrl);
    s.EmailEnabled = input.EmailEnabled;
    s.SmtpHost = input.SmtpHost;
    s.SmtpPort = input.SmtpPort <= 0 ? 587 : input.SmtpPort;
    s.SmtpUseSsl = input.SmtpUseSsl;
    s.SmtpUsername = input.SmtpUsername;
    if (!string.IsNullOrEmpty(input.SmtpPassword)) s.SmtpPassword = protector.Protect(input.SmtpPassword); // keep existing if blank
    s.FromAddress = input.FromAddress;
    s.DefaultRecipient = input.DefaultRecipient;
    s.WebhookEnabled = input.WebhookEnabled;
    s.WebhookUrl = protector.Protect(input.WebhookUrl);
    s.MinSeverity = string.IsNullOrWhiteSpace(input.MinSeverity) ? "low" : input.MinSeverity;
    s.TeamsDigest = input.TeamsDigest;
    s.EmailDigest = input.EmailDigest;
    s.WebhookDigest = input.WebhookDigest;
    s.DigestHourUtc = Math.Clamp(input.DigestHourUtc, 0, 23);
    s.FailureAlertThreshold = input.FailureAlertThreshold <= 0 ? 3 : input.FailureAlertThreshold;
    await db.SaveChangesAsync(ct);
    await audit.WriteAsync("settings.update", "settings", "notifications", "notification settings updated", ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization("RequireAdmin");

// Send a test notification through all enabled channels
app.MapPost("/api/notification-settings/test", async (AppDbContext db, NotificationSender sender, CancellationToken ct) =>
{
    var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct);
    if (cfg is null) return Results.Ok(new { ok = false, message = "No settings configured" });
    var test = new TriggeredAlert
    {
        Id = Guid.NewGuid(),
        PolicyName = "Test Notification",
        Severity = "high",
        Category = "test",
        Condition = "Manual test from Vigil365 settings",
        MetricValue = 1,
        Threshold = 1,
        TriggeredAt = DateTimeOffset.UtcNow,
        Status = "new",
    };
    await sender.DispatchAsync(db, cfg, test, ct);
    await db.SaveChangesAsync(ct);
    var logs = await db.NotificationLogs.Where(l => l.TriggeredAlertId == test.Id).ToListAsync(ct);
    return Results.Ok(new { ok = logs.Any(l => l.Success), results = logs.Select(l => new { l.Channel, l.Success, l.Error }) });
}).RequireAuthorization("RequireAdmin");

// Notification delivery history
app.MapGet("/api/notification-log", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.NotificationLogs.OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct)))
    .RequireAuthorization("RequireAnalyst");

// Per-channel delivery health (consecutive failures, last success/error).
app.MapGet("/api/notification-health", async (AppDbContext db, CancellationToken ct) =>
{
    var cfg = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
    var recent = await db.NotificationLogs.AsNoTracking().OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct);
    var health = NotificationHealth.Compute(recent);
    var threshold = cfg?.FailureAlertThreshold ?? 3;
    return Results.Ok(new { threshold, channels = health, anyFailing = health.Any(h => h.ConsecutiveFailures >= threshold) });
}).RequireAuthorization("RequireAnalyst");

// ── Entity investigation profile (drill-down) ──────────────────────────────
// GET /api/entity/{kind}/{id} — kind = user|device. Merges the entity's alerts
// and tenant audit activity into one reverse-chronological timeline.
app.MapGet("/api/entity/{kind}/{id}", async (EntityProfileBuilder builder, string kind, string id, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "Entity id is required." });
    var profile = await builder.BuildAsync(kind, id, maxItems: 300, ct);
    return Results.Ok(profile);
}).RequireAuthorization("RequireAnalyst");

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

app.MapPost("/api/report-schedules", async (AppDbContext db, AuditLogger audit, System.Security.Claims.ClaimsPrincipal user, ReportSchedule input, CancellationToken ct) =>
{
    var s = new ReportSchedule
    {
        Id = Guid.NewGuid(),
        Name = string.IsNullOrWhiteSpace(input.Name) ? "Weekly executive digest" : input.Name.Trim(),
        ReportType = "exec-digest",
        Cadence = input.Cadence is "daily" or "weekly" or "monthly" ? input.Cadence : "weekly",
        DayOfWeek = Math.Clamp(input.DayOfWeek, 0, 6),
        DayOfMonth = Math.Clamp(input.DayOfMonth, 1, 28),
        HourUtc = Math.Clamp(input.HourUtc, 0, 23),
        Recipients = input.Recipients ?? "",
        IncludeCsv = input.IncludeCsv,
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
app.MapPost("/api/report-schedules/{id:guid}/run-now", async (IServiceProvider sp, AppDbContext db, AuditLogger audit, Guid id, CancellationToken ct) =>
{
    var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (s is null) return Results.NotFound();
    var (ok, status) = await ReportScheduleWorker.DispatchAsync(sp, db, s, ct);
    s.LastRunAt = DateTimeOffset.UtcNow;
    s.LastRunStatus = status;
    await db.SaveChangesAsync(ct);
    await audit.WriteAsync("report.schedule.run", "report", id.ToString(), status, ct);
    return Results.Ok(new { ok, status });
}).RequireAuthorization("RequireAdmin");

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/// <summary>Body shape for POST /api/triggered-alerts/{id}/snooze.</summary>
public sealed record SnoozeRequest(DateTimeOffset? Until, int? DurationHours);

/// <summary>Body shape for PUT /api/admin/users/{email}/role.</summary>
public sealed record RoleChangeRequest(string Role);

/// <summary>Body shape for POST /api/setup/graph (first-run wizard).</summary>
public sealed record GraphSetupRequest(string TenantId, string ClientId, string? ClientSecret);

/// <summary>Body shape for POST /api/admin/users (pre-provision a user).</summary>
public sealed record AddUserRequest(string Email, string Role, string? DisplayName, bool SendInvite = false);

/// <summary>Body shape for the workbench endpoints (assign / disposition).</summary>
public sealed record WorkbenchRequest(string? AssignedTo, string? Disposition);

/// <summary>Body shape for POST /api/alert-notes/{kind}/{targetId}.</summary>
public sealed record NoteRequest(string Text);
