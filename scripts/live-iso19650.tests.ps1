#Requires -Version 5.1
<#
  The ISO 19650 live probes, exercised WITHOUT Revit.

  Four of five matrix runs of 2026 were lost to harness errata, not to Revit.
  So every function scripts/verify-live.ps1 calls in its ISO section runs here
  first, against a FAKE BRIDGE that answers in the shapes the real one does
  (structuredContent parsed from JSON, refusals as isError text) and writes the
  files the real one would write. Each product defect the probes exist to catch
  is simulated once, and the probe must turn it into a failure - a probe that
  cannot fail is not a probe.

    pwsh -NoProfile -File scripts/live-iso19650.tests.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-iso19650.probes.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hz-iso-probe-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$repoRoot = Split-Path -Parent $PSScriptRoot
$script:failed = 0
function Assert($name, $condition) {
    if ($condition) { Write-Host "PASS $name" }
    else { Write-Host "FAIL $name" -ForegroundColor Red; $script:failed++ }
}

# -----------------------------------------------------------------------------
# The fake bridge. $script:fx holds the scenario: which defect to simulate, the
# Revit year, the permission profile and what the "model" contains.
# -----------------------------------------------------------------------------
function Reset-Fake {
    param([int]$Year = 2026, [string]$Defect = '', [string]$Profile = 'safe_write',
          [hashtable]$Counts = @{ OST_DuctCurves = 12; OST_PipeCurves = 3 }, [bool]$Sheet = $true)
    $script:fx = @{ year = $Year; defect = $Defect; profile = $Profile; counts = $Counts; sheet = $Sheet
                    codes_written = $false; calls = New-Object System.Collections.ArrayList; seq = 0 }
}

