# Live probes for horizun_delete_verified deleting ParameterElement / SharedParameterElement /
# GlobalParameter ids (DeleteCommand.cs CaptureParameterInfo / VerifyParameterBindingsRemoved).
# Loaded by scripts/verify-live.ps1 (see README.md); exercised without Revit by
# delete-parameters.tests.ps1. Each case creates its own probe parameter via
# horizun_manage_parameters and then DELETES it with horizun_delete_verified - the
# delete IS the cleanup. The temporary SPF for the shared parameter is removed either way.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'delete-parameters'
    Catalog = @(
        @{ Name = 'delete_verified: a bound shared parameter is deleted and its BindingMap absence is confirmed'; Tool = 'horizun_delete_verified' }
        @{ Name = 'delete_verified: a Global Parameter is deleted and GlobalParametersManager confirms it is gone'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $P = 'horizun_manage_parameters'; $D = 'horizun_delete_verified'
        $out = New-Object System.Collections.Generic.List[object]
        function Case($name, $tool, $ok, $detail) {
            $out.Add(@{ Name = $name; Tool = $tool; Outcome = $(if ($ok) { 'pass' } else { 'fail' }); Detail = [string]$detail })
        }
        function Skip($name, $tool, $why) { $out.Add(@{ Name = $name; Tool = $tool; Outcome = 'not_covered'; Detail = $why }) }
        function Verified($applied) {
            return ($applied.stage -eq 'apply' -and -not $applied.answer.isError -and
                    $applied.answer.data.state -eq 'committed_verified' -and $applied.answer.data.host_verified -eq $true)
        }
        function Why($applied) {
            if ($null -eq $applied) { return 'no answer' }
            $a = $applied.answer
            return ('stage=' + $applied.stage + ' error=' + $a.isError + ' text=' + ([string]$a.text).Substring(0, [Math]::Min(300, ([string]$a.text).Length)))
        }
        $names = @(
            'delete_verified: a bound shared parameter is deleted and its BindingMap absence is confirmed',
            'delete_verified: a Global Parameter is deleted and GlobalParametersManager confirms it is gone'
        )
        $doc = $Ctx.Document
        $tag = ('HZDP_' + ($Ctx.RunId -replace '[^A-Za-z0-9]', ''))
        if ($tag.Length -gt 40) { $tag = $tag.Substring(0, 40) }

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Skip $n $D 'write tier closed' }
            return $out.ToArray()
        }

        # ---- case 1: shared parameter ----
        $spf = Join-Path $Ctx.ScratchRoot ($tag + '.txt')
        try {
            $created = & $Ctx.Apply $P @{ operation = 'create_shared'; target_document = $doc; name = $tag; spf_path = $spf;
                                           spf_group = 'HorizunProbe'; data_type = 'Text'; categories = @('OST_Walls'); binding_kind = 'Instance' } ($tag + '-create')
            $listed = & $Ctx.Call $P @{ operation = 'list_bindings'; target_document = $doc }
            $row = @($listed.data.bindings | Where-Object { $_.name -eq $tag })
            if (-not (Verified $created) -or $row.Count -ne 1) {
                Case $names[0] $D $false ('setup failed: ' + (Why $created) + ' listed=' + $row.Count)
            }
            else {
                $paramId = $row[0].parameter_element_id
                $del = & $Ctx.Apply $D @{ target_document = $doc; mode = 'ids'; ids = @([int64]$paramId) } ($tag + '-delete')
                $delItem = $null
                if (-not $del.answer.isError -and $del.answer.data.results.items) {
                    $delItem = @($del.answer.data.results.items | Where-Object { $_.id -eq [int64]$paramId }) | Select-Object -First 1
                }
                $listedAfter = & $Ctx.Call $P @{ operation = 'list_bindings'; target_document = $doc }
                $stillThere = @($listedAfter.data.bindings | Where-Object { $_.name -eq $tag }).Count
                $ok = (-not $del.answer.isError) -and ($null -ne $delItem) -and ($delItem.verdict -eq 'deleted') -and
                      ($delItem.parameter_kind -eq 'shared_parameter') -and ($delItem.parameter_binding_confirmed_removed -eq $true) -and
                      ($stillThere -eq 0)
                Case $names[0] $D $ok ('delete: ' + (Why $del) + ' item=' + ($delItem | ConvertTo-Json -Compress -Depth 4) + ' still_in_bindings=' + $stillThere)
                if ($stillThere -gt 0) {
                    # belt-and-suspenders cleanup: the delete did not take, try to at least unbind it.
                    try { & $Ctx.Apply $P @{ operation = 'remove_binding'; target_document = $doc; guid = $row[0].guid } ($tag + '-cleanup-unbind') | Out-Null } catch { }
                }
            }
        }
        finally {
            if (Test-Path -LiteralPath $spf) { Remove-Item -LiteralPath $spf -Force -ErrorAction SilentlyContinue }
        }

        # ---- case 2: global parameter ----
        $g = $tag + '_G'
        $gc = & $Ctx.Apply $P @{ operation = 'global_create'; target_document = $doc; name = $g; data_type = 'Length'; value = 500 } ($tag + '-gcreate')
        $gl = & $Ctx.Call $P @{ operation = 'global_list'; target_document = $doc }
        $gRow = @($gl.data.globals | Where-Object { $_.name -eq $g })
        if (-not (Verified $gc) -or $gRow.Count -ne 1) {
            Case $names[1] $D $false ('setup failed: ' + (Why $gc) + ' listed=' + $gRow.Count)
        }
        else {
            $globalId = $gRow[0].id
            $gdel = & $Ctx.Apply $D @{ target_document = $doc; mode = 'ids'; ids = @([int64]$globalId) } ($tag + '-gdelete')
            $gdelItem = $null
            if (-not $gdel.answer.isError -and $gdel.answer.data.results.items) {
                $gdelItem = @($gdel.answer.data.results.items | Where-Object { $_.id -eq [int64]$globalId }) | Select-Object -First 1
            }
            $gl2 = & $Ctx.Call $P @{ operation = 'global_list'; target_document = $doc }
            $gStillThere = @($gl2.data.globals | Where-Object { $_.name -eq $g }).Count
            $gOk = (-not $gdel.answer.isError) -and ($null -ne $gdelItem) -and ($gdelItem.verdict -eq 'deleted') -and
                   ($gdelItem.parameter_kind -eq 'global_parameter') -and ($gdelItem.parameter_binding_confirmed_removed -eq $true) -and
                   ($gStillThere -eq 0)
            Case $names[1] $D $gOk ('delete: ' + (Why $gdel) + ' item=' + ($gdelItem | ConvertTo-Json -Compress -Depth 4) + ' still_in_globals=' + $gStillThere)
            if ($gStillThere -gt 0) {
                try { & $Ctx.Apply $P @{ operation = 'global_delete'; target_document = $doc; name = $g } ($tag + '-cleanup-gdelete') | Out-Null } catch { }
            }
        }

        return $out.ToArray()
    }
}
