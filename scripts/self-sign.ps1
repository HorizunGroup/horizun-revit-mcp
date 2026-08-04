#Requires -Version 5.1
<#
  End the "Security - Unsigned Add-In" dialog for good, without buying anything.

  WHY THE REGISTRY TRICK IS NOT ENOUGH. Measured 2026-08-04: Revit's Always Load
  record is keyed to the BINARY, not to the AddInId. With every add-in on a machine
  marked trusted in HKCU\...\CodeSigning, Revit still prompted for exactly the two
  whose DLL had changed that week. So each rebuild re-arms the dialog, and a
  development loop that reinstalls six times in a morning produces six rounds of
  prompts across five Revit years. scripts/trust-addin.ps1 remains useful for
  reporting and for add-ins nobody is rebuilding; it cannot fix this.

  WHAT THIS DOES INSTEAD. Creates a self-signed CODE SIGNING certificate, trusts it
  for THIS Windows user, and signs the installed binaries with it. Trust then lives
  on the certificate rather than on a file hash, so rebuilding and re-signing does
  not re-prompt. Nothing is bought: New-SelfSignedCertificate and
  Set-AuthenticodeSignature ship with Windows.

  WHAT IT COSTS YOU, stated plainly because it is a real security decision:
  anything signed with this certificate becomes trusted for this user. The private
  key stays in your own CurrentUser store, non-exportable, and only you can sign
  with it - but you are creating a publisher you trust, so treat the key as yours
  alone. It is per-user: no elevation, nothing machine-wide, no other account
  affected. -Remove undoes all of it.

  WHAT IT IS NOT. It is not a purchased certificate. A bought one exists so OTHER
  people's machines trust the build without installing anything; this solves it only
  where the certificate is trusted - your machines and your team's. Windows may still
  warn about the unsigned INSTALLER on a machine that has not trusted this.

    powershell -ExecutionPolicy Bypass -File .\scripts\self-sign.ps1
    ... -Report    what exists and what is signed; changes nothing
    ... -Remove    delete the certificate and untrust it (signatures stay, unverifiable)
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Subject = 'CN=Horizun Group (self-signed add-in signing)',
    [int[]]$Years,
    [switch]$Report,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

function Say($m, $c = 'Gray') { Write-Host "    $m" -ForegroundColor $c }
Write-Host "[sign] $Subject" -ForegroundColor Cyan

if (-not $Years -or $Years.Count -eq 0) {
    $addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
    $Years = @(Get-ChildItem $addinRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { if ($_.Name -match '^\d{4}$') { [int]$_.Name } } | Sort-Object -Unique)
}

# Everything this signs: the add-in per year, plus the installed server.
$targets = @()
foreach ($y in $Years) {
    $dll = Join-Path $env:APPDATA ("Autodesk\Revit\Addins\$y\Horizun\Horizun.Revit.dll")
    if (Test-Path -LiteralPath $dll) { $targets += $dll }
}
$server = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
if (Test-Path -LiteralPath $server) { $targets += $server }
$serverDll = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.dll'
if (Test-Path -LiteralPath $serverDll) { $targets += $serverDll }

$existing = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject })

if ($Report) {
    Write-Host "[sign] certificate:" -ForegroundColor Cyan
    if ($existing.Count -eq 0) { Say 'none yet - run without -Report to create one' 'Yellow' }
    foreach ($c in $existing) {
        Say ("thumbprint {0}  expires {1:yyyy-MM-dd}" -f $c.Thumbprint, $c.NotAfter)
        $pub = @(Get-ChildItem Cert:\CurrentUser\TrustedPublisher -EA SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $c.Thumbprint })
        $root = @(Get-ChildItem Cert:\CurrentUser\Root -EA SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $c.Thumbprint })
        Say ("trusted publisher: {0}   trusted root: {1}" -f
             ($pub.Count -gt 0), ($root.Count -gt 0)) 'DarkGray'
    }
    Write-Host "[sign] binaries:" -ForegroundColor Cyan
    foreach ($t in $targets) {
        $s = Get-AuthenticodeSignature -LiteralPath $t
        $colour = if ($s.Status -eq 'Valid') { 'Green' } else { 'Yellow' }
        Say ("{0,-8} {1}" -f $s.Status, (Split-Path $t -Leaf)) $colour
    }
    exit 0
}

if ($Remove) {
    if ($existing.Count -eq 0) { Say 'no certificate to remove' 'DarkGray'; exit 0 }
    foreach ($c in $existing) {
        foreach ($store in 'TrustedPublisher', 'Root', 'My') {
            $found = @(Get-ChildItem "Cert:\CurrentUser\$store" -EA SilentlyContinue |
                Where-Object { $_.Thumbprint -eq $c.Thumbprint })
            foreach ($f in $found) {
                if ($PSCmdlet.ShouldProcess("CurrentUser\$store", "remove $($c.Thumbprint)")) {
                    Remove-Item $f.PSPath -Force
                    Say "removed from $store" 'Yellow'
                }
            }
        }
    }
    Write-Host ""
    Write-Host "[sign] removed. The signatures on the binaries remain but no longer" -ForegroundColor Cyan
    Write-Host "       verify, so Revit will ask about them again." -ForegroundColor DarkYellow
    exit 0
}

