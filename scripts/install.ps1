#Requires -Version 5.1
<#
.SYNOPSIS
  Install or uninstall Bimwright Revit client components.

.DESCRIPTION
  In a client setup ZIP, this script installs:
    - the self-contained MCP server from server/
    - matching per-Revit plugin ZIPs from plugins/
    - optional MCP client config entries using an absolute server path

  It also remains compatible with the older plugin-only release layout where
  per-Revit plugin ZIPs sit beside this script and the server is installed as a
  .NET global tool by the user.

.PARAMETER SourceDir
  Setup root or plugin ZIP source directory. Defaults to the current setup root
  when server/ or plugins/ exists beside this script; otherwise defaults to
  build/plugin-zip/ relative to the repo root.

.PARAMETER Client
  MCP client wiring mode. Auto wires every known installed config it can find.
  Explicit values wire only that client. none installs files without config edits.

.PARAMETER WireClient
  Backward-compatible alias for older scripts. Overrides -Client when set.

.EXAMPLE
  pwsh .\install.ps1 -WhatIf
  pwsh .\install.ps1
  pwsh .\install.ps1 -Client codex
  pwsh .\install.ps1 -Years 2024 -Client none
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourceDir,
    [switch]$Uninstall,
    [int[]]$Years,
    [ValidateSet('Auto', 'codex', 'opencode', 'claude', 'none')]
    [string]$Client = 'Auto',
    [ValidateSet('opencode', 'codex')]
    [string]$WireClient,
    [string]$ServerInstallRoot
)

$ErrorActionPreference = 'Stop'

if ($WireClient) {
    $Client = $WireClient
}

if (-not $SourceDir) {
    $hasSetupLayout = (Test-Path (Join-Path $PSScriptRoot 'server')) -or (Test-Path (Join-Path $PSScriptRoot 'plugins'))
    if ($hasSetupLayout) {
        $SourceDir = $PSScriptRoot
    } else {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $SourceDir = Join-Path $repoRoot 'build\plugin-zip'
    }
}

if (Test-Path $SourceDir) {
    $SourceDir = (Resolve-Path $SourceDir).Path
}

$pluginSourceDir = if (Test-Path (Join-Path $SourceDir 'plugins')) {
    Join-Path $SourceDir 'plugins'
} else {
    $SourceDir
}

$serverSourceDir = if (Test-Path (Join-Path $SourceDir 'server')) {
    Join-Path $SourceDir 'server'
} else {
    $null
}

$manifestPath = Join-Path $SourceDir 'manifest.json'
$setupVersion = 'dev'
if (Test-Path $manifestPath) {
    try {
        $manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
        if ($manifest.version) { $setupVersion = [string]$manifest.version }
    } catch {
        Write-Warning ("[setup] could not parse manifest.json: {0}" -f $_.Exception.Message)
    }
}

if (-not $ServerInstallRoot) {
    $ServerInstallRoot = Join-Path $env:LOCALAPPDATA ("Bimwright\rvt\server\{0}" -f $setupVersion)
}

function Get-InstalledRevitYears {
    $detected = @()
    $root = 'HKLM:\SOFTWARE\Autodesk\Revit'
    if (-not (Test-Path $root)) { return $detected }
    foreach ($year in 2022..2027) {
        $yearKey = Join-Path $root "$year"
        if (Test-Path $yearKey) { $detected += $year }
    }
    return $detected
}

function Get-AddinsRoot([int]$year) {
    return Join-Path $env:APPDATA ("Autodesk\Revit\Addins\{0}" -f $year)
}

function Find-ServerSourceExe {
    param([string]$ServerDir)
    if (-not $ServerDir) { return $null }
    $preferred = Join-Path $ServerDir 'bimwright-rvt.exe'
    if (Test-Path $preferred) { return $preferred }
    $fallback = Join-Path $ServerDir 'Bimwright.Rvt.Server.exe'
    if (Test-Path $fallback) { return $fallback }
    return $null
}

function Get-BimwrightClientTargets {
    param(
        [int[]]$years,
        [string]$serverCommand
    )
    $supportedYears = @($years | Where-Object { $_ -ge 2022 -and $_ -le 2027 })
    if ($supportedYears.Count -eq 0) {
        return @()
    }

    return ,([pscustomobject]@{
        Name      = 'bimwright-rvt'
        ServerCmd = $serverCommand
        Args      = @()
        Years     = $supportedYears
    })
}

function Write-ConfigAtomic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $bak = "$Path.bimwright.bak"
    Copy-Item -Path $Path -Destination $bak -Force
    $temp = "$Path.bimwright.tmp"
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

