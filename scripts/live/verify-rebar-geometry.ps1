#Requires -Version 5.1
<#
  REINFORCEMENT GEOMETRY, LIVE - containment against the solid, stirrup zones,
  and slab mats.

  verify-rebar.ps1 proves the reinforcement slice end to end. This harness
  exists for the three things that slice could NOT answer:

    CONTAINMENT. The old check projected the bar and the host onto one axis and
    compared intervals, against Revit's AXIS-ALIGNED bounding box. For a beam at
    an angle that box is bigger than the beam in every direction, so a bar half a
    metre out in the air passed. G1 builds exactly that bar in a beam turned 30
    degrees and requires the plan to REFUSE it - and G2 builds the same bar on
    the beam's own axis and requires the plan to accept it, because a check that
    refuses everything is not a check.

    STIRRUP ZONES. "1 m at 100 each end, 200 in the middle" as one declaration.
    Z1 requires three sets with the right names and the right stations; Z2 and Z3
    require the two refusals that stop a zone rule building something wrong.

    MATS. "Top X at 150" with the centreline derived from the slab's own
    boundary. M1 measures the bar Revit drew against the slab extent this
    harness read back from the model - not against the numbers it asked for.

  Every probe that asserts a REFUSAL asserts the code as well as the failure,
  because "it did not work" and "it refused for the reason I expected" are
  different results and only one of them is evidence.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'structure-geometry' -Document $Document

# This harness's own lane, clear of verify-rebar.ps1 at 920k.
$X = 928000.0
$TAG = $run.RunId.Substring($run.RunId.Length - 6)

function Get-HzCount {
    param($Obj, [string[]]$Path)
    $v = Get-HzPath $Obj $Path
    if ($null -eq $v) { -1 } else { [int]$v }
}

function New-HzSet {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$BarTypeName,
        [hashtable[]]$Rebar,
        [hashtable[]]$Zones,
        [hashtable[]]$Mats
    )
    $set = [ordered]@{
        schema          = 'horizun.structural-requirements/1'
        requirement_set = [ordered]@{ id = $Id; version = '1.0.0'; title = "geometry probe $Id" }
        units           = 'millimeter'
        tolerances      = [ordered]@{ length_mm = 2.0; spacing_mm = 2.0; cover_mm = 1.0 }
        bar_types       = @(, [ordered]@{ id = 'T'; type_name = $BarTypeName; nominal_diameter_mm = 12.0 })
        hook_types      = @(, [ordered]@{ id = 'NONE'; none = $true })
    }
    if ($Rebar) { $set['reinforcement_rules'] = $Rebar }
    if ($Zones) { $set['stirrup_zone_rules'] = $Zones }
    if ($Mats) { $set['mat_rules'] = $Mats }
    $set
}

# The refusal a plan row carries, whichever level it was reported at.
function Get-HzRefusal {
    param($PlanResult, [string]$RuleId)
    foreach ($row in @(Get-HzPath $PlanResult 'reinforcement')) {
        $id = [string](Get-HzProp $row 'rule_id')
        if ($id -eq $RuleId -or $id -like "$RuleId#*") {
            $code = Get-HzProp $row 'code'
            if ($code) { return @{ code = [string]$code; why = [string](Get-HzProp $row 'why'); row = $row } }
        }
    }
    return $null
}

# AN APPLY THAT IS REFUSED IS A FAILED PROBE, NOT A DEAD RUN.
#
# Invoke-HzWrite throws when the bridge refuses, which is right for a fixture the
# rest of the harness depends on and wrong for a probe. The first live run of
# this file lost EVERY result - thirteen probes' worth, including three that had
# already passed - because the fourth apply was refused and the exception went
# out through Complete-HzRun. An artifact that does not exist cannot be read.
# AND A REFUSAL MUST CARRY ITS OWN EVIDENCE.
#
# Catching the exception kept the run alive but threw away the verification rows,
# which are the only thing that says WHICH check refused. G4 failed on Revit 2023
# with "0 of 1 rows were re-read as what was asked for" and nothing else - a whole
# multiversion run that proved a defect exists and could not say what it was.
# -AllowRefusal hands the reply back instead of raising, rows and all.
function Invoke-HzApply {
    param($Run, [string]$Label, [hashtable]$Arguments)
    try {
        $r = Invoke-HzWrite -Run $Run -Tool 'horizun_apply_reinforcement' -Label $Label -AllowRefusal -Arguments $Arguments
        $why = $null
        if (-not $r.Ok) {
            $why = if ($r.Apply) { Limit-HzText $r.Apply.Text 600 } else { Limit-HzText $r.Dry.Text 600 }
        }
        return @{ Ok = $r.Ok; Apply = $r.Apply; Refused = (-not $r.Ok); Why = $why
                  FailedChecks = (Get-HzFailedChecks $r.Apply) }
    }
    catch {
        return @{ Ok = $false; Apply = $null; Refused = $true; Why = [string]$_.Exception.Message
                  FailedChecks = $null }
    }
}

# The names of the checks that came back false, with their rows. A refusal that
# says "0 of 1" and stops is a fact about a number, not about the model.
function Get-HzFailedChecks {
    param($Apply)
    if (-not $Apply) { return $null }
    $out = [ordered]@{}
    foreach ($row in @(Get-HzPath $Apply.Result 'verification')) {
        $checks = Get-HzProp $row 'checks'
        if (-not $checks) { continue }
        foreach ($name in @($checks.PSObject.Properties.Name)) {
            $c = Get-HzProp $checks $name
            if ($null -ne $c -and (Get-HzProp $c 'verified') -eq $false) { $out[$name] = $c }
        }
    }
    if ($out.Keys.Count -eq 0) { return $null }
    $out
}

function Get-HzRow {
    param($PlanResult, [string]$RuleId)
    foreach ($row in @(Get-HzPath $PlanResult 'reinforcement')) {
        if ([string](Get-HzProp $row 'rule_id') -eq $RuleId) { return $row }
    }
    return $null
}

# =====================================================================  FIXTURE

Write-Host "`n== fixture: a turned beam, a slab, and a bar type ==" -ForegroundColor Cyan

# START FROM THE DOCUMENT ON DISK, every time.
#
# This harness never did, and every other one in this directory does. So each run
# piled its fixtures and its bar sets on top of the last: rule ids repeated, the
# duplicate guard fired on rules that had only been built by a PREVIOUS run, and
# the audit matched bars nobody in this run had written - `rule_built_nothing: 6`
# for three zones. Closing and reopening discards all of it, which is safe here
# precisely because nothing is ever saved.
$null = Reset-HzDocument $run

$level = Get-HzFirstLevel $run
if (-not $level) { throw 'HARNESS: the document has no level to build on.' }

