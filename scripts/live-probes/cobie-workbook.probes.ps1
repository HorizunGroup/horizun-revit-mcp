# Live probes for horizun_export format=cobie (ExportCobie.cs): a COBie 2.4 workbook
# written by the Core writer and re-read from disk cell by cell.
#
# Stages its OWN level far from the model, a 6 x 4 m ring of 'Generic - 200mm' walls
# (the document's own type, BY NAME), a door of the document's own type BY NAME
# ('M_Single-Flush: 0915 x 2134mm') placed in the south wall, and an own room in the
# ring, numbered and named
# by the probe, in the document's LAST phase (listed, never assumed). Then it
# exports with component_categories [OST_Doors] and NO category_parameter, and
# judges:
#   - the rehearsal: the seven sheets, a token, and the own room's empty Category
#     named as a required-field finding (so deliverable_ready is false);
#   - the apply: files_verified 1, the re-read matching the plan, the reply's sha256
#     equal to the file's own;
#   - the WORKBOOK ITSELF, read back through horizun_excel_read_rows (the server's
#     independent reader, not the add-in's): the own room is a Space row on the own
#     level, the own door is a Component whose Space is that room, and created_by is
#     on every row read.
# Every file goes under the run's scratch folder; everything staged is deleted, newest
# first. Nothing is saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'cobie-workbook'
    Catalog = @(
        @{ Name = 'cobie: the rehearsal plans the seven COBie sheets and stamps a token'; Tool = 'horizun_export' }
        @{ Name = 'cobie: without category_parameter the own room''s Category is a required-field finding and the workbook is not ready'; Tool = 'horizun_export' }
        @{ Name = 'cobie: the workbook is written and re-read from disk cell for cell (files_verified 1, the file''s own sha256)'; Tool = 'horizun_export' }
        @{ Name = 'cobie: the re-read workbook holds the own room as a Space row and the own door as a Component in that room'; Tool = 'horizun_export' }
        @{ Name = 'cobie: created_by is echoed in the reply and on every row read back'; Tool = 'horizun_export' }
        @{ Name = 'cobie probes: everything staged is deleted, newest first'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $X = 'horizun_export'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { $t = "stage=$($r.stage) " + [string]$r.answer.text; if ($t.Length -gt 500) { $t.Substring(0, 500) } else { $t } }
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'cobie-workbook' } | Select-Object -First 1).Catalog | ForEach-Object { $_.Name }
        $doc = $Ctx.Document; $run = [string]$Ctx.RunId

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $(if ($n -like 'cobie probes:*') { 'horizun_delete_verified' } else { $X }) 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }
        $folder = Join-Path $Ctx.ScratchRoot ('hz-cobie-' + $run)
        $null = New-Item -ItemType Directory -Force -Path $folder
        $created = New-Object System.Collections.Generic.List[long]
        $why = New-Object System.Collections.Generic.List[string]

        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if ($q.data) { @($q.data.rows | Where-Object { $_.is_element_type }) } else { @() }
        }
        function Stage($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ('cobie-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            [void]$why.Add($key + ' not created: ' + (Why $r)); return $null
        }
        # A type found BY NAME in the document; a document without it cannot stage the probe.
        function TypeByName($category, $family, $type) {
            $found = @(Types $category | Where-Object { [string]$_.family -eq $family -and [string]$_.type -eq $type }) | Select-Object -First 1
            if (-not $found) { [void]$why.Add("no '$family`: $type' in the document") }
            return $found
        }

        # ---- staging: the last phase, own level, walls, door, room (mm) -----------------------
        $ph = & $Ctx.Call 'horizun_manage_phases' @{ operation = 'list'; target_document = $doc }
        $phase = if ($ph.data) { @($ph.data.phases | Where-Object { $_ }) | Select-Object -Last 1 } else { $null }
        if (-not $phase) { [void]$why.Add('the phases could not be listed: ' + [string]$ph.text) }
        $wallType = if ($phase) { TypeByName 'OST_Walls' 'Basic Wall' 'Generic - 200mm' } else { $null }
        $E = 163000.0; $X0 = 1330000.0; $Y0 = 0.0; $W = 6000.0; $D = 4000.0
        $level = if ($phase -and $wallType) { Stage @{ kind = 'level'; name = ('HZ_COBIE_' + $run); elevation = $E } 'level' } else { $null }
        $walls = @()
        if ($level) {
            $corners = @(@($X0, $Y0), @(($X0 + $W), $Y0), @(($X0 + $W), ($Y0 + $D)), @($X0, ($Y0 + $D)))
            for ($k = 0; $k -lt 4; $k++) {
                $a = $corners[$k]; $b = $corners[($k + 1) % 4]
                $walls += Stage @{ kind = 'wall'; start = @($a[0], $a[1], $E); end = @($b[0], $b[1], $E); height = 3000; level_id = $level; type_id = [long]$wallType.element_id } "wall$k"
            }
        }
        $closed = @($walls | Where-Object { $_ }).Count -eq 4
        $doorType = if ($closed) { TypeByName 'OST_Doors' 'M_Single-Flush' '0915 x 2134mm' } else { $null }
        # create_elements has no 'door' kind: a door is a family_instance hosted on its wall.
        $door = if ($doorType) {
            Stage @{ kind = 'family_instance'; type_id = [long]$doorType.element_id; point = @(($X0 + $W / 2), $Y0, $E); coordinate_mode = 'absolute'
                     level_id = $level; host_id = $walls[0] } 'door'
        } else { $null }
        $number = 'HZC-' + $run.Substring(0, [math]::Min(8, $run.Length)); $roomName = 'HZ COBIE ROOM'; $levelName = 'HZ_COBIE_' + $run
        # A point room takes the phase Revit gives it - the last one, which the export names below;
        # create_elements refuses a phase_id beside a point (MEASURED 2026-09-27, Revit 2026).
        $room = if ($door) { Stage @{ kind = 'room'; point = @(($X0 + $W / 2), ($Y0 + $D / 2)); level_id = $level; number = $number; name = $roomName } 'room' } else { $null }
        $staging = "phase=$(if ($phase) { $phase.name }) level=$level walls=$(@($walls | Where-Object { $_ }).Count) door=$door room=$room " + ($why -join '; ')

        if (-not $room) {
            foreach ($n in $names[0..4]) { Case $n $X 'unverified' ('staging incomplete, so the export never ran: ' + $staging) }
        }
        else {
            $email = 'cobie-probe@example.com'
            $out = Join-Path $folder ('cobie-' + $run + '.xlsx')
            $cobie = @{ created_by = $email; facility = @{ name = 'HZ COBIE PROBE ' + $run }; phase = [string]$phase.name
                        component_categories = @('OST_Doors'); max_findings = 5000 }
            $export = @{ target_document = $doc; format = 'cobie'; output_path = $out; cobie = $cobie }

            # ==== 1 + 2: the rehearsal ===========================================================
            $dry = $export.Clone(); $dry['dry_run'] = $true
            $pre = & $Ctx.Call $X $dry
            $sheetNames = if ($pre.data -and $pre.data.cobie) { @($pre.data.cobie.sheets | ForEach-Object { [string]$_.name }) -join ',' } else { '' }
            $want = 'Facility,Floor,Space,Zone,Type,Component,System'
            if ($pre.isError) { Case $names[0] $X 'fail' ('the rehearsal refused: ' + [string]$pre.text) }
            elseif ($sheetNames -eq $want -and $pre.data.confirmation_token) {
                $spaceRows = @($pre.data.cobie.sheets | Where-Object { $_.name -eq 'Space' })[0].rows
                Case $names[0] $X 'pass' ("sheets=$sheetNames spaces=$spaceRows token issued; scope $($pre.data.cobie.scope | ConvertTo-Json -Compress -Depth 4)")
            }
            else { Case $names[0] $X 'fail' ("sheets='$sheetNames' token=$([bool]$pre.data.confirmation_token)") }

            $items = if ($pre.data -and $pre.data.cobie) { @($pre.data.cobie.findings.items) } else { @() }
            $own = @($items | Where-Object { $_.kind -eq 'required_field' -and $_.sheet -eq 'Space' -and $_.column -eq 'Category' -and [string]$_.row -eq $number }) | Select-Object -First 1
            if ($pre.isError) { Case $names[1] $X 'fail' ('the rehearsal refused: ' + [string]$pre.text) }
            elseif ($own -and $own.blocking -eq $true -and [string]$own.detail -eq 'category_parameter was not given' -and $pre.data.deliverable_ready_if_written -eq $false) {
                Case $names[1] $X 'pass' ("finding: $($own | ConvertTo-Json -Compress); blocking=$(@($pre.data.blocking) -join ',')")
            }
            elseif (-not $own -and $pre.data.cobie.findings.truncated -eq $true) {
                # The list stops at max_findings; an unlisted finding is not an absent one.
                Case $names[1] $X 'unverified' ("the findings list was cut at $($pre.data.cobie.findings.listed) of $($pre.data.cobie.findings.total); the own room's finding may be beyond it")
            }
            else {
                Case $names[1] $X 'fail' ("no required-field Category finding for the own room $number (listed $($pre.data.cobie.findings.listed) of $($pre.data.cobie.findings.total)); ready_if_written=$($pre.data.deliverable_ready_if_written)")
            }

            # ==== 3: the apply, verified from the file ============================================
            $r = & $Ctx.Apply $X $export 'cobie-apply'
            $result = if (Applied $r) { $r.answer.data } else { $null }
            $file = if ($result) { @($result.files)[0] } else { $null }
            $diskSha = if (Test-Path -LiteralPath $out) { (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
            $readSheets = if ($result) { @($result.read_back.sheets | ForEach-Object { [string]$_.name }) -join ',' } else { '' }
            if (-not $result) { Case $names[2] $X 'fail' (Why $r) }
            elseif ($result.files_verified -eq 1 -and $result.read_back.matches_plan -eq $true -and $readSheets -eq $want -and $diskSha -and [string]$file.sha256 -eq $diskSha -and $result.deliverable_ready -eq $false) {
                Case $names[2] $X 'pass' ("bytes=$($file.bytes) sha256=$diskSha cells_sha256=$($result.read_back.cells_sha256) blocking=$(@($result.blocking) -join ',')")
            }
            else { Case $names[2] $X 'fail' ("files_verified=$($result.files_verified) matches_plan=$($result.read_back.matches_plan) sheets='$readSheets' reply_sha=$($file.sha256) disk_sha=$diskSha ready=$($result.deliverable_ready)") }

            # ==== 4 + 5: the workbook itself, through the server's own reader ======================
            if (-not $result) {
                foreach ($n in $names[3..4]) { Case $n $X 'unverified' ('the apply did not produce a verified workbook, so its content was not judged: ' + (Why $r)) }
            }
            else {
                $book = @{}
                $readWhy = New-Object System.Collections.Generic.List[string]
                foreach ($s in 'Facility', 'Space', 'Component') {
                    # Not $x: PowerShell names ignore case, and $X holds this module's tool name.
                    $sheetRead = & $Ctx.Call 'horizun_excel_read_rows' @{ file_path = $out; sheet = $s; max_rows = 10000 }
                    if ($sheetRead.isError -or -not $sheetRead.data) { [void]$readWhy.Add("$s`: " + [string]$sheetRead.text) } else { $book[$s] = $sheetRead.data }
                }
                # Rows are addressed by INDEX and only strings and ints leave these helpers:
                # PowerShell unrolls an array a function returns, so a one-row sheet handed back
                # as a row would be iterated cell by cell.
                function Column($data, $header) { $h = @(@($data.rows)[0]); for ($i = 0; $i -lt $h.Count; $i++) { if ([string]$h[$i] -eq $header) { return $i } }; return -1 }
                function RowCount($data) { return @($data.rows).Count }
                function Value($data, [int]$index, $header) {
                    $c = Column $data $header
                    if ($c -lt 0 -or $index -lt 1 -or $index -ge (RowCount $data)) { return $null }
                    return [string]@(@($data.rows)[$index])[$c]
                }
                function FindRow($data, $header, [scriptblock]$test) {
                    for ($i = 1; $i -lt (RowCount $data); $i++) { if (& $test (Value $data $i $header)) { return $i } }
                    return -1
                }
                function RowText($data, [int]$index) { if ($index -lt 1) { return '(none)' }; return (@(@($data.rows)[$index]) | ForEach-Object { [string]$_ }) -join '|' }

                if ($readWhy.Count -gt 0) {
                    foreach ($n in $names[3..4]) { Case $n $X 'fail' ('horizun_excel_read_rows could not read the written workbook: ' + ($readWhy -join '; ')) }
                }
                else {
                    $sameFile = @($book.Values | Where-Object { [string]$_.sha256 -ne [string]$file.sha256 }).Count -eq 0
                    $space = $book['Space']; $component = $book['Component']
                    $spaceAt = FindRow $space 'Name' { param($v) $v -eq $number }
                    $doorAt = FindRow $component 'Name' { param($v) ([string]$v).EndsWith('-' + $door) }
                    $spaceOk = $spaceAt -ge 1 -and (Value $space $spaceAt 'Description') -eq $roomName -and (Value $space $spaceAt 'FloorName') -eq $levelName
                    $doorOk = $doorAt -ge 1 -and (Value $component $doorAt 'Space') -eq $number -and (Value $component $doorAt 'TypeName') -eq 'M_Single-Flush: 0915 x 2134mm'
                    $detail = "same file=$sameFile space row=$(RowText $space $spaceAt) door row=$(RowText $component $doorAt)"
                    Case $names[3] $X $(if ($sameFile -and $spaceOk -and $doorOk) { 'pass' } else { 'fail' }) $detail

                    $wrong = New-Object System.Collections.Generic.List[string]
                    $rowsRead = 0
                    foreach ($s in 'Facility', 'Space', 'Component') {
                        for ($i = 1; $i -lt (RowCount $book[$s]); $i++) {
                            $rowsRead++
                            $by = Value $book[$s] $i 'CreatedBy'
                            if ($by -ne $email) { [void]$wrong.Add("$s row $($i + 1): '$by'") }
                        }
                    }
                    $replyOk = [string]$result.cobie.created_by -eq $email -and [string]$pre.data.cobie.created_by -eq $email
                    if ($replyOk -and $rowsRead -gt 0 -and $wrong.Count -eq 0) { Case $names[4] $X 'pass' ("created_by=$email on $rowsRead row(s) of Facility, Space and Component") }
                    else { Case $names[4] $X 'fail' ("reply created_by='$($result.cobie.created_by)' rows read=$rowsRead wrong=" + ($wrong -join ', ')) }
                }
            }
        }

        # ==== 6: cleanup, newest first (room, door, door type, walls, level, wall type) ============
        if ($created.Count -eq 0) { Case $names[5] 'horizun_delete_verified' 'unverified' ('nothing was created: ' + $staging) }
        else {
            $ids = $created.ToArray(); [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($ids); id_cap = 500 } 'cobie-cleanup'
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $names[5] 'horizun_delete_verified' 'pass' ('deleted ' + $ids.Count + ' staged id(s), newest first: ' + ($ids -join ',')) }
            else { Case $names[5] 'horizun_delete_verified' 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Why $del)) }
        }
        return $cases.ToArray()
    }
}
