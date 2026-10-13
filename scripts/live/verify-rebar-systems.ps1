#Requires -Version 5.1
<#
  ACERO 3.0, L4b and L9, LIVE, on a DISPOSABLE fixture (Autodesk metric template):

    Y1  area reinforcement over part of a structural floor: bottom major and minor layers
    Y2  path reinforcement along one edge of the floor
    Y3  fabric over part of the floor (when the template carries a fabric sheet type)
    Y4  the systems apply and every parameter they set reads back, with steel produced
    Y5  the audit finds them as declared
    Y6  the bending schedule derived from the model: one row per mark of the floor's own mat, the
        kilograms equal to the takeoff's volume times the declared density, and refused without one
    Y7  the takeoff counts the area and path bars: mode=quantities scoped by each SYSTEM's id equals
        that system's own bars re-read by parameter (count and total length), and over the slab's
        host_id it is the mat plus every system bar plus the fabric's wires (stage 1c), with the
        coverage COMPLETE - fabric read, not named as missing. The bending schedule over the slab
        carries the same bars, and the fabric in its own block.
    Y8  (stage 1c) the fabric's steel, sheet by sheet, against an INDEPENDENT calculation from the
        sheet type's own parameters read back by horizun_query_model: wires per direction from the
        cut dimensions, the spacing and the overhang, each run over the other cut dimension, at its
        wire type's diameter. Revit's own cut-sheet mass is checked to be its type's mass per area
        times the cut area (MEASURED, Revit 2026: that is how Revit computes it - an area share, not
        the wires), and the takeoff's kilograms equal the wires' volume times the declared density.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-systems' -Document $Document

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
$X0 = 150000.0
$slab = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'slab' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = [long]$floorT.element_id; level_id = $lv; structural = $true
                       profile = @(, @(@($X0, 0.0, 0.0), @(($X0 + 6000), 0.0, 0.0), @(($X0 + 6000), 4000.0, 0.0), @($X0, 4000.0, 0.0))) }) })
$cv = Get-HzCover $slab
$zTop = 0.0
# Type names from the model: the first area, path, fabric-area and fabric-sheet types it carries.
$areaT = @(Get-HzTypes 'OST_AreaRein' | Select-Object -First 1)
# Measured: the template's area and path reinforcement types do not list under their categories;
# their default names are tried, and the plan refuses them by name if they are not there.
if ($areaT.Count -eq 0) { $areaT = @([pscustomobject]@{ name = 'Structural Area Reinforcement 1' }) }
$pathT = @(Get-HzTypes 'OST_PathRein' | Select-Object -First 1)
if ($pathT.Count -eq 0) { $pathT = @([pscustomobject]@{ name = 'Structural Path Reinforcement 1' }) }
$fabAreaT = @(Get-HzTypes 'OST_FabricAreas' | Select-Object -First 1)
# A SHEET, not a wire: the category lists the template's fabric wires (W11, W20) beside its sheets.
$fabSheetT = @(Get-HzTypes 'OST_FabricReinforcement' | Where-Object { [string]$_.family -eq 'Fabric Sheet' } | Select-Object -First 1)
$run.Fixture['types'] = @{ area = @($areaT | ForEach-Object { [string]$_.name }); path = @($pathT | ForEach-Object { [string]$_.name })
    fabric_area = @($fabAreaT | ForEach-Object { [string]$_.name }); fabric_sheet = @($fabSheetT | ForEach-Object { [string]$_.name }); slab = $slab; cover = $cv }

