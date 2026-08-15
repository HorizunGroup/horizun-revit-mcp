#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'install-release.ps1')

$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; if ($detail) { Write-Host "        $detail" }; $script:failed++ }
}
function Refuses($name, [scriptblock]$write, $pattern) {
    $path = Join-Path $root ($name.Replace(' ', '-') + '.txt')
    & $write $path
    $message = $null
    try { Read-HorizunInstallResult $path $start $finish 'v0.9.0' | Out-Null }
    catch { $message = $_.Exception.Message }
    Assert $name ([bool]($message -match $pattern)) $message
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-install-result-' + [guid]::NewGuid().ToString('N'))
$start = Get-Date
$finish = $start.AddSeconds(5)
$stamp = $start.AddSeconds(2).ToString('yyyy-MM-dd HH:mm:ss')
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $good = Join-Path $root 'good.txt'
    @("version=0.9.0","installed_local=$stamp","server_installed=yes","server_failure=",
      'any_revit_found=yes','succeeded=2025, 2026','failed=','fully_installed=yes') | Set-Content $good
    $result = Read-HorizunInstallResult $good $start $finish 'v0.9.0'
    Assert 'a fresh complete result passes' ($result.fully_installed -eq 'yes') $null

    Refuses 'partial Revit deployment is refused' {
        param($p) @("version=0.9.0","installed_local=$stamp","server_installed=yes",'server_failure=',
          'any_revit_found=yes','succeeded=2025','failed=2026 locked','fully_installed=no') | Set-Content $p
    } 'partially'
    Refuses 'a failed server is refused' {
        param($p) @("version=0.9.0","installed_local=$stamp","server_installed=no",'server_failure=copy failed',
          'any_revit_found=yes','succeeded=','failed=','fully_installed=no') | Set-Content $p
    } 'server was not installed'
    Refuses 'a stale result is refused' {
        param($p) @('version=0.9.0','installed_local=2020-01-01 00:00:00','server_installed=yes','server_failure=',
          'any_revit_found=yes','succeeded=2025','failed=','fully_installed=yes') | Set-Content $p
    } 'not from this run'
}
finally { if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue } }

if ($failed -eq 0) { Write-Host 'install-result: ALL PASSED' -ForegroundColor Green; exit 0 }
Write-Host "install-result: $failed FAILED" -ForegroundColor Red; exit 1
