#Requires -Version 5.1
<#
  A NUMBER AND A YES/NO THAT A RULE WRITES MUST AGREE WITH THEMSELVES ON RE-READ.

  Audit 2026-10-08 (lote 3, finding 3.2): the CAD audit and update compared a rule's
  parameter value, formatted with the CURRENT culture (0.5 -> "0,5", false -> "False"),
  against the element's AsValueString() ("0' - 6\"", "No"). A number or a boolean the
  rule itself had written could therefore never agree, and was reported as edited by a
  person. The reader now keeps the STORED value beside the display text.

  This builds one wall from a DWG the harness exports itself, with a rule that declares
  a text (Comments), a Double (Base Offset, internal units) and a Yes/No (Room Bounding),
  then audits: zero parameter_differs is the pass. A person's edit of the Double must
  still be reported, so agreement is not bought by ignoring numbers.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'cad-param-types' -Document $Document
$X = 912000.0
$comment = 'declared by the requirement set'

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

Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$fixture = New-HzWallFixture -Run $run -Tag 'ptypes' -Walls @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0; x2 = ($X + 9000.0); y2 = 0.0 })
$run.Fixture['dwg_name'] = $fixture.dwg_name
$run.Fixture['dwg_sha256'] = $fixture.dwg_sha256
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units
$layer = Get-HzWallLayer -Run $run -InstanceId $inst

$set = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-ptypes'; version = '1.0.0'; title = 'Live parameter types' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'walls'; precedence = 10; discipline = 'architecture'
                 layers = @($layer); produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
                 parameters = @{
                     'Comments' = $comment
                     'Base Offset' = @{ value = 0.5; scope = 'instance'; required = $true }
                     'Room Bounding' = @{ value = $false; scope = 'instance'; required = $true }
                 }
                 geometry = @{ from = 'double_lines'; min_thickness_mm = 100.0; max_thickness_mm = 400.0
                               min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 } })
}

Write-Host "`n== build from the set ==" -ForegroundColor Cyan
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set
    level_id = [long]$level.element_id }
$applied = Invoke-HzConversion -Run $run -Plan $plan.Result -Set $set -InstanceId $inst -Tag 'ptypes'

$walls = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls')
$readBack = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'read-params' -Arguments @{
    categories = @('OST_Walls'); include_links = $false; max_rows = 200; parameter_format = 'full'
    return_parameters = @('Comments', 'Base Offset', 'Room Bounding') }
$mine = @(@($readBack.Result.rows) | Where-Object { [string](Get-HzPath $_ 'parameters', 'Comments', 'raw') -eq $comment })
$cells = $(if ($mine.Count -ge 1) { $mine[0].parameters } else { $null })
Add-HzProbe -Run $run -Id 'T1' -Name 'the rule wrote the Double and the Yes/No as their stored values' `
    -Expected 'Base Offset raw 0.5, Room Bounding raw 0' `
    -Observed ("walls_with_comment={0} base_offset={1} room_bounding={2}" -f $mine.Count,
        (Get-HzPath $cells 'Base Offset', 'raw'), (Get-HzPath $cells 'Room Bounding', 'raw')) `
    -Ok ($mine.Count -ge 1 -and [double](Get-HzPath $cells 'Base Offset', 'raw') -eq 0.5 -and
         [int](Get-HzPath $cells 'Room Bounding', 'raw') -eq 0) `
    -Evidence @{ cells = $cells }

Write-Host "`n== audit ==" -ForegroundColor Cyan
$clean = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-clean' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set }
$differs = @(@($clean.Result.findings) | Where-Object { [string]$_.code -eq 'parameter_differs' })
Add-HzProbe -Run $run -Id 'T2' -Name 'a model built from the set AGREES with it about a number and a Yes/No' `
    -Expected 'parameter_differs 0, parameter_missing 0, parameter_unreadable 0' `
    -Observed ("differs={0} missing={1} unreadable={2} first={3}" -f (Get-HzCode $clean.Result 'parameter_differs'),
        (Get-HzCode $clean.Result 'parameter_missing'), (Get-HzCode $clean.Result 'parameter_unreadable'),
        $(if ($differs.Count) { Limit-HzText ([string]$differs[0].says) 200 } else { '' })) `
    -Ok ((Get-HzCode $clean.Result 'parameter_differs') -eq 0 -and
         (Get-HzCode $clean.Result 'parameter_missing') -eq 0 -and
         (Get-HzCode $clean.Result 'parameter_unreadable') -eq 0) `
    -Evidence @{ counts = $clean.Result.counts_by_code; differs = $differs }

$target = $(if ($mine.Count -ge 1) { [long]$mine[0].element_id } else { [long]0 })
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'person-edits' -Arguments @{
    target_document = $Document
    writes = @(@{ target_id = $target; parameter = 'Base Offset'; value = 0.75 }) }
$after = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit-edited' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set }
$changed = @(@($after.Result.findings) | Where-Object { [string]$_.code -eq 'parameter_differs' })
Add-HzProbe -Run $run -Id 'T3' -Name 'and a NUMBER changed by hand is still reported' `
    -Expected 'parameter_differs >= 1 naming Base Offset' `
    -Observed ("differs={0} first={1}" -f $changed.Count, $(if ($changed.Count) { Limit-HzText ([string]$changed[0].says) 200 } else { '' })) `
    -Ok ($changed.Count -ge 1 -and [string]$changed[0].says -match 'Base Offset') `
    -Evidence @{ differs = $changed }

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
