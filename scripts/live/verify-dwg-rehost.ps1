#Requires -Version 5.1
<#
  A DOOR THAT LIVES IN THE WRONG WALL.

  `rehosted` was implemented, unit-tested, and had never been produced by a real
  model - the fixtures carried one wall, and a door needs somewhere else to be.

  What makes it worth its own classification is that NO COMPARISON OF POSITIONS
  can see it. In this fixture the door does not move by a millimetre and the
  drawing is byte-identical between the two readings. What changed is the
  building: somebody drew a new partition where the drawing puts this door, and
  the door is still hosted in the old one. Every reading that compares
  coordinates reports agreement, and the model is wrong.

  It also settles what the bridge should DO about it, which is nothing: Revit
  has no way to move a hosted instance into another host, so applying a
  re-hosting means deleting the element and building a new one - a new
  ElementId, and every tag, dimension and schedule line pointed at the old one
  broken. Detecting it is the honest capability. Applying it is not, and this
  harness pins that the bridge does not pretend otherwise.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-rehost' -Document $Document
$X = 902000.0

function Get-HzClassCount {
    param($Update, [string]$Name)
    $c = Get-HzPath $Update 'counts_by_classification', $Name
    if ($null -eq $c) { -1 } else { [int]$c }
}

function Get-HzMatchedActions {
    param($Update)
    @(@(Get-HzProp $Update 'plan') | Where-Object { $null -ne (Get-HzProp $_ 'element_id') })
}

function Invoke-HzConversion {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)]$Plan, [Parameter(Mandatory)]$Set,
          [Parameter(Mandatory)][long]$InstanceId, [Parameter(Mandatory)][string]$Tag)
    $applyArgs = @{
        target_document = $Run.Document; instance_id = $InstanceId; requirement_set = $Set
        apply_binding = $Plan.apply_binding
        actions = $Plan.execute_plan_request.actions
        candidate_index = $Plan.candidate_index
    }
    $dry = Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag-dry" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
    $tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
    $acts = @($Plan.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $acts) {
        $t = Get-HzProp $tokens $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    (Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $acts
            idempotency_key = (New-HzKey $Run "apply-$Tag") })).Result
}

# =============================================================================
# THE FIXTURE - a wall with a door in it, exported and read back
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

