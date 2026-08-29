#Requires -Version 5.1
<#
  A LINE THAT DIVIDES A ROOM, AND A LINE THAT ONLY LOOKS LIKE ONE.

  Room separation lines are the thing a plan drawing shows and the model has no
  wall for: the boundary between a lobby and a corridor that nobody built. They
  are MODEL curves in a particular category, and a detail line drawn along the
  same coordinates is indistinguishable in every view and bounds nothing at all.

  So this harness does not ask whether a line was created. It asks whether the
  ROOM CHANGED SHAPE. The same room is placed at the same point twice - once
  before the separator exists and once after - and its extent is read from the
  model both times. A separator that bounds nothing leaves those two identical.

  The fixture is built in Revit, exported to DWG and read back, so what is
  converted is a drawing rather than a description of one.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-separators' -Document $Document
$X = 906000.0

# A closed enclosure, and one line drawn across the middle of it. The line is a
# GRID because a grid is the one thing that draws a single straight line on a
# layer of its own in a plan export - measured in this campaign twice over.
$w = 12000.0
$h = 8000.0
$splitY = 4000.0
$roomAt = @(($X + 6000.0), 2000.0)
$farRoomAt = @(($X + 6000.0), 6000.0)

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

function Invoke-HzConversion {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)]$Plan, [Parameter(Mandatory)]$Set,
          [Parameter(Mandatory)][long]$InstanceId, [Parameter(Mandatory)][string]$Tag)
    $applyArgs = @{
        target_document = $Run.Document; instance_id = $InstanceId; requirement_set = $Set
        apply_binding = $Plan.apply_binding
        actions = $Plan.execute_plan_request.actions
        candidate_index = $Plan.candidate_index
    }
    $dry = Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag-dry" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
    $tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
    $acts = @($Plan.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $acts) {
        $t = Get-HzProp $tokens $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    (Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $acts
            idempotency_key = (New-HzKey $Run "apply-$Tag") })).Result
}

<#
  How far the room reaches in Y, read from the model. This is the whole
  measurement: a separator that bounds nothing leaves it unchanged.
#>
function Get-HzRoomSpanY {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long]$RoomId, [string]$Label = 'room-span')
    $rows = @(Get-HzElements -Run $Run -Categories @('OST_Rooms') -WithBox -Label $Label |
              Where-Object { [long]$_.element_id -eq $RoomId })
    if ($rows.Count -ne 1) { return $null }
    $box = Get-HzProp $rows[0] 'bounding_box'
    if ($null -eq $box) { return $null }
    [ordered]@{ Min = [double](@($box.min)[1]); Max = [double](@($box.max)[1]) }
}

function New-HzRoomAt {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][double[]]$Point,
          [Parameter(Mandatory)][long]$LevelId, [Parameter(Mandatory)][string]$Tag)
    $made = Invoke-HzWrite -Run $Run -Tool 'horizun_create_elements' -Label "room-$Tag" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        elements = @(@{ kind = 'room'; level_id = $LevelId; point = @($Point[0], $Point[1], 0.0) }) }
    if ([int]$made.Apply.Result.created_verified -ne 1) {
        throw ("HARNESS: no room was placed at ({0}, {1})" -f $Point[0], $Point[1])
    }
    [long](@($made.Apply.Result.rows)[0].element_id)
}

# =============================================================================
# THE FIXTURE - four walls and one line across them
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

$walls = @(
    @{ x1 = $X;        y1 = 0.0;    x2 = ($X + $w); y2 = 0.0 },
    @{ x1 = ($X + $w); y1 = 0.0;    x2 = ($X + $w); y2 = $h },
    @{ x1 = ($X + $w); y1 = $h;     x2 = $X;        y2 = $h },
    @{ x1 = $X;        y1 = $h;     x2 = $X;        y2 = 0.0 })
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-walls' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @($walls | ForEach-Object {
        @{ kind = 'wall'; start = @([double]$_.x1, [double]$_.y1, 0.0)
           end = @([double]$_.x2, [double]$_.y2, 0.0)
           height = 3000.0; level_id = [long]$level.element_id } }) }
if ([int]$made.Apply.Result.created_verified -ne 4) {
    throw ("HARNESS: the enclosure needs four walls and Revit verified {0}" -f $made.Apply.Result.created_verified)
}

