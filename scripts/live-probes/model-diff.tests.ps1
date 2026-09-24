#Requires -Version 5.1
# Exercises model-diff.probes.ps1 WITHOUT Revit: fake Call/Apply stand in for the bridge.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'model-diff.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'model-diff' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-Fake([bool]$extraRow, [bool]$noLevel) {
    $state = @{ next = 100; deleted = @(); files = @(); extra = $extraRow; noLevel = $noLevel }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('hz-md-probe-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $state.tmp = $tmp
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_list_elements' {
                if ($a.category -eq 'OST_Levels') {
                    if ($state.noLevel) { return @{ isError = $false; data = [pscustomobject]@{ rows = @() } } }
                    return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7 }) } }
                }
                return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 11 }) } }
            }
            'horizun_query_model' { return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 9; is_element_type = $true }) } } }
            'horizun_model_diff' {
                switch ($a.operation) {
                    'snapshot' {
                        $p = Join-Path $state.tmp 'S1.json.gz'; Set-Content -LiteralPath $p -Value 'x'
                        Set-Content -LiteralPath (Join-Path $state.tmp 'S1.meta.json') -Value 'x'
                        return @{ isError = $false; data = [pscustomobject]@{ snapshot_id = 'S1'; path = $p; meta = [pscustomobject]@{ elements = 1 } } }
                    }
                    'list' { return @{ isError = $false; data = [pscustomobject]@{ snapshots = @([pscustomobject]@{ snapshot_id = 'S1' }) } } }
                    'compare' {
                        $csv = Join-Path $state.tmp 'c.csv'; Set-Content -LiteralPath $csv -Value 'x'
                        $rows = @([pscustomobject]@{ state = 'added'; element_id = 101 },
                                  [pscustomobject]@{ state = 'modified'; element_id = 100; moved_mm = 1000.0 })
                        if ($state.extra) { $rows += [pscustomobject]@{ state = 'modified'; element_id = 5; moved_mm = $null } }
                        return @{ isError = $false; data = [pscustomobject]@{ detail = [pscustomobject]@{ rows = $rows }
                                  exports = [pscustomobject]@{ csv = $csv; json = (Join-Path $state.tmp 'c.json') } } }
                    }
                    'colorize' { return @{ isError = $false; data = [pscustomobject]@{ confirmation_token = 't'; to_color = [pscustomobject]@{ added = 1; modified = 1 } } } }
                    'explain' { return @{ isError = $false; data = [pscustomobject]@{ model_elements = 12
                                  narrative = [pscustomobject]@{ en = "'m' holds 12 model elements." }; iso19650 = [pscustomobject]@{ status = 'not_requested' } } } }
                    'record_quality' { return @{ isError = $false; data = [pscustomobject]@{ appended_verified = $true; path = (Join-Path $state.tmp 'q.jsonl') } } }
                    'quality_trend' { return @{ isError = $false; data = [pscustomobject]@{ records = 1; rows = @([pscustomobject]@{ a = 1 }); columns = @('a'); csv = $null } } }
                }
            }
        }
        return @{ isError = $true; text = 'unexpected ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        switch ($tool) {
            'horizun_create_elements' { $id = $state.next; $state.next++
                return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) } } } }
            'horizun_transform_elements' { return @{ stage = 'apply'; answer = @{ isError = $false } } }
            'horizun_delete_verified' { $state.deleted = @($a.ids); return @{ stage = 'apply'; answer = @{ isError = $false } } }
            'horizun_model_diff' { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ view_id = 555; view_name = 'Horizun diff S1'
                view_verified = $true; overrides_applied = 2; overrides_verified = 2; not_in_view = 0 } } } }
        }
    }.GetNewClosure()
    return @{ state = $state; ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $tmp; RunId = 'r1'; WriteGate = $true; Call = $call; Apply = $apply } }
}

# ---- the honest run: everything passes and everything is cleaned --------------------------
$f = New-Fake $false $false
$cases = @(& $module.Run $f.ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
Check 'every catalogued case is reported' ((@($module.Catalog | Where-Object { $by.ContainsKey($_.Name) })).Count -eq 5)
Check 'all five pass on the honest fake' (@($cases | Where-Object { $_.Outcome -eq 'pass' }).Count -eq 5)
Check 'cleanup deletes both walls and the colorized view' ((@($f.state.deleted) -join ',') -eq '100,101,555')
Check 'the snapshot and export files are removed' (-not (Test-Path (Join-Path $f.state.tmp 'S1.json.gz')) -and -not (Test-Path (Join-Path $f.state.tmp 'S1.meta.json')) -and -not (Test-Path (Join-Path $f.state.tmp 'c.csv')))
Remove-Item -LiteralPath $f.state.tmp -Force

# ---- an extra modified row is a failure, not a pass -----------------------------------------
$f = New-Fake $true $false
$cases = @(& $module.Run $f.ctx)
$cmp = $cases | Where-Object { $_.Name -like '*compare shows exactly*' }
Check 'an unexpected extra change fails the compare case' ($cmp.Outcome -eq 'fail')
Get-ChildItem -LiteralPath $f.state.tmp -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
Remove-Item -LiteralPath $f.state.tmp -Force

# ---- a fixture without a level is not_covered, case by case ---------------------------------
$f = New-Fake $false $true
$cases = @(& $module.Run $f.ctx)
Check 'no level in the fixture reports every case not_covered' ((@($cases | Where-Object { $_.Outcome -eq 'not_covered' })).Count -eq 5)
Check 'nothing is created when the fixture cannot stage' ($f.state.next -eq 100)
Remove-Item -LiteralPath $f.state.tmp -Force

if ($fails) { "model-diff probe tests: $fails FAILED"; exit 1 } else { 'model-diff probe tests: ALL PASS'; exit 0 }
