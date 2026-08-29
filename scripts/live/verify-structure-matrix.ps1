#Requires -Version 5.1
<#
  THE MULTIVERSION MATRIX: the structural suites, on every installed Revit.

  This has been deferred since the reinforcement work began, for a good reason -
  behaviour proved on one year and multiplied by five is a number nobody has
  measured. It is run here with express authorisation.

  What it does, per year, and why each step is the way it is:

  * ONE REVIT AT A TIME. The bridge refuses to pick between two Revits of the
    same year, and correctly; two DIFFERENT years at once is worse, because the
    resolver would have to guess which one a call meant. So every year begins by
    closing everything and ends by closing everything.

  * REVIT IS STARTED BY ITS OWN EXE. The .rvt association on this machine is
    Revit 2027, so handing a file to the shell starts the wrong year against an
    add-in built for a different one - which then shows ITS unsigned-add-in
    dialog and reads exactly like the year under test failing to load.

  * THE MODEL IS OPENED THROUGH THE BRIDGE. Revit's own open raises the warnings
    roll-up on these fixtures, and a modal with nobody at the keyboard stops
    Revit servicing the bridge at all - so every later call is refused with
    "Revit has a MODAL DIALOG open" and the year reads as a bridge that never
    came up.

  * A YEAR THAT WILL NOT COME UP IS RECORDED, NOT SKIPPED. The whole point of a
    matrix is to say which years were measured and which were not, so a year
    that fails to start is a row with a reason rather than an absence.

  Each year runs the same two harnesses against that year's own disposable
  fixture. Nothing is ever saved.
#>
[CmdletBinding()]
param(
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027),
    [string]$ArtifactDir,
    [int]$StartupMinutes = 10,
    # USE A BRIDGE THAT IS ALREADY UP FOR THIS YEAR, instead of closing it and
    # starting it again. Measured on this machine: Revit 2023 raises a modal
    # "Revit cannot run the external application Insights" at every start - not
    # a Horizun add-in, but it holds the UI thread, so horizun_health never
    # answers and the year reads as a bridge that never came up. Somebody has to
    # press Close. This switch means somebody has to press it ONCE per year
    # rather than once per attempt, which is what makes re-running a single year
    # after a fix practical. The year is still checked against what the bridge
    # answers, so it can only ever reuse the RIGHT Revit.
    [switch]$UseRunning
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# THREE levels up, not two. This file lives in scripts\live, so two
# Split-Paths land on scripts and every helper path came out as
# scripts\scripts\hz-call.ps1 - which killed the run on the first call,
# after Revit 2023 had already been started.
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
if (-not $ArtifactDir) { $ArtifactDir = Join-Path $repo 'artifacts\live' }
if (-not (Test-Path -LiteralPath $ArtifactDir)) { $null = New-Item -ItemType Directory -Path $ArtifactDir -Force }
$scratch = Join-Path $env:TEMP 'horizun-structure-matrix'
if (-not (Test-Path -LiteralPath $scratch)) { $null = New-Item -ItemType Directory -Path $scratch -Force }

# The disposable model each year opens. A 2026 file cannot be opened by 2023, so
# every year has its own, and 2026 uses the write document the other structural
# harnesses already use.
$MODEL = @{
    2023 = 'C:\hz-live\HZ23_BASE.rvt'
    2024 = 'C:\hz-live\HZ24_BASE.rvt'
    2025 = 'C:\hz-live\HZ25_BASE.rvt'
    2026 = 'C:\hz-live\HZ_WRITE.rvt'
    2027 = 'C:\hz-live\HZ27_BASE.rvt'
}

function Say($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

# ASKING A REVIT THAT IS STILL STARTING IS THE NORMAL CASE HERE, NOT AN ERROR.
#
# This whole function is one long poll against a process that is not ready yet,
# and hz-call.ps1 reaches inside a reply that does not exist while the bridge is
# coming up - "The property 'structuredContent' cannot be found on this object".
# With $ErrorActionPreference = 'Stop' at the top of this file that killed the
# entire matrix on the FIRST poll of the FIRST year, three seconds after
# starting Revit 2023, having measured nothing. A poll that fails is a "not
# yet", and the only correct behaviour is to return null and ask again.
function Ask-Health {
    $h = Join-Path $scratch 'matrix-health.json'
    if (Test-Path -LiteralPath $h) { Remove-Item -LiteralPath $h -Force -ErrorAction SilentlyContinue }
    try {
        $keep = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        & (Join-Path $repo 'scripts\hz-call.ps1') -Tool horizun_health -Json $h -Quiet -TimeoutSec 90 2>&1 | Out-Null
        $ErrorActionPreference = $keep
    }
    catch {
        $ErrorActionPreference = 'Stop'
        return $null
    }
    if (-not (Test-Path -LiteralPath $h)) { return $null }
    try { return (Get-Content -LiteralPath $h -Raw | ConvertFrom-Json).result } catch { return $null }
}

function Close-EveryRevit {
    Stop-Process -Name Revit -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Process -Name Revit -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
    }
    # A discovery file naming a process that is gone is a live-looking lie, and
    # worse once Windows reuses the pid.
    Get-ChildItem (Join-Path $env:USERPROFILE '.horizun\discovery') -Filter 'revit-*.json' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    return @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -eq 0
}

