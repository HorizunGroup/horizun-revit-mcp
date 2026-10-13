#Requires -Version 5.1
<#
  ACERO 3.0, ETAPA 1c, crossing_order, LIVE: the DECLARED order in which bars of different rules pass
  each other, on a DISPOSABLE fixture (Autodesk metric template: M_Concrete-Rectangular-Column
  450 x 600mm, M_Concrete-Rectangular Beam 300 x 600mm, a structural floor of 150 mm or more,
  16M/10M).

  MEASURED BEFORE THIS LOT: Revit pushes a bar that crosses another until the two touch, and says
  nothing - so the plan refuses both sets of any two whose bars pass through each other
  (bars_intersect). An anonymized regression case exposed crossings between beams at a joint, a mat through
  beam bars, the top and bottom U bars of a beam with their legs in one plane. Which bar passes over is
  a detailing decision, now declared.

    N1  joint: the bottom and top bars of two beams crossing at a column, at one level, WITHOUT a
        crossing order: the plan refuses all four sets (bars_intersect)
    N2  the same with a family order (X beams stay, Y beams move toward their host's core by one
        diameter): planned with the Y bars displaced (bottom up, top down, by the model diameter -
        each row's curve against the declared one, computed here), applied and verified, the audit
        clean and listing both displacements, each measured on the model within 1 mm
    U1  one beam, top U and bottom U bars whose legs coincide: refused without an order
    U2  the bottom U's legs nest inside the top U's (legs, one diameter): planned, applied, audited,
        the legs measured on the model within 1 mm of the declared station plus one diameter
    P1  a two-way bottom mat (one mat rule per direction) laid at one depth, and a band bar through it:
        refused without an order
    P2  two orders on the mat's Y layer - over the X layer by contact, over the band bar by 16 mm -
        move it ONCE, up, by the larger; planned, applied, audited, the bars re-read at the declared
        depth plus 16 within 1 mm
    P3  the Y layer sent DOWN instead, toward the soffit: refused crossing_displacement_breaks_cover,
        never squeezed

  Every coordinate comes from the bounding boxes of the elements just made and the covers the model
  reports. Nothing here is a code value: the orders and offsets are the fixture's declaration.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-crossing' -Document $Document

function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzTypeId([string]$Category, [string]$Family, [string]$Name) {
    $t = @(Get-HzTypes $Category | Where-Object { [string]$_.family -eq $Family -and [string]$_.name -eq $Name })
    if ($t.Count -ne 1) { throw "HARNESS: the fixture has no single $Family : $Name type" }
    [long]$t[0].element_id
}
function New-Hz([string]$Label, $Elements) {
    @(@((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label $Label -Arguments @{ target_document = $Document; units = 'mm'; elements = $Elements }).Apply.Result.rows) |
        ForEach-Object { [long](Get-HzProp $_ 'element_id') })
}
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
    $row = @(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }).Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -eq $c) { throw "HARNESS: host $Id has no common cover; this probe declares positions from it" }
    [double]$c
}
function Get-HzRows($Result) { @(@(Get-HzPath $Result 'reinforcement') | Where-Object { $_ }) }
function Get-HzRuleRows($Rows, [string]$Like) { @($Rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like }) }
function Get-HzRowText($Row) {
    if ($null -eq $Row) { return 'no row' }
    $code = Get-HzProp $Row 'code'
    if (-not $code) { return ([string](Get-HzProp $Row 'rule_id') + ' planned') }
    $ov = @(@(Get-HzProp $Row 'overlaps') | Where-Object { $_ }) | Select-Object -First 1
    ([string](Get-HzProp $Row 'rule_id') + ' ' + $code +
     $(if ($ov) { ' vs ' + [string]$ov.other_rule_id + ' clear=' + [string]$ov.clear_mm } else { '' }) +
     ': ' + (Limit-HzText ([string](Get-HzProp $Row 'why')) 260))
}
function Invoke-HzPlan($Set, [string]$Label) {
    (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label $Label -Arguments @{ target_document = $Document; requirement_set = $Set }).Result
}
function Get-HzApplyText($Apply) {
    $ok = $null -ne $Apply.Apply -and $Apply.Apply.Ok
    $verif = @($(if ($Apply.Apply) { @(Get-HzPath $Apply.Apply.Result 'verification') | Where-Object { $_ } }))
    $notOk = @($verif | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    [pscustomobject]@{ Ok = ($ok -and $verif.Count -gt 0 -and $notOk.Count -eq 0); Verified = $verif.Count - $notOk.Count; Count = $verif.Count
        Text = ("applied={0} verified={1}/{2} {3}" -f $ok, ($verif.Count - $notOk.Count), $verif.Count,
            $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 400) }
              elseif (-not $ok) { Limit-HzText $(if ($Apply.Apply) { $Apply.Apply.Text } else { $Apply.Dry.Text }) 400 } else { '' })) }
}
function Get-HzBadFindings($Audit) {
    # The info notes every bar set carries by design (bends Revit draws) are not about crossings.
    $accepted = @('hook_corner_opening', 'hook_end_extension', 'hook_corner_seat')
    @(@(Get-HzPath $Audit.Result 'findings') | Where-Object { $_ -and ([string](Get-HzProp $_ 'severity') -ne 'info' -or [string](Get-HzProp $_ 'code') -notin $accepted) })
}
<# The bars a host carries, re-read from the model: each standing bar's hook-free centreline at its own position. #>
function Get-HzBars([long]$HostId) {
    $rows = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "bars-$HostId" -Arguments @{
        mode = 'rebar'; host_id = $HostId; include_bar_positions = $true; max_rows = 500 }).Result 'rows') | Where-Object { $_ })
    foreach ($r in $rows) {
        $pts = @(@(Get-HzPath $r 'geometry', 'centreline_points_as_declared_mm') | Where-Object { $_ })
        foreach ($p in @(@(Get-HzProp $r 'bar_positions') | Where-Object { $_ })) {
            if ((Get-HzProp $p 'exists') -eq $false) { continue }
            $o = Get-HzProp $p 'offset_from_first_bar_mm'
            [pscustomobject]@{ SetId = [long]$r.id
                Points = @($pts | ForEach-Object { , @(([double]$_[0] + [double]$o.x), ([double]$_[1] + [double]$o.y), ([double]$_[2] + [double]$o.z)) }) }
        }
    }
}
<# The worst vertex-to-vertex distance between two polylines of as many vertices, in either direction. #>
function Get-HzWorst($A, $B) {
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
<# The worst distance from each expected polyline to the nearest model bar, over all expected polylines. #>
function Get-HzWorstOf($Expected, $Model) {
    if (@($Model).Count -eq 0) { return [double]::PositiveInfinity }
    $w = 0.0
    foreach ($e in $Expected) {
        $m = (@($Model) | ForEach-Object { Get-HzWorst $e $_.Points } | Measure-Object -Minimum).Minimum
        if ($m -gt $w) { $w = $m }
    }
    $w
}
function Move-HzPoly($Poly, [double]$Dx, [double]$Dy, [double]$Dz) { , @($Poly | ForEach-Object { , @(([double]$_[0] + $Dx), ([double]$_[1] + $Dy), ([double]$_[2] + $Dz)) }) }
function New-HzSet([string]$Id) {
    [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'crossing_order, synthetic' }
        bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' }) }
}
function Get-HzDisp($Payload) { @(@(Get-HzPath $Payload 'crossing_order', 'displacements') | Where-Object { $_ }) }
function Get-HzMeasured($Audit) { @(@(Get-HzPath $Audit.Result 'crossing_displacements') | Where-Object { $_ }) }
function Get-HzOverlaps($Audit) { @(@(Get-HzPath $Audit.Result 'findings') | Where-Object { $_ -and [string](Get-HzProp $_ 'code') -eq 'overlapping_bar' }) }
<#
  Plan, apply, audit one set that declares a crossing order. The plan must refuse nothing; the apply must verify
  every set; the audit must find nothing but the accepted notes, no overlapping_bar, and list every displacement
  with agrees = true and both ends within 1 mm of the displacement.
#>
function Invoke-HzCrossed($Set, [string]$Label) {
    $plan = Invoke-HzPlan $Set "plan-$Label"
    $rows = @(Get-HzRows $plan)
    $bad = @($rows | Where-Object { Get-HzProp $_ 'code' })
    $out = [pscustomobject]@{ Plan = $plan; Rows = $rows; Bad = $bad; Apply = $null; ApplyText = $null; Audit = $null; Findings = @(); Overlaps = @(); Measured = @() }
    if ($bad.Count) { return $out }
    $ap = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label "apply-$Label" -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $Set; transaction_name = "HZ crossing $Label" }
    $out.Apply = $ap; $out.ApplyText = Get-HzApplyText $ap
    $out.Audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label "audit-$Label" -Arguments @{ target_document = $Document; requirement_set = $Set }
    $out.Findings = @(Get-HzBadFindings $out.Audit)
    $out.Overlaps = @(Get-HzOverlaps $out.Audit)
    $out.Measured = @(Get-HzMeasured $out.Audit)
    $out
}
function Test-HzMeasured($M, [double]$Want, [double]$Tol = 1.0) {
    $s = Get-HzProp $M 'measured_start_mm'; $e = Get-HzProp $M 'measured_end_mm'
    (Get-HzProp $M 'agrees') -eq $true -and $null -ne $s -and $null -ne $e -and
        [Math]::Abs([double]$s - $Want) -le $Tol -and [Math]::Abs([double]$e - $Want) -le $Tol
}
function Get-HzMeasuredText($Ms) {
    (@($Ms) | ForEach-Object { '{0} moved={1} start={2} end={3} agrees={4}' -f (Get-HzProp $_ 'rule_id'), (Get-HzProp $_ 'moved_by_mm'),
        (Get-HzProp $_ 'measured_start_mm'), (Get-HzProp $_ 'measured_end_mm'), (Get-HzProp $_ 'agrees') }) -join '; '
}
<# A planned row's curve_mm against an expected polyline, vertex by vertex (worst). #>
function Get-HzRowDev($Row, $Expected) { Get-HzWorst $Expected @(@(Get-HzProp $Row 'curve_mm') | ForEach-Object { , @([double]$_[0], [double]$_[1], [double]$_[2]) }) }

