#Requires -Version 5.1
<#
  The bootstrap's own helpers, dot-sourced from the shipped install-release.ps1
  (dot-sourcing defines its functions and does nothing else). Audit 2026-10-08:

    * Start-Process -Wait waited for Setup's whole process TREE, and Setup leaves
      the completion helper running for as long as a client or Revit keeps it
      waiting - so the bootstrap hung long after Setup had finished.
    * The server path was assumed rather than read from where Setup installed it.
    * The file held non-ASCII text without a BOM, which Windows PowerShell 5.1
      reads as the ANSI code page. A BOM is not the fix: `irm | iex` keeps it as
      U+FEFF and fails on the first token. ASCII is.

  Registry access is limited to a throwaway key under HKCU:\Software\HorizunTest.
  Run under powershell.exe AND pwsh.
#>
$ErrorActionPreference = 'Stop'
$failed = 0
$hostExe = (Get-Process -Id $PID).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-release-helpers-' + [guid]::NewGuid().ToString('N'))
$testKey = 'HKCU:\Software\HorizunTest\Uninstall-' + [guid]::NewGuid().ToString('N')

function Assert([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" }
        $script:failed++
    }
}

$bootstrap = Join-Path (Split-Path -Parent $PSScriptRoot) 'install-release.ps1'
. $bootstrap

