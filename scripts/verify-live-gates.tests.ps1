#Requires -Version 5.1
<#
  Offline tests for the two execution gates of verify-live.ps1 (no Revit, no server).

  1. A probe whose prerequisite (Needs) was never supplied is NOT EXECUTED. It used to
     be executed and only REPORTED as not covered afterwards - so 'close the open source
     before loading its detached closed-workset copy' closed the write document even
     though the closed-workset paths were never discovered, and the whole write tier
     then stayed shut (measured 2026-10-09 in Revit 2026, revit-2026.log 10:11:35).
  2. -OldFile exists to prove the upgrade guard. A file saved by the SAME Revit version
     is not an upgrade: it opens, becomes the active document and takes the write tier
     with it (measured 2026-10-09, HZ_OLD_2023 in Revit 2023). Such a probe is
     not_covered with the reason fixture_same_version, never executed.
#>
$ErrorActionPreference = 'Stop'
$failures = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:failures++ }
}

. (Join-Path $PSScriptRoot 'verify-live.gates.ps1')

# ---- 1. Needs ------------------------------------------------------------------
$vars = @{ Document = 'HZ_WRITE'; ClosedWorksetActivationReady = $null; Empty = '' }
$resolve = { param($n) $vars[$n] }
Check 'a probe without Needs runs' (Test-HzProbeRunnable -Probe @{ Name = 'a' } -Resolve $resolve)
Check 'a probe whose Needs is supplied runs' (Test-HzProbeRunnable -Probe @{ Name = 'b'; Needs = 'Document' } -Resolve $resolve)
Check 'a probe whose Needs was never supplied does NOT run' (-not (Test-HzProbeRunnable -Probe @{ Name = 'c'; Needs = 'ClosedWorksetActivationReady' } -Resolve $resolve))
Check 'an empty string is not a supplied prerequisite' (-not (Test-HzProbeRunnable -Probe @{ Name = 'd'; Needs = 'Empty' } -Resolve $resolve))
Check 'an unknown prerequisite is not supplied' (-not (Test-HzProbeRunnable -Probe @{ Name = 'e'; Needs = 'NoSuchVariable' } -Resolve $resolve))

# ---- 2. OldFile ----------------------------------------------------------------
$same = Get-HzOldFileGate -SavedVersion '2023' -RevitYear 2023
Check 'a file saved by the running version is not run' (-not $same.Run)
Check 'and its reason is fixture_same_version' ($same.Reason -like 'fixture_same_version*')
$older = Get-HzOldFileGate -SavedVersion '2023' -RevitYear 2026
Check 'a file saved by an earlier version runs' ($older.Run)
$unknown = Get-HzOldFileGate -SavedVersion $null -RevitYear 2026
Check 'an unreadable version runs as it always did, and says so' ($unknown.Run -and $unknown.Reason -like '*unreadable*')
$text = Get-HzOldFileGate -SavedVersion 'Autodesk Revit 2025 (Build: 25.0)' -RevitYear 2025
Check 'a version written as text is read for its year' (-not $text.Run)

# ---- 2b. A policy-hidden Python tool is an explicit live gap -----------------
$withoutPython = @('horizun_health', 'horizun_request_python_access')
$withPython = @($withoutPython + 'horizun_execute_python')
Check 'Python hidden by owner policy is identified as a gap' (Test-HzPolicyHiddenTool -Tool 'horizun_execute_python' -PublishedNames $withoutPython)
Check 'a published Python tool is not treated as hidden' (-not (Test-HzPolicyHiddenTool -Tool 'horizun_execute_python' -PublishedNames $withPython))
Check 'a different missing tool is still a dangling probe' (-not (Test-HzPolicyHiddenTool -Tool 'horizun_query_model' -PublishedNames $withoutPython))

# ---- 3. verify-live.ps1 uses both gates ------------------------------------------
$src = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-live.ps1') -Raw
Check 'verify-live dot-sources the gates' ($src -match "\. \(Join-Path \`$PSScriptRoot 'verify-live\.gates\.ps1'\)")
Check 'the execution loop asks Test-HzProbeRunnable before sending a probe' ($src -match 'if \(-not \(Test-HzProbeRunnable -Probe \$p')
Check 'the -OldFile probe is gated by Get-HzOldFileGate' ($src -match 'Get-HzOldFileGate -SavedVersion')
Check 'verify-live records policy-hidden Python probes without counting them as dangling' ($src -match 'Test-HzPolicyHiddenTool -Tool \$p\.Tool' -and $src -match 'Test-HzPolicyHiddenTool -Tool \$w\.Tool')

