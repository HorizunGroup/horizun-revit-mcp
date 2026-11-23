# Live probes for the read-only analysis operations of horizun_query_structure mode=analytical
# and mode=loads. Everything the reads judge is staged on the module's own level at
# X = 1,130,000 mm (branch C's slot), far from the model: an own structural beam and column.
# Types come BY NAME from the document; a name the document lacks is not_covered with that
# reason, never "the first type". No typed tool creates an analytical member or
# a load (horizun_create_elements has neither kind), and Revit 2023+ builds no analytical
# member for a physical one by itself - so, as the bridge's own fallback rule says, a staging
# script does it through horizun_execute_python in this disposable document: an analytical
# member on the own beam's curve, associated to it, and a 10 kN downward point load hosted at
# its start, in an own load case of an own load nature (a document may carry no load case at
# all, and a load with none has no case, nature or category to read - MEASURED 2026-09-27 on
# HZ_WRITE). The script only STAGES; every case judges the TYPED read of what it made (the own
# load read back by id with its host, its own case and nature, and -10 kN). With Python off on this
# machine, or the script failing, the own-analytical and own-load cases are not_covered with
# the script's reason, and the whole-document reads fall back to whatever the document
# carries. An own element the analytical read does not account for fails. Analytical members
# and loads are deleted first, newest first, with everything else created.
# What was created is deleted at the end. The document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'rm-analysis'
    Catalog = @(
        @{ Name = 'analytical: own beam and column associated (row read with releases) or named without an analytical member'; Tool = 'horizun_query_structure' }
        @{ Name = 'analytical: node gaps measured against the caller tolerance, or named unmeasured'; Tool = 'horizun_query_structure' }
        @{ Name = 'loads: own point load read with case/nature/host'; Tool = 'horizun_query_structure' }
        @{ Name = 'loads: counts, kN units and per-row case and coverage for what the document carries'; Tool = 'horizun_query_structure' }
        @{ Name = 'rm-analysis probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'analytical: own beam and column associated (row read with releases) or named without an analytical member',
            'analytical: node gaps measured against the caller tolerance, or named unmeasured',
            'loads: own point load read with case/nature/host',
            'loads: counts, kN units and per-row case and coverage for what the document carries',
            'rm-analysis probes: everything created is deleted')
        # Not $QS reused as data: PowerShell names are case-insensitive.
        $StructTool = 'horizun_query_structure'; $DeleteTool = 'horizun_delete_verified'
        $tools = @($StructTool, $StructTool, $StructTool, $StructTool, $DeleteTool)
        $doc = $Ctx.Document; $run = $Ctx.RunId
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }

        # ---- staging (write tier only) --------------------------------------------------------
        $created = New-Object System.Collections.ArrayList
        $ownAnalytical = New-Object System.Collections.ArrayList
        $why = @{}
        function Why($keys) { $t = @($keys | Where-Object { $why.ContainsKey($_) } | ForEach-Object { $why[$_] }); if ($t.Count) { $t -join ' | ' } else { 'staging incomplete' } }
        $level = $null; $beam = $null; $column = $null
        $amId = $null; $loadId = $null; $caseName = $null; $natureName = $null
        if (-not $Ctx.WriteGate) {
            function Types($category) {
                $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
                if (-not $q.data) { return @() }
                return @($q.data.rows | Where-Object { $_.is_element_type })
            }
            function Named($rows, $cand) {
                $parts = @($cand -split ': ', 2)
                $fam = if ($parts.Count -eq 2) { $parts[0] } else { $null }
                return @($rows | Where-Object { [string]$_.type -eq $parts[-1] -and (-not $fam -or [string]$_.family -eq $fam) }) | Select-Object -First 1
            }
            # BY NAME: the document's own type of that name, or nothing (the reason is recorded).
            function Bring($category, $candidates, $key) {
                $have = @(Types $category)
                foreach ($cand in $candidates) { $hit = Named $have $cand; if ($hit) { return $hit } }
                $why[$key] = 'the document holds no ' + $category + ' type named ' + ($candidates -join ' or ')
                return $null
            }
            function Create($elements, $key) {
                $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-rma-' + $key)
                if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                    $row = @($r.answer.data.rows) | Select-Object -First 1
                    if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
                }
                $why[$key] = 'create ' + $key + ': ' + (Short $r.answer)
                return $null
            }
            $E = 93000.0; $X = 1130000.0; $Y = 0.0; $Z = $E + 2500.0
            $beamType = Bring 'OST_StructuralFraming' @('M_Concrete-Rectangular Beam: 300 x 600mm', 'M_W Shapes: W310X38.7') 'beamtype'
            $colType = Bring 'OST_StructuralColumns' @('M_Concrete-Rectangular-Column: 300 x 450mm', 'M_W Shapes-Column: W250X49.1') 'coltype'

            $level = Create @(@{ kind = 'level'; name = "HZ_RM_$run"; elevation = $E }) 'level'
            if ($level -and $beamType) {
                $beam = Create @(@{ kind = 'structural_framing'; start = @($X, ($Y + 8000), ($E + 3000)); end = @(($X + 6000), ($Y + 8000), ($E + 3000)); level_id = $level
                                    type_id = [long]$beamType.element_id; structural_type = 'Beam' }) 'beam'
            }
            if ($level -and $colType) {
                # A point with Z needs its mode named (MEASURED 2026-09-27: refused without one).
                $column = Create @(@{ kind = 'structural_column'; point = @(($X + 6000), ($Y + 8000), $E); coordinate_mode = 'absolute'; level_id = $level; type_id = [long]$colType.element_id }) 'column'
            }

            # Staging script (no typed kind exists): analytical member on the own beam, associated,
            # and a hosted point load at its start. It reports ids; the typed reads below judge.
            if ($beam) {
                $tag = ([string]$run).Substring(0, [math]::Min(8, ([string]$run).Length))
                $caseName = "HZ_LC_$tag"; $natureName = "HZ_NAT_$tag"
                $pyA = @"
from Autodesk.Revit.DB import ElementId, Transaction, XYZ, UnitUtils, UnitTypeId
from Autodesk.Revit.DB.Structure import (AnalyticalMember, AnalyticalToPhysicalAssociationManager, PointLoad,
    AnalyticalElementSelector, LoadNature, LoadCase, LoadCaseCategory)
beam = doc.GetElement(ElementId($beam))
out = {'am_id': None, 'load_id': None, 'case_id': None, 'nature_id': None, 'associated': None, 'error': None}
t = Transaction(doc, 'hz probe: analytical member and point load')
t.Start()
try:
    nature = LoadNature.Create(doc, '$natureName')
    case = LoadCase.Create(doc, '$caseName', nature.Id, LoadCaseCategory.Dead)
    am = AnalyticalMember.Create(doc, beam.Location.Curve)
    mgr = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc)
    mgr.AddAssociation(am.Id, beam.Id)
    force = XYZ(0, 0, -UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Kilonewtons))
    pl = PointLoad.Create(doc, am.Id, AnalyticalElementSelector.StartOrBase, force, XYZ.Zero, None)
    pl.LoadCaseId = case.Id
    t.Commit()
    out['nature_id'] = int(str(nature.Id)); out['case_id'] = int(str(case.Id))
    out['am_id'] = int(str(am.Id)); out['load_id'] = int(str(pl.Id))
    out['associated'] = str(mgr.GetAssociatedElementId(am.Id)) == str(beam.Id)