# =====================================================================  FIXTURE
Write-Host "`n== fixture: a joint of two beams at a column, a beam for U bars, a slab ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colType = Get-HzTypeId 'OST_StructuralColumns' 'M_Concrete-Rectangular-Column' '450 x 600mm'
$beamType = Get-HzTypeId 'OST_StructuralFraming' 'M_Concrete-Rectangular Beam' '300 x 600mm'
$floorT = @(Get-HzTypes 'OST_Floors' | Where-Object { [string]$_.name -match '(\d+)\s*mm' -and [string]$_.name -notmatch 'Deck|Metal|Steel|Wood|Timber' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 150 } | Select-Object -First 1)
if ($floorT.Count -eq 0) { throw 'HARNESS: no floor type of 150 mm or more in the template' }

$XN = 170000.0; $XU = $XN + 20000.0; $XS = $XN + 40000.0
$col = (New-Hz 'column' @(, @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @($XN, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }))[0]
$bms = New-Hz 'beams' @(
    @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($XN, 0.0, 3000.0); end = @(($XN + 5000.0), 0.0, 3000.0) },
    @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($XN, 0.0, 3000.0); end = @($XN, 5000.0, 3000.0) },
    @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($XU, 0.0, 3000.0); end = @(($XU + 5000.0), 0.0, 3000.0) })
