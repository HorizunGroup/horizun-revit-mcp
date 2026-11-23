#Requires -Version 5.1
# Exercises egress-travel.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'egress-travel.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'egress-travel' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$script:nextId = 5000
$script:ids = @{}
$script:deleted = $null
$script:endX = 763.0   # metres: the door sits at x = 760 + 3 = 763, y = 0
$script:hasDoor = $true

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_Walls' { @([pscustomobject]@{ element_id = 11; is_element_type = $true }) }
            'OST_Doors' { if ($script:hasDoor) { @([pscustomobject]@{ element_id = 12; is_element_type = $true }) } else { @() } }
            default { @() }
        }
        return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
    }
    if ($tool -eq 'horizun_code_check' -and $arguments.operation -eq 'travel_distance') {
        return @{ isError = $false; data = [pscustomobject]@{ rooms = @([pscustomobject]@{
            room_id = $script:ids.room; distance_m = 4.1; exit_door_id = $script:ids.door; outcome = 'passes'
            polyline_m = @(@(760.3, 3.7), @($script:endX, 0.0)) }) } }
    }
    if ($tool -eq 'horizun_code_check') {
        return @{ isError = $false; data = [pscustomobject]@{ findings = @([pscustomobject]@{ element_id = $script:ids.room; outcome = 'passes'; reason = '' }) } }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}
$fakeApply = {
    param($tool, $arguments, $key)
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            $kind = $arguments.elements[0].kind
            # create_elements has no 'door' kind: the probe must send a hosted family_instance.
            if ($kind -eq 'family_instance' -and $arguments.elements[0].host_id -and $arguments.elements[0].coordinate_mode -eq 'absolute') { $kind = 'door' }
            if ($kind -in @('door', 'room', 'level')) { $script:ids[$kind] = $script:nextId }
            if ($kind -eq 'room') { $script:roomPoint = @($arguments.elements[0].point) }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) } } }
        }
        'horizun_manage_views' {
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ aliases = [pscustomobject]@{ egress = 4000 } } } }
        }
        'horizun_code_check' {
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{
                dry_run = $false; paths_verified = 1; paths = @([pscustomobject]@{ room_id = $script:ids.room; path_id = 4100; verified = $true }) } } }
        }
        'horizun_delete_verified' { $script:deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
    }
    return @{ stage = 'refused'; answer = @{ isError = $true; text = "unexpected apply $tool" } }
}

$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$cases = @(& $module.Run $ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
$names = @($module.Catalog | ForEach-Object { $_.Name })
Check 'every catalog case is reported once' (($cases.Count -eq $names.Count) -and (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
Check 'measure passes: length > 0 and the path ends at the door' ($by[$names[0]].Outcome -eq 'pass')
Check 'the grammar rule passes for the own room' ($by[$names[1]].Outcome -eq 'pass')
Check 'create_paths passes and its path is tracked for cleanup' (($by[$names[2]].Outcome -eq 'pass') -and ($script:deleted -contains 4100))
Check 'cleanup deletes the path, room, door, 4 walls, plan and level (9 ids)' (($by[$names[3]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 9) -and ($script:deleted[0] -eq 4100))

# ---- a route that ends away from the declared door is a fail, not a pass ----
$script:nextId = 5000; $script:ids = @{}; $script:endX = 770.0
$off = @(& $module.Run ([pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't2'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }))
Check 'a path ending 7 m from the door fails the measure case' ((@($off | Where-Object { $_.Name -eq $names[0] })[0].Outcome) -eq 'fail')

# ---- no door type in the fixture: staging is unverified with the reason, never a guess ----
$script:nextId = 5000; $script:ids = @{}; $script:hasDoor = $false; $script:deleted = $null
$none = @(& $module.Run ([pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't4'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }))
$m0 = @($none | Where-Object { $_.Name -eq $names[0] })[0]
Check 'no door: measure is unverified and names the missing door type' (($m0.Outcome -eq 'unverified') -and ($m0.Detail -match 'no door type'))
Check 'no door: what was staged (level, plan, 4 walls) is still deleted' (($script:deleted.Count -eq 6))
$script:hasDoor = $true

$closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
$shut = @(& $module.Run $closed)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))

Check 'the room is placed by an XY point (a room insertion takes no Z; MEASURED 2026-09-26 the XYZ form was refused)' (@($script:roomPoint).Count -eq 2)
if ($fails) { "egress-travel tests: $fails FAILED"; exit 1 } else { 'egress-travel tests: ALL PASS'; exit 0 }
