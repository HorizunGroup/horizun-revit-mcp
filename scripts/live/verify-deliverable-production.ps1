#Requires -Version 7.0
<# New delivery paths over an explicitly disposable active fixture. No saves.
   Records actual MCP requests/replies; visual approval is never inferred.
   Run after the general regression, never concurrently with model mutations. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][ValidateSet('yes-this-model-is-disposable')][string]$Disposable,
    [Parameter(Mandatory)][string]$ExpectedAddinSha256,
    [Parameter(Mandatory)][string]$ArtifactDir
)
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'deliverable-production' -Document $Document -WorkDir (Join-Path $ArtifactDir ('calls-' + [guid]::NewGuid().ToString('N')))
$expectedCases=@('native-readable-tag','paper-dimensions-50','paper-dimensions-100','paper-dimensions-200','duplicate-dimension-role-refuses',
    'overflow-refuses-whole-set','overflow-leaves-no-placement','multi-sheet-atomic-apply',
    'pdf-combined-counts-and-hashes','pdf-combined-overwrite-refusal','pdf-separate-counts-and-hashes',
    'pdf-separate-overwrite-refusal','capture-first','capture-second','visual-acceptance')
function Record([string]$Id,[bool]$Ok,[string]$Expected,$Evidence) {
    Add-HzProbe -Run $run -Id $Id -Name $Id -Expected $Expected -Ok $Ok -Evidence $Evidence
}
function ReadTool([string]$Tool,[hashtable]$Arguments,[string]$Label) {
    Invoke-HzToolStrict -Run $run -Tool $Tool -Arguments $Arguments -Label $Label
}
function WriteTool([string]$Tool,[hashtable]$Arguments,[string]$Label) {
    $Arguments['target_document']=$Document
    Invoke-HzWrite -Run $run -Tool $Tool -Arguments $Arguments -Label $Label
}
try {
    $h=Get-HzHealth $run
    if($h.status -ne 'healthy' -or $h.active_document.title -ne $Document -or $h.addin_assembly.sha256 -ne $ExpectedAddinSha256) {
        throw 'Candidate/document gate refused. Nothing was authored.'
    }
    # Use a real loaded readable tag family. An empty multi-category RFT is
    # not a substitute for a production symbol with visible label geometry.
    try {
        $existing=ReadTool 'horizun_query_planimetry' @{mode='annotations';categories=@('tags');max_rows=100;units='mm'} 'native-tag-fixture'
        $tag=@($existing.Result.rows | Where-Object { $_.bounds_readable -and -not $_.orphaned -and $_.target_count -eq 1 -and -not $_.targets_linked } | Select-Object -First 1)
        if($tag.Count -ne 1){throw 'Fixture missing: no readable native tag with one host target.'}
        $tag=$tag[0]
        $null=ReadTool 'horizun_navigate' @{operation='open_view';view_id=[long]$tag.owner_view_id} 'activate-tag-view'
        $tagPlan=ReadTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=[long]$tag.owner_view_id;tag_type_id=[long]$tag.type_id;tag_mode='by_category';element_ids=@([long]$tag.tagged_element_ids[0]);skip_existing=$false;add_leader=$false;units='mm';distance_space='paper';clearance=1;max_displacement=50} 'native-tag-plan'
        $tagArgs=$tagPlan.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
        $tagWrite=WriteTool 'horizun_annotate' $tagArgs 'native-tag-apply'
        $tagIds=@($tagWrite.Apply.Result.rows | ForEach-Object {[long]$_.element_id})
        $tagRead=ReadTool 'horizun_query_planimetry' @{mode='annotations';element_ids=$tagIds;units='mm'} 'native-tag-reread'
        $actual=@($tagRead.Result.rows)
        Record 'native-readable-tag' ($tagWrite.Ok -and $actual.Count -eq 1 -and $actual[0].bounds_readable -and -not $actual[0].orphaned -and $actual[0].type_id -eq $tag.type_id -and $actual[0].tagged_element_ids[0] -eq $tag.tagged_element_ids[0]) 'Complete planner action commits a real loaded tag and independently re-reads its target, type and extent' @{plan=$tagPlan.Raw;write=$tagWrite.Apply.Raw;read=$tagRead.Raw}
    } catch {
        Record 'native-readable-tag' $false 'Native tag planner/writer and independent re-read' @{error=$_.Exception.Message;stack=$_.ScriptStackTrace}
    }
    $q=ReadTool 'horizun_query_model' @{categories=@('OST_TitleBlocks');fields=@('type_id');max_rows=100} 'titleblock-types'
    $tb=@($q.Result.rows | Where-Object { $_.type_id -gt 0 } | Select-Object -First 1)
    if($tb.Count -ne 1) { throw 'Fixture missing: an installed titleblock type is required.' }
    $suffix=[guid]::NewGuid().ToString('N').Substring(0,8)
    $levels=ReadTool 'horizun_query_model' @{categories=@('OST_Levels');max_rows=1} 'level'
    $dimTypes=ReadTool 'horizun_query_dimensions' @{shapes=@('linear');max_rows=1} 'linear-dimension-type'
    $dimType=[long]@($dimTypes.Result.rows)[0].type.id
    $floor=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='create_floor_plan';key='p';name="HZD-$suffix-plan";level_id=[long]@($levels.Result.rows)[0].element_id})} 'fixture-plan'
    $floorId=[long]$floor.Apply.Result.aliases.p
    $grids=WriteTool 'horizun_create_elements' @{units='mm';elements=@(
        @{kind='grid';name="HZD-$suffix-A";start=@(800000,0,0);end=@(800000,6000,0)},
        @{kind='grid';name="HZD-$suffix-B";start=@(806000,0,0);end=@(806000,6000,0)})} 'fixture-grids'
    $gridIds=@($grids.Apply.Result.rows | ForEach-Object { [long]$_.element_id })
    foreach($scale in 50,100,200) {
        $copy=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='duplicate_view';source_view_id=$floorId;key='p';name="HZD-$suffix-plan-$scale";view_scale=$scale})} "scale-$scale"
        $viewId=[long]$copy.Apply.Result.aliases.p
        $null=ReadTool 'horizun_navigate' @{operation='open_view';view_id=$viewId} "activate-$scale"
        $spec=@{role='general';operation='intent_dimension';element_ids=$gridIds;selector='grid';offset=10;side='positive';dimension_type_id=$dimType}
        $a=@{operation='dimension_set';view_id=$viewId;units='mm';distance_space='paper';sets=@($spec)}
        $paperPlan=ReadTool 'horizun_plan_annotations' $a "paper-plan-$scale"
        $a.distance_space='model'
        $modelPlan=ReadTool 'horizun_plan_annotations' $a "model-plan-$scale"
        $paperY=[double]$paperPlan.Result.next_arguments.actions[0].line_start[1]
        $modelY=[double]$modelPlan.Result.next_arguments.actions[0].line_start[1]
        $writeArgs=$paperPlan.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
        $dim=WriteTool 'horizun_annotate' $writeArgs "dimension-$scale"
        $dimensionIds=@($dim.Apply.Result.rows | ForEach-Object {[long]$_.element_id})
        $reread=ReadTool 'horizun_query_dimensions' @{element_ids=$dimensionIds;units='mm'} "dimension-reread-$scale"
        $row=@($reread.Result.rows)[0]
        Record "paper-dimensions-$scale" ($dim.Ok -and [Math]::Abs(($paperY-$modelY)-10*($scale-1)) -lt .001 -and @($reread.Result.rows).Count -eq 1 -and [Math]::Abs([double]$row.value_internal_feet*304.8-6000) -lt .001 -and $row.broken_references -eq 0 -and $row.owner_view.id -eq $viewId) 'Native 6000 mm dimension created; paper offset normalized by view scale; independent value/reference/view re-read' @{write=$dim.Apply.Raw;read=$reread.Raw;paper_y=$paperY;model_y=$modelY}
    }
    $a.sets=@($spec,$spec)
    $duplicate=Invoke-HzTool -Run $run -Tool 'horizun_plan_annotations' -Arguments $a -Label 'duplicate-role'
    Record 'duplicate-dimension-role-refuses' ($duplicate.IsError -and $duplicate.Text -match 'unique|duplicate') 'A duplicated role never creates annotations' $duplicate.Raw
    $actions=@()
    foreach($i in 1..2) { $actions+=@{operation='create_sheet';key="s$i";number="HZD-$suffix-$i";name="Delivery test $i";title_block_type_id=[long]$tb[0].type_id} }
    foreach($i in 1..3) { $actions+=@{operation='create_drafting';key="v$i";name="HZD-$suffix-view-$i"} }
    $created=WriteTool 'horizun_manage_views' @{units='mm';actions=$actions} 'fixture-views'
    $ids=$created.Apply.Result.aliases
    $sheets=@([long]$ids.s1,[long]$ids.s2)
    $views=@([long]$ids.v1,[long]$ids.v2,[long]$ids.v3)
    $lines=@()
    foreach($v in $views) {
        $lines+=@{operation='create_detail_line';view_id=$v;start=@(0,0);end=@(200,0)}
        $lines+=@{operation='create_detail_line';view_id=$v;start=@(0,0);end=@(0,80)}
    }
    $null=WriteTool 'horizun_detail_2d' @{units='mm';actions=$lines} 'fixture-lines'
    $query=ReadTool 'horizun_query_planimetry' @{mode='sheets';sheet_ids=$sheets;units='mm'} 'paper-extents'
    $paper=@($query.Result.rows)[0].sheet_outline
    if(@($paper).Count -ne 4) { throw 'Fixture sheet has no measurable paper extent.' }
    $x=[double]$paper[0]+30;$y=[double]$paper[1]+30
    # Measure a real viewport first; derive capacity from its native label-inclusive size.
    $measure=ReadTool 'horizun_pack_sheets' @{target_document=$Document;sheet_id=$sheets[0];units='mm';items=@(@{key='v';view_id=$views[0]});margin=5;gap=5;dry_run=$true} 'measure-content'
    if($measure.Result.constructible -ne $true) { throw 'Fixture viewport could not be rehearsed.' }
    $extent=@($measure.Result.plan)[0].estimated_extent
    $w=[double]$extent.max[0]-[double]$extent.min[0];$height=[double]$extent.max[1]-[double]$extent.min[1]
    $rect=@($x,$y,($x+$w+20),($y+$height+20))
    $candidates=@(@{sheet_id=$sheets[0];usable_rect=$rect},@{sheet_id=$sheets[1];usable_rect=$rect})
    $tooMany=Invoke-HzTool -Run $run -Tool 'horizun_pack_sheets' -Label 'overflow-refusal' -Arguments @{
        target_document=$Document;units='mm';sheets=$candidates;margin=5;gap=5;items=@(
            @{key='a';view_id=$views[0]},@{key='b';view_id=$views[1]},@{key='c';view_id=$views[2]}) }
    Record 'overflow-refuses-whole-set' ($tooMany.IsError -and $tooMany.Text -match 'No candidate sheet') 'Three viewports cannot fit in two one-viewport rectangles' $tooMany.Raw
    $before=ReadTool 'horizun_query_planimetry' @{mode='placements';sheet_ids=$sheets;units='mm'} 'placements-after-refusal'
    Record 'overflow-leaves-no-placement' (@($before.Result.rows | Where-Object { $_.view_id -in $views }).Count -eq 0) 'No tested viewport committed by refused rehearsal (titleblock revision schedules are pre-existing)' $before.Raw
    $pack=WriteTool 'horizun_pack_sheets' @{units='mm';sheets=$candidates;margin=5;gap=5;items=@(
        @{key='a';view_id=$views[0]},@{key='b';view_id=$views[1]})} 'multi-sheet-apply'
    $after=ReadTool 'horizun_query_planimetry' @{mode='placements';sheet_ids=$sheets;units='mm'} 'placements-after-apply'
    $placed=@($after.Result.rows | Where-Object { $_.view_id -in $views })
    Record 'multi-sheet-atomic-apply' ($pack.Ok -and $placed.Count -eq 2 -and @($placed.sheet_id | Select-Object -Unique).Count -eq 2 -and @($placed.view_id | Select-Object -Unique).Count -eq 2) 'Two native viewports, one per candidate sheet; titleblock revision schedules excluded' @{write=$pack.Apply.Raw;read=$after.Raw}
    foreach($mode in @($true,$false)) {
        $label=if($mode){'combined'}else{'separate'}
        $path=Join-Path $run.WorkDir "$label.pdf"
        $pdf=WriteTool 'horizun_export' @{format='pdf';view_ids=$sheets;output_path=$path;pdf_combine=$mode;emit_manifest=$true;overwrite=$false} "pdf-$label"
        $delivery=$pdf.Apply.Result.delivery
        $files=@($delivery.files)
        $expected=if($mode){1}else{2}
        $manifest=Get-Content -LiteralPath $pdf.Apply.Result.manifest_path -Raw | ConvertFrom-Json
        $ok=$delivery.package_verified -eq $true -and $files.Count -eq $expected -and @($files | Where-Object { -not $_.page_count_verified }).Count -eq 0 -and @($manifest.files).Count -eq $expected -and ($files | Measure-Object -Property pages -Sum).Sum -eq 2
        foreach($file in $files) { $ok=$ok -and (Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256 }
        Record "pdf-$label-counts-and-hashes" $ok 'Read actual PDFs and manifest, two total pages, exact hashes' $pdf.Apply.Raw
        $again=Invoke-HzTool -Run $run -Tool 'horizun_export' -Label "pdf-$label-overwrite-refusal" -Arguments @{
            target_document=$Document;format='pdf';view_ids=$sheets;output_path=$path;pdf_combine=$mode;emit_manifest=$true;overwrite=$false}
        Record "pdf-$label-overwrite-refusal" ($again.IsError -and $again.Text -match 'already exists') 'Existing deliverables are not overwritten implicitly' $again.Raw
    }
    $captureIndex=0
    foreach($sheet in $sheets) {
        $capture=ReadTool 'horizun_capture_view' @{view_id=$sheet;pixel_size=2400} "capture-$sheet"
        $captureCase=@('capture-first','capture-second')[$captureIndex]
        Record $captureCase $capture.Ok 'Native sheet image available; human visual approval remains separate' $capture.Raw
        $captureIndex++
    }
    Add-HzProbe -Run $run -Id 'visual-acceptance' -Name 'Visual acceptance' -Expected 'Images reviewed for title, scale and legibility' -Status 'not_covered' -Because 'Image review is separate from structural file acceptance.'
}
catch {
    Record 'campaign-interrupted' $false 'Every declared stage reaches its measured assertion' @{error=$_.Exception.Message;stack=$_.ScriptStackTrace}
}
foreach($case in $expectedCases) {
    if(@($run.Probes | Where-Object { $_.id -eq $case }).Count -eq 0) {
        Add-HzProbe -Run $run -Id $case -Name $case -Expected 'Execute the declared live assertion' -Status 'not_covered' -Because 'Campaign stopped before this check; never counted as passed.'
    }
}
$done=Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