function Reply($data) { return @{ isError = $false; text = ($data | ConvertTo-Json -Depth 30 -Compress); data = ($data | ConvertTo-Json -Depth 30 | ConvertFrom-Json) } }
function Refuse([string]$text) { return @{ isError = $true; text = ('Error: ' + $text); data = $null } }
function Sha([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Get-FakeStem($arguments) {
    $c = $arguments.information_container
    if ($c) { return (Get-HorizunIsoContainerName $c) }
    $n = [string]$arguments.output_name
    if ($n.EndsWith('.ifc')) { $n = $n.Substring(0, $n.Length - 4) }
    return $n
}

function Write-FakeSealed([string]$file, $container, [string]$content) {
    [IO.File]::WriteAllText($file, $content)
    $sidecar = @{ schema = 'horizun.container/v1'; name = (Get-HorizunIsoContainerName $container); file = (Split-Path -Leaf $file)
                  sha256 = (Sha $file); bytes = (Get-Item -LiteralPath $file).Length; status = $container.status; revision = $container.revision
                  fields = $container.fields; naming = @{ field_order = $container.field_order; separator = '-' } }
    [IO.File]::WriteAllText($file + '.container.json', ($sidecar | ConvertTo-Json -Depth 10))
}

function Invoke-FakeDeliver($a) {
    $fx = $script:fx
    if ([string]$a.ifc_version -eq 'IFC4x3' -and ($fx.year -lt 2024 -and $fx.defect -ne 'ifc4x3-accepted')) {
        return Refuse ("This Revit ($($fx.year)) has no IFCVersion.IFC4x3. Choose another ifc_version. Nothing was exported.")
    }
    $stem = Get-FakeStem $a
    $ifc = Join-Path ([string]$a.output_folder) ($stem + '.ifc')
    if ($a.information_container -and (Test-Path -LiteralPath ($ifc + '.container.json')) -and $fx.defect -ne 'reseal-accepted') {
        return Refuse ("An information-container sidecar already exists at $ifc.container.json; a sealed delivery is never overwritten. Nothing was exported.")
    }
    $dry = ($null -eq $a.dry_run -or $a.dry_run -eq $true)
    if ($dry) {
        if ($fx.defect -eq 'dry-writes') { [IO.File]::WriteAllText($ifc, 'leaked') }
        $plan = @{ dry_run = $true; ifc_path = $ifc; output_name = $stem
                   information_container_name = $(if ($a.information_container) { $stem } else { $null })
                   options = @(@{ option = 'FileVersion'; value = [string]$a.ifc_version })
                   georeference_in_model = @{ units = 'mm'; survey_point = @{ internal_mm = @(0, 0, 0) }; project_base_point = @{ internal_mm = @(0, 0, 0) }
                                             active_location = @{ name = 'Internal'; angle_to_true_north_deg = 0 } }
                   gates_planned = @(@{ gate = 'export'; requested = $true }, @{ gate = 'information_container'; requested = ($null -ne $a.information_container) })
                   confirmation_token = ('tok-' + [guid]::NewGuid().ToString('N')) }
        if ($a.pset_mapping_path) { $plan.pset_mapping = @{ path = $a.pset_mapping_path; sha256 = (Sha $a.pset_mapping_path) } }
        if ($a.ids_path) { $plan.ids = @{ path = $a.ids_path; sha256 = (Sha $a.ids_path) } }
        return Reply $plan
    }
    if (-not $a.confirmation_token) { return Refuse 'dry_run=false needs the confirmation_token of the rehearsal. Nothing was exported.' }
    if ($a.information_container) {
        Write-FakeSealed $ifc $a.information_container ("ISO-10303-21;`nFILE_SCHEMA(('IFC4'));`nEND-ISO-10303-21;`n" + [guid]::NewGuid())
    } else { [IO.File]::WriteAllText($ifc, 'ISO-10303-21;') }
    $coded = $fx.codes_written
    $ids = $(if ($coded) { 'passed' } else { 'failed' })
    $gates = @(
        @{ gate = 'precheck'; status = $ids; requested = $true; advisory = $true },
        @{ gate = 'export'; status = 'passed'; requested = $true; advisory = $false },
        @{ gate = 'schema_header'; status = 'passed'; requested = $true; advisory = $false; evidence = @{ file_schema_family = 'IFC4' } },
        @{ gate = 'ids_validate'; status = $ids; requested = $true; advisory = $false },
        @{ gate = 'pset_mapping'; status = $ids; requested = $true; advisory = $false },
        @{ gate = 'bcf'; status = $(if ($coded) { 'skipped' } else { 'passed' }); requested = $true; advisory = $false }
    )
    if ($fx.defect -ne 'seal-gate-unreported') {
        $gates += @{ gate = 'information_container'; status = 'passed'; requested = $true; advisory = $false }
    }
    $blocking = @($gates | Where-Object { $_.requested -and -not $_.advisory -and $_.status -ne 'passed' -and $_.status -ne 'skipped' } |
                  ForEach-Object { "$($_.gate) is $($_.status)" })
    $ready = ($blocking.Count -eq 0)
    if ($fx.defect -eq 'ready-lies') { $ready = $true; $blocking = @() }
    return Reply @{ dry_run = $false; deliverable_ready = $ready; blocking = $blocking; ifc_path = $ifc; sha256 = (Sha $ifc)
                    bytes = (Get-Item -LiteralPath $ifc).Length; gates = $gates; information_container_name = $stem
                    georeference_in_file = @{ ifc_site = @{} } }
}

function Invoke-FakeExport($a) {
    $fx = $script:fx
    $requested = [string]$a.output_path
    $ext = [IO.Path]::GetExtension($requested)
    $out = Join-Path (Split-Path -Parent $requested) ((Get-HorizunIsoContainerName $a.information_container) + $ext)
    if ((Test-Path -LiteralPath ($out + '.container.json')) -and $fx.defect -ne 'export-reseal-accepted') {
        return Refuse ("An information-container sidecar already exists at $out.container.json and is never overwritten, whatever overwrite says. Nothing was exported.")
    }
    $dry = ($null -eq $a.dry_run -or $a.dry_run -eq $true)
    if ($dry) { return Reply @{ dry_run = $true; confirmation_token = ('tok-' + [guid]::NewGuid().ToString('N'))
                                information_container = @{ container_output_path = $out } } }
    if (-not $a.confirmation_token) { return Refuse 'needs the confirmation_token. Nothing was exported.' }
    Write-FakeSealed $out $a.information_container ('export ' + [guid]::NewGuid())
    return Reply @{ files = @($out); information_container = @{ name = (Get-HorizunIsoContainerName $a.information_container); file = $out
                                                                 requested_output_path = $requested } }
}

function Invoke-FakeContainer($a) {
    $fx = $script:fx
    switch ([string]$a.operation) {
        'verify' {
            $f = [string]$a.file_path
            if (-not (Test-Path -LiteralPath $f)) { return Reply @{ verdict = 'missing_file'; matches = $false } }
            if (-not (Test-Path -LiteralPath ($f + '.container.json'))) { return Reply @{ verdict = 'missing_sidecar'; matches = $false } }
            $sc = Get-Content -LiteralPath ($f + '.container.json') -Raw | ConvertFrom-Json
            $sha = Sha $f
            if ($sha -ne [string]$sc.sha256) { return Reply @{ verdict = 'modified'; matches = $false; sha256 = $sha } }
            return Reply @{ verdict = 'match'; matches = $true; sha256 = $sha }
        }
        'inspect' {
            $findings = @()
            foreach ($state in @('wip', 'shared', 'published', 'archived')) {
                $folder = Join-Path ([string]$a.root) $state
                foreach ($f in @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -notlike '*.container.json' })) {
                    if ($f.BaseName -notmatch '^HZ01-HRZ-ZZ-XX-[A-Z0-9]{2}-A-[0-9]{4}$') { $findings += @{ kind = 'name_noncompliant'; state = $state; path = $f.FullName } }
                    elseif (-not (Test-Path -LiteralPath ($f.FullName + '.container.json'))) { $findings += @{ kind = 'missing_sidecar'; state = $state; path = $f.FullName } }
                }
            }
            foreach ($d in @($a.deliverables)) {
                $kind = $(if ([string]$d.due -lt [string]$a.as_of) { 'deliverable_overdue' } else { 'deliverable_missing' })
                $findings += @{ kind = $kind; container = $d.container }
            }
            if ($fx.defect -eq 'inspect-writes') { $null = New-Item -ItemType Directory -Force -Path (Join-Path ([string]$a.root) '.horizun') }
            return Reply @{ operation = 'inspect'; coverage_complete = $true; findings = $findings; total_findings = $findings.Count; finding_counts = @{} }
        }
        'stamp' {
            if ($fx.profile -ne 'full_write' -and $fx.profile -ne 'unsafe_code') {
                return Refuse 'stamp with dry_run=false writes files outside the model, and that needs the profile: permission_profile=safe_write.'
            }
            $f = [string]$a.file_path
            $content = [IO.File]::ReadAllText($f)
            Write-FakeSealed $f $a.information_container $content
            return Reply @{ operation = 'stamp'; written = $true; verified = $true }
        }
        'transition' {
            if (-not $a.approved_by -and $fx.defect -ne 'approval-not-enforced') {
                return Refuse 'shared->published needs approved_by: the name of whoever authorised the publication. Nothing was written.'
            }
            $src = [string]$a.file_path
            if (-not (Test-Path -LiteralPath ($src + '.container.json')) -and $fx.defect -ne 'approval-not-enforced') {
                return Refuse 'The source does not match its sidecar (verdict missing_sidecar). Stamp it first (operation=stamp). Nothing was written.'
            }
            $dest = Join-Path (Join-Path ([string]$a.root) 'published') (Split-Path -Leaf $src)
            $dry = ($null -eq $a.dry_run -or $a.dry_run -eq $true)
            if ($dry) { return Reply @{ operation = 'transition'; dry_run = $true; written = $false; destination = $dest; approved_by = $a.approved_by } }
            if (($fx.profile -ne 'full_write' -and $fx.profile -ne 'unsafe_code') -or $fx.defect -eq 'refused-despite-profile') {
                return Refuse 'transition with dry_run=false writes files outside the model, and that needs the profile: permission_profile=safe_write.'
            }
            if (Test-Path -LiteralPath $dest) {
                return Reply @{ operation = 'transition'; dry_run = $false; already_transitioned = $true; written = $false; verified = $true; destination = $dest }
            }
            Copy-Item -LiteralPath $src -Destination $dest
            if (Test-Path -LiteralPath ($src + '.container.json')) { Copy-Item -LiteralPath ($src + '.container.json') -Destination ($dest + '.container.json') }
            $id = [guid]::NewGuid().ToString('N')
            $logDir = Join-Path ([string]$a.root) '.horizun'
            $null = New-Item -ItemType Directory -Force -Path $logDir
            Add-Content -LiteralPath (Join-Path $logDir 'cde-transitions.jsonl') -Value ('{"transition_id":"' + $id + '"}')
            return Reply @{ operation = 'transition'; dry_run = $false; written = $true; verified = $true; destination = $dest; transition_id = $id }
        }
    }
    throw "fake bridge: unexpected container operation $($a.operation)"
}