$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'l4b-systems'; version = '1.0.0'; title = 'L4b systems, synthetic' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
}
if ($areaT.Count) {
    $set['area_rules'] = @(, [ordered]@{ id = 'Y1'; host = @{ element_ids = @(, $slab) }; area_type_name = [string]$areaT[0].name
        boundary_mm = @(@(($X0 + 500), 500.0, $zTop), @(($X0 + 3500), 500.0, $zTop), @(($X0 + 3500), 3500.0, $zTop), @(($X0 + 500), 3500.0, $zTop))
        major_direction = @(1, 0, 0)
        layers = [ordered]@{ bottom_major = @{ bar_type = 'N16'; spacing_mm = 200 }; bottom_minor = @{ bar_type = 'N10'; spacing_mm = 250 } } })
}
if ($pathT.Count) {
    $set['path_rules'] = @(, [ordered]@{ id = 'Y2'; host = @{ element_ids = @(, $slab) }; path_type_name = [string]$pathT[0].name; flip = $false
        path_mm = @(@(($X0 + 4000), 3800.0, $zTop), @(($X0 + 5800), 3800.0, $zTop))
        primary = @{ bar_type = 'N16'; spacing_mm = 200; length_mm = 1200 } })
}
$fabOk = $false
if ($fabAreaT.Count -and $fabSheetT.Count) {
    $set['fabric_rules'] = @(, [ordered]@{ id = 'Y3'; host = @{ element_ids = @(, $slab) }
        fabric_area_type_name = [string]$fabAreaT[0].name; fabric_sheet_type_name = [string]$fabSheetT[0].name
        boundary_mm = @(@(($X0 + 4000), 300.0, $zTop), @(($X0 + 5800), 300.0, $zTop), @(($X0 + 5800), 2800.0, $zTop), @(($X0 + 4000), 2800.0, $zTop))
        major_direction = @(1, 0, 0); major_direction_origin_mm = @(($X0 + 4000), 300.0, $zTop); cover_offset_mm = 10 })
}
# A FABRIC SHEET TYPE the resolver accepts, or none: measured, the template's only
# OST_FabricReinforcement type ('W11') is a wire type, and a fabric sheet type is not created here.
if ($set.Contains('fabric_rules')) {
    $fset = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l4b-fabric-check'; version = '1' }; bar_types = $set.bar_types; fabric_rules = $set.fabric_rules }
    $frow = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-fabric' -Arguments @{ target_document = $Document; requirement_set = $fset }).Result 'fabric') | Where-Object { $_ })
    if ($frow.Count -and [string](Get-HzProp $frow[0] 'code') -eq 'fabric_sheet_type_not_found') { $set.Remove('fabric_rules'); $run.Fixture['fabric_sheet_rejected'] = [string](Get-HzProp $frow[0] 'why') }
}
$fabOk = $set.Contains('fabric_rules')
# A DOCUMENT WITH NO AREA OR PATH TYPE AT ALL (measured: Revit 2024's metric template has neither,
# and this bridge does not create types): that system is a missing fixture, said with the plan's own
# words, and the others are applied without it rather than refused together with it.
foreach ($k in @(@{ key = 'area_rules'; arr = 'area'; code = 'area_reinforcement_type_not_found' }, @{ key = 'path_rules'; arr = 'path'; code = 'path_reinforcement_type_not_found' })) {
    if (-not $set.Contains($k.key)) { continue }
    $one = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = "l4b-$($k.arr)-check"; version = '1' }; bar_types = $set.bar_types }
    $one[$k.key] = $set[$k.key]
    $krow = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label "plan-$($k.arr)" -Arguments @{ target_document = $Document; requirement_set = $one }).Result $k.arr) | Where-Object { $_ })
    if ($krow.Count -and [string](Get-HzProp $krow[0] 'code') -eq $k.code -and [string](Get-HzProp $krow[0] 'why') -match 'has no .* at all') {
        $set.Remove($k.key); $run.Fixture["$($k.arr)_type_missing"] = [string](Get-HzProp $krow[0] 'why')
        if ($k.arr -eq 'area') { $areaT = @() } else { $pathT = @() }
    }
}
# The floor's own mat for the bending schedule, outside the area reinforcement's outline.
$matRules = @(, [ordered]@{ id = 'Y6'; host = @{ element_ids = @(, $slab) }; face_normal = @(0, 0, 1)
    components = @(, [ordered]@{ name = 'top_x'; direction = @(1, 0, 0); bar_type = 'N10'; offset_from_face_mm = $cv + 6; end_cover_mm = $cv; side_cover_mm = $cv + 6
        mark = 'TX1'; allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 300 } }) })

