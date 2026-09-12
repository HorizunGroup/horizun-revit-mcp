#Requires -Version 5.1
<#!
  Configure OPTIONAL central operational logging for Horizun Revit MCP.

  The connection string is protected with the current user's Windows DPAPI and
  is never written to the repository, command line, receipt, or diagnostic
  output. This script does not send a model, prompt, path, element id, user name
  or tool arguments. The forwarder sends only an allowlisted operation receipt.
#>
[CmdletBinding()]
param(
    [string]$ConnectionString,
    [switch]$Disable,
    [switch]$WhatIfOnly,
    [string]$StateRoot
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

if ($Disable -and $ConnectionString) { throw 'Use either -Disable or -ConnectionString, not both.' }
if (-not $StateRoot) { $StateRoot = Join-Path $env:LOCALAPPDATA 'Horizun\enterprise' }
$configPath = Join-Path $StateRoot 'application-insights.json'

function Get-ConnectionValue([string]$Text, [string]$Name) {
    foreach ($part in $Text.Split(';')) {
        $pair = $part.Split('=', 2)
        if ($pair.Count -eq 2 -and $pair[0].Trim().Equals($Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $pair[1].Trim()
        }
    }
    return $null
}

if ($Disable) {
    if ($WhatIfOnly) { Write-Host "[WhatIf] Would remove $configPath"; exit 0 }
    if (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath -Force }
    Write-Host 'Application Insights forwarding disabled. Local receipts remain untouched.'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'ConnectionString is required. Supply Azure Application Insights ConnectionString securely from your administrator.'
}
$instrumentationKey = Get-ConnectionValue $ConnectionString 'InstrumentationKey'
if ($instrumentationKey -notmatch '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$') {
    throw 'ConnectionString must contain a valid InstrumentationKey.'
}
$ingestionEndpoint = Get-ConnectionValue $ConnectionString 'IngestionEndpoint'
if ($ingestionEndpoint -and -not ([Uri]$ingestionEndpoint).Scheme.Equals('https', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'IngestionEndpoint must use HTTPS.'
}
if ($WhatIfOnly) {
    Write-Host '[WhatIf] Connection string is structurally valid. No configuration was written.'
    exit 0
}

[IO.Directory]::CreateDirectory($StateRoot) | Out-Null
$bytes = [Text.Encoding]::UTF8.GetBytes($ConnectionString)
$protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$record = [ordered]@{
    schema = 'horizun.application-insights/1'
    enabled = $true
    configured_utc = [DateTime]::UtcNow.ToString('o')
    connection_string_dpapi_base64 = [Convert]::ToBase64String($protected)
}
$temp = $configPath + '.tmp-' + [guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temp -Destination $configPath -Force
Write-Host "Application Insights forwarding configured at $configPath. Run forward-receipts-to-application-insights.ps1 from a trusted scheduled task."
