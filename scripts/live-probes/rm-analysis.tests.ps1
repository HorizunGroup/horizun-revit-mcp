#Requires -Version 5.1
# Exercises rm-analysis.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# The reply shapes are FROM THE CODE (QueryStructureAnalytical.cs, QueryStructureCommand.Ok,
# StructuralCoverage.Declare/Reason),
# to be held against the first live run - none of them is measured yet.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'rm-analysis.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'rm-analysis' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }
function Obj($h) { [pscustomobject]$h }
# StructuralCoverage.Declare: { coverage, measured, not_measured, is_whole_truth, reasons[] },
# each reason StructuralCoverage.Reason: { what, why, element_id? }.
function Cov($word, [object[]]$reasons = @()) {
    $whole = $word -eq 'complete'
    Obj @{ coverage = $word; measured = $(if ($whole) { 1 } else { 0 }); not_measured = $(if ($whole) { 0 } else { 1 }); is_whole_truth = $whole; reasons = $reasons }
}

function TypeRow($id, $family, $type) { Obj @{ element_id = $id; is_element_type = $true; family = $family; type = $type } }
function New-State {
    $script:nextId = 5000; $script:byKind = @{}; $script:deleted = @(); $script:sent = @{}
    $script:typesInDoc = @{
        'OST_StructuralFraming' = @((TypeRow 205 'M_Concrete-Rectangular Beam' '300 x 600mm'))
        'OST_StructuralColumns' = @((TypeRow 206 'M_Concrete-Rectangular-Column' '300 x 450mm'))
    }
    $script:pwa = { Obj @{ checked = 2; count = 2; ids = @($script:byKind['structural_framing'], $script:byKind['structural_column']); coverage = 'complete' } }
    $script:wholeRows = { @(Obj @{ id = 9001; kind = 'member'; associated_physical_ids = @(); association = 'none'; coverage = 'complete'; node_gaps = @(); member = (Obj @{ releases = (Obj @{}) }) }) }
    $script:loads = { @(Obj @{ id = 900; kind = 'point'; load_case = (Obj @{ id = 50; name = 'DL1'; number = 1 }); nature = 'Dead'; host_id = $null; vector_frame = 'project'; point = (Obj @{ position_mm = @(0, 0, 0); force_kn = @(0, 0, -10) }); unread = @(); coverage = 'complete' }) }
    $script:unmatched = @()
    $script:gaps = { @{ node_gaps_measured = $true; member_ends_beyond_tolerance = 0; member_ends_supported = 0; coverage = (Cov 'complete') } }
    # The staging script: 'off' (disabled on the machine), 'ok' (analytical 14200 + load 14201), 'noload'.
    $script:py = 'off'; $script:pyCode = $null
    $script:ownLoad = { Obj @{ id = 14201; kind = 'point'; load_case = (Obj @{ id = 14199; name = 'HZ_LC_' + $script:runTag; number = 1; assigned = $true }); nature = 'HZ_NAT_' + $script:runTag; host_id = 14200; vector_frame = 'project'
                               point = (Obj @{ position_mm = @(0, 0, 0); force_kn = @(0, 0, -10) }); unread = @(); coverage = 'complete' } }
}

