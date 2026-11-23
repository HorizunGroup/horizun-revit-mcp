#Requires -Version 5.1
# Exercises rooms-topo-federation.probes.ps1 WITHOUT Revit. Shapes from the code
# (CreateElementsEnclosed.cs /
# CreateElementsToposolid.cs / CreateElementsLandXml.cs: the plan row's landxml block),
# to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'rooms-topo-federation.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'rooms-topo-federation' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Deleted = @(); Rooms = $false; Spaces = $false; Plan = $false; NextId = 5000; Made = @(); Y2023 = $false; PosAngle = 30; LxInternal = @(); LxFile = '' }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_query_model' {
                $cat = @($a.categories)[0]; $rows = @()
                if ($cat -eq 'OST_Walls') { $rows += [pscustomobject]@{ is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm'; element_id = 700 } }
                if ($cat -eq 'OST_Toposolid') {
                    if ($s.Mode -eq 'notopo') { $rows += [pscustomobject]@{ is_element_type = $true; family = 'Terrain'; type = 'Site'; element_id = 711 } }
                    else { $rows += [pscustomobject]@{ is_element_type = $true; family = 'Toposolid'; type = 'Toposolid'; element_id = 710 } }
                }
                return & $reply ([pscustomobject]@{ rows = $rows })
            }
            'horizun_manage_phases' { return & $reply ([pscustomobject]@{ phases = @([pscustomobject]@{ index = 0; id = 11; name = 'Existing' }, [pscustomobject]@{ index = 1; id = 12; name = 'New Construction' }) }) }
            'horizun_create_elements' {
                # The rehearsal of an all_enclosed entry (ExpandEnclosed + NothingEnclosed shapes).
                $el = @($a.elements)[0]
                if ($el.kind -eq 'toposolid' -and $el.landxml_path -and -not $s.Y2023) {
                    # The rehearsal's plan row carries the landxml block (CreateElementsLandXml.cs); the
                    # position is rotated and offset so the probe's inverse is exercised, not the identity.
                    $s.LxFile = [System.IO.File]::ReadAllText([string]$el.landxml_path)
                    $n = ([regex]::Matches($s.LxFile, '<P ')).Count
                    $lxb = [pscustomobject]@{ path = $el.landxml_path; surface = 'HZ_RT_EG'; sha256 = 'fake'; linear_unit = 'meter'; points_in_file = $n; points_used = $n; points_unused = 0
                        project_position = [pscustomobject]@{ angle_degrees = $s.PosAngle; east_west_m = 1000.5; north_south_m = -2000.25; elevation_m = 2600 } }
                    return & $reply ([pscustomobject]@{ dry_run = $true; requested = 1; plan = @([pscustomobject]@{ index = 0; kind = 'toposolid'; references_resolved = $true; landxml = $lxb }) })
                }
                if ($el.kind -eq 'toposolid') {
                    # Shape from the code: PlanToposolid's refusal is an INVALID ROW of a successful rehearsal
                    # (dry_run defaults to true; CreateElementsCommand plans every row), not a tool error.
                    $e23 = [pscustomobject]@{ index = 0; error = 'toposolid_not_in_revit_2023: Revit 2023 has no Toposolid element (it arrived in Revit 2024). Nothing was planned.' }
                    return & $reply ([pscustomobject]@{ dry_run = $true; transaction_status = 'not_started'; requested = 1; valid = 0; invalid = 1; errors = @($e23); plan = @()
                        fallback = [pscustomobject]@{ recommended_tool = 'horizun_execute_python'; allowed = $false; write_started = $false } }) $false 'rehearsal: 0 valid, 1 invalid'
                }
                if (-not $s.Plan) { return & $reply $null $true "elements[0]: no_floor_plan: level 'HZ_RT_t1' has no floor plan view. Nothing ran." }
                if ($el.phase_id -ne 12) { return & $reply $null $true 'phase_id must be the last phase' }
                $min = if ($el.ContainsKey('min_area_m2')) { [double]$el.min_area_m2 } else { 0 }
                $circuits = @()
                foreach ($c in @(@{ p = @(1153000, 2000); area = 22.04 }, @{ p = @(1157000, 2000); area = 6.84 })) {
                    $action = if ($el.kind -eq 'room' -and $s.Rooms) { 'skipped_has_room' } elseif ($el.kind -eq 'space' -and $s.Spaces) { 'skipped_has_space' } elseif ($c.area -lt $min) { 'skipped_min_area' } else { 'create' }
                    # Rooms: PlanTopology circuits (sides, is_room_located). Spaces: NewSpaces2 regions and standing spaces, neither.
                    if ($el.kind -eq 'room') { $circuits += [pscustomobject]@{ point_inside = $c.p; area_m2 = $c.area; sides = 4; is_room_located = [bool]$s.Rooms; action = $action } }
                    else { $circuits += [pscustomobject]@{ point_inside = $c.p; area_m2 = $c.area; action = $action } }
                }
                $n = @($circuits | Where-Object { $_.action -eq 'create' }).Count
                $blk = [pscustomobject]@{ index = 0; kind = $el.kind; level_id = $el.level_id; phase_id = 12; floor_plan_id = 4999; circuits = $circuits; circuits_seen = 2; to_create = $n
                    regions_from = 'fake'; link_bounding = 'not_proven: whether Room Bounding walls of a LINKED model close a host region is neither established nor measured by this build.' }
                return & $reply ([pscustomobject]@{ dry_run = $true; requested = $n; enclosed = @($blk) })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_delete_verified' { $s.Deleted += @($a.ids); return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_manage_views' { $s.Plan = $true; $s.NextId = $s.NextId + 1; $s.Made += $s.NextId; return & $ok ([pscustomobject]@{ aliases = [pscustomobject]@{ rtplan = $s.NextId } }) }
            'horizun_create_elements' {
                $els = @($a.elements); $rows = @()
                if ($els[0].landxml_path) {
                    # LandXmlTinRules.SharedToInternal, ported: where the add-in would put each point (mm).
                    $t = [System.IO.File]::ReadAllText([string]$els[0].landxml_path)
                    $ang = [double]$s.PosAngle * [math]::PI / 180; $co = [math]::Cos(-$ang); $si = [math]::Sin(-$ang)
                    $s.LxInternal = @([regex]::Matches($t, '<P id="\d+">([^<]+)</P>') | ForEach-Object {
                        $v = @($_.Groups[1].Value -split ' ' | ForEach-Object { [double]::Parse($_, [System.Globalization.CultureInfo]::InvariantCulture) })
                        $re = $v[1] - 1000.5; $rn = $v[0] - (-2000.25)
                        , @((($re * $co - $rn * $si) * 1000), (($re * $si + $rn * $co) * 1000), (($v[2] - 2600) * 1000)) })
                }
                $count = if ($els[0].placement -eq 'all_enclosed') { if ($els[0].kind -eq 'space') { $s.Spaces = $true } else { $s.Rooms = $true }; 2 } else { $els.Count }
                for ($k = 0; $k -lt $count; $k++) { $s.NextId = $s.NextId + 1; $rows += [pscustomobject]@{ element_id = $s.NextId }; $s.Made += $s.NextId }
                return & $ok ([pscustomobject]@{ rows = $rows })
            }
        }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $env:TEMP ('hz-rtf-' + [guid]::NewGuid().ToString('N'))); RunId = 't1'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($r) { ($r | ForEach-Object { $_.Outcome }) -join ',' }
