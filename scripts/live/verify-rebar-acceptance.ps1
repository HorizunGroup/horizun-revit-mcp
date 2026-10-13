#Requires -Version 5.1
<#
  ACERO 3.0, STAGE 1 ACCEPTANCE: a synthetic training building, built and reinforced with typed
  tools only, on a DISPOSABLE fixture, and judged against an INDEPENDENT calculation.

  The building (Autodesk's metric structural template families):
    - two storeys of 3000 mm, six 450 x 600 columns a storey on a 5000 x 4500 grid (12 columns);
    - 300 x 600 beams at both floor levels, along X and along Y (14 beams);
    - six 1800 x 1200 x 450 footings under the ground-floor columns, with four dowels each
      declared INTO their column (L1: containment against the union, anchorage measured);
    - a 6000 x 4000 slab panel (its own host, top and bottom mats both ways);
    - a 300 mm concrete wall, 3000 x 3000, a two-way mat on each face.

    A1  the fixture is what the script asked for (counts of every element)
    A2  the plan refuses nothing
    A3  the apply verifies every set
    A4  the audit agrees: no finding but the accepted notes (hook_corner_opening, hook_end_extension,
        hook_corner_seat, and coupled_end_seated on a coupled end)
    A5  the bars the model carries, by category, against an independent count (the area and path bars of
        B5's slab included: mode=quantities reads RebarInSystem beside rebar sets)
    A6  the kilograms the model carries (horizun_query_structure mode=quantities, 7850 kg/m3) against an
        independent calculation from nominal masses, within 2 % in total and 3 % per category

  STAGE 1b, in the same building and the same requirement set (planned, applied, re-read and audited with it):
  an annex frame (four columns, beams H and C), a roof of two floors (a flat strip and a 6 % slope shaped
  with horizun_slab_shape) and a second slab.
    B1  hold_ends: U-bars in beam H anchored into both columns, held; every checks.hold_ends verified within
        1 mm, and the six bars re-read from the model within 1 mm of the declared polyline at every vertex
    B2  two couplers in beam C's top layer: three pieces, the middle one coupled at both ends, the two
        couplers shared and on their stations, each piece's length from the member, its covers and stations
    B3  the set declares `clearances` (25 mm between rules): the plan checks declared_clearance and refuses
        nothing; the audit compares every set, reads them all, and finds no overlapping_bar
    B4  follow_face: top and bottom mats of the roof; every across bar re-read at the height of the face
        less its offset at its own station (the inward offset of the two facets, computed here), counts by hand
    B5  area and path reinforcement on the second slab: both systems verified, their bars counted by hand

  STAGE 1c, crossing_order, in the same set: a beam whose top and bottom U bars put their legs in one plane,
  and a slab whose two-way bottom mat lays both directions at one depth. Each crossing is declared.
    C1  the bottom U's legs nest one diameter inside the top U's (legs, the pair declared in contact) and the
        mat's Y layer passes over its X layer by contact (toward the slab's core): the plan lists exactly those
        two displacements, the audit measures both, and the bars re-read from the model stand where THIS
        harness puts them by hand - the displaced ones within 1 mm of the displacement and, listed apart,
        within one diameter + 5 mm of the declared position; the others within 1 mm of the declared one

  THE INDEPENDENT CALCULATION is written here as a bending schedule is written by hand: member lengths
  read off the model's boxes, the plan's covers, spacings and zones, and the nominal mass per metre of
  CSA G30.18 bars (10M 0.785, 13M 0.994, 16M 1.570 kg/m - the published table, used by this harness only;
  the bridge compiles no bar table in). It does not read the plan's rows or the apply's.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-acceptance' -Document $Document
function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 500 }).Result.rows | Where-Object { $_.is_element_type })
}
function New-Hz($Label, $Elements) {
    @(@((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label $Label -Arguments @{ target_document = $Document; units = 'mm'; elements = $Elements }).Apply.Result.rows) |
        ForEach-Object { [long](Get-HzProp $_ 'element_id') })
}
function Get-HzBoxes([long[]]$Ids) {
    $m = @{}
    foreach ($r in @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'boxes' -Arguments @{ element_ids = $Ids; include_links = $false; include_bounding_box = $true; max_rows = 500 }).Result.rows)) {
        $m[[long]$r.element_id] = $r.bounding_box
    }
    $m
}
function Get-HzCover([long]$Id) {
    $row = @(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }).Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -eq $c) { throw "HARNESS: host $Id has no common cover; this acceptance declares covers from the host" }
    [double]$c
}

# =====================================================================  FIXTURE
Write-Host "`n== fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$z0 = 0.0; try { $z0 = [double]$level.bounding_box.min[2] } catch { }
$lv2 = (New-Hz 'level-2' @(, @{ kind = 'level'; elevation = ($z0 + 3000.0); name = 'HZ ACC N2' }))[0]
$lv3 = (New-Hz 'level-3' @(, @{ kind = 'level'; elevation = ($z0 + 6000.0); name = 'HZ ACC N3' }))[0]
$colT = @(Get-HzTypes 'OST_StructuralColumns' | Where-Object { [string]$_.family -eq 'M_Concrete-Rectangular-Column' -and [string]$_.name -eq '450 x 600mm' })[0]
$beamT = @(Get-HzTypes 'OST_StructuralFraming' | Where-Object { [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })[0]
$footT = @(Get-HzTypes 'OST_StructuralFoundation' | Where-Object { [string]$_.family -eq 'M_Footing-Rectangular' -and [string]$_.name -eq '1800 x 1200 x 450mm' })[0]
$wallT = @(Get-HzTypes 'OST_Walls' | Where-Object { [string]$_.family -eq 'Basic Wall' -and [string]$_.name -match 'Concrete' -and [string]$_.name -match '300\s*mm' })[0]
$floorT = @(Get-HzTypes 'OST_Floors' | Where-Object { [string]$_.name -match '(\d+)\s*mm' -and [string]$_.name -notmatch 'Deck|Metal|Steel|Wood|Timber' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 150 } | Select-Object -First 1)[0]
foreach ($t in @(@{ n = 'column'; v = $colT }, @{ n = 'beam'; v = $beamT }, @{ n = 'footing'; v = $footT }, @{ n = 'wall'; v = $wallT }, @{ n = 'floor'; v = $floorT })) {
    if (-not $t.v) { throw "HARNESS: the template has no $($t.n) type this acceptance needs" }
}
$X0 = 260000.0
$nodes = @(@(0, 0), @(5000, 0), @(10000, 0), @(0, 4500), @(5000, 4500), @(10000, 4500))
$cols1 = New-Hz 'columns-1' @($nodes | ForEach-Object { @{ kind = 'structural_column'; type_id = [long]$colT.element_id; level_id = $lv; point = @(($X0 + $_[0]), [double]$_[1], 0.0); coordinate_mode = 'level_offset'; height = 3000.0 } })
$cols2 = New-Hz 'columns-2' @($nodes | ForEach-Object { @{ kind = 'structural_column'; type_id = [long]$colT.element_id; level_id = $lv2; point = @(($X0 + $_[0]), [double]$_[1], 0.0); coordinate_mode = 'level_offset'; height = 3000.0 } })
$beamDefs = @()
foreach ($lvl in @(@{ id = $lv2; z = 3000.0 }, @{ id = $lv3; z = 6000.0 })) {
    foreach ($y in 0, 4500) { foreach ($x in 0, 5000) { $beamDefs += @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lvl.id; start = @(($X0 + $x), [double]$y, ($z0 + $lvl.z)); end = @(($X0 + $x + 5000), [double]$y, ($z0 + $lvl.z)) } } }
    foreach ($x in 0, 5000, 10000) { $beamDefs += @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lvl.id; start = @(($X0 + $x), 0.0, ($z0 + $lvl.z)); end = @(($X0 + $x), 4500.0, ($z0 + $lvl.z)) } }
}
$beams = New-Hz 'beams' $beamDefs
$foots = New-Hz 'footings' @($nodes | ForEach-Object { @{ kind = 'family_instance'; type_id = [long]$footT.element_id; level_id = $lv; point = @(($X0 + $_[0]), [double]$_[1], 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' } })
$XS = $X0 + 20000.0
$slab = (New-Hz 'slab' @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
    profile = @(, @(@($XS, 0.0, 0.0), @(($XS + 6000), 0.0, 0.0), @(($XS + 6000), 4000.0, 0.0), @($XS, 4000.0, 0.0))) }))[0]
$XW = $X0 + 30000.0
$wall = (New-Hz 'wall' @(, @{ kind = 'wall'; type_id = [long]$wallT.element_id; level_id = $lv; start = @($XW, 0.0, 0.0); end = @(($XW + 3000.0), 0.0, 0.0); height = 3000.0; structural = $true; join_rule = 'disallow' }))[0]
# STAGE 1b, THE ANNEX: a frame of four 450 x 600 columns (3600 high) carrying two 300 x 600 beams at 3000 -
# H (y = 0) for the held U-bars, C (y = 4500) for the layer with two couplers -, a roof of two floors (a flat
# strip and a 6 % slope shaped with horizun_slab_shape), and a second slab for area and path reinforcement.
$XA = $X0 + 40000.0
$colsA = New-Hz 'columns-annex' @(@(0, 0), @(6000, 0), @(0, 4500), @(6000, 4500) | ForEach-Object {
    @{ kind = 'structural_column'; type_id = [long]$colT.element_id; level_id = $lv; point = @(($XA + $_[0]), [double]$_[1], 0.0); coordinate_mode = 'level_offset'; height = 3600.0 } })
$beamsA = New-Hz 'beams-annex' @(0, 4500 | ForEach-Object {
    @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @($XA, [double]$_, ($z0 + 3000.0)); end = @(($XA + 6000), [double]$_, ($z0 + 3000.0)) } })
$beamH = $beamsA[0]; $beamC = $beamsA[1]
$XR = $X0 + 50000.0
$RS = 240.0 / 4000.0   # THE ROOF'S DECLARED SLOPE: its far edge raised 240 mm over the 4000 mm slope
$roofStrip = (New-Hz 'roof-strip' @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
    profile = @(, @(@($XR, 0.0, 0.0), @(($XR + 1500), 0.0, 0.0), @(($XR + 1500), 3000.0, 0.0), @($XR, 3000.0, 0.0))) }))[0]
