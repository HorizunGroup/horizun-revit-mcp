#Requires -Version 5.1
<#
  Verify the installed Horizun release without trusting the installer's exit code.

  The checks are deliberately layered:
    1. the installed server and every installed Revit payload match manifest.json;
    2. the requested client configuration points at that exact server, preserving
       the long timeouts Revit work needs;
    3. when Revit is running, the installed server answers horizun_health.

  Revit must be closed while Setup replaces the add-in, so a correct fresh install
  normally finishes in `awaiting_revit`. complete-install.ps1 keeps that last check
  pending and completes it automatically after the first Revit start.

  Exit codes: 0 verified for the state currently available
              1 a check failed
              3 live verification was required but Revit is not available yet
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Claude', 'Codex', 'Both', 'None')]
    [string]$Client = 'Auto',
    [string]$Name = 'horizun-revit',
    [string]$ServerPath,
    [string]$ManifestPath,
    [switch]$SkipLive,
    [switch]$RequireLive,
    [string]$Json
)
$ErrorActionPreference = 'Stop'

if (-not $ServerPath) {
    $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
}
if (-not $ManifestPath) {
    $ManifestPath = Join-Path (Split-Path -Parent (Split-Path -Parent $ServerPath)) 'manifest.json'
}

$claudeConfig = Join-Path $env:USERPROFILE '.claude.json'
$codexConfig = Join-Path $env:USERPROFILE '.codex\config.toml'
$checks = New-Object System.Collections.Generic.List[object]
$problems = New-Object System.Collections.Generic.List[string]
$livePending = $false
$health = $null

function Check([string]$Label, [bool]$Ok, [string]$Detail) {
    $script:checks.Add([pscustomobject]@{ check = $Label; ok = $Ok; detail = $Detail }) | Out-Null
    if ($Ok) { Write-Host ("  OK    {0}" -f $Label) -ForegroundColor Green }
    else {
        Write-Host ("  WRONG {0} - {1}" -f $Label, $Detail) -ForegroundColor Red
        $script:problems.Add("$Label : $Detail") | Out-Null
    }
}

function Resolve-RequestedClient([string]$Requested) {
    if ($Requested -ne 'Auto') { return $Requested }
    $hasClaude = Test-Path -LiteralPath $claudeConfig
    $hasCodex = Test-Path -LiteralPath $codexConfig
    if ($hasClaude -and $hasCodex) { return 'Both' }
    if ($hasClaude) { return 'Claude' }
    if ($hasCodex) { return 'Codex' }
    return 'None'
}

function Get-CodexTable([string[]]$Lines, [string]$Header) {
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -eq $Header) { $start = $i; break }
    }
    if ($start -lt 0) { return @() }
    $end = $Lines.Count
    for ($i = $start + 1; $i -lt $Lines.Count; $i++) {
        $trim = $Lines[$i].Trim()
        if ($trim -match '^\[[^\]]+\]$' -and
            $trim -notmatch "^\[mcp_servers\.$([regex]::Escape($Name))\.") {
            $end = $i
            break
        }
    }
    if ($end -le $start) { return @() }
    return @($Lines[$start..($end - 1)])
}

