#Requires -Version 5.1
<#
  OpenAI Secure MCP Tunnel, as the OFFICIAL tunnel-client actually behaves.

  Every rule below was measured against tunnel-client 0.0.14 (the full client and
  the runtime-cloudflared variant, SHA-256 checked against OpenAI's SHA256SUMS and
  its SLSA attestation) before it was written. Where a rule looks fussy, the
  comment names what went wrong without it.

    1. THE PACKAGE. OpenAI publishes three Windows variants. Only the full
       client (tunnel-client-v<version>-windows-<arch>.zip) has init/doctor and
       --mcp-command. tunnel-client-runtime*.exe answer only `run`, and renaming
       one does not change what it can do - its own --version says
       `flavor=runtime-cloudflared` whatever the file is called. So the variant is
       read from the executable's answers, never from its name.

    2. THE COMMAND LINE, TWICE. --mcp-command is split into argv twice: by the
       Windows C runtime when tunnel-client starts, and again by tunnel-client's
       own POSIX-shell-like parser, where `\` is an escape and `'` opens a quote.
       C:\Windows\System32\cmd.exe arrives as C:WindowsSystem32cmd.exe, and a
       path through C:\Projects\Ana O'Neil\ does not parse at all. What survives
       both layers is forward slashes inside literal double quotes.

    3. ONE PROFILE DIRECTORY. The profile lives where --profile-dir says, and
       every call - init, doctor, run, revoke - passes the same one.

    4. A RUNNING PROCESS IS NOT A CONNECTION, and /readyz is not one either:
       measured, it stays 200 while every poll fails. What proves the tunnel is
       talking to OpenAI is commands_poll_last_successful_timestamp_seconds on its
       loopback /metrics, and only while it is recent.
#>

. (Join-Path $PSScriptRoot 'process.lib.ps1')

$script:HorizunTunnelProfileName = 'horizun-revit'
$script:HorizunTunnelReleases = 'https://github.com/openai/tunnel-client/releases/latest'
# How recent a successful poll must be is DERIVED from the client's effective
# poll settings (Get-HorizunTunnelFreshness); 90 s is what the defaults give.

# ---------------------------------------------------------------------------
# The package
# ---------------------------------------------------------------------------

function Get-HorizunWindowsArchitecture {
    <# 'arm64' or 'amd64' for the MACHINE, not the (possibly emulated) process. #>
    try {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        if ($arch -eq 'Arm64') { return 'arm64' }
        if ($arch -eq 'X64') { return 'amd64' }
    }
    catch { }
    $native = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    if ($native -eq 'ARM64') { return 'arm64' }
    return 'amd64'
}

function Get-HorizunTunnelDownloadAdvice {
    <# The exact package to take from OpenAI's release page, for THIS machine. #>
    $arch = Get-HorizunWindowsArchitecture
    return [pscustomobject]@{
        page      = $script:HorizunTunnelReleases
        asset     = "tunnel-client-v<version>-windows-$arch.zip"
        not_this  = "tunnel-client-runtime-v<version>-windows-$arch.zip and tunnel-client-runtime-cloudflared-v<version>-windows-$arch.zip"
        text      = ("From $($script:HorizunTunnelReleases) download tunnel-client-v<version>-windows-$arch.zip - the FULL client, " +
                     "whose name has no 'runtime' in it. Extract the whole ZIP into one folder and keep every file together " +
                     "(tunnel-client.exe runs cloudflared.exe from beside it). Do not rename a runtime executable: it keeps " +
                     "its limited commands whatever it is called.")
    }
}

# ---------------------------------------------------------------------------
# The command line tunnel-client stores and parses
# ---------------------------------------------------------------------------

function ConvertFrom-HorizunTunnelCommandLine {
    <#
      Port of tunnel-client's parseCommandArgv (pkg/runtimeconfig), used to prove a
      serialized command survives before it is handed over. Outside quotes and
      inside double quotes a backslash escapes the next character; single quotes
      are literal; whitespace separates arguments.
    #>
    param([AllowEmptyString()][string]$Line)
    $text = ([string]$Line).Trim()
    if ($text -eq '') { throw 'command is empty' }
    $parts = New-Object System.Collections.Generic.List[string]
    $sb = New-Object System.Text.StringBuilder
    $inSingle = $false; $inDouble = $false; $escaped = $false
    foreach ($ch in $text.ToCharArray()) {
        if ($escaped) { [void]$sb.Append($ch); $escaped = $false; continue }
        if ($inSingle) {
            if ($ch -eq [char]39) { $inSingle = $false } else { [void]$sb.Append($ch) }
            continue
        }
        if ($inDouble) {
            if ($ch -eq [char]92) { $escaped = $true }
            elseif ($ch -eq [char]34) { $inDouble = $false }
            else { [void]$sb.Append($ch) }
            continue
        }
        if ($ch -eq [char]92) { $escaped = $true }
        elseif ($ch -eq [char]39) { $inSingle = $true }
        elseif ($ch -eq [char]34) { $inDouble = $true }
        elseif ($ch -eq ' ' -or $ch -eq "`t" -or $ch -eq "`n" -or $ch -eq "`r") {
            if ($sb.Length -gt 0) { $parts.Add($sb.ToString()); [void]$sb.Clear() }
        }
        else { [void]$sb.Append($ch) }
    }
    if ($escaped) { throw 'unterminated escape sequence' }
    if ($inSingle -or $inDouble) { throw 'unterminated quoted string' }
    if ($sb.Length -gt 0) { $parts.Add($sb.ToString()) }
    if ($parts.Count -eq 0) { throw 'command is empty' }
    return ,$parts.ToArray()
}

