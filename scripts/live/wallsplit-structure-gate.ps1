#Requires -Version 5.1
<#
  THE STATIC GATE ON THE CAMPAIGN ITSELF, run before Revit is opened.

  Four of the five live runs this capability has cost were spent on defects in
  the harness, not in the product. A harness bug is discovered at the most
  expensive possible moment - with Revit open, a model staged and a session that
  cannot be repeated - so everything about the runner that can be checked without
  Revit is checked here.

  It asserts the SHAPE of the matrix (exactly 1..55, once each) and the HONESTY
  rules it must obey (an absent artifact can never become a pass; an apply that
  was not verified cannot be reported as verified; a rollback is only 'confirmed'
  if Revit said so). It proves the last group by RUNNING the roll-up against
  synthetic artifacts rather than by reading the source for reassuring words.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$matrix = Join-Path $root 'scripts/live/wallsplit-matrix.ps1'
$lib = Join-Path $root 'scripts/live/hz-wallsplit.lib.ps1'
$rollup = Join-Path $root 'scripts/live/wallsplit-rollup.ps1'

$failures = New-Object System.Collections.ArrayList
$checks = 0

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $script:checks++
    if ($Ok) { Write-Host ("  ok    " + $Name) -ForegroundColor DarkGreen }
    else {
        Write-Host ("  FAIL  " + $Name + $(if ($Detail) { " - " + $Detail } else { '' })) -ForegroundColor Red
        $null = $failures.Add($Name + $(if ($Detail) { ": " + $Detail } else { '' }))
    }
}

Write-Host "=== 1-8  syntax and structure ===" -ForegroundColor Cyan

foreach ($f in @($matrix, $lib, $rollup)) {
    $errs = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$errs)
    Check ("parses: " + (Split-Path -Leaf $f)) ($errs.Count -eq 0) $(if ($errs) { $errs[0].Message } else { '' })
}

$src = Get-Content -LiteralPath $matrix -Raw

# ---- the 55 case numbers, taken from the CALLS, not from a list -------------
# Every route that records a case is enumerated, so a case added through a new
# helper cannot slip past this gate by not being in a hand-written table.
$numbers = New-Object System.Collections.ArrayList
foreach ($m in [regex]::Matches($src, '(?m)^\s*(?:\$\w+\s*=\s*)?(?:Positive|Negative|Structural|BlockedFixture|BlockedEnv|UnsupportedApi|NotRun)\s+(\d+)\b')) {
    $null = $numbers.Add([int]$m.Groups[1].Value)
}
foreach ($m in [regex]::Matches($src, 'Add-WsCase\s+-Run\s+\$run\s+-Number\s+(\d+)\b')) {
    $null = $numbers.Add([int]$m.Groups[1].Value)
}
foreach ($m in [regex]::Matches($src, 'Add-WsCase\s+-Run\s+\$run\s+-Number\s+\$n\b')) {
    # the 27/28/29 loop writes three cases through one call site
    foreach ($n in 27, 28, 29) { $null = $numbers.Add($n) }
}

$distinct = @($numbers | Sort-Object -Unique | Where-Object { $_ -ge 1 -and $_ -le 55 })
Check "the matrix mentions exactly 55 distinct case numbers" ($distinct.Count -eq 55) ("found " + $distinct.Count)
Check "the case numbers are exactly 1..55" (($distinct[0] -eq 1) -and ($distinct[-1] -eq 55)) ("range " + $distinct[0] + '..' + $distinct[-1])

$absent = @(1..55 | Where-Object { $distinct -notcontains $_ })
Check "no case number is missing" ($absent.Count -eq 0) ("missing: " + ($absent -join ', '))
Check "there is no case 56" (@($numbers | Where-Object { $_ -gt 55 }).Count -eq 0) 'a number above 55 is written somewhere'
Check "there is no case 0 among the 55" (@($numbers | Where-Object { $_ -eq 0 }).Count -eq 0) 'the canary must not be counted as a case'

