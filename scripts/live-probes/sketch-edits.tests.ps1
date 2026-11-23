#Requires -Version 5.1
# Exercises sketch-edits.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply that
# simulate the sketch (a 2-D loop per element, areas by the shoelace formula) and answer in
# the shapes TransformElementsCommand.EditSketch.cs builds. SHAPES FROM THE CODE, TO BE HELD
# AGAINST THE FIRST LIVE RUN: the rehearsal's plan[] (element_id, mode, loop_index,
# vertex_index, method, sketch_plane, loops_before (model xyz), loops_before_mm, loops_expected_mm, sketch_area_before_m2, expected_area_m2,
# area_parameter_before_m2, area_check, rehearsal_ok, rehearsal_error) under
# transaction_status 'rehearsed_and_cancelled' with a confirmation_token; the apply's rows[]
# (element_id, verified, unique_id_kept, loops_verified, loops_problem, loops_after_mm,
# area_parameter_before_m2, area_parameter_after_m2, area_check) under transaction_status
# 'Committed' with application.state; and the wording of two refusals (off-plane names the
# plane as "(x, y, z) mm" in invariant numbers; FootPrintRoof names SketchId). The
# type names below are the probe's guesses, equally unmeasured.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'sketch-edits.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'sketch-edits' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }

$LevelZ = 117000.0

# Types the fake document carries, as 'Family: Type'.
function Seed($names) {
    foreach ($nm in $names) {
        $fam, $typ = $nm -split ': ', 2
        $cat = if ($fam -eq 'Floor') { 'OST_Floors' } elseif ($fam -like '*Ceiling') { 'OST_Ceilings' } else { 'OST_Roofs' }
        $script:typeId++
        $script:docTypes[$cat] = @(@($script:docTypes[$cat]) + @([pscustomobject]@{ element_id = $script:typeId; is_element_type = $true; family = $fam; type = $typ }) | Where-Object { $_ })
    }
}

function New-State {
    $script:nextId = 5000; $script:sent = @{}; $script:deleted = $null; $script:dryCalls = 0; $script:createdIds = New-Object System.Collections.ArrayList
    $script:docTypes = @{}; $script:elements = @{}; $script:typeId = 900
    # Scenario switches, all off by default.
    Seed @('Floor: Generic 150mm', 'Compound Ceiling: 600 x 600mm Grid', 'Basic Roof: Generic - 400mm')
    $script:ceilingPlaneLift = 0.0; $script:rehearsalCommits = $false; $script:areaOff = 0.0
    $script:lostAfterCommit = $false; $script:roofEditable = $false
}
function Area2($pts) { $s = 0.0; for ($i = 0; $i -lt $pts.Count; $i++) { $p = $pts[$i]; $q = $pts[($i + 1) % $pts.Count]; $s += [double]$p[0] * [double]$q[1] - [double]$q[0] * [double]$p[1] }; [math]::Round([math]::Abs($s) / 2e6, 4) }
function Flat($loop) { @($loop | ForEach-Object { , @([double]$_[0], [double]$_[1]) }) }

