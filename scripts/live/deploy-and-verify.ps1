#Requires -Version 5.1
<#
  ONE COHERENT DEPLOY-AND-RUN CYCLE.

  A live harness measures the bridge that is INSTALLED, not the tree that is
  checked out, and the gap between those two has cost more measured hours on
  this repository than any product defect: a run against a stale binary
  reproduces every bug the old code had, perfectly, and reads as a regression.

  So this is the whole cycle, in one place, refusing at every step where the two
  could diverge:

      1  the tree must be CLEAN, because a binary stamps a commit and must be
         able to name one it actually is;
      2  only the Revit an EARLIER run of this cycle started - recorded by pid,
         start time and executable - is ever closed, and in it only the
         documents that run registered, re-read before each close; a document
         the user opened there, or a Revit of this year the cycle did not start,
         stops the cycle with nothing sent to it (scripts/live/owned-session.ps1);
      3  that Revit is asked to close and given time to; it is never killed,
         because a killed Revit leaves journals and a lock file and the next
         start is not the state anything measured. An MCP server running the
         installed executable is REPORTED and the cycle stops: it belongs to a
         client (possibly the one driving this run) and is never killed;
      4  the add-in and the server are deployed TOGETHER - they share a contract
         hash and there is no partial deployment;
      5  the binaries are re-signed and then watched until they stop changing,
         because signing rewrites them AFTER the deploy reports success and a
         Revit started in that window loads a half-written DLL;
      6  Revit starts and is polled until health reports the EXPECTED commit -
         not until it reports healthy, which an old build does too;
      7  the fixture documents are opened through the bridge and the staging is
         re-read from health, because an open that reports success into an empty
         Revit is exactly how a run reports dozens of product failures that
         are not;
      8  and only then does the harness run.

  Nothing here kills a process the user owns. If Revit will not close, that is
  reported and the cycle stops, because a modal dialog waiting for a person is
  not something a script may answer.

  Paths come from this file's own location and from the environment. No personal
  path is written down, and none is published.

    pwsh -File scripts/live/deploy-and-verify.ps1 -Commit HEAD -Harness verify-dwg-architecture
    ... -Harness verify-live -HarnessArgs @{ WriteProbes = $true }   # the big one
    ... -SkipHarness                                                 # deploy and stage only
#>
[CmdletBinding()]
param(
    # The commit the installed binaries must report. HEAD is the usual answer.
    [string]$Commit = 'HEAD',

    # A harness under scripts/live/, by name, run once the bridge is confirmed.
    [string]$Harness,
    [hashtable]$HarnessArgs = @{},
    [switch]$SkipHarness,

    [ValidateRange(2023, 2027)][int]$Year = 2026,

    # The documents to stage, and which one must end up active.
    [string[]]$OpenDocuments,
    [string]$ActiveDocument = 'HZ_WRITE',

    # The self-signing certificate to re-sign with. Discovered by subject when
    # not given; the cycle refuses rather than creating one, because creating a
    # trusted publisher is the machine owner's decision and not a harness step.
    [string]$CertificateThumbprint,
    [string]$CertificateSubject = 'CN=Horizun Group (self-signed add-in signing)',

    [int]$RevitStartTimeoutMinutes = 8,
    [int]$RevitCloseTimeoutSeconds = 180
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("horizun-cycle-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $scratch -Force

function Say([string]$Message) { Write-Host $Message }

function Call {
    param([string]$Tool, [hashtable]$Arguments, [string]$Tag)
    $ap = Join-Path $scratch "$Tag-args.json"
    $op = Join-Path $scratch "$Tag-out.json"
    ($Arguments | ConvertTo-Json -Depth 12 -Compress) | Set-Content -LiteralPath $ap -Encoding ascii
    & pwsh -NoProfile -File (Join-Path $repo 'scripts\hz-call.ps1') `
        -Tool $Tool -ArgumentsPath $ap -Json $op -Quiet *> $null
    if (Test-Path -LiteralPath $op) { Get-Content -LiteralPath $op -Raw | ConvertFrom-Json } else { $null }
}

function Field {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    $p.Value
}

# ---------------------------------------------------------------- 1. the tree
Set-Location -LiteralPath $repo
$dirty = @(git status --porcelain --untracked-files=no)
if ($dirty.Count -gt 0) {
    throw ("the tree is DIRTY ({0} tracked file(s) changed). A binary stamps a commit, and one built " +
           "from a modified tree names a commit it is not - every artifact from that run would be " +
           "unreproducible. Commit or stash first." -f $dirty.Count)
}
$expected = (git rev-parse $Commit).Trim()
if ($expected -notmatch '^[0-9a-f]{40}$') { throw "could not resolve '$Commit' to a commit" }
Say ("Candidate {0} ({1})" -f $expected.Substring(0, 12), $Commit)

$addinDll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\Horizun\Horizun.Revit.dll"
$serverExe = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
$revitExe = "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
if (-not (Test-Path -LiteralPath $revitExe)) { throw "Revit $Year is not installed at the expected location" }

if (-not $CertificateThumbprint) {
    $cert = @(Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
              Where-Object { $_.Subject -eq $CertificateSubject } |
              Sort-Object NotAfter -Descending)
    if ($cert.Count -eq 0) {
        throw ("no trusted local signing certificate with subject '{0}'. Create one deliberately with " +
               "scripts/self-sign.ps1 - this cycle re-signs with an existing certificate and will not " +
               "create a publisher on your behalf." -f $CertificateSubject)
    }
    $CertificateThumbprint = $cert[0].Thumbprint
}
Say ("Signing certificate {0}" -f $CertificateThumbprint.Substring(0, 12))

# --------------------------------------------------- 2 and 3. close, then exit
# This used to close EVERY document of any Revit of this year with discard_unsaved
# and then kill every horizun-mcp process by name. Ownership is now proved, not
# assumed: see scripts/live/owned-session.ps1.
. (Join-Path $PSScriptRoot 'owned-session.ps1')
$env:HORIZUN_REVIT_YEAR = [string]$Year
$probes = New-HzOwnedProbes -Repo $repo -ServerExe $null
$lock = Enter-HzOwnedLock -Year ([string]$Year)
$prev = Close-HzRecordedRevit -Probes $probes -Name 'deploy-and-verify' -Year ([string]$Year) -ExitTimeoutSec $RevitCloseTimeoutSeconds
if ($prev.state -notin @('no_record', 'closed', 'already_exited')) {
    throw ("the Revit an earlier cycle started was left running ({0}): {1} Recovery pending: {2}. " +
           "Nothing was killed and nothing was deployed." -f $prev.state, $prev.why, $prev.recovery_pending)
}
if ($prev.state -ne 'no_record') { Say ("Previous cycle's Revit: " + $prev.state) }
$free = Test-HzManifestChangeAllowed -Probes $probes -Year ([string]$Year)
if (-not $free.ok) {
    throw ("{0} It was not started by this cycle; it is left alone and nothing was deployed." -f $free.why)
}

# The MCP server holds its own executable open, so a deploy over a running one
# fails. Those processes belong to MCP clients - possibly the very client driving
# this run - and are NOT this cycle's to end. Name them and stop.
$servers = @(Get-CimInstance Win32_Process -Filter "Name = 'horizun-mcp.exe'" -ErrorAction SilentlyContinue |
             Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq [IO.Path]::GetFullPath($serverExe)) })
if ($servers.Count -gt 0) {
    $list = @($servers | ForEach-Object { "pid {0} (parent {1})" -f $_.ProcessId, $_.ParentProcessId }) -join ', '
    throw ("{0} MCP server process(es) run the installed executable: {1}. Close the MCP clients that " +
           "started them and run again; this cycle does not end processes it did not start." -f $servers.Count, $list)
}

# ------------------------------------------------------------- 4 and 5. deploy
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\deploy-both.ps1') 2>&1 |
    Select-Object -Last 4 | ForEach-Object { Say "  $_" }
if ($LASTEXITCODE -ne 0) { throw "deploy-both failed ($LASTEXITCODE)" }

& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\self-sign.ps1') `
    -Thumbprint $CertificateThumbprint 2>&1 | Select-Object -Last 2 | ForEach-Object { Say "  $_" }
if ($LASTEXITCODE -ne 0) { throw "self-sign failed ($LASTEXITCODE)" }

function Snapshot {
    @{ addin      = (Get-FileHash -LiteralPath $addinDll -Algorithm SHA256).Hash
       server     = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
       addinSig   = (Get-AuthenticodeSignature -LiteralPath $addinDll).Status
       serverSig  = (Get-AuthenticodeSignature -LiteralPath $serverExe).Status }
}
# SIGNED-STABLE, not merely deployed. Signing rewrites the binaries after the
# deploy reports success; a Revit started inside that window loads a DLL that is
# still being written and dies in the security dialog.
$first = Snapshot
Start-Sleep -Seconds 3
$second = Snapshot
if ($first.addin -ne $second.addin -or $first.server -ne $second.server) {
    throw 'the installed binaries were still changing after signing; not starting Revit on a moving target'
}
if ($second.addinSig -ne 'Valid' -or $second.serverSig -ne 'Valid') {
    throw "Authenticode is not Valid (add-in $($second.addinSig), server $($second.serverSig))"
}
Say ("Signed-stable: add-in {0} server {1}" -f $second.addin.Substring(0, 12), $second.server.Substring(0, 12))

# --------------------------------------------------- 6. Revit, at THIS commit
$null = Start-HzRecordedRevit -Probes $probes -Name 'deploy-and-verify' -Year ([string]$Year) -Dir $scratch -RevitExe $revitExe
Say 'Revit started (recorded: pid, start time, executable).'
$deadline = (Get-Date).AddMinutes($RevitStartTimeoutMinutes)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    $h2 = Call 'horizun_health' @{} 'poll'
    $result = Field $h2 'result'
    # NOT $commit: PowerShell is case-insensitive, so that name IS the $Commit
    # parameter. $expected was resolved from it at line 104 and the comparison
    # below is sound today - but naming the OBSERVED value after the EXPECTED
    # one leaves the check one refactor away from comparing a value to itself.
    $observedCommit = [string](Field $result 'horizun_commit')
    if ($observedCommit) {
        Say ("  health: {0} commit={1}" -f [string](Field $result 'status'), $observedCommit.Substring(0, 12))
    }
    # BOTH conditions. An old build reports healthy too.
    if ([string](Field $result 'status') -eq 'healthy' -and $observedCommit -eq $expected) { $healthy = $true; break }
}
if (-not $healthy) {
    throw ("health never reported {0} within {1} minutes. If Revit is up, the security dialog may be " +
           "open on another monitor." -f $expected.Substring(0, 12), $RevitStartTimeoutMinutes)
}
Say ("Healthy at {0}" -f $expected.Substring(0, 12))

# ------------------------------------------------------------- 7. the staging
if (-not $OpenDocuments) {
    $OpenDocuments = @('C:\hz-live\HZ_LIVE_A.rvt', 'C:\hz-live\HZ_LIVE_B.rvt', 'C:\hz-live\HZ_WRITE.rvt')
}
$k = 0
foreach ($file in $OpenDocuments) {
    $k++
    # A NEW KEY EVERY TIME. Reusing a key across a deliberate Revit restart once
    # made three opens replay a recorded 'opened' into a Revit with nothing in
    # it, and the harness then reported 57 product failures that were staging.
    $r = Call 'horizun_document_session' @{
        operation = 'open'; file_path = $file; expected_version = [string]$Year
        allow_upgrade = $false; idempotency_key = [guid]::NewGuid().ToString() } "open$k"
    $res = Field $r 'result'
    Say ("  opened {0}: {1} active_verified={2}" -f (Split-Path $file -Leaf),
        [string](Field $res 'status'), [string](Field $res 'active_document_verified'))
    # Registered by the path the bridge publishes, so the NEXT cycle may close it.
    # A document that is not registered keeps this Revit running at that point.
    $reg = Register-HzRecordedDocument -Probes $probes -Name 'deploy-and-verify' -Year ([string]$Year) `
        -ExpectedTitle ([IO.Path]::GetFileNameWithoutExtension($file)) -SourceFile $file
    if (-not $reg.ok) { Say ("  WARNING: not registered, the next cycle will leave this Revit running: " + $reg.why) }
}

# Opening several documents makes the LAST one active. ActiveDocument is an
# explicit staging requirement, not a hint about how the caller should order
# OpenDocuments, so activate it through the same typed open route before the
# health re-read. An already-open document is not reloaded or saved.
$activeMatches = @($OpenDocuments | Where-Object {
    [IO.Path]::GetFileNameWithoutExtension([string]$_) -eq $ActiveDocument
})
if ($activeMatches.Count -ne 1) {
    throw ("ActiveDocument '{0}' must identify exactly one OpenDocuments entry; found {1}." -f
           $ActiveDocument, $activeMatches.Count)
}
$activate = Call 'horizun_document_session' @{
    operation = 'open'; file_path = [string]$activeMatches[0]; expected_version = [string]$Year
    allow_upgrade = $false; idempotency_key = [guid]::NewGuid().ToString() } 'activate-required'
$activateResult = Field $activate 'result'
if ([string](Field $activateResult 'active_document_verified') -ne 'True') {
    throw ("the typed activation of '{0}' did not verify the active document" -f $ActiveDocument)
}

# Do not take the opens' word for it.
$staged = Field (Call 'horizun_health' @{} 'stagecheck') 'result'
$openNow = [int](Field $staged 'open_document_count')
$activeNow = [string](Field (Field $staged 'active_document') 'title')
Say ("Staged: {0} document(s) open, active '{1}'" -f $openNow, $activeNow)
if ($openNow -lt $OpenDocuments.Count -or $activeNow -ne $ActiveDocument) {
    throw ("staging did not take: {0} open, active '{1}'. Refusing to run a harness against a Revit that " +
           "is not staged - every failure would be attributed to the product." -f $openNow, $activeNow)
}

# THE DURABLE RECORD, refreshed from what the bridge actually answered - and
# only NOW, with the documents open.
#
# deploy-both downgrades install-status to deployed_pending_health because at
# that moment nobody has asked the bridge anything. Here somebody has. The
# position is deliberate: live_verified requires an open document, because every
# command in this bridge acts on the active one, and refreshing before the opens
# recorded "pending" on a machine that works - measured, the first time this ran.
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\refresh-install-status.ps1') 2>&1 |
    ForEach-Object { Say "  $_" }

# ------------------------------------------------------------- 8. the harness
if ($SkipHarness -or -not $Harness) {
    Say 'Deployed and staged. No harness was asked for.'
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}

$harnessFile = Join-Path $repo ("scripts\live\" + $Harness + ".ps1")
if (-not (Test-Path -LiteralPath $harnessFile)) {
    $harnessFile = Join-Path $repo ("scripts\" + $Harness + ".ps1")
}
if (-not (Test-Path -LiteralPath $harnessFile)) { throw "no harness named '$Harness'" }

$splat = @{}
foreach ($key in $HarnessArgs.Keys) { $splat[$key] = $HarnessArgs[$key] }
Say ("Running {0}" -f (Split-Path $harnessFile -Leaf))
& pwsh -NoProfile -ExecutionPolicy Bypass -File $harnessFile @splat
$code = $LASTEXITCODE
Say ("CYCLE-EXIT:{0}" -f $code)
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
exit $code
