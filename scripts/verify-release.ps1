#Requires -Version 5.1
<#
  ONE COMMIT, FROM SOURCE TO WHAT IS RUNNING.

  The acceptance report recorded, twice, that "a hash cannot prove a binary came
  from a commit" - because two builds of one commit in two directories produced
  different bytes. That was true, and it was the wrong conclusion to stop at. The
  hash never had to prove PROVENANCE; the commit stamped INTO each binary does
  that. What the hash proves is IDENTITY: that the file staged, the file inside
  the installer, and the file on disk after installing are the same file.

  Together they answer the question a release actually asks, which nothing here
  could answer before:

      is everything that is installed built from ONE clean commit,
      and is that commit the one in the source tree right now?

  Four links, each checked rather than assumed:

    1  git      HEAD, and whether the tree is clean
    2  manifest names a full 40-character commit, from a clean tree, equal to (1)
    3  staged   every file's SHA-256 equals the manifest, and every file's own
                stamped commit equals (2) with no -dirty suffix
    4  installed  same again, for the server and every add-in Revit will load

  Link 4 is what proves the INSTALLER carried the right payload. Reading bytes
  out of a setup.exe would be a different and weaker check; comparing what came
  out the other end is the direct one.

  Exit codes:  0 the chain holds   1 it does not   2 could not run
#>
[CmdletBinding()]
param(
    [string]$Stage,
    [string]$Installer,
    # Also check what is installed on THIS machine. Off by default so the script
    # can validate a package before it is installed.
    [switch]$Installed,
    # Stable releases are permanently unsigned; this explicit acknowledgement
    # prevents absence of publisher identity from being mistaken for trust.
    [switch]$AllowUnsigned,
    # The installer can write a per-run result file. CI must pass the exact file
    # it asked Setup to create instead of reading a global leftover in {app}.
    [string]$InstallResult,
    [string]$Json,
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027)
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
# Hashing a payload, reading a provenance stamp, and finding every Horizun.addin
# on the machine are the same operations pack.ps1 and the deploy scripts need.
# One implementation, so a release cannot be verified by different arithmetic
# from the one that built it.
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')
if (-not $Stage) { $Stage = Join-Path $repo 'dist\stage' }

$problems = New-Object System.Collections.Generic.List[string]
$checks   = New-Object System.Collections.Generic.List[object]

function Check($name, $ok, $detail) {
    $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = $detail }) | Out-Null
    if ($ok) { Write-Host ("  OK    {0}" -f $name) -ForegroundColor Green }
    else {
        Write-Host ("  WRONG {0}" -f $name) -ForegroundColor Red
        if ($detail) { Write-Host ("        {0}" -f $detail) -ForegroundColor DarkRed }
        $problems.Add(("{0}: {1}" -f $name, $detail)) | Out-Null
    }
}

function Sha($path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLower() }

# The commit a BINARY says it is. Stamped into AssemblyInformationalVersion at
# build time as "<version>+<sha>", which Windows exposes as ProductVersion. This
# is the provenance a file hash cannot give.
function StampedCommit($path) {
    try {
        $pv = (Get-Item $path).VersionInfo.ProductVersion
        if (-not $pv) { return $null }
        $plus = $pv.IndexOf('+')
        if ($plus -lt 0) { return $null }
        return $pv.Substring($plus + 1)
    } catch { return $null }
}

