<#
.SYNOPSIS
  Requests a real, publicly-trusted TLS certificate for Vigil365 from Let's
  Encrypt using lego, and emits a .pfx that deploy-public.ps1 can consume.

.DESCRIPTION
  This replaces the self-signed certificate that deploy-public.ps1 falls back to.
  Self-signed means every visitor sees a browser warning, which on a security
  product trains people to click through TLS warnings — so it is not a resting
  state, it is a placeholder.

  Three validation methods:

    dns  (default)  Solves DNS-01. You paste a TXT record into your DNS zone when
                    prompted. Needs NO inbound ports and NO firewall changes, so
                    it works behind CGNAT and with port 80 closed. INTERACTIVE —
                    lego waits on stdin, so run this yourself in a real terminal.
                    Cannot auto-renew: you repeat this every ~90 days.

    godaddy         Same DNS-01 challenge, but lego creates and deletes the TXT
                    record itself through GoDaddy's API. No manual step and
                    renewals are unattended. Needs GODADDY_API_KEY and
                    GODADDY_API_SECRET in the environment. GoDaddy restricts this
                    API to accounts with 10+ domains or a Discount Domain Club
                    plan; smaller accounts get 403 and must use -Method dns.

    http            Solves HTTP-01. lego binds port 80 and Let's Encrypt calls
                    back. Fully automatable for renewals, but port 80 must be
                    reachable from the internet. On an IPv6-only path (no A
                    record) Let's Encrypt validates over IPv6 — your router and
                    Windows firewall must both allow inbound TCP 80. Requires
                    elevation to bind 80.

  Certificate order of operations:
    1. pwsh -File scripts/request-cert.ps1 -Hostname vigil365.in -Email you@example.com
    2. deploy-public.ps1 with the -PfxPath / -PfxPassword it prints

.PARAMETER Staging
  Use the Let's Encrypt staging CA. The resulting certificate is NOT trusted by
  browsers — it only proves the validation plumbing works. Worth it before a
  first HTTP-01 attempt; less worth it for DNS-01, where the manual TXT step is
  the toil and you would just do it twice.

.EXAMPLE
  pwsh -File scripts/request-cert.ps1 -Hostname vigil365.in -Email you@example.com

.EXAMPLE
  pwsh -File scripts/request-cert.ps1 -Hostname vigil365.in -Email you@example.com -Method http
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$Hostname,
  [Parameter(ParameterSetName = "Issue", Mandatory)] [string]$Email,
  [ValidateSet("dns", "godaddy", "http")] [string]$Method = "dns",
  [switch]$Staging,
  [int]$PropagationTimeout = 600,
  [Parameter(ParameterSetName = "Check", Mandatory)] [switch]$CheckTxt,
  [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "certs")
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "   OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "   WARN $m" -ForegroundColor Yellow }
function Bad($m)  { Write-Host "   FAIL $m" -ForegroundColor Red }

# Asks the zone's AUTHORITATIVE nameservers, not a recursive resolver. A cached
# NXDOMAIN from a resolver looks identical to a record that was never saved, and
# only the authoritative answer distinguishes "not there yet" from "not there".
function Get-AcmeTxt {
  param([string]$Zone)
  $name = "_acme-challenge.$Zone"
  $out = [ordered]@{ Name = $name; Servers = @() }
  $ns = @()
  try {
    $ns = Resolve-DnsName $Zone -Type NS -Server 8.8.8.8 -ErrorAction Stop |
          Where-Object { $_.Type -eq "NS" } | Select-Object -ExpandProperty NameHost
  } catch { }
  foreach ($n in $ns) {
    $ip = $null
    try {
      $ip = Resolve-DnsName $n -Type A -Server 8.8.8.8 -ErrorAction Stop |
            Where-Object { $_.Type -eq "A" } | Select-Object -First 1 -ExpandProperty IPAddress
    } catch { }
    if (-not $ip) { continue }
    $vals = @()
    try {
      $vals = Resolve-DnsName $name -Type TXT -Server $ip -ErrorAction Stop |
              Where-Object { $_.Type -eq "TXT" } | ForEach-Object { $_.Strings -join "" }
    } catch { }
    $out.Servers += [pscustomobject]@{ Host = $n; Ip = $ip; Values = $vals }
  }
  [pscustomobject]$out
}

