#Requires -Version 5.1
# Exercises navisworks-handoff.probes.ps1 WITHOUT Revit and WITHOUT Navisworks: a fake
# Call/Apply plays a model with a level/pipe type, a synthetic import that reproduces
# and records one finding, a show that creates a view, and a resolve that clears it.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'navisworks-handoff.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'navisworks-handoff' }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Recorded = $false; FindingId = 'f1'; ViewId = 555; Resolved = $false; Calls = @() }
    $reply = { param($data, $isError = $false) [pscustomobject]@{ isError = $isError; data = $data; text = 'fake' } }
    $call = {
        param($tool, $a)
        $s.Calls += $tool
        switch ($tool) {
            'horizun_list_elements' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 1 }) }) }
            'horizun_query_model' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true }) }) }
            'horizun_coordination' {
                if ($a.operation -eq 'import_navisworks') {
                    if ($s.Mode -eq 'not-reproduced') { return & $reply ([pscustomobject]@{ reproduced = 0; not_reproduced = 1; would_record = 0 }) }
                    return & $reply ([pscustomobject]@{ reproduced = 1; not_reproduced = 0; would_record = 1 })
                }
                if ($a.operation -eq 'list') {
                    if (-not $s.Recorded) { return & $reply ([pscustomobject]@{ rows = @() }) }
                    $row = [pscustomobject]@{ finding_id = $s.FindingId; external_issue_id = 'HZ-NAVIS-t'; external_source = 'navisworks'; priority = 'high'; responsible = 'Mechanical' }
                    return & $reply ([pscustomobject]@{ rows = @($row) })
                }
                return & $reply $null $true
            }
            'horizun_resolve_clash' {
                $row = [pscustomobject]@{ finding_id = $s.FindingId; status = 'proposed'; mover_id = 100; fixed_id = 101; kind = 'elevation'; distance_mm = 150; prediction = 'pair clears' }
                return & $reply ([pscustomobject]@{ proposals = @($row); next_arguments = [pscustomobject]@{ proposals = @([pscustomobject]@{ finding_id = $s.FindingId; element_id = 100; vector_mm = @(0, 0, 150) }) } })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $s.Calls += ($tool + ':apply:' + ($a.operation))
        switch ($tool) {
            'horizun_create_elements' { $id = $s.Next; $s.Next++; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })) } }
            'horizun_coordination' {
                if ($a.operation -eq 'import_navisworks') {
                    $s.Recorded = $true
                    return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ recorded = 1; verified_by_reread = $true })) }
                }
                if ($a.operation -eq 'show') {
                    return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ postconditions = [pscustomobject]@{ all_verified = $true }; view_id = $s.ViewId })) }
                }
                return @{ stage = 'apply'; answer = (& $reply $null $true) }
            }
            'horizun_resolve_clash' {
                $s.Resolved = $true
                return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ postconditions = [pscustomobject]@{ all_verified = $true }; findings_resolved_by_model = @($s.FindingId) })) }
            }
            'horizun_delete_verified' { return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        }
        return @{ stage = 'dry_run'; answer = (& $reply $null $true) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$f = New-Fake 'ok'
$cases = @(& $module.Run $f.Ctx)
Check 'every catalogued case is reported' ($cases.Count -eq $module.Catalog.Count)
Check 'all five pass on a model that behaves' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'names match the catalog exactly' (@($cases | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)
Check 'the finding was recorded before show or resolve ran' ($f.State.Calls.IndexOf('horizun_coordination:apply:import_navisworks') -lt $f.State.Calls.IndexOf('horizun_coordination:apply:show'))
Check 'resolve only ran after a recorded, matched proposal' ($f.State.Resolved -eq $true)
Check 'the pipes and the view are deleted at the end' ($f.State.Calls -contains 'horizun_delete_verified:apply:')

$g = New-Fake 'not-reproduced'
$cases2 = @(& $module.Run $g.Ctx)
Check 'a dry run that reproduces nothing fails case 0 and stops before recording' (
    $cases2[0].Outcome -eq 'fail' -and -not ($g.State.Calls -contains 'horizun_coordination:apply:import_navisworks'))
Check 'the staged pipes are still deleted after a failed dry run' ($g.State.Calls -contains 'horizun_delete_verified:apply:')

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true
$cases3 = @(& $module.Run $h.Ctx)
Check 'without the write gate every case is not_covered' (@($cases3 | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 5)

if ($fails) { "navisworks-handoff tests: $fails FAILED"; exit 1 } else { 'navisworks-handoff tests: ALL PASS'; exit 0 }