function ConvertTo-TomlString {
    param([string]$Value)
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function ConvertTo-TomlStringArray {
    param([object[]]$Values)
    $items = @()
    foreach ($v in @($Values)) {
        if ($null -ne $v) {
            $items += (ConvertTo-TomlString -Value ([string]$v))
        }
    }
    return '[' + ($items -join ', ') + ']'
}

function Remove-LegacyBimwrightEntries {
    param([Parameter(Mandatory = $true)][hashtable]$Map)

    $keys = @($Map.Keys | Where-Object { $_ -match '^bimwright-rvt-r\d{2}$' })
    foreach ($k in $keys) {
        $Map.Remove($k) | Out-Null
    }
    return $keys.Count
}

function Install-BimwrightServer {
    param(
        [string]$ServerDir,
        [string]$InstallRoot
    )
    $sourceExe = Find-ServerSourceExe -ServerDir $ServerDir
    if (-not $sourceExe) { return $null }

    $plannedExe = Join-Path $InstallRoot (Split-Path -Leaf $sourceExe)
    if ($PSCmdlet.ShouldProcess($InstallRoot, 'Install self-contained Bimwright RVT server')) {
        if (Test-Path $InstallRoot) {
            Remove-Item -Path $InstallRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $ServerDir '*') -Destination $InstallRoot -Recurse -Force
        Write-Host ("[server] installed -> {0}" -f $plannedExe)
    } else {
        Write-Host ("[server] preview install -> {0}" -f $plannedExe)
    }
    return $plannedExe
}

function Add-OpencodeEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        $msg = "[opencode] config not found at $ConfigPath"
        if ($RequireExisting) { Write-Warning "$msg - skipping wire" } else { Write-Host "$msg - skipping" }
        return $false
    }

    try {
        $cfg = Read-JsonHashtable -Path $ConfigPath
    } catch {
        Write-Warning ("[opencode] parse failed at {0}: {1} - skipping" -f $ConfigPath, $_.Exception.Message)
        return $false
    }

    if (-not $cfg.ContainsKey('mcp')) { $cfg['mcp'] = @{} }

    $desired = @{}
    foreach ($t in $Targets) {
        $name = $t.Name
        $desired[$name] = [ordered]@{
            type    = 'local'
            command = @($t.ServerCmd) + @($t.Args)
            enabled = $true
        }
    }

    $legacyRemoved = Remove-LegacyBimwrightEntries -Map $cfg['mcp']
    $changed = $legacyRemoved -gt 0
    foreach ($k in $desired.Keys) {
        $existingJson = if ($cfg['mcp'].ContainsKey($k)) { ($cfg['mcp'][$k] | ConvertTo-Json -Depth 20 -Compress) } else { $null }
        $newJson = $desired[$k] | ConvertTo-Json -Depth 20 -Compress
        if ($existingJson -ne $newJson) {
            $cfg['mcp'][$k] = $desired[$k]
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[opencode] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert bimwright-rvt entry')) {
        $content = $cfg | ConvertTo-Json -Depth 50
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $content
        Write-Host ("[opencode] wired {0} entry -> {1} (backup: {2})" -f $desired.Count, $ConfigPath, $bak)
    }
    return $true
}

function Add-CodexEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        $msg = "[codex] config not found at $ConfigPath"
        if ($RequireExisting) { Write-Warning "$msg - skipping wire" } else { Write-Host "$msg - skipping" }
        return $false
    }

    $raw = Get-Content -Raw -Path $ConfigPath -Encoding UTF8
    if ($null -eq $raw) { $raw = '' }

    $changed = $false
    $legacyPattern = '(?ms)^\[mcp_servers\.bimwright-rvt-r\d{2}\].*?(?=^\[|\z)'
    if ([regex]::IsMatch($raw, $legacyPattern)) {
        $raw = [regex]::Replace($raw, $legacyPattern, '')
        $raw = [regex]::Replace($raw, "(\r?\n){3,}", "`n`n")
        $changed = $true
    }

    foreach ($t in $Targets) {
        $name = $t.Name
        $headerLiteral = "[mcp_servers.$name]"
        $commandValue = ConvertTo-TomlString -Value $t.ServerCmd
        $argsValue = ConvertTo-TomlStringArray -Values $t.Args
        $desiredBlock = @"
$headerLiteral
command = $commandValue
args = $argsValue
enabled = true
"@

        $pattern = '(?ms)^\[mcp_servers\.' + [regex]::Escape($name) + '\].*?(?=^\[|\z)'
        $existingMatch = [regex]::Match($raw, $pattern)
        if ($existingMatch.Success) {
            $existingTrim = ($existingMatch.Value -replace '\s+$', '')
            $desiredTrim = ($desiredBlock -replace '\s+$', '')
            if ($existingTrim -ne $desiredTrim) {
                $raw = [regex]::Replace($raw, $pattern, ($desiredBlock + "`n`n"), 1)
                $changed = $true
            }
        } else {
            $sep = if ($raw.EndsWith("`n")) { "`n" } else { "`n`n" }
            $raw = $raw + $sep + $desiredBlock + "`n"
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[codex] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert [mcp_servers.bimwright-rvt] block')) {
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $raw
        Write-Host ("[codex] wired bimwright-rvt block -> {0} (backup: {1})" -f $ConfigPath, $bak)
    }
    return $true
}

