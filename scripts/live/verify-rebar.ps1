#Requires -Version 5.1
<#
  REINFORCEMENT, LIVE.

  What makes this different from every other harness here: a rebar set is the
  one thing where "Revit accepted it" is nearly worthless as evidence. Revit
  will take a layout, place the bars, and report a healthy element - right host,
  right type, right shape - with half the steel standing outside the beam.

  So the probes below are built around measurements Revit does not volunteer:

    the SUBSTANCE - the bar position transforms Revit itself computed, read back
    after the commit and projected onto the host, rather than the positions the
    plan asked for;

    the two COUNTS - array positions and bars standing, which differ whenever an
    end bar is suppressed, checked separately because a takeoff built on the
    wrong one is short by two bars per set;

    the REFUSALS - a set longer than its host, a bar type name that matches two
    types, a non-planar centreline, a shape Revit would have to invent. Each one
    is a thing Revit would happily do.

  MEASURED FIRST, 2026-08-28: this document carries zero rebar bar types, zero
  hook types and zero shapes, and rebar hosting is enabled. So the fixture
  PROVISIONS a bar type before anything can be planned. That provisioning is not
  a product capability and is not pretending to be one - creating a bar type
  means choosing a diameter and a bend radius, which is designing, and the
  bridge refuses to do it. The harness may, because a harness is allowed to
  build the conditions a real structural model already has.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'structure-rebar' -Document $Document

# This harness's own lane, 920 metres east of the model, clear of every other
# harness's fixtures (900k architecture/structure, 916k wall openings).
$X = 920000.0
$TAG = $run.RunId.Substring($run.RunId.Length - 6)

function Get-HzCount {
    param($Obj, [string[]]$Path)
    $v = Get-HzPath $Obj $Path
    if ($null -eq $v) { -1 } else { [int]$v }
}

<#
  The requirement set this harness works from, built around a host whose extent
  was MEASURED rather than assumed. Everything a rule needs is declared here,
  which is the whole point of the artefact.
#>
function New-HzRebarSet {
    param(
        [Parameter(Mandatory)][string]$Id,
        # NOT mandatory: a cover-only set declares no reinforcement rule, and a
        # Mandatory parameter refuses $null rather than accepting the absence.
        [hashtable]$Rule,
        [string]$BarTypeName,
        [hashtable]$CoverRule
    )
    $set = [ordered]@{
        schema           = 'horizun.structural-requirements/1'
        requirement_set  = [ordered]@{ id = $Id; version = '1.0.0'; title = "live probe $Id" }
        units            = 'millimeter'
        tolerances       = [ordered]@{ length_mm = 2.0; spacing_mm = 2.0; cover_mm = 1.0 }
        bar_types        = @(, [ordered]@{ id = 'T'; type_name = $BarTypeName; nominal_diameter_mm = 12.0 })
        hook_types       = @(, [ordered]@{ id = 'NONE'; none = $true })
    }
    if ($CoverRule) { $set['cover_rules'] = @(, $CoverRule) }
    if ($Rule) { $set['reinforcement_rules'] = @(, $Rule) }
    $set
}

# =====================================================================  FIXTURE

Write-Host "`n== fixture: a structural slab and a bar type ==" -ForegroundColor Cyan

$level = Get-HzFirstLevel $run
if (-not $level) { throw 'HARNESS: the document has no level to build on.' }

# A rectangular structural slab, 6000 x 4000, in this harness's own lane. Its
# real extent is READ BACK below rather than computed from these numbers.
$x0 = $X; $x1 = $X + 6000.0; $y0 = 0.0; $y1 = 4000.0
$slabProfile = @(, @(@($x0, $y0, 0.0), @($x1, $y0, 0.0), @($x1, $y1, 0.0), @($x0, $y1, 0.0)))

$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-slab' -Arguments @{
    target_document = $Document
    units           = 'mm'
    transaction_name = "HZ_RB_$TAG slab"
    elements        = @(, [ordered]@{
        kind = 'floor'; structural = $true; level_id = [long]$level.element_id; profile = $slabProfile })
}
$slabId = $null
if ($made.Ok) {
    $rows = @(Get-HzPath $made.Apply.Result 'rows')
    if ($rows.Count -gt 0) { $slabId = [long](Get-HzProp $rows[0] 'element_id') }
}
if (-not $slabId) { throw 'HARNESS: the fixture slab was not created, so nothing below could be measured.' }
$run.Fixture['slab_id'] = $slabId
Add-HzNote $run ("fixture slab {0} at x={1}" -f $slabId, $X)

# ---- the bar type. Measured 2026-08-28: this document has none, and the bridge
# refuses to create one because choosing a diameter is designing. The harness
# provisions it through the documented Python fallback, and if that is not
# permitted on this machine every bar probe below becomes fixture_missing rather
# than a product failure.
$barTypeName = "HZ_RB_$TAG"
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
    'diameter_mm': round(back[0].BarNominalDiameter * 304.8, 3) if back else None,
}
"@
$prov = Invoke-HzTool -Run $run -Tool 'horizun_execute_python' -Label 'fixture-bar-type' -Arguments @{
    code = $provisionCode
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
                     'is disabled unless the machine owner granted it. Bar probes report fixture_missing.')
}

# ===================================================================  Q: QUERY

Write-Host "`n== Q: the read surface ==" -ForegroundColor Cyan

$cov = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-coverage' -Arguments @{ mode = 'coverage' }

# THE GENERATION THIS YEAR ACTUALLY HAS, not the one 2026 has.
#
# This asserted 'bar_terminations_data' outright, which is true only from Revit
# 2026: 2023, 2024 and 2025 have no terminations API and correctly report
# hook_type_and_orientation. Hard-coding the newer answer would have failed three
# of the five years of the multiversion matrix on a fact about Revit rather than
# a defect - and, worse, it would have PASSED a 2025 add-in that wrongly claimed
# the newer API. Deriving the expectation from the host year checks more, not
# less: the reply now has to name the generation that matches the Revit it is
# running in.
$hostYear = [int](Get-HzProp (Get-HzHealth $run) 'revit_version')
$expectedApi = $(if ($hostYear -ge 2026) { 'bar_terminations_data' } else { 'hook_type_and_orientation' })
Add-HzProbe -Run $run -Id 'Q1' `
    -Name 'the bridge says which generation of the rebar API it was compiled against' `
    -Expected ("$expectedApi on Revit $hostYear, and the five layout words") `
    -Observed ("api={0} layouts={1}" -f (Get-HzPath $cov.Result 'rebar_api_generation'),
                                        ((Get-HzPath $cov.Result 'layout_vocabulary') -join ',')) `
    -Ok ($cov.Ok -and (Get-HzPath $cov.Result 'rebar_api_generation') -eq $expectedApi -and
         ((Get-HzPath $cov.Result 'layout_vocabulary') -join ',') -eq
         'single,fixed_number,number_with_spacing,maximum_spacing,minimum_clear_spacing') `
    -Evidence @{ api_generation = (Get-HzPath $cov.Result 'rebar_api_generation')
                 reinforcement_enabled = (Get-HzPath $cov.Result 'reinforcement_enabled')
                 counts = (Get-HzPath $cov.Result 'counts')
                 note = 'measured: no overload of Rebar.CreateFromCurves exists in all five years, so this is the half of the matrix this binary was built for' }

$hosts = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-hosts' -Arguments @{
    mode = 'hosts'; element_ids = @($slabId) }
$hostRow = $null
if ($hosts.Ok) { $rows = Get-HzPath $hosts.Result 'rows'; if ($rows) { $hostRow = $rows[0] } }
$hostBox = if ($hostRow) { Get-HzProp $hostRow 'bounding_box_mm' } else { $null }
Add-HzProbe -Run $run -Id 'Q2' `
    -Name 'the slab is a valid reinforcement host and publishes the box a plan is measured against' `
    -Expected 'is_valid_host true, and a bounding box in millimetres' `
    -Observed ("valid={0} box={1}" -f (Get-HzProp $hostRow 'is_valid_host'), ($null -ne $hostBox)) `
    -Ok ((Get-HzProp $hostRow 'is_valid_host') -eq $true -and $null -ne $hostBox) `
    -Evidence @{ host_id = $slabId; bounding_box_mm = $hostBox
                 cover = (Get-HzProp $hostRow 'cover') }

