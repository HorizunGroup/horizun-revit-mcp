#Requires -Version 5.1
<#
  ACERO 3.0, stage 1c, duplicate_bar and mode=steel_review, LIVE, on a DISPOSABLE fixture
  (Autodesk metric template: a concrete basic wall of 200 mm or more, 16M).

    D1  plan: two rules declaring the SAME bar are both refused as duplicate_bar, each naming the
        other, before anything is written - and not as bars_intersect
    D2  the same bar built as two separate sets (each plan sees only its own bars), and a third set
        whose vertical bar runs through them: every set applies and is verified
    D3  the audit of ONE of the two sets finds the other - a set no rule of it built - as
        duplicate_bar on every coinciding bar pair, and does not judge the crossing bar (unattributed)
    D4  the audit of the two rules as one set reports duplicate_bar between them, once, never also
        as overlapping_bar
    D5  horizun_query_structure mode=steel_review, no requirement set, scoped to the wall: exact
        counts - one duplicate set pair with every bar pair, the crossing bar against both sets, by
        host category - and the coverage complete
    D6  mode=steel_review without a tolerance is refused, and nothing is reviewed
    D7  mode=steel_review over the WHOLE model finds at least the wall's pairs, and says how long it took

  Every coordinate comes from the wall's bounding box and its cover. The 2 mm tolerance is the
  fixture's declaration, not a code value.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-duplicate' -Document $Document

function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }
function Get-HzBox([long]$Id) {
    $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "box-$Id" -Arguments @{
        element_ids = @(, $Id); include_links = $false; include_bounding_box = $true; max_rows = 5 }
    $r = @($q.Result.rows | Where-Object { [long]$_.element_id -eq $Id })
    if ($r.Count -ne 1 -or $null -eq $r[0].bounding_box) { throw "HARNESS: element $Id has no bounding box" }
    $b = $r[0].bounding_box
    [pscustomobject]@{ x0 = [double]$b.min[0]; y0 = [double]$b.min[1]; z0 = [double]$b.min[2]
                       x1 = [double]$b.max[0]; y1 = [double]$b.max[1]; z1 = [double]$b.max[2] }
}
function New-HzSet([string]$Id, $Rules) {
    [ordered]@{
        schema = 'horizun.structural-requirements/1'; units = 'millimeter'
        requirement_set = @{ id = $Id; version = '1.0.0'; title = 'duplicate_bar, synthetic' }
        bar_types = @(, @{ id = 'N16'; type_name = '16M' })
        reinforcement_rules = @($Rules)
    }
}
function Get-HzRows($Result) { @(@(Get-HzPath $Result 'reinforcement') | Where-Object { $_ }) }
function Get-HzApplyOk($Apply) {
    $ok = $null -ne $Apply.Apply -and $Apply.Apply.Ok
    $verif = @($(if ($Apply.Apply) { @(Get-HzPath $Apply.Apply.Result 'verification') }) | Where-Object { $_ })
    $ok -and $verif.Count -gt 0 -and @($verif | Where-Object { (Get-HzProp $_ 'verified') -ne $true }).Count -eq 0
}
function Get-HzCodeFindings($Audit, [string]$Code) {
    @(@(Get-HzPath $Audit.Result 'findings') | Where-Object { $_ -and [string](Get-HzProp $_ 'code') -eq $Code })
}

# ---------------------------------------------------------------- fixture
Write-Host "`n== fixture: a structural wall ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$wallT = @(Get-HzTypes 'OST_Walls' | Where-Object { [string]$_.family -eq 'Basic Wall' -and [string]$_.name -match 'Concrete' -and [string]$_.name -match '(\d+)\s*mm' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 200 } | Select-Object -First 1)
if ($wallT.Count -eq 0) { throw 'HARNESS: no concrete basic wall type of 200 mm or more in the template' }
$XW = 190000.0
$wall = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'wall' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'wall'; type_id = [long]$wallT[0].element_id; level_id = $lv; start = @($XW, 0.0, 0.0); end = @(($XW + 3000.0), 0.0, 0.0); height = 3000.0; structural = $true; join_rule = 'disallow' }) })
$bx = Get-HzBox $wall
$yc = ($bx.y0 + $bx.y1) / 2
$xc = ($bx.x0 + $bx.x1) / 2
$run.Fixture['wall'] = @{ id = $wall; type = [string]$wallT[0].name; box = $bx }

