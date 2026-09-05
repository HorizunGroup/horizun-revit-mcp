#Requires -Version 5.1
<#
  One screen that answers: which clients can reach this Revit, and what is missing?

  It writes nothing. It looks at every client this product knows how to talk to,
  reports what is really configured, and - where something is not - names the one
  step that would fix it.

    scripts/diagnose-integrations.ps1
    scripts/diagnose-integrations.ps1 -Json out.json

  Exit codes: 0 at least one client is configured   3 none is, and the steps are named
#>
[CmdletBinding()]
param([string]$Json, [string]$ServerPath, [string]$StatusPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-stdio.lib.ps1')
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')

if (-not $ServerPath) { $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }

function Say($m, $c = 'Gray') { Write-Host "  $m" -ForegroundColor $c }
function Head($m) { Write-Host ""; Write-Host $m -ForegroundColor Cyan }

Head 'The bridge itself'
$probe = $null
if (Test-Path -LiteralPath $ServerPath -PathType Leaf) {
    $probe = Invoke-HorizunMcpProbe -Command $ServerPath -ListTools -TimeoutSec 120
    if ($probe.ok) {
        Say ("server   {0} {1} - {2} tools, list_changed={3}" -f `
             $probe.server_info.name, $probe.server_info.version, $probe.tool_count, $probe.list_changed) 'Green'
        Say ("path     $ServerPath")
    }
    else { Say "the installed server did not complete an MCP handshake: $($probe.problem)" 'Red' }
}
else { Say "no server at $ServerPath - install Horizun Revit MCP first" 'Red' }

$revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
Say ("Revit    " + $(if ($revit.Count -gt 0) { "running ($($revit.Count) process)" } else { 'not running - every command needs a Revit with a document open' })) `
    $(if ($revit.Count -gt 0) { 'Green' } else { 'Yellow' })

$rows = New-Object System.Collections.Generic.List[object]
function Row($client, $available, $configured, $detail, $next) {
    $rows.Add([pscustomobject]@{ client = $client; available = $available; configured = $configured
                                 detail = $detail; next_step = $next }) | Out-Null
    $colour = if ($configured -eq $true) { 'Green' } elseif ($available -eq $false) { 'DarkGray' } else { 'Yellow' }
    Write-Host ("  {0,-16} {1,-14} {2}" -f $client, $(if ($configured -eq $true) { 'configured' } elseif ($available) { 'available' } else { 'not present' }), $detail) -ForegroundColor $colour
    if ($next) { Write-Host ("  {0,-16} {1,-14} -> {2}" -f '', '', $next) -ForegroundColor Cyan }
}

Head 'Clients'

# --- the two that already work, and must keep working -------------------------
foreach ($e in Get-HorizunExistingClients) {
    if (-not $e.config_exists) {
        Row $e.client $false $false "no configuration file at $($e.config_path)" $null
    }
    elseif ($e.registered) {
        Row $e.client $true $true "horizun is registered in $(Split-Path -Leaf $e.config_path)" $null
    }
    else {
        Row $e.client $true $false "configured, but no horizun entry" `
            "scripts/register-client.ps1 -Client $(if ($e.client -eq 'codex') { 'Codex' } else { 'Claude' })"
    }
}

# --- Claude Desktop ------------------------------------------------------------
$cd = Get-HorizunClaudeDesktop
if (-not $cd.installed) {
    Row 'claude-desktop' $false $false 'not installed (checked the MSIX package and %APPDATA%\Claude)' $null
}
else {
    $how = if ($cd.horizun_extension) { 'extension installed' } elseif ($cd.horizun_in_config) { 'configuration entry' } else { $null }
    if ($how) {
        Row 'claude-desktop' $true $true ("{0} ({1}, {2} install){3}" -f $how, $cd.version, $cd.packaging,
            $(if ($cd.running) { ' - RUNNING, restart it to pick up changes' } else { '' })) $null
    }
    else {
        Row 'claude-desktop' $true $false ("{0}, {1} install; {2} other extension(s) present" -f $cd.version, $cd.packaging, $cd.extensions.Count) `
            'scripts/install-claude-desktop-extension.ps1'
    }
}

# --- ChatGPT Work -------------------------------------------------------------
$tunnel = Get-HorizunTunnelClient
$integrations = Get-HorizunIntegrationStatus -StatusPath $StatusPath
$workState = $null
if ($integrations) { $workState = $integrations.PSObject.Properties['chatgpt'] }
if (-not $tunnel.installed) {
    Row 'chatgpt-work' $false $false "OpenAI tunnel-client is not installed" `
        'scripts/chatgpt-tunnel.ps1 -Status'
}
else {
    $st = if ($workState) { $workState.Value.state } else { 'unknown' }
    Row 'chatgpt-work' $true ($st -eq 'configured') "tunnel-client present; recorded state: $st" `
        $(if ($st -ne 'configured') { 'scripts/chatgpt-tunnel.ps1 -Status' } else { $null })
}

# --- permission boundary --------------------------------------------------------
Head 'Permission boundary'
Say 'horizun_execute_python is advertised by the server and REFUSED until the owner of'
Say 'this machine grants it from inside Revit. Connecting a new client never grants it.'

$configured = @($rows | Where-Object { $_.configured -eq $true })
Write-Host ""
Write-Host ("  {0} of {1} known clients are configured for this bridge." -f $configured.Count, $rows.Count) `
    -ForegroundColor $(if ($configured.Count -gt 0) { 'Green' } else { 'Yellow' })

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        generated_utc      = (Get-Date).ToUniversalTime().ToString('o')
        server_path        = $ServerPath
        server_answers_mcp = $(if ($probe) { [bool]$probe.ok } else { $false })
        server_version     = $(if ($probe -and $probe.server_info) { $probe.server_info.version } else { $null })
        server_tool_count  = $(if ($probe) { $probe.tool_count } else { 0 })
        revit_running      = ($revit.Count -gt 0)
        clients            = $rows
        configured_count   = $configured.Count
        undeterminable     = @()
    } | ConvertTo-Json -Depth 8 | Out-File -FilePath $Json -Encoding utf8
    Say "wrote $Json"
}

if ($configured.Count -eq 0) { exit 3 }
exit 0
