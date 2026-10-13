#Requires -Version 5.1
<#
  REFUERZO DESDE CARTILLA, F1.2, LIVE: a concrete column reinforced by a section rule
  (four corner bars, one intermediate bar on two faces, ties at 150 up its height),
  and an isolated footing with a two-way bottom mat.

  Columns and footings had never been reinforced live. Needs a DISPOSABLE fixture built
  from Autodesk's metric multi-discipline template (M_Concrete-Rectangular-Column,
  M_Footing-Rectangular, 10M/16M, Stirrup/Tie - 135 deg) open and active (-Document),
  and a second document open.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-columns-footings' -Document $Document

function Get-HzTypeId([string]$Category, [string]$Family, [string]$Name) {
    $t = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "type-$Family" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
        Where-Object { $_.is_element_type -and [string]$_.family -eq $Family -and [string]$_.name -eq $Name })
    if ($t.Count -ne 1) { throw "HARNESS: the fixture has no single $Family : $Name type" }
    [long]$t[0].element_id
}

Write-Host "`n== fixture: a column and a footing ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colType = Get-HzTypeId 'OST_StructuralColumns' 'M_Concrete-Rectangular-Column' '450 x 600mm'
$footType = Get-HzTypeId 'OST_StructuralFoundation' 'M_Footing-Rectangular' '1800 x 1200 x 450mm'
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'column' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @(20000.0, 0.0, 0.0); coordinate_mode = 'level_offset' }) }
$column = [long](Get-HzProp @($made.Apply.Result.rows)[0] 'element_id')
$foot = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'footing' -AllowRefusal -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'family_instance'; type_id = $footType; level_id = $lv; point = @(30000.0, 0.0, 0.0); coordinate_mode = 'level_offset'; structural_type = 'Footing' }) }
$footing = $(if ($foot.Apply -and $foot.Apply.Ok) { [long](Get-HzProp @($foot.Apply.Result.rows)[0] 'element_id') } else { $null })
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'mark' -Arguments @{
    target_document = $Document; writes = @(, @{ target_id = $column; parameter = 'Mark'; value = 'C-1' }) }
$run.Fixture['column'] = $column; $run.Fixture['footing'] = $footing

# FROM THE MODEL, not from one year's template: Autodesk names the 135 degree stirrup hook
# with and without a closing period, and the 2024 multi-discipline template gives a footing
# 38.1 mm of cover where 2025-2027 give 40. Both are read here; nothing else adapts.
$hooks = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'hook-types' -Arguments @{
    categories = @('OST_Rebar'); include_links = $false; include_types = $true; max_rows = 500 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'Rebar Hook' -and [string]$_.name -match '^Stirrup/Tie - 135 deg\.?$' })
if ($hooks.Count -ne 1) { throw "HARNESS: the fixture has $($hooks.Count) 'Stirrup/Tie - 135 deg' hook types, not one" }
$hook = [string]$hooks[0].name
$footCover = $null
if ($footing) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'footing-cover' -Arguments @{
        mode = 'hosts'; element_ids = @(, $footing); max_rows = 5 }
    $footCover = Get-HzPath @(Get-HzPath $hq.Result 'rows')[0] 'cover', 'common', 'distance_mm'
    if ($null -eq $footCover) { throw 'HARNESS: the footing has no common cover to lay its mat from' }
    $footCover = [double]$footCover
}
$run.Fixture['hook'] = $hook; $run.Fixture['footing_cover_mm'] = $footCover
$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'cartilla-c1'; version = '1.0.0'; title = 'C-1 and F-1 from a synthetic schedule' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(, @{ id = 'H135'; type_name = $hook })
    section_rules = @(, [ordered]@{
        id = 'C1'; host = @{ category = 'OST_StructuralColumns'; mark = 'C-1' }
        cover = @{ source = 'host' }
        layers = @(
            @{ face = 'perimeter'; count = 4; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true; mark = 'L1' },
            @{ face = 'left'; count = 1; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true; mark = 'L1' },
            @{ face = 'right'; count = 1; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true; mark = 'L1' })
        stirrups = @{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }; mark = 'E1'
                      start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
                      zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } })
}
if ($footing) {
    $set['mat_rules'] = @(, [ordered]@{
        id = 'F1'; host = @{ element_ids = @($footing) }; face_normal = @(0, 0, -1)
        components = @(
            # The footing's own cover, plus half a 16M bar for the first layer and a bar and a half for the second.
            @{ name = 'bottom_x'; direction = @(1, 0, 0); bar_type = 'N16'; offset_from_face_mm = $footCover + 8; end_cover_mm = $footCover; side_cover_mm = $footCover + 8
               allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } },
            @{ name = 'bottom_y'; direction = @(0, 1, 0); bar_type = 'N16'; offset_from_face_mm = $footCover + 24; end_cover_mm = $footCover; side_cover_mm = $footCover + 8
               allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) })
}

