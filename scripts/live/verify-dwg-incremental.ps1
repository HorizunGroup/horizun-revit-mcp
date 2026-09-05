#Requires -Version 5.1
<#
  DWG-3, LIVE: the thirteen steps of an incremental update.

    1  a clean model
    2  link revision A
    3  plan and apply it - four walls, four stamps carrying as-built geometry
    4  the audit agrees
    5  A PERSON EDITS THE BIM: one element is moved, and the drawing does not say so
    6  link revision B (W1 same, W2 same, W3 moved 700, W4 gone, W5 new)
    7  plan the update: leave / review / create / orphan, and a pairing OFFERED
    8  apply the automatic half, and prove the person's edit survived it
    9  accept the pairing, prove the moved wall keeps its ELEMENT ID, and prove
       that planning the SAME revision again has nothing left to build
   10  ONE FILE PLACED TWICE: an update for one placement claims only its own
       four walls and never orphans the other wing's (backlog 8.4d)
   11  A MOVED PLACEMENT: refused with the delta; accepted, the untouched walls
       FOLLOW the drawing and keep their ids; planned again, nothing moves
   12  A MISSING SOURCE FILE: named as source_file_missing, and a run that can
       claim nothing refuses with scope_unidentified instead of "0 changes"
       (backlog 8.4c)
   13  PROVENANCE v1 -> v2, the migration itself, on records that carry no
       placement id at all: claimed and rewritten under one placement, REFUSED
       as ambiguous_v1 under two (before anything is modified, naming both),
       claimed by fingerprint alone on an IMPORTED drawing with no path or hash,
       claimed by stated lineage when the source file has gone, a person's edit
       still review across the rewrite, and the migrating apply replaying under
       its own idempotency_key

  Steps 1-12 measure records THIS build wrote, which are all v2. Step 13 is the
  only place the v1 -> v2 migration is exercised at all, and it needs a genuine
  v1 record to exercise it against: see the note above step 13 for how one is
  produced, and for exactly what that does and does not establish.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-incremental' -Document $Document

function Get-HzKind {
    param($Plan, [string]$Kind)
    $c = Get-HzPath $Plan 'counts_by_kind', $Kind
    if ($null -eq $c) { 0 } else { [int]$c }
}

# =============================================================================
# 1-2. a clean model, and two revisions of one drawing
# =============================================================================
Write-Host "`n== 1-2. two revisions of one drawing ==" -ForegroundColor Cyan
$X = 900000.0
# W1 stays, W2 stays (a PERSON will move its element), W3 moves, W4 goes, W5 arrives.
$revA = @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0;     x2 = ($X + 6000); y2 = 0.0 },
    @{ name = 'W2'; x1 = $X; y1 = 4000.0;  x2 = ($X + 6000); y2 = 4000.0 },
    @{ name = 'W3'; x1 = $X; y1 = 8000.0;  x2 = ($X + 6000); y2 = 8000.0 },
    @{ name = 'W4'; x1 = $X; y1 = 12000.0; x2 = ($X + 6000); y2 = 12000.0 }
)
$revB = @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0;     x2 = ($X + 6000); y2 = 0.0 },
    @{ name = 'W2'; x1 = $X; y1 = 4000.0;  x2 = ($X + 6000); y2 = 4000.0 },
    @{ name = 'W3'; x1 = $X; y1 = 8700.0;  x2 = ($X + 6000); y2 = 8700.0 },
    @{ name = 'W5'; x1 = $X; y1 = 16000.0; x2 = ($X + 6000); y2 = 16000.0 }
)

$null = Reset-HzDocument $run
$fixA = New-HzWallFixture -Run $run -Walls $revA -Tag 'revA'
$null = Reset-HzDocument $run
$fixB = New-HzWallFixture -Run $run -Walls $revB -Tag 'revB'
$null = Reset-HzDocument $run

$run.Fixture['fixture_id'] = 'hz-ab-' + $run.RunId
$run.Fixture['revision_a'] = @{ dwg_name = $fixA.dwg_name; dwg_sha256 = $fixA.dwg_sha256; walls = $revA.Count }
$run.Fixture['revision_b'] = @{ dwg_name = $fixB.dwg_name; dwg_sha256 = $fixB.dwg_sha256; walls = $revB.Count }
$run.Fixture['dwg_sha256'] = $fixB.dwg_sha256
$run.Expected['changes'] = @(
    'W1 unchanged', 'W2 unchanged in the drawing - a person moves its element',
    'W3 moved 700 mm', 'W4 deleted from the drawing', 'W5 added')
Add-HzNote $run ("A {0} / B {1}" -f $fixA.dwg_sha256.Substring(0, 12), $fixB.dwg_sha256.Substring(0, 12))

$wallsAtStart = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-start'

# =============================================================================
# 3. build revision A
# =============================================================================
Write-Host "`n== 3. revision A, planned and applied ==" -ForegroundColor Cyan
$instA = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-A'
$layer = Get-HzWallLayer -Run $run -InstanceId $instA
$factsA = Get-HzCadInstanceFacts -Run $run -InstanceId $instA
$set = New-HzWallRequirementSet -Layer $layer -Units ([string]$factsA.declared_units) -Id 'hz-live-incremental'
$run.Fixture['wall_layer'] = $layer
$run.Fixture['requirement_set_id'] = $set.requirement_set.id
$level = Get-HzFirstLevel $run

$planA = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-A' -Arguments @{
    target_document = $Document; instance_id = $instA; requirement_set = $set; level_id = [long]$level.element_id }
$applyArgs = @{
    target_document = $Document; instance_id = $instA; requirement_set = $set
    apply_binding = $planA.Result.apply_binding
    actions = $planA.Result.execute_plan_request.actions
    candidate_index = $planA.Result.candidate_index
}
$dryA = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-A-dry' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
$tokens = Get-HzPath $dryA.Result 'rehearsal', 'tokens_by_key'
$actsA = @($planA.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
foreach ($a in $actsA) {
    $t = Get-HzProp $tokens $a.key
    if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
}
$applyA = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-A' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $actsA; idempotency_key = (New-HzKey $run 'applyA') })

Add-HzProbe -Run $run -Id 'S3' -Name 'revision A builds four walls, each stamped with what it was BUILT with' `
    -Expected '4 created and verified, 4 provenance rows, 0 anonymous' `
    -Observed ("created={0} provenance={1} anonymous={2}" -f
        $applyA.Result.created_verified, $applyA.Result.provenance_written, $applyA.Result.elements_left_anonymous) `
    -Ok ([int]$applyA.Result.created_verified -eq 4 -and [int]$applyA.Result.provenance_written -eq 4 -and
         [int]$applyA.Result.elements_left_anonymous -eq 0) `
    -Evidence @{ state = $applyA.Result.state; provenance = $applyA.Result.provenance }
if ([int]$applyA.Result.created_verified -ne 4) { throw 'HARNESS: revision A did not build' }
$builtA = @($applyA.Result.provenance | ForEach-Object { [long]$_.element_id })

# =============================================================================
# 4. the audit agrees
# =============================================================================
Write-Host "`n== 4. the audit agrees with revision A ==" -ForegroundColor Cyan
$auditA = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-A' -Arguments @{
    target_document = $Document; instance_id = $instA; requirement_set = $set }
Add-HzProbe -Run $run -Id 'S4' -Name 'the model built from revision A agrees with revision A' `
    -Expected '4 matched by revision, nothing needing a decision' `
    -Observed ("by_revision={0} blocking={1} review={2}" -f $auditA.Result.matched.by_revision,
        $auditA.Result.counts_by_severity.blocking, $auditA.Result.counts_by_severity.review) `
    -Ok ([int]$auditA.Result.matched.by_revision -eq 4 -and
         [int]$auditA.Result.counts_by_severity.blocking -eq 0 -and
         [int]$auditA.Result.counts_by_severity.review -eq 0) `
    -Evidence @{ matched = $auditA.Result.matched }

# =============================================================================
# 5. A PERSON EDITS THE BIM
# =============================================================================
Write-Host "`n== 5. a person moves W2, and the drawing does not say so ==" -ForegroundColor Cyan
# W2 IS THE WALL AT y = 4000, and it is found BY ITS GEOMETRY.
#
# Element ids come back in creation order, which is the DRAWING's order, not the
# order of the walls on the page. A version of this probe that took "the second
# id" moved W3 - the wall revision B ALSO moves - and so measured the one case
# it exists to exclude.
$near = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
    -Min @(($X - 500.0), 3500.0, -1000.0) -Max @(($X + 6500.0), 4500.0, 4000.0) -Label 'find-w2')
$w2 = @($near | Where-Object { $builtA -contains [long]$_.element_id })
if ($w2.Count -ne 1) {
    throw ("HARNESS: expected exactly one built wall around y=4000 and found {0}" -f $w2.Count)
}
$w2Id = [long]$w2[0].element_id
$w2Unique = [string]$w2[0].unique_id
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'person-move' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = @($w2Id); vector = @(0.0, 250.0, 0.0) })
}
Add-HzNote $run ("a person moved element {0} (unique {1}) by 250 mm" -f $w2Id, $w2Unique.Substring(0, 8))

# =============================================================================
# 6-7. revision B, and what the update proposes
# =============================================================================
Write-Host "`n== 6-7. revision B ==" -ForegroundColor Cyan
$instB = Add-HzCadLink -Run $run -DwgPath $fixB.dwg_path -Label 'link-B'

