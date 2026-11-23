#Requires -Version 5.1
<#
  Finish a Horizun installation without racing an active Claude or Codex.

  ONE PASS, IN THE FOREGROUND. If a requested client is open, nothing is edited:
  the state says which client to close and the helper exits 3. Run it again (the
  Start menu entry "Horizun - complete installation", or Setup's Retry) once the
  client is closed. With every client closed it registers beside every existing
  MCP entry, verifies the configuration, and tries horizun_health once through the
  installed server; if Revit is not running yet the state is awaiting_revit.

  There is no logon resume entry, no hidden background worker and no PowerShell
  relaunched from here: an antivirus behaviour engine read exactly that pattern
  (a hidden, policy-bypassing PowerShell re-armed at every sign-in) as malware
  (Norton IDP.HEUR.26, field report 2026-11-03). The register and verify helpers
  run in this same process. Entries left in HKCU Run by older releases are
  removed on every run and by -CancelPending. Re-running the script is safe.

  Exit codes: 0 completed (registered; live check passed, disabled, or pending Revit)
              1 failed
              2 no supported client could be identified
              3 pending: a client is open, or another completion run holds the lock
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Claude', 'Codex', 'Both', 'None')]
    [string]$Client = 'Auto',
    [string]$Name = 'horizun-revit',
    [string]$ServerPath,
    # Accepted for compatibility with older Setup and Run-entry command lines.
    # Nothing waits any more, so they change nothing.
    [int]$WaitTimeoutMinutes = 1440,
    [switch]$Detached,
    [switch]$NoResume,
    [switch]$LiveOnly,
    [switch]$NoLiveWait,
    [switch]$StatusOnly,
    [switch]$CancelPending,
    # The Start menu entry passes this so the window stays until it is read.
    [switch]$Pause,
    [string]$StatusPath,
    [string]$Generation,
    # Deterministic harness hook. When supplied, each non-empty line names a
    # client considered running; normal installs always inspect real processes.
    [string]$ClientStateFile,
    # Test hook: the registry key swept for entries of older releases. Tests point
    # it at a throwaway key under HKCU:\Software\HorizunTest; installs never pass it.
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
)
$ErrorActionPreference = 'Stop'

