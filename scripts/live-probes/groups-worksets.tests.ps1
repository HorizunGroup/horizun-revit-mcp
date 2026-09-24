#Requires -Version 5.1
# Exercises groups-worksets.probes.ps1 WITHOUT Revit: fake Call/Apply stand in for
# the bridge, and every catalogued case must come back with the expected outcome.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'groups-worksets.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'groups-worksets' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [int]$walls, [bool]$workshared, [hashtable]$dryFail = @{}) {
    $state = @{ deleted = $false; wallCount = $walls; workshared = $workshared; applies = New-Object System.Collections.Generic.List[string]; dryFail = $dryFail }
    $call = {
        param($tool, $arguments)
        $op = $arguments.operation
        if ($tool -eq 'horizun_list_elements') {
            $rows = @(1..$state.wallCount | ForEach-Object { [pscustomobject]@{ element_id = 100 + $_; source_kind = 'host' } })
            return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
        }
        if ($tool -eq 'horizun_manage_groups') {
            if ($op -eq 'convert_to_link') { return @{ isError = $true; text = 'refused'; data = [pscustomobject]@{ code = 'api_absent' } } }
            $types = @()
            if ($state.applies.Contains('grp-ungroup') -and -not $state.deleted) { $types = @([pscustomobject]@{ type_id = 900; name = 'HZ_PROBE_GRP_r1_B'; instances = @() }) }
            return @{ isError = $false; data = [pscustomobject]@{ types = $types; type_count = $types.Count; instance_count_returned = 0 } }
        }
        if ($tool -eq 'horizun_manage_worksets') {
            if (-not $state.workshared) { return @{ isError = $true; text = 'not workshared'; data = [pscustomobject]@{ code = 'not_workshared' } } }
            return @{ isError = $false; data = [pscustomobject]@{ worksets = @([pscustomobject]@{ workset_id = 0; name = 'Workset1'; editable = $true }) } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        # A refused rehearsal: the shape Invoke-WriteApply returns when the dry run itself fails.
        if ($state.dryFail.ContainsKey($key)) { return @{ stage = 'dry_run'; answer = @{ isError = $true; data = $null; text = $state.dryFail[$key] } } }
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = $true }
        $data = [pscustomobject]@{ host_verified = $true; workset_id = 7
            result = [pscustomobject]@{ group_id = 500 + $state.applies.Count; type_id = 900 + $state.applies.Count }
            plan = [pscustomobject]@{ move = @([pscustomobject]@{ from_workset_id = 0 }) } }
        return @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' }; dry = @{ data = $data } }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

# A. the disposable fixture, not workshared, three free walls
$ctx = New-Ctx $false 3 $false
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
Check 'every group case passes against a bridge that verifies' (@($catalog | Where-Object { $_ -like 'groups:*' -and $by[$_].Outcome -ne 'pass' }).Count -eq 0)
Check 'the not-workshared refusal is a pass' ($by['worksets: a model that is not workshared is refused typed (not_workshared)'].Outcome -eq 'pass')
Check 'workset writes are not_covered with the reason' (($by['worksets: create, rename and move an element on a workshared model'].Outcome -eq 'not_covered') -and ($by['worksets: create, rename and move an element on a workshared model'].Detail -match 'not workshared'))
Check 'the probe deletes the types it left' ($ctx.State.applies.Contains('grp-cleanup'))

# B. write tier closed
$by = Run-Module (New-Ctx $true 3 $false)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

# C. too few walls
$by = Run-Module (New-Ctx $false 1 $false)
Check 'too few walls is not_covered, never a pass' ($by['groups: create a model group of two walls and re-read its members'].Outcome -eq 'not_covered')

# D. a workshared model
$ctx = New-Ctx $false 3 $true
$by = Run-Module $ctx
Check 'on a workshared model the writes run and pass' ($by['worksets: create, rename and move an element on a workshared model'].Outcome -eq 'pass')
Check 'on a workshared model the refusal case is not_covered' ($by['worksets: a model that is not workshared is refused typed (not_workshared)'].Outcome -eq 'not_covered')
Check 'the moved element is moved back' ($ctx.State.applies.Contains('ws-move-back'))

# E. the live refusal of 2026-09-24 (Revit 2026): the ungroup rehearsal did not verify. The case
#    fails with the bridge's words, and the probe still deletes the types it created.
$msg = 'Error: The group rehearsal could not verify the change (failed: members_released). Nothing was committed.'
$ctx = New-Ctx $false 3 $false @{ 'grp-ungroup' = $msg }
$by = Run-Module $ctx
$u = $by['groups: ungroup and delete what the probe created']
Check 'a refused ungroup rehearsal fails its case' ($u.Outcome -eq 'fail')
Check 'the failure carries the stage and the failing property' ($u.Detail -like '*stage=dry_run*' -and $u.Detail -like '*members_released*')
Check 'the earlier group cases still pass' (@(@('groups: create a model group of two walls and re-read its members', 'groups: rename, duplicate and swap the group type') | Where-Object { $by[$_].Outcome -ne 'pass' }).Count -eq 0)
Check 'cleanup still runs after a refused ungroup' ($ctx.State.applies.Contains('grp-cleanup'))

if ($fails) { "groups-worksets probe tests: $fails FAILED"; exit 1 } else { 'groups-worksets probe tests: ALL PASS'; exit 0 }
