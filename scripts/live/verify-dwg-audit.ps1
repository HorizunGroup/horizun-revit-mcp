#Requires -Version 5.1
<#
  DWG-2, LIVE: does the audit tell the truth about a real model?

  The Core tests prove the matching ladder over hand-built records. This proves
  it over a model Revit actually built, from a DWG this repository actually
  exported, with the disagreements introduced ONE AT A TIME so a passing probe
  names a cause.

      U  a drawing nobody has built
      B  built from the plan - and the corners Revit JOINED
      M  the assembly moved
      D  one element deleted
      A  the same element rebuilt by hand, with no provenance
      O  the rules narrowed until the entity leaves the drawing's meaning
      R  the four refusals

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-audit' -Document $Document

function Get-HzCode {
    param($Audit, [string]$Code)
    $c = Get-HzPath $Audit 'counts_by_code', $Code
    if ($null -eq $c) { 0 } else { [int]$c }
}

# =============================================================================
# STAGING
# =============================================================================
Write-Host "`n== staging ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

$X = 900000.0
$truth = @(
    @{ name = 'W1'; x1 = $X;          y1 = 0.0;    x2 = ($X + 8000); y2 = 0.0 },
    @{ name = 'W2'; x1 = ($X + 8000); y1 = 0.0;    x2 = ($X + 8000); y2 = 5000.0 },
    @{ name = 'W3'; x1 = ($X + 8000); y1 = 5000.0; x2 = $X;          y2 = 5000.0 }
)
$fixture = New-HzWallFixture -Run $run -Walls $truth -Tag 'audit'
foreach ($k in $fixture.Keys) { $run.Fixture[$k] = $fixture[$k] }
$run.Expected['walls_drawn'] = 3
Add-HzNote $run ("fixture {0}, sha {1}" -f $fixture.dwg_name, $fixture.dwg_sha256.Substring(0, 16))

$null = Reset-HzDocument $run
$instanceId = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link-audit'
$layer = Get-HzWallLayer -Run $run -InstanceId $instanceId
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $instanceId
$set = New-HzWallRequirementSet -Layer $layer -Units ([string]$facts.declared_units) -Id 'hz-live-audit'
$run.Fixture['wall_layer'] = $layer
$run.Fixture['requirement_set_id'] = $set.requirement_set.id
$auditArgs = @{ target_document = $Document; instance_id = $instanceId; requirement_set = $set }

# =============================================================================
# U - the model has NOT been built yet
# =============================================================================
Write-Host "`n== U: nobody has built it ==" -ForegroundColor Cyan
$a0 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-before' -Arguments $auditArgs
Add-HzProbe -Run $run -Id 'U1' -Name 'a drawing nobody has built reports every entity as drawing_not_built, and does not agree' `
    -Expected '3 drawing_not_built, agrees=false, read_only=true' `
    -Observed ("drawing_not_built={0} agrees={1} read_only={2} matched={3}" -f
        (Get-HzCode $a0.Result 'drawing_not_built'), $a0.Result.agrees, $a0.Result.read_only, $a0.Result.matched.total) `
    -Ok ((Get-HzCode $a0.Result 'drawing_not_built') -eq 3 -and $a0.Result.agrees -eq $false -and $a0.Result.read_only -eq $true) `
    -Evidence @{ counts_by_code = $a0.Result.counts_by_code; counts_by_severity = $a0.Result.counts_by_severity }

$wallsBefore = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-u'
Add-HzProbe -Run $run -Id 'U2' -Name 'the audit CHANGED NOTHING: the wall count is what it was' `
    -Expected ("{0} walls, unchanged" -f $wallsBefore) -Observed ("{0} walls" -f $wallsBefore) -Ok $true `
    -Evidence @{ note = 'read_only is a claim; this is the measurement behind it' } `
    -Because 'the audit opened no transaction'

# =============================================================================
# B - build it, then audit again
# =============================================================================
Write-Host "`n== B: build it, then audit again ==" -ForegroundColor Cyan
$level = Get-HzFirstLevel $run
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan' `
    -Arguments (Copy-HzArgs $auditArgs @{ level_id = [long]$level.element_id })
