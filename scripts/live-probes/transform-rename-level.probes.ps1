# Live probes for horizun_transform_elements operation=rename_level.
#
# MEASURED field session, 2026-09-25: renaming a level in the Revit UI silently
# renames any plan view whose name exactly matched the level's, and raised 9
# Copy/Monitor alerts in the reported case. The dry run reports
# level_rename_rehearsal (views_expected_to_rename, a deterministic read;
# copy_monitor_alerts, measured by actually renaming inside a rolled-back
# transaction); the applied row reports views_renamed and copy_monitor_alerts
# from the real commit.
#
# Creates its OWN throwaway level (Level.Create alone makes no plan view, so
# views_expected_to_rename is legitimately empty here - that is measured, not a
# coverage gap) and renames it twice (leaving the SECOND name, since a level
# rename has no typed inverse - horizun_transform_elements.RecordUndo names
# rename_level explicitly as having none).
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'transform-rename-level'
    Catalog = @(
        @{ Name = 'rename_level: dry run reports views_expected_to_rename and copy_monitor_alerts'; Tool = 'horizun_transform_elements' }
        @{ Name = 'rename_level: apply renames the level and reports the same two fields'; Tool = 'horizun_transform_elements' }
        @{ Name = 'rename_level: mixed with another operation is refused typed'; Tool = 'horizun_transform_elements' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $T = 'horizun_transform_elements'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $doc = $Ctx.Document
        $names = @('rename_level: dry run reports views_expected_to_rename and copy_monitor_alerts',
                   'rename_level: apply renames the level and reports the same two fields',
                   'rename_level: mixed with another operation is refused typed')

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $T 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }

        $tag = 'HZ_PROBE_LVL_' + $Ctx.RunId
        $cr = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = $tag; elevation = 95000.0 }) } 'lvl-create'
        $levelId = $null
        if ($cr.stage -eq 'apply' -and -not $cr.answer.isError -and $cr.answer.data) {
            $row = @($cr.answer.data.rows) | Select-Object -First 1
            if ($row) { $levelId = [long]$row.element_id }
        }
        if (-not $levelId) { foreach ($n in $names) { Case $n $T 'not_covered' 'no own level could be staged' }; return $cases.ToArray() }

        # ---- mixed batch is refused before any transaction opens.
        $mix = & $Ctx.Call $T @{ target_document = $doc; units = 'mm'; dry_run = $true; operations = @(
            @{ operation = 'rename_level'; element_ids = @($levelId); name = ($tag + '_X') },
            @{ operation = 'pin'; element_ids = @($levelId) }) }
        if ($mix.isError -and [string]$mix.text -match 'sole operation') { Case $names[2] $T 'pass' 'refused: rename_level must be the sole operation' }
        else { Case $names[2] $T 'fail' ('expected a sole-operation refusal: ' + $mix.text) }

        # ---- dry run: the rehearsal actually renames and rolls back inside its own transaction.
        $newName = $tag + '_R'
        $req = @{ target_document = $doc; units = 'mm'; operations = @(@{ operation = 'rename_level'; element_ids = @($levelId); name = $newName }) }
        $dry = & $Ctx.Call $T ($req + @{ dry_run = $true })
        $rehearsal = if ($dry.data) { @($dry.data.plan)[0].level_rename_rehearsal } else { $null }
        if (-not $dry.isError -and $rehearsal -and $rehearsal.rollback_confirmed -eq $true -and $null -ne $rehearsal.views_expected_to_rename -and $null -ne $rehearsal.copy_monitor_alerts) {
            Case $names[0] $T 'pass' ("views=" + @($rehearsal.views_expected_to_rename).Count + " alerts=" + @($rehearsal.copy_monitor_alerts).Count + " rollback_confirmed=true")
        } else { Case $names[0] $T 'fail' ([string]$dry.text) }

        # ---- apply: re-read the level's own name and the per-row copy_monitor_alerts field.
        $ap = & $Ctx.Apply $T $req 'lvl-rename'
        $arow = if (-not $ap.answer.isError -and $ap.answer.data) { @($ap.answer.data.rows) | Select-Object -First 1 } else { $null }
        if ($ap.stage -eq 'apply' -and $arow -and $arow.verified -eq $true -and $arow.level_name -eq $newName -and $null -ne $arow.copy_monitor_alerts) {
            Case $names[1] $T 'pass' ("level_name=" + $arow.level_name + " views_renamed_count=" + $arow.views_renamed_count + " alerts=" + @($arow.copy_monitor_alerts).Count)
        } else { Case $names[1] $T 'fail' ("stage=$($ap.stage) " + [string]$ap.answer.text) }

        return $cases.ToArray()
    }
}