function Resolve-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { return [IO.Path]::GetFullPath($Path).TrimEnd('\') }
    catch { return $Path.TrimEnd('\') }
}

Write-Host ''
Write-Host 'Horizun installation verification' -ForegroundColor Cyan
Write-Host ('-' * 72)

# The release manifest is the installer's statement of exactly what it shipped.
$manifest = $null
Check 'the installed MCP server exists' (Test-Path -LiteralPath $ServerPath -PathType Leaf) $ServerPath
Check 'the installed release manifest exists' (Test-Path -LiteralPath $ManifestPath -PathType Leaf) $ManifestPath
if ((Test-Path -LiteralPath $ServerPath) -and (Test-Path -LiteralPath $ManifestPath)) {
    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        Check 'manifest schema is supported' ($manifest.Schema -eq 2) "schema '$($manifest.Schema)'"
        $serverHash = (Get-FileHash -LiteralPath $ServerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Check 'installed server matches the release manifest' ($serverHash -eq [string]$manifest.Server.Sha256) `
            "installed $serverHash; manifest $($manifest.Server.Sha256)"
    }
    catch {
        Check 'release manifest parses' $false $_.Exception.Message
    }
}

# Only a Revit version actually present on this machine should have been installed.
# A release contains all five payloads; requiring all five under AppData on a machine
# with one Revit would incorrectly condemn a correct selective install.
if ($manifest) {
    foreach ($plugin in @($manifest.Plugins)) {
        $year = [int]$plugin.Year
        $revitApi = "C:\Program Files\Autodesk\Revit $year\RevitAPI.dll"
        if (-not (Test-Path -LiteralPath $revitApi)) { continue }
        $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun.addin"
        $dll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun\Horizun.Revit.dll"
        Check "Revit $year add-in manifest exists" (Test-Path -LiteralPath $addin -PathType Leaf) $addin
        Check "Revit $year add-in binary exists" (Test-Path -LiteralPath $dll -PathType Leaf) $dll
        if (Test-Path -LiteralPath $dll) {
            $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
            Check "Revit $year binary matches the release manifest" ($hash -eq [string]$plugin.Sha256) `
                "installed $hash; manifest $($plugin.Sha256)"
        }
    }
}

$resolvedClient = Resolve-RequestedClient $Client
$expectedServer = Resolve-FullPath $ServerPath

if ($resolvedClient -eq 'Claude' -or $resolvedClient -eq 'Both') {
    Check 'Claude configuration exists' (Test-Path -LiteralPath $claudeConfig -PathType Leaf) $claudeConfig
    if (Test-Path -LiteralPath $claudeConfig) {
        try {
            $cfg = Get-Content -LiteralPath $claudeConfig -Raw | ConvertFrom-Json
            $entry = $cfg.mcpServers.$Name
            Check "Claude has MCP entry '$Name'" ($null -ne $entry) 'entry not found'
            if ($entry) {
                $actual = Resolve-FullPath ([string]$entry.command)
                Check 'Claude points at the installed server' ($actual -ieq $expectedServer) "$actual"
            }
        }
        catch { Check 'Claude configuration parses' $false $_.Exception.Message }
    }
}

if ($resolvedClient -eq 'Codex' -or $resolvedClient -eq 'Both') {
    Check 'Codex configuration exists' (Test-Path -LiteralPath $codexConfig -PathType Leaf) $codexConfig
    if (Test-Path -LiteralPath $codexConfig) {
        try {
            $lines = @(Get-Content -LiteralPath $codexConfig)
            $table = @(Get-CodexTable $lines "[mcp_servers.$Name]")
            Check "Codex has MCP entry '$Name'" ($table.Count -gt 0) 'entry not found'
            if ($table.Count -gt 0) {
                $command = $null
                foreach ($line in $table) {
                    if ($line -match '^\s*command\s*=\s*("(?:[^"\\]|\\.)*")\s*$') {
                        $command = ($Matches[1] | ConvertFrom-Json)
                    }
                    elseif ($line -match "^\s*command\s*=\s*'([^']*)'\s*$") { $command = $Matches[1] }
                }
                $actual = Resolve-FullPath ([string]$command)
                Check 'Codex points at the installed server' ($actual -ieq $expectedServer) "$actual"
                Check 'Codex startup timeout is 120 seconds' (($table -join "`n") -match '(?m)^\s*startup_timeout_sec\s*=\s*120\s*$') 'missing or different'
                Check 'Codex tool timeout is 600 seconds' (($table -join "`n") -match '(?m)^\s*tool_timeout_sec\s*=\s*600\s*$') 'missing or different'
            }
        }
        catch { Check 'Codex configuration can be inspected' $false $_.Exception.Message }
    }
}

if (-not $SkipLive -and $problems.Count -eq 0) {
    $call = Join-Path $PSScriptRoot 'hz-call.ps1'
    if (-not (Test-Path -LiteralPath $call)) {
        Check 'live verification helper exists' $false $call
    }
    else {
        $targetJson = Join-Path ([IO.Path]::GetTempPath()) ('horizun-target-' + [guid]::NewGuid().ToString('N') + '.json')
        $healthJson = Join-Path ([IO.Path]::GetTempPath()) ('horizun-health-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $call -Tool horizun_target `
                -Server $ServerPath -TimeoutSec 30 -Quiet -Json $targetJson
            $targetExit = $LASTEXITCODE
            $target = if (Test-Path -LiteralPath $targetJson) { Get-Content -LiteralPath $targetJson -Raw | ConvertFrom-Json } else { $null }
            $found = if ($target -and $target.result) { [int]$target.result.targets_found } else { 0 }
            if ($targetExit -ne 0) {
                Check 'the installed server answers horizun_target' $false "exit code $targetExit"
            }
            elseif ($found -lt 1) {
                $livePending = $true
                if ($RequireLive) {
                    Write-Host '  WAIT  Revit is not running yet; horizun_health remains pending.' -ForegroundColor Yellow
                }
                else {
                    Write-Host '  READY Revit is not running; on-disk and client checks are complete.' -ForegroundColor Cyan
                }
            }
            else {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $call -Tool horizun_health `
                    -Server $ServerPath -TimeoutSec 120 -Quiet -Json $healthJson
                $healthExit = $LASTEXITCODE
                $healthReply = if (Test-Path -LiteralPath $healthJson) { Get-Content -LiteralPath $healthJson -Raw | ConvertFrom-Json } else { $null }
                $health = if ($healthReply) { $healthReply.result } else { $null }
                Check 'horizun_health answers through the installed server' ($healthExit -eq 0 -and $null -ne $health) `
                    "exit code $healthExit"
                if ($health) {
                    Check 'horizun_health reports healthy' ([string]$health.status -eq 'healthy') "status '$($health.status)'"
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $targetJson, $healthJson -Force -ErrorAction SilentlyContinue
        }
    }
}

$state = if ($problems.Count -gt 0) { 'failed' }
         elseif ($SkipLive) { 'installed_and_registered' }
         elseif ($livePending) { 'awaiting_revit' }
         else { 'live_verified' }

$report = [pscustomobject]@{
    schema = 1
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    state = $state
    client = $resolvedClient
    server_path = $ServerPath
    manifest_path = $ManifestPath
    checks = $checks
    problems = $problems
    health = $health
}
if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$Json.tmp-$([guid]::NewGuid().ToString('N'))"
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $Json -Force
    Write-Host "  wrote $Json"
}

Write-Host ('-' * 72)
if ($problems.Count -gt 0) { exit 1 }
if ($RequireLive -and $livePending) { exit 3 }
Write-Host "  $state" -ForegroundColor Green
exit 0
