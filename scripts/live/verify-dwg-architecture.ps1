#Requires -Version 5.1
<#
  ARCHITECTURE BEYOND STRAIGHT WALLS, LIVE.

  Straight walls from double lines were proved by DWG-1. This proves the three
  things that were impossible or broken before DWG-4:

      C  CURVED walls - an arc that survives as an arc, all the way from the
         drawing to the built element and back through the audit
      F  FLOORS with HOLES - a shaft through a slab is a hole, not a second slab
      R  ROOMS - placed by a point that is genuinely inside, including in an
         L-shaped room whose centroid is not

  Each section builds its fixture, converts it, and re-reads the MODEL. A probe
  that only checked the plan would prove the reading and not the building.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-architecture' -Document $Document
$X = 900000.0

<#
  Did ANY element in this apply come back hosted?

  horizun_apply_cad_plan reports stages, and each stage carries the rows
  create_elements re-read after its own commit. host_verified is only present
  where a host was asked for, which is exactly where it matters.
#>
function Get-HzAnyHostVerified {
    param($Applied)
    foreach ($stage in @(Get-HzProp $Applied 'stages')) {
        foreach ($row in @(Get-HzProp $stage 'rows')) {
            if ((Get-HzProp $row 'host_verified') -eq $true) { return $true }
        }
    }
    $false
}

function Get-HzKindCount {
    param($Plan, [string]$Kind)
    $c = Get-HzPath $Plan 'counts_by_kind', $Kind
    if ($null -eq $c) { 0 } else { [int]$c }
}

function Get-HzCode {
    param($Audit, [string]$Code)
    $c = Get-HzPath $Audit 'counts_by_code', $Code
    if ($null -eq $c) { 0 } else { [int]$c }
}

<#
  Build a drawing from an arbitrary set of typed elements, export it, and throw
  the elements away. The wall fixture in the shared library only draws straight
  walls; this section needs curves and rings.
#>
function New-HzShapeFixture {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][array]$Elements,     # create_elements rows, in mm
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][double[]]$CropMin,   # x,y
        [Parameter(Mandatory)][double[]]$CropMax,
        # Optional: the box the created elements must FIT INSIDE, x,y,z in mm.
        #
        # A fixture that builds the wrong shape is not a finding about the
        # bridge, and it is easy to write one: an arc through two points has two
        # readings, and the long way round is a legal arc that fails every probe
        # downstream for reasons that have nothing to do with what is being
        # tested. So the fixture states its own shape and is measured against it
        # BEFORE it becomes a drawing.
        [double[]]$MustFitWithin
    )
    $level = Get-HzFirstLevel $Run
    $rows = @()
    foreach ($e in $Elements) {
        $row = @{}
        foreach ($k in $e.Keys) { $row[$k] = $e[$k] }
        $row['level_id'] = [long]$level.element_id
        $rows += $row
    }
    $made = Invoke-HzWrite -Run $Run -Tool 'horizun_create_elements' -Label "fx-$Tag" -Arguments @{
        target_document = $Run.Document; units = 'mm'; elements = $rows }
    if ([int]$made.Apply.Result.created_verified -ne $rows.Count) {
        throw ("HARNESS: fixture {0} wanted {1} elements and Revit verified {2}" -f
            $Tag, $rows.Count, $made.Apply.Result.created_verified)
    }

    if ($MustFitWithin) {
        $ids = @(@($made.Apply.Result.rows) | ForEach-Object { [long]$_.element_id })
        $inside = @(Get-HzElementsIn -Run $Run -Categories @('OST_Walls', 'OST_Floors', 'OST_Rooms') `
            -Min @($MustFitWithin[0], $MustFitWithin[1], $MustFitWithin[2]) `
            -Max @($MustFitWithin[3], $MustFitWithin[4], $MustFitWithin[5]) -Label "fx-$Tag-shape")
        $insideIds = @($inside | ForEach-Object { [long]$_.element_id })
        $stray = @($ids | Where-Object { $insideIds -notcontains [long]$_ })
        if ($stray.Count -gt 0) {
            throw ("HARNESS: fixture {0} built {1} element(s) that do not fit the shape it declares " +
                   "({2}..{3} x {4}..{5} mm). The fixture is wrong, not the bridge." -f
                   $Tag, $stray.Count, $MustFitWithin[0], $MustFitWithin[3], $MustFitWithin[1], $MustFitWithin[4])
        }
    }

    $viewName = "HZ_ARCH_${Tag}_$($Run.RunId)"
    $view = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-view" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id; name = $viewName })
    }
    $viewId = [long](@($view.Apply.Result.rows)[0].element_id)
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-crop" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'set_crop'; view_id = $viewId
                       box = @($CropMin[0], $CropMin[1], $CropMax[0], $CropMax[1]) })
    }
    $dwg = Join-Path 'C:\hz-live\dwg' ("HZ_ARCH_${Tag}_$($Run.RunId).dwg")
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_export' -Label "fx-$Tag-export" -Arguments @{
        target_document = $Run.Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwg }
    $file = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_ARCH_${Tag}_$($Run.RunId)*.dwg"))[0]
    if ($null -eq $file) { throw "HARNESS: fixture $Tag exported no DWG" }
    [ordered]@{
        fixture_id = "HZ_ARCH_${Tag}_$($Run.RunId)"
        dwg_path = $file.FullName; dwg_name = $file.Name
        dwg_sha256 = (Get-HzSha256 $file.FullName); dwg_bytes = $file.Length
        elements = $Elements.Count
    }
}

function Convert-HzDrawing {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)]$Fixture,
          [Parameter(Mandatory)][hashtable]$Set, [Parameter(Mandatory)][string]$Tag)
    $instance = Add-HzCadLink -Run $Run -DwgPath $Fixture.dwg_path -Label "link-$Tag"
    $level = Get-HzFirstLevel $Run
    $plan = Invoke-HzToolStrict -Run $Run -Tool 'horizun_plan_from_cad' -Label "plan-$Tag" -Arguments @{
        target_document = $Run.Document; instance_id = $instance; requirement_set = $Set
        level_id = [long]$level.element_id }
    [pscustomobject]@{ InstanceId = $instance; Plan = $plan.Result; Set = $Set }
}

