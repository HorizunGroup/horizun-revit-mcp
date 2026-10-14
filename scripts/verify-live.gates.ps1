#Requires -Version 5.1
<#
  Execution gates of verify-live.ps1, kept apart so they can be tested without Revit
  (scripts/verify-live-gates.tests.ps1).
#>

# A probe whose prerequisite was never supplied is NOT EXECUTED. Its 'Needs' names a
# variable of verify-live; empty, null or absent means the prerequisite is missing and
# the probe is reported not_covered by the report loop - which used to be the ONLY
# place this was checked, AFTER the probe had already been sent. A close whose paths
# were never discovered closed the write document all the same (measured 2026-10-09).
function Test-HzProbeRunnable {
    param([Parameter(Mandatory)][hashtable]$Probe, [Parameter(Mandatory)][scriptblock]$Resolve)
    if (-not $Probe.Needs) { return $true }
    $supplied = & $Resolve $Probe.Needs
    return -not [string]::IsNullOrWhiteSpace([string]$supplied)
}

# execute_python is deliberately absent from tools/list when the machine owner
# leaves arbitrary code disabled. This is a named coverage gap, not evidence that
# a published tool was renamed or deleted. Other absent tools remain dangling.
function Test-HzPolicyHiddenTool {
    param([string]$Tool, [string[]]$PublishedNames)
    return $Tool -eq 'horizun_execute_python' -and
           $PublishedNames -notcontains 'horizun_execute_python'
}

# -OldFile proves the UPGRADE guard. A file the running Revit saved itself is no upgrade:
# it opens, becomes the active document and takes the write tier with it (HZ_OLD_2023 in
# Revit 2023, measured 2026-10-09). The version comes from the file's own header, read
# without opening it; when it cannot be read the probe runs as it always did.
function Get-HzOldFileGate {
    param([string]$SavedVersion, [Parameter(Mandatory)][int]$RevitYear)
    if ([string]$SavedVersion -match '(20\d\d)') {
        $saved = [int]$Matches[1]
        if ($saved -eq $RevitYear) {
            return @{ Run = $false; Reason = ('fixture_same_version: -OldFile was saved by Revit {0}, the version running, so opening it is not an upgrade and would only take the active document' -f $saved) }
        }
        return @{ Run = $true; Reason = ('saved by Revit {0}, running {1}' -f $saved, $RevitYear) }
    }
    return @{ Run = $true; Reason = 'the saved version is unreadable; the probe runs as before' }
}

# ---------------------------------------------------------------------------
# REVIT DIED DURING THE RUN. Autodesk's crash reporter (CER) writes a minidump under
# %LOCALAPPDATA%\Autodesk\CER\<product>\<id>\*.dmp.zip. One signature is known and is
# Autodesk's, not Horizun's (docs/RELEASE-POLICY.md, "Revit 2027 ADP crash"): an access
# violation in AdpSDKCore.dll at +0x11b98, on a thread that is not Revit's main thread,
# in Revit 2027. Anything else - or no dump at all - is unclassified and fails the gate.
# An Autodesk crash is NEVER a pass either: the run is repeated and only a clean
# repetition counts.
# ---------------------------------------------------------------------------
$script:HzAdpSignature = @{ Code = [Convert]::ToUInt32('C0000005', 16); Module = 'AdpSDKCore.dll'; Offset = [uint64]0x11b98; Year = 2027 }
$script:HzRevitHeapSignature = @{ Code = [Convert]::ToUInt32('C0000374', 16); Module = 'ntdll.dll'; Offset = [uint64]0x117eb5; Year = 2027; ThreadIndex = 0 }

# Exception code, faulting module + offset, and the faulting thread's index in the
# thread list (Revit's main thread is the first), from a minidump's bytes. Null when
# the bytes are not a minidump or carry no exception stream.
function Read-HzMinidump {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    try {
        if ($Bytes.Length -lt 32 -or [BitConverter]::ToUInt32($Bytes, 0) -ne [uint32]0x504D444D) { return $null }
        $n = [BitConverter]::ToUInt32($Bytes, 8); $dir = [BitConverter]::ToUInt32($Bytes, 12)
        $streams = @{}
        for ($i = 0; $i -lt $n; $i++) {
            $o = $dir + 12 * $i
            $type = [BitConverter]::ToUInt32($Bytes, $o)
            if (-not $streams.ContainsKey($type)) { $streams[$type] = [BitConverter]::ToUInt32($Bytes, $o + 8) }
        }
        if (-not $streams.ContainsKey([uint32]6)) { return $null }
        $exc = $streams[[uint32]6]
        $tid = [BitConverter]::ToUInt32($Bytes, $exc)
        $code = [BitConverter]::ToUInt32($Bytes, $exc + 8)
        $addr = [BitConverter]::ToUInt64($Bytes, $exc + 24)
        $threadIndex = -1
        if ($streams.ContainsKey([uint32]3)) {
            $t = $streams[[uint32]3]; $count = [BitConverter]::ToUInt32($Bytes, $t)
            for ($i = 0; $i -lt $count; $i++) { if ([BitConverter]::ToUInt32($Bytes, $t + 4 + 48 * $i) -eq $tid) { $threadIndex = $i; break } }
        }
        $module = $null; $offset = $null
        if ($streams.ContainsKey([uint32]4)) {
            $m = $streams[[uint32]4]; $count = [BitConverter]::ToUInt32($Bytes, $m)
            for ($i = 0; $i -lt $count; $i++) {
                $e = $m + 4 + 108 * $i
                $base = [BitConverter]::ToUInt64($Bytes, $e); $size = [BitConverter]::ToUInt32($Bytes, $e + 8)
                if ($addr -ge $base -and $addr -lt ($base + $size)) {
                    $nameRva = [BitConverter]::ToUInt32($Bytes, $e + 20)
                    $len = [BitConverter]::ToUInt32($Bytes, $nameRva)
                    $full = [Text.Encoding]::Unicode.GetString($Bytes, $nameRva + 4, $len)
                    $module = [IO.Path]::GetFileName($full); $offset = [uint64]($addr - $base); break
                }
            }
        }
        return @{ Code = $code; Address = $addr; Module = $module; Offset = $offset; ThreadIndex = $threadIndex }
    }
    catch { return $null }
}

