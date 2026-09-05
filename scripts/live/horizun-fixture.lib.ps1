#Requires -Version 5.1
<#
  FIXTURES: drawings whose contents this repository AUTHORED, so a probe can
  compare what came back against what was drawn rather than against itself.

  Every fixture here is built by the typed commands, exported by Revit's own DWG
  exporter, and then DISCARDED FROM THE MODEL. That last step is not tidiness.
  The drawing is a picture of the walls the fixture just built; leave them in the
  document and the next run matches its own scaffolding, reports "3 matched
  before anything was built", and passes. That happened, and it was a true answer
  to a question nobody had asked.

  Depends on horizun-live.lib.ps1 being dot-sourced first.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The corner of the world these fixtures live in: 900 metres east of anything
# the base model contains, so a crop around them catches nothing else.
$script:HzFixtureOriginX = 900000.0
$script:HzFixtureDir = 'C:\hz-live\dwg'

<#
  Link a DWG into the active document.

  This was for a long time the ONE step in the whole chain that was not typed -
  no command linked a CAD file, so it went through execute_python and the
  artifact said so out loud. horizun_manage_cad_links closed that, and this
  function asks the SERVER whether this build has it rather than assuming: a
  harness run against an older binary still works, falls back, and records which
  route it took. cad_link_route in every artifact is that record.
#>
function Add-HzCadLink {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$DwgPath,
        [string]$Label = 'link',
        # A SECOND placement of a file already linked. The typed command refuses a
        # duplicate by default - two ImportInstances of one drawing is what the
        # placement-identity work exists to tell apart - so the harness that wants
        # exactly that says so, the way a caller would.
        [switch]$AllowDuplicate
    )
    if (-not (Test-Path -LiteralPath $DwgPath)) {
        throw "HARNESS: the fixture DWG is missing: $(Protect-HzText $DwgPath)"
    }

    # Typed first.
    $typed = Get-HzTypedCadLinkTool $Run
    if ($typed) {
        $view = Get-HzFirstFloorPlanId $Run
        if ($null -eq $view) { throw 'HARNESS: no floor plan to link into; a CAD link needs a view.' }
        $w = Invoke-HzWrite -Run $Run -Tool $typed.Tool -Label $Label -Arguments (Copy-HzArgs $typed.Arguments @{
            target_document = $Run.Document
            file_path = $DwgPath
            view_id = $view
            # LET REVIT READ THE HEADER. Measured 2026-08-27: forcing
            # units='millimeter' on a drawing Revit itself exported in INCHES put
            # the geometry at 35433 mm where 900000 was drawn - exactly divided by
            # 25.4 - and nothing downstream could tell, because the LINK then
            # declares millimetre and the requirement set agrees with it. The unit
            # gate compares the link's DECLARED unit against the set's; neither of
            # them knows what the file is really in. 'default' is the only value
            # that asks the drawing.
            units = 'default'
            current_view_only = $true
            allow_duplicate = [bool]$AllowDuplicate
        })
        $id = Get-HzProp $w.Apply.Result 'element_id'
        if ($null -eq $id) { throw ('HARNESS: {0} linked and returned no element id' -f $typed.Tool) }
        # THE COMMAND'S OWN POST-COMMIT CHECK, not this harness's opinion of it.
        if ((Get-HzProp $w.Apply.Result 'host_verified') -ne $true) {
            throw ('HARNESS: {0} did not report host_verified for the link' -f $typed.Tool)
        }
        Add-HzNote $Run ("linked TYPED via {0}: instance {1}, host_verified" -f $typed.Tool, $id)
        $Run.Fixture['cad_link_route'] = 'typed:' + $typed.Tool
        return [long]$id
    }

    Add-HzNote $Run 'NO TYPED CAD-LINK COMMAND IN THIS BUILD - falling back to execute_python'
    $Run.Fixture['cad_link_route'] = 'execute_python (no typed CAD-link command in this build)'
    $py = Join-Path $Run.WorkDir ("link-$Label.py")
@"
from Autodesk.Revit.DB import (Transaction, DWGImportOptions, ImportUnit,
                               FilteredElementCollector, ViewPlan, ViewType)
