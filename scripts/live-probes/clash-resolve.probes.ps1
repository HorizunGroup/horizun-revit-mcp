# Live probe module: horizun_resolve_clash + horizun_undo (see README.md).
# Stages two OWN pipes crossing at the same elevation (50 mm along X, 150 mm along Y,
# far from any building), records the clash in the ledger, proposes, applies (the
# smaller pipe must move and the pair must vanish), undoes (the clash must come BACK
# as a regression), and deletes both pipes.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'clash-resolve'
    Catalog = @(
        @{ Name = 'clash-resolve: propose moves the smaller unconnected pipe with a verifiable prediction'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'clash-resolve: apply keeps the move only after solid re-detection, finding resolved_by_model'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'clash-resolve: horizun_clash no longer sees the pair after apply'; Tool = 'horizun_clash' }
        @{ Name = 'clash-resolve: undo_last restores the pipe and the clash returns as a regression'; Tool = 'horizun_undo' }
        @{ Name = 'clash-resolve: the probe pipes are deleted afterwards'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = @()
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'clash-resolve' } | Select-Object -First 1).Catalog
        function Out-Case($i, $outcome, $detail) { @{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = $detail } }
        if (-not $Ctx.WriteGate) { return @(0..4 | ForEach-Object { Out-Case $_ 'not_covered' 'write tier not enabled' }) }
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

        $x = 520000; $y = 0; $z = 2800
        $created = @()
        function New-Pipe($s, $e, $d, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'pipe'; start = $s; end = $e; diameter = $d; level_id = [long]$level; type_id = [long]$pipeType; system_type_id = [long]$system }) } ($Ctx.RunId + '-cr-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) { return [long]@($r.answer.data.rows)[0].element_id }
            return $null
        }
        try {
            $small = New-Pipe @($x, $y, $z) @(($x + 3000), $y, $z) 50 'small'
            if ($small) { $created += $small }
            $big = New-Pipe @(($x + 1500), ($y - 1500), $z) @(($x + 1500), ($y + 1500), $z) 150 'big'
            if ($big) { $created += $big }
            if (-not $small -or -not $big) { return @(0..4 | ForEach-Object { Out-Case $_ 'unverified' 'the crossing pipes could not be staged' }) }

            $clashArgs = @{ categories_a = @('OST_PipeCurves'); categories_b = @('OST_PipeCurves'); include_links = $false; record_findings = $true; max_results = 500 }
            $null = & $Ctx.Call 'horizun_clash' $clashArgs
            $open = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; status = 'open'; max_rows = 500 }
            $ids = @($open.data.rows | ForEach-Object { [string]$_.finding_id })
            $row = $null; $proposal = $null
            for ($i = 0; $i -lt $ids.Count -and -not $row; $i += 50) {
                $chunk = @($ids[$i..([Math]::Min($i + 49, $ids.Count - 1))])
                $p = & $Ctx.Call 'horizun_resolve_clash' @{ operation = 'propose'; target_document = $doc; finding_ids = $chunk }
                $row = @($p.data.proposals | Where-Object { [long]$_.mover_id -eq $small -and [long]$_.fixed_id -eq $big }) | Select-Object -First 1
                if ($row) { $proposal = @($p.data.next_arguments.proposals | Where-Object { $_.finding_id -eq $row.finding_id }) | Select-Object -First 1 }
            }
            if ($row -and $row.status -eq 'proposed' -and $proposal -and $row.prediction) {
                $cases += Out-Case 0 'pass' ("finding $($row.finding_id): $($row.kind) $($row.distance_mm) mm on $small")
            } else {
                $cases += Out-Case 0 'fail' ('no proposal moved the 50 mm pipe: ' + (($row | ConvertTo-Json -Depth 6 -Compress) -as [string]))
                return $cases
            }
            $fid = [string]$row.finding_id

            $ap = & $Ctx.Apply 'horizun_resolve_clash' @{ operation = 'apply'; target_document = $doc; proposals = @(@{ finding_id = $fid; element_id = $small; vector_mm = @($proposal.vector_mm) }) } ($Ctx.RunId + '-cr-apply')
            $ok = $ap.stage -eq 'apply' -and -not $ap.answer.isError -and $ap.answer.data.postconditions.all_verified -eq $true -and
                  (@($ap.answer.data.findings_resolved_by_model) -contains $fid) -and $ap.answer.data.undo.recorded -eq $true
            $cases += Out-Case 1 $(if ($ok) { 'pass' } else { 'fail' }) ($(if ($ap.answer) { $ap.answer.text } else { $ap.stage }) -as [string])
            if (-not $ok) { return $cases }

            $null = & $Ctx.Call 'horizun_clash' $clashArgs
            $all = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; max_rows = 500 }
            $f = @($all.data.rows | Where-Object { $_.finding_id -eq $fid }) | Select-Object -First 1
            $cases += Out-Case 2 $(if ($f -and $f.status -eq 'resolved_by_model') { 'pass' } else { 'fail' }) ("finding status after re-run: " + $f.status)

            $un = & $Ctx.Apply 'horizun_undo' @{ operation = 'undo_last'; target_document = $doc } ($Ctx.RunId + '-cr-undo')
            $undone = $un.stage -eq 'apply' -and -not $un.answer.isError -and $un.answer.data.postconditions.all_verified -eq $true
            $back = & $Ctx.Call 'horizun_clash' $clashArgs
            $all2 = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; max_rows = 500 }
            $f2 = @($all2.data.rows | Where-Object { $_.finding_id -eq $fid }) | Select-Object -First 1
            $regressed = $f2 -and $f2.status -eq 'open' -and $f2.regression -eq $true -and [int]$back.data.findings.regressions -ge 1
            $cases += Out-Case 3 $(if ($undone -and $regressed) { 'pass' } else { 'fail' }) ("undo: " + ($un.answer.text -as [string]) + " | finding: " + $f2.status + " regression=" + $f2.regression)
        }
        finally {
            if ($created.Count -gt 0) {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = @($created); target_document = $doc; id_cap = 10 } ($Ctx.RunId + '-cr-delete')
                $gone = $del.stage -eq 'apply' -and -not $del.answer.isError
                # Emitted, not appended: a `return` inside try has already written $cases.
                Out-Case 4 $(if ($gone) { 'pass' } else { 'fail' }) ("deleted " + ($created -join ','))
            }
        }
        return $cases
    }
}
