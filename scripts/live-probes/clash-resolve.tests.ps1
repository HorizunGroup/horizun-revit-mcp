#Requires -Version 5.1
# Exercises clash-resolve.probes.ps1 WITHOUT Revit: a fake Call/Apply plays a
# model with two crossing pipes, a ledger, a resolve and an undo.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'clash-resolve.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'clash-resolve' }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Moved = $false; Status = 'open'; Regression = $false; Calls = @() }
    $reply = { param($data, $isError = $false) [pscustomobject]@{ isError = $isError; data = $data; text = 'fake' } }
    $call = {
        param($tool, $a)
        $s.Calls += $tool
        switch ($tool) {
            'horizun_list_elements' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 1 }) }) }
            'horizun_query_model' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true }) }) }
            'horizun_clash' {
                $reg = 0
                if (-not $s.Moved) { if ($s.Status -eq 'resolved_by_model') { $s.Status = 'open'; $s.Regression = $true; $reg = 1 } }
                return & $reply ([pscustomobject]@{ findings = [pscustomobject]@{ regressions = $reg } })
            }
            'horizun_coordination' {
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ finding_id = 'f1'; status = $s.Status; regression = $s.Regression }) })
            }
            'horizun_resolve_clash' {
                $row = [pscustomobject]@{ finding_id = 'f1'; status = 'proposed'; mover_id = 100; fixed_id = 101; kind = 'elevation'; distance_mm = 150; prediction = 'pair clears' }
                if ($s.Mode -eq 'no-proposal') { $row.status = 'report_only' }
                return & $reply ([pscustomobject]@{ proposals = @($row); next_arguments = [pscustomobject]@{ proposals = @([pscustomobject]@{ finding_id = 'f1'; element_id = 100; vector_mm = @(0, 0, 150) }) } })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $s.Calls += ($tool + ':apply')
        switch ($tool) {
            'horizun_create_elements' { $id = $s.Next; $s.Next++; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })) } }
            'horizun_resolve_clash' {
                $s.Moved = $true; $s.Status = 'resolved_by_model'
                return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ postconditions = [pscustomobject]@{ all_verified = $true }; findings_resolved_by_model = @('f1'); undo = [pscustomobject]@{ recorded = $true } })) }
            }
            'horizun_undo' { $s.Moved = $false; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ postconditions = [pscustomobject]@{ all_verified = $true } })) } }
            'horizun_delete_verified' { return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        }
        return @{ stage = 'dry_run'; answer = (& $reply $null $true) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $true; Call = $call; Apply = $apply } }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$f = New-Fake 'ok'
$cases = @(& $module.Run $f.Ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
Check 'every catalogued case is reported' ($cases.Count -eq $module.Catalog.Count)
Check 'all five pass on a model that behaves' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the pipes are deleted at the end' ($f.State.Calls -contains 'horizun_delete_verified:apply')
Check 'names match the catalog exactly' (@($cases | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)

$g = New-Fake 'no-proposal'
$cases2 = @(& $module.Run $g.Ctx)
Check 'a missing proposal fails the propose case and still cleans up' ((@($cases2 | Where-Object { $_.Outcome -eq 'fail' }).Count -ge 1) -and ($g.State.Calls -contains 'horizun_delete_verified:apply'))
Check 'no apply is sent without a proposal' (-not ($g.State.Calls -contains 'horizun_resolve_clash:apply'))

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $false
$cases3 = @(& $module.Run $h.Ctx)
Check 'without the write gate every case is not_covered' (@($cases3 | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 5)

if ($fails) { "clash-resolve tests: $fails FAILED"; exit 1 } else { 'clash-resolve tests: ALL PASS'; exit 0 }
