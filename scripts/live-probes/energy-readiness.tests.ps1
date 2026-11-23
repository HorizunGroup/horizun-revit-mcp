#Requires -Version 5.1
# Exercises energy-readiness.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# The fakes' reply shapes come from the code (CodeCheckEnergy.cs, EnergyReadinessRules.ByOrientation)
# - to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'energy-readiness.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'energy-readiness' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function Reset {
    $script:nextId = 5000; $script:ids = @{}; $script:deleted = $null; $script:energyCalls = 0
    $script:have = @{ walls = $true; windows = $true }; $script:listLoose = $true; $script:windowsAfter = 1
    # HZ_WRITE's energy settings export MEP spaces (MEASURED 2026-09-27); 'missing' names own kinds
    # the fake lists in not_in_energy_model, and built=$false answers as a model that was not built.
    $script:scope = "MEP spaces of phase 'New Construction'"; $script:missing = @(); $script:built = $true
    # gbXML's dry run publishes main_energy_model_present; 'flip' makes the second read differ.
    $script:mainReads = 0; $script:mainFlip = $false
}
Reset
$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_Walls' { if ($script:have.walls) { @([pscustomobject]@{ element_id = 21; family = 'Basic Wall'; type = 'Generic - 200mm'; is_element_type = $true }) } else { @() } }
            'OST_Windows' { if ($script:have.windows) { @([pscustomobject]@{ element_id = 22; family = 'M_Fixed'; type = '0915 x 1220mm'; is_element_type = $true }) } else { @() } }
            default { @() }
        }
        return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
    }
    if ($tool -eq 'horizun_export' -and $arguments.format -eq 'gbxml' -and $arguments.dry_run) {
        $script:mainReads++
        $present = $script:mainFlip -and $script:mainReads -gt 1
        return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; format = 'gbxml'; main_energy_model_present = $present; placed_spaces = 1 } }
    }
    if ($tool -eq 'horizun_code_check' -and $arguments.operation -eq 'energy_readiness') {
        $script:energyCalls++
        if ($script:energyCalls -eq 1) {
            # The baseline, before staging: the fixture has no enclosed room, so no energy model.
            return @{ isError = $false; data = [pscustomobject]@{ operation = 'energy_readiness'
                spaces = [pscustomobject]@{ rooms = 0; spaces = 0; enclosed = 0; unplaced = 0; not_enclosed_count = 0; not_enclosed = @() }
                energy_model = [pscustomobject]@{ built = $false; why = 'no spaces: no placed, enclosed room or space, so the energy model would be empty' }
                surfaces = [pscustomobject]@{ not_measured = 'no energy model was built' }
                window_to_wall = [pscustomobject]@{ not_measured = 'no energy model was built' } } }
        }
        if (-not $script:built) {
            return @{ isError = $false; data = [pscustomobject]@{ operation = 'energy_readiness'
                spaces = [pscustomobject]@{ rooms = 2; spaces = 1; enclosed = 2; unplaced = 0; not_enclosed_count = 0; not_enclosed = @(); energy_scope = $script:scope }
                energy_model = [pscustomobject]@{ built = $false; why = "no spaces: no placed, enclosed $($script:scope), so the energy model would be empty" }
                surfaces = [pscustomobject]@{ not_measured = 'no energy model was built' }
                window_to_wall = [pscustomobject]@{ not_measured = 'no energy model was built' } } }
        }
        $absent = @($script:missing | ForEach-Object { [pscustomobject]@{ id = $script:ids[$_]; category = $_; number = '2'; name = 'Own'; level = 'HZ_ENR_t1' } })
        $loose = if ($script:listLoose) { @([pscustomobject]@{ id = $script:ids.looseroom; category = 'room'; number = '1'; name = 'Room'; level = 'HZ_ENR_t1' }) } else { @() }
        $surfaces = if ($ctxYear -le 2023) {
            [pscustomobject]@{ analytical_surfaces = 7; by_type = [pscustomobject]@{ ExteriorWall = 4 }; without_construction = "not measurable in Revit 2023: EnergyAnalysisSurface.GetConstruction exists from Revit 2024 (RevitAPI.xml 'since 2024')" }
        } else {
            [pscustomobject]@{ analytical_surfaces = 7; by_type = [pscustomobject]@{ ExteriorWall = 4 }; without_construction_count = 0; without_construction = @(); openings_without_construction = 0 }
        }
        $row = { param($o, $n) [pscustomobject]@{ orientation = $o; wall_surfaces = 1; wall_area_m2 = 18.0; windows = $n; window_area_m2 = 1.1 * $n; doors = 0; door_area_m2 = 0; wwr = 0.0 } }
        return @{ isError = $false; data = [pscustomobject]@{ operation = 'energy_readiness'
            spaces = [pscustomobject]@{ rooms = 2; spaces = 1; enclosed = 2; unplaced = 0; not_enclosed_count = $loose.Count; not_enclosed = $loose; energy_scope = $script:scope
                                        not_in_energy_model_count = $absent.Count; not_in_energy_model = $absent }
            energy_model = [pscustomobject]@{ built = $true; rolled_back = $true; azimuth_basis = 'true_north: TransformModel applied the shared coordinates and true north'; analytical_spaces = 1; export_category = 'OST_MEPSpaces' }
            surfaces = $surfaces
            window_to_wall = [pscustomobject]@{ by_orientation = @((& $row 'N' 0), (& $row 'E' 0), (& $row 'S' $script:windowsAfter), (& $row 'W' 0))
                                                total = (& $row 'all' $script:windowsAfter); unmeasured_wall_surfaces = 0 } } }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}
