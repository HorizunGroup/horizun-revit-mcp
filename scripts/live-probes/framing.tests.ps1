#Requires -Version 5.1
# Exercises framing.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply whose replies
# copy the shapes FramingApply.cs / FramingRead.cs / FramingCeiling.cs build
# (plan.sources[].count_by_role, z_mm, top_face_mm, members[].to, no_support_above;
# evidence.sources[] with hanger_supports keyed 'host:<id>'; postconditions.properties[]
# with property/matches; already_applied, evidence.removed_ids, member_count). Those
# shapes were written from the code and then held against Revit 2026 on 2026-09-26: the
# live run passed 10/10 reading exactly these fields (endpoint_read also carries
# location_curve_plus_level_offset for line-based members, MEASURED the same day).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'framing.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'framing' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }

function New-State {
    $script:readExtra = 0
    $script:nextId = 7000; $script:applies = 0; $script:removed = $false; $script:deleted = $null; $script:sent = @{}
    $script:floorId = $null; $script:ceilings = @(); $script:removeTargets = @()
}
$script:noFloorType = $false
$script:structuralTypes = $false; $script:structReads = @('column_constraints', 'location_curve')

# Ceiling A: underside at level + 2400 on a 50 mm compound type, drop 22, main depth 38;
# the floor's top at level + 3000 on a 300 mm type, so every rod ends at 100700.
function Rod($i) { [pscustomobject]@{ i = 20 + $i; role = 'hanger'; type_id = 6001; from = @(860600, (3600 + 1200 * $i), 100510.0); to = @(860600, (3600 + 1200 * $i), 100700.0) } }
function ZRow { [pscustomobject]@{ main_axis = 100491.0; cross_axis = 100461.0; perimeter_axis = 100461.0; hanger_from = 100510.0 } }
$script:ceilingPlanA = {
    [pscustomobject]@{ source_id = $script:ceilings[0]; status = 'planned'; top_face_mm = 100450.0; loops = 1; z_mm = (ZRow)
        count_by_role = [pscustomobject]@{ cross = 12; hanger = 3; main = 4; perimeter = 4 }; member_count = 23; no_support_above = @()
        plan_signature = 'sig'; spec_hash = 'hash'; warnings = @(); members_frame = 'model x, y, z in mm'
        members = @([pscustomobject]@{ i = 0; role = 'main'; type_id = 6001; from = @(860600, 3000, 100491.0); to = @(860600, 6600, 100491.0) }) + @(0..2 | ForEach-Object { Rod $_ }) }
}
$script:ceilingPlanB = {
    [pscustomobject]@{ source_id = $script:ceilings[1]; status = 'planned'; top_face_mm = 100450.0; loops = 1; z_mm = (ZRow)
        count_by_role = [pscustomobject]@{ cross = 6; main = 2; perimeter = 4 }; member_count = 12
        no_support_above = @([pscustomobject]@{ main = 0; point_mm = @(880600, 3600, 100510.0) }, [pscustomobject]@{ main = 1; point_mm = @(881800, 3600, 100510.0) })
        plan_signature = 'sig'; spec_hash = 'hash'; members_frame = 'model x, y, z in mm'; members = @()
        warnings = @("ceiling $($script:ceilings[1]): 2 hanger station(s) have no floor, framing or roof above within 3000 mm and are not placed (no_support_above)") }
}
function Supports($pairs) { $o = New-Object psobject; foreach ($k in $pairs.Keys) { $o | Add-Member -NotePropertyName $k -NotePropertyValue $pairs[$k] }; $o }
$script:ceilingApply = {
    param($arguments)
    Reply ([pscustomobject]@{ dry_run = $false; operation = 'ceiling'; transaction_status = 'Committed'; already_applied = $false
        postconditions = [pscustomobject]@{ all_verified = $true; properties = @(
            [pscustomobject]@{ property = 'member_count'; measured = $true; requested = 23; found_in_committed_model = 23; matches = $true },
            [pscustomobject]@{ property = 'inside_boundary'; measured = $true; requested = 0; found_in_committed_model = 0.0; matches = $true; unit = 'mm'; tolerance = 1.0 }) }
        evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ source_id = $script:ceilings[0]; already_applied = $false; planned = 23; found = 23
            max_endpoint_deviation_mm = 0.0; max_outside_boundary_mm = 0.0; hanger_supports = (Supports @{ "host:$($script:floorId)" = 3 }); no_support_above = @()
            member_ids = @(1..23 | ForEach-Object { 9000 + $_ }); work_plane_ids = @(1..23 | ForEach-Object { 9100 + $_ }) }) } }) $false ''
}

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_Walls' { @(@{ element_id = 401; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm' }) }
            'OST_Doors' { @(@{ element_id = 402; is_element_type = $true; family = 'M_Single-Flush'; type = '0915 x 2134mm' }) }
            'OST_Windows' { @(@{ element_id = 403; is_element_type = $true; family = 'M_Fixed'; type = '0915 x 1220mm' }) }
            'OST_Floors' { if ($script:noFloorType) { @() } else { @(@{ element_id = 404; is_element_type = $true; family = 'Floor'; type = 'Generic 300mm' }) } }
            # The fixture's own framing types, by name: the line-based member 6001, the stud column
            # 6002 and the HZ_FRB beam 6003; with structural types the HSS beam 408 comes first.
            'OST_GenericModel' { @(@{ element_id = 6001; is_element_type = $true; family = 'HZ_STUD'; type = 'HZ_STUD_92' }) }
            'OST_StructuralFraming' { if ($script:structuralTypes) { @(@{ element_id = 408; is_element_type = $true; family = 'M_HSS-Hollow Structural Section'; type = 'HSS152X152X6.4' }) } else { @(@{ element_id = 6003; is_element_type = $true; family = 'HZ_FRB'; type = 'HZ_FRB' }) } }
            'OST_StructuralColumns' { @(@{ element_id = 6002; is_element_type = $true; family = 'HZ_STUDCOL'; type = 'HZ_STUDCOL_92' }) + $(if ($script:structuralTypes) { @(@{ element_id = 409; is_element_type = $true; family = 'M_Concrete-Rectangular-Column'; type = '300 x 450mm' }) } else { @() }) }
            'OST_Ceilings' { @(@{ element_id = 405; is_element_type = $true; family = 'Basic Ceiling'; type = 'Generic' }, @{ element_id = 406; is_element_type = $true; family = 'Compound Ceiling'; type = '600 x 600mm Grid' }) }
            default { @() }
        }
        return Reply ([pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) }) $false ''
    }
    if ($tool -eq 'horizun_framing') {
        switch ($arguments.operation) {
            'wall' {
                return Reply ([pscustomobject]@{ dry_run = $true; operation = 'wall'; transaction_status = 'not_started'
                    plan = [pscustomobject]@{ member_count = 30; sources = @([pscustomobject]@{ source_id = 7003; status = 'planned'; member_count = 30
                        count_by_role = [pscustomobject]@{ cripple = 3; header = 2; jack = 4; king = 4; sill = 1; stud = 14; track = 2 }
                        openings = @([pscustomobject]@{ id = '7004'; read_from = 'rough' }, [pscustomobject]@{ id = '7005'; read_from = 'nominal' }) }) } }) $false ''
            }
            'read' {
                $n = 30 + $script:readExtra; $planes = 30; if ($script:removed) { $n = 0; $planes = 0 }
                return Reply ([pscustomobject]@{ operation = 'read'; member_count = $n; work_plane_count = $planes; sources = @() }) $false ''
            }
            'ceiling' {
                $plan = if ([long]@($arguments.element_ids)[0] -eq $script:ceilings[0]) { & $script:ceilingPlanA } else { & $script:ceilingPlanB }
                return Reply ([pscustomobject]@{ dry_run = $true; operation = 'ceiling'; transaction_status = 'not_started'
                    plan = [pscustomobject]@{ sources = @($plan); member_count = $plan.member_count; members_listed = @($plan.members).Count; truncated = $false } }) $false ''
            }
        }
    }
    return Reply $null $true "unexpected call $tool"
}

