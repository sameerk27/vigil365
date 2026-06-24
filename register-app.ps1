<#
.SYNOPSIS
  Scripts the Entra (Azure AD) app registration Vigil365 needs: read-only Microsoft
  Graph application permissions, a SPA redirect URI, an exposed API scope
  (access_as_user) for the dashboard login, a client secret, and admin consent.

  Outputs the TenantId / ClientId / ClientSecret and a ready-to-run deploy.ps1 line.

.DESCRIPTION
  Uses the Azure CLI (az). You must be signed in as a user who can create app
  registrations and grant admin consent (Application Administrator / Cloud
  Application Administrator / Global Administrator).

  Run it yourself — it creates an identity object and grants tenant consent in
  YOUR tenant. Review before running.

.EXAMPLE
  az login
  .\register-app.ps1 -RedirectUri https://localhost:5001

.EXAMPLE
  .\register-app.ps1 -DisplayName "Vigil365 (Prod)" -RedirectUri https://vigil365.contoso.com
#>
[CmdletBinding()]
param(
    [string]$DisplayName = "Vigil365",
    [string]$RedirectUri = "https://localhost:5001",
    [int]$SecretYears    = 1
)

$ErrorActionPreference = "Stop"
$GraphAppId = "00000003-0000-0000-c000-000000000000"  # Microsoft Graph

# Read-only Graph application permissions the dashboard uses (see README).
$Permissions = @(
    "SecurityEvents.Read.All",
    "SecurityIncident.Read.All",
    "IdentityRiskyUser.Read.All",
    "IdentityRiskEvent.Read.All",
    "AuditLog.Read.All",
    "Reports.Read.All",
    "DeviceManagementManagedDevices.Read.All",
    "ServiceHealth.Read.All",
    "Policy.Read.All",
    "Directory.Read.All",
    "ThreatHunting.Read.All",
    "UserAuthenticationMethod.Read.All"
)

function Require-Az {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "Azure CLI (az) not found. Install: https://learn.microsoft.com/cli/azure/install-azure-cli"
    }
    try { az account show 1>$null 2>$null } catch { }
    if ($LASTEXITCODE -ne 0) { throw "Not signed in. Run 'az login' first." }
}

Write-Host "`n=== Vigil365 app registration ===`n" -ForegroundColor Cyan
Require-Az

$tenantId = az account show --query tenantId -o tsv
Write-Host "Tenant: $tenantId" -ForegroundColor DarkGray

# 1. Resolve permission names -> app-role GUIDs from the Graph service principal.
Write-Host "[1/6] Resolving Graph permission IDs..." -ForegroundColor Yellow
$graphSp = az ad sp show --id $GraphAppId | ConvertFrom-Json
$roleMap = @{}
foreach ($r in $graphSp.appRoles) { $roleMap[$r.value] = $r.id }

$resourceAccess = @()
foreach ($p in $Permissions) {
    if (-not $roleMap.ContainsKey($p)) { Write-Host "      ! Unknown permission '$p' — skipping" -ForegroundColor DarkYellow; continue }
    $resourceAccess += @{ id = $roleMap[$p]; type = "Role" }
}
Write-Host "      Mapped $($resourceAccess.Count) permissions." -ForegroundColor Green

# 2. Create the app registration.
Write-Host "[2/6] Creating app registration '$DisplayName'..." -ForegroundColor Yellow
$app = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg | ConvertFrom-Json
$appId    = $app.appId
$objectId = $app.id
Write-Host "      App (client) ID: $appId" -ForegroundColor Green

# 3. Patch app: SPA redirect URI, Application ID URI, access_as_user scope, Graph permissions.
Write-Host "[3/6] Configuring SPA redirect, exposed API scope, and permissions..." -ForegroundColor Yellow
$scopeId = [guid]::NewGuid().ToString()
$patch = @{
    spa = @{ redirectUris = @($RedirectUri) }
    identifierUris = @("api://$appId")
    api = @{
        oauth2PermissionScopes = @(@{
            id    = $scopeId
            type  = "User"
            value = "access_as_user"
            isEnabled = $true
            adminConsentDisplayName = "Access Vigil365"
            adminConsentDescription = "Allows the signed-in user to access Vigil365 on their behalf."
            userConsentDisplayName  = "Access Vigil365"
            userConsentDescription  = "Allows you to access Vigil365 on your behalf."
        })
    }
    requiredResourceAccess = @(@{
        resourceAppId  = $GraphAppId
        resourceAccess = $resourceAccess
    })
}
$patchJson = $patch | ConvertTo-Json -Depth 10 -Compress
$tmp = New-TemporaryFile
Set-Content -Path $tmp -Value $patchJson -Encoding UTF8
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
    --headers "Content-Type=application/json" --body "@$tmp" | Out-Null
Remove-Item $tmp -Force
Write-Host "      Configured." -ForegroundColor Green

# 4. Ensure a service principal exists (needed for consent).
Write-Host "[4/6] Ensuring service principal..." -ForegroundColor Yellow
az ad sp create --id $appId 2>$null | Out-Null
Write-Host "      Service principal ready." -ForegroundColor Green

# 5. Create a client secret.
Write-Host "[5/6] Creating client secret..." -ForegroundColor Yellow
$cred = az ad app credential reset --id $appId --append --years $SecretYears --display-name "vigil365-deploy" | ConvertFrom-Json
$clientSecret = $cred.password
Write-Host "      Secret created (shown once below)." -ForegroundColor Green

# 6. Grant admin consent for the application permissions.
Write-Host "[6/6] Granting admin consent..." -ForegroundColor Yellow
try {
    az ad app permission admin-consent --id $appId
    Write-Host "      Admin consent granted." -ForegroundColor Green
} catch {
    Write-Host "      Could not auto-consent. Grant it in the portal: Entra > App registrations >" -ForegroundColor DarkYellow
    Write-Host "      $DisplayName > API permissions > Grant admin consent." -ForegroundColor DarkYellow
}

# Output
Write-Host "`n=== Done ===`n" -ForegroundColor Cyan
Write-Host "TenantId     : $tenantId"
Write-Host "ClientId     : $appId"
Write-Host "ClientSecret : $clientSecret  (store securely — not shown again)" -ForegroundColor Yellow
Write-Host "RedirectUri  : $RedirectUri"
Write-Host "`nNext — deploy with:" -ForegroundColor White
Write-Host "  .\deploy.ps1 -TenantId $tenantId -ClientId $appId -AdminEmail you@yourdomain.com -Url $RedirectUri" -ForegroundColor Gray
Write-Host "`nThen enter the client secret in the in-app Setup wizard after signing in.`n" -ForegroundColor White
