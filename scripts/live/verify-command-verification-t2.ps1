#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6 TANDA 2 (6.6-6.9), LIVE.

  6.7  remove_fields resolves every position first (1,2 of four fields leaves the
       first and the last), and a filter's number_value is read in mm (a 3000 mm wall
       passes "equal 3000").
  6.8  copying a wall that hosts a door verifies: the door's copy is a dependent.
  6.9  a write to a group member whose parameter does not vary is refused by default;
       with allow_vary_between_groups=true the definition change is listed, re-read,
       and horizun_undo reverts it.
  6.6  with the project base point raised 2600 mm, plan_from_ifc derives each wall's
       base offset from the level's internal elevation (ProjectElevation).

  Needs a DISPOSABLE fixture open and active (-Document) with a second document open
  (the reset activates it), a door type in the model, and Python enabled (6.6 moves
  the project base point through it: fixture set-up, not verification).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$OutputFolder,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t2' -Document $Document
New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
$tag = $run.RunId.Substring($run.RunId.Length - 8)

Write-Host "`n== fixture: four 3 m walls and a door ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$X = 40000.0
$walls = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'walls' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'wall'; start = @($X, 0.0, 0.0); end = @(($X + 6000.0), 0.0, 0.0); height = 3000.0; level_id = $lv },
        @{ kind = 'wall'; start = @(($X + 6000.0), 0.0, 0.0); end = @(($X + 6000.0), 4000.0, 0.0); height = 3000.0; level_id = $lv },
        @{ kind = 'wall'; start = @(($X + 6000.0), 4000.0, 0.0); end = @($X, 4000.0, 0.0); height = 3000.0; level_id = $lv },
        @{ kind = 'wall'; start = @($X, 4000.0, 0.0); end = @($X, 0.0, 0.0); height = 3000.0; level_id = $lv }) }
$wallIds = @(@($walls.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
if ($wallIds.Count -ne 4) { throw "HARNESS: four walls were not created ($($wallIds.Count))" }
$wallTypeName = [string](@(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'wall-type') | Where-Object { $wallIds -contains [long]$_.element_id } | Select-Object -First 1).type
$doorTypes = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'door-types' -Arguments @{
    categories = @('OST_Doors'); include_links = $false; include_types = $true; max_rows = 50 }).Result.rows | Where-Object { $_.is_element_type })
if ($doorTypes.Count -eq 0) { throw 'HARNESS: the fixture has no door type' }
$door = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'door' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(@{ kind = 'family_instance'; type_id = [long]$doorTypes[0].element_id; point = @(($X + 3000.0), 0.0, 0.0)
                    coordinate_mode = 'absolute'; level_id = $lv; host_id = $wallIds[0] }) }
$doorId = [long](Get-HzProp @($door.Apply.Result.rows)[0] 'element_id')
$run.Fixture['walls'] = $wallIds; $run.Fixture['door'] = $doorId

# ------------------------------------------------------------------ 6.7
Write-Host "`n== 6.7 schedule ==" -ForegroundColor Cyan
$sched = Invoke-HzWrite -Run $run -Tool 'horizun_create_schedule' -Label 'schedule' -Arguments @{
    target_document = $Document; category = 'OST_Walls'; name = "HZ T2 $tag"; itemized = $true; include_links = $false
    fields = @('Family and Type', 'Length', 'Unconnected Height', 'Comments') }
$sid = [long](Get-HzProp $sched.Apply.Result 'schedule_id')
function Get-HzScheduleCells([string]$Label) {
    (Invoke-HzToolStrict -Run $run -Tool 'horizun_get_schedule_data' -Label $Label -Arguments @{ schedule_id = $sid; max_rows = 200 }).Result
}
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_schedules' -Label 'filter-mm' -Arguments @{
    target_document = $Document
    actions = @(@{ operation = 'set_filters'; schedule_id = $sid
                   filters = @(@{ field = 'Unconnected Height'; operator = 'equal'; number_value = 3000 }) }) }
