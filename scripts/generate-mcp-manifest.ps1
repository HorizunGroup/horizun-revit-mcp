#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutFile,
    # A release package must be read, not represented by a guessed URL/hash.
    [string]$PackagePath,
    [switch]$RequirePackage,
    # Compare without changing the tracked source identity or a release artifact.
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "invalid product Version '$version'" }

$metadata = [ordered]@{
    '$schema'='https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json'
    name='io.github.HorizunGroup/horizun-revit-mcp'
    title='Horizun Revit MCP'
    description='Local Windows MCP for Revit 2023-2027. Run the Windows installer before connecting a client.'
    version=$version
    repository=[ordered]@{ url='https://github.com/HorizunGroup/horizun-revit-mcp'; source='github' }
    websiteUrl='https://horizunhub.com'
}
if ($RequirePackage -and -not $PackagePath) { throw 'Release metadata requires an actual -PackagePath.' }
if ($PackagePath) {
    $file = Get-Item -LiteralPath $PackagePath -ErrorAction Stop
    if ($file.PSIsContainer -or $file.Name -cne "horizun-revit-$version.mcpb") {
        throw "Expected the published extension horizun-revit-$version.mcpb."
    }
    if ($file.Length -le 0 -or $file.Length -gt 20MB) { throw 'Extension size is outside the metadata validation limit.' }
    . (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')
    $package = Get-HorizunMcpbManifestFromPackage -Path $file.FullName
    if (@($package.Entries | Where-Object { $_ -ceq 'manifest.json' }).Count -ne 1) {
        throw 'Extension must have exactly one root manifest.json.'
    }
    $problems = @(Test-HorizunMcpbManifest -Manifest $package.Manifest -Distribution Published)
    if ($problems.Count) { throw "Invalid public extension: $($problems -join '; ')" }
    if ($package.Manifest.server.mcp_config.command -cne $script:HorizunMcpbPortableCommand) {
        throw 'The release extension must point to the portable installed-server path.'
    }
    if ($package.Manifest.version -cne $version) { throw 'Extension version differs from the source product version.' }
    $metadata.packages = @([ordered]@{
        registryType = 'mcpb'
        identifier = "https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v$version/$($file.Name)"
        fileSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        transport = [ordered]@{ type = 'stdio' }
    })
}
$json = ($metadata | ConvertTo-Json -Depth 8) + "`n"
if ($Check) {
    # PowerShell 5.1 and 7 format JSON differently. Compare parsed content in
    # this process so indentation alone cannot invalidate a release artifact.
    $actual = $null
    if (Test-Path -LiteralPath $OutFile -PathType Leaf) {
        $actual = [IO.File]::ReadAllText([IO.Path]::GetFullPath($OutFile)) | ConvertFrom-Json | ConvertTo-Json -Depth 8 -Compress
    }
    $expected = $metadata | ConvertTo-Json -Depth 8 -Compress
    if ($actual -cne $expected) {
        throw "Registry metadata is stale: regenerate $OutFile with the same package options."
    }
    Write-Host "[mcp-registry] checked $OutFile for $version"
    return
}
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
# Windows PowerShell 5's `-Encoding utf8` emits a BOM. mcp-publisher treats the
# BOM bytes as JSON content and refuses the file, so write UTF-8 explicitly
# without a BOM. This remains readable by both Windows PowerShell and pwsh.
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "[mcp-registry] generated $OutFile for $version"