function Invoke-HzConversion {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)]$Conversion, [Parameter(Mandatory)][string]$Tag)
    $applyArgs = @{
        target_document = $Run.Document; instance_id = $Conversion.InstanceId
        requirement_set = $Conversion.Set
        apply_binding = $Conversion.Plan.apply_binding
        actions = $Conversion.Plan.execute_plan_request.actions
        candidate_index = $Conversion.Plan.candidate_index
    }
    $dry = Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag-dry" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
    $tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
    $acts = @($Conversion.Plan.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $acts) {
        $t = Get-HzProp $tokens $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    (Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $acts
            idempotency_key = (New-HzKey $Run "apply-$Tag") })).Result
}

# =============================================================================
# C - CURVED WALLS
# =============================================================================
Write-Host "`n== C: curved walls ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

# A quarter-circle wall of radius 5000 about (X+10000, 0), and a straight one so
# the drawing is not trivially all-curve.
#
# WOUND CLOCKWISE, and that is not a detail. From (X+5000, 0) at 180 degrees to
# (X+10000, 5000) at 90, the anticlockwise reading is the 270-degree arc the
# long way round - a legal wall, three times the length, running outside the
# crop box so the export cuts it into a dozen fragments. Both readings pass
# every check create_elements makes, because both describe an arc; only the
# fixture knows which one it meant.
$curveCentre = @(($X + 10000.0), 0.0)
$curved = New-HzShapeFixture -Run $run -Tag 'curve' -CropMin @(($X - 2000), -2000) -CropMax @(($X + 18000), 10000) `
    -MustFitWithin @(($X - 1000.0), -1000.0, -1000.0, ($X + 11000.0), 9000.0, 4000.0) `
    -Elements @(
        @{ kind = 'wall'; start = @(($X + 5000.0), 0.0, 0.0); end = @(($X + 10000.0), 5000.0, 0.0)
           height = 3000.0
           arc = @{ centre = @($curveCentre[0], $curveCentre[1], 0.0); radius = 5000.0; clockwise = $true } },
        @{ kind = 'wall'; start = @($X, 8000.0, 0.0); end = @(($X + 6000.0), 8000.0, 0.0); height = 3000.0 }
    )
foreach ($k in $curved.Keys) { $run.Fixture[$k] = $curved[$k] }
$run.Expected['curved_walls_drawn'] = 1
$run.Expected['curve_radius_mm'] = 5000.0
Add-HzNote $run ("curved fixture {0}" -f $curved.dwg_name)

$null = Reset-HzDocument $run
$instC = Add-HzCadLink -Run $run -DwgPath $curved.dwg_path -Label 'link-curve'
$layerC = Get-HzWallLayer -Run $run -InstanceId $instC
$factsC = Get-HzCadInstanceFacts -Run $run -InstanceId $instC

$qc = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-curve' -Arguments @{
    mode = 'geometry'; instance_id = $instC; max_rows = 2000 }
$arcsKept = [int](Get-HzPath $qc.Result 'harvest_coverage', 'arcs_kept_as_arcs')
Add-HzProbe -Run $run -Id 'C1' -Name 'the harvest keeps the arcs AS arcs, beside the chords it also emits' `
    -Expected 'at least 2 arcs kept (a curved wall exports one line per face)' `
    -Observed ("arcs_kept_as_arcs={0} segments={1}" -f $arcsKept, $qc.Result.segments_matching) `
    -Ok ($arcsKept -ge 2) `
    -Evidence @{ harvest_coverage = $qc.Result.harvest_coverage }

$setC = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-curved'; version = '1.0.0'; title = 'Curved walls from arc pairs' }
    source = @{ units = [string]$factsC.declared_units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'curved'; precedence = 10; discipline = 'architecture'
                 layers = @($layerC); produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
                 geometry = @{ from = 'double_arcs'; min_thickness_mm = 100.0; max_thickness_mm = 400.0
                               min_overlap_fraction = 0.6 } })
}
$level = Get-HzFirstLevel $run
$planC = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-curve' -Arguments @{
    target_document = $Document; instance_id = $instC; requirement_set = $setC; level_id = [long]$level.element_id }
$curvedActions = @($planC.Result.execute_plan_request.actions)
$firstElement = $null
if ($curvedActions.Count -gt 0) { $firstElement = @($curvedActions[0].arguments.elements)[0] }

Add-HzProbe -Run $run -Id 'C2' -Name 'the plan proposes ONE curved wall and emits it as an ARC, not as chords' `
    -Expected '1 wall action carrying an arc block with centre, radius and winding' `
    -Observed ("walls={0} has_arc={1} radius={2}" -f (Get-HzKindCount $planC.Result 'wall'),
        [bool](Get-HzProp $firstElement 'arc'),
        $(if ($firstElement -and (Get-HzProp $firstElement 'arc')) { $firstElement.arc.radius } else { '-' })) `
    -Ok ([int](Get-HzKindCount $planC.Result 'wall') -eq 1 -and $null -ne (Get-HzProp $firstElement 'arc') -and
         [Math]::Abs([double]$firstElement.arc.radius - 5000.0) -lt 5.0) `
    -Evidence @{ element = $firstElement; counts = $planC.Result.counts_by_kind }

$wallsBeforeC = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-before-c'
$appliedC = Invoke-HzConversion -Run $run -Tag 'curve' -Conversion ([pscustomobject]@{
    InstanceId = $instC; Plan = $planC.Result; Set = $setC })
$wallsAfterC = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after-c'

