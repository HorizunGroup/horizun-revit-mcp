#Requires -Version 5.1
<#
  Generate a CycloneDX 1.6 inventory from dist/stage: the bytes that actually
  ship, not the dependency declarations that may or may not be copied.

  Every staged file receives a SHA-256 and a named licence. An unknown path is
  a release failure; silently omitting it would make the SBOM less trustworthy
  than the payload manifest it is meant to complement.
#>
[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo 'dist\stage'
if (-not (Test-Path $stage)) { throw 'Nothing staged. Run: pwsh scripts/pack.ps1 -SkipInstaller' }
if (-not $OutFile) { $OutFile = Join-Path $repo 'dist\sbom.json' }

$thirdPartyDllLicences = @{
    'Newtonsoft.Json.dll'                = 'MIT'
    'IronPython.dll'                     = 'Apache-2.0'
    'IronPython.Modules.dll'             = 'Apache-2.0'
    'IronPython.SQLite.dll'              = 'Apache-2.0'
    'IronPython.Wpf.dll'                 = 'Apache-2.0'
    'Microsoft.Dynamic.dll'              = 'Apache-2.0'
    'Microsoft.Scripting.dll'            = 'Apache-2.0'
    'Microsoft.Scripting.Metadata.dll'   = 'Apache-2.0'
    'Mono.Unix.dll'                      = 'MIT'
    'System.CodeDom.dll'                 = 'MIT'
    'System.Text.Encoding.CodePages.dll' = 'MIT'
    'System.Buffers.dll'                 = 'MIT'
    'System.Memory.dll'                  = 'MIT'
    'System.Numerics.Vectors.dll'        = 'MIT'
    'System.Runtime.CompilerServices.Unsafe.dll' = 'MIT'
}

function Get-StagedLicence([string]$relativePath) {
    $path = $relativePath.Replace('\','/')
    $leaf = [IO.Path]::GetFileName($path)

    if ($path -eq 'Horizun.addin' -or $path -eq 'manifest.json') { return 'Apache-2.0' }

    if ($path.StartsWith('server/', [StringComparison]::OrdinalIgnoreCase)) {
        if ($path.StartsWith('server/client-tools/', [StringComparison]::OrdinalIgnoreCase) -or
            $leaf.StartsWith('horizun-mcp', [StringComparison]::OrdinalIgnoreCase)) {
            return 'Apache-2.0'
        }
        if ($leaf -eq 'Newtonsoft.Json.dll') { return 'MIT' }
        # `dotnet publish --self-contained` redistributes the .NET runtime beside
        # the application. The runtime, apphost, native host and reference facade
        # files in this directory come from dotnet/runtime under MIT.
        return 'MIT'
    }

    if ($path.StartsWith('plugin/', [StringComparison]::OrdinalIgnoreCase)) {
        if ($path -match '^plugin/[^/]+/Horizun\.Revit\.dll$' -or
            $path -match '^plugin/[^/]+/(Resources|Recipes)/') {
            return 'Apache-2.0'
        }
        if ($path -match '^plugin/[^/]+/lib/') {
            return 'PSF-2.0-or-file-header'
        }
        if ($thirdPartyDllLicences.ContainsKey($leaf)) {
            return $thirdPartyDllLicences[$leaf]
        }
    }

    return $null
}

$files = @(Get-ChildItem $stage -Recurse -File | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'the staged payload contains no files to inventory' }

# The server redistributes a complete .NET runtime. A bag of hashed DLL paths is
# not enough for vulnerability tooling: identify the actual NuGet runtime-pack
# component from the published deps graph and bind it to the pinned project value.
$depsPath = Join-Path $stage 'server\horizun-mcp.deps.json'
if (-not (Test-Path $depsPath -PathType Leaf)) { throw 'staged server has no horizun-mcp.deps.json' }
$deps = Get-Content $depsPath -Raw | ConvertFrom-Json
$runtimeName = 'Microsoft.NETCore.App.Runtime.win-x64'
$runtimePrefix = "runtimepack.$runtimeName/"
$runtimeEntries = @($deps.libraries.PSObject.Properties |
    Where-Object { $_.Name.StartsWith($runtimePrefix, [StringComparison]::Ordinal) })
if ($runtimeEntries.Count -ne 1) {
    throw "staged deps must identify exactly one $runtimeName runtime pack; found $($runtimeEntries.Count)"
}
$runtimeVersion = $runtimeEntries[0].Name.Substring($runtimePrefix.Length)
$serverProject = [xml](Get-Content (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj'))
# SelectNodes, for the reason spelled out in pack.ps1: the PowerShell XML adapter
# hands back the XmlElement rather than its text once the element carries an
# attribute, and RuntimeFrameworkVersion carries Condition. The old form compared
# the staged runtime against the literal string "System.Xml.XmlElement".
$pinnedRuntimeVersion = [string]($serverProject.SelectNodes('//RuntimeFrameworkVersion') |
    ForEach-Object { $_.InnerText.Trim() } |
    Where-Object { $_ } | Select-Object -First 1)
if ($runtimeVersion -ne $pinnedRuntimeVersion) {
    throw "staged runtime $runtimeVersion does not match pinned RuntimeFrameworkVersion $pinnedRuntimeVersion"
}
$runtimePurl = "pkg:nuget/$runtimeName@$runtimeVersion"
$runtimeRef = $runtimePurl

$components = @()
$unknown = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($stage.Length + 1).Replace('\','/')
    $licence = Get-StagedLicence $relative
    if (-not $licence) { $unknown += $relative; continue }
    $sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $licenceChoice = if ($licence -eq 'PSF-2.0-or-file-header') {
        @{ name = 'PSF License Agreement, or the more specific permissive licence in the file header' }
    } else {
        @{ id = $licence }
    }
    $components += [ordered]@{
        type     = 'file'
        # bom-ref identifies the COMPONENT, not merely its bytes. The same
        # runtime/stdlib file is intentionally present in several Revit-year
        # payloads; a hash-only ref collapsed 3,520 files into 868 identities
        # and violated CycloneDX's uniqueness requirement. The hash remains the
        # integrity evidence below, while the staged path is the unique identity.
        'bom-ref'= "horizun:file:$relative"
        name     = $relative
        hashes   = @(@{ alg = 'SHA-256'; content = $sha })
        licenses = @(@{ license = $licenceChoice })
        properties = @(
            @{ name = 'horizun:bytes'; value = [string]$file.Length },
            @{ name = 'horizun:origin'; value = $(if ($relative.StartsWith('server/') -and $licence -eq 'MIT' -and $relative -notlike '*/Newtonsoft.Json.dll') { 'dotnet/runtime self-contained publish' } elseif ($licence -eq 'Apache-2.0') { 'Horizun source tree' } else { 'redistributed third-party dependency' }) }
        )
    }
}

if ($unknown.Count -gt 0) {
    throw ("No licence classification for {0} staged file(s): {1}" -f $unknown.Count, (($unknown | Select-Object -First 12) -join ', '))
}

$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props has no Version' }
$applicationRef = "pkg:generic/horizun-revit-mcp@$version"

$runtimeComponent = [ordered]@{
    type      = 'framework'
    'bom-ref' = $runtimeRef
    name      = $runtimeName
    version   = $runtimeVersion
    purl      = $runtimePurl
    supplier  = @{ name = 'Microsoft Corporation' }
    licenses  = @(@{ license = @{ id = 'MIT' } })
    properties = @(
        @{ name = 'horizun:distribution'; value = 'redistributed self-contained runtime pack' },
        @{ name = 'horizun:evidence'; value = 'server/horizun-mcp.deps.json' }
    )
}

$sbom = [ordered]@{
    bomFormat   = 'CycloneDX'
    specVersion= '1.6'
    serialNumber = 'urn:uuid:' + [guid]::NewGuid().ToString()
    version     = 1
    metadata    = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        tools = @{ components = @(@{ type='application'; name='scripts/sbom.ps1'; version='1' }) }
        component = @{
            type='application'; 'bom-ref'=$applicationRef; name='Horizun Revit MCP'; version=$version
            licenses=@(@{ license=@{ id='Apache-2.0' } })
        }
        properties = @(
            @{ name='horizun:inventory-source'; value='dist/stage' },
            @{ name='horizun:not-redistributed'; value='Autodesk RevitAPI.dll and RevitAPIUI.dll' }
        )
    }
    components = @($runtimeComponent) + @($components)
    dependencies = @(
        @{ ref = $applicationRef; dependsOn = @($runtimeRef) },
        @{ ref = $runtimeRef; dependsOn = @() }
    )
}

$outDir = Split-Path -Parent $OutFile
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
$sbom | ConvertTo-Json -Depth 10 | Out-File $OutFile -Encoding utf8
Write-Host ("[sbom] CycloneDX 1.6: {0} files + {1} {2}, every staged byte inventoried -> {3}" -f `
    $components.Count, $runtimeName, $runtimeVersion, $OutFile) -ForegroundColor Green
