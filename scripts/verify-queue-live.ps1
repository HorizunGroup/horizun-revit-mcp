#Requires -Version 5.1
<#
  BOUNDED FIFO AND CANCELLATION, AGAINST A REAL REVIT.

  This is deliberately one MCP session with four overlapping tool calls. A slow
  read-only Python body occupies Revit's UI thread; two reads and a second Python
  body enter behind it. The second Python call is then cancelled while waiting.

  The test proves behavior from observable facts, not only from a success flag:

    * accepted reads return bridge_queue with their admission positions;
    * the surviving reads answer in FIFO order;
    * cancellation says the queued call never started; and
    * the cancelled script's marker file does not exist afterwards.

  No model transaction is opened and the model is not saved.

  Exit codes:  0 all checks passed   1 one or more checks failed
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Document,
    [string]$Server,
    [string]$Json,
    [int]$Year = 2025
)
$ErrorActionPreference = 'Stop'

if (-not $Server) {
    $Server = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
}
if (-not (Test-Path $Server)) { throw "MCP server not found: $Server" }

$env:HORIZUN_REVIT_YEAR = "$Year"
$marker = Join-Path ([IO.Path]::GetTempPath()) ('horizun-queue-cancel-' + [guid]::NewGuid().ToString('N') + '.txt')
$markerPython = $marker.Replace('\', '/')

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Server
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$proc = [Diagnostics.Process]::Start($psi)

function Send($obj) {
    $proc.StandardInput.WriteLine(($obj | ConvertTo-Json -Depth 30 -Compress))
    $proc.StandardInput.Flush()
}

function Read-Reply([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $task = $proc.StandardOutput.ReadLineAsync()
        $remaining = [Math]::Max(1, [int](($deadline - (Get-Date)).TotalMilliseconds))
        if (-not $task.Wait($remaining)) { return $null }
        $line = $task.Result
        if ($null -eq $line) { return $null }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $reply = $line | ConvertFrom-Json } catch { continue }
        if ($null -ne $reply.id) { return $reply }
    }
    return $null
}

function Tool-Request([int]$id, [string]$name, $arguments) {
    return @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'
              params = @{ name = $name; arguments = $arguments } }
}

function Tool-Data($reply) {
    if (-not $reply -or -not $reply.result -or -not $reply.result.content) { return $null }
    $text = $reply.result.content[0].text
    if (-not $text) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail) {
    $checks.Add([pscustomobject]@{ name = $name; ok = $ok; detail = $detail }) | Out-Null
    if ($ok) { Write-Host ("  OK    {0}" -f $name) -ForegroundColor Green }
    else { Write-Host ("  WRONG {0} - {1}" -f $name, $detail) -ForegroundColor Red }
}

