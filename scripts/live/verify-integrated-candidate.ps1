#Requires -Version 5.1
<#
  THE INTEGRATED CANDIDATE, LIVE - ONE REVIT, ONE SESSION, ONE LEDGER.

  Two campaigns were merged into one candidate: the multi-layer wall
  decomposition and the Model Doctor. Each had its own live harness, its own
  fixtures and its own idea of what "green" means. Running them separately
  against the merged build would answer the wrong question - whether each still
  works ALONE - when the thing nobody has measured is whether they work
  TOGETHER, in one Revit, over one document, sharing one contract.

  So this opens Revit once and runs everything in a controlled order, recording
  every phase into a single ledger.

  IT REFUSES BEFORE IT MEASURES. The commit, the contract hash, the product
  version, the Revit year and the active document are all demanded first. Any
  one wrong and it exits 2 having measured NOTHING - which is a different
  outcome from failure and says so. A campaign that measured a stale add-in and
  reported a green matrix is the most expensive thing this file could produce,
  and it has happened in this repository before.

  IT DOES NOT COUNT WHAT IT DID NOT MEASURE. Seven statuses, kept apart:

    passed          measured, and correct.
    failed          measured, and wrong. A finding about the product.
    not_assessable  ran, and the answer cannot be trusted - incomplete
                    coverage, a truncated reply, a bucket that did not add up.
    fixture_missing this machine cannot build the state the case needs. Named,
                    never simulated.
    not_applicable  the case does not apply to this candidate at all.
    available       the capability is present and this campaign does not
                    exercise it - honest, and not a pass.
    implemented_not_live_verified
                    code and contract in place, no Revit has confirmed it.

  A denominator is only ever the number of cases LISTED. There is no run of this
  file that can report 100/100 while a central model, a second user, a closed
  workset or an ACC project is missing - those are declared fixture_missing and
  the rate is reported over the whole list, with the gaps named.

  IT NEVER SAVES. No phase saves, synchronises, or closes a document this
  campaign did not open. The Doctor's correction surface is exercised in dry run
  only; its batch sweep opens detached.

  NOT RUN. This file was written without opening Revit.