Add-HzProbe -Run $run -Id 'Q3' `
    -Name 'a host with one cover on every face reports a COMMON cover; per-face cover is listed separately' `
    -Expected 'cover.common present or explicitly null, and cover.faces enumerated' `
    -Observed ("common={0} faces={1}" -f
        ($null -ne (Get-HzPath $hostRow 'cover', 'common')),
        (@(Get-HzPath $hostRow 'cover', 'faces')).Count) `
    -Ok ($null -ne (Get-HzPath $hostRow 'cover')) `
    -Evidence @{ cover = (Get-HzProp $hostRow 'cover')
                 note = 'null common cover is a FACT about a host whose faces differ, not a failure to read it' }

$covers = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-covers' -Arguments @{ mode = 'covers' }
$coverRows = @(Get-HzPath $covers.Result 'rows')
$coverName = $null
if ($coverRows.Count -gt 0) { $coverName = [string](Get-HzProp $coverRows[0] 'name') }
Add-HzProbe -Run $run -Id 'Q4' `
    -Name 'cover TYPES are listed by measured distance, which is a different question from which face carries which' `
    -Expected 'at least one cover type, each with a distance in millimetres' `
    -Observed ("{0} cover type(s); first '{1}'" -f $coverRows.Count, $coverName) `
    -Ok ($covers.Ok -and $coverRows.Count -ge 1 -and $null -ne (Get-HzProp $coverRows[0] 'distance_mm')) `
    -Evidence @{ cover_types = $coverRows }

# =============================================================  P: PLAN + APPLY

Write-Host "`n== P: plan, apply, and the position check ==" -ForegroundColor Cyan

