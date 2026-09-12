# -----------------------------------------------------------------------------
# Horizun Revit MCP — original Horizun code.
#
# The safety rules of the year-matrix driver, exercised WITHOUT a Revit and
# without anybody's session: canned bridge answers, a temp "manifest" folder,
# and harmless helper processes (a minimised pwsh sleeping) standing in for the
# Revit the rehearsal started. Every case ends with the helper cleaned up by
# this script - the only processes it ever stops are the ones it started here.
#
# Two of the cases use a real process CALLED Revit.exe (a copy of waitfor.exe,
# which has no window and exits on its own): that is the only way to prove that
# a Revit whose year cannot be determined blocks a manifest change in the REAL
# probe and in the REAL helper script, rather than only in a canned answer.
#
#   pwsh -NoProfile -File scripts/live/year-matrix.session.tests.ps1
#   exit 0 = every case held; 1 = at least one did not.
# -----------------------------------------------------------------------------
[CmdletBinding()]
param(
    # WHILE SOMEBODY IS WORKING IN REVIT. Three cases need a process the machine
    # calls "Revit": a copy of waitfor.exe named Revit.exe, which is the only way
    # to prove that the REAL probe and the REAL helper refuse a manifest change
    # next to a Revit whose year cannot be determined. It is not Revit, it has no
    # window, it exits on its own, its helper call runs against a temp %APPDATA%
    # and it can touch no manifest - but it DOES appear in the process list under
    # that name, so it is skippable by name rather than by anybody's judgement.
    # Skipped cases are counted and printed as SKIP; they are never counted as
    # passed.
    [switch]$SkipProcessNamedRevit,
    # A machine-readable result, for the closure evaluator. Console text is for a
    # person; a consolidator that had to parse it would be reading prose.
    [string]$SummaryPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'year-matrix.session.ps1')

