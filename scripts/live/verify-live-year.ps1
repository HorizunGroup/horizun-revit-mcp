#Requires -Version 7
<#
.SYNOPSIS
  Adapter so run-year-matrix.ps1 can drive scripts/verify-live.ps1 for one year.

.DESCRIPTION
  run-year-matrix.ps1 runs harnesses that live in scripts/live and hands each one
  -ArtifactDir; verify-live.ps1 lives in scripts/ and writes its report to -Json.
  This adapter forwards every other argument unchanged, points verify-live at the
  development server the driver built (HORIZUN_SERVER_EXE) and writes the report
  into the driver's per-year artifact folder. It adds no probe and decides nothing.

.EXAMPLE
  & scripts/live/run-year-matrix.ps1 -Years 2026 `
      -PrepareDocument @('2026=C:\hz-live\HZ_WRITE.rvt') `
      -Harness @('verify-live-year.ps1 -Document {title} -WriteProbes -WriteDocument {title} -WriteDocumentDisposable yes-this-model-is-disposable')
#>
[CmdletBinding()]
param(
    # The driver does not substitute placeholders other than {title}; it sets
    # HORIZUN_REVIT_YEAR for the harness shell, so that is the default.
    [string]$Year = $env:HORIZUN_REVIT_YEAR,
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(ValueFromRemainingArguments)][object[]]$Rest
)
$ErrorActionPreference = 'Stop'
$yearNumber = 0
if (-not [int]::TryParse([string]$Year, [ref]$yearNumber)) {
    # An unsubstituted placeholder such as '{year}' falls back to the driver's variable.
    if (-not [int]::TryParse([string]$env:HORIZUN_REVIT_YEAR, [ref]$yearNumber)) { $yearNumber = 0 }
}
if ($yearNumber -lt 2022) {
    Write-Error "No Revit year: pass -Year or run through run-year-matrix.ps1 (HORIZUN_REVIT_YEAR)."
    exit 2
}
$server = $env:HORIZUN_SERVER_EXE
if (-not $server -or -not (Test-Path -LiteralPath $server)) {
    Write-Error 'HORIZUN_SERVER_EXE is not set to an existing server; run this through run-year-matrix.ps1.'
    exit 2
}
$verifyLive = Join-Path (Split-Path -Parent $PSScriptRoot) 'verify-live.ps1'
$json = Join-Path $ArtifactDir ("verify-live-{0}.json" -f $yearNumber)
$forward = @('-Year', $yearNumber, '-Server', $server, '-AllowDevServer', '-Json', $json) + @($Rest)
& $verifyLive @forward
exit $LASTEXITCODE
