#Requires -Version 5.1
# Exercises health-state.probes.ps1 WITHOUT Revit: a fake horizun_health reply in
# the shapes HealthCommand.cs actually produces (workshared with a scan, not
# workshared, and the not-yet-any-batches / has-batches journal states).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'health-state.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'health-state' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function Reply($data) { [pscustomobject]@{ isError = $false; data = $data; text = '' } }

function New-Health([bool]$Workshared, [int]$Batches) {
    $ws = if ($Workshared) {
        [pscustomobject]@{
            workshared = $true; username = 'pablo'; owned_worksets = @('Workset1')
            borrowed_by_me = [pscustomobject]@{ complete = $true; elements_checked = 120; elements_total_candidates = 120; owned_by_current_user_count = 4; elapsed_ms = 12 }
        }
    }
    else {
        [pscustomobject]@{ workshared = $false; note = 'This document is not workshared: there is no borrow/ownership concept to report.' }
    }
    $recent = @()
    for ($i = 0; $i -lt [Math]::Min($Batches, 5); $i++) { $recent += [pscustomobject]@{ tool = 'horizun_write_params_verified'; created_utc = '2026-09-25T00:00:0' + $i + 'Z'; entries = 1 } }
    $rw = [pscustomobject]@{
        source = "Horizun's own write journal (the one horizun_undo reverses), NOT Revit's Undo stack"
        batches_recorded_total = $Batches
        most_recent = $recent
        note = 'The Revit API does NOT expose its Undo/Redo stack to an add-in - there is no method that lists what Ctrl+Z would undo.'
    }
    return Reply ([pscustomobject]@{ workshare_status = $ws; recent_horizun_writes = $rw })
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-health-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $call = { param($tool, $a) New-Health $true 3 }.GetNewClosure()
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-1'; WriteGate = $false; Call = $call; Apply = { throw 'not used' } }
    $cases = @(& $module.Run $ctx)
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0)
    Check 'a workshared document with batches passes both cases' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
    if ($fails) { $cases | ForEach-Object { "    $($_.Name): $($_.Outcome) $($_.Detail)" } }

    $callNws = { param($tool, $a) New-Health $false 0 }.GetNewClosure()
    $ctx2 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-2'; WriteGate = $true; Call = $callNws; Apply = { throw 'not used' } }
    $by2 = @{}; foreach ($c in @(& $module.Run $ctx2)) { $by2[$c.Name] = $c }
    Check 'not-workshared, gated tier, zero batches still passes (an honest empty journal is a valid shape)' (
        @($by2.Values | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)

    $callBadShape = { param($tool, $a) Reply ([pscustomobject]@{ workshare_status = $null; recent_horizun_writes = $null }) }.GetNewClosure()
    $ctx3 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-3'; WriteGate = $true; Call = $callBadShape; Apply = { throw 'not used' } }
    $by3 = @{}; foreach ($c in @(& $module.Run $ctx3)) { $by3[$c.Name] = $c }
    Check 'missing blocks fail rather than reading as an accident of "no document"' (
        @($by3.Values | Where-Object { $_.Outcome -ne 'fail' }).Count -eq 0)

    $callOpenNoBatches = { param($tool, $a) New-Health $true 0 }.GetNewClosure()
    $ctx4 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-4'; WriteGate = $false; Call = $callOpenNoBatches; Apply = { throw 'not used' } }
    $by4 = @{}; foreach ($c in @(& $module.Run $ctx4)) { $by4[$c.Name] = $c }
    Check 'an OPEN write tier with zero batches recorded fails the journal case (the harness expects prior writes by now)' (
        $by4['health: recent_horizun_writes names its journal, not Revit Undo, honestly'].Outcome -eq 'fail')
}
finally {
    Get-ChildItem -LiteralPath $scratch -File -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Remove-Item -LiteralPath $scratch -ErrorAction SilentlyContinue
}
if ($fails) { "health-state probe tests: $fails FAILED"; exit 1 } else { 'health-state probe tests: ALL PASS'; exit 0 }
