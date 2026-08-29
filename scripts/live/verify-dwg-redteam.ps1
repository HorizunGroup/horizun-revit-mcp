#Requires -Version 5.1
<#
  TRYING TO MAKE IT DO THE WRONG THING.

  Every other harness here asks whether the bridge does what it says. This one
  asks whether it can be talked into something else - and the cases are aimed at
  the surface that is newest, because that is where the refusals have had the
  least chance to be wrong:

      the hosting rule, the structural flag, the opening bridge, the change
      classifications, and the plan/apply binding that ties them together.

  A probe here passes when the bridge REFUSES, or when it does the safe thing
  and says so. It fails when the bridge silently does something a person would
  not have asked for - which is the only failure mode that matters at this
  layer, because everything else announces itself.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-redteam' -Document $Document
$X = 900000.0

<#
  The text of a reply, whether it came back as an error or as a payload. A
  provocation that succeeds must be judged on what it actually did, not on
  whether the call threw.
#>
function Get-HzReplyText {
    param($Call)
    if ($Call.IsError) { return [string]$Call.Text }
    ($Call.Result | ConvertTo-Json -Depth 20 -Compress)
}

# =============================================================================
# THE FIXTURE - one drawing, converted honestly, to attack afterwards
# =============================================================================
Write-Host "`n== the fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

$truth = @(
    @{ name = 'W1'; x1 = $X; y1 = 0.0; x2 = ($X + 6000.0); y2 = 0.0 },
    @{ name = 'W2'; x1 = $X; y1 = 9000.0; x2 = ($X + 6000.0); y2 = 9000.0 }
)
$fixture = New-HzWallFixture -Run $run -Walls $truth -Tag 'red'
foreach ($k in $fixture.Keys) { $run.Fixture[$k] = $fixture[$k] }
Add-HzNote $run ("fixture {0}" -f $fixture.dwg_name)

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label 'link-red'
$layer = Get-HzWallLayer -Run $run -InstanceId $inst
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units
$set = New-HzWallRequirementSet -Layer $layer -Units $units -Id 'hz-live-red'

$plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-red' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set
    level_id = [long]$level.element_id }
$baseApply = @{
    target_document = $Document; instance_id = $inst; requirement_set = $set
    apply_binding = $plan.Result.apply_binding
    actions = $plan.Result.execute_plan_request.actions
    candidate_index = $plan.Result.candidate_index
}

# =============================================================================
# B - THE BINDING between a plan and its apply
# =============================================================================
Write-Host "`n== B: the binding ==" -ForegroundColor Cyan

# THE COORDINATES CHANGED AFTER THE PLAN WAS MADE. Everything else about the
# request is a real plan's; only the geometry is somebody else's idea.
$tampered = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
$firstRow = @($tampered[0].arguments.elements)[0]
$firstRow.start[1] = [double]$firstRow.start[1] + 4000.0
$firstRow.end[1] = [double]$firstRow.end[1] + 4000.0
$tamperCall = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'r-tampered' `
    -Arguments (Copy-HzArgs $baseApply @{ dry_run = $true; actions = $tampered })
Add-HzProbe -Run $run -Id 'B1' -Name 'an apply whose ACTIONS were edited after the plan is refused' `
    -Expected 'the binding covers the exact actions, so moved coordinates do not pass' `
    -Observed (Limit-HzText (Get-HzReplyText $tamperCall) 200) `
    -Ok ((Get-HzReplyText $tamperCall) -match 'stale_plan|actions_fingerprint|does not match') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $tamperCall) 600) }

# A BINDING FROM A DIFFERENT PLAN, with these actions.
$otherSet = New-HzWallRequirementSet -Layer $layer -Units $units -Id 'hz-live-red-other' -Height 2400.0
$otherPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-other' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $otherSet
    level_id = [long]$level.element_id }
