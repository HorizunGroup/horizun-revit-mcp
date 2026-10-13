#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-install-completion-' + [guid]::NewGuid().ToString('N'))
$oldProfile = $env:USERPROFILE
$oldLocal = $env:LOCALAPPDATA
$oldAppData = $env:APPDATA
$failed = 0

function Assert([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" }
        $script:failed++
    }
}

try {
    $env:USERPROFILE = Join-Path $root 'profile'
    $env:LOCALAPPDATA = Join-Path $root 'local'
    $env:APPDATA = Join-Path $root 'roaming'
    New-Item -ItemType Directory -Path $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA -Force | Out-Null

    $serverDir = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server'
    New-Item -ItemType Directory -Path $serverDir -Force | Out-Null
    $server = Join-Path $serverDir 'horizun-mcp.exe'
    'fake server bytes for installation completion tests' | Set-Content -LiteralPath $server -Encoding ASCII
    $serverHash = (Get-FileHash -LiteralPath $server -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{
        Schema = 2
        Server = [pscustomobject]@{ Sha256 = $serverHash; Payload = @(
            [pscustomobject]@{ Path = 'horizun-mcp.exe'; Sha256 = $serverHash }
        ) }
        Plugins = @()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $serverDir) 'manifest.json') -Encoding UTF8

    @'
{
  "theme": "keep-me",
  "mcpServers": {
    "other": { "command": "other.exe", "args": [] }
  }
}
'@ | Set-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude.json') -Encoding UTF8

    # A deterministic process-state file reproduces the race even on a runner
    # that already has a real Claude desktop app open.
    $clientState = Join-Path $root 'running-clients.txt'
    'Claude' | Set-Content -LiteralPath $clientState -Encoding ASCII

    $status = Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'complete-install.ps1') `
        -Client Claude -ServerPath $server -StatusPath $status -WaitTimeoutMinutes 1 -NoResume -NoLiveWait `
        -ClientStateFile $clientState
    Assert 'an active client is safely scheduled, not treated as failure' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"

    $first = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    $before = Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude.json') -Raw | ConvertFrom-Json
    Assert 'state records that registration is waiting for client exit' ($first.state -eq 'waiting_for_client_exit') $first.state
    Assert 'the active client configuration was not edited' ('horizun-revit' -notin @($before.mcpServers.PSObject.Properties.Name)) (($before.mcpServers.PSObject.Properties.Name) -join ', ')

    Clear-Content -LiteralPath $clientState
    $deadline = (Get-Date).AddSeconds(25)
    $final = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $status) {
            $final = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
            if ($final.state -eq 'installed_and_registered') { break }
        }
    }
    if (-not $final -or $final.state -ne 'installed_and_registered') {
        foreach ($log in "$status.worker.log", "$status.worker-error.log") {
            if (Test-Path -LiteralPath $log) {
                Write-Host "        worker log: $log"
                Get-Content -LiteralPath $log | ForEach-Object { Write-Host "        $_" }
            }
        }
    }
    Assert 'registration completes automatically after the client exits' ($final -and $final.state -eq 'installed_and_registered') $(if ($final) { $final.state } else { 'no state' })

    $after = Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude.json') -Raw | ConvertFrom-Json
    Assert 'the expected Claude entry was added' ('horizun-revit' -in @($after.mcpServers.PSObject.Properties.Name)) (($after.mcpServers.PSObject.Properties.Name) -join ', ')
    Assert 'unrelated Claude configuration survived' ($after.theme -eq 'keep-me' -and 'other' -in @($after.mcpServers.PSObject.Properties.Name)) $after.theme
    Assert 'Claude points at the installed server' ($after.mcpServers.'horizun-revit'.command -eq $server) $after.mcpServers.'horizun-revit'.command

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-install.ps1') `
        -Client Claude -ServerPath $server -SkipLive
    Assert 'standalone installation verification passes' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"

    # Exercise the other supported CLI through the same deferred path. This is
    # not covered by proving Claude JSON: Codex uses TOML and needs the two long
    # timeouts added without disturbing unrelated tables.
    $codexDir = Join-Path $env:USERPROFILE '.codex'
    New-Item -ItemType Directory -Path $codexDir -Force | Out-Null
    @'
model = "keep-me"

