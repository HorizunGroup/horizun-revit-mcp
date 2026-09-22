#Requires -Version 5.1
<#
  Connect ChatGPT Work to this machine's Horizun bridge through OpenAI's Secure MCP Tunnel.

  THE MECHANISM (developers.openai.com/api/docs/guides/secure-mcp-tunnels):
  OpenAI's tunnel-client runs on this machine, makes an OUTBOUND HTTPS connection
  to OpenAI, long-polls for MCP work and forwards each request over stdio to the
  exact horizun-mcp.exe that Codex and Claude launch. No adapter, no listener, no
  second transport. ChatGPT reaches it through a developer-mode app whose
  connection is that tunnel.

  WHAT THIS SCRIPT DOES NOT DO:
    - download tunnel-client. It is OpenAI's binary; the user installs the FULL
      client ZIP from the official release page (see chatgpt-tunnel.lib.ps1);
    - create the tunnel, the API key or the ChatGPT app - those are account steps;
    - open a port. Its only listener is tunnel-client's own health endpoint,
      bound to loopback;
    - put the API key on a command line, in a log or in a report. It lives in
      DPAPI for this Windows user and reaches tunnel-client only through the
      child's environment block.

  WHAT IT WILL NOT CLAIM: that ChatGPT reached Revit. A running process, a green
  /readyz and even a recent successful poll of OpenAI are local facts. The call
  from ChatGPT through the tunnel into Revit is verified only by making one.

    chatgpt-tunnel.ps1 -Status
    chatgpt-tunnel.ps1 -Status -TunnelClientPath C:\...\tunnel-client.exe   # remembered once proven
    chatgpt-tunnel.ps1 -SetApiKey                        # prompts, never echoes
    chatgpt-tunnel.ps1 -Init -TunnelId tunnel_...        # -Force to replace an existing profile
    chatgpt-tunnel.ps1 -Doctor
    chatgpt-tunnel.ps1 -Start -IUnderstandTrafficLeavesThisMachine
    chatgpt-tunnel.ps1 -Stop
    chatgpt-tunnel.ps1 -Revoke                           # stop, forget the key and this profile

  Exit codes: 0 connected   1 failed   2 a requested action was blocked by a missing
              prerequisite   3 nothing failed, one user step remains
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
    [switch]$Force,
    [string]$TunnelId,
    [switch]$IUnderstandTrafficLeavesThisMachine,
    [string]$ServerPath,
    [string]$TunnelClientPath,
    [string]$Json,
    # Test seams. -ControlPlaneBaseUrl writes a non-OpenAI control plane into a
    # NEW profile so the full lifecycle can be exercised against a local stand-in;
    # it is never needed for real use.
    [string]$StateRoot,
    [string]$StatusPath,
    [string]$ControlPlaneBaseUrl,
    [int]$StartWaitSec = 20,
    # 0 = derive from the tunnel's effective poll settings (Get-HorizunTunnelFreshness).
    [int]$ConnectWaitSec = 0,
    [int]$FreshSec = 0,
    [int]$StartupGraceSec = -1,
    [int]$ProbeTimeoutSec = 15
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-stdio.lib.ps1')
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')
. (Join-Path $PSScriptRoot 'chatgpt-secret.lib.ps1')
. (Join-Path $PSScriptRoot 'chatgpt-tunnel.lib.ps1')

$CLIENT = 'chatgpt'
$PROFILE_NAME = $script:HorizunTunnelProfileName
$TUNNEL_SETTINGS = 'https://platform.openai.com/settings/organization/tunnels'
$CHATGPT_APPS = 'https://chatgpt.com/plugins'

