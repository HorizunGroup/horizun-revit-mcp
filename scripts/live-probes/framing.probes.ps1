# Live probes for horizun_framing. Everything stands on the module's own level, far from
# the real model: an own compound Basic wall with an own door and window, framed with a
# line-based Generic Model type 'HZ_STUD_92' and a 41.3 x 92.1 mm Structural Column type
# 'HZ_STUDCOL_92' that the fixture must carry, found BY NAME (a fixture without them leaves
# the cases not_covered, named; the probe never authors a family). The
# ceiling cases stage an own floor over an own ceiling (the hangers must reach THAT
# floor, by id) and a second ceiling 20 m away with nothing above it (every hanger
# station no_support_above, none planned). Whether Revit keeps each committed axis on
# the planned ends is exactly what each apply measures (endpoints within 1 mm, re-read
# by the tool). A last wall, at 45 degrees, is framed with the document's own Structural
# Columns (studs) and Structural Framing (tracks) types, the Column and Beam placements the
# line-based cases never take; not_covered, named, when the document carries neither
# category. Everything created - framing by operation=remove, staging by
# horizun_delete_verified - is deleted at the end; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'framing'
    Catalog = @(
        @{ Name = 'framing wall: the rehearsal plans studs, tracks, kings, jacks and headers around a door and a window'; Tool = 'horizun_framing' }
        @{ Name = 'framing wall: apply verified, no stud through the door or window, hosted inserts untouched'; Tool = 'horizun_framing' }
        @{ Name = 'framing wall: a second apply of the same spec is already_applied'; Tool = 'horizun_framing' }
        @{ Name = 'framing read: the members are listed by marker for their wall'; Tool = 'horizun_framing' }
        @{ Name = 'framing remove: every member and work plane deleted, verified'; Tool = 'horizun_framing' }
        @{ Name = 'framing ceiling: the rehearsal plans mains, cross, perimeter and hangers up to the floor above'; Tool = 'horizun_framing' }
        @{ Name = 'framing ceiling: apply verified inside the boundary, every hanger carried by the staged floor'; Tool = 'horizun_framing' }
        @{ Name = 'framing ceiling: a ceiling with nothing above reports no_support_above and plans no hanger'; Tool = 'horizun_framing' }
        @{ Name = 'framing probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
        @{ Name = 'framing wall (structural types): column studs and beam tracks on a 45-degree wall, verified'; Tool = 'horizun_framing' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'framing wall: the rehearsal plans studs, tracks, kings, jacks and headers around a door and a window',
            'framing wall: apply verified, no stud through the door or window, hosted inserts untouched',
            'framing wall: a second apply of the same spec is already_applied',
            'framing read: the members are listed by marker for their wall',
            'framing remove: every member and work plane deleted, verified',
            'framing ceiling: the rehearsal plans mains, cross, perimeter and hangers up to the floor above',
            'framing ceiling: apply verified inside the boundary, every hanger carried by the staged floor',
            'framing ceiling: a ceiling with nothing above reports no_support_above and plans no hanger',
            'framing probes: everything created is deleted',
            'framing wall (structural types): column studs and beam tracks on a 45-degree wall, verified')
        $T = 'horizun_framing'; $DeleteTool = 'horizun_delete_verified'   # not $DeleteTool: PowerShell names are case-insensitive and $d holds the rehearsal
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { $tool = $T; if ($catalog[$i] -like 'framing probes:*') { $tool = $DeleteTool }; Case $catalog[$i] $tool 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-fr-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }

        # ---- staging: own level, own compound wall with a door and a window, own member family ----
        $E = 98000.0; $X = 860000.0; $Y = 0.0
        # A wall whose core is a stud layer, by name from the document; else the document's
        # first Basic wall. Doors and windows BY NAME, NEVER "THE FIRST ONE": an MEP fixture
        # carries none (MEASURED 2026-09-26 on HZ_WRITE), and then the wall cases are not_covered.
        $wallType = $null
        function Bring($category, $typeName, $familyName) {
            return @(Types $category | Where-Object { [string]$_.type -eq $typeName -and [string]$_.family -eq $familyName }) | Select-Object -First 1
        }
        $wallType = Bring 'OST_Walls' 'Exterior - Brick on Mtl. Stud' 'Basic Wall'
        if (-not $wallType) { $wallType = Types 'OST_Walls' | Where-Object { [string]$_.family -match '(?i)basic|b.sico' } | Select-Object -First 1 }
        $doorType = Bring 'OST_Doors' '0915 x 2134mm' 'M_Single-Flush'
        $windowType = Bring 'OST_Windows' '0915 x 1220mm' 'M_Fixed'
        # The line-based member (tracks, headers, sills, ceiling mains/cross/perimeter) and the
        # vertical member: VERTICAL MEMBERS ARE STRUCTURAL COLUMNS (MEASURED 2026-09-26: Revit
        # refuses to stand a line-based family on a created plane). The column must fit inside a
        # stud layer: the Autodesk concrete column (300 x 450) does not (inside_layer failed by
        # 148.8 mm, measured the same day), hence a 41.3 x 92.1 mm type of its own name.
        $member = @(Types 'OST_GenericModel' | Where-Object { [string]$_.type -eq 'HZ_STUD_92' } | ForEach-Object { [long]$_.element_id }) | Select-Object -First 1
        $studColumn = @(Types 'OST_StructuralColumns' | Where-Object { [string]$_.type -eq 'HZ_STUDCOL_92' } | ForEach-Object { [long]$_.element_id }) | Select-Object -First 1
        $level = Create @(@{ kind = 'level'; name = "HZ_FR_$run"; elevation = $E }) 'level'
        $wall = $null; $door = $null; $window = $null
        if ($level -and $wallType) { $wall = Create @(@{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 6000), $Y, $E); level_id = $level; type_id = $wallType.element_id; height = 3000 }) 'wall' }
        if ($wall -and $doorType) { $door = Create @(@{ kind = 'family_instance'; type_id = $doorType.element_id; host_id = $wall; point = @(($X + 1500), $Y, $E); coordinate_mode = 'absolute'; level_id = $level }) 'door' }
        if ($wall -and $windowType) { $window = Create @(@{ kind = 'family_instance'; type_id = $windowType.element_id; host_id = $wall; point = @(($X + 4200), $Y, ($E + 900)); coordinate_mode = 'absolute'; level_id = $level }) 'window' }

        $spec = @{ wall = @{ layer = 'core'
            stud = @{ type_id = $studColumn; spacing_mm = 406.4; start = 'wall_start'; double_at_ends = $false; width_mm = 41.3 }
            track = @{ bottom_type_id = $member; top_same_as_bottom = $true; thickness_mm = 0.9 }
            openings = @{ king_studs = 1; jack_studs = $true; header_type_id = $member; sill_type_id = $member; cripple_spacing_mm = 406.4
                          header_depth_mm = 40; sill_depth_mm = 40 } } }
        $wallArgs = @{ operation = 'wall'; target_document = $doc; element_ids = @($wall); spec = $spec }
        $ready = $wall -and $door -and $window -and $member -and $studColumn
        $why = "staging incomplete: wall $wall, door $door, window $window, member type $member, stud column $studColumn (the document must carry the line-based Generic Model type 'HZ_STUD_92' and the Structural Column type 'HZ_STUDCOL_92')"

        # ==== 1: rehearsal ===============================================================
        $planned = 0
        if (-not $ready) { Case $catalog[0] $T 'not_covered' $why }
        else {
            $d = & $Ctx.Call $T ($wallArgs + @{ dry_run = $true })
            $src = $null
            if ($d.data) { $src = @($d.data.plan.sources)[0] }
            if ($d.isError -or -not $src) { Case $catalog[0] $T 'fail' ('rehearsal: ' + (Short $d)) }
            else {
                $c = $src.count_by_role; $planned = [int]$src.member_count
                $problems = @()
                if (@($src.openings).Count -ne 2) { $problems += "read $(@($src.openings).Count) openings, expected 2" }
                foreach ($role in 'stud', 'track', 'king', 'jack', 'header') { if (-not ([int]$c.$role -gt 0)) { $problems += "no $role planned" } }
                if ([int]$c.king -lt 4) { $problems += "kings $($c.king) < 4 for two openings" }
                if ([int]$c.track -lt 2) { $problems += "tracks $($c.track) < 2" }
                if ($problems.Count -gt 0) { Case $catalog[0] $T 'fail' ($problems -join '; ') }
                else { Case $catalog[0] $T 'pass' ("$planned members: " + (($c.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ',') + '; openings read from ' + ((@($src.openings) | ForEach-Object { $_.read_from }) -join ',')) }
            }
        }

        # ==== 2: apply, 3: idempotent apply ===============================================
        $applied = $false
        if (-not $ready) { Case $catalog[1] $T 'not_covered' $why; Case $catalog[2] $T 'not_covered' $why }
        else {
            $a = & $Ctx.Apply $T $wallArgs ($run + '-fr-apply')
            $ev = $null
            if ($a.answer.data) { $ev = @($a.answer.data.evidence.sources)[0] }
            if ($a.stage -ne 'apply' -or $a.answer.isError -or $a.answer.data.postconditions.all_verified -ne $true -or -not $ev) { Case $catalog[1] $T 'fail' ('apply: ' + (Short $a.answer)) }
            elseif ([string]$a.answer.data.application.state -ne 'verified_applied') { Case $catalog[1] $T 'fail' "application.state '$($a.answer.data.application.state)', expected verified_applied" }
            elseif ([int]$ev.stud_crossings -ne 0 -or [int]$ev.inserts_changed -ne 0 -or [int]$ev.inserts_checked -ne 2 -or [int]$ev.found -ne $planned) {
                Case $catalog[1] $T 'fail' "crossings $($ev.stud_crossings), inserts changed $($ev.inserts_changed) of $($ev.inserts_checked), found $($ev.found) of $planned" }
            else { $applied = $true; Case $catalog[1] $T 'pass' ("$($ev.found) members re-read, max endpoint deviation $($ev.max_endpoint_deviation_mm) mm, read by " + (@($a.answer.data.evidence.endpoint_read) -join ',') + ", joins with the wall undone $($a.answer.data.evidence.source_joins_undone)") }
            if (-not $applied) { Case $catalog[2] $T 'not_covered' 'the first apply did not verify' }
            else {
                $b = & $Ctx.Apply $T $wallArgs ($run + '-fr-apply-again')
                if ($b.stage -eq 'apply' -and -not $b.answer.isError -and $b.answer.data.already_applied -eq $true -and $b.answer.data.postconditions.all_verified -eq $true -and [string]$b.answer.data.application.state -eq 'no_op') { Case $catalog[2] $T 'pass' 'already_applied (application no_op), the existing members re-read against the plan' }
                else { Case $catalog[2] $T 'fail' ('second apply: ' + (Short $b.answer)) }
            }
        }

        # ==== 4: read, 5: remove ===========================================================
        $wallFramingGone = $true
        if (-not $applied) { Case $catalog[3] $T 'not_covered' 'nothing was applied'; Case $catalog[4] $T 'not_covered' 'nothing was applied' }
        else {
            $r = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; element_ids = @($wall) }
            if (-not $r.isError -and [int]$r.data.member_count -eq $planned) { Case $catalog[3] $T 'pass' "$($r.data.member_count) marked element(s) for wall $wall" }
            else { Case $catalog[3] $T 'fail' ('read: ' + (Short $r)) }
            $rm = & $Ctx.Apply $T @{ operation = 'remove'; target_document = $doc; element_ids = @($wall) } ($run + '-fr-remove')   # not $x: names are case-insensitive and $X is the staging origin the ceiling cases reuse
            $after = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; element_ids = @($wall) }
            $wallFramingGone = ($rm.stage -eq 'apply' -and -not $rm.answer.isError -and $rm.answer.data.postconditions.all_verified -eq $true)
            if ($rm.stage -eq 'apply' -and -not $rm.answer.isError -and $rm.answer.data.postconditions.all_verified -eq $true -and ([int]$after.data.member_count + [int]$after.data.work_plane_count) -eq 0) { Case $catalog[4] $T 'pass' ("removed $(@($rm.answer.data.evidence.removed_ids).Count) element(s); read finds none") }
            else { Case $catalog[4] $T 'fail' ('remove: ' + (Short $rm.answer) + ' / read after: ' + $after.data.member_count) }
        }

        # ==== 6, 7, 8: ceiling =============================================================
        # Ceiling A (4800 x 3600) hangs 600 mm under an own floor whose TOP is at level +
        # 3000; ceiling B (2400 x 2400) lies 20 m away with nothing above it. A profile's z
        # is the element's height: the ceiling's underside offset, the floor's top face.
        $ceilingTypes = @(Types 'OST_Ceilings')
        $ceilingType = @($ceilingTypes | Where-Object { [string]$_.family -match '(?i)compound|compuest' }) + $ceilingTypes | Select-Object -First 1
        $floorType = Types 'OST_Floors' | Select-Object -First 1
        $CZ = $E + 2400; $FZ = $E + 3000; $CY = $Y + 3000
        function CeilingAt($x0, $y0, $x1, $y1, $key) {
            $item = @{ kind = 'ceiling'; level_id = $level; profile = @(, @(@($x0, $y0, $CZ), @($x1, $y0, $CZ), @($x1, $y1, $CZ), @($x0, $y1, $CZ))) }
            if ($ceilingType) { $item.type_id = $ceilingType.element_id }
            return Create @($item) $key
        }
        $floor = $null; $ceilingA = $null; $ceilingB = $null
        if ($level -and $floorType) {
            $floor = Create @(@{ kind = 'floor'; level_id = $level; type_id = $floorType.element_id
                                 profile = @(, @(@(($X - 600), ($CY - 600), $FZ), @(($X + 5400), ($CY - 600), $FZ), @(($X + 5400), ($CY + 4200), $FZ), @(($X - 600), ($CY + 4200), $FZ))) }) 'floor'
        }
        if ($level) {
            $ceilingA = CeilingAt $X $CY ($X + 4800) ($CY + 3600) 'ceiling-a'
            $ceilingB = CeilingAt ($X + 20000) $CY ($X + 22400) ($CY + 2400) 'ceiling-b'
        }
        $ceilingSpec = @{ ceiling = @{
            main = @{ type_id = $member; spacing_mm = 1200; direction = 'short'; depth_mm = 38 }
            cross = @{ type_id = $member; spacing_mm = 400; depth_mm = 22 }
            perimeter = @{ type_id = $member; depth_mm = 22 }
            hanger = @{ type_id = $studColumn; spacing_mm = 1200; max_length_mm = 3000 }
            drop_mm = 22 } }
        function CeilingArgs($id) { @{ operation = 'ceiling'; target_document = $doc; element_ids = @($id); spec = $ceilingSpec } }
        function Roles($counts) { ($counts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ',' }
        $cWhy = "staging incomplete: floor $floor, ceiling $ceilingA, member type $member (floor type found: $([bool]$floorType))"
        $cPlanned = 0; $hangers = 0; $cCommitted = $false
        if (-not ($floor -and $ceilingA -and $member -and $studColumn)) { Case $catalog[5] $T 'not_covered' $cWhy; Case $catalog[6] $T 'not_covered' $cWhy }
        else {
            $cd = & $Ctx.Call $T ((CeilingArgs $ceilingA) + @{ dry_run = $true })
            $cs = $null
            if ($cd.data) { $cs = @($cd.data.plan.sources)[0] }
            if ($cd.isError -or -not $cs) { Case $catalog[5] $T 'fail' ('rehearsal: ' + (Short $cd)); Case $catalog[6] $T 'not_covered' 'the rehearsal failed' }
            else {
                $cc = $cs.count_by_role; $cPlanned = [int]$cs.member_count; $hangers = [int]$cc.hanger; $zm = $cs.z_mm
                $problems = @()
                foreach ($role in 'main', 'cross', 'perimeter', 'hanger') { if (-not ([int]$cc.$role -gt 0)) { $problems += "no $role planned" } }
                if ([int]$cc.perimeter -lt 4) { $problems += "perimeter $($cc.perimeter) < 4 for a rectangle" }
                if (@($cs.no_support_above).Count -ne 0) { $problems += "$(@($cs.no_support_above).Count) station(s) found nothing above under the staged floor" }
                if (-not ([double]$zm.main_axis -gt [double]$cs.top_face_mm -and [double]$zm.hanger_from -gt [double]$zm.main_axis)) { $problems += "heights out of order: top $($cs.top_face_mm), main $($zm.main_axis), hanger from $($zm.hanger_from)" }
                # Each listed rod climbs from the mains to the floor's underside: above its
                # start, at or below the floor's top, within a floor thickness of it.
                $rods = @($cs.members | Where-Object { $_.role -eq 'hanger' })
                $tops = @($rods | ForEach-Object { [double]@($_.to)[2] })
                $off = @($tops | Where-Object { $_ -le [double]$zm.hanger_from -or $_ -gt ($FZ + 1) -or $_ -lt ($FZ - 1000) })
                if ($rods.Count -eq 0) { $problems += 'no hanger listed in the plan' }
                if ($off.Count -gt 0) { $problems += "$($off.Count) rod(s) end off the floor's underside (tops " + (($off | Select-Object -First 3) -join ',') + ", floor top $FZ)" }
                if ($problems.Count -gt 0) { Case $catalog[5] $T 'fail' ($problems -join '; ') }
                else { Case $catalog[5] $T 'pass' ("$cPlanned members: " + (Roles $cc) + "; top face $($cs.top_face_mm), mains at $($zm.main_axis), rods $($zm.hanger_from) -> " + (($tops | Sort-Object -Unique | Select-Object -First 3) -join ',')) }

                $ca = & $Ctx.Apply $T (CeilingArgs $ceilingA) ($run + '-fr-ceiling')
                $cCommitted = ($ca.stage -eq 'apply' -and -not $ca.answer.isError -and $ca.answer.data.transaction_status -eq 'Committed')
                $cev = $null; $inside = $null
                if ($ca.answer.data) {
                    $cev = @($ca.answer.data.evidence.sources)[0]
                    $inside = @($ca.answer.data.postconditions.properties | Where-Object { $_.property -eq 'inside_boundary' }) | Select-Object -First 1
                }
                if (-not $cCommitted -or $ca.answer.data.postconditions.all_verified -ne $true -or -not $cev) { Case $catalog[6] $T 'fail' ('apply: ' + (Short $ca.answer)) }
                else {
                    $onFloor = 0; $elsewhere = @()
                    if ($cev.hanger_supports) {
                        foreach ($s in $cev.hanger_supports.PSObject.Properties) { if ($s.Name -eq "host:$floor") { $onFloor += [int]$s.Value } else { $elsewhere += "$($s.Name)=$($s.Value)" } }
                    }
                    $problems = @()
                    if ([int]$cev.found -ne $cPlanned) { $problems += "found $($cev.found) of $cPlanned" }
                    if (-not $inside -or $inside.matches -ne $true) { $problems += 'inside_boundary not verified: ' + ($inside | ConvertTo-Json -Compress -Depth 4) }
                    if (@($cev.no_support_above).Count -ne 0) { $problems += "$(@($cev.no_support_above).Count) no_support_above" }
                    if ($hangers -le 0 -or $onFloor -ne $hangers) { $problems += "$onFloor of $hangers hanger(s) carried by floor $floor" }
                    if ($elsewhere.Count -gt 0) { $problems += 'hangers carried elsewhere: ' + ($elsewhere -join ',') }
                    if ($problems.Count -gt 0) { Case $catalog[6] $T 'fail' ($problems -join '; ') }
                    else { Case $catalog[6] $T 'pass' ("$($cev.found) members re-read, $onFloor hangers on floor $floor, max endpoint deviation $($cev.max_endpoint_deviation_mm) mm, max outside boundary $($cev.max_outside_boundary_mm) mm") }
                }
            }
        }
        if (-not ($ceilingB -and $member -and $studColumn)) { Case $catalog[7] $T 'not_covered' "staging incomplete: ceiling $ceilingB, member type $member" }
        else {
            $nd = & $Ctx.Call $T ((CeilingArgs $ceilingB) + @{ dry_run = $true })
            $ns = $null
            if ($nd.data) { $ns = @($nd.data.plan.sources)[0] }
            if ($nd.isError -or -not $ns) { Case $catalog[7] $T 'fail' ('rehearsal: ' + (Short $nd)) }
            elseif (@($ns.no_support_above).Count -gt 0 -and [int]$ns.count_by_role.hanger -eq 0 -and [int]$ns.count_by_role.main -gt 0) {
                Case $catalog[7] $T 'pass' ("$(@($ns.no_support_above).Count) station(s) no_support_above, no hanger planned; still planned: " + (Roles $ns.count_by_role)) }
            else { Case $catalog[7] $T 'fail' ("no_support_above $(@($ns.no_support_above).Count), hangers planned $([int]$ns.count_by_role.hanger), mains $([int]$ns.count_by_role.main)") }
        }

        # ==== 10: structural types on a wall not parallel to X ============================
        # Studs as Structural Columns and tracks as Structural Framing: the vertical column
        # turned onto the wall, the beam's z-justification and its unjoined ends, which the
        # Generic Model cases never exercise. The tool re-reads each column from its base and
        # top constraints and each beam from its curve; the 45-degree wall means an axis or a
        # section read in the wrong frame cannot pass by accident.
        # STAGED, NEVER ASSUMED (MEASURED 2026-09-26: HZ_WRITE carries neither category). The
        # studs are the 41.3 x 92.1 'HZ_STUDCOL_92' column (placed on a vertical line, so its
        # height is the line's); the beam is the document's own HSS or 'HZ_FRB' framing type.
        $columnType = if ($studColumn) { [pscustomobject]@{ element_id = $studColumn; family = 'HZ_STUDCOL' } } else { $null }
        $beamType = Types 'OST_StructuralFraming' | Where-Object { [string]$_.family -match '(?i)HSS|HZ_FRB' } | Select-Object -First 1
        $diag = $null; $structFramed = $false
        # A layer wide enough for the beam: the HZ_FRB section is 203.2 mm wide and the
        # stud wall's layer 152.4 mm (inside_layer failed by 25.4 mm, MEASURED 2026-09-26).
        $diagType = Bring 'OST_Walls' 'Generic - 300mm' 'Basic Wall'
        if (-not $diagType) { $diagType = $wallType }
        if ($level -and $diagType -and $beamType -and $columnType) { $diag = Create @(@{ kind = 'wall'; start = @($X, ($Y - 60000), $E); end = @(($X + 4242.6), ($Y - 60000 + 4242.6), $E); level_id = $level; type_id = $diagType.element_id; height = 3000 }) 'diag-wall' }
        if (-not $diag) { Case $catalog[9] $T 'not_covered' "staging incomplete: structural framing type '$($beamType.element_id)', structural column type '$($columnType.element_id)', 45-degree wall '$diag' (the document must carry both categories' types)" }
        else {
            $sArgs = @{ operation = 'wall'; target_document = $doc; element_ids = @($diag); spec = @{ wall = @{
                stud = @{ type_id = [long]$columnType.element_id; spacing_mm = 1200; start = 'wall_start'; double_at_ends = $false; width_mm = 41.3 }
                track = @{ bottom_type_id = [long]$beamType.element_id; top_same_as_bottom = $true; thickness_mm = 0.9 } } } }
            $sa = & $Ctx.Apply $T $sArgs ($run + '-fr-struct')
            $structFramed = ($sa.stage -eq 'apply' -and -not $sa.answer.isError)
            $sev = $null; $reads = @()
            if ($sa.answer.data) { $sev = @($sa.answer.data.evidence.sources)[0]; $reads = @($sa.answer.data.evidence.endpoint_read) }
            $problems = @()
            if (-not $structFramed -or $sa.answer.data.postconditions.all_verified -ne $true -or -not $sev) { $problems += 'apply: ' + (Short $sa.answer) }
            else {
                if ([string]$sa.answer.data.application.state -ne 'verified_applied') { $problems += "application.state '$($sa.answer.data.application.state)', expected verified_applied" }
                if ([double]$sev.max_endpoint_deviation_mm -gt 1.0) { $problems += "endpoint deviation $($sev.max_endpoint_deviation_mm) mm" }
                # A column placed on a vertical LINE reports a location curve, its real axis, not a
                # point with base/top constraints (MEASURED 2026-09-26, Revit 2026): both reads are
                # accepted for studs; what must exist is a read, and all_verified judged it.
                if ($reads.Count -eq 0) { $problems += 'no member was re-read' }
                if ($reads -notcontains 'location_curve' -and $reads -notcontains 'location_curve_plus_level_offset') { $problems += 'no track was re-read from its location curve' }
            }
            if ($problems.Count -gt 0) { Case $catalog[9] $T 'fail' ($problems -join '; ') }
            else { Case $catalog[9] $T 'pass' ("$($sev.found) members re-read on a 45-degree wall with types $($columnType.element_id)/$($beamType.element_id), max endpoint deviation $($sev.max_endpoint_deviation_mm) mm, read by " + ($reads -join ',') + '; section_along_wall and beam_settings inside all_verified') }
        }

        # ==== 9: cleanup ==================================================================
        # The ceiling's members are not hosted by it: deleting the ceiling would orphan
        # them, so operation=remove runs first whenever an apply committed.
        $notes = @(); $framingGone = $wallFramingGone
        if (-not $wallFramingGone) { $notes += 'the wall framing was not removed' }
        if ($cCommitted) {
            $cx = & $Ctx.Apply $T @{ operation = 'remove'; target_document = $doc; element_ids = @($ceilingA) } ($run + '-fr-ceiling-remove')
            $framingGone = ($cx.stage -eq 'apply' -and -not $cx.answer.isError -and $cx.answer.data.postconditions.all_verified -eq $true)
            if ($framingGone) { $notes += "ceiling framing removed ($(@($cx.answer.data.evidence.removed_ids).Count) element(s))" } else { $notes += 'ceiling remove: ' + (Short $cx.answer) }
        }
        if ($structFramed) {
            $sx = & $Ctx.Apply $T @{ operation = 'remove'; target_document = $doc; element_ids = @($diag) } ($run + '-fr-struct-remove')
            if ($sx.stage -eq 'apply' -and -not $sx.answer.isError -and $sx.answer.data.postconditions.all_verified -eq $true) { $notes += "structural framing removed ($(@($sx.answer.data.evidence.removed_ids).Count) element(s))" }
            else { $framingGone = $false; $notes += 'structural remove: ' + (Short $sx.answer) }
        }
        $ids = @($created | Sort-Object -Descending -Unique)
        if ($ids.Count -eq 0 -and -not $cCommitted) { Case $catalog[8] $DeleteTool 'not_covered' 'nothing was created' }
        else {
            $delOk = $true
            if ($ids.Count -gt 0) {
                $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $ids } ($run + '-fr-cleanup')
                $delOk = ($del.stage -eq 'apply' -and -not $del.answer.isError)
                if ($delOk) { $notes += "$($ids.Count) staged element(s) deleted" } else { $notes += 'cleanup: ' + (Short $del.answer) }
            }
            if ($delOk -and $framingGone) { Case $catalog[8] $DeleteTool 'pass' ($notes -join '; ') } else { Case $catalog[8] $DeleteTool 'fail' ($notes -join '; ') }
        }
        return $cases
    }
}