$roofSlope = (New-Hz 'roof-slope' @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
    profile = @(, @(@(($XR + 1500), 0.0, 0.0), @(($XR + 5500), 0.0, 0.0), @(($XR + 5500), 3000.0, 0.0), @(($XR + 1500), 3000.0, 0.0))) }))[0]
$null = Invoke-HzWrite -Run $run -Tool 'horizun_slab_shape' -Label 'roof-raise' -Arguments @{ target_document = $Document; units = 'mm'
    operation = 'modify_subelement'; element_id = $roofSlope; start = @(($XR + 5500), 0.0); end = @(($XR + 5500), 3000.0); offset = 240.0 }
$XS2 = $X0 + 60000.0
$slab2 = (New-Hz 'slab-systems' @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
    profile = @(, @(@($XS2, 0.0, 0.0), @(($XS2 + 6000), 0.0, 0.0), @(($XS2 + 6000), 4000.0, 0.0), @($XS2, 4000.0, 0.0))) }))[0]
# STAGE 1c: a beam on its own for the U bars, and a slab for the mat laid at one depth.
$XC = $X0 + 70000.0
$beamUC = (New-Hz 'beam-u' @(, @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv2; start = @($XC, 0.0, ($z0 + 3000.0)); end = @(($XC + 5000), 0.0, ($z0 + 3000.0)) }))[0]
$slabC = (New-Hz 'slab-crossing' @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
    profile = @(, @(@(($XC + 10000), 0.0, 0.0), @(($XC + 14000), 0.0, 0.0), @(($XC + 14000), 3000.0, 0.0), @(($XC + 10000), 3000.0, 0.0))) }))[0]
$annex = @($colsA) + @($beamsA) + @($roofStrip, $roofSlope, $slab2, $beamUC, $slabC)

$all = @($cols1) + @($cols2) + @($beams) + @($foots) + @($slab, $wall) + $annex
$box = Get-HzBoxes $all
$run.Fixture['elements'] = @{ columns = (@($cols1) + @($cols2)).Count; beams = $beams.Count; footings = $foots.Count; slab = $slab; wall = $wall
    annex = @{ columns = $colsA; beam_hold = $beamH; beam_couplers = $beamC; roof_strip = $roofStrip; roof_slope = $roofSlope; slab_systems = $slab2 } }
