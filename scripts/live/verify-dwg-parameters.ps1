#Requires -Version 5.1
<#
  THE PARAMETERS A RULE DECLARES, AND THE ONE WRITER THAT KEEPS THEM.

  A drawing carries no fire rating, no phase, no cost code. A layer does: that is
  what a requirement set is for, and `parameters` on a rule is where an
  organisation says "everything on this layer is a 60-minute wall".

  Two things make this worth its own harness rather than a unit test.

  THE VALUES ARE WRITTEN BY THE ONE WRITER THAT RE-READS THEM. There is exactly
  one place in this bridge that coerces, refuses and verifies a parameter write,
  and the conversion hands them to it rather than growing a second set of rules
  about units. A conversion that wrote them itself would be a second answer to
  "what does 60 mean".

  AND IT IS NOT ONE TRANSACTION. Revit commits the create before the ids exist to
  write against, so the parameters are a second write. That is a real property of
  the thing and the apply says so out loud, because a report that implied
  atomicity would be a lie somebody plans a rollback around.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-parameters' -Document $Document
$X = 908000.0
$mark = "HZ-$($run.RunId.Substring($run.RunId.Length - 4))"
$comment = 'declared by the requirement set, not by the drawing'
$run.Expected['mark'] = $mark
$run.Expected['comments'] = $comment

function Get-HzClass {
    param($Update, [string]$Name)
    $c = Get-HzPath $Update 'counts_by_classification', $Name
    if ($null -eq $c) { -1 } else { [int]$c }
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
  The parameters block of the first stage that has one. Every claim about how
  they were written comes from here rather than from the call that asked.
#>
function Get-HzParameterOutcome {
    param($Applied)
    foreach ($stage in @(Get-HzProp $Applied 'stages')) {
        $p = Get-HzProp $stage 'parameters'
        if ($null -ne $p) { return $p }
    }
    $null
}

function New-HzParamSet {
    param([string]$Id, [string]$Layer, [string]$Units, $Parameters)
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = "hz-live-$Id"; version = '1.0.0'; title = "Live $Id" }
        source = @{ units = $Units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @(@{ id = 'walls'; precedence = 10; discipline = 'architecture'
                     layers = @($Layer); produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
                     parameters = $Parameters
                     geometry = @{ from = 'double_lines'; min_thickness_mm = 100.0; max_thickness_mm = 400.0
                                   min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 } })
    }
}

# =============================================================================
# THE FIXTURE - one wall, and a rule with something to say about it
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$fixture = New-HzWallFixture -Run $run -Tag 'param' -Walls @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0; x2 = ($X + 9000.0); y2 = 0.0 })
$run.Fixture['dwg_name'] = $fixture.dwg_name
$run.Fixture['dwg_sha256'] = $fixture.dwg_sha256

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units
$layer = Get-HzWallLayer -Run $run -InstanceId $inst
Add-HzNote $run ("wall layer '{0}'" -f $layer)

$set = New-HzParamSet -Id 'params' -Layer $layer -Units $units -Parameters @{
    'Comments' = $comment
    'Mark' = @{ value = $mark; scope = 'instance'; required = $true }
}

# =============================================================================
# R - THE DECLARED VALUES REACH THE ELEMENT
# =============================================================================
Write-Host "`n== R: what the rule says about the layer ==" -ForegroundColor Cyan

$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set
    level_id = [long]$level.element_id }
$rows = @($plan.Result.execute_plan_request.actions)
$row = $(if ($rows.Count -gt 0) { @($rows[0].arguments.elements)[0] } else { $null })
$declared = @(Get-HzProp $row 'parameters')
$names = @($declared | ForEach-Object { [string](Get-HzProp $_ 'parameter') } | Sort-Object)

Add-HzProbe -Run $run -Id 'R1' -Name 'the values a rule declares travel ON the row that makes the element' `
    -Expected 'both parameters on the create row, each with its value and scope' `
    -Observed ("parameters={0} names={1}" -f $declared.Count, ($names -join ',')) `
    -Ok ($declared.Count -eq 2 -and $names -contains 'Comments' -and $names -contains 'Mark') `
    -Evidence @{ declared = $declared
                 note = 'carried with the row so the two cannot drift apart - a parameter list resolved separately would be a second answer to which element it belonged to' }

