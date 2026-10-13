#Requires -Version 5.1
<#
  ACERO 3.0, L7, LIVE, on a DISPOSABLE fixture: a round column authored with the typed family tool
  (the year's structural column template, a 360-degree revolution), and a pair of stacked beams.

    P1  every M_T shape in the model is judged by its DEFINITION: the circle ones plan as a placed
        hoop, the others are refused as shape_is_not_a_circle
    P2  a hoop placed from a lapped-circle shape applies and reads back on its radius
    P3  a spiral (M_SP) applies with its pitch, turns and height read back
    P4  a beam layer spliced with a coupler: both pieces and the coupler read back
    P5  the audit finds the placed shapes and the coupler as declared
    P6  a layer with two couplers: three pieces, the middle one coupled at both of its ends, read back
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-placed' -Document $Document
function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 500 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }

Write-Host "`n== fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$year = [int]$env:HORIZUN_REVIT_YEAR
$rft = "C:\ProgramData\Autodesk\RVT $year\Family Templates\English\Metric Structural Column.rft"
$rfaDir = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-round-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $rfaDir
$null = Invoke-HzWrite -Run $run -Tool 'horizun_create_family' -Label 'round-family' -Arguments @{
    target_document = $Document; template_path = $rft; output_path = (Join-Path $rfaDir 'HZ-Round-Column.rfa'); units = 'mm'
    types = @(, @{ name = '500mm' })
    forms = @(, @{ kind = 'revolution'; plane = 'xz'; profile = @(, @(@(0, 0, 0), @(250, 0, 0), @(250, 0, 3000), @(0, 0, 3000)))
                   axis_start = @(0, 0, 0); axis_end = @(0, 0, 3000); start_angle_degrees = 0; end_angle_degrees = 360 }) }
$rt = @(Get-HzTypes 'OST_StructuralColumns' | Where-Object { [string]$_.family -eq 'HZ-Round-Column' })[0]
$X0 = 190000.0
$cols = @(@((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'round-columns' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_column'; type_id = [long]$rt.element_id; level_id = $lv; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
        @{ kind = 'structural_column'; type_id = [long]$rt.element_id; level_id = $lv; point = @(($X0 + 3000), 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 }) }).Apply.Result.rows) |
    ForEach-Object { [long](Get-HzProp $_ 'element_id') })
