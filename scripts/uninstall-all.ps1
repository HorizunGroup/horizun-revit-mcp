<#
.SYNOPSIS
  Remove every RvtMcp artifact from this machine (plugin + server + host configs + discovery + logs).

.DESCRIPTION
  Runs a 4-step sweep:
    1. Plugin + .addin for every detected Revit year (delegates to install.ps1 -Uninstall).
    2. Legacy .NET global tool RvtMcp.Server, if present.
    3. MCP host config entries matching rvt-mcp or legacy bimwright-rvt-* in:
       - $env:USERPROFILE\.config\opencode\opencode.json
       - $env:USERPROFILE\.codex\config.toml
       - $env:USERPROFILE\.claude.json (global, if present)
       - $env:APPDATA\Claude\claude_desktop_config.json (if present)
       Project-level .mcp.json files are NOT scanned — emits a reminder notice.
    4. Self-contained server, discovery files, and ToolBaker cache in
       %LOCALAPPDATA%\RvtMcp\ (except logs\ when -KeepLogs).

  Each step is independently skippable if the target does not exist. Failure mid-step
  does not abort the chain; exit code 1 is returned at the end if any step failed.

.PARAMETER WhatIf
  Print the full plan, write nothing.

.PARAMETER Yes
  Skip the interactive confirmation prompt.

