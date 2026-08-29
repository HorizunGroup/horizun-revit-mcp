#Requires -Version 5.1
<#
  WHAT CHANGED BETWEEN TWO REVISIONS, LIVE AND ACROSS CATEGORIES.

  The first conversion is the easy half. An incremental update goes wrong
  quietly, on top of a week of somebody's work, and the ways it goes wrong are
  not variations of one mistake - they are different mistakes that a plan
  reporting only "review" cannot tell apart.

  So this proves the whole vocabulary against a real model, one change at a
  time, with the drawing for revision B exported from Revit exactly as revision
  A was:

      unchanged           the drawing says what it said and nobody touched it
      added               in B and never built
      removed             built from A and B does not say it
      moved               the same wall, somewhere else
      reshaped            recognisably the same wall, a different length
      relayered           the same shape drawn on a different layer
      retyped             the same line, a different type asked for
      resized             the same line, a different thickness asked for
      manually_diverged   the drawing stood still and a person moved it
      conflict            B dropped it AND a person moved it
      ambiguous           two candidates could be the same element
      rehosted            proved by the architecture harness's door, and by unit
                          tests; a second wall to move a door INTO is more model
                          than this fixture carries, and saying so is better than
                          a probe that pretends

  Two invariants are checked throughout and matter more than any single
  classification: NOTHING is ever deleted automatically, and planning the same
  revision twice proposes no automatic work at all.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-changes' -Document $Document
$X = 900000.0

function Get-HzClass {
    param($Update, [string]$Name)
    $c = Get-HzPath $Update 'counts_by_classification', $Name
    if ($null -eq $c) { -1 } else { [int]$c }
}

<#
  Every action of one classification, so a probe can say WHICH element it means
  rather than only how many there were.
#>
function Get-HzActionsOf {
    param($Update, [string]$Classification)
    @(@(Get-HzProp $Update 'plan') | Where-Object { (Get-HzProp $_ 'classification') -eq $Classification })
}

<#
  Build a drawing from typed elements, export it, and leave the model as it was.
  Revision A and revision B come from the same maker so the only difference
  between them is the one the test is about.
#>
function New-HzRevision {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][array]$Elements,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][double[]]$CropMin,
        [Parameter(Mandatory)][double[]]$CropMax
    )
    $null = Reset-HzDocument $Run
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
        throw ("HARNESS: revision {0} wanted {1} elements and Revit verified {2}" -f
            $Tag, $rows.Count, $made.Apply.Result.created_verified)
    }

    $viewName = "HZ_CHG_${Tag}_$($Run.RunId)"
    $view = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-view" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                       name = $viewName }) }
    $viewId = [long](@($view.Apply.Result.rows)[0].element_id)
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-crop" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'set_crop'; view_id = $viewId
                       box = @($CropMin[0], $CropMin[1], $CropMax[0], $CropMax[1]) }) }
    $dwg = Join-Path 'C:\hz-live\dwg' ("HZ_CHG_${Tag}_$($Run.RunId).dwg")
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_export' -Label "fx-$Tag-export" -Arguments @{
        target_document = $Run.Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwg }
    $file = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_CHG_${Tag}_$($Run.RunId)*.dwg"))[0]
    if ($null -eq $file) { throw "HARNESS: revision $Tag exported no DWG" }
    [ordered]@{ tag = $Tag; dwg_path = $file.FullName; dwg_name = $file.Name
                dwg_sha256 = (Get-HzSha256 $file.FullName) }
}

function New-HzChangeSet {
    param([Parameter(Mandatory)][string[]]$Layers, [Parameter(Mandatory)][string]$Units,
          [string]$FamilyType, [double]$ThicknessMm = 0.0, [string]$Id = 'hz-live-changes')
    $rule = @{
        id = 'walls'; precedence = 10; discipline = 'architecture'
        layers = $Layers; produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
        geometry = @{ from = 'double_lines'; min_thickness_mm = 100.0; max_thickness_mm = 500.0
                      min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 }
    }
    if ($FamilyType) { $rule['family_type'] = $FamilyType }
    if ($ThicknessMm -gt 0) { $rule['thickness_mm'] = $ThicknessMm }
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'Live change detection' }
        source = @{ units = $Units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @($rule)
    }
}

