using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Portable alert-policy packs: share a tuned policy set between installs, keep
/// it in version control, or restore it after a rebuild.
///
/// Two rules shape the format:
///  • Runtime state never travels. Id, CreatedAt, LastTriggered and TriggerCount
///    describe *this* install's history; carrying them to another tenant would
///    show fabricated trigger counts for policies that never ran there.
///  • Recipients are stripped by default. NotifyEmail holds an internal address,
///    and packs are meant to be shared (a repo, a colleague). Opt in with
///    includeRecipients when exporting a backup for the same organisation.
///
/// Import matches on Name because ids differ across installs — the name is the
/// only stable natural key a human curates.
/// </summary>
public static class PolicyPack
{
    /// <summary>Bump only for a breaking shape change; importers reject unknown majors.</summary>
    public const int CurrentVersion = 1;

    private static readonly string[] ValidKinds = ["metric", "activity", "anomaly"];
    private static readonly string[] ValidSeverities = ["critical", "high", "medium", "low", "informational"];

    public sealed record PackPolicy(
        string Name,
        bool Enabled,
        string Category,
        string Condition,
        string Kind,
        string Metric,
        string? ActivityPattern,
        int WindowMinutes,
        double BaselineMultiplier,
        int BaselineDays,
        int Threshold,
        string Severity,
        int SuppressionMinutes,
        string? NotifyEmail);

    public sealed record Pack(
        int PackVersion,
        DateTimeOffset ExportedAt,
        string ExportedFrom,
        bool IncludesRecipients,
        IReadOnlyList<PackPolicy> Policies);

    public static PackPolicy ToPack(AlertPolicy p, bool includeRecipients) => new(
        Name: p.Name,
        Enabled: p.Enabled,
        Category: p.Category,
        Condition: p.Condition,
        Kind: p.Kind,
        Metric: p.Metric,
        ActivityPattern: p.ActivityPattern,
        WindowMinutes: p.WindowMinutes,
        BaselineMultiplier: p.BaselineMultiplier,
        BaselineDays: p.BaselineDays,
        Threshold: p.Threshold,
        Severity: p.Severity,
        SuppressionMinutes: p.SuppressionMinutes,
        NotifyEmail: includeRecipients ? p.NotifyEmail : null);

    /// <summary>
    /// An empty Kind means "metric" — that is how the evaluator reads it, and
    /// policies created before the Kind column existed still carry it.
    /// </summary>
    public static string NormaliseKind(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        return k.Length == 0 ? "metric" : k;
    }

    /// <summary>
    /// Validates one packed policy against what the evaluator actually requires.
    ///
    /// Rejects only what would make a policy silently useless — no name, no
    /// threshold, an activity policy with no pattern. Tuning fields that do not
    /// apply to the policy's kind (window on a metric policy, baseline on
    /// anything but anomaly) are coerced to defaults on import instead, matching
    /// how POST /api/alert-policies already treats them. Rejecting those would
    /// fail most real-world policies, since the columns default to zero for
    /// kinds that never read them.
    /// </summary>
    public static string? Validate(PackPolicy? p)
    {
        if (p is null) return "Entry is empty.";
        if (string.IsNullOrWhiteSpace(p.Name)) return "Name is required.";
        if (p.Name.Length > 200) return "Name exceeds 200 characters.";
        if (string.IsNullOrWhiteSpace(p.Category)) return "Category is required.";

        var kind = NormaliseKind(p.Kind);
        if (!ValidKinds.Contains(kind))
            return $"Kind must be one of: {string.Join(", ", ValidKinds)}.";

        if (!ValidSeverities.Contains((p.Severity ?? "").Trim().ToLowerInvariant()))
            return $"Severity must be one of: {string.Join(", ", ValidSeverities)}.";

        if (p.Threshold < 1) return "Threshold must be at least 1.";

        if (kind == "activity" && string.IsNullOrWhiteSpace(p.ActivityPattern))
            return "Activity policies require an activity pattern.";

        if (kind != "activity" && string.IsNullOrWhiteSpace(p.Metric))
            return "Metric and anomaly policies require a metric.";

        return null;
    }

    // Defaults mirror POST /api/alert-policies so an imported policy behaves
    // identically to one created through the UI.
    private static int Window(int v) => v > 0 ? v : 60;
    private static int BaselineDaysOf(int v) => v > 0 ? v : 30;
    private static double MultiplierOf(double v) => v > 0 ? v : 3.0;
    private static int Suppression(int v) => v >= 0 ? v : 60;

    /// <summary>Materialises a validated packed policy as a new entity for this install.</summary>
    public static AlertPolicy ToEntity(PackPolicy p) => new()
    {
        Id = Guid.NewGuid(),
        Name = p.Name.Trim(),
        Enabled = p.Enabled,
        Category = p.Category.Trim(),
        Condition = p.Condition ?? "",
        Kind = NormaliseKind(p.Kind),
        Metric = p.Metric ?? "",
        ActivityPattern = string.IsNullOrWhiteSpace(p.ActivityPattern) ? null : p.ActivityPattern.Trim(),
        WindowMinutes = Window(p.WindowMinutes),
        BaselineMultiplier = MultiplierOf(p.BaselineMultiplier),
        BaselineDays = BaselineDaysOf(p.BaselineDays),
        Threshold = p.Threshold,
        Severity = p.Severity.Trim().ToLowerInvariant(),
        SuppressionMinutes = Suppression(p.SuppressionMinutes),
        NotifyEmail = p.NotifyEmail,
        // Fresh runtime state — this policy has never fired *here*.
        CreatedAt = DateTimeOffset.UtcNow,
        LastTriggered = null,
        TriggerCount = 0,
    };

    /// <summary>Copies pack fields onto an existing policy, preserving its identity and history.</summary>
    public static void ApplyTo(AlertPolicy target, PackPolicy p)
    {
        target.Enabled = p.Enabled;
        target.Category = p.Category.Trim();
        target.Condition = p.Condition ?? "";
        target.Kind = NormaliseKind(p.Kind);
        target.Metric = p.Metric ?? "";
        target.ActivityPattern = string.IsNullOrWhiteSpace(p.ActivityPattern) ? null : p.ActivityPattern.Trim();
        target.WindowMinutes = Window(p.WindowMinutes);
        target.BaselineMultiplier = MultiplierOf(p.BaselineMultiplier);
        target.BaselineDays = BaselineDaysOf(p.BaselineDays);
        target.Threshold = p.Threshold;
        target.Severity = p.Severity.Trim().ToLowerInvariant();
        target.SuppressionMinutes = Suppression(p.SuppressionMinutes);
        // NotifyEmail only overwritten when the pack actually carries one, so
        // importing a shared (stripped) pack never silently clears local routing.
        if (!string.IsNullOrWhiteSpace(p.NotifyEmail)) target.NotifyEmail = p.NotifyEmail;
    }
}