$doorSymbol = Get-HzHostedSymbol -Run $run -Kind 'Door'
if ($null -eq $doorSymbol) {
    # NOT a failure and NOT a pass: Revit ships family templates with the
    # product, and a machine without them cannot host anything.
    foreach ($id in @('H1', 'H2', 'R1', 'R2', 'R3', 'R4', 'R5', 'W1', 'A1')) {
        Add-HzProbe -Run $run -Id $id -Name 'a re-hosted door needs a door family on this machine' `
            -Expected 'Metric Door.rft' -Observed 'no door template' -Status 'fixture_missing'
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}
$run.Fixture['door_type'] = $doorSymbol.type_name

$doorAt = @(($X + 3000.0), 0.0)
$plainAt = @(($X + 9000.0), 0.0)

$fx = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-wall' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'wall'; start = @($X, 0.0, 0.0); end = @(($X + 12000.0), 0.0, 0.0)
                    height = 3000.0; level_id = [long]$level.element_id }) }
$fxWallId = [long](@($fx.Apply.Result.rows)[0].element_id)

$ins = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-door' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'family_instance'; type_id = [long]$doorSymbol.type_id
                    point = @($doorAt[0], $doorAt[1], 0.0); level_id = [long]$level.element_id
                    host_id = $fxWallId }) }
if ([int]$ins.Apply.Result.created_verified -ne 1) { throw 'HARNESS: the fixture door was not built' }

$viewName = "HZ_RH_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$viewId = [long](@($view.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $viewId
                   box = @(($X - 2000.0), -5000.0, ($X + 14000.0), 5000.0) }) }
New-Item -ItemType Directory -Force -Path 'C:\hz-live\dwg' | Out-Null
$dwgPath = Join-Path 'C:\hz-live\dwg' ("HZ_RH_$($run.RunId).dwg")
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwgPath }
$dwgFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter "HZ_RH_$($run.RunId)*.dwg")[0]
if ($null -eq $dwgFile) { throw 'HARNESS: the fixture exported no DWG' }
$run.Fixture['dwg_name'] = $dwgFile.Name
$run.Fixture['dwg_sha256'] = (Get-HzSha256 $dwgFile.FullName)

# ---------------------------------------------------------------- read it back
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
# The reset reopens from disk, so the symbol is provisioned into the document the
# conversion will actually run in, and its name read from THAT one.
$doorSymbol = Get-HzHostedSymbol -Run $run -Kind 'Door'
if ($null -eq $doorSymbol) { throw 'HARNESS: the door symbol did not survive the reset' }

$inst = Add-HzCadLink -Run $run -DwgPath $dwgFile.FullName -Label 'link'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$wallLayer = Get-HzWallLayer -Run $run -InstanceId $inst
$doorLayer = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $doorAt `
    -OtherPoints @(, $plainAt) -Label 'layer-door'
if (-not $doorLayer) { throw 'HARNESS: no layer is exclusive to the door in this drawing' }
Add-HzNote $run ("layers: wall='{0}' door='{1}'" -f $wallLayer, $doorLayer)

# THE POINT TOLERANCE IS ALSO THE HOST SEARCH RADIUS, and it is declared here
# rather than tuned afterwards: a symbol's cluster centre sits wherever its swing
# arc pulls it, and the same number decides which wall the drawing implies.
$doorSet = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-rehost'; version = '1.0.0'; title = 'Live: a door and its host' }
    source = @{ units = [string]$facts.declared_units }
    tolerances = @{ point_mm = 300.0; gap_mm = 300.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'doors'; precedence = 10; discipline = 'architecture'
                 layers = @($doorLayer); produces = 'door'; category = 'OST_Doors'
                 family_type = $doorSymbol.type_name
                 geometry = @{ from = 'point_clusters'; cluster_radius_mm = 1200.0 } })
}
$wallSet = New-HzWallRequirementSet -Layer $wallLayer -Units ([string]$facts.declared_units) `
    -BridgeOpeningsMm 1500.0

# The wall first: a door planned before its host exists is refused, which the
# architecture harness already pins. Here it is only the ground to stand on.
$wallPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-walls' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $wallSet
    level_id = [long]$level.element_id }
$builtWalls = Invoke-HzConversion -Run $run -Plan $wallPlan.Result -Set $wallSet -InstanceId $inst -Tag 'walls'
if ([int]$builtWalls.created_verified -lt 1) { throw 'HARNESS: no wall was converted; the door has no host' }

$doorPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-door' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $doorSet
    level_id = [long]$level.element_id }
$builtDoor = Invoke-HzConversion -Run $run -Plan $doorPlan.Result -Set $doorSet -InstanceId $inst -Tag 'door'
if ([int]$builtDoor.created_verified -ne 1) {
    throw ("HARNESS: the conversion built {0} door(s), not 1" -f $builtDoor.created_verified)
}

$doors = @(Get-HzElements -Run $run -Categories @('OST_Doors') -Label 'doors')
if ($doors.Count -ne 1) { throw ("HARNESS: the document holds {0} doors, not 1" -f $doors.Count) }
$doorId = [long]$doors[0].element_id
$originalHost = Get-HzProp $doors[0] 'host_id'

Add-HzProbe -Run $run -Id 'H1' -Name 'the fixture starts with the door genuinely hosted, and the MODEL says so' `
    -Expected 'one door, carrying a host the document reports' `
    -Observed ("door={0} host_id={1}" -f $doorId, $originalHost) `
    -Ok ($null -ne $originalHost) `
    -Evidence @{ read_from = 'horizun_query_model host_id, not the write that made it'
                 converted_from = $run.Fixture['dwg_name'] }

# =============================================================================
# H2 - THE BASELINE, and where the drawing actually puts this door
# =============================================================================
Write-Host "`n== H2: the baseline reading ==" -ForegroundColor Cyan

$updArgs = @{ target_document = $Document; instance_id = $inst; requirement_set = $doorSet
              level_id = [long]$level.element_id }
$before = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-before' -Arguments $updArgs

$beforeAction = @(Get-HzMatchedActions $before.Result)[0]
if ($null -eq $beforeAction) { throw 'HARNESS: the baseline update matched no element; there is nothing to re-host' }

Add-HzProbe -Run $run -Id 'H2' -Name 'with nothing changed the update finds the door and says so - not rehosted' `
    -Expected 'the door matched, rehosted 0' `
    -Observed ("classification={0} rehosted={1} element={2}" -f
        (Get-HzProp $beforeAction 'classification'), (Get-HzClassCount $before.Result 'rehosted'),
        (Get-HzProp $beforeAction 'element_id')) `
    -Ok ((Get-HzClassCount $before.Result 'rehosted') -eq 0 -and
         [long](Get-HzProp $beforeAction 'element_id') -eq $doorId) `
    -Evidence @{ counts_by_classification = $before.Result.counts_by_classification
                 says = (Get-HzProp $beforeAction 'says') }

