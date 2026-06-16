namespace M365SecurityDashboard.Api.Models;

public sealed class CollectionRun
{
    public long Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public CollectionStatus Status { get; set; }
    public int AlertsUpserted { get; set; }
    public int SourceFailures { get; set; }
    public string? Error { get; set; }
}