.PARAMETER KeepLogs
  Preserve log files during step 4:
  - any `logs\` subdirectory (recursively)
  - any loose `*.log` / `*.jsonl` files at the root of %LOCALAPPDATA%\RvtMcp\
  Other files and subdirectories are removed.

.EXAMPLE
  pwsh scripts/uninstall-all.ps1 -WhatIf
  pwsh scripts/uninstall-all.ps1 -Yes
  pwsh scripts/uninstall-all.ps1 -KeepLogs
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Yes,
    [switch]$KeepLogs
)

$ErrorActionPreference = 'Stop'

$script:handled = @()
$script:skipped = @()
$script:failed  = @()

# duplicated from install.ps1 intentionally — both scripts are standalone entry points for end-users
function Write-ConfigAtomic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $bak = "$Path.rvtmcp.bak"
    Copy-Item -Path $Path -Destination $bak -Force
    $temp = "$Path.rvtmcp.tmp"
    try {
        Set-Content -Path $temp -Value $Content -Encoding UTF8 -NoNewline
        [System.IO.File]::Replace($temp, $Path, [NullString]::Value)
    } catch {
        if (Test-Path $temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
        throw
    }
    return $bak
}

function ConvertTo-Hashtable {
    param([Parameter(ValueFromPipeline = $true)][object]$InputObject)

    process {
        if ($null -eq $InputObject) { return $null }

        if ($InputObject -is [System.Collections.IDictionary]) {
            $hash = @{}
            foreach ($key in $InputObject.Keys) {
                $hash[$key] = ConvertTo-Hashtable $InputObject[$key]
            }
            return $hash
        }

        if ($InputObject -is [pscustomobject]) {
            $hash = @{}
            foreach ($property in $InputObject.PSObject.Properties) {
                $hash[$property.Name] = ConvertTo-Hashtable $property.Value
            }
            return $hash
        }

        if (($InputObject -is [System.Collections.IEnumerable]) -and -not ($InputObject -is [string])) {
            $items = @()
            foreach ($item in $InputObject) {
                $items += ConvertTo-Hashtable $item
            }
            return ,$items
        }

        return $InputObject
    }
}

function Read-JsonHashtable {
    param([string]$Path)
    $raw = Get-Content -Raw -Path $Path
    if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
    return ConvertTo-Hashtable ($raw | ConvertFrom-Json)
}

function Confirm-Sweep {
    param([string[]]$PlannedTargets)
    Write-Host ""
    Write-Host "=== uninstall-all.ps1 — planned targets ==="
    foreach ($t in $PlannedTargets) { Write-Host "  - $t" }
    Write-Host ""
    if ($Yes) { return $true }
    $ans = Read-Host "Proceed? (y/N)"
    return ($ans -match '^(y|yes)$')
}

function Invoke-Step1-Plugin {
    $installScript = Join-Path $PSScriptRoot 'install.ps1'
    if (-not (Test-Path $installScript)) {
        Write-Warning "[step1] install.ps1 not found at $installScript — cannot remove plugin"
        $script:failed += 'step1-plugin'
        return
    }
    try {
        if ($PSCmdlet.ShouldProcess($installScript, 'Delegate plugin uninstall')) {
            & $installScript -Uninstall
        } else {
            Write-Host "[step1] (WhatIf) would call: $installScript -Uninstall"
        }
        $script:handled += 'step1-plugin'
    } catch {
        Write-Warning ("[step1] plugin uninstall failed: {0}" -f $_.Exception.Message)
        $script:failed += 'step1-plugin'
    }
}

function Invoke-Step2-DotnetTool {
    $toolName = 'RvtMcp.Server'
    try {
        $list = & dotnet tool list -g 2>&1 | Out-String
    } catch {
        Write-Warning "[step2] 'dotnet' not on PATH — cannot check global tools"
        $script:skipped += 'step2-dotnet-tool'
        return
    }

    if ($list -notmatch [regex]::Escape($toolName.ToLower())) {
        Write-Host "[step2] $toolName not installed — nothing to remove"
        $script:skipped += 'step2-dotnet-tool'
        return
    }

    try {
        if ($PSCmdlet.ShouldProcess($toolName, 'dotnet tool uninstall -g')) {
            & dotnet tool uninstall -g $toolName
        } else {
            Write-Host "[step2] (WhatIf) would run: dotnet tool uninstall -g $toolName"
        }
        $script:handled += 'step2-dotnet-tool'
    } catch {
        Write-Warning ("[step2] uninstall failed: {0}" -f $_.Exception.Message)
        $script:failed += 'step2-dotnet-tool'
    }
}

function Invoke-Step4-Discovery {
    $root = Join-Path $env:LOCALAPPDATA 'RvtMcp'
    if (-not (Test-Path $root)) {
        Write-Host "[step4] $root not present — nothing to remove"
        $script:skipped += 'step4-discovery'
        return
    }

    $toolBakerPath = Join-Path $root 'ToolBaker'
    $hasToolBaker = Test-Path $toolBakerPath

    if ($KeepLogs) {
        # Preserve: `logs\` subdirectory, and any loose *.log / *.jsonl files at the root.
        $preserved = @()
        $entries = Get-ChildItem -Path $root -Force
        foreach ($e in $entries) {
            $keep = $false
            if ($e.PSIsContainer) {
                if ($e.Name -eq 'logs') { $keep = $true }
            } else {
                if ($e.Extension -in @('.log', '.jsonl')) { $keep = $true }
            }
            if ($keep) {
                $preserved += $e.Name
                continue
            }
            if ($PSCmdlet.ShouldProcess($e.FullName, 'Remove-Item -Recurse')) {
                try {
                    Remove-Item -Path $e.FullName -Recurse -Force
                } catch {
                    Write-Warning ("[step4] failed to remove {0}: {1}" -f $e.FullName, $_.Exception.Message)
                    $script:failed += 'step4-discovery'
                    return
                }
            }
        }
        $keptMsg = if ($preserved.Count -gt 0) { ($preserved -join ', ') } else { '(nothing to keep)' }
        $verb = if ($WhatIfPreference) { 'preview clean' } else { 'cleaned' }
        Write-Host ("[step4] {0} {1} (kept: {2})" -f $verb, $root, $keptMsg)
    } else {
        if ($PSCmdlet.ShouldProcess($root, 'Remove-Item -Recurse')) {
            try {
                Remove-Item -Path $root -Recurse -Force
            } catch {
                Write-Warning ("[step4] failed to remove {0}: {1}" -f $root, $_.Exception.Message)
                $script:failed += 'step4-discovery'
                return
            }
        }
        $verb = if ($WhatIfPreference) { 'preview remove' } else { 'removed' }
        Write-Host ("[step4] {0} {1}" -f $verb, $root)
    }

    $script:handled += 'step4-discovery'
    if ($hasToolBaker) {
        $script:handled += 'step5-toolbaker (contained)'
    }
}

function Remove-ClaudeCodeGlobalEntries {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.claude.json'),
        (Join-Path $env:USERPROFILE '.claude\mcp.json'),
        (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
    )

    $touched = $false
    foreach ($cfgPath in $candidates) {
        if (-not (Test-Path $cfgPath)) { continue }

        try {
            $cfg = Read-JsonHashtable -Path $cfgPath
        } catch {
            Write-Warning ("[step3.claude] parse failed at {0} — skipping this file" -f $cfgPath)
            continue
        }

        # Claude Code uses 'mcpServers' (plural camelCase) per its docs
        if (-not $cfg.ContainsKey('mcpServers') -or $cfg['mcpServers'].Count -eq 0) { continue }

        $bimKeys = @($cfg['mcpServers'].Keys | Where-Object { $_ -eq 'rvt-mcp' -or $_ -like 'rvt-mcp-*' -or $_ -eq 'bimwright-rvt' -or $_ -like 'bimwright-rvt-*' })
        if ($bimKeys.Count -eq 0) { continue }

        foreach ($k in $bimKeys) { $cfg['mcpServers'].Remove($k) | Out-Null }
        if ($cfg['mcpServers'].Count -eq 0) { $cfg.Remove('mcpServers') | Out-Null }

        if ($PSCmdlet.ShouldProcess($cfgPath, ("Remove {0} rvt-mcp entries" -f $bimKeys.Count))) {
            $content = $cfg | ConvertTo-Json -Depth 50
            $bak = Write-ConfigAtomic -Path $cfgPath -Content $content
            Write-Host ("[step3.claude] removed {0} entries -> {1} (backup: {2})" -f $bimKeys.Count, $cfgPath, $bak)
        }
        $touched = $true
    }

    if ($touched) { $script:handled += 'step3-claude-global' }
    else          { $script:skipped += 'step3-claude-global' }

    Write-Host ""
    Write-Host "[step3.claude] NOTE: project-level .mcp.json files are not auto-scanned."
    Write-Host "               If you added rvt-mcp to any project's .mcp.json manually,"
    Write-Host "               remove those entries by hand."
}

function Remove-CodexEntries {
    $cfgPath = Join-Path $env:USERPROFILE '.codex\config.toml'
    if (-not (Test-Path $cfgPath)) {
        Write-Host "[step3.codex] config not present — nothing to unwire"
        $script:skipped += 'step3-codex'
        return
    }

    $raw = Get-Content -Raw -Path $cfgPath -Encoding UTF8
    if ($null -eq $raw) { $raw = '' }

    $pattern = '(?ms)^\[mcp_servers\.(?:rvt-mcp|bimwright-rvt)(?:-r\d{2})?\].*?(?=^\[|\z)'
    $blockMatches = [regex]::Matches($raw, $pattern)
    if ($blockMatches.Count -eq 0) {
        Write-Host "[step3.codex] no rvt-mcp or bimwright-rvt blocks — skipping"
        $script:skipped += 'step3-codex'
        return
    }

    $new = [regex]::Replace($raw, $pattern, '')
    # Collapse triple+ blank lines left behind by removal
    $new = [regex]::Replace($new, "(\r?\n){3,}", "`n`n")

    if ($PSCmdlet.ShouldProcess($cfgPath, ("Remove {0} [mcp_servers.rvt-mcp] blocks" -f $blockMatches.Count))) {
        $bak = Write-ConfigAtomic -Path $cfgPath -Content $new
        Write-Host ("[step3.codex] removed {0} blocks -> {1} (backup: {2})" -f $blockMatches.Count, $cfgPath, $bak)
    }
    $script:handled += 'step3-codex'
}

function Remove-OpencodeEntries {
    $cfgPath = Join-Path $env:USERPROFILE '.config\opencode\opencode.json'
    if (-not (Test-Path $cfgPath)) {
        Write-Host "[step3.opencode] config not present — nothing to unwire"
        $script:skipped += 'step3-opencode'
        return
    }

    try {
        $cfg = Read-JsonHashtable -Path $cfgPath
    } catch {
        Write-Warning ("[step3.opencode] parse failed at {0}: {1} — skipping" -f $cfgPath, $_.Exception.Message)
        $script:failed += 'step3-opencode'
        return
    }

    if (-not $cfg.ContainsKey('mcp') -or $cfg['mcp'].Count -eq 0) {
        Write-Host "[step3.opencode] no mcp entries — skipping"
        $script:skipped += 'step3-opencode'
        return
    }

    $bimKeys = @($cfg['mcp'].Keys | Where-Object { $_ -eq 'rvt-mcp' -or $_ -like 'rvt-mcp-*' -or $_ -eq 'bimwright-rvt' -or $_ -like 'bimwright-rvt-*' })
    if ($bimKeys.Count -eq 0) {
        Write-Host "[step3.opencode] no rvt-mcp or bimwright-rvt entries — skipping"
        $script:skipped += 'step3-opencode'
        return
    }

    foreach ($k in $bimKeys) { $cfg['mcp'].Remove($k) | Out-Null }
    if ($cfg['mcp'].Count -eq 0) { $cfg.Remove('mcp') | Out-Null }

    if ($PSCmdlet.ShouldProcess($cfgPath, ("Remove {0} rvt-mcp entries" -f $bimKeys.Count))) {
        $content = $cfg | ConvertTo-Json -Depth 50
        $bak = Write-ConfigAtomic -Path $cfgPath -Content $content
        Write-Host ("[step3.opencode] removed {0} entries -> {1} (backup: {2})" -f $bimKeys.Count, $cfgPath, $bak)
    }
    $script:handled += 'step3-opencode'
}

# --- Main ---
$planned = @(
    'Step1: plugin + .addin (all detected Revit years via install.ps1 -Uninstall)'
    'Step2: global tool RvtMcp.Server, if present'
    'Step3.opencode: rvt-mcp/bimwright-rvt keys in .config\opencode\opencode.json'
    'Step3.codex: [mcp_servers.rvt-mcp] blocks in .codex\config.toml'
    'Step3.claude: rvt-mcp/bimwright-rvt in Claude global/Desktop configs'
    'Step4: %LOCALAPPDATA%\RvtMcp\ (self-contained server + discovery + ToolBaker)'
)

if (-not (Confirm-Sweep $planned)) {
    Write-Host "Aborted by user."
    return
}

try {
    Invoke-Step1-Plugin
    Invoke-Step2-DotnetTool
    Remove-OpencodeEntries
    Remove-CodexEntries
    Remove-ClaudeCodeGlobalEntries
    Invoke-Step4-Discovery
} catch {
    Write-Warning ("[main] unexpected error — summary follows. Error: {0}" -f $_.Exception.Message)
    $script:failed += 'main-unexpected-error'
} finally {
    Write-Host ""
    Write-Host "=== uninstall-all.ps1 summary ==="
    $mode = if ($WhatIfPreference) { 'WhatIf (no changes)' } else { 'Execute' }
    Write-Host ("Mode   : {0}" -f $mode)
    Write-Host ("Handled: {0}" -f (($script:handled) -join ', '))
    if ($script:skipped.Count -gt 0) { Write-Host ("Skipped: {0}" -f (($script:skipped) -join ', ')) }
    if ($script:failed.Count  -gt 0) { Write-Host ("Failed : {0}" -f (($script:failed)  -join ', ')) }
}

if ($script:failed.Count -gt 0) { exit 1 } else { exit 0 }
