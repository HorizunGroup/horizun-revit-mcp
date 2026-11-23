# Live probes: horizun_code_check operation=energy_readiness (Revit's energy model, built in a
# transaction that is always rolled back). Stages its OWN level far from the model, one room
# placed with NO walls (it must come back BY ID as not enclosed) and, 12 m away, a 6 x 4 m
# enclosure of four walls with one window holding BOTH an own room and an own MEP space: the
# energy model is built from the energy settings' export category (rooms OR spaces - a fixture
# exporting spaces never models a room, MEASURED 2026-09-27 on HZ_WRITE), so whichever the
# settings name is the own space to find, and the own window is counted against a baseline read
# taken before staging. Wall and window types come BY NAME from
# the document (a document without them leaves the cases not_covered). Everything created is deleted; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'energy-readiness'
    Catalog = @(
        @{ Name = 'energy: an own room placed without walls is listed by id in spaces.not_enclosed'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: the energy model is built and rolled back (built, rolled_back, azimuth_basis named)'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: the own walled room and space are enclosed, and the one of the export category is in the energy model'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: surfaces without a construction are a count from 2024 and named not measurable in 2023'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: window_to_wall has the four orientations N,E,S,W and counts the own window'; Tool = 'horizun_code_check' }
        @{ Name = 'energy probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $entries = @(@($script:HzProbeModules | Where-Object { $_.Name -eq 'energy-readiness' } | Select-Object -First 1).Catalog)
        $catalog = @($entries | ForEach-Object { $_.Name }); $tools = @($entries | ForEach-Object { $_.Tool })
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        $why = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-enr-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            [void]$why.Add("$key not created: stage=" + $r.stage + ' ' + (Short $r.answer))
            return $null
        }
        function TypeRows($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Named($rows, $want) {
            @($rows | Where-Object { $want -eq [string]$_.type -or $want -eq ([string]$_.family + ': ' + [string]$_.type) -or
                                     ([string]$_.type -and $want.EndsWith(': ' + [string]$_.type)) }) | Select-Object -First 1
        }
        # A type BY NAME from the document, never "the first type" of the fixture: the first type
        # whose 'Family: Type' matches $pattern. A document without one cannot stage the enclosure.
        function DocType($category, $pattern) {
            $got = @(TypeRows $category | Where-Object { ([string]$_.family + ': ' + [string]$_.type) -match $pattern }) | Select-Object -First 1
            if (-not $got) { [void]$why.Add("the document holds no $category type matching '$pattern'") }
            return $got
        }
        function Energy { & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; operation = 'energy_readiness'; max_findings = 5000 } }
        function WindowsOf($r) { if ($r.data -and $r.data.window_to_wall -and $r.data.window_to_wall.total) { [int]$r.data.window_to_wall.total.windows } else { 0 } }

        # Baseline BEFORE staging: the own window must raise the count, whatever the fixture holds.
        $before = Energy

        # ---- staging (mm): own level, a loose room, and a walled room with one window ----------
        $E = 104000.0; $X = 1140000.0; $Y = 0.0; $X2 = 1142000.0; $Y2 = 12000.0; $Wd = 6000.0; $Dp = 4000.0
        $levelId = Create @{ kind = 'level'; name = "HZ_ENR_$run"; elevation = $E } 'level'
        # A room takes an XY point (MEASURED 2026-09-26 in egress-travel: the XYZ form was refused).
        $looseRoom = if ($levelId) { Create @{ kind = 'room'; point = @($X, $Y); level_id = $levelId } 'loose-room' } else { $null }
        $wallType = DocType 'OST_Walls' 'Generic - 200'
        $windowType = DocType 'OST_Windows' 'Fixed'
        $walls = @()
        if ($levelId -and $wallType) {
            $corners = @(@($X2, $Y2), @(($X2 + $Wd), $Y2), @(($X2 + $Wd), ($Y2 + $Dp)), @($X2, ($Y2 + $Dp)))
            for ($k = 0; $k -lt 4; $k++) {
                $a = $corners[$k]; $b = $corners[($k + 1) % 4]
                $walls += Create @{ kind = 'wall'; start = @($a[0], $a[1], $E); end = @($b[0], $b[1], $E); height = 3000
                                    level_id = $levelId; type_id = $wallType.element_id } "wall$k"
            }
        }
        $closed = @($walls | Where-Object { $_ }).Count -eq 4
        # A window is a family_instance hosted on its wall (create_elements has no 'window' kind).
        $windowId = if ($closed -and $windowType) {
            Create @{ kind = 'family_instance'; type_id = $windowType.element_id; point = @(($X2 + $Wd / 2), $Y2, ($E + 900)); coordinate_mode = 'absolute'
                      level_id = $levelId; host_id = $walls[0] } 'window'
        } else { $null }
        $roomId = if ($closed) { Create @{ kind = 'room'; point = @(($X2 + $Wd / 2), ($Y2 + $Dp / 2)); level_id = $levelId } 'room' } else { $null }
        $spaceId = if ($closed) { Create @{ kind = 'space'; point = @(($X2 + $Wd / 2), ($Y2 + $Dp / 2)); level_id = $levelId } 'space' } else { $null }
        $staging = "level=$levelId loose_room=$looseRoom walls=$(@($walls | Where-Object { $_ }).Count) window=$windowId room=$roomId space=$spaceId " + ($why -join '; ')

        # Whether the document HAS a main energy model, read before and after the rolled-back
        # build through gbXML's read-only dry run (the one reply that publishes it): the build
        # must leave it as it found it. The dry run needs a placed space, so it is read now.
        function MainModel {
            $d = & $Ctx.Call 'horizun_export' @{ target_document = $doc; format = 'gbxml'; output_path = (Join-Path $Ctx.ScratchRoot ("hz-enr-main-$run.xml")); overwrite = $true; dry_run = $true }
            if (-not $d.isError -and $d.data -and $null -ne $d.data.main_energy_model_present) { return [string][bool]$d.data.main_energy_model_present }
            return $null
        }
        $mainBefore = MainModel
        $m = Energy
        $mainAfter = MainModel
        $rep = $m.data
        $em = if ($rep) { $rep.energy_model } else { $null }
        # The own element the energy model must hold: the export category's, read from the reply
        # (energy_scope names it whether or not a model was built). Unreadable -> both must be in.
        $scope = if ($rep -and $rep.spaces) { [string]$rep.spaces.energy_scope } else { '' }
        $exported = if ($scope -match '^MEP spaces') { 'space' } elseif ($scope -match '^rooms of') { 'room' } else { 'either' }
        # Decided per id, never by counting a switch's output: @($null) out of a switch collapses to
        # $null, whose Count is 0 - "0 of 0 staged" read as ready with nothing staged.
        $ownReady = switch ($exported) { 'space' { [bool]$spaceId } 'room' { [bool]$roomId } default { [bool]$roomId -and [bool]$spaceId } }
        $ownIds = @(@($(switch ($exported) { 'space' { $spaceId } 'room' { $roomId } default { $roomId; $spaceId } })) | Where-Object { $_ } | ForEach-Object { [long]$_ })

        # ==== 1: the loose room comes back BY ID as not enclosed ===============================
        if (-not $looseRoom) { Case $catalog[0] $tools[0] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $listed1 = if ($rep -and $rep.spaces) { @($rep.spaces.not_enclosed | Where-Object { [long]$_.id -eq $looseRoom }).Count -gt 0 } else { $false }
            Case $catalog[0] $tools[0] $(if ($listed1) { 'pass' } else { 'fail' }) $(if ($rep) { "not_enclosed_count=$($rep.spaces.not_enclosed_count) own=$looseRoom listed=$listed1" } else { Short $m })
        }

        # ==== 2: built and ROLLED BACK - the only way this operation may touch the model =======
        if (-not $ownReady) { Case $catalog[1] $tools[1] 'unverified' ("staging incomplete for the export category ($exported): " + $staging) }
        else {
            $ok2 = -not $m.isError -and $em -and $em.built -eq $true -and $em.rolled_back -eq $true -and [string]$em.azimuth_basis -match '^(true_north|project_north|unverified)'
            # A MEASURED change of the main energy model fails; an unreadable side is named, not judged.
            $mainChanged = $mainBefore -and $mainAfter -and $mainBefore -ne $mainAfter
            $mainText = if ($mainBefore -and $mainAfter) { "main_energy_model_present $mainBefore -> $mainAfter" } else { "main_energy_model_present unread (before=$mainBefore after=$mainAfter)" }
            Case $catalog[1] $tools[1] $(if ($ok2 -and -not $mainChanged) { 'pass' } else { 'fail' }) $(if ($em) { "built=$($em.built) rolled_back=$($em.rolled_back) tier=$($em.tier) azimuth_basis=$($em.azimuth_basis) analytical_spaces=$($em.analytical_spaces); $mainText" } else { Short $m })
        }

        # ==== 3: room and space enclosed; the export category's own one is IN the model =========
        # Judged only on a BUILT model: with none, not_in_energy_model is absent and "not missing"
        # would pass on nothing at all.
        if (-not $roomId -or -not $spaceId) { Case $catalog[2] $tools[2] 'unverified' ('staging incomplete: ' + $staging) }
        elseif (-not $rep -or -not $rep.spaces) { Case $catalog[2] $tools[2] 'fail' (Short $m) }
        else {
            $asEnclosed = @($rep.spaces.not_enclosed | Where-Object { [long]$_.id -eq $roomId -or [long]$_.id -eq $spaceId } | ForEach-Object { [long]$_.id })
            $missing = $rep.spaces.not_in_energy_model
            $d3 = "export=$exported ($scope) own room=$roomId space=$spaceId in not_enclosed=[$($asEnclosed -join ',')]"
            if (-not $em -or $em.built -ne $true) { Case $catalog[2] $tools[2] 'fail' ("$d3 - no energy model was built: " + $(if ($em) { $em.why } else { Short $m })) }
            elseif ($missing -is [string]) { Case $catalog[2] $tools[2] 'unverified' ("$d3 - the match is unavailable: $missing") }
            else {
                $absent = @($missing | Where-Object { $_ -and $ownIds -contains [long]$_.id } | ForEach-Object { [long]$_.id })
                Case $catalog[2] $tools[2] $(if ($asEnclosed.Count -eq 0 -and $absent.Count -eq 0 -and -not $m.isError) { 'pass' } else { 'fail' }) "$d3 not_in_energy_model own=[$($absent -join ',')] analytical_spaces=$($em.analytical_spaces)"
            }
        }

        # ==== 4: constructions - a count from 2024, named not measurable in 2023 ================
        if (-not $ownReady) { Case $catalog[3] $tools[3] 'unverified' ("staging incomplete for the export category ($exported): " + $staging) }
        else {
            $surf = if ($rep) { $rep.surfaces } else { $null }
            # The own walls have types, so on a tier-Final model some surfaces MUST carry a construction:
            # a count equal to every surface is the below-Final symptom, not a measurement.
            $v4 = if (-not $surf) { 'fail' }
                  elseif ([int]$Ctx.Year -le 2023) { if ([string]$surf.without_construction -match 'not measurable') { 'pass' } else { 'fail' } }
                  elseif (([string]$surf.without_construction_count) -match '^\d+$') { if ([int]$surf.without_construction_count -lt [int]$surf.analytical_surfaces) { 'pass' } else { 'fail' } }
                  elseif ([int]$Ctx.Year -ge 2027 -and [string]$surf.without_construction -match 'not measurable') { 'unverified' }
                  else { 'fail' }
            Case $catalog[3] $tools[3] $v4 $(if ($surf) { "year=$($Ctx.Year) tier=$(if ($em) { $em.tier }) analytical_surfaces=$($surf.analytical_surfaces) without_construction_count=$($surf.without_construction_count) text=$(if ($surf.without_construction -is [string]) { $surf.without_construction })" } else { Short $m })
        }

        # ==== 5: WWR per orientation, and the own window raises the window count ================
        if (-not $windowId -or -not $ownReady) { Case $catalog[4] $tools[4] 'unverified' ("staging incomplete for the export category ($exported): " + $staging) }
        else {
            $wwr = if ($rep) { $rep.window_to_wall } else { $null }
            $rows = if ($wwr) { @($wwr.by_orientation) } else { @() }
            $order = (@($rows | ForEach-Object { $_.orientation }) -join ',')
            $gain = (WindowsOf $m) - (WindowsOf $before)
            Case $catalog[4] $tools[4] $(if ($order -eq 'N,E,S,W' -and $gain -ge 1) { 'pass' } else { 'fail' }) "orientations=$order windows_before=$(WindowsOf $before) windows_after=$(WindowsOf $m) unmeasured_wall_surfaces=$(if ($wwr) { $wwr.unmeasured_wall_surfaces })"
        }

        # ==== 6: cleanup, newest first (space, room, window, walls, types, loose room, level) ===
        $ids = @($created | ForEach-Object { [long]$_ })
        if ($ids.Count -eq 0) { Case $catalog[5] $tools[5] 'unverified' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-enr-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[5] $tools[5] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[5] $tools[5] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        return $cases
    }
}
