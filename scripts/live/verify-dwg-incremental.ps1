#Requires -Version 5.1
<#
  DWG-3, LIVE: the nine steps of an incremental update.

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
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
