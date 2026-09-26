# Live probe module: horizun_coordination operation=import_navisworks / show, feeding
# horizun_resolve_clash. Needs NO Navisworks: stages two OWN crossing pipes (see
# clash-resolve.probes.ps1), writes a SYNTHETIC coordination_handoff.json naming their
# real element ids and a source_file that only matches after normalization (extension +
# "- NWC" decoration stripped), imports it (dry_run then apply), checks the finding
# landed with origin navisworks and its priority/responsible/immovable_side, creates the
# persistent coordination view (show) and re-reads it, resolves the clash and confirms
# it is gone, then deletes both pipes and the view.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'navisworks-handoff'
    Catalog = @(
        @{ Name = 'navisworks-handoff: import_navisworks dry_run reproduces the synthetic issue'; Tool = 'horizun_coordination' }
        @{ Name = 'navisworks-handoff: import_navisworks apply records the finding with origin navisworks'; Tool = 'horizun_coordination' }
        @{ Name = 'navisworks-handoff: show creates a persistent 3D view over the imported finding'; Tool = 'horizun_coordination' }
        @{ Name = 'navisworks-handoff: resolve_clash propose/apply moves the pipe and the pair disappears'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'navisworks-handoff: cleanup deletes the probe pipes and the view'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'navisworks-handoff' } | Select-Object -First 1).Catalog
        function Out-Case($i, $outcome, $detail) { @{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = $detail } }
        if ($Ctx.WriteGate) { return @(0..4 | ForEach-Object { Out-Case $_ 'not_covered' 'write tier closed' } ) }
        $doc = $Ctx.Document

        function Find-Type($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; max_rows = 500; include_links = $false }
            if (-not $q.data) { return $null }
            $t = @($q.data.rows | Where-Object { $_.is_element_type })
            if ($t.Count -gt 0) { return $t[0].element_id } else { return $null }
        }
        $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
        $level = if ($lv.data -and @($lv.data.rows).Count -gt 0) { @($lv.data.rows)[0].element_id } else { $null }
        $pipeType = Find-Type 'OST_PipeCurves'; $system = Find-Type 'OST_PipingSystem'
        if (-not $level -or -not $pipeType -or -not $system) {
            return @(0..4 | ForEach-Object { Out-Case $_ 'not_covered' "'$doc' has no level, pipe type or piping system to stage the crossing" })
        }

        $x = 540000; $y = 0; $z = 2800
        $created = @()
        function New-Pipe($s, $e, $d, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'pipe'; start = $s; end = $e; diameter = $d; level_id = [long]$level; type_id = [long]$pipeType; system_type_id = [long]$system }) } ($Ctx.RunId + '-nh-cr-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) { return [long]@($r.answer.data.rows)[0].element_id }
            return $null
        }
        $viewId = $null
        try {
            $small = New-Pipe @($x, $y, $z) @(($x + 3000), $y, $z) 50 'small'
            if ($small) { $created += $small }
            $big = New-Pipe @(($x + 1500), ($y - 1500), $z) @(($x + 1500), ($y + 1500), $z) 150 'big'
            if ($big) { $created += $big }
            if (-not $small -or -not $big) { return @(0..4 | ForEach-Object { Out-Case $_ 'unverified' 'the crossing pipes could not be staged' }) }

            # ---- synthetic coordination_handoff.json --------------------------------
            $issueId = 'HZ-NAVIS-' + $Ctx.RunId
            # Divisions precomputed: '@(' followed directly by a '/' expression is a known
            # PowerShell array-subexpression parsing trap (op_Division on an Object[]).
            $centroidX = ($x + 1500) / 1000.0
            $centroidZ = $z / 1000.0
            $handoff = @{
                schema = 'naviscoord.coordination/1'
                source = @{ tool = 'naviscoord'; profile = 'probe'; document = $doc; document_path = ''; generated_utc = (Get-Date).ToString('o') }
                totals = @{}
                root_causes = @()
                issues      = @(
                    @{
                        issue_id = $issueId; kind = 'pair'; discipline_pair = @('Mechanical', 'Structure')
                        side_a_discipline = 'Mechanical'; side_b_discipline = 'Structure'
                        representative_clash = 'c1'; clash_count = 1; severity = 8.0; priority = 'high'
                        level = 'probe'; grid = ''; centroid = @($centroidX, 0.0, $centroidZ)
                        max_penetration_mm = 25.0; responsible = 'Mechanical'; immovable_side = 'Structure'
                        root_cause = @{}; why = @(); suggested_action = 'reroute the smaller pipe'
                        score_breakdown = @{}
                        targets = @(
                            @{ side = 'a'; path_id = 'p-small'; revit_element_id = [string]$small; source_file = ($doc + ' - NWC.nwc')
                               discipline = 'Mechanical'; category = 'Pipes'; name = 'probe pipe (small)'; budget_code = ''; classification = ''; actionable_in_revit = $true }
                            @{ side = 'b'; path_id = 'p-big'; revit_element_id = [string]$big; source_file = ($doc + ' - NWC.nwc')
                               discipline = 'Structure'; category = 'Pipes'; name = 'probe pipe (big)'; budget_code = ''; classification = ''; actionable_in_revit = $true }
                        )
                    }
                )
                caveats = @()
            }
            $path = Join-Path $Ctx.ScratchRoot ('hz-navis-handoff-' + $Ctx.RunId + '.json')
            ($handoff | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $path -Encoding utf8

            # ---- 0: dry_run reproduces --------------------------------------------
            $dry = & $Ctx.Call 'horizun_coordination' @{ operation = 'import_navisworks'; target_document = $doc; path = $path; dry_run = $true }
            $dryOk = -not $dry.isError -and $dry.data -and [int]$dry.data.reproduced -ge 1 -and $dry.data.would_record -ge 1
            $cases = @()
            $cases += Out-Case 0 $(if ($dryOk) { 'pass' } else { 'fail' }) (($dry.data | ConvertTo-Json -Depth 6 -Compress) -as [string])
            if (-not $dryOk) { return $cases }

            # ---- 1: apply records with origin navisworks ---------------------------
            $ap = & $Ctx.Apply 'horizun_coordination' @{ operation = 'import_navisworks'; target_document = $doc; path = $path; dry_run = $false } ($Ctx.RunId + '-nh-import')
            $recorded = $ap.stage -eq 'apply' -and -not $ap.answer.isError -and [int]$ap.answer.data.recorded -ge 1 -and $ap.answer.data.verified_by_reread -eq $true
            $open = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; max_rows = 500 }
            $row = @($open.data.rows | Where-Object { $_.external_issue_id -eq $issueId }) | Select-Object -First 1
            $fid = if ($row) { [string]$row.finding_id } else { $null }
            $recordOk = $recorded -and $row -and $row.external_source -eq 'navisworks' -and $row.priority -eq 'high' -and $row.responsible -eq 'Mechanical'
            $cases += Out-Case 1 $(if ($recordOk) { 'pass' } else { 'fail' }) ("finding=$fid recorded=$recorded row=" + (($row | ConvertTo-Json -Depth 4 -Compress) -as [string]))
            if (-not $recordOk) { return $cases }

            # ---- 2: show creates + re-reads the view --------------------------------
            $viewName = 'Horizun - Navisworks probe ' + $Ctx.RunId
            $sv = & $Ctx.Apply 'horizun_coordination' @{ operation = 'show'; target_document = $doc; finding_ids = @($fid); view_name = $viewName } ($Ctx.RunId + '-nh-show')
            $shown = $sv.stage -eq 'apply' -and -not $sv.answer.isError -and $sv.answer.data.postconditions.all_verified -eq $true -and $sv.answer.data.view_id
            if ($shown) { $viewId = [long]$sv.answer.data.view_id }
            $cases += Out-Case 2 $(if ($shown) { 'pass' } else { 'fail' }) (($sv.answer.text -as [string]))

            # ---- 3: resolve_clash propose/apply, the pair disappears ---------------
            $prop = & $Ctx.Call 'horizun_resolve_clash' @{ operation = 'propose'; target_document = $doc; finding_ids = @($fid) }
            $propRow = @($prop.data.proposals | Where-Object { $_.finding_id -eq $fid }) | Select-Object -First 1
            $resolved = $false
            if ($propRow -and $propRow.status -eq 'proposed') {
                $proposal = @($prop.data.next_arguments.proposals | Where-Object { $_.finding_id -eq $fid }) | Select-Object -First 1
                $rap = & $Ctx.Apply 'horizun_resolve_clash' @{ operation = 'apply'; target_document = $doc
                    proposals = @(@{ finding_id = $fid; element_id = [long]$propRow.mover_id; vector_mm = @($proposal.vector_mm) }) } ($Ctx.RunId + '-nh-resolve')
                $resolved = $rap.stage -eq 'apply' -and -not $rap.answer.isError -and $rap.answer.data.postconditions.all_verified -eq $true -and
                            (@($rap.answer.data.findings_resolved_by_model) -contains $fid)
                $cases += Out-Case 3 $(if ($resolved) { 'pass' } else { 'fail' }) (($rap.answer.text -as [string]))
            }
            else {
                $cases += Out-Case 3 'fail' ('no proposal for finding ' + $fid + ': ' + (($propRow | ConvertTo-Json -Depth 6 -Compress) -as [string]))
            }
        }
        finally {
            $ids = @($created)
            if ($viewId) { $ids += $viewId }
            if ($ids.Count -gt 0) {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = @($ids); target_document = $doc; id_cap = 10 } ($Ctx.RunId + '-nh-delete')
                $gone = $del.stage -eq 'apply' -and -not $del.answer.isError
                Out-Case 4 $(if ($gone) { 'pass' } else { 'fail' }) ("deleted " + ($ids -join ','))
            }
        }
        return $cases
    }
}
