#Requires -Version 5.1
# Exercises clearance-zones.probes.ps1 WITHOUT Revit: a fake Call/Apply plays a model
# where a column stands in a panelboard's declared front zone, is moved away, and
# everything is cleaned up.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'clearance-zones.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'clearance-zones' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Deleted = @(); Panel = $null; Column = $null; Moved = $false }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_query_model' {
                $cat = $a.categories[0]
                if ($cat -eq 'OST_Walls') { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true; family = 'Basic Wall'; type = 'Generic' }) }) }
                if ($cat -eq 'OST_ElectricalEquipment' -and $s.Mode -ne 'no-type') {
                    return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 8; is_element_type = $true; family = 'M_Lighting and Appliance Panelboard - 208V MLO'; type = '100 A' }) })
                }
                if ($cat -eq 'OST_Columns' -and $s.Mode -ne 'no-type') {
                    return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 9; is_element_type = $true; family = 'M_Rectangular Column'; type = '610 x 610mm' }) })
                }
                return & $reply ([pscustomobject]@{ rows = @() })
            }
            'horizun_verify_changes' {
                $bad = @($a.clearance_rules | Where-Object { -not ([string]$_.category).StartsWith('OST_') })
                if ($bad.Count -gt 0) { return & $reply $null $true 'clearance_rules[1].category must be a BuiltInCategory token such as OST_ElectricalEquipment.' }
                $f = @()
                if ($s.Mode -ne 'not-zoned' -and (-not $s.Moved -or $s.Mode -eq 'still-there')) {
                    $f = @([pscustomobject]@{ severity = 'error'; reason = 'clearance zone of electrical equipment is invaded by column'; a = [pscustomobject]@{ id = $s.Panel }; b = [pscustomobject]@{ id = $s.Column } })
                }
                if ($s.Mode -eq 'warning-only' -and -not $s.Moved) { $f[0].severity = 'warning' }
                $zoned = if ($s.Mode -eq 'not-zoned') { 0 } else { 1 }
                $partial = ($s.Mode -eq 'not-zoned' -or ($s.Mode -eq 'partial' -and $s.Moved))
                $nm = if ($s.Mode -eq 'not-zoned') { @([pscustomobject]@{ id = $s.Panel; reason = 'its facing points up or down' }) } else { @() }
                return & $reply ([pscustomobject]@{ spatial_check = [pscustomobject]@{
                    status = $(if ($f.Count -gt 0) { 'conflicts' } elseif ($partial) { 'partial' } else { 'clean' }); partial = $partial
                    clearance_rules_source = 'argument'; equipment_clearance = [pscustomobject]@{ zoned = $zoned; not_measured = $nm }
                    errors = @($f | Where-Object { $_.severity -eq 'error' }).Count; findings = $f } })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        if ($tool -eq 'horizun_transform_elements') { $s.Moved = $true; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        if ($tool -eq 'horizun_delete_verified') { $s.Deleted = @($a.ids); return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        $id = $s.Next; $s.Next++
        $el = $a.elements[0]
        if ($el.kind -eq 'family_instance' -and $el.host_id) { $s.Panel = $id }
        elseif ($el.kind -eq 'family_instance') { $s.Column = $id }
        return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($h) { @(& $module.Run $h.Ctx) }
function ByName($r, $pattern) { @($r | Where-Object { $_.Name -like $pattern }) | Select-Object -First 1 }

$h = New-Fake 'ok'; $r = Outcomes $h
Check ($r.Count -eq 4) 'four cases, one per catalog entry'
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('the invaded zone, the clean zone after the move, the refusal and the cleanup all pass: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
Check ($h.State.Deleted.Count -eq 4) ('the column, panel, wall and level are deleted: ' + ($h.State.Deleted -join ','))
Check ($h.State.Deleted -notcontains 8 -and $h.State.Deleted -notcontains 9) 'the document''s own types are never deleted'
Check ($h.State.Deleted[0] -eq $h.State.Column) 'the column is deleted first (reverse creation order)'

$h = New-Fake 'still-there'; $r = Outcomes $h
Check ((ByName $r '*moved away*').Outcome -eq 'fail') 'a clearance finding left after the move fails the clean case'

$h = New-Fake 'warning-only'; $r = Outcomes $h
Check ((ByName $r '*invades*').Outcome -eq 'fail') 'a column reported only as a warning fails the error case'
Check ((ByName $r '*moved away*').Outcome -eq 'not_covered') 'without the invaded case the clean case is not_covered, never a pass'

$h = New-Fake 'partial'; $r = Outcomes $h
Check ((ByName $r '*moved away*').Outcome -eq 'fail') 'a PARTIAL answer after the move is not a clean zone'

$h = New-Fake 'not-zoned'; $r = Outcomes $h
Check ((ByName $r '*invades*').Outcome -eq 'fail') 'a panel that got no zone fails the invaded case'
Check ((ByName $r '*moved away*').Outcome -eq 'not_covered') 'and its "clean" answer is not_covered, not a pass'

$h = New-Fake 'no-type'; $r = Outcomes $h
Check (@($r | Where-Object { $_.Name -like '*invades*' -or $_.Name -like '*moved away*' } | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 2) 'a document without the panel and column types is not_covered, never a pass'
Check ($h.State.Deleted.Count -eq 1) 'the staged level is still deleted'

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = Outcomes $h
Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 3) 'with the write tier closed the three write cases are not_covered'
Check ((ByName $r '*malformed*').Outcome -eq 'pass') 'the read-only refusal case still runs with the write tier closed'

if ($fail -gt 0) { Write-Host "clearance-zones probe tests: $fail FAILED"; exit 1 }
Write-Host 'clearance-zones probe tests: all passed'
