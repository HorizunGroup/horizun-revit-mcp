# -----------------------------------------------------------------------------
# Horizun Revit MCP - sanitized install diagnostic.
#
# Collects the durable facts of THIS machine's installation into a report that
# is SAFE TO SHARE: no user names, no client/model names, no absolute personal
# paths - every path under the profile is rewritten to <profile>\... before it
# is printed. The claims are measured (files hashed, signatures queried,
# status.json read), never inferred from what a working install would look like.
#
#   -Repair        re-runs the known fixes for the states this diagnostic can
#                  recognise: stale/unsigned binaries -> deploy-both + self-sign
#                  (only when a repo tree is present), stale durable record ->
#                  refresh-install-status. Repair NEVER touches trust stores.
#   -SimulateFresh runs the same diagnostic against an EMPTY, temporary data
#                  root and install dir, proving the "nothing installed" report
#                  is reachable and clean - the fresh-machine evidence without
#                  a fresh machine.
#   -Json <path>   writes the sanitized report as JSON beside the console view.
# -----------------------------------------------------------------------------
[CmdletBinding()]
param(
    [switch]$Repair,
    [switch]$SimulateFresh,
    [string]$Json,
    # Isolated roots: point the WHOLE diagnostic (and -Repair) at a sandbox so
    # broken states can be provoked and repaired without touching the real
    # installation. -RepairSource names where known-good bytes come from when
    # repairing a sandbox (default: the real install, read-only).
    [string]$InstallDir,
    [string]$AddinsRoot,
    [string]$StatusPath,
    [string]$RepairSource
)
$ErrorActionPreference = 'Stop'

