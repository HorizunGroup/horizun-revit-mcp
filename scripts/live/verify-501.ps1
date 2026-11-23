#Requires -Version 7
[CmdletBinding()]
param([string]$Year = $env:HORIZUN_REVIT_YEAR, [Parameter(Mandatory)][string]$ArtifactDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
. (Join-Path $PSScriptRoot 'horizun-fixture.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'verify-501' -Document 'pending'
$documentPath = Join-Path $run.WorkDir 'FX30D1.rvt'
function Write-Documents {
    if (-not $env:HORIZUN_HARNESS_DOCUMENTS_MANIFEST) { return }
    $docs = @()
    if (Test-Path -LiteralPath $documentPath) { $docs += @{ path = $documentPath; created_by_harness = $true } }
    @{ schema = 'horizun.harness-documents/v1'; harness = $PSCommandPath; probe_run = ([IO.Path]::GetFileName($run.WorkDir).Substring('horizun-live-'.Length)); scratch_root = $run.WorkDir; documents = $docs } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:HORIZUN_HARNESS_DOCUMENTS_MANIFEST -Encoding utf8
}
function Probe($id, $ok, $expected, $evidence) {
    Add-HzProbe -Run $run -Id $id -Name $id -Expected $expected -Ok $ok -Evidence $evidence
}
function Types($category) {
    $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label "types-$category" -Arguments @{
        target_document = $run.Document; categories = @($category); include_types = $true; include_links = $false; max_rows = 500
    }
    @($q.Result.rows | Where-Object { $_.is_element_type })
}
function Profile-Probe($id, $points, $level, $type) {
    try {
        $w = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label $id -Arguments @{
            target_document = $run.Document; units = 'mm'; elements = @(, @{ kind = 'wall_profile'; level_id = $level; type_id = $type; profile = @(, $points) })
        }
        $result = $w.Apply.Result
        $row = @($result.rows)[0]
        # These are the side-face vertices read by the typed command after its outer commit.
        # Project to the profile plane: the face itself is offset by half the wall width.
        $vertices = @(Get-HzProp $row 'profile_side_face_vertices_feet')
        $maxDiff = 0.0
        if ($vertices.Count -ne $points.Count) { throw "Expected $($points.Count) vertices, read $($vertices.Count)" }
        foreach ($p in $points) {
            $closest = [double]::PositiveInfinity
            foreach ($v in $vertices) {
                $dx = 304.8 * [double]$v[0] - [double]$p[0]
                $dz = 304.8 * [double]$v[2] - [double]$p[2]
                $distance = [math]::Sqrt($dx * $dx + $dz * $dz)
                $closest = [math]::Min($closest, $distance)
            }
            $maxDiff = [math]::Max($maxDiff, $closest)
        }
        $outcome = Get-HzProp $result 'application'
        $outcomeState = [string](Get-HzProp $outcome 'state')
        $ok = [int]$result.created_verified -eq 1 -and $row.verified -eq $true -and $outcomeState -eq 'verified_applied' -and $maxDiff -le 1.0
        Probe $id $ok 'verified_applied; every post-commit vertex within 1 mm' @{ max_difference_mm = $maxDiff; vertices_feet = $vertices; outcome = $outcome; result = $result }
    } catch { Probe $id $false 'profile created and vertices reread' @{ error = $_.Exception.Message } }
}
try {
    if (-not $env:HORIZUN_SERVER_EXE -or -not (Test-Path -LiteralPath $env:HORIZUN_SERVER_EXE)) { throw 'HORIZUN_SERVER_EXE missing; run through the matrix driver' }
    if ($Year -notmatch '^202[3-7]$') { throw 'A Revit year 2023–2027 is required' }
    Write-Documents
    $health = Get-HzHealth $run
    if ([string](Get-HzProp $health 'revit_version') -ne $Year) { throw ('health did not report Revit ' + $Year + ' (a startup dialog, or another year): ' + [string](Get-HzProp $health 'estado')) }
    $project = Invoke-HzWrite -Run $run -Tool 'horizun_document_session' -Label 'new-project' -Arguments @{ operation = 'new_project'; save_as_path = $documentPath }
    Write-Documents
    if ((Get-HzProp $project.Apply.Result 'created') -ne $true -or (Get-HzProp $project.Apply.Result 'activated') -ne $true) { throw 'Project creation or activation was not verified' }
    $run.Document = [string]$project.Apply.Result.title
    $level = [long](Get-HzFirstLevel $run).element_id
    $wallTypes = @(Types 'OST_Walls' | Where-Object { [string]$_.family -match 'Basic Wall|Muro básico|Muro basico' })
    if ($wallTypes.Count -eq 0) { throw 'No basic wall type found in the new template' }
    $wallType = [long]$wallTypes[0].element_id
    try {
        # Two wall types the fresh template carries and nothing uses: the new project holds no walls yet.
        $ids = @(@(Types 'OST_Walls' | Where-Object { [string]$_.family -match 'Basic Wall|Muro básico|Muro basico' -and [long]$_.element_id -ne $wallType }) |
            Select-Object -Last 2 | ForEach-Object { [long]$_.element_id })
        if ($ids.Count -ne 2) { throw 'The new template does not carry two spare basic wall types' }
        $protectedTypes = @(@(Types 'OST_Walls') + @(Types 'OST_Floors') | Where-Object { [long]$_.element_id -notin $ids } | ForEach-Object { [long]$_.element_id })
        $purgeArgs = @{ target_document = $run.Document; mode = 'purge_unused'; max_passes = 1; id_cap = 10000; protect_ids = $protectedTypes }
        $dry = Invoke-HzToolStrict -Run $run -Tool 'horizun_delete_verified' -Label 'purge-dry' -Arguments (Copy-HzArgs $purgeArgs @{ dry_run = $true })
        $d = $dry.Result
        $candidates = @((Get-HzPath $d 'results','items') | ForEach-Object { [long]$_.id })
        $source = if ([int]$Year -le 2024) { 'performance_adviser' } else { 'document_get_unused_elements' }
        $found = @($ids | Where-Object { $_ -in $candidates }).Count -eq 2
        $token = Get-HzProp $d 'confirmation_token'
        $supported = (Get-HzProp $d 'purge_supported') -eq $true
        $sourceOk = [string](Get-HzProp $d 'purge_source') -eq $source
        $note = Get-HzProp $d 'coverage_note'
        if (-not $supported -or -not $sourceOk -or -not $found -or -not $token) { throw "Purge preview did not establish support, source, both candidates and a token: $($d | ConvertTo-Json -Depth 20 -Compress)" }
        $apply = Invoke-HzToolStrict -Run $run -Tool 'horizun_delete_verified' -Label 'purge-apply' -Arguments (Copy-HzArgs $purgeArgs @{ dry_run = $false; confirmation_token = $token; idempotency_key = (New-HzKey $run 'purge') })
        $remaining = @(Types 'OST_Walls' | Where-Object { [long]$_.element_id -in $ids })
        Probe 'purge' ($remaining.Count -eq 0) 'both spare types offered and absent after one confirmed pass' @{ purge_supported = $supported; purge_source = $source; coverage_note = $note; candidate_ids = $ids; both_candidates = $found; remaining = $remaining; apply = $apply.Result }
    } catch { Probe 'purge' $false 'both spare types offered and absent after one confirmed pass' @{ error = $_.Exception.Message } }
    $triangle = @(@(0.0,0.0,0.0), @(6000.0,0.0,0.0), @(3000.0,0.0,3000.0))
    $pentagon = @(@(0.0,10000.0,0.0), @(6000.0,10000.0,0.0), @(6000.0,10000.0,2500.0), @(3000.0,10000.0,4000.0), @(0.0,10000.0,2500.0))
    Profile-Probe 'triangle' $triangle $level $wallType
    Profile-Probe 'pentagon' $pentagon $level $wallType
    try {
        $fixture = Invoke-HzWrite -Run $run -Tool 'horizun_create_elements' -Label 'attach-fixture' -Arguments @{
            target_document = $run.Document; units = 'mm'; elements = @(
                @{ kind = 'wall'; level_id = $level; start = @(20000,20000,0); end = @(26000,20000,0); height = 2500 },
                @{ kind = 'floor'; level_id = $level; offset = 3000; profile = @(, @(@(19000,19000,3000), @(27000,19000,3000), @(27000,21000,3000), @(19000,21000,3000))) })
        }
        $rows = @($fixture.Apply.Result.rows)
        if ($rows.Count -ne 2 -or [int]$fixture.Apply.Result.created_verified -ne 2) { throw 'Wall and floor fixture not verified' }
        $attach = Invoke-HzTool -Run $run -Tool 'horizun_transform_elements' -Label 'attach-dry' -Arguments @{
            target_document = $run.Document; units = 'mm'; dry_run = $true; operations = @(, @{
                operation = 'wall_attach_top'; element_ids = @([long]$rows[0].element_id); attachment_target_id = [long]$rows[1].element_id; expected_end_elevations = @(3000,3000)
            })
        }
        $data = $attach.Result
        $text = $attach.Raw | ConvertTo-Json -Depth 40 -Compress
        if ([int]$Year -le 2024) {
            $fallback = Get-HzProp $data 'fallback'
            $ok = (Get-HzProp $fallback 'allowed') -eq $true -and (Get-HzProp $fallback 'write_started') -eq $false -and $text -match 'wall_profile'
        } else {
            $plan = @(Get-HzProp $data 'plan')
            $predicted = if ($plan.Count) { @(Get-HzProp $plan[0] 'predicted_end_elevations_feet') } else { @() }
            $ok = $attach.Ok -and $predicted.Count -gt 0 -and @(Get-HzProp $predicted[0] 'z').Count -eq 2 -and $null -ne $predicted[0].z[0] -and $null -ne $predicted[0].z[1]
        }
        Probe 'attach-preview' $ok '2023/24 fallback without write; 2025+ predicted end elevations; never apply' @{ reply = $attach.Raw }
    } catch { Probe 'attach-preview' $false 'attachment preview' @{ error = $_.Exception.Message } }
} catch {
    foreach ($id in @('purge','triangle','pentagon','attach-preview')) {
        if (@($run.Probes | Where-Object { $_.id -eq $id }).Count -eq 0) { Probe $id $false 'fixture available and test completed' @{ error = $_.Exception.Message } }
    }
} finally {
    Write-Documents
}
$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode


