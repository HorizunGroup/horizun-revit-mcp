#Requires -Version 5.1
<#
  NAMES A DRAWING CANNOT SUPPLY, PROVED LIVE.

  MEASURED on Revit 2026: no string is reachable from imported DWG geometry at
  any depth. Text arrives as curves on its own layer - the layer name survives,
  the words do not - so a grid bubble reading "A" is, to this bridge, a few arcs.

  Every name therefore comes from the requirement set, and this proves the whole
  path: the set says it, the plan assigns it and says what it was earned ON, the
  apply writes it inside the creating transaction, and the model is re-read to
  confirm it took.

  The refusals matter more than the successes here, because the tempting
  alternative - "the first line is grid 1" - orders by whatever the reading
  returned first, and a grid named that way puts the wrong reference on every
  dimension drawn from it with nothing in the model saying so.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-naming' -Document $Document
$X = 900000.0

function Get-HzReplyText {
    param($Call)
    if ($Call.IsError) { return [string]$Call.Text }
    ($Call.Result | ConvertTo-Json -Depth 20 -Compress)
}

function Get-HzKindCount {
    param($Plan, [string]$Kind)
    $c = Get-HzPath $Plan 'counts_by_kind', $Kind
    if ($null -eq $c) { 0 } else { [int]$c }
}

function Get-HzClass {
    param($Update, [string]$Name)
    $c = Get-HzPath $Update 'counts_by_classification', $Name
    if ($null -eq $c) { -1 } else { [int]$c }
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
  Did any created row re-read with the identity it was asked for?
#>
function Get-HzIdentityVerified {
    param($Applied, [string]$Field = 'name_verified')
    foreach ($stage in @(Get-HzProp $Applied 'stages')) {
        foreach ($row in @(Get-HzProp $stage 'rows')) {
            $id = Get-HzProp $row 'identity_verified'
            if ($id -and (Get-HzProp $id $Field) -eq $true) { return $true }
        }
    }
    $false
}

# =============================================================================
# THE FIXTURE - four grid lines and two rooms, exported from Revit
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

# Four grids at known, WELL-SEPARATED x, so an ordered naming has an
# unambiguous answer - and a fifth thing on its own layer to be the rooms.
$gridXs = @(($X + 0.0), ($X + 6000.0), ($X + 12000.0), ($X + 18000.0))
$roomBoxes = @(
    @{ x0 = ($X + 1000.0); y0 = 12000.0; x1 = ($X + 6000.0); y1 = 17000.0 },
    @{ x0 = ($X + 9000.0); y0 = 12000.0; x1 = ($X + 14000.0); y1 = 17000.0 }
)

$elements = @()
foreach ($gx in $gridXs) {
    $elements += @{ kind = 'grid'; start = @($gx, -1000.0, 0.0); end = @($gx, 9000.0, 0.0) }
}
foreach ($r in $roomBoxes) {
    $elements += @{ kind = 'floor'
                    profile = @(, @(@($r.x0, $r.y0, 0.0), @($r.x1, $r.y0, 0.0),
                                    @($r.x1, $r.y1, 0.0), @($r.x0, $r.y1, 0.0))) }
}

$rows = @()
foreach ($e in $elements) {
    $row = @{}
    foreach ($k in $e.Keys) { $row[$k] = $e[$k] }
    $row['level_id'] = [long]$level.element_id
    $rows += $row
}
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-naming' -Arguments @{
    target_document = $Document; units = 'mm'; elements = $rows }
if ([int]$made.Apply.Result.created_verified -ne $rows.Count) {
    throw ("HARNESS: the fixture wanted {0} elements and Revit verified {1}" -f
        $rows.Count, $made.Apply.Result.created_verified)
}

$viewName = "HZ_NAME_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$fxView = [long](@($view.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $fxView
                   box = @(($X - 3000.0), -3000.0, ($X + 21000.0), 20000.0) }) }
