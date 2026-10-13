#Requires -Version 5.1
<#
  ACERO 3.0, overlapping_bar, LIVE: bars of DIFFERENT rules against each other, on a DISPOSABLE
  fixture (Autodesk metric template: M_Concrete-Rectangular-Column 450 x 600mm,
  M_Concrete-Rectangular Beam 300 x 600mm, a concrete basic wall of 200 mm or more, 16M/10M,
  Stirrup/Tie 135 degree hooks).

  MEASURED BEFORE THIS LOT (Revit 2024 and 2026, verify-rebar-walls.ps1): a wall's horizontals laid
  through a boundary element's stirrups were pushed by Revit until they touched them, the array
  12.5 mm short. Revit moves a bar that crosses another and says nothing.

    O1  joint: a beam bar declared through the column's face bar is refused by the plan - both
        sets, bars_intersect, each naming the other - before anything is written
    O2  joint: the same beam bar between two stirrup stations, clear of the cage, under a declared
        clearance of 25 mm: planned, applied, and the audit reports no overlapping_bar
    O3  wall: a mat whose horizontals run through the boundary element (the measured case), with NO
        clearances declared: refused as bars_intersect, and the reply says only intersections
        were checked
    O4  wall: the mat kept out of the boundary element, under a declared clearance of 25 mm:
        planned, applied, and the audit reports no overlapping_bar
    O5  joint, second pair: the plan of the column cage and a beam bar through its face bar, as
        ONE set, refuses the pair
    O6  the same two rules built as two separate sets (neither plan can see the other's bars), then
        audited as the one set: the audit reports overlapping_bar on the same pair the plan named

  Every coordinate comes from the bounding boxes of the elements just made, or from the plan's own
  rows (the corner bar's position, the stirrup stations). Nothing here is a code value: the 25 mm
  clearance is the fixture's declaration.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-overlap' -Document $Document

function Get-HzTypeId([string]$Category, [string]$Family, [string]$Name) {
    $t = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "type-$Family" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows |
        Where-Object { $_.is_element_type -and [string]$_.family -eq $Family -and [string]$_.name -eq $Name })
    if ($t.Count -ne 1) { throw "HARNESS: the fixture has no single $Family : $Name type" }
    [long]$t[0].element_id
}
function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzHookName([string]$Pattern) {
    $h = @(Get-HzTypes 'OST_Rebar' | Where-Object { [string]$_.family -eq 'Rebar Hook' -and [string]$_.name -match $Pattern })
    if ($h.Count -ne 1) { throw "HARNESS: $($h.Count) hook types match $Pattern" }
    [string]$h[0].name
}
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }
function Get-HzBox([long]$Id) {
    $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "box-$Id" -Arguments @{
        element_ids = @(, $Id); include_links = $false; include_bounding_box = $true; max_rows = 5 }
    $r = @($q.Result.rows | Where-Object { [long]$_.element_id -eq $Id })
    if ($r.Count -ne 1 -or $null -eq $r[0].bounding_box) { throw "HARNESS: element $Id has no bounding box" }
    $b = $r[0].bounding_box
    [pscustomobject]@{ x0 = [double]$b.min[0]; y0 = [double]$b.min[1]; z0 = [double]$b.min[2]
                       x1 = [double]$b.max[0]; y1 = [double]$b.max[1]; z1 = [double]$b.max[2] }
}
function Get-HzCover([long]$Id) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }
    $row = @(Get-HzPath $hq.Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -ne $c) { return [double]$c }
    $fc = @(@(Get-HzPath $row 'cover', 'faces') | ForEach-Object { Get-HzPath $_ 'cover', 'distance_mm' } | Where-Object { $null -ne $_ })
    [double](@($fc | Sort-Object -Descending)[0])
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }
function Get-HzRuleRows($Rows, [string]$Like) { @($Rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like }) }
function Get-HzRowText($Row) {
    if ($null -eq $Row) { return 'no row' }
    $code = Get-HzProp $Row 'code'
    if (-not $code) { return ([string](Get-HzProp $Row 'rule_id') + ' planned') }
    $ov = @(Get-HzProp $Row 'overlaps') | Where-Object { $_ } | Select-Object -First 1
    ([string](Get-HzProp $Row 'rule_id') + ' ' + $code +
     $(if ($ov) { ' vs ' + [string]$ov.other_rule_id + ' clear=' + [string]$ov.clear_mm + ' ' + [string]$ov.relation } else { '' }) +
     ': ' + (Limit-HzText ([string](Get-HzProp $Row 'why')) 220))
}

