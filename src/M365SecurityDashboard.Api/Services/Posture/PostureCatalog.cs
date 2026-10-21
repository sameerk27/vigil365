using System.Text.Json;

namespace M365SecurityDashboard.Api.Services.Posture;

/// <summary>A feature and the service plans that grant it (Data/Posture/features.json).</summary>
public sealed record PostureFeature(string Id, string Name, IReadOnlyList<string> RequiresAnyServicePlan, string Source, string? Note, string Retrieved)
{
    public bool IsFree => RequiresAnyServicePlan.Count == 0;
}

/// <summary>Baseline identifiers a check maps to. Identifiers only, never baseline text.</summary>
public sealed record PostureBaselines(IReadOnlyList<string> Scuba, IReadOnlyList<string> Cis, IReadOnlyList<string> SecureScore);

/// <summary>One check (Data/Posture/checks.json). Copy is Vigil365's own.</summary>
public sealed record PostureCheck(
    string Id, string Title, string Workload, string Feature, string Reads, string Rule,
    string WhyItMatters, string Minimum, IReadOnlyList<string> Fix, string? PortalUrl,
    PostureBaselines Baselines, int? Threshold);

/// <summary>A SKU posture can suggest, with its service plans (Data/Posture/entitlements.json).</summary>
public sealed record EntitlementSku(string StringId, string Guid, string Name, IReadOnlyList<string> ServicePlans);

public sealed record ServicePlanInfo(string Name, string Id, string FriendlyName);

public sealed record EntitlementSource(string Url, string Retrieved, string LastModified);

/// <summary>
/// The posture data maps, embedded in the API: entitlements generated from Microsoft's
/// licensing CSV by scripts/update-entitlements.ps1, and features and checks written by
/// hand with a source on every row. Loaded once; tests guard their consistency.
/// </summary>
public sealed class PostureCatalog
{
    public required IReadOnlyList<PostureFeature> Features { get; init; }
    public required IReadOnlyList<PostureCheck> Checks { get; init; }
    public required IReadOnlyList<EntitlementSku> Skus { get; init; }
    public required IReadOnlyList<ServicePlanInfo> ServicePlans { get; init; }
    public required IReadOnlyDictionary<string, string> ProductNames { get; init; }
    public required EntitlementSource EntitlementSource { get; init; }
    public required string ScubaSource { get; init; }

    public PostureFeature Feature(string id) => Features.First(f => f.Id == id);

    /// <summary>The product name Microsoft gives a SKU part number, else the part number itself.</summary>
    public string ProductName(string skuPartNumber) =>
        ProductNames.TryGetValue(skuPartNumber, out var name) ? name : skuPartNumber;

    private static readonly Lazy<PostureCatalog> Embedded = new(LoadEmbedded);
    public static PostureCatalog Default => Embedded.Value;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record EntitlementsFile(EntitlementSource Source, List<EntitlementSku> Skus, List<ServicePlanInfo> ServicePlans, Dictionary<string, string> ProductNames);
    private sealed record FeaturesFile(List<PostureFeature> Features);
    private sealed record BaselineSources(string Scuba);
    private sealed record ChecksFile(BaselineSources BaselineSources, List<PostureCheck> Checks);

    private static PostureCatalog LoadEmbedded()
    {
        var entitlements = Read<EntitlementsFile>("entitlements.json");
        var features = Read<FeaturesFile>("features.json");
        var checks = Read<ChecksFile>("checks.json");
        return new PostureCatalog
        {
            Features = features.Features,
            Checks = checks.Checks,
            Skus = entitlements.Skus,
            ServicePlans = entitlements.ServicePlans,
            ProductNames = entitlements.ProductNames,
            EntitlementSource = entitlements.Source,
            ScubaSource = checks.BaselineSources.Scuba,
        };
    }

    private static T Read<T>(string file)
    {
        var name = $"Vigil365.posture.{file}";
        using var stream = typeof(PostureCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded posture data '{name}' is missing from the build.");
        return JsonSerializer.Deserialize<T>(stream, Json)
            ?? throw new InvalidOperationException($"Embedded posture data '{name}' is empty.");
    }
}