$StateRoot = Get-HorizunTunnelStateRoot $StateRoot
if (-not $ServerPath) { $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
$profileDir = Get-HorizunTunnelProfileDir -StateRoot $StateRoot
$profilePath = Get-HorizunTunnelProfilePath -StateRoot $StateRoot
$healthUrlFile = Join-Path $StateRoot 'health.url'
$logFile = Join-Path $StateRoot 'logs\tunnel-client.log'
$advice = Get-HorizunTunnelDownloadAdvice

$actions = New-Object System.Collections.Generic.List[object]
$problems = New-Object System.Collections.Generic.List[string]
$blocked = New-Object System.Collections.Generic.List[string]
$evidence = [ordered]@{}
function Say($m, $c = 'Gray') { Write-Host "  $m" -ForegroundColor $c }
function Act($what, $ok, $detail) {
    $actions.Add([pscustomobject]@{ action = $what; ok = [bool]$ok; detail = $detail }) | Out-Null
    if ($ok) { Say $what 'Green' } else { Say "$what - $detail" 'Red'; $problems.Add("$what : $detail") | Out-Null }
}
function Block($what, $why) {
    # A requested action that did not run because something it needs is missing.
    $actions.Add([pscustomobject]@{ action = $what; ok = $false; blocked = $true; detail = $why }) | Out-Null
    Say "$what - NOT RUN: $why" 'Yellow'
    $blocked.Add("$what : $why") | Out-Null
}
function Ensure-Dir([string]$d) { if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null } }