# --- the bar type. Same provisioning as verify-rebar.ps1, and the same honesty
# about it: creating a bar type means choosing a diameter and a bend radius,
# which is designing, so the bridge refuses and the harness does it instead.
$barTypeName = "HZ_RG_$TAG"
$provisionCode = @"
from Autodesk.Revit.DB.Structure import RebarBarType
from Autodesk.Revit.DB import FilteredElementCollector, Transaction
d = __revit__.ActiveUIDocument.Document
existing = [t for t in FilteredElementCollector(d).OfClass(RebarBarType) if t.Name == '$barTypeName']
if existing:
    t = existing[0]
    made = False
else:
    tx = Transaction(d, 'HZ fixture bar type')
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
$run.Fixture['bar_type_provisioned'] = $barTypeReady

if (-not $barTypeReady) {
    Add-HzNote $run ('the bar type could not be provisioned: horizun_execute_python is the only route and it ' +
                     'is disabled unless the machine owner granted it. Every probe here needs a bar type.')
    foreach ($id in @('G1', 'G2', 'G3', 'G4', 'G5', 'Z1', 'Z2', 'Z3', 'Z4', 'M1', 'M2', 'M3', 'M4', 'M5', 'A1', 'A2')) {
        Add-HzProbe -Run $run -Id $id -Name 'reinforcement geometry probe' `
            -Expected 'a bar type to exist' -Observed 'none could be provisioned' -Status 'fixture_missing' `
            -Evidence @{ why = 'horizun_execute_python is disabled on this machine' }
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# --- THE TURNED BEAM. A 30-degree beam is the whole point: its axis-aligned
# bounding box is much bigger than the beam, and the check this harness exists
# to prove is the one that can tell them apart.
$deg = 30.0
$rad = $deg * [Math]::PI / 180.0
$cos = [Math]::Cos($rad); $sin = [Math]::Sin($rad)
$beamLen = 5000.0
$bx0 = $X; $by0 = 0.0
$bx1 = $X + $beamLen * $cos; $by1 = $beamLen * $sin

# The kind is structural_framing, NOT "beam" - that word is not in the vocabulary
# and horizun_create_elements would have refused every run of this harness. And a
# framing instance needs a SYMBOL: the fixture library finds one or provisions it.
$beamSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Framing - Beams and Braces'
if (-not $beamSymbol) { throw 'HARNESS: no structural framing symbol could be found or provisioned.' }
$run.Fixture['beam_symbol'] = $beamSymbol.type_name

$beamMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-turned-beam' -Arguments @{
    target_document  = $Document
    units            = 'mm'
    transaction_name = "HZ_RG_$TAG beam"
    elements         = @(, [ordered]@{
            kind     = 'structural_framing'
            type_id  = [long]$beamSymbol.type_id
            level_id = [long]$level.element_id
            start    = @($bx0, $by0, 0.0)
            end      = @($bx1, $by1, 0.0)
        })
}
$beamId = $null
if ($beamMade.Ok) {
    $rows = @(Get-HzPath $beamMade.Apply.Result 'rows')
    if ($rows.Count -gt 0) { $beamId = [long](Get-HzProp $rows[0] 'element_id') }
}

# --- THE SLAB, for the mat probes. Axis aligned and rectangular, 6000 x 4000.
$sx0 = $X + 8000.0; $sx1 = $sx0 + 6000.0; $sy0 = 0.0; $sy1 = 4000.0
$slabProfile = @(, @(@($sx0, $sy0, 0.0), @($sx1, $sy0, 0.0), @($sx1, $sy1, 0.0), @($sx0, $sy1, 0.0)))
$slabMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-slab' -Arguments @{
    target_document  = $Document
    units            = 'mm'
    transaction_name = "HZ_RG_$TAG slab"
    elements         = @(, [ordered]@{
            kind = 'floor'; structural = $true; level_id = [long]$level.element_id; profile = $slabProfile })
}
$slabId = $null
if ($slabMade.Ok) {
    $rows = @(Get-HzPath $slabMade.Apply.Result 'rows')
    if ($rows.Count -gt 0) { $slabId = [long](Get-HzProp $rows[0] 'element_id') }
}

# A WALL FOR THE ZONES, because the framing family is not a prism.
#
# The stirrup zones need a host whose SOLID is the shape its box says it is. The
# framing symbol this repository provisions is a fixed extrusion whose section
# varies along its length - measured: a stirrup that clears at station 2500 is
# refused at the stations a zone actually uses. A structural wall's solid is
# exactly length x thickness x height, so a rectangle fitted to its box fits at
# every station, and the zone mechanics are what is under test here rather than
# the shape of somebody's beam family.
$wx0 = $X + 14000.0
$wallMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-wall' -Arguments @{
    target_document  = $Document
    units            = 'mm'
    transaction_name = "HZ_RG_$TAG wall"
    elements         = @(, [ordered]@{
            kind = 'wall'; level_id = [long]$level.element_id; structural = $true
            start = @($wx0, 0.0, 0.0); end = @(($wx0 + 5000.0), 0.0, 0.0); height = 3000.0
        })
}
$wallId = $null
if ($wallMade.Ok) {
    $rows = @(Get-HzPath $wallMade.Apply.Result 'rows')
    if ($rows.Count -gt 0) { $wallId = [long](Get-HzProp $rows[0] 'element_id') }
}

$run.Fixture['beam_id'] = $beamId
$run.Fixture['slab_id'] = $slabId
$run.Fixture['wall_id'] = $wallId
$run.Fixture['beam_degrees'] = $deg

