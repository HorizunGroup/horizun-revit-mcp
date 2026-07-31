#Requires -Version 5.1
<#
  Install Horizun Revit MCP on THIS machine, from this source tree.

  This is the path an AI agent (Claude Code, Codex) or a person follows after
  cloning the repository: no installer download, no additional executables -
  everything that runs is built here, from the code you can read, against the
  Revit already installed on this machine.

  What it does, in order:

    1  Finds every Revit (2023-2027) installed on this machine, by looking for
       its RevitAPI.dll. Only those years are built: the add-in compiles
       against each Revit's OWN API, so a year that is not installed cannot be
       built here - and does not need to be.
    2  Builds the add-in once per year, and the MCP server once. Everything is
       built and STAGED before anything is installed, so a build failure - the
       commonest failure - changes nothing at all.
    3  Installs the add-in for each year and the server. A failure after this
       point walks the undo ledger backwards and reports exactly what state
       the machine is in.
    4  Reads every installed binary BACK: the commit it was built from and the
       SHA-256 against what was staged. All halves must name one commit -
       that is what "the server and the add-in are paired" actually means.
    5  Prints the MCP client configuration to add, for Claude Code and Codex.

  What it needs:

    * Windows, with at least one Revit 2023-2027 installed.
    * The .NET SDK (8.0 or later; Revit <= 2024 additionally needs the .NET
      Framework 4.8 targeting pack, which Visual Studio and most SDK installs
      include). Checked before anything runs.
    * Revit CLOSED. Revit holds a lock on the add-in DLL it loaded; this
      refuses to run while any Revit is open, and changes nothing.

  Usage:   powershell -ExecutionPolicy Bypass -File .\install.ps1
           ... -Years 2025,2026    only these years (default: every installed one)
           ... -SkipServer         add-in only - you own the pairing then

  Exit codes:  0 installed and verified   1 refused or failed (message says why)
#>
[CmdletBinding()]
param(
    [int[]]$Years,
    [string]$Config = 'Release',
    [switch]$SkipServer
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo 'scripts\horizun-deploy.lib.ps1')

