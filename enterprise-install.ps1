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
    # "Single" (default) or "Msp"; and the database engine the connection string is for.
    [ValidateSet("Single", "Msp")] [string]$Mode = "Single",
    [ValidateSet("SqlServer", "Postgres")] [string]$DatabaseProvider = "SqlServer",
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
# Runs sc.exe with this exact command line and fails on a non-zero exit code.
# Passed as PowerShell arguments, binPath's embedded quotes reach sc.exe unescaped
# under Windows PowerShell 5.1 (binPath= C:\Program), and Out-Null hid the failure.
function Invoke-Sc([string]$Arguments) {
    $p = Start-Process -FilePath sc.exe -ArgumentList $Arguments -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "sc.exe $Arguments failed with exit code $($p.ExitCode)." }
}
# The "sc create" command line. binPath's value is one argument: the executable,
# quoted because Program Files has a space (\" inside the outer quotes), then its
# arguments. Tested in scripts/tests/install-scripts.Tests.ps1.
function Get-ScCreateArguments([string]$ServiceName, [string]$Exe, [string]$Urls) {
    "create $ServiceName binPath= `"\`"$Exe\`" --environment Production --urls $Urls`" start= auto obj= `"NT AUTHORITY\LocalService`""
}
function Read-Required([string]$Name, [string]$Value) {
    if ($Value) { return $Value }
    do { $Value = Read-Host $Name } while ([string]::IsNullOrWhiteSpace($Value))
    return $Value.Trim()
}
Write-Host "`nVigil365 enterprise installer" -ForegroundColor Cyan
$TenantId = Read-Required "Entra Tenant ID" $TenantId
$ClientId = Read-Required "Entra Application (client) ID" $ClientId
$AdminEmail = Read-Required "First administrator email" $AdminEmail
$SqlConnectionString = Read-Required "$DatabaseProvider connection string" $SqlConnectionString
$ConnectionStringToUse = $SqlConnectionString
if ($Mode -eq "Msp" -and $DatabaseProvider -eq "SqlServer" -and $ConnectionStringToUse -match "SQLEXPRESS") {
    throw "MSP mode needs SQL Server Standard/Enterprise/Azure SQL or PostgreSQL - SQL Server Express stops accepting writes at 10 GB. See docs/MSP_V12_PLAN.md."
}
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
    Edition = [ordered]@{ Mode = $Mode }
    Database = [ordered]@{ Provider = $DatabaseProvider }
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

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { Invoke-Sc "delete $ServiceName"; Start-Sleep -Seconds 2 }
Invoke-Sc (Get-ScCreateArguments $ServiceName $exe "http://127.0.0.1:$Port")
Invoke-Sc "description $ServiceName `"Vigil365 Microsoft 365 security monitoring service`""
Invoke-Sc "failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000"
Invoke-Sc "start $ServiceName"
# "sc start" returns while the service is still starting; one that dies on startup must not read as installed.
try { (Get-Service $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(60)) }
catch { throw "The $ServiceName service was installed but is not running. Windows Event Viewer > Windows Logs > Application records why." }

Write-Host "Installed $ServiceName. Configure a TLS reverse proxy for $PublicUrl -> http://127.0.0.1:$Port, then add $PublicUrl as an Entra SPA redirect URI." -ForegroundColor Green
