# Live probes: horizun_code_check operation=travel_distance (Revit's PathOfTravel).
# Stages its OWN level far above the model, its own floor plan, four walls closing a
# 6 x 4 m room, one door in the south wall (the declared exit) and the room itself;
# measures, checks a travel_distance_m rule through the requirement-set grammar,
# keeps the paths once (create_paths: dry run -> token -> apply), and deletes
# everything it created. The document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'egress-travel'
    Catalog = @(
        @{ Name = 'egress: travel_distance measures the own room to its exit door (length > 0, path ends at the door)'; Tool = 'horizun_code_check' }
        @{ Name = 'egress: a travel_distance_m rule passes through the requirement-set grammar'; Tool = 'horizun_code_check' }
        @{ Name = 'egress: create_paths keeps a PathOfTravel for the room, re-read after commit'; Tool = 'horizun_code_check' }
        @{ Name = 'egress probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @($script:HzProbeModules | Where-Object { $_.Name -eq 'egress-travel' } | Select-Object -First 1).Catalog | ForEach-Object { $_.Name }
        $tools = @('horizun_code_check', 'horizun_code_check', 'horizun_code_check', 'horizun_delete_verified')
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-egr-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }

        # ---- staging: own level, plan, walls, door, room. MEASURED coordinates in mm. ----
        $E = 97000.0; $X = 760000.0; $Y = 0.0; $W = 6000.0; $D = 4000.0
        $levelId = Create @{ kind = 'level'; name = "HZ_EGR_$run"; elevation = $E } 'level'
        # A curtain or stacked wall would take the door as a panel, not as a hosted opening.
        $wallType = @(Types 'OST_Walls' | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
        $doorType = Types 'OST_Doors' | Select-Object -First 1
        # MEASURED (spatial-coherence.probes.ps1): a write fixture may carry no door family;
        # then the probe cannot stage its exit and says so.
        $doorWhy = if ($doorType) { '' } else { ' the document holds no door type' }
        $planId = $null
        if ($levelId) {
            $mv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'
                    actions = @(@{ operation = 'create_floor_plan'; key = 'egress'; name = "HZ_EGR_PLAN_$run"; level_id = $levelId }) } ($run + '-egr-plan')
            if (Applied $mv) { $planId = [long]$mv.answer.data.aliases.egress; [void]$created.Add($planId) }
        }
        $walls = @()
        if ($levelId -and $wallType) {
            $corners = @(@($X, $Y), @(($X + $W), $Y), @(($X + $W), ($Y + $D)), @($X, ($Y + $D)))
            for ($k = 0; $k -lt 4; $k++) {
                $a = $corners[$k]; $b = $corners[($k + 1) % 4]
                $walls += Create @{ kind = 'wall'; start = @($a[0], $a[1], $E); end = @($b[0], $b[1], $E); height = 3000
                                    level_id = $levelId; type_id = $wallType.element_id } "wall$k"
            }
        }
        # create_elements has no 'door' kind: a door is a family_instance hosted on its wall
        # (the same row spatial-coherence.probes.ps1 stages and live-verifies).
        $doorPoint = @(($X + $W / 2), $Y, $E)
        $doorId = if ($walls.Count -eq 4 -and $walls[0] -and $doorType) {
            Create @{ kind = 'family_instance'; type_id = $doorType.element_id; point = $doorPoint; coordinate_mode = 'absolute'
                      level_id = $levelId; host_id = $walls[0] } 'door'
        } else { $null }
        $roomId = if ($doorId) { Create @{ kind = 'room'; point = @(($X + $W / 2), ($Y + $D / 2)); level_id = $levelId } 'room' } else { $null }

        if (-not ($planId -and $doorId -and $roomId)) {
            $why = "staging incomplete: level=$levelId plan=$planId walls=$(@($walls | Where-Object { $_ }).Count) door=$doorId room=$roomId" + $doorWhy
            for ($i = 0; $i -lt 3; $i++) { Case $catalog[$i] $tools[$i] 'unverified' $why }
        }
        else {
            $travel = @{ view_ids = @($planId); exits = @{ element_ids = @($doorId) }; room_ids = @($roomId); max_m = 45 }

            # ==== 1: measure only - no transaction, a routed length ending at the door ======
            $m = & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; operation = 'travel_distance'; travel = $travel }
            $row = if ($m.data) { @($m.data.rooms | Where-Object { [long]$_.room_id -eq $roomId }) | Select-Object -First 1 } else { $null }
            $last = if ($row -and $row.polyline_m) { @($row.polyline_m)[-1] } else { $null }
            $endGap = if ($last) { [math]::Sqrt([math]::Pow([double]$last[0] - $doorPoint[0] / 1000, 2) + [math]::Pow([double]$last[1] - $doorPoint[1] / 1000, 2)) } else { $null }
            $ok1 = -not $m.isError -and $row -and [double]$row.distance_m -gt 0 -and [long]$row.exit_door_id -eq $doorId -and
                   $null -ne $endGap -and $endGap -le 0.5 -and $row.outcome -eq 'passes'
            Case $catalog[0] $tools[0] $(if ($ok1) { 'pass' } else { 'fail' }) $(if ($row) { "distance_m=$($row.distance_m) upper_bound_m=$($row.upper_bound_m) routed=$($row.routed)/$($row.candidates) exit=$($row.exit_door_id) end_gap_m=$endGap outcome=$($row.outcome) $($row.reason)" } else { Short $m })

            # ==== 2: the grammar - a rule with config becomes pass/fail, not unverified =====
            $set = @{ requirement_set = @{ id = "hz-egress-probe-$run"; version = '1'; title = 'egress probe' }
                      rules = @(@{ id = 'egress-max'; selector = @{ category = 'OST_Rooms' }
                                   assertion = @{ measure = 'travel_distance_m'; operator = 'lte'; value = 45 }
                                   config = @{ route_view_id = $planId; exits = @{ element_ids = @($doorId) } } }) }
            $c = & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; requirement_set = $set; include_passes = $true; max_findings = 500 }
            $f = if ($c.data) { @($c.data.findings | Where-Object { [long]$_.element_id -eq $roomId }) | Select-Object -First 1 } else { $null }
            Case $catalog[1] $tools[1] $(if (-not $c.isError -and $f -and $f.outcome -eq 'passes') { 'pass' } else { 'fail' }) $(if ($f) { "outcome=$($f.outcome) $($f.reason)" } else { Short $c })

            # ==== 3: create_paths - dry run, token, apply, re-read ===========================
            $travel.create_paths = $true
            $k = & $Ctx.Apply 'horizun_code_check' @{ target_document = $doc; operation = 'travel_distance'; travel = $travel } ($run + '-egr-paths')
            if (Applied $k) { foreach ($p in @($k.answer.data.paths)) { if ($p.path_id) { [void]$created.Add([long]$p.path_id) } } }
            $ok3 = (Applied $k) -and [int]$k.answer.data.paths_verified -ge 1 -and $k.answer.data.dry_run -eq $false
            Case $catalog[2] $tools[2] $(if ($ok3) { 'pass' } else { 'fail' }) $(if (Applied $k) { "paths_verified=$($k.answer.data.paths_verified)" } else { 'stage ' + $k.stage + ': ' + (Short $k.answer) })
        }

        # ==== 4: cleanup, newest first (paths, room, door, walls, plan, level) ==============
        $ids = @($created | ForEach-Object { [long]$_ })
        if ($ids.Count -eq 0) { Case $catalog[3] $tools[3] 'unverified' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-egr-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[3] $tools[3] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[3] $tools[3] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        return $cases
    }
}