$blind = Invoke-HzTool -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-blind' -Arguments @{
    target_document = $Document; instance_id = $instB; requirement_set = $set; level_id = [long]$level.element_id }
Add-HzRefusalProbe -Run $run -Id 'S6' -Name 'an update that is not told what it supersedes REFUSES rather than duplicating the building' `
    -Call $blind -MustMatch 'supersedes_unstated'

$updArgs = @{
    target_document = $Document; instance_id = $instB; requirement_set = $set
    level_id = [long]$level.element_id; supersedes_sha256 = @($fixA.dwg_sha256)
}
$upd = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update' -Arguments $updArgs

Add-HzProbe -Run $run -Id 'S7a' -Name 'the update reads revision B and finds the four outcomes' `
    -Expected 'leave>=1, review>=1 (the person edit), create>=1 (W5), orphan>=1 (W4 and W3-as-moved)' `
    -Observed ("leave={0} review={1} create={2} orphan={3} candidates={4}" -f
        (Get-HzKind $upd.Result 'leave'), (Get-HzKind $upd.Result 'review'),
        (Get-HzKind $upd.Result 'create'), (Get-HzKind $upd.Result 'orphan'),
        $upd.Result.revision_b.candidates) `
    -Ok ((Get-HzKind $upd.Result 'leave') -ge 1 -and (Get-HzKind $upd.Result 'review') -ge 1 -and
         (Get-HzKind $upd.Result 'create') -ge 1 -and (Get-HzKind $upd.Result 'orphan') -ge 1) `
    -Evidence @{ counts_by_kind = $upd.Result.counts_by_kind; lineage = $upd.Result.lineage }

$review = @($upd.Result.plan | Where-Object { $_.kind -eq 'review' })
$actionsText = ($upd.Result.actions | ConvertTo-Json -Depth 20 -Compress)
Add-HzProbe -Run $run -Id 'S7b' -Name "the person's edit is REVIEW, never an action, and names them as the mover" `
    -Expected 'the moved element in review, absent from the actions, saying A PERSON MOVED THIS' `
    -Observed ("review_ids={0} moved_id={1} named_in_actions={2}" -f
        ((@($review | ForEach-Object { $_.element_id })) -join ','), $w2Id,
        [bool]($actionsText -match [string]$w2Id)) `
    -Ok (@($review | Where-Object { [long]$_.element_id -eq $w2Id }).Count -eq 1 -and
         ($actionsText -notmatch [string]$w2Id) -and
         ([string]$review[0].says -match 'A PERSON MOVED THIS')) `
    -Evidence @{ review = $review }

$offered = @($upd.Result.pairings_offered | Where-Object { $_ })
$moveActions = @($upd.Result.actions | Where-Object { $_.key -match 'move' })
Add-HzProbe -Run $run -Id 'S7c' -Name 'the wall that MOVED in the drawing is offered as a pairing, not taken as one' `
    -Expected 'at least one pairing offered with what it was judged on; no set_curve action' `
    -Observed ("offered={0} paired_on={1} move_actions={2}" -f $offered.Count,
        $(if ($offered.Count) { Limit-HzText ([string]$offered[0].paired_on) 90 } else { '-' }), $moveActions.Count) `
    -Ok ($offered.Count -ge 1 -and -not [string]::IsNullOrWhiteSpace([string]$offered[0].paired_on) -and
         $moveActions.Count -eq 0) `
    -Evidence @{ pairings_offered = $offered }

# =============================================================================
# 8. apply the automatic half
# =============================================================================
Write-Host "`n== 8. apply the automatic half ==" -ForegroundColor Cyan
$wallsBefore8 = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-before-8'
$applyUpd = @{
    target_document = $Document; actions = $upd.Result.actions
    provenance = $upd.Result.provenance; candidate_index = $upd.Result.candidate_index
}
$dry8 = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-upd-dry' `
    -Arguments (Copy-HzArgs $applyUpd @{ dry_run = $true })
$done8 = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-upd' `
    -Arguments (Copy-HzArgs $applyUpd @{ dry_run = $false; idempotency_key = (New-HzKey $run 'upd8') })
$wallsAfter8 = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after-8'

Add-HzProbe -Run $run -Id 'S8a' -Name 'the automatic half builds what revision B ADDS, holds what might be a move, and stamps what it built' `
    -Expected ("{0} walls - W5 built, the moved wall HELD - and every new element stamped" -f ($wallsBefore8 + 1)) `
    -Observed ("before={0} after={1} state={2} stamped={3} anonymous={4}" -f $wallsBefore8, $wallsAfter8,
        $done8.Result.state, $done8.Result.provenance_written, $done8.Result.elements_left_anonymous) `
    -Ok ($wallsAfter8 -eq ($wallsBefore8 + 1) -and [string]$done8.Result.state -eq 'applied' -and
         [int]$done8.Result.provenance_written -ge 1 -and [int]$done8.Result.elements_left_anonymous -eq 0) `
    -Evidence @{ state = $done8.Result.state; provenance = $done8.Result.provenance; actions = $done8.Result.actions }

$survivor = @(Get-HzElements -Run $run -Categories @('OST_Walls') -WithBox -Label 'walls-survivor' |
    Where-Object { [string]$_.unique_id -eq $w2Unique })
Add-HzProbe -Run $run -Id 'S8b' -Name "the person's edit SURVIVED the update" `
    -Expected "the element the person moved is still there, found by its UniqueId" `
    -Observed ("present={0}" -f $survivor.Count) -Ok ($survivor.Count -eq 1) `
    -Evidence @{ unique_id = $w2Unique
                 note = 'the update never emitted an action naming it; this measures the model afterwards' }

# =============================================================================
# 9. accept the pairing
# =============================================================================
Write-Host "`n== 9. accept the pairing ==" -ForegroundColor Cyan
$updB = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-2' -Arguments $updArgs
$pair = @($updB.Result.pairings_offered | Where-Object { $_ })[0]
if ($null -eq $pair) { throw 'HARNESS: no pairing offered on the second pass; step 9 cannot run' }
$pairId = [long]$pair.element_id

$accepted = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-accepted' `
    -Arguments (Copy-HzArgs $updArgs @{
        accept_pairings = @(@{ element_id = $pairId; candidate_id = [string]$pair.candidate_id }) })

Add-HzProbe -Run $run -Id 'S9a' -Name 'an accepted pairing becomes ONE set_curve, and the duplicate create disappears' `
    -Expected 'set_curve>=1 naming the element, its create paired away, nothing rejected' `
    -Observed ("set_curve={0} paired_away={1} rejected={2}" -f
        (Get-HzKind $accepted.Result 'set_curve'), (Get-HzKind $accepted.Result 'paired_away'),
        @($accepted.Result.pairings_rejected).Count) `
    -Ok ((Get-HzKind $accepted.Result 'set_curve') -ge 1 -and (Get-HzKind $accepted.Result 'paired_away') -ge 1 -and
         @($accepted.Result.pairings_rejected).Count -eq 0) `
    -Evidence @{ counts_by_kind = $accepted.Result.counts_by_kind }

$before9 = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-before-9'
$apply9 = @{
    target_document = $Document; actions = $accepted.Result.actions
    provenance = $accepted.Result.provenance; candidate_index = $accepted.Result.candidate_index
}
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-9-dry' `
    -Arguments (Copy-HzArgs $apply9 @{ dry_run = $true })
$done9 = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-9' `
    -Arguments (Copy-HzArgs $apply9 @{ dry_run = $false; idempotency_key = (New-HzKey $run 'upd9') })
$after9 = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after-9'
$stillThere = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls-check-9' |
    Where-Object { [long]$_.element_id -eq $pairId })

Add-HzProbe -Run $run -Id 'S9b' -Name 'the moved wall is RE-SHAPED: same element id, no new wall, and it is re-stamped' `
    -Expected ("element {0} still exists, wall count unchanged at {1}" -f $pairId, $before9) `
    -Observed ("before={0} after={1} present={2} state={3} stamped={4}" -f $before9, $after9,
        $stillThere.Count, $done9.Result.state, $done9.Result.provenance_written) `
    -Ok ($stillThere.Count -eq 1 -and $after9 -eq $before9 -and
         [string]$done9.Result.state -eq 'applied' -and [int]$done9.Result.provenance_written -ge 1) `
    -Evidence @{ state = $done9.Result.state; element_id = $pairId; provenance = $done9.Result.provenance }

# THE IDEMPOTENCE THAT MATTERS. Without a stamp on what step 8 created, this is
# where a second copy of the building appears.
$again = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-again' -Arguments $updArgs
Add-HzProbe -Run $run -Id 'S9d' -Name 'planning the SAME revision again has nothing left to build' `
    -Expected 'no automatic action left' `
    -Observed ("automatic={0} create={1} set_curve={2} leave={3}" -f $again.Result.automatic,
        (Get-HzKind $again.Result 'create'), (Get-HzKind $again.Result 'set_curve'), (Get-HzKind $again.Result 'leave')) `
    -Ok ([int]$again.Result.automatic -eq 0) `
    -Evidence @{ counts_by_kind = $again.Result.counts_by_kind; actions = $again.Result.actions }

$auditFinal = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-final' -Arguments @{
    target_document = $Document; instance_id = $instB; requirement_set = $set }
Add-HzProbe -Run $run -Id 'S9c' -Name 'after the update the audit still tells the truth about what is left' `
    -Expected 'the person-moved element still reported; nothing silently reconciled' `
    -Observed ("blocking={0} review={1} agrees={2}" -f $auditFinal.Result.counts_by_severity.blocking,
        $auditFinal.Result.counts_by_severity.review, $auditFinal.Result.agrees) `
    -Ok ($auditFinal.Result.agrees -eq $false) `
    -Evidence @{ counts_by_code = $auditFinal.Result.counts_by_code }

