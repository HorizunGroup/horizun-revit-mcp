#Requires -Version 5.1
<#
  ACERO 3.0, LIVE, on a DISPOSABLE fixture: bars that FOLLOW the face of sloped and
  variable-thickness slabs (mat components with follow_face). Typed tools only.

    T1  a roof of two structural floors - a flat strip and a 6% slope made with slab_shape,
        meeting at a break: top bars along the slope run across both pieces as one bent bar
        (host = the piece most of it lies on, into = the other), and bars across the slope
        are each at their own height (singles on the slope, one set on the strip).
        plan, apply and audit must agree.
    T2  the same roof from below: bottom bars along the slope, bent on the other side of
        the break, with 180 degree hooks at both ends, and bottom bars across it.
    T3  a variable-thickness slab: a floor type whose one layer is VARIABLE, its far edge
        raised with slab_shape so only the top slopes: bottom bars flat, top bars along the
        slope straight on it, top bars across it each at their own height.
    T4  a mat with into and a flat component is refused by name (mat_into_needs_follow_face).

  What T1-T3 measure that the Core tests cannot: whether Revit builds a 3-point bar bent
  3.4 degrees with a matching or newly created shape, whether a bar hosted in one floor and
  running into the next is held to the host's cover at the end inside it and left alone at
  the end outside it, and whether the per-station singles re-read exactly as planned.
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-slabface' -Document $Document
function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 500 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzHookName([string]$Pattern) {
    $h = @(Get-HzTypes 'OST_Rebar' | Where-Object { [string]$_.family -eq 'Rebar Hook' -and [string]$_.name -match $Pattern })
    if ($h.Count -ne 1) { throw "HARNESS: $($h.Count) hook types match $Pattern" }
    [string]$h[0].name
}
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }
function Get-HzCover([long]$Id) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }
    $row = @(Get-HzPath $hq.Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -ne $c) { return [double]$c }
    $fc = @(@(Get-HzPath $row 'cover', 'faces') | ForEach-Object { Get-HzPath $_ 'cover', 'distance_mm' } | Where-Object { $null -ne $_ })
    [double](@($fc | Sort-Object -Descending)[0])
}
function New-HzFloor([string]$Label, [double]$X0, [double]$X1, [double]$Y1, [long]$TypeId) {
    Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label $Label -Arguments @{ target_document = $Document; units = 'mm'
        elements = @(, @{ kind = 'floor'; type_id = $TypeId; level_id = $lv; structural = $true
            profile = @(, @(@($X0, 0.0, 0.0), @($X1, 0.0, 0.0), @($X1, $Y1, 0.0), @($X0, $Y1, 0.0))) }) })
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }

<#
  One mat set: plan, apply, audit. Each set is applied and audited ON ITS OWN so a refusal
  in one does not keep the next from being measured, and the audit reads only its own rules.
