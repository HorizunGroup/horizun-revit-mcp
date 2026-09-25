#Requires -Version 5.1
# Exercises manage-views-sheet-sets.probes.ps1 WITHOUT Revit.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'manage-views-sheet-sets.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'manage-views-sheet-sets' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$hasPlan = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]
                nextId  = 900
                sets    = New-Object System.Collections.Generic.List[object] }
    # Two pre-existing sets the probe never touches.
    $state.sets.Add([pscustomobject]@{ sheet_set_id = 1; name = 'Existing A'; views = @(101) })
    $state.sets.Add([pscustomobject]@{ sheet_set_id = 2; name = 'Existing B'; views = @(102) })

    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_planimetry') {
            $rows = if ($hasPlan) { @([pscustomobject]@{ view_id = 500; view_type = 'FloorPlan'; is_template = $false }) } else { @() }
            return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
        }
        if ($tool -eq 'horizun_manage_views') {
            $op = $arguments.actions[0].operation
            if ($op -eq 'sheet_set_list') {
                $report = @($state.sets | ForEach-Object { [pscustomobject]@{ sheet_set_id = $_.sheet_set_id; name = $_.name; views = $_.views } })
                $plan = @([pscustomobject]@{ index = 0; operation = 'sheet_set_list'; report = $report })
                return @{ isError = $false; data = [pscustomobject]@{ plan = $plan } }
            }
            return @{ isError = $true; text = 'not a dry-run-only op in this fake' }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()

    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{}; text = 'ok' } } }
        if ($tool -ne 'horizun_manage_views') { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected tool ' + $tool } } }
        $a = $arguments.actions[0]
        switch ($a.operation) {
            'duplicate_view' {
                $id = $state.nextId; $state.nextId++
                $row = [pscustomobject]@{ element_id = $id; verified = $true; actual_class = 'ViewPlan' }
                return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row) }; text = 'ok' } }
            }
            'sheet_set_create' {
                $id = $state.nextId; $state.nextId++
                $state.sets.Add([pscustomobject]@{ sheet_set_id = $id; name = [string]$a.name; views = @($a.view_ids) })
                $row = [pscustomobject]@{ element_id = $id; verified = $true; actual_class = 'ViewSheetSet'; graphics = [pscustomobject]@{ view_ids = @($a.view_ids) } }
                return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row) }; text = 'ok' } }
            }
            'sheet_set_update' {
                $set = $state.sets | Where-Object { $_.sheet_set_id -eq [long]$a.sheet_set_id } | Select-Object -First 1
                if ($set) { $set.name = [string]$a.name; $set.views = @($a.view_ids) }
                $row = [pscustomobject]@{ element_id = [long]$a.sheet_set_id; verified = ($null -ne $set) }
                return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row) }; text = 'ok' } }
            }
            'sheet_set_delete' {
                $before = $state.sets.Count
                $state.sets = New-Object System.Collections.Generic.List[object] (,@($state.sets | Where-Object { $_.sheet_set_id -ne [long]$a.sheet_set_id }))
                $row = [pscustomobject]@{ element_id = $null; verified = ($state.sets.Count -eq $before - 1) }
                return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row) }; text = 'ok' } }
            }
            default { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected op ' + $a.operation } } }
        }
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

# B. a floor plan is available
$ctx = New-Ctx $false $true
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
Check 'create re-reads membership' ($by[$catalog[0]].Outcome -eq 'pass')
Check 'update re-reads the new name' ($by[$catalog[1]].Outcome -eq 'pass')
Check 'list finds it and delete leaves other sets alone' ($by[$catalog[2]].Outcome -eq 'pass')
Check 'the pre-existing sets survive untouched' (($ctx.State.sets | Where-Object { $_.name -eq 'Existing A' -or $_.name -eq 'Existing B' }).Count -eq 2)
Check 'the probe cleans up the view it duplicated' ($ctx.State.applies.Contains('ss-cleanup'))

# C. no floor plan available
$by = Run-Module (New-Ctx $false $false)
Check 'no floor plan: every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

if ($fails) { "manage-views-sheet-sets probe tests: $fails FAILED"; exit 1 } else { 'manage-views-sheet-sets probe tests: ALL PASS'; exit 0 }
