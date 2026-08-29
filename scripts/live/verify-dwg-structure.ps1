#Requires -Version 5.1
<#
  STRUCTURE FROM A DRAWING, LIVE.

  A structural drawing is not an architectural one with different layer names.
  What it carries is different in kind:

      G  GRIDS - the lines everything else is dimensioned from. Revit hosts
         nothing on them and they belong to no level, which is why they are the
         one thing here that does NOT need a storey chosen for it
      S  STRUCTURAL COLUMNS - a real category, not an architectural column with
         a different family. Schedules, analytical models and load takedowns all
         read the category, not the shape
      B  BEAMS - structural framing from single lines
      W  LOAD-BEARING WALLS, and the point of the exercise: a structural wall
         and an architectural one of the same thickness look identical in plan
         and are different elements to every structural schedule
      L  A STRUCTURAL SLAB, the same distinction for a floor

  W and L are the ones that could be faked and must not be. "Created" says a
  wall exists; only re-reading Revit's own parameter says it bears load.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-structure' -Document $Document
$X = 900000.0

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
  A drawing built from arbitrary typed elements, exported, and thrown away.
  Same shape as the architecture harness's fixture, kept separate because a
  structural fixture needs a different crop and a different set of elements.
#>
function New-HzStructureFixture {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][array]$Elements,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][double[]]$CropMin,
        [Parameter(Mandatory)][double[]]$CropMax
    )
    $level = Get-HzFirstLevel $Run
    $rows = @()
    foreach ($e in $Elements) {
        $row = @{}
        foreach ($k in $e.Keys) { $row[$k] = $e[$k] }
        if (-not $row.ContainsKey('level_id')) { $row['level_id'] = [long]$level.element_id }
        $rows += $row
    }
    $made = Invoke-HzWrite -Run $Run -Tool 'horizun_create_elements' -Label "fx-$Tag" -Arguments @{
        target_document = $Run.Document; units = 'mm'; elements = $rows }
    if ([int]$made.Apply.Result.created_verified -ne $rows.Count) {
        throw ("HARNESS: fixture {0} wanted {1} elements and Revit verified {2}" -f
            $Tag, $rows.Count, $made.Apply.Result.created_verified)
    }

    $viewName = "HZ_STR_${Tag}_$($Run.RunId)"
    $view = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-view" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                       name = $viewName }) }
    $viewId = [long](@($view.Apply.Result.rows)[0].element_id)
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-crop" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'set_crop'; view_id = $viewId
                       box = @($CropMin[0], $CropMin[1], $CropMax[0], $CropMax[1]) }) }
    $dwg = Join-Path 'C:\hz-live\dwg' ("HZ_STR_${Tag}_$($Run.RunId).dwg")
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_export' -Label "fx-$Tag-export" -Arguments @{
        target_document = $Run.Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwg }
    $file = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_STR_${Tag}_$($Run.RunId)*.dwg"))[0]
    if ($null -eq $file) { throw "HARNESS: fixture $Tag exported no DWG" }
    [ordered]@{
        fixture_id = "HZ_STR_${Tag}_$($Run.RunId)"
        dwg_path = $file.FullName; dwg_name = $file.Name
        dwg_sha256 = (Get-HzSha256 $file.FullName); dwg_bytes = $file.Length
    }
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

<#
  Did the commit re-read this element as load-bearing? The apply carries the
  rows create_elements verified after its own transaction; structural_verified
  is present only where the row asked for it, which is where it matters.
#>
function Get-HzStructuralVerified {
    param($Applied)
    foreach ($stage in @(Get-HzProp $Applied 'stages')) {
        foreach ($row in @(Get-HzProp $stage 'rows')) {
            $s = Get-HzProp $row 'structural_verified'
            if ($s -and (Get-HzProp $s 'verified') -eq $true) { return $true }
        }
    }
    $false
}

# =============================================================================
# THE FIXTURE - one structural plan carrying all five things
# =============================================================================
Write-Host "`n== the structural fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$columnSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Column'
$beamSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Framing - Beams and Braces'

