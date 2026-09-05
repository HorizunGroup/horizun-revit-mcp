#Requires -Version 5.1
<#
  THE PRE-DELIVERY GATE ON THE BRIDGE'S OWN OPERATIONS, PROVED ON A REAL REVIT.

  require_gate is an OPTIONAL argument on horizun_save_document and
  horizun_export. With it, horizun_audit_model's checks run on the document as it
  stands, the caller's profile is evaluated by the audit's own evaluator, and one
  of four decisions is recorded: allowed, blocked, overridden, not_assessable.
  blocked and not_assessable REFUSE BEFORE THE FILE IS TOUCHED.

  WHY THIS FILE EXISTS. Until it did, the save path had been measured live and
  the export path had been READ. A gate that has only been read is a gate whose
  refusal has never been checked against the disk - and the whole claim of a
  prevention gate is a claim about the disk. So every refusal here is proved the
  only way a refusal can be:

    THE FILE IS HASHED BEFORE AND AFTER. A reply that says "refused" and an
    exporter that ran anyway produce the same reply. A blocked export must leave
    NO file where there was none, and leave a file that was already there
    byte-for-byte identical. Checking the reply is not checking the gate.

    AN EXPIRED OVERRIDE IS TESTED WITH A REAL CLOCK. The expiry used to be
    compared against a now_utc the CALLER supplied, in the same object as the
    override it judged - so an override that expired in March, plus a
    now_utc in February, was an override in date. This harness sends exactly
    that pair and requires a refusal.

    NOT ASSESSABLE IS PRODUCED, NOT DESCRIBED. It has never been seen live. Here
    a requirement is declared over a part of the model nobody measured, nothing
    fails, and the gate must answer not_assessable with a reason that LEADS with
    the coverage problem - because a team that reads it as a fail stops passing
    require_gate on workshared models, and the gate is then gone.

    WITHOUT require_gate NOTHING CHANGES. The opt-in has to stay an opt-in: the
    same export runs, the same file lands, and the reply carries no prevention
    block at all.

  WHAT THIS DELIBERATELY DOES NOT DO. It does not intercept Ctrl+S, a manual
  synchronize, Revit's close, or another add-in's export - and one probe reads
  the SOURCE to prove no Revit event subscription was added, because the cheapest
  way to "fix" a gap in a gate is to subscribe to DocumentSaving and cancel other
  people's saves. Every gated reply names those paths in prevention.not_interceptable.

  EVERY OUTPUT IS DISPOSABLE and lives under this run's own temp directory.
  Nothing is written next to a model.

  Exit 0 when every probe passed, 1 when one failed, 2 when the gate refused with
  nothing run, 3 when the buckets do not add up.
#>
[CmdletBinding()]
param(
    # THE BUILD THESE NUMBERS BELONG TO. Mandatory and exact: a green matrix over
    # an add-in three commits old is indistinguishable from a green matrix.
    [Parameter(Mandatory)][string]$RequireCommit,
    [Parameter(Mandatory)][string]$RequireContractHash,
    # The DISPOSABLE write fixture. Two probes save it, which is what it is for.
    [string]$Document = 'HZ_WRITE',
    [string]$RequireRevitYear = '2026',
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'prevention-gate' -Document $Document

# Every file this run produces. Under the run's temp directory, never beside a model.
$OutDir = Join-Path $run.WorkDir 'gate-outputs'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$PROFILE_NAME = 'horizun-live-prevention-gate'
$PROFILE_VERSION = 'v1'

# =============================================================================
# THE GATE. Nothing below runs until all of these are true.
# =============================================================================

function Assert-HzGateCampaign {
    param([Parameter(Mandatory)]$Run)

    $problems = New-Object System.Collections.ArrayList
    $health = $null
    try { $health = Get-HzHealth $Run } catch { $null = $problems.Add("horizun_health did not answer: $($_.Exception.Message)") }

    if ($health) {
        $status = [string](Get-HzProp $health 'status')
        if ($status -ne 'healthy') { $null = $problems.Add("health reports status '$status', not 'healthy'") }

        $year = [string](Get-HzProp $health 'revit_version')
        if ($year -ne $RequireRevitYear) {
            $null = $problems.Add("this is Revit $year and the run is defined against $RequireRevitYear")
        }

        $commit = [string](Get-HzProp $health 'horizun_commit')
        if (-not $commit) { $null = $problems.Add('health reports no commit, so nothing here could be attributed to a build') }
        elseif ($commit -notlike "$RequireCommit*" -and $RequireCommit -notlike "$commit*") {
            $null = $problems.Add("the running add-in is '$commit' and this run is about '$RequireCommit'")
        }

        $active = Get-HzProp $health 'active_document'
        $title = if ($active) { [string](Get-HzProp $active 'title') } else { $null }
        if ($title -ne $Document) {
            $null = $problems.Add("the active document is '$title' and this run is defined against '$Document'")
        }
    }

    $identity = Get-HzResource -Run $Run -Uri 'horizun://build/identity' -Label 'build-identity'
    $hash = if ($identity) { [string](Get-HzProp $identity 'contract_hash') } else { $null }
    if (-not $hash) { $null = $problems.Add('the server published no contract hash, so the two halves cannot be shown to match') }
    elseif ($hash -ne $RequireContractHash) {
        $null = $problems.Add("the server's contract hash is '$hash' and this run is about '$RequireContractHash'")
    }

    if ($problems.Count -eq 0) {
        Write-Host ("  GATE OK  commit={0} revit={1} document={2}" -f
            (Limit-HzText $RequireCommit 12), $RequireRevitYear, $Document) -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host '  THE RUN DID NOT START. Nothing was measured:' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }
    Write-Host ''
    Write-Host '  This is a refusal, not a failure. No probe ran, so no probe passed' -ForegroundColor Yellow
    Write-Host '  and none failed; nothing about the product was learned either way.' -ForegroundColor Yellow
    exit 2
}

# =============================================================================
# HELPERS
# =============================================================================

<#
  The prevention block of a reply, whether the call succeeded or refused. On a
  refusal the gate's detail is spread flat into structuredContent, so the same
  property name answers on both - which is the point: a harness that reads the
  decision one way on success and another on refusal will one day read a refusal
  as an absent gate.
#>
function Get-HzPrevention {
    param($Call)
    if ($null -eq $Call) { return $null }
    $p = Get-HzProp $Call.Result 'prevention'
    if ($p) { return $p }
    # A last resort for a reply whose structured channel was empty: the text block
    # legally carries the same JSON. Never silently: $null stays $null.
    if ($Call.Text) {
        try {
            $parsed = $Call.Text | ConvertFrom-Json
            return (Get-HzProp $parsed 'prevention')
        } catch { return $null }
    }
    $null
}

function Get-HzDecision {
    param($Call)
    $p = Get-HzPrevention $Call
    if ($null -eq $p) { return $null }
    [string](Get-HzProp $p 'decision')
}

<#
  A requirement set as an object the bridge will accept. Kept in one place so a
  profile in this file can never differ from a profile in it by a typo.
#>
function New-HzGateProfile {
    param([Parameter(Mandatory)][hashtable]$Requirements, [string]$Version = $PROFILE_VERSION)
    @{ name = $PROFILE_NAME; version = $Version; requirements = $Requirements }
}

function New-HzRequireGate {
    param(
        [Parameter(Mandatory)][hashtable]$Requirements,
        [string]$Version = $PROFILE_VERSION,
        [hashtable]$Override,
        [string]$NowUtc,
        [string]$DocumentFingerprint
    )
    $g = @{ profile = (New-HzGateProfile -Requirements $Requirements -Version $Version) }
    if ($Override) { $g['override'] = $Override }
    if ($NowUtc) { $g['now_utc'] = $NowUtc }
    if ($DocumentFingerprint) { $g['document_fingerprint'] = $DocumentFingerprint }
    $g
}

function New-HzOverride {
    param(
        [Parameter(Mandatory)][string[]]$Findings,
        [Parameter(Mandatory)][string]$Operation,
        [string]$ProfileVersion = $PROFILE_VERSION,
        [string]$ExpiresUtc
    )
    $o = @{
        identity = 'horizun.live.harness'
        reason = 'the live harness is proving the override path; nothing here is a delivery decision'
        timestamp_utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        operation = $Operation
        profile_version = $ProfileVersion
        findings_ignored = @($Findings)
    }
    if ($ExpiresUtc) { $o['expires_utc'] = $ExpiresUtc }
    $o
}

function Get-HzUtcStamp {
    param([double]$OffsetSeconds = 0)
    (Get-Date).ToUniversalTime().AddSeconds($OffsetSeconds).ToString('yyyy-MM-ddTHH:mm:ssZ')
}

<#
  A file's sha256, or the explicit fact that it is not there. $null and "absent"
  are different answers and a probe that conflates them cannot tell "the export
  was refused" from "the export wrote nothing readable".
#>
function Get-HzFileState {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Exists = $false; Sha256 = $null; Length = $null }
    }
    $fi = Get-Item -LiteralPath $Path
    [pscustomobject]@{ Exists = $true; Sha256 = (Get-HzSha256 $Path); Length = $fi.Length }
}

