#Requires -Version 5.1
<#
  ACERO 3.0, L6, LIVE, on a DISPOSABLE fixture:

    T1  a stair's waist slab modelled as a sloped structural floor (slab_shape raises one edge):
        a mat along the slope applies, reads back and audits
    T2  a landing as a structural floor with its own mat
    T3  a stair element named as a host is refused by name (stair_not_a_rebar_host), with the way out
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-stairs' -Document $Document
function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }
function Get-HzCover([long]$Id) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }
    $row = @(Get-HzPath $hq.Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -ne $c) { return [double]$c }
    $fc = @(@(Get-HzPath $row 'cover', 'faces') | ForEach-Object { Get-HzPath $_ 'cover', 'distance_mm' } | Where-Object { $null -ne $_ })
    [double](@($fc | Sort-Object -Descending)[0])
}

Write-Host "`n== fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$floorT = @(Get-HzTypes 'OST_Floors' | Where-Object { [string]$_.name -match '(\d+)\s*mm' -and [string]$_.name -notmatch 'Deck|Metal|Steel|Wood|Timber' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 150 } | Select-Object -First 1)[0]
$X0 = 210000.0
$waist = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'waist' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
        profile = @(, @(@($X0, 0.0, 0.0), @(($X0 + 1200), 0.0, 0.0), @(($X0 + 1200), 4000.0, 0.0), @($X0, 4000.0, 0.0))) }) })
# Raise the far edge 2000 mm: a waist slab rising 2000 over 4000.
$shape = Invoke-HzWrite -Run $run -Tool 'horizun_slab_shape' -Label 'raise-edge' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
    operation = 'modify_subelement'; element_id = $waist; start = @($X0, 4000.0); end = @(($X0 + 1200), 4000.0); offset = 2000.0 }
$landing = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'landing' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true; offset = 2000.0
        profile = @(, @(@($X0, 4000.0, 2000.0), @(($X0 + 1200), 4000.0, 2000.0), @(($X0 + 1200), 5200.0, 2000.0), @($X0, 5200.0, 2000.0))) }) })
$cvW = Get-HzCover $waist; $cvL = Get-HzCover $landing
$run.Fixture['elements'] = @{ waist = $waist; landing = $landing; shaped = $(if ($shape.Apply) { $shape.Apply.Ok } else { $false }) }

# The slope's own frame: up the slope, across it, and the normal out of its top face.
$len = [Math]::Sqrt(4000 * 4000 + 2000 * 2000)
$up = @(0.0, (4000 / $len), (2000 / $len)); $n = @(0.0, (-2000 / $len), (4000 / $len))
$set = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = 'l6-stairs'; version = '1.0.0' }
    bar_types = @(@{ id = 'N12'; type_name = '13M' }, @{ id = 'N10'; type_name = '10M' })
    mat_rules = @(
        [ordered]@{ id = 'T1'; host = @{ element_ids = @(, $waist) }; face_normal = @(0.0, (2000 / $len), (-4000 / $len))
            components = @(
                [ordered]@{ name = 'along'; direction = $up; bar_type = 'N12'; offset_from_face_mm = $cvW + 7; end_cover_mm = $cvW; side_cover_mm = $cvW + 7; allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } },
                [ordered]@{ name = 'across'; direction = @(1, 0, 0); bar_type = 'N10'; offset_from_face_mm = $cvW + 19; end_cover_mm = $cvW; side_cover_mm = $cvW + 7; allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 250 } }) },
        [ordered]@{ id = 'T2'; host = @{ element_ids = @(, $landing) }; face_normal = @(0, 0, -1)
            components = @(
                [ordered]@{ name = 'x'; direction = @(1, 0, 0); bar_type = 'N12'; offset_from_face_mm = $cvL + 7; end_cover_mm = $cvL; side_cover_mm = $cvL + 7; allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } },
                [ordered]@{ name = 'y'; direction = @(0, 1, 0); bar_type = 'N12'; offset_from_face_mm = $cvL + 20; end_cover_mm = $cvL; side_cover_mm = $cvL + 7; allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) })
}
$rows = @(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result 'reinforcement')
# EACH HOST APPLIED ON ITS OWN: a refusal on the waist must not keep the landing from being measured.
$verif = @(); $finds = @()
foreach ($m in $set.mat_rules) {
    $mine = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like "$($m.id)#*" })
    if (@($mine | Where-Object { Get-HzProp $_ 'code' }).Count) { continue }
    $one = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = "l6-$($m.id)"; version = '1.0.0' }; bar_types = $set.bar_types; mat_rules = @(, $m) }
    $ap = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label "apply-$($m.id)" -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $one; transaction_name = "HZ rebar L6 $($m.id)" }
    $v = @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'verification') }))
    $verif += $v
    if ($v.Count) {
        $au = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label "audit-$($m.id)" -Arguments @{ target_document = $Document; requirement_set = $one }
        $finds += @(@(Get-HzPath $au.Result 'findings') | Where-Object { $_ -and [string]$_.severity -ne 'info' })
    }
}
foreach ($k in @(@{ id = 'T1'; n = 'L6 a waist slab as a sloped structural floor: a mat along the slope applies, reads back and audits' }, @{ id = 'T2'; n = 'L6 a landing as a structural floor with its own mat' })) {
    $r = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like "$($k.id)#*" })
    $rb = @($r | Where-Object { Get-HzProp $_ 'code' })
    $v = @($verif | Where-Object { $_ -and [string](Get-HzProp $_ 'rule_id') -like "$($k.id)#*" })
    $vn = @($v | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    $f = @($finds | Where-Object { [string](Get-HzProp $_ 'rule_id') -like "$($k.id)#*" })
    Add-HzProbe -Run $run -Id $k.id -Name $k.n -Expected 'planned, applied, verified, no audit error' `
        -Observed ("shaped={0} rows={1} refused={2} verified={3}/{4} findings={5} {6}" -f $run.Fixture['elements'].shaped, $r.Count, $rb.Count, ($v.Count - $vn.Count), $v.Count, $f.Count,
            $(if ($rb.Count) { [string](Get-HzProp $rb[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $rb[0] 'why')) 300) }
              elseif ($vn.Count) { Limit-HzText ((Get-HzProp $vn[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 500 }
              elseif ($f.Count) { [string]$f[0].code + ' exp=' + [string]$f[0].expected + ' obs=' + [string]$f[0].observed } else { '' })) `
        -Ok ($r.Count -eq 2 -and $rb.Count -eq 0 -and $v.Count -eq 2 -and $vn.Count -eq 0 -and $f.Count -eq 0) -Evidence @{ rows = $r; verification = $v; findings = $f }
}

