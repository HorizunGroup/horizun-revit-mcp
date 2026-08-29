#Requires -Version 5.1
<#
  REINFORCEMENT AT SIZE - S, M and L, against budgets declared before the first
  measurement.

  Everything the structural slice has been proved on so far is one host and a
  handful of bars. That says nothing about what happens at the size a real
  deliverable has, and the specific risk is not "slow": it is a cost per bar
  that GROWS with the number of bars. The containment engine walks a mesh per
  sample and the audit compares every bar against every rule; either could be
  quadratic without a single test noticing, because every test so far runs at
  n = 30 where quadratic and linear look identical.

  So this harness measures the same work at three sizes and asks two questions.

  THE BUDGETS, DECLARED HERE BEFORE ANY OF IT RAN. They are not adjusted
  afterwards. A budget moved to accommodate the number it was meant to judge is
  not a budget, and a green obtained that way is worth less than an honest red.

    ABSOLUTE CEILING - is any single stage pathologically slow at L?
        plan     <=  180 s
        apply    <=  600 s
        audit    <=  300 s
        query    <=   60 s
      Generous on purpose: they are there to catch a stage that has fallen off a
      cliff, not to grade the machine. Every call also pays for a fresh
      horizun-mcp.exe and a Revit round trip, which at S is most of the time.

    SCALING - does the cost per bar grow with the number of bars?
        per-bar cost at L  <=  3x  per-bar cost at S,  for each of the four stages.
      This is the question that matters and the one an absolute number cannot
      answer. Three times allows for real constant overhead amortising the wrong
      way at S; it does not allow for O(n^2), which over this range would show up
      as roughly twenty times.

  WHAT IS MEASURED IS WALL CLOCK FROM THIS SCRIPT, including process start and
  transport. That is the honest number for "what does a caller wait", and it is
  the same overhead at all three sizes, so the scaling comparison is unaffected.

  Exit code 0 when everything passed; non-zero otherwise.
#>
[CmdletBinding()]
param(
    [string]$Document = 'HZ_WRITE',
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'structure-performance' -Document $Document

# This harness's own lane, clear of verify-rebar.ps1 at 920k and
# verify-rebar-geometry.ps1 at 928k.
$X = 936000.0
$TAG = $run.RunId.Substring($run.RunId.Length - 6)

# ---------------------------------------------------------------- THE BUDGETS
# Declared before the first measurement. Never edited to accommodate a result.
$CEILING = [ordered]@{ plan = 180.0; apply = 600.0; audit = 300.0; query = 60.0 }
$SCALING_FACTOR = 3.0

# The three sizes. Bars come from reinforcement rules rather than stirrup zones:
# a zone cannot be told its host's cover, so Revit shifts its array and the
# apply's own re-read reports a disagreement - ADR-003 item 7, backlog 9.20.
# Measured here first, as "0 of 12 rows were re-read as what was asked for". A
# timing taken over an apply whose verification FAILED is not a measurement of
# the product working, so the population is the path that does verify.
$SIZES = @(
    [ordered]@{ name = 'S'; walls = 1;  spacing = 400.0 }
    [ordered]@{ name = 'M'; walls = 4;  spacing = 200.0 }
    [ordered]@{ name = 'L'; walls = 12; spacing = 125.0 }
)

function Get-HzCount {
    param($Obj, [string[]]$Path)
    $v = Get-HzPath $Obj $Path
    if ($null -eq $v) { -1 } else { [int]$v }
}

# Time one bridge call and hand back both the reply and the seconds it took.
function Measure-HzCall {
    param($Run, [string]$Tool, [string]$Label, [hashtable]$Arguments, [switch]$Write)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok = $true; $why = $null; $result = $null
    try {
        if ($Write) {
            $r = Invoke-HzWrite -Run $Run -Tool $Tool -Label $Label -Arguments $Arguments
            $ok = [bool]$r.Ok
            if ($r.Apply) { $result = $r.Apply.Result }
        }
        else {
            $r = Invoke-HzTool -Run $Run -Tool $Tool -Label $Label -Arguments $Arguments
            $ok = [bool]$r.Ok
            $result = $r.Result
        }
    }
    catch { $ok = $false; $why = [string]$_.Exception.Message }
    $sw.Stop()
    @{ Ok = $ok; Result = $result; Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3); Why = $why }
}

