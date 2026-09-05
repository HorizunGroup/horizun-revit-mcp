<#
.SYNOPSIS
  What the worksharing fixtures on this machine can and cannot prove.

.DESCRIPTION
  Five conditions were reported for weeks as "fixture missing": ownership on a
  real workshared model, a closed workset, workset placement, an element
  borrowed by somebody else, and a model in ACC. That single label hid two very
  different things, and this harness separates them by measuring:

    REPAIRABLE CONFIGURATION - the fixture exists and the declaration was wrong.
      Measured 2026-09-03 on Revit 2026: C:\hz-live\HZ_CLOSED_L.rvt is a COPY OF
      A CENTRAL (the typed open refuses it as one), it carries the user workset
      HZ_WS_CLOSED, and opening it detached with that workset closed produces a
      real workshared document under the title HZ_CLOSED_L_detached. Ownership
      and closed-workset coverage are both measurable on it.

    GENUINELY ABSENT - no configuration on this machine produces it.
      An element held by ANOTHER user needs a second Revit user; one machine
      cannot borrow from itself, and a borrow simulated by editing the model is
      not a borrow. A cloud model needs an ACC project this machine is entitled
      to open, with the GUIDs Revit uses; the only ACC projects reachable here
      are a client's, which are not test fixtures.

  A probe that cannot run says which of the two it is. Nothing here is
  simulated, and nothing writes to any model.

.PARAMETER ClosedWorksetFile
  The workshared fixture on disk. Opened DETACHED, never as the central.
