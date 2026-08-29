#Requires -Version 5.1
<#
  HOW THIS BEHAVES AT SIZE, AGAINST LIMITS SET BEFORE THE RUN.

  Every reading in this repository has been proved on a drawing with a handful
  of walls in it. A real floor plan has hundreds, and the ways a conversion
  fails at that size are not the ways it fails at three: a pairing search that
  compares every candidate against every other is invisible at ten and takes
  minutes at a thousand.

  THE LIMITS BELOW ARE DECLARED BEFORE ANY MEASUREMENT, AND ARE NOT MOVED
  AFTERWARDS. That is the whole discipline: a budget chosen once a number is
  known is not a budget, it is a description. If a stage exceeds its limit the
  probe FAILS and the limit stays where it is until somebody decides, out loud,
  that the number was wrong.

  They are deliberately generous. The point is not to pin today's milliseconds -
  that would fail on a different machine for no reason worth reading - but to
  catch the shape of failure that matters: work that grows faster than the
  drawing does. So the last probe compares the RATIO of the largest fixture's
  cost to the smallest against the ratio of their sizes.

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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-performance' -Document $Document
$X = 900000.0

# =============================================================================
# THE BUDGET, declared here and nowhere else.
# =============================================================================
$sizes = [ordered]@{
    S = [ordered]@{ walls = 12;  link_ms = 60000;  read_ms = 60000;  plan_ms = 120000; apply_ms = 300000 }
    M = [ordered]@{ walls = 60;  link_ms = 90000;  read_ms = 90000;  plan_ms = 180000; apply_ms = 600000 }
    L = [ordered]@{ walls = 200; link_ms = 180000; read_ms = 180000; plan_ms = 300000; apply_ms = 900000 }
}

# How much worse the LARGEST may be than the smallest, per wall. Anything above
# this is work growing faster than the drawing - the failure that only shows at
# size, and the one worth a harness.
$maxCostGrowthPerWall = 6.0

foreach ($k in $sizes.Keys) {
    $run.Expected["limit_${k}_walls"] = $sizes[$k].walls
    $run.Expected["limit_${k}_read_ms"] = $sizes[$k].read_ms
    $run.Expected["limit_${k}_plan_ms"] = $sizes[$k].plan_ms
    $run.Expected["limit_${k}_apply_ms"] = $sizes[$k].apply_ms
}
$run.Expected['max_cost_growth_per_wall'] = $maxCostGrowthPerWall
$run.Expected['limits_declared'] = 'before any measurement, in the harness source, and not moved afterwards'

<#
  A drawing of N parallel walls, built and exported the way every other fixture
  here is. Parallel and separated so the reading has real work to do - every
  wall is a thickness-valid pairing candidate for its neighbours - without any
  two of them being genuinely ambiguous.
#>
function New-HzSizedFixture {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][int]$Walls, [Parameter(Mandatory)][string]$Tag)
    $null = Reset-HzDocument $Run
    $level = Get-HzFirstLevel $Run

    $pitch = 1500.0
    $rows = @()
    for ($i = 0; $i -lt $Walls; $i++) {
        $y = $i * $pitch
        $rows += @{ kind = 'wall'; start = @($X, $y, 0.0); end = @(($X + 6000.0), $y, 0.0)
                    height = 3000.0; level_id = [long]$level.element_id }
    }

    # Revit's own batch limit is what it is; the fixture is built in chunks so
    # the SIZE of the drawing is not capped by the size of one call.
    $made = 0
    for ($from = 0; $from -lt $rows.Count; $from += 50) {
        $slice = @($rows[$from..([Math]::Min($from + 49, $rows.Count - 1))])
        $r = Invoke-HzWrite -Run $Run -Tool 'horizun_create_elements' -Label "fx-$Tag-$from" -Arguments @{
            target_document = $Run.Document; units = 'mm'; elements = $slice }
        $made += [int]$r.Apply.Result.created_verified
    }
    if ($made -ne $Walls) { throw ("HARNESS: fixture {0} wanted {1} walls and Revit verified {2}" -f $Tag, $Walls, $made) }

    $viewName = "HZ_PERF_${Tag}_$($Run.RunId)"
    $view = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-view" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                       name = $viewName }) }
    $viewId = [long](@($view.Apply.Result.rows)[0].element_id)
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_manage_views' -Label "fx-$Tag-crop" -Arguments @{
        target_document = $Run.Document; units = 'mm'
        actions = @(@{ operation = 'set_crop'; view_id = $viewId
                       box = @(($X - 2000.0), -2000.0, ($X + 8000.0), (($Walls * $pitch) + 2000.0)) }) }
    $dwg = Join-Path 'C:\hz-live\dwg' ("HZ_PERF_${Tag}_$($Run.RunId).dwg")
    $null = Invoke-HzWrite -Run $Run -Tool 'horizun_export' -Label "fx-$Tag-export" -Arguments @{
        target_document = $Run.Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwg }
    $file = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter ("HZ_PERF_${Tag}_$($Run.RunId)*.dwg"))[0]
    if ($null -eq $file) { throw "HARNESS: fixture $Tag exported no DWG" }
    [ordered]@{ tag = $Tag; walls = $Walls; dwg_path = $file.FullName; dwg_name = $file.Name
                dwg_sha256 = (Get-HzSha256 $file.FullName); dwg_bytes = $file.Length }
}

