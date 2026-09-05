#Requires -Version 5.1
<#
  THE WALL-SPLIT LIVE CAMPAIGN, in one Revit 2026 session.

  Every case reports one of five answers and only one of them is good:
  passed, failed, unverified, not_run, blocked_fixture, blocked_environment and
  unsupported_api. A case whose fixture
  could not be built is NOT a case that passed, and nothing here folds one into
  the other - that rule is the whole reason this file exists rather than a
  narration.

  The document is C:\hz-live\HZ_WALLSPLIT.rvt, created by wallsplit-newmodel.ps1
  from Revit's own multi-discipline metric template. It is disposable, it is
  never saved, and it is nobody's project.
#>
[CmdletBinding()]
param(
    [string]$Document = 'HZ_WALLSPLIT',
    # REPO-RELATIVE. This defaulted to an absolute path under one machine's user
    # profile, which named the user in a file that ships in the repository and
    # pointed at a directory nobody else has.
    [string]$FixtureJson = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts/live/fixture-build.json'),
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hz-wallsplit.lib.ps1')

$run = New-WsRun -Name 'wallsplit-campaign' -Document $Document -ArtifactDir $ArtifactDir
Write-Host ("artifacts: " + $run.ArtifactDir) -ForegroundColor DarkGray

# ---------------------------------------------------------------- identity
$health = Invoke-Ws -Run $run -Tool 'horizun_health' -Label 'identity' -Arguments @{}
$identity = [ordered]@{
    horizun_commit = $null; revit = $null; built_from_clean_tree = $null; document = $null
}
if ($health.Result) {
    $identity.horizun_commit = [string]$health.Result.horizun_commit
    $identity.revit = [string]$health.Result.revit_version
    $identity.built_from_clean_tree = $health.Result.built_from_clean_tree
    $identity.document = if ($health.Result.active_document) { [string]$health.Result.active_document.title } else { $null }
}
Write-Host ("bridge   : commit " + $identity.horizun_commit + "  Revit " + $identity.revit +
            "  clean " + $identity.built_from_clean_tree + "  doc " + $identity.document) -ForegroundColor Cyan
if ($identity.document -ne $Document) {
    throw ("HARNESS: the active document is '" + $identity.document + "' and this campaign is about '" + $Document +
           "'. Refusing to measure a document nobody asked about.")
}

# ---------------------------------------------------------------- fixture
$fx = (Get-Content -LiteralPath $FixtureJson -Raw | ConvertFrom-Json).result.output
$made = $fx.made
function Wall-Of { param([string]$Key)
    if ($made.PSObject.Properties.Name -contains $Key) { [long]$made.$Key.wall } else { 0 }
}
function Extra-Of { param([string]$Key, [string]$Field)
    if ($made.PSObject.Properties.Name -contains $Key) {
        $v = $made.$Key
        if ($v.PSObject.Properties.Name -contains $Field) { return $v.$Field }
    }
    $null
}

# ---------------------------------------------------------------- helpers
function Dry { param([long[]]$Ids)
    Invoke-Ws -Run $run -Tool 'horizun_split_multilayer_walls' -Label ("dry-" + ($Ids -join '_')) -AllowError -Arguments @{
        target_document = $Document; element_ids = @($Ids); dry_run = $true
    }
}
function Apply { param([long[]]$Ids, [string]$Token)
    Invoke-Ws -Run $run -Tool 'horizun_split_multilayer_walls' -Label ("apply-" + ($Ids -join '_')) -AllowError -Mutates -Arguments @{
        target_document = $Document; element_ids = @($Ids); dry_run = $false; confirmation_token = $Token
    }
}
function Eligible-Of { param($Reply, [long]$Id)
    if (-not $Reply.Result) { return $null }
    foreach ($e in @($Reply.Result.eligible)) { if ([long]$e.wall_id -eq $Id) { return $e } }
    $null
}
function Rejected-Of { param($Reply, [long]$Id)
    if (-not $Reply.Result) { return $null }
    foreach ($e in @($Reply.Result.rejected)) { if ([long]$e.wall_id -eq $Id) { return $e } }
    $null
}
function Converted-Of { param($Reply, [long]$Id)
    if (-not $Reply.Result) { return $null }
    foreach ($e in @($Reply.Result.already_converted)) { if ([long]$e.wall_id -eq $Id) { return $e } }
    $null
}
function WallReply-Of { param($Reply, [long]$Id)
    if (-not $Reply.Result) { return $null }
    foreach ($w in @($Reply.Result.walls)) { if ([long]$w.source_wall_id -eq $Id) { return $w } }
    $null
}

<#
  Convert one wall and hold the reply to the whole contract: it applied, every
  layer was verified, the carrier kept its id, and the post-commit pass agreed.
#>
function Convert-And-Check {
    param([int]$Case, [string]$Name, [string]$Key, [string]$Expected)

    $id = Wall-Of $Key
    if ($id -eq 0) {
        Add-WsCase -Run $run -Number $Case -Name $Name -Expected $Expected -Status 'blocked_fixture' `
            -Observed "the fixture has no wall for '$Key'" -Because 'the fixture builder did not produce this wall'
        return $null
    }

    $dry = Dry @($id)
    $e = Eligible-Of $dry $id
    if (-not $e) {
        $r = Rejected-Of $dry $id
        Add-WsCase -Run $run -Number $Case -Name $Name -Expected $Expected -Status 'failed' `
            -Observed ("not eligible: " + $(if ($r) { "$($r.reason_code) - $($r.reason)" } else { 'no entry at all' })) `
            -Evidence @{ dry_run_file = $dry.File }
        return $null
    }

    $token = [string]$dry.Result.confirmation_token
    $ap = Apply @($id) $token
    $w = WallReply-Of $ap $id

    if (-not $w) {
        Add-WsCase -Run $run -Number $Case -Name $Name -Expected $Expected -Status 'failed' `
            -Observed ("the apply produced no entry for this wall: " + (Limit-WsText $ap.Text 200)) `
            -Evidence @{ apply_file = $ap.File }
        return $null
    }

    $post = $null
    if ($w.PSObject.Properties.Name -contains 'verification_after_outer_commit') {
        $post = $w.verification_after_outer_commit
    }
    $ok = $w.applied -and ($ap.Result.all_verified -eq $true) -and ($post -and $post.passed -eq $true)

    Add-WsCase -Run $run -Number $Case -Name $Name -Expected $Expected `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed ("applied=" + $w.applied + " produced=" + $w.walls_produced + "/" + $w.walls_expected +
                   " all_verified=" + $ap.Result.all_verified +
                   " post_commit=" + $(if ($post) { $post.passed } else { 'absent' }) +
                   $(if ($w.code) { " code=" + $w.code + " " + (Limit-WsText $w.message 160) } else { '' })) `
        -Evidence @{
            dry_run_file = $dry.File; apply_file = $ap.File
            walls_expected = $w.walls_expected; walls_produced = $w.walls_produced
            carrier_id = $w.source_wall_id; carrier_unique_id = $w.source_wall_unique_id
            core_carrier_layer_index = $w.core_carrier_layer_index
            core_carrier_selection_reason = $w.core_carrier_selection_reason
            original_location_line = $w.original_location_line
            originals_deleted = $ap.Result.originals_deleted
            unexpected_warnings = @($ap.Result.unexpected_warnings)
            layers = @($w.layers | ForEach-Object {
                @{ n = $_.layer_number; material = $_.material_name; type = $_.actual_type_name
                   expected_offset_mm = $_.expected_offset_mm; observed_offset_mm = $_.observed_offset_mm
                   deviation_mm = $_.deviation_mm; geometry_verified = $_.geometry_verified
                   naming_verified = $_.naming_verified; single_layer = $_.single_layer_verified
                   materialised = $_.materialised; wall = $_.resulting_wall_id }
            })
        }

    [ordered]@{ Id = $id; Dry = $dry; Apply = $ap; Wall = $w; Ok = $ok }
}

<#
  A wall this capability must REFUSE, and refuse for the named reason. A refusal
  for the wrong reason has not passed.
#>
function Refuse-And-Check {
    param([int]$Case, [string]$Name, [string]$Key, [string]$Code, [string]$Expected)

    $id = Wall-Of $Key
    if ($id -eq 0) {
        Add-WsCase -Run $run -Number $Case -Name $Name -Expected $Expected -Status 'blocked_fixture' `
            -Observed "the fixture has no wall for '$Key'"
        return
    }
    $dry = Dry @($id)
    $r = Rejected-Of $dry $id
    $ok = $r -and ([string]$r.reason_code -eq $Code)
    Add-WsCase -Run $run -Number $Case -Name $Name -Expected $Expected `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed $(if ($r) { "refused: $($r.reason_code)" } else { 'NOT refused - it came back eligible' }) `
        -Evidence @{ dry_run_file = $dry.File; reason = $(if ($r) { $r.reason } else { $null }) }
}

Write-Host ""
Write-Host "=== geometry and the core ===" -ForegroundColor Cyan

$c1 = Convert-And-Check 1 'straight multilayer wall, single-layer core' 'c01' `
    'seven layers become seven single-layer walls, the original kept as the core carrier'

$c2 = Convert-And-Check 2 'core made of several layers' 'c02_wide_core' `
    'the carrier is chosen inside a multi-layer core by the documented order'

Refuse-And-Check 4 'a wall with a single layer' 'c04_single_layer' 'single_layer' `
    'refused as single_layer - there is nothing to split'

$c5 = Convert-And-Check 5 'flipped wall' 'c05' `
    'the layers land on the same sides they did before the wall was flipped'

Write-Host ""
Write-Host "=== the six location lines ===" -ForegroundColor Cyan
foreach ($ll in 'WallCenterline', 'CoreCenterline', 'FinishFaceExterior', 'FinishFaceInterior', 'CoreExterior', 'CoreInterior') {
    $null = Convert-And-Check 6 ("location line " + $ll) ("c06_" + $ll) `
        ("every layer lands at its planned offset with the wall drawn on " + $ll)
}

Write-Host ""
Write-Host "=== arcs ===" -ForegroundColor Cyan
$null = Convert-And-Check 7 'arc wall' 'c07_arc' 'each layer keeps the centre and angles and changes only its radius'
$null = Convert-And-Check 9 'arc wall, flipped' 'c09_arc_flipped' 'the same, with the exterior side on the other radius'

Write-Host ""
Write-Host "=== constraints, inserts and openings ===" -ForegroundColor Cyan
$null = Convert-And-Check 11 'top-constrained wall' 'c11_top_constrained' 'the layer walls keep the top constraint'
$null = Convert-And-Check 12 'pinned wall' 'c12_pinned' 'the pin is restored on the carrier and the layers'
$null = Convert-And-Check 13 'wall with a door' 'c13_door' 'the door keeps its ElementId, UniqueId and host'
$null = Convert-And-Check 15 'wall with a window' 'c15_window' 'the window keeps its identity, sill and head'
$null = Convert-And-Check 16 'wall with several doors and windows' 'c16_many' 'every insert keeps its identity'
$null = Convert-And-Check 17 'wall with a rectangular opening' 'c17_opening' 'the opening stays on the carrier and cuts every layer'
$null = Convert-And-Check 19 'wall joined at both ends' 'c19_joined' 'both original joins are restored'

Write-Host ""
Write-Host "=== refusals ===" -ForegroundColor Cyan
Refuse-And-Check 10 'stacked wall' 'c10_stacked' 'unsupported_stacked_wall' `
    'refused by name - its root hosts the doors and cannot become a single-layer carrier'
Refuse-And-Check 100 'curtain wall' 'c_curtain' 'not_basic_wall' 'refused - a curtain wall has no compound structure'

$summary = Save-WsRun -Run $run -Identity $identity
