#Requires -Version 5.1
<#
  ACERO 3.0, L3, LIVE: foundations on a DISPOSABLE fixture (Autodesk metric template:
  M_Footing-Rectangular, a bearing wall with its wall foundation, M_Concrete-Rectangular Beam,
  16M/10M, and a slender pile authored here from the year's structural foundation template).

    G1  a combined footing: two-way bottom mat and two-way top mat
    G2  a pile cap: bottom mat, top mat, and side bars on its four faces
    G3  a strip footing under a wall: longitudinal and transverse bars of its bottom mat
    G4  a foundation beam below the level: section rule with stirrups
    G5  a pile: a point-placed foundation reinforced as a vertical member only when the rule says axis: vertical
    G6  everything applies and reads back
    G7  the audit finds it where the rules put it
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-foundations' -Document $Document

function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzTypeId([string]$Category, [string]$Family, [string]$Name) {
    $t = @(Get-HzTypes $Category | Where-Object { [string]$_.family -eq $Family -and [string]$_.name -eq $Name })
    if ($t.Count -ne 1) { throw "HARNESS: the fixture has no single $Family : $Name type" }
    [long]$t[0].element_id
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }

Write-Host "`n== fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$footType = Get-HzTypeId 'OST_StructuralFoundation' 'M_Footing-Rectangular' '1800 x 1200 x 450mm'
$beamType = Get-HzTypeId 'OST_StructuralFraming' 'M_Concrete-Rectangular Beam' '300 x 600mm'
$X0 = 80000.0
$f1 = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'combined' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'family_instance'; type_id = $footType; level_id = $lv; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' }) })
$f2 = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'cap' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'family_instance'; type_id = $footType; level_id = $lv; point = @(($X0 + 4000.0), 0.0, 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' }) })
$beam = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'foundation-beam' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_framing'; type_id = $beamType; level_id = $lv; start = @($X0, 3000.0, -500.0); end = @(($X0 + 5000.0), 3000.0, -500.0) }) })

# A bearing wall and its strip footing: the template's first wall type and first wall-foundation type.
$wallType = @(Get-HzTypes 'OST_Walls' | Where-Object { [string]$_.family -eq 'Basic Wall' } | Select-Object -First 1)
$wfType = @(Get-HzTypes 'OST_StructuralFoundation' | Where-Object { [string]$_.family -match 'Wall Foundation|Footing' -and [string]$_.family -ne 'M_Footing-Rectangular' } | Select-Object -First 1)
$strip = $null
if ($wallType.Count -and $wfType.Count) {
    $wall = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'wall' -Arguments @{ target_document = $Document; units = 'mm'
        elements = @(, @{ kind = 'wall'; type_id = [long]$wallType[0].element_id; level_id = $lv; start = @($X0, 6000.0, 0.0); end = @(($X0 + 5000.0), 6000.0, 0.0); height = 3000.0; structural = $true }) })
    $sw = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'strip' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
        elements = @(, @{ kind = 'wall_foundation'; wall_id = $wall; type_id = [long]$wfType[0].element_id }) }
    if ($sw.Apply -and $sw.Apply.Ok) { $strip = Get-HzId $sw }
    $run.Fixture['strip_types'] = @{ wall = [string]$wallType[0].name; foundation = [string]$wfType[0].family + ' : ' + [string]$wfType[0].name }
}
$run.Fixture['elements'] = @{ combined = $f1; cap = $f2; beam = $beam; strip = $strip }

function Get-HzBox([long]$Id) {
    $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "box-$Id" -Arguments @{
        element_ids = @(, $Id); include_links = $false; include_bounding_box = $true; max_rows = 5 }
    $b = @($q.Result.rows | Where-Object { [long]$_.element_id -eq $Id })[0].bounding_box
    [pscustomobject]@{ x0 = [double]$b.min[0]; y0 = [double]$b.min[1]; z0 = [double]$b.min[2]; x1 = [double]$b.max[0]; y1 = [double]$b.max[1]; z1 = [double]$b.max[2] }
}

# THE HOST'S OWN COVER, read from the model: Revit lays a hosted bar's ends at it whatever is declared.
function Get-HzCover([long]$Id) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }
    $c = Get-HzPath @(Get-HzPath $hq.Result 'rows')[0] 'cover', 'common', 'distance_mm'
    # A host with no COMMON cover (measured: the template's wall foundation - sides 40, bottom cast
    # against earth 75) is laid from its largest face cover, the one the bottom mat lies under.
    if ($null -eq $c) {
        $fc = @(@(Get-HzPath @(Get-HzPath $hq.Result 'rows')[0] 'cover', 'faces') | ForEach-Object { Get-HzPath $_ 'cover', 'distance_mm' } | Where-Object { $null -ne $_ })
        if ($fc.Count -eq 0) { throw "HARNESS: element $Id has no readable cover on any face" }
        return [double](@($fc | Sort-Object -Descending)[0])
    }
    [double]$c
}
$covers = @{}
foreach ($e in @($f1, $f2, $strip)) { if ($e) { $covers[[string]$e] = Get-HzCover $e } }
$run.Fixture['covers'] = $covers