function Add-ClaudeEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        $msg = "[claude] config not found at $ConfigPath"
        if ($RequireExisting) { Write-Warning "$msg - skipping wire" } else { Write-Host "$msg - skipping" }
        return $false
    }

    try {
        $cfg = Read-JsonHashtable -Path $ConfigPath
    } catch {
        Write-Warning ("[claude] parse failed at {0}: {1} - skipping" -f $ConfigPath, $_.Exception.Message)
        return $false
    }

    if (-not $cfg.ContainsKey('mcpServers')) { $cfg['mcpServers'] = @{} }

    $desired = @{}
    foreach ($t in $Targets) {
        $name = $t.Name
        $desired[$name] = [ordered]@{
            command = $t.ServerCmd
            args = @($t.Args)
        }
    }

    $legacyRemoved = Remove-LegacyBimwrightEntries -Map $cfg['mcpServers']
    $changed = $legacyRemoved -gt 0
    foreach ($k in $desired.Keys) {
        $existingJson = if ($cfg['mcpServers'].ContainsKey($k)) { ($cfg['mcpServers'][$k] | ConvertTo-Json -Depth 20 -Compress) } else { $null }
        $newJson = $desired[$k] | ConvertTo-Json -Depth 20 -Compress
        if ($existingJson -ne $newJson) {
            $cfg['mcpServers'][$k] = $desired[$k]
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[claude] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert mcpServers.bimwright-rvt entry')) {
        $content = $cfg | ConvertTo-Json -Depth 50
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $content
        Write-Host ("[claude] wired {0} entry -> {1} (backup: {2})" -f $desired.Count, $ConfigPath, $bak)
    }
    return $true
}

