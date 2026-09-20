# -----------------------------------------------------------------------------
# Horizun Revit MCP - original Horizun code.
#
# AN ISOLATED REVIT SESSION THAT SPANS SEVERAL COMMANDS, on the rules of
# year-matrix.session.ps1. That file already decides - and its tests prove -
# when a Revit may be closed and how: identity is pid + start time + executable,
# re-read before every step; a document is ours only when a REGISTER of this run
# holds its path (a title or an HZ_ prefix is not proof); one foreign document,
# one unanswered health, one identity that does not match, and the process is
# LEFT RUNNING; documents are closed one at a time through a rehearsal whose aim
# is checked before its token is spent; the process is asked to exit, never
# killed; a manifest is restored only with no Revit of that year or of an
# unknown year running, verified against a snapshot taken before anything
# changed, and what cannot be put back is written down as recovery pending.
#
# What this file adds, and only this:
#   - the identity, the register and the start snapshot PERSIST between
#     commands (start, register, stop run as separate processes), in
#     %USERPROFILE%\.horizun\owned-sessions\session-<year>.json;
#   - an exclusive per-year lock for the length of each command, so two runs
#     cannot start, register or stop the same year at once;
#   - a bridge wait tied to the recorded pid, which may close exactly one known
#     third-party notice (Autodesk's Insights "External Tool Failure", English
#     or Spanish) on that pid and nothing else.
#
# MEASURED 2026-09-18, why it exists: the previous dwg-bim session script sent a
# close to every Revit.exe and closed the machine owner's own Revit 2023 session;
# its successor still asked its own Revit to exit BEFORE looking at the
# documents, and treated "the Save dialog names HZ_" as proof of ownership.
# -----------------------------------------------------------------------------
. (Join-Path $PSScriptRoot 'year-matrix.session.ps1')

function Get-HzOwnedRoot {
    $root = $env:HORIZUN_OWNED_SESSION_ROOT
    if ([string]::IsNullOrWhiteSpace($root)) { $root = Join-Path $env:USERPROFILE '.horizun\owned-sessions' }
    $null = New-Item -ItemType Directory -Force -Path $root
    return $root
}

function Get-HzOwnedStatePath([string]$Year) { return (Join-Path (Get-HzOwnedRoot) "session-$Year.json") }

function Enter-HzOwnedLock {
    <#
    .SYNOPSIS
      An exclusive lock on one year for the life of the calling process. A second
      command for the same year - another run, another shell - is refused at once
      instead of racing this one. Released when the stream is disposed or the
      process exits; a crashed command leaves nothing stale behind.
    #>
    param([Parameter(Mandatory)][string]$Year)
    $path = Join-Path (Get-HzOwnedRoot) "session-$Year.lock"
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw "another command holds the Revit $Year session lock ($path); two runs do not drive one year at once. Nothing was changed."
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(("pid {0} since {1:o}" -f $PID, (Get-Date).ToUniversalTime()))
    $fs.SetLength(0); $fs.Write($bytes, 0, $bytes.Length); $fs.Flush()
    return $fs
}

function Read-HzOwnedState([string]$Year) {
    $p = Get-HzOwnedStatePath $Year
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return (Get-Content -LiteralPath $p -Raw | ConvertFrom-Json -AsHashtable)
}

