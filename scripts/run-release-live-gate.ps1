#Requires -Version 5.1
<#
  Own one complete release-gate run against one installed Revit generation.

  The GitHub runner is interactive on purpose: Revit cannot load an add-in or
  service its UI-thread API from Windows Session 0. This script supplies the
  missing lifecycle that a runner alone does not provide:

    * refuse to compete with a Revit somebody already has open;
    * start the exact Revit year with an isolated Horizun data root;
    * open an inactive fixture, then a disposable workshared fixture with one
      named workset closed;
    * leave that disposable fixture active and run BOTH the non-writing and the
      committing tiers in verify-live.ps1;
    * never save, and stop only the Revit process this invocation started.

  Fixture paths live outside the repository. See
  docs/release-runner-fixtures.example.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(2023,2024,2025,2026,2027)]
    [int]$Year,

    [string]$Fixtures = (Join-Path $env:USERPROFILE '.horizun\release-runner-fixtures.json'),
    [string]$Manifest,
    [string]$Server,
    [string]$Json,
    [int]$StartupTimeoutSec = 600,
    [int]$OpenTimeoutSec = 900,
    [switch]$ValidateFixturesOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
# THE MANIFEST THIS GATE MEASURES AGAINST IS THE INSTALLED ONE.
#
# It used to default to dist\stage\manifest.json, and after a source install
# that comparison can never pass. Two independent reasons, either one enough:
# install.ps1 builds into its OWN temporary staging root and never reads
# dist\stage, so the two are separate builds of the same commit; and install.ps1
# SIGNS what it installs, which rewrites every binary after its hash was
# recorded. The staged manifest therefore describes unsigned bytes that are not
# the ones Revit will load, and the gate reported two failures for a correct
# install.
#
# The installed manifest is what the live run is actually about: verify-install
# proves it matches the bytes on disk, and its Commit is what pairs the halves.
# dist\stage remains the fallback for a release-artifact run, where the staged
# payload IS what gets installed.
if (-not $Manifest) {
    $installedManifest = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\manifest.json'
    $Manifest = if (Test-Path -LiteralPath $installedManifest) { $installedManifest }
                else { Join-Path $repo 'dist\stage\manifest.json' }
}
if (-not $Server) { $Server = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
if (-not $Json) { $Json = Join-Path $repo ("artifacts\live\live-{0}.json" -f $Year) }

function Stage([string]$message) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $message) -ForegroundColor Cyan
}

function Require-Text($object, [string]$name, [string]$where) {
    $value = $object.$name
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        throw "$where must define a non-empty '$name'."
    }
    return [string]$value
}

function Require-File([string]$path, [string]$name) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$name does not exist: $path"
    }
    return (Resolve-Path -LiteralPath $path).Path
}