$fakeCall = {
    param($tool, $arguments)
    switch ($tool) {
        'horizun_query_model' {
            if ($arguments.categories) {
                $rows = $script:typesInDoc[$arguments.categories[0]]
                return Reply (Obj @{ rows = @($rows) }) $false ''
            }
            return Reply $null $true 'unexpected query_model call'
        }
        'horizun_execute_python' {
            $script:pyCode = [string]$arguments.code
            if (-not $arguments.target_document) { return Reply $null $true "'target_document' is required for horizun_execute_python. Nothing ran." }
            switch ($script:py) {
                'off' { return Reply (Obj @{ code = 'tool_disabled' }) $true 'horizun_execute_python is DISABLED ON THIS MACHINE' }
                'noload' { return Reply (Obj @{ output = (Obj @{ am_id = 14200; load_id = $null; associated = $true; error = 'type could not be set for newly created point load' }) }) $false '' }
                default { return Reply (Obj @{ output = (Obj @{ nature_id = 14198; case_id = 14199; am_id = 14200; load_id = 14201; associated = $true; error = $null }) }) $false '' }
            }
        }
        'horizun_query_structure' {
            if ($arguments.mode -eq 'loads' -and $arguments.element_ids) {
                $want = @($arguments.element_ids | ForEach-Object { [long]$_ })
                $rows = @(@(& $script:ownLoad) | Where-Object { $_ -and $want -contains [long]$_.id })
                return Reply (Obj @{ mode = 'loads'; matched = $rows.Count; returned = $rows.Count; rows = $rows; counts = (Obj @{ point = $rows.Count; line = 0; area = 0 })
                                     units = (Obj @{ point_force = 'kN'; point_moment = 'kN*m'; line_force = 'kN/m'; area_force = 'kN/m2' }); coverage = (Cov 'complete') }) $false ''
            }
            if ($arguments.mode -eq 'loads') {
                $rows = @(& $script:loads)
                return Reply (Obj @{ mode = 'loads'; matched = $rows.Count; returned = $rows.Count; rows = $rows; counts = (Obj @{ point = $rows.Count; line = 0; area = 0 }); by_load_case = (Obj @{ DL1 = $rows.Count })
                                     units = (Obj @{ point_force = 'kN'; point_moment = 'kN*m'; line_force = 'kN/m'; area_force = 'kN/m2' }); coverage = (Cov 'complete') }) $false ''
            }
            if ($arguments.element_ids) {
                return Reply (Obj @{ mode = 'analytical'; matched = 0; rows = @(); physical_without_analytical = (& $script:pwa); unmatched_ids = $script:unmatched
                                     tolerance_mm = 5; tolerance_source = 'caller'; node_gaps_measured = $true; member_ends_beyond_tolerance = 0; coverage = (Cov 'complete') }) $false ''
            }
            $g = & $script:gaps
            $rows = @(& $script:wholeRows)
            return Reply (Obj @{ mode = 'analytical'; matched = $rows.Count; rows = $rows; tolerance_mm = $arguments.tolerance_mm; tolerance_source = 'caller'
                                 node_gaps_measured = $g.node_gaps_measured; member_ends_beyond_tolerance = $g.member_ends_beyond_tolerance; member_ends_supported = $g.member_ends_supported; coverage = $g.coverage
                                 physical_without_analytical = (Obj @{ checked = 0; count = 0; ids = @(); coverage = 'complete' }) }) $false ''
        }
    }
    return Reply $null $true "unexpected call $tool"
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++; $script:byKind[$arguments.elements[0].kind] = $script:nextId
            return @{ stage = 'apply'; answer = (Reply (Obj @{ rows = @(Obj @{ element_id = $script:nextId }) }) $false '') }
        }
        'horizun_delete_verified' { $script:deleted += , @($arguments.ids); return @{ stage = 'apply'; answer = (Reply (Obj @{}) $false '') } }
    }
    return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") }
}

function RunWith($id, [bool]$gate = $false) {
    $script:runTag = $id.Substring(0, [math]::Min(8, $id.Length))
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $id; WriteGate = $gate; Call = $fakeCall; Apply = $fakeApply }
    $by = @{}; foreach ($c in @(& $module.Run $ctx)) { if ($by.ContainsKey($c.Name)) { $by[$c.Name + '#dup'] = $c } else { $by[$c.Name] = $c } }
    return $by
}
$n = @($module.Catalog | ForEach-Object { $_.Name })

