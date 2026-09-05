#Requires -Version 5.1
<#
  EVERY CORRECTION RECIPE, EXECUTED - not read.

  horizun_apply_corrections has five acting recipes, and until now exactly one of
  them (pin an unpinned link) had ever run against a Revit. The other four were
  code-reviewed, which is a different thing: every rule in the correction cycle
  decides what to do with facts EXTRACTED from a document, and nothing offline
  can tell you whether the extraction, the child tool's rehearsal, or the
  re-audit's verdict is right.

  So this runs once, and it is built around five refusals:

    IT REFUSES TO RUN AGAINST THE WRONG BUILD. Commit, contract hash, Revit year
    and active document are demanded before the first probe. A passing matrix
    over an add-in three commits old is indistinguishable from a passing matrix
    until somebody compares SHAs, and that has happened here.

    IT REFUSES TO WAIT FOR A MODEL TO CONTAIN THE DEFECTS. It BUILDS them - four
    orphan group types, an unplaced room, a view with no template, two view
    templates - and makes the link defects with the typed link commands, so a
    green run means the recipes work rather than that today's model happened to
    be broken in the right places.

    IT REFUSES TO SIMULATE ONE. Where a defect cannot be built on this machine
    the probe is recorded fixture_missing and NAMES what is needed. It is never
    faked and then reported as Revit evidence.

    IT REFUSES TO BELIEVE A REPLY. Every apply is judged by RE-AUDITING, and
    every refusal is judged by proving nothing moved: the elements one check
    lists before and after are compared, not the sentences the command returned.

    IT NEVER SAVES. The document must be a disposable copy the integrator names,
    because this WRITES to it: it deletes group types, applies templates and pins
    links, and that is the point.

  Exit 0 when every probe passed, 1 when one failed, 2 when the gate refused or a
  probe could not be run at all - which is a different thing from a failure, and
  says so.

  THE INTEGRATOR RUNS THIS. Nothing in it can be exercised from a build machine.
