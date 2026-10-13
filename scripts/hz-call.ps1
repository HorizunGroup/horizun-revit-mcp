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
    # A RESOURCE INSTEAD OF A TOOL. Some facts a caller must verify are published
    # as MCP resources rather than tools - the contract hash among them, at
    # horizun://build/identity. A harness that cannot ask for one has to read the
    # fact off a file on disk instead, and a file describes whatever binary last
    # wrote it, not the one that just answered.
    [string]$Resource,
    # JSON object. Defaults to no arguments. Hand-typed JSON is where Windows paths
    # go wrong ("C:\x" is an invalid escape; it must be "C:\\x" or "C:/x"): prefer
    # -ArgumentsObject from PowerShell, or -ArgumentsPath written by ConvertTo-Json.
    [string]$Arguments = '{}',
    # The arguments as a PowerShell hashtable/object, serialized here with
    # ConvertTo-Json - no hand-built JSON, so no escaping to get wrong.
    [object]$ArgumentsObject,
    # Exact transport for callers that launch a separate PowerShell process.
    # JSON quotes and Windows paths are not reliably preserved through the
    # native Windows command line.
    [string]$ArgumentsPath,
    [string]$Server,
    [string]$Json,
    # Seconds to wait for the reply. A model scan or a long script needs more
    # than a default; a timeout here is the harness giving up, not the bridge.
    [int]$TimeoutSec = 900,
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

# ARGUMENTS FIRST, before any server is looked for or started: a malformed
# argument string is the caller's mistake and is reported as one, with the
# position and the usual cause, instead of costing a server process.
if ($PSBoundParameters.ContainsKey('ArgumentsObject') -and
    ($ArgumentsPath -or $PSBoundParameters.ContainsKey('Arguments'))) {
    throw "Use exactly one of -Arguments, -ArgumentsPath or -ArgumentsObject."
}
if ($ArgumentsPath) {
    if (-not (Test-Path -LiteralPath $ArgumentsPath -PathType Leaf)) {
        throw "-ArgumentsPath does not exist: $ArgumentsPath"
    }
    $Arguments = Get-Content -LiteralPath $ArgumentsPath -Raw
}
if ($PSBoundParameters.ContainsKey('ArgumentsObject')) {
    $Arguments = if ($null -eq $ArgumentsObject) { '{}' } else { $ArgumentsObject | ConvertTo-Json -Depth 40 -Compress }
}
try { $argObj = $Arguments | ConvertFrom-Json } catch {
    $why = $_.Exception.Message
    $hint = ''
    if ($why -match 'escape' -or $Arguments -match '[^\\]\\[^\\"/bfnrtu]') {
        $hint = ' A backslash inside a JSON string must be doubled - a Windows path like C:\folder is written ' +
                '"C:\\folder" (or "C:/folder"). Pass -ArgumentsObject @{ path = ''C:\folder'' } and let ' +
                'ConvertTo-Json do the escaping.'
    }
    throw "arguments must be a JSON object: $why.$hint Nothing was sent."
}
if ($null -ne $argObj -and ($argObj -isnot [System.Management.Automation.PSCustomObject])) {
    throw "arguments must be a JSON object ({...}), not $($argObj.GetType().Name). Nothing was sent."
}