$fakeApply = {
    param($tool, $arguments, $key)
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            $el = $arguments.elements[0]; $kind = $el.kind
            if ($kind -eq 'family_instance' -and $el.host_id -and $el.coordinate_mode -eq 'absolute') { $kind = 'window' }
            if ($kind -eq 'room' -and -not $script:ids.looseroom) { $kind = 'looseroom'; $script:loosePoint = @($el.point) }
            $script:ids[$kind] = $script:nextId
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) } } }
        }
        'horizun_delete_verified' { $script:deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
    }
    return @{ stage = 'refused'; answer = @{ isError = $true; text = "unexpected apply $tool" } }
}

function Ctx($year, $runId) { [pscustomobject]@{ Year = $year; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply } }

$ctxYear = 2026
$cases = @(& $module.Run (Ctx 2026 't1'))
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
$names = @($module.Catalog | ForEach-Object { $_.Name })
Check 'every catalog case is reported once' (($cases.Count -eq $names.Count) -and (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
Check 'the loose room is found by id in spaces.not_enclosed' ($by[$names[0]].Outcome -eq 'pass')
Check 'built and rolled back passes, with the main energy model read before and after' (($by[$names[1]].Outcome -eq 'pass') -and ($by[$names[1]].Detail -match 'main_energy_model_present False -> False') -and ($script:mainReads -eq 2))
Check 'the own walled room and space are enclosed and the exported one is in the energy model' ($by[$names[2]].Outcome -eq 'pass')
Check 'the own space is staged at the walled room point' ($script:ids.space -and $script:ids.room)
Check 'a construction count passes from 2024' ($by[$names[3]].Outcome -eq 'pass')
Check 'four orientations and one more window than the baseline pass' ($by[$names[4]].Outcome -eq 'pass')
Check 'cleanup deletes space, room, window, 4 walls, loose room and level (9 ids, newest first), never the document''s types' (($by[$names[5]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 9) -and ($script:deleted[0] -eq $script:ids.space) -and ($script:deleted[1] -eq $script:ids.room) -and ($script:deleted -notcontains 21) -and ($script:deleted -notcontains 22) -and ($script:deleted[-1] -eq $script:ids.level))
Check 'the loose room is placed by an XY point' (@($script:loosePoint).Count -eq 2)

# ---- the loose room missing from not_enclosed, and no window gained: both FAIL, never pass ----
Reset; $script:listLoose = $false; $script:windowsAfter = 0
$bad = @(& $module.Run (Ctx 2026 't2'))
Check 'a loose room not listed by id fails' ((@($bad | Where-Object { $_.Name -eq $names[0] })[0].Outcome) -eq 'fail')
Check 'no window over the baseline fails the WWR case' ((@($bad | Where-Object { $_.Name -eq $names[4] })[0].Outcome) -eq 'fail')

# ---- a main energy model that appeared across the rolled-back build fails ----
Reset; $script:mainFlip = $true
$mf = @(& $module.Run (Ctx 2026 't10'))
$cm = @($mf | Where-Object { $_.Name -eq $names[1] })[0]
Check 'a main energy model present after but not before fails the built-and-rolled-back case' (($cm.Outcome -eq 'fail') -and ($cm.Detail -match 'False -> True'))

# ---- the export category decides WHICH own element must be in the model ----
Reset; $script:missing = @('space')
$m1 = @(& $module.Run (Ctx 2026 't6'))
Check 'exporting spaces: the own space missing from the model fails' ((@($m1 | Where-Object { $_.Name -eq $names[2] })[0].Outcome) -eq 'fail')
Reset; $script:missing = @('space'); $script:scope = "rooms of phase 'New Construction'"
$m2 = @(& $module.Run (Ctx 2026 't7'))
Check 'exporting rooms: a space outside the model is not a finding' ((@($m2 | Where-Object { $_.Name -eq $names[2] })[0].Outcome) -eq 'pass')
Reset; $script:missing = @('room'); $script:scope = "rooms of phase 'New Construction'"
$m3 = @(& $module.Run (Ctx 2026 't8'))
Check 'exporting rooms: the own room missing from the model fails' ((@($m3 | Where-Object { $_.Name -eq $names[2] })[0].Outcome) -eq 'fail')
Reset; $script:built = $false
$m4 = @(& $module.Run (Ctx 2026 't9'))
$c4 = @($m4 | Where-Object { $_.Name -eq $names[2] })[0]
Check 'no energy model built: the in-model case fails and says why, never passes on an absent list' (($c4.Outcome -eq 'fail') -and ($c4.Detail -match 'no energy model was built'))

# ---- Revit 2023: the construction case passes only on the NAMED not-measurable text ----
Reset; $ctxYear = 2023
$y23 = @(& $module.Run (Ctx 2023 't3'))
Check 'Revit 2023 passes on the named not-measurable text, not on a zero' ((@($y23 | Where-Object { $_.Name -eq $names[3] })[0].Outcome) -eq 'pass')
$ctxYear = 2026

# ---- no wall or window type in the document: the walled-room cases are unverified with the reason ----
Reset; $script:have = @{}
$nt = @(& $module.Run (Ctx 2026 't4'))
$c2 = @($nt | Where-Object { $_.Name -eq $names[1] })[0]
Check 'no types: the built case is unverified and names the missing type' (($c2.Outcome -eq 'unverified') -and ($c2.Detail -match 'holds no OST_Walls type'))
Check 'no types: the loose room case still runs and passes' ((@($nt | Where-Object { $_.Name -eq $names[0] })[0].Outcome) -eq 'pass')
Check 'no types: level and loose room are still deleted (2 ids)' ($script:deleted.Count -eq 2)

$closedCtx = (Ctx 2026 't5'); $closedCtx.WriteGate = $true
$shut = @(& $module.Run $closedCtx)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
if ($fails) { "energy-readiness tests: $fails FAILED"; exit 1 } else { 'energy-readiness tests: ALL PASS'; exit 0 }
