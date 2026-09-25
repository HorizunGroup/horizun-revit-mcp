#Requires -Version 5.1
# Exercises delete-parameters.probes.ps1 WITHOUT Revit: a fake Call/Apply that keeps
# a tiny in-memory model of bindings and globals, and a fake horizun_delete_verified
# that mimics DeleteCommand.cs's parameter_kind / parameter_binding_confirmed_removed
# fields well enough to prove the probe reads them correctly.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'delete-parameters.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'delete-parameters' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-Fake([bool]$DeleteFails) {
    $nextId = 1000
    $state = @{ bindings = New-Object System.Collections.ArrayList; globals = New-Object System.Collections.ArrayList; deleteFails = $DeleteFails }
    $reply = { param($data, $isError, $text) [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($a.operation) {
            'list_bindings' { return & $reply ([pscustomobject]@{ count = $state.bindings.Count; bindings = @($state.bindings) }) $false '' }
            'global_list' { return & $reply ([pscustomobject]@{ count = $state.globals.Count; globals = @($state.globals) }) $false '' }
        }
        throw "unexpected call $($a.operation)"
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = [pscustomobject]@{ state = 'committed_verified'; host_verified = $true }
        if ($tool -eq 'horizun_manage_parameters') {
            switch ($a.operation) {
                'create_shared' {
                    $id = $script:nextId++
                    Set-Content -LiteralPath $a.spf_path -Value ("PARAM`t" + [guid]::NewGuid() + "`t" + $a.name)
                    [void]$state.bindings.Add([pscustomobject]@{ name = $a.name; shared = $true; guid = 'g-' + $id; parameter_element_id = $id })
                }
                'remove_binding' { $x = @($state.bindings | Where-Object { $_.guid -eq $a.guid }); foreach ($b in $x) { $state.bindings.Remove($b) } }
                'global_create' { $id = $script:nextId++; [void]$state.globals.Add([pscustomobject]@{ name = $a.name; id = $id; value = [pscustomobject]@{ display = [double]$a.value } }) }
                'global_delete' { $x = @($state.globals | Where-Object { $_.name -eq $a.name }); foreach ($g in $x) { $state.globals.Remove($g) } }
            }
            return @{ stage = 'apply'; answer = (& $reply $ok $false '') }
        }
        if ($tool -eq 'horizun_delete_verified') {
            $delId = [int64]$a.ids[0]
            $bindingRow = @($state.bindings | Where-Object { $_.parameter_element_id -eq $delId }) | Select-Object -First 1
            $globalRow = @($state.globals | Where-Object { $_.id -eq $delId }) | Select-Object -First 1
            if ($state.deleteFails) {
                return @{ stage = 'apply'; answer = (& $reply $null $true 'delete refused for the fake test') }
            }
            $kind = $null; $removedOk = $true
            if ($null -ne $bindingRow) { $kind = 'shared_parameter'; [void]$state.bindings.Remove($bindingRow) }
            elseif ($null -ne $globalRow) { $kind = 'global_parameter'; [void]$state.globals.Remove($globalRow) }
            $item = [pscustomobject]@{ id = $delId; verdict = 'deleted'; parameter_kind = $kind; parameter_binding_confirmed_removed = $removedOk }
            $data = [pscustomobject]@{ results = [pscustomobject]@{ items = @($item) } }
            return @{ stage = 'apply'; answer = (& $reply $data $false '') }
        }
        throw "unexpected tool $tool"
    }.GetNewClosure()
    return @{ state = $state; call = $call; apply = $apply }
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-delparam-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $f = New-Fake $false
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-1'; WriteGate = $false; Call = $f.call; Apply = $f.apply }
    $cases = @(& $module.Run $ctx)
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0)
    Check 'both deletions pass against a model that behaves' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
    Check 'the probe binding and global are gone afterwards (the delete itself cleaned up)' ($f.state.bindings.Count -eq 0 -and $f.state.globals.Count -eq 0)
    Check 'the temporary SPF is removed' (@(Get-ChildItem -LiteralPath $scratch -ErrorAction SilentlyContinue).Count -eq 0)
    if ($fails) { $cases | ForEach-Object { "    $($_.Name): $($_.Outcome) $($_.Detail)" } }

    $b = New-Fake $true
    $ctx2 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-2'; WriteGate = $false; Call = $b.call; Apply = $b.apply }
    $by2 = @{}; foreach ($c in @(& $module.Run $ctx2)) { $by2[$c.Name] = $c }
    Check 'a delete that is refused fails both cases, not a silent pass' (@($by2.Values | Where-Object { $_.Outcome -ne 'fail' }).Count -eq 0)

    $ctx3 = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-3'; WriteGate = $true; Call = (New-Fake $false).call; Apply = { throw 'must not write' } }
    $by3 = @{}; foreach ($c in @(& $module.Run $ctx3)) { $by3[$c.Name] = $c }
    Check 'a closed write tier writes nothing and reports not_covered' (@($by3.Values | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0)
}
finally {
    Get-ChildItem -LiteralPath $scratch -File -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Remove-Item -LiteralPath $scratch -ErrorAction SilentlyContinue
}
if ($fails) { "delete-parameters probe tests: $fails FAILED"; exit 1 } else { 'delete-parameters probe tests: ALL PASS'; exit 0 }