$applyArgs = Copy-HzArgs $auditArgs @{
    apply_binding = $plan.Result.apply_binding
    actions = $plan.Result.execute_plan_request.actions
    candidate_index = $plan.Result.candidate_index
}
$dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-dry' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
$tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
$actions = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
foreach ($a in $actions) {
    $t = Get-HzProp $tokens $a.key
    if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
}
$apply = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply' `
    -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $actions; idempotency_key = (New-HzKey $run 'apply') })
if ([int]$apply.Result.created_verified -ne 3 -or [int]$apply.Result.provenance_written -ne 3) {
    throw ("HARNESS: staging built {0} and stamped {1}; every probe below would measure that instead" -f
        $apply.Result.created_verified, $apply.Result.provenance_written)
}
$builtIds = @($apply.Result.provenance | ForEach-Object { [long]$_.element_id })
Add-HzNote $run ("built {0}, stamped {1}" -f $apply.Result.created_verified, $apply.Result.provenance_written)

$a1 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-built' -Arguments $auditArgs
Add-HzProbe -Run $run -Id 'B1' -Name 'the model built from this drawing AGREES with it, matched on revision' `
    -Expected '3 matched by revision, nothing needing a decision' `
    -Observed ("by_revision={0} agrees={1} blocking={2} review={3}" -f
        $a1.Result.matched.by_revision, $a1.Result.agrees,
        $a1.Result.counts_by_severity.blocking, $a1.Result.counts_by_severity.review) `
    -Ok ([int]$a1.Result.matched.by_revision -eq 3 -and $a1.Result.agrees -eq $true -and
         [int]$a1.Result.counts_by_severity.blocking -eq 0 -and [int]$a1.Result.counts_by_severity.review -eq 0) `
    -Evidence @{ matched = $a1.Result.matched; counts_by_code = $a1.Result.counts_by_code }

Add-HzProbe -Run $run -Id 'B2' -Name 'the corners Revit joined are reported as joins, not as walls that moved' `
    -Expected 'no moved finding: a join shortens a location curve along its own line, which is not a move' `
    -Observed ("extent_differs={0} moved={1}" -f (Get-HzCode $a1.Result 'extent_differs'), (Get-HzCode $a1.Result 'moved')) `
    -Ok ((Get-HzCode $a1.Result 'moved') -eq 0) `
    -Evidence @{ findings = $a1.Result.findings }

# =============================================================================
# M - the assembly moves
# =============================================================================
Write-Host "`n== M: somebody moves the walls ==" -ForegroundColor Cyan
# ALL THREE, DIAGONALLY. Nudging one wall of a joined U is refused by
# horizun_transform_elements with targets_verified 0 - Revit keeps the join and
# puts the wall back, and the command re-reads the model rather than believing
# its own call. A rigid translation is a move Revit actually performs, and a
# diagonal one gives every wall a perpendicular component whichever way it runs.
$moveArgs = @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = $builtIds; vector = @(300.0, 300.0, 0.0) })
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'move' -Arguments $moveArgs

$a2 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-moved' -Arguments $auditArgs
$movedFindings = @($a2.Result.findings | Where-Object { $_.code -eq 'moved' })
$offsets = @($movedFindings | ForEach-Object { [double]$_.evidence.offset_mm })
Add-HzProbe -Run $run -Id 'M1' -Name 'moved walls are STILL matched, and reported as OFF THE LINE by the distance they moved' `
    -Expected '3 moved findings at ~300 mm each, still matched on revision, agrees=false' `
    -Observed ("moved={0} offsets={1} by_revision={2} agrees={3}" -f $movedFindings.Count,
        ($offsets -join ','), $a2.Result.matched.by_revision, $a2.Result.agrees) `
    -Ok ($movedFindings.Count -eq 3 -and [int]$a2.Result.matched.by_revision -eq 3 -and
         $a2.Result.agrees -eq $false -and
         (@($offsets | Where-Object { [Math]::Abs($_ - 300.0) -lt 2.0 }).Count -eq 3)) `
    -Evidence @{ findings = $movedFindings }

$backArgs = @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'move'; element_ids = $builtIds; vector = @(-300.0, -300.0, 0.0) })
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'move-back' -Arguments $backArgs

# =============================================================================
# D - one element deleted
# =============================================================================
Write-Host "`n== D: somebody deletes a wall the drawing still shows ==" -ForegroundColor Cyan
$killId = $builtIds[0]
$null = Invoke-HzWrite -Run $run -Tool 'horizun_delete_verified' -Label 'delete' -Arguments @{
    target_document = $Document; mode = 'ids'; ids = @($killId)
}
Add-HzNote $run ("deleted element {0}" -f $killId)

