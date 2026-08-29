#Requires -Version 5.1
<#
  Finish a Horizun installation without racing an active Claude or Codex.

  If a requested client is open, this script records the pending work, installs
  a CurrentUser resume entry, and starts a hidden helper. The helper waits for a
  real quiet window, registers beside every existing MCP entry, verifies the
  resulting configuration, then waits for the first Revit start and calls
  horizun_health through the installed MCP server.

  This is intentionally per-user and needs no administrator rights. The resume
  entry is removed after live verification, by -CancelPending, or during
  uninstall. Re-running the script is safe.

  Exit codes: 0 completed or safely scheduled
              1 failed
              2 no supported client could be identified
              3 still pending when a detached wait reached its deadline
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Claude', 'Codex', 'Both', 'None')]
    [string]$Client = 'Auto',
    [string]$Name = 'horizun-revit',
    [string]$ServerPath,
    [int]$WaitTimeoutMinutes = 1440,
    [switch]$Detached,
    [switch]$LiveOnly,
    [switch]$NoLiveWait,
    [switch]$NoResume,
    [switch]$StatusOnly,
    [switch]$CancelPending,
    [string]$StatusPath,
    [string]$Generation,
    # Deterministic harness hook. When supplied, each non-empty line names a
    # client considered running; normal installs always inspect real processes.
    [string]$ClientStateFile
)
$ErrorActionPreference = 'Stop'

if ($WaitTimeoutMinutes -lt 1 -or $WaitTimeoutMinutes -gt 10080) {
    throw 'WaitTimeoutMinutes must be between 1 and 10080 (seven days).'
}
if (-not $ServerPath) {
    $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
}
if (-not $StatusPath) {
    $StatusPath = Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json'
}
if (-not $Generation) { $Generation = [guid]::NewGuid().ToString('N') }
if ($Generation -notmatch '^[A-Za-z0-9_-]{8,80}$') { throw 'Generation must be an opaque ASCII identifier (8..80 characters).' }

$register = Join-Path $PSScriptRoot 'register-client.ps1'
$verify = Join-Path $PSScriptRoot 'verify-install.ps1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runNamePrefix = 'HorizunMCPCompleteInstall-'
$runName = $runNamePrefix + $Generation
$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
# -WindowStyle Hidden stops hiding anything once Windows Terminal is the
# default console host (Windows 11): every hidden relaunch shows a terminal
# window, and the awaiting_revit worker shows it for up to 24 hours. conhost
# --headless forces the windowless legacy host regardless of that setting.
$conhostExe = Join-Path $env:SystemRoot 'System32\conhost.exe'
$currentGenerationPath = "$StatusPath.current"
$generationStatusPath = "$StatusPath.generation-$Generation.json"
$verificationPath = "$generationStatusPath.verification.json"

function Get-CurrentGeneration {
    if (-not (Test-Path -LiteralPath $currentGenerationPath -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $currentGenerationPath -Raw).Trim() } catch { return $null }
}

function Remove-StaleResumeEntries {
    # Entries that are NOT this generation's: the legacy un-suffixed name from
    # pre-generation installs, and suffixed entries of superseded generations.
    # Safe on every path because it never touches $runName itself. This used to
    # live only inside Claim-Generation, and the logon resume path exits BEFORE
    # claiming - so an orphaned legacy entry relaunched a visible window at
    # every logon and the script never cleaned it (measured on this machine,
    # 2026-08-21: HorizunMCPCompleteInstall from a pre-generation install).
    if ($NoResume -or -not (Test-Path -LiteralPath $runKey)) { return }
    foreach ($property in @(Get-ItemProperty -LiteralPath $runKey).PSObject.Properties | Where-Object {
        $_.Name -eq 'HorizunMCPCompleteInstall' -or
        ($_.Name -like "$runNamePrefix*" -and $_.Name -ne $runName)
    }) {
        Remove-ItemProperty -LiteralPath $runKey -Name $property.Name -ErrorAction SilentlyContinue
    }
}

function Claim-Generation {
    $dir = Split-Path -Parent $StatusPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$currentGenerationPath.tmp-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $tmp -Value $Generation -Encoding ASCII
    Move-Item -LiteralPath $tmp -Destination $currentGenerationPath -Force
    Remove-StaleResumeEntries
}

function Test-CurrentGeneration { return (Get-CurrentGeneration) -eq $Generation }