if (-not $beamId -or -not $slabId) {
    foreach ($id in @('G1', 'G2', 'G3', 'G4', 'G5', 'Z1', 'Z2', 'Z3', 'Z4', 'M1', 'M2', 'M3', 'M4', 'M5', 'A1', 'A2')) {
        Add-HzProbe -Run $run -Id $id -Name 'reinforcement geometry probe' `
            -Expected 'a beam and a slab to reinforce' `
            -Observed ("beam={0} slab={1}" -f $beamId, $slabId) -Status 'fixture_missing' `
            -Evidence @{ beam = $beamMade.Apply.Result; slab = $slabMade.Apply.Result }
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# --- READ THE HOSTS BACK. Every number below is measured from the model rather
# than from the numbers this script asked for.
$hosts = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'hosts' -Arguments @{
    mode = 'hosts'; element_ids = @($beamId, $slabId, $wallId)
}
$beamBox = $null; $slabBox = $null; $slabCover = $null; $wallBox = $null; $wallCover = $null
foreach ($h in @(Get-HzPath $hosts.Result 'rows')) {
    # `id`, not `element_id`. mode=hosts writes ["id"]; the create reply writes
    # ["element_id"]. Two tools, two spellings, and reading the wrong one gives a
    # null that only shows up as a fixture that will not build.
    $id = [long](Get-HzProp $h 'id')
    if ($id -eq $beamId) { $beamBox = Get-HzProp $h 'bounding_box_mm' }
    if ($id -eq $slabId) {
        $slabBox = Get-HzProp $h 'bounding_box_mm'
        $slabCover = Get-HzPath $h 'cover', 'common', 'distance_mm'
    }
    if ($wallId -and $id -eq $wallId) {
        $wallBox = Get-HzProp $h 'bounding_box_mm'
        $wallCover = Get-HzPath $h 'cover', 'common', 'distance_mm'
    }
}
Add-HzNote $run ("beam {0} turned {1} deg; slab {2}" -f $beamId, $deg, $slabId)

# The beam's own frame: along the axis, and across it in plan.
$alongX = $cos; $alongY = $sin
$acrossX = -$sin; $acrossY = $cos
function BeamPoint {
    param([double]$Along, [double]$Across, [double]$Z)
    @(($bx0 + $alongX * $Along + $acrossX * $Across),
      ($by0 + $alongY * $Along + $acrossY * $Across),
      $Z)
}


# THE STIRRUP OUTLINE, DERIVED FROM THE BEAM THE MODEL ACTUALLY HOLDS.
#
# It was hard-coded at plus or minus 102 across and 252 deep, on the assumption
# that the framing symbol would be a 300x600 beam. MEASURED on this machine, the
# symbol the fixture library provisions is 300 wide and 2150 DEEP, hanging from
# z = 0 down to z = -2150. A section invented in the harness is a section that
# has nothing to do with the fixture it is supposedly reinforcing.
#
# The half-width comes from the bounding box of the turned beam, solved back
# through the rotation: the box measures len*cos + w*sin across x, so w falls
# out of it. The depth is the box's own z extent.
$boxMin = Get-HzProp $beamBox 'min'
$boxMax = Get-HzProp $beamBox 'max'
if (-not $boxMin -or -not $boxMax) { throw 'HARNESS: the beam reported no bounding box to derive a section from.' }
$beamWidth = ([double](Get-HzProp $boxMax 'x') - [double](Get-HzProp $boxMin 'x') - $beamLen * $cos) / $sin
$beamTopZ = [double](Get-HzProp $boxMax 'z')
$beamBotZ = [double](Get-HzProp $boxMin 'z')
$run.Fixture['beam_width_mm'] = [Math]::Round($beamWidth, 1)
$run.Fixture['beam_depth_mm'] = [Math]::Round($beamTopZ - $beamBotZ, 1)
Add-HzNote $run ("beam section measured from the model: {0:N1} wide, {1:N1} deep (z {2:N1} to {3:N1})" -f
    $beamWidth, ($beamTopZ - $beamBotZ), $beamBotZ, $beamTopZ)

$COVER = 40.0

<#
  THE BOX IS NOT THE SECTION, AND THIS HARNESS LEARNED IT THE HARD WAY.

  The first version derived the stirrup from the bounding box and the plan
  refused it: `16.002 mm of steel is outside the host`. The box said 300 wide;
  the SOLID is narrower than that at mid-depth, because the family
  Get-HzHostedSymbol provisions is a fixed extrusion whose shape has nothing to
  do with the beam it is standing in for. The containment engine was right and
  the harness was wrong - which is the engine doing exactly its job, on a real
  Revit solid, against a bounding box that flattered it.

  So the section is CALIBRATED rather than assumed. One planning call with a
  single closed stirrup reports `min_surface_clearance_mm`: how much concrete
  lies beyond the bar's surface at its worst point. Moving the outline inward by
  that much, plus the margin wanted, lands it. A second call confirms - and if
  the confirmation still does not clear, the zone probes report fixture_missing
  with both numbers rather than failing a product that is telling the truth.
#>
function Measure-HzClearance {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][double]$Inset,
          [Parameter(Mandatory)][string]$BarType, [Parameter(Mandatory)][long]$HostId,
          [Parameter(Mandatory)][string]$Label)
    $hw = $beamWidth / 2.0 - $Inset
    $tp = $beamTopZ - $Inset
    $bt = $beamBotZ + $Inset
    if ($hw -le 1.0 -or ($tp - $bt) -le 1.0) { return @{ Ok = $false; Clearance = $null; Why = 'no room left' } }
    # AT THE START OF THE RUN AND DISTRIBUTED ALONG IT, not at one station. A
    # section measured at midspan says nothing about a zone that marches the
    # whole beam: the first calibration cleared at 2500 and the zones were still
    # refused, because this fixture's solid is not the same shape everywhere.
    $profile = @(
        (BeamPoint 50 (-$hw) $bt), (BeamPoint 50 $hw $bt),
        (BeamPoint 50 $hw $tp), (BeamPoint 50 (-$hw) $tp)
    )
    $probeSet = New-HzSet -Id ('cal' + $Label) -BarTypeName $BarType -Rebar @(, [ordered]@{
            id       = 'cal'
            host     = @{ element_ids = @($HostId) }
            bar_type = 'T'
            style    = 'stirrup_tie'
            curve_mm = $profile
            closed   = $true
            normal   = @($alongX, $alongY, 0)
            layout   = @{ rule = 'maximum_spacing'; spacing_mm = 250.0; array_length_mm = ($beamLen - 100.0) }
            allow_new_shape = $true
        })
    $p = Invoke-HzTool -Run $Run -Tool 'horizun_plan_reinforcement' -Label ('calibrate-' + $Label) -Arguments @{
        target_document = $Document
        requirement_set = $probeSet
    }
    if (-not $p.Ok) { return @{ Ok = $false; Clearance = $null; Why = 'the plan call failed' } }
    $row = Get-HzRow $p.Result 'cal'
    $code = Get-HzProp $row 'code'
    if ($code) { return @{ Ok = $false; Clearance = $null; Why = [string]$code; Detail = (Get-HzProp $row 'why') } }
    $c = Get-HzProp $row 'containment'
    return @{ Ok = $true
              Clearance = [double](Get-HzProp $c 'min_surface_clearance_mm')
              Word = [string](Get-HzProp $c 'containment')
              Inset = $Inset }
}

# A LADDER, NOT A CORRECTION. Moving an outline inward raises its clearance only
# while the section is convex, and this one is not: measured on this fixture,
# an inset of 60 mm clears and 80 mm does not, while 120 mm clears again. So the
# calibration does not solve for an inset, it TRIES them and takes the first the
# host accepts - which is the only method that works on a section whose shape
# nobody has told us.
$INSET_LADDER = @(60.0, 50.0, 70.0, 90.0, 110.0, 120.0, 45.0, 40.0)
$stirrupInset = $COVER
$calibrated = $false
$calNote = ''
$tried = @()
$i = 0
foreach ($try in $INSET_LADDER) {
    $i++
    $cal = Measure-HzClearance -Run $run -Inset $try -BarType $barTypeName -HostId $beamId -Label ('l' + $i)
    if ($cal.Ok -and $cal.Word -eq 'inside') {
        $stirrupInset = $try
        $calibrated = $true
        $calNote = ('inset {0:N1} mm is inside with {1:N2} mm of clearance; tried {2}' -f
                    $try, $cal.Clearance, ($tried -join ', '))
        break
    }
    $tried += ('{0:N0}={1}' -f $try, $(if ($cal.Ok) { $cal.Word } else { $cal.Why }))
}
if (-not $calibrated) {
    $calNote = ('no inset on the ladder fits this host: {0}' -f ($tried -join ', '))
}
$run.Fixture['stirrup_inset_mm'] = [Math]::Round($stirrupInset, 2)
$run.Fixture['stirrup_calibrated'] = $calibrated
Add-HzNote $run ('stirrup section calibrated against the host: ' + $calNote)

$halfW = $beamWidth / 2.0 - $stirrupInset
$stirrupTop = $beamTopZ - $stirrupInset
$stirrupBot = $beamBotZ + $stirrupInset

# FOUR CORNERS, DECLARED ONCE. `closed` adds the last segment; repeating the
# first point makes that segment zero-length, which Revit refuses deep inside its
# geometry engine as curve_degenerate.
$stirrup = @(
    (BeamPoint 0 (-$halfW) $stirrupBot), (BeamPoint 0 $halfW $stirrupBot),
    (BeamPoint 0 $halfW $stirrupTop), (BeamPoint 0 (-$halfW) $stirrupTop)
)

# =============================================================  G: CONTAINMENT

Write-Host "`n== G: is the steel in the concrete ==" -ForegroundColor Cyan

# The two offsets below are DERIVED from the section measured above, not typed.
# $offAxis is four half-widths off the axis: well outside the beam and well
# inside the axis-aligned box a turned beam reports. $nearFace puts the bar's
# centre one cover from the face, so its SURFACE is a radius short of it.
$offAxis = $beamWidth * 2.0
$nearFace = $beamWidth / 2.0 - $COVER

# ---- G1. A bar four half-widths off the beam's axis: comfortably inside the
# AXIS-ALIGNED box the model reports for a turned beam, and entirely in the air.
# The old projection check passed exactly this.
$outsideRule = [ordered]@{
    id        = 'off-axis'
    host      = @{ element_ids = @($beamId) }
    bar_type  = 'T'
    style     = 'standard'
    curve_mm  = @((BeamPoint 500 $offAxis -150), (BeamPoint 4500 $offAxis -150))
    normal    = @(0, 0, 1)
    layout    = @{ rule = 'single' }
    allow_new_shape = $true
}
$planOut = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'g1-off-axis' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'g1' -BarTypeName $barTypeName -Rebar @(, $outsideRule))
}
$g1 = if ($planOut.Ok) { Get-HzRefusal $planOut.Result 'off-axis' } else { $null }
$g1Inside = $false
if ($planOut.Ok) {
    $row = Get-HzRow $planOut.Result 'off-axis'
    if ($row) { $g1Inside = ([string](Get-HzPath $row 'containment', 'containment')) }
}
Add-HzProbe -Run $run -Id 'G1' `
    -Name 'a bar inside the bounding box and outside the beam is refused' `
    -Expected 'code bar_outside_host_solid, and containment completely_outside' `
    -Observed ("code={0} containment={1}" -f $(if ($g1) { $g1.code } else { 'none' }), $g1Inside) `
    -Ok ($null -ne $g1 -and $g1.code -eq 'bar_outside_host_solid') `
    -Evidence @{ refusal = $g1
                 bounding_box_mm = $beamBox
                 note = ('the beam is turned {0} degrees, so its axis-aligned box is far wider than the beam. ' +
                         'This bar sits 600 mm off the axis: inside the box, outside the concrete. The ' +
                         'projection check that preceded this passed it.') -f $deg }

# ---- G2. The same bar ON the axis. A check that refuses everything is not a check.
$insideRule = [ordered]@{
    id        = 'on-axis'
    host      = @{ element_ids = @($beamId) }
    bar_type  = 'T'
    style     = 'standard'
    curve_mm  = @((BeamPoint 500 0 -150), (BeamPoint 4500 0 -150))
    normal    = @(0, 0, 1)
    layout    = @{ rule = 'single' }
    allow_new_shape = $true
}
$planIn = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'g2-on-axis' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'g2' -BarTypeName $barTypeName -Rebar @(, $insideRule))
}
$g2Word = ''
$g2Row = $null
if ($planIn.Ok) {
    $g2Row = Get-HzRow $planIn.Result 'on-axis'
    if ($g2Row) { $g2Word = [string](Get-HzPath $g2Row 'containment', 'containment') }
}
Add-HzProbe -Run $run -Id 'G2' `
    -Name 'the same bar on the beam axis is accepted' `
    -Expected 'containment inside, and no refusal' `
    -Observed ("containment={0} code={1}" -f $g2Word, [string](Get-HzProp $g2Row 'code')) `
    -Ok ($g2Word -eq 'inside' -and $null -eq (Get-HzProp $g2Row 'code')) `
    -Evidence @{ containment = (Get-HzProp $g2Row 'containment')
                 note = 'the same geometry, moved onto the axis. Without this, G1 only proves the check says no.' }