# Results come in the order the cases RUN (case 9 runs right after case 5), so they are found by catalog name.
$names = @($module.Catalog)
function At($r, $i) { @($r | Where-Object { $_.Name -eq $names[$i].Name })[0] }

$h = New-Fake 'ok'; $r = @(& $module.Run $h.Ctx)
Check ($r.Count -eq 10) ('ten cases: ' + $r.Count)
Check (@($r | Where-Object { $_.Outcome -ne 'pass' -and $_.Name -notlike '*link-bounded*' }).Count -eq 0) ('everything but the link-bounded declaration passes: ' + (Outcomes $r))
Check ((At $r 5).Outcome -eq 'pass' -and (At $r 5).Detail -match 'not_proven' -and (At $r 5).Detail -match 'not measured') 'link-bounded circuits pass on the reply''s declaration, and say the geometry is not measured'
Check ((At $r 9).Outcome -eq 'pass' -and (At $r 9).Detail -match 'second call: requested 0') ('spaces: apply fills each region, a second call plans nothing: ' + (At $r 9).Detail)
Check ((At $r 4).Detail -match 'space regions m2 6.8;22') ('the space rehearsal records the region areas: ' + (At $r 4).Detail)
Check (@($h.State.Made | Where-Object { $h.State.Deleted -notcontains $_ }).Count -eq 0 -and $h.State.Made.Count -eq 14) ('every staged id is deleted (2 levels, a floor plan, 5 walls, 2 rooms, 2 spaces, 2 toposolids): ' + $h.State.Made.Count)
$lxi = @($h.State.LxInternal)
$want = @(@(1190000, 0, 1000), @(1200000, 0, 1500), @(1200000, 10000, 3000), @(1190000, 10000, 2000), @(1195000, 5000, 3500))
$worst = 0.0
if ($lxi.Count -eq 5) { for ($k = 0; $k -lt 5; $k++) { for ($d = 0; $d -lt 3; $d++) { $worst = [math]::Max($worst, [math]::Abs([double]$lxi[$k][$d] - $want[$k][$d])) } } }
Check ($lxi.Count -eq 5 -and $worst -lt 0.001) ('a TIN written through a rotated, offset position comes back at the probe''s own X (SharedToInternal ported), worst ' + $worst + ' mm')
Check ((At $r 7).Outcome -eq 'pass' -and (At $r 7).Detail -match 'HZ_RT_EG' -and (At $r 7).Detail -notmatch 'identity') ('the LandXML case names the surface and the position it used: ' + (At $r 7).Detail)
Check ($h.State.LxFile -match 'linearUnit="meter"' -and $h.State.LxFile -notmatch '<!DOCTYPE') 'the probe''s file declares its unit and carries no DTD'