# The code's PlanEditSketch, in miniature: refusals first, then the resolved edit.
function PlanEdit($op) {
    $id = [long]@($op.element_ids)[0]
    $el = $script:elements[$id]
    if (-not $el) { return @{ error = "edit_sketch: element $id does not exist. Nothing was changed." } }
    if ($el.kind -eq 'roof' -and -not $script:roofEditable) {
        return @{ error = 'edit_sketch: a FootPrintRoof exposes no SketchId in the Revit API (2023-2027), so its footprint cannot be edited through a SketchEditScope; edit it in Revit. Nothing was changed.' } }
    $pts = if ($op.loop) { @($op.loop) } else { @($op.start, $op.end) }
    foreach ($p in $pts) {
        $off = [math]::Abs([double]$p[2] - $el.planeZ)
        if ($off -gt 1) {
            return @{ error = ("edit_sketch: loop: a point lies {0} mm off the sketch plane. Points are refused rather than projected, so a wrong elevation is never silently flattened; the plane passes through ({1}, {2}, {3}) mm. Nothing was changed." -f
                    $off.ToString([Globalization.CultureInfo]::InvariantCulture), '0', '0', $el.planeZ.ToString([Globalization.CultureInfo]::InvariantCulture)) } }
    }
    if ($op.loop) { return @{ id = $id; el = $el; new = (Flat $op.loop); mode = 'replace_loop'; vi = $null } }
    $vi = -1
    for ($i = 0; $i -lt $el.loop.Count; $i++) { if ([math]::Abs($el.loop[$i][0] - $op.start[0]) -lt 0.5 -and [math]::Abs($el.loop[$i][1] - $op.start[1]) -lt 0.5) { $vi = $i } }
    if ($vi -lt 0) { return @{ error = 'edit_sketch: no vertex of the sketch lies within 0.5 mm of start. Nothing was changed.' } }
    $new = @(for ($i = 0; $i -lt $el.loop.Count; $i++) { if ($i -eq $vi) { , @([double]$op.end[0], [double]$op.end[1]) } else { , $el.loop[$i] } })
    return @{ id = $id; el = $el; new = $new; mode = 'move_vertex'; vi = $vi }
}
function DryReply($pl) {
    $before = Area2 $pl.el.loop
    Reply ([pscustomobject]@{ dry_run = $true; transaction_status = 'rehearsed_and_cancelled'; targets = 1; confirmation_token = ('tok-' + $pl.id)
        plan = @([pscustomobject]@{ element_id = $pl.id; mode = $pl.mode; loop_index = 0; vertex_index = $pl.vi
            loops_before_mm = @(, @($pl.el.loop)); loops_expected_mm = @(, @($pl.new))
            # The fake's plane origin is (0, 0, z) with world axes, so its (u, v) are the model's x, y.
            sketch_plane = [pscustomobject]@{ origin = @(0.0, 0.0, $pl.el.planeZ); x_dir = @(1.0, 0.0, 0.0); y_dir = @(0.0, 1.0, 0.0); normal = @(0.0, 0.0, 1.0) }
            loops_before = @(, @($pl.el.loop | ForEach-Object { , @([double]$_[0], [double]$_[1], $pl.el.planeZ) }))
            method = $(if ($pl.mode -eq 'move_vertex' -or @($pl.new).Count -eq @($pl.el.loop).Count) { 'reshape_in_place' } else { 'delete_and_redraw' })
            coordinates = 'loops_before/loops_expected: model [x,y,z] in the request''s units; *_mm: (u, v) in mm from sketch_plane.origin'
            sketch_area_before_m2 = $before; expected_area_m2 = (Area2 $pl.new); area_parameter_before_m2 = $before; area_check = 'will_verify'
            rehearsal_ok = $true; rehearsal_error = $null; rehearsal_detail = [pscustomobject]@{ curves_deleted = 4; curves_created = @($pl.new).Count } })
        note = 'The edit was rehearsed inside the element''s SketchEditScope and Cancelled.' }) $false ''
}
function ApplyReply($pl) {
    $before = Area2 $pl.el.loop
    $pl.el.loop = $pl.new
    if ($script:lostAfterCommit) { $script:elements.Remove($pl.id) }
    $after = (Area2 $pl.new) + $script:areaOff
    Reply ([pscustomobject]@{ dry_run = $false; transaction_status = 'Committed'; operations_verified = 1; targets = 1
        rows = @([pscustomobject]@{ element_id = $pl.id; verified = $true; unique_id_kept = $true; loops_verified = $true; loops_problem = $null
            loops_after_mm = @(, @($pl.new)); sketch_area_after_m2 = $after; expected_area_m2 = $after
            area_parameter_before_m2 = $before; area_parameter_after_m2 = $after; area_check = 'verified'; area_note = $null })
        application = [pscustomobject]@{ state = 'verified_applied'; fully_applied = $true }
        undo = [pscustomobject]@{ recorded = $false } }) $false ''
}

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $category = @($arguments.categories)[0]
        if ($arguments.include_types) { return Reply ([pscustomobject]@{ rows = @($script:docTypes[$category]) }) $false '' }
        $rows = @(@($arguments.element_ids) | Where-Object { $script:elements[[long]$_] -and $script:elements[[long]$_].category -eq $category } |
                  ForEach-Object { [pscustomobject]@{ element_id = [long]$_; category = $category } })
        return Reply ([pscustomobject]@{ rows = $rows }) $false ''
    }
    if ($tool -eq 'horizun_transform_elements') {
        $script:dryCalls++
        $pl = PlanEdit @($arguments.operations)[0]
        if ($pl.error) { return Reply $null $true $pl.error }
        $r = DryReply $pl
        if ($script:rehearsalCommits) { $pl.el.loop = $pl.new }
        return $r
    }
    return Reply $null $true "unexpected call $tool"
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++; [void]$script:createdIds.Add([long]$script:nextId)
            $item = @($arguments.elements)[0]
            $cat = @{ floor = 'OST_Floors'; ceiling = 'OST_Ceilings'; roof = 'OST_Roofs'; level = 'OST_Levels' }[$item.kind]
            if ($item.profile) {
                $loop = @(@($item.profile)[0])
                $lift = if ($item.kind -eq 'ceiling') { $script:ceilingPlaneLift } else { 0.0 }
                $script:elements[[long]$script:nextId] = @{ kind = $item.kind; category = $cat; loop = (Flat $loop); planeZ = ([double]$loop[0][2] + $lift) }
            }
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); postconditions = [pscustomobject]@{ all_verified = $true } }) $false '') }
        }
        'horizun_transform_elements' {
            $pl = PlanEdit @($arguments.operations)[0]
            if ($pl.error) { return @{ stage = 'dry_run'; answer = (Reply $null $true $pl.error) } }
            $d = DryReply $pl
            return @{ stage = 'apply'; answer = (ApplyReply $pl); dry = $d }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') } }
        default { return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") } }
    }
}