view = None
for v in FilteredElementCollector(doc).OfClass(ViewPlan):
    if not v.IsTemplate and v.ViewType == ViewType.FloorPlan:
        view = v
        break
opts = DWGImportOptions()
opts.Unit = ImportUnit.Default
opts.ThisViewOnly = True
t = Transaction(doc, 'Horizun live fixture: stage a CAD link'); t.Start()
ok, eid = doc.Link(r'$DwgPath', opts, view)
t.Commit()
# ElementId.Value is a 2024+ API. Revit 2023 has IntegerValue only, and reading
# the wrong one throws where the harness would blame the model.
def _eid(x):
    if x is None:
        return None
    return x.IntegerValue if hasattr(x, 'IntegerValue') else x.Value
__output__ = {'status': 'self_reported_verified', 'linked': bool(ok),
              'element_id': _eid(eid),
              'host_view': view.Name if view else None}
"@ | Set-Content -LiteralPath $py -Encoding utf8
    $r = Invoke-HzToolStrict -Run $Run -Tool 'horizun_execute_python' -Label $Label -Arguments @{
        code_path = $py; target_document = $Run.Document; idempotency_key = (New-HzKey $Run $Label)
    }
    $id = [long]$r.Result.output.element_id
    Add-HzNote $Run ("linked via execute_python: instance {0} (self-reported, not host-verified)" -f $id)
    $id
}

<#
  Is there a typed way to link a CAD file in THIS build? Asked of the server, so
  a harness written before the command existed starts using it the moment it
  does, and one running against an older build falls back and says so.
#>
function Get-HzTypedCadLinkTool {
    param([Parameter(Mandatory)]$Run)
    if ($Run.PSObject.Properties.Name -contains 'TypedCadLink') { return $Run.TypedCadLink }

    # ASK THE SERVER'S OWN SCHEMA, not an error message.
    #
    # Whether an operation exists is a fact about the contract the server
    # published. Guessing it from the wording of a refusal made this probe answer
    # "supported" for a command that had never heard of the operation, and the
    # run then failed a long way from the cause.
    $found = $null
    $ops = Get-HzToolEnum -Run $Run -Tool 'horizun_manage_cad_links' -Property 'operation'
    if ($ops -contains 'add') {
        $found = @{ Tool = 'horizun_manage_cad_links'; Arguments = @{ operation = 'add' } }
    }
    Add-Member -InputObject $Run -NotePropertyName 'TypedCadLink' -NotePropertyValue $found -Force
    $found
}

<#
  A floor plan to host a view-specific CAD link. horizun_manage_views CREATES
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
  Build walls, crop a plan view to them, export that view as a DWG, then throw
  the walls away. Returns everything a probe needs to check the drawing against
  what was drawn.
