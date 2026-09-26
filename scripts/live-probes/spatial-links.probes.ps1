# Live probe module: the spatial check across a LOADED REVIT LINK (see README.md).
# Links a scratch COPY of the write document into itself (same file content, other
# path - the original is never touched), picks one wall that lives in the link, and
# creates an own host wall straight across it on an own level at that wall's base.
# The create reply's spatial_check must name the linked wall (source=link); the check
# must list the link as examined. Everything created - wall, level, link type - is
# deleted afterwards.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'spatial-links'
    Catalog = @(
        @{ Name = 'spatial-links: a host wall crossing a LINKED wall comes back with a finding naming the link'; Tool = 'horizun_create_elements' }
        @{ Name = 'spatial-links: the check reports the loaded link as examined';                          Tool = 'horizun_create_elements' }
        @{ Name = 'spatial-links: the link, the wall and the level are deleted afterwards';                Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'spatial-links' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        # $Ctx.WriteGate TRUE means the write tier is CLOSED.
        if ($Ctx.WriteGate) { foreach ($i in 0..2) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        $linkTypeId = $null
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        try {
            $h = & $Ctx.Call 'horizun_health' @{}
            $me = @($h.data.open_documents | Where-Object { $_.title -eq $doc }) | Select-Object -First 1
            if (-not $me -or -not $me.path -or -not (Test-Path -LiteralPath ([string]$me.path))) {
                foreach ($i in 0..1) { Case $i 'not_covered' ("the write document's path is not readable from health: " + $me.path) }
                throw 'HZ_STOP'
            }
            New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
            $src = Join-Path $Ctx.ScratchRoot ('HZ_LINKSRC_' + ($run -replace '[^A-Za-z0-9]', '') + '.rvt')
            Copy-Item -LiteralPath ([string]$me.path) -Destination $src -Force
            $add = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; target_document = $doc; path = $src.Replace([char]92, '/') } ($run + '-sl-add')
            if ($add.stage -ne 'apply' -or $add.answer.isError) { foreach ($i in 0..1) { Case $i 'unverified' ('the link could not be added: ' + (Short $add.answer)) }; throw 'HZ_STOP' }
            $linkTypeId = [long]$add.answer.data.link_type_id

            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); include_links = $true; include_bounding_box = $true; max_rows = 400 }
            $linkWall = @($q.data.rows | Where-Object { $_.source_kind -eq 'link' -and -not $_.is_element_type -and $_.bounding_box } |
                Where-Object { ([double]$_.bounding_box.max[0] - [double]$_.bounding_box.min[0]) -gt 1500 -or ([double]$_.bounding_box.max[1] - [double]$_.bounding_box.min[1]) -gt 1500 }) | Select-Object -First 1
            if (-not $linkWall) { foreach ($i in 0..1) { Case $i 'not_covered' 'the linked copy exposes no wall with a readable bounding box' }; throw 'HZ_STOP' }
            $bb = $linkWall.bounding_box
            $cx = ([double]$bb.min[0] + [double]$bb.max[0]) / 2; $cy = ([double]$bb.min[1] + [double]$bb.max[1]) / 2; $z = [double]$bb.min[2]
            $alongX = ([double]$bb.max[0] - [double]$bb.min[0]) -ge ([double]$bb.max[1] - [double]$bb.min[1])
            # Across the linked wall: perpendicular to its long side, 3 m each way.
            $start = if ($alongX) { @($cx, ($cy - 3000), $z) } else { @(($cx - 3000), $cy, $z) }
            $end = if ($alongX) { @($cx, ($cy + 3000), $z) } else { @(($cx + 3000), $cy, $z) }
            $lv = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = "HZ_SL_$run"; elevation = $z }) } ($run + '-sl-level')
            $levelId = if ($lv.stage -eq 'apply' -and -not $lv.answer.isError) { [long]@($lv.answer.data.rows)[0].element_id } else { $null }
            if ($levelId) { [void]$created.Add($levelId) }
            $w = if ($levelId) { & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'wall'; start = $start; end = $end; level_id = $levelId; height = 2500 }) } ($run + '-sl-wall') } else { $null }
            if (-not $w -or $w.stage -ne 'apply' -or $w.answer.isError) { foreach ($i in 0..1) { Case $i 'unverified' ('the crossing wall could not be created: ' + (Short $w.answer)) }; throw 'HZ_STOP' }
            [void]$created.Add([long]@($w.answer.data.rows)[0].element_id)
            $sc = $w.answer.data.spatial_check
            $hit = @($sc.findings | Where-Object { $_.b.source -eq 'link' })
            Case 0 $(if ($hit.Count -ge 1) { 'pass' } else { 'fail' }) ('status=' + $sc.status + ' findings=' + @($sc.findings).Count + ' link finding=' + $hit[0].reason + ' linked wall=' + $linkWall.element_id)
            Case 1 $(if ([int]$sc.links_examined -ge 1) { 'pass' } else { 'fail' }) ('links_examined=' + $sc.links_examined + ' link_neighbours=' + $sc.link_neighbours_examined + ' skipped=' + (@($sc.links_skipped) -join ','))
        }
        catch { if ([string]$_ -ne 'HZ_STOP') { foreach ($i in 0..1) { if (-not @($out | Where-Object { $_.Name -eq $names[$i].Name }).Count) { Case $i 'unverified' ('probe error: ' + $_) } } } }
        finally {
            $ids = @($created.ToArray()); [array]::Reverse($ids)
            if ($linkTypeId) { $ids += $linkTypeId }
            if ($ids.Count -eq 0) { Case 2 'not_covered' 'nothing was created' }
            else {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 50 } ($run + '-sl-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 2 'pass' ('deleted ' + ($ids -join ',')) }
                else { Case 2 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $out.ToArray()
    }
}
