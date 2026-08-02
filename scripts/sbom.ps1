#Requires -Version 5.1
<#
  Produce an inventory of what is ACTUALLY shipped, from the staged payload.

  Deliberately not generated from the project file. A csproj lists what is
  referenced; a reference with Private=false ships nothing, and a transitive
  dependency nobody declared ships anyway. The obligations - and the attack
  surface - follow the files on the user's disk, so that is what is read here.

  Run pack.ps1 -SkipInstaller first; this describes dist/stage.

  Usage: pwsh scripts/sbom.ps1
#>
[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo 'dist\stage'
if (-not (Test-Path $stage)) { throw "Nothing staged. Run: pwsh scripts/pack.ps1 -SkipInstaller" }
if (-not $OutFile) { $OutFile = Join-Path $repo 'dist\sbom.json' }

# Licences of the components we redistribute. Stated here rather than guessed per
# run: package metadata does not reliably carry an SPDX id, and inventing one is
# worse than naming it once, in the open, where it can be corrected.
$licences = @{
    'Newtonsoft.Json'                = 'MIT'
    'IronPython'                     = 'Apache-2.0'
    'IronPython.Modules'             = 'Apache-2.0'
    'IronPython.SQLite'              = 'Apache-2.0'
    'IronPython.Wpf'                 = 'Apache-2.0'
    'Microsoft.Dynamic'              = 'Apache-2.0'
    'Microsoft.Scripting'            = 'Apache-2.0'
    'Microsoft.Scripting.Metadata'   = 'Apache-2.0'
    'Mono.Unix'                      = 'MIT'
    'System.CodeDom'                 = 'MIT'
    'System.Text.Encoding.CodePages' = 'MIT'
    # These four ship ONLY with the net48 payloads (Revit 2023-2024). .NET
    # Framework does not carry them in the box, so NuGet copies them in as
    # compatibility shims; .NET 8 and .NET 10 provide them and they do not appear
    # in those payloads at all. All from dotnet/runtime, MIT.
    'System.Buffers'                 = 'MIT'
    'System.Memory'                  = 'MIT'
    'System.Numerics.Vectors'        = 'MIT'
    'System.Runtime.CompilerServices.Unsafe' = 'MIT'
    'Horizun.Revit'                  = 'Proprietary (Horizun Group)'
    'horizun-mcp'                    = 'Proprietary (Horizun Group)'
}

function Describe($file, $component) {
    $name = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    $lic  = $licences[$name]
    [pscustomobject]@{
        component = $component
        file      = $file.Name
        version   = $file.VersionInfo.FileVersion
        licence   = if ($lic) { $lic } else { 'UNKNOWN - identify before shipping' }
        sha256    = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower()
        bytes     = $file.Length
    }
}

$items = @()
$unknown = 0

foreach ($dir in Get-ChildItem (Join-Path $stage 'plugin') -Directory -ErrorAction SilentlyContinue) {
    foreach ($dll in Get-ChildItem $dir.FullName -Filter *.dll -File) {
        $row = Describe $dll ("revit-add-in " + $dir.Name)
        if ($row.licence -like 'UNKNOWN*') { $unknown++ }
        $items += $row
    }
    $py = @(Get-ChildItem $dir.FullName -Recurse -Filter *.py -File)
    if ($py.Count -gt 0) {
        $items += [pscustomobject]@{
            component = "revit-add-in " + $dir.Name
            file      = "lib\ ($($py.Count) .py files)"
            version   = 'IronPython 3.4.2 standard library'
            licence   = 'PSF License Agreement (some modules MIT/BSD, see their headers)'
            sha256    = ''
            bytes     = ($py | Measure-Object Length -Sum).Sum
        }
    }
}

foreach ($dll in Get-ChildItem (Join-Path $stage 'server') -Filter *.dll -File -ErrorAction SilentlyContinue) {
    $row = Describe $dll 'mcp-server'
    if ($row.licence -like 'UNKNOWN*') { $unknown++ }
    $items += $row
}

if ($items.Count -eq 0) { throw 'the staged payload contains no files to inventory' }

$sbom = [pscustomobject]@{
    product      = 'Horizun MCP'
    generated    = (Get-Date).ToUniversalTime().ToString('o')
    generated_by = 'scripts/sbom.ps1, from dist/stage - the files that actually ship'
    note         = 'A technical inventory, not a legal opinion. See THIRD-PARTY-NOTICES.md.'
    not_redistributed = @('Autodesk RevitAPI.dll and RevitAPIUI.dll - referenced with Private=false, never copied')
    components   = $items
}
$sbom | ConvertTo-Json -Depth 6 | Out-File $OutFile -Encoding utf8

Write-Host ("[sbom] {0} components -> {1}" -f $items.Count, $OutFile) -ForegroundColor Cyan
if ($unknown -gt 0) {
    # Not a warning to scroll past: an unidentified binary in the payload is an
    # unknown obligation and an unknown provenance.
    throw "$unknown component(s) have no known licence. Identify them and add them to the table in this script before shipping."
}
Write-Host '[sbom] every redistributed component has a named licence' -ForegroundColor Green
