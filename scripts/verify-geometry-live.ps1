#Requires -Version 5.1
<#
  Geometry/contract acceptance against an explicitly selected disposable RVT.
  No open, close, install, save or apply operations are issued. Geometry cases
  use dry_run + revit_rollback, with real inner commits and checked rollback.
  The save_as regression uses an existing sentinel destination and verifies its
  SHA-256 and the active document path independently of the tool's declaration.
  Requires a matching server/add-in build already installed and Revit open.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Fixture,
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$fixturePath = (Resolve-Path -LiteralPath $Fixture).Path
$config = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
if ($config.disposable_fixture -ne $true) { throw 'Fixture must explicitly declare disposable_fixture=true.' }
if (-not $config.document_path -or -not [IO.Path]::IsPathRooted($config.document_path)) { throw 'An absolute document_path is required.' }
if (-not (Test-Path -LiteralPath $config.document_path -PathType Leaf)) { throw 'The disposable fixture RVT must exist on disk.' }
if ($config.expected_revit_year -notin 2023..2027) { throw 'expected_revit_year must be 2023..2027.' }
if ($config.expected_contract_hash -notmatch '^[0-9a-f]{24}$') { throw 'Set expected_contract_hash from the build under test.' }
if ($config.expected_server_sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Set expected_server_sha256 from the server under test.' }
if (-not $config.expected_addin_commit) { throw 'expected_addin_commit is required.' }
if ((Get-FileHash -LiteralPath $Server -Algorithm SHA256).Hash -ne $config.expected_server_sha256) { throw 'Server SHA-256 differs from the fixture manifest.' }
if (@($config.cases).Count -lt 1) { throw 'Provide at least one explicit case.' }
foreach ($case in $config.cases) {
    if (-not $case.name -or $case.tool -notin @('horizun_create_elements', 'horizun_manage_system_types', 'horizun_capture_view')) {
        throw 'Cases require a name and an allowed typed rehearsal/capture tool.'
    }
    if (-not $case.arguments) { throw "Missing arguments for $($case.name)." }
    foreach ($field in @('dry_run', 'validation_mode', 'idempotency_key', 'confirmation_token', 'target_document')) {
        if ($case.arguments.PSObject.Properties.Name -contains $field) { throw "Case $($case.name): $field is controlled by the harness." }
    }
}

$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidenceRoot) { throw 'EvidenceDirectory must be new, to avoid overwriting earlier evidence.' }
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
$steps = New-Object System.Collections.Generic.List[object]
$call = Join-Path $PSScriptRoot 'hz-call.ps1'
function Get-OpenModelHash([string]$path) {
    # Revit holds its RVT open for writing. Share that handle without acquiring
    # write access ourselves; an exclusive hash reader fails on a live fixture.
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
function Invoke-Probe([string]$name, [string]$tool, $arguments) {
    $file = Join-Path $evidenceRoot ('{0:D3}.json' -f $steps.Count)
    & $call -Tool $tool -Arguments ($arguments | ConvertTo-Json -Depth 40 -Compress) -Server $Server -Json $file -Quiet | Out-Null
    $reply = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $steps.Add([pscustomobject]@{ name = $name; evidence = $file; replied = $reply.replied; is_error = $reply.is_error })
    if ($reply.replied -ne $true -or $reply.is_error -ne $false -or $null -eq $reply.result) { throw "Probe '$name' failed; inspect $file." }
    return $reply.result
}
function Assert-Target($health) {
    if ($health.status -ne 'healthy' -or $health.revit_version -ne [string]$config.expected_revit_year -or
        $health.contract_hash -ne $config.expected_contract_hash -or $health.horizun_commit -ne $config.expected_addin_commit -or
        $health.active_document.path -ne $config.document_path -or $health.active_document.is_workshared -ne $false -or
        $health.active_document.is_family_document -ne $false) {
        throw 'Health did not identify the exact expected build and active standalone project fixture.'
    }
}

$passed = $false
$failure = $null
try {
    $initialHealth = Invoke-Probe 'health before' 'horizun_health' @{}
    Assert-Target $initialHealth
    $fixtureHash = Get-OpenModelHash $config.document_path
    $sentinel = Join-Path $evidenceRoot 'dry-run-save-as-sentinel.rvt'
    [IO.File]::WriteAllText($sentinel, 'Horizun dry-run sentinel; intentionally not a Revit model.')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    $saved = Invoke-Probe 'save_as dry_run preserves destination' 'horizun_document_session' @{
        operation = 'save_as'; target_document = $config.document_path
        save_as_path = $sentinel; overwrite = $true; dry_run = $true
    }
    if ($saved.dry_run -ne $true -or $saved.saved -ne $false -or $saved.changes_applied -ne $false -or
        (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash) {
        throw 'save_as dry_run changed the sentinel or did not explicitly certify no write.'
    }
    Assert-Target (Invoke-Probe 'health after save_as dry_run' 'horizun_health' @{})
    foreach ($case in $config.cases) {
        Assert-Target (Invoke-Probe ('target before ' + $case.name) 'horizun_health' @{})
        $arguments = @{}
        foreach ($property in $case.arguments.PSObject.Properties) { $arguments[$property.Name] = $property.Value }
        $arguments.target_document = $config.document_path
        if ($case.tool -ne 'horizun_capture_view') {
            $arguments.dry_run = $true
            $arguments.validation_mode = 'revit_rollback'
        }
        $result = Invoke-Probe $case.name $case.tool $arguments
        if ($case.tool -eq 'horizun_capture_view') {
            if ($result.view_restored -ne $true -or $result.rollback_status -ne 'RolledBack') { throw "View restoration failed: $($case.name)." }
            if ($case.arguments.calibrate_world_to_pixel -eq $true) {
                $mapping = $result.spatial.world_to_pixel
                if ($result.spatial.world_to_pixel_verified -ne $true -or $null -eq $mapping -or
                    $mapping.fit_anchor_count -ne 3 -or @($mapping.independent_checks).Count -lt 3 -or
                    $mapping.tolerance_pixels -gt 1.5 -or $mapping.max_error_pixels -gt $mapping.tolerance_pixels) {
                    throw "Independent pixel calibration was not proven: $($case.name)."
                }
                if ((Get-FileHash -LiteralPath $result.image_path).Hash -ne $mapping.clean_image_sha256 -or
                    (Get-FileHash -LiteralPath $mapping.calibration_image_path).Hash -ne $mapping.calibration_image_sha256) {
                    throw 'Calibration evidence PNG hashes do not match.'
                }
            }
        }
        else {
            if ($result.validation_level -ne 'revit_construction_rolled_back' -or $result.invalid -ne 0 -or
                $result.api_rehearsal.changes_applied -ne $false -or $result.api_rehearsal.provisional_elements_absent -ne $true) {
                throw "Construction/verification/rollback was not proven: $($case.name)."
            }
        }
    }
    Assert-Target (Invoke-Probe 'health after all cases' 'horizun_health' @{})
    if ((Get-OpenModelHash $config.document_path) -ne $fixtureHash) { throw 'The fixture file changed on disk.' }
    $passed = $true
}
catch { $failure = $_.Exception.Message }
finally {
    [pscustomobject]@{
        schema = 1; passed = $passed; failure = $failure; fixture = $fixturePath
        fixture_sha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
        finished_utc = [DateTime]::UtcNow.ToString('o'); steps = @($steps.ToArray())
        scope = 'Real construction and inner-commit readback followed by rollback; no persistent apply or reopened-file fidelity assertion.'
    } | ConvertTo-Json -Depth 40 | Out-File -LiteralPath (Join-Path $evidenceRoot 'summary.json') -Encoding utf8
}
if (-not $passed) { throw $failure }
Write-Output "Geometry rehearsals passed. Evidence: $evidenceRoot"
