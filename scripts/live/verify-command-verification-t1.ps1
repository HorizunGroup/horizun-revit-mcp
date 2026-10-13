#Requires -Version 5.1
<#
  AUDIT 2026-10-08, LOTE 6 TANDA 1, LIVE: deliver_ifc leaves the model as it found it,
  and the IDS pre-check compares what the IFC will say.

  6.1  horizun_deliver_ifc committed the IFC exporter's bookkeeping and left the
       document modified. Measured independently of the command's own report: the
       fixture is saved, the IFC delivered, and a second save states whether the
       document was modified before it (was_modified_before).
  6.4  the Revit pre-check compared a length in feet and a Yes/No as 1/0 against an
       IDS written in SI and TRUE/FALSE. Two 3 m walls must read 3 (not 9.84) and
       Room Bounding must read TRUE.
  6.5  sibling xs:pattern facets are alternatives: a wall named by either of two
       conventions passes, both in the pre-check (type name) and in the exported
       file (IfcWall.Name).

  Needs a DISPOSABLE, writable fixture document already open and active (its title
  in -Document): the harness builds two walls and SAVES it. Synthetic IDS, written
  to the run's work directory.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'command-verification-t1' -Document $Document
New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null

$ids = @'
<?xml version="1.0" encoding="UTF-8"?>
<ids xmlns="http://standards.buildingsmart.org/IDS"
     xmlns:xs="http://www.w3.org/2001/XMLSchema"
     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
     xsi:schemaLocation="http://standards.buildingsmart.org/IDS http://standards.buildingsmart.org/IDS/1.0/ids.xsd">
  <info><title>Horizun live probe - command verification t1</title><version>1.0</version><date>2026-10-08</date></info>
  <specifications>
    <specification name="Room Bounding is a boolean" ifcVersion="IFC4" identifier="T1-BOOL">
      <applicability minOccurs="1" maxOccurs="unbounded"><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
      <requirements>
        <property dataType="IFCBOOLEAN" cardinality="required">
          <propertySet><simpleValue>Constraints</simpleValue></propertySet>
          <baseName><simpleValue>Room Bounding</simpleValue></baseName>
          <value><simpleValue>TRUE</simpleValue></value>
        </property>
      </requirements>
    </specification>
    <specification name="Unconnected Height in metres" ifcVersion="IFC4" identifier="T1-SI">
      <applicability minOccurs="1" maxOccurs="unbounded"><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
      <requirements>
        <property dataType="IFCLENGTHMEASURE" cardinality="required">
          <propertySet><simpleValue>Constraints</simpleValue></propertySet>
          <baseName><simpleValue>Unconnected Height</simpleValue></baseName>
          <value><xs:restriction base="xs:double"><xs:minInclusive value="2.9"/><xs:maxInclusive value="3.1"/></xs:restriction></value>
        </property>
      </requirements>
    </specification>
    <specification name="Two naming conventions" ifcVersion="IFC4" identifier="T1-PAT">
      <applicability minOccurs="1" maxOccurs="unbounded"><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
      <requirements>
        <attribute cardinality="required">
          <name><simpleValue>Name</simpleValue></name>
          <value><xs:restriction base="xs:string"><xs:pattern value="Basic Wall:.*"/><xs:pattern value="__TYPE_PREFIX__.*"/></xs:restriction></value>
        </attribute>
      </requirements>
    </specification>
  </specifications>
</ids>
'@

function Save-HzFixture([string]$Label) {
    $a = @{ target_document = $Document; idempotency_key = (New-HzKey $run "save-$Label") }
    (Invoke-HzToolStrict -Run $run -Tool 'horizun_save_document' -Label "save-$Label" -Arguments $a).Result
}

Write-Host "`n== two 3 m walls, then a save ==" -ForegroundColor Cyan
$level = Get-HzFirstLevel $run
$made = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'walls' -Arguments @{
    target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'wall'; start = @(0.0, 0.0, 0.0); end = @(6000.0, 0.0, 0.0); height = 3000.0; level_id = [long]$level.element_id },
        @{ kind = 'wall'; start = @(6000.0, 0.0, 0.0); end = @(6000.0, 4000.0, 0.0); height = 3000.0; level_id = [long]$level.element_id })
}
if ([int]$made.Apply.Result.created_verified -ne 2) { throw 'HARNESS: two walls were not verified' }
# THE SECOND CONVENTION IS READ OFF THE MODEL: the default wall type differs per
# template, and the pre-check names a wall by its TYPE while the file names it
# "Basic Wall:<type>:<id>". Only the type's leading word goes in, so the pattern
# needs no XSD escaping and the file's name still matches only the first pattern.
$wallRows = @(Get-HzElements -Run $run -Categories @('OST_Walls') -Label 'walls')
$typeName = [string]$wallRows[0].type
$prefix = [regex]::Match($typeName, '^[A-Za-z]+').Value
if (-not $prefix) { throw "HARNESS: the wall type '$typeName' has no leading word to build the second pattern from" }
$run.Fixture['wall_type'] = $typeName
$idsPath = Join-Path $run.WorkDir 'probe-t1.ids'
[IO.File]::WriteAllText($idsPath, $ids.Replace('__TYPE_PREFIX__', $prefix), (New-Object System.Text.UTF8Encoding($false)))
$null = Save-HzFixture 'baseline'

