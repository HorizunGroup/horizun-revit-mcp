<#
.SYNOPSIS
  Run a set of live harnesses against ONE Revit year, in an isolated development
  session, and put the machine back the way it was - including when a harness fails.

.DESCRIPTION
  The modified paths of a release have to be measured in every Revit year that
  ships them, and doing that by hand is where a matrix goes wrong: a manifest
  left swapped, a Revit left running, a year skipped and reported as passing.

  This driver does one year at a time:

    1. REFUSES if a Revit of that year is already running. Somebody may be
       working in it, and this script will not close somebody else's session.
    2. Builds the add-in FOR THAT YEAR (bin is shared across years, so the
       previous year's output would otherwise be loaded and the TFM guard would
       refuse it - or worse, not).
    3. Enables the development session: only that year's manifest is swapped,
       for a signed copy of the build. The installed pair is not replaced.
    4. Starts Revit BY ITS EXECUTABLE and waits for the bridge to publish.
    5. Runs each harness with HORIZUN_SERVER_EXE pointed at the fresh server.
    6. Closes ONLY the Revit it started, and restores the manifest - in a
       finally block, so a harness that throws still leaves the machine clean.

  Every step's result is written to a per-year JSON summary beside the harness
  artifacts, so a year that could not run says why instead of being absent.

.PARAMETER Years
  Which Revit years to sweep. Each is independent; one blocked year does not
  stop the rest.

.PARAMETER Harness
  Harness file names under scripts/live, with their arguments as one string.
  Example: 'verify-registry-contract.ps1 -Mode matched'

.PARAMETER SkipBuild
  Use the existing bin output for the year. Only for a re-run minutes after a
  build; the TFM guard still refuses a mismatch.
