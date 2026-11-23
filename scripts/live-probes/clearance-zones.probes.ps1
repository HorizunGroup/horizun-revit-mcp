# Live probe module: equipment maintenance/access clearance zones (horizun_verify_changes
# clearance_rules, see docs/TOOLS-EXTENDED.md). Stages, on an OWN level far from the
# fixture: a basic wall, a face-hosted panelboard on its +Y face (the document's own
# 'M_Lighting and Appliance Panelboard - 208V MLO: 100 A' type, by name), and an own
# architectural column standing in front of the panel (the document's own column type). verify_changes with a declared
# rule {OST_ElectricalEquipment, front, 900 mm} must return an ERROR naming the column;
# after the column is moved 5 m away the same call must come back without a clearance
# finding. A malformed rule is refused by index. Everything created is deleted.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'clearance-zones'
    Catalog = @(
        @{ Name = 'clearance: a column in front of a panelboard invades its declared 900 mm front zone (error naming the column)'; Tool = 'horizun_verify_changes' }
        @{ Name = 'clearance: the column moved away leaves the same zone clean';                                                  Tool = 'horizun_verify_changes' }
        @{ Name = 'clearance: a malformed clearance rule is refused naming its index';                                            Tool = 'horizun_verify_changes' }
        @{ Name = 'clearance: everything the probe created is deleted';                                                           Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'clearance-zones' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        $rule = @{ category = 'OST_ElectricalEquipment'; face = 'front'; depth_mm = 900 }

        # 2. read-only: the argument is validated before anything is read (no write tier needed).
        $bad = & $Ctx.Call 'horizun_verify_changes' @{ element_ids = @(1); capture = $false; clearance_rules = @($rule, @{ category = 'ElectricalEquipment'; depth_mm = 900 }) }
        Case 2 $(if ($bad.isError -and [string]$bad.text -match 'clearance_rules\[1\]') { 'pass' } else { 'fail' }) ('error=' + $bad.isError + ' ' + (Short $bad))

        # $Ctx.WriteGate TRUE means the write tier is CLOSED (verify-live keeps the reason).
        if ($Ctx.WriteGate) { foreach ($i in 0, 1, 3) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-cz-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $ids = @($r.answer.data.rows | ForEach-Object { [long]$_.element_id })
                foreach ($id in $ids) { [void]$created.Add($id) }
                return @{ ids = $ids; reply = $r }
            }
            return @{ ids = @(); reply = $r }
        }
        # The document's own type of $category that $match accepts; none is not_covered.
        function Bring($category, $match) {
            $had = @(Types $category | Where-Object { (& $match $_) })
            if ($had.Count -gt 0) { return $had[0] }
            return $null
        }
        function Clearance($reply) { @($reply.data.spatial_check.findings | Where-Object { [string]$_.reason -match 'clearance zone' }) }

        try {
            $E = 130000.0; $X = 700000.0
            $lv = Create @(@{ kind = 'level'; name = "HZ_CLEAR_$run"; elevation = $E }) 'level'
            $levelId = if ($lv.ids.Count -gt 0) { $lv.ids[0] } else { $null }
            $basic = @(Types 'OST_Walls' | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
            $panelType = Bring 'OST_ElectricalEquipment' { param($t) [string]$t.family -like 'M_Lighting and Appliance Panelboard - 208V MLO*' -and ([string]$t.type -eq '100 A' -or [string]$t.name -eq '100 A') }
            $column = Bring 'OST_Columns' { param($t) $true }
            if (-not $levelId -or -not $basic -or -not $panelType -or -not $column) {
                $why = "the probe could not stage its model (level=$levelId, basic wall=" + $basic.element_id + ', panel type=' + $panelType.element_id + ', column type=' + $column.element_id + ')'
                foreach ($i in 0, 1) { Case $i 'not_covered' $why }
            }
            else {
                $w = Create @(@{ kind = 'wall'; start = @($X, 0, $E); end = @(($X + 4000), 0, $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }) 'wall'
                # The point 250 mm off the wall's +Y face picks that side (a point inside the
                # wall is refused: no side could be chosen - measured 2026-09-26).
                $p = if ($w.ids.Count -gt 0) { Create @(@{ kind = 'family_instance'; type_id = $panelType.element_id; host_id = $w.ids[0]; point = @(($X + 2000), 250, ($E + 1200)); coordinate_mode = 'absolute'; level_id = $levelId }) 'panel' } else { @{ ids = @() } }
                # 800 mm off the wall axis: clear of the wall and the panel body, inside a 900 mm front zone.
                $c = if ($p.ids.Count -gt 0) { Create @(@{ kind = 'family_instance'; type_id = $column.element_id; coordinate_mode = 'absolute'; height = 3000; point = @(($X + 2000), 800, $E); level_id = $levelId }) 'column' } else { @{ ids = @() } }
                if ($c.ids.Count -eq 0) {
                    foreach ($i in 0, 1) { Case $i 'unverified' ('the wall, panel and column could not be staged: ' + (Short $w.reply.answer) + ' | ' + (Short $p.reply.answer) + ' | ' + (Short $c.reply.answer)) }
                }
                else {
                    $panelId = [long]$p.ids[0]; $colId = [long]$c.ids[0]
                    $ask = @{ element_ids = @($panelId, $colId); capture = $false; clearance_rules = @($rule) }
                    $v1 = & $Ctx.Call 'horizun_verify_changes' $ask
                    $hit = @(Clearance $v1 | Where-Object { $_.severity -eq 'error' -and [long]$_.a.id -eq $panelId -and [long]$_.b.id -eq $colId })
                    $sc1 = $v1.data.spatial_check
                    $invaded = (-not $v1.isError -and $hit.Count -ge 1)
                    Case 0 $(if ($invaded) { 'pass' } else { 'fail' }) ('error=' + $v1.isError + ' errors=' + $sc1.errors + ' zoned=' + $sc1.equipment_clearance.zoned + ' not_measured=' + (@($sc1.equipment_clearance.not_measured | ForEach-Object { [string]$_.id + ': ' + $_.reason }) -join '; ') + ' panel=' + $panelId + ' column=' + $colId + ' finding=' + $hit[0].reason + ' ' + (Short $v1))

                    # A clean answer only means clean when the zone was really built and searched:
                    # without case 0 it cannot be told apart from "never checked".
                    if (-not $invaded) { Case 1 'not_covered' 'the invaded-zone case did not pass, so a clean answer after the move could not tell clean from not checked' }
                    else {
                        $mv = & $Ctx.Apply 'horizun_transform_elements' @{ target_document = $doc; units = 'mm'; operations = @(@{ operation = 'move'; element_ids = @($colId); vector = @(0, 5000, 0) }) } ($run + '-cz-move')
                        if ($mv.stage -ne 'apply' -or $mv.answer.isError) { Case 1 'unverified' ('the column could not be moved: ' + (Short $mv.answer)) }
                        else {
                            $v2 = & $Ctx.Call 'horizun_verify_changes' $ask
                            $left = @(Clearance $v2)
                            $sc2 = $v2.data.spatial_check
                            $measured = ($null -ne $sc2 -and $sc2.partial -eq $false -and [string]$sc2.clearance_rules_source -eq 'argument' -and [int]$sc2.equipment_clearance.zoned -ge 1)
                            Case 1 $(if (-not $v2.isError -and $measured -and $left.Count -eq 0) { 'pass' } else { 'fail' }) ('error=' + $v2.isError + ' partial=' + $sc2.partial + ' source=' + $sc2.clearance_rules_source + ' zoned=' + $sc2.equipment_clearance.zoned + ' clearance findings=' + $left.Count + ' ' + $left[0].reason)
                        }
                    }
                }
            }
        }
        finally {
            $ids = @($created.ToArray())
            if ($ids.Count -eq 0) { Case 3 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-cz-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 3 'pass' ('deleted ' + $ids.Count + ' created ids') }
                else { Case 3 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $out.ToArray()
    }
}