if ($bms.Count -ne 3) { throw "HARNESS: three beams were not created ($($bms.Count))" }
$beamX = $bms[0]; $beamY = $bms[1]; $beamU = $bms[2]
$slab = (New-Hz 'slab' @(, @{ kind = 'floor'; type_id = [long]$floorT[0].element_id; level_id = $lv; structural = $true
    profile = @(, @(@($XS, 0.0, 0.0), @(($XS + 4000), 0.0, 0.0), @(($XS + 4000), 3000.0, 0.0), @($XS, 3000.0, 0.0))) }))[0]
# BOXES ARE bx*: PowerShell variable names ignore case.
$bxC = Get-HzBox $col; $bxX = Get-HzBox $beamX; $bxY = Get-HzBox $beamY; $bxU = Get-HzBox $beamU; $bxS = Get-HzBox $slab
$cvX = Get-HzCover $beamX; $cvY = Get-HzCover $beamY; $cvU = Get-HzCover $beamU; $cvS = Get-HzCover $slab
$run.Fixture['elements'] = @{ column = $col; beam_x = $beamX; beam_y = $beamY; beam_u = $beamU; slab = $slab; floor_type = [string]$floorT[0].name }
$run.Fixture['boxes'] = @{ column = $bxC; beam_x = $bxX; beam_y = $bxY; beam_u = $bxU; slab = $bxS }
$run.Fixture['covers'] = @{ beam_x = $cvX; beam_y = $cvY; beam_u = $cvU; slab = $cvS }

