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
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $catalog = @($script:HzProbeModules | Where-Object { $_.Name -eq 'views-schedules-dwg' })[0].Catalog
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

        $dup = $null; $f1 = $null; $f2 = $null; $tpl = $null
        if (-not $plan) {
            for ($i = 0; $i -le 6; $i++) { Out-Case $i 'unverified' 'the fixture holds no non-template floor plan to duplicate' }
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
                $o = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                        @{ operation = 'order_filters'; view_id = $dup; filter_ids = @($f2, $f1) }
                        @{ operation = 'apply_filter'; view_id = $dup; filter_id = $f2; enabled = $false }) } 'vg-order'
                if (Applied $o) {
                    $order = @(@($o.answer.data.rows)[0].graphics.order) -join ','
                    if ($order -eq ("{0},{1}" -f $f2, $f1)) { Out-Case 2 'pass' ('order ' + $order + ' re-read; f2 disabled and re-read') }
                    else { Out-Case 2 'fail' ('order re-read as ' + $order) }
                }
                else { Out-Case 2 'fail' (Short $o.answer) }

                # ---- precedence report (a READ: the rehearsal carries it) -------------
                if (-not $wall) { Out-Case 3 'unverified' 'the fixture holds no wall to explain' }
                else {
                    $x = & $Ctx.Call 'horizun_manage_views' @{ target_document = $doc; dry_run = $true; actions = @(
                            @{ operation = 'explain_graphics'; view_id = $dup; element_ids = @([long]$wall.element_id) }) }
                    $report = if ($x.data) { @(@($x.data.plan)[0].report)[0] } else { $null }
                    $filters = if ($report) { @($report.layers | Where-Object { $_.source -eq 'filter' }) } else { @() }
                    if ($report -and $report.winners -and $filters.Count -eq 2 -and $filters[0].filter_id -eq $f2 -and $filters[0].enabled -eq $false) {
                        Out-Case 3 'pass' ("line_color from {0}; visible decided by '{1}'" -f $report.winners.line_color.from, $report.winners.visible.decided_by)
                    }
                    else { Out-Case 3 'fail' ('report missing or wrong: ' + (Short $x)) }
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
            }
            else { for ($i = 1; $i -le 6; $i++) { Out-Case $i 'unverified' 'the own view was not created' } }
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
        $d1 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg_layers'; output_path = $json1; dwg_setup = @{ name = $setup } } 'vg-dwg-read'
        $setupId = $null
        if ((Applied $d1) -and (Test-Path -LiteralPath $json1)) {
            $setupId = [long]$d1.answer.data.setup_id; $created.Add($setupId)
            $rows = @((Get-Content -LiteralPath $json1 -Raw | ConvertFrom-Json).rows)
            if ($rows.Count -gt 0) { Out-Case 9 'pass' ("setup {0} created; {1} layer rows in the json" -f $setupId, $rows.Count) }
            else { Out-Case 9 'fail' 'the json holds no layer rows' }
        }
        else { Out-Case 9 'fail' (Short $d1.answer) }

        if ($setupId) {
            $json2 = Join-Path $Ctx.ScratchRoot "hz-dwg-layers-$tag-write.json"
            $d2 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg_layers'; output_path = $json2
                    dwg_setup = @{ name = $setup; layers = @(@{ category = 'OST_Walls'; layer = "HZ-WALLS-$tag"; color = 3 }) } } 'vg-dwg-write'
            if ((Applied $d2) -and @($d2.answer.data.edits | Where-Object { $_.persisted -eq $true }).Count -eq 1) {
                Out-Case 10 'pass' ("MEASURED Revit {0}: the layer row written is read back from a fresh lookup after the commit" -f $Ctx.Year)
            }
            else { Out-Case 10 'fail' ("MEASURED Revit {0}: not persisted or refused - {1}" -f $Ctx.Year, (Short $d2.answer)) }
        }
        else { Out-Case 10 'unverified' 'no setup to write into' }

        if ($setupId -and $dup) {
            $dwg = Join-Path $Ctx.ScratchRoot "hz-vg-$tag.dwg"
            $d3 = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg'; output_path = $dwg; view_ids = @($dup); dwg_setup = @{ name = $setup } } 'vg-dwg-export'
            if ((Applied $d3) -and [int]$d3.answer.data.files_verified -ge 1 -and (Test-Path -LiteralPath $dwg)) {
                Out-Case 11 'pass' ("{0} bytes exported with setup {1}" -f (Get-Item -LiteralPath $dwg).Length, $setup)
            }
            else { Out-Case 11 'fail' (Short $d3.answer) }
        }
        else { Out-Case 11 'unverified' 'needs the own view and the setup' }

        # ---- cleanup ------------------------------------------------------------------------
        if ($created.Count -eq 0) { Out-Case 12 'unverified' 'nothing was created' }
        else {
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created.ToArray()); id_cap = 50 } 'vg-cleanup'
            if (Applied $del) { Out-Case 12 'pass' ('deleted ' + ($created.ToArray() -join ',')) }
            else { Out-Case 12 'fail' ('left behind ' + ($created.ToArray() -join ',') + ': ' + (Short $del.answer)) }
        }
        return $cases.ToArray()
    }
}