$filtered = Get-HzScheduleCells 'cells-filtered'
$bodyRows = @($filtered.body)
$dataRows = @($bodyRows | Where-Object { (@($_) -join '|') -match 'Basic Wall|Generic|Interior|Exterior' })
Add-HzProbe -Run $run -Id 'P67b' -Name '6.7 a filter number on a length field is read in mm' `
    -Expected 'equal 3000 keeps the four 3000 mm walls (>= 4 data rows)' `
    -Observed ("body_rows={0} wall_rows={1}" -f $bodyRows.Count, $dataRows.Count) `
    -Ok ($dataRows.Count -ge 4) -Evidence @{ body = $bodyRows }
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_schedules' -Label 'filter-clear' -Arguments @{
    target_document = $Document; actions = @(@{ operation = 'set_filters'; schedule_id = $sid; filters = @() }) }
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_schedules' -Label 'remove-1-2' -Arguments @{
    target_document = $Document
    actions = @(@{ operation = 'remove_fields'; schedule_id = $sid; fields = @(@{ field_index = 1 }, @{ field_index = 2 }) }) }
$after = Get-HzScheduleCells 'cells-after-remove'
$headerCells = (@(@($after.body)[0]) + @(@($after.header) | ForEach-Object { @($_) })) | ForEach-Object { [string]$_ }
$has = { param($n) @($headerCells | Where-Object { $_ -eq $n }).Count -gt 0 }
Add-HzProbe -Run $run -Id 'P67a' -Name '6.7 removing fields 1 and 2 of four leaves the first and the last' `
    -Expected 'columns: Family and Type, Comments (Length and Unconnected Height gone)' `
    -Observed ("columns={0} cells={1}" -f $after.body_columns, (($headerCells | Select-Object -First 8) -join ' | ')) `
    -Ok ([int]$after.body_columns -eq 2 -and (& $has 'Comments') -and -not (& $has 'Length') -and -not (& $has 'Unconnected Height')) `
    -Evidence @{ header = $after.header; first_body_row = @($after.body)[0] }

# ------------------------------------------------------------------ 6.8
Write-Host "`n== 6.8 copy a wall with its door ==" -ForegroundColor Cyan
$copy = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'copy-wall' -AllowRefusal -Arguments @{
    target_document = $Document; units = 'mm'
    operations = @(@{ operation = 'copy'; element_ids = @($wallIds[0]); vector = @(0.0, -3000.0, 0.0) }) }
$copyOk = $null -ne $copy.Apply -and $copy.Apply.Ok
$row = $(if ($copyOk) { @($copy.Apply.Result.rows)[0] } else { $null })
Add-HzProbe -Run $run -Id 'P68' -Name '6.8 a wall copied with its door verifies; the door copy is a dependent' `
    -Expected 'verified; copies_present 1; dependent_created_ids 1; no unexplained' `
    -Observed $(if ($row) { "verified={0} copies_present={1} dependents={2} unexplained={3}" -f (Get-HzProp $row 'verified'),
        (Get-HzProp $row 'copies_present'), (@(Get-HzProp $row 'dependent_created_ids')).Count, (@(Get-HzProp $row 'unexplained_created_ids')).Count }
        else { 'refused: ' + (Limit-HzText $(if ($copy.Apply) { $copy.Apply.Text } else { $copy.Dry.Text }) 300) }) `
    -Ok ($copyOk -and (Get-HzProp $row 'verified') -eq $true -and (@(Get-HzProp $row 'dependent_created_ids')).Count -eq 1) `
    -Evidence @{ row = $row }