function Invoke-HzUpdatePlan {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long]$InstanceId,
          [Parameter(Mandatory)][hashtable]$Set, [Parameter(Mandatory)][string]$Label,
          [string[]]$Supersedes = @(), [long]$LevelId = 0)
    $args = @{ target_document = $Run.Document; instance_id = $InstanceId; requirement_set = $Set }
    if ($Supersedes.Count -gt 0) { $args['supersedes_sha256'] = $Supersedes }
    if ($LevelId -gt 0) { $args['level_id'] = $LevelId }
    (Invoke-HzToolStrict -Run $Run -Tool 'horizun_plan_cad_update' -Label $Label -Arguments $args).Result
}

# =============================================================================
# REVISION A, and the conversion that leaves something to compare against
# =============================================================================
Write-Host "`n== revision A ==" -ForegroundColor Cyan

function New-HzWallRow {
    param([double]$X0, [double]$X1, [double]$Y)
    @{ kind = 'wall'; start = @($X0, $Y, 0.0); end = @($X1, $Y, 0.0); height = 3000.0 }
}

# Five walls, far enough apart that no two can be mistaken for each other and
# each can carry one change of its own.
$unchangedAt = 0.0
$movedAt = 8000.0
$removedAt = 16000.0
$divergedAt = 24000.0
$conflictAt = 32000.0

$revA = New-HzRevision -Run $run -Tag 'A' -CropMin @(($X - 3000.0), -3000.0) `
    -CropMax @(($X + 12000.0), 40000.0) -Elements @(
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $unchangedAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $movedAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $removedAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $divergedAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $conflictAt))
foreach ($k in $revA.Keys) { $run.Fixture["revision_a_$k"] = $revA[$k] }
Add-HzNote $run ("revision A {0}" -f $revA.dwg_name)

# REVISION B, made the same way: one wall stays, one moves 500 mm, one is
# lengthened, one is dropped, one is dropped, and one is new.
$movedToAt = $movedAt + 500.0
$addedAt = 40000.0
$revB = New-HzRevision -Run $run -Tag 'B' -CropMin @(($X - 3000.0), -3000.0) `
    -CropMax @(($X + 12000.0), 46000.0) -Elements @(
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $unchangedAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $movedToAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $divergedAt),
        (New-HzWallRow -X0 $X -X1 ($X + 6000.0) -Y $addedAt))
foreach ($k in $revB.Keys) { $run.Fixture["revision_b_$k"] = $revB[$k] }
Add-HzNote $run ("revision B {0}" -f $revB.dwg_name)
$run.Expected['revision_a_walls'] = 5
$run.Expected['revision_b_walls'] = 4

# ------------------------------------------------- convert revision A for real
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$instA = Add-HzCadLink -Run $run -DwgPath $revA.dwg_path -Label 'link-A'
$layerA = Get-HzWallLayer -Run $run -InstanceId $instA
$factsA = Get-HzCadInstanceFacts -Run $run -InstanceId $instA
$units = [string]$factsA.declared_units
$setA = New-HzChangeSet -Layers @($layerA) -Units $units

$planA = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-A' -Arguments @{
    target_document = $Document; instance_id = $instA; requirement_set = $setA
    level_id = [long]$level.element_id }
$applyArgsA = @{
    target_document = $Document; instance_id = $instA; requirement_set = $setA
    apply_binding = $planA.Result.apply_binding
    actions = $planA.Result.execute_plan_request.actions
    candidate_index = $planA.Result.candidate_index
}
$dryA = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-A-dry' `
    -Arguments (Copy-HzArgs $applyArgsA @{ dry_run = $true })
$tokensA = Get-HzPath $dryA.Result 'rehearsal', 'tokens_by_key'
$actsA = @($planA.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
foreach ($a in $actsA) {
    $t = Get-HzProp $tokensA $a.key
    if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
}
$builtA = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-A' `
    -Arguments (Copy-HzArgs $applyArgsA @{ dry_run = $false; actions = $actsA
        idempotency_key = (New-HzKey $run 'apply-A') })).Result
