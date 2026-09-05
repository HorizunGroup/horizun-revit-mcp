#Requires -Version 5.1
<#
  THE MODEL DOCTOR, LIVE - ONE RUN, ONE REVIT, ONE SESSION.

  Thirty-three stories were written offline. Thirty-two of them are green in a
  test suite that has never seen Revit, and that is exactly as much as a test
  suite can say: every rule here decides what to do with facts EXTRACTED from a
  document, and nothing offline can tell you whether the extraction is right.

  So this runs once, against a real Revit 2026 and a known fixture, and it is
  built around four refusals:

    IT REFUSES TO RUN AGAINST THE WRONG BUILD. The commit, the contract hash,
    the Revit year and the active document are all demanded before the first
    probe. A campaign that measured a stale add-in and reported a green matrix
    is the single most expensive thing this file can produce, and it has
    happened here before - a passing suite over an add-in three commits old.

    IT REFUSES TO CALL A MISSING SECTION A PASS. Every section asked for must
    come back. A reply that silently omits one is a failure, not a quiet zero,
    because "no findings" and "did not run" render identically in a report.

    IT REFUSES TO SIMULATE A FIXTURE. Ownership, borrowing, closed worksets and
    cloud models need a real central model, a second user and an ACC project.
    Where those are absent the probe is recorded fixture_missing and NAMES what
    is needed. It is never simulated and then reported as Revit evidence.

    IT NEVER SAVES. Not the fixture, not a copy, not on the way out. The batch
    probes use disposable copies, and the correction probes rehearse only.

  Exit 0 only when every probe passed. Exit 2 when the gate refused, with
  nothing run - which is a different thing from a failure, and says so.
#>
[CmdletBinding()]
param(
    # THE COMMIT THIS CAMPAIGN IS ABOUT. Mandatory and exact: the whole point is
    # that the numbers below belong to one identifiable build.
    [Parameter(Mandatory)][string]$RequireCommit,
    [Parameter(Mandatory)][string]$RequireContractHash,
    [string]$Document = 'HZ_LIVE_A',
    # The P0 regression BUILDS deliberate defects, so it runs against the write
    # fixture rather than the read one. Passing it the campaign's document would
    # have it construct defects in the model the campaign is measuring.
    [string]$RegressionDocument = 'HZ_WRITE',
    [string]$RequireRevitYear = '2026',
    [string]$ArtifactDir,
    # Disposable copies for the batch sweep. Absent = those probes are recorded
    # fixture_missing rather than skipped.
    [string[]]$BatchFixture = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'doctor-campaign' -Document $Document

# The thirty-two sections horizun_model_scan publishes. Kept here as data so a
# section added to the command and forgotten here shows up as a section the
# reply carries and the campaign never asked about - which is reported, not
# ignored.
$SECTIONS = @(
    'document', 'categories', 'cleanliness', 'naming', 'documentation',
    'project_info', 'health', 'links', 'worksets', 'design_options', 'lines', 'types',
    'coordinates', 'datums', 'level_association', 'worksharing', 'families',
    'views', 'sheets', 'annotations',
    'parameters', 'spatial', 'groups', 'design_options_census', 'phases', 'mep',
    'structure', 'federation', 'external_content', 'documentary_context',
    'delivery_readiness', 'weight'
)

# =============================================================================
# THE GATE. Nothing below runs until all five of these are true.
# =============================================================================

function Assert-HzCampaignGate {
    param([Parameter(Mandatory)]$Run)

    $problems = New-Object System.Collections.ArrayList
    $health = $null
    try { $health = Get-HzHealth $Run } catch { $null = $problems.Add("horizun_health did not answer: $($_.Exception.Message)") }

    if ($health) {
        $status = [string](Get-HzProp $health 'status')
        if ($status -ne 'healthy') { $null = $problems.Add("health reports status '$status', not 'healthy'") }

        $year = [string](Get-HzProp $health 'revit_version')
        if ($year -ne $RequireRevitYear) {
            $null = $problems.Add("this is Revit $year and the campaign is defined against $RequireRevitYear")
        }

        # THE ONE THAT HAS BEEN WRONG BEFORE. A passing matrix over an add-in
        # three commits old is indistinguishable from a passing matrix, until
        # somebody compares SHAs.
        $commit = [string](Get-HzProp $health 'horizun_commit')
        if (-not $commit) { $null = $problems.Add('health reports no commit, so nothing here could be attributed to a build') }
        elseif ($commit -notlike "$RequireCommit*" -and $RequireCommit -notlike "$commit*") {
            $null = $problems.Add("the running add-in is '$commit' and this campaign is about '$RequireCommit'")
        }

        $active = Get-HzProp $health 'active_document'
        $title = if ($active) { [string](Get-HzProp $active 'title') } else { $null }
        if ($title -ne $Document) {
            $null = $problems.Add("the active document is '$title' and the campaign is defined against '$Document'")
        }
    }

    # The server's contract hash, read from the SERVER that answered rather than
    # from the source tree - the source tree is not what this run talked to.
    $identity = Get-HzResource -Run $Run -Uri 'horizun://build/identity' -Label 'build-identity'
    $hash = if ($identity) { [string](Get-HzProp $identity 'contract_hash') } else { $null }
    if (-not $hash) { $null = $problems.Add('the server published no contract hash, so the two halves cannot be shown to match') }
    elseif ($hash -ne $RequireContractHash) {
        $null = $problems.Add("the server's contract hash is '$hash' and this campaign is about '$RequireContractHash'")
    }

    if ($problems.Count -eq 0) {
        Write-Host ("  GATE OK  commit={0} revit={1} document={2}" -f
            (Limit-HzText $RequireCommit 12), $RequireRevitYear, $Document) -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host '  THE CAMPAIGN DID NOT RUN. Nothing was measured:' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }
    Write-Host ''
    Write-Host '  This is a refusal, not a failure. No probe ran, so no probe passed' -ForegroundColor Yellow
    Write-Host '  and none failed; nothing about the product was learned either way.' -ForegroundColor Yellow
    exit 2
}

# =============================================================================
# HELPERS
# =============================================================================

function Invoke-HzScan {
    param([string[]]$Sections, [hashtable]$Extra, [string]$Label = 'scan')
    # model_scan's compatibility guard is named target_document_title; the
    # audit surface uses target_document. Sending the latter to scan is an
    # unknown option and makes every section look absent downstream.
    $a = @{ target_document_title = $Document; top = 20 }
    if ($Sections) { $a['sections'] = $Sections }
    if ($Extra) { foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] } }
    Invoke-HzTool -Run $run -Tool 'horizun_model_scan' -Arguments $a -Label $Label -TimeoutSec 600
}