[mcp_servers.other]
command = 'other.exe'
args = []
'@ | Set-Content -LiteralPath (Join-Path $codexDir 'config.toml') -Encoding UTF8
    'Codex' | Set-Content -LiteralPath $clientState -Encoding ASCII
    $codexStatus = Join-Path $env:LOCALAPPDATA 'Horizun\codex-install-status.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'complete-install.ps1') `
        -Client Codex -ServerPath $server -StatusPath $codexStatus -WaitTimeoutMinutes 1 -NoResume -NoLiveWait `
        -ClientStateFile $clientState
    Assert 'an active Codex process is safely deferred' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    Clear-Content -LiteralPath $clientState
    $deadline = (Get-Date).AddSeconds(25)
    $codexFinal = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $codexStatus) {
            $codexFinal = Get-Content -LiteralPath $codexStatus -Raw | ConvertFrom-Json
            if ($codexFinal.state -eq 'installed_and_registered') { break }
        }
    }
    Assert 'Codex registration completes automatically after exit' `
        ($codexFinal -and $codexFinal.state -eq 'installed_and_registered') `
        $(if ($codexFinal) { $codexFinal.state } else { 'no state' })
    $codexText = Get-Content -LiteralPath (Join-Path $codexDir 'config.toml') -Raw
    Assert 'Codex keeps unrelated configuration' ($codexText -match 'model = "keep-me"' -and $codexText -match '\[mcp_servers\.other\]') $codexText
    $serverAsTomlJson = $server.Replace('\', '\\')
    Assert 'Codex points at the installed server' ($codexText -match [regex]::Escape($serverAsTomlJson)) $codexText
    Assert 'Codex receives Revit-safe timeouts' `
        ($codexText -match '(?m)^startup_timeout_sec\s*=\s*120\s*$' -and $codexText -match '(?m)^tool_timeout_sec\s*=\s*600\s*$') $codexText
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-install.ps1') `
        -Client Codex -ServerPath $server -SkipLive *> $null
    Assert 'standalone Codex installation verification passes' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"

    $noneReport = Join-Path $root 'none-verification.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-install.ps1') `
        -Client None -ServerPath $server -SkipLive -Json $noneReport *> $null
    $noneState = Get-Content -LiteralPath $noneReport -Raw | ConvertFrom-Json
    Assert 'binary-only verification does not claim client registration' `
        ($LASTEXITCODE -eq 0 -and $noneState.state -eq 'installed') $noneState.state

    Add-Content -LiteralPath $server -Value 'tampered'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-install.ps1') `
        -Client Claude -ServerPath $server -SkipLive *> $null
    Assert 'manifest verification rejects a changed installed server' ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"

    # Registration is itself transactional with the subsequent binary/config
    # verification.  A bad payload discovered after the client write must put
    # the exact pre-registration bytes back, not strand a newly registered
    # command that points at an invalid installation.
    $rollbackConfig = @'
{
  "theme": "rollback-me",
  "mcpServers": {
    "other": { "command": "other.exe", "args": [] }
  }
}
'@
    $claudeConfig = Join-Path $env:USERPROFILE '.claude.json'
    Set-Content -LiteralPath $claudeConfig -Value $rollbackConfig -Encoding UTF8
    $rollbackBefore = [IO.File]::ReadAllBytes($claudeConfig)
    Clear-Content -LiteralPath $clientState
    $rollbackStatus = Join-Path $env:LOCALAPPDATA 'Horizun\rollback-install-status.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'complete-install.ps1') `
        -Client Claude -ServerPath $server -StatusPath $rollbackStatus -WaitTimeoutMinutes 1 -NoResume -NoLiveWait `
        -ClientStateFile $clientState *> $null
    $rollbackExit = $LASTEXITCODE
    $rollbackState = Get-Content -LiteralPath $rollbackStatus -Raw | ConvertFrom-Json
    $rollbackAfter = [IO.File]::ReadAllBytes($claudeConfig)
    Assert 'post-registration verification failure restores the exact previous client config' `
        ($rollbackExit -eq 1 -and $rollbackState.state -eq 'verification_failed' -and
         [Convert]::ToBase64String($rollbackBefore) -eq [Convert]::ToBase64String($rollbackAfter)) `
        "exit=$rollbackExit state=$($rollbackState.state)"

    # Two install generations sharing one status path: the newer claim must make
    # the older worker relinquish the mutex, and only the newer journal is
    # authoritative. This is also run while a real pre-fix finisher may exist on
    # the workstation; its legacy mutex/run value cannot block V2.
    'fake server bytes for generation race' | Set-Content -LiteralPath $server -Encoding ASCII
    $serverHash = (Get-FileHash -LiteralPath $server -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{ Schema=2; Server=[pscustomobject]@{Sha256=$serverHash;Payload=@([pscustomobject]@{Path='horizun-mcp.exe';Sha256=$serverHash})};Plugins=@() } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $serverDir) 'manifest.json') -Encoding UTF8
    $oldServerDir = Join-Path $root 'old-product\MCP\server'
    New-Item -ItemType Directory -Path $oldServerDir -Force | Out-Null
    $oldServer = Join-Path $oldServerDir 'horizun-mcp.exe'
    'superseded server bytes' | Set-Content -LiteralPath $oldServer -Encoding ASCII
    $oldServerHash = (Get-FileHash -LiteralPath $oldServer -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{ Schema=2; Server=[pscustomobject]@{Sha256=$oldServerHash;Payload=@([pscustomobject]@{Path='horizun-mcp.exe';Sha256=$oldServerHash})};Plugins=@() } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $oldServerDir) 'manifest.json') -Encoding UTF8
    $raceStatus = Join-Path $env:LOCALAPPDATA 'Horizun\generation-race.json'
    'Claude' | Set-Content -LiteralPath $clientState -Encoding ASCII
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'complete-install.ps1') `
        -Client Claude -ServerPath $oldServer -StatusPath $raceStatus -WaitTimeoutMinutes 1 -NoResume -NoLiveWait `
        -ClientStateFile $clientState -Generation oldgeneration01
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'complete-install.ps1') `
        -Client Claude -ServerPath $server -StatusPath $raceStatus -WaitTimeoutMinutes 1 -NoResume -NoLiveWait `
        -ClientStateFile $clientState -Generation newgeneration02
    Clear-Content -LiteralPath $clientState
    $deadline = (Get-Date).AddSeconds(40)
    $raceFinal = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $newJournal = "$raceStatus.generation-newgeneration02.json"
        if (Test-Path -LiteralPath $newJournal) {
            $raceFinal = Get-Content -LiteralPath $newJournal -Raw | ConvertFrom-Json
            if ($raceFinal.state -eq 'installed_and_registered') { break }
        }
    }
    $pointer = if (Test-Path "$raceStatus.current") { (Get-Content "$raceStatus.current" -Raw).Trim() } else { '' }
    $raceConfig = Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude.json') -Raw | ConvertFrom-Json
    Assert 'new completion generation supersedes an older worker without being blocked or erased' `
        ($pointer -eq 'newgeneration02' -and $raceFinal -and $raceFinal.state -eq 'installed_and_registered' -and
         $raceConfig.mcpServers.'horizun-revit'.command -eq $server) `
        "pointer=$pointer state=$($raceFinal.state) command=$($raceConfig.mcpServers.'horizun-revit'.command)"

    # Backlog #60. The client closes, then comes back inside the quiet window and
    # stays open until the wait runs out. That is the same answer as a client that
    # never closed - registration_pending, nothing written, no failed attempt
    # counted - not a registration attempted underneath the running client.
    $pendingConfig = Join-Path $env:USERPROFILE '.claude.json'
    Set-Content -LiteralPath $pendingConfig -Value $rollbackConfig -Encoding UTF8
    $pendingBefore = [IO.File]::ReadAllBytes($pendingConfig)
    Clear-Content -LiteralPath $clientState
    $pendingStatus = Join-Path $env:LOCALAPPDATA 'Horizun\quiet-window-status.json'
    # A run key that does not exist: -NoResume never creates it, and nothing here
    # reads or writes the real HKCU Run key.
    $testRunKey = 'HKCU:\Software\HorizunTest\NoSuchRunKey-' + [guid]::NewGuid().ToString('N')
    $p = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + (Join-Path $PSScriptRoot 'complete-install.ps1') + '"'),
        '-Client', 'Claude', '-ServerPath', ('"' + $server + '"'), '-StatusPath', ('"' + $pendingStatus + '"'),
        '-WaitTimeoutMinutes', '1', '-Detached', '-NoResume', '-NoLiveWait', '-Generation', 'quietwindow01',
        '-ClientStateFile', ('"' + $clientState + '"'), '-RunKey', ('"' + $testRunKey + '"'))
    $null = $p.Handle
    # The generation claim is the first thing the worker writes, and its first
    # look at the clients follows within milliseconds (no client: nothing to wait
    # for). The client "reopens" 800 ms later, in the middle of the two-second
    # quiet window, so the second look is the one that sees it.
    $claimDeadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path -LiteralPath "$pendingStatus.current") -and (Get-Date) -lt $claimDeadline -and -not $p.HasExited) {
        Start-Sleep -Milliseconds 20
    }
    Start-Sleep -Milliseconds 800
    'Claude' | Set-Content -LiteralPath $clientState -Encoding ASCII
    if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'the quiet-window worker did not finish within three minutes' }
    $pendingExit = $p.ExitCode
    Clear-Content -LiteralPath $clientState
    $pendingState = Get-Content -LiteralPath "$pendingStatus.generation-quietwindow01.json" -Raw | ConvertFrom-Json
    Assert 'a client back inside the quiet window until the deadline is registration_pending, exit 3 (backlog #60)' `
        ($pendingExit -eq 3 -and $pendingState.state -eq 'registration_pending' -and $pendingState.detail -match 'quiet window') "exit=$pendingExit state=$($pendingState.state) detail=$($pendingState.detail)"
    Assert 'no failed attempt is counted for a client that simply stayed open (backlog #60)' `
        (-not (Test-Path -LiteralPath "$pendingStatus.generation-quietwindow01.json.attempts")) 'an attempts file was written'
    Assert 'nothing is written underneath the reopened client (backlog #60)' `
        ([Convert]::ToBase64String($pendingBefore) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($pendingConfig))) `
        ([IO.File]::ReadAllText($pendingConfig))
    Assert 'the test run key was never created' (-not (Test-Path -LiteralPath $testRunKey)) $testRunKey
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:LOCALAPPDATA = $oldLocal
    $env:APPDATA = $oldAppData
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failed -gt 0) { throw "$failed install completion test(s) failed" }
Write-Host 'install completion: ALL PASSED' -ForegroundColor Green
