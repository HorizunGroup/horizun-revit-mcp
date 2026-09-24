#Requires -Version 5.1
# Exercises styles-units-electrical.probes.ps1 WITHOUT Revit: fake Call / Apply
# answer like the three tools and horizun_delete_verified would.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'styles-units-electrical.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'styles-units-electrical' }

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
# global: GetNewClosure only resolves functions in the global scope (fails under -Command "& x.ps1").
function global:Obj($h) { return ($h | ConvertTo-Json -Depth 20 | ConvertFrom-Json) }

function New-Fakes([bool]$panels, [bool]$breakRestore) {
    $state = @{ accuracy = 0.01; subcats = @(); patterns = @(); applied = New-Object System.Collections.ArrayList; restoreBroken = $breakRestore }
    $call = {
        param($tool, $arguments)
        $op = $arguments.operation
        switch ("$tool/$op") {
            'horizun_manage_styles/list_object_styles' {
                if ($arguments.category) {
                    return @{ isError = $false; data = (Obj @{ category = @{ name = 'Generic Models'; subcategories = @($state.subcats) }; count = $state.subcats.Count }) }
                }
                return @{ isError = $false; data = (Obj @{ count = 120 }) }
            }
            'horizun_manage_styles/list_line_patterns' { return @{ isError = $false; data = (Obj @{ count = 5 + $state.patterns.Count; solid_pattern_id = -3000010; line_patterns = @($state.patterns) }) } }
            'horizun_manage_units/read' { return @{ isError = $false; data = (Obj @{ specs = @(@{ spec = 'autodesk.spec.aec:length-2.0.0'; unit = 'autodesk.unit.unit:millimeters-1.0.1'; accuracy = $state.accuracy; symbol = $null }) }) } }
            'horizun_manage_units/project_information' { return @{ isError = $false; data = (Obj @{ count = 14; fields = @{ name = 'Sample' } }) } }
            'horizun_electrical/list_panels' {
                $p = if ($panels) { @(@{ id = 901; is_panel = $true }) } else { @() }
                return @{ isError = $false; data = (Obj @{ count = $p.Count; panels = $p }) }
            }
            'horizun_electrical/list_circuits' { return @{ isError = $false; data = (Obj @{ count = 0; circuits = @() }) } }
        }
        throw "unexpected call $tool/$op"
    }.GetNewClosure()
    $ok = { param($result) @{ stage = 'apply'; answer = @{ isError = $false; data = (Obj @{ state = 'committed_verified'; postconditions = @{ all_verified = $true }; result = $result }) } } }
    $apply = {
        param($tool, $arguments, $key)
        [void]$state.applied.Add("$tool/$($arguments.operation)/$key")
        switch ("$tool/$($arguments.operation)") {
            'horizun_manage_styles/create_subcategory' {
                $state.subcats += @{ name = $arguments.name; projection_weight = $arguments.projection_weight; color = $arguments.color }
                return (& $ok @{ id = 777 })
            }
            'horizun_manage_styles/create_line_pattern' {
                $state.patterns += @{ name = $arguments.name; segments = @(1, 2, 3, 4) }
                return (& $ok @{ line_pattern_id = 888 })
            }
            'horizun_manage_units/set' {
                if ($key -eq 'sue-acc-restore' -and $state.restoreBroken) { return @{ stage = 'dry_run'; answer = @{ isError = $true; text = 'refused' } } }
                $state.accuracy = $arguments.accuracy
                return (& $ok @{})
            }
            'horizun_electrical/panel_schedule' { return (& $ok @{ panel_schedule_view_id = 999 }) }
            'horizun_delete_verified/' { return @{ stage = 'apply'; answer = @{ isError = $false } } }
        }
        throw "unexpected apply $tool/$($arguments.operation)"
    }.GetNewClosure()
    return @{ Call = $call; Apply = $apply; State = $state }
}

function Invoke-Probe($fakes, [bool]$gate) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $gate
                              Call = $fakes.Call; Apply = $fakes.Apply }
    $by = @{}
    foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }
    return $by
}

Check 'the module registers itself with eight catalogued cases' ($module -and @($module.Catalog).Count -eq 8)

$f = New-Fakes $true $false
$r = Invoke-Probe $f $false
Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $r.ContainsKey($_.Name) }).Count -eq 0)
Check 'every case passes against well-behaved tools' (@($r.Values | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the length accuracy is restored to its original value' ($f.State.accuracy -eq 0.01)
Check 'what the probe created is deleted again' (@($f.State.applied | Where-Object { $_ -like 'horizun_delete_verified*' }).Count -eq 3)

$f = New-Fakes $false $false
$r = Invoke-Probe $f $false
Check 'no electrical equipment makes the schedule case not_covered, with a reason' (
    $r['electrical: create a panel schedule for a panel without one and re-read its panel'].Outcome -eq 'not_covered' -and
    $r['electrical: create a panel schedule for a panel without one and re-read its panel'].Detail -match 'no electrical equipment')
Check 'the electrical reads still pass on an empty document' ($r['electrical: list_panels and list_circuits read the document'].Outcome -eq 'pass')

$f = New-Fakes $true $true
$r = Invoke-Probe $f $false
Check 'a restore that does not verify fails the units case' ($r['units: change length accuracy, re-read it, restore the original and re-read again'].Outcome -eq 'fail')

$f = New-Fakes $true $false
$r = Invoke-Probe $f $true
Check 'a gated write tier reports the writes not_covered and applies nothing' (
    @($r.Values | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 4 -and $f.State.applied.Count -eq 0)
Check 'reads still run when writes are gated' ($r['units: read reports the length format'].Outcome -eq 'pass')

if ($fails) { "styles-units-electrical tests: $fails FAILED"; exit 1 } else { 'styles-units-electrical tests: ALL PASS'; exit 0 }
