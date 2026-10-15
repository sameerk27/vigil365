# register-app.ps1 against a fake Azure CLI. A function named az takes precedence
# over az.exe, records every call (with the --body file read at call time, since
# the script deletes it after) and fails the calls a test names with exit code 1.
# The script used to report "Configured." and "Admin consent granted." when az
# had failed, because $ErrorActionPreference does not apply to native commands.

Describe "register-app.ps1" {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'ScriptTestHelpers.ps1')
        $registerApp = (Resolve-Path (Join-Path $PSScriptRoot '../../register-app.ps1')).Path
        $permissionsDoc = Get-Content (Join-Path $PSScriptRoot '../../graph-permissions.json') -Raw | ConvertFrom-Json
        $requested = @($permissionsDoc.required.name) + @($permissionsDoc.optional.name)
        $graphRoles = @($requested + 'Application.Read.All' | ForEach-Object { @{ value = $_; id = "role-$_" } })

        $global:VigilAzShim = @{
            Calls   = New-Object System.Collections.ArrayList
            Fail    = @()
            GraphSp = ConvertTo-Json -Compress -Depth 5 -InputObject @{ id = 'graph-sp-0001'; appRoles = $graphRoles }
        }

        function az {
            $argv = @($args | ForEach-Object { "$_" })
            $line = $argv -join ' '
            $body = $null
            $at = [array]::IndexOf($argv, '--body')
            if ($at -ge 0) { $body = Get-Content -LiteralPath $argv[$at + 1].TrimStart('@') -Raw }
            [void]$global:VigilAzShim.Calls.Add([pscustomobject]@{ Line = $line; Body = $body })

            foreach ($failing in $global:VigilAzShim.Fail) {
                if ($line -like "$failing*") { $global:LASTEXITCODE = 1; return "ERROR: Insufficient privileges to complete the operation." }
            }
            $global:LASTEXITCODE = 0
            switch -Wildcard ($line) {
                'account show --query tenantId*' { return '33333333-3333-3333-3333-333333333333' }
                'account show*' { return '{}' }
                'ad sp show --id 00000003-0000-0000-c000-000000000000*' { return $global:VigilAzShim.GraphSp }
                'ad app create*' { return '{"appId":"app-0001","id":"app-object-0001"}' }
                'ad sp show --id app-0001*' { return '{"id":"sp-0001"}' }
                'ad app credential reset*' { return '{"password":"secret-from-the-shim"}' }
            }
            return ''
        }

        # Retries wait 6 s between attempts; not here.
        function Start-Sleep { }

        function Invoke-RegisterApp([switch]$MultiTenant, [string[]]$Fail = @()) {
            $global:VigilAzShim.Calls.Clear()
            $global:VigilAzShim.Fail = $Fail
            $lines = New-Object System.Collections.Generic.List[string]
            $thrown = $null
            $params = @{ RedirectUri = 'https://vigil.msp.test' }
            if ($MultiTenant) { $params.MultiTenant = $true }
            try { & $registerApp @params *>&1 | ForEach-Object { $lines.Add("$_") } } catch { $thrown = $_ }
            [pscustomobject]@{ Output = ($lines -join "`n"); Error = $thrown; Calls = @($global:VigilAzShim.Calls) }
        }

        # Always an array (the comma stops PowerShell unrolling a single match).
        function Get-Calls($Run, [string]$Prefix) { , @($Run.Calls | Where-Object { $_.Line -like "$Prefix*" }) }
    }

    AfterAll { Remove-Variable -Name VigilAzShim -Scope Global -ErrorAction SilentlyContinue }

    # ── inst-11: failures are failures ──

    It "stops when the app PATCH fails, instead of printing Configured." {
        $run = Invoke-RegisterApp -Fail 'rest --method PATCH'

        Assert-True ($null -ne $run.Error) "the script to throw"
        Assert-True ("$($run.Error)" -match 'az rest --method PATCH failed') "the error to name the failed call, got: $($run.Error)"
        Assert-True ($run.Output -notmatch 'Configured\.') "no 'Configured.' after a failed PATCH:`n$($run.Output)"
        Assert-True ($run.Output -notmatch '=== Done ===') "no 'Done' after a failed PATCH"
        Assert-Equal 3 (Get-Calls $run 'rest --method PATCH').Count "PATCH attempts (retried while Entra replicates)"
        # Nothing is minted for an app nobody can sign in to.
        Assert-Equal 0 (Get-Calls $run 'ad app credential reset').Count "secrets created after the failure"
    }

    It "reports a failed admin consent instead of printing Admin consent granted." {
        $run = Invoke-RegisterApp -Fail 'ad app permission admin-consent'

        Assert-True ($null -eq $run.Error) "the script to finish (consent can be granted in the portal later), got: $($run.Error)"
        Assert-True ($run.Output -notmatch 'Admin consent granted') "no 'Admin consent granted.' after a failed consent:`n$($run.Output)"
        Assert-True ($run.Output -match 'Could not auto-consent') "the failure to be reported:`n$($run.Output)"
        Assert-True ($run.Output -match 'Grant admin consent') "the portal remedy to be shown"
        Assert-Equal 5 (Get-Calls $run 'ad app permission admin-consent').Count "admin-consent attempts"
    }

    It "reports success only when every call succeeded" {
        $run = Invoke-RegisterApp

        Assert-True ($null -eq $run.Error) "no error, got: $($run.Error)"
        Assert-True ($run.Output -match 'Configured\.') "'Configured.' after a successful PATCH"
        Assert-True ($run.Output -match 'Admin consent granted') "'Admin consent granted.' after a successful consent"
        Assert-Equal 1 (Get-Calls $run 'rest --method PATCH').Count "PATCH attempts"
    }

    # ── inst-12: -MultiTenant grants Application.Read.All in the MSP's own tenant ──

    It "-MultiTenant makes the app multi-tenant with the /consented redirect" {
        $run = Invoke-RegisterApp -MultiTenant

        Assert-True ((Get-Calls $run 'ad app create')[0].Line -match '--sign-in-audience AzureADMultipleOrgs') "a multi-tenant app"
        $patch = (Get-Calls $run 'rest --method PATCH')[0].Body | ConvertFrom-Json
        Assert-Equal @('https://vigil.msp.test/consented') @($patch.web.redirectUris) "Web redirect URIs"
        Assert-Equal @('https://vigil.msp.test') @($patch.spa.redirectUris) "SPA redirect URIs"
    }

    It "-MultiTenant assigns the Application.Read.All app role to the app's own service principal" {
        $run = Invoke-RegisterApp -MultiTenant

        $grant = Get-Calls $run 'rest --method POST --uri https://graph.microsoft.com/v1.0/servicePrincipals/graph-sp-0001/appRoleAssignedTo'
        Assert-Equal 1 $grant.Count "appRoleAssignedTo POSTs to the Graph service principal"
        $body = $grant[0].Body | ConvertFrom-Json
        Assert-Equal 'role-Application.Read.All' $body.appRoleId "the Application.Read.All role id"
        Assert-Equal 'sp-0001' $body.principalId "the app's own service principal"
        Assert-Equal 'graph-sp-0001' $body.resourceId "Microsoft Graph"
        Assert-True ($run.Output -match 'Granted Application.Read.All') "the grant to be reported"
    }

    It "-MultiTenant never asks clients to consent to Application.Read.All" {
        # A direct assignment in the MSP's tenant, deliberately not in requiredResourceAccess.
        $run = Invoke-RegisterApp -MultiTenant

        $patch = (Get-Calls $run 'rest --method PATCH')[0].Body | ConvertFrom-Json
        $ids = @($patch.requiredResourceAccess[0].resourceAccess | ForEach-Object { $_.id })
        Assert-True ($ids -notcontains 'role-Application.Read.All') "Application.Read.All out of requiredResourceAccess"
        Assert-Equal @($requested | ForEach-Object { "role-$_" }) $ids "requiredResourceAccess is graph-permissions.json"
    }

    It "-MultiTenant reports a failed Application.Read.All grant instead of claiming it" {
        $run = Invoke-RegisterApp -MultiTenant -Fail 'rest --method POST --uri https://graph.microsoft.com/v1.0/servicePrincipals/graph-sp-0001/appRoleAssignedTo'

        Assert-True ($null -eq $run.Error) "the script to finish (the readiness card then shows 'not checked'), got: $($run.Error)"
        Assert-True ($run.Output -notmatch 'Granted Application.Read.All') "no 'Granted' after a failed grant"
        Assert-True ($run.Output -match 'Could not grant Application.Read.All') "the failure to be reported:`n$($run.Output)"
    }

    It "without -MultiTenant stays single-tenant and grants nothing extra" {
        $run = Invoke-RegisterApp

        Assert-True ((Get-Calls $run 'ad app create')[0].Line -match '--sign-in-audience AzureADMyOrg') "a single-tenant app"
        Assert-Equal 0 (Get-Calls $run 'rest --method POST').Count "app-role assignments"
        $patch = (Get-Calls $run 'rest --method PATCH')[0].Body | ConvertFrom-Json
        Assert-True ($null -eq $patch.web) "no Web (consent) redirect"
    }
}
