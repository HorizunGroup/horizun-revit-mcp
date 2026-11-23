# Live probe module: rooms in every enclosed circuit and toposolids from points (see
# README.md). Loaded by verify-live.ps1; exercised without Revit by
# rooms-topo-federation.tests.ps1. The document is never saved.
#
# rooms/spaces all_enclosed: the own level gets its own floor plan (Revit places a room in
# a circuit, and finds a level's space regions, only through a plan view of that level).
# Whether Room Bounding walls of a LINKED model close a host region is NOT measured here -
# that needs a linked model whose own walls enclose one; the case records the reply's words.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'rooms-topo-federation'
    Catalog = @(
        @{ Name = 'rooms all_enclosed: the rehearsal lists both circuits of the own level and phase'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: min_area_m2 = 10 skips the small circuit (skipped_min_area)'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: apply creates two rooms, verified'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: a second call finds every circuit filled and plans nothing'; Tool = 'horizun_create_elements' }
        @{ Name = 'spaces all_enclosed: the rehearsal lists as many NewSpaces2 regions as room circuits'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: link-bounded circuits are declared not proven'; Tool = 'horizun_create_elements' }
        @{ Name = 'toposolid: six points on an own level, top re-read (2024+) or refused by name (2023)'; Tool = 'horizun_create_elements' }
        @{ Name = 'toposolid: a LandXML TIN in shared coordinates, placed through the project position (2024+) or refused by name (2023)'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms-topo probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
        @{ Name = 'spaces all_enclosed: apply creates a verified space in each region; a second call plans nothing'; Tool = 'horizun_create_elements' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'rooms-topo-federation' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Short($a) { if ($null -eq $a) { return '(no answer)' }; $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        $doc = $Ctx.Document; $run = $Ctx.RunId

        # ---- rooms/spaces in every enclosed circuit, and a toposolid from points ----------
        # Staged far from the model (X = 1,150,000 mm) on levels of its own, so every circuit
        # of the level is one the probe drew: a 6 x 4 m and a 2 x 4 m bay (wall centrelines)
        # sharing a wall - one over and one under min_area_m2 = 10 whichever face Revit
        # measures to. The toposolid's level sits at 500 mm so an absolute and a level-relative
        # reading of Z differ, which is the convention the verification asserts (absolute).
        $CE = 'horizun_create_elements'
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $names.Count; $i++) { Case $i 'not_covered' 'the write tier is closed for this run' }
            return $out.ToArray()
        }
        $created = New-Object System.Collections.Generic.List[long]
        function Rows($r) { if ($r -and $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) { return @($r.answer.data.rows | Where-Object { $_ -and $_.element_id }) }; return @() }
        function Circ($reply) {
            $b = @($reply.data.enclosed | Where-Object { $_ }) | Select-Object -First 1
            if ($b) { return @($b.circuits | Where-Object { $_ }) }; return @()
        }
        # A LandXML TIN of ONE surface, in metres and SHARED coordinates: each [x, y, z] (internal
        # mm) goes through the inverse of LandXmlTinRules.SharedToInternal - rotate by the
        # position's angle, then add its offsets - so the add-in's own conversion brings it back to
        # the probe's internal X. Faces 1 2 5 / 2 3 5 / 3 4 5 / 4 1 5: four corners and a centre.
        $lx = $null
        function Write-TinFile($path, $pts, $pos) {
            $inv = [System.Globalization.CultureInfo]::InvariantCulture
            $a = [double]$pos.angle_degrees * [math]::PI / 180
            $cos = [math]::Cos($a); $sin = [math]::Sin($a)
            $sb = New-Object System.Text.StringBuilder
            for ($k = 0; $k -lt $pts.Count; $k++) {
                $x = [double]$pts[$k][0] / 1000; $y = [double]$pts[$k][1] / 1000
                $east = $x * $cos - $y * $sin + [double]$pos.east_west_m
                $north = $x * $sin + $y * $cos + [double]$pos.north_south_m
                $elev = [double]$pts[$k][2] / 1000 + [double]$pos.elevation_m
                $null = $sb.Append('<P id="' + ($k + 1) + '">' + $north.ToString('R', $inv) + ' ' + $east.ToString('R', $inv) + ' ' + $elev.ToString('R', $inv) + '</P>')
            }
            $xml = '<?xml version="1.0" encoding="UTF-8"?><LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2" version="1.2">' +
                   '<Units><Metric linearUnit="meter" areaUnit="squareMeter"/></Units><Surfaces><Surface name="HZ_RT_EG"><Definition surfType="TIN"><Pnts>' +
                   $sb.ToString() + '</Pnts><Faces><F>1 2 5</F><F>2 3 5</F><F>3 4 5</F><F>4 1 5</F></Faces></Definition></Surface></Surfaces></LandXML>'
            [System.IO.File]::WriteAllText($path, $xml, (New-Object System.Text.UTF8Encoding($false)))
        }
        function TypeNamed($category, $family, $type) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            return @(@($q.data.rows) | Where-Object { $_ -and $_.is_element_type -and [string]$_.type -eq $type -and [string]$_.family -eq $family }) | Select-Object -First 1
        }
        function Bring($category, $family, $type) { return TypeNamed $category $family $type }
        try {
            $X = 1150000.0; $E = 71000.0
            $wallType = Bring 'OST_Walls' 'Basic Wall' 'Generic - 200mm'
            $lv = @(Rows (& $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = ('HZ_RT_' + $run); elevation = $E }) } ($run + '-rt-level')))
            $level = if ($lv.Count) { [long]$lv[0].element_id } else { $null }
            if ($level) { $created.Add($level) }
            # NewRoom(Room, PlanCircuit) throws for a level without a view, and NewSpaces2 finds space
            # regions through one: the level gets its own floor plan, deleted with the rest.
            $planId = $null
            if ($level) {
                $pv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'create_floor_plan'; level_id = [long]$level; name = ('HZ_RT_PLAN_' + $run); key = 'rtplan' }) } ($run + '-rt-plan')
                if ($pv.stage -eq 'apply' -and -not $pv.answer.isError -and $pv.answer.data.aliases.rtplan) { $planId = [long]$pv.answer.data.aliases.rtplan; $created.Add($planId) }
            }
            $walls = @()
            if ($level -and $wallType) {
                $segs = @(@(0, 0, 8000, 0), @(8000, 0, 8000, 4000), @(8000, 4000, 0, 4000), @(0, 4000, 0, 0), @(6000, 0, 6000, 4000))
                $wallRows = @($segs | ForEach-Object { @{ kind = 'wall'; start = @(($X + $_[0]), $_[1], $E); end = @(($X + $_[2]), $_[3], $E); level_id = $level; type_id = [long]$wallType.element_id; height = 3000 } })
                $walls = @(Rows (& $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = $wallRows } ($run + '-rt-walls')))
                foreach ($w in $walls) { $created.Add([long]$w.element_id) }
            }
            $ph = & $Ctx.Call 'horizun_manage_phases' @{ operation = 'list'; target_document = $doc }
            $phase = @($ph.data.phases | Where-Object { $_ }) | Select-Object -Last 1
            $phaseId = if ($phase) { [long]$phase.id } else { $null }
            if (-not ($level -and $planId -and $walls.Count -eq 5 -and $phaseId)) {
                $why = "staging incomplete: level {0}, floor plan {4}, walls {1}/5 ('Basic Wall: Generic - 200mm' {2}), last phase {3}" -f $level, $walls.Count, $(if ($wallType) { 'found' } else { 'not found' }), $phaseId, $planId
                foreach ($i in @(4, 5, 6, 7, 8, 9, 13)) { Case $i 'unverified' $why }
            }
            else {
                $roomEntry = @{ kind = 'room'; placement = 'all_enclosed'; level_id = $level; phase_id = $phaseId }
                $dry = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @($roomEntry) }
                $circ = @(Circ $dry)
                $blk = @($dry.data.enclosed | Where-Object { $_ }) | Select-Object -First 1
                Case 0 $(if (-not $dry.isError -and $circ.Count -ge 2 -and [int]$blk.to_create -ge 2) { 'pass' } else { 'fail' }) ('circuits: ' + ($circ | ConvertTo-Json -Compress -Depth 4) + ' ' + (Short $dry))
                $small = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(($roomEntry + @{ min_area_m2 = 10 })) }
                $sc = @(Circ $small)
                $ok = @($sc | Where-Object { $_.action -eq 'skipped_min_area' }).Count -ge 1 -and @($sc | Where-Object { $_.action -eq 'create' }).Count -ge 1
                Case 1 $(if ($ok) { 'pass' } else { 'fail' }) ('actions: ' + (($sc | ForEach-Object { '{0} m2 {1}' -f $_.area_m2, $_.action }) -join ', ') + ' ' + (Short $small))
                $app = & $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @($roomEntry) } ($run + '-rt-rooms')
                $rooms = @(Rows $app)
                foreach ($rm in $rooms) { $created.Add([long]$rm.element_id) }
                Case 2 $(if ($rooms.Count -eq 2) { 'pass' } else { 'fail' }) ('{0} rooms: {1}' -f $rooms.Count, (Short $app.answer))
                $again = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @($roomEntry) }
                $ac = @(Circ $again)
                $ok = -not $again.isError -and [int]$again.data.requested -eq 0 -and $ac.Count -ge 2 -and @($ac | Where-Object { $_.action -ne 'skipped_has_room' }).Count -eq 0
                Case 3 $(if ($ok) { 'pass' } else { 'fail' }) ('requested {0}; actions {1}' -f $again.data.requested, (($ac | ForEach-Object { $_.action }) -join ','))
                $sp = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'space'; placement = 'all_enclosed'; level_id = $level; phase_id = $phaseId }) }
                $spc = @(Circ $sp)
                # Space regions come from NewSpaces2 (bounded by space separators, not room separators);
                # with no separator staged they must be as many as the room circuits. Areas are recorded.
                $spCreate = @($spc | Where-Object { $_.action -eq 'create' })
                $areas = { param($list) (@($list | ForEach-Object { [math]::Round([double]$_.area_m2, 1) } | Sort-Object) -join ';') }
                $ok = -not $sp.isError -and $spCreate.Count -ge 2 -and $spCreate.Count -eq $circ.Count
                Case 4 $(if ($ok) { 'pass' } else { 'fail' }) ('room circuits m2 ' + (& $areas $circ) + ' | space regions m2 ' + (& $areas $spc) + ' ' + (Short $sp))
                $lb = [string]$blk.link_bounding
                # The case asserts the DECLARATION, which is what this build promises: link-bounded
                # circuits are reported not_proven, never counted as enclosed. Whether linked walls
                # really close a region is not measured, and the detail says so.
                Case 5 $(if ($lb -match '^not_proven') { 'pass' } else { 'fail' }) ('the reply declares it (the geometry itself is not measured by this probe): ' + $lb)
                $spEntry = @{ kind = 'space'; placement = 'all_enclosed'; level_id = $level; phase_id = $phaseId }
                $spApp = & $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @($spEntry) } ($run + '-rt-spaces')
                $spaces = @(Rows $spApp)
                foreach ($spEl in $spaces) { $created.Add([long]$spEl.element_id) }
                $spAgain = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @($spEntry) }
                $sa = @(Circ $spAgain)
                $spBlk = @($spAgain.data.enclosed | Where-Object { $_ }) | Select-Object -First 1
                $ok = $spaces.Count -ge 2 -and $spaces.Count -eq $spCreate.Count -and -not $spAgain.isError -and [int]$spAgain.data.requested -eq 0 -and
                      @($sa | Where-Object { $_.action -eq 'skipped_has_space' }).Count -eq $spaces.Count
                Case 9 $(if ($ok) { 'pass' } else { 'fail' }) ('{0} spaces: {1} | second call: requested {2}, actions {3}, revit_said {4}' -f $spaces.Count, (Short $spApp.answer), $spAgain.data.requested, (($sa | ForEach-Object { $_.action }) -join ','), $spBlk.revit_said)
            }

            if ([int]$Ctx.Year -le 2023) {
                $t23 = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'toposolid'; level_id = $(if ($level) { $level } else { 1 }); type_id = 1; points = @(@(0, 0, 1000), @(1000, 0, 1000), @(0, 1000, 2000)) }) }
                $said = ([string]$t23.text) + ' ' + ($t23.data | ConvertTo-Json -Compress -Depth 6)
                Case 6 $(if ($said -match 'toposolid_not_in_revit_2023') { 'pass' } else { 'fail' }) $(if ($said -match 'toposolid_not_in_revit_2023[^"]{0,160}') { $Matches[0] } else { Short $t23 })
                New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
                $lx = Join-Path $Ctx.ScratchRoot ('HZ_RT_TIN_' + ($run -replace '[^A-Za-z0-9]', '') + '.xml')
                Write-TinFile $lx @(@(0, 0, 1000), @(1000, 0, 1000), @(1000, 1000, 2000), @(0, 1000, 1500), @(500, 500, 2500)) ([pscustomobject]@{ angle_degrees = 0; east_west_m = 0; north_south_m = 0; elevation_m = 0 })
                $l23 = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'toposolid'; level_id = $(if ($level) { $level } else { 1 }); type_id = 1; landxml_path = $lx }) }
                $said = ([string]$l23.text) + ' ' + ($l23.data | ConvertTo-Json -Compress -Depth 6)
                Case 7 $(if ($said -match 'toposolid_not_in_revit_2023') { 'pass' } else { 'fail' }) $(if ($said -match 'toposolid_not_in_revit_2023[^"]{0,160}') { $Matches[0] } else { Short $l23 })
            }
            else {
                $tl = @(Rows (& $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = ('HZ_RT_TOPO_' + $run); elevation = 500 }) } ($run + '-rt-topolevel')))
                $topoLevel = if ($tl.Count) { [long]$tl[0].element_id } else { $null }
                if ($topoLevel) { $created.Add($topoLevel) }
                $topoType = Bring 'OST_Toposolid' 'Toposolid' 'Toposolid'
                # The system family's default type is 'Toposolid 1' in the 2026 fixture (MEASURED 2026-09-27);
                # the top is re-read from the points, so any type of the Toposolid family serves.
                if (-not $topoType) {
                    $tq = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Toposolid'); include_types = $true; include_links = $false; max_rows = 500 }
                    $topoType = @(@($tq.data.rows) | Where-Object { $_ -and $_.is_element_type -and [string]$_.family -eq 'Toposolid' } | Sort-Object { [string]$_.type }) | Select-Object -First 1
                }
                if (-not $topoType) {
                    $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Toposolid'); include_types = $true; include_links = $false; max_rows = 500 }
                    $seen = @(@($q.data.rows) | Where-Object { $_ -and $_.is_element_type } | ForEach-Object { [string]$_.family + ': ' + [string]$_.type })
                    Case 6 'not_covered' ("no type of the Toposolid family in the document; types seen: " + ($seen -join '; '))
                    Case 7 'not_covered' 'no toposolid type by name (see the previous case)'
                }
                elseif (-not $topoLevel) { Case 6 'unverified' 'the toposolid probe level could not be created'; Case 7 'unverified' 'the toposolid probe level could not be created' }
                else {
                    $tx = $X + 20000
                    $pts = @(@($tx, 0, 1000), @(($tx + 10000), 0, 1500), @(($tx + 10000), 10000, 4000), @($tx, 10000, 2000), @(($tx + 5000), 5000, 3500), @(($tx + 2000), 3000, 1200))
                    $topo = & $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'toposolid'; level_id = $topoLevel; type_id = [long]$topoType.element_id; points = $pts }) } ($run + '-rt-topo')
                    $made = @(Rows $topo)
                    foreach ($m in $made) { $created.Add([long]$m.element_id) }
                    Case 6 $(if ($made.Count -eq 1) { 'pass' } else { 'fail' }) (Short $topo.answer)

                    # The same kind from a LandXML TIN beside it (X + 40 m). A first rehearsal of a
                    # file written as if shared = internal names the active project position; the
                    # file is rewritten through it so the surface lands at the probe's own X. The
                    # re-read proves the solid stands at the CONVERTED points, not the conversion:
                    # the detail records the position, and an identity one leaves the sign unexercised.
                    New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
                    $lx = Join-Path $Ctx.ScratchRoot ('HZ_RT_TIN_' + ($run -replace '[^A-Za-z0-9]', '') + '.xml')
                    $lxX = $X + 40000
                    $tin = @(@($lxX, 0, 1000), @(($lxX + 10000), 0, 1500), @(($lxX + 10000), 10000, 3000), @($lxX, 10000, 2000), @(($lxX + 5000), 5000, 3500))
                    Write-TinFile $lx $tin ([pscustomobject]@{ angle_degrees = 0; east_west_m = 0; north_south_m = 0; elevation_m = 0 })
                    $lxRow = @{ kind = 'toposolid'; level_id = $topoLevel; type_id = [long]$topoType.element_id; landxml_path = $lx }
                    $lxDry = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @($lxRow) }
                    $lxPlan = @($lxDry.data.plan | Where-Object { $_ -and $_.landxml }) | Select-Object -First 1
                    $lxPos = if ($lxPlan) { $lxPlan.landxml.project_position } else { $null }
                    if ($lxDry.isError -or -not $lxPos) { Case 7 'fail' ('the rehearsal named no project position: ' + (Short $lxDry)) }
                    else {
                        Write-TinFile $lx $tin $lxPos
                        $lxApp = & $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @($lxRow) } ($run + '-rt-topo-lx')
                        $lxMade = @(Rows $lxApp)
                        foreach ($m in $lxMade) { $created.Add([long]$m.element_id) }
                        $lxIdentity = [double]$lxPos.angle_degrees -eq 0 -and [double]$lxPos.east_west_m -eq 0 -and [double]$lxPos.north_south_m -eq 0 -and [double]$lxPos.elevation_m -eq 0
                        $lxText = 'surface {0}, {1} of 5 points used; position {2} deg, E/W {3} m, N/S {4} m, elevation {5} m{6}' -f $lxPlan.landxml.surface, $lxPlan.landxml.points_used,
                            $lxPos.angle_degrees, $lxPos.east_west_m, $lxPos.north_south_m, $lxPos.elevation_m, $(if ($lxIdentity) { ' (identity: the shared->internal sign is not exercised here)' } else { '' })
                        $ok = $lxMade.Count -eq 1 -and [int]$lxPlan.landxml.points_used -eq 5 -and [string]$lxPlan.landxml.surface -eq 'HZ_RT_EG'
                        Case 7 $(if ($ok) { 'pass' } else { 'fail' }) ($lxText + '; ' + (Short $lxApp.answer))
                    }
                }
            }
        }
        catch {
            $err = 'probe error: ' + [string]$_
            foreach ($i in @(4, 5, 6, 7, 8, 9, 10, 11, 13)) { $nm = $names[$i].Name; if (-not @($out | Where-Object { $_.Name -eq $nm }).Count) { Case $i 'unverified' $err } }
        }
        finally {
            if ($lx -and (Test-Path -LiteralPath $lx)) { Remove-Item -LiteralPath $lx -Force -ErrorAction SilentlyContinue }
            if ($created.Count -gt 0) {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created.ToArray()) } ($run + '-rt-cleanup')
                Case 8 $(if ($del.stage -eq 'apply' -and -not $del.answer.isError) { 'pass' } else { 'fail' }) ('{0} ids: {1}' -f $created.Count, (Short $del.answer))
            }
            else { Case 8 'not_covered' 'nothing was created' }
        }
        return $out.ToArray()
    }
}
