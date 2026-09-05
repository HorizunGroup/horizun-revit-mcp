#Requires -Version 5.1
<#
  Install, repair, diagnose or remove the Horizun integration in Claude Desktop.

  NO CLAUDE CODE. Nothing here runs `claude`, needs the CLI on PATH, or touches
  ~/.claude.json. Claude Desktop and Claude Code are different products with
  different configuration; this script only ever deals with the desktop app.

  TWO ROUTES, and the honest difference between them:

  1. THE EXTENSION (.mcpb). Claude Desktop installs one from
     Settings > Extensions > Advanced settings > Install Extension. That final
     step happens in the app's own UI and there is no documented command for it,
     so this script prepares everything up to it, hands over the exact file, and
     records `pending_user_action` naming the step. It does NOT write into the
     app's extension store: that directory carries per-extension metadata the app
     maintains, and forging an entry there is inventing a private format.

  2. THE CONFIGURATION FALLBACK (-ConfigFallback). Writing
     mcpServers."horizun-revit" into claude_desktop_config.json is documented,
     scriptable, and gives exactly the same result. It is off by default because
     the extension is the supported route; when it is used, the path is written
     ALREADY EXPANDED for this machine, because the app does not expand
     %LOCALAPPDATA% and a configuration written with it points nowhere.

  WHAT IT REFUSES. Claude Desktop rewrites its configuration from memory while it
  runs, so an edit made underneath it is lost silently and the symptom is "the
  tools never appeared". A running Claude Desktop is therefore refused rather
  than edited hopefully, and the state recorded is pending_client_restart.

  WHAT IT PRESERVES. Other extensions are never touched. Every other key in
  claude_desktop_config.json is preserved and checked after the write; if any went
  missing the backup is restored and the operation reports failure.

    scripts/install-claude-desktop-extension.ps1              # prepare + report
    scripts/install-claude-desktop-extension.ps1 -Diagnose
    scripts/install-claude-desktop-extension.ps1 -ConfigFallback
    scripts/install-claude-desktop-extension.ps1 -Remove
    scripts/install-claude-desktop-extension.ps1 -Rollback

  Exit codes: 0 done  1 failed  2 could not run  3 done, one user step remains
