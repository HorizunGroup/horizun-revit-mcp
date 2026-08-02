#Requires -Version 5.1
<#
  THE LOST REPLY, AGAINST A REAL REVIT.

  The unit tests prove the ledger returns the original job_id and queues nothing.
  They prove it about a Dictionary. This proves it about the thing that matters:
  a script that WRITES, sent twice through the real transport into a real Revit,
  running once.

  Why it is a script and not a verify-live probe: every probe there is one call
  and one predicate. This is four calls whose meaning is in how they relate -
  the second must return the FIRST one's job_id, and the jobs directory must
  gain exactly one record across all of them.

  WHAT IT ACTUALLY MEASURES, rather than what it asks the reply to claim:

    * the count of job records on disk before and after - the caller's reply
      could say anything; the number of files cannot be argued with
    * the job_id handed back on the retry, against the first one
    * queued_again on the retry
    * that the script's own side effect happened ONCE, counted inside Revit

  The script writes to a MODULE-LEVEL PYTHON GLOBAL rather than to the model. A
  model write would prove the same thing and leave a changed file; this leaves a
  counter in the IronPython engine, which is process state and dies with Revit.
  The point being proven is "how many times did the body run", and that is
  answerable without touching a building.

  Exit codes:  0 exactly once   1 it ran more than once, or a guard did not fire
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Document,
    [string]$Server,
    [string]$Json,
    [int]$Year = 2026
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$call = Join-Path $here 'hz-call.ps1'

$jobsDir = Join-Path $env:USERPROFILE '.horizun\jobs'
function JobCount { if (Test-Path $jobsDir) { @(Get-ChildItem $jobsDir -Filter *.jsonl -File).Count } else { 0 } }

$key = 'live-idem-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$steps = New-Object System.Collections.Generic.List[object]

function Step($name, $tool, $argsJson, $expectError) {
    $tmp = [IO.Path]::GetTempFileName()
    & $call -Tool $tool -Arguments $argsJson -Quiet -Json $tmp -Server $Server | Out-Null
    $code = $LASTEXITCODE
    $o = Get-Content $tmp -Raw | ConvertFrom-Json
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue

    $ok = if ($expectError) { $o.is_error -eq $true -and $o.raw -match $expectError } else { $o.is_error -ne $true }
    $steps.Add([pscustomobject]@{
        step = $name; exit = $code; is_error = $o.is_error; ok = $ok
        duration_ms = $o.duration_ms; result = $o.result
        raw_head = $(if ($o.raw) { $o.raw.Substring(0, [Math]::Min(240, $o.raw.Length)) } else { $null })
    }) | Out-Null

    if ($ok) { Write-Host ("  OK    {0}" -f $name) -ForegroundColor Green }
    else { Write-Host ("  WRONG {0}" -f $name) -ForegroundColor Red; if ($o.raw) { Write-Host ("        " + $steps[-1].raw_head) -ForegroundColor DarkRed } }
    return $o
}

# A body that takes long enough to still be queued when the retry arrives, and
# that counts its own executions in a place that survives between calls.
$body = @'
import time
try:
    __hz_runs
except NameError:
    __hz_runs = 0
__hz_runs = __hz_runs + 1
checkpoint("run %d starting" % __hz_runs, 0, 1)
time.sleep(20)
checkpoint("run %d done" % __hz_runs, 1, 1)
__output__ = {"runs_seen_by_this_engine": __hz_runs}
'@

$payloadA = @{ code = $body; run_async = $true; target_document = $Document; idempotency_key = $key } | ConvertTo-Json -Depth 6 -Compress
$payloadB = @{ code = $body + "\n# a different payload under the same key\n"; run_async = $true
               target_document = $Document; idempotency_key = $key } | ConvertTo-Json -Depth 6 -Compress

Write-Host ""
Write-Host "Idempotency, live - Revit $Year, document '$Document'" -ForegroundColor Cyan
Write-Host ("  key: {0}" -f $key)
Write-Host ("-" * 72)

$before = JobCount
Write-Host ("  job records before: {0}" -f $before)

# 1. the request that gets through, whose reply is imagined lost
$first = Step 'the first send is accepted and queued' 'horizun_execute_python' $payloadA $null
$firstJob = $first.result.job_id

# 2. the caller times out and re-sends the IDENTICAL request
$retry = Step 'the retry is recognised and queues nothing' 'horizun_execute_python' $payloadA $null
$retryJob = $retry.result.job_id

