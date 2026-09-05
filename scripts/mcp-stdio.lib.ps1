#Requires -Version 5.1
<#
  Speak MCP to a stdio server and come back with what it actually said.

  scripts/hz-call.ps1 exists to make ONE tool call and is shaped around that. The
  integrations need the other half of the handshake - initialize, the server's
  advertised capabilities, tools/list, and whether the process stayed alive - to
  answer "would this client find the tools?" WITHOUT a client installed. That
  question is the core of the Claude Desktop verification, so it gets
  a transport of its own rather than a flag on the call helper.

  Dot-source it:

      . scripts/mcp-stdio.lib.ps1
      $probe = Invoke-HorizunMcpProbe -Command 'C:\...\horizun-mcp.exe' -ListTools
      $probe.ok            # the handshake completed
      $probe.tool_names    # every tool the server advertised
      $probe.server_info   # name and version it announced

  It never throws for a server that answers badly: a bad answer is a RESULT, and
  a verifier that dies on it reports a broken script instead of a broken bridge.
#>

function Invoke-HorizunMcpProbe {
    [CmdletBinding()]
    param(
        # The executable to launch. For a launcher-style integration this is the
        # command a client would run, exactly as the client would run it.
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$ArgumentList = @(),
        [hashtable]$Environment,
        [string]$WorkingDirectory,
        [int]$TimeoutSec = 120,
        # Ask for the tool list as well as the handshake. Off for a bare liveness
        # check, which is all a "does this path launch?" diagnosis needs.
        [switch]$ListTools,
        [string]$CallTool,
        [string]$CallArgumentsJson = '{}'
    )

    $result = [ordered]@{
        ok               = $false
        command          = $Command
        arguments        = $ArgumentList
        started          = $false
        exit_code        = $null
        protocol_version = $null
        server_info      = $null
        capabilities     = $null
        tool_names       = @()
        tool_count       = 0
        list_changed     = $null
        call_result      = $null
        call_is_error    = $null
        stderr_excerpt   = $null
        problem          = $null
        elapsed_ms       = 0
    }

    # ONE exit point, deliberately. The first version returned inside `try` and
    # filled exit_code, stderr and the clock in `finally` - which runs AFTER the
    # object has already been built, so every probe reported elapsed_ms 0 and no
    # exit code. Everything below assigns into $result and falls through to the
    # single conversion at the end.
    if (-not (Test-Path -LiteralPath $Command -PathType Leaf)) {
        $result.problem = "the command does not exist: $Command"
        return [pscustomobject]$result
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Command
    foreach ($a in $ArgumentList) { $null = $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    if ($Environment) {
        # A SECRET PASSED THIS WAY IS NOT ON A COMMAND LINE. Anything in
        # ArgumentList is visible to every process on the machine through the
        # command line of this one; the environment block of a child is not.
        foreach ($k in $Environment.Keys) { $psi.Environment[$k] = [string]$Environment[$k] }
    }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    try { $proc = [Diagnostics.Process]::Start($psi) }
    catch {
        $result.problem = "the command would not start: $($_.Exception.Message)"
        return [pscustomobject]$result
    }
    $result.started = $true
    $stderr = $proc.StandardError.ReadToEndAsync()

    $send = {
        param($obj)
        $proc.StandardInput.WriteLine(($obj | ConvertTo-Json -Depth 40 -Compress))
        $proc.StandardInput.Flush()
    }

    $readReply = {
        param([int]$id, [int]$seconds)
        $deadline = (Get-Date).AddSeconds($seconds)
        while ((Get-Date) -lt $deadline) {
            $task = $proc.StandardOutput.ReadLineAsync()
            $remaining = [Math]::Max(1, [int](($deadline - (Get-Date)).TotalMilliseconds))
            $delay = [Threading.Tasks.Task]::Delay($remaining)
            $winner = [Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]@($task, $delay)).Result
            if (-not [object]::ReferenceEquals($winner, $task)) { return $null }
            $line = $task.Result
            if ($null -eq $line) { return $null }
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $o = $line | ConvertFrom-Json } catch { continue }
            # Only the reply to the request we sent ends the wait. Taking the
            # first parseable line reads a progress notification as an answer.
            if ($null -ne $o.id -and [int]$o.id -eq $id) { return $o }
        }
        return $null
    }

    try {
        & $send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'
                   params = @{ protocolVersion = '2024-11-05'; capabilities = @{}
                               clientInfo = @{ name = 'horizun-mcp-probe'; version = '1' } } }
        $init = & $readReply 1 ([Math]::Min($TimeoutSec, 90))
        if (-not $init) { $result.problem = 'the server never answered initialize' }
        elseif ($null -ne $init.error) {
            $result.problem = 'initialize returned an error: ' + ($init.error | ConvertTo-Json -Compress)
        }
        else {
            $result.protocol_version = $init.result.protocolVersion
            $result.server_info = $init.result.serverInfo
            $result.capabilities = $init.result.capabilities
            try { $result.list_changed = [bool]$init.result.capabilities.tools.listChanged } catch { $result.list_changed = $null }
            & $send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

            $failed = $false
            if ($ListTools) {
                & $send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
                $list = & $readReply 2 $TimeoutSec
                if (-not $list) { $result.problem = 'the server never answered tools/list'; $failed = $true }
                elseif ($null -ne $list.error) {
                    $result.problem = 'tools/list returned an error: ' + ($list.error | ConvertTo-Json -Compress)
                    $failed = $true
                }
                else {
                    $result.tool_names = @($list.result.tools | ForEach-Object { $_.name })
                    $result.tool_count = $result.tool_names.Count
                }
            }

            if ($CallTool -and -not $failed) {
                $argObj = $CallArgumentsJson | ConvertFrom-Json
                & $send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'
                           params = @{ name = $CallTool; arguments = $argObj } }
                $call = & $readReply 3 $TimeoutSec
                if (-not $call) { $result.problem = "the server never answered tools/call $CallTool"; $failed = $true }
                elseif ($null -ne $call.error) {
                    $result.call_is_error = $true
                    $result.call_result = $call.error
                }
                else {
                    $result.call_is_error = [bool]$call.result.isError
                    $names = @()
                    if ($null -ne $call.result) { $names = @($call.result.PSObject.Properties.Name) }
                    if ($names -contains 'structuredContent' -and $null -ne $call.result.structuredContent) {
                        $result.call_result = $call.result.structuredContent
                    }
                    else { $result.call_result = $call.result.content }
                }
            }

            if (-not $failed) { $result.ok = $true }
        }
    }
    catch { $result.problem = $_.Exception.Message }

    $clock.Stop()
    $result.elapsed_ms = [int]$clock.ElapsedMilliseconds
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(15000)) { try { $proc.Kill() } catch { } }
    try { $result.exit_code = $proc.ExitCode } catch { }
    try {
        $err = $stderr.Result
        if ($err) {
            $err = $err.Trim()
            if ($err.Length -gt 900) { $err = $err.Substring(0, 900) + ' ...' }
            $result.stderr_excerpt = $err
        }
    }
    catch { }

    return [pscustomobject]$result
}
