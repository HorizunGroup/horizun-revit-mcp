#Requires -Version 5.1
# Exercises views-schedules-dwg.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply
# that answer the way the bridge does: everything verifies; the write tier is closed; the
# DWG layer write does not persist; and the three failures MEASURED on Revit 2026 on
# 2026-09-24 (inherited filters on a duplicated view, the precedence report around them,
# an empty DWG layer table on a new setup).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'views-schedules-dwg.probes.ps1')
$module = @($script:HzProbeModules | Where-Object { $_.Name -eq 'views-schedules-dwg' })[0]

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-vg-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

# $inherited: the filters Revit copies onto a duplicated view (MEASURED 2026-09-24: five
# filters on the own view, not two). $dwgMode: 'rows' (the table has rows), 'empty_refused'
# (the default seeds are empty and the tool refuses; AIA gives rows), 'empty_json' (the
# pre-fix behaviour: a created setup whose json holds no rows).
function New-Fake([bool]$persist, [long[]]$inherited = @(150, 151), [string]$dwgMode = 'rows') {
    $state = @{ calls = New-Object System.Collections.Generic.List[string]; persist = $persist
                order = New-Object System.Collections.Generic.List[long]; disabled = New-Object System.Collections.Generic.List[long]
                inherited = @($inherited); dwgMode = $dwgMode; sentOrder = $null; aia = $null
                names = New-Object System.Collections.Generic.List[string] }
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
                    # Every filter on the view, in its CURRENT order - inherited ones included.
                    $layers = @([pscustomobject]@{ source = 'element' })
                    foreach ($fid in $state.order) { $layers += [pscustomobject]@{ source = 'filter'; filter_id = $fid; enabled = ($state.disabled -notcontains $fid) } }
                    $report = [pscustomobject]@{
                        layers = $layers
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
            'vg-create' {
                $state.source = $arguments.actions[0].source_view_id
                foreach ($i in $state.inherited) { $state.order.Add($i) }
                $state.order.Add(201); $state.order.Add(202)
                return (& $ok @{ aliases = [pscustomobject]@{ dup = 200; f1 = 201; f2 = 202 }; actions_verified = 6 })
            }
            'vg-edit' { return (& $ok @{ rows = @([pscustomobject]@{ graphics = [pscustomobject]@{ rules_reread = "OR(P(FilterStringRule[-1001203]FilterStringContains'HZ-EDIT'),P(x))" } }) }) }
            'vg-order' {
                # The tool's contract: filter_ids lists EXACTLY the filters on the view.
                $sent = @($arguments.actions[0].filter_ids | ForEach-Object { [long]$_ })
                $state.sentOrder = $sent
                $same = ($sent.Count -eq $state.order.Count) -and (@($sent | Where-Object { $state.order -notcontains $_ }).Count -eq 0)
                if (-not $same) {
                    return @{ stage = 'dry_run'; answer = @{ isError = $true; data = $null
                              text = "filter_ids must list EXACTLY the filters on view 'HZ_VG' (" + ($state.order -join ', ') + ') in the new order' } }
                }
                $state.order.Clear(); foreach ($i in $sent) { $state.order.Add($i) }
                $state.disabled.Add([long]$arguments.actions[1].filter_id)
                return (& $ok @{ rows = @([pscustomobject]@{ graphics = [pscustomobject]@{ order = @($state.order.ToArray()) } }) })
            }
            'vg-category' { return (& $ok @{ actions_verified = 1 }) }
            'vg-tpl-create' { return (& $ok @{ aliases = [pscustomobject]@{ tpl = 300 } }) }
            'vg-tpl-apply' { return (& $ok @{ actions_verified = 2 }) }
            'vg-tpl-remove' { return (& $ok @{ actions_verified = 1 }) }
            'vg-sched-multi' { return (& $ok @{ schedule_id = 400; category_id = -1; body_rows = 3 }) }
            'vg-sched-key' { return (& $ok @{ schedule_id = 401; postcondition = [pscustomobject]@{ properties = @([pscustomobject]@{ property = 'key_rows'; found = 2 }) } }) }
            'vg-dwg-read' {
                if ($state.dwgMode -eq 'empty_refused') {
                    return @{ stage = 'dry_run'; answer = @{ isError = $true; data = $null
                              text = "dwg_setup 'x' was not created: every seed gave it an EMPTY layer table (revit_default -> 0 rows). Nothing was changed." } }
                }
                if ($state.dwgMode -eq 'empty_json') {
                    Set-Content -LiteralPath $arguments.output_path -Value '{"rows":[]}'
                    return (& $ok @{ setup_id = 500; seeded_from = $null })
                }
                Set-Content -LiteralPath $arguments.output_path -Value '{"rows":[{"category":"Walls","special":"Default","layer":"A-WALL"}]}'
                return (& $ok @{ setup_id = 500; seeded_from = 'revit_default' })
            }
            'vg-dwg-read-aia' {
                $state.aia = $arguments.dwg_setup.layer_standard
                Set-Content -LiteralPath $arguments.output_path -Value '{"rows":[{"category":"Walls","special":"Default","layer":"A-WALL"}]}'
                return (& $ok @{ setup_id = 500; seeded_from = 'layer_standard:AIA' })
            }
            'vg-dwg-write' {
                $state.names.Add([string]$arguments.dwg_setup.layers[0].category)
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

# 4) MEASURED case 1: the duplicated view inherits filters. The probe must send the
#    WHOLE list (the fake refuses a partial one, as the tool does) and move only its own.
$f = New-Fake $true @(1364036, 576429, 576430)
$by = Invoke-Module $f $false 'r-4'
Check 'with inherited filters the reorder case passes' ($by['views-vg: reorder and disable filters, re-read order and state'].Outcome -eq 'pass')
Check 'the full list was sent, inherited filters in place and only 201/202 swapped' (($f.state.sentOrder -join ',') -eq '1364036,576429,576430,202,201')
# 5) MEASURED case 2: the precedence report holds the inherited filters above the module's.
Check 'the precedence case finds its filters by id among inherited ones' ($by['views-vg: precedence report for one element'].Outcome -eq 'pass')
Check 'the precedence detail names their real positions' ($by['views-vg: precedence report for one element'].Detail -match 'f2 at 4 \(disabled\) above f1 at 5 among 5 filters')
# 6) inherited filters interleaved with the module's: they keep their slots.
$reorder = $module.FilterOrder
Check 'FilterOrder keeps foreign filters in their slots' (((& $reorder @(150, 201, 151, 202) @(202, 201)) -join ',') -eq '150,202,151,201')
Check 'FilterOrder refuses when a module filter is missing' ($null -eq (& $reorder @(150, 201) @(202, 201)))
Check 'FilterOrder with only the module filters is the plain swap' (((& $reorder @(201, 202) @(202, 201)) -join ',') -eq '202,201')

# 7) MEASURED case 3: every default seed empty -> the tool refuses, the probe measures the AIA seed.
$f = New-Fake $true @(150) 'empty_refused'
$by = Invoke-Module $f $false 'r-7'
$c9 = $by['views-vg: DWG setup create and layer table read to json']
Check 'an empty default table is retried with layer_standard AIA' ($f.state.aia -eq 'AIA')
Check 'the read case passes and records both the refusal and the seed' ($c9.Outcome -eq 'pass' -and $c9.Detail -match 'layer_standard:AIA' -and $c9.Detail -match 'EMPTY')
Check 'the write then runs against the seeded setup' ($by['views-vg: DWG layer table write persists (measured per year)'].Outcome -eq 'pass')
Check 'the DWG write names OST_Walls, not a display name that follows the language' (($f.state.names -join ',') -eq 'OST_Walls')
# 8) the pre-fix behaviour (a created setup with no rows) still FAILS the read case.
$f = New-Fake $true @(150) 'empty_json'
$by = Invoke-Module $f $false 'r-8'
Check 'a created setup with an empty table is a failure' ($by['views-vg: DWG setup create and layer table read to json'].Outcome -eq 'fail')

Get-ChildItem -LiteralPath $scratch -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
Remove-Item -LiteralPath $scratch
if ($fails) { "views-schedules-dwg probe tests: $fails FAILED"; exit 1 } else { 'views-schedules-dwg probe tests: ALL PASS'; exit 0 }
