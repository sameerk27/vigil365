namespace M365SecurityDashboard.Api.Models;

public enum M365ServiceArea
{
    EntraId = 1,
    Intune = 2,
    DefenderXdr = 3,
    ExchangeOnline = 4,
    ServiceHealth = 5
}

public enum AlertSeverity
{
    Informational = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum CollectionStatus
{
    Started = 0,
    Completed = 1,
    Failed = 2
}
