# Shared by the *.Tests.ps1 files here; dot-sourced in their BeforeAll blocks.
#
# The tests run under Pester 3.4 (built into Windows PowerShell 5.1) as well as
# Pester 5, so they assert by throwing rather than with Should, whose syntax
# differs between the two.

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Expected: $Message" }
}

function Assert-Equal($Expected, $Actual, [string]$Message = "values differ") {
    $e = ConvertTo-Json -InputObject $Expected -Compress -Depth 5
    $a = ConvertTo-Json -InputObject $Actual -Compress -Depth 5
    if ($e -cne $a) { throw "$Message`n  expected: $e`n  actual:   $a" }
}

# Defines, in the caller's scope, the named functions of a script without running
# the script (installers need elevation and build the whole application).
# Use as:  . (Import-ScriptFunction $path 'Name1', 'Name2')
function Import-ScriptFunction([string]$Path, [string[]]$Name) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$Path does not parse: $($errors[0].Message)" }
    $text = foreach ($n in $Name) {
        $fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $n }, $true)
        if (-not $fn) { throw "$Path no longer defines $n" }
        $fn.Extent.Text
    }
    [scriptblock]::Create(($text -join "`n"))
}

# Splits a Windows command line into arguments the way sc.exe (the C runtime)
# does: 2n backslashes before a quote are n backslashes and the quote opens or
# closes a quoted section; 2n+1 are n backslashes and a literal quote; other
# backslashes are literal; whitespace outside quotes separates arguments.
function Split-CommandLine([string]$CommandLine) {
    $result = New-Object System.Collections.Generic.List[string]
    $current = New-Object System.Text.StringBuilder
    $inQuotes = $false; $started = $false; $i = 0
    while ($i -lt $CommandLine.Length) {
        $c = $CommandLine[$i]
        if ($c -eq '\') {
            $n = 0
            while ($i -lt $CommandLine.Length -and $CommandLine[$i] -eq '\') { $n++; $i++ }
            if ($i -lt $CommandLine.Length -and $CommandLine[$i] -eq '"') {
                [void]$current.Append('\' * ($n -shr 1))
                if ($n % 2 -eq 1) { [void]$current.Append('"'); $i++ }
            } else {
                [void]$current.Append('\' * $n)
            }
            $started = $true
        } elseif ($c -eq '"') {
            $inQuotes = -not $inQuotes; $started = $true; $i++
        } elseif (($c -eq ' ' -or $c -eq "`t") -and -not $inQuotes) {
            if ($started) { $result.Add($current.ToString()); [void]$current.Clear(); $started = $false }
            $i++
        } else {
            [void]$current.Append($c); $started = $true; $i++
        }
    }
    if ($started) { $result.Add($current.ToString()) }
    , $result.ToArray()
}
