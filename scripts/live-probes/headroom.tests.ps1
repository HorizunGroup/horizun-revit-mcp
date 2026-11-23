#Requires -Version 5.1
# Exercises headroom.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# The fakes' reply shapes come from the code (CodeCheckHeadroom.cs element rows: outcome, min_clear_mm,
# coverage, measured, on_element, governing.surface.element_id; the view refusal text) - to be held
# against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'headroom.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'headroom' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function Reset {
    $script:nextId = 5000; $script:ids = @{}; $script:deleted = $null; $script:hasTypes = $true
    $script:firstProfile = $null; $script:alonePasses = $false; $script:upClear = 2850.4
    # the embedded pipe's z; 'far' makes the inside pipe read as measured.
    $script:pipeInZ = $null; $script:insideMeasured = $false
}
Reset
$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        if ($arguments.include_bounding_box) {
            # Floor C, 150 mm thick with its top on the upper level (E + H = 115000 mm).
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:ids.floorC; bounding_box = [pscustomobject]@{ min = @(1150000.0, 30000.0, 114850.0); max = @(1154000.0, 34000.0, 115000.0) } }) } }
        }
        $cat = $arguments.categories[0]
        $rows = if (-not $script:hasTypes) { @() }
                elseif ($cat -eq 'OST_Floors') { @([pscustomobject]@{ element_id = 31; family = 'Floor'; type = 'Generic 150mm'; is_element_type = $true }) }
                elseif ($cat -eq 'OST_PipeCurves') { @([pscustomobject]@{ element_id = 41; family = 'Pipe Types'; type = 'Default'; is_element_type = $true }) }
                elseif ($cat -eq 'OST_PipingSystem') { @([pscustomobject]@{ element_id = 42; family = 'Piping System'; type = 'Domestic Cold Water'; is_element_type = $true }) }
                else { @() }
        return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
    }
    if ($tool -eq 'horizun_code_check' -and $arguments.operation -eq 'headroom') {
        $h = $arguments.headroom
        if ([long]$h.view_id -ne 4000) { return @{ isError = $true; text = "headroom.view_id $($h.view_id) is a Level, not a 3D view: the rays need a View3D." } }
        $id = [long]@($h.element_ids)[0]
        $row = if ($id -eq $script:ids['pipe-hung']) {
            [pscustomobject]@{ element_id = $id; outcome = 'passes'; min_clear_mm = 2483.5; coverage = 1.0; measured = 3; on_element = 3; sampling = 'along_location_curve'
                               governing = [pscustomobject]@{ point_mm = @(1141000.0, 32000.0, 114483.5); surface = [pscustomobject]@{ kind = 'host'; element_id = $script:ids.floorA; category = 'Floors' } } }
        } elseif ($id -eq $script:ids['pipe-in']) {
            if ($script:insideMeasured) { [pscustomobject]@{ element_id = $id; outcome = 'passes'; min_clear_mm = 64.5; coverage = 1.0; measured = 3; on_element = 3; sampling = 'along_location_curve' } }
            else { [pscustomobject]@{ element_id = $id; outcome = 'not_measured'; min_clear_mm = $null; coverage = 0.0; measured = 0; on_element = 3; not_measured = 3; inside_target = 3; sampling = 'along_location_curve' } }
        } elseif ($id -eq $script:ids.floorB) {
            [pscustomobject]@{ element_id = $id; outcome = $(if (2850.0 -ge [double]$h.min_mm) { 'passes' } else { 'fails' }); min_clear_mm = 2850.0; coverage = 1.0; measured = 16; on_element = 16
                               governing = [pscustomobject]@{ point_mm = @(1140500.0, 30500.0, 114850.0); surface = [pscustomobject]@{ kind = 'host'; element_id = $script:ids.floorA; category = 'Floors' } } }
        } elseif ($id -eq $script:ids.floorA) {
            [pscustomobject]@{ element_id = $id; outcome = 'passes'; min_clear_mm = $script:upClear; coverage = 1.0; measured = 16; on_element = 16
                               governing = [pscustomobject]@{ point_mm = @(1140500.0, 30500.0, 112000.0); surface = [pscustomobject]@{ kind = 'host'; element_id = $script:ids.floorB; category = 'Floors' } } }
        } elseif ($script:alonePasses) {
            [pscustomobject]@{ element_id = $id; outcome = 'passes'; min_clear_mm = $null; coverage = $null; measured = 0; on_element = 16 }
        } else {
            [pscustomobject]@{ element_id = $id; outcome = 'not_measured'; min_clear_mm = $null; coverage = 0.0; measured = 0; on_element = 16; not_measured = 16
                               reason = 'the rays crossed the element and found no target surface below it (none there, or hidden in this view)' }
        }
        return @{ isError = $false; data = [pscustomobject]@{ operation = 'headroom'; direction = $h.direction; elements = @($row); skipped = @() } }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}
