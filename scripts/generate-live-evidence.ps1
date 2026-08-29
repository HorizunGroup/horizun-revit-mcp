#Requires -Version 5.1
<#
  Distill the five live-gate artifacts into the ONE file the repository is
  allowed to keep: docs/evidence/live-matrix.json.

  The full verify-live reports stay OUTSIDE version control - artifacts/ is
  ignored because they carry machine-local facts (absolute paths, process ids,
  fixture locations), and per docs/RELEASE-POLICY.md they travel as attached
  release artifacts instead. This script writes the durable, sanitized summary
  and REFUSES to write anything that is not a complete green five-year matrix
  bound to one commit: a partial or stale summary in the tree would read
  exactly like a complete one.

  Deterministic on purpose: generated_utc is the NEWEST artifact timestamp,
  not the wall clock, so re-running over the same five files writes the same
  bytes and a diff of the committed manifest shows real changes only.

  Its output is validated again, independently, by EvidenceManifestTests in
  tests/Horizun.Server.Tests - the committed file cannot drift ungreen or
  unsanitized without a test failing.
#>
[CmdletBinding()]
param(
    [string]$ArtifactsDir,
    # The installed release manifest is the authority for the server/add-in
    # hashes the artifacts were checked against.
    [string]$Manifest = (Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\manifest.json'),
    # Full 40-hex commit every artifact must be bound to. Default: the
    # manifest's own commit.
    [string]$Candidate,
    [string]$Out
)
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 evaluates parameter default expressions before
# $PSScriptRoot is reliably populated. Resolve script-relative defaults only
# after binding so the documented no-argument invocation works in both
# powershell.exe and pwsh.
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactsDir)) { $ArtifactsDir = Join-Path $repoRoot 'artifacts\live' }
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $repoRoot 'docs\evidence\live-matrix.json' }

function Fail([string]$why) {
    throw "EVIDENCE REFUSED: $why The durable manifest was NOT written."
}

$years = @(2023, 2024, 2025, 2026, 2027)

if (-not (Test-Path $Manifest)) { Fail "the release manifest does not exist at the given path." }
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
if (-not $Candidate) { $Candidate = [string]$m.Commit }
if ($Candidate -notmatch '^[0-9a-f]{40}$') { Fail "the candidate must be a full 40-hex commit, got '$Candidate'." }
if ([string]$m.Commit -ne $Candidate) { Fail "the installed manifest is at commit $($m.Commit), not the candidate $Candidate." }
if (-not $m.CleanTree) { Fail 'the installed manifest was not built from a clean tree.' }

$serverSha = ([string]$m.Server.Sha256).ToLower()
if ($serverSha -notmatch '^[0-9a-f]{64}$') { Fail 'the manifest carries no valid server SHA-256.' }

$yearRows = @()
$newestUtc = [DateTime]::MinValue
$harnessCommit = $null
$harnessGitBlob = $null
$harnessSha256 = $null

