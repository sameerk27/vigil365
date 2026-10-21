namespace M365SecurityDashboard.Api.Models;

public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    // ── Certificate auth (preferred over the client secret when configured) ──
    /// <summary>Thumbprint of a cert in the Windows certificate store
    /// (CurrentUser\My, falls back to LocalMachine\My).</summary>
    public string CertificateThumbprint { get; set; } = "";
    /// <summary>Path to a PFX file (alternative to the store thumbprint).</summary>
    public string CertificatePath { get; set; } = "";
    /// <summary>Password for the PFX file, if any.</summary>
    public string CertificatePassword { get; set; } = "";

    public string BaseUrl { get; set; } = "https://graph.microsoft.com";
    public string LoginInstance { get; set; } = "https://login.microsoftonline.com";
    public int CollectionIntervalMinutes { get; set; } = 15;

    // ── MSP scale knobs ──
    /// <summary>How many client tenants collect at once. 1 = sequential.</summary>
    public int TenantParallelism { get; set; } = 2;
    /// <summary>Seconds between tenant starts, so a burst of clients does not hit Graph together.</summary>
    public int TenantStaggerSeconds { get; set; } = 3;
    /// <summary>Longest a repeatedly failing tenant is left alone before the next attempt.</summary>
    public int MaxBackoffMinutes { get; set; } = 240;
    public int DevicesNotCheckedInDays { get; set; } = 7;
    public int SignInLookbackHours { get; set; } = 24;
    public string ExchangeQuarantinePath { get; set; } = "";
    public string MailFlowIssuesPath { get; set; } = "";

    /// <summary>
    /// How often the Purview audit search (Copilot agents, Power Platform) runs, in
    /// minutes. Microsoft limits audit searches per tenant per day, so this runs on its
    /// own cadence, not every collection. 0 turns it off.
    /// </summary>
    public int PurviewAuditIntervalMinutes { get; set; } = 60;

    /// <summary>How far back the first Purview audit search looks.</summary>
    public int PurviewAuditLookbackHours { get; set; } = 24;

    /// <summary>
    /// Each search re-reads this much of the previous window: audit records can reach
    /// the log hours after the action. Duplicates are dropped on the record id.
    /// </summary>
    public int PurviewAuditOverlapMinutes { get; set; } = 120;

    /// <summary>A search Microsoft has not finished after this long is abandoned and submitted again.</summary>
    public int PurviewAuditQueryTimeoutMinutes { get; set; } = 180;

    /// <summary>
    /// Power Automate connectors that make a flow high-risk. Matched against the
    /// connector names in the flow's audit record, ignoring case; a trailing * matches
    /// any name that starts with the rest ("HTTP*" covers "HTTP with Microsoft Entra ID").
    /// An array, so configuration replaces the list instead of appending to it.
    /// </summary>
    public string[] PowerPlatformHighRiskConnectors { get; set; } = ["HTTP*", "FTP*", "SFTP*", "Gmail", "Dropbox"];

    // ── Power Platform inventory (optional; docs/COPILOT_POWER_PLATFORM_PLAN.md phase D) ──
    /// <summary>
    /// The Power Platform API. Read only when a tenant's admin has given Vigil365's app
    /// the Power Platform reader role; otherwise the inventory page explains how.
    /// </summary>
    public string PowerPlatformApiBaseUrl { get; set; } = "https://api.powerplatform.com";

    /// <summary>Most environments read per inventory, so a tenant full of developer environments stays quick.</summary>
    public int PowerPlatformMaxEnvironments { get; set; } = 50;

    /// <summary>An app shared with at least this many people is flagged as widely shared.</summary>
    public int PowerPlatformWideSharingUsers { get; set; } = 25;

    public bool HasCertificate() =>
        !string.IsNullOrWhiteSpace(CertificateThumbprint) ||
        !string.IsNullOrWhiteSpace(CertificatePath);

    public bool HasSecret() => IsRealValue(ClientSecret, "YOUR_APP_CLIENT_SECRET");

    /// <summary>Configured = identity known and at least one credential
    /// (certificate preferred, secret as fallback).</summary>
    public bool IsConfigured() =>
        IsRealValue(TenantId, "YOUR_TENANT_ID") &&
        IsRealValue(ClientId, "YOUR_APP_CLIENT_ID") &&
        (HasCertificate() || HasSecret());

    private static bool IsRealValue(string? value, string placeholder) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals(placeholder, StringComparison.OrdinalIgnoreCase);
}