#>
[CmdletBinding()]
param(
    # The candidate. Mandatory and exact: every number below belongs to one
    # identifiable build or to none.
    [Parameter(Mandatory)][string]$RequireCommit,
    [Parameter(Mandatory)][string]$RequireContractHash,
    [string]$RequireVersion = '1.2.0',
    [string]$RequireRevitYear = '2026',

    # The read fixture the Doctor and the general regression measure.
    [string]$Document = 'HZ_LIVE_A',
    # A second document, open and NOT active, for the not-active refusal probes.
    [string]$InactiveDocument,
    # The write fixture. The P0 diagnostics regression BUILDS deliberate defects,
    # so it must not run against the document being measured.
    [string]$WriteDocument = 'HZ_WRITE',
    # The 55-case wall fixture document.
    [string]$WallDocument,
    # Disposable copies for the Doctor's read-only sweep.
    [string[]]$BatchFixture = @(),

    [string]$ArtifactDir,
    # Phases to run. Default: all of them, in this order.
    [string[]]$Phases = @('regression', 'dimensions', 'planimetry', 'dwg',
                          'structure', 'walls', 'doctor'),
    # Refuse to start unless every phase's fixture is present. OFF by default:
    # a partial run with named gaps is more useful than no run at all.
    [switch]$RequireAllFixtures
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'integrated-candidate' -Document $Document

# =============================================================================
# THE GATE. Five facts, all of them before anything is measured.
# =============================================================================

function Assert-CandidateGate {
    param([Parameter(Mandatory)]$Run)

    $problems = New-Object System.Collections.ArrayList
    $health = $null
    try { $health = Get-HzHealth $Run }
    catch { $null = $problems.Add("horizun_health did not answer: $($_.Exception.Message)") }

    if ($health) {
        $status = [string](Get-HzProp $health 'status')
        if ($status -ne 'healthy') { $null = $problems.Add("health reports '$status', not 'healthy'") }

        $year = [string](Get-HzProp $health 'revit_version')
        if ($year -ne $RequireRevitYear) {
            $null = $problems.Add("this is Revit $year and the candidate is defined against $RequireRevitYear")
        }

        # THE ONE THAT HAS BEEN WRONG BEFORE.
        $commit = [string](Get-HzProp $health 'horizun_commit')
        if (-not $commit) {
            $null = $problems.Add('health reports no commit, so nothing measured here could be attributed to a build')
        }
        elseif ($commit -notlike "$RequireCommit*" -and $RequireCommit -notlike "$commit*") {
            $null = $problems.Add("the running add-in is '$commit' and this campaign is about '$RequireCommit'")
        }

        $version = [string](Get-HzProp $health 'horizun_version')
        if ($version -and $RequireVersion -and $version -notlike "$RequireVersion*") {
            $null = $problems.Add("the running add-in reports version '$version', expected '$RequireVersion'")
        }

        $active = Get-HzProp $health 'active_document'
        $title = if ($active) { [string](Get-HzProp $active 'title') } else { $null }
        if ($title -ne $Document) {
            $null = $problems.Add("the active document is '$title' and the campaign is defined against '$Document'")
        }
    }

    # The contract hash from the SERVER that answered, not from the source tree.
    $identity = Get-HzResource -Run $Run -Uri 'horizun://build/identity' -Label 'build-identity'
    $hash = if ($identity) { [string](Get-HzProp $identity 'contract_hash') } else { $null }
    if (-not $hash) {
        $null = $problems.Add('the server published no contract hash, so the two halves cannot be shown to match')
    }
    elseif ($hash -ne $RequireContractHash) {
        $null = $problems.Add("the server's contract hash is '$hash' and this campaign is about '$RequireContractHash'")
    }

    if ($problems.Count -eq 0) {
        Write-Host ("  GATE OK  commit={0} version={1} revit={2} contract={3} document={4}" -f
            (Limit-HzText $RequireCommit 12), $RequireVersion, $RequireRevitYear,
            (Limit-HzText $RequireContractHash 12), $Document) -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host '  THE CAMPAIGN DID NOT RUN. Nothing was measured:' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }
    Write-Host ''
    Write-Host '  This is a REFUSAL, not a failure. No case ran, so no case passed and' -ForegroundColor Yellow
    Write-Host '  none failed; nothing about this candidate was learned either way.' -ForegroundColor Yellow
    exit 2
}

# =============================================================================
# PHASES. Each is a child harness, run once, its exit code recorded as evidence.
# =============================================================================

<#
  One phase: a child harness, its duration, its exit code and its artifact.

  A phase whose fixture is absent is recorded fixture_missing WITH THE NAME of
  what is missing. It is never skipped silently and never simulated - a
  simulated fixture reported as Revit evidence is the one thing this campaign
  must not produce.
#>
function Invoke-Phase {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Script,
        [hashtable]$Arguments = @{},
        [string]$NeedsFixture,
        [string]$FixtureValue,
        [string]$ActivateDocument,
        [string]$ActivatePath,
        [int[]]$PassExitCodes = @(0)
    )

    if (-not ($Phases -contains $Id)) {
        Add-HzProbe -Run $run -Id $Id -Name $Name -Expected 'requested' `
            -Observed 'this phase was not requested' -Status 'not_applicable'
        return
    }

    $path = Join-Path $PSScriptRoot $Script
    if (-not (Test-Path -LiteralPath $path)) {
        Add-HzProbe -Run $run -Id $Id -Name $Name -Expected "scripts/live/$Script" `
            -Observed 'the harness is not in this tree' -Status 'failed' `
            -Because 'a phase whose harness is missing is a hole in the candidate, not an absent fixture.'
        return
    }

    if ($NeedsFixture -and -not $FixtureValue) {
        Add-HzFixtureMissing -Id $Id -Name $Name -Needs $NeedsFixture
        return
    }

    Write-Host ''
    Write-Host ("  -- {0}: {1}" -f $Id, $Name) -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $path)
    foreach ($k in $Arguments.Keys) {
        $v = $Arguments[$k]
        if ($v -is [switch] -or $v -is [bool]) { if ($v) { $argv += "-$k" } }
        elseif ($v -is [array]) {
            # An empty array is omission, not a switch with no value. Passing
            # `-BatchFixture` alone makes PowerShell fail parameter binding
            # before the child harness can honestly record fixture_missing.
            if ($v.Count -gt 0) { $argv += "-$k"; $argv += @($v) }
        }
        elseif ($null -ne $v -and "$v" -ne '') { $argv += "-$k"; $argv += "$v" }
    }
    $code = 1
    try {
        if ($ActivateDocument) {
            $null = Set-HzActiveDocument -Run $run -Document $ActivateDocument -FilePath $ActivatePath
        }
        & powershell @argv
        $code = $LASTEXITCODE
    }
    finally {
        $clock.Stop()
        if ($ActivateDocument -and $ActivateDocument -ne $Document) {
            $null = Set-HzActiveDocument -Run $run -Document $Document `
                -FilePath (Join-Path 'C:\hz-live' ($Document + '.rvt'))
        }
    }

    $ok = $PassExitCodes -contains $code
    Add-HzProbe -Run $run -Id $Id -Name $Name `
        -Expected ("{0} exits {1}" -f $Script, ($PassExitCodes -join ' or ')) `
        -Observed ("exit {0} after {1:n0}s" -f $code, $clock.Elapsed.TotalSeconds) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ script = $Script; exit_code = $code; seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1) }
}

