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

  * The model is handed to an ALREADY RUNNING instance. Passing the file on the
    command line makes Revit open it during startup, where a workshared central
    raises a modal nobody is there to answer and the open is simply abandoned. The
    shell hand-off into a live instance detaches without asking.
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

# EXACTLY ONE INSTANCE, or nothing. Three cycles were lost to this: instances
# accumulated, the bridge correctly refused to pick between two Revit 2026s
# ("this harness will not pick one - that is the same guess the bridge itself
# refuses to make"), and the failure read as "the bridge never came up". Killing
# and hoping is what produced the pile; verifying is what fixes it.
Say 'closing every Revit (nothing to save: the fixture is read-only and detached)'
Stop-Process -Name Revit -Force -ErrorAction SilentlyContinue
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Process -Name Revit -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
$left = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($left.Count -gt 0) {
    Say ("REFUSING to continue: " + $left.Count + " Revit process(es) survived the close (" +
         (($left | ForEach-Object { $_.Id }) -join ', ') + "). Installing over a running Revit " +
         "changes nothing and starting a second instance makes the bridge refuse to pick one.")
    exit 4
}
# Stale discovery files name processes that are gone. The resolver skips them, but a
# leftover for a pid that Windows has since REUSED is a live-looking lie.
Get-ChildItem (Join-Path $env:USERPROFILE '.horizun\discovery') -Filter 'revit-*.json' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

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
Start-Process -FilePath "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
$deadline = (Get-Date).AddMinutes(6)
$up = $false
while ((Get-Date) -lt $deadline) {
    $h = Ask-Health
    if ($h -and $h.status -eq 'healthy') { $up = $true; break }
    Start-Sleep -Seconds 10
}
if (-not $up) { Say 'the bridge never came up'; exit 2 }
Say 'bridge is up with no document'

Say 'handing the model to the running instance'
$before = @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count
Start-Process $Model
Start-Sleep -Seconds 20
$after = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($after.Count -gt $before) {
    # The shell launched a SECOND Revit instead of handing the file to the live one -
    # the .rvt association points at one specific year, and DDE only reaches a Revit
    # that is ready. Two instances mean the bridge will refuse to choose, so say that
    # rather than letting it read as a dead bridge six minutes later.
    Say ("the shell started a SECOND Revit instead of handing over the file (" + $before +
         " -> " + $after.Count + "). Kill all Revit and re-run; if it repeats, the first " +
         "instance was not ready to accept DDE.")
    exit 5
}
$deadline = (Get-Date).AddMinutes(10)
while ((Get-Date) -lt $deadline) {
    $h = Ask-Health
    if ($h) {
        $active = ($h.open_documents | Where-Object { $_.is_active }).title
        if ($active) { Say ("ACTIVE: $active   commit=" + $h.horizun_commit.Substring(0,12)); exit 0 }
    }
    Start-Sleep -Seconds 15
}
Say 'the model never became active'
exit 3