# THE ROOF'S RAISED EDGE, measured: the slope's box rises 240 mm above the strip's top.
$roofRise = [double]$box[$roofSlope].max[2] - [double]$box[$roofStrip].max[2]
Add-HzProbe -Run $run -Id 'A1' -Name 'acceptance: the fixture is what was asked for' `
    -Expected '12 columns, 14 beams, 6 footings, 1 slab, 1 wall; annex: 4 columns, 2 beams, a roof whose slope rises 240 mm, 1 slab; every box readable' `
    -Observed ("columns={0} beams={1} footings={2} annex_columns={3} annex_beams={4} roof_rise_mm={5:N1} boxes={6}/{7}" -f (@($cols1) + @($cols2)).Count, $beams.Count, $foots.Count,
        $colsA.Count, $beamsA.Count, $roofRise, $box.Count, $all.Count) `
    -Ok ((@($cols1) + @($cols2)).Count -eq 12 -and $beams.Count -eq 14 -and $foots.Count -eq 6 -and $colsA.Count -eq 4 -and $beamsA.Count -eq 2 -and
         [Math]::Abs($roofRise - 240.0) -le 1.0 -and $box.Count -eq $all.Count)

$cvCol = Get-HzCover $cols1[0]; $cvBeam = Get-HzCover $beams[0]; $cvFoot = Get-HzCover $foots[0]; $cvSlab = Get-HzCover $slab; $cvWall = Get-HzCover $wall
$cvColA = Get-HzCover $colsA[0]; $cvBeamA = Get-HzCover $beamH; $cvRoof = [Math]::Max((Get-HzCover $roofStrip), (Get-HzCover $roofSlope))
$cvBeamUC = Get-HzCover $beamUC; $cvSlabC = Get-HzCover $slabC
$run.Fixture['covers'] = @{ column = $cvCol; beam = $cvBeam; footing = $cvFoot; slab = $cvSlab; wall = $cvWall; annex_column = $cvColA; annex_beam = $cvBeamA; roof = $cvRoof }

# =====================================================================  REQUIREMENT SET
function Stirrups($ConfMm) {
    [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }; start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
        start_offset_mm = 50; end_offset_mm = 50; symmetric = $true
        zones = @(
            @{ name = 'conf'; length_mm = $ConfMm; layout = @{ rule = 'maximum_spacing'; spacing_mm = 100; include_last_bar = $false } },
            @{ name = 'mid'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200; include_last_bar = $false } }) }
}
$rr = @()
foreach ($i in 0..5) {
    # FOUR DOWELS, two sets of two, from the footing's cover + 60 (clear of its mats) to 600 into its column, 50 inside the
    # column's corner bars - declared INTO the column, with 500 mm of anchorage measured there.
    $f = $box[$foots[$i]]; $c = $box[$cols1[$i]]
    $cx = ([double]$c.min[0] + [double]$c.max[0]) / 2; $cy = ([double]$c.min[1] + [double]$c.max[1]) / 2
    $hx = ([double]$c.max[0] - [double]$c.min[0]) / 2 - 120; $hy = ([double]$c.max[1] - [double]$c.min[1]) / 2 - 120
    $zb = [double]$f.min[2] + $cvFoot + 60; $zt = [double]$c.min[2] + 600
    foreach ($sy in -1, 1) {
        $rr += [ordered]@{ id = "D$i$sy"; host = @{ element_ids = @(, $foots[$i]) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
            curve_mm = @(@(($cx - $hx), ($cy + $sy * $hy), $zb), @(($cx - $hx), ($cy + $sy * $hy), $zt)); normal = @(1, 0, 0)
            layout = @{ rule = 'fixed_number'; number = 2; array_length_mm = (2 * $hx) }
            into = @(, $cols1[$i]); anchorage = @(, @{ element_id = $cols1[$i]; min_length_mm = 500 }) }
    }
}
function Mat($Id, $HostId, $Normal, $Dirs, $Cover, $Bar, $Spacing, $Offset0) {
    $comps = @(); $k = 0
    foreach ($d in $Dirs) {
        $comps += [ordered]@{ name = $d.n; direction = $d.v; bar_type = $Bar; offset_from_face_mm = ($Offset0 + $k * 16); end_cover_mm = $Cover; side_cover_mm = ($Cover + 10)
            allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = $Spacing } }
        $k++
    }
    [ordered]@{ id = $Id; host = @{ element_ids = @(, $HostId) }; face_normal = $Normal; components = $comps }
}
$XY = @(@{ n = 'x'; v = @(1, 0, 0) }, @{ n = 'y'; v = @(0, 1, 0) })
$mats = @()
foreach ($i in 0..5) { $mats += (Mat "F$i" $foots[$i] @(0, 0, -1) $XY $cvFoot 'N16' 200 ($cvFoot + 10)) }
$mats += (Mat 'S-top' $slab @(0, 0, 1) $XY $cvSlab 'N10' 200 ($cvSlab + 6))
$mats += (Mat 'S-bot' $slab @(0, 0, -1) $XY $cvSlab 'N10' 200 ($cvSlab + 6))
$WV = @(@{ n = 'h'; v = @(1, 0, 0) }, @{ n = 'v'; v = @(0, 0, 1) })
$mats += (Mat 'W-pos' $wall @(0, 1, 0) $WV $cvWall 'N13' 250 ($cvWall + 8))
$mats += (Mat 'W-neg' $wall @(0, -1, 0) $WV $cvWall 'N13' 250 ($cvWall + 8))
# ---- STAGE 1b
# B1 HELD U-BARS in beam H, anchored into both of its columns: the run 6 mm from the far columns' cover (where
# Revit can snap an end to a neighbour's cover; this fixture uses 200 mm legs turned into the section, three across the width.
$bH = $box[$beamH]; $cA = $box[$colsA[0]]; $cB = $box[$colsA[1]]
$uIn = $cvBeamA + 9.5 + 7.95          # the bar's centre: cover, a stirrup's room, the radius
$uA = [double]$cA.min[0] + $cvColA + 7.95 + 6; $uB = [double]$cB.max[0] - $cvColA - 7.95 - 6
$uY = [double]$bH.min[1] + $uIn; $uSpread = ([double]$bH.max[1] - [double]$bH.min[1]) - 2 * $uIn
$uBars = @(@{ id = 'HOLD-top'; z = ([double]$bH.max[2] - $uIn); leg = -200.0 }, @{ id = 'HOLD-bot'; z = ([double]$bH.min[2] + $uIn); leg = 200.0 })
$rr += @($uBars | ForEach-Object {
    [ordered]@{ id = $_.id; host = @{ element_ids = @(, $beamH) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true; hold_ends = $true
        curve_mm = @(@($uA, $uY, ($_.z + $_.leg)), @($uA, $uY, $_.z), @($uB, $uY, $_.z), @($uB, $uY, ($_.z + $_.leg)))
        normal = @(0, 1, 0); layout = @{ rule = 'fixed_number'; number = 3; array_length_mm = $uSpread }; into = @($colsA[0], $colsA[1]) } })
# B2 A LAYER WITH TWO COUPLERS: beam C's top bars spliced at 0.33 and 0.66 of its measured length.
$cplTypes = @(Get-HzTypes 'OST_Coupler' | ForEach-Object { [string]$_.name })
$cpl = $(if ($cplTypes -contains 'CPL16M') { 'CPL16M' } elseif ($cplTypes.Count) { $cplTypes[0] } else { $null })
$CPLAT = @(0.33, 0.66)
# B4 THE ROOF: top and bottom mats hosted by the strip, into the slope, every component following the face.
$follow = @{ planarity_mm = 0.5; min_facet_mm = 100 }
function Roof($Id, $Normal) {
    [ordered]@{ id = $Id; host = @{ element_ids = @(, $roofStrip) }; into = @(, $roofSlope); face_normal = $Normal
        components = @(
            [ordered]@{ name = 'along'; direction = @(1, 0, 0); bar_type = 'N13'; offset_from_face_mm = ($cvRoof + 7); end_cover_mm = $cvRoof; side_cover_mm = ($cvRoof + 7)
                allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } },
            [ordered]@{ name = 'across'; direction = @(0, 1, 0); bar_type = 'N10'; offset_from_face_mm = ($cvRoof + 20); end_cover_mm = $cvRoof; side_cover_mm = ($cvRoof + 7)
                allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }
}
$mats += (Roof 'ROOF-top' @(0, 0, 1))
$mats += (Roof 'ROOF-bot' @(0, 0, -1))
# C1 DECLARED CROSSINGS. A top U and a bottom U (16M, three across) whose 300 mm legs stand in one
# plane at one station; and a bottom mat (10M at 200) whose two directions are laid at ONE depth.
$bUC = $box[$beamUC]; $sC = $box[$slabC]
$ucIn = $cvBeamUC + 9.5 + 7.95
$ucA = [double]$bUC.min[0] + $cvBeamUC + 7.95; $ucB = [double]$bUC.max[0] - $cvBeamUC - 7.95
$ucY = [double]$bUC.min[1] + $ucIn; $ucSpread = ([double]$bUC.max[1] - [double]$bUC.min[1]) - 2 * $ucIn
$ucT = [double]$bUC.max[2] - $ucIn; $ucBz = [double]$bUC.min[2] + $ucIn
$ucTop = @(@($ucA, $ucY, ($ucT - 300)), @($ucA, $ucY, $ucT), @($ucB, $ucY, $ucT), @($ucB, $ucY, ($ucT - 300)))
$ucBot = @(@($ucA, $ucY, ($ucBz + 300)), @($ucA, $ucY, $ucBz), @($ucB, $ucY, $ucBz), @($ucB, $ucY, ($ucBz + 300)))
foreach ($u in @(@{ id = 'UC-SUP'; c = $ucTop }, @{ id = 'UC-INF'; c = $ucBot })) {
    $rr += [ordered]@{ id = $u.id; host = @{ element_ids = @(, $beamUC) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
        curve_mm = $u.c; normal = @(0, 1, 0); layout = @{ rule = 'fixed_number'; number = 3; array_length_mm = $ucSpread } }
}
$mcOff = $cvSlabC + 6
# Two mat rules, one per direction: one mat's own components at one depth are refused by the mat itself
# (two_layers_occupy_the_same_plane); across rules the crossing order decides.
foreach ($d in $XY) {
    $mats += [ordered]@{ id = ('MC' + $d.n.ToUpper()); host = @{ element_ids = @(, $slabC) }; face_normal = @(0, 0, -1)
        components = @(, [ordered]@{ name = 'b'; direction = $d.v; bar_type = 'N10'; offset_from_face_mm = $mcOff; end_cover_mm = $cvSlabC
            side_cover_mm = ($cvSlabC + 10); allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }
}
# B5 AREA AND PATH REINFORCEMENT on the second slab: a 3000 x 3000 area (bottom, both ways) and a 1800 mm path.
$zS2 = [double]$box[$slab2].max[2]
$areaT = @(Get-HzTypes 'OST_AreaRein' | Select-Object -First 1)
if ($areaT.Count -eq 0) { $areaT = @([pscustomobject]@{ name = 'Structural Area Reinforcement 1' }) }
$pathT = @(Get-HzTypes 'OST_PathRein' | Select-Object -First 1)
if ($pathT.Count -eq 0) { $pathT = @([pscustomobject]@{ name = 'Structural Path Reinforcement 1' }) }
$AREA = @{ x0 = ($XS2 + 500); y0 = 500.0; w = 3000.0; h = 3000.0; major = 200.0; minor = 250.0 }
$PATH = @{ x0 = ($XS2 + 4000); x1 = ($XS2 + 5800); y = 3800.0; spacing = 200.0; length = 1200.0 }

$set = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = 'acceptance-1'; version = '1.0.0'; title = 'Acero 3.0 acceptance building' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N13'; type_name = '13M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(, @{ id = 'H135'; type_name = 'Stirrup/Tie - 135 deg.' })
    # B3 THE BUILDING IS DECLARED CLEAR: 25 mm between bars of different rules running alongside each other
    # (the harness's declaration, not a code value); crossing bars keep their default contact.
    clearances = [ordered]@{ between_rules_mm = 25
        # C1: the nested U legs are DECLARED in contact - the 25 mm above is for bars that merely run alongside.
        pairs = @(, [ordered]@{ rules = @('UC-SUP', 'UC-INF'); min_clear_mm = 0 }) }
    # C1 THE ORDER, declared: the bottom U's legs give way inward by one diameter; the mat's Y layer passes over its
    # X layer toward the slab's core, by contact.
    crossing_order = [ordered]@{ offset = 'diameter'
        orders = @(
            [ordered]@{ id = 'u-legs'; stays = @('UC-SUP'); moves = @('UC-INF'); move = 'legs'; legs = 'both' },
            [ordered]@{ id = 'mat-y-over-x'; stays = @('MCX'); moves = @('MCY'); move = 'translate'; direction = 'toward_host_core'; offset = 'contact' }) }
    area_rules = @(, [ordered]@{ id = 'AREA'; host = @{ element_ids = @(, $slab2) }; area_type_name = [string]$areaT[0].name
        boundary_mm = @(@($AREA.x0, $AREA.y0, $zS2), @(($AREA.x0 + $AREA.w), $AREA.y0, $zS2), @(($AREA.x0 + $AREA.w), ($AREA.y0 + $AREA.h), $zS2), @($AREA.x0, ($AREA.y0 + $AREA.h), $zS2))
        major_direction = @(1, 0, 0)
        layers = [ordered]@{ bottom_major = @{ bar_type = 'N16'; spacing_mm = $AREA.major }; bottom_minor = @{ bar_type = 'N10'; spacing_mm = $AREA.minor } } })
    path_rules = @(, [ordered]@{ id = 'PATH'; host = @{ element_ids = @(, $slab2) }; path_type_name = [string]$pathT[0].name; flip = $false
        path_mm = @(@($PATH.x0, $PATH.y, $zS2), @($PATH.x1, $PATH.y, $zS2))
        primary = @{ bar_type = 'N16'; spacing_mm = $PATH.spacing; length_mm = $PATH.length } })
    section_rules = @(
        [ordered]@{ id = 'COL'; host = @{ element_ids = (@($cols1) + @($cols2)) }; cover = @{ source = 'host' }
            layers = @(@{ face = 'top'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                       @{ face = 'bottom'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                       @{ face = 'left'; count = 1; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                       @{ face = 'right'; count = 1; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
            stirrups = (Stirrups 600) },
        [ordered]@{ id = 'BEAM'; host = @{ element_ids = $beams }; cover = @{ source = 'host' }
            layers = @(@{ face = 'top'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                       @{ face = 'bottom'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
            stirrups = (Stirrups 1000) })
    reinforcement_rules = $rr; mat_rules = $mats }
if ($cpl) {
    $set['section_rules'] = @($set['section_rules']) + @([ordered]@{ id = 'CPL'; host = @{ element_ids = @(, $beamC) }; cover = @{ source = 'host' }
        layers = @(@{ name = 'top'; face = 'top'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true
                      laps = @($CPLAT | ForEach-Object { @{ at_ratio = $_; coupler_type_name = $cpl } }) },
                   @{ name = 'bottom'; face = 'bottom'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true }) })
}

# =====================================================================  PLAN, APPLY, AUDIT
Write-Host "`n== plan, apply, audit ==" -ForegroundColor Cyan
$plan = (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
$rows = @(@(Get-HzPath $plan 'reinforcement') | Where-Object { $_ })
$bad = @($rows | Where-Object { Get-HzProp $_ 'code' })
Add-HzProbe -Run $run -Id 'A2' -Name 'acceptance: the plan refuses nothing' -Expected 'every row will build' `
    -Observed ("rows={0} refused={1} {2}" -f $rows.Count, $bad.Count, (($bad | Select-Object -First 3 | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + [string](Get-HzProp $_ 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 200) }) -join ' | ')) `
    -Ok ($rows.Count -gt 0 -and $bad.Count -eq 0) -Evidence @{ refused = $bad }

$ap = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ acero 3.0 acceptance' }
$applyOk = $null -ne $ap.Apply -and $ap.Apply.Ok
$verif = @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'verification') | Where-Object { $_ } }))
$nv = @($verif | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'A3' -Name 'acceptance: the apply verifies every set' -Expected 'applied, every set verified' `
    -Observed ("applied={0} sets={1} not_verified={2} {3}" -f $applyOk, $verif.Count, $nv.Count,
        $(if ($nv.Count) { [string](Get-HzProp $nv[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $nv[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600) } elseif (-not $applyOk) { Limit-HzText $(if ($ap.Apply) { $ap.Apply.Text } else { $ap.Dry.Text }) 600 } else { '' })) `
    -Ok ($applyOk -and $verif.Count -gt 0 -and $nv.Count -eq 0) -Evidence @{ not_verified = $nv }

$audit = (Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
# coupled_end_seated (stage 1b): the info note on a coupled end Revit left inside its coupler (documented; B2
# holds each piece's length to its stretch less at most the coupler's body per coupled end).
$accepted = @('hook_corner_opening', 'hook_end_extension', 'hook_corner_seat', 'coupled_end_seated')
$other = @(@(Get-HzPath $audit 'findings') | Where-Object { $_ -and ([string]$_.severity -ne 'info' -or [string]$_.code -notin $accepted) })
Add-HzProbe -Run $run -Id 'A4' -Name 'acceptance: the audit agrees' -Expected 'agrees, or agrees_with_notes whose every note is accepted' `
    -Observed ("verdict={0} other={1} {2}" -f (Get-HzPath $audit 'summary', 'verdict'), $other.Count, (($other | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($other.Count -eq 0) -Evidence @{ findings = $other }

# =====================================================================  INDEPENDENT CALCULATION
# THE BARS THIS TEMPLATE CALLS 10M, 13M AND 16M are 9.5, 12.7 and 15.9 mm (measured: nominal_diameter_mm of
# Autodesk's metric template bar types - not CSA's 11.3 / 16.0 / 19.5). Mass per metre is the round bar's
# area times 7850 kg/m3, the density this harness declares to the bridge too.
$DIA = @{ '10M' = 9.5; '13M' = 12.7; '16M' = 15.9 }
$KGM = @{}; foreach ($k in $DIA.Keys) { $KGM[$k] = [Math]::PI * $DIA[$k] * $DIA[$k] / 4 * 7850 / 1e6 }
$hand = @{}   # category -> @{ bars; kg }
function Add-Hand([string]$Cat, [string]$Bar, [int]$N, [double]$Lmm) {
    if (-not $hand.ContainsKey($Cat)) { $hand[$Cat] = @{ bars = 0; kg = 0.0 } }
    $hand[$Cat].bars += $N; $hand[$Cat].kg += $N * $Lmm / 1000.0 * $KGM[$Bar]
}
function Size($b, $i) { [double]$b.max[$i] - [double]$b.min[$i] }
function Count-Max([double]$Span, [double]$S) { [int][Math]::Ceiling($Span / $S - 1e-9) + 1 }
# Stirrups: 10M closed, two 135 degree hooks; developed length = the centreline perimeter inside the cover
# plus two hook allowances of 93.8 mm (Revit's 135 degree stirrup hook on 10M, measured on the duel model).
function Stirrup-Len([double]$B, [double]$H, [double]$Cv) { 2 * (($B - 2 * $Cv - 9.5) + ($H - 2 * $Cv - 9.5)) + 2 * 93.8 }
function Stirrup-Count([double]$L, [double]$Conf) {
    # $L is the run the stirrups are laid over: the member's length less its two end covers (measured: 33 on a
    # 4550 mm beam with 40 mm covers, 22 on a 3000 mm column).
    # 50 from each end; the confined zone at 100 gives up its last bar to the middle, the middle at <= 200
    # gives up its last to the mirrored zone, which keeps both of its ends: n + middle + (n + 1).
    $nConf = [int][Math]::Floor(($Conf - 1e-9) / 100) + 1
    2 * $nConf + [int][Math]::Ceiling(($L - 100 - 2 * $Conf) / 200 - 1e-9) + 1
}
foreach ($id in (@($cols1) + @($cols2))) {
    $b = $box[$id]; $L = Size $b 2
    $w = [Math]::Min((Size $b 0), (Size $b 1)); $d = [Math]::Max((Size $b 0), (Size $b 1))
    Add-Hand 'Structural Columns' '16M' 8 ($L - 2 * $cvCol)
    Add-Hand 'Structural Columns' '10M' (Stirrup-Count ($L - 2 * $cvCol) 600) (Stirrup-Len $w $d $cvCol)
}
# A BEAM RUNS FROM COLUMN FACE TO COLUMN FACE (measured: its solid is joined to the columns; its box keeps the
# location line's 5000 / 4500): the length is the grid span less the column's size along the beam.
$colX = Size $box[$cols1[0]] 0; $colY = Size $box[$cols1[0]] 1
foreach ($id in $beams) {
    $b = $box[$id]; $alongX = (Size $b 0) -gt (Size $b 1)
    $L = $(if ($alongX) { (Size $b 0) - $colX } else { (Size $b 1) - $colY })
    Add-Hand 'Structural Framing' '16M' 6 ($L - 2 * $cvBeam)
    Add-Hand 'Structural Framing' '10M' (Stirrup-Count ($L - 2 * $cvBeam) 1000) (Stirrup-Len 300 600 $cvBeam)
}
foreach ($i in 0..5) {
    $b = $box[$foots[$i]]; $Lx = Size $b 0; $Ly = Size $b 1
    Add-Hand 'Structural Foundations' '16M' (Count-Max ($Ly - 2 * ($cvFoot + 10)) 200) ($Lx - 2 * $cvFoot)
    Add-Hand 'Structural Foundations' '16M' (Count-Max ($Lx - 2 * ($cvFoot + 10)) 200) ($Ly - 2 * $cvFoot)
    Add-Hand 'Structural Foundations' '16M' 4 (600 + ([double]$box[$cols1[$i]].min[2]) - ([double]$b.min[2] + $cvFoot + 60))
}
foreach ($face in 1, 2) {
    Add-Hand 'Floors' '10M' (Count-Max (4000 - 2 * ($cvSlab + 10)) 200) (6000 - 2 * $cvSlab)
    Add-Hand 'Floors' '10M' (Count-Max (6000 - 2 * ($cvSlab + 10)) 200) (4000 - 2 * $cvSlab)
    Add-Hand 'Walls' '13M' (Count-Max (3000 - 2 * ($cvWall + 10)) 250) (3000 - 2 * $cvWall)
    Add-Hand 'Walls' '13M' (Count-Max (3000 - 2 * ($cvWall + 10)) 250) (3000 - 2 * $cvWall)
}
# STAGE 1b. The held U-bars: two legs and the run, three of each.
foreach ($u in $uBars) { Add-Hand 'Structural Framing' '16M' 3 (2 * 200 + ($uB - $uA)) }
# Beam C, face to face: the coupled top layer is the member less its two end covers in three pieces (the gap
# Revit leaves inside each coupler is a few mm of 16M - inside the 3 %), the bottom layer one piece.
$LC = (Size $box[$beamC] 0) - (Size $box[$colsA[0]] 0)
if ($cpl) {
    Add-Hand 'Structural Framing' '16M' (2 * ($CPLAT.Count + 1)) (($LC - 2 * $cvBeamA) / ($CPLAT.Count + 1))
    Add-Hand 'Structural Framing' '16M' 2 ($LC - 2 * $cvBeamA)
}
# The roof, top and bottom: across bars on stations over the face's plan (5500 mm along x) less the side covers,
# along bars over its 3000 mm; an along bar runs 1500 flat and 4000 on the slope, less the end covers.
$roofCos = 1 / [Math]::Sqrt(1 + $RS * $RS)
$nAcross = Count-Max (5500 - 2 * ($cvRoof + 7)) 200
$nAlong = Count-Max (3000 - 2 * ($cvRoof + 7)) 200
foreach ($face in 1, 2) {
    Add-Hand 'Floors' '10M' $nAcross (3000 - 2 * $cvRoof)
    Add-Hand 'Floors' '13M' $nAlong ((1500 - $cvRoof) + (4000 - $cvRoof) / $roofCos)
}
# AREA AND PATH REINFORCEMENT on the second slab (B5): their bars are RebarInSystem, which mode=quantities
# counts beside the rebar sets. By hand as B5 writes them: a layer at maximum spacing over the outline is
# ceil(width / spacing) + 1 bars as long as the outline along them; a path's bars ceil(path / spacing) + 1,
# each the declared length. Added only for a system the apply verified (a template without the type builds none).
$sysBuilt = @{}
foreach ($v in @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'system_verification') | Where-Object { $_ } }))) {
    if ((Get-HzProp $v 'verified') -eq $true) { $sysBuilt[[string](Get-HzProp $v 'rule_id')] = $true }
}
if ($sysBuilt['AREA']) {
    Add-Hand 'Floors' '16M' (Count-Max $AREA.h $AREA.major) $AREA.w
    Add-Hand 'Floors' '10M' (Count-Max $AREA.w $AREA.minor) $AREA.h
}
if ($sysBuilt['PATH']) { Add-Hand 'Floors' '16M' (Count-Max ($PATH.x1 - $PATH.x0) $PATH.spacing) $PATH.length }
# STAGE 1c. The U bars: the top one as declared; the bottom one's run two diameters shorter (its legs nested).
Add-Hand 'Structural Framing' '16M' 3 (2 * 300 + ($ucB - $ucA))
Add-Hand 'Structural Framing' '16M' 3 (2 * 300 + ($ucB - $ucA) - 2 * $DIA['16M'])
# The mat laid at one depth: its counts and lengths are a mat's; the Y layer only stands higher.
$LxC = Size $sC 0; $LyC = Size $sC 1
Add-Hand 'Floors' '10M' (Count-Max ($LyC - 2 * ($cvSlabC + 10)) 200) ($LxC - 2 * $cvSlabC)
Add-Hand 'Floors' '10M' (Count-Max ($LxC - 2 * ($cvSlabC + 10)) 200) ($LyC - 2 * $cvSlabC)
$run.Fixture['systems_in_hand'] = @($sysBuilt.Keys | Sort-Object)

$q = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'quantities' -Arguments @{ mode = 'quantities'; group_by = @('host_category'); density_kg_per_m3 = 7850
    density_source = 'reinforcing steel, 7850 kg/m3, declared by this acceptance harness'; max_rows = 100 }).Result
$model = @{}
foreach ($g in @(Get-HzPath $q 'groups')) { $model[[string](Get-HzPath $g 'group', 'host_category')] = @{ bars = [int](Get-HzProp $g 'bars'); kg = [double](Get-HzProp $g 'mass_kg') } }
$lines = @(); $barsOk = $true; $kgCatOk = $true
foreach ($cat in ($hand.Keys | Sort-Object)) {
    $m = $model[$cat]
    $lines += ("{0}: bars {1}/{2} kg {3:N1}/{4:N1}" -f $cat, $(if ($m) { $m.bars } else { 'none' }), $hand[$cat].bars, $(if ($m) { $m.kg } else { 0 }), $hand[$cat].kg)
    if (-not $m -or $m.bars -ne $hand[$cat].bars) { $barsOk = $false }
    if (-not $m -or [Math]::Abs($m.kg - $hand[$cat].kg) -gt 0.03 * $hand[$cat].kg) { $kgCatOk = $false }
}
$kgModel = ($model.Values | ForEach-Object { $_.kg } | Measure-Object -Sum).Sum
$kgHand = ($hand.Values | ForEach-Object { $_.kg } | Measure-Object -Sum).Sum
$run.Fixture['quantities'] = @{ model = $model; hand = $hand; kg_model = $kgModel; kg_hand = $kgHand }
$qCov = [string](Get-HzPath $q 'coverage', 'coverage')
Add-HzProbe -Run $run -Id 'A5' -Name 'acceptance: the bars the model carries, by category, against an independent count' -Expected 'equal in every category; coverage complete' `
    -Observed (($lines -join ' | ') + (' | coverage={0} system_bars={1}' -f $qCov, (Get-HzPath $q 'totals', 'sets_from_systems'))) -Ok ($barsOk -and $qCov -eq 'complete') -Evidence @{ model = $model; hand = $hand }
Add-HzProbe -Run $run -Id 'A6' -Name 'acceptance: the kilograms against an independent calculation' -Expected 'within 2 % in total and 3 % per category' `
    -Observed ("model={0:N1} kg hand={1:N1} kg diff={2:N2} % | {3}" -f $kgModel, $kgHand, (100 * ($kgModel - $kgHand) / $kgHand), ($lines -join ' | ')) `
    -Ok ($kgCatOk -and [Math]::Abs($kgModel - $kgHand) -le 0.02 * $kgHand) -Evidence @{ kg_model = $kgModel; kg_hand = $kgHand }

# =====================================================================  STAGE 1b, JUDGED INSIDE THE BUILDING
Write-Host "`n== stage 1b ==" -ForegroundColor Cyan
# The bars a host carries, re-read from the model: one entry per standing bar - its hook-free centreline
# (the bar at position 0 moved by Revit's own offset for that position), its set, its length and its ends.
function Get-HzBars([long]$HostId) {
    $rows = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "bars-$HostId" -Arguments @{
        mode = 'rebar'; host_id = $HostId; include_bar_positions = $true; max_rows = 500 }).Result 'rows') | Where-Object { $_ })
    foreach ($r in $rows) {
        $pts = @(@(Get-HzPath $r 'geometry', 'centreline_points_as_declared_mm') | Where-Object { $_ })
        foreach ($p in @(@(Get-HzProp $r 'bar_positions') | Where-Object { $_ })) {
            if ((Get-HzProp $p 'exists') -eq $false) { continue }
            $o = Get-HzProp $p 'offset_from_first_bar_mm'
            [pscustomobject]@{ SetId = [long]$r.id; Row = $r
                Points = @($pts | ForEach-Object { , @(([double]$_[0] + [double]$o.x), ([double]$_[1] + [double]$o.y), ([double]$_[2] + [double]$o.z)) }) }
        }
    }
}
function Get-HzWorst($A, $B) {
    # The worst vertex-to-vertex distance between two polylines of as many vertices, in either direction.
    if (@($A).Count -ne @($B).Count) { return [double]::PositiveInfinity }
    $best = [double]::PositiveInfinity
    foreach ($rev in $false, $true) {
        $w = 0.0; $n = @($A).Count
        for ($i = 0; $i -lt $n; $i++) {
            $p = $A[$i]; $q = $(if ($rev) { $B[$n - 1 - $i] } else { $B[$i] })
            $d = [Math]::Sqrt([Math]::Pow($p[0] - $q[0], 2) + [Math]::Pow($p[1] - $q[1], 2) + [Math]::Pow($p[2] - $q[2], 2))
            if ($d -gt $w) { $w = $d }
        }
        if ($w -lt $best) { $best = $w }
    }
    $best
}
function Get-HzRuleFindings([string]$Like) { @(@(Get-HzPath $audit 'findings') | Where-Object { $_ -and [string]$_.rule_id -like $Like -and ([string]$_.severity -ne 'info' -or [string]$_.code -notin $accepted) }) }
function Get-HzRuleVerif([string]$Like) { @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like }) }

