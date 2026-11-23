#Requires -Version 5.1
# Exercises export-formats.probes.ps1 WITHOUT Revit. The fakes' reply shapes are
# taken from the code (ExportSets.cs: files[].main.header, files_verified,
# read_back, families, saved_in_format) - shapes from the code, to be held against
# the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'export-formats.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'export-formats' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$hasRooms = $true, [bool]$withWallType = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; nextId = 900; copied = $withWallType; created = @{}; deleted = @(); mainReads = 0; mainFlip = $false; sheet = $null; companionRole = 'xref'; noDoors = $false }
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-export-probe-test-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Force -Path $root
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_model') {
            $rows = if ($arguments.categories[0] -eq 'OST_Walls' -and $state.copied) { @([pscustomobject]@{ element_id = 41; family = 'Basic Wall'; type = 'Generic - 200mm'; is_element_type = $true }) } else { @() }
            return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
        }
        if ($tool -eq 'horizun_query_planimetry') {
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ view_id = 500; view_type = 'FloorPlan'; is_template = $false }) } }
        }
        if ($tool -ne 'horizun_export') { return @{ isError = $true; text = 'unexpected tool ' + $tool } }
        if ($arguments.format -eq 'dwg' -and $arguments.file_naming -eq 'sheet_number') { return @{ isError = $true; text = 'file_naming=sheet_number names sheets only; view 900 is not a sheet. Nothing was exported.' } }
        if ($arguments.format -eq 'gbxml') {
            if (-not $hasRooms) { return @{ isError = $true; text = 'no spaces: the document has no placed, bounded room or space' } }
            $state.mainReads++
            $present = $state.mainFlip -and $state.mainReads -gt 1
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; placed_rooms = 3; main_energy_model_present = $present } }
        }
        if ($arguments.format -eq 'rfa' -and $arguments.category -eq 'OST_Walls') { return @{ isError = $true; text = 'No loadable family to export in category ''OST_Walls''. Nothing was exported.' } }
        if ($arguments.format -eq 'rfa') {
            if ($state.noDoors) { return @{ isError = $true; text = "No loadable family to export in category 'OST_Doors'. Nothing was exported." } }
            $fams = @([pscustomobject]@{ id = 71; name = 'Single-Flush' }, [pscustomobject]@{ id = 70; name = 'Double-Glass' })
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; families = $fams } }
        }
        return @{ isError = $true; text = 'unexpected export call' }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        $ok = { param($data) @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' } } }
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = @($arguments.ids); return (& $ok ([pscustomobject]@{})) }
        if ($tool -eq 'horizun_create_elements') {
            $id = $state.nextId; $state.nextId++
            $kind = [string]$arguments.elements[0].kind
            if ($kind -eq 'wall') { $state.created['wall' + $state.created.Count] = $id } else { $state.created[$kind] = $id }
            return (& $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }))
        }
        if ($tool -eq 'horizun_manage_views') {
            $rows = @($arguments.actions | ForEach-Object {
                $id = $state.nextId; $state.nextId++
                if ($_.operation -eq 'create_sheet') { $state.sheet = $id }
                [pscustomobject]@{ operation = [string]$_.operation; element_id = $id; verified = $true } })
            return (& $ok ([pscustomobject]@{ rows = $rows }))
        }
        $folder = Split-Path $arguments.output_path
        switch ($arguments.format) {
            { $_ -in 'dwg', 'dgn', 'dwfx' } {
                $header = @{ dwg = 'AC1032'; dgn = 'dgn_v8_structured_storage'; dwfx = 'zip_package' }[$arguments.format]
                # A sheet exported unmerged (dwg linked, dgn) writes its placed view beside it as an xref.
                $unmerged = ($arguments.format -eq 'dgn') -or ($arguments.format -eq 'dwg' -and $arguments.dwg_xrefs -ne 'bound')
                $files = @($arguments.view_ids | ForEach-Object {
                    $f = Join-Path $folder ("set-$_." + $arguments.format); Set-Content -LiteralPath $f -Value 'x'
                    $comp = if ($_ -eq $state.sheet -and $unmerged) { @([pscustomobject]@{ path = ($f -replace '\.\w+$', ('-View.' + $arguments.format)); bytes = 900; header = $header; role = $state.companionRole }) } else { @() }
                    [pscustomobject]@{ view_id = $_; file = $f; verified = $true; main = [pscustomobject]@{ path = $f; bytes = 1024; header = $header; header_verified = $true }; other_files = $comp } })
                return (& $ok ([pscustomobject]@{ format = $arguments.format; files_planned = $files.Count; files_verified = $files.Count; files = $files }))
            }
            'gbxml' { return (& $ok ([pscustomobject]@{ files_verified = 1; placed_rooms = 0; placed_spaces = 1; energy_scope = "MEP spaces of phase 'New Construction'"; read_back = [pscustomobject]@{ root = 'gbXML'; space = 1; zone = 1; surface = 6 } })) }
            'rfa' {
                $f = Join-Path $arguments.output_path 'Double-Glass.rfa'; Set-Content -LiteralPath $f -Value 'x'
                return (& $ok ([pscustomobject]@{ files_verified = 1; files = @([pscustomobject]@{ id = 70; file = $f; verified = $true; saved_in_format = '2026'; bytes = 40960 }) }))
            }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected apply' } }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $root; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

$by = Run-Module (New-Ctx $true)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

$ctx = New-Ctx $false $true
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
foreach ($n in $catalog) { Check "passes: $n" ($by[$n].Outcome -eq 'pass') }
Check 'the rfa case exports the family first by NAME, not by position' ($by[$catalog[5]].Detail -match 'Double-Glass')
Check 'the probe deletes the views it duplicated' ($ctx.State.applies.Contains('exp-cleanup'))
Check 'gbxml stages its own level, four walls, a room and a space' ($ctx.State.created.level -and $ctx.State.created.room -and $ctx.State.created.space -and $ctx.State.created.wall3)
Check 'the staged gbxml model is deleted newest first, never the document''s wall type' (($ctx.State.deleted -notcontains 41) -and ($ctx.State.deleted[0] -eq $ctx.State.created.space))

$bare = New-Ctx $false $true; $bare.State.noDoors = $true
$byBare = Run-Module $bare
Check 'a document with no loadable door is not_covered for the rfa case, never a pass' ($byBare[$catalog[5]].Outcome -eq 'not_covered')
Check 'the sheet cases export the own sheet, and the sheet is deleted with the views' (($ctx.State.applies.Contains('exp-sheet-dwg-linked')) -and ($ctx.State.applies.Contains('exp-sheet-dwg-bound')) -and ($ctx.State.applies.Contains('exp-sheet-dgn')) -and ($ctx.State.deleted -contains $ctx.State.sheet))
$odd = New-Ctx $false $true; $odd.State.companionRole = 'unexpected'
$byOdd = Run-Module $odd
Check 'a companion not named as an xref fails the linked and dgn sheet cases, bound still passes' (($byOdd[$catalog[7]].Outcome -eq 'fail') -and ($byOdd[$catalog[9]].Outcome -eq 'fail') -and ($byOdd[$catalog[8]].Outcome -eq 'pass'))
Check 'gbxml reads the main energy model before and after the export' (($by[$catalog[4]].Detail -match 'main_energy_model_present False -> False') -and ($ctx.State.mainReads -eq 2))
$flip = New-Ctx $false $true; $flip.State.mainFlip = $true
$by = Run-Module $flip
Check 'a main energy model that appeared across the export fails the gbxml case' (($by[$catalog[4]].Outcome -eq 'fail') -and ($by[$catalog[4]].Detail -match 'False -> True'))

$by = Run-Module (New-Ctx $false $false)
Check 'own room and space staged yet refused as no spaces: gbxml FAILS, never unverified' ($by[$catalog[4]].Outcome -eq 'fail' -and $by[$catalog[4]].Detail -match 'no spaces')

$by = Run-Module (New-Ctx $false $true $false)
Check 'no wall type: gbxml is unverified and names why, never pass' ($by[$catalog[4]].Outcome -eq 'unverified' -and $by[$catalog[4]].Detail -match 'holds no')

if ($fails) { "export-formats probe tests: $fails FAILED"; exit 1 } else { 'export-formats probe tests: ALL PASS'; exit 0 }
