using System.Text.Json;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// MSP client-tenant onboarding and lifecycle. Admin-only, audited. Client
/// secrets are stored protected and never returned. The tenant list is the
/// only endpoint here that reads tenant-scoped data across tenants, via the
/// sanctioned CrossTenant seam, to show each client's open-alert count.
/// </summary>
public static class TenantEndpoints
{
    public sealed record TenantUpsert(string Name, string? MicrosoftTenantId, string? Notes, bool? IsActive, string? BrandName = null, string? BrandAccentColor = null);
    public sealed record TenantCredentials(string ClientId, string? ClientSecret, string? LoginInstance, string? BaseUrl,
        string? CertificateThumbprint = null, string? CertificatePath = null, string? CertificatePassword = null);

    public sealed record AssignmentsUpdate(Guid[] TenantIds);

    public static void MapTenantEndpoints(this WebApplication app)
    {
        // ── Admin-consent landing (anonymous) ─────────────────────────────────
        // Microsoft redirects the client's Global Administrator here after they
        // approve (or decline) the multi-tenant app. There is no signed-in Vigil365
        // user on this request — the signed `state` (see ConsentState) is the only
        // thing we trust, and it names the exact ClientTenant row being onboarded
        // and carries that row's one-time nonce.
        // Consenting to a multi-tenant app provisions its service principal in the
        // client tenant, so this single approval is the "create app + consent" step.
        // The onboarding dialog polls the tenant's status and runs the test once
        // this records consent, so the page itself only needs to be a dead end the
        // admin can close (CSP forbids inline script/style, hence the plain markup).
        app.MapGet("/consented", async (HttpContext ctx, AppDbContext db, IDataProtectionProvider dataProtection, TenantGraphCredentials creds, AuditLogger audit, Microsoft.Extensions.Options.IOptions<EditionOptions> edition, CancellationToken ct) =>
        {
            if (!edition.Value.IsMsp) return Results.NotFound(); // no client onboarding in a single-organisation install
            var q = ctx.Request.Query;
            if (ConsentState.Decode(dataProtection, q["state"]) is not (Guid rowId, string nonce))
                return Landing(false, "This consent link is invalid or has expired. Start onboarding again from Vigil365.");

            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == rowId, ct);
            if (t is null)
                return Landing(false, "The client this link was for no longer exists.");
            // Used once already: a replay changes nothing, not even LastError. A
            // refused or declined consent keeps the nonce, so the same link can retry.
            if (!ConsentState.IsCurrent(t.ConsentNonce, nonce))
                return Landing(false, "This consent link has already been used. Start onboarding again from Vigil365 if the client still needs to consent.");

            var error = q["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                t.LastError = Truncate($"Admin consent was not granted: {error} {q["error_description"]}".Trim(), 1000);
                await db.SaveChangesAsync(ct);
                return Landing(false, "Consent was not granted. You can close this window and try again.");
            }

            // Success: admin_consent=True&tenant={client entra id}. The state is signed
            // but this parameter is not, so it must at least be a tenant id.
            if (!Guid.TryParse(q["tenant"].ToString(), out var consentedGuid))
                return Landing(false, "Microsoft did not say which tenant consented. Start onboarding again from Vigil365.");
            var consentedTenant = consentedGuid.ToString();
            if (!string.IsNullOrWhiteSpace(t.MicrosoftTenantId)
                && !string.Equals(t.MicrosoftTenantId, consentedTenant, StringComparison.OrdinalIgnoreCase))
            {
                t.LastError = $"Consent was granted in Entra tenant {consentedTenant}, but this client is set to {t.MicrosoftTenantId}.";
                await db.SaveChangesAsync(ct);
                return Landing(false, "Consent was granted in a different Microsoft tenant than expected. Check the client and try again.");
            }

            // A client never takes the MSP's own tenant from a callback (only the
            // install's own row may be it): that would collect the MSP's data as
            // the client's. Usually someone approved while signed in to the MSP.
            if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId) && t.Id != ClientTenant.DefaultId && creds.IsInstallTenant(consentedTenant))
            {
                t.LastError = "Consent was granted in the MSP's own Microsoft tenant, not the client's. The client's Global Administrator must approve it.";
                await db.SaveChangesAsync(ct);
                return Landing(false, "Consent was granted in the MSP's own Microsoft tenant. The client's Global Administrator must approve it.");
            }

