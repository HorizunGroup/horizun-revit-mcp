# Live probes for horizun_document_session operation=sync_with_central. Only the
# harness's OWN workshared material is touched: the year's closed-workset fixture
# opened DETACHED (workshared-fixture.lib.ps1), and - for a real sync - a central the
# harness creates from that detached copy in its own scratch folder (SaveAs with
# WorksharingSaveAsOptions.SaveAsCentral=true, then WorksharingUtils.CreateNewLocal),
# because no project central may ever be synchronized by a probe. A new local is not a
# local on disk until Revit opens it and SAVES it - until then it reads IsCentral=true,
# IsLocal=false exactly like a copy of its central, and the bridge's open guard refuses it
# (MEASURED 2026-09-27 in Revit 2026) - so a second script does that first open and save,
# with the central already closed, as Revit's own "Create New Local" does. Creating that central
# needs horizun_execute_python, and syncing needs the owner's sync switch; when either
# is off the case is not_covered with the reason, never forced. Nothing is saved over
# a fixture. The central is closed before its local is opened (one session holding
# both is not what a user's sync looks like), and the per-run scratch folder is removed
# at the end - kept, and named in the failing case, only when a case failed.
# After relinquish=all, the scratch local BORROWS one element by editing it (the
# keep_borrowed case) and then takes a user workset (the none case), so the branches
# that keep ownership are measured too.
# Shapes from the code (DocumentSessionSync.cs, ExecutePythonCommand.cs - the script's
# __output__ comes back as data.output), to be held against the first live run.
. (Join-Path $PSScriptRoot 'workshared-fixture.lib.ps1')
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'sync-central'
    Catalog = @(
        @{ Name = 'sync central: the non-workshared write document is refused as not_workshared'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: a detached workshared copy is refused as detached_copy, nothing ran'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: with the owner switch off the refusal names Advanced options'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: preview is a labelled estimate, apply syncs a scratch local and verifies relinquish=all'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: keep_borrowed keeps exactly the one element the local borrowed by editing it'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: relinquish=none keeps the owned workset and every owned element'; Tool = 'horizun_document_session' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $S = 'horizun_document_session'
        $nNotWs = 'sync central: the non-workshared write document is refused as not_workshared'
        $nDetached = 'sync central: a detached workshared copy is refused as detached_copy, nothing ran'
        $nOwnerOff = 'sync central: with the owner switch off the refusal names Advanced options'
        $nReal = 'sync central: preview is a labelled estimate, apply syncs a scratch local and verifies relinquish=all'
        $nKeep = 'sync central: keep_borrowed keeps exactly the one element the local borrowed by editing it'
        $nNone = 'sync central: relinquish=none keeps the owned workset and every owned element'
        function Skip($names, $why) { foreach ($n in $names) { Case $n $S 'not_covered' $why } }
        function Code($r) { if ($r.data -and $r.data.code) { [string]$r.data.code } elseif ($r.structured -and $r.structured.code) { [string]$r.structured.code } else { $null } }
        function Short($r) { $t = [string]$r.text; if ($t.Length -gt 300) { $t.Substring(0, 300) } else { $t } }
        function Out($r) { if ($r.data -and $r.data.output) { $r.data.output } else { $null } }
        if ($Ctx.WriteGate) {
            Skip @($nNotWs, $nDetached, $nOwnerOff, $nReal, $nKeep, $nNone) 'the write tier is closed for this run'
            return $cases
        }
        $run = $Ctx.RunId

        $w = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $Ctx.Document; dry_run = $true }
        $wc = Code $w
        if ($w.isError -and $wc -eq 'not_workshared') { Case $nNotWs $S 'pass' 'refused before any census' }
        elseif ($w.isError -and -not $wc) { Case $nNotWs $S 'not_covered' ('refused before the operation ran (no detail code; profile, pause or dispatcher): ' + (Short $w)) }
        elseif ($Ctx.PSObject.Properties['LinkSourceFile'] -and $Ctx.LinkSourceFile -and (Test-Path -LiteralPath ([string]$Ctx.LinkSourceFile))) {
            # Reached also when the workshared write document is refused for another reason
            # (sync_blocked_unverified, sync_not_authorised...): not_workshared is a property of a
            # plain model, so it is provoked on the plain copy either way.
            # The write document is workshared (the release gate's central, MEASURED 2026-09-27):
            # the refusal is provoked on a scratch copy of the run's link source, a plain model.
            New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
            $plain = Join-Path $Ctx.ScratchRoot ('HZ_SYNCPLAIN_' + ($Ctx.RunId -replace '[^A-Za-z0-9]', '') + '.rvt')
            Copy-Item -LiteralPath ([string]$Ctx.LinkSourceFile) -Destination $plain -Force
            $pf = Enter-HzFixtureFile $Ctx $plain 'sync-plain' $null
            if (-not $pf.Title) { Case $nNotWs $S 'not_covered' ('the write document is workshared and the plain copy did not open: ' + $pf.Why) }
            else {
                try {
                    $pw = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $pf.Title; dry_run = $true }
                    $pwc = Code $pw
                    Case $nNotWs $S $(if ($pw.isError -and $pwc -eq 'not_workshared') { 'pass' } else { 'fail' }) ("on the plain copy '" + $pf.Title + "': code=$pwc " + (Short $pw))
                }
                finally { $null = Exit-HzWorksharedFixture $Ctx $pf 'sync-plain' }
            }
        }
        elseif ($w.isError) { Case $nNotWs $S 'not_covered' "the write document refused as $wc and the run names no link source to copy as a plain model" }
        else { Case $nNotWs $S 'fail' ('not refused: ' + (Short $w)) }

        $fixture = Enter-HzWorksharedFixture $Ctx 'sync'
        if (-not $fixture.Title) {
            Skip @($nDetached, $nOwnerOff, $nReal, $nKeep, $nNone) $fixture.Why
            return $cases
        }
        $local = $null
        $centralClosed = $false
        $deferDetached = $false
        $dir = Join-Path ([IO.Path]::GetTempPath()) ('hz-sync-probe-' + $run)
        try {
            $d = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $fixture.Title; dry_run = $true }
            $dc = Code $d
            # The owner switch answers first on a release runner (MEASURED 2026-09-27, v2.1.2 gate:
            # sync_not_authorised): the detached check is then not reached, which is not a failure.
            if ($fixture.Title -eq $Ctx.Document) {
                # The "fixture" is the write model itself (release gate): not a detached copy.
                # Checked at the end on a copy of this probe's own central, once it is closed.
                $deferDetached = $true
            }
            elseif ($d.isError -and $dc -eq 'sync_not_authorised' -and -not $d.data.confirmation_token) {
                Case $nDetached $S 'not_covered' 'the owner switch refused first (sync_not_authorised), so the detached check was not reached; nothing ran'
            }
            else { Case $nDetached $S $(if ($d.isError -and $dc -eq 'detached_copy' -and -not $d.data.confirmation_token) { 'pass' } else { 'fail' }) ("code=$dc on '" + $fixture.Title + "' " + (Short $d)) }

            # A central of the harness's own, in its own scratch folder, and a new local of it.
            # The title is reported as soon as SaveAs renamed the document, so a failure
            # after it still lets the finally close the central under its NEW title.
            $py = @"
