#Requires -Version 5.1
<#
  Shared by deploy.ps1 and deploy-both.ps1.

  WHY IT EXISTS. deploy-both needs something deploy.ps1 cannot give it: nothing may
  land until EVERY half has been built, and if any step fails, everything must go
  back. That means the copy has to be callable from a caller that owns the
  transaction, and the two scripts must not end up with two versions of "how a
  payload is installed" - which is the same divergence that had two open commands
  running different guards for months.

  Everything here is a step. Nothing here decides policy; the callers do.
#>

# The provenance stamp the build bakes into every assembly: version+sha[-dirty].
# Read from the BYTES, not by loading the assembly - loading would lock the file
# and would need the Revit API present for the add-in.
function Get-HorizunProvenance([string]$DllPath) {
    if (-not (Test-Path $DllPath)) { return $null }
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($DllPath))
    $m = [regex]::Match($text, '(\d+\.\d+\.\d+)\+([0-9a-f]{40})(-dirty)?')
    if (-not $m.Success) { return $null }
    [pscustomobject]@{
        Path    = $DllPath
        Version = $m.Groups[1].Value
        Sha     = $m.Groups[2].Value
        Dirty   = $m.Groups[3].Success
    }
}

# Which .NET a given Revit year loads. A net48 plugin in a .NET 8 Revit (or the
# reverse) fails at load with an error that says nothing about why.
function Get-HorizunExpectedTfm([int]$Year) {
    if ($Year -le 2024) { return '.NETFramework,Version=v4.8' }
    if ($Year -ge 2027) { return '.NETCoreApp,Version=v10.0' }
    return '.NETCoreApp,Version=v8.0'
}

function Get-HorizunActualTfm([string]$DllPath) {
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($DllPath))
    if ($text -like '*.NETFramework,Version=v4.8*') { return '.NETFramework,Version=v4.8' }
    if ($text -like '*.NETCoreApp,Version=v10.0*') { return '.NETCoreApp,Version=v10.0' }
    if ($text -like '*.NETCoreApp,Version=v8.0*') { return '.NETCoreApp,Version=v8.0' }
    return 'unrecognized'
}

# bin\<Config> is shared across every RevitYear, so it can hold a build for a
# DIFFERENT year than the one being deployed. Refuse the mismatch rather than
# shipping it.
function Assert-HorizunTfm([string]$DllPath, [int]$Year) {
    $expected = Get-HorizunExpectedTfm $Year
    $actual = Get-HorizunActualTfm $DllPath
    if ($actual -ne $expected) {
        throw ("Refusing to deploy: $DllPath was built for $actual, but Revit $Year needs $expected. " +
               "bin is shared across years - rebuild for this year first.")
    }
}

# EVERY PLACE REVIT WILL LOOK. Both roots matter and only one was ever considered:
# the per-user Addins folder, and the machine-wide ProgramData one that an
# installer writes. A stale Horizun.addin in either points Revit at an old DLL.
function Get-HorizunAddinRoots {
    $roots = @()
    if ($env:APPDATA)     { $roots += (Join-Path $env:APPDATA     'Autodesk\Revit\Addins') }
    if ($env:PROGRAMDATA) { $roots += (Join-Path $env:PROGRAMDATA 'Autodesk\Revit\Addins') }
    $roots
}

<#
  Every Horizun.addin installed on this machine, wherever it is.

  This is the discovery the split-contract check is built on. Deploying to the
  years you remembered says nothing about the years you did not: a Revit 2024 with
  last month's add-in still on disk pairs with the new server and is refused on the
  contract hash, and the person who ran the deploy has no reason to look there.
