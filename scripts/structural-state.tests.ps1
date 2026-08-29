#Requires -Version 5.1
<#
  THE LEDGER MUST NOT CONTRADICT THE EVIDENCE BESIDE IT.

  docs/STRUCTURAL-PROGRAM-STATE.json emitted, for weeks:

      "multiversion": null,
      "multiversion_means": "null because no other Revit year has been opened
       for this path. Compilation is proved on five years; BEHAVIOUR is proved
       on the year above and no other."

  It went on emitting that sentence after five years HAD been measured and
  docs/evidence/structure-matrix.json had been committed saying so. Two files in
  the same repository, describing the same product, saying opposite things -
  and the ledger is the one people read first.

  It was not a bug in the sense of a crash. It was a hard-coded null with a
  hard-coded explanation, which is exactly the shape of thing that survives
  every test that only checks the generator RUNS. These tests check what it
  SAYS, against artifacts they control.

  The second half of the file is about cleanliness. `rollup_repo_clean: false`
  with nothing beside it tells a reader the tree was dirty and refuses to say
  how, so the only safe reading left is the worst one - that product code was
  uncommitted when the numbers were taken. The classification must survive.
#>
$ErrorActionPreference = 'Stop'

$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $name" -ForegroundColor Red
        if ($detail) { Write-Host "        $detail" }
        $script:failed++
    }
}

$repo = Split-Path -Parent $PSScriptRoot
$generator = Join-Path $PSScriptRoot 'generate-structural-state.ps1'
if (-not (Test-Path -LiteralPath $generator)) { throw 'generate-structural-state.ps1 not found beside this test' }

$CAND = ('a' * 40)
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-struct-state-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

# The real evidence file, moved aside for the duration. These tests write their
# own into its place, because the generator resolves it relative to the repo -
# and a test that reads the live file would pass for the wrong reason.
$evidence = Join-Path $repo 'docs\evidence\structure-matrix.json'
$backup = Join-Path $root 'structure-matrix.real.json'
$hadEvidence = Test-Path -LiteralPath $evidence
if ($hadEvidence) { Copy-Item -LiteralPath $evidence -Destination $backup -Force }
# The public projection intentionally omits private evidence artifacts, so a
# clean hosted checkout may not contain docs/evidence at all. This fixture owns
# the file it writes and therefore owns creating its parent directory too.
New-Item -ItemType Directory -Path (Split-Path -Parent $evidence) -Force | Out-Null

$artifacts = Join-Path $repo 'artifacts\live'
if (-not (Test-Path -LiteralPath $artifacts)) { New-Item -ItemType Directory -Path $artifacts -Force | Out-Null }
$written = New-Object System.Collections.Generic.List[string]

function New-RollUp([hashtable]$Override) {
    $doc = [ordered]@{
        schema = 'horizun.live-rollup/1'
        suite = 'structure'
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        candidate = $CAND
        server_sha256 = ('b' * 64)
        contract_hash = 'feacfcf34a37d82678e662e4'
        horizun_version = '1.1.0-dev'
        revit_year = '2026'
        revit_build = '26.4.0.32'
        repo_head = $CAND
        repo_tracked_clean = $true
        repo_modified_paths = @()
        built_from_clean_tree = $true
        steps_run = 3
        steps_failed = 0
        all_results_same_candidate = $true
        other_candidates_seen = @()
        totals = [ordered]@{ passed = 60; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0 }
        steps = @([ordered]@{
            step = 2; name = 'rebar'; harness = 'scripts/live/verify-rebar.ps1'
            exit_code = 0; state = 'passed'; duration_ms = 1000
            artifact = 'structure-rebar-TEST.json'; passed = 34; failed = 0
            unverified = 0; not_covered = 0; fixture_missing = 0; candidate = $CAND
        })
    }
    foreach ($k in $Override.Keys) { $doc[$k] = $Override[$k] }
    $path = Join-Path $artifacts ('structure-all-TEST-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
    ($doc | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $path -Encoding UTF8
    $written.Add($path)
    $path
}

function Set-Matrix($Doc) {
    if ($null -eq $Doc) {
        if (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Force }
        return
    }
    ($Doc | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $evidence -Encoding UTF8
}

function New-Matrix([hashtable]$Override) {
    $rows = @()
    $srcs = [ordered]@{}
    foreach ($y in 2023, 2024, 2025, 2026, 2027) {
        $rows += [ordered]@{
            year = $y; state = 'passed'; why = $null; model = "HZ$y.rvt"
            commit = $CAND; revit_build = "$y.0"; reused_running_revit = $true
            rebar = [ordered]@{ state = 'passed'; passed = 34; failed = 0; unverified = 0
                                not_covered = 0; fixture_missing = 0; artifact = "r-$y.json" }
            geometry = [ordered]@{ state = 'passed'; passed = 16; failed = 0; unverified = 0
                                   not_covered = 0; fixture_missing = 0; artifact = "g-$y.json" }
            performance = [ordered]@{ state = 'passed'; passed = 10; failed = 0; unverified = 0
                                      not_covered = 0; fixture_missing = 0; artifact = "p-$y.json" }
        }
        $srcs["$y"] = [ordered]@{ artifact = "structure-matrix-$y.json"; sha256 = ('c' * 64) }
    }
    $doc = [ordered]@{
        schema = 'horizun.structure-matrix-consolidated/1'
        what_this_is = 'test matrix'
        how_it_was_run = 'THE INSIGHTS MODAL SENTENCE LIVES HERE and must reach the ledger from this file.'
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        years_measured = @('2023', '2024', '2025', '2026', '2027')
        years_green = @('2023', '2024', '2025', '2026', '2027')
        one_build = $true
        one_build_means = 'test'
        commits_seen = @($CAND)
        totals = [ordered]@{ passed = 300; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0 }
        source_artifacts = $srcs
        rows = $rows
    }
    foreach ($k in $Override.Keys) { $doc[$k] = $Override[$k] }
    $doc
}

function Invoke-Generator([string]$RollUp, [string]$OutName) {
    $out = Join-Path $root $OutName
    $log = & pwsh -NoProfile -File $generator -RollUp $RollUp -Out $out -SkipTests 2>&1 | Out-String
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Log = $log
        Doc = $(if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } else { $null })
        Raw = $(if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw } else { '' })
    }
}

