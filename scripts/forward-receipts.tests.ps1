#Requires -Version 5.1
# Isolated DPAPI fixture + mocked HTTP. Never contacts a telemetry endpoint.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hz-receipts-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
Add-Type -AssemblyName System.Security
$connection = 'InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://example.invalid/'
$cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($connection), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
@{schema='horizun.application-insights/1';enabled=$true;connection_string_dpapi_base64=[Convert]::ToBase64String($cipher)} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'application-insights.json') -Encoding UTF8
$receiptFile = Join-Path $testRoot 'receipts-test.jsonl'
1..101 | ForEach-Object {
    @{tool='horizun_query_model';operation_id="fixture-$_";outcome='ok';total_ms=$_;waited_ms=2;
      utc='2026-09-07T00:00:00Z';document='MUST_NOT_LEAVE';success='legacy-wrong';duration_ms=99999} |
        ConvertTo-Json -Compress
} | Set-Content -LiteralPath $receiptFile -Encoding UTF8
$capture = @{ Batches = [Collections.Generic.List[object]]::new(); Partial = $false }
function Invoke-RestMethod {
    param($Method,$Uri,$ContentType,$Body,$TimeoutSec)
    if ($Uri -ne 'https://example.invalid/v2/track') { throw 'Unexpected endpoint.' }
    $parsed = ConvertFrom-Json -InputObject $Body
    $batch = @($parsed)
    $capture.Batches.Add($batch)
    if ($capture.Partial) { return @{itemsReceived=$batch.Count;itemsAccepted=0;errors=@(@{index=0;statusCode=429})} }
    return @{itemsReceived=$batch.Count;itemsAccepted=$batch.Count;errors=@()}
}
function Check($condition, [string]$message) { if (-not $condition) { throw $message } }
try {
    & (Join-Path $PSScriptRoot 'forward-receipts-to-application-insights.ps1') -StateRoot $testRoot -ReceiptRoot $testRoot
    Check ($capture.Batches.Count -eq 2 -and $capture.Batches[0].Count -eq 100 -and $capture.Batches[1].Count -eq 1) 'Expected bounded batches 100+1.'
    $fact = $capture.Batches[0][0].data.baseData
    Check ($fact.properties.outcome -eq 'ok' -and $fact.measurements.total_ms -eq 1 -and $fact.measurements.waited_ms -eq 2) 'Receipt measurements were not mapped.'
    Check ($null -eq $fact.properties.success -and $null -eq $fact.measurements.duration_ms) 'Legacy nonexistent receipt fields must not be emitted.'
    Check (($capture.Batches | ConvertTo-Json -Depth 12) -notmatch 'MUST_NOT_LEAVE') 'Model data escaped the allowlist.'
    $cursorPath = Join-Path $testRoot 'application-insights-cursor.json'
    $before = Get-Content -LiteralPath $cursorPath -Raw
    Check ((($before | ConvertFrom-Json).sent_hashes).Count -eq 101) 'Acknowledged hashes missing.'
    # Same retained data must not be resent.
    & (Join-Path $PSScriptRoot 'forward-receipts-to-application-insights.ps1') -StateRoot $testRoot -ReceiptRoot $testRoot
    Check ($capture.Batches.Count -eq 2) 'Receipt replay was not deduplicated.'
    '{"tool":"horizun_query_model","operation_id":"partial","outcome":"failed","total_ms":7,"utc":"2026-09-07T00:00:00Z"}' |
        Add-Content -LiteralPath $receiptFile -Encoding UTF8
    $capture.Partial = $true
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'forward-receipts-to-application-insights.ps1') -StateRoot $testRoot -ReceiptRoot $testRoot }
    catch { $refused = $true }
    Check $refused 'Partial ingestion must not be acknowledged.'
    Check ((Get-Content -LiteralPath $cursorPath -Raw) -eq $before) 'Partial response advanced cursor.'
    Write-Host 'PASS: mapping, privacy, bounded batching, replay and partial acknowledgement.'
} finally {
    # Exact, freshly-created fixture; no model or user receipt lives under it.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('hz-receipts-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