# Two grids, a load-bearing wall, a slab, a beam and a column, laid out so no
# two of them share a neighbourhood - the layers are told apart by exclusivity
# and that only works if the things are apart.
$gridAt = @(($X + 1000.0), 4000.0)
$wallAt = @(($X + 8000.0), 0.0)
# ON THE EDGE. A floor draws its OUTLINE; the middle of a slab is empty, and a
# sample taken there finds no layer at all.
$slabAt = @(($X + 8000.0), 10000.0)
$columnAt = @(($X + 16000.0), 8000.0)

$elements = @(
    @{ kind = 'grid'; start = @(($X + 1000.0), 0.0, 0.0); end = @(($X + 1000.0), 8000.0, 0.0) },
    @{ kind = 'grid'; start = @(($X - 1000.0), 2000.0, 0.0); end = @(($X + 3000.0), 2000.0, 0.0) },
    @{ kind = 'wall'; start = @(($X + 5000.0), 0.0, 0.0); end = @(($X + 11000.0), 0.0, 0.0)
       height = 3000.0; structural = $true },
    @{ kind = 'floor'; structural = $true
       profile = @(, @(@(($X + 5000.0), 10000.0, 0.0), @(($X + 11000.0), 10000.0, 0.0),
                       @(($X + 11000.0), 14000.0, 0.0), @(($X + 5000.0), 14000.0, 0.0))) }
)
if ($columnSymbol) {
    $elements += @{ kind = 'structural_column'; type_id = [long]$columnSymbol.type_id
                    point = @($columnAt[0], $columnAt[1], 0.0) }
}

$fixture = New-HzStructureFixture -Run $run -Tag 'plan' -Elements $elements `
    -CropMin @(($X - 3000.0), -3000.0) -CropMax @(($X + 20000.0), 17000.0)
foreach ($k in $fixture.Keys) { $run.Fixture[$k] = $fixture[$k] }
$run.Expected['grids_drawn'] = 2
$run.Expected['structural_wall_drawn'] = 1
$run.Expected['structural_slab_drawn'] = 1
Add-HzNote $run ("structural fixture {0}" -f $fixture.dwg_name)

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

# The reset reopens from disk and nothing was saved, so the families are gone
# with it. They are provisioned again into the document the conversion runs in.
$columnSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Column'
$beamSymbol = Get-HzHostedSymbol -Run $run -Kind 'Structural Framing - Beams and Braces'

$inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link-structure'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units

$gridLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $gridAt `
    -OtherPoints @($wallAt, $slabAt, $columnAt) -RadiusMm 900 -Label 'layer-grid'
$wallLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $wallAt `
    -OtherPoints @($gridAt, $slabAt, $columnAt) -RadiusMm 900 -Label 'layer-wall'
$slabLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $slabAt `
    -OtherPoints @($gridAt, $wallAt, $columnAt) -RadiusMm 900 -Label 'layer-slab'
$columnLayer = $null
if ($columnSymbol) {
    $columnLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $columnAt `
        -OtherPoints @($gridAt, $wallAt, $slabAt) -RadiusMm 900 -Label 'layer-column'
}
Add-HzNote $run ("layers: grid='{0}' wall='{1}' slab='{2}' column='{3}'" -f
    $gridLayer, $wallLayer, $slabLayer, $columnLayer)
foreach ($pair in @(@('grid', $gridLayer), @('wall', $wallLayer), @('slab', $slabLayer))) {
    if (-not $pair[1]) { throw ("HARNESS: the fixture drew no {0} on a layer of its own" -f $pair[0]) }
}

# =============================================================================
# G - GRIDS
# =============================================================================
Write-Host "`n== G: grids ==" -ForegroundColor Cyan

$setG = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-grids'; version = '1.0.0'; title = 'Grids from single lines' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'grids'; precedence = 10; discipline = 'structure'
                 layers = @($gridLayer); produces = 'grid'; category = 'OST_Grids'
                 geometry = @{ from = 'single_lines'; min_length_mm = 1000.0 } })
}

# A GRID BELONGS TO NO LEVEL, and asking for one would be the wrong question.
$planG = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-grid' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $setG }
$gridActions = @($planG.Result.execute_plan_request.actions)
$gridRow = $null
if ($gridActions.Count -gt 0) { $gridRow = @($gridActions[0].arguments.elements)[0] }