function Add-HzFixtureMissing {
    # -Observed defaults to the blunt answer, but a case whose fixture is PARTLY
    # present must say so. "Not present on this machine" about a model sitting in
    # C:\hz-live sends the reader looking for a file they already have.
    param([string]$Id, [string]$Name, [string]$Needs, [string]$Observed)
    if ([string]::IsNullOrWhiteSpace($Observed)) {
        $Observed = 'the fixture is not present on this machine'
    }
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Needs `
        -Observed $Observed -Status 'fixture_missing' `
        -Because 'simulating this and reporting it as Revit evidence would be a lie about where the number came from.'
}

# =============================================================================
Assert-CandidateGate -Run $run
# =============================================================================

Write-Host ''
Write-Host '  INTEGRATED CANDIDATE - one Revit, one session' -ForegroundColor Cyan

# The general regression needs a second document open but inactive. Activate it
# once through the typed session command, then restore the measured fixture.
if ($InactiveDocument) {
    $null = Set-HzActiveDocument -Run $run -Document $InactiveDocument `
        -FilePath (Join-Path 'C:\hz-live' ($InactiveDocument + '.rvt'))
    $null = Set-HzActiveDocument -Run $run -Document $Document `
        -FilePath (Join-Path 'C:\hz-live' ($Document + '.rvt'))
}

# ---- 1. The general regression. Dimensions, detail 2D, planimetry, and every
#         refusal the bridge owes, against the read fixture.
Invoke-Phase -Id 'regression' -Name 'the general live regression (verify-live)' `
    -Script '..\verify-live.ps1' `
    -Arguments @{ Year = $RequireRevitYear; Document = $Document; InactiveDocument = $InactiveDocument }

# ---- 2. Dimensions and 2D detail carry their own probes inside verify-live;
#         recorded here so the matrix names them rather than burying them.
Add-HzProbe -Run $run -Id 'dimensions' -Name 'dimensions and 2D detail' `
    -Expected 'exercised by the general regression above' `
    -Observed 'horizun_edit_dimensions, horizun_get_dimension_references and horizun_detail_2d are probed inside verify-live.ps1' `
    -Status $(if ($Phases -contains 'dimensions') { 'available' } else { 'not_applicable' }) `
    -Because 'they have no separate harness; their evidence is the regression phase, and naming them here keeps the matrix honest about where it came from.'

# ---- 3. Planimetry and DWG.
Invoke-Phase -Id 'planimetry' -Name 'planimetry audit and fix' `
    -Script 'verify-dwg-planimetry.ps1' -Arguments @{ Document = $Document }
Invoke-Phase -Id 'dwg' -Name 'the DWG-to-BIM roll-up' `
    -Script 'verify-dwg-all.ps1' -Arguments @{ Document = $Document }

