#Requires -Version 5.1
<#
  Connect ChatGPT Work to this machine's Horizun bridge through OpenAI's tunnel.

  WHAT THE OFFICIAL MECHANISM ACTUALLY IS - checked against OpenAI's documentation
  before a line of this was written, because inventing an endpoint would be worse
  than shipping nothing:

    Secure MCP Tunnel. `tunnel-client` runs INSIDE this network, makes an
    OUTBOUND HTTPS connection to api.openai.com/v1/tunnel/*, long-polls for
    queued MCP work, forwards each JSON-RPC request to the private MCP server and
    posts the reply back through the same tunnel. ChatGPT reaches it by creating a
    developer-mode app and choosing Tunnel under Connection.
    -- developers.openai.com/api/docs/guides/secure-mcp-tunnels

  AND THE DECISIVE DETAIL: that client speaks **stdio** to the private server, via
  `--mcp-command`. So Horizun needs NO adapter, NO HTTP listener and NO second
  transport. The exact same horizun-mcp.exe that Codex and Claude Desktop launch
  is the one ChatGPT reaches, unmodified - which is why JSON-RPC framing,
  initialize, sessions, tools/list with list_changed, tools/call,
  structuredContent, the error codes, cancellation, the bounded FIFO queue and its
  backpressure, and long jobs through submit_job/job_status all behave identically.
  There is nothing to keep in sync because there is no second implementation.

  WHAT THIS SCRIPT DOES NOT DO, and will not be talked into doing:
    - It does not download tunnel-client. That binary is OpenAI's; the user
      installs it from the official release page.
    - It does not create the tunnel, the developer-mode app, or enable anything
      organisation-wide. Those need permissions only the account owner has.
    - It does not open a port. The server has no listener at all; the only network
      connection is tunnel-client's outbound one.
    - It never writes the API key into this repository, a command line, a log, a
      diagnostic or an installer file. The key lives in the Windows credential
      store (DPAPI, current user) and reaches tunnel-client only through the
      environment block of the child process.

  BEFORE ENABLING IT, understand what it means: MCP requests and replies for this
  Revit travel through OpenAI-hosted infrastructure. -Start refuses without
  -IUnderstandTrafficLeavesThisMachine for exactly that reason.

    scripts/chatgpt-tunnel.ps1 -Status
    scripts/chatgpt-tunnel.ps1 -SetApiKey                 # prompts, never echoes
    scripts/chatgpt-tunnel.ps1 -Init -TunnelId tunnel_...
    scripts/chatgpt-tunnel.ps1 -Doctor
    scripts/chatgpt-tunnel.ps1 -Start -IUnderstandTrafficLeavesThisMachine
    scripts/chatgpt-tunnel.ps1 -Stop
    scripts/chatgpt-tunnel.ps1 -Revoke                    # stop, forget the key

  Exit codes: 0 done  1 failed  2 could not run  3 done, one user step remains
#>
[CmdletBinding()]
param(
    [switch]$Status,
    [switch]$SetApiKey,
    [switch]$Init,
    [switch]$Doctor,
    [switch]$Start,
    [switch]$Stop,
    [switch]$Revoke,
    [string]$TunnelId,
    [switch]$IUnderstandTrafficLeavesThisMachine,
    [string]$ServerPath,
    [string]$TunnelClientPath,
    [string]$Json,
    # Test seams.
    [string]$StateRoot,
    [string]$StatusPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-stdio.lib.ps1')
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')
. (Join-Path $PSScriptRoot 'chatgpt-secret.lib.ps1')

$CLIENT = 'chatgpt'
$PROFILE_NAME = 'horizun-revit'
$RELEASES = 'https://github.com/openai/tunnel-client/releases/latest'
$TUNNEL_SETTINGS = 'https://platform.openai.com/settings/organization/tunnels'
$CHATGPT_APPS = 'https://chatgpt.com/plugins'

if (-not $StateRoot) { $StateRoot = Join-Path $env:LOCALAPPDATA 'Horizun\integrations\chatgpt' }
if (-not $ServerPath) { $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
$pidFile = Join-Path $StateRoot 'tunnel-client.pid'
$profileFile = Join-Path $StateRoot 'profiles\horizun-revit.yaml'

$actions = New-Object System.Collections.Generic.List[object]
$problems = New-Object System.Collections.Generic.List[string]
function Say($m, $c = 'Gray') { Write-Host "  $m" -ForegroundColor $c }
function Act($what, $ok, $detail) {
    $actions.Add([pscustomobject]@{ action = $what; ok = [bool]$ok; detail = $detail }) | Out-Null
    if ($ok) { Say $what 'Green' } else { Say "$what - $detail" 'Red'; $problems.Add("$what : $detail") | Out-Null }
}
function Ensure-StateRoot {
    if (-not (Test-Path -LiteralPath $StateRoot)) { New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null }
}

Write-Host ""
Write-Host "Horizun in ChatGPT - OpenAI Secure MCP Tunnel" -ForegroundColor Cyan

$tunnel = Get-HorizunTunnelClient -Override $TunnelClientPath
$chatgpt = Get-HorizunChatGptDesktop
$keyPresent = Test-HorizunChatGptSecret -StateRoot $StateRoot

# --- the running client ---------------------------------------------------------
function Get-RunningTunnel {
    if (-not (Test-Path -LiteralPath $pidFile -PathType Leaf)) { return $null }
    $raw = (Get-Content -LiteralPath $pidFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') { return $null }
    $p = Get-Process -Id ([int]$raw) -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    # A recycled process id must not be reported as our tunnel.
    if ($p.ProcessName -notmatch '(?i)tunnel-client') { return $null }
    return $p
}
$running = Get-RunningTunnel

# --- set the key ----------------------------------------------------------------
if ($SetApiKey) {
    Ensure-StateRoot
    Write-Host ""
    Say "The runtime API key for tunnel-client, from $TUNNEL_SETTINGS." 'Cyan'
    Say "It is read without echo, stored with DPAPI for this Windows user only, and" 'Cyan'
    Say "handed to tunnel-client through its environment - never a command line." 'Cyan'
    $secure = Read-Host -Prompt '  CONTROL_PLANE_API_KEY' -AsSecureString
    $plain = ConvertFrom-HorizunSecureString $secure
    if ([string]::IsNullOrWhiteSpace($plain)) { Act 'store the API key' $false 'nothing was entered'; exit 1 }
    if ($plain -notmatch '^sk-') {
        # Named, not blocked: the documented shape is sk-..., and a pasted tunnel
        # id or a truncated key otherwise fails much later with a network error.
        Say "WARNING: that does not look like an sk-... runtime key." 'Yellow'
    }
    Set-HorizunChatGptSecret -StateRoot $StateRoot -Secret $plain
    $plain = $null
    Act 'stored the API key in the Windows credential store (DPAPI, current user)' $true $null
    $keyPresent = $true
}

# --- stop / revoke ---------------------------------------------------------------
if ($Stop -or $Revoke) {
    if ($running) {
        try {
            $running.CloseMainWindow() | Out-Null
            if (-not $running.WaitForExit(5000)) { $running.Kill(); $running.WaitForExit(10000) }
            Act ("stopped tunnel-client (pid {0}); the outbound connection is gone" -f $running.Id) $true $null
        }
        catch { Act 'stop tunnel-client' $false $_.Exception.Message }
    }
    else { Act 'tunnel-client was not running' $true $null }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    $running = $null

    if ($Revoke) {
        if (Remove-HorizunChatGptSecret -StateRoot $StateRoot) { Act 'forgot the stored API key' $true $null }
        else { Act 'there was no stored API key to forget' $true $null }
        if (Test-Path -LiteralPath $profileFile) {
            Remove-Item -LiteralPath $profileFile -Force
            Act 'removed the tunnel-client profile' $true $null
        }
        Say ""
        Say "REVOKING THE TUNNEL ITSELF is a separate act, and only you can do it:" 'Cyan'
        Say "delete the tunnel at $TUNNEL_SETTINGS, and remove the app in ChatGPT." 'Cyan'
        Set-HorizunIntegrationState -Client $CLIENT -State 'pending_user_action' -StatusPath $StatusPath `
            -Detail 'Everything on this machine is stopped and forgotten: no process, no stored key, no profile.' `
            -PendingUserAction ("Delete the tunnel at $TUNNEL_SETTINGS and remove the developer-mode app at $CHATGPT_APPS. " +
                                'Stopping the client here ends the connection; only you can delete the OpenAI-side objects.') | Out-Null
    }
    else {
        Set-HorizunIntegrationState -Client $CLIENT -State 'configured' -StatusPath $StatusPath `
            -Detail 'tunnel-client is stopped. Nothing on this machine is reachable from OpenAI until it is started again.' | Out-Null
    }
    if ($problems.Count -gt 0) { exit 1 }
    exit 0
}

# --- what is in place ------------------------------------------------------------
if ($tunnel.installed) {
    Act ("tunnel-client found: {0}" -f $tunnel.path) $true $null
    Say ("  version   " + $(if ($tunnel.version) { $tunnel.version } else { '<it reported none>' }))
    Say ("  source    {0}" -f $tunnel.source)
    Say ("  sha256    {0}" -f $tunnel.sha256)
    Say ("  signature {0}{1}" -f $tunnel.signature_status,
         $(if ($tunnel.signer) { " ($($tunnel.signer))" } else { '' }))
    # THE FLAG THIS WHOLE INTEGRATION RESTS ON. Horizun's server is stdio; a
    # build with no --mcp-command has nothing to connect to, and finding that
    # out from a failed `run` is finding it out in the worst place.
    if ($tunnel.supports_mcp_command -eq $false) {
        Act 'this tunnel-client supports --mcp-command' $false `
            ("the installed build does not advertise --mcp-command, which is how a stdio server is reached. " +
             "It is too old for this integration; get the current release from $RELEASES.")
    }
    elseif ($null -eq $tunnel.supports_mcp_command) {
        Say "could not read this build's help output, so --mcp-command support is UNKNOWN; -Doctor will settle it." 'Yellow'
    }
    else { Act 'this tunnel-client supports --mcp-command (stdio)' $true $null }
}
else {
    Say "tunnel-client is NOT installed." 'Yellow'
    Say "It is OpenAI's binary, not Horizun's; this script never downloads it."
    Say "Get it from $RELEASES and put it on PATH."
}
Say ("ChatGPT Work desktop app: " + $(if ($chatgpt.installed) { "installed, $($chatgpt.version)" } else { 'not installed' }))
Say ("runtime API key:     " + $(if ($keyPresent) { 'stored (DPAPI, this user)' } else { 'not stored' }))
Say ("tunnel-client:       " + $(if ($running) { "RUNNING, pid $($running.Id)" } else { 'not running' }))
if ($running) {
    # A process that is alive is not a connection that is up. tunnel-client
    # publishes /readyz on its loopback admin surface; when it has lost the
    # outbound path it keeps running and every ChatGPT tool call fails with
    # nothing on this machine looking wrong.
    $health = Test-HorizunTunnelReady -StateRoot $StateRoot
    if ($health.checked) {
        if ($health.ready) { Act ("the tunnel is connected and ready ({0})" -f $health.endpoint) $true $null }
        else {
            Act 'the tunnel is connected' $false `
                ("tunnel-client is running but its readiness endpoint says it is NOT ready ({0}). " -f $health.detail) +
                'Requests through the tunnel fail until it reconnects; run -Doctor, then -Stop and -Start.'
        }
    }
    else { Say "  (its admin endpoint was not reachable, so connectivity is unknown - run -Doctor)" 'DarkGray' }
}

# --- the server it will expose ----------------------------------------------------
$probe = $null
if (Test-Path -LiteralPath $ServerPath -PathType Leaf) {
    $probe = Invoke-HorizunMcpProbe -Command $ServerPath -ListTools -TimeoutSec 120
    if ($probe.ok) {
        Act ("the server tunnel-client would launch answers MCP: {0} tools, list_changed={1}" -f $probe.tool_count, $probe.list_changed) $true $null
    }
    else { Act 'the server answers MCP' $false $probe.problem }
}
else { Act 'find the installed server' $false "$ServerPath does not exist - install Horizun Revit MCP first" }

# --- init -------------------------------------------------------------------------
if ($Init) {
    Write-Host ""
    Write-Host "Creating the tunnel-client profile" -ForegroundColor Cyan
    if (-not $tunnel.installed) { Act 'run tunnel-client init' $false 'tunnel-client is not installed'; exit 2 }
    if (-not $TunnelId) { Act 'run tunnel-client init' $false "-TunnelId is required; create the tunnel at $TUNNEL_SETTINGS and pass its id"; exit 2 }
    if ($TunnelId -notmatch '^tunnel_[0-9a-f]{32}$') {
        Act 'run tunnel-client init' $false "'$TunnelId' is not the documented tunnel id shape (tunnel_ followed by 32 hex characters)"
        exit 2
    }
    if (-not $keyPresent) { Act 'run tunnel-client init' $false 'no API key is stored; run this script with -SetApiKey first'; exit 2 }
    Ensure-StateRoot

    # The profile is written by tunnel-client's own `init`, from its own sample.
    # Hand-writing that YAML would be guessing at a format this product does not
    # own, and a guess that parses today breaks on the client's next release.
    $args = @('init',
              '--sample', 'sample_mcp_stdio_local',
              '--profile', $PROFILE_NAME,
              '--tunnel-id', $TunnelId,
              '--mcp-command', $ServerPath)
    $r = Invoke-HorizunTunnelClient -Path $tunnel.path -Arguments $args -StateRoot $StateRoot
    if ($r.exit_code -eq 0) { Act "tunnel-client init wrote the '$PROFILE_NAME' profile for $ServerPath" $true $null }
    else { Act 'tunnel-client init' $false ("exit {0}: {1}" -f $r.exit_code, $r.output) }
}

# --- doctor -------------------------------------------------------------------------
if ($Doctor) {
    Write-Host ""
    Write-Host "tunnel-client doctor" -ForegroundColor Cyan
    if (-not $tunnel.installed) { Act 'run tunnel-client doctor' $false 'tunnel-client is not installed'; exit 2 }
    $r = Invoke-HorizunTunnelClient -Path $tunnel.path -Arguments @('doctor', '--profile', $PROFILE_NAME, '--explain') -StateRoot $StateRoot
    Write-Host $r.output
    if ($r.exit_code -eq 0) { Act 'tunnel-client doctor reported the profile healthy' $true $null }
    else { Act 'tunnel-client doctor' $false ("exit {0}" -f $r.exit_code) }
}

# --- start -------------------------------------------------------------------------
if ($Start) {
    Write-Host ""
    if (-not $IUnderstandTrafficLeavesThisMachine) {
        Write-Host "  REFUSED, and here is the decision this needs from you." -ForegroundColor Yellow
        Say "Starting the tunnel makes this Revit reachable from ChatGPT. Every MCP request" 'Yellow'
        Say "and reply travels through OpenAI-hosted infrastructure - the model names, the" 'Yellow'
        Say "element data, the audit findings, everything a tool returns. The server stays" 'Yellow'
        Say "private and no port is opened; the traffic still leaves this machine." 'Yellow'
        Say "Re-run with -IUnderstandTrafficLeavesThisMachine if that is what you want." 'Yellow'
        Set-HorizunIntegrationState -Client $CLIENT -State 'pending_user_action' -StatusPath $StatusPath `
            -Detail 'Everything is prepared. The tunnel was not started: enabling it sends MCP traffic through OpenAI-hosted infrastructure and that is a decision for the owner of the machine.' `
            -PendingUserAction 'Re-run with -Start -IUnderstandTrafficLeavesThisMachine to accept that MCP traffic for this Revit passes through OpenAI services.' | Out-Null
        exit 3
    }
    if (-not $tunnel.installed) { Act 'start tunnel-client' $false 'tunnel-client is not installed'; exit 2 }
    if (-not $keyPresent) { Act 'start tunnel-client' $false 'no API key is stored; run with -SetApiKey first'; exit 2 }
    if ($running) { Act ("tunnel-client is already running (pid {0})" -f $running.Id) $true $null }
    else {
        Ensure-StateRoot
        $secret = Get-HorizunChatGptSecret -StateRoot $StateRoot
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $tunnel.path
        foreach ($a in @('run', '--profile', $PROFILE_NAME)) { $null = $psi.ArgumentList.Add($a) }
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = $StateRoot
        # THE KEY GOES HERE AND NOWHERE ELSE. Not in ArgumentList, which every
        # process on this machine can read off our command line.
        $psi.Environment['CONTROL_PLANE_API_KEY'] = $secret
        $secret = $null
        $p = [Diagnostics.Process]::Start($psi)
        Set-Content -LiteralPath $pidFile -Value ([string]$p.Id) -Encoding ASCII
        Start-Sleep -Milliseconds 1500
        if ($p.HasExited) { Act 'start tunnel-client' $false ("it exited immediately with code {0}; run -Doctor" -f $p.ExitCode) }
        else {
            $running = $p
            Act ("started tunnel-client, pid {0}" -f $p.Id) $true $null
        }
    }
}

# --- state ---------------------------------------------------------------------------
Write-Host ""
$state = $null; $detail = $null; $pending = $null
$evidence = [ordered]@{
    transport             = 'OpenAI Secure MCP Tunnel, stdio to the local server (tunnel-client --mcp-command)'
    adapter_written       = $false
    local_listener_opened = $false
    tunnel_client_path    = $tunnel.path
    tunnel_client_version = $tunnel.version
    chatgpt_desktop       = $chatgpt.installed
    chatgpt_desktop_version = $chatgpt.version
    api_key_stored        = $keyPresent
    api_key_location      = 'DPAPI-protected file under %LOCALAPPDATA%\Horizun\integrations\chatgpt, current user only'
    profile               = $PROFILE_NAME
    server_path           = $ServerPath
    server_answers_mcp    = $(if ($probe) { [bool]$probe.ok } else { $false })
    server_tool_count     = $(if ($probe) { $probe.tool_count } else { 0 })
    tools_list_changed    = $(if ($probe) { $probe.list_changed } else { $null })
    tunnel_running        = [bool]$running
    tunnel_ready          = $(if ($running) { (Test-HorizunTunnelReady -StateRoot $StateRoot).ready } else { $false })
    supports_mcp_command  = $tunnel.supports_mcp_command
    tunnel_client_sha256  = $tunnel.sha256
    tunnel_client_signature = $tunnel.signature_status
    execute_python_granted_by_this = $false
}

if ($problems.Count -gt 0) {
    $state = 'failed'
    $detail = 'The ChatGPT connection did not come up: ' + ($problems -join ' | ')
}
elseif ($running) {
    $state = 'configured'
    $detail = "tunnel-client is running against the '$PROFILE_NAME' profile. The tools appear in ChatGPT once the developer-mode app is pointed at the tunnel."
}
elseif (-not $tunnel.installed) {
    $state = 'pending_user_action'
    $detail = 'The local half is ready: the server answers MCP over stdio, which is exactly what tunnel-client forwards. OpenAI''s tunnel-client is not installed.'
    $pending = "Install tunnel-client from $RELEASES, create a tunnel at $TUNNEL_SETTINGS, then run: scripts/chatgpt-tunnel.ps1 -SetApiKey; -Init -TunnelId tunnel_...; -Start -IUnderstandTrafficLeavesThisMachine"
}
elseif (-not $keyPresent) {
    $state = 'pending_user_action'
    $detail = 'tunnel-client is installed and the server answers MCP. No runtime API key is stored.'
    $pending = "Create a runtime API key at $TUNNEL_SETTINGS and run: scripts/chatgpt-tunnel.ps1 -SetApiKey"
}
else {
    $state = 'pending_user_action'
    $detail = 'tunnel-client and the key are in place, and the server answers MCP. The tunnel is not running.'
    $pending = "Run: scripts/chatgpt-tunnel.ps1 -Start -IUnderstandTrafficLeavesThisMachine - then create the developer-mode app at $CHATGPT_APPS and choose Tunnel under Connection."
}

Set-HorizunIntegrationState -Client $CLIENT -State $state -Detail $detail -PendingUserAction $pending `
    -Evidence ([pscustomobject]$evidence) -StatusPath $StatusPath | Out-Null

Write-Host ("  state: {0}" -f $state) -ForegroundColor $(if ($state -eq 'configured') { 'Green' } elseif ($state -eq 'failed') { 'Red' } else { 'Yellow' })
Say $detail
if ($pending) { Write-Host ""; Say "ONE STEP REMAINS, and it is yours:" 'Cyan'; Say $pending 'Cyan' }
Write-Host ""
Say "Whatever happens here, horizun_execute_python stays refused until the owner of this" 'DarkGray'
Say "machine grants it from inside Revit. Connecting ChatGPT does not grant it." 'DarkGray'

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        generated_utc       = (Get-Date).ToUniversalTime().ToString('o')
        client              = $CLIENT
        state               = $state
        detail              = $detail
        pending_user_action = $pending
        evidence            = [pscustomobject]$evidence
        actions             = $actions
        problems            = $problems
    } | ConvertTo-Json -Depth 10 | Out-File -FilePath $Json -Encoding utf8
    Say "wrote $Json"
}

if ($problems.Count -gt 0) { exit 1 }
if ($state -eq 'pending_user_action') { exit 3 }
exit 0
