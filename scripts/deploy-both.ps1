#Requires -Version 5.1
<#
  Deploy BOTH halves of Horizun - the Revit add-in for every year, and the MCP
  server - from this working tree, as ONE TRANSACTION.

  WHY THIS EXISTS. The two halves share one contract and each hashes it. The server
  refuses an add-in whose hash differs, which is the right behaviour: it stops a
  caller sending an argument the far end will silently ignore. But it makes
  deploying one half a way to break a working setup, and the unsafe move was a
  single command away.

  WHAT THE FIRST VERSION OF THIS SCRIPT STILL GOT WRONG, and this fixes:

    * IT ONLY KNEW ABOUT THE YEARS YOU TYPED. Default 2025 and 2026. A machine with
      Revit 2024 still carrying last month's Horizun.addin was left with an add-in
      that the new server refuses on the contract hash - a split contract created
      by the command whose entire purpose is to prevent one. It now FINDS every
      Horizun.addin installed on this machine, in both the per-user and the
      machine-wide Addins roots, and refuses to finish while any of them is left
      on a different build.

    * IT DEPLOYED AS IT BUILT. Build 2025, install 2025, build 2026, install 2026,
      build server, install server. A failure anywhere past the first install left
      the machine mixed, and the message said "rerun this script" - which is not a
      rollback, it is a hope. Everything is now BUILT AND STAGED first, so a build
      failure changes nothing at all; the installs happen afterwards, and if any of
      them fails every one is put back.

    * IT NEVER CHECKED WHAT LANDED. It printed a timestamp. Now every installed
      binary is read back: the commit it was built from, whether that tree was
      clean, and the SHA-256 of the file against the staged source. All of them
      must agree on one commit - and one commit means one Contract.cs, which is
      what "the halves are paired" actually means.

  WHAT IT NEEDS CLOSED:
    * Revit - it holds a lock on the add-in DLL it loaded.
    * MCP clients do NOT need closing. A running horizun-mcp.exe keeps executing
      the old server from memory; the swap is done by renaming, which Windows
      permits on a running image. See the warning at the end for when that matters.

  Usage:  powershell -ExecutionPolicy Bypass -File .\scripts\deploy-both.ps1
          ... -Years 2025,2026        (default: every year from 2023 to 2027)
          ... -SkipServer             (add-in only - you own the pairing)
          ... -AllowUnpairedYears     (proceed with a known split contract)
#>
[CmdletBinding()]
param(
    # EVERY SUPPORTED YEAR by default. It used to be 2025,2026, which is how a
    # machine ended up with three paired years and one that was not.
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027),
    [string]$Config = 'Release',
    [switch]$SkipServer,

    # Proceed even though a Horizun.addin is installed for a year this run will not
    # touch. Off by default: that is a split contract, and it is exactly what this
    # script exists to prevent.
    [switch]$AllowUnpairedYears
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')

$serverInstall = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server'
$manifestSource = Join-Path $repo 'src\Horizun.Revit\Horizun.addin'
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('horizun-stage-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

# THE ROLLBACK LEDGER. Every reversible thing this script does appends an undo to
# it, newest last, and a failure walks it backwards. It is a list of closures
# rather than a "restore from backup" step, because the two operations that need
# undoing are not the same shape - one is a directory that was moved aside, the
# other is a directory that did not exist before.
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
        Write-Host ("THE ROLLBACK ITSELF DID NOT COMPLETE. These could not be put back, so this machine is in a " +
                    "MIXED state and must be repaired by hand before Revit or an MCP client is started:") -ForegroundColor Red
        foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    }
    else {
        Write-Host "  every change was reversed; this machine is as it was before this run." -ForegroundColor Yellow
    }
}

