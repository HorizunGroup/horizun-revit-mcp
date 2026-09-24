#Requires -Version 5.1
# Exercises parameters.probes.ps1 WITHOUT Revit: a fake Call/Apply that keeps a tiny
# in-memory model (bindings, globals), so every case and its cleanup path run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'parameters.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'parameters' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-Fake([bool]$Broken) {
    $state = @{ bindings = New-Object System.Collections.ArrayList; globals = New-Object System.Collections.ArrayList; broken = $Broken; calls = 0 }
    $reply = { param($data, $isError, $text) [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        $state.calls++
        switch ($a.operation) {
            'list_bindings' { return & $reply ([pscustomobject]@{ count = $state.bindings.Count; bindings = @($state.bindings) }) $false '' }
            'global_list'   { return & $reply ([pscustomobject]@{ count = $state.globals.Count; globals = @($state.globals) }) $false '' }
            'create_project' { return & $reply $null $true 'create_project is not possible: no Revit 2023-2027 API creates a NON-shared project parameter' }
            'keynote_table' { return & $reply ([pscustomobject]@{ source = [pscustomobject]@{ present = $true; path = 'C:\k.txt' }; table_entries = 3; entries = @(); read_only = $true }) $false '' }
            'family_lookup_tables' { return & $reply ([pscustomobject]@{ families_scanned = 4; families = @() }) $false '' }
        }
        throw "unexpected call $($a.operation)"
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = [pscustomobject]@{ state = 'committed_verified'; host_verified = $true }
        switch ($a.operation) {
            'create_shared' {
                if ($state.broken) { return @{ stage = 'dry_run'; answer = (& $reply $null $true 'rehearsal failed') } }
                Set-Content -LiteralPath $a.spf_path -Value ("PARAM`t" + [guid]::NewGuid() + "`t" + $a.name)
                [void]$state.bindings.Add([pscustomobject]@{ name = $a.name; shared = $true; guid = 'g-1' })
            }
            'remove_binding' { $x = @($state.bindings | Where-Object { $_.guid -eq $a.guid }); foreach ($b in $x) { $state.bindings.Remove($b) } }
            'global_create' { [void]$state.globals.Add([pscustomobject]@{ name = $a.name; value = [pscustomobject]@{ display = [double]$a.value } }) }
            'global_set' { ($state.globals | Where-Object { $_.name -eq $a.name }).value.display = [double]$a.value }
            'global_delete' { $x = @($state.globals | Where-Object { $_.name -eq $a.name }); foreach ($g in $x) { $state.globals.Remove($g) } }
        }
        return @{ stage = 'apply'; answer = (& $reply $ok $false '') }
    }.GetNewClosure()
    return @{ state = $state; call = $call; apply = $apply }
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-param-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $f = New-Fake $false
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-1'; WriteGate = $false; Call = $f.call; Apply = $f.apply }
    $cases = @(& $module.Run $ctx)
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0)
    Check 'every case passes against a model that behaves' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
    Check 'the probe binding and global are gone afterwards' ($f.state.bindings.Count -eq 0 -and $f.state.globals.Count -eq 0)
    Check 'the temporary SPF is removed' (@(Get-ChildItem -LiteralPath $scratch).Count -eq 0)
    if ($fails) { $cases | ForEach-Object { "    $($_.Name): $($_.Outcome) $($_.Detail)" } }

    $b = New-Fake $true
    $ctx2 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-2'; WriteGate = $false; Call = $b.call; Apply = $b.apply }
    $by2 = @{}; foreach ($c in @(& $module.Run $ctx2)) { $by2[$c.Name] = $c }
    Check 'a create_shared that never binds fails, and so does the removal it could not do' (
        $by2['parameters: create_shared writes the SPF, binds, and both re-read'].Outcome -eq 'fail' -and
        $by2['parameters: remove_binding withdraws the probe binding and list_bindings agrees'].Outcome -eq 'fail')

    $ctx3 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-3'; WriteGate = $true; Call = (New-Fake $false).call; Apply = { throw 'must not write' } }
    $by3 = @{}; foreach ($c in @(& $module.Run $ctx3)) { $by3[$c.Name] = $c }
    Check 'a closed write tier writes nothing and says not_covered' (
        $by3['parameters: a Global Parameter is created, read back, set and deleted'].Outcome -eq 'not_covered' -and
        $by3['classification: keynote_table reports its source and entries'].Outcome -eq 'pass')
}
finally {
    Get-ChildItem -LiteralPath $scratch -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Remove-Item -LiteralPath $scratch
}
if ($fails) { "parameters probe tests: $fails FAILED"; exit 1 } else { 'parameters probe tests: ALL PASS'; exit 0 }
