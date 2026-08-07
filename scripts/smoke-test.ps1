<#
.SYNOPSIS
  Post-deploy smoke check for a running Vigil365 instance.

.DESCRIPTION
  Catches the deployment failures that unit tests structurally cannot:

    * a stale wwwroot - index.html referencing a bundle that is not on disk
      (this shipped twice before it was caught by hand)
    * assets returning 404
    * the API being up but the database being unreachable
    * auth regressions that leave a protected endpoint anonymous
    * unknown /api paths falling through to the SPA instead of 404ing

  Read-only: issues GETs only, never mutates the tenant or the database.

.EXAMPLE
  pwsh scripts/smoke-test.ps1 -BaseUrl https://vigil365.local:5001
#>
[CmdletBinding()]
param(
  [string]$BaseUrl = "https://vigil365.local:5001",
  # The dev/prod certificate is self-signed; skip validation for localhost checks.
  [switch]$SkipCertCheck = $true
)

$ErrorActionPreference = "Stop"
$script:Failures = 0

function Invoke-Check {
  param([string]$Name, [scriptblock]$Test)
  try {
    & $Test
    Write-Host "  PASS  $Name" -ForegroundColor Green
  } catch {
    $script:Failures++
    Write-Host "  FAIL  $Name" -ForegroundColor Red
    Write-Host "        $($_.Exception.Message)" -ForegroundColor DarkGray
  }
}

function Get-Url {
  param([string]$Path, [int[]]$AllowStatus = @(200))
  $params = @{ Uri = "$BaseUrl$Path"; Method = "GET"; UseBasicParsing = $true }
  if ($SkipCertCheck) { $params.SkipCertificateCheck = $true }
  try {
    $r = Invoke-WebRequest @params
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -and $AllowStatus -contains $code) { return @{ StatusCode = $code; Content = "" } }
    throw "GET $Path -> $(if ($code) { $code } else { $_.Exception.Message })"
  }
  if ($AllowStatus -notcontains $r.StatusCode) { throw "GET $Path -> $($r.StatusCode), expected $($AllowStatus -join '/')" }
  return @{ StatusCode = $r.StatusCode; Content = $r.Content }
}

Write-Host "Vigil365 smoke test -> $BaseUrl`n"

Invoke-Check "API is healthy and the database is reachable" {
  $body = (Get-Url "/health").Content | ConvertFrom-Json
  if ($body.status -eq "unhealthy") { throw "health reports unhealthy" }
  if (-not $body.checks.database.ok) { throw "database check failed: $($body.checks.database.error)" }
}

Invoke-Check "SPA shell is served" {
  $html = (Get-Url "/").Content
  if ($html -notmatch '<div id="root">') { throw "index.html has no #root mount point" }
}

Invoke-Check "index.html references a bundle that actually exists" {
  # The stale-wwwroot failure: publish leaves an index.html pointing at a hash
  # that was never copied, so the app serves a blank page.
  $html = (Get-Url "/").Content
  $assets = [regex]::Matches($html, '/assets/[A-Za-z0-9._-]+\.(?:js|css)') | ForEach-Object { $_.Value } | Select-Object -Unique
  if ($assets.Count -eq 0) { throw "index.html references no bundled assets" }
  foreach ($asset in $assets) {
    $r = Get-Url $asset
    if ($r.StatusCode -ne 200) { throw "$asset -> $($r.StatusCode)" }
  }
  Write-Host "        verified $($assets.Count) asset(s)" -ForegroundColor DarkGray
}

Invoke-Check "pre-paint display preferences script is served" {
  Get-Url "/display-prefs.js" | Out-Null
}

Invoke-Check "protected endpoints reject anonymous callers" {
  foreach ($path in @("/api/dashboard/overview", "/api/alert-policies", "/api/api-tokens")) {
    $r = Get-Url $path -AllowStatus @(401, 403)
    if ($r.StatusCode -notin 401, 403) { throw "$path was reachable anonymously ($($r.StatusCode))" }
  }
}

Invoke-Check "SIEM endpoints reject a forged API token" {
  $params = @{ Uri = "$BaseUrl/api/siem/alerts"; Method = "GET"; UseBasicParsing = $true
               Headers = @{ Authorization = "Bearer vig_not_a_real_token" } }
  if ($SkipCertCheck) { $params.SkipCertificateCheck = $true }
  try {
    Invoke-WebRequest @params | Out-Null
    throw "a forged token was accepted"
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -ne 401) { throw "expected 401 for a forged token, got $code" }
  }
}

Invoke-Check "unknown /api paths 404 as JSON instead of serving the SPA" {
  $r = Get-Url "/api/definitely-not-an-endpoint" -AllowStatus @(404)
  if ($r.StatusCode -ne 404) { throw "expected 404, got $($r.StatusCode)" }
}

Write-Host ""
if ($script:Failures -gt 0) {
  Write-Host "$($script:Failures) check(s) failed." -ForegroundColor Red
  exit 1
}
Write-Host "All checks passed." -ForegroundColor Green

