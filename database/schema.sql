IF DB_ID(N'M365SecurityDashboard') IS NULL
BEGIN
    CREATE DATABASE M365SecurityDashboard;
END
GO

USE M365SecurityDashboard;
GO

IF OBJECT_ID(N'dbo.SecurityAlerts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SecurityAlerts
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SecurityAlerts PRIMARY KEY,
        ExternalId NVARCHAR(256) NULL,
        AlertType NVARCHAR(120) NOT NULL,
        Service INT NOT NULL,
        Severity INT NOT NULL,
        Title NVARCHAR(512) NOT NULL,
        Description NVARCHAR(4000) NULL,
        UserPrincipalName NVARCHAR(320) NULL,
        DeviceName NVARCHAR(256) NULL,
        PortalUrl NVARCHAR(2048) NULL,
        DetectedAt DATETIMEOFFSET NOT NULL,
        LastUpdatedAt DATETIMEOFFSET NOT NULL,
        IsResolved BIT NOT NULL CONSTRAINT DF_SecurityAlerts_IsResolved DEFAULT 0,
        RawJson NVARCHAR(MAX) NOT NULL
    );

    CREATE UNIQUE INDEX UX_SecurityAlerts_SourceExternalId
        ON dbo.SecurityAlerts(Service, AlertType, ExternalId)
        WHERE ExternalId IS NOT NULL;
    CREATE INDEX IX_SecurityAlerts_DetectedAt ON dbo.SecurityAlerts(DetectedAt);
    CREATE INDEX IX_SecurityAlerts_ServiceSeverityResolved ON dbo.SecurityAlerts(Service, Severity, IsResolved);
END
GO

IF OBJECT_ID(N'dbo.CollectionRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CollectionRuns
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionRuns PRIMARY KEY,
        StartedAt DATETIMEOFFSET NOT NULL,
        CompletedAt DATETIMEOFFSET NULL,
        Status INT NOT NULL,
        AlertsUpserted INT NOT NULL,
        SourceFailures INT NOT NULL,
        Error NVARCHAR(4000) NULL
    );

    CREATE INDEX IX_CollectionRuns_StartedAt ON dbo.CollectionRuns(StartedAt);
END
GO
