# execute_python run_async - live probe module.
#
# MEASURED 2026-09-25 (field session): a script run through run_async=true read its
# own 'arguments' back as '{}' and KeyError'd - the queued copy of the request never
# carried them, only the synchronous path did. Fixed in ExecutePythonCommand.cs (the
# 'queued' JObject now copies request["arguments"]); ExecutePythonGateTests.cs pins
# the fix at the source level. This module proves the FULL round trip against a real
# Revit: queue a trivial script with run_async=true and an 'arguments' payload, poll
# horizun_job_status until it finishes, and check the script actually SAW the value -
# the one thing that can only be observed live, because the dispatcher replay only
# happens on Revit's own UI thread.
#
# Nothing is written to the model; the script only echoes its own arguments into
# __output__, so there is nothing to clean up.

$script:HzProbeModules += [pscustomobject]@{
    Name    = 'execute-python-async'
    Catalog = @(
        @{ Name = 'execute-python-async: run_async carries arguments into the deferred script'; Tool = 'horizun_execute_python' }
    )
    Run     = {
        param($Ctx)
        $cases = @()
        $doc = $Ctx.Document
        function Case($name, $tool, $ok, $detail) {
            @{ Name = $name; Tool = $tool; Outcome = $(if ($ok) { 'pass' } else { 'fail' }); Detail = $detail }
        }

        $tag = 'HZ_ASYNC_ARGS_' + $Ctx.RunId
        $code = @'
import json
args = json.loads(HORIZUN_ARGS_JSON)
seen = args.get("probe_value", "__MISSING__")
__output__ = {
    "status": "self_reported_verified",
    "summary": "echoed the caller's own arguments back",
    "verification": {"checked": True, "evidence": [seen]},
    "probe_value_seen": seen
}
'@

        $key = 'execute-python-async-' + $Ctx.RunId
        $queued = & $Ctx.Call 'horizun_execute_python' @{
            target_document  = $doc
            code             = $code
            arguments        = @{ probe_value = $tag }
            run_async        = $true
            idempotency_key  = $key
        }

        $jobId = $null
        if ($queued.data) { $jobId = $queued.data.job_id }
        if ($queued.isError -or -not $jobId) {
            $cases += Case 'execute-python-async: run_async carries arguments into the deferred script' 'horizun_execute_python' $false `
                ("could not queue: isError={0} text={1}" -f $queued.isError, [string]$queued.text)
            return $cases
        }

        # Bounded poll: a trivial script should finish within a few idle ticks of
        # Revit's UI thread. 30 x 1s is generous without risking a hung probe run.
        $state = 'queued'
        $result = $null
        for ($i = 0; $i -lt 30 -and $state -in @('queued', 'running'); $i++) {
            Start-Sleep -Seconds 1
            $status = & $Ctx.Call 'horizun_job_status' @{ job_id = $jobId }
            if ($status.data -and $status.data.jobs -and $status.data.jobs.Count -gt 0) {
                $job = $status.data.jobs[0]
                $state = [string]$job.state
                $result = $job.result
            }
        }

        if ($state -ne 'ok') {
            $cases += Case 'execute-python-async: run_async carries arguments into the deferred script' 'horizun_execute_python' $false `
                ("job_id=$jobId ended in state '$state' instead of 'ok' (timed out or failed)")
            return $cases
        }

        $seen = $null
        if ($result -and $result.output) { $seen = $result.output.probe_value_seen }
        $ok = ($seen -eq $tag)
        $detail = "job_id=$jobId probe_value_seen='$seen' expected='$tag'"
        if (-not $ok -and $seen -eq '__MISSING__') {
            $detail += " - THE REGRESSION: the deferred script read HORIZUN_ARGS_JSON as empty."
        }
        $cases += Case 'execute-python-async: run_async carries arguments into the deferred script' 'horizun_execute_python' $ok $detail
        return $cases
    }
}
