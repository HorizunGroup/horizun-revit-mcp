#Requires -Version 5.1
<#
  Does the extension work for somebody who is not the person who built it?

  The published .mcpb is a file other people install. Every question below is one
  a different Windows account, a different install path, or a different moment in
  the update cycle would ask, and each is answered by DOING it rather than by
  reading the manifest and hoping.

  What it exercises:
    - the published copy is portable and carries no account name
    - the copy a machine installs names a RESOLVED path and does not depend on
      ${HOME} being substituted by the host
    - a server path containing spaces and non-ASCII characters survives the
      manifest, the JSON round-trip, and an actual process launch
    - Horizun not installed is refused with a reason, not staged broken
    - replacing the server binary in place does NOT require reinstalling the
      extension
    - removal touches nothing that belongs to another extension
    - MSIX and classic Claude Desktop layouts are both read

  It needs no Claude Desktop and no Revit.
#>
$ErrorActionPreference = 'Stop'

$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $name" -ForegroundColor Red
        if ($detail) { Write-Host "        $detail" }
        $script:failed++
    }
}

. (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-stdio.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')

$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-portability-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$builder = Join-Path $PSScriptRoot 'build-mcpb.ps1'
$staged = Join-Path $repo "dist\stage\server\integrations\claude-desktop\horizun-revit-$version.mcpb"
$realServer = Join-Path $repo 'dist\stage\server\horizun-mcp.exe'
if (-not (Test-Path -LiteralPath $realServer)) {
    $realServer = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
}

try {
    # ======================================================================
    Write-Host ""
    Write-Host "The published copy belongs to nobody" -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $staged)) {
        Write-Host "  SKIP  no staged package; run scripts/pack.ps1" -ForegroundColor Yellow
    }
    else {
        $pub = Get-HorizunMcpbManifestFromPackage -Path $staged
        Assert 'the published extension validates under the Published rules' `
            ((@(Test-HorizunMcpbManifest $pub.Manifest -Distribution Published -ExpectedVersion $version)).Count -eq 0) `
            ((Test-HorizunMcpbManifest $pub.Manifest -Distribution Published -ExpectedVersion $version) -join '; ')

        # The decisive portability question, asked as somebody else would ask it:
        # substitute a DIFFERENT account's home and see whether the command still
        # points inside that account rather than back at the builder's.
        $others = @('C:\Users\someone', 'C:\Users\someone else', 'D:\Perfiles\usuario')
        $wrong = @()
        foreach ($otherHome in $others) {
            $resolved = $pub.Manifest.server.mcp_config.command.Replace('${HOME}', $otherHome.Replace('\', '/')).Replace('/', '\')
            if (-not $resolved.StartsWith($otherHome, [StringComparison]::OrdinalIgnoreCase)) { $wrong += $resolved }
        }
        Assert 'substituting any other account home resolves inside THAT account' `
            ($wrong.Count -eq 0) ($wrong -join '; ')

        Assert 'the published copy is REFUSED under the Local rules (it depends on a substitution)' `
            ((@(Test-HorizunMcpbManifest $pub.Manifest -Distribution Local)).Count -gt 0) `
            'a published package validated as a local one; the two must not be interchangeable'
    }

    # ======================================================================
    Write-Host ""
    Write-Host "The copy a machine installs depends on nothing it cannot prove" -ForegroundColor Cyan

    $localPath = Join-Path $root 'resolved.mcpb'
    & pwsh -NoProfile -File $builder -Local -Output $localPath -ServerPath $realServer *> $null
    $builtLocal = Test-Path -LiteralPath $localPath
    Assert 'a machine-resolved extension builds' $builtLocal $null
    if ($builtLocal) {
        $loc = Get-HorizunMcpbManifestFromPackage -Path $localPath
        Assert 'it validates under the Local rules' `
            ((@(Test-HorizunMcpbManifest $loc.Manifest -Distribution Local)).Count -eq 0) `
            ((Test-HorizunMcpbManifest $loc.Manifest -Distribution Local) -join '; ')
        Assert 'its command contains no ${HOME}: nothing has to substitute anything' `
            ($loc.Manifest.server.mcp_config.command -notlike '*${HOME}*') $loc.Manifest.server.mcp_config.command
        Assert 'its command is a file that exists right now' `
            (Test-Path -LiteralPath $loc.Manifest.server.mcp_config.command -PathType Leaf) $loc.Manifest.server.mcp_config.command
        Assert 'it is REFUSED under the Published rules (it carries an account name)' `
            ((@(Test-HorizunMcpbManifest $loc.Manifest -Distribution Published)).Count -gt 0) $null
    }

    # The installed wizard has only the portable package, this library and the
    # installed server. Prove that it does not secretly depend on source-only
    # artwork, Directory.Build.props or build-mcpb.ps1.
    if (Test-Path -LiteralPath $staged) {
        $installedLike = Join-Path $root 'installed-client-tools'
        New-Item -ItemType Directory -Path $installedLike -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1') -Destination $installedLike
        $isolatedPackage = Join-Path $installedLike (Split-Path -Leaf $staged)
        Copy-Item -LiteralPath $staged -Destination $isolatedPackage
        $isolatedOutput = Join-Path $root 'installed-wizard-resolved.mcpb'
        $converted = Convert-HorizunMcpbToLocal -PackagePath $isolatedPackage -OutputPath $isolatedOutput -ServerPath $realServer
        Assert 'the installed wizard can resolve the package without the source tree or builder' `
            ((Test-Path -LiteralPath $isolatedOutput) -and $converted.Manifest.server.mcp_config.command -eq [IO.Path]::GetFullPath($realServer)) `
            $converted.Manifest.server.mcp_config.command
        $publishedEntries = @(Get-HorizunMcpbManifestFromPackage -Path $isolatedPackage).Entries | Where-Object { $_ -ne 'manifest.json' }
        $localEntries = @(Get-HorizunMcpbManifestFromPackage -Path $isolatedOutput).Entries | Where-Object { $_ -ne 'manifest.json' }
        Assert 'rewriting the command preserves every non-manifest package entry' `
            (($publishedEntries -join '|') -eq ($localEntries -join '|')) `
            ("published=" + ($publishedEntries -join ',') + " local=" + ($localEntries -join ','))
    }

    # -Local refuses to write where published artifacts are hashed from.
    $intoDist = Join-Path $repo 'dist\should-never-exist.mcpb'
    & pwsh -NoProfile -File $builder -Local -Output $intoDist -ServerPath $realServer *> $null
    Assert '-Local refuses to write into dist\, where published artifacts come from' `
        (-not (Test-Path -LiteralPath $intoDist)) 'a machine-resolved package was written into dist\'
    Remove-Item -LiteralPath $intoDist -Force -ErrorAction SilentlyContinue

    # ======================================================================
    Write-Host ""
    Write-Host "Awkward paths: spaces and non-ASCII" -ForegroundColor Cyan

    # A real copy of the real server, at a path that breaks naive quoting. Both
    # halves matter: JSON must round-trip it, and a process must actually start.
    $awkwardDir = Join-Path $root 'Archivos de programa\Cañón Ñandú 建築\Horizun MCP'
    New-Item -ItemType Directory -Path $awkwardDir -Force | Out-Null
    $awkwardServer = Join-Path $awkwardDir 'horizun-mcp.exe'
    Copy-Item -LiteralPath $realServer -Destination $awkwardServer -Force
    foreach ($side in @('horizun-mcp.dll', 'horizun-mcp.runtimeconfig.json', 'horizun-mcp.deps.json')) {
        $s = Join-Path (Split-Path -Parent $realServer) $side
        if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination $awkwardDir -Force }
    }
    foreach ($dll in @(Get-ChildItem -LiteralPath (Split-Path -Parent $realServer) -Filter *.dll -File)) {
        Copy-Item -LiteralPath $dll.FullName -Destination $awkwardDir -Force -ErrorAction SilentlyContinue
    }

    $awkwardManifest = New-HorizunMcpbManifest -Version $version -Command $awkwardServer
    Assert 'a path with spaces and non-ASCII characters validates as Local' `
        ((@(Test-HorizunMcpbManifest $awkwardManifest -Distribution Local)).Count -eq 0) `
        ((Test-HorizunMcpbManifest $awkwardManifest -Distribution Local) -join '; ')

    # JSON round-trip: the manifest is written and read as UTF-8 without a BOM,
    # and a non-ASCII path must survive that unchanged.
    $jsonPath = Join-Path $root 'awkward.json'
    [IO.File]::WriteAllText($jsonPath, ($awkwardManifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    $reread = (Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    Assert 'the JSON round-trip returns the same path, character for character' `
        ($reread.server.mcp_config.command -ceq $awkwardServer) `
        ("wrote [$awkwardServer] read [$($reread.server.mcp_config.command)]")

    $awkProbe = Invoke-HorizunMcpProbe -Command $awkwardServer -ListTools -TimeoutSec 120
    Assert 'a server at that path actually launches and answers MCP' `
        ($awkProbe.ok -and $awkProbe.tool_count -gt 0) $awkProbe.problem

    # ======================================================================
    Write-Host ""
    Write-Host "Horizun not installed" -ForegroundColor Cyan

    $missing = Join-Path $root 'nowhere\horizun-mcp.exe'
    $missingManifest = New-HorizunMcpbManifest -Version $version -Command $missing
    Assert 'a Local manifest naming a server that is not there is REFUSED' `
        ((@(Test-HorizunMcpbManifest $missingManifest -Distribution Local) -match 'does not exist').Count -gt 0) `
        'it would install cleanly and show no tools, with nothing saying why'

    $wizardStatus = Join-Path $root 'nostatus.json'
    $fakeRoaming = Join-Path $root 'roaming-empty'
    New-Item -ItemType Directory -Path $fakeRoaming -Force | Out-Null
    '{}' | Set-Content -LiteralPath (Join-Path $fakeRoaming 'claude_desktop_config.json') -Encoding UTF8
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'install-claude-desktop-extension.ps1') `
        -RoamingOverride $fakeRoaming -ServerPath $missing -StatusPath $wizardStatus 2>&1 | Out-String
    Assert 'the wizard refuses when the server is not installed, and says to install it first' `
        ($out -match 'does not exist' -and $out -match '(?i)install Horizun Revit MCP first') $out
    $st = (Get-Content -LiteralPath $wizardStatus -Raw | ConvertFrom-Json).integrations.'claude-desktop'
    Assert 'and records failed rather than a cheerful pending' ($st.state -eq 'failed') $st.state

    # ======================================================================
    Write-Host ""
    Write-Host "Updating the server does not require reinstalling the extension" -ForegroundColor Cyan

    # The extension names a PATH. Replace the bytes at that path - which is what
    # every install.ps1 does - and the extension must follow with no second act.
    $updDir = Join-Path $root 'installed'
    New-Item -ItemType Directory -Path $updDir -Force | Out-Null
    $updServer = Join-Path $updDir 'horizun-mcp.exe'
    Copy-Item -LiteralPath $realServer -Destination $updServer -Force

    $updPkg = Join-Path $root 'update.mcpb'
    # -NoToolList: this scenario is about the PATH surviving an update, and the
    # copied exe has no runtime beside it to launch. The tool list is proved
    # against a real server elsewhere in this file.
    & pwsh -NoProfile -File $builder -Local -Output $updPkg -ServerPath $updServer -NoToolList *> $null
    if (-not (Test-Path -LiteralPath $updPkg)) { throw "the update-scenario package did not build" }
    $before = (Get-FileHash -LiteralPath $updPkg -Algorithm SHA256).Hash
    $commandBefore = (Get-HorizunMcpbManifestFromPackage -Path $updPkg).Manifest.server.mcp_config.command

    # "A new release lands." Different bytes, same path.
    $bytes = [IO.File]::ReadAllBytes($updServer)
    [IO.File]::WriteAllBytes($updServer, $bytes)          # rewrite in place
    Add-Content -LiteralPath (Join-Path $updDir 'marker.txt') -Value 'a newer release was installed here'
    $commandAfter = (Get-HorizunMcpbManifestFromPackage -Path $updPkg).Manifest.server.mcp_config.command

    Assert 'the extension still names the same command after the server is replaced' `
        ($commandBefore -eq $commandAfter -and $commandAfter -eq $updServer) "before=$commandBefore after=$commandAfter"
    Assert 'the extension file itself did not have to change' `
        ((Get-FileHash -LiteralPath $updPkg -Algorithm SHA256).Hash -eq $before) $null
    Assert 'and the command still resolves to a file that exists' `
        (Test-Path -LiteralPath $commandAfter -PathType Leaf) $commandAfter

    # ======================================================================
    Write-Host ""
    Write-Host "Removal and repair leave other extensions alone" -ForegroundColor Cyan

    $roaming = Join-Path $root 'roaming'
    $extDir = Join-Path $roaming 'Claude Extensions'
    New-Item -ItemType Directory -Path $extDir -Force | Out-Null
    foreach ($other in @('ant.dir.someone.filesystem', 'ant.dir.someone.windows')) {
        $d = Join-Path $extDir $other
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        @{ manifest_version = '0.3'; name = $other; version = '1.0.0' } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $d 'manifest.json') -Encoding UTF8
    }
    @{ preferences = @{ keep = $true }
       mcpServers = @{ 'someone-elses' = @{ command = 'C:\other.exe' } } } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $roaming 'claude_desktop_config.json') -Encoding UTF8

    $before = @(Get-ChildItem -LiteralPath $extDir -Directory | ForEach-Object { $_.Name })
    $s2 = Join-Path $root 'status2.json'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'install-claude-desktop-extension.ps1') `
        -ConfigFallback -RoamingOverride $roaming -ServerPath $updServer -StatusPath $s2 -Force *> $null
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'install-claude-desktop-extension.ps1') `
        -Remove -RoamingOverride $roaming -StatusPath $s2 -Force *> $null
    $after = @(Get-ChildItem -LiteralPath $extDir -Directory | ForEach-Object { $_.Name })
    $cfg = Get-Content -LiteralPath (Join-Path $roaming 'claude_desktop_config.json') -Raw | ConvertFrom-Json

    Assert 'every other extension directory is still there, untouched' `
        (@($before | Where-Object { $_ -notin $after }).Count -eq 0) `
        (@($before | Where-Object { $_ -notin $after }) -join ', ')
    Assert 'the other MCP server and the preferences survived removal' `
        ($cfg.mcpServers.'someone-elses'.command -eq 'C:\other.exe' -and $cfg.preferences.keep -eq $true) $null
    Assert 'and the horizun-revit entry is gone' ($null -eq $cfg.mcpServers.'horizun-revit') $null

    # ======================================================================
    Write-Host ""
    Write-Host "Both Claude Desktop layouts" -ForegroundColor Cyan

    $seen = Get-HorizunClaudeDesktop -RoamingOverride $roaming
    Assert 'a roaming directory in the MSIX container shape is read like any other' `
        ($seen.installed -and $seen.config_exists -and $seen.extensions.Count -eq 2) `
        "installed=$($seen.installed) config=$($seen.config_exists) extensions=$($seen.extensions.Count)"

    $real = Get-HorizunClaudeDesktop
    if ($real.installed) {
        Assert "this machine's Claude Desktop is found by packaging '$($real.packaging)'" `
            ($real.packaging -in @('msix', 'classic')) $real.packaging
        Assert 'and its configuration path is inside the location that packaging implies' `
            $(if ($real.packaging -eq 'msix') { $real.config_path -like '*\Packages\*\LocalCache\Roaming\Claude\*' }
              else { $real.config_path -like "$env:APPDATA\Claude\*" }) $real.config_path
    }
    else { Write-Host "  SKIP  no Claude Desktop on this machine to cross-check" -ForegroundColor Yellow }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host ""; Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host ""
Write-Host 'mcpb portability: the published copy belongs to nobody and the installed copy assumes nothing.' -ForegroundColor Green
exit 0
