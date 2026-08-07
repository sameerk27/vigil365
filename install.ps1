<#
.SYNOPSIS
  One-shot installer for Vigil365 - builds the frontend, publishes the API, and
  (optionally) installs it as a Windows Service. Graph credentials are entered
  later in the browser via the first-run setup wizard - no JSON editing required.

.EXAMPLE
  # Build + publish to .\publish and run interactively:
  .\install.ps1

  # Build, publish to a custom path, and install as a Windows service:
  .\install.ps1 -PublishPath C:\Apps\Vigil365 -InstallService -Url http://localhost:8080
#>
[CmdletBinding()]
param(
    [string]$PublishPath,
    [switch]$InstallService,
    [string]$Url = "http://localhost:8080",
    [string]$ServiceName = "Vigil365"
)

$ErrorActionPreference = "Stop"

# Resolve the repo root reliably. $PSScriptRoot is not always populated in the
# param() block, so compute it here and fall back to the invocation path.
$RepoRoot = $PSScriptRoot
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoRoot = (Resolve-Path $RepoRoot).Path

if (-not $PublishPath) { $PublishPath = Join-Path $RepoRoot "publish" }
$api    = Join-Path $RepoRoot "src\M365SecurityDashboard.Api"
$client = Join-Path $RepoRoot "src\m365-security-dashboard-client"

function Test-Tool($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$name' not found on PATH. $hint"
    }
}

Write-Host "`n=== Vigil365 installer ===`n" -ForegroundColor Cyan

# 1. Prerequisites
Write-Host "[1/4] Checking prerequisites..." -ForegroundColor Yellow
Test-Tool "dotnet" "Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
Test-Tool "npm"    "Install Node.js 20+: https://nodejs.org/"
Write-Host "      .NET and Node found." -ForegroundColor Green

# 2. Build the frontend into the API's wwwroot
Write-Host "[2/4] Building frontend..." -ForegroundColor Yellow
Push-Location $client
try {
    npm install --no-audit --no-fund
    npm run build
} finally { Pop-Location }
Write-Host "      Frontend built into wwwroot." -ForegroundColor Green

# 3. Publish the API
Write-Host "[3/4] Publishing API to $PublishPath ..." -ForegroundColor Yellow
dotnet publish $api -c Release -o $PublishPath | Out-Null
Write-Host "      Published." -ForegroundColor Green

# 4. Optionally install as a Windows service
$exe = Join-Path $PublishPath "M365SecurityDashboard.Api.exe"
if ($InstallService) {
    Write-Host "[4/4] Installing Windows service '$ServiceName'..." -ForegroundColor Yellow
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        sc.exe stop $ServiceName | Out-Null
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }
    $bin = "`"$exe`" --environment Production --urls $Url"
    sc.exe create $ServiceName binPath= $bin start= auto | Out-Null
    sc.exe start $ServiceName | Out-Null
    Write-Host "      Service '$ServiceName' installed and started on $Url." -ForegroundColor Green
} else {
    Write-Host "[4/4] Skipping service install (use -InstallService to enable)." -ForegroundColor DarkGray
}

# Next steps
Write-Host "`n=== Done ===`n" -ForegroundColor Cyan
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Make sure you have an Entra app registration (run register-app.ps1, or see README)."
Write-Host "  2. Set ConnectionStrings + AzureAd in appsettings.Production.json (DB + login)."
if (-not $InstallService) {
    Write-Host "  3. Start the app from the publish folder (the working directory must be" -ForegroundColor White
    Write-Host "     the publish folder so config + wwwroot resolve; the Windows service" -ForegroundColor White
    Write-Host "     handles this automatically):" -ForegroundColor White
    Write-Host "       cd '$PublishPath'" -ForegroundColor Gray
    Write-Host "       `$env:ASPNETCORE_ENVIRONMENT='Production'; .\M365SecurityDashboard.Api.exe --urls $Url" -ForegroundColor Gray
}
Write-Host "  4. Open $Url, sign in (first user becomes Admin), then use the" -ForegroundColor White
Write-Host "     in-app Setup wizard to enter your Graph credentials - no JSON editing." -ForegroundColor White
Write-Host ""
