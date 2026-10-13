#Requires -Version 5.1
<#
  REFUERZO DESDE CARTILLA, F2.1, LIVE: laps - a top row spliced at a station in mm
  and a bottom row spliced at mid-length by bar diameters, on a straight beam and one
  turned 30 degrees in plan.

  The rule: 2 x 16M top lapped 600 mm at 3000 mm, 2 x 16M bottom lapped 40 diameters
  at half the length, cover read from each host, no stirrups (the hooked-stirrup audit
  is measured by verify-rebar-sections.ps1). Both beams carry Mark V-12; a third with
  another mark must be left alone. Then the same model is audited against a set that
  declares the top lap 700 mm: the audit must measure 600 off the model and say so.

  Needs a DISPOSABLE structural fixture open and active (-Document), built from
  Autodesk's metric structural template (M_Concrete-Rectangular Beam, 16M bars), and
  a second document open. Shapes may be created (allow_new_shape): the fixture is
  thrown away.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-laps' -Document $Document

Write-Host "`n== fixture: three concrete beams ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$types = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'beam-types' -Arguments @{
    categories = @('OST_StructuralFraming'); include_links = $false; include_types = $true; max_rows = 100 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })
if ($types.Count -ne 1) { throw 'HARNESS: the fixture has no single M_Concrete-Rectangular Beam 300 x 600mm type' }
$bt = [long]$types[0].element_id
$c = [Math]::Cos([Math]::PI / 6); $s = [Math]::Sin([Math]::PI / 6)
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beams' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv; start = @(0.0, 0.0, 3000.0); end = @(6000.0, 0.0, 3000.0) },
        @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv; start = @(10000.0, 0.0, 3000.0); end = @((10000.0 + 6000 * $c), (6000 * $s), 3000.0) },
        @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv; start = @(0.0, 8000.0, 3000.0); end = @(6000.0, 8000.0, 3000.0) }) }
$beams = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($beams.Count -ne 3) { throw "HARNESS: three beams were not created ($($beams.Count))" }
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'marks' -Arguments @{
    target_document = $Document
    writes = @(@{ target_id = $beams[0]; parameter = 'Mark'; value = 'V-12' },
               @{ target_id = $beams[1]; parameter = 'Mark'; value = 'V-12' },
               @{ target_id = $beams[2]; parameter = 'Mark'; value = 'V-99' }) }
$run.Fixture['beams'] = $beams

function New-LapSet([double]$TopLapMm) {
    [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = 'cartilla-v12-laps'; version = '1.0.0'; title = 'V-12 laps from a synthetic schedule' }
        bar_types = @(, @{ id = 'N16'; type_name = '16M' })
        section_rules = @(, [ordered]@{
            id = 'V12'; host = @{ category = 'OST_StructuralFraming'; mark = 'V-12' }
            cover = @{ source = 'host' }
            layers = @(
                @{ face = 'top'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true
                   laps = @(, @{ at_mm = 3000; length_mm = $TopLapMm }) },
                @{ face = 'bottom'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true
                   laps = @(, @{ at_ratio = 0.5; length_db = 40 }) }) })
    }
}
$set = New-LapSet 600

Write-Host "`n== plan ==" -ForegroundColor Cyan
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }
$rows = @(Get-HzPath $plan.Result 'reinforcement')
$ruleIds = @($rows | ForEach-Object { [string](Get-HzProp $_ 'rule_id') } | Where-Object { $_ })
if ($ruleIds.Count -eq 0) { $ruleIds = @($rows | ForEach-Object { [string](Get-HzPath $_ 'rule', 'id') } | Where-Object { $_ }) }
$refused = @($rows | Where-Object { Get-HzProp $_ 'code' })
$forOther = @($ruleIds | Where-Object { $_ -match ('#h' + $beams[2] + '#') })
$expected = foreach ($b in $beams[0..1]) { foreach ($l in 'top', 'bottom') { foreach ($p in 'p1', 'p2') { "V12#h$b#$l#$p" } } }
$missing = @($expected | Where-Object { $ruleIds -notcontains $_ })
Add-HzProbe -Run $run -Id 'L1' -Name 'F2.1 each lapped row plans as two pieces on every beam of the mark, and only those' `
    -Expected '#top#p1/#p2 and #bottom#p1/#p2 for both V-12 beams, nothing for V-99, no refusal' `
    -Observed ("rows={0} refused={1} missing={2} other_mark={3} first_refusal={4}" -f $rows.Count, $refused.Count, ($missing -join ','), $forOther.Count,
        $(if ($refused.Count) { (Get-HzProp $refused[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $refused[0] 'why')) 200) } else { '' })) `
    -Ok ($refused.Count -eq 0 -and $missing.Count -eq 0 -and $forOther.Count -eq 0 -and $ruleIds.Count -eq 8) `
    -Evidence @{ rule_ids = $ruleIds; refused = $refused }

Write-Host "`n== apply ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{
    target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar laps' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$created = $(if ($applyOk) { Get-HzProp $apply.Apply.Result 'created_verified' } else { $null })