function Hide-Path([string]$path) {
    if (-not $path) { return $null }
    $out = $path
    if ($env:USERPROFILE) { $out = $out.Replace($env:USERPROFILE, '<profile>') }
    if ($env:USERNAME)    { $out = $out -replace [regex]::Escape("\$($env:USERNAME)\"), '\<user>\' }
    return $out
}

function Get-FileFact([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $item = Get-Item -LiteralPath $path
    $sig = $null
    try { $sig = (Get-AuthenticodeSignature -LiteralPath $path).Status.ToString() } catch { $sig = 'unreadable' }
    [ordered]@{
        path      = Hide-Path $path
        bytes     = $item.Length
        sha256    = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 12)
        signature = $sig
        written   = $item.LastWriteTimeUtc.ToString('u')
    }
}

$dataRoot   = if ($SimulateFresh) { Join-Path $env:TEMP ("hz-fresh-" + [guid]::NewGuid().ToString('N')) }
              else { Join-Path $env:USERPROFILE '.horizun' }
if (-not $InstallDir) {
    $InstallDir = if ($SimulateFresh) { Join-Path $env:TEMP ("hz-fresh-app-" + [guid]::NewGuid().ToString('N')) }
                  else { Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server' }
}
if (-not $StatusPath) {
    $StatusPath = if ($SimulateFresh) { Join-Path $dataRoot 'install-status.json' }
                  else { Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json' }
}
$installDir = $InstallDir
$statusPath = $StatusPath
$sandboxed  = [bool]($PSBoundParameters.ContainsKey('InstallDir') -or $PSBoundParameters.ContainsKey('AddinsRoot'))

$report = [ordered]@{
    schema        = 1
    generated_utc = (Get-Date).ToUniversalTime().ToString('u')
    mode          = if ($SimulateFresh) { 'simulate_fresh' } else { 'live' }
    sanitized     = $true
}

# ---- the server binary -------------------------------------------------------
$serverExe = Join-Path $installDir 'horizun-mcp.exe'
$report.server = if (Test-Path -LiteralPath $serverExe) { Get-FileFact $serverExe } else { 'not_installed' }

# ---- the add-ins, per year ---------------------------------------------------
$addins = [ordered]@{}
foreach ($year in 2023..2027) {
    $dll = if ($AddinsRoot) { Join-Path $AddinsRoot "$year\Horizun.Revit.dll" }
           elseif ($SimulateFresh) { Join-Path $dataRoot "addins\$year\Horizun.Revit.dll" }
           else { Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun\Horizun.Revit.dll" }
    $addins["$year"] = if (Test-Path -LiteralPath $dll) { Get-FileFact $dll } else { 'not_installed' }
}
$report.addins = $addins

# ---- the durable record ------------------------------------------------------
if (Test-Path -LiteralPath $statusPath) {
    try {
        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        $report.durable_record = [ordered]@{
            state   = $status.state
            version = $status.health.horizun_version
            commit  = if ($status.health.horizun_commit) { ([string]$status.health.horizun_commit).Substring(0, 12) } else { $null }
        }
    } catch { $report.durable_record = 'unreadable: ' + $_.Exception.Message }
} else { $report.durable_record = 'absent' }

# ---- MCP client registrations (presence only - contents carry personal paths)
$clients = [ordered]@{}
foreach ($probe in @(
    @{ name = 'claude'; path = Join-Path $env:USERPROFILE '.claude.json' },
    @{ name = 'codex';  path = Join-Path $env:USERPROFILE '.codex\config.toml' })) {
    $p = if ($SimulateFresh) { Join-Path $dataRoot ($probe.name + '-config') } else { $probe.path }
    if (-not (Test-Path -LiteralPath $p)) { $clients[$probe.name] = 'no_config'; continue }
    $text = Get-Content -LiteralPath $p -Raw
    $clients[$probe.name] = if ($text -match 'horizun-revit|horizun-mcp\.exe') { 'registered' } else { 'config_without_horizun' }
}
$report.mcp_clients = $clients

# ---- verdict -----------------------------------------------------------------
$problems = @()
if ($report.server -eq 'not_installed') { $problems += 'server_not_installed' }
else {
    if ($report.server.signature -ne 'Valid') { $problems += 'server_signature_' + $report.server.signature }
}
$installedYears = @($addins.Keys | Where-Object { $addins[$_] -ne 'not_installed' })
if ($installedYears.Count -eq 0) { $problems += 'no_addins_installed' }
foreach ($year in $installedYears) {
    if ($addins[$year].signature -ne 'Valid') { $problems += "addin_${year}_signature_" + $addins[$year].signature }
}
if ($report.durable_record -eq 'absent' -and $installedYears.Count -gt 0) { $problems += 'durable_record_absent' }
$report.problems = $problems
$report.verdict = if ($report.server -eq 'not_installed' -and $installedYears.Count -eq 0) { 'not_installed' }
                  elseif ($problems.Count -eq 0) { 'healthy_on_disk' }
                  else { 'needs_attention' }
$report.verdict_note = 'healthy_on_disk claims DISK state only: binaries present, signed, recorded. ' +
                       'Whether a live Revit pairs with them is horizun_health''s question, not this script''s.'

# ---- sandbox repair: copy known-good bytes back, then RE-DIAGNOSE. ----------
# The claim is never "repair ran" - it is what a re-read of every byte shows.
if ($Repair -and $sandboxed -and $problems.Count -gt 0) {
    if (-not $RepairSource) { $RepairSource = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server' }
    $actions = @()
    $goodServer = Join-Path $RepairSource 'horizun-mcp.exe'
    if ((Test-Path -LiteralPath $goodServer)) {
        New-Item -ItemType Directory -Force $installDir | Out-Null
        Copy-Item -LiteralPath $goodServer -Destination (Join-Path $installDir 'horizun-mcp.exe') -Force
        $actions += 'server restored from repair source'
    } else { $actions += 'SKIPPED server: repair source has no horizun-mcp.exe' }
    if ($AddinsRoot) {
        foreach ($year in 2023..2027) {
            $goodDll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun\Horizun.Revit.dll"
            $target = Join-Path $AddinsRoot "$year\Horizun.Revit.dll"
            if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath (Split-Path $target))) {
                if (Test-Path -LiteralPath $goodDll) {
                    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                    Copy-Item -LiteralPath $goodDll -Destination $target -Force
                    $actions += "addin $year restored"
                }
            }
        }
    }
    $report.repair_actions = $actions
    $report.repair_note = 'Sandbox repair copies known-good bytes and CLAIMS NOTHING: re-run this diagnostic ' +
                          'against the same roots; only its re-read verdict counts.'
}
elseif ($Repair -and -not $SimulateFresh -and -not $sandboxed -and $problems.Count -gt 0) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $actions = @()
    if ($problems | Where-Object { $_ -match 'signature|not_installed' }) {
        if (Test-Path (Join-Path $repoRoot 'scripts\deploy-both.ps1')) {
            $actions += 'deploy-both + self-sign (repo tree present)'
            & (Join-Path $repoRoot 'scripts\deploy-both.ps1') | Out-Null
            $thumb = '915653523FEA808D798B9787BDA08E4B519BFDBE'
            & (Join-Path $repoRoot 'scripts\self-sign.ps1') -Thumbprint $thumb | Out-Null
        } else { $actions += 'SKIPPED binary repair: no repo tree beside this script' }
    }
    if ($problems -contains 'durable_record_absent') {
        if (Test-Path (Join-Path $repoRoot 'scripts\refresh-install-status.ps1')) {
            $actions += 'refresh-install-status'
            & (Join-Path $repoRoot 'scripts\refresh-install-status.ps1') | Out-Null
        }
    }
    $report.repair_actions = $actions
    $report.repair_note = 'Re-run this diagnostic to VERIFY the repair; repair reports what it ran, not that it worked.'
}

if ($SimulateFresh) {
    try { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

# ---- emit --------------------------------------------------------------------
$reportJson = $report | ConvertTo-Json -Depth 6
if ($Json) {
    $dir = Split-Path $Json -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [IO.File]::WriteAllText($Json, $reportJson, [Text.UTF8Encoding]::new($false))
}
$reportJson

# The sanitization self-check is part of the run, not a promise: the emitted
# text must not carry the user name or profile root.
if ($env:USERNAME -and $reportJson -match [regex]::Escape("\$($env:USERNAME)\")) {
    Write-Error 'SANITIZATION FAILED: the report carries the user name. Do not share it.'
    exit 3
}
exit $(if ($report.verdict -eq 'needs_attention') { 1 } else { 0 })
