#Requires -Version 5.1
<#
  MEP FROM A DRAWING, LIVE.

  An MEP drawing is single lines. Everything that makes a run a run - which
  system it belongs to, how big it is, what it connects to - is carried by the
  LAYER and by nothing else in the geometry, so the requirement set is where all
  of it is said. That makes MEP the sharpest test of the whole idea: nothing can
  be inferred from the shape, and a bridge that guessed would produce a model
  that looks right in plan and is wrong in every calculation downstream.

      P  PIPE       - a system Revit will not create the run without, and a
                      bore a drawn line cannot carry
      D  DUCT       - the same, on a mechanical system
      C  CONDUIT    - no system at all: Revit does not put conduit on one, and
                      claiming otherwise would be inventing a fact
      T  CABLE TRAY - the same again, and the one kind with a usable default
      R  REFUSALS   - a system that is not in the document, and a bore on a run
                      that cannot carry one

  Exit code 0 when everything passed; non-zero otherwise.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'dwg-mep' -Document $Document
$X = 900000.0

function Get-HzKindCount {
    param($Plan, [string]$Kind)
    $c = Get-HzPath $Plan 'counts_by_kind', $Kind
    if ($null -eq $c) { 0 } else { [int]$c }
}

function Invoke-HzConversion {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)]$Conversion, [Parameter(Mandatory)][string]$Tag)
    $applyArgs = @{
        target_document = $Run.Document; instance_id = $Conversion.InstanceId
        requirement_set = $Conversion.Set
        apply_binding = $Conversion.Plan.apply_binding
        actions = $Conversion.Plan.execute_plan_request.actions
        candidate_index = $Conversion.Plan.candidate_index
    }
    $dry = Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag-dry" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $true })
    $tokens = Get-HzPath $dry.Result 'rehearsal', 'tokens_by_key'
    $acts = @($Conversion.Plan.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $acts) {
        $t = Get-HzProp $tokens $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    (Invoke-HzToolStrict -Run $Run -Tool 'horizun_apply_cad_plan' -Label "apply-$Tag" `
        -Arguments (Copy-HzArgs $applyArgs @{ dry_run = $false; actions = $acts
            idempotency_key = (New-HzKey $Run "apply-$Tag") })).Result
}

<#
  The bore the commit re-read, in mm, or $null when the run declared none.
#>
function Get-HzBoreRead {
    param($Applied)
    foreach ($stage in @(Get-HzProp $Applied 'stages')) {
        foreach ($row in @(Get-HzProp $stage 'rows')) {
            $d = Get-HzProp $row 'diameter_verified'
            if ($d) { return $d }
        }
    }
    $null
}

# =============================================================================
# THE FIXTURE - four services, each on the layer Revit gives its own discipline
#
# The runs are BUILT in Revit and exported, and at Coarse detail - which is what
# a new floor plan uses - Revit draws a pipe and a duct as a single centreline.
# That is the classic MEP single-line drawing, and it is what this reads back.
#
# Nothing about the export tells the reading what a layer MEANS. The requirement
# sets below say it, one service at a time, and the drawing is the same drawing
# for all of them.
# =============================================================================
Write-Host "`n== the MEP fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run

function Get-HzTypes {
    param([string[]]$Categories, [string]$Label)
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label $Label -Arguments @{
        categories = $Categories; include_types = $true; include_links = $false; max_rows = 60
    }).Result.rows | Where-Object { $_.is_element_type -eq $true })
}
$pipeTypes = Get-HzTypes -Categories @('OST_PipeCurves') -Label 'types-pipe'
$ductTypes = Get-HzTypes -Categories @('OST_DuctCurves') -Label 'types-duct'
$conduitTypes = Get-HzTypes -Categories @('OST_Conduit') -Label 'types-conduit'
$trayTypes = Get-HzTypes -Categories @('OST_CableTray') -Label 'types-tray'
$pipeSystems = Get-HzTypes -Categories @('OST_PipingSystem') -Label 'types-pipesys'
$ductSystems = Get-HzTypes -Categories @('OST_DuctSystem') -Label 'types-ductsys'
Add-HzNote $run ("content: pipe={0} duct={1} conduit={2} tray={3} pipe_systems={4} duct_systems={5}" -f
    $pipeTypes.Count, $ductTypes.Count, $conduitTypes.Count, $trayTypes.Count,
    $pipeSystems.Count, $ductSystems.Count)