#>
function New-HzWallFixture {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][array]$Walls,      # @{ name; x1; y1; x2; y2 } in mm
        [Parameter(Mandatory)][string]$Tag,
        [string]$OutDir = $script:HzFixtureDir,
        [double]$Height = 3000.0,
        [switch]$KeepInModel                       # only for building a SECOND revision from the same walls
    )
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $level = Get-HzFirstLevel $Run
    $elements = @()
    foreach ($w in $Walls) {
        $elements += @{
            kind = 'wall'
            start = @([double]$w.x1, [double]$w.y1, 0.0)
            end = @([double]$w.x2, [double]$w.y2, 0.0)
            height = $Height
            level_id = [long]$level.element_id
        }
    }
    $made = Invoke-HzWrite -Run $Run -Tool 'horizun_create_elements' -Label "fx-$Tag-walls" -Arguments @{
        target_document = $Run.Document; units = 'mm'; elements = $elements
    }
    $verified = [int]$made.Apply.Result.created_verified
    if ($verified -ne $Walls.Count) {
        throw ("HARNESS: fixture {0} wanted {1} walls and Revit verified {2}" -f $Tag, $Walls.Count, $verified)
    }

    $viewName = "HZ_FX_${Tag}_$($Run.RunId)"
    $view = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-view" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id; name = $viewName })
    }
    $rows = @($view.Apply.Result.rows)
    if ($rows.Count -eq 0) { throw "HARNESS: fixture $Tag got no view back" }
    $viewId = [long]$rows[0].element_id

    $xs = @(); $ys = @()
    foreach ($w in $Walls) { $xs += [double]$w.x1; $xs += [double]$w.x2; $ys += [double]$w.y1; $ys += [double]$w.y2 }
    $pad = 2000.0
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-crop" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'set_crop'; view_id = $viewId
                       box = @((($xs | Measure-Object -Minimum).Minimum - $pad),
                               (($ys | Measure-Object -Minimum).Minimum - $pad),
                               (($xs | Measure-Object -Maximum).Maximum + $pad),
                               (($ys | Measure-Object -Maximum).Maximum + $pad)) })
    }

    $dwg = Join-Path $OutDir ("HZ_FX_${Tag}_$($Run.RunId).dwg")
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_export' -Label "fx-$Tag-export" -Arguments @{
        target_document = $Run.Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwg
    }
    $produced = @(Get-ChildItem -LiteralPath $OutDir -Filter ("HZ_FX_${Tag}_$($Run.RunId)*.dwg"))
    if ($produced.Count -eq 0) { throw "HARNESS: fixture $Tag exported no DWG" }
    $file = $produced[0]

    [ordered]@{
        fixture_id = "HZ_FX_${Tag}_$($Run.RunId)"
        tag = $Tag
        dwg_path = $file.FullName
        dwg_name = $file.Name
        dwg_sha256 = (Get-HzSha256 $file.FullName)
        dwg_bytes = $file.Length
        wall_count = $Walls.Count
        walls = $Walls
        height_mm = $Height
        exported_from_view = $viewName
        built_by = @('horizun_create_elements', 'horizun_manage_views(create_floor_plan)',
                     'horizun_manage_views(set_crop)', 'horizun_export(dwg)')
    }
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

<#
  A requirement set aimed at ONE layer of a fixture drawing. The layer name is
  read back from the drawing rather than assumed: Revit names an exported layer
  after the category it came from, and that name is not this repository's to
  predict.
#>
function New-HzWallRequirementSet {
    param(
        [Parameter(Mandatory)][string]$Layer,
        [Parameter(Mandatory)][string]$Units,
        [string]$Id = 'hz-live-walls',
        [string]$Version = '1.0.0',
        [double]$MinThickness = 100.0,
        [double]$MaxThickness = 400.0,
        [double]$Height = 3000.0,
        # How wide a break in a run of wall may be read as an opening. Omitted
        # by default: a plain fixture has no openings, and joining is a
        # judgement a set has to make out loud.
        [double]$BridgeOpeningsMm = 0.0
    )
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = $Id; version = $Version; title = 'Live fixture: walls from double lines' }
        source = @{ units = $Units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @(@{
            id = 'walls'; precedence = 10; discipline = 'architecture'
            layers = @($Layer); produces = 'wall'; category = 'OST_Walls'; height_mm = $Height
            geometry = $(
                $g = @{ from = 'double_lines'; min_thickness_mm = $MinThickness; max_thickness_mm = $MaxThickness
                        min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 }
                if ($BridgeOpeningsMm -gt 0) { $g['bridge_openings_mm'] = $BridgeOpeningsMm }
                $g)
        })
    }
}

<#
  Which layer in this drawing carries walls. Asked of the drawing, never assumed.
#>
function Get-HzWallLayer {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long]$InstanceId)
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_cad' -Label 'cad-layers' -Arguments @{
        mode = 'layers'; instance_id = $InstanceId
    }
    $wallish = @($q.Result.layers | Where-Object { $_.layer -match '(?i)WALL' })
    if ($wallish.Count -eq 0) {
        throw ("HARNESS: no layer in this drawing looks like walls; layers are: {0}" -f
               (@($q.Result.layers | ForEach-Object { $_.layer }) -join ', '))
    }
    [string]$wallish[0].layer
}

