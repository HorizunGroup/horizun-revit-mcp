#Requires -Version 5.1
<#
  ACERO 3.0, L5, LIVE: walls on a DISPOSABLE fixture (Autodesk metric template: a structural basic
  wall on an isolated footing strip, 16M/10M).

    W1  boundary elements at both ends of a structural wall: corner bars, face bars and stirrups up
        its height, inside the end region declared
    W2  both faces of the wall carry a two-way mat (hooked horizontals are not measured here: Revit's
        hook orientation on an open mat bar did not read back as created, in either direction)
    W3  the wall's vertical bars start as dowels in the footing under it (into)
    W4  everything applies and reads back
    W5  the audit finds it where the rules put it
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-walls' -Document $Document

function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }
function Get-HzHookName([string]$Pattern) {
    $h = @(Get-HzTypes 'OST_Rebar' | Where-Object { [string]$_.family -eq 'Rebar Hook' -and [string]$_.name -match $Pattern })
    if ($h.Count -ne 1) { throw "HARNESS: $($h.Count) hook types match $Pattern" }
    [string]$h[0].name
}
function Get-HzBox([long]$Id) {
    $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "box-$Id" -Arguments @{ element_ids = @(, $Id); include_links = $false; include_bounding_box = $true; max_rows = 5 }
    $b = @($q.Result.rows | Where-Object { [long]$_.element_id -eq $Id })[0].bounding_box
    [pscustomobject]@{ x0 = [double]$b.min[0]; y0 = [double]$b.min[1]; z0 = [double]$b.min[2]; x1 = [double]$b.max[0]; y1 = [double]$b.max[1]; z1 = [double]$b.max[2] }
}
function Get-HzCover([long]$Id) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }
    $row = @(Get-HzPath $hq.Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -ne $c) { return [double]$c }
    $fc = @(@(Get-HzPath $row 'cover', 'faces') | ForEach-Object { Get-HzPath $_ 'cover', 'distance_mm' } | Where-Object { $null -ne $_ })
    [double](@($fc | Sort-Object -Descending)[0])
}

