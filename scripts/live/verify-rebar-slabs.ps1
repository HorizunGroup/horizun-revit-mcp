#Requires -Version 5.1
<#
  ACERO 3.0, L4, LIVE: slabs on a DISPOSABLE fixture (Autodesk metric template floors and framing,
  16M/10M).

    H1  a two-way bottom mat and top bars (negativos) over a support line only: a mat `run` window
    H2  a band of top bars across a strip only: a mat `spread` window
    H3  a slab with an opening: the mat omits the bars it would cross, and trimming bars beside the
        hole are declared as ordinary rules
    H4  a ribbed slab: the joists reinforced as beams, the topping with a temperature mesh
    H5  a drop panel (abaco): a thicker floor under the slab with its own bottom mat
    H6  a NON-structural floor as a host is refused by name before any write
    H7  everything planned applies and reads back
    H8  the audit finds it where the rules put it
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

$run = New-HzRun -Harness $PSCommandPath -Name 'rebar-slabs' -Document $Document

function Get-HzTypes([string]$Category) {
    @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$Category" -Arguments @{
        categories = @($Category); include_links = $false; include_types = $true; max_rows = 300 }).Result.rows | Where-Object { $_.is_element_type })
}
function Get-HzRows($Result) { @(Get-HzPath $Result 'reinforcement') }
function Get-HzId($Write) { [long](Get-HzProp @($Write.Apply.Result.rows)[0] 'element_id') }
function New-HzRect([double]$X0, [double]$Y0, [double]$W, [double]$D, [double]$Z = 0.0) { @(@($X0, $Y0, $Z), @(($X0 + $W), $Y0, $Z), @(($X0 + $W), ($Y0 + $D), $Z), @($X0, ($Y0 + $D), $Z)) }
function Get-HzCover([long]$Id) {
    $hq = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_structure' -Label "cover-$Id" -Arguments @{ mode = 'hosts'; element_ids = @(, $Id); max_rows = 5 }
    $row = @(Get-HzPath $hq.Result 'rows')[0]
    $c = Get-HzPath $row 'cover', 'common', 'distance_mm'
    if ($null -ne $c) { return [double]$c }
    $fc = @(@(Get-HzPath $row 'cover', 'faces') | ForEach-Object { Get-HzPath $_ 'cover', 'distance_mm' } | Where-Object { $null -ne $_ })
    if ($fc.Count -eq 0) { throw "HARNESS: element $Id has no readable cover" }
    [double](@($fc | Sort-Object -Descending)[0])
}

Write-Host "`n== fixture ==" -ForegroundColor Cyan
$null = Reset-HzDocument $run
$level = Get-HzFirstLevel $run
$lv = [long]$level.element_id
# Floor types: the thickest generic one for the slabs, the thinnest for the topping, by their names'
# millimetres (the template names its floor types "Generic 150mm", "Generic 300mm", ...).
$floors = @(Get-HzTypes 'OST_Floors' | Where-Object { [string]$_.name -match '(\d+)\s*mm' -and [string]$_.name -notmatch 'Deck|Metal|Steel|Wood|Timber' } |
    ForEach-Object { [pscustomobject]@{ id = [long]$_.element_id; name = [string]$_.name; mm = [double]([regex]::Match([string]$_.name, '(\d+)\s*mm').Groups[1].Value) } } |
    Sort-Object mm)
if ($floors.Count -lt 2) { throw 'HARNESS: the fixture has fewer than two floor types named with their thickness' }
$thin = $floors[0]; $slabT = @($floors | Where-Object { $_.mm -ge 150 } | Select-Object -First 1)[0]; $thick = $floors[-1]
$run.Fixture['floor_types'] = @{ thin = $thin.name; slab = $slabT.name; thick = $thick.name }
$beamT = @(Get-HzTypes 'OST_StructuralFraming' | Where-Object { [string]$_.family -eq 'M_Concrete-Rectangular Beam' } |
    Sort-Object { [double]([regex]::Match([string]$_.name, '^(\d+)').Groups[1].Value) } | Select-Object -First 1)[0]