# 3. the same key with different work must be refused, not silently deduplicated
$null = Step 'the same key with a DIFFERENT payload is refused' 'horizun_execute_python' $payloadB 'already used in this Revit session'

Write-Host ("-" * 72)

$checks = New-Object System.Collections.Generic.List[object]
function Check($name, $ok, $detail) {
    $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = $detail }) | Out-Null
    if ($ok) { Write-Host ("  OK    {0}" -f $name) -ForegroundColor Green }
    else { Write-Host ("  WRONG {0} - {1}" -f $name, $detail) -ForegroundColor Red }
}

Check 'the retry returned the ORIGINAL job_id' ($firstJob -and $retryJob -eq $firstJob) `
      ("first '{0}', retry '{1}'" -f $firstJob, $retryJob)
Check 'the retry says it queued nothing' ($retry.result.queued_again -eq $false) `
      ("queued_again = {0}" -f $retry.result.queued_again)
Check 'the retry is marked as a replay' ($retry.result.replayed -eq $true) `
      ("replayed = {0}" -f $retry.result.replayed)
Check 'the first send did not claim to have executed' ($first.result.executed -eq $false) `
      ("executed = {0}" -f $first.result.executed)

# Wait for the job to finish, then read what it actually did.
Write-Host "  waiting for the job to finish..."
$deadline = (Get-Date).AddSeconds(180)
$status = $null
while ((Get-Date) -lt $deadline) {
    $tmp = [IO.Path]::GetTempFileName()
    & $call -Tool horizun_job_status -Arguments (@{ job_id = $firstJob } | ConvertTo-Json -Compress) -Quiet -Json $tmp -Server $Server | Out-Null
    $status = (Get-Content $tmp -Raw | ConvertFrom-Json).result.jobs[0]
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    if ($status.finished) { break }
    Start-Sleep -Seconds 5
}

$after = JobCount
Write-Host ("  job records after:  {0}" -f $after)

# THE MEASUREMENT. Three sends, one record.
Check 'exactly ONE job record was created by three sends' (($after - $before) -eq 1) `
      ("{0} record(s) appeared" -f ($after - $before))
Check 'the job reached a terminal state' ($status -and $status.finished) `
      ("state = {0}" -f $status.state)
Check 'the job state is ok' ($status.state -eq 'ok') ("state = {0}" -f $status.state)

# What the script itself counted, inside Revit. This is the one that cannot be
# satisfied by a reply that merely says the right thing.
# The record stores the whole CommandResult, so the script's own return value is
# under .output - not at the top level, which is where the first version of this
# looked and found nothing. An absent value is NOT a pass: $null -eq 1 is false,
# so it failed rather than passing silently, which is the only reason it was
# noticed at all.
$runs = $null
if ($status -and $status.result -and $status.result.output) { $runs = $status.result.output.runs_seen_by_this_engine }
Check 'the script body ran exactly ONCE, counted inside Revit' ($runs -eq 1) `
      ("the engine counted '{0}' run(s)" -f $runs)

# The checkpoints are the second, independent witness: the body labels each one
# with its own run number, so two executions would leave "run 2" in the record
# whatever the return value said.
$labels = @()
if ($status -and $status.recent_checkpoints) { $labels = @($status.recent_checkpoints | ForEach-Object { $_.label }) }
Check 'no checkpoint mentions a second run' (-not ($labels -match 'run 2')) `
      ("checkpoints: " + ($labels -join ' | '))

$failed = @($checks | Where-Object { -not $_.ok }) + @($steps | Where-Object { -not $_.ok })

Write-Host ("-" * 72)
Write-Host ("  {0} check(s), {1} wrong" -f ($checks.Count + $steps.Count), $failed.Count)

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        schema           = 1
        generated_utc    = (Get-Date).ToUniversalTime().ToString('o')
        revit_year       = $Year
        document         = $Document
        idempotency_key  = $key
        job_records_before = $before
        job_records_after  = $after
        first_job_id     = $firstJob
        retry_job_id     = $retryJob
        runs_counted_in_revit = $runs
        final_job_state  = $(if ($status) { $status.state } else { $null })
        steps            = $steps
        checks           = $checks
        verdict          = $(if ($failed.Count -eq 0) { 'sent three times, ran once' } else { 'BROKEN' })
    } | ConvertTo-Json -Depth 12 | Out-File -FilePath $Json -Encoding utf8
    Write-Host "  wrote $Json"
}

if ($failed.Count -gt 0) { exit 1 }
Write-Host ""
Write-Host "  SENT THREE TIMES, RAN ONCE." -ForegroundColor Green
exit 0