import os
from Autodesk.Revit.DB import SaveAsOptions, WorksharingSaveAsOptions, WorksharingUtils, ModelPathUtils
d = r'$dir'
if not os.path.isdir(d): os.makedirs(d)
central = os.path.join(d, 'HZ_SYNC_CENTRAL.rvt')
local = os.path.join(d, 'HZ_SYNC_LOCAL.rvt')
o = SaveAsOptions(); o.OverwriteExistingFile = True
w = WorksharingSaveAsOptions(); w.SaveAsCentral = True
o.SetWorksharingOptions(w)
doc.SaveAs(central, o)
out = {'central': central, 'local': local, 'central_title': doc.Title, 'local_exists': False, 'error': None}
try:
    WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(local))
    out['local_exists'] = os.path.exists(local)
except Exception as e:
    out['error'] = str(e)
__output__ = out
"@
            # The same document gate as every write: the script names the detached copy it acts on.
            $p = & $Ctx.Call 'horizun_execute_python' @{ code = $py; target_document = $fixture.Title; idempotency_key = ('sync-central-' + $run) }
            $out = Out $p
            if ($out -and $out.central_title) { $fixture.Title = [string]$out.central_title }   # the detached copy became the central
            if ($p.isError -or -not $out -or -not $out.local_exists) {
                $pc = Code $p
                $why = if ($p.isError -and ($pc -eq 'tool_disabled' -or [string]$p.text -match 'DISABLED|permission_profile=unsafe_code|OFF on a fresh install')) {
                    'python disabled on this machine, so the harness cannot create a scratch central: ' + (Short $p)
                } elseif ($p.isError) {
                    'python refused: ' + $(if ($pc) { $pc + ' - ' } else { '' }) + (Short $p)
                } else {
                    'python ran but did not report a created central and local: ' + $(if ($out -and $out.error) { [string]$out.error + ' ' } else { '' }) + (Short $p)
                }
                Skip @($nOwnerOff, $nReal, $nKeep, $nNone) $why
                return $cases
            }
            $null = Exit-HzWorksharedFixture $Ctx $fixture 'sync'
            $centralClosed = $true
            # The first open and save of the new local, on the write document (the central is closed).
            $pyL = @"
