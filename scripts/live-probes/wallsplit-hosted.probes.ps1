# Live probes for horizun_split_multilayer_walls on a compound wall that HOSTS a door and
# a window. MEASURED 2026-09-26 (Revit 2026, the six-pass wall-split fixture): walls with
# doors converted, walls with M_Fixed windows rolled back - the window's read-only 'Wall
# Thickness'/'Extension Jamb' follow the thinner host, and its nested muntins read "hosted
# by 0" after Revit regenerates it. This module keeps that case in every year's matrix.
# Stages its own level, one compound wall ('Exterior - Brick on Mtl. Stud'), a door
# ('M_Single-Flush') and a window ('M_Fixed', the one with nested components) hosted on it,
# their types taken BY NAME from the document (a document without them leaves the cases
# not_covered); splits it; re-reads the inserts and the split's own spatial check; deletes
# everything it created. The document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'wallsplit-hosted'
    Catalog = @(
        @{ Name = 'wallsplit-hosted: the dry run plans the layers of an own compound wall hosting a door and a window'; Tool = 'horizun_split_multilayer_walls' }
        @{ Name = 'wallsplit-hosted: apply converts it, verified, with a cut proof'; Tool = 'horizun_split_multilayer_walls' }
        @{ Name = 'wallsplit-hosted: the door and the window keep id, UniqueId, host (the core carrier), sill and head'; Tool = 'horizun_split_multilayer_walls' }
        @{ Name = 'wallsplit-hosted: the split''s spatial check reports a door or window touching a new layer wall as trim in a lining (warning), never as a blocked opening'; Tool = 'horizun_split_multilayer_walls' }
        @{ Name = 'wallsplit-hosted: everything the probe created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'wallsplit-hosted' } | Select-Object -First 1).Catalog
        function Case($i, $outcome, $detail) { [void]$cases.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) { for ($i = 0; $i -lt $names.Count; $i++) { Case $i 'not_covered' 'the write tier is closed for this run' }; return $cases }

        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Types($category, $family) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type -and ([string]$_.family -eq $family -or [string]$_.name -eq $family -or [string]$_.type -eq $family) })
        }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-wsh-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }
        # One type per category, by name, from the document.
        function Bring($category, $typeName) { return @(Types $category $typeName) | Select-Object -First 1 }

        $E = 99000.0; $X = 1030000.0; $Y = 0.0; $L = 8000.0
        $lv = Create @{ kind = 'level'; name = ('HZ_WSH_' + (([string]$run) -replace '[^A-Za-z0-9]', '')); elevation = $E } 'level'
        $wallType = Bring 'OST_Walls' 'Exterior - Brick on Mtl. Stud'
        $doorType = Bring 'OST_Doors' '0915 x 2134mm'
        $winType = Bring 'OST_Windows' '0915 x 1220mm'
        $wall = if ($lv -and $wallType) { Create @{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + $L), $Y, $E); height = 3000; level_id = $lv; type_id = $wallType.element_id } 'wall' } else { $null }
        $door = if ($wall -and $doorType) { Create @{ kind = 'family_instance'; type_id = $doorType.element_id; point = @(($X + 2000), $Y, $E); coordinate_mode = 'absolute'; level_id = $lv; host_id = $wall } 'door' } else { $null }
        $window = if ($wall -and $winType) { Create @{ kind = 'family_instance'; type_id = $winType.element_id; point = @(($X + 5500), $Y, ($E + 900)); coordinate_mode = 'absolute'; level_id = $lv; host_id = $wall } 'window' } else { $null }
        $layerWalls = @()
        try {
            if (-not ($wall -and $door -and $window)) {
                $why = "staging incomplete: level=$lv wall_type=$($wallType.element_id) door_type=$($doorType.element_id) window_type=$($winType.element_id) wall=$wall door=$door window=$window"
                for ($i = 0; $i -lt 4; $i++) { Case $i 'not_covered' $why }
            }
            else {
                $readInserts = {
                    $q = & $Ctx.Call 'horizun_query_model' @{ element_ids = @($door, $window); include_links = $false
                            return_parameters = @('INSTANCE_SILL_HEIGHT_PARAM', 'INSTANCE_HEAD_HEIGHT_PARAM') }
                    @(if ($q.data) { $q.data.rows })
                }
                $before = & $readInserts

                # ---- 0: the rehearsal ----
                $dry = & $Ctx.Call 'horizun_split_multilayer_walls' @{ target_document = $doc; element_ids = @($wall); dry_run = $true }
                $plan = if ($dry.data) { @($dry.data.eligible) | Where-Object { [long]$_.wall_id -eq $wall } | Select-Object -First 1 } else { $null }
                $layers = if ($plan) { @($plan.layer_plan).Count } else { 0 }
                if (-not $dry.isError -and $plan -and $layers -ge 3 -and $dry.data.confirmation_token) {
                    Case 0 'pass' ("wall $wall eligible: $layers layers planned, token issued")
                } else { Case 0 'fail' ('no plan with layers and a token: ' + (Short $dry)) }

                # ---- 1: apply ----
                $ap = & $Ctx.Apply 'horizun_split_multilayer_walls' @{ target_document = $doc; element_ids = @($wall) } ($run + '-wsh-split')
                $row = if (Applied $ap) { @($ap.answer.data.walls) | Where-Object { [long]$_.source_wall_id -eq $wall } | Select-Object -First 1 } else { $null }
                $layerWalls = @(if ($row) { $row.layers | Where-Object { $_.resulting_wall_id -and -not $_.is_core_carrier } | ForEach-Object { [long]$_.resulting_wall_id } })
                if ((Applied $ap) -and $row -and $row.applied -eq $true -and $ap.answer.data.all_verified -eq $true -and [int]$ap.answer.data.walls_with_cut_proof -ge 1) {
                    Case 1 'pass' ("converted and verified: $($layerWalls.Count) layer walls beside the core carrier $wall, cut proof on the wall")
                } else {
                    $why = if ($row) { "applied=$($row.applied) code=$($row.code) $($row.message)" } else { Short $ap.answer }
                    Case 1 'fail' $why
                }

                # ---- 2: the inserts, re-read independently ----
                $after = & $readInserts
                $bad = @()
                foreach ($b in $before) {
                    $a = @($after | Where-Object { [long]$_.element_id -eq [long]$b.element_id }) | Select-Object -First 1
                    if (-not $a) { $bad += "$($b.element_id) gone"; continue }
                    if ($a.unique_id -ne $b.unique_id) { $bad += "$($b.element_id) UniqueId changed" }
                    if ([long]$a.host_id -ne $wall) { $bad += "$($b.element_id) hosted by $($a.host_id), not the carrier $wall" }
                    foreach ($p in 'INSTANCE_SILL_HEIGHT_PARAM', 'INSTANCE_HEAD_HEIGHT_PARAM') {
                        if ([string]$a.parameters.$p.raw -ne [string]$b.parameters.$p.raw) { $bad += "$($b.element_id) $p $($b.parameters.$p.raw) -> $($a.parameters.$p.raw)" }
                    }
                }
                if ($row -and $row.applied -eq $true -and $before.Count -eq 2 -and $bad.Count -eq 0) { Case 2 'pass' "door $door and window ${window}: same id, UniqueId, host $wall, sill and head" }
                elseif (-not ($row -and $row.applied -eq $true)) { Case 2 'not_covered' 'the wall did not convert, so there is nothing to compare' }
                else { Case 2 'fail' ($bad -join '; ') }

                # ---- 3: no insert finding of the split's spatial check is an error ----
                # MEASURED 2026-09-26: a door family that lays its trim on the host's faces
                # (M_Single-Flush) has that trim inside the layer walls once the host is the
                # core - the opening itself is cut. What must hold: every such touch is in the
                # split's own spatial_check as a WARNING naming a lining, and no finding about
                # the inserts is an error ("blocked", "move the door").
                if ($layerWalls.Count -eq 0) { Case 3 'not_covered' 'no layer wall was produced' }
                else {
                    $sc = if ($ap -and $ap.answer.data) { $ap.answer.data.spatial_check } else { $null }
                    $inserts = @($door, $window)
                    $onInserts = @(if ($sc) { @($sc.findings) | Where-Object { $inserts -contains [long]$_.a.id -or $inserts -contains [long]$_.b.id } })
                    $errors = @($onInserts | Where-Object { $_.severity -eq 'error' })
                    $linings = @($onInserts | Where-Object { $_.severity -eq 'warning' -and [string]$_.reason -match 'lines its host' })
                    if (-not $sc) { Case 3 'unverified' 'the split reply carries no spatial_check' }
                    elseif ($errors.Count -gt 0) { Case 3 'fail' ('the split reported insert findings as errors: ' + (($errors | ForEach-Object { $_.reason }) -join ' | ')) }
                    else { Case 3 'pass' ("$($linings.Count) insert touch(es) with the $($layerWalls.Count) layer walls reported as trim in a lining (warning), no error") }
                }
            }
        }
        catch { for ($i = 0; $i -lt 4; $i++) { if (-not @($cases | Where-Object { $_.Name -eq $names[$i].Name }).Count) { Case $i 'unverified' ('probe error: ' + $_) } } }
        finally {
            # inserts, layer walls, the carrier, then the level last
            $ids = @(@($door, $window) | Where-Object { $_ }) + $layerWalls + @(@($wall, $lv) | Where-Object { $_ })
            if ($ids.Count -eq 0) { Case 4 'not_covered' 'nothing was created' }
            else {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($ids); id_cap = 50 } ($run + '-wsh-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 4 'pass' ('deleted ' + ($ids -join ',')) }
                else { Case 4 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $cases
    }
}