# ---- B1 hold_ends
$vHold = @(Get-HzRuleVerif 'HOLD-*')
$holds = @($vHold | ForEach-Object { Get-HzPath $_ 'checks', 'hold_ends' } | Where-Object { $_ })
$holdBad = @($holds | Where-Object { (Get-HzProp $_ 'verified') -ne $true -or $null -eq (Get-HzProp $_ 'after_commit_mm') -or [double](Get-HzProp $_ 'after_commit_mm') -gt 1.0 })
# INDEPENDENT: the six bars as declared (each U at its three stations across the width) against the six the
# model carries in beam H, vertex by vertex.
$declU = @(foreach ($u in $uBars) { foreach ($k in 0..2) { $y = $uY + $k * $uSpread / 2
    , @(@($uA, $y, ($u.z + $u.leg)), @($uA, $y, $u.z), @($uB, $y, $u.z), @($uB, $y, ($u.z + $u.leg))) } })
$modelU = @(Get-HzBars $beamH)
$uWorst = @($declU | ForEach-Object { $d = $_; ($modelU | ForEach-Object { Get-HzWorst $d $_.Points } | Measure-Object -Minimum).Minimum })
$uMax = ($uWorst | Measure-Object -Maximum).Maximum
$fHold = @(Get-HzRuleFindings 'HOLD-*')
Add-HzProbe -Run $run -Id 'B1' -Name 'stage 1b: beam bars anchored into the columns with hold_ends stay where the plan put them' `
    -Expected 'two held sets, every checks.hold_ends verified with after_commit_mm <= 1; the 6 bars in beam H each within 1 mm of the declared polyline at every vertex; no audit finding on them' `
    -Observed ("sets={0} held={1}/{2} after_commit_mm={3} model_bars={4} worst_vertex_mm={5:N3} findings={6} {7}" -f $vHold.Count, ($holds.Count - $holdBad.Count), $holds.Count,
        (($holds | ForEach-Object { Get-HzProp $_ 'after_commit_mm' }) -join ','), $modelU.Count, $uMax, $fHold.Count,
        (($fHold | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' ' + [string]$_.observed }) -join '; ')) `
    -Ok ($vHold.Count -eq 2 -and $holds.Count -eq 2 -and $holdBad.Count -eq 0 -and $modelU.Count -eq 6 -and $uMax -le 1.0 -and $fHold.Count -eq 0) `
    -Evidence @{ holds = $holds; worst_per_declared_bar = $uWorst; findings = $fHold }