try {
    Write-Host "install-release helpers under PowerShell $($PSVersionTable.PSVersion)"
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    # ---- 0. A fresh durable status is READ, not taken as success (backlog P0 #3). ----
    # The bootstrap printed "complete or safely scheduled" and exited 0 whenever the
    # finisher had written ANY status in the last seconds - registration_failed included.
    Assert 'the outcome helper exists' ([bool](Get-Command Get-HorizunCompletionOutcome -ErrorAction SilentlyContinue)) 'Get-HorizunCompletionOutcome is not defined'
    if (Get-Command Get-HorizunCompletionOutcome -ErrorAction SilentlyContinue) {
        foreach ($st in 'registration_failed', 'completion_failed', 'verification_failed') {
            Assert "$st is a failure" ((Get-HorizunCompletionOutcome $st) -eq 'failed') (Get-HorizunCompletionOutcome $st)
        }
        Assert 'failed_no_client is no_client' ((Get-HorizunCompletionOutcome 'failed_no_client') -eq 'no_client') (Get-HorizunCompletionOutcome 'failed_no_client')
        foreach ($st in 'installed_and_registered', 'live_verified', 'awaiting_revit', 'waiting_for_client_exit', 'registration_pending') {
            Assert "$st is ok" ((Get-HorizunCompletionOutcome $st) -eq 'ok') (Get-HorizunCompletionOutcome $st)
        }
        Assert 'an unknown or missing state is not success' ((Get-HorizunCompletionOutcome '') -eq 'failed' -and (Get-HorizunCompletionOutcome 'something_new') -eq 'failed') ''
    }

    # ---- 1. Only Setup's PID is waited for. ---------------------------------------
    # A stand-in "Setup" that starts a long-lived child and exits 7 at once.
    $fakeSetup = Join-Path $root 'fake-setup.ps1'
    $childMarker = Join-Path $root 'child.pid'
    @"
`$child = Start-Process -FilePath '$hostExe' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 40' -WindowStyle Hidden -PassThru
Set-Content -LiteralPath '$childMarker' -Value `$child.Id
exit 7
"@ | Set-Content -LiteralPath $fakeSetup -Encoding ASCII
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $code = Invoke-HorizunSetupProcess $hostExe @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $fakeSetup + '"'))
    $watch.Stop()
    Assert 'the exit code of Setup itself is returned' ($code -eq 7) "exit '$code'"
    Assert 'Setup is not waited on past its own exit' ($watch.Elapsed.TotalSeconds -lt 25) ("{0:N1} s" -f $watch.Elapsed.TotalSeconds)
    if (Test-Path -LiteralPath $childMarker) {
        $childId = [int](Get-Content -LiteralPath $childMarker -Raw).Trim()
        Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
    }

    # ---- 2. The install location comes from Setup's own uninstall record. ----------
    New-Item -Path $testKey -Force | Out-Null
    New-ItemProperty -LiteralPath $testKey -Name 'InstallLocation' -Value 'D:\Apps\Horizun\MCP\' -PropertyType String -Force | Out-Null
    $missing = $testKey + '-absent'
    Assert 'InstallLocation is read from the uninstall key' ((Get-HorizunInstalledAppDir @($missing, $testKey)) -eq 'D:\Apps\Horizun\MCP') (Get-HorizunInstalledAppDir @($missing, $testKey))
    Assert 'without a record the per-user default is used' `
        ((Get-HorizunInstalledAppDir @($missing)) -eq (Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP')) (Get-HorizunInstalledAppDir @($missing))

    # ---- 2b. A Setup signed by HORIZUN GROUP S.A.S. (Certum) is accepted; nothing else is. ----
    # scripts/firmar-release.ps1 signs the Setup after CI. The bootstrap used to refuse any
    # state but NotSigned, which would have rejected every correctly signed release.
    function New-FakeSignature([string]$Status, [string]$Subject, [string]$Issuer, [string]$Stamp) {
        [pscustomobject]@{
            Status = $Status
            SignerCertificate = $(if ($Subject) { [pscustomobject]@{ Subject = $Subject; Issuer = $Issuer } } else { $null })
            TimeStamperCertificate = $(if ($Stamp) { [pscustomobject]@{ Subject = $Stamp } } else { $null })
        }
    }
    $horizunSubject = 'CN=HORIZUN GROUP S.A.S., O=HORIZUN GROUP S.A.S., L=Cali, S=Valle del Cauca, C=CO'
    $certumIssuer = 'CN=Certum Code Signing 2021 CA, O=Asseco Data Systems S.A., C=PL'
    $certumStamp = 'CN=Certum Timestamp 2026, O=Asseco Data Systems S.A., C=PL'
    Assert 'the Certum signature of HORIZUN GROUP S.A.S. is accepted' `
        (Test-HorizunCertumSetupSignature (New-FakeSignature 'Valid' $horizunSubject $certumIssuer $certumStamp)) $null
    Assert 'an invalid signature is refused' `
        (-not (Test-HorizunCertumSetupSignature (New-FakeSignature 'HashMismatch' $horizunSubject $certumIssuer $certumStamp))) $null
    Assert 'an unsigned file is not a Certum signature' `
        (-not (Test-HorizunCertumSetupSignature (New-FakeSignature 'NotSigned' '' '' ''))) $null
    Assert 'another publisher is refused' `
        (-not (Test-HorizunCertumSetupSignature (New-FakeSignature 'Valid' 'CN=Someone Else, O=Someone Else' $certumIssuer $certumStamp))) $null
    Assert 'a self-signed certificate is refused' `
        (-not (Test-HorizunCertumSetupSignature (New-FakeSignature 'Valid' $horizunSubject $horizunSubject $certumStamp))) $null
    Assert 'a signature without a Certum timestamp is refused' `
        (-not (Test-HorizunCertumSetupSignature (New-FakeSignature 'Valid' $horizunSubject $certumIssuer ''))) $null

    # ---- 3. The file itself. ------------------------------------------------------------
    $bytes = [IO.File]::ReadAllBytes($bootstrap)
    $nonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count
    Assert 'install-release.ps1 is pure ASCII (5.1 reads it right, irm | iex keeps working)' ($nonAscii -eq 0) "$nonAscii non-ASCII byte(s)"
    $text = [IO.File]::ReadAllText($bootstrap)
    Assert 'the usage line no longer claims irm | iex can install an unsigned release' ($text -notmatch '(?m)^\s*irm [^\r\n]*\| iex\s*$') $null
    Assert 'the usage names the scriptblock form that passes -AllowUnsigned' ($text -match 'scriptblock\]::Create\(\(irm [^)]*\)\)\) -AllowUnsigned') $null
    Assert 'no Start-Process -Wait remains' ($text -notmatch 'Start-Process[^\r\n]*-Wait\b') $null
    Assert 'no message names a Start-menu shortcut that Setup does not create' ($text -notmatch 'Start-menu shortcut') $null
}
finally {
    if (Test-Path -LiteralPath $testKey) { Remove-Item -LiteralPath $testKey -Recurse -Force }
    if (Test-Path -LiteralPath 'HKCU:\Software\HorizunTest') {
        if (@(Get-ChildItem -LiteralPath 'HKCU:\Software\HorizunTest').Count -eq 0 -and
            @((Get-Item -LiteralPath 'HKCU:\Software\HorizunTest').GetValueNames()).Count -eq 0) {
            Remove-Item -LiteralPath 'HKCU:\Software\HorizunTest' -Force
        }
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "install-release helpers: $failed FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'install-release helpers: PASS' -ForegroundColor Green
exit 0