$crossed = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'r-crossed' `
    -Arguments (Copy-HzArgs $baseApply @{ dry_run = $true
        apply_binding = $otherPlan.Result.apply_binding })
Add-HzProbe -Run $run -Id 'B2' -Name "one plan's binding cannot authorise another plan's actions" `
    -Expected 'refused - the binding names the requirement set and the actions it was made for' `
    -Observed (Limit-HzText (Get-HzReplyText $crossed) 200) `
    -Ok ($crossed.IsError -or (Get-HzReplyText $crossed) -match 'stale_plan|mismatch|does not match') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $crossed) 600) }

# =============================================================================
# S - THE REQUIREMENT SET
# =============================================================================
Write-Host "`n== S: the requirement set ==" -ForegroundColor Cyan

# A BRIDGE OF ZERO. "Join nothing" and "do not join" are different instructions
# and only one of them is expressible by omitting the key.
$zeroBridge = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$zeroBridge.rules[0].geometry | Add-Member -NotePropertyName bridge_openings_mm -NotePropertyValue 0 -Force
$zeroCall = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-bridge-zero' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $zeroBridge
    level_id = [long]$level.element_id }
Add-HzProbe -Run $run -Id 'S1' -Name 'bridge_openings_mm of zero is refused rather than read as "do not join"' `
    -Expected 'the set is refused, naming the key and what to do instead' `
    -Observed (Limit-HzText (Get-HzReplyText $zeroCall) 200) `
    -Ok ((Get-HzReplyText $zeroCall) -match 'bridge_openings_mm') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $zeroCall) 500) }

# A BRIDGE WIDE ENOUGH TO SWALLOW A ROOM. The two walls in this fixture are 9 m
# apart and PARALLEL, not collinear - a bridge of 20 m must not join them, and
# if a set ever does join something the reading must SAY so.
$wideBridge = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$wideBridge.rules[0].geometry | Add-Member -NotePropertyName bridge_openings_mm -NotePropertyValue 20000.0 -Force
$widePlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-bridge-wide' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $wideBridge
    level_id = [long]$level.element_id }
Add-HzProbe -Run $run -Id 'S2' -Name 'a huge opening bridge does NOT join two walls that are merely parallel' `
    -Expected '2 walls still, because collinearity is what separates a wall from the wall across the room' `
    -Observed ("walls={0}" -f (Get-HzPath $widePlan.Result 'counts_by_kind', 'wall')) `
    -Ok ([int](Get-HzPath $widePlan.Result 'counts_by_kind', 'wall') -eq 2) `
    -Evidence @{ counts = $widePlan.Result.counts_by_kind
                 note = 'the walls here are 9 m apart and parallel; the bridge offered was 20 m' }

# STRUCTURAL ON SOMETHING THAT CANNOT BEAR LOAD. A no-op that reads like a
# setting is worse than a refusal.
$roomStructural = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-red-room'; version = '1.0.0'; title = 'Structural room' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'rooms'; precedence = 10; discipline = 'architecture'
                 layers = @($layer); produces = 'room'; category = 'OST_Rooms'; structural = $true
                 geometry = @{ from = 'closed_loops'; min_area_mm2 = 100.0 } })
}
$roomPlan = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-room-structural' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $roomStructural
    level_id = [long]$level.element_id }
$roomRow = $null
if (-not $roomPlan.IsError) {
    $roomActions = @($roomPlan.Result.execute_plan_request.actions)
    if ($roomActions.Count -gt 0) { $roomRow = @($roomActions[0].arguments.elements)[0] }
}
Add-HzProbe -Run $run -Id 'S3' -Name 'structural on a ROOM never reaches the row - a silent no-op reads like a setting' `
    -Expected 'either refused, or planned with no structural key at all' `
    -Observed ("error={0} has_structural={1}" -f $roomPlan.IsError,
        ($null -ne (Get-HzProp $roomRow 'structural'))) `
    -Ok ($roomPlan.IsError -or $null -eq (Get-HzProp $roomRow 'structural')) `
    -Evidence @{ row = $roomRow }

# A LAYER GLOB THAT MATCHES NOTHING. A clean plan over nothing is the most
# dangerous possible answer: it looks like success.
$noLayer = $set | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$noLayer.rules[0].layers = @('Z-NO-SUCH-LAYER-*')
$noLayerPlan = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-no-layer' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $noLayer
    level_id = [long]$level.element_id }
$noLayerText = Get-HzReplyText $noLayerPlan
Add-HzProbe -Run $run -Id 'S4' -Name 'a rule whose layers match NOTHING says so, and does not read as a clean conversion' `
    -Expected 'zero actions AND the unclaimed layers named, or a refusal' `
    -Observed (Limit-HzText $noLayerText 200) `
    -Ok ($noLayerPlan.IsError -or
         (@(Get-HzPath $noLayerPlan.Result 'unclaimed').Count -gt 0) -or
         ($noLayerText -match 'layer_map')) `
    -Evidence @{ unclaimed = (Get-HzPath $noLayerPlan.Result 'unclaimed')
                 layer_map = (Get-HzPath $noLayerPlan.Result 'layer_map') }

# =============================================================================
# H - THE HOSTING RULE
# =============================================================================
Write-Host "`n== H: hosting ==" -ForegroundColor Cyan

