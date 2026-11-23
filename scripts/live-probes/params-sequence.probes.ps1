# Live probes for horizun_write_params_verified 'sequence' (values GENERATED in a
# declared spatial order: level, x, y, room at a phase; prefix/start/step/pad and
# restart_per_level). Stages its OWN elements far from the model at X = 1,120,000 mm
# (this branch's slot): two own levels, a box of four walls on the first and two
# walls on the second, one room inside the box, and one door in the box's bottom
# wall. Types are taken BY NAME from the document ('Generic - 200mm', 'M_Single-Flush:
# 0915 x 2134mm'); a document without them leaves those cases not_covered.
# The walls and the room write Comments (ALL_MODEL_INSTANCE_COMMENTS), because
# restart_per_level repeats values by design and a repeated Mark would add Revit's
# duplicate-mark warning to what is being measured; the door writes its MARK with a
# prefix no other element carries, so the FamilyInstance room path (ToRoom/FromRoom
# at a phase) is measured on the parameter doors are numbered in. Nothing is saved;
# every staged element is deleted at the end. Not staged: a family instance WITHOUT
# its own level (the host-level fallback), which no template door reaches.
#
# Reply shapes (data.sequence.order[] with target_id/value/level/room/room_from,
# repeats_across_levels, confirmation_token on the rehearsal; rows[] with target_id
# as a string, confirmed and value_read_back, verification.verified on the apply;
# the phases list of horizun_manage_phases) are from the code
# (WriteParamsSequence.cs / WriteParamsCommand.cs), to be held against the first live run.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'params-sequence'
    Catalog = @(
        @{ Name = 'sequence: a rehearsal numbers own walls by level, x and y, restarting per level, and binds a token'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'sequence: the apply writes every generated value and each one re-reads'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'sequence: order_by room without phase_id is refused by name before anything is generated'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'sequence: an own room is numbered in room order at a phase it exists in, and re-reads'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'sequence: an own door takes its room from to_room/from_room at the phase and its Mark is numbered and re-read'; Tool = 'horizun_write_params_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $WP = 'horizun_write_params_verified'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $names = @('sequence: a rehearsal numbers own walls by level, x and y, restarting per level, and binds a token',
                   'sequence: the apply writes every generated value and each one re-reads',
                   'sequence: order_by room without phase_id is refused by name before anything is generated',
                   'sequence: an own room is numbered in room order at a phase it exists in, and re-reads',
                   'sequence: an own door takes its room from to_room/from_room at the phase and its Mark is numbered and re-read')
        function AllNotCovered($why) { foreach ($n in $names) { Case $n $WP 'not_covered' $why } }
        if ($Ctx.WriteGate) { AllNotCovered 'write tier is not open for this run'; return $cases.ToArray() }

        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.Generic.List[long]
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-sq-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { $created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }
        function Cleanup {
            if ($created.Count -eq 0) { return }
            $ids = $created.ToArray(); [array]::Reverse($ids)
            $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($ids); id_cap = 500 } ($run + '-sq-cleanup')
        }
        # A read-back is accepted when its JSON carries the expected text as a whole
        # string value - independent of how value_read_back nests it.
        function ReadsBack($row, $want) { ($row.value_read_back | ConvertTo-Json -Compress -Depth 6) -match ('"' + [regex]::Escape($want) + '"') }

        # ---- staging (mm): own levels, a wall type by name, walls, one room ----
        $X = 1120000.0; $Y = 0.0; $Wd = 6000.0; $Dp = 4000.0; $Ea = 98000.0; $Eb = 102000.0
        $levelA = Create @{ kind = 'level'; name = "HZ_SQA_$run"; elevation = $Ea } 'level-a'
        $levelB = Create @{ kind = 'level'; name = "HZ_SQB_$run"; elevation = $Eb } 'level-b'
        $wallName = 'Generic - 200mm'
        $wallType = @(Types 'OST_Walls' | Where-Object { $_.type -eq $wallName }) | Select-Object -First 1
        $wallsA = @(); $wallsB = @()
        if ($levelA -and $levelB -and $wallType) {
            $corners = @(@($X, $Y), @(($X + $Wd), $Y), @(($X + $Wd), ($Y + $Dp)), @($X, ($Y + $Dp)))
            for ($k = 0; $k -lt 4; $k++) {
                $a = $corners[$k]; $b = $corners[($k + 1) % 4]
                $wallsA += Create @{ kind = 'wall'; start = @($a[0], $a[1], $Ea); end = @($b[0], $b[1], $Ea); height = 3000
                                     level_id = $levelA; type_id = $wallType.element_id } "wall-a$k"
            }
            foreach ($dx in 0.0, 3000.0) {
                $wallsB += Create @{ kind = 'wall'; start = @(($X + $dx), $Y, $Eb); end = @(($X + $dx), ($Y + $Dp), $Eb); height = 3000
                                     level_id = $levelB; type_id = $wallType.element_id } "wall-b$dx"
            }
        }
        $roomId = if (@($wallsA | Where-Object { $_ }).Count -eq 4) { Create @{ kind = 'room'; point = @(($X + $Wd / 2), ($Y + $Dp / 2)); level_id = $levelA } 'room' } else { $null }
        if (@($wallsA | Where-Object { $_ }).Count -ne 4 -or @($wallsB | Where-Object { $_ }).Count -ne 2) {
            AllNotCovered ("own levels/walls could not be staged (levels=$levelA,$levelB type=" + [string]$wallType.element_id + " walls=" + (@($wallsA + $wallsB) -join ',') + ')')
            Cleanup; return $cases.ToArray()
        }

        # ---- 1: rehearsal. Box wall midpoints: left (X) 01, bottom (X+W/2, Y) 02, top
        # (X+W/2, Y+D) 03, right (X+W) 04 on level A; on level B the counter restarts.
        $tag = "HZSQ$run-"
        $seq = @{ parameter = 'ALL_MODEL_INSTANCE_COMMENTS'; element_ids = @($wallsA + $wallsB); order_by = @('level', 'x', 'y')
                  prefix = $tag; pad = 2; restart_per_level = $true }
        $want = @{}
        $want[[string]$wallsA[3]] = "${tag}01"; $want[[string]$wallsA[0]] = "${tag}02"; $want[[string]$wallsA[2]] = "${tag}03"
        $want[[string]$wallsA[1]] = "${tag}04"; $want[[string]$wallsB[0]] = "${tag}01"; $want[[string]$wallsB[1]] = "${tag}02"
        $dry = & $Ctx.Call $WP @{ target_document = $doc; dry_run = $true; sequence = $seq }
        $order = if ($dry.data -and $dry.data.sequence) { @($dry.data.sequence.order) } else { @() }
        $wrong = @($order | Where-Object { $want[[string]$_.target_id] -cne [string]$_.value })
        if (-not $dry.isError -and $dry.data.confirmation_token -and $order.Count -eq 6 -and $wrong.Count -eq 0 -and $dry.data.sequence.repeats_across_levels -eq $true) {
            Case $names[0] $WP 'pass' ('order: ' + (@($order | ForEach-Object { "$($_.target_id)=$($_.value)@$($_.level)" }) -join ', '))
        } else { Case $names[0] $WP 'fail' ('sequence=' + ($dry.data.sequence | ConvertTo-Json -Compress -Depth 6) + ' text=' + [string]$dry.text) }

        # ---- 2: apply, then every row confirmed and read back as generated.
        $ap = & $Ctx.Apply $WP @{ target_document = $doc; sequence = $seq } ($run + '-sq-apply')
        if (Applied $ap) {
            $rows = @($ap.answer.data.rows)
            $bad = @($rows | Where-Object { $_.confirmed -ne $true -or -not (ReadsBack $_ $want[[string]$_.target_id]) })
            if ($ap.answer.data.verification.verified -eq $true -and $rows.Count -eq 6 -and $bad.Count -eq 0) {
                Case $names[1] $WP 'pass' ('6 rows confirmed and read back')
            } else { Case $names[1] $WP 'fail' ('rows=' + ($rows | ConvertTo-Json -Compress -Depth 6)) }
        } else { Case $names[1] $WP 'fail' (Why $ap) }

        # ---- 3: room order without a phase is refused, by name, with no token.
        $rf = & $Ctx.Call $WP @{ target_document = $doc; dry_run = $true; sequence = @{ parameter = 'ALL_MODEL_INSTANCE_COMMENTS'; element_ids = @($wallsB); order_by = @('room') } }
        $said = [string]$rf.text
        if ($rf.isError -and $said -match 'phase_id' -and -not $rf.data.confirmation_token) {
            Case $names[2] $WP 'pass' ('refused: ' + $said.Substring(0, [Math]::Min(200, $said.Length)))
        } else { Case $names[2] $WP 'fail' ('not refused by name: ' + $said) }

        # ---- 4: the own room, numbered in room order at each phase until one holds it.
        if (-not $roomId) { Case $names[3] $WP 'not_covered' 'the own room could not be placed inside the box' }
        else {
            $ph = & $Ctx.Call 'horizun_manage_phases' @{ operation = 'list'; target_document = $doc }
            $phases = if ($ph.data) { @($ph.data.phases) } else { @() }
            $seen = @(); $held = $null
            foreach ($p in $phases) {
                $rs = @{ parameter = 'ALL_MODEL_INSTANCE_COMMENTS'; element_ids = @($roomId); order_by = @('room'); phase_id = [long]$p.id; prefix = "${tag}R" }
                $d = & $Ctx.Call $WP @{ target_document = $doc; dry_run = $true; sequence = $rs }
                $o = if ($d.data -and $d.data.sequence) { @($d.data.sequence.order) | Select-Object -First 1 } else { $null }
                if ($o -and $o.room -and $d.data.confirmation_token) { $held = @{ phase = $p; seq = $rs; row = $o }; break }
                $seen += ("$($p.name): " + $(if ($d.isError) { [string]$d.text } else { 'no room' }))
            }
            if (-not $held) {
                $outcome = if ($phases.Count -eq 0) { 'not_covered' } else { 'fail' }
                Case $names[3] $WP $outcome ('no phase numbered the own room: ' + ($seen -join ' | ') + ' ' + [string]$ph.text)
            } else {
                $ra = & $Ctx.Apply $WP @{ target_document = $doc; sequence = $held.seq } ($run + '-sq-apply-room')
                $row = if (Applied $ra) { @($ra.answer.data.rows) | Select-Object -First 1 } else { $null }
                if ($row -and $row.confirmed -eq $true -and (ReadsBack $row "${tag}R1") -and $ra.answer.data.verification.verified -eq $true) {
                    Case $names[3] $WP 'pass' ("phase '$($held.phase.name)': room $($held.row.room) via $($held.row.room_from) -> ${tag}R1 re-read")
                } else { Case $names[3] $WP 'fail' ('room apply: ' + $(if ($row) { $row | ConvertTo-Json -Compress -Depth 6 } else { Why $ra })) }
            }
        }

        # ---- 5: an own door in the box's bottom wall, numbered by level, room and x at the
        # phase that held the room. Its room must come from ToRoom/FromRoom (the door path,
        # not a point lookup), be the own room, and its MARK must re-read as generated.
        if (-not $held) { Case $names[4] $WP 'not_covered' 'no phase held the own room (case 4), so the door has no room to take' }
        else {
            function DoorType { @(Types 'OST_Doors' | Where-Object { $_.family -eq 'M_Single-Flush' -and $_.type -eq '0915 x 2134mm' }) | Select-Object -First 1 }
            $doorType = DoorType
            $doorId = if ($doorType) { Create @{ kind = 'family_instance'; type_id = [long]$doorType.element_id; host_id = [long]$wallsA[0]; point = @(($X + 1500), $Y, $Ea); coordinate_mode = 'absolute'; level_id = $levelA } 'door' } else { $null }
            if (-not $doorId) { Case $names[4] $WP 'not_covered' ('no own door could be placed (door type=' + [string]$doorType.element_id + ')') }
            else {
                $ds = @{ parameter = 'ALL_MODEL_MARK'; element_ids = @($doorId); order_by = @('level', 'room', 'x'); phase_id = [long]$held.phase.id; prefix = "${tag}D" }
                $dd = & $Ctx.Call $WP @{ target_document = $doc; dry_run = $true; sequence = $ds }
                $o = if ($dd.data -and $dd.data.sequence) { @($dd.data.sequence.order) | Select-Object -First 1 } else { $null }
                if (-not $o -or -not $dd.data.confirmation_token -or @('to_room', 'from_room') -notcontains [string]$o.room_from -or [string]$o.room -ne [string]$held.row.room) {
                    Case $names[4] $WP 'fail' ('door rehearsal (want the own room ' + [string]$held.row.room + ' via to_room/from_room): ' + $(if ($o) { $o | ConvertTo-Json -Compress -Depth 6 } else { [string]$dd.text }))
                } else {
                    $da = & $Ctx.Apply $WP @{ target_document = $doc; sequence = $ds } ($run + '-sq-apply-door')
                    $row = if (Applied $da) { @($da.answer.data.rows) | Select-Object -First 1 } else { $null }
                    if ($row -and $row.confirmed -eq $true -and (ReadsBack $row "${tag}D1") -and $da.answer.data.verification.verified -eq $true) {
                        Case $names[4] $WP 'pass' ("door $doorId in room $($o.room) via $($o.room_from) -> Mark ${tag}D1 re-read")
                    } else { Case $names[4] $WP 'fail' ('door apply: ' + $(if ($row) { $row | ConvertTo-Json -Compress -Depth 6 } else { Why $da })) }
                }
            }
        }

        Cleanup
        return $cases.ToArray()
    }
}
