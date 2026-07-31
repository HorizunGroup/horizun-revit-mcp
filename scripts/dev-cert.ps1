#Requires -Version 5.1
<#
  Create a SELF-SIGNED code-signing certificate for machines you control.

  READ THIS BEFORE USING IT.

  A self-signed certificate is its own root, so making Windows accept the
  signature means putting that certificate into TWO stores on every machine that
  must trust it:

    Trusted Root Certification Authorities  - so the chain validates at all
    Trusted Publishers                       - so Revit treats the publisher as
                                               known and stops asking

  That is a real security decision, not a formality: from then on the machine
  trusts ANYTHING signed with this key, forever, not just this add-in. If the
  private key ever leaks, whoever has it can sign software that those machines
  accept as yours. Which is exactly why this script does NOT touch either store -
  it prints the commands and leaves the decision, and the elevation, to you.

  This is appropriate for: your own workstations, and a small fleet whose IT
  agrees. It is NOT appropriate for software a client downloads: their IT will
  refuse to install your root, and they would be right to.

  Usage:
    pwsh scripts/dev-cert.ps1                  # create + export the public .cer
    pwsh scripts/dev-cert.ps1 -ShowOnly        # just print what exists and the commands
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=Horizun Group, O=Horizun Group, C=CO',
    [int]$YearsValid = 3,
    [string]$OutDir,
    [switch]$ShowOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist\cert' }

function Existing {
    Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject } | Sort-Object NotAfter -Descending | Select-Object -First 1
}

$cert = Existing

if ($ShowOnly) {
    if ($cert) { "Found: $($cert.Subject)"; "  thumbprint : $($cert.Thumbprint)"; "  valid until: $($cert.NotAfter)" }
    else { "No certificate with subject '$Subject' in CurrentUser\My." }
}
elseif ($cert) {
    Write-Host "A certificate for '$Subject' already exists - reusing it rather than minting a second one." -ForegroundColor Yellow
    Write-Host "  thumbprint : $($cert.Thumbprint)"
    Write-Host "  valid until: $($cert.NotAfter)"
}
else {
    Write-Host "Creating a self-signed code-signing certificate..." -ForegroundColor Cyan
    # Exportable on purpose: you said "my machines", plural. The .pfx export is how
    # it reaches the others - and its password is yours to choose and to keep.
    $cert = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears($YearsValid) `
        -CertStoreLocation Cert:\CurrentUser\My
    Write-Host "  thumbprint : $($cert.Thumbprint)" -ForegroundColor Green
    Write-Host "  valid until: $($cert.NotAfter)" -ForegroundColor Green
}

if (-not $cert) { return }

# The PUBLIC certificate. No private key, nothing secret: this is the file that
# goes into the trust stores, and the one you copy to your other machines.
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$cerPath = Join-Path $OutDir 'horizun-codesign.cer'
if (-not $ShowOnly) {
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
    Write-Host "  public cert: $cerPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "NEXT - run these yourself, in an ELEVATED PowerShell, on each machine" -ForegroundColor Yellow
Write-Host "that should trust this publisher. They change what the machine trusts," -ForegroundColor Yellow
Write-Host "which is why this script will not run them for you:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  certutil -addstore Root             `"$cerPath`"" -ForegroundColor White
Write-Host "  certutil -addstore TrustedPublisher `"$cerPath`"" -ForegroundColor White
Write-Host ""
Write-Host "To undo, on the same machine and also elevated:" -ForegroundColor DarkGray
Write-Host "  certutil -delstore Root             `"Horizun Group`"" -ForegroundColor DarkGray
Write-Host "  certutil -delstore TrustedPublisher `"Horizun Group`"" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Then sign the build with:  pwsh scripts/sign.ps1 -Thumbprint $($cert.Thumbprint)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Protect the private key. It lives in your user certificate store; anyone" -ForegroundColor DarkGray
Write-Host "who obtains it can sign software your machines will accept as Horizun's." -ForegroundColor DarkGray