$script:Passed = 0; $script:Failed = 0; $script:Skipped = 0; $script:Helpers = @()
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:Passed++; Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green }
    else { $script:Failed++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) -ForegroundColor Red }
}
# A case the MACHINE would not let run is never a pass. It is counted and named.
$script:SkippedCases = @()
function Skip([string]$Name, [string]$Why) {
    $script:Skipped++
    $script:SkippedCases += ("{0} ({1})" -f $Name, $Why)
    Write-Host ("  SKIP  {0}  {1}" -f $Name, $Why) -ForegroundColor Yellow
}
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function Get-FreeRevitYear {
    # The helper tests rename manifests under a TEMP %APPDATA%, but the helper's
    # own guard reads the real process list - so they need a year with no Revit
    # running. Whichever year that is depends on what the person at this machine
    # is doing, so it is asked rather than assumed.
    $p = New-HzMatrixProbes -Repo $script:RepoRoot -ServerExe 'C:\nowhere\horizun-mcp.exe'
    foreach ($y in '2023', '2024', '2025', '2026', '2027') {
        if ((Test-HzManifestChangeAllowed -Probes $p -Year $y).ok) { return $y }
    }
    return $null
}
function Start-Helper {
    # A process with a real main window that exits normally on WM_CLOSE, started
    # minimised so it takes no focus. It stands in for "the Revit we started".
    $p = Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 600' -WindowStyle Minimized -PassThru
    $script:Helpers += $p
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 200 }
    return $p
}
function Stop-Helpers {
    foreach ($h in $script:Helpers) { try { if (-not $h.HasExited) { $h.Kill(); $h.WaitForExit(5000) | Out-Null } } catch { } }
    $script:Helpers = @()
}
$tmp = Join-Path $env:TEMP ('hz-matrix-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
# The bridge's anchor lives under the data root; the tests use a folder of their
# own so the anchor rule is exercised without depending on this machine's state.
$anchorDir = Join-Path $tmp 'anchor'
New-Item -ItemType Directory -Force -Path $anchorDir | Out-Null

function Doc([string]$Title, [string]$Path, $Active = $false) {
    return [ordered]@{ title = $Title; path = $Path; is_active = $Active }
}

# A probe table whose every outside effect is canned; tests override entries.
function New-TestProbes {
    param([hashtable]$Manifest, $Docs = @(), [bool]$HealthOk = $true, [int]$EnableCode = 0, [int]$RestoreCode = 0,
          $RevitProcesses = @(), [string]$InstalledSha = 'aaaa', [string]$HealthErrorOnce = '',
          [bool]$InstalledDllPresent = $true, [string]$InstalledDllError = $null)
    $state = @{ manifest = $Manifest; docs = @($Docs); healthOk = $HealthOk; enable = $EnableCode; restore = $RestoreCode
                revit = @($RevitProcesses); sha = $InstalledSha; closeCalls = @(); closeRequests = @(); enableCalls = 0; restoreCalls = 0
                healthPid = $null; healthCalls = 0; healthErrorOnce = $HealthErrorOnce; dismissCalls = @()
                dllPresent = $InstalledDllPresent; dllError = $InstalledDllError
                revitCalls = 0; revitLater = $null; onClose = $null; healthNoDocsField = $false }
    $real = New-HzMatrixProbes -Repo (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) -ServerExe 'C:\nowhere\horizun-mcp.exe'
    $probes = @{
        GetProcess = $real.GetProcess
        CloseMainWindow = $real.CloseMainWindow
        WaitExit = $real.WaitExit
        # Canned process list. `,` on purpose: the real probe protects its array the
        # same way, and the classifier must survive it (it did not - see the
        # 'assign, then wrap' comment in the module).
        RevitProcesses = {
            $state.revitCalls++
            if ($state.revitCalls -gt 1 -and $null -ne $state.revitLater) { return ,@($state.revitLater) }
            return ,@($state.revit) }.GetNewClosure()
        Health = { param($Year, $Dir)
            $state.healthCalls++
            if ($state.healthErrorOnce -and $state.healthCalls -eq 1) { return @{ ok = $false; error = $state.healthErrorOnce } }
            if (-not $state.healthOk) { return @{ ok = $false; error = 'the bridge did not answer (simulated)' } }
            $h = @{ ok = $true; documents = @($state.docs); document_count = @($state.docs).Count }
            if ($state.healthPid) { $h['process_id'] = $state.healthPid }
            return $h }.GetNewClosure()
        CloseDocument = { param($Request)
            $t = [string]$Request['target']
            $state.closeCalls += $t
            $state.closeRequests += $Request
            # A close that really closes: the document leaves the open set, which is
            # what the caller re-reads to believe it.
            $state.docs = @(@($state.docs) | Where-Object {
                ([string]$_['title'] -ne [string]$Request['expect_title']) -or ([string]$_['path'] -ne [string]$Request['expect_path']) })
            if ($null -ne $state.onClose) { & $state.onClose $state $Request }
            return @{ ok = $true; closed = $true } }.GetNewClosure()
        Enable = { param($Year) $state.enableCalls++; if ($state.enable -eq 0) { $state.manifest.dev_present = $true; $state.manifest.aside_present = $true }; return $state.enable }.GetNewClosure()
        Restore = { param($Year) $state.restoreCalls++; if ($state.restore -eq 0) { $state.manifest.dev_present = $false; $state.manifest.aside_present = $false; $state.manifest.installed_present = $true }; return $state.restore }.GetNewClosure()
        ManifestState = { param($Year)
            $dir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
            $expected = Get-HzNormalizedPath (Join-Path $dir 'Horizun\Horizun.Revit.dll')
            $installedAssembly = $expected
            if ($state.manifest.ContainsKey('installed_assembly')) { $installedAssembly = $state.manifest.installed_assembly }
            $err = $null
            if ($state.manifest.ContainsKey('installed_manifest_error')) { $err = $state.manifest.installed_manifest_error }
            return @{ installed_present = $state.manifest.installed_present; dev_present = $state.manifest.dev_present
                      aside_present = $state.manifest.aside_present; dev_dll = $null
                      installed_assembly = $installedAssembly; installed_manifest_error = $err
                      expected_installed_assembly = $expected } }.GetNewClosure()
        InstalledDllState = { param($Year)
            return @{ present = $state.dllPresent; sha256 = $(if ($state.dllError) { $null } else { $state.sha })
                      error = $state.dllError; path = "C:\nowhere\Addins\$Year\Horizun\Horizun.Revit.dll" } }.GetNewClosure()
        # The driver's allow-list, canned: only "External Tool*" is ever dismissed.
        DismissStartupDialog = { param($Title) $state.dismissCalls += $Title; if ($Title -like 'External Tool*') { return @($Title) } else { return @() } }.GetNewClosure()
    }
    return @{ probes = $probes; state = $state }
}
function Manifest([bool]$Installed = $true, [bool]$Dev = $false, [bool]$Aside = $false) { @{ installed_present = $Installed; dev_present = $Dev; aside_present = $Aside } }
function RevitProc([int]$Id, [string]$Exe, [string]$ExeError = $null, [bool]$Exited = $false, [bool]$ExitReadable = $true) {
    [pscustomobject]@{ Id = $Id; Exe = $Exe; ExeError = $ExeError; HasExited = $Exited; ExitStateReadable = $ExitReadable; StartTime = (Get-Date) }
}
# The snapshot a restore is judged against. Built from the same probes, so the
# tests cannot drift from what the driver captures.
function StartState($Probes, [string]$Year = '2024') { Get-HzYearStateAtStart -Probes $Probes -Year $Year }
function Ledger { New-HzRehearsalLedger -AnchorDir $anchorDir }
# $Pid is a read-only automatic variable in PowerShell: naming the parameter
# that way makes every call fail with 'Cannot overwrite variable Pid'.
function BindLedger($L, $OwnerPid) { Register-HzRehearsalSession -Ledger $L -Identity ([ordered]@{ pid = $OwnerPid }) }

Write-Host "== year-matrix session safety ==" -ForegroundColor Cyan
try {
    # -------------------------------------------------------------------------
    # 1. Enable: reported, never thrown; and never on an unproven machine state.
    # -------------------------------------------------------------------------
    $t = New-TestProbes -Manifest (Manifest) -EnableCode 1
    $r = Invoke-HzYearEnable -Probes $t.probes -Year '2024'
    Check 'enable failure is reported, not thrown' ((-not $r.ok) -and ($r.why -match 'exited 1'))
    $t2 = New-TestProbes -Manifest (Manifest) -EnableCode 0
    $t2.probes.Enable = { param($Year) return 0 }   # exit 0 but nothing written
    $r2 = Invoke-HzYearEnable -Probes $t2.probes -Year '2024'
    Check 'enable that writes no manifest is a failure even with exit 0' ((-not $r2.ok) -and ($r2.why -match 'no development manifest'))

    # 0b. The clean-tree flag is a count of status lines, measured on a throwaway
    #     repository: clean -> 0 lines; one untracked file -> exactly that line.
    $tmpRepo = Join-Path ([IO.Path]::GetTempPath()) ('hz-matrix-tests-' + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $tmpRepo | Out-Null
    try {
        & git -C $tmpRepo init -q 2>$null | Out-Null
        & git -C $tmpRepo -c user.email=t@t -c user.name=t commit -q --allow-empty -m init 2>$null | Out-Null
        $clean = Get-HzRepoStatus -Repo $tmpRepo
        Check 'a clean repository has zero status lines (the old [string] -eq "" test said dirty here)' ($clean.Count -eq 0)
        Set-Content -LiteralPath (Join-Path $tmpRepo 'stray.txt') -Value 'x'
        $dirty = Get-HzRepoStatus -Repo $tmpRepo
        Check 'an untracked file is one status line naming it' (($dirty.Count -eq 1) -and ($dirty[0] -match '^\?\? stray\.txt'))
    } finally { Remove-Item -LiteralPath $tmpRepo -Recurse -Force -ErrorAction SilentlyContinue }

    $driver = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'run-year-matrix.ps1') -Raw
    $guard = $driver.IndexOf('if (-not $enable.ok)'); $start = $driver.IndexOf('Start-Process -FilePath $exe')
    Check 'the driver never starts Revit after a failed enable' (($guard -ge 0) -and ($start -gt $guard))
    Check 'the driver contains no forced termination' ($driver -notmatch 'Stop-Process')
    Check 'the driver does not delete other instances'' discovery files' ($driver -notmatch 'Filter "revit-\$year-\*\.json"')

    # 1b. The real probes carry their paths: a wrong repo fails by exit code, not by a null path.
    $realProbes = New-HzMatrixProbes -Repo 'C:\nowhere-hz-tests' -ServerExe 'C:\nowhere-hz-tests\horizun-mcp.exe'
    $enableReal = Invoke-HzYearEnable -Probes $realProbes -Year '2099'
    Check 'the real Enable probe resolves its script path (fails by exit code, not by a null path)' ((-not $enableReal.ok) -and ($enableReal.why -notmatch 'null') -and ($enableReal.why -match 'exited|threw'))
    Check 'the driver takes its clean-tree flag from the counted status lines and records them' (($driver -match 'Get-HzRepoStatus') -and ($driver -match 'repo_status = \$repoStatus') -and ($driver -notmatch '\[string\]\(& git -C \$repo status --porcelain\)'))
    Check 'the driver formats its server-stamp warning as one message (the -f applies to both halves)' ($driver -match '\(\("=== THE SERVER ON DISK IS STAMPED[^\r\n]*" \+\s*\r?\n\s*"[^\r\n]*"\)\s+-f')
    Check 'the driver retries a refused fixture open once after dismissing an allow-listed modal, and keeps state_before' (($driver -match 'Get-HzModalDialogTitle -Text \$refusal') -and ($driver -match 'DismissStartupDialog = \{') -and ($driver -match 'Test-HzDialogTitleAllowed -Title \$Title -Allowed \$script:DismissStartupDialog') -and ($driver -match '\$row\.state_before = \$row\.state'))
    Check 'the driver records each year exactly once' (([regex]::Matches($driver, [regex]::Escape('$summary.years += $row'))).Count -eq 1)
    $rawCode = & $realProbes.Enable '2099'
    Check 'the real Enable probe returns one integer, never the child process lines' ($rawCode -is [int])
    $rawRestore = & $realProbes.Restore '2099'
    Check 'the real Restore probe returns one integer, never the child process lines' ($rawRestore -is [int])
    Check 'an array ending in 0 is read as exit code 0, and lines without a code as none' ((ConvertTo-HzExitCode @('line', 'line', 0)) -eq 0 -and ($null -eq (ConvertTo-HzExitCode @('line'))) -and ((ConvertTo-HzExitCode 3) -eq 3))

    # 1c. THE DRIVER'S OWN WIRING for the corrections below - grep-level, because a
    #     module that refuses safely is no protection if the driver stops calling it.
    Check 'the driver grants no ownership by title pattern any more' (($driver -notmatch 'OwnTitlePattern') -and ($driver -notmatch "\@\('\^HZ"))
    Check 'the driver registers what it opened through the bridge' ($driver -match 'Register-HzOpenedDocument')
    Check 'the driver binds the register to the session it started' ($driver -match 'Register-HzRehearsalSession -Ledger \$ledger -Identity \$identity')
    Check 'the driver blocks a year on an unidentifiable Revit, not only on its own year' ($driver -match 'Test-HzManifestChangeAllowed -Probes \$probes -Year \$year')
    Check 'the driver captures a start snapshot and restores against it' (($driver -match 'Get-HzYearStateAtStart') -and ($driver -match 'Restore-HzYearSession -Probes \$probes -Year \$year -StateAtStart \$stateAtStart'))
    Check 'an exception inside the close still records the failure and still evaluates the restore' ($driver -match "left_running_error")
    Check 'the driver no longer classifies Revit processes by a swallowed MainModule read of its own' ($driver -notmatch 'function Get-YearRevit')

    # -------------------------------------------------------------------------
    # 2. CORRECTION C - a Revit of an unknown year is not "no Revit".
    # -------------------------------------------------------------------------
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4101 'C:\Program Files\Autodesk\Revit 2024\Revit.exe')
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    Check 'a Revit of the target year is classified as the target year' (($c.target.Count -eq 1) -and ($c.target[0].year -eq '2024') -and ($c.unknown.Count -eq 0))
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4102 'C:\Program Files\Autodesk\Revit 2027\Revit.exe')
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    Check 'a Revit of another clearly identified year is not the target year' (($c.target.Count -eq 0) -and ($c.other_year.Count -eq 1) -and ($c.other_year[0].year -eq '2027'))
    Check 'a manifest change for the target year is allowed with only another year running' ((Test-HzManifestChangeAllowed -Probes $t.probes -Year '2024').ok)
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4103 $null 'Access is denied.')
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    $a = Test-HzManifestChangeAllowed -Probes $t.probes -Year '2024'
    Check 'a Revit whose MainModule is inaccessible is UNKNOWN, never dropped' (($c.unknown.Count -eq 1) -and ($c.unknown[0].pid -eq 4103) -and ($c.unknown[0].why -match 'Access is denied'))
    Check 'an unknown-year Revit blocks the manifest change, with its pid and the reason' ((-not $a.ok) -and ($a.blocked_by -eq 'unknown_year') -and ($a.pids -contains 4103) -and ($a.why -match 'pid 4103'))
    Check 'the refusal neither kills the process nor escalates' ($a.why -match 'not touched and no permission is escalated')
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4104 '')
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    Check 'an empty executable path is UNKNOWN' (($c.unknown.Count -eq 1) -and ($c.unknown[0].why -match 'came back empty'))
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4105 'C:\tmp\Revit.exe')
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    Check 'an executable path that names no year is UNKNOWN' (($c.unknown.Count -eq 1) -and ($c.unknown[0].why -match 'does not name a Revit year'))
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4106 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' $null $true $true)
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    Check 'a process that disappeared during the check is not counted as running' (($c.target.Count -eq 0) -and ($c.unknown.Count -eq 0) -and ($c.other_year.Count -eq 0))
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4107 $null 'exited' $false $false)
    $c = Get-HzRevitProcessClasses -Probes $t.probes -Year '2024'
    Check 'a process whose exit state cannot be read counts as running and unknown' ($c.unknown.Count -eq 1)
    # The identity of the machine CHANGES between an early check and the change
    # itself: the second read is the one that decides.
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -RevitProcesses @()
    $t.state.revitLater = @(RevitProc 4108 'C:\Program Files\Autodesk\Revit 2024\Revit.exe')
    $first = Test-HzManifestChangeAllowed -Probes $t.probes -Year '2024'
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'a Revit that appears after an earlier all-clear still defers the restore' ($first.ok -and (-not $r.ok) -and ($r.state -eq 'deferred_revit_running') -and ($t.state.restoreCalls -eq 0))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -RevitProcesses @(RevitProc 4109 $null 'Access is denied.')
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'an unknown-year Revit defers the restore by its own state, nothing renamed' ((-not $r.ok) -and ($r.state -eq 'deferred_revit_unknown_year') -and ($t.state.restoreCalls -eq 0) -and ($r.blocking_pids -contains 4109))
    $t = New-TestProbes -Manifest (Manifest) -RevitProcesses @(RevitProc 4110 $null 'Access is denied.')
    $e = Invoke-HzYearEnable -Probes $t.probes -Year '2024'
    Check 'enable refuses under an unknown-year Revit and never runs the helper' ((-not $e.ok) -and ($e.blocked_by -eq 'unknown_year') -and ($t.state.enableCalls -eq 0))

    # -------------------------------------------------------------------------
    # 3. CORRECTION B - a verified restore, with no false positives.
    # -------------------------------------------------------------------------
    # The exact reproduction from the report: nothing to undo, no installed
    # manifest, no readable hash - and the old code answered ok/nothing_to_restore.
    $t = New-TestProbes -Manifest (Manifest -Installed $true)
    $startState = StartState $t.probes
    $t.state.manifest.installed_present = $false
    $t.state.dllPresent = $false
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'the reported false positive is gone: an installation that vanished is not "nothing to restore"' ((-not $r.ok) -and ($r.state -eq 'installed_manifest_missing'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true)
    $startState = StartState $t.probes
    $t.probes.Restore = { param($Year) $t.state.manifest.dev_present = $false; $t.state.manifest.aside_present = $false; $t.state.manifest.installed_present = $true; $t.state.dllPresent = $false; return 0 }.GetNewClosure()
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'an expected DLL that is absent after the restore is a failure' ((-not $r.ok) -and ($r.state -eq 'installed_dll_missing'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true)
    $startState = StartState $t.probes
    $t.probes.Restore = { param($Year) $t.state.manifest.dev_present = $false; $t.state.manifest.aside_present = $false; $t.state.manifest.installed_present = $false; return 0 }.GetNewClosure()
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'an expected manifest that is absent after the restore is a failure' ((-not $r.ok) -and ($r.state -eq 'installed_manifest_missing'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true)
    $startState = StartState $t.probes
    $t.state.dllError = 'The process cannot access the file (simulated)'
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'a hash that cannot be read afterwards is unverifiable, never correct' ((-not $r.ok) -and ($r.state -eq 'restore_unverifiable') -and ($r.why -match 'could not be hashed now'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -InstalledDllError 'locked (simulated)'
    $startState = StartState $t.probes
    Check 'a start snapshot with an unhashable DLL is marked unreadable' ((-not $startState.readable) -and ($startState.unreadable_because -match 'not hashable'))
    $t.state.dllError = $null
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'a restore judged against an unhashable baseline is unverifiable' ((-not $r.ok) -and ($r.state -eq 'restore_unverifiable'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -InstalledSha 'bbbb'
    $startState = StartState $t.probes
    $t.state.sha = 'cccc'
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'a changed installed DLL is reported after the restore' ((-not $r.ok) -and ($r.state -eq 'installed_dll_changed') -and ($r.why -match 'cccc'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true)
    $startState = StartState $t.probes
    $t.probes.Restore = { param($Year) $t.state.manifest.dev_present = $false; $t.state.manifest.aside_present = $false; $t.state.manifest.installed_present = $true
                          $t.state.manifest.installed_assembly = (Get-HzNormalizedPath 'D:\dev-store\dev-addin\2024\Horizun\Horizun.Revit.dll'); return 0 }.GetNewClosure()
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'a manifest that exists but points at the development copy is not a restored installation' ((-not $r.ok) -and ($r.state -eq 'installed_manifest_wrong_target') -and ($r.why -match 'dev-addin'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -RestoreCode 3
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'restore failure carries the exit code' ((-not $r.ok) -and ($r.state -eq 'restore_failed') -and ($r.why -match 'exited 3'))
    $file = Write-HzRecoveryPending -Dir $tmp -Year '2024' -Record @{ restore = $r }
    $body = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    Check 'recovery pending is written with the steps to recover' ((Test-Path $file) -and ($body.how_to_recover.Count -ge 3) -and ($body.record.restore.state -eq 'restore_failed'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -RestoreCode 0
    $t.probes.Restore = { param($Year) return 0 }   # says fine, changes nothing on disk
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'restore is not believed when the disk still shows the dev manifest' ((-not $r.ok) -and ($r.state -eq 'restore_incomplete'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true) -RevitProcesses @(RevitProc 4201 'C:\Program Files\Autodesk\Revit 2024\Revit.exe')
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'no manifest is swapped while a Revit of that year runs' ((-not $r.ok) -and ($r.state -eq 'deferred_revit_running') -and ($t.state.restoreCalls -eq 0))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true)
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'a verified restore reports restored' ($r.ok -and ($r.state -eq 'restored'))
    $r2 = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart (StartState $t.probes)
    Check 'a second restore is idempotent: nothing to undo and the installation still matches' ($r2.ok -and ($r2.state -eq 'nothing_to_restore') -and ($t.state.restoreCalls -eq 1))
    # An installation that legitimately was not there: the return to that absence
    # is validated explicitly, and an installation appearing is a finding.
    $t = New-TestProbes -Manifest (Manifest -Installed $false) -InstalledDllPresent $false
    $startState = StartState $t.probes
    Check 'no installation at the start is recorded as such' (-not $startState.installation_expected_at_end)
    $t.state.manifest.dev_present = $true
    $t.probes.Restore = { param($Year) $t.state.manifest.dev_present = $false; return 0 }.GetNewClosure()
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'a year that had no installation is restored to having none' ($r.ok -and ($r.state -eq 'restored'))
    $t.state.manifest.installed_present = $true
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart $startState
    Check 'an installation that appeared where there was none is a finding, not a green' ((-not $r.ok) -and ($r.state -eq 'installed_appeared'))
    $t = New-TestProbes -Manifest (Manifest -Dev $true -Aside $true)
    $r = Restore-HzYearSession -Probes $t.probes -Year '2024' -StateAtStart @{ nothing = $true }
    Check 'a restore with no start snapshot is unverifiable, not ok' ((-not $r.ok) -and ($r.state -eq 'restore_unverifiable'))
    # An aside left by an earlier session still means "this year HAS an installation".
    $t = New-TestProbes -Manifest (Manifest -Installed $false -Aside $true)
    $startState = StartState $t.probes
    Check 'an aside copy at the start counts as an installation to put back' ($startState.installation_expected_at_end)

    # 3b. The REAL manifest reader, against a temp %APPDATA%: a relative Assembly
    #     resolves next to the manifest, and a manifest pointing elsewhere is seen.
    $fakeAppData = Join-Path $tmp 'appdata'
    $fakeAddins = Join-Path $fakeAppData 'Autodesk\Revit\Addins\2024'
    New-Item -ItemType Directory -Force -Path (Join-Path $fakeAddins 'Horizun') | Out-Null
    Set-Content -LiteralPath (Join-Path $fakeAddins 'Horizun\Horizun.Revit.dll') -Value 'not a real dll'
    $manifestXml = @'
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Horizun MCP</Name>
    <Assembly>Horizun\Horizun.Revit.dll</Assembly>
    <AddInId>b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30</AddInId>
    <FullClassName>Horizun.Revit.App</FullClassName>
  </AddIn>
</RevitAddIns>
'@
    Set-Content -LiteralPath (Join-Path $fakeAddins 'Horizun.addin') -Value $manifestXml -Encoding utf8
    $savedAppData = $env:APPDATA
    try {
        $env:APPDATA = $fakeAppData
        $realState = Get-HzYearStateAtStart -Probes $realProbes -Year '2024'
        Check 'the real reader resolves a relative <Assembly> next to its manifest' ($realState.manifest.installed_assembly -eq $realState.manifest.expected_installed_assembly)
        Check 'the real reader hashes the installed DLL and says so' ($realState.installed_dll.present -and $realState.installed_dll.sha256 -and $realState.readable)
        Set-Content -LiteralPath (Join-Path $fakeAddins 'Horizun.addin') -Value ($manifestXml -replace 'Horizun\\Horizun\.Revit\.dll', 'C:\elsewhere\Horizun.Revit.dll') -Encoding utf8
        $wrong = Get-HzYearStateAtStart -Probes $realProbes -Year '2024'
        Check 'the real reader reports a manifest pointing somewhere else' ($wrong.manifest.installed_assembly -ne $wrong.manifest.expected_installed_assembly)
        Set-Content -LiteralPath (Join-Path $fakeAddins 'Horizun.addin') -Value '<RevitAddIns><AddIn>' -Encoding utf8
        $broken = Get-HzYearStateAtStart -Probes $realProbes -Year '2024'
        Check 'an unreadable manifest is an error, not an absent manifest' ($broken.manifest.installed_present -and $broken.manifest.installed_manifest_error -and (-not $broken.readable))
    }
    finally { $env:APPDATA = $savedAppData }

    # 3c. THE HELPER, run for real: it validates the conflict BEFORE deleting the
    #     development manifest, and it reports a partial restore exactly.
    $helperYear = Get-FreeRevitYear
    $helperAppData = Join-Path $tmp 'appdata-helper'
    $helperAddins = Join-Path $helperAppData ('Autodesk\Revit\Addins\' + $(if ($helperYear) { $helperYear } else { '2026' }))
    New-Item -ItemType Directory -Force -Path $helperAddins | Out-Null
    $devSession = Join-Path $PSScriptRoot 'dev-addin-session.ps1'
    Set-Content -LiteralPath (Join-Path $helperAddins 'Horizun.addin') -Value $manifestXml -Encoding utf8
    Set-Content -LiteralPath (Join-Path $helperAddins 'Horizun.addin.dev-session-aside') -Value $manifestXml -Encoding utf8
    Set-Content -LiteralPath (Join-Path $helperAddins 'Horizun-dev-session.addin') -Value $manifestXml -Encoding utf8
    $savedAppData = $env:APPDATA
    try {
        if (-not $helperYear) { throw "SKIP: every installed Revit year has a running or unidentifiable Revit, so the helper cannot be exercised without going near somebody's session." }
        $env:APPDATA = $helperAppData
        $out = & pwsh -NoProfile -File $devSession -Year $helperYear -Restore 2>&1
        $code = $LASTEXITCODE
        Check 'the helper refuses a manifest/aside conflict WITHOUT deleting the development manifest first' (
            ($code -eq 3) -and (Test-Path -LiteralPath (Join-Path $helperAddins 'Horizun-dev-session.addin')) -and
            (Test-Path -LiteralPath (Join-Path $helperAddins 'Horizun.addin')) -and
            (Test-Path -LiteralPath (Join-Path $helperAddins 'Horizun.addin.dev-session-aside')) -and
            (($out -join ' ') -match 'NOTHING WAS CHANGED')) ("exit $code : " + ($out -join ' '))
        # Now the ordinary conflict-free restore, with the aside locked open so the
        # rename fails: the removal is reported as done and the rename as pending.
        Remove-Item -LiteralPath (Join-Path $helperAddins 'Horizun.addin') -Force
        $lock = [IO.File]::Open((Join-Path $helperAddins 'Horizun.addin.dev-session-aside'), 'Open', 'Read', 'None')
        try {
            $out = & pwsh -NoProfile -File $devSession -Year $helperYear -Restore 2>&1
            $code = $LASTEXITCODE
        } finally { $lock.Dispose() }
        Check 'a partial restore says exactly what changed and what is pending, and exits non-zero' (
            ($code -eq 4) -and (($out -join ' ') -match 'PENDING') -and (($out -join ' ') -match 'PARTIAL RESTORE') -and
            (($out -join ' ') -match 'removed')) ("exit $code : " + ($out -join ' '))
        # And the same call again, unlocked, completes it.
        $out = & pwsh -NoProfile -File $devSession -Year $helperYear -Restore 2>&1
        $code = $LASTEXITCODE
        Check 'the completing restore renames the aside back and exits 0' (
            ($code -eq 0) -and (Test-Path -LiteralPath (Join-Path $helperAddins 'Horizun.addin')) -and
            (-not (Test-Path -LiteralPath (Join-Path $helperAddins 'Horizun.addin.dev-session-aside')))) ("exit $code : " + ($out -join ' '))
        $out = & pwsh -NoProfile -File $devSession -Year $helperYear -Restore 2>&1
        Check 'a restore with nothing to undo says so and exits 0' (($LASTEXITCODE -eq 0) -and (($out -join ' ') -match 'nothing to restore'))
    }
    catch {
        if ($_.Exception.Message -like 'SKIP:*') {
            foreach ($case in 'the helper refuses a manifest/aside conflict without deleting the development manifest first',
                              'a partial restore says exactly what changed and what is pending',
                              'the completing restore renames the aside back',
                              'a restore with nothing to undo says so') { Skip $case $_.Exception.Message }
        }
        else { throw }
    }
    finally { $env:APPDATA = $savedAppData }

    # -------------------------------------------------------------------------
    # 4. Startup-dialog allow-list never reaches a save/discard/security prompt.
    # -------------------------------------------------------------------------
    Check 'a save prompt is never closed by title' (-not (Test-HzDialogTitleAllowed -Title 'Save File' -Allowed @('*')))
    Check 'a security prompt is never closed by title' (-not (Test-HzDialogTitleAllowed -Title 'Security - Unsigned Add-In' -Allowed @('*', 'Security*')))
    Check 'the named foreign startup failure may be closed' (Test-HzDialogTitleAllowed -Title 'External Tools - External Tool Failure' -Allowed @('External Tool*'))

    # -------------------------------------------------------------------------
    # 5. Identity of the session.
    # -------------------------------------------------------------------------
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true)
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    Check 'identity records pid, start time and executable' (($id.pid -eq $h.Id) -and $id.start_time -and $id.exe)
    $h.Kill(); $h.WaitForExit(5000) | Out-Null
    $L = Ledger; BindLedger $L $id.pid
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a process that already exited is reported as such' ($c.state -eq 'already_exited')
    Stop-Helpers

    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @()
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $noExe = [ordered]@{ pid = $id.pid; start_time = $id.start_time; exe = $null; exe_expected = $id.exe }
    Check 'an unread executable falls back to the expected one' ((Get-HzSessionIdentityState -Probes $t.probes -Identity $noExe).state -eq 'alive')
    $none = [ordered]@{ pid = $id.pid; start_time = $id.start_time; exe = $null; exe_expected = $null }
    Check 'no executable at all is unknown, never alive' ((Get-HzSessionIdentityState -Probes $t.probes -Identity $none).state -eq 'unknown')
    Check 'the identity records the expected executable beside the read one' ($id.Contains('exe_expected'))
    # AN IDENTITY THAT HAS BEEN THROUGH JSON. ConvertFrom-Json turns the round-trip
    # timestamp back into a DateTime, and [string] on that gives the local culture's
    # spelling with no offset - which used to read as a mismatch and strand the very
    # session a recovery was trying to close (measured 2026-09-09).
    $roundTripped = ($id | ConvertTo-Json -Depth 5) | ConvertFrom-Json
    $viaJson = [ordered]@{ pid = [int]$roundTripped.pid; start_time = $roundTripped.start_time
                           exe = [string]$roundTripped.exe; exe_expected = [string]$roundTripped.exe_expected }
    Check 'an identity round-tripped through JSON still matches its own process' (
        (Get-HzSessionIdentityState -Probes $t.probes -Identity $viaJson).state -eq 'alive') `
        ("start_time came back as " + $viaJson.start_time.GetType().Name)
    $localSpelling = [ordered]@{ pid = $id.pid; start_time = ([datetime]::Parse($id.start_time)).ToString('g'); exe = $id.exe }
    $ls = Get-HzSessionIdentityState -Probes $t.probes -Identity $localSpelling
    Check 'a start time that is not a round-trip timestamp is unknown, never a mismatch' (
        ($ls.state -eq 'unknown') -and ($ls.why -match 'round-trip')) ("state=" + $ls.state)
    Stop-Helpers

    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true))
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $shifted = [ordered]@{ pid = $id.pid; start_time = ([DateTimeOffset]::Parse($id.start_time).AddHours(-1)).ToString('o'); exe = $id.exe }
    $L = Ledger; BindLedger $L $id.pid
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $shifted -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a different start time means "not ours": left running' (($c.state -eq 'left_running_identity') -and (-not $h.HasExited) -and ($t.state.closeCalls.Count -eq 0))
    $other = [ordered]@{ pid = $id.pid; start_time = $id.start_time; exe = 'C:\Program Files\Autodesk\Revit 2024\Revit.exe' }
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $other -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a different executable means "not ours": left running' (($c.state -eq 'left_running_identity') -and (-not $h.HasExited))
    # A register bound to another session, or to none, closes nothing.
    $L2 = Ledger; BindLedger $L2 ($id.pid + 1)
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L2 -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a register belonging to another pid closes nothing' (($c.state -eq 'left_running_identity') -and ($c.why -match 'register belongs to pid') -and (-not $h.HasExited))
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger (Ledger) -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'an unbound register closes nothing' (($c.state -eq 'left_running_identity') -and ($c.why -match 'never bound') -and (-not $h.HasExited))
    Stop-Helpers

    # -------------------------------------------------------------------------
    # 6. CORRECTION A - ownership is the registered path, never the title.
    # -------------------------------------------------------------------------
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @(
        (Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true), (Doc 'HZ_PROYECTO_USUARIO' 'C:\obra\HZ_PROYECTO_USUARIO.rvt'))
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a user document called HZ_PROYECTO_USUARIO is foreign despite the HZ_ prefix' (
        ($c.state -eq 'left_running_foreign_document') -and ($c.left_open -contains 'HZ_PROYECTO_USUARIO') -and
        ($t.state.closeCalls.Count -eq 0) -and (-not $h.HasExited))
    $t.state.docs = @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true), (Doc 'HZ_ANCHOR_usuario' 'C:\obra\HZ_ANCHOR_usuario.rvt'))
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a user document called HZ_ANCHOR_usuario is foreign: the anchor is a path, not a name' (
        ($c.state -eq 'left_running_foreign_document') -and ($c.left_open -contains 'HZ_ANCHOR_usuario') -and ($t.state.closeCalls.Count -eq 0))
    $t.state.docs = @((Doc 'HZ24_BASE' 'D:\otra-carpeta\HZ24_BASE.rvt' $true))
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'the same title at another path is foreign' (
        ($c.state -eq 'left_running_foreign_document') -and ($t.state.closeCalls.Count -eq 0) -and
        ($c.foreign[0].why -match 'same name, another file'))
    $t.state.docs = @((Doc 'HZ24_BASE' $null $true))
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a document with no path at all is foreign unless it was registered as pathless' (
        ($c.state -eq 'left_running_foreign_document') -and ($t.state.closeCalls.Count -eq 0))
    $t.state.docs = @((Doc '' $null $true))
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a document with neither title nor path is foreign' (($c.state -eq 'left_running_foreign_document') -and ($t.state.closeCalls.Count -eq 0))
    Stop-Helpers

    # The register itself refuses a bare title, and the ownership test agrees.
    $L = Ledger
    Check 'a document with no path and no pathless declaration is NOT registered' (
        (-not (Add-HzRehearsalDocument -Ledger $L -Title 'HZ_SOMETHING')) -and (@($L.documents).Count -eq 0) -and
        (@($L.registration_failures).Count -eq 1))
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ_CLOSED_L_detached' -Pathless -SourceFile 'C:\hz-live\HZ_CLOSED_L.rvt'
    $one = Test-HzOnlyRehearsalDocuments -Ledger $L -OpenDocuments @((Doc 'HZ_CLOSED_L_detached' $null $true))
    Check 'a document registered as pathless owns a pathless document of that title' ($one.ok -and (@($one.own).Count -eq 1))
    $two = Test-HzOnlyRehearsalDocuments -Ledger $L -OpenDocuments @((Doc 'HZ_CLOSED_L_detached' $null $true), (Doc 'HZ_CLOSED_L_detached' $null))
    Check 'two pathless documents with one title are ambiguous, so neither is owned' ((-not $two.ok) -and (@($two.foreign).Count -eq 2))
    $withPath = Test-HzOnlyRehearsalDocuments -Ledger $L -OpenDocuments @((Doc 'HZ_CLOSED_L_detached' 'C:\hz-live\HZ_CLOSED_L.rvt' $true))
    Check 'a pathless registration does not own a document that HAS a path' (-not $withPath.ok)
    $anchorOnly = Test-HzOnlyRehearsalDocuments -Ledger $L -OpenDocuments @((Doc 'HZ_ANCHOR_2024' (Join-Path $anchorDir 'HZ_ANCHOR_2024.rvt')))
    Check "the bridge's own anchor is recognised by its directory" ($anchorOnly.ok -and (@($anchorOnly.anchors).Count -eq 1) -and (@($anchorOnly.own).Count -eq 0))
    $anchorish = Test-HzOnlyRehearsalDocuments -Ledger $L -OpenDocuments @((Doc 'HZ_ANCHOR_2024' (Join-Path $anchorDir 'sub\HZ_ANCHOR_2024.rvt')))
    Check 'a file under the anchor directory is the anchor; one merely named like it elsewhere is not' ($anchorish.ok)

    # 6b. The close is AIMED at the registered path, and the bridge's rehearsal is
    #     compared with what was intended before the token is spent.
    Check 'a rehearsal that resolves the registered path is accepted' (
        (Test-HzCloseRehearsalMatches -ExpectTitle 'HZ24_BASE' -ExpectPath 'C:\hz-live\HZ24_BASE.rvt' -ResolvedTitle 'HZ24_BASE' -ResolvedPath 'c:/hz-live/HZ24_BASE.rvt').ok)
    $mm = Test-HzCloseRehearsalMatches -ExpectTitle 'HZ24_BASE' -ExpectPath 'C:\hz-live\HZ24_BASE.rvt' -ResolvedTitle 'HZ24_BASE' -ResolvedPath 'D:\otra\HZ24_BASE.rvt'
    Check 'a rehearsal that resolves another file with the same title is refused' ((-not $mm.ok) -and ($mm.why -match 'not the document this run registered'))
    $mm2 = Test-HzCloseRehearsalMatches -ExpectTitle 'HZ_X_detached' -ExpectPath $null -ResolvedTitle 'HZ_X_detached' -ResolvedPath 'C:\hz-live\HZ_X.rvt'
    Check 'a rehearsal that resolves a document WITH a path where none was registered is refused' ((-not $mm2.ok) -and ($mm2.why -match 'WITH a path'))
    $mm3 = Test-HzCloseRehearsalMatches -ExpectTitle 'HZ_X_detached' -ExpectPath $null -ResolvedTitle 'HZ_Y_detached' -ResolvedPath $null
    Check 'a rehearsal that resolves another pathless title is refused' (-not $mm3.ok)
    $mm4 = Test-HzCloseRehearsalMatches -ExpectTitle 'HZ_X_detached' -ExpectPath $null -ResolvedTitle 'HZ_X_detached' -ResolvedPath $null
    Check 'a rehearsal that resolves the registered pathless document is accepted' ($mm4.ok)

    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @(
        (Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true), (Doc 'HZ_ANCHOR_2024' (Join-Path $anchorDir 'HZ_ANCHOR_2024.rvt')))
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 20
    Check 'a session with only registered documents closes normally' (($c.state -eq 'closed') -and $h.HasExited) ("state=$($c.state) why=$($c.why)")
    Check 'only the registered fixture went through the bridge close, aimed at its path' (
        ($t.state.closeCalls.Count -eq 1) -and ($t.state.closeCalls[0] -eq 'C:\hz-live\HZ24_BASE.rvt') -and
        ($t.state.closeRequests[0]['expect_title'] -eq 'HZ24_BASE'))
    Check 'the anchor is left to Revit, never closed' ($t.state.closeCalls -notcontains 'HZ_ANCHOR_2024')
    Check 'the record shows the checks it made before closing' (@($c.checks).Count -ge 2)
    Stop-Helpers

    # 6c. A registered document that was closed and REPLACED by another.
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true))
    $t.state.healthPid = $h.Id
    $t.state.onClose = { param($s, $req) $s.docs = @([ordered]@{ title = 'HZ24_BASE'; path = 'E:\backup\HZ24_BASE.rvt'; is_active = $true }) }
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a registered document replaced by another file of the same name leaves the session running' (
        ($c.state -eq 'left_running_foreign_document') -and (-not $h.HasExited) -and ($t.state.closeCalls.Count -eq 1))
    Stop-Helpers

    # 6d. A foreign document that APPEARS during the close sequence.
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @(
        (Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true), (Doc 'HZ24_OTRO' 'C:\hz-live\HZ24_OTRO.rvt'))
    $t.state.healthPid = $h.Id
    $t.state.onClose = { param($s, $req) $s.docs = @(@($s.docs) + [ordered]@{ title = 'Torre Norte - Arquitectura'; path = 'C:\obra\Torre.rvt'; is_active = $true }) }
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_OTRO' -Path 'C:\hz-live\HZ24_OTRO.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a foreign document that appears mid-sequence stops the close and leaves the session running' (
        ($c.state -eq 'left_running_foreign_document') -and ($t.state.closeCalls.Count -eq 1) -and (-not $h.HasExited) -and
        ($c.why -match 'DURING the close sequence'))
    Stop-Helpers

    # 6e. The bridge says closed and the document is still open.
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true))
    $t.state.healthPid = $h.Id
    $t.probes.CloseDocument = { param($Request) $t.state.closeCalls += [string]$Request['target']; return @{ ok = $true; closed = $true } }.GetNewClosure()
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a close reported done while the document is still open leaves the session running' (
        ($c.state -eq 'left_running_close_unverified') -and (-not $h.HasExited))
    Stop-Helpers

    # -------------------------------------------------------------------------
    # 7. What the bridge could not answer.
    # -------------------------------------------------------------------------
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -HealthOk $false
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'an unanswered health leaves the session running' (($c.state -eq 'left_running_health') -and (-not $h.HasExited) -and ($t.state.closeCalls.Count -eq 0))
    Stop-Helpers

    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true))
    $t.state.healthPid = $h.Id + 1
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a health reply for another pid leaves the session running' (($c.state -eq 'left_running_identity') -and (-not $h.HasExited) -and ($t.state.closeCalls.Count -eq 0))
    $t.state.healthPid = $null
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a health reply that names no pid leaves the session running' (($c.state -eq 'left_running_identity') -and (-not $h.HasExited))
    Stop-Helpers

    # 7b. THE REAL REPLY READERS, against canned reply files. An INCOMPLETE
    #     open-document list is not zero documents, and closed=true is not the
    #     absence of an error - and both judgements used to live inside a probe
    #     that shells out to a real bridge, where nothing could reach them.
    $replies = Join-Path $tmp 'replies'
    New-Item -ItemType Directory -Force -Path $replies | Out-Null
    function Reply([string]$Name, $Body) {
        $p = Join-Path $replies "$Name.json"
        ($Body | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $p -Encoding utf8
        return $p
    }
    $good = Reply 'health-good' @{ is_error = $false; result = @{ process_id = 4242; open_document_count = 1
            open_documents = @(@{ title = 'HZ_WRITE3'; path = 'C:\hz-live\HZ_WRITE3.rvt'; is_active = $true }) } }
    $r = ConvertFrom-HzHealthReply -ReplyPath $good
    Check 'a complete health reply is parsed into documents with their paths' (
        $r.ok -and ($r.process_id -eq 4242) -and (@($r.documents).Count -eq 1) -and
        (@($r.documents)[0].path -eq 'C:\hz-live\HZ_WRITE3.rvt'))
    $empty = Reply 'health-empty' @{ is_error = $false; result = @{ process_id = 4242; open_document_count = 0; open_documents = @() } }
    $r = ConvertFrom-HzHealthReply -ReplyPath $empty
    Check 'zero documents WITH the field present is a legitimate answer' ($r.ok -and (@($r.documents).Count -eq 0))
    $noField = Reply 'health-nofield' @{ is_error = $false; result = @{ process_id = 4242; status = 'healthy' } }
    $r = ConvertFrom-HzHealthReply -ReplyPath $noField
    Check 'a health reply with no open_documents field is refused, never read as zero' ((-not $r.ok) -and ($r.error -match 'no open_documents field'))
    $incomplete = Reply 'health-incomplete' @{ is_error = $false; result = @{ process_id = 4242; open_document_count = 1
            open_documents = @(@{ title = 'A'; path = 'C:\a.rvt' })
            note = 'The open-document list is INCOMPLETE: the collection threw' } }
    $r = ConvertFrom-HzHealthReply -ReplyPath $incomplete
    Check 'a list the bridge itself calls INCOMPLETE is refused' ((-not $r.ok) -and ($r.error -match 'incomplete'))
    $miscount = Reply 'health-miscount' @{ is_error = $false; result = @{ process_id = 4242; open_document_count = 3
            open_documents = @(@{ title = 'A'; path = 'C:\a.rvt' }) } }
    $r = ConvertFrom-HzHealthReply -ReplyPath $miscount
    Check 'a count that disagrees with the list is refused' ((-not $r.ok) -and ($r.error -match 'not the count'))
    $errored = Reply 'health-error' @{ is_error = $true; raw = 'Revit has a MODAL DIALOG open' }
    Check 'an errored reply carries its text out' ((-not (ConvertFrom-HzHealthReply -ReplyPath $errored).ok))
    Check 'a missing reply file is "no reply file", not an exception' ((-not (ConvertFrom-HzHealthReply -ReplyPath (Join-Path $replies 'nope.json')).ok))
    Set-Content -LiteralPath (Join-Path $replies 'health-broken.json') -Value 'not json at all'
    $r = ConvertFrom-HzHealthReply -ReplyPath (Join-Path $replies 'health-broken.json')
    Check 'a reply that is not JSON is refused by name, never treated as empty' ((-not $r.ok) -and ($r.error -match 'not JSON'))
    $dry = Reply 'close-dry' @{ is_error = $false; result = @{ confirmation_token = 'tok-1'; title = 'HZ_WRITE3'; path = 'C:\hz-live\HZ_WRITE3.rvt' } }
    $r = Read-HzCloseRehearsal -ReplyPath $dry -ExpectTitle 'HZ_WRITE3' -ExpectPath 'C:\hz-live\HZ_WRITE3.rvt'
    Check 'a rehearsal for the registered document yields its token' ($r.ok -and ($r.token -eq 'tok-1'))
    $r = Read-HzCloseRehearsal -ReplyPath $dry -ExpectTitle 'HZ_WRITE3' -ExpectPath 'D:\elsewhere\HZ_WRITE3.rvt'
    Check 'a rehearsal that resolved another file yields NO token' ((-not $r.ok) -and ($r.error -match 'not the document this run registered'))
    $noTok = Reply 'close-dry-notok' @{ is_error = $false; result = @{ title = 'HZ_WRITE3'; path = 'C:\hz-live\HZ_WRITE3.rvt' } }
    Check 'a rehearsal with no token and no verdict on discarding is a refusal' (
        (-not (Read-HzCloseRehearsal -ReplyPath $noTok -ExpectTitle 'HZ_WRITE3' -ExpectPath 'C:\hz-live\HZ_WRITE3.rvt').ok))
    # A CLEAN DOCUMENT IS ISSUED NO TOKEN AND NEEDS NONE. Demanding one made an
    # unmodified fixture impossible to close (measured 2026-09-09 on a real
    # session whose harness had never run).
    $clean = Reply 'close-dry-clean' @{ is_error = $false; result = @{ title = 'HZ_WRITE3'; path = 'C:\hz-live\HZ_WRITE3.rvt'
            is_modified = $false; would_discard_unsaved = $false
            note = 'NOTHING WAS CLOSED. This close would discard nothing, so it needs no token: call again with dry_run=false.' } }
    $r = Read-HzCloseRehearsal -ReplyPath $clean -ExpectTitle 'HZ_WRITE3' -ExpectPath 'C:\hz-live\HZ_WRITE3.rvt'
    Check 'a rehearsal that would discard nothing proceeds without a token' ($r.ok -and (-not $r.token) -and ($r.needs_token -eq $false))
    $r = Read-HzCloseRehearsal -ReplyPath $clean -ExpectTitle 'HZ_WRITE3' -ExpectPath 'D:\elsewhere\HZ_WRITE3.rvt'
    Check 'and the aim is still checked when no token is needed' ((-not $r.ok) -and ($r.error -match 'not the document this run registered'))
    $dirty = Reply 'close-dry-dirty' @{ is_error = $false; result = @{ title = 'HZ_WRITE3'; path = 'C:\hz-live\HZ_WRITE3.rvt'
            is_modified = $true; would_discard_unsaved = $true } }
    $r = Read-HzCloseRehearsal -ReplyPath $dirty -ExpectTitle 'HZ_WRITE3' -ExpectPath 'C:\hz-live\HZ_WRITE3.rvt'
    Check 'a rehearsal that WOULD discard work and issued no token is refused' ((-not $r.ok) -and ($r.error -match 'issued no confirmation token'))
    Check 'the driver''s close sends a token only when one was issued' (
        ((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'year-matrix.session.ps1') -Raw) -match "if \(\`$rehearsal\.token\) \{ \`$applyArgs\['confirmation_token'\]"))
    $applied = Reply 'close-apply' @{ is_error = $false; result = @{ closed = $true; closed_evidence = 'no longer in Application.Documents' } }
    Check 'a close that reports closed=true with evidence is accepted' ((Read-HzCloseApply -ReplyPath $applied).ok)
    $silent = Reply 'close-apply-silent' @{ is_error = $false; result = @{ operation = 'close'; note = 'fine' } }
    $r = Read-HzCloseApply -ReplyPath $silent
    Check 'a close reply with no closed flag is unverified, not success' ((-not $r.ok) -and ($r.error -match 'does not report closed=true'))
    $refused = Reply 'close-apply-false' @{ is_error = $false; result = @{ closed = $false } }
    Check 'closed=false is not a close' ((-not (Read-HzCloseApply -ReplyPath $refused).ok))

    # 7c. AND THE PROBES THAT CALL THEM MUST BE ABLE TO. GetNewClosure() rebinds a
    #     scriptblock to a dynamic module that falls back to the GLOBAL scope, so a
    #     probe calling one of this file's functions works under `pwsh -File` and
    #     dies under `pwsh -Command "& script.ps1"` - which is the form the
    #     documented matrix command uses. It cost a real Revit session on
    #     2026-09-09. Two guards: the source, and both invocation forms.
    $moduleAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'year-matrix.session.ps1'), [ref]$null, [ref]$null)
    $moduleFunctions = @($moduleAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
    $closureOffenders = @()
    foreach ($call in @($moduleAst.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                $n.Member.Extent.Text -eq 'GetNewClosure' }, $true))) {
        $body = $call.Expression
        if ($body -isnot [System.Management.Automation.Language.ScriptBlockExpressionAst]) { continue }
        foreach ($cmd in @($body.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))) {
            $name = $cmd.GetCommandName()
            if ($name -and ($moduleFunctions -contains $name)) { $closureOffenders += $name }
        }
    }
    Check 'no probe closure calls one of this file''s functions by name' ($closureOffenders.Count -eq 0) ("offenders: " + ($closureOffenders -join ', '))
    $probeScript = Join-Path $tmp 'closure-probe.ps1'
    @'
. (Join-Path $env:HZ_TESTS_DIR 'year-matrix.session.ps1')
$p = New-HzMatrixProbes -Repo $env:HZ_TESTS_REPO -ServerExe 'C:\nowhere-hz-tests\horizun-mcp.exe'
$dir = $env:HZ_TESTS_TMP
$h = & $p.Health '2099' $dir
"health-ok=$($h.ok) error=$($h.error)"
$c = & $p.CloseDocument ([ordered]@{ year = '2099'; dir = $dir; target = 'C:\nowhere\x.rvt'; expect_title = 'x'; expect_path = 'C:\nowhere\x.rvt' })
"close-ok=$($c.ok) error=$($c.error)"
'@ | Set-Content -LiteralPath $probeScript -Encoding utf8
    $env:HZ_TESTS_DIR = $PSScriptRoot
    $env:HZ_TESTS_TMP = $tmp
    $env:HZ_TESTS_REPO = $script:RepoRoot
    foreach ($form in @('File', 'Command')) {
        $out = if ($form -eq 'File') { & pwsh -NoProfile -File $probeScript 2>&1 } else { & pwsh -NoProfile -Command "& '$probeScript'" 2>&1 }
        $text = ($out | Out-String)
        Check ("the real health and close probes run under pwsh -$form and answer instead of throwing") (
            ($text -match 'health-ok=False') -and ($text -match 'close-ok=False') -and
            ($text -notmatch 'is not recognized as a name of a cmdlet')) ($text -replace '\s+', ' ').Trim()
    }

    # 8. A LATE startup modal in the close path (measured on Revit 2023,
    #    2026-09-09): health is refused naming the dialog; an allow-listed title
    #    is dismissed through the probe and health is asked once more; a save
    #    dialog is never dismissed; probes without the hook behave as before.
    $modalText = "Error: Revit has a MODAL DIALOG open: 'External Tools - External Tool Failure' [#32770]. 'horizun_health' was queued but Revit does not service the bridge until the dialog is answered by a human"
    Check 'the modal title is read from the bridge refusal' ((Get-HzModalDialogTitle -Text $modalText) -eq 'External Tools - External Tool Failure')
    Check 'no modal title is read from other text' ($null -eq (Get-HzModalDialogTitle -Text 'no reply file'))
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true)) -HealthErrorOnce $modalText
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 20
    Check 'an allow-listed late modal is dismissed once and the close goes on' (
        ($c.state -eq 'closed') -and (@($c.dismissed_dialogs) -contains 'External Tools - External Tool Failure') -and
        ($t.state.dismissCalls.Count -eq 1)) ("state=$($c.state) why=$($c.why)")
    Stop-Helpers
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true)) -HealthErrorOnce "Error: Revit has a MODAL DIALOG open: 'Save File' [#32770]."
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a save dialog is never dismissed: the session is left running, nothing closed' (($c.state -eq 'left_running_health') -and ($t.state.closeCalls.Count -eq 0) -and (-not $h.HasExited))
    Stop-Helpers
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true)) -HealthErrorOnce $modalText
    $t.probes.Remove('DismissStartupDialog')
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'without a dismiss hook a refused health still leaves the session running' (($c.state -eq 'left_running_health') -and (-not $h.HasExited))
    Stop-Helpers

    # 9. A close refusal from the bridge stops everything.
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true))
    $t.state.healthPid = $h.Id
    $t.probes.CloseDocument = { param($Request) return @{ ok = $false; error = 'REFUSING TO CLOSE (simulated)' } }
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $null = Add-HzRehearsalDocument -Ledger $L -Title 'HZ24_BASE' -Path 'C:\hz-live\HZ24_BASE.rvt'
    $c = Close-HzRehearsalSession -Probes $t.probes -Identity $id -Ledger $L -Year '2024' -Dir $tmp -ExitTimeoutSec 5
    Check 'a refused document close leaves the session running' (($c.state -eq 'left_running_close_refused') -and (-not $h.HasExited))
    Stop-Helpers

    # 10. Registration goes through the bridge, after the open.
    $h = Start-Helper
    $t = New-TestProbes -Manifest (Manifest -Dev $true) -Docs @((Doc 'HZ24_BASE' 'C:\hz-live\HZ24_BASE.rvt' $true))
    $t.state.healthPid = $h.Id
    $id = New-HzSessionIdentity -Probes $t.probes -ProcessId $h.Id
    $L = Ledger; BindLedger $L $id.pid
    $reg = Register-HzOpenedDocument -Probes $t.probes -Ledger $L -Identity $id -Year '2024' -Dir $tmp -SourceFile 'C:\hz-live\HZ24_BASE.rvt' -ExpectedTitle 'HZ24_BASE'
    Check 'an opened document is registered with the path the bridge publishes' ($reg.ok -and ($reg.path -eq 'C:\hz-live\HZ24_BASE.rvt') -and (@($L.documents).Count -eq 1))
    $t.state.docs = @((Doc 'HZ_CLOSED_L_detached' $null $true))
    $reg2 = Register-HzOpenedDocument -Probes $t.probes -Ledger $L -Identity $id -Year '2024' -Dir $tmp -SourceFile 'C:\hz-live\HZ_CLOSED_L.rvt' -ExpectedTitle 'HZ_CLOSED_L_detached'
    Check 'a detached open with no path is registered EXPLICITLY as pathless' ($reg2.ok -and $reg2.pathless -and (@($L.documents | Where-Object { $_.pathless_declared }).Count -eq 1))
    $t.state.docs = @((Doc 'OTRO' 'C:\obra\OTRO.rvt' $true))
    $reg3 = Register-HzOpenedDocument -Probes $t.probes -Ledger $L -Identity $id -Year '2024' -Dir $tmp -SourceFile 'C:\hz-live\HZ24_BASE.rvt' -ExpectedTitle 'HZ24_BASE'
    Check 'a disagreement between the open and the active document registers nothing' ((-not $reg3.ok) -and (@($L.documents).Count -eq 2))
    $t.state.healthOk = $false
    $reg4 = Register-HzOpenedDocument -Probes $t.probes -Ledger $L -Identity $id -Year '2024' -Dir $tmp -SourceFile 'C:\hz-live\HZ24_BASE.rvt' -ExpectedTitle 'HZ24_BASE'
    Check 'an unreadable session registers nothing and says why' ((-not $reg4.ok) -and ($reg4.why -match 'could not be registered'))
    Stop-Helpers
}
finally {
    Stop-Helpers
}

# -----------------------------------------------------------------------------
# 11. THE REAL THING, briefly: a process actually called Revit.exe whose path
#     names no year. It proves the real probe classifies it as unknown and that
#     the REAL helper script refuses to touch a manifest while it runs. It is a
#     copy of waitfor.exe - no window, and it exits on its own after 20 s even if
#     this script were interrupted before stopping it.
# -----------------------------------------------------------------------------
$fake = $null
# WHICH year to ask the helper about: one that has no Revit of its own running,
# decided BEFORE the fake process starts - otherwise the refusal names that year's
# own Revit and says nothing about the case under test.
$guardYear = Get-FreeRevitYear
try {
    $waitfor = Join-Path $env:SystemRoot 'System32\waitfor.exe'
    if ($SkipProcessNamedRevit) {
        foreach ($case in 'the REAL probe classifies a process called Revit.exe with no year in its path as unknown',
                          'the REAL guard blocks a manifest change while it runs',
                          'the REAL helper refuses to restore while a Revit of unknown year runs') {
            Skip $case 'asked not to start a process the machine would call Revit (-SkipProcessNamedRevit)'
        }
    }
    elseif (Test-Path -LiteralPath $waitfor) {
        $fakeDir = Join-Path $tmp 'fake-revit'
        New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
        Copy-Item -LiteralPath $waitfor -Destination (Join-Path $fakeDir 'Revit.exe') -Force
        $fake = Start-Process -FilePath (Join-Path $fakeDir 'Revit.exe') `
            -ArgumentList 'HzYearMatrixTestSignal', '/t', '20' -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 700
        $probes = New-HzMatrixProbes -Repo (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) -ServerExe 'C:\nowhere\horizun-mcp.exe'
        $c = Get-HzRevitProcessClasses -Probes $probes -Year '2099'
        Check 'the REAL probe classifies a process called Revit.exe with no year in its path as unknown' (
            @($c.unknown | Where-Object { $_.pid -eq $fake.Id }).Count -eq 1) ("unknown=$(@($c.unknown).Count) other=$(@($c.other_year).Count)")
        $a = Test-HzManifestChangeAllowed -Probes $probes -Year '2099'
        Check 'the REAL guard blocks a manifest change while it runs' ((-not $a.ok) -and ($a.blocked_by -eq 'unknown_year') -and ($a.pids -contains $fake.Id))
        # And the helper, invoked on its own, refuses the same way - against a temp
        # %APPDATA% so no real manifest is in reach even if it did not.
        $guardAppData = Join-Path $tmp 'appdata-guard'
        New-Item -ItemType Directory -Force -Path (Join-Path $guardAppData 'Autodesk\Revit\Addins\2026') | Out-Null
        $savedAppData = $env:APPDATA
        try {
            $env:APPDATA = $guardAppData
            if (-not $guardYear) {
                Skip 'the REAL helper refuses to restore while a Revit of unknown year runs' 'no Revit year was free before the test started'
            }
            else {
                $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'dev-addin-session.ps1') -Year $guardYear -Restore 2>&1
                $code = $LASTEXITCODE
                Check 'the REAL helper refuses to restore while a Revit of unknown year runs' (
                    ($code -ne 0) -and (($out -join ' ') -match 'could not be determined')) ("exit $code : " + ($out -join ' '))
            }
        }
        finally { $env:APPDATA = $savedAppData }
    }
    else {
        Write-Host '  SKIP  no waitfor.exe on this machine: the real unknown-year case was not exercised' -ForegroundColor Yellow
    }
}
finally {
    if ($fake) { try { if (-not $fake.HasExited) { $fake.Kill(); $fake.WaitForExit(5000) | Out-Null } } catch { } }
}
Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue

if ($SummaryPath) {
    $dir = Split-Path -Parent $SummaryPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    ([ordered]@{
        schema = 'horizun.year-matrix.session-tests/1'
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        repo_head = (& git -C (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) rev-parse HEAD 2>$null)
        passed = $script:Passed; failed = $script:Failed; skipped = $script:Skipped
        skipped_cases = @($script:SkippedCases)
    } | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $SummaryPath -Encoding utf8
    Write-Host ("  summary: {0}" -f $SummaryPath) -ForegroundColor DarkGray
}
Write-Host ("  {0} passed, {1} failed, {2} skipped" -f $script:Passed, $script:Failed, $script:Skipped) -ForegroundColor $(if ($script:Failed -eq 0) { 'Green' } else { 'Red' })
exit $(if ($script:Failed -eq 0) { 0 } else { 1 })