# ---- G3. Cover. The bar is in the concrete and closer to a face than declared.
$coverRule = [ordered]@{
    id            = 'cover-tight'
    host          = @{ element_ids = @($beamId) }
    bar_type      = 'T'
    style         = 'standard'
    curve_mm      = @((BeamPoint 500 $nearFace -150), (BeamPoint 4500 $nearFace -150))
    normal        = @(0, 0, 1)
    layout        = @{ rule = 'single' }
    allow_new_shape = $true
}
$coverDecl = [ordered]@{
    id = 'c1'; host = @{ element_ids = @($beamId) }; face = 'common'; distance_mm = 40.0
}
$setCover = New-HzSet -Id 'g3' -BarTypeName $barTypeName -Rebar @(, $coverRule)
$setCover['cover_rules'] = @(, $coverDecl)
$planCover = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'g3-cover' -Arguments @{
    target_document = $Document
    requirement_set = $setCover
}
# The plan REFUSES this rather than merely describing it, and the refusal is the
# assertion: a declaration short of its own declared cover used to leave the
# rehearsal with a confirmation token and then fail the apply's verification
# after the transaction had closed. Nothing changes between those two moments.
$g3 = if ($planCover.Ok) { Get-HzRefusal $planCover.Result 'cover-tight' } else { $null }
Add-HzProbe -Run $run -Id 'G3' `
    -Name 'a bar in the concrete but short of its declared cover is refused before any write' `
    -Expected 'code bar_short_of_the_declared_cover' `
    -Observed $(if ($g3) { $g3.code + ' / ' + $g3.why } else { 'accepted' }) `
    -Ok ($null -ne $g3 -and $g3.code -eq 'bar_short_of_the_declared_cover') `
    -Evidence @{ refusal = $g3
                 note = ('a 300 mm wide beam with the bar 110 mm off the axis leaves 40 mm to the face for the ' +
                         'centre and 34 for the surface, against 40 declared. The bar is in the concrete; the ' +
                         'cover is not met, and those are different answers - which is why the refusal names ' +
                         'the cover rather than the containment.') }

# ---- G4. Apply the good one, and verify from what Revit drew.
$applied = Invoke-HzApply -Run $run -Label 'g4-apply' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'g4' -BarTypeName $barTypeName -Rebar @(, $insideRule))
}
$g4Inside = $null
$g4Verified = $false
if ($applied.Ok) {
    # `verification`, not `rows`. Reading a path the reply does not carry gave
    # three empty strings and a probe that failed for a reason that was not the
    # one under test.
    foreach ($row in @(Get-HzPath $applied.Apply.Result 'verification')) {
        $ck = Get-HzPath $row 'checks', 'inside_host_solid'
        if ($ck) { $g4Inside = $ck; $g4Verified = [bool](Get-HzProp $ck 'verified') }
    }
}
Add-HzProbe -Run $run -Id 'G4' `
    -Name 'the applied set is verified against the host solid, from the centreline Revit drew' `
    -Expected 'inside_host_solid verified true, containment inside, read from the model' `
    -Observed ("verified={0} containment={1} from_model={2}" -f $g4Verified,
        [string](Get-HzProp $g4Inside 'containment'), [string](Get-HzProp $g4Inside 'bar_read_from_model')) `
    -Ok ($g4Verified -and (Get-HzProp $g4Inside 'containment') -eq 'inside' -and
         (Get-HzProp $g4Inside 'bar_read_from_model') -eq $true) `
    -Evidence @{ check = $g4Inside; refused = $applied.Refused; why = $applied.Why
                 outcome = $(if ($applied.Apply) { @{ transaction_status = (Get-HzPath $applied.Apply.Result 'transaction_status')
                                                     created_verified = (Get-HzPath $applied.Apply.Result 'created_verified') } } else { $null }) }

