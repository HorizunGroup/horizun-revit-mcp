<#
.SYNOPSIS
  Run a set of live harnesses against ONE Revit year, in an isolated development
  session, and put the machine back the way it was - including when a harness fails.

.DESCRIPTION
  The modified paths of a release have to be measured in every Revit year that
  ships them, and doing that by hand is where a matrix goes wrong: a manifest
  left swapped, a Revit left running, a year skipped and reported as passing.

  This driver does one year at a time:

    1. REFUSES if a Revit of that year is already running - or if any Revit is
       running whose year could NOT be determined, because an unreadable
       MainModule is not "no Revit". Somebody may be working in it, and this
       script will not close somebody else's session.
    2. Builds the add-in FOR THAT YEAR (bin is shared across years, so the
       previous year's output would otherwise be loaded and the TFM guard would
       refuse it - or worse, not).
    3. Enables the development session: only that year's manifest is swapped,
       for a signed copy of the build. The installed pair is not replaced.
    4. Starts Revit BY ITS EXECUTABLE and waits for the bridge to publish.
    5. Runs each harness with HORIZUN_SERVER_EXE pointed at the fresh server,
       and HORIZUN_HARNESS_DOCUMENTS_MANIFEST pointed at a fresh JSON per harness
       run in the year's artifact folder (cleared afterwards). A harness that
       creates and opens models of its own - verify-live.ps1 does, under
       %TEMP%\horizun-live-<run> - writes there which ones (schema
       horizun.harness-documents/v1, rewritten in a finally so a harness that
       dies half-way still declares them). After the harness, each entry is
       adopted into the SAME register (kind 'harness_scratch') only if the
       schema is right, the scratch folder is a direct child of the temp
       directory named horizun-live-<probe_run>, is not a link, and was created
       AFTER this driver started that Revit, and the path is inside that folder
       (no '..'), is a .rvt/.rfa and exists. Anything refused is written down in
       the run's harness_documents and stays foreign; a model of that folder the
       manifest did not list stays foreign too. Before 2026-09-24 nothing was
       adopted and every year ended left_running_foreign_document.
    6. Closes ONLY the Revit it started - and only after proving, right before
       the close, that it is still the same process (pid + start time +
       executable), that the bridge can say what is open AND answers for that
       pid, and that every open document is one THIS RUN REGISTERED, matched by
       the path the bridge publishes rather than by its title (a harness's
       adopted scratch models are closed the same way, discarding changes: they
       are that harness's own disposable copies). One foreign
       document, one doubtful identity or one unanswered question and the Revit
       is LEFT RUNNING, written down as recovery pending. Each close is aimed at
       the registered path and refused if the bridge's own rehearsal resolves a
       different document; the open set is re-read before every close and once
       more before the process is asked to exit normally through its own window.
       It is never killed. Then the manifest is restored - only with no Revit of
       that year and no Revit of an unknown year running, and believed only when
       the restore's exit code AND the disk agree with the snapshot taken before
       anything changed. See year-matrix.session.ps1 for the rules and their tests.

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
    # A FIXTURE SAVED BY AN EARLIER REVIT IS UPGRADED IRREVERSIBLY when a later one
    # opens it, and the bridge refuses to do that unless it is asked in words. One
    # base fixture with an independent copy per year is exactly that case, so the
    # permission is a switch of this driver rather than something it assumes: the
    # per-year check above already prints WHICH year saved the file, and the copy
    # named in -PrepareDocument is the only file this can touch.
    [switch]$PrepareAllowUpgrade,
    # A fixture no typed command can build. Run through horizun_execute_python
    # AFTER the document is open and BEFORE the harnesses, so a harness that would
    # otherwise report fixture_missing has its condition. Nothing is saved: the
    # staging lives as long as the session.
    [string]$PrepareScript,
    [string[]]$DismissStartupDialog = @('External Tool*'),
    # THERE IS NO -RehearsalTitlePattern ANY MORE, deliberately. It used to grant
    # ownership by title - '^HZ_' among them - and a title is not a proof: a
    # user's model called HZ_PROYECTO_USUARIO satisfied it exactly as well as a
    # fixture did, and the register below would then have closed it. Ownership is
    # now the path of a document this run itself opened, read back from the
    # bridge; see year-matrix.session.ps1.
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$server = Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'
if (-not (Test-Path -LiteralPath $server)) { throw "no server build at $server. Build it first." }
. (Join-Path $PSScriptRoot 'year-matrix.session.ps1')
$probes = New-HzMatrixProbes -Repo $repo -ServerExe $server

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
    # -f binds tighter than +: without the inner parentheses only the second
    # string is formatted and the warning prints a literal '{0}' (seen 2026-09-09).
    Write-Host (("=== THE SERVER ON DISK IS STAMPED '{0}' AND THE TREE IS AT {1}. Every run of this sweep will " +
                 "record that server's hash; build it from this tree if that is not what you meant.") -f
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
$repoStatus = Get-HzRepoStatus -Repo $repo
$summary = [ordered]@{
    schema = 'horizun.year-matrix/1'
    started_utc = (Get-Date).ToUniversalTime().ToString('o')
    repo_head = (& git -C $repo rev-parse HEAD).Trim()
    repo_tracked_clean = ($repoStatus.Count -eq 0)
    repo_status = $repoStatus
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
        # Only a title in the allow-list, and never one that could be a save,
        # discard, sync or security prompt - those are a person's decisions.
        if (Test-HzDialogTitleAllowed -Title $title -Allowed $Titles) {
            $null = [HzWin.Native]::PostMessage($hWnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
            $script:HzClosedTitles += $title
        }
        return $true
    }
    $script:HzClosedTitles = @()
    $null = [HzWin.Native]::EnumWindows($cb, [IntPtr]::Zero)
    $script:HzClosedTitles
}

foreach ($year in $Years) {
    $row = [ordered]@{ year = $year; state = 'not_run'; why = $null; document = $null; runs = @() }
    $exe = "C:\Program Files\Autodesk\Revit $year\Revit.exe"
    $enabled = $false
    $started = $null
    $identity = $null
    # The register of what this run opens. Nothing is in it yet, and nothing that
    # is not in it will be closed - including a document whose title looks like a
    # fixture's.
    $ledger = New-HzRehearsalLedger
    $yearDir = Join-Path $ArtifactRoot $year
    New-Item -ItemType Directory -Force -Path $yearDir | Out-Null
    # THE BASELINE THE RESTORE IS JUDGED AGAINST, read before anything changes:
    # which manifests exist, what the installed one points at, and whether the
    # installed DLL is there and hashable. A hash alone could not tell "there was
    # no installation" from "the installation vanished".
    $stateAtStart = Get-HzYearStateAtStart -Probes $probes -Year $year
    $row.state_at_start = $stateAtStart
    $row.installed_dll_sha256_at_start = [string](Get-HzField $stateAtStart.installed_dll 'sha256')
    try {
        if (-not (Test-Path -LiteralPath $exe)) {
            $row.state = 'not_installed'; $row.why = "no Revit $year on this machine"
            continue
        }
        # SOMEBODY ELSE'S SESSION IS NOT OURS TO CLOSE. A year already running is
        # reported as blocked and skipped; the sweep continues with the rest. So is
        # a Revit whose year could not be determined: it MAY be this year, and an
        # unreadable MainModule is not "no Revit".
        $allowed = Test-HzManifestChangeAllowed -Probes $probes -Year $year
        if (-not $allowed.ok) {
            $row.state = 'blocked'
            $row.why = $allowed.why + ' This driver will not close a session it did not start.'
            $row.blocking_pids = @($allowed.pids)
            $row.revit_processes = $allowed.classes
            continue
        }

        if (-not $SkipBuild) {
            Write-Host "`n=== $year : building the add-in for this year ===" -ForegroundColor Cyan
            & dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') -c Release -p:RevitYear=$year -warnaserror --nologo -v q
            if ($LASTEXITCODE -ne 0) {
                $row.state = 'build_failed'; $row.why = "dotnet build -p:RevitYear=$year exited $LASTEXITCODE"
                continue
            }
        }

        Write-Host "=== $year : enabling the development session ===" -ForegroundColor Cyan
        $enable = Invoke-HzYearEnable -Probes $probes -Year $year
        if (-not $enable.ok) {
            # NOTHING STARTS AFTER A FAILED ENABLE. A Revit started here would load
            # whatever manifest the failure left, and the finally block must not
            # find a process it cannot account for.
            $row.state = 'enable_failed'; $row.why = $enable.why
            continue
        }
        $enabled = $true

        # Other instances' discovery files are theirs: nothing is deleted here.
        # The wait below looks for the file of the pid this driver starts.
        $started = Start-Process -FilePath $exe -PassThru
        $identity = New-HzSessionIdentity -Probes $probes -ProcessId $started.Id -ExpectedExe $exe
        $row.session = $identity
        # The register belongs to THIS process from here on. A register with no
        # session, or one bound to another pid, closes nothing.
        Register-HzRehearsalSession -Ledger $ledger -Identity $identity
        # The close path may meet a LATE third-party modal (Revit 2023 raised its
        # "External Tool Failure" a minute after start, 2026-09-09). The module may
        # ask this probe to dismiss it: only a title in -DismissStartupDialog, only
        # on the pid this driver started, and Test-HzDialogTitleAllowed refuses
        # every save/discard/sync/security title before any window is touched.
        $script:HzOwnPid = $started.Id
        $probes.DismissStartupDialog = {
            param([string]$Title)
            if (-not (Test-HzDialogTitleAllowed -Title $Title -Allowed $script:DismissStartupDialog)) { return @() }
            return @(Close-HzStartupDialog -OwnerPid $script:HzOwnPid -Titles $script:DismissStartupDialog)
        }
        Write-Host ("=== {0} : started pid {1} ({2}, {3}); waiting for the bridge ===" -f $year, $identity.pid, $identity.exe, $identity.start_time) -ForegroundColor Cyan

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
            continue
        }

        $env:HORIZUN_SERVER_EXE = $server
        $env:HORIZUN_REVIT_YEAR = $year

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
        # THE ADD-IN THIS YEAR ACTUALLY LOADS, as a token the harness strings can
        # carry: '{addin_sha256}' becomes the SHA-256 of the signed copy the session
        # enabled (the unsigned build if no certificate signed it). A harness that
        # demands -ExpectedAddinSha256 can then refuse a wrong binary per year
        # without the caller knowing the hash before the build.
        $script:AddinSha = $null
        if ($kept['addin_signed']) { $script:AddinSha = $kept['addin_signed'].sha256 }
        elseif ($kept['addin_unsigned']) { $script:AddinSha = $kept['addin_unsigned'].sha256 }

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
                continue
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
                continue
            }
            if ($savedYear -and $savedYear -lt [int]$year) {
                Write-Host ("=== {0} : the fixture was saved by Revit {1} and WILL BE UPGRADED in place by {0}" -f
                            $year, $savedYear) -ForegroundColor Yellow
                $row.fixture_upgraded_on_open = $true
            }
            $script:PrepareTag = 0
            $script:OpenedTitle = $null
            $script:LastRegistration = $null
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
                if ($PrepareAllowUpgrade) { $openArgs['allow_upgrade'] = $true }
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
                # REGISTERED FROM WHAT THE BRIDGE SAYS IS OPEN, not from what was
                # asked for. The file this driver named and the document Revit ended
                # up with are not always the same thing - a detached open renames it
                # and gives it NO path at all - and the register has to hold the
                # identity the close path will compare against, which is the one
                # health publishes. A registration that cannot be made is recorded
                # and the session is simply never closed automatically.
                if (-not $reply.is_error -and $script:OpenedTitle) {
                    $reg = Register-HzOpenedDocument -Probes $probes -Ledger $ledger -Identity $identity `
                        -Year $Year -Dir $Dir -SourceFile $Path -ExpectedTitle $script:OpenedTitle
                    $script:LastRegistration = $reg
                    if (-not $reg.ok) {
                        Write-Host ("=== {0} : the open could NOT be registered ({1}). This session will not be closed automatically." -f
                                    $Year, $reg.why) -ForegroundColor Yellow
                    }
                }
                return (-not $reply.is_error)
            }
            $opened = Open-YearFixture -Path $doc -Year $year -Dir $yearDir -Repo $repo
            if (-not $opened) {
                # A late startup modal refuses the open (Revit 2023, 2026-09-09: the
                # dialog came after the settle window above). Dismiss ONLY an
                # allow-listed title on this pid, then ask once more.
                $lastOut = Join-Path $yearDir ("prepare-open-$script:PrepareTag.out.json")
                $refusal = if (Test-Path -LiteralPath $lastOut) { Get-Content -LiteralPath $lastOut -Raw } else { '' }
                $modal = Get-HzModalDialogTitle -Text $refusal
                if ($modal) {
                    $shut = @(& $probes.DismissStartupDialog $modal)
                    foreach ($t in $shut) {
                        if ($dismissed -notcontains $t) { $dismissed += $t }
                        Write-Host ("=== {0} : closed a startup dialog that refused the open: '{1}'" -f $year, $t) -ForegroundColor Yellow
                    }
                    if ($shut.Count -gt 0) {
                        $row.dismissed_dialogs = $dismissed
                        Start-Sleep -Seconds 2
                        $opened = Open-YearFixture -Path $doc -Year $year -Dir $yearDir -Repo $repo
                    }
                }
            }
            if (-not $opened) {
                $row.state = 'fixture_open_failed'
                $row.why = "horizun_open_document refused $doc on Revit $year; see $yearDir"
                continue
            }
            $row.document = $doc
            $row.document_title = $script:OpenedTitle
            # What the register actually holds for this year, so the summary shows
            # WHY a session was or was not closed automatically.
            $row.document_registration = $script:LastRegistration
            $row.registered_documents = @($ledger.documents)
            if (@($ledger.registration_failures).Count -gt 0) { $row.registration_failures = @($ledger.registration_failures) }

            if ($PrepareScript) {
                if (-not (Test-Path -LiteralPath $PrepareScript)) {
                    $row.state = 'fixture_missing'
                    $row.why = "the preparation script is not on this machine: $PrepareScript"
                    continue
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
                    continue
                }
                Write-Host ("=== {0} : staged the fixture with {1}" -f $year, (Split-Path $PrepareScript -Leaf))
            }
        }

        $harnessIndex = 0
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
            if ($script:AddinSha) { $parts = $parts.Replace('{addin_sha256}', $script:AddinSha) }
            $rest = $parts.Substring($file.Length).Trim()
            Write-Host "--- $year : $file $rest" -ForegroundColor DarkCyan
            $cmd = "& '$path' $rest -ArtifactDir '$yearDir'"
            # WHAT THE HARNESS MADE FOR ITSELF. A harness that creates and opens its
            # own disposable models (verify-live: HZ_LINKSRC_<tag>.rvt,
            # w12-linkcopy-<tag>.rvt ...) declares them in a manifest of its own,
            # one fresh file per harness run; any file of that name left over is
            # removed first, so a stale claim is never read as this run's.
            $harnessIndex++
            $docsManifest = Join-Path $yearDir ("harness-documents-{0:D2}-{1}.json" -f $harnessIndex,
                                                [IO.Path]::GetFileNameWithoutExtension($file))
            Remove-Item -LiteralPath $docsManifest -Force -ErrorAction SilentlyContinue
            $env:HORIZUN_HARNESS_DOCUMENTS_MANIFEST = $docsManifest
            try {
                & pwsh -NoProfile -Command $cmd
                $code = $LASTEXITCODE
            }
            finally { Remove-Item Env:\HORIZUN_HARNESS_DOCUMENTS_MANIFEST -ErrorAction SilentlyContinue }
            # Adopted into the SAME register, only entry by entry and only under
            # every rule of Register-HzHarnessDocuments (folder under TEMP, younger
            # than this Revit, path inside it, file present). What is refused is
            # written down and stays foreign - the close then leaves Revit running.
            $adopted = Register-HzHarnessDocuments -Ledger $ledger -Identity $identity -ManifestPath $docsManifest
            if (@($adopted.rejected).Count -gt 0 -or $adopted.state -eq 'manifest_rejected') {
                Write-Host ("=== {0} : {1} - harness documents NOT adopted: {2}" -f $year, $file, $adopted.why) -ForegroundColor Yellow
                foreach ($rj in @($adopted.rejected)) { Write-Host ("      {0}: {1}" -f $rj.path, $rj.why) -ForegroundColor Yellow }
            }
            $row.runs += [ordered]@{
                harness = $file; arguments = $rest; exit_code = $code
                state = switch ($code) { 0 { 'green' } 1 { 'failed' } 2 { 'unverified' } 3 { 'not_covered' } default { "exit_$code" } }
                harness_documents = $adopted
            }
        }
        $row.registered_documents = @($ledger.documents)
        $row.state = if (@($row.runs | Where-Object { $_.state -ne 'green' }).Count -eq 0) { 'green' } else { 'partial' }
    }
    catch {
        $row.state = 'error'; $row.why = $_.Exception.Message
    }
    finally {
        # THE MACHINE GOES BACK EVEN WHEN THE TEST FAILS - but never by force. A
        # Revit this driver started is closed only after it is proved to be the
        # same process with only the rehearsal's own fixtures open; otherwise it
        # is left running and written down. A manifest is restored only with no
        # Revit of that year running, and the restore is checked on disk.
        $pending = @()
        Remove-Item Env:\HORIZUN_HARNESS_DOCUMENTS_MANIFEST -ErrorAction SilentlyContinue
        if ($identity) {
            # AN EXCEPTION IN HERE MUST NOT COST THE REPORT. If the close path
            # throws - an unreadable process, a probe that dies - the failure is
            # recorded, the restore is still evaluated (with its own guards), and
            # the year still appears in the summary. A finally block that throws
            # takes the whole sweep with it and leaves nothing written down.
            try {
                $close = Close-HzRehearsalSession -Probes $probes -Identity $identity -Ledger $ledger -Year $year -Dir $yearDir
            }
            catch {
                $close = [ordered]@{ state = 'left_running_error'; identity = $identity
                                     why = ("the close path itself failed: " + $_.Exception.Message +
                                            " Nothing was closed by force; the process is left as it is.") }
            }
            $row.close = $close
            if ($close.state -notin @('closed', 'already_exited')) {
                Write-Host ("=== {0} : REVIT LEFT RUNNING ({1}): {2}" -f $year, $close.state, $close.why) -ForegroundColor Red
                $pending += "close: $($close.state) - $($close.why)"
            }
            elseif ($close.state -eq 'closed') {
                # Only the discovery file of the pid this driver started and closed.
                Get-ChildItem $discovery -Filter "revit-$year-$($identity.pid).json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
        try {
            $restore = Restore-HzYearSession -Probes $probes -Year $year -StateAtStart $stateAtStart
        }
        catch {
            $restore = @{ ok = $false; state = 'restore_failed'
                          why = ("the restore path itself failed: " + $_.Exception.Message) }
        }
        $row.restore = $restore
        if (-not $restore.ok) {
            Write-Host ("=== {0} : MANIFEST NOT RESTORED ({1}): {2}" -f $year, $restore.state, $restore.why) -ForegroundColor Red
            $pending += "restore: $($restore.state) - $($restore.why)"
        }
        if ($pending.Count -gt 0) {
            $row.recovery_pending = Write-HzRecoveryPending -Dir $ArtifactRoot -Year $year -Record ([ordered]@{ session = $identity; close = $row.close; restore = $restore; state_before = $row.state; why_before = $row.why })
            # The summary keeps what happened BEFORE the close, too: a row that
            # only said "recovery_pending" hid that the open had been refused.
            $row.state_before = $row.state
            $row.why_before = $row.why
            $row.state = 'recovery_pending'
            $row.why = (($pending -join ' ; ') + " ; see " + $row.recovery_pending)
        }
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
$pendingRows = @($summary.years | Where-Object { $_.state -eq 'recovery_pending' })
foreach ($p in $pendingRows) { Write-Host ("  RECOVERY PENDING for {0}: {1}" -f $p.year, $p.recovery_pending) -ForegroundColor Red }
$bad = @($summary.years | Where-Object { $_.state -notin @('green', 'blocked', 'not_installed') })
exit $(if ($pendingRows.Count -gt 0) { 2 } elseif ($bad.Count -gt 0) { 1 } else { 0 })
