using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The Graph credentials in effect for the current tenant. Scoped, resolved once
/// per scope, and the only place that decides *whose* credentials a Graph call
/// uses:
///
///   1. The tenant's own credentials (set through the tenants API) win.
///   2. Otherwise the install-wide credentials (appsettings + the setup wizard's
///      GraphConfig row) apply as they are if the tenant's Entra id is the one
///      those credentials belong to — or if it has none recorded and is the
///      install's own tenant: in Single mode the one tenant, in MSP mode only
///      <see cref="ClientTenant.DefaultId"/>. That keeps a single-tenant install
///      working unchanged.
///   3. MSP mode: the install's app is the shared multi-tenant MSP app, so a client
///      with a recorded Entra id (its admin consented to that app) uses the
///      install's client id and secret or certificate, asking for a token in the
///      client's own tenant.
///   4. Otherwise the tenant is unconfigured: collection skips it and the dashboard
///      reports "not connected" for it. In particular an MSP client with no Entra id
///      yet has not consented, and must never fall back to the MSP's own tenant —
///      that would collect the MSP's data as the client's.
///
/// Non-credential settings (collection interval, lookbacks, feed paths) are always
/// the install-wide values; see <see cref="Global"/>.
/// </summary>
public sealed class TenantGraphCredentials(
    AppDbContext db,
    ITenantContext tenant,
    IOptions<GraphOptions> global,
    SecretProtector protector,
    IOptions<EditionOptions> edition)
{
    private GraphOptions? _resolved;

    /// <summary>Install-wide settings — the right source for anything that is not a credential.</summary>
    public GraphOptions Global => global.Value;

    public async Task<GraphOptions> ResolveAsync(CancellationToken ct)
    {
        if (_resolved is not null) return _resolved;

        var effective = Clone(global.Value);
        if (tenant.Current is Guid id)
        {
            var row = await db.ClientTenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (row is not null) Apply(effective, row);
        }
        return _resolved = effective;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct) => (await ResolveAsync(ct)).IsConfigured();

    /// <summary>Pure decision, exposed for tests and for the tenants API's health view.</summary>
    public GraphOptions Resolve(ClientTenant row) { var o = Clone(global.Value); Apply(o, row); return o; }

    /// <summary>True when <paramref name="entraId"/> is the tenant the install-wide credentials belong to.</summary>
    public bool IsInstallTenant(string? entraId)
        => !string.IsNullOrWhiteSpace(entraId)
           && string.Equals(entraId.Trim(), global.Value.TenantId, StringComparison.OrdinalIgnoreCase);

    private void Apply(GraphOptions o, ClientTenant row)
    {
        if (row.HasOwnCredentials)
        {
            o.TenantId = row.MicrosoftTenantId ?? "";
            o.ClientId = row.ClientId ?? "";
            o.ClientSecret = protector.Unprotect(row.ClientSecret) ?? "";
            // The client's own certificate, or none — the install's certificate must
            // never be used for another organisation.
            o.CertificateThumbprint = (row.CertificateThumbprint ?? "").Replace(" ", "").ToUpperInvariant();
            o.CertificatePath = row.CertificatePath ?? "";
            o.CertificatePassword = protector.Unprotect(row.CertificatePassword) ?? "";
            if (!string.IsNullOrWhiteSpace(row.LoginInstance)) o.LoginInstance = row.LoginInstance;
            if (!string.IsNullOrWhiteSpace(row.BaseUrl)) o.BaseUrl = row.BaseUrl;
            return;
        }

        var msp = edition.Value.IsMsp;
        if (string.IsNullOrWhiteSpace(row.MicrosoftTenantId))
        {
            // No Entra id yet: the install's own tenant, or an MSP client that has
            // not consented.
            if (msp && row.Id != ClientTenant.DefaultId) Clear(o);
            return;
        }
        if (IsInstallTenant(row.MicrosoftTenantId)) return;

        // Another organisation: the shared MSP app it consented to, in its tenant.
        if (msp) o.TenantId = row.MicrosoftTenantId.Trim();
        else Clear(o);
    }

    /// <summary>Not this tenant's credentials. Leaves everything else intact so
    /// IsConfigured() is false for the right reason.</summary>
    private static void Clear(GraphOptions o)
    {
        o.TenantId = "";
        o.ClientId = "";
        o.ClientSecret = "";
        o.CertificateThumbprint = "";
        o.CertificatePath = "";
    }

    private static GraphOptions Clone(GraphOptions g) => new()
    {
        TenantId = g.TenantId,
        ClientId = g.ClientId,
        ClientSecret = g.ClientSecret,
        CertificateThumbprint = g.CertificateThumbprint,
        CertificatePath = g.CertificatePath,
        CertificatePassword = g.CertificatePassword,
        BaseUrl = g.BaseUrl,
        LoginInstance = g.LoginInstance,
        CollectionIntervalMinutes = g.CollectionIntervalMinutes,
        DevicesNotCheckedInDays = g.DevicesNotCheckedInDays,
        SignInLookbackHours = g.SignInLookbackHours,
        ExchangeQuarantinePath = g.ExchangeQuarantinePath,
        MailFlowIssuesPath = g.MailFlowIssuesPath,
    };
}