function Save-HzOwnedState([string]$Year, $State) {
    $p = Get-HzOwnedStatePath $Year
    $tmp = "$p.tmp"
    ($State | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath $tmp -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination $p -Force
}

function Restore-HzLedger($Saved) {
    # The register comes back from JSON as a hashtable; the module expects its
    # arrays to BE arrays (a single document would otherwise arrive as a scalar).
    $l = New-HzRehearsalLedger
    foreach ($k in @('session_pid', 'session_identity', 'anchor_dir')) { if ($Saved.Contains($k)) { $l[$k] = $Saved[$k] } }
    foreach ($k in @('documents', 'files', 'dialogs_closed', 'registration_failures')) {
        if ($Saved.Contains($k) -and $null -ne $Saved[$k]) { $l[$k] = @($Saved[$k]) }
    }
    return $l
}

function Close-HzInsightsNotice {
    <#
    .SYNOPSIS
      Close Autodesk's own "External Tool Failure" notice for its Insights add-in,
      ONLY on the given pid, only when the body names "Insights", through the
      notice's own window close. English and Spanish titles; the Spanish one is
      nested inside the main window and its close control is not a button
      (measured 2026-09-18). Nothing else is ever answered. Returns the titles it
      closed.
    #>
    param([Parameter(Mandatory)][int]$ProcessId)
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $A = [System.Windows.Automation.AutomationElement]
    $closed = @()
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)
    $isWindow = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    foreach ($top in $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        foreach ($el in @($top) + @($top.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isWindow))) {
            $title = $el.Current.Name
            if ($title -notmatch '^(External Tools - External Tool Failure|Herramientas externas - Fallo de herramienta externa)$') { continue }
            $body = ($el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                     ForEach-Object { $_.Current.Name }) -join ' | '
            if ($body -notmatch '"Insights"') { continue }
            try { $el.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); $closed += $title } catch { }
        }
    }
    return $closed
}

function New-HzOwnedProbes {
    param([Parameter(Mandatory)][string]$Repo, [string]$ServerExe)
    $p = New-HzMatrixProbes -Repo $Repo -ServerExe $ServerExe
    $closeNotice = ${function:Close-HzInsightsNotice}
    # Given the pid by the caller, never by a title: a dialog of another process
    # with the same title is not this one's.
    $p['DismissInsights'] = { param([int]$ProcessId) return @(& $closeNotice -ProcessId $ProcessId) }.GetNewClosure()
    $p['StartRevit'] = { param([string]$Exe, [string]$FailAction)
        if ($FailAction) {
            $env:HORIZUN_TEST_FAIL_ACTION = $FailAction
            try { return (Start-Process -FilePath $Exe -PassThru) } finally { Remove-Item Env:HORIZUN_TEST_FAIL_ACTION -ErrorAction SilentlyContinue }
        }
        return (Start-Process -FilePath $Exe -PassThru)
    }
    return $p
}

function Start-HzOwnedSession {
    <#
    .SYNOPSIS
      Snapshot the year, enable the development manifest (refused with any Revit
      of that year or of an unknown year running), start Revit, record WHICH
      process that is. The state is written before Revit starts, so a crash
      between the two leaves a record a stop can act on.
      Returns the state.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year, [Parameter(Mandatory)][string]$Dir,
          [string]$ServerExe, [string]$FailAction, [string]$RevitExe)
    if (Read-HzOwnedState $Year) {
        throw "a Revit $Year session is already recorded ($(Get-HzOwnedStatePath $Year)); stop it first. Nothing was changed."
    }
    $snap = Get-HzYearStateAtStart -Probes $Probes -Year $Year
    $state = [ordered]@{ schema = 'horizun.owned-session/1'; year = $Year; dir = $Dir; phase = 'enabling'
                         started_utc = (Get-Date).ToUniversalTime().ToString('o'); server_exe = $ServerExe
                         fail_action = $FailAction; state_at_start = $snap; identity = $null; ledger = $null }
    $en = Invoke-HzYearEnable -Probes $Probes -Year $Year
    if (-not $en.ok) { throw ("the development manifest was not enabled: " + $en.why + " Revit was not started.") }
    $state.phase = 'enabled'
    Save-HzOwnedState $Year $state
    if (-not $RevitExe) { $RevitExe = "C:\Program Files\Autodesk\Revit $Year\Revit.exe" }
    $proc = & $Probes.StartRevit $RevitExe $FailAction
    $identity = New-HzSessionIdentity -Probes $Probes -ProcessId $proc.Id -ExpectedExe $RevitExe
    $ledger = New-HzRehearsalLedger
    Register-HzRehearsalSession -Ledger $ledger -Identity $identity
    $state.identity = $identity; $state.ledger = $ledger; $state.phase = 'running'
    Save-HzOwnedState $Year $state
    return $state
}

