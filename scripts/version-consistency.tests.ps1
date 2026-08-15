#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$RequireStage,
    [switch]$RequireInstaller
)

$ErrorActionPreference = 'Stop'
if ($RequireInstaller) { $RequireStage = $true }
$repo = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repo 'Directory.Build.props'
$props = [xml](Get-Content -LiteralPath $propsPath)
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "invalid canonical product Version '$version'"
}
$numericVersion = ($version -split '[-+]')[0]
$fileVersion = "$numericVersion.0"

function Get-EffectiveVersion([string]$relativeProject) {
    $project = Join-Path $repo $relativeProject
    $output = @(& dotnet msbuild $project -getProperty:Version -nologo)
    if ($LASTEXITCODE -ne 0) { throw "MSBuild could not evaluate Version for $relativeProject" }
    $text = ($output -join "`n").Trim()
    # One -getProperty prints the scalar directly; multiple properties print a
    # JSON object. Accept both documented MSBuild output shapes.
    if ($text.StartsWith('{')) {
        try { $doc = $text | ConvertFrom-Json }
        catch { throw "MSBuild Version output for $relativeProject was not JSON: $text" }
        return [string]$doc.Properties.Version
    }
    return $text
}

foreach ($project in 'src/Horizun.Server/Horizun.Server.csproj','src/Horizun.Revit/Horizun.Revit.csproj') {
    $effective = Get-EffectiveVersion $project
    if ($effective -ne $version) {
        throw "$project resolves Version '$effective', but the canonical product version is '$version'. A nearer Directory.Build.props may be shadowing the root."
    }
}

$stage = Join-Path $repo 'dist\stage'
if ($RequireStage -and -not (Test-Path (Join-Path $stage 'manifest.json'))) {
    throw 'a staged manifest is required; run scripts/pack.ps1 -SkipInstaller first'
}

if (Test-Path (Join-Path $stage 'manifest.json')) {
    $manifest = Get-Content (Join-Path $stage 'manifest.json') -Raw | ConvertFrom-Json
    if ([string]$manifest.Server.Product -ne $fileVersion) {
        throw "manifest server Product '$($manifest.Server.Product)' is not '$fileVersion'"
    }
    foreach ($plugin in @($manifest.Plugins)) {
        if ([string]$plugin.Product -ne $fileVersion) {
            throw "manifest Revit $($plugin.Year) Product '$($plugin.Product)' is not '$fileVersion'"
        }
    }

    $binaries = @(
        @{ Label='server apphost'; Path=(Join-Path $stage 'server\horizun-mcp.exe'); Managed=$false },
        @{ Label='server assembly'; Path=(Join-Path $stage 'server\horizun-mcp.dll'); Managed=$true }
    )
    foreach ($plugin in @($manifest.Plugins)) {
        $binaries += @{ Label="Revit $($plugin.Year) assembly"; Path=(Join-Path $stage "plugin\$($plugin.Year)\Horizun.Revit.dll"); Managed=$true }
    }
    foreach ($binary in $binaries) {
        if (-not (Test-Path -LiteralPath $binary.Path)) { throw "$($binary.Label) is missing: $($binary.Path)" }
        $item = Get-Item -LiteralPath $binary.Path
        if ($item.VersionInfo.FileVersion -ne $fileVersion) {
            throw "$($binary.Label) FileVersion '$($item.VersionInfo.FileVersion)' is not '$fileVersion'"
        }
        if (-not ([string]$item.VersionInfo.ProductVersion).StartsWith($version, [StringComparison]::Ordinal)) {
            throw "$($binary.Label) ProductVersion '$($item.VersionInfo.ProductVersion)' does not start with '$version'"
        }
        if ($binary.Managed) {
            $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($item.FullName).Version.ToString()
            if ($assemblyVersion -ne $fileVersion) {
                throw "$($binary.Label) AssemblyVersion '$assemblyVersion' is not '$fileVersion'"
            }
        }
    }

    $sbomPath = Join-Path $repo 'dist\sbom.json'
    if ($RequireStage -and -not (Test-Path $sbomPath)) {
        throw 'dist/sbom.json is required with a release stage; run scripts/sbom.ps1 first'
    }
    if (Test-Path $sbomPath) {
        $sbom = Get-Content $sbomPath -Raw | ConvertFrom-Json
        if ([string]$sbom.metadata.component.version -ne $version) {
            throw "SBOM product version '$($sbom.metadata.component.version)' is not '$version'"
        }
    }
}

$setups = @(Get-ChildItem (Join-Path $repo 'dist') -Filter '*setup.exe' -File -ErrorAction SilentlyContinue)
if ($RequireInstaller) {
    if ($setups.Count -ne 1) { throw "expected exactly one installer, found $($setups.Count)" }
    $expectedName = "horizun-mcp-$version-setup.exe"
    if ($setups[0].Name -ne $expectedName) {
        throw "installer is '$($setups[0].Name)', expected '$expectedName'"
    }
}

$scope = if ($RequireInstaller) { 'canonical, effective MSBuild, staged binaries, manifest, SBOM and installer' }
         elseif (Test-Path (Join-Path $stage 'manifest.json')) { 'canonical, effective MSBuild, staged binaries, manifest and any present SBOM' }
         else { 'canonical and effective MSBuild project' }
Write-Host "[PASS] $scope versions agree on $version" -ForegroundColor Green