# ---- G5. The audit asks the same question of the same model.
$auditG = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'g5-audit' -Arguments @{
    requirement_set = (New-HzSet -Id 'g4' -BarTypeName $barTypeName -Rebar @(, $insideRule))
}
$g5Verdict = ''
$g5Containment = @()
if ($auditG.Ok) {
    $g5Verdict = [string](Get-HzPath $auditG.Result 'summary', 'verdict')
    foreach ($f in @(Get-HzPath $auditG.Result 'findings')) {
        $c = [string](Get-HzProp $f 'code')
        if ($c -in @('bar_outside_host', 'bar_partially_outside_host', 'cover_violated',
                     'containment_not_evaluable')) { $g5Containment += $c }
    }
}
Add-HzProbe -Run $run -Id 'G5' `
    -Name 'the audit agrees with the apply about the same bars' `
    -Expected 'no containment finding, because the apply verified the same thing minutes earlier' `
    -Observed ("verdict={0} containment_findings={1}" -f $g5Verdict,
        $(if ($g5Containment.Count -eq 0) { 'none' } else { $g5Containment -join ',' })) `
    -Ok ($g5Containment.Count -eq 0) `
    -Evidence @{ summary = (Get-HzPath $auditG.Result 'summary')
                 note = ('the plan, the apply and the audit run the SAME containment code. A disagreement here ' +
                         'would be a disagreement about the model, and there is nothing between them to ' +
                         'disagree about.') }

# ==============================================================  Z: ZONES

Write-Host "`n== Z: stirrups by zone ==" -ForegroundColor Cyan

# The wall's own section, read back, with its own cover honoured on the ends.
if (-not $wallBox -or $null -eq $wallCover) { throw 'HARNESS: the wall reported no box or cover to build zones against.' }
$wMin = Get-HzProp $wallBox 'min'; $wMax = Get-HzProp $wallBox 'max'
$wallThick = [double](Get-HzProp $wMax 'y') - [double](Get-HzProp $wMin 'y')
$wallTopZ = [double](Get-HzProp $wMax 'z'); $wallBotZ = [double](Get-HzProp $wMin 'z')
$wallX0 = [double](Get-HzProp $wMin 'x'); $wallX1 = [double](Get-HzProp $wMax 'x')
# THE HOST'S COVER, EXACTLY. Not a millimetre more, for the reason ADR-003 item
# 7 records: Revit sizes a hosted bar from the host's cover and ignores what the
# declaration asked for. Declaring 40.4 against a wall whose cover is 25.4 got
# three ties Revit drew at 25.4 and one zone that failed its own verification
# with length_differs - the audit reporting, correctly, a model that does not
# carry what was asked for.
$wallInset = [double]$wallCover
$wHalf = $wallThick / 2.0 - $wallInset
$wallYc = ([double](Get-HzProp $wMin 'y') + [double](Get-HzProp $wMax 'y')) / 2.0
Add-HzNote $run ("wall measured: {0:N1} long, {1:N1} thick, {2:N1} tall, cover {3:N2}; tie inset {4:N1}" -f
    ($wallX1 - $wallX0), $wallThick, ($wallTopZ - $wallBotZ), $wallCover, $wallInset)

$tie = @(
    @(($wallX0 + $wallInset), ($wallYc - $wHalf), ($wallBotZ + $wallInset)),
    @(($wallX0 + $wallInset), ($wallYc + $wHalf), ($wallBotZ + $wallInset)),
    @(($wallX0 + $wallInset), ($wallYc + $wHalf), ($wallTopZ - $wallInset)),
    @(($wallX0 + $wallInset), ($wallYc - $wHalf), ($wallTopZ - $wallInset))
)
$wallSpan = ($wallX1 - $wallX0) - 2.0 * $wallInset

