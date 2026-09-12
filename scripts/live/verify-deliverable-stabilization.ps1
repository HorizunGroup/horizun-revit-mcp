#Requires -Version 7.0
<# The stabilization block, measured live: annotation coverage with a real
   readable tag family, the PDF print policy proved from page geometry, view
   scale on every scalable creation, the titleblock cell-fit finding, the
   profile preflight refusing by stage and field, and the delivery ledger
   (open, record, invalidate, approve, gate) with horizun_export as the
   publish stage. Disposable active fixture; no saves. Records every MCP
   request/reply. Visual approval of rendered pages is a SEPARATE step this
   harness names and never infers. Run after the general regression, never
   concurrently with model mutations. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][ValidateSet('yes-this-model-is-disposable')][string]$Disposable,
    [Parameter(Mandatory)][string]$ExpectedAddinSha256,
    [Parameter(Mandatory)][string]$ArtifactDir
)
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'deliverable-stabilization' -Document $Document -WorkDir (Join-Path $ArtifactDir ('calls-' + [guid]::NewGuid().ToString('N')))
$expectedCases=@(
    'coverage-readable-tag-commits','coverage-reports-scope','coverage-verify-reason-not-bare-false',
    'coverage-accept-list-refuses-wildcard',
    'coverage-dependent-view-surveys-primary','coverage-hidden-category-excluded-verifiably','coverage-annotation-categories-off-excluded',
    'view-scale-floor-plan','view-scale-drafting','view-scale-refused-on-sheet','view-scale-invalid-value-refused',
    'cell-fit-long-number-is-finding','cell-fit-short-number-passes',
    'preflight-refuses-by-stage-and-field','preflight-ok-plan-carries-undetermined',
    'pdf-print-unknown-field-refused','pdf-print-default-verified-against-sheet','pdf-print-a3-landscape-verified',
    'pdf-print-combined-page-verdicts','pdf-print-default-refuses-page-larger-than-titleblock',
    'pdf-print-margins-offsets-applied','pdf-print-offsets-need-zoom','pdf-print-export-in-background-by-year','cell-fit-name-field',
    'dim-text-offset-paper-verified','dim-text-position-reset','dim-leader-end-verified','dim-text-position-and-offset-refused',
    'tag-move-head-verified','tag-leader-free-end-verified','tag-leader-end-on-attached-refused',
    'ledger-open','ledger-order-enforced','ledger-record-write-reads-scope','ledger-status-detects-change',
    'ledger-gate-closed-refuses-export','ledger-approve-binds-scope','ledger-gate-open-exports-and-records',
    'ledger-open-twice-refused','ledger-corrupt-line-reported','ledger-approval-invalidated-by-scope-change',
    'ledger-previous-run-recovery',
    'visual-acceptance-rendered-pages')
function Record([string]$Id,[bool]$Ok,[string]$Expected,$Evidence,[string]$Observed) {
    Add-HzProbe -Run $run -Id $Id -Name $Id -Expected $Expected -Ok $Ok -Evidence $Evidence -Observed $Observed
}
function ReadTool([string]$Tool,[hashtable]$Arguments,[string]$Label) { Invoke-HzToolStrict -Run $run -Tool $Tool -Arguments $Arguments -Label $Label }
function TryTool([string]$Tool,[hashtable]$Arguments,[string]$Label) { Invoke-HzTool -Run $run -Tool $Tool -Arguments $Arguments -Label $Label }
function WriteTool([string]$Tool,[hashtable]$Arguments,[string]$Label,[switch]$AllowRefusal) {
    $Arguments['target_document']=$Document
    Invoke-HzWrite -Run $run -Tool $Tool -Arguments $Arguments -Label $Label -AllowRefusal:$AllowRefusal
}
function Detail($call) { Get-HzProp $call.Raw 'detail' }
try {
    $h=Get-HzHealth $run
    if($h.status -ne 'healthy' -or $h.active_document.title -ne $Document -or $h.addin_assembly.sha256 -ne $ExpectedAddinSha256) {
        throw ('Candidate/document gate refused: status={0} active={1} sha={2}. Nothing was authored.' -f $h.status,$h.active_document.title,$h.addin_assembly.sha256)
    }
    $suffix=[guid]::NewGuid().ToString('N').Substring(0,8)

    # ------------------------------------------------------------------ 1. coverage
    try {
        $existing=ReadTool 'horizun_query_planimetry' @{mode='annotations';categories=@('tags');max_rows=100;units='mm'} 'native-tag-fixture'
        $tag=@($existing.Result.rows | Where-Object { $_.bounds_readable -and -not $_.orphaned -and $_.target_count -eq 1 -and -not $_.targets_linked } | Select-Object -First 1)
        if($tag.Count -ne 1){throw 'Fixture missing: no readable native tag with one host target.'}
        $tag=$tag[0]
        $null=ReadTool 'horizun_navigate' @{operation='open_view';view_id=[long]$tag.owner_view_id} 'activate-tag-view'
        $plan=ReadTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=[long]$tag.owner_view_id;tag_type_id=[long]$tag.type_id;tag_mode='by_category';element_ids=@([long]$tag.tagged_element_ids[0]);skip_existing=$false;add_leader=$false;units='mm';distance_space='paper';clearance=1;max_displacement=50} 'coverage-plan'
        $cov=$plan.Result.annotation_coverage
        Record 'coverage-reports-scope' ($null -ne $cov -and $cov.schema -eq 'horizun.annotation-coverage/1' -and $cov.coverage_complete -eq $true -and $cov.clearance_scope -eq 'complete' -and $cov.considered -ge 1 -and $cov.bounds.source -in @('annotation_crop','model_crop','none')) 'Planner returns a complete coverage with per-annotation verdicts, a bounds source and complete clearance scope' $plan.Raw ("considered={0} measured={1} excluded_not_visible={2} unknown={3} bounds={4}" -f $cov.considered,$cov.counts.measured,$cov.counts.excluded_not_visible_in_view,$cov.counts.unknown_unreadable_extent,$cov.bounds.source)
        $tagArgs=$plan.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
        $tagWrite=WriteTool 'horizun_annotate' $tagArgs 'coverage-tag-apply'
        $ids=@($tagWrite.Apply.Result.rows | ForEach-Object {[long]$_.element_id})
        $reread=ReadTool 'horizun_query_planimetry' @{mode='annotations';element_ids=$ids;units='mm'} 'coverage-tag-reread'
        $actual=@($reread.Result.rows)
        $row=@($tagWrite.Apply.Result.rows)[0]
        Record 'coverage-readable-tag-commits' ($tagWrite.Ok -and $actual.Count -eq 1 -and $actual[0].bounds_readable -and -not $actual[0].orphaned -and $actual[0].type_id -eq $tag.type_id -and $actual[0].tagged_element_ids[0] -eq $tag.tagged_element_ids[0] -and $row.annotation_coverage.coverage_complete -eq $true) 'Complete planner action commits a real readable tag; the apply row carries the coverage it was judged against; independent re-read of target/type/extent' @{plan=$plan.Raw;write=$tagWrite.Apply.Raw;read=$reread.Raw}
        $script:coverageTagIds=$ids
    } catch { Record 'coverage-readable-tag-commits' $false 'Native readable tag through the full coverage path' @{error=$_.Exception.Message;stack=$_.ScriptStackTrace} }
    # A verification failure must carry a reason, never a bare false: an impossible
    # displacement forces the layout to refuse, and the row must say why.
    try {
        $view=[long]$tag.owner_view_id
        $dense=TryTool 'horizun_annotate' @{target_document=$Document;units='mm';dry_run=$true;actions=@(@{operation='tag';view_id=$view;element_id=[long]$tag.tagged_element_ids[0];point=@(0,0,0);tag_type_id=[long]$tag.type_id;avoid_collisions=$true;layout_clearance=100000;layout_max_displacement=0;require_tag_text=$true})} 'coverage-forced-refusal'
        $rehearsal=Get-HzProp $dense.Result 'rehearsal'
        $rows=@(if($rehearsal){ Get-HzProp $rehearsal 'actions' }); $r0=if($rows.Count -gt 0){$rows[0]}else{$null}
        $reason=if($r0){[string]$r0.reason}else{$dense.Text}
        Record 'coverage-verify-reason-not-bare-false' (($dense.IsError -or ($r0 -and $r0.constructible -eq $false)) -and $reason -and $reason -notmatch 'see this command''s text/tag verification' -and ($reason -match 'obstacle|clearance|displacement|coverage|collision|overlap')) 'A refused tag layout names the obstacle/clearance/coverage cause instead of a generic sentence' $dense.Raw (Limit-HzText $reason 300)
    } catch { Record 'coverage-verify-reason-not-bare-false' $false 'Reasoned refusal' @{error=$_.Exception.Message} }
    $wild=TryTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=[long]$tag.owner_view_id;element_ids=@([long]$tag.tagged_element_ids[0]);units='mm';accept_unmeasurable=@('*')} 'coverage-wildcard'
    Add-HzRefusalProbe -Run $run -Id 'coverage-accept-list-refuses-wildcard' -Name 'coverage-accept-list-refuses-wildcard' -Call $wild -MustMatch 'positive element ids|blanket'
    # Dependent views and hidden categories, self-provisioned so every year's base
    # fixture can measure them. The dependent view is typed (duplicate_view
    # AsDependent). Hiding a category and switching annotation categories off in a
    # view have no typed writer, so that STAGING goes through horizun_execute_python
    # - its evidence is the script's own testimony; the assertion is made by the
    # planner's host-verified coverage afterwards.
    $script:stagedCategoryId=$null
    try {
        # Our own primary: a WithDetailing duplicate of the tag's view (its
        # annotations travel with it), with the view template DETACHED. Measured
        # on 2026 (c7/c8): a view whose template governs V/G answers
        # CanCategoryBeHidden=false for EVERY category (the fixture's L2 carries
        # "Mechanical Plan"), and a dependent view never owns V/G at all - so the
        # fixture view is left as it is and the staging happens on a disposable copy.
        $origPrimaryId=[long]$tag.owner_view_id
        $prim=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='duplicate_view';source_view_id=$origPrimaryId;key='prim';name="HZS-$suffix-primary";duplicate_option='WithDetailing'})} 'coverage-primary-fixture'
        $primaryId=[long]$prim.Apply.Result.aliases.prim
        $pyDetach=@"