# =============================================================================
# 10. ONE FILE PLACED TWICE (backlog 8.4d)
# =============================================================================
Write-Host "`n== 10. one file placed twice: scope is per PLACEMENT ==" -ForegroundColor Cyan

# A first conversion of one placement, through the same two calls step 3 used.
function Invoke-HzFirstConversion {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long]$InstanceId,
          [Parameter(Mandatory)]$Set, [Parameter(Mandatory)][long]$LevelId, [Parameter(Mandatory)][string]$Label)
    $plan = Invoke-HzToolStrict -Run $Run -Tool 'horizun_plan_from_cad' -Label ("plan-" + $Label) -Arguments @{
        target_document = $Run.Document; instance_id = $InstanceId; requirement_set = $Set; level_id = $LevelId }
    $conv = @{
        target_document = $Run.Document; instance_id = $InstanceId; requirement_set = $Set
        apply_binding = $plan.Result.apply_binding
        actions = $plan.Result.execute_plan_request.actions
        candidate_index = $plan.Result.candidate_index
    }
    $dry = Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label ("apply-" + $Label + "-dry") `
        -Arguments (Copy-HzArgs $conv @{ dry_run = $true })
    $tok = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
    $acts = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $acts) {
        $t = Get-HzProp $tok $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label ("apply-" + $Label) `
        -Arguments (Copy-HzArgs $conv @{ dry_run = $false; actions = $acts; idempotency_key = (New-HzKey $Run $Label) })
}

$null = Reset-HzDocument $run
$level10 = Get-HzFirstLevel $run
$wingA = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-wing-A'
$wingB = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-wing-B' -AllowDuplicate
# The second placement is the same file 40 m away - a repeated wing. A typed
# CAD link arrives PINNED (measured 2026-09-03: the move rolled back on
# "Can't move pinned element"), so it is unpinned first, through the same tool.
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'unpin-wing-B' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'unpin'; element_ids = @($wingB) })
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'place-wing-B' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = @($wingB); vector = @(0.0, 40000.0, 0.0) })
}
$factsWingA = Get-HzCadInstanceFacts -Run $run -InstanceId $wingA
$factsWingB = Get-HzCadInstanceFacts -Run $run -InstanceId $wingB
$builtWingA = Invoke-HzFirstConversion -Run $run -InstanceId $wingA -Set $set -LevelId ([long]$level10.element_id) -Label 'wingA'
$builtWingB = Invoke-HzFirstConversion -Run $run -InstanceId $wingB -Set $set -LevelId ([long]$level10.element_id) -Label 'wingB'
$idsWingA = @($builtWingA.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
$idsWingB = @($builtWingB.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
if ($idsWingA.Count -ne 4 -or $idsWingB.Count -ne 4) {
    throw ("HARNESS: the two wings did not build 4+4 stamped walls ({0}+{1})" -f $idsWingA.Count, $idsWingB.Count)
}
Add-HzNote $run ("wing A instance {0} [{1}], wing B instance {2} [{3}]" -f $wingA, $factsWingA.unique_id, $wingB, $factsWingB.unique_id)

$updWingA = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-wing-A' -Arguments @{
    target_document = $Document; instance_id = $wingA; requirement_set = $set; level_id = [long]$level10.element_id }
$planTextA = ($updWingA.Result.plan | ConvertTo-Json -Depth 20 -Compress)
$namesWingB = @($idsWingB | Where-Object { $planTextA -match ('"element_id":' + [string]$_ + '\b') })
Add-HzProbe -Run $run -Id 'S10a' -Name 'an update for wing A claims ONLY wing A: same file, same hash, other placement untouched' `
    -Expected 'scope.claimed=4, scope.other_placement=4, leave=4, orphan=0, no plan row naming a wing-B element' `
    -Observed ("claimed={0} other_placement={1} leave={2} orphan={3} wingB_named={4} identity={5}" -f
        (Get-HzPath $updWingA.Result 'scope', 'claimed'), (Get-HzPath $updWingA.Result 'scope', 'other_placement'),
        (Get-HzKind $updWingA.Result 'leave'), (Get-HzKind $updWingA.Result 'orphan'), $namesWingB.Count,
        (Get-HzPath $updWingA.Result 'source', 'identity', 'mode')) `
    -Ok ([int](Get-HzPath $updWingA.Result 'scope', 'claimed') -eq 4 -and
         [int](Get-HzPath $updWingA.Result 'scope', 'other_placement') -eq 4 -and
         (Get-HzKind $updWingA.Result 'leave') -eq 4 -and (Get-HzKind $updWingA.Result 'orphan') -eq 0 -and
         $namesWingB.Count -eq 0 -and
         [string](Get-HzPath $updWingA.Result 'placement', 'id') -eq [string]$factsWingA.unique_id) `
    -Evidence @{ scope = $updWingA.Result.scope; placement = $updWingA.Result.placement; counts_by_kind = $updWingA.Result.counts_by_kind }

$updWingB = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-wing-B' -Arguments @{
    target_document = $Document; instance_id = $wingB; requirement_set = $set; level_id = [long]$level10.element_id }
Add-HzProbe -Run $run -Id 'S10b' -Name 'and the same from wing B: symmetric, nothing of wing A claimed or orphaned' `
    -Expected 'scope.claimed=4, scope.other_placement=4, orphan=0, automatic=0' `
    -Observed ("claimed={0} other_placement={1} orphan={2} automatic={3}" -f
        (Get-HzPath $updWingB.Result 'scope', 'claimed'), (Get-HzPath $updWingB.Result 'scope', 'other_placement'),
        (Get-HzKind $updWingB.Result 'orphan'), $updWingB.Result.automatic) `
    -Ok ([int](Get-HzPath $updWingB.Result 'scope', 'claimed') -eq 4 -and
         [int](Get-HzPath $updWingB.Result 'scope', 'other_placement') -eq 4 -and
         (Get-HzKind $updWingB.Result 'orphan') -eq 0 -and [int]$updWingB.Result.automatic -eq 0) `
    -Evidence @{ scope = $updWingB.Result.scope }

# =============================================================================
# 11. A MOVED PLACEMENT
# =============================================================================
Write-Host "`n== 11. the placement moves: refused, then accepted, then quiet ==" -ForegroundColor Cyan
# The link is pinned as it arrives (see step 10); unpin before the move.
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'unpin-wing-A' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'unpin'; element_ids = @($wingA) })
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'move-wing-A' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = @($wingA); vector = @(300.0, 0.0, 0.0) })
}
$movedCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-moved-blind' -Arguments @{
    target_document = $Document; instance_id = $wingA; requirement_set = $set; level_id = [long]$level10.element_id }
Add-HzRefusalProbe -Run $run -Id 'S11a' -Name 'a placement that moved since its walls were built is REFUSED with the delta, not re-matched as a changed drawing' `
    -Call $movedCall -MustMatch 'placement_moved.*300'

$followArgs = @{
    target_document = $Document; instance_id = $wingA; requirement_set = $set
    level_id = [long]$level10.element_id; accept_placement_move = $true
}
$follow = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-moved-accepted' -Arguments $followArgs
$followIds = @($follow.Result.plan | Where-Object { $_.kind -eq 'set_curve' } | ForEach-Object { [long]$_.element_id })
Add-HzProbe -Run $run -Id 'S11b' -Name 'accepted, the four untouched walls FOLLOW the drawing as set_curve on their own ids; nothing created, nothing orphaned' `
    -Expected 'set_curve=4 naming the wing-A elements, create=0, orphan=0, placement_moved.accepted=true, delta 300 mm' `
    -Observed ("set_curve={0} create={1} orphan={2} accepted={3} delta={4}" -f
        (Get-HzKind $follow.Result 'set_curve'), (Get-HzKind $follow.Result 'create'), (Get-HzKind $follow.Result 'orphan'),
        (Get-HzPath $follow.Result 'placement_moved', 'accepted'),
        ((@(Get-HzPath $follow.Result 'placement_moved', 'move', 'delta_mm')) -join ',')) `
    -Ok ((Get-HzKind $follow.Result 'set_curve') -eq 4 -and (Get-HzKind $follow.Result 'create') -eq 0 -and
         (Get-HzKind $follow.Result 'orphan') -eq 0 -and
         @($followIds | Where-Object { $idsWingA -contains $_ }).Count -eq 4 -and
         [math]::Abs([double](@(Get-HzPath $follow.Result 'placement_moved', 'move', 'delta_mm'))[0] - 300.0) -lt 1.0) `
    -Evidence @{ placement_moved = $follow.Result.placement_moved; counts_by_kind = $follow.Result.counts_by_kind }

$applyFollow = @{
    target_document = $Document; actions = $follow.Result.actions
    provenance = $follow.Result.provenance; candidate_index = $follow.Result.candidate_index
}
$blindApply = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-moved-blind' `
    -Arguments (Copy-HzArgs $applyFollow @{ dry_run = $true })
Add-HzRefusalProbe -Run $run -Id 'S11c' -Name 'the apply asks for the same consent again: the plan was read-only, the write is not' `
    -Call $blindApply -MustMatch 'placement_moved'
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-moved-dry' `
    -Arguments (Copy-HzArgs $applyFollow @{ dry_run = $true; accept_placement_move = $true })