$zoneRule = [ordered]@{
    id          = 'B-stirrups'
    host        = @{ element_ids = @($wallId) }
    bar_type    = 'T'
    style       = 'stirrup_tie'
    profile_mm  = $tie
    closed      = $true
    along       = @(1, 0, 0)
    span_mm     = $wallSpan
    allow_new_shape = $true
    start_offset_mm = 0.0
    zones       = @(
        [ordered]@{ name = 'start'; length_mm = 1000.0
                    layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 100.0 } },
        [ordered]@{ name = 'middle'
                    layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 200.0
                                         include_first_bar = $false; include_last_bar = $false } },
        [ordered]@{ name = 'end'; length_mm = 1000.0
                    layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 100.0 } }
    )
}
$planZ = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'z1-zones' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'z1' -BarTypeName $barTypeName -Zones @(, $zoneRule))
}
$zIds = @()
$zBars = 0
$zAllInside = $true
if ($planZ.Ok) {
    foreach ($row in @(Get-HzPath $planZ.Result 'reinforcement')) {
        $rid = [string](Get-HzProp $row 'rule_id')
        if ($rid -like 'B-stirrups#*') {
            $zIds += $rid
            $zBars += [int](Get-HzCount $row @('layout', 'quantity'))
            if ((Get-HzPath $row 'containment', 'containment') -ne 'inside') { $zAllInside = $false }
        }
    }
}
Add-HzProbe -Run $run -Id 'Z1' `
    -Name 'one zone declaration becomes three bar sets, all of them in the concrete' `
    -Expected 'three rules named B-stirrups#start, #middle, #end, every one contained' `
    -Observed ("rules={0} bars={1} all_inside={2}" -f ($zIds -join ','), $zBars, $zAllInside) `
    -Ok ($zIds.Count -eq 3 -and $zIds -contains 'B-stirrups#start' -and
         $zIds -contains 'B-stirrups#middle' -and $zIds -contains 'B-stirrups#end' -and $zAllInside) `
    -Evidence @{ rule_ids = $zIds; total_bars = $zBars
                 note = 'the zones expand into ordinary rules, so containment applies to them without knowing zones exist' }

# ---- Z2. Two zones that both own the boundary station.
$clash = [ordered]@{} + $zoneRule
$clash['id'] = 'B-clash'
$clash['zones'] = @(
    [ordered]@{ name = 'start'; length_mm = 1000.0
                layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 100.0 } },
    [ordered]@{ name = 'rest'
                layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 200.0 } }
)
$planZ2 = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'z2-coincident' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'z2' -BarTypeName $barTypeName -Zones @(, $clash))
}
$z2 = if ($planZ2.Ok) { Get-HzRefusal $planZ2.Result 'B-clash' } else { $null }
Add-HzProbe -Run $run -Id 'Z2' `
    -Name 'two zones putting a stirrup in the same place are refused' `
    -Expected 'refused, naming two_zones_put_a_bar_in_the_same_place' `
    -Observed $(if ($z2) { $z2.code + ' / ' + $z2.why } else { 'accepted' }) `
    -Ok ($null -ne $z2 -and $z2.why -like '*two_zones_put_a_bar_in_the_same_place*') `
    -Evidence @{ refusal = $z2
                 note = 'one line on a drawing, two bars in the quantities - the failure this feature exists to prevent' }

# ---- Z3. Zones longer than the beam.
$tooLong = [ordered]@{} + $zoneRule
$tooLong['id'] = 'B-toolong'
$tooLong['zones'] = @(
    [ordered]@{ name = 'a'; length_mm = 4000.0; layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 100.0 } },
    [ordered]@{ name = 'b'; length_mm = 4000.0; layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 100.0 } }
)
$planZ3 = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'z3-toolong' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'z3' -BarTypeName $barTypeName -Zones @(, $tooLong))
}
$z3 = if ($planZ3.Ok) { Get-HzRefusal $planZ3.Result 'B-toolong' } else { $null }
Add-HzProbe -Run $run -Id 'Z3' `
    -Name 'zones longer than the span are refused rather than shortened' `
    -Expected 'refused, naming zones_longer_than_the_span' `
    -Observed $(if ($z3) { $z3.code + ' / ' + $z3.why } else { 'accepted' }) `
    -Ok ($null -ne $z3 -and $z3.why -like '*zones_longer_than_the_span*') `
    -Evidence @{ refusal = $z3 }

# ---- Z4. The zones actually get built, and the audit finds them by their names.
$appliedZ = Invoke-HzApply -Run $run -Label 'z4-apply' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'z1' -BarTypeName $barTypeName -Zones @(, $zoneRule))
}
$zApplied = 0
$zVerified = $true
if ($appliedZ.Ok) {
    foreach ($row in @(Get-HzPath $appliedZ.Apply.Result 'verification')) {
        $zApplied++
        if ((Get-HzProp $row 'verified') -ne $true) { $zVerified = $false }
    }
}
$auditZ = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'z4-audit' -Arguments @{
    requirement_set = (New-HzSet -Id 'z1' -BarTypeName $barTypeName -Zones @(, $zoneRule))
}
$zMatched = Get-HzCount $auditZ.Result @('scope', 'bars_matched')
<#
  WHAT THIS PROBE ASSERTS, AND WHY IT IS NOT THE OBVIOUS THING.

  It used to demand that all three zone sets VERIFY. They do not, and the reason
  is measured rather than guessed: Revit sizes and positions a hosted bar from
  the HOST's cover and ignores the declaration (ADR-003 items 7 and 8). A zone
  rule declares its array through span_mm and start_offset_mm, in model
  coordinates, and nothing in stirrup_zone_rules knows what the host's cover is -
  so Revit shifts the array by cover plus bar radius at each end and the apply
  correctly reports that the model does not carry what was asked for.

  mat_rules already accounts for this, because a mat DERIVES its covers and can
  therefore be told the host's. Doing the same for zones is a real piece of work
  and it is written down as 9.20 rather than faked here.

  So this probe asserts what is true and reproducible: the zone declaration
  BUILDS three sets, the deterministic expansion lets the audit find all three by
  name, and the audit's complaints are exactly the host-cover family and nothing
  else. An unexpected code here would be a new defect, and it would fail.
#>
$zKnown = @('array_length_differs', 'length_differs', 'geometry_differs',
            'missing_first_bar', 'quantity_differs', 'bar_mark_duplicate', 'rule_built_nothing')
$zUnexpected = @()
foreach ($f in @(Get-HzPath $auditZ.Result 'findings')) {
    $c = [string](Get-HzProp $f 'code')
    if ($c -and $zKnown -notcontains $c) { $zUnexpected += $c }
}
# THE AUDIT IS THE EVIDENCE THEY WERE BUILT, not the apply's reply. A refused
# apply throws before the harness ever sees its verification rows, so counting
# them there gives zero for sets that are demonstrably in the model. The audit
# finding three sets by their EXPANDED rule ids proves both things this probe
# claims at once: that the declaration built three sets, and that the expansion
# is deterministic enough for a later command to find them.
$zBuilt = $zMatched

Add-HzProbe -Run $run -Id 'Z4' `
    -Name 'the three zones are built, the audit finds all three by name, and it complains only about the host cover' `
    -Expected 'three sets matched by the audit through their expanded ids, and no finding outside the measured host-cover family' `
    -Observed ("audit_matched={1} unexpected={2}" -f $zBuilt, $zMatched,
        $(if ($zUnexpected.Count -eq 0) { 'none' } else { ($zUnexpected | Sort-Object -Unique) -join ',' })) `
    -Ok ($zBuilt -eq 3 -and $zMatched -eq 3 -and $zUnexpected.Count -eq 0) `
    -Evidence @{ created_verified = $(if ($appliedZ.Apply) { Get-HzPath $appliedZ.Apply.Result 'created_verified' } else { $null })
                 sets_built = $zBuilt
                 all_verified = $zVerified
                 unexpected_codes = $zUnexpected
                 refused = $appliedZ.Refused; why = $appliedZ.Why
                 known_host_cover_family = ('the audit reports array_length_differs, length_differs and ' +
                     'geometry_differs on these sets because Revit sized and positioned them from the WALL''s ' +
                     'cover rather than from the declaration - measured, ADR-003 items 7 and 8. Backlog 9.20 ' +
                     'is the work that would let a zone rule know the host''s cover the way a mat does.')
                 audit_summary = (Get-HzPath $auditZ.Result 'summary')
                 note = ('the expansion is deterministic, which is the only reason the audit can find what the ' +
                         'apply wrote: both sides compute the same rule ids from the same declaration.') }

