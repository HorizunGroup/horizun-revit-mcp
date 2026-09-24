# horizun_model_diff live probe. Staged on the disposable write document, never saved:
#   own wall A -> snapshot -> own wall B + move A 1000 mm -> compare(before=snapshot, after=active)
#   must show EXACTLY: B added, A modified with a ~1000 mm move, nothing deleted, nothing else.
#   colorize dry_run/apply on a duplicate of a discovered view, explain, record_quality + quality_trend.
# Everything created is deleted; the snapshot, exports and the run's quality history are removed.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'model-diff'
    Catalog = @(
        @{ Name = 'model_diff: snapshot of the active document is stored and listed'; Tool = 'horizun_model_diff' }
        @{ Name = 'model_diff: compare shows exactly the created wall added and the moved wall modified'; Tool = 'horizun_model_diff' }
        @{ Name = 'model_diff: colorize overrides the changed elements in a new view and re-reads them'; Tool = 'horizun_model_diff' }
        @{ Name = 'model_diff: explain narrates measured facts'; Tool = 'horizun_model_diff' }
        @{ Name = 'model_diff: record_quality appends a verified line and quality_trend returns it'; Tool = 'horizun_model_diff' }
    )
    Run     = {
        param($Ctx)
        $T = 'horizun_model_diff'; $doc = $Ctx.Document; $key = 'md-' + $Ctx.RunId
        $cases = @(); $made = @(); $files = @()
        $names = @(
            'model_diff: snapshot of the active document is stored and listed',
            'model_diff: compare shows exactly the created wall added and the moved wall modified',
            'model_diff: colorize overrides the changed elements in a new view and re-reads them',
            'model_diff: explain narrates measured facts',
            'model_diff: record_quality appends a verified line and quality_trend returns it')
        function Out($i, $outcome, $detail) { @{ Name = $names[$i]; Tool = $T; Outcome = $outcome; Detail = $detail } }
        function Wall($x0, $y, $tag) {
            $w = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'wall'; start = @($x0, $y, 0); end = @(($x0 + 3000), $y, 0); height = 3000
                                type_id = $wallType; level_id = $levelId }) } ($key + '-' + $tag)
            if ($w.stage -eq 'apply' -and -not $w.answer.isError) { return [long]@($w.answer.data.rows)[0].element_id }
            return $null
        }

        # ---- discover, never assume -------------------------------------------------------
        $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
        $levelId = if ($lv.data -and @($lv.data.rows).Count -gt 0) { @($lv.data.rows)[0].element_id } else { $null }
        $tq = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); include_types = $true; max_rows = 500; include_links = $false }
        $wallType = if ($tq.data) { @($tq.data.rows | Where-Object { $_.is_element_type })[0].element_id } else { $null }
        if (-not $Ctx.WriteGate -or -not $levelId -or -not $wallType) {
            $why = 'no write gate, level or wall type in the fixture (level=' + $levelId + ', wall type=' + $wallType + ')'
            return @(0..4 | ForEach-Object { Out $_ 'not_covered' $why })
        }

        try {
            $x = 900000   # far from anything the fixture holds, so no join touches its walls
            $wallA = Wall $x 0 'a'
            if ($wallA) { $made += $wallA }

            # ---- 0: snapshot ----------------------------------------------------------------
            $snap = & $Ctx.Call $T @{ operation = 'snapshot'; target_document = $doc; categories = @('OST_Walls') }
            $snapId = if (-not $snap.isError -and $snap.data) { [string]$snap.data.snapshot_id } else { $null }
            if ($snapId) { $files += [string]$snap.data.path; $files += ([string]$snap.data.path -replace '\.json\.gz$', '.meta.json') }
            $list = & $Ctx.Call $T @{ operation = 'list' }
            $listed = $snapId -and @($list.data.snapshots | Where-Object { $_.snapshot_id -eq $snapId }).Count -eq 1
            if ($snapId -and $wallA -and $listed -and [int]$snap.data.meta.elements -ge 1) {
                $cases += Out 0 'pass' ('snapshot ' + $snapId + ' holds ' + $snap.data.meta.elements + ' walls and is listed')
            } else { $cases += Out 0 'fail' ('snapshot/list did not hold: ' + $snap.text) }

            # ---- 1: change the model, compare -------------------------------------------------
            $wallB = Wall $x 20000 'b'
            if ($wallB) { $made += $wallB }
            $mv = & $Ctx.Apply 'horizun_transform_elements' @{ target_document = $doc; units = 'mm'
                operations = @(@{ operation = 'move'; element_ids = @($wallA); vector = @(1000, 0, 0) }) } ($key + '-move')
            $moved = $mv.stage -eq 'apply' -and -not $mv.answer.isError
            $cmp = $null
            if ($snapId -and $wallB -and $moved) {
                $cmp = & $Ctx.Call $T @{ operation = 'compare'; before = $snapId; after = 'active'; target_document = $doc; limit = 1000 }
            }
            if (-not $cmp -or $cmp.isError) {
                $cases += Out 1 $(if ($cmp) { 'fail' } else { 'unverified' }) ('staging or compare failed: moved=' + $moved + ' B=' + $wallB + ' ' + $cmp.text)
            } else {
                $rows = @($cmp.data.detail.rows)
                $added = @($rows | Where-Object { $_.state -eq 'added' })
                $modified = @($rows | Where-Object { $_.state -eq 'modified' })
                $deleted = @($rows | Where-Object { $_.state -eq 'deleted' })
                $files += [string]$cmp.data.exports.csv; $files += [string]$cmp.data.exports.json
                $ok = $added.Count -eq 1 -and [long]$added[0].element_id -eq $wallB -and
                      $modified.Count -eq 1 -and [long]$modified[0].element_id -eq $wallA -and
                      [math]::Abs([double]$modified[0].moved_mm - 1000) -lt 1 -and $deleted.Count -eq 0 -and
                      (Test-Path -LiteralPath ([string]$cmp.data.exports.csv))
                $cases += Out 1 $(if ($ok) { 'pass' } else { 'fail' }) ('added=' + $added.Count + ' modified=' + $modified.Count +
                    ' deleted=' + $deleted.Count + ' moved_mm=' + $(if ($modified.Count) { $modified[0].moved_mm } else { 'n/a' }))
            }

            # ---- 2: colorize ------------------------------------------------------------------
            $views = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Views'; max_rows = 30; include_links = $false }
            $colorDone = $false
            if ($snapId -and $cmp -and -not $cmp.isError) {
                foreach ($v in @($views.data.rows) | Select-Object -First 10) {
                    $dry = & $Ctx.Call $T @{ operation = 'colorize'; before = $snapId; view_id = [long]$v.element_id; target_document = $doc; dry_run = $true }
                    if ($dry.isError -or -not $dry.data.confirmation_token) { continue }
                    $ap = & $Ctx.Apply $T @{ operation = 'colorize'; before = $snapId; view_id = [long]$v.element_id; target_document = $doc } ($key + '-color')
                    $colorDone = $true
                    if ($ap.stage -eq 'apply' -and -not $ap.answer.isError) {
                        $d = $ap.answer.data
                        if ($d.view_id) { $made += [long]$d.view_id }
                        $ok = $d.view_verified -eq $true -and [int]$d.overrides_verified -eq [int]$d.overrides_applied -and
                              ([int]$dry.data.to_color.added + [int]$dry.data.to_color.modified) -eq 2
                        $cases += Out 2 $(if ($ok) { 'pass' } else { 'fail' }) ('view ' + $d.view_name + ': applied=' + $d.overrides_applied +
                            ' verified=' + $d.overrides_verified + ' not_in_view=' + $d.not_in_view)
                    } else { $cases += Out 2 'fail' ('colorize apply: ' + $ap.answer.text) }
                    break
                }
            }
            if (-not $colorDone) { $cases += Out 2 'unverified' 'no duplicable view was found or the comparison did not run' }

            # ---- 3: explain -------------------------------------------------------------------
            $ex = & $Ctx.Call $T @{ operation = 'explain'; target_document = $doc }
            $ok = -not $ex.isError -and [long]$ex.data.model_elements -gt 0 -and [string]$ex.data.narrative.en -match 'model elements' -and
                  $ex.data.iso19650.status -eq 'not_requested'
            $cases += Out 3 $(if ($ok) { 'pass' } else { 'fail' }) ([string]$ex.data.narrative.en)

            # ---- 4: record_quality + quality_trend --------------------------------------------
            $project = 'hz-live-' + $Ctx.RunId
            $rq = & $Ctx.Call $T @{ operation = 'record_quality'; target_document = $doc; source = 'model_scan'; project = $project }
            $qt = & $Ctx.Call $T @{ operation = 'quality_trend'; project = $project }
            if ($rq.data.path) { $files += [string]$rq.data.path }
            if ($qt.data.csv) { $files += [string]$qt.data.csv }
            $ok = -not $rq.isError -and $rq.data.appended_verified -eq $true -and -not $qt.isError -and
                  [int]$qt.data.records -eq 1 -and @($qt.data.rows).Count -eq 1 -and @($qt.data.columns).Count -gt 0
            $cases += Out 4 $(if ($ok) { 'pass' } else { 'fail' }) ('records=' + $qt.data.records + ' columns=' + @($qt.data.columns).Count + ' ' + $rq.text)
        }
        finally {
            if ($made.Count -gt 0) {
                $null = & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = @($made); target_document = $doc; id_cap = 10 } ($key + '-cleanup')
            }
            foreach ($f in $files) { if ($f -and (Test-Path -LiteralPath $f)) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue } }
        }
        return $cases
    }
}
