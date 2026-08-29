#Requires -Version 5.1
<#
  THE DIAGNOSTICS P0 SLICE, LIVE - with deliberate defects AND valid controls.

  A rule that refuses everything is not a rule. Half the probes here build a
  defect and require it to be found; the other half build the CORRECT version of
  the same thing and require it NOT to be found, because a check that fires on
  both is worse than no check at all.

  The three things this harness exists to prove, which no amount of offline
  testing can:

    THE FALSE POSITIVE. A survey point ten kilometres from the internal origin is
    CORRECT - it is what a survey point is for - and a tool that reads it and
    reports "geometry 10 km from origin" has misread the model. D1 puts real
    geometry far out and requires it found; D2 requires the control points
    reported and NOT counted as geometry.

    THE NEAR-MISS. Two levels a millimetre apart collide on neither name nor
    elevation, so nothing in Revit ever mentions them - and every element on the
    second is invisible to every schedule filtered on the first.

    THE ROTATED BUILDING. Every grid off the world axes and nothing wrong with
    it. The dominant angle is measured from the grids themselves rather than
    assumed to be zero - and the first run of this harness taught it a second
    lesson: this document ALREADY carried orthogonal grids, so adding a rotated
    building gives the model two genuine grid families and the rule reports the
    minority. Correct, and useless without a number saying the minority exists.

  Nothing is ever saved. Exit code 0 when everything passed.
#>
[CmdletBinding()]
param(
    [string]$Document = 'HZ_WRITE',
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'diagnostics' -Document $Document

# This harness's own lane, clear of the structural ones at 920k, 928k and 936k.
$X = 952000.0
$TAG = $run.RunId.Substring($run.RunId.Length - 6)

function Get-HzFinding {
    param($Result, [string]$Check)
    foreach ($f in @(Get-HzPath $Result 'findings')) {
        if ([string](Get-HzProp $f 'check') -eq $Check) { return $f }
    }
    return $null
}

function Invoke-HzAudit {
    param([hashtable]$Extra)
    $args = @{ target_document = $Document; top = 50 }
    if ($Extra) { foreach ($k in $Extra.Keys) { $args[$k] = $Extra[$k] } }
    Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Label ('audit-' + [guid]::NewGuid().ToString('N').Substring(0, 6)) `
        -Arguments $args
}

# =====================================================================  FIXTURE

Write-Host "`n== fixture: deliberate defects, and the controls that keep them honest ==" -ForegroundColor Cyan

$null = Reset-HzDocument $run

$level = Get-HzFirstLevel $run
if (-not $level) { throw 'HARNESS: the document has no level to build on.' }

# --- LEVELS. One clean pair, one NEAR-COINCIDENT pair a millimetre apart, and a
#     duplicate name. The near-miss is the point: nothing in Revit warns about it.
$levelPlan = @(
    @{ kind = 'level'; name = "HZD_$TAG" + '_A'; elevation = 40000.0 },   # clean, far from the others
    @{ kind = 'level'; name = "HZD_$TAG" + '_B'; elevation = 44000.0 },   # clean
    @{ kind = 'level'; name = "HZD_$TAG" + '_C'; elevation = 48000.0 },   # the near-miss pair, first
    @{ kind = 'level'; name = "HZD_$TAG" + '_C2'; elevation = 48001.0 }   # ...and second, 1 mm apart
)
$levelsMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-levels' -AllowRefusal -Arguments @{
    target_document = $Document; units = 'mm'; transaction_name = "HZD_$TAG levels"
    elements = $levelPlan
}
$levelIds = @()
if ($levelsMade.Ok -and $levelsMade.Apply) {
    foreach ($r in @(Get-HzPath $levelsMade.Apply.Result 'rows')) { $levelIds += [long](Get-HzProp $r 'element_id') }
}
Add-HzNote $run ("levels built: " + $levelIds.Count + " of " + $levelPlan.Count)

# --- GRIDS. A building rotated 30 degrees - every grid off the WORLD axes and
#     nothing wrong with it - plus ONE grid that disagrees with the building.
$deg = 30.0
$rad = $deg * [Math]::PI / 180.0
$cos = [Math]::Cos($rad); $sin = [Math]::Sin($rad)
$gy = 0.0
$gridPlan = @()
foreach ($i in 0..2) {
    $oy = $i * 6000.0
    $gridPlan += @{ kind = 'grid'; name = "HZD$TAG-A$i"
                    start = @($X, ($gy + $oy), 0.0)
                    end = @(($X + 20000.0 * $cos), ($gy + $oy + 20000.0 * $sin), 0.0) }
}
foreach ($i in 0..2) {
    $ox = $i * 6000.0
    $gridPlan += @{ kind = 'grid'; name = "HZD$TAG-N$i"
                    start = @(($X + $ox), $gy, 0.0)
                    end = @(($X + $ox - 20000.0 * $sin), ($gy + 20000.0 * $cos), 0.0) }
}
# THE ONE THAT DISAGREES. About 17 degrees off the building's own angle.
$gridPlan += @{ kind = 'grid'; name = "HZD$TAG-ODD"
                start = @(($X + 30000.0), $gy, 0.0); end = @(($X + 40000.0), ($gy + 3000.0), 0.0) }

$gridsMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-grids' -AllowRefusal -Arguments @{
    target_document = $Document; units = 'mm'; transaction_name = "HZD_$TAG grids"
    elements = $gridPlan
}
$gridIds = @()
if ($gridsMade.Ok -and $gridsMade.Apply) {
    foreach ($r in @(Get-HzPath $gridsMade.Apply.Result 'rows')) { $gridIds += [long](Get-HzProp $r 'element_id') }
}
Add-HzNote $run ("grids built: " + $gridIds.Count + " of " + $gridPlan.Count + " (6 on a 30-degree building, 1 odd)")

