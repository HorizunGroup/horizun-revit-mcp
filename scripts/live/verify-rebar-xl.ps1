#Requires -Version 5.1
<#
  ACERO 3.0, L10 - THE XL MEASUREMENT (NOT YET RUN).

  More than 20 000 bars from ONE requirement set, built in pieces: the set is cut by
  `chunk` (by host_type, at most -HostsPerPiece beams per piece), and every piece goes
  through horizun_submit_job and is polled with horizun_job_status, so no single call
  holds Revit for the whole building. What is recorded, per piece: the plan's expected
  bars, the seconds the rehearsal and the apply took (wall clock from this script, and
  the job record's own running_since / last_event_at), what the apply verified, and the
  audit of the same piece.

  Written offline with L10 and NOT RUN: nothing below has been measured yet. The first
  run is the measurement; this header must then say what it found.

  THE BUDGETS, DECLARED HERE BEFORE ANY OF IT RAN, and not to be moved afterwards:
    one piece's apply job           <= 600 s   (the same ceiling verify-rebar-performance holds)
    one piece's audit job           <= 300 s
    per-bar apply cost, last piece  <= 3 x the first piece's   (document growth must not
                                                                 make the cost per bar grow)

  THE FIXTURE (synthetic, built here in a DISPOSABLE document): -Beams concrete beams
  (M_Concrete-Rectangular Beam 300 x 600mm, 6 m, on the first level, a grid of 20 per row),
  every one carrying Mark XL-<run tag>; one section rule for that mark - 3 x 16M top,
  3 x 16M bottom, a 10M perimeter stirrup at -StirrupSpacingMm with 135 degree hooks, cover
  read from the host. At 100 mm a beam carries about 6 + 58 bars, so the default 320 beams
  plan about 20 500 bars; X1 checks the plan says at least 20 000 before anything is built.

  Needs: a disposable structural fixture open and active (-Document) built from
  Autodesk's metric structural template (10M / 16M bars, Stirrup/Tie - 135 deg.), and a
  second document open (Reset-HzDocument closes and reopens -Document).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [int]$Beams = 320,
    [int]$HostsPerPiece = 20,
    [double]$StirrupSpacingMm = 100.0,
    [int]$JobTimeoutMinutes = 30,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-xl' -Document $Document
$TAG = $run.RunId.Substring($run.RunId.Length - 6)
$MARK = "XL-$TAG"

# ---------------------------------------------------------------- THE BUDGETS
$CEILING = [ordered]@{ apply_job = 600.0; audit_job = 300.0 }
$SCALING_FACTOR = 3.0
$TARGET_BARS = 20000

# Submit one tool call as a job and wait for its record to finish. Returns the job row,
# the wall-clock seconds and the job's own seconds (running_since to last_event_at).
function Invoke-HzJob {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Tool,
          [Parameter(Mandatory)][hashtable]$Arguments, [Parameter(Mandatory)][string]$Label)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $submit = Invoke-HzTool -Run $Run -Tool 'horizun_submit_job' -Label "$Label-submit" -TimeoutSec 120 -Arguments @{
        tool = $Tool; arguments = $Arguments; idempotency_key = (New-HzKey $Run "$Label-submit") }
    $jobId = if ($submit.IsError) { $null } else { [string](Get-HzPath $submit.Result 'job_id') }
    if (-not $jobId) {
        return [pscustomobject]@{ Ok = $false; Job = $null; JobId = $null; WallSeconds = $sw.Elapsed.TotalSeconds
                                  JobSeconds = $null; Why = 'not submitted: ' + (Limit-HzText $submit.Text 300) }
    }
    $deadline = (Get-Date).AddMinutes($JobTimeoutMinutes)
    $job = $null
    while ((Get-Date) -lt $deadline) {
        $poll = Invoke-HzTool -Run $Run -Tool 'horizun_job_status' -Label "$Label-poll" -TimeoutSec 60 -Arguments @{ job_id = $jobId }
        if (-not $poll.IsError) {
            $jobs = @(Get-HzPath $poll.Result 'jobs' | Where-Object { $null -ne $_ })
            if ($jobs.Count -gt 0) {
                $job = $jobs[0]
                if (Get-HzProp $job 'finished') { break }
                if ((Get-HzProp $job 'process_alive') -eq $false) { break }
            }
        }
        Start-Sleep -Seconds 5
    }
    $sw.Stop()
    $jobSeconds = $null
    if ($job) {
        $from = Get-HzProp $job 'running_since'; $to = Get-HzProp $job 'last_event_at'
        if ($from -and $to) {
            $jobSeconds = ([DateTimeOffset]::Parse([string]$to, [Globalization.CultureInfo]::InvariantCulture) -
                           [DateTimeOffset]::Parse([string]$from, [Globalization.CultureInfo]::InvariantCulture)).TotalSeconds
        }
    }
    $finished = $job -and (Get-HzProp $job 'finished')
    [pscustomobject]@{
        Ok = [bool]($finished -and [string](Get-HzProp $job 'final_status') -eq 'ok')
        Job = $job; JobId = $jobId; WallSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
        JobSeconds = $(if ($null -ne $jobSeconds) { [Math]::Round($jobSeconds, 3) } else { $null })
        Why = $(if (-not $job) { 'the job never reported' } elseif (-not $finished) { 'the job did not finish in time' } else { [string](Get-HzProp $job 'final_note') })
    }
}

