#Requires -Version 5.1
# Exercises architecture-edits.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'architecture-edits.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'architecture-edits' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$script:nextId = 1000
$script:calls = @()
$script:sent = @{}
$script:overrides = @{}
$script:panelRows = @(@{ element_id = 51; is_element_type = $true; family = 'P'; type = 'Glass' }, @{ element_id = 52; is_element_type = $true; family = 'P'; type = 'Solid' })
$fakeCall = {
    param($tool, $arguments)
    $script:calls += $tool
    if ($tool -eq 'horizun_query_model') {
        $cat = $arguments.categories[0]
        $rows = switch ($cat) {
            'OST_Walls' { @(@{ element_id = 11; is_element_type = $true; family = 'Curtain Wall'; type = 'CW' }, @{ element_id = 12; is_element_type = $true; family = 'Basic Wall'; type = 'Generic' }) }
            'OST_Floors' { @(@{ element_id = 21; is_element_type = $true; family = 'Floor'; type = 'F' }) }
            'OST_StairsRailing' { @(@{ element_id = 31; is_element_type = $true; family = 'Railing'; type = 'R' }) }
            'OST_CurtainWallMullions' { @(@{ element_id = 41; is_element_type = $true; family = 'M'; type = 'M' }) }
            'OST_CurtainWallPanels' { $script:panelRows }
            default { @() }
        }
        return @{ isError = $false; data = [pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) } }
    }
    if ($tool -eq 'horizun_manage_curtain') {
        return @{ isError = $false; data = [pscustomobject]@{ element_id = $arguments.element_id; base_z = 90000
                  counts = [pscustomobject]@{ panels = 1; u_lines = 0; v_lines = 0 }
                  panels = @([pscustomobject]@{ id = 777; type_id = 51 }) } }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}
$fakeApply = {
    param($tool, $arguments, $key)
    $script:calls += "$tool/$key"
    $script:sent[$key] = $arguments
    if ($script:overrides.ContainsKey($key)) { return $script:overrides[$key] }
    $ok = [pscustomobject]@{ postconditions = [pscustomobject]@{ all_verified = $true }; evidence = [pscustomobject]@{ grid_line_id = 555; mullion_ids = @(556); created_ids = @(600); z_convention = @('offset') } }
    switch ($tool) {
        'horizun_create_elements' { $script:nextId++; return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) } } } }
        'horizun_transform_elements' {
            $row = [pscustomobject]@{ verified = $true; created_ids = @(701, 702); array_id = $(if ($arguments.operations[0].group) { 703 } else { $null }) }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row) } } } }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        default {
            if ($key -eq 'arch-slab-reset') { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'rolled back'; data = $null } } }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = $ok } }
        }
    }
}

$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$cases = @(& $module.Run $ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0))
Check 'the curtain cases pass on verified answers' ((@($cases | Where-Object { $_.Tool -eq 'horizun_manage_curtain' -and $_.Outcome -eq 'pass' }).Count -eq 5))
Check 'a panel is changed to a DIFFERENT type than it has' (@($script:calls | Where-Object { $_ -eq 'horizun_manage_curtain/arch-cw-panel' }).Count -eq 1)
Check 'a rolled-back write is a fail, never a pass' ($by['slab shape: reset the shape'].Outcome -eq 'fail')
Check 'both array cases pass on verified rows' ((@($cases | Where-Object { $_.Tool -eq 'horizun_transform_elements' -and $_.Outcome -eq 'pass' }).Count -eq 2))
Check 'cleanup deletes the railing and array ids, the level LAST' (($script:deleted -contains 600) -and ($script:deleted -contains 703) -and ($script:deleted[-1] -eq 1001))
Check 'grouped radial copies are not listed for deletion' (-not ($script:deleted -contains 702 -and @($script:deleted | Where-Object { $_ -eq 701 }).Count -gt 1))

$closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
$shut = @(& $module.Run $closed)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))

Check 'a panel is changed to a panel type when the fixture offers one' ($script:sent['arch-cw-panel'].type_id -eq 52)

# ---- the two failures measured on Revit 2026 (2026-09-24), replayed as answers ---------
# 1. every apply after a rehearsal refused as stale although nobody touched the model.
# 2. set_panel_type refused its type while its own message said a wall type is valid.
$stale = 'THE MODEL MOVED AFTER THE DRY RUN. The request is identical and the document is the same one, but the elements this resolves to - or the values read off them - are not what was rehearsed. What changed: the same elements match and the counts are unchanged, so a value this plan depends on was edited after the dry run (Nothing was changed.)'
$staleAnswer = @{ stage = 'apply'; answer = @{ isError = $true; text = $stale; data = $null } }
$script:overrides = @{
    'arch-cw-line' = $staleAnswer; 'arch-slab-point' = $staleAnswer; 'arch-slab-reset' = $staleAnswer
    'arch-cw-panel' = @{ stage = 'apply'; answer = @{ isError = $true; data = $null
        text = 'type 12 (Wall) is not valid for panel 777: a curtain panel takes a panel type, a curtain-wall door or window type, or a wall type. Nothing was written.' } }
}
# Only the panel's own type and a curtain-wall row: neither is something to change it to.
$script:panelRows = @(@{ element_id = 51; is_element_type = $true; family = 'P'; type = 'Glass' }, @{ element_id = 53; is_element_type = $true; family = 'Curtain Wall'; type = 'CW' })
$script:sent = @{}; $script:calls = @()
$replay = @(& $module.Run $ctx)
$rb = @{}; foreach ($c in $replay) { $rb[$c.Name] = $c }
Check 'replay: a stale refusal of add_grid_line is a fail that quotes it' (($rb['curtain: add a v grid line at a measured offset and re-read it'].Outcome -eq 'fail') -and ($rb['curtain: add a v grid line at a measured offset and re-read it'].Detail -match 'MODEL MOVED'))
Check 'replay: with no grid line, mullions and removal are unverified, never pass' (($rb['curtain: add mullions on the new line and re-read every segment'].Outcome -eq 'unverified') -and ($rb['curtain: remove the added grid line'].Outcome -eq 'unverified'))
Check 'replay: stale add_point and reset_shape are fails' (($rb['slab shape: add a point with an offset and re-read its elevation'].Outcome -eq 'fail') -and ($rb['slab shape: reset the shape'].Outcome -eq 'fail') -and ($rb['slab shape: reset the shape'].Detail -match 'MODEL MOVED'))
Check 'replay: the railing path route is unaffected' ($rb['railing: sketch a railing on a level and re-read its path'].Outcome -eq 'pass')
Check 'replay: without another panel type the probe falls back to a BASIC wall type' (($script:sent['arch-cw-panel'].type_id -eq 12) -and ($script:sent['arch-cw-panel'].type_id -ne 53))
Check 'replay: a refused panel type is a fail that names the kind it sent' (($rb['curtain: change one panel type and re-read it'].Outcome -eq 'fail') -and ($rb['curtain: change one panel type and re-read it'].Detail -match '^basic wall type 12: .*not valid for panel'))
Check 'replay: every catalogued case is still reported exactly once' ($replay.Count -eq $module.Catalog.Count)

if ($fails) { "architecture-edits tests: $fails FAILED"; exit 1 } else { 'architecture-edits tests: ALL PASS'; exit 0 }
