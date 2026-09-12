#Requires -Version 5.1
<#
Apply an approved, local Horizun enterprise settings policy safely.

This script is intentionally small in scope: it distributes restrictions the
bridge already enforces. It does not claim to provide central identity, signing
or remote administration, and it refuses a policy that enables Python.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PolicyPath,
    [string]$DataRoot,
    [switch]$Yes,
    [switch]$WhatIfOnly
)
$ErrorActionPreference = 'Stop'

function Resolve-DataRoot {
    if ($DataRoot) { return $DataRoot.Trim() }
    if ($env:HORIZUN_DATA_ROOT) { return $env:HORIZUN_DATA_ROOT.Trim() }
    if ($env:USERPROFILE) { return (Join-Path $env:USERPROFILE '.horizun') }
    if ($env:HOMEDRIVE -and $env:HOMEPATH) { return (Join-Path "$env:HOMEDRIVE$env:HOMEPATH" '.horizun') }
    throw 'Cannot resolve Horizun data root. Pass -DataRoot.'
}
function Read-JsonObject([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label does not exist: $Path" }
    try { $v = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "$Label is not valid JSON: $Path" }
    if ($null -eq $v -or -not ($v -is [psobject])) { throw "$Label must be a JSON object: $Path" }
    return $v
}

$policy = Read-JsonObject $PolicyPath 'Policy'
if ($policy.schema -ne 'horizun.enterprise-settings-policy/1') { throw 'Unsupported or missing policy schema.' }
if ([string]::IsNullOrWhiteSpace([string]$policy.policy_id) -or [string]::IsNullOrWhiteSpace([string]$policy.version)) {
    throw 'policy_id and version are required.'
}
if ($null -eq $policy.settings) { throw 'settings is required.' }
$known = @('permission_profile','mcp_paused','force_read_only_on_workshared','enable_execute_python','allowed_tools','denied_tools')
foreach ($p in $policy.settings.PSObject.Properties) {
    if ($known -notcontains $p.Name) { throw "Policy settings key '$($p.Name)' is not enforceable by this installer." }
}
if ($policy.settings.permission_profile -and @('read_only','safe_write','full_write','unsafe_code') -notcontains [string]$policy.settings.permission_profile) {
    throw 'permission_profile must be read_only, safe_write, full_write or unsafe_code.'
}
if ($policy.settings.enable_execute_python -eq $true) {
    throw 'Enterprise policy may not enable Python. It requires explicit local owner consent.'
}
foreach ($name in @('mcp_paused','force_read_only_on_workshared','enable_execute_python')) {
    $v = $policy.settings.$name
    if ($null -ne $v -and -not ($v -is [bool])) { throw "$name must be boolean." }
}
foreach ($name in @('allowed_tools','denied_tools')) {
    $v = $policy.settings.$name
    if ($null -ne $v -and (($v -is [string]) -or -not ($v -is [System.Collections.IEnumerable]))) { throw "$name must be an array." }
}

$root = Resolve-DataRoot
$settingsPath = Join-Path $root 'settings.json'
Write-Host "Policy: $($policy.policy_id) v$($policy.version)" -ForegroundColor Cyan
Write-Host "Settings: $settingsPath"
if ($WhatIfOnly) { Write-Host 'Validated only; nothing changed (-WhatIfOnly).' -ForegroundColor Cyan; exit 0 }
if (-not $Yes -and (Read-Host "Apply this policy? Type 'yes' to proceed") -ne 'yes') { Write-Host 'Cancelled.'; exit 0 }

$mutex = New-Object Threading.Mutex($false, 'Local\Horizun.Revit.Settings.V1')
$held = $false; $temp = $null
try {
    try { $held = $mutex.WaitOne([TimeSpan]::FromSeconds(15)) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'Timed out waiting for another settings writer.' }
    $settings = [ordered]@{}
    if (Test-Path -LiteralPath $settingsPath) {
        $existing = Read-JsonObject $settingsPath 'Existing settings'
        foreach ($p in $existing.PSObject.Properties) { $settings[$p.Name] = $p.Value }
        $backup = "$settingsPath.enterprise-bak-$(Get-Date -Format yyyyMMdd-HHmmss)-$([guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
        Write-Host "Backed up: $backup" -ForegroundColor DarkGray
    }
    foreach ($p in $policy.settings.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    $settings['enterprise_policy_id'] = [string]$policy.policy_id
    $settings['enterprise_policy_version'] = [string]$policy.version
    $settings['enterprise_policy_applied_at_utc'] = [DateTime]::UtcNow.ToString('o')
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $temp = Join-Path $root ('.settings-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temp, ($settings | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $settingsPath -Force
    $temp = $null
    Write-Host 'Policy applied. The next MCP request uses it.' -ForegroundColor Green
}
finally {
    if ($temp -and (Test-Path -LiteralPath $temp)) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    if ($held) { try { $mutex.ReleaseMutex() } catch {} }
    $mutex.Dispose()
}
