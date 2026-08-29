#Requires -Version 5.1
<#
  TWO DRAWINGS, TWO STOREYS, ONE MODEL.

  Every other harness converts one drawing into an empty document. A building is
  not one drawing: it is a plan per storey, often a plan per block, and they land
  in the same model under the same rules. That is the state in which the
  incremental update is most dangerous, because the question "which elements is
  this run about" stops having an obvious answer - and the wrong answer is not a
  wrong number, it is a proposal to delete another storey's work.

  So this builds the real thing: the same requirement set applied to two
  different drawings on two different storeys, and then asks each command what it
  thinks it is looking at.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-multi' -Document $Document
$X = 912000.0

function Get-HzKind {
    param($Update, [string]$Kind)
    $c = Get-HzPath $Update 'counts_by_kind', $Kind
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

# =============================================================================
# THE FIXTURE - two drawings that are not revisions of each other
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

# The two blocks stand well apart, because "which drawing is this element from"
# must never be answerable by looking at where it is.
$fixtureA = New-HzWallFixture -Run $run -Tag 'multi-a' -Walls @(
    @{ name = 'A1'; x1 = $X; y1 = 0.0; x2 = ($X + 9000.0); y2 = 0.0 },
    @{ name = 'A2'; x1 = $X; y1 = 0.0; x2 = $X; y2 = 6000.0 })
$null = Reset-HzDocument $run
$fixtureB = New-HzWallFixture -Run $run -Tag 'multi-b' -Walls @(
    @{ name = 'B1'; x1 = ($X + 30000.0); y1 = 0.0; x2 = ($X + 39000.0); y2 = 0.0 })
$run.Fixture['dwg_a'] = $fixtureA.dwg_name
$run.Fixture['dwg_b'] = $fixtureB.dwg_name
if ($fixtureA.dwg_sha256 -eq $fixtureB.dwg_sha256) {
    throw 'HARNESS: the two fixtures are the same file, so nothing here can tell them apart'
}

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

# A SECOND STOREY, created by this run under a name nothing else can hold.
$tag = $run.RunId.Substring($run.RunId.Length - 4)
$upperName = "HZU-$tag"
$upper = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'upper-level' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'level'; elevation = 60000.0; name = $upperName }) }
$upperId = [long](@($upper.Apply.Result.rows)[0].element_id)

$instA = Add-HzCadLink -Run $run -DwgPath $fixtureA.dwg_path -Label 'link-a'
$instB = Add-HzCadLink -Run $run -DwgPath $fixtureB.dwg_path -Label 'link-b'
$factsA = Get-HzCadInstanceFacts -Run $run -InstanceId $instA
$layerA = Get-HzWallLayer -Run $run -InstanceId $instA
$layerB = Get-HzWallLayer -Run $run -InstanceId $instB
Add-HzNote $run ("A on '{0}', B on '{1}'; second storey '{2}'" -f $layerA, $layerB, $upperName)

# ONE SET, BOTH DRAWINGS. That is the case worth testing: two sibling plans under
# the same rules is what a project looks like, and it is the state in which
# "which elements is this run about" stops being obvious.
$set = New-HzWallRequirementSet -Layer $layerA -Units ([string]$factsA.declared_units) -Id 'hz-live-multi'

$planA = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-a' -Arguments @{
    target_document = $Document; instance_id = $instA; requirement_set = $set
    level_id = [long]$level.element_id }
$builtA = Invoke-HzConversion -Run $run -Plan $planA.Result -Set $set -InstanceId $instA -Tag 'a'

$planB = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-b' -Arguments @{
    target_document = $Document; instance_id = $instB; requirement_set = $set
    level_id = $upperId }
$builtB = Invoke-HzConversion -Run $run -Plan $planB.Result -Set $set -InstanceId $instB -Tag 'b'

Add-HzProbe -Run $run -Id 'L1' -Name 'the same rules build BOTH drawings, each on the storey it was told' `
    -Expected 'A built on the ground storey, B on the storey this run made, both verified' `
    -Observed ("a={0} b={1}" -f $builtA.created_verified, $builtB.created_verified) `
    -Ok ([int]$builtA.created_verified -ge 2 -and [int]$builtB.created_verified -ge 1) `
    -Evidence @{ a = $builtA.state; b = $builtB.state
                 drawings = @($fixtureA.dwg_sha256, $fixtureB.dwg_sha256) }

$walls = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls')
$onGround = @($walls | Where-Object { [string](Get-HzProp $_ 'level') -eq [string]$level.name })
$onUpper = @($walls | Where-Object { [string](Get-HzProp $_ 'level') -eq $upperName })

Add-HzProbe -Run $run -Id 'L2' -Name 'and the MODEL says which storey each is on - not the call that built it' `
    -Expected 'walls on both storeys, read back from the document' `
    -Observed ("ground={0} upper={1} total={2}" -f $onGround.Count, $onUpper.Count, $walls.Count) `
    -Ok ($onGround.Count -ge 2 -and $onUpper.Count -ge 1) `
    -Evidence @{ ground_storey = [string]$level.name; upper_storey = $upperName }

# =============================================================================
# S - WHOSE WORK IS THIS
# =============================================================================
Write-Host "`n== S: whose work is this ==" -ForegroundColor Cyan

$updateA = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-a' -Arguments @{
    target_document = $Document; instance_id = $instA; requirement_set = $set
    level_id = [long]$level.element_id }

