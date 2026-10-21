# grant-power-platform-reader.ps1 against fake Az.Accounts cmdlets and a fake
# Invoke-RestMethod. Functions of those names take precedence over the real ones,
# record every call and answer from the state the test sets up. Nothing reaches
# Microsoft.

Describe "grant-power-platform-reader.ps1" {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'ScriptTestHelpers.ps1')
        $script = (Resolve-Path (Join-Path $PSScriptRoot '../../grant-power-platform-reader.ps1')).Path
        $tenant = '33333333-3333-3333-3333-333333333333'
        $app    = '44444444-4444-4444-4444-444444444444'

        $global:VigilPpShim = @{}

        function Get-AzContext { [pscustomobject]@{ Tenant = [pscustomobject]@{ Id = $global:VigilPpShim.SignedInTenant } } }
        function Connect-AzAccount { [void]$global:VigilPpShim.Calls.Add([pscustomobject]@{ Method = 'SIGNIN'; Uri = "$args"; Body = $null }) }
        function Get-AzAccessToken {
            $s = New-Object System.Security.SecureString
            'secret-token-value'.ToCharArray() | ForEach-Object { $s.AppendChar($_) }
            [pscustomobject]@{ Token = $s }
        }
        function Invoke-RestMethod {
            param($Method, $Uri, $Headers, $Body, $ContentType)
            [void]$global:VigilPpShim.Calls.Add([pscustomobject]@{ Method = "$Method"; Uri = "$Uri"; Body = $Body })
            if ($Headers.Authorization -ne 'Bearer secret-token-value') { throw "unexpected Authorization header" }
            $shim = $global:VigilPpShim
            if ($Uri -like "*servicePrincipals?*appId eq '$app'*") { return @{ value = @($shim.VigilSp) | Where-Object { $_ } } }
            if ($Uri -like "*servicePrincipals?*appId eq '8578e004-a5c6-46e7-913e-12f58912df43'*") { return @{ value = @($shim.PlatformSp) | Where-Object { $_ } } }
            if ($Method -eq 'GET' -and $Uri -like '*/authorization/roleAssignments?*') { return @{ value = @($shim.Assignments) } }
            return @{}
        }

        function Invoke-Grant([switch]$Remove, [switch]$WhatIf, $VigilSp = @{ id = 'vigil-sp-1'; displayName = 'Vigil365' },
                              $PlatformSp = @{ id = 'pp-sp-1' }, $Assignments = @(), [string]$SignedInTenant = $tenant) {
            $global:VigilPpShim = @{
                Calls = New-Object System.Collections.ArrayList
                VigilSp = $VigilSp; PlatformSp = $PlatformSp; Assignments = $Assignments; SignedInTenant = $SignedInTenant
            }
            $lines = New-Object System.Collections.Generic.List[string]
            $thrown = $null
            $params = @{ TenantId = $tenant; AppId = $app }
            if ($Remove) { $params.Remove = $true }
            if ($WhatIf) { $params.WhatIf = $true }
            try { & $script @params *>&1 | ForEach-Object { $lines.Add("$_") } } catch { $thrown = $_ }
            [pscustomobject]@{ Output = ($lines -join "`n"); Error = $thrown; Calls = @($global:VigilPpShim.Calls) }
        }

        function Get-Writes($Run) { , @($Run.Calls | Where-Object { $_.Method -in 'POST', 'DELETE', 'PATCH', 'PUT' }) }
        $reader = 'c886ad2e-27f7-4874-8381-5849b8d8a090'
    }

    AfterAll { Remove-Variable -Name VigilPpShim -Scope Global -ErrorAction SilentlyContinue }

    It "assigns the reader role to Vigil365's service principal at tenant scope" {
        $run = Invoke-Grant

        Assert-True ($null -eq $run.Error) "no error, got: $($run.Error)"
        $writes = Get-Writes $run
        Assert-Equal 1 $writes.Count "one write"
        Assert-True ($writes[0].Uri -eq 'https://api.powerplatform.com/authorization/roleAssignments?api-version=2024-10-01') "the role assignment endpoint, got: $($writes[0].Uri)"
        $body = $writes[0].Body | ConvertFrom-Json
        Assert-Equal $reader $body.roleDefinitionId "the Power Platform reader role"
        Assert-Equal 'vigil-sp-1' $body.principalObjectId "Vigil365's service principal"
        Assert-Equal 'ApplicationUser' $body.principalType "principal type"
        Assert-Equal "/tenants/$tenant" $body.scope "tenant scope"
        Assert-True ($run.Output -match 'Assigned\.') "'Assigned.' reported"
    }

    It "never prints an access token" {
        $run = Invoke-Grant
        Assert-True ($run.Output -notmatch 'secret-token-value') "no token in the output"
    }

    It "does nothing when Vigil365 already has the role" {
        $run = Invoke-Grant -Assignments @(@{ roleAssignmentId = 'ra-1'; principalObjectId = 'vigil-sp-1'; roleDefinitionId = $reader })

        Assert-Equal 0 (Get-Writes $run).Count "writes"
        Assert-True ($run.Output -match 'already has the role') "reported as already on"
    }

    It "a different role or principal is not mistaken for the reader assignment" {
        $run = Invoke-Grant -Assignments @(
            @{ roleAssignmentId = 'ra-1'; principalObjectId = 'vigil-sp-1'; roleDefinitionId = 'ff954d61-a89a-4fbe-ace9-01c367b89f87' },
            @{ roleAssignmentId = 'ra-2'; principalObjectId = 'someone-else'; roleDefinitionId = $reader })

        Assert-Equal 1 (Get-Writes $run).Count "the reader role is still assigned"
    }

    It "registers the Power Platform API service principal first when the tenant lacks it" {
        $run = Invoke-Grant -PlatformSp $null

        $writes = Get-Writes $run
        Assert-Equal 2 $writes.Count "two writes"
        Assert-True ($writes[0].Uri -eq 'https://graph.microsoft.com/v1.0/servicePrincipals') "the service principal is created first"
        Assert-Equal '8578e004-a5c6-46e7-913e-12f58912df43' ($writes[0].Body | ConvertFrom-Json).appId "for the Power Platform API"
        Assert-True ($writes[1].Uri -like '*/authorization/roleAssignments*') "then the role assignment"
    }

    It "stops before any write when the tenant hasn't consented to Vigil365" {
        $run = Invoke-Grant -VigilSp $null

        Assert-True ("$($run.Error)" -match 'Grant it admin consent first') "the remedy is named, got: $($run.Error)"
        Assert-Equal 0 (Get-Writes $run).Count "writes"
    }

    It "-WhatIf changes nothing" {
        $run = Invoke-Grant -WhatIf
        Assert-Equal 0 (Get-Writes $run).Count "writes"

        $run = Invoke-Grant -WhatIf -PlatformSp $null
        Assert-Equal 0 (Get-Writes $run).Count "writes when the service principal is missing"
    }

    It "-Remove deletes only Vigil365's reader assignment" {
        $run = Invoke-Grant -Remove -Assignments @(
            @{ roleAssignmentId = 'ra-1'; principalObjectId = 'vigil-sp-1'; roleDefinitionId = $reader },
            @{ roleAssignmentId = 'ra-2'; principalObjectId = 'someone-else'; roleDefinitionId = $reader })

        $writes = Get-Writes $run
        Assert-Equal 1 $writes.Count "one delete"
        Assert-Equal 'DELETE' $writes[0].Method "a DELETE"
        Assert-True ($writes[0].Uri -eq 'https://api.powerplatform.com/authorization/roleAssignments/ra-1?api-version=2024-10-01') "Vigil365's assignment, got: $($writes[0].Uri)"
    }

    It "-Remove never registers anything" {
        $run = Invoke-Grant -Remove -PlatformSp $null
        Assert-Equal 0 (Get-Writes $run).Count "writes"
    }

    It "signs in to the named tenant when the current Azure context is another one" {
        $run = Invoke-Grant -SignedInTenant '99999999-9999-9999-9999-999999999999'
        Assert-True (@($run.Calls | Where-Object { $_.Method -eq 'SIGNIN' -and $_.Uri -match $tenant }).Count -eq 1) "Connect-AzAccount for $tenant"
    }
}
