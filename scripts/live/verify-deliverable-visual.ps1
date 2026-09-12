#Requires -Version 7.0
<#
  ONE SHEET CARRYING THE TWO THINGS A READBACK CANNOT JUDGE: a dimension whose
  text was deliberately DISPLACED, and a tag with a LEADER reaching its host.

  Everything else in the deliverable harnesses re-reads coordinates and calls it
  verified, which is right and is not the same as knowing the page is legible.
  The 2026-09-08 review left that gap open by name: "a sheet of the harness that
  places the view with the displaced dimension and the tag with a leader, to look
  at them". This builds exactly that, in a disposable fixture, and then EXPORTS
  ONE PDF PAGE and writes visual-request.json - what was asked for beside what
  was re-read - so the person or agent rendering the page has something to
  compare it against.

  It never claims the visual verdict. The last probe is declared not_covered and
  names the file to render: legibility is a judgement, and a harness that scores
  it would be scoring its own request.

  Nothing is saved. The document must be a disposable fixture.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][ValidateSet('yes-this-model-is-disposable')][string]$Disposable,
    [Parameter(Mandatory)][string]$ExpectedAddinSha256,
    [Parameter(Mandatory)][string]$ArtifactDir,
    # Paper millimetres, along the view's up axis. Big enough to SEE in a render;
    # the whole point of the page is that a person can tell it moved.
    [double]$TextOffsetMm = 12,
    [int]$ViewScale = 100
)
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'deliverable-visual' -Document $Document -WorkDir (Join-Path $ArtifactDir ('visual-' + [guid]::NewGuid().ToString('N')))
$expectedCases = @('fixture-annotated-plan', 'dimension-text-displaced', 'tag-with-leader',
    'view-cropped-for-paper', 'view-placed-on-sheet', 'sheet-exported-to-pdf', 'visual-acceptance-rendered-page')
