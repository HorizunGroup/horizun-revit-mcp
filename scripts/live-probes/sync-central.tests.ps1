#Requires -Version 5.1
# Exercises sync-central.probes.ps1 WITHOUT Revit. Shapes from the code
# (DocumentSessionSync.cs: SyncRefuse detail.code, the estimate preview, the apply
# report with its ownership block; ExecutePythonCommand.cs: DocumentGate.ForMutation
# refuses a script that names no target_document, and a script's __output__ comes back
# as data.output - the shape read-only-python.probes.ps1 already reads live), to be
# held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'sync-central.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'sync-central' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('hz-sync-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$writePath = Join-Path $dir 'HZ_WRITE.rvt'; Set-Content -LiteralPath $writePath -Value 'x'
Set-Content -LiteralPath (Join-Path $dir 'HZ_CLOSED.rvt') -Value 'x'
$runId = 'r' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-sync-probe-' + $runId)

function Ok($data) { [pscustomobject]@{ isError = $false; data = [pscustomobject]$data; text = ''; structured = $null } }
function Refused($code, $text) { [pscustomobject]@{ isError = $true; data = [pscustomobject]@{ code = $code; write_started = $false }; text = $text; structured = $null } }

$script:mode = 'python_off'
$script:calls = New-Object System.Collections.ArrayList
$script:pyTargets = New-Object System.Collections.ArrayList
$call = {
    param($tool, $a)
    [void]$script:calls.Add(($tool + ':' + [string]$a.operation + ':' + [string]$a.target_document + $(if ($a.file_path) { ':' + [string]$a.file_path } else { '' })))
    if ($tool -eq 'horizun_health') { return (Ok @{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $writePath }) }) }
    if ($tool -eq 'horizun_open_document') { return (Ok @{ title = 'HZ_WRITE' }) }
    if ($tool -eq 'horizun_execute_python') {
        [void]$script:pyTargets.Add([string]$a.target_document)
        # DocumentGate.ForMutation: no target, no run - whatever the switches say.
        if (-not $a.target_document) { return (Refused $null "'target_document' is required for horizun_execute_python. Nothing ran.") }
        if ($script:mode -eq 'python_off') { return (Refused 'tool_disabled' 'horizun_execute_python is DISABLED ON THIS MACHINE') }
        if ($script:mode -eq 'python_refused') { return (Refused 'document_not_active' 'the target is not the active document') }
        if ([string]$a.code -match 'ALL_MODEL_INSTANCE_COMMENTS') {
            if ($script:mode -eq 'no_borrow') { return (Ok @{ output = [pscustomobject]@{ uid = $null; status = $null } }) }
            return (Ok @{ output = [pscustomobject]@{ uid = 'uid-borrowed'; checkout = 'OwnedByCurrentUser'; status = 'verified' } })
        }
        if ([string]$a.code -match 'CheckoutWorksets') { return (Ok @{ output = [pscustomobject]@{ workset = 'Workset1'; owned = $true } }) }
        if ([string]$a.code -match 'BasicFileInfo\.Extract\(local\)') {
            # The first open and save of the new local (MEASURED 2026-09-27: saved -> IsLocal).
            [void]$script:calls.Add('first-open:' + [string]$a.target_document)
            if ($script:mode -eq 'first_open_fails') { return (Ok @{ output = [pscustomobject]@{ is_local = $false; is_central = $true; central_path = 'C:	\HZ_SYNC_CENTRAL.rvt' } }) }
            return (Ok @{ output = [pscustomobject]@{ is_local = $true; is_central = $false; central_path = 'C:	\HZ_SYNC_CENTRAL.rvt' } })
        }
        if ($script:mode -eq 'local_fails') {
            # SaveAs renamed the detached copy, then CreateNewLocal threw inside the script.
            return (Ok @{ output = [pscustomobject]@{ central = 'C:\t\HZ_SYNC_CENTRAL.rvt'; local = 'C:\t\HZ_SYNC_LOCAL.rvt'; central_title = 'HZ_SYNC_CENTRAL'; local_exists = $false; error = 'CreateNewLocal failed' } })
        }
        if ([string]$a.code -match "d = r'([^']+)'") { New-Item -ItemType Directory -Force -Path $Matches[1] | Out-Null; Set-Content -LiteralPath (Join-Path $Matches[1] 'HZ_SYNC_LOCAL.rvt') -Value 'x' }
        return (Ok @{ output = [pscustomobject]@{ central = 'C:\t\HZ_SYNC_CENTRAL.rvt'; local = 'C:\t\HZ_SYNC_LOCAL.rvt'; central_title = 'HZ_SYNC_CENTRAL'; local_exists = $true; error = $null } })
    }
    switch ([string]$a.operation) {
        'open' {
            if ([string]$a.file_path -like '*HZ_CLOSED*') { return (Ok @{ title = 'HZ_CLOSED_detached' }) }
            return (Ok @{ title = 'HZ_SYNC_LOCAL' })
        }
        'close' { return (Ok @{ would_discard_unsaved = $false; closed = $true }) }
        'sync_with_central' {
            if ($a.target_document -eq 'HZ_WRITE') {
                if ($script:mode -eq 'no_code') { return (Refused $null 'horizun_document_session is hidden/refused by permission_profile=safe_write') }
                return (Refused 'not_workshared' "'HZ_WRITE' is not workshared")
            }
            if ($a.target_document -eq 'HZ_CLOSED_detached') { return (Refused 'detached_copy' "'HZ_CLOSED_detached' is a DETACHED copy") }
            if ($script:mode -eq 'owner_off') { return (Refused 'sync_not_authorised' 'Synchronize with central is OFF ... Horizun Hub tab > Advanced options > Synchronize with central.') }
            $rel = [string]$a.relinquish
            if ($a.dry_run) {
                $est = switch ($rel) {
                    'keep_borrowed' { @{ owned_worksets = 0; owned_elements = 1; borrowed_elements = 1 } }
                    'none' { @{ owned_worksets = 1; owned_elements = 40; borrowed_elements = 1 } }
                    default { @{ owned_worksets = 0; owned_elements = 3; borrowed_elements = 1 } }
                }
                return (Ok @{ operation = 'sync_with_central'; dry_run = $true; preview_kind = 'estimate'; owned_worksets = $est.owned_worksets
                        owned_elements = $est.owned_elements; borrowed_elements = $est.borrowed_elements; has_all_changes_from_central = $true
                        update_status_sample = [pscustomobject]@{ sample_size = 12 }; confirmation_token = ('tok-' + $rel) })
            }
            if ($a.confirmation_token -ne ('tok-' + $rel)) { return (Refused 'confirmation_rejected' 'bad token') }
            $own = switch ($rel) {
                'keep_borrowed' {
                    if ($script:mode -eq 'keep_fails') { @{ verified = $false; owned_worksets_after = 0; owned_elements_after = 0; unexpectedly_released = @([pscustomobject]@{ unique_id = 'uid-borrowed' }); unexpectedly_owned = @() } }
                    else { @{ verified = $true; owned_worksets_after = 0; owned_elements_after = 1; unexpectedly_released = @(); unexpectedly_owned = @() } }
                }
                'none' { @{ verified = $true; owned_worksets_after = 1; owned_elements_after = 40; unexpectedly_released = @(); unexpectedly_owned = @(); arrived_in_owned_worksets = @() } }
                default {
                    $ok = $script:mode -ne 'apply_fails'
                    @{ verified = $ok; owned_worksets_after = 0; owned_elements_after = $(if ($ok) { 0 } else { 2 }); unexpectedly_released = @(); unexpectedly_owned = @() }
                }
            }
            return (Ok @{ operation = 'sync_with_central'; dry_run = $false; sync_verified = $own.verified; has_all_changes_from_central_after = $true
                    central_file_written = $true; is_modified_after = $false; ownership = [pscustomobject]$own })
        }
    }
    return (Ok @{})
}
function Ctx($writeGate) {
    [pscustomobject]@{ Call = $call; Apply = $null; Document = 'HZ_WRITE'; RunId = $runId; Year = '2026'; WriteGate = $writeGate; ClosedWorksetDocument = 'HZ_CLOSED' }
}
function Case1($cases, $prefix) { @($cases | Where-Object { $_.Name -like ($prefix + '*') }) | Select-Object -First 1 }
function Outcome($cases, $prefix) { (Case1 $cases $prefix).Outcome }
function IndexOf($pattern) { for ($i = 0; $i -lt $script:calls.Count; $i++) { if ($script:calls[$i] -like $pattern) { return $i } }; return -1 }
function AllNamed($cases) { ((@($cases | ForEach-Object { $_.Name }) | Sort-Object) -join '|') -eq ((@($module.Catalog | ForEach-Object { $_.Name }) | Sort-Object) -join '|') }
$kKeep = 'sync central: keep_borrowed'
$kNone = 'sync central: relinquish=none'
$kReal = 'sync central: preview is'
$kOwner = 'sync central: with the owner'

