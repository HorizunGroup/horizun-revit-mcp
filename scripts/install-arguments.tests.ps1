#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$installSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\install.ps1') -Raw
if ($installSource -match '&\s+\$dotnet\.Source') {
    throw 'install.ps1 invokes a CommandInfo property directly; Windows PowerShell 5.1 cannot load that command.'
}
Write-Host '  PASS  Windows PowerShell invokes the resolved dotnet path, not a CommandInfo property'
if ($installSource -notmatch '(?s)catch\s*\{.*?actualSdk\s*=\s*''''' -or
    $installSource -notmatch 'isolatedDotnet') {
    throw 'install.ps1 does not recover from a global.json SDK-selection error through the isolated SDK.'
}
Write-Host '  PASS  a failing system dotnet host falls through to the exact isolated SDK'
if ($installSource -match 'LASTEXITCODE\s*-eq\s*0\s*-and\s*\$isolatedVersion') {
    throw 'isolated SDK selection still depends on LASTEXITCODE retained from a prior native-host failure.'
}
Write-Host '  PASS  isolated SDK selection trusts the exact returned version, not stale LASTEXITCODE'
if ($installSource -notmatch 'dotnet\s+publish' -or
    $installSource -notmatch '--self-contained\s+true' -or
    $installSource -notmatch "hostfxr\.dll'.*hostpolicy\.dll") {
    throw 'source installation no longer stages a verified self-contained MCP server.'
}
Write-Host '  PASS  source installation publishes and verifies a self-contained MCP server'
. (Join-Path $PSScriptRoot 'install-arguments.lib.ps1')
$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; if ($detail) { Write-Host "        $detail" }; $script:failed++ }
}

$comma = @(ConvertTo-HorizunRevitYears @('2025,2026'))
Assert 'comma-separated -File syntax yields two years' ($comma.Count -eq 2 -and $comma[0] -eq 2025 -and $comma[1] -eq 2026) ($comma -join ',')
$separate = @(ConvertTo-HorizunRevitYears @('2023','2027','2023'))
Assert 'separate values preserve order and remove duplicates' (($separate -join ',') -eq '2023,2027') ($separate -join ',')
$refused = $null
try { ConvertTo-HorizunRevitYears @('20252026') | Out-Null } catch { $refused = $_.Exception.Message }
Assert 'concatenated accidental year is refused' ([bool]($refused -match 'Unsupported')) $refused

if ($failed -eq 0) { Write-Host 'install-arguments: ALL PASSED' -ForegroundColor Green; exit 0 }
Write-Host "install-arguments: $failed FAILED" -ForegroundColor Red; exit 1
