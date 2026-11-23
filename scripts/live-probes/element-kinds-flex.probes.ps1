# Live probes for the newest horizun_create_elements kinds (sprinkler, flex_pipe,
# flex_duct, space, area, area_boundary). Everything stands on the module's own level, own AreaScheme-backed area
# plan view (created if none exists) and whatever sprinkler/flex-pipe/flex-duct types
# the fixture happens to carry - discovered by querying it, never assumed by id or
# name. What was created is deleted at the end; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'element-kinds-flex'
    Catalog = @(
        @{ Name = 'sprinkler: place at a point and re-read position, type and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'flex_pipe: create a path and re-read its points, diameter and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'flex_duct: create a path and re-read its points, size and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'space: place at a point on a level and re-read the point and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'area_boundary + area: close a loop on an area plan view and re-read the area'; Tool = 'horizun_create_elements' }
        @{ Name = 'element-kinds-flex probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'sprinkler: place at a point and re-read position, type and level',
            'flex_pipe: create a path and re-read its points, diameter and level',
            'flex_duct: create a path and re-read its points, size and level',
            'space: place at a point on a level and re-read the point and level',
            'area_boundary + area: close a loop on an area plan view and re-read the area',
            'element-kinds-flex probes: everything created is deleted')
        $tools = @('horizun_create_elements', 'horizun_create_elements', 'horizun_create_elements', 'horizun_create_elements',
                   'horizun_create_elements', 'horizun_delete_verified')
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        # THE REAL REPLY SHAPE (MEASURED 2026-09-26): create_elements carries no top-level
        # postconditions - its verdict is application.state and verification.verified; the
        # per-row postconditions live on each row. Checking a field the reply does not have made every
        # creation read as unverified.
        function Verified($r) {
            if ($r.stage -ne 'apply' -or $r.answer.isError -or -not $r.answer.data) { return $false }
            $d = $r.answer.data
            if ($d.application -and $d.application.state) { return ($d.application.state -eq 'verified_applied') }
            return ($d.postconditions.all_verified -eq $true)
        }
        function Why($r) { if ($r.stage -ne 'apply') { 'the rehearsal issued no token: ' + (Short $r.answer) } else { Short $r.answer } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        # A system TYPE to create a run with. MEASURED 2026-09-26, Revit 2023 (HZ23_BASE): the
        # piping/duct system types answer no category query while their instances do, and an
        # instance's type_id is the system type.
        function SystemType($category) {
            $t = Types $category | Select-Object -First 1
            if ($t) { return $t }
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $false; include_links = $false; max_rows = 5 }
            $inst = @(if ($q.data) { $q.data.rows | Where-Object { $_.type_id } }) | Select-Object -First 1
            if ($inst) { return [pscustomobject]@{ element_id = [long]$inst.type_id } }
            return $null
        }
        function Instances($category, $max) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $false; include_links = $false; max_rows = $max }
            if (-not $q.data) { return @() }
            return @($q.data.rows)
        }
        # Create() wraps horizun_create_elements for ONE plan row and returns the FIRST
        # created element id, or $null; every id the row created (element_ids, when a
        # single row creates several - area_boundary's own curves) is tracked for cleanup.
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-ekf-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                $ids = if ($row.element_ids) { @($row.element_ids | ForEach-Object { [long]$_ }) } elseif ($row.element_id) { @([long]$row.element_id) } else { @() }
                foreach ($id in $ids) { [void]$created.Add($id) }
                return @{ Row = $row; Reply = $r; Id = if ($ids.Count -gt 0) { $ids[0] } else { $null } }
            }
            return @{ Row = $null; Reply = $r; Id = $null }
        }

        # ---- staging: an own level far above everything, well clear of the real model. ----
        $E = 95000.0; $X = 700000.0; $Y = 0.0
        $lvOut = Create @(@{ kind = 'level'; name = "HZ_EKF_$run"; elevation = $E }) 'level'
        $levelId = $lvOut.Id

        # ==== 1: sprinkler ==================================================================
        # Whatever sprinkler type the document carries; an HVAC fixture usually has none, and then
        # the case is not_covered. It is hosted on an own floor's top face at a point INSIDE that
        # floor - a fixture ceiling far from the point is a face the symbol cannot sit on.
        $sprinklerType = Types 'OST_Sprinklers' | Select-Object -First 1
        if (-not $levelId -or -not $sprinklerType) {
            Case $catalog[0] $tools[0] 'not_covered' ("no own level ($levelId) or no sprinkler type in the document")
        }
        else {
            $sx = $X + 5000; $sy = $Y
            $fl = Create @(@{ kind = 'floor'; level_id = $levelId; profile = @(,@(@($sx, $sy, $E), @(($sx + 4000), $sy, $E), @(($sx + 4000), ($sy + 4000), $E), @($sx, ($sy + 4000), $E))) }) 'spr-floor'
            if (-not (Verified $fl.Reply) -or -not $fl.Id) { Case $catalog[0] $tools[0] 'not_covered' ('no own floor to host the sprinkler on: ' + (Why $fl.Reply)) }
            else {
                $point = @(($sx + 2000), ($sy + 2000), $E)
                $sp = Create @(@{ kind = 'sprinkler'; point = $point; type_id = $sprinklerType.element_id; level_id = $levelId; coordinate_mode = 'absolute'; host_id = [long]$fl.Id }) 'sprinkler'
                if (-not (Verified $sp.Reply)) { Case $catalog[0] $tools[0] 'fail' (Why $sp.Reply) }
                else { Case $catalog[0] $tools[0] 'pass' ("sprinkler $($sp.Id) ($($sprinklerType.family)) hosted on own floor $($fl.Id) at $($point -join ','), position/type/level re-read") }
            }
        }

        # ==== 2/3: flex_pipe / flex_duct ====================================================
        $flexPipeType = Types 'OST_FlexPipeCurves' | Select-Object -First 1
        $pipingSystem = SystemType 'OST_PipingSystem'
        if (-not $levelId -or -not $flexPipeType -or -not $pipingSystem) {
            Case $catalog[1] $tools[1] 'not_covered' "no own level, no flex pipe type or no piping system type in the fixture"
        }
        else {
            $points = @(@($X, ($Y + 2000), $E), @(($X + 1500), ($Y + 2200), $E), @(($X + 3000), ($Y + 2000), $E))
            $fp = Create @(@{ kind = 'flex_pipe'; points = $points; type_id = $flexPipeType.element_id; level_id = $levelId; system_type_id = $pipingSystem.element_id; diameter = 50 }) 'flex-pipe'
            if (Verified $fp.Reply) { Case $catalog[1] $tools[1] 'pass' ("flex_pipe $($fp.Id): 3 points, 50 mm, re-read") }
            else { Case $catalog[1] $tools[1] 'fail' (Why $fp.Reply) }
        }

        # FlexPipeType/FlexDuctType carry their OWN categories (MEASURED 2026-09-26): asking
        # OST_PipeCurves/OST_DuctCurves for a Flex family finds nothing. Prefer a round flex
        # duct.
        $flexDuctType = @(Types 'OST_FlexDuctCurves' | Sort-Object { if ($_.family -match 'Round') { 0 } else { 1 } }) | Select-Object -First 1
        $flexDiameter = 150
        $ductSystem = SystemType 'OST_DuctSystem'
        if (-not $levelId -or -not $flexDuctType -or -not $ductSystem) {
            Case $catalog[2] $tools[2] 'not_covered' "no own level, no flex duct type or no duct system type in the fixture"
        }
        else {
            $points = @(@($X, ($Y + 4000), $E), @(($X + 1500), ($Y + 4200), $E), @(($X + 3000), ($Y + 4000), $E))
            $fd = Create @(@{ kind = 'flex_duct'; points = $points; type_id = $flexDuctType.element_id; level_id = $levelId; system_type_id = $ductSystem.element_id; diameter = $flexDiameter }) 'flex-duct'
            if (-not (Verified $fd.Reply)) {
                # This flex duct type may be rectangular, in which case diameter is the wrong
                # section for it - Revit's own "no settable parameter" refusal names that, and
                # width/height is the retry (plan-time cannot tell the shape; see Contract.cs).
                $fd = Create @(@{ kind = 'flex_duct'; points = $points; type_id = $flexDuctType.element_id; level_id = $levelId; system_type_id = $ductSystem.element_id; width = 200; height = 150 }) 'flex-duct-rect'
            }
            if (Verified $fd.Reply) { Case $catalog[2] $tools[2] 'pass' ("flex_duct $($fd.Id): 3 points, re-read") }
            else { Case $catalog[2] $tools[2] 'fail' (Why $fd.Reply) }
        }

        # ==== 4: space =======================================================================
        if (-not $levelId) { Case $catalog[3] $tools[3] 'not_covered' 'no own level to place a space on' }
        else {
            # BOTH SHAPES A SPACE CAN TAKE. Unbounded (a free point: Revit creates it with area
            # 0 and it is reported, not refused) and ENCLOSED by four own walls, where the
            # point must re-read as inside the space (IsPointInSpace) and the area must be > 0.
            $sPoint = @(($X + 10000), ($Y + 8000))
            $sp2 = Create @(@{ kind = 'space'; point = $sPoint; level_id = $levelId }) 'space'
            $wx = $X + 10000; $wy = $Y
            $corners = @(@($wx, $wy, $E), @(($wx + 4000), $wy, $E), @(($wx + 4000), ($wy + 4000), $E), @($wx, ($wy + 4000), $E))
            $walls = @(for ($i = 0; $i -lt 4; $i++) { @{ kind = 'wall'; start = $corners[$i]; end = $corners[($i + 1) % 4]; level_id = $levelId; height = 3000 } })
            $wl = Create $walls 'space-walls'
            if ($wl.Reply.stage -eq 'apply' -and $wl.Reply.answer.data) { foreach ($wr in @($wl.Reply.answer.data.rows)) { if ($wr.element_id) { [void]$created.Add([long]$wr.element_id) } } }
            $sp3 = if (Verified $wl.Reply) { Create @(@{ kind = 'space'; point = @(($wx + 2000), ($wy + 2000)); level_id = $levelId }) 'space-enclosed' } else { $null }
            if (-not (Verified $sp2.Reply)) { Case $catalog[3] $tools[3] 'fail' ('unbounded: ' + (Why $sp2.Reply)) }
            elseif (-not $sp3) { Case $catalog[3] $tools[3] 'fail' ('the four enclosing walls were not created: ' + (Why $wl.Reply)) }
            elseif (-not (Verified $sp3.Reply)) { Case $catalog[3] $tools[3] 'fail' ('enclosed: ' + (Why $sp3.Reply)) }
            elseif ($sp2.Row.area_enclosed -ne $false -or $sp3.Row.area_enclosed -ne $true) {
                Case $catalog[3] $tools[3] 'fail' ("area_enclosed read unbounded=$($sp2.Row.area_enclosed) enclosed=$($sp3.Row.area_enclosed); expected false and true")
            }
            else {
                Case $catalog[3] $tools[3] 'pass' ("unbounded space $($sp2.Id) area 0 reported; enclosed space $($sp3.Id) inside 4 own walls: area_sqft=$($sp3.Row.area_sqft), point re-read inside")
            }
        }

        # ==== 5: area_boundary + area ========================================================
        # AN AREA SCHEME HAS NO QUERYABLE CATEGORY (MEASURED 2026-09-26: query_model with
        # OST_AreaSchemes matches nothing). The discovery path is the refusal itself: a
        # create_area_plan rehearsal without a scheme lists the document's schemes by id.
        $schemeId = $null
        if ($levelId) {
            $ask = & $Ctx.Call 'horizun_manage_views' @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'create_area_plan'; level_id = $levelId }) }
            $m = [regex]::Match([string]$ask.text, "Schemes: '[^']*' \(id (\d+)\)")
            if ($m.Success) { $schemeId = [long]$m.Groups[1].Value }
        }
        $areaViewId = $null
        if (-not $levelId -or -not $schemeId) {
            Case $catalog[4] $tools[4] 'not_covered' "no own level, or the create_area_plan refusal listed no AreaScheme"
        }
        else {
            $av = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'
                    actions = @(@{ operation = 'create_area_plan'; level_id = $levelId; area_scheme_id = $schemeId; key = 'ekf-areaview' }) } ($run + '-ekf-areaview')
            $avRow = if ($av.stage -eq 'apply' -and -not $av.answer.isError -and $av.answer.data) { @($av.answer.data.rows) | Select-Object -First 1 }
            if ($av.stage -ne 'apply' -or $av.answer.isError -or -not $avRow -or -not $avRow.element_id) {
                Case $catalog[4] $tools[4] 'fail' ("create_area_plan: " + (Why $av))
            }
            else {
                $areaViewId = [long]$avRow.element_id; [void]$created.Add($areaViewId)
                $ax = $X + 20000; $ay = $Y
                $loop = @(@($ax, $ay, $E), @(($ax + 4000), $ay, $E), @(($ax + 4000), ($ay + 4000), $E), @($ax, ($ay + 4000), $E), @($ax, $ay, $E))
                # profile = chains of POINTS (a polyline per chain), not of segment pairs -
                # the shape room_separator and area_boundary share (MEASURED 2026-09-26: pairs
                # are refused in the rehearsal, "point/start/end must contain 3 XYZ coordinates").
                $ab = Create @(@{ kind = 'area_boundary'; profile = @(,$loop); view_id = $areaViewId }) 'area-boundary'
                if (-not (Verified $ab.Reply)) { Case $catalog[4] $tools[4] 'fail' ('area_boundary: ' + (Why $ab.Reply)) }
                else {
                    $ar = Create @(@{ kind = 'area'; point = @(($ax + 2000), ($ay + 2000)); view_id = $areaViewId }) 'area'
                    if (-not (Verified $ar.Reply)) { Case $catalog[4] $tools[4] 'fail' ('area: ' + (Why $ar.Reply)) }
                    else {
                        $row = $ar.Row
                        Case $catalog[4] $tools[4] 'pass' ("area $($ar.Id) inside a 4 m loop: area_sqft=$($row.area_sqft), area_enclosed=$($row.area_enclosed)")
                    }
                }
            }
        }

        # ---- cleanup: newest first, the area view and level last. -------------------------
        $ids = @($created | Select-Object -Unique)
        if ($ids.Count -eq 0) { Case $catalog[5] $tools[5] 'not_covered' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-ekf-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[5] $tools[5] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[5] $tools[5] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Why $del)) }
        }
        return $cases
    }
}
