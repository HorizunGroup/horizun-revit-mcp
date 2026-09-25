# Live probes for the 2026-09-25 field audit session: change_type_by_rule and
# realign_wall_sketch (horizun_transform_elements), and the wall_sketch_drift /
# template_comparison / warnings-root_cause additions to horizun_audit_model.
# Loaded by scripts/verify-live.ps1 (see README.md).
#
# WHAT THIS DOES NOT DO, ON PURPOSE: this bridge has no typed way to CREATE a
# wall's edited elevation profile (Wall.SketchId) - only to detect one and
# realign an EXISTING one. Manufacturing a stranded-profile fixture would need
# horizun_execute_python, which is disabled by default and which no probe
# module in this repo uses. So the drift/realign cases below exercise the
# DETERMINISTIC paths that need no special fixture (refusals, opt-in
# skipping, the check's own shape) rather than the full move-detect-correct
# cycle; a wall with a genuinely stranded profile still gets read here if the
# fixture happens to carry one, but this probe never moves or corrects a wall
# it did not create, to avoid altering fixture state it does not own.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'field-audit-typechange'
    Catalog = @(
        @{ Name = 'change_type_by_rule: dry_run measures short_side/long_side_mm and rehearses a same-type else-rule'; Tool = 'horizun_transform_elements' }
        @{ Name = 'change_type_by_rule: apply commits per-instance and verifies by re-read'; Tool = 'horizun_transform_elements' }
        @{ Name = 'change_type_by_rule: an instance matching no rule and no else refuses the whole batch'; Tool = 'horizun_transform_elements' }
        @{ Name = 'realign_wall_sketch: a wall with no edited profile is refused by name, nothing written'; Tool = 'horizun_transform_elements' }
        @{ Name = 'realign_wall_sketch: mixed with another operation in one call is refused'; Tool = 'horizun_transform_elements' }
        @{ Name = 'wall_sketch_drift: horizun_audit_model runs the check and reports its fields'; Tool = 'horizun_audit_model' }
        @{ Name = 'warnings: every warning item carries a root_cause block'; Tool = 'horizun_audit_model' }
        @{ Name = 'template_comparison: opt-in and absent from the findings when not requested'; Tool = 'horizun_audit_model' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $T = 'horizun_transform_elements'; $A = 'horizun_audit_model'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Ok($applied) { $applied.stage -eq 'apply' -and -not $applied.answer.isError }
        function Why($applied) { "stage=$($applied.stage) " + [string]$applied.answer.text }
        $doc = $Ctx.Document
        $allNames = @(
            'change_type_by_rule: dry_run measures short_side/long_side_mm and rehearses a same-type else-rule',
            'change_type_by_rule: apply commits per-instance and verifies by re-read',
            'change_type_by_rule: an instance matching no rule and no else refuses the whole batch',
            'realign_wall_sketch: a wall with no edited profile is refused by name, nothing written',
            'realign_wall_sketch: mixed with another operation in one call is refused',
            'wall_sketch_drift: horizun_audit_model runs the check and reports its fields',
            'warnings: every warning item carries a root_cause block',
            'template_comparison: opt-in and absent from the findings when not requested'
        )
        if ($Ctx.WriteGate) {
            foreach ($n in $allNames) { Case $n $T 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }

        # ---- a free host wall to work against ------------------------------------------
        $walls = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Walls'; include_links = $false; max_rows = 50; target_document = $doc }
        $hostWalls = @(@($walls.data.rows) | Where-Object { $_.source_kind -eq 'host' } | ForEach-Object { [long]$_.element_id })
        if ($hostWalls.Count -eq 0) {
            foreach ($n in @($allNames[0], $allNames[1], $allNames[2])) { Case $n $T 'not_covered' 'the fixture carries no host wall' }
        } else {
            $wid = $hostWalls[0]

            # ---- 1/2: change_type_by_rule, dry_run then apply, same type (a safe no-op content
            # change that still exercises measurement, rule evaluation, ChangeTypeId and re-read).
            $ruleSameType = $null
            $dry1 = & $Ctx.Call $T @{
                target_document = $doc; dry_run = $true
                operations      = @(@{ operation = 'change_type_by_rule'; element_ids = @($wid); rule = @(@{ else = $true; type_id = 0 }) })
            }
            # type_id 0 is a placeholder that will not resolve; read what it measured/refused
            # to learn the wall's OWN current type, then rebuild the rule against it for real.
            # The wall's own type, read - not provoked out of an error message (the refusal
            # wording for type_id 0 is not a contract; measured 2026-09-25).
            $row = @($walls.data.rows) | Where-Object { [long]$_.element_id -eq $wid } | Select-Object -First 1
            $currentType = if ($row -and $row.type_id) { [long]$row.type_id } else { $null }
            if (-not $currentType) {
                $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); element_ids = @($wid); include_links = $false; max_rows = 5 }
                $qr = @($q.data.rows) | Where-Object { [long]$_.element_id -eq $wid } | Select-Object -First 1
                if ($qr -and $qr.type_id) { $currentType = [long]$qr.type_id }
            }
            if ($currentType) {
                $dry2 = & $Ctx.Call $T @{
                    target_document = $doc; dry_run = $true
                    operations      = @(@{ operation = 'change_type_by_rule'; element_ids = @($wid); rule = @(@{ else = $true; type_id = $currentType }) })
                }
                $plan = @($dry2.data.plan)[0]
                $measured = if ($plan) { @($plan.instances)[0].measured } else { $null }
                $ok1 = (-not $dry2.isError) -and $dry2.data.confirmation_token -and $plan -and $plan.instances.Count -eq 1
                Case $allNames[0] $T $(if ($ok1) { 'pass' } else { 'fail' }) ("measured: " + ($measured | ConvertTo-Json -Compress -ErrorAction SilentlyContinue))

                if ($ok1) {
                    $applied = & $Ctx.Apply $T @{
                        target_document = $doc
                        operations      = @(@{ operation = 'change_type_by_rule'; element_ids = @($wid); rule = @(@{ else = $true; type_id = $currentType }) })
                    } 'ctbr-apply'
                    $verified = (Ok $applied) -and $applied.answer.data.operations_verified -eq 1
                    Case $allNames[1] $T $(if ($verified) { 'pass' } else { 'fail' }) (Why $applied)
                } else { Case $allNames[1] $T 'not_covered' 'the dry_run rehearsal did not pass' }

                # ---- 3: no rule and no else -> refused, nothing written.
                $dry3 = & $Ctx.Call $T @{
                    target_document = $doc; dry_run = $true
                    operations      = @(@{ operation = 'change_type_by_rule'; element_ids = @($wid); rule = @(@{ when = @{ short_side_mm = @{ lt = -1 } }; type_id = $currentType }) })
                }
                $refused3 = ($dry3.isError -and $dry3.text -match 'matched no rule') -or
                            (-not $dry3.isError -and [int]$dry3.data.valid_operations -eq 0 -and [int]$dry3.data.invalid_operations -eq 1 -and
                             [string](@($dry3.data.errors)[0].error) -match 'matched no rule' -and -not $dry3.data.confirmation_token)
                Case $allNames[2] $T $(if ($refused3) { 'pass' } else { 'fail' }) ("isError=$($dry3.isError) text=$($dry3.text)")
            } else {
                foreach ($n in @($allNames[0], $allNames[1], $allNames[2])) { Case $n $T 'unverified' ("could not learn the wall's own current type: " + $dry1.text) }
            }

            # ---- 4: realign_wall_sketch refuses a wall with no edited profile, by name.
            $r4 = & $Ctx.Call $T @{ target_document = $doc; dry_run = $true; operations = @(@{ operation = 'realign_wall_sketch'; element_ids = @($wid) }) }
            # A rehearsal whose only row is invalid answers with the row's error, not a tool
            # error (same shape as every other transform operation).
            $refused4 = ($r4.isError -and $r4.text -match 'no edited profile') -or
                        (-not $r4.isError -and [int]$r4.data.invalid_targets -eq 1 -and [int]$r4.data.valid_targets -eq 0 -and
                         [string](@($r4.data.errors)[0].error) -match 'no edited profile')
            Case $allNames[3] $T $(if ($refused4) { 'pass' } else { 'fail' }) ("isError=$($r4.isError) text=$($r4.text)")

            # ---- 5: realign_wall_sketch mixed with another operation is refused outright.
            $r5 = & $Ctx.Call $T @{
                target_document = $doc; dry_run = $true
                operations      = @(
                    @{ operation = 'realign_wall_sketch'; element_ids = @($wid) },
                    @{ operation = 'pin'; element_ids = @($wid) }
                )
            }
            $refused5 = $r5.isError -and $r5.text -match 'cannot be mixed'
            Case $allNames[4] $T $(if ($refused5) { 'pass' } else { 'fail' }) ("isError=$($r5.isError) text=$($r5.text)")
        }

        # ---- 6: wall_sketch_drift runs as part of the ordinary audit and reports its shape.
        $audit = & $Ctx.Call $A @{ target_document = $doc; top = 20 }
        $wsd = $null
        if (-not $audit.isError) { $wsd = @($audit.data.findings) | Where-Object { $_.check -eq 'wall_sketch_drift' } | Select-Object -First 1 }
        $ok6 = $wsd -and ($null -ne $wsd.count) -and ($null -ne $wsd.summary)
        Case $allNames[5] $A $(if ($ok6) { 'pass' } else { 'fail' }) ("count=$($wsd.count) checks_failed=" + ($audit.data.checks_failed | ConvertTo-Json -Compress -ErrorAction SilentlyContinue))

        # ---- 7: every warning item carries a root_cause block (even when 'unclassified').
        $warnings = if (-not $audit.isError) { @($audit.data.findings) | Where-Object { $_.check -eq 'warnings' } | Select-Object -First 1 } else { $null }
        if ($warnings -and @($warnings.items).Count -gt 0) {
            $missing = @(@($warnings.items) | Where-Object { -not $_.root_cause -or -not $_.root_cause.cause })
            Case $allNames[6] $A $(if ($missing.Count -eq 0) { 'pass' } else { 'fail' }) ("{0} warning item(s), {1} missing root_cause" -f @($warnings.items).Count, $missing.Count)
        } else {
            Case $allNames[6] $A 'not_covered' 'the fixture carries no warnings to classify'
        }

        # ---- 8: template_comparison is opt-in - absent when neither path is given.
        $noTemplate = & $Ctx.Call $A @{ target_document = $doc; top = 5 }
        $tc = if (-not $noTemplate.isError) { @($noTemplate.data.findings) | Where-Object { $_.check -eq 'template_comparison' } } else { @() }
        Case $allNames[7] $A $(if (@($tc).Count -eq 0) { 'pass' } else { 'fail' }) ("template_comparison present without being requested: {0}" -f (@($tc).Count -gt 0))

        return $cases.ToArray()
    }
}
