<#
.SYNOPSIS
  Automated production run for Vigil365. Generates appsettings.Production.json,
  trusts a local HTTPS dev certificate, and starts the published app over HTTPS.
  Graph credentials are entered later in the browser via the setup wizard.

.NOTES
  Prerequisite you must do once in your tenant (cannot be automated locally):
    - Create an Entra app registration (or run register-app.ps1)
    - Add the -Url value below as a SPA redirect URI on that app registration

.EXAMPLE
  .\deploy.ps1 -TenantId <guid> -ClientId <guid> -AdminEmail you@contoso.com

.EXAMPLE
  # Re-publish first, custom URL + DB, then run:
  .\deploy.ps1 -TenantId <guid> -ClientId <guid> -AdminEmail you@contoso.com `
    -Publish -Url https://localhost:5001 -SqlServer ".\SQLEXPRESS"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TenantId,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$AdminEmail,
    [string]$Url       = "https://localhost:5001",
    [string]$SqlServer = ".\SQLEXPRESS",
    [string]$Database  = "M365SecurityDashboard",
    [string]$PublishPath,
    [switch]$Publish,
    [switch]$NoRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoRoot = (Resolve-Path $RepoRoot).Path
if (-not $PublishPath) { $PublishPath = Join-Path $RepoRoot "publish" }

Write-Host "`n=== Vigil365 production deploy ===`n" -ForegroundColor Cyan

# 1. Publish if requested or if no artifact exists yet
$exe = Join-Path $PublishPath "M365SecurityDashboard.Api.exe"
if ($Publish -or -not (Test-Path $exe)) {
    Write-Host "[1/4] Publishing (running install.ps1)..." -ForegroundColor Yellow
    & (Join-Path $RepoRoot "install.ps1") -PublishPath $PublishPath
} else {
    Write-Host "[1/4] Using existing publish at $PublishPath" -ForegroundColor DarkGray
}

# 2. Trust a local HTTPS dev certificate (Production enforces HTTPS)
Write-Host "[2/4] Ensuring a trusted HTTPS certificate..." -ForegroundColor Yellow
try {
    dotnet dev-certs https --trust | Out-Null
    Write-Host "      Dev certificate trusted." -ForegroundColor Green
} catch {
    Write-Host "      Could not auto-trust a dev cert. For real deployments use a proper" -ForegroundColor DarkYellow
    Write-Host "      certificate via reverse proxy or Kestrel (see README HTTPS section)." -ForegroundColor DarkYellow
}

# 3. Generate appsettings.Production.json (login + DB config; secrets stay out of source)
Write-Host "[3/4] Writing appsettings.Production.json..." -ForegroundColor Yellow
$conn = "Server=$SqlServer;Database=$Database;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True"
$config = [ordered]@{
    ConnectionStrings = [ordered]@{ DefaultConnection = $conn }
    AzureAd = [ordered]@{
        Instance = "https://login.microsoftonline.com/"
        TenantId = $TenantId
        ClientId = $ClientId
        Audience = "api://$ClientId"
    }
    Auth = [ordered]@{
        RedirectUri        = $Url
        BootstrapAdminEmail = $AdminEmail
    }
}
$target = Join-Path $PublishPath "appsettings.Production.json"
$config | ConvertTo-Json -Depth 6 | Set-Content -Path $target -Encoding UTF8
Write-Host "      Wrote $target" -ForegroundColor Green

# 4. Run (from the publish folder so config + wwwroot resolve)
if ($NoRun) {
    Write-Host "[4/4] -NoRun set; not starting the app." -ForegroundColor DarkGray
    Write-Host "`nReminder: add '$Url' as a SPA redirect URI on your Entra app registration.`n" -ForegroundColor White
    return
}

Write-Host "[4/4] Starting Vigil365 in Production on $Url ..." -ForegroundColor Yellow
Write-Host "      (Make sure '$Url' is a SPA redirect URI on your Entra app.)`n" -ForegroundColor DarkYellow
Push-Location $PublishPath
try {
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    & $exe --urls $Url
} finally { Pop-Location }