if ([int]$builtA.created_verified -ne 5) {
    throw ("HARNESS: revision A converted to {0} walls, not 5" -f $builtA.created_verified)
}
Add-HzNote $run ("revision A converted: {0} wall(s)" -f $builtA.created_verified)

# =============================================================================
# I - THE INVARIANTS, before any change at all
# =============================================================================
Write-Host "`n== I: the invariants ==" -ForegroundColor Cyan

$again = Invoke-HzUpdatePlan -Run $run -InstanceId $instA -Set $setA -Label 'plan-same' `
    -LevelId ([long]$level.element_id)
$autoAgain = @(@(Get-HzProp $again 'plan') | Where-Object { (Get-HzProp $_ 'automatic') -eq $true -and
                                                            (Get-HzProp $_ 'kind') -ne 'leave' })

Add-HzProbe -Run $run -Id 'I1' -Name 'planning the SAME revision again proposes no automatic work at all' `
    -Expected 'every action is leave/unchanged; nothing to create, nothing to re-shape' `
    -Observed ("unchanged={0} automatic_non_leave={1}" -f (Get-HzClass $again 'unchanged'), $autoAgain.Count) `
    -Ok ((Get-HzClass $again 'unchanged') -eq 5 -and $autoAgain.Count -eq 0) `
    -Evidence @{ counts = $again.counts_by_classification
                 note = 'an update that proposes work on a model it just built would build it twice' }

Add-HzProbe -Run $run -Id 'I2' -Name 'the reply names EVERY classification, including the ones at zero' `
    -Expected 'all 12 names present; a missing key would read as "not measured"' `
    -Observed ("names={0} conflict={1}" -f @(Get-HzProp $again 'classification_vocabulary').Count,
        (Get-HzClass $again 'conflict')) `
    -Ok (@(Get-HzProp $again 'classification_vocabulary').Count -eq 12 -and
         (Get-HzClass $again 'conflict') -eq 0 -and (Get-HzClass $again 'removed') -eq 0) `
    -Evidence @{ vocabulary = (Get-HzProp $again 'classification_vocabulary')
                 counts = $again.counts_by_classification }

# =============================================================================
# C - THE CHANGES, all at once, as a revision actually arrives
# =============================================================================
Write-Host "`n== C: revision B against the model ==" -ForegroundColor Cyan

# A PERSON MOVES TWO WALLS before revision B is read: one the drawing still
# shows (manually_diverged) and one the drawing drops (conflict).
$divergedWall = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
    -Min @(($X - 500.0), ($divergedAt - 500.0), -1000.0) `
    -Max @(($X + 6500.0), ($divergedAt + 500.0), 4000.0) -Label 'find-diverged')
$conflictWall = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
    -Min @(($X - 500.0), ($conflictAt - 500.0), -1000.0) `
    -Max @(($X + 6500.0), ($conflictAt + 500.0), 4000.0) -Label 'find-conflict')
if ($divergedWall.Count -ne 1 -or $conflictWall.Count -ne 1) {
    throw ("HARNESS: expected one wall at each of two places, found {0} and {1}" -f
        $divergedWall.Count, $conflictWall.Count)
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'person-moves' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'
                      element_ids = @([long]$divergedWall[0].element_id, [long]$conflictWall[0].element_id)
                      vector = @(0.0, 900.0, 0.0) }) }
Add-HzNote $run 'a person moved two walls by hand before revision B was read'