from Autodesk.Revit.DB import Transaction, ElementId
view = doc.GetElement(ElementId($primaryId))
had = view.ViewTemplateId
t = Transaction(doc, 'HZS stage: detach the view template from the disposable primary')
t.Start()
view.ViewTemplateId = ElementId.InvalidElementId
t.Commit()
__output__ = {'status': 'self_reported_verified', 'had_template': had != ElementId.InvalidElementId, 'template_detached': view.ViewTemplateId == ElementId.InvalidElementId, 'evidence': {'view_id': $primaryId}}
"@
        $detach=TryTool 'horizun_execute_python' @{target_document=$Document;code=$pyDetach;idempotency_key=("stage-detach-$suffix")} 'coverage-stage-detach-template'
        if($detach.IsError) { throw ('staging (python) refused: ' + (Limit-HzText $detach.Text 300)) }
        $dep=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='duplicate_view';source_view_id=$primaryId;key='dep';name="HZS-$suffix-dependent";duplicate_option='AsDependent'})} 'coverage-dependent-fixture'
        $depView=[long]$dep.Apply.Result.aliases.dep
        $null=ReadTool 'horizun_navigate' @{operation='open_view';view_id=$depView} 'coverage-activate-dependent'
        $depPlan=ReadTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=$depView;tag_type_id=[long]$tag.type_id;tag_mode='by_category';element_ids=@([long]$tag.tagged_element_ids[0]);skip_existing=$false;add_leader=$false;units='mm';distance_space='paper';clearance=1;max_displacement=50} 'coverage-dependent-plan'
        $dc=$depPlan.Result.annotation_coverage
        Record 'coverage-dependent-view-surveys-primary' ($dep.Ok -and $dc.is_dependent_view -eq $true -and [long]$dc.primary_view_id -eq $primaryId -and $dc.considered -ge [int]$cov.considered -and $dc.coverage_complete -eq $true) 'A dependent view is surveyed through its primary: is_dependent_view, primary_view_id and at least the primary annotation count considered' $depPlan.Raw ("dependent={0} primary={1} considered={2} (primary run considered {3})" -f $dc.is_dependent_view,$dc.primary_view_id,$dc.considered,$cov.considered)
        # Stage: hide, in OUR primary, the category of the first measured obstacle
        # whose category that view can hide (CanCategoryBeHidden); the dependent
        # inherits it. The script also reports every category it could not hide.
        $measuredIds=@($dc.entries | Where-Object { $_.verdict -eq 'measured' } | ForEach-Object { [long]$_.element_id })
        if($measuredIds.Count -lt 1) { throw 'Fixture missing: no measured obstacle to hide.' }
        $idList=($measuredIds -join ',')
        $py=@"
from Autodesk.Revit.DB import Transaction, ElementId
view = doc.GetElement(ElementId($primaryId))
chosen = None
refused = []
for raw in [$idList]:
    e = doc.GetElement(ElementId(raw))
    if e is None or e.Category is None:
        continue
    if view.CanCategoryBeHidden(e.Category.Id):
        chosen = e
        break
    if e.Category.Name not in refused:
        refused.append(e.Category.Name)
if chosen is None:
    __output__ = {'status': 'failed', 'reason': 'no measured obstacle has a category this view can hide; CanCategoryBeHidden=false for: ' + ', '.join(refused) + ' (view template: ' + str(view.ViewTemplateId) + ')'}
else:
    cat = chosen.Category
    t = Transaction(doc, 'HZS stage: hide category in the primary view')
    t.Start()
    view.SetCategoryHidden(cat.Id, True)
    t.Commit()
    cat_id = getattr(cat.Id, 'Value', None)
    if cat_id is None:
        cat_id = cat.Id.IntegerValue
    el_id = getattr(chosen.Id, 'Value', None)
    if el_id is None:
        el_id = chosen.Id.IntegerValue
    __output__ = {'status': 'self_reported_verified', 'category': cat.Name, 'hidden': view.GetCategoryHidden(cat.Id), 'dependent_reads_hidden': doc.GetElement(ElementId($depView)).GetCategoryHidden(cat.Id), 'element_id': el_id, 'category_id': cat_id, 'evidence': {'view_id': $primaryId, 'dependent_view_id': $depView, 'category_id': cat_id}}
"@
        $stage=TryTool 'horizun_execute_python' @{target_document=$Document;code=$py;idempotency_key=("stage-hide-$suffix")} 'coverage-stage-hide-category'
        if($stage.IsError) { throw ('staging (python) refused: ' + (Limit-HzText $stage.Text 300)) }
        $stageOut=Get-HzProp $stage.Result 'output'; if(-not $stageOut){ $stageOut=Get-HzProp $stage.Result '__output__' }
        if((Get-HzProp $stageOut 'status') -ne 'self_reported_verified') { throw ('staging found no hideable category: ' + (Limit-HzText ([string](Get-HzProp $stageOut 'reason')) 200)) }
        $hideId=[long](Get-HzProp $stageOut 'element_id'); $script:stagedCategoryId=[long](Get-HzProp $stageOut 'category_id')
        $hidPlan=ReadTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=$depView;tag_type_id=[long]$tag.type_id;tag_mode='by_category';element_ids=@([long]$tag.tagged_element_ids[0]);skip_existing=$false;add_leader=$false;units='mm';distance_space='paper';clearance=1;max_displacement=50} 'coverage-hidden-category-plan'
        $hc=$hidPlan.Result.annotation_coverage
        $hiddenEntry=@($hc.entries | Where-Object { [long]$_.element_id -eq $hideId })
        Record 'coverage-hidden-category-excluded-verifiably' ($hiddenEntry.Count -eq 1 -and $hiddenEntry[0].verdict -eq 'excluded_category_hidden' -and $hc.coverage_complete -eq $true -and [int]$hc.counts.excluded_category_hidden -ge 1) 'An obstacle whose category is hidden in the view is excluded with the verdict excluded_category_hidden and the probe that decided it; coverage stays complete' @{staging_python_self_reported=$stage.Raw;plan=$hidPlan.Raw} ("verdict={0} evidence={1}" -f $hiddenEntry[0].verdict,(Limit-HzText ([string]$hiddenEntry[0].evidence) 120))
        # Stage: a further dependent with EVERY annotation category off.
        $dep2=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='duplicate_view';source_view_id=$primaryId;key='dep2';name="HZS-$suffix-dependent-annoff";duplicate_option='AsDependent'})} 'coverage-annoff-fixture'
        $dep2View=[long]$dep2.Apply.Result.aliases.dep2
        $py2=@"
