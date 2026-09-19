#Requires -Version 5.1
<#
  One iteration of the live loop, unattended: close Revit, install the tree under
  test, start the RIGHT Revit, hand it the disposable model, and stop when the
  bridge says that document is active.

  Two things this gets right that doing it by hand did not:

  * Revit is started by its own exe. The .rvt association on this machine is Revit
    2027, so opening the file with no Revit running starts the wrong year against a
    stale add-in - which then shows the unsigned-add-in dialog for ITS binary and
    reads exactly like the year under test failing to load.

  * The model is opened THROUGH THE BRIDGE, not by the shell. Both of the other
    ways go through Revit's own open, which raises the warnings roll-up on these
    fixtures - and a modal with nobody at the keyboard stops Revit servicing the
    bridge at all, so every later call is refused with "Revit has a MODAL DIALOG
    open" and the cycle reads as a bridge that never came up. Measured again on
    2026-08-27: the shell hand-off cost ten minutes of waiting for a document that
    was sitting behind an unanswered dialog. horizun_open_document opens the same
    file with no dialog, and says whether it worked.
#>
[CmdletBinding()]
param(
    # The model is the one thing nobody can guess for you. Everything else derives:
    # the repo from where this script lives, the workspace from the user's temp.
    [Parameter(Mandatory = $true)][string]$Model,
    [string]$Repo,
    [int]$Year = 2026
)
$ErrorActionPreference = 'Stop'
if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$scratch = Join-Path $env:TEMP 'horizun-live-cycle'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$health  = Join-Path $scratch 'cycle-health.json'

function Say($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

function Ask-Health {
    if (Test-Path $health) { Remove-Item $health -Force }
    & (Join-Path $Repo 'scripts\hz-call.ps1') -Tool horizun_health -Json $health -Quiet -TimeoutSec 90 2>&1 | Out-Null
    if (-not (Test-Path $health)) { return $null }
    return (Get-Content $health -Raw | ConvertFrom-Json).result
}

# ONLY THE REVIT THIS CYCLE STARTED IS EVER CLOSED. This used to be
# `Stop-Process -Name Revit -Force`: every Revit on the machine, the owner's included,
# killed with whatever was unsaved in it. The previous iteration's Revit is now
# recorded (pid, start time, executable, and the model it opened) and closed by the
# rules of scripts/live/owned-session.ps1 - registered documents only, re-read before
# each close, never killed. Anything else of this year, or a Revit whose year cannot
# be read, stops the cycle: installing over a running Revit is not something to guess.
. (Join-Path $Repo 'scripts\live\owned-session.ps1')
$probes = New-HzOwnedProbes -Repo $Repo -ServerExe $null
$lock = Enter-HzOwnedLock -Year ([string]$Year)
$prev = Close-HzRecordedRevit -Probes $probes -Name 'live-cycle' -Year ([string]$Year)
if ($prev.state -notin @('no_record', 'closed', 'already_exited')) {
    Say ("REFUSING to continue: the Revit this cycle started last time was left running (" + $prev.state + "): " + $prev.why)
    exit 4
}
$free = Test-HzManifestChangeAllowed -Probes $probes -Year ([string]$Year)
if (-not $free.ok) {
    Say ("REFUSING to continue: " + $free.why + " It was not started by this cycle and is left alone.")
    exit 4
}
# Stale discovery files name processes that are gone. ONLY those are removed: the
# file of a Revit that is running belongs to whoever runs it.
Get-ChildItem (Join-Path $env:USERPROFILE '.horizun\discovery') -Filter 'revit-*.json' -ErrorAction SilentlyContinue | ForEach-Object {
    $pidInFile = $null
    try { $pidInFile = [int]((Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).pid) } catch { }
    if ($pidInFile -and -not (Get-Process -Id $pidInFile -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $_.FullName -Force }
}

Say 'installing'
$log = & powershell -ExecutionPolicy Bypass -File (Join-Path $Repo 'install.ps1') -Years $Year 2>&1
$stamp = $log | Select-String -Pattern "add-in $Year\s+(\S+)" | Select-Object -First 1
if (-not ($log -match 'installed and verified')) {
    Say 'INSTALL FAILED'
    $log | Select-Object -Last 20
    exit 1
}
Say ("installed: " + ($stamp.Matches[0].Groups[1].Value))

Say "starting Revit $Year by its own exe"
$null = Start-HzRecordedRevit -Probes $probes -Name 'live-cycle' -Year ([string]$Year) -Dir $scratch
$deadline = (Get-Date).AddMinutes(6)
$up = $false
while ((Get-Date) -lt $deadline) {
    $h = Ask-Health
    if ($h -and $h.status -eq 'healthy') { $up = $true; break }
    Start-Sleep -Seconds 10
}
if (-not $up) { Say 'the bridge never came up'; exit 2 }
Say 'bridge is up with no document'

Say 'opening the model THROUGH THE BRIDGE (the shell raises a dialog nobody can answer)'
$openArgs = Join-Path $scratch 'cycle-open.json'
$openOut  = Join-Path $scratch 'cycle-open-result.json'
@{ path = $Model; idempotency_key = ('live-cycle-' + [guid]::NewGuid().ToString('N')) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $openArgs -Encoding UTF8
if (Test-Path $openOut) { Remove-Item $openOut -Force }
& (Join-Path $Repo 'scripts\hz-call.ps1') -Tool horizun_open_document -ArgumentsPath $openArgs `
    -Json $openOut -Quiet -TimeoutSec 900 2>&1 | Out-Null
if (Test-Path $openOut) {
    $opened = Get-Content $openOut -Raw | ConvertFrom-Json
    if ($opened.is_error) { Say ('the open was refused: ' + $opened.raw); exit 3 }
}

$deadline = (Get-Date).AddMinutes(10)
while ((Get-Date) -lt $deadline) {
    $h = Ask-Health
    if ($h) {
        $active = ($h.open_documents | Where-Object { $_.is_active }).title
        if ($active) {
            $reg = Register-HzRecordedDocument -Probes $probes -Name 'live-cycle' -Year ([string]$Year) -ExpectedTitle $active -SourceFile $Model
            if (-not $reg.ok) { Say ("WARNING: the model was not registered, so the next cycle will leave this Revit running: " + $reg.why) }
            Say ("ACTIVE: $active   commit=" + $h.horizun_commit.Substring(0,12)); exit 0
        }
    }
    Start-Sleep -Seconds 15
}
Say 'the model never became active'
exit 3
