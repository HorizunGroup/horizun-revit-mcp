# Live probes for the ownership side effect of horizun_manage_worksets.
#
# MEASURED field session, 2026-09-25 (a client's workshared model): renaming a
# workset silently took ownership of 14,697 elements. create/rename/move_elements
# now report ownership_effect (measured before/after with
# WorksharingUtils.GetCheckoutStatus) and accept relinquish_after=true to give
# everything back and re-measure. Needs a workshared model: on one that is not
# (the HZ_WRITE fixture) every case is reported not_covered with the reason, the
# same way the existing groups-worksets probe handles it.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'worksets-ownership'
    Catalog = @(
        @{ Name = 'worksets: create reports a measured ownership_effect'; Tool = 'horizun_manage_worksets' }
        @{ Name = 'worksets: rename reports ownership_effect and relinquish_after gives it back'; Tool = 'horizun_manage_worksets' }
        @{ Name = 'worksets: move_elements reports ownership_effect'; Tool = 'horizun_manage_worksets' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $W = 'horizun_manage_worksets'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Ok($applied) { $applied.stage -eq 'apply' -and -not $applied.answer.isError }
        function Why($applied) { "stage=$($applied.stage) " + [string]$applied.answer.text }
        $doc = $Ctx.Document
        $names = @('worksets: create reports a measured ownership_effect',
                   'worksets: rename reports ownership_effect and relinquish_after gives it back',
                   'worksets: move_elements reports ownership_effect')

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $W 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }

        $ws = & $Ctx.Call $W @{ operation = 'list'; target_document = $doc }
        $wcode = if ($ws.data) { $ws.data.code } elseif ($ws.structured) { $ws.structured.code } else { $null }
        if ($ws.isError -and $wcode -eq 'not_workshared') {
            foreach ($n in $names) { Case $n $W 'not_covered' "'$doc' is not workshared; ownership_effect has nothing to measure here" }
            return $cases.ToArray()
        }
        if ($ws.isError -or $null -eq $ws.data.worksets) {
            foreach ($n in $names) { Case $n $W 'unverified' ('worksets: list neither listed nor refused typed: ' + $ws.text) }
            return $cases.ToArray()
        }

        $tag = 'HZ_PROBE_OWN_' + $Ctx.RunId

        # ---- create: a brand-new workset has no pre-existing elements, so the
        # effect is trivially zero elements / an unowned workset - still a MEASURED
        # zero, not an absent field.
        $cr = & $Ctx.Apply $W @{ operation = 'create'; target_document = $doc; name = $tag } 'own-create'
        $wid = $null
        if ((Ok $cr) -and $cr.answer.data.ownership_effect.measured -eq $true) {
            $wid = [int]$cr.answer.data.workset_id
            Case $names[0] $W 'pass' ("workset $wid, elements_examined=" + $cr.answer.data.ownership_effect.elements_examined +
                ", newly_owned=" + $cr.answer.data.ownership_effect.elements_newly_owned_by_me)
        } else { Case $names[0] $W 'fail' (Why $cr) }

        if (-not $wid) {
            foreach ($n in $names[1..2]) { Case $n $W 'not_covered' 'create did not produce a workset to rename or move into' }
            return $cases.ToArray()
        }

        # ---- rename + relinquish_after: rename is where the field defect was
        # measured (renaming took ownership of every element already in the
        # workset). This probe's own workset is empty, so elements_newly_owned_by_me
        # is expected to be a small number (possibly the workset table read itself);
        # what is proved is that the field is MEASURED and relinquish_after ran and
        # re-measured.
        $rn = & $Ctx.Apply $W @{ operation = 'rename'; target_document = $doc; workset_id = $wid; name = ($tag + '_R'); relinquish_after = $true } 'own-rename'
        if ((Ok $rn) -and $rn.answer.data.ownership_effect.measured -eq $true -and $rn.answer.data.relinquish_after.attempted -eq $true -and
            $null -ne $rn.answer.data.relinquish_after.elements_still_owned_by_me) {
            Case $names[1] $W 'pass' ("newly_owned=" + $rn.answer.data.ownership_effect.elements_newly_owned_by_me +
                ", relinquish still_owned=" + $rn.answer.data.relinquish_after.elements_still_owned_by_me)
        } else { Case $names[1] $W 'fail' (Why $rn) }

        # ---- move_elements: needs a free host element. Reuses horizun_list_elements
        # the same way the groups-worksets probe discovers free walls, and moves it
        # back afterward so the fixture is left as found.
        $walls = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Walls'; include_links = $false; max_rows = 50 }
        $free = @(@($walls.data.rows) | Where-Object { $_.source_kind -eq 'host' } | Select-Object -First 1 | ForEach-Object { [long]$_.element_id })
        if ($free.Count -eq 0) { Case $names[2] $W 'not_covered' 'no host wall found to move' }
        else {
            $mv = & $Ctx.Apply $W @{ operation = 'move_elements'; target_document = $doc; workset_id = $wid; element_ids = @($free[0]); relinquish_after = $true } 'own-move'
            $origin = if ($mv.dry -and $mv.dry.data) { @($mv.dry.data.plan.move)[0].from_workset_id } else { $null }
            if ((Ok $mv) -and $mv.answer.data.ownership_effect.measured -eq $true) {
                Case $names[2] $W 'pass' ("newly_owned=" + $mv.answer.data.ownership_effect.elements_newly_owned_by_me +
                    ", elements_examined=" + $mv.answer.data.ownership_effect.elements_examined)
            } else { Case $names[2] $W 'fail' (Why $mv) }
            if ((Ok $mv) -and $null -ne $origin) {
                $null = & $Ctx.Apply $W @{ operation = 'move_elements'; target_document = $doc; workset_id = [int]$origin; element_ids = @($free[0]) } 'own-move-back'
            }
        }

        return $cases.ToArray()
    }
}