# ---- the reveal belongs to case 20, the six location lines to case 6 --------
Check "the reveal is inside case 20, not a case of its own" `
    ($src -match "c20_reveal" -and $src -match "-Number 20 -Name 'wall sweep and wall reveal'") ''
# The six keys are BUILT by concatenation - Id ("c06_" + $ll) - so counting the
# literal 'c06_' found one, not six. What matters is that the six line names are
# all iterated and that they all report through a single case 6.
$sixNames = @('WallCenterline', 'CoreCenterline', 'FinishFaceExterior',
              'FinishFaceInterior', 'CoreExterior', 'CoreInterior')
$sixPresent = @($sixNames | Where-Object { $src -match [regex]::Escape($_) }).Count
Check "all six location-line names are iterated" ($sixPresent -eq 6) ("found " + $sixPresent)
Check "the six location lines report as one case (6)" `
    (($src -match "-Number 6 -Name 'each of the six wall location lines'") -and
     (@([regex]::Matches($src, '-Number 6 ')).Count -eq 1)) 'case 6 must be recorded exactly once'
Check "case 6 passes only if all six pass" ($src -match '\$sixOk -and \$sixSeen -eq 6') ''

# ---- the pinned case runs last ---------------------------------------------
$pinnedRun = $src.IndexOf("Start-WsCase -Run `$run -Number 12")
$lastOther = 0
foreach ($m in [regex]::Matches($src, '-Number\s+(\d+)')) {
    $n = [int]$m.Groups[1].Value
    if ($n -ne 12 -and $m.Index -gt $lastOther) { $lastOther = $m.Index }
}
Check "the pinned wall (case 12) is executed after every other case" ($pinnedRun -gt $lastOther) `
    "pinned at $pinnedRun, last other at $lastOther"

# ---- the canary runs before case 1 -----------------------------------------
$canaryAt = $src.IndexOf("=== canary")
$case1At = $src.IndexOf("Positive 1 ")
Check "the canary runs before the first case" (($canaryAt -gt 0) -and ($canaryAt -lt $case1At)) ''
Check "a failed canary stops the run" ($src -match 'exit 3') 'there is no stop after a failed canary'


# The canary is STRICT. It was briefly allowed to continue past one named defect
# so that cases 13-17 could measure whether the carrier's joins carried the cut.
# The chain removes that defect by construction, so the exception is now a valve
# that can only hide a regression.
Check "the canary has no bypass left" `
    (-not ($src -match 'canaryKnownDefectOnly')) 'a known-defect bypass is still in the runner'
Check "any canary failure stops the run" `
    ($src -match [regex]::Escape('if (-not $canaryOk -and -not $ContinueAfterCanary)')) ''
# The canary may continue past the KNOWN join-topology defect, so that cases 13-17
# can measure it. That exception has to stay narrow, or it becomes the suppression
# again wearing a different hat. Four conditions, all required.
$batchApplies = @([regex]::Matches($src, 'Apply\s+@\(\$\w+,\s*\$\w+\)')).Count
Check "at most two batch applies (cases 32 and 53 test batching deliberately)" ($batchApplies -le 2) `
    ("found " + $batchApplies)

# A provisional case and its final verdict intentionally share number 30. The
# provisional row must be removed BEFORE the final one is added; doing it after
# deletes both rows and the expensive live run reaches the end with 54/55.
$case30Start = $src.IndexOf('# 30 idempotence')
$case31Start = $src.IndexOf('# 31 stale plan', $case30Start)
$case30Block = if ($case30Start -ge 0 -and $case31Start -gt $case30Start) {
    $src.Substring($case30Start, $case31Start - $case30Start)
} else { '' }
$remove30At = $case30Block.IndexOf('Remove-RecordedCase 30')
$final30At = $case30Block.IndexOf('Add-WsCase -Run $run -Number 30')
Check "case 30 removes its provisional row before adding the final verdict" `
    (($remove30At -ge 0) -and ($final30At -gt $remove30At)) `
    ("remove at " + $remove30At + ', final add at ' + $final30At)

Write-Host ""
Write-Host "=== 9-16  the honesty rules, PROVEN by running the roll-up ===" -ForegroundColor Cyan

