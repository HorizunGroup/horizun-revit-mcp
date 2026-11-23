#Requires -Version 5.1
<#
  complete-install.ps1 and HKCU Run, against a THROWAWAY key.

  Releases up to 4.2 armed a logon resume entry that relaunched a hidden,
  policy-bypassing PowerShell; an antivirus behaviour engine read that as malware
  (Norton IDP.HEUR.26, 2026-11-03). The helper now never writes Run: every run,
  failing or not, removes whatever Horizun entries an older release left, and
  nothing else.

  Never touches the real HKCU\...\CurrentVersion\Run: the key under test lives at
  HKCU:\Software\HorizunTest\Run-<guid> and is deleted afterwards. Runs under
  whichever PowerShell launched it, so run it under powershell.exe AND pwsh.
#>
$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-run-entries-' + [guid]::NewGuid().ToString('N'))
$runKey = 'HKCU:\Software\HorizunTest\Run-' + [guid]::NewGuid().ToString('N')
$oldProfile = $env:USERPROFILE
$oldLocal = $env:LOCALAPPDATA
$oldAppData = $env:APPDATA
$hostExe = (Get-Process -Id $PID).Path
$script = Join-Path $PSScriptRoot 'complete-install.ps1'
$failed = 0

function Assert([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" }
        $script:failed++
    }
}

function Value-Names {
    if (-not (Test-Path -LiteralPath $runKey)) { return @() }
    return @((Get-Item -LiteralPath $runKey).GetValueNames())
}

function Seed([string[]]$Names) {
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    foreach ($n in $Names) { New-ItemProperty -LiteralPath $runKey -Name $n -Value 'x' -PropertyType String -Force | Out-Null }
}

