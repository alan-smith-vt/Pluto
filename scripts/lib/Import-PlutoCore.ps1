# Compiles the core C# (scripts\lib\**\*.cs, nothing else) into PlutoCore-<hash>.dll and loads it into this
# session (Windows PowerShell 5.1, Framework csc via Add-Type). Dot-source it; sets $PlutoCoreDll and
# $PlutoRefs (the Framework references, full paths) for batches that build on the core (Import-Arms.ps1).
# Core only: domain code (arms, duct model) compiles in its own batch against this dll.
# The dll is cached under %TEMP%\pluto-cs\ (or $env:PLUTO_CS_CACHE), keyed on every source file (path + content), the reference list
# and the runtime, so an unchanged core is not recompiled and a changed one never reuses a stale build.
# Fallback ($PlutoCoreDll = $null): if the dll cannot be written or loaded (e.g. an allow-list policy on
# user temp folders), the core compiles in memory as before; Import-Arms.ps1 then compiles core + arms in
# one in-memory batch (it sets $PlutoWantArms so the core is not compiled twice).

# References by full path where the Framework folder has the dll. A bare name only resolves while nothing
# else is loaded: once the SAP API is in the session every bare name fails with 0x80131502
# (2026-10-30, Run-Sap -Export). A name with no dll there is passed on as before.
if (-not $PlutoRefs) {
    $fwDir = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    $excelAssembly = [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.Office.Interop.Excel")
    $PlutoRefs = @(@($(if ($excelAssembly) { $excelAssembly.Location })) + @("System.Console", "System.IO",
        "System.Collections", "System.Numerics",
        "System.Numerics.Vectors", "System.Web.Extensions",
        "System.Net.Http", "System.Xml", "System.Xml.Linq",
        "System.IO.Compression", "System.IO.Compression.FileSystem") | ForEach-Object {
        $p = Join-Path $fwDir "$_.dll"
        if ($_ -like "*\*") { $_ } elseif (Test-Path $p) { $p } else { $_ }
    } | Where-Object { $_ })
}

# Compiles $Sources (+ $References) into a cached dll <Name>-<hash>.dll and loads it; returns its path, or
# $null when the dll cannot be written or loaded (the caller then compiles in memory).
function Import-PlutoCs([string]$Name, [string[]]$Sources, [string[]]$References) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in ($Sources | Sort-Object)) { [void]$sb.Append($f).Append("`n").Append([IO.File]::ReadAllText($f)).Append("`n") }
    [void]$sb.Append(($References -join "`n")).Append("`n")
    [void]$sb.Append([System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()).Append([Environment]::Version)
    $hash = (-join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString())) | ForEach-Object { $_.ToString("x2") })).Substring(0, 16)
    $dir = $(if ($env:PLUTO_CS_CACHE) { $env:PLUTO_CS_CACHE } else { Join-Path ([IO.Path]::GetTempPath()) "pluto-cs" })
    $dll = Join-Path $dir "$Name-$hash.dll"
    $tag = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    # 1. the cache folder must be a writable folder (else: in memory)
    try {
        if (-not (Test-Path $dir -PathType Container)) { New-Item -ItemType Directory $dir -Force -ErrorAction Stop | Out-Null }
        $probe = Join-Path $dir "probe-$tag.tmp"
        [IO.File]::WriteAllText($probe, ""); Remove-Item $probe -ErrorAction SilentlyContinue
    } catch { Write-Warning ("Pluto: no C# cache at {0} ({1}); compiling in memory" -f $dir, $_.Exception.Message); return $null }
    # 2. build (a compile error is real and is thrown), under a unique name, then rename: two processes
    #    building the same hash both end with one dll; "already there" / "in use" counts as a cache hit
    if (-not (Test-Path $dll)) {
        $tmp = Join-Path $dir "$Name-$hash.$tag.tmp.dll"
        Add-Type -Path $Sources -ReferencedAssemblies $References -OutputAssembly $tmp -OutputType Library -ErrorAction Stop
        try { Move-Item $tmp $dll -ErrorAction Stop } catch { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
    # 3. load (an allow-list policy may refuse a dll from a user folder: in memory)
    try { [void][System.Reflection.Assembly]::LoadFrom($dll); return $dll }
    catch { Write-Warning ("Pluto: cached build {0} not loadable ({1}); compiling in memory" -f $dll, $_.Exception.Message); return $null }
}

if (-not ('FeaturesSidecar' -as [type])) {
    $coreCs = Get-ChildItem -Path $PSScriptRoot -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
    $PlutoCoreDll = Import-PlutoCs "PlutoCore" $coreCs $PlutoRefs
    if (-not $PlutoCoreDll -and -not $PlutoWantArms) { Add-Type -Path $coreCs -ReferencedAssemblies $PlutoRefs }
} elseif (-not $PlutoCoreDll) {
    $loc = ([type]'FeaturesSidecar').Assembly.Location
    if ($loc) { $PlutoCoreDll = $loc }
}