$doorSet = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-red-door'; version = '1.0.0'; title = 'Doors' }
    source = @{ units = $units }
    tolerances = @{ point_mm = 300.0; gap_mm = 300.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'doors'; precedence = 10; discipline = 'architecture'
                 layers = @($layer); produces = 'door'; category = 'OST_Doors'
                 geometry = @{ from = 'point_clusters'; cluster_radius_mm = 900.0 } })
}
$doorNoWalls = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'r-door-no-walls' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $doorSet
    level_id = [long]$level.element_id }
Add-HzProbe -Run $run -Id 'H1' -Name 'a door planned into a model with no walls is refused, and the refusal names the order' `
    -Expected 'host_not_found - convert the wall layers first' `
    -Observed (Limit-HzText (Get-HzReplyText $doorNoWalls) 200) `
    -Ok ((Get-HzReplyText $doorNoWalls) -match 'host_not_found|host_too_far') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $doorNoWalls) 600) }

# A DOOR RULE THAT NAMES NO FAMILY. create_elements has no default FamilySymbol
# to fall back on, and a plan that quietly picked one would build a different
# building.
$doorNoFamily = $doorSet | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$doorNoFamily.requirement_set.id = 'hz-live-red-door-nofam'
$applied = Invoke-HzTool -Run $run -Tool 'horizun_create_elements' -Label 'r-door-no-family' -Arguments @{
    target_document = $Document; units = 'mm'; dry_run = $true
    elements = @(@{ kind = 'family_instance'; point = @($X, 0.0, 0.0)
                    level_id = [long]$level.element_id }) }
Add-HzProbe -Run $run -Id 'H2' -Name 'a family instance with no TYPE is refused rather than given a default' `
    -Expected 'type_id required - a substituted family builds a different building' `
    -Observed (Limit-HzText (Get-HzReplyText $applied) 200) `
    -Ok ((Get-HzReplyText $applied) -match 'type_id') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $applied) 400) }

# =============================================================================
# D - DOING IT TWICE
# =============================================================================
Write-Host "`n== D: doing it twice ==" -ForegroundColor Cyan

$wallsBefore = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'd-before').Count
$dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'd-dry' `
    -Arguments (Copy-HzArgs $baseApply @{ dry_run = $true })
$tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
$acts = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
foreach ($a in $acts) {
    $t = Get-HzProp $tokens $a.key
    if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
}
$key = New-HzKey $run 'd-apply'
$first = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'd-apply-1' `
    -Arguments (Copy-HzArgs $baseApply @{ dry_run = $false; actions = $acts; idempotency_key = $key })).Result
$afterFirst = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'd-after-1').Count

