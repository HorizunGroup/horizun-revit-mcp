# Live probes for horizun_mep_routing. Loaded by verify-live.ps1 inside the write tier.
# Discovers everything from the disposable document (pipe types, segments, ducts) and
# leaves it as found: every size added is removed, every rule added is removed, and the
# resized duct is resized back to its original catalog size. Nothing is saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'mep-routing'
    Catalog = @(
        @{ Name = 'mep_routing: read lists MEP types, pipe segments and the duct catalog'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep_routing: read returns a pipe type''s routing rules by group'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep_routing: set_rules adds an elbow rule, re-reads it, and removes it again'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep_routing: add_sizes puts a size on a pipe segment and remove_sizes takes it off, both re-read'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep_routing: resize moves a duct to another catalog size and back, re-read both times'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep_routing: size_by_flow proposes a catalog size and writes nothing'; Tool = 'horizun_mep_routing' }
    )
    Run     = {
        param($Ctx)
        $T = 'horizun_mep_routing'; $doc = $Ctx.Document; $out = @()
        $N = @{
            Read     = 'mep_routing: read lists MEP types, pipe segments and the duct catalog'
            Rules    = 'mep_routing: read returns a pipe type''s routing rules by group'
            SetRules = 'mep_routing: set_rules adds an elbow rule, re-reads it, and removes it again'
            Sizes    = 'mep_routing: add_sizes puts a size on a pipe segment and remove_sizes takes it off, both re-read'
            Resize   = 'mep_routing: resize moves a duct to another catalog size and back, re-read both times'
            Flow     = 'mep_routing: size_by_flow proposes a catalog size and writes nothing'
        }
        function Case($name, $outcome, $detail) { @{ Name = $name; Tool = 'horizun_mep_routing'; Outcome = $outcome; Detail = $detail } }
        # An apply is verified only when it reached apply, committed, and its checklist says so.
        function Committed($a) {
            return ($a.stage -eq 'apply' -and -not $a.answer.isError -and $a.answer.data -and
                    $a.answer.data.state -eq 'committed_verified' -and $a.answer.data.postconditions.all_verified -eq $true)
        }
        function Why($a) { if ($a.answer) { [string]$a.answer.text } else { 'no answer' } }
        function Near($a, $b) { [math]::Abs([double]$a - [double]$b) -le 0.01 }

        # ---- read: overview -------------------------------------------------------------
        $ov = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; units = 'mm' }
        $types = @(); $segments = @()
        if (-not $ov.isError -and $ov.data) { $types = @($ov.data.types); $segments = @($ov.data.segments) }
        if ($ov.isError -or -not $ov.data) { $out += Case $N.Read 'unverified' ('read failed: ' + $ov.text) }
        elseif ($types.Count -gt 0 -and $null -ne $ov.data.duct_sizes) { $out += Case $N.Read 'pass' ("{0} types, {1} segments" -f $types.Count, $segments.Count) }
        else { $out += Case $N.Read 'fail' 'read answered without MEP types or a duct catalog' }

        # ---- read: rules of a pipe type --------------------------------------------------
        $pipeType = $types | Where-Object { $_.class -eq 'PipeType' -and $_.has_routing_preferences } | Select-Object -First 1
        $elbows = @()
        if (-not $pipeType) {
            $out += Case $N.Rules 'not_covered' 'the write document holds no pipe type with routing preferences'
            $out += Case $N.SetRules 'not_covered' 'no pipe type with routing preferences to edit'
        }
        else {
            $rt = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; type_id = $pipeType.id }
            if ($rt.isError -or -not $rt.data.type.rule_groups) { $out += Case $N.Rules 'fail' ('no rule groups: ' + $rt.text) }
            else {
                $elbows = @($rt.data.type.rule_groups.Elbows)
                $out += Case $N.Rules 'pass' ("type {0}: {1} elbow rule(s), junction {2}" -f $pipeType.id, $elbows.Count, $rt.data.type.preferred_junction)
            }

            # ---- set_rules: add then remove ---------------------------------------------
            $part = if ($elbows.Count -gt 0) { $elbows[0].part.id } else { $null }
            if (-not $part) { $out += Case $N.SetRules 'not_covered' 'the pipe type has no elbow rule whose part could be reused' }
            else {
                $add = & $Ctx.Apply $T @{ operation = 'set_rules'; target_document = $doc; type_id = $pipeType.id
                    rules = @(@{ group = 'Elbows'; action = 'add'; part_id = $part; description = 'hz probe ' + $Ctx.RunId }) } 'mep-rule-add'
                if (-not (Committed $add)) { $out += Case $N.SetRules 'fail' ('add rule: ' + (Why $add)) }
                else {
                    $again = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; type_id = $pipeType.id }
                    $now = @($again.data.type.rule_groups.Elbows)
                    $last = if ($now.Count -gt 0) { $now[$now.Count - 1] } else { $null }
                    $del = & $Ctx.Apply $T @{ operation = 'set_rules'; target_document = $doc; type_id = $pipeType.id
                        rules = @(@{ group = 'Elbows'; action = 'remove'; index = ($now.Count - 1) }) } 'mep-rule-remove'
                    $back = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; type_id = $pipeType.id }
                    $countBack = @($back.data.type.rule_groups.Elbows).Count
                    if ($now.Count -ne $elbows.Count + 1 -or -not $last -or $last.description -ne ('hz probe ' + $Ctx.RunId)) {
                        $out += Case $N.SetRules 'fail' ("after add the re-read holds {0} elbow rules (expected {1}) and the last is '{2}'" -f $now.Count, ($elbows.Count + 1), $last.description)
                    }
                    elseif (-not (Committed $del) -or $countBack -ne $elbows.Count) {
                        $out += Case $N.SetRules 'fail' ("remove did not restore {0} rules (now {1}): {2}" -f $elbows.Count, $countBack, (Why $del))
                    }
                    else { $out += Case $N.SetRules 'pass' ("elbow rules {0} -> {1} -> {2}" -f $elbows.Count, $now.Count, $countBack) }
                }
            }
        }

        # ---- add_sizes / remove_sizes on a pipe segment ------------------------------------
        $segment = $segments | Where-Object { $_.size_count -gt 0 } | Select-Object -First 1
        if (-not $segment) { $out += Case $N.Sizes 'not_covered' 'the write document holds no pipe segment with sizes' }
        else {
            $sr = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; segment_id = $segment.id; units = 'mm' }
            $noms = @($sr.data.segment.sizes | ForEach-Object { [double]$_.nominal })
            $nominal = [math]::Round((($noms | Measure-Object -Maximum).Maximum) + 11.1, 1)
            $size = @{ nominal = $nominal; inner = $nominal - 2; outer = $nominal + 2 }
            $add = & $Ctx.Apply $T @{ operation = 'add_sizes'; target_document = $doc; catalog = 'segment'; segment_id = $segment.id; units = 'mm'; sizes = @($size) } 'mep-size-add'
            $mid = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; segment_id = $segment.id; units = 'mm' }
            $present = @($mid.data.segment.sizes | Where-Object { Near $_.nominal $nominal }).Count -eq 1
            $rem = $null
            if (Committed $add) {
                $rem = & $Ctx.Apply $T @{ operation = 'remove_sizes'; target_document = $doc; catalog = 'segment'; segment_id = $segment.id; units = 'mm'; sizes = @(@{ nominal = $nominal }) } 'mep-size-remove'
            }
            $end = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; segment_id = $segment.id; units = 'mm' }
            $gone = @($end.data.segment.sizes | Where-Object { Near $_.nominal $nominal }).Count -eq 0
            if (-not (Committed $add)) { $out += Case $N.Sizes 'fail' ('add_sizes: ' + (Why $add)) }
            elseif (-not $present) { $out += Case $N.Sizes 'fail' "add_sizes committed but a fresh read does not list $nominal mm" }
            elseif (-not (Committed $rem) -or -not $gone) { $out += Case $N.Sizes 'fail' ('remove_sizes did not take it off: ' + (Why $rem)) }
            else { $out += Case $N.Sizes 'pass' ("segment {0}: {1} mm added and removed; {2} sizes before, {3} after" -f $segment.id, $nominal, $noms.Count, @($end.data.segment.sizes).Count) }
        }

        # ---- resize a duct and back ----------------------------------------------------------
        $ducts = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_DuctCurves'; max_rows = 30; include_links = $false }
        $ids = @($ducts.data.rows | ForEach-Object { $_.element_id } | Where-Object { $_ })
        $picked = $null
        if ($ids.Count -gt 0) {
            $er = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; units = 'mm'; element_ids = $ids }
            $picked = @($er.data.elements) | Where-Object { $_.size_in_catalog -and ($_.kind -eq 'duct_rectangular' -or $_.kind -eq 'duct_round') } | Select-Object -First 1
        }
        if (-not $picked) { $out += Case $N.Resize 'not_covered' 'no round or rectangular duct at a catalog size in the write document' }
        else {
            $shape = if ($picked.kind -eq 'duct_round') { 'round' } else { 'rectangular' }
            $list = @($ov.data.duct_sizes.$shape | ForEach-Object { [double]$_.nominal })
            $current = if ($shape -eq 'round') { [double]$picked.size.diameter } else { [double]$picked.size.width }
            $other = $list | Where-Object { -not (Near $_ $current) } | Sort-Object { [math]::Abs($_ - $current) } | Select-Object -First 1
            if ($null -eq $other) { $out += Case $N.Resize 'not_covered' "the $shape duct catalog has no second size" }
            else {
                $to = @{ operation = 'resize'; target_document = $doc; units = 'mm'; element_ids = @($picked.element_id) }
                $from = $to.Clone()
                if ($shape -eq 'round') { $to.diameter = $other; $from.diameter = $current }
                else { $to.width = $other; $to.height = [double]$picked.size.height; $from.width = $current; $from.height = [double]$picked.size.height }
                $there = & $Ctx.Apply $T $to 'mep-resize-there'
                $back = $null
                if (Committed $there) { $back = & $Ctx.Apply $T $from 'mep-resize-back' }
                $check = & $Ctx.Call $T @{ operation = 'read'; target_document = $doc; units = 'mm'; element_ids = @($picked.element_id) }
                $final = @($check.data.elements)[0]
                $finalValue = if ($shape -eq 'round') { $final.size.diameter } else { $final.size.width }
                if (-not (Committed $there)) { $out += Case $N.Resize 'fail' ('resize: ' + (Why $there)) }
                elseif (-not (Committed $back)) { $out += Case $N.Resize 'fail' ('resize back: ' + (Why $back)) }
                elseif (-not (Near $finalValue $current)) { $out += Case $N.Resize 'fail' "after the round trip duct $($picked.element_id) reads $finalValue, not $current" }
                else {
                    $r = $there.answer.data.result
                    $out += Case $N.Resize 'pass' ("duct {0} {1} -> {2} -> {1} mm; fittings added {3}, replaced {4}" -f $picked.element_id, $current, $other, @($r.fittings_added).Count, @($r.fittings_removed_or_replaced).Count)
                }
            }
        }

        # ---- size_by_flow: a plan, no write ------------------------------------------------------
        if (-not $picked) { $out += Case $N.Flow 'not_covered' 'no duct to size' }
        else {
            $sf = & $Ctx.Call $T @{ operation = 'size_by_flow'; target_document = $doc; units = 'mm'; element_ids = @($picked.element_id); max_velocity = 5; flow = 200 }
            $row = @($sf.data.rows)[0]
            if ($sf.isError -or -not $sf.data) { $out += Case $N.Flow 'fail' ('size_by_flow: ' + $sf.text) }
            elseif ($sf.data.writes -ne $false -or $null -ne $sf.data.confirmation_token) { $out += Case $N.Flow 'fail' 'size_by_flow claims or offers a write' }
            elseif (-not $row.proposed -and -not $row.reason) { $out += Case $N.Flow 'fail' 'the row has neither a proposal nor a reason' }
            else { $out += Case $N.Flow 'pass' ("proposed {0} at {1} m/s" -f ($row.proposed | ConvertTo-Json -Compress), $row.velocity_mps) }
        }
        return $out
    }
}