$script:wallApply = {
    param($arguments)
    $script:applies++
    $state = 'verified_applied'; if ($script:applies -gt 1) { $state = 'no_op' }
    Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = ($script:applies -gt 1)
        application = [pscustomobject]@{ state = $state; fully_applied = $true }
        postconditions = [pscustomobject]@{ all_verified = $true }
        evidence = [pscustomobject]@{ endpoint_read = @('location_curve'); source_joins_undone = 0; sources = @([pscustomobject]@{ source_id = 7003; already_applied = ($script:applies -gt 1)
            planned = 30; found = 30; max_endpoint_deviation_mm = 0.0; stud_crossings = 0; inserts_checked = 2; inserts_changed = 0; joined_to_source = 0 }) } }) $false ''
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            $kind = @($arguments.elements)[0].kind
            if ($kind -eq 'floor') { $script:floorId = $script:nextId }
            if ($kind -eq 'ceiling') { $script:ceilings += $script:nextId }
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); postconditions = [pscustomobject]@{ all_verified = $true } }) $false '') }
        }
        'horizun_framing' {
            if ($arguments.operation -eq 'remove') {
                $script:removed = $true; $script:removeTargets += [long]@($arguments.element_ids)[0]
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ operation = 'remove'; postconditions = [pscustomobject]@{ all_verified = $true }
                    application = [pscustomobject]@{ state = 'verified_applied'; fully_applied = $true }
                    evidence = [pscustomobject]@{ removed_ids = @(1..30 | ForEach-Object { 8000 + $_ }); cascaded_ids = @(); cascade_measured_in_rehearsal = @(); foreign_copies_kept = 0 } }) $false '') }
            }
            if ($arguments.operation -eq 'ceiling') { return @{ stage = 'apply'; answer = (& $script:ceilingApply $arguments) } }
            if ($key -like '*-fr-struct') {
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = $false
                    application = [pscustomobject]@{ state = 'verified_applied'; fully_applied = $true }; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ endpoint_read = $script:structReads; source_joins_undone = 3; sources = @([pscustomobject]@{ source_id = [long]@($arguments.element_ids)[0]
                        planned = 8; found = 8; max_endpoint_deviation_mm = 0.2; stud_crossings = 0; inserts_checked = 0; inserts_changed = 0; joined_to_source = 0 }) } }) $false '') }
            }
            return @{ stage = 'apply'; answer = (& $script:wallApply $arguments) }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') } }
        default { return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") } }
    }
}

