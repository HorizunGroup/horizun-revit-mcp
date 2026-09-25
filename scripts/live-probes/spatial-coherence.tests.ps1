#Requires -Version 5.1
# Exercises spatial-coherence.probes.ps1 WITHOUT Revit: a fake Call/Apply plays a model
# where a column lands in a doorway, then a clear column, then the cleanup.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'spatial-coherence.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'spatial-coherence' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Views = 40; Deleted = @(); Door = $null; Image = (Join-Path $env:TEMP ('hz-fake-' + [guid]::NewGuid().ToString('N') + '.png')) }
    Set-Content -LiteralPath $s.Image -Value 'png' -Encoding ascii
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_query_model' {
                if ($s.Mode -eq 'no-types' -and $a.categories[0] -eq 'OST_Columns') { return & $reply ([pscustomobject]@{ rows = @() }) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true; family = 'Basic Wall'; type = 'Generic' }) })
            }
            'horizun_list_elements' { return & $reply ([pscustomobject]@{ total = $s.Views }) }
            'horizun_copy_between_documents' {
                if ($s.Mode -eq 'no-types') { return & $reply $null $true 'no type named x. Types there: .' }
                return & $reply $null $true 'no type named x. Types there: Basic Wall: Generic | Other: Type.'
            }
            'horizun_verify_changes' {
                if ($s.Mode -eq 'leaks-view') { $s.Views++ }
                $f = [pscustomobject]@{ severity = 'error'; reason = 'door is blocked by structural column'; a = [pscustomobject]@{ id = $s.Door }; b = [pscustomobject]@{ id = 999 } }
                return & $reply ([pscustomobject]@{ scope = [pscustomobject]@{ source = 'last_write' }; spatial_check = [pscustomobject]@{ errors = 1; findings = @($f) }
                    image_path = $s.Image; image = [pscustomobject]@{ captured = $true; temporary_view_rollback = 'RolledBack' } })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        if ($tool -eq 'horizun_copy_between_documents') { return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ state = 'committed_verified' })) } }
        if ($tool -eq 'horizun_delete_verified') { $s.Deleted = @($a.ids); return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        $id = $s.Next; $s.Next++
        $el = $a.elements[0]
        $data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }
        if ($el.kind -eq 'family_instance' -and -not $el.height) { $s.Door = $id }
        if ($el.kind -eq 'family_instance' -and $el.height) {
            if ($key -like '*column-front') {
                $f = [pscustomobject]@{ severity = 'error'; reason = 'structural column stands in the passage in front of a door'; a = [pscustomobject]@{ id = $s.Door }; b = [pscustomobject]@{ id = $id } }
                $data | Add-Member spatial_check ([pscustomobject]@{ status = 'conflicts'; errors = 1; warnings = 0; findings = @($f) })
            }
            elseif ($key -like '*column-in-door') {
                $f = [pscustomobject]@{ severity = 'error'; reason = 'door is blocked by structural column'; a = [pscustomobject]@{ id = $s.Door }; b = [pscustomobject]@{ id = $id } }
                $data | Add-Member spatial_check ([pscustomobject]@{ status = 'conflicts'; errors = 1; warnings = 0; findings = @($f) })
                if ($s.Mode -ne 'no-attention') { $data | Add-Member attention 'Spatial check: 1 error(s)' }
            }
            else { $data | Add-Member spatial_check ([pscustomobject]@{ status = 'clean'; errors = 0; warnings = 0; findings = @() }) }
        }
        return @{ stage = 'apply'; answer = (& $reply $data) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($h) { @(& $module.Run $h.Ctx) }

$h = New-Fake 'ok'; $r = Outcomes $h
Check ($r.Count -eq 6) 'six cases, one per catalog entry'
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('a column in a doorway, the look afterwards and the clear column all pass: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
Check ($h.State.Deleted.Count -eq 7) 'the level, wall, door, the three columns and the copied column type are deleted'

$h = New-Fake 'no-attention'; $r = Outcomes $h
Check ($r[0].Outcome -eq 'fail') 'a finding without the attention headline fails the first case'

$h = New-Fake 'leaks-view'; $r = Outcomes $h
Check ($r[2].Outcome -eq 'fail') 'a view left behind by verify_changes fails the rollback case'

$h = New-Fake 'no-types'; $r = Outcomes $h
Check (@($r[0..4] | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 5) 'a fixture without a structural column type is not_covered, never a pass'
Check ($h.State.Deleted.Count -eq 1) 'the staged level is still deleted'

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = Outcomes $h
Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 6) 'with the write tier closed every case is not_covered'

if ($fail -gt 0) { Write-Host "spatial-coherence probe tests: $fail FAILED"; exit 1 }
Write-Host 'spatial-coherence probe tests: ALL PASS'
