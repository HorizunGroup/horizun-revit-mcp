#Requires -Version 5.1
<#
  THE FIVE YEARS AS ONE MATRIX.

  verify-structure-matrix.ps1 can run every year in one invocation, and on a
  machine where Revit starts unattended that is what it should do. On THIS
  machine Revit 2023 raises a modal at every start - "Revit cannot run the
  external application Insights", nothing to do with this bridge - and it holds
  the UI thread, so the bridge never answers and the year reads as one that
  never came up. Somebody has to press Close.

  So the five years were run as five invocations with -UseRunning, each against
  a Revit that had already been started and cleared. This reads those artifacts
  back and produces the single matrix, keeping the LAST run of each year and
  recording which artifact each row came from.

  It asserts nothing that the rows do not say. In particular one_build is
  derived from the commits the years actually reported, not from an intention:
  a matrix spread over two builds measures two products.
#>
[CmdletBinding()]
param([string]$ArtifactDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
if (-not $ArtifactDir) { $ArtifactDir = Join-Path $repo 'artifacts\live' }

$files = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter 'structure-matrix-*.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike '*-consolidated-*' } | Sort-Object LastWriteTime)
if ($files.Count -eq 0) { throw 'no per-year matrix artifacts to consolidate.' }

# LAST RUN WINS, per year. A year re-run after a fix is the measurement that
# stands; keeping an earlier failure alongside it would report a product that
# no longer exists.
$rows = [ordered]@{}
$sources = [ordered]@{}
foreach ($f in $files) {
    $d = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    foreach ($r in @($d.rows)) {
        $rows[[string]$r.year] = $r
        $sources[[string]$r.year] = $f.Name
    }
}

$years = @($rows.Keys | Sort-Object)
$green = @($years | Where-Object { $rows[$_].state -eq 'passed' })
$commits = @($years | ForEach-Object { [string]$rows[$_].commit } |
    Where-Object { $_ } | Sort-Object -Unique)

$total = [ordered]@{ passed = 0; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0 }
foreach ($y in $years) {
    foreach ($k in @('rebar', 'geometry', 'performance')) {
        $s = $rows[$y].$k
        if (-not $s) { continue }
        foreach ($n in @($total.Keys)) {
            if ($s.PSObject.Properties.Name -contains $n) { $total[$n] += [int]$s.$n }
        }
    }
}

$out = [ordered]@{
    schema = 'horizun.structure-matrix-consolidated/1'
    what_this_is =
        'The structural suites on every installed Revit, consolidated from the per-year runs. A year that ' +
        'could not be measured is a ROW WITH A REASON, never an absence - the point of a matrix is to say ' +
        'which years were measured, and a missing row reads as a pass to anyone skimming.'
    how_it_was_run =
        'Five invocations of verify-structure-matrix.ps1, one per year, with -UseRunning against a Revit ' +
        'already started and cleared by hand. Revit 2023 on this machine raises a modal at every start - ' +
        '"Revit cannot run the external application Insights", unrelated to this bridge - which holds the UI ' +
        'thread so the bridge never answers. Somebody has to press Close, so a single unattended invocation ' +
        'over five years cannot complete here.'
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    years_measured = $years
    years_green = $green
    one_build = ($commits.Count -eq 1)
    one_build_means =
        'true when every year that answered reported the SAME commit. A matrix spread over two builds ' +
        'measures two products.'
    commits_seen = $commits
    totals = $total
    source_artifacts = $sources
    rows = @($years | ForEach-Object { $rows[$_] })
}

$path = Join-Path $ArtifactDir ('structure-matrix-consolidated-' + (Get-Date).ToString('yyyyMMddHHmmss') + '.json')
$out | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8

# THE COPY THAT GOES INTO GIT, sanitised the same way live evidence is.
#
# artifacts/ is ignored because the reports carry machine-local paths, so the
# durable record is a summary that names each source artifact and PINS it by
# SHA-256. The full reports stay local run evidence.
$pinned = [ordered]@{}
foreach ($y in $years) {
    $src = Join-Path $ArtifactDir $sources[$y]
    $sha = if (Test-Path -LiteralPath $src) { (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash } else { $null }
    $pinned[$y] = [ordered]@{ artifact = $sources[$y]; sha256 = $sha }
}
$docRows = @()
foreach ($y in $years) {
    $r = $rows[$y]
    $docRows += [ordered]@{
        year = $r.year
        state = $r.state
        why = $r.why
        # BASENAME ONLY. The path a fixture lives at is this machine's business.
        model = $(if ($r.model) { Split-Path -Leaf ([string]$r.model) } else { $null })
        commit = $r.commit
        revit_build = $r.revit_build
        reused_running_revit = $r.reused_running_revit
        rebar = $r.rebar
        geometry = $r.geometry
        performance = $r.performance
    }
}
$doc = [ordered]@{
    schema = $out.schema
    what_this_is = $out.what_this_is
    how_it_was_run = $out.how_it_was_run
    generated_utc = $out.generated_utc
    years_measured = $out.years_measured
    years_green = $out.years_green
    one_build = $out.one_build
    one_build_means = $out.one_build_means
    commits_seen = $out.commits_seen
    totals = $out.totals
    originals =
        'The full per-year matrix reports are NOT stored in Git: artifacts/ is ignored because they carry ' +
        'machine-local paths. They remain local run evidence, and the SHA-256 below pins each one ' +
        'byte-for-byte.'
    source_artifacts = $pinned
    rows = $docRows
}
$docPath = Join-Path $repo 'docs\evidence\structure-matrix.json'
$doc | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $docPath -Encoding UTF8

Write-Host ''
Write-Host '=== the structural matrix, five Revits ==='
foreach ($y in $years) {
    $r = $rows[$y]
    function Cell($s) {
        if (-not $s) { return '-' }
        "{0} {1}/{2}" -f $s.state, $s.passed, ([int]$s.passed + [int]$s.failed)
    }
    Write-Host ("  {0}  {1,-8} rebar {2,-13} geometry {3,-13} performance {4,-13} {5}" -f
        $y, $r.state, (Cell $r.rebar), (Cell $r.geometry), (Cell $r.performance),
        ([string]$r.commit).Substring(0, 12))
}
Write-Host ''
Write-Host ("  {0} of {1} years green; one build: {2} ({3})" -f
    $green.Count, $years.Count, $out.one_build, ($commits -join ', '))
Write-Host ("  totals: {0} passed, {1} failed, {2} unverified, {3} not covered, {4} fixture missing" -f
    $total.passed, $total.failed, $total.unverified, $total.not_covered, $total.fixture_missing)
Write-Host ("  matrix: " + $path)
Write-Host ("  evidence: " + $docPath)

exit $(if ($green.Count -eq $years.Count -and $total.failed -eq 0) { 0 } else { 1 })
