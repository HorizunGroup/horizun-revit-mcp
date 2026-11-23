#Requires -Version 5.1
<#
  FIXTURE HELPERS shared by the live harnesses: what a harness needs to find in
  the document before it builds anything of its own, read by type and geometry,
  never by a name Revit chose in some language.

  Depends on horizun-live.lib.ps1 being dot-sourced first.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
  The first floor plan of the document. horizun_manage_views CREATES
  views; horizun_query_planimetry mode='views' is what LISTS them - matched on
  view_type, which is an API enum, never on a name Revit chose in some language.
#>
function Get-HzFirstFloorPlanId {
    param([Parameter(Mandatory)]$Run)
    $v = Invoke-HzTool -Run $Run -Tool 'horizun_query_planimetry' -Label 'views-list' -Arguments @{
        mode = 'views'; max_rows = 500
    }
    if (-not $v.Ok) { return $null }
    $rows = @($v.Result.rows | Where-Object {
        ([string]$_.view_type -eq 'FloorPlan') -and (-not $_.is_template)
    })
    if ($rows.Count -eq 0) { return $null }
    [long]$rows[0].view_id
}

<#
  The first level in the document, by ELEVATION - never by name. "Level 1" is
  English, and a harness that matches on it works only on an English Revit.
#>
function Get-HzFirstLevel {
    param([Parameter(Mandatory)]$Run)
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_model' -Label 'levels' -Arguments @{
        categories = @('OST_Levels'); include_links = $false; include_bounding_box = $true; max_rows = 200
    }
    $levels = @($q.Result.rows | Where-Object { -not $_.is_element_type })
    if ($levels.Count -eq 0) { throw 'HARNESS: the fixture document has no level to build on' }
    # Lowest by the bounding box Revit reported, so the choice is geometric.
    $sorted = @($levels | Sort-Object { try { [double]$_.bounding_box.min[2] } catch { 0 } })
    $sorted[0]
}
