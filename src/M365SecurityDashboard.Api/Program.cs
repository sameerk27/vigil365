using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("Graph"));
builder.Services.Configure<AlertingOptions>(builder.Configuration.GetSection("Alerting"));
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddHttpClient<GraphApiClient>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<GraphCollector>();
builder.Services.AddScoped<NotificationSender>();
builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddHostedService<GraphCollectionWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
if (builder.Environment.IsDevelopment()) builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5000", "http://localhost:5173")
              .AllowAnyHeader()
              .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS"));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // EnsureCreated() does not add tables to a pre-existing database, so create
    // the alerting tables idempotently for installs that predate this feature.
    db.Database.ExecuteSqlRaw(AlertingSchema.EnsureTablesSql);
    AlertingSchema.SeedDefaultPolicies(db);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

// Security headers
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/api/dashboard/overview", async (AppDbContext db, CancellationToken ct) =>
{
    var since = DateTimeOffset.UtcNow.AddDays(-30);
    var alerts = db.SecurityAlerts.AsNoTracking().Where(a => !a.IsResolved);
    var totalActive = await alerts.CountAsync(ct);
    var high = await alerts.CountAsync(a => a.Severity == AlertSeverity.High || a.Severity == AlertSeverity.Critical, ct);
    var lastRun = await db.CollectionRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);

    var byService = await alerts
        .GroupBy(a => a.Service)
        .Select(g => new { service = g.Key.ToString(), count = g.Count() })
        .OrderByDescending(x => x.count)
        .ToListAsync(ct);

    var trends = await db.SecurityAlerts.AsNoTracking()
        .Where(a => a.DetectedAt >= since)
        .GroupBy(a => new { Date = a.DetectedAt.Date, a.Severity })
        .Select(g => new { date = g.Key.Date, severity = g.Key.Severity.ToString(), count = g.Count() })
        .OrderBy(x => x.date)
        .ToListAsync(ct);

    return Results.Ok(new
    {
        totalActive,
        highPriority = high,
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
    int page,
    int pageSize,
    CancellationToken ct) =>
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
    {
        return Results.BadRequest(new { error = "Graph credentials are not configured." });
    }

    var collector = services.GetRequiredService<GraphCollector>();
    var run = await collector.CollectAsync(ct);
    return Results.Ok(run);
});

// ── New dashboard endpoints ────────────────────────────────────────────────

// Secure Score trend (direct Graph call)
app.MapGet("/api/dashboard/securescore", async (
    IServiceProvider services, IOptions<GraphOptions> options, CancellationToken ct) =>
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
        return Results.Ok(new { configured = true, error = ex.Message, currentScore = 0.0, maxScore = 100.0, percentage = 0.0, trend = Array.Empty<object>() });
    }
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
    int totalDevices = 0;
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
        return Results.Ok(new { configured = false, total = 0, alerts = Array.Empty<object>() });
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
        return Results.Ok(new { configured = false, total = 0, incidents = Array.Empty<object>() });
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
// Alert Center — server-side policies, triggered alerts, notifications
// ─────────────────────────────────────────────────────────────────────────────

// Policies CRUD
app.MapGet("/api/alert-policies", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.AlertPolicies.OrderByDescending(p => p.CreatedAt).ToListAsync(ct)));

app.MapPost("/api/alert-policies", async (AppDbContext db, AlertPolicy input, CancellationToken ct) =>
{
    input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
    input.CreatedAt = DateTimeOffset.UtcNow;
    input.TriggerCount = 0;
    if (input.SuppressionMinutes <= 0) input.SuppressionMinutes = 60;
    db.AlertPolicies.Add(input);
    await db.SaveChangesAsync(ct);
    return Results.Ok(input);
});

