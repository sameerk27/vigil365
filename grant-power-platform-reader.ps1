<#
.SYNOPSIS
  Turns on Vigil365's optional Power Platform inventory for one tenant by giving the
  Vigil365 app Microsoft's read-only "Power Platform reader" role. -Remove turns it off.

.DESCRIPTION
  Vigil365 lists a tenant's Power Platform environments, apps and flows through the
  Power Platform API. Microsoft grants an app access to that API with a Power Platform
  role, not a Microsoft Graph permission, so admin consent alone doesn't cover it.

  What this script does, in the tenant you name and nowhere else:
    1. Finds the Vigil365 app's service principal (it exists once the tenant has
       consented to Vigil365).
    2. Registers the Power Platform API's own service principal if the tenant doesn't
       have one yet (Microsoft's documented step; without it no app can get a token).
    3. Assigns the Vigil365 app the "Power Platform reader" role at tenant scope,
       unless it already has it. The role grants every permission ending in .Read.

  The role is in preview at Microsoft. Vigil365 itself only ever reads.

  Run it yourself, signed in as a Power Platform Administrator or a Power Platform
  role-based access control administrator of that tenant. Uses the Az.Accounts module:
    Install-Module Az.Accounts -Scope CurrentUser

  Access tokens stay in memory and are never printed.

.EXAMPLE
  .\grant-power-platform-reader.ps1 -TenantId <tenant-id> -AppId <vigil365-app-id>

.EXAMPLE
  .\grant-power-platform-reader.ps1 -TenantId <tenant-id> -AppId <vigil365-app-id> -WhatIf

.EXAMPLE
  .\grant-power-platform-reader.ps1 -TenantId <tenant-id> -AppId <vigil365-app-id> -Remove
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The Microsoft Entra tenant to turn the inventory on for (a client tenant, for an MSP).
    [Parameter(Mandatory)][guid]$TenantId,
    # Vigil365's app (client) ID. Vigil365 shows it, with this command filled in, on its Power Platform page.
    [Parameter(Mandatory)][guid]$AppId,
    # Take the role away again: Vigil365 then stops reading the tenant's Power Platform.
    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$PowerPlatformApi      = "https://api.powerplatform.com"
$PowerPlatformApiAppId = "8578e004-a5c6-46e7-913e-12f58912df43"  # "Power Platform API", Microsoft's own app
$ReaderRoleId          = "c886ad2e-27f7-4874-8381-5849b8d8a090"  # "Power Platform reader"
$ApiVersion            = "2024-10-01"
$Graph                 = "https://graph.microsoft.com/v1.0"

function Get-Token([hashtable]$Resource) {
    $token = (Get-AzAccessToken -TenantId "$TenantId" @Resource).Token
    # Az.Accounts 5 and later return a SecureString; older versions a plain string.
    if ($token -is [securestring]) { $token = [System.Net.NetworkCredential]::new("", $token).Password }
    return $token
}

function Invoke-Api([string]$Method, [string]$Uri, [string]$Token, $Body = $null) {
    $request = @{ Method = $Method; Uri = $Uri; Headers = @{ Authorization = "Bearer $Token" } }
    if ($null -ne $Body) {
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 5 -Compress
        $request.ContentType = "application/json"
    }
    Invoke-RestMethod @request
}

function Find-ServicePrincipal([string]$ForAppId, [string]$Token) {
    $found = Invoke-Api GET "$Graph/servicePrincipals?`$filter=appId eq '$ForAppId'&`$select=id,displayName" $Token
    return @($found.value) | Select-Object -First 1
}

if (-not (Get-Command Get-AzAccessToken -ErrorAction SilentlyContinue)) {
    throw "The Az.Accounts module isn't installed. Run: Install-Module Az.Accounts -Scope CurrentUser"
}

$action = if ($Remove) { "Turning off" } else { "Turning on" }
Write-Host "`n=== $action the Vigil365 Power Platform inventory ===`n" -ForegroundColor Cyan

$context = Get-AzContext
if (-not $context -or "$($context.Tenant.Id)" -ne "$TenantId") {
    Write-Host "Signing in to tenant $TenantId..." -ForegroundColor DarkGray
    Connect-AzAccount -Tenant "$TenantId" | Out-Null
}

$graphToken = $null
$platformToken = $null
try {
    # 1. Vigil365's service principal in this tenant.
    Write-Host "[1/3] Finding the Vigil365 app in this tenant..." -ForegroundColor Yellow
    $graphToken = Get-Token @{ ResourceTypeName = "MSGraph" }
    $vigil = Find-ServicePrincipal "$AppId" $graphToken
    if (-not $vigil) {
        throw "The Vigil365 app ($AppId) isn't in tenant $TenantId yet. Grant it admin consent first (for an MSP client, the client's consent link), then run this again."
    }
    Write-Host "      Found '$($vigil.displayName)'." -ForegroundColor Green

    # 2. The Power Platform API's service principal.
    Write-Host "[2/3] Checking the Power Platform API is registered in this tenant..." -ForegroundColor Yellow
    $platformSp = Find-ServicePrincipal $PowerPlatformApiAppId $graphToken
    if ($platformSp) {
        Write-Host "      Registered." -ForegroundColor Green
    } elseif ($Remove) {
        Write-Host "      Not registered, so Vigil365 can't have the role. Nothing to remove." -ForegroundColor Green
        return
    } elseif ($PSCmdlet.ShouldProcess("tenant $TenantId", "Register the Power Platform API service principal")) {
        Invoke-Api POST "$Graph/servicePrincipals" $graphToken @{ appId = $PowerPlatformApiAppId } | Out-Null
        Write-Host "      Registered it." -ForegroundColor Green
    } else {
        Write-Host "      Would register it." -ForegroundColor DarkGray
        return
    }

    # 3. The role assignment.
    Write-Host "[3/3] Checking the Power Platform reader role..." -ForegroundColor Yellow
    $platformToken = Get-Token @{ ResourceUrl = "$PowerPlatformApi/" }
    $assignments = Invoke-Api GET "$PowerPlatformApi/authorization/roleAssignments?api-version=$ApiVersion" $platformToken
    $current = @(@($assignments.value) | Where-Object { "$($_.principalObjectId)" -eq "$($vigil.id)" -and "$($_.roleDefinitionId)" -eq $ReaderRoleId })

    if ($Remove) {
        if ($current.Count -eq 0) {
            Write-Host "      Vigil365 doesn't have the role. Nothing to remove." -ForegroundColor Green
        }
        foreach ($assignment in $current) {
            if ($PSCmdlet.ShouldProcess("'$($vigil.displayName)' in tenant $TenantId", "Remove the Power Platform reader role")) {
                Invoke-Api DELETE "$PowerPlatformApi/authorization/roleAssignments/$($assignment.roleAssignmentId)?api-version=$ApiVersion" $platformToken | Out-Null
                Write-Host "      Removed. Vigil365 no longer reads this tenant's Power Platform." -ForegroundColor Green
            }
        }
        return
    }

    if ($current.Count -gt 0) {
        Write-Host "      Vigil365 already has the role." -ForegroundColor Green
    } elseif ($PSCmdlet.ShouldProcess("'$($vigil.displayName)' in tenant $TenantId", "Assign the Power Platform reader role (read-only)")) {
        Invoke-Api POST "$PowerPlatformApi/authorization/roleAssignments?api-version=$ApiVersion" $platformToken @{
            roleDefinitionId  = $ReaderRoleId
            principalObjectId = "$($vigil.id)"
            principalType     = "ApplicationUser"
            scope             = "/tenants/$TenantId"
        } | Out-Null
        Write-Host "      Assigned." -ForegroundColor Green
    } else {
        return
    }

    Write-Host "`nDone. In Vigil365, open Power Platform and select Check again." -ForegroundColor Cyan
    Write-Host "A new role can take a few minutes to apply." -ForegroundColor DarkGray
} finally {
    Remove-Variable -Name graphToken, platformToken -ErrorAction SilentlyContinue -WhatIf:$false
}
