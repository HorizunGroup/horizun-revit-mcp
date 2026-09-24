# Live probes for horizun_manage_parameters and horizun_query_classification.
# Loaded by scripts/verify-live.ps1 (see README.md); exercised without Revit by
# parameters.tests.ps1. Everything it creates carries the run id and is removed again:
# a shared parameter bound and then unbound, a Global Parameter created and deleted.
# The SPF is a temporary file under ScratchRoot. The document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'parameters'
    Catalog = @(
        @{ Name = 'parameters: list_bindings reads the whole BindingMap';                            Tool = 'horizun_manage_parameters' }
        @{ Name = 'parameters: create_shared writes the SPF, binds, and both re-read';               Tool = 'horizun_manage_parameters' }
        @{ Name = 'parameters: remove_binding withdraws the probe binding and list_bindings agrees'; Tool = 'horizun_manage_parameters' }
        @{ Name = 'parameters: create_project is refused by name, nothing written';                  Tool = 'horizun_manage_parameters' }
        @{ Name = 'parameters: a Global Parameter is created, read back, set and deleted';           Tool = 'horizun_manage_parameters' }
        @{ Name = 'classification: keynote_table reports its source and entries';                    Tool = 'horizun_query_classification' }
        @{ Name = 'classification: family_lookup_tables reads without opening a family';             Tool = 'horizun_query_classification' }
    )
    Run     = {
        param($Ctx)
        $T = 'horizun_manage_parameters'; $Q = 'horizun_query_classification'
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
        $doc = $Ctx.Document
        $tag = ('HZ_PROBE_' + ($Ctx.RunId -replace '[^A-Za-z0-9]', ''))
        if ($tag.Length -gt 40) { $tag = $tag.Substring(0, 40) }

        # 1. list_bindings
        $list = & $Ctx.Call $T @{ operation = 'list_bindings'; target_document = $doc }
        $bindings = @($list.data.bindings)
        Case 'parameters: list_bindings reads the whole BindingMap' $T (-not $list.isError -and $null -ne $list.data.count -and [int]$list.data.count -eq $bindings.Count) ("count=" + $list.data.count)

        # 4. create_project - a refusal, not a gap
        $cp = & $Ctx.Call $T @{ operation = 'create_project'; target_document = $doc; name = $tag; data_type = 'Text'; categories = @('OST_Walls') }
        Case 'parameters: create_project is refused by name, nothing written' $T ($cp.isError -and ([string]$cp.text) -match 'non-shared') ([string]$cp.text)

        if ($Ctx.WriteGate) {
            foreach ($n in 'parameters: create_shared writes the SPF, binds, and both re-read',
                           'parameters: remove_binding withdraws the probe binding and list_bindings agrees',
                           'parameters: a Global Parameter is created, read back, set and deleted') { Skip $n $T 'write tier closed' }
        }
        else {
            # 2. create_shared into a temporary SPF, on a category the document offers.
            $spf = Join-Path $Ctx.ScratchRoot ($tag + '.txt')
            $category = 'OST_Walls'
            $created = & $Ctx.Apply $T @{ operation = 'create_shared'; target_document = $doc; name = $tag; spf_path = $spf;
                                          spf_group = 'HorizunProbe'; data_type = 'Text'; categories = @($category); binding_kind = 'Instance' } ($tag + '-create')
            $after = & $Ctx.Call $T @{ operation = 'list_bindings'; target_document = $doc }
            $row = @($after.data.bindings | Where-Object { $_.name -eq $tag })
            $onDisk = (Test-Path -LiteralPath $spf) -and ((Get-Content -LiteralPath $spf -Raw) -match [regex]::Escape($tag))
            $okCreate = (Verified $created) -and $row.Count -eq 1 -and $row[0].shared -eq $true -and $onDisk
            Case 'parameters: create_shared writes the SPF, binds, and both re-read' $T $okCreate ((Why $created) + " listed=" + $row.Count + " spf=" + $onDisk)

            # 3. remove_binding - always attempted when the binding exists, so the fixture is left as found.
            if ($row.Count -eq 1) {
                $removed = & $Ctx.Apply $T @{ operation = 'remove_binding'; target_document = $doc; guid = $row[0].guid } ($tag + '-remove')
                $gone = & $Ctx.Call $T @{ operation = 'list_bindings'; target_document = $doc }
                $still = @($gone.data.bindings | Where-Object { $_.name -eq $tag }).Count
                Case 'parameters: remove_binding withdraws the probe binding and list_bindings agrees' $T ((Verified $removed) -and $still -eq 0) ((Why $removed) + " still_listed=" + $still)
            }
            else { Case 'parameters: remove_binding withdraws the probe binding and list_bindings agrees' $T $false 'nothing to remove: create_shared did not bind' }
            if (Test-Path -LiteralPath $spf) { Remove-Item -LiteralPath $spf -Force }

            # 5. Global Parameter create / read / set / delete
            $g = $tag + '_G'
            $gc = & $Ctx.Apply $T @{ operation = 'global_create'; target_document = $doc; name = $g; data_type = 'Length'; value = 1500 } ($tag + '-gcreate')
            $gl = & $Ctx.Call $T @{ operation = 'global_list'; target_document = $doc }
            $gRow = @($gl.data.globals | Where-Object { $_.name -eq $g })
            $readBack = $gRow.Count -eq 1 -and $null -ne $gRow[0].value -and [math]::Abs([double]$gRow[0].value.display - 1500) -lt 1e-6
            $gs = $null; $gd = $null
            if ($gRow.Count -eq 1) {
                $gs = & $Ctx.Apply $T @{ operation = 'global_set'; target_document = $doc; name = $g; value = 2500 } ($tag + '-gset')
                $gd = & $Ctx.Apply $T @{ operation = 'global_delete'; target_document = $doc; name = $g } ($tag + '-gdelete')
            }
            $gl2 = & $Ctx.Call $T @{ operation = 'global_list'; target_document = $doc }
            $left = @($gl2.data.globals | Where-Object { $_.name -eq $g }).Count
            $okGlobal = (Verified $gc) -and $readBack -and (Verified $gs) -and (Verified $gd) -and $left -eq 0
            Case 'parameters: a Global Parameter is created, read back, set and deleted' $T $okGlobal ("create: " + (Why $gc) + " | read_back=" + $readBack + " | set: " + (Why $gs) + " | delete: " + (Why $gd) + " | left=" + $left)
        }

        # 6. keynote table
        $kn = & $Ctx.Call $Q @{ operation = 'keynote_table'; target_document = $doc; max_rows = 50 }
        # Measured 2026-09-24: in 2024/2025 this case failed with an empty detail - the
        # reply's own text was dropped. The detail now carries it, and a model whose
        # keynote table is not present is not_covered with the reason, never a pass.
        $knText = [string]$kn.text
        if ($knText.Length -gt 400) { $knText = $knText.Substring(0, 400) }
        if (-not $kn.isError -and $kn.data -and $kn.data.source -and $kn.data.source.present -eq $false) {
            Skip 'classification: keynote_table reports its source and entries' $Q 'this fixture has no keynote table element (source.present=false)'
        }
        else {
            Case 'classification: keynote_table reports its source and entries' $Q (-not $kn.isError -and $null -ne $kn.data.source -and $null -ne $kn.data.table_entries -and $kn.data.read_only -eq $true) ("error=" + $kn.isError + " entries=" + $kn.data.table_entries + " path=" + $kn.data.source.path + " text=" + $knText)
        }

        # 7. lookup tables
        $lt = & $Ctx.Call $Q @{ operation = 'family_lookup_tables'; target_document = $doc; max_rows = 20 }
        Case 'classification: family_lookup_tables reads without opening a family' $Q (-not $lt.isError -and $null -ne $lt.data.families_scanned) ("scanned=" + $lt.data.families_scanned + " with_tables=" + @($lt.data.families).Count)

        return $out.ToArray()
    }
}