Write-Host "`n== fixture: a structural wall on a footing ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
# A concrete basic wall at least 200 mm thick, by the millimetres in its name.
$wallT = @(Get-HzTypes 'OST_Walls' | Where-Object { [string]$_.family -eq 'Basic Wall' -and [string]$_.name -match 'Concrete' -and [string]$_.name -match '(\d+)\s*mm' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 200 } | Select-Object -First 1)
if ($wallT.Count -eq 0) { throw 'HARNESS: no concrete basic wall type of 200 mm or more in the template' }
$footType = @(Get-HzTypes 'OST_StructuralFoundation' | Where-Object { [string]$_.family -eq 'M_Footing-Rectangular' -and [string]$_.name -eq '1800 x 1200 x 450mm' })[0]
$X0 = 130000.0
$wall = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'wall' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'wall'; type_id = [long]$wallT[0].element_id; level_id = $lv; start = @($X0, 0.0, 0.0); end = @(($X0 + 1800.0), 0.0, 0.0); height = 3000.0; structural = $true; join_rule = 'disallow' }) })
$foot = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'footing' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'family_instance'; type_id = [long]$footType.element_id; level_id = $lv; point = @(($X0 + 900.0), 0.0, 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' }) })
$bw = Get-HzBox $wall; $bf = Get-HzBox $foot
$wc = Get-HzCover $wall
$run.Fixture['elements'] = @{ wall = $wall; footing = $foot; wall_type = [string]$wallT[0].name; wall_cover = $wc }
$h90 = Get-HzHookName '^Standard - 90 deg\.?$'
$h135 = Get-HzHookName '^Stirrup/Tie - 135 deg\.?$'
$thick = $bw.y1 - $bw.y0

$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'l5-walls'; version = '1.0.0'; title = 'L5 walls, synthetic' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(@{ id = 'H90'; type_name = $h90 }, @{ id = 'H135'; type_name = $h135 })
    wall_boundary_rules = @(
        foreach ($e in 'start', 'end') {
            [ordered]@{ id = "W1-$e"; host = @{ element_ids = @(, $wall) }; end = $e; length_mm = 400; cover = @{ source = 'host' }
                layers = @(, @{ face = 'perimeter'; count = 4; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
                stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
                    start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
                    zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } }
        })
    mat_rules = @(
        foreach ($side in @(@{ n = 'pos'; v = @(0, 1, 0) }, @{ n = 'neg'; v = @(0, -1, 0) })) {
            [ordered]@{ id = "W2-$($side.n)"; host = @{ element_ids = @(, $wall) }; face_normal = $side.v
                components = @(
                    # CLEAR OF THE BOUNDARY STIRRUPS. MEASURED (Revit 2024 and 2026): with the outermost h bar at
                    # cover + 8 it crossed the first boundary stirrup (cover + half a 10M), and Revit pushed it
                    # out until tangent - 54.25 mm, the array 12.5 short. Alone on a wall the same side cover
                    # was honoured to the millimetre (48, 52, 54.25, 56, 64).
                    # AND CLEAR OF THE BOUNDARY'S CORNER BARS (overlapping_bar, acero 3.0): at cover + 24 run end
                    # to end, the horizontals crossed the boundary's vertical corner bars (cover + a 10M stirrup +
                    # half a 16M), 6.5 mm apart against 12.75 of radii. Revit left that steel inside steel, and
                    # the plan now refuses it as bars_intersect. The horizontals stop 480 mm from the wall's ends,
                    # past the 400 mm boundary regions (verify-rebar-overlap.ps1 O3/O4 measure both cases), by a run
                    # window: the end cover of a hosted bar is the host's, and another is refused by name.
                    [ordered]@{ name = 'h'; direction = @(1, 0, 0); bar_type = 'N10'; offset_from_face_mm = $wc + 24; end_cover_mm = $wc; side_cover_mm = $wc + 24
                        run = @{ from_mm = 480; to_mm = ($bw.x1 - $bw.x0) - 480 }
                        allow_new_shape = $true
                        layout = @{ rule = 'maximum_spacing'; spacing_mm = 250 } },
                    [ordered]@{ name = 'v'; direction = @(0, 0, 1); bar_type = 'N16'; offset_from_face_mm = $wc + 8; end_cover_mm = $wc; side_cover_mm = $wc + 450
                        allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }
        })
    # W3: one row of dowels down the wall's middle, 300 into the footing, lapped above the joint.
    reinforcement_rules = @(, [ordered]@{ id = 'W3'; host = @{ element_ids = @(, $wall) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
        curve_mm = @(@(($bw.x0 + 600.0), (($bw.y0 + $bw.y1) / 2), ($bw.z0 - 300.0)), @(($bw.x0 + 600.0), (($bw.y0 + $bw.y1) / 2), ($bw.z0 + 600.0))); normal = @(1, 0, 0)
        layout = @{ rule = 'fixed_number'; number = 3; array_length_mm = 600 }; into = @(, $foot)
        anchorage = @(, @{ element_id = $foot; min_length_mm = 250 }) })
}

Write-Host "`n== plan ==" -ForegroundColor Cyan
$rows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
function Show-HzRows([string]$Like) {
    $r = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like })
    $bad = @($r | Where-Object { Get-HzProp $_ 'code' })
    [pscustomobject]@{ Rows = $r; Bad = $bad; Text = ("rows={0} refused={1} {2}" -f $r.Count, $bad.Count,
        ((@($bad | Select-Object -First 2 | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + (Get-HzProp $_ 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 260) })) -join ' | ')) }
}
$w1 = Show-HzRows 'W1-*'; $w2 = Show-HzRows 'W2-*'; $w3 = Show-HzRows 'W3*'
Add-HzProbe -Run $run -Id 'W1' -Name 'L5 boundary elements at both ends of a structural wall' -Expected 'perimeter rows and stirrup zones at both ends, none refused' -Observed $w1.Text -Ok ($w1.Rows.Count -ge 6 -and $w1.Bad.Count -eq 0) -Evidence @{ rows = $w1.Rows }
Add-HzProbe -Run $run -Id 'W2' -Name 'L5 both faces of the wall carry a two-way mat' -Expected '4 rows, none refused' -Observed $w2.Text -Ok ($w2.Rows.Count -eq 4 -and $w2.Bad.Count -eq 0) -Evidence @{ rows = $w2.Rows }
Add-HzProbe -Run $run -Id 'W3' -Name 'L5 dowels from the footing into the wall, anchored' -Expected '1 row, inside the union, anchorage met' -Observed $w3.Text -Ok ($w3.Rows.Count -eq 1 -and $w3.Bad.Count -eq 0) -Evidence @{ rows = $w3.Rows }

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar L5 walls' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = @($(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') }))
$notOk = @($verif | Where-Object { $null -ne $_ -and (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'W4' -Name 'L5 the wall steel applies and every set reads back' -Expected 'every set verified' `
    -Observed ("applied={0} verified={1}/{2} first_failure={3}" -f $applyOk, ($verif.Count - $notOk.Count), $verif.Count,
        $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600) }
          elseif (-not $applyOk) { Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 500 } else { '' })) `
    -Ok ($applyOk -and $verif.Count -gt 0 -and $notOk.Count -eq 0) -Evidence @{ verification = $verif }
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$ours = @(@(Get-HzPath $audit.Result 'findings') | Where-Object { $_ -and [string](Get-HzProp $_ 'severity') -ne 'info' })
Add-HzProbe -Run $run -Id 'W5' -Name 'L5 the audit finds the wall steel where the rules put it' -Expected 'no error or unknown finding' `
    -Observed ("verdict={0} findings={1} {2}" -f (Get-HzProp $audit.Result 'verdict'), $ours.Count, (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $ours }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