function Add-HzFixtureMissing {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][string]$Needs)
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Needs `
        -Observed 'the input this needs is absent, so nothing was measured' -Status 'fixture_missing'
}

<#
  EVERY GATED REPLY NAMES WHAT IT CANNOT REACH. Called on each decision this run
  produces, because "the gate covers Revit's Save button" is the single wrong
  belief this field exists to prevent, and one reply carrying it proves nothing
  about the others.
#>
$script:NotInterceptableSeen = New-Object System.Collections.ArrayList
function Assert-HzNotInterceptable {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Where, $Call)
    $p = Get-HzPrevention $Call
    $paths = @()
    if ($p) { $paths = @(Get-HzProp $p 'not_interceptable' | ForEach-Object { [string]$_.path }) }
    $required = @('revit_ui_save', 'synchronize_with_central')
    $missing = @($required | Where-Object { $paths -notcontains $_ })
    $ok = ($paths.Count -gt 0) -and ($missing.Count -eq 0)
    $null = $script:NotInterceptableSeen.Add([ordered]@{ where = $Where; paths = $paths; ok = $ok })
    Add-HzProbe -Run $run -Id $Id -Name "the $Where reply names the paths this gate does not reach" `
        -Expected "not_interceptable names at least revit_ui_save and synchronize_with_central" `
        -Observed $(if ($paths.Count -eq 0) { 'the reply carried no not_interceptable at all' }
                    else { 'paths: ' + ($paths -join ', ') }) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ paths = $paths; missing = $missing }
}

# =============================================================================
Assert-HzGateCampaign -Run $run

# -----------------------------------------------------------------------------
# PHASE 1 - MEASURE THE MODEL, THEN BUILD THE PROFILES FROM WHAT IT SAID.
#
# A profile written into this file would be a guess about somebody else's
# fixture: a limit that happens to pass on this machine and fails on the next
# turns a harness into a source of false findings. So the audit is asked what it
# measures HERE, and the satisfiable and the failing profile are both derived
# from that number - the same number, one above it and one below.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 1 - what this document actually measures' -ForegroundColor Cyan

# Requirements whose measurement is a plain count, in the order they are most
# likely to be non-zero in an ordinary model. Every one is a name the audit's own
# grammar publishes; an unknown one would refuse the whole gate.
$CANDIDATES = @(
    'max_views_off_sheets', 'max_views_without_template', 'max_levels_without_elements',
    'max_levels_without_views', 'max_warnings', 'max_in_place_families',
    'max_unpinned_links', 'max_open_mep_connectors'
)

$probeSet = @{}
foreach ($c in $CANDIDATES) { $probeSet[$c] = 1000000 }

$audit = Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Label 'audit-baseline' -TimeoutSec 900 -Arguments @{
    target_document = $Document; top = 20; requirement_set = $probeSet
}

$measured = @{}
$docFingerprint = $null
$findingSet = $null
if ($audit.Ok) {
    $docFingerprint = [string](Get-HzProp $audit.Result 'document_fingerprint')
    $findingSet = [string](Get-HzProp $audit.Result 'finding_set_fingerprint')
    foreach ($row in @(Get-HzPath $audit.Result 'gate','rows')) {
        $req = [string](Get-HzProp $row 'requirement')
        $status = [string](Get-HzProp $row 'status')
        $value = Get-HzProp $row 'measured'
        if ($status -eq 'pass' -and $null -ne $value) { $measured[$req] = [double]$value }
    }
}
$run.Fixture['document_fingerprint'] = $docFingerprint
$run.Fixture['finding_set_fingerprint'] = $findingSet
$run.Fixture['measured_requirements'] = $measured

# The requirements this run can actually make fail: measured >= 1, so a limit of
# measured-1 is a real failure rather than a limit nobody can be under.
$usable = @($CANDIDATES | Where-Object { $measured.ContainsKey($_) -and $measured[$_] -ge 1 })

Add-HzProbe -Run $run -Id 'G1.1' -Name 'the audit measured this document and published its identity' `
    -Expected 'a document_fingerprint, a finding_set_fingerprint and per-requirement measured values' `
    -Observed ("fingerprint={0} finding_set={1} measured={2} requirement(s)" -f
               (Limit-HzText ([string]$docFingerprint) 16), (Limit-HzText ([string]$findingSet) 16), $measured.Count) `
    -Status $(if ($docFingerprint -and $findingSet -and $measured.Count -gt 0) { 'passed' } else { 'failed' }) `
    -Evidence @{ measured = $measured; usable = $usable; audit_ok = $audit.Ok; reply = (Limit-HzText $audit.Text 500) }

if ($usable.Count -eq 0) {
    Add-HzFixtureMissing -Id 'G1.2' -Name 'a requirement this document can be made to FAIL' `
        -Needs ("a document where at least one of {0} measures 1 or more. Every candidate measured zero or was not measurable, so no failing profile can be built from a real measurement - and a profile invented here would be testing the harness rather than the model." -f ($CANDIDATES -join ', '))
} else {
    Add-HzProbe -Run $run -Id 'G1.2' -Name 'a failing profile can be built from a real measurement' `
        -Expected 'at least one requirement measuring 1 or more' `
        -Observed ("usable: {0}" -f ($usable -join ', ')) -Status 'passed' `
        -Evidence @{ usable = $usable }
}

# The two profiles, and a third that fails TWO requirements so an override can be
# shown to cover exactly what it names and no more.
$PassProfile = $null; $FailProfile = $null; $FailTwoProfile = $null
$FailReq = $null; $FailReqSecond = $null
if ($usable.Count -ge 1) {
    $FailReq = $usable[0]
    $PassProfile = @{}
    foreach ($k in $usable) { $PassProfile[$k] = [int]$measured[$k] }      # limit == measured: pass
    $FailProfile = @{ $FailReq = [int]($measured[$FailReq] - 1) }          # limit below measured: fail
}
if ($usable.Count -ge 2) {
    $FailReqSecond = $usable[1]
    $FailTwoProfile = @{
        $FailReq       = [int]($measured[$FailReq] - 1)
        $FailReqSecond = [int]($measured[$FailReqSecond] - 1)
    }
}

# A requirement over a part of the model nobody declared, so nobody measured it.
# require_4d_roles reads the readiness check's per-item results, and with no
# readiness_roles declared there are none - which is a hole in coverage, not a
# defect, and is exactly what not_assessable is for.
$UnmeasurableProfile = $null
if ($PassProfile) {
    $UnmeasurableProfile = @{}
    foreach ($k in $PassProfile.Keys) { $UnmeasurableProfile[$k] = $PassProfile[$k] }
    $UnmeasurableProfile['require_4d_roles'] = @('hz-role-nobody-declared')
}

$run.Expected['pass_profile'] = $PassProfile
$run.Expected['fail_profile'] = $FailProfile
$run.Expected['fail_two_profile'] = $FailTwoProfile

# -----------------------------------------------------------------------------
# PHASE 2 - THE EXPORT PATH.
#
# The gate is evaluated on the rehearsal AND on the apply, and it sits outside
# the plan hash on purpose - a caller may add it between the two. That is what
# lets a refusal be proved at the moment it matters: the token is taken from an
# UNGATED rehearsal, and the gate is attached to the apply, so what is being
# refused is a real export that was otherwise ready to write.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 2 - the export path' -ForegroundColor Cyan

$viewId = Get-HzFirstFloorPlanId -Run $run
$run.Fixture['export_view_id'] = $viewId

<#
  Rehearse an export WITHOUT a gate and hand back the token. The gate is
  deliberately outside the plan hash, so a token issued here is still spendable
  by an apply that carries one - which is the only way to put a gate in front of
  an export that was otherwise about to run.
#>
function Get-HzExportToken {
    param([Parameter(Mandatory)][string]$OutputPath, [switch]$Overwrite, [Parameter(Mandatory)][string]$Label)
    $a = @{
        target_document = $Document; format = 'dwg'; output_path = $OutputPath
        view_ids = @($viewId); acad_version = '2018'; dry_run = $true
    }
    if ($Overwrite) { $a['overwrite'] = $true }
    $dry = Invoke-HzTool -Run $run -Tool 'horizun_export' -Arguments $a -Label "$Label-rehearse" -TimeoutSec 900
    if (-not $dry.Ok) { return $null }
    [string](Get-HzProp $dry.Result 'confirmation_token')
}

<#
  Apply an export WITH a gate, using a token from an ungated rehearsal. Returns
  the call and the state of the destination before and after, because the reply
  is not the evidence - the disk is.
#>
function Invoke-HzGatedExport {
    param(
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$Label,
        [hashtable]$RequireGate,
        [switch]$Overwrite
    )
    $before = Get-HzFileState $OutputPath
    $token = Get-HzExportToken -OutputPath $OutputPath -Overwrite:$Overwrite -Label $Label
    if (-not $token) {
        return [pscustomobject]@{ Call = $null; Before = $before; After = (Get-HzFileState $OutputPath); Token = $null }
    }
    $a = @{
        target_document = $Document; format = 'dwg'; output_path = $OutputPath
        view_ids = @($viewId); acad_version = '2018'; dry_run = $false
        confirmation_token = $token; idempotency_key = (New-HzKey $run $Label)
    }
    if ($Overwrite) { $a['overwrite'] = $true }
    if ($RequireGate) { $a['require_gate'] = $RequireGate }
    $call = Invoke-HzTool -Run $run -Tool 'horizun_export' -Arguments $a -Label $Label -TimeoutSec 900
    [pscustomobject]@{ Call = $call; Before = $before; After = (Get-HzFileState $OutputPath); Token = $token }
}

if (-not $viewId) {
    Add-HzFixtureMissing -Id 'G2.0' -Name 'the whole export path' `
        -Needs "a non-template FloorPlan view in '$Document' to export. Without one no export can be attempted, and a gate that was never put in front of a real export has not been measured."
}
elseif (-not $PassProfile) {
    Add-HzFixtureMissing -Id 'G2.0' -Name 'the whole export path' `
        -Needs 'a requirement this document measures at 1 or more, so allowed and blocked can both be produced from the same real measurement.'
}
else {

    # ---- G2.1 ALLOWED: the export runs and the file lands. ----------------
    $allowedPath = Join-Path $OutDir 'gate-allowed.dwg'
    $allowed = Invoke-HzGatedExport -OutputPath $allowedPath -Label 'export-allowed' `
        -RequireGate (New-HzRequireGate -Requirements $PassProfile)

    $decision = Get-HzDecision $allowed.Call
    $after = $allowed.After
    # THE BYTES, not just the existence. A DWG begins with its version signature,
    # so a zero-length file or a stray text file cannot pass as an export.
    $signature = $null
    if ($after.Exists -and $after.Length -gt 6) {
        $bytes = [IO.File]::ReadAllBytes($allowedPath)[0..5]
        $signature = -join ($bytes | ForEach-Object { [char]$_ })
    }
    $ok = ($decision -eq 'allowed') -and $after.Exists -and ($after.Length -gt 0) -and ($signature -like 'AC10*')
    Add-HzProbe -Run $run -Id 'G2.1' -Name 'a satisfied profile lets the export run and the file lands' `
        -Expected 'decision=allowed, and a DWG on disk whose first bytes are its version signature' `
        -Observed ("decision={0} exists={1} bytes={2} signature='{3}'" -f $decision, $after.Exists, $after.Length, $signature) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; exists = $after.Exists; length = $after.Length
                     signature = $signature; sha256 = $after.Sha256; reply = (Limit-HzText $allowed.Call.Text 400) }
    Assert-HzNotInterceptable -Id 'G2.2' -Where 'allowed export' -Call $allowed.Call

    # THE CLOCK THE GATE JUDGED AGAINST, published in the reply. A build without
    # the machine-clock fix carries no such block, so this doubles as proof that
    # the add-in answering is the one that was changed.
    $prevention = Get-HzPrevention $allowed.Call
    $clock = if ($prevention) { Get-HzProp $prevention 'clock' } else { $null }
    $reference = if ($clock) { [string](Get-HzProp $clock 'reference_utc') } else { $null }
    $tolerance = if ($clock) { Get-HzProp $clock 'tolerance_seconds' } else { $null }
    Add-HzProbe -Run $run -Id 'G2.3' -Name 'the gated reply says which clock it judged an expiry against' `
        -Expected 'prevention.clock carries a reference_utc, a machine_utc and the tolerance' `
        -Observed $(if ($clock) { "reference=$reference tolerance=$tolerance" } else { 'the reply carried no clock block' }) `
        -Status $(if ($reference -and $tolerance) { 'passed' } else { 'failed' }) `
        -Evidence @{ clock = $clock }

    # ---- G2.4 BLOCKED, where no file existed: none is created. -------------
    $blockedPath = Join-Path $OutDir 'gate-blocked-fresh.dwg'
    $blocked = Invoke-HzGatedExport -OutputPath $blockedPath -Label 'export-blocked' `
        -RequireGate (New-HzRequireGate -Requirements $FailProfile)

    $decision = Get-HzDecision $blocked.Call
    $refused = $blocked.Call -and $blocked.Call.IsError
    $ok = $refused -and ($decision -eq 'blocked') -and (-not $blocked.After.Exists)
    Add-HzProbe -Run $run -Id 'G2.4' -Name 'a failed profile refuses the export and NO file is created' `
        -Expected 'refused with decision=blocked, and nothing at the destination afterwards' `
        -Observed ("refused={0} decision={1} file_exists_after={2}" -f $refused, $decision, $blocked.After.Exists) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; refused = $refused; before = $blocked.Before; after = $blocked.After
                     requirement = $FailReq; limit = $FailProfile[$FailReq]; measured = $measured[$FailReq]
                     reply = (Limit-HzText $blocked.Call.Text 700) }
    Assert-HzNotInterceptable -Id 'G2.5' -Where 'blocked export' -Call $blocked.Call

    # ---- G2.6 BLOCKED over a file that already exists: byte-identical. -----
    #
    # The refusal that matters most. A destination that already holds somebody's
    # deliverable is where an exporter that ran anyway does actual harm, and a
    # reply saying "refused" looks the same either way. Hash before, hash after.
    $existingPath = Join-Path $OutDir 'gate-blocked-existing.dwg'
    $sentinel = "Horizun live harness sentinel " + $run.RunId + " - this content must survive a blocked export."
    Set-Content -LiteralPath $existingPath -Value $sentinel -Encoding ascii
    $sentinelState = Get-HzFileState $existingPath

    $overExisting = Invoke-HzGatedExport -OutputPath $existingPath -Label 'export-blocked-existing' -Overwrite `
        -RequireGate (New-HzRequireGate -Requirements $FailProfile)

    $decision = Get-HzDecision $overExisting.Call
    $refused = $overExisting.Call -and $overExisting.Call.IsError
    $identical = $overExisting.After.Exists -and ($overExisting.After.Sha256 -eq $sentinelState.Sha256)
    $ok = $refused -and ($decision -eq 'blocked') -and $identical
    Add-HzProbe -Run $run -Id 'G2.6' -Name 'a blocked export leaves an existing file byte-identical' `
        -Expected 'refused with decision=blocked, and the sha256 at the destination unchanged' `
        -Observed ("refused={0} decision={1} sha_before={2} sha_after={3}" -f
                   $refused, $decision, (Limit-HzText ([string]$sentinelState.Sha256) 16),
                   (Limit-HzText ([string]$overExisting.After.Sha256) 16)) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; refused = $refused
                     sha256_before = $sentinelState.Sha256; sha256_after = $overExisting.After.Sha256
                     length_before = $sentinelState.Length; length_after = $overExisting.After.Length }

    # ---- G2.7 OVERRIDDEN: a signed statement lets the same export through. --
    $overriddenPath = Join-Path $OutDir 'gate-overridden.dwg'
    $overridden = Invoke-HzGatedExport -OutputPath $overriddenPath -Label 'export-overridden' `
        -RequireGate (New-HzRequireGate -Requirements $FailProfile `
                        -Override (New-HzOverride -Findings @($FailReq) -Operation 'export'))

    $decision = Get-HzDecision $overridden.Call
    $prevention = Get-HzPrevention $overridden.Call
    $accepted = if ($prevention) { [bool](Get-HzProp $prevention 'override_accepted') } else { $false }
    $blockingStands = @()
    if ($prevention) { $blockingStands = @(Get-HzProp $prevention 'blocking_findings') }
    $ok = ($decision -eq 'overridden') -and $accepted -and $overridden.After.Exists -and
          ($overridden.After.Length -gt 0) -and ($blockingStands.Count -ge 1)
    Add-HzProbe -Run $run -Id 'G2.7' -Name 'a valid override runs the export and records it as overridden' `
        -Expected 'decision=overridden, override_accepted=true, the file on disk, and the findings still listed' `
        -Observed ("decision={0} accepted={1} exists={2} bytes={3} findings_still_listed={4}" -f
                   $decision, $accepted, $overridden.After.Exists, $overridden.After.Length, $blockingStands.Count) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; override_accepted = $accepted; blocking_findings = $blockingStands
                     after = $overridden.After }
    Assert-HzNotInterceptable -Id 'G2.8' -Where 'overridden export' -Call $overridden.Call

    # ---- G2.9 - G2.13 OVERRIDES THAT DO NOT COUNT. -------------------------
    #
    # Five ways an override is not permission. Each writes to its own destination
    # and each must leave it empty: a refusal that still exported is the failure
    # this whole section exists to catch.
    $refusals = @(
        @{ Id = 'G2.9';  Name = 'an override signed for another operation is refused'
           File = 'gate-override-wrong-op.dwg'
           Gate = (New-HzRequireGate -Requirements $FailProfile `
                     -Override (New-HzOverride -Findings @($FailReq) -Operation 'save'))
           Match = "signed for 'save'" }
        @{ Id = 'G2.10'; Name = 'an override signed against another profile version is refused'
           File = 'gate-override-wrong-version.dwg'
           Gate = (New-HzRequireGate -Requirements $FailProfile `
                     -Override (New-HzOverride -Findings @($FailReq) -Operation 'export' -ProfileVersion 'v0'))
           Match = 'profile' }
        @{ Id = 'G2.11'; Name = 'an override that expired is refused against a REAL clock, with no now_utc sent'
           File = 'gate-override-expired.dwg'
           Gate = (New-HzRequireGate -Requirements $FailProfile `
                     -Override (New-HzOverride -Findings @($FailReq) -Operation 'export' `
                                  -ExpiresUtc (Get-HzUtcStamp -3600)))
           Match = 'expired' }
        @{ Id = 'G2.12'; Name = 'an expired override plus a convenient now_utc is refused, not revived'
           File = 'gate-override-backdated.dwg'
           Gate = (New-HzRequireGate -Requirements $FailProfile -NowUtc (Get-HzUtcStamp -7200) `
                     -Override (New-HzOverride -Findings @($FailReq) -Operation 'export' `
                                  -ExpiresUtc (Get-HzUtcStamp -3600)))
           Match = 'seconds apart' }
    )
    if ($FailTwoProfile) {
        $refusals += @{
            Id = 'G2.13'; Name = 'an override that does not cover every blocking finding is refused'
            File = 'gate-override-partial.dwg'
            Gate = (New-HzRequireGate -Requirements $FailTwoProfile `
                      -Override (New-HzOverride -Findings @($FailReq) -Operation 'export'))
            Match = 'does not cover'
        }
    }

    foreach ($case in $refusals) {
        $path = Join-Path $OutDir $case.File
        $r = Invoke-HzGatedExport -OutputPath $path -Label ('export-' + $case.Id) -RequireGate $case.Gate
        $decision = Get-HzDecision $r.Call
        $refused = $r.Call -and $r.Call.IsError
        $matched = $refused -and ($r.Call.Text -match [regex]::Escape($case.Match))
        # A refused override reads as blocked; a refused CLOCK reads as
        # not_assessable, because the gate could not establish the time rather
        # than find a defect. Both are refusals and neither may write.
        $decisionOk = $decision -in @('blocked', 'not_assessable')
        $ok = $refused -and $decisionOk -and $matched -and (-not $r.After.Exists)
        Add-HzProbe -Run $run -Id $case.Id -Name $case.Name `
            -Expected ("refused (blocked or not_assessable), the reason contains '{0}', and NO file at the destination" -f $case.Match) `
            -Observed ("refused={0} decision={1} reason_matched={2} file_exists_after={3}" -f
                       $refused, $decision, $matched, $r.After.Exists) `
            -Status $(if ($ok) { 'passed' } else { 'failed' }) `
            -Evidence @{ decision = $decision; refused = $refused; matched = $matched
                         after = $r.After; reply = (Limit-HzText $r.Call.Text 700) }
    }
    if (-not $FailTwoProfile) {
        Add-HzFixtureMissing -Id 'G2.13' -Name 'an override that does not cover every blocking finding' `
            -Needs 'a document where TWO of the candidate requirements measure 1 or more, so a profile can fail twice and an override can name one of the two. With a single failing requirement, "covers what it names and nothing else" cannot be told from "covers everything".'
    }

    # ---- G2.14 NOT ASSESSABLE, produced for real. --------------------------
    #
    # Nothing fails. A requirement is declared over readiness roles this run never
    # declared, so that part of the measurement did not happen. The gate must
    # refuse, must not call it a fail, and must lead with the coverage problem.
    $notAssessablePath = Join-Path $OutDir 'gate-not-assessable.dwg'
    $na = Invoke-HzGatedExport -OutputPath $notAssessablePath -Label 'export-not-assessable' `
        -RequireGate (New-HzRequireGate -Requirements $UnmeasurableProfile)

    $decision = Get-HzDecision $na.Call
    $prevention = Get-HzPrevention $na.Call
    $why = if ($prevention) { [string](Get-HzProp $prevention 'why') } else { [string]$na.Call.Text }
    $coverage = @()
    $blocking = @()
    if ($prevention) {
        $coverage = @(Get-HzProp $prevention 'coverage_problems')
        $blocking = @(Get-HzProp $prevention 'blocking_findings')
    }
    $leads = $why -match '^NOT ASSESSABLE'
    $ok = ($na.Call -and $na.Call.IsError) -and ($decision -eq 'not_assessable') -and $leads -and
          ($coverage.Count -ge 1) -and ($blocking.Count -eq 0) -and (-not $na.After.Exists)
    Add-HzProbe -Run $run -Id 'G2.14' -Name 'an incomplete measurement is not_assessable, refuses, and is not a fail' `
        -Expected 'decision=not_assessable, the reason LEADS with NOT ASSESSABLE, coverage problems listed, NO blocking findings, and no file' `
        -Observed ("decision={0} leads_with_not_assessable={1} coverage_problems={2} blocking_findings={3} file_exists_after={4}" -f
                   $decision, $leads, $coverage.Count, $blocking.Count, $na.After.Exists) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; why = (Limit-HzText $why 600); coverage_problems = $coverage
                     blocking_findings = $blocking; after = $na.After }
    Assert-HzNotInterceptable -Id 'G2.15' -Where 'not-assessable export' -Call $na.Call

    # AND IT IS DISTINGUISHABLE FROM A FAIL. Not a matter of wording: the blocked
    # reply names blocking findings and no coverage problem; this one is the exact
    # opposite, and a reader can tell them apart from the fields alone.
    $blockedPrevention = Get-HzPrevention $blocked.Call
    # @() AROUND THE WHOLE `if`, not only around its result. As an expression the
    # if-block's output goes through the pipeline, and a one-element array is
    # UNROLLED to the element on the way out - so `.Count` on the single blocking
    # finding this run produces threw, while a two-finding reply would have passed.
    # Measured here against a stub, which is the only reason it was not measured in
    # front of Revit.
    $blockedBlocking = @(if ($blockedPrevention) { Get-HzProp $blockedPrevention 'blocking_findings' })
    $blockedCoverage = @(if ($blockedPrevention) { Get-HzProp $blockedPrevention 'coverage_problems' })
    $distinct = ($blockedBlocking.Count -ge 1) -and ($blocking.Count -eq 0) -and ($coverage.Count -ge 1)
    Add-HzProbe -Run $run -Id 'G2.16' -Name 'not_assessable and blocked are told apart by their fields, not their prose' `
        -Expected 'blocked names blocking findings; not_assessable names coverage problems and no blocking finding' `
        -Observed ("blocked: {0} findings / {1} coverage; not_assessable: {2} findings / {3} coverage" -f
                   $blockedBlocking.Count, $blockedCoverage.Count, $blocking.Count, $coverage.Count) `
        -Status $(if ($distinct) { 'passed' } else { 'failed' }) `
        -Evidence @{ blocked_findings = $blockedBlocking; blocked_coverage = $blockedCoverage
                     not_assessable_findings = $blocking; not_assessable_coverage = $coverage }

    # ---- G2.17 NO require_gate: the opt-in stays optional. -----------------
    $plainPath = Join-Path $OutDir 'gate-absent.dwg'
    $plain = Invoke-HzGatedExport -OutputPath $plainPath -Label 'export-ungated'   # no -RequireGate

    $hasPrevention = $null -ne (Get-HzPrevention $plain.Call)
    $succeeded = $plain.Call -and $plain.Call.Ok
    $ok = $succeeded -and $plain.After.Exists -and ($plain.After.Length -gt 0) -and (-not $hasPrevention)
    Add-HzProbe -Run $run -Id 'G2.17' -Name 'without require_gate the export behaves exactly as before' `
        -Expected 'the export runs, the file lands, and the reply carries NO prevention block' `
        -Observed ("ok={0} exists={1} bytes={2} carries_prevention={3}" -f
                   $succeeded, $plain.After.Exists, $plain.After.Length, $hasPrevention) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ ok = $succeeded; after = $plain.After; carries_prevention = $hasPrevention }

    # The same file, the same size class, from the gated and the ungated path: an
    # allowed gate must not change WHAT is exported, only whether it is.
    $sameShape = $plain.After.Exists -and $allowed.After.Exists -and
                 ([Math]::Abs([long]$plain.After.Length - [long]$allowed.After.Length) -le ([long]$allowed.After.Length / 10 + 1024))
    Add-HzProbe -Run $run -Id 'G2.18' -Name 'an allowed gate changes whether the export runs, never what it produces' `
        -Expected 'the gated and ungated DWG of the same view are the same export within 10%' `
        -Observed ("gated={0} bytes, ungated={1} bytes" -f $allowed.After.Length, $plain.After.Length) `
        -Status $(if ($sameShape) { 'passed' } else { 'failed' }) `
        -Evidence @{ gated = $allowed.After; ungated = $plain.After }
}

# -----------------------------------------------------------------------------
# PHASE 3 - THE SAVE PATH.
#
# The same four decisions on the operation that writes the model itself. The
# refusals are proved the same way - the .rvt is hashed before and after - and
# the two that PROCEED do save the fixture, which is what a disposable write
# fixture is for. horizun_save_document reports its own sha256 before and after,
# and both are cross-checked against the file read from disk here: a command that
# reported its own success is not evidence of it.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 3 - the save path' -ForegroundColor Cyan

$health = Get-HzHealth $run
$rvtPath = [string](Get-HzPath $health 'active_document','path')
$run.Fixture['document_path_known'] = [bool]$rvtPath

function Invoke-HzGatedSave {
    param([Parameter(Mandatory)][string]$Label, [hashtable]$RequireGate)
    $before = Get-HzFileState $rvtPath
    # A SAVE IS A MUTATION AND THE BRIDGE ASKS FOR A KEY. Measured 2026-09-03:
    # without idempotency_key every save in this phase was refused BEFORE the gate
    # ran, and the probes read that as "the gate did not decide" - eleven failures
    # that said nothing about the gate at all. A fresh key per label, because two
    # of these calls are meant to write.
    $a = @{ target_document = $Document; idempotency_key = (New-HzKey $run ('gate-save-' + $Label)) }
    if ($RequireGate) { $a['require_gate'] = $RequireGate }
    $call = Invoke-HzTool -Run $run -Tool 'horizun_save_document' -Arguments $a -Label $Label -TimeoutSec 900
    [pscustomobject]@{ Call = $call; Before = $before; After = (Get-HzFileState $rvtPath) }
}

# A READ-ONLY FIXTURE CANNOT BE SAVED, and a save refused by the file system is
# not a gate decision. HZ_WRITE is deliberately read-only on this machine - the
# write tier commits into it and never saves - so the two probes that PROCEED
# need a writable disposable copy of their own.
$rvtWritable = $false
if ($rvtPath -and (Test-Path -LiteralPath $rvtPath)) {
    try { $rvtWritable = -not ((Get-Item -LiteralPath $rvtPath).IsReadOnly) } catch { $rvtWritable = $false }
}
$run.Fixture['document_writable'] = $rvtWritable

if (-not $rvtPath -or -not (Test-Path -LiteralPath $rvtPath)) {
    Add-HzFixtureMissing -Id 'G3.0' -Name 'the whole save path' `
        -Needs "a '$Document' that has been saved to a readable path. A never-saved document is refused by horizun_save_document for its own reasons, and a refusal for the wrong reason proves nothing about the gate."
}
elseif (-not $FailProfile) {
    Add-HzFixtureMissing -Id 'G3.0' -Name 'the whole save path' `
        -Needs 'a requirement this document measures at 1 or more, so a save can be blocked by a real measurement.'
}
else {
    # ---- G3.1 BLOCKED: the .rvt is not touched. ----------------------------
    $s = Invoke-HzGatedSave -Label 'save-blocked' -RequireGate (New-HzRequireGate -Requirements $FailProfile)
    $decision = Get-HzDecision $s.Call
    $refused = $s.Call -and $s.Call.IsError
    $untouched = $s.After.Exists -and ($s.After.Sha256 -eq $s.Before.Sha256)
    $ok = $refused -and ($decision -eq 'blocked') -and $untouched
    Add-HzProbe -Run $run -Id 'G3.1' -Name 'a failed profile refuses the save and the .rvt is byte-identical' `
        -Expected 'refused with decision=blocked, and the same sha256 on disk afterwards' `
        -Observed ("refused={0} decision={1} sha_unchanged={2}" -f $refused, $decision, $untouched) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; sha256_before = $s.Before.Sha256; sha256_after = $s.After.Sha256
                     reply = (Limit-HzText $s.Call.Text 600) }
    Assert-HzNotInterceptable -Id 'G3.2' -Where 'blocked save' -Call $s.Call

    # ---- G3.3 an expired override on the save path, real clock. ------------
    $s = Invoke-HzGatedSave -Label 'save-expired' -RequireGate (New-HzRequireGate -Requirements $FailProfile `
            -Override (New-HzOverride -Findings @($FailReq) -Operation 'save' -ExpiresUtc (Get-HzUtcStamp -3600)))
    $decision = Get-HzDecision $s.Call
    $refused = $s.Call -and $s.Call.IsError
    $untouched = $s.After.Exists -and ($s.After.Sha256 -eq $s.Before.Sha256)
    $matched = $refused -and ($s.Call.Text -match 'expired')
    $ok = $refused -and ($decision -eq 'blocked') -and $matched -and $untouched
    Add-HzProbe -Run $run -Id 'G3.3' -Name 'an expired override refuses the save against the machine clock' `
        -Expected "refused with decision=blocked, the reason contains 'expired', and the .rvt unchanged" `
        -Observed ("refused={0} decision={1} reason_matched={2} sha_unchanged={3}" -f
                   $refused, $decision, $matched, $untouched) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; matched = $matched
                     sha256_before = $s.Before.Sha256; sha256_after = $s.After.Sha256 }

    # ---- G3.4 NOT ASSESSABLE on the save path. -----------------------------
    $s = Invoke-HzGatedSave -Label 'save-not-assessable' `
        -RequireGate (New-HzRequireGate -Requirements $UnmeasurableProfile)
    $decision = Get-HzDecision $s.Call
    $prevention = Get-HzPrevention $s.Call
    $why = if ($prevention) { [string](Get-HzProp $prevention 'why') } else { [string]$s.Call.Text }
    $coverage = @(if ($prevention) { Get-HzProp $prevention 'coverage_problems' })
    $untouched = $s.After.Exists -and ($s.After.Sha256 -eq $s.Before.Sha256)
    $ok = ($s.Call -and $s.Call.IsError) -and ($decision -eq 'not_assessable') -and
          ($why -match '^NOT ASSESSABLE') -and ($coverage.Count -ge 1) -and $untouched
    Add-HzProbe -Run $run -Id 'G3.4' -Name 'an incomplete measurement refuses the save as not_assessable' `
        -Expected 'decision=not_assessable, leading with the coverage problem, and the .rvt unchanged' `
        -Observed ("decision={0} coverage_problems={1} sha_unchanged={2}" -f $decision, $coverage.Count, $untouched) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; why = (Limit-HzText $why 600); coverage_problems = $coverage }
    Assert-HzNotInterceptable -Id 'G3.5' -Where 'not-assessable save' -Call $s.Call

    # ---- G3.6, G3.8, G3.10: THE THREE THAT WRITE. --------------------------
    #
    # A save refused by the FILE SYSTEM is not a gate decision, and reading one as
    # the other is how a run reports eleven failures that say nothing about the
    # gate. HZ_WRITE is read-only on the machine this was written for - the write
    # tier commits into it and never saves - so these three need a writable
    # disposable copy and say so when they do not have one.
    if (-not $rvtWritable) {
        foreach ($p in @(
            @{ Id = 'G3.6'; Name = 'a valid override lets the save proceed and records it as overridden' },
            @{ Id = 'G3.7'; Name = 'the overridden save reply names the paths this gate does not reach' },
            @{ Id = 'G3.8'; Name = 'a satisfied profile lets the save run' },
            @{ Id = 'G3.9'; Name = 'the allowed save reply names the paths this gate does not reach' },
            @{ Id = 'G3.10'; Name = 'without require_gate the save behaves exactly as before' })) {
            Add-HzFixtureMissing -Id $p.Id -Name $p.Name `
                -Needs ("a WRITABLE disposable .rvt as the active document. '$Document' is read-only on disk, so " +
                        "every save is refused by the file system before the gate decides, and a refusal for the " +
                        "wrong reason proves nothing. Copy it, clear the read-only attribute, open that copy, and " +
                        "run this again with -Document <that title>.")
        }
    }
    else {

    # This one writes. It is the disposable write fixture and the point of the
    # probe is that the gate does not merely refuse: an override that names the
    # failing findings lets a real save through, and the findings still stand in
    # the reply.
    $s = Invoke-HzGatedSave -Label 'save-overridden' -RequireGate (New-HzRequireGate -Requirements $FailProfile `
            -Override (New-HzOverride -Findings @($FailReq) -Operation 'save'))
    $decision = Get-HzDecision $s.Call
    $saved = if ($s.Call.Ok) { [bool](Get-HzProp $s.Call.Result 'saved') } else { $false }
    $reportedBefore = [string](Get-HzProp $s.Call.Result 'sha256_before')
    $reportedAfter = [string](Get-HzProp $s.Call.Result 'sha256_after')
    # THE COMMAND'S OWN CLAIM, CHECKED AGAINST THE DISK THIS SCRIPT READ.
    $agrees = ($reportedBefore -eq $s.Before.Sha256) -and ($reportedAfter -eq $s.After.Sha256)
    $prevention = Get-HzPrevention $s.Call
    $accepted = if ($prevention) { [bool](Get-HzProp $prevention 'override_accepted') } else { $false }
    $ok = $s.Call.Ok -and ($decision -eq 'overridden') -and $saved -and $accepted -and $agrees
    Add-HzProbe -Run $run -Id 'G3.6' -Name 'a valid override lets the save proceed and records it as overridden' `
        -Expected 'decision=overridden, saved=true, override_accepted=true, and the sha256 the command reports matches the disk' `
        -Observed ("decision={0} saved={1} accepted={2} hashes_agree_with_disk={3}" -f
                   $decision, $saved, $accepted, $agrees) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; saved = $saved; override_accepted = $accepted
                     reported_before = $reportedBefore; reported_after = $reportedAfter
                     disk_before = $s.Before.Sha256; disk_after = $s.After.Sha256 }
    Assert-HzNotInterceptable -Id 'G3.7' -Where 'overridden save' -Call $s.Call

    # ---- G3.8 ALLOWED: a satisfied profile saves. ---------------------------
    $s = Invoke-HzGatedSave -Label 'save-allowed' -RequireGate (New-HzRequireGate -Requirements $PassProfile)
    $decision = Get-HzDecision $s.Call
    $outcome = if ($s.Call.Ok) { [string](Get-HzProp $s.Call.Result 'outcome') } else { $null }
    $ok = $s.Call.Ok -and ($decision -eq 'allowed') -and ($outcome -in @('saved_verified', 'nothing_to_save'))
    Add-HzProbe -Run $run -Id 'G3.8' -Name 'a satisfied profile lets the save run' `
        -Expected 'decision=allowed and an outcome the command itself verified' `
        -Observed ("decision={0} outcome={1}" -f $decision, $outcome) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ decision = $decision; outcome = $outcome
                     sha256_before = $s.Before.Sha256; sha256_after = $s.After.Sha256 }
    Assert-HzNotInterceptable -Id 'G3.9' -Where 'allowed save' -Call $s.Call

    # ---- G3.10 no require_gate: unchanged. ---------------------------------
    $s = Invoke-HzGatedSave -Label 'save-ungated'
    $hasPrevention = $null -ne (Get-HzPrevention $s.Call)
    $ok = $s.Call.Ok -and (-not $hasPrevention)
    Add-HzProbe -Run $run -Id 'G3.10' -Name 'without require_gate the save behaves exactly as before' `
        -Expected 'the save runs and the reply carries NO prevention block' `
        -Observed ("ok={0} carries_prevention={1}" -f $s.Call.Ok, $hasPrevention) `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ ok = $s.Call.Ok; carries_prevention = $hasPrevention
                     outcome = $(if ($s.Call.Ok) { Get-HzProp $s.Call.Result 'outcome' } else { $null }) }
}
    }