#>
function Invoke-HzMat([string]$Id, $Mat, $HookTypes) {
    $one = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = "slabface-$Id"; version = '1.0.0' }
        bar_types = @(@{ id = 'N12'; type_name = '13M' }, @{ id = 'N10'; type_name = '10M' }); mat_rules = @(, $Mat) }
    if ($HookTypes) { $one['hook_types'] = $HookTypes }
    $rows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label "plan-$Id" -Arguments @{ target_document = $Document; requirement_set = $one }).Result
    $refused = @($rows | Where-Object { Get-HzProp $_ 'code' })
    $verif = @(); $finds = @()
    if (-not $refused.Count) {
        $ap = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label "apply-$Id" -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $one; transaction_name = "HZ slabface $Id" }
        $verif = @($(if ($ap.Apply) { @(Get-HzPath $ap.Apply.Result 'verification') }))
        if ($verif.Count) {
            $au = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label "audit-$Id" -Arguments @{ target_document = $Document; requirement_set = $one }
            $finds = @(@(Get-HzPath $au.Result 'findings') | Where-Object { $_ -and [string]$_.severity -ne 'info' -and [string](Get-HzProp $_ 'rule_id') -like "$($Mat.id)#*" })
        }
    }
    [pscustomobject]@{ Rows = $rows; Refused = $refused; Verification = @($verif | Where-Object { $_ }); Findings = $finds }
}
function Get-HzObserved($R) {
    $vn = @($R.Verification | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    "rows={0} refused={1} verified={2}/{3} findings={4} {5}" -f $R.Rows.Count, $R.Refused.Count, ($R.Verification.Count - $vn.Count), $R.Verification.Count, $R.Findings.Count,
        $(if ($R.Refused.Count) { [string](Get-HzProp $R.Refused[0] 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $R.Refused[0] 'why')) 400) }
          elseif ($vn.Count) { Limit-HzText ((Get-HzProp $vn[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600 }
          elseif ($R.Findings.Count) { [string]$R.Findings[0].code + ' exp=' + [string]$R.Findings[0].expected + ' obs=' + [string]$R.Findings[0].observed } else { '' })
}
function Test-HzAgree($R, [int]$MinRows) {
    $vn = @($R.Verification | Where-Object { (Get-HzProp $_ 'verified') -ne $true })
    $R.Rows.Count -ge $MinRows -and $R.Refused.Count -eq 0 -and $R.Verification.Count -eq $R.Rows.Count -and $vn.Count -eq 0 -and $R.Findings.Count -eq 0
}
function Get-HzFollow($R, [string]$Like) { @($R.Rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like } | ForEach-Object { Get-HzProp $_ 'follow_face' } | Where-Object { $_ }) }

Write-Host "`n== fixture: a roof of two floors, a variable-thickness slab ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
$floorT = @(Get-HzTypes 'OST_Floors' | Where-Object { [string]$_.name -match '(\d+)\s*mm' -and [string]$_.name -notmatch 'Deck|Metal|Steel|Wood|Timber' } |
    Where-Object { [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) -ge 150 } | Select-Object -First 1)[0]
$X0 = 230000.0
$strip = New-HzFloor 'strip' $X0 ($X0 + 1500) 3000 ([long]$floorT.element_id)
$slope = New-HzFloor 'slope' ($X0 + 1500) ($X0 + 5500) 3000 ([long]$floorT.element_id)
# Raise the far edge 240 mm over 4000: a 6% slope that meets the strip at the break, x = X0 + 1500.
$shape = Invoke-HzWrite -Run $run -Tool 'horizun_slab_shape' -Label 'raise-slope' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
    operation = 'modify_subelement'; element_id = $slope; start = @(($X0 + 5500), 0.0); end = @(($X0 + 5500), 3000.0); offset = 240.0 }
$cvS = Get-HzCover $strip; $cvP = Get-HzCover $slope
$run.Fixture['roof'] = @{ strip = $strip; slope = $slope; shaped = $(if ($shape.Apply) { $shape.Apply.Ok } else { $false }); cover_strip = $cvS; cover_slope = $cvP }
$cv = [Math]::Max($cvS, $cvP)
$follow = @{ planarity_mm = 0.5; min_facet_mm = 100 }