try {
    Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'
            params = @{ protocolVersion = '2024-11-05'; capabilities = @{}
                        clientInfo = @{ name = 'verify-queue-live'; version = '1' } } }
    $init = Read-Reply 30
    if (-not $init) { throw 'The MCP server did not answer initialize.' }
    Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Write-Host ""
    Write-Host "FIFO queue, live - Revit $Year, document '$Document'" -ForegroundColor Cyan
    Write-Host ("  cancellation marker: {0}" -f $marker)
    Write-Host ("-" * 72)

    $blocker = @{ code = "import time`ntime.sleep(6)`n__output__ = {'blocker': 'done'}"
                  target_document = $Document }
    $cancelled = @{ code = "f = open(r'$markerPython', 'w')`nf.write('RAN')`nf.close()`n__output__ = {'marker_written': True}"
                    target_document = $Document }

    # Give the blocker time to reach the UI thread. Everything sent after this
    # point should therefore have at least one request ahead at admission.
    Send (Tool-Request 10 'horizun_execute_python' $blocker)
    Start-Sleep -Milliseconds 1200
    Send (Tool-Request 11 'horizun_health' @{})
    Start-Sleep -Milliseconds 100
    Send (Tool-Request 12 'horizun_execute_python' $cancelled)
    Start-Sleep -Milliseconds 100
    Send (Tool-Request 13 'get_document_info' @{})
    Start-Sleep -Milliseconds 300
    Send @{ jsonrpc = '2.0'; method = 'notifications/cancelled'
            params = @{ requestId = 12; reason = 'verify cancellation before start' } }

    $replies = @{}
    $order = New-Object System.Collections.Generic.List[int]
    $deadline = (Get-Date).AddSeconds(45)
    while ($replies.Count -lt 4 -and (Get-Date) -lt $deadline) {
        $reply = Read-Reply 45
        if (-not $reply) { break }
        $rid = [int]$reply.id
        if ($rid -in 10,11,12,13) {
            $replies[$rid] = $reply
            $order.Add($rid) | Out-Null
        }
    }

    $blockerData = Tool-Data $replies[10]
    $healthData = Tool-Data $replies[11]
    $documentData = Tool-Data $replies[13]
    $cancelMessage = if ($replies[12] -and $replies[12].error) { [string]$replies[12].error.message } else { '' }

    Check 'all four overlapping calls answered' ($replies.Count -eq 4) `
          ("received ids: " + (($replies.Keys | Sort-Object) -join ', '))
    Check 'the blocker itself was not queued' ($blockerData.bridge_queue.queued -eq $false) `
          ("bridge_queue = " + ($blockerData.bridge_queue | ConvertTo-Json -Compress))
    Check 'the first read waited behind the blocker' `
          ($healthData.bridge_queue.queued -eq $true -and [int]$healthData.bridge_queue.ahead_at_admission -ge 1) `
          ("bridge_queue = " + ($healthData.bridge_queue | ConvertTo-Json -Compress))
    Check 'the later read preserved its admission position' `
          ($documentData.bridge_queue.queued -eq $true -and [int]$documentData.bridge_queue.ahead_at_admission -ge 2) `
          ("bridge_queue = " + ($documentData.bridge_queue | ConvertTo-Json -Compress))
    Check 'the two surviving reads answered in FIFO order' `
          ($order.IndexOf(11) -ge 0 -and $order.IndexOf(13) -gt $order.IndexOf(11)) `
          ("reply order: " + ($order -join ', '))
    Check 'cancellation reported that the queued call never started' `
          ($cancelMessage -match 'FIFO queue' -and $cancelMessage -match 'Nothing was executed or written') `
          $cancelMessage
    Check 'the cancelled script produced no marker side effect' (-not (Test-Path $marker)) `
          $(if (Test-Path $marker) { 'marker exists: the cancelled body ran' } else { 'marker absent' })

    $failed = @($checks | Where-Object { -not $_.ok })
    Write-Host ("-" * 72)
    Write-Host ("  {0} checks, {1} wrong" -f $checks.Count, $failed.Count)

    if ($Json) {
        $dir = Split-Path -Parent $Json
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        [pscustomobject]@{
            schema = 1
            generated_utc = (Get-Date).ToUniversalTime().ToString('o')
            revit_year = $Year
            document = $Document
            server_sha256 = (Get-FileHash $Server -Algorithm SHA256).Hash.ToLower()
            reply_order = $order.ToArray()
            checks = $checks.ToArray()
            queue = [pscustomobject]@{
                blocker = $blockerData.bridge_queue
                first_read = $healthData.bridge_queue
                later_read = $documentData.bridge_queue
            }
            cancellation_message = $cancelMessage
            marker_absent = (-not (Test-Path $marker))
            passed = ($failed.Count -eq 0)
        } | ConvertTo-Json -Depth 20 | Out-File -FilePath $Json -Encoding utf8
    }

    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
finally {
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.HasExited -and -not $proc.WaitForExit(10000)) { try { $proc.Kill() } catch { } }
    if (Test-Path $marker) { Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue }
}
