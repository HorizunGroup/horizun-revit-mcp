# Live probes: horizun_manage_phases and horizun_manage_assemblies_parts.
#
# Runs inside the write tier of verify-live.ps1 on the disposable document. It
# discovers a level and a wall type, creates THREE walls of its own far from
# everything (x >= 720 m), exercises the tools on them and deletes them again.
# Nothing it did not create is written; the document is never saved.
#
# $Ctx.WriteGate is verify-live's $writeGate cast to bool: TRUE means the gate is
# CLOSED (verify-live keeps the reason there), so every write case is not_covered.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'phases-options-parts'
    Catalog = @(
        @{ Name = 'phases: list reads phases in order and every phase filter with four presentations'; Tool = 'horizun_manage_phases' }
        @{ Name = 'phases: create_phase is a typed refusal with the API reason and writes nothing'; Tool = 'horizun_manage_phases' }
        @{ Name = 'phases: set_element_phases on a probe wall commits, re-reads, and is restored'; Tool = 'horizun_manage_phases' }
        @{ Name = 'design options: list reports option sets, options and the primary'; Tool = 'horizun_manage_phases' }
        @{ Name = 'parts: create parts from a probe wall, re-read them, then dissolve them'; Tool = 'horizun_manage_assemblies_parts' }
        @{ Name = 'assemblies: assembly of two probe walls re-reads its members, then disassembles'; Tool = 'horizun_manage_assemblies_parts' }
    )
    Run     = {
        param($Ctx)
        $P = 'horizun_manage_phases'; $A = 'horizun_manage_assemblies_parts'
        $cases = @()
        function Case($name, $tool, $outcome, $detail) { [pscustomobject]@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = $detail } }
        function Short($r) { if ($null -eq $r) { return 'no reply' }; $t = [string]$r.text; if ($t.Length -gt 300) { $t.Substring(0, 300) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data.state -eq 'committed_verified' -and $r.answer.data.postconditions.all_verified -eq $true }
        $doc = $Ctx.Document
        $names = @(
            'phases: list reads phases in order and every phase filter with four presentations'
            'phases: create_phase is a typed refusal with the API reason and writes nothing'
            'phases: set_element_phases on a probe wall commits, re-reads, and is restored'
            'design options: list reports option sets, options and the primary'
            'parts: create parts from a probe wall, re-read them, then dissolve them'
            'assemblies: assembly of two probe walls re-reads its members, then disassembles'
        )

        # ---- 1. list (read) ------------------------------------------------------
        $list = & $Ctx.Call $P @{ operation = 'list'; target_document = $doc }
        $phases = @(); $options = @()
        if ($list.isError -or -not $list.data) {
            $cases += Case $names[0] $P 'unverified' ('list did not answer: ' + (Short $list))
        }
        else {
            $phases = @($list.data.phases); $filters = @($list.data.phase_filters); $options = @($list.data.design_options)
            $ordered = $true
            for ($i = 0; $i -lt $phases.Count; $i++) { if ([int]$phases[$i].index -ne $i) { $ordered = $false } }
            $fourEach = $filters.Count -gt 0
            foreach ($f in $filters) {
                foreach ($s in 'new', 'existing', 'demolished', 'temporary') {
                    if (@('by_category', 'overridden', 'hidden') -notcontains [string]$f.presentation.$s) { $fourEach = $false }
                }
            }
            $ok = $phases.Count -ge 1 -and $ordered -and $fourEach -and $list.data.read_only -eq $true
            $cases += Case $names[0] $P $(if ($ok) { 'pass' } else { 'fail' }) ("{0} phases, {1} phase filters, ordered={2}, all presentations readable={3}" -f $phases.Count, $filters.Count, $ordered, $fourEach)
        }

        # ---- 2. create_phase refusal (no write) -------------------------------------
        $cp = & $Ctx.Call $P @{ operation = 'create_phase'; target_document = $doc; name = 'HZ probe phase' }
        $okRefusal = $cp.isError -and ([string]$cp.text -match 'no_phase_creation_api') -and -not ([string]$cp.text -match '"allowed":\s*true')
        $cases += Case $names[1] $P $(if ($okRefusal) { 'pass' } else { 'fail' }) (Short $cp)

        # ---- 4. design options (read) -------------------------------------------
        if ($list.isError -or -not $list.data) {
            $cases += Case $names[3] $P 'unverified' 'list did not answer'
        }
        elseif ($options.Count -eq 0) {
            $cases += Case $names[3] $P 'not_covered' 'the disposable document has no design options; the read path answered with an empty list and design_options_writable=false'
        }
        else {
            $sets = @($options | ForEach-Object { $_.option_set_id } | Sort-Object -Unique)
            $primaryPerSet = $true
            foreach ($s in $sets) { if (@($options | Where-Object { $_.option_set_id -eq $s -and $_.is_primary }).Count -ne 1) { $primaryPerSet = $false } }
            $ok = $primaryPerSet -and $list.data.design_options_writable -eq $false
            $cases += Case $names[3] $P $(if ($ok) { 'pass' } else { 'fail' }) ("{0} options in {1} sets, one primary per set={2}" -f $options.Count, $sets.Count, $primaryPerSet)
        }

        # ---- writes -------------------------------------------------------------
        if ($Ctx.WriteGate) {
            foreach ($n in $names[2], $names[4], $names[5]) { $cases += Case $n $(if ($n -like 'phases*') { $P } else { $A }) 'not_covered' 'the write gate is closed (see the write tier reason)' }
            return $cases
        }
        $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
        $levelId = if ($lv.data -and @($lv.data.rows).Count -gt 0) { @($lv.data.rows)[0].element_id } else { $null }
        $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); include_types = $true; max_rows = 200; include_links = $false }
        $wallType = if ($q.data) { @($q.data.rows | Where-Object { $_.is_element_type -and $_.family -match 'Basic|B.sico' } | Select-Object -First 1).element_id } else { $null }
        if (-not $wallType -and $q.data) { $wallType = @($q.data.rows | Where-Object { $_.is_element_type } | Select-Object -First 1).element_id }
        $walls = @()
        if ($levelId -and $wallType) {
            foreach ($k in 0, 1, 2) {
                $x = 720000 + 6000 * $k
                $mk = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                    elements = @(@{ kind = 'wall'; start = @($x, 0, 0); end = @(($x + 4000), 0, 0); height = 3000; type_id = [long]$wallType; level_id = [long]$levelId }) } ("pop-wall-$k-" + $Ctx.RunId)
                if ($mk.stage -eq 'apply' -and -not $mk.answer.isError) { $walls += [long]@($mk.answer.data.rows)[0].element_id }
            }
        }
        if ($walls.Count -ne 3) {
            $why = "could not stage three probe walls (level=$levelId, wall type=$wallType, created=$($walls.Count))"
            foreach ($n in $names[2], $names[4], $names[5]) { $cases += Case $n $(if ($n -like 'phases*') { $P } else { $A }) 'unverified' $why }
        }
        else {
            try {
                # ---- 3. set_element_phases, restored -------------------------------
                if ($phases.Count -lt 2) {
                    $cases += Case $names[2] $P 'not_covered' 'the disposable document has fewer than two phases'
                }
                else {
                    $st = & $Ctx.Call $P @{ operation = 'element_status'; target_document = $doc; element_ids = @($walls[0]) }
                    $row = if ($st.data) { @($st.data.rows)[0] } else { $null }
                    if (-not $row -or -not $row.has_phases) {
                        $cases += Case $names[2] $P 'unverified' ('element_status did not read the probe wall: ' + (Short $st))
                    }
                    else {
                        $origC = [long]$row.created_phase_id; $origD = [long]$row.demolished_phase_id
                        $first = [long]$phases[0].id; $last = [long]$phases[$phases.Count - 1].id
                        $set = & $Ctx.Apply $P @{ operation = 'set_element_phases'; target_document = $doc; element_ids = @($walls[0]); created_phase_id = $first; demolished_phase_id = $last } ("pop-phase-" + $Ctx.RunId)
                        $after = & $Ctx.Call $P @{ operation = 'element_status'; target_document = $doc; element_ids = @($walls[0]); phase_id = $last }
                        $ar = if ($after.data) { @($after.data.rows)[0] } else { $null }
                        $reread = $ar -and [long]$ar.created_phase_id -eq $first -and [long]$ar.demolished_phase_id -eq $last -and $ar.status -eq 'demolished'
                        $restore = & $Ctx.Apply $P @{ operation = 'set_element_phases'; target_document = $doc; element_ids = @($walls[0]); created_phase_id = $origC; demolished_phase_id = $origD } ("pop-phase-restore-" + $Ctx.RunId)
                        $ok = (Applied $set) -and $reread -and (Applied $restore)
                        $cases += Case $names[2] $P $(if ($ok) { 'pass' } else { 'fail' }) ("set={0}; status in last phase={1}; restore={2}" -f $(if (Applied $set) { 'verified' } else { Short $set.answer }), $ar.status, $(if (Applied $restore) { 'verified' } else { Short $restore.answer }))
                    }
                }

                # ---- 5. parts create + dissolve ---------------------------------------
                $cr = & $Ctx.Apply $A @{ operation = 'create_parts'; target_document = $doc; element_ids = @($walls[0]) } ("pop-parts-" + $Ctx.RunId)
                $lp = & $Ctx.Call $A @{ operation = 'list'; target_document = $doc; element_ids = @($walls[0]) }
                $partIds = if ($lp.data) { @(@($lp.data.elements)[0].part_ids) } else { @() }
                $ds = $null
                if (Applied $cr) { $ds = & $Ctx.Apply $A @{ operation = 'dissolve_parts'; target_document = $doc; element_ids = @($walls[0]) } ("pop-dissolve-" + $Ctx.RunId) }
                $ok = (Applied $cr) -and $partIds.Count -ge 1 -and $ds -and (Applied $ds)
                $cases += Case $names[4] $A $(if ($ok) { 'pass' } else { 'fail' }) ("create={0}; parts re-listed={1}; dissolve={2}" -f $(if (Applied $cr) { 'verified' } else { Short $cr.answer }), $partIds.Count, $(if ($ds -and (Applied $ds)) { 'verified' } elseif ($ds) { Short $ds.answer } else { 'not attempted' }))

                # ---- 6. assembly create + disassemble --------------------------------
                $ca = & $Ctx.Apply $A @{ operation = 'create_assembly'; target_document = $doc; element_ids = @($walls[1], $walls[2]); name = ('HZ probe assembly ' + $Ctx.RunId) } ("pop-asm-" + $Ctx.RunId)
                $asmId = if (Applied $ca) { [long]$ca.answer.data.result.id } else { $null }
                $members = if (Applied $ca) { @($ca.answer.data.result.member_ids | ForEach-Object { [long]$_ } | Sort-Object) } else { @() }
                $da = $null
                if ($asmId) { $da = & $Ctx.Apply $A @{ operation = 'disassemble'; target_document = $doc; assembly_id = $asmId } ("pop-disasm-" + $Ctx.RunId) }
                $want = @($walls[1], $walls[2] | Sort-Object)
                $ok = $asmId -and (($members -join ',') -eq ($want -join ',')) -and $da -and (Applied $da)
                $cases += Case $names[5] $A $(if ($ok) { 'pass' } else { 'fail' }) ("create={0}; members={1}; disassemble={2}" -f $(if (Applied $ca) { 'verified' } else { Short $ca.answer }), ($members -join ','), $(if ($da -and (Applied $da)) { 'verified' } elseif ($da) { Short $da.answer } else { 'not attempted' }))
            }
            finally {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = $walls; target_document = $doc; id_cap = 10 } ("pop-cleanup-" + $Ctx.RunId)
                if ($del.stage -ne 'apply' -or $del.answer.isError) { Write-Warning ('phases-options-parts: probe walls not deleted: ' + (Short $del.answer)) }
            }
        }
        return $cases
    }
}
