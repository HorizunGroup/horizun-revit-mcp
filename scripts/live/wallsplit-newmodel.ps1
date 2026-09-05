#Requires -Version 5.1
<#
  Create the ONE disposable model this campaign runs in, and open it.

  WHY NOT REUSE HZ_WRITE. The write-tier fixture is a stripped copy: the survey
  found two wall types, no door or window symbols, no reinforcement bar types and
  no wall instances at all. A campaign built in it would report most of its matrix
  as fixture_missing for a reason that has nothing to do with the capability.

  A fresh project from Revit's own metric template arrives with wall types, loaded
  door and window families and a clean level structure, and it is disposable by
  construction: it is created here, it is never anybody's project, and it lives
  under C:\hz-live beside the other disposable fixtures.
#>
[CmdletBinding()]
param(
    [string]$Path = 'C:\hz-live\HZ_WALLSPLIT.rvt',
    [string]$Template
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hz-wallsplit.lib.ps1')

if (-not $Template) {
    $candidates = @(
        'C:\ProgramData\Autodesk\RVT 2026\Templates\English\Structural Analysis-DefaultMetric.rte',
        'C:\ProgramData\Autodesk\RVT 2026\Templates\English\DefaultMetric.rte',
        'C:\ProgramData\Autodesk\RVT 2026\Templates\Default_M_ENU.rte',
        'C:\ProgramData\Autodesk\RVT 2026\Templates\Default_M_ENG.rte'
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { $Template = $c; break } }
}
if (-not $Template) { throw 'no metric template found' }
Write-Host ("template : " + $Template) -ForegroundColor DarkGray
Write-Host ("target   : " + $Path) -ForegroundColor DarkGray

$run = New-WsRun -Name 'wallsplit-newmodel' -Document 'HZ_WRITE'

# The model is built from the CURRENT session's Application, so it is a Revit 2026
# file by construction and no version guard can be surprised later.
$code = @"
import os
from Autodesk.Revit.DB import SaveAsOptions

app = __revit__.Application
target = r'$Path'
template = r'$Template'

folder = os.path.dirname(target)
if not os.path.isdir(folder):
    os.makedirs(folder)

existing = None
for d in app.Documents:
    try:
        if d.PathName and os.path.normcase(d.PathName) == os.path.normcase(target):
            existing = d
            break
    except Exception:
        pass

if existing is not None:
    __output__ = {'status': 'completed_unverified', 'summary': 'already open', 'path': target,
                  'created': False, 'title': existing.Title}
else:
    if os.path.isfile(target):
        os.remove(target)
    doc = app.NewProjectDocument(template)
    opts = SaveAsOptions()
    opts.OverwriteExistingFile = True
    doc.SaveAs(target, opts)
    title = doc.Title
    doc.Close(False)
    __output__ = {'status': 'completed_unverified',
                  'summary': 'created a disposable project from the template',
                  'path': target, 'created': True, 'title': title,
                  'exists_on_disk': os.path.isfile(target)}
"@

$made = Invoke-WsPython -Run $run -Label 'new-project' -Code $code
$o = Get-WsOutput $made
if (-not $o) {
    Write-Host 'the model could not be created:' -ForegroundColor Red
    Write-Host (Limit-WsText $made.Text 800) -ForegroundColor DarkYellow
    exit 1
}
Write-Host ("created  : " + $o.created + "   on disk: " + (Test-Path -LiteralPath $Path)) -ForegroundColor Green

# ---- open it, and make it the active document -------------------------------
$open = Invoke-Ws -Run $run -Tool 'horizun_open_document' -Label 'open-disposable' -Mutates -AllowError -Arguments @{
    path        = $Path
    make_active = $true
}
if ($open.Result) {
    Write-Host ("opened   : " + $open.Result.opened + "   active: " + $open.Result.confirmed_active +
                "   title: " + $open.Result.active_document) -ForegroundColor Green
} else {
    Write-Host 'open did not parse:' -ForegroundColor Yellow
    Write-Host (Limit-WsText $open.Text 600) -ForegroundColor DarkYellow
}

# ---- what did we get? --------------------------------------------------------
$title = if ($open.Result) { [string]$open.Result.active_document } else { 'HZ_WALLSPLIT' }
$run2 = New-WsRun -Name 'wallsplit-newmodel-survey' -Document $title -ArtifactDir $run.ArtifactDir
$survey = Invoke-WsPython -Run $run2 -Label 'survey' -Code @'
from Autodesk.Revit.DB import (FilteredElementCollector, WallType, WallKind, Level,
                               FamilySymbol, BuiltInCategory, Wall)
from Autodesk.Revit.DB.Structure import RebarBarType

d = __revit__.ActiveUIDocument.Document

multi = []
for wt in FilteredElementCollector(d).OfClass(WallType):
    try:
        if wt.Kind != WallKind.Basic:
            continue
        cs = wt.GetCompoundStructure()
        if cs is None:
            continue
        layers = cs.GetLayers()
        if len(layers) > 1:
            multi.append({"id": wt.Id.Value, "name": wt.Name, "layers": len(layers),
                          "core_first": cs.GetFirstCoreLayerIndex(),
                          "core_last": cs.GetLastCoreLayerIndex()})
    except Exception:
        pass

levels = sorted([{"id": l.Id.Value, "name": l.Name, "elev": l.Elevation}
                 for l in FilteredElementCollector(d).OfClass(Level)], key=lambda x: x["elev"])

def symbols(cat):
    return [{"id": s.Id.Value, "family": s.Family.Name, "name": s.Name}
            for s in FilteredElementCollector(d).OfCategory(cat).OfClass(FamilySymbol)]

__output__ = {
    "status": "completed_unverified",
    "summary": "what the disposable model offers",
    "title": d.Title,
    "walls_present": len(list(FilteredElementCollector(d).OfClass(Wall))),
    "multilayer_types": sorted(multi, key=lambda x: -x["layers"])[:8],
    "multilayer_count": len(multi),
    "levels": levels[:5],
    "doors": symbols(BuiltInCategory.OST_Doors)[:4],
    "windows": symbols(BuiltInCategory.OST_Windows)[:4],
    "rebar_bar_types": len(list(FilteredElementCollector(d).OfClass(RebarBarType))),
}
'@
$s = Get-WsOutput $survey
if ($s) {
    Write-Host ""
    Write-Host ("document : " + $s.title) -ForegroundColor Cyan
    Write-Host ("  walls already present : " + $s.walls_present)
    Write-Host ("  multilayer wall types : " + $s.multilayer_count)
    foreach ($m in $s.multilayer_types) {
        Write-Host ("      {0,-44} layers {1}  core [{2}..{3}]  id {4}" -f $m.name, $m.layers, $m.core_first, $m.core_last, $m.id)
    }
    Write-Host ("  levels : " + (($s.levels | ForEach-Object { "$($_.name)=$($_.id)" }) -join ', '))
    Write-Host ("  doors  : " + (($s.doors | ForEach-Object { "$($_.family)/$($_.name)=$($_.id)" }) -join ', '))
    Write-Host ("  windows: " + (($s.windows | ForEach-Object { "$($_.family)/$($_.name)=$($_.id)" }) -join ', '))
    Write-Host ("  rebar bar types : " + $s.rebar_bar_types)
    $s | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run.ArtifactDir 'model-survey.json') -Encoding UTF8
} else {
    Write-Host 'survey produced no output:' -ForegroundColor Yellow
    Write-Host (Limit-WsText $survey.Text 700) -ForegroundColor DarkYellow
}
Write-Host ("artifacts: " + $run.ArtifactDir) -ForegroundColor DarkGray