# Five horizontals in the wall's mid plane, 200 apart from z = 500; the same rule twice under two ids.
function New-HzHorizontals([string]$Id) {
    [ordered]@{ id = $Id; host = @{ element_ids = @(, $wall) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
        curve_mm = @(@(($bx.x0 + 300.0), $yc, 500.0), @(($bx.x1 - 300.0), $yc, 500.0)); normal = @(0, 0, 1)
        layout = @{ rule = 'fixed_number'; number = 5; array_length_mm = 800 } }
}
# One vertical in the same plane, through all five.
$vertical = [ordered]@{ id = 'DC'; host = @{ element_ids = @(, $wall) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
    curve_mm = @(@($xc, $yc, 300.0), @($xc, $yc, 2000.0)); normal = @(1, 0, 0); layout = @{ rule = 'single' } }

# ---------------------------------------------------------------- D1: the plan
Write-Host "`n== plan: two rules, one bar ==" -ForegroundColor Cyan
$both = New-HzSet 'dup-ab' @((New-HzHorizontals 'DA'), (New-HzHorizontals 'DB'))
$p1 = (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-d1' -Arguments @{ target_document = $Document; requirement_set = $both }).Result
$rows1 = Get-HzRows $p1
$ra = @($rows1 | Where-Object { [string](Get-HzProp $_ 'rule_id') -eq 'DA' })
$rb = @($rows1 | Where-Object { [string](Get-HzProp $_ 'rule_id') -eq 'DB' })
$ova = $(if ($ra.Count) { @(@(Get-HzProp $ra[0] 'overlaps') | Where-Object { $_ }) } else { @() })
$ovb = $(if ($rb.Count) { @(@(Get-HzProp $rb[0] 'overlaps') | Where-Object { $_ }) } else { @() })
Add-HzProbe -Run $run -Id 'D1' -Name 'plan: two rules declaring the same bar are both refused as duplicate_bar, each naming the other' `
    -Expected 'DA duplicate_bar naming DB (kind duplicate, 5 bar pairs) and DB duplicate_bar naming DA; nothing refused as bars_intersect' `
    -Observed ("DA {0} vs {1} kind={2} pairs={3} | DB {4} vs {5} | why: {6}" -f $(if ($ra.Count) { Get-HzProp $ra[0] 'code' }),
        $(if ($ova.Count) { $ova[0].other_rule_id }), $(if ($ova.Count) { $ova[0].kind }), $(if ($ova.Count) { $ova[0].bar_pairs }),
        $(if ($rb.Count) { Get-HzProp $rb[0] 'code' }), $(if ($ovb.Count) { $ovb[0].other_rule_id }),
        $(if ($ra.Count) { Limit-HzText ([string](Get-HzProp $ra[0] 'why')) 260 })) `
    -Ok ($ra.Count -eq 1 -and $rb.Count -eq 1 -and [string](Get-HzProp $ra[0] 'code') -eq 'duplicate_bar' -and [string](Get-HzProp $rb[0] 'code') -eq 'duplicate_bar' -and
         $ova.Count -eq 1 -and [string]$ova[0].other_rule_id -eq 'DB' -and [string]$ova[0].kind -eq 'duplicate' -and [int]$ova[0].bar_pairs -eq 5 -and
         $ovb.Count -eq 1 -and [string]$ovb[0].other_rule_id -eq 'DA' -and
         @($rows1 | Where-Object { [string](Get-HzProp $_ 'code') -eq 'bars_intersect' }).Count -eq 0) `
    -Evidence @{ rows = $rows1; overlap_check = (Get-HzProp $p1 'overlap_check') }

# ---------------------------------------------------------------- D2: built twice
Write-Host "`n== apply: the same bar as two sets, and a bar through both ==" -ForegroundColor Cyan
$setA = New-HzSet 'dup-a' @(, (New-HzHorizontals 'DA'))
$setB = New-HzSet 'dup-b' @(, (New-HzHorizontals 'DB'))
$setC = New-HzSet 'dup-c' @(, $vertical)
$apA = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-a' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $setA; transaction_name = 'HZ rebar duplicate A' }
$apB = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-b' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $setB; transaction_name = 'HZ rebar duplicate B' }
$apC = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-c' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $setC; transaction_name = 'HZ rebar duplicate C' }
$built = (Get-HzApplyOk $apA) -and (Get-HzApplyOk $apB) -and (Get-HzApplyOk $apC)
Add-HzProbe -Run $run -Id 'D2' -Name 'the same bar built as two separate sets, and a third set through both: every set applied and verified' `
    -Expected 'three applies, every row verified' `
    -Observed ("a={0} b={1} c={2}" -f (Get-HzApplyOk $apA), (Get-HzApplyOk $apB), (Get-HzApplyOk $apC)) -Ok $built `
    -Evidence @{ a = $(if ($apA.Apply) { Get-HzPath $apA.Apply.Result 'verification' }); b = $(if ($apB.Apply) { Get-HzPath $apB.Apply.Result 'verification' }); c = $(if ($apC.Apply) { Get-HzPath $apC.Apply.Result 'verification' }) }

# The three bar sets, as the audits matched them by provenance (the audit of the two rules is D4's).
$auAB = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-ab' -Arguments @{ target_document = $Document; requirement_set = $both }
$auC = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-c' -Arguments @{ target_document = $Document; requirement_set = $setC }
function Get-HzBarOf($Audit, [string]$Rule) {
    $r = @(@(Get-HzPath $Audit.Result 'reinforcement') | Where-Object { $_ -and [string](Get-HzProp $_ 'rule_id') -eq $Rule -and $null -ne (Get-HzProp $_ 'rebar_id') })
    if ($r.Count -ne 1) { throw "HARNESS: the audit matched $($r.Count) bar sets to rule $Rule" }
    [long](Get-HzProp $r[0] 'rebar_id')
}
$idA = Get-HzBarOf $auAB 'DA'; $idB = Get-HzBarOf $auAB 'DB'; $idC = Get-HzBarOf $auC 'DC'
$run.Fixture['bars'] = @{ a = $idA; b = $idB; c = $idC }

# ---------------------------------------------------------------- D3: the audit of one set
Write-Host "`n== audit: one set finds the other ==" -ForegroundColor Cyan
$auA = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-a' -Arguments @{ target_document = $Document; requirement_set = $setA }
$dA = @(Get-HzCodeFindings $auA 'duplicate_bar')
$oA = @(Get-HzCodeFindings $auA 'overlapping_bar')
$dupOnly = Get-HzPath $auA.Result 'overlaps', 'duplicate_only_sets'
Add-HzProbe -Run $run -Id 'D3' -Name 'the audit of one set finds the other set (no rule of it built) as duplicate_bar on every bar pair, and does not judge the crossing bar' `
    -Expected ("one duplicate_bar: rule DA, bar $idA against bar $idB, 5 bar pairs; no overlapping_bar; overlaps.duplicate_only_sets = 2") `
    -Observed ("duplicate_bar={0} {1} | overlapping_bar={2} | duplicate_only_sets={3}" -f $dA.Count,
        $(if ($dA.Count) { 'rule=' + [string](Get-HzProp $dA[0] 'rule_id') + ' bar=' + [string](Get-HzProp $dA[0] 'rebar_id') + ' other=' + [string](Get-HzProp $dA[0] 'other_rebar_id') +
                           ' pairs=' + [string](Get-HzProp $dA[0] 'bar_pairs') + ' dev=' + [string](Get-HzProp $dA[0] 'max_deviation_mm') + ' why: ' + (Limit-HzText ([string](Get-HzProp $dA[0] 'why')) 200) }), $oA.Count, $dupOnly) `
    -Ok ($dA.Count -eq 1 -and [string](Get-HzProp $dA[0] 'rule_id') -eq 'DA' -and [long](Get-HzProp $dA[0] 'rebar_id') -eq $idA -and
         [long](Get-HzProp $dA[0] 'other_rebar_id') -eq $idB -and [int](Get-HzProp $dA[0] 'bar_pairs') -eq 5 -and $oA.Count -eq 0 -and [int]$dupOnly -eq 2) `
    -Evidence @{ findings = $dA; overlaps = (Get-HzPath $auA.Result 'overlaps') }

# ---------------------------------------------------------------- D4: the audit of both rules
$dAB = @(Get-HzCodeFindings $auAB 'duplicate_bar')
$oAB = @(Get-HzCodeFindings $auAB 'overlapping_bar' | Where-Object { @([string](Get-HzProp $_ 'rule_id'), [string](Get-HzProp $_ 'other_rule_id')) -contains 'DB' })
Add-HzProbe -Run $run -Id 'D4' -Name 'the audit of the two rules as one set reports duplicate_bar between them once, never also as overlapping_bar' `
    -Expected 'one duplicate_bar between DA and DB (both bar ids, 5 bar pairs); no overlapping_bar between them' `
    -Observed ("duplicate_bar={0} {1} | overlapping_bar(DA,DB)={2}" -f $dAB.Count,
        $(if ($dAB.Count) { [string](Get-HzProp $dAB[0] 'rule_id') + '/' + [string](Get-HzProp $dAB[0] 'other_rule_id') + ' pairs=' + [string](Get-HzProp $dAB[0] 'bar_pairs') }), $oAB.Count) `
    -Ok ($dAB.Count -eq 1 -and ((@([string](Get-HzProp $dAB[0] 'rule_id'), [string](Get-HzProp $dAB[0] 'other_rule_id')) | Sort-Object) -join ',') -eq 'DA,DB' -and
         ((@([long](Get-HzProp $dAB[0] 'rebar_id'), [long](Get-HzProp $dAB[0] 'other_rebar_id')) | Sort-Object) -join ',') -eq ((@($idA, $idB) | Sort-Object) -join ',') -and
         [int](Get-HzProp $dAB[0] 'bar_pairs') -eq 5 -and $oAB.Count -eq 0) `
    -Evidence @{ findings = $dAB; overlaps = (Get-HzPath $auAB.Result 'overlaps') }

# ---------------------------------------------------------------- D5: the review of the wall
Write-Host "`n== steel_review: no requirement set ==" -ForegroundColor Cyan
$rv = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'review-wall' -Arguments @{ mode = 'steel_review'; host_id = $wall; tolerance_mm = 2; max_rows = 50 }).Result
$walls = @(@(Get-HzProp $rv 'by_host_category') | Where-Object { $_ })
$pairsRv = @(@(Get-HzProp $rv 'pairs') | Where-Object { $_ })
$dupPair = @($pairsRv | Where-Object { [string]$_.code -eq 'duplicate_bar' })
$hitIds = @($pairsRv | Where-Object { [string]$_.code -eq 'overlapping_bar' } | ForEach-Object { ((@([long]$_.rebar_id, [long]$_.other_rebar_id) | Sort-Object) -join ',') } | Sort-Object)
$wantHits = @(((@($idA, $idC) | Sort-Object) -join ','), ((@($idB, $idC) | Sort-Object) -join ',')) | Sort-Object
Add-HzProbe -Run $run -Id 'D5' -Name 'steel_review of the wall, no requirement set: exact counts by kind and host category, complete coverage' `
    -Expected ("3 sets; duplicates 1 set pair / 5 bar pairs ($idA,$idB); intersections 2 set pairs / 10 bar pairs ({0}); one host category; verdict findings; coverage complete" -f ($wantHits -join ' ')) `
    -Observed ("sets={0} dup={1}/{2} hits={3}/{4} [{5}] categories={6} ({7}) verdict={8} coverage={9} bars_compared={10} elapsed={11} ms" -f
        (Get-HzPath $rv 'counts', 'sets'), (Get-HzPath $rv 'counts', 'duplicates', 'set_pairs'), (Get-HzPath $rv 'counts', 'duplicates', 'bar_pairs'),
        (Get-HzPath $rv 'counts', 'intersections', 'set_pairs'), (Get-HzPath $rv 'counts', 'intersections', 'bar_pairs'), ($hitIds -join ' '),
        $walls.Count, (($walls | ForEach-Object { [string]$_.host_category }) -join ','), (Get-HzProp $rv 'verdict'), (Get-HzPath $rv 'coverage', 'coverage'),
        (Get-HzPath $rv 'counts', 'bars_compared'), (Get-HzProp $rv 'elapsed_ms')) `
    -Ok ([int](Get-HzPath $rv 'counts', 'sets') -eq 3 -and [int](Get-HzPath $rv 'counts', 'duplicates', 'set_pairs') -eq 1 -and [int](Get-HzPath $rv 'counts', 'duplicates', 'bar_pairs') -eq 5 -and
         [int](Get-HzPath $rv 'counts', 'intersections', 'set_pairs') -eq 2 -and [int](Get-HzPath $rv 'counts', 'intersections', 'bar_pairs') -eq 10 -and
         $dupPair.Count -eq 1 -and ((@([long]$dupPair[0].rebar_id, [long]$dupPair[0].other_rebar_id) | Sort-Object) -join ',') -eq ((@($idA, $idB) | Sort-Object) -join ',') -and
         ($hitIds -join ' ') -eq ($wantHits -join ' ') -and $walls.Count -eq 1 -and [int]$walls[0].duplicates.set_pairs -eq 1 -and [int]$walls[0].intersections.bar_pairs -eq 10 -and
         [string](Get-HzProp $rv 'verdict') -eq 'findings' -and [string](Get-HzPath $rv 'coverage', 'coverage') -eq 'complete' -and [int](Get-HzPath $rv 'counts', 'bars_compared') -eq 11) `
    -Evidence @{ review = $rv }

# ---------------------------------------------------------------- D6: no tolerance
$noTol = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'review-no-tolerance' -Arguments @{ mode = 'steel_review'; host_id = $wall }
Add-HzProbe -Run $run -Id 'D6' -Name 'steel_review without a tolerance is refused, and nothing is reviewed' `
    -Expected 'an error naming tolerance_required' -Observed ("is_error={0} {1}" -f [bool]$noTol.IsError, (Limit-HzText ([string]$noTol.Text) 200)) `
    -Ok ([bool]$noTol.IsError -and [string]$noTol.Text -match 'tolerance_required') -Evidence @{ text = $noTol.Text }

# ---------------------------------------------------------------- D7: the whole model
$all = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'review-model' -Arguments @{ mode = 'steel_review'; tolerance_mm = 2; max_rows = 500 }).Result
$allDup = @(@(Get-HzProp $all 'pairs') | Where-Object { $_ -and [string]$_.code -eq 'duplicate_bar' -and ((@([long]$_.rebar_id, [long]$_.other_rebar_id) | Sort-Object) -join ',') -eq ((@($idA, $idB) | Sort-Object) -join ',') })
Add-HzProbe -Run $run -Id 'D7' -Name 'steel_review over the whole model finds at least the wall pairs and says how long it took' `
    -Expected 'sets >= 3, duplicates >= 1 including the wall pair, intersections >= 2, elapsed and read time reported' `
    -Observed ("sets={0} system_bars={1} fabric_not_compared={2} dup={3}/{4} hits={5}/{6} close={7} verdict={8} coverage={9} read={10} ms review={11} ms" -f
        (Get-HzPath $all 'counts', 'sets'), (Get-HzPath $all 'counts', 'system_bars'), (Get-HzPath $all 'counts', 'fabric_sheets_not_compared'),
        (Get-HzPath $all 'counts', 'duplicates', 'set_pairs'), (Get-HzPath $all 'counts', 'duplicates', 'bar_pairs'),
        (Get-HzPath $all 'counts', 'intersections', 'set_pairs'), (Get-HzPath $all 'counts', 'intersections', 'bar_pairs'),
        (Get-HzPath $all 'counts', 'closer_than_declared_clearance', 'set_pairs'), (Get-HzProp $all 'verdict'), (Get-HzPath $all 'coverage', 'coverage'),
        (Get-HzProp $all 'read_ms'), (Get-HzProp $all 'elapsed_ms')) `
    -Ok ([int](Get-HzPath $all 'counts', 'sets') -ge 3 -and [int](Get-HzPath $all 'counts', 'duplicates', 'set_pairs') -ge 1 -and $allDup.Count -eq 1 -and
         [int](Get-HzPath $all 'counts', 'intersections', 'set_pairs') -ge 2 -and $null -ne (Get-HzProp $all 'read_ms') -and $null -ne (Get-HzProp $all 'elapsed_ms')) `
    -Evidence @{ counts = (Get-HzProp $all 'counts'); by_host_category = (Get-HzProp $all 'by_host_category'); coverage = (Get-HzProp $all 'coverage') }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