try {
    Write-Host "install-run-entries under PowerShell $($PSVersionTable.PSVersion)"
    $env:USERPROFILE = Join-Path $root 'profile'
    $env:LOCALAPPDATA = Join-Path $root 'local'
    $env:APPDATA = Join-Path $root 'roaming'
    New-Item -ItemType Directory -Path $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA -Force | Out-Null
    $server = Join-Path $root 'horizun-mcp.exe'
    'fake server' | Set-Content -LiteralPath $server -Encoding ASCII
    $status = Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json'
    $clientState = Join-Path $root 'running-clients.txt'
    New-Item -ItemType File -Path $clientState -Force | Out-Null
    $legacy = @('HorizunMCPCompleteInstall', 'HorizunMCPCompleteInstall-aaaaaaaa11', 'HorizunMCPCompleteInstall-bbbbbbbb22')

    # ---- 1. -CancelPending removes every Horizun resume entry and nothing else. ----
    Seed ($legacy + 'OtherVendorApp')
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -CancelPending -StatusPath $status -RunKey $runKey | Out-Null
    $left = Value-Names
    Assert 'CancelPending exits 0' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    Assert 'CancelPending removed the legacy and generation entries' (@($left | Where-Object { $_ -like 'HorizunMCPCompleteInstall*' }).Count -eq 0) ($left -join ', ')
    Assert 'CancelPending left the unrelated entry alone' ('OtherVendorApp' -in $left) ($left -join ', ')

    # ---- 2. A failing run sweeps older entries and writes none of its own. ----
    # Codex is named but has no configuration here, so registration fails -
    # deterministically, with no client process involved.
    Seed $legacy
    $generation = 'newgen' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -Client Codex -ServerPath $server -StatusPath $status `
        -NoLiveWait -ClientStateFile $clientState -Generation $generation -RunKey $runKey *> $null
    $code = $LASTEXITCODE
    $doc = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    $names = Value-Names
    Assert 'a failing registration exits 1' ($code -eq 1) "exit $code"
    Assert 'the state is registration_failed' ($doc.state -eq 'registration_failed') $doc.state
    Assert 'every older Horizun entry was swept' (@($names | Where-Object { $_ -like 'HorizunMCPCompleteInstall*' }).Count -eq 0) ($names -join ', ')
    Assert 'the unrelated entry survives a run' ('OtherVendorApp' -in $names) ($names -join ', ')
    Assert 'no retry counter or logon retry is recorded' (-not $doc.PSObject.Properties['resume_attempts']) ($doc | ConvertTo-Json -Compress)

    # ---- 3. A client that is open: pending (exit 3), nothing armed for later. ----
    'Codex' | Set-Content -LiteralPath $clientState -Encoding ASCII
    $generation = 'opengen' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -Client Codex -ServerPath $server -StatusPath $status `
        -NoLiveWait -ClientStateFile $clientState -Generation $generation -RunKey $runKey *> $null
    $code = $LASTEXITCODE
    $doc = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    $names = Value-Names
    Assert 'an open client exits 3 (pending)' ($code -eq 3) "exit $code"
    Assert 'and reads waiting_for_client_exit, naming the Start menu entry' ($doc.state -eq 'waiting_for_client_exit' -and $doc.detail -match 'Start menu') "$($doc.state): $($doc.detail)"
    Assert 'and no Run entry was written for it' (@($names | Where-Object { $_ -like 'HorizunMCPCompleteInstall*' }).Count -eq 0) ($names -join ', ')
    Clear-Content -LiteralPath $clientState

    # ---- 4. The command line an OLDER Run entry still carries (-Detached ...) ----
    # runs one pass and removes that entry; it does not re-arm itself.
    $generation = 'detgen' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    Seed @("HorizunMCPCompleteInstall-$generation")
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -Client Codex -ServerPath $server -StatusPath $status `
        -WaitTimeoutMinutes 1440 -Detached -NoLiveWait -ClientStateFile $clientState -Generation $generation -RunKey $runKey *> $null
    $names = Value-Names
    Assert 'a legacy -Detached run removes its own old entry' ("HorizunMCPCompleteInstall-$generation" -notin $names) ($names -join ', ')

    # ---- 5. -Client Both on a machine with NEITHER command-line client configured. ----
    # Setup, the bootstrap and install.ps1 all pass -Client Both. On a machine with only
    # Claude Desktop, register-client answers exit 2 ("neither config exists"), which is
    # not a failure: it must read as failed_no_client (exit 2) (backlog P0 #2).
    $generation = 'bothgen' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -Client Both -ServerPath $server -StatusPath $status `
        -NoLiveWait -ClientStateFile $clientState -Generation $generation -RunKey $runKey *> $null
    $code = $LASTEXITCODE
    $doc = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    $names = Value-Names
    Assert '-Client Both with no CLI configured exits 2, not 1' ($code -eq 2) "exit $code"
    Assert 'and reads as failed_no_client, not registration_failed' ($doc.state -eq 'failed_no_client') $doc.state
    Assert 'and leaves no resume entry behind' (@($names | Where-Object { $_ -like 'HorizunMCPCompleteInstall*' }).Count -eq 0) ($names -join ', ')
    Assert 'the unrelated entry is still there at the end' ('OtherVendorApp' -in $names) ($names -join ', ')
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:LOCALAPPDATA = $oldLocal
    $env:APPDATA = $oldAppData
    if (Test-Path -LiteralPath $runKey) { Remove-Item -LiteralPath $runKey -Recurse -Force }
    if (Test-Path -LiteralPath 'HKCU:\Software\HorizunTest') {
        if (@(Get-ChildItem -LiteralPath 'HKCU:\Software\HorizunTest').Count -eq 0 -and
            @((Get-Item -LiteralPath 'HKCU:\Software\HorizunTest').GetValueNames()).Count -eq 0) {
            Remove-Item -LiteralPath 'HKCU:\Software\HorizunTest' -Force
        }
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "install-run-entries: $failed FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'install-run-entries: PASS' -ForegroundColor Green
exit 0
