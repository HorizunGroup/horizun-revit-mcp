#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutFile)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "invalid product Version '$version'" }

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$json = [ordered]@{
    '$schema'='https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json'
    name='io.github.HorizunGroup/horizun-revit-mcp'
    title='Horizun Revit MCP'
    description='Open-source MCP server for Autodesk Revit: typed BIM edits, families, exports and Power BI.'
    version=$version
    repository=@{ url='https://github.com/HorizunGroup/horizun-revit-mcp'; source='github' }
    websiteUrl='https://horizunhub.com'
} | ConvertTo-Json -Depth 5
# Windows PowerShell 5's `-Encoding utf8` emits a BOM. mcp-publisher treats the
# BOM bytes as JSON content and refuses the file, so write UTF-8 explicitly
# without a BOM. This remains readable by both Windows PowerShell and pwsh.
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "[mcp-registry] generated $OutFile for $version"
