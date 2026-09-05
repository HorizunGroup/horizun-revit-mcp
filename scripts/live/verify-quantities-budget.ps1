#Requires -Version 5.1
<#
  QUANTITIES FROM LINKS, AND A BUDGET COMPARISON - LIVE, ONCE, AGAINST ONE REVIT.

  What was already measured live before this file existed: a takeoff of 1053 rows
  in ONE document, and a comparison against a disposable workbook. What was NOT:
  include_links at all. Not a loaded link, not an unloaded one, not two placements
  of one file, not a linked element id beside a host id, not an incompatible unit,
  not an incomplete read carried through into the comparison. Every one of those
  is arithmetic that a test suite can prove; NONE of them is evidence that Revit
  hands the bridge what the arithmetic assumes.

  So this builds its own state in the document it is given and measures the lot.

  FOUR REFUSALS, the same ones the doctor campaign holds:

    IT REFUSES TO RUN AGAINST THE WRONG BUILD. Commit, contract hash, Revit year
    and active document are demanded before the first probe. Exit 2, nothing run.

    IT REFUSES TO SIMULATE A FIXTURE. A link this machine cannot build, or a
    Power BI destination nobody has authorised, is recorded fixture_missing and
    NAMES exactly what is needed. It is never simulated and reported as evidence.

    IT NEVER SAVES. Not the document, not a copy, not on the way out. The links
    it adds live in memory: close the fixture without saving and they are gone.
    Every file it writes is under a run-scoped temp directory it created.

    IT NEVER TOUCHES THE MACHINE'S SETTINGS. The permission probes need a
    read_only profile and the workbook probes need full_write, and this harness
    will not edit %USERPROFILE%\.horizun\settings.json to get either. Instead the
    HOST-RESIDENT tools - horizun_budget_compare, horizun_excel_read_rows,
    horizun_excel_write_rows, horizun_power_bi_push, which are answered inside the
    MCP server and never reach Revit - are called with HORIZUN_DATA_ROOT pointing
    at a disposable settings root this run created. That is the same escape hatch
    the unit tests use, it is per-process, and it is stated in the evidence of
    every probe that uses it so nobody can mistake it for the machine's own
    policy. Calls that DO reach Revit run with the machine's root untouched,
    because the add-in reads the real one and the two halves must agree.

  POWER BI IS DRY RUN ONLY. Nothing is pushed anywhere. The probe asserts that no
  token was requested and no row was sent, and a second probe names exactly what
  an authorised destination would need, for the integrator to answer.

  Exit 0 only when every probe passed. Exit 2 when the gate refused - which is a
  different thing from a failure and says so. Exit 3 if the buckets do not add up.
