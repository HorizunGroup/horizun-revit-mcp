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
    # Remove only this named entry, preserving every other server. Intended for
    # advanced uninstall; unlike -Rollback it does not restore an old whole file.
    [switch]$Remove,
    # Edit even though the client is running. It will probably be overwritten.
    [switch]$Force,
    # Installer convenience: when Both is requested, operate on whichever of the
    # two clients actually has a config file. The normal CLI default remains
    # strict so a misspelled/missing config is not silently ignored.
    [switch]$SkipMissingClients,
    [switch]$WhatIfOnly,
    [string]$Json,
    # By default, registering also removes what an earlier name or install left
    # behind FOR THIS PRODUCT in the same client (the retired names `horizun` and
    # `horizun-next`, or any other name pointing at the same executable), so two
    # entries can never launch two instances. This keeps them instead - what you
    # want to run two builds side by side on purpose.
    [switch]$KeepOtherEntries,
    # Claude Code CLI used to remove legacy user-scope entries. ~/.claude.json is
    # rewritten by Claude Code while it runs, so it is never edited by hand for a
    # removal. Default: `claude` on PATH. A test seam as much as an override.
    [string]$ClaudeCli
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toml-section.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-legacy-registrations.lib.ps1')

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
        # THIS IS NOT A FAILURE ON MOST MACHINES. Claude Code and Codex are
        # command-line tools; Claude DESKTOP is a different product with its own
        # configuration, and somebody who installed only the desktop app lands
        # here and reads a red line about clients they never installed. Name the
        # shortcut that does apply to them instead.
        Write-Host ''
        Write-Host 'Neither Claude Code nor Codex CLI is set up on this machine.' -ForegroundColor Yellow
        Write-Host '  Those are the two COMMAND-LINE clients, and this helper only configures those.'
        Write-Host '  Checked:'
        Write-Host "    $claudeConfig"
        Write-Host "    $codexConfig"
        Write-Host ''
        Write-Host '  If what you use is the Claude DESKTOP app, this is the wrong helper:' -ForegroundColor Cyan
        Write-Host '  run "Conectar Horizun con Claude Desktop" from the Start menu instead.' -ForegroundColor Cyan
        Write-Host '  If you do use Claude Code or Codex, start it once so it writes its'
        Write-Host '  configuration file, close it, and run this again.'
        Write-Host ''
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
if ($Rollback -and $Remove) {
    Write-Host '-Rollback and -Remove are mutually exclusive.' -ForegroundColor Red
    exit 2
}
if (-not $Rollback -and -not $Remove) {
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
    $b = "$path.horizun-bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
    Copy-Item $path $b -Force
    return $b
}

# One entry per file. The deferred completion restores a failed registration from
# this list by comparing each file's hash with the one recorded here, so two
# writes to the same file must collapse into one that keeps the OLDEST backup and
# the NEWEST hash.
function Add-SuccessfulWrite($client, $path, $backup) {
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash
    $prev = $successfulWrites | Where-Object { $_.Path -eq $path } | Select-Object -First 1
    if ($prev) { $prev.CurrentHash = $hash; return }
    $successfulWrites.Add([pscustomobject]@{ Client = $client; Path = $path; Backup = $backup; CurrentHash = $hash }) | Out-Null
}

function Warn-Action($client, $what, $detail) {
    $actions.Add([pscustomobject]@{ client = $client; action = $what; ok = $true; detail = $detail }) | Out-Null
    Say ("{0,-7} {1} - {2}" -f $client, $what, $detail) 'Yellow'
}

