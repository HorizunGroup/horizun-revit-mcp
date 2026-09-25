#Requires -Version 5.1
# Exercises execute-python-async.probes.ps1 WITHOUT Revit: its Run block against a
# fake Call that plays the dispatcher, in three worlds - a correct queue+deferred-run
# (arguments survive), the ORIGINAL BUG reproduced (the fake queue forgets them, exactly
# as the real 'queued' JObject used to), and a queue that never finishes. Pass under
# pwsh 7:  pwsh -NoProfile -File scripts/live-probes/execute-python-async.tests.ps1
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'execute-python-async.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'execute-python-async' }
if (-not $module) { throw 'module did not register' }

function New-Reply($data, [bool]$isError = $false, $text = '') {
    return @{ isError = $isError; text = $text; data = ([pscustomobject]$data | ConvertTo-Json -Depth 20 | ConvertFrom-Json) }
}

# $behavior: 'ok' (the fake queue remembers the arguments, like the fixed bridge),
# 'bug' (the fake queue FORGETS them, exactly like the 'queued' JObject before the
# 2026-09-25 fix), 'never_finishes' (job stays 'running' forever).
#
# $box carries what the fake "queue" actually stored, so job_status echoes back
# whatever execute_python really received - the same shape as the real dispatcher,
# rather than a value this test asserted independently. .GetNewClosure() is REQUIRED:
# an unbound scriptblock resolves its free variables against the CALLER's scope at
# invocation time, not this function's - measured with a minimal repro ($tag read
# back empty without it). See scratchpad note clausura-no-ve-las-funciones-del-script.md.
function New-FakeCall([string]$behavior) {
    $box = @{ storedProbeValue = $null }
    return {
        param($tool, $arguments)
        switch ($tool) {
            'horizun_execute_python' {
                if ($behavior -eq 'never_finishes') { return New-Reply @{ job_id = 'never' } }
                # 'bug' world: the fake queue drops the arguments, exactly like the
                # 'queued' JObject before it copied request["arguments"] across.
                if ($behavior -ne 'bug') { $box.storedProbeValue = $arguments.arguments.probe_value }
                return New-Reply @{ job_id = 'j1' }
            }
            'horizun_job_status' {
                if ($behavior -eq 'never_finishes') {
                    return New-Reply @{ jobs = @(@{ state = 'running'; result = $null }) }
                }
                $seen = if ($box.storedProbeValue) { $box.storedProbeValue } else { '__MISSING__' }
                return New-Reply @{ jobs = @(@{ state = 'ok'; result = @{ output = @{ probe_value_seen = $seen } } }) }
            }
            default { throw "unexpected tool $tool" }
        }
    }.GetNewClosure()
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

# ---- World 1: the fix works - arguments survive the async round trip. ----
$ctx1 = @{ Document = 'HZ_WRITE'; RunId = 'r1'; ScratchRoot = $null; Call = (New-FakeCall 'ok') }
$cases1 = & $module.Run $ctx1
$c1 = $cases1 | Where-Object { $_.Name -like 'execute-python-async:*' } | Select-Object -First 1
Check 'the fix: arguments reach the deferred script -> pass' ($c1.Outcome -eq 'pass')

# ---- World 2: the ORIGINAL BUG reproduced - the deferred script sees '__MISSING__'. ----
$ctx2 = @{ Document = 'HZ_WRITE'; RunId = 'r2'; ScratchRoot = $null; Call = (New-FakeCall 'bug') }
$cases2 = & $module.Run $ctx2
$c2 = $cases2 | Where-Object { $_.Name -like 'execute-python-async:*' } | Select-Object -First 1
Check 'the regression: a missing argument is reported FAIL, not silently passed' ($c2.Outcome -eq 'fail')
Check 'the regression detail names the bug explicitly' ($c2.Detail -match 'THE REGRESSION')

# ---- World 3: the job never finishes - the probe must time out, not hang or pass. ----
$ctx3 = @{ Document = 'HZ_WRITE'; RunId = 'r3'; ScratchRoot = $null; Call = (New-FakeCall 'never_finishes') }
$before = Get-Date
$cases3 = & $module.Run $ctx3
$elapsed = (Get-Date) - $before
$c3 = $cases3 | Where-Object { $_.Name -like 'execute-python-async:*' } | Select-Object -First 1
Check 'a job that never finishes is reported FAIL, not left hanging' ($c3.Outcome -eq 'fail')
Check 'the timeout is bounded (well under a minute in this fake, no real Sleep skipped)' ($elapsed.TotalSeconds -lt 45)

if ($fails) { "execute-python-async probe tests: $fails FAILED"; exit 1 } else { 'execute-python-async probe tests: ALL PASS'; exit 0 }
