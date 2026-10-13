#Requires -Version 5.1
<#
  The Run-key sweep of complete-install.ps1, against a THROWAWAY key.

  The sweep used to enumerate @(Get-ItemProperty ...).PSObject.Properties - the
  properties of the one-element ARRAY (Length, Count, Rank...), never the values
  of the key - so it removed nothing, on 5.1 and on 7 alike, and an orphaned
  resume entry relaunched a hidden helper at every logon forever.

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

function Value-Names { return @((Get-Item -LiteralPath $runKey).GetValueNames()) }

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

    # ---- 1. -CancelPending removes every Horizun resume entry and nothing else. ----
    Seed @('HorizunMCPCompleteInstall', 'HorizunMCPCompleteInstall-aaaaaaaa11', 'HorizunMCPCompleteInstall-bbbbbbbb22', 'OtherVendorApp')
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -CancelPending -StatusPath $status -RunKey $runKey | Out-Null
    $left = Value-Names
    Assert 'CancelPending exits 0' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    Assert 'CancelPending removed the legacy and generation entries' (@($left | Where-Object { $_ -like 'HorizunMCPCompleteInstall*' }).Count -eq 0) ($left -join ', ')
    Assert 'CancelPending left the unrelated entry alone' ('OtherVendorApp' -in $left) ($left -join ', ')

    # ---- 2. A run sweeps stale generations; a failing run caps its own retries. ----
    # Codex is named but has no configuration here, so registration fails every
    # time - deterministically, with no client process involved.
    Seed @('HorizunMCPCompleteInstall', 'HorizunMCPCompleteInstall-oldgen00001')
    $generation = 'newgen' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $own = 'HorizunMCPCompleteInstall-' + $generation
    $states = @()
    for ($i = 1; $i -le 3; $i++) {
        & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -Client Codex -ServerPath $server -StatusPath $status `
            -WaitTimeoutMinutes 1 -NoLiveWait -ClientStateFile $clientState -Generation $generation -RunKey $runKey *> $null
        $code = $LASTEXITCODE
        $doc = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
        $states += $doc
        $names = Value-Names
        if ($i -eq 1) {
            Assert 'a failing registration exits 1' ($code -eq 1) "exit $code"
            Assert 'the stale legacy entry was swept' ('HorizunMCPCompleteInstall' -notin $names) ($names -join ', ')
            Assert 'the stale generation entry was swept' ('HorizunMCPCompleteInstall-oldgen00001' -notin $names) ($names -join ', ')
            Assert 'the unrelated entry survives a run' ('OtherVendorApp' -in $names) ($names -join ', ')
            Assert 'first failure keeps its own entry for a retry' ($own -in $names) ($names -join ', ')
            Assert 'the state is registration_failed' ($doc.state -eq 'registration_failed') $doc.state
            Assert 'the state counts the attempt' ([int]$doc.resume_attempts -eq 1) ([string]$doc.resume_attempts)
        }
    }
    $names = Value-Names
    $last = $states[-1]
    Assert 'the third failure removes its own entry' ($own -notin $names) ($names -join ', ')
    Assert 'the final state says retries are exhausted' ([bool]$last.resume_cleared -and [int]$last.resume_attempts -eq 3) ($last | ConvertTo-Json -Compress)
    Assert 'the unrelated entry is still there at the end' ('OtherVendorApp' -in $names) ($names -join ', ')

    # ---- 3. -Client Both on a machine with NEITHER command-line client configured. ----
    # Setup, the bootstrap and install.ps1 all pass -Client Both. On a machine with only
    # Claude Desktop, register-client answers exit 2 ("neither config exists"), which is
    # not a failure: it must read as failed_no_client (exit 2) and must NOT leave a Run
    # entry that relaunches a hidden worker at the next logons (backlog P0 #2).
    $generation = 'bothgen' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $own = 'HorizunMCPCompleteInstall-' + $generation
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script -Client Both -ServerPath $server -StatusPath $status `
        -WaitTimeoutMinutes 1 -NoLiveWait -ClientStateFile $clientState -Generation $generation -RunKey $runKey *> $null
    $code = $LASTEXITCODE
    $doc = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
    $names = Value-Names
    Assert '-Client Both with no CLI configured exits 2, not 1' ($code -eq 2) "exit $code"
    Assert 'and reads as failed_no_client, not registration_failed' ($doc.state -eq 'failed_no_client') $doc.state
    Assert 'and leaves no resume entry behind' ($own -notin $names) ($names -join ', ')
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
