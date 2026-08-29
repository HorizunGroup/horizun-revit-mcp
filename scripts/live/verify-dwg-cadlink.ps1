#Requires -Version 5.1
<#
  THE TYPED CAD LINK, LIVE.

  Until horizun_manage_cad_links existed, the first step of every DWG-to-BIM
  conversion went through horizun_execute_python: no rehearsal, no confirmation
  token, no post-commit re-read, and no refusal anybody could trust. This proves
  the typed replacement does all four, and that its refusals refuse for the
  reasons they claim.

      L  list, before anything is linked
      A  add: the rehearsal writes nothing, the apply re-reads what it made
      D  add refuses a file that is missing, empty, mis-extensioned or a duplicate
      R  reload: verified by CONTENT, and honest when nothing changed
      P  repoint: the element id survives, the drawing behind it does not
      U  unload: refused by name, with the API fact, and WITHOUT a Python grant
      W  the unit hazard, measured - LAST, because it deliberately links a second
         drawing and everything above measures a document with one link in it

  Exit code 0 when everything passed; non-zero otherwise.
#>
[CmdletBinding()]
param(
    [string]$Document = 'HZ_WRITE',
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-cadlink' -Document $Document

# =============================================================================
# STAGING - two different drawings, so repoint has somewhere to go
# =============================================================================
Write-Host "`n== staging ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run

$X = 900000.0
$fixA = New-HzWallFixture -Run $run -Tag 'linkA' -Walls @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0;    x2 = ($X + 6000); y2 = 0.0 },
    @{ name = 'W2'; x1 = $X; y1 = 4000.0; x2 = ($X + 6000); y2 = 4000.0 }
)
$null = Reset-HzDocument $run
$fixB = New-HzWallFixture -Run $run -Tag 'linkB' -Walls @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0;     x2 = ($X + 6000); y2 = 0.0 },
    @{ name = 'W2'; x1 = $X; y1 = 4000.0;  x2 = ($X + 6000); y2 = 4000.0 },
    @{ name = 'W3'; x1 = $X; y1 = 9000.0;  x2 = ($X + 6000); y2 = 9000.0 }
)
$null = Reset-HzDocument $run
# A THIRD drawing, for the unit measurement alone. It has to be one no probe
# above has linked: 'add' refuses a duplicate, and by the time W runs the repoint
# has legitimately aimed the first link at drawing B.
$fixC = New-HzWallFixture -Run $run -Tag 'linkC' -Walls @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0;     x2 = ($X + 6000); y2 = 0.0 },
    @{ name = 'W2'; x1 = $X; y1 = 12000.0; x2 = ($X + 6000); y2 = 12000.0 }
)
$null = Reset-HzDocument $run

$run.Fixture['fixture_id'] = 'hz-cadlink-' + $run.RunId
$run.Fixture['drawing_a'] = @{ name = $fixA.dwg_name; sha256 = $fixA.dwg_sha256; walls = 2 }
$run.Fixture['drawing_b'] = @{ name = $fixB.dwg_name; sha256 = $fixB.dwg_sha256; walls = 3 }
$run.Fixture['drawing_c'] = @{ name = $fixC.dwg_name; sha256 = $fixC.dwg_sha256; walls = 2
                               used_for = 'the forced-unit measurement, which needs a drawing nothing has linked' }
$run.Fixture['dwg_sha256'] = $fixA.dwg_sha256
$run.Expected['drawing_a_walls'] = 2
$run.Expected['drawing_b_walls'] = 3

$viewId = Get-HzFirstFloorPlanId $run
if ($null -eq $viewId) { throw 'HARNESS: the fixture document has no floor plan to link into' }
# units='default' asks the DRAWING what its numbers mean. Every probe below that
# is not specifically about units uses it, so the geometry lands where it was
# drawn - and the W probes force a wrong one deliberately, to measure what that
# costs.
$linkArgs = @{ target_document = $Document; operation = 'add'; view_id = $viewId; units = 'default' }

# =============================================================================
# L - list
# =============================================================================
Write-Host "`n== L: list ==" -ForegroundColor Cyan
$before = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'list-before' -Arguments @{
    operation = 'list' }