try {
    $r = & $module.Run (Ctx $true)
    Check 'write gate closed: every case not_covered' ((@($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0) -and $r.Count -eq 6)

    $script:mode = 'python_off'
    $r = & $module.Run (Ctx $false)
    Check 'not_workshared refusal passes' ((Outcome $r 'sync central: the non-workshared') -eq 'pass')
    Check 'detached copy refusal passes' ((Outcome $r 'sync central: a detached') -eq 'pass')
    Check 'the python call names the detached copy as its target_document' (@($script:pyTargets) -contains 'HZ_CLOSED_detached')
    Check 'no python: owner-off case is not_covered, reason says python is disabled' (
        (Outcome $r $kOwner) -eq 'not_covered' -and (Case1 $r $kOwner).Detail -like 'python disabled*')
    Check 'no python: real, keep_borrowed and none are not_covered, never forced' (
        (Outcome $r $kReal) -eq 'not_covered' -and (Outcome $r $kKeep) -eq 'not_covered' -and (Outcome $r $kNone) -eq 'not_covered')
    Check 'no python: every catalog name is reported' (AllNamed $r)
    Check 'the detached fixture is closed afterwards' (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_CLOSED_detached' }).Count -ge 1)

    $script:mode = 'python_refused'
    $r = & $module.Run (Ctx $false)
    Check 'python refused: the reason names the refusal, not a disabled tool' ((Case1 $r $kReal).Detail -like 'python refused: document_not_active*')

    $script:mode = 'local_fails'; $script:calls.Clear()
    $r = & $module.Run (Ctx $false)
    Check 'local creation failed after SaveAs: the central is closed under its NEW title' (
        (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_SYNC_CENTRAL' }).Count -ge 1) -and
        (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_CLOSED_detached' }).Count -eq 0))
    Check 'local creation failed: the reason carries the script error' ((Outcome $r $kReal) -eq 'not_covered' -and (Case1 $r $kReal).Detail -like '*CreateNewLocal failed*')

    $script:mode = 'no_code'
    $r = & $module.Run (Ctx $false)
    Check 'a refusal without a detail code is not_covered, never "not refused"' ((Outcome $r 'sync central: the non-workshared') -eq 'not_covered')

    $script:mode = 'owner_off'; $script:calls.Clear()
    $r = & $module.Run (Ctx $false)
    Check 'owner off: refusal naming Advanced options passes' ((Outcome $r $kOwner) -eq 'pass')
    Check 'owner off: real, keep_borrowed and none not_covered' (
        (Outcome $r $kReal) -eq 'not_covered' -and (Outcome $r $kKeep) -eq 'not_covered' -and (Outcome $r $kNone) -eq 'not_covered')
    Check 'owner off: the scratch local and the renamed central are both closed' (
        (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_SYNC_LOCAL' }).Count -ge 1) -and
        (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_SYNC_CENTRAL' }).Count -ge 1))
    Check 'the central is closed before its local is opened' (
        (IndexOf 'horizun_document_session:close:HZ_SYNC_CENTRAL') -ge 0 -and
        (IndexOf 'horizun_document_session:close:HZ_SYNC_CENTRAL') -lt (IndexOf 'horizun_document_session:open::*HZ_SYNC_LOCAL*'))
    Check 'owner off: nothing failed, so the scratch folder is removed' (-not (Test-Path -LiteralPath $scratch))
    Check 'the new local is first opened and saved on the write document, after the central closed and before the typed open' (
        (IndexOf 'first-open:HZ_WRITE') -gt (IndexOf 'horizun_document_session:close:HZ_SYNC_CENTRAL') -and
        (IndexOf 'first-open:HZ_WRITE') -lt (IndexOf 'horizun_document_session:open::*HZ_SYNC_LOCAL*'))

    $script:mode = 'first_open_fails'; $script:calls.Clear()
    $r = & $module.Run (Ctx $false)
    Check 'a local still reading as a central after its first open is never opened; the cases say why' (
        (Outcome $r $kReal) -eq 'not_covered' -and (Case1 $r $kReal).Detail -like '*did not become a local*is_central=True*' -and
        (IndexOf 'horizun_document_session:open::*HZ_SYNC_LOCAL*') -lt 0)

    $script:mode = 'owner_on'; $script:pyTargets.Clear()
    $r = & $module.Run (Ctx $false)
    Check 'owner on: estimate then verified sync passes' ((Outcome $r $kReal) -eq 'pass')
    Check 'owner on: keep_borrowed keeps exactly the borrowed element' ((Outcome $r $kKeep) -eq 'pass')
    Check 'owner on: relinquish=none keeps the taken workset' ((Outcome $r $kNone) -eq 'pass')
    Check 'owner on: the borrow and take scripts name the scratch local' (@($script:pyTargets | Where-Object { $_ -eq 'HZ_SYNC_LOCAL' }).Count -eq 2)
    Check 'owner on: owner-off case is not_covered' ((Outcome $r $kOwner) -eq 'not_covered')
    Check 'owner on: the scratch folder is removed after a clean run' (-not (Test-Path -LiteralPath $scratch))
    Check 'every catalog name is reported' (AllNamed $r)

    $script:mode = 'no_borrow'
    $r = & $module.Run (Ctx $false)
    Check 'nothing to borrow: keep_borrowed not_covered with the reason, none still measured' (
        (Outcome $r $kKeep) -eq 'not_covered' -and (Case1 $r $kKeep).Detail -like 'could not borrow*' -and (Outcome $r $kNone) -eq 'pass')

    $script:mode = 'keep_fails'
    $r = & $module.Run (Ctx $false)
    Check 'a released borrowed element fails keep_borrowed' ((Outcome $r $kKeep) -eq 'fail')
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue

    $script:mode = 'apply_fails'
    $r = & $module.Run (Ctx $false)
    Check 'a failed verification fails the case' ((Outcome $r $kReal) -eq 'fail')
    Check 'a failed relinquish=all leaves keep_borrowed and none not_covered' ((Outcome $r $kKeep) -eq 'not_covered' -and (Outcome $r $kNone) -eq 'not_covered')
    Check 'a failed case keeps the scratch folder and names it' ((Test-Path -LiteralPath $scratch) -and (Case1 $r $kReal).Detail -like ('*scratch kept for inspection at ' + $scratch + '*'))
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fails -gt 0) { "sync-central.tests: $fails FAILED"; exit 1 } else { 'sync-central.tests: all passed'; exit 0 }