# =====================================================================  N: the joint
Write-Host "`n== joint: two beams crossing at a column ==" -ForegroundColor Cyan
# Each beam's bottom and top bars at ONE level in both beams (cover + 30 from the soffit and the top), run into
# the column from 100 inside its far face: X along x on beam X's axis, Y along y on beam Y's axis. They cross
# inside the column.
$yX = ($bxX.y0 + $bxX.y1) / 2; $xY = ($bxY.x0 + $bxY.x1) / 2
$zBot = [Math]::Max($bxX.z0, $bxY.z0) + [Math]::Max($cvX, $cvY) + 30; $zTop = [Math]::Min($bxX.z1, $bxY.z1) - [Math]::Max($cvX, $cvY) - 30
$N = @{
    'NX-bot' = @{ host = $beamX; pts = @(@(($bxC.x0 + 100.0), $yX, $zBot), @(($bxC.x1 + 1500.0), $yX, $zBot)); n = @(0, 1, 0) }
    'NX-top' = @{ host = $beamX; pts = @(@(($bxC.x0 + 100.0), $yX, $zTop), @(($bxC.x1 + 1500.0), $yX, $zTop)); n = @(0, 1, 0) }
    'NY-bot' = @{ host = $beamY; pts = @(@($xY, ($bxC.y0 + 100.0), $zBot), @($xY, ($bxC.y1 + 1500.0), $zBot)); n = @(1, 0, 0) }
    'NY-top' = @{ host = $beamY; pts = @(@($xY, ($bxC.y0 + 100.0), $zTop), @($xY, ($bxC.y1 + 1500.0), $zTop)); n = @(1, 0, 0) }
}
function New-HzJoint([bool]$Ordered) {
    $s = New-HzSet $(if ($Ordered) { 'cx-joint' } else { 'cx-joint-none' })
    $s['reinforcement_rules'] = @('NX-bot', 'NX-top', 'NY-bot', 'NY-top' | ForEach-Object {
        [ordered]@{ id = $_; host = @{ element_ids = @(, $N[$_].host) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
            curve_mm = $N[$_].pts; normal = $N[$_].n; layout = @{ rule = 'single' }; into = @(, $col) } })
    # THE FIXTURE'S DECLARATION: the X family passes, the Y family gives way toward its own core by one diameter.
    if ($Ordered) {
        $s['crossing_order'] = [ordered]@{ offset = 'diameter'
            orders = @(, [ordered]@{ id = 'x-over-y'; stays = @('NX-*'); moves = @('NY-*'); move = 'translate'; direction = 'toward_host_core' }) }
    }
    $s
}
$pN1 = Invoke-HzPlan (New-HzJoint $false) 'plan-n1'
$rN1 = @(Get-HzRows $pN1)
$hitN1 = @($rN1 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'bars_intersect' })
Add-HzProbe -Run $run -Id 'N1' -Name 'joint: two beams crossing at one level, no crossing order, are refused (bars_intersect, all four sets)' `
    -Expected 'NX-bot, NX-top, NY-bot, NY-top all bars_intersect; crossing_order.declared = false' `
    -Observed ((@($rN1 | ForEach-Object { Get-HzRowText $_ }) -join ' | ') + ' | crossing_order.declared=' + [string](Get-HzPath $pN1 'crossing_order', 'declared')) `
    -Ok ($rN1.Count -eq 4 -and $hitN1.Count -eq 4 -and (Get-HzPath $pN1 'crossing_order', 'declared') -eq $false) `
    -Evidence @{ rows = $rN1 }

$cN = Invoke-HzCrossed (New-HzJoint $true) 'n2'
$dN = @(Get-HzDisp $cN.Plan)
# INDEPENDENT: the Y bars where the order puts them - the bottom one up by its model diameter, the top one down.
$diaN = [double](Get-HzPath @(Get-HzRuleRows $cN.Rows 'NY-bot')[0] 'bar_type', 'model_diameter_mm')
$expNYb = Move-HzPoly $N['NY-bot'].pts 0 0 $diaN
$expNYt = Move-HzPoly $N['NY-top'].pts 0 0 (-$diaN)
$devN = @(
    (Get-HzRowDev @(Get-HzRuleRows $cN.Rows 'NY-bot')[0] $expNYb),
    (Get-HzRowDev @(Get-HzRuleRows $cN.Rows 'NY-top')[0] $expNYt),
    (Get-HzRowDev @(Get-HzRuleRows $cN.Rows 'NX-bot')[0] $N['NX-bot'].pts),
    (Get-HzRowDev @(Get-HzRuleRows $cN.Rows 'NX-top')[0] $N['NX-top'].pts))
