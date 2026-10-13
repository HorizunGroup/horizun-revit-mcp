#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6 TANDA 1 (6.2), LIVE: a provenance row is `written` only
  after its transaction committed and the record read back off the element.

  Builds walls from a DWG the harness exports itself, applies the conversion plan,
  and checks every provenance row: written=true must come with written_state
  read_back, and the provenance horizun_audit_cad_model reads off the model must
  agree (no bim_without_source among the walls the apply says it stamped).

  Needs a DISPOSABLE fixture document open and active (-Document); the harness
  closes and reopens it (Reset-HzDocument) and never saves it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t1b' -Document $Document
$X = 915000.0

Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$fixture = New-HzWallFixture -Run $run -Tag 'prov' -Walls @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0; x2 = ($X + 8000.0); y2 = 0.0 },
    @{ name = 'W2'; x1 = ($X + 8000.0); y1 = 0.0; x2 = ($X + 8000.0); y2 = 6000.0 })
$run.Fixture['dwg_sha256'] = $fixture.dwg_sha256
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$layer = Get-HzWallLayer -Run $run -InstanceId $inst
$set = New-HzWallRequirementSet -Layer $layer -Units ([string]$facts.declared_units) -Id 'hz-live-prov'

Write-Host "`n== plan and apply ==" -ForegroundColor Cyan
$plan = (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set; level_id = [long]$level.element_id }).Result
$applyArgs = @{
    target_document = $Document; instance_id = $inst; requirement_set = $set
    apply_binding = $plan.apply_binding; actions = $plan.execute_plan_request.actions; candidate_index = $plan.candidate_index
}
$dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-dry' -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
$tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
$acts = @($plan.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
foreach ($a in $acts) {
    $t = Get-HzProp $tokens $a.key
    if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
}
$applied = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply' -Arguments (Copy-HzArgs $applyArgs @{
    dry_run = $false; actions = $acts; idempotency_key = (New-HzKey $run 'apply') })).Result

$rows = @(Get-HzProp $applied 'provenance')
$claimed = @($rows | Where-Object { (Get-HzProp $_ 'written') -eq $true })
$readBack = @($claimed | Where-Object { [string](Get-HzProp $_ 'written_state') -eq 'read_back' })
Add-HzProbe -Run $run -Id 'P62a' -Name '6.2 every written provenance row was read back after the commit' `
    -Expected 'provenance rows >= 1; every written=true row has written_state=read_back' `
    -Observed ("rows={0} written={1} read_back={2} provenance_written={3} anonymous={4}" -f $rows.Count, $claimed.Count,
        $readBack.Count, (Get-HzProp $applied 'provenance_written'), (Get-HzProp $applied 'elements_left_anonymous')) `
    -Ok ($rows.Count -ge 1 -and $claimed.Count -ge 1 -and $readBack.Count -eq $claimed.Count) `
    -Evidence @{ provenance = $rows }

$audit = (Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'audit' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set }).Result
$stampedIds = @($claimed | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
$anonymousStamped = @(@(Get-HzProp $audit 'findings') | Where-Object {
    [string]$_.code -eq 'bim_without_source' -and $stampedIds -contains [long](Get-HzProp $_ 'element_id') })
Add-HzProbe -Run $run -Id 'P62b' -Name '6.2 the model agrees: no element the apply stamped is reported without source' `
    -Expected 'bim_without_source 0 among the stamped elements' `
    -Observed ("stamped={0} anonymous_among_them={1} bim_without_source_total={2}" -f $stampedIds.Count, $anonymousStamped.Count,
        (Get-HzPath $audit 'counts_by_code', 'bim_without_source')) `
    -Ok ($stampedIds.Count -ge 1 -and $anonymousStamped.Count -eq 0) -Evidence @{ findings = $anonymousStamped }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
