# -----------------------------------------------------------------------------
# Horizun Revit MCP — original Horizun code.
#
# The closure evaluator, exercised against SYNTHETIC evidence trees under %TEMP%.
# No Revit, no manifest, no installation: every artifact here is written by this
# file and thrown away by it.
#
# What it is guarding: a harness that could not find its fixture has not failed,
# so a year goes green with the very thing it was meant to prove recorded as
# fixture_missing. The evaluator exists to refuse that reading for a CLOSURE, and
# these cases are the proof that it refuses it - including the shapes that are
# easy to get wrong: a skipped offline case, a visual acceptance of a different
# file, and an intentional refusal with no recovery behind it.
#
#   pwsh -NoProfile -File scripts/live/verify-local-closure.tests.ps1
#   exit 0 = every case held; 1 = at least one did not.
# -----------------------------------------------------------------------------
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Passed = 0; $script:Failed = 0
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:Passed++; Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green }
    else { $script:Failed++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) -ForegroundColor Red }
}
$evaluator = Join-Path $PSScriptRoot 'verify-local-closure.ps1'
$tmp = Join-Path $env:TEMP ('hz-closure-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function WriteJson([string]$Path, $Body) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    ($Body | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $Path -Encoding utf8
}
$pdfSha = ('a' * 64)

function NewEvidence {
    <#
      A complete, satisfying evidence tree. Every case below takes this and
      breaks exactly one thing, so a failure names one cause.
    #>
    param([string]$Root)
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    WriteJson (Join-Path $Root 'session-tests-summary.json') ([ordered]@{
        passed = 136; failed = 0; skipped = 0; skipped_cases = @(); repo_head = 'deadbeef' })
    # a clean year
    WriteJson (Join-Path $Root 'ym-2024\year-matrix-20260909.json') ([ordered]@{
        schema = 'horizun.year-matrix/1'
        years = @([ordered]@{
            year = '2024'; state = 'green'
            installed_dll_sha256_at_start = 'cafe'
            close = [ordered]@{ state = 'closed'; closed_documents = @('HZ_X') }
            restore = [ordered]@{ ok = $true; state = 'restored'; installed_dll_sha256 = 'cafe' } }) })
    WriteJson (Join-Path $Root 'ym-2024\deliverable-visual-1.json') ([ordered]@{
        code_candidate_commit = 'deadbeef'
        probes = @(
            [ordered]@{ id = 'dimension-text-displaced'; status = 'passed' },
            [ordered]@{ id = 'tag-with-leader'; status = 'passed' },
            [ordered]@{ id = 'sheet-exported-to-pdf'; status = 'passed'
                        evidence = ('{"delivery":{"files":[{"sha256":"' + $pdfSha + '"}]}}') }) })
    # the intentional refusal, with its recovery
    WriteJson (Join-Path $Root 'ym-foreign\year-matrix-20260909.json') ([ordered]@{
        years = @([ordered]@{
            year = '2026'; state = 'recovery_pending'
            recovery_pending = 'C:\evidence\recovery-pending-2026.json'
            close = [ordered]@{ state = 'left_running_foreign_document'; closed_documents = @()
                                foreign = @([ordered]@{ title = 'HZ_DESECHABLE_X'; why = 'this run did not open it' }) }
            restore = [ordered]@{ ok = $false; state = 'deferred_revit_running' } }) })
    WriteJson (Join-Path $Root 'ym-foreign\recovery\recovery.json') ([ordered]@{
        close = [ordered]@{ state = 'closed'; closed_documents = @('HZ_WRITE3', 'HZ_DESECHABLE_X') }
        restore = [ordered]@{ ok = $true; state = 'restored' } })
    WriteJson (Join-Path $Root 'visual-acceptance.json') ([ordered]@{
        reviews = @([ordered]@{ year = '2024'; verdict = 'accepted'; reviewer = 'agent'; pdf_sha256 = $pdfSha
                                whole_page_overlaps = 0
                                render_full_sheet = 'visual-2024/page-001-150dpi.png'
                                render_detail = 'visual-2024/page-001-clip-400dpi.png' }) })
}
function Requirements([string]$Path) {
    WriteJson $Path ([ordered]@{
        schema = 'horizun.local-closure/1'; title = 'test'; visual_acceptance = 'visual-acceptance.json'
        requirements = @(
            [ordered]@{ id = 'suite'; kind = 'offline_suite'; summary = 'session-tests-summary.json' },
            [ordered]@{ id = 'y2024'; kind = 'year_matrix'; root = 'ym-2024'; year = '2024' },
            [ordered]@{ id = 'y2024-tag'; kind = 'harness_probe'; root = 'ym-2024'; artifact = 'deliverable-visual-*.json'; probe = 'tag-with-leader' },
            [ordered]@{ id = 'y2024-visual'; kind = 'visual'; root = 'ym-2024'; artifact = 'deliverable-visual-*.json'; year = '2024' },
            [ordered]@{ id = 'foreign'; kind = 'intentional_refusal'; root = 'ym-foreign'; year = '2026'
                        document = 'HZ_DESECHABLE_X'; recovery = 'ym-foreign/recovery/recovery.json' }) })
}
function RunEvaluator([string]$Root) {
    $req = Join-Path $Root 'requirements.json'
    Requirements $req
    $out = Join-Path $Root 'result.json'
    $null = & pwsh -NoProfile -File $evaluator -EvidenceRoot $Root -Requirements $req -Out $out 2>&1
    $code = $LASTEXITCODE
    $result = if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } else { $null }
    return @{ code = $code; result = $result }
}
function Pending($Run, [string]$Id) { return (@($Run.result.pending) -contains $Id) }

