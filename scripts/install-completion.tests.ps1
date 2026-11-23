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
    # A run key that does not exist: the helper must never create it.
    $testRunKey = 'HKCU:\Software\HorizunTest\NoSuchRunKey-' + [guid]::NewGuid().ToString('N')
    $complete = Join-Path $PSScriptRoot 'complete-install.ps1'
    $started = Get-Date
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Claude -ServerPath $server -StatusPath $status -NoLiveWait `
        -ClientStateFile $clientState -RunKey $testRunKey
    Assert 'an active client is pending (exit 3), not treated as failure' ($LASTEXITCODE -eq 3) "exit $LASTEXITCODE"
    Assert 'an open client answers at once: nothing waits' (((Get-Date) - $started).TotalSeconds -lt 60) "took $([int]((Get-Date) - $started).TotalSeconds) s"
    $leftover = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe' OR Name='conhost.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -match [regex]::Escape($status) })
    Assert 'no helper process is left running after an open-client answer' ($leftover.Count -eq 0) (($leftover | ForEach-Object { $_.ProcessId }) -join ', ')

    $first = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    $before = Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude.json') -Raw | ConvertFrom-Json
    Assert 'state records that registration is waiting for client exit' ($first.state -eq 'waiting_for_client_exit') $first.state
    Assert 'the active client configuration was not edited' ('horizun-revit' -notin @($before.mcpServers.PSObject.Properties.Name)) (($before.mcpServers.PSObject.Properties.Name) -join ', ')

    # The user closes the client and runs the helper again (Setup's Retry or the
    # Start menu entry): one pass, in the foreground.
    Clear-Content -LiteralPath $clientState
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Claude -ServerPath $server -StatusPath $status -NoLiveWait `
        -ClientStateFile $clientState -RunKey $testRunKey
    $againExit = $LASTEXITCODE
    $final = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    Assert 'running it again with the client closed registers (exit 0)' ($againExit -eq 0 -and $final.state -eq 'installed_and_registered') "exit=$againExit state=$($final.state)"

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
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Codex -ServerPath $server -StatusPath $codexStatus -NoLiveWait `
        -ClientStateFile $clientState -RunKey $testRunKey
    Assert 'an active Codex process is pending, nothing edited' ($LASTEXITCODE -eq 3 -and
        (Get-Content -LiteralPath (Join-Path $codexDir 'config.toml') -Raw) -notmatch 'horizun-revit') "exit $LASTEXITCODE"
    Clear-Content -LiteralPath $clientState
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Codex -ServerPath $server -StatusPath $codexStatus -NoLiveWait `
        -ClientStateFile $clientState -RunKey $testRunKey
    $codexFinal = Get-Content -LiteralPath $codexStatus -Raw | ConvertFrom-Json
    Assert 'Codex registration completes when run again after exit' `
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
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Claude -ServerPath $server -StatusPath $rollbackStatus -NoLiveWait `
        -ClientStateFile $clientState -RunKey $testRunKey *> $null
    $rollbackExit = $LASTEXITCODE
    $rollbackState = Get-Content -LiteralPath $rollbackStatus -Raw | ConvertFrom-Json
    $rollbackAfter = [IO.File]::ReadAllBytes($claudeConfig)
    Assert 'post-registration verification failure restores the exact previous client config' `
        ($rollbackExit -eq 1 -and $rollbackState.state -eq 'verification_failed' -and
         [Convert]::ToBase64String($rollbackBefore) -eq [Convert]::ToBase64String($rollbackAfter)) `
        "exit=$rollbackExit state=$($rollbackState.state)"

    # Two install generations sharing one status path: the newer claim is the
    # authoritative one, and a run of the older generation afterwards writes
    # nothing into the shared status (Write-State refuses a superseded generation).
    'fake server bytes for generation race' | Set-Content -LiteralPath $server -Encoding ASCII
    $serverHash = (Get-FileHash -LiteralPath $server -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{ Schema=2; Server=[pscustomobject]@{Sha256=$serverHash;Payload=@([pscustomobject]@{Path='horizun-mcp.exe';Sha256=$serverHash})};Plugins=@() } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $serverDir) 'manifest.json') -Encoding UTF8
    $raceStatus = Join-Path $env:LOCALAPPDATA 'Horizun\generation-race.json'
    'Claude' | Set-Content -LiteralPath $clientState -Encoding ASCII
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Claude -ServerPath $server -StatusPath $raceStatus -NoLiveWait `
        -ClientStateFile $clientState -Generation oldgeneration01 -RunKey $testRunKey *> $null
    Clear-Content -LiteralPath $clientState
    & powershell -NoProfile -ExecutionPolicy Bypass -File $complete `
        -Client Claude -ServerPath $server -StatusPath $raceStatus -NoLiveWait `
        -ClientStateFile $clientState -Generation newgeneration02 -RunKey $testRunKey *> $null
    $raceFinal = Get-Content -LiteralPath "$raceStatus.generation-newgeneration02.json" -Raw | ConvertFrom-Json
    $oldJournal = Get-Content -LiteralPath "$raceStatus.generation-oldgeneration01.json" -Raw | ConvertFrom-Json
    $pointer = if (Test-Path "$raceStatus.current") { (Get-Content "$raceStatus.current" -Raw).Trim() } else { '' }
    $raceConfig = Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude.json') -Raw | ConvertFrom-Json
    Assert 'a newer completion generation is authoritative' `
        ($pointer -eq 'newgeneration02' -and $raceFinal.state -eq 'installed_and_registered' -and
         $oldJournal.state -eq 'waiting_for_client_exit' -and $raceConfig.mcpServers.'horizun-revit'.command -eq $server) `
        "pointer=$pointer new=$($raceFinal.state) old=$($oldJournal.state) command=$($raceConfig.mcpServers.'horizun-revit'.command)"

    # Backlog #60 is gone with the wait: a client open at the moment of the run
    # is the only case, it is answered at once, and nothing ever runs later.
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