# -----------------------------------------------------------------------------
# PHASE 4 - WHAT THIS GATE DELIBERATELY DOES NOT DO, read from the SOURCE.
#
# The gap between "Horizun's own save is gated" and "this model cannot be
# delivered dirty" is exactly one event subscription wide, and subscribing to
# DocumentSaving would close it - by cancelling other people's saves for everyone
# who has the add-in loaded. That is not a decision this bridge takes, and the
# only way to keep proving it is to read the tree.
#
# The same assertion lives in the test suite (CorrectionCycleWiringTests). It is
# repeated here because the artifact this run produces is what somebody reads
# when they ask what the gate covers, and an artifact that says "we chose not to
# intercept" without evidence is a sentence rather than a fact.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 4 - no event was subscribed to on the way' -ForegroundColor Cyan

$srcRoot = Join-Path $run.RepoRoot 'src\Horizun.Revit'
$cancellable = @('DocumentSaving', 'DocumentSavingAs', 'DocumentSynchronizingWithCentral',
                 'DocumentClosing', 'FileExporting', 'DocumentOpened', 'DocumentChanged',
                 'ViewActivated', 'Idling')
$offenders = New-Object System.Collections.ArrayList
$interferenceCount = $null
if (Test-Path -LiteralPath $srcRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $srcRoot -Filter *.cs -Recurse) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($ev in $cancellable) {
            if ($text -match ('\.' + $ev + '\s*\+=')) {
                $null = $offenders.Add(('{0} subscribes to {1}' -f $file.Name, $ev))
            }
        }
        if ($file.Name -ne 'Interference.cs') {
            foreach ($m in [regex]::Matches($text, '\.(\w+)\s*\+=\s*On\w+')) {
                $null = $offenders.Add(('{0} subscribes to {1}' -f $file.Name, $m.Groups[1].Value))
            }
        }
    }
    $interference = Join-Path $srcRoot 'Core\Interference.cs'
    if (Test-Path -LiteralPath $interference) {
        $interferenceCount = ([regex]::Matches((Get-Content -LiteralPath $interference -Raw), '\+=\s*On\w+')).Count
    }
}