            // Only the state is signed, not this parameter: a client never takes an
            // Entra tenant another client already has, or the shared app would
            // collect that client's data as this one's.
            if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId) && await EntraIdOwnerAsync(db, creds, t.Id, consentedTenant, ct) is not null)
            {
                t.LastError = $"Consent was granted in Entra tenant {consentedTenant}, which another client in Vigil365 already uses.";
                await db.SaveChangesAsync(ct);
                return Landing(false, "This Microsoft tenant cannot be connected to this client. Make sure you approved it signed in to the right organisation.");
            }

            if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId))
                t.MicrosoftTenantId = consentedTenant;
            t.ConsentNonce = null; // the link is spent
            t.ConsentGrantedAt = DateTimeOffset.UtcNow;
            t.LastError = null;
            t.ConsecutiveFailures = 0;
            t.NextCollectionAfter = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteForTenantAsync(t.Id, "tenant.consent", "tenant", t.Id.ToString(), $"admin consent granted for {consentedTenant}", ct);

            return Landing(true, $"{t.Name} is connected. You can close this window — Vigil365 will finish automatically.");
        }).AllowAnonymous();

        // ── Any signed-in user ────────────────────────────────────────────────

        // The switcher's data: tenants this user may work in, and the current one.
        app.MapGet("/api/tenants/me", async (System.Security.Claims.ClaimsPrincipal user, TenantAccess access, ITenantContext current, TenantGraphCredentials creds, CancellationToken ct) =>
        {
            var permitted = await access.PermittedTenantsAsync(user, ct);
            return Results.Ok(new
            {
                current = current.Current,
                tenants = permitted.Select(t => new
                {
                    t.Id, t.Name, t.IsActive,
                    configured = creds.Resolve(t).IsConfigured(),
                    t.LastCollectionStatus,
                }),
            });
        });

        // Cross-tenant rollup over the tenants this user may see: open triggered
        // alerts by severity and unresolved critical/high security alerts per
        // client. The MSP's morning triage view; the client sorts worst-first.
        app.MapGet("/api/tenants/rollup", async (System.Security.Claims.ClaimsPrincipal user, TenantAccess access, TenantRollupService rollup, CancellationToken ct) =>
        {
            var permitted = await access.PermittedTenantsAsync(user, ct);
            var rows = await rollup.BuildAsync(permitted, ct);
            return Results.Ok(rows.Select(r => new
            {
                r.Id, r.Name, r.Configured, r.LastCollectionAt, r.LastCollectionStatus, r.LastError,
                open = new { critical = r.OpenAlerts.Critical, high = r.OpenAlerts.High, medium = r.OpenAlerts.Medium, low = r.OpenAlerts.Low, total = r.OpenAlerts.Total },
                unresolvedAlerts = new { critical = r.UnresolvedCritical, high = r.UnresolvedHigh },
                health = r.Health,
            }));
        });

        // Cross-client open-alert queue over the clients this user may see (U6).
        // Acting on an item uses the ordinary alert endpoints with that item's
        // client in X-Vigil-Tenant, so the usual permission check applies.
        app.MapGet("/api/tenants/alerts", async (System.Security.Claims.ClaimsPrincipal user, TenantAccess access, TenantRollupService rollup, int? limit, CancellationToken ct) =>
        {
            var permitted = await access.PermittedTenantsAsync(user, ct);
            return Results.Ok(await rollup.OpenAlertsAcrossAsync(permitted, limit ?? 500, ct));
        });

        // ── Admin ─────────────────────────────────────────────────────────────
        var group = app.MapGroup("/api/tenants").RequireAuthorization("RequireAdmin");

        // Every non-Admin user's assigned tenants, keyed by email.
        group.MapGet("/assignments", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.UserTenantAssignments.AsNoTracking().ToListAsync(ct);
            return Results.Ok(rows.GroupBy(r => r.UserEmail).ToDictionary(g => g.Key, g => g.Select(r => r.TenantId).ToArray()));
        });

        group.MapPut("/assignments/{email}", async (string email, AssignmentsUpdate body, AppDbContext db, System.Security.Claims.ClaimsPrincipal caller, AuditLogger audit, IMemoryCache cache, CancellationToken ct) =>
        {
            email = email.Trim().ToLowerInvariant();
            var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null) return Results.NotFound(new { ok = false, message = "Unknown user." });
            if (user.Role == AppRoles.Admin)
                return Results.BadRequest(new { ok = false, message = "Admins see every tenant; assignments do not apply." });

            var wanted = (body.TenantIds ?? []).Distinct().ToHashSet();
            var known = await db.ClientTenants.AsNoTracking().Where(t => wanted.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
            if (known.Count != wanted.Count)
                return Results.BadRequest(new { ok = false, message = "One or more tenant ids are unknown." });

            var existing = await db.UserTenantAssignments.Where(a => a.UserEmail == email).ToListAsync(ct);
            db.UserTenantAssignments.RemoveRange(existing.Where(a => !wanted.Contains(a.TenantId)));
            var have = existing.Select(a => a.TenantId).ToHashSet();
            var by = AuthHelpers.GetEmail(caller);
            foreach (var id in wanted.Where(id => !have.Contains(id)))
                db.UserTenantAssignments.Add(new UserTenantAssignment { UserEmail = email, TenantId = id, AssignedAt = DateTimeOffset.UtcNow, AssignedBy = by });
            await db.SaveChangesAsync(ct);
            TenantAccess.Invalidate(cache, email);
            await audit.WriteMspAsync("tenant.assign", "user", email, $"{wanted.Count} tenant(s)", ct);
            return Results.Ok(new { ok = true, tenantIds = wanted });
        });

        group.MapGet("", async (AppDbContext db, TenantGraphCredentials creds, CancellationToken ct) =>
        {
            var tenants = await db.ClientTenants.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct);
            var openByTenant = await db.CrossTenant<TriggeredAlert>().AsNoTracking()
                .Where(t => t.Status == "new" || t.Status == "acknowledged") // as the rollup and queue count them
                .GroupBy(t => t.TenantId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            return Results.Ok(tenants.Select(t => View(t, creds, openByTenant.GetValueOrDefault(t.Id))));
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, TenantGraphCredentials creds, CancellationToken ct) =>
        {
            var t = await db.ClientTenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return t is null ? Results.NotFound() : Results.Ok(View(t, creds, null));
        });

        group.MapPost("", async (TenantUpsert body, AppDbContext db, TenantGraphCredentials creds, AuditLogger audit, IMemoryCache cache, Microsoft.Extensions.Options.IOptions<EditionOptions> edition, CancellationToken ct) =>
        {
            if (!edition.Value.IsMsp && await db.ClientTenants.AnyAsync(t => t.IsActive, ct))
                return Results.Conflict(new { ok = false, message = "This is a single-organisation install. Adding client tenants needs MSP mode (Edition:Mode = Msp)." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { ok = false, message = "Name is required." });
            if (await EntraIdOwnerAsync(db, creds, Guid.Empty, body.MicrosoftTenantId, ct) is { } owner)
                return EntraIdInUse(body.MicrosoftTenantId!, owner);

            var t = new ClientTenant
            {
                Name = body.Name.Trim(),
                MicrosoftTenantId = Normalize(body.MicrosoftTenantId),
                Notes = body.Notes,
                IsActive = body.IsActive ?? true,
                CreatedAt = DateTimeOffset.UtcNow,
                BrandName = Normalize(body.BrandName),
                BrandAccentColor = ValidColor(body.BrandAccentColor),
            };
            db.ClientTenants.Add(t);
            await db.SaveChangesAsync(ct);
            TenantResolutionMiddleware.InvalidateSoleTenant(cache);
            await audit.WriteForTenantAsync(t.Id, "tenant.create", "tenant", t.Id.ToString(), t.Name, ct);
            return Results.Created($"/api/tenants/{t.Id}", new { ok = true, id = t.Id });
        });

        group.MapPut("/{id:guid}", async (Guid id, TenantUpsert body, AppDbContext db, TenantGraphCredentials creds, AuditLogger audit, IMemoryCache cache, CancellationToken ct) =>
        {
            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { ok = false, message = "Name is required." });

            var deactivating = t.IsActive && body.IsActive == false;
            if (deactivating && await db.ClientTenants.CountAsync(x => x.IsActive, ct) <= 1)
                return Results.BadRequest(new { ok = false, message = "Cannot deactivate the only active tenant." });
            // A blank Entra id keeps the one consent recorded. The form sending this
            // edit can predate the consent (a link the client's admin opened elsewhere,
            // a test that failed while Graph replicated), and clearing the id would
            // quietly disconnect a consented client. A different id is a deliberate
            // change, and the consent recorded for the old tenant does not carry over.
            var entraId = Normalize(body.MicrosoftTenantId) ?? (t.ConsentGrantedAt is not null ? t.MicrosoftTenantId : null);
            var entraChanged = !string.Equals(entraId, t.MicrosoftTenantId, StringComparison.OrdinalIgnoreCase);
            if (entraChanged && await EntraIdOwnerAsync(db, creds, t.Id, entraId, ct) is { } owner)
                return EntraIdInUse(entraId!, owner);

            t.Name = body.Name.Trim();
            t.MicrosoftTenantId = entraId;
            if (entraChanged) t.ConsentGrantedAt = null;
            t.Notes = body.Notes;
            t.BrandName = Normalize(body.BrandName);
            t.BrandAccentColor = ValidColor(body.BrandAccentColor);
            if (body.IsActive is bool active)
            {
                t.IsActive = active;
                if (active) { t.ConsecutiveFailures = 0; t.NextCollectionAfter = null; } // re-activation clears backoff
            }
            await db.SaveChangesAsync(ct);
            TenantResolutionMiddleware.InvalidateSoleTenant(cache);
            await audit.WriteForTenantAsync(t.Id, "tenant.update", "tenant", t.Id.ToString(), $"{t.Name} active={t.IsActive}", ct);
            return Results.Ok(new { ok = true });
        });

        // Own Graph credentials for this client (a multi-tenant app registration the
        // client's admin has consented to). Omit ClientSecret to keep the stored one.
        group.MapPut("/{id:guid}/credentials", async (Guid id, TenantCredentials body, AppDbContext db, SecretProtector protector, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.ClientId))
                return Results.BadRequest(new { ok = false, message = "ClientId is required." });
            if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId))
                return Results.BadRequest(new { ok = false, message = "Set the client's Microsoft tenant id first — credentials are issued against it." });
            var hasCert = !string.IsNullOrWhiteSpace(body.CertificateThumbprint) || !string.IsNullOrWhiteSpace(body.CertificatePath);
            if (!hasCert && string.IsNullOrWhiteSpace(body.ClientSecret) && string.IsNullOrWhiteSpace(t.ClientSecret) && !t.HasOwnCertificate)
                return Results.BadRequest(new { ok = false, message = "A client secret or a certificate is required the first time." });

            t.ClientId = body.ClientId.Trim();
            if (!string.IsNullOrWhiteSpace(body.ClientSecret)) t.ClientSecret = protector.Protect(body.ClientSecret);
            if (hasCert)
            {
                // Certificate wins over the secret (mirrors GraphApiClient.BuildCredential).
                t.CertificateThumbprint = Normalize(body.CertificateThumbprint)?.Replace(" ", "").ToUpperInvariant();
                t.CertificatePath = Normalize(body.CertificatePath);
                if (!string.IsNullOrWhiteSpace(body.CertificatePassword)) t.CertificatePassword = protector.Protect(body.CertificatePassword);
            }
            t.ConsecutiveFailures = 0; t.NextCollectionAfter = null; // new credentials: try again now
            t.LoginInstance = Normalize(body.LoginInstance);
            t.BaseUrl = Normalize(body.BaseUrl);
            t.ConsentGrantedAt = null; // re-verify with /test
            t.LastError = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteForTenantAsync(t.Id, "tenant.credentials", "tenant", t.Id.ToString(), $"client id {t.ClientId}", ct);
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/{id:guid}/credentials", async (Guid id, AppDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            t.ClientId = null; t.ClientSecret = null; t.LoginInstance = null; t.BaseUrl = null; t.ConsentGrantedAt = null;
            t.CertificateThumbprint = null; t.CertificatePath = null; t.CertificatePassword = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteForTenantAsync(t.Id, "tenant.credentials.clear", "tenant", t.Id.ToString(), null, ct);
            return Results.Ok(new { ok = true });
        });

        // The URL a client's Global Administrator opens to grant the app
        // registration admin consent in their tenant. Uses the tenant's own client
        // id if set, else the install-wide one.
        group.MapGet("/{id:guid}/consent-url", async (Guid id, string? redirectUri, HttpContext ctx, AppDbContext db, IDataProtectionProvider dataProtection, TenantGraphCredentials creds, IConfiguration config, Microsoft.Extensions.Options.IOptions<EditionOptions> edition, CancellationToken ct) =>
        {
            if (!edition.Value.IsMsp) return Results.NotFound();
            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            // Default to this app's own /consented landing so the popup flow needs no
            // argument; an explicit redirectUri is still honoured. It must be a Web
            // redirect registered on the multi-tenant app, or Microsoft rejects it.
            redirectUri = string.IsNullOrWhiteSpace(redirectUri)
                ? $"{(config["Auth:RedirectUri"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/')}/consented"
                : redirectUri;
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme is not ("https" or "http"))
                return Results.BadRequest(new { ok = false, message = "redirectUri must be an absolute http(s) URL registered on the app." });

            var clientId = !string.IsNullOrWhiteSpace(t.ClientId) ? t.ClientId : creds.Global.ClientId;
            if (string.IsNullOrWhiteSpace(clientId) || clientId.Equals("YOUR_APP_CLIENT_ID", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { ok = false, message = "No client id: set the tenant's credentials or the install-wide Graph client id (Setup → register the MSP app)." });

            var login = (!string.IsNullOrWhiteSpace(t.LoginInstance) ? t.LoginInstance : creds.Global.LoginInstance).TrimEnd('/');
            var authority = string.IsNullOrWhiteSpace(t.MicrosoftTenantId) ? "organizations" : t.MicrosoftTenantId;
            // Every link issued until consent is recorded carries the same nonce, so
            // reopening the dialog does not break a link already sent to the client.
            if (t.ConsentNonce is null)
            {
                t.ConsentNonce = ConsentState.NewNonce();
                await db.SaveChangesAsync(ct);
            }
            var state = Uri.EscapeDataString(ConsentState.Encode(dataProtection, t.Id, t.ConsentNonce));
            var url = $"{login}/{authority}/adminconsent?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect.ToString())}&state={state}";
            return Results.Ok(new { ok = true, url });
        });

        // Prove the connection: call Graph as this tenant. On success records the
        // Entra tenant id (from /organization) and the consent timestamp.
        group.MapPost("/{id:guid}/test", async (Guid id, AppDbContext db, IServiceProvider services, AuditLogger audit, Microsoft.Extensions.Options.IOptions<EditionOptions> edition, CancellationToken ct) =>
        {
            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(t.Id);
            var creds = scope.ServiceProvider.GetRequiredService<TenantGraphCredentials>();
            if (!await creds.IsConfiguredAsync(ct))
            {
                t.LastError = "No Graph credentials apply to this tenant.";
                await db.SaveChangesAsync(ct);
                return Results.BadRequest(new { ok = false, message = t.LastError });
            }

            try
            {
                var graph = scope.ServiceProvider.GetRequiredService<GraphApiClient>();
                var org = await graph.GetSinglePageAsync("/v1.0/organization?$select=id,displayName", ct);
                var first = org.FirstOrDefault();
                var entraId = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var display = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

                if (!string.IsNullOrWhiteSpace(t.MicrosoftTenantId) && !string.IsNullOrWhiteSpace(entraId)
                    && !string.Equals(t.MicrosoftTenantId, entraId, StringComparison.OrdinalIgnoreCase))
                {
                    t.LastError = $"Credentials belong to Entra tenant {entraId}, not {t.MicrosoftTenantId}.";
                    await db.SaveChangesAsync(ct);
                    return Results.BadRequest(new { ok = false, message = t.LastError });
                }

                // Never adopt the MSP's own tenant for a client (see /consented). The
                // credential rules already leave such a client unconfigured; this
                // keeps it true if they change.
                if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId) && edition.Value.IsMsp && t.Id != ClientTenant.DefaultId && creds.IsInstallTenant(entraId))
                {
                    t.LastError = "These credentials reach the MSP's own Microsoft tenant, not the client's.";
                    await db.SaveChangesAsync(ct);
                    return Results.BadRequest(new { ok = false, message = t.LastError });
                }
                if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId) && await EntraIdOwnerAsync(db, creds, t.Id, entraId, ct) is not null)
                {
                    t.LastError = $"These credentials reach Entra tenant {entraId}, which another client in Vigil365 already uses.";
                    await db.SaveChangesAsync(ct);
                    return Results.BadRequest(new { ok = false, message = t.LastError });
                }
                if (string.IsNullOrWhiteSpace(t.MicrosoftTenantId)) t.MicrosoftTenantId = entraId;
                t.ConsentGrantedAt = DateTimeOffset.UtcNow;
                t.LastError = null;
                await db.SaveChangesAsync(ct);
                await audit.WriteForTenantAsync(t.Id, "tenant.test", "tenant", t.Id.ToString(), $"connected to {display ?? entraId}", ct);
                return Results.Ok(new { ok = true, microsoftTenantId = t.MicrosoftTenantId, displayName = display });
            }
            catch (Exception ex)
            {
                t.LastError = Truncate(ex.Message, 1000);
                await db.SaveChangesAsync(ct);
                return Results.BadRequest(new { ok = false, message = t.LastError, hint = GraphErrorHint.DescribeOrNull(ex.Message) });
            }
        });

        // Offboarding. Default deactivates (data kept, nothing collected, not
        // selectable). ?purge=true deletes the tenant and, by cascade, every row it
        // owned — the isolation suite proves nothing else goes with it. Its audit
        // entries stay (no foreign key), and the purge itself is recorded against it.
        group.MapDelete("/{id:guid}", async (Guid id, bool? purge, AppDbContext db, AuditLogger audit, IMemoryCache cache, CancellationToken ct) =>
        {
            var t = await db.ClientTenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (await db.ClientTenants.CountAsync(x => x.IsActive && x.Id != id, ct) == 0)
                return Results.BadRequest(new { ok = false, message = "Cannot remove the only active tenant." });

            if (purge == true)
            {
                db.ClientTenants.Remove(t);
                await db.SaveChangesAsync(ct);
                TenantResolutionMiddleware.InvalidateSoleTenant(cache);
                await audit.WriteForTenantAsync(id, "tenant.purge", "tenant", id.ToString(), t.Name, ct);
                return Results.Ok(new { ok = true, purged = true });
            }

            t.IsActive = false;
            await db.SaveChangesAsync(ct);
            TenantResolutionMiddleware.InvalidateSoleTenant(cache);
            await audit.WriteForTenantAsync(id, "tenant.deactivate", "tenant", id.ToString(), t.Name, ct);
            return Results.Ok(new { ok = true, purged = false });
        });
    }

    private static object View(ClientTenant t, TenantGraphCredentials creds, int? openAlerts)
    {
        var effective = creds.Resolve(t);
        return new
        {
            t.Id, t.Name, t.MicrosoftTenantId, t.IsActive, t.Notes, t.CreatedAt,
            hasOwnCredentials = t.HasOwnCredentials,
            clientId = t.ClientId,
            authMode = t.HasOwnCredentials ? (t.HasOwnCertificate ? "certificate" : "secret") : null,
            certificateThumbprint = t.CertificateThumbprint,
            t.BrandName, t.BrandAccentColor,
            t.ConsecutiveFailures, t.NextCollectionAfter,
            credentialSource = t.HasOwnCredentials ? "tenant" : effective.IsConfigured() ? "install" : "none",
            configured = effective.IsConfigured(),
            t.ConsentGrantedAt, t.LastCollectionAt, t.LastCollectionStatus, t.LastError,
            openAlerts,
        };
    }

    /// <summary>
    /// The client other than <paramref name="rowId"/> that already has this Entra
    /// tenant, inactive ones included (their data stays theirs); null if none.
    /// One Entra tenant is one client: a second row would collect the same tenant
    /// under another client's name, for its staff and its contacts. The install's
    /// own row (<see cref="ClientTenant.DefaultId"/>) has the install's tenant even
    /// with no id recorded, since it collects that tenant with no id.
    /// </summary>
    private static async Task<ClientTenant?> EntraIdOwnerAsync(AppDbContext db, TenantGraphCredentials creds, Guid rowId, string? entraId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entraId)) return null;
        var id = entraId.Trim().ToLowerInvariant();
        var recorded = await db.ClientTenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id != rowId && x.MicrosoftTenantId != null && x.MicrosoftTenantId.ToLower() == id, ct);
        if (recorded is not null || rowId == ClientTenant.DefaultId || !creds.IsInstallTenant(entraId)) return recorded;
        return await db.ClientTenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ClientTenant.DefaultId && x.MicrosoftTenantId == null, ct);
    }

    private static IResult EntraIdInUse(string entraId, ClientTenant owner)
        => Results.Conflict(new { ok = false, message = $"Entra tenant {entraId.Trim()} is already used by the client \"{owner.Name}\"." });

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? ValidColor(string? s)
        => s is not null && System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), "^#[0-9a-fA-F]{6}$") ? s.Trim().ToLowerInvariant() : null;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// The page the client's Global Administrator lands on after consent. No inline
    /// script or style (the CSP forbids both) — the onboarding dialog that opened the
    /// popup is what detects completion and finishes. Deliberately minimal.
    /// </summary>
    private static IResult Landing(bool ok, string message)
    {
        var title = ok ? "Consent granted" : "Consent not completed";
        var html = $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title} — Vigil365</title></head>
            <body>
              <main>
                <h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>
                <p>{System.Net.WebUtility.HtmlEncode(message)}</p>
                <p>You can close this window.</p>
              </main>
            </body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
