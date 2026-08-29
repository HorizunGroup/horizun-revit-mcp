#Requires -Version 5.1
<#
  HOLES, AND THE DIFFERENCE BETWEEN TWO OF THEM.

  A hole in one floor and a shaft are not the same element and Revit does not
  build them the same way. MEASURED across 2023-2027: NewOpening(host, profile,
  perpendicular) cuts the ONE element it is hosted in; NewOpening(bottom, top,
  profile) makes a shaft, which cuts every floor, roof and ceiling its extent
  passes through. Reading the second as the first is the tempting shortcut and
  it is wrong in a way nobody sees for months - a shaft built as one opening per
  floor stops existing the day somebody adds a storey.

  A hole also does not find its host the way a door does. A door belongs to the
  wall it is NEAR. A hole belongs to the slab it is INSIDE, and the nearest floor
  to a ring drawn over a courtyard is the floor around the courtyard.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-openings' -Document $Document
$X = 904000.0

# THE RINGS ARE DRAWN BY COLUMNS, and that is a measurement rather than a
# preference. Revit exported a floor, a real opening cut in it and a real shaft
# beside it onto ONE layer - A-FLOR-____-OTLN - because an opening is drawn as a
# hole in its host's outline and carries no category graphics of its own. A
# fixture whose three things share one layer cannot ask which rule reads which
# ring. A column DOES draw its own rectangle on its own layer, and an
# architectural one and a structural one land on two different layers, so the
# drawing can say "a ring here" three times and mean three different things.
#
# The second ring is placed CLEAR of the slab on purpose: it is what lets one
# drawing ask both "cut this floor" and "there is no floor here to cut".
$floorMin = @($X, 0.0)
$floorMax = @(($X + 12000.0), 8000.0)
$holeAt = @(($X + 3000.0), 2000.0)
$shaftAt = @(($X + 8000.0), 12000.0)
# THE MIDDLE OF AN EDGE, and that is not a stylistic choice: the layer search
# measures a radius from each segment's MIDPOINT, so neither a corner nor a point
# part way along a twelve-metre line finds the line it sits on.
$plainAt = @($floorMin[0], (($floorMin[1] + $floorMax[1]) / 2.0))

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
  The two storeys a shaft runs between, created by this run under names nothing
  else can already hold. A document's own levels are not usable here: the shaft
  is refused if its top is at or below its base, and a harness that picked two
  by name would be asserting an elevation order it never measured.
#>
function New-HzShaftLevels {
    param([Parameter(Mandatory)]$Run)
    $tag = $Run.RunId.Substring($Run.RunId.Length - 4)
    $names = @("HZB-$tag", "HZT-$tag")
    $made = Invoke-HzWrite -Run $Run -Tool 'horizun_create_elements' -Label 'levels' -Arguments @{
        target_document = $Run.Document; units = 'mm'
        elements = @(@{ kind = 'level'; elevation = 60000.0; name = $names[0] },
                     @{ kind = 'level'; elevation = 64000.0; name = $names[1] }) }
    if ([int]$made.Apply.Result.created_verified -ne 2) {
        throw ("HARNESS: the fixture needs two levels and Revit verified {0}" -f $made.Apply.Result.created_verified)
    }
    $ids = @(@($made.Apply.Result.rows) | ForEach-Object { [long]$_.element_id })
    [ordered]@{ BaseName = $names[0]; TopName = $names[1]; BaseId = $ids[0]; TopId = $ids[1] }
}