# THE SAME KEY AGAIN. A retry after a dropped connection must not build a second
# building.
$replay = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'd-apply-2' `
    -Arguments (Copy-HzArgs $baseApply @{ dry_run = $false; actions = $acts; idempotency_key = $key })
$afterReplay = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'd-after-2').Count

Add-HzProbe -Run $run -Id 'D1' -Name 'the SAME idempotency key applied twice builds the building once' `
    -Expected 'the wall count after the replay is the count after the first apply' `
    -Observed ("before={0} after={1} after_replay={2}" -f $wallsBefore, $afterFirst, $afterReplay) `
    -Ok ($afterFirst -gt $wallsBefore -and $afterReplay -eq $afterFirst) `
    -Evidence @{ first = $first.state; replay = (Limit-HzText (Get-HzReplyText $replay) 300) }

# AND THE HONEST HAZARD: the same plan, a NEW key. This is not a retry, it is a
# second instruction to build - and the bridge does what it is told. What must
# be true is that the caller was given the tool to notice: an audit of the model
# afterwards has to SAY there are now duplicates.
$auditDup = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_cad_model' -Label 'd-audit' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $set }
$dupCount = Get-HzPath $auditDup.Result 'counts_by_code', 'duplicate_in_model'
Add-HzProbe -Run $run -Id 'D2' -Name 'the audit publishes duplicate_in_model, so a second conversion is FINDABLE' `
    -Expected 'the code is reported with a count - even zero - rather than being absent' `
    -Observed ("duplicate_in_model={0}" -f $dupCount) `
    -Ok ($null -ne $dupCount) `
    -Evidence @{ counts = $auditDup.Result.counts_by_code
                 note = 'plan_from_cad builds what it is asked to build; the audit is what makes a second copy visible' }

# =============================================================================
# F - THE FILE ITSELF
# =============================================================================
Write-Host "`n== F: the file ==" -ForegroundColor Cyan

$notADwg = Join-Path 'C:\hz-live\dwg' ("HZ_RED_NOT_A_DWG_$($run.RunId).dwg")
Set-Content -LiteralPath $notADwg -Value 'this is not a drawing' -Encoding ascii -NoNewline
$badFile = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-not-a-dwg' -Arguments @{
    target_document = $Document; operation = 'add'; file_path = $notADwg; dry_run = $true }
Add-HzProbe -Run $run -Id 'F1' -Name 'a file that is not a DWG is refused by its CONTENT, not by its extension' `
    -Expected 'refused - the name ends in .dwg and the bytes do not' `
    -Observed (Limit-HzText (Get-HzReplyText $badFile) 200) `
    -Ok ($badFile.IsError) `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $badFile) 400) }

$emptyDwg = Join-Path 'C:\hz-live\dwg' ("HZ_RED_EMPTY_$($run.RunId).dwg")
Set-Content -LiteralPath $emptyDwg -Value '' -Encoding ascii -NoNewline
$emptyCall = Invoke-HzTool -Run $run -Tool 'horizun_manage_cad_links' -Label 'r-empty-dwg' -Arguments @{
    target_document = $Document; operation = 'add'; file_path = $emptyDwg; dry_run = $true }
Add-HzProbe -Run $run -Id 'F2' -Name 'a zero-byte drawing is refused rather than linked as an empty one' `
    -Expected 'refused - an empty link would read as a drawing with nothing in it' `
    -Observed (Limit-HzText (Get-HzReplyText $emptyCall) 200) `
    -Ok ($emptyCall.IsError) `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $emptyCall) 400) }

Remove-Item -LiteralPath $notADwg, $emptyDwg -Force -ErrorAction SilentlyContinue

# =============================================================================
# X - THE AWKWARD ONES
#
# Everything above attacks a refusal that already existed. These four attack the
# newest reasoning, where a wrong answer would be silent: two readers claiming
# one layer, a write with no provenance, a tie between two hosts, and two
# identical things on two layers.
# =============================================================================
Write-Host "`n== X: the awkward ones ==" -ForegroundColor Cyan

