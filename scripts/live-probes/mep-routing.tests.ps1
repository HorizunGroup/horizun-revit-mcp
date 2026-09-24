#Requires -Version 5.1
# Exercises mep-routing.probes.ps1 WITHOUT Revit: its Run against a fake Call/Apply that
# keeps a tiny model (elbow rules, segment sizes, one duct) and answers in the reply shapes
# horizun_mep_routing and horizun_list_elements publish.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'mep-routing.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'mep-routing' }

function New-Fake([hashtable]$Break = @{}) {
    $s = [pscustomobject]@{
        Elbows = [System.Collections.ArrayList]@('Elbow A'); Sizes = [System.Collections.ArrayList]@(15.0, 20.0, 25.0)
        Width = 400.0; Height = 250.0; Break = $Break; Calls = [System.Collections.ArrayList]@()
    }
    $obj = { param($h) ($h | ConvertTo-Json -Depth 20 | ConvertFrom-Json) }
    $call = {
        param($tool, $a)
        [void]$s.Calls.Add("$tool/$($a.operation)")
        if ($tool -eq 'horizun_list_elements') { return @{ isError = $false; data = (& $obj @{ rows = @(@{ element_id = 900 }) }) } }
        switch ($a.operation) {
            'read' {
                if ($a.type_id) {
                    $rules = @($s.Elbows | ForEach-Object { @{ part = @{ id = 77; name = 'Elbow: std' }; description = $_; size_ranges = @() } })
                    return @{ isError = $false; data = (& $obj @{ type = @{ id = 5; preferred_junction = 'Tee'; rule_groups = @{ Elbows = $rules; Segments = @() } } }) }
                }
                if ($a.segment_id) { return @{ isError = $false; data = (& $obj @{ segment = @{ id = 6; sizes = @($s.Sizes | ForEach-Object { @{ nominal = $_ } }) } }) } }
                if ($a.element_ids) {
                    return @{ isError = $false; data = (& $obj @{ elements = @(@{ element_id = 900; kind = 'duct_rectangular'; size = @{ width = $s.Width; height = $s.Height }; size_in_catalog = $true }) }) }
                }
                return @{ isError = $false; data = (& $obj @{
                    types = @(@{ id = 5; class = 'PipeType'; has_routing_preferences = $true }, @{ id = 8; class = 'DuctType'; has_routing_preferences = $true })
                    segments = @(@{ id = 6; size_count = $s.Sizes.Count })
                    duct_sizes = @{ round = @(@{ nominal = 100 }); rectangular = @(@{ nominal = 300 }, @{ nominal = 400 }, @{ nominal = 500 }) } }) }
            }
            'size_by_flow' {
                return @{ isError = $false; data = (& $obj @{ writes = $false; rows = @(@{ element_id = 900; proposed = @{ width = 300; height = 250 }; velocity_mps = 2.7 }) }) }
            }
        }
        return @{ isError = $true; text = 'unexpected call'; data = $null }
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        [void]$s.Calls.Add("apply/$($a.operation)/$key")
        if ($s.Break.ContainsKey($key)) { return @{ stage = 'apply'; answer = @{ isError = $true; text = $s.Break[$key]; data = $null } } }
        $result = @{}
        switch ($a.operation) {
            'set_rules' { $r = $a.rules[0]; if ($r.action -eq 'add') { [void]$s.Elbows.Add($r.description) } else { $s.Elbows.RemoveAt($r.index) } }
            'add_sizes' { [void]$s.Sizes.Add([double]$a.sizes[0].nominal) }
            'remove_sizes' { $s.Sizes.Remove([double]$a.sizes[0].nominal) }
            'resize' { $s.Width = [double]$a.width; $s.Height = [double]$a.height; $result = @{ fittings_added = @(); fittings_removed_or_replaced = @() } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $false; data = (& $obj @{ state = 'committed_verified'; postconditions = @{ all_verified = $true }; result = $result }) } }
    }.GetNewClosure()
    return [pscustomobject]@{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $true; Call = $call; Apply = $apply } }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Outcomes($cases) { $h = @{}; foreach ($c in $cases) { $h[$c.Name] = $c.Outcome }; $h }

# 1. Everything answers as a working bridge would: every catalogued case passes and the model is left as found.
$f = New-Fake
$cases = @(& $module.Run $f.Ctx)
$o = Outcomes $cases
Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $o.ContainsKey($_.Name) }).Count -eq 0)
Check 'a working bridge passes every case' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the elbow rule added is removed again' ($f.State.Elbows.Count -eq 1)
Check 'the segment size added is removed again' ($f.State.Sizes.Count -eq 3)
Check 'the duct is resized back to its original width' ($f.State.Width -eq 400)
Check 'the duct went to a different catalog width in between' (@($f.State.Calls | Where-Object { $_ -eq 'apply/resize/mep-resize-there' }).Count -eq 1)

# 2. A failed apply is a failure of that case, and the probe does not try to undo what was never done.
$f = New-Fake @{ 'mep-size-add' = 'refused: nominal in use' }
$o = Outcomes @(& $module.Run $f.Ctx)
Check 'a refused add_sizes fails its case' ($o['mep_routing: add_sizes puts a size on a pipe segment and remove_sizes takes it off, both re-read'] -eq 'fail')
Check 'no remove is attempted after a refused add' (@($f.State.Calls | Where-Object { $_ -like 'apply/remove_sizes*' }).Count -eq 0)

# 3. A resize back that fails is a fail, never a pass on the outbound half alone.
$f = New-Fake @{ 'mep-resize-back' = 'rolled back' }
$o = Outcomes @(& $module.Run $f.Ctx)
Check 'a failed resize back fails the resize case' ($o['mep_routing: resize moves a duct to another catalog size and back, re-read both times'] -eq 'fail')

if ($fails) { "mep-routing probe tests: $fails FAILED"; exit 1 } else { 'mep-routing probe tests: ALL PASS'; exit 0 }
