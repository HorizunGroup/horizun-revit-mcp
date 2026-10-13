#Requires -Version 5.1
<#
  L10 STAGE 1c, LIVE: a schedule card proposed from its OWN axes, planned, applied and
  audited against grids found in the model by name.

  A SYNTHETIC two-span card sheet (nothing from a real drawing) is read by
  horizun_read_schedule: card VX-1 (one beam over three grids: a continuous top row,
  a bottom row per span, three stirrup zones per span) and card VX-2 (the same member
  modelled as two beams, one per span: a top and a bottom row and the zones per span). The
  proposal measures every bar and stirrup run from the card's first axis toward its
  last; the plan places those stations on grids Y7, Y8, Y9 of the model. A rule whose
  axis is not a grid of the model is refused by name.

  Needs a DISPOSABLE structural fixture open and active (-Document) with
  M_Concrete-Rectangular Beam 300 x 600mm, 10M/16M bars and Stirrup/Tie - 135 deg.,
  and no grid named Y6..Y9. Shapes may be created (allow_new_shape, the caller's word on
  a fixture that is thrown away).
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-card-axes' -Document $Document

# ------------------------------------------------------------------ the card sheet
# Drawing millimetres at 1:50: one metre of the member is 20 mm; the first axis 20 mm into the card.
$H = 2.5
function W([string]$t, [double]$h) { [Math]::Round(0.6 * $h * $t.Length, 4) }
function Txt($list, [string]$t, [double]$x, [double]$y, [double]$h) {
    $null = $list.Add(@{ text = $t; at = @($x, $y); height = $h; direction = @(1.0, 0.0); width = (W $t $h) })
}
function Above($list, [string]$t, [double]$cx, [double]$y, [double]$h, [double]$gap = 0.6) { Txt $list $t ($cx - (W $t $h) / 2) ($y + $gap) $h }
function Seg($list, [double]$x0, [double]$y0, [double]$x1, [double]$y1, [string]$c = '#000000') {
    $null = $list.Add(@{ a = @($x0, $y0); b = @($x1, $y1); color = $c })
}
$segs = New-Object System.Collections.ArrayList
$texts = New-Object System.Collections.ArrayList
function Card([string]$title, [double]$ox, [double]$oy, [bool]$top) {
    $X = { param($m) $ox + 20 + $m * 20 }
    Seg $segs $ox $oy ($ox + 300) $oy; Seg $segs ($ox + 300) $oy ($ox + 300) ($oy + 80)
    Seg $segs ($ox + 300) ($oy + 80) $ox ($oy + 80); Seg $segs $ox ($oy + 80) $ox $oy
    Txt $texts $title ($ox + 3) ($oy + 68) 5.0
    foreach ($a in @(@('Y7', 0), @('Y8', 6), @('Y9', 12))) { Above $texts $a[0] (& $X $a[1]) ($oy + 82) $H 0 }
    if ($top) { Seg $segs (& $X 0.2) ($oy + 55) (& $X 11.8) ($oy + 55) '#ff0000'; Above $texts '2#5 L=11.60' (& $X 6.0) ($oy + 55) $H }
    foreach ($s in 0, 6) {
        if (-not $top) { Seg $segs (& $X ($s + 0.2)) ($oy + 55) (& $X ($s + 5.8)) ($oy + 55) '#ff0000'; Above $texts '2#5 L=5.60' (& $X ($s + 3.0)) ($oy + 55) $H }
        Seg $segs (& $X ($s + 0.2)) ($oy + 30) (& $X ($s + 5.8)) ($oy + 30) '#ff0000'; Above $texts '2#5 L=5.60' (& $X ($s + 3.0)) ($oy + 30) $H
        foreach ($z in @(@('8#3@.10', 0.2, 0.9), @('14#3@.30', 1.0, 4.9), @('8#3@.10', 5.0, 5.7))) {
            Seg $segs (& $X ($s + $z[1])) ($oy + 42) (& $X ($s + $z[2])) ($oy + 42)
            Above $texts $z[0] (((& $X ($s + $z[1])) + (& $X ($s + $z[2]))) / 2) ($oy + 42) $H
        }
    }
    Txt $texts 'SON 60' ($ox + 285 - 15) ($oy + 10) $H
}
Card 'VX-1' 20 150 $true
Card 'VX-2' 20 40 $false
$primitives = @{ units = 'drawing_mm'; segments = @($segs); polylines = @(); texts = @($texts) }