<#
  Where the bars of a planned row stand: the first point of its centreline at every position the
  layout builds, from the row the plan published (curve_mm, normal, signed_positions_mm).
#>
function Get-HzRowBars($Row) {
    $c = @(Get-HzProp $Row 'curve_mm')
    $n = Get-HzProp $Row 'normal'
    $lay = Get-HzProp $Row 'layout'
    $s = @(Get-HzProp $lay 'signed_positions_mm')
    $first = (Get-HzProp $lay 'include_first_bar') -ne $false
    $last = (Get-HzProp $lay 'include_last_bar') -ne $false
    $out = @()
    for ($i = 0; $i -lt $s.Count; $i++) {
        if ($s.Count -gt 1 -and (($i -eq 0 -and -not $first) -or ($i -eq $s.Count - 1 -and -not $last))) { continue }
        $out += [pscustomobject]@{
            x = [double]$c[0][0] + [double]$n.x * [double]$s[$i]
            y = [double]$c[0][1] + [double]$n.y * [double]$s[$i]
            z = [double]$c[0][2] + [double]$n.z * [double]$s[$i] }
    }
    $out
}

function New-HzSet([string]$Id, [bool]$Clearance, [double]$Crossing = -1) {
    $s = [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'overlapping_bar, synthetic' }
        bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
        hook_types = @(, @{ id = 'H135'; type_name = $h135 })
    }
    # THE FIXTURE'S declaration, not a code value: 25 mm clear between bars of different rules running alongside.
    if ($Clearance) { $s['clearances'] = [ordered]@{ between_rules_mm = 25 } }
    if ($Clearance -and $Crossing -ge 0) { $s['clearances']['crossing_mm'] = $Crossing }
    $s
}
function New-HzColumnRule([string]$Id, [long]$Column) {
    [ordered]@{ id = $Id; host = @{ element_ids = @(, $Column) }; cover = @{ distance_mm = 40 }
        # A BAR AT THE MIDDLE OF EVERY FACE, not the four corners alone: the column (450 x 600) is wider than the
        # beam (300), so its corner bars stand 92 mm outside the beam and a beam bar at their height and line is
        # steel in air (measured, Revit 2026: bar_partly_outside_host_solid). The middle bar of the face toward
        # the beam stands on the beam's axis.
        layers = @([ordered]@{ face = 'top'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                   [ordered]@{ face = 'bottom'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                   [ordered]@{ face = 'left'; count = 1; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                   [ordered]@{ face = 'right'; count = 1; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        # THE STIRRUPS KNOW THE HOST'S COVER: without it the first one is laid on the column's bottom face, half
        # of it (4.75 mm) out of the concrete (measured, Revit 2024 and 2026, refused bar_partly_outside_host_solid).
        stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
            start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
            zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } }
}
<# A beam bar hosted in the beam and declared into the column: from 100 inside the column's far face to 1500 past its near face. #>
function New-HzBeamBar([string]$Id, [long]$Beam, [long]$Column, $ColumnBox, [double]$Y, [double]$Z) {
    [ordered]@{ id = $Id; host = @{ element_ids = @(, $Beam) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
        curve_mm = @(@(($ColumnBox.x0 + 100.0), $Y, $Z), @(($ColumnBox.x1 + 1500.0), $Y, $Z)); normal = @(0, 1, 0)
        layout = @{ rule = 'single' }; into = @(, $Column) }
}
function Invoke-HzPlan($Set, [string]$Label) {
    (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label $Label -Arguments @{ target_document = $Document; requirement_set = $Set }).Result
}
function Get-HzOverlapFindings($Audit) {
    @(@(Get-HzPath $Audit.Result 'findings') | Where-Object { $_ -and ([string](Get-HzProp $_ 'code') -eq 'overlapping_bar' -or [string](Get-HzProp $_ 'about') -eq 'overlapping_bar') })
}
function Get-HzApplyText($Apply) {
    $ok = $null -ne $Apply.Apply -and $Apply.Apply.Ok
    $verif = @($(if ($Apply.Apply) { @(Get-HzPath $Apply.Apply.Result 'verification') }))
    $notOk = @($verif | Where-Object { $null -ne $_ -and (Get-HzProp $_ 'verified') -ne $true })
    [pscustomobject]@{ Ok = ($ok -and $verif.Count -gt 0 -and $notOk.Count -eq 0); Verified = $verif.Count - $notOk.Count; Count = $verif.Count
        Text = ("applied={0} verified={1}/{2} {3}" -f $ok, ($verif.Count - $notOk.Count), $verif.Count,
            $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 400) }
              elseif (-not $ok) { Limit-HzText $(if ($Apply.Apply) { $Apply.Apply.Text } else { $Apply.Dry.Text }) 400 } else { '' })) }
}

# ---------------------------------------------------------------- fixture
Write-Host "`n== fixture: two columns with a beam each, and a structural wall ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colType = Get-HzTypeId 'OST_StructuralColumns' 'M_Concrete-Rectangular-Column' '450 x 600mm'
$beamType = Get-HzTypeId 'OST_StructuralFraming' 'M_Concrete-Rectangular Beam' '300 x 600mm'
$wallT = @(Get-HzTypes 'OST_Walls' | Where-Object { [string]$_.family -eq 'Basic Wall' -and [string]$_.name -match 'Concrete' -and [string]$_.name -match '(\d+)\s*mm' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 200 } | Select-Object -First 1)
if ($wallT.Count -eq 0) { throw 'HARNESS: no concrete basic wall type of 200 mm or more in the template' }
$h135 = Get-HzHookName '^Stirrup/Tie - 135 deg\.?$'

$XP = 150000.0; $XQ = $XP + 10000.0; $XW = $XP + 20000.0
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'columns' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @($XP, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @($XQ, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }
$cols = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($cols.Count -ne 2) { throw "HARNESS: two columns were not created ($($cols.Count))" }
$bm = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beams' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($XP, 0.0, 3000.0); end = @(($XP + 5000.0), 0.0, 3000.0) },
        @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($XQ, 0.0, 3000.0); end = @(($XQ + 5000.0), 0.0, 3000.0) }) }