if ($CheckTxt) {
  Step "Checking _acme-challenge.$Hostname on the authoritative nameservers"
  $res = Get-AcmeTxt -Zone $Hostname
  if ($res.Servers.Count -eq 0) { throw "Could not determine the authoritative nameservers for $Hostname." }
  $found = $false
  foreach ($s in $res.Servers) {
    if ($s.Values.Count -gt 0) { $found = $true; foreach ($v in $s.Values) { Ok "$($s.Host)  TXT `"$v`"" } }
    else { Bad "$($s.Host)  no TXT record" }
  }
  if ($found) {
    Write-Host "`nRecord is live. Press Enter in the lego window now.`n" -ForegroundColor Green
  } else {
    Write-Host @"

Not published yet. Either it has not saved, or it is still propagating.

In GoDaddy's DNS manager the Name field must be exactly:

    _acme-challenge

NOT the full _acme-challenge.$Hostname — GoDaddy appends the zone for you, so
pasting the FQDN creates _acme-challenge.$Hostname.$Hostname instead.

The Value is the long string lego printed, with NO surrounding quotes.

"@ -ForegroundColor Yellow
  }
  exit ($(if ($found) { 0 } else { 1 }))
}

Step "Locating lego"

# winget puts lego on PATH, but only for shells started AFTER the install —
# hence also probing the package directory.
$lego = (Get-Command lego -ErrorAction SilentlyContinue).Source
if (-not $lego) {
  $lego = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages") `
            -Recurse -Filter "lego.exe" -ErrorAction SilentlyContinue |
          Select-Object -First 1 -ExpandProperty FullName
}
if (-not $lego) {
  throw @"
lego not found. Install it, then open a NEW terminal (PATH is only refreshed for
new shells):

    winget install GoACME.lego
"@
}
Ok "$lego"
Ok "version $((& $lego --version) -replace '^lego version\s*','')"

Step "Preflight"

# No AAAA/A record means nothing Let's Encrypt does can succeed, whichever
# challenge you pick — DNS-01 still requires the name to resolve for issuance.
$records = @()
foreach ($t in @("A", "AAAA")) {
  try { $records += Resolve-DnsName -Name $Hostname -Type $t -ErrorAction Stop |
                    Where-Object { $_.Type -eq $t } } catch { }
}
if ($records.Count -eq 0) {
  throw "$Hostname has no A or AAAA record. Point DNS at this connection first."
}
foreach ($r in $records) { Ok "$($r.Type) -> $($r.IPAddress)" }

if ($Method -eq "http") {
  $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $admin) { throw "HTTP-01 binds port 80 — re-run from an ELEVATED PowerShell." }
  Ok "running elevated"

  if (-not ($records | Where-Object { $_.Type -eq "A" })) {
    Warn "No A record — Let's Encrypt will validate over IPv6."
    Warn "Inbound TCP 80 must be open on BOTH the router and the Windows firewall."
  }

  # A listener already on 80 means lego cannot bind it; better to say so now
  # than to burn a failed-validation against the rate limit.
  $busy = Get-NetTCPConnection -LocalPort 80 -State Listen -ErrorAction SilentlyContinue
  if ($busy) { throw "Port 80 is already in use (PID $($busy[0].OwningProcess)). Stop it, or use -Method dns." }
  Ok "port 80 free"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Random per-issuance password. It ends up in appsettings.Production.json in
# plaintext (Kestrel needs to read it unattended), so it protects the .pfx at
# rest and in transit, not against someone who already has the published folder.
$pfxPassword = [Guid]::NewGuid().ToString("N")

Step "Requesting certificate from Let's Encrypt"
Write-Host "   hostname   $Hostname"
$methodLabel = switch ($Method) {
  "dns"     { "DNS-01 (manual TXT)" }
  "godaddy" { "DNS-01 (GoDaddy API — automatic)" }
  "http"    { "HTTP-01 (port 80)" }
}
Write-Host "   method     $methodLabel"
Write-Host "   CA         $(if ($Staging) { 'STAGING — result will NOT be trusted' } else { 'production' })"

# Flag placement matters: lego 5.x global flags are only --help/--version/
# --log.*/--config. Everything else belongs to the `run` SUBCOMMAND and must
# appear AFTER it. `lego --email ... run` fails with "flag provided but not
# defined: -email".
$legoArgs = @("--log.level", "info", "run",
              "--accept-tos",
              "--email", $Email,
              "--domains", $Hostname,
              "--path", $OutDir,
              "--pfx",
              "--pfx.format", "SHA256",   # default is RC2, which modern Windows resists loading
              "--pfx.password", $pfxPassword)

if ($Staging) { $legoArgs += @("--server", "letsencrypt-staging") }

switch ($Method) {
  "dns" {
    $legoArgs += @("--dns", "manual")

    # lego's manual provider polls for only 60s by default. GoDaddy's minimum
    # TTL is 600s and its edge takes minutes to converge, so the default loses
    # the race even when the record was saved correctly — the failure then reads
    # as "time limit exceeded", which looks like a DNS fault rather than a
    # too-short timeout. Give it room.
    $env:MANUAL_PROPAGATION_TIMEOUT = "$PropagationTimeout"
    $env:MANUAL_POLLING_INTERVAL    = "5"

    Write-Host @"

   lego will print a TXT record, then wait. In GoDaddy's DNS manager:

       Type   TXT
       Name   _acme-challenge          <- just this, GoDaddy appends the zone
       Value  <the long string lego prints, no quotes>
       TTL    600 (the minimum GoDaddy accepts)

   Save it, then confirm it is actually live from a SECOND terminal:

       pwsh -File scripts/request-cert.ps1 -Hostname $Hostname -CheckTxt

   Only press Enter in the lego window once that reports the record.
   After you press Enter lego keeps polling for up to $PropagationTimeout seconds.

"@ -ForegroundColor Yellow
  }

  "godaddy" {
    # lego reads these itself; the script only fails fast if they are absent so
    # the run does not burn a Let's Encrypt failed-validation to tell you.
    if (-not $env:GODADDY_API_KEY -or -not $env:GODADDY_API_SECRET) {
      throw @"
GoDaddy API credentials not set. Create a PRODUCTION key at
https://developer.godaddy.com/keys then, in this terminal:

    `$env:GODADDY_API_KEY    = '<key>'
    `$env:GODADDY_API_SECRET = '<secret>'

Note: since 2024 GoDaddy restricts this API to accounts holding 10+ domains or
a Discount Domain Club plan. A single-domain account gets 403 ACCESS_DENIED —
if that happens, fall back to -Method dns.
"@
    }
    $legoArgs += @("--dns", "godaddy")
    $env:GODADDY_PROPAGATION_TIMEOUT = "$PropagationTimeout"
    Ok "GODADDY_API_KEY / GODADDY_API_SECRET present"
    Write-Host "   No manual step — lego creates and removes the TXT record itself." -ForegroundColor Green
  }

  "http" {
    $legoArgs += @("--http", "--http.address", ":80")
  }
}

& $lego @legoArgs
if ($LASTEXITCODE -ne 0) {
  throw "lego exited $LASTEXITCODE — certificate NOT issued. Nothing was changed."
}

$pfx = Join-Path $OutDir "certificates\$Hostname.pfx"
if (-not (Test-Path $pfx)) {
  # lego sanitises wildcards into a leading underscore.
  $pfx = Get-ChildItem (Join-Path $OutDir "certificates") -Filter "*.pfx" |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $pfx -or -not (Test-Path $pfx)) { throw "lego reported success but no .pfx was found under $OutDir\certificates." }

Step "Verifying the issued certificate"
$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $pfx, $pfxPassword
Ok "subject  $($cert.Subject)"
Ok "issuer   $($cert.Issuer)"
Ok "expires  $($cert.NotAfter)"
if ($cert.Subject -eq $cert.Issuer) { Warn "Self-signed — this is not a CA-issued certificate." }

Write-Host "`n== Next step" -ForegroundColor Cyan
Write-Host @"
Install it (ELEVATED PowerShell — binding 443 needs administrator):

    pwsh -File scripts/deploy-public.ps1 ``
        -Hostname $Hostname ``
        -PfxPath '$pfx' ``
        -PfxPassword (ConvertTo-SecureString '$pfxPassword' -AsPlainText -Force)

Then fully close and reopen the browser — TLS decisions are cached per session.

If you previously ran trust-local-cert.ps1, remove the self-signed certificate
from your Root store afterwards; leaving it trusted means a stale certificate for
this hostname stays valid on this machine.
"@
if ($Staging) {
  Warn "`nThis is a STAGING certificate. Browsers will still warn. Re-run without -Staging for a real one."
}
if ($Method -eq "dns") {
  Warn "`nDNS-01 manual does not auto-renew. This certificate expires $($cert.NotAfter.ToString('yyyy-MM-dd')) — repeat then."
  Warn "To make renewals unattended, use -Method godaddy (your zone is on GoDaddy) or -Method http."
}
if ($Method -ne "dns") {
  Write-Host "`nRenewable unattended: re-run the same command. Schedule it well before $($cert.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Green
}
