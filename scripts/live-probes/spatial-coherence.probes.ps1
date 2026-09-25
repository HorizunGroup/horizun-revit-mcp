# Live probe module: the spatial coherence check after writes + horizun_verify_changes
# (see README.md). Stages, on an OWN level far from the fixture: a basic wall with a
# door in it, then a structural column placed IN the doorway - the field defect that
# motivated the check (every postcondition true, column and door in the same place).
# The column's own create reply must carry spatial_check with an error naming the
# door; horizun_verify_changes must say the same, return an image, and leave no view
# behind; a column far from everything must come back without findings. Everything
# created is deleted.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'spatial-coherence'
    Catalog = @(
        @{ Name = 'spatial: a column placed in a doorway comes back with an error finding naming the door'; Tool = 'horizun_create_elements' }
        @{ Name = 'spatial: verify_changes on the last write reports the conflict and returns an image';    Tool = 'horizun_verify_changes' }
        @{ Name = 'spatial: verify_changes leaves the model as it was (no view survives)';                  Tool = 'horizun_verify_changes' }
        @{ Name = 'spatial: a column away from everything carries no finding';                            Tool = 'horizun_create_elements' }
        @{ Name = 'spatial: a column in front of a door (not touching it) is flagged as blocking the passage'; Tool = 'horizun_create_elements' }
        @{ Name = 'spatial: everything the probe created is deleted';                                     Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'spatial-coherence' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        # $Ctx.WriteGate TRUE means the write tier is CLOSED (verify-live keeps the reason).
        if ($Ctx.WriteGate) { foreach ($i in 0..5) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-sc-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $ids = @($r.answer.data.rows | ForEach-Object { [long]$_.element_id })
                foreach ($id in $ids) { [void]$created.Add($id) }
                return @{ ids = $ids; reply = $r }
            }
            return @{ ids = @(); reply = $r }
        }
        function ViewCount {
            $v = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Views'; max_rows = 1; include_links = $false }
            if ($v.data -and $null -ne $v.data.total) { return [int]$v.data.total }
            if ($v.data -and $null -ne $v.data.count) { return [int]$v.data.count }
            return $null
        }

        try {
            $E = 120000.0; $X = 650000.0; $Y = 0.0
            $lv = Create @(@{ kind = 'level'; name = "HZ_SPATIAL_$run"; elevation = $E }) 'level'
            $levelId = if ($lv.ids.Count -gt 0) { $lv.ids[0] } else { $null }
            $basic = @(Types 'OST_Walls' | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
            $tplRoot = 'C:\ProgramData\Autodesk\RVT ' + $Ctx.Year + '\Templates'
            $tpl = @('English\DefaultMetric.rte', 'Default_M_ENU.rte', 'English-Imperial\Default-Multi-Discipline.rte', 'English\Default-Multi-Discipline_Metric.rte') |
                ForEach-Object { Join-Path $tplRoot $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            $door = Types 'OST_Doors' | Select-Object -First 1
            $doorWhy = ''
            if (-not $door) {
                # The write fixture carries no door family. Bring ONE door type from the
                # Autodesk template of this very Revit (copy_between_documents source_path:
                # opened in the background, never upgraded, closed without saving), learning
                # its exact name from the refusal that lists what the template holds.
                if (-not $tpl) { $doorWhy = ' no Autodesk template found under ' + $tplRoot }
                if ($tpl) {
                    $probe = & $Ctx.Call 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl; category = 'OST_Doors'; type_names = @('__hz_probe_no_such_type__') }
                    $listed = [regex]::Match([string]$probe.text, 'Types there[^:]*:\s*(.+)$', 'Singleline')
                    $name = if ($listed.Success) { (($listed.Groups[1].Value -split ' \| ')[0] -replace '\s*(\.\.\.)?\.?\s*$', '').Trim() } else { $null }
                    if (-not $name) { $doorWhy = ' the template listed no door type: ' + (Short $probe) }
                    if ($name) {
                        $cp = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl; category = 'OST_Doors'; type_names = @($name); duplicate_types = 'use_destination' } ($run + '-sc-doortype')
                        $door = Types 'OST_Doors' | Select-Object -First 1
                        if (-not $door) { $doorWhy = " copying '$name' from $tpl did not give a door type: stage=" + $cp.stage + ' ' + (Short $cp.answer) }
                        if ($door) { [void]$created.Add([long]$door.element_id) }
                    }
                }
            }
            # An ARCHITECTURAL column from this Revit's own template, placed with an explicit
            # height. MEASURED 2026-09-25: the structural column type the fixture happened to
            # carry (loaded by an earlier module) produced no usable solid at the probe's level,
            # so the check had nothing to find - the probe must bring a column it knows.
            $column = $null
            if ($tpl) {
                $cprobe = & $Ctx.Call 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl; category = 'OST_Columns'; type_names = @('__hz_probe_no_such_type__') }
                $clist = [regex]::Match([string]$cprobe.text, 'Types there[^:]*:\s*(.+)$', 'Singleline')
                $cname = if ($clist.Success) { (($clist.Groups[1].Value -split ' \| ')[0] -replace '\s*(\.\.\.)?\.?\s*$', '').Trim() } else { $null }
                if ($cname) {
                    $null = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl; category = 'OST_Columns'; type_names = @($cname); duplicate_types = 'use_destination' } ($run + '-sc-coltype')
                    $column = @(Types 'OST_Columns' | Where-Object { ($_.family + ': ' + $_.type) -eq $cname -or $_.type -eq $cname }) | Select-Object -First 1
                    if ($column) { [void]$created.Add([long]$column.element_id) }
                }
            }
            if (-not $levelId -or -not $basic -or -not $door -or -not $column) {
                $why = "the fixture lacks what the probe stages (level=$levelId, basic wall=" + $basic.element_id + ', door=' + $door.element_id + ', column=' + $column.element_id + ')' + $doorWhy
                foreach ($i in 0..4) { Case $i 'not_covered' $why }
            }
            else {
                $w = Create @(@{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 6000), $Y, $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }) 'wall'
                $d = if ($w.ids.Count -gt 0) { Create @(@{ kind = 'family_instance'; type_id = $door.element_id; point = @(($X + 3000), $Y, $E); coordinate_mode = 'absolute'; level_id = $levelId; host_id = $w.ids[0] }) 'door' } else { @{ ids = @() } }
                if ($d.ids.Count -eq 0) {
                    foreach ($i in 0..4) { Case $i 'unverified' ('the wall and door could not be staged: ' + (Short $w.reply.answer) + ' | ' + (Short $d.reply.answer)) }
                }
                else {
                    $doorId = [long]$d.ids[0]
                    # 1. the column in the doorway
                    $c = Create @(@{ kind = 'family_instance'; type_id = $column.element_id; coordinate_mode = 'absolute'; height = 3000; point = @(($X + 3000), $Y, $E); level_id = $levelId }) 'column-in-door'
                    $sc = if ($c.reply.answer.data) { $c.reply.answer.data.spatial_check } else { $null }
                    $hit = @($sc.findings | Where-Object { $_.severity -eq 'error' -and ([long]$_.a.id -eq $doorId -or [long]$_.b.id -eq $doorId) })
                    if ($c.ids.Count -eq 0) { Case 0 'unverified' ('the column could not be placed: ' + (Short $c.reply.answer)) }
                    else {
                        Case 0 $(if ($hit.Count -ge 1 -and $c.reply.answer.data.attention) { 'pass' } else { 'fail' }) ('status=' + $sc.status + ' errors=' + $sc.errors + ' door=' + $doorId + ' finding=' + $hit[0].reason + ' attention=' + [string]$c.reply.answer.data.attention)
                    }

                    # 2 + 3. verify_changes on the last write, and nothing left behind
                    $before = ViewCount
                    $vc = & $Ctx.Call 'horizun_verify_changes' @{ target_document = $doc; pixel_size = 800 }
                    $after = ViewCount
                    $vhit = @($vc.data.spatial_check.findings | Where-Object { $_.severity -eq 'error' -and ([long]$_.a.id -eq $doorId -or [long]$_.b.id -eq $doorId) })
                    $img = $vc.data.image
                    $okImg = $img.captured -eq $true -and $vc.data.image_path -and (Test-Path -LiteralPath ([string]$vc.data.image_path))
                    Case 1 $(if (-not $vc.isError -and $vc.data.scope.source -eq 'last_write' -and $vhit.Count -ge 1 -and $okImg) { 'pass' } else { 'fail' }) ('error=' + $vc.isError + ' source=' + $vc.data.scope.source + ' errors=' + $vc.data.spatial_check.errors + ' image=' + $img.captured + ' path=' + $vc.data.image_path + ' ' + (Short $vc))
                    $rolled = [string]$img.temporary_view_rollback
                    if ($null -eq $before -or $null -eq $after) { Case 2 'unverified' ('the view count could not be read (before=' + $before + ', after=' + $after + '); rollback=' + $rolled) }
                    else { Case 2 $(if ($before -eq $after -and $rolled -eq 'RolledBack') { 'pass' } else { 'fail' }) ("views before=$before after=$after rollback=$rolled") }

                    # 4. a column far from everything
                    $far = Create @(@{ kind = 'family_instance'; type_id = $column.element_id; coordinate_mode = 'absolute'; height = 3000; point = @(($X + 20000), ($Y + 20000), $E); level_id = $levelId }) 'column-clear'
                    $fsc = if ($far.reply.answer.data) { $far.reply.answer.data.spatial_check } else { $null }
                    if ($far.ids.Count -eq 0) { Case 3 'unverified' ('the clear column could not be placed: ' + (Short $far.reply.answer)) }
                    else { Case 3 $(if ($fsc -and [int]$fsc.errors -eq 0 -and [int]$fsc.warnings -eq 0 -and -not $far.reply.answer.data.attention) { 'pass' } else { 'fail' }) ('status=' + $fsc.status + ' errors=' + $fsc.errors + ' warnings=' + $fsc.warnings) }

                    # 5. a column 700 mm in front of the door: no shared solid, still in the way
                    $front = Create @(@{ kind = 'family_instance'; type_id = $column.element_id; coordinate_mode = 'absolute'; height = 3000; point = @(($X + 3000), ($Y + 700), $E); level_id = $levelId }) 'column-front'
                    $psc = if ($front.reply.answer.data) { $front.reply.answer.data.spatial_check } else { $null }
                    $pass = @($psc.findings | Where-Object { $_.severity -eq 'error' -and ([long]$_.a.id -eq $doorId -or [long]$_.b.id -eq $doorId) -and [string]$_.reason -match 'passage' })
                    if ($front.ids.Count -eq 0) { Case 4 'unverified' ('the front column could not be placed: ' + (Short $front.reply.answer)) }
                    else { Case 4 $(if ($pass.Count -ge 1) { 'pass' } else { 'fail' }) ('status=' + $psc.status + ' errors=' + $psc.errors + ' finding=' + $pass[0].reason) }
                }
            }
        }
        finally {
            $ids = @($created.ToArray())
            if ($ids.Count -eq 0) { Case 5 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-sc-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 5 'pass' ('deleted ' + $ids.Count + ' created ids') }
                else { Case 5 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $out.ToArray()
    }
}