function Wait-HzOwnedBridge {
    <#
    .SYNOPSIS
      Until the bridge answers health FOR THE RECORDED PID. The only window this
      may touch is the Insights notice on that pid.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year, [int]$Minutes = 10)
    $state = Read-HzOwnedState $Year
    if (-not $state -or -not $state.identity) { throw "no running Revit $Year session is recorded" }
    $deadline = (Get-Date).AddMinutes($Minutes)
    $notices = @()
    while ((Get-Date) -lt $deadline) {
        $id = Get-HzSessionIdentityState -Probes $Probes -Identity $state.identity
        if ($id.state -eq 'exited') { throw "the recorded Revit $Year (pid $($state.identity.pid)) exited before the bridge answered" }
        if ($id.state -ne 'alive') { throw "the recorded Revit $Year is not verifiably ours ($($id.state)): $($id.why)" }
        if ($Probes.ContainsKey('DismissInsights')) { $notices += @(& $Probes.DismissInsights ([int]$state.identity.pid)) }
        $read = Read-HzSessionDocuments -Probes $Probes -Identity $state.identity -Year $Year -Dir $state.dir
        if ($read.ok) { return @{ ok = $true; notices_closed = $notices; documents = @($read.documents) } }
        Start-Sleep -Seconds 5
    }
    return @{ ok = $false; notices_closed = $notices; why = "the bridge did not answer for pid $($state.identity.pid) within $Minutes min" }
}

