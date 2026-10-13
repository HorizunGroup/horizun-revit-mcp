#Requires -Version 5.1
<#
  ACERO 3.0, L2, LIVE: column detailing on a DISPOSABLE fixture (Autodesk metric template:
  M_Concrete-Rectangular-Column 450 x 600mm, 16M/10M, Stirrup/Tie 135 and 90 degree hooks).

    K1  confinement zones: closed stirrups at 100 over 600 mm at each end, 200 between
    K2  crossties across the width at every intermediate side bar, 135 at one end, 90 at the other
    K3  the cage applies and every set reads back
    K4  the audit finds the cage where the rule put it
    K5  a bayoneta: the column bar cranks 50 mm inward over 150 mm and runs on into the column above
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-column-detail' -Document $Document

function Get-HzTypeId([string]$Category, [string]$Family, [string]$Name) {
    $t = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "type-$Family" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
        Where-Object { $_.is_element_type -and [string]$_.family -eq $Family -and [string]$_.name -eq $Name })
    if ($t.Count -ne 1) { throw "HARNESS: the fixture has no single $Family : $Name type" }
    [long]$t[0].element_id
}
function Get-HzHookName([string]$Pattern) {
    $h = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "hook-$Pattern" -Arguments @{
        categories = @('OST_Rebar'); include_links = $false; include_types = $true; max_rows = 500 }).Result.rows |
        Where-Object { $_.is_element_type -and [string]$_.family -eq 'Rebar Hook' -and [string]$_.name -match $Pattern })
    if ($h.Count -ne 1) { throw "HARNESS: the fixture has $($h.Count) hook types matching $Pattern, not one" }
    [string]$h[0].name
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }

Write-Host "`n== fixture: two stacked columns, and a third ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colType = Get-HzTypeId 'OST_StructuralColumns' 'M_Concrete-Rectangular-Column' '450 x 600mm'
$lvZ = 0.0
try { $lvZ = [double]$level.bounding_box.min[2] } catch { }
$lvl2 = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'level-2' -Arguments @{
    target_document = $Document; units = 'mm'; elements = @(, @{ kind = 'level'; elevation = ($lvZ + 3000.0); name = 'HZ L2 upper' }) }