# ------------------------------------------------------------------ 6.9
Write-Host "`n== 6.9 group member and the definition change ==" -ForegroundColor Cyan
$spf = Join-Path $run.WorkDir 'hz-t2.txt'
$pname = "HZ_T2_$tag"
$null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_parameters' -Label 'param' -Arguments @{
    target_document = $Document; operation = 'create_shared'; name = $pname; spf_path = $spf; data_type = 'String.Text'
    categories = @('OST_Walls'); binding_kind = 'Instance'; allow_vary_between_groups = $false }
$grp = Invoke-HzWrite -Run $run -Tool 'horizun_manage_groups' -Label 'group' -Arguments @{
    target_document = $Document; operation = 'create'; element_ids = @($wallIds[2], $wallIds[3]); name = "HZ_T2_G_$tag" }
$writeArgs = @{ target_document = $Document; writes = @(@{ target_id = $wallIds[2]; parameter = $pname; value = 'one' }) }
$refused = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'write-member' -AllowRefusal -Arguments $writeArgs
$refText = $(if ($refused.Apply) { $refused.Apply.Text } else { $refused.Dry.Text })
$refConfirmed = $(if ($refused.Apply -and $refused.Apply.Ok) { Get-HzProp $refused.Apply.Result 'writes_confirmed' } else { 0 })
Add-HzProbe -Run $run -Id 'P69a' -Name '6.9 a group member is refused unless the definition change is asked for' `
    -Expected 'nothing written; the reply names the group and allow_vary_between_groups' `
    -Observed ("confirmed={0} text={1}" -f $refConfirmed, (Limit-HzText $refText 220)) `
    -Ok ([int]$refConfirmed -eq 0 -and $refText -match 'model group' -and $refText -match 'allow_vary_between_groups') `
    -Evidence @{ dry = $refused.Dry.Result; apply = $(if ($refused.Apply) { $refused.Apply.Result } else { $null }) }
$allowed = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'write-member-allowed' `
    -Arguments (Copy-HzArgs $writeArgs @{ allow_vary_between_groups = $true })
$changes = @(Get-HzProp $allowed.Apply.Result 'definition_changes')
Add-HzProbe -Run $run -Id 'P69b' -Name '6.9 the allowed definition change is listed and re-read' `
    -Expected 'definition_changes 1, varies_across_groups_after true, verified; the value confirmed' `
    -Observed ("changes={0} after={1} verified={2} confirmed={3}" -f $changes.Count,
        $(if ($changes.Count) { Get-HzProp $changes[0] 'varies_across_groups_after' } else { '' }),
        $(if ($changes.Count) { Get-HzProp $changes[0] 'verified' } else { '' }), (Get-HzProp $allowed.Apply.Result 'writes_confirmed')) `
    -Ok ($changes.Count -eq 1 -and (Get-HzProp $changes[0] 'verified') -eq $true -and [int](Get-HzProp $allowed.Apply.Result 'writes_confirmed') -eq 1) `
    -Evidence @{ definition_changes = $changes; undo = (Get-HzProp $allowed.Apply.Result 'undo') }
$batchId = Get-HzPath $allowed.Apply.Result 'undo', 'batch_id'
$undo = $(if ($batchId) { Invoke-HzWrite -Run $run -Tool 'horizun_undo' -Label 'undo-vary' -AllowRefusal -Arguments @{
    target_document = $Document; operation = 'undo_last' } } else { $null })
$undoOk = $null -ne $undo -and $null -ne $undo.Apply -and $undo.Apply.Ok
Add-HzProbe -Run $run -Id 'P69c' -Name '6.9 horizun_undo reverts the value and the definition change' `
    -Expected 'undo committed; postconditions all verified (vary_between_groups back to false)' `
    -Observed $(if ($undoOk) { "postconditions_all_verified={0}" -f (Get-HzPath $undo.Apply.Result 'postconditions', 'all_verified') }
                elseif ($undo) { 'refused: ' + (Limit-HzText $(if ($undo.Apply) { $undo.Apply.Text } else { $undo.Dry.Text }) 300) }
                else { 'no undo batch id in the reply' }) `
    -Ok ($undoOk -and (Get-HzPath $undo.Apply.Result 'postconditions', 'all_verified') -eq $true) `
    -Evidence @{ undo = $(if ($undoOk) { $undo.Apply.Result } else { $null }) }

# ------------------------------------------------------------------ 6.6
Write-Host "`n== 6.6 elevated project base point ==" -ForegroundColor Cyan
$py = @"
from Autodesk.Revit.DB import *
bp = BasePoint.GetProjectBasePoint(doc)
lv = doc.GetElement(ElementId($lv))
t = Transaction(doc, 'HZ fixture: raise the project base point')
t.Start()
ElementTransformUtils.MoveElement(doc, bp.Id, XYZ(0, 0, 2600.0 / 304.8))
t.Commit()
lv = doc.GetElement(ElementId($lv))
elev = lv.Elevation * 304.8
proj = lv.ProjectElevation * 304.8
__output__ = {'status': 'self_reported_verified', 'level_elevation_mm': elev, 'level_project_elevation_mm': proj,
              'verification': {'evidence': [{'level_id': $lv, 'elevation_mm': elev, 'project_elevation_mm': proj}]}}