# ONE key, used twice on purpose: New-HzKey is monotonic, and the replay probe
# below is about the SAME key arriving again.
$followKey = New-HzKey $run 'follow11'
$doneFollow = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-moved' `
    -Arguments (Copy-HzArgs $applyFollow @{ dry_run = $false; accept_placement_move = $true; idempotency_key = $followKey })
$stillA = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls-after-follow' |
    Where-Object { $idsWingA -contains [long]$_.element_id })
$quiet = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-moved-again' -Arguments @{
    target_document = $Document; instance_id = $wingA; requirement_set = $set; level_id = [long]$level10.element_id }
Add-HzProbe -Run $run -Id 'S11d' -Name 'applied, the walls keep their ids and are re-stamped under the new transform; planned again, nothing has moved' `
    -Expected 'state applied, 4 stamped, 4 wing-A ids still present; second plan: placement_moved null, leave=4, automatic=0' `
    -Observed ("state={0} stamped={1} present={2} again_moved={3} again_leave={4} again_automatic={5}" -f
        $doneFollow.Result.state, $doneFollow.Result.provenance_written, $stillA.Count,
        $(if ($null -eq $quiet.Result.placement_moved) { 'null' } else { 'SET' }),
        (Get-HzKind $quiet.Result 'leave'), $quiet.Result.automatic) `
    -Ok ([string]$doneFollow.Result.state -eq 'applied' -and [int]$doneFollow.Result.provenance_written -ge 4 -and
         $stillA.Count -eq 4 -and $null -eq $quiet.Result.placement_moved -and
         (Get-HzKind $quiet.Result 'leave') -eq 4 -and [int]$quiet.Result.automatic -eq 0) `
    -Evidence @{ apply = $doneFollow.Result.provenance; again = $quiet.Result.counts_by_kind }

$replay = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-moved-replay' `
    -Arguments (Copy-HzArgs $applyFollow @{ dry_run = $false; accept_placement_move = $true; idempotency_key = $followKey })
# THE DISPATCHER REPLAYS FIRST. An identical call under the same key is answered
# from the durable idempotency ledger before the command runs - measured
# 2026-09-03: idempotency.status = replayed, command_executed_in_this_call =
# false, and the echoed body still carries the ORIGINAL replayed=false. The
# command's own ledger only sees a call the dispatcher let through, so the
# probe reads the dispatcher's verdict and accepts either signal.
$replayIdem = Get-HzProp $replay.Result 'idempotency'
$replayStatus = if ($replayIdem) { [string](Get-HzProp $replayIdem 'status') } else { '' }
$replayExecuted = if ($replayIdem) { Get-HzProp $replayIdem 'command_executed_in_this_call' } else { $null }
$replayedFlag = Get-HzProp $replay.Result 'replayed'
$replayOk = ($replayedFlag -eq $true) -or ($replayStatus -eq 'replayed' -and $replayExecuted -eq $false)
Add-HzProbe -Run $run -Id 'S11e' -Name 'the same apply under the same idempotency_key REPLAYS and runs nothing' `
    -Expected 'the dispatcher ledger answers replayed with command_executed_in_this_call=false, or the command reports replayed=true; nothing runs' `
    -Observed ("replayed={0} idempotency.status={1} executed_in_this_call={2}" -f $replayedFlag, $replayStatus, $replayExecuted) `
    -Ok $replayOk `
    -Evidence @{ idempotency = $replayIdem; replayed = $replayedFlag }

# =============================================================================
# 12. A MISSING SOURCE FILE (backlog 8.4c)
# =============================================================================
Write-Host "`n== 12. the source file goes missing ==" -ForegroundColor Cyan
$goneDir = Join-Path $run.WorkDir 'gone'
$null = New-Item -ItemType Directory -Force -Path $goneDir
$gonePath = Join-Path $goneDir ('revA-' + $run.RunId + '.dwg')
Copy-Item -LiteralPath $fixA.dwg_path -Destination $gonePath -Force
$gone = Add-HzCadLink -Run $run -DwgPath $gonePath -Label 'link-gone'
Remove-Item -LiteralPath $gonePath -Force
$factsGone = Get-HzCadInstanceFacts -Run $run -InstanceId $gone
Add-HzProbe -Run $run -Id 'S12a' -Name 'a link whose file has gone is measured as such: path recorded, no hash, file_error' `
    -Expected 'file_sha256 null and file_error naming the path' `
    -Observed ("sha={0} error={1}" -f $(if ($factsGone.file_sha256) { 'set' } else { 'null' }),
        (Limit-HzText ([string](Get-HzProp $factsGone 'file_error')) 120)) `
    -Ok ($null -eq $factsGone.file_sha256 -and [string](Get-HzProp $factsGone 'file_error') -match 'not on this machine') `
    -Evidence @{ facts = $factsGone }

$goneCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-gone' -Arguments @{
    target_document = $Document; instance_id = $gone; requirement_set = $set; level_id = [long]$level10.element_id }
