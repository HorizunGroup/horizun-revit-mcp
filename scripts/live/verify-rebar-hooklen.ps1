#Requires -Version 5.1
<#
  ACERO 3.0, LIVE: a declared hook length (hook_types[].lengths_mm), on a DISPOSABLE fixture.

    H1  the plan lists the (bar type, hook type) pair it would change, as a change of a TYPE
    H2  the apply writes it, re-reads it and verifies every set
    H3  a type change reaches every bar of the type: 50 mm more declared by another set, 50 mm more tail
    H6  the first set's audit then says hook_length_differs
    H4  the audit agrees: no hook_length_differs, nothing but accepted notes
    H5  planned again, the pair is already at the declared length: change false
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [string]$ArtifactDir,
    [double]$LengthMm = 100.0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-hooklen' -Document $Document
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$beamT = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'beam-types' -Arguments @{
    categories = @('OST_StructuralFraming'); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })[0]
$X0 = 230000.0
$beam = [long](Get-HzProp @((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beam' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @($X0, 0.0, 3000.0); end = @(($X0 + 6000), 0.0, 3000.0) }) }).Apply.Result.rows)[0] 'element_id')
$run.Fixture['beam'] = $beam

$set = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = 'hooklen'; version = '1.0.0' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(, [ordered]@{ id = 'H135'; type_name = 'Stirrup/Tie - 135 deg.'; lengths_mm = @{ N10 = $LengthMm } })
    section_rules = @(, [ordered]@{ id = 'HL'; host = @{ element_ids = @(, $beam) }; cover = @{ source = 'host' }
        layers = @(@{ face = 'top'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
                   @{ face = 'bottom'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
            start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
            zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 150 } }) } }) }

Write-Host "`n== H1 ==" -ForegroundColor Cyan
$plan = (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
$changes = @(@(Get-HzPath $plan 'bar_type_changes') | Where-Object { $_ })
$pair = @($changes)[0]
Add-HzProbe -Run $run -Id 'H1' -Name 'the plan lists the declared hook length as a change of a TYPE' -Expected 'one pair, 10M with Stirrup/Tie - 135 deg., change true, declared 100' `
    -Observed (Limit-HzText (($changes | ConvertTo-Json -Depth 6 -Compress)) 700) `
    -Ok ($changes.Count -eq 1 -and (Get-HzProp $pair 'change') -eq $true) -Evidence @{ changes = $changes }

Write-Host "`n== H2 ==" -ForegroundColor Cyan
$ap = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ hook length' }
$ok = $null -ne $ap.Apply -and $ap.Apply.Ok
$v = @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'verification') }))
$nv = @($v | Where-Object { $_ -and (Get-HzProp $_ 'verified') -ne $true })
$tc = @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'bar_type_changes') | Where-Object { $_ } }))
Add-HzProbe -Run $run -Id 'H2' -Name 'the apply writes the hook length, re-reads it and verifies every set' -Expected 'applied, every set verified, the pair verified' `
    -Observed ("applied={0} sets={1} not_verified={2} pair={3} {4}" -f $ok, $v.Count, $nv.Count, (Limit-HzText ($tc | ConvertTo-Json -Depth 6 -Compress) 500),
        $(if (-not $ok -and $ap.Apply) { Limit-HzText $ap.Apply.Text 500 } elseif (-not $ok) { Limit-HzText $ap.Dry.Text 500 } else { '' })) `
    -Ok ($ok -and $nv.Count -eq 0 -and $tc.Count -eq 1 -and (Get-HzProp $tc[0] 'verified') -eq $true) -Evidence @{ changes = $tc; verification = $v }

Write-Host "`n== H3 ==" -ForegroundColor Cyan
$stir = @($v | Where-Object { $_ -and [string](Get-HzProp $_ 'rule_id') -like '*stirrups*' })
$sid = $(if ($stir.Count) { [long](Get-HzProp $stir[0] 'element_id') } else { $null })
$ext = @()
if ($sid) {
    $row = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'stirrup' -Arguments @{ mode = 'rebar'; element_ids = @(, $sid); max_rows = 5 }).Result.rows)[0]
    $pts = @(Get-HzPath $row 'geometry', 'centreline_points_mm')
    if ($pts.Count -ge 4) {
        foreach ($pairIdx in @(@(0, 1), @(($pts.Count - 2), ($pts.Count - 1)))) {
            $a = $pts[$pairIdx[0]]; $b = $pts[$pairIdx[1]]
            $ext += [Math]::Round([Math]::Sqrt([Math]::Pow([double]$a[0] - [double]$b[0], 2) + [Math]::Pow([double]$a[1] - [double]$b[1], 2) + [Math]::Pow([double]$a[2] - [double]$b[2], 2)), 2)
        }
    }
}
$run.Fixture['tail_at_declared_mm'] = $ext
# MEASURED (Revit 2024): Revit's hook length is not the straight tail - 100 drew a tail of 71.18 mm on a
# 10M 135 degree stirrup hook. H3 is therefore judged without a formula, after H4: see below.