function Register-HzOwnedDocument {
    <#
    .SYNOPSIS
      After an open (or a save-as) REPORTED SUCCESS: register the active document
      by the identity the bridge publishes - its path - into the persisted
      register. Nothing is registered on a disagreement.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year, [string]$ExpectedTitle, [string]$SourceFile)
    $state = Read-HzOwnedState $Year
    if (-not $state -or -not $state.identity) { return @{ ok = $false; why = "no running Revit $Year session is recorded; nothing registered" } }
    $ledger = Restore-HzLedger $state.ledger
    $r = Register-HzOpenedDocument -Probes $Probes -Ledger $ledger -Identity $state.identity -Year $Year -Dir $state.dir `
                                   -ExpectedTitle $ExpectedTitle -SourceFile $SourceFile
    $state.ledger = $ledger
    Save-HzOwnedState $Year $state
    return $r
}

function Get-HzOwnedSituation {
    <#
    .SYNOPSIS
      What is ACTUALLY true right now about a recorded session, as opposed to what
      the last command managed to write down before it was interrupted. Changes
      nothing.

      MEASURED: 'status' printed the recorded phase and nothing else, so a session
      whose Revit had gone - closed by hand, crashed, or killed by a restart -
      still read 'running'. A record is what somebody INTENDED; this asks the
      machine. It reports three separate things and does not merge them:

        recorded    the phase the state file holds
        process     alive | exited | not_ours | never_started, asked of the pid
        environment whether the year's manifest is back as the snapshot found it,
                    verified against files and hashes - NEVER inferred from the
                    absence of a Revit, which is the check this exists to refuse

      Returns @{ recorded; process; environment; recovery_pending; needs }.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year)
    $state = Read-HzOwnedState $Year
    if (-not $state) { return @{ recorded = $null; why = "no Revit $Year session is recorded" } }

    # PROJECTED TO WHAT A READER NEEDS. Get-HzSessionIdentityState hands back the live Process object
    # with it, and printing that buries the one word this exists to say under a page of handles.
    $process = if (-not $state.identity) { @{ state = 'never_started'
                                              why = 'the session was interrupted before Revit was started or recorded' } }
               else {
                   $id = Get-HzSessionIdentityState -Probes $Probes -Identity $state.identity
                   # Get-HzField, not dot access: strict mode makes a missing property THROW, and an
                   # identity state that carries no 'why' (an alive process has nothing to explain) is
                   # the ordinary case, not an error.
                   @{ state = [string](Get-HzField $id 'state'); why = [string](Get-HzField $id 'why')
                      pid = [int]$state.identity.pid; exe = [string]$state.identity.exe
                      started = [string]$state.identity.start_time }
               }

    # THE ENVIRONMENT, BY FILES AND HASHES. Test-HzInstallationMatchesStart compares
    # the installed manifest and its DLL against the snapshot taken before anything
    # was enabled; an absent Revit proves nothing about either.
    $now = @{} + (& $Probes.ManifestState $Year)
    $now['installed_dll_state'] = (& $Probes.InstalledDllState $Year)
    $matches = if ($state.state_at_start) { Test-HzInstallationMatchesStart -StateAtStart $state.state_at_start -Now $now }
               else { @{ ok = $false; state = 'restore_unverifiable'
                         why = 'no start snapshot was captured; nothing can be verified against it' } }
    $devLeft = ((Get-HzField $now 'dev_present') -or (Get-HzField $now 'aside_present'))
    $environment = @{ development_manifest_still_in_place = [bool]$devLeft
                      installation_matches_start = [bool]$matches.ok
                      state = [string]$matches.state; why = [string]$matches.why }

    $pendingPath = Join-Path (Get-HzOwnedRoot) "recovery-pending-$Year.json"
    $pending = if (Test-Path -LiteralPath $pendingPath) { Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json } else { $null }

    $needs = @()
    if ($process.state -eq 'exited' -or $process.state -eq 'never_started') {
        if ($devLeft) { $needs += 'the Revit this session started is gone and the development manifest is still in place: run stop, which restores it' }
        else { $needs += 'the Revit this session started is gone and the year is already restored: run stop to clear the record' }
    }
    if ($process.state -eq 'not_ours') { $needs += 'the recorded pid is NOT the process this session started; stop will not touch it' }
    # A LIVE SESSION IS SUPPOSED TO HAVE THE DEVELOPMENT MANIFEST IN PLACE. Saying so as a "need"
    # every time would train a reader to skip the list, which is the one place a real difference shows.
    if ((-not $environment.installation_matches_start) -and ($process.state -ne 'alive')) {
        $needs += ('the installation is not as it was at start: ' + $environment.state)
    }

    return @{ recorded = @{ phase = [string]$state.phase; started_utc = [string]$state.started_utc
                            pid = if ($state.identity) { [int]$state.identity.pid } else { $null }
                            documents = @(if ($state.ledger) { $state.ledger.documents | ForEach-Object { $_.title } }) }
              process = $process; environment = $environment; recovery_pending = $pending; needs = @($needs) }
}

function Stop-HzOwnedSession {
    <#
    .SYNOPSIS
      Close the recorded session by the module's rules and restore the year, or
      leave everything as it is and write recovery pending. The state file is
      removed ONLY when the process is gone AND the restore verified.
      Returns @{ ok; close; restore; recovery_pending }.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year, [int]$ExitTimeoutSec = 240)
    $state = Read-HzOwnedState $Year
    if (-not $state) { return @{ ok = $true; state = 'no_session'; why = "no Revit $Year session is recorded; nothing was touched" } }
    $out = [ordered]@{ ok = $false; close = $null; restore = $null; recovery_pending = $null }
    if ($state.identity) {
        $ledger = Restore-HzLedger $state.ledger
        $close = Close-HzRehearsalSession -Probes $Probes -Identity $state.identity -Ledger $ledger -Year $Year `
                                          -Dir $state.dir -ExitTimeoutSec $ExitTimeoutSec
        $out.close = $close
        if ($close.state -notin @('closed', 'already_exited')) {
            $state.phase = 'left_running'
            Save-HzOwnedState $Year $state
            $out.recovery_pending = Write-HzRecoveryPending -Dir (Get-HzOwnedRoot) -Year $Year -Record $close
            return $out
        }
    }
    $restore = Restore-HzYearSession -Probes $Probes -Year $Year -StateAtStart $state.state_at_start
    $out.restore = $restore
    if (-not $restore.ok) {
        $state.phase = 'restore_pending'
        Save-HzOwnedState $Year $state
        $out.recovery_pending = Write-HzRecoveryPending -Dir (Get-HzOwnedRoot) -Year $Year -Record $restore
        return $out
    }
    Remove-Item -LiteralPath (Get-HzOwnedStatePath $Year) -Force
    $pending = Join-Path (Get-HzOwnedRoot) "recovery-pending-$Year.json"
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    $out.ok = $true
    return $out
}