# The bar is derived from the host's MEASURED box, not from the numbers used to
# build it: a fixture that agreed with itself would prove nothing about the box
# the plan is judged against.
$planProbeIds = @('P1','P2','P3','P4','P5','P6','P7','X1','X2','X3','X4','A1','A2','A3')
if (-not $barTypeReady -or -not $hostBox) {
    foreach ($id in $planProbeIds) {
        Add-HzProbe -Run $run -Id $id -Name 'reinforcement probe' `
            -Expected 'a provisioned bar type and a measured host box' `
            -Observed ("bar_type_ready={0} host_box={1}" -f $barTypeReady, ($null -ne $hostBox)) `
            -Status 'fixture_missing' `
            -Because 'this document carries no rebar bar type and the harness could not provision one; the bridge refuses to create bar types because choosing a diameter is designing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

$bx0 = [double](Get-HzPath $hostBox 'min', 'x'); $bx1 = [double](Get-HzPath $hostBox 'max', 'x')
$by0 = [double](Get-HzPath $hostBox 'min', 'y'); $by1 = [double](Get-HzPath $hostBox 'max', 'y')
$bz0 = [double](Get-HzPath $hostBox 'min', 'z'); $bz1 = [double](Get-HzPath $hostBox 'max', 'z')
$zMid = ($bz0 + $bz1) / 2.0
Add-HzNote $run ("host box measured x {0}..{1}  y {2}..{3}  z {4}..{5}" -f $bx0, $bx1, $by0, $by1, $bz0, $bz1)

# A straight bar spanning the slab in Y, distributed along X (the normal).
$inset = 60.0
$barStart = @(($bx0 + $inset), ($by0 + $inset), $zMid)
$barEnd   = @(($bx0 + $inset), ($by1 - $inset), $zMid)
$arrayLen = ($bx1 - $bx0) - (2.0 * $inset)

function New-HzRule {
    param([string]$Id, [hashtable]$Layout, [array]$Curve, [array]$Normal, [string]$Style = 'standard',
          [bool]$Closed = $false, [string]$Mark)
    $r = [ordered]@{
        id       = $Id
        host     = [ordered]@{ element_ids = @($slabId) }
        bar_type = 'T'
        style    = $Style
        curve_mm = $Curve
        normal   = $Normal
        layout   = $Layout
        allow_new_shape = $true
    }
    if ($Closed) { $r['closed'] = $true }
    if ($Mark) { $r['mark'] = $Mark }
    $r
}

$fitsLayout = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 300.0; array_length_mm = $arrayLen }
$setFits = New-HzRebarSet -Id 'fits' -BarTypeName $barTypeName `
    -Rule (New-HzRule -Id 'slab-bottom-y' -Layout $fitsLayout -Curve @($barStart, $barEnd) -Normal @(1, 0, 0) -Mark "M$TAG")

$plan = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-fits' -Arguments @{
    requirement_set = $setFits }
$planRow = $null
if ($plan.Ok) { $rr = @(Get-HzPath $plan.Result 'reinforcement'); if ($rr.Count -gt 0) { $planRow = $rr[0] } }
$expectedPositions = if ($planRow) { Get-HzCount $planRow @('layout', 'number_of_bar_positions') } else { -1 }
$expectedQuantity = if ($planRow) { Get-HzCount $planRow @('layout', 'quantity') } else { -1 }

Add-HzProbe -Run $run -Id 'P1' `
    -Name 'the plan computes every bar position and says the set fits, without opening a transaction' `
    -Expected 'will_build true, fits true, and one position per array slot' `
    -Observed ("will_build={0} fits={1} positions={2} listed={3}" -f
        (Get-HzProp $planRow 'will_build'), (Get-HzPath $planRow 'fit', 'fits'),
        $expectedPositions, (@(Get-HzPath $planRow 'layout', 'positions_mm')).Count) `
    -Ok ((Get-HzProp $planRow 'will_build') -eq $true -and
         (Get-HzPath $planRow 'fit', 'fits') -eq $true -and
         $expectedPositions -gt 1 -and
         (@(Get-HzPath $planRow 'layout', 'positions_mm')).Count -eq $expectedPositions) `
    -Evidence @{ layout = (Get-HzProp $planRow 'layout'); fit = (Get-HzProp $planRow 'fit')
                 writes_nothing = (Get-HzPath $plan.Result 'writes_nothing') }

Add-HzProbe -Run $run -Id 'P2' `
    -Name 'maximum_spacing never produces a gap wider than the maximum that was declared' `
    -Expected 'resulting spacing <= 300 mm' `
    -Observed ("{0} mm across {1} positions" -f (Get-HzPath $planRow 'layout', 'resulting_spacing_mm'), $expectedPositions) `
    -Ok ([double](Get-HzPath $planRow 'layout', 'resulting_spacing_mm') -le 300.0 + 1e-6) `
    -Evidence @{ declared_maximum_mm = 300.0
                 resulting_spacing_mm = (Get-HzPath $planRow 'layout', 'resulting_spacing_mm')
                 array_length_mm = (Get-HzPath $planRow 'layout', 'array_length_mm')
                 note = 'the count rounds UP for a maximum; rounding to nearest would put bars further apart than the instruction allows' }

$applied = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-fits' -AllowRefusal -Arguments @{
    target_document = $Document
    requirement_set = $setFits
}

# A REFUSED APPLY IS A FAILED PROBE, NOT A DEAD RUN.
#
# Invoke-HzWrite throws when the bridge refuses, which is right for a fixture the
# rest of the file depends on and wrong for a probe. On the first multiversion
# run this file passed eleven probes on Revit 2023 and then died on the twelfth,
# writing NO artifact - so the year reported one defect when what was wanted was
# every defect it has. -AllowRefusal turns the refusal into a row this file can
# read, and the probes below assert against it.
$verifyRows = @(if ($applied.Apply) { Get-HzPath $applied.Apply.Result 'verification' })
$v0 = if ($verifyRows.Count -gt 0) { $verifyRows[0] } else { $null }
$barId = if ($v0) { Get-HzProp $v0 'element_id' } else { $null }

Add-HzProbe -Run $run -Id 'P3' `
    -Name 'the set is built and every check re-read from the model agrees' `
    -Expected 'created_verified 1, and verified true on the row' `
    -Observed ("created_verified={0} verified={1}" -f
        $(if ($applied.Apply) { Get-HzPath $applied.Apply.Result 'created_verified' } else { 'REFUSED' }),
        (Get-HzProp $v0 'verified')) `
    -Ok ($applied.Ok -and $null -ne $applied.Apply -and
         (Get-HzPath $applied.Apply.Result 'created_verified') -eq 1 -and
         (Get-HzProp $v0 'verified') -eq $true) `
    -Evidence @{ checks = (Get-HzProp $v0 'checks'); rebar_id = $barId }

# THE CHECK SPLIT IN TWO AND BOTH HALVES ARE ASSERTED. What used to be one
# check is now `positions_within_host_extent` - the projection
# onto the distribution axis, which answers whether the set is too long for its
# host - and `inside_host_solid`, which is measured against the host's own
# triangulated boundary and is the one that answers whether the steel is in the
# concrete. This probe demands BOTH, which is strictly more than it demanded
# before: the extent check was the whole of it, and a bar can satisfy the extent
# check while standing beside the beam.
Add-HzProbe -Run $run -Id 'P4' `
    -Name 'the bar POSITIONS Revit computed were read back and measured against the host' `
    -Expected 'the extent check verified with every position measured, AND inside_host_solid inside' `
    -Observed ("extent_verified={0} measured={1} outside={2} solid={3} solid_verified={4}" -f
        (Get-HzPath $v0 'checks', 'positions_within_host_extent', 'verified'),
        (Get-HzPath $v0 'checks', 'positions_within_host_extent', 'measured_positions'),
        (@(Get-HzPath $v0 'checks', 'positions_within_host_extent', 'outside_positions')).Count,
        (Get-HzPath $v0 'checks', 'inside_host_solid', 'containment'),
        (Get-HzPath $v0 'checks', 'inside_host_solid', 'verified')) `
    -Ok ((Get-HzPath $v0 'checks', 'positions_within_host_extent', 'verified') -eq $true -and
         [int](Get-HzPath $v0 'checks', 'positions_within_host_extent', 'measured_positions') -eq $expectedPositions -and
         (Get-HzPath $v0 'checks', 'inside_host_solid', 'containment') -eq 'inside' -and
         (Get-HzPath $v0 'checks', 'inside_host_solid', 'verified') -eq $true) `
    -Evidence @{ positions_within_host_extent = (Get-HzPath $v0 'checks', 'positions_within_host_extent')
                 inside_host_solid = (Get-HzPath $v0 'checks', 'inside_host_solid')
                 note = 'this is the check nothing else performs: host, type and shape can all agree while the steel stands outside the concrete' }

Add-HzProbe -Run $run -Id 'P5' `
    -Name "Revit's own bar count matches the count the layout predicted before the transaction" `
    -Expected ("quantity {0} and {1} array positions, both read from the element" -f $expectedQuantity, $expectedPositions) `
    -Observed ("quantity read={0} positions read={1}" -f
        (Get-HzPath $v0 'checks', 'quantity', 'read'),
        (Get-HzPath $v0 'checks', 'number_of_bar_positions', 'read')) `
    -Ok ((Get-HzPath $v0 'checks', 'quantity', 'verified') -eq $true -and
         (Get-HzPath $v0 'checks', 'number_of_bar_positions', 'verified') -eq $true) `
    -Evidence @{ quantity = (Get-HzPath $v0 'checks', 'quantity')
                 positions = (Get-HzPath $v0 'checks', 'number_of_bar_positions')
                 note = 'predicted and measured come from different places on purpose' }

$bars = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-rebar' -Arguments @{
    mode = 'rebar'; host_id = $slabId }