function Invoke-HzCall([string]$tool, $arguments, [int]$timeoutSec, [switch]$PermitFailure) {
    $call = Join-Path $repo 'scripts\hz-call.ps1'
    $record = Join-Path $script:runRoot ("call-{0}-{1}.json" -f $tool, [guid]::NewGuid().ToString('N'))
    $argJson = $arguments | ConvertTo-Json -Depth 20 -Compress
    $argPath = "$record.arguments.json"
    [IO.File]::WriteAllText($argPath, $argJson, [Text.UTF8Encoding]::new($false))
    # A health probe is expected to fail while Revit is still starting. Under
    # Windows PowerShell 5.1, native stderr redirected with 2>&1 becomes an
    # ErrorRecord and `$ErrorActionPreference = 'Stop'` would abort this runner
    # before PermitFailure can inspect the exit code. Capture it under Continue
    # and restore the fail-closed policy immediately afterward.
    $oldErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & pwsh -NoProfile -File $call -Tool $tool -ArgumentsPath $argPath `
            -Server $Server -Json $record -TimeoutSec $timeoutSec -Quiet 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }
    if ($output) { $output | ForEach-Object { Write-Host $_ } }
    $answer = if (Test-Path -LiteralPath $record) { Get-Content $record -Raw | ConvertFrom-Json } else { $null }
    if ($code -ne 0 -and -not $PermitFailure) {
        $why = if ($answer -and $answer.raw) { $answer.raw } else { "hz-call exit $code" }
        throw "$tool failed: $why"
    }
    return [pscustomobject]@{ ExitCode = $code; Answer = $answer }
}

function Wait-ForHealth([datetime]$deadline) {
    while ((Get-Date) -lt $deadline) {
        $call = Invoke-HzCall 'horizun_health' @{} 60 -PermitFailure
        if ($call.ExitCode -eq 0 -and $call.Answer -and
            $call.Answer.result.status -eq 'healthy' -and
            [int]$call.Answer.result.revit_version -eq $Year) {
            return $call.Answer.result
        }
        Start-Sleep -Seconds 10
    }
    throw "Revit $Year did not publish a healthy Horizun bridge within $StartupTimeoutSec seconds."
}

if (-not (Test-Path -LiteralPath $Fixtures -PathType Leaf)) {
    throw "Release-runner fixtures are missing: $Fixtures. Copy docs/release-runner-fixtures.example.json outside the repository and fill it in."
}
try { $fixtureDoc = Get-Content $Fixtures -Raw | ConvertFrom-Json }
catch { throw "Release-runner fixtures are not valid JSON: $($_.Exception.Message)" }

$common = $fixtureDoc.common
if (-not $common) { throw "$Fixtures has no 'common' object." }
$yearConfig = $fixtureDoc.years."$Year"
if (-not $yearConfig) { throw "$Fixtures has no years.$Year object." }

$releaseModel = Require-File (Require-Text $yearConfig 'release_model' "years.$Year") 'release_model'
$releaseTitle = Require-Text $yearConfig 'release_title' "years.$Year"
$inactiveModel = Require-File (Require-Text $yearConfig 'inactive_model' "years.$Year") 'inactive_model'
$inactiveTitle = Require-Text $yearConfig 'inactive_title' "years.$Year"
$closedWorkset = Require-Text $yearConfig 'closed_workset' "years.$Year"
$spfPath = Require-File (Require-Text $common 'spf_path' 'common') 'spf_path'
$spfParam = Require-Text $common 'spf_param' 'common'
$quantityCategory = Require-Text $common 'quantity_category' 'common'
$disposable = Require-Text $common 'write_document_disposable' 'common'
if ($disposable -ne 'yes-this-model-is-disposable') {
    throw "common.write_document_disposable must be exactly 'yes-this-model-is-disposable'."
}
$oldCandidate = [string]$yearConfig.old_file
if ([string]::IsNullOrWhiteSpace($oldCandidate)) { $oldCandidate = Require-Text $common 'old_file' 'common' }
$oldFile = Require-File $oldCandidate 'old_file'

if ($releaseModel -eq $inactiveModel) { throw 'release_model and inactive_model must be different files.' }
if ($releaseTitle -eq $inactiveTitle) { throw 'release_title and inactive_title must be different document titles.' }

$revitExe = Require-File ("C:\Program Files\Autodesk\Revit {0}\Revit.exe" -f $Year) "Revit $Year"
if ($ValidateFixturesOnly) {
    Stage ("fixtures valid for Revit {0}: active/write='{1}', inactive='{2}', closed workset='{3}'" -f `
           $Year, $releaseTitle, $inactiveTitle, $closedWorkset)
    exit 0
}

$null = Require-File $Manifest 'release manifest'
$null = Require-File $Server 'installed MCP server'
try { $manifestDoc = Get-Content $Manifest -Raw | ConvertFrom-Json }
catch { throw "Release manifest is not valid JSON: $($_.Exception.Message)" }
$addin = $manifestDoc.Plugins | Where-Object { [int]$_.Year -eq $Year } | Select-Object -First 1
if (-not $addin) { throw "The release manifest has no Revit $Year payload." }
if ([string]::IsNullOrWhiteSpace([string]$manifestDoc.Commit) -or
    [string]::IsNullOrWhiteSpace([string]$manifestDoc.Server.Sha256) -or
    [string]::IsNullOrWhiteSpace([string]$addin.Sha256)) {
    throw 'The release manifest lacks commit/server/add-in provenance.'
}

