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
$packagePath = Join-Path $repo "src/m365-security-dashboard-client/package.json"

# Targeted replacements - a full XML/JSON round-trip would reformat the files.
$csproj = Get-Content $csprojPath -Raw
$updated = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
if ($updated -eq $csproj) { throw "No <Version> element found in $csprojPath" }
Set-Content $csprojPath $updated -NoNewline

$package = Get-Content $packagePath -Raw
$updatedPkg = [regex]::Replace($package, '"version":\s*"[^"]*"', """version"": ""$Version""", 1)
if ($updatedPkg -eq $package) { throw "No version field found in $packagePath" }
Set-Content $packagePath $updatedPkg -NoNewline

Write-Host "Set version to $Version in both projects.`n" -ForegroundColor Green
& (Join-Path $PSScriptRoot "check-version.ps1")

Write-Host "`nNext: update CHANGELOG.md, commit, then tag:" -ForegroundColor Cyan
Write-Host "  git tag -a v$Version -m ""Vigil365 v$Version""" -ForegroundColor Cyan