$barRows = @(Get-HzPath $bars.Result 'rows')
$b0 = if ($barRows.Count -gt 0) { $barRows[0] } else { $null }
Add-HzProbe -Run $run -Id 'P6' `
    -Name 'the query reads the set back with its measured steel length and both diameters named' `
    -Expected 'total length and volume read off the element, nominal and model diameter both present' `
    -Observed ("length={0} mm volume={1} m3 nominal={2} model={3}" -f
        (Get-HzPath $b0 'measured', 'total_length_mm'), (Get-HzPath $b0 'measured', 'volume_m3'),
        (Get-HzPath $b0 'bar_type', 'nominal_diameter_mm'), (Get-HzPath $b0 'bar_type', 'model_diameter_mm')) `
    -Ok ($bars.Ok -and $barRows.Count -eq 1 -and
         $null -ne (Get-HzPath $b0 'measured', 'total_length_mm') -and
         [double](Get-HzPath $b0 'measured', 'total_length_mm') -gt 0 -and
         $null -ne (Get-HzPath $b0 'bar_type', 'nominal_diameter_mm')) `
    -Evidence @{ measured = (Get-HzProp $b0 'measured'); bar_type = (Get-HzProp $b0 'bar_type')
                 source = (Get-HzPath $b0 'measured', 'source') }

# The substance: one bar of this set is as long as the slab minus two insets,
# and the SET's steel is that times the bar count. Measured, not asserted.
$oneBar = ($by1 - $inset) - ($by0 + $inset)
$expectedSteel = $oneBar * $expectedQuantity
$readSteel = [double](Get-HzPath $b0 'measured', 'total_length_mm')
Add-HzProbe -Run $run -Id 'P7' `
    -Name 'the steel in the model is the declared bar length times the bar count, to a millimetre' `
    -Expected ("{0} mm ({1} mm x {2} bars)" -f $expectedSteel, $oneBar, $expectedQuantity) `
    -Observed ("{0} mm" -f $readSteel) `
    -Ok ([math]::Abs($readSteel - $expectedSteel) -le 1.0) `
    -Evidence @{ one_bar_mm = $oneBar; bars = $expectedQuantity
                 expected_total_mm = $expectedSteel; revit_reports_mm = $readSteel
                 note = 'no hook was declared, so Revit adds nothing to the centreline and the comparison is exact' }

# ===================================================================  X: REFUSALS

Write-Host "`n== X: what it refuses to do ==" -ForegroundColor Cyan

# A set longer than its host. Revit would build this without a word.
$tooLong = [ordered]@{ rule = 'fixed_number'; number = 6; array_length_mm = ($arrayLen + 4000.0) }
$setLong = New-HzRebarSet -Id 'toolong' -BarTypeName $barTypeName `
    -Rule (New-HzRule -Id 'runs-past-the-end' -Layout $tooLong -Curve @($barStart, $barEnd) -Normal @(1, 0, 0))
$planLong = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-too-long' -Arguments @{
    requirement_set = $setLong }
$longRow = $null
if ($planLong.Ok) { $rr = @(Get-HzPath $planLong.Result 'reinforcement'); if ($rr.Count -gt 0) { $longRow = $rr[0] } }
Add-HzProbe -Run $run -Id 'X1' `
    -Name 'a set longer than its host is refused, and the refusal counts the positions that miss' `
    -Expected 'will_build false with code set_outside_host' `
    -Observed ("will_build={0} code={1}" -f (Get-HzProp $longRow 'will_build'), (Get-HzProp $longRow 'code')) `
    -Ok ((Get-HzProp $longRow 'will_build') -eq $false -and (Get-HzProp $longRow 'code') -eq 'set_outside_host') `
    -Evidence @{ code = (Get-HzProp $longRow 'code'); why = (Get-HzProp $longRow 'why')
                 note = 'Revit creates this set without complaint and reports it healthy' }

$applyLong = Invoke-HzTool -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-too-long' -Arguments @{
    target_document = $Document; requirement_set = $setLong; dry_run = $true }
Add-HzProbe -Run $run -Id 'X2' `
    -Name 'a rehearsal that cannot resolve a required row issues NO confirmation token' `
    -Expected 'no confirmation_token, and a note saying there is nothing to confirm' `
    -Observed ("token={0}" -f ($null -ne (Get-HzPath $applyLong.Result 'confirmation_token'))) `
    -Ok ($null -eq (Get-HzPath $applyLong.Result 'confirmation_token')) `
    -Evidence @{ refused = (Get-HzPath $applyLong.Result 'refused')
                 note = (Get-HzPath $applyLong.Result 'confirmation_note') }

# A non-planar centreline. Revit refuses this deep inside its geometry engine
# with a message about nothing in particular.
# EVERY ELEMENT PARENTHESISED. PowerShell's comma binds TIGHTER than arithmetic,
# so `@($a + 1, $b / 2)` parses as `$a + (1, $b) / 2` and dies on an array
# division - which is what the first run of this harness did, at exactly this
# line, after thirteen probes had already passed.
$yMid = ($by0 + $by1) / 2.0
$bent = @($barStart,
          @(($barStart[0] + 40.0), $yMid, ($zMid + 30.0)),
          $barEnd,
          @($barStart[0], $yMid, ($zMid - 30.0)))
$setBent = New-HzRebarSet -Id 'bent' -BarTypeName $barTypeName `
    -Rule (New-HzRule -Id 'not-planar' -Layout $fitsLayout -Curve $bent -Normal @(1, 0, 0))
$planBent = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-not-planar' -Arguments @{
    requirement_set = $setBent }
$bentRow = $null
if ($planBent.Ok) { $rr = @(Get-HzPath $planBent.Result 'reinforcement'); if ($rr.Count -gt 0) { $bentRow = $rr[0] } }
Add-HzProbe -Run $run -Id 'X3' `
    -Name 'a centreline that is not planar is refused before Revit is asked, and says how far off it is' `
    -Expected 'curve_not_planar, with the deviation in millimetres' `
    -Observed ("code={0}" -f (Get-HzProp $bentRow 'code')) `
    -Ok ((Get-HzProp $bentRow 'code') -eq 'curve_not_planar') `
    -Evidence @{ why = (Get-HzProp $bentRow 'why')
                 note = 'no point is named as the culprit because the geometry does not support naming one' }

# A bar type name that does not exist. The bridge refuses to invent one.
$setNoType = New-HzRebarSet -Id 'notype' -BarTypeName "HZ_NO_SUCH_TYPE_$TAG" `
    -Rule (New-HzRule -Id 'missing-type' -Layout $fitsLayout -Curve @($barStart, $barEnd) -Normal @(1, 0, 0))
$planNoType = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-no-bar-type' -Arguments @{
    requirement_set = $setNoType }
$noTypeRow = $null
if ($planNoType.Ok) { $rr = @(Get-HzPath $planNoType.Result 'reinforcement'); if ($rr.Count -gt 0) { $noTypeRow = $rr[0] } }
Add-HzProbe -Run $run -Id 'X4' `
    -Name 'a bar type the model does not have is refused, and the bridge does not create one' `
    -Expected 'bar_type_not_found, saying a bar type carries a diameter and a grade' `
    -Observed ("code={0}" -f (Get-HzProp $noTypeRow 'code')) `
    -Ok ((Get-HzProp $noTypeRow 'code') -eq 'bar_type_not_found' -and
         (Get-HzProp $noTypeRow 'why') -match 'designing') `
    -Evidence @{ why = (Get-HzProp $noTypeRow 'why') }

# ===================================================================  A: AUDIT

Write-Host "`n== A: the audit ==" -ForegroundColor Cyan

$auditOk = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-agrees' -Arguments @{
    requirement_set = $setFits }
