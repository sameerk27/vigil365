<#
.SYNOPSIS
  Installs Vigil365 as a managed Windows production service.

.DESCRIPTION
  This installer is intentionally designed for a server deployment: SQL Server
  and TLS are external dependencies. It publishes the application, writes only
  non-Graph bootstrap configuration, restricts local file permissions, and
  installs a Windows service configured to restart after failures.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TenantId,
    [string]$ClientId,
    [string]$AdminEmail,
    [string]$SqlConnectionString,
    [string]$PublicUrl,
    [string]$InstallPath = "C:\Program Files\Vigil365",
    [string]$ServiceName = "Vigil365",
    [int]$Port = 8080
)

$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer from an elevated PowerShell session."
}
$repoRoot = (Resolve-Path $PSScriptRoot).Path
$api = Join-Path $repoRoot "src\M365SecurityDashboard.Api"
$client = Join-Path $repoRoot "src\m365-security-dashboard-client"
$exe = Join-Path $InstallPath "M365SecurityDashboard.Api.exe"

foreach ($tool in "dotnet", "npm") { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required tool '$tool' is not available on PATH." } }
function Read-Required([string]$Name, [string]$Value) {
    if ($Value) { return $Value }
    do { $Value = Read-Host $Name } while ([string]::IsNullOrWhiteSpace($Value))
    return $Value.Trim()
}
Write-Host "`nVigil365 enterprise installer" -ForegroundColor Cyan
$TenantId = Read-Required "Entra Tenant ID" $TenantId
$ClientId = Read-Required "Entra Application (client) ID" $ClientId
$AdminEmail = Read-Required "First administrator email" $AdminEmail
$SqlConnectionString = Read-Required "SQL Server connection string" $SqlConnectionString
$PublicUrl = Read-Required "Public HTTPS URL (for example https://vigil365.contoso.com)" $PublicUrl
if ($PublicUrl -notmatch '^https://') { throw "The public URL must start with https://" }
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
}

Write-Host "Building and publishing Vigil365..." -ForegroundColor Cyan
Push-Location $client
try { npm ci --no-audit --no-fund; npm run build } finally { Pop-Location }
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
dotnet publish $api -c Release -o $InstallPath | Out-Host

$config = [ordered]@{
    ConnectionStrings = [ordered]@{ DefaultConnection = $SqlConnectionString }
    AzureAd = [ordered]@{ Instance = "https://login.microsoftonline.com/"; TenantId = $TenantId; ClientId = $ClientId; Audience = "api://$ClientId" }
    Auth = [ordered]@{ RedirectUri = $PublicUrl; BootstrapAdminEmail = $AdminEmail }
    Cors = [ordered]@{ AllowedOrigins = @($PublicUrl.TrimEnd('/')) }
    Security = [ordered]@{ RequireHttps = $false }
    DataProtection = [ordered]@{ KeyPath = (Join-Path $InstallPath "keys") }
}
$configPath = Join-Path $InstallPath "appsettings.Production.json"
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
New-Item -ItemType Directory -Force -Path (Join-Path $InstallPath "keys") | Out-Null

# The service identity and local administrators can read config/keys; ordinary users cannot.
$acl = Get-Acl $InstallPath
$acl.SetAccessRuleProtection($true, $false)
foreach ($identity in @("BUILTIN\Administrators", "NT AUTHORITY\SYSTEM", "NT AUTHORITY\LOCAL SERVICE")) {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
}
Set-Acl -LiteralPath $InstallPath -AclObject $acl

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { sc.exe delete $ServiceName | Out-Null; Start-Sleep -Seconds 2 }
$binPath = "`"$exe`" --environment Production --urls http://127.0.0.1:$Port"
sc.exe create $ServiceName binPath= $binPath start= auto obj= "NT AUTHORITY\LocalService" | Out-Null
sc.exe description $ServiceName "Vigil365 Microsoft 365 security monitoring service" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
sc.exe start $ServiceName | Out-Null

Write-Host "Installed $ServiceName. Configure a TLS reverse proxy for $PublicUrl -> http://127.0.0.1:$Port, then add $PublicUrl as an Entra SPA redirect URI." -ForegroundColor Green
