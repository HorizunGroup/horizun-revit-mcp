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
$verifyLive = if ($env:HORIZUN_VERIFY_LIVE_OVERRIDE) { $env:HORIZUN_VERIFY_LIVE_OVERRIDE } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'verify-live.ps1' }
$json = Join-Path $ArtifactDir ("verify-live-{0}.json" -f $yearNumber)
# NAMED, NOT POSITIONAL. Splatting an ARRAY into a script binds '-Name' strings as
# positional VALUES (measured: verify-live received '-Year' as the year). The rest
# of the driver's string is parsed into a hashtable: a token starting with '-' is
# a parameter name; it takes the next token as its value unless that one is also
# a name, in which case it is a switch.
$forward = [ordered]@{ Year = $yearNumber; Server = $server; AllowDevServer = $true; Json = $json }
$tokens = @($Rest | ForEach-Object { [string]$_ })
for ($i = 0; $i -lt $tokens.Count; $i++) {
    $t = $tokens[$i]
    if (-not $t.StartsWith('-')) { Write-Error "Unexpected positional argument '$t'."; exit 2 }
    $name = $t.TrimStart('-').TrimEnd(':')
    if ($i + 1 -lt $tokens.Count -and -not $tokens[$i + 1].StartsWith('-')) { $forward[$name] = $tokens[$i + 1]; $i++ }
    else { $forward[$name] = $true }
}
# THE CLOSED-WORKSET FIXTURE BELONGS TO A YEAR. live-fixtures.json names one title for
# every year (the 2026 file), so Revit 2023 refused it ("saved in Revit 2026") and
# 2027 refused to upgrade it - measured 2026-09-25. The machine's release-runner map
# names one per year; use it unless the caller passed the fixture explicitly.
$runnerMap = Join-Path (Join-Path $env:USERPROFILE '.horizun') 'release-runner-fixtures.json'
if (-not $forward.Contains('ClosedWorksetDocument') -and (Test-Path -LiteralPath $runnerMap)) {
    try {
        $entry = (Get-Content -LiteralPath $runnerMap -Raw | ConvertFrom-Json).years.("$yearNumber")
        if ($entry -and $entry.release_title -and $entry.closed_workset) {
            $forward['ClosedWorksetDocument'] = [string]$entry.release_title
            $forward['ClosedWorksetName'] = [string]$entry.closed_workset
        }
    } catch { Write-Warning "release-runner-fixtures.json could not be read: $($_.Exception.Message)" }
}
& $verifyLive @forward
exit $LASTEXITCODE