Add-HzProbe -Run $run -Id 'L1' -Name 'list is read-only and publishes the API limits rather than hiding them' `
    -Expected 'read_only=true, and unload reported as unavailable with the reason' `
    -Observed ("read_only={0} count={1} unload={2}" -f $before.Result.read_only, $before.Result.count,
        (Get-HzPath $before.Result 'api_limits', 'unload')) `
    -Ok ($before.Result.read_only -eq $true -and
         [string](Get-HzPath $before.Result 'api_limits', 'unload') -eq 'unavailable') `
    -Evidence @{ api_limits = $before.Result.api_limits; count = $before.Result.count }
$linkedBefore = [int]$before.Result.count

# =============================================================================
# A - add
# =============================================================================
Write-Host "`n== A: add ==" -ForegroundColor Cyan
$addArgs = Copy-HzArgs $linkArgs @{ file_path = $fixA.dwg_path }
$dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'add-dry' `
    -Arguments (Copy-HzArgs $addArgs @{ dry_run = $true })
$midCount = [int](Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'list-mid' `
    -Arguments @{ operation = 'list' }).Result.count

Add-HzProbe -Run $run -Id 'A1' -Name 'the rehearsal writes NOTHING, and says what it measured the token against' `
    -Expected ("still {0} CAD instances, a confirmation token, and the file's SHA in the preview" -f $linkedBefore) `
    -Observed ("count={0} wrote_nothing={1} token={2} sha_in_preview={3}" -f $midCount,
        $dry.Result.wrote_nothing, [bool](Get-HzProp $dry.Result 'confirmation_token'),
        [string](Get-HzPath $dry.Result 'file', 'sha256') -eq $fixA.dwg_sha256) `
    -Ok ($midCount -eq $linkedBefore -and $dry.Result.wrote_nothing -eq $true -and
         $null -ne (Get-HzProp $dry.Result 'confirmation_token') -and
         [string](Get-HzPath $dry.Result 'file', 'sha256') -eq $fixA.dwg_sha256) `
    -Evidence @{ rehearsal_kind = $dry.Result.rehearsal_kind; file = $dry.Result.file; view = $dry.Result.view }

$add = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'add' `
    -Arguments (Copy-HzArgs $addArgs @{ dry_run = $false
        confirmation_token = [string](Get-HzProp $dry.Result 'confirmation_token')
        idempotency_key = (New-HzKey $run 'add') })
$instanceId = [long]$add.Result.element_id

Add-HzProbe -Run $run -Id 'A2' -Name 'the apply links the drawing and RE-READS what it made' `
    -Expected 'host_verified, linked (not imported), and the SHA of the file the link RESOLVES to' `
    -Observed ("id={0} host_verified={1} import_or_link={2} sha_matches={3}" -f $instanceId,
        $add.Result.host_verified, (Get-HzPath $add.Result 'instance', 'import_or_link'),
        [string](Get-HzPath $add.Result 'instance', 'file_sha256') -eq $fixA.dwg_sha256) `
    -Ok ($add.Result.host_verified -eq $true -and
         [string](Get-HzPath $add.Result 'instance', 'import_or_link') -eq 'linked' -and
         [string](Get-HzPath $add.Result 'instance', 'file_sha256') -eq $fixA.dwg_sha256) `
    -Evidence @{ instance = $add.Result.instance; verified_by = $add.Result.verified_by }

$q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'query-after-add' -Arguments @{ mode = 'instances' }
$seen = @($q.Result.instances | Where-Object { [long]$_.element_id -eq $instanceId })
Add-HzProbe -Run $run -Id 'A3' -Name 'the drawing this command linked is the drawing horizun_query_cad reads' `
    -Expected 'one instance, same id, same file hash - the typed link and the CAD reader agree' `
    -Observed ("found={0} sha_matches={1}" -f $seen.Count,
        $(if ($seen.Count) { [string]$seen[0].file_sha256 -eq $fixA.dwg_sha256 } else { 'n/a' })) `
    -Ok ($seen.Count -eq 1 -and [string]$seen[0].file_sha256 -eq $fixA.dwg_sha256) `
    -Evidence @{ instance = $seen[0] }

# =============================================================================
# D - what add must refuse
# =============================================================================
Write-Host "`n== D: what add must refuse ==" -ForegroundColor Cyan

$missing = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-missing' `
    -Arguments (Copy-HzArgs $linkArgs @{ file_path = 'C:\ZZ_HORIZUN_NO_SUCH_DRAWING.dwg'; dry_run = $true })