$beamT = @(Get-HzTypes 'OST_StructuralFraming' | Where-Object { [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })[0]
$beam = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beam' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @(($X0 + 6000), 0.0, 3000.0); end = @(($X0 + 12000), 0.0, 3000.0) }) })
$beam2 = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beam2' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @(($X0 + 6000), 4000.0, 3000.0); end = @(($X0 + 12000), 4000.0, 3000.0) }) })
$shapes = @(Get-HzTypes 'OST_Rebar' | Where-Object { [string]$_.family -eq 'Rebar Shape' } | ForEach-Object { [string]$_.name })
$tShapes = @($shapes | Where-Object { $_ -match '^M_T\d+$' })
$couplers = @(Get-HzTypes 'OST_Coupler' | ForEach-Object { [string]$_.name })
$run.Fixture['elements'] = @{ round = $cols; beam = $beam; t_shapes = $tShapes; couplers = $couplers }

function New-HzHoopSet([string]$Id, [long]$Col, [string]$Shape) {
    [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = $Id; version = '1' }
        bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
        section_rules = @(, [ordered]@{ id = $Id; host = @{ element_ids = @(, $Col) }; cover = @{ source = 'host' }
            layers = @(, @{ name = 'ring'; face = 'ring'; count = 6; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
            stirrups = [ordered]@{ bar_type = 'N10'; cover = @{ source = 'host' }; shape_placement = @{ shape = $Shape; kind = 'lapped_circle' }
                zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) } }) }
}

Write-Host "`n== P1: which shapes are circles ==" -ForegroundColor Cyan
$circle = $null; $verdicts = @()
foreach ($sh in $tShapes) {
    $rows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label "plan-$sh" -Arguments @{ target_document = $Document; requirement_set = (New-HzHoopSet "P1$sh" $cols[0] $sh) }).Result
    $bad = @($rows | Where-Object { Get-HzProp $_ 'code' })
    $codes = @($bad | ForEach-Object { [string](Get-HzProp $_ 'code') + ':' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 900) } | Select-Object -Unique)
    $verdicts += "$sh=" + $(if ($bad.Count -eq 0) { 'circle' } else { $codes -join ',' })
    if ($bad.Count -eq 0 -and -not $circle) { $circle = $sh }
}
Add-HzProbe -Run $run -Id 'P1' -Name 'L7 every M_T shape is judged by its definition, not its name' -Expected 'at least one plans as a circle; the rest refused by name' `
    -Observed ($verdicts -join ' | ') -Ok ($null -ne $circle -and @($verdicts | Where-Object { $_ -notmatch '=circle$' -and $_ -notmatch 'shape_is_not_a_circle' }).Count -eq 0) -Evidence @{ verdicts = $verdicts }

$set = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = 'l7-placed'; version = '1.0.0' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' }) }
$rules = @()
if ($circle) { $rules += (New-HzHoopSet 'P2' $cols[0] $circle).section_rules[0] }
if ($shapes -contains 'M_SP') {
    $rules += [ordered]@{ id = 'P3'; host = @{ element_ids = @(, $cols[1]) }; cover = @{ source = 'host' }
        layers = @(, @{ name = 'ring'; face = 'ring'; count = 6; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = [ordered]@{ bar_type = 'N10'; cover = @{ source = 'host' }; start_offset_mm = 50; end_offset_mm = 50
            shape_placement = @{ shape = 'M_SP'; kind = 'spiral'; pitch_mm = 75; base_finishing_turns = 1; top_finishing_turns = 1 }
            zones = @(, @{ name = 'all'; layout = @{ rule = 'single' } }) } }
}
if ($couplers.Count) {
    $rules += [ordered]@{ id = 'P4'; host = @{ element_ids = @(, $beam) }; cover = @{ source = 'host' }
        layers = @(, @{ name = 'bot'; face = 'bottom'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true
            laps = @(, @{ at_ratio = 0.5; coupler_type_name = $(if ($couplers -contains 'CPL16M') { 'CPL16M' } else { $couplers[0] }) }) }) }
}
if ($couplers.Count) {
    # P6 (stage 1b, lot 5): TWO couplers in one layer - its middle piece is coupled at both of its ends.
    $cpl = $(if ($couplers -contains 'CPL16M') { 'CPL16M' } else { $couplers[0] })
    $rules += [ordered]@{ id = 'P6'; host = @{ element_ids = @(, $beam2) }; cover = @{ source = 'host' }
        layers = @(, @{ name = 'top'; face = 'top'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true
            laps = @(@{ at_ratio = 0.33; coupler_type_name = $cpl }, @{ at_ratio = 0.66; coupler_type_name = $cpl }) }) }
}
$set['section_rules'] = $rules

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
# The plan's own refusals, whole, before the apply that would only name their codes.
$planRows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-set' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
$run.Fixture['plan_refusals'] = @($planRows | Where-Object { Get-HzProp $_ 'code' } | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + [string](Get-HzProp $_ 'code') + ': ' + [string](Get-HzProp $_ 'why') })
foreach ($r in $run.Fixture['plan_refusals']) { Write-Host "  plan refused: $r" }
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar L7 placed' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = @($(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') }))
function Show-HzVerif([string]$Like, [string]$Id, [string]$Name, [bool]$Have) {
    if (-not $Have) { Add-HzProbe -Run $run -Id $Id -Name $Name -Status 'fixture_missing' -Expected 'the shape or coupler type in the template' -Observed 'none'; return }
    $v = @($verif | Where-Object { $_ -and [string](Get-HzProp $_ 'rule_id') -like $Like })
    $n = @($v | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected 'applied, every set verified' `
        -Observed ("applied={0} verified={1}/{2} {3}" -f $applyOk, ($v.Count - $n.Count), $v.Count,
            $(if ($n.Count) { [string](Get-HzProp $n[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $n[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 700) }
              elseif (-not $applyOk) { Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 500 } else { '' })) `
        -Ok ($applyOk -and $v.Count -gt 0 -and $n.Count -eq 0) -Evidence @{ verification = $v }
}
Show-HzVerif 'P2#*' 'P2' 'L7 a hoop placed from a lapped-circle shape applies and reads back on its radius' ([bool]$circle)
Show-HzVerif 'P3#*' 'P3' 'L7 a spiral applies with its pitch, turns and height read back' ($shapes -contains 'M_SP')
Show-HzVerif 'P4#*' 'P4' 'L7 a beam layer spliced with a coupler: both pieces and the coupler read back' ($couplers.Count -gt 0)
Show-HzVerif 'P6#*' 'P6' 'stage 1b: a layer with two couplers - three pieces, the middle one coupled at both ends - reads back' ($couplers.Count -gt 0)
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$ours = @(@(Get-HzPath $audit.Result 'findings') | Where-Object { $_ -and [string](Get-HzProp $_ 'severity') -ne 'info' })
Add-HzProbe -Run $run -Id 'P5' -Name 'L7 the audit finds the placed shapes and the coupler as declared' -Expected 'no error or unknown finding' `
    -Observed ("verdict={0} findings={1} {2}" -f (Get-HzProp $audit.Result 'verdict'), $ours.Count, (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $ours }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