function Finish([string]$state, [string]$detail, [string]$pending, [int]$code) {
    # THE ONE EXIT. Every path - including an early refusal and an unexpected
    # exception - leaves the same durable state and the same report.
    if ($state -eq 'pending_user_action' -and -not $pending) { $pending = 'Run chatgpt-tunnel.ps1 -Status and follow the step it names.' }
    try {
        Set-HorizunIntegrationState -Client $CLIENT -State $state -Detail $detail -PendingUserAction $pending `
            -Evidence ([pscustomobject]$evidence) -StatusPath $StatusPath | Out-Null
    }
    catch { Say "could not record the integration state: $($_.Exception.Message)" 'Red' }
    Write-Host ""
    Write-Host ("  state: {0}" -f $state) -ForegroundColor $(if ($state -eq 'configured') { 'Green' } elseif ($state -eq 'failed') { 'Red' } else { 'Yellow' })
    Say $detail
    if ($pending) { Write-Host ""; Say "NEXT STEP:" 'Cyan'; Say $pending 'Cyan' }
    Write-Host ""
    Say "Whatever happens here, horizun_execute_python stays refused until the owner of this" 'DarkGray'
    Say "machine grants it from inside Revit. Connecting ChatGPT does not grant it." 'DarkGray'
    if ($Json) {
        try {
            $dir = Split-Path -Parent $Json
            if ($dir) { Ensure-Dir $dir }
            [pscustomobject]@{
                generated_utc       = (Get-Date).ToUniversalTime().ToString('o')
                client              = $CLIENT
                state               = $state
                detail              = $detail
                pending_user_action = $pending
                exit_code           = $code
                evidence            = [pscustomobject]$evidence
                actions             = $actions
                problems            = $problems
                blocked             = $blocked
            } | ConvertTo-Json -Depth 12 | Out-File -FilePath $Json -Encoding utf8
            Say "wrote $Json"
        }
        catch { Say "could not write $Json : $($_.Exception.Message)" 'Red' }
    }
    exit $code
}

Write-Host ""
Write-Host "Horizun in ChatGPT - OpenAI Secure MCP Tunnel" -ForegroundColor Cyan

try {
    Ensure-Dir $StateRoot

    # ---- which tunnel-client ------------------------------------------------------
    $tunnel = Get-HorizunTunnelClient -Override $TunnelClientPath -StateRoot $StateRoot -ProbeTimeoutSec $ProbeTimeoutSec
    $evidence.tunnel_client = [ordered]@{
        status = $tunnel.status; path = $tunnel.path; source = $tunnel.source; variant = $tunnel.variant
        version = $tunnel.version; sha256 = $tunnel.sha256; signature = $tunnel.signature_status
        supports_mcp_command = $tunnel.supports_mcp_command; probes = $tunnel.probes; problem = $tunnel.problem
    }
    $evidence.download_asset = $advice.asset
    switch ($tunnel.status) {
        'compatible' {
            Act ("tunnel-client {0} is the full client ({1}, via {2})" -f $tunnel.version, $tunnel.path, $tunnel.source) $true $null
            if ($tunnel.source -ne 'remembered selection') { Save-HorizunTunnelSelection -StateRoot $StateRoot -Client $tunnel }
        }
        'absent' { Say 'tunnel-client is NOT installed.' 'Yellow'; Say $advice.text }
        'explicit_missing' { Say $tunnel.problem 'Yellow' }
        'wrong_variant' { Say $tunnel.problem 'Yellow' }
        default { Say ("tunnel-client could not be used: {0}" -f $tunnel.problem) 'Red' }
    }
    $clientOk = ($tunnel.status -eq 'compatible')
    $clientWhy = if ($clientOk) { $null } else { "tunnel-client is $($tunnel.status): $($tunnel.problem)" }

    $chatgpt = Get-HorizunChatGptDesktop
    $evidence.chatgpt_desktop = [ordered]@{ installed = $chatgpt.installed; version = $chatgpt.version }

    # ---- the key ------------------------------------------------------------------
    $keyPresent = Test-HorizunChatGptSecret -StateRoot $StateRoot
    if ($SetApiKey) {
        Write-Host ""
        Say "The runtime API key for tunnel-client, from $TUNNEL_SETTINGS." 'Cyan'
        Say "It is read without echo, stored with DPAPI for this Windows user only, and" 'Cyan'
        Say "handed to tunnel-client through its environment - never a command line." 'Cyan'
        $secure = Read-Host -Prompt '  CONTROL_PLANE_API_KEY' -AsSecureString
        $plain = ConvertFrom-HorizunSecureString $secure
        if ([string]::IsNullOrWhiteSpace($plain)) { Act 'store the API key' $false 'nothing was entered' }
        else {
            if ($plain -notmatch '^sk-') { Say "WARNING: that does not look like an sk-... runtime key." 'Yellow' }
            Set-HorizunChatGptSecret -StateRoot $StateRoot -Secret $plain | Out-Null
            Act 'stored the API key in the Windows credential store (DPAPI, current user)' $true $null
            $keyPresent = $true
        }
        $plain = $null
    }
    $evidence.api_key_stored = $keyPresent
    $evidence.api_key_location = 'DPAPI-protected file under %LOCALAPPDATA%\Horizun\integrations\chatgpt, current user only'

    # ---- the profile, in ONE directory -------------------------------------------
    $legacy = Get-HorizunTunnelLegacyProfiles -StateRoot $StateRoot
    $evidence.profile_dir = $profileDir
    $evidence.profile_path = $profilePath
    $evidence.legacy_profiles = $legacy
    if ($legacy.Count -gt 0) {
        Say ("An earlier version wrote a '{0}' profile in tunnel-client's default directory: {1}" -f $PROFILE_NAME, ($legacy -join '; ')) 'Yellow'
        Say ("It is not used any more and is left untouched. This integration keeps its profile in {0}." -f $profileDir) 'Yellow'
    }

    # ---- the running tunnel ------------------------------------------------------
    $runtime = Get-HorizunTunnelRuntime -StateRoot $StateRoot
    $evidence.runtime_state = $runtime.state

    # ---- stop / revoke: they only take things away --------------------------------
    if ($Stop -or $Revoke) {
        $s = Stop-HorizunTunnel -StateRoot $StateRoot
        $evidence.stop = $s
        if ($s.stopped) { Act $s.detail $true $null }
        elseif ($s.verified) { Act ("nothing to stop: " + $s.detail) $true $null }
        else { Act 'stop the tunnel-client' $false ("NOT VERIFIED: $($s.detail)") }
        if ($Revoke) {
            if (-not $s.verified) {
                Block 'forget the key and the profile' 'the tunnel-client could not be verified as stopped; forgetting its key while it may still be running would leave it unaccounted for'
            }
            else {
                if (Remove-HorizunChatGptSecret -StateRoot $StateRoot) { Act 'forgot the stored API key' $true $null }
                else { Act 'there was no stored API key to forget' $true $null }
                $keyPresent = $false
                if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
                    Remove-Item -LiteralPath $profilePath -Force
                    if (Test-Path -LiteralPath $profilePath) { Act "remove the profile $profilePath" $false 'it is still there' }
                    else { Act "removed the profile $profilePath" $true $null }
                }
                else { Act "there was no profile at $profilePath" $true $null }
                foreach ($f in @($healthUrlFile, (Join-Path $StateRoot 'tunnel-client.pid'))) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
                if ($legacy.Count -gt 0) { Say ("Left untouched (not this integration's directory): {0}" -f ($legacy -join '; ')) 'Yellow' }
            }
        }
        $evidence.api_key_stored = $keyPresent
        $evidence.runtime_state = (Get-HorizunTunnelRuntime -StateRoot $StateRoot).state
        if ($problems.Count -gt 0 -or $blocked.Count -gt 0) {
            Finish 'failed' ('Stopping was not fully verified: ' + ((@($problems) + @($blocked)) -join ' | ')) $null 1
        }
        if ($Revoke) {
            Finish 'pending_user_action' 'Everything this integration keeps on this machine is stopped and forgotten: no process, no stored key, no profile.' `
                ("Delete the tunnel at $TUNNEL_SETTINGS and remove the developer-mode app at $CHATGPT_APPS. Only you can delete the OpenAI-side objects.") 3
        }
        Finish 'pending_user_action' 'tunnel-client is stopped. Nothing on this machine is reachable from ChatGPT until it is started again.' `
            'Run chatgpt-tunnel.ps1 -Start -IUnderstandTrafficLeavesThisMachine to reconnect.' 3
    }

    # ---- the server it would expose ----------------------------------------------
    $serverOk = $false; $serverWhy = $null
    if (Test-Path -LiteralPath $ServerPath -PathType Leaf) {
        $probe = Invoke-HorizunMcpProbe -Command $ServerPath -ListTools -TimeoutSec 120
        $evidence.server = [ordered]@{ path = $ServerPath; answers_mcp = [bool]$probe.ok; tool_count = $probe.tool_count; problem = $probe.problem }
        if ($probe.ok) { $serverOk = $true; Act ("the server answers MCP: {0} tools" -f $probe.tool_count) $true $null }
        else { $serverWhy = "the server does not answer MCP: $($probe.problem)"; Say $serverWhy 'Red' }
    }
    else {
        $serverWhy = "$ServerPath does not exist - install Horizun Revit MCP first"
        $evidence.server = [ordered]@{ path = $ServerPath; answers_mcp = $false; problem = $serverWhy }
        Say $serverWhy 'Red'
    }

    # ---- init ------------------------------------------------------------------
    $chainBroken = $false
    if ($Init) {
        Write-Host ""; Write-Host "Creating the tunnel-client profile" -ForegroundColor Cyan
        $why = $null
        if (-not $clientOk) { $why = $clientWhy }
        elseif (-not $TunnelId) { $why = "-TunnelId is required; create the tunnel at $TUNNEL_SETTINGS and pass its id" }
        elseif ($TunnelId -notmatch '^tunnel_[0-9a-f]{32}$') { $why = "'$TunnelId' is not the documented tunnel id shape (tunnel_ followed by 32 hex characters)" }
        elseif (-not $serverOk) { $why = $serverWhy }
        elseif ((Test-Path -LiteralPath $profilePath) -and -not $Force) { $why = "a profile already exists at $profilePath; re-run with -Force to replace it" }
        $mcpCommand = $null
        if (-not $why) { try { $mcpCommand = ConvertTo-HorizunTunnelMcpCommand -ServerPath $ServerPath } catch { $why = $_.Exception.Message } }
        if ($why) { Block 'tunnel-client init' $why; $chainBroken = $true }
        else {
            Ensure-Dir $profileDir
            $a = @('init', '--sample', 'sample_mcp_stdio_local', '--profile', $PROFILE_NAME, '--profile-dir', $profileDir,
                   '--tunnel-id', $TunnelId, '--health-listen-addr', '127.0.0.1:0', '--mcp-command', $mcpCommand)
            if ($ControlPlaneBaseUrl) { $a += @('--control-plane-base-url', $ControlPlaneBaseUrl) }
            if ($Force) { $a += '--force' }
            $r = Invoke-HorizunTunnelClient -Path $tunnel.path -Arguments $a -StateRoot $StateRoot -TimeoutSec 60 -WithoutKey
            $evidence.init = [ordered]@{ exit_code = $r.exit_code; timed_out = $r.timed_out; output = $r.output }
            $written = Test-Path -LiteralPath $profilePath -PathType Leaf
            $forward = $ServerPath.Replace([char]92, [char]47)
            $yamlText = if ($written) { Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 } else { '' }
            $holdsCommand = $written -and $yamlText.Contains($forward)
            $loopbackOnly = $yamlText -match 'listen_addr:\s*"?127\.0\.0\.1:0"?'
            if ($r.exit_code -eq 0 -and $written -and $holdsCommand -and -not $loopbackOnly) {
                Act 'tunnel-client init' $false "the profile at $profilePath does not bind its health listener to 127.0.0.1:0"; $chainBroken = $true
            }
            elseif ($r.exit_code -eq 0 -and $written -and $holdsCommand) { Act "tunnel-client init wrote $profilePath for $ServerPath (health on 127.0.0.1:0)" $true $null }
            elseif ($r.exit_code -eq 0) { Act 'tunnel-client init' $false "it reported success but $profilePath does not hold the server command"; $chainBroken = $true }
            else { Act 'tunnel-client init' $false ("exit {0}{1}: {2}" -f $r.exit_code, $(if ($r.timed_out) { ' (timed out)' } else { '' }), $r.output); $chainBroken = $true }
        }
    }
    $profileOk = Test-Path -LiteralPath $profilePath -PathType Leaf
    $evidence.profile_exists = $profileOk

    # ---- doctor ------------------------------------------------------------------
    if ($Doctor) {
        Write-Host ""; Write-Host "tunnel-client doctor" -ForegroundColor Cyan
        $why = $null
        if ($chainBroken) { $why = 'an earlier requested step failed' }
        elseif (-not $clientOk) { $why = $clientWhy }
        elseif (-not $profileOk) { $why = "no profile at $profilePath; run -Init -TunnelId tunnel_... first" }
        elseif (-not $keyPresent) { $why = 'no API key is stored; run -SetApiKey first' }
        if ($why) { Block 'tunnel-client doctor' $why; $chainBroken = $true }
        else {
            $r = Invoke-HorizunTunnelClient -Path $tunnel.path -Arguments @('doctor', '--profile', $PROFILE_NAME, '--profile-dir', $profileDir, '--explain') -StateRoot $StateRoot -TimeoutSec 120
            $evidence.doctor = [ordered]@{ exit_code = $r.exit_code; timed_out = $r.timed_out; output = $r.output }
            Write-Host $r.output
            if ($r.exit_code -eq 0) { Act 'tunnel-client doctor reported the profile healthy' $true $null }
            else { Act 'tunnel-client doctor' $false ("exit {0}{1}" -f $r.exit_code, $(if ($r.timed_out) { ' (timed out)' } else { '' })); $chainBroken = $true }
        }
    }

    # ---- start -------------------------------------------------------------------
    if ($Start) {
        Write-Host ""
        if (-not $IUnderstandTrafficLeavesThisMachine) {
            Write-Host "  REFUSED, and here is the decision this needs from you." -ForegroundColor Yellow
            Say "Starting the tunnel makes this Revit reachable from ChatGPT. Every MCP request" 'Yellow'
            Say "and reply travels through OpenAI-hosted infrastructure - the model names, the" 'Yellow'
            Say "element data, the audit findings, everything a tool returns. The server stays" 'Yellow'
            Say "private and no port is opened; the traffic still leaves this machine." 'Yellow'
            Say "Re-run with -IUnderstandTrafficLeavesThisMachine if that is what you want." 'Yellow'
            Finish 'pending_user_action' 'The tunnel was not started: enabling it sends MCP traffic through OpenAI-hosted infrastructure and that is a decision for the owner of the machine.' `
                'Re-run with -Start -IUnderstandTrafficLeavesThisMachine to accept that MCP traffic for this Revit passes through OpenAI services.' 3
        }
        $why = $null
        if ($chainBroken) { $why = 'an earlier requested step failed' }
        elseif (-not $clientOk) { $why = $clientWhy }
        elseif (-not $keyPresent) { $why = 'no API key is stored; run -SetApiKey first' }
        elseif (-not $profileOk) { $why = "no profile at $profilePath; run -Init -TunnelId tunnel_... first" }
        elseif (-not $serverOk) { $why = $serverWhy }
        elseif ($runtime.state -eq 'unreadable') { $why = $runtime.detail + ' Delete it only after making sure no tunnel-client from an earlier start is still running.' }
        elseif ($runtime.state -eq 'legacy') {
            $legacyPid = $null
            try { $legacyPid = [int]((Get-Content -LiteralPath (Join-Path $StateRoot 'tunnel-client.pid') -Raw).Trim()) } catch { }
            $lp = if ($legacyPid) { Get-Process -Id $legacyPid -ErrorAction SilentlyContinue } else { $null }
            if ($lp -and $lp.ProcessName -match '(?i)tunnel-client') {
                $why = "a tunnel-client (pid $legacyPid) started by an earlier version may still be running, and a tunnel with a stdio server supports ONE active client. Close the window it runs in, then start again."
            }
            else { Remove-Item -LiteralPath (Join-Path $StateRoot 'tunnel-client.pid') -Force -ErrorAction SilentlyContinue }
        }
        if ($why) { Block 'start tunnel-client' $why; $chainBroken = $true }
        elseif ($runtime.state -eq 'running') { Act ("tunnel-client is already running (pid {0})" -f $runtime.record.pid) $true $null }
        else {
            Ensure-Dir (Split-Path -Parent $logFile)
            Remove-Item -LiteralPath $healthUrlFile -Force -ErrorAction SilentlyContinue
            $secret = Get-HorizunChatGptSecret -StateRoot $StateRoot
            # The timing this process will poll with, recorded so every later
            # -Status judges it by the same numbers.
            $pollSettings = Get-HorizunTunnelPollSettings -ProfilePath $profilePath
            # --health.listen-addr repeats the profile's 127.0.0.1:0 as a flag, which
            # outranks the profile: the admin surface is loopback even if someone
            # edits the YAML. Get-HorizunTunnelConnection then checks where it REALLY
            # listens. Start-HorizunTunnelProcess documents how the key is delivered.
            $p = $null
            try {
                $p = Start-HorizunTunnelProcess -Executable $tunnel.path -WorkingDirectory $StateRoot -Secret $secret -Arguments @(
                    'run', '--profile', $PROFILE_NAME, '--profile-dir', $profileDir,
                    '--health.listen-addr', '127.0.0.1:0', '--health.url-file', $healthUrlFile, '--log.file', $logFile)
            }
            finally { $secret = $null }
            $deadline = (Get-Date).AddSeconds([Math]::Max(1, $StartWaitSec))
            while ((Get-Date) -lt $deadline -and -not $p.HasExited -and -not (Test-Path -LiteralPath $healthUrlFile -PathType Leaf)) { Start-Sleep -Milliseconds 250 }
            if ($p.HasExited) {
                $tail = if (Test-Path -LiteralPath $logFile) { ((Get-Content -LiteralPath $logFile -Tail 5 -ErrorAction SilentlyContinue) -join ' ') } else { '' }
                $tail = [regex]::Replace($tail, 'sk-[A-Za-z0-9_\-]{8,}', '<redacted>')
                Act 'start tunnel-client' $false ("it exited with code {0}. Last log lines: {1}" -f $p.ExitCode, $tail)
                $chainBroken = $true
            }
            else {
                Save-HorizunTunnelRuntime -StateRoot $StateRoot -Process $p -Executable $tunnel.path -HealthUrlFile $healthUrlFile -LogFile $logFile -PollSettings $pollSettings | Out-Null
                Act ("started tunnel-client, pid {0}, detached from this console; log: {1}" -f $p.Id, $logFile) $true $null
                # Give its first long poll the chance to complete before judging - for
                # the startup grace its own settings give, unless told otherwise.
                $wait = if ($ConnectWaitSec -gt 0) { $ConnectWaitSec } else { (Get-HorizunTunnelFreshness -Settings $pollSettings).startup_grace_seconds }
                $until = (Get-Date).AddSeconds($wait)
                do {
                    $c = Get-HorizunTunnelConnection -StateRoot $StateRoot -FreshSec $FreshSec -StartupGraceSec $StartupGraceSec
                    if ($c.connected -or $c.process -ne 'running' -or $c.control_plane.state -eq 'stale') { break }
                    Start-Sleep -Seconds 2
                } while ((Get-Date) -lt $until)
            }
        }
    }

    # ---- what is true now ----------------------------------------------------------
    $runtime = Get-HorizunTunnelRuntime -StateRoot $StateRoot
    $conn = Get-HorizunTunnelConnection -StateRoot $StateRoot -Runtime $runtime -FreshSec $FreshSec -StartupGraceSec $StartupGraceSec
    $evidence.runtime_state = $runtime.state
    $evidence.connection = $conn
    $evidence.execute_python_granted_by_this = $false

    Write-Host ""
    Say ("tunnel-client process : {0}" -f $runtime.state)
    Say ("local health (/readyz): {0}" -f $(if ($conn.local_health.checked) { if ($conn.local_health.ok) { 'ok' } else { "NOT ok ($($conn.local_health.detail))" } } else { "not checked ($($conn.local_health.detail))" }))
    Say ("contact with OpenAI   : {0} - {1}" -f $conn.control_plane.state, $conn.control_plane.detail)
    Say ("ChatGPT -> Revit call : not verified here; make one tool call from ChatGPT to prove it")

    if ($problems.Count -gt 0) { Finish 'failed' ('The ChatGPT connection did not come up: ' + ($problems -join ' | ')) $null 1 }
    if ($blocked.Count -gt 0) {
        $pend = if ($tunnel.status -in @('absent', 'wrong_variant', 'explicit_missing')) { $advice.text + ' Then run chatgpt-tunnel.ps1 -Status -TunnelClientPath <that tunnel-client.exe>.' } else { $null }
        if ($pend) { Finish 'pending_user_action' ('A requested step was not run: ' + ($blocked -join ' | ')) $pend 2 }
        Finish 'failed' ('A requested step was not run: ' + ($blocked -join ' | ')) $null 2
    }
    if ($runtime.state -eq 'running') {
        if ($conn.connected) {
            Finish 'configured' ("tunnel-client is running, its loopback health is ok and its last successful poll of OpenAI was {0} s ago. This is the local half; a tool call from ChatGPT that reaches Revit has not been verified by this machine." -f $conn.control_plane.age_seconds) $null 0
        }
        if ($conn.control_plane.state -eq 'connecting' -and $conn.local_health.ok) {
            Finish 'pending_user_action' ("tunnel-client is running and still inside its startup grace: {0}" -f $conn.control_plane.detail) `
                'Wait a minute and run chatgpt-tunnel.ps1 -Status again.' 3
        }
        Finish 'failed' ("tunnel-client is running but is NOT connected: local health: {0}; contact with OpenAI: {1} ({2}). Check the key, the tunnel id and the network, then -Stop and -Start. Log: {3}" -f $conn.local_health.detail, $conn.control_plane.state, $conn.control_plane.detail, $logFile) $null 1
    }
    if ($runtime.state -in @('unreadable', 'legacy')) {
        Finish 'failed' ($runtime.detail) $null 1
    }
    switch ($tunnel.status) {
        'absent'           { Finish 'pending_user_action' 'OpenAI''s full tunnel-client is not installed.' ($advice.text + ' Then run chatgpt-tunnel.ps1 -Status -TunnelClientPath <that tunnel-client.exe>.') 3 }
        'explicit_missing' { Finish 'pending_user_action' $tunnel.problem ('Point -TunnelClientPath at the tunnel-client.exe from the full ZIP. ' + $advice.text) 3 }
        'wrong_variant'    { Finish 'pending_user_action' $tunnel.problem ($advice.text + ' Then run chatgpt-tunnel.ps1 -Status -TunnelClientPath <that tunnel-client.exe>.') 3 }
        'compatible'       { }
        default            { Finish 'failed' ("tunnel-client could not be used: " + $tunnel.problem) $null 1 }
    }
    if (-not $serverOk) { Finish 'failed' $serverWhy $null 1 }
    if (-not $keyPresent) { Finish 'pending_user_action' 'tunnel-client is the full client and the server answers MCP. No runtime API key is stored.' "Create a runtime API key at $TUNNEL_SETTINGS and run: chatgpt-tunnel.ps1 -SetApiKey" 3 }
    if (-not $profileOk) { Finish 'pending_user_action' 'The key is stored. No tunnel-client profile exists for this machine yet.' "Create a tunnel at $TUNNEL_SETTINGS and run: chatgpt-tunnel.ps1 -Init -TunnelId tunnel_..." 3 }
    Finish 'pending_user_action' 'tunnel-client, the key and the profile are in place, and the server answers MCP. The tunnel is not running.' `
        "Run: chatgpt-tunnel.ps1 -Start -IUnderstandTrafficLeavesThisMachine - then, in ChatGPT, create the developer-mode app at $CHATGPT_APPS with Tunnel as its connection, and make one tool call to prove the whole path." 3
}
catch {
    $problems.Add("unexpected error: $($_.Exception.Message)") | Out-Null
    Finish 'failed' ("The helper stopped on an unexpected error: " + $_.Exception.Message) $null 1
}