Add-HzRefusalProbe -Run $run -Id 'D1' -Name 'a file that is not there refuses, naming the machine it looked on' `
    -Call $missing -MustMatch 'file_not_found'

$fake = Join-Path $run.WorkDir 'not-really-a-drawing.dwg'
'this is text, not a DWG' | Set-Content -LiteralPath $fake -Encoding utf8
$wrongContent = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-fake' `
    -Arguments (Copy-HzArgs $linkArgs @{ file_path = $fake; dry_run = $true })
# A wrongly-named file is not refused outright - Revit decides - but the reply
# must SAY the header disagrees with the extension rather than let it surprise.
$headerCalled = (-not $wrongContent.IsError) -and
                ((Get-HzPath $wrongContent.Result 'file', 'header_looks_like_dwg') -eq $false)
Add-HzProbe -Run $run -Id 'D2' -Name 'a .dwg whose first bytes are not a DWG marker is reported as such before anything is linked' `
    -Expected 'header_looks_like_dwg=false in the rehearsal, or an outright refusal' `
    -Observed $(if ($wrongContent.IsError) { 'refused: ' + (Limit-HzText $wrongContent.Text 140) }
                else { "header_looks_like_dwg=" + (Get-HzPath $wrongContent.Result 'file', 'header_looks_like_dwg') }) `
    -Ok ($wrongContent.IsError -or $headerCalled) `
    -Evidence @{ file = $(if ($wrongContent.IsError) { $null } else { $wrongContent.Result.file }) }

$emptyFile = Join-Path $run.WorkDir 'empty.dwg'
New-Item -ItemType File -Path $emptyFile -Force | Out-Null
$empty = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-empty' `
    -Arguments (Copy-HzArgs $linkArgs @{ file_path = $emptyFile; dry_run = $true })
Add-HzRefusalProbe -Run $run -Id 'D3' -Name 'a zero-byte drawing refuses instead of linking an empty one' `
    -Call $empty -MustMatch 'empty_file'

$dup = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-duplicate' `
    -Arguments (Copy-HzArgs $addArgs @{ dry_run = $true })
Add-HzRefusalProbe -Run $run -Id 'D4' -Name 'linking the SAME drawing twice refuses, because two instances make every rule guess' `
    -Call $dup -MustMatch 'already_linked'

$noView = Copy-HzArgs $addArgs @{ dry_run = $true }
$noView.Remove('view_id')
$viewless = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-no-view' -Arguments $noView
Add-HzRefusalProbe -Run $run -Id 'D5' -Name 'add without a view refuses, because Revit has no view-free Link overload' `
    -Call $viewless -MustMatch 'view_id is required'

$badUnits = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-units' `
    -Arguments (Copy-HzArgs $addArgs @{ dry_run = $true; units = 'furlongs'; allow_duplicate = $true })
Add-HzRefusalProbe -Run $run -Id 'D6' -Name 'an unknown unit refuses by name rather than falling back to feet' `
    -Call $badUnits -MustMatch "units 'furlongs' is not one this Revit knows"

# =============================================================================
# R - reload
# =============================================================================
Write-Host "`n== R: reload ==" -ForegroundColor Cyan
$reloadArgs = @{ target_document = $Document; operation = 'reload'; instance_id = $instanceId }
$rDry = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'reload-dry' `
    -Arguments (Copy-HzArgs $reloadArgs @{ dry_run = $true })
$reload = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'reload' `
    -Arguments (Copy-HzArgs $reloadArgs @{ dry_run = $false
        confirmation_token = [string](Get-HzProp $rDry.Result 'confirmation_token')
        idempotency_key = (New-HzKey $run 'reload') })

Add-HzProbe -Run $run -Id 'R1' -Name 'reloading a file nobody edited reports geometry_changed=FALSE, and that is an answer' `
    -Expected 'host_verified, and geometry_changed=false with the same fingerprint before and after' `
    -Observed ("host_verified={0} changed={1} same_print={2}" -f $reload.Result.host_verified,
        $reload.Result.geometry_changed,
        [string]$reload.Result.geometry_fingerprint_before -eq [string]$reload.Result.geometry_fingerprint_after) `
    -Ok ($reload.Result.host_verified -eq $true -and $reload.Result.geometry_changed -eq $false -and
         [string]$reload.Result.geometry_fingerprint_before -eq [string]$reload.Result.geometry_fingerprint_after) `
    -Evidence @{ before = $reload.Result.geometry_fingerprint_before
                 after = $reload.Result.geometry_fingerprint_after
                 verified_by = $reload.Result.verified_by }