if (-not (Test-Path -LiteralPath $srcRoot)) {
    Add-HzFixtureMissing -Id 'G4.1' -Name 'the source-level proof that no event was subscribed to' `
        -Needs "the source tree at $srcRoot. This harness reads it directly; run it from a checkout rather than from an installed copy."
} else {
    Add-HzProbe -Run $run -Id 'G4.1' -Name 'the gate subscribes to NO Revit event, and the two that exist are the interference checker''s' `
        -Expected 'zero subscriptions to any cancellable document event across src/Horizun.Revit, and exactly 2 handlers in Interference.cs' `
        -Observed ("offenders={0} interference_handlers={1}" -f $offenders.Count, $interferenceCount) `
        -Status $(if ($offenders.Count -eq 0 -and $interferenceCount -eq 2) { 'passed' } else { 'failed' }) `
        -Evidence @{ offenders = @($offenders); interference_handlers = $interferenceCount
                     events_searched = $cancellable }
}

# EVERY GATED REPLY, not just one. The per-decision probes above each checked
# their own; this is the roll-up, so a decision that quietly stopped carrying the
# field cannot hide behind the ones that still do.
$allNamed = @($script:NotInterceptableSeen | Where-Object { -not $_.ok })
Add-HzProbe -Run $run -Id 'G4.2' -Name 'every gated reply this run produced named what the gate cannot reach' `
    -Expected 'all gated replies carry not_interceptable naming Revit''s own Save and Synchronize with Central' `
    -Observed ("{0} gated replies checked, {1} missing the field" -f $script:NotInterceptableSeen.Count, $allNamed.Count) `
    -Status $(if ($script:NotInterceptableSeen.Count -ge 1 -and $allNamed.Count -eq 0) { 'passed' } else { 'failed' }) `
    -Evidence @{ checked = @($script:NotInterceptableSeen) }

Add-HzNote -Run $run -Text ("every output of this run is under {0} and nothing was written beside a model" -f (Protect-HzText $OutDir))
Add-HzNote -Run $run -Text 'Ctrl+S, manual Synchronize with Central, Revit close and other add-ins are OUT OF SCOPE by design; the gate is opt-in on Horizun''s own save and export only.'

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir

$c = Get-HzCounts $run
$total = $c.passed + $c.failed + $c.unverified + $c.not_covered + $c.fixture_missing +
         $c.not_assessable + $c.not_applicable + $c.available + $c.implemented_not_live_verified
if ($total -ne $run.Probes.Count) {
    Write-Host ("  BUCKETS DO NOT ADD UP: {0} probes, {1} counted" -f $run.Probes.Count, $total) -ForegroundColor Red
    exit 3
}
exit $done.ExitCode