# THE DRAWN LINE. A grid, because it is the one element that puts a single
# straight line on a layer of its own in a plan export - and it is drawn SHORTER
# than the enclosure on purpose, so nothing about the reading depends on a grid's
# habit of running past what it measures.
$null = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-line' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'grid'; start = @(($X + 200.0), $splitY, 0.0)
                    end = @(($X + $w - 200.0), $splitY, 0.0) }) }

$viewName = "HZ_SEP_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$viewId = [long](@($view.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $viewId
                   box = @(($X - 2000.0), -2000.0, ($X + $w + 2000.0), ($h + 2000.0)) }) }
New-Item -ItemType Directory -Force -Path 'C:\hz-live\dwg' | Out-Null
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($viewId)
    output_path = (Join-Path 'C:\hz-live\dwg' ("HZ_SEP_$($run.RunId).dwg")) }
$dwgFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter "HZ_SEP_$($run.RunId)*.dwg")[0]
if ($null -eq $dwgFile) { throw 'HARNESS: the fixture exported no DWG' }
$run.Fixture['dwg_name'] = $dwgFile.Name
$run.Fixture['dwg_sha256'] = (Get-HzSha256 $dwgFile.FullName)

# ---------------------------------------------------------------- read it back
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $dwgFile.FullName -Label 'link'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units

$wallLayer = Get-HzWallLayer -Run $run -InstanceId $inst
# The midpoint of the drawn line: the layer search measures from segment
# midpoints, so this is where the line's own layer is findable.
$lineAt = @(($X + ($w / 2.0)), $splitY)
$lineLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $lineAt `
    -OtherPoints @(@(($X + ($w / 2.0)), 0.0), @($X, ($h / 2.0))) -RadiusMm 900.0 -Label 'layer-line'
$allLayers = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'layers' -Arguments @{
    mode = 'layers'; instance_id = $inst }).Result.layers) | ForEach-Object { [string]$_.layer })
Add-HzNote $run ("layers: {0}" -f ($allLayers -join ', '))
Add-HzNote $run ("chosen: wall='{0}' line='{1}'" -f $wallLayer, $lineLayer)

if (-not $lineLayer) {
    foreach ($id in @('P1', 'P2', 'P3', 'P4', 'P5', 'A1')) {
        Add-HzProbe -Run $run -Id $id -Name 'the drawn line needs a layer of its own in this drawing' `
            -Expected 'a layer exclusive to the line across the enclosure' `
            -Observed ("wall='{0}' line='{1}' all={2}" -f $wallLayer, $lineLayer, ($allLayers -join '|')) `
            -Status 'fixture_missing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

$wallSet = New-HzWallRequirementSet -Layer $wallLayer -Units $units
$wallPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-walls' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $wallSet
    level_id = [long]$level.element_id }
$builtWalls = Invoke-HzConversion -Run $run -Plan $wallPlan.Result -Set $wallSet -InstanceId $inst -Tag 'walls'
if ([int]$builtWalls.created_verified -lt 4) {
    throw ("HARNESS: the enclosure needs four converted walls and got {0}" -f $builtWalls.created_verified)
}
Add-HzNote $run ("the wall pass built {0} wall(s)" -f $builtWalls.created_verified)

function New-HzSeparatorSet {
    param([string]$Id, [string]$Layer, [string]$Units, [string]$Level)
    $rule = @{ id = $Id; precedence = 10; discipline = 'architecture'
               layers = @($Layer); produces = 'room_separator'
               category = 'OST_RoomSeparationLines'
               geometry = @{ from = 'single_lines'; min_length_mm = 3000.0 } }
    if ($Level) { $rule['level'] = $Level }
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = "hz-live-$Id"; version = '1.0.0'; title = "Live $Id" }
        source = @{ units = $Units }
        tolerances = @{ point_mm = 30.0; gap_mm = 30.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @($rule)
    }
}
$sepSet = New-HzSeparatorSet -Id 'separators' -Layer $lineLayer -Units $units -Level ([string]$level.name)

# =============================================================================
# P - THE ROOM, BEFORE AND AFTER
# =============================================================================
Write-Host "`n== P: does the line bound anything ==" -ForegroundColor Cyan