function Get-HzCadInstanceFacts {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][long]$InstanceId)
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_cad' -Label 'cad-instances' -Arguments @{ mode = 'instances' }
    $mine = @($q.Result.instances | Where-Object { [long]$_.element_id -eq $InstanceId })
    if ($mine.Count -ne 1) { throw "HARNESS: query_cad does not see the instance this run linked ($InstanceId)" }
    $mine[0]
}

<#
  A LOADED FAMILY SYMBOL OF A GIVEN CATEGORY, PROVISIONED IF NEED BE.

  Doors, windows and columns are loadable families: Revit will not place one
  unless the family is in the document. A stock installation may carry NO door
  family at all - this machine's does not; only Structural Precast content is
  installed - so a harness that assumed one would report a product failure for
  a missing library.

  So the symbol is provisioned from the family TEMPLATE Revit ships with, which
  is a different thing and always present with the product: an empty door family
  from Metric Door.rft is still a wall-hosted family of category Doors, with the
  opening cut the template carries. It has no panel and no handle, and it does
  not need them - what is being measured is that the bridge reads a symbol from
  a drawing, resolves the wall it belongs to, and places it hosted.

  Returns $null when the machine has no such template, and the caller records
  fixture_missing. It never returns a symbol of the wrong category.
#>
function Get-HzHostedSymbol {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][ValidateSet('Door', 'Window', 'Column', 'Structural Column',
                                          'Structural Framing - Beams and Braces')][string]$Kind,
        [string]$Category
    )
    # THE CATEGORY BELONGS TO THE TEMPLATE, NOT TO WHOEVER ASKS. A family made
    # from Metric Column.rft is an ARCHITECTURAL column whatever it is asked to
    # be, so looking for it in OST_StructuralColumns finds nothing - measured,
    # and it left the structural column probes with no symbol at all.
    if (-not $Category) {
        switch -Regex ($Kind) {
            '^Structural Column$'  { $Category = 'OST_StructuralColumns' }
            '^Structural Framing'  { $Category = 'OST_StructuralFraming' }
            default                { $Category = 'OST_' + $Kind + 's' }
        }
    }

    # Already loaded? Then use it, and do not spend a minute making another.
    $existing = @((Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_model' -Label "sym-$Kind" -Arguments @{
        categories = @($Category); include_types = $true; include_links = $false; max_rows = 50
    }).Result.rows | Where-Object { $_.is_element_type -eq $true })
    if ($existing.Count -gt 0) {
        return [ordered]@{ type_name = [string]$existing[0].name
                           type_id = [long]$existing[0].element_id
                           provisioned = $false }
    }

    $year = [string](Get-HzProp (Get-HzHealth $Run) 'revit_version')
    if (-not $year) { $year = '2026' }
    $template = $null
    foreach ($root in @("C:\ProgramData\Autodesk\RVT $year\Family Templates\English",
                        "C:\ProgramData\Autodesk\RVT $year\Family Templates\English-Imperial")) {
        $candidate = Join-Path $root ("Metric $Kind.rft")
        if (Test-Path -LiteralPath $candidate) { $template = $candidate; break }
        $candidate = Join-Path $root ("$Kind.rft")
        if (Test-Path -LiteralPath $candidate) { $template = $candidate; break }
    }
    if (-not $template) { return $null }

    $outDir = 'C:\hz-live\fam'
    if (-not (Test-Path -LiteralPath $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
    # IT NEEDS GEOMETRY, or it draws nothing.
    #
    # An empty family from the template is a valid family of the right category
    # and it exports NOTHING to a plan view - measured: the door, window and
    # column layers all came back empty, and the reading then had no symbol to
    # find. A single extrusion crossing the view's cut plane is the least that
    # makes the thing visible in a drawing, which is the whole point of reading
    # one. Panels are deliberately plain: what is measured is that a symbol on a
    # layer becomes a hosted instance, not that the door looks like a door.
    $forms = @()
    switch -Regex ($Kind) {
        '^Door$' {
            $forms = @(@{ key = 'panel'; kind = 'extrusion'; plane = 'xz'; solid = $true; depth = 60.0
                          profile = @(, @(@(-450.0, 0.0, 0.0), @(450.0, 0.0, 0.0),
                                          @(450.0, 0.0, 2100.0), @(-450.0, 0.0, 2100.0))) })
        }
        '^Window$' {
            $forms = @(@{ key = 'sash'; kind = 'extrusion'; plane = 'xz'; solid = $true; depth = 60.0
                          profile = @(, @(@(-600.0, 0.0, 900.0), @(600.0, 0.0, 900.0),
                                          @(600.0, 0.0, 2100.0), @(-600.0, 0.0, 2100.0))) })
        }
        '^(Structural )?Column$' {
            $forms = @(@{ key = 'shaft'; kind = 'extrusion'; plane = 'xy'; solid = $true; depth = 3000.0
                          profile = @(, @(@(-150.0, -150.0, 0.0), @(150.0, -150.0, 0.0),
                                          @(150.0, 150.0, 0.0), @(-150.0, 150.0, 0.0))) })
        }
        '^Structural Framing' {
            # IT HAS TO CROSS THE CUT PLANE, or the drawing has nothing to read.
            # A beam-shaped solid hanging below the level is exactly what a real
            # beam looks like and exactly what a floor plan leaves out: the
            # primary view range starts at the level, so geometry underneath it
            # is simply not drawn. This one stands up through the cut instead.
            $forms = @(@{ key = 'web'; kind = 'extrusion'; plane = 'xy'; solid = $true; depth = 2000.0
                          profile = @(, @(@(-1500.0, -150.0, 0.0), @(1500.0, -150.0, 0.0),
                                          @(1500.0, 150.0, 0.0), @(-1500.0, 150.0, 0.0))) })
        }
    }

    $name = 'HZ_' + ($Kind -replace '[^A-Za-z]', '_').ToUpperInvariant()
    $made = Invoke-HzWrite -Run $Run -Tool 'horizun_create_family' -Label "fam-$Kind" -Arguments @{
        target_document = $Run.Document
        template_path = $template
        output_path = (Join-Path $outDir ($name + '.rfa'))
        units = 'mm'; load_into_project = $true; overwrite = $true
        forms = $forms }

    $loaded = Get-HzProp $made.Apply.Result 'loaded_family'
    if (-not $loaded) { return $null }
    $symbolIds = @(Get-HzProp $loaded 'symbol_ids')
    if ($symbolIds.Count -eq 0) { return $null }

    # The NAME, re-read from the document rather than assumed from the file
    # name: a requirement set names a family type, and a name this harness
    # invented would prove nothing about resolution.
    $rows = @((Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_model' -Label "sym-$Kind-after" -Arguments @{
        categories = @($Category); include_types = $true; include_links = $false; max_rows = 50
    }).Result.rows | Where-Object { $_.is_element_type -eq $true -and
                                    [long]$_.element_id -eq [long]$symbolIds[0] })
    if ($rows.Count -eq 0) { return $null }
    [ordered]@{ type_name = [string]$rows[0].name
                type_id = [long]$rows[0].element_id
                provisioned = $true
                template = (Split-Path $template -Leaf) }
}