# ---- B2 two couplers in one layer
if (-not $cpl) {
    Add-HzProbe -Run $run -Id 'B2' -Name 'stage 1b: a layer with two couplers' -Status 'fixture_missing' -Expected 'a coupler type in the Rebar Couplers category' -Observed 'none'
} else {
    $bC = $box[$beamC]; $uMinC = [double]$box[$colsA[2]].max[0]   # beam C runs from column 3's face
    $zMidC = ([double]$bC.min[2] + [double]$bC.max[2]) / 2
    $stations = @($CPLAT | ForEach-Object { $uMinC + $_ * $LC })
    $cuts = @(($uMinC + $cvBeamA)) + $stations + @(($uMinC + $LC - $cvBeamA))
    $expLen = @(for ($k = 0; $k -lt $cuts.Count - 1; $k++) { $cuts[$k + 1] - $cuts[$k] })
    $rowsC = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'bars-beamC' -Arguments @{
        mode = 'rebar'; host_id = $beamC; include_bar_positions = $false; max_rows = 50 }).Result 'rows') | Where-Object { $_ })
    function MinX($r) { (@(Get-HzPath $r 'geometry', 'centreline_points_as_declared_mm') | ForEach-Object { [double]$_[0] } | Measure-Object -Minimum).Minimum }
    $top = @($rowsC | Where-Object { [double]@(Get-HzPath $_ 'geometry', 'centreline_points_as_declared_mm')[0][2] -gt $zMidC } | Sort-Object { MinX $_ })
    $cplIds = @($top | ForEach-Object { , @(@(Get-HzProp $_ 'terminations') | ForEach-Object { [long]$_.coupler_id } | Where-Object { $_ -gt 0 }) })
    $lines = @(); $okC = ($top.Count -eq $expLen.Count)
    for ($k = 0; $k -lt $top.Count; $k++) {
        $t = $top[$k]; $ends = @($cplIds[$k])
        $boxes = @(@(Get-HzProp $t 'terminations') | ForEach-Object { Get-HzProp $_ 'coupler_box_mm' } | Where-Object { $_ })
        $body = ($boxes | ForEach-Object { [double]$_.max[0] - [double]$_.min[0] } | Measure-Object -Maximum).Maximum
        if ($null -eq $body) { $body = 0.0 }
        $len = [double](Get-HzPath $t 'measured', 'bar_centreline_length_mm'); $q = [int](Get-HzPath $t 'measured', 'quantity')
        $want = $(if ($k -lt $expLen.Count) { $expLen[$k] } else { [double]::NaN })
        $wantEnds = $(if ($k -eq 0 -or $k -eq $top.Count - 1) { 1 } else { 2 })
        $lenOk = ($len - $want) -le 1.0 -and ($want - $len) -le ($wantEnds * $body + 1.0)
        $okC = $okC -and $lenOk -and $q -eq 2 -and $ends.Count -eq $wantEnds
        $lines += ("p{0}: bars={1} coupled_ends={2}/{3} length={4:N1} expected={5:N1} (coupler body {6:N1})" -f ($k + 1), $q, $ends.Count, $wantEnds, $len, $want, $body)
    }
    # THE COUPLERS ARE SHARED: each piece's coupled end holds the same coupler as its neighbour's, and each sits
    # on its declared station.
    $shared = $okC -and $top.Count -eq 3 -and @($cplIds[1]).Count -eq 2 -and (@($cplIds[1]) -contains @($cplIds[0])[0]) -and (@($cplIds[1]) -contains @($cplIds[2])[0]) -and @($cplIds[0])[0] -ne @($cplIds[2])[0]
    $atStation = @()
    if ($shared) {
        foreach ($k in 0, 2) {
            $cb = @(@(Get-HzProp $top[$k] 'terminations') | Where-Object { [long]$_.coupler_id -gt 0 })[0].coupler_box_mm
            $s = $stations[[int]($k / 2)]
            $atStation += ([double]$cb.min[0] - 1.0 -le $s -and $s -le [double]$cb.max[0] + 1.0)
        }
    }
    $bottom = @($rowsC | Where-Object { [double]@(Get-HzPath $_ 'geometry', 'centreline_points_as_declared_mm')[0][2] -le $zMidC })
    $vC = @(Get-HzRuleVerif 'CPL#*'); $vCbad = @($vC | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    $fC = @(Get-HzRuleFindings 'CPL#*')
    $planC = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'CPL#*#top#p*' })
    Add-HzProbe -Run $run -Id 'B2' -Name 'stage 1b: a layer with two couplers - three pieces, the middle one coupled at both ends, lengths by hand' `
        -Expected ("plan and model: 3 top pieces of 2 bars, coupled ends 1/2/1 sharing 2 couplers on the stations; each length = its stretch between end covers and stations ({0}) less at most the coupler body per coupled end; bottom 1 piece; verified; no audit finding" -f (($expLen | ForEach-Object { '{0:N1}' -f $_ }) -join '/')) `
        -Observed ("plan_pieces={0} model_top={1} bottom={2} shared={3} at_station={4} verified={5}/{6} findings={7} | {8} {9}" -f $planC.Count, $top.Count, $bottom.Count, $shared, ($atStation -join ','),
            ($vC.Count - $vCbad.Count), $vC.Count, $fC.Count, ($lines -join ' | '),
            (($fC | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' ' + [string]$_.observed }) -join '; ')) `
        -Ok ($planC.Count -eq 3 -and $okC -and $shared -and @($atStation | Where-Object { -not $_ }).Count -eq 0 -and $atStation.Count -eq 2 -and
             $bottom.Count -eq 1 -and $vC.Count -eq 4 -and $vCbad.Count -eq 0 -and $fC.Count -eq 0) `
        -Evidence @{ expected_lengths = $expLen; stations = $stations; pieces = $lines; couplers = $cplIds; findings = $fC }
}

