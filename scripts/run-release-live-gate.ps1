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
if (-not $Manifest) { $Manifest = Join-Path $repo 'dist\stage\manifest.json' }
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
    $output = & pwsh -NoProfile -File $call -Tool $tool -Arguments $argJson `
        -Server $Server -Json $record -TimeoutSec $timeoutSec -Quiet 2>&1
    $code = $LASTEXITCODE
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

function Python-Quote([string]$value) {
    return "u'" + $value.Replace('\', '\\').Replace("'", "\'") + "'"
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

    Stage "opening the disposable release fixture with workset '$closedWorkset' closed"
    $pathLiteral = Python-Quote $releaseModel
    $worksetLiteral = Python-Quote $closedWorkset
    $python = @"
from Autodesk.Revit.DB import BasicFileInfo, DetachFromCentralOption, FilteredWorksetCollector
from Autodesk.Revit.DB import ModelPathUtils, OpenOptions, WorksetConfiguration, WorksetConfigurationOption
from Autodesk.Revit.DB import WorksetId, WorksetKind, WorksharingUtils
from System.Collections.Generic import List

path = $pathLiteral
workset_name = $worksetLiteral
model_path = ModelPathUtils.ConvertUserVisiblePathToModelPath(path)
preview = list(WorksharingUtils.GetUserWorksetInfo(model_path))
matches = [w for w in preview if w.Name == workset_name]
if len(matches) != 1:
    raise Exception("Expected exactly one workset named '%s'; found %s" % (workset_name, len(matches)))

closed_ids = List[WorksetId]()
closed_ids.Add(matches[0].Id)
configuration = WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets)
configuration.Close(closed_ids)
options = OpenOptions()
if BasicFileInfo.Extract(path).IsCentral:
    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets
options.SetOpenWorksetsConfiguration(configuration)
opened = uiapp.OpenAndActivateDocument(model_path, options, False).Document
closed = [{"id": w.Id.IntegerValue, "name": w.Name} for w in FilteredWorksetCollector(opened).OfKind(WorksetKind.UserWorkset) if not w.IsOpen]
if len([w for w in closed if w["name"] == workset_name]) != 1:
    raise Exception("The named workset is not closed after open: %s" % closed)
__output__ = {
    "status": "verified",
    "summary": "Opened the disposable release fixture with the named workset closed",
    "created_ids": [], "modified_ids": [], "deleted_ids": [], "warnings": [],
    "verification": {"checked": True, "evidence": [{"active_title": opened.Title, "closed_worksets": closed}]}
}
"@
    $openClosed = @{
        code = $python
        target_document = $inactiveTitle
        idempotency_key = "release-runner-$runId-open-closed"
    }
    $closedCall = Invoke-HzCall 'horizun_execute_python' $openClosed $OpenTimeoutSec
    $activeReleaseTitle = [string]$closedCall.Answer.result.output.verification.evidence[0].active_title
    if (-not $closedCall.Answer.result.executed -or
        $closedCall.Answer.result.output.verification.checked -ne $true -or
        [string]::IsNullOrWhiteSpace($activeReleaseTitle)) {
        throw "The disposable release fixture did not open with a measured active title."
    }

    $health = (Invoke-HzCall 'horizun_health' @{} 90).Answer.result
    $active = @($health.open_documents | Where-Object { $_.is_active })
    $inactive = @($health.open_documents | Where-Object { $_.title -eq $inactiveTitle -and -not $_.is_active })
    if ($active.Count -ne 1 -or $active[0].title -ne $activeReleaseTitle -or $inactive.Count -ne 1) {
        throw "Fixture state is wrong: '$activeReleaseTitle' must be the one active document and '$inactiveTitle' must be open but inactive."
    }

    Stage 'running the full release gate, including committing write probes (the model is never saved)'
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
}