# WHERE THE DRAWING PUTS IT, taken from the bridge's own evidence rather than
# recomputed here. A harness that re-derives the number proves its own
# arithmetic; this one builds against what the reviewer would read.
# ONE POINT OR MANY. drawing_says_mm is a LIST of [x,y,z], and PowerShell
# unrolls a single-element list of arrays down to the bare numbers - so the
# shape has to be recognised rather than indexed twice and hoped for.
$says = Get-HzPath $beforeAction 'evidence', 'drawing_says_mm'
$first = $null
if ($says -is [array] -and $says.Count -ge 1) {
    $first = $(if ($says[0] -is [array]) { $says[0] } else { $says })
}
if ($null -eq $first -or $first.Count -lt 2) {
    throw 'HARNESS: the baseline action carries no usable drawing_says_mm to build against'
}
$drawnX = [double]$first[0]
$drawnY = [double]$first[1]
Add-HzNote $run ("the drawing puts this door at x={0} y={1}" -f [math]::Round($drawnX, 1), [math]::Round($drawnY, 1))

# =============================================================================
# R - A NEW PARTITION, AND A DOOR LEFT BEHIND IN THE OLD ONE
# =============================================================================
Write-Host "`n== R: somebody draws a new partition ==" -ForegroundColor Cyan

# PERPENDICULAR, THROUGH THE POINT THE DRAWING NAMES. Its centreline passes
# exactly through the candidate, so it is strictly nearer than the converted wall
# unless the candidate sits dead on that wall's centreline - which the guard
# below refuses to paper over.
if ([math]::Abs($drawnY) -lt 1.0) {
    throw ("HARNESS: the drawing puts this door within 1 mm of the converted wall's centreline " +
           "(y=" + $drawnY + "), so a perpendicular wall through it ties rather than wins, and this " +
           "run could report no re-hosting for a reason that has nothing to do with the bridge.")
}
$newWall = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'new-partition' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'wall'; start = @($drawnX, ($drawnY - 4000.0), 0.0)
                    end = @($drawnX, ($drawnY + 4000.0), 0.0)
                    height = 3000.0; level_id = [long]$level.element_id }) }
$newWallId = [long](@($newWall.Apply.Result.rows)[0].element_id)
Add-HzNote $run ("a new partition {0} now runs through the point the drawing names" -f $newWallId)

