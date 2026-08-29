#Requires -Version 5.1
<#
  docs/DWG-PROGRAM-STATE.json, GENERATED.

  The ledger is a claim about what was measured, and a claim typed by hand is a
  claim about what somebody remembered. Every number here comes from a file
  something else wrote:

    the LIVE counts        from the roll-up artifact verify-dwg-all.ps1 writes,
                           which itself refuses to add up results from two
                           different builds
    the OFFLINE counts     from `dotnet test`, run here
    the BUILD counts       from `dotnet build` for each Revit year, run here
    the INVENTORY          from docs/inventory.json, which is generated from the
                           built server's own tools/list
    the CANDIDATE          from the roll-up, which read it off the running
                           bridge - not from git HEAD, which moves for
                           documentation

  Anything this script cannot measure is written as null with a reason beside
  it, never as a zero and never as a guess.

    pwsh -File scripts/generate-dwg-state.ps1
    ... -RollUp <path>     a specific roll-up artifact (default: the newest)
    ... -SkipTests         reuse the counts already in the file for the offline
                           suites, and SAY that is what happened
#>
[CmdletBinding()]
param(
    [string]$RollUp,
    [switch]$SkipTests,
    [string]$Out
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $repo 'docs\DWG-PROGRAM-STATE.json' }
$artifacts = Join-Path $repo 'artifacts\live'

# WHAT THE TREE LOOKED LIKE BEFORE THIS SCRIPT TOUCHED IT.
#
# Measured FIRST, because a generator dirties the tree by writing, and asking
# git afterwards answers a question about this script rather than about the
# thing it is describing. That conflation is what shipped: the ledger said
# `repo_tracked_clean_at_generation: false` while the roll-up it was built from
# said `repo_tracked_clean: true`, and a reader could only conclude that the
# measured candidate was dirty. It was not - the generated documents were.
$startedModified = @(& git -C $repo status --porcelain --untracked-files=no |
                     ForEach-Object { ($_ -replace '^\s*\S+\s+', '').Trim() })
$startedClean = $startedModified.Count -eq 0

# ------------------------------------------------------------------ the roll-up
if (-not $RollUp) {
    $newest = @(Get-ChildItem -LiteralPath $artifacts -Filter 'dwg-all-*.json' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
    if ($newest.Count -eq 0) {
        throw ("no roll-up artifact in {0}. Run scripts/live/verify-dwg-all.ps1 first - this file " +
               "reports what was measured, and with nothing measured there is nothing to report." -f $artifacts)
    }
    $RollUp = $newest[0].FullName
}
$roll = Get-Content -LiteralPath $RollUp -Raw | ConvertFrom-Json
if (-not $roll.all_results_same_candidate) {
    throw ("the roll-up at {0} mixes results from more than one build. A ledger built from it would add " +
           "up numbers that do not belong together." -f (Split-Path $RollUp -Leaf))
}

function Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    $p.Value
}

# -------------------------------------------------------------- offline suites
function Measure-Suite([string]$Project) {
    $output = & dotnet test (Join-Path $repo $Project) --nologo -v q 2>&1 | Out-String
    if ($output -match 'Failed:\s+(?<f>\d+),\s+Passed:\s+(?<p>\d+)') {
        return [ordered]@{ passed = [int]$Matches['p']; failed = [int]$Matches['f'] }
    }
    [ordered]@{ passed = $null; failed = $null
                could_not_measure = 'dotnet test did not report a countable result' }
}

$previous = $null
if (Test-Path -LiteralPath $Out) {
    try { $previous = Get-Content -LiteralPath $Out -Raw | ConvertFrom-Json } catch { $previous = $null }
}

if ($SkipTests) {
    $core = [ordered]@{ passed = [int](Field (Field (Field $previous 'static') 'core_tests') 'passed')
                        failed = [int](Field (Field (Field $previous 'static') 'core_tests') 'failed')
                        measured = 'carried over: this run was asked to skip the suites' }
    $server = [ordered]@{ passed = [int](Field (Field (Field $previous 'static') 'server_tests') 'passed')
                          failed = [int](Field (Field (Field $previous 'static') 'server_tests') 'failed')
                          measured = 'carried over: this run was asked to skip the suites' }
} else {
    Write-Host 'measuring the offline suites...'
    $core = Measure-Suite 'tests\Horizun.Core.Tests\Horizun.Core.Tests.csproj'
    $server = Measure-Suite 'tests\Horizun.Server.Tests\Horizun.Server.Tests.csproj'
}

