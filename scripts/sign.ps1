#Requires -Version 5.1
<#
  Code-sign the built artifacts.

  WHY IT MATTERS HERE, beyond looking professional: Revit shows a "Security -
  Unsigned Add-In" dialog on every start for an unsigned add-in, and the add-in
  does not load until a human clicks it. That is not cosmetic - it means the
  bridge cannot come up unattended, and every client install begins with a
  warning dialog about your software.

  This script does not create or buy a certificate; it signs with one you already
  have. Two ways to get there:

    - A certificate from a real CA, for software other people install. OV is
      enough; EV ships on a hardware token, changes the flow, and buys nothing
      extra here.
    - A SELF-SIGNED certificate for machines you control - see dev-cert.ps1.
      That script creates it and prints the trust commands but never runs them:
      trusting a self-signed publisher means writing to the machine's Trusted
      Root and Trusted Publishers stores, which is the machine owner's decision
      to make deliberately, not a side effect of a build script.

  Then:

    pwsh scripts/sign.ps1 -Thumbprint A1B2...                   # cert already in the store

  ORDER MATTERS:
    pack.ps1 -SkipInstaller  ->  sign.ps1  ->  pack.ps1 -InstallerOnly  ->  sign.ps1
  A plain pack.ps1 wipes the staging folder, so signing and then repacking would
  discard the signatures. And the payload inside a signed installer is not signed
  by signing the wrapper - both steps are needed.
