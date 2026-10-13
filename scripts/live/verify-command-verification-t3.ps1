#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6 TANDA 3 (6.11-6.13), LIVE.

  6.11  a PINNED compound wall joined to a neighbour at a corner splits and verifies:
        the re-pin is now re-read and the carrier is held to its original joins, so a
        normal split must still pass (the regression this change could cause), and
        every produced wall reads back pinned.
  6.12  a host selection that reads back answers Ok with host_elements_verified.
  6.13  a snapshot compared with the active document keys its parameters by identity
        (bip:/guid:), and a snapshot compared with ANOTHER project is refused.

  Needs a DISPOSABLE fixture open and active (-Document), a second document open
  (-OtherDocument, a different project), and a compound wall type in -Document.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$OtherDocument,
    [Parameter(Mandatory)][string]$OtherPath,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t3' -Document $Document

Write-Host "`n== fixture: an L of two compound walls, the first pinned ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$types = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'wall-types' -Arguments @{
    categories = @('OST_Walls'); include_links = $false; include_types = $true; max_rows = 200 }).Result.rows |
    Where-Object { $_.is_element_type -and [string]$_.family -eq 'Basic Wall' -and [string]$_.name -match 'Exterior' })
if ($types.Count -eq 0) { throw 'HARNESS: no Basic Wall type named Exterior* to split' }
$typeId = [long]$types[0].element_id
$run.Fixture['wall_type'] = [string]$types[0].name
$X = 60000.0
$walls = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'walls' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'wall'; type_id = $typeId; start = @($X, 0.0, 0.0); end = @(($X + 6000.0), 0.0, 0.0); height = 3000.0; level_id = $lv },
        @{ kind = 'wall'; type_id = $typeId; start = @(($X + 6000.0), 0.0, 0.0); end = @(($X + 6000.0), 5000.0, 0.0); height = 3000.0; level_id = $lv }) }
$ids = @(@($walls.Apply.Result.rows) | ForEach-Object { [long](Get-HzProp $_ 'element_id') })
$null = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'pin' -Arguments @{
    target_document = $Document; operations = @(@{ operation = 'pin'; element_ids = @($ids[0]) }) }

Write-Host "`n== 6.11 split the pinned wall ==" -ForegroundColor Cyan
$split = Invoke-HzWrite -Run $run -Tool 'horizun_split_multilayer_walls' -Label 'split' -AllowRefusal -Arguments @{
    target_document = $Document; element_ids = @($ids[0]) }
$splitOk = $null -ne $split.Apply -and $split.Apply.Ok
$splitText = $(if ($split.Apply) { $split.Apply.Text } else { $split.Dry.Text })
$produced = @()
if ($splitOk) {
    $produced = @(@(Get-HzPath $split.Apply.Result 'walls') | ForEach-Object { @(Get-HzProp $_ 'layers') } |
        ForEach-Object { Get-HzProp $_ 'resulting_wall_id' } | Where-Object { $_ } | ForEach-Object { [long]$_ } | Select-Object -Unique)
}
$pinPy = @"
from Autodesk.Revit.DB import *
ids = [$((@($ids[0]) + $produced) -join ', ')]
rows = []
for i in ids:
    e = doc.GetElement(ElementId(i))
    rows.append({'id': i, 'exists': e is not None, 'pinned': (e.Pinned if e is not None else None)})
__output__ = {'status': 'self_reported_verified', 'rows': rows, 'verification': {'evidence': rows}}
"@
$pins = Invoke-HzToolStrict -Run $run -Tool 'horizun_execute_python' -Label 'read-pins' -Arguments @{
    target_document = $Document; code = $pinPy; idempotency_key = (New-HzKey $run 'read-pins') }
$pinRows = @(Get-HzPath $pins.Result 'output', 'rows')
$unpinned = @($pinRows | Where-Object { $_.exists -and $_.pinned -ne $true })
Add-HzProbe -Run $run -Id 'P611' -Name '6.11 a pinned compound wall joined at a corner splits, verifies, and stays pinned' `
    -Expected 'split applied (no verify_join_unexpected, no verify_pin_not_restored); every wall it became reads back pinned' `
    -Observed ("applied={0} produced={1} unpinned={2} text={3}" -f $splitOk, $produced.Count, $unpinned.Count, (Limit-HzText $splitText 200)) `
    -Ok ($splitOk -and $produced.Count -ge 2 -and $unpinned.Count -eq 0 -and @($pinRows | Where-Object { $_.exists }).Count -ge 2) -Evidence @{ pins = $pinRows; split = $(if ($splitOk) { $split.Apply.Result } else { $null }) }

Write-Host "`n== 6.12 navigate ==" -ForegroundColor Cyan
$nav = Invoke-HzTool -Run $run -Tool 'horizun_navigate' -Label 'select' -Arguments @{
    target_document = $Document; operation = 'select'; element_ids = @($ids[1]) }
