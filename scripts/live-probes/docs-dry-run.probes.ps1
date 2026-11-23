# Live probe module: the three documentation defects of the 2026-09-30 dry run
# (2026-09-30), each measured on an OWN level at +30,000 mm (see README.md):
#   * horizun_verify_changes orientation=top on walls at +30 m returned a blank image with
#     captured=true. The camera now frames the elements' box; the PNG is measured.
#   * horizun_create_schedule, non-itemized OST_Walls with Type/Count/Length/Area/Volume,
#     grouped by every non-Count field (119 rows instead of 8). It now groups by Type only
#     and totals Length/Area/Volume, and re-reads both.
#   * horizun_manage_views set_crop said verified=true while the placed view looked
#     uncropped. The verdict now needs the active flag, the Crop View parameter, the box and
#     the drawn crop shape; the row reports the viewport's size against the crop.
# Everything the probe creates is deleted; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'docs-dry-run'
    Catalog = @(
        @{ Name = 'docs-dry-run: verify_changes orientation=top on walls at +30 m returns a non-blank image';                Tool = 'horizun_verify_changes' }
        @{ Name = 'docs-dry-run: a non-itemized wall schedule groups by Type only and totals Length/Area/Volume';           Tool = 'horizun_create_schedule' }
        @{ Name = 'docs-dry-run: set_crop on a placed plan verifies on the active flag, parameter, box and drawn shape';     Tool = 'horizun_manage_views' }
        @{ Name = 'docs-dry-run: everything the probe created is deleted';                                                   Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'docs-dry-run' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) { foreach ($i in 0..3) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }

        try {
            $E = 30000.0; $X = 820000.0; $Y = 0.0
            $lv = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = "HZ_DOCS_$run"; elevation = $E }) } ($run + '-dd-level')
            $levelId = if (Applied $lv) { [long]@($lv.answer.data.rows)[0].element_id } else { $null }
            if ($levelId) { [void]$created.Add($levelId) }
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); include_types = $true; include_links = $false; max_rows = 500 }
            $basic = @(@($q.data.rows) | Where-Object { $_.is_element_type -and -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
            $wallIds = @()
            if ($levelId -and $basic) {
                $w = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(
                        @{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 8000), $Y, $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 },
                        @{ kind = 'wall'; start = @(($X + 8000), $Y, $E); end = @(($X + 8000), ($Y + 6000), $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }) } ($run + '-dd-walls')
                if (Applied $w) { $wallIds = @($w.answer.data.rows | ForEach-Object { [long]$_.element_id }); foreach ($id in $wallIds) { [void]$created.Add($id) } }
            }

            # 0. the top picture of walls 30 m up
            if ($wallIds.Count -eq 0) { Case 0 'not_covered' ("the walls at +30 m could not be staged (level=$levelId, type=" + $basic.element_id + ')') }
            else {
                $v = & $Ctx.Call 'horizun_verify_changes' @{ target_document = $doc; element_ids = $wallIds; orientation = 'top'; pixel_size = 800 }
                $img = $v.data.image
                $ok = -not $v.isError -and $img.captured -eq $true -and $img.content.measured -eq $true -and $img.content.blank -eq $false -and $img.temporary_view_rollback -eq 'RolledBack'
                Case 0 $(if ($ok) { 'pass' } else { 'fail' }) ('error=' + $v.isError + ' image=' + ($img | ConvertTo-Json -Compress -Depth 4))
            }

            # 1. the wall schedule of the dry run, by its exact field list
            $s = & $Ctx.Apply 'horizun_create_schedule' @{ target_document = $doc; category = 'OST_Walls'; name = "HZ_DOCS_WALLS_$run"
                                                          fields = @('Type', 'Count', 'Length', 'Area', 'Volume'); itemized = $false; include_links = $false } ($run + '-dd-sched')
            if (Applied $s) {
                [void]$created.Add([long]$s.answer.data.schedule_id)
                $g = $s.answer.data.grouping
                $sort = @($g.sort_group) -join ','; $totals = @($g.totals) -join ','
                $ok = $s.answer.data.postcondition.all_verified -eq $true -and $sort -eq 'Type' -and $totals -eq 'Length,Area,Volume'
                Case 1 $(if ($ok) { 'pass' } else { 'fail' }) ("sort_group=$sort totals=$totals body_rows=" + $s.answer.data.body_rows + ' postcondition=' + ($s.answer.data.postcondition.properties | Where-Object { $_.property -in @('sort_group', 'totals') } | ConvertTo-Json -Compress -Depth 5))
            }
            else { Case 1 'fail' (Short $s.answer) }

            # 2. a plan of the own level, on an own sheet, then cropped around the walls
            if (-not $levelId) { Case 2 'not_covered' 'the own level could not be staged' }
            else {
                # No view_scale: the case measures the crop, and a new plan takes its view type's default
                # template, which may control View Scale - then create_floor_plan refuses a scale up front.
                $number = 'HZD-' + ([string]$run).Substring(0, [math]::Min(8, ([string]$run).Length))
                $st = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'; actions = @(
                        @{ operation = 'create_floor_plan'; key = 'plan'; name = "HZ_DOCS_PLAN_$run"; level_id = $levelId },
                        @{ operation = 'create_sheet'; key = 'sh'; number = $number; name = 'HZ DOCS SHEET' },
                        @{ operation = 'place_view'; sheet_key = 'sh'; view_key = 'plan'; point = @(400, 300); key = 'vp' }) } ($run + '-dd-sheet')
                $planId = $null
                if (Applied $st) {
                    foreach ($r in @($st.answer.data.rows)) { if ($r.operation -in @('create_floor_plan', 'create_sheet') -and $r.element_id) { [void]$created.Add([long]$r.element_id) } }
                    $planId = [long](@($st.answer.data.rows | Where-Object { $_.operation -eq 'create_floor_plan' }) | Select-Object -First 1).element_id
                }
                if (-not $planId) { Case 2 'not_covered' ('the plan on a sheet could not be staged: ' + (Short $st.answer)) }
                else {
                    $c = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'; actions = @(
                            @{ operation = 'set_crop'; view_id = $planId; box = @(($X - 1000), ($Y - 1000), ($X + 9000), ($Y + 7000)) }) } ($run + '-dd-crop')
                    $row = if (Applied $c) { @($c.answer.data.rows)[0] } else { $null }
                    $ok = $row -and $row.verified -eq $true -and $row.crop.verified -eq $true -and @($row.crop.failures).Count -eq 0 -and $row.crop.crop_box_active -eq $true
                    Case 2 $(if ($ok) { 'pass' } else { 'fail' }) $(if ($row) { 'crop=' + ($row.crop | ConvertTo-Json -Compress -Depth 6) } else { Short $c.answer })
                }
            }
        }
        finally {
            $ids = @($created.ToArray())
            if ($ids.Count -eq 0) { Case 3 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-dd-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 3 'pass' ('deleted ' + $ids.Count + ' created ids') }
                else { Case 3 'fail' ('left behind: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $out.ToArray()
    }
}
