# Live probes for horizun_manage_views sheet_set_list/create/update/delete
# (PrintManager.ViewSheetSetting). Field evidence, 2026-09-25: nothing in this
# command could create or edit a print/publish set.
#
# Uses an own DUPLICATED floor plan as the set's sole member (ViewSheetSet.Views
# accepts any non-template view, not only sheets, so this needs no title block).
# Every pre-existing named sheet set is left alone: the probe counts them before
# and after and only ever touches the one it creates.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'manage-views-sheet-sets'
    Catalog = @(
        @{ Name = 'sheet_set: create with an own view and re-read its membership'; Tool = 'horizun_manage_views' }
        @{ Name = 'sheet_set: update renames it and re-reads the name'; Tool = 'horizun_manage_views' }
        @{ Name = 'sheet_set: list finds it, then delete removes it and other sets are unaffected'; Tool = 'horizun_manage_views' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $V = 'horizun_manage_views'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $doc = $Ctx.Document
        $names = @('sheet_set: create with an own view and re-read its membership',
                   'sheet_set: update renames it and re-reads the name',
                   'sheet_set: list finds it, then delete removes it and other sets are unaffected')

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $V 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }

        $tag = 'HZ_PROBE_SS_' + $Ctx.RunId
        $created = New-Object System.Collections.Generic.List[long]

        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        $plan = if ($qv.data) { @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] } else { $null }
        if (-not $plan) { foreach ($n in $names) { Case $n $V 'not_covered' 'no non-template floor plan to duplicate' }; return $cases.ToArray() }

        $dup = & $Ctx.Apply $V @{ target_document = $doc; actions = @(@{ operation = 'duplicate_view'; source_view_id = [long]$plan.view_id; duplicate_option = 'Duplicate'; name = "HZ_SS_VIEW_$tag"; key = 'v' }) } 'ss-dup-view'
        $viewId = $null
        if (Applied $dup) {
            $row = @($dup.answer.data.rows) | Select-Object -First 1
            if ($row.verified -eq $true) { $viewId = [long]$row.element_id; [void]$created.Add($viewId) }
        }
        if (-not $viewId) { foreach ($n in $names) { Case $n $V 'not_covered' ('no own view could be staged: ' + (Why $dup)) }; return $cases.ToArray() }

        $before = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'sheet_set_list' }) }
        $beforeCount = if ($before.data) { @(@($before.data.plan)[0].report).Count } else { $null }

        # ---- create ---------------------------------------------------------------
        $cr = & $Ctx.Apply $V @{ target_document = $doc; actions = @(@{ operation = 'sheet_set_create'; name = $tag; view_ids = @($viewId) }) } 'ss-create'
        $ssId = $null
        if (Applied $cr) {
            $row = @($cr.answer.data.rows) | Select-Object -First 1
            if ($row.verified -eq $true -and $row.actual_class -eq 'ViewSheetSet' -and (@($row.graphics.view_ids) -contains [long]$viewId -or @($row.graphics.view_ids) -contains [string]$viewId)) {
                $ssId = [long]$row.element_id
                Case $names[0] $V 'pass' ("sheet_set_id=$ssId view_ids=" + (@($row.graphics.view_ids) -join ','))
            } else { Case $names[0] $V 'fail' ('re-read did not match: ' + ($row | ConvertTo-Json -Compress -Depth 5)) }
        } else { Case $names[0] $V 'fail' (Why $cr) }

        if (-not $ssId) {
            foreach ($n in $names[1..2]) { Case $n $V 'not_covered' 'sheet_set_create did not produce a set to update or delete' }
        }
        else {
            # ---- update: rename, keep the same membership -------------------------
            $newName = $tag + '_R'
            $up = & $Ctx.Apply $V @{ target_document = $doc; actions = @(@{ operation = 'sheet_set_update'; sheet_set_id = $ssId; name = $newName; view_ids = @($viewId) }) } 'ss-update'
            if (Applied $up) {
                $row = @($up.answer.data.rows) | Select-Object -First 1
                $again = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'sheet_set_list' }) }
                $found = if ($again.data) { @(@($again.data.plan)[0].report) | Where-Object { [long]$_.sheet_set_id -eq $ssId } } else { $null }
                if ($row.verified -eq $true -and $found -and $found.name -eq $newName) { Case $names[1] $V 'pass' ("renamed to $newName, re-listed with that name") }
                else { Case $names[1] $V 'fail' ('rename did not re-read: ' + ($found | ConvertTo-Json -Compress -Depth 5)) }
            } else { Case $names[1] $V 'fail' (Why $up) }

            # ---- list finds it, delete removes it, other sets untouched -----------
            $lst = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'sheet_set_list' }) }
            $report = if ($lst.data) { @(@($lst.data.plan)[0].report) } else { @() }
            $mine = @($report | Where-Object { [long]$_.sheet_set_id -eq $ssId })
            $del = & $Ctx.Apply $V @{ target_document = $doc; actions = @(@{ operation = 'sheet_set_delete'; sheet_set_id = $ssId }) } 'ss-delete'
            $after = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'sheet_set_list' }) }
            $afterCount = if ($after.data) { @(@($after.data.plan)[0].report).Count } else { $null }
            $stillThere = if ($after.data) { @(@(@($after.data.plan)[0].report) | Where-Object { [long]$_.sheet_set_id -eq $ssId }).Count } else { -1 }
            if ($mine.Count -eq 1 -and (Applied $del) -and $stillThere -eq 0 -and $null -ne $beforeCount -and $afterCount -eq $beforeCount) {
                Case $names[2] $V 'pass' ("listed before delete, gone after; other sets unchanged ($beforeCount before, $afterCount after)")
            } else { Case $names[2] $V 'fail' ("mine=$($mine.Count) deleted=" + (Applied $del) + " still_there=$stillThere before=$beforeCount after=$afterCount") }
        }

        if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'ss-cleanup' }
        return $cases.ToArray()
    }
}