Write-Host "`n== T1: the roof's top ==" -ForegroundColor Cyan
$t1 = Invoke-HzMat 'T1' ([ordered]@{ id = 'T1'; host = @{ element_ids = @(, $strip) }; into = @(, $slope); face_normal = @(0, 0, 1)
    components = @(
        [ordered]@{ name = 'along'; direction = @(1, 0, 0); bar_type = 'N12'; offset_from_face_mm = $cv + 7; end_cover_mm = $cv; side_cover_mm = $cv + 7
            allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } },
        [ordered]@{ name = 'across'; direction = @(0, 1, 0); bar_type = 'N10'; offset_from_face_mm = $cv + 20; end_cover_mm = $cv; side_cover_mm = $cv + 7
            allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) $null
$alongF = Get-HzFollow $t1 'T1#along*'
$acrossF = Get-HzFollow $t1 'T1#across*'
$bends = @($alongF | ForEach-Object { [int](Get-HzProp $_ 'bends') })
Add-HzProbe -Run $run -Id 'T1' -Name 'a roof of two floors: the top bars follow the face across the break, the bars across it each at their height' `
    -Expected 'one bent set along (1 bend, host = slope, into = strip), across: one set on the strip + singles on the slope; all applied, verified, no audit error' `
    -Observed ("shaped={0} along_sets={1} bends={2} across_sets={3} {4}" -f $run.Fixture['roof'].shaped, $alongF.Count, ($bends -join ','), $acrossF.Count, (Get-HzObserved $t1)) `
    -Ok ((Test-HzAgree $t1 3) -and $alongF.Count -eq 1 -and $bends -contains 1 -and $acrossF.Count -ge 3) -Evidence @{ rows = $t1.Rows; verification = $t1.Verification; findings = $t1.Findings }

Write-Host "`n== T2: the roof's bottom, hooked ==" -ForegroundColor Cyan
$h180 = Get-HzHookName '^Standard - 180 deg\.?$'
$t2 = Invoke-HzMat 'T2' ([ordered]@{ id = 'T2'; host = @{ element_ids = @(, $strip) }; into = @(, $slope); face_normal = @(0, 0, -1)
    components = @(
        [ordered]@{ name = 'along'; direction = @(1, 0, 0); bar_type = 'N12'; offset_from_face_mm = $cv + 7; end_cover_mm = $cv; side_cover_mm = $cv + 7
            allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 }
            start_hook = @{ hook_type = 'H180' }; end_hook = @{ hook_type = 'H180' } },
        [ordered]@{ name = 'across'; direction = @(0, 1, 0); bar_type = 'N10'; offset_from_face_mm = $cv + 20; end_cover_mm = $cv; side_cover_mm = $cv + 7
            allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) @(, @{ id = 'H180'; type_name = $h180 })
$alongB = Get-HzFollow $t2 'T2#along*'
Add-HzProbe -Run $run -Id 'T2' -Name 'the roof from below: bottom bars bent on the other side of the break, 180 degree hooks at both ends' `
    -Expected 'one bent set along with both hooks, across sets per height; all applied, verified, no audit error' `
    -Observed ("along_sets={0} {1}" -f $alongB.Count, (Get-HzObserved $t2)) `
    -Ok ((Test-HzAgree $t2 3) -and $alongB.Count -eq 1) -Evidence @{ rows = $t2.Rows; verification = $t2.Verification; findings = $t2.Findings }

Write-Host "`n== T3: a variable-thickness slab ==" -ForegroundColor Cyan
# A FLOOR TYPE WHOSE ONE LAYER IS VARIABLE: raising an edge with slab_shape then moves the top only.
$vt = Invoke-HzWrite -Run $run -Tool 'horizun_manage_system_types' -Label 'variable-type' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
    actions = @(, @{ source_type_id = [long]$floorT.element_id; new_name = 'HZ slabface variable 200'
        compound_structure = @{ layers = @(, @{ function = 'Structure'; width = 200.0 }); structural_layer_index = 0; variable_layer_index = 0 } }) }
$vtId = $(if ($vt.Apply -and $vt.Apply.Ok) { [long](Get-HzProp @(Get-HzPath $vt.Apply.Result 'rows')[0] 'new_type_id') } else { $null })
$wedge = $null; $wshape = $null
if ($vtId) {
    $wedge = New-HzFloor 'wedge' ($X0 + 8000) ($X0 + 12000) 3000 $vtId
    $wshape = Invoke-HzWrite -Run $run -Tool 'horizun_slab_shape' -Label 'raise-wedge' -AllowRefusal -Arguments @{ target_document = $Document; units = 'mm'
        operation = 'modify_subelement'; element_id = $wedge; start = @(($X0 + 12000), 0.0); end = @(($X0 + 12000), 3000.0); offset = 200.0 }
}
if (-not $wedge -or -not $wshape -or -not $wshape.Apply -or -not $wshape.Apply.Ok) {
    Add-HzProbe -Run $run -Id 'T3' -Name 'a variable-thickness slab: bottom bars flat, top bars on the slope' -Status 'fixture_missing' `
        -Expected 'a floor type with a variable layer (manage_system_types) raised at one edge (slab_shape)' `
        -Observed $(if (-not $vtId) { 'variable type: ' + $(if ($vt.Apply) { Limit-HzText $vt.Apply.Text 300 } else { Limit-HzText $vt.Dry.Text 300 }) }
                    else { 'slab_shape: ' + $(if ($wshape -and $wshape.Apply) { Limit-HzText $wshape.Apply.Text 300 } elseif ($wshape) { Limit-HzText $wshape.Dry.Text 300 } else { 'not run' }) })
} else {
    $cvW = Get-HzCover $wedge
    $run.Fixture['wedge'] = @{ element = $wedge; type = $vtId; cover = $cvW }
    $t3b = Invoke-HzMat 'T3B' ([ordered]@{ id = 'T3B'; host = @{ element_ids = @(, $wedge) }; face_normal = @(0, 0, -1)
        components = @(
            [ordered]@{ name = 'x'; direction = @(1, 0, 0); bar_type = 'N12'; offset_from_face_mm = $cvW + 7; end_cover_mm = $cvW; side_cover_mm = $cvW + 7
                allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } },
            [ordered]@{ name = 'y'; direction = @(0, 1, 0); bar_type = 'N12'; offset_from_face_mm = $cvW + 20; end_cover_mm = $cvW; side_cover_mm = $cvW + 7
                allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) $null
    $t3t = Invoke-HzMat 'T3T' ([ordered]@{ id = 'T3T'; host = @{ element_ids = @(, $wedge) }; face_normal = @(0, 0, 1)
        components = @(
            [ordered]@{ name = 'x'; direction = @(1, 0, 0); bar_type = 'N12'; offset_from_face_mm = $cvW + 7; end_cover_mm = $cvW; side_cover_mm = $cvW + 7
                allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } },
            [ordered]@{ name = 'y'; direction = @(0, 1, 0); bar_type = 'N12'; offset_from_face_mm = $cvW + 20; end_cover_mm = $cvW; side_cover_mm = $cvW + 7
                allow_new_shape = $true; follow_face = $follow; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) $null
    $flatBottom = @(Get-HzFollow $t3b 'T3B#*' | Where-Object { [int](Get-HzProp $_ 'bends') -ne 0 }).Count -eq 0
    $topY = @(Get-HzFollow $t3t 'T3T#y*')
    Add-HzProbe -Run $run -Id 'T3' -Name 'a variable-thickness slab: bottom bars flat in two sets, top bars on the slope, the ones across it each at their height' `
        -Expected 'bottom: 2 sets, no bend; top: x one set, y one single per station; all applied, verified, no audit error' `
        -Observed ("bottom: {0} | top: y_sets={1} {2}" -f (Get-HzObserved $t3b), $topY.Count, (Get-HzObserved $t3t)) `
        -Ok ((Test-HzAgree $t3b 2) -and $t3b.Rows.Count -eq 2 -and $flatBottom -and (Test-HzAgree $t3t 3) -and $topY.Count -ge 3) `
        -Evidence @{ bottom = @{ rows = $t3b.Rows; verification = $t3b.Verification; findings = $t3b.Findings }; top = @{ rows = $t3t.Rows; verification = $t3t.Verification; findings = $t3t.Findings } }
}

Write-Host "`n== T4: into beside a flat component ==" -ForegroundColor Cyan
$t4set = [ordered]@{ schema = 'horizun.structural-requirements/1'; units = 'millimeter'; requirement_set = @{ id = 'slabface-T4'; version = '1.0.0' }
    bar_types = @(, @{ id = 'N12'; type_name = '13M' })
    mat_rules = @(, [ordered]@{ id = 'T4'; host = @{ element_ids = @(, $strip) }; into = @(, $slope); face_normal = @(0, 0, 1)
        components = @(, [ordered]@{ name = 'flat'; direction = @(1, 0, 0); bar_type = 'N12'; offset_from_face_mm = $cv + 7; end_cover_mm = $cv; side_cover_mm = $cv + 7
            allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) }
$t4 = @(Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-T4' -Arguments @{ target_document = $Document; requirement_set = $t4set }).Result)
$t4why = [string](Get-HzProp @($t4)[0] 'why')
Add-HzProbe -Run $run -Id 'T4' -Name 'into beside a component that does not follow the face is refused by name' `
    -Expected 'one refused row naming mat_into_needs_follow_face' -Observed ("rows={0} code={1} {2}" -f $t4.Count, (Get-HzProp @($t4)[0] 'code'), (Limit-HzText $t4why 300)) `
    -Ok ($t4.Count -eq 1 -and $t4why -like '*mat_into_needs_follow_face*') -Evidence @{ rows = $t4 }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
