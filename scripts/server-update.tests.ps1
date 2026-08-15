#Requires -Version 5.1
[CmdletBinding()]
param([switch]$RequireWindows)
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    if ($RequireWindows) { throw 'server update process-scoping test requires Windows' }
    Write-Host '[SKIP] server update process-scoping test requires Windows'
    exit 0
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-server-update-' + [guid]::NewGuid().ToString('N'))
$a = Join-Path $root 'installed\horizun-mcp.exe'
$b = Join-Path $root 'unrelated\horizun-mcp.exe'
$processA = $null
$processB = $null
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $a), (Split-Path -Parent $b) -Force | Out-Null
    Copy-Item "$env:SystemRoot\System32\ping.exe" $a -Force
    Copy-Item "$env:SystemRoot\System32\ping.exe" $b -Force
    $processA = Start-Process -FilePath $a -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden -PassThru
    $processB = Start-Process -FilePath $b -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'stop-installed-server.ps1') `
        -ServerPath $a -WaitSeconds 10
    if ($LASTEXITCODE -ne 0) { throw "stop helper failed ($LASTEXITCODE)" }
    $processA.Refresh()
    $processB.Refresh()
    if (-not $processA.HasExited) { throw 'the exact installed target was not stopped' }
    if ($processB.HasExited) { throw 'an unrelated same-name executable was stopped' }
    Write-Host '[PASS] updater stops only the exact installed server path'
}
finally {
    foreach ($process in $processA, $processB) {
        if ($process) {
            try { $process.Refresh(); if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } } catch { }
            $process.Dispose()
        }
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