Write-Host "`n== plan ==" -ForegroundColor Cyan
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }
$rows = @(Get-HzPath $plan.Result 'reinforcement')
$refused = @($rows | Where-Object { Get-HzProp $_ 'code' })
$colRows = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'C1#*' -and -not (Get-HzProp $_ 'code') })
$footRows = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'F1#*' -and -not (Get-HzProp $_ 'code') })
Add-HzProbe -Run $run -Id 'C1' -Name 'F1.2 a column section plans its corner bars, its face bars and its ties up the height' `
    -Expected 'C1 rows: perimeter-top, perimeter-bottom, left, right and the tie zone, none refused' `
    -Observed ("column_rows={0} refused={1} first={2}" -f $colRows.Count, $refused.Count,
        $(if ($refused.Count) { [string](Get-HzProp $refused[0] 'rule_id') + ' ' + (Get-HzProp $refused[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $refused[0] 'why')) 200) } else { '' })) `
    -Ok ($colRows.Count -eq 5 -and @($refused | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'C1#*' }).Count -eq 0) `
    -Evidence @{ rows = $colRows; refused = $refused }
if ($footing) {
    Add-HzProbe -Run $run -Id 'F1' -Name 'F1.2 an isolated footing plans a two-way bottom mat' `
        -Expected 'F1#bottom_x and F1#bottom_y planned, none refused' `
        -Observed ("footing_rows={0} refused={1} {2}" -f $footRows.Count, @($refused | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'F1*' }).Count,
            (@($refused | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'F1*' } | ForEach-Object { [string](Get-HzProp $_ 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 300) }) -join '; ')) `
        -Ok ($footRows.Count -eq 2) -Evidence @{ rows = $footRows }
} else {
    Add-HzProbe -Run $run -Id 'F1' -Name 'F1.2 an isolated footing plans a two-way bottom mat' -Status 'fixture_missing' `
        -Expected 'a footing to reinforce' -Observed ('create_elements refused it: ' + (Limit-HzText $(if ($foot.Apply) { $foot.Apply.Text } else { $foot.Dry.Text }) 200))
}

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{
    target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar columns footings' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = $(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') } else { @() })
$colVerified = @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'C1#*' -and (Get-HzProp $_ 'verified') -eq $true })
$footVerified = @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'F1#*' -and (Get-HzProp $_ 'verified') -eq $true })
Add-HzProbe -Run $run -Id 'C2' -Name 'F1.2 the column cage applies and every set reads back' `
    -Expected '5 column sets verified' `
    -Observed ("applied={0} column_verified={1}/{2} text={3}" -f $applyOk, $colVerified.Count, @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'C1#*' }).Count,
        (Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 200)) `
    -Ok ($colVerified.Count -eq 5) -Evidence @{ verification = @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'C1#*' }) }
if ($footing) {
    Add-HzProbe -Run $run -Id 'F2' -Name 'F1.2 the footing mat applies and both layers read back' `
        -Expected '2 footing sets verified' -Observed ("footing_verified={0}" -f $footVerified.Count) `
        -Ok ($footVerified.Count -eq 2) -Evidence @{ verification = @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'F1#*' }) }
}
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$findings = @(Get-HzPath $audit.Result 'findings')
$colLong = @($findings | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'C1#*' -and [string](Get-HzProp $_ 'rule_id') -notlike '*stirrups*' })
Add-HzProbe -Run $run -Id 'C3' -Name 'F1.2 the audit finds the column bars where the rule put them' `
    -Expected 'no audit finding on the column longitudinal sets' `
    -Observed ("findings={0} {1}" -f $colLong.Count, (($colLong | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($colLong.Count -eq 0) -Evidence @{ findings = $findings }

# BACKLOG P0 #8 (2026-10-10): a section rule aimed at the FOOTING. A point-placed host was
# framed as a column whatever it was, so a bottom layer became short vertical bars inside a
# side face that the plan accepted. It must be refused by name and build nothing.
if ($footing) {
    $set2 = [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = 'cartilla-f1s'; version = '1.0.0'; title = 'a section rule aimed at a footing' }
        bar_types = @(, @{ id = 'N16'; type_name = '16M' })
        section_rules = @(, [ordered]@{
            id = 'FS'; host = @{ element_ids = @($footing) }; cover = @{ source = 'host' }
            layers = @(, @{ face = 'bottom'; count = 5; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true }) })
    }
    $p2 = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-footing-section' -Arguments @{ target_document = $Document; requirement_set = $set2 }
    $fs = @(@(Get-HzPath $p2.Result 'reinforcement') | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'FS*' })
    $refusedFs = @($fs | Where-Object { [string](Get-HzProp $_ 'code') -eq 'section_rule_refused' -and [string](Get-HzProp $_ 'why') -match 'not a structural column' })
    $buildable = [int](Get-HzPath $p2.Result 'summary', 'rebar_rows_buildable')
    Add-HzProbe -Run $run -Id 'F3' -Name 'P0 #8 a section rule on a footing is refused by name, not laid as a column' `
        -Expected 'section_rule_refused "not a structural column"; 0 buildable rows' `
        -Observed ("refused={0}/{1} buildable={2} why={3}" -f $refusedFs.Count, $fs.Count, $buildable,
            (Limit-HzText $(if ($fs.Count) { [string](Get-HzProp $fs[0] 'why') } else { '' }) 160)) `
        -Ok ($refusedFs.Count -ge 1 -and $buildable -eq 0) -Evidence @{ rows = $fs }
}

