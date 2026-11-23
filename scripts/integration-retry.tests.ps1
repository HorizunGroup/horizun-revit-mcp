#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'integration-status.lib.ps1')
$path = Join-Path ([IO.Path]::GetTempPath()) ('hz-retry-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    Set-HorizunIntegrationState -StatusPath $path -Client 'claude-desktop' -State failed -Detail 'Access denied' | Out-Null
    Set-HorizunIntegrationState -StatusPath $path -Client 'claude-desktop' -State configured -Detail 'retry succeeded' | Out-Null
    $entry = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).integrations.'claude-desktop'
    if ($entry.state -ne 'configured' -or $entry.detail -ne 'retry succeeded' -or $entry.previous_attempt.state -ne 'failed' -or $entry.previous_attempt.detail -ne 'Access denied') { throw 'Retry did not replace current state and retain its prior failure.' }
    Write-Host 'Integration retry passed.'
} finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
