#Requires -Version 5.1
# Exercises views-schedules-dwg.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply
# that answer the way the bridge does. Three scenarios: everything verifies; the write
# tier is closed; the DWG layer write does not persist (the measured failure).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'views-schedules-dwg.probes.ps1')
$module = @($script:HzProbeModules | Where-Object { $_.Name -eq 'views-schedules-dwg' })[0]

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-vg-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

function New-Fake([bool]$persist) {
    $state = @{ calls = New-Object System.Collections.Generic.List[string]; persist = $persist }
    $call = {
        param($tool, $arguments)
        $state.calls.Add('call:' + $tool)
        switch ($tool) {
            'horizun_query_planimetry' { return @{ isError = $false; data = [pscustomobject]@{ rows = @(
                [pscustomobject]@{ view_id = 100; view_type = 'FloorPlan'; is_template = $true },
                [pscustomobject]@{ view_id = 101; view_type = 'FloorPlan'; is_template = $false }) } } }
            'horizun_query_model' { return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 555 }) } } }
            'horizun_get_schedule_data' { return @{ isError = $false; data = [pscustomobject]@{ body = @() } } }
            'horizun_manage_views' {
                $op = $arguments.actions[0].operation
                if ($op -eq 'explain_graphics') {
                    $report = [pscustomobject]@{
                        layers = @([pscustomobject]@{ source = 'element' },
                                   [pscustomobject]@{ source = 'filter'; filter_id = 202; enabled = $false },
                                   [pscustomobject]@{ source = 'filter'; filter_id = 201; enabled = $true })
                        winners = [pscustomobject]@{ line_color = [pscustomobject]@{ from = 'object_style' }
                                                     visible = [pscustomobject]@{ decided_by = 'no layer hides it' } } }
                    return @{ isError = $false; data = [pscustomobject]@{ plan = @([pscustomobject]@{ report = @($report) }) } }
                }
                if ($op -eq 'set_category_visibility') {
                    return @{ isError = $true; text = 'Error: view takes its model V/G from template (id 300). Edit the template instead - the same action with view_id=300'; data = $null }
                }
            }
        }
        return @{ isError = $true; text = 'unexpected call ' + $tool; data = $null }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.calls.Add('apply:' + $key)
        $ok = { param($data) @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]$data } } }
        switch ($key) {
            'vg-create' { $state.source = $arguments.actions[0].source_view_id; return (& $ok @{ aliases = [pscustomobject]@{ dup = 200; f1 = 201; f2 = 202 }; actions_verified = 6 }) }
            'vg-edit' { return (& $ok @{ rows = @([pscustomobject]@{ graphics = [pscustomobject]@{ rules_reread = "OR(P(FilterStringRule[-1001203]FilterStringContains'HZ-EDIT'),P(x))" } }) }) }
            'vg-order' { return (& $ok @{ rows = @([pscustomobject]@{ graphics = [pscustomobject]@{ order = @(202, 201) } }) }) }
            'vg-category' { return (& $ok @{ actions_verified = 1 }) }
            'vg-tpl-create' { return (& $ok @{ aliases = [pscustomobject]@{ tpl = 300 } }) }
            'vg-tpl-apply' { return (& $ok @{ actions_verified = 2 }) }
            'vg-tpl-remove' { return (& $ok @{ actions_verified = 1 }) }
            'vg-sched-multi' { return (& $ok @{ schedule_id = 400; category_id = -1; body_rows = 3 }) }
            'vg-sched-key' { return (& $ok @{ schedule_id = 401; postcondition = [pscustomobject]@{ properties = @([pscustomobject]@{ property = 'key_rows'; found = 2 }) } }) }
            'vg-dwg-read' {
                Set-Content -LiteralPath $arguments.output_path -Value '{"rows":[{"category":"Walls","layer":"A-WALL"}]}'
                return (& $ok @{ setup_id = 500 })
            }
            'vg-dwg-write' {
                if (-not $state.persist) { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'DWG export setup write failed: the DWG setup did not keep these rows'; data = $null } } }
                return (& $ok @{ edits = @([pscustomobject]@{ key = 'Walls'; persisted = $true }) })
            }
            'vg-dwg-export' {
                Set-Content -LiteralPath $arguments.output_path -Value 'AC1032'
                return (& $ok @{ files_verified = 1 })
            }
            'vg-cleanup' { return (& $ok @{ deleted = 6 }) }
        }
        return @{ stage = 'dry_run'; answer = @{ isError = $true; text = 'unexpected apply ' + $key } }
    }.GetNewClosure()
    return @{ state = $state; call = $call; apply = $apply }
}

function Invoke-Module($fake, [bool]$gate, $run) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = $run; WriteGate = $gate
                              Call = $fake.call; Apply = $fake.apply }
    $by = @{}
    foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }
    return $by
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
$names = @($module.Catalog | ForEach-Object { $_.Name })

# 1) everything verifies
$f = New-Fake $true
$by = Invoke-Module $f $false 'r-1'
Check 'every catalogued case is reported' (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
$notPass = @($names | Where-Object { $by[$_].Outcome -ne 'pass' })
Check ('every case passes on a bridge that verifies (' + ($notPass -join '; ') + ')') ($notPass.Count -eq 0)
Check 'the discovery skipped the template and duplicated view 101' ($f.state.source -eq 101)
Check 'cleanup names every id the module created' ($by['views-vg: cleanup deletes everything the module created'].Detail -match '200,201,202,300,400,401,500')
Check 'the DWG write case says it is a per-year measurement' ($by['views-vg: DWG layer table write persists (measured per year)'].Detail -match 'MEASURED Revit 2026')

# 2) write tier closed: nothing is called and nothing passes
$f = New-Fake $true
$by = Invoke-Module $f $true 'r-2'
Check 'a closed write tier reports every case not_covered' (@($names | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)
Check 'a closed write tier calls nothing' ($f.state.calls.Count -eq 0)

# 3) the layer write does not persist: that case FAILS, never passes quietly
$f = New-Fake $false
$by = Invoke-Module $f $false 'r-3'
Check 'a DWG write that does not persist is a failure' ($by['views-vg: DWG layer table write persists (measured per year)'].Outcome -eq 'fail')
Check 'the other cases are unaffected' ($by['views-vg: DWG export with the named setup to ScratchRoot'].Outcome -eq 'pass')

Get-ChildItem -LiteralPath $scratch -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
Remove-Item -LiteralPath $scratch
if ($fails) { "views-schedules-dwg probe tests: $fails FAILED"; exit 1 } else { 'views-schedules-dwg probe tests: ALL PASS'; exit 0 }
