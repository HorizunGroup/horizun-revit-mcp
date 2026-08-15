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

$register = Join-Path $PSScriptRoot 'register-client.ps1'
$verify = Join-Path $PSScriptRoot 'verify-install.ps1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'HorizunMCPCompleteInstall'
$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$verificationPath = "$StatusPath.verification.json"

function Write-State([string]$State, [string]$Detail, [string]$ResolvedClient, $Extra) {
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
    }
    if ($Extra) {
        foreach ($property in $Extra.PSObject.Properties) { $doc[$property.Name] = $property.Value }
    }
    $tmp = "$StatusPath.tmp-$([guid]::NewGuid().ToString('N'))"
    [pscustomobject]$doc | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $StatusPath -Force
    Write-Host "[Horizun] $State - $Detail" -ForegroundColor $(if ($State -match 'failed') { 'Red' } elseif ($State -match 'waiting|awaiting|scheduled') { 'Yellow' } else { 'Green' })
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
    $command = ('"{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File {1} -Client {2} -ServerPath {3} -StatusPath {4} -WaitTimeoutMinutes {5} -Detached' -f `
        $powerShellExe, $quotedScript, $Resolved, $quotedServer, $quotedStatus, $WaitTimeoutMinutes)
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
        '-WaitTimeoutMinutes', $WaitTimeoutMinutes, '-Detached'
    )
    if ($OnlyLive) { $arguments += '-LiveOnly' }
    if ($NoResume) { $arguments += '-NoResume' }
    if ($NoLiveWait) { $arguments += '-NoLiveWait' }
    if ($ClientStateFile) { $arguments += '-ClientStateFile'; $arguments += ('"' + $ClientStateFile + '"') }
    $stdout = "$StatusPath.worker.log"
    $stderr = "$StatusPath.worker-error.log"
    Start-Process -FilePath $powerShellExe -ArgumentList $arguments -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
}

if ($StatusOnly) {
    if (Test-Path -LiteralPath $StatusPath) { Get-Content -LiteralPath $StatusPath }
    else { Write-Host "No Horizun completion status exists at $StatusPath" -ForegroundColor Yellow }
    exit 0
}

if ($CancelPending) {
    Clear-Resume
    Remove-Item -LiteralPath $StatusPath, $verificationPath -Force -ErrorAction SilentlyContinue
    Write-Host '[Horizun] pending installation completion was cancelled.' -ForegroundColor Yellow
    exit 0
}

foreach ($needed in $register, $verify) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) { throw "Installed completion helper is missing: $needed" }
}
if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) { throw "Installed MCP server is missing: $ServerPath" }

$resolved = Resolve-Client $Client
if ($resolved -eq 'None' -and -not $LiveOnly) {
    Write-State 'failed_no_client' 'Neither Claude nor Codex could be identified. Start the intended client once, close it, and run this helper again.' $resolved $null
    exit 2
}

# Only one hidden finisher may own the pending work. An interactive invocation is
# allowed to schedule that owner; detached duplicates simply leave it alone.
$mutex = $null
$mutexHeld = $false
if ($Detached) {
    $mutex = New-Object Threading.Mutex($false, 'Local\HorizunMCPCompleteInstall')
    try { $mutexHeld = $mutex.WaitOne(0) } catch { $mutexHeld = $false }
    if (-not $mutexHeld) {
        Write-Host '[Horizun] another completion worker is already active.' -ForegroundColor DarkGray
        exit 0
    }
}

try {
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
                Start-Sleep -Seconds 2
                $running = @(Running-Clients $resolved)
            }
        }

        $registerJson = "$StatusPath.registration.json"
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $register,
            '-Client', $resolved, '-ServerPath', $ServerPath, '-Json', $registerJson)
        if ($resolved -eq 'Both') { $arguments += '-SkipMissingClients' }
        if ($ClientStateFile) { $arguments += '-Force' }
        & $powerShellExe @arguments
        if ($LASTEXITCODE -ne 0) {
            Write-State 'registration_failed' "register-client.ps1 exited $LASTEXITCODE; existing client configuration was restored." $resolved $null
            exit 1
        }

        & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File $verify -Client $resolved `
            -ServerPath $ServerPath -SkipLive -Json $verificationPath
        if ($LASTEXITCODE -ne 0) {
            Write-State 'verification_failed' "on-disk or client verification exited $LASTEXITCODE" $resolved $null
            exit 1
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