# A host-only selection takes the path that already refused an unread-back selection;
# the change in 6.12 is on the linked path (SelectAndVerify), which needs a link
# this fixture does not have. This is the regression check of the host path.
Add-HzProbe -Run $run -Id 'P612' -Name '6.12 (regression) a host selection that reads back answers Ok' `
    -Expected 'Ok; selection_verified true' `
    -Observed ("ok={0} selection_verified={1}" -f $nav.Ok, $(if ($nav.Ok) { Get-HzProp $nav.Result 'selection_verified' } else { Limit-HzText $nav.Text 200 })) `
    -Ok ($nav.Ok -and (Get-HzProp $nav.Result 'selection_verified') -eq $true) -Evidence @{ reply = $nav.Result }

Write-Host "`n== 6.13 model diff ==" -ForegroundColor Cyan
$snap = (Invoke-HzToolStrict -Run $run -Tool 'horizun_model_diff' -Label 'snapshot' -Arguments @{
    target_document = $Document; operation = 'snapshot'; categories = @('OST_Walls') }).Result
$snapId = [string](Get-HzProp $snap 'snapshot_id')
if (-not $snapId) { $snapId = [string](Get-HzProp $snap 'id') }
$null = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'comment' -Arguments @{
    target_document = $Document; writes = @(@{ target_id = $ids[1]; parameter = 'Comments'; value = 'changed after the snapshot' }) }
$cmp = Invoke-HzTool -Run $run -Tool 'horizun_model_diff' -Label 'compare-same' -Arguments @{
    target_document = $Document; operation = 'compare'; before = $snapId; after = 'active' }
$keys = @()
if ($cmp.Ok) {
    foreach ($row in @(Get-HzPath $cmp.Result 'detail', 'rows')) {
        foreach ($c in @(Get-HzProp $row 'changes')) { if ($c) { $keys += [string](Get-HzProp $c 'field') } }
    }
}
Add-HzProbe -Run $run -Id 'P613a' -Name '6.13 a parameter change is keyed by its BuiltInParameter, not its translated name' `
    -Expected 'the Comments change appears under bip:ALL_MODEL_INSTANCE_COMMENTS' `
    -Observed ("ok={0} keys={1}" -f $cmp.Ok, (($keys | Select-Object -Unique | Select-Object -First 6) -join ', ')) `
    -Ok ($cmp.Ok -and ($keys -contains 'param:bip:ALL_MODEL_INSTANCE_COMMENTS')) -Evidence @{ compare = $(if ($cmp.Ok) { $cmp.Result } else { $cmp.Text }) }

$k = New-HzKey $run 'activate-other'
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_open_document' -Label 'activate-other' -Arguments @{ path = $OtherPath; idempotency_key = $k }
$cross = Invoke-HzTool -Run $run -Tool 'horizun_model_diff' -Label 'compare-other' -Arguments @{
    target_document = $OtherDocument; operation = 'compare'; before = $snapId; after = 'active' }
# WHAT THE SNAPSHOT CAN PROVE decides what is expected. With a document creation GUID (Revit
# 2024+) another project is refused. Without one (Revit 2023) the identity is Project Information:
# a project whose Project Information differs is still refused, and one that shares it - two
# projects of one template - is compared with identity_proven:false and its warning (owner
# decision 2026-10-10, option B). Never a silent "same project".
$snapCreation = [string](Get-HzPath $snap 'meta', 'document', 'creation_guid')
$crossProven = if ($cross.Ok) { Get-HzProp $cross.Result 'identity_proven' } else { $null }
$crossWarn = if ($cross.Ok) { @(@(Get-HzProp $cross.Result 'warnings') | Where-Object { [string](Get-HzProp $_ 'code') -eq 'identity_unprovable_2023' }).Count } else { 0 }
$refused = (-not $cross.Ok) -and $cross.Text -match 'DIFFERENT projects'
$unprovenSaid = $cross.Ok -and $crossProven -eq $false -and $crossWarn -eq 1 -and [string](Get-HzProp $cross.Result 'project_identity_kind') -eq 'project_info'
Add-HzProbe -Run $run -Id 'P613b' -Name '6.13 another project is refused, or (Revit 2023, one template) compared with identity_proven:false and its warning' `
    -Expected $(if ($snapCreation) { 'creation GUID: refused naming DIFFERENT projects' } else { 'no creation GUID: refused, or compared with identity_proven=false, kind project_info and warning identity_unprovable_2023' }) `
    -Observed ("creation_guid={0} ok={1} identity_proven={2} warning={3} text={4}" -f ($(if ($snapCreation) { 'yes' } else { 'none' })), $cross.Ok, $crossProven, $crossWarn, (Limit-HzText $cross.Text 200)) `
    -Ok ($(if ($snapCreation) { $refused } else { $refused -or $unprovenSaid })) -Evidence @{ reply = $(if ($cross.Ok) { $cross.Result } else { $cross.Text }) }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