# =============================================================================
# P - repoint
# =============================================================================
Write-Host "`n== P: repoint ==" -ForegroundColor Cyan
$printBefore = [string]$reload.Result.geometry_fingerprint_after
$pArgs = @{ target_document = $Document; operation = 'repoint'; instance_id = $instanceId
            file_path = $fixB.dwg_path }
$pDry = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'repoint-dry' `
    -Arguments (Copy-HzArgs $pArgs @{ dry_run = $true })
$repoint = Invoke-HzToolStrict -Run $run -Tool 'horizun_manage_cad_links' -Label 'repoint' `
    -Arguments (Copy-HzArgs $pArgs @{ dry_run = $false
        confirmation_token = [string](Get-HzProp $pDry.Result 'confirmation_token')
        idempotency_key = (New-HzKey $run 'repoint') })

Add-HzProbe -Run $run -Id 'P1' -Name 'repoint keeps the ELEMENT ID and changes the drawing behind it' `
    -Expected 'same element id, points at the requested file, geometry fingerprint changed' `
    -Observed ('id=' + [string]$repoint.Result.element_id +
               ' arrived=' + [string]$repoint.Result.points_at_requested_file +
               ' print_changed=' + [string]([string]$repoint.Result.geometry_fingerprint_after -ne $printBefore)) `
    -Ok ([long]$repoint.Result.element_id -eq $instanceId -and
         $repoint.Result.points_at_requested_file -eq $true -and
         [string]$repoint.Result.geometry_fingerprint_after -ne $printBefore) `
    -Evidence @{ from = $repoint.Result.from_path; to = $repoint.Result.to_path_requested
                 before = $printBefore; after = $repoint.Result.geometry_fingerprint_after }

$qAfter = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'query-after-repoint' -Arguments @{ mode = 'instances' }
$now = @($qAfter.Result.instances | Where-Object { [long]$_.element_id -eq $instanceId })
Add-HzProbe -Run $run -Id 'P2' -Name 'and the CAD reader agrees the link now names the other drawing' `
    -Expected ("file_sha256 = drawing B ({0})" -f $fixB.dwg_sha256.Substring(0, 12)) `
    -Observed ("sha={0}" -f $(if ($now.Count) { ([string]$now[0].file_sha256).Substring(0, 12) } else { 'gone' })) `
    -Ok ($now.Count -eq 1 -and [string]$now[0].file_sha256 -eq $fixB.dwg_sha256) `
    -Evidence @{ instance = $now[0] }

# =============================================================================
# U - unload
# =============================================================================
Write-Host "`n== U: unload ==" -ForegroundColor Cyan
$unload = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-unload' -Arguments @{
    target_document = $Document; operation = 'unload'; instance_id = $instanceId; dry_run = $true }
Add-HzRefusalProbe -Run $run -Id 'U1' -Name 'unload is refused BY NAME, with the API fact behind it' `
    -Call $unload -MustMatch 'no_cad_unload'

# AND WITHOUT A PYTHON GRANT. A capability gap the fallback could close is
# granted; one the API simply does not have is not, because a script would find
# the same absent method.
$fallback = Get-HzPath $unload.Raw 'structured', 'fallback'
if ($null -eq $fallback) { $fallback = Get-HzPath $unload.Raw 'result', 'fallback' }
$granted = $false
if ($null -ne $fallback) { $granted = [bool](Get-HzProp $fallback 'allowed') }
Add-HzProbe -Run $run -Id 'U2' -Name 'and it does NOT grant the Python fallback, because a script would find the same absent API' `
    -Expected 'no fallback grant on the unload refusal' -Observed ("granted={0}" -f $granted) `
    -Ok (-not $granted) `
    -Evidence @{ fallback = $fallback
                 reason = 'CADLinkType declares only Reload and LoadFrom in every Revit 2023-2027' }

$bogus = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-bogus-op' -Arguments @{
    target_document = $Document; operation = 'teleport'; dry_run = $true }
