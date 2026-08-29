#Requires -Version 5.1
<#
  A HOLE IN A WALL, WHICH IS THE THIRD KIND OF HOLE.

  `opening` cuts one floor. `shaft` cuts every floor between two storeys. Neither
  is this: a hole cut into the ONE wall it is hosted in, between two heights the
  drawing does not carry. A plan shows where the hole is along the wall and says
  nothing about where it starts or stops, so the rule supplies both - the same
  shape as height_mm on a wall - and refuses without them.

  The backlog scoped this as the other half of 8.4 and the phase that closed 8.4
  closed only the slab half: wall_opening was reachable by a direct call, by no
  requirement set, and by no harness.

  THE SUBSTANCE IS THE VOLUME. A wall opening that was created and cuts nothing
  passes every other check there is - the element exists, its category is right,
  its host is right - so this harness measures the host wall's SOLID VOLUME
  before and after, and expects it to fall by what the drawing asked for.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-wall-openings' -Document $Document
$X = 916000.0

$wallFrom = $X
$wallTo = ($X + 12000.0)
# ON THE WALL LINE, drawn by a STRUCTURAL column - and both halves of that are
# measured rather than tidy. An ARCHITECTURAL column standing inside a wall is
# absorbed by it and exports nothing at all: the drawing came back with two
# layers instead of three. And moving it clear of the wall does not help, because
# the point tolerance is both the host search radius AND the threshold at which
# coincident vertices merge - large enough to reach a wall 400 mm away, it closes
# a 300 mm ring into nothing.
$holeAt = @(($X + 4000.0), 0.0)
# JUST PAST THE END OF THE WALL - 180 mm, which is inside the host allowance this
# set gives (30 mm point tolerance plus half a 352 mm wall) and CLEAR of the wall
# in plan.
#
# Both halves are measured. A ring lying inside a wall's footprint exports only
# THREE of its four sides: the edge running along the wall is hidden by the wall's
# own cut fill, and closed_loops then correctly finds nothing at all. The same way
# an architectural column standing inside a wall is absorbed by it and exports
# nothing. So the ring stands clear, and its far corner still reaches 330 mm past
# the end of the wall - which is the case Revit does not refuse.
$overAt = @(($X + 12180.0), 0.0)
$plainAt = @(($X + 8000.0), 0.0)     # a stretch of plain wall
$sill = 900.0
$head = 2100.0

function Get-HzKindCount {
    param($Plan, [string]$Kind)
    $c = Get-HzPath $Plan 'counts_by_kind', $Kind
    if ($null -eq $c) { 0 } else { [int]$c }
}

function Get-HzCode {
    param($Audit, [string]$Code)
    $c = Get-HzPath $Audit 'counts_by_code', $Code
    if ($null -eq $c) { -1 } else { [int]$c }
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
  The SOLID volume Revit measures for one element, in cubic metres. Not the
  Volume parameter: a parameter can be stale where the geometry cannot.
#>
function Get-HzSolidVolume {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long]$ElementId, [string]$Label = 'volume')
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_quantities' -Label $Label -Arguments @{
        target_document_title = $Run.Document; element_ids = @($ElementId); detail_level = 'Fine' }
    foreach ($row in @(Get-HzProp $q.Result 'elements')) {
        if ([long](Get-HzProp $row 'element_id') -ne $ElementId) { continue }
        $v = Get-HzPath $row 'volume_geometry_m3', 'value'
        if ($null -eq $v) { $v = Get-HzProp $row 'volume_geometry_m3' }
        if ($v -is [double] -or $v -is [int] -or $v -is [decimal]) { return [double]$v }
        return $null
    }
    $null
}

