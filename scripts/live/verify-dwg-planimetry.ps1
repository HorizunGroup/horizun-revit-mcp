#Requires -Version 5.1
<#
  THE DRAWINGS THE CONVERTED MODEL PRODUCES, AUDITED IN THE MODEL.

  A DWG-to-BIM conversion is not finished when the walls exist. What the project
  actually owes is drawings, and the whole point of building a model rather than
  redrawing lines is that the drawings come OUT of it.

  So this takes the conversion the rest of these harnesses prove, and goes the
  rest of the way: rooms out of the drawing, views out of the rooms, sheets out
  of the views - and then AUDITS the result against a requirement set.

  THE AUDIT IS IN THE MODEL, NEVER THROUGH A PDF. A PDF is a picture of a
  drawing; it cannot say which view is on which sheet, what scale it is at, what
  template it uses, or whether two viewports overlap. Reading one back would be
  measuring the export rather than the model, and every finding would be a
  finding about the printer.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-planimetry' -Document $Document
$X = 900000.0

function Get-HzCode {
    param($Result, [string]$Path, [string]$Code)
    $c = Get-HzPath $Result $Path, $Code
    if ($null -eq $c) { 0 } else { [int]$c }
}

# =============================================================================
# THE CONVERSION this planimetry comes out of
# =============================================================================
Write-Host "`n== the converted model ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

# A drawing with two enclosed rooms, exported from Revit as every other fixture
# here is - the point is that the ROOMS come from the drawing, not from a model
# somebody built by hand for the occasion.
$rooms = @(
    @{ x0 = $X;            y0 = 0.0;    x1 = ($X + 6000.0); y1 = 5000.0 },
    @{ x0 = ($X + 8000.0); y0 = 0.0;    x1 = ($X + 14000.0); y1 = 5000.0 }
)
# WALLS AND A ROOM BOUNDARY, on layers of their own.
#
# A room does NOT come from the wall lines. MEASURED: wall faces export as two
# concentric rings per room - an outer and an inner - and the reading correctly
# nests them, so what comes back is a doughnut, which is the WALL. That is the
# right answer to the wrong question; every candidate was held for review, and
# rightly.
#
# A real drawing set says where a room is on a layer that means room. Here that
# is a slab per room, which exports one clean closed ring on its own layer, and
# the two layers are then told apart by measuring the drawing rather than by
# assuming what Revit will name them.
$elements = @()
foreach ($r in $rooms) {
    foreach ($seg in @(@($r.x0, $r.y0, $r.x1, $r.y0), @($r.x1, $r.y0, $r.x1, $r.y1),
                       @($r.x1, $r.y1, $r.x0, $r.y1), @($r.x0, $r.y1, $r.x0, $r.y0))) {
        $elements += @{ kind = 'wall'; start = @($seg[0], $seg[1], 0.0); end = @($seg[2], $seg[3], 0.0)
                        height = 3000.0 }
    }
    # INSET FROM THE WALLS. A boundary drawn ON the wall centrelines shares
    # every millimetre with them, and no measurement can then tell the two
    # layers apart - which is what happened the first time this ran.
    $i = 600.0
    $elements += @{ kind = 'floor'
                    profile = @(, @(@(($r.x0 + $i), ($r.y0 + $i), 0.0), @(($r.x1 - $i), ($r.y0 + $i), 0.0),
                                    @(($r.x1 - $i), ($r.y1 - $i), 0.0), @(($r.x0 + $i), ($r.y1 - $i), 0.0))) }
}

$level = Get-HzFirstLevel $run
$rows = @()
foreach ($e in $elements) {
    $row = @{}
    foreach ($k in $e.Keys) { $row[$k] = $e[$k] }
    $row['level_id'] = [long]$level.element_id
    $rows += $row
}
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-plani' -Arguments @{
    target_document = $Document; units = 'mm'; elements = $rows }
if ([int]$made.Apply.Result.created_verified -ne $rows.Count) {
    throw ("HARNESS: the fixture wanted {0} elements and Revit verified {1}" -f
        $rows.Count, $made.Apply.Result.created_verified)
}
$viewName = "HZ_PLANI_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$fxView = [long](@($view.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $fxView
                   box = @(($X - 2000.0), -2000.0, ($X + 16000.0), 7000.0) }) }
$dwgPath = Join-Path 'C:\hz-live\dwg' ("HZ_PLANI_$($run.RunId).dwg")
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($fxView); output_path = $dwgPath }
$dwgFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_PLANI_$($run.RunId)*.dwg"))[0]
if ($null -eq $dwgFile) { throw 'HARNESS: the planimetry fixture exported no DWG' }
$fixture = [ordered]@{ dwg_path = $dwgFile.FullName; dwg_name = $dwgFile.Name
                       dwg_sha256 = (Get-HzSha256 $dwgFile.FullName) }