from Autodesk.Revit.DB import ModelPathUtils, OpenOptions, BasicFileInfo
local = r'$([string]$out.local)'
dl = doc.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(local), OpenOptions())
try:
    dl.Save()
finally:
    dl.Close(False)
i = BasicFileInfo.Extract(local)
__output__ = {'is_local': bool(i.IsLocal), 'is_central': bool(i.IsCentral), 'central_path': i.CentralPath}
"@
            $l = & $Ctx.Call 'horizun_execute_python' @{ code = $pyL; target_document = $Ctx.Document; idempotency_key = ('sync-first-open-' + $run) }
            $lo = Out $l
            if ($l.isError -or -not $lo -or $lo.is_local -ne $true -or $lo.is_central -ne $false) {
                Skip @($nOwnerOff, $nReal, $nKeep, $nNone) ('the new local did not become a local on its first open and save: ' +
                    $(if ($lo) { "is_local=$($lo.is_local) is_central=$($lo.is_central) " } else { '' }) + (Short $l))
                return $cases
            }
            $o = & $Ctx.Call $S @{ operation = 'open'; file_path = ([string]$out.local).Replace([char]92, '/'); expected_version = [string]$Ctx.Year; idempotency_key = ('sync-open-' + $run) }
            if ($o.isError -or -not $o.data.title) {
                # A refusal that says a document WAS opened left it open: the finally closes it too.
                if ([string]$o.text -match "A DOCUMENT WAS OPENED.*?title '([^']+)'") { $local = $Matches[1] }
                Skip @($nOwnerOff, $nReal, $nKeep, $nNone) ('the scratch local did not open: ' + (Short $o))
                return $cases
            }
            $local = [string]$o.data.title
            $pv = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'all'; comment = ('hz probe ' + $run); dry_run = $true }
            $pc = Code $pv
            if ($pv.isError -and $pc -eq 'sync_not_authorised') {
                Case $nOwnerOff $S $(if ([string]$pv.text -like '*Advanced options*') { 'pass' } else { 'fail' }) 'refused; the owner has not enabled sync on this machine'
                Skip @($nReal, $nKeep, $nNone) 'the machine owner has not enabled Synchronize with central (Advanced options); a probe never enables it'
                return $cases
            }
            # The switch is ON. A run with an ISOLATED data root (the release gate copies the owner's
            # settings.json into its own folder) may turn it off in that copy only, prove the
            # refusal, and turn it back on. The owner's own file is never touched: the check below
            # refuses unless the isolated root is a different folder from the owner's.
            $isoRoot = [string]$env:HORIZUN_DATA_ROOT
            $ownerRoot = Join-Path $env:USERPROFILE '.horizun'
            $isoSettings = if ($isoRoot) { Join-Path $isoRoot 'settings.json' } else { $null }
            if ($pv.isError) { Case $nOwnerOff $S 'not_covered' "refused as $pc" }
            elseif (-not $isoSettings -or -not (Test-Path -LiteralPath $isoSettings) -or
                    [IO.Path]::GetFullPath($isoRoot).TrimEnd('\') -ieq [IO.Path]::GetFullPath($ownerRoot).TrimEnd('\')) {
                Case $nOwnerOff $S 'not_covered' 'the owner switch is ON and this run has no isolated settings copy to turn it off in'
            }
            else {
                $orig = [IO.File]::ReadAllText($isoSettings)
                try {
                    $js = $orig | ConvertFrom-Json
                    $js.sync_with_central_owner_granted = $false
                    [IO.File]::WriteAllText($isoSettings, ($js | ConvertTo-Json -Depth 20))
                    $off = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'all'; comment = ('hz probe off ' + $run); dry_run = $true }
                    $offc = Code $off
                    Case $nOwnerOff $S $(if ($off.isError -and $offc -eq 'sync_not_authorised' -and [string]$off.text -like '*Advanced options*' -and -not $off.data.confirmation_token) { 'pass' } else { 'fail' }) ("switch turned off in the run's isolated settings copy only: code=$offc " + (Short $off))
                }
                finally { [IO.File]::WriteAllText($isoSettings, $orig) }
            }
            if ($pv.isError -or $pv.data.preview_kind -ne 'estimate' -or -not $pv.data.confirmation_token) {
                Case $nReal $S 'fail' ('the preview is not a labelled estimate with a token: ' + (Short $pv))
                Skip @($nKeep, $nNone) 'the relinquish=all sync did not run'
                return $cases
            }
            $ap = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'all'; comment = ('hz probe ' + $run); dry_run = $false; confirmation_token = [string]$pv.data.confirmation_token; idempotency_key = ('sync-apply-' + $run) }
            $ok = (-not $ap.isError) -and $ap.data.sync_verified -eq $true -and [int]$ap.data.ownership.owned_worksets_after -eq 0 -and
                  [int]$ap.data.ownership.owned_elements_after -eq 0 -and $ap.data.has_all_changes_from_central_after -eq $true
            Case $nReal $S $(if ($ok) { 'pass' } else { 'fail' }) ('estimate owned_elements=' + $pv.data.owned_elements + ' has_all_changes=' +
                $pv.data.has_all_changes_from_central + ' sample=' + $pv.data.update_status_sample.sample_size + '; apply: central_file_written=' +
                $ap.data.central_file_written + ' is_modified_after=' + $ap.data.is_modified_after + ' ' + (Short $ap))
            if (-not $ok) { Skip @($nKeep, $nNone) 'the relinquish=all sync did not verify, so the local does not start from owning nothing'; return $cases }

            # keep_borrowed: after relinquish=all the local owns nothing, so editing one element
            # of a user workset nobody owns BORROWS exactly that element.
            $pyB = @"