# ---- 4. Revit died: classify the CER dump -------------------------------------
# A SYNTHETIC minidump, built here byte by byte: a real one carries this machine's paths
# and never enters the repository. Two threads (the first is Revit's main thread), two
# modules, and an exception record on the thread and at the address the test chooses.
function New-SyntheticMinidump {
    param([uint32]$Code, [string]$FaultModule, [uint64]$FaultOffset, [int]$FaultThreadIndex)
    $ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter($ms)
    $threadIds = @(4100, 4200)
    $modules = @(@{ Name = 'C:\Program Files\Autodesk\Revit 2027\Revit.exe'; Base = [uint64]0x7ff600000000; Size = [uint32]0x01000000 },
                 @{ Name = ('C:\Program Files\Common Files\Autodesk\AdpDesktopSDK\bin\' + 'AdpSDKCore.dll'); Base = [uint64]0x7ff9df910000; Size = [uint32]0x00200000 },
                 @{ Name = 'C:\Program Files\Autodesk\Revit 2027\Utility.dll'; Base = [uint64]0x7ffa00000000; Size = [uint32]0x00100000 })
    $dirRva = 32; $threadRva = $dirRva + 3 * 12; $moduleRva = $threadRva + 4 + 48 * $threadIds.Count
    $excRva = $moduleRva + 4 + 108 * $modules.Count; $stringsRva = $excRva + 168
    # header
    $w.Write([uint32]0x504D444D); $w.Write([uint32]0xA793); $w.Write([uint32]3); $w.Write([uint32]$dirRva)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([uint64]0)
    # stream directory: thread list (3), module list (4), exception (6)
    $w.Write([uint32]3); $w.Write([uint32](4 + 48 * $threadIds.Count)); $w.Write([uint32]$threadRva)
    $w.Write([uint32]4); $w.Write([uint32](4 + 108 * $modules.Count)); $w.Write([uint32]$moduleRva)
    $w.Write([uint32]6); $w.Write([uint32]168); $w.Write([uint32]$excRva)
    # threads
    $w.Write([uint32]$threadIds.Count)
    foreach ($t in $threadIds) { $w.Write([uint32]$t); $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([uint64]0)
                                 $w.Write([uint64]0); $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([uint32]0) }
    # modules, names after the exception stream
    $w.Write([uint32]$modules.Count)
    $nameRva = $stringsRva
    foreach ($m in $modules) {
        $w.Write([uint64]$m.Base); $w.Write([uint32]$m.Size); $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([uint32]$nameRva)
        $w.Write((New-Object byte[] 84))
        $nameRva += 4 + 2 * $m.Name.Length
    }
    # exception stream
    $faultBase = ($modules | Where-Object { $_.Name -like ('*\' + $FaultModule) } | Select-Object -First 1).Base
    $w.Write([uint32]$threadIds[$FaultThreadIndex]); $w.Write([uint32]0)
    $w.Write([uint32]$Code); $w.Write([uint32]0); $w.Write([uint64]0); $w.Write([uint64]($faultBase + $FaultOffset)); $w.Write([uint32]2); $w.Write([uint32]0)
    $w.Write([uint64]0); $w.Write([uint64]8); $w.Write((New-Object byte[] (13 * 8))); $w.Write([uint32]0); $w.Write([uint32]0)
    foreach ($m in $modules) { $w.Write([uint32](2 * $m.Name.Length)); $w.Write([Text.Encoding]::Unicode.GetBytes($m.Name)) }
    $w.Flush(); return ,$ms.ToArray()
}
$AV = [Convert]::ToUInt32('C0000005', 16); $CLR = [Convert]::ToUInt32('E0434352', 16)
$adp = Read-HzMinidump -Bytes (New-SyntheticMinidump -Code $AV -FaultModule 'AdpSDKCore.dll' -FaultOffset 0x11b98 -FaultThreadIndex 1)
Check 'the reader finds the exception code' ($adp.Code -eq $AV)
Check 'the reader names the faulting module and its offset' ($adp.Module -eq 'AdpSDKCore.dll' -and $adp.Offset -eq 0x11b98)
Check 'the reader says the fault was not on the first (main) thread' ($adp.ThreadIndex -eq 1)
Check 'that signature in Revit 2027 is Autodesk''s ADP crash' ((Get-HzCrashClassification -Info $adp -RevitYear 2027) -eq 'revit_crash_autodesk_adp')
Check 'the same signature in another year is NOT classified' ((Get-HzCrashClassification -Info $adp -RevitYear 2026) -eq 'revit_crash_unclassified')
$mainThread = Read-HzMinidump -Bytes (New-SyntheticMinidump -Code $AV -FaultModule 'AdpSDKCore.dll' -FaultOffset 0x11b98 -FaultThreadIndex 0)
Check 'the same fault on the MAIN thread is not the known crash' ((Get-HzCrashClassification -Info $mainThread -RevitYear 2027) -eq 'revit_crash_unclassified')
$otherOffset = Read-HzMinidump -Bytes (New-SyntheticMinidump -Code $AV -FaultModule 'AdpSDKCore.dll' -FaultOffset 0x11b99 -FaultThreadIndex 1)
Check 'another offset in the same module is not the known crash' ((Get-HzCrashClassification -Info $otherOffset -RevitYear 2027) -eq 'revit_crash_unclassified')
$otherCode = Read-HzMinidump -Bytes (New-SyntheticMinidump -Code $CLR -FaultModule 'AdpSDKCore.dll' -FaultOffset 0x11b98 -FaultThreadIndex 1)
Check 'another exception code is not the known crash' ((Get-HzCrashClassification -Info $otherCode -RevitYear 2027) -eq 'revit_crash_unclassified')
$utility = Read-HzMinidump -Bytes (New-SyntheticMinidump -Code $AV -FaultModule 'Utility.dll' -FaultOffset 0x11b98 -FaultThreadIndex 1)
Check 'another module is not the known crash' ((Get-HzCrashClassification -Info $utility -RevitYear 2027) -eq 'revit_crash_unclassified')
Check 'bytes that are not a minidump are refused, never classified' ($null -eq (Read-HzMinidump -Bytes ([Text.Encoding]::ASCII.GetBytes('not a dump at all, just text'))))

$noDump = Get-HzRevitCrashVerdict -Dumps @() -RevitYear 2027 -ProbeInProgress 'p'
Check 'a death with no dump is unclassified' ($noDump.State -eq 'revit_crash_unclassified')
$verdict = Get-HzRevitCrashVerdict -Dumps @(@{ Path = 'X:\cer\a.dmp.zip'; Info = $adp }) -RevitYear 2027 -ProbeInProgress 'load a family'
Check 'a death with the known dump is autodesk_adp, with the dump and the probe in progress' (
    $verdict.State -eq 'revit_crash_autodesk_adp' -and $verdict.Dump -eq 'X:\cer\a.dmp.zip' -and $verdict.ProbeInProgress -eq 'load a family')
$mixed = Get-HzRevitCrashVerdict -Dumps @(@{ Path = 'a'; Info = $adp }, @{ Path = 'b'; Info = $utility }) -RevitYear 2027 -ProbeInProgress 'p'
Check 'if ANY new dump is not the known crash, the verdict is unclassified' ($mixed.State -eq 'revit_crash_unclassified')
Check 'an Autodesk crash is never a pass: the run must be repeated' ($verdict.Repeat -eq $true -and $verdict.Passes -eq $false)

# The finder reads only *.dmp.zip written since the run started, under the root given.
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-cer-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $root 'prod\1') | Out-Null
$old = Join-Path $root 'prod\1\journal.0001.0001.dmp.zip'; Set-Content -LiteralPath $old -Value 'x'; (Get-Item $old).LastWriteTime = (Get-Date).AddHours(-2)
$new = Join-Path $root 'prod\1\journal.0002.0001.dmp.zip'; Set-Content -LiteralPath $new -Value 'x'
$found = @(Find-HzNewCerDumps -Root $root -Since (Get-Date).AddHours(-1))
Check 'only dumps written since the run started are found' ($found.Count -eq 1 -and $found[0] -eq $new)
Remove-Item -LiteralPath $root -Recurse -Force

Check 'verify-live classifies a Revit death before writing its report' ($src -match 'Get-HzRevitCrashVerdict -Dumps')

if ($failures) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'verify-live gates: ALL PASSED'
