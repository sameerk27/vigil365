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

  Run it yourself - it creates an identity object and grants tenant consent in
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
    [int]$SecretYears    = 1,
    # Multi-tenant (MSP) mode: the app is registered as AzureADMultipleOrgs so each
    # client's Global Administrator can grant admin consent in their own tenant, and
    # a Web redirect to {RedirectUri}/consented is added so Vigil365's consent
    # landing page receives the admin-consent callback. The app is also granted
    # Application.Read.All in YOUR tenant only, for the MSP app readiness check.
    [switch]$MultiTenant,
    # Emit a single machine-readable JSON line ({tenantId,clientId,clientSecret,...})
    # as the last output, so the in-app "register the MSP app" flow can parse it.
    [switch]$Json
)

$ErrorActionPreference = "Stop"
$GraphAppId = "00000003-0000-0000-c000-000000000000"  # Microsoft Graph

# The Graph application permissions Vigil365 requests come from graph-permissions.json
# (repo root) - the same list the installer and the API embed, so this script can no
# longer drift from them. Required + optional are both requested.
$PermissionsFile = Join-Path $PSScriptRoot "graph-permissions.json"
if (-not (Test-Path $PermissionsFile)) { throw "graph-permissions.json not found next to register-app.ps1." }
$PermissionsDoc = Get-Content $PermissionsFile -Raw | ConvertFrom-Json
$Permissions = @($PermissionsDoc.required.name) + @($PermissionsDoc.optional.name)

function Require-Az {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "Azure CLI (az) not found. Install: https://learn.microsoft.com/cli/azure/install-azure-cli"
    }
    try { az account show 1>$null 2>$null } catch { }
    if ($LASTEXITCODE -ne 0) { throw "Not signed in. Run 'az login' first." }
}