from Autodesk.Revit.DB import FilteredElementCollector, Transaction, WorksharingUtils, CheckoutStatus, BuiltInParameter, WorksetKind
pick = None
for e in FilteredElementCollector(doc).WhereElementIsNotElementType():
    p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
    if p is None or p.IsReadOnly: continue
    ws = doc.GetWorksetTable().GetWorkset(e.WorksetId)
    if ws is None or ws.Kind != WorksetKind.UserWorkset or ws.Owner: continue
    if WorksharingUtils.GetCheckoutStatus(doc, e.Id) != CheckoutStatus.NotOwned: continue
    pick = e
    break
out = {'uid': None, 'checkout': None, 'status': 'failed'}
if pick is not None:
    t = Transaction(doc, 'hz probe borrow')
    t.Start()
    pick.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set('hz probe $run')
    t.Commit()
    co = str(WorksharingUtils.GetCheckoutStatus(doc, pick.Id))
    # 'status' is the script's verdict for the bridge; the checkout state goes in its own key.
    out = {'uid': pick.UniqueId, 'checkout': co, 'status': 'verified' if co == 'OwnedByCurrentUser' else 'failed',
           'verification': {'checked': True, 'evidence': ['GetCheckoutStatus re-read: ' + co]}}
__output__ = out
"@
            $b = & $Ctx.Call 'horizun_execute_python' @{ code = $pyB; target_document = $local; idempotency_key = ('sync-borrow-' + $run) }
            $bo = Out $b
            if ($b.isError -or -not $bo -or -not $bo.uid -or [string]$bo.checkout -ne 'OwnedByCurrentUser') {
                Case $nKeep $S 'not_covered' ('could not borrow one element of an unowned user workset by editing it: ' + (Short $b))
            }
            else {
                $kp = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'keep_borrowed'; comment = ('hz probe keep ' + $run); dry_run = $true }
                $ka = if (-not $kp.isError -and $kp.data.confirmation_token) {
                    & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'keep_borrowed'; comment = ('hz probe keep ' + $run); dry_run = $false; confirmation_token = [string]$kp.data.confirmation_token; idempotency_key = ('sync-keep-' + $run) }
                } else { $kp }
                # One borrowed before, verified by UniqueId, one owned after, no workset owned:
                # exactly the edited element is still owned.
                $ok = (-not $ka.isError) -and [int]$kp.data.borrowed_elements -eq 1 -and $ka.data.sync_verified -eq $true -and
                      [int]$ka.data.ownership.owned_worksets_after -eq 0 -and [int]$ka.data.ownership.owned_elements_after -eq 1 -and
                      @($ka.data.ownership.unexpectedly_released).Count -eq 0 -and @($ka.data.ownership.unexpectedly_owned).Count -eq 0
                Case $nKeep $S $(if ($ok) { 'pass' } else { 'fail' }) ('borrowed uid=' + $bo.uid + ' preview borrowed=' + $kp.data.borrowed_elements +
                    '; apply owned_after=' + $ka.data.ownership.owned_elements_after + ' ' + (Short $ka))
            }

            # none: take one user workset nobody owns; the sync must keep it and every owned element.
            $pyW = @"