function Invoke-FakeContext($a) {
    $fx = $script:fx
    $questions = @()
    for ($i = 1; $i -le 20; $i++) {
        $questions += @{ order = $i; id = "q$i"; pointer = "/p/$i"; text = @{ es = "pregunta $i"; en = "question $i" }; why = @{ es = 'porque'; en = 'because' } }
    }
    switch ([string]$a.operation) {
        'validate' {
            $doc = Get-Content -LiteralPath ([string]$a.path) -Raw | ConvertFrom-Json
            if (-not $doc.project.code) { return Reply @{ state = 'invalid'; errors = @(@{ pointer = '/project'; keyword = 'required' }); coherence = @(); missing = @() } }
            if ($doc.deliverables -and $fx.defect -ne 'validate-collapses') {
                return Reply @{ state = 'inconsistent'; errors = @(); coherence = @(@{ rule = 'unknown_status_code'; severity = 'error' }); missing = @(@{ id = 'q2' }) }
            }
            return Reply @{ state = 'incomplete'; errors = @(); coherence = @(); missing = @(@{ id = 'q2' }) }
        }
        'questions' {
            $answered = $(if ($a.path) { 1 } else { 0 })
            $pendingRows = @($questions | Select-Object -Skip $answered)
            if ($fx.defect -eq 'questions-miscount') { $answered = 0 }
            return Reply @{ operation = 'questions'; source = $(if ($a.path) { 'file' } else { 'empty' }); total = 20; answered = $answered
                            not_applicable = 0; pending = $pendingRows.Count; next_question_id = $pendingRows[0].id; questions = $pendingRows }
        }
        'draft' {
            if ($fx.defect -eq 'draft-writes') { [IO.File]::WriteAllText([string]$a.path, '{}') }
            return Reply @{ operation = 'draft'; dry_run = $true; written = $false; base = 'empty'; state = 'incomplete'
                            document = @{ schema_version = 1; project = @{ code = $a.answers['/project/code']; name = $a.answers['/project/name'] } } }
        }
    }
    throw "fake bridge: unexpected context operation $($a.operation)"
}

$script:fakeCall = {
    param($tool, $arguments)
    $null = $script:fx.calls.Add($tool)
    switch ($tool) {
        'horizun_query_model' {
            $cat = [string]@($arguments.categories)[0]
            $n = 0; if ($script:fx.counts.ContainsKey($cat)) { $n = [int]$script:fx.counts[$cat] }
            if ($arguments.response_mode -eq 'summary') { return Reply @{ matched_total = $n; coverage_complete = $true } }
            $rows = @(); for ($i = 1; $i -le $n; $i++) { $rows += @{ element_id = 1000 + $i; is_element_type = $false } }
            return Reply @{ rows = $rows; truncated = $false; coverage_complete = $true }
        }
        'horizun_deliver_ifc' { return Invoke-FakeDeliver $arguments }
        'horizun_export' { return Invoke-FakeExport $arguments }
        'horizun_information_container' { return Invoke-FakeContainer $arguments }
        'horizun_project_context' { return Invoke-FakeContext $arguments }
        'horizun_query_planimetry' {
            $rows = @(@{ sheet_id = 11; sheet_number = 'P0'; placeholder = $true })
            if ($script:fx.sheet) { $rows += @{ sheet_id = 12; sheet_number = 'A101'; placeholder = $false } }
            return Reply @{ rows = $rows }
        }
        'horizun_write_params_verified' {
            if ($arguments.dry_run -eq $true) { return Reply @{ dry_run = $true; confirmation_token = 'tok-w' } }
            $script:fx.codes_written = $true
            return Reply @{ writes_confirmed = @($arguments.writes).Count }
        }
    }
    throw "fake bridge: unexpected tool $tool"
}

# The same two-call shape as verify-live's Invoke-WriteApply.
$script:fakeApply = {
    param($tool, $arguments, $key)
    $dry = $arguments.Clone(); $dry['dry_run'] = $true
    $d = & $script:fakeCall $tool $dry
    if ($d.isError -or -not $d.data -or -not $d.data.confirmation_token) { return @{ stage = 'dry_run'; answer = $d } }
    $apply = $arguments.Clone(); $apply['dry_run'] = $false; $apply['confirmation_token'] = $d.data.confirmation_token
    $apply['idempotency_key'] = "live-write-$key"
    return @{ stage = 'apply'; answer = (& $script:fakeCall $tool $apply); dry = $d }
}

