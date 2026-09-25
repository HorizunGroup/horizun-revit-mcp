#Requires -Version 5.1
# Exercises worksets-ownership.probes.ps1 WITHOUT Revit.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'worksets-ownership.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'worksets-ownership' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$workshared, [bool]$hasWall = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; workshared = $workshared }
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_list_elements') {
            $rows = if ($hasWall) { @([pscustomobject]@{ element_id = 501; source_kind = 'host' }) } else { @() }
            return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
        }
        if ($tool -eq 'horizun_manage_worksets') {
            if (-not $state.workshared) { return @{ isError = $true; text = 'not workshared'; data = [pscustomobject]@{ code = 'not_workshared' } } }
            return @{ isError = $false; data = [pscustomobject]@{ worksets = @([pscustomobject]@{ workset_id = 0; name = 'Workset1' }) } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        $effect = [pscustomobject]@{ measured = $true; elements_examined = 0; elements_newly_owned_by_me = 0 }
        $data = [pscustomobject]@{
            workset_id       = 42
            ownership_effect = $effect
        }
        if ($key -eq 'own-rename') {
            $data | Add-Member relinquish_after ([pscustomobject]@{ attempted = $true; elements_still_owned_by_me = 0 })
        }
        $dry = @{ data = [pscustomobject]@{ plan = [pscustomobject]@{ move = @([pscustomobject]@{ from_workset_id = 0 }) } } }
        return @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' }; dry = $dry }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

# A. write tier closed
$by = Run-Module (New-Ctx $true $true)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

# B. not workshared
$by = Run-Module (New-Ctx $false $false)
Check 'not workshared: every case not_covered with the reason' (
    (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0) -and
    ($by[$catalog[0]].Detail -match 'not workshared'))

# C. workshared, a free wall available
$ctx = New-Ctx $false $true $true
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
Check 'create reports a measured ownership_effect' ($by[$catalog[0]].Outcome -eq 'pass')
Check 'rename reports ownership_effect and relinquish_after' ($by[$catalog[1]].Outcome -eq 'pass' -and $by[$catalog[1]].Detail -match 'relinquish')
Check 'move_elements reports ownership_effect' ($by[$catalog[2]].Outcome -eq 'pass')
Check 'a moved element is moved back' ($ctx.State.applies.Contains('own-move-back'))

# D. workshared, no free wall
$by = Run-Module (New-Ctx $false $true $false)
Check 'move_elements is not_covered without a free wall' ($by[$catalog[2]].Outcome -eq 'not_covered')

if ($fails) { "worksets-ownership probe tests: $fails FAILED"; exit 1 } else { 'worksets-ownership probe tests: ALL PASS'; exit 0 }