#>
[CmdletBinding()]
param(
    # THE COMMIT THIS CAMPAIGN IS ABOUT. Mandatory and exact.
    [Parameter(Mandatory)][string]$RequireCommit,
    [Parameter(Mandatory)][string]$RequireContractHash,
    # A DISPOSABLE COPY. This harness WRITES: deletions, template assignments and
    # pins all land in it. Point it at a model somebody is working in and the gate
    # cannot save you - it has no way to know.
    [string]$Document = 'HZ_DOCTOR',
    [string]$RequireRevitYear = '2026',
    [string]$ArtifactDir,
    # The audit's top. It is part of a finding's identity, so every audit in this
    # run uses the same one; large enough that the fixture's own defects are never
    # past the cut.
    [int]$Top = 200,
    # TWO DISPOSABLE MODELS TO LINK. Saved by Revit 2023, so every year can link
    # them, and they stay on disk - which is the whole point: a link whose file
    # has gone unloads to NotFound and no reload recipe can be measured on it.
    [string]$LinkFixtureA = 'C:\hz-live\HZ_OLD_2023.rvt',
    [string]$LinkFixtureB = 'C:\hz-live\HZ_LINK_B.rvt'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'doctor-corrections' -Document $Document

# The five recipes the registry acts on. Kept as data so a recipe added to the
# registry and forgotten here shows up as one the bridge offers and this campaign
# never exercised - reported, not invisible.
$ACTING_RECIPES = @(
    'unpinned_links', 'links', 'views_without_template', 'orphan_group_types', 'rooms'
)

$script:Fixture = [ordered]@{
    group_type_ids           = @()
    room_id                  = $null
    spare_view_id            = $null
    template_id              = $null
    incompatible_template_id = $null
    python_available         = $false
    route                    = [ordered]@{}
}

# =============================================================================
# THE GATE. Nothing below runs until all of these are true.
# =============================================================================

function Assert-HzCorrectionsGate {
    $problems = New-Object System.Collections.ArrayList
    $health = $null
    try { $health = Get-HzHealth $run } catch { $null = $problems.Add("horizun_health did not answer: $($_.Exception.Message)") }

    if ($health) {
        $status = [string](Get-HzProp $health 'status')
        if ($status -ne 'healthy') { $null = $problems.Add("health reports status '$status', not 'healthy'") }

        $year = [string](Get-HzProp $health 'revit_version')
        if ($year -ne $RequireRevitYear) {
            $null = $problems.Add("this is Revit $year and the campaign is defined against $RequireRevitYear")
        }

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

    # The contract hash from the SERVER that answered, not from the source tree -
    # the source tree is not what this run talked to.
    $identity = Get-HzResource -Run $run -Uri 'horizun://build/identity' -Label 'build-identity'
    $hash = if ($identity) { [string](Get-HzProp $identity 'contract_hash') } else { $null }
    if (-not $hash) { $null = $problems.Add('the server published no contract hash, so the two halves cannot be shown to match') }
    elseif ($hash -ne $RequireContractHash) {
        $null = $problems.Add("the server's contract hash is '$hash' and this campaign is about '$RequireContractHash'")
    }

    if ($problems.Count -eq 0) {
        Write-Host ("  GATE OK  commit={0} revit={1} document={2} top={3}" -f
            (Limit-HzText $RequireCommit 12), $RequireRevitYear, $Document, $Top) -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host '  THE CAMPAIGN DID NOT RUN. Nothing was measured, and nothing was written:' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }
    Write-Host ''
    Write-Host '  This is a refusal, not a failure. No probe ran, so no probe passed' -ForegroundColor Yellow
    Write-Host '  and none failed; nothing about the product was learned either way.' -ForegroundColor Yellow
    exit 2
}

# =============================================================================
# TALKING TO THE CORRECTION CYCLE
# =============================================================================

<#
  One audit at the campaign's top. Every finding id in this run belongs to one
  top, so the top is never a per-call decision.
#>
function Invoke-HzAudit {
    param([string]$Label = 'audit')
    Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Label $Label -TimeoutSec 900 `
        -Arguments @{ target_document = $Document; top = $Top }
}

<#
  The finding for one check, out of an audit reply. Returns $null when the check
  produced none - which is a fact, not an error, and the caller says which.
#>
function Get-HzFinding {
    param($Audit, [Parameter(Mandatory)][string]$Check)
    if ($null -eq $Audit -or $Audit.IsError) { return $null }
    foreach ($f in @(Get-HzProp $Audit.Result 'findings')) {
        if ($null -eq $f) { continue }
        if ([string](Get-HzProp $f 'check') -eq $Check) { return $f }
    }
    $null
}

<#
  The element ids a finding's items name, read the way the bridge reads them:
  element_id, id, group_type_id, and nothing that is prose.
#>
function Get-HzFindingElementIds {
    param($Finding)
    $ids = @()
    if ($null -eq $Finding) { return $ids }
    foreach ($item in @(Get-HzProp $Finding 'items')) {
        if ($null -eq $item) { continue }
        foreach ($key in @('element_id', 'id', 'group_type_id')) {
            $raw = Get-HzProp $item $key
            if ($null -ne $raw) {
                $parsed = 0L
                if ([long]::TryParse([string]$raw, [ref]$parsed)) { $ids += $parsed }
                break
            }
        }
    }
    $ids
}

<#
  One horizun_apply_corrections call. dry_run is always explicit: a rehearsal
  that was meant to be an apply and an apply that was meant to be a rehearsal
  both look like a passing probe.
#>
function Invoke-HzCorrections {
    param(
        [Parameter(Mandatory)][string]$SetFingerprint,
        [Parameter(Mandatory)][array]$Actions,
        [Parameter(Mandatory)][bool]$DryRun,
        [Parameter(Mandatory)][string]$Label,
        [string]$Token,
        [string]$IdempotencyKey
    )
    $a = @{
        target_document         = $Document
        finding_set_fingerprint = $SetFingerprint
        actions                 = $Actions
        dry_run                 = $DryRun
    }
    if ($Token) { $a['confirmation_token'] = $Token }
    if (-not $DryRun) {
        if (-not $IdempotencyKey) { $IdempotencyKey = New-HzKey $run $Label }
        $a['idempotency_key'] = $IdempotencyKey
    }
    $call = Invoke-HzTool -Run $run -Tool 'horizun_apply_corrections' -Arguments $a -Label $Label -TimeoutSec 900
    $call | Add-Member -NotePropertyName 'IdempotencyKey' -NotePropertyValue $IdempotencyKey -Force
    $call
}

function Get-HzActionRow {
    param($Call, [int]$Index = 0)
    if ($null -eq $Call) { return $null }
    $rows = @(Get-HzPath $Call.Result 'actions')
    if ($rows.Count -le $Index) { return $null }
    $rows[$Index]
}

function Get-HzRowField {
    param($Row, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Row) { return $null }
    Get-HzProp $Row $Name
}

function Get-HzToken {
    param($Call)
    if ($null -eq $Call -or $Call.IsError) { return $null }
    [string](Get-HzPath $Call.Result 'confirmation_token')
}

<#
  A refusal's machine-readable state. FailWithDetail travels as structuredContent
  on an isError reply, which hz-call.ps1 hands back as .Result - so a refusal and
  a success are read from the same place. A probe that has to find "STALE PLAN"
  in a sentence is testing the wording rather than the behaviour.
#>
function Get-HzRefusalState {
    param($Call)
    if ($null -eq $Call) { return $null }
    $state = Get-HzProp $Call.Result 'state'
    if ($state) { return [string]$state }
    # A refusal with no structured state still has its text, and the probe that
    # called this says so rather than inventing a state.
    $null
}

<#
  WHICH ELEMENTS ONE CHECK STILL LISTS. The only honest way to say "nothing was
  written": ask the audit again and compare, rather than trusting a reply that
  said it refused.

  ALWAYS RETURNS A RECORD, never $null. Under Set-StrictMode a null here would
  throw three lines later inside whichever probe asked, and a harness that dies
  half way through a phase reports its own crash as a product silence. Read .Ok.
#>
function Measure-HzCheck {
    param([Parameter(Mandatory)][string]$Check, [string]$Label = 'measure')
    $audit = Invoke-HzAudit -Label $Label
    if ($audit.IsError) {
        return [pscustomobject]@{
            Ok = $false; Audit = $audit; Finding = $null; FindingId = $null; SetFingerprint = $null
            Count = -1; ElementIds = @(); Why = ('the audit refused: ' + (Limit-HzText $audit.Text 200))
        }
    }
    $finding = Get-HzFinding -Audit $audit -Check $Check
    if ($null -eq $finding) {
        return [pscustomobject]@{
            Ok = $false; Audit = $audit; Finding = $null; FindingId = $null
            SetFingerprint = [string](Get-HzPath $audit.Result 'finding_set_fingerprint')
            Count = -1; ElementIds = @(); Why = "the audit published no '$Check' finding"
        }
    }
    [pscustomobject]@{
        Ok             = $true
        Audit          = $audit
        Finding        = $finding
        FindingId      = [string](Get-HzProp $finding 'finding_id')
        SetFingerprint = [string](Get-HzPath $audit.Result 'finding_set_fingerprint')
        Count          = [int](Get-HzProp $finding 'count')
        ElementIds     = @(Get-HzFindingElementIds $finding)
        Why            = $null
    }
}

function Add-HzFixtureMissing {
    param([string]$Id, [string]$Name, [string]$Needs)
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Needs `
        -Observed 'the fixture is not present on this machine' -Status 'fixture_missing' `
        -Because 'simulating this and reporting it as Revit evidence would be a lie about where the number came from.'
}

<#
  A probe that could not run because the audit it needed did not answer. Distinct
  from a failure AND from a missing fixture: the model has what is needed, the
  bridge did not report it.
#>
function Add-HzUnverified {
    param([string]$Id, [string]$Name, [string]$Expected, [string]$Observed)
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Expected -Observed $Observed -Status 'unverified' `
        -Because 'the check never ran, so nothing about the product was learned - which is not a pass.'
}

# =============================================================================
Assert-HzCorrectionsGate
# =============================================================================

# -----------------------------------------------------------------------------
# PHASE 0 - the defects, built. Nothing below means anything without them, so
# each one is verified through the AUDIT rather than through the reply that
# claimed to have made it.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 0 - building the defects' -ForegroundColor Cyan

$fixtureScript = Join-Path $PSScriptRoot 'doctor-corrections-fixture.py'
$python = Invoke-HzTool -Run $run -Tool 'horizun_execute_python' -Label 'fixture' -TimeoutSec 900 -Arguments @{
    target_document = $Document
    code_path       = $fixtureScript
    idempotency_key = (New-HzKey $run 'fixture')
}
if ($python.IsError) {
    # execute_python is OFF by default and only the machine owner turns it on.
    # That is not a product failure and must not read as one.
    Add-HzProbe -Run $run -Id 'D0.1' -Name 'the defects this campaign corrects are built' `
        -Expected 'doctor-corrections-fixture.py builds orphan group types, an unplaced room, a bare view and two templates' `
        -Observed ('the bridge refused: ' + (Limit-HzText $python.Text 300)) -Status 'fixture_missing' `
        -Because 'no typed command creates an orphan group type, an unplaced room or a view template, and horizun_execute_python is disabled unless the machine owner enabled it. Turn it on with scripts\enable-execute-python.ps1 and run again.'
}
else {
    $script:Fixture.python_available = $true
    # THE REPLY PUTS IT UNDER 'output'. A script assigns __output__ and the bridge
    # publishes it as output; reading the name the SCRIPT used got $null, and the
    # whole run then reported 25 fixtures missing over a fixture that had been
    # built. Both are read here so neither spelling can lose it again.
    $built = Get-HzPath $python.Result 'output'
    if (-not $built) { $built = Get-HzPath $python.Result '__output__' }
    $script:Fixture.group_type_ids = @(Get-HzProp $built 'group_type_ids' | Where-Object { $null -ne $_ } |
                                       ForEach-Object { [long]$_ })
    foreach ($key in @('room_id', 'spare_view_id', 'template_id', 'incompatible_template_id')) {
        $script:Fixture[$key] = Get-HzProp $built $key
    }
    $script:Fixture.route['group_types'] = 'execute_python (no typed command creates a group type)'
    $script:Fixture.route['unplaced_room'] = 'execute_python (no typed command creates a room without a location)'
    $script:Fixture.route['view_template'] = 'execute_python (no typed command mints or clears a view template)'
    $script:Fixture.route['link_defects'] = 'horizun_manage_links unpin/unload (typed)'
    Add-HzProbe -Run $run -Id 'D0.1' -Name 'the defects this campaign corrects are built' `
        -Expected 'at least two orphan group types, one unplaced room, one bare view and one usable template' `
        -Observed ("status={0} group_types={1} room={2} view={3} template={4} wrong_template={5}" -f
            (Get-HzProp $built 'status'), $script:Fixture.group_type_ids.Count, $script:Fixture.room_id,
            $script:Fixture.spare_view_id, $script:Fixture.template_id, $script:Fixture.incompatible_template_id) `
        -Status $(if ($script:Fixture.group_type_ids.Count -ge 2) { 'passed' } else { 'fixture_missing' }) `
        -Because 'a harness that waits for a model to happen to contain an orphan group type reports fixture_missing on most days and passed on the rest, without anybody knowing which.' `
        -Evidence @{ fixture = $built; script = 'scripts/live/doctor-corrections-fixture.py' }
    Add-HzNote -Run $run -Text ('fixture route: ' +
        (($script:Fixture.route.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join '; '))
}

# THE LINK DEFECTS ARE MADE TYPED. Unpinning and unloading are typed commands, so
# they go through them: execute_python is the fallback for what has no typed
# route, never a shortcut past one that does.
# THE LINK FIXTURE, STAGED BY THIS HARNESS. No typed command creates a link
# TYPE, so a document with no links - or with only links whose files are gone -
# left the two link recipes unmeasured in every year. Two targets, because one
# link cannot show that a correction answers for what it named and for nothing
# else; and the first is placed TWICE, because a type is reloaded once however
# many times it is placed. Both targets are 2023-format disposable copies that
# stay on disk: a link whose file has gone unloads to NotFound, never Unloaded.
$linkStagePy = Join-Path $run.WorkDir 'stage-link-fixture.py'
@"
from Autodesk.Revit.DB import (RevitLinkOptions, RevitLinkType, RevitLinkInstance, ModelPathUtils,
                               Transaction, FilteredElementCollector, XYZ, ElementTransformUtils)
TARGETS = [r'$LinkFixtureA', r'$LinkFixtureB']
out = {'status': 'failed', 'targets': TARGETS, 'created': [], 'reused': [],
       'instances_before': 0, 'instances_after': 0, 'types_after': 0, 'why': None}
def eid(x):
    return None if x is None else (x.IntegerValue if hasattr(x, 'IntegerValue') else int(x.Value))
def instances():
    return list(FilteredElementCollector(doc).OfClass(RevitLinkInstance).ToElements())
def type_named(name):
    for inst in instances():
        t = doc.GetElement(inst.GetTypeId())
        try:
            if t is not None and t.Name and t.Name.lower() == name.lower():
                return t
        except Exception:
            pass
    return None
try:
    out['instances_before'] = len(instances())
    for target in TARGETS:
        leaf = target.split('\\')[-1]
        if type_named(leaf) is not None:
            out['reused'].append(target)
            continue
        mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(target)
        t = Transaction(doc, 'Horizun live fixture: stage a link')
        t.Start()
        res = RevitLinkType.Create(doc, mp, RevitLinkOptions(False))
        inst = RevitLinkInstance.Create(doc, res.ElementId)
        t.Commit()
        out['created'].append({'target': target, 'type_id': eid(res.ElementId), 'instance_id': eid(inst.Id)})
    first = type_named(TARGETS[0].split('\\')[-1])
    if first is not None:
        placed = [i for i in instances() if eid(i.GetTypeId()) == eid(first.Id)]
        if len(placed) < 2:
            t = Transaction(doc, 'Horizun live fixture: a second placement')
            t.Start()
            second = RevitLinkInstance.Create(doc, first.Id)
            ElementTransformUtils.MoveElement(doc, second.Id, XYZ(120.0, 0.0, 0.0))
            t.Commit()
            out['created'].append({'second_instance_id': eid(second.Id)})
    after = instances()
    out['instances_after'] = len(after)
    out['types_after'] = len(set(eid(i.GetTypeId()) for i in after))
    out['status'] = 'self_reported_verified' if out['types_after'] >= 2 else 'partial'
    if out['types_after'] < 2:
        out['why'] = 'expected at least two link types; the model holds %d' % out['types_after']
except Exception as ex:
    out['why'] = str(ex)
__output__ = out
"@ | Set-Content -LiteralPath $linkStagePy -Encoding utf8

$linkStage = $null
if ((Test-Path -LiteralPath $LinkFixtureA) -and (Test-Path -LiteralPath $LinkFixtureB)) {
    $linkStageCall = Invoke-HzTool -Run $run -Tool 'horizun_execute_python' -Label 'stage-link-fixture' -TimeoutSec 900 `
        -Arguments @{ code_path = $linkStagePy; target_document = $Document
                      idempotency_key = (New-HzKey $run 'stagelink') }
    $linkStage = if ($linkStageCall.Ok) { Get-HzProp $linkStageCall.Result 'output' } else { $null }
    Add-HzNote -Run $run -Text ("link fixture: " + $(if ($linkStage) {
        "status=$(Get-HzProp $linkStage 'status') types=$(Get-HzProp $linkStage 'types_after') instances=$(Get-HzProp $linkStage 'instances_after') why=$(Get-HzProp $linkStage 'why')"
    } else { 'not staged: ' + (Limit-HzText $linkStageCall.Text 200) }))
}
else {
    Add-HzNote -Run $run -Text ("link fixture: NOT staged - $LinkFixtureA or $LinkFixtureB is not on this machine")
}
$run.Fixture['link_fixture'] = $linkStage

$linksList = Invoke-HzTool -Run $run -Tool 'horizun_manage_links' -Label 'links-list' -TimeoutSec 300 `
    -Arguments @{ operation = 'list'; target_document = $Document }
$linkTypes = @()
if (-not $linksList.IsError) {
    foreach ($row in @(Get-HzPath $linksList.Result 'links')) { if ($null -ne $row) { $linkTypes += $row } }
}
$linkInstanceIds = @()
foreach ($row in $linkTypes) {
    foreach ($instance in @(Get-HzProp $row 'instances')) {
        if ($null -eq $instance) { continue }
        $id = Get-HzProp $instance 'instance_id'
        if ($null -ne $id) {
            $linkInstanceIds += [pscustomobject]@{
                InstanceId = [long]$id
                TypeId     = [long](Get-HzProp $row 'link_type_id')
                Pinned     = (Get-HzProp $instance 'pinned')
                Status     = [string](Get-HzProp $row 'status')
            }
        }
    }
}
Add-HzNote -Run $run -Text ("links present: {0} type(s), {1} instance(s)" -f $linkTypes.Count, $linkInstanceIds.Count)

# =============================================================================
# PHASE 1 - the audit names each defect, and names it the same way twice.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 1 - the audit, and the identity of what it found' -ForegroundColor Cyan

$a1 = Invoke-HzAudit -Label 'audit-1'
if ($a1.IsError) {
    Add-HzUnverified -Id 'D1.0' -Name 'the audit answers' -Expected 'a reply with findings' `
        -Observed (Limit-HzText $a1.Text 300)
}
else {
    Add-HzProbe -Run $run -Id 'D1.0' -Name 'the audit answers' -Expected 'a reply with findings' -Observed 'answered' -Ok $true

    $n = 0
    foreach ($check in $ACTING_RECIPES) {
        $n++
        $finding = Get-HzFinding -Audit $a1 -Check $check
        $findingId = if ($finding) { [string](Get-HzProp $finding 'finding_id') } else { $null }
        $isIssue = if ($finding) { (Get-HzProp $finding 'is_issue') -eq $true } else { $false }
        $count = if ($finding) { Get-HzProp $finding 'count' } else { $null }
        Add-HzProbe -Run $run -Id ("D1.{0}" -f $n) -Name "'$check' is published with a finding_id" `
            -Expected 'the check ran and its finding carries the id a correction has to cite' `
            -Observed $(if ($findingId) { "finding_id=$findingId is_issue=$isIssue count=$count" } else { 'the audit published no such finding' }) `
            -Status $(if ($findingId) { 'passed' } else { 'failed' }) `
            -Evidence @{ check = $check; finding_id = $findingId; is_issue = $isIssue; count = $count }
    }

    # TWO AUDITS OF AN UNCHANGED MODEL REPRODUCE THE SET. Without this a caller
    # could never re-read an audit it had already approved against, and every
    # apply would look stale.
    $a1b = Invoke-HzAudit -Label 'audit-1b'
    $fp1 = [string](Get-HzPath $a1.Result 'finding_set_fingerprint')
    $fp2 = if ($a1b.IsError) { $null } else { [string](Get-HzPath $a1b.Result 'finding_set_fingerprint') }
    Add-HzProbe -Run $run -Id 'D1.6' -Name 'two audits of an unchanged model reproduce the finding_set_fingerprint' `
        -Expected ("the same fingerprint at the same top ({0})" -f $Top) -Observed ("{0} -> {1}" -f $fp1, $fp2) `
        -Status $(if ($fp1 -and $fp1 -eq $fp2) { 'passed' } else { 'failed' }) `
        -Because 'the fingerprint is what a correction cites; if it moved on its own, nothing could ever be approved and then applied.'

    # AND A DIFFERENT top IS A DIFFERENT SET, by design rather than by accident.
    $aTop = Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Label 'audit-other-top' -TimeoutSec 900 `
        -Arguments @{ target_document = $Document; top = 1 }
    $fpTop = if ($aTop.IsError) { $null } else { [string](Get-HzPath $aTop.Result 'finding_set_fingerprint') }
    Add-HzProbe -Run $run -Id 'D1.7' -Name 'an audit at another top is a different finding set' `
        -Expected ("top is part of the identity, so top=1 does not reproduce top={0}" -f $Top) `
        -Observed ("{0} vs {1}" -f $fp1, $fpTop) `
        -Status $(if ($fpTop -and $fpTop -ne $fp1) { 'passed' } else { 'failed' }) `
        -Because 'finding ids hash over the items a finding LISTED, and the list is cut at top - two audits at different tops name the same defect with different ids and neither may cite the other.'
}

# The campaign below cites one audit at a time and re-takes it after every write,
# because an apply makes its own finding set stale by construction.
$orphanState = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-orphans'
$orphanIds = @()
if ($orphanState.Ok) {
    $orphanIds = @($orphanState.ElementIds | Where-Object { $script:Fixture.group_type_ids -contains $_ })
}

# =============================================================================
# PHASE 2 - the rehearsal: a token, executed:false, and the CHILD's own evidence.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 2 - the rehearsal' -ForegroundColor Cyan

if ($orphanIds.Count -lt 2) {
    Add-HzFixtureMissing -Id 'D2.1' -Name 'a rehearsal through the typed child' `
        -Needs 'at least two orphan group types this harness built, listed by the audit. Phase 0 could not build them, so there is nothing here to rehearse a deletion of.'
}
else {
    $rehearsal = Invoke-HzCorrections -SetFingerprint $orphanState.SetFingerprint -DryRun $true -Label 'rehearse-orphan' `
        -Actions @(@{ finding_id = $orphanState.FindingId; element_ids = @($orphanIds[0]) })

    $token = Get-HzToken $rehearsal
    $executed = if ($rehearsal.IsError) { $null } else { Get-HzPath $rehearsal.Result 'executed' }
    Add-HzProbe -Run $run -Id 'D2.1' -Name 'a clean rehearsal issues a token and executed is false' `
        -Expected 'confirmation_token present, executed=false, rehearsed_cleanly=true' `
        -Observed $(if ($rehearsal.IsError) { Limit-HzText $rehearsal.Text 300 } else {
            "token=$([bool]$token) executed=$executed rehearsed_cleanly=$(Get-HzPath $rehearsal.Result 'rehearsed_cleanly')" }) `
        -Status $(if ($token -and $executed -eq $false) { 'passed' } else { 'failed' })

    $row = Get-HzActionRow $rehearsal
    $steps = @(Get-HzRowField $row 'steps')
    $childState = if ($steps.Count) { [string](Get-HzPath $steps[0] @('rehearsal', 'application_state')) } else { $null }
    $childTool = if ($steps.Count) { [string](Get-HzProp $steps[0] 'tool') } else { $null }
    Add-HzProbe -Run $run -Id 'D2.2' -Name 'the rehearsal went THROUGH the typed child and carries its evidence' `
        -Expected 'a step naming horizun_delete_verified whose own rehearsal state comes back' `
        -Observed ("tool={0} child_state={1}" -f $childTool, $childState) `
        -Status $(if ($childTool -eq 'horizun_delete_verified' -and $childState) { 'passed' } else { 'failed' }) `
        -Because 'generating arguments is not a rehearsal; the child resolving them is. A reply with no child evidence proves only that a JSON object was assembled.' `
        -Evidence @{ steps = $steps }

    # AND IT WROTE NOTHING. Read from the audit rather than from the reply.
    $afterRehearsal = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-after-rehearsal'
    Add-HzProbe -Run $run -Id 'D2.3' -Name 'a rehearsal changed nothing in the model' `
        -Expected ("orphan_group_types still counts {0}" -f $orphanState.Count) `
        -Observed $(if ($afterRehearsal.Ok) { "counts $($afterRehearsal.Count)" } else { $afterRehearsal.Why }) `
        -Status $(if ($afterRehearsal.Ok -and $afterRehearsal.Count -eq $orphanState.Count) { 'passed' } else { 'failed' })
}

# =============================================================================
# PHASE 3 - scope. A subset acts on the subset; a wider set is refused.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 3 - the scope is exactly what was named' -ForegroundColor Cyan

$scopeBefore = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-scope'
if ($orphanIds.Count -lt 2 -or -not $scopeBefore.Ok) {
    Add-HzFixtureMissing -Id 'D3.1' -Name 'narrowing and widening over a real finding' `
        -Needs 'at least two orphan group types this harness built and an audit that lists them, so a subset is distinguishable from the whole.'
}
else {
    $narrowed = Invoke-HzCorrections -SetFingerprint $scopeBefore.SetFingerprint -DryRun $true -Label 'scope-narrow' `
        -Actions @(@{ finding_id = $scopeBefore.FindingId; element_ids = @($orphanIds[0]) })
    $narrowRow = Get-HzActionRow $narrowed
    $selected = @(Get-HzRowField $narrowRow 'selected_element_ids')
    Add-HzProbe -Run $run -Id 'D3.1' -Name 'a subset selects the subset and nothing else' `
        -Expected ("selected_element_ids = [{0}] out of the {1} the finding named" -f $orphanIds[0], $scopeBefore.ElementIds.Count) `
        -Observed ("[{0}]" -f ($selected -join ',')) `
        -Status $(if ($selected.Count -eq 1 -and [long]$selected[0] -eq [long]$orphanIds[0]) { 'passed' } else { 'failed' })

    # A WIDER SET. An id this finding never named is the quiet failure a well-typed
    # call cannot show you: it passes every schema and does something nobody agreed to.
    $stranger = 999999999
    $widened = Invoke-HzCorrections -SetFingerprint $scopeBefore.SetFingerprint -DryRun $true -Label 'scope-widen' `
        -Actions @(@{ finding_id = $scopeBefore.FindingId; element_ids = @($orphanIds[0], $stranger) })
    $wideRow = Get-HzActionRow $widened
    $wideCode = [string](Get-HzRowField $wideRow 'refusal_code')
    $wideToken = Get-HzToken $widened
    Add-HzProbe -Run $run -Id 'D3.2' -Name 'an id the finding never named refuses as scope_widened' `
        -Expected 'refusal_code = scope_widened, state unsafe, no token' `
        -Observed ("state={0} refusal_code={1} token={2}" -f (Get-HzRowField $wideRow 'state'), $wideCode, [bool]$wideToken) `
        -Status $(if ($wideCode -eq 'scope_widened' -and -not $wideToken) { 'passed' } else { 'failed' }) `
        -Because 'a proposal that widens from four walls to all walls is still well-typed and still does something nobody agreed to.'

    $afterWiden = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-after-widen'
    Add-HzProbe -Run $run -Id 'D3.3' -Name 'the widened call wrote nothing' `
        -Expected ("orphan_group_types still counts {0}" -f $scopeBefore.Count) `
        -Observed $(if ($afterWiden.Ok) { "counts $($afterWiden.Count)" } else { $afterWiden.Why }) `
        -Status $(if ($afterWiden.Ok -and $afterWiden.Count -eq $scopeBefore.Count) { 'passed' } else { 'failed' })
}

# =============================================================================
# PHASE 4 - requires_input, named in the field and not only in the sentence.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 4 - the inputs a recipe asks for' -ForegroundColor Cyan

$spareView = $script:Fixture.spare_view_id
$pairAudit = Invoke-HzAudit -Label 'audit-pair'
$pairViews = Get-HzFinding -Audit $pairAudit -Check 'views_without_template'
$pairOrphans = Get-HzFinding -Audit $pairAudit -Check 'orphan_group_types'
$pairSet = if ($pairAudit.IsError) { $null } else { [string](Get-HzPath $pairAudit.Result 'finding_set_fingerprint') }
$pairOrphanIds = @(Get-HzFindingElementIds $pairOrphans | Where-Object { $script:Fixture.group_type_ids -contains $_ })
$viewSelectable = ($spareView -and $pairViews -and $pairSet -and
                   (@(Get-HzFindingElementIds $pairViews) -contains [long]$spareView))

if (-not $viewSelectable) {
    Add-HzFixtureMissing -Id 'D4.1' -Name 'views_without_template asks for its template' `
        -Needs ("a printable view with NO view template that this harness built and the audit lists at top={0}. Phase 0 could not build one, or the audit did not list it." -f $Top)
    Add-HzFixtureMissing -Id 'D4.2' -Name 'the other action in the same call still rehearses' `
        -Needs 'the same bare view as D4.1, beside a second correctable finding.'
    Add-HzFixtureMissing -Id 'D4.3' -Name 'one requires_input action withholds the WHOLE token' `
        -Needs 'the same bare view as D4.1.'
    Add-HzFixtureMissing -Id 'D4.4' -Name 'supplying the template makes the action actionable' `
        -Needs 'the same bare view as D4.1, plus a compatible view template.'
    Add-HzFixtureMissing -Id 'D4.5' -Name 'an input the recipe never asked for is refused' `
        -Needs 'the same bare view as D4.1.'
}
else {
    $actions = @(@{ finding_id = [string](Get-HzProp $pairViews 'finding_id'); element_ids = @([long]$spareView) })
    if ($pairOrphanIds.Count -ge 2) {
        $actions += @{ finding_id = [string](Get-HzProp $pairOrphans 'finding_id'); element_ids = @($pairOrphanIds[1]) }
    }

    $missing = Invoke-HzCorrections -SetFingerprint $pairSet -DryRun $true -Label 'input-missing' -Actions $actions
    $viewRow = Get-HzActionRow $missing 0
    $required = @(Get-HzRowField $viewRow 'required_inputs')
    $missingToken = Get-HzToken $missing
    Add-HzProbe -Run $run -Id 'D4.1' -Name 'a missing input is requires_input NAMING it in required_inputs' `
        -Expected 'state=requires_input and required_inputs contains template_view_id' `
        -Observed ("state={0} required_inputs=[{1}] why={2}" -f (Get-HzRowField $viewRow 'state'), ($required -join ','),
                   (Limit-HzText ([string](Get-HzRowField $viewRow 'why')) 160)) `
        -Status $(if ([string](Get-HzRowField $viewRow 'state') -eq 'requires_input' -and $required -contains 'template_view_id') { 'passed' } else { 'failed' }) `
        -Because 'the prose naming the input while the machine-readable field stays empty is the wrong way round: a client branching on data could not tell "which template" from any other refusal.' `
        -Evidence @{ action = $viewRow }

    if ($actions.Count -ge 2) {
        $otherRow = Get-HzActionRow $missing 1
        Add-HzProbe -Run $run -Id 'D4.2' -Name 'the other action in the same call still rehearses' `
            -Expected 'the orphan action reaches state=rehearsed while the view action asks for its input' `
            -Observed ("state={0}" -f (Get-HzRowField $otherRow 'state')) `
            -Status $(if ([string](Get-HzRowField $otherRow 'state') -eq 'rehearsed') { 'passed' } else { 'failed' })
    }
    else {
        Add-HzFixtureMissing -Id 'D4.2' -Name 'the other action in the same call still rehearses' `
            -Needs 'a second correctable finding beside views_without_template - two orphan group types were not available in the same audit.'
    }

    Add-HzProbe -Run $run -Id 'D4.3' -Name 'one requires_input action withholds the WHOLE token' `
        -Expected 'no confirmation_token, and confirmation_withheld=true' `
        -Observed ("token={0} withheld={1}" -f [bool]$missingToken,
                   $(if ($missing.IsError) { 'n/a' } else { Get-HzPath $missing.Result 'confirmation_withheld' })) `
        -Status $(if (-not $missingToken) { 'passed' } else { 'failed' }) `
        -Because 'a token over "the ones that worked" authorises a set nobody read as such.'

    if (-not $script:Fixture.template_id) {
        Add-HzFixtureMissing -Id 'D4.4' -Name 'supplying the template makes the action actionable' `
            -Needs 'a view template compatible with the bare view. Phase 0 could not mint one from the plan it duplicated.'
        Add-HzFixtureMissing -Id 'D4.5' -Name 'an input the recipe never asked for is refused' `
            -Needs 'the same compatible template as D4.4.'
    }
    else {
        $answeredState = Measure-HzCheck -Check 'views_without_template' -Label 'audit-answered'
        if (-not $answeredState.Ok) {
            Add-HzUnverified -Id 'D4.4' -Name 'supplying the template makes the action actionable' `
                -Expected 'an audit that still lists the bare view' -Observed $answeredState.Why
            Add-HzUnverified -Id 'D4.5' -Name 'an input the recipe never asked for is refused' `
                -Expected 'an audit that still lists the bare view' -Observed $answeredState.Why
        }
        else {
            $answered = Invoke-HzCorrections -SetFingerprint $answeredState.SetFingerprint -DryRun $true -Label 'input-answered' `
                -Actions @(@{ finding_id = $answeredState.FindingId; element_ids = @([long]$spareView)
                              inputs = @{ template_view_id = [long]$script:Fixture.template_id } })
            $answeredRow = Get-HzActionRow $answered
            $answeredToken = Get-HzToken $answered
            Add-HzProbe -Run $run -Id 'D4.4' -Name 'supplying the template makes the same action actionable' `
                -Expected 'state=rehearsed, a token is issued, and the child call is a horizun_manage_views apply_template' `
                -Observed $(if ($answered.IsError) { Limit-HzText $answered.Text 300 } else {
                    "state=$(Get-HzRowField $answeredRow 'state') tool=$(Get-HzRowField $answeredRow 'tool') token=$([bool]$answeredToken)" }) `
                -Status $(if ([string](Get-HzRowField $answeredRow 'state') -eq 'rehearsed' -and
                              [string](Get-HzRowField $answeredRow 'tool') -eq 'horizun_manage_views' -and
                              $answeredToken) { 'passed' } else { 'failed' }) `
                -Evidence @{ action = $answeredRow }

            # AN INPUT THE RECIPE NEVER ASKED FOR is a field added to a typed call
            # the registry did not review - the same door the registry exists to close.
            $intruder = Invoke-HzCorrections -SetFingerprint $answeredState.SetFingerprint -DryRun $true -Label 'input-intruder' `
                -Actions @(@{ finding_id = $answeredState.FindingId; element_ids = @([long]$spareView)
                              inputs = @{ template_view_id = [long]$script:Fixture.template_id; operation = 'set_crop' } })
            $intruderRow = Get-HzActionRow $intruder
            $intruderState = [string](Get-HzRowField $intruderRow 'state')
            Add-HzProbe -Run $run -Id 'D4.5' -Name 'an input the recipe never asked for is refused' `
                -Expected 'state=requires_input or unsafe, no token, and the reason names the input' `
                -Observed ("state={0} why={1}" -f $intruderState,
                           (Limit-HzText ([string](Get-HzRowField $intruderRow 'why')) 160)) `
                -Status $(if ($intruderState -in @('requires_input', 'unsafe') -and -not (Get-HzToken $intruder)) { 'passed' } else { 'failed' }) `
                -Because 'merging an input the registry never declared lets a caller add a field to a typed call nobody reviewed.'
        }
    }
}

# =============================================================================
# PHASE 5 - the destructive recipes name their elements, and touch nothing else.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 5 - a delete names what it deletes' -ForegroundColor Cyan

$destructive = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-destructive'
if ($orphanIds.Count -lt 2 -or -not $destructive.Ok) {
    Add-HzFixtureMissing -Id 'D5.1' -Name 'a delete with no element_ids is refused' `
        -Needs 'orphan group types this harness built, listed by an audit that answered.'
}
else {
    $unnamed = Invoke-HzCorrections -SetFingerprint $destructive.SetFingerprint -DryRun $true -Label 'delete-unnamed' `
        -Actions @(@{ finding_id = $destructive.FindingId })
    $unnamedRow = Get-HzActionRow $unnamed
    $unnamedRequired = @(Get-HzRowField $unnamedRow 'required_inputs')
    $unnamedToken = Get-HzToken $unnamed
    $unnamedSteps = @(Get-HzRowField $unnamedRow 'steps')
    Add-HzProbe -Run $run -Id 'D5.1' -Name 'a delete that names no element_ids is requires_input, never "all of them"' `
        -Expected 'state=requires_input, required_inputs contains element_ids, no token, no steps' `
        -Observed ("state={0} required_inputs=[{1}] token={2} steps={3}" -f (Get-HzRowField $unnamedRow 'state'),
                   ($unnamedRequired -join ','), [bool]$unnamedToken, $unnamedSteps.Count) `
        -Status $(if ([string](Get-HzRowField $unnamedRow 'state') -eq 'requires_input' -and
                      $unnamedRequired -contains 'element_ids' -and -not $unnamedToken -and
                      $unnamedSteps.Count -eq 0) { 'passed' } else { 'failed' }) `
        -Because 'an absent selection is not "all of them" - that is the argument an empty actions array is refused with, one level down and with an irreversible delete on the other side.'
}

$roomState = Measure-HzCheck -Check 'rooms' -Label 'audit-rooms'
$roomId = $script:Fixture.room_id
$roomUsable = ($roomId -and $roomState.Ok -and (@($roomState.ElementIds) -contains [long]$roomId))
if (-not $roomUsable) {
    Add-HzFixtureMissing -Id 'D5.2' -Name 'the unplaced-room delete asks for its ids too' `
        -Needs ("a room with NO location that the rooms check lists at top={0}. Phase 0 either could not create one - a document with no phase, or an API refusal, is recorded in the fixture notes - or the check did not report it." -f $Top)
    Add-HzFixtureMissing -Id 'D5.3' -Name 'naming the unplaced room rehearses a typed delete of exactly it' `
        -Needs 'the same unplaced room as D5.2.'
    Add-HzFixtureMissing -Id 'D5.4' -Name 'rooms that are placed but not enclosed are excluded, not corrected' `
        -Needs 'the same unplaced room as D5.2.'
}
else {
    $roomUnnamed = Invoke-HzCorrections -SetFingerprint $roomState.SetFingerprint -DryRun $true -Label 'rooms-unnamed' `
        -Actions @(@{ finding_id = $roomState.FindingId })
    $roomRow = Get-HzActionRow $roomUnnamed
    $roomRequired = @(Get-HzRowField $roomRow 'required_inputs')
    Add-HzProbe -Run $run -Id 'D5.2' -Name 'the unplaced-room delete asks for its ids too' `
        -Expected 'state=requires_input naming element_ids, and no token' `
        -Observed ("state={0} required_inputs=[{1}]" -f (Get-HzRowField $roomRow 'state'), ($roomRequired -join ',')) `
        -Status $(if ([string](Get-HzRowField $roomRow 'state') -eq 'requires_input' -and
                      $roomRequired -contains 'element_ids' -and -not (Get-HzToken $roomUnnamed)) { 'passed' } else { 'failed' })

    $roomNamed = Invoke-HzCorrections -SetFingerprint $roomState.SetFingerprint -DryRun $true -Label 'rooms-named' `
        -Actions @(@{ finding_id = $roomState.FindingId; element_ids = @([long]$roomId) })
    $roomNamedRow = Get-HzActionRow $roomNamed
    $roomSteps = @(Get-HzRowField $roomNamedRow 'steps')
    $roomToken = Get-HzToken $roomNamed
    $roomStepTool = if ($roomSteps.Count) { [string](Get-HzProp $roomSteps[0] 'tool') } else { $null }
    Add-HzProbe -Run $run -Id 'D5.3' -Name 'naming the unplaced room rehearses a typed delete of exactly it' `
        -Expected 'state=rehearsed, one horizun_delete_verified step over that one id, and a token' `
        -Observed ("state={0} steps={1} tool={2} token={3}" -f (Get-HzRowField $roomNamedRow 'state'),
                   $roomSteps.Count, $roomStepTool, [bool]$roomToken) `
        -Status $(if ([string](Get-HzRowField $roomNamedRow 'state') -eq 'rehearsed' -and
                      $roomStepTool -eq 'horizun_delete_verified' -and $roomToken) { 'passed' } else { 'failed' }) `
        -Evidence @{ action = $roomNamedRow }

    # THE ROOM THAT IS PLACED BUT NOT ENCLOSED is excluded by the typed
    # problem_code and never by the sentence beside it. On a model with none, the
    # empty list is the honest observation and the probe says which case it saw.
    $excluded = @(Get-HzRowField $roomNamedRow 'excluded_by_filter')
    $unenclosed = @()
    foreach ($item in @(Get-HzProp $roomState.Finding 'items')) {
        if ([string](Get-HzProp $item 'problem_code') -eq 'not_enclosed') { $unenclosed += $item }
    }
    Add-HzProbe -Run $run -Id 'D5.4' -Name 'rooms that are placed but not enclosed are excluded, not corrected' `
        -Expected 'every room whose problem_code is not "unplaced" appears in excluded_by_filter and in no step' `
        -Observed ("unenclosed in finding={0} excluded_by_filter=[{1}]" -f $unenclosed.Count, ($excluded -join ',')) `
        -Status $(if ($excluded.Count -eq $unenclosed.Count) { 'passed' } else { 'failed' }) `
        -Because 'the rooms finding names two different problems and only one is a deletion; the filter reads the typed code, never the sentence.' `
        -Evidence @{ excluded_by_filter = $excluded; unenclosed = $unenclosed }
}

# =============================================================================
# PHASE 6 - the apply, and the re-audit that judges it.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 6 - apply, then re-audit' -ForegroundColor Cyan

$applied = $null
$appliedToken = $null
$appliedId = $null
$appliedFindingId = $null
$beforeApply = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-before-apply'
if ($orphanIds.Count -lt 2 -or -not $beforeApply.Ok) {
    Add-HzFixtureMissing -Id 'D6.1' -Name 'an apply judged by the re-audit' `
        -Needs 'at least two orphan group types, so one can be deleted and the other proved untouched.'
}
else {
    $appliedId = $orphanIds[0]
    $appliedFindingId = $beforeApply.FindingId
    $left = @($beforeApply.ElementIds | Where-Object { $_ -ne $appliedId })

    $rehearse = Invoke-HzCorrections -SetFingerprint $beforeApply.SetFingerprint -DryRun $true -Label 'apply-rehearse' `
        -Actions @(@{ finding_id = $appliedFindingId; element_ids = @($appliedId) })
    $appliedToken = Get-HzToken $rehearse

    if (-not $appliedToken) {
        Add-HzUnverified -Id 'D6.1' -Name 'the apply consumes the token and writes' `
            -Expected 'the rehearsal issues a token to spend' -Observed (Limit-HzText $rehearse.Text 300)
    }
    else {
        $key = New-HzKey $run 'apply-orphan'
        $applied = Invoke-HzCorrections -SetFingerprint $beforeApply.SetFingerprint -DryRun $false -Label 'apply-orphan' `
            -Token $appliedToken -IdempotencyKey $key `
            -Actions @(@{ finding_id = $appliedFindingId; element_ids = @($appliedId) })

        $appliedRow = Get-HzActionRow $applied
        Add-HzProbe -Run $run -Id 'D6.1' -Name 'the apply consumes the token and reports the action applied' `
            -Expected 'executed=true and the action state is applied' `
            -Observed $(if ($applied.IsError) { Limit-HzText $applied.Text 300 } else {
                "executed=$(Get-HzPath $applied.Result 'executed') state=$(Get-HzRowField $appliedRow 'state')" }) `
            -Status $(if (-not $applied.IsError -and [string](Get-HzRowField $appliedRow 'state') -eq 'applied') { 'passed' } else { 'failed' })

        $rows = @(Get-HzPath $applied.Result @('re_audit', 'rows'))
        $outcome = if ($rows.Count) { [string](Get-HzProp $rows[0] 'outcome') } else { $null }
        $correctedIds = if ($rows.Count) { @(Get-HzPath $rows[0] @('elements', 'corrected')) } else { @() }
        Add-HzProbe -Run $run -Id 'D6.2' -Name 'the re-audit reports corrected for the element that was acted on' `
            -Expected ("outcome=corrected with {0} in elements.corrected" -f $appliedId) `
            -Observed ("outcome={0} corrected=[{1}]" -f $outcome, ($correctedIds -join ',')) `
            -Status $(if ($outcome -eq 'corrected' -and ($correctedIds -contains $appliedId)) { 'passed' } else { 'failed' }) `
            -Because 'the verdict comes from re-running the audit, not from counting the calls that were made.' `
            -Evidence @{ re_audit = $rows }

        # AND THE ONES NOBODY NAMED ARE STILL THERE. The scope claim measured from
        # the far side: a narrowed correction that deleted the lot would report
        # corrected just as happily.
        $afterApply = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-after-apply'
        $survivors = if ($afterApply.Ok) { @($afterApply.ElementIds) } else { @() }
        $allLeftSurvive = $afterApply.Ok
        foreach ($id in $left) { if ($survivors -notcontains $id) { $allLeftSurvive = $false } }
        Add-HzProbe -Run $run -Id 'D6.3' -Name 'the elements the caller did not name are still listed' `
            -Expected ("the {0} unnamed orphan group type(s) are all still reported, and the named one is not" -f $left.Count) `
            -Observed ("named={0} still listed=[{1}]" -f $appliedId, ($survivors -join ',')) `
            -Status $(if ($allLeftSurvive -and $survivors -notcontains $appliedId) { 'passed' } else { 'failed' }) `
            -Because 'a correction that quietly widened would report "corrected" for what it was asked about and say nothing about the rest.'

        # THE SET IS STALE BY CONSTRUCTION AFTER AN APPLY, and the reply says so
        # rather than leaving a caller to spend the fingerprint again.
        $means = [string](Get-HzPath $applied.Result @('re_audit', 'means'))
        Add-HzProbe -Run $run -Id 'D6.4' -Name 'the reply states that its own finding set is now stale' `
            -Expected 'the re_audit block says the finding_set_fingerprint is stale by construction' `
            -Observed (Limit-HzText $means 200) `
            -Status $(if ($means -match 'stale by construction') { 'passed' } else { 'failed' })

        # WHAT idempotency_key DOES, held to what it does. Measured 2026-09-03:
        # the DISPATCHER's durable ledger records this call like every other
        # mutating one, so the shared sentence is simply true here. The command
        # used to publish a second block claiming the opposite; there is one now.
        $idem = Get-HzPath $applied.Result 'idempotency'
        $idemKey = if ($idem) { [string](Get-HzProp $idem 'key') } else { $null }
        $executedHere = if ($idem) { Get-HzProp $idem 'command_executed_in_this_call' } else { $null }
        Add-HzProbe -Run $run -Id 'D6.5' -Name 'the reply carries ONE idempotency block and it is the dispatcher''s' `
            -Expected 'the key is echoed, the block reports that this call executed, and no second block contradicts it' `
            -Observed $(if ($idem) { "key=$idemKey status=$(Get-HzProp $idem 'status') executed_in_this_call=$executedHere replay=$(Get-HzProp $idem 'replay')" } else { 'no idempotency block' }) `
            -Status $(if ($idem -and $idemKey -eq $key -and $executedHere -eq $true -and
                          $null -eq (Get-HzProp $idem 'replay')) { 'passed' } else { 'failed' }) `
            -Because 'a command that publishes its own idempotency block beside the dispatcher''s has two answers to one question, and on a replay they disagreed.'
    }
}

# =============================================================================
# PHASE 7 - the spent token, replayed.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 7 - replaying what was already applied' -ForegroundColor Cyan

if (-not $applied -or $applied.IsError -or -not $appliedToken) {
    Add-HzFixtureMissing -Id 'D7.1' -Name 'replaying a spent token' `
        -Needs 'an apply that succeeded in phase 6, whose token and idempotency_key can then be re-sent.'
    Add-HzFixtureMissing -Id 'D7.2' -Name 'the re-sent apply names WHY rather than failing generically' `
        -Needs 'the same applied call as D7.1.'
    Add-HzFixtureMissing -Id 'D7.3' -Name 'the re-sent apply wrote nothing' `
        -Needs 'the same applied call as D7.1.'
}
else {
    $beforeReplay = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-before-replay'
    $replay = Invoke-HzCorrections -SetFingerprint ([string](Get-HzPath $applied.Result 'finding_set_fingerprint')) `
        -DryRun $false -Label 'replay' -Token $appliedToken -IdempotencyKey $applied.IdempotencyKey `
        -Actions @(@{ finding_id = $appliedFindingId; element_ids = @($appliedId) })

    # THE SAME KEY IS A REPLAY, AND THAT IS THE POINT. Measured 2026-09-03: the
    # dispatcher answers an identical re-send from its durable ledger, reports
    # command_executed_in_this_call = false, and the command never runs - which
    # is at-most-once doing its job. What the SINGLE-USE TOKEN protects against
    # is the same actions under a NEW key, and D7.4 measures that separately.
    $replayState = Get-HzRefusalState $replay
    $replayIdem = Get-HzPath $replay.Result 'idempotency'
    $replayedByLedger = $replayIdem -and [string](Get-HzProp $replayIdem 'status') -eq 'replayed' -and
                        (Get-HzProp $replayIdem 'command_executed_in_this_call') -eq $false
    Add-HzProbe -Run $run -Id 'D7.1' -Name 'the identical apply, re-sent under the same key, runs nothing' `
        -Expected 'the durable ledger replays the recorded reply and the command does not execute again' `
        -Observed ("is_error={0} ledger_status={1} executed_in_this_call={2}" -f $replay.IsError,
                   $(if ($replayIdem) { Get-HzProp $replayIdem 'status' } else { 'none' }),
                   $(if ($replayIdem) { Get-HzProp $replayIdem 'command_executed_in_this_call' } else { 'none' })) `
        -Status $(if ($replayedByLedger -or $replay.IsError) { 'passed' } else { 'failed' }) `
        -Because 'a retry that re-ran the apply would delete a second time; a retry that is refused and a retry that is replayed both prevent it, and the reply says which happened.'

    # AND THE SAME ACTIONS UNDER A NEW KEY: this is where the single-use token
    # and the pre-apply re-check are measured, with no ledger to hide behind.
    $fresh = Invoke-HzCorrections -SetFingerprint ([string](Get-HzPath $applied.Result 'finding_set_fingerprint')) `
        -DryRun $false -Label 'replay-new-key' -Token $appliedToken `
        -IdempotencyKey ($applied.IdempotencyKey + '-fresh') `
        -Actions @(@{ finding_id = $appliedFindingId; element_ids = @($appliedId) })
    $freshState = Get-HzRefusalState $fresh
    Add-HzProbe -Run $run -Id 'D7.2' -Name 'the same actions under a NEW key are refused, and the refusal names why' `
        -Expected 'a machine-readable state: stale_plan or refused - the token is spent and the finding set moved' `
        -Observed ("is_error={0} state={1} text={2}" -f $fresh.IsError, $freshState, (Limit-HzText $fresh.Text 160)) `
        -Status $(if ($fresh.IsError -and $freshState -in @('stale_plan', 'refused')) { 'passed' } else { 'failed' })

    $afterReplay = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-after-replay'
    Add-HzProbe -Run $run -Id 'D7.3' -Name 'the re-sent apply wrote nothing' `
        -Expected ("orphan_group_types still counts {0}" -f $beforeReplay.Count) `
        -Observed $(if ($afterReplay.Ok) { "counts $($afterReplay.Count)" } else { $afterReplay.Why }) `
        -Status $(if ($afterReplay.Ok -and $beforeReplay.Ok -and $afterReplay.Count -eq $beforeReplay.Count) { 'passed' } else { 'failed' })
}

# =============================================================================
# PHASE 8 - a stale plan: the model moves between the rehearsal and the apply.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 8 - the model moved' -ForegroundColor Cyan

$staleState = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-stale'
$remaining = @()
if ($staleState.Ok) {
    $remaining = @($staleState.ElementIds | Where-Object { $script:Fixture.group_type_ids -contains $_ })
}

if ($remaining.Count -lt 2) {
    Add-HzFixtureMissing -Id 'D8.1' -Name 'an apply refused because the model moved' `
        -Needs 'two orphan group types still standing: one to rehearse a correction over, one to delete behind its back.'
    Add-HzFixtureMissing -Id 'D8.2' -Name 'the stale refusal names the finding that moved' `
        -Needs 'the same pair as D8.1.'
    Add-HzFixtureMissing -Id 'D8.3' -Name 'the stale apply wrote nothing' `
        -Needs 'the same pair as D8.1.'
}
else {
    $target = $remaining[0]
    $mover = $remaining[1]
    $staleRehearsal = Invoke-HzCorrections -SetFingerprint $staleState.SetFingerprint -DryRun $true -Label 'stale-rehearse' `
        -Actions @(@{ finding_id = $staleState.FindingId; element_ids = @($target) })
    $staleToken = Get-HzToken $staleRehearsal

    # THE MODEL MOVES, through a typed command and behind the correction's back -
    # exactly what happens when somebody else is working in the file.
    $mv = Invoke-HzWrite -Run $run -Tool 'horizun_delete_verified' -Label 'stale-mover' -AllowRefusal -Arguments @{
        target_document = $Document; mode = 'ids'; ids = @($mover)
    }
    $moverText = if ($null -ne $mv.Apply) { $mv.Apply.Text } else { $mv.Dry.Text }

    if (-not $mv.Ok -or -not $staleToken) {
        $why = if (-not $staleToken) { 'the rehearsal issued no token' } else { 'the mover delete did not land: ' + (Limit-HzText $moverText 200) }
        foreach ($probe in @(
            @{ Id = 'D8.1'; Name = 'an apply refused because the model moved' },
            @{ Id = 'D8.2'; Name = 'the stale refusal names the finding that moved' },
            @{ Id = 'D8.3'; Name = 'the stale apply wrote nothing' })) {
            Add-HzUnverified -Id $probe.Id -Name $probe.Name `
                -Expected 'a rehearsed correction, then a typed delete that moves the model behind it' -Observed $why
        }
    }
    else {
        $stale = Invoke-HzCorrections -SetFingerprint $staleState.SetFingerprint -DryRun $false -Label 'stale-apply' `
            -Token $staleToken -Actions @(@{ finding_id = $staleState.FindingId; element_ids = @($target) })
        $state8 = Get-HzRefusalState $stale
        Add-HzProbe -Run $run -Id 'D8.1' -Name 'an apply over a model that moved refuses as stale_plan' `
            -Expected 'state=stale_plan' `
            -Observed ("is_error={0} state={1} text={2}" -f $stale.IsError, $state8, (Limit-HzText $stale.Text 240)) `
            -Status $(if ($stale.IsError -and $state8 -eq 'stale_plan') { 'passed' } else { 'failed' }) `
            -Because 'the token still matched. What changed is the model, and a correction approved against one set of findings must not be spent on another.'

        Add-HzProbe -Run $run -Id 'D8.2' -Name 'the stale refusal names the finding that moved' `
            -Expected 'the drift mentions orphan_group_types and the finding id that was approved' `
            -Observed (Limit-HzText $stale.Text 300) `
            -Status $(if ($stale.Text -match 'orphan_group_types' -and $stale.Text -match 'approved:') { 'passed' } else { 'failed' })

        $afterStale = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-after-stale'
        $stillThere = $afterStale.Ok -and (@($afterStale.ElementIds) -contains $target)
        Add-HzProbe -Run $run -Id 'D8.3' -Name 'the stale apply wrote nothing' `
            -Expected ("group type {0} is still listed as an orphan" -f $target) `
            -Observed $(if ($afterStale.Ok) { "orphans now [$(@($afterStale.ElementIds) -join ',')]" } else { $afterStale.Why }) `
            -Status $(if ($stillThere) { 'passed' } else { 'failed' })
    }
}

# =============================================================================
# PHASE 9 - one call, one action applied and one failed. rollback_scope, tested.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 9 - per-action rollback, induced' -ForegroundColor Cyan

<#
  THE ONLY FAILURE THAT CAN BE INDUCED FROM OUTSIDE.

  The apply RE-REHEARSES every action before it writes, so anything an outsider
  can break between the rehearsal and the apply is caught by that re-rehearsal
  and refuses the WHOLE call - a stronger guarantee than per-action rollback, and
  the one phase 8 measures. What is left is a child whose own dry run passes and
  whose apply fails, and horizun_manage_views has exactly one: apply_template
  checks that template_view_id IS a template, not that Revit will accept it on
  THIS view. A template minted from a 3D view rehearses onto a floor plan and
  throws when it is assigned.

  Both outcomes are recorded honestly. If the rehearsal catches it, that is a
  better product and this probe says so instead of failing.
#>
$mixedAudit = Invoke-HzAudit -Label 'audit-mixed'
$mixedSet = if ($mixedAudit.IsError) { $null } else { [string](Get-HzPath $mixedAudit.Result 'finding_set_fingerprint') }
$mixedOrphans = Get-HzFinding -Audit $mixedAudit -Check 'orphan_group_types'
$mixedViews = Get-HzFinding -Audit $mixedAudit -Check 'views_without_template'
$mixedOrphanIds = @(Get-HzFindingElementIds $mixedOrphans | Where-Object { $script:Fixture.group_type_ids -contains $_ })
$mixedViewOk = ($script:Fixture.spare_view_id -and $mixedViews -and
                (@(Get-HzFindingElementIds $mixedViews) -contains [long]$script:Fixture.spare_view_id))

if (-not $script:Fixture.incompatible_template_id -or -not $mixedViewOk -or
    $mixedOrphanIds.Count -lt 1 -or -not $mixedSet) {
    Add-HzFixtureMissing -Id 'D9.1' -Name 'one call returns a per-action verdict for each action' `
        -Needs 'a view template Revit will REFUSE on the bare view (phase 0 mints one from a 3D view), that bare view still listed by the audit, and one surviving orphan group type to succeed beside it. Without a child whose dry run passes and whose apply fails, a per-action failure cannot be induced from outside at all: the pre-apply re-rehearsal turns every other induced failure into a whole-call refusal, which phase 8 measures instead.'
    Add-HzFixtureMissing -Id 'D9.2' -Name 'one action applied while the other failed' -Needs 'the same fixture as D9.1.'
    Add-HzFixtureMissing -Id 'D9.3' -Name 'the applied action survived and the failed one wrote nothing' -Needs 'the same fixture as D9.1.'
}
else {
    $mixedActions = @(
        @{ finding_id = [string](Get-HzProp $mixedOrphans 'finding_id'); element_ids = @($mixedOrphanIds[0]) },
        @{ finding_id = [string](Get-HzProp $mixedViews 'finding_id'); element_ids = @([long]$script:Fixture.spare_view_id)
           inputs = @{ template_view_id = [long]$script:Fixture.incompatible_template_id } }
    )
    $mixedDry = Invoke-HzCorrections -SetFingerprint $mixedSet -DryRun $true -Label 'mixed-rehearse' -Actions $mixedActions
    $mixedToken = Get-HzToken $mixedDry

    if (-not $mixedToken) {
        # THE BETTER PRODUCT. The child's rehearsal caught the template Revit would
        # refuse, so no token was issued and nothing can be written.
        $note = if ($mixedDry.IsError) { $mixedDry.Text } else { [string](Get-HzPath $mixedDry.Result 'note') }
        Add-HzProbe -Run $run -Id 'D9.1' -Name 'a child that would fail is caught in the rehearsal instead' `
            -Expected 'either a token to spend, or no token and nothing written' `
            -Observed ('no token: ' + (Limit-HzText $note 240)) -Ok $true `
            -Because 'a rehearsal that refuses a template Revit would reject is strictly better than a per-action rollback afterwards. This probe passes on the stronger behaviour and names it.'
        Add-HzFixtureMissing -Id 'D9.2' -Name 'one action applied while the other failed' `
            -Needs 'a child whose OWN dry run passes and whose apply fails. horizun_manage_views refuses the incompatible template during the rehearsal in this build, so no such child exists here and a per-action failure cannot be induced from outside.'
        Add-HzFixtureMissing -Id 'D9.3' -Name 'the applied action survived and the failed one wrote nothing' `
            -Needs 'the same child as D9.2.'
    }
    else {
        $mixedApply = Invoke-HzCorrections -SetFingerprint $mixedSet -DryRun $false -Label 'mixed-apply' `
            -Token $mixedToken -Actions $mixedActions
        $rowOrphan = Get-HzActionRow $mixedApply 0
        $rowView = Get-HzActionRow $mixedApply 1
        $stateOrphan = [string](Get-HzRowField $rowOrphan 'state')
        $stateView = [string](Get-HzRowField $rowView 'state')

        Add-HzProbe -Run $run -Id 'D9.1' -Name 'one call returns a per-action verdict for each action' `
            -Expected 'two rows, each with its own state - never one verdict for the pair' `
            -Observed ("actions[0]={0} actions[1]={1}" -f $stateOrphan, $stateView) `
            -Status $(if ($stateOrphan -and $stateView) { 'passed' } else { 'failed' }) `
            -Evidence @{ actions = @(Get-HzPath $mixedApply.Result 'actions') }

        # MEASURED 2026-09-03 ON REVIT 2026: Revit ACCEPTED the 3D-derived template
        # on the plan view, so both actions applied and no per-action failure was
        # induced. That is not a defect and it is not a pass either: the probe
        # asked for a failure it could not produce. What CAN be asserted from this
        # run is asserted - the two rows carry their own verdicts (D9.1), the
        # applied action really applied, and nothing rolled back that should not
        # have - and D9.2/D9.3 record the failure they could not induce.
        $afterMixedOrphans = Measure-HzCheck -Check 'orphan_group_types' -Label 'audit-after-mixed'
        $orphanGone = $afterMixedOrphans.Ok -and (@($afterMixedOrphans.ElementIds) -notcontains $mixedOrphanIds[0])
        $afterMixedViews = Measure-HzCheck -Check 'views_without_template' -Label 'audit-after-mixed-views'
        $viewUntouched = $afterMixedViews.Ok -and (@($afterMixedViews.ElementIds) -contains [long]$script:Fixture.spare_view_id)
        $inducedFailure = ($stateView -eq 'failed')

        if (-not $inducedFailure) {
            Add-HzProbe -Run $run -Id 'D9.2' -Name 'one action applied while the other failed' `
                -Expected 'the orphan delete applied and the template action failed, so per_action could be measured' `
                -Observed ("orphan={0} view={1} rollback_scope={2} - Revit ACCEPTED the template, so no action failed" -f
                           $stateOrphan, $stateView, (Get-HzPath $mixedApply.Result 'rollback_scope')) `
                -Status 'unverified' `
                -Because ('NOT VERIFIED, not not-applicable: the condition applies perfectly well and this run could ' +
                          'not produce it. A child whose own dry run passes and whose apply then FAILS. The ' +
                          'pre-apply re-rehearsal turns every externally induced breakage into a whole-call ' +
                          'refusal (phase 8), and the one remaining candidate - a view template of another view type - ' +
                          'was applied by Revit without complaint. Reporting a pass here would claim per_action was ' +
                          'measured when nothing failed.')
            Add-HzProbe -Run $run -Id 'D9.3' -Name 'both actions of the mixed call really landed' `
                -Expected 'the group type is gone from the audit and the bare view is no longer listed' `
                -Observed ("orphan_deleted={0} view_still_bare={1}" -f $orphanGone, $viewUntouched) `
                -Status $(if ($orphanGone -and -not $viewUntouched) { 'passed' } else { 'failed' }) `
                -Because 'two applied rows must both be true in the model; a row that says applied over an unchanged model is the failure this whole campaign is against.'
        }
        else {
            Add-HzProbe -Run $run -Id 'D9.2' -Name 'one action applied while the other failed' `
                -Expected 'the orphan delete is applied and the incompatible template is failed' `
                -Observed ("orphan={0} view={1} rollback_scope={2}" -f $stateOrphan, $stateView,
                           (Get-HzPath $mixedApply.Result 'rollback_scope')) `
                -Status $(if ($stateOrphan -eq 'applied') { 'passed' } else { 'failed' }) `
                -Because 'rollback_scope: per_action claims exactly this, and prose is not evidence.'
            Add-HzProbe -Run $run -Id 'D9.3' -Name 'the applied action survived and the failed one wrote nothing' `
                -Expected 'the group type is gone from the audit AND the bare view still has no template' `
                -Observed ("orphan_deleted={0} view_still_bare={1}" -f $orphanGone, $viewUntouched) `
                -Status $(if ($orphanGone -and $viewUntouched) { 'passed' } else { 'failed' }) `
                -Because 'per_action is only honest if the failure rolled back its own transaction and left the earlier one alone.'
        }
    }
}

# =============================================================================
# PHASE 10 - the two link recipes.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 10 - links: pin, and reload' -ForegroundColor Cyan

if ($linkInstanceIds.Count -eq 0) {
    Add-HzFixtureMissing -Id 'D10.1' -Name 'the unpinned-link recipe pins a link' `
        -Needs ("a Revit link in '{0}'. No typed command LINKS a model in this build - horizun_manage_links says so by name - so the fixture cannot create one: put a .rvt link into the disposable copy before running. A link is another file and cannot be simulated." -f $Document)
    Add-HzFixtureMissing -Id 'D10.2' -Name 'the pin applies and the re-audit reports corrected' -Needs 'the same link fixture as D10.1.'
    Add-HzFixtureMissing -Id 'D10.3' -Name 'the link really is pinned afterwards' -Needs 'the same link fixture as D10.1.'
    Add-HzFixtureMissing -Id 'D10.4' -Name 'the reload recipe selects only the UNLOADED link types' -Needs 'the same link fixture as D10.1.'
    Add-HzFixtureMissing -Id 'D10.5' -Name 'the reload applies and the re-audit agrees the link is loaded' -Needs 'the same link fixture as D10.1.'
}
else {
    # THE DEFECT, MADE TYPED. A link that is already pinned is not the finding.
    $instanceId = $null
    foreach ($candidate in $linkInstanceIds) {
        if ($candidate.Pinned -eq $true) {
            $unpin = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'unpin-fixture' -AllowRefusal -Arguments @{
                target_document = $Document; operation = 'unpin'; link_instance_id = $candidate.InstanceId
            }
            if (-not $unpin.Ok) { continue }
        }
        $instanceId = $candidate.InstanceId
        break
    }

    $pinState = Measure-HzCheck -Check 'unpinned_links' -Label 'audit-unpinned'
    if (-not $instanceId -or -not $pinState.Ok -or @($pinState.ElementIds) -notcontains $instanceId) {
        Add-HzFixtureMissing -Id 'D10.1' -Name 'the unpinned-link recipe pins a link' `
            -Needs 'a link instance the audit reports as unpinned. The document has links but none could be left unpinned for the check to find.'
        Add-HzFixtureMissing -Id 'D10.2' -Name 'the pin applies and the re-audit reports corrected' -Needs 'the same unpinned link as D10.1.'
        Add-HzFixtureMissing -Id 'D10.3' -Name 'the link really is pinned afterwards' -Needs 'the same unpinned link as D10.1.'
    }
    else {
        # A REVERSIBLE RECIPE KEEPS THE OLD DEFAULT: naming the finding is enough,
        # because pinning is not deleting.
        $pinRehearsal = Invoke-HzCorrections -SetFingerprint $pinState.SetFingerprint -DryRun $true -Label 'pin-rehearse' `
            -Actions @(@{ finding_id = $pinState.FindingId })
        $pinRow = Get-HzActionRow $pinRehearsal
        $pinToken = Get-HzToken $pinRehearsal
        Add-HzProbe -Run $run -Id 'D10.1' -Name 'a reversible recipe rehearses without being handed a list of ids' `
            -Expected 'state=rehearsed and a token, from an action that named only the finding' `
            -Observed ("state={0} token={1} steps={2}" -f (Get-HzRowField $pinRow 'state'), [bool]$pinToken,
                       @(Get-HzRowField $pinRow 'steps').Count) `
            -Status $(if ([string](Get-HzRowField $pinRow 'state') -eq 'rehearsed' -and $pinToken) { 'passed' } else { 'failed' }) `
            -Because 'the explicit-selection rule is about deleting, not about every correction; pinning the links a finding named is what naming the finding means.'

        if (-not $pinToken) {
            Add-HzUnverified -Id 'D10.2' -Name 'the pin applies and the re-audit reports corrected' `
                -Expected 'a token from the pin rehearsal' -Observed (Limit-HzText $pinRehearsal.Text 240)
            Add-HzUnverified -Id 'D10.3' -Name 'the link really is pinned afterwards' `
                -Expected 'a token from the pin rehearsal' -Observed (Limit-HzText $pinRehearsal.Text 240)
        }
        else {
            $pinApply = Invoke-HzCorrections -SetFingerprint $pinState.SetFingerprint -DryRun $false -Label 'pin-apply' `
                -Token $pinToken -Actions @(@{ finding_id = $pinState.FindingId })
            $pinAppliedRow = Get-HzActionRow $pinApply
            $pinRows = @(Get-HzPath $pinApply.Result @('re_audit', 'rows'))
            $pinOutcome = if ($pinRows.Count) { [string](Get-HzProp $pinRows[0] 'outcome') } else { $null }
            Add-HzProbe -Run $run -Id 'D10.2' -Name 'the pin applies and the re-audit reports corrected' `
                -Expected 'action state=applied and re_audit outcome=corrected' `
                -Observed $(if ($pinApply.IsError) { Limit-HzText $pinApply.Text 300 } else {
                    "state=$(Get-HzRowField $pinAppliedRow 'state') outcome=$pinOutcome" }) `
                -Status $(if (-not $pinApply.IsError -and [string](Get-HzRowField $pinAppliedRow 'state') -eq 'applied' -and
                              $pinOutcome -eq 'corrected') { 'passed' } else { 'failed' }) `
                -Evidence @{ re_audit = $pinRows }

            $afterPin = Measure-HzCheck -Check 'unpinned_links' -Label 'audit-after-pin'
            Add-HzProbe -Run $run -Id 'D10.3' -Name 'the link really is pinned afterwards' `
                -Expected ("link instance {0} is no longer reported as unpinned" -f $instanceId) `
                -Observed $(if ($afterPin.Ok) { "unpinned now [$(@($afterPin.ElementIds) -join ',')]" } else { $afterPin.Why }) `
                -Status $(if ($afterPin.Ok -and @($afterPin.ElementIds) -notcontains $instanceId) { 'passed' } else { 'failed' })
        }
    }

    # ---- RELOAD. The links finding lists every link TYPE with its status and only
    # an Unloaded one may be reloaded; a NotFound one needs a path, which is a
    # decision nobody supplied.
    # TRY EVERY LINK TYPE, not just the first. A link whose file is not on disk
    # unloads to NotFound, and horizun_manage_links refuses to call that a
    # successful unload - correctly, and the harness then reported the whole case
    # as fixture_missing because ONE link happened to be broken. A model with a
    # broken link and a sound one has the fixture; it just was not the first.
    $unloadAttempts = @()
    $unload = $null
    $unloadText = $null
    $unloadedListed = $false
    $linkState = $null
    $triedTypes = @()
    foreach ($candidateLink in $linkInstanceIds) {
        $typeId = $candidateLink.TypeId
        if ($triedTypes -contains $typeId) { continue }
        $triedTypes += $typeId
        $attempt = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label ("unload-fixture-$typeId") -AllowRefusal -Arguments @{
            target_document = $Document; operation = 'unload'; link_type_id = $typeId
        }
        $attemptText = if ($null -ne $attempt.Apply) { $attempt.Apply.Text } else { $attempt.Dry.Text }
        $unloadAttempts += ("type {0}: {1}" -f $typeId, (Limit-HzText $attemptText 160))
        $state = Measure-HzCheck -Check 'links' -Label ("audit-links-$typeId")
        $listed = $false
        if ($state.Ok) {
            foreach ($item in @(Get-HzProp $state.Finding 'items')) {
                if ([string](Get-HzProp $item 'status') -in @('Unloaded', 'LocallyUnloaded')) { $listed = $true }
            }
        }
        $unload = $attempt; $unloadText = $attemptText; $linkState = $state; $unloadedListed = $listed
        if ($attempt.Ok -and $listed) { break }
    }
    if ($unloadAttempts.Count -gt 1) {
        Add-HzNote $run ("unload tried " + $unloadAttempts.Count + " link type(s): " + ($unloadAttempts -join ' | '))
    }
    if ($null -eq $unload) {
        $unloadText = 'no link type could be tried'
        $linkState = Measure-HzCheck -Check 'links' -Label 'audit-links'
    }
    if (-not $unload.Ok -or -not $unloadedListed) {
        Add-HzFixtureMissing -Id 'D10.4' -Name 'the reload recipe selects only the UNLOADED link types' `
            -Needs ('a link type this harness can unload so the links check reports it - a link whose file is ON ' +
                    'DISK, because one that is gone unloads to NotFound and the typed command refuses to call ' +
                    'that an unload. Tried: ' + (($unloadAttempts -join ' | ')))
        Add-HzFixtureMissing -Id 'D10.5' -Name 'the reload applies and the re-audit agrees the link is loaded' `
            -Needs 'the same unloaded link as D10.4.'
    }
    else {
        $reload = Invoke-HzCorrections -SetFingerprint $linkState.SetFingerprint -DryRun $true -Label 'reload-rehearse' `
            -Actions @(@{ finding_id = $linkState.FindingId })
        $reloadNarrow = Invoke-HzCorrections -SetFingerprint $linkState.SetFingerprint -DryRun $true `
            -Label 'reload-rehearse-narrow' -Actions @(@{ finding_id = $linkState.FindingId; element_ids = @($typeId) })
        $reloadRow = Get-HzActionRow $reload
        $reloadToken = Get-HzToken $reloadNarrow
        $excludedTypes = @(Get-HzRowField $reloadRow 'excluded_by_filter')
        $reloadSelected = @(Get-HzRowField $reloadRow 'selected_element_ids')
        # THE SELECTION is what this probe is about: the unloaded type this run made
        # is in, and every loaded type is out. A document that has collected other
        # unloaded links over previous campaigns will fail the REHEARSAL for one of
        # them - a link whose file is gone cannot be reloaded - and that says
        # nothing about the filter. The apply below is narrowed to this run's type
        # for the same reason.
        $loadedExcluded = $true
        foreach ($item in @(Get-HzProp $linkState.Finding 'items')) {
            $itemId = [long](Get-HzProp $item 'id')
            $itemStatus = [string](Get-HzProp $item 'status')
            if ($itemStatus -notin @('Unloaded', 'LocallyUnloaded') -and ($reloadSelected -contains $itemId)) {
                $loadedExcluded = $false
            }
        }
        Add-HzProbe -Run $run -Id 'D10.4' -Name 'the reload recipe selects only the UNLOADED link types' `
            -Expected 'the unloaded type this run made is selected, and no type of any other status is' `
            -Observed ("state={0} selected=[{1}] excluded=[{2}] only_unloaded_selected={3}" -f
                       (Get-HzRowField $reloadRow 'state'), ($reloadSelected -join ','),
                       ($excludedTypes -join ','), $loadedExcluded) `
            -Status $(if (($reloadSelected -contains $typeId) -and $loadedExcluded) { 'passed' } else { 'failed' }) `
            -Because 'a link type whose file is NotFound needs a new path, which is a decision - the filter reads the typed status, never the summary sentence.' `
            -Evidence @{ action = $reloadRow; finding = (Get-HzProp $linkState.Finding 'items') }

        if (-not $reloadToken) {
            Add-HzUnverified -Id 'D10.5' -Name 'the reload applies and the re-audit agrees the link is loaded' `
                -Expected 'a token from the reload rehearsal' -Observed (Limit-HzText $reload.Text 300)
        }
        else {
            $reloadApply = Invoke-HzCorrections -SetFingerprint $linkState.SetFingerprint -DryRun $false `
                -Label 'reload-apply' -Token $reloadToken `
                -Actions @(@{ finding_id = $linkState.FindingId; element_ids = @($typeId) })
            $reloadAppliedRow = Get-HzActionRow $reloadApply
            $reloadRows = @(Get-HzPath $reloadApply.Result @('re_audit', 'rows'))
            $reloadOutcome = if ($reloadRows.Count) { [string](Get-HzProp $reloadRows[0] 'outcome') } else { $null }
            Add-HzProbe -Run $run -Id 'D10.5' -Name 'the reload applies and the re-audit agrees the link is loaded' `
                -Expected 'action state=applied and re_audit outcome=corrected' `
                -Observed $(if ($reloadApply.IsError) { Limit-HzText $reloadApply.Text 300 } else {
                    "state=$(Get-HzRowField $reloadAppliedRow 'state') outcome=$reloadOutcome" }) `
                -Status $(if (-not $reloadApply.IsError -and
                              [string](Get-HzRowField $reloadAppliedRow 'state') -eq 'applied' -and
                              $reloadOutcome -eq 'corrected') { 'passed' } else { 'failed' }) `
                -Evidence @{ re_audit = $reloadRows; item_state_after = (Get-HzProp $reloadRows[0] 'item_state_after') }

            # ---- D10.6: the INSTANCES of the reloaded type, and its status, read
            # from the model rather than from the reply. A type is reloaded once
            # however many times it is placed, and the re-audit judges the TYPE:
            # this is the check that the instances came back with it.
            $afterList = Invoke-HzTool -Run $run -Tool 'horizun_manage_links' -Label 'links-after-reload' `
                -TimeoutSec 300 -Arguments @{ operation = 'list'; target_document = $Document }
            $afterRow = $null
            foreach ($row in @(Get-HzPath $afterList.Result 'links')) {
                if ($null -ne $row -and [long](Get-HzProp $row 'link_type_id') -eq [long]$typeId) { $afterRow = $row }
            }
            $instancesBefore = @($linkInstanceIds | Where-Object { $_.TypeId -eq [long]$typeId }).Count
            $instancesAfter = @(Get-HzProp $afterRow 'instances').Count
            Add-HzProbe -Run $run -Id 'D10.6' -Name 'the reloaded type reads Loaded and keeps every instance it had' `
                -Expected ("status=Loaded and {0} instance(s), the same the type had before the unload" -f $instancesBefore) `
                -Observed $(if ($null -eq $afterRow) { 'the type is no longer listed at all' } else {
                    "status=$(Get-HzProp $afterRow 'status') instances=$instancesAfter" }) `
                -Status $(if ($null -ne $afterRow -and
                              [string](Get-HzProp $afterRow 'status') -eq 'Loaded' -and
                              $instancesAfter -eq $instancesBefore) { 'passed' } else { 'failed' }) `
                -Because 'the correction acts on a link TYPE; an instance lost on the way back would be a silent deletion.' `
                -Evidence @{ link_type = $afterRow; instances_before = $instancesBefore }

            # ---- D10.7: ANOTHER type left unloaded on purpose. The re-audit must
            # answer for what the action named and for nothing else - and the
            # links check, being an inventory, still lists the other one as the
            # issue it still is.
            $secondType = $null
            foreach ($candidateLink in $linkInstanceIds) {
                if ([long]$candidateLink.TypeId -eq [long]$typeId) { continue }
                $try = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label ("unload-second-$($candidateLink.TypeId)") `
                    -AllowRefusal -Arguments @{
                        target_document = $Document; operation = 'unload'; link_type_id = $candidateLink.TypeId }
                if ($try.Ok) { $secondType = [long]$candidateLink.TypeId; break }
            }
            if ($null -eq $secondType) {
                Add-HzFixtureMissing -Id 'D10.7' -Name 'a second unloaded link the action does not name stays unloaded' `
                    -Needs ('a SECOND link type whose file is on disk, so one can be reloaded while the other stays ' +
                            'unloaded. This document has ' + $linkTypes.Count + ' type(s) and only one could be unloaded.')
            }
            else {
                # Unload the first one again, so both are unloaded, and then name
                # only ONE of them in the correction.
                $null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'unload-again-first' -AllowRefusal `
                    -Arguments @{ target_document = $Document; operation = 'unload'; link_type_id = $typeId }
                $bothState = Measure-HzCheck -Check 'links' -Label 'audit-two-unloaded'
                $narrow = Invoke-HzCorrections -SetFingerprint $bothState.SetFingerprint -DryRun $true `
                    -Label 'reload-one-of-two-rehearse' `
                    -Actions @(@{ finding_id = $bothState.FindingId; element_ids = @($typeId) })
                $narrowToken = Get-HzToken $narrow
                if (-not $narrowToken) {
                    Add-HzUnverified -Id 'D10.7' -Name 'a second unloaded link the action does not name stays unloaded' `
                        -Expected 'a token from the narrowed reload rehearsal' -Observed (Limit-HzText $narrow.Text 300)
                }
                else {
                    $narrowApply = Invoke-HzCorrections -SetFingerprint $bothState.SetFingerprint -DryRun $false `
                        -Label 'reload-one-of-two-apply' -Token $narrowToken `
                        -Actions @(@{ finding_id = $bothState.FindingId; element_ids = @($typeId) })
                    $narrowRows = @(Get-HzPath $narrowApply.Result @('re_audit', 'rows'))
                    $narrowOutcome = if ($narrowRows.Count) { [string](Get-HzProp $narrowRows[0] 'outcome') } else { $null }
                    $stateAfter = if ($narrowRows.Count) { Get-HzProp $narrowRows[0] 'item_state_after' } else { $null }
                    $claimed = @()
                    if ($null -ne $stateAfter) {
                        foreach ($prop in $stateAfter.PSObject.Properties) { $claimed += [long]$prop.Name }
                    }
                    $stillUnloaded = $false
                    $finalState = Measure-HzCheck -Check 'links' -Label 'audit-after-narrow'
                    if ($finalState.Ok) {
                        foreach ($item in @(Get-HzProp $finalState.Finding 'items')) {
                            if ([long](Get-HzProp $item 'id') -eq $secondType -and
                                [string](Get-HzProp $item 'status') -in @('Unloaded', 'LocallyUnloaded')) {
                                $stillUnloaded = $true
                            }
                        }
                    }
                    Add-HzProbe -Run $run -Id 'D10.7' -Name 'a second unloaded link the action does not name stays unloaded' `
                        -Expected ("outcome=corrected for type $typeId only, the re-audit answering for that id alone, " +
                                   "and type $secondType still listed as Unloaded by the inventory") `
                        -Observed ("outcome={0} answered_for=[{1}] second_type_still_unloaded={2}" -f
                                   $narrowOutcome, ($claimed -join ','), $stillUnloaded) `
                        -Status $(if ($narrowOutcome -eq 'corrected' -and $claimed.Count -eq 1 -and
                                      $claimed[0] -eq [long]$typeId -and $stillUnloaded) { 'passed' } else { 'failed' }) `
                        -Because 'an inventory keeps listing what was not corrected, and a re-audit that answered for it too would be claiming work nobody asked for.' `
                        -Evidence @{ re_audit = $narrowRows; second_type = $secondType }
                }
            }
        }
    }
}

# =============================================================================
# PHASE 11 - what the registry offers that this campaign did NOT exercise.
# =============================================================================
Write-Host ''
Write-Host '  PHASE 11 - the recipes this run did not reach' -ForegroundColor Cyan

$corr = Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Label 'registry' -TimeoutSec 900 `
    -Arguments @{ target_document = $Document; top = $Top; propose_corrections = $true }
if ($corr.IsError) {
    Add-HzUnverified -Id 'D11.1' -Name 'every acting recipe in the registry is one this campaign exercises' `
        -Expected 'the audit publishes its correction registry' -Observed (Limit-HzText $corr.Text 240)
    Add-HzUnverified -Id 'D11.2' -Name 'the registry names no tool that runs arbitrary code' `
        -Expected 'the audit publishes its correction registry' -Observed (Limit-HzText $corr.Text 240)
}
else {
    $registry = Get-HzPath $corr.Result @('corrections', 'registry')
    $entries = @(Get-HzProp $registry 'entries')
    $acting = @()
    foreach ($entry in $entries) {
        if ($null -eq $entry) { continue }
        if (Get-HzProp $entry 'tool') { $acting += [string](Get-HzProp $entry 'finding_type') }
    }
    $unexercised = @($acting | Where-Object { $_ -notin $ACTING_RECIPES })
    Add-HzProbe -Run $run -Id 'D11.1' -Name 'every acting recipe in the registry is one this campaign exercises' `
        -Expected ('the acting entries are exactly: ' + ($ACTING_RECIPES -join ', ')) `
        -Observed ("registry acting = [{0}]" -f ($acting -join ', ')) `
        -Status $(if ($unexercised.Count -eq 0 -and $acting.Count -eq $ACTING_RECIPES.Count) { 'passed' } else { 'failed' }) `
        -Because 'a recipe added to the registry and never added here is one the bridge offers and nobody has ever run; that gap should be visible, not invisible.' `
        -Evidence @{ acting = $acting; not_exercised = $unexercised }

    $flat = ($registry | ConvertTo-Json -Depth 8 -Compress)
    Add-HzProbe -Run $run -Id 'D11.2' -Name 'the registry names no tool that runs arbitrary code' `
        -Expected 'horizun_execute_python is not reachable from a correction' `
        -Observed $(if ($flat) { ('registry published, {0} chars' -f $flat.Length) } else { 'no registry' }) `
        -Status $(if ($flat -and $flat -notmatch 'execute_python') { 'passed' } else { 'failed' }) `
        -Because 'a correction surface with an arbitrary-code escape hatch has no safety model - it has a list of suggestions and a way around the list.'
}

# A CORRECTION ON A WORKSHARED MODEL is not in this document and cannot be faked
# into one.
Add-HzFixtureMissing -Id 'D11.3' -Name 'a correction applied on a workshared model' `
    -Needs 'a CENTRAL model on a share plus a local of it. Every recipe here writes, and on a workshared document the element may be owned by somebody else - a refusal path a single-user fixture cannot produce at all.'

# =============================================================================
$run.Fixture['doctor_fixture'] = $script:Fixture
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir

$c = Get-HzCounts $run
$total = $c.passed + $c.failed + $c.unverified + $c.not_covered + $c.fixture_missing +
         $c.not_assessable + $c.not_applicable + $c.available + $c.implemented_not_live_verified
if ($total -ne $run.Probes.Count) {
    Write-Host ("  BUCKETS DO NOT ADD UP: {0} probes, {1} counted" -f $run.Probes.Count, $total) -ForegroundColor Red
    exit 3
}

Write-Host ''
Write-Host '  THE DOCUMENT WAS WRITTEN TO AND NOTHING WAS SAVED. Close it without' -ForegroundColor Yellow
Write-Host '  saving, or throw the copy away: it now carries deleted group types, a' -ForegroundColor Yellow
Write-Host '  pinned link and whatever else the phases above applied.' -ForegroundColor Yellow

if ($c.failed -gt 0) { exit 1 }
if ($c.unverified -gt 0) { exit 2 }
exit $done.ExitCode
