#Requires -Version 5.1
# Exercises quantities-rooms.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply whose
# replies copy the shapes the code builds - QuantitiesRoomFinishes.cs (rows[] with room_id,
# surface, gross_m2, openings_deduction_m2, opening_ids, opening_size_basis,
# bounding_element_keys; not_measured[] with id/state/reason; the phase refusal that lists
# the document's phases), QuantitiesCarbon.cs (rows[] per material with volume_m3, mass_kg,
# kgco2e, counted, no_density; materials_without_factor/_density; coverage.counted),
# RoomMembershipReader.cs (row.room {state, room{id}, basis}; room_membership) and
# QuantitiesByRoom.cs (by_room keyed by room id and '(unassigned)').
# SHAPES FROM THE CODE, TO BE HELD AGAINST THE FIRST LIVE RUN.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'quantities-rooms.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'quantities-rooms' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }
function Obj($pairs) { $o = New-Object psobject; foreach ($k in $pairs.Keys) { $o | Add-Member -NotePropertyName $k -NotePropertyValue $pairs[$k] }; $o }

$tpl = Join-Path ([IO.Path]::GetTempPath()) ('hz-rooms-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tpl | Out-Null

function New-State { $script:nextId = 5000; $script:ids = @{}; $script:westGone = $false; $script:cleanup = $null }
$script:variant = 'good'
$types = @{
    OST_Walls   = [pscustomobject]@{ is_element_type = $true; type = 'Exterior - Brick on Mtl. Stud'; family = 'Basic Wall'; element_id = 101 }
    OST_Doors   = [pscustomobject]@{ is_element_type = $true; type = '0915 x 2134mm'; family = 'M_Single-Flush'; element_id = 102 }
    OST_Windows = [pscustomobject]@{ is_element_type = $true; type = '0915 x 1220mm'; family = 'M_Fixed'; element_id = 103 }
    OST_Floors  = [pscustomobject]@{ is_element_type = $true; type = 'Generic 150mm'; family = 'Floor'; element_id = 104 }
}
function Id($k) { $script:ids[$k] }
function WallIds { @('wall-s', 'wall-e', 'wall-n', 'wall-w') | ForEach-Object { Id $_ } }
function RoomRef { [pscustomobject]@{ id = (Id 'room'); number = 'HZRMr1'; name = 'HZ_RM_ROOM_r1'; level = 'HZ_RM_r1' } }
function Hit($state, $basis, $inRoom) {
    [pscustomobject]@{ state = $state; room = $(if ($inRoom) { RoomRef } else { $null }); space = $null; basis = $basis; sample_point = @(1111000.0, 1000.0, 95001.0) }
}

function Fake-Call($tool, $a) {
    if ($tool -eq 'horizun_query_model' -and $a.include_types) { return Reply ([pscustomobject]@{ rows = @($types[$a.categories[0]]) }) $false '' }
    if ($tool -eq 'horizun_query_model') {
        if (-not $a.phase) { return Reply $null $true 'include_room: phase is required: rooms and spaces exist per phase, and a hidden default (the last phase) would silently place elements in a different building. Name the phase.' }
        $rows = @()
        foreach ($id in $a.element_ids) {
            $hit = if ($id -eq (Id 'floor')) { Hit 'assigned' 'floor_top_face' $true }
                   elseif ($id -eq (Id 'space')) { Hit 'assigned' 'location_point' $true }
                   elseif ($script:variant -eq 'bounding-assigned' -and $id -eq (Id 'wall-s')) { Hit 'assigned' 'wall_solid_centroid' $true }
                   else { Hit 'unassigned' 'wall_solid_centroid' $false }
            $rows += [pscustomobject]@{ element_id = [long]$id; room = $hit }
        }
        return Reply ([pscustomobject]@{ rows = $rows; room_membership = [pscustomobject]@{ phase = $a.phase; assigned = 2; unassigned = ($rows.Count - 2); unlocatable = 0; in_space = 0; complete = $true; room_boundary_location = 'Finish' } }) $false ''
    }
    if ($a.mode -eq 'room_finishes') {
        if (-not $a.phase) { return Reply $null $true "mode 'room_finishes': phase is required: rooms, spaces and the doors facing them are phase-dependent, and a hidden default (the last phase) would silently measure a different building. Name the phase. Nothing was measured." }
        if ($a.phase -like 'HZ_NO_SUCH_PHASE*') { return Reply $null $true "mode 'room_finishes': No phase is named '$($a.phase)'. Phases in this document: Existing, New Construction. Nothing was measured." }
        $room = Id 'room'
        if ($a.phase -eq 'Existing') {
            return Reply ([pscustomobject]@{ mode = 'room_finishes'; phase = 'Existing'; rows = @(); not_measured = @([pscustomobject]@{ id = $room; kind = 'room'; state = 'other_phase'; reason = 'It belongs to another phase (id 2).' }) }) $false ''
        }
        if ($script:westGone) {
            return Reply ([pscustomobject]@{ mode = 'room_finishes'; phase = $a.phase; rows = @(); not_measured = @([pscustomobject]@{ id = $room; kind = 'room'; number = 'HZRMr1'; name = 'HZ_RM_ROOM_r1'; level = 'HZ_RM_r1'; state = 'not_enclosed'; reason = 'Its boundaries do not close, so Revit gives it no area. Not a zero.' }) }) $false ''
        }
        $openingIds = if ($script:variant -eq 'no-door-deduction') { @((Id 'window')) } else { @((Id 'door'), (Id 'window')) }
        $deduction = if ($script:variant -eq 'no-door-deduction') { 1.1163 } else { 3.0689 }
        $wallRow = [pscustomobject]@{ room_id = [long]$room; kind = 'room'; surface = 'wall'; bounding_type = 'Walls: Exterior - Brick on Mtl. Stud'; material = 'Gypsum Wall Board'; material_source = 'face'; code = $null
            gross_m2 = 52.7481; openings_deduction_m2 = $deduction; openings = $openingIds.Count; openings_unsized = 0; opening_size_basis = @('rough')
            opening_ids = $openingIds; bounding_element_keys = @(WallIds | ForEach-Object { 'host:' + $_ }) }
        $floorRow = [pscustomobject]@{ room_id = [long]$room; kind = 'room'; surface = 'floor'; bounding_type = 'Floors: Generic 150mm'; material = 'Concrete, Cast-in-Place gray'; material_source = 'face'; code = $null
            gross_m2 = 18.9; openings_deduction_m2 = $null; openings = 0; openings_unsized = 0; opening_size_basis = @(); opening_ids = @(); bounding_element_keys = @('host:' + (Id 'floor')) }
        return Reply ([pscustomobject]@{ mode = 'room_finishes'; phase = $a.phase; rows = @($wallRow, $floorRow); rows_total = 2; not_measured = @()
            totals_by_kind = [pscustomobject]@{ room = [pscustomobject]@{ wall_gross_m2 = 52.7481; floor_gross_m2 = 18.9; ceiling_gross_m2 = 0; openings_deduction_m2 = $deduction } }
            openings = @($openingIds | ForEach-Object { [pscustomobject]@{ room_id = [long]$room; bounding_element_key = 'host:' + (Id 'wall-s'); insert_id = $_; insert_kind = 'door'; width_m = 0.915; height_m = 2.134; area_m2 = 1.9526; size_basis = 'rough' } }) }) $false ''
    }
    if ($a.mode -eq 'carbon') {
        # QuantitiesModeArguments.cs: a key carbon does not read is refused before anything is measured.
        if ($a.phase -and $script:variant -ne 'carbon-ignores-phase') { return Reply $null $true "'phase' is not read in mode 'carbon': it would be silently ignored, and the reply would read as though it had been honoured. It is read by: room_finishes, takeoff with group_by='room'. Drop it, or use that mode. Nothing was measured." }
        $table = @{}; foreach ($f in $a.carbon_factors) { $table[[string]$f.material] = $f }
        $mats = @(@{ n = 'Brick, Common'; v = 1.234567; m = 2400.0 }, @{ n = 'Gypsum Wall Board'; v = 0.5; m = 500.0 }, @{ n = 'Concrete, Cast-in-Place gray'; v = 2.8; m = $null })
        $rows = @(); $without = @(); $total = 0.0; $counted = 0
        foreach ($x in $mats) {
            $f = $table[$x.n]; $kg = $null; $c = 0; $nf = 0
            if (-not $f) { $nf = 1; $without += $x.n }
            elseif ($f.per -eq 'm3') { $kg = [math]::Round($x.v * $f.factor, 3); $c = 1 }
            else { $kg = [math]::Round($x.m * $f.factor, 3); $c = 1 }
            if ($kg) { $total += $kg; $counted += $c }
            $rows += [pscustomobject]@{ material = $x.n; material_class = $null; code = $null; level = 'HZ_RM_r1'; factor = $(if ($f) { $f.factor } else { $null }); factor_per = $(if ($f) { $f.per } else { $null })
                matched_by = $(if ($f) { 'material' } else { $null }); volume_m3 = $x.v; area_m2 = 10.0; mass_kg = $x.m; mass_readings = $(if ($x.m) { 1 } else { 0 })
                kgco2e = $kg; readings = 1; counted = $c; no_factor = $nf; no_density = 0; unreadable_volume = 0; complete = ($c -eq 1) }
        }
        return Reply ([pscustomobject]@{ mode = 'carbon'; factor_source = $a.factor_source; rows = $rows; rows_total = $rows.Count; kgco2e_counted_total = $total
            materials_without_factor = $without; materials_without_density = @(); unreadable_volumes = @(); elements_without_materials = @(); failed = @()
            coverage = [pscustomobject]@{ elements = @($a.element_ids).Count; readings = 3; counted = $counted; complete = ($without.Count -eq 0) } }) $false ''
    }
    if ($a.mode -eq 'takeoff') {
        $byRoom = Obj ([ordered]@{ ([string](Id 'room')) = [pscustomobject]@{ room = (RoomRef); elements = 1 }; '(unassigned)' = [pscustomobject]@{ room = $null; elements = 1 } })
        return Reply ([pscustomobject]@{ mode = 'takeoff'; by_code = [pscustomobject]@{}; by_room = $byRoom
            rows = @([pscustomobject]@{ element_id = (Id 'floor'); room = [pscustomobject]@{ state = 'assigned'; room_id = (Id 'room'); basis = 'floor_top_face'; reason = $null } })
            room_membership = [pscustomobject]@{ phase = $a.phase; assigned = 1; unassigned = 1; unlocatable = 0 } }) $false ''
    }
    throw "unexpected call $tool"
}
function Fake-Apply($tool, $a, $key) {
    $suffix = ([string]$key) -replace '^.*?-rm-', ''
    if ($tool -eq 'horizun_create_elements') {
        $id = $script:nextId++; $script:ids[$suffix] = $id
        return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }) $false '') }
    }
    if ($tool -eq 'horizun_delete_verified') {
        if ($suffix -eq 'open') { $script:westGone = $true } else { $script:cleanup = @($a.ids) }
        # horizun_delete_verified takes mode='ids' + ids (MEASURED 2026-09-27: element_ids is refused)
        return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ deleted = @($a.ids) }) $false '') }
    }
    return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') }
}
function Run-Probe($writeGate) {
    New-State
    $ctx = @{ WriteGate = $writeGate; Document = 'HZ_WRITE'; RunId = 'r1'; Year = 2026; ScratchRoot = $tpl
              Call = { param($tool, $a) Fake-Call $tool $a }; Apply = { param($tool, $a, $key) Fake-Apply $tool $a $key } }
    @(& $module.Run $ctx)
}
function Outcomes($res) { ($res | ForEach-Object { $_.Outcome }) -join ',' }