$beams = @(@($bm.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($beams.Count -ne 2) { throw "HARNESS: two beams were not created ($($beams.Count))" }
$wall = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'wall' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'wall'; type_id = [long]$wallT[0].element_id; level_id = $lv; start = @($XW, 0.0, 0.0); end = @(($XW + 1800.0), 0.0, 0.0); height = 3000.0; structural = $true; join_rule = 'disallow' }) })
# BOXES ARE bx*: PowerShell variable names ignore case.
$bxCP = Get-HzBox $cols[0]; $bxCQ = Get-HzBox $cols[1]; $bxBP = Get-HzBox $beams[0]; $bxBQ = Get-HzBox $beams[1]; $bxW = Get-HzBox $wall
$wc = Get-HzCover $wall
$run.Fixture['elements'] = @{ column_p = $cols[0]; column_q = $cols[1]; beam_p = $beams[0]; beam_q = $beams[1]; wall = $wall; wall_type = [string]$wallT[0].name; wall_cover = $wc }
$run.Fixture['boxes'] = @{ column_p = $bxCP; column_q = $bxCQ; beam_p = $bxBP; beam_q = $bxBQ; wall = $bxW }

<#
  The column cage's own rows, planned alone: where its corner bars stand and at which heights its
  stirrups are laid. The beam bars below are placed from these numbers, not from a guess.
