# Impact preview (MCP App ui://horizun/impact-preview) - live probe module.
#
# The app renders the REHEARSAL replies of five bulk-write tools and, when rows are
# excluded, asks for a NEW rehearsal of the narrowed request. This module proves,
# against a real Revit, the two facts that design leans on:
#   1. every rehearsal carries the fields the app's adapter reads (change_preview rows,
#      plan_resolved, confirmation_token, and each tool's own rows);
#   2. a narrowed request is a DIFFERENT plan: it rehearses with its own token, and the
#      full plan's token is REFUSED for it before anything is written.
# Everything here is a rehearsal except case 3, which is an apply that must be refused
# by the confirmation gate; nothing is created, so there is nothing to clean up.
# The replies are saved under ScratchRoot as candidate fixtures (anonymise before
# committing any of them to tests/Horizun.Server.Tests/ImpactPreview/fixtures).

$script:HzProbeModules += [pscustomobject]@{
    Name    = 'impact-preview'
    Catalog = @(
        @{ Name = 'impact-preview: write_params rehearsal carries the rows the app reads'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'impact-preview: a narrowed write_params rehearsal is a different plan with its own token'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'impact-preview: the full plan token is refused for a narrowed request'; Tool = 'horizun_write_params_verified' }
        @{ Name = 'impact-preview: set_keynote rehearsal names the ids behind each target'; Tool = 'horizun_set_keynote' }
        @{ Name = 'impact-preview: delete_verified rehearsal marks requested and cascade rows'; Tool = 'horizun_delete_verified' }
        @{ Name = 'impact-preview: transform_elements rehearsal lists a row per element id'; Tool = 'horizun_transform_elements' }
        @{ Name = 'impact-preview: create_elements rehearsal numbers its rows create:<index>'; Tool = 'horizun_create_elements' }
    )
    Run     = {
        param($Ctx)
        $cases = @()
        $doc = $Ctx.Document
        function Case($name, $tool, $ok, $detail) {
            @{ Name = $name; Tool = $tool; Outcome = $(if ($ok) { 'pass' } else { 'fail' }); Detail = $detail }
        }
        function Save($label, $reply) {
            if (-not $Ctx.ScratchRoot -or -not $reply -or -not $reply.data) { return }
            try {
                $path = Join-Path $Ctx.ScratchRoot ("impact-preview-{0}-{1}.json" -f $label, $Ctx.Year)
                $reply.data | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8
            } catch { }
        }
        function PreviewRows($d) { if ($d -and $d.change_preview) { return @($d.change_preview.rows) } else { return @() } }

        # ---- Discover: two host walls. Nothing is assumed about the fixture. ----
        $list = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Walls'; max_rows = 20; include_links = $false }
        $walls = @()
        if ($list.data) { $walls = @($list.data.rows | Where-Object { $_.element_id } | ForEach-Object { [long]$_.element_id } | Select-Object -First 2) }
        $wallNames = @(
            @{ Name = 'impact-preview: write_params rehearsal carries the rows the app reads'; Tool = 'horizun_write_params_verified' }
            @{ Name = 'impact-preview: a narrowed write_params rehearsal is a different plan with its own token'; Tool = 'horizun_write_params_verified' }
            @{ Name = 'impact-preview: the full plan token is refused for a narrowed request'; Tool = 'horizun_write_params_verified' }
            @{ Name = 'impact-preview: set_keynote rehearsal names the ids behind each target'; Tool = 'horizun_set_keynote' }
            @{ Name = 'impact-preview: delete_verified rehearsal marks requested and cascade rows'; Tool = 'horizun_delete_verified' }
            @{ Name = 'impact-preview: transform_elements rehearsal lists a row per element id'; Tool = 'horizun_transform_elements' })
        if ($walls.Count -lt 2) {
            $why = "'$doc' has fewer than two host walls, so no wall-based rehearsal can be staged."
            foreach ($c in $wallNames) { $cases += @{ Name = $c.Name; Tool = $c.Tool; Outcome = 'not_covered'; Detail = $why } }
        }
        else {
            $tag = 'HZ_IMPACT_' + $Ctx.RunId
            $full = @{ target_document = $doc; dry_run = $true
                       writes = @(@{ target_id = $walls[0]; parameter = 'Comments'; value = $tag },
                                  @{ target_id = $walls[1]; parameter = 'Comments'; value = $tag }) }
            $r1 = & $Ctx.Call 'horizun_write_params_verified' $full
            Save 'write-params' $r1
            $d1 = $r1.data
            $rows = PreviewRows $d1
            $ok1 = (-not $r1.isError) -and $d1 -and $d1.confirmation_token -and $d1.plan_resolved -and
                   @($d1.rows).Count -eq 2 -and (@($d1.rows | Where-Object { $null -ne $_.index }).Count -eq 2) -and
                   $rows.Count -ge 2 -and (@($rows | Where-Object { $_.element_id }).Count -ge 2)
            $cases += Case 'impact-preview: write_params rehearsal carries the rows the app reads' 'horizun_write_params_verified' $ok1 `
                ("token={0} plan_elements={1} rows={2} preview_rows={3}" -f [bool]$d1.confirmation_token, $d1.plan_resolved.elements, @($d1.rows).Count, $rows.Count)

            # Exclude write #1: exactly what the app sends.
            $narrow = @{ target_document = $doc; dry_run = $true
                         writes = @(@{ target_id = $walls[0]; parameter = 'Comments'; value = $tag }) }
            $r2 = & $Ctx.Call 'horizun_write_params_verified' $narrow
            $d2 = $r2.data
            $ok2 = (-not $r2.isError) -and $d2 -and $d2.confirmation_token -and $d1 -and
                   $d2.confirmation_token -ne $d1.confirmation_token -and
                   [int]$d2.plan_resolved.elements -lt [int]$d1.plan_resolved.elements -and
                   $d2.plan_resolved.fingerprint -ne $d1.plan_resolved.fingerprint
            $cases += Case 'impact-preview: a narrowed write_params rehearsal is a different plan with its own token' 'horizun_write_params_verified' $ok2 `
                ("elements {0} -> {1}; tokens differ={2}" -f $d1.plan_resolved.elements, $d2.plan_resolved.elements, ($d2.confirmation_token -ne $d1.confirmation_token))

            # The full plan's token on the narrowed request: the gate must refuse it.
            $ok3 = $false; $detail3 = 'no full-plan token to spend'
            if ($d1 -and $d1.confirmation_token) {
                $spend = @{ target_document = $doc; dry_run = $false; confirmation_token = $d1.confirmation_token
                            idempotency_key = ('impact-preview-mismatch-' + $Ctx.RunId)
                            writes = @(@{ target_id = $walls[0]; parameter = 'Comments'; value = $tag }) }
                $r3 = & $Ctx.Call 'horizun_write_params_verified' $spend
                $state = $null
                if ($r3.data) { $state = $r3.data.state }
                $ok3 = $r3.isError -eq $true -and ($state -in @('refused', 'stale_plan') -or [string]$r3.text -match 'Nothing was changed')
                $detail3 = "isError=$($r3.isError) state=$state"
                # If it was NOT refused, say so loudly: that would be a write nobody rehearsed.
                if (-not $ok3 -and $r3.data -and $r3.data.transaction_status -eq 'Committed') {
                    $detail3 += " - COMMITTED: the Comments of $($walls[0]) now read '$tag' in the disposable document"
                }
            }
            $cases += Case 'impact-preview: the full plan token is refused for a narrowed request' 'horizun_write_params_verified' $ok3 $detail3

            $r4 = & $Ctx.Call 'horizun_set_keynote' @{ target_document = $doc; dry_run = $true; element_ids = @($walls[0], $walls[1]); keynote = $tag }
            Save 'set-keynote' $r4
            $d4 = $r4.data
            $named = @()
            if ($d4) { $named = @($d4.targets | ForEach-Object { @($_.requested_elements) }) | ForEach-Object { [string]$_ } }
            $ok4 = (-not $r4.isError) -and $d4 -and $d4.confirmation_token -and @($d4.targets).Count -ge 1 -and
                   ($named -contains [string]$walls[0]) -and ($named -contains [string]$walls[1])
            $cases += Case 'impact-preview: set_keynote rehearsal names the ids behind each target' 'horizun_set_keynote' $ok4 `
                ("targets={0} requested_elements={1}" -f @($d4.targets).Count, ($named -join ','))

            $r5 = & $Ctx.Call 'horizun_delete_verified' @{ target_document = $doc; dry_run = $true; mode = 'ids'; ids = @($walls[0]) }
            Save 'delete-ids' $r5
            $rows5 = PreviewRows $r5.data
            $req5 = @($rows5 | Where-Object { $_.captured_state.role -eq 'requested' -and [string]$_.captured_state.raw_id -eq [string]$walls[0] })
            $bad5 = @($rows5 | Where-Object { $_.captured_state.role -notin @('requested', 'cascade') })
            $ok5 = (-not $r5.isError) -and $r5.data.confirmation_token -and $req5.Count -eq 1 -and $bad5.Count -eq 0
            $cases += Case 'impact-preview: delete_verified rehearsal marks requested and cascade rows' 'horizun_delete_verified' $ok5 `
                ("preview_rows={0} requested_match={1} unknown_roles={2}" -f $rows5.Count, $req5.Count, $bad5.Count)

            $r6 = & $Ctx.Call 'horizun_transform_elements' @{ target_document = $doc; dry_run = $true
                                                             operations = @(@{ operation = 'pin'; element_ids = @($walls[0], $walls[1]) }) }
            Save 'transform' $r6
            $ids6 = @(PreviewRows $r6.data | ForEach-Object { [string]$_.element_id })
            $ok6 = (-not $r6.isError) -and $r6.data.confirmation_token -and ($ids6 -contains [string]$walls[0]) -and ($ids6 -contains [string]$walls[1])
            $cases += Case 'impact-preview: transform_elements rehearsal lists a row per element id' 'horizun_transform_elements' $ok6 ("preview ids=" + ($ids6 -join ','))
        }

        $r7 = & $Ctx.Call 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; dry_run = $true
            elements = @(@{ kind = 'grid'; name = ('HZIP1_' + $Ctx.RunId); start = @(900000, 0, 0); end = @(900000, 6000, 0) },
                         @{ kind = 'grid'; name = ('HZIP2_' + $Ctx.RunId); start = @(906000, 0, 0); end = @(906000, 6000, 0) }) }
        Save 'create' $r7
        $uids = @(PreviewRows $r7.data | ForEach-Object { [string]$_.unique_id })
        $ok7 = (-not $r7.isError) -and $r7.data.confirmation_token -and ($uids -contains 'create:0') -and ($uids -contains 'create:1')
        $cases += Case 'impact-preview: create_elements rehearsal numbers its rows create:<index>' 'horizun_create_elements' $ok7 ("preview unique_ids=" + ($uids -join ','))
        return $cases
    }
}
