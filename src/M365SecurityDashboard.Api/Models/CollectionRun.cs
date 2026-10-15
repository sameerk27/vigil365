namespace M365SecurityDashboard.Api.Models;

public sealed class CollectionRun : ITenantScoped
{
    /// <summary>Owning tenant. Stamped from the tenant context on insert; see ITenantScoped.</summary>
    public Guid TenantId { get; set; }

    public long Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public CollectionStatus Status { get; set; }
    public int AlertsUpserted { get; set; }
    public int SourceFailures { get; set; }
    public string? Error { get; set; }

    /// <summary>Real Graph API requests made during this run (delta of the in-process counter).</summary>
    public int GraphRequestCount { get; set; }
    /// <summary>Graph 429 (throttle) responses received during this run.</summary>
    public int GraphThrottleCount { get; set; }

    /// <summary>JSON array of { source, error } for each source that failed this run.</summary>
    public string? SourceFailureDetails { get; set; }
}
