#Requires -Version 5.1
<#
  Build everything a machine needs, stage it, and produce the installer.

  "Everything" means ONE ARTIFACT PER SUPPORTED REVIT YEAR - 2023 through 2027 -
  each compiled against that year's own RevitAPI.dll.

  It used to be one per RUNTIME, on the reasoning that years sharing a target
  framework can share a binary. They share a framework; they do not share an API.
  RevitAPI.dll differs between every one of these years, so a plugin compiled
  against 2026 and loaded by 2025 binds to methods that version does not have -
  and it fails at the first CALL, not at load, which reads like a broken command
  rather than a broken install.

  The output path does not carry the year, so the builds would overwrite each
  other in bin: each is copied out to its own staging folder the moment it is
  built, and its target framework is re-read from the staged bytes before the next
  build starts. A year whose Revit is not installed on this machine is reported and
  LEFT OUT, never substituted.

  Usage:
    pwsh scripts/pack.ps1                 # build + stage + compile the installer
    pwsh scripts/pack.ps1 -SkipInstaller  # build + stage only
    pwsh scripts/pack.ps1 -InstallerOnly  # compile the installer from what is ALREADY staged

  To ship SIGNED, the order matters, because a normal run wipes the staging folder
  and would throw the signatures away:

    pack.ps1 -SkipInstaller     ->  sign.ps1     ->  pack.ps1 -InstallerOnly
    (build and stage)              (sign the        (wrap the signed payload)
                                    staged files)

  Then sign the produced setup.exe too: signing the wrapper does not sign what is
  inside it, and signing what is inside does not sign the wrapper.