Write-Host "`n== F1.3 reconcile the column against its schedule ==" -ForegroundColor Cyan
# The schedule a detailer would have written for this column, taken here from the design
# intent: six 16M bars (L1) and the ties (E1) - lengths per bar as the model measured them,
# because the probe is about the comparison, not about re-deriving a schedule.
$qty = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'quantities' -Arguments @{
    mode = 'quantities'; host_id = $column; group_by = @('mark') }).Result
$byMark = @{}
foreach ($g in @(Get-HzPath $qty 'groups')) { $byMark[[string](Get-HzPath $g 'group', 'mark')] = $g }
$schedule = @()
if ($byMark.ContainsKey('L1') -and $byMark.ContainsKey('E1')) {
    foreach ($mk in 'L1', 'E1') {
        $g = $byMark[$mk]; $n = [int](Get-HzProp $g 'bars')
        $schedule += @{ mark = $mk; bar_type = $(if ($mk -eq 'L1') { '16M' } else { '10M' }); count = $n
                        length_mm = [Math]::Round([double](Get-HzProp $g 'total_length_m') * 1000.0 / $n, 3) }
    }
}
$rec = $(if ($schedule.Count -eq 2) { (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'reconcile' -Arguments @{
    mode = 'reconcile'; host_id = $column; schedule = $schedule; tolerances = @{ length_pct = 0.5 } }).Result } else { $null })
Add-HzProbe -Run $run -Id 'R1' -Name 'F1.3 the column reconciles with its schedule: six 16M (L1) and its ties (E1)' `
    -Expected 'verdict agrees; L1 count 6' `
    -Observed $(if ($rec) { "verdict={0} L1_bars={1} marks={2}" -f (Get-HzProp $rec 'verdict'), (Get-HzProp $byMark['L1'] 'bars'), (Get-HzPath $rec 'summary', 'marks') } else { 'marks L1/E1 not found in the takeoff: ' + (($byMark.Keys) -join ',') }) `
    -Ok ($null -ne $rec -and (Get-HzProp $rec 'verdict') -eq 'agrees' -and [int](Get-HzProp $byMark['L1'] 'bars') -eq 6) -Evidence @{ reconcile = $rec }
if ($schedule.Count -eq 2) {
    $wrong = @(@{ mark = 'L1'; bar_type = '16M'; count = 7; length_mm = $schedule[0].length_mm }, $schedule[1])
    $rec2 = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'reconcile-wrong' -Arguments @{
        mode = 'reconcile'; host_id = $column; schedule = $wrong; tolerances = @{ length_pct = 0.5 } }).Result
    $l1 = @(@(Get-HzProp $rec2 'rows') | Where-Object { [string](Get-HzProp $_ 'mark') -eq 'L1' })
    $fields = @($(if ($l1.Count) { @(Get-HzProp $l1[0] 'differences') | ForEach-Object { [string](Get-HzProp $_ 'field') } }))
    Add-HzProbe -Run $run -Id 'R2' -Name 'F1.3 a schedule that asks for one bar more is a difference on that mark only' `
        -Expected 'L1 differs on count and total length; E1 agrees' `
        -Observed ("verdict={0} L1_fields={1}" -f (Get-HzProp $rec2 'verdict'), ($fields -join ',')) `
        -Ok ((Get-HzProp $rec2 'verdict') -eq 'differences_found' -and $fields -contains 'count' -and [int](Get-HzPath $rec2 'summary', 'agrees') -eq 1) `
        -Evidence @{ reconcile = $rec2 }
}

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