<#
  WHICH LAYER THE DRAWING PUT A THING ON, measured rather than assumed.

  Revit names exported layers from its own configuration, and a harness that
  hardcoded "A-DOOR" would pass on this machine and fail on the next one for a
  reason that has nothing to do with the bridge. What IS known is where the
  element was placed, so the layer is read off the geometry nearest that point:
  the layer carrying the most segments within the radius, excluding any layer
  the caller has already accounted for.
#>
function Get-HzLayersNear {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][long]$InstanceId,
        [Parameter(Mandatory)][double[]]$Point,       # x,y in mm
        [double]$RadiusMm = 1500.0,
        [string]$Label = 'layers-near'
    )
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_cad' -Label $Label -Arguments @{
        mode = 'geometry'; instance_id = $InstanceId; max_rows = 5000 }
    $tally = @{}
    foreach ($seg in @($q.Result.segments)) {
        $layer = [string]$seg.layer
        $mx = ([double]$seg.start_mm[0] + [double]$seg.end_mm[0]) / 2.0
        $my = ([double]$seg.start_mm[1] + [double]$seg.end_mm[1]) / 2.0
        $d = [Math]::Sqrt((($mx - $Point[0]) * ($mx - $Point[0])) + (($my - $Point[1]) * ($my - $Point[1])))
        if ($d -gt $RadiusMm) { continue }
        if ($tally.ContainsKey($layer)) { $tally[$layer] = $tally[$layer] + 1 } else { $tally[$layer] = 1 }
    }
    $tally
}