foreach ($y in $years) {
    $path = Join-Path $ArtifactsDir "live-$y.json"
    if (-not (Test-Path $path)) { Fail "the Revit $y artifact is missing: a five-year claim needs five artifacts." }
    $rawBytes = [IO.File]::ReadAllBytes($path)
    $artifactSha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($rawBytes)).Replace('-', '').ToLower()
    $text = [Text.Encoding]::UTF8.GetString($rawBytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    $r = $text | ConvertFrom-Json

    if ([int]$r.schema -lt 2) {
        Fail "live-$y.json predates harness provenance. Re-run the live gate with the current committed verify-live.ps1; historical artifacts cannot be upgraded by assertion."
    }
    $reportHarnessCommit = ([string]$r.harness_commit).ToLowerInvariant()
    $reportHarnessGitBlob = ([string]$r.harness_git_blob).ToLowerInvariant()
    $reportHarnessSha = ([string]$r.harness_sha256).ToLowerInvariant()
    if ($reportHarnessCommit -notmatch '^[0-9a-f]{40}$' -or
        $reportHarnessGitBlob -notmatch '^[0-9a-f]{40,64}$' -or
        $reportHarnessSha -notmatch '^[0-9a-f]{64}$' -or
        -not $r.harness_path_matches_repository -or
        -not $r.harness_tracked_clean -or
        [string]$r.harness_file -ne 'scripts/verify-live.ps1') {
        Fail "live-$y.json does not identify a clean, committed verify-live.ps1 harness."
    }
    if ($null -eq $harnessCommit) {
        $harnessCommit = $reportHarnessCommit
        $harnessGitBlob = $reportHarnessGitBlob
        $harnessSha256 = $reportHarnessSha
    }
    elseif ($reportHarnessCommit -ne $harnessCommit -or
            $reportHarnessGitBlob -ne $harnessGitBlob -or
            $reportHarnessSha -ne $harnessSha256) {
        Fail "live-$y.json used harness $reportHarnessCommit/$reportHarnessGitBlob/$reportHarnessSha, not the one shared by the other years ($harnessCommit/$harnessGitBlob/$harnessSha256)."
    }

    if ([int]$r.revit_year -ne $y) { Fail "live-$y.json says revit_year=$($r.revit_year)." }
    if (-not $r.release_gate) { Fail "the Revit $y run was not a release gate." }
    if ([string]$r.expected_commit -ne $Candidate) {
        Fail "the Revit $y artifact is bound to $($r.expected_commit), not the candidate $Candidate - all five must point at ONE commit."
    }
    if (([string]$r.server_sha256).ToLower() -ne $serverSha) { Fail "the Revit $y run used a server the manifest does not describe." }
    if ($r.server_is_dev_build) { Fail "the Revit $y run used a dev-build server." }

    $rows = @($r.probes)
    $names = @($rows | ForEach-Object { $_.name })
    if ((@($names | Select-Object -Unique)).Count -ne $names.Count) { Fail "the Revit $y artifact carries duplicate probe names." }
    $passRows = @($rows | Where-Object { $_.outcome -eq 'pass' })
    $badRows = @($rows | Where-Object { $_.outcome -ne 'pass' })
    $s = $r.summary
    if ([int]$s.probes -ne $rows.Count) { Fail "the Revit $y summary says probes=$($s.probes) beside $($rows.Count) rows." }
    if ([int]$s.passed -ne $passRows.Count -or $badRows.Count -ne 0 -or
        [int]$s.failed -ne 0 -or [int]$s.unverified -ne 0 -or [int]$s.not_covered -ne 0) {
        Fail "the Revit $y matrix is not green (failed=$($s.failed), unverified=$($s.unverified), not_covered=$($s.not_covered))."
    }
    foreach ($needed in @('the server binary matches the release manifest', 'the add-in binary matches the release manifest')) {
        $row = @($rows | Where-Object { $_.name -eq $needed })
        if ($row.Count -ne 1 -or $row[0].outcome -ne 'pass') { Fail "the Revit $y artifact lacks a passing '$needed' probe." }
    }

    $dims = @($r.dimensions.cases)
    $d2d = @($r.detail_2d.cases)
    $dimsPass = @($dims | Where-Object { $_.outcome -eq 'pass' }).Count
    $d2dPass = @($d2d | Where-Object { $_.outcome -eq 'pass' }).Count
    if ($dims.Count -lt 1 -or $dimsPass -ne $dims.Count) { Fail "Revit $y dimension cases: $dimsPass/$($dims.Count) passed." }
    if ($d2d.Count -lt 1 -or $d2dPass -ne $d2d.Count) { Fail "Revit $y detail-2D cases: $d2dPass/$($d2d.Count) passed." }

    # The planimetry section, held to the same bar - and split per TOOL, because
    # "planimetry is green" is two claims: the query read what was staged, and
    # the auditor judged it. A year may not claim either over the other.
    $plan = @($r.planimetry.cases)
    $planPass = @($plan | Where-Object { $_.outcome -eq 'pass' }).Count
    $planFailed = @($plan | Where-Object { $_.outcome -eq 'fail' }).Count
    $planUnverified = @($plan | Where-Object { $_.outcome -eq 'unverified' }).Count
    $planNotCovered = @($plan | Where-Object { $_.outcome -eq 'not_covered' }).Count
    if ($plan.Count -lt 1 -or $planPass -ne $plan.Count) {
        Fail "Revit $y planimetry cases: $planPass/$($plan.Count) passed (failed=$planFailed, unverified=$planUnverified, not_covered=$planNotCovered)."
    }
    $planQuery = @($plan | Where-Object { $_.tool -eq 'horizun_query_planimetry' })
    $planAudit = @($plan | Where-Object { $_.tool -eq 'horizun_audit_planimetry' })
    if ($planQuery.Count -lt 1 -or $planAudit.Count -lt 1) {
        Fail "Revit $y planimetry cases do not exercise both tools (query=$($planQuery.Count), audit=$($planAudit.Count))."
    }

    $fix = @($r.fix_planimetry.cases)
    $fixPass = @($fix | Where-Object { $_.outcome -eq 'pass' }).Count
    if ($fix.Count -lt 1 -or $fixPass -ne $fix.Count) {
        Fail "Revit $y fix-planimetry cases: $fixPass/$($fix.Count) passed."
    }

    # Autonomous production is the release claim added by this matrix. Keep
    # every branch explicit so a future harness cannot replace one capability
    # with five easy probes and still inherit the same green label.
    $production = @($r.planimetry_production.cases)
    $productionPass = @($production | Where-Object { $_.outcome -eq 'pass' }).Count
    $productionExpected = [ordered]@{
        horizun_pack_sheets      = 1
        horizun_plan_annotations = 2
        horizun_manage_revisions = 1
        horizun_capture_view      = 1
    }
    if ($production.Count -ne 5 -or $productionPass -ne $production.Count) {
        Fail "Revit $y planimetry-production cases: $productionPass/$($production.Count) passed; exactly five are required."
    }
    foreach ($tool in $productionExpected.Keys) {
        $actual = @($production | Where-Object { $_.tool -eq $tool }).Count
        if ($actual -ne [int]$productionExpected[$tool]) {
            Fail "Revit $y planimetry production expected $($productionExpected[$tool]) '$tool' case(s), found $actual."
        }
    }

    # The linked-and-production section (schema 5). Same discipline as the
    # production block above: exact per-tool counts, so a future harness cannot
    # trade one linked capability for three easy probes under the same label.
    $dp2 = @($r.dimension_production.cases)
    $dp2Pass = @($dp2 | Where-Object { $_.outcome -eq 'pass' }).Count
    $dp2Expected = [ordered]@{
        horizun_query_model              = 1
        horizun_get_dimension_references = 3
        horizun_annotate                 = 2
        horizun_query_dimensions         = 1
        horizun_plan_annotations         = 2
        horizun_plan_views               = 1
        horizun_manage_views             = 3
        horizun_manage_schedules         = 2
        horizun_manage_revisions         = 1
        horizun_health                   = 1
    }
    if ($dp2.Count -ne 18 -or $dp2Pass -ne $dp2.Count) {
        Fail "Revit $y linked-production cases: $dp2Pass/$($dp2.Count) passed; exactly eighteen are required."
    }
    foreach ($tool in $dp2Expected.Keys) {
        $actual = @($dp2 | Where-Object { $_.tool -eq $tool }).Count
        if ($actual -ne [int]$dp2Expected[$tool]) {
            Fail "Revit $y linked production expected $($dp2Expected[$tool]) '$tool' case(s), found $actual."
        }
    }

    $addin = $m.Plugins | Where-Object { [int]$_.Year -eq $y } | Select-Object -First 1
    if (-not $addin) { Fail "the release manifest has no Revit $y payload." }
    $addinSha = ([string]$addin.Sha256).ToLower()
    if ($addinSha -notmatch '^[0-9a-f]{64}$') { Fail "the manifest carries no valid Revit $y add-in SHA-256." }

    $when = [DateTime]::Parse([string]$r.generated_utc, [Globalization.CultureInfo]::InvariantCulture,
                              [Globalization.DateTimeStyles]::AdjustToUniversal)
    if ($when -gt $newestUtc) { $newestUtc = $when }

    $yearRows += [ordered]@{
        revit_year             = $y
        revit_build            = [string]$r.dimensions.revit_build
        artifact_sha256        = $artifactSha
        artifact_generated_utc = $when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        probes                 = [int]$s.probes
        actual_probes          = $rows.Count
        asserting              = [int]$s.asserting
        passed                 = [int]$s.passed
        failed                 = [int]$s.failed
        unverified             = [int]$s.unverified
        not_covered            = [int]$s.not_covered
        dimension_cases        = [ordered]@{ passed = $dimsPass; total = $dims.Count }
        detail_2d_cases        = [ordered]@{ passed = $d2dPass; total = $d2d.Count }
        fix_planimetry_cases   = [ordered]@{ passed = $fixPass; total = $fix.Count }
        planimetry             = [ordered]@{
            passed          = $planPass
            total           = $plan.Count
            failed          = $planFailed
            unverified      = $planUnverified
            not_covered     = $planNotCovered
            query_coverage  = [ordered]@{ passed = @($planQuery | Where-Object { $_.outcome -eq 'pass' }).Count; total = $planQuery.Count }
            audit_coverage  = [ordered]@{ passed = @($planAudit | Where-Object { $_.outcome -eq 'pass' }).Count; total = $planAudit.Count }
        }
        planimetry_production = [ordered]@{
            passed = $productionPass
            total  = $production.Count
            tools  = [ordered]@{
                horizun_pack_sheets       = @($production | Where-Object { $_.tool -eq 'horizun_pack_sheets' -and $_.outcome -eq 'pass' }).Count
                horizun_plan_annotations  = @($production | Where-Object { $_.tool -eq 'horizun_plan_annotations' -and $_.outcome -eq 'pass' }).Count
                horizun_manage_revisions  = @($production | Where-Object { $_.tool -eq 'horizun_manage_revisions' -and $_.outcome -eq 'pass' }).Count
                horizun_capture_view       = @($production | Where-Object { $_.tool -eq 'horizun_capture_view' -and $_.outcome -eq 'pass' }).Count
            }
        }
        dimension_production = [ordered]@{
            passed = $dp2Pass
            total  = $dp2.Count
            tools  = $(
                $dp2Tools = [ordered]@{}
                foreach ($tool in $dp2Expected.Keys) {
                    $dp2Tools[$tool] = @($dp2 | Where-Object { $_.tool -eq $tool -and $_.outcome -eq 'pass' }).Count
                }
                $dp2Tools
            )
        }
        addin_sha256           = $addinSha
    }
}