$X0 = 100000.0
$s1 = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'slab' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = $slabT.id; level_id = $lv; profile = @(, (New-HzRect $X0 0 6000 4000)); structural = $true }) })
$s2 = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'slab-opening' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = $slabT.id; level_id = $lv; profile = @(, (New-HzRect ($X0 + 8000) 0 6000 4000)); structural = $true }) })
$null = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'opening' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'slab_opening'; host_id = $s2; allow_structural = $true; shape = 'rectangular'; center = @(($X0 + 11000), 2000.0, 0.0); width = 800.0; height = 600.0 }) }
$top = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'topping' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = $thin.id; level_id = $lv; profile = @(, (New-HzRect ($X0 + 16000) 0 4000 3000)); structural = $true }) })
$joists = @(@((Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'joists' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(
        @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @(($X0 + 16000), 1000.0, (-$thin.mm)); end = @(($X0 + 20000), 1000.0, (-$thin.mm)) },
        @{ kind = 'structural_framing'; type_id = [long]$beamT.element_id; level_id = $lv; start = @(($X0 + 16000), 2000.0, (-$thin.mm)); end = @(($X0 + 20000), 2000.0, (-$thin.mm)) }) }).Apply.Result.rows) |
    ForEach-Object { [long](Get-HzProp $_ 'element_id') })
$drop = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'drop-panel' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = $thick.id; level_id = $lv; profile = @(, (New-HzRect ($X0 + 22000) 0 1500 1500 (-$slabT.mm))); offset = (-$slabT.mm); structural = $true }) })
$nonStruct = Get-HzId (Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'non-structural' -Arguments @{ target_document = $Document; units = 'mm'
    elements = @(, @{ kind = 'floor'; type_id = $slabT.id; level_id = $lv; profile = @(, (New-HzRect ($X0 + 26000) 0 3000 3000)); structural = $false }) })
$run.Fixture['elements'] = @{ slab = $s1; slab_with_opening = $s2; topping = $top; joists = $joists; drop = $drop; non_structural = $nonStruct }
$cv = @{}
foreach ($e in @($s1, $s2, $top, $drop)) { $cv[[string]$e] = Get-HzCover $e }
$run.Fixture['covers'] = $cv

function New-HzComp([long]$HostId, [string]$Name, [double[]]$Dir, [double]$Depth, [double]$Spacing, $Extra) {
    $c = [ordered]@{ name = $Name; direction = $Dir; bar_type = 'N16'; offset_from_face_mm = $cv[[string]$HostId] + $Depth
        end_cover_mm = $cv[[string]$HostId]; side_cover_mm = $cv[[string]$HostId] + 8; allow_new_shape = $true
        layout = @{ rule = 'maximum_spacing'; spacing_mm = $Spacing } }
    if ($Extra) { foreach ($k in $Extra.Keys) { $c[$k] = $Extra[$k] } }
    $c
}
$set = [ordered]@{
    schema = 'horizun.structural-requirements/1'; units = 'millimeter'
    requirement_set = @{ id = 'l4-slabs'; version = '1.0.0'; title = 'L4 slabs, synthetic' }
    bar_types = @(@{ id = 'N16'; type_name = '16M' }, @{ id = 'N10'; type_name = '10M' })
    mat_rules = @(
        [ordered]@{ id = 'H1-bot'; host = @{ element_ids = @(, $s1) }; face_normal = @(0, 0, -1)
            components = @((New-HzComp $s1 'x' @(1, 0, 0) 8 200 $null), (New-HzComp $s1 'y' @(0, 1, 0) 24 200 $null)) },
        # Negativos over a support line at 3000 mm along X: top bars across it, 1200 long.
        [ordered]@{ id = 'H1-neg'; host = @{ element_ids = @(, $s1) }; face_normal = @(0, 0, 1)
            components = @(, (New-HzComp $s1 'x' @(1, 0, 0) 8 150 @{ run = @{ from_mm = 2400; to_mm = 3600 } })) },
        # A band of top bars across Y over 1000 mm of the slab's width only.
        [ordered]@{ id = 'H2'; host = @{ element_ids = @(, $s1) }; face_normal = @(0, 0, 1)
            components = @(, (New-HzComp $s1 'y' @(0, 1, 0) 24 150 @{ spread = @{ from_mm = 2500; to_mm = 3500 } })) },
        [ordered]@{ id = 'H3'; host = @{ element_ids = @(, $s2) }; face_normal = @(0, 0, -1)
            openings = @{ policy = 'omit'; minimum_size_mm = 100 }
            components = @((New-HzComp $s2 'x' @(1, 0, 0) 8 200 $null), (New-HzComp $s2 'y' @(0, 1, 0) 24 200 $null)) },
        [ordered]@{ id = 'H4-mesh'; host = @{ element_ids = @(, $top) }; face_normal = @(0, 0, 1)
            components = @(, [ordered]@{ name = 'x'; direction = @(1, 0, 0); bar_type = 'N10'; offset_from_face_mm = [Math]::Round($thin.mm / 2, 1)
                end_cover_mm = $cv[[string]$top]; side_cover_mm = $cv[[string]$top] + 6; allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 250 } }) },
        [ordered]@{ id = 'H5'; host = @{ element_ids = @(, $drop) }; face_normal = @(0, 0, -1)
            components = @((New-HzComp $drop 'x' @(1, 0, 0) 8 150 $null), (New-HzComp $drop 'y' @(0, 1, 0) 24 150 $null)) })
    section_rules = @($joists | ForEach-Object { [ordered]@{ id = "H4-j$_"; host = @{ element_ids = @(, $_) }; cover = @{ source = 'host' }
        layers = @(, @{ face = 'bottom'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true })
        stirrups = [ordered]@{ bar_type = 'N10'; allow_new_shape = $true; cover = @{ source = 'host' }
            zones = @(, @{ name = 'all'; layout = @{ rule = 'maximum_spacing'; spacing_mm = 250 } }) } } })
}

