#Requires -Version 5.1
<#
  Administer horizun_execute_python: restore it ON, or turn it OFF.

  execute_python runs arbitrary code inside Revit with the full API and the
  rights of the signed-in user. It is DISABLED BY DEFAULT. The preferred
  interactive path is Revit's Python ON/OFF button, whose owner-approved grant
  remains active until that same user revokes it. This script provides the same
  durable choice for explicit administration:

    - ENABLE a machine deliberately with unsafe_code plus an explicit true.
    - DISABLE it deliberately with -Disable; an explicit false always wins.

  WHY A SCRIPT AND NOT "edit settings.json". The gate reads BOTH
  "permission_profile" AND "enable_execute_python" from
  %USERPROFILE%\.horizun\settings.json — and editing that file by hand tends to
  go wrong (a malformed file falls CLOSED, disabling everything; a path with
  %LOCALAPPDATA% never expands under PowerShell). This writes exactly the two
  keys, PRESERVES every other setting already in the file, and backs the file
  up first.

  IT SURVIVES UPDATES. settings.json lives under %USERPROFILE%\.horizun\, which
  install.ps1 never touches. Enable it once and re-running the installer leaves it
  enabled. Disabling is this same script with -Disable.

  The add-in re-reads settings on every call, so no Revit restart is needed for
  the gate itself. Compatible MCP clients receive notifications/tools/list_changed;
  restart the client only when it does not support dynamic tool-list refresh.

    scripts/enable-execute-python.ps1              # show the warning, ask, re-enable/restore
    scripts/enable-execute-python.ps1 -Yes         # re-enable without the prompt (automation)
    scripts/enable-execute-python.ps1 -Disable     # turn it off (the explicit switch the defaults respect)
    scripts/enable-execute-python.ps1 -WhatIfOnly  # show the change, write nothing

  Exit codes:  0 done   1 refused or failed   2 could not run
#>
[CmdletBinding()]
param(
    # Turn the capability OFF instead of on. Sets enable_execute_python=false and
    # leaves every other setting — including permission_profile — as it was.
    [switch]$Disable,
    # Skip the interactive confirmation. For automation only; the warning is still
    # printed so a log still carries it.
    [Alias('Force')]
    [switch]$Yes,
    # Where Horizun keeps its state, if not the default. Mirrors the add-in's own
    # resolution: HORIZUN_DATA_ROOT, else the user profile, + \.horizun.
    [string]$DataRoot,
    # Show what would change and exit without writing.
    [switch]$WhatIfOnly
)
$ErrorActionPreference = 'Stop'

function Resolve-DataRoot {
    if ($DataRoot) { return $DataRoot.Trim() }
    # Same order the C# HorizunPaths uses (minus the SpecialFolder API PowerShell
    # cannot reach the same way): the override variable, then the user's home.
    if ($env:HORIZUN_DATA_ROOT) { return $env:HORIZUN_DATA_ROOT.Trim() }
    $userHome = $env:USERPROFILE
    if (-not $userHome) { $userHome = "$env:HOMEDRIVE$env:HOMEPATH" }
    if (-not $userHome) {
        Write-Host 'Cannot find your home directory (USERPROFILE / HOMEDRIVE+HOMEPATH are empty).' -ForegroundColor Red
        Write-Host 'Pass -DataRoot with the folder Horizun keeps its state in, or set HORIZUN_DATA_ROOT.' -ForegroundColor Red
        exit 2
    }
    return (Join-Path $userHome '.horizun')
}

$root = Resolve-DataRoot
$settingsPath = Join-Path $root 'settings.json'
$settingsMutexName = 'Local\Horizun.Revit.Settings.V1'

function Read-Settings([string]$Path) {
    $result = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $result }
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if (-not $raw.Trim()) { return $result }
    try { $parsed = $raw | ConvertFrom-Json }
    catch {
        throw "settings.json exists but is not valid JSON: $Path`nRefusing to overwrite it. Fix or move it aside, then run this again. Nothing was changed."
    }
    foreach ($p in $parsed.PSObject.Properties) { $result[$p.Name] = $p.Value }
    return $result
}

# ---- Read what is there now, WITHOUT clobbering it. --------------------------
# A malformed file falls back to read_only on the add-in side (safe), so we must
# not silently overwrite one: that would throw away real settings we could not
# read. Stop and let a human look instead.
try { $settings = Read-Settings $settingsPath }
catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }

$currentProfile = if ($settings.Contains('permission_profile')) { [string]$settings['permission_profile'] } else { '(unset -> safe_write default)' }
$currentEnabled = if ($settings.Contains('enable_execute_python')) { [bool]$settings['enable_execute_python'] } else { $false }

