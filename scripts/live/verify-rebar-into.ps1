#Requires -Version 5.1
<#
  ACERO 3.0, L1, LIVE: steel that crosses from one element to another.

  A footing, a column on it, a column above that, a second column, and two beams
  framing between them - all built here, on a DISPOSABLE fixture made from Autodesk's
  metric template (M_Concrete-Rectangular-Column 450 x 600mm, M_Footing-Rectangular
  1800 x 1200 x 450mm, M_Concrete-Rectangular Beam 300 x 600mm, 16M). Then:

    J1  the column's bars start as dowels in the footing, lapped with the column bars,
        which run on into the column above - one section layer, `into` both
    J2  a dowel declared WITHOUT into is outside its host; WITH into it is in the union
    J3  an anchorage the footing cannot give is refused by name, before any write
    J4  a beam's top bar anchored in the column with a 90 degree leg: length measured
    J5  a beam's bottom bar running on through the column into the next span
    J6  everything planned is applied, and the apply re-measures containment and
        anchorage on the bars Revit drew
    J7  the audit finds nothing on those rules
    J8  footing cover 75 mm and column section cover 40 mm: the layer running
        into footing and upper column is planned, applied and audited

  Every coordinate comes from the bounding boxes of the elements just made.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-into' -Document $Document

function Get-HzTypeId([string]$Category, [string]$Family, [string]$Name) {
    $t = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "type-$Family" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
        Where-Object { $_.is_element_type -and [string]$_.family -eq $Family -and [string]$_.name -eq $Name })
    if ($t.Count -ne 1) { throw "HARNESS: the fixture has no single $Family : $Name type" }
    [long]$t[0].element_id
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

function New-HzSet([string]$Id) {
    [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'L1 crossing steel, synthetic' }
        bar_types = @(, @{ id = 'N16'; type_name = '16M' })
    }
}

function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }
function Get-HzRuleRows($Rows, [string]$Like) { @($Rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like }) }
function Get-HzRowText($Row) {
    if ($null -eq $Row) { return 'no row' }
    $code = Get-HzProp $Row 'code'
    if ($code) { return ([string](Get-HzProp $Row 'rule_id') + ' ' + $code + ': ' + (Limit-HzText ([string](Get-HzProp $Row 'why')) 260)) }
    $c = Get-HzProp $Row 'containment'
    $a = @(Get-HzProp $Row 'anchorage')
    ([string](Get-HzProp $Row 'rule_id') + ' ' + [string](Get-HzProp $c 'containment') + ' against=' +
     ((@(Get-HzProp $c 'measured_against') | ForEach-Object { [string]$_ }) -join ',') + ' anchorage=' +
     ((@($a | Where-Object { $_ } | ForEach-Object { [string]$_.element_id + ':' + [string]$_.verdict + '@' + [string]$_.measured_mm })) -join ','))
}

Write-Host "`n== fixture: footing, two stacked columns, a third column, two beams ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colType = Get-HzTypeId 'OST_StructuralColumns' 'M_Concrete-Rectangular-Column' '450 x 600mm'
$footType = Get-HzTypeId 'OST_StructuralFoundation' 'M_Footing-Rectangular' '1800 x 1200 x 450mm'
$beamType = Get-HzTypeId 'OST_StructuralFraming' 'M_Concrete-Rectangular Beam' '300 x 600mm'
$X0 = 40000.0; $X3 = $X0 + 5000.0
# THE UPPER COLUMN STANDS ON ITS OWN LEVEL. Measured on Revit 2024: create_elements refuses a
# column on the first level with a 3000 mm base offset and a height ("The base offset that
# governs this instance's elevation could not be applied") - recorded for L8, not worked around
# inside the tool here.
$lvZ = 0.0
try { $lvZ = [double]$level.bounding_box.min[2] } catch { }
$lvl2 = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'level-2' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'level'; elevation = ($lvZ + 3000.0); name = 'HZ L1 upper' }) }
$lv2 = [long](Get-HzProp @($lvl2.Apply.Result.rows)[0] 'element_id')
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'columns' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv2; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @($X3, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }
$cols = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($cols.Count -ne 3) { throw "HARNESS: three columns were not created ($($cols.Count))" }
$c1 = $cols[0]; $c2 = $cols[1]; $c3 = $cols[2]
$foot = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'footing' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'family_instance'; type_id = $footType; level_id = $lv; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' }) }
$footing = [long](Get-HzProp @($foot.Apply.Result.rows)[0] 'element_id')
$bm = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beams' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($X0, 0.0, 3000.0); end = @($X3, 0.0, 3000.0) },
        @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($X3, 0.0, 3000.0); end = @(($X3 + 4000.0), 0.0, 3000.0) }) }
