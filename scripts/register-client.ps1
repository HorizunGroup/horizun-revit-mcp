#Requires -Version 5.1
<#
  Register Horizun in Codex and Claude, IN PARALLEL WITH WHAT IS ALREADY THERE.

  The point of this script is what it does NOT do. It adds one entry and touches
  nothing else: aec-model-bridge, aec-model-bridge-2025, rvt-mcp, civil3d-mcp
  and every other server stay exactly as they are. A cutover is a decision taken
  with evidence in front of you, not a side effect of installing something.

  It registers under its own name - `horizun-revit` - so it sits beside whatever
  else is there rather than replacing it. Two entries can point at two different
  binaries; one entry cannot be in two states.

  THE NAME WAS `horizun-next` UNTIL 0.3.0, from the months when this build was the
  candidate sitting beside a shipped one, then plain `horizun` from 0.3.0. A name
  with no product in it reads fine alone but stops meaning anything on a machine
  that also has civil3d-mcp, navisworks and half a dozen other bridges registered
  - `horizun-revit` is the one that still says what it is from the client's own
  server list. Pass -Name to keep an old entry, or to run two builds side by
  side - which is what the parameter is for and was never the reason for the
  default.

  THE HAZARD THIS EXISTS TO HANDLE. Both clients rewrite their configuration file
  from memory while they run, so an edit made underneath a running client is lost
  the next time it saves - silently, and the symptom is "the tool never appeared".
  Measured here: ~/.claude.json was rewritten four minutes into an editing
  session. So a running client is REFUSED by default rather than edited
  hopefully.

  Backups are taken before every write and -Rollback puts the newest one back, so
  nothing here is a one-way door.

    scripts/register-client.ps1                    # both, using the installed exe
    scripts/register-client.ps1 -Client Codex
    scripts/register-client.ps1 -Rollback          # undo, newest backup first
    scripts/register-client.ps1 -WhatIfOnly        # show the change, write nothing

  Exit codes:  0 done   1 refused or failed   2 could not run
#>
[CmdletBinding()]
param(
    [string]$Name = 'horizun-revit',
    [ValidateSet('Claude', 'Codex', 'Both')]
    [string]$Client = 'Both',
    [string]$ServerPath,
    [switch]$Rollback,
    # Edit even though the client is running. It will probably be overwritten.
    [switch]$Force,
    # Installer convenience: when Both is requested, operate on whichever of the
    # two clients actually has a config file. The normal CLI default remains
    # strict so a misspelled/missing config is not silently ignored.
    [switch]$SkipMissingClients,
    [switch]$WhatIfOnly,
    [string]$Json
)
$ErrorActionPreference = 'Stop'

if ($Name -notmatch '^[A-Za-z0-9_-]{1,64}$') {
    Write-Host 'Name must contain only ASCII letters, digits, underscore or hyphen (1..64 characters).' -ForegroundColor Red
    exit 2
}

$claudeConfig = Join-Path $env:USERPROFILE '.claude.json'
$codexConfig  = Join-Path $env:USERPROFILE '.codex\config.toml'

if ($Client -eq 'Both' -and $SkipMissingClients) {
    $hasClaude = Test-Path $claudeConfig
    $hasCodex = Test-Path $codexConfig
    if (-not $hasClaude -and -not $hasCodex) {
        Write-Host 'Neither Claude nor Codex has an existing configuration file. Start the client once, close it, and run this helper again.' -ForegroundColor Red
        exit 2
    }
    if ($hasClaude -and -not $hasCodex) { $Client = 'Claude' }
    elseif ($hasCodex -and -not $hasClaude) { $Client = 'Codex' }
}

$actions  = New-Object System.Collections.Generic.List[object]
$problems = New-Object System.Collections.Generic.List[string]
$successfulWrites = New-Object System.Collections.Generic.List[object]

