<#
.SYNOPSIS
  Sets the release version in both places at once.

.DESCRIPTION
  Updating the API's <Version> and the client's package.json by hand is how they
  drift. This writes both, then verifies, so cutting a release is one command.

.EXAMPLE
  pwsh scripts/set-version.ps1 1.1.0
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$Version
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$csprojPath = Join-Path $repo "src/M365SecurityDashboard.Api/M365SecurityDashboard.Api.csproj"
$installerCsprojPath = Join-Path $repo "src/M365SecurityDashboard.GuiInstaller/M365SecurityDashboard.GuiInstaller.csproj"
$packagePath = Join-Path $repo "src/m365-security-dashboard-client/package.json"

# Targeted replacements - a full XML/JSON round-trip would reformat the files.
$csproj = Get-Content $csprojPath -Raw
$updated = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
if ($updated -eq $csproj) { throw "No <Version> element found in $csprojPath" }
Set-Content $csprojPath $updated -NoNewline

# The installer exe carries its own <Version> (shown in the file's Properties);
# keep it in step so the shipped Vigil365-Setup.exe reports the release version.
$installer = Get-Content $installerCsprojPath -Raw
$updatedInstaller = [regex]::Replace($installer, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
if ($updatedInstaller -eq $installer) { throw "No <Version> element found in $installerCsprojPath" }
Set-Content $installerCsprojPath $updatedInstaller -NoNewline

$package = Get-Content $packagePath -Raw
$updatedPkg = [regex]::Replace($package, '"version":\s*"[^"]*"', """version"": ""$Version""", 1)
if ($updatedPkg -eq $package) { throw "No version field found in $packagePath" }
Set-Content $packagePath $updatedPkg -NoNewline

# package-lock.json repeats the version twice (top level and the root package
# entry); left alone it drifts from package.json until the next npm install.
$lockPath = Join-Path (Split-Path $packagePath) "package-lock.json"
if (Test-Path $lockPath) {
  $lock = Get-Content $lockPath -Raw
  # Instance Replace: its 3rd argument really is a count (the static overload's
  # 4th is RegexOptions), so only the first two - ours, not the dependencies'.
  $lockRx = [regex]'"version":\s*"[^"]*"'
  Set-Content $lockPath ($lockRx.Replace($lock, """version"": ""$Version""", 2)) -NoNewline
}

Write-Host "Set version to $Version in the API, installer and client.`n" -ForegroundColor Green
& (Join-Path $PSScriptRoot "check-version.ps1")

Write-Host "`nNext: update CHANGELOG.md, commit, then tag:" -ForegroundColor Cyan
Write-Host "  git tag -a v$Version -m ""Vigil365 v$Version""" -ForegroundColor Cyan