$beforeRoom = New-HzRoomAt -Run $run -Point $roomAt -LevelId ([long]$level.element_id) -Tag 'before'
$beforeSpan = Get-HzRoomSpanY -Run $run -RoomId $beforeRoom -Label 'span-before'
if ($null -eq $beforeSpan) { throw 'HARNESS: the model reports no extent for the room placed before the separator' }
Add-HzNote $run ("before: the room reaches y {0:N0} to {1:N0}" -f $beforeSpan.Min, $beforeSpan.Max)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_delete_verified' -Label 'room-remove' -Arguments @{
    target_document = $Document; mode = 'ids'; ids = @($beforeRoom) }

$sepPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-sep' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $sepSet }
$sepRows = @($sepPlan.Result.execute_plan_request.actions)
$sepRow = $(if ($sepRows.Count -gt 0) { @($sepRows[0].arguments.elements)[0] } else { $null })
$chain = @(@(Get-HzProp $sepRow 'profile')[0])

Add-HzProbe -Run $run -Id 'P1' -Name 'a drawn line reaches the plan as a room separator, as an OPEN chain' `
    -Expected 'kind=room_separator, one chain of two points, not a closed ring' `
    -Observed ("separators={0} kind={1} points={2}" -f (Get-HzKindCount $sepPlan.Result 'room_separator'),
        [string](Get-HzProp $sepRow 'kind'), $chain.Count) `
    -Ok ((Get-HzKindCount $sepPlan.Result 'room_separator') -eq 1 -and
         [string](Get-HzProp $sepRow 'kind') -eq 'room_separator' -and $chain.Count -eq 2) `
    -Evidence @{ row = $sepRow
                 note = 'a chain closed back to its start would bound a room the drawing does not show' }

$viewNamed = @(@($sepPlan.Result.apply_binding.resolved_names) |
               Where-Object { [string]$_.what -match '(?i)view' })
Add-HzProbe -Run $run -Id 'P5' -Name 'the plan takes the separator through a plan of ITS OWN storey, and records which' `
    -Expected 'a view in resolved_names, and a level_id on the row' `
    -Observed ("view_named={0} level_id={1}" -f $viewNamed.Count, [string](Get-HzProp $sepRow 'level_id')) `
    -Ok ($viewNamed.Count -ge 1 -and $null -ne (Get-HzProp $sepRow 'level_id')) `
    -Evidence @{ resolved = $viewNamed
                 note = 'this used to be whatever view was on screen, which is measured to take Revit down when the storey is not the one the separator sits on' }

$applied = Invoke-HzConversion -Run $run -Plan $sepPlan.Result -Set $sepSet -InstanceId $inst -Tag 'sep'
$separators = @(Get-HzElements -Run $run -Categories @('OST_RoomSeparationLines') -Label 'separators')

Add-HzProbe -Run $run -Id 'P2' -Name 'it is built and re-read as a room BOUNDARY, not as any line that looks like one' `
    -Expected '1 created and verified, and the model holds it under Room Separation Lines' `
    -Observed ("created={0} state={1} in_model={2}" -f $applied.created_verified,
        (Get-HzProp $applied 'state'), $separators.Count) `
    -Ok ([int]$applied.created_verified -eq 1 -and $separators.Count -ge 1) `
    -Evidence @{ state = $applied.state; stages = $applied.stages
                 note = 'a detail line along the same coordinates is identical in every view and bounds nothing' }

$afterRoom = New-HzRoomAt -Run $run -Point $roomAt -LevelId ([long]$level.element_id) -Tag 'after'
$afterSpan = Get-HzRoomSpanY -Run $run -RoomId $afterRoom -Label 'span-after'
if ($null -eq $afterSpan) { throw 'HARNESS: the model reports no extent for the room placed after the separator' }
Add-HzNote $run ("after: the room reaches y {0:N0} to {1:N0}" -f $afterSpan.Min, $afterSpan.Max)

# THE MEASUREMENT. The enclosure is 8 m deep and the line crosses it at 4 m, so a
# separator that bounds the room halves it. The tolerance is a wall's thickness,
# because a room stops at the face and not at the centreline.
$wasDeep = ($beforeSpan.Max - $beforeSpan.Min)
$nowDeep = ($afterSpan.Max - $afterSpan.Min)
Add-HzProbe -Run $run -Id 'P3' -Name 'and the ROOM CHANGES SHAPE - the same point, half the space' `
    -Expected ("the room reached about {0:N0} mm deep and now reaches about {1:N0}" -f $h, $splitY) `
    -Observed ("before={0:N0} after={1:N0} split_at={2:N0}" -f $wasDeep, $nowDeep, $splitY) `
    -Ok ([Math]::Abs($wasDeep - $h) -le 400.0 -and [Math]::Abs($nowDeep - $splitY) -le 400.0) `
    -Evidence @{ before = $beforeSpan; after = $afterSpan
                 note = 'this is the only question worth asking: a line that was created and bounds nothing passes every other check' }

