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

function Get-HorizunTunnelClient {
    <#
      Find OpenAI's tunnel-client, the officially supported way to reach a private
      MCP server from ChatGPT. It is NOT shipped by this product and never
      downloaded by it: this reports whether the user has installed it.
    #>
    [CmdletBinding()]
    param([string]$Override)

    $info = [ordered]@{
        client            = 'chatgpt-tunnel'
        installed         = $false
        path              = $null
        version           = $null
        source            = $null
        # Provenance, as far as a file on disk can carry it. tunnel-client is
        # published unsigned on GitHub Releases, so an Authenticode status of
        # NotSigned is the expected answer, not a finding - what matters is that
        # the user can see WHICH file is being run and where it came from.
        sha256            = $null
        signature_status  = $null
        signer            = $null
        file_version      = $null
        # Whether this build accepts the flag the whole integration depends on.
        supports_mcp_command = $null
        download_from     = 'https://github.com/openai/tunnel-client/releases/latest'
        problem           = $null
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Override) { $candidates.Add($Override) | Out-Null }
    if ($env:HORIZUN_TUNNEL_CLIENT) { $candidates.Add($env:HORIZUN_TUNNEL_CLIENT) | Out-Null }
    $onPath = Get-Command 'tunnel-client' -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) | Out-Null }
    foreach ($guess in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\tunnel-client\tunnel-client.exe'),
        (Join-Path $env:LOCALAPPDATA 'Horizun\integrations\chatgpt\tunnel-client.exe'),
        (Join-Path $env:USERPROFILE '.local\bin\tunnel-client.exe'))) {
        $candidates.Add($guess) | Out-Null
    }

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        if (-not (Test-Path -LiteralPath $c -PathType Leaf)) { continue }
        $info.installed = $true
        $info.path = (Resolve-Path -LiteralPath $c).Path
        $info.source = if ($Override) { 'explicit' } elseif ($onPath -and $onPath.Source -eq $c) { 'PATH' } else { 'well-known location' }
        try {
            $item = Get-Item -LiteralPath $info.path
            $info.file_version = $item.VersionInfo.FileVersion
            $info.sha256 = (Get-FileHash -LiteralPath $info.path -Algorithm SHA256).Hash.ToLower()
            $sig = Get-AuthenticodeSignature -LiteralPath $info.path
            $info.signature_status = [string]$sig.Status
            if ($sig.SignerCertificate) { $info.signer = $sig.SignerCertificate.Subject }
        }
        catch { }
        # ASKING AN ARBITRARY EXECUTABLE FOR ITS HELP CAN HANG FOREVER.
        # -TunnelClientPath points wherever the caller says, and a program that
        # ignores its arguments and waits on stdin - cmd.exe, for one - blocks
        # the diagnosis indefinitely. Measured here: `cmd.exe help quickstart`
        # starts an interactive shell and never returns. So every probe runs with
        # stdin CLOSED and a hard deadline, and a timeout is an answer.
        $runProbe = {
            param([string]$exe, [string[]]$probeArgs, [int]$seconds)
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $exe
            $psi.Arguments = (($probeArgs | ForEach-Object {
                if ($_ -notmatch '[\s"]') { $_ }
                else { '"' + ([regex]::Replace($_, '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
            }) -join ' ')
            $psi.UseShellExecute = $false
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $p = $null
            try { $p = [Diagnostics.Process]::Start($psi) } catch { return $null }
            try {
                $p.StandardInput.Close()          # never let it wait for input
                $stdout = $p.StandardOutput.ReadToEndAsync()
                $stderr = $p.StandardError.ReadToEndAsync()
                if (-not $p.WaitForExit($seconds * 1000)) {
                    try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
                    return $null
                }
                return ($stdout.Result + "`n" + $stderr.Result)
            }
            catch { return $null }
            finally { if ($p -and -not $p.HasExited) { try { $p.Kill() } catch { } } }
        }

        $versionText = & $runProbe $info.path @('version') 10
        if ($null -eq $versionText) { $info.problem = 'found, but it did not answer `version` within 10 seconds' }
        else { $info.version = $versionText.Trim() }

        # THE FLAG THE WHOLE INTEGRATION RESTS ON. Horizun's server is stdio; if
        # this build has no --mcp-command there is nothing to connect and the
        # honest answer is to say so now rather than after a failed `run`.
        $help = ''
        foreach ($probe in @(@('help', 'quickstart'), @('init', '--help'))) {
            $text = & $runProbe $info.path $probe 10
            if ($null -ne $text) { $help += $text }
        }
        # No help at all is UNKNOWN, not unsupported: a build that would not talk
        # is a different fact from a build that answered and lacks the flag.
        if ([string]::IsNullOrWhiteSpace($help)) { $info.supports_mcp_command = $null }
        else { $info.supports_mcp_command = [bool]($help -match '--mcp-command') }
        break
    }
    return [pscustomobject]$info
}

function Get-HorizunChatGptDesktop {
    <#
      Whether the ChatGPT desktop app is installed. Presence of the app says
      NOTHING about whether this account may create developer-mode MCP apps -
      that is a workspace/plan permission, checked in the product, not on disk.
      The status this returns therefore never claims capability.
    #>
    [CmdletBinding()]
    param()
    $info = [ordered]@{
        client              = 'chatgpt-desktop'
        installed           = $false
        package_family_name = $null
        version             = $null
        install_location    = $null
        running             = $false
    }
    $appx = $null
    try { $appx = Get-AppxPackage -Name 'OpenAI.ChatGPT-Desktop' -ErrorAction SilentlyContinue | Select-Object -First 1 } catch { $appx = $null }
    if ($appx) {
        $info.installed = $true
        $info.package_family_name = $appx.PackageFamilyName
        $info.version = [string]$appx.Version
        $info.install_location = $appx.InstallLocation
    }
    $info.running = @(Get-Process -Name 'ChatGPT' -ErrorAction SilentlyContinue).Count -gt 0
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
