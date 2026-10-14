#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OwnerSettings,
    [Parameter(Mandatory=$true)][string]$IsolatedSettings,
    [Parameter(Mandatory=$true)][ValidateSet('Start','Restore')][string]$Mode
)
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'Restore') {
    if (Test-Path -LiteralPath $OwnerSettings -PathType Leaf) {
        Copy-Item -LiteralPath $OwnerSettings -Destination $IsolatedSettings -Force
    }
    elseif (Test-Path -LiteralPath $IsolatedSettings) {
        Remove-Item -LiteralPath $IsolatedSettings -Force
    }
    return
}

# The runner owns this separate data root. The owner's durable file is read only.
$settings = if (Test-Path -LiteralPath $OwnerSettings -PathType Leaf) {
    Get-Content -LiteralPath $OwnerSettings -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
if ($null -eq $settings -or $settings -isnot [pscustomobject]) {
    throw 'Owner settings must be a JSON object.'
}
$settings | Add-Member -NotePropertyName permission_profile -NotePropertyValue 'unsafe_code' -Force
$settings | Add-Member -NotePropertyName enable_execute_python -NotePropertyValue $true -Force
$json = $settings | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($IsolatedSettings, $json, [Text.UTF8Encoding]::new($false))