# ---- B3 the building declared clear
$pc = Get-HzProp $plan 'overlap_check'; $ao = Get-HzPath $audit 'overlaps'
$clash = @($rows | Where-Object { [string](Get-HzProp $_ 'code') -in @('bars_intersect', 'bars_closer_than_declared_clearance', 'overlap_not_evaluable') })
$ovF = @(@(Get-HzPath $audit 'findings') | Where-Object { $_ -and ([string](Get-HzProp $_ 'code') -eq 'overlapping_bar' -or [string](Get-HzProp $_ 'about') -eq 'overlapping_bar') })
$unread = @(@(Get-HzProp $ao 'unreadable') | Where-Object { $_ })
Add-HzProbe -Run $run -Id 'B3' -Name 'stage 1b: the whole building, declared clear by 25 mm, plans without a clash and audits without overlapping_bar' `
    -Expected 'plan overlap_check.checked = declared_clearance, no row bars_intersect / closer than declared / not evaluable; audit overlaps.checked = declared_clearance, every set compared, nothing unreadable, 0 overlapping_bar' `
    -Observed ("plan checked={0} clashes={1} | audit checked={2} sets_compared={3}/{4} unreadable={5} overlapping_bar={6} {7}" -f (Get-HzProp $pc 'checked'), $clash.Count,
        (Get-HzProp $ao 'checked'), (Get-HzProp $ao 'sets_compared'), $verif.Count, $unread.Count, $ovF.Count,
        $(if ($clash.Count) { [string](Get-HzProp $clash[0] 'rule_id') + ' ' + (Limit-HzText ([string](Get-HzProp $clash[0] 'why')) 300) } elseif ($ovF.Count) { [string]$ovF[0].rule_id + ' vs ' + [string](Get-HzProp $ovF[0] 'other_rule_id') + ' clear=' + [string](Get-HzProp $ovF[0] 'clear_mm') } else { '' })) `
    -Ok ([string](Get-HzProp $pc 'checked') -eq 'declared_clearance' -and $clash.Count -eq 0 -and [string](Get-HzProp $ao 'checked') -eq 'declared_clearance' -and
         [int](Get-HzProp $ao 'sets_compared') -ge $verif.Count -and $unread.Count -eq 0 -and $ovF.Count -eq 0) `
    -Evidence @{ plan = $pc; audit = $ao; clashes = $clash; findings = $ovF }