$curveRows = @()
foreach ($stage in @($appliedC.stages)) {
    $rows = Get-HzProp $stage 'rows'
    if ($rows) { $curveRows += @($rows) }
}
Add-HzProbe -Run $run -Id 'C3' -Name 'ONE wall is built, not one per chord, and its CURVE is verified after the commit' `
    -Expected '1 wall created and verified; a chorded reading would have built a dozen' `
    -Observed ("created={0} walls_delta={1} state={2}" -f $appliedC.created_verified,
        ($wallsAfterC - $wallsBeforeC), $appliedC.state) `
    -Ok ([int]$appliedC.created_verified -eq 1 -and ($wallsAfterC - $wallsBeforeC) -eq 1) `
    -Evidence @{ state = $appliedC.state; provenance = $appliedC.provenance }

$auditC = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-curve' -Arguments @{
    target_document = $Document; instance_id = $instC; requirement_set = $setC }
Add-HzProbe -Run $run -Id 'C4' -Name 'and the audit AGREES - a correctly built curved wall is not reported as moved' `
    -Expected 'matched by revision, no moved finding, agrees=true' `
    -Observed ("by_revision={0} moved={1} agrees={2}" -f $auditC.Result.matched.by_revision,
        (Get-HzCode $auditC.Result 'moved'), $auditC.Result.agrees) `
    -Ok ([int]$auditC.Result.matched.by_revision -ge 1 -and (Get-HzCode $auditC.Result 'moved') -eq 0) `
    -Evidence @{ matched = $auditC.Result.matched; counts = $auditC.Result.counts_by_code }

# THE REFUSAL: a declaration that describes no arc
$badArc = Invoke-HzTool -Run $run -Tool 'horizun_create_elements' -Label 'r-bad-arc' -Arguments @{
    target_document = $Document; units = 'mm'; dry_run = $true
    elements = @(@{ kind = 'wall'; start = @($X, 0.0, 0.0); end = @(($X + 5000.0), 0.0, 0.0)
                    height = 3000.0; level_id = [long]$level.element_id
                    arc = @{ centre = @($X, 0.0, 0.0); radius = 9999.0 } })
}
$badText = $badArc.Text
if (-not $badArc.IsError) { $badText = ($badArc.Result | ConvertTo-Json -Depth 12 -Compress) }
Add-HzProbe -Run $run -Id 'C5' -Name 'a centre that is not equidistant from both ends is refused, naming both distances' `
    -Expected 'arc_does_not_close, before anything is written' `
    -Observed (Limit-HzText $badText 200) `
    -Ok ($badText -match 'arc_does_not_close') `
    -Evidence @{ reply = (Limit-HzText $badText 600) }

# =============================================================================
# F - FLOORS WITH HOLES
# =============================================================================
Write-Host "`n== F: floors with holes ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

# A slab with a shaft through it, drawn as two rings of model lines... but a
# floor exports its EDGES, so the fixture is a floor with an opening in it.
$slabOuter = @(
    @(($X + 0.0), 0.0, 0.0), @(($X + 12000.0), 0.0, 0.0),
    @(($X + 12000.0), 9000.0, 0.0), @(($X + 0.0), 9000.0, 0.0))
$slabHole = @(
    @(($X + 4000.0), 3000.0, 0.0), @(($X + 4000.0), 6000.0, 0.0),
    @(($X + 7000.0), 6000.0, 0.0), @(($X + 7000.0), 3000.0, 0.0))
$slabFixture = New-HzShapeFixture -Run $run -Tag 'slab' -CropMin @(($X - 2000), -2000) -CropMax @(($X + 14000), 11000) `
    -Elements @(@{ kind = 'floor'; profile = @($slabOuter, $slabHole) })
$run.Fixture['slab_fixture'] = $slabFixture
$run.Expected['slab_outline_m2'] = 108.0
$run.Expected['slab_hole_m2'] = 9.0
Add-HzNote $run ("slab fixture {0}" -f $slabFixture.dwg_name)

$null = Reset-HzDocument $run
$instF = Add-HzCadLink -Run $run -DwgPath $slabFixture.dwg_path -Label 'link-slab'
$layersF = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-slab-layers' -Arguments @{
    mode = 'layers'; instance_id = $instF }
$slabLayers = @($layersF.Result.layers | Where-Object { $_.layer -match '(?i)FLOOR|SLAB' })
$layerF = $(if ($slabLayers.Count) { [string]$slabLayers[0].layer } else { [string](@($layersF.Result.layers)[0].layer) })
$factsF = Get-HzCadInstanceFacts -Run $run -InstanceId $instF

$setF = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-slabs'; version = '1.0.0'; title = 'Slabs from closed loops' }
    source = @{ units = [string]$factsF.declared_units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'slabs'; precedence = 10; discipline = 'architecture'
                 layers = @($layerF); produces = 'floor'; category = 'OST_Floors'
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 1000000.0 } })
}
$level = Get-HzFirstLevel $run
$planF = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-slab' -Arguments @{
    target_document = $Document; instance_id = $instF; requirement_set = $setF; level_id = [long]$level.element_id }
$slabActions = @($planF.Result.execute_plan_request.actions)
$slabRow = $null
if ($slabActions.Count -gt 0) { $slabRow = @($slabActions[0].arguments.elements)[0] }
$ringCount = 0
if ($slabRow -and (Get-HzProp $slabRow 'profile')) { $ringCount = @($slabRow.profile).Count }

Add-HzProbe -Run $run -Id 'F1' -Name 'the shaft is read as a HOLE in the slab, not as a second slab' `
    -Expected '1 floor action whose profile carries 2 rings - outer, then the hole' `
    -Observed ("floors={0} rings={1}" -f (Get-HzKindCount $planF.Result 'floor'), $ringCount) `
    -Ok ([int](Get-HzKindCount $planF.Result 'floor') -eq 1 -and $ringCount -eq 2) `
    -Evidence @{ counts = $planF.Result.counts_by_kind; rings = $ringCount }

$floorsBefore = Get-HzElementCount -Run $run -Categories @('OST_Floors') -Label 'floors-before'
$appliedF = Invoke-HzConversion -Run $run -Tag 'slab' -Conversion ([pscustomobject]@{
    InstanceId = $instF; Plan = $planF.Result; Set = $setF })
$floorsAfter = Get-HzElementCount -Run $run -Categories @('OST_Floors') -Label 'floors-after'

Add-HzProbe -Run $run -Id 'F2' -Name 'the slab is BUILT, with its hole, and verified by re-reading' `
    -Expected '1 floor created and verified' `
    -Observed ("created={0} floors_delta={1} state={2}" -f $appliedF.created_verified,
        ($floorsAfter - $floorsBefore), $appliedF.state) `
    -Ok ([int]$appliedF.created_verified -eq 1 -and ($floorsAfter - $floorsBefore) -eq 1) `
    -Evidence @{ state = $appliedF.state; provenance = $appliedF.provenance }

$auditF = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-slab' -Arguments @{
    target_document = $Document; instance_id = $instF; requirement_set = $setF }
Add-HzProbe -Run $run -Id 'F3' -Name 'and the audit agrees about the slab it just built' `
    -Expected 'matched, nothing blocking' `
    -Observed ("matched={0} blocking={1}" -f $auditF.Result.matched.total,
        $auditF.Result.counts_by_severity.blocking) `
    -Ok ([int]$auditF.Result.matched.total -ge 1 -and [int]$auditF.Result.counts_by_severity.blocking -eq 0) `
    -Evidence @{ matched = $auditF.Result.matched; counts = $auditF.Result.counts_by_code }

# =============================================================================
# R - ROOMS
# =============================================================================
Write-Host "`n== R: rooms ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