$lv2 = [long](Get-HzProp @($lvl2.Apply.Result.rows)[0] 'element_id')
$X0 = 60000.0
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'columns' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv; point = @(($X0 + 3000.0), 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
        @{ kind = 'structural_column'; type_id = $colType; level_id = $lv2; point = @(($X0 + 3000.0), 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }
$cols = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($cols.Count -ne 3) { throw "HARNESS: three columns were not created ($($cols.Count))" }
$k1 = $cols[0]; $k5 = $cols[1]; $k5up = $cols[2]
$run.Fixture['columns'] = @{ cage = $k1; cranked = $k5; above = $k5up }
$h135 = Get-HzHookName '^Stirrup/Tie - 135 deg\.?$'
$h90 = Get-HzHookName '^Stirrup/Tie - 90 deg\.?$'
$run.Fixture['hooks'] = @{ h135 = $h135; h90 = $h90 }

$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'l2-column-detail'; version = '1.0.0'; title = 'L2 column detailing, synthetic' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(@{ id = 'H135'; type_name = $h135 }, @{ id = 'H90'; type_name = $h90 })
    section_rules = @(
        [ordered]@{
            id = 'K1'; host = @{ element_ids = @(, $k1) }; cover = @{ distance_mm = 40 }
            layers = @(
                @{ face = 'perimeter'; count = 4; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                @{ name = 'side-l'; face = 'left'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                @{ name = 'side-r'; face = 'right'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
            stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; symmetric = $true; cover = @{ source = 'declared'; distance_mm = 40 }
                start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
                zones = @(@{ name = 'end'; length_mm = 600; layout = @{ rule = 'maximum_spacing'; spacing_mm = 100; include_last_bar = $false } },
                          @{ name = 'middle'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200; include_last_bar = $false } }) }
            ties = @(, [ordered]@{ name = 'x'; bar_type = 'N10'; layer = 'side-l'; allow_new_shape = $true
                start = @{ hook_type = 'H135'; orientation = 'right' }; end = @{ hook_type = 'H90'; orientation = 'right' } })
        },
        [ordered]@{
            id = 'K5'; host = @{ element_ids = @(, $k5) }; cover = @{ distance_mm = 40 }
            layers = @(, [ordered]@{ name = 'face-top'; face = 'top'; count = 3; bar_type = 'N16'; allow_new_shape = $true
                extend = @{ from_mm = 40; to_mm = 3800 }; into = @(, $k5up)
                crank = @{ at_mm = 2700; run_mm = 150; inward_mm = 50 } })
        })
}

Write-Host "`n== plan ==" -ForegroundColor Cyan
$rows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
$bad = @($rows | Where-Object { Get-HzProp $_ 'code' })
$zoneRows = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like "K1#h$k1#stirrups#*" })
$tieRows = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like "K1#h$k1#tie-x-*" })
$crank = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like "K5#*" })[0]
$badText = (@($bad | Select-Object -First 3 | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + (Get-HzProp $_ 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 240) }) -join ' | ')
Add-HzProbe -Run $run -Id 'K1' -Name 'L2 confinement zones: 100 over 600 mm at each end, 200 between, symmetric' `
    -Expected 'three stirrup zone rows (end, middle, end mirrored), none refused' `
    -Observed ("zones={0} ids={1} refused={2} {3}" -f $zoneRows.Count, ((@($zoneRows | ForEach-Object { ([string](Get-HzProp $_ 'rule_id')).Split('#')[-1] })) -join ','), $bad.Count, $badText) `
    -Ok ($zoneRows.Count -ge 3 -and $bad.Count -eq 0) -Evidence @{ rows = $zoneRows }
Add-HzProbe -Run $run -Id 'K2' -Name 'L2 crossties across the width at every intermediate side bar, at the stirrup stations' `
    -Expected 'two tie bars x (zone rows), each open, 135 at the start and 90 at the end' `
    -Observed ("tie_rows={0} first={1}" -f $tieRows.Count, $(if ($tieRows.Count) { [string](Get-HzProp $tieRows[0] 'rule_id') + ' closed=' + [string](Get-HzProp $tieRows[0] 'closed') } else { '' })) `
    -Ok ($tieRows.Count -ge 2 -and @($tieRows | Where-Object { (Get-HzProp $_ 'closed') -ne $false }).Count -eq 0) -Evidence @{ rows = $tieRows }

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{
    target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar L2 column detail' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = $(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') } else { @() })
$notOk = @($verif | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'K3' -Name 'L2 the column cage, its confinement zones, its crossties and the cranked bars apply and read back' `
    -Expected 'every set verified' `
    -Observed ("applied={0} verified={1}/{2} first_failure={3}" -f $applyOk, ($verif.Count - $notOk.Count), $verif.Count,
        $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 700) }
          elseif (-not $applyOk) { Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 500 } else { '' })) `
    -Ok ($applyOk -and $verif.Count -gt 0 -and $notOk.Count -eq 0) -Evidence @{ verification = $verif }

$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$findings = @(Get-HzPath $audit.Result 'findings')
$ours = @($findings | Where-Object { [string](Get-HzProp $_ 'rule_id') -match '^K[15]#' -and [string](Get-HzProp $_ 'severity') -ne 'info' })
Add-HzProbe -Run $run -Id 'K4' -Name 'L2 the audit finds the cage where the rule put it' `
    -Expected 'no error or unknown finding on K1 or K5' `
    -Observed ("findings={0} {1}" -f $ours.Count, (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $findings }
$crankVerified = @($verif | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'K5#*' -and (Get-HzProp $_ 'verified') -eq $true })
Add-HzProbe -Run $run -Id 'K5' -Name 'L2 a bayoneta cranks the bars inward and runs them on into the column above' `
    -Expected 'K5 planned (4-point centreline, inside the two columns), applied and verified' `
    -Observed ("plan={0} points={1} applied_verified={2}" -f $(if ($crank) { if (Get-HzProp $crank 'code') { [string](Get-HzProp $crank 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $crank 'why')) 300) } else { 'ok' } } else { 'no row' }),
        $(if ($crank -and -not (Get-HzProp $crank 'code')) { @(Get-HzProp $crank 'curve_mm').Count } else { 0 }), $crankVerified.Count) `
    -Ok ($null -ne $crank -and -not (Get-HzProp $crank 'code') -and @(Get-HzProp $crank 'curve_mm').Count -eq 4 -and $crankVerified.Count -eq 1) -Evidence @{ row = $crank }