# ZERO ERRORS AND ZERO UNKNOWNS is the substance; the verdict WORD follows from
# it. Asserting the word alone would break the moment an `info` finding appears -
# and `agrees_with_notes` exists precisely so info findings stop hiding under
# `agrees`.
Add-HzProbe -Run $run -Id 'A1' `
    -Name 'the model that was just built AGREES with the set it was built from' `
    -Expected 'zero errors and zero unknown, and a verdict that says so' `
    -Observed ("verdict={0} errors={1} unknown={2} info={3}" -f
        (Get-HzPath $auditOk.Result 'summary', 'verdict'),
        (Get-HzPath $auditOk.Result 'summary', 'errors'),
        (Get-HzPath $auditOk.Result 'summary', 'unknown'),
        (Get-HzPath $auditOk.Result 'summary', 'info')) `
    -Ok ($auditOk.Ok -and
         [int](Get-HzPath $auditOk.Result 'summary', 'errors') -eq 0 -and
         [int](Get-HzPath $auditOk.Result 'summary', 'unknown') -eq 0 -and
         @('agrees', 'agrees_with_notes') -contains
             [string](Get-HzPath $auditOk.Result 'summary', 'verdict')) `
    -Evidence @{ summary = (Get-HzPath $auditOk.Result 'summary')
                 bars_matched = (Get-HzPath $auditOk.Result 'scope', 'bars_matched')
                 findings = (Get-HzPath $auditOk.Result 'findings') }

# The same model against a set asking for MORE bars. Nothing is written; the
# audit has to notice.
$denser = [ordered]@{ rule = 'maximum_spacing'; spacing_mm = 150.0; array_length_mm = $arrayLen }
$setDense = New-HzRebarSet -Id 'fits' -BarTypeName $barTypeName `
    -Rule (New-HzRule -Id 'slab-bottom-y' -Layout $denser -Curve @($barStart, $barEnd) -Normal @(1, 0, 0) -Mark "M$TAG")
$auditDense = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-denser' -Arguments @{
    requirement_set = $setDense }
$codes = @()
foreach ($f in @(Get-HzPath $auditDense.Result 'findings')) { $codes += [string](Get-HzProp $f 'code') }
Add-HzProbe -Run $run -Id 'A2' `
    -Name 'a set asking for closer spacing than the model carries is caught by the bar COUNT' `
    -Expected 'verdict differences_found, with quantity_differs among the codes' `
    -Observed ("verdict={0} codes={1}" -f
        (Get-HzPath $auditDense.Result 'summary', 'verdict'), ($codes -join ',')) `
    -Ok ((Get-HzPath $auditDense.Result 'summary', 'verdict') -eq 'differences_found' -and
         $codes -contains 'quantity_differs') `
    -Evidence @{ summary = (Get-HzPath $auditDense.Result 'summary'); codes = $codes }

Add-HzProbe -Run $run -Id 'A3' `
    -Name 'the audit publishes what it does NOT check, rather than leaving the gap to be discovered' `
    -Expected 'not_checked names laps, couplers and overlapping bars' `
    -Observed ("{0}" -f ((Get-HzPath $auditOk.Result 'summary', 'not_checked') -join ',')) `
    -Ok (((Get-HzPath $auditOk.Result 'summary', 'not_checked') -join ',') -match 'lap_insufficient' -and
         ((Get-HzPath $auditOk.Result 'summary', 'not_checked') -join ',') -match 'missing_coupler') `
    -Evidence @{ not_checked = (Get-HzPath $auditOk.Result 'summary', 'not_checked')
                 verdict_means = (Get-HzPath $auditOk.Result 'summary', 'verdict_means') }

# ==================================================================  L: LAYOUTS

Write-Host "`n== L: the other four layouts ==" -ForegroundColor Cyan

<#
  Each layout gets its own bar, at its own X, so a failure names one layout
  rather than a set. The expectations come from the arithmetic in
  RebarLayoutRules and the observations come from Revit - which is the only
  arrangement in which agreement means anything.
#>
function Test-HzLayout {
    param(
        [Parameter(Mandatory)][string]$ProbeId,
        [Parameter(Mandatory)][string]$RuleId,
        [Parameter(Mandatory)][hashtable]$Layout,
        [Parameter(Mandatory)][double]$AtX,
        [Parameter(Mandatory)][int]$ExpectPositions,
        [Parameter(Mandatory)][int]$ExpectQuantity,
        [Parameter(Mandatory)][string]$Name
    )
    $start = @($AtX, ($by0 + $inset), $zMid)
    $end   = @($AtX, ($by1 - $inset), $zMid)
    $rule = New-HzRule -Id $RuleId -Layout $Layout -Curve @($start, $end) -Normal @(1, 0, 0)
    $set = New-HzRebarSet -Id $RuleId -BarTypeName $barTypeName -Rule $rule

    $w = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label ("apply-" + $RuleId) -AllowRefusal -Arguments @{
        target_document = $Document; requirement_set = $set }
    $rows = @(if ($w.Apply) { Get-HzPath $w.Apply.Result 'verification' })
    $row = if ($rows.Count -gt 0) { $rows[0] } else { $null }
    $gotPos = Get-HzCount $row @('checks', 'number_of_bar_positions', 'read')
    $gotQty = Get-HzCount $row @('checks', 'quantity', 'read')

    Add-HzProbe -Run $run -Id $ProbeId -Name $Name `
        -Expected ("{0} array positions and {1} bars, verified against the model" -f $ExpectPositions, $ExpectQuantity) `
        -Observed ("positions={0} bars={1} verified={2}" -f $gotPos, $gotQty, (Get-HzProp $row 'verified')) `
        -Ok ($w.Ok -and (Get-HzProp $row 'verified') -eq $true -and
             $gotPos -eq $ExpectPositions -and $gotQty -eq $ExpectQuantity) `
        -Evidence @{ layout = $Layout; checks = (Get-HzProp $row 'checks') }
}

Test-HzLayout -ProbeId 'L1' -RuleId 'lay-single' -Name 'layout single builds exactly one bar at one position' `
    -Layout ([ordered]@{ rule = 'single' }) -AtX ($bx0 + 300.0) -ExpectPositions 1 -ExpectQuantity 1

# THE ONE THAT SEPARATES THE TWO COUNTS. Four array positions, the first bar
# excluded, so three bars stand. A check that compared only one of the two
# numbers would pass this whichever way it was wrong.
Test-HzLayout -ProbeId 'L2' -RuleId 'lay-fixed' `
    -Name 'fixed_number with the first bar excluded: four POSITIONS and three BARS' `
    -Layout ([ordered]@{ rule = 'fixed_number'; number = 4; array_length_mm = 900.0
                         include_first_bar = $false }) `
    -AtX ($bx0 + 900.0) -ExpectPositions 4 -ExpectQuantity 3