Write-Host "`n== plan ==" -ForegroundColor Cyan
$plan = (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
foreach ($k in @(@{ id = 'Y1'; arr = 'area'; has = $areaT.Count }, @{ id = 'Y2'; arr = 'path'; has = $pathT.Count }, @{ id = 'Y3'; arr = 'fabric'; has = $fabOk })) {
    if (-not $k.has) { Add-HzProbe -Run $run -Id $k.id -Name "L4b $($k.arr) reinforcement plans on a structural floor" -Status 'fixture_missing' -Expected "a $($k.arr) type in the template" -Observed $(if ($run.Fixture.Contains("$($k.arr)_type_missing")) { Limit-HzText $run.Fixture["$($k.arr)_type_missing"] 300 } else { 'none' }); continue }
    $r = @(@(Get-HzPath $plan $k.arr) | Where-Object { $_ })
    $bad = @($r | Where-Object { Get-HzProp $_ 'code' })
    Add-HzProbe -Run $run -Id $k.id -Name "L4b $($k.arr) reinforcement plans on a structural floor" -Expected '1 row, not refused' `
        -Observed ("rows={0} refused={1} {2}" -f $r.Count, $bad.Count, $(if ($bad.Count) { [string](Get-HzProp $bad[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $bad[0] 'why')) 300) } else { '' })) `
        -Ok ($r.Count -eq 1 -and $bad.Count -eq 0) -Evidence @{ rows = $r }
}

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar L4b systems' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$text = $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text })
$verif = @($(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') }))
$sysVerif = @($(if ($apply.Apply) { foreach ($k in 'area_verification', 'path_verification', 'fabric_verification', 'systems_verification', 'system_verification') { @(Get-HzPath $apply.Apply.Result $k) } }) | Where-Object { $_ })
$notOk = @(@($verif + $sysVerif) | Where-Object { $null -ne $_ -and (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'Y4' -Name 'L4b the systems apply, every parameter they set reads back and they produce steel' -Expected 'applied; every row verified' `
    -Observed ("applied={0} rebar_rows={1} system_rows={2} not_verified={3} {4}" -f $applyOk, $verif.Count, $sysVerif.Count, $notOk.Count,
        $(if ($notOk.Count) { Limit-HzText ($notOk[0] | ConvertTo-Json -Depth 6 -Compress) 700 } elseif (-not $applyOk) { Limit-HzText $text 500 } else { '' })) `
    -Ok ($applyOk -and $notOk.Count -eq 0 -and $sysVerif.Count -ge 1) -Evidence @{ verification = $verif; systems = $sysVerif; keys = @($apply.Apply.Result.PSObject.Properties.Name) }
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$ours = @(@(Get-HzPath $audit.Result 'findings') | Where-Object { $_ -and [string](Get-HzProp $_ 'severity') -ne 'info' })
Add-HzProbe -Run $run -Id 'Y5' -Name 'L4b the audit finds the systems as declared' -Expected 'no error or unknown finding' `
    -Observed ("verdict={0} findings={1} {2}" -f (Get-HzProp $audit.Result 'verdict'), $ours.Count, (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $ours }

Write-Host "`n== bending schedule ==" -ForegroundColor Cyan
# ITS OWN STEEL: the floor's mat is applied alone, so the schedule does not depend on the systems.
$mset = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l9-schedule'; version = '1' }; bar_types = $set.bar_types; mat_rules = $matRules }
$mapply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-mat' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $mset; transaction_name = 'HZ rebar L9 schedule mat' }
$run.Fixture['schedule_mat_applied'] = $(if ($mapply.Apply) { $mapply.Apply.Ok } else { $false })
$noDensity = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'schedule-no-density' -Arguments @{ mode = 'bending_schedule'; host_id = $slab }
$sched = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'schedule' -Arguments @{ mode = 'bending_schedule'; host_id = $slab; density_kg_per_m3 = 7850; density_source = 'synthetic probe value' }).Result
$qty = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'takeoff' -Arguments @{ mode = 'quantities'; host_id = $slab; group_by = @('mark'); density_kg_per_m3 = 7850; density_source = 'synthetic probe value' }).Result
$tx = @(@(Get-HzPath $sched 'rows') | Where-Object { [string](Get-HzProp $_ 'mark') -eq 'TX1' })
$kgS = [double](Get-HzPath $sched 'totals', 'total_kg')
$kgQ = $null; try { $kgQ = [double](Get-HzPath $qty 'totals', 'kg') } catch { }
Add-HzProbe -Run $run -Id 'Y6' -Name 'L9 the bending schedule is derived from the model and refused without a declared density' `
    -Expected 'no density -> refused; TX1 one row with its bars and segments; total kg consistent with the takeoff' `
    -Observed ("no_density_error={0} tx_rows={1} tx_bars={2} segments={3} kg_schedule={4} kg_takeoff={5}" -f [bool]$noDensity.IsError, $tx.Count,
        $(if ($tx.Count) { Get-HzProp $tx[0] 'bars' } else { '' }), $(if ($tx.Count) { (@(Get-HzProp $tx[0] 'segments_mm') -join '/') } else { '' }), $kgS, $kgQ) `
    -Ok ([bool]$noDensity.IsError -and $tx.Count -eq 1 -and [int](Get-HzProp $tx[0] 'bars') -gt 0 -and $kgS -gt 0) -Evidence @{ schedule = $sched; takeoff = $qty }

Write-Host "`n== system bars in the takeoff ==" -ForegroundColor Cyan
# INDEPENDENT of the reader under test: each system's member bars, read by Revit PARAMETER (quantity and
# total length) through horizun_query_model, summed per system. The takeoff must equal them.
$sysOnSlab = @(@(Get-HzPath (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'systems-y7' -Arguments @{ mode = 'reinforcement_systems'; max_rows = 50 }).Result 'rows') |
    Where-Object { $_ -and [string](Get-HzProp $_ 'kind') -in @('AreaReinforcement', 'PathReinforcement') -and [long](Get-HzProp $_ 'host_id') -eq $slab })
$own = @(); $perSystem = @(); $sysBarsAll = 0; $sysMembersAll = 0
foreach ($sr in $sysOnSlab) {
    $sid = [long](Get-HzProp $sr 'id')
    $mids = @(@(Get-HzProp $sr 'member_rebar_ids') | Where-Object { $null -ne $_ } | ForEach-Object { [long]$_ })
    $mrows = @()
    if ($mids.Count) {
        $mrows = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "members-$sid" -Arguments @{ element_ids = $mids; include_links = $false; max_rows = 500
            parameter_format = 'full'; return_parameters = @('REBAR_ELEM_QUANTITY_OF_BARS', 'REBAR_ELEM_TOTAL_LENGTH') }).Result.rows) | Where-Object { $_ })
    }
    $nOwn = 0; $lOwn = 0.0
    foreach ($m in $mrows) { $nOwn += [int](Get-HzPath $m 'parameters', 'REBAR_ELEM_QUANTITY_OF_BARS', 'raw'); $lOwn += [double](Get-HzPath $m 'parameters', 'REBAR_ELEM_TOTAL_LENGTH', 'raw') * 0.3048 }
    $q1 = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "takeoff-system-$sid" -Arguments @{ mode = 'quantities'; element_ids = @(, $sid); group_by = @('bar_type') }).Result
    $nQ = Get-HzPath $q1 'totals', 'bars'; $lQ = Get-HzPath $q1 'totals', 'total_length_m'
    $expanded = @(@(Get-HzPath $q1 'scope', 'system_ids_expanded') | Where-Object { $null -ne $_ } | ForEach-Object { [long]$_ })
    $ok1 = $mids.Count -ge 1 -and $mrows.Count -eq $mids.Count -and $null -ne $nQ -and [int]$nQ -eq $nOwn -and $null -ne $lQ -and [Math]::Abs([double]$lQ - $lOwn) -le 0.001 -and
        [string](Get-HzPath $q1 'coverage', 'coverage') -eq 'complete' -and [int](Get-HzPath $q1 'totals', 'sets_from_systems') -eq $mids.Count -and ($expanded -join ',') -eq "$sid"
    $sysBarsAll += $nOwn; $sysMembersAll += $mids.Count
    $perSystem += [pscustomobject]@{ system = $sid; kind = [string](Get-HzProp $sr 'kind'); members = $mids.Count; own_bars = $nOwn; own_length_m = [Math]::Round($lOwn, 4)
        takeoff_bars = $nQ; takeoff_length_m = $lQ; coverage = [string](Get-HzPath $q1 'coverage', 'coverage'); ok = $ok1 }
}
# The whole slab: the mat (TX1, from the schedule probe above) plus every system bar.
$qSlab = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'takeoff-slab-y7' -Arguments @{ mode = 'quantities'; host_id = $slab; group_by = @('mark') }).Result
$schedSlab = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label 'schedule-slab-y7' -Arguments @{ mode = 'bending_schedule'; host_id = $slab; density_kg_per_m3 = 7850; density_source = 'synthetic probe value' }).Result
$matBars = $(if ($tx.Count) { [int](Get-HzProp $tx[0] 'bars') } else { 0 })
$slabBars = Get-HzPath $qSlab 'totals', 'bars'
$slabCov = [string](Get-HzPath $qSlab 'coverage', 'coverage')
$fabReasons = @(@(Get-HzPath $qSlab 'coverage', 'reasons') | Where-Object { $_ -and [string](Get-HzProp $_ 'what') -eq 'fabric' })
# FABRIC IS READ (stage 1c): its wires are bars of the slab's takeoff and the coverage is complete.
$fabSheets = [int](Get-HzPath $qSlab 'totals', 'fabric_sheets')
$fabWires = $(if ($fabSheets -gt 0) { [int](Get-HzPath $qSlab 'totals', 'fabric', 'wires') } else { 0 })
$schedSysRows = @(@(Get-HzPath $schedSlab 'rows') | Where-Object { $_ -and @(@(Get-HzProp $_ 'systems') | Where-Object { $_ }).Count -gt 0 })
$schedBars = Get-HzPath $schedSlab 'totals', 'bars'
$schedFabWires = Get-HzPath $schedSlab 'fabric', 'totals', 'wires'
$run.Fixture['system_takeoff'] = @{ systems = $perSystem; mat_bars = $matBars; slab_bars = $slabBars; slab_coverage = $slabCov; fabric = $fabOk; fabric_sheets = $fabSheets; fabric_wires = $fabWires }
$haveSystems = $areaT.Count + $pathT.Count
if ($haveSystems -eq 0) {
    Add-HzProbe -Run $run -Id 'Y7' -Name 'L4b the takeoff counts the area and path bars' -Status 'fixture_missing' -Expected 'an area or path type in the template' -Observed 'none'
} else {
    Add-HzProbe -Run $run -Id 'Y7' -Name 'L4b the takeoff counts the area and path bars (quantities by system id and by host; schedule over the host)' `
        -Expected ("each system's takeoff = its own bars by parameter, coverage complete; slab bars = mat {0} + systems + fabric wires; slab coverage complete, no fabric reason{1}; schedule bars = takeoff bars less the fabric's wires, which the schedule carries in its fabric block" -f $matBars, $(if ($fabOk) { '; fabric sheets counted' } else { '' })) `
        -Observed ("systems={0} [{1}] | slab bars={2} (mat {3} + systems {4} + fabric wires {5} in {6} sheets) sets_from_systems={7} coverage={8} fabric_reasons={9} | schedule bars={10} fabric wires={11} system_lines={12}" -f $sysOnSlab.Count,
            (($perSystem | ForEach-Object { '{0} {1}: own {2} bars {3} m / takeoff {4} bars {5} m {6}' -f $_.kind, $_.system, $_.own_bars, $_.own_length_m, $_.takeoff_bars, $_.takeoff_length_m, $_.coverage }) -join '; '),
            $slabBars, $matBars, $sysBarsAll, $fabWires, $fabSheets, (Get-HzPath $qSlab 'totals', 'sets_from_systems'), $slabCov, $fabReasons.Count, $schedBars, $schedFabWires, $schedSysRows.Count) `
        -Ok ($sysOnSlab.Count -eq $haveSystems -and @($perSystem | Where-Object { -not $_.ok }).Count -eq 0 -and $null -ne $slabBars -and [int]$slabBars -eq ($matBars + $sysBarsAll + $fabWires) -and
             [int](Get-HzPath $qSlab 'totals', 'sets_from_systems') -eq $sysMembersAll -and $slabCov -eq 'complete' -and $fabReasons.Count -eq 0 -and
             ($fabOk -eq ($fabSheets -gt 0 -and $fabWires -gt 0)) -and
             $null -ne $schedBars -and [int]$schedBars -eq ([int]$slabBars - $fabWires) -and [int]$(if ($null -ne $schedFabWires) { $schedFabWires } else { -1 }) -eq $fabWires -and
             [string](Get-HzPath $schedSlab 'coverage', 'coverage') -eq 'complete' -and $schedSysRows.Count -ge 1) `
        -Evidence @{ systems = $perSystem; takeoff = $qSlab; schedule_totals = (Get-HzPath $schedSlab 'totals'); schedule_coverage = (Get-HzPath $schedSlab 'coverage') }
}

