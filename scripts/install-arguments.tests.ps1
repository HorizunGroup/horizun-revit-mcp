#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
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