# X1 - ONE LAYER, TWO READERS. A drawing with both a straight wall and a curved
# one puts lines and arcs on the same layer. If the line rule consumes the arc's
# CHORDS as walls while the arc rule builds the curve, the drawing converts to
# one curved wall plus a dozen straight ones lying on top of it.
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$mixed = @(
    @{ kind = 'wall'; start = @($X, 0.0, 0.0); end = @(($X + 6000.0), 0.0, 0.0); height = 3000.0
       level_id = [long]$level.element_id },
    @{ kind = 'wall'; start = @(($X + 5000.0), 9000.0, 0.0); end = @(($X + 10000.0), 14000.0, 0.0)
       height = 3000.0; level_id = [long]$level.element_id
       arc = @{ centre = @(($X + 10000.0), 9000.0, 0.0); radius = 5000.0; clockwise = $true } }
)
$mixedMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'x-fixture' -Arguments @{
    target_document = $Document; units = 'mm'; elements = $mixed }
if ([int]$mixedMade.Apply.Result.created_verified -ne 2) {
    throw 'HARNESS: the mixed fixture needs one straight wall and one curved one'
}
$mixedView = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'x-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = "HZ_RED_MIX_$($run.RunId)" }) }
$mixedViewId = [long](@($mixedView.Apply.Result.rows)[0].element_id)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'x-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $mixedViewId
                   box = @(($X - 2000.0), -2000.0, ($X + 13000.0), 17000.0) }) }
$mixedPath = Join-Path 'C:\hz-live\dwg' ("HZ_RED_MIX_$($run.RunId).dwg")
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'x-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($mixedViewId); output_path = $mixedPath }
$mixedFile = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_RED_MIX_$($run.RunId)*.dwg"))[0]

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$mixInst = Add-HzCadLink -Run $run -DwgPath $mixedFile.FullName -Label 'x-link'
$mixLayer = Get-HzWallLayer -Run $run -InstanceId $mixInst
$mixFacts = Get-HzCadInstanceFacts -Run $run -InstanceId $mixInst
$mixUnits = [string]$mixFacts.declared_units

$twoReaders = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-red-mixed'; version = '1.0.0'; title = 'Lines and arcs, one layer' }
    source = @{ units = $mixUnits }
    tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(
        @{ id = 'curved'; precedence = 20; discipline = 'architecture'
           layers = @($mixLayer); produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
           geometry = @{ from = 'double_arcs'; min_thickness_mm = 100.0; max_thickness_mm = 500.0
                         min_overlap_fraction = 0.6 } },
        @{ id = 'straight'; precedence = 10; discipline = 'architecture'
           layers = @($mixLayer); produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
           geometry = @{ from = 'double_lines'; min_thickness_mm = 100.0; max_thickness_mm = 500.0
                         min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 } }
    )
}
$mixPlan = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'x-plan' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $twoReaders
    level_id = [long]$level.element_id }
$mixText = Get-HzReplyText $mixPlan
$mixWalls = 0
if (-not $mixPlan.IsError) { $mixWalls = [int](Get-HzPath $mixPlan.Result 'counts_by_kind', 'wall') }

Add-HzProbe -Run $run -Id 'X1' -Name 'a straight wall and a curved one on ONE layer do not become a wall per chord' `
    -Expected 'two walls, or a refusal - never the curve plus a straight wall for each of its chords' `
    -Observed ("error={0} walls={1}" -f $mixPlan.IsError, $mixWalls) `
    -Ok ($mixPlan.IsError -or $mixWalls -le 3) `
    -Evidence @{ counts = $(if ($mixPlan.IsError) { $null } else { $mixPlan.Result.counts_by_kind })
                 reply = (Limit-HzText $mixText 400)
                 note = 'the arc reader consumes the chords it read the arc from; a line reader on the same layer must not build them too' }