#>
[CmdletBinding()]
param(
    [string]$PfxPath,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Config = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')
$stage = Join-Path $repo 'dist\stage'

if ($PfxPath) {
    throw 'PfxPath is no longer accepted: signtool exposes its password in the process command line. Import the PFX into the protected certificate store, then pass -Thumbprint.'
}
if (-not $Thumbprint) {
    throw "Give -Thumbprint for a code-signing certificate already installed in the protected certificate store. Nothing was signed."
}

# signtool ships with the Windows SDK; there is no single fixed path, so look.
# Its absence is not fatal: Windows PowerShell signs Authenticode natively through
# Set-AuthenticodeSignature, which is the same underlying API. Requiring a
# multi-gigabyte SDK install to sign three files would be a poor trade.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1

# Our own assemblies and the installer, FROM THE STAGE - the bytes that actually
# get packaged, not a bin copy that never ships. This was the defect: it signed
# src\...\bin\...\horizun-mcp.exe (never packaged) and never signed
# horizun-mcp.dll at all (the apphost is a launcher; the server's CODE is the
# dll). Third-party DLLs (IronPython, Newtonsoft) are signed by their publishers
# and are not re-signed here: replacing someone else's signature with ours would
# misstate who produced them.
# Only staged own binaries that are NOT already signed. This matters on the SECOND
# pass: after pack -InstallerOnly wraps the signed payload, re-signing that payload
# would produce fresh bytes (a new timestamp) that no longer match what is inside
# the installer just built. So the payload is signed once, on the first pass; the
# second pass finds it already signed, skips it, and signs only the setup.exe.
$staged = @(Get-HorizunOwnBinaries $stage) | Where-Object { $_ -and -not (Get-HorizunSignatureInfo $_).Signed } | ForEach-Object { Get-Item $_ }
$setup  = @(Get-ChildItem (Join-Path $repo 'dist') -Filter '*setup.exe' -File -ErrorAction SilentlyContinue)
$targets = @($staged) + @($setup)
$signedStagedPayload = $staged.Count -gt 0

if (-not $targets) {
    throw "Nothing to sign - run scripts/pack.ps1 -SkipInstaller first (no staged own binaries, and no setup.exe in dist)."
}

if ($signtool) {
    $args = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    $args += @('/sha1', $Thumbprint)

    foreach ($t in $targets) {
        Write-Host "[sign] $($t.FullName)" -ForegroundColor Cyan
        & $signtool.FullName @args $t.FullName
        if ($LASTEXITCODE -ne 0) { throw "signtool failed on $($t.FullName)" }
        $check = Get-AuthenticodeSignature -LiteralPath $t.FullName
        if ($check.Status -ne 'Valid' -or -not $check.TimeStamperCertificate) {
            throw "signtool returned success but Authenticode read-back is $($check.Status) or lacks a trusted timestamp on $($t.FullName)"
        }
    }
}
else {
    # Native path. The certificate must be in the store; a .pfx is imported by the
    # operator, not by this script, so no password ever passes through here.
    if (-not $Thumbprint) {
        throw "signtool is not installed, so signing goes through PowerShell and needs -Thumbprint " +
              "(a certificate already in CurrentUser\My). Import your .pfx yourself first, or install " +
              "the Windows SDK. Nothing was signed."
    }
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $cert) { throw "No certificate with thumbprint $Thumbprint in CurrentUser\My. Nothing was signed." }
    if (-not $cert.HasPrivateKey) { throw "That certificate has no private key, so it cannot sign. Nothing was signed." }

    foreach ($t in $targets) {
        Write-Host "[sign] $($t.FullName)" -ForegroundColor Cyan
        # Timestamping keeps the signature valid past the certificate's expiry. If the
        # timestamp authority is unreachable we sign anyway and SAY so, rather than
        # silently producing a signature that dies with the certificate.
        Set-AuthenticodeSignature -FilePath $t.FullName -Certificate $cert `
            -HashAlgorithm SHA256 -TimestampServer $TimestampUrl -ErrorAction SilentlyContinue | Out-Null

        # Ask the RIGHT question. Status is about the trust chain, not about the
        # timestamp: a self-signed certificate reports UnknownError even when the
        # timestamp succeeded perfectly. Judging the timestamp by Status threw away
        # good timestamps and re-signed without one. The presence of a timestamper
        # certificate is the only thing that answers "was it timestamped".
        $check = Get-AuthenticodeSignature -FilePath $t.FullName
        if (-not $check.TimeStamperCertificate) {
            Set-AuthenticodeSignature -FilePath $t.FullName -Certificate $cert -HashAlgorithm SHA256 | Out-Null
            $check = Get-AuthenticodeSignature -FilePath $t.FullName
            Write-Warning "  signed WITHOUT a timestamp (could not reach $TimestampUrl) - this signature expires with the certificate"
        }

        Write-Host ("  status: {0}   signer: {1}" -f $check.Status, $check.SignerCertificate.Subject)
        Write-Host ("  timestamped by: {0}" -f $(if ($check.TimeStamperCertificate) { ($check.TimeStamperCertificate.Subject -split ',')[0] } else { 'NOBODY' }))
        if ($check.Status -eq 'NotSigned') { throw "Signing produced nothing on $($t.FullName)." }
    }
}

# SIGN, THEN MANIFEST. Signing changed the bytes of every own binary, so the
# manifest the build wrote now describes files that no longer exist on disk. Recompute
# it from the SIGNED stage, recording the new hashes and a signature block. Without
# this, -InstallerOnly and verify-release both refuse the stage (correctly) because
# its hashes describe the unsigned files.
if ($signedStagedPayload -and (Test-Path (Join-Path $stage 'manifest.json'))) {
    Write-Host ""
    Write-Host "[sign] recomputing manifest.json from the signed stage" -ForegroundColor Cyan
    $doc = Update-HorizunManifestToStage $stage
    Write-Host ("  manifest signed={0}, signer {1}" -f $doc.Signed,
                $(if ($doc.Signature.SignerSubject) { ($doc.Signature.SignerSubject -split ',')[0] } else { 'NONE' }))
    $mismatch = @(Test-HorizunStageMatchesManifest $stage)
    if ($mismatch.Count -gt 0) { throw "manifest still does not match the stage after recompute: $($mismatch -join '; ')" }
    Write-Host "  stage now matches manifest (all hashes re-verified)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Signed $($targets.Count) file(s). Verify with:" -ForegroundColor Green
Write-Host "  Get-AuthenticodeSignature <file> | Format-List Status, StatusMessage, SignerCertificate"
Write-Host "A self-signed certificate reports UnknownError ('not trusted by the trust provider')" -ForegroundColor DarkGray
Write-Host "until it is in the machine's Trusted Root store - that is expected, not a failure." -ForegroundColor DarkGray
Write-Host "Then: pwsh scripts/pack.ps1 -InstallerOnly   (wraps the signed payload)" -ForegroundColor Yellow
Write-Host "and sign the produced setup.exe too - signing the wrapper does not sign" -ForegroundColor Yellow
Write-Host "what is inside it. Do NOT re-run a plain pack.ps1: it wipes the staging" -ForegroundColor Yellow
Write-Host "folder and would throw the signatures away." -ForegroundColor Yellow