# The evidence generator itself must still be looking at the same harness bytes
# the reports name. This prevents a later harness edit from being presented as
# the reproducible source of an older matrix.
$currentHarness = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\verify-live.ps1'
if (-not (Test-Path $currentHarness)) { Fail 'scripts/verify-live.ps1 is missing.' }
$currentHarnessSha = (Get-FileHash -LiteralPath $currentHarness -Algorithm SHA256).Hash.ToLowerInvariant()
if ($currentHarnessSha -ne $harnessSha256) {
    Fail "the current verify-live.ps1 SHA-256 is $currentHarnessSha, but the five reports used $harnessSha256. Generate evidence from the exact harness that ran."
}
$harnessSpec = $harnessCommit + ':scripts/verify-live.ps1'
$committedHarnessBlobLines = @(& git -C (Split-Path -Parent $PSScriptRoot) rev-parse $harnessSpec 2>$null)
if ($LASTEXITCODE -ne 0 -or $committedHarnessBlobLines.Count -ne 1 -or
    ([string]$committedHarnessBlobLines[0]).ToLowerInvariant() -ne $harnessGitBlob) {
    Fail "the recorded harness blob $harnessGitBlob is not scripts/verify-live.ps1 at commit $harnessCommit."
}

$doc = [ordered]@{
    schema           = 5
    generated_utc    = $newestUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    candidate_commit = $Candidate
    harness_commit   = $harnessCommit
    harness_git_blob = $harnessGitBlob
    harness_sha256   = $harnessSha256
    generator        = 'scripts/generate-live-evidence.ps1'
    originals        = 'The full verify-live reports are NOT stored in Git: artifacts/ is ignored because they carry machine-local paths. They remain local run evidence and, per docs/RELEASE-POLICY.md, are attached to each release as release artifacts; the SHA-256 here pins each one byte-for-byte.'
    server_sha256    = $serverSha
    years            = $yearRows
}

$json = ($doc | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"

# The whole point of this file is that it can live in a public tree. Refuse to
# write anything that smells machine-local, whatever future field slipped it in.
foreach ($forbidden in @('(?i)[a-z]:[\\/]', '(?i)users[\\/]', '(?i)onedrive', '(?i)%userprofile%',
                         '(?i)\.rvt\b', '(?i)\.rfa\b', '"pid"', '(?i)appdata')) {
    if ($json -match $forbidden) { Fail "the rendered manifest matches the forbidden pattern '$forbidden' - machine-local data must not be versioned." }
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[IO.File]::WriteAllText($Out, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "wrote $Out (candidate $($Candidate.Substring(0,9)), $($yearRows.Count) years, all green)"
