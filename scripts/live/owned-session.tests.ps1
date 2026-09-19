# -----------------------------------------------------------------------------
# Horizun Revit MCP - original Horizun code.
#
# The rules of owned-session.ps1 (and therefore of scripts/dwg-bim/session.ps1),
# exercised WITHOUT a Revit and without anybody's session: canned bridge answers,
# a temp state root, and a harmless helper process (a minimised pwsh sleeping)
# standing in for "the Revit this run started". The only process this script ever
# stops is a helper it started itself.
#
#   pwsh -NoProfile -File scripts/live/owned-session.tests.ps1
#   exit 0 = every case held; 1 = at least one did not.
# -----------------------------------------------------------------------------
[CmdletBinding()]
param([string]$SummaryPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('hz-owned-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $root
$env:HORIZUN_OWNED_SESSION_ROOT = Join-Path $root 'state'
. (Join-Path $PSScriptRoot 'owned-session.ps1')

$script:Passed = 0; $script:Failed = 0; $script:Helpers = @()
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:Passed++; Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green }
    else { $script:Failed++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) -ForegroundColor Red }
}
function Start-Helper {
    $p = Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 600' -WindowStyle Minimized -PassThru
    $script:Helpers += $p
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 200 }
    return $p
}
function Stop-Helpers { foreach ($h in $script:Helpers) { try { if (-not $h.HasExited) { $h.Kill(); $h.WaitForExit(5000) | Out-Null } } catch { } }; $script:Helpers = @() }
function Doc([string]$Title, [string]$Path, $Active = $false) { [ordered]@{ title = $Title; path = $Path; is_active = $Active } }
$pwshExe = (Get-Process -Id $PID).Path

# Canned outside world; the helper is the "Revit". Every override is per case.
function New-Fake {
    param($Docs = @(), [bool]$HealthOk = $true, [int]$RestoreCode = 0, $RevitProcesses = @())
    $state = @{ docs = @($Docs); healthOk = $HealthOk; restore = $RestoreCode; revit = @($RevitProcesses)
                manifest = @{ installed_present = $true; dev_present = $false; aside_present = $false }
                helper = $null; enableCalls = 0; restoreCalls = 0; closeCalls = @(); onClose = $null; startCalls = 0 }
    $real = New-HzMatrixProbes -Repo 'C:\nowhere-hz-tests' -ServerExe 'C:\nowhere-hz-tests\horizun-mcp.exe'
    $probes = @{
        GetProcess = $real.GetProcess; CloseMainWindow = $real.CloseMainWindow; WaitExit = $real.WaitExit
        RevitProcesses = { return ,@($state.revit) }.GetNewClosure()
        StartRevit = { param($Exe, $FailAction) $state.startCalls++; $h = Start-Helper; $state.helper = $h; return $h }.GetNewClosure()
        Health = { param($Year, $Dir)
            if (-not $state.healthOk) { return @{ ok = $false; error = 'the bridge did not answer (simulated)' } }
            $pidNow = if ($state.helper) { $state.helper.Id } else { 0 }
            return @{ ok = $true; documents = @($state.docs); document_count = @($state.docs).Count; process_id = $pidNow } }.GetNewClosure()
        CloseDocument = { param($Request)
            $state.closeCalls += [string]$Request['target']
            $state.docs = @(@($state.docs) | Where-Object { ([string]$_['path'] -ne [string]$Request['expect_path']) -or ([string]$_['title'] -ne [string]$Request['expect_title']) })
            if ($null -ne $state.onClose) { & $state.onClose $state $Request }
            return @{ ok = $true; closed = $true } }.GetNewClosure()
        Enable = { param($Year) $state.enableCalls++; $state.manifest.dev_present = $true; $state.manifest.aside_present = $true; return 0 }.GetNewClosure()
        Restore = { param($Year) $state.restoreCalls++
            if ($state.restore -eq 0) { $state.manifest.dev_present = $false; $state.manifest.aside_present = $false; $state.manifest.installed_present = $true }
            return $state.restore }.GetNewClosure()
        ManifestState = { param($Year)
            $dir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
            $expected = Get-HzNormalizedPath (Join-Path $dir 'Horizun\Horizun.Revit.dll')
            return @{ installed_present = $state.manifest.installed_present; dev_present = $state.manifest.dev_present
                      aside_present = $state.manifest.aside_present; dev_dll = $null; installed_assembly = $expected
                      installed_manifest_error = $null; expected_installed_assembly = $expected } }.GetNewClosure()
        InstalledDllState = { param($Year) return @{ present = $true; sha256 = 'aaaa'; error = $null; path = 'C:\nowhere\x.dll' } }
    }
    return @{ probes = $probes; state = $state }
}
function Reset-State { Remove-Item -LiteralPath $env:HORIZUN_OWNED_SESSION_ROOT -Recurse -Force -ErrorAction SilentlyContinue }
function StartFake($f) { return (Start-HzOwnedSession -Probes $f.probes -Year '2099' -Dir $root -RevitExe $pwshExe) }
$userDoc = 'C:\Users\someone\Documents\Proyecto.rvt'
$ourDoc = 'C:\hz-tests\models\HZ_SO_CLEAN.rvt'