try {
    # ---- the plain run: members named without analytical ----
    New-State
    $by = RunWith 't1'
    Check 'every catalogued case is reported exactly once' (($by.Count -eq $n.Count) -and (@($n | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    Check 'own beam and column named without an analytical member are not_covered, saying so' (($by[$n[0]].Outcome -eq 'not_covered') -and ($by[$n[0]].Detail -match 'named without'))
    Check 'node gaps measured with the caller tolerance pass' ($by[$n[1]].Outcome -eq 'pass')
    Check 'python off: the own point load is not_covered, naming python and the missing typed kind' (($by[$n[2]].Outcome -eq 'not_covered') -and ($by[$n[2]].Detail -match 'python is disabled') -and ($by[$n[2]].Detail -match 'no typed tool creates one'))
    Check 'the loads read passes on counts, kN and per-row coverage' (($by[$n[3]].Outcome -eq 'pass') -and ($by[$n[3]].Detail -match '1 point'))
    Check 'cleanup deletes the 3 created ids in reverse' (($by[$n[4]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 1) -and (@($script:deleted[0]).Count -eq 3) -and (@($script:deleted[0])[0] -eq 5003))
    Check 'the column names its coordinate mode (a Z point without one is refused)' ($script:sent['t1-rma-column'].elements[0].coordinate_mode -eq 'absolute')

    # ---- python on: the staging script makes an own analytical member and load; the TYPED read judges ----
    New-State
    $script:py = 'ok'
    $bp = RunWith 'tp'
    Check 'the staging script names the write document and the own beam' (($script:pyCode -match 'ElementId\(5002\)') -and ($script:pyCode -match 'AnalyticalMember.Create') -and ($script:pyCode -match 'AddAssociation'))
    Check 'python on: the own load read back by host with case, nature and -10 kN passes' (($bp[$n[2]].Outcome -eq 'pass') -and ($bp[$n[2]].Detail -match 'own load 14201 on analytical 14200'))
    Check 'python on: load, member, case and nature are deleted first, in that order' ((@($script:deleted[0])[0] -eq 14201) -and (@($script:deleted[0])[1] -eq 14200) -and (@($script:deleted[0])[2] -eq 14199) -and (@($script:deleted[0])[3] -eq 14198))
    Check 'the staging script creates its own nature and case and assigns the case to the load' (($script:pyCode -match "LoadNature.Create\(doc, 'HZ_NAT_tp'\)") -and ($script:pyCode -match "LoadCase.Create\(doc, 'HZ_LC_tp'") -and ($script:pyCode -match 'pl.LoadCaseId = case.Id'))
    New-State
    $script:py = 'ok'
    $script:ownLoad = { Obj @{ id = 14201; kind = 'point'; load_case = (Obj @{ id = 51; name = 'LC1'; number = 1 }); nature = 'Dead'; host_id = 14200; vector_frame = 'project'
                               point = (Obj @{ position_mm = @(0, 0, 0); force_kn = @(0, 0, -9) }); unread = @(); coverage = 'complete' } }
    $bw = RunWith 'tw'
    Check 'a load read back with the wrong force fails, naming it' (($bw[$n[2]].Outcome -eq 'fail') -and ($bw[$n[2]].Detail -match 'force z -9 kN, expected -10'))
    New-State
    $script:py = 'ok'
    $script:ownLoad = { Obj @{ id = 14201; kind = 'point'; load_case = (Obj @{ id = $null; name = $null; number = $null; assigned = $false }); nature = $null; host_id = 14200; vector_frame = 'project'
                               point = (Obj @{ position_mm = @(0, 0, 0); force_kn = @(0, 0, -10) }); unread = @(); coverage = 'complete' } }
    $bc = RunWith 'tc'
    Check 'a load read back with no case, when the script assigned its own, fails naming the case' (($bc[$n[2]].Outcome -eq 'fail') -and ($bc[$n[2]].Detail -match "expected the own 'HZ_LC_tc'"))
    New-State
    $script:py = 'ok'
    $script:ownLoad = { @() }
    $bm = RunWith 'tm'
    Check 'a load the script reported but the typed read cannot find fails' (($bm[$n[2]].Outcome -eq 'fail') -and ($bm[$n[2]].Detail -match 'own load 14201 is not among'))
    New-State
    $script:py = 'noload'
    $bn = RunWith 'tn'
    Check 'a script that made no load is not_covered with its own error, and its member is still deleted' (($bn[$n[2]].Outcome -eq 'not_covered') -and ($bn[$n[2]].Detail -match 'type could not be set') -and (@($script:deleted[0])[0] -eq 14200))

    # ---- an own member the read does not account for is a fail ----
    New-State
    $script:pwa = { Obj @{ checked = 1; count = 0; ids = @(); coverage = 'complete' } }
    $script:unmatched = @(5002)
    $b5 = RunWith 't5'
    Check 'an own beam in unmatched_ids fails' (($b5[$n[0]].Outcome -eq 'fail') -and ($b5[$n[0]].Detail -match 'unmatched'))

    # ---- associated: the analytical row is read with its releases ----
    New-State
    $script:pwa = { Obj @{ checked = 2; count = 0; ids = @(); coverage = 'complete' } }
    $script:wholeRows = { @((Obj @{ id = 9101; kind = 'member'; associated_physical_ids = @(5002); association = 'associated'; coverage = 'complete'
                                    member = (Obj @{ releases = (Obj @{ start = (Obj @{ type = 'Fixed' }) }) }) }),
                            (Obj @{ id = 9102; kind = 'member'; associated_physical_ids = @(5003); association = 'associated'; coverage = 'complete'; member = (Obj @{ releases = $null }) })) }
    $b6 = RunWith 't6'
    Check 'an associated member without a releases block fails, naming it' (($b6[$n[0]].Outcome -eq 'fail') -and ($b6[$n[0]].Detail -match 'releases'))
    New-State
    $script:pwa = { Obj @{ checked = 2; count = 0; ids = @(); coverage = 'complete' } }
    $script:wholeRows = { @(5002, 5003 | ForEach-Object { Obj @{ id = 9100 + $_; kind = 'member'; associated_physical_ids = @($_); coverage = 'complete'; node_gaps = @(); member = (Obj @{ releases = (Obj @{}) }) } }) }
    $b7 = RunWith 't7'
    Check 'associated beam and column pass with their analytical ids' (($b7[$n[0]].Outcome -eq 'pass') -and ($b7[$n[0]].Detail -match 'associated to analytical member 14102'))
    Check 'the analytical members read for the own ones are deleted first' ((@($script:deleted[0]) -contains 14102) -and (@($script:deleted[0]) -contains 14103) -and (@($script:deleted[0])[0] -eq 14103))

    # ---- node gaps unmeasured without a reason is a fail; with a reason, a pass ----
    New-State
    $script:gaps = { @{ node_gaps_measured = $false; member_ends_beyond_tolerance = $null; coverage = (Cov 'partial') } }
    $b8 = RunWith 't8'
    Check 'gaps unmeasured with no reason naming node_gaps fail' (($b8[$n[1]].Outcome -eq 'fail') -and ($b8[$n[1]].Detail -match 'node_gaps'))
    New-State
    $script:gaps = { @{ node_gaps_measured = $false; member_ends_beyond_tolerance = $null
        coverage = (Cov 'partial' @(Obj @{ what = 'node_gaps'; why = 'ends x segments exceeds 50000000 checks; narrow with element_ids. Node gaps were NOT measured, which is not the same as none.' })) } }
    $b9 = RunWith 't9'
    Check 'gaps unmeasured and named pass' ($b9[$n[1]].Outcome -eq 'pass')
    New-State
    $script:gaps = { @{ node_gaps_measured = $false; member_ends_beyond_tolerance = $null
        coverage = (Cov 'partial' @(Obj @{ what = 'supports'; why = '1 boundary condition(s) would not give their geometry - node_gaps may be off' })) } }
    $b18 = RunWith 't18'
    Check 'a reason that says node_gaps only in its why text does not name the unmeasured check' (($b18[$n[1]].Outcome -eq 'fail') -and ($b18[$n[1]].Detail -match 'no coverage reason names node_gaps'))

    # ---- a type absent from the document: the own beam is not staged, never another type ----
    New-State
    $script:typesInDoc['OST_StructuralFraming'] = @((TypeRow 299 'M_Concrete-Rectangular Beam' 'Some Other'))
    $b10 = RunWith 't10'
    Check 'a beam type the document lacks by name is not staged, and the case names why' ((-not $script:sent.ContainsKey('t10-rma-beam')) -and ($b10[$n[0]].Detail -notmatch 'beam 5'))

    # ---- vouched associated but absent from a whole read that fit on one page: a fail ----
    New-State
    $script:pwa = { Obj @{ checked = 2; count = 0; ids = @(); coverage = 'complete' } }
    $b13 = RunWith 't13'
    Check 'an own member vouched associated that no row of a one-page read lists fails' (($b13[$n[0]].Outcome -eq 'fail') -and ($b13[$n[0]].Detail -match 'no row of the whole analytical read'))

    # ---- nothing to read is not_covered, never a pass ----
    New-State
    $script:wholeRows = { @() }
    $script:loads = { @() }
    $b14 = RunWith 't14'
    Check 'no analytical element makes the end classification not_covered' (($b14[$n[1]].Outcome -eq 'not_covered') -and ($b14[$n[1]].Detail -match 'no analytical member'))
    Check 'no load makes the loads read not_covered' (($b14[$n[3]].Outcome -eq 'not_covered') -and ($b14[$n[3]].Detail -match 'no point, line or area load'))
    New-State
    $script:loads = { @(Obj @{ id = 901; kind = 'line'; load_case = (Obj @{ id = 50; name = 'DL1'; number = 1 }); unread = @(); coverage = 'complete'; line = (Obj @{ force1_kn_m = $null }) }) }
    $b15 = RunWith 't15'
    Check 'loads with no converted force on the page are not_covered' (($b15[$n[3]].Outcome -eq 'not_covered') -and ($b15[$n[3]].Detail -match 'kN conversion was not exercised'))

    # ---- closed write tier: staging cases not_covered, the document reads still run ----
    New-State
    $b12 = RunWith 't12' $true
    $staged = @(0, 2, 4 | ForEach-Object { $b12[$n[$_]].Outcome })
    Check 'a closed write tier reports the staged cases not_covered and still reads gaps and loads' ((@($staged | Where-Object { $_ -ne 'not_covered' }).Count -eq 0) -and ($b12[$n[1]].Outcome -eq 'pass') -and ($b12[$n[3]].Outcome -eq 'pass') -and ($script:sent.Count -eq 0))
}
finally { }

if ($fails) { "rm-analysis tests: $fails FAILED"; exit 1 } else { 'rm-analysis tests: ALL PASS'; exit 0 }
