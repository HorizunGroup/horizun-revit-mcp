#Requires -Version 5.1
<#
  REBUILD THE MATRIX FROM THE ARTIFACTS ALONE.

  The runner also prints a roll-up, but that one is computed from state held in
  memory - and memory is exactly what a terminating Revit takes with it. This
  script never runs the campaign and never talks to Revit. It reads the
  `case-NN-final.json` files a run left on disk and recomputes the buckets from
  them, so a run that died at case 40 still yields the 39 answers it earned.

  It is also the independent recount the evidence audit needs: if this disagrees
  with what the runner printed, one of the two is wrong and the disagreement is
  the finding.

  Any case with no artifact is `not_run`. That is the whole point: an ABSENT
  artifact can never become a pass, because a pass has to come from a file that
  says so.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDir,
    [string]$OutFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $RunDir)) { throw "no such run directory: $RunDir" }

$BUCKETS = @('passed', 'failed', 'unverified', 'not_run',
             'blocked_fixture', 'blocked_environment', 'unsupported_api')

$rows = @{}
$problems = @()

foreach ($f in Get-ChildItem -LiteralPath $RunDir -Filter 'case-*-final.json' -File) {
    try { $row = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json }
    catch { $problems += ("unreadable artifact: " + $f.Name); continue }

    $n = [int]$row.case
    if ($n -lt 1 -or $n -gt 55) {
        # Case 0 is the canary, which is deliberately NOT one of the 55.
        if ($n -ne 0) { $problems += ("artifact for out-of-range case " + $n + ": " + $f.Name) }
        continue
    }
    if ($rows.ContainsKey($n)) { $problems += ("two artifacts claim case " + $n) }
    if ($BUCKETS -notcontains [string]$row.status) {
        $problems += ("case " + $n + " has an unknown status '" + $row.status + "'")
        continue
    }
    # A 'failed' row has to point at something a reader can open, and a row that
    # is not a pass has to say why. Rows that cannot do either are downgraded to
    # not_run rather than counted: an unsupported claim is not evidence.
    $hasEvidence = ($row.PSObject.Properties.Name -contains 'evidence' -and $row.evidence) -or
                   ($row.PSObject.Properties.Name -contains 'observed' -and $row.observed)
    if ([string]$row.status -eq 'failed' -and -not $hasEvidence) {
        $problems += ("case " + $n + " is 'failed' but cites nothing")
    }
    if (@('blocked_fixture', 'blocked_environment', 'unsupported_api', 'not_run') -contains [string]$row.status) {
        $why = if ($row.PSObject.Properties.Name -contains 'because') { [string]$row.because } else { '' }
        if (-not $why) { $why = [string]$row.observed }
        if (-not $why) { $problems += ("case " + $n + " is '" + $row.status + "' with no stated reason") }
    }
    $rows[$n] = $row
}

# Every case the run never reached. NOT a pass, NOT a product failure - a fact
# about the run.
$missing = @()
for ($i = 1; $i -le 55; $i++) {
    if (-not $rows.ContainsKey($i)) {
        $missing += $i
        $rows[$i] = [pscustomobject]@{
            case = $i; name = "(no artifact)"; status = 'not_run'
            observed = 'the campaign left no artifact for this case'
            because = 'the campaign left no artifact for this case'
            expected = $null; evidence = $null
        }
    }
}

$counts = [ordered]@{}
foreach ($b in $BUCKETS) { $counts[$b] = 0 }
foreach ($n in $rows.Keys) { $counts[[string]$rows[$n].status]++ }

$total = 0
foreach ($b in $BUCKETS) { $total += $counts[$b] }
$executed = $counts['passed'] + $counts['failed'] + $counts['unverified']

$summary = [ordered]@{
    source              = 'rebuilt from per-case artifacts only'
    run_dir             = (Split-Path -Leaf $RunDir)
    buckets             = $counts
    bucket_total        = $total
    executed            = $executed
    executed_pass_rate  = $(if ($executed -gt 0) { [math]::Round($counts['passed'] / $executed, 4) } else { 0 })
    coverage_rate       = [math]::Round($executed / 55.0, 4)
    verified_pass_rate  = [math]::Round($counts['passed'] / 55.0, 4)
    cases_without_artifact = $missing
    integrity_problems  = $problems
    cases               = @(1..55 | ForEach-Object { $rows[$_] })
    rebuilt_utc         = (Get-Date).ToUniversalTime().ToString('o')
}

if (-not $OutFile) { $OutFile = Join-Path $RunDir 'rollup-from-artifacts.json' }
$summary | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $OutFile -Encoding UTF8

Write-Host ""
foreach ($b in $BUCKETS) { Write-Host ("  {0,-22} {1,3}" -f $b, $counts[$b]) }
Write-Host ("  {0,-22} {1,3}" -f 'TOTAL', $total) -ForegroundColor Cyan
Write-Host ("  executed {0}/55   executed_pass_rate {1}   coverage_rate {2}   verified_pass_rate {3}" -f
    $executed, $summary.executed_pass_rate, $summary.coverage_rate, $summary.verified_pass_rate) -ForegroundColor Cyan
if ($missing.Count) { Write-Host ("  cases with no artifact: " + ($missing -join ', ')) -ForegroundColor Yellow }
foreach ($p in $problems) { Write-Host ("  INTEGRITY: " + $p) -ForegroundColor Red }
Write-Host ("  artifact: " + $OutFile) -ForegroundColor DarkGray

if ($total -ne 55) {
    Write-Host "THE BUCKETS DO NOT ADD TO 55." -ForegroundColor Red
    exit 2
}
if ($problems.Count) { exit 4 }
if ($counts['passed'] -eq 55) { Write-Host "55/55." -ForegroundColor Green; exit 0 }
Write-Host ("NOT 55/55: {0} passed. The buckets above are the answer." -f $counts['passed']) -ForegroundColor Yellow
exit 0