Add-HzProbe -Run $run -Id 'S1' -Name 'an update for drawing A leaves the OTHER drawing alone - it proposes no orphans at all' `
    -Expected 'orphan 0: B was built from a different file and is none of this run''s business' `
    -Observed ("leave={0} orphan={1} create={2} review={3}" -f (Get-HzKind $updateA.Result 'leave'),
        (Get-HzKind $updateA.Result 'orphan'), (Get-HzKind $updateA.Result 'create'),
        (Get-HzKind $updateA.Result 'review')) `
    -Ok ((Get-HzKind $updateA.Result 'orphan') -eq 0 -and (Get-HzKind $updateA.Result 'leave') -ge 2) `
    -Evidence @{ counts_by_kind = $updateA.Result.counts_by_kind
                 note = 'the wrong answer here is not a wrong number - it is a proposal to delete another storey' }

Add-HzProbe -Run $run -Id 'S2' -Name 'and it does not propose to BUILD B again either, on the storey it is not on' `
    -Expected 'create 0: everything drawing A says is already in the model' `
    -Observed ("create={0} automatic={1}" -f (Get-HzKind $updateA.Result 'create'),
        (Get-HzProp $updateA.Result 'automatic')) `
    -Ok ((Get-HzKind $updateA.Result 'create') -eq 0) `
    -Evidence @{ counts_by_kind = $updateA.Result.counts_by_kind }

$updateB = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-b' -Arguments @{
    target_document = $Document; instance_id = $instB; requirement_set = $set
    level_id = $upperId }
Add-HzProbe -Run $run -Id 'S3' -Name 'the same is true from the other side, which is not the same test' `
    -Expected 'B: orphan 0, create 0' `
    -Observed ("leave={0} orphan={1} create={2}" -f (Get-HzKind $updateB.Result 'leave'),
        (Get-HzKind $updateB.Result 'orphan'), (Get-HzKind $updateB.Result 'create')) `
    -Ok ((Get-HzKind $updateB.Result 'orphan') -eq 0 -and (Get-HzKind $updateB.Result 'create') -eq 0 -and
         (Get-HzKind $updateB.Result 'leave') -ge 1) `
    -Evidence @{ counts_by_kind = $updateB.Result.counts_by_kind
                 note = 'A has two walls and B has one, so a run that confused them would not divide evenly' }

# =============================================================================
# A - WHAT EACH AUDIT IS ABOUT
# =============================================================================
Write-Host "`n== A: what each audit is about ==" -ForegroundColor Cyan

$auditA = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-a' -Arguments @{
    target_document = $Document; instance_id = $instA; requirement_set = $set }
$auditB = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-b' -Arguments @{
    target_document = $Document; instance_id = $instB; requirement_set = $set }

Add-HzProbe -Run $run -Id 'A1' -Name 'the audit of A finds everything A draws, and claims nothing A does not' `
    -Expected 'by_revision >= 2, drawing_not_built 0, built_not_in_drawing 0' `
    -Observed ("by_revision={0} not_built={1} not_in_drawing={2} extent_differs={3}" -f
        $auditA.Result.matched.by_revision, (Get-HzCode $auditA.Result 'drawing_not_built'),
        (Get-HzCode $auditA.Result 'built_not_in_drawing'), (Get-HzCode $auditA.Result 'extent_differs')) `
    -Ok ([int]$auditA.Result.matched.by_revision -ge 2 -and
         (Get-HzCode $auditA.Result 'drawing_not_built') -eq 0 -and
         (Get-HzCode $auditA.Result 'built_not_in_drawing') -eq 0) `
    -Evidence @{ matched = $auditA.Result.matched; counts = $auditA.Result.counts_by_code
                 extent_differs_means = 'Revit trims a joined wall back to where the centrelines cross, so an as-built run is shorter than the line that produced it. Two walls that meet at a corner report it, and it is a fact about Revit rather than a disagreement with the drawing.' }

Add-HzProbe -Run $run -Id 'A2' -Name 'and the audit of B matches ONE, not three - the other storey is not its subject' `
    -Expected 'by_revision 1, drawing_not_built 0' `
    -Observed ("by_revision={0} not_built={1}" -f $auditB.Result.matched.by_revision,
        (Get-HzCode $auditB.Result 'drawing_not_built')) `
    -Ok ([int]$auditB.Result.matched.by_revision -eq 1 -and
         (Get-HzCode $auditB.Result 'drawing_not_built') -eq 0) `
    -Evidence @{ matched = $auditB.Result.matched; counts = $auditB.Result.counts_by_code
                 note = 'A has two walls and B has one, so an audit that swept the whole model would fail this rather than pass it by luck' }

# THE OTHER DRAWING IS NAMED, NOT HIDDEN AND NOT BLAMED. An audit that swept it
# in would report another storey as work this drawing failed to build; one that
# ignored it would let a model quietly accumulate conversions nobody remembers.
# Each is counted, once per element, as what it is.
Add-HzProbe -Run $run -Id 'A3' -Name 'each audit NAMES the other drawing''s work as the other drawing''s - by count, not by guess' `
    -Expected "A's audit sees B's 1 element as from another drawing; B's audit sees A's 2" `
    -Observed ("a_sees={0} (B built {1}) b_sees={2} (A built {3})" -f
        (Get-HzCode $auditA.Result 'built_from_another_drawing'), $builtB.created_verified,
        (Get-HzCode $auditB.Result 'built_from_another_drawing'), $builtA.created_verified) `
    -Ok ((Get-HzCode $auditA.Result 'built_from_another_drawing') -eq [int]$builtB.created_verified -and
         (Get-HzCode $auditB.Result 'built_from_another_drawing') -eq [int]$builtA.created_verified) `
    -Evidence @{ a = $auditA.Result.counts_by_code; b = $auditB.Result.counts_by_code
                 note = 'the two counts are different numbers, so a run that confused the drawings could not produce both' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