function New-HzHoleSet {
    param([string]$Id, [string]$Layer, [string]$Units, $Sill, $Head, [switch]$OmitHead)
    $rule = @{ id = $Id; precedence = 10; discipline = 'architecture'
               layers = @($Layer); produces = 'wall_opening'; category = 'OST_SWallRectOpening'
               geometry = @{ from = 'closed_loops'; min_area_mm2 = 50000.0 } }
    if ($null -ne $Sill) { $rule['sill_height_mm'] = $Sill }
    if (-not $OmitHead -and $null -ne $Head) { $rule['head_height_mm'] = $Head }
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = "hz-live-$Id"; version = '1.0.0'; title = "Live $Id" }
        source = @{ units = $Units }
        # SMALL, because the point tolerance is also the threshold at which
        # coincident vertices merge, and a 300 mm ring read with a 600 mm
        # tolerance closes into nothing. Both rings sit ON the wall line, so the
        # host search has no distance to cover.
        tolerances = @{ point_mm = 30.0; gap_mm = 30.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @($rule)
    }
}

# =============================================================================
# THE FIXTURE - a wall, a ring on it, and a ring reaching past its end
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

# THE RINGS ARE DRAWN BY COLUMNS, measured in the openings harness: a real Revit
# opening carries no graphics of its own and exports as a break in its host's
# outline, on the host's own layer. A column draws its own rectangle on its own
# layer, and an architectural one and a structural one land on two different ones.
$holeSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Column'
if ($null -eq $holeSymbol) {
    foreach ($id in @('W1', 'W2', 'W3', 'W4', 'W5', 'W6', 'W7', 'A1')) {
        Add-HzProbe -Run $run -Id $id -Name 'the ring this fixture draws needs a column template on this machine' `
            -Expected 'Metric Structural Column.rft' `
            -Observed 'no structural column template' -Status 'fixture_missing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

$fx = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-wall' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'wall'; start = @($wallFrom, 0.0, 0.0); end = @($wallTo, 0.0, 0.0)
                    height = 3000.0; level_id = [long]$level.element_id }) }
if ([int]$fx.Apply.Result.created_verified -ne 1) { throw 'HARNESS: the fixture wall was not built' }

$rings = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-rings' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_column'; type_id = [long]$holeSymbol.type_id
           point = @($holeAt[0], $holeAt[1], 0.0); level_id = [long]$level.element_id },
        @{ kind = 'floor'; level_id = [long]$level.element_id
           profile = @(, @(@(($overAt[0] - 150.0), ($overAt[1] - 150.0), 0.0),
                           @(($overAt[0] + 150.0), ($overAt[1] - 150.0), 0.0),
                           @(($overAt[0] + 150.0), ($overAt[1] + 150.0), 0.0),
                           @(($overAt[0] - 150.0), ($overAt[1] + 150.0), 0.0))) }) }
if ([int]$rings.Apply.Result.created_verified -ne 2) {
    throw ("HARNESS: the fixture wanted two rings and Revit verified {0}" -f $rings.Apply.Result.created_verified)
}

$viewName = "HZ_WO_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$viewId = [long](@($view.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $viewId
                   box = @(($X - 2000.0), -3000.0, ($X + 15000.0), 3000.0) }) }
New-Item -ItemType Directory -Force -Path 'C:\hz-live\dwg' | Out-Null
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($viewId)
    output_path = (Join-Path 'C:\hz-live\dwg' ("HZ_WO_$($run.RunId).dwg")) }
$dwgFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter "HZ_WO_$($run.RunId)*.dwg")[0]
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
$holeLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $holeAt `
    -OtherPoints @($plainAt, $overAt) -RadiusMm 500.0 -Label 'layer-hole'
$overLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $overAt `
    -OtherPoints @($plainAt, $holeAt) -RadiusMm 500.0 -Label 'layer-over'
$allLayers = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'layers' -Arguments @{
    mode = 'layers'; instance_id = $inst }).Result.layers) | ForEach-Object { [string]$_.layer })
Add-HzNote $run ("layers: {0}" -f ($allLayers -join ', '))
Add-HzNote $run ("chosen: wall='{0}' hole='{1}' over='{2}'" -f $wallLayer, $holeLayer, $overLayer)

