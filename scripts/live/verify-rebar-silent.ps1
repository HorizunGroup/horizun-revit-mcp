#Requires -Version 5.1
<#
  ACERO 3.0, L8, LIVE: silent failures turned into named refusals, on a DISPOSABLE fixture.

    Q1  a column on the first level with a 3000 mm base offset and a height is created where asked
        (create_elements used to refuse it: the base was set before the top)
    Q2  steel planned into a member of a model group placed twice is refused by name before any write
        (Revit removes such steel at the commit without an error)
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-silent' -Document $Document
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colType = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'col-types' -Arguments @{
    categories = @('OST_StructuralColumns'); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'M_Concrete-Rectangular-Column' -and [string]$_.name -eq '450 x 600mm' })[0]
$X0 = 170000.0

Write-Host "`n== Q1 ==" -ForegroundColor Cyan
$c = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'offset-column' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_column'; type_id = [long]$colType.element_id; level_id = $lv; point = @($X0, 0.0, 3000.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }
$cid = $(if ($c.Apply -and $c.Apply.Ok) { [long](Get-HzProp @($c.Apply.Result.rows)[0] 'element_id') } else { $null })
$z0 = $null; $z1 = $null
if ($cid) {
    $b = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'box' -Arguments @{ element_ids = @(, $cid); include_links = $false; include_bounding_box = $true; max_rows = 5 }).Result.rows)[0].bounding_box
    $z0 = [double]$b.min[2]; $z1 = [double]$b.max[2]
}
$lvZ = 0.0; try { $lvZ = [double]$level.bounding_box.min[2] } catch { }
Add-HzProbe -Run $run -Id 'Q1' -Name 'L8 a column with a base offset and a height is created where it was asked' -Expected 'created; base at level + 3000, top at level + 6000' `
    -Observed ("created={0} z0={1} z1={2} {3}" -f [bool]$cid, $z0, $z1, $(if (-not $cid) { Limit-HzText $(if ($c.Apply) { $c.Apply.Text } else { $c.Dry.Text }) 1500 } else { '' })) `
    -Ok ($cid -and [Math]::Abs($z0 - ($lvZ + 3000)) -lt 1 -and [Math]::Abs($z1 - ($lvZ + 6000)) -lt 1) -Evidence @{ id = $cid }

Write-Host "`n== Q2 ==" -ForegroundColor Cyan
$g = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'group-column' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_column'; type_id = [long]$colType.element_id; level_id = $lv; point = @(($X0 + 3000), 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }
$member = [long](Get-HzProp @($g.Apply.Result.rows)[0] 'element_id')
$grp = Invoke-HzWrite -Run $run -Tool 'horizun_manage_groups' -Label 'group' -AllowRefusal -Arguments @{ target_document = $Document; operation = 'create'; element_ids = @(, $member); name = 'HZ L8 bay' }
$gid = $null
try { $gid = [long](@(Get-HzPath $grp.Apply.Result 'rows')[0].group_id) } catch { }
if (-not $gid) { try { $gid = [long](Get-HzPath $grp.Apply.Result 'group_id') } catch { } }
# The reply's shape is not pinned here: any group id it names will do.
if (-not $gid -and $grp.Apply) { $m = [regex]::Match([string]$grp.Apply.Text, '"(?:group_id|group_instance_id|instance_id)"\s*:\s*(\d+)'); if ($m.Success) { $gid = [long]$m.Groups[1].Value } }
$copy = $(if ($gid) { Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'copy-group' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
    operations = @(, @{ operation = 'copy'; element_ids = @(, $gid); vector = @(0.0, 3000.0, 0.0) }) } } else { $null })
$set = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = 'l8-group'; version = '1' }
    bar_types = @(, @{ id = 'N16'; type_name = '16M' })
    section_rules = @(, [ordered]@{ id = 'Q2'; host = @{ element_ids = @(, $member) }; cover = @{ source = 'host' }
        layers = @(, @{ face = 'perimeter'; count = 4; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true }) }) }
$row = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result 'reinforcement'))[0]
Add-HzProbe -Run $run -Id 'Q2' -Name 'L8 steel in a member of a group placed twice is refused by name before any write' -Expected 'host_in_model_group, naming the instance count' `
    -Observed ("group={0} copied={1} code={2} why={3}" -f $gid, $(if ($copy -and $copy.Apply) { $copy.Apply.Ok } else { $false }), [string](Get-HzProp $row 'code'), (Limit-HzText ([string](Get-HzProp $row 'why')) 300)) `
    -Ok ([string](Get-HzProp $row 'code') -eq 'host_in_model_group') -Evidence @{ row = $row; group = $(if ($grp.Apply) { $grp.Apply.Text } else { $grp.Dry.Text }) }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