# ---- B4 the roof, following its face
# INDEPENDENT: the face is the strip's top (bottom) and, past the break at XR + 1500, the slope rising at the
# declared 240 / 4000. A bar offset d along each facet's own normal stands, at its station x, at the higher of
# the two offset planes (the inward offset of a valley): top z0 + max(-d, s(x - xb) - d / cos), bottom
# zb0 + max(d, s(x - xb) + d / cos). The across bars are straight; each is judged at its own height.
$xb = $XR + 1500; $zT = [double]$box[$roofStrip].max[2]; $zB = [double]$box[$roofStrip].min[2]; $dA = $cvRoof + 20
$roofBars = @(@(Get-HzBars $roofStrip) + @(Get-HzBars $roofSlope))
$zMid = { param($x) ($zT + $zB) / 2 + [Math]::Max(0, $RS * ($x - $xb)) }
$across = @($roofBars | Where-Object { [Math]::Abs($_.Points[-1][1] - $_.Points[0][1]) -gt [Math]::Abs($_.Points[-1][0] - $_.Points[0][0]) })
$along = @($roofBars | Where-Object { [Math]::Abs($_.Points[-1][1] - $_.Points[0][1]) -le [Math]::Abs($_.Points[-1][0] - $_.Points[0][0]) })
$hDev = @(); $nTop = 0; $nBot = 0
foreach ($b in $across) {
    $x = $b.Points[0][0]; $z = $b.Points[0][2]; $sx = $RS * ($x - $xb)
    $isTop = $z -gt (& $zMid $x)
    $want = $(if ($isTop) { $nTop++; $zT + [Math]::Max(-$dA, $sx - $dA / $roofCos) } else { $nBot++; $zB + [Math]::Max($dA, $sx + $dA / $roofCos) })
    $flat = ($b.Points | ForEach-Object { [Math]::Abs($_[2] - $z) } | Measure-Object -Maximum).Maximum
    $hDev += [pscustomobject]@{ x = [Math]::Round($x, 1); top = $isTop; z = [Math]::Round($z, 2); expected = [Math]::Round($want, 2); dev = [Math]::Abs($z - $want) + $flat }
}
$hWorst = ($hDev | ForEach-Object { $_.dev } | Measure-Object -Maximum).Maximum
$aTop = @($along | Where-Object { $_.Points[0][2] -gt (& $zMid $_.Points[0][0]) }).Count; $aBot = $along.Count - $aTop
$vR = @(Get-HzRuleVerif 'ROOF-*'); $vRbad = @($vR | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
$fR = @(Get-HzRuleFindings 'ROOF-*')
Add-HzProbe -Run $run -Id 'B4' -Name 'stage 1b: a roof of two floors, top and bottom mats following the face, each across bar at its own height by hand' `
    -Expected ("across bars {0} top + {0} bottom, each within 1 mm of face - cover - offsets at its station; along bars {1} + {1}; every set verified; no audit finding" -f $nAcross, $nAlong) `
    -Observed ("across top={0} bottom={1} worst_height_dev_mm={2:N3} | along top={3} bottom={4} | sets verified={5}/{6} findings={7} {8}" -f $nTop, $nBot, $hWorst, $aTop, $aBot,
        ($vR.Count - $vRbad.Count), $vR.Count, $fR.Count, (($fR | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' ' + [string]$_.observed }) -join '; ')) `
    -Ok ($nTop -eq $nAcross -and $nBot -eq $nAcross -and $aTop -eq $nAlong -and $aBot -eq $nAlong -and $hWorst -le 1.0 -and $vR.Count -ge 4 -and $vRbad.Count -eq 0 -and $fR.Count -eq 0) `
    -Evidence @{ heights = $hDev; findings = $fR }

# ---- B5 area and path reinforcement
$sysV = @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'system_verification') | Where-Object { $_ } }))
$sysBad = @($sysV | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
$sysRows = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'systems' -Arguments @{ mode = 'reinforcement_systems'; max_rows = 50 }).Result 'rows') |
    Where-Object { $_ -and [long](Get-HzProp $_ 'host_id') -eq $slab2 })
