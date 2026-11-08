using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// subscribedSkus lists free and self-service grants next to bought licences: Stream, Power BI (free),
/// Windows Store or PSTN conferencing with 1,000,000+ seats, and viral, trial, preview and developer
/// plans with 10,000. Counted as purchased they turn a tenant of ~2,700 licences into 13 million at
/// 0% use and ~100% "waste".
/// </summary>
public class LicenseSkusTests
{
    [Theory]
    [InlineData("STREAM", 1000000, 4)]
    [InlineData("MCOPSTNC", 10000000, 0)]
    [InlineData("POWERAPPS_VIRAL", 10000, 15)]
    [InlineData("FLOW_FREE", 10000, 1407)]
    [InlineData("Power_Pages_vTrial_for_Makers", 10000, 11)]
    [InlineData("PROJECT_MADEIRA_PREVIEW_IW_SKU", 10000, 0)]
    [InlineData("POWERAPPS_DEV", 10000, 30)]
    [InlineData("MICROSOFT_BUSINESS_CENTER", 10000, 2)]
    public void Free_and_self_service_grants_are_not_bought_licences(string sku, int purchased, int consumed) =>
        Assert.True(LicenseSkus.IsFree(sku, purchased, consumed));

    [Theory]
    [InlineData("SPE_E5", 1025, 1025)]
    [InlineData("ENTERPRISEPACK", 300, 120)]
    [InlineData("BIG_REAL_SKU", 10000, 9000)]
    public void Bought_licences_are_counted(string sku, int purchased, int consumed) =>
        Assert.False(LicenseSkus.IsFree(sku, purchased, consumed));
}