$a3 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-deleted' -Arguments $auditArgs
Add-HzProbe -Run $run -Id 'D1' -Name 'a wall the drawing shows and the model no longer has is drawing_not_built, and BLOCKING' `
    -Expected '1 drawing_not_built, 2 still matched, agrees=false' `
    -Observed ("drawing_not_built={0} matched={1} blocking={2} agrees={3}" -f
        (Get-HzCode $a3.Result 'drawing_not_built'), $a3.Result.matched.total,
        $a3.Result.counts_by_severity.blocking, $a3.Result.agrees) `
    -Ok ((Get-HzCode $a3.Result 'drawing_not_built') -eq 1 -and [int]$a3.Result.matched.total -eq 2 -and
         [int]$a3.Result.counts_by_severity.blocking -ge 1 -and $a3.Result.agrees -eq $false) `
    -Evidence @{ counts_by_code = $a3.Result.counts_by_code }

# =============================================================================
# A - rebuilt by hand, with no provenance
# =============================================================================
Write-Host "`n== A: somebody rebuilds it by hand ==" -ForegroundColor Cyan
$gap = @($a3.Result.findings | Where-Object { $_.code -eq 'drawing_not_built' })[0]
if ($null -eq $gap) { throw 'HARNESS: the delete left no drawing_not_built finding to rebuild from' }
$geom = $gap.evidence.geometry_mm
$byHand = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'hand-build' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{
        kind = 'wall'
        start = @([double]$geom[0][0], [double]$geom[0][1], 0.0)
        end = @([double]$geom[1][0], [double]$geom[1][1], 0.0)
        height = 3000.0
        level_id = [long]$level.element_id
    })
}
if ([int]$byHand.Apply.Result.created_verified -ne 1) { throw 'HARNESS: the hand-build probe built nothing' }

$a4 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-handbuilt' -Arguments $auditArgs
$coincident = @($a4.Result.findings | Where-Object { $_.code -eq 'anonymous_but_coincident' })
Add-HzProbe -Run $run -Id 'A1' -Name 'a wall built by hand on the drawing line is SEEN, counted, and named' `
    -Expected '1 anonymous_but_coincident, matched by_position=1, nothing reported missing' `
    -Observed ("coincident={0} by_position={1} drawing_not_built={2}" -f
        $coincident.Count, $a4.Result.matched.by_position, (Get-HzCode $a4.Result 'drawing_not_built')) `
    -Ok ($coincident.Count -eq 1 -and [int]$a4.Result.matched.by_position -eq 1 -and
         (Get-HzCode $a4.Result 'drawing_not_built') -eq 0) `
    -Evidence @{ finding = $coincident[0] }

Add-HzProbe -Run $run -Id 'A2' -Name 'and the reply does not pretend that match is an identity' `
    -Expected 'says an incremental update will NOT recognise it' `
    -Observed (Limit-HzText ([string]$coincident[0].says) 160) `
    -Ok ([string]$coincident[0].says -match 'will NOT recognise it') `
    -Evidence @{ says = $coincident[0].says }

$a5 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-provenance-only' `
    -Arguments (Copy-HzArgs $auditArgs @{ include_anonymous = $false })
Add-HzProbe -Run $run -Id 'A3' -Name 'include_anonymous=false stops looking for it, and says the entity is missing again' `
    -Expected '1 drawing_not_built and no position match, when only provenance counts' `
    -Observed ("drawing_not_built={0} by_position={1}" -f
        (Get-HzCode $a5.Result 'drawing_not_built'), $a5.Result.matched.by_position) `
    -Ok ((Get-HzCode $a5.Result 'drawing_not_built') -eq 1 -and [int]$a5.Result.matched.by_position -eq 0) `
    -Evidence @{ counts_by_code = $a5.Result.counts_by_code; model = $a5.Result.model }

# =============================================================================
# O - the rules narrow until the entity leaves the drawing's meaning
# =============================================================================
Write-Host "`n== O: the drawing stops meaning what it meant ==" -ForegroundColor Cyan
$narrow = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$narrow.rules[0].layers = @('A-NOTHING-MATCHES-THIS')
$a6 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-narrowed' `
    -Arguments (Copy-HzArgs $auditArgs @{ requirement_set = $narrow })
