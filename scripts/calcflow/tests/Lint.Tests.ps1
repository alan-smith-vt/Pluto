# Lint.Tests.ps1 -- rules that keep CalcFlow portable and free of project data, over the files CalcFlow owns:
# scripts\calcflow, the frame reader and package writer in scripts\lib, and the notes in vault\calcflow.
#   * code files are ASCII without a BOM (Windows PowerShell 5.1 reads BOM-less files as ANSI)
#   * every .ps1 parses (under 5.1 this also rejects PowerShell 7-only syntax)
#   * no PowerShell 7-only operators (?? ?. ?[ && || ternary), checked from the tokens when running on 7
#   * no C# 6+ syntax in the C# sources
#   * no project-identifying words: tests\denylist.local.txt (gitignored, one regex per line) is checked against every
#     file in scope when it exists on this machine
# Copied from pipe-review (tests/Lint.Tests.ps1 at d4b33aa, 2026-11-06): scoped to CalcFlow's files instead of a repo.

$skipDirs = '[\\/](\.git|out|bin|obj)[\\/]'
$lib = Join-Path $script:Repo 'scripts'
$lib = Join-Path $lib 'lib'
$scope = @(Get-ChildItem -LiteralPath $script:CalcFlow -Recurse -File -Force)
$scope += @(Get-Item -LiteralPath (Join-Path (Join-Path $lib 'readers') 'AnlFrameReader.cs'))
$scope += @(Get-Item -LiteralPath (Join-Path (Join-Path $lib 'exporters') 'FramePackageWriter.cs'))
$notes = Join-Path (Join-Path $script:Repo 'vault') 'calcflow'
if (Test-Path -LiteralPath $notes) { $scope += @(Get-ChildItem -LiteralPath $notes -Recurse -File) }
$all = @($scope | Where-Object { $_.FullName -notmatch $skipDirs })
$codeExt = @('.ps1', '.psd1', '.cs', '.cmd', '.yml', '.csproj')
$textExt = $codeExt + @('.md', '.txt', '.anl')

Test 'code files are ASCII and no text file has a BOM' {
    $bad = @()
    foreach ($f in $all | Where-Object { $textExt -contains $_.Extension.ToLowerInvariant() }) {
        $b = [IO.File]::ReadAllBytes($f.FullName)
        if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { $bad += "$($f.Name): BOM" }
        if ($codeExt -contains $f.Extension.ToLowerInvariant()) {
            for ($i = 0; $i -lt $b.Length; $i++) { if ($b[$i] -gt 127) { $bad += "$($f.Name): non-ASCII byte at $i"; break } }
        }
    }
    Assert-True ($bad.Count -eq 0) ($bad -join '; ')
}

Test 'every PowerShell file parses' {
    $bad = @()
    foreach ($f in $all | Where-Object { $_.Extension -eq '.ps1' -or $_.Extension -eq '.psd1' }) {
        $tokens = $null; $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) { $bad += "$($f.Name): $($errors[0].Message) (line $($errors[0].Extent.StartLineNumber))" }
    }
    Assert-True ($bad.Count -eq 0) ($bad -join '; ')
}

Test 'no PowerShell 7-only operators (CalcFlow must also run on Windows PowerShell 5.1)' {
    if ($PSVersionTable.PSEdition -ne 'Core') { return }   # 5.1 rejects them when parsing (test above)
    $forbidden = @('QuestionQuestion', 'QuestionQuestionEquals', 'QuestionDot', 'QuestionLBracket', 'AndAnd', 'OrOr', 'QuestionMark')
    $bad = @()
    foreach ($f in $all | Where-Object { $_.Extension -eq '.ps1' -or $_.Extension -eq '.psd1' }) {
        $tokens = $null; $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
        foreach ($t in $tokens) { if ($forbidden -contains $t.Kind.ToString()) { $bad += "$($f.Name) line $($t.Extent.StartLineNumber): $($t.Text)" } }
    }
    Assert-True ($bad.Count -eq 0) ($bad -join '; ')
}

Test 'C# sources avoid C# 6+ syntax (string interpolation, ?., nameof)' {
    $bad = @()
    foreach ($f in $all | Where-Object { $_.Extension -eq '.cs' }) {
        $n = 0
        foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
            $n++
            $code = ($line -replace '"([^"\\]|\\.)*"', '""') -replace '//.*$', ''
            if ($code -match '\$"|\?\.[A-Za-z]|\?\[|\bnameof\s*\(') { $bad += "$($f.Name) line $n" }
        }
    }
    Assert-True ($bad.Count -eq 0) ($bad -join '; ')
}

Test 'no project-identifying words (local denylist)' {
    $deny = Join-Path $PSScriptRoot 'denylist.local.txt'
    if (-not (Test-Path -LiteralPath $deny)) { Write-Host '        (no tests\denylist.local.txt on this machine: skipped)' -ForegroundColor DarkGray; return }
    $patterns = @(Get-Content -LiteralPath $deny | Where-Object { $_.Trim() -ne '' -and -not $_.StartsWith('#') })
    $bad = @()
    foreach ($f in $all | Where-Object { $_.Name -ne 'denylist.local.txt' }) {
        foreach ($p in $patterns) { if ($f.Name -match $p) { $bad += "file name $($f.Name) matches /$p/" } }
        if ($f.Extension -in '.xlsx', '.pdf', '.png') { continue }
        $text = [IO.File]::ReadAllText($f.FullName)
        foreach ($p in $patterns) { if ($text -match $p) { $bad += "$($f.Name) matches /$p/" } }
    }
    Assert-True ($bad.Count -eq 0) ($bad -join '; ')
}
