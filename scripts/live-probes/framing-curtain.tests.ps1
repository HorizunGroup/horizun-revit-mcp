#Requires -Version 5.1
# Exercises framing-curtain.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply whose
# replies copy the shapes the code builds - SHAPES FROM THE CODE, TO BE HELD AGAINST THE FIRST
# LIVE RUN: CurtainWallSummary / CurtainCeilingSummary (plan.sources[].method, pieces[].role,
# carrier.action / type_id / span, layers[].plane_mm, hangers[].base_mm / top_mm, not_built),
# VerifyCurtainWalls (evidence.sources[].pieces[].grid.layout_vert / layout_vert_text /
# spacing_mm / spacing_problems, carrier.type_ok / location_line / inserts_checked / inserts_changed / deleted /
# deleted_with_it[_measured]), VerifyRemoved + VerifyCurtainRestores (evidence.carrier_restores[].
# restored / wall_id / recreated_with_new_id / inserts_changed / not_restored_because),
# VerifyCurtainCeilings (pieces[].grid.grid1.layout / layout_text / spacing_mm / spacing_problems,
# evidence.hanger_recheck.stations_checked / not_at_support), ManageCurtainCommand.Read (counts,
# host_kind, grid1_angle_deg; where ModelEditRunner places that read result is exactly what the
# first live run must confirm - the probe looks in data, data.read and data.result) and
# horizun_write_params_verified (verification.verified).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'framing-curtain.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'framing-curtain' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }
# Every type comes from the fake query, and a missing one stays missing.