"@
$pyCall = Invoke-HzToolStrict -Run $run -Tool 'horizun_execute_python' -Label 'raise-pbp' -Arguments @{
    target_document = $Document; code = $py; idempotency_key = (New-HzKey $run 'raise-pbp') }
$elevMm = [double](Get-HzPath $pyCall.Result 'output', 'level_elevation_mm')
$projMm = [double](Get-HzPath $pyCall.Result 'output', 'level_project_elevation_mm')
$run.Fixture['level_elevation_mm'] = $elevMm; $run.Fixture['level_project_elevation_mm'] = $projMm
if ([Math]::Abs($elevMm - $projMm) -lt 1000) { throw "HARNESS: the base point did not separate Elevation ($elevMm) from ProjectElevation ($projMm)" }
$exp = Invoke-HzWrite -Run $run -Tool 'horizun_export' -Label 'export-ifc' -Arguments @{
    target_document = $Document; format = 'ifc'; output_path = (Join-Path $OutputFolder "hz-t2-$tag.ifc"); overwrite = $true }
$ifcPath = Join-Path $OutputFolder "hz-t2-$tag.ifc"
$plan = (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_from_ifc' -Label 'plan-ifc' -Arguments @{
    target_document = $Document; path = $ifcPath; level_id = $lv; only_kinds = @('wall')
    type_mapping = @{ IFCWALL = [string]$wallTypeName; IFCWALLSTANDARDCASE = [string]$wallTypeName } }).Result
# The rows create_elements would receive, where the offset is.
$wallRows = @(@(Get-HzPath $plan 'execute_plan_request', 'actions') | ForEach-Object { @(Get-HzPath $_ 'arguments', 'elements') } |
    Where-Object { $null -ne $_ -and [string](Get-HzProp $_ 'kind') -eq 'wall' -and $null -ne (Get-HzProp $_ 'offset') })
$basis = @($wallRows | ForEach-Object { [double]$_.start[2] - [double](Get-HzProp $_ 'offset') })
$offProj = @($basis | Where-Object { [Math]::Abs($_ - $projMm) -gt 1.0 })
Add-HzProbe -Run $run -Id 'P66' -Name '6.6 IFC wall offsets are measured from the level''s internal elevation' `
    -Expected ("every wall row: start.z - offset == ProjectElevation ({0} mm), not Elevation ({1} mm)" -f $projMm, $elevMm) `
    -Observed ("wall_rows={0} basis={1} off_project={2}" -f $wallRows.Count, (($basis | Select-Object -Unique | ForEach-Object { [Math]::Round($_, 1) }) -join ','), $offProj.Count) `
    -Ok ($wallRows.Count -ge 1 -and $offProj.Count -eq 0) -Evidence @{ rows = ($wallRows | Select-Object -First 6) }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
