#Requires -Version 7.0
<#
  Prepared-fixture probes for backlog phase 3B. No Revit launch, close, document
  activation, fixture reset or client configuration change. The owner prepares
  a disposable document and supplies its measured expectations; see the matrix.
  A missing probe is fixture_missing and makes this run fail, never a pass.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$Fixture,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$cases = @(Get-Content -LiteralPath $Fixture -Raw | ConvertFrom-Json -AsHashtable)
$required = @('12-section','12-rotated-plan','14-hosts','15-whole-set','16-category',
              '17-stale-child-plan','18-cad-options','19-relinquish','20-slab-position',
              '31-profile-extension','31-profile-translation','33-worksets','50-crosshatch',
              '51-cad-commit-load','52-prior-isolate','52-large-view')

# Validate the entire matrix before any call. Expectations must come from the
# fixture's ground truth, not be copied from the reply being tested.
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($case in $cases) {
    foreach ($field in 'id','tool','arguments','write','assertions') {
        if (-not $case.ContainsKey($field)) { throw "fixture case lacks $field" }
    }
    if (-not $seen.Add([string]$case.id)) { throw "duplicate probe id: $($case.id)" }
    if ($required -notcontains $case.id) { throw "unknown probe id: $($case.id)" }
    if ($case.arguments.target_document -ne $Document) { throw "probe $($case.id) targets another document" }
    if (@($case.assertions).Count -eq 0) { throw "probe $($case.id) has no measured expectation" }
    foreach ($assertion in $case.assertions) {
        if (-not $assertion.ContainsKey('path') -or -not $assertion.ContainsKey('equals')) {
            throw "probe $($case.id) needs path and equals on every assertion"
        }
    }
}
$run = New-HzRun -Harness $PSCommandPath -Name 'backlog-3b' -Document $Document
$health = Get-HzHealth $run
if ((Get-HzPath $health @('active_document','title')) -ne $Document) {
    throw 'the prepared disposable document is not active; no probe was run'
}
foreach ($case in $cases) {
    if ($case.write) {
        $reply = Invoke-HzWrite -Run $run -Tool $case.tool -Arguments $case.arguments -Label $case.id -AllowRefusal
    } else {
        $reply = Invoke-HzTool -Run $run -Tool $case.tool -Arguments $case.arguments -Label $case.id
    }
    # Paths start at the call wrapper: Result.* on reads, Dry.Result.* or
    # Apply.Result.* on writes; Ok may be asserted false for refusal probes.
    $ok = $true
    $observed = @()
    foreach ($assertion in $case.assertions) {
        $value = Get-HzPath $reply ([string[]]$assertion.path.Split('.'))
        $actual = ConvertTo-Json -InputObject $value -Depth 40 -Compress
        $expected = ConvertTo-Json -InputObject $assertion.equals -Depth 40 -Compress
        if ($actual -cne $expected) { $ok = $false }
        $observed += "$($assertion.path)=$actual (expected $expected)"
    }
    Add-HzProbe -Run $run -Id $case.id -Name $case.id -Expected ($case.assertions | ConvertTo-Json -Depth 40 -Compress) `
        -Observed ($observed -join '; ') -Ok $ok -Evidence $reply
}
$missing = @($required | Where-Object { -not $seen.Contains($_) })
foreach ($id in $missing) {
    Add-HzProbe -Run $run -Id $id -Name $id -Expected 'prepared fixture and measured assertions' `
        -Status fixture_missing -Because 'pending live: this fixture was not supplied'
}
$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
if ($missing.Count -gt 0) { exit 1 }
exit $end.ExitCode
