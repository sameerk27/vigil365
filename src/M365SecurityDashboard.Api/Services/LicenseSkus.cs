using System.Text.RegularExpressions;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Tells a bought licence from a free or self-service grant in <c>/subscribedSkus</c>. Microsoft lists
/// free products (Stream, Power BI free, Windows Store, PSTN conferencing) with 1,000,000+ seats and
/// viral, trial, preview and developer plans with 10,000; counting them as purchased made the licence
/// panel read millions of seats at 0% use.
/// </summary>
public static partial class LicenseSkus
{
    public const int UnlimitedSeats = 1_000_000;
    public const int SelfServiceSeats = 10_000;

    [GeneratedRegex("VIRAL|FREE|TRIAL|PREVIEW|_IW(_|$)|_DEV$", RegexOptions.IgnoreCase)]
    private static partial Regex FreeName();

    /// <summary>A free or self-service grant: by name, by an unlimited seat count, or Microsoft's exact 10,000-seat grant with under half in use (a larger bought licence that is under-used is real waste, so it stays counted).</summary>
    public static bool IsFree(string? skuPartNumber, int purchased, int consumed) =>
        purchased >= UnlimitedSeats
        || (skuPartNumber is not null && FreeName().IsMatch(skuPartNumber))
        || (purchased == SelfServiceSeats && consumed * 2L < purchased);
}