$dwgPath = Join-Path 'C:\hz-live\dwg' ("HZ_NAME_$($run.RunId).dwg")
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($fxView); output_path = $dwgPath }
$dwgFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_NAME_$($run.RunId)*.dwg"))[0]
if ($null -eq $dwgFile) { throw 'HARNESS: the naming fixture exported no DWG' }
$run.Fixture['dwg_name'] = $dwgFile.Name
$run.Fixture['dwg_sha256'] = (Get-HzSha256 $dwgFile.FullName)
$run.Expected['grids_drawn'] = $gridXs.Count
$run.Expected['rooms_drawn'] = $roomBoxes.Count
Add-HzNote $run ("fixture {0}" -f $dwgFile.Name)

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $dwgFile.FullName -Label 'link-naming'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units

# WHICH LAYER IS WHICH, measured. Grids and slabs export on layers Revit names
# from its own configuration.
$gridLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point @($gridXs[0], 4000.0) `
    -OtherPoints @(, @(($X + 3000.0), 12000.0)) -RadiusMm 900 -Label 'layer-grid'
$roomLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst `
    -Point @(($X + 3000.0), 12000.0) -OtherPoints @(, @($gridXs[0], 4000.0)) -RadiusMm 900 -Label 'layer-room'
if (-not $gridLayer -or -not $roomLayer) {
    throw ("HARNESS: could not tell the grid and room layers apart (grid='{0}' room='{1}')" -f
        $gridLayer, $roomLayer)
}
Add-HzNote $run ("layers: grid='{0}' room boundary='{1}'" -f $gridLayer, $roomLayer)

function New-HzGridSet {
    param([hashtable]$Naming, [string]$Id = 'hz-live-grid-naming')
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'Grids named from the set' }
        source = @{ units = $units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @(@{ id = 'grids'; precedence = 10; discipline = 'structure'
                     layers = @($gridLayer); produces = 'grid'; category = 'OST_Grids'
                     naming = $Naming
                     geometry = @{ from = 'single_lines'; min_length_mm = 1000.0 } })
    }
}

# =============================================================================
# G - GRIDS NAMED BY POSITION IN A DECLARED ORDER
# =============================================================================
Write-Host "`n== G: grids ==" -ForegroundColor Cyan

# NAMES THIS MODEL DOES NOT ALREADY HAVE.
#
# MEASURED: the fixture document is a real model and already holds grids called
# 2, 3 and 4, so a set asking for those is refused - correctly, and before
# anything is built. The names are therefore made unique to this run, which is
# also what stops two runs of this harness colliding with each other.
$gridNames = @(1, 2, 3, 4 | ForEach-Object { "HZ$($run.RunId.Substring($run.RunId.Length - 4))-$_" })
$run.Expected['grid_names'] = $gridNames
$ordered = New-HzGridSet @{ strategy = 'ordered'; axis = 'x'; direction = 'ascending'
                            values = $gridNames }
$planG = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-grids' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $ordered }
$gridActions = @($planG.Result.execute_plan_request.actions)
$gridRows = @()
foreach ($a in $gridActions) { $gridRows += @($a.arguments.elements) }
$namesInPlan = @($gridRows | ForEach-Object { [string](Get-HzProp $_ 'name') } | Where-Object { $_ })

Add-HzProbe -Run $run -Id 'G1' -Name 'the plan gives each grid the name its POSITION earns, from the set' `
    -Expected '4 grids named 1..4 in x order' `
    -Observed ("grids={0} names={1}" -f (Get-HzKindCount $planG.Result 'grid'), ($namesInPlan -join ',')) `
    -Ok ((Get-HzKindCount $planG.Result 'grid') -eq 4 -and $namesInPlan.Count -eq 4) `
    -Evidence @{ naming = (Get-HzProp $planG.Result 'naming') }

$namedOn = @(@(Get-HzPath $planG.Result 'naming', 'grids', 'names') |
             ForEach-Object { Get-HzProp $_ 'named_on' } | Where-Object { $_ })
