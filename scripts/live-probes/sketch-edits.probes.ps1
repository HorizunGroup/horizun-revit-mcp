# Live probes for horizun_transform_elements operation=edit_sketch (TransformElementsCommand.
# EditSketch.cs). Everything stands on the module's own level at X = 1,170,000 mm (H, the
# eighth letter, takes slot 7 of the 1,1x0,000 band), far from the real model: an own floor,
# an own ceiling and an own footprint roof, their types taken BY NAME from the document -
# never the first type of a category. The profiles
# sit at the level's own elevation (offset 0), so the sketch plane is there whichever face
# Revit sketches on; should a plane still lie elsewhere, the tool names it in its off-plane
# refusal and the edit is re-sent on it once, said in the case detail (a measurement, not a
# guess).
#
# Every apply is held to the area the PROBE computes for what it asked (floor 24 -> 20 m2 by
# a replaced loop, then -2 m2 by one corner moved 1000 mm along a 4000 mm edge; ceiling
# 12 -> 9 m2 by an L), not only to the tool's own expected_area_m2, and to the id twice: the
# tool's re-read (unique_id_kept, loops_verified, area_check) and an independent
# horizun_query_model read of the same id in the same category after the commit. The
# rehearsal case sends two dry runs: the second must still read the ORIGINAL boundary, the
# only outside proof that the first was cancelled. FootPrintRoof is refused by name (no
# SketchId in 2023-2027). Everything staged is deleted with horizun_delete_verified
# mode='ids'; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'sketch-edits'
    Catalog = @(
        @{ Name = 'edit_sketch floor rehearsal: the curve edits are made in the SketchEditScope and cancelled - a second rehearsal still reads the original boundary'; Tool = 'horizun_transform_elements' }
        @{ Name = 'edit_sketch floor replace_loop: same id, the loop re-read as sent, Area 24 -> 20 m2'; Tool = 'horizun_transform_elements' }
        @{ Name = 'edit_sketch floor move_vertex: same id, one corner moved 1000 mm, Area down by 2 m2'; Tool = 'horizun_transform_elements' }
        @{ Name = 'edit_sketch ceiling replace_loop: same id, the rectangle becomes an L, Area 12 -> 9 m2'; Tool = 'horizun_transform_elements' }
        @{ Name = 'edit_sketch footprint roof: refused by name (no SketchId), no token issued'; Tool = 'horizun_transform_elements' }
        @{ Name = 'edit_sketch probes: everything staged is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'edit_sketch floor rehearsal: the curve edits are made in the SketchEditScope and cancelled - a second rehearsal still reads the original boundary',
            'edit_sketch floor replace_loop: same id, the loop re-read as sent, Area 24 -> 20 m2',
            'edit_sketch floor move_vertex: same id, one corner moved 1000 mm, Area down by 2 m2',
            'edit_sketch ceiling replace_loop: same id, the rectangle becomes an L, Area 12 -> 9 m2',
            'edit_sketch footprint roof: refused by name (no SketchId), no token issued',
            'edit_sketch probes: everything staged is deleted')
        $T = 'horizun_transform_elements'; $DeleteTool = 'horizun_delete_verified'
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { $tool = $T; if ($catalog[$i] -like 'edit_sketch probes:*') { $tool = $DeleteTool }; Case $catalog[$i] $tool 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $s = [string]$a.text; if ($s.Length -gt 400) { $s.Substring(0, 400) } else { $s } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-sk-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }
        # Candidates in order, as 'Family: Type': the first the document carries. These are the
        # Autodesk template's names, NOT measured here - the first live run confirms them (a miss
        # is a named not_covered, never a silent fallback to another type).
        function Bring($category, $names) {
            $have = @(Types $category)
            foreach ($n in $names) {
                $fam, $typ = $n -split ': ', 2
                $hit = @($have | Where-Object { [string]$_.family -eq $fam -and [string]$_.type -eq $typ }) | Select-Object -First 1
                if ($hit) { return $hit }
            }
            return $null
        }
        # [x, y] pairs in model mm -> [x, y, z] rows, returned whole (the comma keeps PowerShell
        # from unrolling the array of points into loose points).
        function At($pts, $z) { $out = New-Object System.Collections.ArrayList; foreach ($p in $pts) { [void]$out.Add(@([double]$p[0], [double]$p[1], [double]$z)) }; return , $out.ToArray() }
        function Box($ox, $oy, $w, $h) { return , @(@($ox, $oy), @(($ox + $w), $oy), @(($ox + $w), ($oy + $h)), @($ox, ($oy + $h))) }
        function Near($a, $b, $tol) { ($null -ne $a) -and ([math]::Abs([double]$a - [double]$b) -le $tol) }
        function Plan($reply) { if ($reply -and $reply.data -and $reply.data.plan) { return @($reply.data.plan)[0] }; return $null }

        # ---- staging: own level, own floor, own ceiling, own footprint roof ----------------
        $X0 = 1170000.0; $Y0 = 0.0; $E = 117000.0
        $floorNames = @('Floor: Generic 150mm', 'Floor: Generic 300mm')
        $ceilingNames = @('Compound Ceiling: 600 x 600mm Grid', 'Basic Ceiling: Generic')
        $roofNames = @('Basic Roof: Generic - 125mm', 'Basic Roof: Generic - 400mm')
        $floorType = Bring 'OST_Floors' $floorNames
        $ceilingType = Bring 'OST_Ceilings' $ceilingNames
        $roofType = Bring 'OST_Roofs' $roofNames
        $level = Create @(@{ kind = 'level'; name = "HZ_SK_$run"; elevation = $E }) 'level'
        $floor = $null; $ceiling = $null; $roof = $null
        if ($level -and $floorType) { $floor = Create @(@{ kind = 'floor'; level_id = $level; type_id = $floorType.element_id; profile = @(, (At (Box $X0 $Y0 6000 4000) $E)) }) 'floor' }
        if ($level -and $ceilingType) { $ceiling = Create @(@{ kind = 'ceiling'; level_id = $level; type_id = $ceilingType.element_id; profile = @(, (At (Box $X0 ($Y0 + 10000) 4000 3000) $E)) }) 'ceiling' }
        if ($level -and $roofType) { $roof = Create @(@{ kind = 'roof'; level_id = $level; type_id = $roofType.element_id; profile = @(, (At (Box $X0 ($Y0 + 20000) 3000 3000) $E)) }) 'roof' }
        $floorWhy = "staging incomplete: level $level, floor $floor (floor type staged by name: $([bool]$floorType); tried " + ($floorNames -join ', ') + ')'
        $ceilingWhy = "staging incomplete: level $level, ceiling $ceiling (ceiling type staged by name: $([bool]$ceilingType); tried " + ($ceilingNames -join ', ') + ')'
        $roofWhy = "staging incomplete: level $level, roof $roof (roof type staged by name: $([bool]$roofType); tried " + ($roofNames -join ', ') + ')'

        # One edit on one element at plane height z, sent alone as the operation requires.
        function Op($id, $edit, $z) {
            $o = @{ operation = 'edit_sketch'; element_ids = @($id) }
            if ($edit.loop) { $o.loop_index = 0; $o.loop = (At $edit.loop $z) }
            else { $o.start = @([double]$edit.start[0], [double]$edit.start[1], [double]$z); $o.end = @([double]$edit.end[0], [double]$edit.end[1], [double]$z) }
            return $o
        }
        function EditArgs($id, $edit, $z) { @{ target_document = $doc; units = 'mm'; operations = @(Op $id $edit $z) } }
        # A rehearsal at the level's height. An off-plane refusal names a point of the plane in
        # invariant numbers; the edit is re-sent at that height once and the case says so.
        function Rehearse($id, $edit) {
            $z = $E; $note = $null
            $r = & $Ctx.Call $T ((EditArgs $id $edit $z) + @{ dry_run = $true })
            if ($r.isError -and ([string]$r.text) -match 'plane passes through \((-?[0-9.]+), (-?[0-9.]+), (-?[0-9.]+)\) mm') {
                $z = [double]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture)
                $note = "the sketch plane of $id lies at z $z mm, not at the level's $E mm; the edit was sent there"
                $r = & $Ctx.Call $T ((EditArgs $id $edit $z) + @{ dry_run = $true })
            }
            return @{ reply = $r; z = $z; note = $note }
        }
        # The tool's own re-read, then an independent one: the same id, still in its category.
        function Judge($a, $id, $category, $askedArea, $mode) {
            $problems = @()
            $row = $null
            if ($a.answer.data -and $a.answer.data.rows) { $row = @($a.answer.data.rows)[0] }
            if ($a.stage -ne 'apply' -or $a.answer.isError -or -not $row) { return @('apply: ' + (Short $a.answer)) }
            $dp = Plan $a.dry
            if (-not $dp -or [string]$dp.mode -ne $mode) { $problems += "the rehearsal planned mode '$($dp.mode)', the probe sent $mode" }
            if ([string]$a.answer.data.transaction_status -ne 'Committed') { $problems += "transaction_status '$($a.answer.data.transaction_status)'" }
            if ([string]$a.answer.data.application.state -ne 'verified_applied') { $problems += "application.state '$($a.answer.data.application.state)', expected verified_applied" }
            if ([long]$row.element_id -ne [long]$id) { $problems += "the row names $($row.element_id), the edit was sent to $id" }
            if ($row.verified -ne $true) { $problems += 'the row is not verified' }
            if ($row.unique_id_kept -ne $true) { $problems += 'unique_id_kept is not true: the element was recreated or lost' }
            if ($row.loops_verified -ne $true) { $problems += "loops not verified: $($row.loops_problem)" }
            if ([string]$row.area_check -ne 'verified') { $problems += "area_check '$($row.area_check)' $($row.area_note)" }
            if (-not (Near $row.area_parameter_after_m2 $askedArea 0.01)) { $problems += "Area after $($row.area_parameter_after_m2) m2, the probe asked for $askedArea m2" }
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); element_ids = @($id); include_links = $false; max_rows = 5 }
            if (-not (@($q.data.rows) | Where-Object { [long]$_.element_id -eq [long]$id })) { $problems += "horizun_query_model finds no $category with id $id after the commit" }
            return $problems
        }
        function Tail($note) { if ($note) { '; ' + $note } else { '' } }

        # ==== 0: rehearsal, twice =========================================================
        $replace = @{ loop = (Box $X0 $Y0 5000 4000) }
        $floorZ = $E; $floorNote = $null
        if (-not $floor) { Case $catalog[0] $T 'not_covered' $floorWhy }
        else {
            $h = Rehearse $floor $replace; $floorZ = $h.z; $floorNote = $h.note
            $p1 = Plan $h.reply; $p2 = $null
            $problems = @()
            if ($h.reply.isError -or -not $p1) { $problems += 'rehearsal: ' + (Short $h.reply) }
            else {
                if ([string]$h.reply.data.transaction_status -ne 'rehearsed_and_cancelled') { $problems += "transaction_status '$($h.reply.data.transaction_status)', expected rehearsed_and_cancelled" }
                if ($p1.rehearsal_ok -ne $true) { $problems += "rehearsal_ok is not true: $($p1.rehearsal_error)" }
                if (-not $h.reply.data.confirmation_token) { $problems += 'no confirmation_token was issued' }
                if ([string]$p1.mode -ne 'replace_loop') { $problems += "mode '$($p1.mode)', expected replace_loop" }
                if (-not (Near $p1.area_parameter_before_m2 24 0.01)) { $problems += "Area before $($p1.area_parameter_before_m2) m2, staged 24" }
                if (-not (Near $p1.expected_area_m2 20 0.01)) { $problems += "expected_area_m2 $($p1.expected_area_m2), the probe asked for 20" }
                $again = & $Ctx.Call $T ((EditArgs $floor $replace $floorZ) + @{ dry_run = $true })
                $p2 = Plan $again
                if ($again.isError -or -not $p2) { $problems += 'second rehearsal: ' + (Short $again) }
                else {
                    $b1 = ConvertTo-Json -InputObject $p1.loops_before_mm -Compress -Depth 8
                    $b2 = ConvertTo-Json -InputObject $p2.loops_before_mm -Compress -Depth 8
                    if ($b1 -ne $b2 -or -not (Near $p2.area_parameter_before_m2 24 0.01)) { $problems += "the second rehearsal reads $($p2.area_parameter_before_m2) m2 and loops $b2 (the first read $b1): the first rehearsal was not cancelled" }
                }
            }
            if ($problems.Count -gt 0) { Case $catalog[0] $T 'fail' ($problems -join '; ') }
            else { Case $catalog[0] $T 'pass' ("rehearsed_and_cancelled with rehearsal_ok; the second rehearsal still reads $($p2.area_parameter_before_m2) m2 and the same loop" + (Tail $floorNote)) }
        }

        # ==== 1: replace the floor's loop ================================================
        if (-not $floor) { Case $catalog[1] $T 'not_covered' $floorWhy }
        else {
            $a = & $Ctx.Apply $T (EditArgs $floor $replace $floorZ) ($run + '-sk-floor-replace')
            $problems = @(Judge $a $floor 'OST_Floors' 20 'replace_loop')
            if ($problems.Count -gt 0) { Case $catalog[1] $T 'fail' ($problems -join '; ') }
            else { $row = @($a.answer.data.rows)[0]; Case $catalog[1] $T 'pass' ("id $floor kept (unique_id_kept; horizun_query_model reads it), loops re-read, Area $($row.area_parameter_before_m2) -> $($row.area_parameter_after_m2) m2" + (Tail $floorNote)) }
        }

        # ==== 2: move one corner of the floor ============================================
        # The corner at (X0, Y0 + 4000) exists before and after case 1; moved 1000 mm in +x it
        # cuts a 1000 x 4000 triangle off whatever the boundary is: 2 m2 less, by construction.
        $move = @{ start = @($X0, ($Y0 + 4000)); end = @(($X0 + 1000), ($Y0 + 4000)) }
        if (-not $floor) { Case $catalog[2] $T 'not_covered' $floorWhy }
        else {
            $m = & $Ctx.Apply $T (EditArgs $floor $move $floorZ) ($run + '-sk-floor-move')
            $dp = Plan $m.dry
            if (-not $dp -or $null -eq $dp.area_parameter_before_m2) { Case $catalog[2] $T 'fail' ('no Area was read before the move: ' + (Short $m.answer)) }
            else {
                $asked = [double]$dp.area_parameter_before_m2 - 2.0
                $problems = @(Judge $m $floor 'OST_Floors' $asked 'move_vertex')
                if ($null -eq $dp.vertex_index) { $problems += 'the rehearsal named no vertex_index' }
                if ($problems.Count -gt 0) { Case $catalog[2] $T 'fail' ($problems -join '; ') }
                else { $row = @($m.answer.data.rows)[0]; Case $catalog[2] $T 'pass' ("id $floor kept, vertex $($dp.vertex_index) moved, Area $($dp.area_parameter_before_m2) -> $($row.area_parameter_after_m2) m2") }
            }
        }

        # ==== 3: the ceiling's rectangle becomes an L =====================================
        $lShape = @{ loop = @(@($X0, ($Y0 + 10000)), @(($X0 + 4000), ($Y0 + 10000)), @(($X0 + 4000), ($Y0 + 11500)),
                              @(($X0 + 2000), ($Y0 + 11500)), @(($X0 + 2000), ($Y0 + 13000)), @($X0, ($Y0 + 13000))) }
        if (-not $ceiling) { Case $catalog[3] $T 'not_covered' $ceilingWhy }
        else {
            $h = Rehearse $ceiling $lShape
            $ca = & $Ctx.Apply $T (EditArgs $ceiling $lShape $h.z) ($run + '-sk-ceiling-replace')
            $problems = @(Judge $ca $ceiling 'OST_Ceilings' 9 'replace_loop')
            $dp = Plan $ca.dry
            if ($dp -and -not (Near $dp.area_parameter_before_m2 12 0.01)) { $problems += "Area before $($dp.area_parameter_before_m2) m2, staged 12" }
            if ($problems.Count -gt 0) { Case $catalog[3] $T 'fail' ($problems -join '; ') }
            else { $row = @($ca.answer.data.rows)[0]; Case $catalog[3] $T 'pass' ("id $ceiling kept, the L re-read, Area $($row.area_parameter_before_m2) -> $($row.area_parameter_after_m2) m2" + (Tail $h.note)) }
        }

        # ==== 4: a footprint roof is refused by name ======================================
        if (-not $roof) { Case $catalog[4] $T 'not_covered' $roofWhy }
        else {
            $rr = & $Ctx.Call $T ((EditArgs $roof @{ loop = (Box $X0 ($Y0 + 20000) 2000 2000) } $E) + @{ dry_run = $true })
            $said = [string]$rr.text
            if ($rr.isError -and $said -match 'FootPrintRoof' -and $said -match 'SketchId' -and -not ($rr.data -and $rr.data.confirmation_token)) { Case $catalog[4] $T 'pass' ('refused: ' + (Short $rr)) }
            else { Case $catalog[4] $T 'fail' ('expected a refusal naming FootPrintRoof and SketchId: ' + $(if ($rr.isError) { Short $rr } else { 'the rehearsal was accepted' })) }
        }

        # ==== 5: cleanup ==================================================================
        $ids = @($created | Sort-Object -Descending -Unique)
        if ($ids.Count -eq 0) { Case $catalog[5] $DeleteTool 'not_covered' 'nothing was created' }
        else {
            $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $ids } ($run + '-sk-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[5] $DeleteTool 'pass' "$($ids.Count) staged element(s) deleted" }
            else { Case $catalog[5] $DeleteTool 'fail' ('cleanup: ' + (Short $del.answer)) }
        }
        return $cases
    }
}
