#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Reports,
    [Parameter(Mandatory = $true)][string]$ExpectedCommit,
    [int[]]$Years = @(2023,2024,2025,2026,2027),
    [string]$Json,
    [string]$Markdown
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-matrix.lib.ps1')
$matrix = Get-HorizunLiveMatrix -Reports $Reports -ExpectedCommit $ExpectedCommit -Years $Years
function Write-Report([string]$path, [string]$content) {
    $parent = Split-Path -Parent $path
    if ($parent) { $null = New-Item -ItemType Directory -Force -Path $parent }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($path), $content, [Text.UTF8Encoding]::new($false))
}
if ($Json) { Write-Report $Json ($matrix | ConvertTo-Json -Depth 12) }
if ($Markdown) {
    $lines = @('# Horizun — Live Revit verification', '', "Commit: $ExpectedCommit", '',
        'This reports recorded checks, not coverage of every Revit capability.', '',
        '| Revit | Result | Passed | Failed | Unverified |', '|---|---|---:|---:|---:|')
    foreach ($row in $matrix.versions) {
        $lines += "| $($row.year) | $($row.status) | $($row.passed) | $($row.failed) | $($row.unverified) |"
    }
    $lines += @('', ('| Tool / probe | ' + ($Years -join ' | ') + ' |'), ('|---|' + ('---|' * $Years.Count)))
    foreach ($entry in $matrix.coverage) {
        $label = ("{0}: {1}" -f $entry.tool, $entry.name) -replace '\|','\|' -replace '[\r\n]', ' '
        $states = @($Years | ForEach-Object { $entry.years["$_"] })
        $lines += "| $label | $($states -join ' | ') |"
    }
    $lines += @('', 'Findings:', '')
    foreach ($row in $matrix.versions) {
        foreach ($issue in $row.issues) { $lines += "- Revit $($row.year): $issue" }
    }
    foreach ($issue in $matrix.issues) { $lines += "- $issue" }
    if ($matrix.complete) { $lines += '- All recorded checks passed for the requested commit and versions.' }
    Write-Report $Markdown ($lines -join "`n")
}
$matrix.versions | Format-Table year,status,passed,failed,unverified -AutoSize | Out-Host
if (-not $matrix.complete) { Write-Host 'Live matrix incomplete. See report findings.'; exit 1 }
exit 0