Add-HzProbe -Run $run -Id 'G1' -Name 'grids are planned WITHOUT a level - Revit hosts them on none' `
    -Expected '2 grid actions, each with start and end and no level_id' `
    -Observed ("grids={0} has_start={1} level_id={2}" -f (Get-HzKindCount $planG.Result 'grid'),
        [bool](Get-HzProp $gridRow 'start'), [string](Get-HzProp $gridRow 'level_id')) `
    -Ok ((Get-HzKindCount $planG.Result 'grid') -eq 2 -and $null -ne (Get-HzProp $gridRow 'start') -and
         $null -eq (Get-HzProp $gridRow 'level_id')) `
    -Evidence @{ row = $gridRow; counts = $planG.Result.counts_by_kind }

$gridsBefore = Get-HzElementCount -Run $run -Categories @('OST_Grids') -Label 'grids-before'
$appliedG = Invoke-HzConversion -Run $run -Tag 'grid' -Conversion ([pscustomobject]@{
    InstanceId = $inst; Plan = $planG.Result; Set = $setG })
$gridsAfter = Get-HzElementCount -Run $run -Categories @('OST_Grids') -Label 'grids-after'

Add-HzProbe -Run $run -Id 'G2' -Name 'both grids are built and verified by re-reading' `
    -Expected '2 created and verified' `
    -Observed ("created={0} grids_delta={1} state={2}" -f $appliedG.created_verified,
        ($gridsAfter - $gridsBefore), $appliedG.state) `
    -Ok ([int]$appliedG.created_verified -eq 2 -and ($gridsAfter - $gridsBefore) -eq 2) `
    -Evidence @{ state = $appliedG.state; provenance = $appliedG.provenance }

$auditG = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-grid' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $setG }
Add-HzProbe -Run $run -Id 'G3' -Name 'and the audit agrees about the grids it just built' `
    -Expected 'matched by revision, nothing reported moved' `
    -Observed ("by_revision={0} moved={1}" -f $auditG.Result.matched.by_revision,
        (Get-HzCode $auditG.Result 'moved')) `
    -Ok ([int]$auditG.Result.matched.by_revision -ge 2 -and (Get-HzCode $auditG.Result 'moved') -eq 0) `
    -Evidence @{ matched = $auditG.Result.matched; counts = $auditG.Result.counts_by_code }

# THE NAMES A DRAWING DOES NOT CARRY. Grid bubbles are TEXT, and text does not
# survive a DWG import as text - it arrives as curves on its own layer. Saying so
# is the finding; inventing "A" and "B" would be worse than useless.
$coverage = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-coverage' -Arguments @{
    mode = 'geometry'; instance_id = $inst; max_rows = 1 }).Result.harvest_coverage
Add-HzProbe -Run $run -Id 'G4' -Name 'the bridge SAYS that grid names cannot come from the drawing' `
    -Expected 'the harvest declares text unreachable rather than inventing names' `
    -Observed (Limit-HzText ([string](Get-HzProp $coverage 'text_is_unavailable')) 160) `
    -Ok ($null -ne (Get-HzProp $coverage 'text_is_unavailable')) `
    -Evidence @{ text_is_unavailable = (Get-HzProp $coverage 'text_is_unavailable')
                 note = 'a grid named by guess is worse than a grid not named' }

# =============================================================================
# W - LOAD-BEARING WALLS
# =============================================================================
Write-Host "`n== W: load-bearing walls ==" -ForegroundColor Cyan

$setW = New-HzWallRequirementSet -Layer $wallLayer -Units $units -Id 'hz-live-structural-walls'
$setW.rules[0]['discipline'] = 'structure'
$setW.rules[0]['structural'] = $true

$planW = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-swall' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $setW
    level_id = [long]$level.element_id }
$wallActions = @($planW.Result.execute_plan_request.actions)
$wallRow = $null
if ($wallActions.Count -gt 0) { $wallRow = @($wallActions[0].arguments.elements)[0] }

Add-HzProbe -Run $run -Id 'W1' -Name 'a rule that says structural puts it in the row that builds the wall' `
    -Expected '1 wall action carrying structural true' `
    -Observed ("walls={0} structural={1}" -f (Get-HzKindCount $planW.Result 'wall'),
        [string](Get-HzProp $wallRow 'structural')) `
    -Ok ((Get-HzKindCount $planW.Result 'wall') -eq 1 -and (Get-HzProp $wallRow 'structural') -eq $true) `
    -Evidence @{ row = $wallRow }