#>
[CmdletBinding()]
param(
    [switch]$Diagnose,
    [switch]$ConfigFallback,
    [switch]$Remove,
    [switch]$Rollback,
    # Write under a running Claude Desktop anyway. It will probably be lost.
    [switch]$Force,
    [string]$PackagePath,
    [string]$ServerPath,
    [string]$Json,
    # Test seams. Both default to the real thing.
    [string]$RoamingOverride,
    [string]$StatusPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')
. (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-stdio.lib.ps1')
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')

$CLIENT = 'claude-desktop'
$NAME = 'horizun-revit'
$actions = New-Object System.Collections.Generic.List[object]
$problems = New-Object System.Collections.Generic.List[string]

function Say($m, $c = 'Gray') { Write-Host "  $m" -ForegroundColor $c }
function Act($what, $ok, $detail) {
    $actions.Add([pscustomobject]@{ action = $what; ok = [bool]$ok; detail = $detail }) | Out-Null
    if ($ok) { Say $what 'Green' } else { Say "$what - $detail" 'Red'; $problems.Add("$what : $detail") | Out-Null }
}

$stageRoot = Join-Path $env:LOCALAPPDATA 'Horizun\integrations\claude-desktop'
if (-not $ServerPath) { $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }

Write-Host ""
Write-Host "Horizun in Claude Desktop" -ForegroundColor Cyan

# --- what is actually there -----------------------------------------------------
$cd = Get-HorizunClaudeDesktop -RoamingOverride $RoamingOverride
if (-not $cd.installed) {
    Say "Claude Desktop is not installed on this machine." 'Yellow'
    Say "Checked the Store/MSIX package and %APPDATA%\Claude. Nothing to configure."
    Set-HorizunIntegrationState -Client $CLIENT -State 'unsupported' -StatusPath $StatusPath `
        -Detail 'Claude Desktop is not installed: neither the MSIX package nor %APPDATA%\Claude is present.' | Out-Null
    exit 2
}
Say ("found Claude Desktop {0} ({1} install)" -f $cd.version, $cd.packaging) 'Green'
Say ("config      {0}" -f $cd.config_path)
Say ("extensions  {0}" -f $cd.extensions_dir)
if ($cd.extensions.Count -gt 0) {
    Say ("already installed: " + (($cd.extensions | ForEach-Object { "$($_.name) $($_.version)" }) -join ', '))
}
if ($cd.running) { Say "Claude Desktop is RUNNING." 'Yellow' }

# --- rollback -------------------------------------------------------------------
function Get-Backups {
    if (-not $cd.config_path) { return @() }
    $dir = Split-Path -Parent $cd.config_path
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Filter 'claude_desktop_config.json.horizun-bak-*' -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending)
}

if ($Rollback) {
    $backups = Get-Backups
    if ($backups.Count -eq 0) { Act 'restore a backup' $false 'no backup taken by this script was found'; exit 1 }
    if ($cd.running -and -not $Force) {
        Act 'restore a backup' $false 'Claude Desktop is RUNNING and would overwrite the restored file from memory. Close it and re-run.'
        exit 1
    }
    Copy-Item -LiteralPath $backups[0].FullName -Destination $cd.config_path -Force
    Act ("restored " + $backups[0].Name) $true $null
    Set-HorizunIntegrationState -Client $CLIENT -State 'configured' -StatusPath $StatusPath `
        -Detail ("claude_desktop_config.json was restored from " + $backups[0].Name) | Out-Null
    exit 0
}

# --- the server the integration will run ----------------------------------------
$serverPresent = Test-Path -LiteralPath $ServerPath -PathType Leaf
$probe = $null
if ($serverPresent) {
    # THE ONE CHECK THAT MATTERS. An extension whose command does not answer MCP
    # installs cleanly and then shows no tools, with nothing anywhere saying why.
    $probe = Invoke-HorizunMcpProbe -Command $ServerPath -ListTools -TimeoutSec 120
    if ($probe.ok) {
        Act ("the installed server answers MCP: {0} {1}, {2} tools" -f `
             $probe.server_info.name, $probe.server_info.version, $probe.tool_count) $true $null
    }
    else { Act 'the installed server answers MCP' $false $probe.problem }
}
else {
    Act 'find the installed server' $false ("$ServerPath does not exist - install Horizun Revit MCP first; " +
        "an extension that names a missing command shows no tools and no reason")
}

# --- diagnose -------------------------------------------------------------------
if ($Diagnose) {
    Write-Host ""
    Write-Host "Diagnosis" -ForegroundColor Cyan
    $ext = $cd.horizun_extension
    if ($ext) { Say ("extension installed: {0} {1} at {2}" -f $ext.name, $ext.version, $ext.path) 'Green' }
    else { Say "the '$NAME' extension is NOT in Claude Desktop's extension store." 'Yellow' }
    if ($cd.horizun_in_config) { Say "claude_desktop_config.json carries mcpServers.$NAME" 'Green' }
    else { Say "claude_desktop_config.json does NOT carry mcpServers.$NAME" 'Yellow' }
    if ($cd.problem) { Say $cd.problem 'Red' }

    $staged = @(Get-ChildItem -LiteralPath $stageRoot -Filter '*.mcpb' -ErrorAction SilentlyContinue)
    if ($staged.Count -gt 0) { Say ("staged package: " + ($staged | ForEach-Object { $_.Name }) -join ', ') }
    else { Say "no package staged at $stageRoot" }

    if (-not $ext -and -not $cd.horizun_in_config) {
        Write-Host ""
        Say "Neither route is in place. Run this script with no arguments to prepare the extension," 'Yellow'
        Say "or with -ConfigFallback to write the documented configuration entry instead." 'Yellow'
    }
    elseif ($cd.running) {
        Write-Host ""
        Say "Claude Desktop is running: restart it before judging whether the tools appear." 'Yellow'
    }
    if ($Json) { $script:emitJson = $true }
    if ($problems.Count -gt 0) { exit 1 }
    exit 0
}

# --- remove ---------------------------------------------------------------------
if ($Remove) {
    Write-Host ""
    Write-Host "Removing only '$NAME'" -ForegroundColor Cyan

    if ($cd.horizun_in_config) {
        if ($cd.running -and -not $Force) {
            Act 'remove the configuration entry' $false 'Claude Desktop is RUNNING and could restore it from memory. Close it and re-run.'
        }
        else {
            $backup = "$($cd.config_path).horizun-bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
            Copy-Item -LiteralPath $cd.config_path -Destination $backup -Force
            $cfg = Get-Content -LiteralPath $cd.config_path -Raw | ConvertFrom-Json
            $before = @($cfg.PSObject.Properties.Name)
            $cfg.mcpServers.PSObject.Properties.Remove($NAME)
            $out = $cfg | ConvertTo-Json -Depth 100
            $null = $out | ConvertFrom-Json
            Set-Content -LiteralPath $cd.config_path -Value $out -Encoding UTF8
            $after = Get-Content -LiteralPath $cd.config_path -Raw | ConvertFrom-Json
            $lost = @($before | Where-Object { $_ -notin @($after.PSObject.Properties.Name) })
            if ($lost.Count -gt 0) {
                Copy-Item -LiteralPath $backup -Destination $cd.config_path -Force
                Act 'remove the configuration entry' $false ("it would have removed " + ($lost -join ', ') + " - restored the backup")
            }
            else { Act ("removed mcpServers.$NAME; backup " + (Split-Path -Leaf $backup)) $true $null }
        }
    }
    else { Act "the configuration entry was already absent" $true $null }

    # The extension itself is removed from inside Claude Desktop. Deleting the
    # app's own directory behind its back leaves its extension list describing
    # something that is no longer there.
    if ($cd.horizun_extension) {
        Say "The extension is still installed in Claude Desktop." 'Yellow'
        Say "Remove it from Settings > Extensions - this script does not delete entries from the app's store." 'Yellow'
    }
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
        Act "removed the staged package directory" $true $null
    }
    Remove-HorizunIntegrationState -Client $CLIENT -StatusPath $StatusPath | Out-Null
    if ($problems.Count -gt 0) { exit 1 }
    exit 0
}

# --- prepare the package --------------------------------------------------------
$repo = Split-Path -Parent $PSScriptRoot
if (-not $PackagePath) {
    $candidates = @()
    # Beside the installed server first: that is where the installer puts it, and
    # it is the copy whose version matches the installed pair.
    $beside = Join-Path (Split-Path -Parent $ServerPath) 'integrations\claude-desktop'
    if (Test-Path -LiteralPath $beside) { $candidates += @(Get-ChildItem -LiteralPath $beside -Filter '*.mcpb' -ErrorAction SilentlyContinue) }
    $inDist = Join-Path $repo 'dist'
    if (Test-Path -LiteralPath $inDist) { $candidates += @(Get-ChildItem -LiteralPath $inDist -Filter '*.mcpb' -ErrorAction SilentlyContinue) }
    if ($candidates.Count -gt 0) { $PackagePath = ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
}

$manifestOk = $false
if ($PackagePath -and (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    try {
        $pkg = Get-HorizunMcpbManifestFromPackage -Path $PackagePath
        $mp = @(Test-HorizunMcpbManifest $pkg.Manifest)
        if ($mp.Count -gt 0) { Act 'validate the package manifest' $false ($mp -join '; ') }
        else {
            Act ("package {0} carries a valid manifest: {1} {2}" -f (Split-Path -Leaf $PackagePath), $pkg.Manifest.name, $pkg.Manifest.version) $true $null
            $manifestOk = $true
        }
    }
    catch { Act 'read the package manifest' $false $_.Exception.Message }
}
else {
    Act 'find the extension package' $false ("no .mcpb found beside the installed server or in dist\. " +
        "Build one with scripts/build-mcpb.ps1, or pass -PackagePath.")
}

if ($manifestOk) {
    if (-not (Test-Path -LiteralPath $stageRoot)) { New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null }
    $stagedPath = Join-Path $stageRoot (Split-Path -Leaf $PackagePath)

    # THE FILE THE USER INSTALLS NAMES THE RESOLVED PATH, NOT ${HOME}.
    #
    # The published package writes ${HOME} so no account name ships. Whether a
    # given host substitutes that variable is a HOST behaviour, and this project
    # has not demonstrated it - an extension that depends on it would install
    # cleanly and then show no tools, with nothing anywhere saying why. So the
    # copy staged for installation is rewritten here with the path this machine
    # actually has, and validated under the Local rules, which REQUIRE the file
    # to exist and REFUSE ${HOME}.
    $rebuilt = $false
    if ($serverPresent) {
        try {
            $localPkg = Convert-HorizunMcpbToLocal -PackagePath $PackagePath -OutputPath $stagedPath -ServerPath $ServerPath
            $rebuilt = $true
            Act ("built the machine-resolved extension: command {0}" -f $localPkg.Manifest.server.mcp_config.command) $true $null
        }
        catch { Act 'build the machine-resolved extension' $false $_.Exception.Message }
    }
    if (-not $rebuilt) {
        # Fall back to the published copy. It is a valid extension; it merely
        # depends on the host substituting ${HOME}, which is why the state below
        # records that dependency instead of hiding it.
        Copy-Item -LiteralPath $PackagePath -Destination $stagedPath -Force
        $sourceSha = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLower()
        if ((Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLower() -ne $sourceSha) {
            Act 'stage the package' $false 'the copy does not match the source byte for byte'
        }
        else { Act 'staged the PUBLISHED package; its command depends on the host expanding ${HOME}' $true $null }
    }
    $stagedSha = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLower()
    Act ("staged {0} ({1} bytes, sha {2}...)" -f (Split-Path -Leaf $stagedPath), (Get-Item $stagedPath).Length, $stagedSha.Substring(0, 12)) $true $null
}

# --- the configuration fallback --------------------------------------------------
$configWritten = $false
if ($ConfigFallback) {
    Write-Host ""
    Write-Host "Writing the documented configuration entry" -ForegroundColor Cyan
    if (-not $serverPresent) {
        Act 'write mcpServers.horizun-revit' $false 'the server is not installed; the entry would point at a file that does not exist'
    }
    elseif ($cd.running -and -not $Force) {
        Act 'write mcpServers.horizun-revit' $false `
            'Claude Desktop is RUNNING. It rewrites claude_desktop_config.json from memory, so this edit would be lost silently and the tools would simply never appear. Close every Claude Desktop window and re-run (or pass -Force to write anyway).'
    }
    else {
        try {
            $cfg = if ($cd.config_exists) { Get-Content -LiteralPath $cd.config_path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
            $beforeTop = @($cfg.PSObject.Properties.Name)
            if (-not $cfg.PSObject.Properties['mcpServers']) {
                $cfg | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{}) -Force
            }
            $beforeServers = @($cfg.mcpServers.PSObject.Properties.Name)

            # ALREADY EXPANDED, deliberately. cmd.exe expands %LOCALAPPDATA% and
            # this app does not, so a configuration written with the variable
            # points at a path that does not exist and the client says nothing.
            $entry = [pscustomobject]@{ command = $ServerPath; args = @() }

            $backup = $null
            if ($cd.config_exists) {
                $backup = "$($cd.config_path).horizun-bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
                Copy-Item -LiteralPath $cd.config_path -Destination $backup -Force
            }
            else {
                $dir = Split-Path -Parent $cd.config_path
                if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            }

            $cfg.mcpServers | Add-Member -NotePropertyName $NAME -NotePropertyValue $entry -Force
            $out = $cfg | ConvertTo-Json -Depth 100
            $null = $out | ConvertFrom-Json     # never replace a config with one that will not parse
            Set-Content -LiteralPath $cd.config_path -Value $out -Encoding UTF8

            $after = Get-Content -LiteralPath $cd.config_path -Raw | ConvertFrom-Json
            $afterTop = @($after.PSObject.Properties.Name)
            $afterServers = @($after.mcpServers.PSObject.Properties.Name)
            $lostTop = @($beforeTop | Where-Object { $_ -notin $afterTop })
            $lostServers = @($beforeServers | Where-Object { $_ -notin $afterServers })

            if ($lostTop.Count -gt 0 -or $lostServers.Count -gt 0) {
                if ($backup) { Copy-Item -LiteralPath $backup -Destination $cd.config_path -Force }
                Act 'write mcpServers.horizun-revit' $false `
                    ("it would have removed " + (($lostTop + $lostServers) -join ', ') + " - restored the backup and changed nothing")
            }
            elseif ($NAME -notin $afterServers) {
                if ($backup) { Copy-Item -LiteralPath $backup -Destination $cd.config_path -Force }
                Act 'write mcpServers.horizun-revit' $false 'the entry was not there after writing - restored the backup'
            }
            elseif ($after.mcpServers.$NAME.command -ne $ServerPath) {
                if ($backup) { Copy-Item -LiteralPath $backup -Destination $cd.config_path -Force }
                Act 'write mcpServers.horizun-revit' $false 'the entry came back naming a different command - restored the backup'
            }
            else {
                $configWritten = $true
                Act ("wrote mcpServers.$NAME; {0} other top-level key(s) and {1} other server(s) intact{2}" -f `
                     $afterTop.Count, ($afterServers.Count - 1), $(if ($backup) { "; backup " + (Split-Path -Leaf $backup) } else { '' })) $true $null
            }
        }
        catch { Act 'write mcpServers.horizun-revit' $false $_.Exception.Message }
    }
}

# --- record the state, and name the one step nobody else can take ----------------
Write-Host ""
$state = $null; $detail = $null; $pending = $null
$evidence = [ordered]@{
    claude_desktop_version = $cd.version
    packaging              = $cd.packaging
    config_path            = $cd.config_path
    extensions_dir         = $cd.extensions_dir
    other_extensions       = @($cd.extensions | Where-Object { $_.name -ne $NAME } | ForEach-Object { $_.name })
    staged_package         = $(if ($manifestOk) { $stagedPath } else { $null })
    staged_package_sha256  = $(if ($manifestOk) { $stagedSha } else { $null })
    staged_command_resolved = [bool]$rebuilt
    depends_on_home_substitution = -not [bool]$rebuilt
    server_path            = $ServerPath
    server_answers_mcp     = $(if ($probe) { [bool]$probe.ok } else { $false })
    server_tool_count      = $(if ($probe) { $probe.tool_count } else { 0 })
    extension_installed    = [bool]$cd.horizun_extension
    config_entry_present   = [bool]($cd.horizun_in_config -or $configWritten)
}

if ($problems.Count -gt 0) {
    $state = 'failed'
    $detail = 'Preparation did not complete: ' + ($problems -join ' | ')
}
elseif ($configWritten -or $cd.horizun_in_config) {
    if ($cd.running) {
        $state = 'pending_client_restart'
        $detail = "mcpServers.$NAME is in claude_desktop_config.json. Claude Desktop is running and will not see it until it restarts."
    }
    else {
        $state = 'configured'
        $detail = "mcpServers.$NAME names the installed server, read back from the file after writing."
    }
}
elseif ($cd.horizun_extension) {
    $state = if ($cd.running) { 'pending_client_restart' } else { 'configured' }
    $detail = "The '$NAME' extension is installed in Claude Desktop."
    if ($cd.running) { $detail += ' Claude Desktop is running and will not list the tools until it restarts.' }
}
else {
    $state = 'pending_user_action'
    $detail = "Everything up to the install step is done: the package is staged and validated, and the server it names answers MCP."
    $pending = ("In Claude Desktop: Settings > Extensions > Advanced settings > Install Extension, and choose " +
                "$stagedPath - then restart Claude Desktop. There is no documented command for that step, so no script takes it.")
}

Set-HorizunIntegrationState -Client $CLIENT -State $state -Detail $detail -PendingUserAction $pending `
    -Evidence ([pscustomobject]$evidence) -StatusPath $StatusPath | Out-Null

Write-Host ("  state: {0}" -f $state) -ForegroundColor $(if ($state -in @('configured','verified')) { 'Green' } elseif ($state -eq 'failed') { 'Red' } else { 'Yellow' })
Say $detail
if ($pending) { Write-Host ""; Say "ONE STEP REMAINS, and it is yours:" 'Cyan'; Say $pending 'Cyan' }

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        generated_utc       = (Get-Date).ToUniversalTime().ToString('o')
        client              = $CLIENT
        state               = $state
        detail              = $detail
        pending_user_action = $pending
        evidence            = [pscustomobject]$evidence
        actions             = $actions
        problems            = $problems
    } | ConvertTo-Json -Depth 10 | Out-File -FilePath $Json -Encoding utf8
    Say "wrote $Json"
}

if ($problems.Count -gt 0) { exit 1 }
if ($state -eq 'pending_user_action') { exit 3 }
exit 0
