#Requires -Version 5.1
<#
  ACERO 3.0, stages 1b and 1c, LIVE: hold_ends, on a DISPOSABLE fixture that makes Revit move a bar.

  WHAT MAKES REVIT MOVE A BAR (measured, stage 1c, anonymized regression case and this fixture, read typed through
  horizun_query_structure mode=rebar include_constraints): Revit pins each handle of a new bar to the faces of
  its host AND of the elements joined to it. A handle declared INSIDE the cover zone of such a face - between
  the face and its cover plane - gets a ToCover constraint and is moved onto the cover plane; a handle declared
  anywhere else gets FixedDistanceToHostFace and stays. The stage-1b fixture put the ends 6 mm SHORT of the
  column cover (outside the zone), so Revit never moved them. This one puts the legs of a beam U-bar 6 mm
  INSIDE the cover zone of the face of the column the beam enters - a face the beam buries, so the steel is in
  the concrete and the plan does not refuse it, as on the anonymized regression case (BEAM-B into its round columns).

  Two equal frames - a 300 x 600 beam between two 450 x 600 columns flush with its top - frame A without
  hold_ends, frame B with it. The covers are READ from the hosts, never assumed.

    E1  without hold_ends: Revit moves the legs onto the column cover - the audit measures the bar off its
        plan by about the depth declared into the zone, the typed constraints name the columns' covers, AND
        (stage 1c) the apply itself refuses to call those two bars verified: geometry_matches_the_plan fails
        on them, naming hold_ends, while the control bar is verified. The apply reports success only for what
        it re-read as planned - a moved bar is the truth this fixture builds.
    E2  with hold_ends: every bar re-read after the commit within 1 mm of the plan at every vertex
    E3  with hold_ends: the audit agrees (nothing but accepted notes)
    E4  control, frame A: the same bar with its legs 6 mm short of the cover plane is NOT moved and no leg is
        pinned to a cover - the stage-1b fixture, and why it never reproduced the move
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

# How deep into the cover zone the legs are declared (and, for the control, how far short of it).
$Depth = 6.0
$Radius = 15.9 / 2

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-holdends' -Document $Document
function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function New-Hz($Label, $Elements) {
    @(@((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label $Label -Arguments @{ target_document = $Document; units = 'mm'; elements = $Elements }).Apply.Result.rows) |
        ForEach-Object { [long](Get-HzProp $_ 'element_id') })
}
function Get-HzCommonCover([long]$HostId) {
    $row = @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$HostId" -Arguments @{ mode = 'hosts'; element_ids = @(, $HostId) }).Result.rows) | Where-Object { $_ })[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -eq $c) { throw "host $HostId has no common cover; this fixture needs one to place its legs" }
    [double]$c
}
# Per bar on a host: rule id (provenance mark is not needed: one rule per host side), constraints read typed.
function Get-HzConstraints([long]$HostId, [string]$Label) {
    @(@((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label $Label -Arguments @{
        mode = 'rebar'; host_id = $HostId; include_constraints = $true; include_bar_positions = $false; max_rows = 20 }).Result.rows) | Where-Object { $_ })
}
# The legs of a U-bar are its first and last segments (edges 0 and 2): what Revit holds them with.
function Get-HzLegs($Row) {
    @(@(Get-HzPath $Row 'constraints', 'handles') | Where-Object { $_ -and [string]$_.handle_type -eq 'Edge' -and $null -ne $_.edge_number -and [int]$_.edge_number -in @(0, 2) })
}
function Format-HzLegs($Legs) {
    ($Legs | ForEach-Object { $c = $_.current; if ($c) { "edge$($_.edge_number)=$($c.type)->$($c.target_element_id)" } else { "edge$($_.edge_number)=none" } }) -join ','
}

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$colT = @(Get-HzTypes 'OST_StructuralColumns' | Where-Object { [string]$_.family -eq 'M_Concrete-Rectangular-Column' -and [string]$_.name -eq '450 x 600mm' })[0]
$beamT = @(Get-HzTypes 'OST_StructuralFraming' | Where-Object { [string]$_.family -eq 'M_Concrete-Rectangular Beam' -and [string]$_.name -eq '300 x 600mm' })[0]
$frames = @()
foreach ($k in 0, 1) {
    $X0 = 300000.0 + 12000 * $k
    # The columns end flush with the beam's top, as on the anonymized regression case: the beam buries the face it enters.
    $cols = New-Hz "cols-$k" @(@{ kind = 'structural_column'; type_id = [long]$colT.element_id; level_id = $lv; point = @($X0, 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 },
                              @{ kind = 'structural_column'; type_id = [long]$colT.element_id; level_id = $lv; point = @(($X0 + 6000), 0.0, 0.0); coordinate_mode = 'level_offset'; height = 3000.0 })
    $beam = (New-Hz "beam-$k" @(, @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @($X0, 0.0, 3000.0); end = @(($X0 + 6000), 0.0, 3000.0) }))[0]
    $frames += @{ k = $k; x0 = $X0; cols = $cols; beam = $beam }
}
$colCover = Get-HzCommonCover $frames[0].cols[0]
$beamCover = Get-HzCommonCover $frames[0].beam
$run.Fixture['frames'] = $frames
$run.Fixture['covers_mm'] = @{ column = $colCover; beam = $beamCover }
$run.Fixture['depth_into_cover_zone_mm'] = $Depth