# ---- the certificate ----
$cert = $existing | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert -and $cert.NotAfter -gt (Get-Date).AddDays(30)) {
    Say ("reusing certificate {0} (expires {1:yyyy-MM-dd})" -f $cert.Thumbprint, $cert.NotAfter) 'DarkGray'
}
else {
    if (-not $PSCmdlet.ShouldProcess('CurrentUser\My', "create a self-signed code-signing certificate")) { exit 0 }
    # CodeSigningCert, five years, key NOT exportable: the point of this key is that
    # it never leaves this machine. A copyable signing key is a signing key somebody
    # else can use to sign something you did not write.
    $cert = New-SelfSignedCertificate -Subject $Subject -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
        -KeyExportPolicy NonExportable -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 3072
    Say ("created {0}" -f $cert.Thumbprint) 'Green'
}

# ---- trust it, for this user only ----
# TrustedPublisher is what stops the prompt; Root is needed because a self-signed
# certificate is its own chain and would otherwise fail validation.
$tmp = Join-Path $env:TEMP ("horizun-self-sign-" + $cert.Thumbprint + ".cer")
Export-Certificate -Cert $cert -FilePath $tmp -Force | Out-Null
try {
    foreach ($store in 'TrustedPublisher', 'Root') {
        $already = @(Get-ChildItem "Cert:\CurrentUser\$store" -EA SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
        if ($already.Count -gt 0) { Say "already in $store" 'DarkGray'; continue }
        if ($PSCmdlet.ShouldProcess("CurrentUser\$store", "trust $($cert.Thumbprint)")) {
            Import-Certificate -FilePath $tmp -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
            Say "trusted in $store" 'Green'
        }
    }
}
finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }

# ---- sign, and VERIFY the signature rather than trusting the call ----
if ($targets.Count -eq 0) {
    Write-Host "[sign] nothing installed to sign. Run install.ps1 first." -ForegroundColor Yellow
    exit 2
}
Write-Host "[sign] signing $($targets.Count) file(s)" -ForegroundColor Cyan
$ok = 0; $bad = @(); $locked = @()
foreach ($t in $targets) {
    $name = Split-Path $t -Leaf
    if (-not $PSCmdlet.ShouldProcess($name, 'sign')) { continue }
    try {
        Set-AuthenticodeSignature -LiteralPath $t -Certificate $cert `
            -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' `
            -ErrorAction Stop | Out-Null
    }
    catch {
        # A timestamp needs the network; without one the signature still works, it
        # just stops verifying when the certificate expires. Say which happened.
        try {
            Set-AuthenticodeSignature -LiteralPath $t -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop | Out-Null
            Say "$name signed WITHOUT a timestamp (no network?) - it stops verifying when the cert expires" 'Yellow'
        }
        catch {
            # A locked file is somebody else USING it - Revit has the add-in loaded,
            # or a server process holds the exe. That is not a failure of signing, and
            # on a machine where two agents work at once it is the normal case. Name
            # it as skipped so the summary does not read like a broken certificate.
            if ($_.Exception.Message -match 'being used by another process') {
                $locked += $name
                Say "$name : SKIPPED - in use (close that Revit / restart the MCP client, then re-run)" 'Yellow'
            }
            else { $bad += "$name : $($_.Exception.Message)" }
            continue
        }
    }
    # The read-back, not the return value. Set() not throwing is not a signature -
    # the same lesson the sprinkler work paid for twice.
    #
    # RETRIED, because the first read straight after writing returns UnknownError:
    # the file has just been rewritten and the chain check races the write. Measured
    # - four files reported UnknownError and every one read Valid a moment later.
    # Reporting the first read as a failure would have condemned a signature that
    # was fine, which is the same class of mistake as measuring with a broken ruler.
    $sig = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Start-Sleep -Milliseconds 300
        $sig = Get-AuthenticodeSignature -LiteralPath $t
        if ($sig.Status -eq 'Valid') { break }
    }
    if ($sig.Status -eq 'Valid') { $ok++; Say "$name : Valid" 'Green' }
    else { $bad += ("$name : signed but reads back $($sig.Status) after 5 tries") }
}

Write-Host ""
if ($locked.Count -gt 0) {
    Write-Host "[sign] $($locked.Count) file(s) were IN USE and were skipped: $($locked -join ', ')" -ForegroundColor Yellow
    Write-Host "       Nothing is wrong with them - they are simply loaded. Close that Revit" -ForegroundColor DarkYellow
    Write-Host "       (or restart the MCP client for the server) and run this again." -ForegroundColor DarkYellow
    Write-Host ""
}
if ($bad.Count -gt 0) {
    Write-Host "[sign] $ok signed, $($bad.Count) FAILED:" -ForegroundColor Red
    foreach ($b in $bad) { Say $b 'Red' }
    exit 1
}
if ($locked.Count -gt 0) { exit 3 }
Write-Host "[sign] $ok file(s) signed and verified Valid." -ForegroundColor Green
Write-Host "       Restart Revit. It will not ask about these add-ins again - and it" -ForegroundColor DarkYellow
Write-Host "       will not start asking again after a rebuild, PROVIDED the rebuild is" -ForegroundColor DarkYellow
Write-Host "       re-signed: run this after every install.ps1." -ForegroundColor DarkYellow
