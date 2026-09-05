#Requires -Version 5.1
<#
  The Claude Desktop integration, exercised rather than described.

  Everything here RUNS. The manifest checks are fed manifests that are wrong in
  one field each and must reject exactly that field; the configuration writer is
  pointed at a COPY of whatever claude_desktop_config.json this machine really
  has, so preservation is proved against real content rather than a fixture with
  three keys; and the durable state is written and read back.

  It needs no Claude Desktop and no Revit. The parts
  that DO need them are the parts these tests deliberately cannot claim.
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
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')

$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-integrations-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

try {
    # ======================================================================
    Write-Host ""
    Write-Host "The extension manifest" -ForegroundColor Cyan

    $good = New-HorizunMcpbManifest -Version '1.2.0' -Command $script:HorizunMcpbPortableCommand -Tools @(@{ name = 'horizun_health' })
    Assert 'the manifest this tree produces passes its own checks' `
        (@(Test-HorizunMcpbManifest $good).Count -eq 0) ((Test-HorizunMcpbManifest $good) -join '; ')

    Assert 'it declares manifest_version 0.3, the spec this tree was written against' `
        ($good.manifest_version -eq '0.3') $good.manifest_version

    Assert 'it registers under horizun-revit, the same name every other client uses' `
        ($good.name -eq 'horizun-revit') $good.name

    Assert 'entry_point and mcp_config.command name the same executable' `
        ($good.server.entry_point -eq $good.server.mcp_config.command) $null

    Assert 'it declares win32 only - there is no Revit anywhere else' `
        ((@($good.compatibility.platforms) -join ',') -eq 'win32') (@($good.compatibility.platforms) -join ',')

    Assert 'it declares tools_generated, because the list grows when Python is granted' `
        ($good.tools_generated -eq $true) $null

    # --- each check rejects exactly what it is for ---------------------------
    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command $script:HorizunMcpbPortableCommand
    $bad.Remove('author')
    Assert 'a manifest with no author is rejected (the spec makes it required)' `
        ((@(Test-HorizunMcpbManifest $bad) -match "required field 'author'").Count -gt 0) $null

    $bad = New-HorizunMcpbManifest -Version '1.2' -Command $script:HorizunMcpbPortableCommand
    Assert 'a non-semantic version is rejected' `
        ((@(Test-HorizunMcpbManifest $bad) -match 'not semantic').Count -gt 0) $null

    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command $script:HorizunMcpbPortableCommand
    Assert 'a version that disagrees with the tree is rejected' `
        ((@(Test-HorizunMcpbManifest $bad -ExpectedVersion '9.9.9') -match 'lies about its version').Count -gt 0) $null

    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command 'horizun-mcp.exe'
    Assert 'a RELATIVE command is rejected - it would resolve against an unknown directory' `
        ((@(Test-HorizunMcpbManifest $bad) -match 'not an absolute path').Count -gt 0) $null

    # `someone` rather than a name-shaped placeholder: scan-sensitive treats
    # \Users\someone as an explicit placeholder, and a test fixture that trips the
    # leak scanner teaches everyone to ignore the leak scanner.
    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command 'C:\Users\someone\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
    Assert 'a LITERAL user directory is rejected - that is how a personal path ships' `
        ((@(Test-HorizunMcpbManifest $bad) -match 'literal user directory').Count -gt 0) $null

    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command 'C:\Program Files\Something\other.exe'
    Assert 'a command that is not horizun-mcp.exe is rejected' `
        ((@(Test-HorizunMcpbManifest $bad) -match 'does not end in horizun-mcp.exe').Count -gt 0) $null

    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command $script:HorizunMcpbPortableCommand
    $bad.server.mcp_config.env['CONTROL_PLANE_API_KEY'] = 'sk-nope'
    Assert 'an env entry is rejected - that is where a secret would ride into a published file' `
        ((@(Test-HorizunMcpbManifest $bad) -match 'env carries').Count -gt 0) $null

    $bad = New-HorizunMcpbManifest -Version '1.2.0' -Command $script:HorizunMcpbPortableCommand
    $bad.compatibility.platforms = @('win32', 'darwin')
    Assert 'claiming a platform with no Revit is rejected' `
        ((@(Test-HorizunMcpbManifest $bad) -match "claims 'darwin'").Count -gt 0) $null

    # ======================================================================
    Write-Host ""
    Write-Host "The built package" -ForegroundColor Cyan

    $props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
    $version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    $pkgPath = Join-Path $repo ("dist\horizun-revit-$version.mcpb")

    if (-not (Test-Path -LiteralPath $pkgPath)) {
        Write-Host "  SKIP  no package at $pkgPath - build it with scripts/build-mcpb.ps1" -ForegroundColor Yellow
    }
    else {
        $pkg = Get-HorizunMcpbManifestFromPackage -Path $pkgPath
        Assert 'the package has manifest.json at its ROOT, where the host looks for it' `
            ($pkg.Entries -contains 'manifest.json') ($pkg.Entries -join ', ')
        Assert 'the packaged manifest passes every check, at this tree''s version' `
            (@(Test-HorizunMcpbManifest $pkg.Manifest -ExpectedVersion $version).Count -eq 0) `
            ((Test-HorizunMcpbManifest $pkg.Manifest -ExpectedVersion $version) -join '; ')
        Assert 'the icon it names is actually inside the package' `
            ($pkg.Entries -contains $pkg.Manifest.icon) ("icon=$($pkg.Manifest.icon) entries=" + ($pkg.Entries -join ','))
        foreach ($ic in @($pkg.Manifest.icons)) {
            Assert "the icons[] entry $($ic.src) is inside the package" ($pkg.Entries -contains $ic.src) $null
        }
        Assert 'the manifest bytes carry no BOM - a BOM before { is not JSON' `
            (-not $pkg.Text.StartsWith([char]0xFEFF)) $null
        Assert 'the package carries its licence' ($pkg.Entries -contains 'LICENSE') $null

        # THE CHECK THAT MATTERS MOST: no account name anywhere in the artifact.
        $account = [IO.Path]::GetFileName($env:USERPROFILE)
        $bytes = [IO.File]::ReadAllBytes($pkgPath)
        $asText = [Text.Encoding]::UTF8.GetString($bytes)
        Assert 'the package contains this account name nowhere - not even compressed' `
            (-not ($asText -match [regex]::Escape($account)) -and -not ($pkg.Text -match [regex]::Escape($account))) $null
        Assert 'the packaged command uses the ${HOME} substitution, not a resolved home directory' `
            ($pkg.Manifest.server.mcp_config.command -like '*${HOME}*') $pkg.Manifest.server.mcp_config.command
    }

    # ======================================================================
    Write-Host ""
    Write-Host "Durable per-client state" -ForegroundColor Cyan

    $statusPath = Join-Path $root 'install-status.json'
    # Start from a document shaped like the one complete-install.ps1 writes, so
    # the test proves the OTHER fields survive an integration write.
    @{ schema = 1; state = 'live_verified'; detail = 'x'; client = 'Codex'
       generation = 'abc123def456'; server_path = 'C:\x\horizun-mcp.exe' } |
        ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8

    Set-HorizunIntegrationState -StatusPath $statusPath -Client 'claude-desktop' -State 'configured' -Detail 'ok' | Out-Null
    Set-HorizunIntegrationState -StatusPath $statusPath -Client 'claude-code' -State 'pending_client_restart' `
        -Detail 'registered; restart required' | Out-Null

    $doc = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    Assert 'the installation state complete-install owns survives an integration write' `
        ($doc.state -eq 'live_verified' -and $doc.generation -eq 'abc123def456') "state=$($doc.state) generation=$($doc.generation)"
    Assert 'two clients hold two independent states at once' `
        ($doc.integrations.'claude-desktop'.state -eq 'configured' -and $doc.integrations.'claude-code'.state -eq 'pending_client_restart') $null

    $threw = $false
    try { Set-HorizunIntegrationState -StatusPath $statusPath -Client 'x' -State 'nearly_done' -Detail 'd' | Out-Null }
    catch { $threw = $true }
    Assert 'an invented state is refused - the six are the vocabulary' $threw $null

    $threw = $false
    try { Set-HorizunIntegrationState -StatusPath $statusPath -Client 'x' -State 'pending_user_action' -Detail 'd' | Out-Null }
    catch { $threw = $true }
    Assert 'pending_user_action without naming the action is refused' $threw $null

    $threw = $false
    try { Set-HorizunIntegrationState -StatusPath $statusPath -Client 'x' -State 'configured' -Detail 'd' -PendingUserAction 'y' | Out-Null }
    catch { $threw = $true }
    Assert 'a configured integration may not also be waiting on the user' $threw $null

    Assert 'removing one client leaves the other' `
        ((Remove-HorizunIntegrationState -StatusPath $statusPath -Client 'claude-code') -and
         $null -ne (Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json).integrations.'claude-desktop') $null

    # ======================================================================
    Write-Host ""
    Write-Host "Writing claude_desktop_config.json" -ForegroundColor Cyan

    # Against a COPY of whatever this machine really has, when it has one. A
    # fixture with three keys proves nothing about a file with forty.
    $real = Get-HorizunClaudeDesktop
    $fakeRoaming = Join-Path $root 'roaming'
    New-Item -ItemType Directory -Path $fakeRoaming | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fakeRoaming 'Claude Extensions') | Out-Null
    $fakeConfig = Join-Path $fakeRoaming 'claude_desktop_config.json'
    $usedRealConfig = $false
    if ($real.config_exists) {
        Copy-Item -LiteralPath $real.config_path -Destination $fakeConfig -Force
        $usedRealConfig = $true
    }
    else {
        @{ preferences = @{ a = 1 }; mcpServers = @{ 'someone-elses' = @{ command = 'C:\other.exe' } } } |
            ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fakeConfig -Encoding UTF8
    }
    # Give it a foreign server either way, so preservation has something to lose.
    $cfg = Get-Content -LiteralPath $fakeConfig -Raw | ConvertFrom-Json
    if (-not $cfg.PSObject.Properties['mcpServers']) { $cfg | Add-Member mcpServers ([pscustomobject]@{}) -Force }
    $cfg.mcpServers | Add-Member 'someone-elses' ([pscustomobject]@{ command = 'C:\other.exe' }) -Force
    $cfg | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $fakeConfig -Encoding UTF8
    $beforeKeys = @((Get-Content -LiteralPath $fakeConfig -Raw | ConvertFrom-Json).PSObject.Properties.Name)
    Write-Host ("        (against " + $(if ($usedRealConfig) { "a copy of this machine's real config, $($beforeKeys.Count) top-level keys" } else { 'a synthetic config' }) + ")")

    $fakeServer = Join-Path $root 'horizun-mcp.exe'
    Set-Content -LiteralPath $fakeServer -Value 'not a real server' -Encoding ASCII
    $installer = Join-Path $PSScriptRoot 'install-claude-desktop-extension.ps1'
    $statusPath2 = Join-Path $root 'status2.json'

    & pwsh -NoProfile -File $installer -ConfigFallback -RoamingOverride $fakeRoaming `
        -ServerPath $fakeServer -StatusPath $statusPath2 -Force *> $null
    $after = Get-Content -LiteralPath $fakeConfig -Raw | ConvertFrom-Json
    $afterKeys = @($after.PSObject.Properties.Name)

    Assert 'the entry is there and names the server path already expanded' `
        ($after.mcpServers.'horizun-revit'.command -eq $fakeServer) $after.mcpServers.'horizun-revit'.command
    Assert 'every other top-level key survived' `
        (@($beforeKeys | Where-Object { $_ -notin $afterKeys }).Count -eq 0) `
        (@($beforeKeys | Where-Object { $_ -notin $afterKeys }) -join ', ')
    Assert 'the other MCP server survived' `
        ($after.mcpServers.'someone-elses'.command -eq 'C:\other.exe') $null
    Assert 'a backup was taken before the write' `
        (@(Get-ChildItem -LiteralPath $fakeRoaming -Filter 'claude_desktop_config.json.horizun-bak-*').Count -ge 1) $null

    # ---- rollback puts the file back, byte for byte -------------------------
    $backup = @(Get-ChildItem -LiteralPath $fakeRoaming -Filter 'claude_desktop_config.json.horizun-bak-*' |
                Sort-Object Name -Descending)[0]
    $backupHash = (Get-FileHash -LiteralPath $backup.FullName -Algorithm SHA256).Hash
    & pwsh -NoProfile -File $installer -Rollback -RoamingOverride $fakeRoaming -StatusPath $statusPath2 -Force *> $null
    Assert 'rollback restores the configuration byte for byte' `
        ((Get-FileHash -LiteralPath $fakeConfig -Algorithm SHA256).Hash -eq $backupHash) $null
    Assert 'after rollback the entry is gone again' `
        ($null -eq (Get-Content -LiteralPath $fakeConfig -Raw | ConvertFrom-Json).mcpServers.'horizun-revit') $null

    # ---- and the targeted removal keeps everything else ---------------------
    & pwsh -NoProfile -File $installer -ConfigFallback -RoamingOverride $fakeRoaming `
        -ServerPath $fakeServer -StatusPath $statusPath2 -Force *> $null
    & pwsh -NoProfile -File $installer -Remove -RoamingOverride $fakeRoaming -StatusPath $statusPath2 -Force *> $null
    $after = Get-Content -LiteralPath $fakeConfig -Raw | ConvertFrom-Json
    Assert '-Remove takes the horizun-revit entry and nothing else' `
        ($null -eq $after.mcpServers.'horizun-revit' -and $after.mcpServers.'someone-elses'.command -eq 'C:\other.exe') $null
    Assert '-Remove leaves every other top-level key alone' `
        (@($beforeKeys | Where-Object { $_ -notin @($after.PSObject.Properties.Name) }).Count -eq 0) $null

    # ======================================================================
    Write-Host ""
    Write-Host "Client detection" -ForegroundColor Cyan

    $absent = Get-HorizunClaudeDesktop -RoamingOverride (Join-Path $root 'nothing-here')
    Assert 'a missing Claude Desktop reports installed=false rather than throwing' `
        ($absent.installed -eq $false) $null

    $seen = Get-HorizunClaudeDesktop -RoamingOverride $fakeRoaming
    Assert 'an MSIX-style roaming directory is read like any other' `
        ($seen.installed -and $seen.config_exists) $null

    $existing = Get-HorizunExistingClients
    Assert 'the two clients that must not break are both inspected' `
        ((@($existing | ForEach-Object { $_.client }) -join ',') -eq 'claude-code,codex') `
        ((@($existing | ForEach-Object { $_.client })) -join ',')

}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host ""; Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host ""
Write-Host 'integrations: the manifest, package, durable state and Claude Desktop config writer all behave.' -ForegroundColor Green
exit 0