$serverInstall  = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server'
$serverExe      = Join-Path $serverInstall 'horizun-mcp.exe'
$manifestSource = Join-Path $repo 'src\Horizun.Revit\Horizun.addin'
$stagingRoot    = Join-Path ([IO.Path]::GetTempPath()) ('horizun-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

# The undo ledger, same shape as deploy-both.ps1: every reversible step appends
# its undo BEFORE acting, and a failure walks it backwards, newest first.
$undo = New-Object System.Collections.Generic.List[object]
$installedThings = New-Object System.Collections.Generic.List[object]

function Invoke-Rollback([string]$Because) {
    Write-Host ""
    Write-Host "ROLLING BACK: $Because" -ForegroundColor Red
    if ($undo.Count -eq 0) {
        Write-Host "  nothing had been installed yet, so nothing needed undoing." -ForegroundColor DarkYellow
        return
    }
    $failures = @()
    for ($i = $undo.Count - 1; $i -ge 0; $i--) {
        $step = $undo[$i]
        try { & $step.Action; Write-Host ("  restored: " + $step.What) -ForegroundColor DarkYellow }
        catch { $failures += ($step.What + ": " + $_.Exception.Message) }
    }
    if ($failures.Count -gt 0) {
        Write-Host ""
        Write-Host ("THE ROLLBACK ITSELF DID NOT COMPLETE. Repair by hand before starting Revit or an MCP client:") -ForegroundColor Red
        foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    }
    else {
        Write-Host "  every change was reversed; this machine is as it was before this run." -ForegroundColor Yellow
    }
}

try {
    # =========================================================================
    # 0. REFUSE UP FRONT, so nothing is half-done.
    # =========================================================================
    $revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
    if ($revit.Count -gt 0) {
        throw ("Revit is running (pid " + (($revit | ForEach-Object { $_.Id }) -join ', ') +
               "). It holds the add-in DLL open. Close Revit and run this again. Nothing was changed.")
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw ("The .NET SDK is not on PATH, and this install BUILDS everything it installs. " +
               "Install it from https://dotnet.microsoft.com/download (SDK, not just the runtime) " +
               "and run this again. Nothing was changed.")
    }
    # An SDK answers `dotnet --version`; a runtime-only install does not.
    & dotnet --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet is on PATH but reports no SDK. Install the .NET SDK and run this again. Nothing was changed."
    }

    # =========================================================================
    # 1. WHICH REVITS ARE HERE. Detected, not assumed: the add-in is compiled
    #    against each year's own RevitAPI.dll, so its presence IS the test.
    # =========================================================================
    $detected = @()
    foreach ($y in 2023..2027) {
        if (Test-Path (Join-Path "C:\Program Files\Autodesk\Revit $y" 'RevitAPI.dll')) { $detected += $y }
    }
    if ($detected.Count -eq 0) {
        throw ("No Revit 2023-2027 found under C:\Program Files\Autodesk. This add-in compiles against " +
               "the Revit API of the machine it runs on; without a Revit there is nothing to build " +
               "against and nothing to install into. Nothing was changed.")
    }

    if ($Years) {
        $missing = @($Years | Where-Object { $detected -notcontains $_ })
        if ($missing.Count -gt 0) {
            throw ("Revit " + ($missing -join ', ') + " is not installed on this machine (no RevitAPI.dll), " +
                   "so it cannot be built here. Installed years: " + ($detected -join ', ') + ". Nothing was changed.")
        }
    }
    else { $Years = $detected }

    Write-Host "[Horizun] installing from source for Revit $($Years -join ', ')$(if (-not $SkipServer) { ' + MCP server' })" -ForegroundColor Cyan

    # A year already carrying a Horizun.addin OUTSIDE this run's list would be
    # left on another build - and the server refuses a contract hash it does not
    # share. Warn loudly rather than guess; the person may be about to uninstall it.
    $unpaired = @(Get-HorizunInstalledAddins | Where-Object { $Years -notcontains $_.Year })
    if ($unpaired.Count -gt 0) {
        $names = ($unpaired | ForEach-Object { "Revit $($_.Year) [$($_.Scope)]" }) -join ', '
        Write-Host ""
        Write-Host ("WARNING: Horizun is already installed for $names, which this run will NOT touch. If the " +
                    "contract differs between builds, those Revits will be refused by the new server. Re-run " +
                    "with -Years including them, or uninstall them.") -ForegroundColor Yellow
    }

    # =========================================================================
    # 2. BUILD AND STAGE EVERYTHING FIRST. Staged per year on purpose: the
    #    project compiles with -p:RevitYear=<y> and bin\<Config> is SHARED, so
    #    installing straight out of bin would ship the last build to every year.
    # =========================================================================
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $binDir = Join-Path $repo "src\Horizun.Revit\bin\$Config"
    $staged = @{}

    foreach ($y in $Years) {
        Write-Host ""
        Write-Host "--- building the add-in for Revit $y" -ForegroundColor Yellow
        & dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') `
            -c $Config -p:RevitYear=$y -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $y. NOTHING was installed." }

        $dll = Join-Path $binDir 'Horizun.Revit.dll'
        if (-not (Test-Path $dll)) { throw "Revit $y built but produced no Horizun.Revit.dll. NOTHING was installed." }
        Assert-HorizunTfm $dll $y

        $stage = Join-Path $stagingRoot "$y"
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item (Join-Path $binDir '*') $stage -Recurse -Force
        $staged[$y] = $stage

        $prov = Get-HorizunProvenance (Join-Path $stage 'Horizun.Revit.dll')
        Write-Host ("    staged  {0}  {1}" -f (Get-HorizunActualTfm $dll),
                    $(if ($prov) { $prov.Sha.Substring(0, 12) + $(if ($prov.Dirty) { '-dirty' } else { '' }) } else { 'no provenance (no .git here?)' }))
    }

    $serverStage = $null
    if (-not $SkipServer) {
        Write-Host ""
        Write-Host "--- building the MCP server" -ForegroundColor Yellow
        & dotnet build (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c $Config -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "Server build failed. NOTHING was installed." }

        $serverBin = Join-Path $repo "src\Horizun.Server\bin\$Config\net8.0"
        if (-not (Test-Path (Join-Path $serverBin 'horizun-mcp.exe'))) {
            throw "No server build at $serverBin. NOTHING was installed."
        }
        $serverStage = Join-Path $stagingRoot 'server'
        New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
        Get-ChildItem $serverBin -File | ForEach-Object { Copy-Item $_.FullName $serverStage -Force }
        Write-Host "    staged  server"
    }

    # =========================================================================
    # 3. INSTALL. Every step registers its undo BEFORE it acts.
    # =========================================================================
    foreach ($y in $Years) {
        Write-Host ""
        Write-Host "--- installing for Revit $y" -ForegroundColor Yellow

        $addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$y"
        $pluginDir  = Join-Path $addinsRoot 'Horizun'
        $manifest   = Join-Path $addinsRoot 'Horizun.addin'

        $hadPlugin   = Test-Path $pluginDir
        $hadManifest = Test-Path $manifest
        $backup      = Join-Path $stagingRoot ("backup-$y")
        if ($hadPlugin -or $hadManifest) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            if ($hadPlugin)   { Copy-Item $pluginDir (Join-Path $backup 'Horizun') -Recurse -Force }
            if ($hadManifest) { Copy-Item $manifest $backup -Force }
        }

        $undo.Add([pscustomobject]@{
            What = "Revit $y add-in"
            Action = {
                if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force }
                if ($hadPlugin)   { Copy-Item (Join-Path $backup 'Horizun') $pluginDir -Recurse -Force }
                if ($hadManifest) { Copy-Item (Join-Path $backup 'Horizun.addin') $manifest -Force }
                elseif (Test-Path $manifest) { Remove-Item $manifest -Force }
            }.GetNewClosure()
        })

        $result = Install-HorizunPayload -Source $staged[$y] -Year $y -ManifestSource $manifestSource
        $installedThings.Add([pscustomobject]@{
            Kind = "add-in $y"; Dll = $result.Dll; StagedDll = (Join-Path $staged[$y] 'Horizun.Revit.dll')
        })
        Write-Host ("    plugin : {0}" -f $result.Dll)
    }

    if (-not $SkipServer) {
        Write-Host ""
        Write-Host "--- installing the MCP server" -ForegroundColor Yellow

        $firstInstall = -not (Test-Path $serverInstall)
        if ($firstInstall) {
            # FIRST INSTALL - the case the update scripts refuse. The directory is
            # created by us, so its undo is to remove it entirely.
            New-Item -ItemType Directory -Path $serverInstall -Force | Out-Null
            $undo.Add([pscustomobject]@{
                What = 'MCP server (first install)'
                Action = { if (Test-Path $serverInstall) { Remove-Item $serverInstall -Recurse -Force } }.GetNewClosure()
            })
            Get-ChildItem $serverStage -File | ForEach-Object { Copy-Item $_.FullName $serverInstall -Force }
        }
        else {
            # UPDATE. Windows will not overwrite a running executable but WILL
            # rename one: move the old files aside, copy the new ones in. A running
            # horizun-mcp.exe keeps executing the old image from memory and picks
            # up the new files on its next start.
            $serverBackup = Join-Path $serverInstall ('replaced-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            New-Item -ItemType Directory -Path $serverBackup -Force | Out-Null
            $moved = New-Object System.Collections.Generic.List[object]
            $originalNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach ($f in @(Get-ChildItem $serverInstall -File)) { [void]$originalNames.Add($f.Name) }

            $undo.Add([pscustomobject]@{
                What = 'MCP server'
                Action = {
                    foreach ($f in @(Get-ChildItem $serverInstall -File -ErrorAction SilentlyContinue)) {
                        if (-not $originalNames.Contains($f.Name)) { Remove-Item $f.FullName -Force }
                    }
                    foreach ($m in $moved) {
                        if (Test-Path $m.To) {
                            if (Test-Path $m.From) { Remove-Item $m.From -Force }
                            [IO.File]::Move($m.To, $m.From)
                        }
                    }
                    if ((Test-Path $serverBackup) -and -not @(Get-ChildItem $serverBackup -File)) {
                        Remove-Item $serverBackup -Recurse -Force
                    }
                }.GetNewClosure()
            })

            foreach ($f in @(Get-ChildItem $serverInstall -File)) {
                $dest = Join-Path $serverBackup $f.Name
                [IO.File]::Move($f.FullName, $dest)
                $moved.Add([pscustomobject]@{ From = $f.FullName; To = $dest })
            }
            Get-ChildItem $serverStage -File | ForEach-Object { Copy-Item $_.FullName $serverInstall -Force }
        }

        $installedThings.Add([pscustomobject]@{
            Kind = 'server'
            Dll = (Join-Path $serverInstall 'horizun-mcp.dll')
            StagedDll = (Join-Path $serverStage 'horizun-mcp.dll')
        })
        Write-Host ("    server : {0}$(if ($firstInstall) { '  (first install)' })" -f $serverInstall)
    }

    # =========================================================================
    # 4. VERIFY WHAT LANDED. Two builds of one clean commit cannot disagree
    #    about the contract, so "every half names one commit" IS the pairing
    #    check. A tree without git stamps "unknown" - reported, not failed:
    #    everything here was built from ONE tree in one run either way, and the
    #    hash comparison below still proves each copy landed intact.
    # =========================================================================
    Write-Host ""
    Write-Host "--- verifying what is on disk" -ForegroundColor Yellow

    $problems = @(); $shas = @{}
    foreach ($thing in $installedThings) {
        $installedHash = Get-HorizunFileHash $thing.Dll
        $stagedHash    = Get-HorizunFileHash $thing.StagedDll
        if ($installedHash -ne $stagedHash) {
            $problems += "$($thing.Kind): the installed file does not match what was staged."
        }
        $prov = Get-HorizunProvenance $thing.Dll
        $label = if ($prov) { $prov.Sha.Substring(0, 12) + $(if ($prov.Dirty) { '-dirty' } else { '' }) } else { 'unknown' }
        if ($prov) { $shas[$thing.Kind] = $prov.Sha }
        Write-Host ("    {0,-12} {1}  sha256 {2}" -f $thing.Kind, $label, $installedHash.Substring(0, 12))
    }
    $distinct = @($shas.Values | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        $problems += "The installed halves name DIFFERENT commits - the pairing this install exists to guarantee is broken."
    }
    if ($problems.Count -gt 0) {
        throw ("VERIFICATION FAILED after installing:" + [Environment]::NewLine +
               "  - " + ($problems -join ([Environment]::NewLine + "  - ")))
    }
    Write-Host ("    all {0} half(s) built from one tree in one run, and every file matches what was staged." -f $installedThings.Count) -ForegroundColor Green
}
catch {
    Invoke-Rollback $_.Exception.Message
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path $stagingRoot) {
        try { Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# =============================================================================
# 5. WHAT TO DO NEXT - printed, because the next step happens in a different
#    program and nothing here can do it for you.
# =============================================================================
Write-Host ""
Write-Host "[Horizun] installed and verified." -ForegroundColor Green
Write-Host ""
Write-Host "Add the MCP server to your client. The path below is THIS machine's, already" -ForegroundColor Cyan
Write-Host "expanded - do not retype it with %LOCALAPPDATA%, which PowerShell does not expand." -ForegroundColor Cyan
Write-Host ""
Write-Host "  Claude Code:"
Write-Host "    claude mcp add horizun -- `"$serverExe`""
Write-Host ""
# TOML literal strings (single quotes) take Windows paths as they are; the
# double-quoted form would need every backslash doubled, and one missed pair is
# a config that loads with a path pointing nowhere.
#
# The timeouts are not decoration. A model scan or a batch open occupies Revit's
# UI thread for minutes, and a client with a 60-second default gives up on work
# that is still running - the bridge then looks broken while it is busy.
Write-Host "  Codex - add to $($env:USERPROFILE)\.codex\config.toml:"
Write-Host "    [mcp_servers.horizun]"
Write-Host "    command = '$serverExe'"
Write-Host "    args = []"
Write-Host "    startup_timeout_sec = 120"
Write-Host "    tool_timeout_sec = 600"
Write-Host ""
Write-Host "  Cursor, Cline, Windsurf, Claude Desktop and other MCP clients - in their"
Write-Host "  JSON config (mcpServers), using the SAME path:"
Write-Host "    { `"mcpServers`": { `"horizun`": { `"command`": `"$($serverExe -replace '\\', '\\')`" } } }"
Write-Host ""
Write-Host "Then START REVIT and note two things:" -ForegroundColor Cyan
Write-Host ("  * Revit will show a 'Security - Unsigned Add-In' dialog - this build is unsigned. " +
            "Choose 'Always Load'. The dialog RETURNS after every update (the decision is " +
            "remembered per binary), and it may open on a different monitor.")
Write-Host "  * A 'Horizun Hub' ribbon tab appears once a document is open; its 'Estado del puente' button answers 'is this working?' without leaving Revit."
Write-Host ""
Write-Host "Verify the pairing from your MCP client: call horizun_health. Updating later is:"
Write-Host "  git pull, close Revit, run install.ps1 again."
exit 0
