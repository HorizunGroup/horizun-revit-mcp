#Requires -Version 5.1
# Exercises impact-preview.probes.ps1 WITHOUT Revit: its Run block against a fake
# Call that answers with rehearsal shapes, in three worlds - a correct bridge, a bridge
# whose confirmation gate wrongly accepts the full-plan token, and a fixture without
# walls. Pass under pwsh 7: pwsh -NoProfile -File scripts/live-probes/impact-preview.tests.ps1
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'impact-preview.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'impact-preview' }
if (-not $module) { throw 'module did not register' }

function New-Reply($data, [bool]$isError = $false, $text = '') {
    return @{ replied = $true; isError = $isError; text = $text; data = ([pscustomobject]$data | ConvertTo-Json -Depth 20 | ConvertFrom-Json) }
}

function New-FakeCall([int]$walls, [bool]$gateAcceptsWrongToken) {
    $state = @{ n = 0 }
    return {
        param($tool, $arguments)
        $state.n++
        switch ($tool) {
            'horizun_list_elements' {
                $rows = @(); for ($i = 0; $i -lt $walls; $i++) { $rows += @{ element_id = 100 + $i } }
                return New-Reply @{ rows = $rows }
            }
            'horizun_write_params_verified' {
                $writes = @($arguments.writes)
                if ($arguments.dry_run -eq $false) {
                    if ($gateAcceptsWrongToken) { return New-Reply @{ transaction_status = 'Committed' } }
                    return New-Reply @{ state = 'stale_plan' } $true 'The plan CHANGED. (Nothing was changed.)'
                }
                $i = 0
                $rows = @($writes | ForEach-Object { @{ index = $i++; target_id = [string]$_.target_id; parameter = 'Comments' } })
                $prev = @($writes | ForEach-Object { @{ element_id = $_.target_id; unique_id = "u$($_.target_id)" } })
                return New-Reply @{ rows = $rows; change_preview = @{ rows = $prev }
                                    plan_resolved = @{ elements = $writes.Count; fingerprint = "fp$($writes.Count)" }
                                    confirmation_token = "tok$($writes.Count)" }
            }
            'horizun_set_keynote' {
                return New-Reply @{ confirmation_token = 'k'; targets = @(@{ target_id = '9'; requested_elements = @($arguments.element_ids | ForEach-Object { [string]$_ }) }) }
            }
            'horizun_delete_verified' {
                return New-Reply @{ confirmation_token = 'd'; change_preview = @{ rows = @(
                    @{ captured_state = @{ role = 'requested'; raw_id = [string]$arguments.ids[0] } },
                    @{ captured_state = @{ role = 'cascade'; raw_id = '555'; parent_raw_id = [string]$arguments.ids[0] } }) } }
            }
            'horizun_transform_elements' {
                return New-Reply @{ confirmation_token = 't'; change_preview = @{ rows = @($arguments.operations[0].element_ids | ForEach-Object { @{ element_id = $_ } }) } }
            }
            'horizun_create_elements' {
                $created = @(); for ($k = 0; $k -lt @($arguments.elements).Count; $k++) { $created += @{ unique_id = "create:$k" } }
                return New-Reply @{ confirmation_token = 'c'; change_preview = @{ rows = $created } }
            }
            default { throw "unexpected tool $tool" }
        }
    }.GetNewClosure()
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function RunWith($call) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $null; RunId = 'r1'; WriteGate = $true; Call = $call; Apply = $null }
    $by = @{}
    foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }
    return $by
}
$names = @($module.Catalog | ForEach-Object { $_.Name })

$good = RunWith (New-FakeCall 3 $false)
$notPassed = @($names | Where-Object { $good[$_].Outcome -ne 'pass' })
foreach ($n in $notPassed) { "        $n -> $($good[$n].Outcome): $($good[$n].Detail)" }
Check 'a correct bridge passes every catalogued case' ($notPassed.Count -eq 0)
Check 'every reported case is catalogued' (@($good.Keys | Where-Object { $names -notcontains $_ }).Count -eq 0)

$leaky = RunWith (New-FakeCall 3 $true)
$refusal = $leaky['impact-preview: the full plan token is refused for a narrowed request']
Check 'a gate that accepts the full-plan token for a narrowed request FAILS' ($refusal.Outcome -eq 'fail')
Check 'and the failure says it committed' ([string]$refusal.Detail -match 'COMMITTED')

$bare = RunWith (New-FakeCall 1 $false)
Check 'without two walls the wall cases are not_covered, never passed' (
    @($names | Where-Object { $_ -notmatch 'create_elements' } | Where-Object { $bare[$_].Outcome -ne 'not_covered' }).Count -eq 0)
Check 'and the create case still runs' ($bare['impact-preview: create_elements rehearsal numbers its rows create:<index>'].Outcome -eq 'pass')

if ($fails) { "impact-preview probe tests: $fails FAILED"; exit 1 } else { 'impact-preview probe tests: ALL PASS'; exit 0 }
