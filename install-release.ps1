#Requires -Version 5.1
<#
  Download the official Horizun setup from GitHub Releases, verify it against
  SHA256SUMS.txt from the SAME release, then launch it. No Git or .NET SDK.

  Usage:
    irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1 | iex
    powershell -ExecutionPolicy Bypass -File .\install-release.ps1
    powershell -ExecutionPolicy Bypass -File .\install-release.ps1 -Version vX.Y.Z

  The default is a quiet CLI install. It then registers Horizun immediately when
  the selected client is closed, or schedules the registration safely after the
  active client exits. Live verification completes automatically after Revit's
  first start. Pass -Interactive only when the Setup wizard itself is wanted.
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [ValidateSet('Auto', 'Claude', 'Codex', 'Both', 'None')]
    [string]$Client = 'Auto',
    [switch]$KeepDownloadedFiles,
    [switch]$Interactive,
    # Retained for compatibility. Quiet is the 0.9+ default.
    [switch]$Silent,
    [switch]$VerifyOnly,
    [switch]$NoClientCompletion,
    [switch]$NoLiveVerification
)
$ErrorActionPreference = 'Stop'

if ($Interactive -and $Silent) { throw '-Interactive and -Silent are mutually exclusive.' }

# Kept in this file deliberately: the documented bootstrap downloads this ONE
# script to a temp folder. A helper beside the repository copy would not exist
# there, so depending on one makes the no-Git installation fail before download.
function Read-HorizunInstallResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][datetime]$StartedLocal,
        [Parameter(Mandatory=$true)][datetime]$FinishedLocal,
        [string]$ExpectedVersion
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Setup returned but wrote no result for this run at $Path. Exit code 0 alone is not proof of a complete install."
    }
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $at = $line.IndexOf('=')
        if ($at -le 0) { continue }
        $key = $line.Substring(0, $at).Trim()
        if ($values.ContainsKey($key)) { throw "Install result repeats '$key'; refusing an ambiguous report." }
        $values[$key] = $line.Substring($at + 1).Trim()
    }
    foreach ($required in 'version','installed_local','server_installed','any_revit_found','succeeded','failed','fully_installed') {
        if (-not $values.ContainsKey($required)) { throw "Install result is missing '$required'." }
    }
    $stamp = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($values.installed_local, 'yyyy-MM-dd HH:mm:ss',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeLocal, [ref]$stamp)) {
        throw "Install result has an invalid installed_local stamp: '$($values.installed_local)'."
    }
    if ($stamp -lt $StartedLocal.AddSeconds(-2) -or $stamp -gt $FinishedLocal.AddMinutes(1)) {
        throw "Install result is not from this run: stamp $stamp, launch $StartedLocal, return $FinishedLocal."
    }
    if ($ExpectedVersion) {
        $want = $ExpectedVersion.TrimStart('v')
        if ($values.version.TrimStart('v') -ne $want) {
            throw "Install result is for version '$($values.version)', expected '$ExpectedVersion'."
        }
    }
    if ($values.server_installed -ne 'yes') { throw "The MCP server was not installed: $($values.server_failure)" }
    if ($values.any_revit_found -ne 'yes') { throw 'No supported Revit installation was found; the add-in was not installed.' }
    if ($values.fully_installed -ne 'yes' -or -not [string]::IsNullOrWhiteSpace($values.failed)) {
        throw "Setup completed only partially. Succeeded: '$($values.succeeded)'. Failed: '$($values.failed)'."
    }
    if ([string]::IsNullOrWhiteSpace($values.succeeded)) {
        throw 'Setup claimed fully_installed=yes but named no successfully installed Revit year.'
    }
    [pscustomobject]$values
}