except Exception as e:
    if t.HasStarted() and not t.HasEnded(): t.RollBack()
    out['error'] = str(e)
__output__ = out
"@
                $pa = & $Ctx.Call 'horizun_execute_python' @{ code = $pyA; target_document = $doc; idempotency_key = ($run + '-rma-analytical') }
                $po = if ($pa.data -and $pa.data.output) { $pa.data.output } else { $null }
                # Nature and case first, so the reversed cleanup deletes load, member, case, nature.
                if ($po -and $po.nature_id) { [void]$created.Add([long]$po.nature_id) }
                if ($po -and $po.case_id) { [void]$created.Add([long]$po.case_id) }
                if ($po -and $po.am_id) { $amId = [long]$po.am_id; [void]$created.Add($amId) }
                if ($po -and $po.load_id) { $loadId = [long]$po.load_id; [void]$created.Add($loadId) }
                if (-not $loadId) {
                    $why['analytical'] = if ($pa.isError -and ([string]$pa.text -match 'DISABLED|tool_disabled|permission_profile=unsafe_code|OFF on a fresh install')) { 'python is disabled on this machine, so no analytical member or load could be staged (no typed tool creates one): ' + (Short $pa) }
                                         elseif ($pa.isError) { 'the staging script was refused: ' + (Short $pa) }
                                         else { 'the staging script made no load: ' + $(if ($po -and $po.error) { [string]$po.error } else { Short $pa }) }
                }
            }
            else { $why['analytical'] = 'no own beam to put an analytical member on: ' + (Why @('level', 'beamtype', 'beam')) }
        }

        # ==== 2: the whole document's analytical read with a caller tolerance (after staging, so
        # an analytical member Revit made for the own beam is on it) ===========================
        $whole = & $Ctx.Call $StructTool @{ mode = 'analytical'; target_document = $doc; tolerance_mm = 5; max_rows = 500 }
        if ($whole.isError -or -not $whole.data) { Case $catalog[1] $tools[1] 'fail' ('analytical read: ' + (Short $whole)) }
        else {
            $w = $whole.data; $problems = @()
            if ([string]$w.tolerance_source -ne 'caller' -or [math]::Abs([double]$w.tolerance_mm - 5) -gt 0.001) { $problems += "tolerance $($w.tolerance_mm) from '$($w.tolerance_source)', expected 5 from the caller" }
            # StructuralCoverage.Reason publishes { what, why, element_id? }: read the field, not the JSON text.
            $reasonWhat = if ($w.coverage) { @($w.coverage.reasons | ForEach-Object { [string]$_.what }) } else { @() }
            if ($w.node_gaps_measured -eq $true) { if ($null -eq $w.member_ends_beyond_tolerance -or $null -eq $w.member_ends_supported) { $problems += 'gaps measured but member_ends_beyond_tolerance or member_ends_supported is null' } }
            elseif ($w.node_gaps_measured -eq $false) {
                if ($null -ne $w.member_ends_beyond_tolerance) { $problems += 'gaps not measured but a count is given' }
                if ($reasonWhat -notcontains 'node_gaps') { $problems += 'gaps not measured and no coverage reason names node_gaps' }
            }
            else { $problems += "node_gaps_measured is '$($w.node_gaps_measured)'" }
            if (-not $w.coverage) { $problems += 'no coverage block' }
            if (-not $w.physical_without_analytical) { $problems += 'no physical_without_analytical block' }
            if ($problems.Count) { Case $catalog[1] $tools[1] 'fail' ($problems -join '; ') }
            elseif ([int]$w.matched -eq 0) { Case $catalog[1] $tools[1] 'not_covered' ("no analytical member or panel in the document (" + (Why @('analytical')) + "): the end classification ran on nothing; physical without analytical: $($w.physical_without_analytical.count) of $($w.physical_without_analytical.checked)") }
            else { Case $catalog[1] $tools[1] 'pass' ("$($w.matched) analytical element(s); gaps measured: $($w.node_gaps_measured), ends beyond 5 mm: $($w.member_ends_beyond_tolerance), supported: $($w.member_ends_supported); physical without analytical: $($w.physical_without_analytical.count) of $($w.physical_without_analytical.checked)") }
        }

        # ==== 1: the own beam and column are accounted for ======================================
        $own = @(@($beam, $column) | Where-Object { $_ })
        if ($Ctx.WriteGate) { Case $catalog[0] $tools[0] 'not_covered' 'the write tier is closed for this run: no own beam or column' }
        elseif ($own.Count -eq 0) { Case $catalog[0] $tools[0] 'not_covered' ('no own beam or column: ' + (Why @('level', 'beamtype', 'coltype', 'beam', 'column'))) }
        else {
            $ar = & $Ctx.Call $StructTool @{ mode = 'analytical'; target_document = $doc; element_ids = $own; tolerance_mm = 5 }
            if ($ar.isError -or -not $ar.data) { Case $catalog[0] $tools[0] 'fail' ('analytical read of the own members: ' + (Short $ar)) }
            else {
                $pwa = $ar.data.physical_without_analytical
                $without = @($pwa.ids | ForEach-Object { [long]$_ })
                $unmatched = @($ar.data.unmatched_ids | ForEach-Object { [long]$_ })
                $problems = @(); $said = @(); $rowsRead = 0
                foreach ($id in $own) {
                    if ($unmatched -contains $id) { $problems += "own $id is in unmatched_ids: the read did not see a structural element it staged"; continue }
                    if ($without -contains $id) { $said += "$id named without an analytical member"; continue }
                    if ([string]$pwa.coverage -ne 'complete' -or $null -eq $pwa.ids) { $problems += "own $id neither listed nor vouched for (physical_without_analytical coverage '$($pwa.coverage)')"; continue }
                    $rowsFor = if ($whole.data) { @($whole.data.rows | Where-Object { @($_.associated_physical_ids | ForEach-Object { [long]$_ }) -contains $id }) } else { @() }
                    if ($rowsFor.Count -eq 0) {
                        if ($whole.data -and [int]$whole.data.matched -le 500) { $problems += "own $id is vouched associated but no row of the whole analytical read ($($whole.data.matched) element(s), every one on the page) lists it"; continue }
                        $said += "$id associated (its analytical row is not on the first page of $($whole.data.matched))"; continue
                    }
                    $am = $rowsFor[0]
                    if ($am.kind -eq 'member' -and $null -eq $am.member.releases) { $problems += "own $id's analytical member $($am.id) carries no releases block"; continue }
                    if ($am.kind -eq 'member' -and $whole.data.node_gaps_measured -eq $true -and $null -eq $am.node_gaps) { $problems += "own $id's analytical member $($am.id) carries no node_gaps although gaps were measured"; continue }
                    if (-not $am.coverage) { $problems += "own $id's analytical row $($am.id) carries no coverage"; continue }
                    $said += "$id associated to analytical $($am.kind) $($am.id) (coverage $($am.coverage), releases read)"; $rowsRead++; [void]$ownAnalytical.Add([long]$am.id)
                }
                if ($problems.Count) { Case $catalog[0] $tools[0] 'fail' ($problems -join '; ') }
                elseif ($rowsRead -eq 0) { Case $catalog[0] $tools[0] 'not_covered' ('no own element has an analytical row this run could read (Revit 2023+ builds none by itself; ' + (Why @('analytical')) + '), so association, releases and end fields were not exercised: ' + ($said -join '; ')) }
                else { Case $catalog[0] $tools[0] 'pass' ($said -join '; ') }
            }
        }

        # ==== 3 and 4: loads ====================================================================
        if ($Ctx.WriteGate) { Case $catalog[2] $tools[2] 'not_covered' 'the write tier is closed for this run: no own load' }
        elseif (-not $loadId) { Case $catalog[2] $tools[2] 'not_covered' (Why @('analytical')) }
        else {
            # The own load read BACK by the typed read, by its id: never the script's word.
            $ol = & $Ctx.Call $StructTool @{ mode = 'loads'; target_document = $doc; element_ids = @($loadId); max_rows = 50 }
            $mine = if ($ol.data) { @($ol.data.rows | Where-Object { [long]$_.id -eq $loadId }) | Select-Object -First 1 } else { $null }
            $problems = @()
            if ($ol.isError -or -not $ol.data) { $problems += 'loads read by id: ' + (Short $ol) }
            elseif (-not $mine) { $problems += "own load $loadId is not among the $(@($ol.data.rows).Count) row(s) read for it (unmatched_ids: $(@($ol.data.unmatched_ids) -join ','))" }
            else {
                if ([string]$mine.kind -ne 'point') { $problems += "kind '$($mine.kind)', expected point" }
                if ([long]$mine.host_id -ne $amId) { $problems += "host_id $($mine.host_id), expected the own analytical member $amId" }
                if (-not $mine.load_case -or [string]$mine.load_case.name -ne $caseName -or $mine.load_case.assigned -ne $true) { $problems += "load case '$($mine.load_case.name)' (assigned $($mine.load_case.assigned)), expected the own '$caseName'" }
                if ([string]$mine.nature -ne $natureName) { $problems += "nature '$($mine.nature)', expected the own '$natureName'" }
                $fz = if ($mine.point -and $null -ne $mine.point.force_kn) { [double]@($mine.point.force_kn)[2] } else { $null }
                if ($null -eq $fz -or [math]::Abs($fz + 10.0) -gt 0.01) { $problems += "force z $fz kN, expected -10" }
                if (-not $mine.vector_frame) { $problems += 'no vector_frame for published force components' }
            }
            if ($problems.Count) { Case $catalog[2] $tools[2] 'fail' ($problems -join '; ') }
            else { Case $catalog[2] $tools[2] 'pass' ("own load $loadId on analytical $amId read back: case '$($mine.load_case.name)', nature '$($mine.nature)', force $(@($mine.point.force_kn) -join ',') kN, frame $($mine.vector_frame)") }
        }
        $ld = & $Ctx.Call $StructTool @{ mode = 'loads'; target_document = $doc; max_rows = 500 }
        if ($ld.isError -or -not $ld.data) { Case $catalog[3] $tools[3] 'fail' ('loads read: ' + (Short $ld)) }
        else {
            $l = $ld.data; $problems = @()
            $counts = $l.counts
            if ($null -eq $counts -or $null -eq $counts.point -or $null -eq $counts.line -or $null -eq $counts.area) { $problems += 'counts must name point, line and area' }
            elseif ([int]$l.matched -ne ([int]$counts.point + [int]$counts.line + [int]$counts.area)) { $problems += "matched $($l.matched) is not point+line+area" }
            if ([string]$l.units.point_force -ne 'kN' -or [string]$l.units.area_force -ne 'kN/m2') { $problems += "units are $($l.units | ConvertTo-Json -Compress)" }
            foreach ($r in @($l.rows)) {
                if (-not $r.load_case) { $problems += "load $($r.id) has no load_case block" }
                if (-not $r.coverage) { $problems += "load $($r.id) has no coverage" }
                elseif ([string]$r.coverage -ne 'complete' -and @($r.unread).Count -eq 0) { $problems += "load $($r.id) is '$($r.coverage)' with nothing named unread" }
            }
            if (-not $l.coverage) { $problems += 'no coverage block' }
            $converted = @(@($l.rows) | Where-Object { ($_.point -and $null -ne $_.point.force_kn) -or ($_.line -and $null -ne $_.line.force1_kn_m) -or ($_.area -and $null -ne $_.area.force1_kn_m2) })
            foreach ($r in $converted) { if (-not $r.vector_frame) { $problems += "load $($r.id) publishes force components with no vector_frame" } }
            if ($problems.Count) { Case $catalog[3] $tools[3] 'fail' ($problems -join '; ') }
            elseif ([int]$l.matched -eq 0) { Case $catalog[3] $tools[3] 'not_covered' ('the document carries no point, line or area load (' + (Why @('analytical')) + '): no load row was read and no kN conversion ran') }
            elseif ($converted.Count -eq 0) { Case $catalog[3] $tools[3] 'not_covered' "$($l.matched) load(s), but no row on the page published a converted force: the kN conversion was not exercised" }
            else { Case $catalog[3] $tools[3] 'pass' ("$($counts.point) point, $($counts.line) line, $($counts.area) area load(s) read in kN units; by case: $($l.by_load_case | ConvertTo-Json -Compress)") }
        }

        # ---- cleanup: members, level ----
        if ($Ctx.WriteGate) { Case $catalog[4] $tools[4] 'not_covered' 'the write tier is closed for this run: nothing was created' }
        else {
            # Analytical members read for the own ones go first (the list is reversed).
            $ids = @(@($created) + @($ownAnalytical) | Select-Object -Unique)
            if ($ids.Count -eq 0) { Case $catalog[4] $tools[4] 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-rma-cleanup')
                if (-not ($del.stage -eq 'apply' -and -not $del.answer.isError)) { Case $catalog[4] $tools[4] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
                else { Case $catalog[4] $tools[4] 'pass' ("deleted $($ids.Count) created ids") }
            }
        }
        return $cases
    }
}