$preexisting = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($preexisting.Count -gt 0) {
    throw ("REFUSING: {0} Revit process(es) were already open (pids {1}). Close them yourself; this gate never kills somebody else's session." -f `
           $preexisting.Count, (($preexisting | ForEach-Object { $_.Id }) -join ', '))
}

$tempBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$runId = "{0}-{1}-{2}" -f $Year, ([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')), ([guid]::NewGuid().ToString('N').Substring(0,8))
$script:runRoot = Join-Path $tempBase "horizun-release-live-$runId"
New-Item -ItemType Directory -Force -Path $script:runRoot | Out-Null
# A COPY of the inactive fixture, for the link-refusal probe. The inactive model
# itself is OPEN in the gate's Revit, and Revit refuses to link an open
# document's file - so the harness gets a same-year copy that nothing has open.
# It lives under runRoot and leaves with it.
$linkSource = Join-Path $script:runRoot ("HZ_LINKSRC_{0}.rvt" -f $Year)
Copy-Item -Path $inactiveModel -Destination $linkSource -Force
# The bridge deliberately rejects LocalApplicationData as shared state: packaged
# and elevated processes can resolve it to different folders. RUNNER_TEMP lives
# there on Windows, so keep call transcripts in it but put the shared add-in /
# server root under the stable user profile.
$dataRoot = Join-Path $env:USERPROFILE ".horizun\release-runs\$runId"
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
'{"permission_profile":"unsafe_code","enable_execute_python":true}' |
    Set-Content (Join-Path $dataRoot 'settings.json') -Encoding utf8

$oldDataRoot = $env:HORIZUN_DATA_ROOT
$oldTargetYear = $env:HORIZUN_REVIT_YEAR
$env:HORIZUN_DATA_ROOT = $dataRoot
$env:HORIZUN_REVIT_YEAR = "$Year"
$launched = $null
$ownedPid = $null

try {
    Stage "starting Revit $Year in this interactive Windows session"
    $launched = Start-Process -FilePath $revitExe -PassThru
    $ownedPid = $launched.Id

    $health = Wait-ForHealth (Get-Date).AddSeconds($StartupTimeoutSec)
    if ([int]$health.process_id -ne $ownedPid) {
        throw "The bridge answered from pid $($health.process_id), but this gate launched pid $ownedPid. Refusing to guess."
    }
    Stage "bridge healthy on pid $ownedPid; opening the inactive fixture first"

    $openInactive = @{
        path = $inactiveModel
        expected_version = "$Year"
        on_open_dialog = 'dismiss'
        idempotency_key = "release-runner-$runId-open-inactive"
    }
    $inactiveCall = Invoke-HzCall 'horizun_open_document' $openInactive $OpenTimeoutSec
    if (-not $inactiveCall.Answer.result.opened -or
        -not $inactiveCall.Answer.result.confirmed_active -or
        $inactiveCall.Answer.result.active_document -ne $inactiveTitle) {
        throw "The inactive fixture did not open as '$inactiveTitle'."
    }

    # Keep two documents open so the suite can prove the inactive-document
    # refusal. Open the disposable release source normally (all worksets) and
    # retain its stable absolute path; verify-live then copies it, opens that
    # copy through typed document_session with one workset closed, restores this
    # source, and closes the copy without saving. The former Python detach hid
    # the source path as a relative *_detached.rvt name and made exact cleanup
    # impossible.
    Stage "opening the disposable release source with every workset loaded"
    $inspectRelease = Invoke-HzCall 'horizun_document_session' @{
        operation = 'inspect'; file_path = $releaseModel
    } 90
    $openRelease = @{
        operation = 'open'; file_path = $releaseModel; expected_version = "$Year"
        allow_upgrade = $false; open_all_worksets = $true
        idempotency_key = "release-runner-$runId-open-release"
    }
    if ($inspectRelease.Answer.result.file.is_central -eq $true) {
        # The fixtures file carries the explicit disposable opt-in. This model
        # is never synchronized or saved; opening its central directly keeps the
        # path stable so the harness can create its own temporary copy.
        $openRelease.open_central = $true
    }
    $releaseCall = Invoke-HzCall 'horizun_document_session' $openRelease $OpenTimeoutSec
    $activeReleaseTitle = [string]$releaseCall.Answer.result.title
    if ($releaseCall.Answer.result.active_document_verified -ne $true -or
        [string]::IsNullOrWhiteSpace($activeReleaseTitle) -or
        $releaseCall.Answer.result.workset_configuration_satisfied -ne $true -or
        [int]$releaseCall.Answer.result.workset_configuration_evidence.worksets_closed -ne 0) {
        throw 'The disposable release source did not open active with measured all-worksets evidence.'
    }

    Stage "running the full release gate; it will open '$releaseTitle' with workset '$closedWorkset' closed"
    $verify = Join-Path $repo 'scripts\verify-live.ps1'
    $verifyArgs = @(
        '-NoProfile', '-File', $verify,
        '-Year', "$Year",
        '-Server', $Server,
        '-Fixtures', $Fixtures,
        '-Document', $activeReleaseTitle,
        '-InactiveDocument', $inactiveTitle,
        '-SpfPath', $spfPath,
        '-SpfParam', $spfParam,
        '-QuantityCategory', $quantityCategory,
        '-OldFile', $oldFile,
        '-ClosedWorksetDocument', $activeReleaseTitle,
        '-ClosedWorksetName', $closedWorkset,
        '-LinkSourceFile', $linkSource,
        '-WriteDocument', $activeReleaseTitle,
        '-WriteDocumentDisposable', $disposable,
        '-ExpectedCommit', [string]$manifestDoc.Commit,
        '-ExpectedServerSha256', [string]$manifestDoc.Server.Sha256,
        '-ExpectedAddinSha256', [string]$addin.Sha256,
        '-Json', $Json,
        '-ReleaseGate', '-WriteProbes'
    )
    & pwsh @verifyArgs
    $verifyExit = $LASTEXITCODE
    if ($verifyExit -ne 0) { throw "verify-live.ps1 failed with exit $verifyExit; the JSON report was preserved at $Json." }

    $report = Get-Content $Json -Raw | ConvertFrom-Json
    if (-not $report.release_gate -or -not $report.write_tier.requested -or
        $report.summary.failed -ne 0 -or $report.summary.unverified -ne 0 -or
        $report.summary.not_covered -ne 0) {
        throw "The live report is not a complete green release gate: $($report.summary | ConvertTo-Json -Compress)"
    }
    # STRUCTURAL COHERENCE, checked where the verdict is consumed. A report whose
    # summary and probe rows disagree (probes=112 beside 114 rows was shipped
    # once) is not evidence of anything; verify-live now refuses to write one,
    # and this gate refuses to accept one, so a drift needs BOTH seats broken.
    $reportRows = @($report.probes)
    if ([int]$report.summary.probes -ne $reportRows.Count) {
        throw ("The live report disagrees with itself: summary.probes={0} but the report carries {1} probe rows." -f `
               $report.summary.probes, $reportRows.Count)
    }
    $rowsNotCovered = @($reportRows | Where-Object { $_.outcome -eq 'not_covered' }).Count
    if (([int]$report.summary.passed + [int]$report.summary.failed + [int]$report.summary.unverified + $rowsNotCovered) -ne $reportRows.Count) {
        throw ("The live report disagrees with itself: passed({0}) + failed({1}) + unverified({2}) + not_covered rows({3}) != {4} rows." -f `
               $report.summary.passed, $report.summary.failed, $report.summary.unverified, $rowsNotCovered, $reportRows.Count)
    }
    Stage ("GREEN Revit {0}: {1} passed, including {2} committing probes" -f `
           $Year, $report.summary.passed, $report.write_tier.probes)
}
finally {
    if ($ownedPid) {
        $owned = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if ($owned -and $owned.ProcessName -eq 'Revit') {
            Stage "stopping only the Revit process launched by this gate (pid $ownedPid); no model is saved"
            Stop-Process -Id $ownedPid -Force -ErrorAction SilentlyContinue
            try { $owned.WaitForExit(30000) | Out-Null } catch { }
        }
    }
    $env:HORIZUN_DATA_ROOT = $oldDataRoot
    $env:HORIZUN_REVIT_YEAR = $oldTargetYear
    # The link-source copy exists only so the harness could stage a link; unlike
    # the call transcripts, a copied fixture model is not evidence and does not
    # stay behind.
    if ($linkSource -and (Test-Path $linkSource)) {
        Remove-Item $linkSource -Force -ErrorAction SilentlyContinue
    }
}
