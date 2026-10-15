<#
.SYNOPSIS
  Installs PostgreSQL 16 on this Windows machine (via winget) and creates the
  database and login Vigil365 MSP mode needs.

.DESCRIPTION
  MSP mode needs PostgreSQL 14+ or a full SQL Server edition; the Setup wizard
  refuses SQL Server Express and does not install a database for you. This is the
  quick route for a Windows evaluation machine. For production, install and
  operate PostgreSQL the way your organisation normally does.

  It asks you (privately - nothing is echoed or logged) for two passwords:
    1. the PostgreSQL superuser ("postgres") password
    2. the password for the new "vigil365" login the app will use
  Passwords must be 12+ characters from  A-Z a-z 0-9 ! @ # % ^ * _ + - . , :
  (no spaces, quotes, semicolons or '=' - they would break the connection string).

  It then prints the connection string to paste into Setup, with a placeholder
  instead of your password.

  Run from an ELEVATED PowerShell (Run as administrator).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install-postgres-local.ps1
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
  [int]$Port = 5432,
  [string]$Database = 'vigil365',
  [string]$AppUser = 'vigil365',
  # Use an installer you already downloaded (e.g. when winget's download fails)
  # instead of winget. It is verified against the SHA-256 EnterpriseDB publishes
  # for 16.15-5 before it is run.
  [string]$InstallerPath
)

$ExpectedInstallerSha256 = '4065dbc5ffb52010e7357dfd6a74934878f1c6faf7a75cad1411a22e11b7b929'

$ErrorActionPreference = 'Stop'

function Read-PlainPassword([string]$Prompt) {
  while ($true) {
    $a = Read-Host -AsSecureString $Prompt
    $b = Read-Host -AsSecureString 'Type it again to confirm'
    $pa = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($a))
    $pb = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($b))
    if ($pa -cne $pb) { Write-Host 'The two entries differ - try again.' -ForegroundColor Yellow; continue }
    if ($pa -notmatch '^[A-Za-z0-9!@#%^*_+\-.,:]{12,}$') {
      Write-Host 'Use 12+ characters from A-Z a-z 0-9 ! @ # % ^ * _ + - . , :   (no spaces, quotes, ; or =).' -ForegroundColor Yellow; continue
    }
    return $pa
  }
}

if ($AppUser -notmatch '^[a-z][a-z0-9_]*$' -or $Database -notmatch '^[a-z][a-z0-9_]*$') {
  throw 'Database and user names must be lower-case letters, digits and underscores.'
}
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget was not found. Install PostgreSQL 16 manually, then create the database and login yourself.' }

$existing = Get-Service -Name 'postgresql-x64-16' -ErrorAction SilentlyContinue
$psql = Join-Path $env:ProgramFiles 'PostgreSQL\16\bin\psql.exe'

Write-Host "`nPostgreSQL 16 for Vigil365 MSP mode`n" -ForegroundColor Cyan
$super = Read-PlainPassword 'Choose the PostgreSQL superuser ("postgres") password'

if ($existing) {
  Write-Host "PostgreSQL 16 is already installed (service 'postgresql-x64-16' is $($existing.Status)); skipping the install. Use its existing postgres password." -ForegroundColor Yellow
} else {
  $installerArgs = "--mode unattended --unattendedmodeui none --superpassword $super --serverport $Port --servicename postgresql-x64-16 --disable-components pgAdmin,stackbuilder"
  if ($InstallerPath) {
    if (-not (Test-Path $InstallerPath)) { throw "Installer not found: $InstallerPath" }
    $actual = (Get-FileHash $InstallerPath -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $ExpectedInstallerSha256) { throw "Installer checksum mismatch (got $actual). Re-download it; it was not run." }
    Write-Host 'Installer checksum verified. Installing PostgreSQL 16 (takes a few minutes)...' -ForegroundColor Cyan
    $p = Start-Process -FilePath $InstallerPath -ArgumentList $installerArgs -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "The PostgreSQL installer failed (exit $($p.ExitCode))." }
  } else {
    Write-Host 'Installing PostgreSQL 16 (about 385 MB download from the official EnterpriseDB distributor, verified by winget against a published SHA-256)...' -ForegroundColor Cyan
    winget install --exact --id PostgreSQL.PostgreSQL.16 --source winget --silent --accept-package-agreements --accept-source-agreements --override $installerArgs
    if ($LASTEXITCODE -ne 0) { throw "winget install failed (exit $LASTEXITCODE). If the download keeps failing, download the installer yourself and re-run with -InstallerPath <file>." }
  }
}

if (-not (Test-Path $psql)) { throw "psql.exe not found at $psql - the install may have failed or used another folder." }

Write-Host 'Waiting for the PostgreSQL service...' -ForegroundColor Cyan
$svc = Get-Service -Name 'postgresql-x64-16'
if ($svc.Status -ne 'Running') { Start-Service $svc }
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-NetConnection -ComputerName localhost -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue)) {
  if ((Get-Date) -gt $deadline) { throw "PostgreSQL did not start listening on port $Port within 60 seconds." }
  Start-Sleep -Seconds 2
}

$appPw = Read-PlainPassword "Choose the password for the new '$AppUser' login (the app uses this one)"

$env:PGPASSWORD = $super
try {
  function Invoke-Psql([string]$Sql) {
    $out = & $psql -h localhost -p $Port -U postgres -d postgres -v ON_ERROR_STOP=1 -tAc $Sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $out" }
    return ($out | Out-String).Trim()
  }
  if ((Invoke-Psql "SELECT 1 FROM pg_roles WHERE rolname='$AppUser'") -eq '1') {
    Invoke-Psql "ALTER ROLE $AppUser LOGIN PASSWORD '$appPw'" | Out-Null
    Write-Host "Login '$AppUser' already existed - its password was reset to the one you just chose."
  } else {
    Invoke-Psql "CREATE ROLE $AppUser LOGIN PASSWORD '$appPw'" | Out-Null
    Write-Host "Created login '$AppUser'."
  }
  if ((Invoke-Psql "SELECT 1 FROM pg_database WHERE datname='$Database'") -eq '1') {
    Write-Host "Database '$Database' already exists - left as is."
  } else {
    Invoke-Psql "CREATE DATABASE $Database OWNER $AppUser" | Out-Null
    Write-Host "Created database '$Database' owned by '$AppUser'."
  }
}
finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }

# Prove the app login works, without printing the password.
$env:PGPASSWORD = $appPw
try {
  $who = & $psql -h localhost -p $Port -U $AppUser -d $Database -tAc 'SELECT current_user' 2>&1
  if ($LASTEXITCODE -ne 0 -or ($who | Out-String).Trim() -ne $AppUser) { throw "The '$AppUser' login could not connect: $who" }
}
finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }

Write-Host "`nDone. PostgreSQL is running and the '$AppUser' login can connect to '$Database'." -ForegroundColor Green
Write-Host "`nIn Vigil365 Setup choose  MSP  +  PostgreSQL  and paste this connection string,"
Write-Host "replacing the placeholder with the '$AppUser' password you just chose:`n"
Write-Host "  Host=localhost;Port=$Port;Database=$Database;Username=$AppUser;Password=<the $AppUser password>" -ForegroundColor White
Write-Host ''