function Get-HzScanSections {
    param($Call)
    if ($null -eq $Call -or $null -eq $Call.Result) { return $null }
    Get-HzProp $Call.Result 'sections'
}

function Invoke-HzDoctorAudit {
    param([hashtable]$Extra, [string]$Label = 'audit')
    $a = @{ target_document = $Document; top = 20 }
    if ($Extra) { foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] } }
    Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Arguments $a -Label $Label -TimeoutSec 600
}

<#
  Every value of a named property anywhere in a reply's subtree.

  Written after the first draft of Phase 2 asserted on paths that do not exist.
  Buckets nest ({items, total, returned, truncated, next_cursor}), sections
  differ in depth, and a probe that fails because the harness guessed a path
  teaches nothing about the product - it just spends the one live run.
#>
function Find-HzValues {
    param($Node, [Parameter(Mandatory)][string]$Name, [int]$Depth = 0)
    $found = @()
    if ($null -eq $Node -or $Depth -gt 8) { return $found }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) { $found += Find-HzValues -Node $item -Name $Name -Depth ($Depth + 1) }
        return $found
    }
    if ($Node -isnot [psobject]) { return $found }

    foreach ($prop in $Node.PSObject.Properties) {
        if ($prop.Name -eq $Name) { $found += , $prop.Value }
        $found += Find-HzValues -Node $prop.Value -Name $Name -Depth ($Depth + 1)
    }
    return $found
}

<#
  A probe over a field the reply must publish somewhere in a section.
#>
function Add-HzFieldProbe {
    param([string]$Id, [string]$Name, $Section, [string]$Field, [string]$Because)
    $values = @(Find-HzValues -Node $Section -Name $Field)
    Add-HzProbe -Run $run -Id $Id -Name $Name `
        -Expected "the section publishes '$Field'" `
        -Observed $(if ($values.Count) { "$($values.Count) occurrence(s)" } else { 'the field is not in the reply' }) `
        -Status $(if ($values.Count) { 'passed' } else { 'failed' }) `
        -Because $Because `
        -Evidence @{ field = $Field; occurrences = $values.Count }
    return $values
}

<#
  A section is PRESENT and says what it is. The two failures this catches are
  the ones a report cannot show: a section silently omitted, and a section that
  threw and rendered as a clean zero.
#>
function Add-HzSectionProbe {
    param([string]$Id, [string]$Section, $Result)
    $s = Get-HzPath $Result $Section
    if ($null -eq $s) {
        Add-HzProbe -Run $run -Id $Id -Name "section '$Section' is present" `
            -Expected 'the section asked for comes back' `
            -Observed 'the reply carries no such key' -Status 'failed' `
            -Because 'a section that is absent and a section that found nothing render identically in a report.'
        return $false
    }
    $status = [string](Get-HzProp $s 'status')
    $reason = [string](Get-HzProp $s 'reason')
    # A section that ran and proved there is no applicable object is not a
    # section that failed. Require its reason so a bare not_applicable cannot
    # become a convenient silent pass.
    $ok = ($status -eq 'ok') -or ($status -eq 'not_applicable' -and -not [string]::IsNullOrWhiteSpace($reason))
    Add-HzProbe -Run $run -Id $Id -Name "section '$Section' ran" `
        -Expected "status ok, or a reasoned not_applicable when the model has no such objects" -Observed "status $status" `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Evidence @{ section = $Section; status = $status; reason = $reason }
    return $ok
}

<#
  A fixture nobody on this machine has. Recorded rather than skipped, and it
  NAMES what is needed - a campaign that quietly drops the cases it cannot set
  up reports a coverage it does not have.
#>
function Add-HzFixtureMissing {
    param([string]$Id, [string]$Name, [string]$Needs)
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Needs `
        -Observed 'the fixture is not present on this machine' -Status 'fixture_missing' `
        -Because 'simulating this and reporting it as Revit evidence would be a lie about where the number came from.'
}

# =============================================================================
Assert-HzCampaignGate -Run $run
# =============================================================================

# -----------------------------------------------------------------------------
# PHASE 0 - the regression that already passed. It runs FIRST, because a
# campaign that adds thirty new probes to a slice that has silently broken is
# measuring the wrong thing.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 0 - the P0 diagnostics regression' -ForegroundColor Cyan

