#Requires -Version 5.1
# Exercises phases-options-parts.probes.ps1 WITHOUT Revit, against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'phases-options-parts.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'phases-options-parts' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-Fake([bool]$withOptions, [bool]$breakDissolve) {
    $state = @{ calls = @(); applies = @(); nextWall = 900; created = 1; demolished = -1; parts = @(); deleted = @() }
    $call = {
        param($tool, $a)
        $state.calls += "$tool/$($a.operation)"
        $ok = { param($d) @{ isError = $false; data = [pscustomobject]$d; text = '' } }
        switch ($tool) {
            'horizun_manage_phases' {
                switch ($a.operation) {
                    'list' {
                        $pres = [pscustomobject]@{ new = 'overridden'; existing = 'by_category'; demolished = 'hidden'; temporary = 'hidden' }
                        $opts = if ($withOptions) { @([pscustomobject]@{ id = 50; option_set_id = 40; is_primary = $true }, [pscustomobject]@{ id = 51; option_set_id = 40; is_primary = $false }) } else { @() }
                        return & $ok @{ read_only = $true; phases = @([pscustomobject]@{ index = 0; id = 1 }, [pscustomobject]@{ index = 1; id = 2 })
                                        phase_filters = @([pscustomobject]@{ id = 7; presentation = $pres }); design_options = $opts; design_options_writable = $false }
                    }
                    'create_phase' { return @{ isError = $true; data = $null; text = 'no_phase_creation_api: Revit cannot. {"state":"refused","code":"no_phase_creation_api"}' } }
                    'element_status' {
                        $status = if ($state.demolished -eq 2 -and $a.phase_id -eq 2) { 'demolished' } else { 'new' }
                        return & $ok @{ rows = @([pscustomobject]@{ has_phases = $true; created_phase_id = $state.created; demolished_phase_id = $state.demolished; status = $status }) }
                    }
                }
            }
            'horizun_manage_assemblies_parts' {
                if ($a.operation -eq 'list') { return & $ok @{ elements = @([pscustomobject]@{ part_ids = $state.parts }) } }
            }
            'horizun_list_elements' { return & $ok @{ rows = @([pscustomobject]@{ element_id = 30 }) } }
            'horizun_query_model' { return & $ok @{ rows = @([pscustomobject]@{ is_element_type = $true; family = 'Basic Wall'; element_id = 31 }) } }
        }
        return @{ isError = $true; data = $null; text = "fake: no answer for $tool" }
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $state.applies += "$tool/$($a.operation)"
        $verified = { param($result) @{ stage = 'apply'; answer = @{ isError = $false; text = ''
            data = [pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }; result = [pscustomobject]$result } } } }
        switch ($tool) {
            'horizun_create_elements' { $state.nextWall++; return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $state.nextWall }) } } } }
            'horizun_delete_verified' { $state.deleted = @($a.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
            'horizun_manage_phases' { $state.created = $a.created_phase_id; $state.demolished = $a.demolished_phase_id; return & $verified @{} }
            'horizun_manage_assemblies_parts' {
                switch ($a.operation) {
                    'create_parts' { $state.parts = @(1001, 1002); return & $verified @{} }
                    'dissolve_parts' {
                        if ($breakDissolve) { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'rolled back'; data = [pscustomobject]@{ state = 'rolled_back' } } } }
                        $state.parts = @(); return & $verified @{}
                    }
                    'create_assembly' { return & $verified @{ id = 77; member_ids = @($a.element_ids) } }
                    'disassemble' { return & $verified @{ assembly_id = $a.assembly_id } }
                }
            }
        }
        return @{ stage = 'dry_run'; answer = @{ isError = $true; text = 'fake: unknown' } }
    }.GetNewClosure()
    return @{ state = $state; ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $false; Call = $call; Apply = $apply } }
}

function Outcomes($cases) { $o = @{}; foreach ($c in $cases) { $o[$c.Name] = $c.Outcome }; $o }

# A healthy model with design options: every case passes, and the walls are cleaned up.
$f = New-Fake $true $false
$r = Outcomes (& $module.Run $f.ctx)
Check 'every catalogued case is reported' ((@($module.Catalog | Where-Object { -not $r.ContainsKey($_.Name) })).Count -eq 0)
Check 'all six cases pass on a healthy fake' ((@($r.Values | Where-Object { $_ -ne 'pass' })).Count -eq 0)
Check 'the phases of the probe wall are restored' ($f.state.created -eq 1 -and $f.state.demolished -eq -1)
Check 'the three probe walls are deleted' (@($f.state.deleted).Count -eq 3)

# No design options: not_covered with a reason, never a fail or a pass.
$f = New-Fake $false $false
$r = Outcomes (& $module.Run $f.ctx)
Check 'a document without design options is not_covered' ($r['design options: list reports option sets, options and the primary'] -eq 'not_covered')

# A dissolve that rolls back fails the parts case and still cleans up.
$f = New-Fake $true $true
$r = Outcomes (& $module.Run $f.ctx)
Check 'a dissolve that did not verify fails the parts case' ($r['parts: create parts from a probe wall, re-read them, then dissolve them'] -eq 'fail')
Check 'cleanup still runs after a failed case' (@($f.state.deleted).Count -eq 3)

# Closed write gate: reads still run, writes are not_covered and nothing is applied.
$f = New-Fake $true $false
$f.ctx.WriteGate = $true
$r = Outcomes (& $module.Run $f.ctx)
Check 'a closed gate applies nothing' (@($f.state.applies).Count -eq 0)
Check 'a closed gate reports the write cases not_covered' ($r['assemblies: assembly of two probe walls re-reads its members, then disassembles'] -eq 'not_covered')
Check 'a closed gate still runs the reads' ($r['phases: list reads phases in order and every phase filter with four presentations'] -eq 'pass')

if ($fails) { "phases-options-parts tests: $fails FAILED"; exit 1 } else { 'phases-options-parts tests: ALL PASS'; exit 0 }
