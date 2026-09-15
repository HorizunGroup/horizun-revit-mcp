#Requires -Version 5.1
<# Exercise real package export, metadata and refusal paths without Revit. #>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$generator = Join-Path $PSScriptRoot 'generate-mcp-manifest.ps1'
$prepare = Join-Path $PSScriptRoot 'prepare-release-metadata.ps1'
$notesBuilder = Join-Path $PSScriptRoot 'build-release-notes.ps1'
$checks = 0
function Assert-Metadata([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Refused([scriptblock]$Action, [string]$Reason) {
    $caught = $null
    try { & $Action } catch { $caught = $_.Exception.Message }
    Assert-Metadata ($null -ne $caught -and $caught -match $Reason) "Expected refusal '$Reason', received '$caught'."
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $tempBase ('horizun-release-metadata-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
. (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')

function Write-FixturePackage([string]$Path, $Manifest, [switch]$Duplicate) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $count = if ($Duplicate) { 2 } else { 1 }
        for ($i = 0; $i -lt $count; $i++) {
            $entry = $zip.CreateEntry('manifest.json')
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($Manifest | ConvertTo-Json -Depth 20)) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}

try {
    $name = "horizun-revit-$version.mcpb"
    $stage = Join-Path $root "dist/stage/server/integrations/claude-desktop/$name"
    # Use the real builder, but never launch a server or install anything.
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-mcpb.ps1') -Output $stage -ServerPath unused -NoToolList
    Assert-Metadata ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $stage)) 'The real extension builder failed.'
    $beforeHash = (Get-FileHash -LiteralPath $stage).Hash.ToLowerInvariant()
    & $prepare -Dist (Join-Path $root 'dist')
    $exported = Join-Path $root "dist/$name"
    $metadataPath = Join-Path $root 'dist/server.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Metadata ((Get-FileHash -LiteralPath $exported).Hash.ToLowerInvariant() -ceq $beforeHash) 'Export rebuilt or changed the staged package.'
    Assert-Metadata ($metadata.version -ceq $version -and @($metadata.packages).Count -eq 1) 'Release identity differs from the product version.'
    Assert-Metadata ($metadata.packages[0].registryType -ceq 'mcpb' -and $metadata.packages[0].transport.type -ceq 'stdio') 'Incorrect registry package type or transport.'
    Assert-Metadata ($metadata.packages[0].identifier -ceq "https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v$version/$name") 'Registry URL is not the exact tagged asset.'
    Assert-Metadata ($metadata.packages[0].fileSha256 -ceq $beforeHash) 'Registry hash does not identify the exported bytes.'
    Assert-Metadata ($metadata.description -match 'Windows installer before') 'Registry metadata hides the external installation prerequisite.'
    $bytes = [IO.File]::ReadAllBytes($metadataPath)
    Assert-Metadata (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) 'The registry JSON contains a UTF-8 BOM.'

    # Separate processes catch hashtable ordering or platform-dependent output
    # hidden by generating and checking inside a single PowerShell session.
    & pwsh -NoProfile -File $generator -OutFile $metadataPath -PackagePath $exported -RequirePackage -Check
    Assert-Metadata ($LASTEXITCODE -eq 0) 'Metadata is not stable across PowerShell processes.'
    $noProfileRunner = Join-Path $root 'without-userprofile.ps1'
    @'
param([string]$Generator, [string]$Metadata, [string]$Package)
$ErrorActionPreference = 'Stop'
Remove-Item Env:USERPROFILE -ErrorAction SilentlyContinue
& $Generator -OutFile $Metadata -PackagePath $Package -RequirePackage -Check
'@ | Set-Content -LiteralPath $noProfileRunner -Encoding UTF8
    & pwsh -NoProfile -File $noProfileRunner -Generator $generator -Metadata $metadataPath -Package $exported
    Assert-Metadata ($LASTEXITCODE -eq 0) 'Published-package validation depends on the runner USERPROFILE.'

    $notes = Get-Content -LiteralPath (Join-Path $root 'dist/release-notes.md') -Raw
    Assert-Metadata ($notes.Contains("horizun-mcp-$version-setup.exe") -and $notes.Contains("/blob/v$version/docs/CLIENTS.md")) 'Release notes do not identify this release and its versioned guide.'
    Assert-Metadata ($notes.Contains('Run the Windows') -and $notes.Contains('Settings > Extensions') -and $notes.Contains('not an independent')) 'Release notes omit installation prerequisites, remaining steps or evidence scope.'
    Assert-Metadata (-not $notes.Contains('## Unreleased')) 'Unreleased changes leaked into the released notes.'

    $unused = Join-Path $root 'never-written.json'
    Assert-Refused { & $generator -OutFile $unused -RequirePackage } 'requires an actual'
    Assert-Refused { & $generator -OutFile $unused -PackagePath (Join-Path $root $name) -RequirePackage } 'does not exist|Cannot find path'
    $manifest = New-HorizunMcpbManifest -Version '0.0.0' -Command $script:HorizunMcpbPortableCommand
    $wrongVersion = Join-Path $root "wrong-version/$name"
    Write-FixturePackage $wrongVersion $manifest
    Assert-Refused { & $generator -OutFile $unused -PackagePath $wrongVersion -RequirePackage } 'version differs'
    $manifest.version = $version
    $manifest.server.entry_point = 'C:/custom/horizun-mcp.exe'
    $manifest.server.mcp_config.command = 'C:/custom/horizun-mcp.exe'
    $local = Join-Path $root "local-path/$name"
    Write-FixturePackage $local $manifest
    Assert-Refused { & $generator -OutFile $unused -PackagePath $local -RequirePackage } 'portable installed-server path'
    $manifest = New-HorizunMcpbManifest -Version $version -Command $script:HorizunMcpbPortableCommand
    $duplicate = Join-Path $root "duplicate/$name"
    Write-FixturePackage $duplicate $manifest -Duplicate
    Assert-Refused { & $generator -OutFile $unused -PackagePath $duplicate -RequirePackage } 'exactly one root'
    Assert-Metadata (-not (Test-Path -LiteralPath $unused)) 'A refused package wrote registry metadata.'

    # A changed archive is structurally valid but no longer matches the release.
    $recordHash = (Get-FileHash -LiteralPath $metadataPath).Hash
    $zip = [IO.Compression.ZipFile]::Open($exported, [IO.Compression.ZipArchiveMode]::Update)
    try { $null = $zip.CreateEntry('changed-after-packaging.txt') } finally { $zip.Dispose() }
    Assert-Refused { & $generator -OutFile $metadataPath -PackagePath $exported -RequirePackage -Check } 'metadata is stale'
    Assert-Metadata ((Get-FileHash -LiteralPath $metadataPath).Hash -ceq $recordHash) '-Check silently rewrote stale metadata.'
    Assert-Refused { & $prepare -Dist (Join-Path $root 'missing-stage') } 'Missing staged extension'

    $identity = Join-Path $root 'identity.json'
    & $generator -OutFile $identity
    $sourceIdentity = Get-Content -LiteralPath $identity -Raw | ConvertFrom-Json
    Assert-Metadata ($sourceIdentity.version -ceq $version -and -not $sourceIdentity.packages) 'Source identity must not invent a downloadable artifact.'
    [IO.File]::WriteAllText($identity, '{}')
    Assert-Refused { & $generator -OutFile $identity -Check } 'metadata is stale'

    # Fail closed if someone tags a version with no release notes. Copy only
    # this generator and its data inputs; the real checkout remains untouched.
    $fixtureRepo = Join-Path $root 'missing-changelog'
    New-Item -ItemType Directory -Path (Join-Path $fixtureRepo 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath $notesBuilder -Destination (Join-Path $fixtureRepo 'scripts/build-release-notes.ps1')
    Copy-Item -LiteralPath (Join-Path $repo 'Directory.Build.props') -Destination $fixtureRepo
    [IO.File]::WriteAllText((Join-Path $fixtureRepo 'CHANGELOG.md'), "# Changes`n`n## Unreleased`nPending.")
    Assert-Refused { & (Join-Path $fixtureRepo 'scripts/build-release-notes.ps1') -OutFile (Join-Path $root 'no-notes.md') } 'needs a release section'
    Write-Host "release metadata: PASS ($checks checks)"
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'horizun-release-metadata-*') { throw "Unsafe test cleanup target: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
