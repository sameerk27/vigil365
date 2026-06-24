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
    [string]$Hostname,
    [int]$Port         = 5001,
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

# When a hostname is given, serve HTTPS on that name with a self-signed cert.
if ($Hostname) { $Url = "https://${Hostname}:${Port}" }

Write-Host "`n=== Vigil365 production deploy ===`n" -ForegroundColor Cyan

# 1. Publish if requested or if no artifact exists yet
$exe = Join-Path $PublishPath "M365SecurityDashboard.Api.exe"
if ($Publish -or -not (Test-Path $exe)) {
    Write-Host "[1/4] Publishing (running install.ps1)..." -ForegroundColor Yellow
    & (Join-Path $RepoRoot "install.ps1") -PublishPath $PublishPath
} else {
    Write-Host "[1/4] Using existing publish at $PublishPath" -ForegroundColor DarkGray
}

# 2. Prepare an HTTPS certificate. Production Kestrel does NOT auto-use the dev
#    cert, so we export a PFX and configure Kestrel to use it.
#    -Hostname  -> self-signed cert for that internal name (trusted for this user)
#    localhost  -> the .NET dev cert
$useHttps = $Url.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)
$pfxPath = Join-Path $PublishPath "vigil365-https.pfx"
$pfxPass = $null
if ($useHttps -and $Hostname) {
    Write-Host "[2/4] Creating self-signed certificate for '$Hostname'..." -ForegroundColor Yellow
    $pfxPass = [guid]::NewGuid().ToString("N")
    $sec = ConvertTo-SecureString $pfxPass -AsPlainText -Force
    $cert = New-SelfSignedCertificate -DnsName $Hostname -FriendlyName "Vigil365 $Hostname" `
        -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(2)
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $sec | Out-Null
    # Trust it for the current user so this machine's browsers accept it.
    $cerTmp = Join-Path $PublishPath "vigil365-host.cer"
    Export-Certificate -Cert $cert -FilePath $cerTmp | Out-Null
    Import-Certificate -FilePath $cerTmp -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
    Remove-Item $cerTmp -Force
    Write-Host "      Certificate created and trusted for the current user." -ForegroundColor Green

    # Map the hostname to localhost so the browser resolves it (needs admin to edit hosts).
    $hostsFile = "$env:SystemRoot\System32\drivers\etc\hosts"
    $hostsLine = "127.0.0.1`t$Hostname"
    try {
        if (-not (Select-String -Path $hostsFile -SimpleMatch $Hostname -Quiet)) {
            Add-Content -Path $hostsFile -Value $hostsLine -ErrorAction Stop
            Write-Host "      Added hosts entry: $hostsLine" -ForegroundColor Green
        } else { Write-Host "      Hosts entry for '$Hostname' already present." -ForegroundColor DarkGray }
    } catch {
        Write-Host "      Could not edit the hosts file (run as Administrator, or add manually):" -ForegroundColor DarkYellow
        Write-Host "        $hostsLine  ->  $hostsFile" -ForegroundColor DarkYellow
    }
} elseif ($useHttps) {
    Write-Host "[2/4] Preparing HTTPS dev certificate (localhost)..." -ForegroundColor Yellow
    try {
        dotnet dev-certs https --trust | Out-Null
        $pfxPass = [guid]::NewGuid().ToString("N")
        dotnet dev-certs https --export-path $pfxPath --password $pfxPass --format Pfx | Out-Null
        Write-Host "      Exported + trusted dev certificate." -ForegroundColor Green
    } catch {
        Write-Host "      Certificate prep failed. Use -Hostname, a reverse proxy, or a real cert." -ForegroundColor DarkYellow
        throw
    }
} else {
    Write-Host "[2/4] HTTP URL given; skipping certificate (use a proxy for TLS in prod)." -ForegroundColor DarkGray
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
if ($useHttps) {
    # Drive the HTTPS binding via Kestrel config (so we don't pass --urls).
    $config["Kestrel"] = [ordered]@{
        Endpoints = [ordered]@{
            Https = [ordered]@{
                Url = $Url
                Certificate = [ordered]@{ Path = "vigil365-https.pfx"; Password = $pfxPass }
            }
        }
    }
} else {
    # Plain HTTP behind a proxy — disable in-app HTTPS redirect to avoid loops.
    $config["Security"] = [ordered]@{ RequireHttps = $false }
}
$target = Join-Path $PublishPath "appsettings.Production.json"
$config | ConvertTo-Json -Depth 8 | Set-Content -Path $target -Encoding UTF8
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
    if ($useHttps) { & $exe } else { & $exe --urls $Url }
} finally { Pop-Location }
