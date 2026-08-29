#Requires -Version 5.1
<#
  THE STRUCTURAL SUITE, IN ONE ORDER, AGAINST ONE CANDIDATE.

  Separate from verify-dwg-all.ps1 on purpose. The two suites answer different
  questions and fail for different reasons, and a single runner that added their
  numbers together would produce a total nobody could act on: "247 of 251" says
  nothing about whether reinforcement works.

  The order is the same principle as the DWG suite - narrowest claim first, so
  the first failure is the most specific one. Reading comes before writing,
  writing before auditing, and anything adversarial goes last.

  WHAT THIS RUNNER REFUSES TO DO. It will not add up results that do not come
  from the same build. Every harness records the commit the RUNNING bridge
  reports, and if two disagree the roll-up says so and exits non-zero, because
  half a suite measured against yesterday's binary is not a suite.

  Exit code 0 when every harness passed and every result belongs to one build.
#>
[CmdletBinding()]
param(
    [string]$Document = 'HZ_WRITE',
    [string]$ArtifactDir,
    [string[]]$Only
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

# The suite. `name` is the artifact prefix AND the New-HzRun name inside the
# harness - that coupling is what makes discovery work, and a mismatch shows up
# as a silently absent artifact rather than an error.
$order = @(
    @{ step = 2; name = 'rebar'; harness = 'verify-rebar' },
    # NOT called 'rebar-geometry': the discovery below globs structure-<name>-*,
    # and a name that is a prefix of another one makes two harnesses fight over
    # the same files.
    @{ step = 3; name = 'geometry'; harness = 'verify-rebar-geometry' },
    # THE SLICE AT SIZE. Budgets declared before anything was measured, and
    # the only harness that applies a rule declaring an array length - which
    # is how the array-length defect in ADR-003 item 11 was found at all.
    @{ step = 4; name = 'performance'; harness = 'verify-rebar-performance' }
)
if ($Only) { $order = @($order | Where-Object { $Only -contains $_.name }) }

if (-not $ArtifactDir) {
    $ArtifactDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts\live'
}
if (-not (Test-Path -LiteralPath $ArtifactDir)) {
    $null = New-Item -ItemType Directory -Path $ArtifactDir -Force
}

# ---------------------------------------------------------- 1. WHOSE BYTES
Write-Host "`n=== 1  identity ===" -ForegroundColor Cyan
$identityRun = New-HzRun -Harness $PSCommandPath -Name 'structure-all-identity' -Document $Document
$manifest = Get-HzManifest -Run $identityRun
$candidate = [string]$manifest.code_candidate_commit
$serverSha = [string]$manifest.server_sha256
if (-not $candidate) {
    throw 'the bridge did not name the commit it was built from; nothing below would be reproducible'
}
Write-Host ("    candidate {0}  server {1}  Revit {2} {3}" -f
    $candidate.Substring(0, 12), $serverSha.Substring(0, 12),
    $manifest.revit_year, $manifest.revit_build)
Write-Host ("    repo HEAD {0}  tracked clean: {1}" -f
    ([string]$manifest.repo_head).Substring(0, 12), $manifest.repo_tracked_clean)

$results = @()
foreach ($entry in $order) {
    Write-Host ("`n=== {0}  {1} ===" -f $entry.step, $entry.name) -ForegroundColor Cyan
    $script = Join-Path $PSScriptRoot ($entry.harness + '.ps1')
    if (-not (Test-Path -LiteralPath $script)) {
        Write-Host ("    MISSING: {0}" -f $script) -ForegroundColor Red
        $results += [ordered]@{ step = $entry.step; name = $entry.name; harness = $entry.harness
                                state = 'missing'; exit_code = $null }
        continue
    }

    $before = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter ('structure-' + $entry.name + '-*.json') `
                -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $script -Document $Document -ArtifactDir $ArtifactDir
    $code = $LASTEXITCODE
    $clock.Stop()

    # THE ARTIFACT THIS RUN WROTE, not the newest one that happens to be there.
    $after = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter ('structure-' + $entry.name + '-*.json') `
               -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.FullName } |
               Sort-Object LastWriteTime -Descending)
    $row = [ordered]@{
        step = $entry.step; name = $entry.name; harness = ('scripts/live/' + $entry.harness + '.ps1')
        exit_code = $code; state = $(if ($code -eq 0) { 'passed' } else { 'failed' })
        duration_ms = [int]$clock.ElapsedMilliseconds
        artifact = $null; passed = $null; failed = $null; unverified = $null
        not_covered = $null; fixture_missing = $null; candidate = $null
    }
    if ($after.Count -gt 0) {
        $doc = Get-Content -LiteralPath $after[0].FullName -Raw | ConvertFrom-Json
        $row.artifact = $after[0].Name
        $row.passed = [int](Get-HzProp $doc 'passed')
        $row.failed = [int](Get-HzProp $doc 'failed')
        $row.unverified = [int](Get-HzProp $doc 'unverified')
        $row.not_covered = [int](Get-HzProp $doc 'not_covered')
        $row.fixture_missing = [int](Get-HzProp $doc 'fixture_missing')
        $row.candidate = [string](Get-HzProp $doc 'code_candidate_commit')
    }
    $results += $row
}

