# -----------------------------------------------------------------------------
# Horizun Revit MCP - an ISOLATED Revit session for DWG -> BIM evaluation runs.
#
#   session.ps1 start    [-Year 2026] [-FailAction <action key>] [-DeclineAddIn <name>...]
#   session.ps1 register [-Year 2026] [-ExpectedTitle <title>] [-SourceFile <path>]
#   session.ps1 stop     [-Year 2026]
#   session.ps1 status   [-Year 2026]
#
# A thin wrapper over scripts/live/owned-session.ps1, which applies the rules of
# scripts/live/year-matrix.session.ps1 (and its tests) to a session that spans
# several commands. In short:
#   start    builds this tree for the year, refuses if ANY Revit of that year - or
#            one whose year cannot be read - is running, snapshots the year, enables
#            the development manifest, starts Revit and records pid + start time +
#            executable; then waits until the bridge answers FOR THAT PID. While it
#            waits it may close Autodesk's Insights notice on that pid, and answer
#            "Do Not Load" to the unsigned-add-in prompt ONLY for an add-in named in
#            -DeclineAddIn (never Horizun). -FailAction sets HORIZUN_TEST_FAIL_ACTION
#            for the started Revit only.
#   register records the ACTIVE document by the path the bridge publishes. The
#            drivers call it after every open or save-as that reported success; a
#            document that is not registered is somebody else's.
#   stop     closes only documents in the register, one at a time, re-reading the
#            session before each; any other document - or an unanswered health, or
#            an identity that no longer matches - leaves Revit RUNNING and writes
#            recovery pending. The process is asked to exit, never killed. The
#            manifest is restored and verified against the start snapshot.
#   status   prints the recorded state; changes nothing.
#
# Writes the server path to $env:TEMP\hz_srv.txt and the year to $env:TEMP\hz_year.txt,
# which the drivers pass as HORIZUN_SERVER_EXE and HORIZUN_REVIT_YEAR.
# -----------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('start', 'register', 'stop', 'status')][string]$Operation,
    [ValidateSet('2023', '2024', '2025', '2026', '2027')][string]$Year = '2026',
    [string]$FailAction = '',
    [string[]]$DeclineAddIn = @(),
    [string]$ExpectedTitle,
    [string]$SourceFile,
    [int]$WaitMinutes = 10,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'scripts\live\owned-session.ps1')
$server = Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'
$probes = New-HzOwnedProbes -Repo $repo -ServerExe $server

function Close-HzNamedUnsignedPrompt([int]$ProcessId, [string[]]$Names) {
    # The unsigned-add-in prompt, on THIS pid, for an add-in NAMED by the caller and
    # never Horizun: "Do Not Load" for this process only, no trust stored.
    if (-not $Names) { return @() }
    $A = [System.Windows.Automation.AutomationElement]
    $done = @()
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)
    foreach ($w in $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        $dlg = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Security - Unsigned Add-In')))
        if (-not $dlg) { continue }
        $body = ($dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                 ForEach-Object { $_.Current.Name }) -join "`n"
        $named = $Names | Where-Object { $body -match ('Name:\s+' + [regex]::Escape($_) + '\s') }
        if (-not $named -or $body -match 'Horizun') { continue }
        $btn = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Do Not Load')))
        if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $done += $named }
    }
    return $done
}

$lock = Enter-HzOwnedLock -Year $Year
try {
    switch ($Operation) {
        'status' {
            $s = Read-HzOwnedState $Year
            if (-not $s) { "no Revit $Year session is recorded"; break }
            [ordered]@{ phase = $s.phase; pid = $s.identity.pid; started = $s.started_utc
                        registered = @($s.ledger.documents | ForEach-Object { $_.title }) } | ConvertTo-Json -Depth 5
        }
        'register' {
            $r = Register-HzOwnedDocument -Probes $probes -Year $Year -ExpectedTitle $ExpectedTitle -SourceFile $SourceFile
            ($r | ConvertTo-Json -Depth 5 -Compress)
            if (-not $r.ok) { exit 3 }
        }
        'stop' {
            $r = Stop-HzOwnedSession -Probes $probes -Year $Year
            if ($r.ok) { "stopped: Revit $Year session closed and its manifest restored (verified)" }
            elseif ($r.state -eq 'no_session') { $r.why }
            else {
                $what = if ($r.close -and $r.close.state -notin @('closed', 'already_exited')) { "close: $($r.close.state) - $($r.close.why)" }
                        else { "restore: $($r.restore.state) - $($r.restore.why)" }
                "NOT stopped. $what Recovery pending: $($r.recovery_pending)"
                exit 2
            }
        }
        'start' {
            $dir = Join-Path (Get-HzOwnedRoot) ("run-$Year-" + (Get-Date -Format 'yyyyMMddHHmmss'))
            $null = New-Item -ItemType Directory -Force -Path $dir
            if (-not $NoBuild) {
                # Build BEFORE anything changes: a failed build leaves the machine as it was.
                dotnet build (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c Release --nologo -v:q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'the server did not build; nothing was changed' }
                dotnet build (Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj') -c Release "-p:RevitYear=$Year" --nologo -v:q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "the add-in for $Year did not build; nothing was changed" }
            }
            $state = Start-HzOwnedSession -Probes $probes -Year $Year -Dir $dir -ServerExe $server -FailAction $FailAction
            Set-Content -Path "$env:TEMP\hz_srv.txt" -Value $server -NoNewline
            Set-Content -Path "$env:TEMP\hz_year.txt" -Value $Year -NoNewline
            "server: $server"
            "revit $Year pid $($state.identity.pid) recorded"
            $deadline = (Get-Date).AddMinutes($WaitMinutes)
            $up = $null
            while ((Get-Date) -lt $deadline) {
                $declined = @(Close-HzNamedUnsignedPrompt -ProcessId ([int]$state.identity.pid) -Names $DeclineAddIn)
                if ($declined.Count) { "$(Get-Date -Format HH:mm:ss) declined $($declined -join ', ') for this Revit process" }
                $up = Wait-HzOwnedBridge -Probes $probes -Year $Year -Minutes 1
                if ($up.notices_closed) { "$(Get-Date -Format HH:mm:ss) closed Autodesk's Insights failure notice" }
                if ($up.ok) { "$(Get-Date -Format HH:mm:ss) bridge up"; break }
            }
            if (-not $up -or -not $up.ok) { throw "the bridge did not answer for the recorded pid in $WaitMinutes min; the session is recorded - stop it with: session.ps1 stop -Year $Year" }
        }
    }
}
finally { $lock.Dispose() }
