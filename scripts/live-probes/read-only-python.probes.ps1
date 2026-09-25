# Live probes for horizun_execute_python read_only=true (Core/ReadOnlyPythonGuard.cs).
# Loaded by scripts/verify-live.ps1 (see README.md); exercised without Revit by
# read-only-python.tests.ps1. Nothing here is left behind: the wall created in case 1
# lives inside a TransactionGroup this handler always rolls back, and Save/SaveAs are
# never actually reached (they are refused before the script runs).
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'read-only-python'
    Catalog = @(
        @{ Name = 'read_only: a wall committed inside read_only is rolled back and does not persist'; Tool = 'horizun_execute_python' }
        @{ Name = 'read_only: a Save() call is refused before the script runs';                        Tool = 'horizun_execute_python' }
        @{ Name = 'read_only: preflight reports the scan without executing';                            Tool = 'horizun_execute_python' }
    )
    Run     = {
        param($Ctx)
        $T = 'horizun_execute_python'
        $out = New-Object System.Collections.Generic.List[object]
        function Case($name, $ok, $detail) {
            $out.Add(@{ Name = $name; Tool = $T; Outcome = $(if ($ok) { 'pass' } else { 'fail' }); Detail = [string]$detail })
        }
        function Skip($name, $why) { $out.Add(@{ Name = $name; Tool = $T; Outcome = 'not_covered'; Detail = $why }) }
        function Disabled($reply) {
            if (-not $reply.isError) { return $false }
            return ([string]$reply.text) -match 'DISABLED ON THIS MACHINE'
        }
        $doc = $Ctx.Document
        $names = @(
            'read_only: a wall committed inside read_only is rolled back and does not persist',
            'read_only: a Save() call is refused before the script runs',
            'read_only: preflight reports the scan without executing'
        )

        # ---- case 3 first: needs no write tier, just execute_python enabled ----
        $preflightCode = "doc.SaveAs(r'C:\nope.rvt')"
        $pf = & $Ctx.Call $T @{ target_document = $doc; code = $preflightCode; preflight = $true; read_only = $true }
        if (Disabled $pf) {
            foreach ($n in $names) { Skip $n 'horizun_execute_python is disabled on this machine (safe default) - nothing here can be exercised' }
            return $out.ToArray()
        }
        $pfOk = (-not $pf.isError) -and ($pf.data.executed -eq $false) -and ($pf.data.would_run -eq $false) -and
                ([string]$pf.data.checks.read_only_scan) -match 'WOULD BE REFUSED' -and
                ([string]$pf.data.checks.read_only_scan) -match 'SaveAs'
        Case $names[2] $pfOk ("isError=" + $pf.isError + " executed=" + $pf.data.executed + " would_run=" + $pf.data.would_run + " scan=" + $pf.data.checks.read_only_scan)

        if ($Ctx.WriteGate) {
            foreach ($n in $names[0..1]) { Skip $n 'write tier closed' }
            return $out.ToArray()
        }

        # ---- case 2: the real (non-preflight) refusal, before anything runs ----
        $saveKey = 'live-readonly-save-' + $Ctx.RunId
        $sv = & $Ctx.Call $T @{ target_document = $doc; code = "doc.Save()"; read_only = $true; idempotency_key = $saveKey }
        $svOk = $sv.isError -and (([string]$sv.text) -match 'REFUSES') -and (([string]$sv.text) -match 'Document\.Save')
        Case $names[1] $svOk ("isError=" + $sv.isError + " text=" + ([string]$sv.text).Substring(0, [Math]::Min(300, ([string]$sv.text).Length)))

        # ---- case 1: a real write, committed by the script, inside read_only ----
        $wallCode = @'
from Autodesk.Revit.DB import Line, XYZ, Wall, Transaction, FilteredElementCollector, Level
levels = list(FilteredElementCollector(doc).OfClass(Level))
if not levels:
    __output__ = {'status': 'failed', 'error': 'no Level in this document to build on'}
else:
    level = levels[0]
    before = len(list(FilteredElementCollector(doc).OfClass(Wall)))
    t = Transaction(doc, 'HZ read_only probe wall')
    t.Start()
    try:
        w = Wall.Create(doc, Line.CreateBound(XYZ(0, 0, 0), XYZ(10, 0, 0)), level.Id, False)
        t.Commit()
    except Exception as ex:
        t.RollBack()
        __output__ = {'status': 'failed', 'error': str(ex)}
    else:
        wid = w.Id.IntegerValue if hasattr(w.Id, 'IntegerValue') else w.Id.Value
        __output__ = {'status': 'self_reported_verified', 'wall_id': wid, 'walls_before': before}
'@
        $wallKey = 'live-readonly-wall-' + $Ctx.RunId
        $wr = & $Ctx.Call $T @{ target_document = $doc; code = $wallCode; read_only = $true; idempotency_key = $wallKey }
        $wallId = $null
        if (-not $wr.isError -and $wr.data.output.status -eq 'self_reported_verified') { $wallId = $wr.data.output.wall_id }

        $checkGone = $null
        if ($null -ne $wallId) {
            $goneCode = @'
from Autodesk.Revit.DB import ElementId
wid = int(__import__('json').loads(HORIZUN_ARGS_JSON)['wall_id'])
e = doc.GetElement(ElementId(wid))
__output__ = {'status': 'self_reported_verified', 'still_present': e is not None}
'@
            $goneKey = 'live-readonly-wall-gone-' + $Ctx.RunId
            $checkGone = & $Ctx.Call $T @{ target_document = $doc; code = $goneCode; read_only = $true; idempotency_key = $goneKey; arguments = @{ wall_id = $wallId } }
        }

        $wallOk = (-not $wr.isError) -and ($wr.data.output.status -eq 'self_reported_verified') -and
                  ($wr.data.read_only -eq $true) -and ($wr.data.read_only_check.ok -eq $true) -and
                  ($wr.data.read_only_check.rolled_back -eq $true) -and ($wr.data.transaction_left_open -eq $false) -and
                  ($null -ne $checkGone) -and (-not $checkGone.isError) -and ($checkGone.data.output.still_present -eq $false)
        Case $names[0] $wallOk ("run: isError=" + $wr.isError + " output=" + ($wr.data.output | ConvertTo-Json -Compress -Depth 4) +
            " read_only_check=" + ($wr.data.read_only_check | ConvertTo-Json -Compress -Depth 4) +
            " | gone_check: " + $(if ($checkGone) { ($checkGone.data.output | ConvertTo-Json -Compress -Depth 3) } else { 'not run (no wall_id)' }))

        return $out.ToArray()
    }
}