# ONLY THE TWO REVIT WILL DRAW.
#
# MEASURED: a floor plan of this document exports the pipe and the duct and
# NOTHING for the conduit or the cable tray - not with the view set to
# Coordination either. Revit will not put them in this drawing, and chasing
# that is fixture authoring rather than anything about reading a drawing.
#
# It changes nothing that matters. An electrical drawing arrives as lines from
# somebody else, and what makes a line a conduit is the requirement set saying
# so - the same mechanism the beam probes rest on. The conduit and tray rules
# below read the pipe layer and say that is what they are doing.
$runs = [ordered]@{
    pipe = @{ y = 0.0;    categories = @('OST_PipeCurves') }
    duct = @{ y = 6000.0; categories = @('OST_DuctCurves') }
}
foreach ($key in @($runs.Keys)) { $runs[$key]['at'] = @(($X + 3000.0), $runs[$key].y) }

$elements = @()
if ($pipeTypes.Count -gt 0 -and $pipeSystems.Count -gt 0) {
    $elements += @{ kind = 'pipe'; type_id = [long]$pipeTypes[0].element_id
                    system_type_id = [long]$pipeSystems[0].element_id
                    level_id = [long]$level.element_id
                    start = @($X, $runs['pipe'].y, 0.0); end = @(($X + 6000.0), $runs['pipe'].y, 0.0) }
}
if ($ductTypes.Count -gt 0 -and $ductSystems.Count -gt 0) {
    $elements += @{ kind = 'duct'; type_id = [long]$ductTypes[0].element_id
                    system_type_id = [long]$ductSystems[0].element_id
                    level_id = [long]$level.element_id
                    start = @($X, $runs['duct'].y, 0.0); end = @(($X + 6000.0), $runs['duct'].y, 0.0) }
}
if ($elements.Count -lt 2) {
    throw ("HARNESS: this document carries only {0} of the two drawable services; the fixture cannot be built" -f
        $elements.Count)
}

$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'fx-runs' -Arguments @{
    target_document = $Document; units = 'mm'; elements = $elements }
if ([int]$made.Apply.Result.created_verified -ne $elements.Count) {
    throw ("HARNESS: the fixture wanted {0} runs and Revit verified {1}" -f
        $elements.Count, $made.Apply.Result.created_verified)
}

$viewName = "HZ_MEP_$($run.RunId)"
$view = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-view' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'create_floor_plan'; key = 'v'; level_id = [long]$level.element_id
                   name = $viewName }) }
$viewId = [long](@($view.Apply.Result.rows)[0].element_id)

# COORDINATION, not Architectural.
#
# MEASURED: an architectural floor plan drew the pipe and the duct and NOTHING
# for the conduit or the cable tray. Revit's view discipline hides the other
# disciplines' categories, so an architectural view of an electrical run is an
# empty view - which is correct of Revit and useless as a fixture. The
# discipline is a view parameter, so it is set through the typed write that
# verifies what it wrote.
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'fx-discipline' -Arguments @{
    target_document = $Document
    writes = @(@{ target_id = $viewId; parameter = 'VIEW_DISCIPLINE'; value = 4095 }) }   # Coordination

$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_views' -Label 'fx-crop' -Arguments @{
    target_document = $Document; units = 'mm'
    actions = @(@{ operation = 'set_crop'; view_id = $viewId
                   box = @(($X - 2000.0), -3000.0, ($X + 8000.0), 9000.0) }) }
