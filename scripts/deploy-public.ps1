<#
.SYNOPSIS
  Serves Vigil365 on a real public hostname over HTTPS on port 443.

.DESCRIPTION
  deploy.ps1 targets a LOCAL install: a hosts-file alias, a self-signed
  certificate, and a high port. This script is the public counterpart — it binds
  every interface on 443 with a real certificate and opens the Windows firewall.

  READ THIS FIRST. Publishing Vigil365 to the internet changes its threat model:
  the README's "no inbound exposure by default" no longer holds, /health and
  /api/auth/config answer anonymously (exposing collection state and your tenant
  and client ids), and the application has never had a third-party penetration
  test. Prefer restricting inbound 443 to known source IPs, or fronting this with
  a reverse proxy / WAF, rather than opening it to the world.

  Must be run from an ELEVATED PowerShell: binding 443 and writing firewall
  rules both require administrator rights.

.PARAMETER PfxPath
  A real certificate for the public hostname. Without it the script falls back to
  a self-signed certificate so you can verify the plumbing, but every visitor
  will see a browser warning — do not leave that in place.

  To get a real certificate, either:
    win-acme   https://www.win-acme.com   (interactive, HTTP-01, needs port 80 in)
    certbot    https://certbot.eff.org    (DNS-01 works behind a closed port 80)

.EXAMPLE
  pwsh -File scripts/deploy-public.ps1 -Hostname vigil365.in -PfxPath C:\certs\vigil365.pfx -PfxPassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$Hostname,
  [int]$Port = 443,
  [string]$PfxPath,
  [System.Security.SecureString]$PfxPassword,
  [string]$PublishPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "publish"),
  [switch]$SkipFirewall,
  [switch]$NoRun
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$fail = 0

function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "   OK   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "   WARN $msg" -ForegroundColor Yellow }
function Bad($msg)  { $script:fail++; Write-Host "   FAIL $msg" -ForegroundColor Red }

Step "Preflight"

# Elevation — binding 443 and firewall changes both need it.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) { Ok "running elevated" } else { Bad "not elevated — re-run PowerShell as Administrator" }

# DNS must point at this connection, or nobody reaches the app.
try {
  $resolved = (Resolve-DnsName $Hostname -Type A -ErrorAction Stop |
               Where-Object { $_.IPAddress } | Select-Object -First 1).IPAddress
  $public = (Invoke-RestMethod -Uri "https://api.ipify.org?format=json" -TimeoutSec 8).ip
  if ($resolved -eq $public) { Ok "$Hostname -> $resolved (matches this connection)" }
  else { Bad "$Hostname resolves to $resolved but this connection is $public — update the A record" }
  Warn "residential IPs usually change; the A record will go stale unless it is static or dynamic-DNS updated"
} catch {
  Bad "could not verify DNS: $($_.Exception.Message)"
}

# Port must be free. Note netstat also lists OUTBOUND :443 connections — only a
# LISTENING socket is a conflict.
$listening = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($listening) { Bad "port $Port already has a listener (PID $($listening[0].OwningProcess))" }
else { Ok "port $Port is free" }

if (-not (Test-Path (Join-Path $PublishPath "M365SecurityDashboard.Api.exe"))) {
  Bad "no published build at $PublishPath — run: dotnet publish src/M365SecurityDashboard.Api -c Release -o publish"
} else { Ok "published build found" }

if ($fail -gt 0) { Write-Host "`n$fail preflight check(s) failed. Nothing was changed.`n" -ForegroundColor Red; exit 1 }

Step "Certificate"
$pfxOut = Join-Path $PublishPath "vigil365-public.pfx"
$pfxPlain = $null

if ($PfxPath) {
  if (-not (Test-Path $PfxPath)) { Bad "PFX not found: $PfxPath"; exit 1 }
  Copy-Item $PfxPath $pfxOut -Force
  if ($PfxPassword) {
    $pfxPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
  }
  Ok "using supplied certificate"
} else {
  Warn "no -PfxPath given — generating a SELF-SIGNED certificate"
  Warn "every visitor will get a browser trust warning; replace before real use"
  $pfxPlain = [Guid]::NewGuid().ToString("N")
  $cert = New-SelfSignedCertificate -DnsName $Hostname -FriendlyName "Vigil365 $Hostname" `
      -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable `
      -NotAfter (Get-Date).AddYears(1)
  Export-PfxCertificate -Cert $cert -FilePath $pfxOut `
      -Password (ConvertTo-SecureString -String $pfxPlain -Force -AsPlainText) | Out-Null
  Ok "self-signed certificate written to $pfxOut"
}

Step "Configuration"
$url = if ($Port -eq 443) { "https://$Hostname" } else { "https://${Hostname}:$Port" }
$cfgPath = Join-Path $PublishPath "appsettings.Production.json"

# Preserve everything already configured (connection string, AzureAd, admin
# email) and change only what public hosting requires.
$cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }

# Bind every interface — 127.0.0.1 would be unreachable from outside.
$cfg | Add-Member -NotePropertyName Kestrel -NotePropertyValue ([pscustomobject]@{
  Endpoints = [pscustomobject]@{
    Https = [pscustomobject]@{
      Url = "https://0.0.0.0:$Port"
      Certificate = [pscustomobject]@{ Path = (Split-Path -Leaf $pfxOut); Password = $pfxPlain }
    }
  }
}) -Force

if (-not $cfg.Auth) { $cfg | Add-Member -NotePropertyName Auth -NotePropertyValue ([pscustomobject]@{}) -Force }
$cfg.Auth | Add-Member -NotePropertyName RedirectUri -NotePropertyValue $url -Force

$cfg | Add-Member -NotePropertyName Cors -NotePropertyValue ([pscustomobject]@{
  AllowedOrigins = @($url)
}) -Force

$cfg | ConvertTo-Json -Depth 8 | Set-Content $cfgPath -Encoding UTF8
Ok "wrote $cfgPath (bind 0.0.0.0:$Port, redirect $url)"

Step "Firewall"
if ($SkipFirewall) {
  Warn "skipped by request — inbound $Port must be allowed some other way"
} else {
  $ruleName = "Vigil365 HTTPS $Port"
  Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
      -Protocol TCP -LocalPort $Port -Profile Any | Out-Null
  Ok "inbound TCP $Port allowed"
  Warn "this allows the whole internet; restrict with -RemoteAddress on the rule if you can"
}

Write-Host @"

Still required, and only you can do these:

  1. Router: forward external TCP $Port to this machine (192.168.x.x) on $Port.
     Without it the port is open on Windows but unreachable from outside.

  2. Entra: add "$url" as a SPA redirect URI on app registration
     $(if ($cfg.AzureAd.ClientId) { $cfg.AzureAd.ClientId } else { "<your client id>" }).
     Sign-in fails with AADSTS50011 until this exists.

  3. Certificate: replace the self-signed PFX with a real one if you used the
     fallback, then re-run this script with -PfxPath.

"@ -ForegroundColor Cyan

if ($NoRun) { Write-Host "-NoRun set; not starting.`n"; exit 0 }

Step "Starting"
Push-Location $PublishPath
try {
  $env:ASPNETCORE_ENVIRONMENT = "Production"
  Write-Host "   $url`n" -ForegroundColor Green
  & (Join-Path $PublishPath "M365SecurityDashboard.Api.exe")
} finally { Pop-Location }