Write-Host '== owned-session safety ==' -ForegroundColor Cyan
try {
    # 1. The happy path: start records the helper, register holds the path, stop closes it, exits and restores.
    Reset-State
    $f = New-Fake
    $s = StartFake $f
    Check 'start records pid, start time and executable of the process it started' (($s.identity.pid -eq $f.state.helper.Id) -and $s.identity.start_time -and ($s.identity.exe -eq $pwshExe))
    Check 'the state is persisted before anything else can happen' (Test-Path -LiteralPath (Get-HzOwnedStatePath '2099'))
    $f.state.docs = @(Doc 'HZ_SO_CLEAN' $ourDoc $true)
    $reg = Register-HzOwnedDocument -Probes $f.probes -Year '2099' -ExpectedTitle 'HZ_SO_CLEAN'
    Check 'register records the active document by its path' ($reg.ok -and ((Read-HzOwnedState '2099').ledger.documents[0].path -eq $ourDoc))
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'stop closes the registered document, the process exits, the year is restored' ($r.ok -and ($r.close.state -eq 'closed') -and ($f.state.closeCalls -contains $ourDoc) -and ($r.restore.state -eq 'restored'))
    Check 'a verified stop removes the state' (-not (Test-Path -LiteralPath (Get-HzOwnedStatePath '2099')))
    Stop-Helpers

    # 2. A document the run did not register, inside the run's own process: nothing is closed.
    Reset-State
    $f = New-Fake
    $s = StartFake $f
    $f.state.docs = @(Doc 'HZ_SO_CLEAN' $ourDoc $true)
    $null = Register-HzOwnedDocument -Probes $f.probes -Year '2099' -ExpectedTitle 'HZ_SO_CLEAN'
    $f.state.docs = @((Doc 'HZ_SO_CLEAN' $ourDoc), (Doc 'Proyecto' $userDoc $true))
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'a foreign document in our own process leaves it running and closes NOTHING' ((-not $r.ok) -and ($r.close.state -eq 'left_running_foreign_document') -and ($f.state.closeCalls.Count -eq 0) -and (-not $f.state.helper.HasExited))
    Check 'that stop writes recovery pending and keeps the state' ($r.recovery_pending -and (Test-Path -LiteralPath $r.recovery_pending) -and ((Read-HzOwnedState '2099').phase -eq 'left_running'))
    Check 'and does not restore the manifest under a running session' ($f.state.restoreCalls -eq 0)
    Stop-Helpers

    # 3. A personal document whose name starts with HZ_: a prefix is not ownership.
    Reset-State
    $f = New-Fake
    $s = StartFake $f
    $f.state.docs = @(Doc 'HZ_SO_CLEAN' $ourDoc $true)
    $null = Register-HzOwnedDocument -Probes $f.probes -Year '2099' -ExpectedTitle 'HZ_SO_CLEAN'
    $f.state.docs = @((Doc 'HZ_SO_CLEAN' $ourDoc), (Doc 'HZ_PROYECTO_USUARIO' 'C:\Users\someone\HZ_PROYECTO_USUARIO.rvt' $true))
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'an HZ_-named personal document is foreign and nothing is closed' (($r.close.state -eq 'left_running_foreign_document') -and ($f.state.closeCalls.Count -eq 0))
    Stop-Helpers

    # 4. A document that appears DURING the close sequence stops it.
    Reset-State
    $f = New-Fake
    $s = StartFake $f
    $f.state.docs = @(Doc 'HZ_A' 'C:\hz-tests\HZ_A.rvt' $true)
    $null = Register-HzOwnedDocument -Probes $f.probes -Year '2099' -ExpectedTitle 'HZ_A'
    $f.state.docs = @(Doc 'HZ_B' 'C:\hz-tests\HZ_B.rvt' $true) + @($f.state.docs)
    $null = Register-HzOwnedDocument -Probes $f.probes -Year '2099' -ExpectedTitle 'HZ_B'
    $f.state.onClose = { param($st, $req) $st.docs = @($st.docs) + @(Doc 'Nuevo' 'C:\Users\someone\Nuevo.rvt') }
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'a document opened while the close runs stops the sequence; the process stays' (($r.close.state -eq 'left_running_foreign_document') -and ($f.state.closeCalls.Count -eq 1) -and (-not $f.state.helper.HasExited))
    Stop-Helpers

    # 5. A reused pid (another start time): not ours.
    Reset-State
    $f = New-Fake
    $s = StartFake $f
    $st = Read-HzOwnedState '2099'
    $st.identity.start_time = ([DateTimeOffset]$f.state.helper.StartTime).AddHours(-3).ToUniversalTime().ToString('o')
    Save-HzOwnedState '2099' $st
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'a pid whose start time differs is not ours: left running, nothing closed' (($r.close.state -eq 'left_running_identity') -and (-not $f.state.helper.HasExited) -and ($f.state.closeCalls.Count -eq 0))
    Stop-Helpers

    # 6. Health that cannot answer is not "no documents".
    Reset-State
    $f = New-Fake
    $s = StartFake $f
    $f.state.healthOk = $false
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'an unanswered health leaves the process running' (($r.close.state -eq 'left_running_health') -and (-not $f.state.helper.HasExited))
    $real = ConvertFrom-HzHealthReply -ReplyPath (Join-Path $root 'missing.json')
    Check 'a missing health reply is not a document list' (-not $real.ok)
    Stop-Helpers

    # 7. A Revit of the same year that this run did not start: start refuses before changing anything.
    Reset-State
    $f = New-Fake -RevitProcesses @([pscustomobject]@{ Id = 4242; Exe = 'C:\Program Files\Autodesk\Revit 2099\Revit.exe'; ExeError = $null; HasExited = $false; ExitStateReadable = $true; StartTime = (Get-Date) })
    $threw = $false; try { $null = StartFake $f } catch { $threw = $true }
    Check 'a foreign Revit of the same year: start refuses, enables nothing, starts nothing, records nothing' ($threw -and ($f.state.enableCalls -eq 0) -and ($f.state.startCalls -eq 0) -and (-not (Test-Path -LiteralPath (Get-HzOwnedStatePath '2099'))))
    $f = New-Fake -RevitProcesses @([pscustomobject]@{ Id = 4243; Exe = $null; ExeError = 'Access is denied'; HasExited = $false; ExitStateReadable = $true; StartTime = (Get-Date) })
    $threw = $false; try { $null = StartFake $f } catch { $threw = $true }
    Check 'a Revit whose year cannot be read also blocks start' ($threw -and ($f.state.enableCalls -eq 0))

    # 8. A failed restore is recorded and recoverable, never reported as a clean stop.
    Reset-State
    $f = New-Fake -RestoreCode 1
    $s = StartFake $f
    $r = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'a failed restore: not ok, recovery pending written, state kept as restore_pending' ((-not $r.ok) -and ($r.restore.state -eq 'restore_failed') -and $r.recovery_pending -and ((Read-HzOwnedState '2099').phase -eq 'restore_pending'))
    $f.state.restore = 0
    $r2 = Stop-HzOwnedSession -Probes $f.probes -Year '2099' -ExitTimeoutSec 20
    Check 'running stop again recovers it and clears the pending record' ($r2.ok -and ($r2.restore.state -eq 'restored') -and (-not (Test-Path -LiteralPath (Join-Path $env:HORIZUN_OWNED_SESSION_ROOT 'recovery-pending-2099.json'))))
    Stop-Helpers

    # 9. Two runs at once: the lock refuses the second; a recorded session refuses a second start.
    Reset-State
    $lock = Enter-HzOwnedLock -Year '2099'
    $threw = $false
    try { $job = Start-Job -ScriptBlock { param($m, $r) $env:HORIZUN_OWNED_SESSION_ROOT = $r; . $m; try { $l = Enter-HzOwnedLock -Year '2099'; $l.Dispose(); 'acquired' } catch { 'refused' } } -ArgumentList (Join-Path $PSScriptRoot 'owned-session.ps1'), $env:HORIZUN_OWNED_SESSION_ROOT
          $res = Receive-Job -Job $job -Wait -AutoRemoveJob } finally { $lock.Dispose() }
    Check 'a second process cannot take the year lock while the first holds it' ($res -eq 'refused')
    $f = New-Fake
    $s = StartFake $f
    $threw = $false; try { $null = StartFake $f } catch { $threw = $true }
    Check 'a second start on a recorded session is refused' ($threw -and ($f.state.startCalls -eq 1))
    Stop-Helpers

    # 10. The wrappers carry no forced termination and no name-based ownership.
    $session = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'dwg-bim\session.ps1') -Raw
    $module = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-session.ps1') -Raw
    Check 'session.ps1 never kills and never closes a window itself' (($session -notmatch 'Stop-Process|\.Kill\(|CloseMainWindow') -and ($module -notmatch 'Stop-Process|\.Kill\(|CloseMainWindow\(\)'))
    Check 'session.ps1 grants nothing by a document name prefix' ($session -notmatch 'ExpectPrefix|\^HZ_')
    Check 'the stop path goes through the module that checks documents before any close' ($module -match 'Close-HzRehearsalSession' -and $module -match 'Restore-HzYearSession')

    # 11. The recorded-Revit API (live-cycle, structure matrix, deploy-and-verify).
    Reset-State
    $f = New-Fake
    $rec = Start-HzRecordedRevit -Probes $f.probes -Name 'unit' -Year '2099' -Dir $root -RevitExe $pwshExe
    Check 'recorded start keeps pid, start time and executable' (($rec.identity.pid -eq $f.state.helper.Id) -and $rec.identity.start_time -and (Test-Path -LiteralPath (Get-HzRecordPath 'unit' '2099')))
    $threw = $false; try { $null = Start-HzRecordedRevit -Probes $f.probes -Name 'unit' -Year '2099' -Dir $root -RevitExe $pwshExe } catch { $threw = $true }
    Check 'a second recorded start with a live record is refused' ($threw -and ($f.state.startCalls -eq 1))
    $f.state.docs = @(Doc 'HZ_REC' 'C:\hz-tests\HZ_REC.rvt' $true)
    $reg = Register-HzRecordedDocument -Probes $f.probes -Name 'unit' -Year '2099' -ExpectedTitle 'HZ_REC'
    Check 'recorded register holds the document by path' ($reg.ok)
    $f.state.docs = @((Doc 'HZ_REC' 'C:\hz-tests\HZ_REC.rvt'), (Doc 'HZ_PERSONAL' 'C:\Users\someone\HZ_PERSONAL.rvt' $true))
    $r = Close-HzRecordedRevit -Probes $f.probes -Name 'unit' -Year '2099' -ExitTimeoutSec 20
    Check 'recorded close with a foreign HZ_ document closes nothing and keeps the record' (($r.state -eq 'left_running_foreign_document') -and ($f.state.closeCalls.Count -eq 0) -and (-not $f.state.helper.HasExited) -and (Test-Path -LiteralPath (Get-HzRecordPath 'unit' '2099')) -and $r.recovery_pending)
    $f.state.docs = @(Doc 'HZ_REC' 'C:\hz-tests\HZ_REC.rvt' $true)
    $r = Close-HzRecordedRevit -Probes $f.probes -Name 'unit' -Year '2099' -ExitTimeoutSec 20
    Check 'once the foreign document is gone, the recorded close closes ours and removes the record' (($r.state -eq 'closed') -and ($f.state.closeCalls -contains 'C:\hz-tests\HZ_REC.rvt') -and (-not (Test-Path -LiteralPath (Get-HzRecordPath 'unit' '2099'))))
    $r = Close-HzRecordedRevit -Probes $f.probes -Name 'unit' -Year '2099'
    Check 'nothing recorded reports no_record and touches nothing' ($r.state -eq 'no_record')
    Stop-Helpers
    $f = New-Fake -RevitProcesses @([pscustomobject]@{ Id = 4244; Exe = 'C:\Program Files\Autodesk\Revit 2099\Revit.exe'; ExeError = $null; HasExited = $false; ExitStateReadable = $true; StartTime = (Get-Date) })
    $threw = $false; try { $null = Start-HzRecordedRevit -Probes $f.probes -Name 'unit' -Year '2099' -Dir $root -RevitExe $pwshExe } catch { $threw = $true }
    Check 'recorded start refuses beside a Revit of the same year it did not start' ($threw -and ($f.state.startCalls -eq 0) -and (-not (Test-Path -LiteralPath (Get-HzRecordPath 'unit' '2099'))))

    # 13. The close the module SENDS is one the contract accepts (measured live 2026-09-19: the close
    #     carried expected_version, the server refused it, and the session was - safely - left running).
    $contract = Get-Content -LiteralPath (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\Horizun.Contracts\Contract.cs') -Raw
    $m = [regex]::Match($contract, '\["close"\]\s*=\s*new\[\]\s*\{([^}]*)\}')
    $allowed = @('operation', 'dry_run', 'idempotency_key') + @([regex]::Matches($m.Groups[1].Value, '"([a-z_]+)"') | ForEach-Object { $_.Groups[1].Value })
    $ym = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'year-matrix.session.ps1') -Raw
    $sent = @([regex]::Matches($ym, "@\{\s*operation\s*=\s*'close'[^}]*\}") | ForEach-Object {
        [regex]::Matches($_.Value, '(?:^|[;{\s])([a-z_]+)\s*=') | ForEach-Object { $_.Groups[1].Value } })
    $extra = @($sent | Where-Object { $_ -notin $allowed } | Sort-Object -Unique)
    Check 'the close the module sends uses only keys the contract accepts for close' ($m.Success -and $sent.Count -gt 0 -and $extra.Count -eq 0) ("not accepted: " + ($extra -join ', '))

    # 12. The migrated scripts: no name-based kill, no MCP server termination, no discard of what is not registered.
    $scripts = [ordered]@{
        'live-cycle.ps1'               = Join-Path (Split-Path -Parent $PSScriptRoot) 'live-cycle.ps1'
        'verify-structure-matrix.ps1'  = Join-Path $PSScriptRoot 'verify-structure-matrix.ps1'
        'deploy-and-verify.ps1'        = Join-Path $PSScriptRoot 'deploy-and-verify.ps1'
    }
    foreach ($k in $scripts.Keys) {
        $code = (Get-Content -LiteralPath $scripts[$k] -Raw) -split "`n" | Where-Object { $_ -notmatch '^\s*#' }
        $code = $code -join "`n"
        Check "$k never stops processes, kills or closes a window itself" ($code -notmatch 'Stop-Process|\.Kill\(|CloseMainWindow|taskkill')
        Check "$k never starts Revit outside the recorded API" ($code -notmatch 'Start-Process')
        Check "$k never sends discard_unsaved itself" ($code -notmatch 'discard_unsaved')
        Check "$k closes and starts only through the recorded API" ($code -match 'Close-HzRecordedRevit' -and $code -match 'Start-HzRecordedRevit' -and $code -match 'Register-HzRecordedDocument')
    }
}
finally {
    Stop-Helpers
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ("== {0} passed, {1} failed ==" -f $script:Passed, $script:Failed)
if ($SummaryPath) { @{ passed = $script:Passed; failed = $script:Failed } | ConvertTo-Json | Set-Content -LiteralPath $SummaryPath -Encoding utf8 }
exit ([int]($script:Failed -gt 0))
