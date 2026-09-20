#Requires -Version 5.1
<# Export the exact staged extension; never rebuild it during publication. #>
[CmdletBinding()]
param([string]$Dist)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Dist) { $Dist = Join-Path $repo 'dist' }
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$name = "horizun-revit-$version.mcpb"
$source = Join-Path $Dist "stage/server/integrations/claude-desktop/$name"
$destination = Join-Path $Dist $name
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing staged extension: $source" }
# Validate before copying, then read back the exported bytes for the final metadata.
& (Join-Path $PSScriptRoot 'generate-mcp-manifest.ps1') -OutFile (Join-Path $Dist 'server.json') -PackagePath $source -RequirePackage
Copy-Item -LiteralPath $source -Destination $destination -Force
if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
    throw 'Exported extension bytes differ from the staged package.'
}
& (Join-Path $PSScriptRoot 'generate-mcp-manifest.ps1') -OutFile (Join-Path $Dist 'server.json') -PackagePath $destination -RequirePackage -Check
& (Join-Path $PSScriptRoot 'build-release-notes.ps1') -OutFile (Join-Path $Dist 'release-notes.md')