Add-HzProbe -Run $run -Id 'L2' -Name 'F2.1 every piece applies and reads back' `
    -Expected 'applied; created_verified equals the planned pieces (8)' `
    -Observed ("applied={0} created_verified={1} planned={2} text={3}" -f $applyOk, $created, $ruleIds.Count,
        (Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 220)) `
    -Ok ($applyOk -and [int]$created -eq $ruleIds.Count) -Evidence @{ apply = $(if ($apply.Apply) { $apply.Apply.Result } else { $null })
                                                                      apply_text = $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) }

Write-Host "`n== audit ==" -ForegroundColor Cyan
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$verdict = [string](Get-HzProp $audit.Result 'verdict')
if (-not $verdict) { $verdict = [string](Get-HzPath $audit.Result 'summary', 'verdict') }
$laps = @(Get-HzPath $audit.Result 'laps')
# 600 mm declared on top; 40 x the model's 16M diameter on the bottom.
$lapRows = @($laps | ForEach-Object { '{0} declared={1} measured={2} ok={3}' -f (Get-HzProp $_ 'rule_id'), (Get-HzProp $_ 'declared_mm'), (Get-HzProp $_ 'measured_mm'), (Get-HzProp $_ 'ok') })
$lapsExact = @($laps | Where-Object { (Get-HzProp $_ 'ok') -and $null -ne (Get-HzProp $_ 'measured_mm') -and
                                      [Math]::Abs([double](Get-HzProp $_ 'measured_mm') - [double](Get-HzProp $_ 'declared_mm')) -le 2 })
Add-HzProbe -Run $run -Id 'L3' -Name 'F2.1 the audit measures every lap off the model, at its declared length' `
    -Expected 'four laps listed, each ok, measured within 2 mm of declared' `
    -Observed ("laps={0} exact={1} :: {2}" -f $laps.Count, $lapsExact.Count, ($lapRows -join '; ')) `
    -Ok ($laps.Count -eq 4 -and $lapsExact.Count -eq 4) -Evidence @{ laps = $laps }
# D4: a lap is not collinear - the next piece sits one bar diameter across, in contact.
$across = @($laps | ForEach-Object { '{0} declared_across={1} measured_across={2}' -f (Get-HzProp $_ 'rule_id'), (Get-HzProp $_ 'declared_across_mm'), (Get-HzProp $_ 'measured_across_mm') })
$acrossExact = @($laps | Where-Object { $null -ne (Get-HzProp $_ 'measured_across_mm') -and [double](Get-HzProp $_ 'declared_across_mm') -gt 0 -and
                                        [Math]::Abs([double](Get-HzProp $_ 'measured_across_mm') - [double](Get-HzProp $_ 'declared_across_mm')) -le 1 })
Add-HzProbe -Run $run -Id 'L6' -Name 'D4 each lapped piece sits one bar diameter across, in contact, as the audit measures it' `
    -Expected 'four laps, each measured across within 1 mm of its declared bar diameter (never 0: not collinear)' `
    -Observed ("laps={0} across_exact={1} :: {2}" -f $laps.Count, $acrossExact.Count, ($across -join '; ')) `
    -Ok ($laps.Count -eq 4 -and $acrossExact.Count -eq 4) -Evidence @{ laps = $laps }
$findings = @(Get-HzPath $audit.Result 'findings')
Add-HzProbe -Run $run -Id 'L4' -Name 'F2.1 the audit finds the model agrees with the lapped rule' `
    -Expected 'verdict agrees, no findings' `
    -Observed ("verdict={0} findings={1} {2}" -f $verdict, $findings.Count, (($findings | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code }) -join '; ')) `
    -Ok ($verdict -eq 'agrees') -Evidence @{ audit = $audit.Result }
# NARROWER, NOT LOOSER: L4 stands as it is. Revit gives identical bars the same Mark, so
# equal pieces in two beams carry one mark - since L9 that is not a finding, and one mark on
# DIFFERENT bars (bar_mark_conflict) counts here like any other, so no code is excused.
$other = @($findings | Where-Object { $_ })
Add-HzProbe -Run $run -Id 'L4b' -Name 'F2.1 apart from Revit''s own bar marks, the audit finds nothing' `
    -Expected 'no finding at all (bar_mark_conflict included)' `
    -Observed ("other_findings={0} {1}" -f $other.Count, (($other | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code }) -join '; ')) `
    -Ok ($other.Count -eq 0) -Evidence @{ findings = $other }

Write-Host "`n== audit against a longer declared lap ==" -ForegroundColor Cyan
$audit2 = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-700' -Arguments @{ target_document = $Document; requirement_set = (New-LapSet 700) }
$short = @(@(Get-HzPath $audit2.Result 'findings') | Where-Object { [string]$_.code -eq 'lap_insufficient' })
$shortTop = @($short | Where-Object { [string]$_.rule_id -match '#top#p1$' -and [Math]::Abs([double](([string]$_.observed) -replace '[^0-9.\-]', '') - 600) -le 2 })
Add-HzProbe -Run $run -Id 'L5' -Name 'F2.1 a lap declared longer than the model has is lap_insufficient, at the length measured' `
    -Expected 'lap_insufficient on both top laps, observed 600 against 700 declared; none on the bottom' `
    -Observed ("lap_insufficient={0} top_at_600={1} :: {2}" -f $short.Count, $shortTop.Count,
        (($short | ForEach-Object { [string]$_.rule_id + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($short.Count -eq 2 -and $shortTop.Count -eq 2) -Evidence @{ findings = $short }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