function New-HzRule([hashtable]$F, [string]$Id, [double]$Inside, [double]$Y, [string[]]$Layers) {
    # U-bars in the beam's middle plane. The legs stand in the columns, beyond the face the beam enters
    # (x0 + 225 and x0 + 6000 - 225): $Inside > 0 puts the leg's surface that far INTO the cover zone of that
    # face, $Inside < 0 that far short of the cover plane.
    $X0 = $F.x0; $rr = @()
    $xa = $X0 + 225 - $colCover + $Inside - $Radius; $xb = $X0 + 6000 - 225 + $colCover - $Inside + $Radius
    foreach ($layer in @(@{ n = 'top'; z = 3000.0 - $beamCover - $Radius; leg = -200.0 }, @{ n = 'bot'; z = 2400.0 + $beamCover + $Radius; leg = 200.0 }) | Where-Object { $_.n -in $Layers }) {
        $rr += [ordered]@{ id = "$Id-$($layer.n)"; host = @{ element_ids = @(, $F.beam) }; bar_type = 'N16'; style = 'standard'; allow_new_shape = $true
            curve_mm = @(@($xa, $Y, ($layer.z + $layer.leg)), @($xa, $Y, $layer.z), @($xb, $Y, $layer.z), @($xb, $Y, ($layer.z + $layer.leg)))
            normal = @(0, 1, 0); layout = @{ rule = 'single' }; into = @($F.cols) }
    }
    $rr
}
function New-HzSet([string]$Id, $Rules, [bool]$Hold) {
    [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = $Id; version = '1' }
        bar_types = @(, @{ id = 'N16'; type_name = '16M' }); hold_ends = $Hold; reinforcement_rules = @($Rules) }
}
function Get-HzGeometryFindings($Audit) {
    @(@(Get-HzPath $Audit 'findings') | Where-Object { $_ -and [string]$_.code -in @('geometry_differs', 'length_differs') })
}
function Get-HzWorst($Findings, [string]$Prefix) {
    $v = @($Findings | Where-Object { [string]$_.code -eq 'geometry_differs' -and ([string]$_.rule_id).StartsWith($Prefix) } |
        ForEach-Object { [double](([string]$_.observed) -replace '[^0-9.]', '') })
    if ($v.Count) { ($v | Measure-Object -Maximum).Maximum } else { 0.0 }
}

Write-Host "`n== E1, E4: without hold_ends ==" -ForegroundColor Cyan
$rulesA = @(New-HzRule $frames[0] 'SNAP' $Depth 0.0 @('top', 'bot')) + @(New-HzRule $frames[0] 'CTRL' (-$Depth) 60.0 @('top'))
$setA = New-HzSet 'hold-a' $rulesA $false
$apA = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-a' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $setA; transaction_name = 'HZ hold A' }
# STAGE 1c: the apply judges each bar against its plan with the audit's own rule, so a set whose bars Revit moved
# COMMITS and is NOT verified - the reply is a failure carrying its verification rows in `detail`. "Applied" here
# means the transaction committed and every row was re-read; whether each row is verified is E1's question.
$resA = $(if ($apA.Apply) { $apA.Apply.Result } else { $null })
$vA = @(@(Get-HzPath $resA 'verification') + @(Get-HzPath $resA 'detail', 'verification') | Where-Object { $_ })
$okA = $vA.Count -eq 3
function Get-HzGeoCheck($Row) { Get-HzPath $Row 'checks', 'geometry_matches_the_plan' }
$vSnap = @($vA | Where-Object { ([string]$_.rule_id).StartsWith('SNAP') })
$vCtrl = @($vA | Where-Object { ([string]$_.rule_id).StartsWith('CTRL') })
$snapRefused = @($vSnap | Where-Object { $g = Get-HzGeoCheck $_; (Get-HzProp $_ 'verified') -eq $false -and $g -and (Get-HzProp $g 'verified') -eq $false -and
    [double](Get-HzProp $g 'worst_mm') -gt 1.0 -and ([string](Get-HzProp $g 'why')).Contains('hold_ends') })