$modelN = $(if ($cN.Audit) { Get-HzWorstOf @($expNYb, $expNYt) @(Get-HzBars $beamY) } else { [double]::PositiveInfinity })
$mN = @($cN.Measured)
Add-HzProbe -Run $run -Id 'N2' -Name 'joint: a family order moves the Y bars toward their core by one diameter; planned, applied, audited, measured' `
    -Expected ('no row refused; displacements NY-bot (0,0,1) and NY-top (0,0,-1) moved_by = model diameter {0}; rows within 0.01 mm of the declared curve moved by hand; every set verified; audit clean, no overlapping_bar; 2 crossing_displacements, each end within 1 mm of {0}; the Y bars re-read within 1 mm' -f $diaN) `
    -Observed $(if ($cN.Bad.Count) { 'plan refused: ' + (Get-HzRowText $cN.Bad[0]) } else {
        ('displacements={0} [{1}] row_dev_mm={2} | {3} | findings={4} overlapping_bar={5} | measured: {6} | model_worst_mm={7:N3}' -f $dN.Count,
            ((@($dN | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + ((@(Get-HzProp $_ 'direction') | ForEach-Object { [string]$_ }) -join ',') + ' by ' + [string](Get-HzProp $_ 'moved_by_mm') })) -join '; '),
            (($devN | ForEach-Object { '{0:N3}' -f $_ }) -join ','), $cN.ApplyText.Text, $cN.Findings.Count, $cN.Overlaps.Count, (Get-HzMeasuredText $mN), $modelN) }) `
    -Ok ($cN.Bad.Count -eq 0 -and $dN.Count -eq 2 -and @($devN | Where-Object { $_ -gt 0.01 }).Count -eq 0 -and $cN.ApplyText.Ok -and
         $cN.Findings.Count -eq 0 -and $cN.Overlaps.Count -eq 0 -and $mN.Count -eq 2 -and @($mN | Where-Object { -not (Test-HzMeasured $_ $diaN) }).Count -eq 0 -and $modelN -le 1.0 -and
         @($dN | Where-Object { [Math]::Abs([double](Get-HzProp $_ 'moved_by_mm') - $diaN) -gt 0.001 }).Count -eq 0) `
    -Evidence @{ displacements = $dN; measured = $mN; row_dev = $devN; findings = $cN.Findings; model_worst_mm = $modelN }