function Record([string]$Id, [bool]$Ok, [string]$Expected, $Evidence, [string]$Observed = '') {
    Add-HzProbe -Run $run -Id $Id -Name $Id -Expected $Expected -Ok $Ok -Evidence $Evidence -Observed $Observed
}
function ReadTool([string]$Tool, [hashtable]$Arguments, [string]$Label) {
    Invoke-HzToolStrict -Run $run -Tool $Tool -Arguments $Arguments -Label $Label
}
function WriteTool([string]$Tool, [hashtable]$Arguments, [string]$Label) {
    $Arguments['target_document'] = $Document
    Invoke-HzWrite -Run $run -Tool $Tool -Arguments $Arguments -Label $Label
}
# What the page is SUPPOSED to show, written down before it is built.
$request = [ordered]@{
    document = $Document
    view_scale = $ViewScale
    text_offset_paper_mm = @(0, $TextOffsetMm)
    wall_mm = @{ start = @(0, 0); end = @(6000, 0); height = 3000 }
    grids_mm = @(0, 6000)
    crop_box_mm = @(-3000, -5000, 9000, 5000)
}
try {
    $h = Get-HzHealth $run
    if ($h.status -ne 'healthy' -or $h.active_document.title -ne $Document -or $h.addin_assembly.sha256 -ne $ExpectedAddinSha256) {
        throw 'Candidate/document gate refused. Nothing was authored.'
    }
    $suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $request['sheet_number'] = "HZV-$suffix"

    # ---- the fixture: one plan, one wall, two grids -------------------------
    $levels = ReadTool 'horizun_query_model' @{ categories = @('OST_Levels'); max_rows = 1 } 'level'
    $levelId = [long]@($levels.Result.rows)[0].element_id
    # THE TYPE, NOT AN INSTANCE OF IT. A titleblock type is loaded in a document
    # long before any sheet carries one, and asking for instances reports "no
    # titleblock" about a document that has the family (measured on a fixture
    # derived from Autodesk's own template, 2026-09-09). Instances are the
    # fallback, for a model that has them and whose types the query cannot list.
    $titleBlockTypeId = 0
    $tbTypes = @((ReadTool 'horizun_query_model' @{ categories = @('OST_TitleBlocks'); include_types = $true; max_rows = 200 } 'titleblock-types').Result.rows |
        Where-Object { (Get-HzProp $_ 'is_element_type') -eq $true })
    if ($tbTypes.Count -gt 0) { $titleBlockTypeId = [long](Get-HzProp $tbTypes[0] 'element_id') }
    else {
        $tbInstances = @((ReadTool 'horizun_query_model' @{ categories = @('OST_TitleBlocks'); fields = @('type_id'); max_rows = 100 } 'titleblock-instances').Result.rows |
            Where-Object { (Get-HzProp $_ 'type_id') -gt 0 })
        if ($tbInstances.Count -gt 0) { $titleBlockTypeId = [long](Get-HzProp $tbInstances[0] 'type_id') }
    }
    if ($titleBlockTypeId -le 0) { throw 'Fixture missing: this document has no titleblock type, loaded or placed.' }
    $request['title_block_type_id'] = $titleBlockTypeId
    # The scale goes on at creation: create_floor_plan is one of the operations
    # that accept view_scale, and the writer re-reads it (view_scale_verified).
    # THE VIEW TITLE IS ALSO A FIELD WITH A SIZE, and a smaller one than it looks.
    # It wraps at about 96 mm, and Revit's own title family puts the scale 0.52 mm
    # under the title's box - measured identically on all five years' pages. So a
    # second line does not spill into free paper: it lands on '1 : 100'.
    # This was tried, on 2026-09-09: moving the long description here to keep it on
    # the page cleared the titleblock and put THREE new collisions on the scale, in
    # all five years. The description is not page content - it is the run's own
    # account of itself, and it is legible in visual-request.json, in the probe ids
    # and in the acceptance record, none of which has to fit in 96 mm.
    $viewName = "HZV-$suffix-plan"
    $request['view_name'] = $viewName
    $plan = WriteTool 'horizun_manage_views' @{ units = 'mm'; actions = @(
            @{ operation = 'create_floor_plan'; key = 'p'; name = $viewName; level_id = $levelId; view_scale = $ViewScale }) } 'fixture-plan'
    $viewId = [long]$plan.Apply.Result.aliases.p
    $null = ReadTool 'horizun_navigate' @{ operation = 'open_view'; view_id = $viewId } 'activate-plan'
    # The wall is built on a NAMED type, so its id is known here and its Type Mark
    # can be given a value below: a tag whose label resolves to nothing is refused
    # by the planner, correctly, for having no readable text.
    $wallTypes = @((ReadTool 'horizun_query_model' @{ categories = @('OST_Walls'); include_types = $true; max_rows = 400 } 'wall-types').Result.rows |
        Where-Object { $_.is_element_type -eq $true })
    if ($wallTypes.Count -lt 1) { throw 'Fixture missing: this document has no wall type to build with.' }
    $wallTypeId = [long]$wallTypes[0].element_id
    $request['wall_type_id'] = $wallTypeId
    $made = WriteTool 'horizun_create_elements' @{ units = 'mm'; elements = @(
            @{ kind = 'wall'; start = @(0, 0, 0); end = @(6000, 0, 0); level_id = $levelId; height = 3000; type_id = $wallTypeId },
            @{ kind = 'grid'; name = "HZV-$suffix-A"; start = @(0, -3000, 0); end = @(0, 3000, 0) },
            @{ kind = 'grid'; name = "HZV-$suffix-B"; start = @(6000, -3000, 0); end = @(6000, 3000, 0) }) } 'fixture-elements'
    $madeRows = @($made.Apply.Result.rows)
    $wallId = [long]$madeRows[0].element_id
    $gridIds = @($madeRows[1..2] | ForEach-Object { [long]$_.element_id })
    Record 'fixture-annotated-plan' ($made.Ok -and $madeRows.Count -eq 3 -and $wallId -gt 0) `
        'A new uncropped plan with one wall and two grids, all committed and re-read by the writer' `
        @{ plan = $plan.Apply.Raw; elements = $made.Apply.Raw }
    $request['wall_id'] = $wallId
    $request['grid_ids'] = $gridIds
    $request['view_id'] = $viewId
    # WHERE THE FIXTURE ACTUALLY IS, in model millimetres. The crop box below is
    # expressed in the VIEW's own right/up plane anchored at the view origin, and
    # the two frames coincide only when that origin is the model origin. Recording
    # the wall's real box means an empty page can be explained by arithmetic rather
    # than by another run.
    $wallBox = @((ReadTool 'horizun_query_model' @{ categories = @('OST_Walls'); include_bounding_box = $true
            bounding_box = @{ min = @(-20000, -20000, -20000); max = @(20000, 20000, 20000); units = 'mm' }
            max_rows = 50 } 'wall-box').Result.rows | Where-Object { [long]$_.element_id -eq $wallId })
    if ($wallBox.Count -eq 1) { $request['wall_bounding_box_model_mm'] = (Get-HzProp $wallBox[0] 'bounding_box') }

    # ---- the dimension, then its text pushed off the line -------------------
    # A DIMENSION TYPE IS NOT A DIMENSION. query_dimensions lists the dimensions a
    # document already has, so on a fixture that has none - which is every clean
    # one - this used to fail on an empty row set. The types are elements of the
    # category; the old route stays as a fallback for a document that has both.
    $dimTypeId = 0
    $dimTypeRows = @((ReadTool 'horizun_query_model' @{ categories = @('OST_Dimensions'); include_types = $true; max_rows = 200 } 'dimension-types').Result.rows |
        Where-Object { (Get-HzProp $_ 'is_element_type') -eq $true })
    if ($dimTypeRows.Count -gt 0) { $dimTypeId = [long](Get-HzProp $dimTypeRows[0] 'element_id') }
    else {
        $dimTypes = ReadTool 'horizun_query_dimensions' @{ shapes = @('linear'); max_rows = 1 } 'linear-dimension-type'
        $existing = @($dimTypes.Result.rows)
        if ($existing.Count -gt 0) { $dimTypeId = [long]$existing[0].type.id }
    }
    # NOT A REFUSAL WHEN IT IS UNKNOWN. horizun_annotate resolves the document's
    # own default dimension type, and falls back to any type of the right style,
    # so the id is passed only when this harness could actually find one.
    $request['dimension_type_id'] = $dimTypeId
    # intent_dimension rather than dimension_set: a set REQUIRES a
    # dimension_type_id and this harness must work on a fixture whose types it
    # cannot enumerate. One dimension is all this page needs.
    $dimPlanArgs = @{ operation = 'intent_dimension'; view_id = $viewId; units = 'mm'
        distance_space = 'paper'; element_ids = $gridIds; selector = 'grid'
        offset = 10; side = 'positive' }
    if ($dimTypeId -gt 0) { $dimPlanArgs['dimension_type_id'] = $dimTypeId }
    $planned = ReadTool 'horizun_plan_annotations' $dimPlanArgs 'dimension-plan'
    $dimArgs = $planned.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
    $dim = WriteTool 'horizun_annotate' $dimArgs 'dimension-apply'
    $dimId = [long]@($dim.Apply.Result.rows)[0].element_id
    $request['dimension_id'] = $dimId
    $off = WriteTool 'horizun_edit_dimensions' @{ units = 'mm'; actions = @(
            @{ element_id = $dimId; text_offset = @(0, $TextOffsetMm); distance_space = 'paper' }) } 'dimension-text-offset'
    $offRow = @($off.Apply.Result.rows)[0]
    $offField = @($offRow.fields | Where-Object { $_.field -eq 'text_position' })[0]
    # The writer's own field record: what was asked for, what was there before, and
    # what it re-read afterwards. (There is no 'after' field - the names are
    # requested_feet / before_feet / read_feet, and reading one that does not exist
    # is how the first live run of this harness ended.)
    $request['dimension_text_position'] = [ordered]@{
        requested_feet = (Get-HzProp $offField 'requested_feet')
        before_feet = (Get-HzProp $offField 'before_feet')
        read_feet = (Get-HzProp $offField 'read_feet')
        tolerance_feet = (Get-HzProp $offField 'tolerance_feet')
        match = (Get-HzProp $offField 'match')
    }
    $dimRead = ReadTool 'horizun_query_dimensions' @{ element_ids = @($dimId); units = 'mm' } 'dimension-reread'
    $dimRow = @($dimRead.Result.rows)[0]
    $request['dimension_value_mm'] = [double]$dimRow.value_internal_feet * 304.8
    Record 'dimension-text-displaced' ($dim.Ok -and $off.Ok -and $offRow.verified -eq $true -and $offField.match -eq $true -and
        [Math]::Abs([double]$request['dimension_value_mm'] - 6000) -lt 0.001 -and [int]$dimRow.broken_references -eq 0) `
        ("A 6000 mm grid dimension whose text is moved $TextOffsetMm mm of PAPER off the line, re-read and matched by the writer") `
        @{ plan = $planned.Raw; write = $dim.Apply.Raw; offset = $off.Apply.Raw; read = $dimRead.Raw } `
        ("value=" + [string]$request['dimension_value_mm'] + "mm, text_position match=" + [string]$offField.match)

    # ---- the tag, with a leader that has to reach the wall ------------------
    # A TAG FAMILY THAT CAN TAG A WALL - a wall tag if the document has one, else a
    # multi-category tag. The type an existing tag happens to use is NOT a
    # candidate: the first readable tag in this fixture is a Mechanical Equipment
    # Tag, and a category tag cannot tag a wall. The rehearsal refused it, which is
    # the bridge being right and the harness being wrong (measured 2026-09-09).
    $tagTypeId = $null; $tagCategory = $null
    foreach ($cat in 'OST_WallTags', 'OST_MultiCategoryTags') {
        $rows = @((ReadTool 'horizun_query_model' @{ categories = @($cat); include_types = $true; max_rows = 200 } ("tag-types-" + $cat)).Result.rows |
            Where-Object { $_.is_element_type -eq $true })
        if ($rows.Count -gt 0) { $tagTypeId = [long]$rows[0].element_id; $tagCategory = $cat; break }
    }
    # A DOCUMENT WITHOUT A WALL-CAPABLE TAG FAMILY IS A FIXTURE GAP, NOT A FAILURE
    # AND NOT AN INTERRUPTION. Measured on a 2023 fixture: zero wall tag types and
    # zero multi-category tag types. The rest of the page is still worth producing
    # and looking at, so the tag stage is recorded as fixture_missing by name and
    # the run goes on with no tag.
    $tagIds = @()
    $request['tag_type_id'] = $tagTypeId
    $request['tag_category'] = $tagCategory
    if (-not $tagTypeId) {
        Add-HzProbe -Run $run -Id 'tag-with-leader' -Name 'tag-with-leader' `
            -Expected 'One tag on the wall, with a leader whose end can be seen reaching its host' `
            -Status 'fixture_missing' `
            -Because ('this document has no wall tag family and no multi-category tag family loaded, so no tag can be ' +
                      'placed on a wall here. A tag family cannot be made by this bridge: the Revit API offers no way ' +
                      'to create a label inside an annotation family, and extracting one from a project template needs ' +
                      'horizun_execute_python, which is disabled on this machine by its owner.') `
            -Evidence @{ wall_tag_types = 0; multi_category_tag_types = 0 }
    }
    if ($tagTypeId) {
    # Something for the label to show. Both candidate families label Type Mark, and
    # writing it on the wall's TYPE is a change to this disposable fixture only -
    # nothing here is ever saved.
    $mark = WriteTool 'horizun_write_params_verified' @{ writes = @(
            @{ target_id = $wallTypeId; parameter = 'Type Mark'; value = ("HZV-" + $suffix.Substring(0, 4)) }) } 'wall-type-mark'
    $request['wall_type_mark'] = ("HZV-" + $suffix.Substring(0, 4))
    $request['wall_type_mark_written'] = $mark.Ok
    # THE MODE FOLLOWS THE FAMILY. Revit's own refusal for a multi-category family
    # under by_category is "There is no loaded tag type that can be used when
    # tagging referenceToTag with tagMode" (measured 2026-09-09): a Multi-Category
    # tag is created with TM_ADDBY_MULTICATEGORY, which is tag_mode='multi_category'.
    $tagMode = if ($tagCategory -eq 'OST_MultiCategoryTags') { 'multi_category' } else { 'by_category' }
    $request['tag_mode'] = $tagMode
    $tagPlan = ReadTool 'horizun_plan_annotations' @{ operation = 'auto_tags'; view_id = $viewId; tag_type_id = $tagTypeId
        tag_mode = $tagMode; element_ids = @($wallId); skip_existing = $false; add_leader = $true
        units = 'mm'; distance_space = 'paper'; clearance = 1; max_displacement = 60 } 'tag-plan'
    $tagArgs = $tagPlan.Result.next_arguments | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
    $tagWrite = WriteTool 'horizun_annotate' $tagArgs 'tag-apply'
    $tagIds = @($tagWrite.Apply.Result.rows | ForEach-Object { [long]$_.element_id })
    $tagRead = ReadTool 'horizun_query_planimetry' @{ mode = 'annotations'; element_ids = $tagIds; units = 'mm' } 'tag-reread'
    $tagRow = @($tagRead.Result.rows)[0]
    $request['tag_id'] = $tagIds
    # An annotation row's box is bounding_box; 'extent' is a sheet placement's field.
    $request['tag_bounding_box_mm'] = (Get-HzProp $tagRow 'bounding_box')
    $request['tag_head_point_mm'] = (Get-HzProp $tagRow 'tag_head_point')
    $request['tag_has_leader'] = (Get-HzProp $tagRow 'has_leader')
    $request['tag_targets'] = (Get-HzProp $tagRow 'tagged_element_ids')
    # A LEADER WITH NOWHERE TO GO CANNOT BE LOOKED AT. The layout put the tag
    # directly on its wall, which is a fine placement and makes the leader
    # degenerate: the first rendered page showed a tag, a wall, and no visible
    # leader between them. The head is moved off the host so the leader is drawn -
    # 2.5 m of model, 25 mm of paper at 1:100, downwards, away from the dimension.
    $moved = WriteTool 'horizun_transform_elements' @{ units = 'mm'; operations = @(
            @{ operation = 'move_tag_head'; element_ids = @($tagIds[0]); vector = @(0, -2500, 0) }) } 'tag-move-head'
    $request['tag_head_moved_model_mm'] = @(0, -2500, 0)
    $tagRead = ReadTool 'horizun_query_planimetry' @{ mode = 'annotations'; element_ids = $tagIds; units = 'mm' } 'tag-reread-after-move'
    $tagRow = @($tagRead.Result.rows)[0]
    $request['tag_bounding_box_mm'] = (Get-HzProp $tagRow 'bounding_box')
    $request['tag_head_point_mm'] = (Get-HzProp $tagRow 'tag_head_point')
    $request['tag_has_leader'] = (Get-HzProp $tagRow 'has_leader')
    Record 'tag-with-leader' ($tagWrite.Ok -and $moved.Ok -and $tagIds.Count -eq 1 -and (Get-HzProp $tagRow 'bounds_readable') -and
        -not (Get-HzProp $tagRow 'orphaned') -and ((Get-HzProp $tagRow 'has_leader') -eq $true) -and
        ([long]@(Get-HzProp $tagRow 'tagged_element_ids')[0] -eq $wallId)) `
        'One tag on the wall, with a leader, its head moved off the host so the leader is drawn, re-read as unorphaned with a readable extent and the wall as its single target' `
        @{ plan = $tagPlan.Raw; write = $tagWrite.Apply.Raw; move = $moved.Apply.Raw; read = $tagRead.Raw } `
        ("tag=" + ($tagIds -join ',') + " target=" + (@(Get-HzProp $tagRow 'tagged_element_ids') -join ',') +
         " leader=" + [string](Get-HzProp $tagRow 'has_leader') + " head=" + ((@(Get-HzProp $tagRow 'tag_head_point')) -join ','))
    }

    # ---- crop, so the page is a drawing and not a continent ------------------
    # set_crop's box is in the VIEW's right/up plane anchored at the view origin,
    # which is why the fixture is built at the origin: the request and the frame
    # then coincide, and if they did not the rendered page would be empty - which
    # is a thing a person SEES, and the paper measurement below refuses anyway.
    $crop = WriteTool 'horizun_manage_views' @{ units = 'mm'; actions = @(
            @{ operation = 'set_crop'; view_id = $viewId; box = $request['crop_box_mm'] }) } 'crop-plan'

    # ---- one sheet, the view measured and then placed on it ------------------
    # A SHEET NAME HAS TO FIT ITS TITLEBLOCK, and this one is measured rather than
    # hoped for. On the Autodesk titleblock this fixture inherits, the sheet-name
    # label sits between the Project Name value and the Project Number row: 44.3 mm
    # of clear paper around a vertical centre of 680.7 mm, in a style whose line box
    # is 24.8 mm tall and whose field is between 107 and 113 mm wide (measured off
    # the exported pages on 2026-09-09 - 'Visual review' fitted one line at 107.0 mm,
    # the next token wrapped). So ONE line fits with ~10 mm to spare and TWO do not:
    # the previous name, 'Visual review - displaced dimension and tag leader', wrapped
    # to FIVE lines spanning 106 mm and printed straight through both fields.
    # This name is 9 characters, about 78 mm at the ~8.7 mm/character that style
    # measures - a real sheet name, not a description crammed into a name field.
    # The page is measured after export (see the overlap check in
    # scripts/render-pdf.py), so a name that stops fitting is caught rather than
    # accepted - which is how the first attempt at this fix was caught moving the
    # collision to the view title instead of removing it.
    $sheetName = 'Dim + tag'
    $request['sheet_name'] = $sheetName
    $sheet = WriteTool 'horizun_manage_views' @{ units = 'mm'; actions = @(
            @{ operation = 'create_sheet'; key = 's'; number = "HZV-$suffix"; name = $sheetName
               title_block_type_id = $titleBlockTypeId }) } 'create-sheet'
    $sheetId = [long]$sheet.Apply.Result.aliases.s
    $request['sheet_id'] = $sheetId
    $outline = @((ReadTool 'horizun_query_planimetry' @{ mode = 'sheets'; sheet_ids = @($sheetId); units = 'mm' } 'sheet-outline').Result.rows)[0].sheet_outline
    if (@($outline).Count -ne 4) { throw 'The fixture sheet has no measurable paper extent.' }
    $rect = @(([double]$outline[0] + 20), ([double]$outline[1] + 20), ([double]$outline[2] - 20), ([double]$outline[3] - 20))
    $request['usable_rect_mm'] = $rect
    # A REFUSAL HERE IS RECORDED, NOT THROWN. If the viewport will not fit, the page
    # is still worth exporting and looking at: an empty or wrongly cropped sheet is
    # the finding, and the run should hand it over rather than stopping one stage
    # short of the evidence.
    try {
        $placed = WriteTool 'horizun_pack_sheets' @{ units = 'mm'
            sheets = @(@{ sheet_id = $sheetId; usable_rect = $rect })
            items = @(@{ key = 'v'; view_id = $viewId }); margin = 5; gap = 5 } 'place-view'
        $placement = ReadTool 'horizun_query_planimetry' @{ mode = 'placements'; sheet_ids = @($sheetId); units = 'mm' } 'placement-reread'
        $onSheet = @($placement.Result.rows | Where-Object { [long]$_.view_id -eq $viewId })
        Record 'view-placed-on-sheet' ($placed.Ok -and $onSheet.Count -eq 1) `
            'The annotated plan is placed once on its own sheet, through the packer, and the placement is re-read' `
            @{ sheet = $sheet.Apply.Raw; place = $placed.Apply.Raw; read = $placement.Raw }
        # THE SIZE THAT WAS ACTUALLY PLACED, not the size a rehearsal estimated: the
        # placement row carries the viewport's own outline on the paper.
        $paperW = $null; $paperH = $null
        if ($onSheet.Count -eq 1) {
            $box = (Get-HzProp $onSheet[0] 'box_outline')
            if ($null -eq $box) { $box = (Get-HzProp $onSheet[0] 'extent') }
            if ($null -ne $box -and @($box).Count -eq 4) {
                $paperW = [double]@($box)[2] - [double]@($box)[0]
                $paperH = [double]@($box)[3] - [double]@($box)[1]
            }
        }
        $request['viewport_paper_mm'] = @{ width = $paperW; height = $paperH }
        Record 'view-cropped-for-paper' ($crop.Ok -and $null -ne $paperW -and $paperW -lt 500 -and $paperH -lt 500) `
            'The crop is applied and the placed viewport measures less than 500 mm on paper, so the page can be looked at' `
            @{ crop = $crop.Apply.Raw; placement = $placement.Raw } ("viewport " + [string]$paperW + " x " + [string]$paperH + " mm")
    }
    catch {
        Record 'view-placed-on-sheet' $false 'The annotated plan is placed once on its own sheet, through the packer' `
            @{ error = $_.Exception.Message } 'the packer refused the placement'
        Record 'view-cropped-for-paper' $false 'The crop is applied and the placed viewport measures less than 500 mm on paper' `
            @{ error = $_.Exception.Message } 'nothing was placed, so nothing could be measured on the paper'
    }

    # ---- the page ------------------------------------------------------------
    $pdf = Join-Path $run.WorkDir 'visual-review.pdf'
    $files = @()
    try {
        $export = WriteTool 'horizun_export' @{ format = 'pdf'; view_ids = @($sheetId); output_path = $pdf
            pdf_combine = $true; emit_manifest = $true; overwrite = $false } 'export-pdf'
        $delivery = $export.Apply.Result.delivery
        $files = @($delivery.files)
        $hashOk = $true
        foreach ($f in $files) { $hashOk = $hashOk -and ((Get-FileHash -LiteralPath $f.path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $f.sha256) }
        $request['pdf'] = @($files | ForEach-Object { @{ path = $_.path; pages = $_.pages; sha256 = $_.sha256 } })
        Record 'sheet-exported-to-pdf' ($export.Ok -and $delivery.package_verified -eq $true -and $files.Count -eq 1 -and
            ([int]@($files)[0].pages -eq 1) -and $hashOk) `
            'One PDF, one page, reopened and hashed by the exporter and re-hashed here' $export.Apply.Raw `
            ("files=" + $files.Count + " pages=" + [string]@($files)[0].pages)
    }
    catch {
        Record 'sheet-exported-to-pdf' $false 'One PDF, one page, reopened and hashed by the exporter' `
            @{ error = $_.Exception.Message } 'the export refused'
    }

    # ---- and the part a harness must not score -----------------------------
    $requestPath = Join-Path $ArtifactDir 'visual-request.json'
    ($request | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $requestPath -Encoding utf8
    Add-HzProbe -Run $run -Id 'visual-acceptance-rendered-page' -Name 'Visual acceptance of the rendered page' `
        -Expected ('A person or agent renders the page and confirms: the dimension reading 6000 between the two grids, ' +
                   'its text displaced in the direction asked for, text legible and not clipped, nothing outside the ' +
                   'printable area' + $(if ($tagIds.Count -gt 0) { ', and the tag leader reaching the tagged wall' } else { ' (this document has no tag family, so there is no tag on this page)' })) `
        -Status 'not_covered' `
        -Because ("Render " + (@($files | ForEach-Object { $_.path }) -join '; ') + " and compare with " + $requestPath +
                  ". This harness measures files and coordinates; legibility is a judgement it must not score.")
}
catch {
    Record 'campaign-interrupted' $false 'Every declared stage reaches its measured assertion' @{ error = $_.Exception.Message; stack = $_.ScriptStackTrace }
}
foreach ($case in $expectedCases) {
    if (@($run.Probes | Where-Object { $_.id -eq $case }).Count -eq 0) {
        Add-HzProbe -Run $run -Id $case -Name $case -Expected 'Execute the declared live assertion' -Status 'not_covered' -Because 'Campaign stopped before this check; never counted as passed.'
    }
}
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
