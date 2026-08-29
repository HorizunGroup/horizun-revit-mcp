#Requires -Version 5.1
<#
  THE WHOLE DWG PATH, IN ONE ORDER, AGAINST ONE CANDIDATE.

  Each harness here can be run on its own and says so in its own artifact. This
  runs all of them, in a FIXED order, and writes one roll-up naming the single
  candidate every result belongs to.

  The order is not alphabetical and it is not arbitrary. It goes from the
  narrowest claim to the widest, so that when something fails the first failure
  is the most specific one:

     1  identity      which bytes are we measuring, and is the tree clean
     2  chain         the basic read-plan-apply-verify path
     3  cadlink       linking a drawing through a typed command
     4  architecture  curved walls, floors with holes, rooms, doors, windows, columns
     5  structure     grids, load-bearing walls and slabs, structural columns, beams
     6  mep           pipe and duct, where nothing is readable off the geometry
     7  audit         does the model agree with the drawing, and about what
     8  incremental   a second revision against the first
     9  changes       the whole change vocabulary, one change at a time
    10  planimetry    the drawings the model produces, audited in the model
    11  performance   how it behaves at size, against limits set beforehand
    12  naming        names a drawing cannot supply, from the set that can
    13  rehost        the change no comparison of positions can see
    14  openings      a hole in one floor, and a shaft, which are not the same thing
    15  wall-openings the third kind of hole, measured as the volume the wall loses
    16  separators    a line that divides a room, and the profiler that reads layers
    17  parameters    the values a rule declares, and the one writer that keeps them
    18  multi         two drawings, two storeys, one model
    19  redteam       trying to make it do the wrong thing
    20  the roll-up   every count, and the candidate they all belong to

  redteam stays LAST. It is the widest claim there is - that nothing here can be
  talked into the wrong answer - and a suite that ran it early would report the
  narrow failures underneath it as attacks that succeeded.

  A harness that fails does NOT stop the run. Every step is attempted
  and the roll-up reports every one, because "the first thing broke so nothing
  else was measured" is the least useful report available.

    pwsh -File scripts/live/verify-dwg-all.ps1
    ... -ArtifactDir <dir>       where the roll-up and each artifact are written
    ... -Only chain,audit        a subset, in the same order
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

$order = @(
    @{ step = 2;  name = 'chain';        harness = 'verify-dwg-chain' }
    @{ step = 3;  name = 'cadlink';      harness = 'verify-dwg-cadlink' }
    @{ step = 4;  name = 'architecture'; harness = 'verify-dwg-architecture' }
    @{ step = 5;  name = 'structure';    harness = 'verify-dwg-structure' }
    @{ step = 6;  name = 'mep';          harness = 'verify-dwg-mep' }
    @{ step = 7;  name = 'audit';        harness = 'verify-dwg-audit' }
    @{ step = 8;  name = 'incremental';  harness = 'verify-dwg-incremental' }
    @{ step = 9;  name = 'changes';      harness = 'verify-dwg-changes' }
    @{ step = 10; name = 'planimetry';   harness = 'verify-dwg-planimetry' }
    @{ step = 11; name = 'performance';  harness = 'verify-dwg-performance' }
    @{ step = 12; name = 'naming';       harness = 'verify-dwg-naming' }
    @{ step = 13; name = 'rehost';       harness = 'verify-dwg-rehost' }
    @{ step = 14; name = 'openings';     harness = 'verify-dwg-openings' }
    @{ step = 15; name = 'wall-openings'; harness = 'verify-dwg-wall-openings' }
    @{ step = 16; name = 'separators';   harness = 'verify-dwg-separators' }
    @{ step = 17; name = 'parameters';   harness = 'verify-dwg-parameters' }
    @{ step = 18; name = 'multi';        harness = 'verify-dwg-multi' }
    @{ step = 19; name = 'redteam';      harness = 'verify-dwg-redteam' }
)
if ($Only) { $order = @($order | Where-Object { $Only -contains $_.name }) }

if (-not $ArtifactDir) {
    $ArtifactDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts\live'
}
if (-not (Test-Path -LiteralPath $ArtifactDir)) {
    $null = New-Item -ItemType Directory -Path $ArtifactDir -Force
}

# ---------------------------------------------------------- 1. WHOSE BYTES
#
# Everything below is a claim about one build. If this cannot be established the
# run stops here, because thirteen green harnesses against an unknown binary are
# thirteen results nobody can reproduce.
Write-Host "`n=== 1  identity ===" -ForegroundColor Cyan
$identityRun = New-HzRun -Harness $PSCommandPath -Name 'dwg-all-identity' -Document $Document
$manifest = Get-HzManifest -Run $identityRun
$candidate = [string]$manifest.code_candidate_commit
$serverSha = [string]$manifest.server_sha256
if (-not $candidate) { throw 'the bridge did not name the commit it was built from; nothing below would be reproducible' }
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

    $before = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter ('dwg-' + $entry.name + '-*.json') `
                -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $script -Document $Document -ArtifactDir $ArtifactDir
    $code = $LASTEXITCODE
    $clock.Stop()

    # THE ARTIFACT THIS RUN WROTE, not the newest one that happens to be there.
    $after = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter ('dwg-' + $entry.name + '-*.json') `
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

# ------------------------------------------------------------ 20. THE ROLL-UP
Write-Host "`n=== 20  the roll-up ===" -ForegroundColor Cyan

$totals = [ordered]@{
    passed = 0; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0
}
$otherCandidates = @()
foreach ($r in $results) {
    foreach ($k in @($totals.Keys)) { if ($null -ne $r[$k]) { $totals[$k] = $totals[$k] + [int]$r[$k] } }
    if ($r.candidate -and $r.candidate -ne $candidate) { $otherCandidates += $r.candidate }
}
$stepsFailed = @($results | Where-Object { $_.state -ne 'passed' })

# EVERY RESULT MUST BELONG TO THE SAME BUILD. A roll-up that adds up numbers
# from two candidates is a number nobody can act on.
$coherent = $otherCandidates.Count -eq 0

$rollUp = [ordered]@{
    schema = 'horizun.live-rollup/1'
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
    built_from_clean_tree = $manifest.built_from_clean_tree
    steps_run = $results.Count
    steps_failed = $stepsFailed.Count
    all_results_same_candidate = $coherent
    other_candidates_seen = $otherCandidates
    totals = $totals
    steps = $results
}
$rollUpPath = Join-Path $ArtifactDir ('dwg-all-' + $identityRun.RunId + '.json')
($rollUp | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $rollUpPath -Encoding UTF8

foreach ($r in $results) {
    $mark = if ($r.state -eq 'passed') { 'PASS' } else { 'FAIL' }
    Write-Host ("  {0,-5} {1,2}  {2,-14} {3,4} passed  {4,2} failed  {5,2} unverified  {6,2} fixture-missing" -f
        $mark, $r.step, $r.name, $r.passed, $r.failed, $r.unverified, $r.fixture_missing)
}
Write-Host ""
Write-Host ("  {0} probes passed, {1} failed, {2} unverified, {3} not covered, {4} fixture missing" -f
    $totals.passed, $totals.failed, $totals.unverified, $totals.not_covered, $totals.fixture_missing)
Write-Host ("  candidate {0}, all results from one build: {1}" -f $candidate.Substring(0, 12), $coherent)
Write-Host ("  roll-up: {0}" -f $rollUpPath)

if ($stepsFailed.Count -gt 0 -or -not $coherent) { exit 1 }
exit 0