# The rules below are not asserted by reading the source for reassuring words.
# Synthetic artifacts are written, the real roll-up is run against them, and its
# answer is compared to what the rule demands. A rule that a comment claims but
# the code does not enforce fails here.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("hz-gate-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Force -Path $tmp
try {
    function Write-Case {
        param([int]$N, [string]$Status, $Evidence, [string]$Because, [string]$Observed)
        ([ordered]@{ case = $N; name = "synthetic"; expected = 'x'; observed = $Observed
                     status = $Status; because = $Because; evidence = $Evidence } |
            ConvertTo-Json -Depth 10) | Set-Content -LiteralPath (Join-Path $tmp ("case-{0:d2}-final.json" -f $N)) -Encoding UTF8
    }

    # 11: an ABSENT artifact must never become a pass.
    Write-Case 1 'passed' @{ a = 1 } '' 'ok'
    $out = & $rollup -RunDir $tmp 2>&1 | Out-String
    $rb = Get-Content -LiteralPath (Join-Path $tmp 'rollup-from-artifacts.json') -Raw | ConvertFrom-Json
    Check "an absent artifact becomes not_run, never passed" `
        (($rb.buckets.passed -eq 1) -and ($rb.buckets.not_run -eq 54)) `
        ("passed=" + $rb.buckets.passed + " not_run=" + $rb.buckets.not_run)
    Check "the buckets still add to 55 when 54 artifacts are missing" ($rb.bucket_total -eq 55) `
        ("total " + $rb.bucket_total)
    Check "coverage_rate reflects what actually ran" ($rb.coverage_rate -eq 0.0182) ("got " + $rb.coverage_rate)

    # 15: a 'failed' row that cites nothing is an integrity problem.
    Write-Case 2 'failed' $null '' ''
    $null = & $rollup -RunDir $tmp 2>&1
    $rb = Get-Content -LiteralPath (Join-Path $tmp 'rollup-from-artifacts.json') -Raw | ConvertFrom-Json
    Check "a 'failed' row that cites no artifact is reported as an integrity problem" `
        (@($rb.integrity_problems | Where-Object { $_ -match 'cites nothing' }).Count -eq 1) `
        ($rb.integrity_problems -join '; ')

    # 16: a blocked row with no stated reason is an integrity problem.
    Remove-Item -LiteralPath (Join-Path $tmp 'case-02-final.json') -Force
    Write-Case 3 'blocked_fixture' $null '' ''
    $null = & $rollup -RunDir $tmp 2>&1
    $rb = Get-Content -LiteralPath (Join-Path $tmp 'rollup-from-artifacts.json') -Raw | ConvertFrom-Json
    Check "a blocked row with no stated reason is reported as an integrity problem" `
        (@($rb.integrity_problems | Where-Object { $_ -match 'no stated reason' }).Count -eq 1) `
        ($rb.integrity_problems -join '; ')

    # An unknown bucket cannot be smuggled in.
    Remove-Item -LiteralPath (Join-Path $tmp 'case-03-final.json') -Force
    Write-Case 4 'mostly_fine' @{ a = 1 } 'x' 'x'
    $null = & $rollup -RunDir $tmp 2>&1
    $rb = Get-Content -LiteralPath (Join-Path $tmp 'rollup-from-artifacts.json') -Raw | ConvertFrom-Json
    Check "an invented status is refused, not counted" `
        (@($rb.integrity_problems | Where-Object { $_ -match 'unknown status' }).Count -eq 1) `
        ($rb.integrity_problems -join '; ')
    Remove-Item -LiteralPath (Join-Path $tmp 'case-04-final.json') -Force
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# ---- 12/13/14: the rules the RUNNER must obey, in its own source -----------
# These three are about how a case is CLASSIFIED at the moment it is recorded,
# which the roll-up cannot see; they are checked where they live.
Check "an apply with no wall entry is recorded unverified, not passed" `
    ($src -match "Status \`$\(if \(\`$ok\) \{ 'passed' \} elseif \(-not \`$w\) \{ 'unverified' \} else \{ 'failed' \}\)") `
    'the pinned case must record unverified when the apply returns no entry'
Check "a pass requires BOTH all_verified and the post-commit pass" `
    ($src -match "\`$ap\.Result\.all_verified -eq \`$true\) -and \`$post -and \(\`$post\.passed -eq \`$true\)") ''
Check "rollback is only reported confirmed when Revit confirmed it" `
    ($src -match "rollback_status -eq 'RolledBack'\) -and \(\`$w\.rollback_confirmed -eq \`$true\)") ''

# ---- the library keeps its promises ----------------------------------------
$libSrc = Get-Content -LiteralPath $lib -Raw

# The three ways a live run can measure the past without noticing.
Check "every call mints a fresh idempotency key" `
    ($libSrc -match [regex]::Escape('$Arguments[''idempotency_key''] = [guid]::NewGuid().ToString()')) ''
Check "the key is minted for NON-mutating calls too" `
    (-not ($libSrc -match [regex]::Escape('if ($Mutates -and'))) 'the key must not be conditional on mutation'
Check "an old artifact is deleted before the call" `
    ($libSrc -match [regex]::Escape('if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }')) ''
Check "a replayed answer is refused" `
    ($libSrc -match 'REPLAY REFUSED') ''
$onceSrc = Get-Content -LiteralPath (Join-Path $root 'scripts/live/hz-once.ps1') -Raw
Check "hz-once refuses when no artifact appeared" ($onceSrc -match 'NO ARTIFACT') ''
Check "hz-once refuses a replayed key" ($onceSrc -match 'REPLAY: the reply carries') ''

Check "the harness never suppresses the warning itself" `
    (-not ($src -match 'DeleteWarning')) 'the harness must not touch the model warnings'

# ---- one call at a time, one wall per apply --------------------------------
Check "no concurrent calls are issued" (-not ($src -match 'Start-Job|ForEach-Object\s+-Parallel')) ''
Check "the library accepts exactly the seven buckets" `
    ($libSrc -match "ValidateSet\('passed', 'failed', 'unverified', 'not_run', 'blocked_fixture', 'blocked_environment', 'unsupported_api'\)") ''

# AND THAT THE MATRIX ONLY USES THEM. The check above reads the LIBRARY's
# declaration; it says nothing about what the runner actually passes. Cases 34
# and 35 passed 'not_covered' - a word the library rejects - so a run reaching
# them raised ParameterBindingValidationException under StrictMode and died at
# case 34, on the very path a failing run takes. Found by reading, because no
# run has ever got that far.
# Only as far as the NEXT parameter: -Status and -Observed share a line, and
# reading to end-of-line collected 'absent' out of an -Observed expression
# and reported it as an illegal bucket. A gate that cries wolf gets muted.
$statusLiterals = [regex]::Matches($src, "-Status.*?(?=\s-[A-Z]|`n)") |
    ForEach-Object { [regex]::Matches($_.Value, "'([a-z_]+)'") } |
    ForEach-Object { $_ } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$legalBuckets = @('passed', 'failed', 'unverified', 'not_run',
                  'blocked_fixture', 'blocked_environment', 'unsupported_api')