$fakeApply = {
    param($tool, $arguments, $key)
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            $el = $arguments.elements[0]
            $script:ids[($key -split '-hdr-')[-1]] = $script:nextId
            if ($el.kind -eq 'floor' -and $null -eq $script:firstProfile) { $script:firstProfile = $el.profile }
            if ($key -like '*-hdr-pipe-in') { $script:pipeInZ = [double]@($el.start)[2] }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) } } }
        }
        'horizun_manage_views' { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ aliases = [pscustomobject]@{ hdr = 4000 } } } } }
        'horizun_delete_verified' { $script:deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
    }
    return @{ stage = 'refused'; answer = @{ isError = $true; text = "unexpected apply $tool" } }
}

function Ctx($runId) { [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply } }
$names = @($module.Catalog | ForEach-Object { $_.Name })
function Outcome($set, $i) { (@($set | Where-Object { $_.Name -eq $names[$i] })[0]).Outcome }

$cases = @(& $module.Run (Ctx 't1'))
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
Check 'every catalog case is reported once' (($cases.Count -eq $names.Count) -and (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
Check 'a floor profile is one loop of four XYZ points' ((@($script:firstProfile).Count -eq 1) -and (@($script:firstProfile[0]).Count -eq 4) -and (@($script:firstProfile[0][0]).Count -eq 3))
Check 'down from B reaches A with full coverage and passes' ((Outcome $cases 0) -eq 'pass')
Check 'up from A matches down within 1 mm' ((Outcome $cases 1) -eq 'pass')
Check 'a threshold above the measured clear height fails the element' ((Outcome $cases 2) -eq 'pass')
Check 'C with nothing below is not_measured' ((Outcome $cases 3) -eq 'pass')
Check 'a level id as view_id is refused by name' ((Outcome $cases 4) -eq 'pass')
Check 'the hung pipe is sampled along its curve and measured down to A' ((Outcome $cases 6) -eq 'pass')
Check 'the pipe inside C is inside_target, not measured' ((Outcome $cases 7) -eq 'pass')
Check 'the inside pipe runs through the middle of C''s read-back thickness' ($script:pipeInZ -eq 114925.0)
Check 'cleanup deletes 2 pipes, view, 3 floors and 2 levels (8 ids, newest first), never the document''s types' (((Outcome $cases 5) -eq 'pass') -and ($script:deleted.Count -eq 8) -and ($script:deleted[0] -eq $script:ids['pipe-in']) -and ($script:deleted -notcontains 31) -and ($script:deleted -notcontains 41))

Reset; $script:upClear = 2855.0; $script:alonePasses = $true
$bad = @(& $module.Run (Ctx 't2'))
Check 'up and down 5 mm apart fail the symmetry case' ((Outcome $bad 1) -eq 'fail')
Check 'an element with nothing measured reported as passes FAILS the not_measured case' ((Outcome $bad 3) -eq 'fail')

Reset; $script:insideMeasured = $true
$far = @(& $module.Run (Ctx 't5'))
Check 'a pipe inside a floor read as a clear height to the floor''s far side FAILS the inside case' ((Outcome $far 7) -eq 'fail')

Reset; $script:hasTypes = $false
$nt = @(& $module.Run (Ctx 't3'))
$m0 = @($nt | Where-Object { $_.Name -eq $names[0] })[0]
Check 'no floor type: the measuring cases are unverified and name the missing type' (($m0.Outcome -eq 'unverified') -and ($m0.Detail -match 'no Generic floor type') -and ((Outcome $nt 3) -eq 'unverified'))
Check 'no floor type: the two levels and the view are still deleted (3 ids)' ($script:deleted.Count -eq 3)

$closedCtx = (Ctx 't4'); $closedCtx.WriteGate = $true
$shut = @(& $module.Run $closedCtx)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
if ($fails) { "headroom tests: $fails FAILED"; exit 1 } else { 'headroom tests: ALL PASS'; exit 0 }
