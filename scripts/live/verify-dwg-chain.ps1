#Requires -Version 5.1
<#
  DWG-1, LIVE: query -> plan -> dry-run -> apply -> provenance -> idempotence -> stale.

  The whole conversion chain against a drawing THIS SCRIPT AUTHORED, so every
  probe compares what came back against what was drawn rather than against
  itself. Three walls, 200 mm apart from anything else in the model, exported by
  Revit's own DWG exporter and then discarded from the document.

  Every probe states what it EXPECTED before it looks, so a pass is a comparison
  and not a description of whatever happened.

  Run it with Revit open on the write fixture:
      pwsh -File scripts/live/verify-dwg-chain.ps1

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-chain' -Document $Document

# =============================================================================
# STAGING - a document nobody has built in, and a drawing we drew
# =============================================================================
Write-Host "`n== staging ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

# THE TRUTH: three walls forming a U, 900 m east of the base model.
$X = 900000.0
$truth = @(
    @{ name = 'W1'; x1 = $X;         y1 = 0.0;    x2 = ($X + 8000); y2 = 0.0 },
    @{ name = 'W2'; x1 = ($X + 8000); y1 = 0.0;    x2 = ($X + 8000); y2 = 5000.0 },
    @{ name = 'W3'; x1 = ($X + 8000); y1 = 5000.0; x2 = $X;         y2 = 5000.0 }
)
$fixture = New-HzWallFixture -Run $run -Walls $truth -Tag 'chain'
foreach ($k in $fixture.Keys) { $run.Fixture[$k] = $fixture[$k] }
$run.Expected['walls_drawn'] = 3
$run.Expected['origin_mm'] = @($X, 0.0)
Add-HzNote $run ("fixture {0}, {1} bytes, sha {2}" -f $fixture.dwg_name, $fixture.dwg_bytes, $fixture.dwg_sha256.Substring(0, 16))

# The walls were exported and then must NOT stay in the model: the drawing is a
# picture of them, and leaving them makes every probe below match the fixture's
# own scaffolding.
$null = Reset-HzDocument $run
$instanceId = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link-chain'

# =============================================================================
# Q - reading the drawing
# =============================================================================
Write-Host "`n== Q: reading the drawing ==" -ForegroundColor Cyan

$h = Get-HzHealth $run
Add-HzProbe -Run $run -Id 'Q1' -Name 'health names the Revit and the active document' `
    -Expected "healthy, active=$Document" `
    -Observed ("{0}, active={1}" -f $h.status, $h.active_document.title) `
    -Ok (($h.status -eq 'healthy') -and ([string]$h.active_document.title -eq $Document)) `
    -Evidence @{ commit = $h.horizun_commit; version = $h.horizun_version; revit = $h.revit_build }

$qi = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-instances' -Arguments @{ mode = 'instances' }
$mine = @($qi.Result.instances | Where-Object { [long]$_.element_id -eq $instanceId })
Add-HzProbe -Run $run -Id 'Q2' -Name 'query instances finds exactly the CAD we linked, with its file hash' `
    -Expected ("1 instance, linked, sha={0}" -f $fixture.dwg_sha256.Substring(0, 16)) `
    -Observed ("{0} match, import_or_link={1}, sha={2}" -f $mine.Count,
        $(if ($mine.Count) { $mine[0].import_or_link } else { '-' }),
        $(if ($mine.Count -and $mine[0].file_sha256) { ([string]$mine[0].file_sha256).Substring(0, 16) } else { '-' })) `
    -Ok ($mine.Count -eq 1 -and $mine[0].import_or_link -eq 'linked' -and $mine[0].file_sha256 -eq $fixture.dwg_sha256) `
    -Evidence @{ declared_units = $mine[0].declared_units
                 source_fingerprint = $mine[0].source_fingerprint
                 linked_file_status = $mine[0].linked_file_status
                 declared_units_route = $mine[0].declared_units_route }

$ql = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-layers' -Arguments @{
    mode = 'layers'; instance_id = $instanceId }
$wallLayers = @($ql.Result.layers | Where-Object { $_.layer -match '(?i)WALL' })
Add-HzProbe -Run $run -Id 'Q3' -Name 'query layers reports the DWG layers, including a wall layer' `
    -Expected 'at least one layer whose name contains WALL' `
    -Observed ("{0} layers; wall-ish: {1}" -f $ql.Result.layer_count, ((@($wallLayers | ForEach-Object { $_.layer })) -join ', ')) `
    -Ok (([int]$ql.Result.layer_count -ge 1) -and ($wallLayers.Count -ge 1)) `
    -Evidence @{ layers = @($ql.Result.layers | ForEach-Object {
        @{ layer = $_.layer; primitives = $_.primitive_count; segments = $_.segment_count } }) }