Add-HzProbe -Run $run -Id 'G2' -Name 'and it says what each name was earned ON, so a reviewer need not re-derive it' `
    -Expected 'every assignment carries named_on naming the axis, direction and position' `
    -Observed (Limit-HzText ($namedOn -join ' | ') 200) `
    -Ok ($namedOn.Count -eq 4 -and ($namedOn -join ' ') -match 'along x ascending') `
    -Evidence @{ named_on = $namedOn }

$gridsBefore = @(Get-HzElements -Run $run -Categories @('OST_Grids') -Label 'grids-before').Count
$appliedG = Invoke-HzConversion -Run $run -Plan $planG.Result -Set $ordered -InstanceId $inst -Tag 'grids'
$gridsAfterRows = @(Get-HzElements -Run $run -Categories @('OST_Grids') -Label 'grids-after')
$builtNames = @($gridsAfterRows | ForEach-Object { [string]$_.name } | Sort-Object)

Add-HzProbe -Run $run -Id 'G3' -Name 'the grids are BUILT with those names, and the name is re-read after the commit' `
    -Expected '4 created, identity_verified, and the model holding 1 2 3 4' `
    -Observed ("created={0} name_verified={1} model_names={2}" -f $appliedG.created_verified,
        (Get-HzIdentityVerified $appliedG), ((@($builtNames | Where-Object { $gridNames -contains $_ })) -join ',')) `
    -Ok ([int]$appliedG.created_verified -eq 4 -and (Get-HzIdentityVerified $appliedG) -and
         @($builtNames | Where-Object { $gridNames -contains $_ }).Count -eq 4) `
    -Evidence @{ state = $appliedG.state; model_names = $builtNames }

# THE REFUSALS. Each is a way a name could have been invented.
$short = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-short' -Arguments @{
    target_document = $Document; instance_id = $inst
    requirement_set = (New-HzGridSet @{ strategy = 'ordered'; axis = 'x'
                                        values = @($gridNames[0], $gridNames[1]) } 'hz-short') }
Add-HzProbe -Run $run -Id 'G4' -Name 'four grids and two names names NOTHING, rather than shifting every name after the gap' `
    -Expected 'naming_unresolved naming both counts' `
    -Observed (Limit-HzText (Get-HzReplyText $short) 200) `
    -Ok ((Get-HzReplyText $short) -match 'naming_unresolved') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $short) 500) }

$noAxis = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-no-axis' -Arguments @{
    target_document = $Document; instance_id = $inst
    requirement_set = (New-HzGridSet @{ strategy = 'ordered'; values = $gridNames } 'hz-noaxis') }
Add-HzProbe -Run $run -Id 'G5' -Name 'an ordered naming with NO AXIS is refused - ordering without one is ordering by luck' `
    -Expected 'the set is refused, naming the axis options' `
    -Observed (Limit-HzText (Get-HzReplyText $noAxis) 200) `
    -Ok ((Get-HzReplyText $noAxis) -match 'axis') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $noAxis) 400) }

# A NAME THE MODEL ALREADY HOLDS. The grids are built now, so asking for the
# same names again is exactly the collision Revit refuses at creation.
$again = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-collide' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $ordered }
Add-HzProbe -Run $run -Id 'G6' -Name 'a name the MODEL already holds is refused BEFORE half the batch is built' `
    -Expected 'refused, naming the collision - Revit refuses a duplicate grid name at creation' `
    -Observed (Limit-HzText (Get-HzReplyText $again) 200) `
    -Ok ((Get-HzReplyText $again) -match 'already holds something called') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $again) 500)
                 note = 'discovering this at creation takes the batch down after building part of it' }

# =============================================================================
# R - ROOMS NAMED AND NUMBERED
# =============================================================================
Write-Host "`n== R: rooms ==" -ForegroundColor Cyan

