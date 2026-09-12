#Requires -Version 5.1
# Focused, typed-only verification of the optimization work. It never enables
# Python and does not claim to be the full release gate. The lifecycle runner
# opens an explicitly disposable fixture and owns closing its Revit process.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$Year,
      [Parameter(Mandatory=$true)][string]$Document,
      [Parameter(Mandatory=$true)][string]$Server,
      [Parameter(Mandatory=$true)][string]$ExpectedCommit,
      [switch]$LocalBuild,
      [string]$ExpectedServerSha256,
      [string]$ExpectedAddinSha256,
      [Parameter(Mandatory=$true)][string]$Json,
      [Parameter(Mandatory=$true)][ValidateSet('yes-this-model-is-disposable')][string]$Disposable,
      [string]$Category='OST_DuctCurves')
$ErrorActionPreference='Stop'
$runId=[guid]::NewGuid().ToString('N')
$runRoot=Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($Json))) "calls-$Year-$runId"
$null=New-Item -ItemType Directory -Path $runRoot -Force
. (Join-Path $PSScriptRoot 'live-family.probes.ps1')
. (Join-Path $PSScriptRoot 'live-optimization.probes.ps1')
function Call([string]$tool,$arguments) {
    $path=Join-Path $runRoot ([guid]::NewGuid().ToString('N')+'.json')
    $argPath="$path.arguments.json"
    $arguments|ConvertTo-Json -Depth 35 -Compress|Set-Content -LiteralPath $argPath -Encoding UTF8
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'hz-call.ps1') -Tool $tool -ArgumentsPath $argPath -Server $Server -Json $path -Quiet
    $code=$LASTEXITCODE
    if(-not(Test-Path -LiteralPath $path)) { return @{isError=$true;text="transport exit $code"} }
    $r=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    return @{isError=($code -ne 0 -or $r.is_error);data=$r.result;text=$r.raw;structured=$r.result}
}
function Apply([string]$tool,$arguments,[string]$key) {
    $dryArgs=$arguments.Clone();$dryArgs.dry_run=$true
    $dry=Call $tool $dryArgs
    if($dry.isError -or -not $dry.data.confirmation_token) {return @{stage='dry_run';answer=$dry}}
    $applyArgs=$arguments.Clone();$applyArgs.dry_run=$false;$applyArgs.confirmation_token=$dry.data.confirmation_token
    $applyArgs.idempotency_key="optimization-$runId-$key"
    return @{stage='apply';answer=(Call $tool $applyArgs);dry=$dry}
}
$results=@()
function Record([string]$name,$result) {
    $script:results+=@{name=$name;outcome=$result.outcome;detail=$result.detail}
    Write-Host "$($result.outcome): $name — $($result.detail)"
}
try {
    $serverHash=(Get-FileHash -LiteralPath $Server -Algorithm SHA256).Hash
    $addinPath=Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\Horizun\Horizun.Revit.dll"
    $addinHash=(Get-FileHash -LiteralPath $addinPath -Algorithm SHA256).Hash
    if(-not $ExpectedServerSha256 -or -not $ExpectedAddinSha256 -or
        $serverHash -ne $ExpectedServerSha256 -or $addinHash -ne $ExpectedAddinSha256) {
        throw 'Installed binaries do not match the supplied local build manifest.'
    }
    $health=Call 'horizun_health' @{}
    if($health.isError -or $health.data.status -ne 'healthy' -or $health.data.revit_version -ne "$Year" -or
        $health.data.horizun_commit -ne ($ExpectedCommit + $(if($LocalBuild){'-dirty'}else{''})) -or
        $health.data.built_from_clean_tree -ne (-not $LocalBuild) -or
        @($health.data.open_documents|Where-Object {$_.is_active -and $_.title -eq $Document}).Count -ne 1) {
        throw 'Health did not confirm this build, year and disposable active document.'
    }
    Record 'health and source provenance' @{outcome='pass';detail="commit $ExpectedCommit; Revit $Year"}
    Record 'query response equivalence and size' (Invoke-HorizunQueryEfficiencyProbe -Category $Category -Call {param($t,$a) Call $t $a})

    $templateRoot=Join-Path $env:ProgramData "Autodesk\RVT $Year\Family Templates"
    $template=Get-ChildItem -LiteralPath $templateRoot -Recurse -Filter '*.rft' -File |
        Where-Object {$_.BaseName -match '^(Metric Generic Model|Generic Model|Modelo gen.rico m.trico)$'} |
        Sort-Object FullName|Select-Object -First 1
    Record 'native RFA creation, version and type values' (Invoke-HorizunFamilyProbe -Document $Document -Template $template.FullName `
        -Year $Year -OutputDirectory $runRoot -RunId $runId -Apply {param($t,$a,$k) Apply $t $a $k} -Call {param($t,$a) Call $t $a})

    $level=Apply 'horizun_create_elements' @{target_document=$Document;units='mm';elements=@(@{kind='level';name="HZ_OPT_$runId";elevation=45678})} 'level'
    if($level.stage -ne 'apply' -or $level.answer.isError -or $level.answer.data.created_verified -ne 1) {throw 'Disposable test level was not created and verified.'}
    $levelId=$level.answer.data.rows[0].element_id
    Record 'named pin workflow and confirmation preview' (Invoke-HorizunWorkflowPreviewProbe -Document $Document -ElementId $levelId `
        -Apply {param($t,$a,$k) Apply $t $a $k} -Call {param($t,$a) Call $t $a})
    $views=Call 'horizun_query_model' @{categories=@('OST_Views');include_links=$false;max_rows=500;return_fields=@('name','is_view_template','view_type')}
    if($views.isError) {throw 'Template discovery failed.'}
    $templateView=$views.data.rows|Where-Object {$_.is_view_template -eq $true -and $_.view_type -eq 'FloorPlan'}|Select-Object -First 1
    if(-not $templateView) {throw 'The disposable fixture needs a floor-plan view template for workflow verification.'}
    $view=Apply 'horizun_manage_views' @{target_document=$Document;actions=@(@{operation='create_floor_plan';level_id=$levelId;name="HZ_OPT_VIEW_$runId"})} 'view'
    if($view.stage -ne 'apply' -or $view.answer.isError) {throw 'Test view creation failed.'}
    $viewId=$view.answer.data.rows[0].element_id
    $applyTemplate=Apply 'horizun_execute_plan' @{target_document=$Document;workflow=@{
        name='apply_view_template';view_ids=@($viewId);template_view_id=$templateView.element_id}} 'template'
    if($applyTemplate.stage -ne 'apply' -or $applyTemplate.answer.isError) {throw "View-template workflow failed: $($applyTemplate.answer.text)"}
    $viewRead=Call 'horizun_query_model' @{categories=@('OST_Views');include_links=$false;name="HZ_OPT_VIEW_$runId";return_fields=@('view_template_id')}
    if($viewRead.isError -or $viewRead.data.rows.Count -ne 1 -or $viewRead.data.rows[0].view_template_id -ne $templateView.element_id) {throw 'Template ID was not independently re-read.'}
    Record 'view-template workflow' @{outcome='pass';detail='assigned template independently re-read on the created floor plan'}
    $blocks=Call 'horizun_query_model' @{categories=@('OST_TitleBlocks');include_links=$false;include_types=$true;max_rows=100;return_fields=@('is_element_type')}
    $block=$blocks.data.rows|Where-Object {$_.is_element_type -eq $true}|Select-Object -First 1
    if($blocks.isError -or -not $block) {throw 'The disposable fixture needs a title-block type for sheet workflow verification.'}
    $sheetNumber="OPT_$($runId.Substring(0,8))"
    $sheet=Apply 'horizun_execute_plan' @{target_document=$Document;workflow=@{
        name='prepare_sheet_set';sheets=@(@{view_id=$viewId;number=$sheetNumber;name='Optimization proof';title_block_type_id=$block.element_id;point=@(200,150)})}} 'sheet'
    if($sheet.stage -ne 'apply' -or $sheet.answer.isError) {throw "Sheet-set workflow failed: $($sheet.answer.text)"}
    $sheetRead=Call 'horizun_query_model' @{categories=@('OST_Sheets');include_links=$false;parameters=@(@{name='SHEET_NUMBER';operator='equals';value=$sheetNumber});max_rows=5}
    if($sheetRead.isError -or $sheetRead.data.matched_total -ne 1) {throw 'Created sheet number was not independently re-read.'}
    Record 'sheet-set workflow' @{outcome='pass';detail='sheet and viewport committed; unique sheet number independently re-read'}
    Record 'async deduplication, durable result and replay refusal' (Invoke-HorizunAsyncRecoveryProbe -RunId $runId -Category $Category -Call {param($t,$a) Call $t $a})
} catch {Record 'suite execution' @{outcome='fail';detail=$_.Exception.Message}}
finally {
    $report=@{schema=1;suite='optimization-typed';generated_utc=[DateTime]::UtcNow.ToString('o');revit_year=$Year
        expected_commit=$ExpectedCommit;local_build=[bool]$LocalBuild;server_sha256=$serverHash;addin_sha256=$addinHash
        python_enabled_by_suite=$false;probes=$results
        complete=($results.Count -ge 7 -and @($results|Where-Object {$_.outcome -ne 'pass'}).Count -eq 0)}
    $report|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $Json -Encoding UTF8
}
if(-not $report.complete){exit 1}
