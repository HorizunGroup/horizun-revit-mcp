#Requires -Version 5.1
# Local-only typed verification. No packaging, Git changes or publication.
[CmdletBinding()]
param(
    [ValidateSet(2023,2024,2025,2026,2027)][int[]]$Years=@(2023,2024,2025,2026,2027),
    [string]$Fixtures=(Join-Path $env:USERPROFILE '.horizun\release-runner-fixtures.json'),
    [string]$Manifest=(Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\manifest.json'),
    [string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\optimization')
)
$ErrorActionPreference='Stop'
$null=New-Item -ItemType Directory -Path $OutputDirectory -Force
$failed=@()
foreach($year in ($Years|Select-Object -Unique)) {
    $report=Join-Path $OutputDirectory "optimization-$year.json"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-release-live-gate.ps1') `
        -Year $year -OptimizationOnly -Fixtures $Fixtures -Manifest $Manifest -Json $report
    if($LASTEXITCODE -ne 0) {$failed+=$year}
}
if($failed.Count) {throw "Local verification incomplete for Revit $($failed -join ', '). See $OutputDirectory"}