$appliedW = Invoke-HzConversion -Run $run -Tag 'swall' -Conversion ([pscustomobject]@{
    InstanceId = $inst; Plan = $planW.Result; Set = $setW })

Add-HzProbe -Run $run -Id 'W2' -Name 'and the wall RE-READS as load-bearing, not merely created' `
    -Expected '1 created, structural_verified true off the commit' `
    -Observed ("created={0} structural_verified={1}" -f $appliedW.created_verified,
        (Get-HzStructuralVerified $appliedW)) `
    -Ok ([int]$appliedW.created_verified -eq 1 -and (Get-HzStructuralVerified $appliedW)) `
    -Evidence @{ stages = $appliedW.stages
                 note = 'a wall that reports itself structural and is not appears in no structural schedule' }

# =============================================================================
# L - A STRUCTURAL SLAB
# =============================================================================
Write-Host "`n== L: structural slab ==" -ForegroundColor Cyan

$setL = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-structural-slab'; version = '1.0.0'; title = 'Structural slab' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'slab'; precedence = 10; discipline = 'structure'
                 layers = @($slabLayer); produces = 'floor'; category = 'OST_Floors'
                 structural = $true
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 1000000.0 } })
}
$planL = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-slab' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $setL
    level_id = [long]$level.element_id }
$slabActions = @($planL.Result.execute_plan_request.actions)
$slabRow = $null
if ($slabActions.Count -gt 0) { $slabRow = @($slabActions[0].arguments.elements)[0] }

Add-HzProbe -Run $run -Id 'L1' -Name 'a structural slab is planned as a floor that says it bears load' `
    -Expected '1 floor action, profile of loops, structural true' `
    -Observed ("floors={0} structural={1} rings={2}" -f (Get-HzKindCount $planL.Result 'floor'),
        [string](Get-HzProp $slabRow 'structural'),
        $(if ($slabRow -and (Get-HzProp $slabRow 'profile')) { @($slabRow.profile).Count } else { 0 })) `
    -Ok ((Get-HzKindCount $planL.Result 'floor') -eq 1 -and (Get-HzProp $slabRow 'structural') -eq $true) `
    -Evidence @{ row = $slabRow }

$slabsBefore = Get-HzElementCount -Run $run -Categories @('OST_Floors') -Label 'slabs-before'
$appliedL = Invoke-HzConversion -Run $run -Tag 'slab' -Conversion ([pscustomobject]@{
    InstanceId = $inst; Plan = $planL.Result; Set = $setL })
$slabsAfter = Get-HzElementCount -Run $run -Categories @('OST_Floors') -Label 'slabs-after'

Add-HzProbe -Run $run -Id 'L2' -Name 'the slab is built and RE-READS as structural' `
    -Expected '1 created, floors delta 1, structural_verified true' `
    -Observed ("created={0} floors_delta={1} structural_verified={2}" -f $appliedL.created_verified,
        ($slabsAfter - $slabsBefore), (Get-HzStructuralVerified $appliedL)) `
    -Ok ([int]$appliedL.created_verified -eq 1 -and ($slabsAfter - $slabsBefore) -eq 1 -and
         (Get-HzStructuralVerified $appliedL)) `
    -Evidence @{ stages = $appliedL.stages }

# THE CONTROL. The same drawing, the same layer, WITHOUT the declaration - and
# the result must differ, or the flag proves nothing.
$setLplain = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-plain-slab'; version = '1.0.0'; title = 'Plain slab' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'slab'; precedence = 10; discipline = 'architecture'
                 layers = @($slabLayer); produces = 'floor'; category = 'OST_Floors'
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 1000000.0 } })
}
$planPlain = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-slab-plain' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $setLplain
    level_id = [long]$level.element_id }
$plainActions = @($planPlain.Result.execute_plan_request.actions)
$plainRow = $null
if ($plainActions.Count -gt 0) { $plainRow = @($plainActions[0].arguments.elements)[0] }