Write-Host "`n== plan ==" -ForegroundColor Cyan
$rows = Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan' -Arguments @{ target_document = $Document; requirement_set = $set }).Result
function Show-HzRows([string]$Like) {
    $r = @($rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -like $Like })
    $bad = @($r | Where-Object { Get-HzProp $_ 'code' })
    [pscustomobject]@{ Rows = $r; Bad = $bad; Text = ("rows={0} refused={1} {2}" -f $r.Count, $bad.Count,
        ((@($bad | Select-Object -First 2 | ForEach-Object { [string](Get-HzProp $_ 'rule_id') + ' ' + (Get-HzProp $_ 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $_ 'why')) 250) })) -join ' | ')) }
}
$h1 = Show-HzRows 'H1-*'; $h2 = Show-HzRows 'H2*'; $h3 = Show-HzRows 'H3*'; $h4 = Show-HzRows 'H4*'; $h5 = Show-HzRows 'H5*'
$neg = @($h1.Rows | Where-Object { [string](Get-HzProp $_ 'rule_id') -eq 'H1-neg#x' })[0]
$negLen = $(if ($neg) { [double](Get-HzProp $neg 'expected_bar_length_mm') } else { 0 })
Add-HzProbe -Run $run -Id 'H1' -Name 'L4 a two-way bottom mat and top bars over a support line only (run window)' -Expected '3 rows, none refused; the top bars 1200 mm long' `
    -Observed ($h1.Text + " neg_length=$negLen") -Ok ($h1.Rows.Count -eq 3 -and $h1.Bad.Count -eq 0 -and [Math]::Abs($negLen - 1200) -lt 0.5) -Evidence @{ rows = $h1.Rows }
Add-HzProbe -Run $run -Id 'H2' -Name 'L4 a band of top bars across part of the slab (spread window)' -Expected '1 row, array 1000 mm' `
    -Observed ($h2.Text + ' array=' + [string](Get-HzPath @($h2.Rows)[0] 'layout', 'array_length_mm')) `
    -Ok ($h2.Rows.Count -eq 1 -and $h2.Bad.Count -eq 0 -and [Math]::Abs([double](Get-HzPath @($h2.Rows)[0] 'layout', 'array_length_mm') - 1000) -lt 0.5) -Evidence @{ rows = $h2.Rows }
Add-HzProbe -Run $run -Id 'H3' -Name 'L4 a slab with an opening: the mat omits what would cross the hole' -Expected 'rows planned (runs split round the opening), none refused' `
    -Observed $h3.Text -Ok ($h3.Rows.Count -ge 3 -and $h3.Bad.Count -eq 0) -Evidence @{ rows = $h3.Rows }
Add-HzProbe -Run $run -Id 'H4' -Name 'L4 a ribbed slab: joists as beams, topping with a temperature mesh' -Expected 'joist rows and the mesh, none refused' `
    -Observed $h4.Text -Ok ($h4.Rows.Count -ge 5 -and $h4.Bad.Count -eq 0) -Evidence @{ rows = $h4.Rows }
Add-HzProbe -Run $run -Id 'H5' -Name 'L4 a drop panel (abaco) under the slab with its own bottom mat' -Expected '2 rows, none refused' `
    -Observed $h5.Text -Ok ($h5.Rows.Count -eq 2 -and $h5.Bad.Count -eq 0) -Evidence @{ rows = $h5.Rows }

$ns = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l4-ns'; version = '1' }; bar_types = $set.bar_types
    mat_rules = @(, [ordered]@{ id = 'H6'; host = @{ element_ids = @(, $nonStruct) }; face_normal = @(0, 0, -1)
        components = @(, [ordered]@{ name = 'x'; direction = @(1, 0, 0); bar_type = 'N16'; offset_from_face_mm = 50; end_cover_mm = 25; side_cover_mm = 33
            allow_new_shape = $true; layout = @{ rule = 'maximum_spacing'; spacing_mm = 200 } }) }) }