from Autodesk.Revit.DB import Transaction, ElementId
view = doc.GetElement(ElementId($primaryId))
dep2 = doc.GetElement(ElementId($dep2View))
t = Transaction(doc, 'HZS stage: annotation categories off in the primary view')
t.Start()
view.SetCategoryHidden(ElementId($($script:stagedCategoryId)), False)
view.AreAnnotationCategoriesHidden = True
t.Commit()
__output__ = {'status': 'self_reported_verified', 'annotation_categories_hidden': view.AreAnnotationCategoriesHidden, 'dependent_reads_hidden': dep2.AreAnnotationCategoriesHidden, 'category_restored': not view.GetCategoryHidden(ElementId($($script:stagedCategoryId))), 'evidence': {'view_id': $primaryId, 'dependent_view_id': $dep2View}}
"@
        $stage2=TryTool 'horizun_execute_python' @{target_document=$Document;code=$py2;idempotency_key=("stage-annoff-$suffix")} 'coverage-stage-annotations-off'
        if($stage2.IsError) { throw ('staging (python) refused: ' + (Limit-HzText $stage2.Text 300)) }
        $offPlan=TryTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=$dep2View;tag_type_id=[long]$tag.type_id;tag_mode='by_category';element_ids=@([long]$tag.tagged_element_ids[0]);skip_existing=$false;add_leader=$false;units='mm';distance_space='paper';clearance=1;max_displacement=50} 'coverage-annotations-off-plan'
        $oc=Get-HzProp $offPlan.Result 'annotation_coverage'
        $offEntries=@(if($oc){ $oc.entries })
        $allOff=($offEntries.Count -ge 1) -and (@($offEntries | Where-Object { $_.verdict -ne 'excluded_annotation_categories_hidden' -and $_.verdict -ne 'measured' }).Count -eq 0) -and (@($offEntries | Where-Object { $_.verdict -eq 'excluded_annotation_categories_hidden' }).Count -ge 1)
        Record 'coverage-annotation-categories-off-excluded' ($offPlan.Ok -and $null -ne $oc -and $oc.coverage_complete -eq $true -and $allOff) 'With every annotation category off in the view, unreadable obstacles are excluded as excluded_annotation_categories_hidden (a still-readable box stays an obstacle); nothing is unknown' @{staging_python_self_reported=$stage2.Raw;plan=$offPlan.Raw} ("entries={0} off={1}" -f $offEntries.Count,@($offEntries | Where-Object { $_.verdict -eq 'excluded_annotation_categories_hidden' }).Count)
    } catch { Record 'coverage-b-section-error' $false 'Dependent view / hidden category section reaches every assertion' @{error=$_.Exception.Message;stack=$_.ScriptStackTrace} (Limit-HzText $_.Exception.Message 240) }

    # ------------------------------------------------------------------ 1b. 8B: text positions and leaders
    # Dimension: an existing single-segment dimension of the fixture, its text moved
    # 5 mm ON PAPER (scaled by the view scale), verified by re-read, then reset; a
    # text leader switched on with an explicit end. Tag: the readable tag committed
    # above, head moved by a vector and its leader made free with an explicit end.
    try {
        # A single-segment dimension of our own: two grids 6000 mm apart, a fresh
        # floor plan, and the planned/committed grid-to-grid dimension.
        $lv=ReadTool 'horizun_query_model' @{categories=@('OST_Levels');max_rows=1} '8b-level'
        $lvId=[long]@($lv.Result.rows)[0].element_id
        $dt=ReadTool 'horizun_query_dimensions' @{shapes=@('linear');max_rows=1} '8b-linear-type'
        $dtId=[long]@($dt.Result.rows)[0].type.id
        $g=WriteTool 'horizun_create_elements' @{units='mm';elements=@(
            @{kind='grid';name="HZS-$suffix-GA";start=@(700000,0,0);end=@(700000,6000,0)},
            @{kind='grid';name="HZS-$suffix-GB";start=@(706000,0,0);end=@(706000,6000,0)})} '8b-grids'
        $gIds=@($g.Apply.Result.rows | ForEach-Object { [long]$_.element_id })
        $pv=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='create_floor_plan';key='p';name="HZS-$suffix-dimplan";level_id=$lvId})} '8b-plan'
        $dimView=[long]$pv.Apply.Result.aliases.p
        $null=ReadTool 'horizun_navigate' @{operation='open_view';view_id=$dimView} '8b-activate-dim-view'
        $dp=ReadTool 'horizun_plan_annotations' @{operation='intent_dimension';view_id=$dimView;units='mm';distance_space='paper';element_ids=$gIds;selector='grid';offset=10;side='positive';dimension_type_id=$dtId} '8b-dimension-plan'
        $dw=WriteTool 'horizun_annotate' ($dp.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable) '8b-dimension-commit'
        $dimId=[long]@($dw.Apply.Result.rows)[0].element_id
        $viewRow=ReadTool 'horizun_query_model' @{element_ids=@($dimView);return_parameters=@('VIEW_SCALE');max_rows=1} '8b-dim-view-scale'
        $dimScale=[int](@($viewRow.Result.rows)[0].parameters.VIEW_SCALE.raw)
        $off=WriteTool 'horizun_edit_dimensions' @{units='mm';actions=@(@{element_id=$dimId;text_offset=@(5,0);distance_space='paper'})} '8b-dim-text-offset'
        $offRow=@($off.Apply.Result.rows)[0]; $offField=@($offRow.fields | Where-Object { $_.field -eq 'text_position' })[0]
        $moved=if($offField){ [Math]::Sqrt(([double]$offField.read_feet[0]-[double]$offField.before_feet[0])*([double]$offField.read_feet[0]-[double]$offField.before_feet[0])+([double]$offField.read_feet[1]-[double]$offField.before_feet[1])*([double]$offField.read_feet[1]-[double]$offField.before_feet[1])+([double]$offField.read_feet[2]-[double]$offField.before_feet[2])*([double]$offField.read_feet[2]-[double]$offField.before_feet[2]))*304.8 }else{ -1 }
        Record 'dim-text-offset-paper-verified' ($off.Ok -and $offRow.verified -eq $true -and $offField.match -eq $true -and [Math]::Abs($moved-5*$dimScale) -lt 0.01) ("5 mm on paper at 1:$dimScale moves the text " + (5*$dimScale) + " mm in the model; TextPosition re-read within 1e-5 ft") $off.Apply.Raw ("moved_mm={0} scale={1}" -f [Math]::Round($moved,3),$dimScale)
        $reset=WriteTool 'horizun_edit_dimensions' @{units='mm';actions=@(@{element_id=$dimId;reset_text_position=$true})} '8b-dim-reset'
        $resetRow=@($reset.Apply.Result.rows)[0]; $resetField=@($resetRow.fields | Where-Object { $_.field -eq 'reset_text_position' })[0]
        Record 'dim-text-position-reset' ($reset.Ok -and $resetRow.verified -eq $true -and $resetField.match -eq $true -and $resetField.verification -match 'invocation_completed') 'reset_text_position runs and reports before/after without claiming a default it cannot read' $reset.Apply.Raw
        $bf=@($offField.before_feet | ForEach-Object { [double]$_ })
        $end=@(($bf[0]*304.8+200),($bf[1]*304.8+150),($bf[2]*304.8))
        $lead=WriteTool 'horizun_edit_dimensions' @{units='mm';actions=@(@{element_id=$dimId;leader=$true;leader_end=$end})} '8b-dim-leader' -AllowRefusal
        $leadRow=if($lead.Apply){@($lead.Apply.Result.rows)[0]}else{$null}
        $leadFields=@(if($leadRow){ $leadRow.fields })
        $leadOk=$lead.Ok -and $leadRow.verified -eq $true -and @($leadFields | Where-Object { $_.field -eq 'leader' -and $_.match -eq $true }).Count -eq 1 -and @($leadFields | Where-Object { $_.field -eq 'leader_end' -and $_.match -eq $true }).Count -eq 1
        $leadCall=if($lead.Apply){$lead.Apply}else{$lead.Dry}
        Record 'dim-leader-end-verified' $leadOk 'leader=true plus an explicit leader_end commit and both re-read (HasLeader, LeaderEndPosition within 1e-5 ft)' $leadCall.Raw (Limit-HzText $leadCall.Text 200)
        $both=TryTool 'horizun_edit_dimensions' @{target_document=$Document;units='mm';dry_run=$true;actions=@(@{element_id=$dimId;text_position=@(0,0,0);text_offset=@(1,1)})} '8b-dim-both'
        # A rehearsal publishes the invalid row in errors[] beside a successful reply
        # (contract: dry_run never has to fail to say no); judge the row, not the status.
        $bothErr=@(Get-HzProp $both.Result 'errors'); $bothMsg=if($bothErr.Count -gt 0){[string]$bothErr[0].error}else{[string]$both.Text}
        Record 'dim-text-position-and-offset-refused' (($both.IsError -or ($bothErr.Count -eq 1 -and [int](Get-HzProp $both.Result 'valid_actions') -eq 0)) -and $bothMsg -match 'two answers to one question') 'text_position and text_offset together are refused by name in the rehearsal (errors[] names the row, nothing is planned)' $both.Raw (Limit-HzText $bothMsg 200)
    } catch { Record '8b-dimension-section-error' $false '8B dimension section reaches every assertion' @{error=$_.Exception.Message;stack=$_.ScriptStackTrace} (Limit-HzText $_.Exception.Message 240) }
    try {
        $tagId=[long]$script:coverageTagIds[0]
        $null=ReadTool 'horizun_navigate' @{operation='open_view';view_id=[long]$tag.owner_view_id} '8b-activate-tag-view'
        # A leader end near the tagged element, from its own bounding box (mm).
        $hostRow=ReadTool 'horizun_query_model' @{element_ids=@([long]$tag.tagged_element_ids[0]);include_bounding_box=$true;coordinate_units='mm';max_rows=1} '8b-tagged-element-box'
        $hb=@($hostRow.Result.rows)[0].bounding_box
        $end=@(([double]$hb.max[0]+300),([double]$hb.max[1]+300),(([double]$hb.min[2]+[double]$hb.max[2])/2))
        $mv=WriteTool 'horizun_transform_elements' @{units='mm';operations=@(@{operation='move_tag_head';element_ids=@($tagId);vector=@(50,0,0)})} '8b-tag-move-head'
        $mvText=[string]$mv.Apply.Text
        $mvRead=ReadTool 'horizun_query_planimetry' @{mode='annotations';element_ids=@($tagId);units='mm'} '8b-tag-reread-1'
        Record 'tag-move-head-verified' ($mv.Ok -and $mvText -match '"verified":\s*true' -and $mvText -match 'head_read_feet' -and @($mvRead.Result.rows).Count -eq 1 -and (Get-HzProp @($mvRead.Result.rows)[0] 'bounds_readable')) 'move_tag_head by vector commits and TagHeadPosition is re-read within 1e-5 ft; the tag stays readable' @{write=$mv.Apply.Raw;read=$mvRead.Raw}
        $attached=TryTool 'horizun_transform_elements' @{target_document=$Document;units='mm';dry_run=$true;operations=@(@{operation='set_tag_leader';element_ids=@($tagId);has_leader=$true;leader_end_condition='attached';leader_end=@(0,0,0)})} '8b-tag-attached-end'
        $attachedErr=@(Get-HzProp $attached.Result 'errors'); $attachedMsg=if($attachedErr.Count -gt 0){[string]$attachedErr[0].error}else{[string]$attached.Text}
        Record 'tag-leader-end-on-attached-refused' (($attached.IsError -or $attachedErr.Count -gt 0) -and $attachedMsg -match 'FREE leader end') 'A free end on an attached leader is refused by name, never dropped' $attached.Raw (Limit-HzText $attachedMsg 200)
        $free=WriteTool 'horizun_transform_elements' @{units='mm';operations=@(@{operation='set_tag_leader';element_ids=@($tagId);has_leader=$true;leader_end_condition='free';leader_end=@(($end[0]),($end[1]),0)})} '8b-tag-free-end' -AllowRefusal
        $freeCall=if($free.Apply){$free.Apply}else{$free.Dry}; $freeText=[string]$freeCall.Text
        $freeRead=ReadTool 'horizun_query_planimetry' @{mode='annotations';element_ids=@($tagId);units='mm'} '8b-tag-reread-2'
        $hasLeader=Get-HzProp @($freeRead.Result.rows)[0] 'has_leader'
        Record 'tag-leader-free-end-verified' ($free.Ok -and $freeText -match '"verified":\s*true' -and $freeText -match '"leader_end"' -and $hasLeader -eq $true) 'has_leader + free end condition + explicit leader_end commit; the leader end is re-read within 1e-5 ft and an independent query sees the leader' @{write=$freeCall.Raw;read=$freeRead.Raw} (Limit-HzText $freeText 200)
    } catch { Record '8b-tag-section-error' $false '8B tag section reaches every assertion' @{error=$_.Exception.Message;stack=$_.ScriptStackTrace} (Limit-HzText $_.Exception.Message 240) }

    # ------------------------------------------------------------------ 2. view scale
    $levels=ReadTool 'horizun_query_model' @{categories=@('OST_Levels');max_rows=1} 'level'
    $levelId=[long]@($levels.Result.rows)[0].element_id
    $fp=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='create_floor_plan';key='p';name="HZS-$suffix-plan";level_id=$levelId;view_scale=200})} 'view-scale-floor'
    $fpRow=@($fp.Apply.Result.rows)[0]
    $fpRead=ReadTool 'horizun_query_model' @{element_ids=@([long]$fp.Apply.Result.aliases.p);return_parameters=@('VIEW_SCALE');max_rows=1} 'view-scale-floor-reread'
    $fpScale=[int](@($fpRead.Result.rows)[0].parameters.VIEW_SCALE.raw)
    Record 'view-scale-floor-plan' ($fp.Ok -and $fpRow.view_scale -eq 200 -and $fpRow.view_scale_verified -eq $true -and $fpScale -eq 200) 'create_floor_plan honours view_scale=200, reports it per row and an independent parameter read agrees' @{write=$fp.Apply.Raw;read=$fpRead.Raw} ("row.view_scale={0} reread={1}" -f $fpRow.view_scale,$fpScale)
    $dr=WriteTool 'horizun_manage_views' @{units='mm';actions=@(@{operation='create_drafting';key='d';name="HZS-$suffix-drafting";view_scale=20})} 'view-scale-drafting'
    $drRow=@($dr.Apply.Result.rows)[0]
    Record 'view-scale-drafting' ($dr.Ok -and $drRow.view_scale -eq 20 -and $drRow.view_scale_verified -eq $true) 'create_drafting honours view_scale=20 and re-reads it' $dr.Apply.Raw ("row.view_scale={0}" -f $drRow.view_scale)
    $q=ReadTool 'horizun_query_model' @{categories=@('OST_TitleBlocks');fields=@('type_id');max_rows=100} 'titleblock-types'
    $tb=@($q.Result.rows | Where-Object { $_.type_id -gt 0 } | Select-Object -First 1)
    if($tb.Count -ne 1) { throw 'Fixture missing: an installed titleblock type is required.' }
    # manage_views reports per-action validation inside a successful dry run
    # (valid/invalid/errors), so the refusal is read from errors[], not from a
    # tool error.
    $sheetScale=TryTool 'horizun_manage_views' @{target_document=$Document;units='mm';dry_run=$true;actions=@(@{operation='create_sheet';key='s';number="HZS-$suffix-X";name='scale refusal';title_block_type_id=[long]$tb[0].type_id;view_scale=100})} 'view-scale-sheet'
    $sheetErr=@(Get-HzProp $sheetScale.Result 'errors'); $sheetMsg=if($sheetErr.Count -gt 0){[string]$sheetErr[0].error}else{''}
    Record 'view-scale-refused-on-sheet' ($sheetScale.Ok -and (Get-HzProp $sheetScale.Result 'invalid') -eq 1 -and $sheetMsg -match 'no drawing scale|would be ignored' -and @(Get-HzProp $sheetScale.Result 'plan').Count -eq 0) 'view_scale on create_sheet is refused per action, naming that a sheet has no drawing scale and that the argument would otherwise be ignored; nothing planned' $sheetScale.Raw (Limit-HzText $sheetMsg 200)
    # Revit accepts custom scales (1:7 is valid), so the value probe measures the
    # template case instead: a view whose template controls View Scale must refuse
    # the assignment by name rather than let the template win silently.
    $templates=ReadTool 'horizun_query_model' @{categories=@('OST_Views');return_fields=@('name','is_view_template','view_type');max_rows=200} 'view-templates'
    $planTemplates=@($templates.Result.rows | Where-Object { $_.is_view_template -eq $true -and $_.view_type -match 'FloorPlan|Plan' })
    if($planTemplates.Count -gt 0) {
        $t=$planTemplates[0]
        $tpl=TryTool 'horizun_manage_views' @{target_document=$Document;units='mm';dry_run=$true;actions=@(@{operation='create_floor_plan';key='pt';name="HZS-$suffix-templated";level_id=$levelId},@{operation='apply_template';view_key='pt';template_view_id=[long]$t.element_id;view_scale=250})} 'view-scale-template'
        $tplText=$tpl.Text
        $controlled=$tplText -match 'controls View Scale'
        $tplRows=@(Get-HzProp $tpl.Result 'rows')
        Record 'view-scale-invalid-value-refused' ($controlled -or ($tpl.Ok -and (Get-HzProp $tpl.Result 'invalid') -eq 0)) 'A template that controls View Scale refuses view_scale by name; a template that leaves it uncontrolled lets the assignment verify' $tpl.Raw (Limit-HzText $tplText 240)
    } else {
        Add-HzProbe -Run $run -Id 'view-scale-invalid-value-refused' -Name 'view-scale-invalid-value-refused' -Expected 'Template-controlled scale refuses by name' -Status 'fixture_missing' -Because 'The fixture has no plan view template to measure template control against.'
    }

    # ------------------------------------------------------------------ 3. sheets + cell fit
    $actions=@()
    $actions+=@{operation='create_sheet';key='s1';number="HZS-$suffix-LONG-NUMBER-1";name="Stabilization 1";title_block_type_id=[long]$tb[0].type_id}
    $actions+=@{operation='create_sheet';key='s2';number="S-$suffix";name="Stabilization 2";title_block_type_id=[long]$tb[0].type_id}
    foreach($i in 1..2) { $actions+=@{operation='create_drafting';key="v$i";name="HZS-$suffix-view-$i"} }
    $created=WriteTool 'horizun_manage_views' @{units='mm';actions=$actions} 'fixture-sheets'
    $ids=$created.Apply.Result.aliases
    $sheets=@([long]$ids.s1,[long]$ids.s2); $views=@([long]$ids.v1,[long]$ids.v2)
    $lines=@(); foreach($v in $views) { $lines+=@{operation='create_detail_line';view_id=$v;start=@(0,0);end=@(200,0)}; $lines+=@{operation='create_detail_line';view_id=$v;start=@(0,0);end=@(0,80)} }
    $null=WriteTool 'horizun_detail_2d' @{units='mm';actions=$lines} 'fixture-lines'
    $cellRules=@{requirement_set=@{id='cells';version='1'};rules=@(@{id='number-fits';entity='sheet';selector=@{applies_to_all=$true};assertion=@{operator='fits_titleblock_cell';value=@{field='sheet_number';cell_width=40;text_height=5;char_width_factor=0.6}}})}
    $audit=ReadTool 'horizun_audit_planimetry' @{scope='sheets';sheet_ids=$sheets;requirement_set=$cellRules;units='mm'} 'cell-fit-audit'
    $findings=@($audit.Result.findings | Where-Object { (Get-HzProp $_ 'rule_id') -eq 'number-fits' })
    $long=@($findings | Where-Object { $_.status -eq 'failed' -and ($_.element_ids -contains $sheets[0]) })
    $short=@($findings | Where-Object { $_.status -eq 'failed' -and ($_.element_ids -contains $sheets[1]) })
    Record 'cell-fit-long-number-is-finding' ($long.Count -eq 1 -and [double]$long[0].observed.estimated_text_width -gt 40 -and $long[0].observed.value -eq "HZS-$suffix-LONG-NUMBER-1") 'A 22+ character number at 5 mm text in a 40 mm cell is a failed finding with the arithmetic and the untrimmed value' $audit.Raw ("long findings={0} short findings={1}" -f $long.Count,$short.Count)
    Record 'cell-fit-short-number-passes' ($short.Count -eq 0) 'A 10 character number fits the same cell and raises no finding' $audit.Raw

    # ------------------------------------------------------------------ 4. preflight
    $dimTypes=ReadTool 'horizun_query_dimensions' @{shapes=@('linear');max_rows=1} 'linear-dimension-type'
    $dimType=[long]@($dimTypes.Result.rows)[0].type.id
    $pdfDir=$run.WorkDir
    $profile=@{id='stabilization';version='1';units='mm';
        views=@(@{view_id=[long]$tag.owner_view_id;tags=@{element_ids=@([long]$tag.tagged_element_ids[0]);tag_type_id=[long]$tag.type_id;clearance=1;max_displacement=50;skip_existing=$false;add_leader=$false}});
        packing=@{sheets=@(@{sheet_id=$sheets[0];usable_rect=@(30,30,400,250)},@{sheet_id=$sheets[1];usable_rect=@(30,30,400,250)});items=@(@{key='a';view_id=$views[0]});margin=5;gap=5};
        publication=@{format='pdf';view_ids=$sheets;output_path=(Join-Path $pdfDir 'ledger.pdf');pdf_combine=$true;overwrite=$false;emit_manifest=$true;pdf_print=@{paper_format='ISO_A3';orientation='landscape'}};
        requirement_set=@{requirement_set=@{id='std';version='1'};rules=@(@{id='number';entity='sheet';selector=@{applies_to_all=$true};assertion=@{field='sheet_number';operator='not_empty'}})}}
    $bad=[System.Management.Automation.PSSerializer]::Deserialize([System.Management.Automation.PSSerializer]::Serialize($profile))
    $bad=$profile | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
    $bad.views[0].tags.tag_mode='by_family'
    $bad.packing.items[0].key=''
    $bad.publication.output_path='relative.pdf'
    $bad.publication.pdf_print=@{paper_size='A1'}
    $pre=TryTool 'horizun_plan_views' @{operation='deliverable_set';delivery_profile=$bad} 'preflight-bad'
    # A structured refusal carries its detail as the error reply's result.
    $d=if((Get-HzProp $pre.Result 'schema') -eq 'horizun.delivery-preflight/1'){ $pre.Result } else { Detail $pre }
    $fields=@(if($d){$d.errors | ForEach-Object { "$($_.stage)/$($_.field)" }})
    Record 'preflight-refuses-by-stage-and-field' ($pre.IsError -and $d -and $d.ok -eq $false -and $d.error_count -ge 4 -and ($fields -contains ("tags_{0}/tags.tag_mode" -f $tag.owner_view_id)) -and ($fields -contains 'pack/packing.items[0].key') -and ($fields -contains 'publish/publication.output_path') -and ($fields -contains 'publish/publication.pdf_print')) 'Four independent stage errors are all reported at once, by stage and field, and the plan is withheld' $pre.Raw ($fields -join ', ')
    $good=ReadTool 'horizun_plan_views' @{operation='deliverable_set';delivery_profile=$profile} 'preflight-good'
    $pf=$good.Result.preflight
    Record 'preflight-ok-plan-carries-undetermined' ($pf.ok -eq $true -and $good.Result.safe_to_execute -eq $true -and @($pf.undetermined).Count -ge 3 -and (@($pf.checked) -contains 'host.titleblocks') -and (@($pf.checked) -contains 'host.output_conflicts')) 'A valid profile passes static and host preflight, lists what only rehearsal/export decide, and names the host checks it ran' $good.Raw ("undetermined={0} checked={1}" -f @($pf.undetermined).Count,(@($pf.checked) -join ','))

    # ------------------------------------------------------------------ 5. pdf print policy
    $unknown=TryTool 'horizun_export' @{target_document=$Document;format='pdf';view_ids=$sheets;output_path=(Join-Path $pdfDir 'unknown.pdf');dry_run=$true;pdf_print=@{paper_size='A1'}} 'pdf-print-unknown'
    Add-HzRefusalProbe -Run $run -Id 'pdf-print-unknown-field-refused' -Name 'pdf-print-unknown-field-refused' -Call $unknown -MustMatch 'unknown field.*paper_size'
    $def=WriteTool 'horizun_export' @{format='pdf';view_ids=@($sheets[1]);output_path=(Join-Path $pdfDir 'default.pdf');pdf_combine=$true;emit_manifest=$true;overwrite=$false;pdf_print=@{}} 'pdf-print-default'
    $defPol=$def.Apply.Result.delivery.print_policy
    $defPaper=@($defPol.options | Where-Object { $_.option -eq 'paper_format' })[0]
    $defPage=@($defPol.pages)[0]
    Record 'pdf-print-default-verified-against-sheet' ($def.Ok -and $defPol.verifiable_options_held -eq $true -and $defPaper.status -eq 'verified' -and $defPaper.source -eq 'defaulted' -and $defPage.page_verified -eq $true) 'Default paper is proved against the sheet outline read from the model; nothing requested is claimed' $def.Apply.Raw ("paper_format={0}/{1} page {2}x{3} mm expected {4}x{5} pt" -f $defPaper.source,$defPaper.status,$defPage.page_width_mm,$defPage.page_height_mm,$defPage.expected_width_points,$defPage.expected_height_points)
    $a3=WriteTool 'horizun_export' @{format='pdf';view_ids=@($sheets[1]);output_path=(Join-Path $pdfDir 'a3-landscape.pdf');pdf_combine=$true;emit_manifest=$true;overwrite=$false;pdf_print=@{paper_format='ISO_A3';orientation='landscape';color_depth='grayscale'}} 'pdf-print-a3'
    $a3Pol=$a3.Apply.Result.delivery.print_policy
    $a3Rows=@{}; foreach($o in $a3Pol.options){ $a3Rows[$o.option]=$o }
    $a3Page=@($a3Pol.pages)[0]
    Record 'pdf-print-a3-landscape-verified' ($a3.Ok -and $a3Pol.verifiable_options_held -eq $true -and $a3Rows.paper_format.status -eq 'verified' -and $a3Rows.paper_format.source -eq 'requested' -and $a3Rows.orientation.status -eq 'verified' -and $a3Rows.color_depth.status -eq 'requested_unverifiable' -and $a3Rows.color_depth.applied -eq 'grayscale' -and [Math]::Abs([double]$a3Page.page_width_mm-420) -lt 1 -and [Math]::Abs([double]$a3Page.page_height_mm-297) -lt 1) 'ISO_A3 landscape is proved from the produced page (420x297 mm); grayscale is applied but honestly unverifiable' $a3.Apply.Raw ("page {0}x{1} mm paper={2} orientation={3} color={4}/{5}" -f $a3Page.page_width_mm,$a3Page.page_height_mm,$a3Rows.paper_format.status,$a3Rows.orientation.status,$a3Rows.color_depth.applied,$a3Rows.color_depth.status)
    $comb=WriteTool 'horizun_export' @{format='pdf';view_ids=$sheets;output_path=(Join-Path $pdfDir 'combined-policy.pdf');pdf_combine=$true;emit_manifest=$true;overwrite=$false;pdf_print=@{paper_format='ISO_A3';orientation='landscape'}} 'pdf-print-combined'
    $combPages=@($comb.Apply.Result.delivery.print_policy.pages)
    Record 'pdf-print-combined-page-verdicts' ($comb.Ok -and $combPages.Count -eq 2 -and @($combPages | Where-Object { $_.page_verified -eq $true }).Count -eq 2 -and @($combPages | ForEach-Object { $_.source_view_id }) -contains $sheets[0]) 'A combined PDF carries one verdict per page, each mapped to its source sheet' $comb.Apply.Raw
    $script:renderTargets=@((Join-Path $pdfDir 'combined-policy.pdf'),(Join-Path $pdfDir 'a3-landscape.pdf'))
    # Options the page cannot testify to are still APPLIED and read back: margins
    # placement with explicit offsets (feet in the option object, from mm here).
    # Revit's Margins placement IS LowerLeft (same enum value, measured on 2026):
    # lower_left with explicit offsets (feet in the option object, from mm here).
    $clip=TryTool 'horizun_export' @{target_document=$Document;format='pdf';view_ids=@($sheets[1]);output_path=(Join-Path $pdfDir 'clipped.pdf');dry_run=$true;units='mm';pdf_print=@{paper_format='ISO_A3';orientation='landscape';placement='lower_left';origin_offset_x=10;origin_offset_y=20}} 'pdf-print-offsets-without-zoom'
    Add-HzRefusalProbe -Run $run -Id 'pdf-print-offsets-need-zoom' -Name 'pdf-print-offsets-need-zoom' -Call $clip -MustMatch 'clipped' -Expected 'Offsets with fit_to_page are refused by name (Revit fits the sheet to the whole paper and the offset pushes it off the page - rendered and measured on 2026), never exported as a clipped print'
    $margins=WriteTool 'horizun_export' @{format='pdf';view_ids=@($sheets[1]);output_path=(Join-Path $pdfDir 'margins.pdf');pdf_combine=$true;emit_manifest=$false;overwrite=$false;units='mm';pdf_print=@{paper_format='ISO_A3';orientation='landscape';placement='lower_left';zoom='zoom';zoom_percentage=35;origin_offset_x=10;origin_offset_y=20}} 'pdf-print-margins'
    $mRows=@{}; foreach($o in @($margins.Apply.Result.delivery.print_policy.options)){ $mRows[$o.option]=$o }
    $offX=[double]$mRows.origin_offset_x.applied*304.8; $offY=[double]$mRows.origin_offset_y.applied*304.8
    Record 'pdf-print-margins-offsets-applied' ($margins.Ok -and $mRows.placement.applied -eq 'lower_left' -and $mRows.placement.status -eq 'requested_unverifiable' -and [Math]::Abs($offX-10) -lt 0.01 -and [Math]::Abs($offY-20) -lt 0.01 -and $mRows.origin_offset_x.status -eq 'requested_unverifiable' -and [int]$mRows.zoom_percentage.applied -eq 35 -and $mRows.paper_format.status -eq 'verified') 'placement=lower_left with 10/20 mm offsets beside an explicit zoom of 35% is applied (offsets and zoom read back from the option object) and honestly reported unverifiable; the paper is still proved. Offsets with fit_to_page are refused: measured to clip the print' $margins.Apply.Raw ("placement={0} x={1}mm y={2}mm" -f $mRows.placement.applied,[Math]::Round($offX,3),[Math]::Round($offY,3))
    # export_in_background=true is refused on EVERY year (a background export returns
    # before the file exists, measured on 2026); on 2023/2024 the option does not exist.
    $hostYear=[int]$h.revit_version
    $bg=TryTool 'horizun_export' @{target_document=$Document;format='pdf';view_ids=@($sheets[1]);output_path=(Join-Path $pdfDir 'background.pdf');pdf_combine=$true;emit_manifest=$false;overwrite=$false;dry_run=$true;pdf_print=@{paper_format='ISO_A3';orientation='landscape';export_in_background=$true}} 'pdf-print-background'
    $bgPattern=if($hostYear -ge 2025){ 'before the file exists' } else { '2025' }
    Add-HzRefusalProbe -Run $run -Id 'pdf-print-export-in-background-by-year' -Name 'pdf-print-export-in-background-by-year' -Call $bg -MustMatch $bgPattern -Expected ("Revit ${hostYear}: export_in_background=true is refused by name (" + $bgPattern + "), never dropped and never reported as a file that is not there")
    # fits_titleblock_cell over the sheet NAME.
    $nameRules=@{requirement_set=@{id='cells-name';version='1'};rules=@(@{id='name-fits';entity='sheet';selector=@{applies_to_all=$true};assertion=@{operator='fits_titleblock_cell';value=@{field='name';cell_width=20;text_height=3}}})}
    $nameAudit=ReadTool 'horizun_audit_planimetry' @{scope='sheets';sheet_ids=$sheets;requirement_set=$nameRules;units='mm'} 'cell-fit-name-audit'
    $nameFindings=@($nameAudit.Result.findings | Where-Object { (Get-HzProp $_ 'rule_id') -eq 'name-fits' -and $_.status -eq 'failed' })
    Record 'cell-fit-name-field' ($nameFindings.Count -eq 2 -and @($nameFindings | Where-Object { $_.observed.field -eq 'name' -and [double]$_.observed.estimated_text_width -gt 20 }).Count -eq 2) 'Both 15-character names at 3 mm text overflow a 20 mm cell (27 mm estimated) and are findings on the name field' $nameAudit.Raw ("findings={0}" -f $nameFindings.Count)
    # Inside the paper is not inside the titleblock: with Default paper the
    # overflowing sheet number enlarges the page beyond the ARCH E1 titleblock
    # (measured 70.8 mm on c1). The export must refuse naming the composition
    # defect; the file may exist and the reply must say so.
    $overflow=WriteTool 'horizun_export' @{format='pdf';view_ids=@($sheets[0]);output_path=(Join-Path $pdfDir 'default-overflow.pdf');pdf_combine=$true;emit_manifest=$false;overwrite=$false;pdf_print=@{}} 'pdf-print-default-overflow' -AllowRefusal
    $ovCall=if($overflow.Apply){$overflow.Apply}else{$overflow.Dry}
    $ovText=[string]$ovCall.Text
    Record 'pdf-print-default-refuses-page-larger-than-titleblock' ((-not $overflow.Ok) -and $ovText -match 'larger than the titleblock' -and $ovText -match 'external_files_may_exist|may exist|PDF package verification failed') 'A Default-paper page larger than its titleblock is a composition defect: the export fails naming it and admits the file may exist' $ovCall.Raw (Limit-HzText $ovText 260)

    # ------------------------------------------------------------------ 6. ledger
    # Recovery across runs: the ledger a PREVIOUS run left on disk is resumed
    # against the document as it stands now. A fresh fixture no longer holds the
    # recorded elements, so the expected answer is invalidation with the reason
    # (or a document mismatch when another model is active) - never a silent
    # "all current".
    $previousIdFile=Join-Path $ArtifactDir 'last-delivery-id.txt'
    if(Test-Path -LiteralPath $previousIdFile) {
        $previousId=(Get-Content -LiteralPath $previousIdFile -Raw).Trim()
        $prev=TryTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$previousId} 'ledger-previous-run'
        $prevRev=@(Get-HzProp $prev.Result 'reverification')
        $prevMismatch=[string](Get-HzProp $prev.Result 'document_mismatch')
        $prevStale=@($prevRev | Where-Object { (Get-HzProp $_ 'scope_current') -eq $false })
        Record 'ledger-previous-run-recovery' ($prev.Ok -and (($prevMismatch -ne '') -or ($prevRev.Count -ge 1 -and $prevStale.Count -ge 1))) 'A ledger left by an earlier run replays from disk and reports what no longer holds (stale scopes invalidated with reasons, or a document mismatch)' $prev.Raw ("id={0} reverified={1} stale={2} mismatch={3}" -f $previousId,$prevRev.Count,$prevStale.Count,($prevMismatch -ne ''))
    } else {
        Add-HzProbe -Run $run -Id 'ledger-previous-run-recovery' -Name 'ledger-previous-run-recovery' -Expected 'Resume a ledger left by an earlier run' -Status 'not_covered' -Because 'First run in this artifact directory; the next run resumes the ledger this one leaves.'
    }
    $deliveryId="stab-$suffix"
    Set-Content -LiteralPath $previousIdFile -Value $deliveryId -Encoding utf8
    $open=ReadTool 'horizun_plan_views' @{operation='delivery_open';delivery_profile=$profile;delivery_id=$deliveryId} 'ledger-open'
    $o=$open.Result
    Record 'ledger-open' ($o.delivery_id -eq $deliveryId -and (Test-Path $o.ledger_file) -and $o.resume.next_stage.key -eq ("view_{0}" -f $tag.owner_view_id) -and $o.resume.publish_gate.open -eq $false -and $o.document.fingerprint) 'Opening writes the ledger file, names the first stage and keeps the gate shut' $open.Raw ("next={0} file={1}" -f $o.resume.next_stage.key,$o.ledger_file)
    $viewKey="view_{0}" -f $tag.owner_view_id; $tagsKey="tags_{0}" -f $tag.owner_view_id; $capKey="capture_view_{0}" -f $tag.owner_view_id
    $ahead=TryTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='in_progress'} 'ledger-ahead'
    Add-HzRefusalProbe -Run $run -Id 'ledger-order-enforced' -Name 'ledger-order-enforced' -Call $ahead -MustMatch "depends on '$viewKey'"
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$viewKey;status='in_progress'} 'ledger-view-start'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$viewKey;status='completed'} 'ledger-view-done'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='in_progress'} 'ledger-tags-start'
    $noKey=TryTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='completed';facts=@{element_ids=$script:coverageTagIds}} 'ledger-tags-nokey'
    $rec=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='completed';facts=@{idempotency_key='k-tags';element_ids=$script:coverageTagIds}} 'ledger-tags-done'
    $st=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-1'
    $rv=@($st.Result.resume.needs_reverification)
    Record 'ledger-record-write-reads-scope' ($noKey.IsError -and $noKey.Text -match 'idempotency_key' -and $rec.Ok -and $rv.Count -eq 1 -and $rv[0].key -eq $tagsKey -and @($rv[0].scope).Count -eq @($script:coverageTagIds).Count -and $rv[0].scope[0].version_guid -and @($st.Result.resume.completed_writes)[0].never_replay -eq $true) 'A completed write without its key is refused; with it the host stores each element VersionGuid and resume lists the write as never-replay' @{nokey=$noKey.Raw;rec=$rec.Raw;status=$st.Raw}
    # The model moves under a completed stage: the recorded tag is DELETED (a typed,
    # verified deletion - pinning was measured NOT to change a tag's VersionGuid on
    # this fixture, and a tag has no location sample so 'move' is refused by
    # design). status must invalidate the write with the reason.
    $moved=WriteTool 'horizun_delete_verified' @{mode='ids';ids=$script:coverageTagIds} 'ledger-delete-tag' -AllowRefusal
    $st2=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-2'
    $rv2=@($st2.Result.reverification)
    $inv=@($rv2 | Where-Object { $_.key -eq $tagsKey })
    $invReason=if($inv.Count -gt 0){[string](Get-HzProp $inv[0] 'reason')}else{''}
    $invCurrent=if($inv.Count -gt 0){Get-HzProp $inv[0] 'scope_current'}else{$null}
    Record 'ledger-status-detects-change' ($moved.Ok -and $inv.Count -eq 1 -and $invCurrent -eq $false -and $invReason -match 'no longer exists|changed' -and (@($st2.Result.stages | Where-Object { $_.key -eq $tagsKey })[0].status -eq 'invalidated') -and @($st2.Result.resume.needs_attention).Count -ge 1) 'Deleting a recorded element is detected at status: the write stage is invalidated with the reason and listed for attention' @{delete=$moved.Apply.Raw;status=$st2.Raw} ("scope_current={0} reason={1}" -f $invCurrent,(Limit-HzText $invReason 160))
    $closed=TryTool 'horizun_export' @{target_document=$Document;format='pdf';view_ids=$sheets;output_path=(Join-Path $pdfDir 'ledger.pdf');pdf_combine=$true;emit_manifest=$true;overwrite=$false;delivery_id=$deliveryId;dry_run=$true;pdf_print=@{paper_format='ISO_A3';orientation='landscape'}} 'ledger-export-closed'
    Add-HzRefusalProbe -Run $run -Id 'ledger-gate-closed-refuses-export' -Name 'ledger-gate-closed-refuses-export' -Call $closed -MustMatch 'publish gate is closed'
    # Re-arm and drive the rest of the delivery through the ledger.
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='pending'} 'ledger-tags-rearm'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='in_progress'} 'ledger-tags-start-2'
    # The deleted tag cannot be recorded again (the host re-reads scopes); the
    # stage is redone with a FRESH rehearsal and the new ids are what gets recorded.
    $replan=ReadTool 'horizun_plan_annotations' @{operation='auto_tags';view_id=[long]$tag.owner_view_id;tag_type_id=[long]$tag.type_id;tag_mode='by_category';element_ids=@([long]$tag.tagged_element_ids[0]);skip_existing=$false;add_leader=$false;units='mm';distance_space='paper';clearance=1;max_displacement=50} 'ledger-tag-replan'
    $reArgs=$replan.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
    $reWrite=WriteTool 'horizun_annotate' $reArgs 'ledger-tag-recreate'
    $newTagIds=@($reWrite.Apply.Result.rows | ForEach-Object {[long]$_.element_id})
    $stale=TryTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='completed';facts=@{idempotency_key='k-tags-2';element_ids=$script:coverageTagIds}} 'ledger-tags-stale-ids'
    if(-not ($stale.IsError -and $stale.Text -match 'do not exist')) { throw 'HARNESS: recording deleted ids as a completed write was not refused.' }
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$tagsKey;status='completed';facts=@{idempotency_key='k-tags-2';element_ids=$newTagIds}} 'ledger-tags-done-2'
    $cap=ReadTool 'horizun_capture_view' @{view_id=[long]$tag.owner_view_id;pixel_size=800} 'ledger-capture-view'
    $capPath=[string](Get-HzProp $cap.Result 'image_path')
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$capKey;status='in_progress'} 'ledger-cap-start'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$capKey;status='completed';facts=@{files=@($capPath)}} 'ledger-cap-done'
    $pack=WriteTool 'horizun_pack_sheets' @{units='mm';sheets=@(@{sheet_id=$sheets[0];usable_rect=@(30,30,400,250)},@{sheet_id=$sheets[1];usable_rect=@(30,30,400,250)});margin=5;gap=5;items=@(@{key='a';view_id=$views[0]})} 'ledger-pack'
    # Multi-sheet packing applies through the atomic plan: viewports come back per
    # action under actions[].data.rows[].element_id.
    $packIds=@(); foreach($act in @(Get-HzProp $pack.Apply.Result 'actions')) { foreach($r in @(Get-HzProp (Get-HzProp $act 'data') 'rows')) { $id=[long](Get-HzProp $r 'element_id'); if($id -gt 0){ $packIds+=$id } } }
    if($packIds.Count -eq 0) {
        $placed=ReadTool 'horizun_query_planimetry' @{mode='placements';sheet_ids=$sheets;units='mm'} 'ledger-pack-placements'
        $packIds=@($placed.Result.rows | Where-Object { $_.view_id -in $views } | ForEach-Object { [long]$_.element_id })
    }
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='pack';status='in_progress'} 'ledger-pack-start'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='pack';status='completed';facts=@{idempotency_key='k-pack';element_ids=$packIds}} 'ledger-pack-done'
    $aud=ReadTool 'horizun_audit_planimetry' @{scope='sheets';sheet_ids=$sheets;requirement_set=$profile.requirement_set;units='mm'} 'ledger-audit'
    $blocking=@($aud.Result.findings | Where-Object { $_.status -eq 'failed' -and $_.severity -eq 'blocking' }).Count
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='audit';status='in_progress'} 'ledger-audit-start'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='audit';status='completed';facts=@{no_blocking_findings=($blocking -eq 0);finding_set_fingerprint=[string](Get-HzProp $aud.Result 'finding_set_fingerprint')}} 'ledger-audit-done'
    foreach($s in $sheets) {
        $k="capture_sheet_$s"
        $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$k;status='in_progress'} "ledger-$k-start"
        $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$k;status='awaiting_approval'} "ledger-$k-await"
    }
    $anon=TryTool 'horizun_plan_views' @{operation='delivery_approve';delivery_id=$deliveryId;stage_key="capture_sheet_$($sheets[0])";identity='';decision='approved'} 'ledger-approve-anon'
    $ap=ReadTool 'horizun_plan_views' @{operation='delivery_approve';delivery_id=$deliveryId;stage_key="capture_sheet_$($sheets[0])";identity='harness-reviewer';decision='approved';note='fixture geometry; not a production approval'} 'ledger-approve-1'
    $stage0=@($ap.Result.stages | Where-Object { $_.key -eq "capture_sheet_$($sheets[0])" })[0]
    $rvA=@($ap.Result.resume.needs_reverification | Where-Object { $_.key -eq "capture_sheet_$($sheets[0])" })
    Record 'ledger-approve-binds-scope' ($anon.IsError -and $anon.Text -match 'identity' -and $ap.Ok -and $stage0.status -eq 'approved' -and $rvA.Count -eq 1 -and @($rvA[0].scope).Count -ge 2) 'An anonymous approval is refused; a named one binds the sheet and its placements (VersionGuids) so a later change invalidates it' @{anon=$anon.Raw;approve=$ap.Raw} ("scope entries={0}" -f @($rvA[0].scope).Count)
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_approve';delivery_id=$deliveryId;stage_key="capture_sheet_$($sheets[1])";identity='harness-reviewer';decision='approved';note='fixture geometry; not a production approval'} 'ledger-approve-2'
    # Persistence and recovery of approvals: with the gate OPEN, the model moves
    # under an approved sheet (its packed viewport is deleted); status must
    # invalidate that approval and close the gate; a fresh approval reopens it.
    $stOpen=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-open-before-change'
    $sheet1Key="capture_sheet_$($sheets[0])"
    $vpDel=WriteTool 'horizun_delete_verified' @{mode='ids';ids=@([long]$packIds[0])} 'ledger-delete-viewport' -AllowRefusal
    $stInv=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-after-change'
    $invEntry=@($stInv.Result.reverification | Where-Object { $_.key -eq $sheet1Key })
    $invStage=@($stInv.Result.stages | Where-Object { $_.key -eq $sheet1Key })[0]
    Record 'ledger-approval-invalidated-by-scope-change' ($stOpen.Result.resume.publish_gate.open -eq $true -and $vpDel.Ok -and $invEntry.Count -eq 1 -and (Get-HzProp $invEntry[0] 'scope_current') -eq $false -and $invStage.status -eq 'invalidated' -and $stInv.Result.resume.publish_gate.open -eq $false) 'An open gate closes when an approved sheet loses a placement: the approval is invalidated with the reason and must be given again' @{before=$stOpen.Raw;delete=$vpDel.Apply.Raw;after=$stInv.Raw} ("gate_before={0} gate_after={1} stage={2}" -f $stOpen.Result.resume.publish_gate.open,$stInv.Result.resume.publish_gate.open,$invStage.status)
    # The deleted viewport was in the PACK stage's scope, so the invalidation
    # cascaded through pack -> audit -> both sheet approvals. Recovery is the real
    # thing: re-arm the packing, pack again (a NEW viewport), record its ids,
    # re-audit, then capture and approve both sheets afresh.
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='pack';status='pending'} 'ledger-pack-rearm'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='pack';status='in_progress'} 'ledger-pack-start-2'
    $pack2=WriteTool 'horizun_pack_sheets' @{units='mm';sheets=@(@{sheet_id=$sheets[0];usable_rect=@(30,30,400,250)},@{sheet_id=$sheets[1];usable_rect=@(30,30,400,250)});margin=5;gap=5;items=@(@{key='a';view_id=$views[0]})} 'ledger-pack-2'
    $packIds2=@(); foreach($act in @(Get-HzProp $pack2.Apply.Result 'actions')) { foreach($r in @(Get-HzProp (Get-HzProp $act 'data') 'rows')) { $id=[long](Get-HzProp $r 'element_id'); if($id -gt 0){ $packIds2+=$id } } }
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='pack';status='completed';facts=@{idempotency_key='k-pack-2';element_ids=$packIds2}} 'ledger-pack-done-2'
    $aud2=ReadTool 'horizun_audit_planimetry' @{scope='sheets';sheet_ids=$sheets;requirement_set=$profile.requirement_set;units='mm'} 'ledger-audit-2'
    $blocking2=@($aud2.Result.findings | Where-Object { $_.status -eq 'failed' -and $_.severity -eq 'blocking' }).Count
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='audit';status='pending'} 'ledger-audit-rearm'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='audit';status='in_progress'} 'ledger-audit-start-2'
    $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key='audit';status='completed';facts=@{no_blocking_findings=($blocking2 -eq 0);finding_set_fingerprint=[string](Get-HzProp $aud2.Result 'finding_set_fingerprint')}} 'ledger-audit-done-2'
    foreach($s in $sheets) {
        $k="capture_sheet_$s"
        $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$k;status='pending'} "ledger-$k-rearm"
        $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$k;status='in_progress'} "ledger-$k-start-2"
        $null=ReadTool 'horizun_plan_views' @{operation='delivery_record';delivery_id=$deliveryId;stage_key=$k;status='awaiting_approval'} "ledger-$k-await-2"
        $null=ReadTool 'horizun_plan_views' @{operation='delivery_approve';delivery_id=$deliveryId;stage_key=$k;identity='harness-reviewer';decision='approved';note='re-approved after the packing was redone'} "ledger-$k-approve-2"
    }
    # The ledger file is the record: a second open under the same id is refused,
    # and a corrupt line is reported by replay, never skipped in silence.
    $twice=TryTool 'horizun_plan_views' @{operation='delivery_open';delivery_profile=$profile;delivery_id=$deliveryId} 'ledger-open-twice'
    Add-HzRefusalProbe -Run $run -Id 'ledger-open-twice-refused' -Name 'ledger-open-twice-refused' -Call $twice -MustMatch 'already exists'
    $ledgerFile=[string]$o.ledger_file
    Add-Content -LiteralPath $ledgerFile -Value '{not json - a corrupt line appended by the harness}' -Encoding utf8
    $stCorrupt=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-corrupt'
    $problems=@($stCorrupt.Result.replay_problems)
    Record 'ledger-corrupt-line-reported' ($problems.Count -eq 1 -and ([string]$problems[0].problem) -match 'unparseable' -and $stCorrupt.Result.resume.publish_gate.open -eq $true) 'A corrupt event line is reported in replay_problems with its line number; the stages before it still replay and the gate keeps its state' $stCorrupt.Raw ("problems={0}" -f $problems.Count)
    $stFinal=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-gate'
    $gateOpen=$stFinal.Result.resume.publish_gate.open -eq $true
    $pub=WriteTool 'horizun_export' @{format='pdf';view_ids=$sheets;output_path=(Join-Path $pdfDir 'ledger.pdf');pdf_combine=$true;emit_manifest=$true;overwrite=$false;delivery_id=$deliveryId;pdf_print=@{paper_format='ISO_A3';orientation='landscape'}} 'ledger-export-open' -AllowRefusal
    $led=$pub.Apply.Result.delivery_ledger
    $stAfter=ReadTool 'horizun_plan_views' @{operation='delivery_status';delivery_id=$deliveryId} 'ledger-status-after'
    $pubStage=@($stAfter.Result.stages | Where-Object { $_.key -eq 'publish' })[0]
    Record 'ledger-gate-open-exports-and-records' ($gateOpen -and $pub.Ok -and $led.publish_gate_at_export.open -eq $true -and $pubStage.status -eq 'completed' -and $stAfter.Result.resume.publish_gate.open -eq $false -and (Test-Path (Join-Path $pdfDir 'ledger.pdf'))) 'With every stage done and every sheet approved the gate opens, the export runs, the publish stage is recorded with hashes, and the gate closes again' @{status=$stFinal.Raw;export=$pub.Apply.Raw;after=$stAfter.Raw} ("gate_before={0} publish={1} gate_after={2}" -f $gateOpen,$pubStage.status,$stAfter.Result.resume.publish_gate.open)
    $script:renderTargets+=(Join-Path $pdfDir 'ledger.pdf')
    Add-HzProbe -Run $run -Id 'visual-acceptance-rendered-pages' -Name 'Visual acceptance of rendered PDF pages' -Expected 'Rendered PDF pages reviewed by a person/agent for title, scale and legibility' -Status 'not_covered' -Because ('Render ' + ($script:renderTargets -join '; ') + ' and review; this harness measures files, never legibility.')
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
