#Requires -Version 5.1
# Exercises element-kinds-flex.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# THE FAKES COPY THE REAL REPLY SHAPES (MEASURED 2026-09-26 in Revit 2026): create_elements
# answers rows + verification + application.state and NO top-level postconditions; the
# create_area_plan rehearsal without a scheme is a refusal listing the schemes by id. An
# earlier version faked data.postconditions on create_elements, so the probe checked a field
# the real reply never has and every creation read as unverified in the live matrix.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'element-kinds-flex.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'element-kinds-flex' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function Reset-Fake([bool]$fixtureHasSprinkler) {
    $script:nextId = 2000
    $script:deleted = $null
    $script:sent = @{}
    $script:calls = New-Object System.Collections.ArrayList
    $script:sprinklerPresent = $fixtureHasSprinkler
}

$fakeCall = {
    param($tool, $arguments)
    [void]$script:calls.Add(@{ Tool = $tool; Arguments = $arguments })
    if ($tool -eq 'horizun_query_model') {
        $cat = $arguments.categories[0]
        $rows = @()
        if ($arguments.include_types -eq $true) {
            $rows = switch ($cat) {
                'OST_Sprinklers' { if ($script:sprinklerPresent) { @(@{ element_id = 101; is_element_type = $true; family = 'M_Sprinkler - Pendent - Hosted'; type = '15 mm Pendent' }) } else { @() } }
                'OST_FlexPipeCurves' { @(@{ element_id = 102; is_element_type = $true; family = 'Flex Pipe Round'; type = 'Flex - Round' }) }
                'OST_PipingSystem' { @(@{ element_id = 103; is_element_type = $true; family = 'Piping System'; type = 'Hydronic Supply' }) }
                'OST_FlexDuctCurves' { @(@{ element_id = 107; is_element_type = $true; family = 'Flex Duct Rectangular' }, @{ element_id = 104; is_element_type = $true; family = 'Flex Duct Round' }) }
                'OST_DuctSystem' { @(@{ element_id = 105; is_element_type = $true; family = 'Duct System'; type = 'Supply Air' }) }
                # The categories the old probe asked: a real fixture answers types there that are NOT flex.
                'OST_PipeCurves' { @(@{ element_id = 902; is_element_type = $true; family = 'Pipe Types'; type = 'PVC - DWV' }) }
                'OST_DuctCurves' { @(@{ element_id = 904; is_element_type = $true; family = 'Rectangular Duct'; type = 'Radius Elbows' }) }
                default { @() }
            }
        }
        return @{ isError = $false; data = [pscustomobject]@{ matched_total = @($rows).Count; rows = @($rows | ForEach-Object { [pscustomobject]$_ }) } }
    }
    if ($tool -eq 'horizun_manage_views' -and $arguments.dry_run -eq $true) {
        return @{ isError = $true; data = $null; text = "Error: create_area_plan needs area_scheme_id or area_scheme_name. Schemes: 'Gross Building' (id 106), 'Rentable' (id 108). Nothing was written." }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}

function Real-CreateReply($rows) {
    return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{
        dry_run = $false; transaction_status = 'Committed'; requested = @($rows).Count; created_verified = @($rows).Count; rows = @($rows)
        verification = [pscustomobject]@{ intended = @($rows).Count; actual = @($rows).Count; verified = $true }
        application = [pscustomobject]@{ state = 'verified_applied'; fully_applied = $true } } } }
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $rows = foreach ($el in @($arguments.elements)) {
                $script:nextId++
                $id = $script:nextId
                switch ($el.kind) {
                    'area_boundary' {
                        $ids = @($id, ($id + 1), ($id + 2), ($id + 3))
                        $script:nextId += 3
                        [pscustomobject]@{ element_id = $ids[0]; element_ids = $ids; verified = $true }
                    }
                    'space' {
                        if ($key -like '*space-enclosed') { [pscustomobject]@{ element_id = $id; verified = $true; area_sqft = 172.2; area_enclosed = $true } }
                        else { [pscustomobject]@{ element_id = $id; verified = $true; area_sqft = 0; area_enclosed = $false } }
                    }
                    'area' { [pscustomobject]@{ element_id = $id; verified = $true; area_sqft = 172.0; area_enclosed = $true } }
                    default { [pscustomobject]@{ element_id = $id; verified = $true } }
                }
                if ($el.kind -eq 'flex_duct') { $script:flexDuctId = $id }
                if ($el.kind -eq 'flex_pipe') { $script:flexId = $id }
                if ($el.kind -eq 'floor') { $script:floorId = $id }
            }
            return (Real-CreateReply @($rows))
        }
        'horizun_manage_views' {
            $script:nextId++
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); application = [pscustomobject]@{ state = 'verified_applied' } } } }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        default { return @{ stage = 'apply'; answer = @{ isError = $true; text = "unexpected apply $tool" } } }
    }
}

# ---- a fixture WITH a sprinkler type ----
Reset-Fake $true
$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$cases = @(& $module.Run $ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
$sprinkler = 'sprinkler: place at a point and re-read position, type and level'
$space = 'space: place at a point on a level and re-read the point and level'
$area = 'area_boundary + area: close a loop on an area plan view and re-read the area'

Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0))
Check 'the sprinkler is hosted on the own floor, at a point inside it' (
    ($by[$sprinkler].Outcome -eq 'pass') -and ([long]$script:sent['t1-ekf-sprinkler'].elements[0].host_id -eq $script:floorId))