Add-HzRefusalProbe -Run $run -Id 'S12b' -Name 'a run that can claim NOTHING refuses with scope_unidentified naming source_file_missing - never "0 changes"' `
    -Call $goneCall -MustMatch 'scope_unidentified.*source_file_missing'

# =============================================================================
# 13. PROVENANCE v1 -> v2: THE MIGRATION ITSELF
# =============================================================================
# Steps 1-12 all measure records THIS build wrote, and this build writes v2. So
# the migration - the branch that claims a record carrying no placement id,
# rewrites it, and refuses when two placements could have built it - had never
# run against a v1 record at all, in any harness, on any machine. It was
# reasoned about, which is this repository's word for unverified.
#
# HOW A GENUINE v1 RECORD IS PRODUCED HERE. Not by hand-writing values: by
# CONVERTING with this build and then DEMOTING the result through
# CadProvenanceV1Fixture, which writes the retired v1 schema built from
# CadProvenanceV1Shape - the v1 definition as it stood in CadProvenanceStore
# before provenance v2, field for field, in the same order, with the same single
# unit spec, and with the five fields v2 added simply absent. Every value in the
# record is one this build's own converter wrote; only the placement half is
# gone, which is exactly what a model converted by 1.1.x carries.
#
# WHAT THAT ESTABLISHES, AND WHAT IT DOES NOT. It establishes that THIS build's
# reader, scope rules, planner and apply handle a record of v1's shape. It does
# NOT establish that a 1.1.x BINARY wrote that shape - no old binary is run
# here, and no fixture can claim otherwise. The evidence for the shape itself is
# documentary: the definition in this repository's own history, cited in
# CadProvenanceV1Shape and in docs/DWG-TO-BIM.md.
#
# The fixture is reachable ONLY through horizun_execute_python, by reflection on
# the loaded add-in: no command resolves it, no tool exposes it, and a Core test
# fails the build if any file under Commands/ so much as names it. On a machine
# that has not granted execute_python these probes are fixture_missing - not
# passed, and not a product failure.

<#
  Is arbitrary code available on this machine at all? Asked by RUNNING one line,
  not by reading a tool list: the list can be stale in a client that does not
  implement list-change notifications, and what matters here is whether the call
  works.
#>
function Test-HzExecutePython {
    param([Parameter(Mandatory)]$Run)
    if ($Run.PSObject.Properties.Name -contains 'PythonAvailable') { return $Run.PythonAvailable }
    $probe = Join-Path $Run.WorkDir 'python-available.py'
    "__output__ = {'status': 'self_reported_verified', 'reachable': True}" |
        Set-Content -LiteralPath $probe -Encoding utf8
    $call = Invoke-HzTool -Run $Run -Tool 'horizun_execute_python' -Label 'python-available' -Arguments @{
        code_path = $probe; target_document = $Run.Document; idempotency_key = (New-HzKey $Run 'pyprobe')
    }
    $has = [bool]($call.Ok -and (Get-HzPath $call.Result 'output', 'reachable') -eq $true)
    Add-Member -InputObject $Run -NotePropertyName 'PythonAvailable' -NotePropertyValue $has -Force
    if (-not $has) {
        Add-HzNote $Run ('execute_python is not available here: ' + (Limit-HzText ([string]$call.Text) 160))
    }
    $has
}

<#
  Drive CadProvenanceV1Fixture. op is 'demote' or 'inspect'; the reply is the
  fixture's own JSON, which reports what the MODEL holds after the commit rather
  than what was asked for.
#>
function Invoke-HzV1Fixture {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][ValidateSet('demote', 'inspect')][string]$Op,
        [Parameter(Mandatory)][long[]]$ElementIds,
        [Parameter(Mandatory)][string]$Label
    )
    $safe = ($Label -replace '[^A-Za-z0-9_.-]', '_')
    $requestPath = Join-Path $Run.WorkDir ("v1fixture-$safe.request.json")
    (@{ op = $Op; element_ids = @($ElementIds) } | ConvertTo-Json -Depth 8 -Compress) |
        Set-Content -LiteralPath $requestPath -Encoding utf8
    $py = Join-Path $Run.WorkDir ("v1fixture-$safe.py")
@"
import System

# The add-in is already loaded in this AppDomain, so the fixture is reached the
# way a debugger would reach it. Nothing publishes it as a tool.
fixture = None
for assembly in System.AppDomain.CurrentDomain.GetAssemblies():
    try:
        found = assembly.GetType('Horizun.Revit.Core.CadProvenanceV1Fixture')
    except:
        found = None
    if found is not None:
        fixture = found
        break

if fixture is None:
    __output__ = {'status': 'failed',
                  'error': 'CadProvenanceV1Fixture is not in the loaded add-in: this Revit is running a build from before the v1 migration fixture, so no genuine v1 record can be staged against it.'}
else:
    handle = open(r'$requestPath', 'r')
    request = handle.read()
    handle.close()
    reply = fixture.GetMethod('Run').Invoke(None, System.Array[System.Object]([doc, request]))
    __output__ = {'status': 'self_reported_verified', 'fixture_json': reply}
"@ | Set-Content -LiteralPath $py -Encoding utf8

    $r = Invoke-HzToolStrict -Run $Run -Tool 'horizun_execute_python' -Label $Label -Arguments @{
        code_path = $py; target_document = $Run.Document; idempotency_key = (New-HzKey $Run $Label)
    }
    $out = Get-HzProp $r.Result 'output'
    $json = Get-HzProp $out 'fixture_json'
    if (-not $json) {
        throw ('HARNESS: the v1 fixture returned nothing usable: {0}' -f
               (Limit-HzText ([string](Get-HzProp $out 'error')) 300))
    }
    $json | ConvertFrom-Json
}

<#
  Demote a freshly converted set to v1, and REFUSE TO CONTINUE unless every one
  of them came back v1 with no v2 entity left. A partly demoted set would
  measure the migration against a mixture, which is neither of the two cases.
#>
function Set-HzProvenanceToV1 {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long[]]$ElementIds, [Parameter(Mandatory)][string]$Label)
    $done = Invoke-HzV1Fixture -Run $Run -Op 'demote' -ElementIds $ElementIds -Label $Label
    if ([int]$done.demoted_verified -ne $ElementIds.Count) {
        throw ('HARNESS: the v1 fixture demoted {0} of {1} elements, so the migration cannot be staged' -f
               $done.demoted_verified, $ElementIds.Count)
    }
    Add-HzNote $Run ('{0}: {1} element(s) now carry the v1 schema and no v2 entity, re-read after the commit' -f
                     $Label, $done.demoted_verified)
    $done
}

Write-Host "`n== 13. provenance v1 -> v2: the migration ==" -ForegroundColor Cyan
if (-not (Test-HzExecutePython -Run $run)) {
    $why = 'horizun_execute_python is not available on this machine, so CadProvenanceV1Fixture cannot be ' +
           'reached and no genuine v1 record can be staged. Grant it once with ' +
           'scripts\enable-execute-python.ps1 and re-run: these probes are the ONLY coverage of the ' +
           'v1 -> v2 provenance migration anywhere in this repository.'
    foreach ($missing in @(
        @{ id = 'S13a'; name = 'a v1 conversion under ONE placement is claimed, counted and rewritten as v2' },
        @{ id = 'S13a2'; name = 'the apply rewrites v1 records as v2 without touching a wall' },
        @{ id = 'S13a3'; name = 'planned again, there is nothing left to migrate' },
        @{ id = 'S13b'; name = 'a v1 set two placements could have built is refused as ambiguous_v1' },
        @{ id = 'S13b2'; name = 'the ambiguity refusal names both placements and the model is untouched' },
        @{ id = 'S13c'; name = 'ambiguity refuses even when other elements ARE claimable' },
        @{ id = 'S13c2'; name = 'and it refuses BEFORE anything is modified' },
        @{ id = 'S13d'; name = 'an IMPORTED drawing with no external path migrates by placement identity' },
        @{ id = 'S13d2'; name = 'and the rewrite gives it the placement id it never had' },
        @{ id = 'S13e'; name = 'a v1 set whose source file has gone is not rescued by being legacy' },
        @{ id = 'S13e2'; name = 'told what it supersedes, the same run claims it and says the hash is unavailable' },
        @{ id = 'S13f'; name = "a person's edit on a v1 element is review across the migration" },
        @{ id = 'S13f2'; name = 'and stays review after it, with the as-built line unchanged' },
        @{ id = 'S13g'; name = 'the migrating apply replays under the same idempotency_key' })) {
        Add-HzProbe -Run $run -Id $missing.id -Name $missing.name -Status 'fixture_missing' `
            -Expected 'a genuine v1 provenance record to migrate' -Observed $why -Because $why
    }
} else {

# -----------------------------------------------------------------------------
# 13a. ONE placement: claimed, counted, rewritten, nothing left to migrate
# -----------------------------------------------------------------------------
$null = Reset-HzDocument $run
$level13 = Get-HzFirstLevel $run
$legacy = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-legacy'
$factsLegacy = Get-HzCadInstanceFacts -Run $run -InstanceId $legacy
$builtLegacy = Invoke-HzFirstConversion -Run $run -InstanceId $legacy -Set $set `
    -LevelId ([long]$level13.element_id) -Label 'legacy'
$idsLegacy = @($builtLegacy.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
if ($idsLegacy.Count -ne 4) { throw ('HARNESS: the legacy fixture built {0} stamped walls, not 4' -f $idsLegacy.Count) }
$demoted = Set-HzProvenanceToV1 -Run $run -ElementIds $idsLegacy -Label 'demote-legacy'
$run.Fixture['v1_schema'] = $demoted.schema

$updV1 = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-v1' -Arguments @{
    target_document = $Document; instance_id = $legacy; requirement_set = $set; level_id = [long]$level13.element_id }
$v1Files = Get-HzPath $updV1.Result 'scope', 'exists', 'v1_files'
$v1Counted = [int](Get-HzProp $v1Files $fixA.dwg_sha256)
$ambiguousHere = @(Get-HzProp $updV1.Result 'ambiguous_v1')
Add-HzProbe -Run $run -Id 'S13a' -Name 'a v1 conversion under ONE placement is READ as v1, claimed for migration, and every record queued for rewrite' `
    -Expected 'scope.migrated_from_v1=4, scope.claimed=0, ambiguous_v1 empty, leave=4, automatic=0, restamp=4 all of them migrated_from_v1, and the plan itself counts 4 v1 records against this file' `
    -Observed ('migrated={0} claimed={1} ambiguous={2} leave={3} automatic={4} restamp={5} restamped_on_apply={6} v1_files[A]={7}' -f
        (Get-HzPath $updV1.Result 'scope', 'migrated_from_v1'), (Get-HzPath $updV1.Result 'scope', 'claimed'),
        $ambiguousHere.Count, (Get-HzKind $updV1.Result 'leave'), $updV1.Result.automatic,
        (Get-HzPath $updV1.Result 'restamp', 'count'),
        (Get-HzPath $updV1.Result 'migrated_from_v1', 'restamped_on_apply'), $v1Counted) `
    -Ok ([int](Get-HzPath $updV1.Result 'scope', 'migrated_from_v1') -eq 4 -and
         [int](Get-HzPath $updV1.Result 'scope', 'claimed') -eq 0 -and
         $ambiguousHere.Count -eq 0 -and
         (Get-HzKind $updV1.Result 'leave') -eq 4 -and [int]$updV1.Result.automatic -eq 0 -and
         [int](Get-HzPath $updV1.Result 'restamp', 'count') -eq 4 -and
         [int](Get-HzPath $updV1.Result 'migrated_from_v1', 'restamped_on_apply') -eq 4 -and
         $v1Counted -eq 4) `
    -Evidence @{ scope = $updV1.Result.scope; migrated_from_v1 = $updV1.Result.migrated_from_v1
                 restamp = $updV1.Result.restamp; counts_by_kind = $updV1.Result.counts_by_kind
                 v1_schema = $demoted.schema }

$applyV1 = @{
    target_document = $Document; actions = $updV1.Result.actions
    provenance = $updV1.Result.provenance; candidate_index = $updV1.Result.candidate_index
}
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-v1-dry' `
    -Arguments (Copy-HzArgs $applyV1 @{ dry_run = $true })
# ONE key, used twice on purpose: the replay probe below is about the SAME key
# arriving again.
$v1Key = New-HzKey $run 'migrate13a'
$doneV1 = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-v1' `
    -Arguments (Copy-HzArgs $applyV1 @{ dry_run = $false; idempotency_key = $v1Key })
$afterV1 = Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds $idsLegacy -Label 'inspect-after-migrate'
$rowsV1 = @($afterV1.elements)
$nowV2 = @($rowsV1 | Where-Object {
    [string](Get-HzProp $_ 'provenance_version') -eq 'v2' -and
    [string](Get-HzProp $_ 'placement_id') -eq [string]$factsLegacy.unique_id -and
    (Get-HzProp $_ 'has_v1_entity') -ne $true -and (Get-HzProp $_ 'has_v2_entity') -eq $true })
$stillLegacy = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls-after-migrate' |
    Where-Object { $idsLegacy -contains [long]$_.element_id })
$wasV1Rows = @($doneV1.Result.restamps | Where-Object { [string]$_.was_version -eq 'v1' })
Add-HzProbe -Run $run -Id 'S13a2' -Name 'the apply REWRITES the four v1 records as v2 without touching a wall: same element ids, the placement now named, the v1 entity gone' `
    -Expected ('provenance_rewritten=4, migrated_from_v1=4, restamp_failed=0, the same 4 element ids still in the model, each reading v2 under placement {0} with no v1 entity left' -f
        (Limit-HzText ([string]$factsLegacy.unique_id) 12)) `
    -Observed ('state={0} rewritten={1} migrated={2} failed={3} ids_present={4} now_v2={5} restamp_rows_was_v1={6}' -f
        $doneV1.Result.state, $doneV1.Result.provenance_rewritten, $doneV1.Result.migrated_from_v1,
        $doneV1.Result.restamp_failed, $stillLegacy.Count, $nowV2.Count, $wasV1Rows.Count) `
    -Ok ([string]$doneV1.Result.state -eq 'applied' -and
         [int]$doneV1.Result.provenance_rewritten -eq 4 -and [int]$doneV1.Result.migrated_from_v1 -eq 4 -and
         [int]$doneV1.Result.restamp_failed -eq 0 -and $stillLegacy.Count -eq 4 -and $nowV2.Count -eq 4 -and
         $wasV1Rows.Count -eq 4) `
    -Evidence @{ restamps = $doneV1.Result.restamps; after = $rowsV1 }

$quietV1 = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-v1-again' -Arguments @{
    target_document = $Document; instance_id = $legacy; requirement_set = $set; level_id = [long]$level13.element_id }