try {
    Write-Host ''
    Write-Host '== the multiversion block comes from the artifact ==' -ForegroundColor Cyan

    Set-Matrix (New-Matrix @{})
    $r = Invoke-Generator (New-RollUp @{}) 'withmatrix.json'
    $mv = $r.Doc.live.multiversion

    Assert 'a matrix on disk is never reported as no multiversion run' `
        ($null -ne $mv) `
        'multiversion was null while docs/evidence/structure-matrix.json existed - the original defect'

    Assert 'THE RETIRED SENTENCE IS GONE' `
        ($r.Raw -notmatch 'no other Revit year has been opened') `
        'the ledger still claims no other Revit was opened'

    Assert 'the years, the greens and the totals are the artifact''s' `
        (@($mv.years_measured).Count -eq 5 -and @($mv.years_green).Count -eq 5 -and
         [int]$mv.totals.passed -eq 300 -and [int]$mv.totals.failed -eq 0) `
        ($mv | ConvertTo-Json -Compress)

    Assert 'the matrix is cited by path AND pinned by SHA-256' `
        ($mv.source -eq 'docs/evidence/structure-matrix.json' -and
         $mv.source_sha256 -match '^[0-9a-f]{64}$') `
        'a citation nobody can verify is a claim'

    Assert 'every year carries its own suite counts and its source artifact SHA' `
        (@($mv.per_year.PSObject.Properties.Name).Count -eq 5 -and
         [int]$mv.per_year.'2023'.suites.rebar.passed -eq 34 -and
         [int]$mv.per_year.'2027'.suites.performance.passed -eq 10 -and
         $mv.per_year.'2025'.source_artifact_sha256 -match '^[0-9a-fA-F]{64}$') `
        ($mv.per_year | ConvertTo-Json -Compress -Depth 6)

    Assert 'the per-year totals are INTEGERS, not the double Measure-Object returns' `
        ($mv.per_year.'2023'.passed -is [int] -or $mv.per_year.'2023'.passed -is [long]) `
        ('60.0 in a ledger reads as a measurement with a decimal place: ' + $mv.per_year.'2023'.passed)

    Assert 'the how-it-was-run note reaches the ledger from the artifact' `
        ([string]$mv.how_it_was_run -match 'INSIGHTS MODAL SENTENCE') `
        'the reason a year needed a human hand must travel with the numbers'

    Write-Host ''
    Write-Host '== the cross-checks are computed, not copied ==' -ForegroundColor Cyan

    Assert 'a matrix and a roll-up on the same build agree' `
        ($mv.agrees_with_the_roll_up -eq $true -and $mv.rows_add_up_to_the_totals -eq $true) `
        ($mv | ConvertTo-Json -Compress)

    # A MATRIX FROM A DIFFERENT BUILD. The ledger must not present two products
    # as one page just because both files parse.
    Set-Matrix (New-Matrix @{ commits_seen = @(('d' * 40)) })
    $r2 = Invoke-Generator (New-RollUp @{}) 'otherbuild.json'
    Assert 'a matrix from a DIFFERENT build is flagged, not blended' `
        ($r2.Doc.live.multiversion.agrees_with_the_roll_up -eq $false) `
        'two builds described as one page'

    # TOTALS THAT DO NOT ADD UP. The artifact says 999; the rows say 300.
    Set-Matrix (New-Matrix @{ totals = [ordered]@{ passed = 999; failed = 0; unverified = 0
                                                   not_covered = 0; fixture_missing = 0 } })
    $r3 = Invoke-Generator (New-RollUp @{}) 'badtotals.json'
    Assert 'a summary that does not re-add is caught here rather than believed' `
        ($r3.Doc.live.multiversion.rows_add_up_to_the_totals -eq $false) `
        'the ledger copied a total nobody checked'

    # A YEAR THAT FAILED. Coverage must stop saying complete.
    $m = New-Matrix @{}
    $m.years_green = @('2023', '2024', '2025', '2026')
    $m.rows[4].state = 'failed'
    $m.rows[4].performance.failed = 3
    $m.rows[4].performance.passed = 7
    $m.rows[4].performance.state = 'failed'
    $m.totals.failed = 3
    $m.totals.passed = 297
    Set-Matrix $m
    $r4 = Invoke-Generator (New-RollUp @{}) 'oneyearred.json'
    Assert 'one red year stops the coverage line saying complete' `
        ([string]$r4.Doc.live.multiversion.behaviour_coverage -match '^partial') `
        ([string]$r4.Doc.live.multiversion.behaviour_coverage)

    # NO MATRIX AT ALL. The absence must be about the evidence, not a claim
    # about the years.
    Set-Matrix $null
    $r5 = Invoke-Generator (New-RollUp @{}) 'nomatrix.json'
    Assert 'with no matrix, the null is explained as missing EVIDENCE' `
        ($null -eq $r5.Doc.live.multiversion -and
         [string]$r5.Doc.live.multiversion_means -match 'statement about the evidence') `
        ([string]$r5.Doc.live.multiversion_means)

    Write-Host ''
    Write-Host '== cleanliness answers three questions, and says which ==' -ForegroundColor Cyan

    Set-Matrix (New-Matrix @{})

    # A DIRTY TREE OF GENERATED DOCUMENTS is the ordinary case and must not read
    # as uncommitted product code.
    $r6 = Invoke-Generator (New-RollUp @{
        repo_tracked_clean = $false
        repo_modified_paths = @(' M docs/inventory.json', ' M docs/evidence/structure-matrix.json')
    }) 'dirtydocs.json'
    $c6 = $r6.Doc.cleanliness
    Assert 'generated documents dirty do NOT read as uncommitted product code' `
        ($c6.rollup_repo_clean -eq $false -and
         $c6.rollup_repo_modified_recorded -eq $true -and
         $c6.rollup_repo_modified_classified.clean_of_product_code -eq $true -and
         @($c6.rollup_repo_modified_classified.generated_documents).Count -eq 2) `
        ($c6.rollup_repo_modified_classified | ConvertTo-Json -Compress)

    # UNCOMMITTED PRODUCT CODE is the one that matters, and must be named.
    $r7 = Invoke-Generator (New-RollUp @{
        repo_tracked_clean = $false
        repo_modified_paths = @(' M src/Horizun.Revit/Commands/Whatever.cs', ' M docs/inventory.json')
    }) 'dirtysrc.json'
    $c7 = $r7.Doc.cleanliness
    Assert 'uncommitted code under src/ is named as product code' `
        ($c7.rollup_repo_modified_classified.clean_of_product_code -eq $false -and
         @($c7.rollup_repo_modified_classified.product_code).Count -eq 1) `
        ($c7.rollup_repo_modified_classified | ConvertTo-Json -Compress)

    # AN OLDER ROLL-UP HAS NOT ANSWERED THE QUESTION. Absent must not be empty.
    $noPaths = [ordered]@{}
    (New-RollUp @{}) | Out-Null
    $rollNoPaths = New-RollUp @{ repo_tracked_clean = $false }
    $j = Get-Content -LiteralPath $rollNoPaths -Raw | ConvertFrom-Json
    $j.PSObject.Properties.Remove('repo_modified_paths')
    ($j | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $rollNoPaths -Encoding UTF8
    $r8 = Invoke-Generator $rollNoPaths 'nopaths.json'
    $c8 = $r8.Doc.cleanliness
    Assert 'a roll-up that predates the field says NOT RECORDED, never an empty list' `
        ($c8.rollup_repo_modified_recorded -eq $false -and $null -eq $c8.rollup_repo_modified_classified) `
        ('an empty list beside a dirty tree reads as "nothing was modified": ' +
         ($c8 | ConvertTo-Json -Compress -Depth 4))

    Assert 'the one question that decides reproducibility is still its own field' `
        ($c8.code_candidate_clean -eq $true -and
         [string]$c8.code_candidate_clean_means -match 'reproducible') `
        'code_candidate_clean is the only one that decides anything'
}
finally {
    foreach ($p in $written) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
    if ($hadEvidence) { Copy-Item -LiteralPath $backup -Destination $evidence -Force }
    elseif (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Force }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "structural state tests: $failed FAILED" -ForegroundColor Red
    exit 1
}
Write-Host 'structural state tests: ALL PASS' -ForegroundColor Green
exit 0