Check 'flex types are asked in their own categories, never PipeCurves/DuctCurves' (
    (@($script:calls | Where-Object { $_.Tool -eq 'horizun_query_model' -and @($_.Arguments.categories)[0] -eq 'OST_FlexPipeCurves' }).Count -ge 1) -and
    (@($script:calls | Where-Object { $_.Tool -eq 'horizun_query_model' -and @($_.Arguments.categories)[0] -in @('OST_PipeCurves', 'OST_DuctCurves') }).Count -eq 0))
Check 'flex_pipe passes and sends a 3-point path with the flex type' (
    ($by['flex_pipe: create a path and re-read its points, diameter and level'].Outcome -eq 'pass') -and
    (@($script:sent['t1-ekf-flex-pipe'].elements[0].points).Count -eq 3) -and ([long]$script:sent['t1-ekf-flex-pipe'].elements[0].type_id -eq 102))
Check 'flex_duct prefers the round flex type and a 150 mm diameter' (
    ($by['flex_duct: create a path and re-read its points, size and level'].Outcome -eq 'pass') -and
    ([long]$script:sent['t1-ekf-flex-duct'].elements[0].type_id -eq 104) -and ([double]$script:sent['t1-ekf-flex-duct'].elements[0].diameter -eq 150) -and
    (-not $script:sent.ContainsKey('t1-ekf-flex-duct-rect')))
Check 'space passes with BOTH an unbounded and an enclosed space' (
    ($by[$space].Outcome -eq 'pass') -and ($by[$space].Detail -match 'unbounded') -and ($by[$space].Detail -match 'enclosed space'))
Check 'the enclosed space stands inside four own walls' (@($script:sent['t1-ekf-space-walls'].elements | Where-Object { $_.kind -eq 'wall' }).Count -eq 4)
Check 'the area scheme comes from the create_area_plan refusal, and the area passes enclosed' (
    ($by[$area].Outcome -eq 'pass') -and ($by[$area].Detail -match 'area_enclosed=True') -and
    ([long]$script:sent['t1-ekf-areaview'].actions[0].area_scheme_id -eq 106))
$chain = @($script:sent['t1-ekf-area-boundary'].elements[0].profile[0])
Check 'area_boundary sends ONE chain of 5 points that closes on itself (points, not segment pairs)' (($chain.Count -eq 5) -and (@($chain[0]).Count -eq 3) -and (($chain[0] -join ',') -eq ($chain[4] -join ',')))
Check 'cleanup deletes what was created, the flex runs and the four walls included' (
    ($script:deleted.Count -ge 14) -and ($script:deleted -contains $script:flexId) -and ($script:deleted -contains $script:flexDuctId))

# ---- an unbounded space reported enclosed (or the reverse) is a failure, not a pass ----
Reset-Fake $true
$fakeApplyLie = {
    param($tool, $arguments, $key)
    $r = & $fakeApply $tool $arguments $key
    if ($key -like '*space-enclosed') { @($r.answer.data.rows)[0].area_enclosed = $false }
    return $r
}
$ctxLie = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't3'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApplyLie }
$lie = @(& $module.Run $ctxLie); $lieBy = @{}; foreach ($c in $lie) { $lieBy[$c.Name] = $c }
Check 'an enclosed space that reads unbounded fails the case' ($lieBy[$space].Outcome -eq 'fail')

# ---- a reply WITHOUT application.state=verified_applied is not a pass ----
Reset-Fake $true
$fakeApplyPartial = {
    param($tool, $arguments, $key)
    $r = & $fakeApply $tool $arguments $key
    if ($tool -eq 'horizun_create_elements' -and $arguments.elements[0].kind -eq 'flex_pipe') { $r.answer.data.application.state = 'partial' }
    return $r
}
$ctxPartial = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't4'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApplyPartial }
$partial = @(& $module.Run $ctxPartial); $partialBy = @{}; foreach ($c in $partial) { $partialBy[$c.Name] = $c }
Check 'a partial application fails the flex_pipe case' ($partialBy['flex_pipe: create a path and re-read its points, diameter and level'].Outcome -eq 'fail')

# ---- a fixture WITHOUT a sprinkler type: that case is not_covered, nothing is copied in ----
Reset-Fake $false
$ctxNo = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't5'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$no = @(& $module.Run $ctxNo); $noBy = @{}; foreach ($c in $no) { $noBy[$c.Name] = $c }
Check 'a fixture without a sprinkler type reports that case not_covered and places none' (
    ($noBy[$sprinkler].Outcome -eq 'not_covered') -and -not $script:sent.ContainsKey('t5-ekf-sprinkler'))

Reset-Fake $true
$closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
$shut = @(& $module.Run $closed)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))

# ---- a rectangular flex duct: the round attempt is refused, the width/height retry passes ----
Reset-Fake $true
$fakeApplyRect = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    if ($tool -eq 'horizun_create_elements' -and $arguments.elements[0].kind -eq 'flex_duct' -and $arguments.elements[0].ContainsKey('diameter')) {
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'this duct exposes no settable diameter' } }
    }
    & $fakeApply $tool $arguments $key
}
$ctxRect = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't2'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApplyRect }
$rectCases = @(& $module.Run $ctxRect)
$rectBy = @{}; foreach ($c in $rectCases) { $rectBy[$c.Name] = $c }
Check 'a rectangular flex duct retries with width/height and passes' (
    ($rectBy['flex_duct: create a path and re-read its points, size and level'].Outcome -eq 'pass') -and
    ($script:sent.ContainsKey('t2-ekf-flex-duct-rect')) -and
    ($script:sent['t2-ekf-flex-duct-rect'].elements[0].width -eq 200)
)

if ($fails) { "element-kinds-flex tests: $fails FAILED"; exit 1 } else { 'element-kinds-flex tests: ALL PASS'; exit 0 }