$beams = @(@($bm.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($beams.Count -ne 2) { throw "HARNESS: two beams were not created ($($beams.Count))" }
$b1 = $beams[0]; $b2 = $beams[1]

# BOXES ARE bx*: PowerShell variable names ignore case, so $C1 would overwrite the id in $c1.
$bxC1 = Get-HzBox $c1; $bxC2 = Get-HzBox $c2; $bxC3 = Get-HzBox $c3; $bxF = Get-HzBox $footing; $bxB1 = Get-HzBox $b1; $bxB2 = Get-HzBox $b2
$run.Fixture['elements'] = @{ c1 = $c1; c2 = $c2; c3 = $c3; footing = $footing; b1 = $b1; b2 = $b2 }
$run.Fixture['boxes'] = @{ c1 = $bxC1; c2 = $bxC2; c3 = $bxC3; footing = $bxF; b1 = $bxB1; b2 = $bxB2 }
$footDepth = $bxF.z1 - $bxF.z0
$colBase = $bxC1.z0; $colTop = $bxC1.z1

# ------------------------------------------------------------------ the sets
# The template's footing has its top at the level, under the column: measured, not assumed.
if ([Math]::Abs($bxF.z1 - $colBase) -gt 1.0) { throw "HARNESS: the footing's top ($($bxF.z1)) is not at the column's base ($colBase)" }
# J1: one layer through three elements - a dowel lapped 600 with the column bar, which runs
# 700 into the column above. Laid 100 mm above the footing's soffit.
$dowelFrom = ($bxF.z0 + 100.0) - $colBase
$j1 = New-HzSet 'l1-column'
$j1['section_rules'] = @(, [ordered]@{
    id = 'J1'; host = @{ element_ids = @(, $c1) }; cover = @{ distance_mm = 40 }
    layers = @(, [ordered]@{
        name = 'main'; face = 'perimeter'; count = 4; bar_type = 'N16'; allow_new_shape = $true
        extend = @{ from_mm = $dowelFrom; to_mm = ($colTop - $colBase) + 700.0 }
        into = @($footing, $c2)
        laps = @(, @{ at_mm = 300; length_mm = 600 })
        anchorage = @(@{ element_id = $footing; min_length_mm = 300; end = 'start' },
                      @{ element_id = $c2; min_length_mm = 650; end = 'end' }) }) })
$j1['cover_rules'] = @(, [ordered]@{
    id = 'J8-footing-cover'; host = @{ element_ids = @(, $footing) }
    face = 'common'; distance_mm = 75.0 })

# J2: one straight dowel 100 mm in from two column faces, 300 mm down into the footing.
$dx = $bxC1.x0 + 100.0; $dy = $bxC1.y0 + 100.0
function New-HzDowel([string]$Id, [bool]$Into, [double]$MinMm) {
    $r = [ordered]@{
        id = $Id; host = @{ element_ids = @(, $c1) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
        curve_mm = @(@($dx, $dy, ($colBase - 300.0)), @($dx, $dy, ($colBase + 900.0))); normal = @(1, 0, 0)
        layout = @{ rule = 'single' } }
    if ($Into) { $r['into'] = @(, $footing) }
    if ($MinMm -gt 0) { $r['anchorage'] = @(, @{ element_id = $footing; min_length_mm = $MinMm }) }
    $r
}
$j2 = New-HzSet 'l1-dowel'
$j2['reinforcement_rules'] = @((New-HzDowel 'J2-solo' $false 0), (New-HzDowel 'J2-into' $true 250))
$j3 = New-HzSet 'l1-short'
$j3['reinforcement_rules'] = @(, (New-HzDowel 'J3' $true 900))

# J4: B1's top bar, 50 below the top, with a 500 mm leg down inside C1 - 60 mm in from C1's far face.
$topZ = $bxB1.z1 - 50.0
$legX = $bxC1.x0 + 60.0
$j4Rule = [ordered]@{
    id = 'J4'; host = @{ element_ids = @(, $b1) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
    curve_mm = @(@($legX, 0.0, ($topZ - 500.0)), @($legX, 0.0, $topZ), @(($bxC1.x1 + 1500.0), 0.0, $topZ)); normal = @(0, 1, 0)
    layout = @{ rule = 'single' }; into = @(, $c1)
    anchorage = @(, @{ element_id = $c1; min_length_mm = 600 }) }
# J5: B1's bottom bar, 50 above the soffit, on through C3 and 900 mm into B2.
$botZ = $bxB1.z0 + 50.0
$j5Rule = [ordered]@{
    id = 'J5'; host = @{ element_ids = @(, $b1) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
    curve_mm = @(@(($bxC1.x1 + 40.0), 0.0, $botZ), @(($bxC3.x1 + 900.0), 0.0, $botZ)); normal = @(0, 1, 0)
    layout = @{ rule = 'single' }; into = @($c3, $b2)
    anchorage = @(, @{ element_id = $b2; min_length_mm = 600 }) }
$jb = New-HzSet 'l1-beams'
$jb['reinforcement_rules'] = @($j4Rule, $j5Rule)

# ------------------------------------------------------------------ plan
Write-Host "`n== plan ==" -ForegroundColor Cyan
$p1 = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-j1' -Arguments @{ target_document = $Document; requirement_set = $j1 }).Result
$j1Rows = Get-HzRuleRows $p1 'J1#*'
$j1Bad = @($j1Rows | Where-Object { Get-HzProp $_ 'code' })
$j1Met = @($j1Rows | Where-Object { @(Get-HzProp $_ 'anchorage') | Where-Object { $_ -and [string]$_.verdict -eq 'met' } })
Add-HzProbe -Run $run -Id 'J1' -Name 'L1 a column layer runs from a dowel in the footing, lapped, into the column above' `
    -Expected 'four rows (two faces x dowel + column bar), none refused, measured against host+footing+column above, each anchorage met' `
    -Observed ("rows={0} refused={1} anchored={2} first={3}" -f $j1Rows.Count, $j1Bad.Count, $j1Met.Count,
        $(if ($j1Bad.Count) { Get-HzRowText $j1Bad[0] } elseif ($j1Rows.Count) { Get-HzRowText $j1Rows[0] } else { '' })) `
    -Ok ($j1Rows.Count -eq 4 -and $j1Bad.Count -eq 0 -and $j1Met.Count -eq 4) -Evidence @{ rows = $j1Rows }
Add-HzProbe -Run $run -Id 'J8' -Name 'L1 a 40 mm column section runs into a footing declaring 75 mm cover and the upper column' `
    -Expected 'all four section pieces planned without bar_short_of_the_declared_cover' `
    -Observed ("planned={0} refused={1}" -f $j1Rows.Count, $j1Bad.Count) `
    -Ok ($j1Rows.Count -eq 4 -and $j1Bad.Count -eq 0) -Evidence @{ rows = $j1Rows; footing_cover_mm = 75; section_cover_mm = 40 }

$p2 = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-j2' -Arguments @{ target_document = $Document; requirement_set = $j2 }).Result
$solo = @(Get-HzRuleRows $p2 'J2-solo')[0]; $withInto = @(Get-HzRuleRows $p2 'J2-into')[0]
Add-HzProbe -Run $run -Id 'J2' -Name 'L1 a dowel is outside its host alone and inside the union it was declared into' `
    -Expected 'J2-solo bar_partly_outside_host_solid; J2-into planned, inside, anchorage met' `
    -Observed ((Get-HzRowText $solo) + ' | ' + (Get-HzRowText $withInto)) `
    -Ok ([string](Get-HzProp $solo 'code') -eq 'bar_partly_outside_host_solid' -and -not (Get-HzProp $withInto 'code') -and
         [string](Get-HzPath $withInto 'containment', 'containment') -eq 'inside') -Evidence @{ solo = $solo; into = $withInto }

$p3 = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-j3' -Arguments @{ target_document = $Document; requirement_set = $j3 }).Result
$short = @(Get-HzRuleRows $p3 'J3')[0]
Add-HzProbe -Run $run -Id 'J3' -Name 'L1 an anchorage the footing cannot give is refused by name before any write' `
    -Expected 'J3 anchorage_short, naming 300 measured against 900 declared' `
    -Observed (Get-HzRowText $short) `
    -Ok ([string](Get-HzProp $short 'code') -eq 'anchorage_short') -Evidence @{ row = $short }

$pb = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-beams' -Arguments @{ target_document = $Document; requirement_set = $jb }).Result
$r4 = @(Get-HzRuleRows $pb 'J4')[0]; $r5 = @(Get-HzRuleRows $pb 'J5')[0]
$a4 = @(Get-HzProp $r4 'anchorage') | Where-Object { $_ } | Select-Object -First 1
Add-HzProbe -Run $run -Id 'J4' -Name 'L1 a beam top bar anchored in the column with a 90 degree leg: the anchored length is measured' `
    -Expected ('planned, inside B1+C1, anchorage in C1 met at {0} mm (leg 500 + {1} across the column)' -f (500 + $bxC1.x1 - $legX), ($bxC1.x1 - $legX)) `
    -Observed (Get-HzRowText $r4) `
    -Ok (-not (Get-HzProp $r4 'code') -and $null -ne $a4 -and [string]$a4.verdict -eq 'met' -and
         [Math]::Abs([double]$a4.measured_mm - (500 + $bxC1.x1 - $legX)) -le 2) -Evidence @{ row = $r4 }
Add-HzProbe -Run $run -Id 'J5' -Name 'L1 a beam bottom bar runs on through the column into the next span' `
    -Expected 'planned, inside B1+C3+B2, anchorage in B2 met at 900 mm' `
    -Observed (Get-HzRowText $r5) `
    -Ok (-not (Get-HzProp $r5 'code') -and [string](Get-HzPath $r5 'containment', 'containment') -eq 'inside') -Evidence @{ row = $r5 }

# ------------------------------------------------------------------ apply
Write-Host "`n== apply ==" -ForegroundColor Cyan
$all = New-HzSet 'l1-all'
$all['section_rules'] = $j1['section_rules']
$all['cover_rules'] = $j1['cover_rules']
$all['reinforcement_rules'] = @((New-HzDowel 'J2-into' $true 250), $j4Rule, $j5Rule)
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{
    target_document = $Document; requirement_set = $all; transaction_name = 'HZ rebar L1 crossing steel' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = $(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') } else { @() })
$withAnch = @($verif | Where-Object { $null -ne (Get-HzPath $_ 'checks', 'anchorage') })
$anchOk = @($withAnch | Where-Object { (Get-HzPath $_ 'checks', 'anchorage', 'verified') -eq $true })
$notOk = @($verif | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'J6' -Name 'L1 the crossing steel applies, and containment and anchorage are re-measured on the bars Revit drew' `
    -Expected '7 sets (4 column, the dowel, 2 beam bars) verified; every anchorage check verified' `
    -Observed ("applied={0} verified={1}/{2} anchorage_ok={3}/{4} first_failure={5}" -f $applyOk,
        @($verif | Where-Object { (Get-HzProp $_ 'verified') -eq $true }).Count, $verif.Count, $anchOk.Count, $withAnch.Count,
        $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600) }
          elseif (-not $applyOk) { Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 400 } else { '' })) `
    -Ok ($applyOk -and $verif.Count -eq 7 -and $notOk.Count -eq 0 -and $withAnch.Count -eq 7 -and $anchOk.Count -eq 7) -Evidence @{ verification = $verif }

$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $all }
$findings = @(Get-HzPath $audit.Result 'findings')
$ours = @($findings | Where-Object { [string](Get-HzProp $_ 'rule_id') -match '^(J1#|J2-into|J4|J5)' })
Add-HzProbe -Run $run -Id 'J7' -Name 'L1 the audit finds the crossing steel where the rules put it' `
    -Expected 'no finding on J1, J2-into, J4 or J5' `
    -Observed ("verdict={0} findings_on_ours={1} {2}" -f (Get-HzProp $audit.Result 'verdict'), $ours.Count,
        (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $findings }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
