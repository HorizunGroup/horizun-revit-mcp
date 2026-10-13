#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6 TANDA 4 (6.15), LIVE: an issued revision is written last.

  One batch creates a revision with a description, puts it on a sheet and issues it;
  a second updates the ISSUED revision's description and keeps it issued. Both must
  apply and verify: Revit locks an issued revision, so `issued` has to be written
  after everything else, and an issued revision re-stated as issued is un-issued
  first.

  Needs a DISPOSABLE fixture with at least one sheet, opened from -Path (the harness
  activates it and never saves it).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$Path,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t4' -Document $Document
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_open_document' -Label 'activate' -Arguments @{
    path = $Path; idempotency_key = (New-HzKey $run 'activate') }
$sheets = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'sheets' -Arguments @{
    categories = @('OST_Sheets'); include_links = $false; max_rows = 20 }).Result.rows | Where-Object { -not $_.is_element_type })
if ($sheets.Count -eq 0) { throw 'HARNESS: the fixture has no sheet' }
$sheetId = [long]$sheets[0].element_id
$tag = $run.RunId.Substring($run.RunId.Length - 8)

Write-Host "`n== create, place and issue in one batch ==" -ForegroundColor Cyan
$create = Invoke-HzWrite -Run $run -Tool 'horizun_manage_revisions' -Label 'create-issued' -AllowRefusal -Arguments @{
    target_document = $Document
    actions = @(@{ key = 'r'; operation = 'create_revision'; description = "HZ T4 $tag"; revision_date = '2026-10-08'
                   issued_by = 'HZ'; issued_to = 'HZ'; issued = $true; sheet_ids = @($sheetId) }) }
$createOk = $null -ne $create.Apply -and $create.Apply.Ok
$revId = $null
if ($createOk) {
    $revId = Get-HzPath $create.Apply.Result 'aliases', 'r'
    if (-not $revId) { $revId = Get-HzProp @(Get-HzProp $create.Apply.Result 'rows')[0] 'revision_id' }
}
Add-HzProbe -Run $run -Id 'P615a' -Name '6.15 a revision created with a description and a sheet can be issued in the same batch' `
    -Expected 'applied and verified; a revision id' `
    -Observed ("applied={0} revision={1} text={2}" -f $createOk, $revId, (Limit-HzText $(if ($create.Apply) { $create.Apply.Text } else { $create.Dry.Text }) 220)) `
    -Ok ($createOk -and $null -ne $revId) -Evidence @{ reply = $(if ($createOk) { $create.Apply.Result } else { $null }) }

Write-Host "`n== update the issued revision ==" -ForegroundColor Cyan
$update = $(if ($revId) { Invoke-HzWrite -Run $run -Tool 'horizun_manage_revisions' -Label 'update-issued' -AllowRefusal -Arguments @{
    target_document = $Document
    actions = @(@{ key = 'u'; operation = 'update_revision'; revision_id = [long]$revId; description = "HZ T4 $tag (amended)"; issued = $true }) } } else { $null })
$updateOk = $null -ne $update -and $null -ne $update.Apply -and $update.Apply.Ok
$readBack = $(if ($revId) { (Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label 'revision' -Arguments @{
    element_ids = @([long]$revId); include_links = $false; parameter_format = 'full'
    return_parameters = @('Revision Description', 'Issued') }).Result } else { $null })
$row = $(if ($readBack) { @($readBack.rows)[0] } else { $null })
$desc = $(if ($row) { Get-HzPath $row 'parameters', 'Revision Description', 'raw' } else { $null })
$issued = $(if ($row) { Get-HzPath $row 'parameters', 'Issued', 'raw' } else { $null })
Add-HzProbe -Run $run -Id 'P615b' -Name '6.15 an issued revision re-stated as issued takes a new description and stays issued' `
    -Expected 'applied; Revision Description = amended; Issued = 1' `
    -Observed ("applied={0} description={1} issued={2} text={3}" -f $updateOk, $desc, $issued,
        (Limit-HzText $(if ($update -and $update.Apply) { $update.Apply.Text } elseif ($update) { $update.Dry.Text } else { '' }) 200)) `
    -Ok ($updateOk -and [string]$desc -eq "HZ T4 $tag (amended)" -and [int]$issued -eq 1) -Evidence @{ row = $row }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