$measured = [ordered]@{}

foreach ($tag in @($sizes.Keys)) {
    $limit = $sizes[$tag]
    Write-Host ("`n== {0}: {1} walls ==" -f $tag, $limit.walls) -ForegroundColor Cyan

    $fixture = New-HzSizedFixture -Run $run -Walls $limit.walls -Tag $tag
    $run.Fixture["${tag}_dwg_name"] = $fixture.dwg_name
    $run.Fixture["${tag}_dwg_sha256"] = $fixture.dwg_sha256
    $run.Fixture["${tag}_dwg_bytes"] = $fixture.dwg_bytes
    $run.Fixture["${tag}_walls"] = $fixture.walls

    $null = Reset-HzDocument $run
    $level = Get-HzFirstLevel $run

    $linkClock = [Diagnostics.Stopwatch]::StartNew()
    $inst = Add-HzCadLink -Run $run -DwgPath $fixture.dwg_path -Label "perf-link-$tag"
    $linkClock.Stop()

    $layer = Get-HzWallLayer -Run $run -InstanceId $inst
    $facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
    $set = New-HzWallRequirementSet -Layer $layer -Units ([string]$facts.declared_units) `
        -Id "hz-live-perf-$tag"

    $readClock = [Diagnostics.Stopwatch]::StartNew()
    $geometry = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label "perf-read-$tag" -Arguments @{
        mode = 'geometry'; instance_id = $inst; max_rows = 5000 }
    $readClock.Stop()

    $planClock = [Diagnostics.Stopwatch]::StartNew()
    $plan = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label "perf-plan-$tag" -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $set
        level_id = [long]$level.element_id }
    $planClock.Stop()

    $applyArgs = @{
        target_document = $Document; instance_id = $inst; requirement_set = $set
        apply_binding = $plan.Result.apply_binding
        actions = $plan.Result.execute_plan_request.actions
        candidate_index = $plan.Result.candidate_index
    }
    $applyClock = [Diagnostics.Stopwatch]::StartNew()
    $dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label "perf-dry-$tag" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
    $tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
    $acts = @($plan.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $acts) {
        $t = Get-HzProp $tokens $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    $applied = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label "perf-apply-$tag" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $acts
            idempotency_key = (New-HzKey $run "perf-apply-$tag") })).Result
    $applyClock.Stop()

    $walls = [int]$limit.walls
    $m = [ordered]@{
        walls = $walls
        segments = [int](Get-HzProp $geometry.Result 'segments_matching')
        link_ms = [int]$linkClock.ElapsedMilliseconds
        read_ms = [int]$readClock.ElapsedMilliseconds
        plan_ms = [int]$planClock.ElapsedMilliseconds
        apply_ms = [int]$applyClock.ElapsedMilliseconds
        planned = [int](Get-HzPath $plan.Result 'counts_by_kind', 'wall')
        created = [int]$applied.created_verified
        truncated = [bool](Get-HzProp $geometry.Result 'truncated')
    }
    $m['total_ms'] = $m.link_ms + $m.read_ms + $m.plan_ms + $m.apply_ms
    $m['ms_per_wall'] = [Math]::Round($m.total_ms / [double]$walls, 2)
    $measured[$tag] = $m
    Add-HzNote $run ("{0}: {1} walls, {2} segments, read {3} ms, plan {4} ms, apply {5} ms, {6} ms/wall" -f
        $tag, $m.walls, $m.segments, $m.read_ms, $m.plan_ms, $m.apply_ms, $m.ms_per_wall)

    Add-HzProbe -Run $run -Id "$tag-1" -Name "the drawing is READ within the budget declared for $tag" `
        -Expected ("harvest and query under {0} ms, and the reading COMPLETE rather than truncated" -f $limit.read_ms) `
        -Observed ("read={0} ms segments={1} truncated={2}" -f $m.read_ms, $m.segments, $m.truncated) `
        -Ok ($m.read_ms -le $limit.read_ms -and -not $m.truncated) `
        -Evidence $m

    Add-HzProbe -Run $run -Id "$tag-2" -Name "the reading becomes a PLAN within the budget declared for $tag" `
        -Expected ("under {0} ms, and one wall planned per wall drawn" -f $limit.plan_ms) `
        -Observed ("plan={0} ms planned={1} of {2}" -f $m.plan_ms, $m.planned, $walls) `
        -Ok ($m.plan_ms -le $limit.plan_ms -and $m.planned -eq $walls) `
        -Evidence $m

    Add-HzProbe -Run $run -Id "$tag-3" -Name "the plan is APPLIED and verified within the budget declared for $tag" `
        -Expected ("under {0} ms, every wall created AND re-read" -f $limit.apply_ms) `
        -Observed ("apply={0} ms created={1} of {2}" -f $m.apply_ms, $m.created, $walls) `
        -Ok ($m.apply_ms -le $limit.apply_ms -and $m.created -eq $walls) `
        -Evidence $m
}

# =============================================================================
# G - HOW THE COST GROWS
# =============================================================================
Write-Host "`n== G: how the cost grows ==" -ForegroundColor Cyan

$small = $measured['S']
$large = $measured['L']
$growth = if ($small.ms_per_wall -gt 0) { $large.ms_per_wall / $small.ms_per_wall } else { 0 }

Add-HzProbe -Run $run -Id 'G1' -Name 'cost per wall does not RUN AWAY between the smallest drawing and the largest' `
    -Expected ("the largest costs no more than {0}x per wall - a search that compares everything against " +
               "everything is invisible at 12 walls and fatal at 1000" -f $maxCostGrowthPerWall) `
    -Observed ("S={0} ms/wall L={1} ms/wall growth={2}x over {3}x the walls" -f
        $small.ms_per_wall, $large.ms_per_wall, [Math]::Round($growth, 2),
        [Math]::Round($large.walls / [double]$small.walls, 1)) `
    -Ok ($growth -le $maxCostGrowthPerWall) `
    -Evidence @{ small = $small; large = $large
                 growth_per_wall = [Math]::Round($growth, 3)
                 limit = $maxCostGrowthPerWall
                 note = 'the limit was declared in the harness source before any of this was measured' }

Add-HzProbe -Run $run -Id 'G2' -Name 'every size read the WHOLE drawing - a truncated reading would flatter every number' `
    -Expected 'no size reports truncated, and segments rise with walls' `
    -Observed ("S={0} M={1} L={2} segments; truncated: {3}/{4}/{5}" -f
        $measured['S'].segments, $measured['M'].segments, $measured['L'].segments,
        $measured['S'].truncated, $measured['M'].truncated, $measured['L'].truncated) `
    -Ok (-not $measured['S'].truncated -and -not $measured['M'].truncated -and -not $measured['L'].truncated -and
         $measured['L'].segments -gt $measured['S'].segments) `
    -Evidence @{ measured = $measured }

foreach ($tag in @($measured.Keys)) { $run.Fixture["measured_$tag"] = $measured[$tag] }

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