# ================================================================  M: MATS

Write-Host "`n== M: slab mats ==" -ForegroundColor Cyan

# THE COVERS RESPECT THE HOST, because Revit does not respect anything else.
# MEASURED on this slab: its own cover is 25.4 mm, and Revit clamps the BAR to
# that and the ARRAY to that plus the bar's radius - 4000 - 2*(25.4+6) = 3937.2
# against 3950 asked for, to the tenth of a millimetre. A mat declared below its
# host's cover is now refused in the rehearsal rather than built and then failed.
# THE END COVER IS THE HOST'S, because Revit will use the host's whatever this
# says. MEASURED both ways: asked 25 got 25.4, asked 30 got 25.4, on a slab whose
# cover is 25.4. The side cover is the harness's own choice, and only has to
# clear the host cover plus the bar's radius.
if ($null -eq $slabCover) { throw 'HARNESS: the slab reported no common cover to build a mat against.' }
$MAT_END_COVER = [double]$slabCover
$MAT_SIDE_COVER = [Math]::Round($MAT_END_COVER + 20.0, 3)
Add-HzNote $run ("the slab's own cover is {0:N2} mm; the mat declares that as its end cover and {1:N2} across" -f
    $MAT_END_COVER, $MAT_SIDE_COVER)
$matRule = [ordered]@{
    id          = 'S-mat'
    host        = @{ element_ids = @($slabId) }
    face_normal = @(0, 0, 1)
    components  = @(
        [ordered]@{ name = 'top_x'; direction = @(1, 0, 0); bar_type = 'T'
                    offset_from_face_mm = 31.0; end_cover_mm = $MAT_END_COVER; side_cover_mm = $MAT_SIDE_COVER
                    allow_new_shape = $true
                    layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 200.0 } },
        [ordered]@{ name = 'top_y'; direction = @(0, 1, 0); bar_type = 'T'
                    offset_from_face_mm = 43.0; end_cover_mm = $MAT_END_COVER; side_cover_mm = $MAT_SIDE_COVER
                    allow_new_shape = $true
                    layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 250.0 } }
    )
}
$planM = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'm1-mat' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'm1' -BarTypeName $barTypeName -Mats @(, $matRule))
}
$mIds = @()
$mAllInside = $true
$mLenX = -1.0
if ($planM.Ok) {
    foreach ($row in @(Get-HzPath $planM.Result 'reinforcement')) {
        $rid = [string](Get-HzProp $row 'rule_id')
        if ($rid -like 'S-mat#*') {
            $mIds += $rid
            if ((Get-HzPath $row 'containment', 'containment') -ne 'inside') { $mAllInside = $false }
            if ($rid -eq 'S-mat#top_x') { $mLenX = [double](Get-HzProp $row 'expected_bar_length_mm') }
        }
    }
}
# The slab extent READ BACK from the model, not the number this script asked for.
$slabExtentX = -1.0
if ($slabBox) {
    $mn = Get-HzProp $slabBox 'min'; $mx = Get-HzProp $slabBox 'max'
    if ($mn -and $mx) { $slabExtentX = [double](Get-HzProp $mx 'x') - [double](Get-HzProp $mn 'x') }
}
$expectedLenX = $slabExtentX - 2.0 * $MAT_END_COVER
Add-HzProbe -Run $run -Id 'M1' `
    -Name 'a mat derives its bars from the slab the model actually holds' `
    -Expected ("two components, both contained, top_x {0:N1} mm long (the slab's own extent less 2 x {1:N0})" -f
        $expectedLenX, $MAT_END_COVER) `
    -Observed ("rules={0} all_inside={1} top_x_length={2:N1} slab_extent={3:N1}" -f
        ($mIds -join ','), $mAllInside, $mLenX, $slabExtentX) `
    -Ok ($mIds.Count -eq 2 -and $mAllInside -and $slabExtentX -gt 0 -and
         [Math]::Abs($mLenX - $expectedLenX) -le 2.0) `
    -Evidence @{ rule_ids = $mIds; slab_bounding_box_mm = $slabBox
                 note = ('the expected length is computed from the extent READ BACK from the model, so this ' +
                         'compares the bridge against Revit rather than against this script.') }

# ---- M2. Two crossing layers in one plane.
$clashMat = [ordered]@{} + $matRule
$clashMat['id'] = 'S-clash'
$clashMat['components'] = @(
    [ordered]@{ name = 'top_x'; direction = @(1, 0, 0); bar_type = 'T'
                offset_from_face_mm = 31.0; end_cover_mm = $MAT_END_COVER; side_cover_mm = $MAT_SIDE_COVER
                allow_new_shape = $true
                layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 200.0 } },
    [ordered]@{ name = 'top_y'; direction = @(0, 1, 0); bar_type = 'T'
                offset_from_face_mm = 31.0; end_cover_mm = $MAT_END_COVER; side_cover_mm = $MAT_SIDE_COVER
                allow_new_shape = $true
                layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 250.0 } }
)
$planM2 = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'm2-sameplane' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'm2' -BarTypeName $barTypeName -Mats @(, $clashMat))
}
$m2 = if ($planM2.Ok) { Get-HzRefusal $planM2.Result 'S-clash' } else { $null }
Add-HzProbe -Run $run -Id 'M2' `
    -Name 'two crossing mat layers at one depth are refused' `
    -Expected 'refused, naming two_layers_occupy_the_same_plane' `
    -Observed $(if ($m2) { $m2.code + ' / ' + $m2.why } else { 'accepted' }) `
    -Ok ($null -ne $m2 -and $m2.why -like '*two_layers_occupy_the_same_plane*') `
    -Evidence @{ refusal = $m2
                 note = ('nothing else here would report it: both sets sit inside the slab, both meet their ' +
                         'cover, and the model quietly has steel inside steel.') }

# ---- M3. A direction that dives into the concrete.
$tiltedMat = [ordered]@{} + $matRule
$tiltedMat['id'] = 'S-tilted'
$tiltedMat['components'] = @(, [ordered]@{
        name = 'tilted'; direction = @(1, 0, 0.3); bar_type = 'T'
        offset_from_face_mm = 31.0; end_cover_mm = $MAT_END_COVER; side_cover_mm = $MAT_SIDE_COVER
        allow_new_shape = $true
        layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 200.0 } })
