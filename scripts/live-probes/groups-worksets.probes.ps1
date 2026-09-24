# Live probes for horizun_manage_groups and horizun_manage_worksets.
# Loaded by scripts/verify-live.ps1 (see README.md). Runs on the disposable write
# document; creates its own group from walls it finds, redefines it, and removes
# every group type it created. Worksets need a workshared model: on one that is not
# (the HZ_WRITE fixture) the typed refusal is probed and the write cases are
# reported not_covered with the reason.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'groups-worksets'
    Catalog = @(
        @{ Name = 'groups: list reads group types, instances and members'; Tool = 'horizun_manage_groups' }
        @{ Name = 'groups: create a model group of two walls and re-read its members'; Tool = 'horizun_manage_groups' }
        @{ Name = 'groups: add a member to the group type and re-read the members'; Tool = 'horizun_manage_groups' }
        @{ Name = 'groups: remove that member and re-read the members'; Tool = 'horizun_manage_groups' }
        @{ Name = 'groups: rename, duplicate and swap the group type'; Tool = 'horizun_manage_groups' }
        @{ Name = 'groups: convert_to_link is refused typed (api_absent)'; Tool = 'horizun_manage_groups' }
        @{ Name = 'groups: ungroup and delete what the probe created'; Tool = 'horizun_manage_groups' }
        @{ Name = 'worksets: a model that is not workshared is refused typed (not_workshared)'; Tool = 'horizun_manage_worksets' }
        @{ Name = 'worksets: create, rename and move an element on a workshared model'; Tool = 'horizun_manage_worksets' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $G = 'horizun_manage_groups'; $W = 'horizun_manage_worksets'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Ok($applied) { $applied.stage -eq 'apply' -and -not $applied.answer.isError -and $applied.answer.data.host_verified -eq $true }
        function Why($applied) { "stage=$($applied.stage) " + [string]$applied.answer.text }
        $doc = $Ctx.Document
        if ($Ctx.WriteGate) {
            foreach ($n in @('groups: list reads group types, instances and members', 'groups: create a model group of two walls and re-read its members',
                             'groups: add a member to the group type and re-read the members', 'groups: remove that member and re-read the members',
                             'groups: rename, duplicate and swap the group type', 'groups: convert_to_link is refused typed (api_absent)',
                             'groups: ungroup and delete what the probe created')) { Case $n $G 'not_covered' 'write tier is not open for this run' }
            Case 'worksets: a model that is not workshared is refused typed (not_workshared)' $W 'not_covered' 'write tier is not open for this run'
            Case 'worksets: create, rename and move an element on a workshared model' $W 'not_covered' 'write tier is not open for this run'
            return $cases.ToArray()
        }

        # ---- groups -------------------------------------------------------------
        $list = & $Ctx.Call $G @{ operation = 'list'; target_document = $doc }
        $grouped = @{}
        if (-not $list.isError -and $null -ne $list.data.types) {
            foreach ($t in @($list.data.types)) { foreach ($i in @($t.instances)) { foreach ($m in @($i.member_ids)) { $grouped[[long]$m] = $true } } }
            Case 'groups: list reads group types, instances and members' $G 'pass' ("{0} type(s), {1} instance(s)" -f $list.data.type_count, $list.data.instance_count_returned)
        } else { Case 'groups: list reads group types, instances and members' $G 'fail' $list.text }

        $refuse = & $Ctx.Call $G @{ operation = 'convert_to_link'; target_document = $doc; group_ids = @(1) }
        $code = if ($refuse.data) { $refuse.data.code } elseif ($refuse.structured) { $refuse.structured.code } else { $null }
        Case 'groups: convert_to_link is refused typed (api_absent)' $G $(if ($refuse.isError -and $code -eq 'api_absent') { 'pass' } else { 'fail' }) ("isError=$($refuse.isError) code=$code")

        $walls = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Walls'; include_links = $false; max_rows = 200 }
        $free = @(@($walls.data.rows) | Where-Object { $_.source_kind -eq 'host' -and -not $grouped.ContainsKey([long]$_.element_id) } | ForEach-Object { [long]$_.element_id })
        $rest = @('groups: create a model group of two walls and re-read its members', 'groups: add a member to the group type and re-read the members',
                  'groups: remove that member and re-read the members', 'groups: rename, duplicate and swap the group type', 'groups: ungroup and delete what the probe created')
        if ($free.Count -lt 3) {
            foreach ($n in $rest) { Case $n $G 'not_covered' ("the fixture has {0} ungrouped host wall(s); 3 are needed" -f $free.Count) }
        } else {
            $tag = 'HZ_PROBE_GRP_' + $Ctx.RunId
            $created = New-Object System.Collections.Generic.List[long]
            $gid = $null; $tid = $null
            $c = & $Ctx.Apply $G @{ operation = 'create'; target_document = $doc; element_ids = @($free[0], $free[1]); name = $tag } 'grp-create'
            if (Ok $c) { $gid = [long]$c.answer.data.result.group_id; $tid = [long]$c.answer.data.result.type_id; $created.Add($tid)
                Case $rest[0] $G 'pass' "group $gid type $tid" } else { Case $rest[0] $G 'fail' (Why $c) }
            if ($gid) {
                $a = & $Ctx.Apply $G @{ operation = 'add_members'; target_document = $doc; group_ids = @($gid); element_ids = @($free[2]) } 'grp-add'
                if (Ok $a) { $gid = [long]$a.answer.data.result.group_id; $tid = [long]$a.answer.data.result.type_id; $created.Add($tid)
                    Case $rest[1] $G 'pass' "group now $gid" } else { Case $rest[1] $G 'fail' (Why $a) }
                $r = & $Ctx.Apply $G @{ operation = 'remove_members'; target_document = $doc; group_ids = @($gid); element_ids = @($free[2]) } 'grp-remove'
                if (Ok $r) { $gid = [long]$r.answer.data.result.group_id; $tid = [long]$r.answer.data.result.type_id; $created.Add($tid)
                    Case $rest[2] $G 'pass' "group now $gid" } else { Case $rest[2] $G 'fail' (Why $r) }
                $rn = & $Ctx.Apply $G @{ operation = 'rename_type'; target_document = $doc; type_id = $tid; name = ($tag + '_B') } 'grp-rename'
                $du = & $Ctx.Apply $G @{ operation = 'duplicate_type'; target_document = $doc; type_id = $tid; name = ($tag + '_C') } 'grp-dup'
                $sw = $null
                if (Ok $du) { $dup = [long]$du.answer.data.result.type_id; $created.Add($dup)
                    $sw = & $Ctx.Apply $G @{ operation = 'swap_type'; target_document = $doc; group_ids = @($gid); type_id = $dup } 'grp-swap' }
                if ((Ok $rn) -and (Ok $du) -and $sw -and (Ok $sw)) { Case $rest[3] $G 'pass' 'rename, duplicate and swap verified' }
                else { Case $rest[3] $G 'fail' ("rename: " + (Why $rn) + " | duplicate: " + (Why $du) + " | swap: " + $(if ($sw) { Why $sw } else { 'not attempted' })) }
                $u = & $Ctx.Apply $G @{ operation = 'ungroup'; target_document = $doc; group_ids = @($gid) } 'grp-ungroup'
                # Redefinition replaces types, so the ids to delete are the probe-named types that exist NOW.
                $now = & $Ctx.Call $G @{ operation = 'list'; target_document = $doc }
                $ids = @(@($now.data.types) | Where-Object { [string]$_.name -like ($tag + '*') } | ForEach-Object { [long]$_.type_id })
                $d = if ($ids.Count -gt 0) { & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = $ids; target_document = $doc } 'grp-cleanup' } else { @{ stage = 'none' } }
                $after = & $Ctx.Call $G @{ operation = 'list'; target_document = $doc }
                $left = @(@($after.data.types) | Where-Object { [string]$_.name -like ($tag + '*') })
                if ((Ok $u) -and $left.Count -eq 0) { Case $rest[4] $G 'pass' ("ungrouped; {0} probe type(s) deleted" -f $ids.Count) }
                else { Case $rest[4] $G 'fail' ("ungroup: " + (Why $u) + " | delete stage=" + $d.stage + " | probe types left: " + $left.Count) }
            } else { foreach ($n in $rest[1..4]) { Case $n $G 'not_covered' 'the probe group was not created' } }
        }

        # ---- worksets -------------------------------------------------------------
        $ws = & $Ctx.Call $W @{ operation = 'list'; target_document = $doc }
        $wcode = if ($ws.data) { $ws.data.code } elseif ($ws.structured) { $ws.structured.code } else { $null }
        if ($ws.isError -and $wcode -eq 'not_workshared') {
            $wr = & $Ctx.Call $W @{ operation = 'create'; target_document = $doc; name = 'HZ_PROBE_WS'; dry_run = $true }
            $wrcode = if ($wr.data) { $wr.data.code } elseif ($wr.structured) { $wr.structured.code } else { $null }
            Case 'worksets: a model that is not workshared is refused typed (not_workshared)' $W $(if ($wr.isError -and $wrcode -eq 'not_workshared') { 'pass' } else { 'fail' }) "list and create both refused: create code=$wrcode"
            Case 'worksets: create, rename and move an element on a workshared model' $W 'not_covered' "'$doc' is not workshared; the run brings no disposable workshared fixture"
        }
        elseif (-not $ws.isError -and $null -ne $ws.data.worksets) {
            Case 'worksets: a model that is not workshared is refused typed (not_workshared)' $W 'not_covered' "'$doc' is workshared, so the refusal cannot be provoked here"
            $name = 'HZ_PROBE_WS_' + $Ctx.RunId
            $cr = & $Ctx.Apply $W @{ operation = 'create'; target_document = $doc; name = $name } 'ws-create'
            $detail = 'create: ' + (Why $cr)
            $okAll = Ok $cr
            if ($okAll) {
                $wid = [int]$cr.answer.data.workset_id
                $rn = & $Ctx.Apply $W @{ operation = 'rename'; target_document = $doc; workset_id = $wid; name = ($name + '_R') } 'ws-rename'
                $okAll = Ok $rn; $detail += ' | rename: ' + (Why $rn)
                if ($okAll -and $free.Count -gt 0) {
                    $mv = & $Ctx.Apply $W @{ operation = 'move_elements'; target_document = $doc; workset_id = $wid; element_ids = @($free[0]) } 'ws-move'
                    $okAll = Ok $mv; $detail += ' | move: ' + (Why $mv)
                    $origin = @($mv.dry.data.plan.move)[0].from_workset_id
                    if ($okAll -and $null -ne $origin) { $null = & $Ctx.Apply $W @{ operation = 'move_elements'; target_document = $doc; workset_id = [int]$origin; element_ids = @($free[0]) } 'ws-move-back' }
                }
            }
            Case 'worksets: create, rename and move an element on a workshared model' $W $(if ($okAll) { 'pass' } else { 'fail' }) ($detail + ' (a created workset cannot be deleted typed; the document is never saved)')
        }
        else {
            Case 'worksets: a model that is not workshared is refused typed (not_workshared)' $W 'fail' ("list neither listed nor refused typed: " + $ws.text)
            Case 'worksets: create, rename and move an element on a workshared model' $W 'unverified' 'list answered neither way'
        }
        return $cases.ToArray()
    }
}