Write-Host "`n== read the card sheet ==" -ForegroundColor Cyan
$read = Invoke-HzToolStrict -Run $run -Tool 'horizun_read_schedule' -Label 'read' -Arguments @{
    primitives = $primitives; scale = 50; tolerances = @{ length_mm = 30; zone_span_mm = 600; support_gap_mm = 500 }
    requirement_set = @{ set_id = 'card-axes'; bar_type_names = @{ '#5' = '16M'; '#3' = '10M' }
                         stirrup_hook_type_name = 'Stirrup/Tie - 135 deg.' } }
$set = Get-HzProp $read.Result 'requirement_set'
$np = @(@(Get-HzPath $read.Result 'requirement_set_not_proposed') | Where-Object { $_ })
$extents = @()
if ($set) { foreach ($r in @($set.section_rules)) { foreach ($l in @($r.layers)) { $extents += ('{0}:{1}..{2}' -f $l.extend.axis, $l.extend.from_mm, $l.extend.to_mm) } } }
Add-HzProbe -Run $run -Id 'A1' -Name 'L10 1c the card is proposed as stations from its own axes' `
    -Expected 'a set; every layer extent from Y7 toward Y9; nothing listed as not proposed' `
    -Observed ("set={0} not_proposed={1} rules={2} extents={3}" -f ($null -ne $set), $np.Count, $(if ($set) { @($set.section_rules).Count } else { 0 }), ($extents -join ' ')) `
    -Ok ($null -ne $set -and $np.Count -eq 0 -and @($extents | Where-Object { $_ -notlike 'Y7:*' }).Count -eq 0) -Evidence @{ read = $read.Result }
if (-not $set) { $end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir; exit $end.ExitCode }
# The caller's word on a disposable fixture: shapes may be created.
foreach ($r in @($set.section_rules)) {
    foreach ($l in @($r.layers)) { $l | Add-Member -NotePropertyName allow_new_shape -NotePropertyValue $true -Force }
    if ($r.PSObject.Properties['stirrups']) { $r.stirrups | Add-Member -NotePropertyName allow_new_shape -NotePropertyValue $true -Force }
}

Write-Host "`n== fixture: grids Y7 Y8 Y9 and the beams ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$z = 3000.0
$types = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'beam-types' -Arguments @{
    categories = @('OST_StructuralFraming'); include_links = $false; include_types = $true; max_rows = 100 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })
if ($types.Count -ne 1) { throw 'HARNESS: the fixture has no single M_Concrete-Rectangular Beam 300 x 600mm type' }
$bt = [long]$types[0].element_id
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'grids-and-beams' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'grid'; name = 'Y7'; start = @(0.0, -3000.0, 0.0); end = @(0.0, 9000.0, 0.0) },
        @{ kind = 'grid'; name = 'Y8'; start = @(6000.0, -3000.0, 0.0); end = @(6000.0, 9000.0, 0.0) },
        @{ kind = 'grid'; name = 'Y9'; start = @(12000.0, -3000.0, 0.0); end = @(12000.0, 9000.0, 0.0) },
        @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv; start = @(0.0, 0.0, $z); end = @(12000.0, 0.0, $z) },
        @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv; start = @(0.0, 5000.0, $z); end = @(6000.0, 5000.0, $z) },
        @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv; start = @(6000.0, 5000.0, $z); end = @(12000.0, 5000.0, $z) }) }