$illegalBuckets = @($statusLiterals | Where-Object { $legalBuckets -notcontains $_ })
Check "the matrix passes only buckets the library accepts" `
    ($illegalBuckets.Count -eq 0) ("the matrix passes: " + ($illegalBuckets -join ', '))
Check "every case is written to disk as it is recorded" ($libSrc -match "case-\{0:d2\}-final\.json") ''
Check "checkpoints append rather than rewrite" ($libSrc -match 'Add-Content -LiteralPath \$Ctx\.File') ''
Check "checkpoints carry the installed commit and the document fingerprint" `
    (($libSrc -match 'installed_commit') -and ($libSrc -match 'document_fingerprint')) ''
# The record must carry a LEAF, never the path it was handed: an absolute
# artifact path names the machine's user in evidence meant to be shareable.
Check "checkpoint artifact paths are reduced to a leaf" ($libSrc -match 'Split-Path -Leaf \$rel') ''
Check "the checkpoint record stores the reduced value, not the raw argument" `
    (($libSrc -match 'artifact\s+= \$rel') -and -not ($libSrc -match 'artifact\s+= \$Artifact')) ''

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host ("STRUCTURE GATE GREEN: {0}/{0} checks." -f $checks) -ForegroundColor Green
    exit 0
}
Write-Host ("STRUCTURE GATE RED: {0} of {1} checks failed." -f $failures.Count, $checks) -ForegroundColor Red
foreach ($f in $failures) { Write-Host ("  " + $f) -ForegroundColor Red }
exit 1