$v2Here = Get-HzPath $quietV1.Result 'scope', 'exists', 'v2_placements', ([string]$factsLegacy.unique_id)
$v1FilesAfter = Get-HzPath $quietV1.Result 'scope', 'exists', 'v1_files'
$v1FilesLeft = if ($null -eq $v1FilesAfter) { 0 } else { @($v1FilesAfter.PSObject.Properties).Count }
Add-HzProbe -Run $run -Id 'S13a3' -Name 'planned again, there is NOTHING left to migrate: the same elements are claimed as v2 and no v1 record of this file remains' `
    -Expected 'migrated_from_v1=0, claimed=4, restamp=0, automatic=0, and the model reports 4 elements under this placement with no v1 file left' `
    -Observed ('migrated={0} claimed={1} restamp={2} automatic={3} v2_here={4} v1_files_left={5}' -f
        (Get-HzPath $quietV1.Result 'scope', 'migrated_from_v1'), (Get-HzPath $quietV1.Result 'scope', 'claimed'),
        (Get-HzPath $quietV1.Result 'restamp', 'count'), $quietV1.Result.automatic,
        (Get-HzProp $v2Here 'elements'), $v1FilesLeft) `
    -Ok ([int](Get-HzPath $quietV1.Result 'scope', 'migrated_from_v1') -eq 0 -and
         [int](Get-HzPath $quietV1.Result 'scope', 'claimed') -eq 4 -and
         [int](Get-HzPath $quietV1.Result 'restamp', 'count') -eq 0 -and
         [int]$quietV1.Result.automatic -eq 0 -and
         [int](Get-HzProp $v2Here 'elements') -eq 4 -and $v1FilesLeft -eq 0) `
    -Evidence @{ scope = $quietV1.Result.scope }

$replayV1 = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-v1-replay' `
    -Arguments (Copy-HzArgs $applyV1 @{ dry_run = $false; idempotency_key = $v1Key })
$idemV1 = Get-HzProp $replayV1.Result 'idempotency'
$statusV1 = if ($idemV1) { [string](Get-HzProp $idemV1 'status') } else { '' }
$ranV1 = if ($idemV1) { Get-HzProp $idemV1 'command_executed_in_this_call' } else { $null }
$flagV1 = Get-HzProp $replayV1.Result 'replayed'
$afterReplay = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-v1-after-replay' -Arguments @{
    target_document = $Document; instance_id = $legacy; requirement_set = $set; level_id = [long]$level13.element_id }
Add-HzProbe -Run $run -Id 'S13g' -Name 'the SAME migrating apply under the SAME idempotency_key replays and rewrites nothing a second time' `
    -Expected 'the dispatcher answers replayed with command_executed_in_this_call=false, or the body says replayed=true; previous_partial is null because no run here ended partial; and the model still has nothing left to migrate' `
    -Observed ('replayed={0} status={1} executed={2} previous_partial={3} migrated_after={4} claimed_after={5}' -f
        $flagV1, $statusV1, $ranV1,
        $(if ($null -eq (Get-HzProp $replayV1.Result 'previous_partial')) { 'null' } else { 'SET' }),
        (Get-HzPath $afterReplay.Result 'scope', 'migrated_from_v1'),
        (Get-HzPath $afterReplay.Result 'scope', 'claimed')) `
    -Ok ((($flagV1 -eq $true) -or ($statusV1 -eq 'replayed' -and $ranV1 -eq $false)) -and
         $null -eq (Get-HzProp $replayV1.Result 'previous_partial') -and
         [int](Get-HzPath $afterReplay.Result 'scope', 'migrated_from_v1') -eq 0 -and
         [int](Get-HzPath $afterReplay.Result 'scope', 'claimed') -eq 4) `
    -Evidence @{ idempotency = $idemV1; replayed = $flagV1
                 previous_partial_means = 'the ledger remembers a PARTIAL run against its placement. Null is the honest answer here: nothing in this scenario ended partial, and a probe that manufactured one would be measuring the harness.' }

# -----------------------------------------------------------------------------
# 13f. A PERSON'S EDIT SURVIVES THE MIGRATION
# -----------------------------------------------------------------------------
# The migration rewrites the RECORD, and must not rewrite the evidence inside
# it. An element somebody moved is recognised by comparing where it stands
# against the geometry its record says it was BUILT with - so that field has to
# come through the rewrite untouched, or a person's work becomes a wall the next
# update quietly re-shapes.
$null = Reset-HzDocument $run
$levelEdit = Get-HzFirstLevel $run
$editInst = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-edited'
$builtEdit = Invoke-HzFirstConversion -Run $run -InstanceId $editInst -Set $set `
    -LevelId ([long]$levelEdit.element_id) -Label 'edited'
$idsEdit = @($builtEdit.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
if ($idsEdit.Count -ne 4) { throw 'HARNESS: the person-edit fixture did not build four stamped walls' }
$null = Set-HzProvenanceToV1 -Run $run -ElementIds $idsEdit -Label 'demote-edited'

$nearEdit = @(Get-HzElementsIn -Run $run -Categories @('OST_Walls') `
    -Min @(($X - 500.0), 3500.0, -1000.0) -Max @(($X + 6500.0), 4500.0, 4000.0) -Label 'find-edit-target')
$target = @($nearEdit | Where-Object { $idsEdit -contains [long]$_.element_id })
if ($target.Count -ne 1) { throw ('HARNESS: expected one v1 wall around y=4000 and found {0}' -f $target.Count) }
$editId = [long]$target[0].element_id
$editUnique = [string]$target[0].unique_id
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'person-move-v1' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = @($editId); vector = @(0.0, 250.0, 0.0) })
}
$beforeEdit = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds @($editId) -Label 'inspect-edited-before').elements)
$builtLineBefore = [string](Get-HzProp $beforeEdit[0] 'built_geometry_mm')

$updEdit = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-edited' -Arguments @{
    target_document = $Document; instance_id = $editInst; requirement_set = $set; level_id = [long]$levelEdit.element_id }
$reviewEdit = @($updEdit.Result.plan | Where-Object { $_.kind -eq 'review' })
$editActions = ($updEdit.Result.actions | ConvertTo-Json -Depth 20 -Compress)
$migratedIds = @(Get-HzPath $updEdit.Result 'migrated_from_v1', 'element_ids')
# EVERY TERM NAMED AND REPORTED. This probe's Observed printed four values that
# were all true while its -Ok came out false, and nothing in the artifact said
# which term disagreed - a probe nobody can debug is a probe nobody can trust.
$editRows = @($reviewEdit | Where-Object { [long]$_.element_id -eq $editId })
$t_isReview = ($editRows.Count -eq 1)
$t_saysMoved = $t_isReview -and ([string]$editRows[0].says -match 'A PERSON MOVED THIS')
$t_inMigrated = [bool](@($migratedIds | ForEach-Object { [long]$_ }) -contains [long]$editId)
$t_notInActions = -not [bool]([string]$editActions -match ('"element_id":\s*' + [string]$editId + '\b'))
Add-HzProbe -Run $run -Id 'S13f' -Name "a person's edit on a v1 element is REVIEW across the migration: claimed for rewrite, never re-shaped, never in the actions" `
    -Expected 'the moved element is review saying A PERSON MOVED THIS, it is listed in migrated_from_v1, and no action names it' `
    -Observed ('is_review={0} says_moved={1} in_migrated={2} not_in_actions={3} review_ids={4} automatic={5}' -f
        $t_isReview, $t_saysMoved, $t_inMigrated, $t_notInActions,
        ((@($reviewEdit | ForEach-Object { $_.element_id })) -join ','), $updEdit.Result.automatic) `
    -Ok ($t_isReview -and $t_saysMoved -and $t_inMigrated -and $t_notInActions) `
    -Evidence @{ review = $reviewEdit; migrated_from_v1 = $updEdit.Result.migrated_from_v1
                 terms = @{ is_review = $t_isReview; says_moved = $t_saysMoved
                            in_migrated = $t_inMigrated; not_in_actions = $t_notInActions } }

$applyEdit = @{
    target_document = $Document; actions = $updEdit.Result.actions
    provenance = $updEdit.Result.provenance; candidate_index = $updEdit.Result.candidate_index
}
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-edited-dry' `
    -Arguments (Copy-HzArgs $applyEdit @{ dry_run = $true })
$doneEdit = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-edited' `
    -Arguments (Copy-HzArgs $applyEdit @{ dry_run = $false; idempotency_key = (New-HzKey $run 'migrate13f') })
$afterEdit = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds @($editId) -Label 'inspect-edited-after').elements)
$rowEdit = $afterEdit[0]
$survivor13 = @(Get-HzElements -Run $run -Categories @('OST_Walls') -WithBox -Label 'walls-edited-after' |
    Where-Object { [string]$_.unique_id -eq $editUnique })
