#Requires -Version 5.1
<#
  THE STRUCTURAL PROGRAMME'S LEDGER, GENERATED.

  docs/STRUCTURAL-PROGRAM-STATE.json is written by this script from files
  something else produced: the live roll-up the suite wrote, docs/inventory.json
  which the built server produced, and the offline test counts measured here by
  running them. Nothing in it is typed by hand, because a ledger somebody edits
  is a claim rather than a record.

  A row that says null says so because nothing measured it - never because it
  was assumed to work.

  Usage:
    pwsh -File scripts/generate-structural-state.ps1
    pwsh -File scripts/generate-structural-state.ps1 -RollUp artifacts/live/structure-all-....json
#>
[CmdletBinding()]
param(
    [string]$RollUp,
    [string]$Out,
    [switch]$SkipTests
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved AFTER binding: under Windows PowerShell 5.1 a parameter default that
# reads $PSScriptRoot evaluates before it is populated.
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $repo 'docs\STRUCTURAL-PROGRAM-STATE.json' }
$artifactDir = Join-Path $repo 'artifacts\live'

if (-not $RollUp) {
    if (-not (Test-Path -LiteralPath $artifactDir)) {
        throw "no artifacts/live directory: run scripts/live/verify-structure-all.ps1 first."
    }
    $found = @(Get-ChildItem -LiteralPath $artifactDir -Filter 'structure-all-*.json' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending)
    if ($found.Count -eq 0) { throw "no structure-all-*.json roll-up found in $artifactDir." }
    $RollUp = $found[0].FullName
}
if (-not (Test-Path -LiteralPath $RollUp)) { throw "roll-up not found: $RollUp" }

$roll = Get-Content -LiteralPath $RollUp -Raw | ConvertFrom-Json

# A ROLL-UP THAT ADDED UP TWO BUILDS IS NOT EVIDENCE, and a ledger built on one
# would carry the incoherence forward under a single candidate.
if (-not $roll.all_results_same_candidate) {
    throw ("the roll-up says its harnesses did not all run against one build " +
           "(other candidates seen: " + ($roll.other_candidates_seen -join ', ') + "). Nothing is generated.")
}

function Get-HzTestCount {
    param([string]$Project)
    $out = & dotnet test (Join-Path $repo $Project) -c Release --nologo 2>&1 | Out-String
    if ($out -match 'Failed:\s+(\d+),\s+Passed:\s+(\d+)') {
        return [ordered]@{ failed = [int]$Matches[1]; passed = [int]$Matches[2] }
    }
    return [ordered]@{ failed = $null; passed = $null }
}

$core = [ordered]@{ passed = $null; failed = $null }
$server = [ordered]@{ passed = $null; failed = $null }
if (-not $SkipTests) {
    Write-Host '[structural-state] measuring the offline suites...' -ForegroundColor DarkGray
    $core = Get-HzTestCount 'tests\Horizun.Core.Tests'
    $server = Get-HzTestCount 'tests\Horizun.Server.Tests'
}

$inventoryPath = Join-Path $repo 'docs\inventory.json'
$inventory = $null
if (Test-Path -LiteralPath $inventoryPath) {
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
}