if (-not $Server -and $env:HORIZUN_SERVER_EXE) {
    # A development session drives a freshly built server against a development
    # add-in WITHOUT replacing the installed pair (scripts/live/dev-addin-session.ps1).
    # The live library calls this script without -Server, so the override has to
    # travel in the environment. Set it only for that shell; the installed server
    # stays the default everywhere else.
    $Server = $env:HORIZUN_SERVER_EXE
}
if (-not $Server) {
    $Server = if ($env:HORIZUN_SERVER_EXE) { $env:HORIZUN_SERVER_EXE } else { Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
}
if (-not (Test-Path $Server)) { throw "MCP server not found: $Server" }

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

# A reply PowerShell's object model refuses although it is valid JSON - a property named
# "" or two names differing only in case - is still the reply. It is read as a hashtable
# (PowerShell 6+), exactly those keys are renamed so every later ConvertFrom-Json of the
# saved file works, and reply_parse_note SAYS so. Dropping the line instead left the caller
# waiting out -TimeoutSec on an answer it already had (MEASURED 2026-09-27: a structural
# load with no load case was grouped under "", and a finished call looked hung for 15 min).
$script:replyParseNote = $null
function Repair-JsonKeys($node) {
    if ($node -is [System.Collections.IDictionary]) {
        $fixed = [ordered]@{}
        foreach ($k in @($node.Keys)) {
            $name = [string]$k
            if ($name -eq '') { $name = '(empty key)' }
            while ($fixed.Contains($name)) { $name = $name + ' (case duplicate)' }
            $fixed[$name] = Repair-JsonKeys $node[$k]
        }
        return $fixed
    }
    if ($node -is [System.Collections.IList] -and -not ($node -is [string])) {
        $items = @(foreach ($item in $node) { , (Repair-JsonKeys $item) })
        return , $items
    }
    return $node
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
        $o = $null
        try { $o = $line | ConvertFrom-Json } catch {
            $why = $_.Exception.Message
            if ($PSVersionTable.PSVersion.Major -ge 6) {
                try { $o = Repair-JsonKeys ($line | ConvertFrom-Json -AsHashtable) } catch { $o = $null }
            }
            if ($null -eq $o) {
                Write-Verbose ("stdout was not JSON: {0}" -f $why)
                continue
            }
            $script:replyParseNote = "valid JSON that ConvertFrom-Json refused ($why); read as a hashtable, " +
                "with an empty key renamed '(empty key)' and a key differing only in case suffixed ' (case duplicate)'"
            Write-Verbose $script:replyParseNote
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

if ($Resource) {
    Send @{ jsonrpc = '2.0'; id = 2; method = 'resources/read'; params = @{ uri = $Resource } }
} else {
    # THE ARGUMENTS AS GIVEN. $argObj came from ConvertFrom-Json, which under PowerShell 6+
    # turns ISO 8601 text into a DateTime, and sending it back through ConvertTo-Json moved
    # "2026-10-10T12:00:00+02:00" into this machine's zone before the server saw it (backlog
    # P0 #1). The text was validated above; it is sent as it is, on one line - a raw line
    # break cannot occur inside a JSON string, so removing them changes nothing else.
    $argsLine = ($Arguments -replace "[`r`n]+", ' ').Trim()
    $line = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":' +
            (ConvertTo-Json -InputObject ([string]$Tool) -Compress) + ',"arguments":' + $argsLine + '}}'
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()
}
$reply = ReadReply $TimeoutSec
$clock.Stop()

try { $proc.StandardInput.Close() } catch { }
if (-not $proc.WaitForExit(30000)) { try { $proc.Kill() } catch { } }

$text = $null; $isError = $null; $data = $null
if ($reply -and $Resource) {
    # A resource answers with contents[], not content[] with an isError beside it.
    # Its text IS the payload, so it is parsed as the data channel directly.
    try { $text = $reply.result.contents[0].text } catch { $text = $null }
    $isError = $false
    if ($null -ne $reply.error) { $isError = $true; $text = ($reply.error | ConvertTo-Json -Compress) }
    if ($text -and -not $isError) { try { $data = $text | ConvertFrom-Json } catch { $data = $null } }
}
elseif ($reply) {
    $text = $reply.result.content[0].text
    $isError = [bool]$reply.result.isError
    # The text block is for a person and can legally contain the JSON payload
    # followed by "what Revit raised while this ran". Parsing the whole block
    # then returns null on precisely the calls that raised a warning or dialog.
    # structuredContent is the machine channel and carries the payload alone.
    #
    # ASK WHETHER THE PROPERTY EXISTS, not just whether it is null. Under
    # Set-StrictMode a reply with no structuredContent at all - which is what comes
    # back when Revit has gone away mid-run - makes the dereference THROW, and the
    # harness then dies reporting a missing property instead of reporting that the
    # bridge lost Revit. That is the transport hiding the finding.
    $resultNames = @()
    if ($reply.result -is [System.Collections.IDictionary]) { $resultNames = @($reply.result.Keys) }
    elseif ($null -ne $reply.result) { $resultNames = @($reply.result.PSObject.Properties.Name) }
    if ($resultNames -contains 'structuredContent' -and $null -ne $reply.result.structuredContent) {
        $data = $reply.result.structuredContent
    }
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
    # Set only when the reply parsed as a hashtable with renamed keys (see Repair-JsonKeys).
    reply_parse_note = $script:replyParseNote
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
