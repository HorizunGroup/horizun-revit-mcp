# Live probes for horizun_health's workshare_status and recent_horizun_writes
# blocks (HealthCommand.cs). Loaded by scripts/verify-live.ps1 (see README.md);
# exercised without Revit by health-state.tests.ps1. Read-only: horizun_health
# writes nothing, so there is nothing to clean up.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'health-state'
    Catalog = @(
        @{ Name = 'health: workshare_status reports ownership for the active document';               Tool = 'horizun_health' }
        @{ Name = 'health: recent_horizun_writes names its journal, not Revit Undo, honestly';         Tool = 'horizun_health' }
    )
    Run     = {
        param($Ctx)
        $T = 'horizun_health'
        $out = New-Object System.Collections.Generic.List[object]
        function Case($name, $ok, $detail) {
            $out.Add(@{ Name = $name; Tool = $T; Outcome = $(if ($ok) { 'pass' } else { 'fail' }); Detail = [string]$detail })
        }
        $names = @(
            'health: workshare_status reports ownership for the active document',
            'health: recent_horizun_writes names its journal, not Revit Undo, honestly'
        )

        $h = & $Ctx.Call $T @{}
        if ($h.isError -or -not $h.data) {
            foreach ($n in $names) { Case $n $false ('horizun_health itself failed: ' + ([string]$h.text)) }
            return $out.ToArray()
        }

        # ---- workshare_status ----
        $ws = $h.data.workshare_status
        $wsOk = $null -ne $ws
        if ($wsOk) {
            if ($ws.workshared -eq $true) {
                $wsOk = ($null -ne $ws.owned_worksets) -and ($null -ne $ws.borrowed_by_me) -and
                        ($null -ne $ws.borrowed_by_me.complete) -and ($ws.borrowed_by_me.elements_checked -ge 0) -and
                        ($ws.borrowed_by_me.owned_by_current_user_count -ge 0)
            }
            elseif ($ws.workshared -eq $false) {
                $wsOk = ($null -ne $ws.note) -and ($ws.note -match 'not workshared')
            }
            else { $wsOk = ($null -ne $ws.measured) -and ($ws.measured -eq $false) }   # honestly unmeasured is still a valid shape
        }
        Case $names[0] $wsOk ($ws | ConvertTo-Json -Compress -Depth 5)

        # ---- recent_horizun_writes ----
        $rw = $h.data.recent_horizun_writes
        $rwOk = ($null -ne $rw) -and ($null -ne $rw.source) -and (([string]$rw.source) -match 'NOT') -and
                (([string]$rw.note) -match "Ctrl\+Z") -and ($null -ne $rw.batches_recorded_total) -and
                ($null -ne $rw.most_recent)
        # A stronger check when the write tier has been open: by the time this module
        # runs (last section loaded, see README.md), the rest of the harness has
        # already committed plenty of typed writes into THIS SAME document, so the
        # journal must show at least one.
        if ($rwOk -and -not $Ctx.WriteGate) {
            $rwOk = ($rw.batches_recorded_total -gt 0) -and (@($rw.most_recent).Count -gt 0) -and
                    (-not [string]::IsNullOrWhiteSpace([string]$rw.most_recent[0].tool))
        }
        Case $names[1] $rwOk ('batches_recorded_total=' + $rw.batches_recorded_total + ' most_recent_count=' + @($rw.most_recent).Count +
            ' source=' + $rw.source + ' write_gate=' + $Ctx.WriteGate)

        return $out.ToArray()
    }
}