$rows = @()
$started = (Get-Date).ToUniversalTime()

foreach ($year in $Years) {
    $exe = "C:\Program Files\Autodesk\Revit $year\Revit.exe"
    $row = [ordered]@{
        year = $year; state = 'not_run'; why = $null
        model = $MODEL[$year]; commit = $null; revit_build = $null
        reused_running_revit = $false
        rebar = $null; geometry = $null; performance = $null
    }

    Write-Host ''
    Say ("================ Revit $year ================")

    if (-not (Test-Path -LiteralPath $exe)) {
        $row.state = 'not_installed'; $row.why = "no Revit.exe at $exe"
        Say $row.why; $rows += $row; continue
    }
    if (-not (Test-Path -LiteralPath $MODEL[$year])) {
        $row.state = 'fixture_missing'; $row.why = ("no disposable model at " + $MODEL[$year])
        Say $row.why; $rows += $row; continue
    }
    $health = $null
    $reused = $false
    if ($UseRunning) {
        $h = Ask-Health
        if ($h -and $h.status -eq 'healthy' -and [string]$h.revit_version -eq [string]$year) {
            $health = $h; $reused = $true
            Say "reusing the Revit $year already running"
        }
    }

    if (-not $health) {
        if (-not (Close-EveryRevit)) {
            $row.state = 'blocked'; $row.why = 'a Revit process survived the close'
            Say $row.why; $rows += $row; continue
        }

        Say "starting Revit $year by its own exe"
        Start-Process -FilePath $exe
        $deadline = (Get-Date).AddMinutes($StartupMinutes)
        while ((Get-Date) -lt $deadline) {
            $h = Ask-Health
            if ($h -and $h.status -eq 'healthy') { $health = $h; break }
            Start-Sleep -Seconds 10
        }
    }
    $row.reused_running_revit = $reused
    if (-not $health) {
        $row.state = 'bridge_never_came_up'
        $row.why = ("no healthy answer within $StartupMinutes minute(s). A security dialog on another " +
                    "monitor looks exactly like this - check for one before believing the year is broken.")
        Say $row.why; $rows += $row; continue
    }
    $row.commit = [string]$health.horizun_commit
    $row.revit_build = [string]$health.revit_build
    Say ("bridge up: " + $health.revit_version + " " + $health.revit_build +
         "  commit=" + $row.commit.Substring(0, 12))

    if ([string]$health.revit_version -ne [string]$year) {
        $row.state = 'wrong_year'
        $row.why = ("the bridge answered for Revit " + $health.revit_version + ", not $year")
        Say $row.why; $rows += $row; continue
    }

    Say ("opening " + $MODEL[$year] + " through the bridge")
    $openArgs = Join-Path $scratch "open-$year.json"
    $openOut = Join-Path $scratch "open-$year-out.json"
    @{ path = $MODEL[$year]; idempotency_key = ("matrix-$year-" + [guid]::NewGuid().ToString('N')) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $openArgs -Encoding UTF8
    if (Test-Path -LiteralPath $openOut) { Remove-Item -LiteralPath $openOut -Force }
    try {
        $keep = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        & (Join-Path $repo 'scripts\hz-call.ps1') -Tool horizun_open_document -ArgumentsPath $openArgs `
            -Json $openOut -Quiet -TimeoutSec 1200 2>&1 | Out-Null
        $ErrorActionPreference = $keep
    }
    catch { $ErrorActionPreference = 'Stop' }
    if (Test-Path -LiteralPath $openOut) {
        $o = Get-Content -LiteralPath $openOut -Raw | ConvertFrom-Json
        if ($o.is_error) {
            $row.state = 'open_refused'
            $row.why = ($o.raw -replace '\s+', ' ')
            Say $row.why; $rows += $row; continue
        }
    }

    $title = [IO.Path]::GetFileNameWithoutExtension($MODEL[$year])
    $deadline = (Get-Date).AddMinutes(10)
    $active = $null
    while ((Get-Date) -lt $deadline) {
        $h = Ask-Health
        if ($h) {
            $a = @($h.open_documents | Where-Object { $_.is_active })
            if ($a.Count -gt 0) { $active = [string]$a[0].title; break }
        }
        Start-Sleep -Seconds 10
    }
    if (-not $active) {
        $row.state = 'model_never_active'; $row.why = 'the document never became active'
        Say $row.why; $rows += $row; continue
    }
    Say "ACTIVE: $active"

    # THE PERFORMANCE SUITE IS HERE FOR A REASON BEYOND TIMINGS. It is the only
    # harness that applies a rule declaring an array length, and Revit lays such
    # a set out over one MODEL bar diameter less than the number declared
    # (ADR-003 item 11) - measured on 2026 and on 2026 alone. Whether the other
    # four years do the same is a question only a matrix can answer, and getting
    # it wrong would mean the correction is right on one Revit and wrong on four.
    foreach ($suite in @(
            @{ key = 'rebar'; script = 'verify-rebar.ps1' },
            @{ key = 'geometry'; script = 'verify-rebar-geometry.ps1' },
            @{ key = 'performance'; script = 'verify-rebar-performance.ps1' })) {
        Say ("running " + $suite.script)
        $before = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter 'structure-*.json' -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name)
        & (Join-Path $repo ('scripts\live\' + $suite.script)) -Document $active -ArtifactDir $ArtifactDir 2>&1 |
            Select-Object -Last 6 | ForEach-Object { Write-Host ('    ' + $_) }
        $after = @(Get-ChildItem -LiteralPath $ArtifactDir -Filter 'structure-*.json' -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name)
        $new = @($after | Where-Object { $before -notcontains $_ })
        if ($new.Count -eq 0) {
            $row[$suite.key] = [ordered]@{ state = 'no_artifact'
                                           why = 'the harness wrote nothing, so nothing can be read back' }
            continue
        }
        # The counts are TOP LEVEL in the artifact and the candidate is under
        # code_candidate_commit. Reading $art.counts gives $null for every one of
        # them, and [int]$null is 0 - so a run that failed everything would have
        # been recorded as passed with 0/0. Checked against a real artifact
        # before this ever ran.
        $art = Get-Content -LiteralPath (Join-Path $ArtifactDir $new[0]) -Raw | ConvertFrom-Json
        $row[$suite.key] = [ordered]@{
            state = $(if ([int]$art.failed -eq 0 -and [int]$art.passed -gt 0) { 'passed' } else { 'failed' })
            artifact = $new[0]
            candidate = [string]$art.code_candidate_commit
            revit_year = [string]$art.revit_year
            revit_build = [string]$art.revit_build
            passed = [int]$art.passed; failed = [int]$art.failed
            unverified = [int]$art.unverified; not_covered = [int]$art.not_covered
            fixture_missing = [int]$art.fixture_missing
        }
    }

    $bad = @($row.rebar, $row.geometry, $row.performance | Where-Object { $_ -and $_.state -ne 'passed' })
    $row.state = $(if ($bad.Count -eq 0) { 'passed' } else { 'failed' })
    $rows += $row
}

if ($UseRunning) {
    Say 'leaving Revit running (-UseRunning): closing it here would make the next run pay the startup dialog again'
}
else {
    Say 'closing every Revit'
    $null = Close-EveryRevit
}

# ---------------------------------------------------------------- the matrix
Write-Host ''
Write-Host '=== the matrix ==='
foreach ($r in $rows) {
    $reb = if ($r.rebar) { "{0} {1}/{2}" -f $r.rebar.state, $r.rebar.passed, ($r.rebar.passed + $r.rebar.failed) } else { '-' }
    $geo = if ($r.geometry) { "{0} {1}/{2}" -f $r.geometry.state, $r.geometry.passed, ($r.geometry.passed + $r.geometry.failed) } else { '-' }
    $perf = if ($r.performance) { "{0} {1}/{2}" -f $r.performance.state, $r.performance.passed, ($r.performance.passed + $r.performance.failed) } else { '-' }
    Write-Host ("  {0}  {1,-20} rebar: {2,-13} geom: {3,-13} perf: {4,-13} {5}" -f
        $r.year, $r.state, $reb, $geo, $perf,
        $(if ($r.why) { '- ' + $r.why.Substring(0, [Math]::Min(60, $r.why.Length)) } else { '' }))
}

$measured = @($rows | Where-Object { $_.state -in @('passed', 'failed') })
$green = @($rows | Where-Object { $_.state -eq 'passed' })
$commits = @($rows | Where-Object { $_.commit } | ForEach-Object { $_.commit } | Sort-Object -Unique)

$out = [ordered]@{
    schema = 'horizun.structure-matrix/1'
    what_this_is =
        'The structural suites run on every installed Revit, one year at a time. A year that could not ' +
        'be measured is a ROW WITH A REASON, never an absence - the point of a matrix is to say which ' +
        'years were measured, and a missing row reads as a pass to anyone skimming.'
    generated_utc = $started.ToString('o')
    finished_utc = (Get-Date).ToUniversalTime().ToString('o')
    years_requested = $Years
    years_measured = @($measured | ForEach-Object { $_.year })
    years_green = @($green | ForEach-Object { $_.year })
    one_build = ($commits.Count -eq 1)
    one_build_means =
        'true when every year that answered reported the SAME commit. A matrix spread over two builds ' +
        'measures two products.'
    commits_seen = $commits
    rows = $rows
}
$path = Join-Path $ArtifactDir ('structure-matrix-' + (Get-Date).ToString('yyyyMMddHHmmss') + '.json')
$out | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
Write-Host ''
Write-Host ("  {0} of {1} year(s) measured, {2} green; one build: {3}" -f
    $measured.Count, $Years.Count, $green.Count, $out.one_build)
Write-Host ("  matrix: " + $path)

exit $(if ($green.Count -eq $Years.Count) { 0 } else { 1 })