# THE MULTIVERSION MATRIX, read rather than described.
#
# This block used to be written as `multiversion = $null` with a sentence saying
# no other Revit year had been opened. That sentence went on being emitted after
# five years HAD been measured, so the ledger and docs/evidence/structure-matrix.json
# said opposite things about the same product - and the ledger is the file people
# read first. Nothing below is typed here: every number comes out of the artifact,
# and if the artifact is missing the block says so instead of asserting a
# negative.
$multiversionPath = Join-Path $repo 'docs\evidence\structure-matrix.json'
$mx = $null
$mxSha = $null
if (Test-Path -LiteralPath $multiversionPath) {
    $mx = Get-Content -LiteralPath $multiversionPath -Raw | ConvertFrom-Json
    $mxSha = (Get-FileHash -LiteralPath $multiversionPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

$matrixPath = Join-Path $repo 'docs\STRUCTURAL-API-MATRIX.json'
$matrix = $null
if (Test-Path -LiteralPath $matrixPath) {
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
}

$head = (& git -C $repo rev-parse HEAD).Trim()
$dirty = @(& git -C $repo status --porcelain --untracked-files=no)
$generatorStartedClean = ($dirty.Count -eq 0)

# HOW FAR HEAD HAS MOVED SINCE THE LIVE NUMBERS WERE TAKEN.
#
# The ledger records the candidate the RUNNING bridge reported, which is the
# build the live numbers belong to. What it did not record is how much code has
# landed since - and a reader who sees "34/34 live" beside a head carrying
# thousands of lines of unverified work is being allowed to draw a conclusion
# nothing here supports. Documentation commits after a candidate are ordinary;
# commits touching src/ are not, and they are counted separately.
$commitsAfter = @()
$codeCommitsAfter = @()
$candidateIsAncestor = $false
if ($roll.candidate) {
    & git -C $repo merge-base --is-ancestor $roll.candidate $head 2>$null
    $candidateIsAncestor = ($LASTEXITCODE -eq 0)
    if ($candidateIsAncestor) {
        $commitsAfter = @(& git -C $repo rev-list --count "$($roll.candidate)..$head")
        $codeCommitsAfter = @(& git -C $repo rev-list "$($roll.candidate)..$head" -- src/ tools/)
    }
}
$codeAfter = @($codeCommitsAfter).Count
$totalAfter = if (@($commitsAfter).Count -gt 0) { [int]$commitsAfter[0] } else { 0 }

# A MODIFIED PATH IS NOT A MODIFIED PATH. Uncommitted changes under src/ mean
# the numbers describe code that is not in the history; uncommitted changes to a
# generated document mean a generator ran. Publishing one undifferentiated list
# forces every reader to make this distinction by eye, and most will not.
function Split-HzModified {
    param([string[]]$Porcelain)
    $product = @(); $harness = @(); $generated = @(); $other = @()
    foreach ($line in @($Porcelain)) {
        if (-not $line) { continue }
        $path = ($line -replace '^..\s+', '') -replace '^.*? -> ', ''
        switch -Regex ($path) {
            '^(src|tools)/'                                  { $product += $path; break }
            '^docs/(inventory|STRUCTURAL-PROGRAM-STATE|DWG-PROGRAM-STATE|MAXIMUM-PROGRAM-STATE|evidence/)' {
                                                               $generated += $path; break }
            '^(scripts|tests)/'                              { $harness += $path; break }
            default                                          { $other += $path }
        }
    }
    [ordered]@{
        product_code = $product
        product_code_means =
            'uncommitted changes under src/ or tools/. This is the only class that would make the live ' +
            'numbers describe code that is not in the history.'
        harness_and_tests = $harness
        generated_documents = $generated
        other = $other
        clean_of_product_code = ($product.Count -eq 0)
    }
}
# AN ARTIFACT THAT PREDATES A FIELD HAS NOT ANSWERED THE QUESTION. The roll-up
# only started recording which paths were modified in this same commit, so an
# older one has no list - and reading its absence as an empty list would publish
# "nothing was modified" beside "the tree was dirty", which is worse than saying
# nothing. It is reported as not recorded.
$rollupModifiedRecorded = ($roll.PSObject.Properties.Name -contains 'repo_modified_paths')
$rollupModifiedPaths = @(if ($rollupModifiedRecorded) { $roll.repo_modified_paths })
$rollupModifiedClass = $(if ($rollupModifiedRecorded) { Split-HzModified $rollupModifiedPaths } else { $null })
$generatorModifiedClass = Split-HzModified @($dirty)

# EVERY FIELD OUT OF THE ARTIFACT. The only things computed here are the
# cross-checks - whether the matrix and the roll-up are talking about the same
# build, and whether the per-year rows add up to the totals the file claims.
# A ledger that copied the artifact's own summary without checking it would
# carry an inconsistent artifact forward as though it were coherent.
$multiversion = $null
if ($mx) {
    $mxYears = @($mx.years_measured)
    $mxGreen = @($mx.years_green)
    $mxCommits = @($mx.commits_seen)
    $perYear = [ordered]@{}
    $sumPassed = 0; $sumFailed = 0
    foreach ($row in @($mx.rows)) {
        $suites = [ordered]@{}
        foreach ($k in @('rebar', 'geometry', 'performance')) {
            $suite = $row.$k
            if (-not $suite) { continue }
            $suites[$k] = [ordered]@{
                state = $suite.state
                passed = [int]$suite.passed
                failed = [int]$suite.failed
                unverified = [int]$suite.unverified
                not_covered = [int]$suite.not_covered
                fixture_missing = [int]$suite.fixture_missing
                artifact = $suite.artifact
            }
            $sumPassed += [int]$suite.passed
            $sumFailed += [int]$suite.failed
        }
        $src = $mx.source_artifacts.([string]$row.year)
        $perYear[[string]$row.year] = [ordered]@{
            state = $row.state
            revit_build = $row.revit_build
            commit = $row.commit
            model = $row.model
            suites = $suites
            # [int], because Measure-Object returns a double and a ledger that says
            # a year scored 60.0 reads as a measurement with a decimal place.
            passed = [int](@($suites.Keys | ForEach-Object { $suites[$_].passed } | Measure-Object -Sum).Sum)
            source_artifact = $(if ($src) { $src.artifact } else { $null })
            source_artifact_sha256 = $(if ($src) { $src.sha256 } else { $null })
        }
    }

    # THE CROSS-CHECKS. Each is a question the ledger answers rather than assumes.
    $sameBuild = ($mxCommits.Count -eq 1 -and [string]$mxCommits[0] -eq [string]$roll.candidate)
    $rowsAddUp = ([int]$mx.totals.passed -eq $sumPassed -and [int]$mx.totals.failed -eq $sumFailed)

    $multiversion = [ordered]@{
        source = 'docs/evidence/structure-matrix.json'
        source_sha256 = $mxSha
        source_means =
            'the sanitised record that is committed. artifacts/ is ignored because the per-year reports carry ' +
            'machine-local paths; this file pins each of them by SHA-256 so a reader with the artifacts can ' +
            'verify them byte for byte.'
        generated_utc = $mx.generated_utc
        years_measured = $mxYears
        years_green = $mxGreen
        candidate = $(if ($mxCommits.Count -eq 1) { $mxCommits[0] } else { $null })
        commits_seen = $mxCommits
        one_build = $mx.one_build
        one_build_means = $mx.one_build_means
        totals = $mx.totals
        per_year = $perYear
        how_it_was_run = $mx.how_it_was_run
        agrees_with_the_roll_up = $sameBuild
        agrees_with_the_roll_up_means =
            'true when the matrix and the live roll-up above report the SAME candidate commit. False would ' +
            'mean this ledger is describing two different products in one page, and the two blocks below it ' +
            'could not be read together.'
        rows_add_up_to_the_totals = $rowsAddUp
        rows_add_up_means =
            'the per-year suite counts were re-added here and compared with the totals the artifact states. ' +
            'A summary nobody re-added is a claim.'
        behaviour_coverage =
            $(if ($mxGreen.Count -eq $mxYears.Count -and [int]$mx.totals.failed -eq 0) {
                'complete for the structural slice: every installed Revit year was MEASURED, not merely ' +
                'compiled against, and every year is green on the same build.'
              } else {
                'partial: see years_green against years_measured, and the per-year rows for what failed.'
              })
    }
}

$state = [ordered]@{
    schema = 'horizun.structural-program/1'
    what_this_is =
        'The structural programme''s own ledger, GENERATED by scripts/generate-structural-state.ps1 from ' +
        'files something else wrote: the live roll-up the suite produced, docs/inventory.json which the built ' +
        'server produced, and the offline counts measured here by running the suites. A row that says null ' +
        'says so because nothing measured it, never because it was assumed to work.'
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    generated_by = 'scripts/generate-structural-state.ps1'
    generated_from = [ordered]@{
        roll_up = ('artifacts/live/' + (Split-Path -Leaf $RollUp))
        roll_up_sha256 = (Get-FileHash -LiteralPath $RollUp -Algorithm SHA256).Hash.ToLowerInvariant()
        inventory = 'docs/inventory.json'
        api_matrix = 'docs/STRUCTURAL-API-MATRIX.json'
    }
    candidate = [ordered]@{
        commit = $roll.candidate
        commit_means = 'read off the RUNNING bridge, not from git HEAD - HEAD moves for documentation and this is the build the live numbers belong to.'
        server_sha256 = $roll.server_sha256
        contract_hash = $roll.contract_hash
        version = $roll.horizun_version
        built_from_clean_tree = $roll.built_from_clean_tree
        repo_head_at_roll_up = $roll.repo_head
        repo_tracked_clean_at_roll_up = $roll.repo_tracked_clean
        published = $false
        published_means = 'nothing here has been pushed, tagged or released.'
    }
    code_since_candidate = [ordered]@{
        means =
            'the live numbers below belong to the CANDIDATE, not to the head of this branch. When ' +
            'code_commits_after_candidate is greater than zero, there is product code here that no live probe ' +
            'has ever run. That is a statement about coverage, not about quality.'
        head_now = $head
        candidate_is_an_ancestor_of_head = $candidateIsAncestor
        commits_after_candidate = $totalAfter
        code_commits_after_candidate = $codeAfter
        live_numbers_cover_head = ($candidateIsAncestor -and $codeAfter -eq 0)
        why_it_can_be_nonzero =
            'live probes run against the INSTALLED bridge, so code that has not been installed has not been ' +
            'probed. install.ps1 requires Revit to be closed, and a session may also be told not to run it. ' +
            'Either way the effect is the same: a session can build, test and compile every year, and still ' +
            'not make a live probe touch a line of what it wrote.'
    }
    cleanliness = [ordered]@{
        means =
            'three DIFFERENT questions, and only the first decides whether the live numbers are reproducible. ' +
            'A reader who sees one false here should be able to tell immediately which question it answers, ' +
            'because two of the three are false in the ordinary course of generating this very file.'

        # 1. THE ONLY ONE THAT DECIDES ANYTHING.
        code_candidate_clean = $roll.built_from_clean_tree
        code_candidate_clean_means =
            'the INSTALLED binary the live probes ran against was built from a tree with no uncommitted ' +
            'changes. This is the question that matters: it is what makes the numbers reproducible from the ' +
            'candidate commit alone. The two below are about the working tree at moments that have no bearing ' +
            'on what was measured.'

        # 2. THE WORKING TREE WHEN THE PROBES RAN.
        rollup_repo_clean = $roll.repo_tracked_clean
        rollup_repo_modified = $rollupModifiedPaths
        rollup_repo_modified_recorded = $rollupModifiedRecorded
        rollup_repo_modified_recorded_means =
            'false when the roll-up artifact predates this field, in which case WHICH files were modified is ' +
            'not recoverable after the fact and this ledger says so rather than publishing an empty list. An ' +
            'empty list beside a dirty tree would read as "nothing was modified", which is worse than an ' +
            'admitted gap. The next roll-up records it.'
        rollup_repo_clean_means =
            'whether the working tree had uncommitted tracked changes while the probes ran. It is routinely ' +
            'false and routinely harmless: this script and the roll-up both WRITE into docs/, so generating ' +
            'evidence dirties the tree that the evidence then reports on. What matters is WHICH files, which ' +
            'is why rollup_repo_modified is beside it - and why a false with an empty list means the roll-up ' +
            'predates the paths being recorded rather than that nothing was modified.'
        rollup_repo_modified_classified = $rollupModifiedClass

        # 3. THE WORKING TREE WHEN THIS FILE WAS WRITTEN.
        generator_started_clean = $generatorStartedClean
        generator_started_modified = @($dirty)
        generator_started_modified_classified = $generatorModifiedClass
        generator_started_clean_means =
            'whether the tree was clean when this generator STARTED. It says nothing about the live numbers, ' +
            'which were taken earlier against an installed binary; it says whether this ledger was generated ' +
            'from a state somebody else could reproduce.'

        generated_files = @('docs/STRUCTURAL-PROGRAM-STATE.json', 'docs/evidence/structure-matrix.json')
        generated_files_means =
            'written by generators, not edited by hand. Seeing these in a modified list is the expected case ' +
            'and is not evidence of uncommitted product code.'
    }
    static = [ordered]@{
        core_tests = $core
        server_tests = $server
        measured = $(if ($SkipTests) { 'skipped by -SkipTests' } else { 'here, this run' })
    }
    live = [ordered]@{
        revit_year = $roll.revit_year
        revit_build = $roll.revit_build
        harnesses = $roll.steps_run
        harnesses_failed = $roll.steps_failed
        all_results_same_candidate = $roll.all_results_same_candidate
        totals = $roll.totals
        steps = $roll.steps
        multiversion = $multiversion
        multiversion_means =
            $(if ($multiversion) {
                'read from ' + $multiversion.source + ', which is itself generated from the per-year matrix ' +
                'artifacts and pins each of them by SHA-256. Every number in that block came out of the file; ' +
                'none of it is typed here. BEHAVIOUR is proved on the years listed, not merely compilation.'
              } else {
                'null because docs/evidence/structure-matrix.json is not present, so no multiversion run has ' +
                'been recorded for this path. That is a statement about the evidence, not about the years.'
              })
    }
    api_matrix = $(if ($matrix) { [ordered]@{
        probe_list_sha256 = $matrix.probe_list_sha256
        years_probed = $matrix.portable_subset.years_probed
        types_in_every_year = @($matrix.portable_subset.types_in_every_year).Count
        types_in_some_years = @($matrix.portable_subset.types_in_some_years).Count
        members_in_every_year = @($matrix.portable_subset.members_in_every_year).Count
        members_in_some_years = @($matrix.portable_subset.members_in_some_years).Count
        means = 'measured by reflection over the installed RevitAPI assemblies. The partial rows are why ADR-002 exists.'
    } } else { $null })
    inventory = $(if ($inventory) { [ordered]@{
        tools = $inventory.counts.tools
        operations = $inventory.counts.operations
        enumerated_variants = $inventory.counts.enumerated_variants
        contract_hash = $inventory.generated_from_contract_hash
    } } else { $null })
    not_implemented = @(
        'per-face cover: only the COMMON cover is written. Reading per-face cover works.',
        'free-form bars, area reinforcement, path reinforcement and fabric: the schema cannot ask for them.',
        'laps, couplers, splices and stirrup zones: not in the schema and not audited - the audit lists them under not_checked.',
        'steel connections, plates, bolts and welds: read only, through horizun_query_structure mode=connections.',
        'bar type, hook type and shape CREATION: refused, because each carries a design decision.',
        'reinforcement from a DWG: not started.'
    )
}

$json = $state | ConvertTo-Json -Depth 20
# LF, matching .gitattributes, which pins the whole repository to eol=lf because
# line endings once reached the compiled binary.
$json = $json -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($Out, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("[structural-state] {0} probes passed, {1} failed, across {2} harness(es) at candidate {3}" -f
    $roll.totals.passed, $roll.totals.failed, $roll.steps_run, ([string]$roll.candidate).Substring(0, 12))
Write-Host ("[structural-state] written to {0}" -f $Out)