$applied = Invoke-HzConversion -Run $run -Plan $plan.Result -Set $set -InstanceId $inst -Tag 'params'
$outcome = Get-HzParameterOutcome $applied
if ($null -eq $outcome) { throw 'HARNESS: the apply reported no parameter outcome at all' }

$walls = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls')
$wallIds = @($walls | ForEach-Object { [long]$_.element_id })
if ($wallIds.Count -lt 1) { throw 'HARNESS: the conversion built no wall to carry parameters' }

# AND ASK THE MODEL. What the apply reports is what its writer re-read; this is
# the document, one call later, through a different tool.
$readBack = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'read-params' -Arguments @{
    categories = @('OST_Walls'); include_links = $false; max_rows = 200
    return_parameters = @('Comments', 'Mark') }
# A PROJECTED PARAMETER IS A CELL, NOT A SCALAR: it carries whether it exists
# and whether it could be read, and 'raw' is the value. Comparing the cell to a
# string finds nothing and looks exactly like a write that never happened.
$mine = @(@($readBack.Result.rows) | Where-Object {
    $cell = Get-HzPath $_ 'parameters', 'Mark'
    $value = $(if ($cell -is [string]) { $cell } else { Get-HzProp $cell 'raw' })
    [string]$value -eq $mark })

Add-HzProbe -Run $run -Id 'R2' -Name 'the values are written by the ONE verified writer, and the model holds them' `
    -Expected ("written_by horizun_write_params_verified, all_written, and the model reads Mark='{0}'" -f $mark) `
    -Observed ("written_by={0} all_written={1} requested={2} model_rows_with_that_mark={3}" -f
        (Get-HzProp $outcome 'written_by'), (Get-HzProp $outcome 'all_written'),
        (Get-HzProp $outcome 'requested'), $mine.Count) `
    -Ok ([string](Get-HzProp $outcome 'written_by') -eq 'horizun_write_params_verified' -and
         (Get-HzProp $outcome 'all_written') -eq $true -and
         [int](Get-HzProp $outcome 'requested') -ge 2 -and $mine.Count -ge 1) `
    -Evidence @{ outcome = $outcome
                 note = 'one writer, because a conversion that wrote parameters itself would be a second set of rules about what a value means' }

Add-HzProbe -Run $run -Id 'R3' -Name 'and the apply says out loud that creation and parameters are NOT one transaction' `
    -Expected 'atomic_with_creation false, with the consequence stated' `
    -Observed ("atomic_with_creation={0}" -f (Get-HzProp $outcome 'atomic_with_creation')) `
    -Ok ((Get-HzProp $outcome 'atomic_with_creation') -eq $false -and
         $null -ne (Get-HzProp $outcome 'atomicity_means')) `
    -Evidence @{ means = (Get-HzProp $outcome 'atomicity_means')
                 note = 'Revit commits the create before the ids exist to write against; a report that implied otherwise is one somebody plans a rollback around' }

# =============================================================================
# A - THE AUDIT READS BACK WHAT THE RULE NAMED
# =============================================================================
Write-Host "`n== A: the audit ==" -ForegroundColor Cyan

$clean = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-clean' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set }
Add-HzProbe -Run $run -Id 'A1' -Name 'a model built from the set AGREES with it about every declared value' `
    -Expected 'parameter_differs 0, parameter_missing 0, parameter_unreadable 0' `
    -Observed ("differs={0} missing={1} unreadable={2}" -f (Get-HzCode $clean.Result 'parameter_differs'),
        (Get-HzCode $clean.Result 'parameter_missing'), (Get-HzCode $clean.Result 'parameter_unreadable')) `
    -Ok ((Get-HzCode $clean.Result 'parameter_differs') -eq 0 -and
         (Get-HzCode $clean.Result 'parameter_missing') -eq 0 -and
         (Get-HzCode $clean.Result 'parameter_unreadable') -eq 0) `
    -Evidence @{ counts = $clean.Result.counts_by_code }

