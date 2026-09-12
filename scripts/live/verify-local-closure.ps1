#Requires -Version 7.0
<#
  IS THIS CLOSURE ACTUALLY CLOSED?

  Every harness already answers for itself, and none of them answers this: a year
  can finish 'green' while the one thing it was supposed to prove came back
  fixture_missing, because a harness that could not find its fixture has not
  failed - it has not measured. That reading is right for a harness and wrong for
  a closure, so the judgement lives here instead of being smuggled into exit
  codes that other runs depend on. NOTHING in the harnesses changes: this reads
  their artifacts.

  It takes a REQUIREMENTS file naming what this particular closure must have
  covered, and answers 'complete' only when every one of them is satisfied. A
  required case that is failed, blocked, fixture_missing, not_covered or simply
  absent makes the answer 'pending', and says which.

  Three kinds of evidence are kept apart, because they are not the same thing:

    * AUTOMATIC - a probe a machine measured (kind: offline_suite, year_matrix,
      harness_probe). Only 'passed' counts.
    * VISUAL - a page a person or agent looked at (kind: visual). The record must
      name the SHA-256 of the very PDF the harness produced, so an acceptance
      cannot drift away from the file it was given; a review of a different file
      is not a review of this one. It must also name the full-sheet render AND
      the detail render, and carry the whole page's measured overlapping word
      pairs, which must be zero - because a reviewer sees the crop they chose,
      and a collision anywhere else on the sheet is still a collision.
    * INTENTIONAL REFUSAL - a run that was supposed to be refused (kind:
      intentional_refusal): the driver meeting a document it never opened. It
      counts as satisfied only with ALL of its conditions - the refusal, an empty
      closed_documents, the named document, a recovery record written - AND a
      recorded recovery afterwards that closed the session and put the manifest
      back. A refusal with no recovery is not a pass; it is a machine left open.

      pwsh -File verify-local-closure.ps1 -EvidenceRoot <dir> [-Requirements <json>]
      exit 0 = complete   1 = pending   2 = the requirements themselves are unusable
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$Requirements = (Join-Path $PSScriptRoot 'local-closure.requirements.json'),
    [string]$Out
)
$ErrorActionPreference = 'Stop'

function Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { if ($Object.Contains($Name)) { return $Object[$Name] } return $null }
    $names = @(); try { $names = @($Object.PSObject.Properties.Name) } catch { $names = @() }
    if ($names -contains $Name) { return $Object.$Name }
    return $null
}
function ReadJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}
function NewestUnder([string]$Dir, [string]$Filter) {
    if (-not (Test-Path -LiteralPath $Dir)) { return $null }
    $found = @(Get-ChildItem -LiteralPath $Dir -Filter $Filter -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($found.Count -eq 0) { return $null }
    return $found[0].FullName
}
# One row per requirement; 'satisfied' is the only word that counts.
$results = @()
function Verdict([string]$Id, [string]$Kind, [bool]$Satisfied, [string]$Because, $Evidence = $null) {
    $script:results += [ordered]@{ id = $Id; kind = $Kind; satisfied = $Satisfied; because = $Because; evidence = $Evidence }
}

$spec = ReadJson $Requirements
if (-not $spec -or -not (Field $spec 'requirements')) {
    Write-Host "the requirements file is missing or unreadable: $Requirements" -ForegroundColor Red
    exit 2
}
$visualRecord = $null
$visualPath = Field $spec 'visual_acceptance'
if ($visualPath) {
    $resolved = Join-Path $EvidenceRoot $visualPath
    $visualRecord = ReadJson $resolved
}

foreach ($req in @(Field $spec 'requirements')) {
    $id = [string](Field $req 'id')
    $kind = [string](Field $req 'kind')
    $root = [string](Field $req 'root')
    $dir = if ($root) { Join-Path $EvidenceRoot $root } else { $EvidenceRoot }
    switch ($kind) {

        # ---- the offline driver-safety suite: passed only, and SKIPPED IS NOT PASSED
        'offline_suite' {
            $path = Join-Path $EvidenceRoot ([string](Field $req 'summary'))
            $s = ReadJson $path
            if (-not $s) { Verdict $id $kind $false "no summary at $path"; break }
            $failed = [int](Field $s 'failed'); $skipped = [int](Field $s 'skipped'); $passed = [int](Field $s 'passed')
            $ok = ($failed -eq 0) -and ($skipped -eq 0) -and ($passed -gt 0)
            $why = "$passed passed, $failed failed, $skipped skipped"
            if ($skipped -gt 0) { $why += " - a skipped case is not a covered case: " + ((@(Field $s 'skipped_cases')) -join '; ') }
            Verdict $id $kind $ok $why @{ summary = $path; commit = (Field $s 'repo_head') }
        }

        # ---- a year of the matrix: green, closed by itself, restored and verified
        'year_matrix' {
            $year = [string](Field $req 'year')
            $summaryPath = NewestUnder $dir 'year-matrix-*.json'
            if (-not $summaryPath) { Verdict $id $kind $false "no year-matrix summary under $dir"; break }
            $sum = ReadJson $summaryPath
            $row = @(@(Field $sum 'years') | Where-Object { [string](Field $_ 'year') -eq $year })
            if ($row.Count -ne 1) { Verdict $id $kind $false "no row for $year in $summaryPath"; break }
            $row = $row[0]
            $close = Field $row 'close'; $restore = Field $row 'restore'
            $closeState = [string](Field $close 'state')
            $restoreState = [string](Field $restore 'state')
            $restoreOk = [bool](Field $restore 'ok')
            $shaStart = [string](Field $row 'installed_dll_sha256_at_start')
            $shaEnd = [string](Field $restore 'installed_dll_sha256')
            $problems = @()
            if ([string](Field $row 'state') -ne 'green') { $problems += ("row state is '" + [string](Field $row 'state') + "'") }
            if ($closeState -notin @('closed', 'already_exited')) { $problems += "close state is '$closeState'" }
            if (-not $restoreOk) { $problems += "restore not ok ('$restoreState')" }
            if ($restoreState -notin @('restored', 'nothing_to_restore')) { $problems += "restore state is '$restoreState'" }
            # The installed DLL has to be the one that was there before the run. When
            # nothing had to be restored the restore does not re-publish the hash, and
            # its own check already compared them - so only compare when both are known.
            if ($shaStart -and $shaEnd -and ($shaStart -ne $shaEnd)) { $problems += "installed DLL changed ($shaStart -> $shaEnd)" }
            if (Field $row 'recovery_pending') { $problems += ("a recovery is pending: " + [string](Field $row 'recovery_pending')) }
            Verdict $id $kind ($problems.Count -eq 0) `
                $(if ($problems.Count -eq 0) { "green; closed '$closeState'; restore '$restoreState'; installed DLL unchanged" } else { $problems -join '; ' }) `
                @{ summary = $summaryPath }
        }

        # ---- one probe of one harness: passed, never merely present
        'harness_probe' {
            $probeId = [string](Field $req 'probe')
            $pattern = [string](Field $req 'artifact')
            if (-not $pattern) { $pattern = '*.json' }
            $artifact = NewestUnder $dir $pattern
            if (-not $artifact) { Verdict $id $kind $false "no artifact matching '$pattern' under $dir"; break }
            $a = ReadJson $artifact
            $probe = @(@(Field $a 'probes') | Where-Object { [string](Field $_ 'id') -eq $probeId })
            if ($probe.Count -eq 0) { Verdict $id $kind $false "the artifact has no probe '$probeId' ($artifact)"; break }
            $status = [string](Field $probe[0] 'status')
            Verdict $id $kind ($status -eq 'passed') `
                ("probe '$probeId' is '$status'" + $(if ($status -ne 'passed') { " - " + [string](Field $probe[0] 'because') } else { '' })) `
                @{ artifact = $artifact; candidate = (Field $a 'code_candidate_commit') }
        }

        # ---- a page somebody looked at, bound to the file they were given
        'visual' {
            $year = [string](Field $req 'year')
            if (-not $visualRecord) { Verdict $id $kind $false 'no visual-acceptance record was declared or found'; break }
            $entry = @(@(Field $visualRecord 'reviews') | Where-Object { [string](Field $_ 'year') -eq $year })
            if ($entry.Count -ne 1) { Verdict $id $kind $false "no visual review recorded for $year"; break }
            $entry = $entry[0]
            $verdictText = [string](Field $entry 'verdict')
            $reviewedSha = ([string](Field $entry 'pdf_sha256')).ToLower()
            # The PDF the harness actually produced, so an acceptance cannot be of
            # some other page.
            $artifact = NewestUnder $dir ([string](Field $req 'artifact'))
            $producedSha = $null
            if ($artifact) {
                $a = ReadJson $artifact
                foreach ($p in @(Field $a 'probes')) {
                    if ([string](Field $p 'id') -eq 'sheet-exported-to-pdf') {
                        $ev = Field $p 'evidence'
                        $m = [regex]::Match([string]$ev, '"sha256"\s*:\s*"([0-9a-f]{64})"')
                        if ($m.Success) { $producedSha = $m.Groups[1].Value }
                    }
                }
            }
            $problems = @()
            if ($verdictText -ne 'accepted') { $problems += "the review says '$verdictText'" }
            if (-not $reviewedSha) { $problems += 'the review names no PDF hash' }
            if ($producedSha -and $reviewedSha -and ($producedSha -ne $reviewedSha)) {
                $problems += "the review is of another file (reviewed $reviewedSha, the run produced $producedSha)"
            }
            if (-not $producedSha) { $problems += 'the run published no PDF hash to compare the review against' }
            # "NO OBVIOUS OVERLAPS" IS A MEASUREMENT OF THE WHOLE PAGE, not an
            # impression of the crop the reviewer chose. A sheet name printed
            # straight through the titleblock on five pages that had all been
            # accepted, because the reviewer looked at the detail and the record
            # only ever asked whether a word had run off the paper (2026-09-09).
            # The count comes from scripts/render-pdf.py over the entire sheet.
            $overlaps = Field $entry 'whole_page_overlaps'
            if ($null -eq $overlaps) { $problems += 'the review does not say how many overlapping word pairs the whole page has' }
            elseif ([int]$overlaps -ne 0) { $problems += "the page has $overlaps overlapping word pair(s)" }
            # AND BOTH THINGS HAVE TO HAVE BEEN LOOKED AT. The detail answers 'is the
            # annotation right'; the full sheet answers 'does the page print'.
            foreach ($needed in 'render_full_sheet', 'render_detail') {
                if (-not [string](Field $entry $needed)) { $problems += "the review names no $needed" }
            }
            Verdict $id $kind ($problems.Count -eq 0) `
                $(if ($problems.Count -eq 0) { "accepted by " + [string](Field $entry 'reviewer') + ", against the PDF the run produced; whole page measured at 0 overlapping word pairs; full sheet and detail both looked at" } else { $problems -join '; ' }) `
                @{ artifact = $artifact; reviewed_sha256 = $reviewedSha }
        }

        # ---- the refusal that was the point, and the recovery that must follow it
        'intentional_refusal' {
            $year = [string](Field $req 'year')
            $expectDoc = [string](Field $req 'document')
            $summaryPath = NewestUnder $dir 'year-matrix-*.json'
            if (-not $summaryPath) { Verdict $id $kind $false "no year-matrix summary under $dir"; break }
            $sum = ReadJson $summaryPath
            $row = @(@(Field $sum 'years') | Where-Object { [string](Field $_ 'year') -eq $year })
            if ($row.Count -ne 1) { Verdict $id $kind $false "no row for $year in $summaryPath"; break }
            $row = $row[0]
            $close = Field $row 'close'
            $problems = @()
            if ([string](Field $close 'state') -ne 'left_running_foreign_document') {
                $problems += ("close state is '" + [string](Field $close 'state') + "', not left_running_foreign_document")
            }
            if (@(Field $close 'closed_documents').Count -ne 0) {
                $problems += ("closed_documents is not empty: " + ((@(Field $close 'closed_documents')) -join ', '))
            }
            $foreignTitles = @(@(Field $close 'foreign') | ForEach-Object { [string](Field $_ 'title') })
            if ($expectDoc -and ($foreignTitles -notcontains $expectDoc)) {
                $problems += ("the refusal does not name '$expectDoc' (it names: " + ($foreignTitles -join ', ') + ')')
            }
            if (-not (Field $row 'recovery_pending')) { $problems += 'no recovery record was written' }
            # AND THE RECOVERY. A protection that leaves a machine open is only half
            # the story; the closure needs the other half on the record.
            $recPath = [string](Field $req 'recovery')
            $rec = if ($recPath) { ReadJson (Join-Path $EvidenceRoot $recPath) } else { $null }
            if (-not $rec) { $problems += "no recovery record at '$recPath'" }
            else {
                $rc = Field $rec 'close'; $rr = Field $rec 'restore'
                if ([string](Field $rc 'state') -ne 'closed') { $problems += ("the recovery did not close the session (" + [string](Field $rc 'state') + ')') }
                if (-not [bool](Field $rr 'ok')) { $problems += ("the recovery did not restore the manifest (" + [string](Field $rr 'state') + ')') }
            }
            Verdict $id $kind ($problems.Count -eq 0) `
                $(if ($problems.Count -eq 0) { "refused as designed, closed nothing, and the session was recovered and verified afterwards" } else { $problems -join '; ' }) `
                @{ summary = $summaryPath; recovery = $recPath }
        }

        default { Verdict $id $kind $false "unknown requirement kind '$kind'" }
    }
}

$kinds = @{ offline_suite = 'automatic'; year_matrix = 'automatic'; harness_probe = 'automatic'
            visual = 'visual review'; intentional_refusal = 'intentional refusal' }
Write-Host ''
Write-Host ("== local closure: " + [string](Field $spec 'title') + " ==") -ForegroundColor Cyan
foreach ($group in 'automatic', 'visual review', 'intentional refusal') {
    $rows = @($results | Where-Object { $kinds[$_.kind] -eq $group })
    if ($rows.Count -eq 0) { continue }
    Write-Host ("  -- {0} --" -f $group) -ForegroundColor DarkCyan
    foreach ($r in $rows) {
        Write-Host ("  {0,-9} {1,-26} {2}" -f $(if ($r.satisfied) { 'SATISFIED' } else { 'PENDING' }), $r.id, $r.because) `
            -ForegroundColor $(if ($r.satisfied) { 'Green' } else { 'Red' })
    }
}
$pending = @($results | Where-Object { -not $_.satisfied })
$complete = ($pending.Count -eq 0)
Write-Host ''
Write-Host ("  {0}" -f $(if ($complete) { 'COMPLETE - every required case of this closure is covered' }
                         else { "PENDING - $($pending.Count) required case(s) not covered: " + (($pending | ForEach-Object { $_.id }) -join ', ') })) `
    -ForegroundColor $(if ($complete) { 'Green' } else { 'Red' })

if ($Out) {
    ([ordered]@{
        schema = 'horizun.local-closure.result/1'
        title = (Field $spec 'title')
        evaluated_utc = (Get-Date).ToUniversalTime().ToString('o')
        evidence_root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
        requirements_file = (Resolve-Path -LiteralPath $Requirements).Path
        complete = $complete
        pending = @($pending | ForEach-Object { $_.id })
        results = $results
    } | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $Out -Encoding utf8
    Write-Host ("  written: {0}" -f $Out) -ForegroundColor DarkGray
}
exit $(if ($complete) { 0 } else { 1 })