$roomSet = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-room-naming'; version = '1.0.0'; title = 'Rooms named from the set' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'rooms'; precedence = 10; discipline = 'architecture'
                 layers = @($roomLayer); produces = 'room'; category = 'OST_Rooms'
                 naming = @{ strategy = 'by_position'
                             by_position = @(
                                 @{ x_mm = ($X + 3500.0); y_mm = 14500.0; tolerance_mm = 1500.0
                                    name = 'Office'; number = '101' },
                                 @{ x_mm = ($X + 11500.0); y_mm = 14500.0; tolerance_mm = 1500.0
                                    name = 'Store'; number = '102' }) }
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 5000000.0 } })
}
$planR = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-rooms' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $roomSet
    level_id = [long]$level.element_id }
$roomRows = @()
foreach ($a in @($planR.Result.execute_plan_request.actions)) { $roomRows += @($a.arguments.elements) }

Add-HzProbe -Run $run -Id 'R1' -Name 'a room carries BOTH a name and a number, neither of which the drawing has' `
    -Expected '2 rooms, each with name and number from the declared positions' `
    -Observed ("rooms={0} names={1} numbers={2}" -f (Get-HzKindCount $planR.Result 'room'),
        ((@($roomRows | ForEach-Object { [string](Get-HzProp $_ 'name') } | Where-Object { $_ })) -join ','),
        ((@($roomRows | ForEach-Object { [string](Get-HzProp $_ 'number') } | Where-Object { $_ })) -join ',')) `
    -Ok ((Get-HzKindCount $planR.Result 'room') -eq 2 -and
         @($roomRows | Where-Object { (Get-HzProp $_ 'name') -and (Get-HzProp $_ 'number') }).Count -eq 2) `
    -Evidence @{ rows = $roomRows }

$appliedR = Invoke-HzConversion -Run $run -Plan $planR.Result -Set $roomSet -InstanceId $inst -Tag 'rooms'
$roomsNow = @(Get-HzElements -Run $run -Categories @('OST_Rooms') -Label 'rooms-after')

Add-HzProbe -Run $run -Id 'R2' -Name 'the rooms are built NAMED AND NUMBERED, and both are re-read after the commit' `
    -Expected '2 created, name and number verified off the commit' `
    -Observed ("created={0} name_verified={1} number_verified={2} rooms={3}" -f $appliedR.created_verified,
        (Get-HzIdentityVerified $appliedR 'name_verified'),
        (Get-HzIdentityVerified $appliedR 'number_verified'), $roomsNow.Count) `
    -Ok ([int]$appliedR.created_verified -eq 2 -and
         (Get-HzIdentityVerified $appliedR 'name_verified') -and
         (Get-HzIdentityVerified $appliedR 'number_verified')) `
    -Evidence @{ state = $appliedR.state; stages = $appliedR.stages
                 note = 'Revit assigns a room number the instant one is placed, so a room nobody numbered still HAS one' }

# =============================================================================
# A - THE AUDIT SEES A NAME SOMEBODY CHANGED
# =============================================================================
Write-Host "`n== A: the audit ==" -ForegroundColor Cyan

$auditArgs = @{ target_document = $Document; instance_id = $inst; requirement_set = $roomSet }
$clean = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-clean' -Arguments $auditArgs
Add-HzProbe -Run $run -Id 'A1' -Name 'a model built from the set AGREES with it about every name' `
    -Expected 'room_name_differs 0 and room_number_differs 0' `
    -Observed ("name_differs={0} number_differs={1}" -f (Get-HzCode $clean.Result 'room_name_differs'),
        (Get-HzCode $clean.Result 'room_number_differs')) `
    -Ok ((Get-HzCode $clean.Result 'room_name_differs') -eq 0 -and
         (Get-HzCode $clean.Result 'room_number_differs') -eq 0) `
    -Evidence @{ counts = $clean.Result.counts_by_code }

# A PERSON RENAMES A ROOM. The drawing did not change and the set did not
# change, so the model and the set now disagree about something only the set
# could ever have said.
if ($roomsNow.Count -lt 1) { throw 'HARNESS: no room to rename' }
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'person-renames' -Arguments @{
    target_document = $Document
    writes = @(@{ target_id = [long]$roomsNow[0].element_id; parameter = 'ROOM_NAME'; value = 'Somewhere Else' }) }

