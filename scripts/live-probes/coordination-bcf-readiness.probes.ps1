# Live probe module: horizun_coordination's BCF-from-any-tool import
# (CoordinationImportBcfExternal.cs) plus navisworks_readiness/prepare_navisworks
# (CoordinationNavisworksReadiness.cs). Needs NO Navisworks and NO BIMcollab/Solibri:
# stages two OWN crossing pipes, writes their IfcGUID (Identity Data) parameter so a
# hand-built BCF 2.1 zip can name them by IfcGuid exactly the way an external tool
# would, imports it (dry_run then apply) and checks the finding lands with origin
# bcf. Then it reads the Navisworks readiness of the write document's 3D view and,
# ONLY on a view this probe creates itself (never a real 'Navisworks'/'{3D}' view
# already in the fixture), rehearses and applies prepare_navisworks and re-reads
# Fine. Every staged element and view is deleted at the end.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'coordination-bcf-readiness'
    Catalog = @(
        @{ Name = 'coordination-bcf-readiness: BCF import dry_run reproduces the crossing pipes by IfcGuid'; Tool = 'horizun_coordination' }
        @{ Name = 'coordination-bcf-readiness: BCF import apply records the finding with origin bcf'; Tool = 'horizun_coordination' }
        @{ Name = 'coordination-bcf-readiness: navisworks_readiness names the 3D view and its detail level'; Tool = 'horizun_coordination' }
        @{ Name = 'coordination-bcf-readiness: prepare_navisworks dry_run returns a token and the planned change'; Tool = 'horizun_coordination' }
        @{ Name = 'coordination-bcf-readiness: prepare_navisworks apply sets Fine on a disposable view'; Tool = 'horizun_coordination' }
        @{ Name = 'coordination-bcf-readiness: cleanup deletes the probe pipes and the disposable view'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'coordination-bcf-readiness' } | Select-Object -First 1).Catalog
        function Out-Case($i, $outcome, $detail) { @{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = $detail } }
        if ($Ctx.WriteGate) { return @(0..5 | ForEach-Object { Out-Case $_ 'not_covered' 'write tier closed' } ) }
        $doc = $Ctx.Document
        $cases = @()
        $created = @()
        $viewId = $null

        try {
            # ---- stage two crossing pipes (same shape as navisworks-handoff) --------
            function Find-Type($category) {
                $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; max_rows = 500; include_links = $false }
                if (-not $q.data) { return $null }
                $t = @($q.data.rows | Where-Object { $_.is_element_type })
                if ($t.Count -gt 0) { return $t[0].element_id } else { return $null }
            }
            $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
            $level = if ($lv.data -and @($lv.data.rows).Count -gt 0) { @($lv.data.rows)[0].element_id } else { $null }
            $pipeType = Find-Type 'OST_PipeCurves'; $system = Find-Type 'OST_PipingSystem'
            if (-not $level -or -not $pipeType -or -not $system) {
                foreach ($i in 0..1) { $cases += Out-Case $i 'not_covered' "'$doc' has no level, pipe type or piping system to stage the crossing" }
            }
            else {
                $x = 560000; $y = 0; $z = 2800
                function New-Pipe($s, $e, $d, $key) {
                    $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                        elements = @(@{ kind = 'pipe'; start = $s; end = $e; diameter = $d; level_id = [long]$level; type_id = [long]$pipeType; system_type_id = [long]$system }) } ($Ctx.RunId + '-bcfr-' + $key)
                    if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) { return [long]@($r.answer.data.rows)[0].element_id }
                    return $null
                }
                $small = New-Pipe @($x, $y, $z) @(($x + 3000), $y, $z) 50 'small'
                if ($small) { $created += $small }
                $big = New-Pipe @(($x + 1500), ($y - 1500), $z) @(($x + 1500), ($y + 1500), $z) 150 'big'
                if ($big) { $created += $big }

                if (-not $small -or -not $big) {
                    foreach ($i in 0..1) { $cases += Out-Case $i 'unverified' 'the crossing pipes could not be staged' }
                }
                else {
                    # ---- write a synthetic IfcGUID on each pipe, then READ it back via
                    # horizun_query_model return_parameters - exactly the parameter path
                    # CoordinationImportBcfExternal.cs's FindByIfcGuidParameter uses. The
                    # string does not need to be a spec-valid compressed guid: the fast
                    # path matches by exact IFC_GUID parameter equality, nothing decodes it.
                    $guidSmall = 'HZPROBE' + $Ctx.RunId + 'SMALL'
                    $guidBig = 'HZPROBE' + $Ctx.RunId + 'BIG'
                    $wp = & $Ctx.Apply 'horizun_write_params_verified' @{ target_document = $doc; writes = @(
                            @{ target_id = $small; parameter = 'IfcGUID'; value = $guidSmall },
                            @{ target_id = $big; parameter = 'IfcGUID'; value = $guidBig }) } ($Ctx.RunId + '-bcfr-ifcguid')
                    $wrote = $wp.stage -eq 'apply' -and -not $wp.answer.isError -and $wp.answer.data.verification.verified -eq $true
                    if (-not $wrote) {
                        foreach ($i in 0..1) { $cases += Out-Case $i 'not_covered' ('this document/version does not accept a written IfcGUID parameter on OST_PipeCurves: ' + (($wp.answer | ConvertTo-Json -Depth 6 -Compress) -as [string])) }
                    }
                    else {
                        $rq = & $Ctx.Call 'horizun_query_model' @{ target_document = $doc; element_ids = @($small, $big)
                            return_parameters = @('IfcGUID'); parameter_format = 'compact'; max_rows = 10 }
                        $rowSmall = @($rq.data.rows | Where-Object { $_.element_id -eq $small }) | Select-Object -First 1
                        $rowBig = @($rq.data.rows | Where-Object { $_.element_id -eq $big }) | Select-Object -First 1
                        $readBack = -not $rq.isError -and $rowSmall -and $rowBig -and
                                    $rowSmall.parameters.IfcGUID -eq $guidSmall -and $rowBig.parameters.IfcGUID -eq $guidBig
                        if (-not $readBack) {
                            foreach ($i in 0..1) { $cases += Out-Case $i 'unverified' ('read-back of the written IfcGUID did not match: ' + (($rq.data | ConvertTo-Json -Depth 6 -Compress) -as [string])) }
                        }
                        else {
                            # ---- a hand-built BCF 2.1 zip naming both pipes by IfcGuid -------
                            # Deterministic from RunId (not a random Guid): the offline test
                            # harness predicts it the same way import_navisworks's probe does
                            # with its issue_id, and this ledger never validates a topic's own
                            # Guid as RFC4122 - only BcfTopicGuid(finding.Id) has to be one.
                            $topicGuid = 'HZBCF-' + $Ctx.RunId
                            $bcfDir = Join-Path $Ctx.ScratchRoot ('hz-bcf-readiness-' + $Ctx.RunId)
                            $topicDir = Join-Path $bcfDir 'TOPIC'
                            New-Item -ItemType Directory -Force -Path $topicDir | Out-Null
                            Set-Content -LiteralPath (Join-Path $bcfDir 'bcf.version') -Encoding utf8 -Value '<Version VersionId="2.1"><DetailedVersion>2.1</DetailedVersion></Version>'
                            $markup = @"
<?xml version="1.0" encoding="UTF-8"?>
<Markup>
  <Topic Guid="$topicGuid" TopicType="Clash" TopicStatus="Open">
    <Title>Probe pipe crossing (bcf-readiness $($Ctx.RunId))</Title>
    <Priority>High</Priority>
  </Topic>
  <Viewpoints Guid="$([guid]::NewGuid())"><Viewpoint>viewpoint.bcfv</Viewpoint></Viewpoints>
</Markup>
"@
                            $viewpoint = @"
<?xml version="1.0" encoding="UTF-8"?>
<VisualizationInfo Guid="$([guid]::NewGuid())">
  <Components>
    <Selection>
      <Component IfcGuid="$guidSmall"><OriginatingSystem>HorizunProbe</OriginatingSystem></Component>
      <Component IfcGuid="$guidBig"><OriginatingSystem>HorizunProbe</OriginatingSystem></Component>
    </Selection>
  </Components>
</VisualizationInfo>
"@
                            Set-Content -LiteralPath (Join-Path $topicDir 'markup.bcf') -Encoding utf8 -Value $markup
                            Set-Content -LiteralPath (Join-Path $topicDir 'viewpoint.bcfv') -Encoding utf8 -Value $viewpoint
                            $bcfZip = Join-Path $Ctx.ScratchRoot ('hz-bcf-readiness-' + $Ctx.RunId + '.bcfzip')
                            if (Test-Path -LiteralPath $bcfZip) { Remove-Item -LiteralPath $bcfZip -Force }
                            # Windows PowerShell 5.1 accepts only .zip as the archive output
                            # extension. A BCFZIP is a ZIP with a different name.
                            $zipFile = [IO.Path]::ChangeExtension($bcfZip, '.zip')
                            if (Test-Path -LiteralPath $zipFile) { Remove-Item -LiteralPath $zipFile -Force }
                            try {
                                Compress-Archive -Path (Join-Path $bcfDir '*') -DestinationPath $zipFile -Force
                                Move-Item -LiteralPath $zipFile -Destination $bcfZip -Force
                            }
                            finally {
                                if (Test-Path -LiteralPath $zipFile) { Remove-Item -LiteralPath $zipFile -Force }
                            }

                            # ---- 0: dry_run reproduces ---------------------------------
                            $dry = & $Ctx.Call 'horizun_coordination' @{ operation = 'import'; target_document = $doc; path = $bcfZip; dry_run = $true }
                            $dryOk = -not $dry.isError -and @($dry.data.external_reproduced).Count -ge 1 -and [int]$dry.data.external_would_record -ge 1
                            $cases += Out-Case 0 $(if ($dryOk) { 'pass' } else { 'fail' }) (($dry.data | ConvertTo-Json -Depth 6 -Compress) -as [string])

                            if (-not $dryOk) { $cases += Out-Case 1 'not_covered' 'the import dry run did not reproduce the synthetic bcf topic' }
                            else {
                                # ---- 1: apply records with origin bcf ----------------------
                                $ap = & $Ctx.Call 'horizun_coordination' @{ operation = 'import'; target_document = $doc; path = $bcfZip; dry_run = $false; idempotency_key = ($Ctx.RunId + '-bcfr-import') }
                                $open = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; max_rows = 500 }
                                $row = @($open.data.rows | Where-Object { $_.external_issue_id -eq $topicGuid }) | Select-Object -First 1
                                $recordOk = -not $ap.isError -and [int]$ap.data.external_recorded -ge 1 -and $ap.data.verified_by_reread -eq $true -and
                                            $row -and $row.external_source -eq 'bcf'
                                $cases += Out-Case 1 $(if ($recordOk) { 'pass' } else { 'fail' }) (('finding=' + ($row.finding_id -as [string]) + ' ') + (($ap.data | ConvertTo-Json -Depth 6 -Compress) -as [string]))
                            }
                        }
                    }
                }
            }

            # ---- 2: navisworks_readiness names the view and its detail level --------
            # One pass through a do/while(false) so every early exit is a `break` that still
            # reaches the finally below: a `return $cases` inside this try evaluated $cases
            # BEFORE the finally appended the cleanup case, which then went unreported
            # (MEASURED 2026-09-26, Revit 2023: "cleanup ... did not report this case").
            do {
                $ready = & $Ctx.Call 'horizun_coordination' @{ operation = 'navisworks_readiness'; target_document = $doc }
                # A fixture with NO 'Navisworks' and NO '{3D}' view (MEASURED 2026-09-26: Revit
                # 2023's HZ23_BASE) is refused, rightly, naming what is missing. That refusal is
                # the answer for this fixture; the probe then stages its OWN 'Navisworks' view
                # and asks again, so the verdict case still measures a view and its detail level.
                $refusedNoView = $ready.isError -and ([string]$ready.text -match "No 3D view whose name contains 'Navisworks'")
                $firstNote = ''
                if ($refusedNoView) {
                    $mv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'
                            actions = @(@{ operation = 'create_3d'; key = 'nwprobe'; name = 'Navisworks' }) } ($Ctx.RunId + '-bcfr-view')
                    if ($mv.stage -ne 'apply' -or $mv.answer.isError -or -not $mv.answer.data.aliases.nwprobe) {
                        foreach ($i in 2..4) { $cases += Out-Case $i 'unverified' ('the fixture has no Navisworks/{3D} view and the probe could not create its own: ' + (($mv.answer | ConvertTo-Json -Depth 6 -Compress) -as [string])) }
                        break
                    }
                    $viewId = [long]$mv.answer.data.aliases.nwprobe
                    $firstNote = "the fixture had no Navisworks/{3D} view and the first call refused naming what was missing; with the probe's own view: "
                    $ready = & $Ctx.Call 'horizun_coordination' @{ operation = 'navisworks_readiness'; target_document = $doc }
                }
                $readyOk = -not $ready.isError -and $ready.data -and -not [string]::IsNullOrWhiteSpace([string]$ready.data.detail_level) -and
                           @('ready', 'not_ready') -contains [string]$ready.data.verdict -and -not [string]::IsNullOrWhiteSpace([string]$ready.data.view_name) -and
                           (-not $refusedNoView -or [string]$ready.data.view_name -eq 'Navisworks')
                $readyDetail = if ($ready.data) { ($ready.data | ConvertTo-Json -Depth 6 -Compress) -as [string] } else { [string]$ready.text }
                $cases += Out-Case 2 $(if ($readyOk) { 'pass' } else { 'fail' }) ($firstNote + $readyDetail)
                if (-not $readyOk) {
                    foreach ($i in 3..4) { $cases += Out-Case $i 'not_covered' 'no navisworks_readiness verdict to prepare against' }
                    break
                }

                # ---- disposable view: NEVER the real one already reported above ---------
                if (-not $viewId) {
                    if ($ready.data.view_name -eq 'Navisworks') {
                        foreach ($i in 3..4) { $cases += Out-Case $i 'not_covered' "a real 3D view already named exactly 'Navisworks' exists in this fixture; not touching it - only a view this probe creates itself is prepared." }
                        break
                    }
                    $mv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'
                            actions = @(@{ operation = 'create_3d'; key = 'nwprobe'; name = 'Navisworks' }) } ($Ctx.RunId + '-bcfr-view')
                    if ($mv.stage -ne 'apply' -or $mv.answer.isError -or -not $mv.answer.data.aliases.nwprobe) {
                        foreach ($i in 3..4) { $cases += Out-Case $i 'unverified' ('could not create the disposable Navisworks view: ' + (($mv.answer | ConvertTo-Json -Depth 6 -Compress) -as [string])) }
                        break
                    }
                    $viewId = [long]$mv.answer.data.aliases.nwprobe
                }

                # ---- 3: prepare_navisworks dry_run: a token and the planned change ------
                $prepDry = & $Ctx.Call 'horizun_coordination' @{ operation = 'prepare_navisworks'; target_document = $doc; dry_run = $true }
                $prepDryOk = -not $prepDry.isError -and $prepDry.data -and -not [string]::IsNullOrWhiteSpace([string]$prepDry.data.confirmation_token) -and
                             [long]$prepDry.data.view_id -eq $viewId -and [string]$prepDry.data.detail_level_after -eq 'Fine'
                $cases += Out-Case 3 $(if ($prepDryOk) { 'pass' } else { 'fail' }) (($prepDry.data | ConvertTo-Json -Depth 6 -Compress) -as [string])
                if (-not $prepDryOk) { $cases += Out-Case 4 'not_covered' 'the dry run gave no plan to apply' }
                else {
                    # ---- 4: apply sets Fine, verified by re-read ---------------------------
                    $prepApply = & $Ctx.Apply 'horizun_coordination' @{ operation = 'prepare_navisworks'; target_document = $doc } ($Ctx.RunId + '-bcfr-prepare')
                    $prepOk = $prepApply.stage -eq 'apply' -and -not $prepApply.answer.isError -and
                              [string]$prepApply.answer.data.detail_level -eq 'Fine' -and
                              $prepApply.answer.data.postconditions.all_verified -eq $true
                    $cases += Out-Case 4 $(if ($prepOk) { 'pass' } else { 'fail' }) (($prepApply.answer.text -as [string]))
                }
            } while ($false)
        }
        finally {
            $ids = @($created)
            if ($viewId) { $ids += $viewId }
            if ($ids.Count -gt 0) {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = @($ids); target_document = $doc; id_cap = 10 } ($Ctx.RunId + '-bcfr-delete')
                $gone = $del.stage -eq 'apply' -and -not $del.answer.isError
                $cases += Out-Case 5 $(if ($gone) { 'pass' } else { 'fail' }) ('deleted ' + ($ids -join ','))
            }
            else {
                $cases += Out-Case 5 'not_covered' 'nothing was created to delete'
            }
        }
        return $cases
    }
}