# An L-SHAPED room, on purpose: its centroid is in the notch, outside it.
$lShape = @(
    @(($X + 0.0), 0.0, 0.0), @(($X + 10000.0), 0.0, 0.0), @(($X + 10000.0), 2500.0, 0.0),
    @(($X + 2500.0), 2500.0, 0.0), @(($X + 2500.0), 10000.0, 0.0), @(($X + 0.0), 10000.0, 0.0))
$roomFixture = New-HzShapeFixture -Run $run -Tag 'room' -CropMin @(($X - 2000), -2000) -CropMax @(($X + 12000), 12000) `
    -Elements @(@{ kind = 'floor'; profile = @(, $lShape) })   # ONE loop of six points:
        # @($lShape) would flatten it back to six loops of three numbers, and
        # create_elements would read each as a point. The comma is the loop.
$run.Fixture['room_fixture'] = $roomFixture
$run.Expected['room_shape'] = 'L, whose centroid is outside it'
Add-HzNote $run ("room fixture {0}" -f $roomFixture.dwg_name)

$null = Reset-HzDocument $run
$instR = Add-HzCadLink -Run $run -DwgPath $roomFixture.dwg_path -Label 'link-room'
$layersR = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-room-layers' -Arguments @{
    mode = 'layers'; instance_id = $instR }
$layerR = [string](@($layersR.Result.layers)[0].layer)
$factsR = Get-HzCadInstanceFacts -Run $run -InstanceId $instR

$setR = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-rooms'; version = '1.0.0'; title = 'Rooms from closed loops' }
    source = @{ units = [string]$factsR.declared_units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'rooms'; precedence = 10; discipline = 'architecture'
                 layers = @($layerR); produces = 'room'; category = 'OST_Rooms'
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 1000000.0 } })
}
$level = Get-HzFirstLevel $run
$planR = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-room' -Arguments @{
    target_document = $Document; instance_id = $instR; requirement_set = $setR; level_id = [long]$level.element_id }
$roomActions = @($planR.Result.execute_plan_request.actions)
$roomRow = $null
if ($roomActions.Count -gt 0) { $roomRow = @($roomActions[0].arguments.elements)[0] }

Add-HzProbe -Run $run -Id 'R1' -Name 'a room is planned as a POINT, not a profile - the shape create_elements actually reads' `
    -Expected '1 room action carrying point and no profile' `
    -Observed ("rooms={0} has_point={1} has_profile={2}" -f (Get-HzKindCount $planR.Result 'room'),
        [bool](Get-HzProp $roomRow 'point'), [bool](Get-HzProp $roomRow 'profile')) `
    -Ok ([int](Get-HzKindCount $planR.Result 'room') -eq 1 -and $null -ne (Get-HzProp $roomRow 'point') -and
         $null -eq (Get-HzProp $roomRow 'profile')) `
    -Evidence @{ element = $roomRow }

# THE POINT MUST BE INSIDE THE L. Its centroid is not, and that is the whole test.
$px = [double](@($roomRow.point)[0]); $py = [double](@($roomRow.point)[1])
$insideL = (($px -ge $X) -and ($px -le ($X + 10000)) -and ($py -ge 0) -and ($py -le 2500)) -or
           (($px -ge $X) -and ($px -le ($X + 2500)) -and ($py -ge 0) -and ($py -le 10000))
$cx = ($lShape | ForEach-Object { $_[0] } | Measure-Object -Average).Average
$cy = ($lShape | ForEach-Object { $_[1] } | Measure-Object -Average).Average
$centroidInside = (($cx -ge $X) -and ($cx -le ($X + 10000)) -and ($cy -ge 0) -and ($cy -le 2500)) -or
                  (($cx -ge $X) -and ($cx -le ($X + 2500)) -and ($cy -ge 0) -and ($cy -le 10000))
Add-HzProbe -Run $run -Id 'R2' -Name "the point is INSIDE the L, and the centroid - which is not - was rejected" `
    -Expected 'the planned point lies in one of the L arms; the centroid lies in neither' `
    -Observed ("point=({0}, {1}) inside={2}; centroid=({3}, {4}) inside={5}" -f
        [Math]::Round($px, 1), [Math]::Round($py, 1), $insideL,
        [Math]::Round($cx, 1), [Math]::Round($cy, 1), $centroidInside) `
    -Ok ($insideL -and -not $centroidInside) `
    -Evidence @{ point = @($px, $py); centroid = @($cx, $cy)
                 note = 'a room placed at this centroid lands in the corridor next door' }

$roomsBefore = Get-HzElementCount -Run $run -Categories @('OST_Rooms') -Label 'rooms-before'
$appliedR = Invoke-HzConversion -Run $run -Tag 'room' -Conversion ([pscustomobject]@{
    InstanceId = $instR; Plan = $planR.Result; Set = $setR })
$roomsAfter = Get-HzElementCount -Run $run -Categories @('OST_Rooms') -Label 'rooms-after'

Add-HzProbe -Run $run -Id 'R3' -Name 'the room is BUILT and verified by re-reading' `
    -Expected '1 room created and verified' `
    -Observed ("created={0} rooms_delta={1} state={2}" -f $appliedR.created_verified,
        ($roomsAfter - $roomsBefore), $appliedR.state) `
    -Ok ([int]$appliedR.created_verified -eq 1 -and ($roomsAfter - $roomsBefore) -eq 1) `
    -Evidence @{ state = $appliedR.state; provenance = $appliedR.provenance }

# =============================================================================
# D, W, K - DOORS, WINDOWS, AND ARCHITECTURAL COLUMNS
#
# One drawing carries all three, because that is how a floor plan arrives, and
# because the interesting part is the ORDER. A plan is computed before it is
# applied, so a single run cannot build a wall and then host a door in it: the
# wall does not exist at the moment the plan is made. The bridge refuses rather
# than placing the door unhosted - a door-shaped object standing beside its own
# opening creates, verifies and schedules perfectly, which is what makes it
# dangerous - and the refusal names the fix.
# =============================================================================
Write-Host "`n== D, W, K: doors, windows and columns ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