# =====================================================================  U: alternate legs
Write-Host "`n== U bars: legs in one plane ==" -ForegroundColor Cyan
# The bar's centre: cover, a stirrup's room (9.5), the radius (7.95). 300 mm legs, three bars across the width.
$uIn = $cvU + 9.5 + 7.95
$uA = $bxU.x0 + $cvU + 7.95; $uB = $bxU.x1 - $cvU - 7.95
$uY = $bxU.y0 + $uIn; $uSpread = ($bxU.y1 - $bxU.y0) - 2 * $uIn
$zUt = $bxU.z1 - $uIn; $zUb = $bxU.z0 + $uIn
$uTop = @(@($uA, $uY, ($zUt - 300)), @($uA, $uY, $zUt), @($uB, $uY, $zUt), @($uB, $uY, ($zUt - 300)))
$uBot = @(@($uA, $uY, ($zUb + 300)), @($uA, $uY, $zUb), @($uB, $uY, $zUb), @($uB, $uY, ($zUb + 300)))
function New-HzU([bool]$Ordered) {
    $s = New-HzSet $(if ($Ordered) { 'cx-u' } else { 'cx-u-none' })
    $s['reinforcement_rules'] = @(
        [ordered]@{ id = 'U-SUP'; host = @{ element_ids = @(, $beamU) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
            curve_mm = $uTop; normal = @(0, 1, 0); layout = @{ rule = 'fixed_number'; number = 3; array_length_mm = $uSpread } },
        [ordered]@{ id = 'U-INF'; host = @{ element_ids = @(, $beamU) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
            curve_mm = $uBot; normal = @(0, 1, 0); layout = @{ rule = 'fixed_number'; number = 3; array_length_mm = $uSpread } })
    if ($Ordered) {
        $s['crossing_order'] = [ordered]@{ offset = 'diameter'
            orders = @(, [ordered]@{ id = 'u-legs'; stays = @('U-SUP'); moves = @('U-INF'); move = 'legs'; legs = 'both' }) }
    }
    $s
}
$pU1 = Invoke-HzPlan (New-HzU $false) 'plan-u1'
$rU1 = @(Get-HzRows $pU1)
$hitU1 = @($rU1 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'bars_intersect' })
Add-HzProbe -Run $run -Id 'U1' -Name 'U bars: the top and bottom U legs in one plane at one station are refused without an order' `
    -Expected 'U-SUP and U-INF bars_intersect, alongside' `
    -Observed ((@($rU1 | ForEach-Object { Get-HzRowText $_ }) -join ' | ')) `
    -Ok ($rU1.Count -eq 2 -and $hitU1.Count -eq 2 -and @(@(Get-HzProp $hitU1[0] 'overlaps') | Where-Object { $_ -and [string]$_.relation -eq 'alongside' }).Count -ge 1) `
    -Evidence @{ rows = $rU1 }

$cU = Invoke-HzCrossed (New-HzU $true) 'u2'
$dU = @(Get-HzDisp $cU.Plan)
$diaU = [double](Get-HzPath @(Get-HzRuleRows $cU.Rows 'U-INF')[0] 'bar_type', 'model_diameter_mm')
# INDEPENDENT: the bottom U's legs one diameter inside its declared stations, at each of the three positions.
$expUb = @(, @(@(($uA + $diaU), $uY, ($zUb + 300)), @(($uA + $diaU), $uY, $zUb), @(($uB - $diaU), $uY, $zUb), @(($uB - $diaU), $uY, ($zUb + 300))))
$devU = Get-HzRowDev @(Get-HzRuleRows $cU.Rows 'U-INF')[0] $expUb[0]
$expUAll = @(foreach ($k in 0..2) { Move-HzPoly $expUb[0] 0 ($k * $uSpread / 2) 0 })
$expTAll = @(foreach ($k in 0..2) { Move-HzPoly $uTop 0 ($k * $uSpread / 2) 0 })
$modelUb = @(); $modelU = [double]::PositiveInfinity; $modelT = [double]::PositiveInfinity
if ($cU.Audit) { $modelUb = @(Get-HzBars $beamU); $modelU = Get-HzWorstOf $expUAll $modelUb; $modelT = Get-HzWorstOf $expTAll $modelUb }
$mU = @($cU.Measured)
Add-HzProbe -Run $run -Id 'U2' -Name 'U bars: the bottom U legs nest inside the top U legs by one diameter; planned, applied, audited, measured' `
    -Expected ('no row refused; one displacement U-INF legs both by {0}; its row within 0.01 mm of the legs moved by hand; verified; audit clean, no overlapping_bar; the legs measured within 1 mm of {0}; the 3 bottom U re-read within 1 mm of the nested stations and the 3 top U at the declared ones' -f $diaU) `
    -Observed $(if ($cU.Bad.Count) { 'plan refused: ' + (Get-HzRowText $cU.Bad[0]) } else {
        ('displacements={0} {1} row_dev_mm={2:N3} | {3} | findings={4} overlapping_bar={5} | measured: {6} | model bars={7} bottom_worst_mm={8:N3} top_worst_mm={9:N3}' -f $dU.Count,
            ((@($dU | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + [string](Get-HzProp $_ 'legs') + ' by ' + [string](Get-HzProp $_ 'moved_by_mm') })) -join '; '),
            $devU, $cU.ApplyText.Text, $cU.Findings.Count, $cU.Overlaps.Count, (Get-HzMeasuredText $mU), $modelUb.Count, $modelU, $modelT) }) `
    -Ok ($cU.Bad.Count -eq 0 -and $dU.Count -eq 1 -and $devU -le 0.01 -and $cU.ApplyText.Ok -and $cU.Findings.Count -eq 0 -and $cU.Overlaps.Count -eq 0 -and
         $mU.Count -eq 1 -and (Test-HzMeasured $mU[0] $diaU) -and $modelUb.Count -eq 6 -and $modelU -le 1.0 -and $modelT -le 1.0) `
    -Evidence @{ displacements = $dU; measured = $mU; row_dev = $devU; findings = $cU.Findings; model_bottom_worst_mm = $modelU; model_top_worst_mm = $modelT }