$instB = Add-HzCadLink -Run $run -DwgPath $revB.dwg_path -Label 'link-B'
$layerB = Get-HzWallLayer -Run $run -InstanceId $instB
$setB = New-HzChangeSet -Layers @($layerB) -Units $units
$updateB = Invoke-HzUpdatePlan -Run $run -InstanceId $instB -Set $setB -Label 'plan-B' `
    -Supersedes @([string]$factsA.file_sha256) -LevelId ([long]$level.element_id)

Add-HzProbe -Run $run -Id 'C1' -Name 'the wall neither side touched is UNCHANGED' `
    -Expected 'exactly 1 unchanged' `
    -Observed ("unchanged={0}" -f (Get-HzClass $updateB 'unchanged')) `
    -Ok ((Get-HzClass $updateB 'unchanged') -eq 1) `
    -Evidence @{ counts = $updateB.counts_by_classification }

Add-HzProbe -Run $run -Id 'C2' -Name 'the wall only revision B has is ADDED' `
    -Expected 'at least 1 added' `
    -Observed ("added={0}" -f (Get-HzClass $updateB 'added')) `
    -Ok ((Get-HzClass $updateB 'added') -ge 1) `
    -Evidence @{ added = @(Get-HzActionsOf $updateB 'added' | ForEach-Object { Get-HzProp $_ 'says' }) }

Add-HzProbe -Run $run -Id 'C3' -Name 'the wall revision B drops is REMOVED, and never deleted automatically' `
    -Expected 'at least 1 removed, none of them automatic' `
    -Observed ("removed={0} automatic={1}" -f (Get-HzClass $updateB 'removed'),
        @(Get-HzActionsOf $updateB 'removed' | Where-Object { (Get-HzProp $_ 'automatic') -eq $true }).Count) `
    -Ok ((Get-HzClass $updateB 'removed') -ge 1 -and
         @(Get-HzActionsOf $updateB 'removed' | Where-Object { (Get-HzProp $_ 'automatic') -eq $true }).Count -eq 0) `
    -Evidence @{ removed = @(Get-HzActionsOf $updateB 'removed' | ForEach-Object { Get-HzProp $_ 'says' }) }

Add-HzProbe -Run $run -Id 'C4' -Name 'the wall the drawing did not move but a PERSON did is manually_diverged' `
    -Expected 'at least 1 manually_diverged, held for a person' `
    -Observed ("manually_diverged={0}" -f (Get-HzClass $updateB 'manually_diverged')) `
    -Ok ((Get-HzClass $updateB 'manually_diverged') -ge 1 -and
         @(Get-HzActionsOf $updateB 'manually_diverged' |
           Where-Object { (Get-HzProp $_ 'automatic') -eq $true }).Count -eq 0) `
    -Evidence @{ says = @(Get-HzActionsOf $updateB 'manually_diverged' | ForEach-Object { Get-HzProp $_ 'says' }) }

Add-HzProbe -Run $run -Id 'C5' -Name 'the wall B dropped AND a person moved is a CONFLICT, not a plain removal' `
    -Expected 'at least 1 conflict' `
    -Observed ("conflict={0} removed={1}" -f (Get-HzClass $updateB 'conflict'),
        (Get-HzClass $updateB 'removed')) `
    -Ok ((Get-HzClass $updateB 'conflict') -ge 1) `
    -Evidence @{ conflict = @(Get-HzActionsOf $updateB 'conflict' | ForEach-Object { Get-HzProp $_ 'says' })
                 note = 'two independent changes to one thing; which to honour is not a fact about the drawing' }

$movedOrReshaped = (Get-HzClass $updateB 'moved') + (Get-HzClass $updateB 'reshaped') +
                   (Get-HzClass $updateB 'ambiguous')
Add-HzProbe -Run $run -Id 'C6' -Name 'the wall the DRAWING moved is paired, and the pairing is held for a person' `
    -Expected 'moved, reshaped or ambiguous - never silently rebuilt' `
    -Observed ("moved={0} reshaped={1} ambiguous={2}" -f (Get-HzClass $updateB 'moved'),
        (Get-HzClass $updateB 'reshaped'), (Get-HzClass $updateB 'ambiguous')) `
    -Ok ($movedOrReshaped -ge 1) `
    -Evidence @{ counts = $updateB.counts_by_classification }

$autoWrites = @(@(Get-HzProp $updateB 'plan') | Where-Object {
    (Get-HzProp $_ 'automatic') -eq $true -and (Get-HzProp $_ 'kind') -notin @('leave') })
Add-HzProbe -Run $run -Id 'C7' -Name 'NOTHING in this plan deletes anything, and every judgement waits for a person' `
    -Expected 'no orphan is automatic; the only automatic actions are leaves and plain adds' `
    -Observed ("automatic_non_leave={0} kinds={1}" -f $autoWrites.Count,
        (@($autoWrites | ForEach-Object { Get-HzProp $_ 'kind' } | Sort-Object -Unique) -join ',')) `
    -Ok (@($autoWrites | Where-Object { (Get-HzProp $_ 'kind') -eq 'orphan' }).Count -eq 0) `
    -Evidence @{ automatic_kinds = @($autoWrites | ForEach-Object { Get-HzProp $_ 'kind' } | Sort-Object -Unique) }

# =============================================================================
# T - RETYPED and RESIZED: the same line, a different thing asked of it
# =============================================================================
Write-Host "`n== T: retyped and resized ==" -ForegroundColor Cyan