# ---- The warning: what to weigh BEFORE restoring it. ------------------------
if (-not $Disable) {
    Write-Host ''
    Write-Host '  You are about to enable horizun_execute_python durably (advanced / unsafe code).' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  It is disabled by default. This durable administrative opt-in runs ARBITRARY'
    Write-Host '  CODE inside Revit with the full API'
    Write-Host '  and your Windows rights. Before you continue, weigh this:'
    Write-Host ''
    Write-Host '   - TRUSTED CLIENTS AND PROMPTS ONLY. An agent that reads untrusted content' -ForegroundColor Gray
    Write-Host '     (a client''s model, a linked DWG, a PDF, an email) can be fed injected' -ForegroundColor Gray
    Write-Host '     instructions and run them with these rights.' -ForegroundColor Gray
    Write-Host '   - LIMITED VERIFICATION. Unlike the typed commands, nothing rehearses what a' -ForegroundColor Gray
    Write-Host '     script will do. Scripts are expected to verify their own work through the' -ForegroundColor Gray
    Write-Host '     structured __output__ evidence contract, but a wrong script can still lose' -ForegroundColor Gray
    Write-Host '     work silently.' -ForegroundColor Gray
    Write-Host '   - TYPED COMMANDS FIRST. Python is the fallback for what they do not cover;' -ForegroundColor Gray
    Write-Host '     for anything recurring a typed command is still the verified path.' -ForegroundColor Gray
    Write-Host '   - REVERSIBLE. Turn it back off any time with:  this script -Disable' -ForegroundColor Gray
    Write-Host ''
    Write-Host "  File:    $settingsPath"
    Write-Host "  Now:     permission_profile=$currentProfile  enable_execute_python=$currentEnabled"
    Write-Host '  After:   permission_profile=unsafe_code  enable_execute_python=true'
    Write-Host ''
} else {
    Write-Host ''
    Write-Host '  Disabling horizun_execute_python.' -ForegroundColor Cyan
    Write-Host "  File:    $settingsPath"
    Write-Host "  Now:     permission_profile=$currentProfile  enable_execute_python=$currentEnabled"
    Write-Host '  After:   enable_execute_python=false  (permission_profile left unchanged)'
    Write-Host ''
}

if ($WhatIfOnly) {
    Write-Host 'Nothing was changed (-WhatIfOnly).' -ForegroundColor Cyan
    exit 0
}

# ---- Ask, unless told not to. -----------------------------------------------
if (-not $Yes) {
    $verb = if ($Disable) { 'Disable' } else { 'Enable' }
    $answer = Read-Host "  $verb it? Type 'yes' to proceed"
    if ($answer -ne 'yes') {
        Write-Host 'Cancelled. Nothing was changed.' -ForegroundColor Cyan
        exit 0
    }
}

# ---- Apply under the SAME inter-process mutex as Revit. ---------------------
# The preview above is informational. Re-read only after taking the mutex: two
# Revit processes and this admin script may all update the same file, and an OFF
# action must never be overwritten by an older temporary-grant snapshot.
$mutex = $null
$held = $false
$tempPath = $null
try {
    $mutex = New-Object Threading.Mutex($false, $settingsMutexName)
    try { $held = $mutex.WaitOne([TimeSpan]::FromSeconds(15)) }
    catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'Timed out waiting for another Horizun settings writer. Nothing was changed.' }

    $settings = Read-Settings $settingsPath
    if ($Disable) {
        $settings['enable_execute_python'] = $false
        $settings.Remove('execute_python_ui_granted')
        $settings.Remove('execute_python_ui_grant_until_utc')
        $settings.Remove('execute_python_ui_granted_at_utc')
    } else {
        $settings['permission_profile']    = 'unsafe_code'
        $settings['enable_execute_python'] = $true
        $settings.Remove('execute_python_ui_granted')
        $settings.Remove('execute_python_ui_grant_until_utc')
        $settings.Remove('execute_python_ui_granted_at_utc')
    }

    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Force -Path $root | Out-Null }
    if (Test-Path -LiteralPath $settingsPath) {
        $backup = "$settingsPath.horizun-bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
        Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
        Write-Host "  Backed up existing settings to: $backup" -ForegroundColor DarkGray
    }

    $json = $settings | ConvertTo-Json -Depth 20
    $tempPath = Join-Path $root ('.settings.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($tempPath, $json, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $settingsPath) {
        # Windows PowerShell 5.1's overload binder rejects a null backup path
        # even though File.Replace accepts it. Use a private same-volume backup
        # and remove it immediately; the timestamped user backup above remains.
        $replaceBackup = Join-Path $root ('.settings-replace.' + [Guid]::NewGuid().ToString('N') + '.bak')
        [IO.File]::Replace($tempPath, $settingsPath, $replaceBackup)
        Remove-Item -LiteralPath $replaceBackup -Force -ErrorAction SilentlyContinue
    } else {
        [IO.File]::Move($tempPath, $settingsPath)
    }
    $tempPath = $null

    # Verify while still owning the writer mutex. OFF means both durable false
    # and no live temporary grant; checking only the first key was a false green.
    $check = (Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8) | ConvertFrom-Json
    $grantPresent = $null -ne $check.PSObject.Properties['execute_python_ui_grant_until_utc']
    $persistentUiGrantPresent = $null -ne $check.PSObject.Properties['execute_python_ui_granted']
    $uiMetadataPresent = $null -ne $check.PSObject.Properties['execute_python_ui_granted_at_utc']
    if ($Disable) {
        if ($check.enable_execute_python -ne $false -or $grantPresent -or $persistentUiGrantPresent -or $uiMetadataPresent) {
            throw 'Wrote the file but the durable switch or UI grant metadata did not read back as OFF.'
        }
    } elseif ($check.permission_profile -ne 'unsafe_code' -or $check.enable_execute_python -ne $true -or $grantPresent -or $persistentUiGrantPresent -or $uiMetadataPresent) {
        throw 'Wrote the file but the durable unsafe-code opt-in did not read back exactly.'
    }
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    if ($tempPath -and (Test-Path -LiteralPath $tempPath)) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
    if ($held) { try { $mutex.ReleaseMutex() } catch { } }
    if ($mutex) { $mutex.Dispose() }
}

Write-Host ''
if ($Disable) {
    Write-Host 'horizun_execute_python is now DISABLED.' -ForegroundColor Green
} else {
    Write-Host 'horizun_execute_python is now ENABLED.' -ForegroundColor Green
}
Write-Host 'Compatible MCP clients refresh the tool list automatically. Restart once only if yours does not.' -ForegroundColor Yellow
exit 0
