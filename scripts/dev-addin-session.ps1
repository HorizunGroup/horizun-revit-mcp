#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet(2022,2023,2024,2025,2026,2027)][int]$Year,
    [switch]$Enable,
    [switch]$Restore,
    [switch]$Unsigned,
    [string]$DevRoot = (Join-Path $env:USERPROFILE '.horizun\geometry-dev'),
    [string]$StageManifest
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
if ($Enable -eq $Restore) { throw 'Specify exactly one of Enable or Restore.' }
$repo = Split-Path -Parent $PSScriptRoot
$root = [IO.Path]::GetFullPath($DevRoot)
$statePath = Join-Path $root "session-$Year.json"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
$installed = Join-Path $addins 'Horizun.addin'
$aside = Join-Path $root "original-$Year.addin"
$manifest = Join-Path $addins 'Horizun-geometry-dev.addin'
foreach ($process in @(Get-Process Revit -ErrorAction SilentlyContinue)) {
    try { $processPath = $process.Path } catch { throw 'Cannot determine the year of a running Revit; no manifests changed.' }
    if (-not $processPath -or $processPath -like "*\Revit $Year\*") { throw "Close Revit $Year before changing its manifest (PID $($process.Id))." }
}
if ($Restore) {
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'No session ledger; refusing to guess what to restore.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.restored) { Write-Output 'Already restored.'; exit 0 }
    if ((Test-Path -LiteralPath $installed) -or -not (Test-Path -LiteralPath $aside)) { throw 'Unexpected installed/aside state; no files changed.' }
    if ((Get-FileHash -LiteralPath $aside).Hash -ne $state.installed_manifest_sha256) { throw 'Original manifest hash changed.' }
    if (Test-Path -LiteralPath $manifest) {
        if ((Get-FileHash -LiteralPath $manifest).Hash -ne $state.dev_manifest_sha256) { throw 'Development manifest was changed by another process.' }
        Remove-Item -LiteralPath $manifest
    }
    Move-Item -LiteralPath $aside -Destination $installed
    if ((Get-FileHash -LiteralPath $installed).Hash -ne $state.installed_manifest_sha256) { throw 'Restoration hash mismatch.' }
    $state.restored = $true
    $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Output "Restored original manifest for Revit $Year; installed binaries were never replaced."
    exit 0
}
if ((Test-Path -LiteralPath $manifest) -or (Test-Path -LiteralPath $aside)) { throw 'A development session already exists; restore it first.' }
if (-not (Test-Path -LiteralPath $installed)) { throw 'Expected installed Horizun.addin is absent; refusing to guess deployment identity.' }
if ($StageManifest) {
    $prepared = Get-Content -LiteralPath $StageManifest -Raw | ConvertFrom-Json
    if ($prepared.enabled -or [int]$prepared.year -ne $Year) { throw 'Stage must be disabled and match the requested Revit year.' }
    $stageAddin = $prepared.stage
    $stageServer = Join-Path $prepared.stage 'server'
    $stageDll = Join-Path $stageAddin 'Horizun.Revit.dll'
    $stageExe = Join-Path $stageServer 'horizun-mcp.exe'
    $stageServerDll = Join-Path $stageServer 'horizun-mcp.dll'
    if ($prepared.server_dll_sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Prepared stage must identify the managed server DLL; nothing was enabled.' }
    if ((Get-FileHash -LiteralPath $stageDll -Algorithm SHA256).Hash -ne $prepared.addin_sha256 -or
        (Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash -ne $prepared.server_sha256 -or
        (Get-FileHash -LiteralPath $stageServerDll -Algorithm SHA256).Hash -ne $prepared.server_dll_sha256) { throw 'Prepared stage hash mismatch; nothing was enabled.' }
} else {
dotnet build (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c Release --nologo -v:q
if ($LASTEXITCODE) { throw 'Matching server build failed.' }
dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') -c Release "-p:RevitYear=$Year" --nologo -v:q
if ($LASTEXITCODE) { throw 'Add-in build failed.' }
$stageAddin = Join-Path $repo 'src\Horizun.Revit\bin\Release'
$stageServer = Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0'
}
$stage = Join-Path $root ("$Year-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $stageAddin | Where-Object { $_.Name -notin @('server', 'stage.json') } | Copy-Item -Destination $stage -Recurse
$serverStage = Join-Path $stage 'server'
New-Item -ItemType Directory -Path $serverStage | Out-Null
Copy-Item -Path (Join-Path $stageServer '*') -Destination $serverStage -Recurse
$serverExe = Join-Path $serverStage 'horizun-mcp.exe'
$dll = Join-Path $stage 'Horizun.Revit.dll'
if ($StageManifest -and ((Get-FileHash -LiteralPath $dll).Hash -ne $prepared.addin_sha256 -or
    (Get-FileHash -LiteralPath $serverExe).Hash -ne $prepared.server_sha256 -or
    (Get-FileHash -LiteralPath (Join-Path $serverStage 'horizun-mcp.dll')).Hash -ne $prepared.server_dll_sha256)) { throw 'Copied stage hash mismatch; nothing was enabled.' }
if ($Unsigned) {
    foreach ($binary in @($dll, $serverExe, (Join-Path $serverStage 'horizun-mcp.dll'))) {
        if ((Get-AuthenticodeSignature -LiteralPath $binary).Status -ne 'NotSigned') {
            throw "Unsigned session requires an unsigned binary: $binary. Nothing was enabled."
        }
    }
    Write-Warning 'Unsigned development binaries; Revit may show a security dialog.'
} else {
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq 'CN=Horizun Group (self-signed add-in signing)' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
    (Test-Path -LiteralPath "Cert:\CurrentUser\TrustedPublisher\$($_.Thumbprint)") -and (Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($_.Thumbprint)")
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert) {
    $signature = Set-AuthenticodeSignature -FilePath $dll -Certificate $cert -HashAlgorithm SHA256
    if ($signature.Status -ne 'Valid') { throw "Development signature failed: $($signature.Status)." }
} else { Write-Warning 'No existing trusted certificate. Revit may require its owner to approve loading this build.' }
}
$assemblyPath = [Security.SecurityElement]::Escape($dll)
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>Horizun geometry development</Name>
<Assembly>$assemblyPath</Assembly><AddInId>b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30</AddInId>
<FullClassName>Horizun.Revit.App</FullClassName><VendorId>HRZN</VendorId><VendorDescription>Horizun Group</VendorDescription>
</AddIn></RevitAddIns>
"@
$stagedManifest = Join-Path $stage 'manifest.xml'
[IO.File]::WriteAllText($stagedManifest, $xml, [Text.UTF8Encoding]::new($false))
$state = [ordered]@{
    year=$Year; stage=$stage; restored=$false
    installed_manifest_sha256=(Get-FileHash -LiteralPath $installed).Hash
    dev_manifest_sha256=(Get-FileHash -LiteralPath $stagedManifest).Hash
    dev_addin_sha256=(Get-FileHash -LiteralPath $dll).Hash
    server_exe=$serverExe; server_sha256=(Get-FileHash -LiteralPath $serverExe).Hash
    server_dll_sha256=(Get-FileHash -LiteralPath (Join-Path $serverStage 'horizun-mcp.dll')).Hash
}
$state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
try {
    Move-Item -LiteralPath $installed -Destination $aside
    Copy-Item -LiteralPath $stagedManifest -Destination $manifest
    if ((Get-FileHash -LiteralPath $manifest).Hash -ne $state.dev_manifest_sha256) { throw 'Development manifest verification failed.' }
} catch {
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
    if ((Test-Path -LiteralPath $aside) -and -not (Test-Path -LiteralPath $installed)) { Move-Item -LiteralPath $aside -Destination $installed }
    throw
}
Write-Output "Development session enabled for $Year. Ledger: $statePath. Restore after closing that Revit."
Write-Output "Matching server (pass -Server or HORIZUN_SERVER_EXE): $serverExe"