# =====================================================================  FIXTURE

Write-Host "`n== fixture: $Beams beams with mark $MARK ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$types = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'beam-types' -Arguments @{
    categories = @('OST_StructuralFraming'); include_links = $false; include_types = $true; max_rows = 100 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })
if ($types.Count -ne 1) { throw 'HARNESS: the fixture has no single M_Concrete-Rectangular Beam 300 x 600mm type' }
$bt = [long]$types[0].element_id

$beamIds = New-Object System.Collections.Generic.List[long]
for ($start = 0; $start -lt $Beams; $start += 200) {
    $batch = @()
    for ($i = $start; $i -lt [Math]::Min($Beams, $start + 200); $i++) {
        $x = ($i % 20) * 8000.0; $y = [Math]::Floor($i / 20) * 4000.0
        $batch += @{ kind = 'structural_framing'; type_id = $bt; level_id = $lv
                     start = @($x, $y, 3000.0); end = @(($x + 6000.0), $y, 3000.0) }
    }
    $made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label "beams-$start" -Arguments @{
        target_document = $Document; units = 'mm'; transaction_name = "HZ XL $TAG beams"; elements = $batch }
    foreach ($row in @($made.Apply.Result.rows)) { $beamIds.Add([long](Get-HzProp $row 'element_id')) }
}
if ($beamIds.Count -ne $Beams) { throw "HARNESS: $Beams beams were asked for and $($beamIds.Count) were created" }
for ($start = 0; $start -lt $beamIds.Count; $start += 200) {
    $writes = @($beamIds | Select-Object -Skip $start -First 200 | ForEach-Object { @{ target_id = $_; parameter = 'Mark'; value = $MARK } })
    $null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label "marks-$start" -Arguments @{
        target_document = $Document; writes = $writes }
}
$run.Fixture['beams'] = $beamIds.Count
$run.Fixture['mark'] = $MARK

$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = "xl-$TAG"; version = '1.0.0'; title = 'XL synthetic schedule (L10)' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    hook_types = @(, @{ id = 'H135'; type_name = 'Stirrup/Tie - 135 deg.' })
    section_rules = @(, [ordered]@{
        id = 'VXL'; host = @{ category = 'OST_StructuralFraming'; mark = $MARK }
        cover = @{ source = 'host' }
        layers = @(
            @{ face = 'top'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true },
            @{ face = 'bottom'; count = 3; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = @{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
                      start = @{ hook_type = 'H135' }; end = @{ hook_type = 'H135' }
                      zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = $StirrupSpacingMm } }) } })
}

# =====================================================================  THE CUT

Write-Host "`n== the cut ==" -ForegroundColor Cyan
$chunk0 = @{ by = 'host_type'; hosts_per_piece = $HostsPerPiece; piece_index = 0 }
$plan0 = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-piece-0' -Arguments @{
    target_document = $Document; requirement_set = $set; chunk = $chunk0 }