Add-HzProbe -Run $run -Id 'L3' -Name 'without the declaration the row says NOTHING about load, and the document decides' `
    -Expected 'no structural key at all - not false, which would be this choosing' `
    -Observed ("has_structural_key={0}" -f ($null -ne (Get-HzProp $plainRow 'structural'))) `
    -Ok ($null -eq (Get-HzProp $plainRow 'structural')) `
    -Evidence @{ row = $plainRow
                 note = 'silence leaves the document its default; false would be the bridge deciding' }

# =============================================================================
# S - STRUCTURAL COLUMNS
# =============================================================================
Write-Host "`n== S: structural columns ==" -ForegroundColor Cyan

if (-not $columnSymbol -or -not $columnLayer) {
    foreach ($id in @('S1', 'S2')) {
        Add-HzProbe -Run $run -Id $id -Name 'a structural column needs a structural column family on this machine' `
            -Expected 'Metric Column.rft provisioned into OST_StructuralColumns' `
            -Observed ("symbol={0} layer={1}" -f ($null -ne $columnSymbol), $columnLayer) `
            -Status 'fixture_missing'
    }
} else {
    $setS = @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = 'hz-live-scolumns'; version = '1.0.0'; title = 'Structural columns' }
        source = @{ units = $units }
        tolerances = @{ point_mm = 300.0; gap_mm = 300.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @(@{ id = 'scolumns'; precedence = 10; discipline = 'structure'
                     layers = @($columnLayer); produces = 'structural_column'
                     category = 'OST_StructuralColumns'; family_type = $columnSymbol.type_name
                     geometry = @{ from = 'point_clusters'; cluster_radius_mm = 900.0 } })
    }
    $planS = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-scolumn' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $setS
        level_id = [long]$level.element_id }
    $sActions = @($planS.Result.execute_plan_request.actions)
    $sRow = $null
    if ($sActions.Count -gt 0) { $sRow = @($sActions[0].arguments.elements)[0] }

    Add-HzProbe -Run $run -Id 'S1' -Name 'a structural column is planned as its OWN kind, not as a family instance' `
        -Expected "1 action of kind structural_column" `
        -Observed ("columns={0} kind={1}" -f (Get-HzKindCount $planS.Result 'structural_column'),
            [string](Get-HzProp $sRow 'kind')) `
        -Ok ((Get-HzKindCount $planS.Result 'structural_column') -eq 1 -and
             (Get-HzProp $sRow 'kind') -eq 'structural_column') `
        -Evidence @{ row = $sRow
                     note = 'the category is what every structural schedule and load takedown reads' }

    $sBefore = Get-HzElementCount -Run $run -Categories @('OST_StructuralColumns') -Label 'scols-before'
    $appliedS = Invoke-HzConversion -Run $run -Tag 'scolumn' -Conversion ([pscustomobject]@{
        InstanceId = $inst; Plan = $planS.Result; Set = $setS })
    $sAfter = Get-HzElementCount -Run $run -Categories @('OST_StructuralColumns') -Label 'scols-after'
    $sPlaced = @(Get-HzElementsIn -Run $run -Categories @('OST_StructuralColumns') `
        -Min @(($columnAt[0] - 600.0), ($columnAt[1] - 600.0), -1000.0) `
        -Max @(($columnAt[0] + 600.0), ($columnAt[1] + 600.0), 4000.0) -Label 'scol-where')

    Add-HzProbe -Run $run -Id 'S2' -Name 'and it is built in the structural category, where the drawing put it' `
        -Expected '1 created, delta 1 in OST_StructuralColumns, within 600 mm of the cluster' `
        -Observed ("created={0} delta={1} within_600mm={2}" -f $appliedS.created_verified,
            ($sAfter - $sBefore), $sPlaced.Count) `
        -Ok ([int]$appliedS.created_verified -eq 1 -and ($sAfter - $sBefore) -eq 1 -and $sPlaced.Count -eq 1) `
        -Evidence @{ state = $appliedS.state; drawn_at = $columnAt }
}

# =============================================================================
# B - BEAMS
#
# A LAYER MEANS WHAT THE REQUIREMENT SET SAYS IT MEANS. These lines were drawn
# as grids, and here a second set declares that the same layer carries beams -
# which is not a trick, it is the whole mechanism: a DWG has no element types,
# only layers, and the rules are where a person says what each one is. The
# drawing is unchanged and reads two ways because two people asked it different
# questions.
# =============================================================================
Write-Host "`n== B: beams ==" -ForegroundColor Cyan

function New-HzBeamSet {
    param([string]$FamilyType, [string]$Units, [string]$Layer, [string]$Id = 'hz-live-beams')
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'Beams from single lines' }
        source = @{ units = $Units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @(@{ id = 'beams'; precedence = 10; discipline = 'structure'
                     layers = @($Layer); produces = 'beam'; category = 'OST_StructuralFraming'
                     family_type = $FamilyType
                     geometry = @{ from = 'single_lines'; min_length_mm = 1000.0 } })
    }
}

# THE REFUSAL FIRST. A family that is not loaded must stop the plan by name -
# substituting whatever framing type happens to be there builds a different
# building and verifies it happily.
$missingSet = New-HzBeamSet -FamilyType 'HZ_NO_SUCH_BEAM_FAMILY' -Units $units -Layer $gridLayer `
    -Id 'hz-live-beams-missing'
$missing = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-beam-missing' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $missingSet
    level_id = [long]$level.element_id }
$missingText = $missing.Text
if (-not $missing.IsError) { $missingText = ($missing.Result | ConvertTo-Json -Depth 12 -Compress) }
Add-HzProbe -Run $run -Id 'B1' -Name 'a beam family that is not loaded stops the plan BY NAME' `
    -Expected 'type_not_found naming the family, and nothing planned' `
    -Observed (Limit-HzText $missingText 200) `
    -Ok ($missingText -match 'type_not_found') `
    -Evidence @{ reply = (Limit-HzText $missingText 600) }

if (-not $beamSymbol) {
    foreach ($id in @('B2', 'B3')) {
        Add-HzProbe -Run $run -Id $id -Name 'beams need a structural framing family on this machine' `
            -Expected 'Metric Structural Framing - Beams and Braces.rft' `
            -Observed 'the template is not installed here' `
            -Status 'fixture_missing'
    }
} else {
    $setB = New-HzBeamSet -FamilyType $beamSymbol.type_name -Units $units -Layer $gridLayer
    $planB = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-beam' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $setB
        level_id = [long]$level.element_id }
    $bActions = @($planB.Result.execute_plan_request.actions)
    $beamRow = $null
    if ($bActions.Count -gt 0) { $beamRow = @($bActions[0].arguments.elements)[0] }

    Add-HzProbe -Run $run -Id 'B2' -Name 'a beam is planned as structural framing from a single line' `
        -Expected '2 actions of kind structural_framing, each with start and end' `
        -Observed ("beams={0} kind={1} has_start={2}" -f (Get-HzKindCount $planB.Result 'beam'),
            [string](Get-HzProp $beamRow 'kind'), [bool](Get-HzProp $beamRow 'start')) `
        -Ok ((Get-HzKindCount $planB.Result 'beam') -eq 2 -and
             (Get-HzProp $beamRow 'kind') -eq 'structural_framing' -and
             $null -ne (Get-HzProp $beamRow 'start')) `
        -Evidence @{ row = $beamRow; counts = $planB.Result.counts_by_kind
                     note = 'the same lines the grid rule read; a layer means what the set says it means' }

    $bBefore = Get-HzElementCount -Run $run -Categories @('OST_StructuralFraming') -Label 'beams-before'
    $appliedB = Invoke-HzConversion -Run $run -Tag 'beam' -Conversion ([pscustomobject]@{
        InstanceId = $inst; Plan = $planB.Result; Set = $setB })
    $bAfter = Get-HzElementCount -Run $run -Categories @('OST_StructuralFraming') -Label 'beams-after'

    Add-HzProbe -Run $run -Id 'B3' -Name 'and both beams are built in the structural framing category' `
        -Expected '2 created and verified, delta 2' `
        -Observed ("created={0} delta={1} state={2}" -f $appliedB.created_verified,
            ($bAfter - $bBefore), $appliedB.state) `
        -Ok ([int]$appliedB.created_verified -eq 2 -and ($bAfter - $bBefore) -eq 2) `
        -Evidence @{ state = $appliedB.state; provenance = $appliedB.provenance }
}

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
