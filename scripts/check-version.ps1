<#
.SYNOPSIS
  Fails if the API and client versions have drifted apart.

.DESCRIPTION
  The version is shown in the sidebar, on the login screen, in /health, and in
  exported policy packs. Two independent declarations (the API's <Version> and
  the client's package.json "version") will eventually disagree, and a support
  report naming the wrong build is worse than no version at all. CI runs this so
  a release cannot ship mismatched numbers.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$csprojPath = Join-Path $repo "src/M365SecurityDashboard.Api/M365SecurityDashboard.Api.csproj"
$packagePath = Join-Path $repo "src/m365-security-dashboard-client/package.json"

$apiVersion = ([xml](Get-Content $csprojPath)).Project.PropertyGroup.Version | Where-Object { $_ }
$clientVersion = (Get-Content $packagePath -Raw | ConvertFrom-Json).version

if (-not $apiVersion) {
  Write-Host "FAIL  No <Version> declared in $csprojPath" -ForegroundColor Red
  exit 1
}

Write-Host "API    (csproj)       $apiVersion"
Write-Host "Client (package.json) $clientVersion"

if ($apiVersion -ne $clientVersion) {
  Write-Host "`nFAIL  Versions differ. Update both, or run scripts/set-version.ps1 <version>." -ForegroundColor Red
  exit 1
}

Write-Host "`nPASS  Versions match ($apiVersion)." -ForegroundColor Green