function Say($m, $c = 'Gray') { Write-Host "  $m" -ForegroundColor $c }
function Act($client, $what, $ok, $detail) {
    $actions.Add([pscustomobject]@{ client = $client; action = $what; ok = [bool]$ok; detail = $detail }) | Out-Null
    if ($ok) { Say ("{0,-7} {1}" -f $client, $what) 'Green' }
    else { Say ("{0,-7} {1} - {2}" -f $client, $what, $detail) 'Red'; $problems.Add("$client : $what : $detail") | Out-Null }
}

# --- the binary ---------------------------------------------------------------
if (-not $ServerPath) { $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
if (-not $Rollback) {
    if (-not (Test-Path $ServerPath)) {
        Write-Host "The installed server is not there: $ServerPath" -ForegroundColor Red
        Write-Host "Install the release first. Registering a path that does not exist gives a client that fails at startup"
        Write-Host "with a message about the client rather than about the missing file."
        exit 2
    }
    # A bin/Release path here is how a client ends up pinned to a developer build
    # for weeks. Named, not blocked: it is a legitimate thing to do deliberately.
    if ($ServerPath -match '(?i)[\\/]bin[\\/]Release[\\/]') {
        Say "WARNING: registering a DEVELOPER build, not the installed artifact:" 'Yellow'
        Say "         $ServerPath" 'Yellow'
    }
}

function Backup($path) {
    if (-not (Test-Path $path)) { return $null }
    $b = "$path.horizun-bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $path $b -Force
    return $b
}

function ClientIsRunning($which) {
    $pattern = if ($which -eq 'Claude') { '(?i)^claude' } else { '(?i)^codex' }
    return @(Get-Process | Where-Object { $_.ProcessName -match $pattern }).Count -gt 0
}

# --- rollback -----------------------------------------------------------------
if ($Rollback) {
    Write-Host ""
    Write-Host "Rolling back client configuration" -ForegroundColor Cyan
    foreach ($t in @(
        @{ Name = 'Claude'; Path = $claudeConfig },
        @{ Name = 'Codex';  Path = $codexConfig })) {

        if ($Client -ne 'Both' -and $Client -ne $t.Name) { continue }

        $backups = @(Get-ChildItem (Split-Path -Parent $t.Path) -Filter ((Split-Path -Leaf $t.Path) + '.horizun-bak-*') `
                     -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        if ($backups.Count -eq 0) { Act $t.Name 'restore a backup' $false 'no backup taken by this script was found'; continue }

        if ((ClientIsRunning $t.Name) -and -not $Force) {
            Act $t.Name 'restore a backup' $false "$($t.Name) is RUNNING - it would overwrite the restored file from memory. Close it and re-run."
            continue
        }

        if ($WhatIfOnly) { Act $t.Name "would restore $($backups[0].Name)" $true $null; continue }
        Copy-Item $backups[0].FullName $t.Path -Force
        Act $t.Name "restored $($backups[0].Name)" $true $null
    }
    if ($problems.Count -gt 0) { exit 1 }
    exit 0
}

Write-Host ""
Write-Host "Registering '$Name' beside what is already configured" -ForegroundColor Cyan
Write-Host "  server: $ServerPath"
Write-Host ("  sha256: " + (Get-FileHash $ServerPath -Algorithm SHA256).Hash.ToLower())
Write-Host ""

# --- Claude: JSON --------------------------------------------------------------
if ($Client -eq 'Both' -or $Client -eq 'Claude') {
    if (-not (Test-Path $claudeConfig)) { Act 'Claude' 'add the entry' $false "no config at $claudeConfig" }
    elseif ((ClientIsRunning 'Claude') -and -not $Force) {
        Act 'Claude' 'add the entry' $false `
            'Claude is RUNNING. It rewrites ~/.claude.json from memory, so this edit would be lost silently and the tool would simply never appear. Close every Claude window and re-run (or pass -Force to write anyway).'
    }
    else {
        try {
            $raw = Get-Content $claudeConfig -Raw
            $cfg = $raw | ConvertFrom-Json
            if (-not $cfg.mcpServers) { $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) -Force }

            $existing = @($cfg.mcpServers.PSObject.Properties.Name)
            $entry = [pscustomobject]@{ command = $ServerPath; args = @(); env = [pscustomobject]@{} }

            if ($WhatIfOnly) {
                Act 'Claude' "would set mcpServers.$Name (leaving $($existing.Count) existing entries untouched)" $true $null
            }
            else {
                $backup = Backup $claudeConfig
                $cfg.mcpServers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry -Force

                # Depth matters: the default is 2 and this file nests far deeper.
                # A shallow write turns whole objects into the literal text
                # "System.Object[]" and destroys the rest of the configuration.
                $out = $cfg | ConvertTo-Json -Depth 100

                # Parse what we are about to write BEFORE replacing the original.
                # A config that does not parse is a client that starts with none.
                $null = $out | ConvertFrom-Json
                Set-Content -Path $claudeConfig -Value $out -Encoding UTF8

                $after = @((Get-Content $claudeConfig -Raw | ConvertFrom-Json).mcpServers.PSObject.Properties.Name)
                $lost = @($existing | Where-Object { $_ -notin $after })
                if ($lost.Count -gt 0) {
                    Copy-Item $backup $claudeConfig -Force
                    Act 'Claude' 'add the entry' $false ("it would have removed " + ($lost -join ', ') + " - restored the backup and changed nothing")
                }
                elseif ($Name -notin $after) {
                    Copy-Item $backup $claudeConfig -Force
                    Act 'Claude' 'add the entry' $false 'the entry was not there after writing - restored the backup'
                }
                else {
                    Act 'Claude' "added '$Name'; $($existing.Count) existing entries intact; backup $(Split-Path -Leaf $backup)" $true $null
                    $successfulWrites.Add([pscustomobject]@{ Client = 'Claude'; Path = $claudeConfig; Backup = $backup }) | Out-Null
                }
            }
        }
        catch { Act 'Claude' 'add the entry' $false $_.Exception.Message }
    }
}

# --- Codex: TOML ---------------------------------------------------------------
#
# Appended as a whole table rather than parsed and rewritten. There is no TOML
# writer in Windows PowerShell, and a hand-rolled one would be a new way to
# corrupt a file that carries several other servers. A table can appear anywhere
# in a TOML document, so appending is both valid and the smallest possible edit.
if ($Client -eq 'Both' -or $Client -eq 'Codex') {
    if (-not (Test-Path $codexConfig)) { Act 'Codex' 'add the entry' $false "no config at $codexConfig" }
    elseif ((ClientIsRunning 'Codex') -and -not $Force) {
        Act 'Codex' 'add the entry' $false `
            'Codex is RUNNING. Close it and re-run (or pass -Force to write anyway).'
    }
    else {
        try {
            $lines = @(Get-Content $codexConfig)
            $header = "[mcp_servers.$Name]"
            $startIdx = -1
            for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq $header) { $startIdx = $i; break } }

            $block = @(
                '',
                "# Horizun MCP - added by scripts/register-client.ps1.",
                "# Registered BESIDE the existing servers, which are untouched.",
                $header,
                ('command = ' + (ConvertTo-Json -InputObject ([string]$ServerPath) -Compress)),
                'args = []',
                # The scan of a large model and the first call after a cold start
                # both exceed a short default, and a timeout here reads as a broken
                # bridge rather than a slow one.
                'startup_timeout_sec = 120',
                'tool_timeout_sec = 600'
            )

            if ($startIdx -ge 0) {
                # Replace the existing table: from its header to the next
                # top-level table header, so its keys are not left orphaned under
                # the new one.
                $endIdx = $lines.Count
                for ($i = $startIdx + 1; $i -lt $lines.Count; $i++) {
                    if ($lines[$i].Trim() -match '^\[[^\]]+\]$' -and $lines[$i].Trim() -notmatch "^\[mcp_servers\.$([regex]::Escape($Name))\.") { $endIdx = $i; break }
                }
                $new = @()
                if ($startIdx -gt 0) { $new += $lines[0..($startIdx - 1)] }
                $new += $block
                if ($endIdx -lt $lines.Count) { $new += $lines[$endIdx..($lines.Count - 1)] }
            }
            else { $new = $lines + $block }

            if ($WhatIfOnly) {
                Act 'Codex' ("would " + $(if ($startIdx -ge 0) { 'replace' } else { 'append' }) + " $header") $true $null
            }
            else {
                $backup = Backup $codexConfig
                Set-Content -Path $codexConfig -Value $new -Encoding UTF8

                # Structural check. Not a TOML parse - there is none here - but it
                # catches the failures this edit can actually cause: the table
                # missing, or another server's table lost.
                $afterText = Get-Content $codexConfig -Raw
                $othersBefore = @($lines | Where-Object { $_.Trim() -match '^\[mcp_servers\.[^.\]]+\]$' } | ForEach-Object { $_.Trim() })
                $othersAfter = @(Get-Content $codexConfig | Where-Object { $_.Trim() -match '^\[mcp_servers\.[^.\]]+\]$' } | ForEach-Object { $_.Trim() })
                $lost = @($othersBefore | Where-Object { $_ -notin $othersAfter })

                if ($afterText -notmatch [regex]::Escape($header)) {
                    Copy-Item $backup $codexConfig -Force
                    Act 'Codex' 'add the entry' $false 'the table was not there after writing - restored the backup'
                }
                elseif ($lost.Count -gt 0) {
                    Copy-Item $backup $codexConfig -Force
                    Act 'Codex' 'add the entry' $false ("it would have removed " + ($lost -join ', ') + " - restored the backup")
                }
                else {
                    Act 'Codex' "added '$Name'; $($othersAfter.Count) server table(s) present; backup $(Split-Path -Leaf $backup)" $true $null
                    $successfulWrites.Add([pscustomobject]@{ Client = 'Codex'; Path = $codexConfig; Backup = $backup }) | Out-Null
                }
            }
        }
        catch { Act 'Codex' 'add the entry' $false $_.Exception.Message }
    }
}

# --- report --------------------------------------------------------------------
Write-Host ""
if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        name          = $Name
        server_path   = $ServerPath
        server_sha256 = $(if (Test-Path $ServerPath) { (Get-FileHash $ServerPath -Algorithm SHA256).Hash.ToLower() } else { $null })
        what_if_only  = [bool]$WhatIfOnly
        actions       = $actions
        problems      = $problems
    } | ConvertTo-Json -Depth 6 | Out-File -FilePath $Json -Encoding utf8
    Say "wrote $Json"
}

if ($problems.Count -gt 0) {
    # A Both request is one user intention. If one client fails after the other
    # succeeded, restore every successful write so the operation is atomic
    # across the two configuration files as well as within each individual file.
    for ($i = $successfulWrites.Count - 1; $i -ge 0; $i--) {
        $write = $successfulWrites[$i]
        if ($write.Backup -and (Test-Path -LiteralPath $write.Backup)) {
            Copy-Item -LiteralPath $write.Backup -Destination $write.Path -Force
            Say "$($write.Client) restored after another requested client failed" 'Yellow'
        }
    }
    Write-Host "  Nothing was left half-done: failed writes and earlier successful writes were restored." -ForegroundColor Yellow
    exit 1
}

if (-not $WhatIfOnly) {
    Write-Host "  RESTART both clients for the entry to be picked up." -ForegroundColor Cyan
    Write-Host "  Then, from each: tools/list, horizun_target, horizun_health - and compare data_root."
}
exit 0