Add-HzRefusalProbe -Run $run -Id 'U3' -Name 'an operation outside the enum refuses and DOES hand over the fallback' `
    -Call $bogus -MustMatch 'unsupported operation'

$bogusFallback = Get-HzPath $bogus.Raw 'structured', 'fallback'
if ($null -eq $bogusFallback) { $bogusFallback = Get-HzPath $bogus.Raw 'result', 'fallback' }
$bogusGranted = $false
if ($null -ne $bogusFallback) { $bogusGranted = [bool](Get-HzProp $bogusFallback 'allowed') }
Add-HzProbe -Run $run -Id 'U4' -Name 'the two refusals are told apart: a missing operation grants, a missing API does not' `
    -Expected 'the unknown operation grants the fallback; unload does not' `
    -Observed ("unknown_operation_granted={0} unload_granted={1}" -f $bogusGranted, $granted) `
    -Ok ($bogusGranted -and -not $granted) `
    -Evidence @{ unknown_operation = $bogusFallback; unload = $fallback }

# =============================================================================
# =============================================================================
# W - the unit hazard, measured
# =============================================================================
Write-Host "`n== W: forcing a unit the drawing is not in ==" -ForegroundColor Cyan
#
# MEASURED 2026-08-27, and it is the most dangerous thing on this page.
#
# The fixture DWG is exported by Revit in INCHES. Linking it with units='default'
# lets Revit read the header and the geometry lands where it was drawn. Linking a
# drawing with units='millimeter' - a value a caller might reasonably pass
# because the model is metric - puts it at 1/25.4 of that, silently.
#
# Nothing downstream can catch it. horizun_query_cad then reports the link's
# DECLARED unit as millimetre, a requirement set declaring millimetre AGREES with
# it, and the unit gate compares those two and passes. The gate can only ever
# compare a declaration against a declaration; neither knows what is in the file.
# This probe exists so that limit is measured rather than discovered.
#
# It forces the unit on DRAWING C - a file nothing above has linked - because of
# W3 below: a second link of a file already linked reuses the existing
# CADLinkType and the options it was created with, so forcing a unit there
# changes nothing at all.
$forced = Invoke-HzWrite -Run $run -Tool 'horizun_manage_cad_links' -Label 'forced-units' -Arguments @{
    target_document = $Document; operation = 'add'; view_id = $viewId
    file_path = $fixC.dwg_path; units = 'millimeter'; current_view_only = $true
}
$forcedId = [long](Get-HzProp $forced.Apply.Result 'element_id')
$forcedGeom = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'forced-geometry' -Arguments @{
    mode = 'geometry'; instance_id = $forcedId; max_rows = 500 }
$honestGeom = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'honest-geometry' -Arguments @{
    mode = 'geometry'; instance_id = $instanceId; max_rows = 500 }
$forcedMinX = [double](Get-HzPath $forcedGeom.Result 'bounding_box_mm', 'min')[0]
$honestMinX = [double](Get-HzPath $honestGeom.Result 'bounding_box_mm', 'min')[0]
$ratio = if ($forcedMinX -ne 0) { $honestMinX / $forcedMinX } else { 0 }

Add-HzProbe -Run $run -Id 'W1' -Name 'forcing a unit the drawing is not in moves the geometry by exactly that unit ratio' `
    -Expected 'the forced link lands at 1/25.4 of where it was drawn - inches read as millimetres' `
    -Observed ("drawn_at={0} honest_min_x={1} forced_min_x={2} ratio={3}" -f $X,
        [Math]::Round($honestMinX, 1), [Math]::Round($forcedMinX, 1), [Math]::Round($ratio, 3)) `
    -Ok ([Math]::Abs($ratio - 25.4) -lt 0.5) `
    -Evidence @{ drawn_at_mm = $X; honest_min_x_mm = $honestMinX; forced_min_x_mm = $forcedMinX; ratio = $ratio }

$forcedFacts = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'forced-instances' -Arguments @{ mode = 'instances' }
$forcedRow = @($forcedFacts.Result.instances | Where-Object { [long]$_.element_id -eq $forcedId })
# AND THE GOOD NEWS, also measured: Revit records the drawing's OWN unit on the
# type, not the one the import was forced to. IMPORT_DISPLAY_UNITS reads 'inch'
# on a link created with units='millimeter', because the DWG says inch. That is
# what keeps the hazard catchable: a requirement set declaring millimetre
# DISAGREES with the link, and the unit gate refuses.
#
# The first version of this probe asserted the opposite - that the link would
# declare the forced unit and the gate would therefore pass - and it was wrong.
# The measurement is recorded as it came out.
Add-HzProbe -Run $run -Id 'W2' -Name "the link still declares the DRAWING's own unit, not the one the import was forced to" `
    -Expected "declared_units = inch, read from IMPORT_DISPLAY_UNITS - the DWG's own unit survives the forcing" `
    -Observed ("declared={0} route={1}" -f $forcedRow[0].declared_units, $forcedRow[0].declared_units_route) `
    -Ok ([string]$forcedRow[0].declared_units -eq 'inch') `
    -Evidence @{ instance = $forcedRow[0]
                 why_this_matters = 'this is what makes the unit hazard CATCHABLE. The gate compares the link ' +
                                    'declaration against the requirement set; because the declaration follows ' +
                                    'the drawing rather than the import option, a set that says millimetre ' +
                                    'disagrees and is refused - see W4.' }