$doorSymbol = Get-HzHostedSymbol -Run $run -Kind 'Door'
$windowSymbol = Get-HzHostedSymbol -Run $run -Kind 'Window'
$columnSymbol = Get-HzHostedSymbol -Run $run -Kind 'Column'

if ($null -eq $doorSymbol -or $null -eq $windowSymbol -or $null -eq $columnSymbol) {
    # NOT a failure and NOT a pass. Revit ships family TEMPLATES with the
    # product; a machine without them cannot host anything, and saying so is the
    # honest answer.
    foreach ($id in @('D1', 'D2', 'D3', 'D4', 'D5', 'W1', 'W2', 'K1', 'K2')) {
        Add-HzProbe -Run $run -Id $id -Name 'hosted families need a family template on this machine' `
            -Expected 'Metric Door.rft, Metric Window.rft and Metric Column.rft' `
            -Observed ("door={0} window={1} column={2}" -f ($null -ne $doorSymbol),
                ($null -ne $windowSymbol), ($null -ne $columnSymbol)) `
            -Status 'fixture_missing'
    }
} else {
    Add-HzNote $run ("symbols: door '{0}' window '{1}' column '{2}'" -f
        $doorSymbol.type_name, $windowSymbol.type_name, $columnSymbol.type_name)
    $run.Fixture['door_type'] = $doorSymbol.type_name
    $run.Fixture['window_type'] = $windowSymbol.type_name
    $run.Fixture['column_type'] = $columnSymbol.type_name

    $level = Get-HzFirstLevel $run
    $doorAt = @(($X + 3000.0), 0.0)
    $windowAt = @(($X + 9000.0), 0.0)
    $columnAt = @(($X + 6000.0), 5000.0)

    # The fixture, built in Revit and then exported: a wall with a door and a
    # window in it, and a column standing clear of it.
    $hostWall = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-hostwall' -Arguments @{
        target_document = $Document; units = 'mm'
        elements = @(@{ kind = 'wall'; start = @($X, 0.0, 0.0); end = @(($X + 12000.0), 0.0, 0.0)
                        height = 3000.0; level_id = [long]$level.element_id })
    }
    $hostWallId = [long](@($hostWall.Apply.Result.rows)[0].element_id)

    $inserts = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-inserts' -Arguments @{
        target_document = $Document; units = 'mm'
        elements = @(
            @{ kind = 'family_instance'; type_id = [long]$doorSymbol.type_id
               point = @($doorAt[0], $doorAt[1], 0.0); level_id = [long]$level.element_id
               host_id = $hostWallId },
            @{ kind = 'family_instance'; type_id = [long]$windowSymbol.type_id
               point = @($windowAt[0], $windowAt[1], 1000.0); level_id = [long]$level.element_id
               host_id = $hostWallId },
            @{ kind = 'family_instance'; type_id = [long]$columnSymbol.type_id
               point = @($columnAt[0], $columnAt[1], 0.0); level_id = [long]$level.element_id })
    }
    if ([int]$inserts.Apply.Result.created_verified -ne 3) {
        throw ("HARNESS: the fixture wanted a door, a window and a column and Revit verified {0}" -f
            $inserts.Apply.Result.created_verified)
    }

    $viewName = "HZ_ARCH_dwk_$($run.RunId)"
    $view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-dwk-view' -Arguments @{
        target_document = $Document; units = 'mm'
        actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                       name = $viewName }) }
    $dwkView = [long](@($view.Apply.Result.rows)[0].element_id)
    $null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-dwk-crop' -Arguments @{
        target_document = $Document; units = 'mm'
        actions = @(@{ operation = 'set_crop'; view_id = $dwkView
                       box = @(($X - 2000.0), -3000.0, ($X + 14000.0), 8000.0) }) }
    $dwkPath = Join-Path 'C:\hz-live\dwg' ("HZ_ARCH_dwk_$($run.RunId).dwg")
    $null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-dwk-export' -Arguments @{
        target_document = $Document; format = 'dwg'; view_ids = @($dwkView); output_path = $dwkPath }
    $dwkFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter "HZ_ARCH_dwk_$($run.RunId)*.dwg")[0]
    if ($null -eq $dwkFile) { throw 'HARNESS: the door/window/column fixture exported no DWG' }
    $run.Fixture['dwk_dwg_name'] = $dwkFile.Name
    $run.Fixture['dwk_dwg_sha256'] = (Get-HzSha256 $dwkFile.FullName)
    Add-HzNote $run ("door/window/column fixture {0}" -f $dwkFile.Name)

    # ---------------------------------------------------------------- link it
    $null = Reset-HzDocument $run
    $level = Get-HzFirstLevel $run

    # THE RESET DISCARDS THE LOADED FAMILIES TOO.
    #
    # Isolation reopens the document from disk, and nothing this run did was
    # saved - which is the point. So the symbols are provisioned again into the
    # document the conversion will actually run in, and the names are re-read
    # from THAT document rather than carried over from the one that exported
    # the drawing.
    $doorSymbol = Get-HzHostedSymbol -Run $run -Kind 'Door'
    $windowSymbol = Get-HzHostedSymbol -Run $run -Kind 'Window'
    $columnSymbol = Get-HzHostedSymbol -Run $run -Kind 'Column'
    if ($null -eq $doorSymbol -or $null -eq $windowSymbol -or $null -eq $columnSymbol) {
        throw 'HARNESS: the families provisioned before the reset could not be provisioned after it'
    }
    $instD = Add-HzCadLink -Run $run -DwgPath $dwkFile.FullName -Label 'link-dwk'
    $wallLayer = Get-HzWallLayer -Run $run -InstanceId $instD
    $factsD = Get-HzCadInstanceFacts -Run $run -InstanceId $instD

    # WHICH LAYER, MEASURED, AND BY DIFFERENCE.
    #
    # The names Revit exports come from its own configuration - one machine's
    # A-DOOR is another's A-WALL-____-OTLN - so what is known here is only where
    # each thing was PUT. A wall draws on several layers at once (its cut face
    # and its outline are two), so excluding one of them is not enough: the
    # layers of a plain stretch of the same wall, carrying no insert at all, are
    # what a symbol's layer has to differ from.
    # SEVERAL plain stretches, not one: a wall draws its outline only where
    # something interrupts it, so a single clear sample in the middle of a run
    # misses layers that are ordinary wall everywhere else.
    $wallOnlySet = @{}
    $plainAt = @(@(($X + 800.0), 0.0), @(($X + 5500.0), 0.0), @(($X + 11600.0), 0.0))
    for ($i = 0; $i -lt $plainAt.Count; $i++) {
        foreach ($k in (Get-HzLayersNear -Run $run -InstanceId $instD -Point $plainAt[$i] `
                        -RadiusMm 700 -Label ("layers-plain-$i")).Keys) { $wallOnlySet[$k] = $true }
    }
    $wallOnlyLayers = @($wallOnlySet.Keys)
    Add-HzNote $run ("a plain stretch of wall draws on: " + (($wallOnlyLayers | Sort-Object) -join ', '))

    # EXCLUSIVITY, not popularity. The wall's outline layer is near the door AND
    # near the window - it is drawn at every jamb - and on one run it outnumbered
    # the glazing, so the window rule claimed the wall's outline and still, by
    # luck, produced one candidate in the right place. A symbol's own layer is
    # the one near THIS thing and near none of the others.
    $doorLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $instD -Point $doorAt `
        -OtherPoints (@($windowAt, $columnAt) + $plainAt) -Label 'layer-door'
    $windowLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $instD -Point $windowAt `
        -OtherPoints (@($doorAt, $columnAt) + $plainAt) -Label 'layer-window'
    $columnLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $instD -Point $columnAt `
        -OtherPoints (@($doorAt, $windowAt) + $plainAt) -Label 'layer-column'
    foreach ($pair in @(@('door', $doorLayer), @('window', $windowLayer), @('column', $columnLayer))) {
        if (-not $pair[1]) {
            throw ("HARNESS: the fixture drew no {0} symbol on any layer of its own. The provisioned family " +
                   "has no geometry crossing the view's cut plane, so there is nothing in the drawing to " +
                   "read - that is the fixture, not the bridge." -f $pair[0])
        }
    }
    Add-HzNote $run ("layers: wall='{0}' door='{1}' window='{2}' column='{3}'" -f
        $wallLayer, $doorLayer, $windowLayer, $columnLayer)

    function New-HzInsertSet {
        param([string]$Id, [string]$Layer, [string]$Produces, [string]$Category,
              [string]$FamilyType, [string]$Units, [double]$ClusterMm = 1200.0)
        @{
            schema = 'horizun.cad-requirements/1'
            requirement_set = @{ id = "hz-live-$Id"; version = '1.0.0'; title = "Live $Id" }
            source = @{ units = $Units }
            # A SYMBOL IS NOT A POINT. Its marks spread over the width of the
            # thing it stands for, and the cluster centre is pulled off the wall
            # by whatever the symbol draws into the room. 300 mm is deliberate
            # and declared; gap_mm may not be smaller than it, and the set
            # refuses a document that says otherwise.
            tolerances = @{ point_mm = 300.0; gap_mm = 300.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
            rules = @(@{ id = $Id; precedence = 10; discipline = 'architecture'
                         layers = @($Layer); produces = $Produces; category = $Category
                         family_type = $FamilyType
                         geometry = @{ from = 'point_clusters'; cluster_radius_mm = $ClusterMm } })
        }
    }

    $setD = New-HzInsertSet -Id 'doors' -Layer $doorLayer -Produces 'door' -Category 'OST_Doors' `
        -FamilyType $doorSymbol.type_name -Units ([string]$factsD.declared_units)

    # ------------------------------- D1: the refusal, BEFORE the walls exist
    $tooEarly = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-door-early' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $setD
        level_id = [long]$level.element_id }
    $earlyText = $tooEarly.Text
    if (-not $tooEarly.IsError) { $earlyText = ($tooEarly.Result | ConvertTo-Json -Depth 12 -Compress) }
    Add-HzProbe -Run $run -Id 'D1' -Name 'a door planned before its wall exists is REFUSED, and the refusal names the fix' `
        -Expected 'host_not_found - convert the wall layers first' `
        -Observed (Limit-HzText $earlyText 220) `
        -Ok ($earlyText -match 'host_not_found') `
        -Evidence @{ reply = (Limit-HzText $earlyText 700) }

    # -------------------------------------------- now build the walls, pass 1
    # ONE WALL, NOT THREE.
    #
    # The drawing shows this wall broken at the door and again at the window,
    # because that is what a plan section looks like. Declaring how wide an
    # opening may be is what lets the reading span them - and without it the
    # door has no continuous wall to live in, which is exactly how the gap was
    # found.
    $wallSet = New-HzWallRequirementSet -Layer $wallLayer -Units ([string]$factsD.declared_units) `
        -BridgeOpeningsMm 1500.0
    $wallPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-dwk-walls' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $wallSet
        level_id = [long]$level.element_id }
    $builtWalls = Invoke-HzConversion -Run $run -Tag 'dwk-walls' -Conversion ([pscustomobject]@{
        InstanceId = $instD; Plan = $wallPlan.Result; Set = $wallSet })
    if ([int]$builtWalls.created_verified -lt 1) {
        throw ("HARNESS: the wall pass built {0} walls; the openings have nothing to live in" -f
            $builtWalls.created_verified)
    }
    Add-HzNote $run ("wall pass built {0} wall(s)" -f $builtWalls.created_verified)

    $spanned = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
        -Min @(($X - 500.0), -500.0, -1000.0) -Max @(($X + 12500.0), 500.0, 4000.0) -Label 'walls-spanned')
    Add-HzProbe -Run $run -Id 'D0' -Name 'the wall broken at two openings is read as ONE continuous wall' `
        -Expected '1 wall spanning the whole run, not one fragment per opening' `
        -Observed ("built={0} on_the_line={1}" -f $builtWalls.created_verified, $spanned.Count) `
        -Ok ([int]$builtWalls.created_verified -eq 1 -and $spanned.Count -eq 1) `
        -Evidence @{ bridge_openings_mm = 1500.0
                     note = 'a plan drawing breaks a wall at every door and window' }

    # ------------------------------------------------- D2/D3: the door, pass 2
    $planD = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-door' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $setD
        level_id = [long]$level.element_id }
    $doorActions = @($planD.Result.execute_plan_request.actions)
    $doorRow = $null
    if ($doorActions.Count -gt 0) { $doorRow = @($doorActions[0].arguments.elements)[0] }
    $hostNamed = @(@($planD.Result.apply_binding.resolved_names) |
                   Where-Object { [string]$_.what -eq 'host_wall' })

    Add-HzProbe -Run $run -Id 'D2' -Name 'once the wall is there the plan NAMES it, and records which wall it chose' `
        -Expected '1 family_instance carrying host_id, and a host_wall in resolved_names' `
        -Observed ("doors={0} host_id={1} named={2}" -f (Get-HzKindCount $planD.Result 'door'),
            [string](Get-HzProp $doorRow 'host_id'), $hostNamed.Count) `
        -Ok ((Get-HzKindCount $planD.Result 'door') -eq 1 -and
             $null -ne (Get-HzProp $doorRow 'host_id') -and $hostNamed.Count -eq 1) `
        -Evidence @{ row = $doorRow; resolved = $hostNamed }

    $doorsBefore = Get-HzElementCount -Run $run -Categories @('OST_Doors') -Label 'doors-before'
    $appliedD = Invoke-HzConversion -Run $run -Tag 'door' -Conversion ([pscustomobject]@{
        InstanceId = $instD; Plan = $planD.Result; Set = $setD })
    $doorsNow = @(Get-HzElements -Run $run -Categories @('OST_Doors') -Label 'doors-after')
    $doorsAfter = $doorsNow.Count
    $doorHostVerified = Get-HzAnyHostVerified $appliedD

    # AND ASK THE MODEL, not only the write. host_verified is what the command
    # re-read after its own commit; host_id is what the document says now, and a
    # door that agrees with both is hosted twice over.
    $doorHostFromModel = $null
    if ($doorsNow.Count -eq 1) { $doorHostFromModel = Get-HzProp $doorsNow[0] 'host_id' }

    Add-HzProbe -Run $run -Id 'D3' -Name 'the door is built IN the wall - host_verified after the commit, and the model agrees' `
        -Expected '1 door created, host_verified true, and its host is the wall the plan named' `
        -Observed ("created={0} doors_delta={1} host_verified={2} model_host={3}" -f $appliedD.created_verified,
            ($doorsAfter - $doorsBefore), $doorHostVerified, $doorHostFromModel) `
        -Ok ([int]$appliedD.created_verified -eq 1 -and ($doorsAfter - $doorsBefore) -eq 1 -and
             $doorHostVerified -and $null -ne $doorHostFromModel -and
             [long]$doorHostFromModel -eq [long](Get-HzProp $doorRow 'host_id')) `
        -Evidence @{ state = $appliedD.state; stages = $appliedD.stages
                     planned_host = (Get-HzProp $doorRow 'host_id'); model_host = $doorHostFromModel }

    $auditD = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-door' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $setD }
    Add-HzProbe -Run $run -Id 'D4' -Name 'and the audit finds the door it just placed' `
        -Expected 'matched by revision, no moved finding' `
        -Observed ("by_revision={0} moved={1}" -f $auditD.Result.matched.by_revision,
            (Get-HzCode $auditD.Result 'moved')) `
        -Ok ([int]$auditD.Result.matched.by_revision -ge 1 -and (Get-HzCode $auditD.Result 'moved') -eq 0) `
        -Evidence @{ matched = $auditD.Result.matched; counts = $auditD.Result.counts_by_code }

    # -------------------------- D5: a symbol nowhere near a wall is refused
    $farSet = New-HzInsertSet -Id 'doors-far' -Layer $columnLayer -Produces 'door' -Category 'OST_Doors' `
        -FamilyType $doorSymbol.type_name -Units ([string]$factsD.declared_units)
    $far = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-door-far' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $farSet
        level_id = [long]$level.element_id }
    $farText = $far.Text
    if (-not $far.IsError) { $farText = ($far.Result | ConvertTo-Json -Depth 12 -Compress) }
    Add-HzProbe -Run $run -Id 'D5' -Name 'a door symbol 5 m from any wall is refused, and the refusal says how far' `
        -Expected 'host_too_far, naming the distance and the allowance' `
        -Observed (Limit-HzText $farText 220) `
        -Ok ($farText -match 'host_too_far') `
        -Evidence @{ reply = (Limit-HzText $farText 700) }

    # ------------------------------------------------------ W: the window
    $setW = New-HzInsertSet -Id 'windows' -Layer $windowLayer -Produces 'window' -Category 'OST_Windows' `
        -FamilyType $windowSymbol.type_name -Units ([string]$factsD.declared_units)
    $planW = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-window' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $setW
        level_id = [long]$level.element_id }
    $windowActions = @($planW.Result.execute_plan_request.actions)
    $windowRow = $null
    if ($windowActions.Count -gt 0) { $windowRow = @($windowActions[0].arguments.elements)[0] }
    Add-HzProbe -Run $run -Id 'W1' -Name 'a window is hosted the same way a door is, and by the same wall' `
        -Expected '1 family_instance carrying host_id' `
        -Observed ("windows={0} host_id={1}" -f (Get-HzKindCount $planW.Result 'window'),
            [string](Get-HzProp $windowRow 'host_id')) `
        -Ok ((Get-HzKindCount $planW.Result 'window') -eq 1 -and
             $null -ne (Get-HzProp $windowRow 'host_id')) `
        -Evidence @{ row = $windowRow }

    $windowsBefore = Get-HzElementCount -Run $run -Categories @('OST_Windows') -Label 'windows-before'
    $appliedW = Invoke-HzConversion -Run $run -Tag 'window' -Conversion ([pscustomobject]@{
        InstanceId = $instD; Plan = $planW.Result; Set = $setW })
    $windowsNow = @(Get-HzElements -Run $run -Categories @('OST_Windows') -Label 'windows-after')
    $windowsAfter = $windowsNow.Count
    $windowHostVerified = Get-HzAnyHostVerified $appliedW
    $windowHostFromModel = $null
    if ($windowsNow.Count -eq 1) { $windowHostFromModel = Get-HzProp $windowsNow[0] 'host_id' }

    Add-HzProbe -Run $run -Id 'W2' -Name 'the window is built IN the wall and the model says which wall' `
        -Expected '1 window created, host_verified true, host_id agreeing with the plan' `
        -Observed ("created={0} windows_delta={1} host_verified={2} model_host={3}" -f $appliedW.created_verified,
            ($windowsAfter - $windowsBefore), $windowHostVerified, $windowHostFromModel) `
        -Ok ([int]$appliedW.created_verified -eq 1 -and ($windowsAfter - $windowsBefore) -eq 1 -and
             $windowHostVerified -and $null -ne $windowHostFromModel -and
             [long]$windowHostFromModel -eq [long](Get-HzProp $windowRow 'host_id')) `
        -Evidence @{ state = $appliedW.state; planned_host = (Get-HzProp $windowRow 'host_id')
                     model_host = $windowHostFromModel }

    # ------------------------------------------- K: the architectural column
    $setK = New-HzInsertSet -Id 'columns' -Layer $columnLayer -Produces 'column' -Category 'OST_Columns' `
        -FamilyType $columnSymbol.type_name -Units ([string]$factsD.declared_units)
    $planK = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-column' -Arguments @{
        target_document = $Document; instance_id = $instD; requirement_set = $setK
        level_id = [long]$level.element_id }
    $columnActions = @($planK.Result.execute_plan_request.actions)
    $columnRow = $null
    if ($columnActions.Count -gt 0) { $columnRow = @($columnActions[0].arguments.elements)[0] }
    Add-HzProbe -Run $run -Id 'K1' -Name 'a column stands on a level and claims NO wall host' `
        -Expected '1 family_instance with a point and no host_id' `
        -Observed ("columns={0} has_point={1} host_id={2}" -f (Get-HzKindCount $planK.Result 'column'),
            [bool](Get-HzProp $columnRow 'point'), [string](Get-HzProp $columnRow 'host_id')) `
        -Ok ((Get-HzKindCount $planK.Result 'column') -eq 1 -and
             $null -ne (Get-HzProp $columnRow 'point') -and
             $null -eq (Get-HzProp $columnRow 'host_id')) `
        -Evidence @{ row = $columnRow }

    $columnsBefore = Get-HzElementCount -Run $run -Categories @('OST_Columns') -Label 'columns-before'
    $appliedK = Invoke-HzConversion -Run $run -Tag 'column' -Conversion ([pscustomobject]@{
        InstanceId = $instD; Plan = $planK.Result; Set = $setK })
    $columnsAfter = Get-HzElementCount -Run $run -Categories @('OST_Columns') -Label 'columns-after'
    $placed = @(Get-HzElementsIn -Run $run -Categories @('OST_Columns') `
        -Min @(($columnAt[0] - 600.0), ($columnAt[1] - 600.0), -1000.0) `
        -Max @(($columnAt[0] + 600.0), ($columnAt[1] + 600.0), 4000.0) -Label 'column-where')
    Add-HzProbe -Run $run -Id 'K2' -Name 'the column is built WHERE THE DRAWING PUT IT, re-read from the model' `
        -Expected '1 column created, and found within 600 mm of the drawn cluster' `
        -Observed ("created={0} delta={1} within_600mm={2}" -f $appliedK.created_verified,
            ($columnsAfter - $columnsBefore), $placed.Count) `
        -Ok ([int]$appliedK.created_verified -eq 1 -and ($columnsAfter - $columnsBefore) -eq 1 -and
             $placed.Count -eq 1) `
        -Evidence @{ state = $appliedK.state; drawn_at = $columnAt }
}

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