$againEdit = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-edited-again' -Arguments @{
    target_document = $Document; instance_id = $editInst; requirement_set = $set; level_id = [long]$levelEdit.element_id }
$reviewAgain = @($againEdit.Result.plan | Where-Object { $_.kind -eq 'review' -and [long]$_.element_id -eq $editId })
Add-HzProbe -Run $run -Id 'S13f2' -Name 'and after the migration it is STILL review: the record is v2, the AS-BUILT line came through the rewrite unchanged, and the element never moved' `
    -Expected 'the element is still there under its own UniqueId, its record now v2 carrying the same built_geometry_mm, and the next plan still calls it review' `
    -Observed ('present={0} version={1} built_line_unchanged={2} still_review={3} migrated={4}' -f
        $survivor13.Count, [string](Get-HzProp $rowEdit 'provenance_version'),
        ([string](Get-HzProp $rowEdit 'built_geometry_mm') -eq $builtLineBefore), $reviewAgain.Count,
        $doneEdit.Result.migrated_from_v1) `
    -Ok ($survivor13.Count -eq 1 -and [string](Get-HzProp $rowEdit 'provenance_version') -eq 'v2' -and
         [string](Get-HzProp $rowEdit 'built_geometry_mm') -eq $builtLineBefore -and
         $reviewAgain.Count -eq 1 -and [int]$doneEdit.Result.migrated_from_v1 -ge 1) `
    -Evidence @{ built_line_before = $builtLineBefore; after = $rowEdit
                 migrated = $doneEdit.Result.migrated_from_v1 }

# -----------------------------------------------------------------------------
# 13c / 13b. TWO PLACEMENTS while the elements are v1: refused, nothing moved
# -----------------------------------------------------------------------------
$null = Reset-HzDocument $run
$levelAmb = Get-HzFirstLevel $run
$ambA = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-amb-A'
$ambB = Add-HzCadLink -Run $run -DwgPath $fixA.dwg_path -Label 'link-amb-B' -AllowDuplicate
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'unpin-amb-B' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'unpin'; element_ids = @($ambB) })
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'place-amb-B' -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = @($ambB); vector = @(0.0, 40000.0, 0.0) })
}
$factsAmbA = Get-HzCadInstanceFacts -Run $run -InstanceId $ambA
$factsAmbB = Get-HzCadInstanceFacts -Run $run -InstanceId $ambB
$madeAmbA = Invoke-HzFirstConversion -Run $run -InstanceId $ambA -Set $set -LevelId ([long]$levelAmb.element_id) -Label 'ambA'
$madeAmbB = Invoke-HzFirstConversion -Run $run -InstanceId $ambB -Set $set -LevelId ([long]$levelAmb.element_id) -Label 'ambB'
$idsAmbA = @($madeAmbA.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
$idsAmbB = @($madeAmbB.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
if ($idsAmbA.Count -ne 4 -or $idsAmbB.Count -ne 4) { throw 'HARNESS: the two wings did not build 4+4 stamped walls' }
$bothWings = @($idsAmbA + $idsAmbB)

# 13c comes FIRST, while wing A is still v2 and only wing B is legacy. This is
# the case that used to be PLANNED rather than refused: something was claimable,
# so the ambiguity was reported in a field and the run went on - and the drawing
# entities behind the ambiguous wing then matched nothing in scope and came back
# as creates.
$null = Set-HzProvenanceToV1 -Run $run -ElementIds $idsAmbB -Label 'demote-ambB'
$mixedBefore = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds $bothWings -Label 'inspect-mixed-before').elements)
$wallsBeforeMixed = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-before-mixed'
$mixedCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-mixed' -Arguments @{
    target_document = $Document; instance_id = $ambA; requirement_set = $set; level_id = [long]$levelAmb.element_id }
Add-HzRefusalProbe -Run $run -Id 'S13c' -Name 'ambiguity refuses even when four OTHER elements are perfectly claimable - the case that used to be planned' `
    -Call $mixedCall -MustMatch 'ambiguous_v1' `
    -Expected 'refused as ambiguous_v1 although four elements of this very placement are claimable: planning the rest would report the ambiguous wing as new work and build a second copy of it'
$mixedAfter = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds $bothWings -Label 'inspect-mixed-after').elements)
$wallsAfterMixed = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after-mixed'
$mixedUnchanged = (($mixedBefore | ConvertTo-Json -Depth 8 -Compress) -eq ($mixedAfter | ConvertTo-Json -Depth 8 -Compress))
$mixedNamesBoth = ([string]$mixedCall.Text -match [regex]::Escape([string]$factsAmbA.unique_id)) -and
                  ([string]$mixedCall.Text -match [regex]::Escape([string]$factsAmbB.unique_id))
Add-HzProbe -Run $run -Id 'S13c2' -Name 'and it refused BEFORE anything was modified: every element id, every record and the wall count exactly as they were' `
    -Expected ('{0} walls before and after, the eight provenance records byte-identical, both placements named, and no advice to run the first-conversion command' -f $wallsBeforeMixed) `
    -Observed ('walls before={0} after={1} records_identical={2} names_both_placements={3}' -f
        $wallsBeforeMixed, $wallsAfterMixed, $mixedUnchanged, $mixedNamesBoth) `
    -Ok ($wallsAfterMixed -eq $wallsBeforeMixed -and $mixedUnchanged -and $mixedNamesBoth -and
         ([string]$mixedCall.Text -match 'Do NOT run horizun_plan_from_cad')) `
    -Evidence @{ refusal = (Limit-HzText ([string]$mixedCall.Text) 1200); before = $mixedBefore }

# 13b: BOTH wings legacy, and a RE-ISSUED drawing whose lineage the caller
# states. Nothing at all is claimable now, and the refusal still has to be the
# ambiguity - not scope_unidentified, whose advice is to run the FIRST
# conversion against a model that already holds the conversion.
$null = Set-HzProvenanceToV1 -Run $run -ElementIds $idsAmbA -Label 'demote-ambA'
$reissue = Add-HzCadLink -Run $run -DwgPath $fixB.dwg_path -Label 'link-amb-reissue'
$ambBefore = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds $bothWings -Label 'inspect-amb-before').elements)
$wallsBeforeAmb = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-before-amb'
$ambCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-ambiguous' -Arguments @{
    target_document = $Document; instance_id = $reissue; requirement_set = $set
    level_id = [long]$levelAmb.element_id; supersedes_sha256 = @($fixA.dwg_sha256) }
Add-HzRefusalProbe -Run $run -Id 'S13b' -Name 'a re-issued drawing whose v1 elements TWO placements could have built is refused as ambiguous_v1 - never claimed, never orphaned' `
    -Call $ambCall -MustMatch 'ambiguous_v1' `
    -Expected 'refused as ambiguous_v1 naming both placements, and NOT as scope_unidentified with its first-conversion advice'
$ambAfter = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds $bothWings -Label 'inspect-amb-after').elements)
$wallsAfterAmb = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after-amb'
$ambUnchanged = (($ambBefore | ConvertTo-Json -Depth 8 -Compress) -eq ($ambAfter | ConvertTo-Json -Depth 8 -Compress))
$ambNamesBoth = ([string]$ambCall.Text -match [regex]::Escape([string]$factsAmbA.unique_id)) -and
                ([string]$ambCall.Text -match [regex]::Escape([string]$factsAmbB.unique_id))
Add-HzProbe -Run $run -Id 'S13b2' -Name 'the ambiguity refusal names BOTH placements, by CAD instance and by placement id, and the model is untouched' `
    -Expected ('both CAD instances {0} and {1} named in the refusal, all eight records byte-identical, wall count unchanged at {2}' -f
        $ambA, $ambB, $wallsBeforeAmb) `
    -Observed ('names_both_placement_ids={0} records_identical={1} walls before={2} after={3}' -f
        $ambNamesBoth, $ambUnchanged, $wallsBeforeAmb, $wallsAfterAmb) `
    -Ok ($ambNamesBoth -and $ambUnchanged -and $wallsAfterAmb -eq $wallsBeforeAmb -and
         ([string]$ambCall.Text -match ('\b' + [string]$ambA + '\b')) -and
         ([string]$ambCall.Text -match ('\b' + [string]$ambB + '\b'))) `
    -Evidence @{ refusal = (Limit-HzText ([string]$ambCall.Text) 1400); before = $ambBefore }

# -----------------------------------------------------------------------------
# 13d. AN IMPORTED DRAWING: no external file, so no path and no hash
# -----------------------------------------------------------------------------
# The narrowest migration path there is. There is no file hash to scope by and a
# v1 record carries no placement id, so the ONLY thing that can claim it is its
# exact source fingerprint - the one identity a v1 record can still prove.
$null = Reset-HzDocument $run
$levelImp = Get-HzFirstLevel $run
$importPy = Join-Path $run.WorkDir 'import-embedded.py'
@"
from Autodesk.Revit.DB import (Transaction, DWGImportOptions, ImportUnit,
                               FilteredElementCollector, ViewPlan, ViewType)
view = None
for v in FilteredElementCollector(doc).OfClass(ViewPlan):
    if not v.IsTemplate and v.ViewType == ViewType.FloorPlan:
        view = v
        break
