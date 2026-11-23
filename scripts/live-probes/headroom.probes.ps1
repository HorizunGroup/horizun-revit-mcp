# Live probes: horizun_code_check operation=headroom (vertical rays in a 3D view, read-only).
# Stages its OWN two levels 3 m apart far above the model, a 4 x 4 m floor on each at
# X = 1,140,000 mm (the lower one is the surface the upper one stands clear of), a third floor
# 10 m away with nothing below it, and its own 3D view (no template, no section box). The
# floor type is the document's own first Generic floor type. Two own pipes exercise the
# curve path: one hung 500 mm under the upper floor over the lower one (sampled along its
# location curve), one run through the middle of the lone floor's thickness (the ray meets
# that floor before the pipe and again after it: inside_target, never a clear height to the
# floor's own far side). Pipe and piping-system types come BY NAME from the document; a
# document without them leaves the pipe cases not staged, with the reason. Everything created
# is deleted; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'headroom'
    Catalog = @(
        @{ Name = 'headroom: down from the own upper floor reaches the own lower floor (surface by id, full coverage, passes)'; Tool = 'horizun_code_check' }
        @{ Name = 'headroom: up from the own lower floor measures the same clear height as down (within 1 mm)'; Tool = 'horizun_code_check' }
        @{ Name = 'headroom: a min_mm above the measured clear height fails the element'; Tool = 'horizun_code_check' }
        @{ Name = 'headroom: an own floor with nothing below is not_measured, never passes'; Tool = 'horizun_code_check' }
        @{ Name = 'headroom: a view_id that is not a 3D view is refused by name'; Tool = 'horizun_code_check' }
        @{ Name = 'headroom probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
        @{ Name = "headroom: an own pipe hung under the upper floor is sampled along its curve and measured down to the lower one"; Tool = 'horizun_code_check' }
        @{ Name = "headroom: an own pipe run inside the own floor is inside_target, never measured against the floor's far side"; Tool = 'horizun_code_check' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $entries = @(@($script:HzProbeModules | Where-Object { $_.Name -eq 'headroom' } | Select-Object -First 1).Catalog)
        $catalog = @($entries | ForEach-Object { $_.Name }); $tools = @($entries | ForEach-Object { $_.Tool })
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        $why = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-hdr-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            [void]$why.Add("$key not created: stage=" + $r.stage + ' ' + (Short $r.answer))
            return $null
        }
        function TypeRows($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Named($rows, $want) {
            @($rows | Where-Object { $want -eq [string]$_.type -or $want -eq ([string]$_.family + ': ' + [string]$_.type) -or
                                     ([string]$_.type -and $want.EndsWith(': ' + [string]$_.type)) }) | Select-Object -First 1
        }
        # The document's own first Generic floor type, by name.
        $floorType = @(TypeRows 'OST_Floors' | Where-Object { ([string]$_.family + ': ' + [string]$_.type) -match 'Generic' }) | Select-Object -First 1
        if (-not $floorType) { [void]$why.Add('the document holds no Generic floor type') }

        # ---- staging (mm): two own levels 3 m apart, floors A (lower), B (upper), C (alone) ------
        $E = 112000.0; $H = 3000.0; $X = 1140000.0; $Y = 30000.0; $S = 4000.0; $XC = $X + 10000.0
        $l1 = Create @{ kind = 'level'; name = "HZ_HDR1_$run"; elevation = $E } 'level1'
        $l2 = Create @{ kind = 'level'; name = "HZ_HDR2_$run"; elevation = ($E + $H) } 'level2'
        # One loop of four points. The leading comma keeps PowerShell from unrolling the
        # list of loops into its single loop when the function returns it.
        function Square($x0, $z) { , @(, @(@($x0, $Y, $z), @(($x0 + $S), $Y, $z), @(($x0 + $S), ($Y + $S), $z), @($x0, ($Y + $S), $z))) }
        $floorA = if ($l1 -and $floorType) { Create @{ kind = 'floor'; level_id = $l1; type_id = $floorType.element_id; profile = (Square $X $E) } 'floorA' } else { $null }
        $floorB = if ($l2 -and $floorType) { Create @{ kind = 'floor'; level_id = $l2; type_id = $floorType.element_id; profile = (Square $X ($E + $H)) } 'floorB' } else { $null }
        $floorC = if ($l2 -and $floorType) { Create @{ kind = 'floor'; level_id = $l2; type_id = $floorType.element_id; profile = (Square $XC ($E + $H)) } 'floorC' } else { $null }
        $viewId = $null
        $mv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'create_3d'; key = 'hdr'; name = "HZ_HDR_3D_$run" }) } ($run + '-hdr-view')
        if (Applied $mv) { $viewId = [long]$mv.answer.data.aliases.hdr; [void]$created.Add($viewId) } else { [void]$why.Add('3D view not created: stage=' + $mv.stage + ' ' + (Short $mv.answer)) }
        # Fine detail through the view's own parameter (VIEW_DETAIL_LEVEL: 3 = Fine), a verified typed
        # write: a fresh 3D view takes its type's default (Medium in the 2026 fixture, MEASURED
        # 2026-09-27) and headroom refuses anything below Fine.
        if ($viewId) {
            $fine = & $Ctx.Apply 'horizun_write_params_verified' @{ target_document = $doc; writes = @(@{ target_id = $viewId; parameter = 'VIEW_DETAIL_LEVEL'; value = 3 }) } ($run + '-hdr-fine')
            if ($fine.stage -ne 'apply' -or $fine.answer.isError) { [void]$why.Add('the own 3D view could not be set to Fine: ' + (Short $fine.answer)) }
        }
        # ---- two own pipes: hung under B over A, and run through the middle of C's thickness ------
        function Bring($category, $candidates) {
            foreach ($cand in $candidates) { $hit = Named (TypeRows $category) $cand; if ($hit) { return $hit } }
            [void]$why.Add("no $category type named " + ($candidates -join ' / ') + ' in the document')
            return $null
        }
        $pipeType = Bring 'OST_PipeCurves' @('Pipe Types: Default', 'Pipe Types: Standard')
        $pipeSys = Bring 'OST_PipingSystem' @('Domestic Cold Water', 'Hydronic Supply')
        $pipeHung = $null; $pipeIn = $null
        if ($l1 -and $floorA -and $floorB -and $pipeType -and $pipeSys) {
            $pipeHung = Create @{ kind = 'pipe'; start = @(($X + 500), ($Y + 2000), ($E + 2500)); end = @(($X + 3500), ($Y + 2000), ($E + 2500)); diameter = 25
                                  level_id = $l1; type_id = [long]$pipeType.element_id; system_type_id = [long]$pipeSys.element_id } 'pipe-hung'
        }
        if ($l2 -and $floorC -and $pipeType -and $pipeSys) {
            # C's real thickness, read back: the pipe goes through the middle of it.
            $bb = & $Ctx.Call 'horizun_query_model' @{ element_ids = @($floorC); include_bounding_box = $true; include_links = $false }
            $box = if ($bb.data) { (@($bb.data.rows) | Select-Object -First 1).bounding_box } else { $null }
            $thick = if ($box) { [double]@($box.max)[2] - [double]@($box.min)[2] } else { 0 }
            if ($thick -ge 60) {
                $mid = ([double]@($box.max)[2] + [double]@($box.min)[2]) / 2
                $pipeIn = Create @{ kind = 'pipe'; start = @(($XC + 500), ($Y + 2000), $mid); end = @(($XC + 3500), ($Y + 2000), $mid); diameter = 15
                                    level_id = $l2; type_id = [long]$pipeType.element_id; system_type_id = [long]$pipeSys.element_id } 'pipe-in'
            } else { [void]$why.Add("floor C's thickness read as $thick mm (under 60 mm, or unread): no pipe fits inside it: " + (Short $bb)) }
        }
        $staging = "levels=$l1,$l2 floors=$floorA,$floorB,$floorC view=$viewId pipes=$pipeHung,$pipeIn " + ($why -join '; ')

        function Headroom($ids, $direction, $minMm) {
            & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; operation = 'headroom'; max_findings = 50
                headroom = @{ view_id = $viewId; element_ids = @($ids); min_mm = $minMm; direction = $direction; spacing_mm = 1000 } }
        }
        function RowOf($r, $id) { if ($r.data) { @($r.data.elements | Where-Object { [long]$_.element_id -eq $id }) | Select-Object -First 1 } else { $null } }
        function Describe($row) { if ($row) { "outcome=$($row.outcome) min_clear_mm=$($row.min_clear_mm) coverage=$($row.coverage) measured=$($row.measured)/$($row.on_element) surface=$($row.governing.surface.element_id)" } else { 'no row' } }

        # headroom refuses a 3D view below Fine detail (MEP curves have no faces there). A fresh 3D view
        # takes its view type's default detail level and no typed setter for View.DetailLevel exists yet, so
        # a refusal here is a STAGING gap - cases 1-4 become unverified - never a measured result.
        $gate = if ($viewId -and $floorB) { Headroom $floorB "down" 2000 } else { $null }
        if ($gate -and $gate.isError -and [string]$gate.text -match "detail level") {
            $staging += "; the own 3D view is below Fine detail, refused by name: " + (Short $gate); $viewId = $null
        }
        $down = $null
        if (-not ($floorA -and $floorB -and $viewId)) { for ($i = 0; $i -lt 3; $i++) { Case $catalog[$i] $tools[$i] 'unverified' ('staging incomplete: ' + $staging) } }
        else {
            # ==== 1: down from B - the lower own floor is the surface, every sample measured ======
            $r1 = Headroom $floorB 'down' 2000
            $down = RowOf $r1 $floorB
            $clear = if ($down -and $null -ne $down.min_clear_mm) { [double]$down.min_clear_mm } else { $null }
            # B's own thickness is the only unknown: the clear height is under 3000 and over 2000 mm.
            $ok1 = -not $r1.isError -and $down -and $down.outcome -eq 'passes' -and [long]$down.governing.surface.element_id -eq $floorA -and
                   [double]$down.coverage -eq 1 -and $null -ne $clear -and $clear -lt $H -and $clear -gt 2000
            Case $catalog[0] $tools[0] $(if ($ok1) { 'pass' } else { 'fail' }) $(if ($down) { Describe $down } else { Short $r1 })

            # ==== 2: up from A - the same gap from the other side ==================================
            $r2 = Headroom $floorA 'up' 2000
            $up = RowOf $r2 $floorA
            $ok2 = $up -and $null -ne $clear -and $null -ne $up.min_clear_mm -and [math]::Abs([double]$up.min_clear_mm - $clear) -le 1 -and [long]$up.governing.surface.element_id -eq $floorB
            Case $catalog[1] $tools[1] $(if ($ok2) { 'pass' } else { 'fail' }) ("down=$clear up: " + $(if ($up) { Describe $up } else { Short $r2 }))

            # ==== 3: the same element judged against a threshold above what was measured ==========
            if ($null -eq $clear) { Case $catalog[2] $tools[2] 'unverified' 'case 1 measured no clear height' }
            else {
                $r3 = Headroom $floorB 'down' ([math]::Round($clear + 100))
                $hi = RowOf $r3 $floorB
                Case $catalog[2] $tools[2] $(if ($hi -and $hi.outcome -eq 'fails') { 'pass' } else { 'fail' }) ("min_mm=$([math]::Round($clear + 100)) " + $(if ($hi) { Describe $hi } else { Short $r3 }))
            }
        }

        # ==== 4: C has nothing below it: not_measured, and never a pass ===========================
        if (-not ($floorC -and $viewId)) { Case $catalog[3] $tools[3] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $r4 = Headroom $floorC 'down' 2000
            $alone = RowOf $r4 $floorC
            $ok4 = -not $r4.isError -and $alone -and $alone.outcome -eq 'not_measured' -and [int]$alone.measured -eq 0
            Case $catalog[3] $tools[3] $(if ($ok4) { 'pass' } else { 'fail' }) $(if ($alone) { (Describe $alone) + " reason=$($alone.reason)" } else { Short $r4 })
        }

        # ==== 5: a level id is not a 3D view: refused by name, no ray cast =========================
        if (-not ($l1 -and $floorB)) { Case $catalog[4] $tools[4] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $r5 = & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; operation = 'headroom'
                    headroom = @{ view_id = $l1; element_ids = @($floorB); min_mm = 2000 } }
            Case $catalog[4] $tools[4] $(if ($r5.isError -and [string]$r5.text -match 'not a 3D view') { 'pass' } else { 'fail' }) (Short $r5)
        }

        # ==== 7: the hung pipe - sampled along its location curve, measured down to A ==============
        if (-not ($pipeHung -and $viewId)) { Case $catalog[6] $tools[6] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $r7 = Headroom $pipeHung 'down' 1000
            $hung = RowOf $r7 $pipeHung
            # The pipe's centre is 2500 mm over A: its underside is a little less, never more.
            $ok7 = -not $r7.isError -and $hung -and [string]$hung.sampling -eq 'along_location_curve' -and [int]$hung.measured -ge 1 -and
                   [double]$hung.coverage -eq 1 -and [long]$hung.governing.surface.element_id -eq $floorA -and
                   $null -ne $hung.min_clear_mm -and [double]$hung.min_clear_mm -gt 2400 -and [double]$hung.min_clear_mm -lt 2500 -and $hung.outcome -eq 'passes'
            Case $catalog[6] $tools[6] $(if ($ok7) { 'pass' } else { 'fail' }) $(if ($hung) { (Describe $hung) + " sampling=$($hung.sampling)" } else { Short $r7 })
        }

        # ==== 8: the pipe inside C - inside_target, not a clear height to C's own underside =========
        if (-not ($pipeIn -and $viewId)) { Case $catalog[7] $tools[7] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $r8 = Headroom $pipeIn 'down' 1000
            $inRow = RowOf $r8 $pipeIn
            $ok8 = -not $r8.isError -and $inRow -and $inRow.outcome -eq 'not_measured' -and [int]$inRow.measured -eq 0 -and [int]$inRow.inside_target -ge 1
            Case $catalog[7] $tools[7] $(if ($ok8) { 'pass' } else { 'fail' }) $(if ($inRow) { (Describe $inRow) + " inside_target=$($inRow.inside_target)" } else { Short $r8 })
        }

        # ==== 6: cleanup, newest first (pipes, view, floors, floor type, levels) ====================
        $ids = @($created | ForEach-Object { [long]$_ })
        if ($ids.Count -eq 0) { Case $catalog[5] $tools[5] 'unverified' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-hdr-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[5] $tools[5] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[5] $tools[5] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        return $cases
    }
}