# -----------------------------------------------------------------------------
# A RECORDED REVIT WITHOUT A DEVELOPMENT MANIFEST. Scripts that start Revit on the
# INSTALLED add-in (live-cycle.ps1, verify-structure-matrix.ps1,
# deploy-and-verify.ps1) used to "close every Revit" - by process name, or by
# closing every open document with discard_unsaved - before their next step.
# They now record the Revit they start and the documents they open, under their
# own name, and close only that, by the same rules; nothing else is ever sent a
# close. There is no manifest here, so nothing is restored.
# -----------------------------------------------------------------------------
function Get-HzRecordPath([string]$Name, [string]$Year) { return (Join-Path (Get-HzOwnedRoot) "recorded-$Name-$Year.json") }

function Read-HzRecord([string]$Name, [string]$Year) {
    $p = Get-HzRecordPath $Name $Year
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return (Get-Content -LiteralPath $p -Raw | ConvertFrom-Json -AsHashtable)
}

function Save-HzRecord([string]$Name, [string]$Year, $Record) {
    $p = Get-HzRecordPath $Name $Year
    ($Record | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath "$p.tmp" -Encoding utf8
    Move-Item -LiteralPath "$p.tmp" -Destination $p -Force
}

function Start-HzRecordedRevit {
    <#
    .SYNOPSIS
      Start Revit <Year> and record which process that is. Refused when a record
      already exists (close it first) and when ANY Revit of that year - or of a
      year that cannot be read - is running: that one is not this script's.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Year,
          [Parameter(Mandatory)][string]$Dir, [string]$RevitExe)
    if (Read-HzRecord $Name $Year) { throw "a Revit $Year recorded by $Name already exists ($(Get-HzRecordPath $Name $Year)); close it first. Nothing was started." }
    $running = Test-HzManifestChangeAllowed -Probes $Probes -Year $Year
    if (-not $running.ok) { throw ("{0} Nothing was started and nothing was sent to that process." -f $running.why) }
    if (-not $RevitExe) { $RevitExe = "C:\Program Files\Autodesk\Revit $Year\Revit.exe" }
    $proc = & $Probes.StartRevit $RevitExe $null
    $identity = New-HzSessionIdentity -Probes $Probes -ProcessId $proc.Id -ExpectedExe $RevitExe
    $ledger = New-HzRehearsalLedger
    Register-HzRehearsalSession -Ledger $ledger -Identity $identity
    $rec = [ordered]@{ schema = 'horizun.recorded-revit/1'; name = $Name; year = $Year; dir = $Dir
                       started_utc = (Get-Date).ToUniversalTime().ToString('o'); identity = $identity; ledger = $ledger }
    Save-HzRecord $Name $Year $rec
    return $rec
}

function Register-HzRecordedDocument {
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Year,
          [string]$ExpectedTitle, [string]$SourceFile)
    $rec = Read-HzRecord $Name $Year
    if (-not $rec) { return @{ ok = $false; why = "no Revit $Year is recorded by $Name; nothing registered" } }
    $ledger = Restore-HzLedger $rec.ledger
    $r = Register-HzOpenedDocument -Probes $Probes -Ledger $ledger -Identity $rec.identity -Year $Year -Dir $rec.dir `
                                   -ExpectedTitle $ExpectedTitle -SourceFile $SourceFile
    $rec.ledger = $ledger
    Save-HzRecord $Name $Year $rec
    return $r
}

function Close-HzRecordedRevit {
    <#
    .SYNOPSIS
      Close the Revit this script recorded, by the module's rules, or leave it
      running and say why (recovery pending is written). Returns the module's
      record plus state 'no_record' when there is nothing recorded.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Year,
          [int]$ExitTimeoutSec = 240)
    $rec = Read-HzRecord $Name $Year
    if (-not $rec) { return [ordered]@{ state = 'no_record'; why = "nothing recorded by $Name for Revit $Year" } }
    $ledger = Restore-HzLedger $rec.ledger
    $close = Close-HzRehearsalSession -Probes $Probes -Identity $rec.identity -Ledger $ledger -Year $Year -Dir $rec.dir -ExitTimeoutSec $ExitTimeoutSec
    if ($close.state -in @('closed', 'already_exited')) {
        Remove-Item -LiteralPath (Get-HzRecordPath $Name $Year) -Force
        return $close
    }
    $close['recovery_pending'] = Write-HzRecoveryPending -Dir (Get-HzOwnedRoot) -Year "$Name-$Year" -Record $close
    return $close
}