#>
[CmdletBinding()]
param(
    [string]$Config = 'Release',
    [switch]$SkipInstaller,
    [switch]$InstallerOnly,
    # Package from a tree with uncommitted changes. OFF by default - see below.
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$dist  = Join-Path $repo 'dist'
$stage = Join-Path $dist 'stage'
# Hashing a payload, and reading a provenance stamp out of a DLL, are the same
# operations the deploy scripts and verify-release need. One implementation.
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')

function Step($m) { Write-Host "[pack] $m" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
# THE PACKAGE MUST NAME ONE COMMIT, AND THE TREE MUST BE THAT COMMIT.
#
# A build from a dirty tree produces binaries stamped `<sha>-dirty`, and a sha
# with -dirty on it names a commit the binary IS NOT. Everything downstream then
# rests on a number that cannot be resolved: the manifest says one thing, git
# says another, and "is the fix in this build?" has no answer that survives
# checking. It used to be discovered afterwards, by reading horizun_health on an
# installed binary. It is refused here instead, before anything is built.
# ---------------------------------------------------------------------------
Push-Location $repo
try {
    $commit = (& git rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $commit) { $commit = 'unknown'; $dirty = $null }
    else {
        $commit = $commit.Trim()
        $status = @(& git status --porcelain)
        $dirty = ($status.Count -gt 0)
    }
}
finally { Pop-Location }

if ($dirty) {
    $msg = "The working tree has $($status.Count) uncommitted change(s), so this package would carry binaries " +
           "stamped '$($commit.Substring(0,12))-dirty' - a sha that names a commit these binaries are not. " +
           "Commit or stash first. -AllowDirty packages anyway and records dirty=true in the manifest, which is " +
           "for a local experiment and never for a release."
    if (-not $AllowDirty) {
        Write-Host ""
        $status | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
        throw $msg
    }
    Write-Host "[pack] WARNING: $msg" -ForegroundColor Yellow
}
Step ("commit {0}{1}" -f $commit, $(if ($dirty) { ' (DIRTY)' } else { ' (clean)' }))

if ($InstallerOnly) {
    if (-not (Test-Path (Join-Path $stage 'plugin'))) { throw "Nothing staged yet - run pack.ps1 -SkipInstaller first." }
    # VALIDATE BEFORE WRAPPING. The manifest must describe the stage as it is NOW.
    # If sign.ps1 (or anything) changed a staged byte after the manifest was written
    # and the manifest was not recomputed, wrapping it would ship an installer whose
    # payload does not match its own manifest. sign.ps1 recomputes the manifest after
    # signing; this refuses to build if for any reason it did not.
    $mismatch = @(Test-HorizunStageMatchesManifest $stage)
    if ($mismatch.Count -gt 0) {
        throw ("Refusing to build the installer: the staged payload no longer matches manifest.json. " +
               "A file was signed or modified after the manifest was written. Re-run sign.ps1 (which " +
               "recomputes the manifest from the signed stage), or re-stage. Mismatches: " +
               (($mismatch | Select-Object -First 8) -join '; '))
    }
    Step 'validated the staged payload against manifest.json (every hash re-checked)'
    Step 'using the existing staged payload (nothing rebuilt, nothing wiped)'
}
else {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
}

if (-not $InstallerOnly) {

# --- The MCP server: self-contained Windows x64. -----------------------------
# A release install promises "no SDK" and may land on a Revit 2023/2024 machine
# with no modern .NET runtime at all. Framework-dependent output installed fine
# and then failed at first launch. Publish the runtime beside it so Setup's own
# success includes everything the server needs.
Step 'publishing the MCP server (win-x64, self-contained)'
$serverBin = Join-Path $stage 'server'
& dotnet publish (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c $Config `
    -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -p:RestoreLockedMode=true -o $serverBin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'server self-contained publish failed' }
foreach ($requiredRuntimeFile in 'horizun-mcp.exe','horizun-mcp.dll','hostfxr.dll','hostpolicy.dll') {
    if (-not (Test-Path (Join-Path $serverBin $requiredRuntimeFile))) {
        throw "self-contained server publish is missing $requiredRuntimeFile"
    }
}
$serverProject = [xml](Get-Content (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj'))
# SelectNodes, not the PowerShell XML adapter. `$xml.Project.PropertyGroup.X`
# returns a STRING for a bare element but the XmlElement itself once the element
# carries an attribute - and RuntimeFrameworkVersion carries Condition. Casting
# that to [string] yields the literal text "System.Xml.XmlElement", so the pin
# check compared the published runtime against a type name and threw on every
# correct build. It is the check that was broken, not the publish.
$expectedServerRuntime = [string]($serverProject.SelectNodes('//RuntimeFrameworkVersion') |
    ForEach-Object { $_.InnerText.Trim() } |
    Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($expectedServerRuntime)) {
    throw 'Horizun.Server.csproj must pin RuntimeFrameworkVersion for the redistributed self-contained runtime'
}
$serverDepsPath = Join-Path $serverBin 'horizun-mcp.deps.json'
if (-not (Test-Path $serverDepsPath)) { throw 'self-contained server publish is missing horizun-mcp.deps.json' }
$serverDeps = Get-Content $serverDepsPath -Raw | ConvertFrom-Json
$runtimePrefix = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/'
$runtimeEntries = @($serverDeps.libraries.PSObject.Properties |
    Where-Object { $_.Name.StartsWith($runtimePrefix, [StringComparison]::Ordinal) })
if ($runtimeEntries.Count -ne 1) {
    throw "published deps must identify exactly one $runtimePrefix component; found $($runtimeEntries.Count)"
}
$serverRuntimeVersion = $runtimeEntries[0].Name.Substring($runtimePrefix.Length)
if ($serverRuntimeVersion -ne $expectedServerRuntime) {
    throw "self-contained publish resolved runtime $serverRuntimeVersion, expected pinned $expectedServerRuntime"
}
Step "  pinned Microsoft.NETCore.App.Runtime.win-x64 $serverRuntimeVersion"
$clientTools = Join-Path $stage 'server\client-tools'
New-Item -ItemType Directory -Path $clientTools -Force | Out-Null
Copy-Item (Join-Path $repo 'scripts\register-client.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\verify-clients.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\verify-install.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\complete-install.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\stop-installed-server.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\hz-call.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\uninstall-cleanup.ps1') $clientTools -Force
Copy-Item (Join-Path $repo 'scripts\toml-section.lib.ps1') $clientTools -Force
Step '  staged safe Codex/Claude registration, deferred completion and verification helpers'

# --- The plugin, ONE ARTIFACT PER YEAR. --------------------------------------
#
# It used to be one per RUNTIME: the 2024 build was shipped to Revit 2023 and the
# 2026 build to Revit 2025, on the reasoning that they share a target framework.
# They do not share an API. RevitAPI.dll differs between every one of these years,
# and a plugin compiled against 2026 and loaded by 2025 binds to methods that
# version does not have - a MissingMethodException at the first call, not at load,
# so it looks like the command is broken rather than the install.
#
# Each year is now built against its OWN RevitAPI and staged under its own name.
$manifest = @()
foreach ($year in 2023, 2024, 2025, 2026, 2027) {

    $revitDir = "C:\Program Files\Autodesk\Revit $year"
    if (-not (Test-Path (Join-Path $revitDir 'RevitAPI.dll'))) {
        # Not installed on THIS machine. Say so and leave that year out of the
        # payload: shipping a year we could not build against its own API is the
        # thing this loop exists to stop.
        Step ("  Revit $year is not installed here - NOT building it, and it will NOT be in the installer")
        continue
    }

    Step ("building the plugin for Revit {0} against its own RevitAPI" -f $year)
    & dotnet restore (Join-Path $repo 'src\Horizun.Revit') -p:RevitYear=$year --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw ("plugin locked restore failed for $year") }
    & dotnet build (Join-Path $repo 'src\Horizun.Revit') -c $Config -p:RevitYear=$year --no-restore --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw ("plugin build failed for $year") }

    $bin = Join-Path $repo "src\Horizun.Revit\bin\$Config"
    $dll = Join-Path $bin 'Horizun.Revit.dll'
    if (-not (Test-Path $dll)) { throw "no plugin DLL after building $year" }

    # bin is shared across years, so prove the bytes are the ones just built rather
    # than a leftover of the previous iteration.
    $expected = if ($year -le 2024) { '.NETFramework,Version=v4.8' }
                elseif ($year -ge 2027) { '.NETCoreApp,Version=v10.0' }
                else { '.NETCoreApp,Version=v8.0' }
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($dll))
    if ($text -notlike "*$expected*") { throw "staged the wrong runtime for ${year}: expected $expected" }

    $out = Join-Path $stage "plugin\$year"
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    Get-ChildItem $bin -Filter *.dll -File | ForEach-Object { Copy-Item $_.FullName $out -Force }
    $lib = Join-Path $bin 'lib'
    if (Test-Path $lib) { Copy-Item $lib (Join-Path $out 'lib') -Recurse -Force }
    # The ribbon icons. Same omission as the deploy library's: *.dll and lib were
    # the whole list, and a missing icon degrades silently to an imageless button.
    $res = Join-Path $bin 'Resources'
    if (Test-Path $res) { Copy-Item $res (Join-Path $out 'Resources') -Recurse -Force }

    # The recipes. Unlike an icon, a missing one does not degrade: the tool is still
    # advertised and still accepted, and it fails when somebody uses it. So this is
    # not "copy if present" - if the build produced recipes and the payload has none,
    # that is a broken installer and it stops here.
    $recipes = Join-Path $bin 'Recipes'
    if (Test-Path $recipes) {
        Copy-Item $recipes (Join-Path $out 'Recipes') -Recurse -Force
        $staged = (Get-ChildItem (Join-Path $out 'Recipes') -Filter *.py -File).Count
        $built  = (Get-ChildItem $recipes -Filter *.py -File).Count
        if ($staged -ne $built) {
            throw "staged $staged of $built recipes for ${year}: the recipe-backed tools would install and then fail at first use"
        }
    }

    # IronPython is not shipped by Revit; without it the escape hatch dies at load.
    foreach ($needed in 'IronPython.dll', 'Microsoft.Scripting.dll') {
        if (-not (Test-Path (Join-Path $out $needed))) { throw "$needed missing from the $year payload" }
    }

    $staged = Get-Item (Join-Path $out 'Horizun.Revit.dll')
    # THE WHOLE HASH. It used to be the first 16 hex characters, which is a
    # convenient thing to read in a log and not a thing to verify an artifact
    # against - and it was the only identity the manifest carried.
    $sha = (Get-FileHash $staged.FullName -Algorithm SHA256).Hash.ToLower()

    # AND EVERY OTHER FILE. Horizun.Revit.dll was the only one recorded, so a
    # release could be verified as correct with a corrupted IronPython.dll sitting
    # beside a perfect plugin - and that failure surfaces as a broken
    # execute_python weeks later, with nothing tying it back to the install.
    $payload = Get-HorizunPayloadListing $out

    $manifest += [pscustomobject]@{
        Year      = $year
        Runtime   = if ($year -le 2024) { 'net48' } elseif ($year -ge 2027) { 'net10.0' } else { 'net8.0' }
        Product   = $staged.VersionInfo.FileVersion
        Sha256    = $sha
        Size      = $staged.Length
        Files     = (Get-ChildItem $out -Recurse -File).Count
        BuiltAgainst = "RevitAPI $year"
        Payload      = $payload.Files
        StdLibFiles  = $payload.StdLibFiles
        StdLibDigest = $payload.StdLibDigest
    }
    Step ("  staged {0} files for {1} ({2} hashed individually, {3} stdlib aggregated), sha {4}..." -f `
          $manifest[-1].Files, $year, $payload.FileCount, $payload.StdLibFiles, $sha.Substring(0, 16))
}

if ($manifest.Count -eq 0) { throw 'no Revit year could be built on this machine; there is nothing to package' }

# Every supported year, or say which is missing. A package with four of five is a
# package that silently drops a Revit generation, and the symptom lands on
# whoever opens that year six weeks later.
$missing = @(2023, 2024, 2025, 2026, 2027 | Where-Object { $_ -notin $manifest.Year })
if ($missing.Count -gt 0) {
    Write-Host ("[pack] WARNING: no payload for " + ($missing -join ', ') +
                " - that Revit is not installed on this machine. The installer will NOT cover those years.") -ForegroundColor Yellow
}

# The SERVER's identity belongs here too. It is half of what gets installed and
# it was the half the manifest never mentioned, so "does the installed server
# match this release?" could not be answered from the release's own record.
$serverExe = Join-Path $stage 'server\horizun-mcp.exe'
if (-not (Test-Path $serverExe)) { throw "the staged server executable is missing: $serverExe" }
$serverItem = Get-Item $serverExe
$serverSha  = (Get-FileHash $serverExe -Algorithm SHA256).Hash.ToLower()

# schema 2: an OBJECT, not a bare array. The array had nowhere to put the commit,
# the server, or whether the tree was clean - the three facts needed to say that
# a running binary is this release.
$doc = [pscustomobject]@{
    Schema         = 2
    Commit         = $commit
    CleanTree      = (-not $dirty)
    BuiltUtc       = (Get-Date).ToUniversalTime().ToString('o')
    Config         = $Config
    Server         = [pscustomobject]@{
        File    = 'server/horizun-mcp.exe'
        Product = $serverItem.VersionInfo.FileVersion
        Sha256  = $serverSha
        Size    = $serverItem.Length
        SelfContained = $true
        RuntimeIdentifier = 'win-x64'
        RuntimeComponent = [pscustomobject]@{
            Name = 'Microsoft.NETCore.App.Runtime.win-x64'
            Version = $serverRuntimeVersion
            Purl = "pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@$serverRuntimeVersion"
        }
        # The rest of the server directory: Newtonsoft, the runtimeconfig, the deps
        # file. horizun-mcp.exe was the only one recorded, and it is an apphost -
        # the code that runs is in horizun-mcp.dll, which the manifest never named.
        Payload = (Get-HorizunPayloadListing (Join-Path $stage 'server')).Files
    }
    # What Revit reads to find the add-in at all. A payload that is perfect and a
    # manifest that is wrong is an add-in that does not load, and it was outside
    # the record entirely.
    AddinManifest  = [pscustomobject]@{
        File   = 'Horizun.addin'
        Sha256 = (Get-HorizunFileHash (Join-Path $repo 'src\Horizun.Revit\Horizun.addin'))
    }
    MissingYears   = $missing
    Plugins        = $manifest
}
# Depth 6: Plugins -> Payload -> the per-file objects. At depth 5 ConvertTo-Json
# silently renders the payload entries as type names, which would put the string
# "System.Management.Automation.PSCustomObject" in the manifest where a hash belongs.
$doc | ConvertTo-Json -Depth 6 | Out-File (Join-Path $stage 'manifest.json') -Encoding utf8
Step ("  manifest: commit {0}, server {1}..., years {2}" -f `
      $commit.Substring(0, [Math]::Min(12, $commit.Length)), $serverSha.Substring(0, 16),
      (($manifest | ForEach-Object { $_.Year }) -join ', '))

Copy-Item (Join-Path $repo 'src\Horizun.Revit\Horizun.addin') $stage -Force

}   # end of the build+stage phase

# --- The installer. ----------------------------------------------------------
if ($SkipInstaller) { Step 'staging done (installer skipped)'; return }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup (ISCC.exe) not found - the payload is staged in $stage but no setup was built."
    return
}

# ONE source for the product version. Every project inherits it, and the
# installer receives the same value.
$versionProps = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = ($versionProps.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'could not read <Version> from Directory.Build.props' }
# CLEAR THE OLD ONES FIRST. The output name carries the version, so bumping it
# leaves the previous installer sitting in dist/ beside the new one - and every
# consumer of this directory picks with `Get-ChildItem dist -Filter '*setup.exe' |
# Select-Object -First 1`, which is alphabetical. Measured here: after the 0.3.0
# bump, dist/ held both 0.2.0 and 0.3.0 setups, and "first" was 0.2.0. CI would
# have hashed, published, installed and verified LAST RELEASE'S installer under
# this commit's name, and every check downstream would have passed about it.
$stale = @(Get-ChildItem $dist -Filter '*setup.exe' -File -ErrorAction SilentlyContinue)
foreach ($s in $stale) {
    Step ("removing a previous installer so only this build is in dist: {0}" -f $s.Name)
    Remove-Item $s.FullName -Force
}

Step ("compiling the installer for version $version")
& $iscc (Join-Path $repo 'installer\horizun-mcp.iss') /Q "/DAppVersion=$version"
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$built = @(Get-ChildItem $dist -Filter '*setup.exe' -File)
if ($built.Count -ne 1) {
    throw ("expected exactly one installer in $dist after building, found " + $built.Count +
           " (" + (($built | ForEach-Object { $_.Name }) -join ', ') + "). Anything that picks 'the first ' +
           'setup.exe' would be choosing between them alphabetically.")
}
Step ("installer: {0} ({1:N1} MB)" -f $built[0].FullName, ($built[0].Length / 1MB))