#>
[CmdletBinding()]
param(
    [string]$ClosedWorksetFile = 'C:\hz-live\HZ_CLOSED_L.rvt',
    [string]$ClosedWorksetName = 'HZ_WS_CLOSED',
    # AN AUTHORISED, DISPOSABLE CLOUD MODEL, if one exists. Both GUIDs as REVIT
    # knows them - not the ids in the ACC web URL, which name the same things
    # differently and resolve to nothing. Absent: W7 stays fixture_missing and
    # says so; present: it is measured like any other probe.
    [string]$CloudProjectGuid,
    [string]$CloudModelGuid,
    [string]$ArtifactDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'worksharing-fixtures' -Document 'HZ_CLOSED_L'
Write-Host "`n== worksharing fixtures: what exists, and what does not ==" -ForegroundColor Cyan

$health = Get-HzHealth $run
if (-not $health) { throw 'no bridge answered; nothing was measured.' }

# ---------------------------------------------------------------- W1: it exists
if (-not (Test-Path -LiteralPath $ClosedWorksetFile)) {
    Add-HzProbe -Run $run -Id 'W1' -Name 'a workshared fixture is on this machine' `
        -Expected "a workshared .rvt at $ClosedWorksetFile" -Observed 'not on this machine' `
        -Status 'fixture_missing' `
        -Because 'a workshared model - a central and a local of it - made from a disposable sample. No client model.'
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# A COPY OF A CENTRAL IS STILL A CENTRAL, and the typed open says so rather than
# working in the file everyone synchronises to. That refusal is the first
# measurement: it is why the fixture looked unusable.
$asCentral = Invoke-HzTool -Run $run -Tool 'horizun_open_document' -Label 'open-as-central' -Arguments @{
    path = $ClosedWorksetFile; expected_version = [string](Get-HzProp $health 'revit_version')
    activate = $true; idempotency_key = (New-HzKey $run 'open-as-central')
}
Add-HzProbe -Run $run -Id 'W1' -Name 'a copy of a central is refused as a central, not opened as a local' `
    -Expected 'REFUSED, naming detach or open_central; nothing opened' `
    -Observed (Limit-HzText ([string]$asCentral.Raw) 180) `
    -Ok ($asCentral.IsError -and [string]$asCentral.Raw -match 'workshared CENTRAL model' -and
         [string]$asCentral.Raw -match 'detach=true') `
    -Evidence @{ refusal = (Limit-HzText ([string]$asCentral.Raw) 400) }

# A SECOND DETACH GETS A SECOND TITLE. Measured 2026-09-03: with a detached copy
# already open, Revit hands back HZ_CLOSED_L_detached_1 and the typed open
# REFUSES to report it as the requested file - correctly, because every tool
# after it would have been talking to a document nobody asked for. So the run
# closes what an earlier run left behind before opening its own.
$base = [System.IO.Path]::GetFileNameWithoutExtension($ClosedWorksetFile)
function Get-HzStaleDetached {
    param($Run, [string]$Base)
    $h = Get-HzHealth $Run
    @(@(Get-HzProp $h 'open_documents') | ForEach-Object { [string](Get-HzProp $_ 'title') } |
        Where-Object { $_ -and $_ -like ($Base + '_detached*') })
}
$stale = @(Get-HzStaleDetached -Run $run -Base $base)
for ($pass = 0; $pass -lt 4 -and $stale.Count -gt 0; $pass++) {
    foreach ($t in $stale) {
        Write-Host ("  (closing a detached copy left open: {0})" -f $t) -ForegroundColor DarkGray
        # TWO REFUSALS, BOTH RIGHT, BOTH MEASURED 2026-09-03. Revit cannot close the
        # ACTIVE document, and the typed close asks for activate_other rather than
        # changing what the user is looking at as a side effect. And a detached copy
        # carries unsaved changes, which the close will not discard silently: it
        # wants a rehearsal, a token, and discard_unsaved said out loud. These are
        # disposable copies of a disposable fixture, so this run says it out loud.
        $rehearse = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label ('close-dry-' + $t) -Arguments @{
            operation = 'close'; target_document = $t; save = $false; activate_other = $true
            discard_unsaved = $true; dry_run = $true
            idempotency_key = (New-HzKey $run ('close-dry-' + $t + '-' + $pass))
        }
        $tok = [string](Get-HzProp $rehearse.Result 'confirmation_token')
        $closeArgs = @{
            operation = 'close'; target_document = $t; save = $false; activate_other = $true
            discard_unsaved = $true; dry_run = $false
            idempotency_key = (New-HzKey $run ('close-stale-' + $t + '-' + $pass))
        }
        if ($tok) { $closeArgs['confirmation_token'] = $tok }
        $null = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label ('close-stale-' + $t) -Arguments $closeArgs
    }
    $stale = @(Get-HzStaleDetached -Run $run -Base $base)
}
if ($stale.Count -gt 0) {
    # Revit numbers each new detached copy, and the typed open REFUSES to report
    # one whose title it cannot tie to the request - correctly, because every
    # tool after it would be talking to a document nobody asked for. With copies
    # it could not close, this run cannot produce the fixture it needs.
    Add-HzProbe -Run $run -Id 'W2' -Name 'the fixture opens detached with the named workset CLOSED' `
        -Expected 'no detached copy of the fixture already open, so the new one carries the plain _detached title' `
        -Observed ("still open: " + ($stale -join ', ')) -Status 'not_assessable' `
        -Because ('a detached copy from an earlier run is still open and would not close. Revit names the next one ' +
                  '_detached_N, and the typed open refuses a title it cannot tie to the request. Close those ' +
                  'documents, or restart Revit, and run this again.')
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# --------------------------------------------------- W2: opened with one closed
$opened = Invoke-HzToolStrict -Run $run -Tool 'horizun_document_session' -Label 'open-detached-closed' -Arguments @{
    operation = 'open'; file_path = $ClosedWorksetFile
    expected_version = [string](Get-HzProp $health 'revit_version')
    detach = $true; close_workset_names = @($ClosedWorksetName)
    idempotency_key = (New-HzKey $run 'open-detached-closed')
}
$title = [string](Get-HzProp $opened.Result 'active_document')
if (-not $title) { $title = [string](Get-HzProp $opened.Result 'title') }
Add-HzNote $run ("detached title: {0}" -f $title)
Add-HzProbe -Run $run -Id 'W2' -Name 'the fixture opens detached with the named workset CLOSED' `
    -Expected "workshared, detached, closed_worksets_requested contains $ClosedWorksetName" `
    -Observed ("workshared={0} detached={1} requested={2} title={3}" -f
        (Get-HzProp $opened.Result 'is_workshared'), (Get-HzProp $opened.Result 'detached'),
        (@(Get-HzProp $opened.Result 'closed_worksets_requested') -join ','), $title) `
    -Ok ((Get-HzProp $opened.Result 'is_workshared') -eq $true -and
         (Get-HzProp $opened.Result 'detached') -eq $true -and
         @(Get-HzProp $opened.Result 'closed_worksets_requested') -contains $ClosedWorksetName) `
    -Evidence @{ open = $opened.Result }

# THE TITLE CHANGES, and a scan given the wrong one is refused. That is the
# second reason this fixture read as missing.
Add-HzProbe -Run $run -Id 'W3' -Name 'a detached open renames the document, so the scan title is not the file title' `
    -Expected 'the active document title carries a _detached suffix' `
    -Observed $title `
    -Ok ($title -and $title -ne [System.IO.Path]::GetFileNameWithoutExtension($ClosedWorksetFile)) `
    -Evidence @{ file_title = [System.IO.Path]::GetFileNameWithoutExtension($ClosedWorksetFile); document_title = $title }

# ------------------------------------------- W4: a closed workset is INCOMPLETE
$scan = Invoke-HzToolStrict -Run $run -Tool 'horizun_model_scan' -Label 'scan-worksets' -Arguments @{
    target_document_title = $title; sections = @('worksets'); top = 50 }
$cov = Get-HzProp $scan.Result 'visibility_coverage'
Add-HzProbe -Run $run -Id 'W4' -Name 'a CLOSED workset makes the scan report incomplete coverage, by name' `
    -Expected 'is_workshared true, worksets_closed >= 1, worksets_open < worksets_total, coverage_complete false' `
    -Observed ("total={0} open={1} closed={2} complete={3}" -f
        (Get-HzProp $cov 'worksets_total'), (Get-HzProp $cov 'worksets_open'),
        (Get-HzProp $cov 'worksets_closed'), (Get-HzProp $cov 'coverage_complete')) `
    -Ok ((Get-HzProp $cov 'is_workshared') -eq $true -and
         [int](Get-HzProp $cov 'worksets_closed') -ge 1 -and
         [int](Get-HzProp $cov 'worksets_open') -lt [int](Get-HzProp $cov 'worksets_total') -and
         (Get-HzProp $cov 'coverage_complete') -eq $false) `
    -Evidence @{ visibility_coverage = $cov }

# --------------------------------------------- W5: the ownership census is real
$wsh = Invoke-HzToolStrict -Run $run -Tool 'horizun_model_scan' -Label 'scan-worksharing' -Arguments @{
    target_document_title = $title; sections = @('worksharing'); top = 20 }
$section = Get-HzPath $wsh.Result 'sections', 'worksharing'
if (-not $section) { $section = Get-HzProp $wsh.Result 'worksharing' }
$own = Get-HzProp $section 'ownership'
Add-HzProbe -Run $run -Id 'W5' -Name 'ownership is censused on a REAL workshared model, without relinquishing anything' `
    -Expected 'status ok, a scanned population greater than zero, and the four buckets balancing' `
    -Observed ("status={0} scanned={1} mine={2} others={3} none={4} unreadable={5} balance={6}" -f
        (Get-HzProp $own 'status'), (Get-HzProp $own 'elements_scanned'),
        (Get-HzProp $own 'elements_owned_by_me'), (Get-HzProp $own 'elements_owned_by_others'),
        (Get-HzProp $own 'elements_not_owned'), (Get-HzProp $own 'elements_unreadable'),
        (Get-HzProp $own 'counts_balance')) `
    -Ok ([string](Get-HzProp $own 'status') -eq 'ok' -and
         [int](Get-HzProp $own 'elements_scanned') -gt 0 -and
         (Get-HzProp $own 'counts_balance') -eq $true -and
         (Get-HzProp $section 'is_workshared') -eq $true) `
    -Evidence @{ worksharing = $section }

# ------------------------------------------------ W6, W7: what is NOT here
# A BORROW BY SOMEBODY ELSE. The census above reads it; nothing on one machine
# can produce it. Reported as the census's own zero, not as a pass.
# If a second user ever DOES hold something on this central, the census will say
# so and this probe must pass rather than keep reporting the condition missing.
$ownedByOthers = 0
try { $ownedByOthers = [int](Get-HzProp $own 'elements_owned_by_others') } catch { $ownedByOthers = 0 }
$byOwner = Get-HzProp $own 'by_owner'
$otherNamed = $false
foreach ($entry in @($byOwner)) {
    if ($null -eq $entry) { continue }
    $who = [string](Get-HzProp $entry 'owner')
    if ($who -and $who -ne [string](Get-HzProp $own 'me')) { $otherNamed = $true }
}
if ($ownedByOthers -gt 0) {
    Add-HzProbe -Run $run -Id 'W6' -Name 'an element held by ANOTHER user' `
        -Expected 'elements_owned_by_others greater than zero, with that user named in by_owner' `
        -Observed ("elements_owned_by_others={0} named_other_owner={1}" -f $ownedByOthers, $otherNamed) `
        -Ok ($otherNamed) `
        -Evidence @{ ownership = $own }
}
else {
Add-HzProbe -Run $run -Id 'W6' -Name 'an element held by ANOTHER user' `
    -Expected 'elements_owned_by_others greater than zero, with that user named in by_owner' `
    -Observed ("elements_owned_by_others={0}; the census ran and found none" -f $ownedByOthers) `
    -Status 'fixture_missing' `
    -Because ('a SECOND Revit user holding at least one element of this central. One machine cannot borrow from ' +
              'itself, and a borrow simulated by editing the model is not a borrow. The census that would read it ' +
              'is proved by W5; what is missing is the condition, not the code.') `
    -Evidence @{ census_ran = $true; owned_by_others = $ownedByOthers }
}

if ($CloudProjectGuid -and $CloudModelGuid) {
    # A cloud model, opened BY GUID, the way Revit names it. Everything after the
    # open is the same measurement as for a file on disk: is it workshared, does
    # the census run, and does the scan say where the model lives.
    $cloudOpen = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label 'open-cloud' -Arguments @{
        operation = 'open'; cloud_project_guid = $CloudProjectGuid; cloud_model_guid = $CloudModelGuid
        activate = $true; idempotency_key = (New-HzKey $run 'open-cloud')
    }
    $cloudTitle = [string](Get-HzPath $cloudOpen.Result 'document', 'title')
    if (-not $cloudOpen.Ok -or -not $cloudTitle) {
        Add-HzFixtureMissing -Id 'W7' -Name 'a model in ACC / BIM 360' `
            -Needs ('the two GUIDs were given but the model did not open: ' +
                    (Limit-HzText ([string]$cloudOpen.Text) 300))
    }
    else {
        $cloudScan = Invoke-HzToolStrict -Run $run -Tool 'horizun_model_scan' -Label 'scan-cloud' -Arguments @{
            target_document_title = $cloudTitle; sections = @('worksharing'); top = 20 }
        $cloudSection = Get-HzPath $cloudScan.Result 'sections', 'worksharing'
        if (-not $cloudSection) { $cloudSection = Get-HzProp $cloudScan.Result 'worksharing' }
        $cloudOwn = Get-HzProp $cloudSection 'ownership'
        Add-HzProbe -Run $run -Id 'W7' -Name 'a model in ACC / BIM 360' `
            -Expected 'the cloud model opens by GUID, reads as workshared, and its ownership census runs' `
            -Observed ("title={0} workshared={1} census={2} scanned={3}" -f $cloudTitle,
                (Get-HzProp $cloudSection 'is_workshared'), (Get-HzProp $cloudOwn 'status'),
                (Get-HzProp $cloudOwn 'elements_scanned')) `
            -Ok ((Get-HzProp $cloudSection 'is_workshared') -eq $true -and
                 [string](Get-HzProp $cloudOwn 'status') -eq 'ok') `
            -Evidence @{ worksharing = $cloudSection; opened = $cloudTitle }
        $null = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label 'close-cloud' -Arguments @{
            operation = 'close'; target_document = $cloudTitle; save = $false; activate_other = $true
            discard_unsaved = $true; idempotency_key = (New-HzKey $run 'close-cloud')
        }
    }
}
else {
Add-HzProbe -Run $run -Id 'W7' -Name 'a model in ACC / BIM 360' `
    -Expected 'a cloud model opened by cloud_project_guid and cloud_model_guid as REVIT knows them' `
    -Observed 'no authorised cloud fixture on this machine' `
    -Status 'fixture_missing' `
    -Because ('an ACC project this machine is entitled to open, whose model is DISPOSABLE. The ACC projects ' +
              'reachable from this account belong to a client and are not test fixtures: opening one to make a ' +
              'test pass would put client content into an evidence file. A downloaded copy cannot stand in - it ' +
              'is a local model with different worksharing, different ownership and no cloud state at all. ' +
              'When one exists, pass -CloudProjectGuid and -CloudModelGuid and this probe runs.')
}

# Leave the fixture as it was found: close what this run opened, save nothing.
# The same two refusals as above apply to this one: it is the active document,
# and it carries unsaved changes. Leaving it open would hand the NEXT harness a
# document it did not ask for - which is exactly what happened the first time.
$dry = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label 'close-detached-dry' -Arguments @{
    operation = 'close'; target_document = $title; save = $false; activate_other = $true
    discard_unsaved = $true; dry_run = $true
    idempotency_key = (New-HzKey $run 'close-detached-dry')
}
$closeArgs = @{
    operation = 'close'; target_document = $title; save = $false; activate_other = $true
    discard_unsaved = $true; dry_run = $false
    idempotency_key = (New-HzKey $run 'close-detached')
}
$t2 = [string](Get-HzProp $dry.Result 'confirmation_token')
if ($t2) { $closeArgs['confirmation_token'] = $t2 }
$closed = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label 'close-detached' -Arguments $closeArgs
Add-HzProbe -Run $run -Id 'W8' -Name 'the run leaves no document of its own open behind it' `
    -Expected "$title is closed and something else is active" `
    -Observed (Limit-HzText ([string]$closed.Raw) 160) `
    -Ok (-not $closed.IsError) `
    -Evidence @{ close = $closed.Result }

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
