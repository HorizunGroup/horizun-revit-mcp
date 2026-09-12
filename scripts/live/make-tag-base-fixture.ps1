#Requires -Version 7.0
<#
  A DISPOSABLE FIXTURE THAT HAS A TAG FAMILY, BUILT WITHOUT ASKING ANYONE.

  The tag probes could not run before Revit 2026 because no wall-capable tag
  family existed in a 2023-format file on this machine: the 2026 RFA cannot open
  backwards, the local libraries hold no tag family at all (2,241 families read
  by scripts/rfa-provenance.py, 411 of them 2023-format, none a tag), no typed
  command loads an existing .rfa into a project, and horizun_execute_python -
  which could - is disabled by the machine's owner and stays that way.

  What was left unlooked-at is that Autodesk's own PROJECT TEMPLATES ship with
  those families already loaded. A template is a document: the bridge can open
  one and save it as a project, which is a typed route to a fixture that HAS the
  family, with no library install, no permission change and nobody's afternoon.

  This harness opens the candidate templates in turn, ASKS the model which tag
  families are loaded - a string search in the file cannot answer it, the streams
  are compressed - keeps the first that can tag a wall and also carries what the
  deliverable harness needs (a titleblock, a wall type, a level, a linear
  dimension type), and saves it as a NEW file. It never writes to the template:
  the source stays read-only and the output is a path that must not already
  exist.

  It closes every document it opened, including the one it produced, so the
  driver that runs it finds nothing it does not recognise.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('yes-this-model-is-disposable')][string]$Disposable,
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string[]]$Template,
    [string]$Document = ''
)
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'tag-base-fixture' -Document 'template-staging' -WorkDir (Join-Path $ArtifactDir ('stage-' + [guid]::NewGuid().ToString('N')))
$record = [ordered]@{ output = $Output; candidates = @() }

function CloseDoc([string]$Target) {
    # A document this harness opened, closed the way the bridge insists: rehearse,
    # then act. Nothing here is ever saved, so nothing here can lose work.
    $dry = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label ('close-dry-' + [IO.Path]::GetFileNameWithoutExtension($Target)) -Arguments @{
        operation = 'close'; target_document = $Target; dry_run = $true; activate_other = $true }
    $token = $null
    try { $token = Get-HzProp $dry.Result 'confirmation_token' } catch { $token = $null }
    $apply = @{ operation = 'close'; target_document = $Target; discard_unsaved = $true; activate_other = $true
                idempotency_key = ('tagbase-close-' + (Get-Date -Format 'yyyyMMddHHmmssfff')) }
    if ($token) { $apply['confirmation_token'] = $token }
    $out = Invoke-HzTool -Run $run -Tool 'horizun_document_session' -Label ('close-' + [IO.Path]::GetFileNameWithoutExtension($Target)) -Arguments $apply
    return (-not $out.IsError)
}

