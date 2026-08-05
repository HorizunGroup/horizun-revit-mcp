#Requires -Version 5.1
<#
  Administer horizun_execute_python: restore it ON, or turn it OFF.

  execute_python runs arbitrary code inside Revit with the full API and the
  rights of the signed-in user. It is ENABLED BY DEFAULT — a machine with no
  settings.json, or one without these keys, already exposes it — so this script
  is NOT the activation step it used to be. It exists for administration:

    - RE-ENABLE / RESTORE a machine where somebody explicitly disabled it
      (enable_execute_python=false, or a profile below unsafe_code).
    - DISABLE it deliberately with -Disable, which is the switch the defaults
      respect: an explicit false always wins over the default-on posture.

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
  the gate itself. The MCP SERVER decides whether to advertise the tool when it
  starts, so RESTART YOUR MCP CLIENT once for the tool to appear or disappear.

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

# ---- Read what is there now, WITHOUT clobbering it. --------------------------
# A malformed file falls back to read_only on the add-in side (safe), so we must
# not silently overwrite one: that would throw away real settings we could not
# read. Stop and let a human look instead.
$settings = [ordered]@{}
if (Test-Path $settingsPath) {
    $raw = Get-Content $settingsPath -Raw -Encoding UTF8
    if ($raw.Trim()) {
        try {
            $parsed = $raw | ConvertFrom-Json
        } catch {
            Write-Host "settings.json exists but is not valid JSON: $settingsPath" -ForegroundColor Red
            Write-Host 'Refusing to overwrite it. Fix or move it aside, then run this again. Nothing was changed.' -ForegroundColor Red
            exit 1
        }
        foreach ($p in $parsed.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    }
}

$currentProfile = if ($settings.Contains('permission_profile')) { [string]$settings['permission_profile'] } else { '(unset -> unsafe_code default)' }
$currentEnabled = if ($settings.Contains('enable_execute_python')) { [bool]$settings['enable_execute_python'] } else { $true }

# ---- The warning: what to weigh BEFORE restoring it. ------------------------
if (-not $Disable) {
    Write-Host ''
    Write-Host '  You are about to re-enable horizun_execute_python (advanced / unsafe code).' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  It is enabled by default on a fresh install; if it is off on this machine,'
    Write-Host '  somebody chose that. It runs ARBITRARY CODE inside Revit with the full API'
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

# ---- Apply. -----------------------------------------------------------------
if ($Disable) {
    $settings['enable_execute_python'] = $false
} else {
    $settings['permission_profile']    = 'unsafe_code'
    $settings['enable_execute_python'] = $true
}

if (-not (Test-Path $root)) { New-Item -ItemType Directory -Force -Path $root | Out-Null }

# Back up an existing file before writing, newest-wins, so nothing here is a
# one-way door.
if (Test-Path $settingsPath) {
    $backup = "$settingsPath.horizun-bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $settingsPath $backup -Force
    Write-Host "  Backed up existing settings to: $backup" -ForegroundColor DarkGray
}

$json = ($settings | ConvertTo-Json -Depth 20)
# ConvertTo-Json on a single-key object can emit a bare value on 5.1; force an
# object shape by round-tripping only when needed is overkill — settings always
# has >=1 key here and ConvertTo-Json wraps ordered dictionaries as objects.
Set-Content -Path $settingsPath -Value $json -Encoding UTF8

# ---- Verify by reading it back. ---------------------------------------------
$check = (Get-Content $settingsPath -Raw -Encoding UTF8) | ConvertFrom-Json
$okProfile = (-not $Disable) -eq ($check.permission_profile -eq 'unsafe_code')
$okEnabled = $check.enable_execute_python -eq (-not $Disable)

Write-Host ''
if ($Disable) {
    if ($check.enable_execute_python -eq $false) {
        Write-Host 'horizun_execute_python is now DISABLED.' -ForegroundColor Green
    } else {
        Write-Host 'Wrote the file but read back an unexpected value. Check it by hand.' -ForegroundColor Red
        exit 1
    }
} else {
    if ($check.permission_profile -eq 'unsafe_code' -and $check.enable_execute_python -eq $true) {
        Write-Host 'horizun_execute_python is now ENABLED.' -ForegroundColor Green
    } else {
        Write-Host 'Wrote the file but read back an unexpected value. Check it by hand.' -ForegroundColor Red
        exit 1
    }
}
Write-Host 'RESTART YOUR MCP CLIENT once so the tool appears or disappears in its tool list.' -ForegroundColor Yellow
exit 0