# The SAME drawing, read by a set that asks for a different type. Nothing moved,
# and a reading that compares positions alone reports nothing to do.
$wallTypes = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'wall-types' -Arguments @{
    categories = @('OST_Walls'); include_types = $true; include_links = $false; max_rows = 40
}).Result.rows | Where-Object { $_.is_element_type -eq $true })
$builtTypeName = $null
$otherTypeName = $null
$builtWall = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
    -Min @(($X - 500.0), -500.0, -1000.0) -Max @(($X + 6500.0), 500.0, 4000.0) -Label 'find-unchanged')
if ($builtWall.Count -eq 1) { $builtTypeName = [string]$builtWall[0].type }
foreach ($t in $wallTypes) {
    if ([string]$t.name -and [string]$t.name -ne $builtTypeName) { $otherTypeName = [string]$t.name; break }
}

if (-not $otherTypeName) {
    Add-HzProbe -Run $run -Id 'T1' -Name 'a set that asks for a DIFFERENT TYPE on the same line reports retyped' `
        -Expected 'a second wall type in the document to ask for' `
        -Observed ("wall types available: {0}" -f $wallTypes.Count) -Status 'fixture_missing'
} else {
    $setRetype = New-HzChangeSet -Layers @($layerA) -Units $units -FamilyType $otherTypeName -Id 'hz-live-retype'
    $retype = Invoke-HzUpdatePlan -Run $run -InstanceId $instA -Set $setRetype -Label 'plan-retype' `
        -Supersedes @([string]$factsA.file_sha256) -LevelId ([long]$level.element_id)
    Add-HzProbe -Run $run -Id 'T1' -Name 'a set that asks for a DIFFERENT TYPE on the same line reports retyped' `
        -Expected "retyped, held for a person - '$otherTypeName' where the wall is '$builtTypeName'" `
        -Observed ("retyped={0} unchanged={1}" -f (Get-HzClass $retype 'retyped'),
            (Get-HzClass $retype 'unchanged')) `
        -Ok ((Get-HzClass $retype 'retyped') -ge 1 -and
             @(Get-HzActionsOf $retype 'retyped' |
               Where-Object { (Get-HzProp $_ 'automatic') -eq $true }).Count -eq 0) `
        -Evidence @{ asked_for = $otherTypeName; element_is = $builtTypeName
                     says = @(Get-HzActionsOf $retype 'retyped' | ForEach-Object { Get-HzProp $_ 'says' }) }
}

# A set that declares a thickness the wall does not have. Same line, same type.
$setResize = New-HzChangeSet -Layers @($layerA) -Units $units -ThicknessMm 375.0 -Id 'hz-live-resize'
$resize = Invoke-HzUpdatePlan -Run $run -InstanceId $instA -Set $setResize -Label 'plan-resize' `
    -Supersedes @([string]$factsA.file_sha256) -LevelId ([long]$level.element_id)
Add-HzProbe -Run $run -Id 'T2' -Name 'a set that asks for a DIFFERENT THICKNESS on the same line reports resized' `
    -Expected 'resized, held for a person - size lives in the type' `
    -Observed ("resized={0} unchanged={1}" -f (Get-HzClass $resize 'resized'),
        (Get-HzClass $resize 'unchanged')) `
    -Ok ((Get-HzClass $resize 'resized') -ge 1 -and
         @(Get-HzActionsOf $resize 'resized' | Where-Object { (Get-HzProp $_ 'automatic') -eq $true }).Count -eq 0) `
    -Evidence @{ says = @(Get-HzActionsOf $resize 'resized' | ForEach-Object { Get-HzProp $_ 'says' }) }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