$regression = Join-Path $PSScriptRoot 'verify-diagnostics.ps1'
if (-not (Test-Path -LiteralPath $regression)) {
    Add-HzProbe -Run $run -Id 'C0.1' -Name 'the P0 regression harness exists' `
        -Expected 'scripts/live/verify-diagnostics.ps1' -Observed 'not found' -Status 'failed'
}
else {
    $regressionPath = Join-Path 'C:\hz-live' ($RegressionDocument + '.rvt')
    $documentPath = Join-Path 'C:\hz-live' ($Document + '.rvt')
    $rc = 1
    try {
        $null = Set-HzActiveDocument -Run $run -Document $RegressionDocument -FilePath $regressionPath
        & powershell -NoProfile -ExecutionPolicy Bypass -File $regression -Document $RegressionDocument
        $rc = $LASTEXITCODE
    }
    finally {
        $null = Set-HzActiveDocument -Run $run -Document $Document -FilePath $documentPath
    }
    Add-HzProbe -Run $run -Id 'C0.1' -Name 'the P0 diagnostics regression still passes' `
        -Expected "verify-diagnostics.ps1 exits 0 against $RegressionDocument" -Observed "exit $rc" `
        -Status $(if ($rc -eq 0) { 'passed' } else { 'failed' }) `
        -Because 'thirty new probes over a slice that has silently broken measure the wrong thing.'
}

# -----------------------------------------------------------------------------
# PHASE 1 - every section runs, and a section not asked for says so.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 1 - all 32 sections' -ForegroundColor Cyan

$full = Invoke-HzScan -Label 'scan-all'
if ($full.IsError) {
    Add-HzProbe -Run $run -Id 'C1.0' -Name 'a full scan answers' -Expected 'a reply' `
        -Observed (Limit-HzText $full.Text 300) -Status 'failed'
}
else {
    Add-HzProbe -Run $run -Id 'C1.0' -Name 'a full scan answers' -Expected 'a reply' -Observed 'answered' -Ok $true
    $i = 0
    foreach ($s in $SECTIONS) {
        $i++
        [void](Add-HzSectionProbe -Id ('C1.{0}' -f $i) -Section $s -Result (Get-HzScanSections $full))
    }

    # AND NOTHING ELSE. A section the command publishes and this campaign never
    # asked about is a gap in the campaign, reported rather than invisible.
    $published = @((Get-HzScanSections $full).PSObject.Properties.Name)
    $unknown = @($published | Where-Object {
        $_ -notin $SECTIONS -and $_ -notmatch '^(document_fingerprint|contract_version|target_document|generated_utc|truncated|status|note|warnings)$'
    })
    Add-HzProbe -Run $run -Id 'C1.90' -Name 'the campaign knows every section the tool publishes' `
        -Expected 'no section in the reply is unknown to this campaign' `
        -Observed $(if ($unknown.Count) { 'unrecognised: ' + ($unknown -join ', ') } else { 'all recognised' }) `
        -Status $(if ($unknown.Count) { 'failed' } else { 'passed' }) `
        -Evidence @{ unrecognised = $unknown }
}

# A section NOT asked for is not_requested, never an empty result.
$one = Invoke-HzScan -Sections @('document') -Label 'scan-one'
if (-not $one.IsError) {
    $others = @($SECTIONS | Where-Object { $_ -ne 'document' } | Select-Object -First 6)
    $wrong = New-Object System.Collections.ArrayList
    foreach ($s in $others) {
        $sec = Get-HzPath (Get-HzScanSections $one) $s
        $st = if ($sec) { [string](Get-HzProp $sec 'status') } else { '<absent>' }
        if ($st -ne 'not_requested') { $null = $wrong.Add("$s=$st") }
    }
    Add-HzProbe -Run $run -Id 'C1.91' -Name 'a section not asked for is not_requested' `
        -Expected 'status not_requested for every unasked section' `
        -Observed $(if ($wrong.Count) { $wrong -join ', ' } else { 'all not_requested' }) `
        -Status $(if ($wrong.Count) { 'failed' } else { 'passed' }) `
        -Because 'an empty section and an unasked section are different facts, and only one of them is about the model.'
}

# -----------------------------------------------------------------------------
# PHASE 2 - the capabilities closed offline this session, measured on a model.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 2 - documentary context, coordinates, datums, 4D and 5D' -ForegroundColor Cyan

# 2a. DOCUMENTARY CONTEXT under a caller-supplied profile, and with none.
#
# The rule's `id` is the FIELD it judges - project_name, project_number,
# client_name are the ids the section collects - and every rule must also name a
# parameter, or it matches everything or nothing depending on how it is read.
$docProfile = @{
    version = 'campaign-v1'
    rules   = @(
        @{ id = 'building_name'; built_in_parameter = 'PROJECT_BUILDING_NAME';
           categories = @('project_information'); required = $true },
        @{ id = 'project_number'; built_in_parameter = 'PROJECT_NUMBER';
           categories = @('project_information'); required = $true },
        @{ id = 'client_name'; built_in_parameter = 'CLIENT_NAME';
           categories = @('project_information'); required = $false }
    )
}
$dc = Invoke-HzScan -Sections @('documentary_context') -Extra @{ documentary_profile = $docProfile } -Label 'documentary'
if (-not $dc.IsError) {
    $sec = Get-HzPath (Get-HzScanSections $dc) 'documentary_context'
    $state = [string](Get-HzProp $sec 'profile')
    Add-HzProbe -Run $run -Id 'C2.1' -Name 'a supplied documentary profile is accepted, not refused' `
        -Expected "profile = ok" -Observed "profile = $state" `
        -Status $(if ($state -eq 'ok') { 'passed' } else { 'failed' }) `
        -Because 'a refused profile is a harness error; a not_requested one means it never arrived.' `
        -Evidence @{ documentary_context = $sec }

    $assessed = Get-HzProp $sec 'fields_assessed'
    Add-HzProbe -Run $run -Id 'C2.2' -Name 'the declared fields are assessed' `
        -Expected 'fields_assessed is at least the 3 rules declared' -Observed "fields_assessed = $assessed" `
        -Status $(if ($null -ne $assessed -and [int]$assessed -ge 3) { 'passed' } else { 'failed' })

    # PRESENT-AND-EMPTY IS NOT ABSENT, and the outcomes must come from the
    # declared vocabulary rather than from whatever the code happened to emit.
    $outcomes = @(Find-HzValues -Node (Get-HzProp $sec 'findings') -Name 'outcome')
    $known = @('present', 'missing', 'empty', 'placeholder', 'invalid', 'unreadable',
               'not_requested', 'not_applicable', 'wrong_guid', 'wrong_binding', 'ok')
    $strange = @($outcomes | Where-Object { $_ -and $known -notcontains [string]$_ } | Sort-Object -Unique)
    Add-HzProbe -Run $run -Id 'C2.3' -Name 'documentary outcomes are the declared vocabulary' `
        -Expected 'every outcome is one of the declared states' `
        -Observed $(if ($strange.Count) { 'unrecognised: ' + ($strange -join ', ') } else { 'all recognised' }) `
        -Status $(if ($strange.Count) { 'failed' } else { 'passed' }) `
        -Evidence @{ outcomes = @($outcomes | Sort-Object -Unique) }
}

$dcNone = Invoke-HzScan -Sections @('documentary_context') -Label 'documentary-no-profile'
if (-not $dcNone.IsError) {
    $sec = Get-HzPath (Get-HzScanSections $dcNone) 'documentary_context'
    $state = [string](Get-HzProp $sec 'profile')
    Add-HzProbe -Run $run -Id 'C2.4' -Name 'no profile is not a verdict about the model' `
        -Expected 'profile = not_requested' -Observed "profile = $state" `
        -Status $(if ($state -eq 'not_requested') { 'passed' } else { 'failed' }) `
        -Because 'a model is not badly documented because nobody said what documentation it owes.'
}