$h = New-Fake 'ok'; $h.Ctx.Year = 2023; $h.State.Y2023 = $true; $r = @(& $module.Run $h.Ctx)
Check ((At $r 6).Outcome -eq 'pass' -and (At $r 6).Detail -match 'toposolid_not_in_revit_2023') ('2023 reports the named refusal: ' + (At $r 6).Outcome)
Check ((At $r 7).Outcome -eq 'pass' -and (At $r 7).Detail -match 'toposolid_not_in_revit_2023') ('2023 refuses a landxml_path row by the same name: ' + (At $r 7).Outcome)

$h = New-Fake 'notopo'; $r = @(& $module.Run $h.Ctx)
Check ((At $r 6).Outcome -eq 'not_covered' -and (At $r 6).Detail -match 'Terrain: Site') ('no toposolid type by name: not_covered, naming what it saw: ' + (At $r 6).Detail)
Check ((At $r 7).Outcome -eq 'not_covered') ('no toposolid type: the LandXML case is not_covered too: ' + (At $r 7).Outcome)

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = @(& $module.Run $h.Ctx)
Check ($r.Count -eq 10 -and @($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0) ('write tier closed: rooms, spaces and toposolid cases are not_covered: ' + (Outcomes $r))

if ($fail -gt 0) { Write-Host "$fail check(s) failed"; exit 1 }
Write-Host 'all checks passed'
