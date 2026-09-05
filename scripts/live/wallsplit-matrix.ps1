#Requires -Version 5.1
<#
  THE 55-CASE MATRIX, in one Revit 2026 session, against one candidate.

  SEVEN answers, and only one of them is good: passed, failed, unverified,
  not_run, blocked_fixture, blocked_environment, unsupported_api - the set
  Add-WsCase actually accepts. The roll-up asserts they add to 55; a bucket that
  quietly disappears is how a matrix flatters itself.

  This header used to name five, two of which the library rejects, and cases 34
  and 35 passed one of them. Under StrictMode that is a binding exception, so
  the run died at case 34 - on the exact path a failing run takes.

  Nothing here turns "the fixture could not be built" into "it worked", and the
  six location lines are ONE case that passes only if all six do. Inflating them
  into six rows would make the denominator disagree with the mandate.

  The document is C:\hz-live\HZ_WALLSPLIT.rvt: disposable, created from Revit's
  own multi-discipline metric template, never anybody's project, never saved by
  this script.
#>
[CmdletBinding()]
param(
    [string]$Document = 'HZ_WALLSPLIT',
    [string]$ExpectCommit,
    [string]$ArtifactDir,
    [string]$RepoHead = 'unset',
    [switch]$ContinueAfterCanary
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hz-wallsplit.lib.ps1')

$run = New-WsRun -Name 'wallsplit-matrix' -Document $Document -ArtifactDir $ArtifactDir
$art = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts\live'
Write-Host ("artifacts: " + $run.ArtifactDir) -ForegroundColor DarkGray

# ------------------------------------------------------------------ identity
$health = Invoke-Ws -Run $run -Tool 'horizun_health' -Label 'identity' -Arguments @{}
if (-not $health.Result) { throw 'the bridge did not answer horizun_health' }
$identity = [ordered]@{
    horizun_commit        = [string]$health.Result.horizun_commit
    horizun_version       = [string]$health.Result.horizun_version
    built_from_clean_tree = $health.Result.built_from_clean_tree
    revit_version         = [string]$health.Result.revit_version
    revit_build           = [string]$health.Result.revit_build
    document              = [string]$health.Result.active_document.title
    document_path         = [string]$health.Result.active_document.path
}
Write-Host ("bridge: " + $identity.horizun_commit.Substring(0, 7) + "  Revit " + $identity.revit_version +
            "  clean " + $identity.built_from_clean_tree + "  doc " + $identity.document) -ForegroundColor Cyan
if ($identity.document -ne $Document) { throw "the active document is '$($identity.document)', not $Document" }
if ($ExpectCommit -and $identity.horizun_commit -ne $ExpectCommit) {
    throw ("the bridge is running " + $identity.horizun_commit + " and this run was told to expect " + $ExpectCommit)
}
# Every checkpoint from here on carries this. A checkpoint written before health
# answered would be stamping a commit nobody measured, so the stamp is set once,
# here, and the fingerprint is the document's own identity rather than its path -
# the path names a user, the fingerprint names the file.
# ONLY FIELDS THE REPLY ACTUALLY CARRIES. This asked for element_count, which
# horizun_health does not publish on active_document, and StrictMode stopped the
# run on the first line - before a single case. Whatever the payload holds is
# used, and nothing is assumed to be there.
$docProps = @($health.Result.active_document.PSObject.Properties.Name)
$fpParts = @($identity.document)
foreach ($field in 'is_workshared', 'is_family_document', 'has_been_saved_to_disk') {
    if ($docProps -contains $field) { $fpParts += ($field + '=' + [string]$health.Result.active_document.$field) }
}
$docFingerprint = ($fpParts -join '|')
Set-WsIdentity -Head $RepoHead -Installed $identity.horizun_commit -DocumentFingerprint $docFingerprint
Write-Host ("document fingerprint: " + $docFingerprint) -ForegroundColor DarkGray

# ------------------------------------------------------------------ fixture map
$made = @{}
$whyNot = @{}
$typeCounts = @{}
foreach ($f in 'fixture-build.json', 'fixture2-build.json', 'fixture3-build.json',
                'fixture4-build.json', 'fixture5-build.json', 'fixture6-build.json') {
    $p = Join-Path $art $f
    if (-not (Test-Path $p)) { continue }
    $o = (Get-Content -LiteralPath $p -Raw | ConvertFrom-Json).result.output
    foreach ($prop in $o.made.PSObject.Properties) { $made[$prop.Name] = $prop.Value }
    # WHY something was not built is evidence too, and it is the only thing that
    # separates "we did not try" from "Revit refused, and here is what it said".
    if (@($o.PSObject.Properties.Name) -contains 'unbuildable' -and $o.unbuildable) {
        foreach ($prop in $o.unbuildable.PSObject.Properties) { $whyNot[$prop.Name] = [string]$prop.Value }
    }
    if (@($o.PSObject.Properties.Name) -contains 'type_counts' -and $o.type_counts) {
        foreach ($prop in $o.type_counts.PSObject.Properties) { $typeCounts[$prop.Name] = $prop.Value }
    }
}
Write-Host ("fixture keys: " + $made.Count + "   recorded refusals: " + $whyNot.Count) -ForegroundColor DarkGray

function Id { param([string]$Key) if ($made.ContainsKey($Key)) { [long]$made[$Key].wall } else { 0 } }
function Fx { param([string]$Key, [string]$Field)
    if ($made.ContainsKey($Key)) {
        $v = $made[$Key]
        if ($v.PSObject.Properties.Name -contains $Field) { return $v.$Field }
    }
    $null }

function Evidence-Field {
    param($Evidence, [Parameter(Mandatory)][string]$Field)
    if ($null -eq $Evidence) { return $null }
    if ($Evidence -is [System.Collections.IDictionary]) {
        if ($Evidence.Contains($Field)) { return $Evidence[$Field] }
        return $null
    }
    if (@($Evidence.PSObject.Properties.Name) -contains $Field) { return $Evidence.$Field }
    $null
}

# ------------------------------------------------------------------ calls
function Dry { param([long[]]$Ids)
    Invoke-Ws -Run $run -Tool 'horizun_split_multilayer_walls' -Label ("dry-" + ($Ids -join '_')) -AllowError -Arguments @{
        target_document = $Document; element_ids = @($Ids); dry_run = $true } }
function Apply { param([long[]]$Ids, [string]$Token)
    Invoke-Ws -Run $run -Tool 'horizun_split_multilayer_walls' -Label ("apply-" + ($Ids -join '_')) -AllowError -Mutates -Arguments @{
        target_document = $Document; element_ids = @($Ids); dry_run = $false; confirmation_token = $Token } }
function Pick { param($Reply, [string]$Bucket, [long]$Id)
    if (-not $Reply.Result) { return $null }
    if (@($Reply.Result.PSObject.Properties.Name) -notcontains $Bucket) { return $null }
    $field = if ($Bucket -eq 'walls') { 'source_wall_id' } else { 'wall_id' }
    foreach ($e in @($Reply.Result.$Bucket)) { if ([long]$e.$field -eq $Id) { return $e } }
    $null }

function Remove-RecordedCase { param([int]$Number)
    for ($i = $run.Probes.Count - 1; $i -ge 0; $i--) {
        if ([int]$run.Probes[$i].case -eq $Number) { $run.Probes.RemoveAt($i) }
    }
}

<# Every field the mandate requires each case to carry. #>
function Evidence { param($Dry, $Ap, [long]$Id)
    $e = Pick $Dry 'eligible' $Id
    $w = if ($Ap) { Pick $Ap 'walls' $Id } else { $null }
    $post = if ($w -and (@($w.PSObject.Properties.Name) -contains 'verification_after_outer_commit')) { $w.verification_after_outer_commit } else { $null }
    # THE REVERSIBLE PASS, read directly. It was being inferred from all_verified,
    # which is a broader flag - it also carries the warnings - so "the reversible
    # pass passed" could be reported false by something that pass never looked at.
    $pre = if ($w -and (@($w.PSObject.Properties.Name) -contains 'verification_before_subtransaction_commit')) {
        $w.verification_before_subtransaction_commit } else { $null }
    [ordered]@{
        case_source_wall_id      = $Id
        source_wall_unique_id    = if ($e) { $e.wall_unique_id } elseif ($w) { $w.source_wall_unique_id } else { $null }
        cross_section_eligible   = [bool]$e
        core_carrier             = if ($w) { @{ layer_index = $w.core_carrier_layer_index; reason = $w.core_carrier_selection_reason } }
                                   elseif ($e) { @{ layer_index = $e.core_carrier_layer_index; reason = $e.core_carrier_selection_reason } } else { $null }
        location_line            = if ($e) { $e.original_location_line } elseif ($w) { $w.original_location_line } else { $null }
        geometry_class           = if ($e) { $e.geometry_class } else { $null }
        exterior_normal          = if ($e) { @{ source = $e.exterior_normal_source; corroborated = $e.exterior_normal_corroborated; agreement = $e.exterior_normal_agreement } } else { $null }
        dependencies_before      = if ($e) { @($e.dependency_ledger | ForEach-Object { @{ id = $_.element_id; kind = $_.kind; disposition = $_.disposition } }) } else { @() }
        dependencies_after       = if ($w) { @($w.dependency_ledger | ForEach-Object { @{ id = $_.element_id; kind = $_.kind; disposition = $_.disposition } }) } else { @() }
        structural_before        = if ($e) { @($e.dependency_ledger | Where-Object { $_.kind -match 'rebar|foundation|reinforcement|fabric' } | ForEach-Object { @{ id = $_.element_id; kind = $_.kind } }) } else { @() }
        structural_after         = if ($w) { @($w.dependency_ledger | Where-Object { $_.kind -match 'rebar|foundation|reinforcement|fabric' } | ForEach-Object { @{ id = $_.element_id; kind = $_.kind } }) } else { @() }
        resulting_wall_ids       = if ($w) { @($w.layers | Where-Object { $_.resulting_wall_id } | ForEach-Object { $_.resulting_wall_id }) } else { @() }
        layers                   = if ($w) { @($w.layers | ForEach-Object { [ordered]@{
                                       layer_number = $_.layer_number; material = $_.material_name
                                       planned_type_name = $_.planned_type_name; expected_type_name = $_.expected_type_name
                                       actual_type_name = $_.actual_type_name; naming_verified = $_.naming_verified
                                       expected_offset_mm = $_.expected_offset_mm; observed_offset_mm = $_.observed_offset_mm
                                       deviation_mm = $_.deviation_mm; geometry_verified = $_.geometry_verified
                                       cut_probed = $_.cut_probed; cut_verified = $_.cut_verified
                                       cut_not_probed_reason = $_.cut_not_probed_reason
                                       claims_withdrawn = $_.claims_withdrawn
                                       single_layer_verified = $_.single_layer_verified; materialised = $_.materialised
                                       resulting_wall_id = $_.resulting_wall_id
                                       resulting_wall_unique_id = $_.resulting_wall_unique_id } }) } else { @() }
        pre_commit_verification  = if ($pre) { @{ passed = $pre.passed; code = $pre.code
                                                   cut_coverage = $pre.cut_coverage } } else { $null }
        cut_coverage_pre         = if ($pre) { $pre.cut_coverage } else { $null }
        provenance               = if ($post) { $post.provenance } else { $null }
        insert_checks            = if ($post) { $post.dependencies } else { $null }
        cut_coverage             = if ($post) { $post.cut_coverage } else { $null }
        joins                    = if ($post) { $post.joins } else { $null }
        rollback                 = if ($w) { @{ status = $w.rollback_status; confirmed = $w.rollback_confirmed } } else { $null }
        post_commit_verification = if ($post) { @{ passed = $post.passed; code = $post.code; can_roll_back = $post.can_roll_back } } else { $null }
        warnings                 = if ($Ap) { @($Ap.Result.unexpected_warnings) } else { @() }
        originals_deleted        = if ($Ap) { $Ap.Result.originals_deleted } else { $null }
        walls_expected           = if ($w) { $w.walls_expected } else { $null }
        walls_produced           = if ($w) { $w.walls_produced } else { $null }
        dry_run_artifact         = $Dry.File
        apply_artifact           = if ($Ap) { $Ap.File } else { $null }
    } }

<# A wall that must convert and be verified end to end. #>
function Positive { param([int]$N, [string]$Name, [string]$Key, [string]$Expected)
    $id = Id $Key
    if ($id -eq 0) { Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'blocked_fixture' `
        -Observed "no fixture wall for '$Key' in this document" -Because "no fixture wall for '$Key' in this document"; return $null }
    $ctx = Start-WsCase -Run $run -Number $N -Name $Name -WallId $id -WallUniqueId ([string](Fx $Key 'unique_id')) -Operation 'split_multilayer_walls'

    # BEFORE THE CALL, NOT AFTER. A checkpoint written after Revit answers is
    # exactly the one that is missing when Revit does not answer.
    Write-WsCheckpoint $ctx 'dry_run_started' 'nothing asked yet'
    $dry = Dry @($id)
    Write-WsCheckpoint $ctx 'dry_run_received' ("parsed=" + $dry.Parsed + " error=" + $dry.IsError) $dry.File

    $e = Pick $dry 'eligible' $id
    if (-not $e) {
        $r = Pick $dry 'rejected' $id
        Write-WsCheckpoint $ctx 'final' ('not eligible: ' + $(if ($r) { $r.reason_code } else { 'absent' }))
        Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'failed' `
            -Observed ("not eligible: " + $(if ($r) { $r.reason_code } else { 'absent from every bucket' })) `
            -Evidence (Evidence $dry $null $id); return $null }

    # THE DANGEROUS ONE. Everything known about the wall is on disk before the
    # write is asked for, so a termination inside it still names the wall.
    $ctx.LastRegenerate = 'about to enter the executor'
    Write-WsCheckpoint $ctx 'apply_started' 'the transaction has not been opened yet'
    $ap = Apply @($id) ([string]$dry.Result.confirmation_token)
    Write-WsCheckpoint $ctx 'apply_received' ("parsed=" + $ap.Parsed + " error=" + $ap.IsError) $ap.File

    $w = Pick $ap 'walls' $id
    Write-WsCheckpoint $ctx 'verification_started' 'reading the reply back'
    $ev = Evidence $dry $ap $id
    Write-WsCheckpoint $ctx 'verification_received' ("applied=" + $(if ($w) { $w.applied } else { 'no entry' }))
    if (-not $w) {
        Write-WsCheckpoint $ctx 'final' 'the apply produced no entry'
        Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'failed' -Observed 'the apply produced no entry' -Evidence $ev; return $null }
    $post = $ev.post_commit_verification
    $ok = $w.applied -and ($ap.Result.all_verified -eq $true) -and $post -and ($post.passed -eq $true)
    Write-WsCheckpoint $ctx 'final' ("applied=" + $w.applied + " ok=" + [bool]$ok)
    Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed ("applied={0} produced={1}/{2} all_verified={3} post={4}{5}" -f $w.applied, $w.walls_produced, $w.walls_expected,
                   $ap.Result.all_verified, $(if ($post) { $post.passed } else { 'absent' }), $(if ($w.code) { " code=$($w.code)" } else { '' })) `
        -Evidence $ev
    [ordered]@{ Id = $id; Dry = $dry; Apply = $ap; Wall = $w; Ok = $ok; Evidence = $ev; Token = [string]$dry.Result.confirmation_token } }

<# A wall that must be REFUSED, for the named reason, and must never reach the executor. #>
function Negative { param([int]$N, [string]$Name, [string]$Key, [string]$Code, [string]$Expected)
    $id = Id $Key
    if ($id -eq 0) { Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'blocked_fixture' `
        -Observed "no fixture wall for '$Key' in this document" -Because "no fixture wall for '$Key' in this document"; return }
    $ctx = Start-WsCase -Run $run -Number $N -Name $Name -WallId $id -WallUniqueId ([string](Fx $Key 'unique_id')) -Operation 'split_multilayer_walls (refusal expected)'
    Write-WsCheckpoint $ctx 'dry_run_started' 'a refusal is expected; no write is planned'
    $dry = Dry @($id)
    Write-WsCheckpoint $ctx 'dry_run_received' ("parsed=" + $dry.Parsed) $dry.File
    $r = Pick $dry 'rejected' $id
    $e = Pick $dry 'eligible' $id
    $ok = $r -and ([string]$r.reason_code -eq $Code) -and (-not $e)
    Write-WsCheckpoint $ctx 'final' ("refused=" + [bool]$r + " code=" + $(if ($r) { $r.reason_code } else { 'none' }))
    Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed $(if ($r) { "refused: $($r.reason_code)" } elseif ($e) { 'ELIGIBLE - it reached the executor' } else { 'absent from every bucket' }) `
        -Evidence ([ordered]@{ case_source_wall_id = $id; reason_code = $(if ($r) { $r.reason_code }); reason = $(if ($r) { $r.reason })
                               entered_executor = [bool]$e; dry_run_artifact = $dry.File }) }

<#
  THREE DIFFERENT REASONS, THREE DIFFERENT ANSWERS.

  The old single Skip wrote 'not_covered' for all of them, and a reader could not
  tell a case that needs a prebuilt RVT from one Revit has no API for. Which one
  it is decides who can fix it and whether it is ever fixable at all:

    blocked_fixture     - a different document would exercise it. Ours does not
                          carry the type, or the object could not be constructed
                          on demand. Fixable by building the RVT.
    blocked_environment - it needs something outside this session: a second user,
                          a central model. Fixable by arranging the environment.
    unsupported_api     - Revit exposes no public way to set it up. Not fixable
                          by us at all, and saying so is the honest answer.
    not_run             - the campaign never reached it. A fact about the RUN,
                          never about the product.
#>
function BlockedFixture { param([int]$N, [string]$Name, [string]$Expected, [string]$Why)
    Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'blocked_fixture' -Observed $Why -Because $Why }
function BlockedEnv { param([int]$N, [string]$Name, [string]$Expected, [string]$Why)
    Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'blocked_environment' -Observed $Why -Because $Why }
function UnsupportedApi { param([int]$N, [string]$Name, [string]$Expected, [string]$Why)
    Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'unsupported_api' -Observed $Why -Because $Why }
function NotRun { param([int]$N, [string]$Name, [string]$Expected, [string]$Why)
    Add-WsCase -Run $run -Number $N -Name $Name -Expected $Expected -Status 'not_run' -Observed $Why -Because $Why }

<# What the fixture pass recorded when it could not build something. #>
function Why { param([string]$Key)
    if ($whyNot.ContainsKey($Key)) { return $whyNot[$Key] }
    'the fixture pass recorded no reason for this key' }

<#
  A structural case is only a measurement if the structural OBJECT exists. A wall
  with `rebar: null` on it would convert perfectly and prove nothing about rebar,
  and reporting that as a pass is the exact dishonesty this campaign is against.
#>
function Structural { param([int]$N, [string]$Name, [string]$Key, [string]$Field, [string]$Expected)
    $id = Id $Key
    if ($id -eq 0) { BlockedFixture $N $Name $Expected ("no fixture wall for '" + $Key + "': " + (Why $Key)); return $null }
    $obj = Fx $Key $Field
    if (-not $obj) {
        BlockedFixture $N $Name $Expected ("the wall exists but its " + $Field + " was never built: " + (Why $Key))
        return $null }
    Positive $N $Name $Key $Expected }

# =====================================================================  canary
# THE SIMPLEST THING THAT MUST WORK, before anything else is attempted.
#
# It is not one of the 55 and it is not counted in them: it is the gate that
# decides whether running the 55 would produce 55 measurements or 55 copies of
# one failure. The previous session learned this the expensive way - fourteen
# applies all failed for the same reason, and the fourteen rows said fourteen
# things when they were saying one.
#
# "success" from the command is NOT enough. The reply is read back field by
# field, and the wall is re-read from the model afterwards.
Write-Host ""; Write-Host "=== canary  the simplest wall that must convert ===" -ForegroundColor Cyan
$canaryId = Id 'canary'
$canaryOk = $false
$canaryDetail = @()
if ($canaryId -eq 0) {
    $canaryDetail += 'no canary wall in the fixture'
} else {
    $cctx = Start-WsCase -Run $run -Number 0 -Name 'canary' -WallId $canaryId `
        -WallUniqueId ([string](Fx 'canary' 'unique_id')) -Operation 'canary split'
    Write-WsCheckpoint $cctx 'dry_run_started' 'the canary has not been touched'
    $cdry = Dry @($canaryId)
    Write-WsCheckpoint $cctx 'dry_run_received' ("eligible=" + [bool](Pick $cdry 'eligible' $canaryId)) $cdry.File
    $ce = Pick $cdry 'eligible' $canaryId
    if (-not $ce) {
        $cr = Pick $cdry 'rejected' $canaryId
        $canaryDetail += ('not eligible: ' + $(if ($cr) { $cr.reason_code } else { 'absent from every bucket' }))
    } else {
        Write-WsCheckpoint $cctx 'apply_started' 'the canary apply is the first write of the campaign'
        $cap = Apply @($canaryId) ([string]$cdry.Result.confirmation_token)
        Write-WsCheckpoint $cctx 'apply_received' ("error=" + $cap.IsError) $cap.File
        $cw = Pick $cap 'walls' $canaryId
        $cev = Evidence $cdry $cap $canaryId
        $cpost = $cev.post_commit_verification

        # Twenty-five things, each read back rather than assumed.
        $layers = @($cev.layers)
        $withVolume = @($layers | Where-Object { $_.materialised })
        $zeroWidth = @($layers | Where-Object { -not $_.materialised })
        $checks = [ordered]@{
            'the apply reported applied'          = [bool]($cw -and $cw.applied)
            'the original was not deleted'        = ($cev.originals_deleted -eq 0)
            'the original id is still the source' = ($cev.case_source_wall_id -eq $canaryId)
            'the original UniqueId is recorded'   = [bool]$cev.source_wall_unique_id
            'the original became the carrier'     = ($cev.core_carrier -ne $null -and $cev.core_carrier.layer_index -ge 0)
            'one wall per layer with volume'      = ($cev.walls_produced -eq $cev.walls_expected -and $withVolume.Count -eq $cev.walls_produced)
            'zero-width layers made no wall'      = (@($zeroWidth | Where-Object { $_.resulting_wall_id }).Count -eq 0)
            'every result is single-layer'        = ($withVolume.Count -gt 0 -and @($withVolume | Where-Object { -not $_.single_layer_verified }).Count -eq 0)
            'every type name is as specified'     = (@($withVolume | Where-Object { -not $_.naming_verified }).Count -eq 0)
            'every layer geometry verified'       = (@($withVolume | Where-Object { -not $_.geometry_verified }).Count -eq 0)
            'every offset within tolerance'       = (@($withVolume | Where-Object { [math]::Abs([double]$_.deviation_mm) -gt 0.5 }).Count -eq 0)
            'the numbering starts at 01'          = (@($layers | Where-Object { $_.layer_number -eq 1 }).Count -eq 1)
            'the exterior normal was corroborated'= ($cev.exterior_normal -ne $null -and $cev.exterior_normal.corroborated -eq $true)
            'provenance was written'              = ($cpost -ne $null -and $cev.provenance -ne $null)
            'the reversible pass passed'          = ($cev.pre_commit_verification -ne $null -and
                                                     $cev.pre_commit_verification.passed -eq $true)
            'all_verified'                        = ($cap.Result.all_verified -eq $true)
            'the post-commit pass passed'         = ($cpost -ne $null -and $cpost.passed -eq $true)
            'no unexpected warnings'              = (@($cev.warnings).Count -eq 0)
        }

        # NOT A CHECK - A DISCLOSURE. The canary is DEFINED as a wall carrying
        # nothing, so the verifier correctly reports cut_coverage.probed = false
        # and says no probe was run. Every check above can be green with that
        # field false, which means a green canary says NOTHING about whether a
        # hole in the carrier reaches its layers - the one property the
        # carrier-to-layer joins exist to deliver. Recorded here so the artifact
        # can never be read as covering it.
        $cutProbed = $false
        if ($cev.cut_coverage -and (@($cev.cut_coverage.PSObject.Properties.Name) -contains 'probed')) {
            $cutProbed = [bool]$cev.cut_coverage.probed
        }
        $cutNote = if ($cev.cut_coverage -and (@($cev.cut_coverage.PSObject.Properties.Name) -contains 'note')) {
            [string]$cev.cut_coverage.note } else { '' }
        Add-WsNote $run ("canary cut coverage: probed=" + $cutProbed +
                         $(if ($cutNote) { " - " + $cutNote } else { '' }))
        if (-not $cutProbed) {
            Add-WsNote $run ("the canary proves the CONVERSION, not the CUTS. Cases 13-17 " +
                             "are what test a hole reaching every layer, and a green canary " +
                             "is not evidence for them.")
        }
        foreach ($k in $checks.Keys) { if (-not $checks[$k]) { $canaryDetail += $k } }
        $canaryOk = ($canaryDetail.Count -eq 0)

        # NOT A CHECK - A DISCLOSURE. The canary is DEFINED as a wall carrying
        # nothing, so the verifier correctly reports cut_coverage.probed = false
        # and says no probe was run. Every check above can be green with that
        # field false, which means a green canary says NOTHING about whether a
        # hole in the carrier reaches its layers - the one property the
        # carrier-to-layer joins exist to deliver. Recorded here so the artifact
        # can never be read as covering it.
        $cutProbed = $false
        if ($cev.cut_coverage -and (@($cev.cut_coverage.PSObject.Properties.Name) -contains 'probed')) {
            $cutProbed = [bool]$cev.cut_coverage.probed
        }
        $cutNote = if ($cev.cut_coverage -and (@($cev.cut_coverage.PSObject.Properties.Name) -contains 'note')) {
            [string]$cev.cut_coverage.note } else { '' }
        Add-WsNote $run ("canary cut coverage: probed=" + $cutProbed +
                         $(if ($cutNote) { " - " + $cutNote } else { '' }))
        if (-not $cutProbed) {
            Add-WsNote $run ("the canary proves the CONVERSION, not the CUTS. Cases 13-17 " +
                             "are what test a hole reaching every layer, and a green canary " +
                             "is not evidence for them.")
        }
        foreach ($k in $checks.Keys) { if (-not $checks[$k]) { $canaryDetail += $k } }
        $canaryOk = ($canaryDetail.Count -eq 0)

        Write-WsCheckpoint $cctx 'final' ("canary ok=" + $canaryOk)
        $canaryFile = Join-Path $run.ArtifactDir 'canary.json'
        ([ordered]@{
            passed = $canaryOk
            failures = $canaryDetail
            checks = $checks
            warning_texts = @($cev.warnings | ForEach-Object { [string]$_ })
            cut_coverage_probed = $cutProbed
            cut_coverage_note = $cutNote
            proves = 'the conversion: identity, cardinality, numbering, naming, offsets, provenance'
            does_not_prove = $(if ($cutProbed) { $null } else {
                'that an opening in the carrier is cut through the layer walls - this wall hosts nothing, ' +
                'so no probe ran. That is what cases 13 to 17 measure.' })
            evidence = $cev
        } | ConvertTo-Json -Depth 40) | Set-Content -LiteralPath $canaryFile -Encoding UTF8
    }
}
if ($canaryOk) {
    Write-Host "  canary PASSED - the simple path works, the matrix is worth running" -ForegroundColor Green
    if (-not $cutProbed) {
        Write-Host "  ...and it proved NOTHING about cuts: this wall hosts nothing, so no probe ran." -ForegroundColor DarkYellow
    }
} else {
    # STRICT AGAIN. The canary was allowed to continue past one named defect so
    # that cases 13-17 could measure whether the carrier's joins carried the cut
    # through. They do, the chain delivers it without joining anything across a
    # gap, and the exception is gone with the defect it existed for.
    Write-Host "  CANARY FAILED: $($canaryDetail -join ' | ')" -ForegroundColor Red
    Add-WsNote $run ("canary failed: " + ($canaryDetail -join ' | '))
}
if (-not $canaryOk -and -not $ContinueAfterCanary) {
    # STOP. Running 55 cases through a path that is already broken produces 55
    # copies of one finding and 55 more chances to leave a model half-written.
    Write-Host ""
    Write-Host "the matrix is NOT being run. Every case would go through the path the canary just" -ForegroundColor Red
    Write-Host "showed is broken, and 55 rows saying the same thing is not 55 measurements." -ForegroundColor Red
    Write-Host "Pass -ContinueAfterCanary only to characterise a failure deliberately." -ForegroundColor DarkYellow
    exit 3
}

# =====================================================================  1 - 12
Write-Host ""; Write-Host "=== 1-11  geometry, the core, the location lines ===" -ForegroundColor Cyan

$c1 = Positive 1 'straight multilayer wall, single-layer core' 'c01' `
    'cross_section Vertical, eligible, every layer with volume becomes a single-layer wall, the original stays as the core carrier'
if ($c1 -and $c1.Ok) {
    $ev = $c1.Evidence
    $verticalOk = ($ev.geometry_class -eq 'Line') -and ($ev.walls_produced -eq $ev.walls_expected) -and ($ev.originals_deleted -eq 0)
    Add-WsNote $run ("case 1 vertical evidence: geometry_class={0} produced={1}/{2} originals_deleted={3} carrier_layer={4}" -f
        $ev.geometry_class, $ev.walls_produced, $ev.walls_expected, $ev.originals_deleted, $ev.core_carrier.layer_index)
    if (-not $verticalOk) { Add-WsNote $run 'case 1: the vertical evidence does not hold even though the case passed - inspect it' }
}

$null = Positive 2 'core made of several layers' 'c02_wide_core' 'the carrier is chosen inside the core by the documented order'
$null = Positive 3 'core with no Structure-function layer' 'c03_no_structure_core' 'the thickest core layer carries, and the reason says so'
Negative 4 'wall with no valid core' 'c04_no_valid_core' 'no_valid_core' 'refused by name, never falling back to layer 0'
$null = Positive 5 'flipped wall' 'c05' 'the layers land on the sides the flip put them'

# Case 6 is ONE case over six walls: it passes only if all six do.
$sixOk = $true; $sixSeen = 0; $sixDetail = @()
foreach ($ll in 'WallCenterline', 'CoreCenterline', 'FinishFaceExterior', 'FinishFaceInterior', 'CoreExterior', 'CoreInterior') {
    $id = Id ("c06_" + $ll)
    if ($id -eq 0) { $sixOk = $false; $sixDetail += "$ll : no fixture"; continue }
    $dry = Dry @($id); $e = Pick $dry 'eligible' $id
    if (-not $e) { $sixOk = $false; $sixDetail += "$ll : not eligible"; continue }
    $ap = Apply @($id) ([string]$dry.Result.confirmation_token)
    $w = Pick $ap 'walls' $id
    $ev = Evidence $dry $ap $id
    $post = $ev.post_commit_verification
    $good = $w -and $w.applied -and ($ap.Result.all_verified -eq $true) -and $post -and ($post.passed -eq $true)
    if (-not $good) { $sixOk = $false }
    $sixSeen++
    $sixDetail += ("{0} : line={1} produced={2}/{3} ok={4}" -f $ll, $ev.location_line, $ev.walls_produced, $ev.walls_expected, [bool]$good)
}
Add-WsCase -Run $run -Number 6 -Name 'each of the six wall location lines' `
    -Expected 'every layer lands at its planned offset on all six lines' `
    -Status $(if ($sixOk -and $sixSeen -eq 6) { 'passed' } else { 'failed' }) `
    -Observed ($sixDetail -join ' | ') -Evidence @{ lines = $sixDetail }

$null = Positive 7 'arc wall, exterior' 'c07_arc' 'each layer keeps centre and angles and changes only radius'
$null = Positive 8 'arc wall, the other way' 'c08_arc_interior' 'the same, curving the other way'
$null = Positive 9 'arc wall, flipped' 'c09_arc_flipped' 'the same, with the exterior side on the other radius'
Negative 10 'stacked wall' 'c10_stacked' 'unsupported_stacked_wall' 'refused: its root hosts the doors'
$null = Positive 11 'top-constrained wall' 'c11_top_constrained' 'the layer walls keep the top constraint'
# CASE 12 IS RUN LAST, at the bottom of this file. Revit terminated inside it in
# the previous session, and a case that can end the process must not be allowed
# to cost the other 54 their answers. Its contractual number stays 12.

# =====================================================================  13 - 26
Write-Host ""; Write-Host "=== 13-26  inserts, openings, joins, refusals ===" -ForegroundColor Cyan
$null = Positive 13 'door with its own parameters' 'c13_door' 'the door keeps ElementId, UniqueId, host, sill and every parameter'
$null = Positive 14 'door with nested components' 'c14_nested' 'the subcomponent set is preserved by id AND by symbol'
$null = Positive 15 'window with sill, head and flips' 'c15_window' 'the window keeps identity, sill, head and both flips'
$null = Positive 16 'several doors and windows' 'c16_many' 'every insert keeps its identity'
$null = Positive 17 'rectangular opening' 'c17_opening' 'the opening stays on the carrier and cuts every secondary layer'
UnsupportedApi 18 'opening cut from a profile' 'the profiled opening stays and cuts every layer' `
    'no fixture: Document.Create.NewOpening(curveArray) refuses a wall host - "the hostElement is not a floor, ceiling, roof or toposolid". No typed route builds a profiled WALL opening.'
$null = Positive 19 'wall joined at both ends' 'c19_joined' 'both original joins are restored, in order, with their cut order'
# Case 20 covers BOTH the sweep and the reveal, on two walls, and passes only if
# both do - the same rule as case 6. A separate row for the reveal would make the
# denominator 56 and the roll-up would refuse it.
$twentyOk = $true; $twentyDetail = @(); $twentyEvidence = @()
foreach ($pair in @(@{ key = 'c20_sweep'; what = 'sweep' }, @{ key = 'c20_reveal'; what = 'reveal' })) {
    $id = Id $pair.key
    if ($id -eq 0) { $twentyOk = $false; $twentyDetail += ($pair.what + ': no fixture'); continue }
    $dry = Dry @($id); $e = Pick $dry 'eligible' $id
    if (-not $e) {
        $r = Pick $dry 'rejected' $id
        $twentyOk = $false
        $twentyDetail += ($pair.what + ': not eligible - ' + $(if ($r) { $r.reason_code } else { 'no entry' }))
        continue }
    $ap = Apply @($id) ([string]$dry.Result.confirmation_token)
    $w = Pick $ap 'walls' $id
    $ev = Evidence $dry $ap $id
    $post = $ev.post_commit_verification
    $good = $w -and $w.applied -and ($ap.Result.all_verified -eq $true) -and $post -and ($post.passed -eq $true)
    if (-not $good) { $twentyOk = $false }
    $twentyDetail += ("{0}: applied={1} produced={2}/{3} ok={4}" -f $pair.what, $(if ($w) { $w.applied } else { 'n/a' }),
                      $ev.walls_produced, $ev.walls_expected, [bool]$good)
    $twentyEvidence += $ev
}
Add-WsCase -Run $run -Number 20 -Name 'wall sweep and wall reveal' `
    -Expected 'each stays on the carrier with its type, profile, distance, offset and position' `
    -Status $(if ($twentyOk) { 'passed' } else { 'failed' }) `
    -Observed ($twentyDetail -join ' | ') -Evidence @{ walls = $twentyEvidence }
UnsupportedApi 21 'wall with an edited elevation profile' 'preserved, or refused before writing' `
    'no fixture: the API exposes no way to edit a wall elevation profile from a script. The refusal path (unsupported_edited_profile via Wall.SketchId) is unexercised.'
UnsupportedApi 22 'wall attached at top or base' 'preserved, or refused before writing' `
    'no fixture: there is no public API to attach a wall to a roof or floor. The refusal path (unsupported_attached_wall) is unexercised.'
Negative 23 'slanted wall' 'c23_slanted' 'unsupported_cross_section' `
    'SingleSlanted is refused and never reaches the executor'
Negative 24 'wall inside a group' 'c24_group' 'unsupported_group_member' 'refused: new walls cannot join a group definition from here'
UnsupportedApi 25 'wall in a design option' 'refused as unsupported_design_option' `
    'no fixture: the API exposes no way to create a design option and this template carries none'
BlockedEnv 26 'element owned by another user' 'refused as element_not_editable' `
    'no fixture: a second Revit user cannot be simulated in one session and the document is not workshared'

# =====================================================================  27 - 35
Write-Host ""; Write-Host "=== 27-35  rollback, idempotence, stale plan, batch ===" -ForegroundColor Cyan

# 27-29: a GENUINE failure is better evidence than an injected one. The rebar
# placed outside the future core is a real conversion that must roll back.
$outside = Id 'c47_rebar_outside'
if ($outside -eq 0) {
    BlockedFixture 27 'rollback after the layers are created' 'the wall rolls back whole' 'no fixture for a genuine mid-conversion failure'
    BlockedFixture 28 'rollback during the openings' 'the wall rolls back whole' 'no fixture'
    BlockedFixture 29 'rollback during host verification' 'the wall rolls back whole' 'no fixture'
} else {
    $dry = Dry @($outside); $e = Pick $dry 'eligible' $outside
    if (-not $e) {
        $r = Pick $dry 'rejected' $outside
        foreach ($n in 27, 28, 29) {
            Add-WsCase -Run $run -Number $n -Name 'rollback on a genuine conversion failure' `
                -Expected 'the wall rolls back whole and Revit confirms it' -Status 'failed' `
                -Observed ("the wall was refused before the executor: " + $(if ($r) { $r.reason_code } else { 'no entry' })) }
    } else {
        $ap = Apply @($outside) ([string]$dry.Result.confirmation_token)
        $w = Pick $ap 'walls' $outside
        $ev = Evidence $dry $ap $outside
        $rolled = $w -and (-not $w.applied) -and ($w.rollback_status -eq 'RolledBack') -and ($w.rollback_confirmed -eq $true) -and ($w.walls_produced -eq 0)
        Add-WsCase -Run $run -Number 27 -Name 'a wall that cannot convert rolls back WHOLE' `
            -Expected 'applied=false, rollback RolledBack and CONFIRMED, walls_produced 0' `
            -Status $(if ($rolled) { 'passed' } else { 'failed' }) `
            -Observed ("applied={0} rollback={1}/{2} produced={3} code={4}" -f $w.applied, $w.rollback_status, $w.rollback_confirmed, $w.walls_produced, $w.code) `
            -Evidence $ev
        $rightCode = $w -and ($w.code -eq 'rebar_outside_core_carrier')
        Add-WsCase -Run $run -Number 28 -Name 'reinforcement outside the core names its own code' `
            -Expected 'rebar_outside_core_carrier, naming the positions that fell out' `
            -Status $(if ($rightCode) { 'passed' } else { 'failed' }) `
            -Observed ("code=" + $(if ($w) { $w.code } else { 'absent' })) -Evidence $ev
        # after a rollback the model must be exactly as it was
        $after = Dry @($outside)
        $stillEligible = [bool](Pick $after 'eligible' $outside)
        Add-WsCase -Run $run -Number 29 -Name 'after a rollback the wall is exactly as it was' `
            -Expected 'it is eligible again, unchanged' -Status $(if ($stillEligible) { 'passed' } else { 'failed' }) `
            -Observed ("eligible again: " + $stillEligible) -Evidence @{ dry_run_artifact = $after.File }
    }
}

# 30 idempotence
$idem = Positive 30 'a second identical apply is idempotent' 'c30_idempotent' 'the second call answers already_split and opens no transaction'
if ($idem -and $idem.Ok) {
    $again = Dry @($idem.Id)
    $conv = Pick $again 'already_converted' $idem.Id
    $ok = $conv -and ([string]$conv.state -eq 'already_split') -and ($conv.transaction_opened -eq $false)
    # Replace the provisional conversion row before recording the actual
    # idempotence verdict. Removing afterwards deletes BOTH rows because they
    # deliberately share case number 30, leaving a dishonest denominator of 54.
    Remove-RecordedCase 30
    Add-WsCase -Run $run -Number 30 -Name 'a second identical apply is idempotent' `
        -Expected 'already_split, no transaction, no duplicates' -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed ("state=" + $(if ($conv) { $conv.state } else { 'not in already_converted' })) `
        -Evidence @{ already_converted = $conv; dry_run_artifact = $again.File }
}

# 31 stale plan
$staleId = Id 'c31_stale'
if ($staleId -eq 0) { BlockedFixture 31 'a stale plan is refused' 'stale_plan' 'no fixture wall for c31_stale' }
else {
    $dry = Dry @($staleId)
    $token = [string]$dry.Result.confirmation_token
    $moveArgs = @{
        target_document = $Document; units = 'mm'
        operations = @(@{ operation = 'move'; element_ids = @($staleId); vector = @(100.0, 0.0, 0.0) })
    }
    $moveDry = Invoke-Ws -Run $run -Tool 'horizun_transform_elements' -Label 'stale-move-dry' -AllowError -Arguments `
        (@{} + $moveArgs + @{ dry_run = $true })
    $moveToken = if ($moveDry.Result) { [string]$moveDry.Result.confirmation_token } else { $null }
    $moved = Invoke-Ws -Run $run -Tool 'horizun_transform_elements' -Label 'stale-move' -Mutates -AllowError -Arguments `
        (@{} + $moveArgs + @{ dry_run = $false; confirmation_token = $moveToken })
    $ap = Apply @($staleId) $token
    $w = Pick $ap 'walls' $staleId
    $refusedStale = ($ap.IsError -and ($ap.Text -match 'stale|MODEL MOVED AFTER THE DRY RUN')) -or
                    ($w -and $w.code -eq 'stale_plan')
    Add-WsCase -Run $run -Number 31 -Name 'a plan that no longer describes the wall is refused' `
        -Expected 'stale_plan, nothing written' -Status $(if ($refusedStale) { 'passed' } else { 'failed' }) `
        -Observed $(if ($w) { "code=$($w.code)" } else { Limit-WsText $ap.Text 200 }) `
        -Evidence @{ move_artifact = $moved.File; apply_artifact = $ap.File }
}

# 32 mixed batch
$valid = Id 'c32_valid'; $invalid = Id 'c04_no_valid_core'
if ($valid -eq 0 -or $invalid -eq 0) { BlockedFixture 32 'mixed batch' 'one converts, one is refused' 'no fixture pair' }
else {
    $dry = Dry @($valid, $invalid)
    $eOk = [bool](Pick $dry 'eligible' $valid)
    $rOk = [bool](Pick $dry 'rejected' $invalid)
    $ap = if ($eOk) { Apply @($valid, $invalid) ([string]$dry.Result.confirmation_token) } else { $null }
    $w = if ($ap) { Pick $ap 'walls' $valid } else { $null }
    $ok = $eOk -and $rOk -and $w -and $w.applied
    Add-WsCase -Run $run -Number 32 -Name 'batch with one valid wall and one invalid' `
        -Expected 'the valid one converts, the invalid one is refused, neither affects the other' `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed ("eligible={0} rejected={1} applied={2}" -f $eOk, $rOk, $(if ($w) { $w.applied } else { 'n/a' })) `
        -Evidence (Evidence $dry $ap $valid)
}

# 33 tags and dimensions
$null = Positive 33 'tags and dimensions on the carrier' 'c33_tag_dim' 'the tag still tags the carrier and the dimension keeps its references'

# 34/35 are properties of every conversion above, asserted over the recorded rows.
$withLayers = @($run.Probes | Where-Object {
    if ($_.status -ne 'passed' -or -not $_.evidence) { return $false }
    $rollback = Evidence-Field $_.evidence 'rollback'
    $post = Evidence-Field $_.evidence 'post_commit_verification'
    if (($rollback -and $rollback.status -eq 'RolledBack') -or -not $post -or $post.passed -ne $true) { return $false }
    $null -ne (Evidence-Field $_.evidence 'layers')
})
$idsOk = $true; $geomOk = $true; $detail = @()
foreach ($p in $withLayers) {
    foreach ($l in @((Evidence-Field $p.evidence 'layers') | Where-Object { $_.materialised })) {
        if (-not $l.naming_verified) { $idsOk = $false; $detail += "case $($p.case) layer $($l.layer_number): naming" }
        if (-not $l.single_layer_verified) { $idsOk = $false; $detail += "case $($p.case) layer $($l.layer_number): not single-layer" }
        if (-not $l.geometry_verified) { $geomOk = $false; $detail += "case $($p.case) layer $($l.layer_number): geometry $($l.deviation_mm) mm" }
    }
}
Add-WsCase -Run $run -Number 34 -Name 'insert ElementId and UniqueId preserved across every conversion' `
    -Expected 'every insert keeps its id and UniqueId, and every resulting type is single-layer and correctly named' `
    -Status $(if ($withLayers.Count -gt 0 -and $idsOk) { 'passed' } elseif ($withLayers.Count -eq 0) { 'blocked_fixture' } else { 'failed' }) `
    -Observed ("conversions examined: {0}{1}" -f $withLayers.Count, $(if ($detail) { ' | ' + ($detail -join ' | ') } else { '' })) `
    -Evidence @{ conversions = $withLayers.Count; problems = $detail }
Add-WsCase -Run $run -Number 35 -Name 'geometry of every layer measured against its planned offset' `
    -Expected 'every materialised layer within 0.5 mm of its planned offset' `
    -Status $(if ($withLayers.Count -gt 0 -and $geomOk) { 'passed' } elseif ($withLayers.Count -eq 0) { 'blocked_fixture' } else { 'failed' }) `
    -Observed ("conversions examined: {0}" -f $withLayers.Count) -Evidence @{ problems = $detail }

# =====================================================================  36 - 55
Write-Host ""; Write-Host "=== 36-55  structural ===" -ForegroundColor Cyan
$null = Positive 36 'structural wall with a WallFoundation' 'c36_foundation' 'the footing stays on the carrier, same level, displaced exactly as the carrier'
$null = Positive 37 'wall with one bar' 'c37_single_bar' 'the bar keeps identity, host, type, shape and layout, and stays inside the core'
$null = Positive 38 'wall with a distributed bar set' 'c38_distributed' 'every position preserved and inside the core carrier'
$null = Structural 39 'wall with stirrups or ties' 'c39_stirrup' 'rebar' 'the stirrup set is preserved by identity and stays inside the core'
$null = Structural 40 'wall with AreaReinforcement' 'c40_area' 'area' 'members and boundary preserved, every member still in the system'
$null = Structural 41 'wall with PathReinforcement' 'c41_path' 'path' 'members and path preserved'
$null = Structural 42 'wall with FabricArea or FabricSheet' 'c42_fabric' 'fabric' 'the fabric area and its sheets are preserved'
$null = Positive 43 'wall with a non-default cover' 'c43_cover' 'the cover is part of the wall state fingerprint and the bars stay contained'
$null = Positive 44 'foundation and rebar together' 'c44_foundation_rebar' 'both preserved, both verified'
$null = Positive 45 'door and rebar together' 'c45_door_rebar' 'the door keeps its id and the bars stay inside the core'
$null = Structural 46 'door, foundation and rebar together' 'c46_all_three' 'rebar' 'the door, the footing and the bar are all preserved by identity'
# 47 is measured in the 27-29 block, from the same wall; recorded here as its own case.
$outsideRow = @($run.Probes | Where-Object { $_.case -eq 28 })
if ($outsideRow.Count -gt 0) {
    Add-WsCase -Run $run -Number 47 -Name 'rebar deliberately outside the future core' `
        -Expected 'rebar_outside_core_carrier and a whole-wall rollback; the bars are NOT moved to fit' `
        -Status $outsideRow[0].status -Observed $outsideRow[0].observed -Evidence $outsideRow[0].evidence
} else { NotRun 47 'rebar deliberately outside the future core' 'rebar_outside_core_carrier' 'the 27-29 block did not record a result to carry here' }
BlockedFixture 48 'foundation that cannot keep its alignment' 'rollback' 'no fixture: a foundation that Revit refuses to realign cannot be constructed on demand'
BlockedFixture 49 'reinforcement system with an unreadable member' 'prior refusal with its own code' 'no fixture: an unreadable member cannot be produced on demand'
# 50: the dependency STATE changes between the two calls. The token is bound to
# that state, not to a list of ids, so moving the bar must invalidate it - if the
# plan were bound to ids alone this would sail through and split a wall whose
# reinforcement is no longer where the plan measured it.
$staleBar = Id 'c50_stale_bar'
$staleBarRebar = Fx 'c50_stale_bar' 'rebar'
if ($staleBar -eq 0 -or -not $staleBarRebar) {
    BlockedFixture 50 'a bar changed between dry run and apply' 'stale_plan' `
        ("no wall with a movable bar: " + (Why 'c50_stale_bar'))
} else {
    $ctx = Start-WsCase -Run $run -Number 50 -Name 'a bar changed between dry run and apply' `
        -WallId $staleBar -WallUniqueId ([string](Fx 'c50_stale_bar' 'unique_id')) -Operation 'stale plan via a moved bar'
    Write-WsCheckpoint $ctx 'dry_run_started' 'planning against the bar where it is now'
    $dry = Dry @($staleBar)
    Write-WsCheckpoint $ctx 'dry_run_received' ("eligible=" + [bool](Pick $dry 'eligible' $staleBar)) $dry.File
    $token = [string]$dry.Result.confirmation_token
    Write-WsCheckpoint $ctx 'apply_started' 'MOVING THE BAR FIRST - this is the mutation under test'
    # The typed transform command correctly refuses Rebar: it has no LocationPoint or
    # LocationCurve that command can re-read. This is fixture mutation, so use the explicit
    # unsafe-code route and make the script prove the centreline actually moved.
    $moved = Invoke-WsPython -Run $run -Label 'stale-bar-move' -Code @"
from Autodesk.Revit.DB import ElementId, ElementTransformUtils, Transaction, XYZ
from Autodesk.Revit.DB.Structure import Rebar, MultiplanarOption

d = __revit__.ActiveUIDocument.Document
bar = d.GetElement(ElementId($staleBarRebar))

def first_point(b):
    curves = b.GetCenterlineCurves(False, False, False, MultiplanarOption.IncludeAllMultiplanarCurves, 0)
    return curves[0].GetEndPoint(0)

before = first_point(bar)
tx = Transaction(d, 'HZ stale rebar fixture mutation')
tx.Start()
try:
    ElementTransformUtils.MoveElement(d, bar.Id, XYZ(0.0, 500.0 / 304.8, 0.0))
    d.Regenerate()
    after = first_point(bar)
    moved_mm = before.DistanceTo(after) * 304.8
    if moved_mm < 499.0:
        raise Exception('Revit reported only %.3f mm of the requested 500 mm move' % moved_mm)
    tx.Commit()
    __output__ = {'status': 'self_reported_verified', 'summary': 'bar moved for stale-plan probe',
                  'modified_ids': [bar.Id.Value], 'verification': {'checked': True,
                  'evidence': [{'before': [before.X, before.Y, before.Z],
                                'after': [after.X, after.Y, after.Z], 'moved_mm': moved_mm}]}}
except Exception as ex:
    if tx.HasStarted(): tx.RollBack()
    __output__ = {'status': 'failed', 'summary': str(ex), 'verification': {'checked': True, 'evidence': []}}
"@
    Write-WsCheckpoint $ctx 'apply_started' 'the bar has moved; replaying the old token' $moved.File
    $ap = Apply @($staleBar) $token
    Write-WsCheckpoint $ctx 'apply_received' ("error=" + $ap.IsError) $ap.File
    $w = Pick $ap 'walls' $staleBar
    $refused = ($ap.IsError -and ($ap.Text -match 'stale|MODEL MOVED AFTER THE DRY RUN')) -or
               ($w -and $w.code -eq 'stale_plan')
    $wroteNothing = (-not $w) -or (-not $w.applied)
    Write-WsCheckpoint $ctx 'final' ("refused=" + [bool]$refused)
    Add-WsCase -Run $run -Number 50 -Name 'a bar moved between the dry run and the apply' `
        -Expected 'stale_plan, and nothing written - the token is bound to dependency STATE, not to a list of ids' `
        -Status $(if ($refused -and $wroteNothing) { 'passed' } else { 'failed' }) `
        -Observed ("moved={0} refused={1} applied={2}" -f ($moved.Result.evidence_status -eq 'self_reported_verified'), [bool]$refused,
                   $(if ($w) { $w.applied } else { 'no entry' })) `
        -Evidence @{ bar_id = $staleBarRebar; move_artifact = $moved.File; dry_run_artifact = $dry.File; apply_artifact = $ap.File }
}
# 51: idempotence on the structural path specifically. Case 30 proves it for an
# architectural wall; a structural wall carries a dependency ledger the
# architectural one does not, and the provenance stamp has to survive being read
# back through it.
$c51 = Structural 51 'second apply on a structural wall' 'c51_second_apply' 'rebar' `
    'it converts once, and the second call answers already_split without opening a transaction'
if ($c51 -and $c51.Ok) {
    $again = Dry @($c51.Id)
    $conv = Pick $again 'already_converted' $c51.Id
    $ok = $conv -and ([string]$conv.state -eq 'already_split') -and ($conv.transaction_opened -eq $false)
    Remove-RecordedCase 51
    Add-WsCase -Run $run -Number 51 -Name 'second apply on a structural wall' `
        -Expected 'already_split, no transaction, the reinforcement untouched' `
        -Status $(if ($ok) { 'passed' } else { 'failed' }) `
        -Observed ("state=" + $(if ($conv) { $conv.state } else { 'not in already_converted' }) +
                   " transaction_opened=" + $(if ($conv) { $conv.transaction_opened } else { 'n/a' })) `
        -Evidence @{ already_converted = $conv; dry_run_artifact = $again.File }
}
# 52: one of the produced walls is deleted behind the tool's back. The sibling
# set no longer matches the stamp, and the honest answer is neither "already
# split" nor "never split" - it is that the set is incomplete and repairable.
$c52 = Structural 52 'a sibling deleted' 'c52_structural_partial' 'rebar' `
    'it converts, and after one produced wall is deleted the state reads as a repairable partial'
if ($c52 -and $c52.Ok) {
    $siblings = @($c52.Evidence.resulting_wall_ids | Where-Object { $_ -and ([long]$_ -ne $c52.Id) })
    if ($siblings.Count -eq 0) {
        Remove-RecordedCase 52
        Add-WsCase -Run $run -Number 52 -Name 'a sibling deleted' `
            -Expected 'repairable_partial_state' -Status 'failed' `
            -Observed 'the conversion produced no sibling to delete' -Evidence $c52.Evidence
    } else {
        $victim = [long]$siblings[0]
        $delDry = Invoke-Ws -Run $run -Tool 'horizun_delete_verified' -Label 'delete-sibling-dry' -AllowError -Arguments @{
            target_document = $Document; mode = 'ids'; ids = @($victim); dry_run = $true }
        $deleteToken = [string](Evidence-Field -Evidence $delDry.Result -Field 'confirmation_token')
        if ([string]::IsNullOrWhiteSpace($deleteToken)) {
            Remove-RecordedCase 52
            Add-WsCase -Run $run -Number 52 -Name 'a sibling deleted behind the tool' `
                -Expected 'the sibling set is reported incomplete and repairable, never as a clean already_split' `
                -Status 'failed' -Observed 'the verified-delete dry run issued no confirmation token; no delete was attempted' `
                -Evidence @{ deleted_wall = $victim; delete_dry_artifact = $delDry.File }
        } else {
            $del = Invoke-Ws -Run $run -Tool 'horizun_delete_verified' -Label 'delete-sibling' -Mutates -AllowError -Arguments @{
                target_document = $Document; mode = 'ids'; ids = @($victim); dry_run = $false
                confirmation_token = $deleteToken }
            $after = Dry @($c52.Id)
            $conv = Pick $after 'already_converted' $c52.Id
            $rej = Pick $after 'rejected' $c52.Id
            $state = if ($conv) { [string]$conv.state } elseif ($rej) { [string]$rej.reason_code } else { 'absent' }
            $ok = ($state -match 'partial')
            Remove-RecordedCase 52
            Add-WsCase -Run $run -Number 52 -Name 'a sibling deleted behind the tool' `
                -Expected 'the sibling set is reported incomplete and repairable, never as a clean already_split' `
                -Status $(if ($ok) { 'passed' } else { 'failed' }) `
                -Observed ("deleted=" + (-not $del.IsError) + " state=" + $state) `
                -Evidence @{ deleted_wall = $victim; delete_dry_artifact = $delDry.File
                             delete_artifact = $del.File; dry_run_artifact = $after.File; state = $state }
        }
    }
}
# 53: one architectural wall and one structural wall in ONE apply. Case 32 mixes
# a valid wall with an invalid one; this mixes two valid walls whose dependency
# classes are different, which is where a shared collector would leak one wall's
# ledger into the other's plan.
$archId = Id 'c32_second'
$structId = Id 'c53_structural'
if ($archId -eq 0 -or $structId -eq 0 -or -not (Fx 'c53_structural' 'rebar')) {
    BlockedFixture 53 'mixed architectural and structural batch' 'both convert independently' `
        $(if (-not (Fx 'c53_structural' 'rebar')) {
            'the structural wall exists but the template has no RebarBarType, so it carries no dependency to isolate'
          } else { 'missing a wall: c32_second=' + $archId + ' c53_structural=' + $structId })
} else {
    $ctx = Start-WsCase -Run $run -Number 53 -Name 'mixed architectural and structural batch' `
        -WallId $structId -WallUniqueId ([string](Fx 'c53_structural' 'unique_id')) -Operation 'batch of two'
    Write-WsCheckpoint $ctx 'dry_run_started' 'two walls, two dependency classes, one call'
    $dry = Dry @($archId, $structId)
    Write-WsCheckpoint $ctx 'dry_run_received' 'planned' $dry.File
    $eA = Pick $dry 'eligible' $archId
    $eS = Pick $dry 'eligible' $structId
    if (-not ($eA -and $eS)) {
        Write-WsCheckpoint $ctx 'final' 'one of the two was not eligible'
        Add-WsCase -Run $run -Number 53 -Name 'mixed architectural and structural batch' `
            -Expected 'both convert, neither ledger contaminates the other' -Status 'failed' `
            -Observed ("architectural eligible={0} structural eligible={1}" -f [bool]$eA, [bool]$eS) `
            -Evidence @{ dry_run_artifact = $dry.File }
    } else {
        Write-WsCheckpoint $ctx 'apply_started' 'committing both'
        $ap = Apply @($archId, $structId) ([string]$dry.Result.confirmation_token)
        Write-WsCheckpoint $ctx 'apply_received' ("error=" + $ap.IsError) $ap.File
        $wA = Pick $ap 'walls' $archId
        $wS = Pick $ap 'walls' $structId
        $evS = Evidence $dry $ap $structId
        $evA = Evidence $dry $ap $archId
        # The architectural wall must carry NO structural dependency, and the
        # structural one must still carry its own.
        $noLeak = (@($evS.structural_after).Count -gt 0)
        $cleanArch = (@($evA.structural_before).Count -eq 0) -and (@($evA.structural_after).Count -eq 0)
        $ok = $wA -and $wS -and $wA.applied -and $wS.applied -and ($ap.Result.all_verified -eq $true) -and $noLeak -and $cleanArch
        Write-WsCheckpoint $ctx 'final' ("ok=" + [bool]$ok)
        Add-WsCase -Run $run -Number 53 -Name 'mixed architectural and structural batch' `
            -Expected 'both convert, the structural ledger stays on the structural wall and the architectural wall gains none' `
            -Status $(if ($ok) { 'passed' } else { 'failed' }) `
            -Observed ("arch applied={0} struct applied={1} all_verified={2} struct_deps={3} arch_deps={4}" -f
                       $(if ($wA) { $wA.applied } else { 'n/a' }), $(if ($wS) { $wS.applied } else { 'n/a' }),
                       $ap.Result.all_verified, @($evS.structural_after).Count, @($evA.structural_after).Count) `
            -Evidence @{ architectural = $evA; structural = $evS }
    }
}
# 54 and 55 are PROPERTIES of everything above, measured over the rows that were
# actually recorded. They are derived, not re-run: re-converting a wall to prove a
# property of the conversions would be measuring a different wall.
$structuralRows = @($run.Probes | Where-Object {
    if (-not $_.evidence -or $_.status -ne 'passed') { return $false }
    $rollback = Evidence-Field $_.evidence 'rollback'
    $post = Evidence-Field $_.evidence 'post_commit_verification'
    if (($rollback -and $rollback.status -eq 'RolledBack') -or -not $post -or $post.passed -ne $true) { return $false }
    $structuralAfter = @(Evidence-Field $_.evidence 'structural_after' | Where-Object { $null -ne $_ })
    return $structuralAfter.Count -gt 0
})
$postProblems = @()
foreach ($r in $structuralRows) {
    $post = Evidence-Field $r.evidence 'post_commit_verification'
    if (-not $post) { $postProblems += ("case " + $r.case + ": no post-commit verification block"); continue }
    if ($post.passed -ne $true) { $postProblems += ("case " + $r.case + ": post-commit " + $post.code) }
    # The structural objects the plan saw must be the structural objects the
    # second pass found. A ledger that shrank silently is the failure this case
    # exists to catch.
    $before = @((Evidence-Field $r.evidence 'structural_before') | ForEach-Object { [string]$_.id })
    $after = @((Evidence-Field $r.evidence 'structural_after') | ForEach-Object { [string]$_.id })
    foreach ($id in $before) { if ($after -notcontains $id) { $postProblems += ("case " + $r.case + ": structural " + $id + " vanished") } }
}
Add-WsCase -Run $run -Number 54 -Name 'post-commit verification of every structural object' `
    -Expected 'the pass that runs after the outer commit agrees with the one that ran inside it, on every structural row' `
    -Status $(if ($structuralRows.Count -eq 0) { 'not_run' } elseif ($postProblems.Count -eq 0) { 'passed' } else { 'failed' }) `
    -Observed ("structural conversions examined: {0}{1}" -f $structuralRows.Count,
               $(if ($postProblems.Count) { ' | ' + ($postProblems -join ' | ') } else { '' })) `
    -Because $(if ($structuralRows.Count -eq 0) { 'no structural conversion completed, so there was nothing to verify twice' } else { $null }) `
    -Evidence @{ rows = $structuralRows.Count; problems = $postProblems }

# 55: no element may appear twice. Two ways to fail - the same dependency listed
# more than once in one ledger, and the same produced wall claimed by two cases.
$dupProblems = @()
$allProduced = @{}
$convertedRows = @($run.Probes | Where-Object {
    if (-not $_.evidence -or $_.status -ne 'passed') { return $false }
    $rollback = Evidence-Field $_.evidence 'rollback'
    $post = Evidence-Field $_.evidence 'post_commit_verification'
    if (($rollback -and $rollback.status -eq 'RolledBack') -or -not $post -or $post.passed -ne $true) { return $false }
    $produced = @(Evidence-Field $_.evidence 'resulting_wall_ids' | Where-Object { $null -ne $_ })
    return $produced.Count -gt 0
})
foreach ($r in $convertedRows) {
    $seen = @{}
    foreach ($d in @(Evidence-Field $r.evidence 'dependencies_after')) {
        $k = [string]$d.id + '/' + [string]$d.kind
        if ($seen.ContainsKey($k)) { $dupProblems += ("case " + $r.case + ": dependency " + $k + " listed twice") }
        $seen[$k] = $true
    }
    foreach ($wid in @(Evidence-Field $r.evidence 'resulting_wall_ids')) {
        $k = [string]$wid
        if ($allProduced.ContainsKey($k)) {
            $dupProblems += ("wall " + $k + " produced by case " + $allProduced[$k] + " AND case " + $r.case)
        }
        $allProduced[$k] = $r.case
    }
}
Add-WsCase -Run $run -Number 55 -Name 'zero duplicated objects' `
    -Expected 'no dependency appears twice in one ledger and no produced wall is claimed by two conversions' `
    -Status $(if ($convertedRows.Count -eq 0) { 'not_run' } elseif ($dupProblems.Count -eq 0) { 'passed' } else { 'failed' }) `
    -Observed ("conversions examined: {0}, walls produced: {1}{2}" -f $convertedRows.Count, $allProduced.Count,
               $(if ($dupProblems.Count) { ' | ' + ($dupProblems -join ' | ') } else { '' })) `
    -Because $(if ($convertedRows.Count -eq 0) { 'no conversion completed, so there was nothing to check for duplicates' } else { $null }) `
    -Evidence @{ conversions = $convertedRows.Count; produced = $allProduced.Count; problems = $dupProblems }

# =====================================================================  12, last
# THE PINNED WALL. Run in isolation, at the end, with a checkpoint around every
# stage the previous session's journal implicated. If Revit terminates here, the
# 54 answers above are already on disk and the roll-up rebuilds from them.
Write-Host ""; Write-Host "=== 12  the pinned wall, deliberately last ===" -ForegroundColor Cyan
$pinnedId = Id 'c12_pinned'
if ($pinnedId -eq 0) {
    BlockedFixture 12 'pinned wall' 'the pin is restored on the carrier and on the layers' 'no fixture wall for c12_pinned'
} else {
    $pctx = Start-WsCase -Run $run -Number 12 -Name 'pinned wall' -WallId $pinnedId `
        -WallUniqueId ([string](Fx 'c12_pinned' 'unique_id')) -Operation 'split a PINNED wall'
    Write-WsCheckpoint $pctx 'health_before' 'about to ask health before touching the pinned wall'
    $h2 = Invoke-Ws -Run $run -Tool 'horizun_health' -Label 'pinned-health-before' -AllowError -Arguments @{}
    Write-WsCheckpoint $pctx 'health_before' ("healthy=" + $(if ($h2.Result) { $h2.Result.status } else { 'no answer' })) $h2.File

    Write-WsCheckpoint $pctx 'dry_run_started' 'reading Pinned'
    $dry = Dry @($pinnedId)
    Write-WsCheckpoint $pctx 'dry_run_received' ("eligible=" + [bool](Pick $dry 'eligible' $pinnedId)) $dry.File
    $e = Pick $dry 'eligible' $pinnedId
    if (-not $e) {
        $r = Pick $dry 'rejected' $pinnedId
        Write-WsCheckpoint $pctx 'final' 'not eligible'
        Add-WsCase -Run $run -Number 12 -Name 'pinned wall' `
            -Expected 'the pin is released, the wall converts, and the pin is restored on the carrier and every layer' `
            -Status 'failed' -Observed ("not eligible: " + $(if ($r) { $r.reason_code } else { 'absent' })) `
            -Evidence (Evidence $dry $null $pinnedId)
    } else {
        $pctx.LastRegenerate = 'none yet - the executor has not been entered'
        Write-WsCheckpoint $pctx 'apply_started' 'ENTERING THE EXECUTOR: unpin, ChangeTypeId, regenerate, place, join, repin'
        $ap = Apply @($pinnedId) ([string]$dry.Result.confirmation_token)
        Write-WsCheckpoint $pctx 'apply_received' ("error=" + $ap.IsError + " parsed=" + $ap.Parsed) $ap.File
        $w = Pick $ap 'walls' $pinnedId
        Write-WsCheckpoint $pctx 'verification_started' 'reading the reply back'
        $ev = Evidence $dry $ap $pinnedId
        $post = $ev.post_commit_verification
        Write-WsCheckpoint $pctx 'verification_received' ("applied=" + $(if ($w) { $w.applied } else { 'no entry' }))
        $ok = $w -and $w.applied -and ($ap.Result.all_verified -eq $true) -and $post -and ($post.passed -eq $true)
        $h3 = Invoke-Ws -Run $run -Tool 'horizun_health' -Label 'pinned-health-after' -AllowError -Arguments @{}
        Write-WsCheckpoint $pctx 'health_after' ("status=" + $(if ($h3.Result) { $h3.Result.status } else { 'NO ANSWER - Revit may be gone' })) $h3.File
        Write-WsCheckpoint $pctx 'final' ("ok=" + [bool]$ok)
        Add-WsCase -Run $run -Number 12 -Name 'pinned wall' `
            -Expected 'the pin is released, the wall converts, and the pin is restored on the carrier and every layer' `
            -Status $(if ($ok) { 'passed' } elseif (-not $w) { 'unverified' } else { 'failed' }) `
            -Observed ("applied={0} produced={1}/{2} all_verified={3} health_after={4}" -f
                       $(if ($w) { $w.applied } else { 'no entry' }), $ev.walls_produced, $ev.walls_expected,
                       $ap.Result.all_verified, $(if ($h3.Result) { $h3.Result.status } else { 'no answer' })) `
            -Because $(if (-not $w) { 'the apply returned no entry for this wall, so the case has no answer either way' } else { $null }) `
            -Evidence $ev
    }
}

# ------------------------------------------------------------------ roll-up
$summary = Save-WsRun -Run $run -Identity $identity

$total = $summary.passed + $summary.failed + $summary.unverified + $summary.not_run +
         $summary.blocked_fixture + $summary.blocked_environment + $summary.unsupported_api
Write-Host ""
Write-Host ("roll-up: passed {0} + failed {1} + unverified {2} + not_run {3} + blocked_fixture {4} + blocked_env {5} + unsupported_api {6} = {7}" -f
    $summary.passed, $summary.failed, $summary.unverified, $summary.not_run,
    $summary.blocked_fixture, $summary.blocked_environment, $summary.unsupported_api, $total) -ForegroundColor Cyan
if ($total -ne 55) {
    Write-Host ("THE BUCKETS DO NOT ADD TO 55 - they add to " + $total +
                ". A matrix whose denominator disagrees with the mandate is not a matrix.") -ForegroundColor Red
    exit 2
}
if ($summary.passed -eq 55) {
    Write-Host "every case passed." -ForegroundColor Green
} else {
    Write-Host "NOT 55/55. The buckets above are the answer; nothing here rounds them up." -ForegroundColor Yellow
}
if ($summary.failed -gt 0 -or $summary.unverified -gt 0 -or $summary.not_run -gt 0) { exit 1 }
