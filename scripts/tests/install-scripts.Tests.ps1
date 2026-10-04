# install.ps1 and enterprise-install.ps1 create the Windows service with one
# sc.exe command line, passed verbatim through Start-Process. Passed as separate
# PowerShell arguments instead, Windows PowerShell 5.1 dropped binPath's inner
# quotes (binPath= C:\Program) and the service was never created. These check the
# command line each script builds, as sc.exe will split it, and - on an elevated
# Windows machine, such as the CI runner - create and read back a real service.

Describe "enterprise-install.ps1 service command line" {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'ScriptTestHelpers.ps1')
        $scriptPath = Join-Path $PSScriptRoot '../../enterprise-install.ps1'
        . (Import-ScriptFunction $scriptPath 'Get-ScCreateArguments', 'Invoke-Sc')
        $exe = 'C:\Program Files\Vigil365\M365SecurityDashboard.Api.exe'
    }

    It "is the exact command line sc.exe needs" {
        Assert-Equal 'create Vigil365 binPath= "\"C:\Program Files\Vigil365\M365SecurityDashboard.Api.exe\" --environment Production --urls http://127.0.0.1:8080" start= auto obj= "NT AUTHORITY\LocalService"' `
            (Get-ScCreateArguments 'Vigil365' $exe 'http://127.0.0.1:8080')
    }

    It "keeps the quoted executable and its arguments in binPath's one value, and runs as LOCAL SERVICE" {
        $argv = Split-CommandLine (Get-ScCreateArguments 'Vigil365' $exe 'http://127.0.0.1:8080')
        Assert-Equal @('create', 'Vigil365', 'binPath=',
            '"C:\Program Files\Vigil365\M365SecurityDashboard.Api.exe" --environment Production --urls http://127.0.0.1:8080',
            'start=', 'auto', 'obj=', 'NT AUTHORITY\LocalService') $argv
    }

    It "fails loudly when sc.exe fails (Windows)" -Skip:($env:OS -ne 'Windows_NT') {
        $name = 'Vigil365NoSuchService' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $thrown = $null
        try { Invoke-Sc "query $name" } catch { $thrown = $_ }
        Assert-True ($null -ne $thrown) "Invoke-Sc to throw for a service that does not exist"
        Assert-True ("$thrown" -match 'exit code 1060') "the error to carry sc's exit code, got: $thrown"
    }

    It "creates a real service whose binary path keeps the quoted executable (Windows, elevated)" -Skip:(-not ($env:OS -eq 'Windows_NT' -and ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
        $name = 'Vigil365ScTest' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $probeExe = 'C:\Program Files\Vigil365 Sc Test\M365SecurityDashboard.Api.exe'
        try {
            Invoke-Sc (Get-ScCreateArguments $name $probeExe 'http://127.0.0.1:8080')
            $qc = (& sc.exe qc $name) -join "`n"
            $bin = [regex]::Match($qc, 'BINARY_PATH_NAME\s*:\s*(.+)').Groups[1].Value.Trim()
            Assert-Equal "`"$probeExe`" --environment Production --urls http://127.0.0.1:8080" $bin "the service's binary path"
            Assert-True ($qc -match 'SERVICE_START_NAME\s*:\s*NT AUTHORITY\\LocalService') "the service to run as LOCAL SERVICE:`n$qc"
        } finally {
            & sc.exe delete $name | Out-Null
        }
    }
}

Describe "install.ps1 service command line" {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'ScriptTestHelpers.ps1')
        $scriptPath = Join-Path $PSScriptRoot '../../install.ps1'
        . (Import-ScriptFunction $scriptPath 'Get-ScCreateArguments', 'Invoke-Sc')
    }

    It "keeps the quoted executable and its arguments in binPath's one value" {
        $exe = 'C:\Apps\Vigil 365\M365SecurityDashboard.Api.exe'
        $line = Get-ScCreateArguments 'Vigil365' $exe 'http://localhost:8080'
        Assert-Equal 'create Vigil365 binPath= "\"C:\Apps\Vigil 365\M365SecurityDashboard.Api.exe\" --environment Production --urls http://localhost:8080" start= auto' $line
        Assert-Equal @('create', 'Vigil365', 'binPath=',
            '"C:\Apps\Vigil 365\M365SecurityDashboard.Api.exe" --environment Production --urls http://localhost:8080',
            'start=', 'auto') (Split-CommandLine $line)
    }

    It "fails loudly when sc.exe fails (Windows)" -Skip:($env:OS -ne 'Windows_NT') {
        $thrown = $null
        try { Invoke-Sc ('query Vigil365NoSuchService' + [guid]::NewGuid().ToString('N').Substring(0, 8)) } catch { $thrown = $_ }
        Assert-True ("$thrown" -match 'exit code 1060') "Invoke-Sc to throw with sc's exit code, got: $thrown"
    }
}

Describe "the command-line splitter these tests rely on" {
    BeforeAll { . (Join-Path $PSScriptRoot 'ScriptTestHelpers.ps1') }

    It "follows the C runtime's quote and backslash rules" {
        Assert-Equal @('a b', 'c') (Split-CommandLine '"a b" c')
        Assert-Equal @('say "hi"') (Split-CommandLine '"say \"hi\""')
        Assert-Equal @('C:\dir\', 'x') (Split-CommandLine '"C:\dir\\" x')
        Assert-Equal @('a\\b') (Split-CommandLine 'a\\b')
        # Unescaped, the inner quote closes the section: the 5.1 failure mode.
        Assert-Equal @('binPath=', 'C:\Program', 'Files\x.exe --urls') (Split-CommandLine 'binPath= "C:\Program" Files\x.exe" --urls"')
    }
}