if (-not $ServerPath) {
    # Installed, this script lives in <app>\server\client-tools, so the server it
    # belongs to is its own sibling wherever Setup put <app>; the per-user default
    # is only the fallback for a source checkout.
    $sibling = Join-Path (Split-Path -Parent $PSScriptRoot) 'horizun-mcp.exe'
    $ServerPath = if (Test-Path -LiteralPath $sibling -PathType Leaf) { $sibling }
                  else { Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
}
if (-not $StatusPath) {
    $StatusPath = Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json'
}
if (-not $Generation) { $Generation = [guid]::NewGuid().ToString('N') }
if ($Generation -notmatch '^[A-Za-z0-9_-]{8,80}$') { throw 'Generation must be an opaque ASCII identifier (8..80 characters).' }

. (Join-Path $PSScriptRoot 'client-process.lib.ps1')
# Publish-HorizunAtomicFile / Remove-HorizunOrphanTempFiles: every status write here is an
# atomic replace that survives an existing or locked destination (field report, 3.7.0).
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')
$register = Join-Path $PSScriptRoot 'register-client.ps1'
$verify = Join-Path $PSScriptRoot 'verify-install.ps1'
$runKey = $RunKey
$runNamePrefix = 'HorizunMCPCompleteInstall-'
$currentGenerationPath = "$StatusPath.current"
$generationStatusPath = "$StatusPath.generation-$Generation.json"
$verificationPath = "$generationStatusPath.verification.json"

function Get-CurrentGeneration {
    if (-not (Test-Path -LiteralPath $currentGenerationPath -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $currentGenerationPath -Raw).Trim() } catch { return $null }
}

function Get-RunValueNames {
    # THE VALUE NAMES OF THE KEY. This used to wrap Get-ItemProperty in @() and
    # enumerate PSObject.Properties of the result - the properties of a one-element
    # ARRAY (Length, Count, Rank...), never the key's values - so on 5.1 and 7 alike
    # the sweep and -CancelPending removed nothing (audit 2026-10-08).
    if (-not (Test-Path -LiteralPath $runKey)) { return @() }
    return @((Get-Item -LiteralPath $runKey).GetValueNames())
}

function Remove-LegacyResumeEntries {
    # Releases up to 4.2 re-armed this helper at every sign-in through HKCU Run
    # (the un-suffixed legacy name and one suffixed name per generation). Nothing
    # writes those entries any more; every run removes whatever an older release
    # left, and only names with our fixed prefix.
    foreach ($name in @(Get-RunValueNames | Where-Object {
        $_ -eq 'HorizunMCPCompleteInstall' -or $_ -like "$runNamePrefix*"
    })) {
        Remove-ItemProperty -LiteralPath $runKey -Name $name -ErrorAction SilentlyContinue
    }
}

function Invoke-HorizunHelper([string]$Path, [hashtable]$Arguments) {
    # In this process, never a second powershell.exe: the helpers are scripts of
    # this same folder, and a chained relaunch with a bypassed policy is one of
    # the behaviours an antivirus reads as malware. A helper's `exit N` comes
    # back as $LASTEXITCODE; a throw is that helper failing, exit 1.
    $global:LASTEXITCODE = 0
    try { & $Path @Arguments | Out-Host }
    catch {
        Write-Warning "$(Split-Path -Leaf $Path): $($_.Exception.Message)"
        return 1
    }
    return [int]$LASTEXITCODE
}

function Claim-Generation {
    $dir = Split-Path -Parent $StatusPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Write-HorizunAtomicText -Path $currentGenerationPath -Text $Generation -Encoding ASCII
    # Temporaries left by a run that died between write and publish (field report).
    $null = Remove-HorizunOrphanTempFiles -Path $StatusPath
}

function Test-CurrentGeneration { return (Get-CurrentGeneration) -eq $Generation }

function Write-State([string]$State, [string]$Detail, [string]$ResolvedClient, $Extra) {
    if (-not (Test-CurrentGeneration)) { return $false }
    $dir = Split-Path -Parent $StatusPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $doc = [ordered]@{
        schema = 1
        updated_utc = (Get-Date).ToUniversalTime().ToString('o')
        state = $State
        detail = $Detail
        client = $ResolvedClient
        server_path = $ServerPath
        verification_path = $verificationPath
        generation = $Generation
    }
    if ($Extra) {
        foreach ($property in $Extra.PSObject.Properties) { $doc[$property.Name] = $property.Value }
    }
    # THE PER-CLIENT INTEGRATION BLOCK IS NOT OURS TO DROP. This function writes
    # the whole document, so a Claude Desktop record written by
    # scripts/integration-status.lib.ps1 would vanish at the next state change -
    # and the user would be told nothing is pending when something is.
    if (Test-Path -LiteralPath $StatusPath -PathType Leaf) {
        try {
            $existing = Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json
            $carried = $existing.PSObject.Properties['integrations']
            if ($carried -and $null -ne $carried.Value) { $doc['integrations'] = $carried.Value }
        }
        catch { }
    }
    # Each file is replaced atomically, retrying a destination that exists or is held
    # open: Move-Item -Force/Copy-Item -Force failed here with "Cannot create a file when
    # that file already exists" and "file in use" and left completion_failed behind.
    $json = [pscustomobject]$doc | ConvertTo-Json -Depth 20
    Write-HorizunAtomicText -Path $generationStatusPath -Text $json
    Write-HorizunAtomicText -Path $StatusPath -Text $json
    Write-Host "[Horizun] $State - $Detail" -ForegroundColor $(if ($State -match 'failed') { 'Red' } elseif ($State -match 'waiting|awaiting|pending') { 'Yellow' } else { 'Green' })
    return
}

function Resolve-Client([string]$Requested) {
    if ($Requested -ne 'Auto') { return $Requested }
    $claudeConfig = Join-Path $env:USERPROFILE '.claude.json'
    $codexConfig = Join-Path $env:USERPROFILE '.codex\config.toml'
    $hasClaude = Test-Path -LiteralPath $claudeConfig
    $hasCodex = Test-Path -LiteralPath $codexConfig

    # Preserve the caller's intent when the installer was launched by an agent.
    # Both desktop apps may be open on a real workstation; waiting for an unrelated
    # client would turn "one action" back into an unexplained permanent pending
    # state. These variables are inherited by Setup and its completion helper.
    $fromCodex = [bool]($env:CODEX_THREAD_ID -or $env:CODEX_CI -or $env:CODEX_INTERNAL_ORIGINATOR_OVERRIDE)
    $fromClaude = [bool]($env:CLAUDECODE -or $env:CLAUDE_CODE_ENTRYPOINT -or $env:CLAUDE_CODE_SESSION)
    if ($fromCodex -and -not $fromClaude -and ($hasCodex -or (Get-Command codex -ErrorAction SilentlyContinue))) { return 'Codex' }
    if ($fromClaude -and -not $fromCodex -and ($hasClaude -or (Get-Command claude -ErrorAction SilentlyContinue))) { return 'Claude' }

    if ($hasClaude -and $hasCodex) { return 'Both' }
    if ($hasClaude) { return 'Claude' }
    if ($hasCodex) { return 'Codex' }

    # A first CLI start can put the executable on PATH before it creates a config.
    # Name that situation rather than pretending registration succeeded. The
    # registrar deliberately refuses to invent a whole client config from scratch.
    $claudeCommand = Get-Command claude -ErrorAction SilentlyContinue
    $codexCommand = Get-Command codex -ErrorAction SilentlyContinue
    if ($claudeCommand -and $codexCommand) { return 'Both' }
    if ($claudeCommand) { return 'Claude' }
    if ($codexCommand) { return 'Codex' }
    return 'None'
}

function Requested-Clients([string]$Resolved) {
    if ($Resolved -eq 'Both') { return @('Claude', 'Codex') }
    if ($Resolved -eq 'None') { return @() }
    return @($Resolved)
}

function Client-IsRunning([string]$Which) {
    if ($ClientStateFile) {
        if (-not (Test-Path -LiteralPath $ClientStateFile)) { return $false }
        return $Which -in @(Get-Content -LiteralPath $ClientStateFile | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    # By path and command line, not by the name "claude": Claude Desktop's own
    # process does not rewrite ~/.claude.json, Claude Code (including the copy
    # Desktop runs for its Code tab) does. See client-process.lib.ps1.
    $kind = if ($Which -eq 'Claude') { 'ClaudeCode' } else { 'Codex' }
    return Test-HorizunClientRunning $kind
}

function Running-Clients([string]$Resolved) {
    return @(Requested-Clients $Resolved | Where-Object { Client-IsRunning $_ })
}

function Restore-RegistrationWrites([string]$ReportPath) {
    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { return $false }
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    $ok = $true
    $writes = @($report.writes)
    for ($i = $writes.Count - 1; $i -ge 0; $i--) {
        $write = $writes[$i]
        if (-not (Test-Path -LiteralPath $write.Backup -PathType Leaf) -or -not (Test-Path -LiteralPath $write.Path -PathType Leaf)) { $ok = $false; continue }
        $current = (Get-FileHash -LiteralPath $write.Path -Algorithm SHA256).Hash
        if ($current -ne [string]$write.CurrentHash) { $ok = $false; continue }
        Copy-Item -LiteralPath $write.Backup -Destination $write.Path -Force
    }
    return $ok
}

function Complete-Installation {
    if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) { throw "Installed MCP server is missing: $ServerPath" }
    $resolved = Resolve-Client $Client
    if ($resolved -eq 'None' -and -not $LiveOnly) {
        Write-State 'failed_no_client' 'Neither Claude nor Codex could be identified. Start the intended client once, close it, and run this helper again.' $resolved $null
        return 2
    }
    try {
        if (-not $LiveOnly) {
            # Never write underneath a running client: it may rewrite its own
            # configuration when it exits. Say which one to close and stop.
            $running = @(Running-Clients $resolved)
            if ($running.Count -gt 0) {
                Write-State 'waiting_for_client_exit' ("Close " + ($running -join ' and ') + ', then run "Horizun - complete installation" from the Start menu (or this helper again). No configuration has been edited.') $resolved `
                    ([pscustomobject]@{ running_clients = $running })
                return 3
            }

            $registerJson = "$generationStatusPath.registration.json"
            $arguments = @{ Client = $resolved; ServerPath = $ServerPath; Json = $registerJson }
            if ($resolved -eq 'Both') { $arguments.SkipMissingClients = $true }
            if ($ClientStateFile) { $arguments.Force = $true }
            $registerExit = Invoke-HorizunHelper $register $arguments
            if ($resolved -eq 'Both' -and $registerExit -eq 2) {
                # register-client -SkipMissingClients: NEITHER command-line client has a
                # configuration on this machine (Claude Desktop only, or a CLI never started).
                # That is the same answer the Auto path gives - not a failed registration.
                Write-State 'failed_no_client' 'Neither Claude Code nor Codex is configured on this machine. For Claude Desktop use install-claude-desktop-extension.ps1; for a command-line client, start it once, close it, and run this helper again.' $resolved $null
                return 2
            }
            if ($registerExit -ne 0) {
                Write-State 'registration_failed' "register-client.ps1 exited $registerExit; existing client configuration was restored. Run this helper again once the cause is fixed." $resolved $null
                return 1
            }
            if (-not (Test-CurrentGeneration)) {
                [void](Restore-RegistrationWrites $registerJson)
                return 0
            }

            $verifyExit = Invoke-HorizunHelper $verify @{ Client = $resolved; ServerPath = $ServerPath; SkipLive = $true; Json = $verificationPath }
            if ($verifyExit -ne 0) {
                $restored = Restore-RegistrationWrites $registerJson
                Write-State 'verification_failed' "on-disk or client verification exited $verifyExit. Run this helper again once the cause is fixed." $resolved $null
                if (-not $restored) { Write-Warning 'Verification failed and at least one client config could not be safely restored because it changed after registration.' }
                return 1
            }
            if (-not (Test-CurrentGeneration)) {
                [void](Restore-RegistrationWrites $registerJson)
                return 0
            }
            Write-State 'installed_and_registered' 'Binaries and client configuration are verified. Restart the client; live health is the remaining check.' $resolved $null
        }

        if ($NoLiveWait) {
            Write-State 'installed_and_registered' 'Live verification was explicitly disabled.' $resolved $null
            return 0
        }

        # One attempt, now. There is no background wait for Revit: horizun_health
        # from the client is the live check, and running this helper again records it.
        $liveExit = Invoke-HorizunHelper $verify @{ Client = $resolved; ServerPath = $ServerPath; RequireLive = $true; Json = $verificationPath }
        if ($liveExit -eq 0) {
            $evidence = Get-Content -LiteralPath $verificationPath -Raw | ConvertFrom-Json
            Write-State 'live_verified' 'horizun_health answered healthy through the installed server.' $resolved `
                ([pscustomobject]@{ health = $evidence.health })
            return 0
        }
        if ($liveExit -eq 3) {
            Write-State 'awaiting_revit' 'Registered and verified on disk. Start Revit, restart the client and call horizun_health; run this helper again to record the live check.' $resolved $null
            return 0
        }
        Write-State 'verification_failed' "live verification exited $liveExit." $resolved $null
        return 1
    }
    catch {
        Write-State 'completion_failed' $_.Exception.Message $resolved $null
        return 1
    }
}

if ($StatusOnly) {
    $current = Get-CurrentGeneration
    $authoritative = if ($current) { "$StatusPath.generation-$current.json" } else { $StatusPath }
    if (Test-Path -LiteralPath $authoritative) { Get-Content -LiteralPath $authoritative }
    else { Write-Host "No Horizun completion status exists at $StatusPath" -ForegroundColor Yellow }
    exit 0
}

# Every run, including -CancelPending and the legacy -Detached command line an
# older Run entry still carries: an entry under Run is never ours to keep.
Remove-LegacyResumeEntries

if ($CancelPending) {
    Remove-Item -LiteralPath $StatusPath, $verificationPath, $currentGenerationPath -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath (Split-Path -Parent $StatusPath) -Filter ((Split-Path -Leaf $StatusPath) + '.generation-*.json') -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host '[Horizun] pending installation completion was cancelled.' -ForegroundColor Yellow
    exit 0
}

foreach ($needed in $register, $verify) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) { throw "Installed completion helper is missing: $needed" }
}

# One run at a time may touch client configuration.
$statusBytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($StatusPath).ToLowerInvariant())
$statusHash = [BitConverter]::ToString(([Security.Cryptography.SHA256]::Create()).ComputeHash($statusBytes)).Replace('-', '').Substring(0, 20)
$mutex = New-Object Threading.Mutex($false, "Local\HorizunMCPCompleteInstallV2-$statusHash")
$mutexHeld = $false
try { $mutexHeld = $mutex.WaitOne([TimeSpan]::FromSeconds(30)) } catch { $mutexHeld = $false }
$code = 3
try {
    if (-not $mutexHeld) {
        Write-Host '[Horizun] another completion run still owns this status path; nothing was changed. Run this helper again when it finishes.' -ForegroundColor Yellow
    }
    else {
        Claim-Generation
        $code = Complete-Installation
    }
}
finally {
    if ($mutexHeld) { try { $mutex.ReleaseMutex() } catch { } }
    $mutex.Dispose()
}
if ($Pause) { [void](Read-Host 'Press Enter to close / Pulsa Enter para cerrar') }
exit $code