<#
  THE LAYER THAT BELONGS TO THIS THING AND TO NOTHING ELSE.

  Reading "the commonest layer near the door" is not enough: a wall draws its
  OUTLINE at every jamb, so that layer is near the door and near the window and
  near nothing else in between - and on one run it outnumbered the glazing and
  the window rule claimed it. It happened to still produce one candidate in the
  right place, which is the worst kind of pass.

  What identifies a symbol's own layer is exclusivity. It is near THIS thing,
  and not near any of the others, and not on a plain stretch of the wall. Given
  where each thing was placed - which the fixture knows exactly - that is a
  measurement rather than a guess.
#>
function Get-HzExclusiveLayerNear {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][long]$InstanceId,
        [Parameter(Mandatory)][double[]]$Point,
        [Parameter(Mandatory)][array]$OtherPoints,   # x,y pairs: everything this is not
        [double]$RadiusMm = 900.0,
        [string]$Label = 'layer-exclusive'
    )
    # ONE PAIR IS NOT TWO NUMBERS. PowerShell flattens @(@(x, y)) to @(x, y), so
    # a caller passing a single other-point ends up passing two scalars, and the
    # loop below then indexes a double. Saying so beats "index outside the bounds
    # of the array" from three frames down.
    foreach ($other in $OtherPoints) {
        if ($null -eq $other -or -not ($other -is [array]) -or $other.Count -lt 2) {
            # The message names the fix literally, and must not INTERPOLATE it:
            # the very thing it is telling you to write is a variable reference,
            # and under StrictMode a message about an undefined variable is not
            # the error anybody needed.
            throw ('HARNESS: OtherPoints must be a list of x,y PAIRS. PowerShell flattens @(@(x, y)) to ' +
                   '@(x, y), so a caller passing exactly one pair passes two numbers. Write @(, $pair).')
        }
    }

    $mine = Get-HzLayersNear -Run $Run -InstanceId $InstanceId -Point $Point -RadiusMm $RadiusMm -Label $Label
    $elsewhere = @{}
    for ($i = 0; $i -lt $OtherPoints.Count; $i++) {
        foreach ($k in (Get-HzLayersNear -Run $Run -InstanceId $InstanceId -Point $OtherPoints[$i] `
                        -RadiusMm $RadiusMm -Label ("$Label-other-$i")).Keys) { $elsewhere[$k] = $true }
    }
    $ranked = @($mine.GetEnumerator() | Where-Object { -not $elsewhere.ContainsKey($_.Key) } |
                Sort-Object -Property @{ Expression = 'Value'; Descending = $true },
                                      @{ Expression = 'Key'; Descending = $false })
    if ($ranked.Count -eq 0) { return $null }
    [string]$ranked[0].Key
}

function Get-HzLayerNear {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][long]$InstanceId,
        [Parameter(Mandatory)][double[]]$Point,       # x,y in mm
        [double]$RadiusMm = 1500.0,
        [string[]]$Exclude = @(),
        [string]$Label = 'layer-near'
    )
    $tally = Get-HzLayersNear -Run $Run -InstanceId $InstanceId -Point $Point -RadiusMm $RadiusMm -Label $Label
    # Count first, then NAME, so a tie resolves the same way every run. A harness
    # that is only sometimes right is worse than one that is never right: it
    # passes until the day it matters.
    $ranked = @($tally.GetEnumerator() | Where-Object { $Exclude -notcontains $_.Key } |
                Sort-Object -Property @{ Expression = 'Value'; Descending = $true },
                                      @{ Expression = 'Key'; Descending = $false })
    if ($ranked.Count -eq 0) { return $null }
    [string]$ranked[0].Key
}
