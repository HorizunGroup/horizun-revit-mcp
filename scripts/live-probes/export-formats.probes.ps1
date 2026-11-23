# Live probes for horizun_export's view sets (dwg/dgn/dwfx, one file per view),
# gbXML and family .rfa (ExportSets.cs). Field ask, 2026-09-26: DWG took exactly
# one view, and gbXML/DGN/DWFX/.rfa had no typed path at all.
#
# Stages two OWN duplicated floor plans (never exports somebody's views by
# position), an OWN sheet with the second one placed on it (dwg linked and dgn must
# name the placed view's file an xref companion, bound must write none), and for gbXML an OWN walled enclosure on an own level holding an own
# room AND an own space - the energy settings' export category (rooms OR spaces)
# decides which one the model is built from, and a fixture without either left
# the case unverified (MEASURED 2026-09-27 on HZ_WRITE). Every file goes under the
# run's scratch folder and everything staged is deleted, newest first. Nothing is saved. The .rfa case exports ONE door
# family chosen by name order from the dry run's own list; the model is not
# modified by it (EditFamily works on an in-memory copy).
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'export-formats'
    Catalog = @(
        @{ Name = 'export dwg set: two own views, one verified AC10xx file each'; Tool = 'horizun_export' }
        @{ Name = 'export dwg set: sheet_number naming refuses a view that is not a sheet'; Tool = 'horizun_export' }
        @{ Name = 'export dgn: one own view, header verified'; Tool = 'horizun_export' }
        @{ Name = 'export dwfx: one own view, zip header verified'; Tool = 'horizun_export' }
        @{ Name = 'export gbxml: an own enclosed room and space, Space/Zone counts re-read'; Tool = 'horizun_export' }
        @{ Name = 'export rfa: one loadable door family, format read back'; Tool = 'horizun_export' }
        @{ Name = 'export rfa: a system category refuses with no loadable family'; Tool = 'horizun_export' }
        @{ Name = "export dwg sheet linked: the own sheet's placed view is written as a verified xref companion"; Tool = 'horizun_export' }
        @{ Name = 'export dwg sheet bound: one verified file, no companion'; Tool = 'horizun_export' }
        @{ Name = 'export dgn sheet: the own placed view is a verified xref companion'; Tool = 'horizun_export' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $X = 'horizun_export'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $doc = $Ctx.Document
        $names = @('export dwg set: two own views, one verified AC10xx file each',
                   'export dwg set: sheet_number naming refuses a view that is not a sheet',
                   'export dgn: one own view, header verified',
                   'export dwfx: one own view, zip header verified',
                   'export gbxml: an own enclosed room and space, Space/Zone counts re-read',
                   'export rfa: one loadable door family, format read back',
                   'export rfa: a system category refuses with no loadable family',
                   "export dwg sheet linked: the own sheet's placed view is written as a verified xref companion",
                   'export dwg sheet bound: one verified file, no companion',
                   'export dgn sheet: the own placed view is a verified xref companion')

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $X 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }
        $folder = Join-Path $Ctx.ScratchRoot ('hz-export-' + $Ctx.RunId)
        $null = New-Item -ItemType Directory -Force -Path $folder
        $created = New-Object System.Collections.Generic.List[long]

        # ---- own views ----------------------------------------------------------------
        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        $plan = if ($qv.data) { @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] } else { $null }
        $viewIds = @()
        if ($plan) {
            foreach ($suffix in 'A', 'B') {
                $dup = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'duplicate_view'; source_view_id = [long]$plan.view_id; duplicate_option = 'Duplicate'; name = "HZ_EXP_$suffix`_$($Ctx.RunId)"; key = 'v' }) } "exp-dup-$suffix"
                if (Applied $dup) {
                    $row = @($dup.answer.data.rows) | Select-Object -First 1
                    if ($row.verified -eq $true) { $viewIds += [long]$row.element_id; [void]$created.Add([long]$row.element_id) }
                }
            }
        }
        if ($viewIds.Count -lt 2) {
            foreach ($n in @($names[0..3]) + @($names[7..9])) { Case $n $X 'not_covered' 'two own floor plans could not be staged' }
        } else {
            # dwg set, view_name naming
            $r = & $Ctx.Apply $X @{ target_document = $doc; format = 'dwg'; output_path = (Join-Path $folder 'set.dwg'); view_ids = $viewIds; file_naming = 'view_name' } 'exp-dwg-set'
            if (Applied $r) {
                $d = $r.answer.data
                $bad = @($d.files | Where-Object { $_.verified -ne $true -or -not ([string]$_.main.header).StartsWith('AC10') -or -not (Test-Path -LiteralPath $_.file) })
                if ($d.files_verified -eq 2 -and $bad.Count -eq 0) { Case $names[0] $X 'pass' ('headers=' + (@($d.files | ForEach-Object { $_.main.header }) -join ',')) }
                else { Case $names[0] $X 'fail' ($d | ConvertTo-Json -Compress -Depth 6) }
            } else { Case $names[0] $X 'fail' (Why $r) }

            $r = & $Ctx.Call $X @{ target_document = $doc; format = 'dwg'; output_path = (Join-Path $folder 'sheets.dwg'); view_ids = $viewIds; file_naming = 'sheet_number'; dry_run = $true }
            if ($r.isError -and [string]$r.text -match 'names sheets only') { Case $names[1] $X 'pass' 'refused by name before anything was written' }
            else { Case $names[1] $X 'fail' ('expected a sheets-only refusal: ' + [string]$r.text) }

            foreach ($pair in @(@{ i = 2; f = 'dgn'; want = 'dgn_' }, @{ i = 3; f = 'dwfx'; want = 'zip_package' })) {
                $r = & $Ctx.Apply $X @{ target_document = $doc; format = $pair.f; output_path = (Join-Path $folder ('one.' + $pair.f)); view_ids = @($viewIds[0]) } ('exp-' + $pair.f)
                if (Applied $r) {
                    $row = @($r.answer.data.files)[0]
                    if ($r.answer.data.files_verified -eq 1 -and ([string]$row.main.header).StartsWith($pair.want) -and [long]$row.main.bytes -gt 0) { Case $names[$pair.i] $X 'pass' ("header=$($row.main.header) bytes=$($row.main.bytes)") }
                    else { Case $names[$pair.i] $X 'fail' ($r.answer.data | ConvertTo-Json -Compress -Depth 6) }
                } else { Case $names[$pair.i] $X 'fail' (Why $r) }
            }

            # An own sheet with the second own view placed on it. Unmerged, Revit writes the sheet
            # and, beside it, each placed view as <stem>-...: dwg_xrefs=linked and DGN must name
            # those companions role xref; bound merges them into the one file.
            $sheetId = $null
            $number = 'HZX-' + ([string]$Ctx.RunId).Substring(0, [math]::Min(8, ([string]$Ctx.RunId).Length))
            $sh = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'; actions = @(
                    @{ operation = 'create_sheet'; number = $number; name = 'HZ EXP SHEET'; key = 'sh' },
                    @{ operation = 'place_view'; sheet_key = 'sh'; view_id = $viewIds[1]; point = @(300, 300); key = 'vp' }) } 'exp-sheet'
            if (Applied $sh) {
                $rows = @($sh.answer.data.rows)
                $sRow = @($rows | Where-Object { $_.operation -eq 'create_sheet' }) | Select-Object -First 1
                $vRow = @($rows | Where-Object { $_.operation -eq 'place_view' }) | Select-Object -First 1
                if ($sRow -and $sRow.element_id) { [void]$created.Add([long]$sRow.element_id) }
                if ($sRow.verified -eq $true -and $vRow.verified -eq $true) { $sheetId = [long]$sRow.element_id }
            }
            if (-not $sheetId) { foreach ($n in $names[7..9]) { Case $n $X 'not_covered' ('the own sheet with a placed view could not be staged: ' + (Why $sh)) } }
            else {
                foreach ($s in @(@{ i = 7; f = 'dwg'; x = 'linked'; companions = $true }, @{ i = 8; f = 'dwg'; x = 'bound'; companions = $false }, @{ i = 9; f = 'dgn'; x = $null; companions = $true })) {
                    $arg = @{ target_document = $doc; format = $s.f; output_path = (Join-Path $folder ('sheet-' + $s.f + $(if ($s.x) { '-' + $s.x }) + '.' + $s.f)); view_ids = @($sheetId) }
                    if ($s.x) { $arg['dwg_xrefs'] = $s.x }
                    $r = & $Ctx.Apply $X $arg ('exp-sheet-' + $s.f + $(if ($s.x) { '-' + $s.x }))
                    if (-not (Applied $r)) { Case $names[$s.i] $X 'fail' (Why $r); continue }
                    $row = @($r.answer.data.files)[0]
                    $others = @($row.other_files)
                    $xrefs = @($others | Where-Object { $_.role -eq 'xref' })
                    $shape = if ($s.companions) { $xrefs.Count -ge 1 -and $xrefs.Count -eq $others.Count } else { $others.Count -eq 0 }
                    $d = "verified=$($row.verified) header=$($row.main.header) companions=$($others.Count) xref=$($xrefs.Count)"
                    Case $names[$s.i] $X $(if ($r.answer.data.files_verified -eq 1 -and $row.verified -eq $true -and $shape) { 'pass' } else { 'fail' }) $d
                }
            }
        }

        # ---- gbXML --------------------------------------------------------------------
        # Staging: own level, a 6 x 4 m ring of 'Generic - 200mm' walls (BY NAME, from the
        # document; without it the case is unverified, named), one room and one space
        # at the same point inside it.
        $gbWhy = New-Object System.Collections.Generic.List[string]
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if ($q.data) { @($q.data.rows | Where-Object { $_.is_element_type }) } else { @() }
        }
        function Stage($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ('exp-gb-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            [void]$gbWhy.Add($key + ' not created: ' + (Why $r)); return $null
        }
        $wallType = @(Types 'OST_Walls' | Where-Object { [string]$_.type -eq 'Generic - 200mm' -and [string]$_.family -eq 'Basic Wall' }) | Select-Object -First 1
        if (-not $wallType) { [void]$gbWhy.Add("the document holds no 'Basic Wall: Generic - 200mm' type") }
        $gE = 151000.0; $gX = 1210000.0; $gY = 0.0; $gW = 6000.0; $gD = 4000.0
        $gLevel = Stage @{ kind = 'level'; name = ('HZ_EXP_GB_' + $Ctx.RunId); elevation = $gE } 'level'
        $ring = @()
        if ($gLevel -and $wallType) {
            $corners = @(@($gX, $gY), @(($gX + $gW), $gY), @(($gX + $gW), ($gY + $gD)), @($gX, ($gY + $gD)))
            for ($k = 0; $k -lt 4; $k++) {
                $a = $corners[$k]; $b = $corners[($k + 1) % 4]
                $ring += Stage @{ kind = 'wall'; start = @($a[0], $a[1], $gE); end = @($b[0], $b[1], $gE); height = 3000; level_id = $gLevel; type_id = [long]$wallType.element_id } "wall$k"
            }
        }
        $gClosed = @($ring | Where-Object { $_ }).Count -eq 4
        $gMid = @(($gX + $gW / 2), ($gY + $gD / 2))
        $gRoom = if ($gClosed) { Stage @{ kind = 'room'; point = $gMid; level_id = $gLevel } 'room' } else { $null }
        $gSpace = if ($gClosed) { Stage @{ kind = 'space'; point = $gMid; level_id = $gLevel } 'space' } else { $null }
        $gStaging = "level=$gLevel walls=$(@($ring | Where-Object { $_ }).Count) room=$gRoom space=$gSpace " + ($gbWhy -join '; ')

        $gbPath = Join-Path $folder 'energy.xml'
        $pre = & $Ctx.Call $X @{ target_document = $doc; format = 'gbxml'; output_path = $gbPath; dry_run = $true }
        if (-not $gRoom -or -not $gSpace) { Case $names[4] $X 'unverified' ('staging incomplete, so the export never ran: ' + $gStaging) }
        elseif ($pre.isError -and [string]$pre.text -match 'no spaces') { Case $names[4] $X 'fail' ('an own enclosed room AND space were staged, yet the dry run refused as no spaces: ' + [string]$pre.text + ' | ' + $gStaging) }
        elseif ($pre.isError) { Case $names[4] $X 'fail' ('dry run refused: ' + [string]$pre.text) }
        else {
            $r = & $Ctx.Apply $X @{ target_document = $doc; format = 'gbxml'; output_path = $gbPath } 'exp-gbxml'
            # The export builds its model in a transaction it rolls back: the main energy model is
            # read again (the dry run publishes it; overwrite because the file now exists).
            $post = & $Ctx.Call $X @{ target_document = $doc; format = 'gbxml'; output_path = $gbPath; overwrite = $true; dry_run = $true }
            $mainBefore = if ($pre.data -and $null -ne $pre.data.main_energy_model_present) { [string][bool]$pre.data.main_energy_model_present } else { $null }
            $mainAfter = if (-not $post.isError -and $post.data -and $null -ne $post.data.main_energy_model_present) { [string][bool]$post.data.main_energy_model_present } else { $null }
            $mainChanged = $mainBefore -and $mainAfter -and $mainBefore -ne $mainAfter
            if (Applied $r) {
                $rb = $r.answer.data.read_back
                if ($mainChanged) { Case $names[4] $X 'fail' ("the main energy model changed across the export: main_energy_model_present $mainBefore -> $mainAfter") }
                elseif ($r.answer.data.files_verified -eq 1 -and [int]$rb.space -gt 0 -and $rb.root -eq 'gbXML') { Case $names[4] $X 'pass' ("space=$($rb.space) zone=$($rb.zone) surface=$($rb.surface) construction=$($rb.construction) rooms=$($r.answer.data.placed_rooms) spaces=$($r.answer.data.placed_spaces) scope=$($r.answer.data.energy_scope); main_energy_model_present $mainBefore -> $mainAfter") }
                else { Case $names[4] $X 'fail' ($r.answer.data | ConvertTo-Json -Compress -Depth 6) }
            } else { Case $names[4] $X 'fail' (Why $r) }
        }

        # ---- rfa ----------------------------------------------------------------------
        $rfaFolder = Join-Path $folder 'families'
        $null = New-Item -ItemType Directory -Force -Path $rfaFolder
        $list = & $Ctx.Call $X @{ target_document = $doc; format = 'rfa'; output_path = $rfaFolder; category = 'OST_Doors'; dry_run = $true }
        $door = if ($list.data) { @($list.data.families | Sort-Object { [string]$_.name })[0] } else { $null }
        # A freshly opened fixture may carry no loadable door (MEASURED 2026-09-27: HZ_WRITE from
        # disk has none); then the case is not_covered.
        if (-not $door) { Case $names[5] $X 'not_covered' ('no loadable door family in this document: ' + [string]$list.text) }
        else {
            $r = & $Ctx.Apply $X @{ target_document = $doc; format = 'rfa'; output_path = $rfaFolder; family_ids = @([long]$door.id) } 'exp-rfa'
            if (Applied $r) {
                $row = @($r.answer.data.files)[0]
                if ($r.answer.data.files_verified -eq 1 -and $row.verified -eq $true -and [string]$row.saved_in_format -ne '' -and (Test-Path -LiteralPath $row.file)) { Case $names[5] $X 'pass' ("family='$($door.name)' format=$($row.saved_in_format) bytes=$($row.bytes)") }
                else { Case $names[5] $X 'fail' ($r.answer.data | ConvertTo-Json -Compress -Depth 6) }
            } else { Case $names[5] $X 'fail' (Why $r) }
        }
        $r = & $Ctx.Call $X @{ target_document = $doc; format = 'rfa'; output_path = $rfaFolder; category = 'OST_Walls'; dry_run = $true }
        if ($r.isError -and [string]$r.text -match 'No loadable family') { Case $names[6] $X 'pass' 'refused: walls are system families' }
        else { Case $names[6] $X 'fail' ('expected a no-loadable-family refusal: ' + [string]$r.text) }

        # Newest first: the room, space and walls go before the wall type and the level they use.
        if ($created.Count -gt 0) { $ids = $created.ToArray(); [array]::Reverse($ids); $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($ids) } 'exp-cleanup' }
        return $cases.ToArray()
    }
}