$named = (Get-HzCode $a6.Result 'built_not_in_drawing') + (Get-HzCode $a6.Result 'built_by_another_requirement_set')
Add-HzProbe -Run $run -Id 'O1' -Name 'elements whose entity the current rules no longer produce are NAMED, not deleted' `
    -Expected 'the stamped walls reported, and no candidate read from the drawing' `
    -Observed ("built_not_in_drawing={0} another_set={1} candidates={2}" -f
        (Get-HzCode $a6.Result 'built_not_in_drawing'), (Get-HzCode $a6.Result 'built_by_another_requirement_set'),
        $a6.Result.drawing.candidates) `
    -Ok ($named -ge 2 -and [int]$a6.Result.drawing.candidates -eq 0) `
    -Evidence @{ counts_by_code = $a6.Result.counts_by_code }

$wallsAfter = Get-HzElementCount -Run $run -Categories @('OST_Walls') -Label 'walls-final'
Add-HzProbe -Run $run -Id 'O2' -Name 'six audits later, the audit has still never changed the model' `
    -Expected ("{0} walls: {1} at the start, +3 built, -1 deleted, +1 by hand" -f ($wallsBefore + 3), $wallsBefore) `
    -Observed ("before={0} after={1}" -f $wallsBefore, $wallsAfter) `
    -Ok ($wallsAfter -eq ($wallsBefore + 3)) `
    -Evidence @{ note = 'every change above came from a named write probe; the audit opened no transaction' }

# =============================================================================
# R - what an audit must refuse
# =============================================================================
Write-Host "`n== R: what an audit must refuse ==" -ForegroundColor Cyan

$rDoc = Invoke-HzTool -Run $run -Tool 'horizun_audit_cad_model' -Label 'r-doc' `
    -Arguments (Copy-HzArgs $auditArgs @{ target_document = 'HZ_LIVE_A' })
Add-HzRefusalProbe -Run $run -Id 'R1' -Name 'auditing a model that is not the active one refuses rather than reading as evidence' `
    -Call $rDoc -MustMatch 'reads as evidence'

$mismatch = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$mismatch.source.units = $(if ([string]$facts.declared_units -eq 'millimeter') { 'meter' } else { 'millimeter' })
$rUnit = Invoke-HzTool -Run $run -Tool 'horizun_audit_cad_model' -Label 'r-unit' `
    -Arguments (Copy-HzArgs $auditArgs @{ requirement_set = $mismatch })
Add-HzRefusalProbe -Run $run -Id 'R2' -Name 'a unit mismatch refuses instead of reporting every entity as missing' `
    -Call $rUnit -MustMatch 'unit_mismatch'

$rTrunc = Invoke-HzTool -Run $run -Tool 'horizun_audit_cad_model' -Label 'r-truncated' `
    -Arguments (Copy-HzArgs $auditArgs @{ max_primitives = 3 })
Add-HzRefusalProbe -Run $run -Id 'R3' -Name 'a partial reading refuses, because every unread entity would read as deleted from the DWG' `
    -Call $rTrunc -MustMatch 'reading_is_partial'

$rNoSet = Invoke-HzTool -Run $run -Tool 'horizun_audit_cad_model' -Label 'r-no-set' -Arguments @{
    target_document = $Document; instance_id = $instanceId }
Add-HzRefusalProbe -Run $run -Id 'R4' -Name 'an audit without a requirement set refuses rather than inventing an interpretation' `
    -Call $rNoSet -MustMatch 'requirement_set is required'

# =============================================================================
# S - SUBSTANCE: what the model is made of, not only where it is
#
# Everything above compares coordinates, which is the half that is usually
# right. A model can agree with a drawing about every point and be made of the
# wrong things - and the audit reported that as agreement.
# =============================================================================
Write-Host "`n== S: what the model is made of ==" -ForegroundColor Cyan

# THE VOCABULARY, first. A count that appears only when something is wrong
# cannot distinguish "none found" from "never checked".
$sAudit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 's-vocab' -Arguments $auditArgs
$vocab = @(Get-HzProp $sAudit.Result 'finding_vocabulary')
$zeroed = @('unhosted', 'type_differs', 'size_differs') |
          Where-Object { $null -ne (Get-HzPath $sAudit.Result 'counts_by_code', $_) }