opts = DWGImportOptions()
opts.Unit = ImportUnit.Default
opts.ThisViewOnly = True
# IMPORT, not Link: an imported drawing keeps no external file reference at all,
# which is the case that used to fall out of scope entirely (backlog 8.4c).
t = Transaction(doc, 'Horizun live fixture: IMPORT a drawing'); t.Start()
ok, eid = doc.Import(r'$($fixA.dwg_path)', opts, view)
t.Commit()
# ElementId.Value arrived in the 2024 API; 2023 has only IntegerValue, and
# reading the wrong one throws AttributeError - which the harness then reported
# as "Revit gave back no ImportInstance", blaming the model for its own bug.
def _eid(x):
    if x is None:
        return None
    return x.IntegerValue if hasattr(x, 'IntegerValue') else x.Value
__output__ = {'status': 'self_reported_verified', 'imported': bool(ok),
              'element_id': _eid(eid)}
"@ | Set-Content -LiteralPath $importPy -Encoding utf8
$importCall = Invoke-HzTool -Run $run -Tool 'horizun_execute_python' -Label 'import-embedded' -Arguments @{
    code_path = $importPy; target_document = $Document; idempotency_key = (New-HzKey $run 'import13d') }
$importedId = if ($importCall.Ok) { Get-HzPath $importCall.Result 'output', 'element_id' } else { $null }

if ($null -eq $importedId) {
    $whyImport = 'Revit gave back no ImportInstance for an IMPORTED (not linked) drawing on this machine: ' +
                 (Limit-HzText ([string]$importCall.Text) 200)
    foreach ($id in @('S13d', 'S13d2')) {
        Add-HzProbe -Run $run -Id $id -Status 'fixture_missing' `
            -Name 'an IMPORTED drawing with no external path migrates a v1 record by placement identity alone' `
            -Expected 'a DWG imported rather than linked, so the placement has no path and no hash' `
            -Observed $whyImport -Because $whyImport
    }
} else {
    $importedId = [long]$importedId
    $factsImp = Get-HzCadInstanceFacts -Run $run -InstanceId $importedId
    # The set is built from THIS instance's own layer and declared units: an
    # imported drawing is read through the same rules but nothing says Revit
    # declares it the way a link is declared.
    $layerImp = Get-HzWallLayer -Run $run -InstanceId $importedId
    $setImp = New-HzWallRequirementSet -Layer $layerImp -Units ([string]$factsImp.declared_units) -Id 'hz-live-imported'
    $madeImp = Invoke-HzFirstConversion -Run $run -InstanceId $importedId -Set $setImp `
        -LevelId ([long]$levelImp.element_id) -Label 'imported'
    $idsImp = @($madeImp.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
    if ($idsImp.Count -ne 4) { throw 'HARNESS: the imported drawing did not convert to four stamped walls' }
    $null = Set-HzProvenanceToV1 -Run $run -ElementIds $idsImp -Label 'demote-imported'

    $updImp = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-imported' -Arguments @{
        target_document = $Document; instance_id = $importedId; requirement_set = $setImp
        level_id = [long]$levelImp.element_id }
    $idImp = Get-HzPath $updImp.Result 'source', 'identity'
    Add-HzProbe -Run $run -Id 'S13d' -Name 'an IMPORTED drawing has no external file: identity falls back to the PLACEMENT, source_hash says unavailable rather than guessing, and its v1 records are still claimed' `
        -Expected "identity mode embedded_placement, source_hash 'unavailable', external_path null, is_linked false, migrated_from_v1=4" `
        -Observed ('mode={0} source_hash={1} external_path={2} is_linked={3} migrated={4} claimed={5}' -f
            (Get-HzProp $idImp 'mode'), (Get-HzProp $idImp 'source_hash'),
            $(if (Get-HzProp $idImp 'external_path') { 'set' } else { 'null' }),
            (Get-HzProp $factsImp 'is_linked'),
            (Get-HzPath $updImp.Result 'scope', 'migrated_from_v1'), (Get-HzPath $updImp.Result 'scope', 'claimed')) `
        -Ok ([string](Get-HzProp $idImp 'mode') -eq 'embedded_placement' -and
             [string](Get-HzProp $idImp 'source_hash') -eq 'unavailable' -and
             $null -eq (Get-HzProp $idImp 'external_path') -and
             (Get-HzProp $factsImp 'is_linked') -eq $false -and
             [int](Get-HzPath $updImp.Result 'scope', 'migrated_from_v1') -eq 4) `
        -Evidence @{ identity = $idImp; scope = $updImp.Result.scope; facts = $factsImp }

    $applyImp = @{
        target_document = $Document; actions = $updImp.Result.actions
        provenance = $updImp.Result.provenance; candidate_index = $updImp.Result.candidate_index
    }
    $null = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-imported-dry' `
        -Arguments (Copy-HzArgs $applyImp @{ dry_run = $true })
    $doneImp = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_update' -Label 'apply-imported' `
        -Arguments (Copy-HzArgs $applyImp @{ dry_run = $false; idempotency_key = (New-HzKey $run 'migrate13d') })
    $afterImp = @((Invoke-HzV1Fixture -Run $run -Op 'inspect' -ElementIds $idsImp -Label 'inspect-imported-after').elements)
    $impV2 = @($afterImp | Where-Object {
        [string](Get-HzProp $_ 'provenance_version') -eq 'v2' -and
        [string](Get-HzProp $_ 'placement_id') -eq [string]$factsImp.unique_id })
    Add-HzProbe -Run $run -Id 'S13d2' -Name 'and the rewrite gives an embedded import what it never had: every record now names the placement that built it' `
        -Expected 'provenance_rewritten=4, migrated_from_v1=4, and all four records read v2 under the import placement id' `
        -Observed ('rewritten={0} migrated={1} now_v2_under_placement={2}' -f
            $doneImp.Result.provenance_rewritten, $doneImp.Result.migrated_from_v1, $impV2.Count) `
        -Ok ([int]$doneImp.Result.provenance_rewritten -eq 4 -and [int]$doneImp.Result.migrated_from_v1 -eq 4 -and
             $impV2.Count -eq 4) `
        -Evidence @{ restamps = $doneImp.Result.restamps; after = $afterImp }
}

# -----------------------------------------------------------------------------
# 13e. THE SOURCE FILE GOES MISSING under a v1-stamped set
# -----------------------------------------------------------------------------
$null = Reset-HzDocument $run
$levelGone = Get-HzFirstLevel $run
$goneDir13 = Join-Path $run.WorkDir 'gone13'
$null = New-Item -ItemType Directory -Force -Path $goneDir13
$gonePath13 = Join-Path $goneDir13 ('legacy-' + $run.RunId + '.dwg')
Copy-Item -LiteralPath $fixA.dwg_path -Destination $gonePath13 -Force
$goneSha13 = Get-HzSha256 $gonePath13
$goneInst = Add-HzCadLink -Run $run -DwgPath $gonePath13 -Label 'link-gone-legacy'
$madeGone = Invoke-HzFirstConversion -Run $run -InstanceId $goneInst -Set $set `
    -LevelId ([long]$levelGone.element_id) -Label 'goneLegacy'
$idsGone = @($madeGone.Result.provenance | Where-Object { $_.written } | ForEach-Object { [long]$_.element_id })
if ($idsGone.Count -ne 4) { throw 'HARNESS: the missing-source fixture did not build four stamped walls' }
$null = Set-HzProvenanceToV1 -Run $run -ElementIds $idsGone -Label 'demote-gone'
Remove-Item -LiteralPath $gonePath13 -Force

$goneBlind = Invoke-HzTool -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-gone-legacy' -Arguments @{
    target_document = $Document; instance_id = $goneInst; requirement_set = $set; level_id = [long]$levelGone.element_id }
Add-HzRefusalProbe -Run $run -Id 'S13e' -Name 'a v1 set whose drawing file has GONE is not rescued by being legacy: with no lineage stated the run refuses rather than claiming by guesswork' `
    -Call $goneBlind -MustMatch 'scope_unidentified.*source_file_missing' `
    -Expected 'refused as scope_unidentified naming source_file_missing - the bytes cannot be hashed, and a v1 record carries no placement id to match on instead'

$goneNamed = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-gone-named' -Arguments @{
    target_document = $Document; instance_id = $goneInst; requirement_set = $set
    level_id = [long]$levelGone.element_id; supersedes_sha256 = @($goneSha13) }
$idGone = Get-HzPath $goneNamed.Result 'source', 'identity'
Add-HzProbe -Run $run -Id 'S13e2' -Name 'told what it supersedes, the same run claims the v1 set against the geometry Revit still holds - and still says the hash is unavailable rather than reporting the recorded one as confirmed' `
    -Expected "identity mode source_file_missing, source_hash 'unavailable', migrated_from_v1=4, restamp=4" `
    -Observed ('mode={0} source_hash={1} migrated={2} restamp={3} leave={4}' -f
        (Get-HzProp $idGone 'mode'), (Get-HzProp $idGone 'source_hash'),
        (Get-HzPath $goneNamed.Result 'scope', 'migrated_from_v1'),
        (Get-HzPath $goneNamed.Result 'restamp', 'count'), (Get-HzKind $goneNamed.Result 'leave')) `
    -Ok ([string](Get-HzProp $idGone 'mode') -eq 'source_file_missing' -and
         [string](Get-HzProp $idGone 'source_hash') -eq 'unavailable' -and
         [int](Get-HzPath $goneNamed.Result 'scope', 'migrated_from_v1') -eq 4 -and
         [int](Get-HzPath $goneNamed.Result 'restamp', 'count') -eq 4) `
    -Evidence @{ identity = $idGone; scope = $goneNamed.Result.scope }

}   # end of: execute_python is available on this machine

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