# --- 1. git ------------------------------------------------------------------
Push-Location $repo
try {
    $head = (& git rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { Write-Error "not a git repository: $repo"; exit 2 }
    $head = $head.Trim()
    $dirtyFiles = @(& git status --porcelain)
}
finally { Pop-Location }

Write-Host ""
Write-Host "Release chain - $repo" -ForegroundColor Cyan
Write-Host ("-" * 72)
Write-Host ("  source commit  {0}" -f $head)

Check 'the working tree has no uncommitted changes' ($dirtyFiles.Count -eq 0) `
      ("$($dirtyFiles.Count) change(s): " + (($dirtyFiles | Select-Object -First 8) -join '; '))

# --- 2. manifest -------------------------------------------------------------
$manifestPath = Join-Path $Stage 'manifest.json'
if (-not (Test-Path $manifestPath)) { Write-Error "no manifest at $manifestPath - run pack.ps1 first"; exit 2 }
$doc = Get-Content $manifestPath -Raw | ConvertFrom-Json

Check 'the manifest is schema 2 (it carries a commit and the server)' ($doc.Schema -eq 2) `
      ("schema is '{0}'" -f $doc.Schema)
Check 'the manifest names a full 40-character commit' ($doc.Commit -and $doc.Commit.Length -eq 40) `
      ("commit is '{0}'" -f $doc.Commit)
Check 'the manifest was built from a clean tree' ([bool]$doc.CleanTree) 'CleanTree is false'
#
# THE MANIFEST COMMIT VERSUS HEAD, and the one case where a difference is not a
# broken chain.
#
# A release is built, and then work continues - on the harness that verifies it,
# on the changelog that describes it. Rebuilding and reinstalling for a change to
# a PowerShell script would be ceremony, and worse, it would make the evidence
# describe a build nobody had exercised.
#
# So the rule is not "HEAD equals the manifest". It is: everything the ARTIFACT
# is built from must be unchanged since it was built. That is checkable, and it
# is checked - the product paths are named, and a change to any of them makes
# this a broken chain again.
$productPaths = @('src', 'installer')
$driftedFiles = @()
if ($doc.Commit -ne $head) {
    Push-Location $repo
    try { $driftedFiles = @(& git diff --name-only $doc.Commit $head -- $productPaths 2>$null) }
    finally { Pop-Location }
}

if ($doc.Commit -eq $head) {
    Check 'the manifest commit is the source commit' $true $null
}
else {
    Check ("no product source changed since the packaged commit (HEAD is " + $head.Substring(0, 12) + ", " +
           "package is " + $doc.Commit.Substring(0, 12) + ")") `
          ($driftedFiles.Count -eq 0) `
          ("these changed under " + ($productPaths -join '/, ') + "/ and are NOT in the installed artifact: " +
           (($driftedFiles | Select-Object -First 8) -join ', '))
}

# --- 3. staged ---------------------------------------------------------------
$expected = $doc.Commit

$serverStaged = Join-Path $Stage $doc.Server.File.Replace('/', '\')
if (Test-Path $serverStaged) {
    Check 'staged server: sha256 matches the manifest' ((Sha $serverStaged) -eq $doc.Server.Sha256) `
          ("file {0} vs manifest {1}" -f (Sha $serverStaged), $doc.Server.Sha256)
    $sc = StampedCommit $serverStaged
    Check 'staged server: the binary names the manifest commit' ($sc -eq $expected) `
          ("the binary says '{0}'" -f $sc)
    Check 'staged server: not built from a dirty tree' ($sc -and -not $sc.EndsWith('-dirty')) `
          ("the binary says '{0}'" -f $sc)
}
else { Check 'staged server: present' $false "missing: $serverStaged" }

