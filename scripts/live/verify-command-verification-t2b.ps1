#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6, TANDA 2b, LIVE: 6.10 - coordination show reads EVERY
  override back, and its verdict depends on that and on the section box.

  A wall is drawn through a floor, a clash run records the pair, and show paints the
  finding in a new 3D view. The reply must count the overrides it asked for and the
  ones it read back - equal and above zero - and verify the section box, which the
  bridge reads off the view it created.

  Needs a DISPOSABLE document open and active (-Document) with at least one floor, and
  a second document open. The coordination ledger written for that document lives in
  the bridge's state folder, keyed by the disposable document.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t2b' -Document $Document

Write-Host "`n== fixture: a wall through a floor ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'wall-and-floor' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        # ONE loop: the comma keeps PowerShell from flattening it into four loops.
        @{ kind = 'floor'; level_id = [long]$level.element_id
           profile = @(, @(@(50000.0, 50000.0, 0.0), @(54000.0, 50000.0, 0.0), @(54000.0, 54000.0, 0.0), @(50000.0, 54000.0, 0.0))) },
        @{ kind = 'wall'; level_id = [long]$level.element_id; start = @(52000.0, 49000.0, -1000.0); end = @(52000.0, 55000.0, -1000.0); height = 3000.0 }) }
$ids = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
$run.Fixture['elements'] = $ids

Write-Host "`n== clash, recorded ==" -ForegroundColor Cyan
$clash = Invoke-HzToolStrict -Run $run -Tool 'horizun_clash' -Label 'clash' -Arguments @{
    categories_a = @('OST_Walls'); categories_b = @('OST_Floors'); include_links = $false; record_findings = $true; max_results = 200 }
$list = Invoke-HzToolStrict -Run $run -Tool 'horizun_coordination' -Label 'list' -Arguments @{ operation = 'list' }
# THIS fixture's pair: the clash point lies where the wall crosses the floor.
$mine = @(@(Get-HzPath $list.Result 'rows') | Where-Object {
    $p = @(Get-HzProp $_ 'point_mm'); $p.Count -ge 2 -and [Math]::Abs([double]$p[0] - 52000) -lt 2100 -and [Math]::Abs([double]$p[1] - 52000) -lt 2100 })
# The pair THIS run drew: a reset deletes earlier runs' elements, and their findings stay in the ledger.
$findingIds = @($mine | Sort-Object { [string](Get-HzProp $_ 'last_seen_utc') } -Descending | Select-Object -First 1 |
    ForEach-Object { [string](Get-HzProp $_ 'finding_id') } | Where-Object { $_ })
if ($findingIds.Count -eq 0) { throw "HARNESS: the clash run recorded no finding to show ($(Limit-HzText $clash.Text 200))" }

Write-Host "`n== show ==" -ForegroundColor Cyan
$show = Invoke-HzWrite -Run $run -Tool 'horizun_coordination' -Label 'show' -AllowRefusal -Arguments @{
    target_document = $Document; operation = 'show'; finding_ids = $findingIds; view_name = 'HZ night coordination 2b' }
$r = $(if ($show.Apply) { $show.Apply } else { $show.Dry })
$res = $r.Result
$requested = Get-HzProp $res 'overrides_requested'
$applied = Get-HzProp $res 'overrides_applied'
$box = @(@(Get-HzPath $res 'postconditions', 'properties') | Where-Object { [string](Get-HzProp $_ 'property') -eq 'section_box_active' })
$boxVerified = $box.Count -eq 1 -and (Get-HzProp $box[0] 'measured') -eq $true -and (Get-HzProp $box[0] 'matches') -eq $true
Add-HzProbe -Run $run -Id 'P610' -Name '6.10 show reads every override back and verifies the section box' `
    -Expected 'applied; overrides_requested = overrides_applied > 0; section_box_active verified' `
    -Observed ("applied={0} requested={1} read_back={2} box_verified={3} painted_requested={4} text={5}" -f ($null -ne $show.Apply -and $show.Apply.Ok),
        $requested, $applied, $boxVerified, (Get-HzProp $res 'elements_painted_requested'), (Limit-HzText $r.Text 200)) `
    -Ok ($null -ne $show.Apply -and $show.Apply.Ok -and $null -ne $requested -and [int]$requested -gt 0 -and [int]$requested -eq [int]$applied -and $boxVerified) `
    -Evidence @{ show = $res; findings = $findingIds }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