# Tests dot-source the standalone bootstrap to exercise the exact parser that
# ships. Dot-sourcing defines functions and performs no network or installation.
if ($MyInvocation.InvocationName -eq '.') { return }
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
    if ($VerifyOnly) {
        Write-Host '[Horizun] verification-only requested; Setup was NOT launched.' -ForegroundColor Green
        return
    }

    $resultPath = Join-Path $temporary 'install-result.txt'
    $installerClient = if ($NoClientCompletion) { 'None' } else { $Client }
    $arguments = @("/HORIZUNRESULT=$resultPath", "/HORIZUNCLIENT=$installerClient", '/NORESTART')
    if ($NoLiveVerification) { $arguments += '/HORIZUNNOLIVE=-NoLiveWait' }
    if (-not $Interactive) {
        $arguments += '/VERYSILENT'
        $arguments += '/SUPPRESSMSGBOXES'
    }
    $startedLocal = Get-Date
    $process = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru
    $finishedLocal = Get-Date
    if ($process.ExitCode -ne 0) { throw "The setup exited with code $($process.ExitCode). Review its on-screen report." }
    $installResult = Read-HorizunInstallResult -Path $resultPath -StartedLocal $startedLocal `
        -FinishedLocal $finishedLocal -ExpectedVersion $release.tag_name
    Write-Host ("[Horizun] setup completed successfully for Revit " + $installResult.succeeded + '.') -ForegroundColor Green

    if (-not $NoClientCompletion -and $Client -ne 'None') {
        $serverRoot = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server'
        $complete = Join-Path $serverRoot 'client-tools\complete-install.ps1'
        if (Test-Path -LiteralPath $complete -PathType Leaf) {
            # The 0.9+ Setup itself launches the same finisher so double-clicking
            # the EXE is as complete as using this bootstrap. Give it a moment to
            # publish fresh durable state; only invoke it here for a package that
            # installed the helper but did not launch it. Two concurrent writers
            # are not a convenience feature.
            $statusPath = Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json'
            $freshCompletion = $false
            for ($attempt = 1; $attempt -le 20; $attempt++) {
                if (Test-Path -LiteralPath $statusPath) {
                    try {
                        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                        $updated = [datetime]::Parse([string]$status.updated_utc).ToUniversalTime()
                        if ($updated -ge $startedLocal.ToUniversalTime().AddSeconds(-2)) { $freshCompletion = $true; break }
                    }
                    catch { }
                }
                Start-Sleep -Milliseconds 250
            }
            $completionExit = 0
            if (-not $freshCompletion) {
                $completionArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $complete, '-Client', $Client)
                if ($NoLiveVerification) { $completionArgs += '-NoLiveWait' }
                & powershell @completionArgs
                $completionExit = $LASTEXITCODE
            }
            if ($completionExit -eq 1) {
                throw 'The release installed, but automatic client completion failed. Review %LOCALAPPDATA%\Horizun\install-status.json.'
            }
            elseif ($completionExit -eq 2) {
                Write-Host '[Horizun] binaries are installed, but no Claude/Codex configuration exists yet.' -ForegroundColor Yellow
                Write-Host '          Start the intended client once, close it, then use the Start-menu shortcut' -ForegroundColor Yellow
                Write-Host '          "Completar y verificar instalación de Horizun".' -ForegroundColor Yellow
            }
            else {
                Write-Host '[Horizun] client registration and first-start health verification are complete or safely scheduled.' -ForegroundColor Green
                Write-Host ("          status: " + $statusPath) -ForegroundColor DarkGray
            }
        }
        else {
            # v0.8 and older releases predate the deferred completion helper. A
            # current bootstrap may legitimately be used to pin one of them; do
            # not claim automation that artifact cannot provide.
            Write-Host "[Horizun] $($release.tag_name) predates automatic client completion." -ForegroundColor Yellow
            Write-Host '          Close the client and register the installed server manually:' -ForegroundColor Yellow
            $serverExe = Join-Path $serverRoot 'horizun-mcp.exe'
            Write-Host "          claude mcp add --scope user horizun-revit -- `"$serverExe`""
            Write-Host "          codex mcp add horizun-revit -- `"$serverExe`""
        }
    }
}
finally {
    if ($KeepDownloadedFiles) { Write-Host "Downloaded files kept at $temporary" -ForegroundColor DarkYellow }
    elseif (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
