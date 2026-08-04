<#
.SYNOPSIS
  Builds Vigil365-Setup.exe — a single self-contained installer.

.DESCRIPTION
  This is a RELEASE-time script, run by whoever ships Vigil365. It does all the
  building here so the customer's server does not have to: the published
  application is compressed and embedded inside the installer executable.

  The result needs nothing on the target machine — no source tree, no Node, no
  .NET SDK, not even the .NET runtime. Both the application and the installer
  are published self-contained.

  What the customer still needs is Azure CLI, and only because the wizard
  registers the Entra application for them. The wizard installs it if missing.

.PARAMETER SkipClient
  Reuse the existing wwwroot instead of running npm. Only for iterating on the
  installer itself — a shipped build must never skip it, or the SPA in the
  payload is whatever happened to be lying around.

.EXAMPLE
  pwsh -File scripts/build-installer.ps1
#>
[CmdletBinding()]
param(
  [switch]$SkipClient,
  [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist")
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$installerProj = Join-Path $repo "src\M365SecurityDashboard.GuiInstaller"
$apiProj = Join-Path $repo "src\M365SecurityDashboard.Api\M365SecurityDashboard.Api.csproj"
$clientDir = Join-Path $repo "src\m365-security-dashboard-client"

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "   OK   $m" -ForegroundColor Green }

$staging = Join-Path ([IO.Path]::GetTempPath()) "vigil365-payload"
$payload = Join-Path $installerProj "payload.zip"

Step "Building the SPA"
if ($SkipClient) {
  Write-Host "   skipped (-SkipClient) — payload will reuse the existing wwwroot" -ForegroundColor Yellow
} else {
  Push-Location $clientDir
  try {
    # `ci` not `install`: a shipped artifact should be built from the lockfile,
    # not from whatever the ranges happen to resolve to today.
    if (Test-Path (Join-Path $clientDir "package-lock.json")) { npm ci --no-audit --no-fund }
    else { npm install --no-audit --no-fund }
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
  } finally { Pop-Location }
  Ok "vite build -> src/M365SecurityDashboard.Api/wwwroot"
}

$indexPath = Join-Path $repo "src\M365SecurityDashboard.Api\wwwroot\index.html"
if (-not (Test-Path $indexPath)) { throw "No wwwroot/index.html — the SPA did not build, so the payload would ship without a UI." }

Step "Publishing the application (self-contained)"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
# Self-contained so the target server needs no .NET at all. NOT trimmed: EF Core
# and the config binder resolve types by reflection, and trimming silently
# removes them — the failure shows up at runtime, not here.
dotnet publish $apiProj -c Release -r win-x64 --self-contained true -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the API" }

if (-not (Test-Path (Join-Path $staging "wwwroot\index.html"))) { throw "Published output has no wwwroot — refusing to ship a payload with no UI." }
if (-not (Test-Path (Join-Path $staging "hostfxr.dll")))        { throw "Published output is not self-contained (no hostfxr.dll)." }

# appsettings.Production.json is written by the wizard at install time from the
# customer's answers. Shipping one would overwrite theirs on every upgrade.
Remove-Item (Join-Path $staging "appsettings.Production.json") -Force -ErrorAction SilentlyContinue

Ok ("{0} files, {1:N1} MB" -f (Get-ChildItem $staging -Recurse -File).Count,
                              ((Get-ChildItem $staging -Recurse | Measure-Object Length -Sum).Sum / 1MB))

Step "Compressing the payload"
Remove-Item $payload -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $payload -CompressionLevel Optimal
Ok ("payload.zip  {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))

Step "Building the installer"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
# Self-contained and single-file so the customer double-clicks one thing and it
# runs on a server with no .NET installed.
dotnet publish (Join-Path $installerProj "M365SecurityDashboard.GuiInstaller.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the installer" }

$exe = Join-Path $OutDir "M365SecurityDashboard.GuiInstaller.exe"
if (-not (Test-Path $exe)) { throw "Installer executable was not produced." }

$final = Join-Path $OutDir "Vigil365-Setup.exe"
Move-Item $exe $final -Force

# Loose files beside a single-file exe invite shipping the wrong thing.
Get-ChildItem $OutDir -File | Where-Object { $_.Name -ne "Vigil365-Setup.exe" } | Remove-Item -Force
Remove-Item $payload -Force -ErrorAction SilentlyContinue
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n== Done" -ForegroundColor Cyan
Ok ("{0}  ({1:N1} MB)" -f $final, ((Get-Item $final).Length / 1MB))
Write-Host @"

Ship that one file. On the target server it needs no source tree, no Node, and
no .NET — it carries the application and the runtime with it.

It must be run as Administrator: it registers a Windows service, creates a SQL
login, and may install a certificate.
"@