Write-Host "`n== pre-check (rehearsal) ==" -ForegroundColor Cyan
$deliverArgs = @{ target_document = $Document; output_folder = $OutputFolder; output_name = 'hz-probe-t1'
                  ifc_version = 'IFC4'; ids_path = $idsPath; precheck = $true; overwrite = $true; max_findings = 10 }
$dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_deliver_ifc' -Label 'deliver-dry' -Arguments (Copy-HzArgs $deliverArgs @{ dry_run = $true })
$pre = @{}
foreach ($r in @($dry.Result.precheck.results)) { $pre[[string]$r.identifier] = $r }
function Get-HzObserved($r) { @(@($r.findings) | ForEach-Object { [string]$_.observed }) -join ' ; ' }

$si = $pre['T1-SI']
Add-HzProbe -Run $run -Id 'P64a' -Name '6.4 a length is compared in metres, the unit the IFC carries' `
    -Expected 'T1-SI pass on both walls (3 within 2.9..3.1)' `
    -Observed ("outcome={0} passing={1} failing={2} undecidable={3} observed={4}" -f $si.outcome, $si.passing, $si.failing, $si.not_decidable, (Get-HzObserved $si)) `
    -Ok ([string]$si.outcome -eq 'pass' -and [int]$si.failing -eq 0) -Evidence @{ result = $si }
$bool = $pre['T1-BOOL']
Add-HzProbe -Run $run -Id 'P64b' -Name '6.4 a Yes/No reads TRUE/FALSE' `
    -Expected 'T1-BOOL pass on both walls' `
    -Observed ("outcome={0} passing={1} failing={2} observed={3}" -f $bool.outcome, $bool.passing, $bool.failing, (Get-HzObserved $bool)) `
    -Ok ([string]$bool.outcome -eq 'pass' -and [int]$bool.failing -eq 0) -Evidence @{ result = $bool }
$pat = $pre['T1-PAT']
Add-HzProbe -Run $run -Id 'P65a' -Name '6.5 sibling patterns are alternatives (pre-check, type name)' `
    -Expected 'T1-PAT pass: the type name matches the second pattern (its leading word) only' `
    -Observed ("outcome={0} passing={1} failing={2} observed={3}" -f $pat.outcome, $pat.passing, $pat.failing, (Get-HzObserved $pat)) `
    -Ok ([string]$pat.outcome -eq 'pass' -and [int]$pat.failing -eq 0) -Evidence @{ result = $pat }

Write-Host "`n== the delivery ==" -ForegroundColor Cyan
$applyArgs = Copy-HzArgs $deliverArgs @{ dry_run = $false; idempotency_key = (New-HzKey $run 'deliver') }
$token = Get-HzProp $dry.Result 'confirmation_token'
if ($token) { $applyArgs['confirmation_token'] = $token }
$done = (Invoke-HzToolStrict -Run $run -Tool 'horizun_deliver_ifc' -Label 'deliver' -Arguments $applyArgs -TimeoutSec 1800).Result
$iso = Get-HzProp $done 'model_isolation'
$after = Save-HzFixture 'after-deliver'
Add-HzProbe -Run $run -Id 'P61' -Name '6.1 deliver_ifc leaves the document unmodified' `
    -Expected 'model_isolation present; the next save finds was_modified_before=false' `
    -Observed ("isolation={0} was_modified_before={1} saved={2}" -f
        $(if ($iso) { (Get-HzProp $iso 'status') } else { '(absent)' }), (Get-HzProp $after 'was_modified_before'), (Get-HzProp $after 'saved')) `
    -Ok ($null -ne $iso -and (Get-HzProp $after 'was_modified_before') -eq $false) `
    -Evidence @{ model_isolation = $iso; model_changes = (Get-HzProp $done 'model_changes'); save = $after }

$fileRun = @(@(Get-HzPath $done 'ids_validation', 'results') | Where-Object { [string]$_.identifier -eq 'T1-PAT' })
$fp = $(if ($fileRun.Count) { $fileRun[0] } else { $null })
Add-HzProbe -Run $run -Id 'P65b' -Name '6.5 sibling patterns are alternatives (exported file, IfcWall.Name)' `
    -Expected 'T1-PAT pass in the file: the IFC name matches the first pattern only' `
    -Observed ("outcome={0} passing={1} failing={2} observed={3}" -f $(if ($fp) { $fp.outcome } else { '(none)' }),
        $(if ($fp) { $fp.passing } else { '' }), $(if ($fp) { $fp.failing } else { '' }), $(if ($fp) { Get-HzObserved $fp } else { '' })) `
    -Ok ($null -ne $fp -and [string]$fp.outcome -eq 'pass') -Evidence @{ result = $fp }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