from Autodesk.Revit.DB import FilteredWorksetCollector, WorksetKind, WorksharingUtils, WorksetId
from System.Collections.Generic import List
free = [w for w in FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset) if not w.Owner]
out = {'workset': None, 'owned': False, 'status': 'failed'}
if free:
    WorksharingUtils.CheckoutWorksets(doc, List[WorksetId]([free[0].Id]))
    owned = bool(doc.GetWorksetTable().GetWorkset(free[0].Id).Owner)
    out = {'workset': free[0].Name, 'owned': owned, 'status': 'verified' if owned else 'failed',
           'verification': {'checked': True, 'evidence': ['workset owner re-read: ' + str(owned)]}}
__output__ = out
"@
            $k = & $Ctx.Call 'horizun_execute_python' @{ code = $pyW; target_document = $local; idempotency_key = ('sync-take-' + $run) }
            $ko = Out $k
            if ($k.isError -or -not $ko -or -not $ko.owned) {
                Case $nNone $S 'not_covered' ('could not take a user workset nobody owns: ' + (Short $k))
            }
            else {
                $np = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'none'; comment = ('hz probe none ' + $run); dry_run = $true }
                $na = if (-not $np.isError -and $np.data.confirmation_token) {
                    & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'none'; comment = ('hz probe none ' + $run); dry_run = $false; confirmation_token = [string]$np.data.confirmation_token; idempotency_key = ('sync-none-' + $run) }
                } else { $np }
                $ok = (-not $na.isError) -and [int]$np.data.owned_worksets -ge 1 -and $na.data.sync_verified -eq $true -and
                      [int]$na.data.ownership.owned_worksets_after -eq [int]$np.data.owned_worksets -and
                      [int]$na.data.ownership.owned_elements_after -ge [int]$np.data.owned_elements -and
                      @($na.data.ownership.unexpectedly_released).Count -eq 0
                Case $nNone $S $(if ($ok) { 'pass' } else { 'fail' }) ('took workset ' + $ko.workset + '; preview owned_worksets=' + $np.data.owned_worksets +
                    ' owned_elements=' + $np.data.owned_elements + '; apply ' + (Short $na))
            }
        }
        finally {
            # A failed close must not skip the deferred detached case below: it would go unreported.
            $closeNote = $null
            try { if ($local) { $null = Exit-HzWorksharedFixture $Ctx @{ Title = $local; WritePath = $fixture.WritePath } 'sync-local' } }
            catch { $closeNote = 'closing the scratch local failed: ' + $_.Exception.Message }
            try { if (-not $centralClosed) { $null = Exit-HzWorksharedFixture $Ctx $fixture 'sync' } }
            catch { $closeNote = (@($closeNote, ('closing the fixture failed: ' + $_.Exception.Message)) | Where-Object { $_ }) -join '; ' }
            if ($deferDetached) {
                $ownCentral = Join-Path $dir 'HZ_SYNC_CENTRAL.rvt'
                if (-not (Test-Path -LiteralPath $ownCentral)) { Case $nDetached $S 'not_covered' ('the write model is the fixture and this probe made no central of its own to copy' + $(if ($closeNote) { ' (' + $closeNote + ')' } else { '' })) }
                else {
                    $dcopy = Join-Path $dir 'HZ_SYNC_DETACHED.rvt'
                    Copy-Item -LiteralPath $ownCentral -Destination $dcopy -Force
                    $df = Enter-HzFixtureFile $Ctx $dcopy 'sync-detached' $fixture.WritePath
                    if (-not $df.Title) { Case $nDetached $S 'fail' ('a copy of the probe''s own closed central did not open detached: ' + $df.Why) }
                    else {
                        try {
                            $dd = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $df.Title; dry_run = $true }
                            $ddc = Code $dd
                            Case $nDetached $S $(if ($dd.isError -and $ddc -eq 'detached_copy' -and -not $dd.data.confirmation_token) { 'pass' } else { 'fail' }) ("code=$ddc on the detached copy '" + $df.Title + "' of the probe's own central " + (Short $dd))
                        }
                        finally { $null = Exit-HzWorksharedFixture $Ctx $df 'sync-detached' }
                    }
                }
            }
            if (Test-Path -LiteralPath $dir) {
                $failed = @($cases | Where-Object { $_.Outcome -eq 'fail' })
                if ($failed.Count -eq 0) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
                $note = if ($failed.Count -gt 0) { ' | scratch kept for inspection at ' + $dir }
                        elseif (Test-Path -LiteralPath $dir) { ' | scratch folder could not be removed: ' + $dir } else { $null }
                if ($note) { foreach ($fc in @($cases | Where-Object { $_.Name -eq $nReal -or $_.Outcome -eq 'fail' })) { $fc.Detail = [string]$fc.Detail + $note } }
            }
        }
        return $cases
    }
}