$dwgPath = Join-Path 'C:\hz-live\dwg' ("HZ_MEP_$($run.RunId).dwg")
$null = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'fx-export' -Arguments @{
    target_document = $Document; format = 'dwg'; view_ids = @($viewId); output_path = $dwgPath }
$file = @(Get-ChildItem -LiteralPath 'C:\hz-live\dwg' -Filter "HZ_MEP_$($run.RunId)*.dwg")[0]
if ($null -eq $file) { throw 'HARNESS: the MEP fixture exported no DWG' }
$run.Fixture['dwg_name'] = $file.Name
$run.Fixture['dwg_sha256'] = (Get-HzSha256 $file.FullName)
Add-HzNote $run ("MEP fixture {0}" -f $file.Name)

$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$inst = Add-HzCadLink -Run $run -DwgPath $file.FullName -Label 'link-mep'
$facts = Get-HzCadInstanceFacts -Run $run -InstanceId $inst
$units = [string]$facts.declared_units

$layers = @{}
foreach ($key in @($runs.Keys)) {
    $others = @()
    foreach ($other in @($runs.Keys)) { if ($other -ne $key) { $others += , $runs[$other].at } }
    $layers[$key] = Get-HzExclusiveLayerNear -Run $run -InstanceId $inst -Point $runs[$key].at `
        -OtherPoints $others -RadiusMm 1500 -Label "layer-$key"
}
Add-HzNote $run ("layers: " + ((@($runs.Keys) | ForEach-Object { "$_='$($layers[$_])'" }) -join ' '))
foreach ($key in @($runs.Keys)) {
    if (-not $layers[$key]) { throw ("HARNESS: the fixture drew no {0} on a layer of its own" -f $key) }
}

# HOW MANY LINES EACH SERVICE DREW, measured rather than assumed. Revit draws a
# pipe as one centreline at Coarse and as two walls of the run at Fine, and a
# cable tray as its two sides at every detail level. The counts below are what
# the probes expect, and they are read from this drawing rather than guessed.
$minRunMm = 1000.0
$drawnLines = @{}
foreach ($key in @($runs.Keys)) {
    $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_cad' -Label "count-$key" -Arguments @{
        mode = 'geometry'; instance_id = $inst; layer = $layers[$key]; max_rows = 500 }
    $drawnLines[$key] = @(@($q.Result.segments) |
        Where-Object { [double]$_.length_mm -ge $minRunMm }).Count
}
Add-HzNote $run ("runs long enough per layer (>= $minRunMm mm): " +
    ((@($runs.Keys) | ForEach-Object { "$_=$($drawnLines[$_])" }) -join ' '))

function New-HzRunSet {
    param([string]$Id, [string]$Layer, [string]$Produces, [string]$Category,
          [string]$FamilyType, [string]$SystemType, [double]$DiameterMm = 0.0)
    $rule = @{ id = $Id; precedence = 10; discipline = 'mep'
               layers = @($Layer); produces = $Produces; category = $Category
               geometry = @{ from = 'single_lines'; min_length_mm = 1000.0 } }
    if ($FamilyType) { $rule['family_type'] = $FamilyType }
    if ($SystemType) { $rule['system_type'] = $SystemType }
    if ($DiameterMm -gt 0) { $rule['diameter_mm'] = $DiameterMm }
    @{
        schema = 'horizun.cad-requirements/1'
        requirement_set = @{ id = "hz-live-$Id"; version = '1.0.0'; title = "Live $Id" }
        source = @{ units = $units }
        tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
        rules = @($rule)
    }
}

# =============================================================================
# P - PIPE
# =============================================================================
Write-Host "`n== P: pipe ==" -ForegroundColor Cyan

if ($pipeTypes.Count -eq 0 -or $pipeSystems.Count -eq 0) {
    foreach ($id in @('P1', 'P2', 'P3')) {
        Add-HzProbe -Run $run -Id $id -Name 'a pipe needs a pipe type and a piping system in the document' `
            -Expected 'at least one PipeType and one PipingSystemType' `
            -Observed ("types={0} systems={1}" -f $pipeTypes.Count, $pipeSystems.Count) `
            -Status 'fixture_missing'
    }
} else {
    $pipeType = [string]$pipeTypes[0].name
    $pipeSystem = [string]$pipeSystems[0].name
    $setP = New-HzRunSet -Id 'pipe' -Layer $layers['pipe'] -Produces 'pipe' -Category 'OST_PipeCurves' `
        -FamilyType $pipeType -SystemType $pipeSystem -DiameterMm 100.0
    $planP = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-pipe' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $setP
        level_id = [long]$level.element_id }
    $pActions = @($planP.Result.execute_plan_request.actions)
    $pRow = $null
    if ($pActions.Count -gt 0) { $pRow = @($pActions[0].arguments.elements)[0] }
    $namedSystem = @(@($planP.Result.apply_binding.resolved_names) |
                     Where-Object { [string]$_.what -eq 'system_type' })

    Add-HzProbe -Run $run -Id 'P1' -Name 'the plan resolves the SYSTEM by name and records which one it chose' `
        -Expected 'every drawn line planned as a pipe, carrying system_type_id and diameter, and a system_type in resolved_names' `
        -Observed ("pipes={0} system_type_id={1} diameter={2} named={3}" -f
            (Get-HzKindCount $planP.Result 'pipe'), [string](Get-HzProp $pRow 'system_type_id'),
            [string](Get-HzProp $pRow 'diameter'), $namedSystem.Count) `
        -Ok ((Get-HzKindCount $planP.Result 'pipe') -eq $drawnLines['pipe'] -and
             $null -ne (Get-HzProp $pRow 'system_type_id') -and
             $null -ne (Get-HzProp $pRow 'diameter') -and $namedSystem.Count -eq 1) `
        -Evidence @{ row = $pRow; resolved = $namedSystem }

    $before = Get-HzElementCount -Run $run -Categories @('OST_PipeCurves') -Label 'pipes-before'
    $appliedP = Invoke-HzConversion -Run $run -Tag 'pipe' -Conversion ([pscustomobject]@{
        InstanceId = $inst; Plan = $planP.Result; Set = $setP })
    $after = Get-HzElementCount -Run $run -Categories @('OST_PipeCurves') -Label 'pipes-after'
    $bore = Get-HzBoreRead $appliedP

    Add-HzProbe -Run $run -Id 'P2' -Name 'the pipe is built on that system and verified by re-reading' `
        -Expected '1 created, delta 1' `
        -Observed ("created={0} delta={1} state={2}" -f $appliedP.created_verified, ($after - $before),
            $appliedP.state) `
        -Ok ([int]$appliedP.created_verified -eq $drawnLines['pipe'] -and
             ($after - $before) -eq $drawnLines['pipe']) `
        -Evidence @{ state = $appliedP.state; provenance = $appliedP.provenance }

    Add-HzProbe -Run $run -Id 'P3' -Name 'and its BORE is the one the rule declared, re-read off the commit' `
        -Expected '100 mm, read back from Revit rather than assumed from the type' `
        -Observed ("requested={0} read={1} verified={2}" -f
            $(if ($bore) { Get-HzProp $bore 'requested_mm' } else { '-' }),
            $(if ($bore) { Get-HzProp $bore 'read_mm' } else { '-' }),
            $(if ($bore) { Get-HzProp $bore 'verified' } else { '-' })) `
        -Ok ($null -ne $bore -and (Get-HzProp $bore 'verified') -eq $true) `
        -Evidence @{ diameter_verified = $bore
                     note = 'a drawn line has no width; a run left at the type default is a different main' }
}

# =============================================================================
# D - DUCT
# =============================================================================
Write-Host "`n== D: duct ==" -ForegroundColor Cyan

if ($ductTypes.Count -eq 0 -or $ductSystems.Count -eq 0) {
    foreach ($id in @('D1', 'D2')) {
        Add-HzProbe -Run $run -Id $id -Name 'a duct needs a duct type and a mechanical system in the document' `
            -Expected 'at least one DuctType and one MechanicalSystemType' `
            -Observed ("types={0} systems={1}" -f $ductTypes.Count, $ductSystems.Count) `
            -Status 'fixture_missing'
    }
} else {
    # A ROUND duct type, or the declared diameter is a request the run cannot
    # answer - and the refusal for that is R2, deliberately, not this.
    $round = @($ductTypes | Where-Object { [string]$_.name -match '(?i)round' })
    $ductType = [string]$(if ($round.Count -gt 0) { $round[0].name } else { $ductTypes[0].name })
    $setD = New-HzRunSet -Id 'duct' -Layer $layers['duct'] -Produces 'duct' -Category 'OST_DuctCurves' `
        -FamilyType $ductType -SystemType ([string]$ductSystems[0].name) `
        -DiameterMm $(if ($round.Count -gt 0) { 300.0 } else { 0.0 })
    $planD = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-duct' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $setD
        level_id = [long]$level.element_id }
    $dActions = @($planD.Result.execute_plan_request.actions)
    $dRow = $null
    if ($dActions.Count -gt 0) { $dRow = @($dActions[0].arguments.elements)[0] }

    Add-HzProbe -Run $run -Id 'D1' -Name 'a duct is planned on a mechanical system, from a single line' `
        -Expected ("{0} duct(s) - one per drawn run long enough - each carrying system_type_id" -f $drawnLines['duct']) `
        -Observed ("ducts={0} system_type_id={1} type_id={2}" -f
            (Get-HzKindCount $planD.Result 'duct'), [string](Get-HzProp $dRow 'system_type_id'),
            [string](Get-HzProp $dRow 'type_id')) `
        -Ok ((Get-HzKindCount $planD.Result 'duct') -eq $drawnLines['duct'] -and
             $null -ne (Get-HzProp $dRow 'system_type_id')) `
        -Evidence @{ row = $dRow; duct_type = $ductType }

    $before = Get-HzElementCount -Run $run -Categories @('OST_DuctCurves') -Label 'ducts-before'
    $appliedD = Invoke-HzConversion -Run $run -Tag 'duct' -Conversion ([pscustomobject]@{
        InstanceId = $inst; Plan = $planD.Result; Set = $setD })
    $after = Get-HzElementCount -Run $run -Categories @('OST_DuctCurves') -Label 'ducts-after'

    Add-HzProbe -Run $run -Id 'D2' -Name 'and it is built and verified by re-reading' `
        -Expected ("{0} created, and the model gains exactly that many" -f $drawnLines['duct']) `
        -Observed ("created={0} before={1} after={2} delta={3}" -f $appliedD.created_verified,
            $before, $after, ($after - $before)) `
        -Ok ([int]$appliedD.created_verified -eq $drawnLines['duct'] -and
             ($after - $before) -eq $drawnLines['duct']) `
        -Evidence @{ state = $appliedD.state; bore = (Get-HzBoreRead $appliedD) }
}

# =============================================================================
# C - CONDUIT, and T - CABLE TRAY
#
# Neither goes on an MEP system: Revit does not put one there, and a rule that
# named a system for conduit would be asking for a fact that does not exist.
# =============================================================================
Write-Host "`n== C and T: conduit and cable tray ==" -ForegroundColor Cyan

if ($conduitTypes.Count -eq 0) {
    Add-HzProbe -Run $run -Id 'C1' -Name 'conduit built from a single line, at a declared bore' `
        -Expected 'at least one ConduitType in the document' `
        -Observed 'this document has no conduit type' -Status 'fixture_missing'
} else {
    $setC = New-HzRunSet -Id 'conduit' -Layer $layers['pipe'] -Produces 'conduit' -Category 'OST_Conduit' `
        -FamilyType ([string]$conduitTypes[0].name) -DiameterMm 50.0
    $planC = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-conduit' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $setC
        level_id = [long]$level.element_id }
    $before = Get-HzElementCount -Run $run -Categories @('OST_Conduit') -Label 'cond-before'
    $appliedC = Invoke-HzConversion -Run $run -Tag 'conduit' -Conversion ([pscustomobject]@{
        InstanceId = $inst; Plan = $planC.Result; Set = $setC })
    $after = Get-HzElementCount -Run $run -Categories @('OST_Conduit') -Label 'cond-after'
    $boreC = Get-HzBoreRead $appliedC

    Add-HzProbe -Run $run -Id 'C1' -Name 'conduit is built from a single line at the declared bore, with NO system' `
        -Expected '1 created, delta 1, 50 mm re-read' `
        -Observed ("created={0} delta={1} bore_verified={2}" -f $appliedC.created_verified,
            ($after - $before), $(if ($boreC) { Get-HzProp $boreC 'verified' } else { '-' })) `
        -Ok ([int]$appliedC.created_verified -eq $drawnLines['pipe'] -and
             ($after - $before) -eq $drawnLines['pipe'] -and
             $null -ne $boreC -and (Get-HzProp $boreC 'verified') -eq $true) `
        -Evidence @{ diameter_verified = $boreC }
}

if ($trayTypes.Count -eq 0) {
    Add-HzProbe -Run $run -Id 'T1' -Name 'cable tray built from a single line' `
        -Expected 'at least one CableTrayType in the document' `
        -Observed 'this document has no cable tray type' -Status 'fixture_missing'
} else {
    $setT = New-HzRunSet -Id 'tray' -Layer $layers['pipe'] -Produces 'cable_tray' -Category 'OST_CableTray' `
        -FamilyType ([string]$trayTypes[0].name)
    $planT = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-tray' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $setT
        level_id = [long]$level.element_id }
    $before = Get-HzElementCount -Run $run -Categories @('OST_CableTray') -Label 'tray-before'
    $appliedT = Invoke-HzConversion -Run $run -Tag 'tray' -Conversion ([pscustomobject]@{
        InstanceId = $inst; Plan = $planT.Result; Set = $setT })
    $after = Get-HzElementCount -Run $run -Categories @('OST_CableTray') -Label 'tray-after'

    Add-HzProbe -Run $run -Id 'T1' -Name 'cable tray is built from a single line and verified' `
        -Expected '1 created, delta 1' `
        -Observed ("created={0} delta={1}" -f $appliedT.created_verified, ($after - $before)) `
        -Ok ([int]$appliedT.created_verified -eq $drawnLines['pipe'] -and
             ($after - $before) -eq $drawnLines['pipe']) `
        -Evidence @{ state = $appliedT.state }
}

# =============================================================================
# R - THE REFUSALS
# =============================================================================
Write-Host "`n== R: refusals ==" -ForegroundColor Cyan

$ghostSystem = New-HzRunSet -Id 'ghost' -Layer $layers['pipe'] -Produces 'pipe' -Category 'OST_PipeCurves' `
    -FamilyType $(if ($pipeTypes.Count -gt 0) { [string]$pipeTypes[0].name } else { 'HZ_NO_PIPE_TYPE' }) `
    -SystemType 'HZ_NO_SUCH_SYSTEM' -DiameterMm 100.0
$ghost = Invoke-HzTool -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-ghost-system' -Arguments @{
    target_document = $Document; instance_id = $inst; requirement_set = $ghostSystem
    level_id = [long]$level.element_id }
$ghostText = $ghost.Text
if (-not $ghost.IsError) { $ghostText = ($ghost.Result | ConvertTo-Json -Depth 12 -Compress) }
Add-HzProbe -Run $run -Id 'R1' -Name 'a system that is not in the document stops the plan and LISTS the ones that are' `
    -Expected 'system_type_not_found, naming what the document actually has' `
    -Observed (Limit-HzText $ghostText 240) `
    -Ok ($ghostText -match 'system_type_not_found') `
    -Evidence @{ reply = (Limit-HzText $ghostText 800)
                 note = 'a run put on the wrong system connects to the wrong things and reads correct in every view' }

# A BORE ON SOMETHING THAT HAS NONE. A cable tray has a width and a height; a
# declared diameter is a request it cannot answer, and answering it by setting
# one of them would be a different tray.
if ($trayTypes.Count -gt 0) {
    $badBore = New-HzRunSet -Id 'tray-bore' -Layer $layers['pipe'] -Produces 'cable_tray' `
        -Category 'OST_CableTray' -FamilyType ([string]$trayTypes[0].name) -DiameterMm 200.0
    $badBore.rules[0].layers = @($layers['pipe'])
    $planBad = Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_cad' -Label 'plan-tray-bore' -Arguments @{
        target_document = $Document; instance_id = $inst; requirement_set = $badBore
        level_id = [long]$level.element_id }
    $trayBefore = Get-HzElementCount -Run $run -Categories @('OST_CableTray') -Label 'tray-bore-before'
    $badArgs = @{
        target_document = $Document; instance_id = $inst; requirement_set = $badBore
        apply_binding = $planBad.Result.apply_binding
        actions = $planBad.Result.execute_plan_request.actions
        candidate_index = $planBad.Result.candidate_index }
    $badDry = Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-tray-bore-dry' `
        -Arguments (Copy-HzArgs $badArgs @{ dry_run = $true })
    $badTokens = Get-HzPath $badDry.Result 'rehearsal', 'tokens_by_key'
    $badActs = @($planBad.Result.execute_plan_request.actions | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
    foreach ($a in $badActs) {
        $t = Get-HzProp $badTokens $a.key
        if ($t) { $a | Add-Member -NotePropertyName confirmation_token -NotePropertyValue $t -Force }
    }
    # THE REAL WRITE. A rehearsal creates nothing and so never touches the bore;
    # asking it to refuse would have been asking the wrong half of the command.
    $badApply = Invoke-HzTool -Run $run -Tool 'horizun_apply_cad_plan' -Label 'apply-tray-bore' `
        -Arguments (Copy-HzArgs $badArgs @{ dry_run = $false; actions = $badActs
            idempotency_key = (New-HzKey $run 'apply-tray-bore') })
    $badText = $badApply.Text
    if (-not $badApply.IsError) { $badText = ($badApply.Result | ConvertTo-Json -Depth 12 -Compress) }
    $trayAfter = Get-HzElementCount -Run $run -Categories @('OST_CableTray') -Label 'tray-bore-after'

    Add-HzProbe -Run $run -Id 'R2' -Name 'a diameter asked of a run that has none is refused, and NOTHING is written' `
        -Expected 'a refusal naming the width and height a tray really has, and no new tray' `
        -Observed ("delta={0} reply={1}" -f ($trayAfter - $trayBefore), (Limit-HzText $badText 200)) `
        -Ok ($badText -match 'diameter' -and $badText -match '(?i)width' -and
             ($trayAfter - $trayBefore) -eq 0) `
        -Evidence @{ reply = (Limit-HzText $badText 700)
                     trays_before = $trayBefore; trays_after = $trayAfter }
} else {
    Add-HzProbe -Run $run -Id 'R2' -Name 'a diameter asked of a run that has none is refused' `
        -Expected 'a cable tray type to ask it of' `
        -Observed 'this document has no cable tray type' -Status 'fixture_missing'
}

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
