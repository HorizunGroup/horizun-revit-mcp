#Requires -Version 5.1
<#
  ISO 19650 INFORMATION MANAGEMENT, LIVE.

  The probes for horizun_deliver_ifc, horizun_export with an information
  container, horizun_project_context and horizun_information_container, dot-
  sourced by scripts/verify-live.ps1 and exercised WITHOUT Revit by
  scripts/live-iso19650.tests.ps1. Every function talks to the bridge only
  through the -Call / -Apply scriptblocks it is handed, so the tests can drive
  the same code with recorded replies before a matrix run pays for Revit.

  WHAT THIS SECTION REFUSES TO ASSUME

    the delivery class      the disposable fixture of each year is a different
                            model. The class the mapping and the IDS name is
                            DISCOVERED by counting elements in the model, never
                            taken from the fixture this code was written against.
    the IFC version         IFC4x3 exists in IFCVersion from Revit 2024 on. The
                            delivery uses IFC4 (IfcDeliveryRules.VersionSchemaFamily:
                            IFC4 -> FILE_SCHEMA family IFC4) in every year, and the
                            IFC4x3 plan-or-refusal is its own probe with the
                            expectation named per year.
    the permission profile  host-resident writes (stamp, transition) need
                            full_write. The probe reads the profile from health
                            and then believes the REPLY: a refusal must name the
                            profile, a success must be a verified, logged copy.
    the model               nothing here writes a parameter unless the caller
                            passes -ReadyWrite, which commits the mapped code on
                            every element of the class in the DISPOSABLE model.

  Every temporary file lives in ONE folder per run under the harness scratch
  directory (never under the repository), and every record is sanitised before
  it can reach a report: a temp path carries the account name.
#>