foreach ($k in $fixture.Keys) { $run.Fixture[$k] = $fixture[$k] }
$run.Expected['rooms_drawn'] = 2
Add-HzNote $run ("fixture {0}" -f $fixture.dwg_name)

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link-plan'
$layer = Get-HzWallLayer -Run $run -InstanceId $inst
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units

# WHICH LAYER CARRIES THE ROOM BOUNDARY, measured by difference. The slab draws
# only its OUTLINE, so it is sampled on an edge - the middle of a floor is empty.
$roomEdgeAt = @(($rooms[0].x0 + 3000.0), ($rooms[0].y1 - 600.0))
$midWallAt = @(($rooms[0].x0 + 3000.0), $rooms[0].y1)
$roomLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $roomEdgeAt `
    -OtherPoints @(, $midWallAt) -RadiusMm 250 -Label 'layer-room'
if (-not $roomLayer) { throw 'HARNESS: the fixture drew no room boundary on a layer of its own' }
Add-HzNote $run ("layers: wall='{0}' room boundary='{1}'" -f $layer, $roomLayer)

$wallSet = New-HzWallRequirementSet -Layer $layer -Units $units -Id 'hz-live-plani-walls'
$wallPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-walls' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $wallSet
    level_id = [long]$level.element_id }

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

$builtWalls = Invoke-HzConversion -Run $run -Plan $wallPlan.Result -Set $wallSet -InstanceId $inst -Tag 'walls'
if ([int]$builtWalls.created_verified -lt 8) {
    throw ("HARNESS: the conversion built {0} walls; two enclosed rooms need eight" -f
        $builtWalls.created_verified)
}
Add-HzNote $run ("converted {0} wall(s) from the drawing" -f $builtWalls.created_verified)

# THE ROOMS, from the same drawing. Revit places a room by a point that must be
# inside the enclosure, which is what the interpretation's interior point is for.
$roomSet = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-plani-rooms'; version = '1.0.0'; title = 'Rooms' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 250.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'rooms'; precedence = 10; discipline = 'architecture'
                 layers = @($roomLayer); produces = 'room'; category = 'OST_Rooms'
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 5000000.0 } })
}
$roomPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-rooms' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $roomSet
    level_id = [long]$level.element_id }
$builtRooms = Invoke-HzConversion -Run $run -Plan $roomPlan.Result -Set $roomSet -InstanceId $inst -Tag 'rooms'

Add-HzProbe -Run $run -Id 'P1' -Name 'the ROOMS come out of the drawing, not out of a model built for the occasion' `
    -Expected '2 rooms created and verified from the same DWG the walls came from' `
    -Observed ("created={0} state={1}" -f $builtRooms.created_verified, $builtRooms.state) `
    -Ok ([int]$builtRooms.created_verified -eq 2) `
    -Evidence @{ state = $builtRooms.state; drawing = $fixture.dwg_name }

$roomRows = @(Get-HzElements -Run $run -Categories @('OST_Rooms') -Label 'rooms-built')
if ($roomRows.Count -lt 2) { throw "HARNESS: the model has $($roomRows.Count) rooms; the views need two" }

# =============================================================================
# V - THE VIEWS, derived from the rooms
# =============================================================================
Write-Host "`n== V: views out of the rooms ==" -ForegroundColor Cyan

# THE ANCHOR MUST BE A PLAN THE CROPS CAN ACTUALLY BE SET ON.
#
# MEASURED: anchoring in a view governed by a SCOPE BOX rolls the whole batch
# back, correctly - Revit takes the crop from the scope box and the written crop
# does not verify. So the anchor is chosen by measurement: a floor plan of the
# rooms' own level, with no scope box governing it.
$planViews = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_planimetry' -Label 'q-views' -Arguments @{
    mode = 'views'; max_rows = 500 }).Result.rows |
    Where-Object { [string]$_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true -and
                   $null -eq (Get-HzProp $_ 'scope_box_id') -and
                   [long](Get-HzProp $_ 'level_id') -eq [long]$level.element_id })
if ($planViews.Count -eq 0) {
    throw ('HARNESS: no floor plan on the rooms level without a scope box - a crop written into a ' +
           'scope-boxed view does not verify, and the batch rolls back')
}
$anchor = [long]$planViews[0].view_id
Add-HzNote $run ("anchored in view {0} ('{1}'), no scope box" -f $anchor, [string]$planViews[0].name)

$viewsBefore = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_planimetry' -Label 'v-before' -Arguments @{
    mode = 'views'; max_rows = 500 }).Result.rows).Count

# horizun_plan_views is a PLANNER: it changes nothing and hands its work to
# horizun_manage_views, which rehearses it and writes under a token. Stopping at
# the planner and counting views would have measured the plan, not the model -
# which is exactly what happened the first time this ran.
$planned = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_views' -Label 'room-views-plan' -Arguments @{
    target_document = $Document; operation = 'room_views'; plan_view_id = $anchor
    room_ids = @($roomRows | ForEach-Object { [long]$_.element_id })
    kinds = @('plan'); units = 'mm' }
