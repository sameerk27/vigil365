<#
.SYNOPSIS
  Trusts the self-signed certificate on THIS machine so the browser stops
  warning. Testing convenience only.

.DESCRIPTION
  This does not make the certificate valid - it tells this one computer to
  accept it. Every other visitor still gets the warning, which on a security
  product trains people to click through TLS warnings. Use it to test locally,
  then replace the certificate with a real one (see deploy-public.ps1 header).

  Reads the PFX password from appsettings.Production.json so you do not have to
  handle it. Run elevated to install for all users; without elevation it lands
  in the current user's store, which is enough for your own browser.

.EXAMPLE
  pwsh -File scripts/trust-local-cert.ps1
#>
[CmdletBinding()]
param(
  [string]$PublishPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "publish")
)

$ErrorActionPreference = "Stop"

$cfgPath = Join-Path $PublishPath "appsettings.Production.json"
if (-not (Test-Path $cfgPath)) { throw "No appsettings.Production.json at $cfgPath - run deploy-public.ps1 first." }

$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$certNode = $cfg.Kestrel.Endpoints.Https.Certificate
if (-not $certNode.Path) { throw "No certificate configured in $cfgPath." }

$pfx = Join-Path $PublishPath $certNode.Path
if (-not (Test-Path $pfx)) { throw "Certificate file not found: $pfx" }

$loaded = New-Object Security.Cryptography.X509Certificates.X509Certificate2 `
    $pfx, $certNode.Password, "MachineKeySet,PersistKeySet"

if ($loaded.Subject -ne $loaded.Issuer) {
  Write-Host "This certificate is NOT self-signed - it is issued by:" -ForegroundColor Yellow
  Write-Host "  $($loaded.Issuer)" -ForegroundColor Yellow
  Write-Host "If browsers still warn, the chain is likely incomplete rather than untrusted." -ForegroundColor Yellow
  exit 0
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$storeLocation = if ($admin) { "LocalMachine" } else { "CurrentUser" }

$store = New-Object Security.Cryptography.X509Certificates.X509Store "Root", $storeLocation
$store.Open("ReadWrite")
$store.Add($loaded)
$store.Close()

Write-Host "`nTrusted '$($loaded.Subject)' in $storeLocation\Root (expires $($loaded.NotAfter))." -ForegroundColor Green
Write-Host "Fully close and reopen the browser - TLS decisions are cached per session.`n"
Write-Host "Remember: only THIS machine trusts it. Replace with a real certificate before anyone else uses the site." -ForegroundColor Yellow

