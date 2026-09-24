# Live probes: horizun_code_check (the three Colombian example sets),
# horizun_link_schedule (import -> match -> write -> status_view on the probe's OWN
# walls) and horizun_federation_check (synthetic rules). Loaded by verify-live.ps1;
# exercised without Revit by code-checks-4d-federation.tests.ps1.
#
# Everything the probe creates (two walls, one plan, the 4D view) is deleted at the
# end; the disposable document is never saved.
$script:HzCc4dStandards = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'standards'

$script:HzProbeModules += [pscustomobject]@{
    Name    = 'code-checks-4d-federation'
    Catalog = @(
        @{ Name = 'code-check: the NTC 6047 set runs and every rule reports a verdict'; Tool = 'horizun_code_check' }
        @{ Name = 'code-check: the NSR-10 set passes nothing while its thresholds are unverified'; Tool = 'horizun_code_check' }
        @{ Name = 'code-check: the RETILAP set runs and a rule that examined nothing is not_decidable'; Tool = 'horizun_code_check' }
        @{ Name = 'link-schedule: import parses the synthetic CSV'; Tool = 'horizun_link_schedule' }
        @{ Name = 'link-schedule: match links exactly the two probe walls by Mark'; Tool = 'horizun_link_schedule' }
        @{ Name = 'link-schedule: write stamps Comments and re-reads every row'; Tool = 'horizun_link_schedule' }
        @{ Name = 'link-schedule: status_view colours a duplicated view and re-reads each override'; Tool = 'horizun_link_schedule' }
        @{ Name = 'federation-check: synthetic rules classify the host and answer every link'; Tool = 'horizun_federation_check' }
        @{ Name = 'federation-check: malformed rules are refused'; Tool = 'horizun_federation_check' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = $detail }) }
        function Short($a) { if ($null -eq $a) { return '(no answer)' }; $t = [string]$a.text; if ($t.Length -gt 300) { $t.Substring(0, 300) } else { $t } }
        $valid = @('passes', 'fails', 'not_decidable')
        $doc = $Ctx.Document

        # ---- 1. code checks: read-only, need only a document ----------------------------
        $n1 = 'code-check: the NTC 6047 set runs and every rule reports a verdict'
        $n2 = 'code-check: the NSR-10 set passes nothing while its thresholds are unverified'
        $n3 = 'code-check: the RETILAP set runs and a rule that examined nothing is not_decidable'
        $sets = @(
            @{ N = $n1; F = 'co-ntc6047-accesibilidad.json' }, @{ N = $n2; F = 'co-nsr10-titulo-k-evacuacion.json' },
            @{ N = $n3; F = 'co-retilap-iluminancia.json' })
        foreach ($s in $sets) {
            $path = Join-Path $script:HzCc4dStandards $s.F
            if (-not (Test-Path -LiteralPath $path)) { Case $s.N 'horizun_code_check' 'unverified' "HARNESS: $path not found"; continue }
            $r = & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; requirement_set_path = $path; max_findings = 50 }
            if ($r.isError -or -not $r.data) { Case $s.N 'horizun_code_check' 'fail' ('refused or crashed: ' + (Short $r)); continue }
            $rules = @($r.data.rules)
            $bad = @($rules | Where-Object { $valid -notcontains [string]$_.verdict })
            $emptyPass = @($rules | Where-Object { [int]$_.examined -eq 0 -and $_.verdict -ne 'not_decidable' })
            $ok = $rules.Count -gt 0 -and $bad.Count -eq 0 -and $emptyPass.Count -eq 0
            if ($s.N -eq $n2) { $ok = $ok -and [int]$r.data.totals.passes -eq 0 -and [int]$r.data.totals.fails -eq 0 }
            $summary = ('{0} rules, verdict {1}, totals {2}' -f $rules.Count, $r.data.verdict, ($r.data.totals | ConvertTo-Json -Compress))
            Case $s.N 'horizun_code_check' $(if ($ok) { 'pass' } else { 'fail' }) $summary
        }

        # ---- 3. federation: read-only ------------------------------------------------------
        $f1 = 'federation-check: synthetic rules classify the host and answer every link'
        $f2 = 'federation-check: malformed rules are refused'
        $fr = & $Ctx.Call 'horizun_federation_check' @{ target_document = $doc; rules = @{
                    models = @(@{ match = '$host'; discipline = 'PROBE'; forbidden_categories = @('OST_ProbeNothing') })
                    expected_links = @(@{ name_matches = '^HZ_NO_SUCH_LINK_' + $Ctx.RunId }); same_site = $true } }
        if ($fr.isError -or -not $fr.data) { Case $f1 'horizun_federation_check' 'fail' ('refused or crashed: ' + (Short $fr)) }
        else {
            $hostRow = @($fr.data.models) | Where-Object { $_.host -eq $true } | Select-Object -First 1
            $missing = @($fr.data.expected_links) | Where-Object { $_.state -eq 'missing' }
            $siteOk = @($fr.data.site).Count -eq @($fr.data.links).Count
            $ok = $hostRow -and $hostRow.state -eq 'clean' -and @($missing).Count -eq 1 -and $siteOk -and $fr.data.verdict -eq 'fails'
            Case $f1 'horizun_federation_check' $(if ($ok) { 'pass' } else { 'fail' }) ('verdict {0}, links {1}, summary {2}' -f $fr.data.verdict, @($fr.data.links).Count, ($fr.data.summary | ConvertTo-Json -Compress))
        }
        $fb = & $Ctx.Call 'horizun_federation_check' @{ target_document = $doc; rules = @{ modles = @() } }
        Case $f2 'horizun_federation_check' $(if ($fb.isError -and ([string]$fb.text) -match 'unknown key') { 'pass' } else { 'fail' }) (Short $fb)

        # ---- 2. link_schedule: import is file-only; the rest needs the write tier ---------
        $l1 = 'link-schedule: import parses the synthetic CSV'
        $l2 = 'link-schedule: match links exactly the two probe walls by Mark'
        $l3 = 'link-schedule: write stamps Comments and re-reads every row'
        $l4 = 'link-schedule: status_view colours a duplicated view and re-reads each override'
        $tag = ('HZ4D_{0}' -f ([string]$Ctx.RunId).Substring(0, [Math]::Min(8, ([string]$Ctx.RunId).Length)))
        $csv = Join-Path $Ctx.ScratchRoot ("$tag.csv")
        Set-Content -LiteralPath $csv -Encoding UTF8 -Value @(
            'id,name,start,finish,wbs,percent_complete',
            "${tag}_A1,Probe walls done,2026-01-01,2026-02-01,1.1,100",
            "${tag}_A2,Probe walls future,2027-01-01,2027-02-01,1.2,0")
        $imp = & $Ctx.Call 'horizun_link_schedule' @{ operation = 'import'; schedule_path = $csv }
        $impOk = -not $imp.isError -and $imp.data -and [int]$imp.data.schedule.activities -eq 2 -and [int]$imp.data.schedule.rejected_rows -eq 0
        Case $l1 'horizun_link_schedule' $(if ($impOk) { 'pass' } else { 'fail' }) (Short $imp)

        if ($Ctx.WriteGate) {
            foreach ($n in @($l2, $l3, $l4)) { Case $n 'horizun_link_schedule' 'not_covered' 'the write tier is gated: the probe cannot stage its own walls' }
            Remove-Item -LiteralPath $csv
            return $cases.ToArray()
        }

        $created = @(); $views = @()
        try {
            $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
            $levelId = if ($lv.data -and @($lv.data.rows).Count -gt 0) { @($lv.data.rows)[0].element_id } else { $null }
            $wt = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); include_types = $true; max_rows = 200; include_links = $false }
            $wallType = if ($wt.data) { @($wt.data.rows | Where-Object { $_.is_element_type })[0].element_id } else { $null }
            if (-not $levelId -or -not $wallType) {
                foreach ($n in @($l2, $l3, $l4)) { Case $n 'horizun_link_schedule' 'unverified' 'the fixture has no level or wall type to stage probe walls' }
                return $cases.ToArray()
            }
            $x = 640000
            $mk = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(
                    @{ kind = 'wall'; start = @($x, 0, 0); end = @(($x + 3000), 0, 0); height = 3000; type_id = $wallType; level_id = $levelId },
                    @{ kind = 'wall'; start = @($x, 2000, 0); end = @(($x + 3000), 2000, 0); height = 3000; type_id = $wallType; level_id = $levelId }) } "$tag-walls"
            if ($mk.stage -eq 'apply' -and -not $mk.answer.isError) { $created = @(@($mk.answer.data.rows) | ForEach-Object { [long]$_.element_id }) }
            if ($created.Count -ne 2) {
                foreach ($n in @($l2, $l3, $l4)) { Case $n 'horizun_link_schedule' 'unverified' ('probe walls not staged: ' + (Short $mk.answer)) }
                return $cases.ToArray()
            }
            $marks = & $Ctx.Apply 'horizun_write_params_verified' @{ target_document = $doc; writes = @(
                    @{ target_id = $created[0]; parameter = 'Mark'; value = "${tag}_A1" },
                    @{ target_id = $created[1]; parameter = 'Mark'; value = "${tag}_A2" }) } "$tag-marks"
            $match = @{ parameter = 'ALL_MODEL_MARK'; key = 'id' }

            $m = & $Ctx.Call 'horizun_link_schedule' @{ operation = 'match'; target_document = $doc; schedule_path = $csv; match = $match }
            $linked = if ($m.data) { @($m.data.links | ForEach-Object { [long]$_.element_id }) } else { @() }
            $mOk = -not $m.isError -and $marks.stage -eq 'apply' -and $linked.Count -eq 2 -and
                   @($linked | Where-Object { $created -contains $_ }).Count -eq 2 -and [int]$m.data.activities_without_elements -eq 0
            Case $l2 'horizun_link_schedule' $(if ($mOk) { 'pass' } else { 'fail' }) (Short $m)

            $w = & $Ctx.Apply 'horizun_link_schedule' @{ operation = 'write'; target_document = $doc; schedule_path = $csv; match = $match
                    write = @{ activity_parameter = 'ALL_MODEL_INSTANCE_COMMENTS' } } "$tag-write"
            $wOk = $w.stage -eq 'apply' -and -not $w.answer.isError -and [int]$w.answer.data.rows_verified -eq 2 -and
                   $w.answer.data.application.state -eq 'verified_applied'
            Case $l3 'horizun_link_schedule' $(if ($wOk) { 'pass' } else { 'fail' }) ('stage ' + $w.stage + ': ' + (Short $w.answer))

            $mv = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'
                    actions = @(@{ operation = 'create_floor_plan'; key = 'hz4d'; name = "${tag}_PLAN"; level_id = $levelId }) } "$tag-plan"
            $planId = if ($mv.stage -eq 'apply' -and $mv.answer.data) { $mv.answer.data.aliases.hz4d } else { $null }
            if ($planId) { $views += [long]$planId }
            if (-not $planId) { Case $l4 'horizun_link_schedule' 'unverified' ('probe plan not staged: ' + (Short $mv.answer)) }
            else {
                $sv = & $Ctx.Apply 'horizun_link_schedule' @{ operation = 'status_view'; target_document = $doc; schedule_path = $csv
                        match = $match; view_id = [long]$planId; as_of = '2026-06-15' } "$tag-status"
                if ($sv.stage -eq 'apply' -and $sv.answer.data -and $sv.answer.data.view_id) { $views += [long]$sv.answer.data.view_id }
                $counts = if ($sv.dry) { $sv.dry.data.status_counts } else { $null }
                $sOk = $sv.stage -eq 'apply' -and -not $sv.answer.isError -and [int]$sv.answer.data.overrides_verified -eq 2 -and
                       $counts -and [int]$counts.done -eq 1 -and [int]$counts.future -eq 1
                Case $l4 'horizun_link_schedule' $(if ($sOk) { 'pass' } else { 'fail' }) ('stage ' + $sv.stage + ': ' + (Short $sv.answer))
            }
        }
        finally {
            $ids = @($created) + @($views)
            if ($ids.Count -gt 0) {
                [void](& $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = $ids; target_document = $doc; id_cap = 20 } "$tag-cleanup")
            }
            if (Test-Path -LiteralPath $csv) { Remove-Item -LiteralPath $csv }
        }
        return $cases.ToArray()
    }
}