$layer = [string]$wallLayers[0].layer
$run.Fixture['wall_layer'] = $layer

$qg = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-geom' -Arguments @{
    mode = 'geometry'; instance_id = $instanceId; layer = $layer; max_rows = 500 }
Add-HzProbe -Run $run -Id 'Q4' -Name 'query geometry returns the wall faces in millimetres' `
    -Expected 'at least 6 segments - two faces per wall, three walls' `
    -Observed ("{0} matching on '{1}'" -f $qg.Result.segments_matching, $layer) `
    -Ok ([int]$qg.Result.segments_matching -ge 6) `
    -Evidence @{ bounding_box_mm = $qg.Result.bounding_box_mm; set_fingerprint = $qg.Result.set_fingerprint
                 segments_matching = $qg.Result.segments_matching }

$page1 = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-page1' -Arguments @{
    mode = 'geometry'; instance_id = $instanceId; layer = $layer; max_rows = 3; offset = 0 }
$page2 = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-page2' -Arguments @{
    mode = 'geometry'; instance_id = $instanceId; layer = $layer; max_rows = 3; offset = 3 }
$ids1 = @($page1.Result.segments | ForEach-Object { $_.surrogate_id })
$ids2 = @($page2.Result.segments | ForEach-Object { $_.surrogate_id })
$overlap = @($ids1 | Where-Object { $ids2 -contains $_ })
Add-HzProbe -Run $run -Id 'Q5' -Name 'paging loses nothing and repeats nothing' `
    -Expected 'two pages of 3, no surrogate id in both' `
    -Observed ("page1={0} page2={1} overlap={2}" -f $ids1.Count, $ids2.Count, $overlap.Count) `
    -Ok ($ids1.Count -eq 3 -and $ids2.Count -ge 1 -and $overlap.Count -eq 0) `
    -Evidence @{ page1_ids = $ids1; page2_ids = $ids2; truncated_on_page1 = $page1.Result.truncated }

$qg2 = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-geom-again' -Arguments @{
    mode = 'geometry'; instance_id = $instanceId; layer = $layer; max_rows = 500 }
Add-HzProbe -Run $run -Id 'Q6' -Name 'reading the same drawing twice gives the same set fingerprint' `
    -Expected ([string]$qg.Result.set_fingerprint) -Observed ([string]$qg2.Result.set_fingerprint) `
    -Ok ([string]$qg.Result.set_fingerprint -eq [string]$qg2.Result.set_fingerprint) `
    -Evidence @{ first = $qg.Result.set_fingerprint; second = $qg2.Result.set_fingerprint }

$bbox = $qg.Result.bounding_box_mm
$minX = [double]$bbox.min[0]
$withinX = ($minX -ge ($X - 3000)) -and ($minX -le ($X + 3000))
Add-HzProbe -Run $run -Id 'Q7' -Name 'the geometry lands where we drew it - no double unit scaling' `
    -Expected ("min x within 3 m of {0}" -f $X) -Observed ("min x = {0}" -f $minX) -Ok $withinX `
    -Evidence @{ bounding_box_mm = $bbox
                 note = 'a double-applied unit factor would put this at 25x or 1/25x of the truth' }

$qc = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'q-coverage' -Arguments @{ mode = 'coverage' }
$prov = $qc.Result.provenance
Add-HzProbe -Run $run -Id 'Q8' -Name 'coverage publishes what CANNOT be read rather than leaving it to be discovered' `
    -Expected 'text unavailable, blocks unavailable, hatches unavailable, identity derived' `
    -Observed ("text={0} blocks={1} hatch={2} identity={3}" -f $prov.text, $prov.block_names_and_attributes,
        $prov.hatches, $prov.entity_identity) `
    -Ok ($prov.text -eq 'unavailable' -and $prov.block_names_and_attributes -eq 'unavailable' -and
         $prov.hatches -eq 'unavailable' -and $prov.entity_identity -eq 'derived') `
    -Evidence @{ provenance = $prov }

# =============================================================================
# P - planning
# =============================================================================
Write-Host "`n== P: planning ==" -ForegroundColor Cyan

$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $instanceId
$units = [string]$facts.declared_units
$set = New-HzWallRequirementSet -Layer $layer -Units $units -Id 'hz-live-chain'
$run.Fixture['requirement_set_id'] = $set.requirement_set.id
$run.Fixture['requirement_set_version'] = $set.requirement_set.version

