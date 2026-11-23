#Requires -Version 5.1
# Exercises cobie-workbook.probes.ps1 WITHOUT Revit. The fakes' reply shapes are taken
# FROM THE CODE, not invented: the export rehearsal and apply from ExportCobie.cs
# (dry_run, planned_files, deliverable_ready_if_written, blocking, cobie.sheets[],
# cobie.findings.items[] as CobieFinding.ToJson writes them, files[], read_back,
# deliverable_ready), the column headers from CobieRules.Columns, and the workbook read
# back from the server's ExcelReadRows.cs (sha256, sheets, rows as arrays with null for
# an empty cell). Every field a fake carries is one the real reply carries; the shapes
# are to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'cobie-workbook.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'cobie-workbook' }
if (-not $module) { 'module did not register'; exit 1 }

# CobieRules.Columns, verbatim.
$columns = @{
    Facility  = @('Name', 'CreatedBy', 'CreatedOn', 'Category', 'ProjectName', 'SiteName', 'LinearUnits', 'AreaUnits', 'VolumeUnits', 'CurrencyUnit',
                  'AreaMeasurement', 'ExternalSystem', 'ExternalProjectObject', 'ExternalProjectIdentifier', 'ExternalSiteObject', 'ExternalSiteIdentifier',
                  'ExternalFacilityObject', 'ExternalFacilityIdentifier', 'Description', 'ProjectDescription', 'SiteDescription', 'Phase')
    Space     = @('Name', 'CreatedBy', 'CreatedOn', 'Category', 'FloorName', 'Description', 'ExtSystem', 'ExtObject', 'ExtIdentifier', 'RoomTag',
                  'UsableHeight', 'GrossArea', 'NetArea')
    Component = @('Name', 'CreatedBy', 'CreatedOn', 'TypeName', 'Space', 'Description', 'ExtSystem', 'ExtObject', 'ExtIdentifier', 'SerialNumber',
                  'InstallationDate', 'WarrantyStartDate', 'TagNumber', 'BarCode', 'AssetIdentifier')
}
$sheetOrder = @('Facility', 'Floor', 'Space', 'Zone', 'Type', 'Component', 'System')