# A PERSON CHANGES ONE. The drawing has not moved and neither has the element:
# the only thing that differs is a value the set is the sole source of.
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'person-edits' -Arguments @{
    target_document = $Document
    writes = @(@{ target_id = $wallIds[0]; parameter = 'Mark'; value = 'CHANGED-BY-HAND' }) }

$after = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-edited' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set }
$differs = @(@($after.Result.findings) | Where-Object { [string]$_.code -eq 'parameter_differs' })

Add-HzProbe -Run $run -Id 'A2' -Name 'a value changed BY HAND is reported, because the set is the only place it came from' `
    -Expected 'parameter_differs >= 1, naming the parameter and both values' `
    -Observed ("differs={0} first={1}" -f (Get-HzCode $after.Result 'parameter_differs'),
        $(if ($differs.Count -ge 1) { Limit-HzText ($differs[0] | ConvertTo-Json -Depth 6 -Compress) 180 } else { '(none)' })) `
    -Ok ((Get-HzCode $after.Result 'parameter_differs') -ge 1 -and $differs.Count -ge 1) `
    -Evidence @{ finding = $(if ($differs.Count -ge 1) { $differs[0] } else { $null })
                 counts = $after.Result.counts_by_code }

$vocabulary = @('parameter_differs', 'parameter_missing', 'parameter_unreadable')
$absent = @($vocabulary | Where-Object { (Get-HzCode $after.Result $_) -lt 0 })
Add-HzProbe -Run $run -Id 'A3' -Name 'and all three parameter codes are published, including the ones that are zero' `
    -Expected 'every code present in counts_by_code' `
    -Observed ("published={0} absent={1}" -f (@($vocabulary).Count - $absent.Count),
        $(if ($absent.Count -eq 0) { 'none' } else { $absent -join ',' })) `
    -Ok ($absent.Count -eq 0) `
    -Evidence @{ counts = $after.Result.counts_by_code
                 note = 'a code that disappears reads as "not measured" rather than "none found"' }

# =============================================================================
# I - THE INCREMENTAL SEES IT TOO
# =============================================================================
Write-Host "`n== I: the next run ==" -ForegroundColor Cyan

# THE DRAWING HAS NOT MOVED. Every comparison of geometry reports this model as
# unchanged, and it is not: somebody edited a value the set is the sole source
# of. That is the one kind of change a drawing can never report, and the update
# was blind to all of them until this was measured.
$update = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set
    level_id = [long]$level.element_id }
$diverged = @(@(Get-HzProp $update.Result 'plan') |
              Where-Object { (Get-HzProp $_ 'classification') -eq 'manually_diverged' })
$firstDiverged = $(if ($diverged.Count -ge 1) { $diverged[0] } else { $null })

Add-HzProbe -Run $run -Id 'I1' -Name 'the incremental reports the edited value, on a drawing that has not moved' `
    -Expected 'manually_diverged >= 1, as a review, naming the parameter and both values' `
    -Observed ("manually_diverged={0} field={1} set_says={2} model_holds={3}" -f
        (Get-HzClass $update.Result 'manually_diverged'),
        [string](Get-HzPath $firstDiverged 'evidence', 'field'),
        [string](Get-HzPath $firstDiverged 'evidence', 'set_says'),
        [string](Get-HzPath $firstDiverged 'evidence', 'model_holds')) `
    -Ok ((Get-HzClass $update.Result 'manually_diverged') -ge 1 -and $null -ne $firstDiverged -and
         [string](Get-HzProp $firstDiverged 'kind') -eq 'review' -and
         [string](Get-HzPath $firstDiverged 'evidence', 'model_holds') -eq 'CHANGED-BY-HAND') `
    -Evidence @{ action = $firstDiverged
                 counts_by_classification = $update.Result.counts_by_classification }