# ------------------------------------------------------------- THE ROLL-UP
Write-Host "`n=== roll-up ===" -ForegroundColor Cyan

$totals = [ordered]@{ passed = 0; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0 }
$otherCandidates = @()
foreach ($r in $results) {
    foreach ($k in @($totals.Keys)) { if ($null -ne $r[$k]) { $totals[$k] = $totals[$k] + [int]$r[$k] } }
    if ($r.candidate -and $r.candidate -ne $candidate) { $otherCandidates += $r.candidate }
}
$stepsFailed = @($results | Where-Object { $_.state -ne 'passed' })
$coherent = $otherCandidates.Count -eq 0

$rollUp = [ordered]@{
    schema = 'horizun.live-rollup/1'
    suite = 'structure'
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    order_means = 'narrowest claim first, so the first failure is the most specific one'
    candidate = $candidate
    server_sha256 = $serverSha
    contract_hash = $manifest.contract_hash
    horizun_version = $manifest.horizun_version
    revit_year = $manifest.revit_year
    revit_build = $manifest.revit_build
    repo_head = $manifest.repo_head
    repo_tracked_clean = $manifest.repo_tracked_clean
    repo_modified_paths = @($manifest.repo_modified_paths)
    built_from_clean_tree = $manifest.built_from_clean_tree
    steps_run = $results.Count
    steps_failed = $stepsFailed.Count
    all_results_same_candidate = $coherent
    other_candidates_seen = $otherCandidates
    totals = $totals
    steps = $results
}
$rollUpPath = Join-Path $ArtifactDir ('structure-all-' + $identityRun.RunId + '.json')
($rollUp | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $rollUpPath -Encoding UTF8

foreach ($r in $results) {
    $mark = if ($r.state -eq 'passed') { 'PASS' } else { 'FAIL' }
    Write-Host ("  {0,-5} {1,2}  {2,-16} {3,4} passed  {4,2} failed  {5,2} unverified  {6,2} fixture-missing" -f
        $mark, $r.step, $r.name, $r.passed, $r.failed, $r.unverified, $r.fixture_missing)
}
Write-Host ""
Write-Host ("  {0} probes passed, {1} failed, {2} unverified, {3} not covered, {4} fixture missing" -f
    $totals.passed, $totals.failed, $totals.unverified, $totals.not_covered, $totals.fixture_missing)
Write-Host ("  candidate {0}, all results from one build: {1}" -f $candidate.Substring(0, 12), $coherent)
Write-Host ("  roll-up: {0}" -f $rollUpPath)

if ($stepsFailed.Count -gt 0 -or -not $coherent) { exit 1 }
exit 0