Write-Host '== local closure evaluator ==' -ForegroundColor Cyan
try {
    # 1. Everything covered.
    $root = Join-Path $tmp 'ok'; NewEvidence $root
    $r = RunEvaluator $root
    Check 'a complete evidence tree is COMPLETE and exits 0' (($r.code -eq 0) -and $r.result.complete) ("exit $($r.code)")

    # 2-4. A required probe that did not measure is never a pass.
    foreach ($status in 'fixture_missing', 'not_covered', 'failed') {
        $root = Join-Path $tmp ('probe-' + $status); NewEvidence $root
        $a = Get-Content -LiteralPath (Join-Path $root 'ym-2024\deliverable-visual-1.json') -Raw | ConvertFrom-Json
        foreach ($p in $a.probes) { if ($p.id -eq 'tag-with-leader') { $p.status = $status } }
        WriteJson (Join-Path $root 'ym-2024\deliverable-visual-1.json') $a
        $r = RunEvaluator $root
        Check ("a required probe recorded '$status' leaves the closure PENDING") (($r.code -eq 1) -and (-not $r.result.complete) -and (Pending $r 'y2024-tag')) ("exit $($r.code)")
    }

    # 5. A skipped offline case is not a covered case.
    $root = Join-Path $tmp 'suite-skipped'; NewEvidence $root
    WriteJson (Join-Path $root 'session-tests-summary.json') ([ordered]@{
        passed = 133; failed = 0; skipped = 3; skipped_cases = @('the REAL probe ...'); repo_head = 'deadbeef' })
    $r = RunEvaluator $root
    Check 'skipped offline cases leave the closure PENDING and are named' (
        ($r.code -eq 1) -and (Pending $r 'suite') -and
        (@($r.result.results | Where-Object { $_.id -eq 'suite' })[0].because -match 'not a covered case')) ("exit $($r.code)")

    # 6-8. A year that did not end the way a year must.
    $root = Join-Path $tmp 'year-restore'; NewEvidence $root
    $s = Get-Content -LiteralPath (Join-Path $root 'ym-2024\year-matrix-20260909.json') -Raw | ConvertFrom-Json
    $s.years[0].restore.ok = $false; $s.years[0].restore.state = 'restore_failed'
    WriteJson (Join-Path $root 'ym-2024\year-matrix-20260909.json') $s
    $r = RunEvaluator $root
    Check 'a year whose manifest was not restored leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'y2024'))

    $root = Join-Path $tmp 'year-recovery'; NewEvidence $root
    $s = Get-Content -LiteralPath (Join-Path $root 'ym-2024\year-matrix-20260909.json') -Raw | ConvertFrom-Json
    $s.years[0] | Add-Member -NotePropertyName recovery_pending -NotePropertyValue 'C:\x.json' -Force
    WriteJson (Join-Path $root 'ym-2024\year-matrix-20260909.json') $s
    $r = RunEvaluator $root
    Check 'a year with a pending recovery leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'y2024'))

    $root = Join-Path $tmp 'year-dll'; NewEvidence $root
    $s = Get-Content -LiteralPath (Join-Path $root 'ym-2024\year-matrix-20260909.json') -Raw | ConvertFrom-Json
    $s.years[0].restore.installed_dll_sha256 = 'beef'
    WriteJson (Join-Path $root 'ym-2024\year-matrix-20260909.json') $s
    $r = RunEvaluator $root
    Check 'a year whose installed DLL changed leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'y2024'))

    # 9. The intentional refusal, complete, is APPROVED.
    $root = Join-Path $tmp 'ok2'; NewEvidence $root
    $r = RunEvaluator $root
    Check 'the intentional refusal with its recovery counts as satisfied' (
        (@($r.result.results | Where-Object { $_.id -eq 'foreign' })[0].satisfied -eq $true))

    # 10. A refusal with no recovery is a machine left open, not a pass.
    $root = Join-Path $tmp 'foreign-no-recovery'; NewEvidence $root
    Remove-Item -LiteralPath (Join-Path $root 'ym-foreign\recovery\recovery.json') -Force
    $r = RunEvaluator $root
    Check 'a refusal with no recovery record leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'foreign'))

    # 11. A refusal that closed something is not the refusal that was asked for.
    $root = Join-Path $tmp 'foreign-closed'; NewEvidence $root
    $s = Get-Content -LiteralPath (Join-Path $root 'ym-foreign\year-matrix-20260909.json') -Raw | ConvertFrom-Json
    $s.years[0].close.closed_documents = @('HZ_WRITE3')
    WriteJson (Join-Path $root 'ym-foreign\year-matrix-20260909.json') $s
    $r = RunEvaluator $root
    Check 'a refusal that closed a document leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'foreign'))

    # 11b. A recovery that did not restore the manifest is not a recovery.
    $root = Join-Path $tmp 'foreign-restore'; NewEvidence $root
    WriteJson (Join-Path $root 'ym-foreign\recovery\recovery.json') ([ordered]@{
        close = [ordered]@{ state = 'closed' }; restore = [ordered]@{ ok = $false; state = 'deferred_revit_running' } })
    $r = RunEvaluator $root
    Check 'a recovery that left the manifest swapped leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'foreign'))

    # 12-14. The visual review is bound to the file it reviewed.
    $root = Join-Path $tmp 'visual-other-file'; NewEvidence $root
    WriteJson (Join-Path $root 'visual-acceptance.json') ([ordered]@{
        reviews = @([ordered]@{ year = '2024'; verdict = 'accepted'; reviewer = 'agent'; pdf_sha256 = ('b' * 64) }) })
    $r = RunEvaluator $root
    Check 'a visual acceptance of ANOTHER file leaves the closure PENDING' (
        ($r.code -eq 1) -and (Pending $r 'y2024-visual') -and
        (@($r.result.results | Where-Object { $_.id -eq 'y2024-visual' })[0].because -match 'another file'))

    $root = Join-Path $tmp 'visual-missing'; NewEvidence $root
    WriteJson (Join-Path $root 'visual-acceptance.json') ([ordered]@{ reviews = @() })
    $r = RunEvaluator $root
    Check 'a year with no visual review recorded leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'y2024-visual'))

    $root = Join-Path $tmp 'visual-rejected'; NewEvidence $root
    WriteJson (Join-Path $root 'visual-acceptance.json') ([ordered]@{
        reviews = @([ordered]@{ year = '2024'; verdict = 'rejected'; reviewer = 'agent'; pdf_sha256 = $pdfSha }) })
    $r = RunEvaluator $root
    Check 'a visual review that says rejected leaves the closure PENDING' (($r.code -eq 1) -and (Pending $r 'y2024-visual'))

    # "No obvious overlaps" is the whole page, measured. These three are the shapes
    # that let a sheet name print through a titleblock across five accepted pages.
    $root = Join-Path $tmp 'visual-overlaps'; NewEvidence $root
    WriteJson (Join-Path $root 'visual-acceptance.json') ([ordered]@{
        reviews = @([ordered]@{ year = '2024'; verdict = 'accepted'; reviewer = 'agent'; pdf_sha256 = $pdfSha
                                whole_page_overlaps = 7
                                render_full_sheet = 'full.png'; render_detail = 'detail.png' }) })
    $r = RunEvaluator $root
    Check 'an accepted page that MEASURES overlapping words leaves the closure PENDING' (
        ($r.code -eq 1) -and (Pending $r 'y2024-visual') -and
        (@($r.result.results | Where-Object { $_.id -eq 'y2024-visual' })[0].because -match 'overlapping word pair'))

    $root = Join-Path $tmp 'visual-unmeasured'; NewEvidence $root
    WriteJson (Join-Path $root 'visual-acceptance.json') ([ordered]@{
        reviews = @([ordered]@{ year = '2024'; verdict = 'accepted'; reviewer = 'agent'; pdf_sha256 = $pdfSha
                                render_full_sheet = 'full.png'; render_detail = 'detail.png' }) })
    $r = RunEvaluator $root
    Check 'an acceptance that never measured the overlaps leaves the closure PENDING' (
        ($r.code -eq 1) -and (Pending $r 'y2024-visual') -and
        (@($r.result.results | Where-Object { $_.id -eq 'y2024-visual' })[0].because -match 'how many overlapping'))

    $root = Join-Path $tmp 'visual-detail-only'; NewEvidence $root
    WriteJson (Join-Path $root 'visual-acceptance.json') ([ordered]@{
        reviews = @([ordered]@{ year = '2024'; verdict = 'accepted'; reviewer = 'agent'; pdf_sha256 = $pdfSha
                                whole_page_overlaps = 0; render_detail = 'detail.png' }) })
    $r = RunEvaluator $root
    Check 'a review of the DETAIL alone, with no full sheet, leaves the closure PENDING' (
        ($r.code -eq 1) -and (Pending $r 'y2024-visual') -and
        (@($r.result.results | Where-Object { $_.id -eq 'y2024-visual' })[0].because -match 'render_full_sheet'))

    # 15. Absent evidence is never silently satisfied.
    $root = Join-Path $tmp 'no-artifact'; NewEvidence $root
    Remove-Item -LiteralPath (Join-Path $root 'ym-2024\deliverable-visual-1.json') -Force
    $r = RunEvaluator $root
    Check 'a missing artifact leaves its requirements PENDING' (($r.code -eq 1) -and (Pending $r 'y2024-tag'))

    $root = Join-Path $tmp 'no-year'; NewEvidence $root
    Remove-Item -LiteralPath (Join-Path $root 'ym-2024\year-matrix-20260909.json') -Force
    $r = RunEvaluator $root
    Check 'a missing year-matrix summary leaves its year PENDING' (($r.code -eq 1) -and (Pending $r 'y2024'))

    # 16. The evaluator changes nothing about how the harnesses report.
    $lib = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'horizun-live.lib.ps1') -Raw
    Check 'Complete-HzRun still fails only on failed probes (harness exit codes unchanged)' (
        $lib -match 'ExitCode = \$\(if \(\$bad\) \{ 1 \} else \{ 0 \}\)')
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ("  {0} passed, {1} failed" -f $script:Passed, $script:Failed) -ForegroundColor $(if ($script:Failed -eq 0) { 'Green' } else { 'Red' })
exit $(if ($script:Failed -eq 0) { 0 } else { 1 })