$ids = @(@($made.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($ids.Count -ne 6) { throw "HARNESS: grids and beams were not created ($($ids.Count))" }
$beams = $ids[3..5]
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'marks' -Arguments @{
    target_document = $Document
    writes = @(@{ target_id = $beams[0]; parameter = 'Mark'; value = 'VX-1' },
               @{ target_id = $beams[1]; parameter = 'Mark'; value = 'VX-2' },
               @{ target_id = $beams[2]; parameter = 'Mark'; value = 'VX-2' }) }
$run.Fixture['beams'] = $beams

Write-Host "`n== plan ==" -ForegroundColor Cyan
$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }
$rows = @(Get-HzPath $plan.Result 'reinforcement')
$ruleIds = @($rows | ForEach-Object { [string](Get-HzProp $_ 'rule_id') } | Where-Object { $_ })
$refused = @($rows | Where-Object { Get-HzProp $_ 'code' })
$expect = @("VX-1#h$($beams[0])#top", "VX-1#h$($beams[0])#bottom-1", "VX-1#h$($beams[0])#bottom-2",
            "VX-2#h$($beams[1])#bottom-1", "VX-2#h$($beams[2])#bottom-2")
$missing = @($expect | Where-Object { $ruleIds -notcontains $_ })
$wrongHost = @($ruleIds | Where-Object { $_ -eq "VX-2#h$($beams[1])#bottom-2" -or $_ -eq "VX-2#h$($beams[2])#bottom-1" })
Add-HzProbe -Run $run -Id 'A2' -Name 'L10 1c the plan places every station on the named grids, each span''s steel in its own host' `
    -Expected 'no refusal; top and both bottom rows on VX-1; on VX-2 each span''s row and stirrups in the beam of that span only' `
    -Observed ("rows={0} refused={1} missing={2} wrong_host={3} first_refusal={4}" -f $rows.Count, $refused.Count, ($missing -join ','), $wrongHost.Count,
        $(if ($refused.Count) { (Get-HzProp $refused[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $refused[0] 'why')) 220) } else { '' })) `
    -Ok ($refused.Count -eq 0 -and $missing.Count -eq 0 -and $wrongHost.Count -eq 0) -Evidence @{ rule_ids = $ruleIds; refused = $refused }

Write-Host "`n== an axis the model does not have ==" -ForegroundColor Cyan
$bad = $set | ConvertTo-Json -Depth 40 | ConvertFrom-Json
foreach ($r in @($bad.section_rules)) {
    foreach ($l in @($r.layers)) { if ($l.extend.axis -eq 'Y7') { $l.extend.axis = 'Y6' } }
    if ($r.PSObject.Properties['stirrups'] -and $r.stirrups.extent.axis -eq 'Y7') { $r.stirrups.extent.axis = 'Y6' }
}
$badPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-missing-axis' -Arguments @{ target_document = $Document; requirement_set = $bad }
$badRows = @(@(Get-HzPath $badPlan.Result 'reinforcement') | Where-Object { Get-HzProp $_ 'code' })
$codes = @($badRows | ForEach-Object { [string](Get-HzProp $_ 'code') + ' ' + [string](Get-HzProp $_ 'why') })
Add-HzProbe -Run $run -Id 'A5' -Name 'L10 1c a rule measured from an axis the model does not have is refused by name' `
    -Expected 'every section rule refused for every host with axis_not_in_model, before anything is built' `
    -Observed ("refused={0} {1}" -f $badRows.Count, (Limit-HzText ($codes | Select-Object -First 1) 200)) `
    -Ok ($badRows.Count -ge 3 -and @($codes | Where-Object { $_ -notmatch 'axis_not_in_model' }).Count -eq 0) -Evidence @{ refused = $badRows }

Write-Host "`n== apply ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{
    target_document = $Document; requirement_set = $set; transaction_name = 'HZ card from its axes' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$created = $(if ($applyOk) { Get-HzProp $apply.Apply.Result 'created_verified' } else { $null })
Add-HzProbe -Run $run -Id 'A3' -Name 'L10 1c the proposed set applies and reads back' `
    -Expected 'applied; created_verified equals the planned sets' `
    -Observed ("applied={0} created_verified={1} planned_sets={2} text={3}" -f $applyOk, $created, $ruleIds.Count,
        (Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 260)) `
    -Ok ($applyOk -and [int]$created -eq $ruleIds.Count) -Evidence @{ apply = $(if ($apply.Apply) { $apply.Apply.Result } else { $null }) }

Write-Host "`n== audit ==" -ForegroundColor Cyan
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$verdict = [string](Get-HzProp $audit.Result 'verdict')
if (-not $verdict) { $verdict = [string](Get-HzPath $audit.Result 'summary', 'verdict') }
$errors = @(@(Get-HzPath $audit.Result 'findings') | Where-Object { $_ -and [string]$_.severity -ne 'info' })
Add-HzProbe -Run $run -Id 'A4' -Name 'L10 1c the audit finds the model agrees with the card''s stations' `
    -Expected 'verdict agrees, or agrees_with_notes with info findings only' `
    -Observed ("verdict={0} errors={1} {2}" -f $verdict, $errors.Count, (($errors | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code }) -join '; ')) `
    -Ok (($verdict -eq 'agrees' -or $verdict -eq 'agrees_with_notes') -and $errors.Count -eq 0) -Evidence @{ audit = $audit.Result }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