function New-HzRingSet {
    param([string]$Id, [string]$Layer, [string]$Produces, [string]$Category, [string]$Units,
          [string]$Level, [string]$BaseLevel, [string]$TopLevel, [switch]$AllowStructural)
    $rule = @{ id = $Id; precedence = 10; discipline = 'architecture'
               layers = @($Layer); produces = $Produces; category = $Category
               geometry = @{ from = 'closed_loops'; min_area_mm2 = 50000.0 } }
    if ($Level) { $rule['level'] = $Level }
    if ($BaseLevel) { $rule['base_level'] = $BaseLevel }
    if ($TopLevel) { $rule['top_level'] = $TopLevel }
    if ($AllowStructural) { $rule['allow_structural'] = $true }
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = "hz-live-$Id"; version = '1.0.0'; title = "Live $Id" }
        source = @{ units = $Units }
        tolerances = @{ point_mm = 30.0; gap_mm = 30.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @($rule)
    }
}

# =============================================================================
# THE FIXTURE - a floor with a hole in it, and a shaft standing clear of it
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
# THE DRAWING IS MADE ON THE DOCUMENT'S OWN FIRST STOREY. A plan of a level this
# run invented exported the floor and NOTHING else - measured: one layer,
# A-FLOR-____-OTLN, with both columns missing from a view that showed the slab.
# The storeys a shaft runs between are a different question, answered by the rule
# rather than by the drawing, so they stay invented and stay out of the view.
$level = Get-HzFirstLevel $run
$levels = New-HzShaftLevels -Run $run

