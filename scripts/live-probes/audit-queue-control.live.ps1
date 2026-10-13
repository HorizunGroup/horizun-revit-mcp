#Requires -Version 7.0
<#
  LIVE probes for lote 5 of the 2026-10-08 audit, against ONE running Revit named by pid,
  below the MCP server (raw pipe). Read-only on the model: the queued commands are
  horizun_model_scan / horizun_health on whatever document is active.

    b) __horizun_cancel_job: a durable job still queued is removed and its record finished
       "cancelled"; a job already running answers not_in_queue and finishes normally.
    c) a SYNC request removed by __horizun_cancel_queued answers with the structured
       detail.never_started=true the server now decides on (not just the words).
    e) with every ordinary connection held, a control message still gets a reserved slot,
       an ordinary command on a reserved slot is refused, and past the reserve the
       connection is refused outright.
    d) a refused ExternalEvent.Raise() cannot be provoked on demand in a healthy Revit; it is
       reported not_covered here and stays proven offline.

  Usage: pwsh -File audit-queue-control.live.ps1 -RevitPid <pid> -Out <result.json>
#>
param(
    [Parameter(Mandatory)][int]$RevitPid,
    [Parameter(Mandatory)][string]$Out,
    [int]$OrdinarySlots = 21,     # RequestGate.MaxDepth + 5
    [int]$ReservedSlots = 4,      # PipeAdmission.ControlReserve
    [string]$Document = 'HZ_NIGHT_W',
    [ValidateSet('b','c','e','all')][string]$Only = 'all'
)
function Trace($m) { Write-Host ((Get-Date).ToString('HH:mm:ss.fff') + ' ' + $m) }
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'audit-pipe.lib.ps1')
$bridge = Get-HzBridge -RevitPid $RevitPid
$jobs = Join-Path $env:USERPROFILE '.horizun\jobs'
$cases = New-Object System.Collections.Generic.List[object]
function Case($name, $ok, $detail) { $cases.Add([pscustomobject]@{ name = $name; outcome = $(if ($ok) { 'pass' } else { 'fail' }); detail = [string]$detail }) }
function Skip($name, $why) { $cases.Add([pscustomobject]@{ name = $name; outcome = 'not_covered'; detail = [string]$why }) }

function Job-Events([string]$jobId) {
    $p = Join-Path $jobs "$jobId.jsonl"
    if (-not (Test-Path -LiteralPath $p)) { return @() }
    @(Get-Content -LiteralPath $p | Where-Object { $_.Trim() } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $_ })
}
function Job-Finish([string]$jobId) { @(Job-Events $jobId | Where-Object { $_.event -eq 'finish' }) | Select-Object -Last 1 }
function Job-Ran([string]$jobId) { @(Job-Events $jobId | Where-Object { $_.event -eq 'running' }).Count -gt 0 }
function Submit-Scan([string]$label) {
    $r = Invoke-HzRaw -Bridge $bridge -Command 'horizun_submit_job' -Params @{
        tool = 'horizun_model_scan'; arguments = @{ target_document_title = $Document }; idempotency_key = "night-$label-" + [guid]::NewGuid().ToString('N')
    } -TimeoutMs 60000
    if (-not $r.success) { throw "submit_job refused: $($r.error)" }
    return [string]$r.data.job_id
}
function Wait-Until([scriptblock]$cond, [int]$ms) {
    $until = [DateTime]::UtcNow.AddMilliseconds($ms)
    while ([DateTime]::UtcNow -lt $until) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 100 }
    return [bool](& $cond)
}

# ---------------------------------------------------------------- b) cancel_job
if ($Only -in 'b','all') { Trace 'b: start'
try {
    $a = Submit-Scan 'A'
    $b = Submit-Scan 'B'
    $cancelB = Invoke-HzRaw -Bridge $bridge -Command '__horizun_cancel_job' -Params @{ job_id = $b } -TimeoutMs 10000
    $bFinished = Wait-Until { $null -ne (Job-Finish $b) } 10000
    $bFinish = Job-Finish $b
    Case 'cancel_job: a queued durable job is removed and recorded cancelled; it never ran' `
        ($cancelB.success -and $cancelB.data.cancelled_before_start -eq $true -and $cancelB.data.state -eq 'removed_before_start' -and
         $bFinished -and $bFinish.status -eq 'cancelled' -and -not (Job-Ran $b)) `
        ("answer=" + ($cancelB.data | ConvertTo-Json -Compress) + " finish=" + ($bFinish | ConvertTo-Json -Compress) + " ran=" + (Job-Ran $b))

    $aRunning = Wait-Until { Job-Ran $a } 120000
    if (-not $aRunning) { Skip 'cancel_job: a running job answers not_in_queue and is not cancelled' 'job A never reached running within 120 s' }
    else {
        $cancelA = Invoke-HzRaw -Bridge $bridge -Command '__horizun_cancel_job' -Params @{ job_id = $a } -TimeoutMs 10000
        $aDone = Wait-Until { $null -ne (Job-Finish $a) } 600000
        $aFinish = Job-Finish $a
        Case 'cancel_job: a running job answers not_in_queue and is not cancelled' `
            ($cancelA.success -and $cancelA.data.cancelled_before_start -eq $false -and $cancelA.data.state -eq 'not_in_queue' -and
             $aDone -and $aFinish.status -ne 'cancelled') `
            ("answer=" + ($cancelA.data | ConvertTo-Json -Compress) + " finish=" + ($aFinish | ConvertTo-Json -Compress))
    }
}
catch { Case 'cancel_job: harness' $false $_.Exception.Message } }