function New-Ctx([bool]$gate, [bool]$withTypes = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; nextId = 900; kinds = @{}; walls = New-Object System.Collections.Generic.List[long]
                deleted = @(); wallCopied = $withTypes; doorCopied = $withTypes
                roomNumber = $null; roomName = $null; roomPhase = $null; levelName = $null; door = $null; room = $null
                exportArgs = $null; applyArgs = $null; file = $null; sha = $null
                wrongSpace = $false; dropCreatedBy = $false; noFinding = $false; mismatch = $false; noPhases = $false; otherFile = $false; truncated = $false }
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-cobie-probe-test-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Force -Path $root
    $cols = $columns; $order = $sheetOrder
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_model') {
            $cat = [string]$arguments.categories[0]
            $rows = @()
            if ($cat -eq 'OST_Walls' -and $state.wallCopied) { $rows = @([pscustomobject]@{ element_id = 41; family = 'Basic Wall'; type = 'Generic - 200mm'; is_element_type = $true }) }
            if ($cat -eq 'OST_Doors' -and $state.doorCopied) { $rows = @([pscustomobject]@{ element_id = 42; family = 'M_Single-Flush'; type = '0915 x 2134mm'; is_element_type = $true }) }
            return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
        }
        if ($tool -eq 'horizun_manage_phases') {
            if ($state.noPhases) { return @{ isError = $true; text = 'phases unreadable'; data = $null } }
            return @{ isError = $false; data = [pscustomobject]@{ phases = @([pscustomobject]@{ id = 1; name = 'Existing' }, [pscustomobject]@{ id = 2; name = 'New Construction' }) } }
        }
        if ($tool -eq 'horizun_export') {
            # The rehearsal (ExportCobie.cs dry-run branch).
            $state.exportArgs = $arguments
            $items = @()
            if (-not $state.noFinding) {
                $items += [pscustomobject]@{ kind = 'required_field'; blocking = $true; sheet = 'Space'; row = $state.roomNumber; column = 'Category'
                                             detail = 'category_parameter was not given'; element_id = $state.room }
            }
            $items += [pscustomobject]@{ kind = 'required_field'; blocking = $true; sheet = 'Type'; row = 'M_Single-Flush: 0915 x 2134mm'; column = 'Category'
                                         detail = 'category_parameter was not given'; element_id = 42 }
            $sheets = @($order | ForEach-Object { [pscustomobject]@{ name = $_; rows = 1; columns = 9; required = @('Name', 'CreatedBy', 'CreatedOn') } })
            $cobie = [pscustomobject]@{
                created_by = [string]$arguments.cobie.created_by; created_on = '2026-09-27T10:00:00'; created_on_source = 'export_time_utc'
                phase = [string]$arguments.cobie.phase; space_source = 'rooms'; facility = [string]$arguments.cobie.facility.name
                sheets = $sheets; scope = [pscustomobject]@{ phase = [string]$arguments.cobie.phase }
                findings = [pscustomobject]@{ total = $(if ($state.truncated) { 9000 } else { $items.Count }); blocking = $items.Count; advisory = 0
                                              listed = $items.Count; truncated = [bool]$state.truncated; items = $items }
                content_sha256 = ('ab' * 32)
            }
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; format = 'cobie'; output_path = [string]$arguments.output_path
                        planned_files = @([string]$arguments.output_path); overwrite = $false; deliverable_ready_if_written = $false
                        blocking = @('required_field: ' + $items.Count); cobie = $cobie; long_run = $false; confirmation_token = 'tok-cobie' } }
        }
        if ($tool -eq 'horizun_excel_read_rows') {
            # ExcelReadRows.cs: the rows of ONE sheet, null for an empty cell, the file's sha256.
            $name = [string]$arguments.sheet
            $email = [string]$state.applyArgs.cobie.created_by
            $list = New-Object System.Collections.Generic.List[object]
            $list.Add([object[]]$cols[$name])
            switch ($name) {
                'Facility' { $list.Add([object[]]@([string]$state.applyArgs.cobie.facility.name, $email, '2026-09-27T10:00:00', $null, $null, $null, 'millimeters')) }
                'Space' {
                    $list.Add([object[]]@('001', $email, '2026-09-27T10:00:00', $null, 'Level 1', 'Existing room'))
                    $list.Add([object[]]@($state.roomNumber, $email, '2026-09-27T10:00:00', $null, $state.levelName, $state.roomName, 'Autodesk Revit 2026', 'Room', 'uid-room', $state.roomNumber, 3000, $null, 24))
                }
                'Component' {
                    $space = if ($state.wrongSpace) { '001' } else { $state.roomNumber }
                    $by = if ($state.dropCreatedBy) { $null } else { $email }
                    # Parenthesised: the comma binds tighter than +, and would fold the whole row into one string.
                    $list.Add([object[]]@(('M_Single-Flush: 0915 x 2134mm-' + $state.door), $by, '2026-09-27T10:00:00', 'M_Single-Flush: 0915 x 2134mm', $space))
                }
            }
            $sha = if ($state.otherFile) { 'ff' * 32 } else { $state.sha }
            return @{ isError = $false; data = [pscustomobject]@{ file_path = [string]$arguments.file_path; sha256 = $sha; sheet = $name; sheets = $order
                        rows = $list.ToArray(); row_count = $list.Count; widest_row = 15; rows_truncated = 0; merged_ranges = @(); formulas_without_cached_value = 0; notes = @() } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add([string]$key)
        $ok = { param($data) @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' } } }
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = @($arguments.ids); return (& $ok ([pscustomobject]@{})) }
        if ($tool -eq 'horizun_create_elements') {
            $id = $state.nextId; $state.nextId++
            $e = $arguments.elements[0]
            $kind = [string]$e.kind
            # CreateElementsEnclosed: phase_id and min_area_m2 go with placement='all_enclosed' only.
            if ($kind -eq 'room' -and -not $e.placement -and ($e.ContainsKey('phase_id') -or $e.ContainsKey('min_area_m2'))) {
                return @{ stage = 'dry_run'; answer = @{ isError = $true; data = $null; text = "Error: elements[0]: phase_id and min_area_m2 go with placement='all_enclosed' only. Nothing ran." } }
            }
            if ($kind -eq 'wall') { $state.walls.Add([long]$id) } else { $state.kinds[$kind] = $id }
            if ($kind -eq 'level') { $state.levelName = [string]$e.name }
            if ($kind -eq 'family_instance') { $state.door = $id }
            if ($kind -eq 'room') { $state.room = $id; $state.roomNumber = [string]$e.number; $state.roomName = [string]$e.name; $state.roomPhase = $(if ($e.ContainsKey('phase_id')) { $e.phase_id } else { 'none' }) }
            return (& $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }))
        }
        if ($tool -eq 'horizun_export') {
            # The apply (ExportCobie.cs after RequireConfirmation): a file on disk, re-read.
            $state.applyArgs = $arguments
            $path = [string]$arguments.output_path
            Set-Content -LiteralPath $path -Value 'fake workbook bytes'
            $state.file = $path
            $state.sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($state.mismatch) {
                return @{ stage = 'apply'; answer = @{ isError = $true; data = $null
                          text = "The workbook on disk does not hold what was planned: Space!A2: planned text '001', read text '00l'. Success is not claimed." } }
            }
            $readSheets = @($order | ForEach-Object { [pscustomobject]@{ name = $_; rows = 1; columns = 9 } })
            return (& $ok ([pscustomobject]@{
                format = 'cobie'; requested_output_path = $path; files_verified = 1
                files = @([pscustomobject]@{ path = $path; bytes = 20; sha256 = $state.sha; last_write_utc = '2026-09-27T10:00:00.0000000Z' })
                read_back = [pscustomobject]@{ sheets = $readSheets; cells_sha256 = ('cd' * 32); planned_cells_sha256 = ('cd' * 32); matches_plan = $true; means = 'the file was re-opened from disk' }
                deliverable_ready = $false; blocking = @('required_field: 2')
                cobie = [pscustomobject]@{ created_by = [string]$arguments.cobie.created_by; phase = [string]$arguments.cobie.phase }
                verdict_basis = 'files_verified is the FILE''s' }))
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected apply ' + $tool } }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $root; RunId = 'r1abcdefgh'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

$by = Run-Module (New-Ctx $true)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

$ctx = New-Ctx $false
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
# Every case names its tool as the catalogue does - a reply that overwrote a tool-name
# variable (PowerShell names ignore case) would show up here as a hashtable.
Check 'every case reports the tool its catalogue entry names, as a string' (@($module.Catalog | Where-Object { -not ($by[$_.Name].Tool -is [string]) -or $by[$_.Name].Tool -ne $_.Tool }).Count -eq 0)
foreach ($n in $catalog) {
    Check "passes: $n" ($by[$n].Outcome -eq 'pass')
    if ($by[$n].Outcome -ne 'pass') { "        $($by[$n].Outcome): $($by[$n].Detail)" }
}
$s = $ctx.State
Check 'it stages its own level, four walls, a door and a numbered, named room' ($s.kinds.level -and $s.walls.Count -eq 4 -and $s.door -and $s.room -and $s.roomNumber -eq 'HZC-r1abcdef' -and $s.roomName -eq 'HZ COBIE ROOM')
Check 'the room is placed by point with no phase_id (Revit gives the last phase) and the export names the LAST phase listed' (($s.roomPhase -eq 'none') -and ($s.exportArgs.cobie.phase -eq 'New Construction') -and ($s.applyArgs.cobie.phase -eq 'New Construction'))
Check 'the export takes doors as components, no category_parameter, the probe''s own e-mail, a file under the scratch root' (
    (@($s.exportArgs.cobie.component_categories) -join ',') -eq 'OST_Doors' -and -not $s.exportArgs.cobie.ContainsKey('category_parameter') -and
    $s.exportArgs.cobie.created_by -eq 'cobie-probe@example.com' -and ([string]$s.exportArgs.output_path).StartsWith($ctx.ScratchRoot) -and $s.exportArgs.dry_run -eq $true)
Check 'the rehearsal is a plain dry run and the apply goes through the token path' (-not $s.applyArgs.ContainsKey('dry_run') -and $s.applies.Contains('cobie-apply'))
Check 'every idempotency key is used once' (@($s.applies | Group-Object | Where-Object { $_.Count -gt 1 }).Count -eq 0)
Check 'cleanup deletes the room first and the level last, newest first, never the document''s types' (($s.deleted[0] -eq $s.room) -and ($s.deleted[1] -eq $s.door) -and ($s.deleted -notcontains 42) -and ($s.deleted -notcontains 41) -and ($s.deleted[-1] -eq [long]$s.kinds.level) -and
    ([array]::IndexOf($s.deleted, $s.walls[0]) -lt [array]::IndexOf($s.deleted, [long]$s.kinds.level)))
Check 'the workbook check names the space and door rows it judged' ($by[$catalog[3]].Detail -match 'same file=True' -and $by[$catalog[3]].Detail -match 'HZC-r1abcdef')

$c = New-Ctx $false; $c.State.wrongSpace = $true
$by = Run-Module $c
Check 'a door whose Space is another room fails the workbook case, and only that one' (($by[$catalog[3]].Outcome -eq 'fail') -and ($by[$catalog[4]].Outcome -eq 'pass') -and ($by[$catalog[2]].Outcome -eq 'pass'))

$c = New-Ctx $false; $c.State.dropCreatedBy = $true
$by = Run-Module $c
Check 'a row read back without created_by fails the created_by case' (($by[$catalog[4]].Outcome -eq 'fail') -and ($by[$catalog[4]].Detail -match "Component row 2: ''"))

$c = New-Ctx $false; $c.State.noFinding = $true
$by = Run-Module $c
Check 'no Category finding for the own room fails the finding case' (($by[$catalog[1]].Outcome -eq 'fail') -and ($by[$catalog[0]].Outcome -eq 'pass'))

$c = New-Ctx $false; $c.State.noFinding = $true; $c.State.truncated = $true
$by = Run-Module $c
Check 'an own-room finding missing from a TRUNCATED list is unverified, never pass or fail' (($by[$catalog[1]].Outcome -eq 'unverified') -and ($by[$catalog[1]].Detail -match 'cut at'))

$c = New-Ctx $false; $c.State.mismatch = $true
$by = Run-Module $c
Check 'a workbook that does not re-read as planned fails the apply case; its content is left unjudged, never passed' (
    ($by[$catalog[2]].Outcome -eq 'fail') -and ($by[$catalog[3]].Outcome -eq 'unverified') -and ($by[$catalog[4]].Outcome -eq 'unverified') -and ($by[$catalog[5]].Outcome -eq 'pass'))

$c = New-Ctx $false; $c.State.otherFile = $true
$by = Run-Module $c
Check 'a workbook read back with another sha256 than the reply named fails the workbook case' (($by[$catalog[3]].Outcome -eq 'fail') -and ($by[$catalog[3]].Detail -match 'same file=False'))

$c = New-Ctx $false $false
$by = Run-Module $c
Check 'no types: the export cases are unverified and name why, never pass' ((@($catalog[0..4] | Where-Object { $by[$_].Outcome -ne 'unverified' }).Count -eq 0) -and ($by[$catalog[0]].Detail -match 'in the document'))
Check 'no types: nothing was staged, so there is nothing to delete' (($by[$catalog[5]].Outcome -eq 'unverified') -and (-not $c.State.applies.Contains('cobie-cleanup')))

$c = New-Ctx $false; $c.State.noPhases = $true
$by = Run-Module $c
Check 'phases that cannot be listed leave the export unverified, never assumed' (($by[$catalog[0]].Outcome -eq 'unverified') -and ($by[$catalog[0]].Detail -match 'phases could not be listed'))

if ($fails) { "cobie-workbook probe tests: $fails FAILED"; exit 1 } else { 'cobie-workbook probe tests: ALL PASS'; exit 0 }
