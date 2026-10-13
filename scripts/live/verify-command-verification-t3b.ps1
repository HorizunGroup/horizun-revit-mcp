#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6, TANDA 3b, LIVE: 6.12 on the LINKED path - navigate select
  answers Ok only when Revit's selection holds what was asked, and a linked target is
  verified as its link instance.

  A disposable copy of the link source (LinkSourceDocument in live-fixtures.json, a
  model of the run's year that is not a copy of the host) is linked into the document,
  one of its walls, pipes or ducts is selected through (element_id, link_instance_id), then the same
  wall together with a host wall. Both must answer Ok with link_instances_verified -
  if Revit did not report the link instance after SetReferences, the 6.12 refusal would
  fail every linked selection, and this is where that would show.

  Needs a DISPOSABLE document open and active (-Document), and a second document open.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$LinkSource,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t3b' -Document $Document

Write-Host "`n== fixture: a link and a host wall ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
if (-not (Test-Path -LiteralPath $LinkSource)) { throw "HARNESS: no link source at $LinkSource" }
$copy = Join-Path ([IO.Path]::GetTempPath()) ('HZ_NAV_LINK_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.rvt')
Copy-Item -LiteralPath $LinkSource -Destination $copy -Force
$add = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'link-add' -Arguments @{
    operation = 'add'; target_document = $Document; path = $copy.Replace([char]92, '/') }
$inst = [long](Get-HzProp $add.Apply.Result 'link_instance_id')
$level = Get-HzFirstLevel $run
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'host-wall' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'wall'; start = @(-30000.0, -30000.0, 0.0); end = @(-26000.0, -30000.0, 0.0); height = 3000.0; level_id = [long]$level.element_id }) }
$hostWall = [long](Get-HzProp @($made.Apply.Result.rows)[0] 'element_id')
$run.Fixture['link_instance_id'] = $inst
$run.Fixture['host_wall'] = $hostWall

$q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'linked-walls' -Arguments @{
    categories = @('OST_Walls', 'OST_PipeCurves', 'OST_DuctCurves'); include_links = $true; link_instance_ids = @($inst); include_types = $false; max_rows = 500 }
$linked = @(@($q.Result.rows) | Where-Object { [long](Get-HzProp $_ 'link_instance_id') -eq $inst })
if ($linked.Count -eq 0) { throw "HARNESS: the link $inst lists no wall, pipe or duct ($(Limit-HzText $q.Text 200))" }
$linkedWall = [long](Get-HzProp $linked[0] 'element_id')
$run.Fixture['linked_wall'] = $linkedWall

Write-Host "`n== select a linked wall ==" -ForegroundColor Cyan
$one = Invoke-HzTool -Run $run -Tool 'horizun_navigate' -Label 'select-linked' -Arguments @{
    target_document = $Document; operation = 'select'; selections = @(, @{ element_id = $linkedWall; link_instance_id = $inst }) }
Add-HzProbe -Run $run -Id 'P612b' -Name '6.12 a linked selection answers Ok, verified as its link instance' `
    -Expected 'Ok; link_instances_verified true; linked_elements_verified false (what Revit exposes)' `
    -Observed ("ok={0} links_verified={1} linked_elements_verified={2} selected_after={3} text={4}" -f $one.Ok,
        (Get-HzProp $one.Result 'link_instances_verified'), (Get-HzProp $one.Result 'linked_elements_verified'),
        (Get-HzProp $one.Result 'selected_after'), $(if (-not $one.Ok) { Limit-HzText $one.Text 200 } else { '' })) `
    -Ok ($one.Ok -and (Get-HzProp $one.Result 'link_instances_verified') -eq $true) -Evidence @{ reply = $one.Result; text = $one.Text }

Write-Host "`n== select a linked wall and a host wall ==" -ForegroundColor Cyan
$two = Invoke-HzTool -Run $run -Tool 'horizun_navigate' -Label 'select-mixed' -Arguments @{
    target_document = $Document; operation = 'select'
    selections = @(@{ element_id = $linkedWall; link_instance_id = $inst }, @{ element_id = $hostWall }) }
Add-HzProbe -Run $run -Id 'P612c' -Name '6.12 a host and a linked selection together answer Ok with both halves verified' `
    -Expected 'Ok; host_elements_verified and link_instances_verified true' `
    -Observed ("ok={0} host_verified={1} links_verified={2} selected_after={3} text={4}" -f $two.Ok,
        (Get-HzProp $two.Result 'host_elements_verified'), (Get-HzProp $two.Result 'link_instances_verified'),
        (Get-HzProp $two.Result 'selected_after'), $(if (-not $two.Ok) { Limit-HzText $two.Text 200 } else { '' })) `
    -Ok ($two.Ok -and (Get-HzProp $two.Result 'host_elements_verified') -eq $true -and (Get-HzProp $two.Result 'link_instances_verified') -eq $true) `
    -Evidence @{ reply = $two.Result; text = $two.Text }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