Test-HzLayout -ProbeId 'L3' -RuleId 'lay-numsp' `
    -Name 'number_with_spacing derives the array length instead of being told it' `
    -Layout ([ordered]@{ rule = 'number_with_spacing'; number = 5; spacing_mm = 200.0 }) `
    -AtX ($bx0 + 2200.0) -ExpectPositions 5 -ExpectQuantity 5

# A 12 mm bar with 100 mm CLEAR is 112 mm centre to centre. 900 / 112 = 8.03
# gaps, and a MINIMUM rounds DOWN: 8 gaps, 9 positions. Reading the declared
# number as centre-to-centre would give 10 positions and every bar closer
# together than the instruction allows.
Test-HzLayout -ProbeId 'L4' -RuleId 'lay-clear' `
    -Name 'minimum_clear_spacing measures between bar SURFACES, so the diameter is part of the count' `
    -Layout ([ordered]@{ rule = 'minimum_clear_spacing'; spacing_mm = 100.0; array_length_mm = 900.0 }) `
    -AtX ($bx0 + 3400.0) -ExpectPositions 9 -ExpectQuantity 9

# ====================================================================  C: COVER

Write-Host "`n== C: cover ==" -ForegroundColor Cyan

if ($coverName) {
    $coverRule = [ordered]@{
        id = 'slab-cover'; host = [ordered]@{ element_ids = @($slabId) }
        face = 'common'; cover_type_name = $coverName
    }
    $setCover = New-HzRebarSet -Id 'cover' -BarTypeName $barTypeName -Rule $null -CoverRule $coverRule
    $applyCover = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-cover' -AllowRefusal -Arguments @{
        target_document = $Document; requirement_set = $setCover }
    $hostsAfter = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-hosts-after-cover' -Arguments @{
        mode = 'hosts'; element_ids = @($slabId) }
    $rowsAfter = @(Get-HzPath $hostsAfter.Result 'rows')
    $commonAfter = $null
    if ($rowsAfter.Count -gt 0) { $commonAfter = Get-HzPath $rowsAfter[0] 'cover', 'common', 'name' }
    Add-HzProbe -Run $run -Id 'C1' `
        -Name 'the host COMMON cover is the type the rule names, re-read from the model afterwards' `
        -Expected ("common cover '{0}'" -f $coverName) `
        -Observed ("common cover '{0}'" -f $commonAfter) `
        -Ok ([string]$commonAfter -eq [string]$coverName) `
        -Evidence @{ cover_set = $(if ($applyCover.Apply) { Get-HzPath $applyCover.Apply.Result 'cover_set' })
                     cover_verified = $(if ($applyCover.Apply) { Get-HzPath $applyCover.Apply.Result 'cover_verified' })
                     refused_why = $(if (-not $applyCover.Apply) { Limit-HzText $applyCover.Dry.Text 400 })
                     read_back = $commonAfter
                     note = 'SetCommonCoverType does not throw when it does not take, so the re-read is the evidence' }
} else {
    Add-HzProbe -Run $run -Id 'C1' -Name 'cover' -Expected 'a cover type in the document' `
        -Observed 'none' -Status 'fixture_missing' `
        -Because 'this document defines no rebar cover type, and the bridge refuses to create one because a cover distance is a design decision'
}

$setNoCover = New-HzRebarSet -Id 'nocover' -BarTypeName $barTypeName -Rule $null -CoverRule ([ordered]@{
    id = 'impossible'; host = [ordered]@{ element_ids = @($slabId) }
    face = 'common'; cover_type_name = ("HZ_NO_COVER_" + $TAG) })
$planNoCover = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-no-cover' -Arguments @{
    requirement_set = $setNoCover }
$ncRow = $null
if ($planNoCover.Ok) { $rr = @(Get-HzPath $planNoCover.Result 'cover'); if ($rr.Count -gt 0) { $ncRow = $rr[0] } }
Add-HzProbe -Run $run -Id 'C2' `
    -Name 'a cover type the model does not define is refused, and the bridge does not create one' `
    -Expected 'cover_type_not_found' `
    -Observed ("code={0}" -f (Get-HzProp $ncRow 'code')) `
    -Ok ((Get-HzProp $ncRow 'code') -eq 'cover_type_not_found') `
    -Evidence @{ why = (Get-HzProp $ncRow 'why') }

# =================================================================  R: RED TEAM

Write-Host "`n== R: replay, and a token that belongs to another plan ==" -ForegroundColor Cyan

# THE SAME KEY TWICE. A replayed mutation must return the first answer and build
# nothing, or a retried network call doubles somebody's reinforcement.
$replaySet = New-HzRebarSet -Id 'replay' -BarTypeName $barTypeName -Rule (New-HzRule `
    -Id 'replay-bar' -Layout ([ordered]@{ rule = 'single' }) `
    -Curve @(@(($bx0 + 5200.0), ($by0 + $inset), $zMid), @(($bx0 + 5200.0), ($by1 - $inset), $zMid)) `
    -Normal @(1, 0, 0))
$replayKey = (New-HzKey $run 'replay')
$dryR = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_reinforcement' -Label 'replay-dry' -Arguments @{
    target_document = $Document; requirement_set = $replaySet; dry_run = $true }
$tokenR = [string](Get-HzPath $dryR.Result 'confirmation_token')
$firstR = Invoke-HzTool -Run $run -Tool 'horizun_apply_reinforcement' -Label 'replay-1' -Arguments @{
    target_document = $Document; requirement_set = $replaySet; dry_run = $false
    confirmation_token = $tokenR; idempotency_key = $replayKey }
$qBefore = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-before-replay' -Arguments @{
    mode = 'rebar'; host_id = $slabId; include_bar_positions = $false }
$barsBefore = Get-HzCount $qBefore.Result @('matched')
$secondR = Invoke-HzTool -Run $run -Tool 'horizun_apply_reinforcement' -Label 'replay-2' -Arguments @{
    target_document = $Document; requirement_set = $replaySet; dry_run = $false
    confirmation_token = $tokenR; idempotency_key = $replayKey }
$qAfter = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-after-replay' -Arguments @{
    mode = 'rebar'; host_id = $slabId; include_bar_positions = $false }
$barsAfter = Get-HzCount $qAfter.Result @('matched')

Add-HzProbe -Run $run -Id 'R1' `
    -Name 'the same idempotency key twice replays the first answer and builds nothing the second time' `
    -Expected 'status replayed, command_executed_in_this_call false, and the set count unchanged' `
    -Observed ("status={0} executed={1} sets {2} -> {3}" -f
        (Get-HzPath $secondR.Result 'idempotency', 'status'),
        (Get-HzPath $secondR.Result 'idempotency', 'command_executed_in_this_call'),
        $barsBefore, $barsAfter) `
    -Ok ($firstR.Ok -and
         (Get-HzPath $secondR.Result 'idempotency', 'status') -eq 'replayed' -and
         (Get-HzPath $secondR.Result 'idempotency', 'command_executed_in_this_call') -eq $false -and
         $barsBefore -eq $barsAfter -and $barsBefore -gt 0) `
    -Evidence @{ idempotency = (Get-HzPath $secondR.Result 'idempotency')
                 # WHAT THE TWO CALLS ACTUALLY SAID. On Revit 2023 this probe
                 # reported "status= executed=" - an empty idempotency block and
                 # no way to tell a replay that answered wrongly from a FIRST
                 # call that never succeeded, which is a different defect.
                 first_ok = $firstR.Ok
                 first_said = (Limit-HzText $firstR.Text 500)
                 second_ok = $secondR.Ok
                 second_said = (Limit-HzText $secondR.Text 500)
                 sets_before = $barsBefore; sets_after = $barsAfter
                 note = 'the count is the evidence: a replay that answered correctly and built anyway would look identical in the reply' }

# A TOKEN THAT BELONGS TO ANOTHER PLAN - real, unexpired and unused, for a
# different requirement set.
$otherSet = New-HzRebarSet -Id 'other' -BarTypeName $barTypeName -Rule (New-HzRule `
    -Id 'other-bar' -Layout ([ordered]@{ rule = 'single' }) `
    -Curve @(@(($bx0 + 5500.0), ($by0 + $inset), $zMid), @(($bx0 + 5500.0), ($by1 - $inset), $zMid)) `
    -Normal @(1, 0, 0))
$dryOther = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_reinforcement' -Label 'token-dry-other' -Arguments @{
    target_document = $Document; requirement_set = $otherSet; dry_run = $true }
$otherToken = [string](Get-HzPath $dryOther.Result 'confirmation_token')

$thirdSet = New-HzRebarSet -Id 'third' -BarTypeName $barTypeName -Rule (New-HzRule `
    -Id 'third-bar' -Layout ([ordered]@{ rule = 'single' }) `
    -Curve @(@(($bx0 + 5800.0), ($by0 + $inset), $zMid), @(($bx0 + 5800.0), ($by1 - $inset), $zMid)) `
    -Normal @(1, 0, 0))
$qCross0 = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-before-crossed' -Arguments @{
    mode = 'rebar'; host_id = $slabId; include_bar_positions = $false }