function ConvertTo-HorizunTunnelMcpCommand {
    <#
      The --mcp-command VALUE for one executable with no arguments: forward
      slashes inside literal double quotes. Verified by parsing it back exactly
      as tunnel-client will; anything that does not come back as the same single
      path is refused here rather than discovered by the client.
    #>
    param([Parameter(Mandatory = $true)][string]$ServerPath)
    if (-not [IO.Path]::IsPathRooted($ServerPath)) { throw "the server path must be absolute: $ServerPath" }
    if ($ServerPath.IndexOf([char]34) -ge 0) { throw "the server path contains a double quote, which no Windows path can: $ServerPath" }
    $forward = $ServerPath.Replace([char]92, [char]47)
    $value = [string][char]34 + $forward + [string][char]34
    $back = ConvertFrom-HorizunTunnelCommandLine $value
    if ($back.Count -ne 1 -or $back[0] -cne $forward) {
        throw ("the server path does not survive tunnel-client's command parser: {0}" -f $ServerPath)
    }
    return $value
}

# ---------------------------------------------------------------------------
# Where things live
# ---------------------------------------------------------------------------

function Get-HorizunTunnelStateRoot {
    param([string]$StateRoot)
    if ($StateRoot) { return $StateRoot }
    return (Join-Path $env:LOCALAPPDATA 'Horizun\integrations\chatgpt')
}

function Get-HorizunTunnelProfileDir {
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    return (Join-Path $StateRoot 'profiles')
}

function Get-HorizunTunnelProfilePath {
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    return (Join-Path (Get-HorizunTunnelProfileDir -StateRoot $StateRoot) ($script:HorizunTunnelProfileName + '.yaml'))
}

function Get-HorizunTunnelLegacyProfiles {
    <#
      Where a profile named horizun-revit could have been written by 2.0.1 and
      earlier, which never passed --profile-dir: the client's own default
      directory. Reported so a person can see it; never deleted, because that
      directory is the client's and may hold other profiles of their own.
    #>
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    $ours = [IO.Path]::GetFullPath((Get-HorizunTunnelProfileDir -StateRoot $StateRoot))
    $dirs = New-Object System.Collections.Generic.List[string]
    if ($env:TUNNEL_CLIENT_PROFILE_DIR) { $dirs.Add($env:TUNNEL_CLIENT_PROFILE_DIR) }
    if ($env:XDG_CONFIG_HOME) { $dirs.Add((Join-Path $env:XDG_CONFIG_HOME 'tunnel-client')) }
    $userHome = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
    if ($userHome) { $dirs.Add((Join-Path $userHome '.config\tunnel-client')) }
    $found = @()
    foreach ($d in ($dirs | Select-Object -Unique)) {
        try { $full = [IO.Path]::GetFullPath($d) } catch { continue }
        if ($full -eq $ours) { continue }
        $f = Join-Path $full ($script:HorizunTunnelProfileName + '.yaml')
        if (Test-Path -LiteralPath $f -PathType Leaf) { $found += $f }
    }
    return ,$found
}

function Get-HorizunTunnelSelectionPath {
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    return (Join-Path $StateRoot 'tunnel-client.selection.json')
}

