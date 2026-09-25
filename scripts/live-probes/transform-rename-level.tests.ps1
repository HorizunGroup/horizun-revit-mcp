#Requires -Version 5.1
# Exercises transform-rename-level.probes.ps1 WITHOUT Revit.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'transform-rename-level.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'transform-rename-level' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$hasLevel = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string] }
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_transform_elements') {
            $ops = @($arguments.operations)
            if ($ops.Count -gt 1) { return @{ isError = $true; text = 'rename_level must be the sole operation in its batch: ...' } }
            $op = $ops[0]
            if ($op.operation -ne 'rename_level') { return @{ isError = $true; text = 'unexpected op' } }
            $rehearsal = [pscustomobject]@{ views_expected_to_rename = @(); copy_monitor_alerts = @(); rollback_confirmed = $true }
            $plan = @([pscustomobject]@{ index = 0; operation = 'rename_level'; level_rename_rehearsal = $rehearsal })
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; plan = $plan; confirmation_token = 'tok-1' } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_create_elements') {
            if (-not $hasLevel) { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'refused' } } }
            $rows = @([pscustomobject]@{ element_id = 777 })
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = $rows }; text = 'ok' } }
        }
        if ($tool -eq 'horizun_transform_elements') {
            $name = $arguments.operations[0].name
            $row = [pscustomobject]@{ index = 0; operation = 'rename_level'; verified = $true; level_name = $name; views_renamed_count = 0; copy_monitor_alerts = @() }
            $data = [pscustomobject]@{ rows = @($row) }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected tool ' + $tool } }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

# A. write tier closed
$by = Run-Module (New-Ctx $true)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

# B. a level can be staged
$ctx = New-Ctx $false $true
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
Check 'mixed batch is refused typed' ($by[$catalog[2]].Outcome -eq 'pass')
Check 'dry run reports the rehearsal fields' ($by[$catalog[0]].Outcome -eq 'pass')
Check 'apply renames and reports copy_monitor_alerts' ($by[$catalog[1]].Outcome -eq 'pass')

# C. no level could be staged
$by = Run-Module (New-Ctx $false $false)
Check 'no level staged: every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

if ($fails) { "transform-rename-level probe tests: $fails FAILED"; exit 1 } else { 'transform-rename-level probe tests: ALL PASS'; exit 0 }
