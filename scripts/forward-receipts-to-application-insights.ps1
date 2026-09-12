#Requires -Version 5.1
<#!
  Forward only allowlisted local operation facts to an administrator-configured
  Azure Application Insights resource. No endpoint is contacted unless the
  DPAPI-protected configuration exists. Failed sends do not remove local data.
#>
[CmdletBinding()]
param(
    [switch]$WhatIfOnly,
    [string]$StateRoot,
    [string]$ReceiptRoot
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
if (-not $StateRoot) { $StateRoot = Join-Path $env:LOCALAPPDATA 'Horizun\enterprise' }
if (-not $ReceiptRoot) { $ReceiptRoot = Join-Path $env:USERPROFILE '.horizun\receipts' }
$configPath = Join-Path $StateRoot 'application-insights.json'
$cursorPath = Join-Path $StateRoot 'application-insights-cursor.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { Write-Host 'Application Insights forwarding is not configured.'; exit 0 }

function Get-ConnectionValue([string]$Text, [string]$Name) {
    foreach ($part in $Text.Split(';')) {
        $pair = $part.Split('=', 2)
        if ($pair.Count -eq 2 -and $pair[0].Trim().Equals($Name, [StringComparison]::OrdinalIgnoreCase)) { return $pair[1].Trim() }
    }
    return $null
}
function Read-Json([string]$Path, [object]$Fallback) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $Fallback }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { throw "Could not read ${Path}: $($_.Exception.Message)" }
}

$config = Read-Json $configPath $null
if ($config.schema -ne 'horizun.application-insights/1' -or -not $config.enabled) { throw 'Application Insights configuration is invalid or disabled.' }
$protected = [Convert]::FromBase64String([string]$config.connection_string_dpapi_base64)
$connection = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
$iKey = Get-ConnectionValue $connection 'InstrumentationKey'
if ($iKey -notmatch '^[0-9A-Fa-f-]{36}$') { throw 'Configured InstrumentationKey is invalid.' }
$endpoint = Get-ConnectionValue $connection 'IngestionEndpoint'
if (-not $endpoint) { $endpoint = 'https://dc.services.visualstudio.com/' }
$endpoint = $endpoint.TrimEnd('/') + '/v2/track'
if (-not ([Uri]$endpoint).Scheme.Equals('https', [StringComparison]::OrdinalIgnoreCase)) { throw 'Configured ingestion endpoint is not HTTPS.' }

$cursor = Read-Json $cursorPath ([pscustomobject]@{ schema = 'horizun.application-insights-cursor/1'; sent_hashes = @() })
if (-not $cursor.sent_hashes) { $cursor | Add-Member -Force -NotePropertyName sent_hashes -NotePropertyValue @() }
$sent = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($prior in @($cursor.sent_hashes)) { [void]$sent.Add([string]$prior) }
$events = New-Object Collections.Generic.List[object]
$retained = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @(Get-ChildItem -LiteralPath $ReceiptRoot -Filter 'receipts-*.jsonl' -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $hash = [BitConverter]::ToString(([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($line)))).Replace('-', '').ToLowerInvariant()
        if (-not $retained.Add($hash)) { continue }
        if ($sent.Contains($hash)) { continue }
        try { $receipt = $line | ConvertFrom-Json } catch { continue }
        $properties = [ordered]@{ tool = [string]$receipt.tool; outcome = [string]$receipt.outcome; dry_run = [string]$receipt.dry_run; transaction_status = [string]$receipt.transaction_status; all_verified = [string]$receipt.all_verified; receipt_hash = $hash }
        $measurements = [ordered]@{}
        foreach ($metric in @('waited_ms','total_ms')) {
            $value = $receipt.$metric
            if ($null -ne $value -and $value -is [ValueType]) {
                $number = [double]$value
                if (-not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0) { $measurements[$metric] = $number }
            }
        }
        $events.Add([ordered]@{ name = "Microsoft.ApplicationInsights.$iKey.Event"; time = [string]$receipt.utc; iKey = $iKey; tags = @{ 'ai.cloud.role' = 'horizun-revit-mcp' }; data = @{ baseType = 'EventData'; baseData = @{ ver = 2; name = 'horizun_operation_receipt'; properties = $properties; measurements = $measurements } } })
    }
}
if ($events.Count -eq 0) { Write-Host 'No new receipt facts to forward.'; exit 0 }
if ($WhatIfOnly) { Write-Host "[WhatIf] Would send $($events.Count) sanitized receipt event(s) to the configured HTTPS endpoint."; exit 0 }

$accepted = 0
for ($offset = 0; $offset -lt $events.Count; $offset += 100) {
    $last = [Math]::Min($offset + 99, $events.Count - 1)
    $batch = @($events[$offset..$last])
    $payload = ConvertTo-Json -InputObject $batch -Depth 8 -Compress
    try { $response = Invoke-RestMethod -Method Post -Uri $endpoint -ContentType 'application/json' -Body $payload -TimeoutSec 30 }
    catch { throw "Forwarding stopped after $accepted accepted events. Unacknowledged receipts remain retryable: $($_.Exception.Message)" }
    # A 206 can arrive without throwing. Never acknowledge a partially accepted
    # batch as complete; retry is at-least-once and receipt_hash enables dedup.
    if ($null -eq $response.itemsAccepted -or $response.itemsAccepted -ne $batch.Count -or
        $response.itemsReceived -ne $batch.Count -or @($response.errors).Count -gt 0) {
        throw "Ingestion did not acknowledge the complete batch. Cursor unchanged for this batch; retries may duplicate accepted receipt_hash values."
    }
    foreach ($event in $batch) { [void]$sent.Add([string]$event.data.baseData.properties.receipt_hash) }
    $accepted += $batch.Count
    $cursor.schema = 'horizun.application-insights-cursor/1'
    # Prune against retained receipt files, not arbitrary HashSet enumeration.
    # Otherwise old retained receipts become 'new' every time the 10k cap rolls.
    $cursor | Add-Member -Force -NotePropertyName sent_hashes -NotePropertyValue @($sent | Where-Object { $retained.Contains($_) } | Sort-Object)
    $temp = $cursorPath + '.tmp-' + [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temp, ($cursor | ConvertTo-Json -Depth 4 -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $cursorPath -Force
}
Write-Host "Forwarded $accepted sanitized receipt event(s)."