$members = @($sysRows | ForEach-Object { @(Get-HzProp $_ 'member_rebar_ids') } | Where-Object { $_ })
$mrows = @()
if ($members.Count) {
    $mrows = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'system-bars' -Arguments @{ element_ids = @($members | ForEach-Object { [long]$_ }); include_links = $false; max_rows = 50
        parameter_format = 'full'; return_parameters = @('REBAR_ELEM_QUANTITY_OF_BARS', 'REBAR_ELEM_LENGTH', 'REBAR_ELEM_TOTAL_LENGTH', 'REBAR_ELEM_BAR_SPACING') }).Result.rows) | Where-Object { $_ })
}
$run.Fixture['systems'] = @{ verification = $sysV; rows = $sysRows; members = $mrows }
# INDEPENDENT: a layer of an area laid at maximum spacing over the outline's width is ceil(width / spacing) + 1
# bars as long as the outline is along them; a path's bars are ceil(path / spacing) + 1, each the declared
# length. Read back per system from its own bars (quantity and bar length) and, for the area, its layers' lines.
$sysKind = @{}; foreach ($r in $sysRows) { $sysKind[[string]$r.kind] = $r }
function Get-HzSysBars($Row) {
    $ids = @(@(Get-HzProp $Row 'member_rebar_ids') | Where-Object { $_ } | ForEach-Object { [long]$_ })
    @($mrows | Where-Object { $ids -contains [long]$_.element_id } | ForEach-Object {
        '{0}x{1:N0}' -f [int](Get-HzPath $_ 'parameters', 'REBAR_ELEM_QUANTITY_OF_BARS', 'raw'), ([double](Get-HzPath $_ 'parameters', 'REBAR_ELEM_LENGTH', 'raw') * 304.8) } | Sort-Object)
}
$wantArea = @(('{0}x{1:N0}' -f (Count-Max $AREA.h $AREA.major), $AREA.w), ('{0}x{1:N0}' -f (Count-Max $AREA.w $AREA.minor), $AREA.h) | Sort-Object)
$wantPath = @('{0}x{1:N0}' -f (Count-Max ($PATH.x1 - $PATH.x0) $PATH.spacing), $PATH.length)
$gotArea = @($(if ($sysKind['AreaReinforcement']) { Get-HzSysBars $sysKind['AreaReinforcement'] }))
$gotPath = @($(if ($sysKind['PathReinforcement']) { Get-HzSysBars $sysKind['PathReinforcement'] }))
$lines = @{}; if ($sysKind['AreaReinforcement']) { foreach ($l in @(@(Get-HzProp $sysKind['AreaReinforcement'] 'layers') | Where-Object { $_ })) { $lines[[string]$l.layer] = $l.lines } }
$linesOk = [int]$lines['BottomOrBackMajor'] -eq (Count-Max $AREA.h $AREA.major) -and [int]$lines['BottomOrBackMinor'] -eq (Count-Max $AREA.w $AREA.minor)
Add-HzProbe -Run $run -Id 'B5' -Name 'stage 1b: area and path reinforcement on a slab of the building, quantities read back' `
    -Expected ("both systems verified; area bars {0} (bottom major / minor lines {1}/{2}); path bars {3}" -f ($wantArea -join ' + '), (Count-Max $AREA.h $AREA.major), (Count-Max $AREA.w $AREA.minor), ($wantPath -join '')) `
    -Observed ("systems={0} verified={1}/{2} | area {3} lines {4}/{5} | path {6}" -f $sysRows.Count, ($sysV.Count - $sysBad.Count), $sysV.Count, ($gotArea -join ' + '),
        $lines['BottomOrBackMajor'], $lines['BottomOrBackMinor'], ($gotPath -join ' + ')) `
    -Ok ($sysRows.Count -eq 2 -and $sysV.Count -eq 2 -and $sysBad.Count -eq 0 -and $linesOk -and
         ($gotArea -join '|') -eq ($wantArea -join '|') -and ($gotPath -join '|') -eq ($wantPath -join '|')) `
    -Evidence @{ verification = $sysV; rows = $sysRows; members = $mrows; want = @{ area = $wantArea; path = $wantPath } }
# ---- C1 crossing_order
# INDEPENDENT: where THIS harness puts the bars. The bottom U's legs one 16M diameter (15.9, the template's bar)
# inside its declared stations at each of its three positions; the Y bars of the mat two 10M radii (9.5, contact
# with the X bars) above the declared depth; everything else where it was declared.
$dU = $DIA['16M']; $dM = $DIA['10M']
function Move-Poly($Poly, [double]$Dx, [double]$Dy, [double]$Dz) { , @($Poly | ForEach-Object { , @(([double]$_[0] + $Dx), ([double]$_[1] + $Dy), ([double]$_[2] + $Dz)) }) }
$ucBotMoved = @(@(($ucA + $dU), $ucY, ($ucBz + 300)), @(($ucA + $dU), $ucY, $ucBz), @(($ucB - $dU), $ucY, $ucBz), @(($ucB - $dU), $ucY, ($ucBz + 300)))
$expTop = @(foreach ($k in 0..2) { Move-Poly $ucTop 0 ($k * $ucSpread / 2) 0 })
$expBot = @(foreach ($k in 0..2) { Move-Poly $ucBotMoved 0 ($k * $ucSpread / 2) 0 })
$declBot = @(foreach ($k in 0..2) { Move-Poly $ucBot 0 ($k * $ucSpread / 2) 0 })
$modelUC = @(Get-HzBars $beamUC)
function Get-Nearest($Exp, $Model) { (@($Model) | ForEach-Object { Get-HzWorst $Exp $_.Points } | Measure-Object -Minimum).Minimum }
$topDev = @($expTop | ForEach-Object { Get-Nearest $_ $modelUC })
$botDev = @($expBot | ForEach-Object { Get-Nearest $_ $modelUC })
$botFromDecl = @($declBot | ForEach-Object { Get-Nearest $_ $modelUC })
$zMat = [double]$sC.min[2] + $mcOff
$modelMC = @(Get-HzBars $slabC)
$mcY = @($modelMC | Where-Object { [Math]::Abs($_.Points[-1][1] - $_.Points[0][1]) -gt [Math]::Abs($_.Points[-1][0] - $_.Points[0][0]) })
$mcX = @($modelMC | Where-Object { [Math]::Abs($_.Points[-1][1] - $_.Points[0][1]) -le [Math]::Abs($_.Points[-1][0] - $_.Points[0][0]) })
function Get-ZDev($Bars, [double]$Z) { $m = ($Bars | ForEach-Object { [Math]::Max([Math]::Abs($_.Points[0][2] - $Z), [Math]::Abs($_.Points[-1][2] - $Z)) } | Measure-Object -Maximum).Maximum; if ($null -eq $m) { [double]::PositiveInfinity } else { $m } }
$yDev = Get-ZDev $mcY ($zMat + $dM); $yFromDecl = Get-ZDev $mcY $zMat; $xDev = Get-ZDev $mcX $zMat
$nMcY = Count-Max ((Size $sC 0) - 2 * ($cvSlabC + 10)) 200; $nMcX = Count-Max ((Size $sC 1) - 2 * ($cvSlabC + 10)) 200
$planDisp = @(@(Get-HzPath $plan 'crossing_order', 'displacements') | Where-Object { $_ })
$auditDisp = @(@(Get-HzPath $audit 'crossing_displacements') | Where-Object { $_ })
$dispIds = @($planDisp | ForEach-Object { [string](Get-HzProp $_ 'rule_id') } | Sort-Object)
$auditOk = @($auditDisp | Where-Object { (Get-HzProp $_ 'agrees') -eq $true }).Count
$botMax = ($botDev | Measure-Object -Maximum).Maximum; $topMax = ($topDev | Measure-Object -Maximum).Maximum; $botDeclMax = ($botFromDecl | Measure-Object -Maximum).Maximum
$fC1 = @(Get-HzRuleFindings 'UC-*') + @(Get-HzRuleFindings 'MC*')
$run.Fixture['crossing'] = @{ plan = $planDisp; audit = $auditDisp; bottom_u_dev = $botDev; top_u_dev = $topDev; bottom_u_from_declared = $botFromDecl
    mat_y_dev = $yDev; mat_y_from_declared = $yFromDecl; mat_x_dev = $xDev }
Add-HzProbe -Run $run -Id 'C1' -Name 'stage 1c: crossing_order in the building - nested U legs and a mat laid at one depth, displaced as declared and measured by hand' `
    -Expected ('plan displacements exactly MCY#b and UC-INF; audit crossing_displacements 2, both agree; model: 3 bottom U within 1 mm of the legs moved {0} inward (listed apart: within {1:N1} of the declared), 3 top U within 1 mm of declared; {2} Y bars within 1 mm of the declared depth + {3} (listed apart: within {4:N1} of it), {5} X bars within 1 mm of the declared depth; no finding on them' -f $dU, ($dU + 5), $nMcY, $dM, ($dM + 5), $nMcX) `
    -Observed ('plan=[{0}] audit={1}/{2} agree | U: bottom_worst={3:N3} (from declared {4:N3}) top_worst={5:N3} model_bars={6} | mat: Y={7} worst={8:N3} (from declared {9:N3}) X={10} worst={11:N3} | findings={12} {13}' -f
        ($dispIds -join ','), $auditOk, $auditDisp.Count, $botMax, $botDeclMax, $topMax, $modelUC.Count, $mcY.Count, $yDev, $yFromDecl, $mcX.Count, $xDev, $fC1.Count,
        (($fC1 | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' ' + [string]$_.observed }) -join '; ')) `
    -Ok (($dispIds -join ',') -eq 'MCY#b,UC-INF' -and $auditDisp.Count -eq 2 -and $auditOk -eq 2 -and $modelUC.Count -eq 6 -and
         $botMax -le 1.0 -and $topMax -le 1.0 -and $botDeclMax -le ($dU + 5) -and
         $mcY.Count -eq $nMcY -and $mcX.Count -eq $nMcX -and $yDev -le 1.0 -and $yFromDecl -le ($dM + 5) -and $xDev -le 1.0 -and $fC1.Count -eq 0) `
    -Evidence @{ plan = $planDisp; audit = $auditDisp; findings = $fC1 }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
