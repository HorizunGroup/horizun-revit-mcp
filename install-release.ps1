#Requires -Version 5.1
<#
  Download the official Horizun setup from GitHub Releases, verify it against
  SHA256SUMS.txt from the SAME release, then launch it. No Git or .NET SDK.

  Usage:
    powershell -ExecutionPolicy Bypass -File .\install-release.ps1
    powershell -ExecutionPolicy Bypass -File .\install-release.ps1 -Version v0.6.0
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [switch]$KeepDownloadedFiles
)
$ErrorActionPreference = 'Stop'
$repo = 'HorizunGroup/horizun-revit-mcp'
$encodedVersion = [Uri]::EscapeDataString($Version)
$api = if ($Version -eq 'latest') {
    "https://api.github.com/repos/$repo/releases/latest"
} else {
    "https://api.github.com/repos/$repo/releases/tags/$encodedVersion"
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('horizun-release-install-' + [Guid]::NewGuid().ToString('N'))

try {
    if (@(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Revit is running. Close every Revit window and run this installer again. Nothing was downloaded or changed.'
    }
    New-Item -ItemType Directory -Path $temporary | Out-Null
    Write-Host "[Horizun] reading release metadata from $api" -ForegroundColor Cyan
    $headers = @{ 'User-Agent' = 'Horizun-Revit-MCP-Verified-Installer' }
    $release = Invoke-RestMethod -Uri $api -Headers $headers
    if ($release.draft -or $release.prerelease) {
        Write-Host "[Horizun] selected release $($release.tag_name) is marked prerelease/draft." -ForegroundColor Yellow
    }
    $setupAssets = @($release.assets | Where-Object { $_.name -match '^horizun-mcp-.+-setup\.exe$' })
    $sumAssets = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' })
    if ($setupAssets.Count -ne 1) { throw "Release $($release.tag_name) must contain exactly one setup.exe asset; found $($setupAssets.Count)." }
    if ($sumAssets.Count -ne 1) { throw "Release $($release.tag_name) has no unique SHA256SUMS.txt. Refusing an unverified download." }
    foreach ($asset in @($setupAssets[0], $sumAssets[0])) {
        if ([IO.Path]::GetFileName($asset.name) -ne $asset.name) { throw "Release asset name contains a path: $($asset.name)" }
        $assetUri = [Uri]$asset.browser_download_url
        if ($assetUri.Scheme -ne 'https' -or $assetUri.Host -ne 'github.com' -or
            -not $assetUri.AbsolutePath.StartsWith("/$repo/releases/download/", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release asset URL is outside the fixed Horizun GitHub release path: $($asset.browser_download_url)"
        }
    }

    $setupPath = Join-Path $temporary $setupAssets[0].name
    $sumsPath = Join-Path $temporary 'SHA256SUMS.txt'
    Invoke-WebRequest -Uri $setupAssets[0].browser_download_url -Headers $headers -OutFile $setupPath
    Invoke-WebRequest -Uri $sumAssets[0].browser_download_url -Headers $headers -OutFile $sumsPath

    $escapedName = [Regex]::Escape($setupAssets[0].name)
    $matches = @(Get-Content -LiteralPath $sumsPath | Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+\*?$escapedName$" })
    if ($matches.Count -ne 1) { throw "SHA256SUMS.txt does not contain exactly one hash for $($setupAssets[0].name)." }
    [void]($matches[0] -match '^([0-9a-fA-F]{64})')
    $expected = $Matches[1].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA-256 mismatch. Expected $expected but downloaded $actual. The installer was NOT launched."
    }

    Write-Host "[Horizun] verified $($release.tag_name): $actual" -ForegroundColor Green
    $process = Start-Process -FilePath $setupPath -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "The setup exited with code $($process.ExitCode). Review its on-screen report." }
    Write-Host '[Horizun] setup completed successfully.' -ForegroundColor Green
}
finally {
    if ($KeepDownloadedFiles) { Write-Host "Downloaded files kept at $temporary" -ForegroundColor DarkYellow }
    elseif (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