Write-Host "`n== H4 ==" -ForegroundColor Cyan
$au = (Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
$bad = @(@(Get-HzPath $au 'findings') | Where-Object { $_ -and ([string]$_.severity -ne 'info' -or [string]$_.code -notin @('hook_corner_opening', 'hook_end_extension', 'hook_corner_seat')) })
Add-HzProbe -Run $run -Id 'H4' -Name 'the audit agrees with the declared hook length' -Expected 'no hook_length_differs; nothing but accepted notes' `
    -Observed ("verdict={0} other={1} {2}" -f (Get-HzProp $au 'verdict'), $bad.Count, (($bad | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($bad.Count -eq 0) -Evidence @{ hook_lengths = (Get-HzPath $au 'hook_lengths'); findings = $bad }

Write-Host "`n== H5 ==" -ForegroundColor Cyan
$again = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'replan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result 'bar_type_changes') | Where-Object { $_ })
Add-HzProbe -Run $run -Id 'H5' -Name 'planned again, the pair is already at the declared length' -Expected 'change false' `
    -Observed (Limit-HzText ($again | ConvertTo-Json -Depth 6 -Compress) 500) -Ok ($again.Count -eq 1 -and (Get-HzProp $again[0] 'change') -eq $false) -Evidence @{ changes = $again }

Write-Host "`n== H3, H6: the same pair declared again, 50 mm longer, by another set ==" -ForegroundColor Cyan
# A TYPE CHANGE REACHES EVERY BAR OF THE TYPE: a second beam, a second set declaring the pair 50 mm longer.
# Then the FIRST beam's stirrup must have grown by exactly 50 mm at each tail, and the first set's audit
# must now say hook_length_differs.
$beam2 = [long](Get-HzProp @((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'beam2' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @($X0, 3000.0, 3000.0); end = @(($X0 + 6000), 3000.0, 3000.0) }) }).Apply.Result.rows)[0] 'element_id')
$set2 = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'hooklen-b'; version = '1.0.0' }; bar_types = $set.bar_types
    hook_types = @(, [ordered]@{ id = 'H135'; type_name = 'Stirrup/Tie - 135 deg.'; lengths_mm = @{ N10 = ($LengthMm + 50) } })
    section_rules = @(, [ordered]@{ id = 'HL2'; host = @{ element_ids = @(, $beam2) }; cover = @{ source = 'host' }; layers = $set.section_rules[0].layers; stirrups = $set.section_rules[0].stirrups }) }
$ap2 = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-2' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set2; transaction_name = 'HZ hook length +50' }
$ext2 = @()
if ($sid) {
    $row = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'stirrup-again' -Arguments @{ mode = 'rebar'; element_ids = @(, $sid); max_rows = 5 }).Result.rows)[0]
    $pts = @(Get-HzPath $row 'geometry', 'centreline_points_mm')
    if ($pts.Count -ge 4) {
        foreach ($pairIdx in @(@(0, 1), @(($pts.Count - 2), ($pts.Count - 1)))) {
            $a = $pts[$pairIdx[0]]; $b = $pts[$pairIdx[1]]
            $ext2 += [Math]::Round([Math]::Sqrt([Math]::Pow([double]$a[0] - [double]$b[0], 2) + [Math]::Pow([double]$a[1] - [double]$b[1], 2) + [Math]::Pow([double]$a[2] - [double]$b[2], 2)), 2)
        }
    }
}
$grew = @(for ($i = 0; $i -lt [Math]::Min($ext.Count, $ext2.Count); $i++) { [Math]::Round($ext2[$i] - $ext[$i], 2) })
Add-HzProbe -Run $run -Id 'H3' -Name 'a declared hook length reaches the drawn bars: 50 mm more declared, 50 mm more tail, on every bar of the type' `
    -Expected 'the first beam''s stirrup tails each 50 mm longer (within 0.5), after another set declared the pair 50 mm longer' `
    -Observed ("applied_2={0} tails_before={1} tails_after={2} grew={3}" -f $(if ($ap2.Apply) { $ap2.Apply.Ok } else { $false }), ($ext -join ','), ($ext2 -join ','), ($grew -join ',')) `
    -Ok ($grew.Count -eq 2 -and @($grew | Where-Object { [Math]::Abs($_ - 50) -gt 0.5 }).Count -eq 0) -Evidence @{ before = $ext; after = $ext2 }
$au3 = (Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-after' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
$hd = @(@(Get-HzPath $au3 'findings') | Where-Object { $_ -and [string]$_.code -eq 'hook_length_differs' })
Add-HzProbe -Run $run -Id 'H6' -Name 'the first set''s audit catches the type changed under it' -Expected 'hook_length_differs, declared 100 against 150' `
    -Observed (Limit-HzText (($hd | Select-Object -First 1 | ConvertTo-Json -Depth 5 -Compress)) 500) -Ok ($hd.Count -ge 1) -Evidence @{ findings = $hd }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