$ctrlVerified = @($vCtrl | Where-Object { $g = Get-HzGeoCheck $_; (Get-HzProp $_ 'verified') -eq $true -and $g -and (Get-HzProp $g 'verified') -eq $true })
$auA = (Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-a' -Arguments @{ target_document = $Document; requirement_set = $setA }).Result
$geoA = Get-HzGeometryFindings $auA
$worstSnap = Get-HzWorst $geoA 'SNAP'
$worstCtrl = Get-HzWorst $geoA 'CTRL'
$barsA = Get-HzConstraints $frames[0].beam 'constraints-a'
# Provenance tells which bar is which: the snap U-bars stand at y = 0, the control at y = 60.
$snapRows = @($barsA | Where-Object { [Math]::Abs([double]$_.geometry.centreline_points_mm[0][1]) -lt 1 })
$ctrlRows = @($barsA | Where-Object { [Math]::Abs([double]$_.geometry.centreline_points_mm[0][1] - 60) -lt 1 })
$colIds = @($frames[0].cols | ForEach-Object { [long]$_ })
$snapLegs = @($snapRows | ForEach-Object { Get-HzLegs $_ })
$snapLegsOnColumnCover = @($snapLegs | Where-Object { $_.current -and [string]$_.current.type -eq 'ToCover' -and [long]$_.current.target_element_id -in $colIds })
$ctrlLegs = @($ctrlRows | ForEach-Object { Get-HzLegs $_ })
$ctrlLegsOnCover = @($ctrlLegs | Where-Object { $_.current -and [string]$_.current.type -eq 'ToCover' })
$snapMoved = $worstSnap -gt 1.0
Add-HzProbe -Run $run -Id 'E1' -Name 'without hold_ends: Revit moves legs declared inside a cover zone onto the cover, and the apply says so' `
    -Expected ("committed with 3 rows re-read; both U-bars measured more than 1 mm off their plan (declared {0} mm into the zone); their 4 legs pinned ToCover on the columns; the apply NOT verified on both U-bars (geometry_matches_the_plan false, naming hold_ends) and verified on the control" -f $Depth) `
    -Status $(if ($okA -and $snapMoved -and $snapRows.Count -eq 2 -and $snapLegsOnColumnCover.Count -eq 4 -and $snapRefused.Count -eq 2 -and $ctrlVerified.Count -eq 1 -and -not $apA.Apply.Ok) { 'passed' } elseif ($okA -and -not $snapMoved) { 'not_assessable' } else { 'failed' }) `
    -Observed ("rows={0} apply_ok={7} apply_refused_snap={8}/{9} ctrl_verified={10} [{11}] worst_geometry_mm={1} bars={2} legs_on_column_cover={3}/{4} [{5}] findings={6}" -f $vA.Count, $worstSnap, $snapRows.Count, $snapLegsOnColumnCover.Count, $snapLegs.Count,
        (($snapRows | ForEach-Object { Format-HzLegs (Get-HzLegs $_) }) -join ' | '),
        (($geoA | Where-Object { ([string]$_.rule_id).StartsWith('SNAP') } | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' ' + [string]$_.observed }) -join '; '),
        $(if ($apA.Apply) { $apA.Apply.Ok } else { $null }), $snapRefused.Count, $vSnap.Count, $ctrlVerified.Count,
        (($vA | ForEach-Object { $g = Get-HzGeoCheck $_; [string]$_.rule_id + ' verified=' + (Get-HzProp $_ 'verified') + ' geometry=' + (Get-HzProp $g 'verified') + ' worst=' + (Get-HzProp $g 'worst_mm') }) -join '; ')) `
    -Evidence @{ findings = $geoA; apply_geometry = @($vA | ForEach-Object { @{ rule = $_.rule_id; geometry_matches_the_plan = (Get-HzGeoCheck $_) } })
                 constraints = @($snapRows | ForEach-Object { @{ bar = $_.id; constraints = $_.constraints } }) }
Add-HzProbe -Run $run -Id 'E4' -Name 'control: legs short of the cover plane are not moved and not pinned to a cover' `
    -Expected 'the control bar within 1 mm of its plan; none of its legs ToCover' `
    -Observed ("bars={0} worst_geometry_mm={1} legs_on_cover={2}/{3} [{4}]" -f $ctrlRows.Count, $worstCtrl, $ctrlLegsOnCover.Count, $ctrlLegs.Count, (($ctrlRows | ForEach-Object { Format-HzLegs (Get-HzLegs $_) }) -join ' | ')) `
    -Ok ($okA -and $ctrlRows.Count -eq 1 -and $worstCtrl -le 1.0 -and $ctrlLegs.Count -eq 2 -and $ctrlLegsOnCover.Count -eq 0) `
    -Evidence @{ constraints = @($ctrlRows | ForEach-Object { @{ bar = $_.id; constraints = $_.constraints } }) }

Write-Host "`n== E2, E3: with hold_ends ==" -ForegroundColor Cyan
$setB = New-HzSet 'hold-b' @(New-HzRule $frames[1] 'SNAP' $Depth 0.0 @('top', 'bot')) $true
$apB = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply-b' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $setB; transaction_name = 'HZ hold B' }
$okB = $null -ne $apB.Apply -and $apB.Apply.Ok
$vB = @($(if ($apB.Apply) { @(Get-HzPath $apB.Apply.Result 'verification') | Where-Object { $_ } }))
$holds = @($vB | ForEach-Object { Get-HzPath $_ 'checks', 'hold_ends' } | Where-Object { $_ })
$bad = @($holds | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
$moved = @($holds | Where-Object { [double](Get-HzProp $_ 'before_mm') -gt 1.0 })
$barsB = Get-HzConstraints $frames[1].beam 'constraints-b'
Add-HzProbe -Run $run -Id 'E2' -Name 'with hold_ends: every bar re-read within 1 mm of the plan' `
    -Expected 'applied; both bars moved by Revit before the hold (before_mm > 1) and every hold_ends check verified, after_commit_mm <= 1' `
    -Observed ("applied={0} sets={1} held={2}/{3} moved_before={4} {5} legs_after=[{6}]" -f $okB, $vB.Count, ($holds.Count - $bad.Count), $holds.Count, $moved.Count,
        (($holds | ForEach-Object { 'before=' + (Get-HzProp $_ 'before_mm') + ' after=' + (Get-HzProp $_ 'after_commit_mm') + ' switched=' + (Get-HzProp $_ 'handles_switched_to_fixed') }) -join ' | '),
        (($barsB | ForEach-Object { Format-HzLegs (Get-HzLegs $_) }) -join ' | ')) `
    -Ok ($okB -and $holds.Count -eq 2 -and $bad.Count -eq 0 -and $moved.Count -eq 2) `
    -Evidence @{ holds = $holds; constraints_after = @($barsB | ForEach-Object { @{ bar = $_.id; constraints = $_.constraints } }) }
$auB = (Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit-b' -Arguments @{ target_document = $Document; requirement_set = $setB }).Result
$otherB = @(@(Get-HzPath $auB 'findings') | Where-Object { $_ -and ([string]$_.severity -ne 'info' -or [string]$_.code -notin @('hook_corner_opening', 'hook_end_extension', 'hook_corner_seat')) })
Add-HzProbe -Run $run -Id 'E3' -Name 'with hold_ends: the audit agrees' -Expected 'no finding but accepted notes' `
    -Observed ("verdict={0} other={1} {2}" -f (Get-HzPath $auB 'summary', 'verdict'), $otherB.Count, (($otherB | Select-Object -First 3 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' ' + [string]$_.observed }) -join '; ')) `
    -Ok ($otherB.Count -eq 0) -Evidence @{ findings = $otherB }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