$badSet = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'bad'; version = '1'; title = 'bad' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'walls'; layers = @($layer); produces = 'wall'; geometry = @{ from = 'double_lines' } })
}
$pBad = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'p-bad-set' -Arguments @{
    instance_id = $instanceId; requirement_set = $badSet }
Add-HzRefusalProbe -Run $run -Id 'P1' -Name 'a requirement set missing its thickness bounds is refused WHOLE' `
    -Call $pBad -MustMatch 'any two parallel lines are a wall' `
    -Expected 'refused, naming why any two parallel lines would otherwise be a wall'

$mismatch = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$mismatch.source.units = $(if ($units -eq 'millimeter') { 'meter' } else { 'millimeter' })
$pMis = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'p-unit-mismatch' -Arguments @{
    instance_id = $instanceId; requirement_set = $mismatch }
Add-HzRefusalProbe -Run $run -Id 'P2' -Name 'a unit mismatch between the link and the set refuses without writing' `
    -Call $pMis -MustMatch 'unit_mismatch'

$pNoLevel = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'p-no-level' -Arguments @{
    instance_id = $instanceId; requirement_set = $set }
Add-HzRefusalProbe -Run $run -Id 'P3a' -Name 'walls with no level declared anywhere are refused, not placed on a guess' `
    -Call $pNoLevel -MustMatch 'level_unresolved'

$pBadLevel = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'p-bad-level' -Arguments @{
    instance_id = $instanceId; requirement_set = $set; level_name = 'ZZ Nivel Que No Existe' }
Add-HzRefusalProbe -Run $run -Id 'P3b' -Name 'a level name that matches nothing is refused, and the reply lists what does exist' `
    -Call $pBadLevel -MustMatch 'level_not_found'

$level = Get-HzFirstLevel $run
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'p-plan' -Arguments @{
    instance_id = $instanceId; requirement_set = $set; level_id = [long]$level.element_id }
$wallCount = [int]$plan.Result.counts_by_kind.wall
Add-HzProbe -Run $run -Id 'P3' -Name 'the plan proposes exactly the three walls we drew' `
    -Expected '3 wall actions - a compound wall exports one line per material layer, and they are one wall' `
    -Observed ("actions={0} walls={1} deferred={2}" -f $plan.Result.actions, $wallCount, $plan.Result.deferred) `
    -Ok ($wallCount -eq 3) `
    -Evidence @{ plan_fingerprint = $plan.Result.plan_fingerprint
                 coverage = $plan.Result.coverage
                 warnings = $plan.Result.warnings
                 counts_by_kind = $plan.Result.counts_by_kind }

$binding = $plan.Result.apply_binding
$hasAll = $binding.plan_fingerprint -and $binding.actions_fingerprint -and $binding.source_fingerprint -and
          $binding.requirement_set_sha256 -and $binding.target_document -and
          (@($binding.resolved_names).Count -ge 1)
Add-HzProbe -Run $run -Id 'P4' -Name 'the binding covers the drawing, the rules, the actions, the target and the ids the names resolved to' `
    -Expected 'plan, actions, source, requirement set, target document and at least one resolved name' `
    -Observed ("plan={0} actions={1} source={2} set={3} target={4} resolved={5}" -f
        [bool]$binding.plan_fingerprint, [bool]$binding.actions_fingerprint, [bool]$binding.source_fingerprint,
        [bool]$binding.requirement_set_sha256, [bool]$binding.target_document, @($binding.resolved_names).Count) `
    -Ok ([bool]$hasAll) -Evidence @{ apply_binding = $binding }
$run.Fixture['requirement_set_sha256'] = [string]$binding.requirement_set_sha256

# =============================================================================
# A - applying
# =============================================================================
Write-Host "`n== A: applying ==" -ForegroundColor Cyan

$wallsBefore = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-before'
$applyArgs = @{
    target_document = $Document
    instance_id = $instanceId
    requirement_set = $set
    apply_binding = $binding
    actions = $plan.Result.execute_plan_request.actions
    candidate_index = $plan.Result.candidate_index
}

$dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'a-dry' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
$wallsAfterDry = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after-dry'
Add-HzProbe -Run $run -Id 'A1' -Name 'the dry run writes NOTHING and says so' `
    -Expected ("walls unchanged at {0}, state=rehearsed" -f $wallsBefore) `
    -Observed ("walls={0} state={1}" -f $wallsAfterDry, $dry.Result.state) `
    -Ok ($wallsAfterDry -eq $wallsBefore -and [string]$dry.Result.state -eq 'rehearsed') `
    -Evidence @{ rehearsal_means = $dry.Result.rehearsal.means; walls_before = $wallsBefore; walls_after = $wallsAfterDry }