# --- THE OUTLIER. A wall five kilometres from the internal origin, plus a
#     control wall right beside the others.
$farX = 5000000.0
$wallsMade = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fixture-walls' -AllowRefusal -Arguments @{
    target_document = $Document; units = 'mm'; transaction_name = "HZD_$TAG walls"
    elements = @(
        @{ kind = 'wall'; level_id = [long]$level.element_id; structural = $true
           start = @($X, 0.0, 0.0); end = @(($X + 6000.0), 0.0, 0.0); height = 3000.0 },
        @{ kind = 'wall'; level_id = [long]$level.element_id; structural = $true
           start = @($farX, 0.0, 0.0); end = @(($farX + 6000.0), 0.0, 0.0); height = 3000.0 }
    )
}
$wallIds = @()
if ($wallsMade.Ok -and $wallsMade.Apply) {
    foreach ($r in @(Get-HzPath $wallsMade.Apply.Result 'rows')) { $wallIds += [long](Get-HzProp $r 'element_id') }
}
Add-HzNote $run ("walls built: " + $wallIds.Count + " (one at " + $X + " mm, one at " + $farX + " mm)")

if ($levelIds.Count -lt 4 -or $gridIds.Count -lt 7 -or $wallIds.Count -lt 2) {
    foreach ($id in @('D1', 'D2', 'D3', 'D4', 'D4b', 'D5', 'D6', 'D7', 'D8', 'D9', 'D10', 'D11', 'D12',
                      'D13', 'D14', 'D15')) {
        Add-HzProbe -Run $run -Id $id -Name 'diagnostics probe' -Status 'fixture_missing' `
            -Expected 'four levels, seven grids and two walls' `
            -Observed ("levels=$($levelIds.Count) grids=$($gridIds.Count) walls=$($wallIds.Count)") `
            -Evidence @{ levels = $levelsMade.Apply; grids = $gridsMade.Apply; walls = $wallsMade.Apply }
    }
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}

# ==================================================================  D: DATUMS

Write-Host "`n== D: datums - the near-miss nothing else reports ==" -ForegroundColor Cyan

$audit = Invoke-HzAudit @{ tolerances = @{ level_coincidence_mm = 2.0; grid_axis_tolerance_degrees = 0.5 } }
$datums = if ($audit.Ok) { Get-HzFinding $audit.Result 'datums' } else { $null }

Add-HzProbe -Run $run -Id 'D1' `
    -Name 'two levels a millimetre apart are found, although nothing in Revit mentions them' `
    -Expected 'coincident >= 1' `
    -Observed ("coincident=" + (Get-HzPath $datums 'levels', 'coincident')) `
    -Ok ([int](Get-HzPath $datums 'levels', 'coincident') -ge 1) `
    -Evidence @{ levels = (Get-HzProp $datums 'levels')
                 note = 'they collide on neither name nor elevation, so Revit never warns' }

Add-HzProbe -Run $run -Id 'D2' `
    -Name 'the near-miss is reported with BOTH names and the separation, not just a count' `
    -Expected "an item naming HZD_${TAG}_C and HZD_${TAG}_C2 about 1 mm apart" `
    -Observed (($(foreach ($i in @(Get-HzPath $datums 'items')) {
        if ([string](Get-HzProp $i 'code') -like '*coincident*') {
            "{0}|{1}|{2}" -f (Get-HzProp $i 'first_name'), (Get-HzProp $i 'second_name'), (Get-HzProp $i 'separation_mm')
        } }) -join ' ; ')) `
    -Ok ($(
        $hit = $false
        foreach ($i in @(Get-HzPath $datums 'items')) {
            if ([string](Get-HzProp $i 'first_name') -like "*_C" -and
                [string](Get-HzProp $i 'second_name') -like "*_C2" -and
                [double](Get-HzProp $i 'separation_mm') -le 2.0) { $hit = $true }
        }
        $hit)) `
    -Evidence @{ items = (Get-HzPath $datums 'items') }

# THE CONTROL. The clean levels 4 m apart must NOT be reported.
Add-HzProbe -Run $run -Id 'D3' `
    -Name 'levels four metres apart are NOT reported, so the rule is not refusing everything' `
    -Expected 'no coincidence naming the clean pair A and B' `
    -Observed ("coincident=" + (Get-HzPath $datums 'levels', 'coincident') + " over " +
               (Get-HzPath $datums 'levels', 'measured') + " level(s)") `
    -Ok ($(
        $bad = $false
        foreach ($i in @(Get-HzPath $datums 'items')) {
            $a = [string](Get-HzProp $i 'first_name'); $b = [string](Get-HzProp $i 'second_name')
            if (($a -like "*_A" -and $b -like "*_B") -or ($a -like "*_B" -and $b -like "*_A")) { $bad = $true }
        }
        -not $bad)) `
    -Evidence @{ note = 'a rule that fires on a correct model is worse than no rule' }

# MEASURED, NOT ASSUMED, AND THE FIRST RUN TAUGHT THIS PROBE ITS OWN LESSON.
#
# The first version asserted off_axis = 1: six grids on a 30-degree building plus
# one odd one. It measured 8, and it was RIGHT - this document already carried
# orthogonal grids, so the model genuinely has two grid families and the rule
# reported the minority. A correct number and a useless one, which is why the
# finding now publishes angle_families and on_dominant_axis: a reader seeing
# "off_axis: 8" cannot tell a rotated wing from a mistake without them.
#
# So the probe asserts the RULE against the model's own content rather than
# against a fixture assumption: everything that does not agree with the largest
# family is off axis, and nothing else is.
$gridsBlock = Get-HzProp $datums 'grids'
$measuredGrids = [int](Get-HzProp $gridsBlock 'measured')
$curved = [int](Get-HzProp $gridsBlock 'curved_not_evaluated')
$onDominant = [int](Get-HzProp $gridsBlock 'on_dominant_axis')
$offAxis = [int](Get-HzProp $gridsBlock 'off_axis')
$families = [int](Get-HzProp $gridsBlock 'angle_families')

Add-HzProbe -Run $run -Id 'D4' `
    -Name 'off-axis is exactly what disagrees with the largest grid family, and the model says how many families it has' `
    -Expected 'on_dominant_axis + off_axis = the straight grids measured, and angle_families >= 2 here' `
    -Observed ("measured=$measuredGrids curved=$curved on_dominant=$onDominant off_axis=$offAxis " +
               "families=$families dominant=" + (Get-HzProp $gridsBlock 'dominant_angle_degrees')) `
    -Ok (($onDominant + $offAxis) -eq ($measuredGrids - $curved) -and $families -ge 2) `
    -Evidence @{ grids = $gridsBlock
                 note = 'this document carries orthogonal grids AND a 30-degree building, so it genuinely has ' +
                        'two grid families. The rule reports the minority, which is defensible only because ' +
                        'angle_families says the minority exists.' }

Add-HzProbe -Run $run -Id 'D4b' `
    -Name 'the dominant angle is MEASURED from the grids rather than assumed to be zero' `
    -Expected 'a dominant angle agreed by the largest family, and a stated reason' `
    -Observed ("dominant=" + (Get-HzProp $gridsBlock 'dominant_angle_degrees') +
               " on_dominant=$onDominant of " + ($measuredGrids - $curved)) `
    -Ok ($onDominant -ge 1 -and $onDominant -ge ($measuredGrids - $curved - $onDominant) -and
         ([string](Get-HzProp $gridsBlock 'dominant_angle_means')) -match 'MEASURED') `
    -Evidence @{ note = 'a building rotated thirty degrees has every grid off the WORLD axes and nothing ' +
                        'wrong with it; assuming zero would report the whole site plan.' }

Add-HzProbe -Run $run -Id 'D5' `
    -Name 'the grid that is reported is the one that disagrees with the building' `
    -Expected "an off_axis item named HZD$TAG-ODD" `
    -Observed (($(foreach ($i in @(Get-HzPath $datums 'items')) {
        if ([string](Get-HzProp $i 'code') -eq 'off_axis') { [string](Get-HzProp $i 'first_name') } }) -join ',')) `
    -Ok ($(
        $hit = $false
        foreach ($i in @(Get-HzPath $datums 'items')) {
            if ([string](Get-HzProp $i 'code') -eq 'off_axis' -and
                [string](Get-HzProp $i 'first_name') -like '*ODD') { $hit = $true }
        }
        $hit)) `
    -Evidence @{ items = (Get-HzPath $datums 'items') }

# ============================================================  C: COORDINATES

Write-Host "`n== C: coordinates - geometry far out, control points near ==" -ForegroundColor Cyan

$coords = if ($audit.Ok) { Get-HzFinding $audit.Result 'coordinates' } else { $null }

Add-HzProbe -Run $run -Id 'D6' `
    -Name 'the three control points are reported TOGETHER, because confusing them is the whole problem' `
    -Expected 'internal_origin, project_base_point and survey_point all present' `
    -Observed (($(foreach ($n in @('internal_origin', 'project_base_point', 'survey_point')) {
        "{0}={1}" -f $n, (Get-HzPath $coords 'control_points', $n, 'readable') }) -join ' ')) `
    -Ok ($null -ne (Get-HzPath $coords 'control_points', 'internal_origin') -and
         $null -ne (Get-HzPath $coords 'control_points', 'project_base_point') -and
         $null -ne (Get-HzPath $coords 'control_points', 'survey_point')) `
    -Evidence @{ control_points = (Get-HzProp $coords 'control_points') }

Add-HzProbe -Run $run -Id 'D7' `
    -Name 'geometry five kilometres out is found at a 1 km radius' `
    -Expected 'count >= 1' `
    -Observed ("count=" + (Get-HzProp $coords 'count') +
               " farthest=" + (Get-HzPath $coords 'geometry_extent', 'farthest_element_mm')) `
    -Ok ([int](Get-HzProp $coords 'count') -ge 1) `
    -Evidence @{ geometry_extent = (Get-HzProp $coords 'geometry_extent')
                 items = (Get-HzPath $coords 'items') }

Add-HzProbe -Run $run -Id 'D8' `
    -Name 'the same geometry is NOT found at a 10 km radius, so the tolerance is the callers' `
    -Expected 'count = 0 with origin_distance_mm raised to 10 km' `
    -Observed ($(
        $wide = Invoke-HzAudit @{ tolerances = @{ origin_distance_mm = 10000000.0 } }
        $c2 = if ($wide.Ok) { Get-HzFinding $wide.Result 'coordinates' } else { $null }
        "count=" + (Get-HzProp $c2 'count'))) `
    -Ok ($(
        $wide2 = Invoke-HzAudit @{ tolerances = @{ origin_distance_mm = 10000000.0 } }
        $c3 = if ($wide2.Ok) { Get-HzFinding $wide2.Result 'coordinates' } else { $null }
        $null -ne $c3 -and [int](Get-HzProp $c3 'count') -eq 0)) `
    -Evidence @{ note = 'the radius is an argument, and the same model answers differently under a different one' }

Add-HzProbe -Run $run -Id 'D9' `
    -Name 'the distance explanation says it measured from the INTERNAL ORIGIN and not a control point' `
    -Expected 'count_means naming the internal origin and the survey-point false positive' `
    -Observed ([string](Get-HzProp $coords 'count_means')).Substring(0, [Math]::Min(90, ([string](Get-HzProp $coords 'count_means')).Length)) `
    -Ok (([string](Get-HzProp $coords 'count_means')) -match 'INTERNAL ORIGIN' -and
         ([string](Get-HzProp $coords 'count_means')) -match 'survey point') `
    -Evidence @{ count_means = (Get-HzProp $coords 'count_means') }

# ==============================================================  R: READINESS

Write-Host "`n== R: 4D/5D readiness - evidence found, never absence assumed ==" -ForegroundColor Cyan

$noRoles = Invoke-HzAudit @{}
$readinessBare = if ($noRoles.Ok) { Get-HzFinding $noRoles.Result 'readiness' } else { $null }

Add-HzProbe -Run $run -Id 'D10' `
    -Name 'with NO roles declared, readiness is not_assessable rather than "not ready"' `
    -Expected 'every dimension not_assessable, and a stated reason' `
    -Observed ("4d=" + (Get-HzPath $readinessBare 'dimensions', '4d', 'state') +
               " 5d=" + (Get-HzPath $readinessBare 'dimensions', '5d', 'state')) `
    -Ok ([string](Get-HzPath $readinessBare 'dimensions', '4d', 'state') -eq 'not_assessable' -and
         [string](Get-HzPath $readinessBare 'dimensions', '5d', 'state') -eq 'not_assessable') `
    -Evidence @{ not_assessed_because = (Get-HzProp $readinessBare 'not_assessed_because')
                 note = 'no parameter name is compiled in, so with nothing declared there is nothing to look ' +
                        'for, and answering "not ready" would be inventing a standard' }

$withRoles = Invoke-HzAudit @{
    readiness_roles = @(
        @{ id = 'task'; dimension = '4d'; parameter_names = @("HZD_NO_SUCH_PARAM_$TAG") },
        @{ id = 'cost'; dimension = '5d'; parameter_names = @('Comments') }
    )
}
$readiness = if ($withRoles.Ok) { Get-HzFinding $withRoles.Result 'readiness' } else { $null }

Add-HzProbe -Run $run -Id 'D11' `
    -Name 'a parameter that does not exist reports readiness_absent, naming what was looked for' `
    -Expected '4d absent' `
    -Observed ("4d=" + (Get-HzPath $readiness 'dimensions', '4d', 'state')) `
    -Ok ([string](Get-HzPath $readiness 'dimensions', '4d', 'state') -eq 'readiness_absent') `
    -Evidence @{ dimensions = (Get-HzProp $readiness 'dimensions'); items = (Get-HzPath $readiness 'items') }

Add-HzProbe -Run $run -Id 'D12' `
    -Name 'a parameter that EXISTS and is blank is worded differently from one that does not exist' `
    -Expected 'the 5d role reports the parameter EXISTS, which is a different state from absent' `
    -Observed ($(
        $why = ''
        foreach ($i in @(Get-HzPath $readiness 'items')) {
            if ([string](Get-HzProp $i 'role') -eq 'cost') { $why = [string](Get-HzProp $i 'why') }
        }
        $why.Substring(0, [Math]::Min(110, $why.Length)))) `
    -Ok ($(
        $ok = $false
        foreach ($i in @(Get-HzPath $readiness 'items')) {
            if ([string](Get-HzProp $i 'role') -eq 'cost') {
                $w = [string](Get-HzProp $i 'why')
                # Either it exists and is blank, or it carries values. Both are
                # distinguishable from "no parameter matching any declared name".
                if ($w -notmatch 'no parameter matching any declared name') { $ok = $true }
            }
        }
        $ok)) `
    -Evidence @{ items = (Get-HzPath $readiness 'items')
                 note = 'a model set up and not filled in is a day away from ready; one not set up is a ' +
                        'decision away. Collapsing them destroys the only thing worth knowing.' }

# ====================================================================  G: GATE

Write-Host "`n== G: the gate over the new findings ==" -ForegroundColor Cyan

$gated = Invoke-HzAudit @{
    requirement_set = @{ max_coincident_levels = 0; max_grids_off_axis = 0; max_elements_far_from_origin = 0 }
    tolerances = @{ level_coincidence_mm = 2.0 }
}
$gate = if ($gated.Ok) { Get-HzPath $gated.Result 'gate' } else { $null }

Add-HzProbe -Run $run -Id 'D13' `
    -Name 'three requirements reading three different PARTS of two findings each get their own row' `
    -Expected 'three rows, all fail, verdict fail' `
    -Observed ("rows=" + (@(Get-HzPath $gate 'rows')).Count + " verdict=" + (Get-HzProp $gate 'verdict')) `
    -Ok ((@(Get-HzPath $gate 'rows')).Count -eq 3 -and [string](Get-HzProp $gate 'verdict') -eq 'fail') `
    -Evidence @{ gate = $gate
                 note = 'before E1 the gate could read ONE count per finding, so two requirements about ' +
                        'datums could not both be expressed' }

$refused = Invoke-HzAudit @{ requirement_set = @{ level_coincidence_mm = 1.0 } }
Add-HzProbe -Run $run -Id 'D14' `
    -Name 'a TOLERANCE passed as a requirement is refused and told where it belongs' `
    -Expected 'refused, naming the tolerances object' `
    -Observed (Limit-HzText $refused.Text 140) `
    -Ok ((-not $refused.Ok) -and ($refused.Text -match 'TOLERANCE') -and ($refused.Text -match 'tolerances')) `
    -Evidence @{ note = 'a tolerance configures a check rather than asserting on it, so it cannot pass or fail' }

$noTarget = Invoke-HzTool -Run $run -Tool 'horizun_audit_model' -Label 'audit-no-target' -Arguments @{ top = 5 }
Add-HzProbe -Run $run -Id 'D15' `
    -Name 'an audit with no target_document is refused, naming the document it would have audited' `
    -Expected 'refused, naming the active document' `
    -Observed (Limit-HzText $noTarget.Text 140) `
    -Ok ((-not $noTarget.Ok) -and ($noTarget.Text -match 'target_document is required')) `
    -Evidence @{ note = 'an audit is a claim about a NAMED model, and a report naming the wrong one is worse ' +
                        'than no report' }

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