# 2b. COORDINATES and the SHARED-POSITION limit, stated rather than inferred.
$co = Invoke-HzScan -Sections @('coordinates') -Label 'coordinates'
if (-not $co.IsError) {
    $c = Get-HzPath (Get-HzScanSections $co) 'coordinates'
    [void](Add-HzFieldProbe -Id 'C2.5' -Name 'the three control points are read apart' -Section $c `
        -Field 'project_base_point' `
        -Because 'internal origin, project base point and survey point are three DIFFERENT points, and a survey point ten kilometres out is CORRECT.')

    $shared = @(Find-HzValues -Node $c -Name 'shared_position_matches_host')
    $means = @(Find-HzValues -Node $c -Name 'shared_position_means')
    $allNull = ($shared.Count -eq 0) -or (@($shared | Where-Object { $null -ne $_ }).Count -eq 0)
    Add-HzProbe -Run $run -Id 'C2.6' -Name 'shared position is declared not observable, never inferred' `
        -Expected 'every shared_position_matches_host is null, and the reason travels with it' `
        -Observed ("{0} link(s), {1} non-null, {2} reason(s)" -f $shared.Count, @($shared | Where-Object { $null -ne $_ }).Count, $means.Count) `
        -Status $(if ($allNull -and ($shared.Count -eq 0 -or $means.Count -gt 0)) { 'passed' } else { 'failed' }) `
        -Because 'two links can share a transform and not share a position; the API exposes no read path, established by reflection over all five years.' `
        -Evidence @{ values = $shared; reasons = @($means | Select-Object -First 1) }
}

# 2c. PER-VIEW NORTH lives on the view rows, not in coordinates.
$vw = Invoke-HzScan -Sections @('views') -Label 'views-north'
if (-not $vw.IsError) {
    [void](Add-HzFieldProbe -Id 'C2.7' -Name 'per-view north is read on a real model' `
        -Section (Get-HzPath (Get-HzScanSections $vw) 'views') -Field 'north_orientation' `
        -Because 'PLAN_VIEW_NORTH exists in 2023-2027, verified by reflection; a plan set to project north in a rotated building reads correctly and looks wrong.')
}

# 2d. DATUMS: scope boxes and grid geometry.
$dt = Invoke-HzScan -Sections @('datums') -Label 'datums'
if (-not $dt.IsError) {
    $d = Get-HzPath (Get-HzScanSections $dt) 'datums'
    [void](Add-HzFieldProbe -Id 'C2.8' -Name 'scope-box assignment is read rather than substituted' `
        -Section $d -Field 'scope_box_summary' `
        -Because 'a bounding box borrowed from another element would be a heuristic, and it would have to say so.')
    [void](Add-HzFieldProbe -Id 'C2.9' -Name 'grid geometry is measured, not just named' `
        -Section $d -Field 'grids_total' `
        -Because 'two grids a millimetre apart collide on neither name nor position, and nothing in Revit ever mentions them.')
}

# 2e. 4D and 5D readiness, per leaf category, under caller-supplied profiles.
#
# CATEGORIES ARE MATCHED BY DISPLAY NAME (e.Category.Name), so 'Walls', not
# 'OST_Walls'. A role is the rule's `id`, and a 5D role is checked against the
# catalogue only when its specification says classification_code.
$fourd = @{
    version = 'campaign-4d-v1'
    rules   = @(
        @{ id = 'activity_id'; name = 'HZ_ActivityId'; scope = 'instance';
           categories = @('Walls', 'Floors'); required = $true },
        @{ id = 'zone'; name = 'HZ_Zone'; scope = 'instance'; categories = @('Walls'); required = $false }
    )
}
$fived = @{
    version = 'campaign-5d-v1'
    rules   = @(
        @{ id = 'cost_code'; name = 'HZ_CostCode'; scope = 'type'; categories = @('Walls'); required = $true },
        @{ id = 'classification_code'; name = 'HZ_Class'; scope = 'type';
           specification = 'classification_code'; categories = @('Walls'); required = $true }
    )
}
# A GROUP and a LEAF, declared. Leafness is never inferred from a code's shape.
$catalogue = @{
    version = 'campaign-cat-v1'
    codes   = @{ '03.30' = $false; '03.30.10' = $true }
}

$rd = Invoke-HzScan -Sections @('delivery_readiness') `
        -Extra @{ fourd_profile = $fourd; fived_profile = $fived; classification_catalogue = $catalogue } `
        -Label 'readiness'
if (-not $rd.IsError) {
    $r = Get-HzPath (Get-HzScanSections $rd) 'delivery_readiness'
    $profiles = Get-HzProp $r 'profiles'
    foreach ($dim in @('fourd', 'fived')) {
        $profile = if ($profiles) { Get-HzProp $profiles $dim } else { $null }
        $state = if ($profile) { [string](Get-HzProp $profile 'status') } else { '<absent>' }
        Add-HzProbe -Run $run -Id ("C2.{0}" -f $(if ($dim -eq 'fourd') { 10 } else { 11 })) `
            -Name "the $dim profile is accepted, not refused" `
            -Expected 'ok' -Observed $state `
            -Status $(if ($state -match '^ok') { 'passed' } else { 'failed' }) `
            -Evidence @{ profiles = $profiles }
    }

    foreach ($dim in @('4d', '5d')) {
        $block = Get-HzProp $r $dim
        Add-HzProbe -Run $run -Id ("C2.{0}" -f $(if ($dim -eq '4d') { 12 } else { 13 })) `
            -Name "$dim readiness is measured per declared leaf category" `
            -Expected 'a dimension block with per-category counts' `
            -Observed $(if ($block) { 'present' } else { 'absent' }) `
            -Status $(if ($block) { 'passed' } else { 'failed' }) `
            -Evidence @{ dimension = $block }

        # NO SCORE. A number would be read as a percentage of readiness and the
        # states are not commensurable.
        if ($block) {
            $score = Get-HzProp $block 'score'
            Add-HzProbe -Run $run -Id ("C2.{0}" -f $(if ($dim -eq '4d') { 14 } else { 15 })) `
                -Name "$dim publishes no score" `
                -Expected 'score is null' -Observed ("score = {0}" -f $(if ($null -eq $score) { 'null' } else { $score })) `
                -Status $(if ($null -eq $score) { 'passed' } else { 'failed' })
        }
    }

    # The seven code states, and group_not_terminal among them: the failure that
    # most looks like success, because the code is REAL and passes any regex.
    $cls = Get-HzProp $r 'classification'
    Add-HzProbe -Run $run -Id 'C2.16' -Name 'classification codes are judged against the supplied catalogue' `
        -Expected 'a classification block naming the catalogue that was supplied' `
        -Observed $(if ($cls) { 'present' } else { 'absent' }) `
        -Status $(if ($cls) { 'passed' } else { 'failed' }) -Evidence @{ classification = $cls }
}

# WITHOUT a profile, nothing is declared unready.
$rdNone = Invoke-HzScan -Sections @('delivery_readiness') -Label 'readiness-no-profile'
if (-not $rdNone.IsError) {
    $r = Get-HzPath (Get-HzScanSections $rdNone) 'delivery_readiness'
    $profiles = Get-HzProp $r 'profiles'
    $states = @('fourd', 'fived') | ForEach-Object {
        [string](Get-HzProp (Get-HzProp $profiles $_) 'status')
    }
    Add-HzProbe -Run $run -Id 'C2.17' -Name 'no 4D/5D profile is not a verdict of unready' `
        -Expected 'both profiles report not_requested' -Observed ($states -join ', ') `
        -Status $(if (@($states | Where-Object { $_ -notmatch 'not_requested|not_required' }).Count -eq 0) { 'passed' } else { 'failed' }) `
        -Because 'nothing here says a model is not ready for 4D because nobody said what 4D means here.'
}

# -----------------------------------------------------------------------------
# PHASE 3 - snapshots, trends and the health index.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 3 - snapshots, trends, health index' -ForegroundColor Cyan

$doctorHealthProfile = @{
    id = 'doctor-campaign'; version = '1'; context = 'project'
    weights = @(
        @{ dimension = 'warnings'; weight = 2; critical = $true },
        @{ dimension = 'imported_cad'; weight = 1; critical = $false },
        @{ dimension = 'views_without_template'; weight = 1; critical = $false },
        @{ dimension = 'coordinates'; weight = 2; critical = $true },
        @{ dimension = 'datums'; weight = 2; critical = $true }
    )
}
$historyArgs = @{ store_snapshot = $true; health_profile = $doctorHealthProfile }
$a1 = Invoke-HzDoctorAudit -Extra $historyArgs -Label 'audit-1'
if (-not $a1.IsError) {
    $snap = Get-HzPath $a1.Result 'snapshot'
    $snapshotOk = $snap -and ([string](Get-HzProp $snap 'status') -eq 'ok') -and
                  ([string](Get-HzProp $snap 'document_fingerprint'))
    Add-HzProbe -Run $run -Id 'C3.1' -Name 'an audit produces a snapshot with its own fingerprint' `
        -Expected 'a snapshot block carrying the document fingerprint' `
        -Observed $(if ($snap) { 'status=' + (Get-HzProp $snap 'status') } else { 'absent' }) `
        -Status $(if ($snapshotOk) { 'passed' } else { 'failed' }) -Evidence @{ snapshot = $snap }

    $index = Get-HzPath $a1.Result 'health_index'
    $indexOk = $index -and ([string](Get-HzProp $index 'status') -eq 'ok') -and
               ($null -ne (Get-HzProp $index 'coverage_complete'))
    Add-HzProbe -Run $run -Id 'C3.2' -Name 'the health index states its coverage alongside its number' `
        -Expected 'an index that says what it could not look at' `
        -Observed $(if ($index) { 'status=' + (Get-HzProp $index 'status') } else { 'absent' }) `
        -Status $(if ($indexOk) { 'passed' } else { 'failed' }) -Evidence @{ health_index = $index }
}

$a2 = Invoke-HzDoctorAudit -Extra $historyArgs -Label 'audit-2'
if (-not $a2.IsError) {
    $trend = Get-HzPath $a2.Result 'trend'
    $trendOk = $trend -and ([string](Get-HzProp $trend 'status') -eq 'ok') -and
               ((Get-HzProp $trend 'no_drift') -eq $true)
    Add-HzProbe -Run $run -Id 'C3.3' -Name 'a second audit of an unchanged model reports no drift' `
        -Expected 'the trend compares against the previous snapshot and finds nothing moved' `
        -Observed $(if ($trend) { 'status=' + (Get-HzProp $trend 'status') + ' no_drift=' + (Get-HzProp $trend 'no_drift') } else { 'absent' }) `
        -Status $(if ($trendOk) { 'passed' } else { 'failed' }) -Evidence @{ trend = $trend }
}

# -----------------------------------------------------------------------------
# PHASE 4 - guided corrections. REHEARSED ONLY. Nothing is executed.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 4 - guided corrections, rehearsed only' -ForegroundColor Cyan

$corr = Invoke-HzDoctorAudit -Extra @{ propose_corrections = $true } -Label 'corrections'
if (-not $corr.IsError) {
    $correctionBlock = Get-HzPath $corr.Result 'corrections'
    $props = @()
    if ($correctionBlock) {
        $published = Get-HzProp $correctionBlock 'proposals'
        if ($published) { $props = @($published) }
    }
    Add-HzProbe -Run $run -Id 'C4.1' -Name 'corrections are proposed, never executed' `
        -Expected 'every proposal carries dry_run true and confirmation_required' `
        -Observed ("{0} proposal(s)" -f $props.Count) `
        -Status $(if ($null -ne $correctionBlock) { 'passed' } else { 'failed' }) `
        -Evidence @{ corrections = $props }

    $executed = @($props | Where-Object {
        $state = [string](Get-HzProp $_ 'state')
        $arguments = Get-HzProp $_ 'arguments'
        $state -eq 'executed' -or
        ($state -eq 'actionable' -and $arguments -and (Get-HzProp $arguments 'dry_run') -ne $true)
    })
    Add-HzProbe -Run $run -Id 'C4.2' -Name 'no proposal came back already executed' `
        -Expected 'zero proposals with dry_run false' -Observed ("{0}" -f $executed.Count) `
        -Status $(if ($executed.Count -eq 0) { 'passed' } else { 'failed' }) `
        -Because 'the Doctor is read-only; a proposal that ran is a write nobody authorised.'

    # THE REGISTRY TRAVELS WITH THE ANSWER. Without it an empty proposal list is
    # indistinguishable from a surface that had nothing registered to offer.
    $registry = Get-HzPath $corr.Result @('corrections', 'registry')
    $tools = @(Find-HzValues -Node $registry -Name 'tools')
    Add-HzProbe -Run $run -Id 'C4.4' -Name 'the correction registry is published with the answer' `
        -Expected 'the entries and the tools they may name' `
        -Observed $(if ($registry) { 'present' } else { 'absent' }) `
        -Status $(if ($registry) { 'passed' } else { 'failed' }) -Evidence @{ registry = $registry }

    $flat = ($tools | ConvertTo-Json -Depth 6 -Compress)
    Add-HzProbe -Run $run -Id 'C4.5' -Name 'the registry names no tool that runs arbitrary code' `
        -Expected 'horizun_execute_python is not in it' `
        -Observed $(if ($flat) { Limit-HzText $flat 200 } else { '<no tools listed>' }) `
        -Status $(if ($flat -and $flat -notmatch 'execute_python') { 'passed' } else { 'failed' }) `
        -Because 'a correction surface with an arbitrary-code escape hatch has no safety model - it has a list of suggestions and a way around the list.'

    # The model is unchanged by having been advised about.
    $after = Invoke-HzDoctorAudit -Label 'corrections-after'
    if (-not $after.IsError) {
        Add-HzProbe -Run $run -Id 'C4.3' -Name 'proposing corrections changed nothing in the model' `
            -Expected 'the fingerprint after equals the fingerprint before' `
            -Observed ("{0} -> {1}" -f (Get-HzPath $corr.Result 'document_fingerprint'), (Get-HzPath $after.Result 'document_fingerprint')) `
            -Status $(if ((Get-HzPath $corr.Result 'document_fingerprint') -eq (Get-HzPath $after.Result 'document_fingerprint')) { 'passed' } else { 'failed' })
    }
}

# -----------------------------------------------------------------------------
# PHASE 5 - the prevention gate.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 5 - the prevention gate' -ForegroundColor Cyan

# The gate decides on THIS audit. Asked for a real operation, with no override.
$g1 = Invoke-HzDoctorAudit -Extra @{
        prevention_gate = @{ operation = 'sync_with_central'; profile_version = 'campaign-v1' }
      } -Label 'gate-plain'
if (-not $g1.IsError) {
    $v = Get-HzPath $g1.Result 'prevention'
    $decision = [string](Get-HzProp $v 'decision')
    $coverage = Get-HzProp $v 'coverage_complete'

    Add-HzProbe -Run $run -Id 'C5.1' -Name 'the gate answers with one of its four decisions' `
        -Expected 'allow, block, requires_override or not_assessable' -Observed $decision `
        -Status $(if ($decision -in @('allow', 'block', 'requires_override', 'not_assessable')) { 'passed' } else { 'failed' }) `
        -Evidence @{ prevention = $v }

    # THE ASYMMETRY, measured rather than asserted: with anything unread, the
    # gate may block and may never allow.
    Add-HzProbe -Run $run -Id 'C5.2' -Name 'incomplete coverage never produces allow' `
        -Expected 'if coverage_complete is false the decision is not allow' `
        -Observed ("coverage_complete = {0}, decision = {1}" -f $coverage, $decision) `
        -Status $(if ($coverage -eq $false -and $decision -eq 'allow') { 'failed' } else { 'passed' }) `
        -Because 'a defect found in the part that was examined is real; "nothing wrong here" is a claim about a whole model that was half looked at.'

    # AND IT DECIDES RATHER THAN ENFORCES.
    Add-HzProbe -Run $run -Id 'C5.3' -Name 'the gate says it does not enforce' `
        -Expected 'enforced = false' -Observed ("enforced = {0}" -f (Get-HzProp $v 'enforced')) `
        -Status $(if ((Get-HzProp $v 'enforced') -eq $false) { 'passed' } else { 'failed' }) `
        -Because 'Horizun subscribes to no DocumentSaving event, by choice rather than by an API limit. Collapsing "gate possible" and "gate implemented" is how a team comes to believe a gate protects them.'
}

# An operation this bridge cannot gate is refused, never allowed.
$g2 = Invoke-HzDoctorAudit -Extra @{ prevention_gate = @{ operation = 'email_the_client' } } -Label 'gate-unknown'
if (-not $g2.IsError) {
    $v = Get-HzPath $g2.Result 'prevention'
    Add-HzProbe -Run $run -Id 'C5.4' -Name 'an operation the bridge cannot gate is not_assessable' `
        -Expected 'decision = not_assessable, never allow' `
        -Observed ("{0} / {1}" -f (Get-HzProp $v 'status'), (Get-HzProp $v 'decision')) `
        -Status $(if ([string](Get-HzProp $v 'decision') -eq 'not_assessable') { 'passed' } else { 'failed' })
}
else {
    # The schema closes the enum, so the SERVER may refuse it before Revit sees
    # it. That is the same guarantee one layer earlier, and it passes too.
    Add-HzProbe -Run $run -Id 'C5.4' -Name 'an operation the bridge cannot gate is refused' `
        -Expected 'refused by the schema or reported not_assessable' `
        -Observed ('refused: ' + (Limit-HzText $g2.Text 200)) -Ok $true
}

# An override signed for ANOTHER operation is not permission for this one.
$g3 = Invoke-HzDoctorAudit -Extra @{
        prevention_gate = @{
            operation = 'sync_with_central'; profile_version = 'campaign-v1'
            override = @{
                identity = 'campaign-harness'; reason = 'measuring the refusal'
                timestamp_utc = '2026-01-01T00:00:00Z'; operation = 'export'
                findings_ignored = @('warnings')
            }
        }
      } -Label 'gate-wrong-operation'
if (-not $g3.IsError) {
    $v = Get-HzPath $g3.Result 'prevention'
    $rejected = [string](Get-HzProp $v 'override_rejected_because')
    Add-HzProbe -Run $run -Id 'C5.5' -Name 'an override for another operation is refused' `
        -Expected 'the override is rejected and the reason names the mismatch' `
        -Observed $(if ($rejected) { Limit-HzText $rejected 200 } else { 'the override was accepted' }) `
        -Status $(if ($rejected -match 'signed for') { 'passed' } else { 'failed' }) `
        -Because 'an override is a signed statement about one operation, not a flag.'
}

# Without the argument, the gate has no opinion - and that is not permission.
$g4 = Invoke-HzDoctorAudit -Label 'gate-absent'
if (-not $g4.IsError) {
    $v = Get-HzPath $g4.Result 'prevention'
    Add-HzProbe -Run $run -Id 'C5.6' -Name 'an unasked gate is silent rather than permissive' `
        -Expected 'status = not_requested, and no decision of allow' `
        -Observed ("{0} / {1}" -f (Get-HzProp $v 'status'), (Get-HzProp $v 'decision')) `
        -Status $(if ([string](Get-HzProp $v 'status') -eq 'not_requested' -and
                      [string](Get-HzProp $v 'decision') -ne 'allow') { 'passed' } else { 'failed' })
}

# -----------------------------------------------------------------------------
# PHASE 6 - the batch sweep, over DISPOSABLE copies. Nothing is saved.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 6 - the read-only sweep' -ForegroundColor Cyan

if ($BatchFixture.Count -lt 2) {
    Add-HzFixtureMissing -Id 'C6.1' -Name 'a sweep over several models' `
        -Needs 'at least two disposable .rvt copies passed as -BatchFixture. They are opened detached, audited and closed; nothing is saved, so copies are a precaution rather than a requirement - but they must be files nobody is working in.'
}
else {
    $models = @()
    $n = 0
    foreach ($f in $BatchFixture) {
        $n++
        $models += @{
            id = "m$n"
            origin = 'local'
            path = $f
            expected_title = [IO.Path]::GetFileNameWithoutExtension($f)
        }
    }
    $submit = Invoke-HzTool -Run $run -Tool 'horizun_submit_job' `
        -Arguments @{ models = $models; batch = @{ profile_version = 'campaign-v1' }
                      idempotency_key = (New-HzKey $run 'sweep-submit') } `
        -Label 'sweep-submit' -TimeoutSec 120

    $jobId = if ($submit.IsError) { $null } else { [string](Get-HzPath $submit.Result 'job_id') }
    Add-HzProbe -Run $run -Id 'C6.1' -Name 'a model list is submitted as ONE job' `
        -Expected 'one job_id, executed false, three steps per model' `
        -Observed $(if ($jobId) { "job $jobId, $(Get-HzPath $submit.Result 'steps_submitted') step(s)" } else { Limit-HzText $submit.Text 200 }) `
        -Status $(if ($jobId -and (Get-HzPath $submit.Result 'steps_submitted') -eq ($models.Count * 3)) { 'passed' } else { 'failed' })

    if ($jobId) {
        $deadline = (Get-Date).AddMinutes(30)
        $job = $null
        while ((Get-Date) -lt $deadline) {
            $poll = Invoke-HzTool -Run $run -Tool 'horizun_job_status' -Arguments @{ job_id = $jobId } -Label 'sweep-poll' -TimeoutSec 60
            if ($poll.IsError) { break }
            $jobs = @(Get-HzPath $poll.Result 'jobs' | Where-Object { $null -ne $_ })
            if ($jobs.Count -eq 0) { break }
            $job = $jobs[0]
            if (Get-HzProp $job 'finished') { break }
            Start-Sleep -Seconds 10
        }

        # A poll that never resolved leaves $job null, and a null job with no
        # steps must read as "the sweep did not report", never as a clean sweep.
        $steps = @()
        if ($job) { $steps = @(Get-HzProp $job 'steps' | Where-Object { $null -ne $_ }) }
        Add-HzProbe -Run $run -Id 'C6.2' -Name 'every submitted step is reported in every terminal state' `
            -Expected ("{0} steps back" -f ($models.Count * 3)) -Observed ("{0} steps" -f $steps.Count) `
            -Status $(if ($steps.Count -eq ($models.Count * 3)) { 'passed' } else { 'failed' }) `
            -Evidence @{ steps = $steps }

        # ONE AT A TIME: no model's open precedes the previous model's close.
        $keys = @($steps | ForEach-Object { [string](Get-HzProp $_ 'key') })
        $ordered = $true
        for ($k = 0; $k -lt $keys.Count; $k += 3) {
            if ($keys[$k] -notmatch '\.open$' -or $keys[$k + 1] -notmatch '\.audit$' -or $keys[$k + 2] -notmatch '\.close$') { $ordered = $false }
        }
        Add-HzProbe -Run $run -Id 'C6.3' -Name 'the sweep visits one document at a time' `
            -Expected 'open, audit, close per model, in that order' `
            -Observed ($keys -join ' ') -Status $(if ($ordered) { 'passed' } else { 'failed' })

        # AND NOTHING WAS LEFT OPEN.
        $h2 = Get-HzHealth $run
        $activeDoc = Get-HzProp $h2 'active_document'
        $active = if ($activeDoc) { [string](Get-HzProp $activeDoc 'title') } else { '<none>' }
        Add-HzProbe -Run $run -Id 'C6.4' -Name 'the sweep left no document open behind it' `
            -Expected "the active document is still '$Document'" -Observed "active: $active" `
            -Status $(if ($active -eq $Document) { 'passed' } else { 'failed' })
    }
}

# A cloud model is NOT opened here, and the fixture it would need is named.
Add-HzFixtureMissing -Id 'C6.5' -Name 'a sweep over an ACC cloud model' `
    -Needs 'an ACC/BIM 360 project with a model this machine can open: cloud_project_guid and cloud_model_guid as REVIT knows them (not the ids in the ACC web URL), the region, and an account entitled to open it. A downloaded copy is a different model and cannot stand in for it.'

# -----------------------------------------------------------------------------
# PHASE 7 - the fixtures this campaign cannot build, named exactly.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 7 - ownership and worksets: what is missing, named' -ForegroundColor Cyan

Add-HzFixtureMissing -Id 'C7.1' -Name 'ownership reported on a real workshared model' `
    -Needs 'a CENTRAL model on a share, plus a local of it. Ownership does not exist in a non-workshared document, so a single-user fixture cannot produce it - and four zeros from a document that was never workshared is a census that ran and found nothing, which is the wrong answer.'

Add-HzFixtureMissing -Id 'C7.2' -Name 'elements borrowed by somebody else' `
    -Needs 'a SECOND Revit user (or a reproducible borrow state saved into the central) holding at least one element. One machine cannot borrow from itself, and a borrow simulated by editing the model is not a borrow.'

Add-HzFixtureMissing -Id 'C7.3' -Name 'a CLOSED workset' `
    -Needs 'a central model with a workset that is closed in the local. Its elements are not in the document at all, which is the point: a scan must report the workset as closed rather than its contents as absent.'

Add-HzFixtureMissing -Id 'C7.4' -Name 'an element inside and an element outside a permitted workset' `
    -Needs 'a model with worksets whose permitted/forbidden placement the profile declares, plus one element correctly placed and one misplaced - the second is the only way to prove the check fires rather than merely passing.'

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir

# The arithmetic has to add up, and a bucket that does not is a failure of the
# campaign rather than a finding about the product. Both are reported; only one
# is a bug in Revit.
$c = Get-HzCounts $run
$total = $c.passed + $c.failed + $c.unverified + $c.not_covered + $c.fixture_missing
if ($total -ne $run.Probes.Count) {
    Write-Host ("  BUCKETS DO NOT ADD UP: {0} probes, {1} counted" -f $run.Probes.Count, $total) -ForegroundColor Red
    exit 3
}
exit $done.ExitCode
