#Requires -Version 5.1
# Live probe module: view filters and their precedence, category V/G, view templates,
# multi-category and key schedules, and the DWG export layer table.
#
# Everything is created by this module in the disposable document and deleted at the
# end; nothing that was there before is touched except by being READ (a floor plan
# to duplicate, a wall to explain). The DWG layer-table write is a MEASUREMENT: the
# case records, per Revit year, whether a fresh lookup after the commit still holds
# the row that was written.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'views-schedules-dwg'
    Catalog = @(
        @{ Name = 'views-vg: create two filters on an own duplicated view'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: edit a filter rule and re-read it'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: reorder and disable filters, re-read order and state'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: precedence report for one element'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: category V/G hide and override on an own view'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: template create, govern V/G, apply, remove'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: V/G on a template-governed view refuses naming the template'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: multi-category schedule create and read'; Tool = 'horizun_create_schedule' }
        @{ Name = 'views-vg: key schedule with key rows create and read'; Tool = 'horizun_create_schedule' }
        @{ Name = 'views-vg: DWG setup create and layer table read to json'; Tool = 'horizun_export' }
        @{ Name = 'views-vg: DWG layer table write persists (measured per year)'; Tool = 'horizun_export' }
        @{ Name = 'views-vg: DWG export with the named setup to ScratchRoot'; Tool = 'horizun_export' }
        @{ Name = 'views-vg: cleanup deletes everything the module created'; Tool = 'horizun_delete_verified' }
        # Appended, not inserted: every Out-Case above is called by its FIXED
        # numeric index, so these three live at the end regardless of where their
        # own logic runs in the script.
        @{ Name = 'views-vg: color_by_value re-reads each legend value''s override colour'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: hide_elements (temporary) re-reads exactly the requested id''s visibility'; Tool = 'horizun_manage_views' }
        @{ Name = 'views-vg: re-exporting the same DWG with overwrite still counts exactly one produced file'; Tool = 'horizun_export' }
    )
    # MEASURED (2026-09-24, Revit 2026): a DUPLICATED view carries the source view's
    # filters - Revit copies them - so the own view held five filters, not two, and
    # order_filters (which takes the WHOLE list, by contract) refused the pair. The
    # full order is read first; the module's own filters are rearranged among the
    # positions they already occupy, and every inherited filter stays where it was.
    # Returns $null when one of $mine is not on the view.
    FilterOrder = {
        param([long[]]$current, [long[]]$mine)
        $cur = @($current); $own = @($mine)
        foreach ($m in $own) { if ($cur -notcontains $m) { return $null } }
        $result = New-Object System.Collections.Generic.List[long]
        $next = 0
        foreach ($id in $cur) {
            if ($own -contains $id) { $result.Add([long]$own[$next]); $next++ }
            else { $result.Add([long]$id) }
        }
        return , $result.ToArray()
    }
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $self = @($script:HzProbeModules | Where-Object { $_.Name -eq 'views-schedules-dwg' })[0]
        $catalog = $self.Catalog
        $filterOrder = $self.FilterOrder
        function Out-Case($i, $outcome, $detail) {
            $cases.Add(@{ Name = $catalog[$i].Name; Tool = $catalog[$i].Tool; Outcome = $outcome; Detail = [string]$detail })
        }
        function Short($answer) {
            if ($null -eq $answer) { return 'no answer' }
            $t = [string]$answer.text
            if ($t.Length -gt 300) { $t = $t.Substring(0, 300) + '...' }
            return $t
        }
        function Applied($r) { return ($r -and $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) }

        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Out-Case $i 'not_covered' 'write tier closed: this module commits into the disposable document' }
            return $cases.ToArray()
        }
        $doc = $Ctx.Document
        $tag = ([string]$Ctx.RunId) -replace '[^A-Za-z0-9]', ''
        $created = New-Object System.Collections.Generic.List[long]

        # ---- discover: a floor plan to duplicate and one wall to explain -------------
        $plan = $null; $wall = $null
        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        if ($qv.data) { $plan = @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] }
        $qw = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); max_rows = 1 }
        if ($qw.data) { $wall = @($qw.data.rows)[0] }
        # STAGED WHEN THE FIXTURE HAS NO WALL. MEASURED 2026-09-26: the 2023-2027 HVAC write
        # models hold no host wall, and their plans show only linked content, so the
        # precedence report, color_by_value and hide_elements had nothing of their own to
        # act on. An own level, an own wall on it and an own plan of that level give all
        # three a wall the view really shows; they are deleted last, level after its views.
        $staged = New-Object System.Collections.Generic.List[long]
        if (-not $wall) {
            $sE = 97000.0; $sX = 990000.0
            $sl = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = "HZ_VG_LV_$tag"; elevation = $sE }) } 'vg-stage-level'
            $slId = if (Applied $sl) { [long]@($sl.answer.data.rows)[0].element_id } else { $null }
            if ($slId) {
                $sw = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'wall'; start = @($sX, 0, $sE); end = @(($sX + 4000), 0, $sE); level_id = $slId; height = 3000 }) } 'vg-stage-wall'
                $swId = if (Applied $sw) { [long]@($sw.answer.data.rows)[0].element_id } else { $null }
                $sp = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'create_floor_plan'; level_id = $slId; name = "HZ_VG_PLAN_$tag"; key = 'vgplan' }) } 'vg-stage-plan'
                $spId = if (Applied $sp -and $sp.answer.data.aliases) { [long]$sp.answer.data.aliases.vgplan } else { $null }
                foreach ($id in @($swId, $spId, $slId)) { if ($id) { $staged.Add($id) } }
                if ($swId -and $spId) {
                    $wall = [pscustomobject]@{ element_id = $swId }
                    $plan = [pscustomobject]@{ view_id = $spId; view_type = 'FloorPlan'; is_template = $false }
                }
            }
        }

        $dup = $null; $f1 = $null; $f2 = $null; $tpl = $null
        if (-not $plan) {
            for ($i = 0; $i -le 6; $i++) { Out-Case $i 'unverified' 'the fixture holds no non-template floor plan to duplicate' }
            # 13/14 (color_by_value, hide_elements) are marked later, unconditionally on
            # $dup - which stays null in this branch too, so they are covered there.
        }
        else {
            # ---- A: own view, two filters ---------------------------------------------
            $a = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                    @{ operation = 'duplicate_view'; source_view_id = [long]$plan.view_id; duplicate_option = 'WithDetailing'; name = "HZ_VG_$tag"; key = 'dup' }
                    @{ operation = 'apply_template'; view_key = 'dup'; template_view_id = -1 }
                    @{ operation = 'create_filter'; name = "HZ_F1_$tag"; categories = @('OST_Walls'); key = 'f1'
                       rules = @(@{ parameter = 'ALL_MODEL_MARK'; operator = 'equals'; value = 'HZ' }) }
                    @{ operation = 'create_filter'; name = "HZ_F2_$tag"; categories = @('OST_Walls'); key = 'f2'
                       rules = @(@{ parameter = 'ALL_MODEL_MARK'; operator = 'has_no_value' }) }
                    @{ operation = 'apply_filter'; view_key = 'dup'; filter_key = 'f1'; overrides = @{ line_color = '#FF0000' } }
                    @{ operation = 'apply_filter'; view_key = 'dup'; filter_key = 'f2'; overrides = @{ halftone = $true } }) } 'vg-create'
            if (Applied $a) {
                $dup = [long]$a.answer.data.aliases.dup; $f1 = [long]$a.answer.data.aliases.f1; $f2 = [long]$a.answer.data.aliases.f2
                foreach ($id in @($dup, $f1, $f2)) { $created.Add($id) }
                Out-Case 0 'pass' ("view {0}, filters {1} and {2}; {3} actions verified" -f $dup, $f1, $f2, $a.answer.data.actions_verified)
            }
            else { Out-Case 0 'fail' (Short $a.answer) }

            if ($dup) {
                # ---- edit the rule ----------------------------------------------------
                $e = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                        @{ operation = 'edit_filter'; filter_id = $f1; match = 'any'
                           rules = @(@{ parameter = 'ALL_MODEL_MARK'; operator = 'contains'; value = 'HZ-EDIT' }
                                     @{ parameter = 'ALL_MODEL_MARK'; operator = 'begins_with'; value = 'X' }) }) } 'vg-edit'
                $reread = if (Applied $e) { [string]@($e.answer.data.rows)[0].graphics.rules_reread } else { '' }
                if ($reread -match 'OR\(' -and $reread -match 'HZ-EDIT') { Out-Case 1 'pass' ('rules re-read: ' + $reread) }
                else { Out-Case 1 'fail' ('re-read "' + $reread + '"; ' + (Short $e.answer)) }

                # ---- reorder, then disable -------------------------------------------
                # The view's WHOLE filter list is read first (explain_graphics lists every
                # filter on the view, whatever the element); inherited filters keep their
                # places and only the module's two swap.
                $probeId = if ($wall) { [long]$wall.element_id } else { $f1 }
                $before = & $Ctx.Call 'horizun_manage_views' @{ target_document = $doc; dry_run = $true; actions = @(
                        @{ operation = 'explain_graphics'; view_id = $dup; element_ids = @($probeId) }) }
                $beforeReport = if ($before.data) { @(@($before.data.plan)[0].report)[0] } else { $null }
                $current = @(if ($beforeReport) { $beforeReport.layers | Where-Object { $_.source -eq 'filter' } | ForEach-Object { [long]$_.filter_id } })
                $wanted = & $filterOrder $current @($f2, $f1)
                if (-not $wanted) { Out-Case 2 'fail' ('could not read the view''s filter list, or it lacks the module''s filters: [' + ($current -join ',') + ']; ' + (Short $before)) }
                else {
                    $o = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                            @{ operation = 'order_filters'; view_id = $dup; filter_ids = @($wanted) }
                            @{ operation = 'apply_filter'; view_id = $dup; filter_id = $f2; enabled = $false }) } 'vg-order'
                    if (Applied $o) {
                        $order = @(@($o.answer.data.rows)[0].graphics.order) -join ','
                        $inherited = $current.Count - 2
                        if ($order -eq ($wanted -join ',')) { Out-Case 2 'pass' ("order $order re-read ($inherited inherited filters kept in place); f2 disabled and re-read") }
                        else { Out-Case 2 'fail' ('order re-read as ' + $order + ', wanted ' + ($wanted -join ',')) }
                    }
                    else { Out-Case 2 'fail' (Short $o.answer) }
                }

                # ---- precedence report (a READ: the rehearsal carries it) -------------
                # The module's filters are located BY ID: the duplicated view also holds
                # the source view's filters, so no absolute position is assumed.
                if (-not $wall) { Out-Case 3 'unverified' 'the fixture holds no wall to explain' }
                else {
                    $x = & $Ctx.Call 'horizun_manage_views' @{ target_document = $doc; dry_run = $true; actions = @(
                            @{ operation = 'explain_graphics'; view_id = $dup; element_ids = @([long]$wall.element_id) }) }
                    $report = if ($x.data) { @(@($x.data.plan)[0].report)[0] } else { $null }
                    $filters = @(if ($report) { $report.layers | Where-Object { $_.source -eq 'filter' } })
                    $ids = @($filters | ForEach-Object { [long]$_.filter_id })
                    $i1 = [array]::IndexOf($ids, [long]$f1); $i2 = [array]::IndexOf($ids, [long]$f2)
                    $ok = $report -and $report.winners -and $i1 -ge 0 -and $i2 -ge 0 -and $i2 -lt $i1 -and
                          $filters[$i2].enabled -eq $false -and $filters[$i1].enabled -eq $true
                    if ($ok) {
                        Out-Case 3 'pass' ("f2 at {0} (disabled) above f1 at {1} among {2} filters; line_color from {3}; visible decided by '{4}'" -f
                            ($i2 + 1), ($i1 + 1), $ids.Count, $report.winners.line_color.from, $report.winners.visible.decided_by)
                    }
                    else { Out-Case 3 'fail' ("report missing or wrong: f2 at $i2, f1 at $i1 among [" + ($ids -join ',') + ']; ' + (Short $x)) }
                }

                # ---- category V/G on the own view --------------------------------------
                $vg = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                        @{ operation = 'set_category_visibility'; view_id = $dup; category = 'OST_Walls'; hidden = $true
                           overrides = @{ halftone = $true; line_color = '#00AA00'; transparency = 40 } }) } 'vg-category'
                if (Applied $vg) { Out-Case 4 'pass' 'walls hidden with halftone, colour and transparency; all four re-read' }
                else { Out-Case 4 'fail' (Short $vg.answer) }

                # ---- template: create, govern V/G, apply, then refuse, then remove ------
                $t = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                        @{ operation = 'create_template'; view_id = $dup; name = "HZ_TPL_$tag"; key = 'tpl' }) } 'vg-tpl-create'
                if (Applied $t) {
                    $tpl = [long]$t.answer.data.aliases.tpl; $created.Add($tpl)
                    $g = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                            @{ operation = 'set_template_controls'; view_id = $tpl; parameters = @('VIS_GRAPHICS_MODEL'); controlled = $true }
                            @{ operation = 'apply_template'; view_id = $dup; template_view_id = $tpl }) } 'vg-tpl-apply'
                    $refuse = & $Ctx.Call 'horizun_manage_views' @{ target_document = $doc; dry_run = $true; actions = @(
                            @{ operation = 'set_category_visibility'; view_id = $dup; category = 'OST_Walls'; hidden = $false }) }
                    $refused = $refuse.isError -or ($refuse.data -and [int]$refuse.data.invalid -gt 0)
                    $names = ([string]$refuse.text) -match ("view_id=" + $tpl)
                    if ($refused -and $names) { Out-Case 6 'pass' ('refused with the alternative view_id=' + $tpl) }
                    else { Out-Case 6 'fail' ('refused=' + $refused + ' names_template=' + $names + '; ' + (Short $refuse)) }
                    $r = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                            @{ operation = 'apply_template'; view_id = $dup; template_view_id = -1 }) } 'vg-tpl-remove'
                    if ((Applied $g) -and (Applied $r)) { Out-Case 5 'pass' ("template {0} created, VIS_GRAPHICS_MODEL governed, applied and removed; each re-read" -f $tpl) }
                    else { Out-Case 5 'fail' ('govern/apply: ' + (Short $g.answer) + ' | remove: ' + (Short $r.answer)) }
                }
                else { Out-Case 5 'fail' (Short $t.answer); Out-Case 6 'unverified' 'no template was created to govern the view' }
                # color_by_value and hide_elements (cases 13/14) run LATER, right before
                # cleanup - so any filter id they create is appended to $created AFTER
                # 500 (the DWG setup), keeping that id's position in the cleanup case's
                # detail stable for anyone matching it by eye or by pattern.
            }
            else {
                for ($i = 1; $i -le 6; $i++) { Out-Case $i 'unverified' 'the own view was not created' }
                # 13/14 are marked later, unconditionally on $dup (which stays null here too).
            }
        }

        # ---- schedules ------------------------------------------------------------------
        $mc = & $Ctx.Apply 'horizun_create_schedule' @{ target_document = $doc; category = 'OST_MultiCategory'; name = "HZ_MC_$tag"
                                                         fields = @('Category', 'Family', 'Type', 'Count'); include_links = $false } 'vg-sched-multi'
        if (Applied $mc) {
            $created.Add([long]$mc.answer.data.schedule_id)
            $read = & $Ctx.Call 'horizun_get_schedule_data' @{ schedule_id = [long]$mc.answer.data.schedule_id; max_rows = 50 }
            if ([long]$mc.answer.data.category_id -eq -1 -and -not $read.isError) { Out-Case 7 'pass' ("multi-category schedule, {0} body rows; read back" -f $mc.answer.data.body_rows) }
            else { Out-Case 7 'fail' ('category_id ' + $mc.answer.data.category_id + '; read: ' + (Short $read)) }
        }
        else { Out-Case 7 'fail' (Short $mc.answer) }

        $ks = & $Ctx.Apply 'horizun_create_schedule' @{ target_document = $doc; category = 'OST_Rooms'; name = "HZ_KEY_$tag"
                                                         key_schedule = $true; key_rows = 2 } 'vg-sched-key'
        if (Applied $ks) {
            $created.Add([long]$ks.answer.data.schedule_id)
            $kr = @($ks.answer.data.postcondition.properties | Where-Object { $_.property -eq 'key_rows' })
            Out-Case 8 'pass' ('key schedule; key_rows re-read: ' + ($kr | ConvertTo-Json -Compress -Depth 4))
        }
        else { Out-Case 8 'fail' (Short $ks.answer) }

        # ---- DWG layer table ---------------------------------------------------------------
        $setup = "HZ_DWG_$tag"
        $json1 = Join-Path $Ctx.ScratchRoot "hz-dwg-layers-$tag-read.json"
        # MEASURED (2026-09-24, Revit 2026): a setup created from an empty options
        # object had an EMPTY layer table. The tool now seeds a new setup from whatever
        # really gives it rows and refuses when every seed is empty; the refusal is
        # itself a measurement, so the probe then tries the explicit AIA standard and
        # records which seed produced the table.
        $d1 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg_layers'; output_path = $json1; dwg_setup = @{ name = $setup } } 'vg-dwg-read'
        $firstRefusal = $null
        if (-not (Applied $d1) -and ([string]$d1.answer.text) -match 'empty_layer_table|EMPTY layer') {
            $firstRefusal = Short $d1.answer
            $d1 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg_layers'; output_path = $json1
                    dwg_setup = @{ name = $setup; layer_standard = 'AIA' } } 'vg-dwg-read-aia'
        }
        $setupId = $null
        if ((Applied $d1) -and (Test-Path -LiteralPath $json1)) {
            $setupId = [long]$d1.answer.data.setup_id; $created.Add($setupId)
            $rows = @((Get-Content -LiteralPath $json1 -Raw | ConvertFrom-Json).rows)
            $how = "seeded from '{0}'" -f $d1.answer.data.seeded_from
            if ($firstRefusal) { $how += '; every default seed was empty: ' + $firstRefusal }
            if ($rows.Count -gt 0) { Out-Case 9 'pass' ("MEASURED Revit {0}: setup {1} created, {2}; {3} layer rows in the json" -f $Ctx.Year, $setupId, $how, $rows.Count) }
            else { Out-Case 9 'fail' ("MEASURED Revit {0}: the json holds no layer rows ({1})" -f $Ctx.Year, $how) }
        }
        else { Out-Case 9 'fail' ("MEASURED Revit {0}: {1}" -f $Ctx.Year, (Short $d1.answer)) }

        if ($setupId) {
            $json2 = Join-Path $Ctx.ScratchRoot "hz-dwg-layers-$tag-write.json"
            $d2 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg_layers'; output_path = $json2
                    dwg_setup = @{ name = $setup; layers = @(@{ category = 'OST_Walls'; layer = "HZ-WALLS-$tag"; color = 3 }) } } 'vg-dwg-write'
            # @() around the if: an assignment unrolls a one-row array, and on Windows
            # PowerShell 5.1 a lone PSCustomObject has no Count.
            $kept = @(if (Applied $d2) { $d2.answer.data.edits | Where-Object { $_.persisted -eq $true } })
            if ($kept.Count -eq 1) {
                Out-Case 10 'pass' ("MEASURED Revit {0}: row '{1}' (named by OST_Walls, whatever Revit's language) read back from a fresh lookup after the commit" -f $Ctx.Year, $kept[0].key)
            }
            else { Out-Case 10 'fail' ("MEASURED Revit {0}: not persisted or refused - {1}" -f $Ctx.Year, (Short $d2.answer)) }
        }
        else { Out-Case 10 'unverified' 'no setup to write into' }

        if ($setupId -and $dup) {
            # The fixture's views show loaded RVT links, which Revit writes as companion xref files:
            # dwg_xrefs='bound' keeps the drawing one self-contained file, the count these cases assert.
            $dwg = Join-Path $Ctx.ScratchRoot "hz-vg-$tag.dwg"
            $d3 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg'; output_path = $dwg; view_ids = @($dup); dwg_setup = @{ name = $setup }; dwg_xrefs = 'bound' } 'vg-dwg-export'
            # The produced file is the one the reply names (file_naming may add an ordinal), not output_path.
            $dwgOut = if (Applied $d3) { [string](@($d3.answer.data.files) | Select-Object -First 1).file } else { $null }
            if ((Applied $d3) -and [int]$d3.answer.data.files_verified -ge 1 -and $dwgOut -and (Test-Path -LiteralPath $dwgOut)) {
                Out-Case 11 'pass' ("{0} bytes exported with setup {1} to {2}" -f (Get-Item -LiteralPath $dwgOut).Length, $setup, $dwgOut)
            }
            else { Out-Case 11 'fail' (Short $d3.answer) }

            # ---- re-export the SAME dwg with overwrite: exactly one produced file,
            # ---- never folded into a bare "at least one matching file" count --------
            if (Applied $d3) {
                $d4 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg'; output_path = $dwg
                        view_ids = @($dup); dwg_setup = @{ name = $setup }; dwg_xrefs = 'bound'; overwrite = $true } 'vg-dwg-reexport'
                if ((Applied $d4) -and [int]$d4.answer.data.files_verified -eq 1) {
                    Out-Case 15 'pass' ("re-export with overwrite=true still measured exactly 1 produced file (was {0} before)" -f $d4.answer.data.files_verified)
                }
                else { Out-Case 15 'fail' (Short $d4.answer) }
            }
            else { Out-Case 15 'unverified' 'the first dwg export did not succeed' }
        }
        else {
            Out-Case 11 'unverified' 'needs the own view and the setup'
            Out-Case 15 'unverified' 'needs the own view and the setup'
        }

        # ---- color_by_value and hide_elements, on the own view (run here, after every
        # ---- other id-producing case, so the new filter id lands after 500 in $created) ---
        if (-not $dup) {
            Out-Case 13 'unverified' 'the own view was not created'
            Out-Case 14 'unverified' 'the own view was not created'
        }
        else {
            # ---- color_by_value: each legend value's OVERRIDE COLOUR is re-read, not
            # ---- only that a filter got attached and made visible ---------------------
            # The legend is built from what the VIEW shows, so colour a category the own
            # view really shows (an HVAC fixture has no walls; MEASURED 2026-09-26: an empty
            # view is now refused in the rehearsal, before any token).
            # WALLS SHOWN AGAIN FIRST. Case 4 hid the Walls category on this very view (and the
            # template round trip keeps it hidden), and a hidden category hides its elements:
            # color_by_value then finds no wall to colour, and hide_elements would read a wall
            # "not visible" that its own hide did not hide (MEASURED 2026-09-26).
            $unhide = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                    @{ operation = 'set_category_visibility'; view_id = $dup; category = 'OST_Walls'; hidden = $false }) } 'vg-unhide-walls'
            $unhideNote = if (Applied $unhide) { '' } else { ' (walls could not be shown again on the own view: ' + (Short $unhide.answer) + ')' }
            $cbvCategory = $null
            foreach ($c in @('OST_Walls', 'OST_DuctCurves', 'OST_PipeCurves', 'OST_MechanicalEquipment', 'OST_Floors', 'OST_StructuralColumns', 'OST_Doors')) {
                $seen = & $Ctx.Call 'horizun_query_model' @{ target_document = $doc; scope = 'view'; view_id = $dup; categories = @($c); include_types = $false; include_links = $false; max_rows = 1 }
                if ($seen.data -and ([int]$seen.data.matched_total -gt 0 -or @($seen.data.rows).Count -gt 0)) { $cbvCategory = $c; break }
            }
            if (-not $cbvCategory) { $cbvCategory = 'OST_Walls' }
            $cbv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                    @{ operation = 'color_by_value'; view_id = $dup; categories = @($cbvCategory); parameter = 'ALL_MODEL_MARK'
                       filter_prefix = "HZ_CBV_$tag" }) } 'vg-color-by-value'
            if (Applied $cbv) {
                $row = @($cbv.answer.data.rows)[0]
                $legend = @($row.graphics.legend)
                foreach ($fid in $legend.filter_id) { $created.Add([long]$fid) }
                $ov = $row.graphics.overrides_verified
                if ($row.verified -eq $true -and $ov -and $ov.all_verified -eq $true -and $legend.Count -gt 0) {
                    Out-Case 13 'pass' ("{0} value(s) coloured, every override colour re-read from the committed view: {1}" -f
                        $legend.Count, (($ov.by_value | ForEach-Object { $_.value + '=' + $_.found_rgb }) -join '; '))
                }
                else { Out-Case 13 'fail' ('overrides_verified: ' + ($ov | ConvertTo-Json -Compress -Depth 5)) }
            }
            else { Out-Case 13 'fail' ((Short $cbv.answer) + $unhideNote) }

            # ---- hide_elements (temporary): the SPECIFIC requested id must read
            # ---- not-visible in the view's temporary mode, not only that the mode is on
            if (-not $wall) { Out-Case 14 'unverified' 'the fixture holds no wall to hide' }
            else {
                $wallId = [long]$wall.element_id
                $hide = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                        @{ operation = 'hide_elements'; view_id = $dup; element_ids = @($wallId) }) } 'vg-hide-temp'
                if (Applied $hide) {
                    $row = @($hide.answer.data.rows)[0]
                    $ev = $row.graphics.elements_verified
                    $mine = @(if ($ev) { $ev.by_element | Where-Object { [long]$_.element_id -eq $wallId } })
                    if ($row.verified -eq $true -and $ev -and $ev.all_verified -eq $true -and $mine.Count -eq 1 -and $mine[0].hidden -eq $true) {
                        Out-Case 14 'pass' ("wall {0} re-read as not-visible under TemporaryHideIsolate; elements_verified.all_verified=true" -f $wallId)
                    }
                    else { Out-Case 14 'fail' ('elements_verified: ' + ($ev | ConvertTo-Json -Compress -Depth 5)) }
                    # Undo the temporary state so this view leaves no lingering mode -
                    # deleting the view at cleanup would do this anyway, but a probe
                    # that leaves the disposable document in temporary-hide mode until
                    # then is a smaller footprint avoided for free.
                    $null = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                            @{ operation = 'reset_temporary'; view_id = $dup }) } 'vg-reset-temp'
                }
                else { Out-Case 14 'fail' (Short $hide.answer) }
            }
        }

        # ---- cleanup ------------------------------------------------------------------------
        # The staged wall, plan and level go LAST (in that order): the level would take
        # every view of it with it, and a view already gone is not "deleted" by the call.
        foreach ($id in $staged) { $created.Add($id) }
        if ($created.Count -eq 0) { Out-Case 12 'unverified' 'nothing was created' }
        else {
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created.ToArray()); id_cap = 50 } 'vg-cleanup'
            if (Applied $del) { Out-Case 12 'pass' ('deleted ' + ($created.ToArray() -join ',')) }
            else { Out-Case 12 'fail' ('left behind ' + ($created.ToArray() -join ',') + ': ' + (Short $del.answer)) }
        }
        return $cases.ToArray()
    }
}
