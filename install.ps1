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
    * The exact .NET SDK in global.json (currently 10.0.400). The SDK is fixed for
      reproducible release bytes; targets remain net48/net8/net10 by Revit year.
      SDK-style restore obtains the .NET Framework 4.8 references for older Revit.
    * Revit CLOSED. Revit holds a lock on the add-in DLL it loaded; this
      refuses to run while any Revit is open, and changes nothing.

  Usage:   powershell -ExecutionPolicy Bypass -File .\install.ps1
           ... -Years 2025,2026    only these years (default: every installed one)
           ... -SkipServer         add-in only - you own the pairing then

  Exit codes:  0 installed and verified   1 refused or failed (message says why)
#>
[CmdletBinding()]
param(
    [string[]]$Years,
    [string]$Config = 'Release',
    [switch]$SkipServer
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo 'scripts\horizun-deploy.lib.ps1')
. (Join-Path $repo 'scripts\install-arguments.lib.ps1')

$serverInstall  = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server'
$serverExe      = Join-Path $serverInstall 'horizun-mcp.exe'
$manifestSource = Join-Path $repo 'src\Horizun.Revit\Horizun.addin'
$horizunAddInId = 'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30'
$stagingRoot    = Join-Path ([IO.Path]::GetTempPath()) ('horizun-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

# The undo ledger, same shape as deploy-both.ps1: every reversible step appends
# its undo BEFORE acting, and a failure walks it backwards, newest first.
$undo = New-Object System.Collections.Generic.List[object]
$installedThings = New-Object System.Collections.Generic.List[object]
$installMutex = New-Object System.Threading.Mutex($false, 'Local\Horizun.Revit.MCP.Install')
$installMutexHeld = $false

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
    try { $installMutexHeld = $installMutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $installMutexHeld = $true }
    if (-not $installMutexHeld) {
        throw 'Another Horizun installation is already running. Wait for it to finish and run this again. Nothing was changed.'
    }
    # powershell.exe -File does not bind `-Years 2025,2026` to [int[]] the way
    # an interactive PowerShell expression does; it produced 20252026. Parse the
    # documented CLI syntax explicitly and validate the closed supported set.
    $Years = @(ConvertTo-HorizunRevitYears $Years)
    # =========================================================================
    # 0. REFUSE UP FRONT, so nothing is half-done.
    # =========================================================================
    $revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
    if ($revit.Count -gt 0) {
        throw ("Revit is running (pid " + (($revit | ForEach-Object { $_.Id }) -join ', ') +
               "). It holds the add-in DLL open. Close Revit and run this again. Nothing was changed.")
    }

    $requiredSdk = [string]((Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version)
    $isolatedSdkRoot = Join-Path $env:LOCALAPPDATA ("Programs\dotnet-sdk-$requiredSdk")
    $isolatedDotnet = Join-Path $isolatedSdkRoot 'dotnet.exe'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $actualSdk = ''
    if ($dotnet) {
        # Windows PowerShell 5.1 does not evaluate a property expression as the
        # command operand of `&`; capture the path first or it tries to invoke
        # the CommandInfo object itself and reports "The command could not be
        # loaded" before the isolated-SDK fallback can run.
        $dotnetPath = [string]$dotnet.Source
        try {
            $actualSdk = [string](& $dotnetPath --version 2>$null | Select-Object -First 1)
            if ($LASTEXITCODE -ne 0) { $actualSdk = '' }
        }
        catch {
            # A global.json asking for an unavailable exact SDK makes the
            # system dotnet host write a native error. Under PS 5.1 and Stop
            # that is terminating; it is precisely when the isolated fallback
            # below must be tried.
            $actualSdk = ''
        }
    }
    $actualSdk = $actualSdk.Trim()
    if ($actualSdk -ne $requiredSdk -and (Test-Path -LiteralPath $isolatedDotnet -PathType Leaf)) {
        $isolatedVersion = ''
        try {
            $isolatedVersion = [string](& $isolatedDotnet --version 2>$null | Select-Object -First 1)
        }
        catch { $isolatedVersion = '' }
        # The exact version string is the success criterion. Windows PowerShell
        # can retain the previous native process' LASTEXITCODE after a caught
        # host-selection failure even though this isolated invocation succeeded.
        if ($isolatedVersion.Trim() -eq $requiredSdk) {
            $env:DOTNET_ROOT = $isolatedSdkRoot
            $env:PATH = "$isolatedSdkRoot;$env:PATH"
            $dotnet = Get-Command dotnet -ErrorAction Stop
            $actualSdk = $isolatedVersion.Trim()
        }
    }
    if ($actualSdk -ne $requiredSdk) {
        $reported = if ($actualSdk) { $actualSdk } else { 'none' }
        throw ("The build requires .NET SDK $requiredSdk from global.json, but dotnet selected $reported. " +
               "Install that exact SDK from https://dotnet.microsoft.com/download or place its official ZIP under " +
               "$isolatedSdkRoot. Nothing was changed.")
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

    $addinIdentityConflicts = @($Years | ForEach-Object {
        $year = [int]$_
        $userRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
        $machineRoot = Join-Path $env:PROGRAMDATA 'Autodesk\Revit\Addins'
        $expectedUserManifest = [IO.Path]::GetFullPath((Join-Path $userRoot "$year\Horizun.addin"))
        Assert-HorizunNoReparseTree (Join-Path $userRoot ([string]$year)) "Revit $year per-user deployment"
        foreach ($candidate in @(Get-HorizunManifestsByAddInId -AddinsRoot $userRoot -Year $year -AddInId $horizunAddInId)) {
            if ([IO.Path]::GetFullPath($candidate) -ine $expectedUserManifest) { $candidate }
        }
        foreach ($candidate in @(Get-HorizunManifestsByAddInId -AddinsRoot $machineRoot -Year $year -AddInId $horizunAddInId)) {
            $candidate
        }
    })
    if ($addinIdentityConflicts.Count -gt 0) {
        throw ("Horizun is already registered under another manifest path or scope for a selected Revit year. " +
               "A second manifest with the same AddInId would make Revit load competing copies. Remove or migrate " +
               "the conflicting installation first. Conflicts: " + ($addinIdentityConflicts -join '; ') +
               '. Nothing was changed.')
    }
    if (Test-Path -LiteralPath $serverInstall -PathType Container) {
        Assert-HorizunNoReparseTree $serverInstall 'MCP server update'
    }

    # Revit 2027 targets net10.0, while 2023-2026 need an SDK capable of the
    # net48/net8 targets. Check the selected years before staging anything so a
    # missing SDK is a precise refusal, not a compiler failure halfway through.
    $sdkMajors = @(& dotnet --list-sdks | ForEach-Object {
        if ($_ -match '^\s*(\d+)\.') { [int]$Matches[1] }
    })
    $requiredSdkMajor = if ($Years -contains 2027) { 10 } else { 8 }
    if (-not ($sdkMajors | Where-Object { $_ -ge $requiredSdkMajor })) {
        throw ("The selected Revit years require .NET SDK $requiredSdkMajor.0 or later, but installed SDK " +
               "majors are: " + $(if ($sdkMajors.Count -gt 0) { ($sdkMajors | Sort-Object -Unique) -join ', ' } else { 'none' }) +
               ". Install the SDK from https://dotnet.microsoft.com/download and run this again. Nothing was changed.")
    }

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
        & dotnet restore (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') -p:RevitYear=$y --locked-mode --nologo
        if ($LASTEXITCODE -ne 0) { throw "Locked restore failed before building Revit $y. NOTHING was installed." }
        & dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') `
            -c $Config -p:RevitYear=$y --no-restore -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $y. NOTHING was installed." }

        $dll = Join-Path $binDir 'Horizun.Revit.dll'
        if (-not (Test-Path $dll)) { throw "Revit $y built but produced no Horizun.Revit.dll. NOTHING was installed." }
        Assert-HorizunTfm $dll $y

        $stage = Join-Path $stagingRoot "$y"
        Copy-HorizunPluginPayloadToStage -Source $binDir -Destination $stage
        $staged[$y] = $stage

        $prov = Get-HorizunProvenance (Join-Path $stage 'Horizun.Revit.dll')
        Write-Host ("    staged  {0}  {1}" -f (Get-HorizunActualTfm $dll),
                    $(if ($prov) { $prov.Sha.Substring(0, 12) + $(if ($prov.Dirty) { '-dirty' } else { '' }) } else { 'no provenance (no .git here?)' }))
    }

    $serverStage = $null
    if (-not $SkipServer) {
        Write-Host ""
        Write-Host "--- publishing the MCP server (win-x64, self-contained)" -ForegroundColor Yellow
        $serverStage = Join-Path $stagingRoot 'server'
        New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
        & dotnet publish (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c $Config `
            -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false `
            -p:RestoreLockedMode=true -o $serverStage --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Self-contained server publish failed. NOTHING was installed." }
        foreach ($requiredRuntimeFile in 'horizun-mcp.exe','horizun-mcp.dll','hostfxr.dll','hostpolicy.dll') {
            if (-not (Test-Path (Join-Path $serverStage $requiredRuntimeFile) -PathType Leaf)) {
                throw "Self-contained server publish is missing $requiredRuntimeFile. NOTHING was installed."
            }
        }
        $clientTools = Join-Path $serverStage 'client-tools'
        New-Item -ItemType Directory -Path $clientTools -Force | Out-Null
        foreach ($helper in 'register-client.ps1','verify-clients.ps1','verify-install.ps1','complete-install.ps1','stop-installed-server.ps1','hz-call.ps1','uninstall-cleanup.ps1','toml-section.lib.ps1') {
            Copy-Item (Join-Path $PSScriptRoot "scripts\$helper") $clientTools -Force
        }
        Write-Host "    staged  self-contained server"
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

        $serverBackup = $null
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
            # Keep the rollback image beside, not inside, the installed payload.
            # This lets exact payload verification reject every unmanifested file
            # without mistaking our own transaction ledger for product content.
            $serverBackup = Join-Path (Split-Path -Parent $serverInstall) `
                ('.install-rollback-' + [guid]::NewGuid().ToString('N'))
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

        # Client helpers are a directory, while the server's transactional
        # replacement above intentionally handles only top-level runtime files.
        # Give the directory its own undo record so a failed source update cannot
        # leave new registration logic beside an old server (or vice versa).
        $installedClientTools = Join-Path $serverInstall 'client-tools'
        $clientToolsBackup = Join-Path $stagingRoot 'backup-client-tools'
        $hadClientTools = Test-Path -LiteralPath $installedClientTools
        if ($hadClientTools) { Copy-Item $installedClientTools $clientToolsBackup -Recurse -Force }
        $undo.Add([pscustomobject]@{
            What = 'MCP client completion helpers'
            Action = {
                if (Test-Path -LiteralPath $installedClientTools) { Remove-Item $installedClientTools -Recurse -Force }
                if ($hadClientTools) { Copy-Item $clientToolsBackup $installedClientTools -Recurse -Force }
            }.GetNewClosure()
        })
        if (Test-Path -LiteralPath $installedClientTools) { Remove-Item $installedClientTools -Recurse -Force }
        Copy-Item (Join-Path $serverStage 'client-tools') $installedClientTools -Recurse -Force

        # Release Setup installs dist/stage/manifest.json. A source build needs
        # the same on-disk identity contract, generated from the exact staged
        # bytes in this run, so verify-install can prove server + selected Revit
        # payloads without trusting the installer's exit code.
        $installedManifest = Join-Path (Split-Path -Parent $serverInstall) 'manifest.json'
        $manifestBackup = Join-Path $stagingRoot 'backup-installed-manifest.json'
        $hadInstalledManifest = Test-Path -LiteralPath $installedManifest
        if ($hadInstalledManifest) { Copy-Item $installedManifest $manifestBackup -Force }
        $undo.Add([pscustomobject]@{
            What = 'installed payload manifest'
            Action = {
                if ($hadInstalledManifest) { Copy-Item $manifestBackup $installedManifest -Force }
                elseif (Test-Path -LiteralPath $installedManifest) { Remove-Item $installedManifest -Force }
            }.GetNewClosure()
        })
        $pluginManifestRows = foreach ($y in $Years) {
            $pluginDll = Join-Path $staged[$y] 'Horizun.Revit.dll'
            $listing = Get-HorizunPayloadListing $staged[$y]
            [pscustomobject]@{
                Year = [int]$y
                Sha256 = Get-HorizunFileHash $pluginDll
                Files = $listing.FileCount
                StdLibFiles = $listing.StdLibFiles
                StdLibDigest = $listing.StdLibDigest
                Payload = $listing.Files
            }
        }
        $sourceProvenance = Get-HorizunProvenance (Join-Path $serverStage 'horizun-mcp.dll')
        $sourceManifest = [pscustomobject]@{
            Schema = 2
            Commit = $(if ($sourceProvenance) { $sourceProvenance.Sha } else { 'unknown' })
            CleanTree = [bool]($sourceProvenance -and -not $sourceProvenance.Dirty)
            BuiltUtc = (Get-Date).ToUniversalTime().ToString('o')
            Config = $Config
            SourceInstall = $true
            Server = [pscustomobject]@{
                File = 'server/horizun-mcp.exe'
                Sha256 = Get-HorizunFileHash (Join-Path $serverStage 'horizun-mcp.exe')
                Payload = (Get-HorizunPayloadListing $serverStage).Files
            }
            Plugins = @($pluginManifestRows)
            AddinManifest = [pscustomobject]@{
                File = 'Horizun.addin'
                Sha256 = Get-HorizunFileHash $manifestSource
            }
        }
        $manifestTemp = "$installedManifest.tmp-$([guid]::NewGuid().ToString('N'))"
        $sourceManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestTemp -Encoding UTF8
        Move-Item -LiteralPath $manifestTemp -Destination $installedManifest -Force
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

    if (-not $SkipServer) {
        # These directories were created by older source installers after their
        # transaction had already succeeded. They are not a valid part of the
        # runtime payload and can otherwise hide stale executable dependencies.
        foreach ($old in @(Get-ChildItem -LiteralPath $serverInstall -Directory -Filter 'replaced-*' -ErrorAction SilentlyContinue)) {
            try { Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction Stop }
            catch {
                # A client can restart the just-replaced server between the update
                # stop and this cleanup. Its old DLL then remains locked under a
                # legacy replaced-* directory. That stale copy is not part of the
                # installed payload and must not invalidate or roll back a verified
                # update; the generation-aware finisher retries after the client has
                # actually exited and held a quiet window.
                $legacyQuarantine = Join-Path (Split-Path -Parent $serverInstall) `
                    ('.legacy-backup-' + [guid]::NewGuid().ToString('N'))
                try {
                    # Windows permits an in-use image directory to be renamed even
                    # when it will not permit deleting the loaded DLL. Moving it out
                    # of server\ is essential: exact payload verification must never
                    # waive executable extras merely because an old client is alive.
                    Move-Item -LiteralPath $old.FullName -Destination $legacyQuarantine -ErrorAction Stop
                    Write-Warning ("Legacy server backup was still in use and was quarantined outside the " +
                                   "loadable payload until client exit: " + $legacyQuarantine)
                }
                catch {
                    # Leave it visible. The exact verifier below will fail closed
                    # rather than bless a payload that still contains extra code.
                    Write-Warning ("Legacy server backup could not be removed or quarantined: " +
                                   $old.FullName + " (" + $_.Exception.Message + ")")
                }
            }
        }

        $installedVerifier = Join-Path $serverInstall 'client-tools\verify-install.ps1'
        $installedVerificationOutput = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $installedVerifier `
            -Client None -ServerPath $serverExe -SkipLive 2>&1)
        $installedVerificationExit = $LASTEXITCODE
        if ($installedVerificationExit -ne 0) {
            foreach ($line in $installedVerificationOutput) { Write-Host $line -ForegroundColor Red }
            throw 'Exact installed-payload verification failed (server, add-in files or .addin manifest).'
        }
        foreach ($line in $installedVerificationOutput) { Write-Host $line }

        # Only the exact rollback image owned by this transaction is disposable.
        # A failed verification above still has it available to restore from.
        if ($serverBackup -and (Test-Path -LiteralPath $serverBackup)) {
            try { Remove-Item -LiteralPath $serverBackup -Recurse -Force -ErrorAction Stop }
            catch {
                # The old server image can remain mapped by a client which raced
                # the update. The new bytes are already exactly verified, so this
                # is deferred cleanup, not an installation failure. The
                # generation-aware finisher retries after the client quiet window.
                $verifiedQuarantine = Join-Path (Split-Path -Parent $serverInstall) `
                    ('.legacy-backup-' + [guid]::NewGuid().ToString('N'))
                try {
                    Move-Item -LiteralPath $serverBackup -Destination $verifiedQuarantine -ErrorAction Stop
                    Write-Warning ("Verified update is installed, but its obsolete rollback image is still in use " +
                                   "and was quarantined for cleanup after client exit: $verifiedQuarantine")
                    $serverBackup = $verifiedQuarantine
                }
                catch {
                    # Never let the finisher delete an .install-rollback-* name:
                    # until this rename succeeds it is indistinguishable from an
                    # active transaction owned by another installer process.
                    Write-Warning ("Verified update is installed, but its obsolete rollback image could not be " +
                                   "quarantined and requires manual cleanup after client exit: $serverBackup")
                }
            }
        }
    }
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
    if ($installMutexHeld) { try { $installMutex.ReleaseMutex() } catch { } }
    $installMutex.Dispose()
}

