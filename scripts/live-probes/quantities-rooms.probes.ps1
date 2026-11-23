# Live probes for horizun_quantities modes room_finishes and carbon, and for room membership
# (horizun_query_model include_room, takeoff group_by='room'). Everything stands on the
# module's own level at X = 1,110,000 mm (branch A's slot), far from the real model: four
# own walls of a Basic wall type taken BY NAME from the document (never "the first type";
# a document without it leaves the cases not staged) closing a 6 x 4 m rectangle, an own
# door in the south wall and an own window in the north wall, an own room and an own space
# inside, an own floor inside, and one more own wall 2 m outside the rectangle. What the probe
# cannot know in advance (wall thickness, room height, the wall type's materials, the phase
# rooms land in) is read from the replies, never assumed: the carbon factor table is built
# from the material names the first carbon call reports as without a factor. Everything
# created is deleted at the end with horizun_delete_verified mode='ids'; nothing is saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'quantities-rooms'
    Catalog = @(
        @{ Name = 'rooms room_finishes: gross wall and floor faces of an own room, door and window deducted in their own column'; Tool = 'horizun_quantities' }
        @{ Name = 'rooms room_finishes: without phase it is refused and nothing is measured'; Tool = 'horizun_quantities' }
        @{ Name = 'rooms carbon: with no matching factor every material is named no_factor and nothing is counted'; Tool = 'horizun_quantities' }
        @{ Name = 'rooms carbon: a two-material table (per m3 and per kg) counts exactly those, the rest named'; Tool = 'horizun_quantities' }
        @{ Name = 'rooms membership: include_room puts the floor and the space in the room, the outside wall unassigned'; Tool = 'horizun_query_model' }
        @{ Name = 'rooms membership: include_room without phase is refused'; Tool = 'horizun_query_model' }
        @{ Name = 'rooms takeoff: group_by=room rolls the floor into the room key and the outside wall into (unassigned)'; Tool = 'horizun_quantities' }
        @{ Name = 'rooms room_finishes: a room left unenclosed (one wall deleted) is named not_enclosed, never a zero'; Tool = 'horizun_quantities' }
        @{ Name = 'rooms probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
        @{ Name = 'rooms carbon: a phase (which carbon does not read) is refused by name, nothing measured'; Tool = 'horizun_quantities' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'rooms room_finishes: gross wall and floor faces of an own room, door and window deducted in their own column',
            'rooms room_finishes: without phase it is refused and nothing is measured',
            'rooms carbon: with no matching factor every material is named no_factor and nothing is counted',
            'rooms carbon: a two-material table (per m3 and per kg) counts exactly those, the rest named',
            'rooms membership: include_room puts the floor and the space in the room, the outside wall unassigned',
            'rooms membership: include_room without phase is refused',
            'rooms takeoff: group_by=room rolls the floor into the room key and the outside wall into (unassigned)',
            'rooms room_finishes: a room left unenclosed (one wall deleted) is named not_enclosed, never a zero',
            'rooms probes: everything created is deleted',
            'rooms carbon: a phase (which carbon does not read) is refused by name, nothing measured')
        $Q = 'horizun_quantities'; $QM = 'horizun_query_model'; $DeleteTool = 'horizun_delete_verified'   # never $q/$d: names are case-insensitive
        function ToolOf($i) { if ($i -eq 4 -or $i -eq 5) { $QM } elseif ($i -eq 8) { $DeleteTool } else { $Q } }
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] (ToolOf $i) 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Near($a, $b, $tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
        function Types($category) {
            $r = & $Ctx.Call $QM @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $r.data) { return @() }
            return @($r.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-rm-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }
        function Bring($category, $typeName, $familyName) {
            return @(Types $category | Where-Object { [string]$_.type -eq $typeName -and [string]$_.family -eq $familyName }) | Select-Object -First 1
        }

        # ---- staging: own level, a closed rectangle of own walls, door, window, room, space, floor ----
        $E = 95000.0; $X = 1110000.0; $Y = 0.0; $W = 6000.0; $D = 4000.0
        $wallType = Bring 'OST_Walls' 'Exterior - Brick on Mtl. Stud' 'Basic Wall'
        $doorType = Bring 'OST_Doors' '0915 x 2134mm' 'M_Single-Flush'
        $windowType = Bring 'OST_Windows' '0915 x 1220mm' 'M_Fixed'
        $floorType = Bring 'OST_Floors' 'Generic 150mm' 'Floor'
        $level = Create @(@{ kind = 'level'; name = "HZ_RM_$run"; elevation = $E }) 'level'
        $walls = @{}
        if ($level -and $wallType) {
            $corners = @(@($X, $Y), @(($X + $W), $Y), @(($X + $W), ($Y + $D)), @($X, ($Y + $D)))
            $sides = @('s', 'e', 'n', 'w')
            for ($i = 0; $i -lt 4; $i++) {
                $a = $corners[$i]; $b = $corners[($i + 1) % 4]
                $walls[$sides[$i]] = Create @(@{ kind = 'wall'; start = @($a[0], $a[1], $E); end = @($b[0], $b[1], $E); level_id = $level; type_id = $wallType.element_id; height = 3000 }) ('wall-' + $sides[$i])
            }
        }
        $outside = $null
        if ($level -and $wallType) { $outside = Create @(@{ kind = 'wall'; start = @(($X + $W + 2000), $Y, $E); end = @(($X + $W + 2000), ($Y + $D), $E); level_id = $level; type_id = $wallType.element_id; height = 3000 }) 'wall-out' }
        $door = $null; $window = $null
        if ($walls['s'] -and $doorType) { $door = Create @(@{ kind = 'family_instance'; type_id = $doorType.element_id; host_id = $walls['s']; point = @(($X + 1500), $Y, $E); coordinate_mode = 'absolute'; level_id = $level }) 'door' }
        if ($walls['n'] -and $windowType) { $window = Create @(@{ kind = 'family_instance'; type_id = $windowType.element_id; host_id = $walls['n']; point = @(($X + 4200), ($Y + $D), ($E + 900)); coordinate_mode = 'absolute'; level_id = $level }) 'window' }
        $floor = $null
        if ($level -and $floorType) {
            $floor = Create @(@{ kind = 'floor'; level_id = $level; type_id = $floorType.element_id
                    profile = @(, @(@(($X + 500), ($Y + 500), $E), @(($X + $W - 500), ($Y + 500), $E), @(($X + $W - 500), ($Y + $D - 500), $E), @(($X + 500), ($Y + $D - 500), $E))) }) 'floor'
        }
        $room = $null; $space = $null
        $closed = $walls['s'] -and $walls['e'] -and $walls['n'] -and $walls['w']
        if ($closed) {
            $room = Create @(@{ kind = 'room'; point = @(($X + 3000), ($Y + 2000)); level_id = $level; name = "HZ_RM_ROOM_$run"; number = "HZRM$run" }) 'room'
            $space = Create @(@{ kind = 'space'; point = @(($X + 1000), ($Y + 1000)); level_id = $level }) 'space'
        }
        $why = "staging incomplete: walls $($walls.Count)/4 closed=$([bool]$closed), outside wall $outside, door $door, window $window, floor $floor, room $room, space $space"

        # The phase is never assumed: a bogus name makes the tool list the document's phases,
        # and the phase is the one in which room_finishes measures THIS room.
        $phase = $null; $rf = $null
        if ($room) {
            $ph = & $Ctx.Call $Q @{ mode = 'room_finishes'; element_ids = @($room); phase = "HZ_NO_SUCH_PHASE_$run" }
            $names = @()
            if ($ph.isError -and ([string]$ph.text) -match 'Phases in this document: ([^.]*)\.') { $names = @($Matches[1] -split ', ') }
            [array]::Reverse($names)
            foreach ($n in $names) {
                $try = & $Ctx.Call $Q @{ mode = 'room_finishes'; element_ids = @($room); phase = $n }
                if (-not $try.isError -and $try.data -and @($try.data.rows | Where-Object { [string]$_.room_id -eq [string]$room }).Count -gt 0) { $phase = $n; $rf = $try; break }
            }
            if (-not $phase) { $why = "the room $room was measured in no phase of: $($names -join ', ')" }
        }

        # ==== 1: room_finishes, gross beside deduction =====================================
        if (-not ($rf -and $door -and $window -and $floor)) { Case $catalog[0] $Q 'not_covered' $why }
        else {
            $rows = @($rf.data.rows | Where-Object { [string]$_.room_id -eq [string]$room })
            $wallRows = @($rows | Where-Object { $_.surface -eq 'wall' })
            $gross = 0.0; $deduct = 0.0; $ids = @(); $bases = @()
            foreach ($r in $wallRows) { $gross += [double]$r.gross_m2; $deduct += [double]$r.openings_deduction_m2; $ids += @($r.opening_ids | ForEach-Object { [string]$_ }); $bases += @($r.opening_size_basis) }
            $floorRow = @($rows | Where-Object { $_.surface -eq 'floor' -and (@($_.bounding_element_keys | ForEach-Object { [string]$_ }) -contains ('host:' + $floor)) }) | Select-Object -First 1
            $problems = @()
            if (-not ($gross -gt 0)) { $problems += 'no gross wall face' }
            if ($ids -notcontains [string]$door) { $problems += "door $door not among the deducted openings" }
            if ($ids -notcontains [string]$window) { $problems += "window $window not among the deducted openings" }
            # 0.915 x 2.134 + 0.915 x 1.220 = 3.069 m2 nominal; rough sizes may be larger.
            if (-not ($deduct -ge 2.9 -and $deduct -le 4.5)) { $problems += "deduction $deduct m2 outside 2.9..4.5" }
            if (-not ($gross -gt $deduct)) { $problems += 'gross is not above the deduction' }
            if (@($bases).Count -eq 0) { $problems += 'no opening_size_basis named' }
            $listed = @($rf.data.openings | ForEach-Object { [string]$_.insert_id })
            if ($listed -notcontains [string]$door -or $listed -notcontains [string]$window) { $problems += "door/window not both in the per-opening list: $($listed -join ',')" }
            if (-not $floorRow) { $problems += "no floor row bounded by the own floor $floor" }
            $detail = "phase '$phase': wall gross $([math]::Round($gross, 3)) m2, deduction $([math]::Round($deduct, 3)) m2 ($($bases -join ',')), floor row $([bool]$floorRow)"
            if ($problems.Count -eq 0) { Case $catalog[0] $Q 'pass' $detail } else { Case $catalog[0] $Q 'fail' (($problems -join '; ') + ' | ' + $detail) }
        }

        # ==== 2: no phase ===================================================================
        if (-not $room) { Case $catalog[1] $Q 'not_covered' $why }
        else {
            $np = & $Ctx.Call $Q @{ mode = 'room_finishes'; element_ids = @($room) }
            if ($np.isError -and ([string]$np.text) -match 'phase is required') { Case $catalog[1] $Q 'pass' 'refused: phase is required' }
            else { Case $catalog[1] $Q 'fail' ('not refused: ' + (Short $np)) }
        }

        # ==== 3 and 4: carbon ===============================================================
        $carbonIds = @(@($walls.Values) + @($floor) | Where-Object { $_ })
        $matA = $null; $matB = $null
        if (-not ($floor -and $closed)) { Case $catalog[2] $Q 'not_covered' $why; Case $catalog[3] $Q 'not_covered' $why }
        else {
            $c0 = & $Ctx.Call $Q @{ mode = 'carbon'; element_ids = $carbonIds; factor_source = "probe ${run}: a factor no material matches"
                    carbon_factors = @(@{ material = "HZ_NO_SUCH_MATERIAL_$run"; factor = 1; per = 'm3' }) }
            $without = @()
            if (-not $c0.isError -and $c0.data) { $without = @($c0.data.materials_without_factor | Where-Object { $_ }) }
            if ($c0.isError -or -not $c0.data) { Case $catalog[2] $Q 'fail' ('carbon: ' + (Short $c0)) }
            elseif ([double]$c0.data.kgco2e_counted_total -ne 0 -or [int]$c0.data.coverage.counted -ne 0 -or $without.Count -lt 1) {
                Case $catalog[2] $Q 'fail' "counted $($c0.data.coverage.counted), total $($c0.data.kgco2e_counted_total), without factor: $($without -join ', ')"
            }
            else { Case $catalog[2] $Q 'pass' "nothing counted; named without a factor: $($without -join ', ')" }

            if ($without.Count -lt 2) { Case $catalog[3] $Q 'not_covered' "the staged walls and floor carry $($without.Count) named material(s); two are needed" }
            else {
                $matA = [string]$without[0]; $matB = [string]$without[1]
                $c1 = & $Ctx.Call $Q @{ mode = 'carbon'; element_ids = $carbonIds; factor_source = "probe ${run}: two invented factors"
                        carbon_factors = @(@{ material = $matA; factor = 100; per = 'm3' }, @{ material = $matB; factor = 2; per = 'kg' }) }
                if ($c1.isError -or -not $c1.data) { Case $catalog[3] $Q 'fail' ('carbon: ' + (Short $c1)) }
                else {
                    $problems = @()
                    $rowsA = @($c1.data.rows | Where-Object { [string]$_.material -eq $matA })
                    $rowsB = @($c1.data.rows | Where-Object { [string]$_.material -eq $matB })
                    foreach ($r in $rowsA) {
                        if (-not ([int]$r.counted -gt 0)) { $problems += "$matA row not counted" }
                        elseif (-not (Near ([double]$r.kgco2e) ([double]$r.volume_m3 * 100) 0.01)) { $problems += "$matA kgco2e $($r.kgco2e) != volume $($r.volume_m3) x 100" }
                    }
                    foreach ($r in $rowsB) {
                        if ($null -ne $r.mass_kg -and [int]$r.no_density -eq 0) {
                            if (-not (Near ([double]$r.kgco2e) ([double]$r.mass_kg * 2) 0.01)) { $problems += "$matB kgco2e $($r.kgco2e) != mass $($r.mass_kg) x 2" }
                        }
                        elseif (@($c1.data.materials_without_density) -notcontains $matB) { $problems += "$matB has no density and is not named in materials_without_density" }
                    }
                    if ($rowsA.Count -eq 0 -or $rowsB.Count -eq 0) { $problems += "rows: $matA $($rowsA.Count), $matB $($rowsB.Count)" }
                    $still = @($c1.data.materials_without_factor)
                    if ($still -contains $matA -or $still -contains $matB) { $problems += 'a tabled material is still named without a factor' }
                    if (@($without | Where-Object { $_ -ne $matA -and $_ -ne $matB } | Where-Object { $still -notcontains $_ }).Count -gt 0) { $problems += 'an untabled material vanished from materials_without_factor' }
                    $detail = "$matA per m3 x100, $matB per kg x2; counted total $($c1.data.kgco2e_counted_total) kgCO2e; without density: $(@($c1.data.materials_without_density) -join ', ')"
                    if ($problems.Count -eq 0) { Case $catalog[3] $Q 'pass' $detail } else { Case $catalog[3] $Q 'fail' (($problems -join '; ') + ' | ' + $detail) }
                }
            }
        }

        # ==== 5 and 6: query_model include_room =============================================
        if (-not ($phase -and $floor -and $space -and $outside)) { Case $catalog[4] $QM 'not_covered' $why }
        else {
            $ids = @(@($floor, $space, $outside) + @($walls.Values) | Where-Object { $_ })
            $m = & $Ctx.Call $QM @{ element_ids = $ids; include_links = $false; include_room = $true; phase = $phase; max_rows = 50 }
            if ($m.isError -or -not $m.data) { Case $catalog[4] $QM 'fail' ('query: ' + (Short $m)) }
            else {
                function RowOf($id) { @($m.data.rows | Where-Object { [string]$_.element_id -eq [string]$id }) | Select-Object -First 1 }
                $problems = @()
                $f = RowOf $floor; $s = RowOf $space; $o = RowOf $outside
                if (-not ($f -and $f.room.state -eq 'assigned' -and [string]$f.room.room.id -eq [string]$room -and $f.room.basis -eq 'floor_top_face')) { $problems += "floor: $($f.room | ConvertTo-Json -Compress -Depth 4)" }
                if (-not ($s -and $s.room.state -eq 'assigned' -and [string]$s.room.room.id -eq [string]$room -and $s.room.basis -eq 'location_point')) { $problems += "space: $($s.room | ConvertTo-Json -Compress -Depth 4)" }
                if (-not ($o -and $o.room.state -eq 'unassigned' -and $o.room.basis -eq 'wall_solid_centroid')) { $problems += "outside wall: $($o.room | ConvertTo-Json -Compress -Depth 4)" }
                $bounding = @($walls.Values | ForEach-Object { $r = RowOf $_; if ($r) { [string]$r.room.state } else { 'missing' } })
                $boundary = [string]$m.data.room_membership.room_boundary_location
                # By design a room-bounding wall is unassigned when rooms are computed at the finish.
                if ($boundary -eq 'Finish' -and @($bounding | Where-Object { $_ -ne 'unassigned' }).Count -gt 0) { $problems += "bounding walls at Finish: $($bounding -join ',')" }
                $detail = "boundary $boundary; bounding walls $($bounding -join ','); assigned $($m.data.room_membership.assigned), unassigned $($m.data.room_membership.unassigned), unlocatable $($m.data.room_membership.unlocatable)"
                if ($problems.Count -eq 0) { Case $catalog[4] $QM 'pass' $detail } else { Case $catalog[4] $QM 'fail' (($problems -join '; ') + ' | ' + $detail) }
            }
        }
        if (-not $floor) { Case $catalog[5] $QM 'not_covered' $why }
        else {
            $np = & $Ctx.Call $QM @{ element_ids = @($floor); include_links = $false; include_room = $true }
            if ($np.isError -and ([string]$np.text) -match 'phase is required') { Case $catalog[5] $QM 'pass' 'refused: phase is required' }
            else { Case $catalog[5] $QM 'fail' ('not refused: ' + (Short $np)) }
        }

        # ==== 7: takeoff group_by room =====================================================
        if (-not ($phase -and $floor -and $outside)) { Case $catalog[6] $Q 'not_covered' $why }
        else {
            $t = & $Ctx.Call $Q @{ mode = 'takeoff'; element_ids = @($floor, $outside); classification_parameter = 'Comments'
                    quantities = @(@{ name = 'n'; source = 'count'; unit = 'ea' }); group_by = 'room'; phase = $phase }
            if ($t.isError -or -not $t.data -or -not $t.data.by_room) { Case $catalog[6] $Q 'fail' ('takeoff: ' + (Short $t)) }
            else {
                $inRoom = $t.data.by_room.PSObject.Properties[[string]$room]
                $unassigned = $t.data.by_room.PSObject.Properties['(unassigned)']
                $keys = @($t.data.by_room.PSObject.Properties | ForEach-Object { $_.Name + '=' + $_.Value.elements })
                if ($inRoom -and [int]$inRoom.Value.elements -eq 1 -and $unassigned -and [int]$unassigned.Value.elements -eq 1) { Case $catalog[6] $Q 'pass' ("by_room " + ($keys -join ', ')) }
                else { Case $catalog[6] $Q 'fail' ("by_room " + ($keys -join ', ') + "; expected $room=1 and (unassigned)=1") }
            }
        }

        # ==== 8: unenclosed room ===========================================================
        if (-not ($phase -and $walls['w'])) { Case $catalog[7] $Q 'not_covered' $why }
        else {
            $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = @([long]$walls['w']) } ($run + '-rm-open')
            if ($del.stage -ne 'apply' -or $del.answer.isError) { Case $catalog[7] $Q 'fail' ('deleting the west wall: ' + (Short $del.answer)) }
            else {
                [void]$created.Remove([long]$walls['w'])
                $u = & $Ctx.Call $Q @{ mode = 'room_finishes'; element_ids = @($room); phase = $phase }
                $nm = if ($u.data) { @($u.data.not_measured | Where-Object { [string]$_.id -eq [string]$room }) | Select-Object -First 1 } else { $null }
                $rowsLeft = if ($u.data) { @($u.data.rows | Where-Object { [string]$_.room_id -eq [string]$room }).Count } else { -1 }
                if (-not $u.isError -and $nm -and $nm.state -eq 'not_enclosed' -and $rowsLeft -eq 0) { Case $catalog[7] $Q 'pass' ("named: " + $nm.reason) }
                else { Case $catalog[7] $Q 'fail' ("state $($nm.state), rows $rowsLeft | " + (Short $u)) }
            }
        }

        # ==== 10: carbon refuses a phase ====================================================
        # Carbon does not filter by phase; a phase it silently ignored would read as "the carbon
        # of that phase". Run before the cleanup so the ids are real; the refusal comes first anyway.
        $cp = & $Ctx.Call $Q @{ mode = 'carbon'; element_ids = @($carbonIds); phase = $(if ($phase) { $phase } else { 'New Construction' })
                factor_source = "probe ${run}: refusal"; carbon_factors = @(@{ material = "HZ_NO_SUCH_MATERIAL_$run"; factor = 1; per = 'm3' }) }
        if ($cp.isError -and ([string]$cp.text) -match "'phase' is not read in mode 'carbon'") { Case $catalog[9] $Q 'pass' 'refused by name' }
        else { Case $catalog[9] $Q 'fail' ('not refused: ' + (Short $cp)) }

        # ==== 9: cleanup ===================================================================
        if ($created.Count -eq 0) { Case $catalog[8] $DeleteTool 'not_covered' 'nothing was created' }
        else {
            $ids = @($created | ForEach-Object { [long]$_ })
            [array]::Reverse($ids)   # inserts, room and space before the walls and the level
            $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-rm-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[8] $DeleteTool 'pass' "$($ids.Count) id(s) deleted" }
            else { Case $catalog[8] $DeleteTool 'fail' ('cleanup: ' + (Short $del.answer)) }
        }
        return $cases
    }
}
