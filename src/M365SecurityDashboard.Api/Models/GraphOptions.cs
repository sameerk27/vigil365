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
    public int DevicesNotCheckedInDays { get; set; } = 7;
    public int SignInLookbackHours { get; set; } = 24;
    public string ExchangeQuarantinePath { get; set; } = "";
    public string MailFlowIssuesPath { get; set; } = "";

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