function New-State {
    $script:nextId = 7000; $script:wallApplies = 0; $script:sent = @{}; $script:deleted = $null; $script:removeTargets = @(); $script:reusedKeys = @()
    $script:noGlazing = $false; $script:w1Restore = @{ restored = $true; inserts_changed = 0; why = $null }; $script:notAtSupport = @()
    $script:layoutText = 'Fixed Distance'; $script:glazingRefused = $false
    $script:noOverlap = $false; $script:w3Restored = $true; $script:pagedWalls = $false
    $script:overlapNote = 'the curtain walls OVERLAP the kept carrier: a wall with several openings (multi_opening=keep_carrier) stays full length with the placeholder type so its inserts keep their ids, tags and data'
}

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_Walls' { @(@{ element_id = 501; is_element_type = $true; family = 'Curtain Wall'; type = 'Curtain Wall 1' },
                            @{ element_id = 502; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm' },
                            @{ element_id = 503; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 150mm' }) }
            'OST_Doors' { @(@{ element_id = 504; is_element_type = $true; family = 'M_Single-Flush'; type = '0915 x 2134mm' }) }
            'OST_Roofs' { if ($script:noGlazing) { @() } else { @(@{ element_id = 505; is_element_type = $true; family = 'Sloped Glazing'; type = 'Sloped Glazing' }) } }
            'OST_Floors' { @(@{ element_id = 506; is_element_type = $true; family = 'Floor'; type = 'Generic 300mm' }) }
            'OST_Ceilings' { @(@{ element_id = 507; is_element_type = $true; family = 'Compound Ceiling'; type = '600 x 600mm Grid' }) }
            default { @() }
        }
        # QueryModelCommand pages with truncated / next_cursor; types and instances share the rows.
        if ($script:pagedWalls -and $arguments.categories[0] -eq 'OST_Walls' -and -not $arguments.cursor) {
            $inst = @(1..3 | ForEach-Object { [pscustomobject]@{ element_id = 8000 + $_; is_element_type = $false; family = 'Basic Wall'; type = 'Generic - 200mm' } })
            return Reply ([pscustomobject]@{ rows = $inst; truncated = $true; next_cursor = 'page-2' }) $false ''
        }
        return Reply ([pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }); truncated = $false }) $false ''
    }
    if ($tool -eq 'horizun_framing' -and $arguments.operation -eq 'wall') {
        $sid = [long]@($arguments.element_ids)[0]; $tid = [long]$arguments.spec.wall.curtain_type_id
        if ($sid -eq 7007) {
            $src = [pscustomobject]@{ source_id = 7007; status = 'planned'; method = 'curtain'; length_mm = 6000.0; height_mm = 3000.0; core_offset_mm = 0.0
                openings = @([pscustomobject]@{ id = '7008'; start = 1042.5; end = 1957.5; sill = 0.0; head = 2134.0 }, [pscustomobject]@{ id = '7009'; start = 4042.5; end = 4957.5; sill = 0.0; head = 2134.0 })
                pieces = @(0..4 | ForEach-Object { [pscustomobject]@{ i = $_; role = $(if ($_ -lt 3) { 'curtain_segment' } else { 'curtain_header' }); type_id = $tid } })
                skipped = @(); warnings = @('2 openings: ' + $script:overlapNote)
                carrier = [pscustomobject]@{ action = 'keep'; original_type_id = 502; type_id = 503; span = @(0.0, 6000.0); opening_id = $null; replaced_by = $null; overlap = $script:overlapNote } }
            return Reply ([pscustomobject]@{ dry_run = $true; operation = 'wall'; plan = [pscustomobject]@{ method = 'curtain'; sources = @($src); member_count = 5 } }) $false ''
        }
        if ($sid -eq 7002) {
            $src = [pscustomobject]@{ source_id = 7002; status = 'planned'; method = 'curtain'; length_mm = 6000.0; height_mm = 3000.0; core_offset_mm = 0.0
                openings = @([pscustomobject]@{ id = '7004'; start = 2042.5; end = 2957.5; sill = 0.0; head = 2134.0 })
                pieces = @([pscustomobject]@{ i = 0; role = 'curtain_segment'; type_id = $tid }, [pscustomobject]@{ i = 1; role = 'curtain_segment'; type_id = $tid }, [pscustomobject]@{ i = 2; role = 'curtain_header'; type_id = $tid })
                skipped = @(); carrier = [pscustomobject]@{ action = 'trim'; original_type_id = 502; type_id = 503; span = @(2042.5, 2957.5); opening_id = '7004' } }
        }
        else {
            $src = [pscustomobject]@{ source_id = $sid; status = 'planned'; method = 'curtain'; openings = @(); pieces = @([pscustomobject]@{ i = 0; role = 'curtain_segment'; type_id = $tid })
                skipped = @(); carrier = [pscustomobject]@{ action = 'delete'; original_type_id = 502; type_id = $null; span = $null; replaced_by = 'every curtain_segment piece'
                    deleted_with_it = [pscustomobject]@{ count = 0; by_category = [pscustomobject]@{}; ids = @() } } }
        }
        return Reply ([pscustomobject]@{ dry_run = $true; operation = 'wall'; plan = [pscustomobject]@{ method = 'curtain'; sources = @($src); member_count = @($src.pieces).Count } }) $false ''
    }
    if ($tool -eq 'horizun_framing' -and $arguments.operation -eq 'ceiling') {
        if ($script:glazingRefused) { return Reply $null $true "layers[0].type_id 505: the Sloped Glazing type's grid 1 layout is None; the hangers follow grid 1. Nothing was planned." }
        $hangers = @(0..2 | ForEach-Object { [pscustomobject]@{ i = 2 + $_; type_id = 501; from = @(1190000, (600 + 1200 * $_)); to = @(1194800, (600 + 1200 * $_)); base_mm = 100480.0; top_mm = 100700.0 } })
        return Reply ([pscustomobject]@{ dry_run = $true; operation = 'ceiling'; plan = [pscustomobject]@{ method = 'curtain'; member_count = 5; sources = @([pscustomobject]@{
            source_id = 7006; status = 'planned'; method = 'curtain'; top_face_mm = 100450.0; sketch_loops = 1; openings_not_cut = @()
            layers = @([pscustomobject]@{ i = 0; type_id = 505; plane_mm = 100450.0; angle_deg = 0 }, [pscustomobject]@{ i = 1; type_id = 505; plane_mm = 100480.0; angle_deg = 90 })
            hanger_base_mm = 100480.0; hanger_direction_deg = 0.0; hangers = $hangers; not_built = @(); hanger_supports = [pscustomobject]@{ 'host:7005' = 3 } }) } }) $false ''
    }
    if ($tool -eq 'horizun_manage_curtain') {
        return Reply ([pscustomobject]@{ element_id = [long]$arguments.element_id; grid_index = 0; host_kind = 'FootPrintRoof'
            counts = [pscustomobject]@{ u_lines = 11; v_lines = 4; mullions = 40; panels = 60 }; grid1_angle_deg = 0.0; grid2_angle_deg = 90.0 }) $false ''
    }
    return Reply $null $true "unexpected call $tool"
}