# W4: the gate, on the forced link, for real.
$forcedSet = New-HzWallRequirementSet -Layer 'A-WALL-ANY' -Units 'millimeter' -Id 'hz-forced-units'
$gate = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'forced-plan' -Arguments @{
    target_document = $Document; instance_id = $forcedId; requirement_set = $forcedSet }
Add-HzRefusalProbe -Run $run -Id 'W4' -Name 'planning the forced link with a millimetre requirement set is REFUSED by the unit gate' `
    -Call $gate -MustMatch 'unit_mismatch' `
    -Expected 'unit_mismatch: the link declares inch, the set declares millimetre, and building anyway would be out by 25.4'

# ---------------------------------------------------------------------------
# W3: why W1 had to use a drawing this document had not linked before.
# ---------------------------------------------------------------------------
$second = Invoke-HzWrite -Run $run -Tool 'horizun_manage_cad_links' -Label 'second-link' -Arguments @{
    target_document = $Document; operation = 'add'; view_id = $viewId
    file_path = $fixA.dwg_path; units = 'millimeter'; allow_duplicate = $true; current_view_only = $true
}
$secondId = [long](Get-HzProp $second.Apply.Result 'element_id')
$secondGeom = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label 'second-geometry' -Arguments @{
    mode = 'geometry'; instance_id = $secondId; max_rows = 500 }
$secondMinX = [double](Get-HzPath $secondGeom.Result 'bounding_box_mm', 'min')[0]

# WHAT THIS ACTUALLY MEASURED, and the correction it forced.
#
# The first version of this probe asserted that a duplicate link REUSES the
# existing CADLinkType and its options, so a forced unit on the second call would
# change nothing. That was inferred from a run in which BOTH links happened to
# pass units='millimeter' - the reading had a different cause, and there was
# never any evidence for the claim.
#
# Measured properly: the duplicate gets its OWN type and honours its OWN units,
# so two placements of one drawing can sit at different scales in one model.
# That makes allow_duplicate more dangerous than the first reading suggested,
# not less.
Add-HzProbe -Run $run -Id 'W3' -Name 'a SECOND link of the same file gets its OWN type and its OWN units - two placements can differ in scale' `
    -Expected 'the duplicate honours the unit THIS call asked for, so it lands 25.4x away from the first placement' `
    -Observed ("first_min_x={0} second_min_x={1} asked_for=millimeter" -f
        [Math]::Round($honestMinX, 1), [Math]::Round($secondMinX, 1)) `
    -Ok ([Math]::Abs($secondMinX - ($honestMinX / 25.4)) -lt 5.0) `
    -Evidence @{ first_min_x_mm = $honestMinX; second_min_x_mm = $secondMinX
                 means = 'one drawing, two placements, two scales, in one document. This is the third reason ' +
                         'allow_duplicate is off by default and the refusal names the instance already there.' }

# Put the document back to one link, so the probes below measure one drawing.
# HOUSEKEEPING, NOT A PROBE: a cleanup that cannot run is worth a note and the
# probes that follow, not the loss of every measurement taken so far.
foreach ($extra in @($forcedId, $secondId)) {
    $drop = Invoke-HzWrite -Run $run -Tool 'horizun_delete_verified' -Label ("drop-" + $extra) -AllowRefusal `
        -Arguments @{ target_document = $Document; mode = 'ids'; ids = @($extra) }
    if (-not $drop.Ok) {
        Add-HzNote $run ("could not remove the extra link {0}: {1}" -f $extra,
            (Limit-HzText $(if ($drop.Apply) { $drop.Apply.Text } else { $drop.Dry.Text }) 140))
    }
}


$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
