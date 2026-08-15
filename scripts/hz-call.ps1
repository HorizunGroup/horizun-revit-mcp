#Requires -Version 5.1
<#
  Call one Horizun tool over the real MCP transport, from a script.

  WHY THIS EXISTS RATHER THAN A CHAT CLIENT. Evidence for a release has to be
  reproducible and machine-readable. A tool call made through a chat window
  leaves a narration; this leaves the request, the reply, the duration and the
  exit status, in JSON, from the SAME stdio transport a client uses - the
  installed horizun-mcp.exe speaking JSON-RPC over stdin/stdout.

  It is also the harness the workflow runs need: seven skills, each run twice,
  with inputs and outputs hashed. That is not something to drive by hand.

  This helper sends ONE request and waits for its reply. Separate helper processes
  may run concurrently: the add-in admits them to its bounded FIFO queue and still
  executes only one Revit API command at a time. A full queue is refused as explicit
  backpressure; accepted calls report their measured wait in bridge_queue.

    scripts/hz-call.ps1 -Tool horizun_health
    scripts/hz-call.ps1 -Tool horizun_open_document -Arguments '{"path":"C:\\x\\a.rvt"}'
    scripts/hz-call.ps1 -Tool horizun_execute_python -Arguments (Get-Content q.json -Raw) -Json out.json

  Exit codes:  0 the tool answered   1 the tool returned an error   2 no reply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tool,
    # JSON object. Defaults to no arguments.
    [string]$Arguments = '{}',
    [string]$Server,
    [string]$Json,
    # Seconds to wait for the reply. A model scan or a long script needs more
    # than a default; a timeout here is the harness giving up, not the bridge.
    [int]$TimeoutSec = 900,
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

if (-not $Server) {
    $Server = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
}
if (-not (Test-Path $Server)) { throw "MCP server not found: $Server" }

try { $argObj = $Arguments | ConvertFrom-Json } catch { throw "-Arguments must be a JSON object: $($_.Exception.Message)" }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Server
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
# Output only. StandardInputEncoding does not exist on .NET Framework, which is
# what Windows PowerShell 5.1 runs on, and setting it throws at runtime rather
# than at parse time - so it survives a syntax check and dies on first use.
# No BOM: a BOM on the first line is not JSON, and the server would reject it.
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)

$proc = [Diagnostics.Process]::Start($psi)

function Send($obj) {
    $line = $obj | ConvertTo-Json -Depth 40 -Compress
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()
}

function ReadReply($seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $task = $proc.StandardOutput.ReadLineAsync()
        # Windows PowerShell 5.1 can leave both Task.Wait(timeout) and repeated
        # IsCompleted polling blind to a redirected StreamReader completion.
        # Task.WhenAny observes it correctly. Keep exactly one outstanding read
        # and race it against the remaining deadline.
        $remaining = [Math]::Max(1, [int](($deadline - (Get-Date)).TotalMilliseconds))
        $delay = [Threading.Tasks.Task]::Delay($remaining)
        $winner = [Threading.Tasks.Task]::WhenAny(
            [Threading.Tasks.Task[]]@($task, $delay)).Result
        if (-not [object]::ReferenceEquals($winner, $task)) {
            Write-Verbose "stdout read reached its deadline"
            return $null
        }
        $line = $task.Result
        Write-Verbose ("stdout line: {0} characters" -f $(if ($null -eq $line) { -1 } else { $line.Length }))
        if ($null -eq $line) { return $null }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $o = $line | ConvertFrom-Json } catch {
            Write-Verbose ("stdout was not JSON: {0}" -f $_.Exception.Message)
            continue
        }
        # Notifications carry no id. Only a reply to OUR request ends the wait -
        # taking the first line that parses is how a caller ends up reading a
        # progress notification as its answer.
        if ($null -ne $o.id) {
            Write-Verbose ("received reply id {0}" -f $o.id)
            return $o
        }
        Write-Verbose "ignored notification without id"
    }
    return $null
}

$clock = [Diagnostics.Stopwatch]::StartNew()

Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{ protocolVersion = '2024-11-05'; capabilities = @{}
                    clientInfo = @{ name = 'hz-call'; version = '1' } } }
$null = ReadReply 60
Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'
        params = @{ name = $Tool; arguments = $argObj } }
$reply = ReadReply $TimeoutSec
$clock.Stop()

try { $proc.StandardInput.Close() } catch { }
if (-not $proc.WaitForExit(30000)) { try { $proc.Kill() } catch { } }

$text = $null; $isError = $null; $data = $null
if ($reply) {
    $text = $reply.result.content[0].text
    $isError = [bool]$reply.result.isError
    # The text block is for a person and can legally contain the JSON payload
    # followed by "what Revit raised while this ran". Parsing the whole block
    # then returns null on precisely the calls that raised a warning or dialog.
    # structuredContent is the machine channel and carries the payload alone.
    if ($null -ne $reply.result.structuredContent) { $data = $reply.result.structuredContent }
    elseif ($text) { try { $data = $text | ConvertFrom-Json } catch { $data = $null } }
}

$out = [pscustomobject]@{
    tool          = $Tool
    arguments     = $argObj
    server        = $Server
    server_sha256 = (Get-FileHash $Server -Algorithm SHA256).Hash.ToLower()
    called_utc    = (Get-Date).ToUniversalTime().ToString('o')
    duration_ms   = $clock.ElapsedMilliseconds
    replied       = ($null -ne $reply)
    is_error      = $isError
    # Both: the parsed object when it parsed, and the raw text always. A reply
    # that failed to parse is still the reply, and dropping it is how a failure
    # becomes unexplainable an hour later.
    result        = $data
    raw           = $text
}

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $out | ConvertTo-Json -Depth 40 | Out-File -FilePath $Json -Encoding utf8
}

if (-not $Quiet) {
    if (-not $reply) { Write-Host "no reply in ${TimeoutSec}s" -ForegroundColor Red }
    elseif ($isError) { Write-Host ("ERROR ({0} ms): {1}" -f $clock.ElapsedMilliseconds, $text) -ForegroundColor Red }
    else {
        Write-Host ("ok ({0} ms)" -f $clock.ElapsedMilliseconds) -ForegroundColor Green
        if ($text) { Write-Host $text }
    }
}

if (-not $reply) { exit 2 }
if ($isError) { exit 1 }
exit 0