# Runs az and returns its stdout. $ErrorActionPreference does not apply to native
# commands, so the exit code is checked here: a failed PATCH or consent used to be
# reported as done. -Attempts retries while Entra replicates a just-created app or
# service principal (consent against one not yet visible fails for a few seconds).
function Invoke-Az([string[]]$AzArgs, [int]$Attempts = 1) {
    $ErrorActionPreference = "Continue"   # az's stderr is captured, not raised; the exit code decides
    for ($i = 1; ; $i++) {
        $out = & az @AzArgs 2>&1
        if ($LASTEXITCODE -eq 0) {
            return (@($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n")
        }
        if ($i -ge $Attempts) { throw "az $($AzArgs[0..2] -join ' ') failed: $((@($out | ForEach-Object { "$_" }) -join ' ').Trim())" }
        Write-Host "      Not ready yet (attempt $i/$Attempts), Entra may still be replicating. Retrying..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 6
    }
}

Write-Host "`n=== Vigil365 app registration ===`n" -ForegroundColor Cyan
Require-Az

$tenantId = Invoke-Az @("account", "show", "--query", "tenantId", "-o", "tsv")
Write-Host "Tenant: $tenantId" -ForegroundColor DarkGray

# 1. Resolve permission names -> app-role GUIDs from the Graph service principal.
Write-Host "[1/6] Resolving Graph permission IDs..." -ForegroundColor Yellow
$graphSp = Invoke-Az @("ad", "sp", "show", "--id", $GraphAppId) | ConvertFrom-Json
$roleMap = @{}
foreach ($r in $graphSp.appRoles) { $roleMap[$r.value] = $r.id }

$resourceAccess = @()
foreach ($p in $Permissions) {
    if (-not $roleMap.ContainsKey($p)) { Write-Host "      ! Unknown permission '$p' - skipping" -ForegroundColor DarkYellow; continue }
    $resourceAccess += @{ id = $roleMap[$p]; type = "Role" }
}
Write-Host "      Mapped $($resourceAccess.Count) permissions." -ForegroundColor Green

# 2. Create the app registration.
$audience = if ($MultiTenant) { "AzureADMultipleOrgs" } else { "AzureADMyOrg" }
Write-Host "[2/6] Creating app registration '$DisplayName' ($audience)..." -ForegroundColor Yellow
$app = Invoke-Az @("ad", "app", "create", "--display-name", $DisplayName, "--sign-in-audience", $audience) | ConvertFrom-Json
$appId    = $app.appId
$objectId = $app.id
Write-Host "      App (client) ID: $appId" -ForegroundColor Green

# 3. Patch app: SPA redirect URI, Application ID URI, access_as_user scope, Graph permissions.
# In multi-tenant mode also register a Web redirect to Vigil365's /consented landing
# page so the client admin-consent callback is accepted.
Write-Host "[3/6] Configuring redirects, exposed API scope, and permissions..." -ForegroundColor Yellow
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
if ($MultiTenant) {
    $patch.web = @{ redirectUris = @(("{0}/consented" -f $RedirectUri.TrimEnd('/'))) }
}
$patchJson = $patch | ConvertTo-Json -Depth 10 -Compress
$tmp = New-TemporaryFile
Set-Content -Path $tmp -Value $patchJson -Encoding UTF8
try {
    # Without this the app has no SPA redirect, scope or permissions: nobody can sign in. Stop here.
    Invoke-Az @("rest", "--method", "PATCH", "--uri", "https://graph.microsoft.com/v1.0/applications/$objectId",
                "--headers", "Content-Type=application/json", "--body", "@$tmp") -Attempts 3 | Out-Null
} finally { Remove-Item $tmp -Force }
Write-Host "      Configured." -ForegroundColor Green

# 4. Ensure a service principal exists (needed for consent).
Write-Host "[4/6] Ensuring service principal..." -ForegroundColor Yellow
$sp = try { Invoke-Az @("ad", "sp", "show", "--id", $appId) } catch { Invoke-Az @("ad", "sp", "create", "--id", $appId) -Attempts 5 }
$spObjectId = ($sp | ConvertFrom-Json).id
Write-Host "      Service principal ready." -ForegroundColor Green

# 5. Create a client secret.
Write-Host "[5/6] Creating client secret..." -ForegroundColor Yellow
$cred = Invoke-Az @("ad", "app", "credential", "reset", "--id", $appId, "--append", "--years", $SecretYears,
                    "--display-name", "vigil365-deploy") -Attempts 3 | ConvertFrom-Json
$clientSecret = $cred.password
Write-Host "      Secret created (shown once below)." -ForegroundColor Green

# 6. Grant admin consent for the application permissions. It routinely fails on the
# first try while the new permissions and service principal replicate, so retry.
Write-Host "[6/6] Granting admin consent..." -ForegroundColor Yellow
try {
    Invoke-Az @("ad", "app", "permission", "admin-consent", "--id", $appId) -Attempts 5 | Out-Null
    Write-Host "      Admin consent granted." -ForegroundColor Green
} catch {
    Write-Host "      Could not auto-consent: $($_.Exception.Message)" -ForegroundColor DarkYellow
    Write-Host "      Collection stays empty until it is granted. In the portal: Entra > App registrations >" -ForegroundColor DarkYellow
    Write-Host "      $DisplayName > API permissions > Grant admin consent." -ForegroundColor DarkYellow
}

# MSP mode: let the app read its OWN registration (Application.Read.All), so the
# onboarding dialog can check client consent will work. A direct app-role
# assignment in this tenant, as the GUI installer makes - deliberately not in
# requiredResourceAccess, so clients are never asked to consent to it.
if ($MultiTenant) {
    $roleId = $roleMap["Application.Read.All"]
    $body = New-TemporaryFile
    try {
        if (-not $roleId) { throw "Graph has no Application.Read.All app role." }
        Set-Content -Path $body -Encoding UTF8 -Value (@{ principalId = $spObjectId; resourceId = $graphSp.id; appRoleId = $roleId } | ConvertTo-Json -Compress)
        Invoke-Az @("rest", "--method", "POST", "--uri", "https://graph.microsoft.com/v1.0/servicePrincipals/$($graphSp.id)/appRoleAssignedTo",
                    "--headers", "Content-Type=application/json", "--body", "@$body") -Attempts 3 | Out-Null
        Write-Host "      Granted Application.Read.All in this tenant (MSP app readiness check; clients never see it)." -ForegroundColor Green
    } catch {
        Write-Host "      Could not grant Application.Read.All ($($_.Exception.Message)). The MSP app readiness card will show 'not checked'." -ForegroundColor DarkYellow
    } finally { Remove-Item $body -Force }
}

# Output
Write-Host "`n=== Done ===`n" -ForegroundColor Cyan
Write-Host "TenantId     : $tenantId"
Write-Host "ClientId     : $appId"
Write-Host "ClientSecret : $clientSecret  (store securely - not shown again)" -ForegroundColor Yellow
Write-Host "RedirectUri  : $RedirectUri"
Write-Host "`nNext - deploy with:" -ForegroundColor White
Write-Host "  .\deploy.ps1 -TenantId $tenantId -ClientId $appId -AdminEmail you@yourdomain.com -Url $RedirectUri" -ForegroundColor Gray
Write-Host "`nThen enter the client secret in the in-app Setup wizard after signing in.`n" -ForegroundColor White

# Machine-readable line last, for the in-app "register the MSP app" flow. Everything
# above went to the host stream; this single line is the only stdout, so the caller
# can parse it without the human text getting in the way.
if ($Json) {
    $out = [ordered]@{
        tenantId     = $tenantId
        clientId     = $appId
        clientSecret = $clientSecret
        redirectUri  = $RedirectUri
        multiTenant  = [bool]$MultiTenant
    }
    Write-Output ($out | ConvertTo-Json -Compress)
}