# =====================================================================  P: a mat and a band bar
Write-Host "`n== slab: a two-way mat at one depth, and a band bar ==" -ForegroundColor Cyan
# Both directions of the bottom mat at ONE depth (cover + 6 for 10M), so every X bar crosses every Y bar. A MAT'S
# END COVER IS THE HOST'S (the trap): kept, with the side cover 10 more.
$offM = $cvS + 6
$spanX = ($bxS.y1 - $bxS.y0) - 2 * ($cvS + 10)
$nX = [int][Math]::Ceiling($spanX / 200 - 1e-9) + 1; $sX = $spanX / ($nX - 1)
# The band bar (16M) along x, between the first two X bars, at cover + 8 from the soffit - through every Y bar.
$yB = $bxS.y0 + $cvS + 10 + $sX / 2; $zB = $bxS.z0 + $cvS + 8
$pb = @(@(($bxS.x0 + $cvS + 8), $yB, $zB), @(($bxS.x1 - $cvS - 8), $yB, $zB))
function New-HzMat($Orders, [string]$Id) {
    $s = New-HzSet $Id
    # TWO MAT RULES, one per direction: ONE mat whose two components share a depth is refused by the mat itself
    # (two_layers_occupy_the_same_plane, measured 2025) - a mat's own layers are spaced by its own declaration.
    # Across rules, which passes over is the crossing order's to say.
    $s['mat_rules'] = @(@(@{ id = 'PX'; d = @(1, 0, 0) }, @{ id = 'PY'; d = @(0, 1, 0) }) | ForEach-Object {
        [ordered]@{ id = $_.id; host = @{ element_ids = @(, $slab) }; face_normal = @(0, 0, -1)
            components = @(, [ordered]@{ name = 'b'; direction = $_.d; bar_type = 'N10'; offset_from_face_mm = $offM; end_cover_mm = $cvS; side_cover_mm = ($cvS + 10)
                allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) } })
    $s['reinforcement_rules'] = @(, [ordered]@{ id = 'PB'; host = @{ element_ids = @(, $slab) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
        curve_mm = $pb; normal = @(0, 1, 0); layout = @{ rule = 'single' } })
    if ($Orders) { $s['crossing_order'] = [ordered]@{ offset = 'contact'; orders = $Orders } }
    $s
}
$pP1 = Invoke-HzPlan (New-HzMat $null 'cx-mat-none') 'plan-p1'
$rP1 = @(Get-HzRows $pP1)
$hitP1 = @($rP1 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'bars_intersect' })
Add-HzProbe -Run $run -Id 'P1' -Name 'slab: a two-way mat at one depth and a band bar through it are refused without an order' `
    -Expected 'PX, PY and PB all bars_intersect' `
    -Observed ((@($rP1 | ForEach-Object { Get-HzRowText $_ }) -join ' | ')) `
    -Ok ($rP1.Count -eq 3 -and $hitP1.Count -eq 3) -Evidence @{ rows = $rP1 }

# P3 FIRST, a plan only: the Y layer sent DOWN, toward the soffit, by contact - out of its cover.
$down = @(, [ordered]@{ id = 'y-down'; stays = @('PX', 'PB'); moves = @('PY'); move = 'translate'; direction = @(0, 0, -1) })
$pP3 = Invoke-HzPlan (New-HzMat $down 'cx-mat-down') 'plan-p3'
$yP3 = @(Get-HzRuleRows (Get-HzRows $pP3) 'PY*')[0]
Add-HzProbe -Run $run -Id 'P3' -Name 'slab: a displacement that breaks the cover is refused by name, never squeezed' `
    -Expected 'PY crossing_displacement_breaks_cover, its row carrying the crossing block (declared_curve_mm beside the displaced curve)' `
    -Observed (Get-HzRowText $yP3) `
    -Ok ([string](Get-HzProp $yP3 'code') -eq 'crossing_displacement_breaks_cover' -and $null -ne (Get-HzPath $yP3 'crossing', 'declared_curve_mm')) `
    -Evidence @{ row = $yP3 }

# P2: two orders on the Y layer, both up (toward the slab's core): over the X layer by contact, over the band bar
# by a declared 16 mm. One displacement, by the larger.
$up = @(
    [ordered]@{ id = 'x-over-y'; stays = @('PX'); moves = @('PY'); move = 'translate'; direction = 'toward_host_core' },
    [ordered]@{ id = 'band-over-y'; stays = @('PB'); moves = @('PY'); move = 'translate'; direction = 'toward_host_core'; offset = @{ mm = 16 } })
$cP = Invoke-HzCrossed (New-HzMat $up 'cx-mat') 'p2'
$dP = @(Get-HzDisp $cP.Plan)
$diaP = [double](Get-HzPath @(Get-HzRuleRows $cP.Rows 'PY*')[0] 'bar_type', 'model_diameter_mm')
# INDEPENDENT: every Y bar 16 mm above the declared depth (16 > contact with an X bar, 2 x 4.75); the X bars and
# the band where they were declared.
$yRow = @(Get-HzRuleRows $cP.Rows 'PY*')[0]
$yPts = @(@(Get-HzProp $yRow 'curve_mm') | ForEach-Object { , @([double]$_[0], [double]$_[1], [double]$_[2]) })
$zYWant = $bxS.z0 + $offM + 16.0
$devP = [Math]::Max([Math]::Abs($yPts[0][2] - $zYWant), [Math]::Abs($yPts[-1][2] - $zYWant))
$modelP = @(); $modelPY = [double]::PositiveInfinity; $nYModel = 0
if ($cP.Audit) {
    $modelP = @(Get-HzBars $slab)
    $ys = @($modelP | Where-Object { [Math]::Abs($_.Points[-1][1] - $_.Points[0][1]) -gt [Math]::Abs($_.Points[-1][0] - $_.Points[0][0]) })
    $nYModel = $ys.Count
    $modelPY = ($ys | ForEach-Object { [Math]::Max([Math]::Abs($_.Points[0][2] - $zYWant), [Math]::Abs($_.Points[-1][2] - $zYWant)) } | Measure-Object -Maximum).Maximum
    if ($null -eq $modelPY) { $modelPY = [double]::PositiveInfinity }
}
$nYPlan = [int](Get-HzPath $yRow 'layout', 'quantity')
$mP = @($cP.Measured)
Add-HzProbe -Run $run -Id 'P2' -Name 'slab: two orders move the Y layer once, up, by the larger offset (16 over contact); planned, applied, audited, measured' `
    -Expected ('no row refused; one displacement PY up by 16 against PX and PB (orders x-over-y, band-over-y); every Y bar planned at z {0:N2}; verified; audit clean, no overlapping_bar; measured within 1 mm of 16; every Y bar re-read within 1 mm of z {0:N2}' -f $zYWant) `
    -Observed $(if ($cP.Bad.Count) { 'plan refused: ' + (Get-HzRowText $cP.Bad[0]) } else {
        ('displacements={0} {1} plan_dev_mm={2:N3} | {3} | findings={4} overlapping_bar={5} | measured: {6} | model Y bars={7}/{8} worst_z_mm={9:N3}' -f $dP.Count,
            ((@($dP | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' by ' + [string](Get-HzProp $_ 'moved_by_mm') + ' orders=' + ((@(Get-HzProp $_ 'order_ids') | ForEach-Object { [string]$_ }) -join '+') })) -join '; '),
            $devP, $cP.ApplyText.Text, $cP.Findings.Count, $cP.Overlaps.Count, (Get-HzMeasuredText $mP), $nYModel, $nYPlan, $modelPY) }) `
    -Ok ($cP.Bad.Count -eq 0 -and $dP.Count -eq 1 -and [Math]::Abs([double](Get-HzProp $dP[0] 'moved_by_mm') - 16.0) -le 0.001 -and
         @(Get-HzProp $dP[0] 'order_ids').Count -eq 2 -and $devP -le 0.01 -and $cP.ApplyText.Ok -and $cP.Findings.Count -eq 0 -and $cP.Overlaps.Count -eq 0 -and
         $mP.Count -eq 1 -and (Test-HzMeasured $mP[0] 16.0) -and $nYModel -eq $nYPlan -and $nYModel -gt 0 -and $modelPY -le 1.0) `
    -Evidence @{ displacements = $dP; measured = $mP; plan_dev = $devP; findings = $cP.Findings; model_y_worst_mm = $modelPY; y_bar_diameter = $diaP }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