# ---------------------------------------------------------------- the builds
$years = @(2023, 2024, 2025, 2026, 2027)
$buildErrors = 0
$buildWarnings = 0
$builtYears = @()
if (-not $SkipTests) {
    foreach ($y in $years) {
        Write-Host ("building the add-in for Revit {0}..." -f $y)
        # NOT $out. PowerShell variable names are case-insensitive, so $out IS
        # the $Out parameter - the path this file gets written to - and the
        # whole build log landed in it. Set-Content then failed on a path
        # several thousand lines long, and only on the path that runs the
        # builds, which is why the fast path looked fine.
        $buildLog = & dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') `
            -c Release -p:RevitYear=$y -warnaserror --nologo -v q 2>&1 | Out-String
        # COUNT MSBUILD'S OWN SUMMARY, not the word "error" anywhere in the log.
        #
        # The first version searched for the substring, and -warnaserror is in
        # every command line: five clean builds were published as 10 errors and
        # 5 warnings. A number that is wrong in the SAFE direction is still a
        # number nobody can use, and this one is the whole point of the row.
        if ($buildLog -match '(?m)^\s*(?<n>\d+)\s+Error\(s\)') { $buildErrors += [int]$Matches['n'] }
        elseif ($LASTEXITCODE -ne 0) { $buildErrors++ }
        if ($buildLog -match '(?m)^\s*(?<n>\d+)\s+Warning\(s\)') { $buildWarnings += [int]$Matches['n'] }
        $builtYears += $y
    }
}

# -------------------------------------------------------------- the inventory
$inventory = $null
$inventoryPath = Join-Path $repo 'docs\inventory.json'
if (Test-Path -LiteralPath $inventoryPath) {
    $inv = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    # The counts live under `counts`; reading them off the top level returns
    # null for every one of them, which would publish a ledger claiming the
    # surface was never measured.
    $c = Field $inv 'counts'
    $inventory = [ordered]@{
        tools = Field $c 'tools'
        reads = Field $c 'reads'
        writes = Field $c 'writes'
        destructive = Field $c 'destructive'
        operations = Field $c 'operations'
        enumerated_variants = Field $c 'enumerated_variants'
        generated_from_server_sha = Field $inv 'generated_from_server_sha'
        code_candidate_commit = Field $inv 'code_candidate_commit'
        means = ("generated from the built server's own tools/list, never typed by hand. An enumerated " +
                 "value is an ARGUMENT, not a proven behaviour.")
    }
}

# ------------------------------------------------------------------- the runs
$runs = @()
foreach ($step in $roll.steps) {
    $runs += [ordered]@{
        step = $step.step
        name = $step.name
        harness = $step.harness
        passed = $step.passed
        failed = $step.failed
        unverified = $step.unverified
        not_covered = $step.not_covered
        fixture_missing = $step.fixture_missing
        artifact = ('artifacts/live/' + [string]$step.artifact)
    }
}