function RunBy($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; $by }

try {
    New-State
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
    $cases = @(& $module.Run $ctx)
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    $n = $module.Catalog | ForEach-Object { $_.Name }
    Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($n | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    Check 'the rehearsal with two openings and every role passes' ($by[$n[0]].Outcome -eq 'pass')
    Check 'the verified apply passes' ($by[$n[1]].Outcome -eq 'pass')
    Check 'the second apply is already_applied' ($by[$n[2]].Outcome -eq 'pass')
    Check 'read and remove pass' (($by[$n[3]].Outcome -eq 'pass') -and ($by[$n[4]].Outcome -eq 'pass'))
    $applySent = $script:sent['t1-fr-apply']   # not $sent: that IS $script:sent here
    Check 'the apply names the wall and the member types found by name everywhere' (($applySent.operation -eq 'wall') -and ($applySent.element_ids[0] -eq 7002) -and ($applySent.spec.wall.stud.type_id -eq 6002) -and ($applySent.spec.wall.track.bottom_type_id -eq 6001))
    $hosted = @($script:sent.Values | Where-Object { $_.elements -and @($_.elements)[0].kind -eq 'family_instance' -and @($_.elements)[0].host_id -eq 7002 }).Count
    Check 'the door and the window are hosted on the staged wall' ($hosted -eq 2)

    Check 'the ceiling rehearsal passes with every role and every rod ending under the floor' ($by[$n[5]].Outcome -eq 'pass')
    Check 'the ceiling apply passes with the hangers on the staged floor' (($by[$n[6]].Outcome -eq 'pass') -and ($by[$n[6]].Detail -match '3 hangers on floor 7005'))
    Check 'the ceiling with nothing above passes on no_support_above' (($by[$n[7]].Outcome -eq 'pass') -and ($by[$n[7]].Detail -match '^2 station'))
    $ceilSent = $script:sent['t1-fr-ceiling']
    Check 'the ceiling apply names ceiling A, the line-based member for mains/cross/perimeter and the stud column for hangers' (($ceilSent.operation -eq 'ceiling') -and ($ceilSent.element_ids[0] -eq 7006) -and
        (@('main', 'cross', 'perimeter' | Where-Object { $ceilSent.spec.ceiling.$_.type_id -ne 6001 }).Count -eq 0) -and ($ceilSent.spec.ceiling.hanger.type_id -eq 6002))
    function Box($item) {
        $pts = @(@($item.profile)[0]); $xs = @($pts | ForEach-Object { $_[0] }); $ys = @($pts | ForEach-Object { $_[1] })
        @{ x0 = ($xs | Measure-Object -Minimum).Minimum; x1 = ($xs | Measure-Object -Maximum).Maximum; y0 = ($ys | Measure-Object -Minimum).Minimum; y1 = ($ys | Measure-Object -Maximum).Maximum; z = $pts[0][2] }
    }
    $fl = Box $script:sent['t1-fr-floor'].elements[0]; $ca = Box $script:sent['t1-fr-ceiling-a'].elements[0]; $cb = Box $script:sent['t1-fr-ceiling-b'].elements[0]
    Check 'the floor covers ceiling A from above and misses ceiling B' (($fl.z -gt $ca.z) -and ($fl.x0 -le $ca.x0) -and ($fl.x1 -ge $ca.x1) -and ($fl.y0 -le $ca.y0) -and ($fl.y1 -ge $ca.y1) -and ($cb.x0 -gt $fl.x1) -and ($ca.z -eq $cb.z))
    Check 'the compound ceiling type is preferred' (($script:sent['t1-fr-ceiling-a'].elements[0].type_id -eq 406) -and ($script:sent['t1-fr-ceiling-b'].elements[0].type_id -eq 406))
    Check 'cleanup removes the ceiling framing first, then the eight staged elements (the 45-degree wall included)' (($by[$n[8]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 8) -and
        (@($script:removeTargets) -contains 7006) -and ($by[$n[8]].Detail -match '^ceiling framing removed'))
    Check 'without HSS types the structural case uses the HZ_STUDCOL_92 column and the HZ_FRB beam and passes' (($by[$n[9]].Outcome -eq 'pass') -and ($by[$n[9]].Detail -match '6002/6003'))

    # ---- structural types present: column studs and beam tracks on the 45-degree wall ----
    New-State; $script:structuralTypes = $true
    $structBy = RunBy ([pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1s'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply })
    $diagSent = $script:sent['t1s-fr-diag-wall'].elements[0]; $structSent = $script:sent['t1s-fr-struct']
    Check 'the structural case passes on its re-reads' (($structBy[$n[9]].Outcome -eq 'pass') -and ($structBy[$n[9]].Detail -match 'location_curve'))
    Check 'the structural wall runs at 45 degrees and is framed with the column and beam types' (([math]::Abs(($diagSent.end[0] - $diagSent.start[0]) - ($diagSent.end[1] - $diagSent.start[1])) -lt 0.1) -and
        ($structSent.spec.wall.stud.type_id -eq 6002) -and ($structSent.spec.wall.track.bottom_type_id -eq 408))
    Check 'the structural framing is removed and its wall deleted at cleanup' ((@($script:removeTargets) -contains [long]$structSent.element_ids[0]) -and ($script:deleted.Count -eq 8) -and ($structBy[$n[8]].Outcome -eq 'pass'))
    New-State; $script:structReads = @('column_constraints')
    $noColBy = RunBy ([pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1t'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply })
    Check 'a structural apply whose tracks were not read from a location curve fails' (($noColBy[$n[9]].Outcome -eq 'fail') -and ($noColBy[$n[9]].Detail -match 'location curve'))
    $script:structuralTypes = $false; $script:structReads = @('column_constraints', 'location_curve')

    # ---- a stud through an opening is a fail, not a pass ----
    New-State
    $script:wallApply = {
        param($arguments)
        $script:applies++
        Reply ([pscustomobject]@{ already_applied = $false; application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 30; stud_crossings = 1; inserts_checked = 2; inserts_changed = 0 }) } }) $false ''
    }
    $badBy = RunBy $ctx
    Check 'a stud crossing an opening fails the apply case with the count named' (($badBy[$n[1]].Outcome -eq 'fail') -and ($badBy[$n[1]].Detail -match 'crossings 1'))
    Check 'nothing downstream claims a pass after a failed apply' (($badBy[$n[2]].Outcome -eq 'not_covered') -and ($badBy[$n[4]].Outcome -eq 'not_covered'))

    # ---- a second apply that builds again is a fail ----
    New-State
    $script:wallApply = {
        param($arguments)
        $script:applies++
        Reply ([pscustomobject]@{ already_applied = $false; application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 30; stud_crossings = 0; inserts_checked = 2; inserts_changed = 0 }) } }) $false ''
    }
    $dblBy = RunBy $ctx
    Check 'a second apply that is not already_applied fails' ($dblBy[$n[2]].Outcome -eq 'fail')

    # ---- an idempotent apply declared 'uncertain' is a fail ----
    New-State
    $script:wallApply = {
        param($arguments)
        $script:applies++
        Reply ([pscustomobject]@{ already_applied = ($script:applies -gt 1); application = [pscustomobject]@{ state = $(if ($script:applies -gt 1) { 'uncertain' } else { 'verified_applied' }) }
            postconditions = [pscustomobject]@{ all_verified = $true }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 30; stud_crossings = 0; inserts_checked = 2; inserts_changed = 0 }) } }) $false ''
    }
    $unsureBy = RunBy $ctx
    Check 'an idempotent apply declared uncertain fails' (($unsureBy[$n[1]].Outcome -eq 'pass') -and ($unsureBy[$n[2]].Outcome -eq 'fail'))

    # ---- read counting more members than the plan (a doubled apply) is a fail ----
    New-State
    $script:readExtra = 30
    $surplusBy = RunBy $ctx
    Check 'read finding more members than planned fails' ($surplusBy[$n[3]].Outcome -eq 'fail')

    # ---- hangers carried by something that is not the staged floor are a fail ----
    New-State
    $script:ceilingApply = {
        param($arguments)
        Reply ([pscustomobject]@{ transaction_status = 'Committed'; postconditions = [pscustomobject]@{ all_verified = $true; properties = @([pscustomobject]@{ property = 'inside_boundary'; matches = $true }) }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 23; hanger_supports = (Supports @{ "host:$($script:floorId)" = 2; 'linked:55/66' = 1 }); no_support_above = @() }) } }) $false ''
    }
    $alienBy = RunBy $ctx
    Check 'a hanger carried by another element fails the ceiling apply, named' (($alienBy[$n[6]].Outcome -eq 'fail') -and ($alienBy[$n[6]].Detail -match 'linked:55/66'))
    Check 'a committed ceiling apply is still removed at cleanup' ((@($script:removeTargets) -contains 7006) -and ($alienBy[$n[8]].Outcome -eq 'pass'))

    # ---- a member outside the boundary fails even when the counts agree ----
    New-State
    $script:ceilingApply = {
        param($arguments)
        Reply ([pscustomobject]@{ transaction_status = 'Committed'; postconditions = [pscustomobject]@{ all_verified = $true; properties = @([pscustomobject]@{ property = 'member_count'; matches = $true }) }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 23; hanger_supports = (Supports @{ "host:$($script:floorId)" = 3 }); no_support_above = @() }) } }) $false ''
    }
    $noInsideBy = RunBy $ctx
    Check 'an apply whose checklist lacks inside_boundary fails' (($noInsideBy[$n[6]].Outcome -eq 'fail') -and ($noInsideBy[$n[6]].Detail -match 'inside_boundary'))

    # ---- a ceiling with nothing above that still plans hangers is a fail ----
    New-State
    $script:ceilingPlanB = { & $script:ceilingPlanA }
    $rooflessBy = RunBy $ctx
    Check 'planned hangers without no_support_above fail the no-support case' (($rooflessBy[$n[7]].Outcome -eq 'fail') -and ($rooflessBy[$n[7]].Detail -match 'hangers planned 3'))

    # ---- no floor type: the floor-dependent ceiling cases are not_covered, nothing removed ----
    New-State; $script:noFloorType = $true
    $bareBy = RunBy $ctx
    Check 'without a floor type the two floor cases are not_covered with the reason' (($bareBy[$n[5]].Outcome -eq 'not_covered') -and ($bareBy[$n[5]].Detail -match 'floor type found: False') -and ($bareBy[$n[6]].Outcome -eq 'not_covered'))
    Check 'without a floor type no ceiling remove is sent and cleanup still passes' ((-not (@($script:removeTargets) -contains 7005)) -and ($bareBy[$n[8]].Outcome -eq 'pass'))
    $script:noFloorType = $false

    $closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
    $shut = @(& $module.Run $closed)
    Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
    Check 'a closed write tier names the delete tool on the cleanup case and only there' ((@($shut | Where-Object { $_.Name -like 'framing probes:*' -and $_.Tool -eq 'horizun_delete_verified' }).Count -eq 1) -and
        (@($shut | Where-Object { $_.Name -notlike 'framing probes:*' -and $_.Tool -ne 'horizun_framing' }).Count -eq 0))
}
finally { }

if ($fails) { "framing tests: $fails FAILED"; exit 1 } else { 'framing tests: ALL PASS'; exit 0 }
