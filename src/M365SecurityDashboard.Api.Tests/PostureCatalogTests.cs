using System.Globalization;
using M365SecurityDashboard.Api.Services.Posture;
using static M365SecurityDashboard.Api.Tests.PostureFixtures;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The posture data maps (Data/Posture/*.json) are hand-written or generated from Microsoft's
/// licensing CSV. These guard that every row keeps its source, that the maps agree with each
/// other, and that baseline references stay identifiers (docs/LICENSE_POSTURE_PLAN.md §6).
/// </summary>
public sealed class PostureCatalogTests
{
    private static readonly PostureCatalog Catalog = PostureCatalog.Default;

    private static bool IsDate(string s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsHttps(string? s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    [Fact]
    public void Check_ids_are_unique_and_every_check_explains_itself()
    {
        Assert.Equal(Catalog.Checks.Count, Catalog.Checks.Select(c => c.Id).Distinct().Count());
        Assert.All(Catalog.Checks, c =>
        {
            Assert.Matches(@"^PST-[A-Z]{3}-\d{3}$", c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Title), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.WhyItMatters), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Minimum), c.Id);
            Assert.NotEmpty(c.Fix);
            Assert.Contains(c.Reads, new[] { "graph", "exchange" });
        });
    }

    [Fact]
    public void Every_check_names_a_feature_that_exists_and_a_rule_the_evaluator_knows()
    {
        Assert.All(Catalog.Checks, c => Assert.Contains(Catalog.Features, f => f.Id == c.Feature));

        // A fully licensed tenant reaches every Graph rule.
        var findings = Evaluate(Snapshot([], false, E5)).Findings;
        Assert.Equal(Catalog.Checks.Count, findings.Count);
        Assert.DoesNotContain(findings, f => f.Detail.StartsWith("No rule", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_feature_keeps_its_source_and_when_it_was_read()
    {
        Assert.All(Catalog.Features, f =>
        {
            Assert.True(IsHttps(f.Source), $"{f.Id}: source must be an https URL");
            Assert.True(IsDate(f.Retrieved), $"{f.Id}: retrieved must be yyyy-MM-dd");
        });
        Assert.Single(Catalog.Features, f => f.IsFree);
    }

    [Fact]
    public void Every_service_plan_a_feature_needs_is_in_Microsofts_list_and_some_licence_includes_it()
    {
        var known = Catalog.ServicePlans.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in Catalog.Features.Where(f => !f.IsFree))
        {
            Assert.All(feature.RequiresAnyServicePlan, plan => Assert.Contains(plan, known));
            // Otherwise a NotLicensed finding would have nothing to suggest.
            Assert.Contains(Catalog.Skus, k => k.ServicePlans.Intersect(feature.RequiresAnyServicePlan, StringComparer.OrdinalIgnoreCase).Any());
        }
    }

    [Fact]
    public void Entitlements_keep_their_source_and_every_licence_has_an_id_and_plans()
    {
        Assert.True(IsHttps(Catalog.EntitlementSource.Url));
        Assert.True(IsDate(Catalog.EntitlementSource.Retrieved));
        Assert.All(Catalog.Skus, k =>
        {
            Assert.True(Guid.TryParse(k.Guid, out _), $"{k.StringId}: GUID");
            Assert.False(string.IsNullOrWhiteSpace(k.StringId));
            Assert.False(string.IsNullOrWhiteSpace(k.Name));
            Assert.NotEmpty(k.ServicePlans);
        });
        Assert.All(Catalog.ServicePlans, p => Assert.True(Guid.TryParse(p.Id, out _), $"{p.Name}: GUID"));
    }

    [Fact]
    public void Baseline_references_are_identifiers_only()
    {
        Assert.True(IsHttps(Catalog.ScubaSource));
        Assert.All(Catalog.Checks, c =>
        {
            Assert.All(c.Baselines.Scuba, id => Assert.Matches(@"^MS\.[A-Z]+\.\d+\.\d+v\d+$", id));
            // CIS: recommendation numbers only, never benchmark text, until its licence terms are checked.
            Assert.All(c.Baselines.Cis, id => Assert.Matches(@"^\d+(\.\d+)*$", id));
            Assert.All(c.Baselines.SecureScore, id => Assert.Matches(@"^[A-Za-z0-9_]+$", id));
        });
    }

    [Fact]
    public void Portal_links_go_to_Microsoft_admin_centres_over_https()
    {
        Assert.All(Catalog.Checks.Where(c => c.PortalUrl is not null), c =>
        {
            Assert.True(IsHttps(c.PortalUrl), c.Id);
            var host = new Uri(c.PortalUrl!).Host;
            Assert.True(host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase), $"{c.Id}: {host}");
        });
    }

    [Fact]
    public void Threshold_checks_have_a_threshold_between_1_and_100()
    {
        Assert.All(Catalog.Checks.Where(c => c.Threshold is not null), c => Assert.InRange(c.Threshold!.Value, 1, 100));
        Assert.NotNull(Catalog.Checks.Single(c => c.Rule == "mfaRegistration").Threshold);
        Assert.True(IsDate(Catalog.EntitlementSource.LastModified));
    }
}