#>
function Get-HzCage([string]$Id, [long]$Column) {
    $s = New-HzSet "$Id-probe" $false
    $s['section_rules'] = @(, (New-HzColumnRule $Id $Column))
    $rows = @(Get-HzRows (Invoke-HzPlan $s "plan-$Id-alone"))
    $bad = @($rows | Where-Object { Get-HzProp $_ 'code' })
    if ($bad.Count) { throw ("HARNESS: the column cage $Id alone did not plan: " + (Get-HzRowText $bad[0])) }
    $bars = @(Get-HzRuleRows $rows "$Id#h*" | Where-Object { [string](Get-HzProp $_ 'rule_id') -notlike '*#stirrups*' } | ForEach-Object { Get-HzRowBars $_ })
    $stations = @(Get-HzRuleRows $rows "$Id#h*#stirrups*" | ForEach-Object { Get-HzRowBars $_ } | ForEach-Object { $_.z } | Sort-Object)
    if ($bars.Count -lt 4 -or $stations.Count -lt 4) { throw "HARNESS: the cage $Id planned $($bars.Count) bars and $($stations.Count) stirrup stations" }
    # The bar on the beam's side (largest x) among those on the column's middle line in y - the middle bar of
    # that face, which a beam bar along x at its y runs straight through, inside the beam.
    $bx = Get-HzBox $Column; $yc = ($bx.y0 + $bx.y1) / 2
    $corner = @($bars | Where-Object { [Math]::Abs([double]$_.y - $yc) -lt 50 } | Sort-Object -Property x -Descending)[0]
    if ($null -eq $corner) { throw "HARNESS: the cage $Id has no bar on the column's middle line in y" }
    [pscustomobject]@{ Corner = $corner; Stations = $stations }
}
<# Halfway between the two stirrup stations nearest the beam's mid-depth: as far from both as the cage allows. #>
function Get-HzBetween($Stations, $BeamBox) {
    $mid = ($BeamBox.z0 + $BeamBox.z1) / 2
    $best = $null
    for ($i = 0; $i + 1 -lt $Stations.Count; $i++) {
        $z = ([double]$Stations[$i] + [double]$Stations[$i + 1]) / 2
        if ($z -lt $BeamBox.z0 + 80 -or $z -gt $BeamBox.z1 - 80) { continue }
        if ($null -eq $best -or [Math]::Abs($z - $mid) -lt [Math]::Abs($best - $mid)) { $best = $z }
    }
    if ($null -eq $best) { throw 'HARNESS: no two stirrup stations of the column lie inside the beam depth' }
    $best
}

$cageP = Get-HzCage 'COLP' $cols[0]
$cageQ = Get-HzCage 'COLQ' $cols[1]
$zP = Get-HzBetween $cageP.Stations $bxBP
$zQ = Get-HzBetween $cageQ.Stations $bxBQ
$run.Fixture['cages'] = @{ p_corner = $cageP.Corner; q_corner = $cageQ.Corner; p_bar_z = $zP; q_bar_z = $zQ }

# ---------------------------------------------------------------- O1 / O2: the joint
Write-Host "`n== joint: a beam bar into a caged column ==" -ForegroundColor Cyan
$o1 = New-HzSet 'ov-joint-clash' $true
$o1['section_rules'] = @(, (New-HzColumnRule 'COLP' $cols[0]))
$o1['reinforcement_rules'] = @(, (New-HzBeamBar 'BEAMP' $beams[0] $cols[0] $bxCP $cageP.Corner.y $zP))
$p1 = Invoke-HzPlan $o1 'plan-o1'
$rows1 = @(Get-HzRows $p1)
$beam1 = @(Get-HzRuleRows $rows1 'BEAMP')[0]
$hitBy = @(@(Get-HzProp $beam1 'overlaps') | Where-Object { $_ })
$colHit = @($rows1 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'bars_intersect' -and [string](Get-HzProp $_ 'rule_id') -like 'COLP#h*' })
Add-HzProbe -Run $run -Id 'O1' -Name 'joint: a beam bar declared through the column face bar is refused by the plan, both sets, before anything is written' `
    -Expected "BEAMP bars_intersect naming a COLP layer, and that COLP row bars_intersect naming BEAMP; overlap_check.checked = declared_clearance" `
    -Observed ((Get-HzRowText $beam1) + ' | ' + $(if ($colHit.Count) { Get-HzRowText $colHit[0] } else { 'no COLP row refused' }) +
               ' | checked=' + [string](Get-HzPath $p1 'overlap_check', 'checked')) `
    -Ok ([string](Get-HzProp $beam1 'code') -eq 'bars_intersect' -and $hitBy.Count -ge 1 -and [string]$hitBy[0].other_rule_id -like 'COLP#h*' -and
         [double]$hitBy[0].clear_mm -lt 0 -and $colHit.Count -ge 1 -and
         @(@(Get-HzProp $colHit[0] 'overlaps') | Where-Object { $_ -and [string]$_.other_rule_id -eq 'BEAMP' }).Count -ge 1 -and
         [string](Get-HzPath $p1 'overlap_check', 'checked') -eq 'declared_clearance') `
    -Evidence @{ beam = $beam1; column = $colHit; overlap_check = (Get-HzProp $p1 'overlap_check') }