#>
[CmdletBinding()]
param(
    # THE COMMIT THESE NUMBERS BELONG TO. Mandatory and exact.
    [Parameter(Mandatory)][string]$RequireCommit,
    [Parameter(Mandatory)][string]$RequireContractHash,
    [string]$Document = 'HZ_WRITE',
    [string]$RequireRevitYear = '2026',
    [string]$ArtifactDir,
    # A DISPOSABLE .rvt to link into the document. Absent: this run copies the
    # active document's own file on disk, which is disposable by construction (the
    # copy) and makes the strongest version of the identity probe - the linked
    # elements wear the SAME element ids as the host ones.
    [string]$LinkFixture,
    # What to measure. Both are checked against the document before anything is
    # asserted; a category with no elements is fixture_missing, not a failure.
    [string]$Category = 'OST_Walls',
    # The parameter this run writes budget codes into. Text, per element, and
    # restored to nothing at the end - the document is never saved either way.
    [string]$ClassificationParameter = 'Comments',
    # An AUTHORISED Power BI destination, if one exists. Absent: the push probe is
    # dry run only and the authorised case is recorded fixture_missing by name.
    [string]$PowerBiWorkspaceId,
    [string]$PowerBiDatasetId,
    [string]$PowerBiTable = 'HorizunBudgetComparison',
    # A WORKSHARED CENTRAL to link with one workset closed. Q4.2 measures what a
    # takeoff says when part of a linked model was never loaded.
    [string]$ClosedWorksetLink = 'C:\hz-live\HZ_CLOSED_L.rvt',
    [string]$ClosedWorksetName = 'HZ_WS_CLOSED'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'quantities-budget' -Document $Document

# =============================================================================
# THE GATE. Nothing below runs until all of these are true.
# =============================================================================

function Assert-HzGate {
    param([Parameter(Mandatory)]$Run)

    $problems = New-Object System.Collections.ArrayList
    $health = $null
    try { $health = Get-HzHealth $Run } catch { $null = $problems.Add("horizun_health did not answer: $($_.Exception.Message)") }

    if ($health) {
        $status = [string](Get-HzProp $health 'status')
        if ($status -ne 'healthy') { $null = $problems.Add("health reports status '$status', not 'healthy'") }

        $year = [string](Get-HzProp $health 'revit_version')
        if ($year -ne $RequireRevitYear) {
            $null = $problems.Add("this is Revit $year and the run is defined against $RequireRevitYear")
        }

        $commit = [string](Get-HzProp $health 'horizun_commit')
        if (-not $commit) { $null = $problems.Add('health reports no commit, so nothing here could be attributed to a build') }
        elseif ($commit -notlike "$RequireCommit*" -and $RequireCommit -notlike "$commit*") {
            $null = $problems.Add("the running add-in is '$commit' and this run is about '$RequireCommit'")
        }

        $active = Get-HzProp $health 'active_document'
        $title = if ($active) { [string](Get-HzProp $active 'title') } else { $null }
        if ($title -ne $Document) {
            $null = $problems.Add("the active document is '$title' and this run is defined against '$Document'")
        }
    }

    $identity = Get-HzResource -Run $Run -Uri 'horizun://build/identity' -Label 'build-identity'
    $hash = if ($identity) { [string](Get-HzProp $identity 'contract_hash') } else { $null }
    if (-not $hash) { $null = $problems.Add('the server published no contract hash, so the two halves cannot be shown to match') }
    elseif ($hash -ne $RequireContractHash) {
        $null = $problems.Add("the server's contract hash is '$hash' and this run is about '$RequireContractHash'")
    }

    if ($problems.Count -eq 0) {
        Write-Host ("  GATE OK  commit={0} revit={1} document={2}" -f
            (Limit-HzText $RequireCommit 12), $RequireRevitYear, $Document) -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host '  THE RUN DID NOT START. Nothing was measured:' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }
    Write-Host ''
    Write-Host '  This is a refusal, not a failure. No probe ran, so no probe passed' -ForegroundColor Yellow
    Write-Host '  and none failed; nothing about the product was learned either way.' -ForegroundColor Yellow
    exit 2
}

# =============================================================================
# HELPERS
# =============================================================================

function Add-HzUnverified {
    param([string]$Id, [string]$Name, [string]$Expected, [string]$Observed)
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Expected -Observed $Observed -Status 'unverified' `
        -Because 'the condition was not produced, so nothing about the product was learned here - which is not a pass, and is not a missing fixture either.'
}

function Add-HzFixtureMissing {
    param([string]$Id, [string]$Name, [string]$Needs)
    Add-HzProbe -Run $run -Id $Id -Name $Name -Expected $Needs `
        -Observed 'the fixture is not present on this machine' -Status 'fixture_missing' `
        -Because 'simulating this and reporting it as Revit evidence would be a lie about where the number came from.'
}

<#
  A HOST-RESIDENT call, under a settings root this run owns.

  horizun_budget_compare, horizun_excel_read_rows, horizun_excel_write_rows and
  horizun_power_bi_push are answered INSIDE the MCP server and never reach Revit,
  so the server that answers them may read its settings from anywhere. Pointing
  HORIZUN_DATA_ROOT at a disposable directory is how this run exercises read_only
  and full_write without editing the machine's own settings.json - which it must
  never do, and which would be a change to the owner's policy rather than a test.

  Every probe that uses this says so in its evidence.
#>
function New-HzProfileRoot {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Profile)
    $root = Join-Path $Run.WorkDir ("settings-" + $Profile)
    if (-not (Test-Path -LiteralPath $root)) {
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        ('{"permission_profile":"' + $Profile + '"}') |
            Set-Content -LiteralPath (Join-Path $root 'settings.json') -Encoding ascii
    }
    $root
}

function Invoke-HzHostTool {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Arguments = @{},
        [Parameter(Mandatory)][string]$Label,
        [string]$Profile = 'full_write',
        [int]$TimeoutSec = 300
    )
    $root = New-HzProfileRoot -Run $Run -Profile $Profile
    $had = Test-Path Env:\HORIZUN_DATA_ROOT
    $saved = if ($had) { $env:HORIZUN_DATA_ROOT } else { $null }
    try {
        $env:HORIZUN_DATA_ROOT = $root
        Invoke-HzTool -Run $Run -Tool $Tool -Arguments $Arguments -Label $Label -TimeoutSec $TimeoutSec
    }
    finally {
        if ($had) { $env:HORIZUN_DATA_ROOT = $saved }
        else { Remove-Item Env:\HORIZUN_DATA_ROOT -ErrorAction SilentlyContinue }
    }
}

<#
  A minimal .xlsx, byte for byte the package horizun_excel_write_rows expects.

  The writer APPENDS and never invents a package - "an xlsx never is [created],
  its structure cannot be invented" - so the baseline this run compares against
  has to start somewhere, and it starts here rather than as a checked-in binary
  nobody can regenerate.
#>
function New-HzMinimalWorkbook {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sheet)
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $parts = [ordered]@{
        '[Content_Types].xml' =
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' +
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' +
            '<Default Extension="xml" ContentType="application/xml"/>' +
            '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>' +
            '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' +
            '</Types>'
        '_rels/.rels' =
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>' +
            '</Relationships>'
        'xl/workbook.xml' =
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
            '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">' +
            ('<sheets><sheet name="{0}" sheetId="1" r:id="rId1"/></sheets></workbook>' -f $Sheet)
        'xl/_rels/workbook.xml.rels' =
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>' +
            '</Relationships>'
        'xl/worksheets/sheet1.xml' =
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
            '<dimension ref="A1:A1"/><sheetData/></worksheet>'
    }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $parts.Keys) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
                try { $writer.Write([string]$parts[$name]) } finally { $writer.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    $Path
}

<#
  One takeoff. Returns the raw call so a probe can read its refusal as easily as
  its rows.
#>
function Invoke-HzTakeoff {
    param(
        [Parameter(Mandatory)][array]$Quantities,
        [string]$Classification = $ClassificationParameter,
        [switch]$IncludeLinks,
        [int]$Top = 2000,
        [Parameter(Mandatory)][string]$Label
    )
    $a = @{
        mode = 'takeoff'
        target_document_title = $Document
        category = $Category
        classification_parameter = $Classification
        quantities = $Quantities
        include_links = [bool]$IncludeLinks
        top = $Top
    }
    Invoke-HzTool -Run $run -Tool 'horizun_quantities' -Arguments $a -Label $Label -TimeoutSec 900
}

function Get-HzTakeoffRows { param($Call) if ($null -eq $Call -or -not $Call.Ok) { return @() } @($Call.Result.rows) }

function Get-HzQuantityState {
    param($Row, [string]$Name)
    [string](Get-HzPath $Row @('quantities', $Name, 'state'))
}

<#
  The identity of a takeoff row: the placement it came through and the element id
  inside that placement. An element id names an element in ONE document, so this
  triple is what makes two rows the same row - and what makes a linked element
  wearing a host element's id a DIFFERENT row rather than a duplicate.
#>
function Get-HzRowIdentity {
    param($Row)
    '{0}|{1}|{2}' -f ([string](Get-HzProp $Row 'document')),
                     ([string](Get-HzProp $Row 'link_instance_id')),
                     ([string](Get-HzProp $Row 'element_id'))
}

# =============================================================================
Assert-HzGate -Run $run
# =============================================================================

$work = $run.WorkDir
Add-HzNote -Run $run -Text ("every file this run writes is under a run-scoped temp directory; nothing is saved into the model")

# -----------------------------------------------------------------------------
# PHASE 1 - the host takeoff: the five reading states, kept apart, in units the
# caller declared.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 1 - readings, states and units in the host' -ForegroundColor Cyan

$hostCount = 0
try { $hostCount = Get-HzElementCount -Run $run -Categories @($Category) -Label 'host-count' } catch { $hostCount = 0 }

$missingParameter = 'HZ_NO_SUCH_PARAMETER_' + $run.RunId.Replace('-', '_')
$dimensioned = $null
$codedIds = @()
# HOW MANY ELEMENTS THE TAKEOFF ITSELF MEASURES IN THE HOST. Not the query's
# matched_total: query_model counts element TYPES among its matches and the
# takeoff's collector does not, so the two numbers are allowed to differ and the
# link arithmetic below must be built on the one the takeoff produced.
$hostMeasured = $null

if ($hostCount -lt 2) {
    Add-HzFixtureMissing -Id 'Q1.0' -Name 'a host category with elements to code and measure' `
        -Needs ("at least two elements of $Category in '$Document'. Pass -Category with a category this document " +
                "actually holds; a takeoff of nothing is not a measurement of anything.")
}
else {
    $run.Fixture['category'] = $Category
    $run.Fixture['host_elements_in_category'] = $hostCount

    # ---- disposable state: three elements, two codes. ------------------------
    $sample = @(Get-HzElements -Run $run -Categories @($Category) -MaxRows 8 -Label 'host-sample')
    $codedIds = @($sample | Select-Object -First 3 | ForEach-Object { [long]$_.element_id })
    $codeOf = @{}
    $writes = @()
    for ($i = 0; $i -lt $codedIds.Count; $i++) {
        $code = if ($i -lt 2) { 'HZ-A' } else { 'HZ-B' }
        $codeOf[[string]$codedIds[$i]] = $code
        $writes += @{ target_id = $codedIds[$i]; parameter = $ClassificationParameter; value = $code }
    }
    # STAGING IS A PROBE OF ITS OWN. If the codes did not land, every by_code
    # assertion below is measuring the staging rather than the takeoff - and a
    # thrown harness error would leave no artifact at all to say so.
    $coded = Invoke-HzWrite -Run $run -Tool 'horizun_write_params_verified' -Label 'code-host' -AllowRefusal -Arguments @{
        target_document = $Document
        target_document_title = $Document
        writes = $writes
        transaction_name = 'Horizun live: takeoff codes'
    }
    Add-HzProbe -Run $run -Id 'Q1.0' -Name "this run's budget codes are written into the host and re-read" `
        -Expected "$($writes.Count) write(s) of '$ClassificationParameter', each verified against a re-read after the commit" `
        -Observed $(if ($coded.Ok) {
                        "verified=$(Get-HzPath $coded.Apply.Result @('verification','verified')) " +
                        "confirmed=$(Get-HzPath $coded.Apply.Result @('verification','confirmed_against_your_value'))" +
                        "/$(Get-HzPath $coded.Apply.Result @('verification','rows'))"
                    }
                    elseif ($coded.Apply) { Limit-HzText $coded.Apply.Text 240 } else { Limit-HzText $coded.Dry.Text 240 }) `
        -Status $(if ($coded.Ok -and $true -eq (Get-HzPath $coded.Apply.Result @('verification', 'verified'))) { 'passed' } else { 'failed' }) `
        -Because 'the codes are this run''s disposable state; nothing below means anything if they are not in the model.'
    $run.Expected['coded_host_elements'] = $codeOf

    # ---- which dimensioned parameter this category actually carries. ---------
    # Asked, not assumed: the incompatible-unit case needs a parameter Revit
    # itself calls a Length/Area/Volume, and which of those exists depends on the
    # category somebody passed.
    foreach ($candidate in @('Volume', 'Area', 'Length')) {
        $probeUnit = switch ($candidate) { 'Volume' { 'm3' } 'Area' { 'm2' } default { 'm' } }
        $t = Invoke-HzTakeoff -Quantities @(@{ name = 'probe'; source = 'parameter'; parameter = $candidate; unit = $probeUnit }) `
                              -Top 20 -Label ('probe-' + $candidate.ToLowerInvariant())
        if (-not $t.Ok) { continue }
        $measured = @(Get-HzTakeoffRows $t | Where-Object { (Get-HzQuantityState $_ 'probe') -eq 'measured' })
        if ($measured.Count -gt 0) {
            $dimensioned = [pscustomobject]@{ Parameter = $candidate; Unit = $probeUnit }
            break
        }
    }

    # ---- the takeoff every Phase 1 probe reads. ------------------------------
    $quantities = @(
        @{ name = 'each';        source = 'count';           unit = 'un' },
        @{ name = 'volume';      source = 'geometry_volume'; unit = 'm3' },
        @{ name = 'absent_read'; source = 'parameter'; parameter = $missingParameter; unit = 'm3' },
        @{ name = 'text_value';  source = 'parameter'; parameter = $ClassificationParameter; unit = 'm3' }
    )
    if ($dimensioned) {
        # A Length parameter declared as m2. The one case the description promises
        # is never silently relabelled.
        $wrong = if ($dimensioned.Unit -eq 'm2') { 'm3' } else { 'm2' }
        $quantities += @{ name = 'wrong_unit'; source = 'parameter'; parameter = $dimensioned.Parameter; unit = $wrong }
        $quantities += @{ name = 'right_unit'; source = 'parameter'; parameter = $dimensioned.Parameter; unit = $dimensioned.Unit }
    }

    $host1 = Invoke-HzTakeoff -Quantities $quantities -Top 2000 -Label 'host-takeoff'
    $hostRows = @(Get-HzTakeoffRows $host1)
    if ($host1.Ok) {
        $hostMeasured = [int](Get-HzProp $host1.Result 'elements_requested')
        $run.Fixture['host_elements_measured'] = $hostMeasured
    }

    Add-HzProbe -Run $run -Id 'Q1.1' -Name 'the takeoff answers, and every row names its element and its document' `
        -Expected 'each row carries element_id and document; host rows carry link_instance_id null' `
        -Observed $(if ($host1.Ok) { "$($hostRows.Count) row(s)" } else { Limit-HzText $host1.Text 220 }) `
        -Status $(if ($host1.Ok -and $hostRows.Count -gt 0 -and
                      @($hostRows | Where-Object { -not (Get-HzProp $_ 'element_id') -or -not (Get-HzProp $_ 'document') }).Count -eq 0 -and
                      @($hostRows | Where-Object { $null -ne (Get-HzProp $_ 'link_instance_id') }).Count -eq 0) { 'passed' } else { 'failed' }) `
        -Evidence @{ rows = $hostRows.Count; first = ($hostRows | Select-Object -First 1) }

    if ($host1.Ok) {
        # ---- Q1.2 the five states, and a reason whenever it is not a measurement.
        $states = @{}
        $reasonless = 0
        foreach ($row in $hostRows) {
            foreach ($q in @($quantities | ForEach-Object { $_.name })) {
                $state = Get-HzQuantityState $row $q
                if (-not $state) { $state = '(no state)' }
                $states[$state] = 1 + $(if ($states.ContainsKey($state)) { $states[$state] } else { 0 })
                if ($state -ne 'measured' -and -not (Get-HzPath $row @('quantities', $q, 'reason'))) { $reasonless++ }
            }
        }
        $known = @('measured', 'absent', 'empty', 'unreadable', 'invalid')
        $unknown = @($states.Keys | Where-Object { $known -notcontains $_ })
        Add-HzProbe -Run $run -Id 'Q1.2' -Name 'every reading is one of the five states, and says why when it is not a measurement' `
            -Expected 'states within measured | absent | empty | unreadable | invalid, each non-measurement carrying a reason' `
            -Observed (($states.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ') `
            -Status $(if ($unknown.Count -eq 0 -and $reasonless -eq 0) { 'passed' } else { 'failed' }) `
            -Evidence @{ states = $states; unknown_states = $unknown; non_measurements_without_a_reason = $reasonless }

        # ---- Q1.3 an absent parameter is ABSENT, not zero and not empty. ------
        $absentStates = @($hostRows | ForEach-Object { Get-HzQuantityState $_ 'absent_read' } | Sort-Object -Unique)
        $absentValues = @($hostRows | ForEach-Object { Get-HzPath $_ @('quantities', 'absent_read', 'value') } |
                          Where-Object { $null -ne $_ })
        Add-HzProbe -Run $run -Id 'Q1.3' -Name 'a parameter that does not exist reads absent, and carries no number' `
            -Expected "every reading of '$missingParameter' is absent with a null value" `
            -Observed ("states: " + ($absentStates -join ',') + "; non-null values: " + $absentValues.Count) `
            -Status $(if ($absentStates.Count -eq 1 -and $absentStates[0] -eq 'absent' -and $absentValues.Count -eq 0) { 'passed' } else { 'failed' }) `
            -Because 'a zero here would read as "this element has none of it" rather than "nobody could read it".'

        # ---- Q1.4 text where a number was declared is INVALID. ---------------
        $textStates = @($hostRows | Where-Object { $codedIds -contains [long](Get-HzProp $_ 'element_id') } |
                        ForEach-Object { Get-HzQuantityState $_ 'text_value' } | Sort-Object -Unique)
        Add-HzProbe -Run $run -Id 'Q1.4' -Name 'a text parameter read as a quantity is invalid, not coerced' `
            -Expected "the elements this run coded read '$ClassificationParameter' as invalid" `
            -Observed ('states: ' + ($textStates -join ',')) `
            -Status $(if ($textStates.Count -eq 1 -and $textStates[0] -eq 'invalid') { 'passed' } else { 'failed' }) `
            -Evidence @{ coded_elements = $codedIds; states = $textStates }

        # ---- Q1.5 / Q1.6 the units. ------------------------------------------
        if (-not $dimensioned) {
            Add-HzFixtureMissing -Id 'Q1.5' -Name 'a Length/Area/Volume parameter declared in the wrong unit' `
                -Needs ("a category whose elements carry a dimensioned parameter Revit calls Length, Area or Volume. " +
                        "$Category in '$Document' answered none of Volume/Area/Length as measured, so there is nothing " +
                        "to declare the wrong unit for. Pass -Category with one that has one.")
            Add-HzFixtureMissing -Id 'Q1.6' -Name 'the same parameter declared in the RIGHT unit' `
                -Needs 'the same fixture as Q1.5.'
        }
        else {
            $wrongStates = @($hostRows | ForEach-Object { Get-HzQuantityState $_ 'wrong_unit' } | Sort-Object -Unique)
            $wrongUnitsKept = @($hostRows | ForEach-Object { [string](Get-HzPath $_ @('quantities', 'wrong_unit', 'unit')) } | Sort-Object -Unique)
            $declaredWrong = [string]($quantities | Where-Object { $_.name -eq 'wrong_unit' } | ForEach-Object { $_.unit })
            Add-HzProbe -Run $run -Id 'Q1.5' -Name 'a dimensioned parameter declared in an incompatible unit is invalid, never relabelled' `
                -Expected ("every reading of '" + $dimensioned.Parameter + "' declared '" + $declaredWrong + "' is invalid, and the reply still reports the unit the caller declared") `
                -Observed ('states: ' + ($wrongStates -join ',') + '; unit echoed: ' + ($wrongUnitsKept -join ',')) `
                -Status $(if ($wrongStates.Count -eq 1 -and $wrongStates[0] -eq 'invalid' -and
                              $wrongUnitsKept.Count -eq 1 -and $wrongUnitsKept[0] -eq $declaredWrong) { 'passed' } else { 'failed' }) `
                -Because 'a value quietly relabelled into the declared unit is a wrong number that looks right all the way into a budget.' `
                -Evidence @{ parameter = $dimensioned.Parameter; declared = $declaredWrong; measured_in = $dimensioned.Unit }

            $rightMeasured = @($hostRows | Where-Object { (Get-HzQuantityState $_ 'right_unit') -eq 'measured' })
            Add-HzProbe -Run $run -Id 'Q1.6' -Name 'the same parameter in its own unit is measured' `
                -Expected ("'" + $dimensioned.Parameter + "' declared '" + $dimensioned.Unit + "' is measured on at least one element") `
                -Observed ("$($rightMeasured.Count) of $($hostRows.Count) row(s) measured") `
                -Status $(if ($rightMeasured.Count -gt 0) { 'passed' } else { 'failed' })
        }

        # ---- Q1.7 the buckets are kept apart in by_code. ----------------------
        $codeA = Get-HzPath $host1.Result @('by_code', 'HZ-A')
        $tallies = if ($codeA) { Get-HzPath $codeA @('quantities', 'absent_read') } else { $null }
        Add-HzProbe -Run $run -Id 'Q1.7' -Name 'by_code counts measured, absent, empty, unreadable and invalid separately' `
            -Expected "code HZ-A publishes all five counters, with absent = its element count for the parameter that does not exist" `
            -Observed $(if ($tallies) {
                    "elements=$(Get-HzProp $codeA 'elements') measured=$(Get-HzProp $tallies 'measured') absent=$(Get-HzProp $tallies 'absent') empty=$(Get-HzProp $tallies 'empty') unreadable=$(Get-HzProp $tallies 'unreadable') invalid=$(Get-HzProp $tallies 'invalid')"
                } else { 'code HZ-A is not in by_code' }) `
            -Status $(if ($tallies -and
                          [int](Get-HzProp $tallies 'absent') -eq [int](Get-HzProp $codeA 'elements') -and
                          [int](Get-HzProp $tallies 'measured') -eq 0 -and
                          $false -eq (Get-HzProp $tallies 'complete')) { 'passed' } else { 'failed' }) `
            -Evidence @{ by_code_HZ_A = $codeA }

        # ---- Q1.8 coverage is not claimed complete when readings failed. ------
        Add-HzProbe -Run $run -Id 'Q1.8' -Name 'a takeoff with invalid readings does not claim complete coverage' `
            -Expected 'coverage_complete false, and coverage.invalid_readings greater than zero' `
            -Observed ("coverage_complete=$(Get-HzProp $host1.Result 'coverage_complete') invalid=$(Get-HzPath $host1.Result @('coverage','invalid_readings'))") `
            -Status $(if ($false -eq (Get-HzProp $host1.Result 'coverage_complete') -and
                          [int](Get-HzPath $host1.Result @('coverage', 'invalid_readings')) -gt 0) { 'passed' } else { 'failed' })
    }
    else {
        Add-HzProbe -Run $run -Id 'Q1.2' -Name 'the five reading states' -Expected 'a takeoff to read them from' `
            -Observed (Limit-HzText $host1.Text 220) -Status 'unverified'
    }

    # ---- Q1.9 / Q1.10 the classification non-values, kept apart. --------------
    $noParam = Invoke-HzTakeoff -Quantities @(@{ name = 'each'; source = 'count'; unit = 'un' }) `
                                -Classification $missingParameter -Top 50 -Label 'classification-missing'
    $noParamCodes = @(Get-HzTakeoffRows $noParam | ForEach-Object { [string](Get-HzProp $_ 'classification_code') } | Sort-Object -Unique)
    Add-HzProbe -Run $run -Id 'Q1.9' -Name 'a classification parameter that does not exist is (no such parameter), not blank' `
        -Expected "every classification_code is '(no such parameter)'" `
        -Observed ('codes: ' + ($noParamCodes -join ' | ')) `
        -Status $(if ($noParam.Ok -and $noParamCodes.Count -eq 1 -and $noParamCodes[0] -eq '(no such parameter)') { 'passed' } else { 'failed' }) `
        -Because 'a missing parameter and an empty one are different findings and would otherwise render the same.'

    $emptyCodes = @(Get-HzTakeoffRows $host1 |
                    Where-Object { $codedIds -notcontains [long](Get-HzProp $_ 'element_id') } |
                    ForEach-Object { [string](Get-HzProp $_ 'classification_code') } | Sort-Object -Unique)
    Add-HzProbe -Run $run -Id 'Q1.10' -Name 'an element whose classification parameter is unset is (empty)' `
        -Expected "the elements this run did NOT code carry '(empty)'" `
        -Observed ('codes: ' + (($emptyCodes | Select-Object -First 5) -join ' | ')) `
        -Status $(if ($emptyCodes -contains '(empty)') { 'passed' } else { 'failed' }) `
        -Evidence @{ distinct_codes = $emptyCodes }
}

# -----------------------------------------------------------------------------
# PHASE 2 - the links. Loaded, placed twice, and unloaded.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 2 - quantities FROM LINKS' -ForegroundColor Cyan

$linkPath = $null
$linkSource = $null
if ($LinkFixture) {
    if (Test-Path -LiteralPath $LinkFixture) { $linkSource = (Resolve-Path -LiteralPath $LinkFixture).Path }
}
else {
    $health = Get-HzHealth $run
    $activePath = [string](Get-HzPath $health @('active_document', 'path'))
    if ($activePath -and (Test-Path -LiteralPath $activePath)) { $linkSource = $activePath }
}
if ($linkSource) {
    # A COPY, always. The original is never linked, never opened by this run and
    # never written; what gets linked lives in the run's temp directory and dies
    # with it.
    $linkPath = Join-Path $work ('HZ_LINK_' + $run.RunId.Substring($run.RunId.Length - 8) + '.rvt')
    Copy-Item -LiteralPath $linkSource -Destination $linkPath -Force
    $run.Fixture['link_source_sha256'] = Get-HzSha256 $linkPath
    $run.Fixture['link_is_a_copy_of_the_active_document'] = (-not $LinkFixture)
}

$linkTypeId = $null
$instanceA = $null
$instanceB = $null
$linkTitle = $null

$linkProbeIds = @('Q2.1', 'Q2.2', 'Q2.3', 'Q2.4', 'Q2.5', 'Q2.6', 'Q2.7', 'Q2.8', 'Q2.9', 'Q2.10', 'Q2.11')

if ($null -eq $hostMeasured) {
    foreach ($id in $linkProbeIds) {
        Add-HzProbe -Run $run -Id $id -Name 'quantities through a Revit link' `
            -Expected 'a host takeoff to compare the linked one against' `
            -Observed 'Phase 1 produced no host takeoff, so there is no baseline number for the link arithmetic' `
            -Status 'unverified'
    }
}
elseif (-not $linkPath) {
    foreach ($id in $linkProbeIds) {
        Add-HzFixtureMissing -Id $id -Name 'a Revit link to measure through' `
            -Needs ("a disposable .rvt to link, as -LinkFixture, or an active document whose file exists on disk for " +
                    "this run to copy. Nothing here is simulated: a takeoff of a link that is not there would be a " +
                    "takeoff of the host wearing a link's name.")
    }
}
else {
    $add = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'link-add' -AllowRefusal -Arguments @{
        operation = 'add'; path = $linkPath; target_document = $Document
    }
    if ($add.Ok) {
        $linkTypeId = [long](Get-HzProp $add.Apply.Result 'link_type_id')
        $instanceA = [string](Get-HzProp $add.Apply.Result 'link_instance_id')
    }

    if (-not $linkTypeId) {
        $why = if ($add.Apply) { Limit-HzText $add.Apply.Text 300 } elseif ($add.Dry) { Limit-HzText $add.Dry.Text 300 } else { 'no reply' }
        foreach ($id in $linkProbeIds) {
            Add-HzProbe -Run $run -Id $id -Name 'quantities through a Revit link' `
                -Expected 'a link this run created in the fixture' -Observed "the link could not be added: $why" `
                -Status 'unverified'
        }
    }
    else {
        $run.Fixture['link_type_id'] = $linkTypeId
        $run.Fixture['link_instance_a'] = $instanceA

        $countQ = @(@{ name = 'each'; source = 'count'; unit = 'un' })
        $linked1 = Invoke-HzTakeoff -Quantities $countQ -IncludeLinks -Top 4000 -Label 'takeoff-one-link'
        $rows1 = @(Get-HzTakeoffRows $linked1)
        $docs1 = @(if ($linked1.Ok) { Get-HzProp $linked1.Result 'documents' } else { @() })
        $linkDoc = @($docs1 | Where-Object { [string](Get-HzProp $_ 'kind') -eq 'link' }) | Select-Object -First 1
        if ($linkDoc) { $linkTitle = [string](Get-HzProp $linkDoc 'document') }

        # ---- Q2.1 provenance of the loaded link. -----------------------------
        $transform = if ($linkDoc) { Get-HzProp $linkDoc 'transform' } else { $null }
        Add-HzProbe -Run $run -Id 'Q2.1' -Name 'a LOADED link is reported with its document, its instance id and its transform' `
            -Expected 'the documents block carries kind=link, a document title, a path, the link_instance_id this run created, and a transform' `
            -Observed $(if ($linkDoc) {
                    "document=$(Get-HzProp $linkDoc 'document') instance=$(Get-HzProp $linkDoc 'link_instance_id') transform=$(if ($transform) { 'present' } else { 'absent' })"
                } else { 'no link entry in documents' }) `
            -Status $(if ($linkDoc -and
                          [string](Get-HzProp $linkDoc 'link_instance_id') -eq $instanceA -and
                          (Get-HzProp $linkDoc 'path') -and $transform -and
                          $null -ne (Get-HzProp $transform 'is_identity')) { 'passed' } else { 'failed' }) `
            -Evidence @{ documents = $docs1 }

        # ---- Q2.2 host rows and linked rows are distinguishable. -------------
        $linkedRows = @($rows1 | Where-Object { [string](Get-HzProp $_ 'link_instance_id') -eq $instanceA })
        $hostOnly = @($rows1 | Where-Object { $null -eq (Get-HzProp $_ 'link_instance_id') })
        Add-HzProbe -Run $run -Id 'Q2.2' -Name 'host rows and linked rows are told apart on the row itself' `
            -Expected 'every row is either a host row (link_instance_id null) or a linked row carrying the instance id, and both kinds exist' `
            -Observed ("host rows: $($hostOnly.Count); linked rows: $($linkedRows.Count); rows in total: $($rows1.Count)") `
            -Status $(if ($linked1.Ok -and $hostOnly.Count -gt 0 -and $linkedRows.Count -gt 0 -and
                          ($hostOnly.Count + $linkedRows.Count) -eq $rows1.Count) { 'passed' } else { 'failed' })

        # ---- Q2.3 a linked element id is not a host element id. --------------
        $hostIds = @($hostOnly | ForEach-Object { [string](Get-HzProp $_ 'element_id') })
        $linkIds = @($linkedRows | ForEach-Object { [string](Get-HzProp $_ 'element_id') })
        $shared = @($linkIds | Where-Object { $hostIds -contains $_ })
        $identities = @($rows1 | ForEach-Object { Get-HzRowIdentity $_ })
        $distinct = @($identities | Sort-Object -Unique)
        Add-HzProbe -Run $run -Id 'Q2.3' -Name 'a linked element id is never confused with a host element id' `
            -Expected 'rows are identified by (document, link_instance_id, element_id), so an id shared between host and link is two rows, not one' `
            -Observed ("$($shared.Count) element id(s) occur in both documents; $($distinct.Count) distinct row identities over $($identities.Count) rows") `
            -Status $(if ($linked1.Ok -and $distinct.Count -eq $identities.Count) { 'passed' } else { 'failed' }) `
            -Because 'an element id names an element inside ONE document; the same integer names something unrelated in every link.' `
            -Evidence @{ ids_in_both = ($shared | Select-Object -First 10); rows = $identities.Count; distinct_identities = $distinct.Count }

        # ---- Q2.4 the linked elements are actually counted. -------------------
        $requestedWithLink = if ($linked1.Ok) { [int](Get-HzProp $linked1.Result 'elements_requested') } else { 0 }
        Add-HzProbe -Run $run -Id 'Q2.4' -Name 'the linked elements are measured, not merely listed' `
            -Expected "elements_requested exceeds the host's own $hostMeasured element(s) once a link is loaded" `
            -Observed ("elements_requested=$requestedWithLink, host alone=$hostMeasured") `
            -Status $(if ($null -ne $hostMeasured -and $requestedWithLink -gt $hostMeasured) { 'passed' } else { 'failed' })

        # ---- the SECOND placement of the SAME file. --------------------------
        $addInstance = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'link-add-instance' -AllowRefusal -Arguments @{
            operation = 'add_instance'; link_type_id = $linkTypeId; target_document = $Document
        }
        if ($addInstance.Ok) { $instanceB = [string](Get-HzProp $addInstance.Apply.Result 'link_instance_id') }

        Add-HzProbe -Run $run -Id 'Q2.5' -Name 'a second instance of the SAME linked file can be placed, and is verified' `
            -Expected 'add_instance returns a new link_instance_id, instances_after 2, verified by re-reading the instance and its type' `
            -Observed $(if ($instanceB) {
                    "instance=$instanceB instances_after=$(Get-HzProp $addInstance.Apply.Result 'instances_after') verified=$(Get-HzProp $addInstance.Apply.Result 'verified_after_reread')"
                } elseif ($addInstance.Apply) { Limit-HzText $addInstance.Apply.Text 260 } else { 'no reply' }) `
            -Status $(if ($instanceB -and $instanceB -ne $instanceA -and
                          [int](Get-HzProp $addInstance.Apply.Result 'instances_after') -eq 2 -and
                          $true -eq (Get-HzProp $addInstance.Apply.Result 'verified_after_reread')) { 'passed' } else { 'failed' }) `
            -Because 'Revit holds one link type per path, so a file placed twice is one type with two instances - and until this operation existed the bridge could not create the second one.'

        if (-not $instanceB) {
            foreach ($id in @('Q2.6', 'Q2.7', 'Q2.8', 'Q2.9')) {
                Add-HzProbe -Run $run -Id $id -Name 'two placements of one linked file' `
                    -Expected 'a second instance of the link type' -Observed 'the second placement was not created' -Status 'unverified'
            }
        }
        else {
            $run.Fixture['link_instance_b'] = $instanceB
            $linked2 = Invoke-HzTakeoff -Quantities $countQ -IncludeLinks -Top 6000 -Label 'takeoff-two-placements'
            $rows2 = @(Get-HzTakeoffRows $linked2)
            $docs2 = @(if ($linked2.Ok) { Get-HzProp $linked2.Result 'documents' } else { @() })
            $placements = @($docs2 | Where-Object { [string](Get-HzProp $_ 'kind') -eq 'link' })

            Add-HzProbe -Run $run -Id 'Q2.6' -Name 'the same file placed twice is TWO entries, numbered, each with its own instance id' `
                -Expected 'two link entries, placement 1 and 2, placements_of_this_document 2 on both, and two different link_instance_ids' `
                -Observed ("link entries: $($placements.Count); instances: " +
                           (@($placements | ForEach-Object { Get-HzProp $_ 'link_instance_id' }) -join ',') +
                           "; placements: " + (@($placements | ForEach-Object { Get-HzProp $_ 'placement' }) -join ',')) `
                -Status $(if ($placements.Count -eq 2 -and
                              (@($placements | ForEach-Object { [string](Get-HzProp $_ 'link_instance_id') } | Sort-Object -Unique)).Count -eq 2 -and
                              (@($placements | ForEach-Object { [int](Get-HzProp $_ 'placement') } | Sort-Object) -join ',') -eq '1,2' -and
                              @($placements | Where-Object { [int](Get-HzProp $_ 'placements_of_this_document') -ne 2 }).Count -eq 0) { 'passed' } else { 'failed' }) `
                -Because 'Revit hands both instances the SAME Document; a scope keyed by document reported every row under whichever instance it kept last.' `
                -Evidence @{ documents = $docs2 }

            $ids2 = @($rows2 | ForEach-Object { Get-HzRowIdentity $_ })
            $distinct2 = @($ids2 | Sort-Object -Unique)
            $perInstance = @{}
            foreach ($row in $rows2) {
                $key = [string](Get-HzProp $row 'link_instance_id')
                if (-not $key) { $key = '(host)' }
                $perInstance[$key] = 1 + $(if ($perInstance.ContainsKey($key)) { $perInstance[$key] } else { 0 })
            }
            # TRUNCATION IS NOT A FINDING. rows is a page; by_code and the coverage
            # counters are exact. Comparing "rows per instance" across a page that
            # stopped inside one placement would measure the page size, so a
            # truncated reply is recorded not_assessable rather than failed.
            $truncated2 = $true -eq (Get-HzProp $linked2.Result 'truncated')
            $q27 = if (-not $linked2.Ok) { 'failed' }
                   elseif ($truncated2) { 'not_assessable' }
                   elseif ($distinct2.Count -eq $ids2.Count -and
                           $perInstance.ContainsKey($instanceA) -and $perInstance.ContainsKey($instanceB) -and
                           $perInstance[$instanceA] -eq $perInstance[$instanceB]) { 'passed' }
                   else { 'failed' }
            Add-HzProbe -Run $run -Id 'Q2.7' -Name 'no element is counted twice under one placement, and both placements are present' `
                -Expected '(document, link_instance_id, element_id) is unique across every row, and both instance ids carry the same number of rows' `
                -Observed $(if ($truncated2) { "the reply was truncated at $(Get-HzProp $linked2.Result 'top') row(s) of $(Get-HzProp $linked2.Result 'rows_matching'); per-placement row counts cannot be compared across a page" }
                            else { ($perInstance.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ' }) `
                -Status $q27 `
                -Evidence @{ rows = $ids2.Count; distinct_identities = $distinct2.Count; rows_per_instance = $perInstance
                             truncated = $truncated2 }

            $repeated = @(if ($linked2.Ok) { Get-HzProp $linked2.Result 'repeated_link_documents' } else { @() })
            $repeatedIds = @(if ($repeated.Count -gt 0) { $repeated[0].link_instance_ids } else { @() })
            Add-HzProbe -Run $run -Id 'Q2.8' -Name 'the repetition is DECLARED, with both instance ids named' `
                -Expected 'repeated_link_documents names the file, says it is placed 2 times, and lists both link instance ids' `
                -Observed $(if ($repeated.Count -gt 0) {
                        "document=$($repeated[0].document) placements=$($repeated[0].placements) ids=$(@($repeatedIds) -join ',')"
                    } else { 'repeated_link_documents is empty' }) `
                -Status $(if ($repeated.Count -eq 1 -and [int]$repeated[0].placements -eq 2 -and
                              (@($repeatedIds) -contains $instanceA) -and (@($repeatedIds) -contains $instanceB)) { 'passed' } else { 'failed' }) `
                -Because 'a total that doubles without a word looks exactly like a double count; the reply has to say which it is.'

            $requested2 = if ($linked2.Ok) { [int](Get-HzProp $linked2.Result 'elements_requested') } else { 0 }
            $expected2 = $hostMeasured + 2 * ($requestedWithLink - $hostMeasured)
            Add-HzProbe -Run $run -Id 'Q2.9' -Name 'a file placed twice contributes its elements twice, and the arithmetic adds up' `
                -Expected "elements_requested = host ($hostMeasured) + 2 x linked ($($requestedWithLink - $hostMeasured)) = $expected2" `
                -Observed "elements_requested=$requested2" `
                -Status $(if ($requested2 -eq $expected2) { 'passed' } else { 'failed' })
        }

        # ---- Q4.3: THE SAME FILE PLACED TWICE, AT DIFFERENT TRANSFORMS. The
        # probes above place the second instance where Revit places it - at the
        # type origin - so the transforms they compared were equal by
        # construction. Moving one is what makes each placement's transform its
        # own, and it has to happen HERE: the run removes its link a few lines
        # below, and a moved instance that no longer exists cannot be measured.
        if (-not $instanceB -or -not $linkTypeId) {
    Add-HzFixtureMissing -Id 'Q4.3' -Name 'two placements of one linked file at DIFFERENT transforms' `
        -Needs 'the two placements of phase 2; this run did not get them.'
}
else {
    $move = Invoke-HzWrite -Run $run -Tool 'horizun_transform_elements' -Label 'move-second-placement' -AllowRefusal `
        -Arguments @{
            target_document = $Document; units = 'mm'
            operations = @(@{ operation = 'move'; element_ids = @([long]$instanceB); vector = @(25000.0, 0.0, 0.0) })
        }
    if (-not $move.Ok) {
        Add-HzProbe -Run $run -Id 'Q4.3' -Name 'two placements of one linked file at DIFFERENT transforms' `
            -Expected 'the second placement moves 25 m and the takeoff reports a different transform for it' `
            -Observed ('the move was refused: ' + (Limit-HzText $(if ($move.Apply) { $move.Apply.Text } else { $move.Dry.Text }) 260)) `
            -Status 'unverified'
    }
    else {
        $movedTake = Invoke-HzTakeoff -Quantities @(@{ name = 'each'; source = 'count'; unit = 'un' }) `
            -IncludeLinks -Top 4000 -Label 'takeoff-moved-placement'
        $movedDocs = @(if ($movedTake.Ok) { Get-HzProp $movedTake.Result 'documents' } else { @() })
        $placements = @($movedDocs | Where-Object { [string](Get-HzProp $_ 'kind') -eq 'link' -and
                                                    [string](Get-HzProp $_ 'link_instance_id') -in @($instanceA, $instanceB) })
        $identities = @()
        $origins = @()
        foreach ($pl in $placements) {
            $tr = Get-HzProp $pl 'transform'
            $identities += [bool](Get-HzProp $tr 'is_identity')
            $origins += ((@(Get-HzProp $tr 'origin_m') | ForEach-Object { [string]$_ }) -join ',')
        }
        $distinctOrigins = @($origins | Sort-Object -Unique)
        $movedRows = @(Get-HzTakeoffRows $movedTake)
        $rowsA = @($movedRows | Where-Object { [string](Get-HzProp $_ 'link_instance_id') -eq $instanceA })
        $rowsB = @($movedRows | Where-Object { [string](Get-HzProp $_ 'link_instance_id') -eq $instanceB })
        $rowIds = @($movedRows | ForEach-Object { Get-HzRowIdentity $_ })
        $distinctRows = @($rowIds | Sort-Object -Unique)
        Add-HzProbe -Run $run -Id 'Q4.3' -Name 'two placements of one linked file at DIFFERENT transforms are told apart' `
            -Expected 'two link placements, exactly one at the identity transform, different origins, the same element count each, and no row identity shared between them' `
            -Observed ("placements={0} identity_flags=[{1}] distinct_origins={2} rows_a={3} rows_b={4} distinct_row_identities={5}/{6}" -f
                       $placements.Count, ($identities -join ','), $distinctOrigins.Count,
                       $rowsA.Count, $rowsB.Count, $distinctRows.Count, $rowIds.Count) `
            -Status $(if ($movedTake.Ok -and $placements.Count -eq 2 -and $distinctOrigins.Count -eq 2 -and
                          $rowsA.Count -gt 0 -and $rowsA.Count -eq $rowsB.Count -and
                          $distinctRows.Count -eq $rowIds.Count) { 'passed' } else { 'failed' }) `
            -Because 'each placement carries its own transform, and a takeoff that mixed them would attribute one placement quantities to the other.' `
            -Evidence @{ placements = $placements; rows_a = $rowsA.Count; rows_b = $rowsB.Count }
    }
}

        # ---- the link UNLOADED. ---------------------------------------------
        $unload = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'link-unload' -AllowRefusal -Arguments @{
            operation = 'unload'; link_type_id = $linkTypeId; target_document = $Document
        }
        if (-not $unload.Ok) {
            $why = if ($unload.Apply) { Limit-HzText $unload.Apply.Text 260 } else { Limit-HzText $unload.Dry.Text 260 }
            foreach ($id in @('Q2.10', 'Q2.11')) {
                Add-HzProbe -Run $run -Id $id -Name 'a link that is NOT loaded' -Expected 'the link unloaded' `
                    -Observed "the unload did not happen: $why" -Status 'unverified'
            }
        }
        else {
            $unloaded = Invoke-HzTakeoff -Quantities $countQ -IncludeLinks -Top 4000 -Label 'takeoff-unloaded'
            $notLoaded = @(if ($unloaded.Ok) { Get-HzProp $unloaded.Result 'links_not_loaded' } else { @() })
            $notLoadedIds = @($notLoaded | ForEach-Object { [string](Get-HzProp $_ 'link_instance_id') })
            $expectedNotLoaded = @($instanceA, $instanceB | Where-Object { $_ })
            $namedAll = @($expectedNotLoaded | Where-Object { $notLoadedIds -notcontains $_ }).Count -eq 0
            Add-HzProbe -Run $run -Id 'Q2.10' -Name 'an UNLOADED link is named in links_not_loaded, once per placement' `
                -Expected ("every placement this run created (" + ($expectedNotLoaded -join ',') +
                           ") appears in links_not_loaded with state not_loaded and a means sentence") `
                -Observed ("links_not_loaded: " + ($notLoadedIds -join ',')) `
                -Status $(if ($unloaded.Ok -and $namedAll -and $notLoaded.Count -ge 1 -and
                              @($notLoaded | Where-Object { [string](Get-HzProp $_ 'state') -ne 'not_loaded' }).Count -eq 0 -and
                              @($notLoaded | Where-Object { -not (Get-HzProp $_ 'means') }).Count -eq 0) { 'passed' } else { 'failed' }) `
                -Because 'a placement dropped from the list is a placement whose absence nobody was told about.' `
                -Evidence @{ links_not_loaded = $notLoaded; expected_instances = $expectedNotLoaded }

            $requested3 = if ($unloaded.Ok) { [int](Get-HzProp $unloaded.Result 'elements_requested') } else { -1 }
            $unloadedRows = @(Get-HzTakeoffRows $unloaded | Where-Object { $null -ne (Get-HzProp $_ 'link_instance_id') })
            Add-HzProbe -Run $run -Id 'Q2.11' -Name "an unloaded link's elements are ABSENT, not counted as zero, and coverage says so" `
                -Expected "no linked rows at all, elements_requested back to the host's $hostMeasured, and coverage_complete false" `
                -Observed ("linked rows: $($unloadedRows.Count); elements_requested=$requested3; coverage_complete=$(Get-HzProp $unloaded.Result 'coverage_complete')") `
                -Status $(if ($unloaded.Ok -and $unloadedRows.Count -eq 0 -and $requested3 -eq $hostMeasured -and
                              $false -eq (Get-HzProp $unloaded.Result 'coverage_complete')) { 'passed' } else { 'failed' }) `
                -Because 'a link that was not measured and a link with nothing in it render identically unless the reply separates them.'

            # Put it back, so the document this run was lent is left as it was
            # found. Nothing is saved either way; this is courtesy, not cleanup.
            $null = Invoke-HzWrite -Run $run -Tool 'horizun_manage_links' -Label 'link-reload' -AllowRefusal -Arguments @{
                operation = 'reload'; link_type_id = $linkTypeId; target_document = $Document
            }
        }

        # And take the link back out. Best effort, and NOT a probe: the document is
        # never saved, so these placements die with the session either way - but a
        # later harness in the same Revit should not have to know about them.
        $removed = Invoke-HzWrite -Run $run -Tool 'horizun_delete_verified' -Label 'link-remove' -AllowRefusal -Arguments @{
            mode = 'ids'; ids = @($linkTypeId); target_document = $Document
            transaction_name = 'Horizun live: remove the run''s link'
        }
        Add-HzNote -Run $run -Text ("the link this run added was " +
            $(if ($removed.Ok) { 'deleted again' } else { 'left in the unsaved document; close it without saving' }))
    }
}

# -----------------------------------------------------------------------------
# PHASE 3 - the budget comparison, its destinations, and the permission rule.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 3 - the comparison against a disposable budget' -ForegroundColor Cyan

$takeoffPath = $null
$baselinePath = $null

if ($hostCount -ge 2) {
    # The takeoff the comparison joins against: one quantity that reads, one that
    # cannot. That second one is the whole point of the not_comparable probe.
    $forBudget = Invoke-HzTakeoff -Top 4000 -Label 'takeoff-for-budget' -Quantities @(
        @{ name = 'volume'; source = 'geometry_volume'; unit = 'm3' },
        @{ name = 'each';   source = 'count';           unit = 'un' },
        @{ name = 'gap';    source = 'parameter'; parameter = $missingParameter; unit = 'm3' }
    )
    if ($forBudget.Ok) {
        $takeoffPath = Join-Path $work 'takeoff.json'
        ($forBudget.Result | ConvertTo-Json -Depth 40) | Set-Content -LiteralPath $takeoffPath -Encoding utf8

        # The model's own numbers, so the baseline can be built to MEAN something:
        # one code matching within tolerance, one deliberately off, one the model
        # does not have at all.
        $volA = [double](Get-HzPath $forBudget.Result @('by_code', 'HZ-A', 'quantities', 'volume', 'known_total'))
        $volB = [double](Get-HzPath $forBudget.Result @('by_code', 'HZ-B', 'quantities', 'volume', 'known_total'))
        $baselinePath = Join-Path $work 'baseline.xlsx'
        $null = New-HzMinimalWorkbook -Path $baselinePath -Sheet 'Presupuesto'
        $seed = Invoke-HzHostTool -Run $run -Tool 'horizun_excel_write_rows' -Label 'baseline-seed' -Profile 'full_write' -Arguments @{
            file_path = $baselinePath
            sheet = 'Presupuesto'
            idempotency_key = (New-HzKey $run 'baseline')
            rows = @(
                @('Codigo', 'Descripcion', 'Und', 'Cantidad', 'Precio'),
                @('HZ-A', 'Codigo que coincide', 'm3', $volA, 100.0),
                @('HZ-B', 'Codigo que difiere', 'm3', ($volB * 2.0 + 1.0), 50.0),
                @('HZ-Z', 'Codigo que el modelo no tiene', 'm3', 7.0, 25.0)
            )
        }
        if (-not $seed.Ok) { $baselinePath = $null }
        else { $run.Fixture['baseline_sha256'] = [string](Get-HzProp $seed.Result 'sha256_after') }
    }
}

function New-HzBudgetArgs {
    param([hashtable]$Extra = @{}, [string]$QuantityField = 'volume')
    $a = @{
        model_rows_path = $takeoffPath
        baseline = @{
            file_path = $baselinePath
            sheet = 'Presupuesto'
            header_row = 1
            columns = @{ code = 'Codigo'; description = 'Descripcion'; unit = 'Und'; quantity = 'Cantidad'; unit_price = 'Precio' }
        }
        mapping = @{
            code_field = 'classification_code'
            quantity_field = $QuantityField
            tolerances = @{ quantity_pct = 1.0 }
        }
    }
    foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] }
    $a
}

if (-not $takeoffPath -or -not $baselinePath) {
    foreach ($id in @('Q3.1', 'Q3.2', 'Q3.3', 'Q3.4', 'Q3.5', 'Q3.6', 'Q3.7', 'Q3.8')) {
        Add-HzProbe -Run $run -Id $id -Name 'the budget comparison' `
            -Expected 'a takeoff and a disposable baseline workbook to compare it against' `
            -Observed 'one of them could not be produced on this machine' -Status 'unverified'
    }
}
else {
    # ---- Q3.1 the comparison itself. -----------------------------------------
    $compare = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare' -Profile 'full_write' `
                                 -Arguments (New-HzBudgetArgs)
    $lines = @(if ($compare.Ok) { Get-HzPath $compare.Result @('comparison', 'lines') } else { @() })
    $byCode = @{}
    foreach ($line in $lines) { $byCode[[string](Get-HzProp $line 'code')] = $line }
    Add-HzProbe -Run $run -Id 'Q3.1' -Name 'the comparison joins the takeoff to the budget, code by code' `
        -Expected 'HZ-A unchanged, HZ-B modified, HZ-Z removed, and every line carrying its trace' `
        -Observed (($lines | ForEach-Object { "$(Get-HzProp $_ 'code')=$(Get-HzProp $_ 'status')" }) -join ' ') `
        -Status $(if ($compare.Ok -and
                      [string](Get-HzPath $byCode['HZ-A'] @('status')) -eq 'unchanged' -and
                      [string](Get-HzPath $byCode['HZ-B'] @('status')) -eq 'modified' -and
                      [string](Get-HzPath $byCode['HZ-Z'] @('status')) -eq 'removed') { 'passed' } else { 'failed' }) `
        -Evidence @{ settings_root = 'a DISPOSABLE full_write settings root this run created; the machine settings were not touched'
                     lines = $lines }

    # ---- Q3.2 an incomplete read is NOT compared. ----------------------------
    $gap = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-gap' -Profile 'full_write' `
                             -Arguments (New-HzBudgetArgs -QuantityField 'gap')
    $gapLines = @(if ($gap.Ok) { Get-HzPath $gap.Result @('comparison', 'lines') } else { @() })
    $gapA = @($gapLines | Where-Object { [string](Get-HzProp $_ 'code') -eq 'HZ-A' }) | Select-Object -First 1
    # The reason is NOT pinned to one string: which coverage failure a code lands
    # in depends on how many of its elements read, and all four of these mean the
    # same thing here - it was not compared. Pinning one would be asserting the
    # fixture rather than the rule.
    $coverageReasons = @('model_absent', 'partial_coverage', 'incomplete_read', 'model_invalid')
    Add-HzProbe -Run $run -Id 'Q3.2' -Name 'a code whose quantity could not be read is not_comparable, never compared' `
        -Expected ("HZ-A comes back not_comparable with a coverage reason (" + ($coverageReasons -join ' | ') +
                   ") and no quantity_delta at all") `
        -Observed $(if ($gapA) { "status=$(Get-HzProp $gapA 'status') reason=$(Get-HzProp $gapA 'reason')" } else { Limit-HzText $gap.Text 240 }) `
        -Status $(if ($gapA -and [string](Get-HzProp $gapA 'status') -eq 'not_comparable' -and
                      $coverageReasons -contains [string](Get-HzProp $gapA 'reason') -and
                      $null -eq (Get-HzProp $gapA 'quantity_delta')) { 'passed' } else { 'failed' }) `
        -Because 'a sum over the elements that happened to read is a lower bound wearing the code name, and subtracting it from a budget invents a saving.' `
        -Evidence @{ line = $gapA }

    # ---- Q3.2b and the opt-in does not rescue a total nobody measured. -------
    $optIn = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-gap-optin' -Profile 'full_write' `
                               -Arguments (New-HzBudgetArgs -QuantityField 'gap' -Extra @{
                                   mapping = @{
                                       code_field = 'classification_code'
                                       quantity_field = 'gap'
                                       tolerances = @{ quantity_pct = 1.0 }
                                       rules = @{ compare_partial_coverage = $true }
                                   }
                               })
    $optInLines = @(if ($optIn.Ok) { Get-HzPath $optIn.Result @('comparison', 'lines') } else { @() })
    $optInA = @($optInLines | Where-Object { [string](Get-HzProp $_ 'code') -eq 'HZ-A' }) | Select-Object -First 1
    Add-HzProbe -Run $run -Id 'Q3.2b' -Name 'compare_partial_coverage compares a FRAGMENT, and still refuses a total nobody measured' `
        -Expected 'HZ-A stays not_comparable even with the opt-in, because no element carried the quantity at all' `
        -Observed $(if ($optInA) { "status=$(Get-HzProp $optInA 'status') reason=$(Get-HzProp $optInA 'reason')" } else { Limit-HzText $optIn.Text 240 }) `
        -Status $(if ($optInA -and [string](Get-HzProp $optInA 'status') -eq 'not_comparable') { 'passed' } else { 'failed' }) `
        -Because 'the opt-in says "compare the part that read"; where nothing read there is no part, and a zero would be a fabrication rather than a fragment.'

    # ---- Q3.3 an undeclared unit pair is unit_incompatible. ------------------
    $units = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-units' -Profile 'full_write' `
                               -Arguments (New-HzBudgetArgs -QuantityField 'each')
    $unitLines = @(if ($units.Ok) { Get-HzPath $units.Result @('comparison', 'lines') } else { @() })
    $unitA = @($unitLines | Where-Object { [string](Get-HzProp $_ 'code') -eq 'HZ-A' }) | Select-Object -First 1
    Add-HzProbe -Run $run -Id 'Q3.3' -Name "a model quantity in 'un' against a baseline in 'm3' is unit_incompatible, never converted" `
        -Expected 'not_comparable with reason unit_incompatible, because no {from,to,factor} was declared' `
        -Observed $(if ($unitA) { "status=$(Get-HzProp $unitA 'status') reason=$(Get-HzProp $unitA 'reason')" } else { Limit-HzText $units.Text 240 }) `
        -Status $(if ($unitA -and [string](Get-HzProp $unitA 'status') -eq 'not_comparable' -and
                      [string](Get-HzProp $unitA 'reason') -eq 'unit_incompatible') { 'passed' } else { 'failed' })

    # ---- Q3.4 the Excel destination, read back row by row. -------------------
    $reportPath = Join-Path $work 'comparison.xlsx'
    $written = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-excel' -Profile 'full_write' `
                                 -Arguments (New-HzBudgetArgs -Extra @{
                                     outputs = @{ excel = @{ file_path = $reportPath; sheet = 'Comparison' } }
                                     idempotency_key = (New-HzKey $run 'excel-out')
                                 })
    $destination = @(if ($written.Ok) { Get-HzProp $written.Result 'destinations' }) | Select-Object -First 1
    $readBack = $null
    if ($written.Ok -and (Test-Path -LiteralPath $reportPath)) {
        $readBack = Invoke-HzHostTool -Run $run -Tool 'horizun_excel_read_rows' -Label 'read-report' -Profile 'read_only' -Arguments @{
            file_path = $reportPath; sheet = 'Comparison'
        }
    }
    $sheetRows = @(if ($readBack -and $readBack.Ok) { Get-HzProp $readBack.Result 'rows' } else { @() })
    $header = @(if ($sheetRows.Count -gt 0) { $sheetRows[0] })
    $sheetByCode = @{}
    for ($i = 1; $i -lt $sheetRows.Count; $i++) {
        $cells = @($sheetRows[$i])
        if ($cells.Count -gt 1) { $sheetByCode[[string]$cells[1]] = $cells }
    }
    $rowsAgree = $true
    foreach ($code in @('HZ-A', 'HZ-B', 'HZ-Z')) {
        if (-not $sheetByCode.ContainsKey($code)) { $rowsAgree = $false; continue }
        if ([string]$sheetByCode[$code][0] -ne [string](Get-HzPath $byCode[$code] @('status'))) { $rowsAgree = $false }
    }
    Add-HzProbe -Run $run -Id 'Q3.4' -Name 'the Excel output is written, and reading it back agrees with the comparison row by row' `
        -Expected 'destination written, then a header row plus one row per code whose status matches the structured reply' `
        -Observed $(if ($destination) {
                "status=$(Get-HzProp $destination 'status') rows_written=$(Get-HzPath $destination @('evidence','rows_written')) read back=$($sheetRows.Count) row(s)"
            } else { Limit-HzText $written.Text 240 }) `
        -Status $(if ($destination -and [string](Get-HzProp $destination 'status') -eq 'written' -and
                      $sheetRows.Count -eq ($lines.Count + 1) -and
                      ($header.Count -gt 1 -and [string]$header[0] -eq 'status' -and [string]$header[1] -eq 'code') -and
                      $rowsAgree) { 'passed' } else { 'failed' }) `
        -Evidence @{ destination = $destination; header = $header; rows_read_back = $sheetRows.Count }

    # ---- Q3.5 / Q3.6 THE PERMISSION RULE, both directions. -------------------
    $readOnlyNote = 'answered by a server whose HORIZUN_DATA_ROOT this run pointed at a DISPOSABLE settings root ' +
                    'holding permission_profile=read_only. The machine settings file was never opened for writing.'

    $noOutputs = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-read-only' -Profile 'read_only' `
                                   -Arguments (New-HzBudgetArgs)
    $roLines = @(if ($noOutputs.Ok) { Get-HzPath $noOutputs.Result @('comparison', 'lines') } else { @() })
    Add-HzProbe -Run $run -Id 'Q3.5' -Name 'a comparison with NO outputs runs under a read_only profile' `
        -Expected 'the full comparison comes back, and destinations is empty' `
        -Observed $(if ($noOutputs.Ok) { "$($roLines.Count) line(s), destinations: $(@(Get-HzProp $noOutputs.Result 'destinations').Count)" } else { Limit-HzText $noOutputs.Text 240 }) `
        -Status $(if ($noOutputs.Ok -and $roLines.Count -eq $lines.Count -and
                      @(Get-HzProp $noOutputs.Result 'destinations').Count -eq 0) { 'passed' } else { 'failed' }) `
        -Because 'reading a budget and writing one are different acts; hiding the reading because the same tool can write refuses a machine the arithmetic it is entitled to.' `
        -Evidence @{ settings_root = $readOnlyNote }

    $refusedExcel = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-read-only-excel' -Profile 'read_only' `
                                      -Arguments (New-HzBudgetArgs -Extra @{
                                          outputs = @{ excel = @{ file_path = (Join-Path $work 'refused.xlsx') } }
                                          idempotency_key = (New-HzKey $run 'refused')
                                      })
    Add-HzRefusalProbe -Run $run -Id 'Q3.6' -Name 'the SAME call with a destination is refused under that profile, naming it' `
        -Call $refusedExcel -MustMatch 'permission_profile=read_only' `
        -Expected 'refused, naming permission_profile=read_only, with nothing read and nothing written'
    Add-HzProbe -Run $run -Id 'Q3.6b' -Name 'and the refused destination file was never created' `
        -Expected 'refused.xlsx does not exist' `
        -Observed $(if (Test-Path -LiteralPath (Join-Path $work 'refused.xlsx')) { 'the file is there' } else { 'no such file' }) `
        -Status $(if (Test-Path -LiteralPath (Join-Path $work 'refused.xlsx')) { 'failed' } else { 'passed' }) `
        -Evidence @{ settings_root = $readOnlyNote }

    # ---- Q3.7 Power BI, DRY RUN ONLY. ---------------------------------------
    $dry = Invoke-HzHostTool -Run $run -Tool 'horizun_budget_compare' -Label 'compare-power-bi-dry' -Profile 'full_write' `
                             -Arguments (New-HzBudgetArgs -Extra @{
                                 outputs = @{ power_bi = @{
                                     dataset_id = '00000000-0000-0000-0000-000000000000'
                                     table = $PowerBiTable
                                     dry_run = $true
                                 } }
                                 idempotency_key = (New-HzKey $run 'pbi-dry')
                             })
    $pbi = @(if ($dry.Ok) { Get-HzProp $dry.Result 'destinations' }) | Select-Object -First 1
    $evidence = if ($pbi) { Get-HzProp $pbi 'evidence' } else { $null }
    $note = if ($evidence) { [string](Get-HzProp $evidence 'note') } else { '' }
    Add-HzProbe -Run $run -Id 'Q3.7' -Name 'Power BI is validated and NOTHING is sent: no token requested, no row pushed' `
        -Expected "the destination is 'skipped' for dry_run, the push reports dry_run true, rows_validated > 0, and its own note says no token was requested and no row was sent" `
        -Observed $(if ($pbi) {
                "status=$(Get-HzProp $pbi 'status') dry_run=$(Get-HzProp $evidence 'dry_run') rows_validated=$(Get-HzProp $evidence 'rows_validated') credentials_configured=$(Get-HzProp $evidence 'credentials_configured')"
            } else { Limit-HzText $dry.Text 240 }) `
        -Status $(if ($pbi -and [string](Get-HzProp $pbi 'status') -eq 'skipped' -and
                      $true -eq (Get-HzProp $evidence 'dry_run') -and
                      [int](Get-HzProp $evidence 'rows_validated') -gt 0 -and
                      $note -match 'No token was requested and no row was sent') { 'passed' } else { 'failed' }) `
        -Because 'a dry run that quietly asked Microsoft for a token would have reached the network while reporting that it had not.' `
        -Evidence @{ destination = $pbi }

    # ---- Q3.8 the destination nobody has authorised. -------------------------
    if ($PowerBiDatasetId) {
        Add-HzProbe -Run $run -Id 'Q3.8' -Name 'an authorised Power BI push' `
            -Expected 'a real push to the destination the integrator supplied' `
            -Observed ("a dataset was supplied ($PowerBiDatasetId) but this harness does not push. Pushing rows into a " +
                       "shared semantic model is the integrator's decision and is made deliberately, not by a " +
                       "verification run that happened to be given an id.") `
            -Status 'not_covered' `
            -Because 'this run is read-and-rehearse; the one irreversible act on its surface is left to a person.'
    }
    else {
        Add-HzFixtureMissing -Id 'Q3.8' -Name 'an authorised Power BI destination to push into' `
            -Needs ('a test push dataset nobody depends on: its dataset id and (if it lives in a workspace) the ' +
                    'workspace id, a table in it whose columns match the comparison sheet header ' +
                    '(run_id, status, code, description, unit, baseline_quantity, model_quantity, quantity_delta, ' +
                    'quantity_delta_pct, unit_price, baseline_amount, model_amount, amount_delta, elements, reason, ' +
                    'trace), plus credentials in the MCP SERVER environment - either HORIZUN_POWER_BI_ACCESS_TOKEN, ' +
                    'or HORIZUN_POWER_BI_TENANT_ID + HORIZUN_POWER_BI_CLIENT_ID + HORIZUN_POWER_BI_CLIENT_SECRET. ' +
                    'Credentials are never accepted in tool arguments. Pass the ids as -PowerBiWorkspaceId and ' +
                    '-PowerBiDatasetId; even then this harness only rehearses, and the push stays a human decision.')
    }
}

# -----------------------------------------------------------------------------
# What this run did not build, named exactly.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '  PHASE 4 - what is missing, named' -ForegroundColor Cyan

# NOT a missing fixture. Nothing is absent from this machine: the linked models
# are here, the reads run, and every one of them succeeded. What did not happen is
# Revit THROWING, and a condition that was not observed is not a resource that is
# missing - calling it fixture_missing would file it beside the ACC model and the
# second user, which are genuinely unobtainable here.
#
# The TRANSLATION of a failed read is proved structurally, over the real function
# with a substituted measurement (TakeoffReadingRules, TakeoffUnreadableReadingTests):
# Failed becomes unreadable, NotApplicable becomes absent, a measured zero stays a
# number, and the reading keeps document, link instance and element id.
Add-HzUnverified -Id 'Q4.1' -Name 'a link whose elements cannot be READ (unreadable, not absent)' `
    -Expected 'a linked element whose quantity read THROWS, so the unreadable path is exercised by Revit rather than by a substituted measurement' `
    -Observed ('every read in this run returned a value or a typed absence; Revit raised no exception. The ' +
               'mapping from a failed read to unreadable is proved offline by TakeoffUnreadableReadingTests - ' +
               'what is NOT proved is Revit producing the failure.')

# ---- Q4.2: A LINK WITH A CLOSED WORKSET, and what can actually be observed.
#
# MEASURED, Revit 2026, 2026-09-04: a link created with
# RevitLinkOptions(false, WorksetConfiguration) closing HZ_WS_CLOSED by id reports,
# in the LINKED document, every workset IsOpen=true - and hands over the 392
# elements of that workset, exactly as when the same type is reloaded with
# OpenAllWorksets. Both censuses read 9800 elements. So the API exposes no way to
# read back the configuration a link was loaded with, and IsOpen inside a link is
# not evidence of the effective state.
#
# This probe therefore reports the two facts SEPARATELY - requested_closed, which
# is what the staging asked for, and observed_closed, which is what Revit shows -
# and records not_verifiable when they disagree. It does NOT fail the product for
# a property the API does not expose, and it does NOT pass on a request nobody
# could confirm.
if (-not (Test-Path -LiteralPath $ClosedWorksetLink)) {
    Add-HzFixtureMissing -Id 'Q4.2' -Name 'a link with a CLOSED workset' `
        -Needs ("a workshared central to link with a workset closed: $ClosedWorksetLink is not on this machine.")
}
else {
    $wsPy = Join-Path $run.WorkDir 'link-closed-workset.py'
    @"
# Link a workshared CENTRAL with one workset CLOSED, then measure what Revit shows
# through the link: the workset flags AND the element census per workset, which is
# the only observable that could distinguish a closed workset from an open one.
from Autodesk.Revit.DB import (RevitLinkOptions, RevitLinkType, RevitLinkInstance, ModelPathUtils,
                               Transaction, FilteredElementCollector, FilteredWorksetCollector,
                               WorksetKind, WorksetConfiguration, WorksetConfigurationOption,
                               WorksharingUtils, ElementWorksetFilter)

TARGET = r'$ClosedWorksetLink'
CLOSE = '$ClosedWorksetName'
out = {'status': 'failed', 'target': TARGET, 'close': CLOSE, 'type_id': None, 'instance_id': None,
       'requested_closed': None, 'worksets_seen': [], 'observed': None, 'reload_status': None,
       'observed_all_open': None, 'why': None}

def eid(x):
    return None if x is None else (x.IntegerValue if hasattr(x, 'IntegerValue') else int(x.Value))

def census(linkDoc):
    if linkDoc is None:
        return None
    rows = []
    for w in FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset):
        try:
            n = FilteredElementCollector(linkDoc).WherePasses(
                ElementWorksetFilter(w.Id)).WhereElementIsNotElementType().GetElementCount()
        except Exception as ex:
            n = -1
        rows.append({'name': w.Name, 'id': eid(w.Id), 'is_open': w.IsOpen, 'elements': n})
    return {'worksets': rows,
            'elements_total': FilteredElementCollector(linkDoc).WhereElementIsNotElementType().GetElementCount()}

try:
    mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(TARGET)
    closedIds = []
    for pv in WorksharingUtils.GetUserWorksetInfo(mp):
        out['worksets_seen'].append(pv.Name)
        if pv.Name == CLOSE:
            closedIds.append(pv.Id)
    out['requested_closed'] = [eid(w) for w in closedIds]
    cfg = WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets)
    if closedIds:
        cfg.Close(closedIds)
    t = Transaction(doc, 'Horizun live fixture: link with a closed workset')
    try:
        t.Start()
        res = RevitLinkType.Create(doc, mp, RevitLinkOptions(False, cfg))
        inst = RevitLinkInstance.Create(doc, res.ElementId)
        t.Commit()
    finally:
        if t.HasStarted() and not t.HasEnded():
            t.RollBack()
    out['type_id'] = eid(res.ElementId)
    out['instance_id'] = eid(inst.Id)
    out['observed'] = census(inst.GetLinkDocument())
    # THE CONTROL: the same type, reloaded with everything open. LoadFrom is a
    # document-level load and Revit refuses it inside a transaction.
    try:
        loaded = doc.GetElement(res.ElementId).LoadFrom(
            mp, WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets))
        out['reload_status'] = str(loaded.LoadResult) if loaded is not None else None
        out['observed_all_open'] = census(inst.GetLinkDocument())
    except Exception as exLoad:
        out['reload_status'] = 'threw: ' + str(exLoad)
    out['status'] = 'self_reported_verified' if closedIds else 'partial'
    if not closedIds:
        out['why'] = 'the central does not carry a workset named ' + CLOSE
except Exception as ex:
    out['why'] = str(ex)
__output__ = out
"@ | Set-Content -LiteralPath $wsPy -Encoding utf8

    $wsCall = Invoke-HzTool -Run $run -Tool 'horizun_execute_python' -Label 'link-closed-workset' -TimeoutSec 900 `
        -Arguments @{ code_path = $wsPy; target_document = $Document; idempotency_key = (New-HzKey $run 'wslink') }
    $wsOut = if ($wsCall.Ok) { Get-HzProp $wsCall.Result 'output' } else { $null }
    if (-not $wsOut -or [string](Get-HzProp $wsOut 'status') -ne 'self_reported_verified') {
        Add-HzFixtureMissing -Id 'Q4.2' -Name 'a link with a CLOSED workset' `
            -Needs ('the link could not be staged with a closed workset: ' +
                    (Limit-HzText $(if ($wsOut) { [string](Get-HzProp $wsOut 'why') } else { $wsCall.Text }) 260))
    }
    else {
        $requested = @(Get-HzProp $wsOut 'requested_closed')
        $observed = Get-HzProp $wsOut 'observed'
        $control = Get-HzProp $wsOut 'observed_all_open'
        # THE WITNESS: how many elements the workset that was asked to close holds,
        # with the request applied and with everything open. An absence is only
        # evidence if the workset has something to be absent.
        $witnessClosed = -1; $witnessOpen = -1; $flagClosed = $null
        foreach ($w in @(Get-HzProp $observed 'worksets')) {
            if ([string](Get-HzProp $w 'name') -eq $ClosedWorksetName) {
                $witnessClosed = [int](Get-HzProp $w 'elements'); $flagClosed = (Get-HzProp $w 'is_open')
            }
        }
        foreach ($w in @(Get-HzProp $control 'worksets')) {
            if ([string](Get-HzProp $w 'name') -eq $ClosedWorksetName) { $witnessOpen = [int](Get-HzProp $w 'elements') }
        }
        $wsTake = Invoke-HzTakeoff -Quantities @(@{ name = 'each'; source = 'count'; unit = 'un' }) `
            -IncludeLinks -Top 4000 -Label 'takeoff-closed-workset'
        $wsInstance = [string](Get-HzProp $wsOut 'instance_id')
        $wsDoc = $null
        foreach ($docRow in @(if ($wsTake.Ok) { Get-HzProp $wsTake.Result 'documents' } else { @() })) {
            if ([string](Get-HzProp $docRow 'link_instance_id') -eq $wsInstance) { $wsDoc = $docRow }
        }
        $wsVis = if ($wsDoc) { Get-HzProp $wsDoc 'visibility_coverage' } else { $null }
        $saysLimit = $null -ne $wsVis -and [string](Get-HzProp $wsVis 'linked_document_means') -match 'NOT evidence'

        if ($witnessOpen -le 0) {
            Add-HzFixtureMissing -Id 'Q4.2' -Name 'a link with a CLOSED workset' `
                -Needs ("the workset $ClosedWorksetName holds no elements even with everything open, so its absence " +
                        "could never be observed. A central whose closed workset carries witness elements.")
        }
        elseif ($witnessClosed -lt $witnessOpen) {
            # The configuration WAS honoured: the elements are not there.
            Add-HzProbe -Run $run -Id 'Q4.2' -Name 'a link with a CLOSED workset is observable, and the takeoff says so' `
                -Expected "the workset's elements are absent through the link ($witnessOpen with everything open) and the takeoff reports incomplete coverage" `
                -Observed ("witness_elements closed={0} open={1} is_open_flag={2} takeoff_coverage_complete={3}" -f
                           $witnessClosed, $witnessOpen, $flagClosed, (Get-HzProp $wsTake.Result 'coverage_complete')) `
                -Status $(if ((Get-HzProp $wsTake.Result 'coverage_complete') -eq $false) { 'passed' } else { 'failed' }) `
                -Because 'an absence caused by a closed workset priced as zero is the most expensive kind of wrong number.' `
                -Evidence @{ observed = $observed; control = $control; document = $wsDoc }
        }
        else {
            Add-HzProbe -Run $run -Id 'Q4.2' -Name 'a workset closed on a LINK is not observable through the API' `
                -Expected 'either the elements of the closed workset are absent through the link, or the reply says the effective state cannot be read' `
                -Observed ("requested_closed=[{0}] witness_elements closed={1} open={2} is_open_flag={3} total closed={4} open={5} reload={6}" -f
                           ($requested -join ','), $witnessClosed, $witnessOpen, $flagClosed,
                           (Get-HzProp $observed 'elements_total'), (Get-HzProp $control 'elements_total'),
                           (Get-HzProp $wsOut 'reload_status')) `
                -Status 'unverified' `
                -Because ('MEASURED: the workset was requested closed by id and Revit hands over its ' + $witnessOpen +
                          ' elements anyway, with IsOpen=true, identical to the same type reloaded with every ' +
                          'workset open. The API exposes no way to read back a link''s load configuration, so the ' +
                          'EFFECTIVE state is not observable - which the reply now says on every link row rather ' +
                          'than claiming a coverage it cannot support. Reporting this as passed would be a claim ' +
                          'about a property nobody measured; reporting it as a missing fixture would blame a ' +
                          'resource that is present.') `
                -Evidence @{ observed = $observed; control = $control; document = $wsDoc
                             reply_declares_the_limit = $saysLimit }
            Add-HzProbe -Run $run -Id 'Q4.2b' -Name 'the reply DECLARES that a link''s workset state is not evidence' `
                -Expected 'every linked document row carries linked_document_means saying an absence in a link is NOT evidence of a closed workset' `
                -Observed $(if ($null -eq $wsVis) { 'the staged link was not measured by the takeoff' }
                            else { "linked_document_means=" + (Limit-HzText ([string](Get-HzProp $wsVis 'linked_document_means')) 160) }) `
                -Status $(if ($saysLimit) { 'passed' } else { 'failed' }) `
                -Because 'a limitation the reply does not state is a limitation the reader will not know about.'
        }
    }
}

# =============================================================================
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir

$c = Get-HzCounts $run
$total = $c.passed + $c.failed + $c.unverified + $c.not_covered + $c.fixture_missing +
         $c.not_assessable + $c.not_applicable + $c.available + $c.implemented_not_live_verified
if ($total -ne $run.Probes.Count) {
    Write-Host ("  BUCKETS DO NOT ADD UP: {0} probes, {1} counted" -f $run.Probes.Count, $total) -ForegroundColor Red
    exit 3
}
exit $done.ExitCode
