#Requires -Version 5.1
<#
.SYNOPSIS
Builds a local Revit MCP playground bundle for Revit 2023 and Revit 2026.

.DESCRIPTION
Reuses the repository's release staging and MCPB builders. It does not install
anything and does not compile an installer. The result is copied to one portable
output folder with:

  server/                         self-contained win-x64 MCP server
  revit-addins/2023/              Revit 2023 manifest and payload
  revit-addins/2026/              Revit 2026 manifest and payload
  claude-desktop/*.mcpb           machine-resolved Claude Desktop extension
  build-manifest.json             SHA-256 inventory of every generated file
  README.txt                      local test/deployment instructions

The MCPB intentionally points to the server executable in this output folder.
Moving the folder invalidates that absolute path; rerun this script after moving.

.EXAMPLE
pwsh -File scripts/build-playground-artifacts.ps1

.EXAMPLE
pwsh -File scripts/build-playground-artifacts.ps1 `
  -OutputDirectory C:\RevitMcpPlayground\generated
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo 'artifacts\revit-mcp-playground'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $repo 'dist\stage'
$requiredYears = @(2023, 2026)

function Step([string]$Message) {
    Write-Host "[playground-build] $Message" -ForegroundColor Cyan
}

function Assert-LastExitCode([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Assert-BuildPrerequisites {
    $globalJsonPath = Join-Path $repo 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw "global.json is missing: $globalJsonPath"
    }

    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    $requiredSdk = [string]$globalJson.sdk.version
    $installedSdks = @(& dotnet --list-sdks 2>$null | ForEach-Object {
        if ($_ -match '^([^ ]+) ') { $Matches[1] }
    })
    Assert-LastExitCode 'dotnet --list-sdks'
    if ($requiredSdk -notin $installedSdks) {
        throw ("This repository requires .NET SDK $requiredSdk exactly, but it is not installed. " +
               "Install it from https://dotnet.microsoft.com/download and rerun this script. " +
               "Installed SDKs: " + ($installedSdks -join ', '))
    }

    foreach ($year in $requiredYears) {
        $revitApi = "C:\Program Files\Autodesk\Revit $year\RevitAPI.dll"
        $revitApiUi = "C:\Program Files\Autodesk\Revit $year\RevitAPIUI.dll"
        if (-not (Test-Path -LiteralPath $revitApi -PathType Leaf)) {
            throw "Revit $year API not found: $revitApi"
        }
        if (-not (Test-Path -LiteralPath $revitApiUi -PathType Leaf)) {
            throw "Revit $year API UI assembly not found: $revitApiUi"
        }
    }
}

function Copy-RevitPayload([int]$Year, [string]$DestinationRoot) {
    $sourcePayload = Join-Path $stage "plugin\$Year"
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePayload 'Horizun.Revit.dll') -PathType Leaf)) {
        throw "The staged Revit $Year add-in is missing: $sourcePayload"
    }

    $yearRoot = Join-Path $DestinationRoot ([string]$Year)
    $addinPayload = Join-Path $yearRoot 'Horizun'
    New-Item -ItemType Directory -Path $addinPayload -Force | Out-Null
    Copy-Item -Path (Join-Path $sourcePayload '*') -Destination $addinPayload -Recurse -Force

    $manifestSource = Join-Path $repo 'src\Horizun.Revit\Horizun.addin'
    Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $yearRoot 'Horizun.addin') -Force

    $manifestXml = [xml](Get-Content -LiteralPath (Join-Path $yearRoot 'Horizun.addin') -Raw)
    $assemblyPath = [string]$manifestXml.RevitAddIns.AddIn.Assembly
    if ($assemblyPath -ne 'Horizun\Horizun.Revit.dll') {
        throw "Unexpected Assembly path in the Revit add-in manifest: $assemblyPath"
    }
}

function Write-OutputReadme([string]$Path, [string]$McpbName) {
    $text = @"
Revit MCP playground build
===========================

This folder was produced from the repository source. It contains no Setup.exe
and the build script installed nothing.

Contents
--------
server\
  Self-contained Windows x64 MCP server. Claude Desktop starts
  server\horizun-mcp.exe through the generated MCPB.

revit-addins\2023\ and revit-addins\2026\
  Copy each year's Horizun.addin and Horizun folder to:
  %APPDATA%\Autodesk\Revit\Addins\<YEAR>\
  Revit must be closed while replacing an installed add-in.

claude-desktop\$McpbName
  Import once in Claude Desktop:
  Settings > Extensions > Advanced settings > Install Extension.
  The MCPB contains an absolute path to this folder's server\horizun-mcp.exe.
  If this output folder moves, rerun the build before importing the MCPB.

Validation
----------
1. Start one supported Revit version and open a disposable test document.
2. Start/restart Claude Desktop.
3. Call horizun_health and verify the reported Revit year and build identity.
4. Test Revit 2023 and 2026 separately before changing create/write tools.

Safety
------
Use disposable test models. Do not overwrite a production or central model.
The MCPB does not enable horizun_execute_python.
"@
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

Step 'checking exact SDK and Revit 2023/2026 API prerequisites'
Assert-BuildPrerequisites

Step "building the existing release stage without creating Setup.exe ($Configuration)"
& (Join-Path $repo 'scripts\pack.ps1') -Config $Configuration -SkipInstaller
Assert-LastExitCode 'scripts/pack.ps1 -SkipInstaller'

if (-not $NoClean -and (Test-Path -LiteralPath $OutputDirectory)) {
    $repoFull = [IO.Path]::GetFullPath($repo).TrimEnd('\') + '\'
    if (-not $OutputDirectory.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an output folder outside the repository without -NoClean: $OutputDirectory"
    }
    Step "cleaning previous output: $OutputDirectory"
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

$serverOut = Join-Path $OutputDirectory 'server'
$addinOut = Join-Path $OutputDirectory 'revit-addins'
$claudeOut = Join-Path $OutputDirectory 'claude-desktop'
New-Item -ItemType Directory -Path $serverOut, $addinOut, $claudeOut -Force | Out-Null

Step 'copying the self-contained MCP server'
Copy-Item -LiteralPath (Join-Path $stage 'server\*') -Destination $serverOut -Recurse -Force
$serverExe = Join-Path $serverOut 'horizun-mcp.exe'
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    throw "Generated server is missing: $serverExe"
}

foreach ($year in $requiredYears) {
    Step "copying the Revit $year add-in"
    Copy-RevitPayload -Year $year -DestinationRoot $addinOut
}

$versionProps = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
$version = [string]($versionProps.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Directory.Build.props contains no product Version.'
}
$mcpbName = "horizun-revit-playground-$version.mcpb"
$mcpbPath = Join-Path $claudeOut $mcpbName

Step 'building a machine-resolved Claude Desktop MCPB from the generated server'
& (Join-Path $repo 'scripts\build-mcpb.ps1') `
    -Local `
    -ServerPath $serverExe `
    -Output $mcpbPath
Assert-LastExitCode 'scripts/build-mcpb.ps1 -Local'
if (-not (Test-Path -LiteralPath $mcpbPath -PathType Leaf)) {
    throw "Generated MCPB is missing: $mcpbPath"
}

Write-OutputReadme -Path (Join-Path $OutputDirectory 'README.txt') -McpbName $mcpbName

Step 'writing SHA-256 build inventory'
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($OutputDirectory.Length).TrimStart('\').Replace('\', '/')
        Size = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [pscustomobject]@{
    Schema = 'revit-mcp-playground-build/1'
    GeneratedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Configuration = $Configuration
    ProductVersion = $version
    RevitYears = $requiredYears
    Mcpb = "claude-desktop/$mcpbName"
    Server = 'server/horizun-mcp.exe'
    Files = $files
}
$manifestPath = Join-Path $OutputDirectory 'build-manifest.json'
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

# Include the final manifest itself in a detached checksum so its contents do not
# recursively need to hash themselves.
$manifestSha = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    (Join-Path $OutputDirectory 'build-manifest.sha256'),
    "$manifestSha  build-manifest.json`r`n",
    [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host 'Playground artifacts built successfully.' -ForegroundColor Green
Write-Host "Output: $OutputDirectory" -ForegroundColor Green
Write-Host "Server: $serverExe"
Write-Host "Revit 2023: $(Join-Path $addinOut '2023')"
Write-Host "Revit 2026: $(Join-Path $addinOut '2026')"
Write-Host "MCPB: $mcpbPath"