# -----------------------------------------------------------------------------
# Sanitising. A record read by somebody who was not here must not carry the
# machine's profile path, in either of the two shapes it travels in.
# -----------------------------------------------------------------------------
function Protect-HorizunIsoText {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    # Most specific first: the temp folder usually lives under LOCALAPPDATA.
    $roots = @(
        @{ Root = ([IO.Path]::GetTempPath().TrimEnd('\', '/')); Token = '<temp>' },
        @{ Root = $env:TEMP; Token = '<temp>' },
        @{ Root = $env:LOCALAPPDATA; Token = '<localappdata>' },
        @{ Root = $env:APPDATA; Token = '<appdata>' },
        @{ Root = $env:USERPROFILE; Token = '<userprofile>' }
    )
    foreach ($pair in $roots) {
        if (-not [string]::IsNullOrEmpty($pair.Root) -and $pair.Root.Length -ge 3) {
            $out = $out.Replace($pair.Root, $pair.Token)
            # JSON-escaped form of the same path, as it appears inside a quoted reply.
            $out = $out.Replace($pair.Root.Replace('\', '\\'), $pair.Token)
        }
    }
    $out = [regex]::Replace($out, '[A-Za-z]:[\\/]+Users[\\/]+[A-Za-z0-9._-]+', '<user-profile>')
    $out = [regex]::Replace($out, '[A-Za-z]--Users-[A-Za-z0-9._]+-', '<user-profile-dir>-')
    if ($env:USERNAME -and $env:USERNAME.Length -ge 3) {
        $out = [regex]::Replace($out, [regex]::Escape($env:USERNAME), '<account>',
                                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    if ($env:COMPUTERNAME -and $env:COMPUTERNAME.Length -ge 3) {
        $out = [regex]::Replace($out, [regex]::Escape($env:COMPUTERNAME), '<machine>',
                                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    return $out
}

function Protect-HorizunIsoValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return (Protect-HorizunIsoText $Value) }
    $json = $Value | ConvertTo-Json -Depth 40 -Compress
    return ((Protect-HorizunIsoText $json) | ConvertFrom-Json)
}

function Limit-HorizunIsoText {
    param([string]$Text, [int]$Max = 400)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $clean = Protect-HorizunIsoText $Text
    if ($clean.Length -le $Max) { return $clean }
    return ($clean.Substring(0, $Max) + '...')
}

# -----------------------------------------------------------------------------
# The case catalog. Named FIRST, so a gated run reports every case NOT COVERED
# by name instead of quietly shrinking the denominator, and so every Revit year
# reports the same set of rows to the matrix.
# -----------------------------------------------------------------------------
function Get-HorizunIsoCaseCatalog {
    param([switch]$IncludeReadyWrite)
    $catalog = @(
        [pscustomobject]@{ Id = 'ISO-D1'; Tier = 'write'; Tool = 'horizun_deliver_ifc'
            Name = 'deliver_ifc dry run returns the plan, the model georeference and a token, and creates no file' }
        [pscustomobject]@{ Id = 'ISO-D2'; Tier = 'write'; Tool = 'horizun_deliver_ifc'
            Name = 'deliver_ifc with a generated TAB mapping and IDS passes export, schema_header and the container seal, and the sidecar verifies as match' }
        [pscustomobject]@{ Id = 'ISO-D3'; Tier = 'write'; Tool = 'horizun_deliver_ifc'
            Name = 'deliver_ifc decides ids_validate and pset_mapping on the exported file and deliverable_ready follows its gates' }
        [pscustomobject]@{ Id = 'ISO-D4'; Tier = 'write'; Tool = 'horizun_deliver_ifc'
            Name = 'a second deliver_ifc onto the sealed name is refused and nothing is exported' }
        [pscustomobject]@{ Id = 'ISO-D5'; Tier = 'write'; Tool = 'horizun_deliver_ifc'
            Name = 'IFC4x3 is planned where this Revit has it (2024+) and refused by name where it does not' }
        [pscustomobject]@{ Id = 'ISO-E1'; Tier = 'write'; Tool = 'horizun_export'
            Name = 'export ifc with information_container names the file after the container and seals it (verify = match)' }
        [pscustomobject]@{ Id = 'ISO-E2'; Tier = 'write'; Tool = 'horizun_export'
            Name = 'a repeated export onto a sealed name is refused whatever overwrite says, and nothing is exported' }
        [pscustomobject]@{ Id = 'ISO-E3'; Tier = 'write'; Tool = 'horizun_export'
            Name = 'export pdf of a real sheet with information_container seals one combined file' }
        [pscustomobject]@{ Id = 'ISO-H1'; Tier = 'host'; Tool = 'horizun_project_context'
            Name = 'project_context validate keeps invalid, inconsistent and incomplete apart' }
        [pscustomobject]@{ Id = 'ISO-H2'; Tier = 'host'; Tool = 'horizun_project_context'
            Name = 'project_context questions returns the ordered bilingual intake and counts an answered field' }
        [pscustomobject]@{ Id = 'ISO-H3'; Tier = 'host'; Tool = 'horizun_project_context'
            Name = 'project_context draft rehearsal returns the drafted document and writes no file' }
        [pscustomobject]@{ Id = 'ISO-H4'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'inspect of a temporary CDE names the bad name, the missing sidecar and the missing and overdue deliverables, and writes nothing' }
        [pscustomobject]@{ Id = 'ISO-H5'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'shared->published without approved_by is refused and nothing is written' }
        [pscustomobject]@{ Id = 'ISO-H6'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'shared->published with approved_by rehearses every check on a sealed container and writes nothing' }
        [pscustomobject]@{ Id = 'ISO-H7'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'shared->published apply lands a verified, logged copy that replays as already_transitioned, or the permission profile refuses it with nothing written' }
    )
    if ($IncludeReadyWrite) {
        $catalog += [pscustomobject]@{ Id = 'ISO-D6'; Tier = 'write'; Tool = 'horizun_deliver_ifc'
            Name = 'after the mapped parameter is written to every element of the class, deliver_ifc returns deliverable_ready=true' }
    }
    return $catalog
}

# One place turns a verify-live outcome into the consolidator's status. An
# unknown outcome is a harness bug and throws, rather than becoming a new bucket.
function ConvertTo-HorizunIsoStatus {
    param([string]$Outcome)
    switch ($Outcome) {
        'pass' { return 'passed' }
        'fail' { return 'failed' }
        'unverified' { return 'unverified' }
        'not_covered' { return 'not_covered' }
    }
    throw "HARNESS: unknown outcome '$Outcome'; the ISO section knows pass, fail, unverified and not_covered."
}

function New-HorizunIsoRecorder {
    param([switch]$IncludeReadyWrite)
    return [pscustomobject]@{
        Catalog = @(Get-HorizunIsoCaseCatalog -IncludeReadyWrite:$IncludeReadyWrite)
        Results = @{}
    }
}

# Record one case. A second verdict for the same case is a harness bug (a close
# that ran twice writes every verdict twice), so the FIRST one stands and the
# repetition is reported inside its detail.
function Complete-HorizunIsoCase {
    param($Recorder, [string]$Id, [string]$Outcome, [string]$Detail, $Evidence)
    $entry = $Recorder.Catalog | Where-Object { $_.Id -eq $Id } | Select-Object -First 1
    if (-not $entry) { throw "HARNESS: '$Id' is not in the ISO case catalog." }
    $status = ConvertTo-HorizunIsoStatus $Outcome
    if ($Recorder.Results.ContainsKey($Id)) {
        $Recorder.Results[$Id].detail = $Recorder.Results[$Id].detail +
            (' [HARNESS: a second verdict ({0}) was offered for this case and ignored]' -f $Outcome)
        return
    }
    $Recorder.Results[$Id] = [pscustomobject]@{
        id = $Id; name = $entry.Name; tool = $entry.Tool; tier = $entry.Tier
        outcome = $Outcome; status = $status
        detail = (Limit-HorizunIsoText $Detail 1200)
        evidence = (Protect-HorizunIsoValue $Evidence)
        recorded_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Complete-HorizunIsoTier {
    param($Recorder, [string]$Tier, [string]$Outcome, [string]$Detail)
    foreach ($entry in $Recorder.Catalog) {
        if ($entry.Tier -eq $Tier -and -not $Recorder.Results.ContainsKey($entry.Id)) {
            Complete-HorizunIsoCase $Recorder $entry.Id $Outcome $Detail $null
        }
    }
}

# Every catalog case, in catalog order. One that never received a verdict is
# UNVERIFIED and says so: the section ended before it ran, which is a harness
# defect and must not read like a pass or disappear.
function Close-HorizunIsoRecorder {
    param($Recorder)
    $rows = @()
    foreach ($entry in $Recorder.Catalog) {
        if (-not $Recorder.Results.ContainsKey($entry.Id)) {
            Complete-HorizunIsoCase $Recorder $entry.Id 'unverified' 'the ISO section ended before this case ran - a harness defect, not a product result' $null
        }
        $rows += $Recorder.Results[$entry.Id]
    }
    return $rows
}

# -----------------------------------------------------------------------------
# The run folder. One per run, under the harness scratch directory, never inside
# the repository.
# -----------------------------------------------------------------------------
function New-HorizunIsoRunDirectory {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$RunId, [string]$RepoRoot)
    if ($RunId -notmatch '^[A-Za-z0-9_-]{6,64}$') { throw "HARNESS: run id '$RunId' is not a safe folder name." }
    $rootFull = [IO.Path]::GetFullPath($Root)
    $path = [IO.Path]::GetFullPath((Join-Path $rootFull ('iso19650-' + $RunId)))
    if ($RepoRoot) {
        $repoFull = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($path.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
            throw "HARNESS: refusing to stage ISO probe files inside the repository ($repoFull)."
        }
    }
    if (Test-Path -LiteralPath $path) { throw "HARNESS: the run folder already exists: $path. A run folder is never reused." }
    $null = New-Item -ItemType Directory -Force -Path $path
    return $path
}

# Removes the run folder only when it is exactly the one this run made: under
# the given root, named for the run. Kept (and said so) when any case did not
# pass, because the files are then the evidence somebody needs.
function Remove-HorizunIsoRunDirectory {
    param([string]$Path, [string]$Root, [string]$RunId)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return $false }
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $full) -ne ('iso19650-' + $RunId)) {
        throw "HARNESS: refusing to delete '$full': it is not this run's ISO folder under $rootFull."
    }
    Remove-Item -LiteralPath $full -Recurse -Force
    return $true
}

function Get-HorizunIsoFileStamp {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    $sha = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return ('{0}|{1}|{2}' -f $sha, $item.Length, $item.LastWriteTimeUtc.Ticks)
}

# Every file under a folder with its size and write time. Two equal snapshots
# mean nothing under the folder was created, changed or removed.
function Get-HorizunIsoFolderSnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $base = [IO.Path]::GetFullPath($Path)
    $lines = @(Get-ChildItem -LiteralPath $base -Recurse -Force -File -ErrorAction SilentlyContinue |
        Sort-Object FullName | ForEach-Object {
            '{0}|{1}|{2}' -f $_.FullName.Substring($base.Length), $_.Length, $_.LastWriteTimeUtc.Ticks })
    return ($lines -join "`n")
}

# -----------------------------------------------------------------------------
# Pure helpers: the plan per year, the container, the generated inputs.
# -----------------------------------------------------------------------------

# IFC4 for the delivery in every year: IfcDeliveryRules.VersionSchemaFamily maps it
# to FILE_SCHEMA family IFC4, which is also the IDS ifcVersion, and every IFCVersion
# enum from 2023 to 2027 has it. IFC4x3 is probed separately, per year.
function Get-HorizunIsoIfcPlan {
    param([Parameter(Mandatory = $true)][int]$Year)
    return [pscustomobject]@{
        version = 'IFC4'
        schema_family = 'IFC4'
        ids_version = 'IFC4'
        ifc4x3_available = ($Year -ge 2024)
    }
}

function Get-HorizunIsoClassCandidates {
    # In preference order. The IFC4 class each category exports as, the IDS
    # entity names that class may carry in the file, and nothing else.
    return @(
        [pscustomobject]@{ category = 'OST_DuctCurves'; ifc_class = 'IfcDuctSegment'; ids_entities = @('IFCDUCTSEGMENT') }
        [pscustomobject]@{ category = 'OST_PipeCurves'; ifc_class = 'IfcPipeSegment'; ids_entities = @('IFCPIPESEGMENT') }
        [pscustomobject]@{ category = 'OST_CableTray'; ifc_class = 'IfcCableCarrierSegment'; ids_entities = @('IFCCABLECARRIERSEGMENT') }
        [pscustomobject]@{ category = 'OST_Floors'; ifc_class = 'IfcSlab'; ids_entities = @('IFCSLAB') }
        [pscustomobject]@{ category = 'OST_StructuralColumns'; ifc_class = 'IfcColumn'; ids_entities = @('IFCCOLUMN') }
        [pscustomobject]@{ category = 'OST_StructuralFraming'; ifc_class = 'IfcBeam'; ids_entities = @('IFCBEAM') }
        [pscustomobject]@{ category = 'OST_Walls'; ifc_class = 'IfcWall'; ids_entities = @('IFCWALL', 'IFCWALLSTANDARDCASE') }
        [pscustomobject]@{ category = 'OST_GenericModel'; ifc_class = 'IfcBuildingElementProxy'; ids_entities = @('IFCBUILDINGELEMENTPROXY') }
    )
}

# Ask the MODEL which candidate category holds elements. The preferred category
# (the run's -QuantityCategory) goes first when it is a known candidate. Nothing
# is chosen that the model did not count.
function Find-HorizunIsoDeliveryClass {
    param([scriptblock]$Call, [string]$PreferredCategory)
    $candidates = @(Get-HorizunIsoClassCandidates)
    $ordered = @()
    if ($PreferredCategory) { $ordered += @($candidates | Where-Object { $_.category -eq $PreferredCategory }) }
    $ordered += @($candidates | Where-Object { $_.category -ne $PreferredCategory })
    $tried = @()
    foreach ($candidate in $ordered) {
        $answer = & $Call 'horizun_query_model' @{
            categories = @($candidate.category); include_links = $false; response_mode = 'summary'; max_rows = 1 }
        if ($answer.isError -or -not $answer.data) {
            $tried += ('{0}: query failed ({1})' -f $candidate.category, (Limit-HorizunIsoText $answer.text 160))
            continue
        }
        $count = 0
        if ($null -ne $answer.data.matched_total) { $count = [int]$answer.data.matched_total }
        $tried += ('{0}={1}' -f $candidate.category, $count)
        if ($count -gt 0) {
            return [pscustomobject]@{
                found = $true; category = $candidate.category; ifc_class = $candidate.ifc_class
                ids_entities = @($candidate.ids_entities); count = $count
                coverage_complete = $answer.data.coverage_complete; tried = $tried
            }
        }
    }
    return [pscustomobject]@{ found = $false; tried = $tried }
}

# The mapping is the EXPORTER'S OWN FORMAT - TAB separated - and the IDS asks for
# exactly the property the mapping declares, on exactly the class the model has.
function New-HorizunIsoDeliveryInputs {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)]$Class,
          [Parameter(Mandatory = $true)][string]$IdsVersion, [string]$Parameter = 'Comments',
          [string]$PropertySet = 'HZ_Delivery', [string]$Property = 'Code')
    $null = New-Item -ItemType Directory -Force -Path $Directory
    $tab = "`t"
    $mappingText = ('# Horizun live probe - generated per run; fields separated by TAB' + "`r`n" +
                    'PropertySet:' + $tab + $PropertySet + $tab + 'I' + $tab + $Class.ifc_class + "`r`n" +
                    $tab + $Property + $tab + 'Text' + $tab + $Parameter + "`r`n")
    $entities = @($Class.ids_entities)
    if ($entities.Count -eq 1) {
        $entityXml = '<name><simpleValue>' + $entities[0] + '</simpleValue></name>'
    } else {
        $entityXml = '<name><xs:restriction base="xs:string">' +
            (($entities | ForEach-Object { '<xs:enumeration value="' + $_ + '"/>' }) -join '') +
            '</xs:restriction></name>'
    }
    $idsText = '<?xml version="1.0" encoding="UTF-8"?>' + "`r`n" +
        '<ids xmlns="http://standards.buildingsmart.org/IDS" xmlns:xs="http://www.w3.org/2001/XMLSchema">' +
        '<info><title>Horizun live delivery probe</title></info><specifications>' +
        '<specification name="' + $Class.ifc_class + ' carries ' + $PropertySet + '.' + $Property + '" ifcVersion="' + $IdsVersion + '">' +
        '<applicability><entity>' + $entityXml + '</entity></applicability>' +
        '<requirements><property><propertySet><simpleValue>' + $PropertySet + '</simpleValue></propertySet>' +
        '<baseName><simpleValue>' + $Property + '</simpleValue></baseName></property></requirements>' +
        '</specification></specifications></ids>'
    $mappingPath = Join-Path $Directory 'delivery-psets.txt'
    $idsPath = Join-Path $Directory 'delivery.ids'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($mappingPath, $mappingText, $utf8)
    [IO.File]::WriteAllText($idsPath, $idsText, $utf8)
    return [pscustomobject]@{
        mapping = $mappingPath; ids = $idsPath
        mapping_sha256 = (Get-FileHash -LiteralPath $mappingPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ids_sha256 = (Get-FileHash -LiteralPath $idsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        property_set = $PropertySet; property = $Property; parameter = $Parameter
    }
}

# The seven ISO 19650-2 fields with their default patterns, so the name is
# checked by the SAME defaults the bridge ships.
function New-HorizunIsoContainer {
    param([Parameter(Mandatory = $true)][string]$Number, [string]$Type = 'M3', [string]$Status = 'S2',
          [string]$Revision = 'P01', [string]$Title = 'Horizun live probe')
    return @{
        fields = @{ project = 'HZ01'; originator = 'HRZ'; volume = 'ZZ'; level = 'XX'; type = $Type; role = 'A'; number = $Number }
        field_order = @('project', 'originator', 'volume', 'level', 'type', 'role', 'number')
        separator = '-'
        status = $Status
        revision = $Revision
        title = $Title
    }
}

function Get-HorizunIsoContainerName {
    param([Parameter(Mandatory = $true)]$Container)
    $parts = @()
    foreach ($field in $Container.field_order) { $parts += [string]$Container.fields[$field] }
    return ($parts -join $Container.separator)
}

function Copy-HorizunIsoArgs {
    param([hashtable]$Arguments, [hashtable]$Set)
    $copy = @{}
    foreach ($key in $Arguments.Keys) { $copy[$key] = $Arguments[$key] }
    if ($Set) { foreach ($key in $Set.Keys) { $copy[$key] = $Set[$key] } }
    return $copy
}

function Get-HorizunIsoGates {
    param($Data)
    $byName = @{}
    if ($null -eq $Data -or $null -eq $Data.gates) { return $byName }
    foreach ($gate in @($Data.gates)) {
        if ($gate -and $gate.gate) { $byName[[string]$gate.gate] = $gate }
    }
    return $byName
}

function Get-HorizunIsoGateStatus {
    param([hashtable]$Gates, [string]$Name)
    if ($Gates.ContainsKey($Name)) { return [string]$Gates[$Name].status }
    return '(absent)'
}

# The ONE readiness rule, recomputed from the gates the reply carried. The
# probe does not trust deliverable_ready: it checks it against this.
function Test-HorizunIsoReadinessFollowsGates {
    param($Data)
    $gates = @($Data.gates)
    $expected = $true
    foreach ($mandatory in @('export', 'schema_header')) {
        $g = $gates | Where-Object { $_.gate -eq $mandatory } | Select-Object -First 1
        if (-not $g -or [string]$g.status -ne 'passed') { $expected = $false }
    }
    foreach ($g in $gates) {
        if ($g.requested -ne $true -or $g.advisory -eq $true) { continue }
        if ([string]$g.status -ne 'passed' -and [string]$g.status -ne 'skipped') { $expected = $false }
    }
    $blocking = @($Data.blocking)
    $consistent = ([bool]$Data.deliverable_ready -eq $expected)
    if ($Data.deliverable_ready -ne $true -and $blocking.Count -eq 0) { $consistent = $false }
    if ($Data.deliverable_ready -eq $true -and $blocking.Count -ne 0) { $consistent = $false }
    return [pscustomobject]@{ expected = $expected; consistent = $consistent }
}

function Invoke-HorizunIsoVerify {
    param([scriptblock]$Call, [string]$FilePath)
    $verify = & $Call 'horizun_information_container' @{ operation = 'verify'; file_path = $FilePath }
    if ($verify.isError -or -not $verify.data) {
        return [pscustomobject]@{ ok = $false; verdict = $null; sha256 = $null; text = $verify.text }
    }
    return [pscustomobject]@{
        ok = ([string]$verify.data.verdict -eq 'match' -and $verify.data.matches -eq $true)
        verdict = [string]$verify.data.verdict; sha256 = [string]$verify.data.sha256; text = $null
    }
}

# -----------------------------------------------------------------------------
# 1. horizun_deliver_ifc - the write tier, disposable document.
# -----------------------------------------------------------------------------
function Invoke-HorizunIsoDeliveryProbes {
    param([string]$Document, [int]$Year, [string]$RunDirectory, [string]$RunId, [string]$PreferredCategory,
          [scriptblock]$Call, [scriptblock]$Apply, $Recorder, [switch]$ReadyWrite, [string]$MappedParameter = 'Comments')
    $plan = Get-HorizunIsoIfcPlan -Year $Year
    $class = Find-HorizunIsoDeliveryClass -Call $Call -PreferredCategory $PreferredCategory
    $deliveryIds = @('ISO-D1', 'ISO-D2', 'ISO-D3', 'ISO-D4', 'ISO-D6')
    if (-not $class.found) {
        foreach ($id in $deliveryIds) {
            if ($Recorder.Catalog | Where-Object { $_.Id -eq $id }) {
                Complete-HorizunIsoCase $Recorder $id 'not_covered' ('the disposable model has no element in any candidate category, so no delivery class could be discovered: ' + ($class.tried -join ', ')) $null
            }
        }
    }
    $outDir = Join-Path $RunDirectory 'deliver'
    $null = New-Item -ItemType Directory -Force -Path $outDir

    if ($class.found) {
        $inputs = New-HorizunIsoDeliveryInputs -Directory (Join-Path $RunDirectory 'inputs') -Class $class `
            -IdsVersion $plan.ids_version -Parameter $MappedParameter
        $container = New-HorizunIsoContainer -Number '0001' -Type 'M3'
        $name = Get-HorizunIsoContainerName $container
        $ifcPath = Join-Path $outDir ($name + '.ifc')
        $sidecarPath = $ifcPath + '.container.json'
        $discovered = @{ category = $class.category; ifc_class = $class.ifc_class; elements = $class.count
                         tried = $class.tried; ifc_version = $plan.version; mapping_sha256 = $inputs.mapping_sha256
                         ids_sha256 = $inputs.ids_sha256 }
        $base = @{
            target_document = $Document; output_folder = $outDir; information_container = $container
            ifc_version = $plan.version; pset_mapping_path = $inputs.mapping; ids_path = $inputs.ids; bcf = $true
        }

        # ---- D1: the rehearsal -------------------------------------------------
        $before = Get-HorizunIsoFolderSnapshot $outDir
        $dry = & $Call 'horizun_deliver_ifc' (Copy-HorizunIsoArgs $base @{ dry_run = $true })
        $after = Get-HorizunIsoFolderSnapshot $outDir
        $d = $dry.data
        $problems = @()
        if ($dry.isError -or -not $d) { $problems += ('the rehearsal failed: ' + (Limit-HorizunIsoText $dry.text 300)) }
        else {
            if ($d.dry_run -ne $true) { $problems += 'dry_run is not true' }
            if ([string]::IsNullOrWhiteSpace([string]$d.confirmation_token)) { $problems += 'no confirmation_token' }
            if (-not [string]::Equals([string]$d.ifc_path, $ifcPath, [StringComparison]::OrdinalIgnoreCase)) {
                $problems += ('ifc_path is ' + [string]$d.ifc_path + ', expected the container name in the output folder') }
            if ([string]$d.information_container_name -ne $name) { $problems += ('information_container_name is ' + [string]$d.information_container_name) }
            $geo = $d.georeference_in_model
            if (-not $geo -or $null -eq $geo.survey_point -or $null -eq $geo.project_base_point -or $null -eq $geo.active_location) {
                $problems += 'georeference_in_model lacks the survey point, the project base point or the active location' }
            $plannedSeal = @($d.gates_planned) | Where-Object { $_.gate -eq 'information_container' } | Select-Object -First 1
            if (-not $plannedSeal -or $plannedSeal.requested -ne $true) { $problems += 'the information_container gate is not planned' }
            if (-not $d.pset_mapping -or [string]$d.pset_mapping.sha256 -ne $inputs.mapping_sha256) { $problems += 'the plan does not bind the mapping by its SHA-256' }
            if (-not $d.ids -or [string]$d.ids.sha256 -ne $inputs.ids_sha256) { $problems += 'the plan does not bind the IDS by its SHA-256' }
        }
        if ($before -ne $after) { $problems += 'the rehearsal changed the output folder' }
        if ($problems.Count -eq 0) {
            Complete-HorizunIsoCase $Recorder 'ISO-D1' 'pass' ('rehearsal of ' + $name + '.ifc (' + $plan.version + ', class ' + $class.ifc_class +
                ' discovered from ' + $class.count + ' ' + $class.category + ') returned the plan, the model georeference and a token; the output folder is unchanged') `
                @{ discovered = $discovered; georeference_in_model = $d.georeference_in_model; gates_planned = $d.gates_planned }
        } elseif ($dry.isError -or -not $d) {
            Complete-HorizunIsoCase $Recorder 'ISO-D1' 'unverified' ($problems -join '; ') @{ discovered = $discovered }
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-D1' 'fail' ($problems -join '; ') @{ discovered = $discovered }
        }

        # ---- D2/D3: the apply --------------------------------------------------
        $applied = & $Apply 'horizun_deliver_ifc' $base 'iso-deliver'
        $sealed = $false
        if ($applied.stage -ne 'apply') {
            $why = 'the rehearsal did not issue a token, so the delivery was never applied: ' + (Limit-HorizunIsoText $applied.answer.text 300)
            Complete-HorizunIsoCase $Recorder 'ISO-D2' 'unverified' $why $null
            Complete-HorizunIsoCase $Recorder 'ISO-D3' 'unverified' $why $null
        } elseif ($applied.answer.isError -or -not $applied.answer.data) {
            Complete-HorizunIsoCase $Recorder 'ISO-D2' 'fail' ('the delivery apply failed: ' + (Limit-HorizunIsoText $applied.answer.text 400)) $null
            Complete-HorizunIsoCase $Recorder 'ISO-D3' 'unverified' 'the delivery apply failed, so no gate could be judged' $null
        } else {
            $a = $applied.answer.data
            $gates = Get-HorizunIsoGates $a
            $export = Get-HorizunIsoGateStatus $gates 'export'
            $header = Get-HorizunIsoGateStatus $gates 'schema_header'
            $seal = Get-HorizunIsoGateStatus $gates 'information_container'
            $verify = Invoke-HorizunIsoVerify -Call $Call -FilePath $ifcPath
            $problems = @()
            if ($export -ne 'passed') { $problems += "export=$export" }
            if ($header -ne 'passed') { $problems += "schema_header=$header" }
            if ($seal -ne 'passed') { $problems += "information_container=$seal" }
            if (-not (Test-Path -LiteralPath $ifcPath -PathType Leaf)) { $problems += 'no IFC at the planned path' }
            if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) { $problems += 'no sidecar beside the IFC' }
            if (-not $verify.ok) { $problems += ('information_container verify: ' + $(if ($verify.verdict) { $verify.verdict } else { Limit-HorizunIsoText $verify.text 200 })) }
            elseif ($verify.sha256 -ne [string]$a.sha256) { $problems += 'the verified SHA-256 is not the one the delivery reported' }
            $schemaFamily = $null
            if ($gates.ContainsKey('schema_header') -and $gates['schema_header'].evidence) { $schemaFamily = [string]$gates['schema_header'].evidence.file_schema_family }
            if ($header -eq 'passed' -and $schemaFamily -ne $plan.schema_family) { $problems += ('FILE_SCHEMA family is ' + $schemaFamily + ', expected ' + $plan.schema_family) }
            $evidence = @{ discovered = $discovered; sha256 = $a.sha256; bytes = $a.bytes; gates = $a.gates
                           verify = $verify.verdict; georeference_in_file = $a.georeference_in_file }
            if ($problems.Count -eq 0) {
                $sealed = $true
                Complete-HorizunIsoCase $Recorder 'ISO-D2' 'pass' ('export, schema_header (' + $schemaFamily + ') and information_container passed; ' +
                    'horizun_information_container verify reads match with the delivered SHA-256') $evidence
            } else {
                Complete-HorizunIsoCase $Recorder 'ISO-D2' 'fail' ($problems -join '; ') $evidence
            }

            $ids = Get-HorizunIsoGateStatus $gates 'ids_validate'
            $pset = Get-HorizunIsoGateStatus $gates 'pset_mapping'
            $bcf = Get-HorizunIsoGateStatus $gates 'bcf'
            $ready = Test-HorizunIsoReadinessFollowsGates $a
            $problems = @()
            if ($ids -ne 'passed' -and $ids -ne 'failed') { $problems += "ids_validate=$ids (not decided on the file)" }
            if ($pset -ne 'passed' -and $pset -ne 'failed') { $problems += "pset_mapping=$pset (not decided on the file)" }
            if ($ids -eq 'failed' -and $bcf -ne 'passed') { $problems += "the IDS failed but bcf=$bcf" }
            if ($ids -eq 'passed' -and $bcf -ne 'skipped') { $problems += "the IDS passed but bcf=$bcf" }
            if (-not $ready.consistent) { $problems += ('deliverable_ready=' + $a.deliverable_ready + ' does not follow the gates (expected ' + $ready.expected + ') or blocking disagrees') }
            if ($null -eq $a.georeference_in_file) { $problems += 'georeference_in_file is missing' }
            if ($problems.Count -eq 0) {
                Complete-HorizunIsoCase $Recorder 'ISO-D3' 'pass' ('ids_validate=' + $ids + ', pset_mapping=' + $pset + ', bcf=' + $bcf +
                    '; deliverable_ready=' + $a.deliverable_ready + ' is what the gates imply (blocking: ' + (@($a.blocking) -join '; ') + ')') `
                    @{ gates = $a.gates; blocking = $a.blocking; deliverable_ready = $a.deliverable_ready }
            } else {
                Complete-HorizunIsoCase $Recorder 'ISO-D3' 'fail' ($problems -join '; ') @{ gates = $a.gates; blocking = $a.blocking }
            }
        }

        # ---- D4: the seal holds ------------------------------------------------
        if (-not $sealed) {
            Complete-HorizunIsoCase $Recorder 'ISO-D4' 'unverified' 'no sealed delivery exists to be refused (ISO-D2 did not pass)' $null
        } else {
            $ifcBefore = Get-HorizunIsoFileStamp $ifcPath
            $sidecarBefore = Get-HorizunIsoFileStamp $sidecarPath
            $folderBefore = Get-HorizunIsoFolderSnapshot $outDir
            $again = & $Call 'horizun_deliver_ifc' (Copy-HorizunIsoArgs $base @{
                dry_run = $false; overwrite = $true; idempotency_key = ('live-iso-reseal-' + $RunId) })
            $unchanged = ((Get-HorizunIsoFileStamp $ifcPath) -eq $ifcBefore -and (Get-HorizunIsoFileStamp $sidecarPath) -eq $sidecarBefore -and
                          (Get-HorizunIsoFolderSnapshot $outDir) -eq $folderBefore)
            if ($again.isError -and $again.text -match 'sidecar' -and $again.text -match 'Nothing was exported' -and $unchanged) {
                Complete-HorizunIsoCase $Recorder 'ISO-D4' 'pass' ('an apply with overwrite=true onto the sealed name was refused before export (' +
                    (Limit-HorizunIsoText $again.text 200) + '); the IFC, its sidecar and the folder are byte-for-byte unchanged') $null
            } elseif (-not $again.isError) {
                Complete-HorizunIsoCase $Recorder 'ISO-D4' 'fail' 'the second delivery onto the sealed name was ACCEPTED' $null
            } else {
                Complete-HorizunIsoCase $Recorder 'ISO-D4' 'fail' ('refused=' + $again.isError + ' unchanged=' + $unchanged + ': ' + (Limit-HorizunIsoText $again.text 300)) $null
            }
        }

        # ---- D6 (opt-in): commit the code, and the same delivery becomes ready --
        if ($ReadyWrite) {
            Invoke-HorizunIsoReadyProbe -Document $Document -Class $class -Inputs $inputs -Plan $plan -OutDir $outDir `
                -RunId $RunId -Call $Call -Apply $Apply -Recorder $Recorder
        }
    }

    # ---- D5: IFC4x3 per year (independent of the discovered class) ------------
    $x3Name = 'HZ_ISO_4X3_' + $RunId
    $x3Path = Join-Path $outDir ($x3Name + '.ifc')
    $x3 = & $Call 'horizun_deliver_ifc' @{ target_document = $Document; output_folder = $outDir; output_name = $x3Name
                                            ifc_version = 'IFC4x3'; dry_run = $true }
    $x3Created = Test-Path -LiteralPath $x3Path
    if ($plan.ifc4x3_available) {
        $row = $null
        if ($x3.data) { $row = @($x3.data.options) | Where-Object { $_.option -eq 'FileVersion' } | Select-Object -First 1 }
        if (-not $x3.isError -and $x3.data.dry_run -eq $true -and $x3.data.confirmation_token -and $row -and [string]$row.value -eq 'IFC4x3' -and -not $x3Created) {
            Complete-HorizunIsoCase $Recorder 'ISO-D5' 'pass' ("Revit $Year planned an IFC4x3 delivery (FileVersion IFC4x3, token issued) and wrote nothing") $null
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-D5' 'fail' ("Revit $Year has IFCVersion.IFC4x3 but the rehearsal did not plan it: " + (Limit-HorizunIsoText $x3.text 300)) $null
        }
    } else {
        if ($x3.isError -and $x3.text -match 'IFCVersion\.IFC4x3' -and -not $x3Created) {
            Complete-HorizunIsoCase $Recorder 'ISO-D5' 'pass' ("Revit $Year has no IFCVersion.IFC4x3 and the delivery was refused by name before anything was exported") $null
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-D5' 'fail' ("Revit $Year predates IFC4x3 but the request was not refused by name: " + (Limit-HorizunIsoText $x3.text 300)) $null
        }
    }
}

# Opt-in, because it COMMITS: the mapped parameter on every element of the class
# in the disposable model, then the same delivery under a new container number.
function Invoke-HorizunIsoReadyProbe {
    param([string]$Document, $Class, $Inputs, $Plan, [string]$OutDir, [string]$RunId,
          [scriptblock]$Call, [scriptblock]$Apply, $Recorder)
    $rows = & $Call 'horizun_query_model' @{ categories = @($Class.category); include_links = $false; max_rows = 5000 }
    if ($rows.isError -or -not $rows.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'unverified' ('the elements of the class could not be listed: ' + (Limit-HorizunIsoText $rows.text 200)) $null
        return
    }
    if ($rows.data.truncated -eq $true -or $rows.data.coverage_complete -eq $false) {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'unverified' ('the element list of ' + $Class.category + ' is truncated or incomplete; a partial write cannot prove readiness') $null
        return
    }
    $ids = @(@($rows.data.rows) | Where-Object { $_.is_element_type -ne $true -and $null -ne $_.element_id } | ForEach-Object { [long]$_.element_id })
    if ($ids.Count -eq 0) {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'unverified' 'the listed rows carried no element id' $null
        return
    }
    $code = 'HZ-' + $RunId
    $writes = @($ids | ForEach-Object { @{ target_id = $_; parameter = 'ALL_MODEL_INSTANCE_COMMENTS'; value = $code } })
    $written = & $Apply 'horizun_write_params_verified' @{ target_document = $Document; writes = $writes
                                                         transaction_name = 'Horizun live: ISO delivery code' } 'iso-ready-write'
    if ($written.stage -ne 'apply' -or $written.answer.isError -or [int]$written.answer.data.writes_confirmed -ne $ids.Count) {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'unverified' ('the code could not be written and re-read on all ' + $ids.Count + ' elements: ' +
            (Limit-HorizunIsoText $written.answer.text 300)) $null
        return
    }
    $container = New-HorizunIsoContainer -Number '0004' -Type 'M3'
    $again = & $Apply 'horizun_deliver_ifc' @{
        target_document = $Document; output_folder = $OutDir; information_container = $container
        ifc_version = $Plan.version; pset_mapping_path = $Inputs.mapping; ids_path = $Inputs.ids; bcf = $true } 'iso-deliver-ready'
    if ($again.stage -ne 'apply' -or $again.answer.isError -or -not $again.answer.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'fail' ('the delivery after the write failed: ' + (Limit-HorizunIsoText $again.answer.text 300)) $null
        return
    }
    $gates = Get-HorizunIsoGates $again.answer.data
    $states = 'ids_validate={0} pset_mapping={1} information_container={2}' -f (Get-HorizunIsoGateStatus $gates 'ids_validate'),
        (Get-HorizunIsoGateStatus $gates 'pset_mapping'), (Get-HorizunIsoGateStatus $gates 'information_container')
    if ($again.answer.data.deliverable_ready -eq $true -and (Get-HorizunIsoGateStatus $gates 'ids_validate') -eq 'passed' -and
        (Get-HorizunIsoGateStatus $gates 'pset_mapping') -eq 'passed' -and (Get-HorizunIsoGateStatus $gates 'information_container') -eq 'passed') {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'pass' ('after a verified write on ' + $ids.Count + ' ' + $Class.category + ', deliverable_ready=true; ' + $states) `
            @{ gates = $again.answer.data.gates }
    } else {
        Complete-HorizunIsoCase $Recorder 'ISO-D6' 'fail' ('deliverable_ready=' + $again.answer.data.deliverable_ready + '; ' + $states + '; blocking: ' +
            (@($again.answer.data.blocking) -join '; ')) @{ gates = $again.answer.data.gates }
    }
}

# -----------------------------------------------------------------------------
# 2. horizun_export with an information container.
# -----------------------------------------------------------------------------
function New-HorizunIsoCde {
    param([Parameter(Mandatory = $true)][string]$Root)
    foreach ($state in @('wip', 'shared', 'published', 'archived')) {
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $Root $state)
    }
    return @{ wip = 'wip'; shared = 'shared'; published = 'published'; archived = 'archived' }
}

function Invoke-HorizunIsoExportProbes {
    param([string]$Document, [string]$RunDirectory, [string]$RunId, [scriptblock]$Call, [scriptblock]$Apply, $Recorder)
    $cdeRoot = Join-Path $RunDirectory 'cde-export'
    $states = New-HorizunIsoCde $cdeRoot
    $shared = Join-Path $cdeRoot 'shared'
    $container = New-HorizunIsoContainer -Number '0002' -Type 'M3'
    $name = Get-HorizunIsoContainerName $container
    $requested = Join-Path $shared 'requested-name.ifc'
    $expected = Join-Path $shared ($name + '.ifc')
    $request = @{ target_document = $Document; format = 'ifc'; ifc_version = 'IFC4'; output_path = $requested
                  information_container = $container }
    $sealed = $null

    # ---- E1 ---------------------------------------------------------------------
    $r = & $Apply 'horizun_export' $request 'iso-export-ifc'
    if ($r.stage -ne 'apply') {
        Complete-HorizunIsoCase $Recorder 'ISO-E1' 'unverified' ('the export rehearsal issued no token: ' + (Limit-HorizunIsoText $r.answer.text 300)) $null
    } elseif ($r.answer.isError -or -not $r.answer.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-E1' 'fail' ('the export with a container failed: ' + (Limit-HorizunIsoText $r.answer.text 300)) $null
    } else {
        $ic = $r.answer.data.information_container
        $verify = Invoke-HorizunIsoVerify -Call $Call -FilePath $expected
        $problems = @()
        if (-not $ic) { $problems += 'the reply carries no information_container block' }
        else {
            if ([string]$ic.name -ne $name) { $problems += ('container name is ' + [string]$ic.name) }
            if (-not [string]::Equals([string]$ic.file, $expected, [StringComparison]::OrdinalIgnoreCase)) { $problems += ('file is ' + [string]$ic.file + ', expected the container name beside output_path') }
        }
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) { $problems += 'no file carries the container name' }
        if (Test-Path -LiteralPath $requested) { $problems += 'a file was also written under the requested name' }
        if (-not (Test-Path -LiteralPath ($expected + '.container.json') -PathType Leaf)) { $problems += 'no sidecar beside the file' }
        if (-not $verify.ok) { $problems += ('verify: ' + $(if ($verify.verdict) { $verify.verdict } else { Limit-HorizunIsoText $verify.text 200 })) }
        if ($problems.Count -eq 0) {
            $sealed = $expected
            Complete-HorizunIsoCase $Recorder 'ISO-E1' 'pass' ('the IFC took the container name ' + $name + ' (not the requested one), was sealed, and verify reads match') `
                @{ name = $name; verify = $verify.verdict; sha256 = $verify.sha256 }
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-E1' 'fail' ($problems -join '; ') $null
        }
    }

    # ---- E2 ---------------------------------------------------------------------
    if (-not $sealed) {
        Complete-HorizunIsoCase $Recorder 'ISO-E2' 'unverified' 'no sealed export exists to be refused (ISO-E1 did not pass)' $null
    } else {
        $fileBefore = Get-HorizunIsoFileStamp $sealed
        $folderBefore = Get-HorizunIsoFolderSnapshot $shared
        $again = & $Call 'horizun_export' (Copy-HorizunIsoArgs $request @{ overwrite = $true; dry_run = $false
                                                                          idempotency_key = ('live-iso-export-reseal-' + $RunId) })
        $unchanged = ((Get-HorizunIsoFileStamp $sealed) -eq $fileBefore -and (Get-HorizunIsoFolderSnapshot $shared) -eq $folderBefore)
        if ($again.isError -and $again.text -match 'sidecar' -and $again.text -match 'never overwritten' -and
            $again.text -match 'Nothing was exported' -and $unchanged) {
            Complete-HorizunIsoCase $Recorder 'ISO-E2' 'pass' ('overwrite=true onto the sealed name was refused before export; the file and the folder are unchanged') $null
        } elseif (-not $again.isError) {
            Complete-HorizunIsoCase $Recorder 'ISO-E2' 'fail' 'a second export onto the sealed name was ACCEPTED' $null
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-E2' 'fail' ('refused=' + $again.isError + ' unchanged=' + $unchanged + ': ' + (Limit-HorizunIsoText $again.text 300)) $null
        }
    }

    # ---- E3: a PDF, only if the model has a real sheet ---------------------------
    $sheets = & $Call 'horizun_query_planimetry' @{ mode = 'sheets'; units = 'mm' }
    if ($sheets.isError -or -not $sheets.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-E3' 'unverified' ('the sheets could not be listed: ' + (Limit-HorizunIsoText $sheets.text 200)) $null
    } else {
        $sheet = @($sheets.data.rows) | Where-Object { $_.placeholder -eq $false -and $null -ne $_.sheet_id } | Select-Object -First 1
        if (-not $sheet) {
            Complete-HorizunIsoCase $Recorder 'ISO-E3' 'not_covered' 'the disposable model has no non-placeholder sheet to print' $null
        } else {
            $pdfContainer = New-HorizunIsoContainer -Number '0003' -Type 'DR'
            $pdfName = Get-HorizunIsoContainerName $pdfContainer
            $pdfExpected = Join-Path $shared ($pdfName + '.pdf')
            $p = & $Apply 'horizun_export' @{ target_document = $Document; format = 'pdf'; view_ids = @([long]$sheet.sheet_id)
                                               output_path = (Join-Path $shared 'requested-sheet.pdf'); information_container = $pdfContainer } 'iso-export-pdf'
            if ($p.stage -ne 'apply') {
                Complete-HorizunIsoCase $Recorder 'ISO-E3' 'unverified' ('the PDF rehearsal issued no token: ' + (Limit-HorizunIsoText $p.answer.text 300)) $null
            } elseif ($p.answer.isError -or -not $p.answer.data -or -not $p.answer.data.information_container) {
                Complete-HorizunIsoCase $Recorder 'ISO-E3' 'fail' ('the PDF export with a container failed: ' + (Limit-HorizunIsoText $p.answer.text 300)) $null
            } else {
                $verify = Invoke-HorizunIsoVerify -Call $Call -FilePath $pdfExpected
                if ((Test-Path -LiteralPath $pdfExpected -PathType Leaf) -and $verify.ok -and
                    [string]$p.answer.data.information_container.name -eq $pdfName) {
                    Complete-HorizunIsoCase $Recorder 'ISO-E3' 'pass' ('sheet ' + [string]$sheet.sheet_number + ' printed as one combined PDF named ' + $pdfName + ' and sealed (verify = match)') `
                        @{ sheet_id = $sheet.sheet_id; verify = $verify.verdict }
                } else {
                    Complete-HorizunIsoCase $Recorder 'ISO-E3' 'fail' ('file present=' + (Test-Path -LiteralPath $pdfExpected) + ' verify=' + $verify.verdict) $null
                }
            }
        }
    }
    return [pscustomobject]@{ cde_root = $cdeRoot; states = $states; sealed_source = $sealed }
}

# -----------------------------------------------------------------------------
# 3. Host-resident: project context and the CDE. No Revit is needed for any of
#    these; they run in every verify-live run, write tier or not.
# -----------------------------------------------------------------------------
function Test-HorizunIsoProfileAllowsExternalWrite {
    param([string]$PermissionProfile)
    if ([string]::IsNullOrWhiteSpace($PermissionProfile)) { return $null }
    return ($PermissionProfile -eq 'full_write' -or $PermissionProfile -eq 'unsafe_code')
}

function Invoke-HorizunIsoHostProbes {
    param([string]$RunDirectory, [string]$RunId, [scriptblock]$Call, $Recorder, [string]$PermissionProfile,
          [string]$SealedSource, [string]$SealedCdeRoot)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $ctxDir = Join-Path $RunDirectory 'context'
    $null = New-Item -ItemType Directory -Force -Path $ctxDir
    $invalid = Join-Path $ctxDir 'invalid.json'
    $inconsistent = Join-Path $ctxDir 'inconsistent.json'
    $incomplete = Join-Path $ctxDir 'incomplete.json'
    [IO.File]::WriteAllText($invalid, '{"schema_version":1,"project":{}}', $utf8)
    [IO.File]::WriteAllText($inconsistent, ('{"schema_version":1,"project":{"code":"HZ01"},"naming":{"status_codes":{"S2":"shared"}},' +
        '"deliverables":[{"container":"HZ01-HRZ-ZZ-XX-M3-A-0001","required_status":"A1"}]}'), $utf8)
    [IO.File]::WriteAllText($incomplete, '{"schema_version":1,"project":{"code":"HZ01"}}', $utf8)

    # ---- H1 -----------------------------------------------------------------
    $states = @{}
    $answers = @{}
    foreach ($pair in @(@('invalid', $invalid), @('inconsistent', $inconsistent), @('incomplete', $incomplete))) {
        $v = & $Call 'horizun_project_context' @{ operation = 'validate'; path = $pair[1] }
        $answers[$pair[0]] = $v
        $states[$pair[0]] = $(if ($v.isError -or -not $v.data) { '(error)' } else { [string]$v.data.state })
    }
    $problems = @()
    if ($states.invalid -ne 'invalid' -or @($answers.invalid.data.errors).Count -eq 0) { $problems += ('invalid file read as ' + $states.invalid) }
    $rules = @()
    if ($answers.inconsistent.data) { $rules = @(@($answers.inconsistent.data.coherence) | ForEach-Object { [string]$_.rule }) }
    if ($states.inconsistent -ne 'inconsistent' -or $rules -notcontains 'unknown_status_code') { $problems += ('inconsistent file read as ' + $states.inconsistent + ' (rules: ' + ($rules -join ',') + ')') }
    if ($states.incomplete -ne 'incomplete' -or @($answers.incomplete.data.missing).Count -eq 0) { $problems += ('incomplete file read as ' + $states.incomplete) }
    $h1Outcome = 'pass'
    if (@($answers.Values | Where-Object { $_.isError -or -not $_.data }).Count -gt 0) { $h1Outcome = 'unverified' }
    elseif ($problems.Count -gt 0) { $h1Outcome = 'fail' }
    Complete-HorizunIsoCase $Recorder 'ISO-H1' $h1Outcome $(if ($problems.Count -eq 0) { 'three files, three verdicts: invalid (schema errors), inconsistent (unknown_status_code), incomplete (open intake questions)' } else { $problems -join '; ' }) $states

    # ---- H2 -----------------------------------------------------------------
    $all = & $Call 'horizun_project_context' @{ operation = 'questions' }
    $partial = & $Call 'horizun_project_context' @{ operation = 'questions'; path = $incomplete }
    if ($all.isError -or -not $all.data -or $partial.isError -or -not $partial.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-H2' 'unverified' ('questions failed: ' + (Limit-HorizunIsoText ($all.text + ' ' + $partial.text) 300)) $null
    } else {
        $qs = @($all.data.questions)
        $problems = @()
        # Without include_answered the list holds the PENDING questions only.
        if ($qs.Count -eq 0 -or [int]$all.data.pending -ne $qs.Count) { $problems += ('pending=' + $all.data.pending + ' but ' + $qs.Count + ' questions listed') }
        if ([int]$all.data.answered + [int]$all.data.not_applicable + [int]$all.data.pending -ne [int]$all.data.total) { $problems += 'answered + not_applicable + pending is not total' }
        if (-not $all.data.next_question_id -or [string]$all.data.next_question_id -ne [string]$qs[0].id) { $problems += 'next_question_id is not the first pending question' }
        $bad = @($qs | Where-Object { -not $_.id -or -not $_.pointer -or -not $_.text -or -not $_.text.es -or -not $_.text.en -or -not $_.why })
        if ($bad.Count -gt 0) { $problems += ([string]$bad.Count + ' question(s) lack id, pointer, or the es/en text and why') }
        $orders = @($qs | ForEach-Object { [int]$_.order })
        for ($i = 1; $i -lt $orders.Count; $i++) { if ($orders[$i] -lt $orders[$i - 1]) { $problems += 'the questions are not in intake order'; break } }
        if ([int]$partial.data.answered -lt 1) { $problems += 'project.code in the incomplete file was not counted as answered' }
        if ([string]$partial.data.source -ne 'file') { $problems += ('the incomplete file was read as source ' + [string]$partial.data.source) }
        Complete-HorizunIsoCase $Recorder 'ISO-H2' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
            $(if ($problems.Count -eq 0) { ('' + $qs.Count + ' ordered bilingual questions; with the incomplete file ' + $partial.data.answered + ' answered and ' + $partial.data.pending + ' pending') } else { $problems -join '; ' }) `
            @{ total = $all.data.total; pending_without_file = $all.data.pending; answered_with_file = $partial.data.answered }
    }

    # ---- H3 -----------------------------------------------------------------
    $draftPath = Join-Path $ctxDir 'drafted-project-context.json'
    $draft = & $Call 'horizun_project_context' @{ operation = 'draft'; path = $draftPath; dry_run = $true
                                                  answers = @{ '/project/code' = 'HZ01'; '/project/name' = 'Horizun live probe' } }
    if ($draft.isError -or -not $draft.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-H3' 'unverified' ('draft failed: ' + (Limit-HorizunIsoText $draft.text 300)) $null
    } else {
        $problems = @()
        if ($draft.data.dry_run -ne $true -or $draft.data.written -ne $false) { $problems += 'the rehearsal did not say dry_run=true, written=false' }
        if ([string]$draft.data.document.project.code -ne 'HZ01') { $problems += 'the drafted document lacks project.code' }
        if ([string]$draft.data.base -ne 'empty') { $problems += ('base is ' + [string]$draft.data.base) }
        if (Test-Path -LiteralPath $draftPath) { $problems += 'the rehearsal CREATED the file' }
        Complete-HorizunIsoCase $Recorder 'ISO-H3' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
            $(if ($problems.Count -eq 0) { ('drafted onto an empty context (state ' + [string]$draft.data.state + '); no file was created') } else { $problems -join '; ' }) $null
    }

    # ---- H4: inspect a temporary CDE -----------------------------------------
    $cdeRoot = Join-Path $RunDirectory 'cde-host'
    $cdeStates = New-HorizunIsoCde $cdeRoot
    [IO.File]::WriteAllText((Join-Path $cdeRoot 'wip\not a container name.txt'), 'probe', $utf8)
    $unsealed = Join-Path $cdeRoot 'shared\HZ01-HRZ-ZZ-XX-M3-A-0005.txt'
    [IO.File]::WriteAllText($unsealed, 'probe', $utf8)
    $asOf = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $deliverables = @(
        @{ container = 'HZ01-HRZ-ZZ-XX-M3-A-0006'; due = '2020-01-01'; required_status = 'S2' },
        @{ container = 'HZ01-HRZ-ZZ-XX-M3-A-0007'; due = '2999-12-31'; required_status = 'A1' }
    )
    $snapBefore = Get-HorizunIsoFolderSnapshot $cdeRoot
    $inspect = & $Call 'horizun_information_container' @{ operation = 'inspect'; root = $cdeRoot; states = $cdeStates
                                                         deliverables = $deliverables; as_of = $asOf }
    $snapAfter = Get-HorizunIsoFolderSnapshot $cdeRoot
    if ($inspect.isError -or -not $inspect.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-H4' 'unverified' ('inspect failed: ' + (Limit-HorizunIsoText $inspect.text 300)) $null
    } else {
        $findings = @($inspect.data.findings)
        $kinds = @($findings | ForEach-Object { [string]$_.kind })
        $problems = @()
        foreach ($k in @('name_noncompliant', 'missing_sidecar', 'deliverable_overdue', 'deliverable_missing')) {
            if ($kinds -notcontains $k) { $problems += ('no ' + $k + ' finding') }
        }
        $overdue = $findings | Where-Object { $_.kind -eq 'deliverable_overdue' } | Select-Object -First 1
        if ($overdue -and [string]$overdue.container -ne 'HZ01-HRZ-ZZ-XX-M3-A-0006') { $problems += 'the overdue finding names the wrong deliverable' }
        $missing = $findings | Where-Object { $_.kind -eq 'deliverable_missing' } | Select-Object -First 1
        if ($missing -and [string]$missing.container -ne 'HZ01-HRZ-ZZ-XX-M3-A-0007') { $problems += 'the missing finding names the wrong deliverable' }
        if ($inspect.data.coverage_complete -ne $true) { $problems += 'coverage_complete is not true over four existing folders' }
        if ($snapBefore -ne $snapAfter) { $problems += 'inspect changed the CDE folders' }
        if (Test-Path -LiteralPath (Join-Path $cdeRoot '.horizun')) { $problems += 'inspect created .horizun' }
        Complete-HorizunIsoCase $Recorder 'ISO-H4' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
            $(if ($problems.Count -eq 0) { ('findings: ' + (($kinds | Sort-Object -Unique) -join ', ') + '; coverage complete; the folders are unchanged') } else { $problems -join '; ' }) `
            @{ finding_counts = $inspect.data.finding_counts; total_findings = $inspect.data.total_findings }
    }

    # ---- H5: the approval rule ------------------------------------------------
    $pubSnapshot = Get-HorizunIsoFolderSnapshot (Join-Path $cdeRoot 'published')
    $noApprover = & $Call 'horizun_information_container' @{ operation = 'transition'; root = $cdeRoot; states = $cdeStates
        file_path = $unsealed; from_state = 'shared'; to_state = 'published'; status = 'A1'; dry_run = $false }
    if ($noApprover.isError -and $noApprover.text -match 'approved_by' -and
        (Get-HorizunIsoFolderSnapshot (Join-Path $cdeRoot 'published')) -eq $pubSnapshot -and
        -not (Test-Path -LiteralPath (Join-Path $cdeRoot '.horizun'))) {
        Complete-HorizunIsoCase $Recorder 'ISO-H5' 'pass' 'an apply without approved_by was refused naming approved_by; published/ and the transition log are untouched' $null
    } elseif (-not $noApprover.isError) {
        Complete-HorizunIsoCase $Recorder 'ISO-H5' 'fail' 'shared->published WITHOUT approved_by was accepted' $null
    } else {
        Complete-HorizunIsoCase $Recorder 'ISO-H5' 'fail' ('refused for another reason, or something was written: ' + (Limit-HorizunIsoText $noApprover.text 300)) $null
    }

    # ---- H6/H7: a sealed source ------------------------------------------------
    $allows = Test-HorizunIsoProfileAllowsExternalWrite $PermissionProfile
    $source = $null; $sourceRoot = $null; $sourceStates = $null; $sourceOrigin = $null
    if ($SealedSource -and (Test-Path -LiteralPath $SealedSource -PathType Leaf) -and $SealedCdeRoot) {
        $source = $SealedSource; $sourceRoot = $SealedCdeRoot
        $sourceStates = @{ wip = 'wip'; shared = 'shared'; published = 'published'; archived = 'archived' }
        $sourceOrigin = 'the IFC sealed by horizun_export in this run'
    } elseif ($allows -ne $false) {
        # No export sealed anything. Seal the unsealed shared file host-side - the
        # stamp is itself a host-resident write, so it needs the same profile.
        $stamp = & $Call 'horizun_information_container' @{ operation = 'stamp'; file_path = $unsealed; dry_run = $false
            information_container = (New-HorizunIsoContainer -Number '0005' -Type 'M3') }
        if (-not $stamp.isError -and $stamp.data.written -eq $true -and $stamp.data.verified -eq $true) {
            $source = $unsealed; $sourceRoot = $cdeRoot; $sourceStates = $cdeStates
            $sourceOrigin = 'a file sealed host-side by operation=stamp in this run'
        }
    }
    if (-not $source) {
        $why = 'no sealed container exists: the export tier did not seal one and the permission profile (' + $PermissionProfile + ') does not allow a host-side stamp'
        Complete-HorizunIsoCase $Recorder 'ISO-H6' 'not_covered' $why $null
        Complete-HorizunIsoCase $Recorder 'ISO-H7' 'not_covered' $why $null
    } else {
        $published = Join-Path $sourceRoot 'published'
        $log = Join-Path $sourceRoot '.horizun\cde-transitions.jsonl'
        $transition = @{ operation = 'transition'; root = $sourceRoot; states = $sourceStates; file_path = $source
                         from_state = 'shared'; to_state = 'published'; status = 'A1'; approved_by = 'horizun-live-probe'
                         note = ('verify-live ' + $RunId) }
        $pubBefore = Get-HorizunIsoFolderSnapshot $published
        $logExisted = Test-Path -LiteralPath $log
        $rehearsal = & $Call 'horizun_information_container' (Copy-HorizunIsoArgs $transition @{ dry_run = $true })
        $h6Problems = @()
        if ($rehearsal.isError -or -not $rehearsal.data) { $h6Problems += ('the rehearsal was refused: ' + (Limit-HorizunIsoText $rehearsal.text 300)) }
        else {
            if ($rehearsal.data.dry_run -ne $true -or $rehearsal.data.written -ne $false) { $h6Problems += 'the rehearsal did not say dry_run=true, written=false' }
            if ([string]$rehearsal.data.approved_by -ne 'horizun-live-probe') { $h6Problems += 'approved_by was not carried into the plan' }
        }
        if ((Get-HorizunIsoFolderSnapshot $published) -ne $pubBefore) { $h6Problems += 'the rehearsal wrote into published/' }
        if (-not $logExisted -and (Test-Path -LiteralPath $log)) { $h6Problems += 'the rehearsal wrote the transition log' }
        $h6Outcome = 'pass'
        if ($rehearsal.isError -or -not $rehearsal.data) { $h6Outcome = 'fail' } elseif ($h6Problems.Count -gt 0) { $h6Outcome = 'fail' }
        Complete-HorizunIsoCase $Recorder 'ISO-H6' $h6Outcome $(if ($h6Problems.Count -eq 0) { ('every check passed on ' + $sourceOrigin + ' and nothing was copied, stamped or logged') } else { $h6Problems -join '; ' }) $null

        $apply = & $Call 'horizun_information_container' (Copy-HorizunIsoArgs $transition @{ dry_run = $false })
        if ($apply.isError) {
            $writtenAnyway = ((Get-HorizunIsoFolderSnapshot $published) -ne $pubBefore) -or (-not $logExisted -and (Test-Path -LiteralPath $log))
            if ($apply.text -match 'profile' -and -not $writtenAnyway -and $allows -ne $true) {
                Complete-HorizunIsoCase $Recorder 'ISO-H7' 'pass' ('the apply was refused by the permission profile (' + $PermissionProfile + ') with nothing copied or logged') $null
            } else {
                Complete-HorizunIsoCase $Recorder 'ISO-H7' 'fail' ('profile=' + $PermissionProfile + ' written=' + $writtenAnyway + ': ' + (Limit-HorizunIsoText $apply.text 300)) $null
            }
        } else {
            $dest = [string]$apply.data.destination
            $problems = @()
            if ($allows -eq $false) { $problems += ('the profile ' + $PermissionProfile + ' should have refused this write') }
            if ($apply.data.written -ne $true -or $apply.data.verified -ne $true) { $problems += 'written/verified are not both true' }
            if (-not $dest -or -not (Test-Path -LiteralPath $dest -PathType Leaf)) { $problems += 'no copy at the destination' }
            elseif ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { $problems += 'the copy is not byte-identical to the source' }
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { $problems += 'the source was moved or deleted' }
            $logged = $false
            if (Test-Path -LiteralPath $log) { $logged = ((Get-Content -LiteralPath $log -Raw) -match [regex]::Escape([string]$apply.data.transition_id)) }
            if (-not $logged) { $problems += 'the transition id is not in .horizun/cde-transitions.jsonl' }
            $replay = & $Call 'horizun_information_container' (Copy-HorizunIsoArgs $transition @{ dry_run = $false })
            if ($replay.isError -or $replay.data.already_transitioned -ne $true -or $replay.data.written -ne $false) { $problems += 'the same transition sent again did not answer already_transitioned' }
            Complete-HorizunIsoCase $Recorder 'ISO-H7' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
                $(if ($problems.Count -eq 0) { ('a byte-identical copy landed in published/, sealed, verified and logged; the replay answered already_transitioned (' + $sourceOrigin + ')') } else { $problems -join '; ' }) `
                @{ transition_id = $apply.data.transition_id }
        }
    }
}

# -----------------------------------------------------------------------------
# The section, end to end. One call from verify-live; the tests drive the same.
# -----------------------------------------------------------------------------
function Invoke-HorizunIsoSection {
    param([int]$Year, [string]$Document, [Parameter(Mandatory = $true)][string]$ScratchRoot, [string]$RepoRoot,
          [Parameter(Mandatory = $true)][string]$RunId, [string]$WriteGate, [string]$PreferredCategory,
          [switch]$ReadyWrite, [string]$MappedParameter = 'Comments', [string]$PermissionProfile,
          [Parameter(Mandatory = $true)][scriptblock]$Call, [Parameter(Mandatory = $true)][scriptblock]$Apply)
    $recorder = New-HorizunIsoRecorder -IncludeReadyWrite:$ReadyWrite
    $runDir = New-HorizunIsoRunDirectory -Root $ScratchRoot -RunId $RunId -RepoRoot $RepoRoot
    $sealed = $null; $sealedRoot = $null
    if ($WriteGate) {
        Complete-HorizunIsoTier $recorder 'write' 'not_covered' $WriteGate
    } else {
        try {
            Invoke-HorizunIsoDeliveryProbes -Document $Document -Year $Year -RunDirectory $runDir -RunId $RunId `
                -PreferredCategory $PreferredCategory -Call $Call -Apply $Apply -Recorder $recorder `
                -ReadyWrite:$ReadyWrite -MappedParameter $MappedParameter
        } catch {
            foreach ($id in @('ISO-D1', 'ISO-D2', 'ISO-D3', 'ISO-D4', 'ISO-D5', 'ISO-D6')) {
                if (($recorder.Catalog | Where-Object { $_.Id -eq $id }) -and -not $recorder.Results.ContainsKey($id)) {
                    Complete-HorizunIsoCase $recorder $id 'unverified' ('HARNESS: the delivery probes threw: ' + $_.Exception.Message) $null
                }
            }
        }
        try {
            $export = Invoke-HorizunIsoExportProbes -Document $Document -RunDirectory $runDir -RunId $RunId `
                -Call $Call -Apply $Apply -Recorder $recorder
            if ($export -and $export.sealed_source) { $sealed = $export.sealed_source; $sealedRoot = $export.cde_root }
        } catch {
            foreach ($id in @('ISO-E1', 'ISO-E2', 'ISO-E3')) {
                if (-not $recorder.Results.ContainsKey($id)) {
                    Complete-HorizunIsoCase $recorder $id 'unverified' ('HARNESS: the export probes threw: ' + $_.Exception.Message) $null
                }
            }
        }
    }
    try {
        Invoke-HorizunIsoHostProbes -RunDirectory $runDir -RunId $RunId -Call $Call -Recorder $recorder `
            -PermissionProfile $PermissionProfile -SealedSource $sealed -SealedCdeRoot $sealedRoot
    } catch {
        Complete-HorizunIsoTier $recorder 'host' 'unverified' ('HARNESS: the host-resident probes threw: ' + $_.Exception.Message)
    }
    $cases = Close-HorizunIsoRecorder $recorder
    return [pscustomobject]@{ run_directory = $runDir; cases = $cases }
}

# -----------------------------------------------------------------------------
# The consolidator's record (horizun.live-evidence/2). One case is probe x
# harness x Revit year x document; the statuses are the consolidator's own.
# -----------------------------------------------------------------------------
function New-HorizunIsoIdentity {
    param($Health, $BuildIdentity, [string]$ServerSha256, [string]$HarnessFile = 'scripts/verify-live.ps1',
          [string]$HarnessSha256, [string]$HarnessGitBlob, $HarnessTrackedClean, [string]$RepoHead, $RepoClean,
          [int]$Year, [string]$RunId, [string]$Document)
    $addinSha = $null; $revitBuild = $null; $commit = $null; $version = $null; $revitYear = $null; $language = $null
    if ($Health) {
        if ($Health.addin_assembly) { $addinSha = [string]$Health.addin_assembly.sha256 }
        $revitBuild = [string]$Health.revit_build
        $commit = [string]$Health.horizun_commit
        $version = [string]$Health.horizun_version
        $revitYear = [string]$Health.revit_version
        $language = [string]$Health.revit_language
    }
    if (-not $revitYear) { $revitYear = [string]$Year }
    $contract = $null
    if ($BuildIdentity) { $contract = [string]$BuildIdentity.contract_hash }
    return [ordered]@{
        run_id = ('verify-live-iso19650-' + $RunId); harness_file = $HarnessFile; harness_sha256 = $HarnessSha256
        harness_git_blob = $HarnessGitBlob; harness_tracked_clean = $HarnessTrackedClean
        code_candidate_commit = $commit; repo_head = $RepoHead; repo_tracked_clean = $RepoClean
        revit_year = $revitYear; revit_build = $revitBuild; revit_language = $language; horizun_version = $version
        server_sha256 = $ServerSha256; addin_sha256 = $addinSha; contract_hash = $contract
        document = $Document
    }
}

function ConvertTo-HorizunIsoEvidence {
    param([Parameter(Mandatory = $true)]$Cases, [Parameter(Mandatory = $true)]$Identity)
    $counts = [ordered]@{ passed = 0; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0
                          not_assessable = 0; not_applicable = 0; available = 0; implemented_not_live_verified = 0 }
    $probes = @()
    foreach ($c in @($Cases)) {
        if (-not $counts.Contains([string]$c.status)) { throw "HARNESS: case $($c.id) has status '$($c.status)', which the consolidator does not know." }
        $counts[[string]$c.status] = $counts[[string]$c.status] + 1
        $probes += [ordered]@{
            id = $c.id; name = $c.name; tool = $c.tool; tier = $c.tier; status = $c.status
            observed = $c.detail; because = $(if ($c.status -eq 'passed') { $null } else { $c.detail })
            evidence = $c.evidence; recorded_utc = $c.recorded_utc
        }
    }
    $record = [ordered]@{ schema = 'horizun.live-evidence/2'; generated_utc = (Get-Date).ToUniversalTime().ToString('o') }
    foreach ($key in $Identity.Keys) { $record[$key] = $Identity[$key] }
    foreach ($key in $counts.Keys) { $record[$key] = $counts[$key] }
    $record['section'] = 'iso19650'
    $record['probes'] = $probes
    $record['notes'] = @('The ISO 19650 section of scripts/verify-live.ps1. Every case also appears, by the same name, in that run''s main report.')
    return (Protect-HorizunIsoValue ([pscustomobject]$record))
}

function Write-HorizunIsoEvidence {
    param([Parameter(Mandatory = $true)]$Evidence, [Parameter(Mandatory = $true)][string]$Path)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Force -Path $dir }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, ($Evidence | ConvertTo-Json -Depth 40), $utf8)
    return $Path
}

# After the section: the consolidator's record, then the run folder - removed
# only when every case passed, because otherwise its files are the evidence
# somebody needs. The evidence file itself must live OUTSIDE the run folder.
function Complete-HorizunIsoLiveRun {
    param([Parameter(Mandatory = $true)]$Section, [Parameter(Mandatory = $true)]$Identity,
          [Parameter(Mandatory = $true)][string]$EvidencePath, [Parameter(Mandatory = $true)][string]$ScratchRoot,
          [Parameter(Mandatory = $true)][string]$RunId, [switch]$KeepFiles)
    $runFull = [IO.Path]::GetFullPath($Section.run_directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($EvidencePath).StartsWith($runFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'HARNESS: the ISO evidence file may not live inside the run folder that is removed after a green run.'
    }
    $evidence = ConvertTo-HorizunIsoEvidence -Cases $Section.cases -Identity $Identity
    $written = Write-HorizunIsoEvidence -Evidence $evidence -Path $EvidencePath
    $notPassed = @($Section.cases | Where-Object { $_.outcome -ne 'pass' })
    $removed = $false
    if ($notPassed.Count -eq 0 -and -not $KeepFiles) {
        $removed = Remove-HorizunIsoRunDirectory -Path $Section.run_directory -Root $ScratchRoot -RunId $RunId
    }
    return [pscustomobject]@{
        evidence_file = (Protect-HorizunIsoText $written)
        run_directory = (Protect-HorizunIsoText $Section.run_directory)
        run_directory_removed = $removed
        passed = $evidence.passed; failed = $evidence.failed; unverified = $evidence.unverified; not_covered = $evidence.not_covered
        cases = @($Section.cases)
    }
}