# --- legacy entries of this product: Claude Code ---------------------------------
#
# Looks at ~/.claude.json read-only and removes through `claude mcp remove`, never
# by editing the file: Claude Code rewrites it from memory while it runs, and a
# hand edit is the one that gets lost. Only user-scope entries are removed; an
# entry scoped to a project is reported with the command that removes it.
function Remove-ClaudeLegacyEntries {
    $cfg = Get-Content $claudeConfig -Raw | ConvertFrom-Json
    $found = @(Get-HorizunJsonLegacyEntries -Servers $cfg.mcpServers -CurrentName $Name -ServerPath $ServerPath)
    $targets = @($found | Where-Object { $_.remove })
    foreach ($k in @($found | Where-Object { $_.skipped })) {
        Say "Claude  kept '$($k.name)': a retired name, but it launches another program" 'Yellow'
    }
    if ($cfg.PSObject.Properties['projects'] -and $cfg.projects) {
        foreach ($proj in @($cfg.projects.PSObject.Properties)) {
            if (-not ($proj.Value -and $proj.Value.PSObject.Properties['mcpServers'])) { continue }
            foreach ($e in @(Get-HorizunJsonLegacyEntries -Servers $proj.Value.mcpServers -CurrentName $Name -ServerPath $ServerPath | Where-Object { $_.remove })) {
                Warn-Action 'Claude' "project-scope '$($e.name)' in $($proj.Name) NOT removed" `
                    "it launches this product too; from that folder run: claude mcp remove $($e.name) --scope local (or --scope project)"
            }
        }
    }
    if ($targets.Count -eq 0) { Say 'Claude  no legacy entry of this product' 'DarkGray'; return }

    $list = ($targets | ForEach-Object { "$($_.name) [$($_.reason)]" }) -join ', '
    if ($WhatIfOnly) { Act 'Claude' "would remove legacy: $list" $true $null; return }

    $cli = $ClaudeCli
    if (-not $cli) {
        $c = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($c) { $cli = $c.Source }
    }
    if ($cli -and -not (Test-Path -LiteralPath $cli)) { $cli = $null }
    if (-not $cli) {
        foreach ($t in $targets) {
            Warn-Action 'Claude' "legacy '$($t.name)' NOT removed" "the claude CLI was not found; run: claude mcp remove $($t.name) --scope user"
        }
        return
    }

    $before = @($cfg.mcpServers.PSObject.Properties.Name)
    $backup = Backup $claudeConfig
    foreach ($t in $targets) {
        $null = & $cli mcp remove $t.name --scope user 2>&1
        if ($LASTEXITCODE -ne 0) {
            Warn-Action 'Claude' "legacy '$($t.name)' NOT removed" "claude mcp remove exited $LASTEXITCODE; run it by hand: claude mcp remove $($t.name) --scope user"
        }
    }
    $after = @((Get-Content $claudeConfig -Raw | ConvertFrom-Json).mcpServers.PSObject.Properties.Name)
    $gone = @($targets | Where-Object { $_.name -notin $after } | ForEach-Object { $_.name })
    $lost = @($before | Where-Object { $_ -notin $after -and $_ -notin $gone })
    if ($lost.Count -gt 0) {
        Copy-Item $backup $claudeConfig -Force
        Act 'Claude' 'remove legacy entries' $false ("the CLI also removed " + ($lost -join ', ') + ' - restored the backup')
        return
    }
    if ($gone.Count -gt 0) {
        Act 'Claude' ("removed legacy: " + ($gone -join ', ') + "; backup $(Split-Path -Leaf $backup)") $true $null
        Add-SuccessfulWrite 'Claude' $claudeConfig $backup
    }
}

function ClientIsRunning($which) {
    $pattern = if ($which -eq 'Claude') { '(?i)^claude' } else { '(?i)^codex' }
    return @(Get-Process | Where-Object { $_.ProcessName -match $pattern }).Count -gt 0
}

# --- targeted removal ----------------------------------------------------------
if ($Remove) {
    Write-Host ""
    Write-Host "Removing only '$Name' from client configuration" -ForegroundColor Cyan

    if ($Client -eq 'Both' -or $Client -eq 'Claude') {
        if (-not (Test-Path $claudeConfig)) {
            if ($SkipMissingClients) { Act 'Claude' 'entry already absent' $true "no config at $claudeConfig" }
            else { Act 'Claude' 'remove the entry' $false "no config at $claudeConfig" }
        }
        elseif ((ClientIsRunning 'Claude') -and -not $Force) {
            Act 'Claude' 'remove the entry' $false 'Claude is RUNNING and could restore the entry from memory. Close it and re-run.'
        }
        else {
            try {
                $cfg = Get-Content $claudeConfig -Raw | ConvertFrom-Json
                $present = $cfg.mcpServers -and ($Name -in @($cfg.mcpServers.PSObject.Properties.Name))
                if (-not $present) { Act 'Claude' 'entry already absent' $true $null }
                elseif ($WhatIfOnly) { Act 'Claude' "would remove mcpServers.$Name" $true $null }
                else {
                    $backup = Backup $claudeConfig
                    $cfg.mcpServers.PSObject.Properties.Remove($Name)
                    $out = $cfg | ConvertTo-Json -Depth 100
                    $null = $out | ConvertFrom-Json
                    Set-Content -Path $claudeConfig -Value $out -Encoding UTF8
                    $after = Get-Content $claudeConfig -Raw | ConvertFrom-Json
                    if ($after.mcpServers -and ($Name -in @($after.mcpServers.PSObject.Properties.Name))) {
                        Copy-Item $backup $claudeConfig -Force
                        Act 'Claude' 'remove the entry' $false 'entry remained after writing; backup restored'
                    }
                    else {
                        Act 'Claude' "removed '$Name'; backup $(Split-Path -Leaf $backup)" $true $null
                        $successfulWrites.Add([pscustomobject]@{ Client='Claude'; Path=$claudeConfig; Backup=$backup; CurrentHash=(Get-FileHash $claudeConfig -Algorithm SHA256).Hash }) | Out-Null
                    }
                }
            }
            catch { Act 'Claude' 'remove the entry' $false $_.Exception.Message }
        }
    }

    if ($Client -eq 'Both' -or $Client -eq 'Codex') {
        if (-not (Test-Path $codexConfig)) {
            if ($SkipMissingClients) { Act 'Codex' 'entry already absent' $true "no config at $codexConfig" }
            else { Act 'Codex' 'remove the entry' $false "no config at $codexConfig" }
        }
        elseif ((ClientIsRunning 'Codex') -and -not $Force) {
            Act 'Codex' 'remove the entry' $false 'Codex is RUNNING and could restore the entry from memory. Close it and re-run.'
        }
        else {
            try {
                $lines = @(Get-Content $codexConfig)
                $header = "[mcp_servers.$Name]"
                $range = Get-HorizunTomlTableRange $lines $header $Name
                $startIdx = if ($range) { $range.Start } else { -1 }
                if ($startIdx -lt 0) { Act 'Codex' 'entry already absent' $true $null }
                elseif ($WhatIfOnly) { Act 'Codex' "would remove $header" $true $null }
                else {
                    $endIdx = $range.EndExclusive
                    $new = @()
                    if ($startIdx -gt 0) { $new += $lines[0..($startIdx-1)] }
                    if ($endIdx -lt $lines.Count) { $new += $lines[$endIdx..($lines.Count-1)] }
                    $backup = Backup $codexConfig
                    Set-Content -Path $codexConfig -Value $new -Encoding UTF8
                    if ((Get-Content $codexConfig -Raw) -match [regex]::Escape($header)) {
                        Copy-Item $backup $codexConfig -Force
                        Act 'Codex' 'remove the entry' $false 'table remained after writing; backup restored'
                    }
                    else {
                        Act 'Codex' "removed '$Name'; backup $(Split-Path -Leaf $backup)" $true $null
                        $successfulWrites.Add([pscustomobject]@{ Client='Codex'; Path=$codexConfig; Backup=$backup; CurrentHash=(Get-FileHash $codexConfig -Algorithm SHA256).Hash }) | Out-Null
                    }
                }
            }
            catch { Act 'Codex' 'remove the entry' $false $_.Exception.Message }
        }
    }
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

if (-not $Remove) {
    Write-Host ""
    Write-Host "Registering '$Name' beside what is already configured" -ForegroundColor Cyan
    Write-Host "  server:      $ServerPath"
    # Labelled, because an unexplained 64-character hex line looks like an error
    # code to the person watching this window.
    Write-Host ("  fingerprint: sha256 " + (Get-FileHash $ServerPath -Algorithm SHA256).Hash.ToLower())
    Write-Host "               (identifies the exact build being registered; nothing is wrong)" -ForegroundColor DarkGray
    Write-Host ""
}

# --- Claude: JSON --------------------------------------------------------------
if (-not $Remove -and ($Client -eq 'Both' -or $Client -eq 'Claude')) {
    if (-not (Test-Path $claudeConfig)) { Act 'Claude' 'add the entry' $false "no config at $claudeConfig" }
    elseif ((ClientIsRunning 'Claude') -and -not $Force) {
        Act 'Claude' 'add the entry' $false `
            'Claude is RUNNING. It rewrites ~/.claude.json from memory, so this edit would be lost silently and the tool would simply never appear. Close every Claude window and re-run (or pass -Force to write anyway).'
    }
    else {
        try {
            if (-not $KeepOtherEntries) { Remove-ClaudeLegacyEntries }
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
                    Add-SuccessfulWrite 'Claude' $claudeConfig $backup
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
if (-not $Remove -and ($Client -eq 'Both' -or $Client -eq 'Codex')) {
    if (-not (Test-Path $codexConfig)) { Act 'Codex' 'add the entry' $false "no config at $codexConfig" }
    elseif ((ClientIsRunning 'Codex') -and -not $Force) {
        Act 'Codex' 'add the entry' $false `
            'Codex is RUNNING. Close it and re-run (or pass -Force to write anyway).'
    }
    else {
        try {
            $file = Read-HorizunTextFile $codexConfig
            $lines = @(Split-HorizunTextLines $file.Text)
            $header = "[mcp_servers.$Name]"

            # Legacy entries of THIS product go in the same edit as the new one:
            # one backup, one write, one verification.
            $legacyRemoved = @()
            $workLines = $lines
            if (-not $KeepOtherEntries) {
                $cleanup = Remove-HorizunTomlLegacySections -Lines $lines -CurrentName $Name -ServerPath $ServerPath
                foreach ($k in @($cleanup.Skipped)) {
                    Say "Codex   kept '$($k.Name)': a retired name, but it launches another program" 'Yellow'
                }
                if ($cleanup.Error) { throw "legacy cleanup refused, nothing written: $($cleanup.Error)" }
                $legacyRemoved = @($cleanup.Removed)
                $workLines = @($cleanup.Lines)
            }

            $range = Get-HorizunTomlTableRange $workLines $header $Name
            $startIdx = if ($range) { $range.Start } else { -1 }

            $body = @(
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
                # the new one. The explanatory comment is written only when the
                # table is first added, so a re-run does not stack copies of it.
                $endIdx = $range.EndExclusive
                $new = @()
                if ($startIdx -gt 0) { $new += $workLines[0..($startIdx - 1)] }
                $new += $body
                if ($endIdx -lt $workLines.Count) {
                    # Keep the separator the table had before the next one.
                    if ($workLines[$endIdx - 1].Trim() -eq '') { $new += '' }
                    $new += $workLines[$endIdx..($workLines.Count - 1)]
                }
            }
            else {
                $new = @($workLines) + @('', '# Horizun MCP - added by scripts/register-client.ps1.',
                    '# Registered BESIDE the existing servers, which are untouched.') + $body
            }

            $removedText = ($legacyRemoved | ForEach-Object { "$($_.Name) [$($_.Reason)]" }) -join ', '
            $unchanged = (($new -join "`n") -ceq ($lines -join "`n"))

            if ($WhatIfOnly) {
                $verb = if ($unchanged) { 'keep (already registered)' } elseif ($startIdx -ge 0) { 'replace' } else { 'append' }
                Act 'Codex' ("would $verb $header" + $(if ($legacyRemoved.Count) { "; would remove legacy: $removedText" } else { '' })) $true $null
            }
            elseif ($unchanged) {
                Act 'Codex' "'$Name' already registered as written; no legacy entry; nothing changed" $true $null
            }
            else {
                # TOML check before anything is written: when a Python with
                # tomllib is at hand, a file that parsed must still parse.
                if ($legacyRemoved.Count -gt 0) {
                    $was = Test-HorizunTomlWithPython -Lines $lines
                    if ($was -eq $true) {
                        $now = Test-HorizunTomlWithPython -Lines $new
                        if ($now -eq $false) { throw 'the result would not parse as TOML - nothing written' }
                    }
                }

                $backup = Backup $codexConfig
                Write-HorizunTextFile $codexConfig $new $file.Bom $file.NewLine

                # Structural check. Not a TOML parse - there is none here - but it
                # catches the failures this edit can actually cause: the table
                # missing, another server's table lost, or a legacy table left.
                $afterLines = @(Split-HorizunTextLines (Read-HorizunTextFile $codexConfig).Text)
                $namesBefore = @(Get-HorizunTomlMcpSections -Lines $lines | ForEach-Object { $_.Name })
                $namesAfter = @(Get-HorizunTomlMcpSections -Lines $afterLines | ForEach-Object { $_.Name })
                $removedNames = @($legacyRemoved | ForEach-Object { $_.Name })
                $lost = @($namesBefore | Where-Object { $_ -notin $namesAfter -and $_ -notin $removedNames })
                $left = @($removedNames | Where-Object { $_ -in $namesAfter })

                if ($Name -notin $namesAfter) {
                    Copy-Item $backup $codexConfig -Force
                    Act 'Codex' 'add the entry' $false 'the table was not there after writing - restored the backup'
                }
                elseif ($lost.Count -gt 0) {
                    Copy-Item $backup $codexConfig -Force
                    Act 'Codex' 'add the entry' $false ("it would have removed " + ($lost -join ', ') + " - restored the backup")
                }
                elseif ($left.Count -gt 0) {
                    Copy-Item $backup $codexConfig -Force
                    Act 'Codex' 'add the entry' $false ("legacy table(s) remained after writing: " + ($left -join ', ') + " - restored the backup")
                }
                else {
                    if ($legacyRemoved.Count -gt 0) { Act 'Codex' "removed legacy: $removedText" $true $null }
                    Act 'Codex' "added '$Name'; $($namesAfter.Count) server table(s) present; backup $(Split-Path -Leaf $backup)" $true $null
                    Add-SuccessfulWrite 'Codex' $codexConfig $backup
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
        remove        = [bool]$Remove
        actions       = $actions
        problems      = $problems
        writes        = $successfulWrites
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

if (-not $WhatIfOnly -and -not $Remove) {
    Write-Host "  RESTART both clients for the entry to be picked up." -ForegroundColor Cyan
    Write-Host "  Then, from each: tools/list, horizun_target, horizun_health - and compare data_root."
}
exit 0
