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
    $horizunAddInId = 'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30'
    foreach ($root in Get-HorizunAddinRoots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($yearDir in Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue) {
            $year = 0
            if (-not [int]::TryParse($yearDir.Name, [ref]$year)) { $year = 0 }
            if ($year -eq 0) { continue }
            foreach ($manifest in @(Get-HorizunManifestsByAddInId -AddinsRoot $root -Year $year -AddInId $horizunAddInId)) {
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

function Assert-HorizunNoReparseTree([string]$Path, [string]$Label) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $volume = [IO.Path]::GetPathRoot($full)
    $current = $volume.TrimEnd('\')
    if (-not $current) { $current = $volume }
    foreach ($component in $full.Substring($volume.Length).Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $component
        if (-not (Test-Path -LiteralPath $current)) { break }
        $ancestor = Get-Item -LiteralPath $current -Force
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing $Label through a link or junction: $current"
        }
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return }
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue($full)
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing $Label through a link or junction: $current"
        }
        foreach ($child in @(Get-ChildItem -LiteralPath $current -Directory -Force -ErrorAction Stop)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing $Label through a link or junction: $($child.FullName)"
            }
            $pending.Enqueue($child.FullName)
        }
    }
}

function Get-HorizunManifestsByAddInId {
    param(
        [Parameter(Mandatory)][string]$AddinsRoot,
        [Parameter(Mandatory)][int]$Year,
        [Parameter(Mandatory)][string]$AddInId
    )
    $yearRoot = Join-Path $AddinsRoot ([string]$Year)
    Assert-HorizunNoReparseTree $yearRoot "Revit $Year add-in manifest discovery"
    if (-not (Test-Path -LiteralPath $yearRoot -PathType Container)) { return @() }
    $needle = [Guid]::Empty
    if (-not [Guid]::TryParse($AddInId.Trim(), [ref]$needle)) {
        throw "Invalid expected Revit AddInId: $AddInId"
    }
    $found = @()
    foreach ($candidate in @(Get-ChildItem -LiteralPath $yearRoot -Filter '*.addin' -File -Force -ErrorAction Stop)) {
        try {
            $settings = New-Object Xml.XmlReaderSettings
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($candidate.FullName, $settings)
            try {
                while ($reader.Read()) {
                    if ($reader.NodeType -eq [Xml.XmlNodeType]::Element -and $reader.LocalName -eq 'AddInId') {
                        $candidateId = [Guid]::Empty
                        $candidateValue = $reader.ReadElementContentAsString().Trim()
                        if ([Guid]::TryParse($candidateValue, [ref]$candidateId) -and $candidateId -eq $needle) {
                            $found += $candidate.FullName
                            break
                        }
                    }
                }
            }
            finally { $reader.Dispose() }
        }
        catch { throw "Cannot safely inspect Revit add-in manifest '$($candidate.FullName)': $($_.Exception.Message)" }
    }
    return @($found)
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
    Assert-HorizunNoReparseTree $pluginDir "Revit $Year deployment"

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

<#
  Materialise the exact add-in payload shape consumed by Install-HorizunPayload.
  A dotnet bin directory also contains build/debug artifacts (PDB, deps.json and
  RID assets) which are not Revit loadable payload. Manifests must be computed
  from this projection, never from the raw bin directory, or verification will
  describe files the installer deliberately did not deploy.
#>
function Copy-HorizunPluginPayloadToStage {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Plugin build output is missing: $Source"
    }
    if (Test-Path -LiteralPath $Destination) {
        throw "Plugin stage must be a new directory: $Destination"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Filter '*.dll' -File |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Force }
    foreach ($directory in 'lib','Resources','Recipes') {
        $from = Join-Path $Source $directory
        if (Test-Path -LiteralPath $from -PathType Container) {
            Copy-Item -LiteralPath $from -Destination (Join-Path $Destination $directory) -Recurse -Force
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Destination 'Horizun.Revit.dll') -PathType Leaf)) {
        throw "Projected plugin payload has no Horizun.Revit.dll: $Destination"
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
    Assert-HorizunNoReparseTree $Root 'payload inventory'
    $rootFull = (Resolve-Path $Root).Path.TrimEnd('\') + '\'

    $files = @()
    $stdlib = @()
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName) {
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

# ---------------------------------------------------------------------------
# SIGNING AND MANIFEST INTEGRITY. Three functions the release flow needs so the
# same arithmetic that signs and manifests a stage is the one that validates it.
#
# The defect these exist to fix: the manifest used to be written during the build
# (pack -SkipInstaller) BEFORE sign.ps1 ran. Signing a PE file changes its bytes,
# so every hash in the manifest then described the UNSIGNED file, while the
# installer wrapped the SIGNED one. -InstallerOnly rebuilt nothing and re-checked
# nothing, so it shipped a manifest that did not describe its own payload. The fix
# is to RECOMPUTE the manifest after signing (Update-HorizunManifestToStage) and
# to REFUSE to build an installer whose stage no longer matches its manifest
# (Test-HorizunStageMatchesManifest).
# ---------------------------------------------------------------------------

# Whether a PE file carries an Authenticode signature, and by whom. SIGNED is about
# the presence of a signer certificate, NOT about trust: a self-signed certificate
# reports Status 'UnknownError' ("not trusted by the trust provider") yet the file
# IS signed. Trust is the machine owner's separate decision; this reports both so
# neither is confused for the other.
function Get-HorizunSignatureInfo([string]$Path) {
    if (-not (Test-Path $Path)) { return [pscustomobject]@{ Signed = $false; Status = 'missing'; Thumbprint = $null; Subject = $null } }
    $sig = Get-AuthenticodeSignature -FilePath $Path
    [pscustomobject]@{
        Signed     = [bool]$sig.SignerCertificate
        Status     = "$($sig.Status)"
        Thumbprint = $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Thumbprint } else { $null })
        Subject    = $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { $null })
        Timestamped = [bool]$sig.TimeStamperCertificate
    }
}