# X2 - A WRITE WITH NO PROVENANCE INDEX. Elements built without provenance are
# invisible to every later audit and every incremental update: the next run
# reports the whole conversion as missing and builds it again.
$straightSet = New-HzWallRequirementSet -Layer $mixLayer -Units $mixUnits -Id 'hz-live-red-noprov'
$provPlan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'x-prov-plan' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $straightSet
    level_id = [long]$level.element_id }
$noIndex = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'x-no-index' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $straightSet
    apply_binding = $provPlan.Result.apply_binding
    actions = $provPlan.Result.execute_plan_request.actions
    dry_run = $true }
$noIndexText = Get-HzReplyText $noIndex
Add-HzProbe -Run $run -Id 'X2' -Name 'an apply with no candidate_index is refused, or says the elements will carry no provenance' `
    -Expected 'never a silent write of elements no audit and no update can ever see' `
    -Observed (Limit-HzText $noIndexText 220) `
    -Ok ($noIndex.IsError -or $noIndexText -match 'candidate_index|provenance') `
    -Evidence @{ reply = (Limit-HzText $noIndexText 600)
                 note = 'without provenance the next incremental run reports the whole conversion as missing' }

# X3 - A HOST TIE. A symbol exactly between two parallel walls has two nearest
# hosts at the same distance. Whatever it picks, it must pick the SAME one twice
# - an answer that alternates makes every update report a rehosting.
$tieWalls = @(
    @{ kind = 'wall'; start = @($X, 20000.0, 0.0); end = @(($X + 6000.0), 20000.0, 0.0)
       height = 3000.0; level_id = [long]$level.element_id },
    @{ kind = 'wall'; start = @($X, 21000.0, 0.0); end = @(($X + 6000.0), 21000.0, 0.0)
       height = 3000.0; level_id = [long]$level.element_id }
)
$null = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'x-tie-walls' -Arguments @{
    target_document = $Document; units = 'mm'; elements = $tieWalls }

$tieSet = @{
    schema = 'horizun.cad-requirements/1'
    requirement_set = @{ id = 'hz-live-red-tie'; version = '1.0.0'; title = 'A door between two walls' }
    source = @{ units = $mixUnits }
    tolerances = @{ point_mm = 700.0; gap_mm = 700.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
    rules = @(@{ id = 'doors'; precedence = 10; discipline = 'architecture'
                 layers = @($mixLayer); produces = 'door'; category = 'OST_Doors'
                 geometry = @{ from = 'point_clusters'; cluster_radius_mm = 20000.0 } })
}
$tieA = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'x-tie-a' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $tieSet
    level_id = [long]$level.element_id }
$tieB = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'x-tie-b' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $tieSet
    level_id = [long]$level.element_id }

function Get-HzHostIds {
    param($Call)
    if ($Call.IsError) { return @() }
    $ids = @()
    foreach ($action in @(Get-HzPath $Call.Result 'execute_plan_request', 'actions')) {
        foreach ($el in @(Get-HzPath $action 'arguments', 'elements')) {
            $h = Get-HzProp $el 'host_id'
            if ($h) { $ids += [long]$h }
        }
    }
    $ids
}
$hostsA = @(Get-HzHostIds $tieA)
$hostsB = @(Get-HzHostIds $tieB)
Add-HzProbe -Run $run -Id 'X3' -Name 'the same drawing planned twice resolves the same HOST both times' `
    -Expected 'identical host ids - an answer that alternates reports a rehosting on every run' `
    -Observed ("first=[{0}] second=[{1}] errorA={2} errorB={3}" -f ($hostsA -join ','), ($hostsB -join ','),
        $tieA.IsError, $tieB.IsError) `
    -Ok (($hostsA -join ',') -eq ($hostsB -join ',')) `
    -Evidence @{ first = $hostsA; second = $hostsB
                 note = 'a tie must break the same way twice, whichever way it breaks' }

# X4 - THE SAME PLAN, READ TWICE. Nothing in the model changed between these two
# calls, so every id, every fingerprint and every count must be identical. A
# reading that depends on collection order is a reading that cannot be audited.
$readA = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'x-stable-a' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $straightSet
    level_id = [long]$level.element_id }