function Remove-LegacyServerBackups {
    # Source installers before the external rollback ledger kept executable
    # backups below server\replaced-*. A client racing the update can keep one
    # DLL locked until that client exits. This finisher runs after the quiet
    # window, which is the first reliable opportunity to remove it.
    $serverRoot = Split-Path -Parent $ServerPath
    if (-not (Test-Path -LiteralPath $serverRoot -PathType Container)) { return }
    $rootFull = [IO.Path]::GetFullPath($serverRoot).TrimEnd('\')
    $rootPrefix = $rootFull + '\'
    foreach ($old in @(Get-ChildItem -LiteralPath $rootFull -Directory -Filter 'replaced-*' -ErrorAction SilentlyContinue)) {
        $full = [IO.Path]::GetFullPath($old.FullName)
        if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (($old.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Write-Warning "Refusing to recursively clean reparse-point legacy backup: $full"
            continue
        }
        try { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Legacy server backup remains in use and will be retried by a later completion: $full ($($_.Exception.Message))" }
    }

    # In-use legacy images are quarantined beside server\ so the exact runtime
    # payload stays clean. They are ours only when the fixed prefix is present;
    # never enumerate or delete any broader parent content.
    $installRoot = Split-Path -Parent $rootFull
    $installPrefix = [IO.Path]::GetFullPath($installRoot).TrimEnd('\') + '\'
    # Only the verified quarantine prefix is disposable here. Never touch
    # .install-rollback-*: that name may belong to a concurrently running source
    # install whose undo ledger still needs it.
    foreach ($pattern in '.legacy-backup-*') {
        foreach ($old in @(Get-ChildItem -LiteralPath $installRoot -Directory -Filter $pattern -ErrorAction SilentlyContinue)) {
            $full = [IO.Path]::GetFullPath($old.FullName)
            if (-not $full.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if (($old.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Warning "Refusing to recursively clean reparse-point deferred backup: $full"
                continue
            }
            try { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Deferred backup remains in use and will be retried later: $full ($($_.Exception.Message))" }
        }
    }
}

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
    $tmp = "$generationStatusPath.tmp-$([guid]::NewGuid().ToString('N'))"
    [pscustomobject]$doc | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $generationStatusPath -Force
    Copy-Item -LiteralPath $generationStatusPath -Destination $StatusPath -Force
    Write-Host "[Horizun] $State - $Detail" -ForegroundColor $(if ($State -match 'failed') { 'Red' } elseif ($State -match 'waiting|awaiting|scheduled') { 'Yellow' } else { 'Green' })
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
    $pattern = if ($Which -eq 'Claude') { '(?i)^claude$' } else { '(?i)^(codex|openai\.codex)$' }
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -match $pattern).Count -gt 0
}

function Running-Clients([string]$Resolved) {
    return @(Requested-Clients $Resolved | Where-Object { Client-IsRunning $_ })
}

function Get-ResumeCommand([string]$Resolved) {
    $quotedScript = '"' + $PSCommandPath.Replace('"', '""') + '"'
    $quotedServer = '"' + $ServerPath.Replace('"', '""') + '"'
    $quotedStatus = '"' + $StatusPath.Replace('"', '""') + '"'
    $command = ('"{0}" --headless "{1}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File {2} -Client {3} -ServerPath {4} -StatusPath {5} -WaitTimeoutMinutes {6} -Detached' -f `
        $conhostExe, $powerShellExe, $quotedScript, $Resolved, $quotedServer, $quotedStatus, $WaitTimeoutMinutes)
    $command += ' -Generation ' + $Generation
    if ($NoLiveWait) { $command += ' -NoLiveWait' }
    if ($ClientStateFile) { $command += ' -ClientStateFile "' + $ClientStateFile.Replace('"', '""') + '"' }
    return $command
}

function Set-Resume([string]$Resolved) {
    if ($NoResume) { return }
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name $runName -Value (Get-ResumeCommand $Resolved) -Type String
}

function Clear-Resume {
    if (Test-Path -LiteralPath $runKey) {
        Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
    }
}

function Start-DetachedWorker([string]$Resolved, [switch]$OnlyLive) {
    $arguments = @(
        '-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $PSCommandPath + '"'), '-Client', $Resolved,
        '-ServerPath', ('"' + $ServerPath + '"'),
        '-StatusPath', ('"' + $StatusPath + '"'),
        '-WaitTimeoutMinutes', $WaitTimeoutMinutes, '-Detached', '-Generation', $Generation
    )
    if ($OnlyLive) { $arguments += '-LiveOnly' }
    if ($NoResume) { $arguments += '-NoResume' }
    if ($NoLiveWait) { $arguments += '-NoLiveWait' }
    if ($ClientStateFile) { $arguments += '-ClientStateFile'; $arguments += ('"' + $ClientStateFile + '"') }
    # Through conhost --headless so no terminal window appears while the worker
    # waits (under Windows Terminal as default host, Hidden alone shows one).
    # The stdout/stderr redirects are gone with it: a redirect would capture
    # conhost's streams, not the child's. The durable generation records ARE the
    # worker's evidence; stdout was best-effort duplication.
    $headlessArguments = @('--headless', $powerShellExe) + $arguments
    Start-Process -FilePath $conhostExe -ArgumentList $headlessArguments -WindowStyle Hidden | Out-Null
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

if ($StatusOnly) {
    $current = Get-CurrentGeneration
    $authoritative = if ($current) { "$StatusPath.generation-$current.json" } else { $StatusPath }
    if (Test-Path -LiteralPath $authoritative) { Get-Content -LiteralPath $authoritative }
    else { Write-Host "No Horizun completion status exists at $StatusPath" -ForegroundColor Yellow }
    exit 0
}

if ($CancelPending) {
    if (Test-Path -LiteralPath $runKey) {
        foreach ($property in @(Get-ItemProperty -LiteralPath $runKey).PSObject.Properties |
            Where-Object { $_.Name -eq 'HorizunMCPCompleteInstall' -or $_.Name -like "$runNamePrefix*" }) {
            Remove-ItemProperty -LiteralPath $runKey -Name $property.Name -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -LiteralPath $StatusPath, $verificationPath, $currentGenerationPath -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath (Split-Path -Parent $StatusPath) -Filter ((Split-Path -Leaf $StatusPath) + '.generation-*.json') -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host '[Horizun] pending installation completion was cancelled.' -ForegroundColor Yellow
    exit 0
}

foreach ($needed in $register, $verify) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) { throw "Installed completion helper is missing: $needed" }
}
if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) { throw "Installed MCP server is missing: $ServerPath" }

if (-not $Detached) { Claim-Generation }
$resolved = Resolve-Client $Client
if ($resolved -eq 'None' -and -not $LiveOnly) {
    Write-State 'failed_no_client' 'Neither Claude nor Codex could be identified. Start the intended client once, close it, and run this helper again.' $resolved $null
    exit 2
}

# One generation at a time may touch client configuration. Interactive setup
# invocations participate too: otherwise a new install could register concurrently
# with the older detached finisher it just superseded.
$mutex = $null
$mutexHeld = $false
$statusBytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($StatusPath).ToLowerInvariant())
$statusHash = [BitConverter]::ToString(([Security.Cryptography.SHA256]::Create()).ComputeHash($statusBytes)).Replace('-', '').Substring(0, 20)
$mutex = New-Object Threading.Mutex($false, "Local\HorizunMCPCompleteInstallV2-$statusHash")
try { $mutexHeld = $mutex.WaitOne([TimeSpan]::FromSeconds(30)) } catch { $mutexHeld = $false }
if (-not $mutexHeld) {
    Set-Resume $resolved
    if (-not $Detached) { Start-DetachedWorker $resolved }
    Write-Host '[Horizun] another completion generation still owns this status path; this generation remains scheduled.' -ForegroundColor DarkGray
    exit 3
}

try {
    if ($Detached -and -not (Get-CurrentGeneration)) { Claim-Generation }
    # The sweep runs on EVERY startup, including the early exits below: a stale
    # entry never belongs in Run whatever this generation decides about itself.
    Remove-StaleResumeEntries
    if (-not (Test-CurrentGeneration)) { Clear-Resume; exit 0 }
    Set-Resume $resolved
    $deadline = (Get-Date).AddMinutes($WaitTimeoutMinutes)

    if (-not $LiveOnly) {
        $running = @(Running-Clients $resolved)
        if ($running.Count -gt 0 -and -not $Detached) {
            Write-State 'waiting_for_client_exit' ("Close " + ($running -join ' and ') + '; registration will finish automatically afterward.') $resolved `
                ([pscustomobject]@{ running_clients = $running })
            Start-DetachedWorker $resolved
            exit 0
        }

        while ($running.Count -gt 0 -and (Get-Date) -lt $deadline) {
            if (-not (Test-CurrentGeneration)) { Clear-Resume; exit 0 }
            Write-State 'waiting_for_client_exit' ("Waiting for " + ($running -join ' and ') + ' to close; no configuration has been edited.') $resolved `
                ([pscustomobject]@{ running_clients = $running })
            Start-Sleep -Seconds 2
            $running = @(Running-Clients $resolved)
        }
        if ($running.Count -gt 0) {
            Write-State 'registration_pending' 'The client stayed open through the wait window. The user-level resume entry remains active.' $resolved `
                ([pscustomobject]@{ running_clients = $running })
            exit 3
        }

        # Require a short quiet window. This closes the close/reopen race: if the
        # client comes back immediately, registration waits instead of writing
        # underneath the new process.
        Start-Sleep -Seconds 2
        $running = @(Running-Clients $resolved)
        if ($running.Count -gt 0) {
            if (-not $Detached) { Start-DetachedWorker $resolved; exit 0 }
            while ($running.Count -gt 0 -and (Get-Date) -lt $deadline) {
                if (-not (Test-CurrentGeneration)) { Clear-Resume; exit 0 }
                Start-Sleep -Seconds 2
                $running = @(Running-Clients $resolved)
            }
        }

        Remove-LegacyServerBackups

        $registerJson = "$generationStatusPath.registration.json"
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $register,
            '-Client', $resolved, '-ServerPath', $ServerPath, '-Json', $registerJson)
        if ($resolved -eq 'Both') { $arguments += '-SkipMissingClients' }
        if ($ClientStateFile) { $arguments += '-Force' }
        & $powerShellExe @arguments
        if ($LASTEXITCODE -ne 0) {
            Write-State 'registration_failed' "register-client.ps1 exited $LASTEXITCODE; existing client configuration was restored." $resolved $null
            exit 1
        }
        if (-not (Test-CurrentGeneration)) {
            [void](Restore-RegistrationWrites $registerJson)
            Clear-Resume
            exit 0
        }

        & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File $verify -Client $resolved `
            -ServerPath $ServerPath -SkipLive -Json $verificationPath
        if ($LASTEXITCODE -ne 0) {
            $restored = Restore-RegistrationWrites $registerJson
            Write-State 'verification_failed' "on-disk or client verification exited $LASTEXITCODE" $resolved $null
            if (-not $restored) { Write-Warning 'Verification failed and at least one client config could not be safely restored because it changed after registration.' }
            exit 1
        }
        if (-not (Test-CurrentGeneration)) {
            [void](Restore-RegistrationWrites $registerJson)
            Clear-Resume
            exit 0
        }
        Write-State 'installed_and_registered' 'Binaries and client configuration are verified. Restart the client; live health is the remaining automatic check.' $resolved $null
    }

    if ($NoLiveWait) {
        Clear-Resume
        Write-State 'installed_and_registered' 'Live verification was explicitly disabled.' $resolved $null
        exit 0
    }

    & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File $verify -Client $resolved `
        -ServerPath $ServerPath -RequireLive -Json $verificationPath
    if ($LASTEXITCODE -eq 0) {
        $evidence = Get-Content -LiteralPath $verificationPath -Raw | ConvertFrom-Json
        Clear-Resume
        Write-State 'live_verified' 'horizun_health answered healthy through the installed server.' $resolved `
            ([pscustomobject]@{ health = $evidence.health })
        exit 0
    }

    if (-not $Detached) {
        Write-State 'awaiting_revit' 'Start Revit once. Live verification will finish automatically in the background.' $resolved $null
        Start-DetachedWorker $resolved -OnlyLive
        exit 0
    }

    Write-State 'awaiting_revit' 'Waiting for the first Revit start so horizun_health can be verified.' $resolved $null
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-CurrentGeneration)) { Clear-Resume; exit 0 }
        Start-Sleep -Seconds 5
        & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File $verify -Client $resolved `
            -ServerPath $ServerPath -RequireLive -Json $verificationPath *> $null
        if ($LASTEXITCODE -eq 0) {
            $evidence = Get-Content -LiteralPath $verificationPath -Raw | ConvertFrom-Json
            Clear-Resume
            Write-State 'live_verified' 'horizun_health answered healthy through the installed server.' $resolved `
                ([pscustomobject]@{ health = $evidence.health })
            exit 0
        }
    }

    Write-State 'awaiting_revit' 'Revit was not available during this wait window. The user-level resume entry remains active.' $resolved $null
    exit 3
}
catch {
    Write-State 'completion_failed' $_.Exception.Message $resolved $null
    exit 1
}
finally {
    if ($mutexHeld) { try { $mutex.ReleaseMutex() } catch { } }
    if ($mutex) { $mutex.Dispose() }
}