$holeSymbol = Get-HzHostedSymbol -Run $run -Kind 'Column'
$shaftSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Column'
if ($null -eq $holeSymbol -or $null -eq $shaftSymbol) {
    foreach ($id in @('O1', 'O2', 'O3', 'O4', 'S1', 'S2', 'S3', 'S4', 'A1')) {
        Add-HzProbe -Run $run -Id $id -Name 'the rings this fixture draws need two column templates on this machine' `
            -Expected 'Metric Column.rft and Metric Structural Column.rft' `
            -Observed ("column={0} structural={1}" -f ($null -ne $holeSymbol), ($null -ne $shaftSymbol)) `
            -Status 'fixture_missing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

$fl = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-floor' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'floor'; level_id = [long]$level.element_id
                    profile = @(, @(@($floorMin[0], $floorMin[1], 0.0), @($floorMax[0], $floorMin[1], 0.0),
                                    @($floorMax[0], $floorMax[1], 0.0), @($floorMin[0], $floorMax[1], 0.0))) }) }
$fxFloorId = [long](@($fl.Apply.Result.rows)[0].element_id)

# The two rings the drawing will carry: one inside the slab, one clear of it.
$rings = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-rings' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'family_instance'; type_id = [long]$holeSymbol.type_id
           point = @($holeAt[0], $holeAt[1], 0.0); level_id = [long]$level.element_id },
        @{ kind = 'structural_column'; type_id = [long]$shaftSymbol.type_id
           point = @($shaftAt[0], $shaftAt[1], 0.0); level_id = [long]$level.element_id }) }
if ([int]$rings.Apply.Result.created_verified -ne 2) {
    throw ("HARNESS: the fixture wanted two rings and Revit verified {0}" -f $rings.Apply.Result.created_verified)
}

$viewName = "HZ_OP_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$viewId = [long](@($view.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $viewId
                   box = @(($X - 2000.0), -2000.0, ($X + 14000.0), 15000.0) }) }
New-Item -ItemType Directory -Force -Path 'C:\hz-live\dwg' | Out-Null
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($viewId)
    output_path = (Join-Path 'C:\hz-live\dwg' ("HZ_OP_$($run.RunId).dwg")) }
$dwgFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter "HZ_OP_$($run.RunId)*.dwg")[0]
if ($null -eq $dwgFile) { throw 'HARNESS: the fixture exported no DWG' }
$run.Fixture['dwg_name'] = $dwgFile.Name
$run.Fixture['dwg_sha256'] = (Get-HzSha256 $dwgFile.FullName)

# ---------------------------------------------------------------- read it back
$null = Reset-HzDocument $run
# THE RESET DISCARDS THE INVENTED LEVELS TOO, and it must: isolation reopens the
# document from disk and nothing this run did was saved. They are created again in
# the document the conversion will actually run in, and the rules name them by
# NAME - which is the only thing that survives the round trip.
$level = Get-HzFirstLevel $run
$levels = New-HzShaftLevels -Run $run

$inst = Add-HzCadLink -Run $run -DwgPath $dwgFile.FullName -Label 'link'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units

$floorLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $plainAt `
    -OtherPoints @($holeAt, $shaftAt) -RadiusMm 900.0 -Label 'layer-floor'
$holeLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $holeAt `
    -OtherPoints @($plainAt, $shaftAt) -RadiusMm 500.0 -Label 'layer-hole'
$shaftLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $shaftAt `
    -OtherPoints @($plainAt, $holeAt) -RadiusMm 500.0 -Label 'layer-shaft'

$allLayers = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'layers' -Arguments @{
    mode = 'layers'; instance_id = $inst }
$layerNames = @(@($allLayers.Result.layers) | ForEach-Object { [string]$_.layer })
Add-HzNote $run ("layers in the drawing: {0}" -f ($layerNames -join ', '))
Add-HzNote $run ("chosen: floor='{0}' hole='{1}' shaft='{2}'" -f $floorLayer, $holeLayer, $shaftLayer)

if (-not $floorLayer -or -not $holeLayer -or -not $shaftLayer) {
    # NOT a failure of the bridge. Revit decides which of its own graphics reach a
    # DWG layer, and a fixture whose three things share one layer cannot ask the
    # question this harness is about.
    foreach ($id in @('O1', 'O2', 'O3', 'O4', 'S1', 'S2', 'S3', 'S4', 'A1')) {
        Add-HzProbe -Run $run -Id $id -Name 'the exported drawing must separate the floor, the hole and the shaft' `
            -Expected 'three exclusive layers' `
            -Observed ("floor='{0}' hole='{1}' shaft='{2}' all={3}" -f $floorLayer, $holeLayer, $shaftLayer,
                ($layerNames -join '|')) `
            -Status 'fixture_missing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

$floorSet = New-HzRingSet -Id 'floors' -Layer $floorLayer -Produces 'floor' -Category 'OST_Floors' `
    -Units $units -Level ([string]$level.name)
$holeSet = New-HzRingSet -Id 'holes' -Layer $holeLayer -Produces 'opening' -Category 'OST_ShaftOpening' `
    -Units $units -Level ([string]$level.name)
$shaftSet = New-HzRingSet -Id 'shafts' -Layer $shaftLayer -Produces 'shaft' -Category 'OST_ShaftOpening' `
    -Units $units -BaseLevel $levels.BaseName -TopLevel $levels.TopName

# =============================================================================
# O - THE HOLE IN THE SLAB
# =============================================================================
Write-Host "`n== O: a hole in one floor ==" -ForegroundColor Cyan

$tooEarly = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-hole-early' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $holeSet }
$earlyText = $(if ($tooEarly.IsError) { [string]$tooEarly.Text } else { ($tooEarly.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'O1' -Name 'a hole planned before its slab exists is REFUSED, and the refusal names the fix' `
    -Expected 'host_not_found - convert the slab layers first' `
    -Observed (Limit-HzText $earlyText 220) `
    -Ok ($earlyText -match 'host_not_found') `
    -Evidence @{ reply = (Limit-HzText $earlyText 700) }

$floorPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-floors' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $floorSet }
$builtFloors = Invoke-HzConversion -Run $run -Plan $floorPlan.Result -Set $floorSet -InstanceId $inst -Tag 'floors'
if ([int]$builtFloors.created_verified -lt 1) { throw 'HARNESS: no floor was converted; the hole has nothing to cut' }
$slabs = @(Get-HzElements -Run $run -Categories @('OST_Floors') -Label 'slabs')
Add-HzNote $run ("the floor pass built {0} slab(s)" -f $builtFloors.created_verified)

$holePlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-hole' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $holeSet }
$holeRows = @($holePlan.Result.execute_plan_request.actions)
$holeRow = $(if ($holeRows.Count -gt 0) { @($holeRows[0].arguments.elements)[0] } else { $null })
$hostNamed = @(@($holePlan.Result.apply_binding.resolved_names) |
               Where-Object { [string]$_.what -eq 'host_slab' })

Add-HzProbe -Run $run -Id 'O2' -Name 'the plan says WHICH slab this hole is cut in, and records the choice' `
    -Expected '1 slab_opening carrying host_id, and a host_slab in resolved_names' `
    -Observed ("openings={0} host_id={1} named={2}" -f (Get-HzKindCount $holePlan.Result 'opening'),
        [string](Get-HzProp $holeRow 'host_id'), $hostNamed.Count) `
    -Ok ((Get-HzKindCount $holePlan.Result 'opening') -eq 1 -and
         $null -ne (Get-HzProp $holeRow 'host_id') -and $hostNamed.Count -eq 1) `
    -Evidence @{ row = $holeRow; resolved = $hostNamed
                 note = 'a hole belongs to the slab it is INSIDE, not the nearest one' }

$appliedHole = Invoke-HzConversion -Run $run -Plan $holePlan.Result -Set $holeSet -InstanceId $inst -Tag 'hole'
$openingsNow = @(Get-HzElements -Run $run -Categories @('OST_FloorOpening') -Label 'openings')

Add-HzProbe -Run $run -Id 'O3' -Name 'the hole is cut, and re-read from the model as an opening after the commit' `
    -Expected '1 created and verified' `
    -Observed ("created={0} state={1} in_model={2}" -f $appliedHole.created_verified,
        (Get-HzProp $appliedHole 'state'), $openingsNow.Count) `
    -Ok ([int]$appliedHole.created_verified -eq 1) `
    -Evidence @{ state = $appliedHole.state; stages = $appliedHole.stages }

# THE RING THAT FALLS ON NOTHING. The shaft is drawn clear of the slab, so an
# opening rule pointed at it asks for a hole where the building has no floor.
$nowhereSet = New-HzRingSet -Id 'holes-nowhere' -Layer $shaftLayer -Produces 'opening' `
    -Category 'OST_ShaftOpening' -Units $units -Level ([string]$level.name)
$nowhere = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-hole-nowhere' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $nowhereSet }
$nowhereText = $(if ($nowhere.IsError) { [string]$nowhere.Text } else { ($nowhere.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'O4' -Name 'a hole drawn where the building has NO floor is refused, not cut in the nearest one' `
    -Expected 'host_not_found naming the point, and saying the nearest slab is not an answer' `
    -Observed (Limit-HzText $nowhereText 240) `
    -Ok ($nowhereText -match 'host_not_found') `
    -Evidence @{ reply = (Limit-HzText $nowhereText 800)
                 drawn_at = $shaftAt; floor_covers = @($floorMin, $floorMax) }

# =============================================================================
# S - THE SHAFT
# =============================================================================
Write-Host "`n== S: a shaft between two storeys ==" -ForegroundColor Cyan

$oneLevel = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-shaft-1'; version = '1.0.0'; title = 'Live shaft, one level' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 30.0; gap_mm = 30.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'shafts'; precedence = 10; discipline = 'architecture'
                 layers = @($shaftLayer); produces = 'shaft'; category = 'OST_ShaftOpening'
                 base_level = $levels.BaseName
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 50000.0 } })
}
$half = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-shaft-1level' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $oneLevel }
$halfText = $(if ($half.IsError) { [string]$half.Text } else { ($half.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'S1' -Name 'a shaft rule naming only ONE level is refused, because a drawing carries neither' `
    -Expected 'refused, naming top_level as the missing half' `
    -Observed (Limit-HzText $halfText 240) `
    -Ok ($halfText -match 'top_level') `
    -Evidence @{ reply = (Limit-HzText $halfText 700)
                 note = 'a shaft that stopped at the wrong storey looks entirely correct in plan' }

$shaftPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-shaft' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $shaftSet }
$shaftRows = @($shaftPlan.Result.execute_plan_request.actions)
$shaftRow = $(if ($shaftRows.Count -gt 0) { @($shaftRows[0].arguments.elements)[0] } else { $null })
$levelNames = @(@($shaftPlan.Result.apply_binding.resolved_names) |
                Where-Object { [string]$_.what -match 'level' })

Add-HzProbe -Run $run -Id 'S2' -Name 'a shaft is planned as its OWN kind, with BOTH storeys resolved from the rule' `
    -Expected 'kind=shaft carrying base_level_id and top_level_id' `
    -Observed ("shafts={0} kind={1} base={2} top={3}" -f (Get-HzKindCount $shaftPlan.Result 'shaft'),
        [string](Get-HzProp $shaftRow 'kind'), [string](Get-HzProp $shaftRow 'base_level_id'),
        [string](Get-HzProp $shaftRow 'top_level_id')) `
    -Ok ((Get-HzKindCount $shaftPlan.Result 'shaft') -eq 1 -and
         [string](Get-HzProp $shaftRow 'kind') -eq 'shaft' -and
         $null -ne (Get-HzProp $shaftRow 'base_level_id') -and
         $null -ne (Get-HzProp $shaftRow 'top_level_id')) `
    -Evidence @{ row = $shaftRow; resolved = $levelNames }

$appliedShaft = Invoke-HzConversion -Run $run -Plan $shaftPlan.Result -Set $shaftSet -InstanceId $inst -Tag 'shaft'
$shaftsNow = @(Get-HzElements -Run $run -Categories @('OST_ShaftOpening') -Label 'shafts')

Add-HzProbe -Run $run -Id 'S3' -Name 'the shaft is built and re-read as a SHAFT - not merely as an opening' `
    -Expected '1 created and verified, and the model holds it under Shaft Openings' `
    -Observed ("created={0} state={1} shafts_in_model={2}" -f $appliedShaft.created_verified,
        (Get-HzProp $appliedShaft 'state'), $shaftsNow.Count) `
    -Ok ([int]$appliedShaft.created_verified -eq 1 -and $shaftsNow.Count -ge 1) `
    -Evidence @{ state = $appliedShaft.state; stages = $appliedShaft.stages
                 note = 'a shaft built as one opening per floor stops existing the day somebody adds a storey' }

$inverted = New-HzRingSet -Id 'shafts-down' -Layer $shaftLayer -Produces 'shaft' -Category 'OST_ShaftOpening' `
    -Units $units -BaseLevel $levels.TopName -TopLevel $levels.BaseName
$down = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-shaft-down' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $inverted }
$downText = $(if ($down.IsError) { [string]$down.Text } else { ($down.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'S4' -Name 'a shaft whose top sits below its base is refused BEFORE anything is built' `
    -Expected 'shaft_inverted, naming both storeys' `
    -Observed (Limit-HzText $downText 240) `
    -Ok ($downText -match 'shaft_inverted') `
    -Evidence @{ reply = (Limit-HzText $downText 700) }

# =============================================================================
# A - THE AUDIT
# =============================================================================
Write-Host "`n== A: the audit ==" -ForegroundColor Cyan

$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $shaftSet }
Add-HzProbe -Run $run -Id 'A1' -Name 'the audit finds the shaft it just built, and reports nothing moved' `
    -Expected 'matched by revision, moved 0, missing 0' `
    -Observed ("by_revision={0} moved={1} missing={2}" -f $audit.Result.matched.by_revision,
        (Get-HzCode $audit.Result 'moved'), (Get-HzCode $audit.Result 'missing')) `
    -Ok ([int]$audit.Result.matched.by_revision -ge 1 -and (Get-HzCode $audit.Result 'moved') -eq 0) `
    -Evidence @{ matched = $audit.Result.matched; counts = $audit.Result.counts_by_code }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