# =============================================================================
# P4 - THE STOREY NOBODY NAMED
# =============================================================================
Write-Host "`n== P4: the storey ==" -ForegroundColor Cyan

$noLevel = New-HzSeparatorSet -Id 'separators-nolevel' -Layer $lineLayer -Units $units -Level $null
$homeless = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-sep-nolevel' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $noLevel }
$homelessText = $(if ($homeless.IsError) { [string]$homeless.Text } else { ($homeless.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'P4' -Name 'a separator with no storey named anywhere is REFUSED, not put on a default one' `
    -Expected 'level_unresolved - a 2D drawing does not carry a storey' `
    -Observed (Limit-HzText $homelessText 240) `
    -Ok ($homelessText -match 'level_unresolved') `
    -Evidence @{ reply = (Limit-HzText $homelessText 700)
                 note = 'a separator on the wrong storey bounds a room nobody meant, and looks correct in plan' }

# =============================================================================
# P6 - THE VIEW THAT TAKES REVIT DOWN
# =============================================================================
Write-Host "`n== P6: the wrong storey ==" -ForegroundColor Cyan

# A PLAN OF ANOTHER STOREY. Revit does not refuse this - the process goes away
# mid-transaction, no exception, no message, and the bridge sees a closed pipe.
# Measured on this fixture, which is why the command checks before Revit looks.
# A STOREY THIS RUN MAKES, and a plan of it. Hunting the document for one that
# happens to exist would make the probe depend on the fixture model rather than
# on the bridge.
$tag = $run.RunId.Substring($run.RunId.Length - 4)
$otherLevel = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'other-level' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'level'; elevation = 60000.0; name = "HZO-$tag" }) }
$otherLevelId = [long](@($otherLevel.Apply.Result.rows)[0].element_id)
$otherView = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'other-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = $otherLevelId
                   name = "HZ_SEP_OTHER_$($run.RunId)" }) }
$otherViewId = [long](@($otherView.Apply.Result.rows)[0].element_id)

$wrongView = Invoke-HzTool -Run $run -Tool 'horizun_create_elements' -Label 'sep-wrong-view' -Arguments @{
    target_document = $Document; units = 'mm'; dry_run = $true
    elements = @(@{ kind = 'room_separator'; level_id = [long]$level.element_id
                    view_id = $otherViewId
                    profile = @(, @(@(($X + 500.0), 6000.0, 0.0), @(($X + 5000.0), 6000.0, 0.0))) }) }