$o2 = New-HzSet 'ov-joint-clear' $true
$o2['section_rules'] = @(, (New-HzColumnRule 'COLP' $cols[0]))
# 90 mm off the column's middle line in y, between two stirrup stations: clear of the face bars (the middle
# one stands on that line, the corners 242 mm off it) and of the stirrups, and still inside the 300 mm beam.
$o2['reinforcement_rules'] = @(, (New-HzBeamBar 'BEAMP' $beams[0] $cols[0] $bxCP (($bxCP.y0 + $bxCP.y1) / 2 + 90.0) $zP))
$p2 = Invoke-HzPlan $o2 'plan-o2'
$rows2 = @(Get-HzRows $p2)
$bad2 = @($rows2 | Where-Object { Get-HzProp $_ 'code' })
$apply2 = $null; $a2 = $null; $audit2 = $null; $f2 = @()
if ($bad2.Count -eq 0) {
    $apply2 = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-o2' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $o2; transaction_name = 'HZ rebar overlap joint clear' }
    $a2 = Get-HzApplyText $apply2
    $audit2 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-o2' -Arguments @{ target_document = $Document; requirement_set = $o2 }
    $f2 = @(Get-HzOverlapFindings $audit2)
}
Add-HzProbe -Run $run -Id 'O2' -Name 'joint: the beam bar clear of the cage, under a declared 25 mm clearance, plans, applies and audits with no overlapping_bar' `
    -Expected 'no row refused; every set verified; audit: no overlapping_bar finding, overlaps.checked = declared_clearance, nothing unreadable' `
    -Observed $(if ($bad2.Count) { 'plan refused: ' + (Get-HzRowText $bad2[0]) } else {
        $a2.Text + ' | audit overlapping_bar=' + $f2.Count + ' checked=' + [string](Get-HzPath $audit2.Result 'overlaps', 'checked') +
        ' sets=' + [string](Get-HzPath $audit2.Result 'overlaps', 'sets_compared') +
        $(if ($f2.Count) { ' first=' + [string]$f2[0].rule_id + ' vs ' + [string](Get-HzProp $f2[0] 'other_rule_id') + ' clear=' + [string](Get-HzProp $f2[0] 'clear_mm') } else { '' }) }) `
    -Ok ($bad2.Count -eq 0 -and $a2.Ok -and $f2.Count -eq 0 -and [string](Get-HzPath $audit2.Result 'overlaps', 'checked') -eq 'declared_clearance' -and
         [int](Get-HzPath $audit2.Result 'overlaps', 'sets_compared') -ge 2 -and @(@(Get-HzPath $audit2.Result 'overlaps', 'unreadable') | Where-Object { $_ }).Count -eq 0) `
    -Evidence @{ plan = (Get-HzProp $p2 'overlap_check'); audit_overlaps = $(if ($audit2) { Get-HzPath $audit2.Result 'overlaps' }); findings = $f2 }