Write-Host "`n== T3: a stair element as a host ==" -ForegroundColor Cyan
# A COMPONENT STAIR type - cast in place first. The category also lists the legacy sketch-based
# family 'Stair' (measured, Revit 2024: its types are not StairsType and create_elements refuses them).
$stairT = @(@(Get-HzTypes 'OST_Stairs' | Where-Object { [string]$_.family -eq 'Cast-In-Place Stair' }) +
            @(Get-HzTypes 'OST_Stairs' | Where-Object { [string]$_.family -eq 'Assembled Stair' }) | Select-Object -First 1)
$run.Fixture['stair_type'] = @($stairT | ForEach-Object { [string]$_.family + ' : ' + [string]$_.name })
$lvlTop = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'level-top' -Arguments @{ target_document = $Document; units = 'mm'; elements = @(, @{ kind = 'level'; elevation = 3060.0; name = 'HZ L6 top' }) }
$st = $(if ($stairT.Count) { Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'stair' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'stairs'; level_id = $lv; top_level_id = (Get-HzId $lvlTop); type_id = [long]$stairT[0].element_id; desired_risers = 18; tread_depth = 280
        runs = @(, @{ start = @(($X0 + 3000), 0.0, 0.0); end = @(($X0 + 3000), 4760.0, 0.0); width = 1200.0; expected_risers = 18 }) }) } } else { $null })
$stair = $(if ($st -and $st.Apply -and $st.Apply.Ok) { [long](Get-HzProp @($st.Apply.Result.rows)[0] 'element_id') } else { $null })
if (-not $stair) {
    Add-HzProbe -Run $run -Id 'T3' -Name 'L6 a stair element as a host is refused by name' -Status 'fixture_missing' -Expected 'a stair created by create_elements' `
        -Observed $(if ($st) { Limit-HzText $(if ($st.Apply) { $st.Apply.Text } else { $st.Dry.Text }) 300 } else { 'no stair type in the template' })
} else {
    $sset = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l6-stair-host'; version = '1' }; bar_types = $set.bar_types
        reinforcement_rules = @(, [ordered]@{ id = 'T3'; host = @{ element_ids = @(, $stair) }; bar_type = 'N12'; style = 'standard'; allow_new_shape = $true
            # ACROSS ONE STEP, inside it: tread 6 runs y 1400..1680 with its top at 6 x 170 = 1020; the bar is
            # 60 below that, clear of the soffit, and 500 either side of the run's centreline.
            curve_mm = @(@(($X0 + 2500), 1540.0, 960.0), @(($X0 + 3500), 1540.0, 960.0)); normal = @(0, 1, 0); layout = @{ rule = 'single' } }) }
    $srow = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-stair' -Arguments @{ target_document = $Document; requirement_set = $sset }).Result 'reinforcement'))[0]
    # WHAT REVIT SAYS DECIDES (measured, Revit 2024 and 2026: a cast-in-place stair IS a valid host):
    # a stair Revit accepts plans a bar inside its solid; one it refuses is refused by name.
    $code = [string](Get-HzProp $srow 'code')
    Add-HzProbe -Run $run -Id 'T3' -Name 'L6 a stair element: hosted where Revit accepts it, refused by name where it does not' `
        -Expected 'no code and will_build (Revit accepts the stair) or stair_not_a_rebar_host' `
        -Observed ("code={0} will_build={1} {2}" -f $code, (Get-HzProp $srow 'will_build'), (Limit-HzText ([string](Get-HzProp $srow 'why')) 300)) `
        -Ok (($code -eq '' -and (Get-HzProp $srow 'will_build') -eq $true) -or $code -eq 'stair_not_a_rebar_host') -Evidence @{ row = $srow }
}

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