#>
[CmdletBinding()]
param(
    [string[]]$Years = @('2023', '2024', '2025', '2026', '2027'),
    [Parameter(Mandatory)][string[]]$Harness,
    [string]$ArtifactRoot,
    [int]$BridgeTimeoutSec = 300,
    # A harness that measures writes needs its fixture ACTIVE, and a Revit this
    # driver just started has nothing open. Give the file per year, or one file
    # for every year: '2023=C:\hz-live\HZ23_BASE.rvt' or 'C:\hz-live\HZ_WRITE.rvt'.
    [string[]]$PrepareDocument = @(),
    # SOME REVIT YEARS OPEN A MODAL BEFORE THE BRIDGE EXISTS, and it belongs to
    # another add-in. Revit 2023 on this machine raises "External Tools - External
    # Tool Failure" from Autodesk Insights at every start; it holds the UI thread,
    # so the add-in never publishes and the year reads as no_bridge. Only titles
    # NAMED here are closed, only on the Revit this driver started, and only while
    # waiting for the bridge - nothing else on the desktop is touched.
    # A CENTRAL cannot be opened as itself. Opening it detached - preserving the
    # worksets - is the only way one machine can measure the workshared write path
    # at all, and the document is then titled '<name>_detached', which is what a
    # harness must be told to expect.
    [switch]$PrepareDetach,
    # A fixture no typed command can build. Run through horizun_execute_python
    # AFTER the document is open and BEFORE the harnesses, so a harness that would
    # otherwise report fixture_missing has its condition. Nothing is saved: the
    # staging lives as long as the session.
    [string]$PrepareScript,
    [string[]]$DismissStartupDialog = @('External Tool*'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$server = Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'
if (-not (Test-Path -LiteralPath $server)) { throw "no server build at $server. Build it first." }

# WHICH SERVER IS THIS, beyond its hash. The driver does not build the server, so
# the file it is handed may have been compiled at any commit - and a run that
# records only a SHA-256 names a file nobody can attribute afterwards. The stamp
# the build writes into the assembly is read here and compared with the tree.
$serverStamp = $null
try {
    $serverStamp = [Diagnostics.FileVersionInfo]::GetVersionInfo($server).ProductVersion
}
catch { $serverStamp = $null }
$repoHead = (& git -C $repo rev-parse HEAD).Trim()
$serverMatchesTree = ($serverStamp -ne $null) -and ($serverStamp -like "*$repoHead*")
if (-not $serverMatchesTree) {
    Write-Host ("=== THE SERVER ON DISK IS STAMPED '{0}' AND THE TREE IS AT {1}. Every run of this sweep will " +
                "record that server's hash; build it from this tree if that is not what you meant." -f
                $serverStamp, $repoHead.Substring(0, 7)) -ForegroundColor Yellow
}
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $repo 'artifacts\live\year-matrix' }
New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null

# ONE FILE FOR SEVERAL YEARS is how a matrix quietly measures the wrong thing.
# A document saved by Revit 2026 cannot be opened by 2023 at all, and one saved
# by 2023 is UPGRADED in place by 2027 - which changes the fixture the earlier
# years are supposed to share. Say it here, before anything is built, and let
# the caller decide; the per-year check after the bridge is what refuses.
$unqualified = @($PrepareDocument | Where-Object { $_ -notmatch '^\s*\d{4}\s*=' })
if ($unqualified.Count -gt 0 -and $Years.Count -gt 1) {
    Write-Host ("=== ONE DOCUMENT FOR {0} YEARS: '{1}'. Prefer a fixture per year - " +
                "-PrepareDocument '2023=...','2024=...' - because a file saved by a later Revit will not " +
                "open in an earlier one, and an earlier file is upgraded in place by a later one." -f
                $Years.Count, ($unqualified -join ', ')) -ForegroundColor Yellow
}

$discovery = Join-Path $env:USERPROFILE '.horizun\discovery'
$summary = [ordered]@{
    schema = 'horizun.year-matrix/1'
    started_utc = (Get-Date).ToUniversalTime().ToString('o')
    repo_head = (& git -C $repo rev-parse HEAD).Trim()
    repo_tracked_clean = ([string](& git -C $repo status --porcelain) -eq '')
    server_exe = $server
    server_sha256 = (Get-FileHash $server).Hash.ToLower()
    server_stamp = $serverStamp
    server_matches_tree = $serverMatchesTree
    dotnet_sdk = (& dotnet --version).Trim()
    server_deps_sha256 = $(
        $deps = [IO.Path]::ChangeExtension($server, '.deps.json')
        if (Test-Path -LiteralPath $deps) { (Get-FileHash $deps).Hash.ToLower() } else { $null })
    harnesses = $Harness
    years = @()
}

Add-Type -Namespace HzWin -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
'@ -ErrorAction SilentlyContinue

function Close-HzStartupDialog {
    param([int]$OwnerPid, [string[]]$Titles)
    $closed = @()
    if (-not $Titles -or $Titles.Count -eq 0) { return $closed }
    $cb = [HzWin.Native+EnumWindowsProc] {
        param($hWnd, $param)
        if (-not [HzWin.Native]::IsWindowVisible($hWnd)) { return $true }
        [uint32]$owner = 0
        $null = [HzWin.Native]::GetWindowThreadProcessId($hWnd, [ref]$owner)
        if ($owner -ne $OwnerPid) { return $true }
        $len = [HzWin.Native]::GetWindowTextLength($hWnd)
        if ($len -le 0) { return $true }
        $sb = New-Object System.Text.StringBuilder ($len + 1)
        $null = [HzWin.Native]::GetWindowText($hWnd, $sb, $sb.Capacity)
        $title = $sb.ToString()
        foreach ($t in $Titles) {
            if ($title -like $t) {
                $null = [HzWin.Native]::PostMessage($hWnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
                $script:HzClosedTitles += $title
                break
            }
        }
        return $true
    }
    $script:HzClosedTitles = @()
    $null = [HzWin.Native]::EnumWindows($cb, [IntPtr]::Zero)
    $script:HzClosedTitles
}

function Get-YearRevit([string]$Year) {
    @(Get-Process Revit -ErrorAction SilentlyContinue | Where-Object {
        try { $_.MainModule.FileName -like "*\Revit $Year\*" } catch { $false } })
}

foreach ($year in $Years) {
    $row = [ordered]@{ year = $year; state = 'not_run'; why = $null; document = $null; runs = @() }
    $exe = "C:\Program Files\Autodesk\Revit $year\Revit.exe"
    $enabled = $false
    $started = $null
    try {
        if (-not (Test-Path -LiteralPath $exe)) {
            $row.state = 'not_installed'; $row.why = "no Revit $year on this machine"
            $summary.years += $row; continue
        }
        # SOMEBODY ELSE'S SESSION IS NOT OURS TO CLOSE. A year already running is
        # reported as blocked and skipped; the sweep continues with the rest.
        $running = Get-YearRevit $year
        if ($running.Count -gt 0) {
            $row.state = 'blocked'
            $row.why = "Revit $year is already running (pid $($running[0].Id)). This driver will not close a session it did not start."
            $summary.years += $row; continue
        }

        if (-not $SkipBuild) {
            Write-Host "`n=== $year : building the add-in for this year ===" -ForegroundColor Cyan
            & dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') -c Release -p:RevitYear=$year -warnaserror --nologo -v q
            if ($LASTEXITCODE -ne 0) {
                $row.state = 'build_failed'; $row.why = "dotnet build -p:RevitYear=$year exited $LASTEXITCODE"
                $summary.years += $row; continue
            }
        }

        Write-Host "=== $year : enabling the development session ===" -ForegroundColor Cyan
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'dev-addin-session.ps1') -Year $year -Enable
        if ($LASTEXITCODE -ne 0) { throw "dev-addin-session -Enable exited $LASTEXITCODE" }
        $enabled = $true

        Get-ChildItem $discovery -Filter "revit-$year-*.json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        $started = Start-Process -FilePath $exe -PassThru
        Write-Host "=== $year : started pid $($started.Id); waiting for the bridge ===" -ForegroundColor Cyan

        $deadline = (Get-Date).AddSeconds($BridgeTimeoutSec)
        $bridge = $null
        $dismissed = @()
        while ((Get-Date) -lt $deadline) {
            $bridge = @(Get-ChildItem $discovery -Filter "revit-$year-$($started.Id).json" -ErrorAction SilentlyContinue)
            if ($bridge.Count -gt 0) { break }
            if ($started.HasExited) { break }
            $shut = @(Close-HzStartupDialog -OwnerPid $started.Id -Titles $DismissStartupDialog)
            foreach ($t in $shut) {
                if ($dismissed -notcontains $t) {
                    $dismissed += $t
                    Write-Host ("=== {0} : closed a startup dialog that was holding the UI thread: '{1}'" -f $year, $t) -ForegroundColor Yellow
                }
            }
            Start-Sleep -Seconds 4
        }
        # AND AFTER THE BRIDGE, TOO. Revit 2023 publishes first and raises the
        # other add-in's failure dialog a moment later, so a loop that stops at
        # the discovery file never sees it - and every call is then refused with
        # "Revit has a MODAL DIALOG open" while the bridge is perfectly healthy.
        for ($settle = 0; $settle -lt 8; $settle++) {
            $shut = @(Close-HzStartupDialog -OwnerPid $started.Id -Titles $DismissStartupDialog)
            foreach ($t in $shut) {
                if ($dismissed -notcontains $t) {
                    $dismissed += $t
                    Write-Host ("=== {0} : closed a startup dialog that was holding the UI thread: '{1}'" -f $year, $t) -ForegroundColor Yellow
                }
            }
            Start-Sleep -Seconds 2
        }
        if ($dismissed.Count -gt 0) { $row.dismissed_dialogs = $dismissed }
        if (-not $bridge -or $bridge.Count -eq 0) {
            $row.state = 'no_bridge'
            $row.why = ("Revit $year published no bridge within $BridgeTimeoutSec s. A security dialog for an " +
                        "unsigned add-in, or a modal on another monitor, holds the UI thread and looks exactly " +
                        "like this.")
            $summary.years += $row; continue
        }

        $env:HORIZUN_SERVER_EXE = $server
        $env:HORIZUN_REVIT_YEAR = $year
        $yearDir = Join-Path $ArtifactRoot $year
        New-Item -ItemType Directory -Force -Path $yearDir | Out-Null

        # THE EXACT BINARIES THIS YEAR RAN, kept where nothing overwrites them.
        # The development store holds ONE signed copy per year and the next
        # session signs over it, which is how ten signed files a record named
        # became unrecoverable. Both halves are kept: the UNSIGNED build output,
        # which is what a rebuild of the candidate can be compared against, and
        # the SIGNED file Revit actually loaded.
        $binDir = Join-Path $yearDir 'binaries'
        New-Item -ItemType Directory -Force -Path $binDir | Out-Null
        $kept = [ordered]@{}
        $signedDll = Join-Path $env:USERPROFILE ".horizun\dev-addin\$year\Horizun\Horizun.Revit.dll"
        $unsignedDll = Join-Path $repo 'src\Horizun.Revit\bin\Release\Horizun.Revit.dll'
        foreach ($pair in @(@{ k = 'addin_signed'; p = $signedDll }, @{ k = 'addin_unsigned'; p = $unsignedDll },
                            @{ k = 'server'; p = $server })) {
            if (-not (Test-Path -LiteralPath $pair.p)) { $kept[$pair.k] = $null; continue }
            $sha = (Get-FileHash -LiteralPath $pair.p).Hash.ToLower()
            $dest = Join-Path $binDir ("{0}-{1}{2}" -f $pair.k, $sha.Substring(0, 16),
                                       [IO.Path]::GetExtension($pair.p))
            if (-not (Test-Path -LiteralPath $dest)) { Copy-Item -LiteralPath $pair.p -Destination $dest -Force }
            $kept[$pair.k] = [ordered]@{ sha256 = $sha; kept_at = $dest; source = $pair.p }
        }
        $row.binaries = $kept

        # Open the fixture this year's harnesses measure, through the typed open,
        # so a failure here is reported as a fixture problem rather than as every
        # harness failing for the same reason.
        $doc = $null
        foreach ($spec in $PrepareDocument) {
            if ($spec -match '^\s*(\d{4})\s*=\s*(.+)$') { if ($Matches[1] -eq $year) { $doc = $Matches[2].Trim() } }
            elseif (-not $doc) { $doc = $spec.Trim() }
        }
        if ($doc) {
            if (-not (Test-Path -LiteralPath $doc)) {
                $row.state = 'fixture_missing'; $row.why = "the document for $year is not on this machine: $doc"
                $summary.years += $row; continue
            }
            # WHAT VERSION SAVED THIS FILE - asked of the header, not of the open.
            # horizun_file_info reads it without opening anything, so an
            # incompatible fixture is named here instead of arriving as a refusal
            # from horizun_open_document with the year already half spent.
            $infoArgs = Join-Path $yearDir 'prepare-fileinfo.args.json'
            $infoOut = Join-Path $yearDir 'prepare-fileinfo.out.json'
            (@{ paths = @($doc.Replace([char]92, '/')) } | ConvertTo-Json -Depth 5) |
                Set-Content -LiteralPath $infoArgs -Encoding utf8
            & pwsh -NoProfile -File (Join-Path $repo 'scripts\hz-call.ps1') -Tool horizun_file_info `
                -ArgumentsPath $infoArgs -Json $infoOut -Quiet -TimeoutSec 300
            $savedYear = $null
            if (Test-Path -LiteralPath $infoOut) {
                try {
                    $info = Get-Content -LiteralPath $infoOut -Raw | ConvertFrom-Json
                    $body = $null
                    if ($info.result -and $info.result.structuredContent) { $body = $info.result.structuredContent }
                    elseif ($info.result) { $body = $info.result }
                    elseif ($info.raw) { $body = $info.raw | ConvertFrom-Json }
                    $first = @($body.files)[0]
                    if ($first) {
                        foreach ($field in 'revit_version', 'saved_in_version', 'format') {
                            if ($null -ne $first.$field -and [string]$first.$field -match '(20\d\d)') {
                                $savedYear = [int]$Matches[1]; break
                            }
                        }
                    }
                }
                catch { $savedYear = $null }
            }
            $row.fixture_saved_version = $savedYear
            if ($savedYear -and $savedYear -gt [int]$year) {
                $row.state = 'fixture_incompatible'
                $row.why = ("the document was saved by Revit $savedYear and cannot be opened by Revit $year. " +
                            "Give this year its own fixture: -PrepareDocument '$year=<a file saved by $year or earlier>'")
                $summary.years += $row; continue
            }
            if ($savedYear -and $savedYear -lt [int]$year) {
                Write-Host ("=== {0} : the fixture was saved by Revit {1} and WILL BE UPGRADED in place by {0}" -f
                            $year, $savedYear) -ForegroundColor Yellow
                $row.fixture_upgraded_on_open = $true
            }
            $script:PrepareTag = 0
            $script:OpenedTitle = $null
            function Open-YearFixture {
                param([string]$Path, [string]$Year, [string]$Dir, [string]$Repo)
                $script:PrepareTag++
                $argsFile = Join-Path $Dir ("prepare-open-$script:PrepareTag.args.json")
                $outFile = Join-Path $Dir ("prepare-open-$script:PrepareTag.out.json")
                # A literal backslash, by code point: written as a regex it was an
                # invalid pattern, and written as '\' it matched two of them.
                $openArgs = @{ path = ($Path.Replace([char]92, '/')); expected_version = $Year; activate = $true
                    idempotency_key = ('year-matrix-open-' + $Year + '-' + (Get-Date -Format 'yyyyMMddHHmmssfff')) }
                if ($PrepareDetach) { $openArgs['detach'] = $true }
                ($openArgs | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $argsFile -Encoding utf8
                & pwsh -NoProfile -File (Join-Path $Repo 'scripts\hz-call.ps1') -Tool horizun_open_document `
                    -ArgumentsPath $argsFile -Json $outFile -Quiet -TimeoutSec 900
                if (-not (Test-Path -LiteralPath $outFile)) { return $false }
                $reply = Get-Content -LiteralPath $outFile -Raw | ConvertFrom-Json
                # WHAT THE DOCUMENT IS ACTUALLY CALLED NOW. A detached open renames
                # it - HZ_CLOSED_L becomes HZ_CLOSED_L_detached, and the second one
                # in a session becomes _detached_1 - and a harness told the file's
                # name refuses, correctly, because that is not the active document.
                try {
                    $body = $null
                    # `raw` is the reply plus whatever the caller printed after it,
                    # so parsing it as JSON throws on the trailing text and the
                    # title came back empty. `result` is already the parsed body.
                    if ($reply.result -and $reply.result.structuredContent) { $body = $reply.result.structuredContent }
                    elseif ($reply.result) { $body = $reply.result }
                    elseif ($reply.raw) { $body = $reply.raw | ConvertFrom-Json }
                    foreach ($cand in @($body.active_document, $body.document.title, $body.title)) {
                        if ($cand) { $script:OpenedTitle = [string]$cand; break }
                    }
                }
                catch { }
                return (-not $reply.is_error)
            }
            if (-not (Open-YearFixture -Path $doc -Year $year -Dir $yearDir -Repo $repo)) {
                $row.state = 'fixture_open_failed'
                $row.why = "horizun_open_document refused $doc on Revit $year; see $yearDir"
                $summary.years += $row; continue
            }
            $row.document = $doc
            $row.document_title = $script:OpenedTitle

            if ($PrepareScript) {
                if (-not (Test-Path -LiteralPath $PrepareScript)) {
                    $row.state = 'fixture_missing'
                    $row.why = "the preparation script is not on this machine: $PrepareScript"
                    $summary.years += $row; continue
                }
                $stageArgs = Join-Path $yearDir 'prepare-script.args.json'
                $stageOut = Join-Path $yearDir 'prepare-script.out.json'
                (@{ code_path = ($PrepareScript.Replace([char]92, '/')); dry_run = $false
                    target_document = $script:OpenedTitle
                    idempotency_key = ('year-matrix-stage-' + $Year + '-' + (Get-Date -Format 'yyyyMMddHHmmssfff')) } |
                    ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $stageArgs -Encoding utf8
                & pwsh -NoProfile -File (Join-Path $repo 'scripts\hz-call.ps1') -Tool horizun_execute_python `
                    -ArgumentsPath $stageArgs -Json $stageOut -Quiet -TimeoutSec 900
                $stageOk = $false
                if (Test-Path -LiteralPath $stageOut) {
                    $reply = Get-Content -LiteralPath $stageOut -Raw | ConvertFrom-Json
                    $stageOk = (-not $reply.is_error)
                }
                # The script's own word, not the bridge's: execute_python carries
                # no verification of its own, and this is staging, not evidence.
                $row.prepare_script = @{ path = $PrepareScript; self_reported_ok = $stageOk; artifact = $stageOut }
                if (-not $stageOk) {
                    $row.state = 'fixture_missing'
                    $row.why = "the preparation script did not report success; see $stageOut"
                    $summary.years += $row; continue
                }
                Write-Host ("=== {0} : staged the fixture with {1}" -f $year, (Split-Path $PrepareScript -Leaf))
            }
        }

        foreach ($h in $Harness) {
            $file = (($h.Trim()) -split '\s+')[0]
            $path = Join-Path $PSScriptRoot $file
            if (-not (Test-Path -LiteralPath $path)) {
                $row.runs += [ordered]@{ harness = $file; state = 'missing'; exit_code = $null }
                continue
            }
            # A harness that opened a document of its own left it ACTIVE, and the
            # next one was refused for measuring a model nobody asked it about.
            # Re-activating here makes harness ORDER stop mattering - EXCEPT for a
            # detached open, which does not re-activate a document but MAKES
            # ANOTHER ONE: opening the same central detached twice leaves
            # <name>_detached and <name>_detached_1, and the harness was then told
            # the name of the first while the second was active.
            $null = Close-HzStartupDialog -OwnerPid $started.Id -Titles $DismissStartupDialog
            if ($doc -and -not $PrepareDetach) {
                $null = Open-YearFixture -Path $doc -Year $year -Dir $yearDir -Repo $repo
            }
            # {title} is what the document ended up being called, read AFTER the
            # re-open: spelling it out in the caller's string is impossible for a
            # detached open, whose name is only known once Revit has made it.
            $parts = $h.Trim()
            if ($script:OpenedTitle) { $parts = $parts.Replace('{title}', $script:OpenedTitle) }
            $rest = $parts.Substring($file.Length).Trim()
            Write-Host "--- $year : $file $rest" -ForegroundColor DarkCyan
            $cmd = "& '$path' $rest -ArtifactDir '$yearDir'"
            & pwsh -NoProfile -Command $cmd
            $code = $LASTEXITCODE
            $row.runs += [ordered]@{
                harness = $file; arguments = $rest; exit_code = $code
                state = switch ($code) { 0 { 'green' } 1 { 'failed' } 2 { 'unverified' } 3 { 'not_covered' } default { "exit_$code" } }
            }
        }
        $row.state = if (@($row.runs | Where-Object { $_.state -ne 'green' }).Count -eq 0) { 'green' } else { 'partial' }
    }
    catch {
        $row.state = 'error'; $row.why = $_.Exception.Message
    }
    finally {
        # THE MACHINE GOES BACK EVEN WHEN THE TEST FAILS. A manifest left swapped
        # is a Revit that loads a development build tomorrow without anybody
        # meaning it to.
        if ($started -and -not $started.HasExited) {
            try { Stop-Process -Id $started.Id -Force -ErrorAction Stop; Start-Sleep -Seconds 6 } catch { }
        }
        if ($enabled) {
            try { & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'dev-addin-session.ps1') -Year $year -Restore }
            catch { $row.why = ($row.why + " ; RESTORE FAILED: " + $_.Exception.Message).Trim(' ;') }
        }
        Get-ChildItem $discovery -Filter "revit-$year-*.json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        $summary.years += $row
    }
}

$summary.finished_utc = (Get-Date).ToUniversalTime().ToString('o')
$out = Join-Path $ArtifactRoot ('year-matrix-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.json')
($summary | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $out -Encoding utf8

Write-Host "`n== year matrix ==" -ForegroundColor Cyan
foreach ($y in $summary.years) {
    $colour = switch ($y.state) { 'green' { 'Green' } 'blocked' { 'Yellow' } 'not_installed' { 'Yellow' } default { 'Red' } }
    Write-Host ("  {0,-6} {1,-14} {2}" -f $y.year, $y.state, $y.why) -ForegroundColor $colour
    foreach ($r in $y.runs) { Write-Host ("           {0,-38} {1}" -f $r.harness, $r.state) -ForegroundColor DarkGray }
}
Write-Host "  summary: $out" -ForegroundColor Cyan
$bad = @($summary.years | Where-Object { $_.state -notin @('green', 'blocked', 'not_installed') })
exit $(if ($bad.Count -gt 0) { 1 } else { 0 })