if ([int]$planned.Result.rooms_planned -lt $roomRows.Count) {
    throw ("HARNESS: the planner covered {0} of {1} rooms" -f
        $planned.Result.rooms_planned, $roomRows.Count)
}
$next = $planned.Result.next_arguments | ConvertTo-Json -Depth 32 | ConvertFrom-Json
$nextHash = @{}
foreach ($prop in $next.PSObject.Properties) { $nextHash[$prop.Name] = $prop.Value }
$nextHash['target_document'] = $Document
$roomViews = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'room-views' -Arguments $nextHash

$viewsAfter = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_planimetry' -Label 'v-after' -Arguments @{
    mode = 'views'; max_rows = 500 }).Result.rows).Count

Add-HzProbe -Run $run -Id 'P2' -Name 'a view per room is DERIVED from the model and re-read from it' `
    -Expected 'the view count rises by one per room, measured from the model afterwards' `
    -Observed ("before={0} after={1} rooms={2} planned={3}" -f $viewsBefore, $viewsAfter,
        $roomRows.Count, $planned.Result.actions_planned) `
    -Ok (($viewsAfter - $viewsBefore) -ge $roomRows.Count) `
    -Evidence @{ created = ($viewsAfter - $viewsBefore); rooms = $roomRows.Count
                 planner_said = $planned.Result.actions_planned }

# =============================================================================
# A - THE AUDIT, in the model
# =============================================================================
Write-Host "`n== A: audited in the model ==" -ForegroundColor Cyan

$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_planimetry' -Label 'audit-plani' -Arguments @{
    target_document = $Document; scope = 'model' }

$catalog = @(Get-HzProp $audit.Result 'check_catalog')
if ($catalog.Count -eq 0) { $catalog = @(Get-HzPath $audit.Result 'catalog', 'checks') }
Add-HzProbe -Run $run -Id 'P3' -Name 'the audit publishes the checks it CAN run, so a caller can see what was not asked' `
    -Expected 'a catalog of universal checks travels with the reply' `
    -Observed ("catalog_entries={0}" -f $catalog.Count) `
    -Ok ($catalog.Count -ge 1) `
    -Evidence @{ catalog = ($catalog | Select-Object -First 12) }

# A CHECK WITH NO POPULATION MUST NOT REPORT "PASSED". This model has no sheets
# at all, and a sheet check that answered "passed" on nothing would be the exact
# false clean this bridge exists to refuse.
$sheetAudit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_planimetry' -Label 'audit-sheets' -Arguments @{
    target_document = $Document; scope = 'sheets' }
$sheetJson = ($sheetAudit.Result | ConvertTo-Json -Depth 12 -Compress)
Add-HzProbe -Run $run -Id 'P4' -Name 'a check with NO population answers not_applicable, never passed' `
    -Expected 'not_applicable somewhere in the sheet-scope reply of a model with no sheets' `
    -Observed (Limit-HzText $sheetJson 200) `
    -Ok ($sheetJson -match 'not_applicable') `
    -Evidence @{ note = 'passing on an empty population is how a report says clean about nothing' }

# A REFUSAL: a check id that does not exist. Running nothing under a misspelt
# name would report a clean model, which is worse than an error.
$typo = Invoke-HzTool -Run $run -Tool 'horizun_audit_planimetry' -Label 'audit-typo' -Arguments @{
    target_document = $Document; scope = 'model'; checks = @('no_such_check_at_all') }
$typoText = $typo.Text
if (-not $typo.IsError) { $typoText = ($typo.Result | ConvertTo-Json -Depth 12 -Compress) }
Add-HzProbe -Run $run -Id 'P5' -Name 'a misspelt check id is REFUSED rather than quietly running nothing' `
    -Expected 'the reply names the unknown check instead of reporting a clean model' `
    -Observed (Limit-HzText $typoText 200) `
    -Ok ($typoText -match 'no_such_check_at_all') `
    -Evidence @{ reply = (Limit-HzText $typoText 500) }

# AND NEVER THROUGH A PDF. The audit answers about views and sheets in the
# model; nothing here exports one, and this probe records that the reply is
# about model entities rather than about a picture of them.
$inventory = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_planimetry' -Label 'q-inventory' -Arguments @{
    mode = 'inventory' }
Add-HzProbe -Run $run -Id 'P6' -Name 'the planimetry is read from the MODEL - view ids and types, not a rendered page' `
    -Expected 'an inventory of model entities, each with an id' `
    -Observed ("views={0} sheets={1}" -f (Get-HzPath $inventory.Result 'collected', 'views'),
        (Get-HzPath $inventory.Result 'collected', 'sheets')) `
    -Ok ($null -ne (Get-HzPath $inventory.Result 'collected', 'views')) `
    -Evidence @{ inventory = $inventory.Result
                 note = 'a PDF cannot say which view is on which sheet, at what scale, under which template' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