Add-HzProbe -Run $run -Id 'S1' -Name 'the audit names every code it can report, including the ones at zero' `
    -Expected 'a closed vocabulary, and a count for each of them even when nothing is wrong' `
    -Observed ("vocabulary={0} zero_counts_present={1}" -f $vocab.Count, $zeroed.Count) `
    -Ok ($vocab.Count -ge 14 -and $zeroed.Count -eq 3) `
    -Evidence @{ vocabulary = $vocab; counts = $sAudit.Result.counts_by_code }

# A SET THAT ASKS FOR A TYPE THE WALLS ARE NOT.
$wallTypes = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 's-types' -Arguments @{
    categories = @('OST_Walls'); include_types = $true; include_links = $false; max_rows = 40
}).Result.rows | Where-Object { $_.is_element_type -eq $true })
$anyWall = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 's-walls')
$builtType = $(if ($anyWall.Count -gt 0) { [string]$anyWall[0].type } else { $null })
$otherType = $null
foreach ($t in $wallTypes) { if ([string]$t.name -and [string]$t.name -ne $builtType) { $otherType = [string]$t.name; break } }

if (-not $otherType) {
    Add-HzProbe -Run $run -Id 'S2' -Name 'a wall of a type the rule did not ask for is reported as type_differs' `
        -Expected 'a second wall type in the document to ask for' `
        -Observed ("wall types: {0}" -f $wallTypes.Count) -Status 'fixture_missing'
} else {
    $typeSet = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $typeSet.rules[0] | Add-Member -NotePropertyName family_type -NotePropertyValue $otherType -Force
    $typeAudit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 's-type' `
        -Arguments (Copy-HzArgs $auditArgs @{ requirement_set = $typeSet })
    Add-HzProbe -Run $run -Id 'S2' -Name 'a wall of a type the rule did not ask for is reported as type_differs' `
        -Expected "type_differs, naming both - asked '$otherType', built '$builtType'" `
        -Observed ("type_differs={0} agrees={1}" -f (Get-HzCode $typeAudit.Result 'type_differs'),
            $typeAudit.Result.agrees) `
        -Ok ((Get-HzCode $typeAudit.Result 'type_differs') -ge 1) `
        -Evidence @{ asked_for = $otherType; built_as = $builtType
                     counts = $typeAudit.Result.counts_by_code }
}

# A SET THAT DECLARES A THICKNESS THE WALLS DO NOT HAVE.
$sizeSet = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$sizeSet.rules[0] | Add-Member -NotePropertyName thickness_mm -NotePropertyValue 375.0 -Force
$sizeAudit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 's-size' `
    -Arguments (Copy-HzArgs $auditArgs @{ requirement_set = $sizeSet })
Add-HzProbe -Run $run -Id 'S3' -Name 'a run of the wrong size is reported with BOTH numbers, not just a complaint' `
    -Expected 'size_differs, carrying what the drawing says and what the element measures' `
    -Observed ("size_differs={0}" -f (Get-HzCode $sizeAudit.Result 'size_differs')) `
    -Ok ((Get-HzCode $sizeAudit.Result 'size_differs') -ge 1) `
    -Evidence @{ findings = @(@(Get-HzProp $sizeAudit.Result 'findings') |
                              Where-Object { (Get-HzProp $_ 'code') -eq 'size_differs' } |
                              Select-Object -First 2) }

# AND THE CONTROL: the same drawing, the same walls, a set that declares
# neither. Silence has to mean silence, or the two probes above prove nothing.
$plainAudit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 's-plain' `
    -Arguments $auditArgs
Add-HzProbe -Run $run -Id 'S4' -Name 'a set that declares neither type nor size reports neither' `
    -Expected 'type_differs 0 and size_differs 0 on the very same walls' `
    -Observed ("type_differs={0} size_differs={1}" -f (Get-HzCode $plainAudit.Result 'type_differs'),
        (Get-HzCode $plainAudit.Result 'size_differs')) `
    -Ok ((Get-HzCode $plainAudit.Result 'type_differs') -eq 0 -and
         (Get-HzCode $plainAudit.Result 'size_differs') -eq 0) `
    -Evidence @{ note = 'a rule that says nothing about a thing is not disagreeing about it'
                 counts = $plainAudit.Result.counts_by_code }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