# ---- 4. Structure and reinforcement.
Invoke-Phase -Id 'structure' -Name 'structure and reinforcement' `
    -Script 'verify-structure-all.ps1' -Arguments @{ Document = $Document }

# ---- 5. The 55 wall cases. Its own fixture document, because the matrix builds
#         and converts walls; running it over the read fixture would change the
#         model every later phase is measuring.
Invoke-Phase -Id 'walls' -Name 'the 55-case wall decomposition matrix' `
    -Script 'wallsplit-matrix.ps1' `
    -NeedsFixture ('a wall fixture document built by scripts/live/wallsplit-fixture*.py, named with -WallDocument. ' +
                   'It must NOT be the read fixture: the matrix converts walls, and every later phase would then ' +
                   'be measuring a model this campaign changed.') `
    -FixtureValue $WallDocument `
    -ActivateDocument $WallDocument `
    -ActivatePath (Join-Path 'C:\hz-live' ($WallDocument + '.rvt')) `
    -Arguments @{ Document = $WallDocument }

# ---- 6. The Model Doctor: sections, snapshots, trends, health index,
#         corrections in dry run, prevention as a decision, batch read-only.
Invoke-Phase -Id 'doctor' -Name 'the Model Doctor campaign' `
    -Script 'verify-doctor-campaign.ps1' `
    -Arguments @{
        RequireCommit       = $RequireCommit
        RequireContractHash = $RequireContractHash
        Document            = $Document
        RegressionDocument  = $WriteDocument
        RequireRevitYear    = $RequireRevitYear
        BatchFixture        = $BatchFixture
    }

# =============================================================================
# THE FIXTURES THIS MACHINE CANNOT BUILD. Named, never simulated.
# =============================================================================

Write-Host ''
Write-Host '  -- fixtures that must be declared rather than invented' -ForegroundColor Cyan

Add-HzFixtureMissing -Id 'fx.central' -Name 'ownership on a real workshared model' `
    -Needs ('a CENTRAL model on a share plus a local of it. Ownership does not exist in a non-workshared ' +
            'document, so a single-user fixture cannot produce it - and four zeros from a document that was ' +
            'never workshared is a census that RAN AND FOUND NOTHING, which is the wrong answer rather than a ' +
            'missing one.') `
    -Observed ('a workshared pair DOES exist on this machine (C:\hz-live\HZ_CLOSED.rvt central, ' +
               'HZ_CLOSED_L.rvt local), so the blocker is not the model. No ownership census has ever been ' +
               'run against it, and this campaign does not open it. Until one does, there is no measurement ' +
               'here - which is why this stays fixture_missing rather than becoming a zero.')

Add-HzFixtureMissing -Id 'fx.borrow' -Name 'elements borrowed by a second user' `
    -Needs ('a SECOND Revit user, or a borrow state saved into the central and reproducible. One machine ' +
            'cannot borrow from itself, and a borrow simulated by editing the model is not a borrow - it is ' +
            'the same element in a different state, which is exactly what the check under test tells apart.')

Add-HzFixtureMissing -Id 'fx.workset' -Name 'a closed workset' `
    -Needs ('a central model with a workset closed in the local. Its elements are not in the document at all, ' +
            'which is the point: the tool must report the WORKSET as closed rather than its CONTENTS as absent.') `
    -Observed ('the MODEL half is already here - C:\hz-live\HZ_CLOSED.rvt is a central, HZ_CLOSED_L.rvt is a ' +
               'disposable local of it, and live-fixtures.json names ClosedWorksetDocument. What is missing is ' +
               'ClosedWorksetName: verify-live.ps1 requires BOTH before it runs the probe, so this case is one ' +
               'recorded string away rather than one fixture away.')

Add-HzFixtureMissing -Id 'fx.placement' -Name 'a permitted and a forbidden workset placement' `
    -Needs ('a model with worksets whose permitted placement the profile declares, plus one element correctly ' +
            'placed and one misplaced. A check that never fires and a check that cannot fire look identical; ' +
            'the misplaced element is the only proof the rule works.')

Add-HzFixtureMissing -Id 'fx.acc' -Name 'a real ACC cloud model' `
    -Needs ('an ACC / BIM 360 project with cloud_project_guid and cloud_model_guid AS REVIT KNOWS THEM - not ' +
            'the ids in the ACC web URL, which decode to valid-looking GUIDs that open to "the central model ' +
            'is missing" - plus the region and an entitled account. A downloaded copy cannot stand in: it is a ' +
            'LOCAL model that resembles the cloud one, with different worksharing, different ownership, and no ' +
            'evidence whatsoever about the cloud model state.')

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir

$c = Get-HzCounts $run
$total = $c.passed + $c.failed + $c.unverified + $c.not_covered + $c.fixture_missing + `
         $c.not_assessable + $c.not_applicable + $c.available + $c.implemented_not_live_verified
Write-Host ''
if ($total -ne $run.Probes.Count) {
    # A denominator that does not add up is a defect in THIS campaign, not a
    # finding about the product, and the two must never be reported alike.
    Write-Host ("  BUCKETS DO NOT ADD UP: {0} cases, {1} counted" -f $run.Probes.Count, $total) -ForegroundColor Red
    exit 3
}

Write-Host ("  {0} of {1} cases passed. {2} fixture_missing - see the artifact for what each one needs." -f
    $c.passed, $run.Probes.Count, $c.fixture_missing) -ForegroundColor $(if ($c.failed) { 'Red' } else { 'Yellow' })
Write-Host '  This is NOT a rate over the cases that ran: the denominator is every case listed.' -ForegroundColor DarkGray
if ($c.fixture_missing -gt 0) {
    Write-Host '  A run with any fixture_missing can never be reported as complete.' -ForegroundColor Yellow
}
exit $done.ExitCode