$doc = [ordered]@{
    schema = 'horizun.dwg-program/2'
    what_this_is = ("The DWG-to-BIM program's own ledger, GENERATED by " +
                    "scripts/generate-dwg-state.ps1 from files something else wrote. Every number was " +
                    "MEASURED - the live counts from the roll-up artifact, the offline counts from " +
                    "dotnet test, the inventory from the built server's own tools/list. A row that says " +
                    "null says so because nothing measured it, never because it was assumed to work.")
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    generated_by = 'scripts/generate-dwg-state.ps1'
    generated_from = [ordered]@{
        roll_up = ('artifacts/live/' + (Split-Path $RollUp -Leaf))
        roll_up_sha256 = (Get-FileHash -LiteralPath $RollUp -Algorithm SHA256).Hash.ToLowerInvariant()
        inventory = 'docs/inventory.json'
    }
    candidate = [ordered]@{
        commit = $roll.candidate
        commit_means = ("read off the RUNNING bridge, not from git HEAD - HEAD moves for documentation " +
                        "and this is the build the live numbers belong to.")
        server_sha256 = $roll.server_sha256
        contract_hash = $roll.contract_hash
        version = $roll.horizun_version
        branch = (& git -C $repo rev-parse --abbrev-ref HEAD).Trim()
        repo_head_at_generation = (& git -C $repo rev-parse HEAD).Trim()
        published = $false
        published_means = ('nothing here has been pushed, tagged or released. v1.0.0 is untouched and no ' +
                           'v1.1.0 tag exists.')
    }
    cleanliness = [ordered]@{
        means = ("THREE DIFFERENT QUESTIONS that one boolean used to answer wrongly. A generator dirties " +
                 "the tree by writing, so asking git after it has run answers a question about the " +
                 "generator, not about the code that was measured.")

        code_candidate_clean = $roll.built_from_clean_tree
        code_candidate_clean_means = ("did the BINARY come from a tree that matched a commit. Read from the " +
                                      "bridge's own stamp. This is the one that decides whether the live " +
                                      "numbers are reproducible.")

        rollup_repo_clean = $roll.repo_tracked_clean
        rollup_repo_clean_means = ("was the working tree clean when the EVIDENCE was measured, recorded by " +
                                   "the harness at the moment it ran.")

        generator_started_clean = $startedClean
        generator_started_modified = $startedModified
        generator_started_clean_means = ("was the tree clean when THIS SCRIPT started. False is ordinary and " +
                                         "is not a fault: regenerating docs/inventory.json first leaves it " +
                                         "modified, and that file is a generated document, not code. Read the " +
                                         "list beside it before concluding anything.")

        generated_files = @('docs/DWG-PROGRAM-STATE.json')
        generated_files_means = ("what this script writes. These are expected to be modified afterwards; " +
                                 "they are the output, not evidence of a dirty candidate.")

        documentation_head_after_commit = $null
        documentation_head_after_commit_means = ("cannot be known here - the commit that carries this file " +
                                                 "does not exist while the file is being written. Null is the " +
                                                 "honest answer; git log is where a reader finds it.")

        evidence_is_reproducible = ($roll.built_from_clean_tree -eq $true -and $roll.repo_tracked_clean -eq $true)
        evidence_is_reproducible_means = ("the binary named a commit it WAS, and the tree matched that commit " +
                                          "when the probes ran. This is the question a reader is actually " +
                                          "asking, and it does not depend on what a generator did afterwards.")
    }
    static = [ordered]@{
        core_tests = $core
        server_tests = $server
        revit_builds = [ordered]@{
            years = $(if ($builtYears.Count -gt 0) { $builtYears } else { $null })
            errors = $(if ($builtYears.Count -gt 0) { $buildErrors } else { $null })
            warnings = $(if ($builtYears.Count -gt 0) { $buildWarnings } else { $null })
            flags = '-c Release -warnaserror'
            measured = $(if ($builtYears.Count -gt 0) { 'here, this run' }
                         else { 'not measured: this run was asked to skip the builds' })
        }
        inventory = $inventory
    }
    live = [ordered]@{
        revit_year = $roll.revit_year
        revit_build = $roll.revit_build
        revit_year_note = ('2023, 2024, 2025 and 2027 were NOT opened. The multi-version live matrix ' +
                           'remains deferred and needs express authorisation.')
        total_probes = $roll.totals.passed
        failed = $roll.totals.failed
        unverified = $roll.totals.unverified
        not_covered = $roll.totals.not_covered
        fixture_missing = $roll.totals.fixture_missing
        all_results_same_candidate = $roll.all_results_same_candidate
        runs = $runs
    }
}

($doc | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $Out -Encoding UTF8
Write-Host ("wrote {0}" -f $Out)
Write-Host ("  candidate {0}" -f ([string]$roll.candidate).Substring(0, 12))
Write-Host ("  live {0} passed / {1} failed across {2} harness(es)" -f
    $roll.totals.passed, $roll.totals.failed, $runs.Count)
Write-Host ("  offline core {0}/{1}, server {2}/{3}" -f
    $core.passed, ($core.passed + $core.failed), $server.passed, ($server.passed + $server.failed))