if (-not $holeLayer -or -not $overLayer) {
    foreach ($id in @('W1', 'W2', 'W3', 'W4', 'W5', 'W6', 'W7', 'A1')) {
        Add-HzProbe -Run $run -Id $id -Name 'the two drawn rings need layers of their own in this drawing' `
            -Expected 'a layer exclusive to each ring' `
            -Observed ("hole='{0}' over='{1}' all={2}" -f $holeLayer, $overLayer, ($allLayers -join '|')) `
            -Status 'fixture_missing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

$holeSet = New-HzHoleSet -Id 'holes' -Layer $holeLayer -Units $units -Sill $sill -Head $head

# =============================================================================
# W - THE HOLE
# =============================================================================
Write-Host "`n== W: a hole in a wall ==" -ForegroundColor Cyan

$tooEarly = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-hole-early' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $holeSet }
$earlyText = $(if ($tooEarly.IsError) { [string]$tooEarly.Text } else { ($tooEarly.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'W1' -Name 'a hole planned before its WALL exists is refused, and the refusal names the fix' `
    -Expected 'host_not_found - convert the wall layers first' `
    -Observed (Limit-HzText $earlyText 220) `
    -Ok ($earlyText -match 'host_not_found') `
    -Evidence @{ reply = (Limit-HzText $earlyText 700) }

$wallSet = New-HzWallRequirementSet -Layer $wallLayer -Units $units -BridgeOpeningsMm 1500.0
$wallPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-walls' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $wallSet
    level_id = [long]$level.element_id }
$builtWalls = Invoke-HzConversion -Run $run -Plan $wallPlan.Result -Set $wallSet -InstanceId $inst -Tag 'walls'
if ([int]$builtWalls.created_verified -lt 1) { throw 'HARNESS: no wall was converted; the hole has nothing to cut' }
# THE WALL THIS RUN BUILT, chosen by WHERE IT IS. The fixture document holds
# thousands of walls; taking the first row of a category sweep would measure the
# volume of somebody else's wall and report it as this one's.
$walls = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
    -Min @(($wallFrom - 500.0), -1500.0, -1000.0) -Max @(($wallTo + 500.0), 1500.0, 4000.0) -Label 'walls')
if ($walls.Count -ne 1) {
    throw ("HARNESS: expected exactly one wall where the fixture drew one and found {0}" -f $walls.Count)
}
$hostWallId = [long]$walls[0].element_id
$volumeBefore = Get-HzSolidVolume -Run $run -ElementId $hostWallId -Label 'volume-before'
Add-HzNote $run ("wall {0} measures {1} m3 before the hole" -f $hostWallId, $volumeBefore)

$holePlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-hole' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $holeSet }
$holeRows = @($holePlan.Result.execute_plan_request.actions)
$holeRow = $(if ($holeRows.Count -gt 0) { @($holeRows[0].arguments.elements)[0] } else { $null })
$hostNamed = @(@($holePlan.Result.apply_binding.resolved_names) |
               Where-Object { [string]$_.what -eq 'host_wall' })
$c1 = @(Get-HzProp $holeRow 'corner_1')
$c2 = @(Get-HzProp $holeRow 'corner_2')

Add-HzProbe -Run $run -Id 'W2' -Name 'the plan names WHICH wall, and carries the two heights the drawing cannot give' `
    -Expected ("1 wall_opening with host_id, corner_1 z={0} and corner_2 z={1}" -f $sill, $head) `
    -Observed ("openings={0} kind={1} host_id={2} sill={3} head={4}" -f
        (Get-HzKindCount $holePlan.Result 'wall_opening'), [string](Get-HzProp $holeRow 'kind'),
        [string](Get-HzProp $holeRow 'host_id'),
        $(if ($c1.Count -ge 3) { $c1[2] } else { '(none)' }),
        $(if ($c2.Count -ge 3) { $c2[2] } else { '(none)' })) `
    -Ok ((Get-HzKindCount $holePlan.Result 'wall_opening') -eq 1 -and
         [string](Get-HzProp $holeRow 'kind') -eq 'wall_opening' -and
         $null -ne (Get-HzProp $holeRow 'host_id') -and $hostNamed.Count -eq 1 -and
         $c1.Count -ge 3 -and [double]$c1[2] -eq $sill -and [double]$c2[2] -eq $head) `
    -Evidence @{ row = $holeRow; resolved = $hostNamed
                 note = 'a plan shows where a hole is along a wall and says nothing about how high it is' }

$applied = Invoke-HzConversion -Run $run -Plan $holePlan.Result -Set $holeSet -InstanceId $inst -Tag 'hole'

# THE CATEGORY IS PROVED BY THE RE-READ, not by a second query. create_elements
# verifies a wall_opening by asking the committed element for its CATEGORY - "is
# it an Opening" is equally true of a shaft and of a hole in a floor - so a row
# that came back verified is the model's own answer about which of the three
# this is.
$verifiedRows = @()
foreach ($stage in @(Get-HzProp $applied 'stages')) {
    foreach ($row in @(Get-HzPath $stage 'verification', 'rows')) { $verifiedRows += $row }
    foreach ($row in @(Get-HzProp $stage 'rows')) { $verifiedRows += $row }
}
$kindVerified = @($verifiedRows | Where-Object {
    [string](Get-HzProp $_ 'kind') -eq 'wall_opening' -and (Get-HzProp $_ 'kind_verified') -eq $true })

Add-HzProbe -Run $run -Id 'W3' -Name 'it is built and re-read as a WALL opening - not merely as an Opening' `
    -Expected '1 created and verified, its kind re-read from the committed element' `
    -Observed ("created={0} state={1} kind_verified_rows={2} actual={3}" -f $applied.created_verified,
        (Get-HzProp $applied 'state'), $kindVerified.Count,
        $(if ($kindVerified.Count -ge 1) { Get-HzProp $kindVerified[0] 'actual_category' } else { '(none)' })) `
    -Ok ([int]$applied.created_verified -eq 1 -and $kindVerified.Count -ge 1) `
    -Evidence @{ state = $applied.state; rows = $verifiedRows
                 note = 'the verification asks the committed element for its category, because is-it-an-Opening is equally true of a shaft and of a hole in a floor' }

# THE SUBSTANCE. An opening that was created and cuts nothing passes every other
# check there is: the element exists, the category is right, the host is right.
$volumeAfter = Get-HzSolidVolume -Run $run -ElementId $hostWallId -Label 'volume-after'
$removed = $(if ($null -ne $volumeBefore -and $null -ne $volumeAfter) { $volumeBefore - $volumeAfter } else { $null })
Add-HzNote $run ("wall {0} measures {1} m3 after the hole" -f $hostWallId, $volumeAfter)

Add-HzProbe -Run $run -Id 'W4' -Name 'and the WALL IS ACTUALLY CUT - measured as solid volume, not asserted' `
    -Expected 'the wall loses volume; a hole 1200 mm high through a 200 mm wall removes roughly 0.07 m3' `
    -Observed ("before={0} after={1} removed={2}" -f $volumeBefore, $volumeAfter, $removed) `
    -Ok ($null -ne $removed -and $removed -gt 0.01) `
    -Evidence @{ before_m3 = $volumeBefore; after_m3 = $volumeAfter; removed_m3 = $removed
                 measured_by = 'horizun_quantities volume_geometry_m3 - the solid Revit holds, not the Volume parameter, which can be stale where the geometry cannot'
                 note = 'an opening that was created and cuts nothing passes every other check there is' }

# =============================================================================
# X - THE REFUSALS
# =============================================================================
Write-Host "`n== X: what is refused ==" -ForegroundColor Cyan

# ON the wall, and reaching past its end. Revit does not fail on this - it
# PROJECTS the corners onto the host and cuts the hole at the end instead.
$overSet = New-HzHoleSet -Id 'holes-over' -Layer $overLayer -Units $units -Sill $sill -Head $head
$overPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-over' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $overSet }
Add-HzNote $run ("the overshooting ring planned {0} opening(s)" -f (Get-HzKindCount $overPlan.Result 'wall_opening'))

# REFUSED IN THE REHEARSAL, which is where it has to happen: Revit does not fail
# on a rectangle past the end of a wall, it PROJECTS the corners onto the host
# and cuts the hole at the end instead.
$over = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-over-dry' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $overSet
    dry_run = $true
    apply_binding = $overPlan.Result.apply_binding
    actions = $overPlan.Result.execute_plan_request.actions
    candidate_index = $overPlan.Result.candidate_index }
$overText = $(if ($over.IsError) { [string]$over.Text } else { ($over.Result | ConvertTo-Json -Depth 14 -Compress) })
Add-HzProbe -Run $run -Id 'W5' -Name 'a hole reaching past the END of its wall is refused, with the overshoot measured' `
    -Expected 'opening_off_the_wall, naming how far past the end it reaches' `
    -Observed (Limit-HzText $overText 260) `
    -Ok ($overText -match 'opening_off_the_wall' -and $overText -match 'rehearsed_nothing') `
    -Evidence @{ reply = (Limit-HzText $overText 800)
                 note = 'Revit does not refuse this - it projects the corners onto the host and cuts the hole at the end, in a place nobody drew. And the rehearsal must SAY it could build nothing: create_elements answers a dry run with valid/invalid counts and does not fail the call, so a stage graded on success alone reported this one as rehearsed clean.' }

$oneHeight = New-HzHoleSet -Id 'holes-onehigh' -Layer $holeLayer -Units $units -Sill $sill -Head $head -OmitHead
$oneCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-one-height' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $oneHeight }
$oneText = $(if ($oneCall.IsError) { [string]$oneCall.Text } else { ($oneCall.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'W6' -Name 'a rule naming only ONE height is refused - a plan carries neither' `
    -Expected 'refused, naming head_height_mm' `
    -Observed (Limit-HzText $oneText 240) `
    -Ok ($oneText -match 'head_height_mm') `
    -Evidence @{ reply = (Limit-HzText $oneText 700)
                 note = 'a hole at a height nobody chose is invisible in the plan it was drawn on' }

$inverted = New-HzHoleSet -Id 'holes-inverted' -Layer $holeLayer -Units $units -Sill $head -Head $sill
$invCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-inverted' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $inverted }
$invText = $(if ($invCall.IsError) { [string]$invCall.Text } else { ($invCall.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'W7' -Name 'a head at or below the sill is refused, because the hole would cut nothing' `
    -Expected 'refused, naming both heights' `
    -Observed (Limit-HzText $invText 240) `
    -Ok ($invText -match 'no height and cut nothing') `
    -Evidence @{ reply = (Limit-HzText $invText 700) }

# =============================================================================
# A - THE AUDIT
# =============================================================================
Write-Host "`n== A: the audit ==" -ForegroundColor Cyan

$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $holeSet }
Add-HzProbe -Run $run -Id 'A1' -Name 'the audit finds the hole it just cut, and reports nothing missing' `
    -Expected 'matched by revision >= 1, drawing_not_built 0' `
    -Observed ("by_revision={0} not_built={1} moved={2}" -f $audit.Result.matched.by_revision,
        (Get-HzCode $audit.Result 'drawing_not_built'), (Get-HzCode $audit.Result 'moved')) `
    -Ok ([int]$audit.Result.matched.by_revision -ge 1 -and
         (Get-HzCode $audit.Result 'drawing_not_built') -eq 0) `
    -Evidence @{ matched = $audit.Result.matched; counts = $audit.Result.counts_by_code }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