# =====================================================================  FIXTURE

Write-Host "`n== fixture: walls to hang stirrups on ==" -ForegroundColor Cyan

$null = Reset-HzDocument $run

$level = Get-HzFirstLevel $run
if (-not $level) { throw 'HARNESS: the document has no level to build on.' }

# The bar type, provisioned the same way the other structural harnesses do it -
# and with the same honesty: creating one means choosing a diameter and a bend
# radius, which is designing, so the bridge refuses and the harness does it.
$barTypeName = "HZ_RP_$TAG"
$provisionCode = @"
from Autodesk.Revit.DB.Structure import RebarBarType
from Autodesk.Revit.DB import FilteredElementCollector, Transaction
d = __revit__.ActiveUIDocument.Document
existing = [t for t in FilteredElementCollector(d).OfClass(RebarBarType) if t.Name == '$barTypeName']
if existing:
    t = existing[0]
    made = False
else:
    tx = Transaction(d, 'HZ perf bar type')
    tx.Start()
    t = RebarBarType.Create(d)
    t.Name = '$barTypeName'
    t.BarNominalDiameter = 12.0 / 304.8
    t.BarModelDiameter = 12.0 / 304.8
    t.StandardBendDiameter = 48.0 / 304.8
    t.StandardHookBendDiameter = 48.0 / 304.8
    t.StirrupTieBendDiameter = 48.0 / 304.8
    tx.Commit()
    made = True