$dirty = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-renamed' -Arguments $auditArgs
Add-HzProbe -Run $run -Id 'A2' -Name 'a room renamed BY HAND is reported, because the set is the only place a name came from' `
    -Expected 'room_name_differs 1, naming both sides' `
    -Observed ("name_differs={0}" -f (Get-HzCode $dirty.Result 'room_name_differs')) `
    -Ok ((Get-HzCode $dirty.Result 'room_name_differs') -ge 1) `
    -Evidence @{ findings = @(@(Get-HzProp $dirty.Result 'findings') |
                              Where-Object { (Get-HzProp $_ 'code') -eq 'room_name_differs' } |
                              Select-Object -First 2) }

Add-HzProbe -Run $run -Id 'A3' -Name 'the new codes are in the published vocabulary, with their zeros' `
    -Expected 'grid_name_differs, room_name_differs, room_number_differs all present as counts' `
    -Observed ("vocabulary={0}" -f @(Get-HzProp $dirty.Result 'finding_vocabulary').Count) `
    -Ok (@('grid_name_differs', 'room_name_differs', 'room_number_differs') |
         ForEach-Object { $null -ne (Get-HzPath $dirty.Result 'counts_by_code', $_) } |
         Where-Object { -not $_ } | Measure-Object | ForEach-Object { $_.Count -eq 0 }) `
    -Evidence @{ vocabulary = (Get-HzProp $dirty.Result 'finding_vocabulary') }

# =============================================================================
# I - AND THE INCREMENTAL SEES IT
# =============================================================================
Write-Host "`n== I: the next run ==" -ForegroundColor Cyan

# THE SAME EDIT, THROUGH THE OTHER COMMAND. The audit answers "does the model
# match the set"; the update answers "what should happen next". Both have to see
# a renamed room, and the update saw NOTHING until this was measured - a name
# moves no line, so a comparison of geometry reports the model as unchanged.
$updated = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-renamed' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $roomSet
    level_id = [long]$level.element_id }
$divergedRooms = @(@(Get-HzProp $updated.Result 'plan') |
                   Where-Object { (Get-HzProp $_ 'classification') -eq 'manually_diverged' })
$firstRoom = $(if ($divergedRooms.Count -ge 1) { $divergedRooms[0] } else { $null })

Add-HzProbe -Run $run -Id 'I1' -Name 'the incremental reports the renamed room, on a drawing that has not moved' `
    -Expected 'manually_diverged >= 1, as a review, naming the field and both values' `
    -Observed ("manually_diverged={0} field={1} set_says={2} model_holds={3}" -f
        (Get-HzClass $updated.Result 'manually_diverged'),
        [string](Get-HzPath $firstRoom 'evidence', 'field'),
        [string](Get-HzPath $firstRoom 'evidence', 'set_says'),
        [string](Get-HzPath $firstRoom 'evidence', 'model_holds')) `
    -Ok ((Get-HzClass $updated.Result 'manually_diverged') -ge 1 -and $null -ne $firstRoom -and
         [string](Get-HzProp $firstRoom 'kind') -eq 'review' -and
         [string](Get-HzPath $firstRoom 'evidence', 'model_holds') -eq 'Somewhere Else') `
    -Evidence @{ action = $firstRoom
                 counts_by_classification = $updated.Result.counts_by_classification }

Add-HzProbe -Run $run -Id 'I2' -Name 'and it proposes nothing automatic, and does not call the rename a MOVE' `
    -Expected 'automatic 0 and moved 0' `
    -Observed ("automatic={0} moved={1}" -f (Get-HzProp $updated.Result 'automatic'),
        (Get-HzClass $updated.Result 'moved')) `
    -Ok ([int](Get-HzProp $updated.Result 'automatic') -eq 0 -and
         (Get-HzClass $updated.Result 'moved') -eq 0) `
    -Evidence @{ counts_by_classification = $updated.Result.counts_by_classification
                 note = 'the element did not move by a millimetre; what changed is a value only the set could have supplied' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