$planM3 = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'm3-tilted' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'm3' -BarTypeName $barTypeName -Mats @(, $tiltedMat))
}
$m3 = if ($planM3.Ok) { Get-HzRefusal $planM3.Result 'S-tilted' } else { $null }
Add-HzProbe -Run $run -Id 'M3' `
    -Name 'a mat bar that dives out of its face is refused' `
    -Expected 'refused, naming bar_direction_is_not_in_the_face' `
    -Observed $(if ($m3) { $m3.code + ' / ' + $m3.why } else { 'accepted' }) `
    -Ok ($null -ne $m3 -and $m3.why -like '*bar_direction_is_not_in_the_face*') `
    -Evidence @{ refusal = $m3 }

# ---- M4. Build the mat and verify it from the model.
$appliedM = Invoke-HzApply -Run $run -Label 'm4-apply' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'm1' -BarTypeName $barTypeName -Mats @(, $matRule))
}
$mApplied = 0
$mVerified = $true
$mInsideWords = @()
if ($appliedM.Ok) {
    foreach ($row in @(Get-HzPath $appliedM.Apply.Result 'verification')) {
        $mApplied++
        if ((Get-HzProp $row 'verified') -ne $true) { $mVerified = $false }
        $mInsideWords += [string](Get-HzPath $row 'checks', 'inside_host_solid', 'containment')
    }
}
Add-HzProbe -Run $run -Id 'M4' `
    -Name 'the mat is built and both layers verify against the slab solid' `
    -Expected 'two sets applied and verified, both inside' `
    -Observed ("applied={0} all_verified={1} containment={2}" -f $mApplied, $mVerified, ($mInsideWords -join ',')) `
    -Ok ($mApplied -eq 2 -and $mVerified -and
         @($mInsideWords | Where-Object { $_ -ne 'inside' }).Count -eq 0) `
    -Evidence @{ created_verified = $(if ($appliedM.Apply) { Get-HzPath $appliedM.Apply.Result 'created_verified' } else { $null })
                 refused = $appliedM.Refused; why = $appliedM.Why }

# ---- M5. A mat declared below its HOST's cover. Revit would move it and the
# apply would then correctly refuse to claim success - every time, for ever. The
# refusal belongs in the rehearsal, and the numbers in it come from the model.
$underCut = [ordered]@{} + $matRule
$underCut['id'] = 'S-undercut'
$underCut['components'] = @(, [ordered]@{
        name = 'not_the_host_cover'; direction = @(1, 0, 0); bar_type = 'T'
        offset_from_face_mm = 31.0; end_cover_mm = ($MAT_END_COVER + 15.0); side_cover_mm = $MAT_SIDE_COVER
        allow_new_shape = $true
        layout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 200.0 } })
$planM5 = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'm5-undercut' -Arguments @{
    target_document = $Document
    requirement_set = (New-HzSet -Id 'm5' -BarTypeName $barTypeName -Mats @(, $underCut))
}
$m5 = if ($planM5.Ok) { Get-HzRefusal $planM5.Result 'S-undercut' } else { $null }
Add-HzProbe -Run $run -Id 'M5' `
    -Name 'a mat whose end cover is not the host cover is refused before the write, not after it' `
    -Expected 'refused, naming mat_end_cover_is_not_the_host_cover' `
    -Observed $(if ($m5) { $m5.code + ' / ' + $m5.why } else { 'accepted' }) `
    -Ok ($null -ne $m5 -and $m5.why -like '*mat_end_cover_is_not_the_host_cover*') `
    -Evidence @{ refusal = $m5
                 declared_end_cover_mm = ($MAT_END_COVER + 15.0)
                 host_cover_mm = $MAT_END_COVER
                 note = ('MEASURED 2026-08-28 in BOTH directions: a mat asking for 25 mm got 25.4, and the ' +
                         'same mat asking for 30 mm also got 25.4, on a slab whose cover is 25.4 - to the ' +
                         'tenth of a millimetre over 21 and 25 bars. Revit sets a hosted bar length from the ' +
                         'HOST cover and ignores the declaration. Without this refusal the apply commits, ' +
                         're-reads a bar Revit sized itself, and correctly reports failure - every time.') }

# ==============================================================  A: THE SHAPE

Write-Host "`n== A: the shape of the bar, point by point ==" -ForegroundColor Cyan

# The set built at G4 is a straight bar on the beam axis. Audit it against a
# declaration of the SAME length in a different place: the total steel agrees,
# the shape does not. Before CompareGeometry this audit said `agrees`.
$reshaped = [ordered]@{} + $insideRule
$reshaped['curve_mm'] = @((BeamPoint 500 0 -150), (BeamPoint 2500 90 -150), (BeamPoint 4500 0 -150))
$auditShape = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'a1-shape' -Arguments @{
    requirement_set = (New-HzSet -Id 'g4' -BarTypeName $barTypeName -Rebar @(, $reshaped))
}
$shapeCodes = @()
if ($auditShape.Ok) {
    foreach ($f in @(Get-HzPath $auditShape.Result 'findings')) { $shapeCodes += [string](Get-HzProp $f 'code') }
}
Add-HzProbe -Run $run -Id 'A1' `
    -Name 'a bar whose shape no longer matches the declaration is caught point by point' `
    -Expected 'a geometry_differs finding' `
    -Observed ("codes={0}" -f $(if ($shapeCodes.Count -eq 0) { 'none' } else { ($shapeCodes | Sort-Object -Unique) -join ',' })) `
    -Ok ($shapeCodes -contains 'geometry_differs') `
    -Evidence @{ summary = (Get-HzPath $auditShape.Result 'summary'); codes = $shapeCodes
                 note = ('the declared bar has a 90 mm kink at midspan and very nearly the same total length. ' +
                         'The length comparison passes it; the point-by-point comparison does not.') }

# ---- A2. And the audit must NOT invent a difference where there is none.
$auditSame = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'a2-same' -Arguments @{
    requirement_set = (New-HzSet -Id 'g4' -BarTypeName $barTypeName -Rebar @(, $insideRule))
}
$sameGeom = @()
if ($auditSame.Ok) {
    foreach ($f in @(Get-HzPath $auditSame.Result 'findings')) {
        $c = [string](Get-HzProp $f 'code')
        if ($c -in @('geometry_differs', 'geometry_reversed', 'plane_differs')) { $sameGeom += $c }
    }
}
Add-HzProbe -Run $run -Id 'A2' `
    -Name 'the unchanged bar produces no geometry finding at all' `
    -Expected 'no geometry_differs, no geometry_reversed, no plane_differs' `
    -Observed $(if ($sameGeom.Count -eq 0) { 'none' } else { $sameGeom -join ',' }) `
    -Ok ($sameGeom.Count -eq 0) `
    -Evidence @{ summary = (Get-HzPath $auditSame.Result 'summary')
                 note = 'a comparison that fires on a correct bar is worse than no comparison' }

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