#>
function Get-HorizunInstalledAddins {
    $found = @()
    foreach ($root in Get-HorizunAddinRoots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($yearDir in Get-ChildItem $root -Directory -ErrorAction SilentlyContinue) {
            $manifest = Join-Path $yearDir.FullName 'Horizun.addin'
            if (-not (Test-Path $manifest)) { continue }

            $year = 0
            if (-not [int]::TryParse($yearDir.Name, [ref]$year)) { $year = 0 }
            $dll = Join-Path $yearDir.FullName 'Horizun\Horizun.Revit.dll'

            $found += [pscustomobject]@{
                Year       = $year
                Root       = $root
                AddinsDir  = $yearDir.FullName
                Manifest   = $manifest
                PluginDir  = Join-Path $yearDir.FullName 'Horizun'
                Dll        = $dll
                DllExists  = (Test-Path $dll)
                Provenance = (Get-HorizunProvenance $dll)
                Scope      = if ($root -like "$env:PROGRAMDATA*") { 'all-users' } else { 'per-user' }
            }
        }
    }
    $found | Sort-Object Year, Scope
}

# Revit holds a lock on the plugin it loaded. A delete part-way down the list
# aborts with half the payload gone - a broken install that only shows up at the
# next Revit start. Ask first, and name every file that says no.
function Get-HorizunLockedFiles([string]$Dir) {
    $locked = @()
    foreach ($f in @(Get-ChildItem $Dir -Filter *.dll -File -ErrorAction SilentlyContinue)) {
        try { $s = [IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None'); $s.Close() }
        catch { $locked += $f.Name }
    }
    $locked
}

<#
  Install one year's payload from a directory that already holds a build for THAT
  year. Does no building, no version policy and no rollback - the caller owns all
  three.
#>
function Install-HorizunPayload {
    param(
        [Parameter(Mandatory)][string]$Source,      # a bin dir, or a staged copy of one
        [Parameter(Mandatory)][int]$Year,
        [Parameter(Mandatory)][string]$ManifestSource,
        [string]$AddinsRoot
    )

    $dll = Join-Path $Source 'Horizun.Revit.dll'
    if (-not (Test-Path $dll)) { throw "No add-in build at $Source (Horizun.Revit.dll is missing)." }
    Assert-HorizunTfm $dll $Year

    if (-not $AddinsRoot) { $AddinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year" }
    $pluginDir = Join-Path $AddinsRoot 'Horizun'
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

    $locked = Get-HorizunLockedFiles $pluginDir
    if ($locked.Count -gt 0) {
        throw ("Refusing to deploy to Revit ${Year}: these files are locked by a running process - " +
               ($locked -join ', ') + ". Close Revit and run this again. Nothing was changed.")
    }

    # Clear the previous payload first: the runtimes do not carry the same set of
    # dependency DLLs, so copying over an older deploy leaves the leftovers of the
    # other framework sitting next to the new plugin.
    @(Get-ChildItem $pluginDir -Filter *.dll -File -ErrorAction SilentlyContinue) | Remove-Item -Force

    Get-ChildItem $Source -Filter *.dll -File | ForEach-Object { Copy-Item $_.FullName $pluginDir -Force }

    # The Python standard library (IronPython.StdLib), so `import json` resolves.
    $libSrc = Join-Path $Source 'lib'
    $pyCount = 0
    if (Test-Path $libSrc) {
        $libDst = Join-Path $pluginDir 'lib'
        if (Test-Path $libDst) { Remove-Item $libDst -Recurse -Force }
        Copy-Item $libSrc $libDst -Recurse -Force
        $pyCount = (Get-ChildItem $libDst -Recurse -Filter *.py -File).Count
    }

    # The ribbon icons. Found the expensive way: this function copied *.dll and lib
    # and nothing else, so the first deploy of the ribbon shipped a tab whose
    # buttons had no images - Ribbon.cs degrades to a plain button on a missing
    # icon, which is exactly why nothing failed and nobody was told.
    $resSrc = Join-Path $Source 'Resources'
    if (Test-Path $resSrc) {
        $resDst = Join-Path $pluginDir 'Resources'
        if (Test-Path $resDst) { Remove-Item $resDst -Recurse -Force }
        Copy-Item $resSrc $resDst -Recurse -Force
    }

    # The recipes: the geometry behind the recipe-backed tools. THE SAME OMISSION AS
    # THE ICONS ABOVE would be worse here, not milder: a missing icon degrades to a
    # plain button, while a missing recipe is a tool that is advertised by tools/list,
    # accepted by the dispatcher, and fails at the moment of use. They are counted and
    # returned so the deploy REPORTS how many landed rather than assuming any did.
    $recSrc = Join-Path $Source 'Recipes'
    $recipeCount = 0
    if (Test-Path $recSrc) {
        $recDst = Join-Path $pluginDir 'Recipes'
        if (Test-Path $recDst) { Remove-Item $recDst -Recurse -Force }
        Copy-Item $recSrc $recDst -Recurse -Force
        $recipeCount = (Get-ChildItem $recDst -Filter *.py -File).Count
    }

    Copy-Item $ManifestSource $AddinsRoot -Force

    [pscustomobject]@{
        Year      = $Year
        AddinsDir = $AddinsRoot
        PluginDir = $pluginDir
        Dll       = (Join-Path $pluginDir 'Horizun.Revit.dll')
        Manifest  = (Join-Path $AddinsRoot 'Horizun.addin')
        StdLibPy  = $pyCount
        Recipes   = $recipeCount
    }
}

# SHA-256 of a file, for proving a copy landed intact rather than assuming it did.
function Get-HorizunFileHash([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

<#
  EVERY FILE OF AN INSTALLED PAYLOAD, hashed.

  The release manifest used to carry two hashes: horizun-mcp.exe and, per year,
  Horizun.Revit.dll. Those are the two files whose provenance anybody thinks about,
  and they are a small minority of what actually gets installed. Newtonsoft.Json,
  IronPython, Microsoft.Scripting and the whole Python standard library ship
  alongside them, are loaded by the same process, and were covered by nothing: a
  release could be verified as correct with a corrupted IronPython.dll next to a
  perfect Horizun.Revit.dll, and the failure would surface as a broken
  execute_python weeks later with no way back to the install.

  THE STDLIB IS AGGREGATED, deliberately and visibly. It is roughly two thousand
  .py files; listing them individually would make the manifest unreadable and
  unreviewable, which is its own kind of unverifiable. Instead every file under
  lib\ contributes its relative path AND its hash to one ordered digest, so a
  single changed byte anywhere in the tree changes the aggregate - and the file
  count is carried beside it so a tree that lost files cannot match one that did not.
#>
function Get-HorizunPayloadListing([string]$Root) {
    if (-not (Test-Path $Root)) { return $null }
    $rootFull = (Resolve-Path $Root).Path.TrimEnd('\') + '\'

    $files = @()
    $stdlib = @()
    foreach ($f in Get-ChildItem $Root -Recurse -File | Sort-Object FullName) {
        $rel = $f.FullName.Substring($rootFull.Length).Replace('\', '/')
        if ($rel -like 'lib/*') { $stdlib += [pscustomobject]@{ Rel = $rel; File = $f } ; continue }
        $files += [pscustomobject]@{
            Path   = $rel
            Sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
            Size   = $f.Length
        }
    }

    $stdlibDigest = $null
    if ($stdlib.Count -gt 0) {
        $sb = New-Object System.Text.StringBuilder
        foreach ($s in ($stdlib | Sort-Object Rel)) {
            [void]$sb.Append($s.Rel).Append([char]31)
            [void]$sb.Append((Get-FileHash $s.File.FullName -Algorithm SHA256).Hash.ToLower()).Append([char]30)
        }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString()))
            $stdlibDigest = ([BitConverter]::ToString($bytes) -replace '-', '').ToLower()
        }
        finally { $sha.Dispose() }
    }

    [pscustomobject]@{
        Files       = $files
        FileCount   = $files.Count
        StdLibFiles = $stdlib.Count
        # One ordered digest over every path AND hash under lib\. Named so nobody
        # mistakes it for the hash of a file.
        StdLibDigest = $stdlibDigest
    }
}
