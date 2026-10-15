<#
.SYNOPSIS
  Fails if either engine's migration set has drifted from the EF model.

.DESCRIPTION
  Vigil365 runs on SQL Server and PostgreSQL, and EF Core cannot share one
  migration set between them. Every model change therefore needs two
  migrations — one per context — and the one that gets forgotten is the one
  that breaks a customer's upgrade. CI runs this after the build so a model
  change cannot merge with a stale set for either engine.

  Requires the dotnet-ef tool from .config/dotnet-tools.json (dotnet tool restore).
  Neither check opens a database connection: has-pending-model-changes compares
  the compiled model against the last snapshot.

.PARAMETER Configuration
  Build configuration the API was compiled with (default Release, matching CI).
  The script passes --no-build; build first.
#>
[CmdletBinding()]
param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "src/M365SecurityDashboard.Api/M365SecurityDashboard.Api.csproj"

$contexts = @(
  @{ Name = "SQL Server"; Context = "AppDbContext";         Dir = "Data/Migrations" },
  @{ Name = "Postgres";   Context = "PostgresAppDbContext"; Dir = "Data/Migrations/Postgres" }
)

$failed = $false
foreach ($c in $contexts) {
  Write-Host "Checking $($c.Name) migrations ($($c.Context))..."
  $output = & dotnet ef migrations has-pending-model-changes `
    --project $project --context $c.Context --configuration $Configuration --no-build 2>&1
  $text = ($output | Out-String).Trim()

  if ($LASTEXITCODE -eq 0) {
    Write-Host "  PASS  $text" -ForegroundColor Green
  } else {
    $failed = $true
    Write-Host "  FAIL  $text" -ForegroundColor Red
    Write-Host "        Add one:  dotnet ef migrations add <Name> --project src/M365SecurityDashboard.Api --context $($c.Context) --output-dir $($c.Dir)" -ForegroundColor Yellow
  }
}

if ($failed) {
  Write-Host "`nFAIL  A migration set is out of date. Model changes need a migration for EVERY engine." -ForegroundColor Red
  exit 1
}

Write-Host "`nPASS  Both migration sets match the model." -ForegroundColor Green
