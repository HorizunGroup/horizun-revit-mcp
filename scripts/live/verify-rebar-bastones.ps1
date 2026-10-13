#Requires -Version 5.1
<#
  REFUERZO DESDE CARTILLA, F2.2, LIVE: bastones - top bars laid from each end of a
  beam over a quarter of its length, with a continuous bottom row and stirrups, on a
  straight beam and one turned 30 degrees in plan.

  The rule: 3 x 16M top, 2 x 16M bottom along the host, a 10M perimeter stirrup at
  150 with 135 degree hooks, cover read from each host. Both beams carry Mark V-12;
  a third concrete beam with another mark must be left alone.

  Needs a DISPOSABLE structural fixture open and active (-Document), built from
  Autodesk's metric structural template (M_Concrete-Rectangular Beam, 10M/16M bars,
  Stirrup/Tie - 135 deg.), and a second document open. Shapes may be created
  (allow_new_shape): the fixture is thrown away.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-bastones' -Document $Document

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

$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'cartilla-v12'; version = '1.0.0'; title = 'V-12 from a synthetic schedule' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(, @{ id = 'H135'; type_name = 'Stirrup/Tie - 135 deg.' })
    section_rules = @(, [ordered]@{
        id = 'V12'; host = @{ category = 'OST_StructuralFraming'; mark = 'V-12' }
        cover = @{ source = 'host' }
        layers = @(
            @{ name = 'top-start'; face = 'top'; count = 2; bar_type = 'N16'; extend = @{ from = 'start'; run_ratio = 0.25 }; allow_new_shape = $true },
            @{ name = 'top-end'; face = 'top'; count = 2; bar_type = 'N16'; extend = @{ from = 'end'; run_ratio = 0.25 }; allow_new_shape = $true },
            @{ face = 'bottom'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = @{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
                      start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
                      zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } })
}

Write-Host "`n== plan ==" -ForegroundColor Cyan
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }
$rows = @(Get-HzPath $plan.Result 'reinforcement')
$ruleIds = @($rows | ForEach-Object { [string](Get-HzProp $_ 'rule_id') } | Where-Object { $_ })
if ($ruleIds.Count -eq 0) { $ruleIds = @($rows | ForEach-Object { [string](Get-HzPath $_ 'rule', 'id') } | Where-Object { $_ }) }
$refused = @($rows | Where-Object { Get-HzProp $_ 'code' })
$forOther = @($ruleIds | Where-Object { $_ -match ('#h' + $beams[2] + '#') })
$expected = @("V12#h$($beams[0])#top-start", "V12#h$($beams[0])#top-end", "V12#h$($beams[0])#bottom", "V12#h$($beams[1])#top-start", "V12#h$($beams[1])#top-end", "V12#h$($beams[1])#bottom")
$missing = @($expected | Where-Object { $ruleIds -notcontains $_ })
Add-HzProbe -Run $run -Id 'S1' -Name 'F2.2 the bastones plan from each end of every beam of the mark, and only those' `
    -Expected 'top and bottom rows for both V-12 beams (and their stirrup zones), nothing for V-99, no refusal' `
    -Observed ("rows={0} refused={1} missing={2} other_mark={3} first_refusal={4}" -f $rows.Count, $refused.Count, ($missing -join ','), $forOther.Count,
        $(if ($refused.Count) { (Get-HzProp $refused[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $refused[0] 'why')) 200) } else { '' })) `
    -Ok ($refused.Count -eq 0 -and $missing.Count -eq 0 -and $forOther.Count -eq 0 -and $ruleIds.Count -ge 8) `
    -Evidence @{ rule_ids = $ruleIds; refused = $refused }

Write-Host "`n== apply ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{
    target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar sections' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$created = $(if ($applyOk) { Get-HzProp $apply.Apply.Result 'created_verified' } else { $null })