try {
    if (Test-Path -LiteralPath $Output) {
        throw "the destination already exists and this harness never overwrites one: $Output"
    }
    $h = Get-HzHealth $run
    if ((Get-HzProp $h 'status') -ne 'healthy') { throw 'the bridge is not healthy; nothing was opened.' }
    $chosen = $null
    foreach ($tpl in $Template) {
        if (-not (Test-Path -LiteralPath $tpl)) {
            $record.candidates += [ordered]@{ template = $tpl; usable = $false; why = 'not on this machine' }
            continue
        }
        $opened = $false
        $entry = [ordered]@{ template = $tpl; template_sha256 = (Get-FileHash -LiteralPath $tpl).Hash.ToLower(); usable = $false }
        try {
            $open = Invoke-HzTool -Run $run -Tool 'horizun_open_document' -Label ('open-' + [IO.Path]::GetFileNameWithoutExtension($tpl)) -Arguments @{
                path = ($tpl.Replace([char]92, '/')); activate = $true
                idempotency_key = ('tagbase-open-' + (Get-Date -Format 'yyyyMMddHHmmssfff')) }
            if ($open.IsError) { $entry.why = 'the bridge refused to open it: ' + (Limit-HzText $open.Text 200); $record.candidates += $entry; continue }
            $opened = $true
            $title = $null
            foreach ($cand in @((Get-HzProp $open.Result 'active_document'), (Get-HzProp $open.Result 'title'))) {
                if ($cand) { $title = [string]$cand; break }
            }
            $entry.opened_title = $title
            # ASK THE MODEL. Which tag families are loaded is not a question a byte
            # search can answer: the template's streams are compressed.
            $counts = [ordered]@{}
            foreach ($cat in 'OST_MultiCategoryTags', 'OST_WallTags', 'OST_TitleBlocks', 'OST_Walls') {
                $q = Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label ("types-$cat-" + [IO.Path]::GetFileNameWithoutExtension($tpl)) -Arguments @{
                    categories = @($cat); include_types = $true; max_rows = 300 }
                $types = @(@($q.Result.rows) | Where-Object { (Get-HzProp $_ 'is_element_type') -eq $true })
                $counts[$cat] = $types.Count
                if ($cat -eq 'OST_MultiCategoryTags' -and $types.Count -gt 0) { $entry.multi_category_tag_type = [long](Get-HzProp $types[0] 'element_id'); $entry.multi_category_tag_name = [string](Get-HzProp $types[0] 'family') }
                if ($cat -eq 'OST_WallTags' -and $types.Count -gt 0) { $entry.wall_tag_type = [long](Get-HzProp $types[0] 'element_id'); $entry.wall_tag_name = [string](Get-HzProp $types[0] 'family') }
            }
            $levels = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label ('levels-' + [IO.Path]::GetFileNameWithoutExtension($tpl)) -Arguments @{ categories = @('OST_Levels'); max_rows = 5 }).Result.rows)
            # DIMENSION TYPES, NOT DIMENSIONS. query_dimensions lists the dimensions
            # a document HAS; a template has none, and asking it that way reports
            # "no linear dimension type" about a document with twenty of them
            # (measured on Default_M_ENU.rte, 2026-09-09). The types are elements
            # of the category, which is what include_types returns.
            $dims = @((Invoke-HzToolStrict -Run $run -Tool 'horizun_query_model' -Label ('dimtypes-' + [IO.Path]::GetFileNameWithoutExtension($tpl)) -Arguments @{
                categories = @('OST_Dimensions'); include_types = $true; max_rows = 200 }).Result.rows |
                Where-Object { (Get-HzProp $_ 'is_element_type') -eq $true })
            $entry.counts = $counts
            $entry.levels = $levels.Count
            $entry.linear_dimension_types = $dims.Count
            $hasTag = ($counts['OST_MultiCategoryTags'] -gt 0) -or ($counts['OST_WallTags'] -gt 0)
            # The dimension type is NOT a gate: horizun_annotate resolves the
            # document's default itself and falls back to any type of the right
            # style, so a template that reports none through a category query still
            # dimensions perfectly. It is recorded because it is worth knowing.
            $entry.usable = $hasTag -and ($counts['OST_TitleBlocks'] -gt 0) -and ($counts['OST_Walls'] -gt 0) -and ($levels.Count -gt 0)
            if (-not $entry.usable) {
                $missing = @()
                if (-not $hasTag) { $missing += 'no wall-capable tag family' }
                if ($counts['OST_TitleBlocks'] -le 0) { $missing += 'no titleblock type' }
                if ($counts['OST_Walls'] -le 0) { $missing += 'no wall type' }
                if ($levels.Count -le 0) { $missing += 'no level' }
                $entry.why = $missing -join '; '
                $record.candidates += $entry
                $null = CloseDoc $title
                $opened = $false
                continue
            }
            # SAVED AS A NEW FILE, never over the template. overwrite stays false.
            $save = Invoke-HzToolStrict -Run $run -Tool 'horizun_document_session' -Label 'save-as-fixture' -Arguments @{
                operation = 'save_as'; target_document = $title
                save_as_path = $Output; overwrite = $false
                idempotency_key = ('tagbase-saveas-' + (Get-Date -Format 'yyyyMMddHHmmssfff')) }
            $entry.saved_as = $Output
            $entry.save_reply = (Limit-HzText $save.Text 300)
            $chosen = $entry
            $record.candidates += $entry
            # The produced document is open under its new path; close it so the
            # driver finds nothing it did not register.
            $newTitle = [IO.Path]::GetFileNameWithoutExtension($Output)
            $null = CloseDoc $newTitle
            $opened = $false
            break
        }
        finally {
            if ($opened) { $null = CloseDoc ([string]$entry.opened_title) }
        }
    }
    if (-not $chosen) {
        Add-HzProbe -Run $run -Id 'tag-base-fixture' -Name 'tag-base-fixture' `
            -Expected 'A template with a wall-capable tag family, a titleblock, a wall type, a level and a linear dimension type, saved as a new disposable project' `
            -Status 'fixture_missing' `
            -Because ('none of the candidate templates carried everything the tag probes need: ' +
                      (($record.candidates | ForEach-Object { "$($_.template): $($_.why)" }) -join ' | ')) `
            -Evidence $record
    }
    else {
        $exists = Test-Path -LiteralPath $Output
        $sha = if ($exists) { (Get-FileHash -LiteralPath $Output).Hash.ToLower() } else { $null }
        $record.output_sha256 = $sha
        Add-HzProbe -Run $run -Id 'tag-base-fixture' -Name 'tag-base-fixture' `
            -Expected 'A template with a wall-capable tag family saved as a new disposable project, the template untouched' `
            -Ok ($exists -and $sha) `
            -Observed ("from " + $chosen.template + " -> " + $Output) `
            -Evidence $record
    }
}
catch {
    Add-HzProbe -Run $run -Id 'tag-base-fixture' -Name 'tag-base-fixture' -Expected 'A disposable fixture with a tag family' `
        -Ok $false -Evidence @{ error = $_.Exception.Message; stack = $_.ScriptStackTrace; record = $record }
}
($record | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'tag-base-fixture.json') -Encoding utf8
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