$stagedHashes = @{}
foreach ($year in $Years) {
    $entry = $doc.Plugins | Where-Object { $_.Year -eq $year }
    if (-not $entry) { Check "staged add-in $year : in the manifest" $false 'the manifest has no payload for this year'; continue }

    $dll = Join-Path $Stage "plugin\$year\Horizun.Revit.dll"
    if (-not (Test-Path $dll)) { Check "staged add-in $year : present" $false "missing: $dll"; continue }

    $h = Sha $dll
    $stagedHashes[$year] = $h
    Check "staged add-in $year : sha256 matches the manifest" ($h -eq $entry.Sha256) `
          ("file {0} vs manifest {1}" -f $h, $entry.Sha256)
    Check "staged add-in $year : full 64-character hash in the manifest" ($entry.Sha256.Length -eq 64) `
          ("the manifest carries {0} characters - a prefix identifies nothing" -f $entry.Sha256.Length)

    $c = StampedCommit $dll
    Check "staged add-in $year : the binary names the manifest commit" ($c -eq $expected) `
          ("the binary says '{0}'" -f $c)
}

# Two years sharing a hash means one was built against the wrong RevitAPI - the
# exact defect the per-year build exists to remove, and it is invisible in a
# build log because bin/ is shared across years.
$dupes = $stagedHashes.GetEnumerator() | Group-Object Value | Where-Object { $_.Count -gt 1 }
Check 'every staged add-in is a DISTINCT binary' ($dupes.Count -eq 0) `
      (($dupes | ForEach-Object { 'years ' + (($_.Group | ForEach-Object { $_.Key }) -join ' and ') + ' are identical' }) -join '; ')

# --- the Claude Desktop extension ---------------------------------------------
#
# It is a shipped artifact like any other, so it is held to the same three
# questions: is it there, does its manifest satisfy the spec THIS tree validates
# against, and does it name this release's version. A .mcpb whose manifest says
# 1.1.6 installs perfectly and then makes every diagnosis wrong.
. (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')
$mcpbDir = Join-Path $Stage 'server\integrations\claude-desktop'
$mcpbFiles = @(Get-ChildItem -LiteralPath $mcpbDir -Filter '*.mcpb' -ErrorAction SilentlyContinue)
Check 'the Claude Desktop extension is staged' ($mcpbFiles.Count -eq 1) `
      ("expected exactly one .mcpb under server\integrations\claude-desktop; found " + $mcpbFiles.Count)
if ($mcpbFiles.Count -eq 1) {
    $mcpb = $mcpbFiles[0].FullName
    $stageVersion = $null
    try { $stageVersion = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
    catch { $stageVersion = $null }
    try {
        $pkg = Get-HorizunMcpbManifestFromPackage -Path $mcpb
        $mcpbProblems = @(Test-HorizunMcpbManifest $pkg.Manifest -ExpectedVersion ([string]$stageVersion))
        Check 'the extension manifest satisfies the MCPB spec and names this version' ($mcpbProblems.Count -eq 0) `
              ($mcpbProblems -join '; ')
        Check 'the extension declares the installed server, not a bundled copy' `
              ($pkg.Manifest.server.mcp_config.command -like '*horizun-mcp.exe') `
              ("command is " + $pkg.Manifest.server.mcp_config.command)
        # A published artifact that carries the building account's home directory
        # is a privacy defect, not a cosmetic one - and it is invisible unless
        # something reads the compressed bytes.
        $account = [IO.Path]::GetFileName($env:USERPROFILE)
        $mcpbText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($mcpb))
        Check 'the extension carries no local account name' `
              (-not ($mcpbText -match [regex]::Escape($account)) -and -not ($pkg.Text -match [regex]::Escape($account))) `
              'the packaged bytes contain this machine''s account name'
        # The extension is part of the payload the manifest hashes, so it is
        # already covered by the stage/manifest comparison below; this names it
        # explicitly so a missing entry is a sentence rather than a count.
        $rel = 'integrations/claude-desktop/' + $mcpbFiles[0].Name
        $listed = @($doc.Server.Payload | Where-Object { $_.Path -replace '\\', '/' -eq $rel })
        Check 'the extension is listed in the payload manifest with its hash' ($listed.Count -eq 1) `
              ("manifest.json has no payload entry for $rel")
        if ($listed.Count -eq 1) {
            Check 'the extension hash in the manifest is the extension on disk' `
                  ((Sha $mcpb) -eq $listed[0].Sha256) 'the staged .mcpb does not match its manifest hash'
        }
    }
    catch { Check 'the extension manifest can be read' $false $_.Exception.Message }
}

