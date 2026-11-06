# Assert.ps1 -- a dependency-free test harness (no Pester: Windows PowerShell 5.1 ships an old version).
# Test files call  Test 'what it checks' { ...asserts... }  ; Run-Tests.ps1 counts the results.
# Copied from pipe-review (tests/Assert.ps1 at d4b33aa, 2026-11-06), unchanged.

$script:TestResults = New-Object System.Collections.Generic.List[object]
$script:CurrentFile = ''

function Test {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Body)
    $t0 = [DateTime]::UtcNow
    try {
        & $Body
        $script:TestResults.Add([pscustomobject]@{ File = $script:CurrentFile; Name = $Name; Ok = $true; Message = ''; Ms = ([DateTime]::UtcNow - $t0).TotalMilliseconds })
        Write-Host ("  ok    {0}" -f $Name)
    }
    catch {
        $msg = $_.Exception.Message
        if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) { $msg = "$msg (line $($_.InvocationInfo.ScriptLineNumber))" }
        $script:TestResults.Add([pscustomobject]@{ File = $script:CurrentFile; Name = $Name; Ok = $false; Message = $msg; Ms = 0 })
        Write-Host ("  FAIL  {0}`n        {1}" -f $Name, $msg) -ForegroundColor Red
    }
}

function Assert-True {
    param($Condition, [string]$Message = 'expected true')
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message = '')
    $a = if ($null -eq $Actual) { '<null>' } else { [string]$Actual }
    $e = if ($null -eq $Expected) { '<null>' } else { [string]$Expected }
    if ($a -cne $e) { throw ("{0} expected '{1}' got '{2}'" -f $Message, $e, $a).Trim() }
}

function Assert-Near {
    param([double]$Actual, [double]$Expected, [double]$Tolerance = 1e-9, [string]$Message = '')
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) { throw ("{0} expected {1} +/- {2} got {3}" -f $Message, $Expected, $Tolerance, $Actual).Trim() }
}

function Assert-Match {
    param([string]$Actual, [string]$Pattern, [string]$Message = '')
    if ($Actual -notmatch $Pattern) { throw ("{0} '{1}' does not match /{2}/" -f $Message, $Actual, $Pattern).Trim() }
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Body, [string]$Pattern = '.')
    $threw = $false
    try { & $Body } catch {
        $threw = $true
        $m = $_.Exception.Message
        if ($_.Exception.InnerException) { $m = "$m $($_.Exception.InnerException.Message)" }
        if ($m -notmatch $Pattern) { throw "threw, but the message '$m' does not match /$Pattern/" }
    }
    if (-not $threw) { throw "expected an exception matching /$Pattern/" }
}

function New-TestDir {
    param([string]$Name)
    $root = Join-Path (Join-Path $PSScriptRoot 'out') $Name
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    New-Item -ItemType Directory -Path $root | Out-Null
    return (Resolve-Path -LiteralPath $root).Path
}