function Invoke-Section {
    param([string]$WriteGate, [switch]$ReadyWrite, [string]$Profile = 'safe_write', [string]$Preferred = 'OST_DuctCurves')
    $scratch = Join-Path $testRoot ('scratch-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $null = New-Item -ItemType Directory -Path $scratch
    $run = [guid]::NewGuid().ToString('N')
    $section = Invoke-HorizunIsoSection -Year $script:fx.year -Document 'HZ_DISPOSABLE' -ScratchRoot $scratch -RepoRoot $repoRoot `
        -RunId $run -WriteGate $WriteGate -PreferredCategory $Preferred -ReadyWrite:$ReadyWrite -PermissionProfile $Profile `
        -Call $script:fakeCall -Apply $script:fakeApply
    $byId = @{}
    foreach ($c in $section.cases) { $byId[$c.id] = $c }
    return [pscustomobject]@{ section = $section; byId = $byId; scratch = $scratch; run = $run }
}

function Outcomes($result, [string[]]$ids) { return (($ids | ForEach-Object { $result.byId[$_].outcome }) -join ',') }

try {
    # ---- pure helpers -----------------------------------------------------------
    $plan23 = Get-HorizunIsoIfcPlan -Year 2023
    $plan24 = Get-HorizunIsoIfcPlan -Year 2024
    Assert 'IFC4 is the delivery version and schema family every year' ($plan23.version -eq 'IFC4' -and $plan23.schema_family -eq 'IFC4' -and $plan24.ids_version -eq 'IFC4')
    Assert 'IFC4x3 is expected from 2024 only' ((-not $plan23.ifc4x3_available) -and $plan24.ifc4x3_available -and (Get-HorizunIsoIfcPlan -Year 2027).ifc4x3_available)

    $c1 = New-HorizunIsoContainer -Number '0001'
    Assert 'container name joins the seven ISO fields in order' ((Get-HorizunIsoContainerName $c1) -eq 'HZ01-HRZ-ZZ-XX-M3-A-0001')
    $defaults = @{ project = '^[A-Z0-9]{2,6}$'; originator = '^[A-Z0-9]{3,6}$'; volume = '^[A-Z0-9]{2}$'; level = '^[A-Z0-9]{2}$'
                   type = '^[A-Z0-9]{2}$'; role = '^[A-Z0-9]{1,2}$'; number = '^[0-9]{4,6}$' }
    $okFields = $true
    foreach ($c in @($c1, (New-HorizunIsoContainer -Number '0003' -Type 'DR'))) {
        foreach ($k in $defaults.Keys) { if ([string]$c.fields[$k] -notmatch $defaults[$k]) { $okFields = $false } }
    }
    Assert 'every generated container field satisfies the ISO 19650-2 default patterns the bridge ships' $okFields

    $walls = Get-HorizunIsoClassCandidates | Where-Object { $_.category -eq 'OST_Walls' }
    $inputs = New-HorizunIsoDeliveryInputs -Directory (Join-Path $testRoot 'inputs') -Class $walls -IdsVersion 'IFC4'
    $lines = [IO.File]::ReadAllLines($inputs.mapping)
    $psetLine = $lines | Where-Object { $_ -like 'PropertySet:*' }
    $propLine = $lines | Where-Object { $_.StartsWith("`t") }
    Assert 'the mapping is TAB separated the way the exporter splits it' (($psetLine -split "`t").Count -eq 4 -and ($psetLine -split "`t")[3] -eq 'IfcWall' -and
                                                                           ($propLine -split "`t")[1] -eq 'Code' -and ($propLine -split "`t")[3] -eq 'Comments')
    Assert 'the mapping has no line indented with spaces' (@($lines | Where-Object { $_ -match '^ +\S' }).Count -eq 0)
    $xml = [xml][IO.File]::ReadAllText($inputs.ids)
    $ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('i', 'http://standards.buildingsmart.org/IDS'); $ns.AddNamespace('xs', 'http://www.w3.org/2001/XMLSchema')
    $spec = $xml.SelectSingleNode('//i:specification', $ns)
    $enum = @($xml.SelectNodes('//i:applicability/i:entity/i:name/xs:restriction/xs:enumeration', $ns) | ForEach-Object { $_.value })
    Assert 'the IDS is well-formed, declares IFC4 and asks for HZ_Delivery.Code' ($spec.ifcVersion -eq 'IFC4' -and
        $xml.SelectSingleNode('//i:requirements/i:property/i:propertySet/i:simpleValue', $ns).InnerText -eq 'HZ_Delivery' -and
        $xml.SelectSingleNode('//i:requirements/i:property/i:baseName/i:simpleValue', $ns).InnerText -eq 'Code')
    Assert 'a class with two IFC entity names is an xs:enumeration' (($enum -join ',') -eq 'IFCWALL,IFCWALLSTANDARDCASE')
    $ducts = Get-HorizunIsoClassCandidates | Where-Object { $_.category -eq 'OST_DuctCurves' }
    $ductInputs = New-HorizunIsoDeliveryInputs -Directory (Join-Path $testRoot 'inputs-duct') -Class $ducts -IdsVersion 'IFC4'
    $ductXml = [xml][IO.File]::ReadAllText($ductInputs.ids)
    $ns2 = New-Object Xml.XmlNamespaceManager($ductXml.NameTable); $ns2.AddNamespace('i', 'http://standards.buildingsmart.org/IDS')
    Assert 'a single IFC entity is a simpleValue' ($ductXml.SelectSingleNode('//i:applicability/i:entity/i:name/i:simpleValue', $ns2).InnerText -eq 'IFCDUCTSEGMENT')
    Assert 'the generated inputs carry their SHA-256' ($inputs.mapping_sha256 -eq (Sha $inputs.mapping) -and $inputs.ids_sha256 -eq (Sha $inputs.ids))

    $readyOk = Test-HorizunIsoReadinessFollowsGates ([pscustomobject]@{ deliverable_ready = $false; blocking = @('ids_validate is failed')
        gates = @([pscustomobject]@{ gate = 'export'; status = 'passed'; requested = $true; advisory = $false },
                  [pscustomobject]@{ gate = 'schema_header'; status = 'passed'; requested = $true; advisory = $false },
                  [pscustomobject]@{ gate = 'precheck'; status = 'failed'; requested = $true; advisory = $true },
                  [pscustomobject]@{ gate = 'ids_validate'; status = 'failed'; requested = $true; advisory = $false }) })
    Assert 'readiness recomputed from the gates agrees with an honest not-ready reply' ($readyOk.consistent -and -not $readyOk.expected)
    $readyAdvisory = Test-HorizunIsoReadinessFollowsGates ([pscustomobject]@{ deliverable_ready = $true; blocking = @()
        gates = @([pscustomobject]@{ gate = 'export'; status = 'passed'; requested = $true; advisory = $false },
                  [pscustomobject]@{ gate = 'schema_header'; status = 'passed'; requested = $true; advisory = $false },
                  [pscustomobject]@{ gate = 'precheck'; status = 'failed'; requested = $true; advisory = $true },
                  [pscustomobject]@{ gate = 'bcf'; status = 'skipped'; requested = $true; advisory = $false }) })
    Assert 'an advisory failure and a skipped gate do not block readiness' ($readyAdvisory.consistent -and $readyAdvisory.expected)
    $readyLie = Test-HorizunIsoReadinessFollowsGates ([pscustomobject]@{ deliverable_ready = $true; blocking = @()
        gates = @([pscustomobject]@{ gate = 'export'; status = 'passed'; requested = $true; advisory = $false },
                  [pscustomobject]@{ gate = 'pset_mapping'; status = 'not_decidable'; requested = $true; advisory = $false }) })
    Assert 'ready=true beside an undecided gate is caught' (-not $readyLie.consistent)

    # ---- the recorder -----------------------------------------------------------
    $rec = New-HorizunIsoRecorder
    Complete-HorizunIsoCase $rec 'ISO-D1' 'pass' 'first' $null
    Complete-HorizunIsoCase $rec 'ISO-D1' 'fail' 'second' $null
    $threw = $false; try { Complete-HorizunIsoCase $rec 'ISO-X9' 'pass' 'x' $null } catch { $threw = $true }
    $threwOutcome = $false; try { Complete-HorizunIsoCase $rec 'ISO-D2' 'green' 'x' $null } catch { $threwOutcome = $true }
    $closed = Close-HorizunIsoRecorder $rec
    Assert 'the first verdict stands and a repeat is named' ($rec.Results['ISO-D1'].outcome -eq 'pass' -and $rec.Results['ISO-D1'].detail -match 'second verdict')
    Assert 'an unknown case id and an unknown outcome throw' ($threw -and $threwOutcome)
    Assert 'a case that never ran closes as unverified, in catalog order' ($closed.Count -eq 15 -and $closed[1].id -eq 'ISO-D2' -and $closed[1].outcome -eq 'unverified')
    Assert 'the opt-in ready case exists only when asked for' (@(Get-HorizunIsoCaseCatalog).Count -eq 15 -and @(Get-HorizunIsoCaseCatalog -IncludeReadyWrite).Count -eq 16)
    $names = @(Get-HorizunIsoCaseCatalog -IncludeReadyWrite | ForEach-Object { $_.Name })
    Assert 'case names are unique (verify-live refuses a report with duplicate names)' (@($names | Select-Object -Unique).Count -eq $names.Count)
    $liveSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-live.ps1') -Raw
    $collide = @($names | Where-Object { $liveSource.Contains("'" + $_ + "'") })
    Assert 'no ISO case name collides with a probe name verify-live already uses' ($collide.Count -eq 0)

    # ---- the run folder ---------------------------------------------------------
    $refused = $false; try { $null = New-HorizunIsoRunDirectory -Root (Join-Path $repoRoot 'artifacts') -RunId 'abcdef123' -RepoRoot $repoRoot } catch { $refused = $true }
    Assert 'a run folder inside the repository is refused' $refused
    $dirA = New-HorizunIsoRunDirectory -Root $testRoot -RunId 'runfolder01' -RepoRoot $repoRoot
    $reuse = $false; try { $null = New-HorizunIsoRunDirectory -Root $testRoot -RunId 'runfolder01' -RepoRoot $repoRoot } catch { $reuse = $true }
    Assert 'a run folder is never reused' $reuse
    $wrongDelete = $false; try { $null = Remove-HorizunIsoRunDirectory -Path $dirA -Root $testRoot -RunId 'otherrun99' } catch { $wrongDelete = $true }
    Assert 'removal refuses a folder that is not this run''s' ($wrongDelete -and (Test-Path -LiteralPath $dirA))
    Assert 'removal deletes exactly this run''s folder' ((Remove-HorizunIsoRunDirectory -Path $dirA -Root $testRoot -RunId 'runfolder01') -and -not (Test-Path -LiteralPath $dirA))

    # ---- sanitising --------------------------------------------------------------
    $leak = 'wrote ' + (Join-Path ([IO.Path]::GetTempPath()) 'x\y.ifc') + ' and C:\Users\someone\a.txt and C:\t\claude\C--Users-someone-repo\z'
    $clean = Protect-HorizunIsoText $leak
    Assert 'temp and profile paths are redacted in both shapes' ($clean -notmatch 'Users[\\/]+someone' -and $clean -notmatch 'C--Users-someone-' -and $clean -match '<temp>')
    if ($env:USERNAME -and $env:USERNAME.Length -ge 3) { Assert 'the account name is redacted' ((Protect-HorizunIsoText ("x " + $env:USERNAME + " y")) -notmatch [regex]::Escape($env:USERNAME)) }

    # ---- 1. green, Revit 2026, safe_write -----------------------------------------
    Reset-Fake -Year 2026
    $r = Invoke-Section
    $all = @('ISO-D1', 'ISO-D2', 'ISO-D3', 'ISO-D4', 'ISO-D5', 'ISO-E1', 'ISO-E2', 'ISO-E3', 'ISO-H1', 'ISO-H2', 'ISO-H3', 'ISO-H4', 'ISO-H5', 'ISO-H6', 'ISO-H7')
    Assert '2026 safe_write: every case passes' ((Outcomes $r $all) -eq ((@('pass') * 15) -join ','))
    if ((Outcomes $r $all) -ne ((@('pass') * 15) -join ',')) { $r.section.cases | Where-Object { $_.outcome -ne 'pass' } | ForEach-Object { Write-Host ("   {0} {1}: {2}" -f $_.id, $_.outcome, $_.detail) } }
    Assert 'H7 took the profile-refusal branch under safe_write' ($r.byId['ISO-H7'].detail -match 'refused by the permission profile')
    Assert 'H6 used the IFC sealed by export' ($r.byId['ISO-H6'].detail -match 'sealed by horizun_export')
    Assert 'the discovery chose the preferred category with elements' ($r.byId['ISO-D1'].detail -match 'OST_DuctCurves' -and $r.byId['ISO-D1'].detail -match 'IfcDuctSegment')
    Assert 'the discovery asked the model with a summary query' ($script:fx.calls -contains 'horizun_query_model')
    Assert 'no case detail carries a temp or profile path' (@($r.section.cases | Where-Object { ($_.detail + ($_.evidence | ConvertTo-Json -Depth 30 -Compress)) -match 'Users[\\/]+' }).Count -eq 0)
    Assert 'every case record carries id, name, tool, tier and the consolidator status' (@($r.section.cases | Where-Object { -not $_.id -or -not $_.name -or -not $_.tool -or -not $_.tier -or $_.status -notin @('passed', 'failed', 'unverified', 'not_covered') }).Count -eq 0)
    $runDir = $r.section.run_directory
    Assert 'every file the section wrote is inside its run folder' (@(Get-ChildItem -LiteralPath $r.scratch -Force | Where-Object { $_.FullName -ne $runDir }).Count -eq 0)

    # The consolidator's record, and the run folder cleanup.
    $identity = New-HorizunIsoIdentity -Health ([pscustomobject]@{ horizun_commit = ('a' * 40); revit_version = '2026'; revit_build = '26.0.4.409'
            horizun_version = '2.0.5'; addin_assembly = [pscustomobject]@{ sha256 = ('b' * 64) } }) `
        -BuildIdentity ([pscustomobject]@{ contract_hash = 'c0ffee' }) -ServerSha256 ('d' * 64) -HarnessSha256 ('e' * 64) `
        -HarnessGitBlob ('f' * 40) -HarnessTrackedClean $true -RepoHead ('a' * 40) -RepoClean $true -Year 2026 -RunId $r.run -Document 'HZ_DISPOSABLE'
    $session = Join-Path $testRoot 'session'
    $evidencePath = Join-Path $session ("iso19650-2026-{0}.json" -f $r.run)
    $insideRefused = $false
    try { $null = Complete-HorizunIsoLiveRun -Section $r.section -Identity $identity -EvidencePath (Join-Path $runDir 'e.json') -ScratchRoot $r.scratch -RunId $r.run } catch { $insideRefused = $true }
    Assert 'the evidence file may not live inside the folder that is removed' $insideRefused
    $live = Complete-HorizunIsoLiveRun -Section $r.section -Identity $identity -EvidencePath $evidencePath -ScratchRoot $r.scratch -RunId $r.run
    $ev = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    Assert 'the record is horizun.live-evidence/2 with totals that match its probes' ($ev.schema -eq 'horizun.live-evidence/2' -and $ev.passed -eq 15 -and
        $ev.failed -eq 0 -and @($ev.probes).Count -eq 15 -and $ev.harness_file -eq 'scripts/verify-live.ps1' -and $ev.revit_year -eq '2026')
    Assert 'a green run removes its folder' ($live.run_directory_removed -and -not (Test-Path -LiteralPath $runDir))
    $python = $null
    foreach ($candidate in @('python', 'python3', 'py')) { if (Get-Command $candidate -ErrorAction SilentlyContinue) { $python = $candidate; break } }
    if ($python) {
        $consolidated = Join-Path $testRoot 'consolidated.json'
        $output = & $python (Join-Path $PSScriptRoot 'consolidate-live-session.py') --session $session --out $consolidated 2>&1
        $ok = ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $consolidated))
        Assert 'scripts/consolidate-live-session.py accepts the ISO record' $ok
        if (-not $ok) { Write-Host ($output | Out-String) }
        else {
            $cons = Get-Content -LiteralPath $consolidated -Raw | ConvertFrom-Json
            Assert 'the consolidator counts 15 distinct cases (probe x harness x year x document)' ([int]$cons.coverage.unique_cases -eq 15)
        }
    } else { Write-Host 'SKIP consolidator round trip (no python on PATH)' }
    $badStatus = $false
    try { $null = ConvertTo-HorizunIsoEvidence -Cases @([pscustomobject]@{ id = 'X'; status = 'green' }) -Identity $identity } catch { $badStatus = $true }
    Assert 'an unknown status never reaches the consolidator' $badStatus

    # ---- 2. Revit 2023: IFC4x3 is refused by name ---------------------------------
    Reset-Fake -Year 2023
    $r = Invoke-Section
    Assert '2023: IFC4x3 refusal is the pass' ($r.byId['ISO-D5'].outcome -eq 'pass' -and $r.byId['ISO-D5'].detail -match 'no IFCVersion.IFC4x3')
    Reset-Fake -Year 2023 -Defect 'ifc4x3-accepted'
    Assert '2023: an IFC4x3 plan where the enum lacks it fails' ((Invoke-Section).byId['ISO-D5'].outcome -eq 'fail')

    # ---- 3. full_write: the transition lands, is logged, and replays ----------------
    Reset-Fake -Year 2026 -Profile 'full_write'
    $r = Invoke-Section -Profile 'full_write'
    Assert 'full_write: the apply lands a logged copy and replays as already_transitioned' ($r.byId['ISO-H7'].outcome -eq 'pass' -and $r.byId['ISO-H7'].detail -match 'already_transitioned')
    Reset-Fake -Year 2026 -Profile 'full_write' -Defect 'refused-despite-profile'
    Assert 'full_write: a profile refusal the profile does not explain fails' ((Invoke-Section -Profile 'full_write').byId['ISO-H7'].outcome -eq 'fail')

    # ---- 4. the opt-in ready case ------------------------------------------------
    Reset-Fake -Year 2026
    $r = Invoke-Section -ReadyWrite
    Assert 'ready probe: writing the code turns the same delivery ready' ($r.byId['ISO-D6'].outcome -eq 'pass' -and @($r.section.cases).Count -eq 16)

    # ---- 5. the write gate closed ------------------------------------------------
    Reset-Fake -Year 2026
    $r = Invoke-Section -WriteGate 'needs -WriteProbes; the default run commits nothing'
    Assert 'gated: every write case is NOT COVERED with the gate as reason' ((Outcomes $r @('ISO-D1', 'ISO-D5', 'ISO-E1', 'ISO-E3')) -eq 'not_covered,not_covered,not_covered,not_covered' -and
        $r.byId['ISO-E2'].detail -match 'needs -WriteProbes')
    Assert 'gated: host-resident cases still run' ((Outcomes $r @('ISO-H1', 'ISO-H2', 'ISO-H3', 'ISO-H4', 'ISO-H5')) -eq 'pass,pass,pass,pass,pass')
    Assert 'gated + safe_write: no sealed container, so H6/H7 are NOT COVERED by name' ((Outcomes $r @('ISO-H6', 'ISO-H7')) -eq 'not_covered,not_covered')
    Assert 'gated: no write-tier tool was called' (-not ($script:fx.calls -contains 'horizun_deliver_ifc') -and -not ($script:fx.calls -contains 'horizun_export'))
    Reset-Fake -Year 2026 -Profile 'full_write'
    $r = Invoke-Section -WriteGate 'gate closed' -Profile 'full_write'
    Assert 'gated + full_write: a host-side stamp provides the sealed source' ((Outcomes $r @('ISO-H6', 'ISO-H7')) -eq 'pass,pass' -and $r.byId['ISO-H6'].detail -match 'stamp')

    # ---- 6. the model decides the class ----------------------------------------
    Reset-Fake -Year 2026 -Counts @{ OST_DuctCurves = 0; OST_PipeCurves = 4 }
    $r = Invoke-Section
    Assert 'no ducts: the pipes the model has are chosen instead' ($r.byId['ISO-D1'].outcome -eq 'pass' -and $r.byId['ISO-D1'].detail -match 'IfcPipeSegment')
    Reset-Fake -Year 2026 -Counts @{}
    $r = Invoke-Section
    Assert 'no candidate class: D1-D4 NOT COVERED naming what was counted, D5 still measured' (
        (Outcomes $r @('ISO-D1', 'ISO-D2', 'ISO-D3', 'ISO-D4', 'ISO-D5')) -eq 'not_covered,not_covered,not_covered,not_covered,pass' -and $r.byId['ISO-D1'].detail -match 'OST_DuctCurves=0')
    Reset-Fake -Year 2026 -Sheet $false
    Assert 'no real sheet: the PDF case is NOT COVERED, not passed' ((Invoke-Section).byId['ISO-E3'].outcome -eq 'not_covered')

    # ---- 7. every simulated product defect is caught ----------------------------------
    foreach ($case in @(
        @{ defect = 'dry-writes'; id = 'ISO-D1'; why = 'a rehearsal that writes a file' },
        @{ defect = 'seal-gate-unreported'; id = 'ISO-D2'; why = 'the container gate missing from the report (the defect fixed in 654a954)' },
        @{ defect = 'ready-lies'; id = 'ISO-D3'; why = 'deliverable_ready=true beside a failed IDS' },
        @{ defect = 'reseal-accepted'; id = 'ISO-D4'; why = 'a delivery onto a sealed name' },
        @{ defect = 'export-reseal-accepted'; id = 'ISO-E2'; why = 'an export onto a sealed name' },
        @{ defect = 'validate-collapses'; id = 'ISO-H1'; why = 'inconsistent reported as incomplete' },
        @{ defect = 'draft-writes'; id = 'ISO-H3'; why = 'a draft rehearsal that writes' },
        @{ defect = 'questions-miscount'; id = 'ISO-H2'; why = 'an answered field not counted' },
        @{ defect = 'inspect-writes'; id = 'ISO-H4'; why = 'an inspect that writes' },
        @{ defect = 'approval-not-enforced'; id = 'ISO-H5'; why = 'publication without approved_by' })) {
        Reset-Fake -Year 2026 -Defect $case.defect
        $r = Invoke-Section
        Assert ("caught: " + $case.why) ($r.byId[$case.id].outcome -eq 'fail')
    }

    # ---- 8. a throwing bridge is UNVERIFIED, never a pass, never a crash ------------
    Reset-Fake -Year 2026
    $throwing = { param($tool, $arguments) if ($tool -eq 'horizun_export') { throw 'transport died' } & $script:fakeCall $tool $arguments }
    $scratch = Join-Path $testRoot 'scratch-throw'; $null = New-Item -ItemType Directory -Path $scratch
    $throwingApply = { param($tool, $arguments, $key) if ($tool -eq 'horizun_export') { throw 'transport died' } & $script:fakeApply $tool $arguments $key }
    $section = Invoke-HorizunIsoSection -Year 2026 -Document 'HZ_DISPOSABLE' -ScratchRoot $scratch -RepoRoot $repoRoot -RunId 'throwrun01' `
        -PreferredCategory 'OST_DuctCurves' -PermissionProfile 'safe_write' -Call $throwing -Apply $throwingApply
    $e = @($section.cases | Where-Object { $_.id -like 'ISO-E*' })
    Assert 'a transport that throws mid-section leaves its cases UNVERIFIED and the rest measured' (
        @($e | Where-Object { $_.outcome -ne 'unverified' }).Count -eq 0 -and $e[0].detail -match 'transport died' -and
        @($section.cases | Where-Object { $_.id -eq 'ISO-D2' -and $_.outcome -eq 'pass' }).Count -eq 1 -and @($section.cases).Count -eq 15)

    # ---- 9. THE GLUE IN verify-live.ps1, run from its own text --------------------
    # The block between the iso19650-section markers is executed here with the
    # harness's transport functions stubbed, so a typo in the glue fails here
    # rather than an hour into a matrix run.
    $liveText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-live.ps1') -Raw
    $glue = [regex]::Match($liveText, '(?s)# <iso19650-section>[^\n]*\n(.*?)# </iso19650-section>').Groups[1].Value
    Assert 'verify-live carries the ISO section between its markers' ($glue -match 'Invoke-HorizunIsoSection' -and $glue -match 'Complete-HorizunIsoLiveRun')
    function Invoke-Write($tool, $arguments) {
        if ($tool -eq 'horizun_health') {
            return Reply @{ horizun_commit = ('a' * 40); revit_version = [string]$script:fx.year; revit_build = '26.0.4.409'; horizun_version = '2.0.5'
                            addin_assembly = @{ sha256 = ('b' * 64) }; operational_controls = @{ permission_profile = $script:fx.profile } }
        }
        return (& $script:fakeCall $tool $arguments)
    }
    function Invoke-WriteApply($tool, $arguments, $keyName) { return (& $script:fakeApply $tool $arguments $keyName) }
    function Send-Rpc($obj) { $script:sentRpc = $obj }
    function Read-Rpc([int]$TimeoutMs) {
        return ('{"id":990601,"result":{"contents":[{"uri":"horizun://build/identity","text":"{\"contract_hash\":\"c0ffee\"}"}]}}' | ConvertFrom-Json)
    }
    function Add-Write($name, $tool, $outcome, $detail) { $script:writeResults += @{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = $detail } }
    foreach ($glueCase in @(@{ run = [guid]::NewGuid().ToString('N'); expect = 'pass' }, @{ run = 'bad run id!'; expect = 'unverified' })) {
        Reset-Fake -Year 2026
        $script:writeResults = @()
        $Year = 2026; $WriteDocument = 'HZ_DISPOSABLE'; $Document = 'HZ_DISPOSABLE'; $writeGate = $null
        $scratchDir = Join-Path $testRoot ('glue-' + [guid]::NewGuid().ToString('N').Substring(0, 8)); $null = New-Item -ItemType Directory -Path $scratchDir
        $repositoryRoot = $repoRoot; $probeRun = $glueCase.run; $QuantityCategory = 'OST_DuctCurves'
        $IsoReadyProbe = [switch]$false; $IsoMappedParameter = 'Comments'; $serverSha = ('d' * 64)
        $harnessFile = 'scripts/verify-live.ps1'; $harnessSha256 = ('e' * 64); $harnessGitBlob = ('f' * 40); $harnessTrackedClean = $true
        $harnessCommit = ('a' * 40); $Json = Join-Path $testRoot ('glue-report-' + $glueCase.expect + '\live-2026.json')
        $isoLive = $null
        . ([scriptblock]::Create($glue))
        $outcomes = @($script:writeResults | ForEach-Object { $_.Outcome } | Select-Object -Unique)
        Assert ("glue ($($glueCase.expect)): every ISO case reaches Add-Write once, as $($glueCase.expect)") (
            $script:writeResults.Count -eq 15 -and ($outcomes -join ',') -eq $glueCase.expect)
        if ($glueCase.expect -eq 'pass') {
            $evidenceFile = Join-Path (Split-Path -Parent $Json) ("iso19650-2026-{0}.json" -f $probeRun)
            $glueEvidence = Get-Content -LiteralPath $evidenceFile -Raw | ConvertFrom-Json
            Assert 'glue: the evidence lands beside -Json with the identity health and resources/read gave' (
                $glueEvidence.contract_hash -eq 'c0ffee' -and $glueEvidence.addin_sha256 -eq ('b' * 64) -and $glueEvidence.revit_build -eq '26.0.4.409' -and
                $glueEvidence.document -eq 'HZ_DISPOSABLE' -and $script:sentRpc.method -eq 'resources/read')
            Assert 'glue: the report block is serialisable and the run folder is gone after a green run' (
                $isoLive.run_directory_removed -and (($isoLive | ConvertTo-Json -Depth 40) -match 'ISO-H7'))
        } else {
            Assert 'glue: a section that throws is recorded, never lost' (@($script:writeResults | Where-Object { $_.Detail -match 'HARNESS' }).Count -eq 15)
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'hz-iso-probe-*') { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($script:failed -gt 0) { Write-Host "$($script:failed) assertion(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'all ISO 19650 probe assertions passed'
exit 0