Write-Host "`n== a circular column ==" -ForegroundColor Cyan
# The template's round concrete column, by family name; its smallest type that is at least 400 mm.
$round = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'round-types' -Arguments @{
    categories = @('OST_StructuralColumns'); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -match 'Round' -and [string]$_.family -match 'Concrete' })
$run.Fixture['round_types'] = @($round | ForEach-Object { [string]$_.family + ' : ' + [string]$_.name })
if ($round.Count -eq 0) {
    # NONE IN THE TEMPLATE (measured: Revit 2024's metric project template has no round concrete
    # column). One is authored here with the typed family tool: the year's structural column
    # template and a 360 degree revolution of a 250 x 3000 rectangle - a true cylinder.
    $year = [int]$env:HORIZUN_REVIT_YEAR
    $rft = "C:\ProgramData\Autodesk\RVT $year\Family Templates\English\Metric Structural Column.rft"
    $rfaDir = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-round-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $null = New-Item -ItemType Directory -Path $rfaDir
    if (Test-Path -LiteralPath $rft) {
        $fam = Invoke-HzWrite -Run $run -Tool 'horizun_create_family' -Label 'round-family' -AllowRefusal -Arguments @{
            target_document = $Document; template_path = $rft; output_path = (Join-Path $rfaDir 'HZ-Round-Column.rfa'); units = 'mm'
            types = @(, @{ name = '500mm' })
            forms = @(, @{ kind = 'revolution'; plane = 'xz'; profile = @(, @(@(0, 0, 0), @(250, 0, 0), @(250, 0, 3000), @(0, 0, 3000)))
                           axis_start = @(0, 0, 0); axis_end = @(0, 0, 3000); start_angle_degrees = 0; end_angle_degrees = 360 }) }
        $run.Fixture['round_family'] = $(if ($fam.Apply) { Limit-HzText $fam.Apply.Text 300 } else { Limit-HzText $fam.Dry.Text 300 })
        $round = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'round-types-2' -Arguments @{
            categories = @('OST_StructuralColumns'); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
            Where-Object { $_.is_element_type -and [string]$_.family -eq 'HZ-Round-Column' })
    }
}
if ($round.Count -eq 0) {
    Add-HzProbe -Run $run -Id 'K6' -Name 'L2 a circular column: six bars on a ring inside a circular hoop' -Status 'fixture_missing' `
        -Expected 'a round concrete column type in the template' -Observed 'none found'
} else {
    $rt = [long]$round[0].element_id
    $rc = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'round-column' -Arguments @{
        target_document = $Document; units = 'mm'
        elements = @(, @{ kind = 'structural_column'; type_id = $rt; level_id = $lv; point = @(($X0 + 6000.0), 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }
    $k6 = [long](Get-HzProp @($rc.Apply.Result.rows)[0] 'element_id')
    $run.Fixture['round_column'] = @{ id = $k6; type = [string]$round[0].name }
    $rset = [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = 'l2-round'; version = '1.0.0'; title = 'L2 circular column, synthetic' }
        bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
        hook_types = @(, @{ id = 'H135'; type_name = $h135 })
        section_rules = @(, [ordered]@{
            id = 'K6'; host = @{ element_ids = @(, $k6) }; cover = @{ distance_mm = 40 }
            layers = @(, @{ name = 'ring'; face = 'ring'; count = 6; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        })
    }
    # The hoop: planned on its own, and refused by name (measured: Revit will not build an arc chain from curves).
    $hset = [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = 'l2-hoop'; version = '1.0.0'; title = 'L2 circular hoop, synthetic' }
        bar_types = @(, @{ id = 'N10'; type_name = '10M' })
        section_rules = @(, [ordered]@{
            id = 'K7'; host = @{ element_ids = @(, $k6) }; cover = @{ distance_mm = 40 }
            stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'declared'; distance_mm = 40 }
                zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } })
    }
    $hrows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-hoop' -Arguments @{ target_document = $Document; requirement_set = $hset }).Result
    $hcodes = @($hrows | ForEach-Object { [string](Get-HzProp $_ 'code') })
    Add-HzProbe -Run $run -Id 'K7' -Name 'L2 a circular hoop is refused by name, before any transaction (Revit will not build an arc chain from curves)' `
        -Expected 'every hoop row arc_chain_not_buildable_from_curves' -Observed ("rows={0} codes={1}" -f $hrows.Count, ($hcodes -join ',')) `
        -Ok ($hrows.Count -ge 1 -and @($hcodes | Where-Object { $_ -ne 'arc_chain_not_buildable_from_curves' }).Count -eq 0) -Evidence @{ rows = $hrows }
    $rrows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-round' -Arguments @{ target_document = $Document; requirement_set = $rset }).Result
    $rbad = @($rrows | Where-Object { Get-HzProp $_ 'code' })
    $rapply = $(if ($rbad.Count -eq 0) { Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-round' -AllowRefusal -Arguments @{
        target_document = $Document; requirement_set = $rset; transaction_name = 'HZ rebar L2 round column' } } else { $null })
    $rverif = @($(if ($rapply -and $rapply.Apply) { @(Get-HzPath $rapply.Apply.Result 'verification') }))
    $rnot = @($rverif | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    $raudit = $(if ($rverif.Count) { Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-round' -Arguments @{ target_document = $Document; requirement_set = $rset } } else { $null })
    $rfind = @($(if ($raudit) { @(Get-HzPath $raudit.Result 'findings') | Where-Object { [string](Get-HzProp $_ 'rule_id') -like 'K6#*' -and [string](Get-HzProp $_ 'severity') -ne 'info' } }))
    Add-HzProbe -Run $run -Id 'K6' -Name 'L2 a circular column: six bars evenly on a ring, read off the circular solid' `
        -Expected '6 ring bars planned, applied, verified, and no audit error' `
        -Observed ("type={0} rows={1} refused={2} verified={3}/{4} audit_errors={5} {6}" -f [string]$round[0].name, $rrows.Count, $rbad.Count,
            ($rverif.Count - $rnot.Count), $rverif.Count, $rfind.Count,
            $(if ($rbad.Count) { [string](Get-HzProp $rbad[0] 'rule_id') + ' ' + (Get-HzProp $rbad[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $rbad[0] 'why')) 300) }
              elseif ($rverif.Count -eq 0 -or $null -eq $rverif[0]) { 'apply: ' + (Limit-HzText $(if ($rapply -and $rapply.Apply) { $rapply.Apply.Text } elseif ($rapply) { $rapply.Dry.Text } else { 'not run' }) 900) }
              elseif ($rnot.Count) { [string](Get-HzProp $rnot[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $rnot[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600) }
              else { (($rfind | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ') })) `
        -Ok ($rbad.Count -eq 0 -and $rverif.Count -eq 6 -and $rnot.Count -eq 0 -and $rfind.Count -eq 0) -Evidence @{ rows = $rrows; verification = $rverif; findings = $rfind }
}

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