$fakeApply = {
    param($tool, $arguments, $key)
    # A key reused for a different call is refused by the bridge (MEASURED 2026-09-27: the ceiling
    # apply reused the staging ceiling's key) - so every Apply key must be new.
    if ($script:sent.ContainsKey($key)) { $script:reusedKeys += $key }
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) }) $false '') }
        }
        'horizun_write_params_verified' {
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ transaction_status = 'Committed'; verification = [pscustomobject]@{ verified = $true } }) $false '') }
        }
        'horizun_framing' {
            $op = $arguments.operation; $sid = [long]@($arguments.element_ids)[0]
            if ($op -eq 'remove') {
                $script:removeTargets += @($arguments.element_ids)
                $restores = @()
                foreach ($id in @($arguments.element_ids)) {
                    if ([long]$id -eq 7002) {
                        $restores += [pscustomobject]@{ carrier_id = 7002; action_at_apply = 'trim'; wall_id = 7002; restored = $script:w1Restore.restored; line_deviation_mm = 0.0
                                                        inserts_checked = 1; inserts_changed = $script:w1Restore.inserts_changed; not_restored_because = $script:w1Restore.why }
                    }
                    elseif ([long]$id -eq 7007) {
                        $restores += [pscustomobject]@{ carrier_id = 7007; action_at_apply = 'keep'; wall_id = 7007; restored = $script:w3Restored; line_deviation_mm = 0.0; inserts_changed = 0
                            not_restored_because = $(if ($script:w3Restored) { $null } else { 'its type changed after the apply' }) }
                    }
                    elseif ([long]$id -eq 7003) {
                        $restores += [pscustomobject]@{ carrier_id = 7003; action_at_apply = 'delete'; wall_id = 9999; recreated_with_new_id = $true; restored = $true
                                                        line_deviation_mm = 0.0; base_deviation_mm = 0.0; top_deviation_mm = 0.0; not_in_record = 'mark, comments, phase, workset and other instance parameters' }
                    }
                }
                $data = [pscustomobject]@{ operation = 'remove'; transaction_status = 'Committed'; postconditions = [pscustomobject]@{ all_verified = $true }
                    application = [pscustomobject]@{ state = 'verified_applied' }
                    evidence = [pscustomobject]@{ removed_ids = @(8001, 8002, 8003); cascaded_ids = @(); cascade_measured_in_rehearsal = @(); foreign_copies_kept = 0; sources = @($arguments.element_ids) } }
                if ($restores.Count -gt 0) { $data.evidence | Add-Member -NotePropertyName carrier_restores -NotePropertyValue $restores }
                return @{ stage = 'apply'; answer = (Reply $data $false '') }
            }
            if ($op -eq 'ceiling') {
                # The REAL shape (MEASURED 2026-09-27): grid 1 lines run at the angle + 90; the cross
                # layer has grid 1 None and its members on grid 2, whose lines run at 0.
                $g1 = { param($deg) [pscustomobject]@{ lines = 11; layout = 1; layout_text = 'Fixed Distance'; spacing_mm = 406.4; spacing_problems = @(); direction_deg = $deg; planned_direction_deg = $deg } }
                $none = [pscustomobject]@{ lines = 0; layout = 0; layout_text = 'None'; spacing_check = 'no grid line to measure' }
                $pieces = @([pscustomobject]@{ i = 0; role = 'curtain_layer'; id = 9101; plane_deviation_mm = 0.0; footprint_deviation_mm = 0.2; slope_defining_edges = 0
                                grid = [pscustomobject]@{ grid1 = (& $g1 90.0); grid2 = $none } },
                            [pscustomobject]@{ i = 1; role = 'curtain_layer'; id = 9102; plane_deviation_mm = 0.0; footprint_deviation_mm = 0.2; slope_defining_edges = 0
                                grid = [pscustomobject]@{ grid1 = $none; grid2 = [pscustomobject]@{ lines = 8; layout = 1; layout_text = 'Fixed Distance'; spacing_mm = 406.4; spacing_problems = @(); direction_deg = 0.0 } } }) +
                          @(2..4 | ForEach-Object { [pscustomobject]@{ i = $_; role = 'curtain_hanger'; id = 9100 + $_; location_deviation_mm = 0.0; base_top_deviation_mm = 0.0; grid = [pscustomobject]@{ vertical_lines = 3; horizontal_lines = 0; layout_vert = 1; layout_vert_text = $script:layoutText; spacing_mm = 406.4; spacing_problems = @(); mullions_by_role = [pscustomobject]@{ vertical_interior = 3; horizontal_border = 2 } } } })
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'ceiling'; transaction_status = 'Committed'; already_applied = $false
                    application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ hanger_recheck = [pscustomobject]@{ stations_checked = 9; not_at_support = $script:notAtSupport; max_gap_mm = 0.1 }
                        sources = @([pscustomobject]@{ source_id = 7006; already_applied = $false; pieces = $pieces; not_built = @(); member_ids = @(9101..9105) }) } }) $false '') }
            }
            if ($sid -eq 7007) {
                $grid3 = [pscustomobject]@{ vertical_lines = 3; horizontal_lines = 0; layout_vert = 1; layout_vert_text = $script:layoutText; spacing_mm = 406.4; spacing_problems = @() }
                $pieces3 = @(0..4 | ForEach-Object { [pscustomobject]@{ i = $_; role = $(if ($_ -lt 3) { 'curtain_segment' } else { 'curtain_header' }); id = 9301 + $_; grid = $grid3 } })
                $carrier3 = [pscustomobject]@{ id = 7007; action = 'keep'; type_ok = $true; location_line = 0; curve_deviation_mm = 0.0; inserts_checked = 2; inserts_changed = 0
                    overlap = $(if ($script:noOverlap) { $null } else { $script:overlapNote }); overlapped_by = $(if ($script:noOverlap) { @() } else { @(9301..9305) }) }
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = $false
                    application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ source_id = 7007; already_applied = $false; pieces = $pieces3; carrier = $carrier3; piece_ids = @(9301..9305) }) } }) $false '') }
            }
            if ($sid -eq 7002) {
                $script:wallApplies++
                $again = $script:wallApplies -gt 1
                $grid = [pscustomobject]@{ vertical_lines = 5; horizontal_lines = 0; layout_vert = 1; layout_vert_text = $script:layoutText; spacing_mm = 406.4; spacing_problems = @() }
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = $again
                    application = [pscustomobject]@{ state = $(if ($again) { 'no_op' } else { 'verified_applied' }) }; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ source_id = 7002; already_applied = $again
                        pieces = @(0..2 | ForEach-Object { [pscustomobject]@{ i = $_; role = $(if ($_ -eq 2) { 'curtain_header' } else { 'curtain_segment' }); id = 9001 + $_; location_deviation_mm = 0.0; grid = $grid } })
                        carrier = [pscustomobject]@{ id = 7002; action = 'trim'; type_ok = $true; location_line = 0; curve_deviation_mm = 0.0; inserts_checked = 1; inserts_changed = 0 }; piece_ids = @(9001, 9002, 9003) }) } }) $false '') }
            }
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = $false
                application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
                evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ source_id = $sid; already_applied = $false
                    pieces = @([pscustomobject]@{ i = 0; role = 'curtain_segment'; id = 9011; grid = [pscustomobject]@{ vertical_lines = 7; layout_vert = 1; layout_vert_text = 'Fixed Distance'; spacing_mm = 406.4; spacing_problems = @() } })
                    carrier = [pscustomobject]@{ id = $sid; action = 'delete'; deleted = $true; deleted_with_it = @(); deleted_with_it_measured = @(); inserts_checked = 0; inserts_changed = 0 } }) } }) $false '') }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') } }
        default { return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") } }
    }
}