$stillThere = @(Get-HzElements -Run $run -Categories @('OST_Doors') -Label 'doors-after' `
    | Where-Object { [long]$_.element_id -eq $doorId })
$hostNow = $(if ($stillThere.Count -eq 1) { Get-HzProp $stillThere[0] 'host_id' } else { $null })

$after = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-after' -Arguments $updArgs
$rehosted = @(@(Get-HzProp $after.Result 'plan') |
              Where-Object { (Get-HzProp $_ 'classification') -eq 'rehosted' })

Add-HzProbe -Run $run -Id 'R1' -Name 'the door is reported REHOSTED - it lives in one wall and the drawing implies another' `
    -Expected 'rehosted 1, naming this door' `
    -Observed ("rehosted={0} element={1}" -f (Get-HzClassCount $after.Result 'rehosted'),
        $(if ($rehosted.Count -ge 1) { Get-HzProp $rehosted[0] 'element_id' } else { '(none)' })) `
    -Ok ((Get-HzClassCount $after.Result 'rehosted') -eq 1 -and $rehosted.Count -eq 1 -and
         [long](Get-HzProp $rehosted[0] 'element_id') -eq $doorId) `
    -Evidence @{ counts_by_classification = $after.Result.counts_by_classification
                 says = $(if ($rehosted.Count -ge 1) { Get-HzProp $rehosted[0] 'says' } else { $null }) }

Add-HzProbe -Run $run -Id 'R2' -Name 'and NO comparison of positions could have seen it: the door never moved' `
    -Expected 'the same element, in the host it was built in, and the drawing byte-identical' `
    -Observed ("host_before={0} host_now={1} moved={2} dwg_sha_unchanged={3}" -f $originalHost, $hostNow,
        (Get-HzClassCount $after.Result 'moved'),
        ((Get-HzSha256 $dwgFile.FullName) -eq [string]$run.Fixture['dwg_sha256'])) `
    -Ok ($null -ne $hostNow -and [long]$hostNow -eq [long]$originalHost -and
         (Get-HzClassCount $after.Result 'moved') -eq 0 -and
         (Get-HzSha256 $dwgFile.FullName) -eq [string]$run.Fixture['dwg_sha256']) `
    -Evidence @{ note = 'the building changed - not the drawing and not the element - which is the whole reason this classification exists rather than falling out of a geometric diff' }

$ev = $(if ($rehosted.Count -ge 1) { Get-HzProp $rehosted[0] 'evidence' } else { $null })
$evNow = Get-HzProp $ev 'hosted_in_now'
$evImplied = Get-HzProp $ev 'drawing_implies_host'

Add-HzProbe -Run $run -Id 'R3' -Name 'the finding NAMES both walls, so a reviewer need not re-derive which is which' `
    -Expected 'hosted_in_now = the wall it was built in; drawing_implies_host = the new partition' `
    -Observed ("hosted_in_now={0} drawing_implies_host={1} built_in={2} new={3}" -f
        $evNow, $evImplied, $originalHost, $newWallId) `
    -Ok ($null -ne $evNow -and $null -ne $evImplied -and
         [long]$evNow -eq [long]$originalHost -and [long]$evImplied -eq $newWallId) `
    -Evidence @{ evidence = $ev }

Add-HzProbe -Run $run -Id 'R4' -Name 'nothing is applied: re-hosting is a review, because Revit cannot do it without a new element' `
    -Expected 'kind=review, automatic false, and no automatic action in the whole plan' `
    -Observed ("kind={0} automatic={1} plan_automatic={2}" -f
        $(if ($rehosted.Count -ge 1) { Get-HzProp $rehosted[0] 'kind' } else { '(none)' }),
        $(if ($rehosted.Count -ge 1) { Get-HzProp $rehosted[0] 'automatic' } else { '(none)' }),
        (Get-HzProp $after.Result 'automatic')) `
    -Ok ($rehosted.Count -eq 1 -and [string](Get-HzProp $rehosted[0] 'kind') -eq 'review' -and
         -not (Get-HzProp $rehosted[0] 'automatic') -and [int](Get-HzProp $after.Result 'automatic') -eq 0) `
    -Evidence @{ why = 'FamilyInstance.Host has no setter in 2023-2027, so applying this means deleting the door and building another - a new ElementId, and every tag, dimension and schedule line pointed at the old one broken' }

# STANDING, NOT ONE-SHOT. A finding that reports once and then goes quiet is
# worse than no finding: the second run reads as a model somebody fixed.
$again = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_cad_update' -Label 'update-again' -Arguments $updArgs
Add-HzProbe -Run $run -Id 'R5' -Name 'and it is still reported on the next run - a review nobody acted on does not go quiet' `
    -Expected 'rehosted 1 again, and still nothing automatic' `
    -Observed ("rehosted={0} automatic={1}" -f (Get-HzClassCount $again.Result 'rehosted'),
        (Get-HzProp $again.Result 'automatic')) `
    -Ok ((Get-HzClassCount $again.Result 'rehosted') -eq 1 -and [int](Get-HzProp $again.Result 'automatic') -eq 0) `
    -Evidence @{ counts_by_classification = $again.Result.counts_by_classification }

# =============================================================================
# W, A - the boundary and the vocabulary
# =============================================================================
Write-Host "`n== W, A: the boundary and the vocabulary ==" -ForegroundColor Cyan

$typedRehost = @()
foreach ($tool in @(Get-HzToolList $run)) {
    $name = [string](Get-HzProp $tool 'name')
    foreach ($prop in @('operation', 'kind')) {
        foreach ($v in @(Get-HzToolEnum -Run $run -Tool $name -Property $prop)) {
            if ([string]$v -match '(?i)rehost|set_host|change_host') { $typedRehost += ($name + '.' + $prop + '=' + $v) }
        }
    }
}
Add-HzProbe -Run $run -Id 'W1' -Name 'no typed command OFFERS to re-host, so nobody is invited to apply what cannot be applied' `
    -Expected 'the served contract has no rehost operation anywhere' `
    -Observed ("offered={0}" -f $(if ($typedRehost.Count -eq 0) { 'none' } else { $typedRehost -join ',' })) `
    -Ok ($typedRehost.Count -eq 0) `
    -Evidence @{ found = $typedRehost }

$counts = Get-HzProp $after.Result 'counts_by_classification'
$vocab = @('unchanged', 'added', 'removed', 'moved', 'reshaped', 'retyped', 'relayered', 'resized',
           'rehosted', 'manually_diverged', 'ambiguous', 'conflict')
$missing = @($vocab | Where-Object { $null -eq (Get-HzProp $counts $_) })
Add-HzProbe -Run $run -Id 'A1' -Name 'rehosted is published in the closed vocabulary, alongside every other classification and its zero' `
    -Expected 'all 12 classifications present, including the ones that are zero' `
    -Observed ("published={0} missing={1}" -f (@($vocab).Count - $missing.Count),
        $(if ($missing.Count -eq 0) { 'none' } else { $missing -join ',' })) `
    -Ok ($missing.Count -eq 0) `
    -Evidence @{ counts_by_classification = $counts
                 note = 'a key that disappears reads as "not measured" rather than "none found"' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