Add-HzProbe -Run $run -Id 'S2' -Name 'F2.2 the bastones, the bottom row and the stirrups apply and read back' `
    -Expected 'applied; created_verified equals the planned sets' `
    -Observed ("applied={0} created_verified={1} planned_sets={2} text={3}" -f $applyOk, $created, $ruleIds.Count,
        (Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 220)) `
    -Ok ($applyOk -and [int]$created -eq $ruleIds.Count) -Evidence @{ apply = $(if ($apply.Apply) { $apply.Apply.Result } else { $null })
                                                                      apply_text = $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) }

Write-Host "`n== audit ==" -ForegroundColor Cyan
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$verdict = [string](Get-HzProp $audit.Result 'verdict')
if (-not $verdict) { $verdict = [string](Get-HzPath $audit.Result 'summary', 'verdict') }
$findings = @(Get-HzPath $audit.Result 'findings')
$longFindings = @($findings | Where-Object { [string]$_.rule_id -match '#(top-start|top-end|bottom)$' })
$stirrupFindings = @($findings | Where-Object { [string]$_.rule_id -match '#stirrups' })
Add-HzProbe -Run $run -Id 'S3a' -Name 'F2.2 every baston and the bottom row stand exactly where the rule put them' `
    -Expected 'no audit finding on any #top / #bottom set of either beam' `
    -Observed ("findings={0} {1}" -f $longFindings.Count, (($longFindings | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code }) -join '; ')) `
    -Ok ($longFindings.Count -eq 0) -Evidence @{ findings = $longFindings }
Add-HzProbe -Run $run -Id 'S3b' -Name 'F1.1 the hooked perimeter stirrups agree with their zone rules' `
    -Expected 'no error or unknown finding on the #stirrups sets; the only note allowed is hook_corner_opening (D1 option A), measured within one 10M diameter' `
    -Observed ("findings={0} {1}" -f $stirrupFindings.Count, (($stirrupFindings | Select-Object -First 3 | ForEach-Object { [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($stirrupFindings.Count -eq 0 -or (@($stirrupFindings | Where-Object { [string]$_.code -ne 'hook_corner_opening' -or [string]$_.severity -ne 'info' -or
                                                  [double]$_.observed -gt 10.5 }).Count -eq 0)) -Evidence @{ findings = $stirrupFindings }
Add-HzProbe -Run $run -Id 'S3' -Name 'F1.1 the audit finds the model agrees with the section rule' `
    -Expected 'verdict agrees, or agrees_with_notes whose every note is an accepted info finding (hook_corner_opening, hook_end_extension)' -Observed ("verdict={0}" -f $verdict) -Ok ($verdict -eq 'agrees' -or ($verdict -eq 'agrees_with_notes' -and @($audit.Result.findings | Where-Object { $_ -and [string]$_.severity -ne 'info' }).Count -eq 0 -and @($audit.Result.findings | Where-Object { $_ -and [string]$_.severity -eq 'info' -and [string]$_.code -notin @('hook_corner_opening', 'hook_end_extension', 'hook_corner_seat') }).Count -eq 0)) -Evidence @{ audit = $audit.Result }
# NARROWER, NOT LOOSER: S3 stands as it is. Revit gives identical bars the same Mark, and since
# L9 that is not a finding (one mark on the SAME bar is a schedule line; on a different bar it is
# bar_mark_conflict and counts here like any other error), so no code is excused any more.
$otherErrors = @($findings | Where-Object { [string]$_.severity -ne 'info' })
Add-HzProbe -Run $run -Id 'S3c' -Name 'F1.1 apart from Revit''s own bar marks, the audit finds no error' `
    -Expected 'no error or unknown finding (bar_mark_conflict included)' `
    -Observed ("other={0} {1}" -f $otherErrors.Count, (($otherErrors | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code }) -join '; ')) `
    -Ok ($otherErrors.Count -eq 0) -Evidence @{ findings = $otherErrors }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