$wrongText = $(if ($wrongView.IsError) { [string]$wrongView.Text } else { ($wrongView.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'P6' -Name 'a separator pointed at a plan of ANOTHER storey is refused before Revit sees it' `
    -Expected 'separator_view_wrong_storey, naming both storeys, and nothing created' `
    -Observed (Limit-HzText $wrongText 240) `
    -Ok ($wrongText -match 'separator_view_wrong_storey') `
    -Evidence @{ reply = (Limit-HzText $wrongText 700); drawn_on = [string]$level.name; aimed_at = "HZO-$tag"
                 note = 'Revit does not refuse this one - it stops, so the refusal has to happen first' }

# =============================================================================
# A - THE AUDIT
# =============================================================================
Write-Host "`n== A: the audit ==" -ForegroundColor Cyan

$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $sepSet }
Add-HzProbe -Run $run -Id 'A1' -Name 'the audit finds the separator it just built, and reports nothing moved' `
    -Expected 'matched by revision, moved 0' `
    -Observed ("by_revision={0} moved={1} missing={2}" -f $audit.Result.matched.by_revision,
        (Get-HzCode $audit.Result 'moved'), (Get-HzCode $audit.Result 'missing')) `
    -Ok ([int]$audit.Result.matched.by_revision -ge 1 -and (Get-HzCode $audit.Result 'moved') -eq 0) `
    -Evidence @{ matched = $audit.Result.matched; counts = $audit.Result.counts_by_code }

# =============================================================================
# M - THE MAPPING ASSISTANT
# =============================================================================
Write-Host "`n== M: what would read this drawing ==" -ForegroundColor Cyan

# THIS DRAWING CARRIES TWO DIFFERENT READINGS - walls as pairs of parallel lines,
# and one line that is a line - so it is the one that can tell whether the
# profiler ranks by structure or merely by count.
$profile = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'profile' -Arguments @{
    mode = 'profile'; instance_id = $inst; max_layers = 20 }
$profiled = @(Get-HzProp $profile.Result 'layers')
$wallRow = @($profiled | Where-Object { [string](Get-HzProp $_ 'layer') -eq $wallLayer })
$lineRow = @($profiled | Where-Object { [string](Get-HzProp $_ 'layer') -eq $lineLayer })
$wallBest = $(if ($wallRow.Count -ge 1) { [string](Get-HzPath $wallRow[0] 'best_reading', 'from') } else { '(none)' })
$lineBest = $(if ($lineRow.Count -ge 1) { [string](Get-HzPath $lineRow[0] 'best_reading', 'from') } else { '(none)' })

Add-HzProbe -Run $run -Id 'M1' -Name 'the profiler reads each layer as what it IS, not as whatever claims the most pieces' `
    -Expected 'the wall layer as double_lines, the drawn line as single_lines' `
    -Observed ("layers={0} wall='{1}'->{2} line='{3}'->{4}" -f $profiled.Count, $wallLayer, $wallBest,
        $lineLayer, $lineBest) `
    -Ok ($wallBest -eq 'double_lines' -and $lineBest -eq 'single_lines') `
    -Evidence @{ wall = $(if ($wallRow.Count -ge 1) { Get-HzProp $wallRow[0] 'best_reading' } else { $null })
                 line = $(if ($lineRow.Count -ge 1) { Get-HzProp $lineRow[0] 'best_reading' } else { $null })
                 note = 'single_lines reads every segment as its own candidate, so a ranking by count picks it everywhere' }

$measured = Get-HzPath $wallRow[0] 'best_reading', 'thickness_mm'
Add-HzProbe -Run $run -Id 'M2' -Name 'and it reports the thickness it MEASURED, so a band written from it cannot exclude these walls' `
    -Expected 'a thickness range around the 200 mm these walls are' `
    -Observed ("min={0} max={1} measured_on={2}" -f (Get-HzProp $measured 'min'), (Get-HzProp $measured 'max'),
        (Get-HzProp $measured 'measured_on')) `
    -Ok ($null -ne $measured -and [double](Get-HzProp $measured 'min') -gt 0) `
    -Evidence @{ thickness_mm = $measured }

# WHAT IT WILL NOT SAY. Every produces is null, and the skeleton it hands back is
# REFUSED by the loader until a person decides - which is the whole point: the
# drawing says where the geometry is and not what the building is.
$skeleton = Get-HzProp $profile.Result 'requirement_set_skeleton'
$skeletonRules = @(Get-HzProp $skeleton 'rules')
$named = @($skeletonRules | Where-Object { $null -ne (Get-HzProp $_ 'produces') })
Add-HzProbe -Run $run -Id 'M3' -Name 'it says NOTHING about what a layer means - every produces is left for a person' `
    -Expected 'rules >= 2, and none of them naming what it produces' `
    -Observed ("rules={0} with_produces={1}" -f $skeletonRules.Count, $named.Count) `
    -Ok ($skeletonRules.Count -ge 2 -and $named.Count -eq 0) `
    -Evidence @{ refuses_to_say = (Get-HzProp $profile.Result 'refuses_to_say')
                 you_must_supply = (Get-HzProp $profile.Result 'you_must_supply') }

$asIs = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-skeleton' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $skeleton
    level_id = [long]$level.element_id }
$asIsText = $(if ($asIs.IsError) { [string]$asIs.Text } else { ($asIs.Result | ConvertTo-Json -Depth 8 -Compress) })
Add-HzProbe -Run $run -Id 'M4' -Name 'and the skeleton it hands back does not convert anything until somebody fills it in' `
    -Expected 'refused - a rule must say what it produces' `
    -Observed (Limit-HzText $asIsText 220) `
    -Ok ($asIs.IsError -and $asIsText -match 'must say what it produces') `
    -Evidence @{ reply = (Limit-HzText $asIsText 600)
                 note = 'a skeleton that happened to load would be one somebody could apply without ever deciding what the layers are' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