# The own binaries a release signs and stakes its identity on: the server apphost,
# the server's real code (horizun-mcp.dll), and one Horizun.Revit.dll per year.
# Third-party DLLs (Newtonsoft, IronPython) are signed by their publishers and are
# not ours to re-sign, so they are not in this list.
function Get-HorizunOwnBinaries([string]$Stage) {
    $own = @()
    foreach ($n in 'horizun-mcp.exe', 'horizun-mcp.dll') {
        $p = Join-Path $Stage "server\$n"
        if (Test-Path $p) { $own += $p }
    }
    foreach ($dll in Get-ChildItem (Join-Path $Stage 'plugin') -Filter 'Horizun.Revit.dll' -Recurse -File -ErrorAction SilentlyContinue) {
        $own += $dll.FullName
    }
    $own
}

<#
  Re-hash every file the manifest references, from the stage as it is NOW, and
  return each disagreement as a string. An empty result means the stage is
  byte-for-byte what the manifest says. Used by -InstallerOnly before it builds
  and by verify-release, so a file signed or altered AFTER the manifest was
  written is caught rather than shipped.
#>
function Test-HorizunStageMatchesManifest([string]$Stage) {
    $manifestPath = Join-Path $Stage 'manifest.json'
    if (-not (Test-Path $manifestPath)) { return @("no manifest at $manifestPath") }
    $doc = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $bad = @()

    # The server apphost.
    $serverFile = Join-Path $Stage ($doc.Server.File -replace '/', '\')
    if (-not (Test-Path $serverFile)) { $bad += "server missing: $($doc.Server.File)" }
    elseif ((Get-HorizunFileHash $serverFile) -ne $doc.Server.Sha256) {
        $bad += "server $($doc.Server.File): $((Get-HorizunFileHash $serverFile)) vs manifest $($doc.Server.Sha256)"
    }

    # The whole server directory (horizun-mcp.dll, Newtonsoft, the runtimeconfig...).
    $serverDir = Join-Path $Stage 'server'
    $serverListing = Get-HorizunPayloadListing $serverDir
    $serverExpected = @($doc.Server.Payload | ForEach-Object { "$($_.Path)".Replace('\', '/') })
    $serverActual = @($serverListing.Files | ForEach-Object { "$($_.Path)".Replace('\', '/') })
    foreach ($path in @($serverActual | Where-Object { $serverExpected -notcontains $_ })) {
        $bad += "unexpected server payload: $path"
    }
    foreach ($path in @($serverExpected | Where-Object { $serverActual -notcontains $_ })) {
        $bad += "server manifest entry absent from payload: $path"
    }
    foreach ($p in @($doc.Server.Payload)) {
        $onDisk = Join-Path $serverDir ($p.Path -replace '/', '\')
        if (-not (Test-Path $onDisk)) { $bad += "server payload missing: $($p.Path)"; continue }
        if ((Get-HorizunFileHash $onDisk) -ne $p.Sha256) { $bad += "server payload $($p.Path): changed since the manifest" }
    }

    # Every year's plugin dll and its whole payload.
    foreach ($entry in @($doc.Plugins)) {
        $pluginDir = Join-Path $Stage "plugin\$($entry.Year)"
        $dll = Join-Path $pluginDir 'Horizun.Revit.dll'
        if (-not (Test-Path $dll)) { $bad += "plugin $($entry.Year) missing"; continue }
        if ((Get-HorizunFileHash $dll) -ne $entry.Sha256) {
            $bad += "plugin $($entry.Year) Horizun.Revit.dll: $((Get-HorizunFileHash $dll)) vs manifest $($entry.Sha256)"
        }
        $pluginListing = Get-HorizunPayloadListing $pluginDir
        $pluginExpected = @($entry.Payload | ForEach-Object { "$($_.Path)".Replace('\', '/') })
        $pluginActual = @($pluginListing.Files | ForEach-Object { "$($_.Path)".Replace('\', '/') })
        foreach ($path in @($pluginActual | Where-Object { $pluginExpected -notcontains $_ })) {
            $bad += "plugin $($entry.Year) unexpected payload: $path"
        }
        foreach ($path in @($pluginExpected | Where-Object { $pluginActual -notcontains $_ })) {
            $bad += "plugin $($entry.Year) manifest entry absent from payload: $path"
        }
        if ([int]$entry.StdLibFiles -ne [int]$pluginListing.StdLibFiles) {
            $bad += "plugin $($entry.Year) stdlib file count: stage $($pluginListing.StdLibFiles) vs manifest $($entry.StdLibFiles)"
        }
        if ("$($entry.StdLibDigest)" -ne "$($pluginListing.StdLibDigest)") {
            $bad += "plugin $($entry.Year) stdlib digest changed since the manifest"
        }
        $actualTotal = [int]$pluginListing.FileCount + [int]$pluginListing.StdLibFiles
        if ($null -ne $entry.Files -and [int]$entry.Files -ne $actualTotal) {
            $bad += "plugin $($entry.Year) total file count: stage $actualTotal vs manifest $($entry.Files)"
        }
        foreach ($p in @($entry.Payload)) {
            $onDisk = Join-Path $pluginDir ($p.Path -replace '/', '\')
            if (-not (Test-Path $onDisk)) { $bad += "plugin $($entry.Year) payload missing: $($p.Path)"; continue }
            if ((Get-HorizunFileHash $onDisk) -ne $p.Sha256) { $bad += "plugin $($entry.Year) payload $($p.Path): changed since the manifest" }
        }
    }

    # The .addin, if the manifest recorded it.
    if ($doc.AddinManifest) {
        $addin = Join-Path $Stage 'Horizun.addin'
        if (-not (Test-Path $addin)) { $bad += 'Horizun.addin missing from the stage' }
        elseif ((Get-HorizunFileHash $addin) -ne $doc.AddinManifest.Sha256) { $bad += 'Horizun.addin: changed since the manifest' }
    }

    $bad
}

<#
  Recompute the manifest from the stage AS IT IS NOW, preserving the provenance
  fields the build wrote (commit, clean-tree, product versions) and refreshing
  every hash, size and payload listing so they describe the CURRENT bytes - the
  signed ones, when this runs after sign.ps1. It also records a Signature block:
  whether every own binary is signed, by whom, and each file's signature status.

  This is what makes "sign, then manifest" true instead of "manifest, then sign".
#>
function Update-HorizunManifestToStage([string]$Stage) {
    $manifestPath = Join-Path $Stage 'manifest.json'
    if (-not (Test-Path $manifestPath)) { throw "no manifest at $manifestPath - run pack.ps1 -SkipInstaller first" }
    $doc = Get-Content $manifestPath -Raw | ConvertFrom-Json

    # Server: apphost hash+size, and the whole directory listing.
    $serverFile = Join-Path $Stage ($doc.Server.File -replace '/', '\')
    if (Test-Path $serverFile) {
        $doc.Server.Sha256 = Get-HorizunFileHash $serverFile
        $doc.Server | Add-Member -Force NoteProperty Size ((Get-Item $serverFile).Length)
        $doc.Server.Payload = (Get-HorizunPayloadListing (Join-Path $Stage 'server')).Files
    }

    # Each plugin: dll hash+size, file count, and the whole payload + stdlib digest.
    foreach ($entry in @($doc.Plugins)) {
        $pluginDir = Join-Path $Stage "plugin\$($entry.Year)"
        $dll = Join-Path $pluginDir 'Horizun.Revit.dll'
        if (-not (Test-Path $dll)) { continue }
        $entry.Sha256 = Get-HorizunFileHash $dll
        $entry | Add-Member -Force NoteProperty Size ((Get-Item $dll).Length)
        $listing = Get-HorizunPayloadListing $pluginDir
        $entry.Payload = $listing.Files
        $entry | Add-Member -Force NoteProperty StdLibFiles $listing.StdLibFiles
        $entry | Add-Member -Force NoteProperty StdLibDigest $listing.StdLibDigest
        $entry | Add-Member -Force NoteProperty Files ((Get-ChildItem $pluginDir -Recurse -File).Count)
    }

    # The .addin.
    if ($doc.AddinManifest) {
        $addin = Join-Path $Stage 'Horizun.addin'
        if (Test-Path $addin) { $doc.AddinManifest.Sha256 = Get-HorizunFileHash $addin }
    }

    # The signature block: what is signed, and by whom.
    $own = Get-HorizunOwnBinaries $Stage
    $sigFiles = @()
    $allSigned = $own.Count -gt 0
    $thumb = $null; $subject = $null
    foreach ($p in $own) {
        $info = Get-HorizunSignatureInfo $p
        if (-not $info.Signed -or $info.Status -ne 'Valid' -or -not $info.Timestamped) { $allSigned = $false }
        elseif (-not $thumb) { $thumb = $info.Thumbprint; $subject = $info.Subject }
        $sigFiles += [pscustomobject]@{
            File = ($p.Substring($Stage.Length).TrimStart('\') -replace '\\', '/')
            Signed = $info.Signed; Status = $info.Status; Thumbprint = $info.Thumbprint; Timestamped = $info.Timestamped
        }
    }
    $doc | Add-Member -Force NoteProperty Signed $allSigned
    $doc | Add-Member -Force NoteProperty Signature ([pscustomobject]@{
        Signed = $allSigned
        SignerThumbprint = $thumb
        SignerSubject = $subject
        Files = $sigFiles
        RecomputedUtc = (Get-Date).ToUniversalTime().ToString('o')
    })

    $doc | ConvertTo-Json -Depth 6 | Out-File $manifestPath -Encoding utf8
    $doc
}