# =============================================================================
# 5. WHAT TO DO NEXT - printed, because the next step happens in a different
#    program and nothing here can do it for you.
# =============================================================================
Write-Host ""
Write-Host "[Horizun] installed and verified." -ForegroundColor Green

# =============================================================================
# 4b. RE-SIGN, when this machine already trusts its own certificate.
#
# Measured 2026-08-04: Revit's "Always Load" is keyed to the BINARY, so every
# install re-arms the unsigned-add-in dialog for every year - unless the fresh
# binaries are signed with a certificate this user already trusts. self-sign.ps1
# creates and trusts that certificate ONCE, as the operator's own deliberate
# decision; what kept going wrong afterwards was the human step of re-running it
# after every install. So the install does it, and only in the case that adds no
# new trust: certificate already present, already trusted. If there is none,
# nothing is created - a script must not mint a trusted publisher as a side
# effect of an install.
# =============================================================================
$signScript = Join-Path $PSScriptRoot 'scripts\self-sign.ps1'
$trustedCert = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object {
    if ($_.Subject -ne 'CN=Horizun Group (self-signed add-in signing)' -or -not $_.HasPrivateKey -or $_.NotAfter -le (Get-Date).AddDays(30)) { return $false }
    $thumb = $_.Thumbprint
    $inPublisher = Test-Path -LiteralPath "Cert:\CurrentUser\TrustedPublisher\$thumb"
    $inRoot = Test-Path -LiteralPath "Cert:\CurrentUser\Root\$thumb"
    return $inPublisher -and $inRoot
} | Sort-Object NotAfter -Descending | Select-Object -First 1)
$haveCert = $trustedCert.Count -eq 1
if ((Test-Path -LiteralPath $signScript) -and $haveCert) {
    Write-Host ""
    Write-Host "--- re-signing the fresh binaries (existing certificate; no new trust)" -ForegroundColor Yellow
    & powershell -NoProfile -ExecutionPolicy Bypass -File $signScript -Thumbprint $trustedCert[0].Thumbprint
    if ($LASTEXITCODE -eq 3) {
        Write-Host "[Horizun] some files were IN USE and stayed unsigned - close that Revit or MCP client and run scripts\self-sign.ps1 again." -ForegroundColor Yellow
    } elseif ($LASTEXITCODE -ne 0) {
        Write-Host "[Horizun] re-signing FAILED (the install itself is fine). Run scripts\self-sign.ps1 by hand to see why." -ForegroundColor Yellow
    }
} elseif (-not $haveCert) {
    Write-Host ""
    Write-Host "[Horizun] binaries are UNSIGNED: Revit will show the 'Unsigned Add-In' dialog again." -ForegroundColor Yellow
    Write-Host "          To end that permanently on THIS machine's account:  powershell -ExecutionPolicy Bypass -File .\scripts\self-sign.ps1" -ForegroundColor Yellow
    Write-Host "          (creates a self-signed cert and trusts it for this user - a deliberate decision, so it is not done for you)" -ForegroundColor DarkYellow
    Write-Host "          A same-subject certificate that is not already trusted in BOTH Root and TrustedPublisher is deliberately ignored." -ForegroundColor DarkYellow
}
Write-Host ""
Write-Host "Manual recovery commands (automatic completion normally handles this)." -ForegroundColor Cyan
Write-Host "The path below is THIS machine's, already" -ForegroundColor Cyan
Write-Host "expanded - do not retype it with %LOCALAPPDATA%, which PowerShell does not expand." -ForegroundColor Cyan
Write-Host ""
    Write-Host "  Claude Code:"
    Write-Host "    claude mcp add --scope user horizun-revit -- `"$serverExe`""
Write-Host ""
# TOML literal strings (single quotes) take Windows paths as they are; the
# double-quoted form would need every backslash doubled, and one missed pair is
# a config that loads with a path pointing nowhere.
#
# The timeouts are not decoration. A model scan or a batch open occupies Revit's
# UI thread for minutes, and a client with a 60-second default gives up on work
# that is still running - the bridge then looks broken while it is busy.
    Write-Host "  Codex CLI:"
    Write-Host "    codex mcp add horizun-revit -- `"$serverExe`""
    Write-Host "  Then keep these timeouts under [mcp_servers.horizun-revit] in $($env:USERPROFILE)\.codex\config.toml:"