function Ctx($runId) { [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply } }
function RunBy($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; $by }
$n = @($module.Catalog | ForEach-Object { $_.Name })

try {
    New-State
    $cases = @(& $module.Run (Ctx 't1'))
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($n | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    foreach ($i in 0..5) { Check "case $i passes on a faithful tool: $($n[$i].Substring(0, [math]::Min(60, $n[$i].Length)))" ($by[$n[$i]].Outcome -eq 'pass') }
    $rep = @($script:sent['t1-sk-floor-replace'].operations)[0]
    $xs = @($rep.loop | ForEach-Object { $_[0] })
    Check 'the replace sends ONE edit_sketch on the floor: a 5000 x 4000 loop at the level height, loop_index 0' ((@($script:sent['t1-sk-floor-replace'].operations).Count -eq 1) -and ($rep.operation -eq 'edit_sketch') -and
        ($rep.loop_index -eq 0) -and (@($rep.loop).Count -eq 4) -and (@($rep.loop | Where-Object { $_[2] -ne $LevelZ }).Count -eq 0) -and
        (($xs | Measure-Object -Maximum).Maximum - ($xs | Measure-Object -Minimum).Minimum -eq 5000))
    $mv = @($script:sent['t1-sk-floor-move'].operations)[0]
    Check 'the move sends start (X0, 4000) and end (X0 + 1000, 4000) on the plane, and no loop' ((-not $mv.loop) -and ($mv.start[0] -eq 1170000) -and ($mv.start[1] -eq 4000) -and
        ($mv.end[0] -eq 1171000) -and ($mv.end[1] -eq 4000) -and ($mv.start[2] -eq $LevelZ))
    Check 'the move is held to 20 - 2 = 18 m2' ($by[$n[2]].Detail -match '20 -> 18 m2')
    Check 'the ceiling receives the six-vertex L' (@(@($script:sent['t1-sk-ceiling-replace'].operations)[0].loop).Count -eq 6)
    Check 'the floor was created with the first named floor type' ($script:sent['t1-sk-floor'].elements[0].type_id -eq @($script:docTypes['OST_Floors'] | Where-Object { $_.type -eq 'Generic 150mm' })[0].element_id)
    Check 'the roof type falls back to the second name when the document lacks the first' ((
        $script:sent['t1-sk-roof'].elements[0].type_id -eq @($script:docTypes['OST_Roofs'])[0].element_id) -and (@($script:docTypes['OST_Roofs'])[0].type -eq 'Generic - 400mm'))
    Check 'cleanup deletes the four staged elements, and nothing else' ((@($script:deleted).Count -eq 4) -and
        ($script:createdIds.Count -eq 4) -and
        (@(@($script:createdIds) | Where-Object { @($script:deleted) -notcontains $_ }).Count -eq 0))
    Check 'the rehearsal case sends two dry runs on the floor, the ceiling one, the roof one' ($script:dryCalls -eq 4)

    # ---- the second floor name is used when the document lacks the first ----
    New-State
    $script:docTypes['OST_Floors'] = @([pscustomobject]@{ element_id = 77; is_element_type = $true; family = 'Floor'; type = 'Generic 300mm' })
    $null = RunBy (Ctx 't2')
    Check 'a floor type the document carries under the second name is used' ($script:sent['t2-sk-floor'].elements[0].type_id -eq 77)
    Check 'a type the document already carried is not deleted at cleanup' (@($script:deleted) -notcontains 77)

    # ---- the ceiling's plane lies elsewhere: re-sent there once, and said ----
    New-State; $script:ceilingPlaneLift = 2400.0
    $liftBy = RunBy (Ctx 't3')
    Check 'an off-plane refusal is followed once on the plane it names, and the case says where' (($liftBy[$n[3]].Outcome -eq 'pass') -and ($liftBy[$n[3]].Detail -match 'z 119400 mm') -and
        (@(@($script:sent['t3-sk-ceiling-replace'].operations)[0].loop | Where-Object { $_[2] -ne 119400 }).Count -eq 0))

    # ---- a rehearsal that was not cancelled is a fail ----
    New-State; $script:rehearsalCommits = $true
    $leakBy = RunBy (Ctx 't4')
    Check 'a rehearsal that changed the sketch fails case 0, named' (($leakBy[$n[0]].Outcome -eq 'fail') -and ($leakBy[$n[0]].Detail -match 'not cancelled'))

    # ---- the tool agrees with itself but not with what was asked ----
    New-State; $script:areaOff = 1.0
    $offBy = RunBy (Ctx 't5')
    Check 'an Area that is not the one the probe asked for fails, both numbers named' (($offBy[$n[1]].Outcome -eq 'fail') -and ($offBy[$n[1]].Detail -match 'Area after 21 m2, the probe asked for 20'))
    Check 'the ceiling is held to 9 m2 the same way' (($offBy[$n[3]].Outcome -eq 'fail') -and ($offBy[$n[3]].Detail -match 'asked for 9'))

    # ---- the id no longer resolves after the commit, whatever the row claims ----
    New-State; $script:lostAfterCommit = $true
    $lostBy = RunBy (Ctx 't6')
    Check 'an id the independent read cannot find fails the apply case' (($lostBy[$n[1]].Outcome -eq 'fail') -and ($lostBy[$n[1]].Detail -match 'horizun_query_model finds no OST_Floors'))

    # ---- a roof that is not refused is a fail ----
    New-State; $script:roofEditable = $true
    $roofBy = RunBy (Ctx 't7')
    Check 'an accepted footprint-roof rehearsal fails the refusal case' (($roofBy[$n[4]].Outcome -eq 'fail') -and ($roofBy[$n[4]].Detail -match 'was accepted'))

    # ---- no floor type by name anywhere: the floor cases are not_covered, named ----
    New-State; $script:docTypes = @{}; Seed @('Compound Ceiling: 600 x 600mm Grid', 'Basic Roof: Generic - 125mm')
    $bareBy = RunBy (Ctx 't8')
    Check 'without a named floor type the three floor cases are not_covered with the names tried' ((@(0, 1, 2 | Where-Object { $bareBy[$n[$_]].Outcome -ne 'not_covered' }).Count -eq 0) -and
        ($bareBy[$n[0]].Detail -match 'Floor: Generic 150mm, Floor: Generic 300mm'))
    Check 'without a floor no floor edit is sent and the other cases still run' ((-not $script:sent.ContainsKey('t8-sk-floor-replace')) -and ($bareBy[$n[3]].Outcome -eq 'pass') -and ($bareBy[$n[4]].Outcome -eq 'pass') -and ($bareBy[$n[5]].Outcome -eq 'pass'))

    # ---- closed write tier ----
    $closed = Ctx 't9'; $closed.WriteGate = $true
    $shut = @(& $module.Run $closed)
    Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
    Check 'a closed write tier names the delete tool on the cleanup case and only there' ((@($shut | Where-Object { $_.Name -like 'edit_sketch probes:*' -and $_.Tool -eq 'horizun_delete_verified' }).Count -eq 1) -and
        (@($shut | Where-Object { $_.Name -notlike 'edit_sketch probes:*' -and $_.Tool -ne 'horizun_transform_elements' }).Count -eq 0))
}
finally { }

if ($fails) { "sketch-edits tests: $fails FAILED"; exit 1 } else { 'sketch-edits tests: ALL PASS'; exit 0 }