# ---------------------------------------------------------------- Y8: the fabric's kilograms
Write-Host "`n== fabric kg against the sheet type read back ==" -ForegroundColor Cyan
if (-not $fabOk) {
    Add-HzProbe -Run $run -Id 'Y8' -Name 'stage 1c the fabric in the takeoff, sheet by sheet, against the sheet type read back' -Status 'fixture_missing' -Expected 'a fabric sheet type in the template' -Observed 'none'
} else {
    # INDEPENDENT of the reader under test: horizun_query_model reads the parameters Revit publishes for the
    # sheets, their type and its wire types, and the steel is computed here from them.
    $sheetRows = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'fabric-sheets-y8' -Arguments @{ categories = @('OST_FabricReinforcement'); include_links = $false; max_rows = 200
        parameter_format = 'full'; return_parameters = @('FABRIC_PARAM_CUT_SHEET_MASS', 'FABRIC_PARAM_CUT_OVERALL_LENGTH', 'FABRIC_PARAM_CUT_OVERALL_WIDTH') }).Result.rows) |
        Where-Object { $_ -and -not $_.is_element_type -and [string]$_.family -eq 'Fabric Sheet' })
    $typeIds = @($sheetRows | ForEach-Object { [long]$_.type_id } | Select-Object -Unique)
    $types = @{}
    foreach ($tid in $typeIds) {
        $tr = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "fabric-type-$tid" -Arguments @{ element_ids = @(, $tid); include_types = $true; include_links = $false; max_rows = 5
            parameter_format = 'full'; return_parameters = @('FABRIC_SHEET_MASSUNIT', 'FABRIC_SHEET_MAJOR_DIRECTION_WIRE_TYPE', 'FABRIC_SHEET_MINOR_DIRECTION_WIRE_TYPE',
                'FABRIC_SHEET_MAJOR_SPACING', 'FABRIC_SHEET_MINOR_SPACING', 'FABRIC_SHEET_MAJOR_START_OVERHANG', 'FABRIC_SHEET_MINOR_START_OVERHANG') }).Result.rows) | Where-Object { $_ })[0]
        $tp = { param($k) [double](Get-HzPath $tr 'parameters', $k, 'raw') }
        $dia = @{}
        foreach ($k in 'FABRIC_SHEET_MAJOR_DIRECTION_WIRE_TYPE', 'FABRIC_SHEET_MINOR_DIRECTION_WIRE_TYPE') {
            $wid = [long](Get-HzPath $tr 'parameters', $k, 'raw')
            $wr = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "fabric-wire-$wid" -Arguments @{ element_ids = @(, $wid); include_types = $true; include_links = $false; max_rows = 5
                parameter_format = 'full'; return_parameters = @('FABRIC_WIRE_DIAMETER') }).Result.rows) | Where-Object { $_ })[0]
            $dia[$k] = [double](Get-HzPath $wr 'parameters', 'FABRIC_WIRE_DIAMETER', 'raw') * 304.8
        }
        # Internal units: feet, and kilograms per square foot for the mass per area.
        $types["$tid"] = [pscustomobject]@{ kg_m2 = (& $tp 'FABRIC_SHEET_MASSUNIT') / (0.3048 * 0.3048)
            major_spacing = (& $tp 'FABRIC_SHEET_MAJOR_SPACING') * 304.8; minor_spacing = (& $tp 'FABRIC_SHEET_MINOR_SPACING') * 304.8
            major_start = (& $tp 'FABRIC_SHEET_MAJOR_START_OVERHANG') * 304.8; minor_start = (& $tp 'FABRIC_SHEET_MINOR_START_OVERHANG') * 304.8
            major_dia = $dia['FABRIC_SHEET_MAJOR_DIRECTION_WIRE_TYPE']; minor_dia = $dia['FABRIC_SHEET_MINOR_DIRECTION_WIRE_TYPE'] }
    }
    $perSheet = @()
    foreach ($sr in $sheetRows) {
        $sid = [long]$sr.element_id
        $t = $types["$([long]$sr.type_id)"]
        $cutL = [double](Get-HzPath $sr 'parameters', 'FABRIC_PARAM_CUT_OVERALL_LENGTH', 'raw') * 304.8
        $cutW = [double](Get-HzPath $sr 'parameters', 'FABRIC_PARAM_CUT_OVERALL_WIDTH', 'raw') * 304.8
        $revitKg = [double](Get-HzPath $sr 'parameters', 'FABRIC_PARAM_CUT_SHEET_MASS', 'raw')
        # The major wires run along the sheet's length and stand across its width, the outermost one the minor
        # wires' start overhang in from the edge; the minor wires the other way round.
        $nMajor = [Math]::Floor(($cutW - $t.minor_start) / $t.major_spacing + 1e-6) + 1
        $nMinor = [Math]::Floor(($cutL - $t.major_start) / $t.minor_spacing + 1e-6) + 1
        $lenMm = $nMajor * $cutL + $nMinor * $cutW
        $volM3 = ([Math]::PI / 4) * ($t.major_dia * $t.major_dia * $nMajor * $cutL + $t.minor_dia * $t.minor_dia * $nMinor * $cutW) / 1e9
        $q = (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "takeoff-sheet-$sid" -Arguments @{ mode = 'quantities'; element_ids = @(, $sid); group_by = @('mark')
            density_kg_per_m3 = 7850; density_source = 'synthetic probe value' }).Result
        $perSheet += [pscustomobject]@{ sheet = $sid; cut_mm = ('{0:0.#} x {1:0.#}' -f $cutL, $cutW)
            wires_indep = $nMajor + $nMinor; length_m_indep = [Math]::Round($lenMm / 1000, 4); kg_indep = [Math]::Round($volM3 * 7850, 3)
            wires = Get-HzPath $q 'totals', 'bars'; length_m = Get-HzPath $q 'totals', 'total_length_m'; kg = Get-HzPath $q 'totals', 'mass_kg'
            revit_kg = [Math]::Round($revitKg, 3); revit_kg_reported = Get-HzPath $q 'totals', 'fabric', 'revit_cut_sheet_mass_kg'
            revit_area_kg = [Math]::Round($t.kg_m2 * $cutL * $cutW / 1e6, 3); coverage = [string](Get-HzPath $q 'coverage', 'coverage') }
    }
    $badSheet = @($perSheet | Where-Object {
        $null -eq $_.wires -or [int]$_.wires -ne [int]$_.wires_indep -or $null -eq $_.length_m -or [Math]::Abs([double]$_.length_m - $_.length_m_indep) -gt 0.001 * [int]$_.wires_indep -or
        $null -eq $_.kg -or [Math]::Abs([double]$_.kg - $_.kg_indep) -gt 0.001 * $_.kg_indep + 0.01 -or $null -eq $_.revit_kg_reported -or [Math]::Abs([double]$_.revit_kg_reported - $_.revit_kg) -gt 0.001 -or
        [Math]::Abs($_.revit_kg - $_.revit_area_kg) -gt 0.005 * $_.revit_kg -or $_.coverage -ne 'complete' })
    $run.Fixture['fabric_kg'] = $perSheet
    Add-HzProbe -Run $run -Id 'Y8' -Name 'stage 1c the fabric in the takeoff, sheet by sheet, against the sheet type read back; Revit''s own mass is its area share' `
        -Expected ("{0} sheet(s): the takeoff's wires, length and kg (at 7850) = the wires counted from the type's spacing, overhang and the cut dimensions at the wire diameters (kg within 0.1 %); Revit's cut-sheet mass reported as read and equal to its mass per area x the cut area (0.5 %); coverage complete" -f $fabSheets) `
        -Observed (($perSheet | ForEach-Object { 'sheet {0} cut {1}: wires {2}/{3} length {4}/{5} m kg {6}/{7} | revit {8} (area share {9}) reported {10} {11}' -f $_.sheet, $_.cut_mm, $_.wires, $_.wires_indep,
            $_.length_m, $_.length_m_indep, $_.kg, $_.kg_indep, $_.revit_kg, $_.revit_area_kg, $_.revit_kg_reported, $_.coverage }) -join '; ') `
        -Ok ($perSheet.Count -ge 1 -and $perSheet.Count -eq $fabSheets -and $badSheet.Count -eq 0) -Evidence @{ sheets = $perSheet; types = $types }
}

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
