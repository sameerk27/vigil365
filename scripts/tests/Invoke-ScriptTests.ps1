<#
.SYNOPSIS
  Runs the Pester tests for the repository's PowerShell scripts: register-app.ps1
  against a fake Azure CLI, and the sc.exe command line install.ps1 and
  enterprise-install.ps1 build (plus a real service round trip when elevated on
  Windows). Fails if any test fails or none ran.

.DESCRIPTION
  Works with whatever Pester is installed, from 3.4 (built into Windows
  PowerShell 5.1) to 5.x. CI runs it under PowerShell 7 on Linux and Windows and
  under Windows PowerShell 5.1, the shell whose argument passing broke sc.exe.
  The Linux installer has its own test: scripts/tests/enterprise-install.test.sh.

.EXAMPLE
  ./scripts/tests/Invoke-ScriptTests.ps1
#>
$ErrorActionPreference = "Stop"
Import-Module Pester   # the newest installed version
Write-Host "Pester $((Get-Module Pester).Version) on PowerShell $($PSVersionTable.PSVersion)"
$result = Invoke-Pester -Path $PSScriptRoot -PassThru
if ($result.TotalCount -eq 0) { throw "No script tests ran." }
if ($result.FailedCount -gt 0) { throw "$($result.FailedCount) script test(s) failed." }
