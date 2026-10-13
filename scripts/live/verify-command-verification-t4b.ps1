#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6, TANDA 4b, LIVE: 6.17 - plan_annotations layout defaults
  are millimetres in every unit the call declares.

  One wall is drawn and an auto_tags plan is asked for it twice, without clearance or
  max_displacement: in mm and in metres. The planned tag action carries both distances
  in the caller's units, so the same 10 mm clearance and 1200 mm displacement must read
  10 / 1200 in mm and 0.01 / 1.2 in metres. Before the fix the defaults were taken in
  the declared unit: 10 METRES of clearance with units=m.

  Needs a DISPOSABLE document open and active (-Document) with a floor plan, and a
  second document open.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t4b' -Document $Document

Write-Host "`n== fixture: one wall in a floor plan ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$views = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_planimetry' -Label 'views' -Arguments @{ mode = 'views'; max_rows = 500 }
$level = Get-HzFirstLevel $run
$plans = @($views.Result.rows | Where-Object { [string]$_.view_type -eq 'FloorPlan' -and -not $_.is_template })
$onLevel = @($plans | Where-Object { [string](Get-HzProp $_ 'level_name') -eq [string]$level.name })
$plan = $(if ($onLevel.Count) { $onLevel[0] } elseif ($plans.Count) { $plans[0] } else { $null })
if ($null -eq $plan) { throw 'HARNESS: the fixture has no floor plan' }
$viewId = [long]$plan.view_id
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'wall' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'wall'; start = @(0.0, 0.0, 0.0); end = @(4000.0, 0.0, 0.0); height = 3000.0; level_id = [long]$level.element_id }) }
$wall = [long](Get-HzProp @($made.Apply.Result.rows)[0] 'element_id')
$run.Fixture['wall'] = $wall
$run.Fixture['view_id'] = $viewId

function Get-LayoutOf($reply) {
    $json = $reply.Result | ConvertTo-Json -Depth 60 -Compress
    $c = [regex]::Match($json, '"layout_clearance":\s*([-0-9.eE+]+)')
    $m = [regex]::Match($json, '"layout_max_displacement":\s*([-0-9.eE+]+)')
    [pscustomobject]@{
        clearance = $(if ($c.Success) { [double]::Parse($c.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { $null })
        max_displacement = $(if ($m.Success) { [double]::Parse($m.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { $null })
    }
}

Write-Host "`n== auto_tags in mm and in metres ==" -ForegroundColor Cyan
$got = @{}
foreach ($u in 'mm', 'm') {
    $r = Invoke-HzTool -Run $run -Tool 'horizun_plan_annotations' -Label "tags-$u" -Arguments @{
        operation = 'auto_tags'; view_id = $viewId; element_ids = @(, $wall); tag_mode = 'by_category'
        skip_existing = $false; add_leader = $false; units = $u; distance_space = 'model' }
    $got[$u] = [pscustomobject]@{ ok = $r.Ok; text = $r.Text; layout = $(if ($r.Ok) { Get-LayoutOf $r } else { $null }); raw = $r.Result }
}
function Near($a, [double]$b) { $null -ne $a -and [Math]::Abs([double]$a - $b) -le 1e-6 * [Math]::Max(1, [Math]::Abs($b)) }
$mm = $got['mm']; $m = $got['m']
Add-HzProbe -Run $run -Id 'P617' -Name '6.17 layout defaults are millimetres whatever unit the call declares' `
    -Expected 'mm: clearance 10, max_displacement 1200; m: clearance 0.01, max_displacement 1.2' `
    -Observed ("mm ok={0} c={1} d={2} | m ok={3} c={4} d={5} | {6}" -f $mm.ok, $(if ($mm.layout) { $mm.layout.clearance }), $(if ($mm.layout) { $mm.layout.max_displacement }),
        $m.ok, $(if ($m.layout) { $m.layout.clearance }), $(if ($m.layout) { $m.layout.max_displacement }),
        (Limit-HzText $(if (-not $m.ok) { $m.text } elseif (-not $mm.ok) { $mm.text } else { '' }) 200)) `
    -Ok ($mm.ok -and $m.ok -and (Near $mm.layout.clearance 10) -and (Near $mm.layout.max_displacement 1200) -and
         (Near $m.layout.clearance 0.01) -and (Near $m.layout.max_displacement 1.2)) `
    -Evidence @{ mm = $mm.raw; m = $m.raw }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