$crossBefore = Get-HzCount $qCross0.Result @('matched')
$crossed = Invoke-HzTool -Run $run -Tool 'horizun_apply_reinforcement' -Label 'token-crossed' -Arguments @{
    target_document = $Document; requirement_set = $thirdSet; dry_run = $false
    confirmation_token = $otherToken; idempotency_key = (New-HzKey $run 'crossed') }
$qCross1 = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-after-crossed' -Arguments @{
    mode = 'rebar'; host_id = $slabId; include_bar_positions = $false }
$crossAfter = Get-HzCount $qCross1.Result @('matched')
$crossedText = ''
if ($crossed.IsError) { $crossedText = [string]$crossed.Text }
else { $crossedText = ($crossed.Result | ConvertTo-Json -Depth 8 -Compress) }

# THE COUNT IS THE PROBE. The first version of this matched the refusal text
# against three words the bridge does not use - it says "refuses", not
# "refused" - and never once measured the thing the probe is named after.
# A refusal that was worded differently and built the bar anyway would have
# passed it.
Add-HzProbe -Run $run -Id 'R2' `
    -Name 'a valid token minted for a DIFFERENT requirement set is refused, and nothing is built' `
    -Expected 'the call errors, says nothing was changed, and the set count is unchanged' `
    -Observed ("error={0} sets {1} -> {2}" -f $crossed.IsError, $crossBefore, $crossAfter) `
    -Ok ($crossed.IsError -and $crossAfter -eq $crossBefore -and
         $crossedText -match 'NOT THE ONE THAT WAS REHEARSED' -and
         $crossedText -match 'Nothing was changed') `
    -Evidence @{ reply = (Limit-HzText $crossedText 700)
                 sets_before = $crossBefore; sets_after = $crossAfter
                 note = 'the token was real, unexpired and unused - only the plan it was minted for differs' }

$auditFinal = Invoke-HzTool -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-final' -Arguments @{
    requirement_set = $setFits }
# MATCHED EXACTLY ONE, and every remaining finding is about MARKS rather than
# about the rule. The first version of this probe demanded verdict `agrees`, and
# by then the audit was right to disagree: six sets stand in this slab and all
# six carry the same schedule mark, so a schedule counts them as one line. That
# is a true finding about the model, not a defect in the matching - and see R4,
# which is the measurement behind it.
$otherCodes = @()
foreach ($f in @(Get-HzPath $auditFinal.Result 'findings')) {
    if ((Get-HzProp $f 'severity') -eq 'error' -and (Get-HzProp $f 'code') -ne 'bar_mark_duplicate') {
        $otherCodes += [string](Get-HzProp $f 'code')
    }
}
Add-HzProbe -Run $run -Id 'R3' `
    -Name 'with several more sets in the same host, the original rule still matches exactly one of them' `
    -Expected 'exactly one bar set matched, and no error about the rule itself' `
    -Observed ("matched={0} sets_in_host={1} other_errors={2}" -f
        (Get-HzPath $auditFinal.Result 'scope', 'bars_matched'), $barsAfter,
        $(if ($otherCodes.Count -eq 0) { 'none' } else { $otherCodes -join ',' })) `
    -Ok ((Get-HzPath $auditFinal.Result 'scope', 'bars_matched') -eq 1 -and $otherCodes.Count -eq 0) `
    -Evidence @{ summary = (Get-HzPath $auditFinal.Result 'summary')
                 scope = (Get-HzPath $auditFinal.Result 'scope')
                 other_error_codes = $otherCodes
                 note = 'bars are matched to rules by PROVENANCE; with several sets in one host, matching by position would pick the wrong one' }

# THE MARK COLLISION IS THE POINT, not an inconvenience. MEASURED on Revit 2026,
# in a rolled-back transaction: three fresh bars all come back as mark "1"; setting
# the first to AAA leaves the others alone; and a bar created afterwards INHERITS
# AAA. So a set that declares a mark hands it to everything built next that does
# not declare its own, and a schedule groups by mark.
$markCodes = @()
foreach ($f in @(Get-HzPath $auditFinal.Result 'findings')) {
    if ((Get-HzProp $f 'code') -eq 'bar_mark_duplicate') { $markCodes += $f }
}
Add-HzProbe -Run $run -Id 'R4' `
    -Name 'the audit finds the mark collision that a schedule would silently count as one line' `
    -Expected 'bar_mark_duplicate, naming more than one bar' `
    -Observed ("{0} duplicate-mark finding(s), first names {1} bars" -f $markCodes.Count,
        $(if ($markCodes.Count -gt 0) { @(Get-HzProp $markCodes[0] 'rebar_ids').Count } else { 0 })) `
    -Ok ($markCodes.Count -ge 1 -and (@(Get-HzProp $markCodes[0] 'rebar_ids').Count -ge 2)) `
    -Evidence @{ finding = $markCodes[0]
                 marks_checked_over = (Get-HzPath $auditFinal.Result 'summary', 'marks_checked_over')
                 note = 'the check scans every bar in the audited hosts, not only the ones a rule matched - a collision between a set this bridge built and one somebody modelled by hand is exactly the one worth finding' }

# =====================================================  D: THE SECOND RUN

Write-Host "`n== D: running the same set twice ==" -ForegroundColor Cyan