$nsRow = @(Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-ns' -Arguments @{ target_document = $Document; requirement_set = $ns }).Result)[0]
Add-HzProbe -Run $run -Id 'H6' -Name 'L4 a non-structural floor as a host is refused by name before any write' -Expected 'host_not_structural' `
    -Observed ([string](Get-HzProp $nsRow 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $nsRow 'why')) 300)) `
    -Ok ([string](Get-HzProp $nsRow 'code') -eq 'host_not_structural' -or [string](Get-HzProp $nsRow 'why') -match 'host_not_structural') -Evidence @{ row = $nsRow }

# H9: a section rule declaring 25 mm on a joist whose own cover is larger is refused by name.
$low = [ordered]@{ schema = $set.schema; units = $set.units; requirement_set = @{ id = 'l4-low'; version = '1' }; bar_types = $set.bar_types
    section_rules = @(, [ordered]@{ id = 'H9'; host = @{ element_ids = @(, $joists[0]) }; cover = @{ distance_mm = 25 }
        layers = @(, @{ face = 'bottom'; count = 2; bar_type = 'N16'; extend = 'host_length'; allow_new_shape = $true }) }) }
$lowRow = @(Get-HzRows (Invoke-HzToolStrict -Run $run -Tool 'horizun_plan_reinforcement' -Label 'plan-low' -Arguments @{ target_document = $Document; requirement_set = $low }).Result)[0]
Add-HzProbe -Run $run -Id 'H9' -Name 'L4 a section cover declared below the host cover is refused by name (Revit would hold the bars to the host cover)' `
    -Expected 'section_rule_refused naming section_cover_below_the_host_cover' `
    -Observed ([string](Get-HzProp $lowRow 'code') + ': ' + (Limit-HzText ([string](Get-HzProp $lowRow 'why')) 300)) `
    -Ok ([string](Get-HzProp $lowRow 'why') -match 'section_cover_below_the_host_cover') -Evidence @{ row = $lowRow }

Write-Host "`n== apply and audit ==" -ForegroundColor Cyan
$apply = Invoke-HzWrite -Run $run -Tool 'horizun_apply_reinforcement' -Label 'apply' -AllowRefusal -Arguments @{ target_document = $Document; requirement_set = $set; transaction_name = 'HZ rebar L4 slabs' }
$applyOk = $null -ne $apply.Apply -and $apply.Apply.Ok
$verif = @($(if ($apply.Apply) { @(Get-HzPath $apply.Apply.Result 'verification') }))
$notOk = @($verif | Where-Object { $null -ne $_ -and (Get-HzProp $_ 'verified') -ne $true })
Add-HzProbe -Run $run -Id 'H7' -Name 'L4 the slabs apply and every set reads back' -Expected 'every set verified' `
    -Observed ("applied={0} verified={1}/{2} first_failure={3}" -f $applyOk, ($verif.Count - $notOk.Count), $verif.Count,
        $(if ($notOk.Count) { [string](Get-HzProp $notOk[0] 'rule_id') + ' ' + (Limit-HzText ((Get-HzProp $notOk[0] 'checks') | ConvertTo-Json -Depth 6 -Compress) 600) }
          elseif (-not $applyOk) { Limit-HzText $(if ($apply.Apply) { $apply.Apply.Text } else { $apply.Dry.Text }) 500 } else { '' })) `
    -Ok ($applyOk -and $verif.Count -gt 0 -and $notOk.Count -eq 0) -Evidence @{ verification = $verif }
$audit = Invoke-HzToolStrict -Run $run -Tool 'horizun_audit_reinforcement' -Label 'audit' -Arguments @{ target_document = $Document; requirement_set = $set }
$ours = @(@(Get-HzPath $audit.Result 'findings') | Where-Object { [string](Get-HzProp $_ 'rule_id') -match '^H[1-5]' -and [string](Get-HzProp $_ 'severity') -ne 'info' })
Add-HzProbe -Run $run -Id 'H8' -Name 'L4 the audit finds the slab steel where the rules put it' -Expected 'no error or unknown finding on H1-H5' `
    -Observed ("findings={0} {1}" -f $ours.Count, (($ours | Select-Object -First 4 | ForEach-Object { [string]$_.rule_id + ' ' + [string]$_.code + ' exp=' + [string]$_.expected + ' obs=' + [string]$_.observed }) -join '; ')) `
    -Ok ($ours.Count -eq 0) -Evidence @{ findings = $ours }

$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