Write-Host "    [mcp_servers.horizun-revit]"
Write-Host "    command = '$serverExe'"
Write-Host "    args = []"
Write-Host "    startup_timeout_sec = 120"
Write-Host "    tool_timeout_sec = 600"
    Write-Host ""
    Write-Host "For manual recovery, close Claude/Codex before registering, then reopen it." -ForegroundColor Yellow
Write-Host "  Cursor, Cline, Windsurf and other stdio MCP clients - in their"
Write-Host "  JSON config (mcpServers), using the SAME path:"
Write-Host "    { `"mcpServers`": { `"horizun-revit`": { `"command`": `"$($serverExe -replace '\\', '\\')`" } } }"
Write-Host ""
Write-Host "Then START REVIT and note two things:" -ForegroundColor Cyan
if ($haveCert) {
    Write-Host ("  * The binaries were re-signed with this machine's trusted certificate, so the " +
                "'Security - Unsigned Add-In' dialog should NOT appear. If it does, a file was in " +
                "use during signing - run scripts\self-sign.ps1 again with Revit closed.")
} else {
    Write-Host ("  * Revit will show a 'Security - Unsigned Add-In' dialog - this build is unsigned. " +
                "Choose 'Always Load'. Revit normally remembers the choice for this add-in identity; " +
                "a trust or policy reset may bring it back, and it may open on a different monitor.")
}
Write-Host "  * A 'Horizun Hub' ribbon tab appears once a document is open; its 'Estado del puente' button answers 'is this working?' without leaving Revit."
Write-Host ""
Write-Host "Verify the pairing from your MCP client: call horizun_health. Updating later is:"
Write-Host "  git pull, close Revit, run install.ps1 again."

# Installation and client registration cannot safely be one synchronous write
# while the invoking Claude/Codex process is alive. The installed finisher makes
# it one USER action instead: it registers immediately when possible, otherwise
# waits for the active client to exit, verifies the config, then completes
# horizun_health after Revit's first start.
$completeInstall = Join-Path $serverInstall 'client-tools\complete-install.ps1'
if (Test-Path -LiteralPath $completeInstall -PathType Leaf) {
    Write-Host ""
    Write-Host "[Horizun] completing client registration automatically." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $completeInstall -Client Both
    if ($LASTEXITCODE -eq 1) {
        Write-Host "[Horizun] automatic completion failed; binaries remain installed and verified." -ForegroundColor Red
        Write-Host "          Review $env:LOCALAPPDATA\Horizun\install-status.json" -ForegroundColor Red
        exit 1
    }
}

# Claude Desktop owns a different packaging format. Prepare its real .mcpb from
# the installed server as part of the same user-facing installation. The app
# still requires one documented Install Extension click; the helper records that
# exact pending action instead of claiming it happened.
$desktopExtension = Join-Path $serverInstall 'client-tools\install-claude-desktop-extension.ps1'
if (Test-Path -LiteralPath $desktopExtension -PathType Leaf) {
    Write-Host ""
    Write-Host "[Horizun] preparing Claude Desktop, when it is installed." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $desktopExtension
    $desktopExit = $LASTEXITCODE
    if ($desktopExit -eq 1) {
        Write-Host "[Horizun] Claude Desktop preparation failed; the Revit bridge remains installed and verified." -ForegroundColor Yellow
        Write-Host "          Use the Start-menu repair shortcut after reviewing install-status.json." -ForegroundColor Yellow
    }
}
exit 0