$pieceCount = [int](Get-HzPath $plan0.Result 'chunk', 'piece_count')
$partition = [string](Get-HzPath $plan0.Result 'chunk', 'partition_sha256')
$covered = [int](Get-HzPath $plan0.Result 'chunk', 'hosts_covered')
$expectedPieces = [int][Math]::Ceiling($Beams / [double]$HostsPerPiece)
Add-HzProbe -Run $run -Id 'X0' -Name 'L10 the set is cut into pieces of at most hosts_per_piece beams, every beam in one' `
    -Expected "$expectedPieces pieces, $Beams hosts covered, a partition digest" `
    -Observed "pieces=$pieceCount covered=$covered partition=$partition" `
    -Ok ($pieceCount -eq $expectedPieces -and $covered -eq $Beams -and $partition -match '^[0-9a-f]{64}$') `
    -Evidence @{ chunk = (Get-HzPath $plan0.Result 'chunk') }
if ($pieceCount -lt 1 -or -not $partition) {
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# Every piece planned first, so the 20 000 is established before anything is built.
$pieces = @()
$plannedBars = 0
for ($p = 0; $p -lt $pieceCount; $p++) {
    $chunk = @{ by = 'host_type'; hosts_per_piece = $HostsPerPiece; piece_index = $p; partition_sha256 = $partition }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label "plan-piece-$p" -TimeoutSec 900 -Arguments @{
        target_document = $Document; requirement_set = $set; chunk = $chunk }
    $sw.Stop()
    $bars = [int](Get-HzPath $plan.Result 'summary', 'expected_bars')
    $plannedBars += $bars
    $pieces += [ordered]@{ index = $p; chunk = $chunk; hosts = [int](Get-HzPath $plan.Result 'chunk', 'piece', 'hosts')
                           expected_bars = $bars; would_apply = [bool](Get-HzProp $plan.Result 'would_apply')
                           plan_seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3) }
}
Add-HzProbe -Run $run -Id 'X1' -Name "L10 the plan, summed over the pieces, asks for at least $TARGET_BARS bars" `
    -Expected ">= $TARGET_BARS expected bars, every piece would apply" `
    -Observed ("expected_bars={0} pieces_that_would_not_apply={1}" -f $plannedBars, @($pieces | Where-Object { -not $_.would_apply }).Count) `
    -Ok ($plannedBars -ge $TARGET_BARS -and @($pieces | Where-Object { -not $_.would_apply }).Count -eq 0) -Evidence @{ pieces = $pieces }

# =====================================================================  THE LOOP

Write-Host "`n== apply, piece by piece, through submit_job ==" -ForegroundColor Cyan
foreach ($piece in $pieces) {
    $p = $piece.index
    $base = @{ target_document = $Document; requirement_set = $set; chunk = $piece.chunk; transaction_name = "HZ XL $TAG piece $p" }
    $dry = Invoke-HzJob -Run $run -Tool 'horizun_apply_reinforcement' -Label "dry-$p" -Arguments (Copy-HzArgs $base @{ dry_run = $true })
    $token = if ($dry.Ok) { [string](Get-HzPath $dry.Job 'result', 'confirmation_token') } else { $null }
    $piece['dry_wall_seconds'] = $dry.WallSeconds; $piece['dry_job_seconds'] = $dry.JobSeconds
    if (-not $token) { $piece['applied'] = $false; $piece['why'] = 'no token: ' + $dry.Why; continue }
    $apply = Invoke-HzJob -Run $run -Tool 'horizun_apply_reinforcement' -Label "apply-$p" -Arguments (Copy-HzArgs $base @{
        dry_run = $false; confirmation_token = $token; idempotency_key = (New-HzKey $run "apply-$p") })
    $piece['apply_wall_seconds'] = $apply.WallSeconds; $piece['apply_job_seconds'] = $apply.JobSeconds
    $piece['applied'] = $apply.Ok
    $piece['created_verified'] = $(if ($apply.Job) { Get-HzPath $apply.Job 'result', 'created_verified' } else { $null })
    if (-not $apply.Ok) { $piece['why'] = $apply.Why }

    $audit = Invoke-HzJob -Run $run -Tool 'horizun_audit_reinforcement' -Label "audit-$p" -Arguments @{
        target_document = $Document; requirement_set = $set; chunk = $piece.chunk }
    $piece['audit_wall_seconds'] = $audit.WallSeconds; $piece['audit_job_seconds'] = $audit.JobSeconds
    $piece['audit_verdict'] = $(if ($audit.Job) { [string](Get-HzPath $audit.Job 'result', 'summary', 'verdict') } else { $null })
    # THE FINDINGS, counted by code and severity, so a verdict other than agrees says why.
    $af = @($(if ($audit.Job) { @(Get-HzPath $audit.Job 'result', 'findings') | Where-Object { $_ } }))
    $piece['finding_codes'] = @($af | Group-Object { [string]$_.code + ':' + [string]$_.severity } | ForEach-Object { $_.Name + '=' + $_.Count })
    $piece['first_error'] = @($af | Where-Object { [string]$_.severity -ne 'info' } | Select-Object -First 2 | ForEach-Object {
        [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed + ' ' + (Limit-HzText ([string]$_.why) 200) })
    Write-Host ("  piece {0,3}: hosts={1} bars={2} apply={3}s (job {4}s) audit={5}s verdict={6}" -f $p, $piece.hosts,
        $piece.expected_bars, $piece.apply_wall_seconds, $piece.apply_job_seconds, $piece.audit_wall_seconds, $piece.audit_verdict)
}

$notApplied = @($pieces | Where-Object { -not $_['applied'] })
Add-HzProbe -Run $run -Id 'X2' -Name 'L10 every piece was rehearsed, applied and verified through submit_job' `
    -Expected 'every apply job finished ok' `
    -Observed ("applied={0}/{1} first_failure={2}" -f ($pieces.Count - $notApplied.Count), $pieces.Count,
        $(if ($notApplied.Count) { [string]$notApplied[0]['why'] } else { '' })) `
    -Ok ($notApplied.Count -eq 0) -Evidence @{ pieces = $pieces }

$slowApply = @($pieces | Where-Object { $null -ne $_['apply_job_seconds'] -and [double]$_['apply_job_seconds'] -gt $CEILING.apply_job })
$slowAudit = @($pieces | Where-Object { $null -ne $_['audit_job_seconds'] -and [double]$_['audit_job_seconds'] -gt $CEILING.audit_job })
$untimed = @($pieces | Where-Object { $null -eq $_['apply_job_seconds'] -or $null -eq $_['audit_job_seconds'] })
Add-HzProbe -Run $run -Id 'X3' -Name 'L10 every piece stays inside the ceilings declared before the run' `
    -Expected ("apply job <= {0} s and audit job <= {1} s per piece, every piece timed" -f $CEILING.apply_job, $CEILING.audit_job) `
    -Observed ("slow_apply={0} slow_audit={1} untimed={2} max_apply={3}s" -f $slowApply.Count, $slowAudit.Count, $untimed.Count,
        (($pieces | ForEach-Object { [double]($_['apply_job_seconds'] -as [double]) } | Measure-Object -Maximum).Maximum)) `
    -Ok ($slowApply.Count -eq 0 -and $slowAudit.Count -eq 0 -and $untimed.Count -eq 0) -Evidence @{ pieces = $pieces }

$timed = @($pieces | Where-Object { $_['applied'] -and $_['apply_job_seconds'] -and $_.expected_bars -gt 0 })
if ($timed.Count -ge 2) {
    $first = [double]$timed[0]['apply_job_seconds'] / $timed[0].expected_bars
    $last = [double]$timed[-1]['apply_job_seconds'] / $timed[-1].expected_bars
    Add-HzProbe -Run $run -Id 'X4' -Name 'L10 the cost per bar does not grow as the document fills' `
        -Expected ("per-bar apply cost of the last piece <= {0} x the first's" -f $SCALING_FACTOR) `
        -Observed ("first={0:N4} s/bar last={1:N4} s/bar ratio={2:N2}" -f $first, $last, ($last / $first)) `
        -Ok ($last -le $SCALING_FACTOR * $first) -Evidence @{ first = $timed[0]; last = $timed[-1] }
}
else {
    Add-HzProbe -Run $run -Id 'X4' -Name 'L10 the cost per bar does not grow as the document fills' -Status 'not_assessable' `
        -Expected 'two applied, timed pieces' -Observed ("timed pieces={0}" -f $timed.Count)
}

# AGREES, OR AGREES WITH NOTES THAT ARE ALL ACCEPTED INFO FINDINGS - the same bar the sections probe
# holds: hook_corner_opening (D1, option A) hook_end_extension and hook_corner_seat, all measured.
$accepted = @('hook_corner_opening', 'hook_end_extension', 'hook_corner_seat')
$notAgreeing = @($pieces | Where-Object { -not ($_['audit_verdict'] -eq 'agrees' -or ($_['audit_verdict'] -eq 'agrees_with_notes' -and
    @($_['finding_codes'] | Where-Object { $_ -notmatch (':info=') -or ($_ -split ':')[0] -notin $accepted }).Count -eq 0)) })
Add-HzProbe -Run $run -Id 'X5' -Name 'L10 the audit of every piece agrees' `
    -Expected 'verdict agrees on every piece, or agrees_with_notes whose every note is hook_corner_opening, hook_end_extension or hook_corner_seat' `
    -Observed ("not_agreeing={0} verdicts={1} first={2}" -f $notAgreeing.Count, (($pieces | ForEach-Object { $_['audit_verdict'] } | Sort-Object -Unique) -join ','),
        $(if ($notAgreeing.Count) { (@($notAgreeing[0]['finding_codes']) -join ' ') + ' | ' + (@($notAgreeing[0]['first_error']) -join ' | ') } else { '' })) `
    -Ok ($notAgreeing.Count -eq 0) -Evidence @{ pieces = $pieces }

# =====================================================================  THE TWO READS

Write-Host "`n== parse_notation and compare_bars ==" -ForegroundColor Cyan
$notation = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'parse-notation' -Arguments @{
    mode = 'parse_notation'; lines = @('3#5 L=6.00', 'E#3 c/0.10', '4#5 L=1,234') }
$nRows = @(Get-HzPath $notation.Result 'rows')
Add-HzProbe -Run $run -Id 'X6' -Name 'L10 parse_notation reads schedule cells in the running bridge, refusing the ambiguous one' `
    -Expected 'two read (6000 mm; 100 mm spacing), one refused notation_bad_number' `
    -Observed ("ok={0} rows={1} codes={2}" -f $notation.Ok, $nRows.Count, (($nRows | ForEach-Object { [string](Get-HzProp $_ 'code') }) -join ',')) `
    -Ok ($notation.Ok -and $nRows.Count -eq 3 -and [double](Get-HzProp $nRows[0] 'length_mm') -eq 6000 -and
         [double](Get-HzProp $nRows[1] 'spacing_mm') -eq 100 -and [string](Get-HzProp $nRows[2] 'code') -eq 'notation_bad_number') `
    -Evidence @{ reply = $notation.Result }

$host0 = $beamIds[0]
$same = Invoke-HzTool -Run $run -Tool 'horizun_query_structure' -Label 'compare-bars-self' -Arguments @{
    mode = 'compare_bars'; host_id_a = $host0; host_id_b = $host0
    bar_tolerances = @{ identical_mm = 1.0; near_mm = 50.0; length_mm = 1.0 }; max_rows = 10 }
Add-HzProbe -Run $run -Id 'X7' -Name 'L10 compare_bars pairs a beam''s bars with themselves, bar for bar' `
    -Expected 'verdict identical, every bar identical, none left over' `
    -Observed ("ok={0} verdict={1} summary={2}" -f $same.Ok, [string](Get-HzProp $same.Result 'verdict'),
        ((Get-HzProp $same.Result 'summary') | ConvertTo-Json -Compress)) `
    -Ok ($same.Ok -and [string](Get-HzProp $same.Result 'verdict') -eq 'identical') -Evidence @{ summary = (Get-HzProp $same.Result 'summary') }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
