#Requires -Version 5.1
<#
  THE LEDGER DESCRIBES WHAT WAS MEASURED, NOT WHAT THE GENERATOR DID.

  docs/DWG-PROGRAM-STATE.json used to carry one boolean,
  `repo_tracked_clean_at_generation`, and it answered the wrong question. A
  generator dirties the tree by writing, so asking git after it has run reports
  on the generator - and the file said `false` while the roll-up it was built
  from said the tree was clean when the probes ran. A reader could only conclude
  that the measured candidate was dirty. It was not.

  Three different questions now live in three fields, and the one a reader is
  actually asking is derived from the two that matter:

    code_candidate_clean     did the BINARY come from a tree matching a commit
    rollup_repo_clean        was the tree clean when the EVIDENCE was measured
    generator_started_clean  was the tree clean when THIS SCRIPT started
    evidence_is_reproducible the first two, together

  These tests drive the generator against synthetic roll-ups in five states,
  because four of them cannot be produced on demand on a working machine.
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
$generator = Join-Path $PSScriptRoot 'generate-dwg-state.ps1'
if (-not (Test-Path -LiteralPath $generator)) { throw 'generate-dwg-state.ps1 not found beside this test' }

$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-dwg-state-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

try {
    # A roll-up shaped exactly like the real one, with the fields under test
    # controllable. Written into the repository's own artifacts folder because
    # the generator resolves roll-ups relative to the repo - and removed after.
    $artifacts = Join-Path $repo 'artifacts\live'
    if (-not (Test-Path -LiteralPath $artifacts)) { New-Item -ItemType Directory -Path $artifacts -Force | Out-Null }
    $written = New-Object System.Collections.Generic.List[string]

    function New-RollUp([hashtable]$Override) {
        $doc = [ordered]@{
            schema = 'horizun.live-rollup/1'
            generated_utc = (Get-Date).ToUniversalTime().ToString('o')
            candidate = ('a' * 40)
            server_sha256 = ('b' * 64)
            contract_hash = '56adffa29ad1b9f34b091cf7'
            horizun_version = '1.1.0-dev'
            revit_year = '2026'
            revit_build = '26.4.0.32'
            repo_head = ('a' * 40)
            repo_tracked_clean = $true
            built_from_clean_tree = $true
            steps_run = 1
            steps_failed = 0
            all_results_same_candidate = $true
            other_candidates_seen = @()
            totals = [ordered]@{ passed = 7; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0 }
            steps = @([ordered]@{
                step = 2; name = 'chain'; harness = 'scripts/live/verify-dwg-chain.ps1'
                exit_code = 0; state = 'passed'; duration_ms = 1000
                artifact = 'dwg-chain-test.json'; passed = 7; failed = 0
                unverified = 0; not_covered = 0; fixture_missing = 0; candidate = ('a' * 40)
            })
        }
        foreach ($k in $Override.Keys) { $doc[$k] = $Override[$k] }
        $path = Join-Path $artifacts ('dwg-all-TEST-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
        ($doc | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $path -Encoding UTF8
        $written.Add($path)
        $path
    }

    function Invoke-Generator([string]$RollUp, [string]$OutName) {
        $out = Join-Path $root $OutName
        $log = & pwsh -NoProfile -File $generator -RollUp $RollUp -Out $out -SkipTests 2>&1 | Out-String
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Log = $log
            Doc = $(if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } else { $null })
        }
    }

    # ---------------------------------------------------------------- state 1
    # A CLEAN CANDIDATE. Both cleannesses true; the verdict follows.
    $r = Invoke-Generator (New-RollUp @{}) 'clean.json'
    Assert 'a clean candidate and clean evidence read as reproducible' `
        ($r.Doc -and $r.Doc.cleanliness.code_candidate_clean -eq $true -and
         $r.Doc.cleanliness.rollup_repo_clean -eq $true -and
         $r.Doc.cleanliness.evidence_is_reproducible -eq $true) `
        ($r.Doc.cleanliness | ConvertTo-Json -Compress)

    Assert 'the generator NEVER reports its own writing as a dirty candidate' `
        ($r.Doc.cleanliness.generated_files -contains 'docs/DWG-PROGRAM-STATE.json') `
        'generated_files must name what this script writes, so a reader can discount it'

    Assert 'the head a documentation commit will carry is NULL, not guessed' `
        ($null -eq $r.Doc.cleanliness.documentation_head_after_commit) `
        'the commit carrying this file does not exist while the file is written'

    # ---------------------------------------------------------------- state 2
    # A BINARY BUILT FROM A DIRTY TREE. The evidence is not reproducible, and it
    # must say so no matter how clean everything else looks.
    $r = Invoke-Generator (New-RollUp @{ built_from_clean_tree = $false }) 'dirtybin.json'
    Assert 'a binary from a DIRTY tree is never reported as reproducible evidence' `
        ($r.Doc.cleanliness.code_candidate_clean -eq $false -and
         $r.Doc.cleanliness.evidence_is_reproducible -eq $false) `
        ($r.Doc.cleanliness | ConvertTo-Json -Compress)

    # ---------------------------------------------------------------- state 3
    # THE TREE MOVED WHILE THE PROBES RAN. Same conclusion, different reason,
    # and the two reasons stay distinguishable.
    $r = Invoke-Generator (New-RollUp @{ repo_tracked_clean = $false }) 'dirtyrun.json'
    Assert 'a tree that was dirty WHEN THE PROBES RAN is reported separately' `
        ($r.Doc.cleanliness.code_candidate_clean -eq $true -and
         $r.Doc.cleanliness.rollup_repo_clean -eq $false -and
         $r.Doc.cleanliness.evidence_is_reproducible -eq $false) `
        ($r.Doc.cleanliness | ConvertTo-Json -Compress)

    # ---------------------------------------------------------------- state 4
    # A BINARY FROM ANOTHER COMMIT than the tree currently sits on. The ledger
    # must name the BINARY's commit - HEAD moves for documentation, and the live
    # numbers belong to the build, not to the checkout.
    $other = ('c' * 40)
    $r = Invoke-Generator (New-RollUp @{ candidate = $other; repo_head = $other
                                         steps = @([ordered]@{
                                             step = 2; name = 'chain'
                                             harness = 'scripts/live/verify-dwg-chain.ps1'
                                             exit_code = 0; state = 'passed'; duration_ms = 1
                                             artifact = 'x.json'; passed = 7; failed = 0
                                             unverified = 0; not_covered = 0; fixture_missing = 0
                                             candidate = $other }) }) 'othercommit.json'
    $head = (& git -C $repo rev-parse HEAD).Trim()
    Assert 'the ledger names the BINARY commit, and records HEAD separately' `
        ($r.Doc.candidate.commit -eq $other -and $r.Doc.candidate.repo_head_at_generation -eq $head -and
         $other -ne $head) `
        ("candidate=" + $r.Doc.candidate.commit + " head=" + $r.Doc.candidate.repo_head_at_generation)

    # ---------------------------------------------------------------- state 5
    # RESULTS FROM MORE THAN ONE CANDIDATE. A ledger built from these would add
    # up numbers that do not belong together, so the generator REFUSES.
    $mixed = New-RollUp @{ all_results_same_candidate = $false; other_candidates_seen = @(('d' * 40)) }
    $r = Invoke-Generator $mixed 'mixed.json'
    Assert 'a roll-up mixing two candidates is REFUSED rather than summed' `
        ($r.ExitCode -ne 0 -and $null -eq $r.Doc) `
        ("exit=" + $r.ExitCode + " wrote=" + ($null -ne $r.Doc))
    Assert 'and the refusal says why' `
        ($r.Log -match 'more than one build') $r.Log

    # ------------------------------------------------------------ no roll-up
    $r = Invoke-Generator (Join-Path $root 'does-not-exist.json') 'missing.json'
    Assert 'a roll-up that does not exist is refused, not invented' `
        ($r.ExitCode -ne 0 -and $null -eq $r.Doc) ("exit=" + $r.ExitCode)

    # ---------------------------------------------------- the shipped ledger
    $shipped = Join-Path $repo 'docs\DWG-PROGRAM-STATE.json'
    if (Test-Path -LiteralPath $shipped) {
        $doc = Get-Content -LiteralPath $shipped -Raw | ConvertFrom-Json
        Assert 'the shipped ledger carries the separated cleanliness block' `
            ($null -ne $doc.cleanliness -and $null -ne $doc.cleanliness.evidence_is_reproducible) `
            'docs/DWG-PROGRAM-STATE.json must be regenerated with the current schema'
        Assert 'and it no longer carries the boolean that answered the wrong question' `
            ($null -eq $doc.candidate.repo_tracked_clean_at_generation) `
            'repo_tracked_clean_at_generation conflated three questions and is gone'
    }
}
finally {
    foreach ($f in $written) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'dwg-state: the ledger describes what was measured, not what the generator did.' -ForegroundColor Green
exit 0