function New-HzMat([string]$Id, [long]$HostId, [double[]]$Face, [object[]]$Components) {
    $cv = $covers[[string]$HostId]
    foreach ($c in $Components) { $c['end_cover_mm'] = $cv; $c['side_cover_mm'] = $cv + 8; $c['offset_from_face_mm'] = $cv + $c['offset_from_face_mm'] }
    [ordered]@{ id = $Id; host = @{ element_ids = @(, $HostId) }; face_normal = $Face; components = $Components }
}
function New-HzComp([string]$Name, [double[]]$Dir, [double]$Offset, [double]$Spacing) {
    @{ name = $Name; direction = $Dir; bar_type = 'N16'; offset_from_face_mm = $Offset; end_cover_mm = 0; side_cover_mm = 0
       allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = $Spacing } }
}

# THE SIDE BARS BETWEEN THE TWO MATS (overlapping_bar, acero 3.0): from side cover + 8 the first and last
# side bars lay at the height of the mats' outer layer, whose ends run to the end cover - straight through them,
# -15.9 mm clear (refused bars_intersect, measured in all four years). The mats' steel reaches cover + 32 (the
# inner layer at + 24, a 16M); the side bars start 50 from the top and bottom faces' cover.
function New-HzSide([string]$Id, [double[]]$Face) {
    $m = New-HzMat $Id $f2 $Face @(, (New-HzComp 'h' @(0, 1, 0) 8 150))
    $m.components[0]['side_cover_mm'] = $covers[[string]$f2] + 50
    $m
}
$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'l3-foundations'; version = '1.0.0'; title = 'L3 foundations, synthetic' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    mat_rules = @(
        (New-HzMat 'G1-bot' $f1 @(0, 0, -1) @((New-HzComp 'x' @(1, 0, 0) 8 200), (New-HzComp 'y' @(0, 1, 0) 24 200))),
        (New-HzMat 'G1-top' $f1 @(0, 0, 1) @((New-HzComp 'x' @(1, 0, 0) 8 250), (New-HzComp 'y' @(0, 1, 0) 24 250))),
        (New-HzMat 'G2-bot' $f2 @(0, 0, -1) @((New-HzComp 'x' @(1, 0, 0) 8 150), (New-HzComp 'y' @(0, 1, 0) 24 150))),
        (New-HzMat 'G2-top' $f2 @(0, 0, 1) @((New-HzComp 'x' @(1, 0, 0) 8 200), (New-HzComp 'y' @(0, 1, 0) 24 200))),
        (New-HzSide 'G2-side-px' @(1, 0, 0)),
        (New-HzSide 'G2-side-nx' @(-1, 0, 0)))
    section_rules = @(, [ordered]@{
        id = 'G4'; host = @{ element_ids = @(, $beam) }; cover = @{ distance_mm = 50 }
        layers = @(@{ face = 'top'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                   @{ face = 'bottom'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'declared'; distance_mm = 50 }
            zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) } })
}
if ($strip) {
    $set['mat_rules'] += , (New-HzMat 'G3' $strip @(0, 0, -1) @((New-HzComp 'long' @(1, 0, 0) 8 200), (New-HzComp 'trans' @(0, 1, 0) 24 200)))
}