# SAME BINDING, ONE COORDINATE MOVED. The binding is legitimate; the actions are not.
$tampered = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
$tampered[0].arguments.elements[0].end[0] = [double]$tampered[0].arguments.elements[0].end[0] + 5000
$tamper = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'a-tamper' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true; actions = $tampered })
Add-HzRefusalProbe -Run $run -Id 'A2' -Name 'a legitimate binding with an EDITED action is refused as stale' `
    -Call $tamper -MustMatch 'stale_plan[\s\S]*the actions' `
    -Expected 'stale_plan naming the actions, because a binding that does not cover what is about to be built is not a binding'

$tokens = $dry.Result.rehearsal.tokens_by_key
$applyActions = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
foreach ($a in $applyActions) {
    $k = $a.key
    if ($tokens.$k) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $tokens.$k -Force }
}
$applyKey = New-HzKey $run 'cad-apply'
$apply = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'a-apply' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $applyActions; idempotency_key = $applyKey })
$wallsAfter = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-after'
Add-HzProbe -Run $run -Id 'A3' -Name 'the apply creates exactly the planned walls, verified by re-reading' `
    -Expected ("3 more walls than {0}, created_verified=3" -f $wallsBefore) `
    -Observed ("walls={0} created_verified={1} state={2}" -f $wallsAfter, $apply.Result.created_verified, $apply.Result.state) `
    -Ok ((($wallsAfter - $wallsBefore) -eq 3) -and ([int]$apply.Result.created_verified -eq 3)) `
    -Evidence @{ stages = $apply.Result.stages; state = $apply.Result.state; atomicity = $apply.Result.atomicity }

Add-HzProbe -Run $run -Id 'A4' -Name 'every created element carries provenance, and none is left anonymous' `
    -Expected '3 written, 0 anonymous' `
    -Observed ("written={0} anonymous={1}" -f $apply.Result.provenance_written, $apply.Result.elements_left_anonymous) `
    -Ok (([int]$apply.Result.provenance_written -eq 3) -and ([int]$apply.Result.elements_left_anonymous -eq 0)) `
    -Evidence @{ provenance = $apply.Result.provenance }

# =============================================================================
# I - doing it twice
# =============================================================================
Write-Host "`n== I: doing it twice ==" -ForegroundColor Cyan
$again = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'a-again' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $applyActions; idempotency_key = $applyKey })
$wallsAgain = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-again'
Add-HzProbe -Run $run -Id 'I1' -Name 'repeating the SAME apply with the same key does not duplicate anything' `
    -Expected ("walls stay at {0}" -f $wallsAfter) -Observed ("walls={0}" -f $wallsAgain) `
    -Ok ($wallsAgain -eq $wallsAfter) `
    -Evidence @{ second_state = $again.Result.state; second_created = $again.Result.created_verified
                 idempotency = $again.Result.idempotency }

# =============================================================================
# S - things that must refuse
# =============================================================================
Write-Host "`n== S: things that must refuse ==" -ForegroundColor Cyan

$wrongSet = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$wrongSet.rules[0].height_mm = 2700.0
$sSet = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 's-set' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true; requirement_set = $wrongSet })
Add-HzRefusalProbe -Run $run -Id 'S1' -Name 'editing the requirement set invalidates the plan' `
    -Call $sSet -MustMatch 'stale_plan[\s\S]*requirement set'

$forged = $binding | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$forged.source_fingerprint = 'cadsrc:0000000000000000000000'
$sSrc = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 's-source' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true; apply_binding = $forged })
Add-HzRefusalProbe -Run $run -Id 'S2' -Name 'a forged source fingerprint is refused' -Call $sSrc -MustMatch 'stale_plan'

$otherDoc = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 's-doc' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true; target_document = 'HZ_LIVE_A' })
Add-HzRefusalProbe -Run $run -Id 'S3' -Name 'aiming the plan at another document refuses' `
    -Call $otherDoc -MustMatch '.' -Expected 'refused by the active-document guard or the target binding'

$noBinding = Copy-HzArgs $applyArgs @{ dry_run = $true }
$noBinding.Remove('apply_binding')
$sNone = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 's-no-binding' -Arguments $noBinding
Add-HzRefusalProbe -Run $run -Id 'S4' -Name 'no binding at all refuses rather than defaulting to trust' `
    -Call $sNone -MustMatch 'apply_binding is required'

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
