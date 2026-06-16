namespace M365SecurityDashboard.Api.Models;

public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "https://graph.microsoft.com";
    public int CollectionIntervalMinutes { get; set; } = 15;
    public int DevicesNotCheckedInDays { get; set; } = 7;
    public int SignInLookbackHours { get; set; } = 24;
    public string ExchangeQuarantinePath { get; set; } = "";
    public string MailFlowIssuesPath { get; set; } = "";

    public bool IsConfigured() =>
        IsRealValue(TenantId, "YOUR_TENANT_ID") &&
        IsRealValue(ClientId, "YOUR_APP_CLIENT_ID") &&
        IsRealValue(ClientSecret, "YOUR_APP_CLIENT_SECRET");

    private static bool IsRealValue(string? value, string placeholder) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals(placeholder, StringComparison.OrdinalIgnoreCase);
}