$readB = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'x-stable-b' -Arguments @{
    target_document = $Document; instance_id = $mixInst; requirement_set = $straightSet
    level_id = [long]$level.element_id }
$fpA = [string](Get-HzPath $readA.Result 'apply_binding', 'plan_fingerprint')
$fpB = [string](Get-HzPath $readB.Result 'apply_binding', 'plan_fingerprint')
$actA = [string](Get-HzPath $readA.Result 'apply_binding', 'actions_fingerprint')
$actB = [string](Get-HzPath $readB.Result 'apply_binding', 'actions_fingerprint')
Add-HzProbe -Run $run -Id 'X4' -Name 'reading one drawing twice yields the same plan, byte for byte' `
    -Expected 'identical plan and actions fingerprints - a reading that depends on collection order cannot be audited' `
    -Observed ("plan_fp_equal={0} actions_fp_equal={1}" -f ($fpA -eq $fpB), ($actA -eq $actB)) `
    -Ok ($fpA -eq $fpB -and $actA -eq $actB -and $fpA -ne '') `
    -Evidence @{ plan_fingerprint = $fpA; actions_fingerprint = $actA }

# =============================================================================
# N - THE SURFACES ADDED THIS PHASE
# =============================================================================
Write-Host "`n== N: naming, holes, separators, parameters ==" -ForegroundColor Cyan

<#
  A rule built on the honest wall set, with one thing about it changed. Each of
  these is a way to get the bridge to decide something a drawing cannot answer.
#>
function New-HzAttackSet {
    param([string]$Id, [hashtable]$Rule)
    $base = @{ id = $Id; precedence = 10; discipline = 'architecture'
               layers = @($layer); produces = 'wall'; category = 'OST_Walls'; height_mm = 3000.0
               geometry = @{ from = 'double_lines'; min_thickness_mm = 100.0; max_thickness_mm = 400.0
                             min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 } }
    foreach ($k in $Rule.Keys) { $base[$k] = $Rule[$k] }
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = "hz-red-$Id"; version = '1.0.0'; title = "Red team $Id" }
        source = @{ units = $units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @($base)
    }
}

function Invoke-HzAttack {
    param([string]$Label, $Set)
    Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label $Label -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $Set
        level_id = [long]$level.element_id }
}

# THE SAME NAME TWICE IN ONE LIST. Revit refuses a duplicate grid name at
# creation, so a plan that carried one would fail after building part of a batch.
$dupes = Invoke-HzAttack 'n-dupe-names' (New-HzAttackSet 'dupe' @{
    produces = 'grid'; category = 'OST_Grids'
    geometry = @{ from = 'single_lines'; min_length_mm = 1000.0 }
    naming = @{ strategy = 'ordered'; axis = 'x'; values = @('A', 'B', 'A') } })
Add-HzProbe -Run $run -Id 'N1' -Name 'a naming list that repeats a name is refused before anything is built' `
    -Expected 'refused, naming the repeat' `
    -Observed (Limit-HzText (Get-HzReplyText $dupes) 220) `
    -Ok ((Get-HzReplyText $dupes) -match "(?i)dupli|repeat|twice|already") `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $dupes) 600) }

# ORDERED, WITH NO AXIS. Ordering without one is ordering by whatever Revit
# returned first, which is not stable between runs let alone between machines.
$noAxis = Invoke-HzAttack 'n-no-axis' (New-HzAttackSet 'noaxis' @{
    produces = 'grid'; category = 'OST_Grids'
    geometry = @{ from = 'single_lines'; min_length_mm = 1000.0 }
    naming = @{ strategy = 'ordered'; values = @('A', 'B') } })