function Get-HzCrashClassification {
    param($Info, [Parameter(Mandatory)][int]$RevitYear)
    $s = $script:HzAdpSignature
    if ($Info -and $RevitYear -eq $s.Year -and [uint32]$Info.Code -eq $s.Code -and
        [string]$Info.Module -eq $s.Module -and $null -ne $Info.Offset -and [uint64]$Info.Offset -eq $s.Offset -and
        [int]$Info.ThreadIndex -gt 0) { return 'revit_crash_autodesk_adp' }
    $s = $script:HzRevitHeapSignature
    if ($Info -and $RevitYear -eq $s.Year -and [uint32]$Info.Code -eq $s.Code -and
        [string]$Info.Module -eq $s.Module -and $null -ne $Info.Offset -and [uint64]$Info.Offset -eq $s.Offset -and
        [int]$Info.ThreadIndex -eq $s.ThreadIndex) { return 'revit_crash_revit_heap' }
    return 'revit_crash_unclassified'
}

# Every CER dump written since the run started, newest last.
function Find-HzNewCerDumps {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][datetime]$Since)
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.dmp.zip' -File -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -ge $Since } | Sort-Object LastWriteTime | ForEach-Object { $_.FullName })
}

# The minidump inside a CER zip, read in memory: nothing is extracted to disk.
function Read-HzCerDumpZip {
    param([Parameter(Mandatory)][string]$Path)
    try {
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entry = $zip.Entries | Where-Object { $_.Name -like '*.dmp' } | Select-Object -First 1
            if (-not $entry) { return $null }
            $s = $entry.Open(); $ms = New-Object IO.MemoryStream
            try { $s.CopyTo($ms) } finally { $s.Dispose() }
            return Read-HzMinidump -Bytes $ms.ToArray()
        } finally { $zip.Dispose() }
    } catch { return $null }
}

# One verdict for a run in which Revit died. Every new dump must be the known Autodesk
# crash for the verdict to be revit_crash_autodesk_adp; no dump, or any other dump, is
# revit_crash_unclassified. Neither ever passes.
function Get-HzRevitCrashVerdict {
    param([object[]]$Dumps = @(), [Parameter(Mandatory)][int]$RevitYear, [string]$ProbeInProgress)
    $rows = @($Dumps | ForEach-Object { @{ Path = $_.Path; Class = (Get-HzCrashClassification -Info $_.Info -RevitYear $RevitYear); Info = $_.Info } })
    $state = 'revit_crash_unclassified'
    if ($rows.Count -gt 0 -and -not @($rows | Where-Object { $_.Class -ne $rows[0].Class }).Count -and
        $rows[0].Class -in @('revit_crash_autodesk_adp', 'revit_crash_revit_heap')) { $state = $rows[0].Class }
    $dump = $null; if ($rows.Count -gt 0) { $dump = $rows[$rows.Count - 1].Path }
    $why = if ($rows.Count -eq 0) { 'Revit died and Autodesk''s crash reporter left no dump since the run started' }
           elseif ($state -eq 'revit_crash_autodesk_adp') { 'the known Autodesk ADP crash (AdpSDKCore.dll+0x11b98, 0xC0000005, not the main thread, Revit 2027): repeat the run; only a clean repetition counts' }
           elseif ($state -eq 'revit_crash_revit_heap') { 'the owner-accepted Revit 2027 heap-corruption crash (ntdll.dll+0x117eb5, 0xC0000374, main thread): repeat the run; only a clean repetition counts' }
           else { 'a dump that is not the known Autodesk ADP crash: treated as a failure until somebody reads it' }
    return @{ State = $state; Dump = $dump; ProbeInProgress = $ProbeInProgress; Repeat = $true; Passes = $false; Why = $why
              Dumps = @($rows | ForEach-Object { @{ path = $_.Path; class = $_.Class } }) }
}
