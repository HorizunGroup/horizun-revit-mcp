# Live probes for horizun_manage_groups and horizun_manage_worksets.
# Loaded by scripts/verify-live.ps1 (see README.md). Runs on the disposable write
# document; creates its own group from walls it finds, redefines it, and removes
# every group type it created. Worksets need a workshared model: on one that is not
# (the HZ_WRITE fixture) the typed refusal is probed and the write cases are
# reported not_covered with the reason - unless the run names a closed-workset fixture,
# opened DETACHED as the disposable workshared model (workshared-fixture.lib.ps1).
. (Join-Path $PSScriptRoot 'workshared-fixture.lib.ps1')
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
        function Why($applied) {
            # A refused regeneration names the deletions it judged: they are the diagnosis.
            $del = @()
            foreach ($src in @($applied.answer.structured, $applied.answer.data)) { if ($src) { $del += @($src.deletions); $del += @($src.detail.deletions); $del += @($src.rehearsal.deleted_elements) } }
            $del = @($del | Where-Object { $_ })
            $dtext = if ($del.Count) { ' deletions=' + ($del | ConvertTo-Json -Compress -Depth 4) } else {
                # The rehearsal block may sit deeper: show what follows deleted_elements wherever it is.
                $all = [string]$applied.answer.text + ' ' + [string]($applied.answer.structured | ConvertTo-Json -Depth 25 -Compress)
                $at = $all.IndexOf('deleted_elements')
                if ($at -ge 0) { ' ' + $all.Substring($at, [math]::Min(1500, $all.Length - $at)) } else { ' (no deleted_elements anywhere in the reply)' } }
            "stage=$($applied.stage) " + [string]$applied.answer.text + $dtext }
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
                # add_members regenerates every member, and the members are fixture walls with no Horizun mark of
                # this user: the guard refuses that unless the owner agreed. The release fixture is this run's own
                # disposable model, so the probe says so (accept_member_regeneration) instead of borrowing a pass.
                $a = & $Ctx.Apply $G @{ operation = 'add_members'; target_document = $doc; group_ids = @($gid); element_ids = @($free[2]); accept_member_regeneration = $true } 'grp-add'
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
        # create, rename and move one element into the new workset and back, on $target.
        function Invoke-WsWrites($target, $freeId) {
            $name = 'HZ_PROBE_WS_' + $Ctx.RunId
            $cr = & $Ctx.Apply $W @{ operation = 'create'; target_document = $target; name = $name } 'ws-create'
            $detail = 'on ' + $target + ' | create: ' + (Why $cr)
            $okAll = Ok $cr
            if ($okAll) {
                $wid = [int]$cr.answer.data.workset_id
                $rn = & $Ctx.Apply $W @{ operation = 'rename'; target_document = $target; workset_id = $wid; name = ($name + '_R') } 'ws-rename'
                $okAll = Ok $rn; $detail += ' | rename: ' + (Why $rn)
                if ($okAll -and $freeId) {
                    $mv = & $Ctx.Apply $W @{ operation = 'move_elements'; target_document = $target; workset_id = $wid; element_ids = @($freeId) } 'ws-move'
                    $okAll = Ok $mv; $detail += ' | move: ' + (Why $mv)
                    $origin = @($mv.dry.data.plan.move)[0].from_workset_id
                    if ($okAll -and $null -ne $origin) { $null = & $Ctx.Apply $W @{ operation = 'move_elements'; target_document = $target; workset_id = [int]$origin; element_ids = @($freeId) } 'ws-move-back' }
                }
                elseif ($okAll) { $okAll = $false; $detail += ' | move: no free host element to move' }
            }
            Case 'worksets: create, rename and move an element on a workshared model' $W $(if ($okAll) { 'pass' } else { 'fail' }) ($detail + ' (a created workset cannot be deleted typed; the document is never saved)')
        }

        $ws = & $Ctx.Call $W @{ operation = 'list'; target_document = $doc }
        $wcode = if ($ws.data) { $ws.data.code } elseif ($ws.structured) { $ws.structured.code } else { $null }
        if ($ws.isError -and $wcode -eq 'not_workshared') {
            $wr = & $Ctx.Call $W @{ operation = 'create'; target_document = $doc; name = 'HZ_PROBE_WS'; dry_run = $true }
            $wrcode = if ($wr.data) { $wr.data.code } elseif ($wr.structured) { $wr.structured.code } else { $null }
            Case 'worksets: a model that is not workshared is refused typed (not_workshared)' $W $(if ($wr.isError -and $wrcode -eq 'not_workshared') { 'pass' } else { 'fail' }) "list and create both refused: create code=$wrcode"
            # The write document is not workshared: open the year's closed-workset fixture
            # DETACHED as the disposable workshared model, and close it without saving.
            $fixture = Enter-HzWorksharedFixture $Ctx 'grp'
            if (-not $fixture.Title) {
                Case 'worksets: create, rename and move an element on a workshared model' $W 'not_covered' ("'$doc' is not workshared and " + $fixture.Why)
            }
            else {
                try { Invoke-WsWrites $fixture.Title (Get-HzFreeHostElement $Ctx) }
                finally { $null = Exit-HzWorksharedFixture $Ctx $fixture 'grp' }
            }
        }
        elseif (-not $ws.isError -and $null -ne $ws.data.worksets) {
            # The release gate's write model is a workshared central (MEASURED 2026-09-27): the
            # refusal is provoked on a scratch copy of the run's link source, a plain model,
            # opened as the active document and closed without saving.
            $nNot = 'worksets: a model that is not workshared is refused typed (not_workshared)'
            $srcFile = if ($Ctx.PSObject.Properties['LinkSourceFile']) { [string]$Ctx.LinkSourceFile } else { '' }
            if (-not $srcFile -or -not (Test-Path -LiteralPath $srcFile)) {
                Case $nNot $W 'not_covered' "'$doc' is workshared and the run names no -LinkSourceFile to open a plain model from"
            }
            else {
                New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
                $plain = Join-Path $Ctx.ScratchRoot ('HZ_PLAIN_' + ($Ctx.RunId -replace '[^A-Za-z0-9]', '') + '.rvt')
                Copy-Item -LiteralPath $srcFile -Destination $plain -Force
                $pf = Enter-HzFixtureFile $Ctx $plain 'grp-plain' $null
                if (-not $pf.Title) { Case $nNot $W 'not_covered' ("'$doc' is workshared and the plain copy did not open: " + $pf.Why) }
                else {
                    try {
                        $pl = & $Ctx.Call $W @{ operation = 'list'; target_document = $pf.Title }
                        $plc = if ($pl.data) { $pl.data.code } elseif ($pl.structured) { $pl.structured.code } else { $null }
                        if (-not $pl.isError -and $null -ne $pl.data.worksets) { Case $nNot $W 'not_covered' "the link source copy '$($pf.Title)' is workshared too" }
                        else {
                            $pw = & $Ctx.Call $W @{ operation = 'create'; target_document = $pf.Title; name = 'HZ_PROBE_WS'; dry_run = $true }
                            $pwc = if ($pw.data) { $pw.data.code } elseif ($pw.structured) { $pw.structured.code } else { $null }
                            Case $nNot $W $(if ($pl.isError -and $plc -eq 'not_workshared' -and $pw.isError -and $pwc -eq 'not_workshared') { 'pass' } else { 'fail' }) "on the plain copy '$($pf.Title)': list code=$plc, create code=$pwc"
                        }
                    }
                    finally { $null = Exit-HzWorksharedFixture $Ctx $pf 'grp-plain' }
                }
            }
            Invoke-WsWrites $doc $(if ($free.Count -gt 0) { $free[0] } else { Get-HzFreeHostElement $Ctx })
        }
        else {
            Case 'worksets: a model that is not workshared is refused typed (not_workshared)' $W 'fail' ("list neither listed nor refused typed: " + $ws.text)
            Case 'worksets: create, rename and move an element on a workshared model' $W 'unverified' 'list answered neither way'
        }
        return $cases.ToArray()
    }
}