# --- permanent unsigned state of our OWN binaries -----------------------------
#
# Public releases deliberately carry no Authenticode publisher identity. The
# switch is an acknowledgement, not a temporary compatibility exception.
$own = @(Get-HorizunOwnBinaries $Stage)
if (-not $AllowUnsigned) {
    Check 'the caller explicitly acknowledges the unsigned release policy' $false `
          'pass -AllowUnsigned only after accepting that Windows cannot authenticate the publisher'
}
else {
    Check 'the caller explicitly acknowledges the unsigned release policy' $true $null
    Check 'the manifest declares the package unsigned' (-not [bool]$doc.Signed) `
          'manifest.Signed is true, but public Horizun releases must be unsigned'
    $unexpected = @()
    foreach ($p in $own) {
        $info = Get-HorizunSignatureInfo $p
        if ($info.Status -ne 'NotSigned') {
            $unexpected += "$(Split-Path $p -Leaf): $($info.Status)"
        }
    }
    Check ('every staged own binary is unsigned by policy (' + $own.Count + ' checked)') ($unexpected.Count -eq 0) `
          ('unexpected Authenticode states: ' + ($unexpected -join ', '))
}

# --- no mixing: the stage matches its manifest, by the SAME function the -----
# --- installer build uses to gate itself. Belt and braces with the per-file --
# --- checks above, but it is the exact call -InstallerOnly makes, so a green --
# --- verify-release means -InstallerOnly will not refuse. ---------------------
$mixing = @(Test-HorizunStageMatchesManifest $Stage)
Check 'the whole stage matches manifest.json (no signed/unsigned mixing)' ($mixing.Count -eq 0) `
      (($mixing | Select-Object -First 6) -join '; ')

# --- the installer -----------------------------------------------------------
if (-not $Installer) {
    $Installer = (Get-ChildItem (Join-Path $repo 'dist') -Filter '*setup.exe' -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
$installerSha = $null
if ($Installer -and (Test-Path $Installer)) {
    $installerSha = Sha $Installer
    $installerSignature = Get-HorizunSignatureInfo $Installer
    Check 'installer is unsigned by public-release policy' ($installerSignature.Status -eq 'NotSigned') `
          "unexpected Authenticode state $($installerSignature.Status)"
    $newerPayload = @(Get-ChildItem $Stage -Recurse -File |
                      Where-Object { $_.LastWriteTimeUtc -gt (Get-Item $Installer).LastWriteTimeUtc })
    # The installer must be NEWER than everything it wrapped. Otherwise it is a
    # setup.exe built from an earlier stage - the mixed-commit case, and one that
    # no hash inside the manifest would reveal.
    Check 'the installer is newer than every file it packaged' ($newerPayload.Count -eq 0) `
          ("$($newerPayload.Count) staged file(s) changed after the installer was built, e.g. " +
           (($newerPayload | Select-Object -First 3 | ForEach-Object { $_.Name }) -join ', '))
}
else { Check 'an installer was produced' $false 'no *setup.exe in dist/' }

# --- 4. installed ------------------------------------------------------------
$installedReport = @()
if ($Installed) {
    $serverInstalled = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
    if (Test-Path $serverInstalled) {
        $h = Sha $serverInstalled
        Check 'installed server: sha256 matches the manifest' ($h -eq $doc.Server.Sha256) `
              ("installed {0} vs manifest {1}" -f $h, $doc.Server.Sha256)
        $c = StampedCommit $serverInstalled
        Check 'installed server: the binary names the manifest commit' ($c -eq $expected) `
              ("the installed binary says '{0}'" -f $c)
        $installedReport += [pscustomobject]@{ what = 'server'; path = $serverInstalled; sha256 = $h; commit = $c }

        # horizun-mcp.exe is an APPHOST. The code that runs is horizun-mcp.dll, and
        # the manifest named neither it nor Newtonsoft.Json - so the one hash this
        # check had was the hash of the launcher, not of the server.
        if ($doc.Server.Payload) {
            $serverDir = Split-Path -Parent $serverInstalled
            $wrong = @()
            foreach ($p in $doc.Server.Payload) {
                $onDiskPath = Join-Path $serverDir ($p.Path -replace '/', '\')
                if (-not (Test-Path $onDiskPath)) { $wrong += "$($p.Path): missing"; continue }
                $actual = Sha $onDiskPath
                if ($actual -ne $p.Sha256) { $wrong += "$($p.Path): $actual vs $($p.Sha256)" }
            }
            Check ('installed server: all ' + @($doc.Server.Payload).Count + ' payload files match the manifest') `
                  ($wrong.Count -eq 0) ($wrong -join '; ')
        }
    }
    else { Check 'installed server: present' $false "missing: $serverInstalled" }

    foreach ($year in $Years) {
        $entry = $doc.Plugins | Where-Object { $_.Year -eq $year }
        if (-not $entry) { continue }

        $dll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun\Horizun.Revit.dll"
        if (-not (Test-Path $dll)) { Check "installed add-in $year : present" $false "missing: $dll"; continue }

        $h = Sha $dll
        Check "installed add-in $year : sha256 matches the manifest" ($h -eq $entry.Sha256) `
              ("installed {0} vs manifest {1}" -f $h, $entry.Sha256)
        $c = StampedCommit $dll
        Check "installed add-in $year : the binary names the manifest commit" ($c -eq $expected) `
              ("the installed binary says '{0}'" -f $c)

        # Revit loads nothing without the .addin manifest beside it.
        $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun.addin"
        Check "installed add-in $year : Revit's .addin manifest is present" (Test-Path $addin) "missing: $addin"
        if ((Test-Path $addin) -and $doc.AddinManifest) {
            Check "installed add-in $year : the .addin manifest matches the release" `
                  ((Sha $addin) -eq $doc.AddinManifest.Sha256) `
                  ("installed {0} vs manifest {1}" -f (Sha $addin), $doc.AddinManifest.Sha256)
        }

        # EVERY OTHER FILE OF THE PAYLOAD. Horizun.Revit.dll was the only one
        # checked, and it is a minority of what gets loaded: Newtonsoft, IronPython
        # and Microsoft.Scripting run in the same process and were covered by
        # nothing. A release could verify clean with a corrupted IronPython.dll
        # beside a perfect plugin, and that surfaces as a broken execute_python
        # weeks later with nothing tying it back to the install.
        if ($entry.Payload) {
            $pluginDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun"
            $wrong = @()
            foreach ($p in $entry.Payload) {
                $onDiskPath = Join-Path $pluginDir ($p.Path -replace '/', '\')
                if (-not (Test-Path $onDiskPath)) { $wrong += "$($p.Path): missing"; continue }
                $actual = Sha $onDiskPath
                if ($actual -ne $p.Sha256) { $wrong += "$($p.Path): $actual vs $($p.Sha256)" }
            }
            Check ("installed add-in $year : all " + @($entry.Payload).Count + " payload files match the manifest") `
                  ($wrong.Count -eq 0) ($wrong -join '; ')

            # The Python standard library, as one ordered digest over every path AND
            # hash under lib\. Two thousand entries in the manifest would be
            # unreviewable, which is its own kind of unverifiable; one digest plus a
            # count catches both a changed byte and a tree that lost files.
            if ($entry.StdLibDigest) {
                $libDir = Join-Path $pluginDir 'lib'
                $actualLib = Get-HorizunPayloadListing $pluginDir
                Check "installed add-in $year : the Python stdlib is byte-for-byte the release's" `
                      ($actualLib -and $actualLib.StdLibDigest -eq $entry.StdLibDigest -and
                       $actualLib.StdLibFiles -eq $entry.StdLibFiles) `
                      ("installed {0} files/digest {1}, manifest {2} files/digest {3}" -f `
                       $(if ($actualLib) { $actualLib.StdLibFiles } else { 'none' }),
                       $(if ($actualLib -and $actualLib.StdLibDigest) { $actualLib.StdLibDigest.Substring(0, 12) } else { '-' }),
                       $entry.StdLibFiles, $entry.StdLibDigest.Substring(0, 12))
            }
        }

        $installedReport += [pscustomobject]@{ what = "add-in $year"; path = $dll; sha256 = $h; commit = $c }
    }

    # THE RESULT FILE MUST BE FROM THIS INSTALLER, NOT AN EARLIER ONE.
    #
    # Measured on 2026-07-30: a silent install exited 1 having deployed nothing,
    # and the four-hour-old install-result.txt still sitting in {app} said
    # fully_installed=yes for all five years. Anything that read it without
    # asking WHEN would have recorded a failed install as a complete one.
    $resultFile = if ($InstallResult) { $InstallResult } else { Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\install-result.txt' }
    if (-not (Test-Path $resultFile)) { Check 'the installer left a result file' $false "missing: $resultFile" }
    else {
        $rText = Get-Content $resultFile -Raw
        Check 'the install reported every year installed' ($rText -match '(?m)^fully_installed[ \t]*=[ \t]*yes[ \t]*\r?$') `
              'install-result.txt does not say fully_installed=yes'
        $failedLine = [regex]::Match($rText, '(?m)^failed[ \t]*=[ \t]*(.*)$')
        $failedYears = if ($failedLine.Success) { $failedLine.Groups[1].Value.Trim() } else { '<missing failed= line>' }
        Check 'the install reported no failed Revit year' ($failedLine.Success -and $failedYears.Length -eq 0) `
              ("install-result.txt failed= value is '{0}'" -f $failedYears)
        if ($Installer -and (Test-Path $Installer)) {
            $newer = (Get-Item $resultFile).LastWriteTimeUtc -ge (Get-Item $Installer).LastWriteTimeUtc
            Check 'the result file is from THIS installer, not an earlier run' $newer `
                  ("install-result.txt is {0} UTC, the installer is {1} UTC - this is a leftover, and what is on disk was put there by something else" -f `
                   (Get-Item $resultFile).LastWriteTimeUtc, (Get-Item $Installer).LastWriteTimeUtc)
        }
    }

    # Left behind by a rolled-back deployment. Their presence means a previous
    # install did not finish cleaning up, and the folder Revit loads may not be
    # the one that was just verified.
    $leftovers = @()
    foreach ($year in $Years) {
        foreach ($n in 'Horizun.installing', 'Horizun.previous') {
            $p = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\$n"
            if (Test-Path $p) { $leftovers += $p }
        }
    }
    Check 'no half-finished install folders were left behind' ($leftovers.Count -eq 0) ($leftovers -join '; ')

    # EVERY Horizun.addin ON THIS MACHINE, not only the ones the manifest names.
    #
    # The loop above walks the manifest's years and looks them up on disk. That can
    # only find a year that is MISSING; it cannot find a year that is present and
    # should not be, and that is the shape the failure actually takes. Measured on
    # this machine: five years installed, on TWO different commits, because
    # deploy-both defaulted to 2025,2026 and the other three kept an older build.
    # Every check above passed for the years it looked at, and three Revits were
    # sitting there ready to be refused by the server on the contract hash.
    #
    # It also looks in the machine-wide Addins root, which nothing here did. An
    # add-in an installer put in ProgramData is loaded by Revit exactly like one in
    # the user's own folder.
    $onDisk = @(Get-HorizunInstalledAddins)
    $strays = @()
    foreach ($a in $onDisk) {
        $sha = if ($a.Provenance) { $a.Provenance.Sha } else { $null }
        if (-not $sha) {
            $strays += "Revit $($a.Year) [$($a.Scope)] at $($a.Manifest): its DLL carries no provenance stamp"
        }
        elseif ($sha -ne $expected) {
            $strays += ("Revit $($a.Year) [$($a.Scope)] at $($a.Manifest): built from " +
                        $sha.Substring(0, 12) + ", not the manifest commit " + $expected.Substring(0, 12))
        }
        elseif ($a.Provenance.Dirty) {
            $strays += "Revit $($a.Year) [$($a.Scope)]: built from a DIRTY tree, so the sha names a commit it is not"
        }
    }
    Check ('every Horizun.addin on this machine is the manifest commit (' + $onDisk.Count + ' found)') `
          ($strays.Count -eq 0) `
          (($strays -join '; ') + '. A Revit left on an older build pairs with the new server and is REFUSED on the contract hash.')

    $installedReport += $onDisk | ForEach-Object {
        [pscustomobject]@{
            what   = "addin-manifest $($_.Year) [$($_.Scope)]"
            path   = $_.Manifest
            sha256 = (Get-HorizunFileHash $_.Dll)
            commit = $(if ($_.Provenance) { $_.Provenance.Sha } else { $null })
        }
    }
}

# --- report ------------------------------------------------------------------
Write-Host ("-" * 72)
$failed = @($checks | Where-Object { -not $_.ok })
Write-Host ("  {0} checks, {1} wrong" -f $checks.Count, $failed.Count)

$report = [pscustomobject]@{
    schema            = 1
    generated_utc     = (Get-Date).ToUniversalTime().ToString('o')
    source_commit     = $head
    source_clean      = ($dirtyFiles.Count -eq 0)
    manifest_commit   = $doc.Commit
    product_paths     = $productPaths
    product_drift     = $driftedFiles
    manifest_clean    = [bool]$doc.CleanTree
    stage             = $Stage
    installer         = $Installer
    installer_sha256  = $installerSha
    signature_policy  = $(if ($AllowUnsigned) { 'unsigned_by_policy' } else { 'unsigned_unacknowledged' })
    install_result    = $InstallResult
    checked_installed = [bool]$Installed
    installed         = $installedReport
    server            = $doc.Server
    plugins           = $doc.Plugins
    checks            = $checks
    problems          = $problems
    verdict           = $(if ($failed.Count -eq 0) { 'one clean commit end to end' } else { 'BROKEN - see problems' })
}

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $report | ConvertTo-Json -Depth 8 | Out-File -FilePath $Json -Encoding utf8
    Write-Host "  wrote $Json"
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "  The chain is BROKEN. A release cannot be assembled from mixed commits:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }
    exit 1
}

Write-Host ""
Write-Host ("  ONE CLEAN COMMIT END TO END: {0}" -f $head) -ForegroundColor Green
exit 0