Write-Host "`n== plan ==" -ForegroundColor Cyan
$rows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
function Show-HzRows([string]$Like) {
    $r = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like })
    $bad = @($r | Where-Object { Get-HzProp $_ 'code' })
    [pscustomobject]@{ Rows = $r; Bad = $bad; Text = ("rows={0} refused={1} {2}" -f $r.Count, $bad.Count,
        ((@($bad | Select-Object -First 2 | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + (Get-HzProp $_ 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 250) })) -join ' | ')) }
}
$g1 = Show-HzRows 'G1-*'; $g2 = Show-HzRows 'G2-*'; $g3 = Show-HzRows 'G3*'; $g4 = Show-HzRows 'G4#*'
Add-HzProbe -Run $run -Id 'G1' -Name 'L3 a combined footing plans a two-way bottom mat and a two-way top mat' -Expected '4 rows, none refused' -Observed $g1.Text -Ok ($g1.Rows.Count -eq 4 -and $g1.Bad.Count -eq 0) -Evidence @{ rows = $g1.Rows }
Add-HzProbe -Run $run -Id 'G2' -Name 'L3 a pile cap plans bottom and top mats and side bars on two faces' -Expected '6 rows, none refused' -Observed $g2.Text -Ok ($g2.Rows.Count -eq 6 -and $g2.Bad.Count -eq 0) -Evidence @{ rows = $g2.Rows }
if ($strip) {
    Add-HzProbe -Run $run -Id 'G3' -Name 'L3 a strip footing under a wall plans its longitudinal and transverse bars' -Expected '2 rows, none refused' -Observed $g3.Text -Ok ($g3.Rows.Count -eq 2 -and $g3.Bad.Count -eq 0) -Evidence @{ rows = $g3.Rows }
} else {
    Add-HzProbe -Run $run -Id 'G3' -Name 'L3 a strip footing under a wall plans its longitudinal and transverse bars' -Status 'fixture_missing' -Expected 'a wall foundation' -Observed 'the template gave no wall or wall-foundation type, or create_elements refused it'
}
Add-HzProbe -Run $run -Id 'G4' -Name 'L3 a foundation beam below the level plans its top, bottom and stirrups' -Expected 'top, bottom and the stirrup zone, none refused' -Observed $g4.Text -Ok ($g4.Rows.Count -ge 3 -and $g4.Bad.Count -eq 0) -Evidence @{ rows = $g4.Rows }

