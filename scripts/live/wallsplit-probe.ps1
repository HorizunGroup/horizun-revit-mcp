#Requires -Version 5.1
<#
  A first, small probe: is the fixture buildable at all?

  Before writing a 55-case harness it is worth finding out whether the three
  things every structural case depends on can be made in this document: python
  permission in force, a compound wall type, and a reinforcement bar type. A
  harness written against assumptions about those would fail 55 times for one
  reason.
#>
[CmdletBinding()]
param([string]$Document = 'HZ_WRITE')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hz-wallsplit.lib.ps1')

$run = New-WsRun -Name 'wallsplit-probe' -Document $Document
Write-Host ("artifact dir: " + $run.ArtifactDir) -ForegroundColor DarkGray

# ---- 1. is python in force? ------------------------------------------------
$py = Invoke-WsPython -Run $run -Label 'permission' -Code @'
__output__ = {"python": "permitted"}
'@
$pythonOk = -not $py.IsError
Write-Host ("python permitted : " + $pythonOk) -ForegroundColor $(if ($pythonOk) { 'Green' } else { 'Yellow' })
if (-not $pythonOk) { Write-Host ("  " + (Limit-WsText $py.Text 300)) -ForegroundColor DarkYellow }

# ---- 2. what does this document already have? ------------------------------
if ($pythonOk) {
    $survey = Invoke-WsPython -Run $run -Label 'survey' -Code @'
from Autodesk.Revit.DB import FilteredElementCollector, WallType, WallKind, Level, FamilySymbol, BuiltInCategory
from Autodesk.Revit.DB.Structure import RebarBarType, RebarHostData

d = __revit__.ActiveUIDocument.Document

basic = []
for wt in FilteredElementCollector(d).OfClass(WallType):
    try:
        if wt.Kind != WallKind.Basic:
            continue
        cs = wt.GetCompoundStructure()
        n = 0 if cs is None else len(cs.GetLayers())
        basic.append({"id": wt.Id.Value, "name": wt.Name, "layers": n})
    except Exception:
        pass

multi = [b for b in basic if b["layers"] > 1]
levels = [{"id": l.Id.Value, "name": l.Name, "elev": l.Elevation}
          for l in FilteredElementCollector(d).OfClass(Level)]
levels.sort(key=lambda x: x["elev"])

bar_types = [{"id": t.Id.Value, "name": t.Name}
             for t in FilteredElementCollector(d).OfClass(RebarBarType)]

doors = [s.Id.Value for s in FilteredElementCollector(d)
         .OfCategory(BuiltInCategory.OST_Doors).OfClass(FamilySymbol)]
windows = [s.Id.Value for s in FilteredElementCollector(d)
           .OfCategory(BuiltInCategory.OST_Windows).OfClass(FamilySymbol)]

__output__ = {
    "basic_wall_types": len(basic),
    "multilayer_wall_types": len(multi),
    "multilayer_examples": sorted(multi, key=lambda x: -x["layers"])[:6],
    "levels": levels[:4],
    "rebar_bar_types": len(bar_types),
    "rebar_bar_type_examples": bar_types[:4],
    "door_symbols": len(doors),
    "window_symbols": len(windows),
}
'@
    $o = Get-WsOutput $survey
    if ($o) {
        Write-Host ""
        Write-Host "document survey" -ForegroundColor Cyan
        Write-Host ("  basic wall types      : " + $o.basic_wall_types)
        Write-Host ("  multilayer wall types : " + $o.multilayer_wall_types)
        foreach ($m in $o.multilayer_examples) {
            Write-Host ("      {0,-46} layers {1}  id {2}" -f $m.name, $m.layers, $m.id)
        }
        Write-Host ("  levels                : " + (($o.levels | ForEach-Object { "$($_.name)=$($_.id)" }) -join ', '))
        Write-Host ("  rebar bar types       : " + $o.rebar_bar_types)
        foreach ($b in $o.rebar_bar_type_examples) { Write-Host ("      {0}  id {1}" -f $b.name, $b.id) }
        Write-Host ("  door symbols          : " + $o.door_symbols)
        Write-Host ("  window symbols        : " + $o.window_symbols)
    } else {
        Write-Host "survey produced no __output__" -ForegroundColor Yellow
        Write-Host (Limit-WsText $survey.Text 500) -ForegroundColor DarkYellow
    }
}

# ---- 3. what does the tool itself say about this document right now? --------
$dry = Invoke-Ws -Run $run -Tool 'horizun_split_multilayer_walls' -Label 'dry-whole-model' -AllowError -Arguments @{
    target_document = $Document
    dry_run         = $true
}
if ($dry.Result) {
    $r = $dry.Result
    $names = @($r.PSObject.Properties.Name)
    Write-Host ""
    Write-Host "tool dry run over the whole model" -ForegroundColor Cyan
    foreach ($k in 'schema_version', 'would_convert_walls', 'would_produce_walls', 'already_split_walls',
                   'partial_state_walls', 'reverse_census_ran', 'provenance_index_ran', 'tolerance_mm') {
        if ($names -contains $k) { Write-Host ("  {0,-22}: {1}" -f $k, $r.$k) }
    }
    if ($names -contains 'scope') { Write-Host ("  scope.resolved        : " + $r.scope.resolved) }
    if ($names -contains 'rejected') {
        $byCode = @{}
        foreach ($x in $r.rejected) {
            $c = [string]$x.reason_code
            if (-not $byCode.ContainsKey($c)) { $byCode[$c] = 0 }
            $byCode[$c]++
        }
        Write-Host "  rejections by code:"
        foreach ($k in ($byCode.Keys | Sort-Object)) { Write-Host ("      {0,-32} {1}" -f $k, $byCode[$k]) }
    }
} else {
    Write-Host ""
    Write-Host "dry run did not parse:" -ForegroundColor Yellow
    Write-Host (Limit-WsText $dry.Text 700) -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host ("artifacts in " + $run.ArtifactDir) -ForegroundColor DarkGray