try {
    Write-Host "[Horizun] deploying add-in ($($Years -join ', ')) + server" -ForegroundColor Cyan

    # =========================================================================
    # 0. REFUSE UP FRONT, so nothing is half-done.
    # =========================================================================
    $revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
    if ($revit.Count -gt 0) {
        throw ("Revit is running (pid " + (($revit | ForEach-Object { $_.Id }) -join ', ') +
               "). It holds the add-in DLL open. Close Revit and run this again. Nothing was changed.")
    }
    $mcpRunning = @(Get-Process -Name 'horizun-mcp' -ErrorAction SilentlyContinue)

    # =========================================================================
    # 1. WHAT IS ALREADY INSTALLED, everywhere - not just where we are about to
    #    write. A Horizun.addin this run does not touch is an add-in on a
    #    different build, and the server will refuse it on the contract hash.
    # =========================================================================
    $existing = @(Get-HorizunInstalledAddins)
    if ($existing.Count -gt 0) {
        Write-Host ""
        Write-Host "Horizun add-ins already installed on this machine:" -ForegroundColor DarkGray
        foreach ($e in $existing) {
            $sha = if ($e.Provenance) { $e.Provenance.Sha.Substring(0, 12) + $(if ($e.Provenance.Dirty) { '-dirty' } else { '' }) }
                   else { 'no provenance stamp' }
            Write-Host ("  Revit {0} [{1}]  {2}" -f $e.Year, $e.Scope, $sha) -ForegroundColor DarkGray
            Write-Host ("      {0}" -f $e.Manifest) -ForegroundColor DarkGray
        }
    }

    # An installed year outside this run. The per-user root is one we can fix by
    # adding the year; a machine-wide one may need the installer, so it is named
    # separately rather than folded into a single number.
    $unpaired = @($existing | Where-Object { $Years -notcontains $_.Year })
    if ($unpaired.Count -gt 0) {
        $names = ($unpaired | ForEach-Object { "Revit $($_.Year) [$($_.Scope)]" }) -join ', '
        if (-not $AllowUnpairedYears) {
            throw ("SPLIT CONTRACT REFUSED. Horizun is installed for $names, and this run would leave " +
                   "$(if ($unpaired.Count -eq 1) { 'it' } else { 'them' }) on the old build while the server moves " +
                   "to the new one. The server and the add-in hash the same contract and the server refuses an " +
                   "add-in whose hash differs, so $(if ($unpaired.Count -eq 1) { 'that Revit' } else { 'those Revits' }) " +
                   "would start refusing every call - and nothing would point at this deploy as the cause. " +
                   "NOTHING WAS CHANGED. Either include $(($unpaired | ForEach-Object { $_.Year } | Sort-Object -Unique) -join ', ') " +
                   "in -Years, uninstall $(if ($unpaired.Count -eq 1) { 'it' } else { 'them' }), or pass " +
                   "-AllowUnpairedYears if you genuinely intend the split.")
        }
        Write-Host ""
        Write-Host ("WARNING: proceeding with a KNOWN SPLIT CONTRACT (-AllowUnpairedYears). $names will keep the " +
                    "old add-in and will refuse every call once the new server is in place.") -ForegroundColor Yellow
    }

    # =========================================================================
    # 2. BUILD AND STAGE EVERYTHING FIRST. Nothing is installed until every half
    #    has compiled, so a build failure - the commonest failure by far - leaves
    #    the machine untouched instead of half-moved.
    #
    #    Staged per year on purpose: the project compiles with
    #    DefineConstants=REVIT<year> and picks its TargetFramework from it, and
    #    bin\<Config> is SHARED across years. Building them all and installing
    #    afterwards, straight out of bin, would ship the last build to every year.
    # =========================================================================
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $binDir = Join-Path $repo "src\Horizun.Revit\bin\$Config"
    $staged = @{}

    foreach ($y in $Years) {
        Write-Host ""
        Write-Host "--- building Revit $y" -ForegroundColor Yellow
        & dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') `
            -c $Config -p:RevitYear=$y -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $y. NOTHING was deployed." }

        $dll = Join-Path $binDir 'Horizun.Revit.dll'
        if (-not (Test-Path $dll)) { throw "Revit $y built but produced no Horizun.Revit.dll. NOTHING was deployed." }
        # Caught here rather than at install time: a mis-targeted build is a build
        # problem, and finding it before anything moves is the whole point of staging.
        Assert-HorizunTfm $dll $y

        $stage = Join-Path $stagingRoot "$y"
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item (Join-Path $binDir '*') $stage -Recurse -Force
        $staged[$y] = $stage

        $prov = Get-HorizunProvenance (Join-Path $stage 'Horizun.Revit.dll')
        Write-Host ("    staged  {0}  {1}" -f (Get-HorizunActualTfm $dll),
                    $(if ($prov) { $prov.Sha.Substring(0, 12) + $(if ($prov.Dirty) { '-dirty' } else { '' }) } else { 'no stamp' }))
    }

    $serverStage = $null
    if (-not $SkipServer) {
        Write-Host ""
        Write-Host "--- building MCP server" -ForegroundColor Yellow
        & dotnet build (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c $Config -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "Server build failed. NOTHING was deployed." }

        $serverBin = Join-Path $repo "src\Horizun.Server\bin\$Config\net8.0"
        if (-not (Test-Path (Join-Path $serverBin 'horizun-mcp.exe'))) {
            throw "No server build at $serverBin. NOTHING was deployed."
        }
        if (-not (Test-Path $serverInstall)) {
            throw ("Horizun MCP is not installed at $serverInstall, so there is nothing to update in place. " +
                   "Install it once with the installer, then this script can keep it in step. NOTHING was deployed.")
        }

        $serverStage = Join-Path $stagingRoot 'server'
        New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
        Get-ChildItem $serverBin -File | ForEach-Object { Copy-Item $_.FullName $serverStage -Force }
        Write-Host "    staged  server"
    }

    # =========================================================================
    # 3. INSTALL. From here on, every step registers its undo BEFORE it acts.
    # =========================================================================
    foreach ($y in $Years) {
        Write-Host ""
        Write-Host "--- installing Revit $y" -ForegroundColor Yellow

        $addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$y"
        $pluginDir = Join-Path $addinsRoot 'Horizun'
        $manifest = Join-Path $addinsRoot 'Horizun.addin'

        # The backup IS the move: the directory being replaced is kept under a
        # stamped name rather than copied somewhere and hoped about.
        $hadPlugin = Test-Path $pluginDir
        $hadManifest = Test-Path $manifest
        $backup = Join-Path $stagingRoot ("backup-$y")

        if ($hadPlugin -or $hadManifest) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            if ($hadPlugin) { Copy-Item $pluginDir (Join-Path $backup 'Horizun') -Recurse -Force }
            if ($hadManifest) { Copy-Item $manifest $backup -Force }
        }

        $undo.Add([pscustomobject]@{
            What = "Revit $y add-in"
            Action = {
                if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force }
                if ($hadPlugin) { Copy-Item (Join-Path $backup 'Horizun') $pluginDir -Recurse -Force }
                if ($hadManifest) { Copy-Item (Join-Path $backup 'Horizun.addin') $manifest -Force }
                elseif (Test-Path $manifest) { Remove-Item $manifest -Force }
            }.GetNewClosure()
        })

        $result = Install-HorizunPayload -Source $staged[$y] -Year $y -ManifestSource $manifestSource
        $installedThings.Add([pscustomobject]@{
            Kind = "add-in $y"; Dll = $result.Dll; StagedDll = (Join-Path $staged[$y] 'Horizun.Revit.dll')
        })
        Write-Host ("    plugin : {0}" -f $result.Dll)
        if ($result.StdLibPy -gt 0) { Write-Host ("    stdlib : {0} .py files" -f $result.StdLibPy) }
        # Read back rather than assumed: a recipe-backed tool with no recipe on disk
        # installs cleanly and fails the first time somebody calls it.
        if ($result.Recipes -gt 0) { Write-Host ("    recipes: {0} .py files" -f $result.Recipes) }
    }

    if (-not $SkipServer) {
        Write-Host ""
        Write-Host "--- installing MCP server" -ForegroundColor Yellow

        # REPLACING A SERVER THAT IS RUNNING. Windows will not let you overwrite a
        # running executable, but it WILL let you rename one: the loaded image keeps
        # its handle to the file wherever it now lives, so a move out of the way
        # followed by a copy in is a clean swap. Measured on this install with four
        # horizun-mcp.exe live - .exe, .dll and Newtonsoft all renamed without complaint.
        $oldStamp = Get-HorizunProvenance (Join-Path $serverInstall 'horizun-mcp.dll')
        $serverBackup = Join-Path $serverInstall ('replaced-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path $serverBackup -Force | Out-Null

        $moved = New-Object System.Collections.Generic.List[object]

        # WHAT WAS THERE BEFORE, BY NAME. The rollback needs this to undo the half of
        # the swap that moving files cannot describe.
        #
        # THE BUG THIS FIXES: the undo put the moved-aside originals back and stopped.
        # Any file the NEW release adds that the old one did not have - a dependency
        # picked up this version, a renamed assembly - was copied in and then simply
        # left there, because there was no original of that name to move back over it.
        # So a "successful" rollback produced a directory that was neither the old
        # release nor the new one: every old file restored, plus strangers. For .NET
        # that is not cosmetic, and the message printed underneath it says "this machine
        # is as it was before this run", which would have been false.
        $originalNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($f in @(Get-ChildItem $serverInstall -File)) { [void]$originalNames.Add($f.Name) }

        $undo.Add([pscustomobject]@{
            What = 'MCP server'
            Action = {
                # 1. Anything this run BROUGHT IN that was not there before. Removed
                #    first, so restoring an original cannot collide with a stranger.
                foreach ($f in @(Get-ChildItem $serverInstall -File -ErrorAction SilentlyContinue)) {
                    if (-not $originalNames.Contains($f.Name)) { Remove-Item $f.FullName -Force }
                }
                # 2. The originals, back where they were.
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
            try {
                [IO.File]::Move($f.FullName, $dest)
                $moved.Add([pscustomobject]@{ From = $f.FullName; To = $dest })
            }
            catch {
                throw ("Could not move '" + $f.Name + "' out of the way: " + $_.Exception.Message)
            }
        }

        Get-ChildItem $serverStage -File | ForEach-Object { Copy-Item $_.FullName $serverInstall -Force }
        $installedThings.Add([pscustomobject]@{
            Kind = 'server'
            Dll = (Join-Path $serverInstall 'horizun-mcp.dll')
            StagedDll = (Join-Path $serverStage 'horizun-mcp.dll')
        })
        Write-Host ("    server : {0}" -f $serverInstall)
        Write-Host ("    replaced -> {0}" -f $serverBackup)
    }

    # =========================================================================
    # 4. VERIFY WHAT LANDED. The old script printed a timestamp, which proves a
    #    file was written and nothing about what is in it.
    #
    #    THE COMMIT IS THE CONTRACT. Contract.cs is one file in one repository; two
    #    binaries built from the same clean commit cannot disagree about its hash.
    #    So "every installed half reports the same sha, from a clean tree" IS the
    #    pairing check, and it is checkable here, on disk, without starting Revit.
    # =========================================================================
    Write-Host ""
    Write-Host "--- verifying what is on disk" -ForegroundColor Yellow

    $problems = @()
    $shas = @{}
    foreach ($thing in $installedThings) {
        $prov = Get-HorizunProvenance $thing.Dll
        if (-not $prov) {
            $problems += "$($thing.Kind): the installed binary carries no provenance stamp, so which commit it " +
                         "came from cannot be established."
            continue
        }
        if ($prov.Dirty) {
            $problems += "$($thing.Kind): built from a DIRTY tree ($($prov.Sha.Substring(0,12))-dirty). The sha " +
                         "names a commit this binary is not, so nothing downstream can identify what is running."
        }

        # The copy landed intact. Cheap, and the alternative is trusting Copy-Item
        # about the one file whose contents this whole script is about.
        $installedHash = Get-HorizunFileHash $thing.Dll
        $stagedHash = Get-HorizunFileHash $thing.StagedDll
        if ($installedHash -ne $stagedHash) {
            $problems += "$($thing.Kind): the installed file does not match what was staged " +
                         "(installed $installedHash, staged $stagedHash)."
        }

        $shas[$thing.Kind] = $prov.Sha
        Write-Host ("    {0,-12} {1}  sha256 {2}" -f $thing.Kind, $prov.Sha.Substring(0, 12), $installedHash.Substring(0, 12))
    }

    $distinct = @($shas.Values | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        $problems += ("The installed halves were built from DIFFERENT commits (" +
                      (($shas.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value.Substring(0,12))" }) -join ', ') +
                      "). Different commits can mean a different Contract.cs, which is precisely the pairing this " +
                      "script exists to guarantee.")
    }

    # And nothing left behind on an older build, anywhere on the machine.
    foreach ($e in @(Get-HorizunInstalledAddins)) {
        if (-not $e.Provenance) {
            $problems += "Revit $($e.Year) [$($e.Scope)] has a Horizun.addin whose DLL carries no provenance stamp."
            continue
        }
        if ($distinct.Count -eq 1 -and $e.Provenance.Sha -ne $distinct[0]) {
            $problems += ("Revit $($e.Year) [$($e.Scope)] is still on commit $($e.Provenance.Sha.Substring(0,12)) " +
                          "while everything this run installed is on $($distinct[0].Substring(0,12)). That Revit " +
                          "will be refused by the server on the contract hash.")
        }
    }

    if ($problems.Count -gt 0) {
        throw ("VERIFICATION FAILED after installing:" + [Environment]::NewLine +
               "  - " + ($problems -join ([Environment]::NewLine + "  - ")))
    }

    Write-Host ("    all {0} half(s) report one commit, from a clean tree, and every file matches what was staged." `
                -f $installedThings.Count) -ForegroundColor Green
}
catch {
    Invoke-Rollback $_.Exception.Message
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    # The staging directory holds the backups the rollback reads, so it is only
    # removed once the run is over one way or the other.
    if (Test-Path $stagingRoot) {
        try { Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ""
Write-Host "[Horizun] both halves deployed and verified." -ForegroundColor Green

# The durable install record now describes bytes that no longer exist. Downgrade
# it HONESTLY: deployed_pending_health, naming the new commit, old health block
# dropped. Best effort - a deploy must not fail because a status file could not
# be written - but the failure is SAID, not swallowed silently.
try {
    $deployedStamp = Get-HorizunProvenance (Join-Path $serverInstall 'horizun-mcp.dll')
    if ($deployedStamp -and $deployedStamp.Sha) {
        & (Join-Path $PSScriptRoot 'refresh-install-status.ps1') -DeployedCommit $deployedStamp.Sha
    }
}
catch {
    Write-Warning "install-status.json could not be refreshed: $($_.Exception.Message). The durable record may still describe the previous build."
}

if (-not $SkipServer -and $mcpRunning.Count -gt 0) {
    # Did the CONTRACT actually move between the server that keeps running and the
    # add-in just deployed? A deploy that never touched Contract.cs cannot break the
    # pairing - measured 2026-07-30 by deploying twice under live sessions without a
    # restart. A warning that fires when nothing will break teaches people to ignore
    # it, and this warning matters when it is true. Any doubt (no stamp, a -dirty
    # build, git refusing) falls back to warning: the conservative direction.
    $contractMoved = $true
    $newStamp = Get-HorizunProvenance (Join-Path $serverInstall 'horizun-mcp.dll')
    if ($oldStamp -and $newStamp -and -not $oldStamp.Dirty -and -not $newStamp.Dirty) {
        if ($oldStamp.Sha -eq $newStamp.Sha) { $contractMoved = $false }
        else {
            & git -C $repo diff --quiet $oldStamp.Sha $newStamp.Sha -- 'src/Horizun.Contracts/Contract.cs' 2>$null
            if ($LASTEXITCODE -eq 0) { $contractMoved = $false }
        }
    }

    if ($contractMoved) {
        Write-Host ""
        Write-Host ("WARNING: $($mcpRunning.Count) horizun-mcp.exe were already running when this ran " +
                    "(pid " + (($mcpRunning | ForEach-Object { $_.Id }) -join ', ') + ").") -ForegroundColor Yellow
        Write-Host ("They are still executing the OLD server from memory. The new files are on disk and " +
                    "will be used by the NEXT one to start.") -ForegroundColor Yellow
        Write-Host ("The CONTRACT changed between the two builds (or provenance could not prove it did not), " +
                    "so until every MCP client is restarted those sessions will refuse on the contract hash " +
                    "as soon as Revit loads the new add-in. Restart them before using horizun.") -ForegroundColor Yellow
    }
    else {
        Write-Host ""
        Write-Host ("$($mcpRunning.Count) horizun-mcp.exe keep running the old server from memory, and that is FINE " +
                    "for pairing: Contract.cs is identical between " +
                    $oldStamp.Sha.Substring(0, 7) + " and " + $newStamp.Sha.Substring(0, 7) +
                    ", so the hash they exchange has not moved. Live sessions keep working against the new add-in.") -ForegroundColor DarkGray
        Write-Host ("Restart MCP clients only when you want them to pick up new SERVER-side code.") -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Start Revit, then restart your MCP client. Confirm the pairing with horizun_health:"
Write-Host "  horizun_commit should be this tree's HEAD, and built_from_clean_tree true."
Write-Host "  A 'contract hash mismatch' means one half is still the old one."
