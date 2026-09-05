#Requires -Version 5.1
<#
  Find the MCP clients on this machine, and say what each can actually do.

  WHY THIS IS NOT A PATH CONSTANT. Claude Desktop ships two ways on Windows. The
  classic installer puts its configuration at %APPDATA%\Claude. The Store/MSIX
  build does not: its %APPDATA% is redirected into the package container, so the
  same file lives at

      %LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Roaming\Claude\

  A check that tests only the first path reports "Claude Desktop is not installed"
  on a machine where it is installed, running, and already carrying extensions.
  That mistake was made on this machine during this work; this function exists so
  it cannot be made again.

  Nothing here writes anything. It looks, and reports what it saw.
#>

function Get-HorizunClaudeDesktop {
    <#
      Returns one object describing Claude Desktop: whether it is installed, how,
      where its configuration and extension store live, which extensions are
      already there, and whether it is running right now.

      installed=$false is a normal answer, not an error.
    #>
    [CmdletBinding()]
    param(
        # Test seam: pretend the roaming directory is somewhere else. Used by the
        # gate so it can exercise every branch without a Claude Desktop.
        [string]$RoamingOverride
    )

    $info = [ordered]@{
        client              = 'claude-desktop'
        installed           = $false
        packaging           = $null      # 'msix' | 'classic' | $null
        package_family_name = $null
        version             = $null
        install_location    = $null
        roaming_dir         = $null
        config_path         = $null
        config_exists       = $false
        extensions_dir      = $null
        extensions          = @()
        horizun_extension   = $null
        horizun_in_config   = $false
        running             = $false
        problem             = $null
    }

    if ($RoamingOverride) {
        $info.installed = Test-Path -LiteralPath $RoamingOverride -PathType Container
        $info.packaging = 'override'
        $info.roaming_dir = $RoamingOverride
    }
    else {
        # 1. MSIX / Store. Get-AppxPackage is the only reliable way to find the
        #    container name; guessing the family suffix is not possible.
        $appx = $null
        try { $appx = Get-AppxPackage -Name 'Claude' -ErrorAction SilentlyContinue | Select-Object -First 1 } catch { $appx = $null }
        if ($appx) {
            $candidate = Join-Path $env:LOCALAPPDATA ("Packages\{0}\LocalCache\Roaming\Claude" -f $appx.PackageFamilyName)
            $info.installed = $true
            $info.packaging = 'msix'
            $info.package_family_name = $appx.PackageFamilyName
            $info.version = [string]$appx.Version
            $info.install_location = $appx.InstallLocation
            $info.roaming_dir = $candidate
        }
        else {
            # 2. Classic per-user installer.
            $classic = Join-Path $env:APPDATA 'Claude'
            if (Test-Path -LiteralPath $classic -PathType Container) {
                $info.installed = $true
                $info.packaging = 'classic'
                $info.roaming_dir = $classic
                $exe = Join-Path $env:LOCALAPPDATA 'AnthropicClaude'
                if (Test-Path -LiteralPath $exe) { $info.install_location = $exe }
            }
        }
    }

    if ($info.roaming_dir) {
        $info.config_path = Join-Path $info.roaming_dir 'claude_desktop_config.json'
        $info.config_exists = Test-Path -LiteralPath $info.config_path -PathType Leaf
        $info.extensions_dir = Join-Path $info.roaming_dir 'Claude Extensions'
        if (Test-Path -LiteralPath $info.extensions_dir -PathType Container) {
            $found = New-Object System.Collections.Generic.List[object]
            foreach ($dir in @(Get-ChildItem -LiteralPath $info.extensions_dir -Directory -ErrorAction SilentlyContinue)) {
                $mf = Join-Path $dir.FullName 'manifest.json'
                $entry = [ordered]@{ id = $dir.Name; path = $dir.FullName; name = $null; version = $null }
                if (Test-Path -LiteralPath $mf -PathType Leaf) {
                    try {
                        $m = Get-Content -LiteralPath $mf -Raw | ConvertFrom-Json
                        $entry.name = [string]$m.name
                        $entry.version = [string]$m.version
                    }
                    catch { $entry.name = '<unreadable manifest>' }
                }
                $found.Add([pscustomobject]$entry) | Out-Null
            }
            $info.extensions = $found.ToArray()
            $info.horizun_extension = @($info.extensions | Where-Object { $_.name -eq 'horizun-revit' }) | Select-Object -First 1
        }
        if ($info.config_exists) {
            try {
                $cfg = Get-Content -LiteralPath $info.config_path -Raw | ConvertFrom-Json
                $servers = $cfg.PSObject.Properties['mcpServers']
                if ($servers -and $servers.Value) {
                    $info.horizun_in_config = 'horizun-revit' -in @($servers.Value.PSObject.Properties.Name)
                }
            }
            catch { $info.problem = "claude_desktop_config.json does not parse: $($_.Exception.Message)" }
        }
    }

    # "claude" also matches the Claude Code CLI, which is a different product and
    # does not own claude_desktop_config.json. Match the desktop app's own
    # process names only.
    $info.running = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @('Claude', 'claude-desktop', 'AnthropicClaude') }).Count -gt 0

    return [pscustomobject]$info
}

function Get-HorizunExistingClients {
    <# Codex and Claude Code: the two already registered, which must not break. #>
    [CmdletBinding()]
    param()
    $claudeCode = Join-Path $env:USERPROFILE '.claude.json'
    $codex = Join-Path $env:USERPROFILE '.codex\config.toml'
    $out = New-Object System.Collections.Generic.List[object]

    $entry = [ordered]@{ client = 'claude-code'; config_path = $claudeCode; config_exists = (Test-Path -LiteralPath $claudeCode -PathType Leaf); registered = $false }
    if ($entry.config_exists) {
        try {
            $cfg = Get-Content -LiteralPath $claudeCode -Raw | ConvertFrom-Json
            if ($cfg.mcpServers) { $entry.registered = 'horizun-revit' -in @($cfg.mcpServers.PSObject.Properties.Name) }
        }
        catch { }
    }
    $out.Add([pscustomobject]$entry) | Out-Null

    $entry = [ordered]@{ client = 'codex'; config_path = $codex; config_exists = (Test-Path -LiteralPath $codex -PathType Leaf); registered = $false; registered_names = @() }
    if ($entry.config_exists) {
        $names = @(Select-String -LiteralPath $codex -Pattern '^\s*\[mcp_servers\.([^.\]]+)\]\s*$' -AllMatches |
                   ForEach-Object { $_.Matches[0].Groups[1].Value })
        $entry.registered_names = $names
        $entry.registered = @($names | Where-Object { $_ -match '^horizun' }).Count -gt 0
    }
    $out.Add([pscustomobject]$entry) | Out-Null

    return $out.ToArray()
}