# THE SAME REQUIREMENT SET, A SECOND TIME, DELIBERATELY. Not a retry - a fresh
# rehearsal and a fresh key, which is what somebody does when they are not sure
# the first run took. Nothing used to stop it: the idempotency ledger protects a
# RETRY of one call, and a second rehearsal produces an identical plan that reads
# as a first-time operation. The result was a second coincident cage in the same
# beam, and the audit does not look for coincident bars.
$again = Invoke-HzTool -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-again' -Arguments @{
    requirement_set = $setFits }
$againRow = $null
if ($again.Ok) { $rr = @(Get-HzPath $again.Result 'reinforcement'); if ($rr.Count -gt 0) { $againRow = $rr[0] } }
Add-HzProbe -Run $run -Id 'D1' `
    -Name 'running the same rule into the same host a second time is refused, by name' `
    -Expected 'this_rule_already_built_a_set_in_this_host' `
    -Observed ("code={0}" -f (Get-HzProp $againRow 'code')) `
    -Ok ((Get-HzProp $againRow 'code') -eq 'this_rule_already_built_a_set_in_this_host') `
    -Evidence @{ why = (Get-HzProp $againRow 'why')
                 note = 'a fresh rehearsal and a fresh idempotency key - the ledger cannot see this, because it is not a retry' }

$sets0 = Get-HzCount (Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-before-second' `
    -Arguments @{ mode = 'rebar'; host_id = $slabId; include_bar_positions = $false }).Result @('matched')
$applyAgain = Invoke-HzTool -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-again-dry' -Arguments @{
    target_document = $Document; requirement_set = $setFits; dry_run = $true }
$sets1 = Get-HzCount (Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'q-after-second' `
    -Arguments @{ mode = 'rebar'; host_id = $slabId; include_bar_positions = $false }).Result @('matched')
Add-HzProbe -Run $run -Id 'D2' `
    -Name 'the second rehearsal issues no token and the set count does not move' `
    -Expected 'no confirmation_token, and the same number of sets in the host' `
    -Observed ("token={0} sets {1} -> {2}" -f
        ($null -ne (Get-HzPath $applyAgain.Result 'confirmation_token')), $sets0, $sets1) `
    -Ok ($null -eq (Get-HzPath $applyAgain.Result 'confirmation_token') -and $sets0 -eq $sets1) `
    -Evidence @{ refused = (Get-HzPath $applyAgain.Result 'refused')
                 sets_before = $sets0; sets_after = $sets1 }

# =====================================================  V: WHAT THE APPLY READ

Write-Host "`n== V: what the apply read back ==" -ForegroundColor Cyan

$checks0 = Get-HzProp $v0 'checks'
$checkNames = @()
if ($checks0) { $checkNames = @($checks0.PSObject.Properties.Name) }
Add-HzProbe -Run $run -Id 'V1' `
    -Name 'the apply reads back the terminations it wrote, not only the identity of the bar' `
    -Expected 'hook type and termination orientation checked at BOTH ends' `
    -Observed ("checks: {0}" -f ($checkNames -join ',')) `
    -Ok ($checkNames -contains 'hook_type_start' -and $checkNames -contains 'hook_type_end' -and
         $checkNames -contains 'termination_orientation_start' -and
         $checkNames -contains 'termination_orientation_end') `
    -Evidence @{ hook_start = (Get-HzPath $v0 'checks', 'hook_type_start')
                 orientation_start = (Get-HzPath $v0 'checks', 'termination_orientation_start')
                 note = 'the length assertion is switched OFF when a hook is declared, so without these a hook that did not take was the one case nothing looked at' }

Add-HzProbe -Run $run -Id 'V2' `
    -Name 'every bar position Revit computed is compared against the one the plan predicted' `
    -Expected 'positions_match_the_plan verified, predicted count equal to measured count' `
    -Observed ("verified={0} predicted={1} measured={2} worst={3} mm" -f
        (Get-HzPath $v0 'checks', 'positions_match_the_plan', 'verified'),
        (Get-HzPath $v0 'checks', 'positions_match_the_plan', 'predicted'),
        (Get-HzPath $v0 'checks', 'positions_match_the_plan', 'measured'),
        (Get-HzPath $v0 'checks', 'positions_match_the_plan', 'worst_difference_mm')) `
    -Ok ((Get-HzPath $v0 'checks', 'positions_match_the_plan', 'verified') -eq $true) `
    -Evidence @{ positions_match_the_plan = (Get-HzPath $v0 'checks', 'positions_match_the_plan')
                 note = 'containment alone passes a set with the right count at the wrong pitch' }

Add-HzProbe -Run $run -Id 'V3' `
    -Name 'the containment test starts from the centreline Revit DREW, not the one that was asked for' `
    -Expected 'bar_read_from_model true on BOTH the extent check and the solid check' `
    -Observed ("extent={0} solid={1}" -f
        (Get-HzPath $v0 'checks', 'positions_within_host_extent', 'bar_read_from_model'),
        (Get-HzPath $v0 'checks', 'inside_host_solid', 'bar_read_from_model')) `
    -Ok ((Get-HzPath $v0 'checks', 'positions_within_host_extent', 'bar_read_from_model') -eq $true -and
         (Get-HzPath $v0 'checks', 'inside_host_solid', 'bar_read_from_model') -eq $true) `
    -Evidence @{ positions_within_host_extent = (Get-HzPath $v0 'checks', 'positions_within_host_extent')
                 inside_host_solid = (Get-HzPath $v0 'checks', 'inside_host_solid')
                 note = 'the position transforms are OFFSETS from bar 0, so adding them to the DECLARED bar made the whole check translation-invariant' }

$pitchRow = Get-HzPath $b0 'layout'
Add-HzProbe -Run $run -Id 'V4' `
    -Name 'the pitch is MEASURED between bar positions, because MaxSpacing is the number the layout was given' `
    -Expected 'measured_pitch_mm present and equal to the resulting spacing, not to the declared maximum 300' `
    -Observed ("measured_pitch={0} max_spacing={1}" -f
        (Get-HzProp $pitchRow 'measured_pitch_mm'), (Get-HzProp $pitchRow 'max_spacing_mm')) `
    -Ok ($null -ne (Get-HzProp $pitchRow 'measured_pitch_mm') -and
         [math]::Abs([double](Get-HzProp $pitchRow 'measured_pitch_mm') -
                     [double](Get-HzPath $planRow 'layout', 'resulting_spacing_mm')) -le 1.0) `
    -Evidence @{ layout = $pitchRow
                 plan_resulting_spacing_mm = (Get-HzPath $planRow 'layout', 'resulting_spacing_mm')
                 note = 'measured on Revit 2026: maximum_spacing(300 over 1000) reports MaxSpacing 300 and lays the bars at 250' }

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
