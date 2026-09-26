#Requires -Version 5.1
# Exercises field-audit-typechange.probes.ps1 WITHOUT Revit: fakes answer the calls the
# module makes, so a harness error (a typo, a wrong field) shows here, not in a matrix run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'field-audit-typechange.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'field-audit-typechange' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        if ($tool -eq 'horizun_list_elements') {
            if ($mode -eq 'no-walls') { return & $reply ([pscustomobject]@{ rows = @() }) }
            return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 11; source_kind = 'host'; type_id = 22 }) })
        }
        if ($tool -eq 'horizun_transform_elements') {
            $op = $a.operations[0]
            if ($a.operations.Count -gt 1) { return & $reply $null $true 'realign_wall_sketch cannot be mixed with other operations' }
            if ($op.operation -eq 'realign_wall_sketch') { return & $reply $null $true 'wall 11 has no edited profile' }
            $rule = $op.rule[0]
            if ($rule.type_id -eq 0) { return & $reply $null $true 'type 0 is not a valid type for it' }
            if ($rule.when) { return & $reply $null $true "ElementId 11: no rule matched and no 'else' was declared (measured: short_side_mm=3000, long_side_mm=6000, area_m2=18)." }
            return & $reply ([pscustomobject]@{ confirmation_token = 't'; plan = @([pscustomobject]@{ instances = @([pscustomobject]@{ element_id = 11; measured = @{ short_side_mm = 200 } }) }) })
        }
        if ($tool -eq 'horizun_audit_model') {
            $f = @([pscustomobject]@{ check = 'wall_sketch_drift'; count = 0; summary = 'none' },
                   [pscustomobject]@{ check = 'warnings'; items = @([pscustomobject]@{ root_cause = [pscustomobject]@{ cause = 'unclassified' } }) })
            return & $reply ([pscustomobject]@{ findings = $f })
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = { param($tool, $a, $key) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = [pscustomobject]@{ operations_verified = 1 }; text = 'ok' } } }
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply }
}

$r = @(& $module.Run (New-Fake 'ok'))
Check ($r.Count -eq 8) 'eight cases, one per catalog entry'
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('all pass against a cooperative fake: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))

$r = @(& $module.Run (New-Fake 'no-walls'))
Check (@($r[0..2] | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 3) 'no host wall: the type-change cases are not_covered, never a pass'

$ctx = New-Fake 'ok'; $ctx.WriteGate = $true
$r = @(& $module.Run $ctx)
Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 8) 'with the write tier closed every case is not_covered'

if ($fail -gt 0) { Write-Host "field-audit-typechange probe tests: $fail FAILED"; exit 1 }
Write-Host 'field-audit-typechange probe tests: ALL PASS'
