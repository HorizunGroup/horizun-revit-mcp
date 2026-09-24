# Live probes: curtain grids, railings, slab shape and arrays.
#
# Everything stands on what this module creates in the disposable document: its own
# level at a known elevation far above the model, a curtain wall, a basic wall, a
# floor. Types are discovered by querying the fixture; nothing is assumed by id or
# name. What was created is deleted at the end; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'architecture-edits'
    Catalog = @(
        @{ Name = 'curtain: read the grid of an own curtain wall';                    Tool = 'horizun_manage_curtain' }
        @{ Name = 'curtain: add a v grid line at a measured offset and re-read it';   Tool = 'horizun_manage_curtain' }
        @{ Name = 'curtain: add mullions on the new line and re-read every segment';   Tool = 'horizun_manage_curtain' }
        @{ Name = 'curtain: change one panel type and re-read it';                    Tool = 'horizun_manage_curtain' }
        @{ Name = 'curtain: remove the added grid line';                              Tool = 'horizun_manage_curtain' }
        @{ Name = 'railing: sketch a railing on a level and re-read its path';         Tool = 'horizun_create_railing' }
        @{ Name = 'slab shape: add a point with an offset and re-read its elevation';  Tool = 'horizun_slab_shape' }
        @{ Name = 'slab shape: reset the shape';                                       Tool = 'horizun_slab_shape' }
        @{ Name = 'array: linear array of an own wall lands every copy on the formula'; Tool = 'horizun_transform_elements' }
        @{ Name = 'array: grouped radial array lands every copy on the formula';       Tool = 'horizun_transform_elements' }
        @{ Name = 'architecture probes: everything created is deleted';                Tool = 'horizun_delete_verified' }
    )
    Run = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'curtain: read the grid of an own curtain wall', 'curtain: add a v grid line at a measured offset and re-read it',
            'curtain: add mullions on the new line and re-read every segment', 'curtain: change one panel type and re-read it',
            'curtain: remove the added grid line', 'railing: sketch a railing on a level and re-read its path',
            'slab shape: add a point with an offset and re-read its elevation', 'slab shape: reset the shape',
            'array: linear array of an own wall lands every copy on the formula', 'array: grouped radial array lands every copy on the formula',
            'architecture probes: everything created is deleted')
        $tools = @('horizun_manage_curtain', 'horizun_manage_curtain', 'horizun_manage_curtain', 'horizun_manage_curtain', 'horizun_manage_curtain',
                   'horizun_create_railing', 'horizun_slab_shape', 'horizun_slab_shape', 'horizun_transform_elements', 'horizun_transform_elements',
                   'horizun_delete_verified')
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Verified($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data -and $r.answer.data.postconditions.all_verified -eq $true }
        function Why($r) { if ($r.stage -ne 'apply') { 'the rehearsal issued no token: ' + (Short $r.answer) } else { Short $r.answer } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } $key
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $ids = @($r.answer.data.rows | ForEach-Object { [long]$_.element_id })
                foreach ($id in $ids) { [void]$created.Add($id) }
                return $ids
            }
            return $null
        }

        # ---- staging: an own level far above everything, and the types to build with.
        $E = 90000.0; $X = 600000.0; $Y = 0.0
        $level = Create @(@{ kind = 'level'; name = "HZ_ARCH_$run"; elevation = $E }) 'arch-level'
        $wallTypes = Types 'OST_Walls'
        $curtainType = @($wallTypes | Where-Object { $_.family -match 'Curtain|cortina' -or $_.type -match 'Curtain|cortina' }) | Select-Object -First 1
        $basicType = @($wallTypes | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
        $floorType = Types 'OST_Floors' | Select-Object -First 1
        $railType = Types 'OST_StairsRailing' | Select-Object -First 1
        $levelId = if ($level) { $level[0] } else { $null }

        # ---- curtain ---------------------------------------------------------------------
        $cw = $null
        if ($levelId -and $curtainType) { $cw = Create @(@{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 6000), $Y, $E); level_id = $levelId; type_id = $curtainType.element_id; height = 3000 }) 'arch-cw' }
        if (-not $cw) {
            foreach ($n in $catalog[0..4]) { Case $n 'horizun_manage_curtain' 'not_covered' ("no own curtain wall could be staged (level=$levelId, curtain type=" + $curtainType.element_id + ')') }
        }
        else {
            $cwId = $cw[0]
            $rd = & $Ctx.Call 'horizun_manage_curtain' @{ target_document = $doc; operation = 'read'; element_id = $cwId }
            if ($rd.isError -or -not $rd.data) { Case $catalog[0] 'horizun_manage_curtain' 'fail' (Short $rd) }
            elseif ($rd.data.element_id -eq $cwId -and $null -ne $rd.data.counts) { Case $catalog[0] 'horizun_manage_curtain' 'pass' ("panels=" + $rd.data.counts.panels + " u=" + $rd.data.counts.u_lines + " v=" + $rd.data.counts.v_lines + " base_z=" + $rd.data.base_z) }
            else { Case $catalog[0] 'horizun_manage_curtain' 'fail' 'the read did not name the wall or its counts' }

            $add = & $Ctx.Apply 'horizun_manage_curtain' @{ target_document = $doc; operation = 'add_grid_line'; element_id = $cwId; direction = 'v'; offset = 1234 } 'arch-cw-line'
            $lineId = $null
            if (Verified $add) { $lineId = [long]$add.answer.data.evidence.grid_line_id; Case $catalog[1] 'horizun_manage_curtain' 'pass' "grid line $lineId at 1234 mm, re-read" }
            else { Case $catalog[1] 'horizun_manage_curtain' 'fail' (Why $add) }

            if ($lineId) {
                $mullionType = Types 'OST_CurtainWallMullions' | Select-Object -First 1
                if (-not $mullionType) { Case $catalog[2] 'horizun_manage_curtain' 'not_covered' 'the fixture offers no mullion type' }
                else {
                    $m = & $Ctx.Apply 'horizun_manage_curtain' @{ target_document = $doc; operation = 'set_mullions'; element_id = $cwId; grid_line_id = $lineId; mode = 'add'; mullion_type_id = $mullionType.element_id } 'arch-cw-mullion'
                    if (Verified $m) { Case $catalog[2] 'horizun_manage_curtain' 'pass' ('mullions ' + (@($m.answer.data.evidence.mullion_ids) -join ',')) } else { Case $catalog[2] 'horizun_manage_curtain' 'fail' (Why $m) }
                }
            }
            else { Case $catalog[2] 'horizun_manage_curtain' 'unverified' 'no grid line was added to put a mullion on' }

            $rd2 = & $Ctx.Call 'horizun_manage_curtain' @{ target_document = $doc; operation = 'read'; element_id = $cwId }
            $panel = @($rd2.data.panels) | Select-Object -First 1
            $panelType = if ($panel) { Types 'OST_CurtainWallPanels' | Where-Object { [long]$_.element_id -ne [long]$panel.type_id } | Select-Object -First 1 }
            if (-not $panel -or -not $panelType) { Case $catalog[3] 'horizun_manage_curtain' 'not_covered' 'no panel, or no second panel type, to change to' }
            else {
                $pt = & $Ctx.Apply 'horizun_manage_curtain' @{ target_document = $doc; operation = 'set_panel_type'; element_id = $cwId; panel_ids = @([long]$panel.id); type_id = $panelType.element_id } 'arch-cw-panel'
                if (Verified $pt) { Case $catalog[3] 'horizun_manage_curtain' 'pass' ("panel " + $panel.id + " -> type " + $panelType.element_id) } else { Case $catalog[3] 'horizun_manage_curtain' 'fail' (Why $pt) }
            }

            if ($lineId) {
                $rm = & $Ctx.Apply 'horizun_manage_curtain' @{ target_document = $doc; operation = 'remove_grid_line'; element_id = $cwId; grid_line_id = $lineId } 'arch-cw-remove'
                if (Verified $rm) { Case $catalog[4] 'horizun_manage_curtain' 'pass' "grid line $lineId gone" } else { Case $catalog[4] 'horizun_manage_curtain' 'fail' (Why $rm) }
            }
            else { Case $catalog[4] 'horizun_manage_curtain' 'unverified' 'no grid line was added to remove' }
        }

        # ---- railing -----------------------------------------------------------------------
        if (-not $levelId -or -not $railType) { Case $catalog[5] 'horizun_create_railing' 'not_covered' 'no own level or no railing type in the fixture' }
        else {
            $rl = & $Ctx.Apply 'horizun_create_railing' @{ target_document = $doc; units = 'mm'; type_id = $railType.element_id; level_id = $levelId
                    path = @(@($X, ($Y + 3000), $E), @(($X + 3000), ($Y + 3000), $E), @(($X + 3000), ($Y + 5000), $E)) } 'arch-railing'
            if (Verified $rl) {
                foreach ($id in @($rl.answer.data.evidence.created_ids)) { [void]$created.Add([long]$id) }
                Case $catalog[5] 'horizun_create_railing' 'pass' ('railing ' + (@($rl.answer.data.evidence.created_ids) -join ',') + ', 2 segments re-read')
            }
            else { Case $catalog[5] 'horizun_create_railing' 'fail' (Why $rl) }
        }

        # ---- slab shape --------------------------------------------------------------------
        $fl = $null
        if ($levelId -and $floorType) {
            $fx = $X; $fy = $Y + 8000
            $fl = Create @(@{ kind = 'floor'; level_id = $levelId; type_id = $floorType.element_id
                              profile = @(,@(@($fx, $fy, $E), @(($fx + 4000), $fy, $E), @(($fx + 4000), ($fy + 4000), $E), @($fx, ($fy + 4000), $E))) }) 'arch-floor'
        }
        if (-not $fl) { foreach ($n in $catalog[6..7]) { Case $n 'horizun_slab_shape' 'not_covered' 'no own floor could be staged' } }
        else {
            $sp = & $Ctx.Apply 'horizun_slab_shape' @{ target_document = $doc; operation = 'add_point'; element_id = $fl[0]; points = @(,@(($X + 2000), ($Y + 10000), -50)) } 'arch-slab-point'
            if (Verified $sp) { Case $catalog[6] 'horizun_slab_shape' 'pass' ('offset -50 mm re-read; z read as ' + (@($sp.answer.data.evidence.z_convention) -join ',')) }
            else { Case $catalog[6] 'horizun_slab_shape' 'fail' (Why $sp) }
            $rs = & $Ctx.Apply 'horizun_slab_shape' @{ target_document = $doc; operation = 'reset_shape'; element_id = $fl[0] } 'arch-slab-reset'
            if (Verified $rs) { Case $catalog[7] 'horizun_slab_shape' 'pass' 'no interior vertex, all at zero' } else { Case $catalog[7] 'horizun_slab_shape' 'fail' (Why $rs) }
        }

        # ---- arrays ------------------------------------------------------------------------
        $bw = $null
        if ($levelId -and $basicType) { $bw = Create @(@{ kind = 'wall'; start = @(($X + 10000), $Y, $E); end = @(($X + 11000), $Y, $E); level_id = $levelId; type_id = $basicType.element_id; height = 1000 }) 'arch-bw' }
        if (-not $bw) { foreach ($n in $catalog[8..9]) { Case $n 'horizun_transform_elements' 'not_covered' 'no own basic wall could be staged' } }
        else {
            $la = & $Ctx.Apply 'horizun_transform_elements' @{ target_document = $doc; units = 'mm'; operations = @(@{ operation = 'array_linear'; element_ids = @($bw[0]); count = 3; vector = @(0, 2000, 0) }) } 'arch-array-linear'
            $laRow = if ($la.answer.data) { @($la.answer.data.rows) | Select-Object -First 1 }
            if ($la.stage -eq 'apply' -and -not $la.answer.isError -and $laRow.verified -eq $true) {
                foreach ($id in @($laRow.created_ids)) { [void]$created.Add([long]$id) }
                Case $catalog[8] 'horizun_transform_elements' 'pass' ('copies ' + (@($laRow.created_ids) -join ','))
            }
            else { Case $catalog[8] 'horizun_transform_elements' 'fail' (Why $la) }
            $ra = & $Ctx.Apply 'horizun_transform_elements' @{ target_document = $doc; units = 'mm'; operations = @(@{ operation = 'array_radial'; element_ids = @($bw[0]); count = 3; group = $true
                    axis_start = @(($X + 10000), ($Y - 3000), $E); axis_end = @(($X + 10000), ($Y - 3000), ($E + 1000)); angle_degrees = 90 }) } 'arch-array-radial'
            $raRow = if ($ra.answer.data) { @($ra.answer.data.rows) | Select-Object -First 1 }
            if ($ra.stage -eq 'apply' -and -not $ra.answer.isError -and $raRow.verified -eq $true) {
                # Its copies live inside the array's groups: the array and the level take them with them.
                if ($raRow.array_id) { [void]$created.Add([long]$raRow.array_id) }
                Case $catalog[9] 'horizun_transform_elements' 'pass' ('array ' + $raRow.array_id + ' copies ' + (@($raRow.created_ids) -join ','))
            }
            else { Case $catalog[9] 'horizun_transform_elements' 'fail' (Why $ra) }
        }

        # ---- cleanup: newest first, the level last. ----------------------------------------
        $ids = @($created | Select-Object -Unique)
        if ($ids.Count -eq 0) { Case $catalog[10] 'horizun_delete_verified' 'not_covered' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } 'arch-cleanup'
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[10] 'horizun_delete_verified' 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[10] 'horizun_delete_verified' 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Why $del)) }
        }
        return $cases
    }
}