function Ctx($runId) { [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply } }
function RunBy($context) { $by = @{}; foreach ($c in @(& $module.Run $context)) { $by[$c.Name] = $c }; $by }
$names = @($module.Catalog | ForEach-Object { $_.Name })

try {
    New-State
    $cases = @(& $module.Run (Ctx 't1'))
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    Check 'no idempotency key is used twice (the bridge refuses a reused key for a different call)' (@($script:reusedKeys).Count -eq 0)
    foreach ($name in $names) { Check ('the happy path passes: ' + $name) ($by[$name].Outcome -eq 'pass') }
    $applySent = $script:sent['t1-frc-apply']
    Check 'the wall apply names the one-door wall, method curtain, the document''s curtain type and the placeholder type' (($applySent.operation -eq 'wall') -and ($applySent.element_ids[0] -eq 7002) -and
        ($applySent.spec.wall.method -eq 'curtain') -and ($applySent.spec.wall.curtain_type_id -eq 501) -and ($applySent.spec.wall.placeholder_type_id -eq 503))
    Check 'the door is hosted on the staged wall' (($script:sent['t1-frc-door'].elements[0].host_id -eq 7002))
    Check 'the remove names both carriers' ((@($script:removeTargets) -contains 7002) -and (@($script:removeTargets) -contains 7003))
    $ceilSent = $script:sent['t1-frc-ceiling-apply']
    Check 'the ceiling apply sends the sloped glazing layer at 0 deg and again 30 mm above it with no angle, the curtain type as hanger' (($ceilSent.element_ids[0] -eq 7006) -and (@($ceilSent.spec.ceiling.layers).Count -eq 2) -and
        ($ceilSent.spec.ceiling.layers[0].type_id -eq 505) -and ($ceilSent.spec.ceiling.layers[0].angle_deg -eq 0) -and ($ceilSent.spec.ceiling.layers[1].type_id -eq 505) -and
        (-not $ceilSent.spec.ceiling.layers[1].ContainsKey('angle_deg')) -and ($ceilSent.spec.ceiling.layers[1].offset_mm - $ceilSent.spec.ceiling.layers[0].offset_mm -eq 30) -and ($ceilSent.spec.ceiling.hanger.type_id -eq 501))
    Check 'cleanup deletes the recreated carrier, never the deleted one, and no document type' ((@($script:deleted) -contains 9999) -and -not (@($script:deleted) -contains 7003) -and
        (@(501..507 | Where-Object { @($script:deleted) -contains $_ }).Count -eq 0) -and (@($script:deleted).Count -eq 9))
    Check 'the manage_curtain case reports the grid angles' ($by[$names[7]].Detail -match 'grid 1 at 0 deg, grid 2 at 90 deg')
    $a3Sent = $script:sent['t1-frc-apply3']
    Check 'the one-door and the two-door walls run on the DEFAULT multi_opening (none sent)' ((-not $applySent.spec.wall.ContainsKey('multi_opening')) -and ($null -ne $a3Sent) -and
        (-not $a3Sent.spec.wall.ContainsKey('multi_opening')) -and ($a3Sent.element_ids[0] -eq 7007))
    Check 'both doors are hosted on the third wall and its kept carrier is removed on its own' (($script:sent['t1-frc-door3a'].elements[0].host_id -eq 7007) -and
        ($script:sent['t1-frc-door3b'].elements[0].host_id -eq 7007) -and ((@($script:sent['t1-frc-remove3'].element_ids) -join ',') -eq '7007'))
    Check 'the two-door case reports the overlapping piece ids' ($by[$names[9]].Detail -match 'overlapped_by 9301,9302,9303,9304,9305')

    # ---- a kept carrier whose result does not name the overlap fails, and is still removed ----
    New-State; $script:noOverlap = $true
    $ovBy = RunBy (Ctx 't9')
    Check 'a kept carrier whose result does not name the overlap fails the two-door case' (($ovBy[$names[9]].Outcome -eq 'fail') -and ($ovBy[$names[9]].Detail -match 'does not name the overlap'))
    Check 'that committed apply is still removed, and the cleanup passes' ((@($script:removeTargets) -contains 7007) -and ($ovBy[$names[8]].Outcome -eq 'pass'))

    # ---- the kept carrier not restored: the two-door case and the cleanup fail ----
    New-State; $script:w3Restored = $false
    $w3By = RunBy (Ctx 't10')
    Check 'a kept carrier not restored fails the two-door case with its reason, and the cleanup' (($w3By[$names[9]].Outcome -eq 'fail') -and
        ($w3By[$names[9]].Detail -match 'type changed after the apply') -and ($w3By[$names[8]].Outcome -eq 'fail'))

    # ---- a carrier whose restore is refused fails the remove case, and the cleanup ----
    New-State; $script:w1Restore = @{ restored = $false; inserts_changed = 0; why = 'its type changed after the apply' }
    $refusedBy = RunBy (Ctx 't2')
    Check 'a refused restore fails the remove case with its reason' (($refusedBy[$names[4]].Outcome -eq 'fail') -and ($refusedBy[$names[4]].Detail -match 'type changed after the apply'))
    Check 'a refused restore fails the cleanup case too' ($refusedBy[$names[8]].Outcome -eq 'fail')

    # ---- the door not back where it was is a fail ----
    New-State; $script:w1Restore = @{ restored = $true; inserts_changed = 1; why = $null }
    $movedBy = RunBy (Ctx 't3')
    Check 'a restored carrier whose door moved fails the remove case' (($movedBy[$names[4]].Outcome -eq 'fail') -and ($movedBy[$names[4]].Detail -match 'door back'))

    # ---- a hanger not at its support fails the ceiling apply ----
    New-State; $script:notAtSupport = @(9103)
    $shortBy = RunBy (Ctx 't4')
    Check 'a hanger not at the support fails the ceiling apply, counted' (($shortBy[$names[6]].Outcome -eq 'fail') -and ($shortBy[$names[6]].Detail -match '1 hanger\(s\) not at the support'))

    # ---- no sloped glazing type: the ceiling and read cases are not_covered, named ----
    New-State; $script:noGlazing = $true
    $bareBy = RunBy (Ctx 't5')
    Check 'without a sloped glazing type the ceiling cases are not_covered with the reason' (($bareBy[$names[5]].Outcome -eq 'not_covered') -and ($bareBy[$names[5]].Detail -match "sloped glazing type ''") -and ($bareBy[$names[6]].Outcome -eq 'not_covered'))
    Check 'without a layer the manage_curtain read is not_covered and the cleanup still passes' (($bareBy[$names[7]].Outcome -eq 'not_covered') -and ($bareBy[$names[8]].Outcome -eq 'pass'))

    # ---- the document's Sloped Glazing type has no grid 1: the ceiling is not_covered, named ----
    New-State; $script:glazingRefused = $true
    $gBy = RunBy (Ctx 't7')
    Check 'a Sloped Glazing type without grid 1 leaves the ceiling cases not_covered naming it, no ceiling apply sent' (($gBy[$names[5]].Outcome -eq 'not_covered') -and
        ($gBy[$names[5]].Detail -match 'no grid 1') -and ($gBy[$names[6]].Outcome -eq 'not_covered') -and -not $script:sent.ContainsKey('t7-frc-ceiling-apply'))

    # ---- a type past the first page of a truncated query is found on the next page ----
    New-State; $script:pagedWalls = $true
    $pageBy = RunBy (Ctx 't14')
    Check 'a type past the first page of a truncated query is found on the next page' (($pageBy[$names[1]].Outcome -eq 'pass') -and
        ($script:sent['t14-frc-apply'].spec.wall.curtain_type_id -eq 501))

    # ---- a closed write tier ----
    New-State
    $closed = Ctx 't6'; $closed.WriteGate = $true
    $shut = @(& $module.Run $closed)
    Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
    Check 'a closed write tier names each case''s own tool' ((@($shut | Where-Object { $_.Name -like 'framing curtain probes:*' -and $_.Tool -eq 'horizun_delete_verified' }).Count -eq 1) -and
        (@($shut | Where-Object { $_.Name -like 'manage_curtain*' -and $_.Tool -eq 'horizun_manage_curtain' }).Count -eq 1) -and
        (@($shut | Where-Object { $_.Name -like 'framing curtain wall*' -and $_.Tool -ne 'horizun_framing' }).Count -eq 0))
}
finally { }

if ($fails) { "framing-curtain tests: $fails FAILED"; exit 1 } else { 'framing-curtain tests: ALL PASS'; exit 0 }