# ---------------------------------------------------------------- O3 / O4: the wall
Write-Host "`n== wall: a mat against a boundary element ==" -ForegroundColor Cyan
function New-HzBoundary {
    [ordered]@{ id = 'WB'; host = @{ element_ids = @(, $wall) }; end = 'start'; length_mm = 400; cover = @{ source = 'host' }
        layers = @(, @{ face = 'perimeter'; count = 4; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
            start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
            zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } }
}
function New-HzMat([double]$HOffset, [double]$HEnd, [double]$HSide, [double]$VOffset) {
    # A HOSTED BAR'S END COVER IS THE HOST'S (Revit ignores another, and the mat refuses it by name): the
    # horizontals are kept HEnd from the wall's ends with a run window, the end cover staying the wall's.
    $h = [ordered]@{ name = 'h'; direction = @(1, 0, 0); bar_type = 'N10'; offset_from_face_mm = $HOffset; end_cover_mm = $wc; side_cover_mm = $HSide
        allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 250 } }
    if ($HEnd -gt $wc) { $h['run'] = @{ from_mm = $HEnd; to_mm = ($bxW.x1 - $bxW.x0) - $HEnd } }
    [ordered]@{ id = 'WM'; host = @{ element_ids = @(, $wall) }; face_normal = @(0, 1, 0)
        components = @(
            $h,
            # The verticals are kept out of the 400 mm boundary region in both sets: only the horizontals change.
            [ordered]@{ name = 'v'; direction = @(0, 0, 1); bar_type = 'N16'; offset_from_face_mm = $VOffset; end_cover_mm = $wc; side_cover_mm = 480
                allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }
}
# THE MEASURED CASE: horizontals at cover + 8, end to end (end cover = the wall's), so they run through the
# boundary region - across its corner bars and its stirrups. No clearances declared: intersections only.
$o3 = New-HzSet 'ov-wall-clash' $false
$o3['wall_boundary_rules'] = @(, (New-HzBoundary))
$o3['mat_rules'] = @(, (New-HzMat ($wc + 8) $wc ($wc + 8) ($wc + 30)))
$p3 = Invoke-HzPlan $o3 'plan-o3'
$rows3 = @(Get-HzRows $p3)
$h3 = @(Get-HzRuleRows $rows3 'WM#h*')
$h3Hit = @($h3 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'bars_intersect' })
$h3Names = @($h3Hit | ForEach-Object { @(Get-HzProp $_ 'overlaps') } | Where-Object { $_ } | ForEach-Object { [string]$_.other_rule_id } | Select-Object -Unique)
Add-HzProbe -Run $run -Id 'O3' -Name 'wall: a mat run through the boundary element (the measured case), no clearances declared, is refused as bars_intersect' `
    -Expected "WM#h refused bars_intersect naming WB rows; overlap_check.checked = intersections_only, and checked_means says only intersections were looked for" `
    -Observed ($(if ($h3.Count) { Get-HzRowText $h3[0] } else { 'no WM#h row' }) + ' | against=' + ($h3Names -join ',') +
               ' | checked=' + [string](Get-HzPath $p3 'overlap_check', 'checked')) `
    -Ok ($h3Hit.Count -ge 1 -and @($h3Names | Where-Object { $_ -like 'WB#h*' }).Count -ge 1 -and
         [string](Get-HzPath $p3 'overlap_check', 'checked') -eq 'intersections_only' -and
         [string](Get-HzPath $p3 'overlap_check', 'checked_means') -match 'only bars that pass through') `
    -Evidence @{ rows = $h3; overlap_check = (Get-HzProp $p3 'overlap_check') }

# Kept out of the region: the horizontals end 480 mm from the wall's ends, past the 400 mm boundary.
$o4 = New-HzSet 'ov-wall-clear' $true
$o4['wall_boundary_rules'] = @(, (New-HzBoundary))
$o4['mat_rules'] = @(, (New-HzMat ($wc + 30) 480 ($wc + 30) ($wc + 8)))
$p4 = Invoke-HzPlan $o4 'plan-o4'
$rows4 = @(Get-HzRows $p4)
$bad4 = @($rows4 | Where-Object { Get-HzProp $_ 'code' })
$a4 = $null; $audit4 = $null; $f4 = @()
if ($bad4.Count -eq 0) {
    $apply4 = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-o4' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $o4; transaction_name = 'HZ rebar overlap wall clear' }
    $a4 = Get-HzApplyText $apply4
    $audit4 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-o4' -Arguments @{ target_document = $Document; requirement_set = $o4 }
    $f4 = @(Get-HzOverlapFindings $audit4)
}
Add-HzProbe -Run $run -Id 'O4' -Name 'wall: the mat kept out of the boundary element, under a declared 25 mm clearance, plans, applies and audits with no overlapping_bar' `
    -Expected 'no row refused; every set verified; audit: no overlapping_bar finding, overlaps.checked = declared_clearance, nothing unreadable' `
    -Observed $(if ($bad4.Count) { 'plan refused: ' + (Get-HzRowText $bad4[0]) } else {
        $a4.Text + ' | audit overlapping_bar=' + $f4.Count + ' checked=' + [string](Get-HzPath $audit4.Result 'overlaps', 'checked') +
        $(if ($f4.Count) { ' first=' + [string]$f4[0].rule_id + ' vs ' + [string](Get-HzProp $f4[0] 'other_rule_id') + ' clear=' + [string](Get-HzProp $f4[0] 'clear_mm') } else { '' }) }) `
    -Ok ($bad4.Count -eq 0 -and $a4.Ok -and $f4.Count -eq 0 -and [string](Get-HzPath $audit4.Result 'overlaps', 'checked') -eq 'declared_clearance' -and
         @(@(Get-HzPath $audit4.Result 'overlaps', 'unreadable') | Where-Object { $_ }).Count -eq 0) `
    -Evidence @{ plan = (Get-HzProp $p4 'overlap_check'); audit_overlaps = $(if ($audit4) { Get-HzPath $audit4.Result 'overlaps' }); findings = $f4 }

# ---------------------------------------------------------------- O5 / O6: plan and audit agree
Write-Host "`n== joint, second pair: the plan and the audit name the same pair ==" -ForegroundColor Cyan
$beamQ = New-HzBeamBar 'BEAMQ' $beams[1] $cols[1] $bxCQ $cageQ.Corner.y $zQ
# CROSSING_MM 5 HERE: if Revit pushes the beam bar out of the corner bar until the two touch, contact is
# legal under crossing_mm 0 and the audit would rightly say nothing. Five millimetres makes the pushed bar a
# finding too, so the audit answers whether Revit moved it or not.
$whole = New-HzSet 'ov-joint-whole' $true 5
$whole['section_rules'] = @(, (New-HzColumnRule 'COLQ' $cols[1]))
$whole['reinforcement_rules'] = @(, $beamQ)
$p5 = Invoke-HzPlan $whole 'plan-o5'
$beam5 = @(Get-HzRuleRows (Get-HzRows $p5) 'BEAMQ')[0]
$planPair = @(@(Get-HzProp $beam5 'overlaps') | Where-Object { $_ } | ForEach-Object { [string]$_.other_rule_id })
Add-HzProbe -Run $run -Id 'O5' -Name 'joint, second pair: the cage and a beam bar through its face bar, as one set, are refused as a pair' `
    -Expected 'BEAMQ bars_intersect naming a COLQ layer' -Observed (Get-HzRowText $beam5) `
    -Ok ([string](Get-HzProp $beam5 'code') -eq 'bars_intersect' -and @($planPair | Where-Object { $_ -like 'COLQ#h*' }).Count -ge 1) `
    -Evidence @{ row = $beam5 }

# Built as two sets: each plan sees only its own bars (overlap_with_unattributed_bar), so both build.
$half1 = New-HzSet 'ov-joint-half-cage' $true 5; $half1['section_rules'] = $whole['section_rules']
$half2 = New-HzSet 'ov-joint-half-beam' $true 5; $half2['reinforcement_rules'] = $whole['reinforcement_rules']
$ap1 = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-half-cage' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $half1; transaction_name = 'HZ rebar overlap half cage' }
# The beam bar's own verification may fail here: Revit may move a bar laid through another. It is committed
# either way, and that is what the audit below is for.
$ap2 = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-half-beam' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $half2; transaction_name = 'HZ rebar overlap half beam' }
$built = ($null -ne $ap1.Apply -and $ap1.Apply.Ok) -and ($null -ne $ap2.Apply -and @(Get-HzPath $ap2.Apply.Result 'verification').Count -ge 1)
$audit6 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-o6' -Arguments @{ target_document = $Document; requirement_set = $whole }
$f6 = @(Get-HzOverlapFindings $audit6 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'overlapping_bar' })
$same = @($f6 | Where-Object {
    $ids = @([string](Get-HzProp $_ 'rule_id'), [string](Get-HzProp $_ 'other_rule_id'))
    ($ids -contains 'BEAMQ') -and @($ids | Where-Object { $planPair -contains $_ }).Count -ge 1 })
Add-HzProbe -Run $run -Id 'O6' -Name 'the same two rules built as separate sets: the audit of the whole reports overlapping_bar on the pair the plan named' `
    -Expected ('both halves committed; overlapping_bar between BEAMQ and ' + ($planPair -join ' or ') + ' with both bar ids, the clear distance and the point') `
    -Observed ("built={0} cage: {1} | beam: {2} | overlapping_bar={3} {4}" -f $built, (Get-HzApplyText $ap1).Text, (Get-HzApplyText $ap2).Text, $f6.Count,
        $(if ($f6.Count) { [string]$f6[0].rule_id + ' vs ' + [string](Get-HzProp $f6[0] 'other_rule_id') + ' clear=' + [string](Get-HzProp $f6[0] 'clear_mm') + ' ' + [string](Get-HzProp $f6[0] 'kind') } else { '' })) `
    -Ok ($built -and $same.Count -ge 1 -and $null -ne (Get-HzProp $same[0] 'other_rebar_id') -and $null -ne (Get-HzProp $same[0] 'point_mm')) `
    -Evidence @{ findings = $f6; plan_pair = $planPair; audit_overlaps = (Get-HzPath $audit6.Result 'overlaps') }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