function Add-ClaudeEntries {
    param(
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )
    $paths = @(
        (Join-Path $env:USERPROFILE '.claude.json'),
        (Join-Path $env:USERPROFILE '.claude\mcp.json'),
        (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
    )
    $handled = $false
    foreach ($path in $paths) {
        if (Add-ClaudeEntry -ConfigPath $path -Targets $Targets -RequireExisting:$RequireExisting) {
            $handled = $true
        }
    }
    return $handled
}

if (-not $Years -or $Years.Count -eq 0) {
    $Years = Get-InstalledRevitYears
    if ($Years.Count -eq 0) {
        Write-Warning "No Revit installations detected under HKLM:\SOFTWARE\Autodesk\Revit\. Use -Years to force explicit list."
        return
    }
    Write-Host ("Detected Revit years: {0}" -f ($Years -join ', '))
}

$handled = @()
$skipped = @()
$previewed = @()

foreach ($year in $Years) {
    $yearTwo = "{0:D2}" -f ($year - 2000)
    $addinFile = "Bimwright.R$yearTwo.addin"
    $addinsRoot = Get-AddinsRoot $year
    $pluginDir = Join-Path $addinsRoot 'Bimwright'
    $addinPath = Join-Path $addinsRoot $addinFile

    if ($Uninstall) {
        $didSomething = $false
        if (Test-Path $pluginDir) {
            if ($PSCmdlet.ShouldProcess($pluginDir, 'Remove plugin folder')) {
                Remove-Item $pluginDir -Recurse -Force
            }
            $didSomething = $true
        }
        if (Test-Path $addinPath) {
            if ($PSCmdlet.ShouldProcess($addinPath, 'Remove addin manifest')) {
                Remove-Item $addinPath -Force
            }
            $didSomething = $true
        }
        if ($didSomething) {
            if ($WhatIfPreference) {
                Write-Host ("[R{0}] preview uninstall from {1}" -f $yearTwo, $addinsRoot)
                $previewed += "R$yearTwo"
            } else {
                Write-Host ("[R{0}] uninstalled from {1}" -f $yearTwo, $addinsRoot)
                $handled += "R$yearTwo"
            }
        } else {
            Write-Host ("[R{0}] nothing to remove at {1}" -f $yearTwo, $addinsRoot)
            $skipped += "R$yearTwo"
        }
        continue
    }

    $zip = Join-Path $pluginSourceDir ("Bimwright.Rvt.Plugin.R{0}.zip" -f $yearTwo)
    if (-not (Test-Path $zip)) {
        Write-Warning ("[R{0}] skipped - missing zip {1}" -f $yearTwo, $zip)
        $skipped += "R$yearTwo"
        continue
    }

    if (-not (Test-Path $addinsRoot)) {
        if ($PSCmdlet.ShouldProcess($addinsRoot, 'Create Revit addins directory')) {
            New-Item -ItemType Directory -Path $addinsRoot -Force | Out-Null
        }
    }

    if (Test-Path $pluginDir) {
        if ($PSCmdlet.ShouldProcess($pluginDir, 'Clean previous install')) {
            Remove-Item $pluginDir -Recurse -Force
        }
    }
    if ($PSCmdlet.ShouldProcess($pluginDir, 'Create plugin folder')) {
        New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zipHasAddin = $false
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $zipHasAddin = @($archive.Entries | Where-Object { $_.Name -eq $addinFile }).Count -gt 0
    } finally {
        $archive.Dispose()
    }
    if (-not $zipHasAddin) {
        Write-Warning ("[R{0}] zip {1} does not contain {2} - skipping" -f $yearTwo, $zip, $addinFile)
        $skipped += "R$yearTwo"
        continue
    }

    if ($PSCmdlet.ShouldProcess($zip, "Extract to $pluginDir")) {
        Expand-Archive -Path $zip -DestinationPath $pluginDir -Force
    }

    $extractedAddin = Join-Path $pluginDir $addinFile
    if ($PSCmdlet.ShouldProcess($addinPath, 'Move addin manifest to addins root')) {
        Move-Item -Path $extractedAddin -Destination $addinPath -Force
    }

    if ($WhatIfPreference) {
        Write-Host ("[R{0}] preview install -> {1}" -f $yearTwo, $pluginDir)
        $previewed += "R$yearTwo"
    } else {
        Write-Host ("[R{0}] installed -> {1}" -f $yearTwo, $pluginDir)
        $handled += "R$yearTwo"
    }
}

$serverCommand = $null
if (-not $Uninstall) {
    $serverCommand = Install-BimwrightServer -ServerDir $serverSourceDir -InstallRoot $ServerInstallRoot
    if (-not $serverCommand) {
        if (Get-Command bimwright-rvt -ErrorAction SilentlyContinue) {
            $serverCommand = 'bimwright-rvt'
        }
    }
}

$wireStatus = @()
if (-not $Uninstall -and $Client -ne 'none') {
    $clientWasDefaultAuto = ($Client -eq 'Auto' -and -not $WireClient)
    if (-not $serverCommand) {
        if ($clientWasDefaultAuto) {
            Write-Host "[wire] no setup server found and bimwright-rvt is not on PATH - skipping Auto wire"
        } else {
            Write-Warning "[wire] no server command available - install from setup ZIP or install Bimwright.Rvt.Server first"
        }
    } else {
        $targets = Get-BimwrightClientTargets -years $Years -serverCommand $serverCommand
        if ($targets.Count -eq 0) {
            Write-Warning "[wire] no plugin-supported Revit years (2022-2027) detected - skipping wire"
        } else {
            $requireExisting = -not ($Client -eq 'Auto')
            if ($Client -eq 'Auto' -or $Client -eq 'opencode') {
                $ok = Add-OpencodeEntry -ConfigPath (Join-Path $env:USERPROFILE '.config\opencode\opencode.json') -Targets $targets -RequireExisting:$requireExisting
                if ($ok) { $wireStatus += 'opencode' }
            }
            if ($Client -eq 'Auto' -or $Client -eq 'codex') {
                $ok = Add-CodexEntry -ConfigPath (Join-Path $env:USERPROFILE '.codex\config.toml') -Targets $targets -RequireExisting:$requireExisting
                if ($ok) { $wireStatus += 'codex' }
            }
            if ($Client -eq 'Auto' -or $Client -eq 'claude') {
                $ok = Add-ClaudeEntries -Targets $targets -RequireExisting:$requireExisting
                if ($ok) { $wireStatus += 'claude' }
            }
        }
    }
}

Write-Host ""
Write-Host "=== install.ps1 summary ==="
Write-Host ("Mode   : {0}" -f ($(if ($Uninstall) { 'Uninstall' } else { 'Install' })))
Write-Host ("Source : {0}" -f $SourceDir)
Write-Host ("Years  : {0}" -f ($Years -join ', '))
Write-Host ("Handled: {0}" -f ($(if ($handled.Count -gt 0) { $handled -join ', ' } else { 'none' })))
if ($previewed.Count -gt 0) {
    Write-Host ("Previewed: {0}" -f ($previewed -join ', '))
}
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped: {0}" -f ($skipped -join ', '))
}
if ($serverCommand) {
    Write-Host ("Server : {0}" -f $serverCommand)
}
if ($Client -ne 'none') {
    Write-Host ("Client : {0}" -f $Client)
    Write-Host ("Wired  : {0}" -f ($(if ($wireStatus.Count -gt 0) { $wireStatus -join ', ' } else { 'none' })))
}