Add-HzProbe -Run $run -Id 'I2' -Name 'and it proposes NOTHING automatic, because overwriting would discard the decision' `
    -Expected 'automatic 0' `
    -Observed ("automatic={0} moved={1} unchanged={2}" -f (Get-HzProp $update.Result 'automatic'),
        (Get-HzClass $update.Result 'moved'), (Get-HzClass $update.Result 'unchanged')) `
    -Ok ([int](Get-HzProp $update.Result 'automatic') -eq 0 -and
         (Get-HzClass $update.Result 'moved') -eq 0) `
    -Evidence @{ says = $(if ($null -ne $firstDiverged) { Get-HzProp $firstDiverged 'says' } else { $null })
                 note = 'overwriting discards the decision and silence hides it - neither belongs to an unattended run' }

# =============================================================================
# X - THE REFUSALS
# =============================================================================
Write-Host "`n== X: what the set will not accept ==" -ForegroundColor Cyan

$badScope = New-HzParamSet -Id 'params-badscope' -Layer $layer -Units $units -Parameters @{
    'Comments' = @{ value = 'x'; scope = 'everything' } }
$scopeCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-badscope' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $badScope
    level_id = [long]$level.element_id }
$scopeText = $(if ($scopeCall.IsError) { [string]$scopeCall.Text } else { ($scopeCall.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'X1' -Name 'a scope that is neither instance nor type is refused, and says why type is dangerous' `
    -Expected 'refused, naming instance and type, and that a type write reaches elements this run did not create' `
    -Observed (Limit-HzText $scopeText 240) `
    -Ok ($scopeText -match 'instance or type') `
    -Evidence @{ reply = (Limit-HzText $scopeText 700) }

$noValue = New-HzParamSet -Id 'params-novalue' -Layer $layer -Units $units -Parameters @{
    'Comments' = @{ scope = 'instance' } }
$valueCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-novalue' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $noValue
    level_id = [long]$level.element_id }
$valueText = $(if ($valueCall.IsError) { [string]$valueCall.Text } else { ($valueCall.Result | ConvertTo-Json -Depth 12 -Compress) })
Add-HzProbe -Run $run -Id 'X2' -Name 'a parameter declared with NO value is refused rather than writing an empty one' `
    -Expected 'refused - omit the parameter rather than declaring one with nothing to write' `
    -Observed (Limit-HzText $valueText 240) `
    -Ok ($valueText -match 'declares no value') `
    -Evidence @{ reply = (Limit-HzText $valueText 700) }

$unknown = New-HzParamSet -Id 'params-unknown' -Layer $layer -Units $units -Parameters @{
    'HZ_NO_SUCH_PARAMETER_ANYWHERE' = 'x' }
$unknownPlan = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-unknown' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $unknown
    level_id = [long]$level.element_id }
$unknownText = $(if ($unknownPlan.IsError) { [string]$unknownPlan.Text } else { 'planned' })
$unknownOutcome = $null
if (-not $unknownPlan.IsError) {
    $unknownApplied = Invoke-HzConversion -Run $run -Plan $unknownPlan.Result -Set $unknown -InstanceId $inst -Tag 'unknown'
    $unknownOutcome = Get-HzParameterOutcome $unknownApplied
    $unknownText = ("state={0} all_written={1}" -f (Get-HzProp $unknownApplied 'state'),
                    (Get-HzProp $unknownOutcome 'all_written'))
}
Add-HzProbe -Run $run -Id 'X3' -Name 'a parameter NO element carries leaves the stage applied_without_parameters, never clean' `
    -Expected 'the elements exist and the stage says the parameters did not' `
    -Observed (Limit-HzText $unknownText 240) `
    -Ok ($unknownPlan.IsError -or
         ([string](Get-HzProp $unknownApplied 'state') -eq 'applied_without_parameters' -or
          (Get-HzProp $unknownOutcome 'all_written') -eq $false)) `
    -Evidence @{ outcome = $unknownOutcome
                 note = 'the ids are kept, so the fix is to write the parameters - never to build the elements again' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