# ---------------------------------------------------------------- c) structured never_started
if ($Only -in 'c','all') { Trace 'c: start'
try {
    $long = Connect-HzPipe -Bridge $bridge
    $w1 = Send-HzLine -Pipe $long -Bridge $bridge -Command 'horizun_model_scan' -Params @{ target_document_title = $Document }
    Start-Sleep -Milliseconds 300
    $queued = Connect-HzPipe -Bridge $bridge
    $w2 = Send-HzLine -Pipe $queued -Bridge $bridge -Command 'horizun_health' -Params @{}
    Start-Sleep -Milliseconds 300
    $cancel = Invoke-HzRaw -Bridge $bridge -Command '__horizun_cancel_queued' -Params @{ wire_id = $w2 } -TimeoutMs 10000
    $w2Reply = Read-HzLine -Pipe $queued -TimeoutMs 30000
    $w1Reply = Read-HzLine -Pipe $long -TimeoutMs 600000
    $long.Dispose(); $queued.Dispose()
    if ($cancel.data.cancelled_before_start -ne $true) {
        Skip 'never_started: a request removed by cancel_queued carries detail.never_started' ("the request was not still queued: " + ($cancel.data | ConvertTo-Json -Compress))
    }
    else {
        Case 'never_started: a request removed by cancel_queued carries detail.never_started' `
            ($w2Reply.success -eq $false -and $w2Reply.detail.never_started -eq $true -and $w2Reply.detail.transaction_status -eq 'not_started') `
            ("reply=" + ($w2Reply | ConvertTo-Json -Compress -Depth 6))
    }
    Case 'never_started: the request ahead of it was not disturbed' ($w1Reply.success -eq $true) ("scan success=" + $w1Reply.success)
}
catch { Case 'never_started: harness' $false $_.Exception.Message } }

# ---------------------------------------------------------------- e) reserved control connections
if ($Only -in 'e','all') { Trace 'e: start'
$mute = New-Object System.Collections.Generic.List[object]
try {
    for ($i = 0; $i -lt $OrdinarySlots; $i++) { $mute.Add((Connect-HzPipe -Bridge $bridge)) }
    Trace ('e: ' + $mute.Count + ' mute connections')
    Start-Sleep -Milliseconds 500
    Trace 'e: control on reserved slot'
    $status = Invoke-HzRaw -Bridge $bridge -Command '__horizun_request_status' -Params @{ wire_id = 'nothing-' + [guid]::NewGuid().ToString('N') } -TimeoutMs 10000
    Case 'reserved slots: a control message gets through with every ordinary connection held' ($status.success -eq $true) ("reply=" + ($status | ConvertTo-Json -Compress -Depth 4))
    Trace 'e: ordinary on reserved slot'
    $ordinary = Invoke-HzRaw -Bridge $bridge -Command 'horizun_health' -Params @{} -TimeoutMs 10000
    Case 'reserved slots: an ordinary command on a reserved slot is refused and nothing runs' `
        ($ordinary.success -eq $false -and [string]$ordinary.error -match 'reserved for control') ("reply=" + [string]$ordinary.error)
    for ($i = 0; $i -lt $ReservedSlots; $i++) { $mute.Add((Connect-HzPipe -Bridge $bridge)) }
    Start-Sleep -Milliseconds 500
    Trace 'e: past the reserve'
    $over = Invoke-HzRaw -Bridge $bridge -Command '__horizun_request_status' -Params @{ wire_id = 'x' } -TimeoutMs 10000
    Case 'reserved slots: past the reserve the connection is refused' ($over.success -eq $false -and [string]$over.error -match 'Too many') ("reply=" + [string]$over.error)
}
catch { Case 'reserved slots: harness' $false $_.Exception.Message }
finally { Trace 'e: disposing'; foreach ($m in $mute) { try { $m.Dispose() } catch { } } } }

Skip 'raise refused: only its own request is withdrawn' 'ExternalEvent.Raise() cannot be made to refuse in a healthy Revit on demand; proven offline (RequestGate.Abandon tests) and by build.'

$result = [pscustomobject]@{ revit_pid = $RevitPid; protocol = $bridge.Protocol; at = (Get-Date).ToString('o'); cases = $cases }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Out -Encoding utf8
$cases | Format-Table -AutoSize -Wrap name, outcome | Out-String -Width 200
