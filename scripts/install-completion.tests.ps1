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
        Server = [pscustomobject]@{ Sha256 = $serverHash }
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

    Add-Content -LiteralPath $server -Value 'tampered'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-install.ps1') `
        -Client Claude -ServerPath $server -SkipLive *> $null
    Assert 'manifest verification rejects a changed installed server' ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:LOCALAPPDATA = $oldLocal
    $env:APPDATA = $oldAppData
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failed -gt 0) { throw "$failed install completion test(s) failed" }
Write-Host 'install completion: ALL PASSED' -ForegroundColor Green