Add-HzProbe -Run $run -Id 'N2' -Name 'an ordered naming with no axis is refused - an implicit order is nobody''s decision' `
    -Expected 'refused, naming axis' `
    -Observed (Limit-HzText (Get-HzReplyText $noAxis) 220) `
    -Ok ((Get-HzReplyText $noAxis) -match '(?i)axis') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $noAxis) 600) }

# PERMISSION TO CUT A LOAD-BEARING SLAB, on a rule that cuts nothing. A key that
# reaches a builder which ignores it reads as an authorisation somebody gave.
$falsePermission = Invoke-HzAttack 'n-permission' (New-HzAttackSet 'permission' @{
    allow_structural = $true })
Add-HzProbe -Run $run -Id 'N3' -Name 'permission to cut a structural slab, on a rule that cuts nothing, is refused' `
    -Expected 'refused - the key would sit in the set reading as an authorisation nothing asked for' `
    -Observed (Limit-HzText (Get-HzReplyText $falsePermission) 220) `
    -Ok ((Get-HzReplyText $falsePermission) -match 'allow_structural') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $falsePermission) 600) }

# TWO STOREYS ON A THING HOSTED ON ONE. A shaft runs between two; a wall does not,
# and a set that says otherwise has one of them wrong.
$twoLevels = Invoke-HzAttack 'n-two-levels' (New-HzAttackSet 'twolevels' @{
    base_level = 'L1'; top_level = 'L2' })
Add-HzProbe -Run $run -Id 'N4' -Name 'a wall rule that names two storeys is refused, because only a shaft runs between them' `
    -Expected 'refused, naming base_level/top_level' `
    -Observed (Limit-HzText (Get-HzReplyText $twoLevels) 220) `
    -Ok ((Get-HzReplyText $twoLevels) -match 'base_level|top_level') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $twoLevels) 600) }

# A PARAMETER WITH NO VALUE. Writing an empty one is a decision; omitting the
# parameter is the other decision, and only one of them was written down.
$empty = Invoke-HzAttack 'n-empty-param' (New-HzAttackSet 'emptyparam' @{
    parameters = @{ 'Comments' = @{ scope = 'instance' } } })
Add-HzProbe -Run $run -Id 'N5' -Name 'a parameter declared with no value is refused rather than written empty' `
    -Expected 'refused - omit the parameter rather than declaring one with nothing to write' `
    -Observed (Limit-HzText (Get-HzReplyText $empty) 220) `
    -Ok ((Get-HzReplyText $empty) -match 'declares no value') `
    -Evidence @{ reply = (Limit-HzText (Get-HzReplyText $empty) 600) }

# THE DANGEROUS ONE THAT IS ALLOWED. A type-scope write is legitimate and reaches
# every instance of that type in the model - including ones this conversion never
# touched. It is not refused; what must never happen is that it goes unsaid.
$redWalls = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'red-walls')
if ($redWalls.Count -lt 1) { throw 'HARNESS: no wall to aim a type write at' }
$typeWrite = Invoke-HzTool -Run $run -Tool 'horizun_write_params_verified' -Label 'n-type-scope' -Arguments @{
    target_document = $Document; dry_run = $true
    writes = @(@{ target_id = [long]$redWalls[0].element_id; parameter = 'Type Comments'; value = 'HZ red team'
                  scope = 'type' }) }
$typeText = Get-HzReplyText $typeWrite
$reach = Get-HzPath $typeWrite.Result 'elements_that_would_change'
$collateral = Get-HzPath $typeWrite.Result 'collateral_elements'
Add-HzProbe -Run $run -Id 'N6' -Name 'a TYPE write is allowed and its blast radius is stated before the token is spent' `
    -Expected 'a rehearsal that says how many elements would change, and how many were never named' `
    -Observed ("would_change={0} collateral={1}" -f $reach, $collateral) `
    -Ok ($null -ne $reach -and $null -ne $collateral) `
    -Evidence @{ reply = (Limit-HzText $typeText 700)
                 note = 'a type write reaches every instance of that type, including ones this conversion did not create - refusing it would be wrong and hiding it would be worse' }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
