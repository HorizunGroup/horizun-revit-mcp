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
      -Harness @('verify-live-year.ps1 -Year 2026 -Document {title} -WriteProbes -WriteDocument {title} -WriteDocumentDisposable yes-this-model-is-disposable')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Year,
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(ValueFromRemainingArguments)][object[]]$Rest
)
$ErrorActionPreference = 'Stop'
$server = $env:HORIZUN_SERVER_EXE
if (-not $server -or -not (Test-Path -LiteralPath $server)) {
    Write-Error 'HORIZUN_SERVER_EXE is not set to an existing server; run this through run-year-matrix.ps1.'
    exit 2
}
$verifyLive = Join-Path (Split-Path -Parent $PSScriptRoot) 'verify-live.ps1'
$json = Join-Path $ArtifactDir ("verify-live-{0}.json" -f $Year)
$forward = @('-Year', $Year, '-Server', $server, '-AllowDevServer', '-Json', $json) + @($Rest)
& $verifyLive @forward
exit $LASTEXITCODE