if ($strip) {
    # G8: the same strip mat with its transverse layer 74 mm under the bottom - inside that face's 75 mm cover.
    $low = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l3-strip-low'; version = '1' }; bar_types = $set.bar_types
        mat_rules = @(, [ordered]@{ id = 'G8'; host = @{ element_ids = @(, $strip) }; face_normal = @(0, 0, -1)
            components = @(, @{ name = 'trans'; direction = @(0, 1, 0); bar_type = 'N16'; offset_from_face_mm = 74; end_cover_mm = 40; side_cover_mm = 48
                                allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) }
    $lrow = @(Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-low' -Arguments @{ target_document = $Document; requirement_set = $low }).Result)[0]
    Add-HzProbe -Run $run -Id 'G8' -Name 'L3 a mat layer inside the cover of its own face is refused by name, not moved by Revit in silence' `
        -Expected 'mat_rule_refused naming mat_offset_below_the_face_cover and 75 mm' `
        -Observed ([string](Get-HzProp $lrow 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $lrow 'why')) 300)) `
        -Ok ([string](Get-HzProp $lrow 'why') -match 'mat_offset_below_the_face_cover' -and [string](Get-HzProp $lrow 'why') -match '75') -Evidence @{ row = $lrow }
}

Write-Host "`n== a pile ==" -ForegroundColor Cyan
# Authored with the typed family tool from the year's structural foundation template: a 400 x 400
# prism 6000 tall. Placed at a point, it is a foundation - so P0 #8 refuses a section rule on it,
# and axis: vertical is how a rule says it is a vertical member.
$year = [int]$env:HORIZUN_REVIT_YEAR
$rft = "C:\ProgramData\Autodesk\RVT $year\Family Templates\English\Metric Structural Foundation.rft"
$rfaDir = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-pile-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $rfaDir
$pileType = $null
if (Test-Path -LiteralPath $rft) {
    $fam = Invoke-HzWrite -Run $run -Tool 'horizun_create_family' -Label 'pile-family' -AllowRefusal -Arguments @{
        target_document = $Document; template_path = $rft; output_path = (Join-Path $rfaDir 'HZ-Pile.rfa'); units = 'mm'
        types = @(, @{ name = '400x400' })
        forms = @(, @{ kind = 'extrusion'; plane = 'xy'; depth = 6000; profile = @(, @(@(-200, -200, 0), @(200, -200, 0), @(200, 200, 0), @(-200, 200, 0))) }) }
    $run.Fixture['pile_family'] = $(if ($fam.Apply) { Limit-HzText $fam.Apply.Text 300 } else { Limit-HzText $fam.Dry.Text 300 })
    $pt = @(Get-HzTypes 'OST_StructuralFoundation' | Where-Object { [string]$_.family -eq 'HZ-Pile' })
    if ($pt.Count) { $pileType = [long]$pt[0].element_id }
}
if (-not $pileType) {
    Add-HzProbe -Run $run -Id 'G5' -Name 'L3 a pile is reinforced as a vertical member when the rule says axis: vertical' -Status 'fixture_missing' `
        -Expected 'a pile family authored from the structural foundation template' -Observed ([string]$run.Fixture['pile_family'])
} else {
    $pile = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'pile' -Arguments @{ target_document = $Document; units = 'mm'
        elements = @(, @{ kind = 'family_instance'; type_id = $pileType; level_id = $lv; point = @(($X0 + 8000.0), 0.0, 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' }) })
    $run.Fixture['pile'] = $pile
    function New-HzPileSet([bool]$Vertical) {
        $r = [ordered]@{ id = 'G5'; host = @{ element_ids = @(, $pile) }; cover = @{ distance_mm = 50 }
            layers = @(, @{ face = 'perimeter'; count = 4; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
            stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'declared'; distance_mm = 50 }
                zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 250 } }) } }
        if ($Vertical) { $r['axis'] = 'vertical' }
        [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l3-pile'; version = '1' }; bar_types = $set.bar_types; section_rules = @(, $r) }
    }
    $plain = @(Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-pile-plain' -Arguments @{ target_document = $Document; requirement_set = (New-HzPileSet $false) }).Result)
    $pset = New-HzPileSet $true
    $prows = @(Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-pile' -Arguments @{ target_document = $Document; requirement_set = $pset }).Result)
    $pbad = @($prows | Where-Object { Get-HzProp $_ 'code' })
    $papply = $(if ($pbad.Count -eq 0) { Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-pile' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $pset; transaction_name = 'HZ rebar L3 pile' } } else { $null })
    $pverif = @($(if ($papply -and $papply.Apply) { @(Get-HzPath $papply.Apply.Result 'verification') }))
    $pnot = @($pverif | Where-Object { $null -ne $_ -and (Get-HzProp $_ 'verified') -ne $true })
    $paudit = $(if ($pverif.Count) { Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-pile' -Arguments @{ target_document = $Document; requirement_set = $pset } } else { $null })
    $pfind = @($(if ($paudit) { @(Get-HzPath $paudit.Result 'findings') | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'G5#*' -and [string](Get-HzProp $_ 'severity') -ne 'info' } }))
    Add-HzProbe -Run $run -Id 'G5' -Name 'L3 a pile is reinforced as a vertical member when the rule says axis: vertical, and refused when it does not' `
        -Expected 'without axis: section_rule_refused (not a structural column); with axis: vertical: planned, applied, verified, no audit error' `
        -Observed ("plain={0} vertical_rows={1} refused={2} verified={3}/{4} audit_errors={5} {6}" -f [string](Get-HzProp @($plain)[0] 'code'), $prows.Count, $pbad.Count,
            ($pverif.Count - $pnot.Count), $pverif.Count, $pfind.Count,
            $(if ($pbad.Count) { [string](Get-HzProp $pbad[0] 'rule_id') + ' ' + (Get-HzProp $pbad[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $pbad[0] 'why')) 300) }
              elseif ($pnot.Count) { [string](Get-HzProp $pnot[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $pnot[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 500) }
              elseif ($pverif.Count -eq 0) { 'apply: ' + (Limit-HzText $(if ($papply -and $papply.Apply) { $papply.Apply.Text } elseif ($papply) { $papply.Dry.Text } else { 'not run' }) 500) }
              else { (($pfind | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ') })) `
        -Ok ([string](Get-HzProp @($plain)[0] 'code') -eq 'section_rule_refused' -and $pbad.Count -eq 0 -and $pverif.Count -ge 3 -and $pnot.Count -eq 0 -and $pfind.Count -eq 0) `
        -Evidence @{ plain = $plain; rows = $prows; verification = $pverif; findings = $pfind }
}

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar L3 foundations' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = @($(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') }))
$notOk = @($verif | Where-Object { $null -ne $_ -and (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'G6' -Name 'L3 the foundations apply and every set reads back' -Expected 'every set verified' `
    -Observed ("applied={0} verified={1}/{2} first_failure={3}" -f $applyOk, ($verif.Count - $notOk.Count), $verif.Count,
        $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600) }
          elseif (-not $applyOk) { Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 500 } else { '' })) `
    -Ok ($applyOk -and $verif.Count -gt 0 -and $notOk.Count -eq 0) -Evidence @{ verification = $verif }
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$ours = @(@(Get-HzPath $audit.Result 'findings') | Where-Object { [string](Get-HzProp $_ 'rule_id') -match '^G[1-4]' -and [string](Get-HzProp $_ 'severity') -ne 'info' })
Add-HzProbe -Run $run -Id 'G7' -Name 'L3 the audit finds the foundations where the rules put them' -Expected 'no error or unknown finding on G1-G4' `
    -Observed ("findings={0} {1}" -f $ours.Count, (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $ours; observed = @($verif | Where-Object { $_ -and [string](Get-HzProp $_ 'rule_id') -match '^(G4#.*stirrups|G3#trans)' }) }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