app.MapPut("/api/alert-policies/{id:guid}", async (AppDbContext db, Guid id, AlertPolicy input, CancellationToken ct) =>
{
    var p = await db.AlertPolicies.FindAsync([id], ct);
    if (p is null) return Results.NotFound();
    p.Name = input.Name;
    p.Enabled = input.Enabled;
    p.Category = input.Category;
    p.Condition = input.Condition;
    p.Metric = input.Metric;
    p.Threshold = input.Threshold;
    p.Severity = input.Severity;
    p.NotifyEmail = input.NotifyEmail;
    p.SuppressionMinutes = input.SuppressionMinutes <= 0 ? 60 : input.SuppressionMinutes;
    await db.SaveChangesAsync(ct);
    return Results.Ok(p);
});

app.MapDelete("/api/alert-policies/{id:guid}", async (AppDbContext db, Guid id, CancellationToken ct) =>
{
    var p = await db.AlertPolicies.FindAsync([id], ct);
    if (p is null) return Results.NotFound();
    db.AlertPolicies.Remove(p);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Triggered alerts
app.MapGet("/api/triggered-alerts", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.TriggeredAlerts.OrderByDescending(t => t.TriggeredAt).Take(500).ToListAsync(ct)));

app.MapPost("/api/triggered-alerts/{id:guid}/acknowledge", async (AppDbContext db, Guid id, CancellationToken ct) =>
{
    var t = await db.TriggeredAlerts.FindAsync([id], ct);
    if (t is null) return Results.NotFound();
    t.Status = "acknowledged";
    t.AcknowledgedAt = DateTimeOffset.UtcNow;
    t.AcknowledgedBy = "dashboard";
    await db.SaveChangesAsync(ct);
    return Results.Ok(t);
});

app.MapPost("/api/triggered-alerts/{id:guid}/resolve", async (AppDbContext db, Guid id, CancellationToken ct) =>
{
    var t = await db.TriggeredAlerts.FindAsync([id], ct);
    if (t is null) return Results.NotFound();
    t.Status = "resolved";
    await db.SaveChangesAsync(ct);
    return Results.Ok(t);
});

// Per-alert snooze. Body: { "until": "2026-06-22T18:00:00Z" } or { "durationHours": 4|24|168 }.
// Until wins if both are supplied; durationHours defaults to 24 if neither is supplied.
app.MapPost("/api/triggered-alerts/{id:guid}/snooze", async (
    AppDbContext db, Guid id, SnoozeRequest input, CancellationToken ct) =>
{
    var t = await db.TriggeredAlerts.FindAsync([id], ct);
    if (t is null) return Results.NotFound();
    if (t.Status is "resolved" or "auto_resolved")
        return Results.BadRequest(new { error = "Cannot snooze a terminal alert." });

    var until = input.Until
        ?? (input.DurationHours is { } h ? DateTimeOffset.UtcNow.AddHours(Math.Clamp(h, 1, 8760)) : DateTimeOffset.UtcNow.AddHours(24));
    t.SnoozedUntil = until;
    t.SnoozedBy = "dashboard";
    await db.SaveChangesAsync(ct);
    return Results.Ok(t);
});

app.MapPost("/api/triggered-alerts/{id:guid}/unsnooze", async (
    AppDbContext db, Guid id, CancellationToken ct) =>
{
    var t = await db.TriggeredAlerts.FindAsync([id], ct);
    if (t is null) return Results.NotFound();
    t.SnoozedUntil = null;
    t.SnoozedBy = null;
    await db.SaveChangesAsync(ct);
    return Results.Ok(t);
});

// Manually run an evaluation pass (used by the dashboard "refresh" + on-demand check)
app.MapPost("/api/alert-policies/evaluate", async (AlertEvaluator evaluator, CancellationToken ct) =>
{
    var fired = await evaluator.EvaluateAsync(ct);
    return Results.Ok(new { fired });
});

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
    });
});

app.MapPut("/api/notification-settings", async (AppDbContext db, SecretProtector protector, NotificationSettings input, CancellationToken ct) =>
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
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { ok = true });
});

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
});

// Notification delivery history
app.MapGet("/api/notification-log", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(await db.NotificationLogs.OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct)));

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Body shape for POST /api/triggered-alerts/{id}/snooze.</summary>
public sealed record SnoozeRequest(DateTimeOffset? Until, int? DurationHours);