"quantities-rooms probes (offline)"
$res = Run-Probe $true
Check 'write gate closed: every case not_covered' ((@($res | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 10) -and $res.Count -eq 10)

$script:variant = 'good'
$res = Run-Probe $false
$bad = @($res | Where-Object { $_.Outcome -ne 'pass' } | ForEach-Object { $_.Name + ' => ' + $_.Outcome + ': ' + $_.Detail })
Check ('good model: all 10 cases pass' + $(if ($bad.Count) { ' | ' + ($bad -join ' || ') } else { '' })) ($res.Count -eq 10 -and $bad.Count -eq 0)
Check 'the phase is the one that measured the room (New Construction), never assumed' (([string]$res[0].Detail) -match "phase 'New Construction'")
Check 'cleanup deletes every created id but the west wall already deleted' ((@($script:cleanup).Count -eq 10) -and (@($script:cleanup) -notcontains (Id 'wall-w')))

$script:variant = 'no-door-deduction'
$res = Run-Probe $false
Check 'a door missing from the deduction fails the room_finishes case' ($res[0].Outcome -eq 'fail' -and ([string]$res[0].Detail) -match 'door')

$script:variant = 'bounding-assigned'
$res = Run-Probe $false
Check 'a bounding wall assigned to a room at Finish fails the membership case' ($res[4].Outcome -eq 'fail' -and ([string]$res[4].Detail) -match 'bounding walls')

$script:variant = 'carbon-ignores-phase'
$res = Run-Probe $false
$refusal = @($res | Where-Object { $_.Name -like 'rooms carbon: a phase*' }) | Select-Object -First 1
Check 'a carbon call that ignores the phase fails the refusal case' ($refusal -and $refusal.Outcome -eq 'fail')

Remove-Item -LiteralPath $tpl -Recurse -Force -ErrorAction SilentlyContinue
if ($fails -gt 0) { "FAILED: $fails"; exit 1 } else { 'ALL PASS'; exit 0 }