back = [x for x in FilteredElementCollector(d).OfClass(RebarBarType) if x.Name == '$barTypeName']
__output__ = {
    'status': 'self_reported_verified' if len(back) == 1 else 'failed',
    'created': made,
    'name': '$barTypeName',
    'count_with_that_name': len(back),
}
"@
$prov = Invoke-HzTool -Run $run -Tool 'horizun_execute_python' -Label 'fixture-bar-type' -Arguments @{
    code            = $provisionCode
    target_document = $Document
    idempotency_key = (New-HzKey $run 'bartype')
}
$barTypeReady = $false
if ($prov.Ok) {
    $out = Get-HzPath $prov.Result '__output__'
    if ($null -eq $out) { $out = Get-HzPath $prov.Result 'output' }
    if ($out) { $barTypeReady = ((Get-HzProp $out 'count_with_that_name') -eq 1) }
}
$run.Fixture['bar_type_name'] = $barTypeName
if (-not $barTypeReady) {
    Add-HzNote $run ('the bar type could not be provisioned: horizun_execute_python is the only route and it ' +
                     'is disabled unless the machine owner granted it.')
    foreach ($id in @('P1', 'P2', 'P3', 'P4', 'P5', 'P6', 'P7', 'P8', 'P9', 'P10')) {
        Add-HzProbe -Run $run -Id $id -Name 'reinforcement performance probe' -Status 'fixture_missing' `
            -Expected 'a bar type to measure with' -Observed 'none could be provisioned'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# ONE WALL PER SIZE-SLOT, built once and reused across the three sizes. The
# largest size needs the most, so build that many and let S and M take a prefix:
# rebuilding the fixture between sizes would put document growth INSIDE the
# measurement, and a model that has been written to twelve times is not the same
# model as a fresh one.
$maxWalls = ($SIZES | ForEach-Object { [int]$_.walls } | Measure-Object -Maximum).Maximum
$wallLen = 6000.0
$wallElements = @()
for ($i = 0; $i -lt $maxWalls; $i++) {
    $wx0 = $X + $i * (($wallLen) + 2000.0)
    $wallElements += [ordered]@{
        kind = 'wall'; level_id = [long]$level.element_id; structural = $true
        start = @($wx0, 0.0, 0.0); end = @(($wx0 + $wallLen), 0.0, 0.0); height = 3000.0
    }
}
$wallsMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-walls' -Arguments @{
    target_document  = $Document
    units            = 'mm'
    transaction_name = "HZ_RP_$TAG walls"
    elements         = $wallElements
}
$wallIds = @()
if ($wallsMade.Ok) {
    foreach ($row in @(Get-HzPath $wallsMade.Apply.Result 'rows')) {
        $wallIds += [long](Get-HzProp $row 'element_id')
    }
}
if ($wallIds.Count -ne $maxWalls) {
    Add-HzProbe -Run $run -Id 'P0' -Name 'the fixture walls were built' -Status 'fixture_missing' `
        -Expected "$maxWalls walls in the model" -Observed ($wallIds.Count.ToString() + ' walls') `
        -Evidence @{ walls = $wallsMade.Apply }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}
Add-HzNote $run ("$maxWalls walls built, ids " + ($wallIds -join ', '))

# --- READ THEM BACK. Every dimension below is measured, not assumed.
$hosts = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'hosts' -Arguments @{
    mode = 'hosts'; element_ids = $wallIds
}
$geom = @{}
foreach ($h in @(Get-HzPath $hosts.Result 'rows')) {
    $id = [long](Get-HzProp $h 'id')      # `id` here; the create reply writes `element_id`.
    $box = Get-HzProp $h 'bounding_box_mm'
    $cov = Get-HzPath $h 'cover', 'common', 'distance_mm'
    if ($box -and $null -ne $cov) { $geom[$id] = @{ Box = $box; Cover = [double]$cov } }
}
if ($geom.Count -ne $maxWalls) {
    Add-HzProbe -Run $run -Id 'P0' -Name 'every fixture wall reports a box and a cover' -Status 'fixture_missing' `
        -Expected "$maxWalls walls with a box and a cover" `
        -Observed ($geom.Count.ToString() + ' report both')
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# A zone rule fitted to one wall's own measured section, at a given spacing.
function New-HzWallRule {
    param([long]$WallId, [string]$RuleId, [double]$Spacing)
    $g = $geom[$WallId]
    $mn = Get-HzProp $g.Box 'min'; $mx = Get-HzProp $g.Box 'max'
    $inset = $g.Cover                       # the HOST's cover, exactly - ADR-003 item 7.
    $y0 = [double](Get-HzProp $mn 'y'); $y1 = [double](Get-HzProp $mx 'y')
    $yc = ($y0 + $y1) / 2.0
    $half = ($y1 - $y0) / 2.0 - $inset
    $z0 = [double](Get-HzProp $mn 'z'); $z1 = [double](Get-HzProp $mx 'z')
    $x0 = [double](Get-HzProp $mn 'x'); $x1 = [double](Get-HzProp $mx 'x')
    $tie = @(
        @(($x0 + $inset), ($yc - $half), ($z0 + $inset)),
        @(($x0 + $inset), ($yc + $half), ($z0 + $inset)),
        @(($x0 + $inset), ($yc + $half), ($z1 - $inset)),
        @(($x0 + $inset), ($yc - $half), ($z1 - $inset))
    )
    [ordered]@{
        id = $RuleId
        host = @{ element_ids = @($WallId) }
        bar_type = 'T'
        style = 'stirrup_tie'
        curve_mm = $tie
        closed = $true
        normal = @(1, 0, 0)
        allow_new_shape = $true
            layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = $Spacing
                             array_length_mm = (($x1 - $x0) - 2.0 * $inset) }
    }
}

function New-HzSizeSet {
    param([string]$Id, [hashtable[]]$Zones)
    [ordered]@{
        schema          = 'horizun.structural-requirements/1'
        requirement_set = [ordered]@{ id = $Id; version = '1.0.0'; title = "performance probe $Id" }
        units           = 'millimeter'
        tolerances      = [ordered]@{ length_mm = 2.0; spacing_mm = 2.0; cover_mm = 1.0 }
        bar_types       = @(, [ordered]@{ id = 'T'; type_name = $barTypeName; nominal_diameter_mm = 12.0 })
        hook_types      = @(, [ordered]@{ id = 'NONE'; none = $true })
        reinforcement_rules = $Zones
    }
}

# =====================================================================  MEASURE

$measured = [ordered]@{}

foreach ($size in $SIZES) {
    $name = [string]$size.name
    $n = [int]$size.walls
    Write-Host ("`n== size {0}: {1} wall(s) at {2} mm ==" -f $name, $n, $size.spacing) -ForegroundColor Cyan

    $zones = @()
    for ($i = 0; $i -lt $n; $i++) {
        $zones += (New-HzWallRule -WallId $wallIds[$i] -RuleId ("$name-w$i") -Spacing ([double]$size.spacing))
    }
    $set = New-HzSizeSet -Id "perf-$name" -Zones $zones

    # PLAN.
    $plan = Measure-HzCall -Run $run -Tool 'horizun_plan_reinforcement' -Label "plan-$name" -Arguments @{
        target_document = $Document; requirement_set = $set
    }
    $bars = 0; $refusals = @()
    if ($plan.Ok) {
        foreach ($row in @(Get-HzPath $plan.Result 'reinforcement')) {
            $code = Get-HzProp $row 'code'
            if ($code) { $refusals += [string]$code; continue }
            $q = Get-HzCount $row @('layout', 'quantity')
            if ($q -gt 0) { $bars += $q }
        }
    }

    # APPLY.
    $apply = Measure-HzCall -Run $run -Tool 'horizun_apply_reinforcement' -Label "apply-$name" -Write -Arguments @{
        target_document = $Document; requirement_set = $set
        transaction_name = "HZ_RP_$TAG $name"
    }
    # created_verified COUNTS SETS, NOT BARS. Revit models a rebar array as ONE
    # element carrying a layout, so twelve rules that plan 588 bar positions
    # create twelve elements. Comparing the two was this harness reporting a
    # difference between a count of sets and a count of bars and calling it a
    # defect. The bar positions live on the verification rows, one per set, and
    # that IS comparable with what the plan counted.
    $created = -1
    $positionsInModel = 0
    $positionsReadable = $true
    if ($apply.Ok) {
        $created = Get-HzCount $apply.Result @('created_verified')
        # $rowPositions, NOT $n. `$n` is the WALL COUNT this whole iteration is
        # built on, and reusing it here left it holding 16 - so the query below
        # asked for $wallIds[0..15] out of twelve and the run died on an index.
        # The second time in one file that a short name was already taken.
        foreach ($vrow in @(Get-HzPath $apply.Result 'verification')) {
            $rowPositions = Get-HzCount $vrow @('checks', 'number_of_bar_positions', 'read')
            if ($rowPositions -lt 0) { $positionsReadable = $false }
            else { $positionsInModel += $rowPositions }
        }
    }
    else { $positionsReadable = $false }

    # AUDIT.
    $audit = Measure-HzCall -Run $run -Tool 'horizun_audit_reinforcement' -Label "audit-$name" -Arguments @{
        target_document = $Document; requirement_set = $set
    }

    # QUERY - the read surface over the same population.
    $query = Measure-HzCall -Run $run -Tool 'horizun_query_structure' -Label "query-$name" -Arguments @{
        mode = 'rebar'; element_ids = @($wallIds[0..($n - 1)])
    }

    $measured[$name] = [ordered]@{
        walls = $n; spacing = [double]$size.spacing
        planned_bars = $bars; created_verified = $created
        rules = $n
        bar_positions_in_the_model = $(if ($positionsReadable) { $positionsInModel } else { -1 })
        refusals = @($refusals | Sort-Object -Unique)
        plan_s = $plan.Seconds; apply_s = $apply.Seconds
        audit_s = $audit.Seconds; query_s = $query.Seconds
        plan_ok = $plan.Ok; apply_ok = $apply.Ok; audit_ok = $audit.Ok; query_ok = $query.Ok
        plan_why = $plan.Why; apply_why = $apply.Why; audit_why = $audit.Why; query_why = $query.Why
    }
    Write-Host ("   {0}: {1} bars planned, {2} verified | plan {3}s apply {4}s audit {5}s query {6}s" -f
        $name, $bars, $created, $plan.Seconds, $apply.Seconds, $audit.Seconds, $query.Seconds)
}

$run.Fixture['measured'] = $measured

# =====================================================================  VERDICT

# P1 - every size actually produced work. A stage that refused everything makes
# every timing below meaningless, and a fast zero is not a result.
$sizesWithBars = @($measured.Keys | Where-Object { [int]$measured[$_].planned_bars -gt 0 })
if ($sizesWithBars.Count -ne $SIZES.Count) {
    Add-HzProbe -Run $run -Id 'P1' -Name 'every size planned bars to measure' -Status 'failed' `
        -Expected ('all ' + $SIZES.Count + ' sizes to plan at least one bar') `
        -Observed ($sizesWithBars.Count.ToString() + ' did') `
        -Because 'timings taken over an empty plan measure nothing at all.' `
        -Evidence @{ measured = $measured }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}
Add-HzProbe -Run $run -Id 'P1' -Name 'every size planned bars to measure' -Status 'passed' `
    -Expected ('all ' + $SIZES.Count + ' sizes to plan at least one bar') `
    -Observed (($SIZES | ForEach-Object { $_.name + '=' + $measured[$_.name].planned_bars }) -join ', ') `
    -Evidence @{ measured = $measured }

# P2 - the plan and the model agree about how many bars exist. Timing an apply
# that quietly built a third of what was planned is timing the wrong thing.
$mismatch = @()
foreach ($k in $measured.Keys) {
    $m = $measured[$k]
    # One verified SET per rule...
    if ([int]$m.created_verified -ne [int]$m.rules) {
        $mismatch += ("{0}: {1} rule(s), {2} set(s) verified" -f $k, $m.rules, $m.created_verified)
    }
    # ...carrying, between them, every bar position the plan counted.
    if ([int]$m.bar_positions_in_the_model -ne [int]$m.planned_bars) {
        $mismatch += ("{0}: planned {1} bar position(s), the model reports {2}" -f
                      $k, $m.planned_bars, $m.bar_positions_in_the_model)
    }
}
Add-HzProbe -Run $run -Id 'P2' -Name 'the model carries every bar the plan counted, at all three sizes' `
    -Status $(if ($mismatch.Count -eq 0) { 'passed' } else { 'failed' }) `
    -Expected ('one verified set per rule, and the bar positions on those sets adding up to what the plan ' +
               'counted, at S, M and L') `
    -Observed $(if ($mismatch.Count -eq 0) {
                    ($SIZES | ForEach-Object {
                        $mm = $measured[$_.name]
                        '{0}: {1} set(s), {2} bar position(s)' -f $_.name, $mm.created_verified, $mm.bar_positions_in_the_model
                    }) -join '; ' }
                else { $mismatch -join '; ' }) `
    -Because $(if ($mismatch.Count -eq 0) { $null }
               else { 'a timing taken over an apply that built something other than what was planned is a ' +
                      'timing of the wrong work. Refusals seen: ' +
                      (($measured.Keys | ForEach-Object { $_ + '=' + $measured[$_].apply_why }) -join ' | ') }) `
    -Evidence @{ measured = $measured }

# P3..P6 - the absolute ceiling at L, one probe per stage.
# $sizeS and $sizeL, NOT $S and $L. POWERSHELL VARIABLE NAMES ARE CASE
# INSENSITIVE, so `$S` and the stage loop's `$s` were one variable: the first
# iteration overwrote the S measurement with the string 'plan', and every
# scaling probe below then tried to index a string with a string. It failed
# loudly here. In a script that only ever read one of the two it would not have.
$sizeL = $measured['L']
$stage = 3
foreach ($stageName in @('plan', 'apply', 'audit', 'query')) {
    $took = [double]$sizeL[$stageName + '_s']
    $cap = [double]$CEILING[$stageName]
    $ranL = [bool]$sizeL[$stageName + '_ok']
    Add-HzProbe -Run $run -Id ('P' + $stage) -Name ("$stageName at L is inside the declared ceiling") `
        -Status $(if (-not $ranL) { 'unverified' } elseif ($took -le $cap) { 'passed' } else { 'failed' }) `
        -Because $(if ($ranL) { $null }
                   else { 'the stage did not succeed, so this is the duration of a failure and not a ' +
                          'measurement of the work: ' + ([string]$sizeL[$stageName + '_why']) }) `
        -Expected ("{0} at L within the {1:N0} s declared before measuring" -f $stageName, $cap) `
        -Observed ("{0:N2} s over {1} bars" -f $took, $sizeL.planned_bars) `
        -Evidence @{ stage = $stageName; seconds = $took; ceiling_s = $cap; bars = $sizeL.planned_bars }
    $stage++
}

# P7..P10 - the scaling question, one probe per stage. This is the one that
# would catch a quadratic.
$sizeS = $measured['S']
foreach ($stageName in @('plan', 'apply', 'audit', 'query')) {
    $perS = [double]$sizeS[$stageName + '_s'] / [double]$sizeS.planned_bars
    $perL = [double]$sizeL[$stageName + '_s'] / [double]$sizeL.planned_bars
    $ratio = if ($perS -gt 0) { $perL / $perS } else { [double]::PositiveInfinity }
    $bothRan = [bool]$sizeS[$stageName + '_ok'] -and [bool]$sizeL[$stageName + '_ok']
    # PER BAR, WITH THE UNIT SAID OUT LOUD. The apply's unit of work is the SET -
    # one element per rule, whatever its array holds - so its per-bar figure falls
    # for two reasons at once: fixed overhead amortising, and more bars riding on
    # each set. The ratio still answers the question this probe asks (does the
    # cost per bar GROW), and it would still catch a quadratic; it is not a claim
    # that the apply does per-bar work.
    Add-HzProbe -Run $run -Id ('P' + $stage) -Name ("$stageName cost per bar does not grow from S to L") `
        -Status $(if (-not $bothRan) { 'unverified' } elseif ($ratio -le $SCALING_FACTOR) { 'passed' } else { 'failed' }) `
        -Expected ("cost per bar at L no more than {0:N1}x the cost per bar at S" -f $SCALING_FACTOR) `
        -Observed ("{0:N4} s/bar at S over {1} bars, {2:N4} s/bar at L over {3} bars - {4:N2}x" -f
                   $perS, $sizeS.planned_bars, $perL, $sizeL.planned_bars, $ratio) `
        -Because $(if ($bothRan) {
                       'below 1.0 means the fixed per-call overhead is amortising over more bars, which is ' +
                       'the expected shape for work that is linear in them.' }
                   else {
                       'the stage did not succeed at both sizes, so this ratio compares two failures. A ' +
                       'refused call takes about the same time whatever it was asked for, which makes the ' +
                       'ratio 1.0 and lands it inside any budget - the exact way a performance harness ' +
                       'reports green over work that never happened.' }) `
        -Evidence @{ stage = $stageName; per_bar_s_at_S = $perS; per_bar_s_at_L = $perL
                     ratio = $ratio; limit = $SCALING_FACTOR }
    $stage++
}

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
