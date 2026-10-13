#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6, TANDA 4c, LIVE: 6.16 - cad_connect's declared transition
  types are committed through Guard and the fitting's type is read back.

  Two collinear rectangular ducts of different section leave a drawn 600 mm gap; a
  rehearsed cad_connect transition junction declares a transition type. Revit's own
  fitting is shorter than the drawn piece, so the declared type is TRIED: its change is
  committed, the fitting's type re-read against the declared one, the attempt rolled
  back and the state confirmed restored. The reply's sentence must carry invariant
  numbers on any machine culture.

  Needs a DISPOSABLE MEP document open and active (-Document) with a rectangular duct
  type, a supply-air duct system type and a rectangular transition fitting loaded, and
  a second document open.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t4c' -Document $Document

Write-Host "`n== fixture: two ducts and a drawn transition gap ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'duct-types' -Arguments @{
    categories = @('OST_DuctCurves', 'OST_DuctFitting', 'OST_DuctSystem'); include_types = $true; include_links = $false; max_rows = 500 }
$rows = @($q.Result.rows | Where-Object { $_.is_element_type })
$ductType = @($rows | Where-Object { [string]$_.family -eq 'Rectangular Duct' } | Select-Object -First 1)
$system = @($rows | Where-Object { [string]$_.family -eq 'Duct System' -and [string]$_.name -match 'Supply' } | Select-Object -First 1)
$transition = @($rows | Where-Object { [string]$_.family -match '^Rectangular Transition' } | Select-Object -First 1)
if ($ductType.Count -ne 1 -or $system.Count -ne 1 -or $transition.Count -ne 1) {
    throw 'HARNESS: the fixture needs a Rectangular Duct type, a supply Duct System type and a Rectangular Transition fitting' }
$X = 300000.0; $Z = 3000.0
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'ducts' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'duct'; start = @($X, 0.0, $Z); end = @(($X + 3000), 0.0, $Z); level_id = [long]$level.element_id
           type_id = [long]$ductType[0].element_id; system_type_id = [long]$system[0].element_id; width = 600; height = 400 },
        @{ kind = 'duct'; start = @(($X + 3600), 0.0, $Z); end = @(($X + 6600), 0.0, $Z); level_id = [long]$level.element_id
           type_id = [long]$ductType[0].element_id; system_type_id = [long]$system[0].element_id; width = 400; height = 300 }) }
$ids = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($ids.Count -ne 2) { throw "HARNESS: two ducts were not created ($($ids.Count))" }
$run.Fixture['ducts'] = $ids
$run.Fixture['transition_type_id'] = [long]$transition[0].element_id

Write-Host "`n== cad_connect, rehearsed, with a declared transition type ==" -ForegroundColor Cyan
$junction = @{ id = 'T1'; fitting = 'transition'; at = @(($X + 3300), 0.0, $Z); elements = @($ids[0], $ids[1]); automatic = $true
               drawn = @{ from_mm = @(($X + 3000), 0.0, $Z); to_mm = @(($X + 3600), 0.0, $Z); length_mm = 600 } }
$r = Invoke-HzTool -Run $run -Tool 'horizun_cad_connect' -Label 'connect-dry' -Arguments @{
    target_document = $Document; dry_run = $true; junctions = @(, $junction); transition_types = @(, [long]$transition[0].element_id) }
$row = $(if ($r.Ok) { @(Get-HzPath $r.Result 'junctions')[0] } else { $null })
$tried = $(if ($null -ne $row) { @(Get-HzProp $row 'transition_types_tried') } else { @() })
$t0 = $(if ($tried.Count) { $tried[0] } else { $null })
Add-HzProbe -Run $run -Id 'P616' -Name '6.16 a declared transition type is committed, read back as that type, and rolled back' `
    -Expected 'the declared type resolved and tried without a type read-back error; rolled back with the state restored' `
    -Observed ("ok={0} tried={1} resolved={2} revit_said={3} rolled_back={4} restored={5} text={6}" -f $r.Ok, $tried.Count,
        $(if ($t0) { Get-HzProp $t0 'resolved' }), $(if ($t0) { Get-HzProp $t0 'revit_said' }), $(if ($t0) { Get-HzProp $t0 'rolled_back' }),
        $(if ($t0) { Get-HzProp $t0 'state_restored' }), $(if (-not $r.Ok) { Limit-HzText $r.Text 200 } else { '' })) `
    -Ok ($r.Ok -and $null -ne $t0 -and (Get-HzProp $t0 'resolved') -eq $true -and $null -eq (Get-HzProp $t0 'revit_said') -and
         [string](Get-HzProp $t0 'rolled_back') -eq 'RolledBack' -and (Get-HzProp $t0 'state_restored') -eq $true) `
    -Evidence @{ junction = $row }
$means = $(if ($null -ne $row) { [string](Get-HzPath $row 'transition_check', 'means') } else { '' })
Add-HzProbe -Run $run -Id 'P616b' -Name 'the transition sentence carries invariant numbers' `
    -Expected 'a decimal in the sentence is written with a point, never a comma' `
    -Observed ("means={0}" -f (Limit-HzText $means 240)) `
    -Ok ($means -match '\d\.\d mm' -and $means -notmatch '\d,\d') -Evidence @{ means = $means }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