function Save-HorizunTunnelSelection {
    <# Remember a client that was PROVEN compatible, so later calls use the same one. #>
    param([Parameter(Mandatory = $true)][string]$StateRoot, [Parameter(Mandatory = $true)]$Client)
    if (-not (Test-Path -LiteralPath $StateRoot)) { New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null }
    $doc = [pscustomobject]@{
        path         = $Client.path
        sha256       = $Client.sha256
        version      = $Client.version
        recorded_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $path = Get-HorizunTunnelSelectionPath -StateRoot $StateRoot
    $tmp = "$path.tmp-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $tmp -Value ($doc | ConvertTo-Json) -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Get-HorizunTunnelSelection {
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    $path = Get-HorizunTunnelSelectionPath -StateRoot $StateRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { return $null }
    if (-not $doc -or -not $doc.path) { return $null }
    return $doc
}

# ---------------------------------------------------------------------------
# Which client, and what it can do - by evidence
# ---------------------------------------------------------------------------

function Get-HorizunTunnelClient {
    <#
      Find OpenAI's tunnel-client and establish, from its own answers, whether it
      is the full client this integration needs.

      status, one of:
        absent            nothing found anywhere
        explicit_missing  a path chosen explicitly (-Override, HORIZUN_TUNNEL_CLIENT
                          or a remembered selection) does not exist. It is NEVER
                          silently replaced by another executable.
        wrong_variant     a runtime-only build (it said so, or refused `init`)
        failed            it would not start, or did not answer in time
        unverified        it answered, but not in a way that proves compatibility
        compatible        `init --help` succeeded and documents --mcp-command

      Nothing here is downloaded, and nothing is run except --version and
      `init --help`, each with stdin closed and a deadline.
    #>
    [CmdletBinding()]
    param([string]$Override, [string]$StateRoot, [int]$ProbeTimeoutSec = 15)

    $advice = Get-HorizunTunnelDownloadAdvice
    $info = [ordered]@{
        client               = 'chatgpt-tunnel'
        status               = 'absent'
        installed            = $false
        path                 = $null
        source               = $null
        variant              = $null
        version              = $null
        sha256               = $null
        signature_status     = $null
        signer               = $null
        file_version         = $null
        supports_mcp_command = $null
        probes               = @()
        problem              = $null
        download_from        = $advice.page
        download_asset       = $advice.asset
    }

    # --- which file ----------------------------------------------------------
    $chosen = $null
    if ($Override) { $chosen = @{ path = $Override; source = 'explicit' } }
    elseif ($env:HORIZUN_TUNNEL_CLIENT) { $chosen = @{ path = $env:HORIZUN_TUNNEL_CLIENT; source = 'HORIZUN_TUNNEL_CLIENT' } }
    elseif ($StateRoot) {
        $sel = Get-HorizunTunnelSelection -StateRoot $StateRoot
        if ($sel) { $chosen = @{ path = [string]$sel.path; source = 'remembered selection' } }
    }
    if ($chosen) {
        $info.path = $chosen.path
        $info.source = $chosen.source
        if (-not (Test-Path -LiteralPath $chosen.path -PathType Leaf)) {
            $info.status = 'explicit_missing'
            $info.problem = ("the tunnel-client chosen by {0} does not exist: {1}. Nothing else was substituted for it." -f $chosen.source, $chosen.path)
            return [pscustomobject]$info
        }
    }
    else {
        $candidates = New-Object System.Collections.Generic.List[object]
        $onPath = Get-Command 'tunnel-client' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($onPath) { $candidates.Add(@{ path = $onPath.Source; source = 'PATH' }) }
        foreach ($guess in @(
            (Join-Path $env:LOCALAPPDATA 'Programs\tunnel-client\tunnel-client.exe'),
            (Join-Path $env:LOCALAPPDATA 'Horizun\integrations\chatgpt\tunnel-client\tunnel-client.exe'),
            (Join-Path $env:LOCALAPPDATA 'Horizun\integrations\chatgpt\tunnel-client.exe'),
            (Join-Path $env:USERPROFILE '.local\bin\tunnel-client.exe'))) {
            $candidates.Add(@{ path = $guess; source = 'well-known location' })
        }
        foreach ($c in $candidates) {
            if ($c.path -and (Test-Path -LiteralPath $c.path -PathType Leaf)) { $chosen = $c; break }
        }
        if (-not $chosen) {
            $info.problem = 'tunnel-client was not found. ' + $advice.text
            return [pscustomobject]$info
        }
        $info.source = $chosen.source
    }
    $info.installed = $true
    $info.path = (Resolve-Path -LiteralPath $chosen.path).Path

    # --- provenance, as far as a file carries it ------------------------------
    try {
        $item = Get-Item -LiteralPath $info.path
        $info.file_version = $item.VersionInfo.FileVersion
        $info.sha256 = (Get-FileHash -LiteralPath $info.path -Algorithm SHA256).Hash.ToLower()
        $sig = Get-AuthenticodeSignature -LiteralPath $info.path
        $info.signature_status = [string]$sig.Status
        if ($sig.SignerCertificate) { $info.signer = $sig.SignerCertificate.Subject }
    }
    catch { }

    $excerpt = { param($r) $t = (($r.stdout + "`n" + $r.stderr) -replace '\s+', ' ').Trim(); if ($t.Length -gt 240) { $t.Substring(0, 240) + ' ...' } else { $t } }
    $record = {
        param($r)
        [pscustomobject]@{
            arguments = ($r.arguments -join ' ')
            started   = $r.started
            exit_code = $r.exit_code
            timed_out = $r.timed_out
            error     = $r.error
            excerpt   = (& $excerpt $r)
        }
    }

    # --- version: the official flag is --version (`version` is not a command) --
    $v = Invoke-HorizunProcess -Path $info.path -Arguments @('--version') -TimeoutSec $ProbeTimeoutSec
    $info.probes += (& $record $v)
    if (-not $v.started) {
        $info.status = 'failed'
        $info.problem = "tunnel-client would not start: $($v.error). If Windows blocked it (SmartScreen, antivirus or application control), unblock the file you downloaded from OpenAI."
        return [pscustomobject]$info
    }
    if ($v.timed_out) {
        $info.status = 'failed'
        $info.problem = "tunnel-client did not answer --version within $ProbeTimeoutSec seconds."
        return [pscustomobject]$info
    }
    $versionText = ($v.stdout + "`n" + $v.stderr).Trim()
    if ($v.exit_code -eq 0 -and $versionText) {
        $info.version = (($versionText -split '\s+')[0]).Trim()
        if ($versionText -match 'flavor=(runtime[\w-]*)') { $info.variant = $Matches[1] }
    }

    # --- the capability the whole integration depends on ---------------------
    $h = Invoke-HorizunProcess -Path $info.path -Arguments @('init', '--help') -TimeoutSec $ProbeTimeoutSec
    $info.probes += (& $record $h)
    $helpText = ($h.stdout + "`n" + $h.stderr)
    if ($h.started -and -not $h.timed_out -and $h.exit_code -eq 0 -and $helpText -match '--mcp-command') {
        if ($info.variant) {
            # Contradictory evidence is not proof either way.
            $info.status = 'unverified'
            $info.problem = "tunnel-client reports flavor=$($info.variant) yet documents init --mcp-command; its capabilities could not be established."
        }
        else {
            $info.variant = 'full'
            $info.status = 'compatible'
            $info.supports_mcp_command = $true
        }
        return [pscustomobject]$info
    }
    $refusedInit = $h.started -and -not $h.timed_out -and $h.exit_code -ne 0 -and $helpText -match 'unknown command "init"'
    if ($info.variant -or $refusedInit) {
        if (-not $info.variant) {
            $info.variant = if ($helpText -match 'for "(tunnel-client-runtime[^"]*)"') { $Matches[1] -replace '^tunnel-client-', '' } else { 'runtime' }
        }
        $info.status = 'wrong_variant'
        $info.supports_mcp_command = $false
        $info.problem = ("This is the runtime-only tunnel-client ({0}): it has no init, doctor or --mcp-command, and renaming it does not change that. " -f $info.variant) + $advice.text
        return [pscustomobject]$info
    }
    if (-not $h.started -or $h.timed_out) {
        $info.status = 'failed'
        $info.problem = if ($h.timed_out) { "tunnel-client did not answer 'init --help' within $ProbeTimeoutSec seconds." } else { "tunnel-client would not start for 'init --help': $($h.error)" }
        return [pscustomobject]$info
    }
    # It answered, but not with a successful help that documents the flag. An
    # error text that merely mentions --mcp-command is not support.
    $info.status = 'unverified'
    $info.problem = ("tunnel-client answered 'init --help' with exit code {0} and no usable help, so its support for --mcp-command is not established: {1}" -f $h.exit_code, (& $excerpt $h))
    return [pscustomobject]$info
}

# ---------------------------------------------------------------------------
# The running process: identity, not a bare pid
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# How often a healthy tunnel polls: the EFFECTIVE configuration
# ---------------------------------------------------------------------------

function ConvertFrom-HorizunGoDuration {
    <# A Go duration ("30000ms", "30s", "1m30s", "2h") in seconds; $null if not one. #>
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $t = $Text.Trim().Trim('"', "'")
    if ($t -match '^\d+(\.\d+)?$') { return [double]::Parse($t, [Globalization.CultureInfo]::InvariantCulture) }   # bare number: seconds
    $total = 0.0; $rest = $t
    while ($rest.Length -gt 0) {
        if ($rest -notmatch '^(\d+(?:\.\d+)?)(ms|s|m|h)') { return $null }
        $n = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        switch ($Matches[2]) { 'ms' { $total += $n / 1000 } 's' { $total += $n } 'm' { $total += $n * 60 } 'h' { $total += $n * 3600 } }
        $rest = $rest.Substring($Matches[0].Length)
    }
    return $total
}

function Get-HorizunTunnelPollSettings {
    <#
      The poll timing the tunnel-client WILL use, resolved the way it resolves it:
      flag > environment > profile YAML > default. This helper passes no poll flag,
      so the environment it hands the child (its own) and the profile decide.

        poll_timeout             default 30 s  (CONTROL_PLANE_POLL_TIMEOUT / poll_timeout)
        poll_deadline_guardrail  default 5 s   (CONTROL_PLANE_POLL_DEADLINE_GUARDRAIL / poll_deadline_guardrail)
        initial_poll_timeout     default 30 s  (CONTROL_PLANE_INITIAL_POLL_TIMEOUT / initial_poll_timeout)

      A healthy idle client completes one empty poll within
      poll_timeout + poll_deadline_guardrail (OpenAI's configuration reference),
      and the timestamp of a successful poll is recorded when it completes.
    #>
    param([string]$ProfilePath)
    $yaml = if ($ProfilePath -and (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) { Get-Content -LiteralPath $ProfilePath -Raw -Encoding UTF8 } else { '' }
    $pick = {
        param([string]$envName, [string]$yamlKey, [double]$default)
        $e = [Environment]::GetEnvironmentVariable($envName, 'Process')
        $v = ConvertFrom-HorizunGoDuration $e
        if ($null -ne $v -and $v -gt 0) { return @($v, "environment $envName") }
        if ($yaml -match ("(?m)^\s*" + [regex]::Escape($yamlKey) + ":\s*(\S+)")) {
            $v = ConvertFrom-HorizunGoDuration $Matches[1]
            if ($null -ne $v -and $v -gt 0) { return @($v, "profile $yamlKey") }
        }
        return @($default, 'tunnel-client default')
    }
    $pt = & $pick 'CONTROL_PLANE_POLL_TIMEOUT' 'poll_timeout' 30
    $gr = & $pick 'CONTROL_PLANE_POLL_DEADLINE_GUARDRAIL' 'poll_deadline_guardrail' 5
    $ip = & $pick 'CONTROL_PLANE_INITIAL_POLL_TIMEOUT' 'initial_poll_timeout' 30
    return [pscustomobject]@{
        poll_timeout_seconds         = $pt[0]; poll_timeout_source = $pt[1]
        poll_deadline_guardrail_seconds = $gr[0]; poll_deadline_guardrail_source = $gr[1]
        initial_poll_timeout_seconds = $ip[0]; initial_poll_timeout_source = $ip[1]
    }
}

function Get-HorizunTunnelFreshness {
    <#
      From the effective poll settings, two numbers:

        fresh_seconds    the last successful poll must be younger than this:
                         TWO full poll cycles (poll_timeout + guardrail each) plus a
                         20 s margin for backoff and scheduling. One slow or lost
                         cycle is tolerated; two in a row is a lost connection.
                         With the defaults, 2 x (30 + 5) + 20 = 90 s.
        startup_grace    before its first successful poll, a new process is
                         "connecting", not "failed", for the first poll's worst
                         case (min(initial, poll_timeout) + guardrail) plus the same
                         margin: 55 s with the defaults.

      These are derived from the client's documented behaviour and measured only
      against a LOCAL stand-in for the control plane - not against OpenAI.
    #>
    param($Settings)
    if (-not $Settings) { $Settings = Get-HorizunTunnelPollSettings }
    $cycle = [double]$Settings.poll_timeout_seconds + [double]$Settings.poll_deadline_guardrail_seconds
    $first = [Math]::Min([double]$Settings.initial_poll_timeout_seconds, [double]$Settings.poll_timeout_seconds) + [double]$Settings.poll_deadline_guardrail_seconds
    return [pscustomobject]@{
        fresh_seconds         = [int][Math]::Ceiling(2 * $cycle + 20)
        startup_grace_seconds = [int][Math]::Ceiling($first + 20)
    }
}

# ---------------------------------------------------------------------------
# Starting it
# ---------------------------------------------------------------------------

function Start-HorizunTunnelProcess {
    <#
      Start tunnel-client DETACHED, with the key reaching it and nobody else.

      ShellExecute with a hidden window of its own: measured, a child sharing the
      helper's console dies when that window is closed, and a child that inherits
      the helper's handles keeps a caller's captured stdout open for as long as it
      lives. ShellExecute inherits neither - but it also ignores
      ProcessStartInfo.EnvironmentVariables and gives the child a copy of THIS
      process's environment block.

      So the key is placed in this process's environment - PROCESS scope only,
      which is memory, never the registry - for the instant of the start, and the
      variable's PREVIOUS value (or its absence) is put back in `finally`, whether
      the start succeeded or threw. User- and Machine-scope variables are never
      read or written here. The key is never an argument.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Secret
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Executable
    $psi.Arguments = Join-HorizunWindowsArguments $Arguments
    $psi.UseShellExecute = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.WorkingDirectory = $WorkingDirectory
    $name = 'CONTROL_PLANE_API_KEY'
    $previous = [Environment]::GetEnvironmentVariable($name, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($name, $Secret, 'Process')
        return [Diagnostics.Process]::Start($psi)
    }
    finally {
        # [NullString]::Value, not $null: PowerShell turns $null into "" for a .NET
        # string parameter, and .NET 8 then leaves the variable DEFINED and empty
        # (.NET Framework deletes it). Measured under PowerShell 7.
        if ($null -eq $previous) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process') }
        else { [Environment]::SetEnvironmentVariable($name, $previous, 'Process') }
    }
}

function Get-HorizunTunnelListeners {
    <#
      Every TCP address the tunnel-client process and its direct children are
      LISTENING on, as the operating system reports it - not as configured.
    #>
    param([Parameter(Mandatory = $true)][int]$ProcessId)
    $ids = @($ProcessId)
    try { $ids += @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" -ErrorAction Stop | ForEach-Object { [int]$_.ProcessId }) } catch { }
    $out = @()
    foreach ($id in $ids) {
        try {
            foreach ($c in @(Get-NetTCPConnection -OwningProcess $id -State Listen -ErrorAction Stop)) {
                $addr = [string]$c.LocalAddress
                $out += [pscustomobject]@{ pid = $id; address = $addr; port = [int]$c.LocalPort
                                           loopback = ($addr -eq '127.0.0.1' -or $addr -eq '::1') }
            }
        }
        catch { }
    }
    return ,$out
}

function Get-HorizunTunnelRuntimePath {
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    return (Join-Path $StateRoot 'tunnel-runtime.json')
}

function Save-HorizunTunnelRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$StateRoot,
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$HealthUrlFile,
        [Parameter(Mandatory = $true)][string]$LogFile,
        $PollSettings
    )
    $doc = [pscustomobject]@{
        schema            = 1
        pid               = $Process.Id
        process_start_utc = $Process.StartTime.ToUniversalTime().ToString('o')
        executable        = $Executable
        profile           = $script:HorizunTunnelProfileName
        profile_dir       = (Get-HorizunTunnelProfileDir -StateRoot $StateRoot)
        health_url_file   = $HealthUrlFile
        log_file          = $LogFile
        started_utc       = (Get-Date).ToUniversalTime().ToString('o')
        poll_settings     = $PollSettings
    }
    $path = Get-HorizunTunnelRuntimePath -StateRoot $StateRoot
    $tmp = "$path.tmp-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $tmp -Value ($doc | ConvertTo-Json) -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $path -Force
    return $doc
}

function Get-HorizunTunnelRuntime {
    <#
      Is the tunnel THIS integration started still running?

      state, one of:
        none         no record, and no legacy pid file
        legacy       only a 2.0.1-style bare pid file exists; it cannot prove
                     which process it names, so nothing acts on it
        unreadable   a record exists but cannot be parsed or is incomplete
        not_running  the recorded process has exited
        mismatch     a process holds that pid, but it is not the one recorded
                     (different start time or executable): the pid was reused
        running      the recorded process, verified by pid + start time + path
    #>
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    $out = [ordered]@{ state = 'none'; record = $null; process = $null; detail = $null }
    $path = Get-HorizunTunnelRuntimePath -StateRoot $StateRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if (Test-Path -LiteralPath (Join-Path $StateRoot 'tunnel-client.pid') -PathType Leaf) {
            $out.state = 'legacy'
            $out.detail = 'an earlier version left tunnel-client.pid, which names a process id and nothing that proves which process it is; it is not acted on.'
        }
        return [pscustomobject]$out
    }
    $doc = $null
    try { $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $doc = $null }
    if (-not $doc -or -not $doc.pid -or -not $doc.process_start_utc -or -not $doc.executable) {
        $out.state = 'unreadable'
        $out.detail = "the process record $path could not be read, so no process is attributed to this integration."
        return [pscustomobject]$out
    }
    $out.record = $doc
    $p = Get-Process -Id ([int]$doc.pid) -ErrorAction SilentlyContinue
    if (-not $p) { $out.state = 'not_running'; $out.detail = "the recorded tunnel-client (pid $($doc.pid)) has exited."; return [pscustomobject]$out }
    $sameStart = $false; $samePath = $false
    try {
        # PowerShell 7's ConvertFrom-Json turns an ISO-8601 string into a DateTime
        # by itself; 5.1 leaves it a string. Re-parsing the DateTime as text goes
        # through the local culture and loses the zone - measured: five hours off,
        # and every running tunnel reported as a reused pid.
        $raw = $doc.process_start_utc
        $recorded = if ($raw -is [datetime]) { $raw.ToUniversalTime() }
                    else { [datetime]::Parse([string]$raw, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
        $sameStart = [Math]::Abs(($p.StartTime.ToUniversalTime() - $recorded).TotalSeconds) -lt 2
    }
    catch { }
    try { $samePath = [string]::Equals($p.Path, [string]$doc.executable, [StringComparison]::OrdinalIgnoreCase) } catch { }
    if ($sameStart -and $samePath) {
        $out.state = 'running'; $out.process = $p
        $out.detail = "tunnel-client pid $($doc.pid), verified by start time and executable."
    }
    else {
        $out.state = 'mismatch'
        $out.detail = "pid $($doc.pid) now belongs to a different process ($($p.ProcessName)); the recorded tunnel-client has exited and that process is not touched."
    }
    return [pscustomobject]$out
}

function Stop-HorizunTunnel {
    <#
      Stop ONLY the verified tunnel-client this integration started, and say
      exactly what is true afterwards. Never kills by name, never kills an
      unverified pid, never reports "stopped" without seeing the process exit.
    #>
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    $rt = Get-HorizunTunnelRuntime -StateRoot $StateRoot
    $res = [ordered]@{ previous_state = $rt.state; stopped = $false; verified = $false; detail = $rt.detail }
    $recordPath = Get-HorizunTunnelRuntimePath -StateRoot $StateRoot
    switch ($rt.state) {
        'running' {
            try { $rt.process.Kill() } catch { }
            $gone = $false
            try { $gone = $rt.process.WaitForExit(10000) } catch { $gone = $true }
            if (-not $gone) { $gone = -not (Get-Process -Id $rt.record.pid -ErrorAction SilentlyContinue) }
            if ($gone) {
                Remove-Item -LiteralPath $recordPath -Force -ErrorAction SilentlyContinue
                $res.stopped = $true; $res.verified = $true
                $res.detail = "stopped tunnel-client pid $($rt.record.pid) and saw it exit."
            }
            else { $res.detail = "asked tunnel-client pid $($rt.record.pid) to stop, and it was still running 10 seconds later." }
        }
        { $_ -in @('not_running', 'mismatch') } {
            Remove-Item -LiteralPath $recordPath -Force -ErrorAction SilentlyContinue
            $res.verified = $true
        }
        'none' { $res.verified = $true; $res.detail = 'no tunnel-client was recorded as started by this integration.' }
        default { }   # legacy, unreadable: cannot be verified, nothing is touched
    }
    return [pscustomobject]$res
}

# ---------------------------------------------------------------------------
# Is it connected? Four separate questions.
# ---------------------------------------------------------------------------

function Get-HorizunTunnelConnection {
    <#
      process        the verified tunnel-client is running
      local_health   its loopback /readyz answers 2xx
      control_plane  its last SUCCESSFUL poll of OpenAI is recent
                     (commands_poll_last_successful_timestamp_seconds younger than
                     the threshold derived from the poll settings it started with);
                     'connecting' while a new process is inside its startup grace
      listeners      what the process actually LISTENS on, from the OS; anything
                     not loopback makes local health fail
      chatgpt_call   never verified here: it takes a real call from ChatGPT

      -FreshSec 0 (the default) derives the threshold; a positive value overrides it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$StateRoot,
        $Runtime,
        [int]$FreshSec = 0,
        # -1 derives it; 0 or more overrides it (a test seam).
        [int]$StartupGraceSec = -1,
        [int]$TimeoutSec = 5
    )
    if (-not $Runtime) { $Runtime = Get-HorizunTunnelRuntime -StateRoot $StateRoot }
    $settings = $null
    if ($Runtime.record -and $Runtime.record.PSObject.Properties['poll_settings'] -and $Runtime.record.poll_settings) { $settings = $Runtime.record.poll_settings }
    if (-not $settings) { $settings = Get-HorizunTunnelPollSettings -ProfilePath (Get-HorizunTunnelProfilePath -StateRoot $StateRoot) }
    $fresh = Get-HorizunTunnelFreshness -Settings $settings
    $threshold = if ($FreshSec -gt 0) { $FreshSec } else { $fresh.fresh_seconds }
    if ($StartupGraceSec -ge 0) { $fresh.startup_grace_seconds = $StartupGraceSec }
    $res = [ordered]@{
        process       = $Runtime.state
        local_health  = [ordered]@{ checked = $false; ok = $false; url = $null; detail = $null }
        listeners     = @()
        control_plane = [ordered]@{ state = 'unknown'; last_success_utc = $null; age_seconds = $null; threshold_seconds = $threshold
                                    startup_grace_seconds = $fresh.startup_grace_seconds; poll_settings = $settings; detail = $null }
        chatgpt_call  = [ordered]@{ verified = $false; detail = 'Not verifiable from this machine: it takes a real tool call from ChatGPT, through the tunnel, reaching Revit.' }
        connected     = $false
    }
    if ($Runtime.state -ne 'running') {
        $res.local_health.detail = 'the tunnel-client is not running'
        $res.control_plane.detail = 'the tunnel-client is not running'
        return [pscustomobject]$res
    }
    $urlFile = [string]$Runtime.record.health_url_file
    $base = $null
    if ($urlFile -and (Test-Path -LiteralPath $urlFile -PathType Leaf)) { $base = (Get-Content -LiteralPath $urlFile -Raw).Trim() }
    if (-not $base) {
        $res.local_health.detail = "the client has not written its health URL to $urlFile"
        $res.control_plane.detail = 'no health URL, so the poll metric cannot be read'
        return [pscustomobject]$res
    }
    try { $uri = [uri]$base } catch { $uri = $null }
    if (-not $uri -or $uri.Host -notin @('127.0.0.1', 'localhost', '::1', '[::1]')) {
        $res.local_health.detail = "refusing to probe a non-loopback health address: $base"
        $res.control_plane.detail = $res.local_health.detail
        return [pscustomobject]$res
    }
    # WHERE IT REALLY LISTENS. The profile asks for 127.0.0.1:0 and run repeats it
    # as a flag; this reads the answer from the operating system.
    $res.listeners = Get-HorizunTunnelListeners -ProcessId ([int]$Runtime.record.pid)
    $exposed = @($res.listeners | Where-Object { -not $_.loopback })
    if ($exposed.Count -gt 0) {
        $res.local_health.detail = "the tunnel-client listens on a NON-loopback address: " + (($exposed | ForEach-Object { "$($_.address):$($_.port)" }) -join ', ')
        $res.control_plane.detail = 'not evaluated: the local surface is exposed'
        return [pscustomobject]$res
    }
    $res.local_health.url = $base.TrimEnd('/') + '/readyz'
    try {
        $r = Invoke-WebRequest -Uri $res.local_health.url -TimeoutSec $TimeoutSec -UseBasicParsing -ErrorAction Stop
        $res.local_health.checked = $true
        $res.local_health.ok = ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300)
        $res.local_health.detail = "HTTP $($r.StatusCode)"
    }
    catch { $res.local_health.checked = $true; $res.local_health.detail = $_.Exception.Message }

    try {
        $m = Invoke-WebRequest -Uri ($base.TrimEnd('/') + '/metrics') -TimeoutSec $TimeoutSec -UseBasicParsing -ErrorAction Stop
        $content = [string]$m.Content
        $values = @()
        foreach ($line in ($content -split "`r?`n")) {
            if ($line -match '^commands_poll_last_successful_timestamp_seconds(\{[^}]*\})?\s+(\S+)') {
                $n = 0.0
                if ([double]::TryParse($Matches[2], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$n)) { $values += $n }
            }
        }
        if ($values.Count -eq 0) {
            $res.control_plane.detail = 'the client does not publish commands_poll_last_successful_timestamp_seconds, so contact with OpenAI cannot be established'
        }
        else {
            $latest = ($values | Measure-Object -Maximum).Maximum
            if ($latest -le 0) {
                $res.control_plane.state = 'never'
                $res.control_plane.detail = 'the client has not completed a single successful poll of OpenAI'
            }
            else {
                $when = [DateTimeOffset]::FromUnixTimeMilliseconds([long]($latest * 1000))
                $age = [int]([DateTimeOffset]::UtcNow - $when).TotalSeconds
                $res.control_plane.last_success_utc = $when.UtcDateTime.ToString('o')
                $res.control_plane.age_seconds = $age
                if ($age -le $threshold) {
                    $res.control_plane.state = 'fresh'
                    $res.control_plane.detail = "last successful poll $age s ago (fresh within $threshold s)"
                }
                else {
                    $res.control_plane.state = 'stale'
                    $res.control_plane.detail = "last successful poll $age s ago, older than $threshold s: the connection to OpenAI is not currently working"
                }
            }
        }
    }
    catch { $res.control_plane.detail = "the metrics endpoint did not answer: $($_.Exception.Message)" }

    if ($res.control_plane.state -in @('never', 'unknown')) {
        $age = $null
        try { $age = ([DateTime]::UtcNow - $Runtime.process.StartTime.ToUniversalTime()).TotalSeconds } catch { }
        if ($null -ne $age -and $age -lt $fresh.startup_grace_seconds) {
            $res.control_plane.state = 'connecting'
            $res.control_plane.detail = ("started {0} s ago; its first poll can take up to {1} s. " -f [int]$age, $fresh.startup_grace_seconds) + $res.control_plane.detail
        }
    }
    $res.connected = ($res.local_health.ok -and $res.control_plane.state -eq 'fresh')
    return [pscustomobject]$res
}
