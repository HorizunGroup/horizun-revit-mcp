<#
.SYNOPSIS
  Load a FRESHLY BUILT add-in into one Revit year without replacing the
  installed pair, and put it back afterwards.

.DESCRIPTION
  The installed server and add-in are one pair with one contract hash, and
  install.ps1 replaces both - which is right for an install and wrong for a
  development session on a machine where another Revit is somebody's work
  session and the installed server is what their MCP clients are running.

  This script touches exactly ONE installed file: the manifest
  %APPDATA%\Autodesk\Revit\Addins\<year>\Horizun.addin is renamed aside and a
  development manifest pointing at a COPY of the build output is written in its
  place. The installed DLLs, the installed server and every other year stay as
  they are. -Restore reverses it; the script refuses to -Enable twice.

  The copied binaries are signed with the machine's EXISTING trusted self-signed
  certificate when there is one (the same one install.ps1 re-signs with); no
  certificate is created and no trust is minted. Without one, Revit will show
  its security dialog for the unsigned DLL, and that dialog is a person's call.

  Drive the session with the fresh server:
      $env:HORIZUN_SERVER_EXE = "<repo>\src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe"
      $env:HORIZUN_REVIT_YEAR = "<year>"
  scripts/hz-call.ps1 honours the first; the live library calls hz-call.

.PARAMETER Year
  The Revit year to load the development build into. That Revit must be CLOSED.

.PARAMETER Enable
  Copy bin\<Config> into <DevRoot>\<Year>\Horizun, sign, swap the manifest.

.PARAMETER Restore
  Delete the development manifest and rename the installed one back.

.PARAMETER DevRoot
  Where the copy lives. Default: %USERPROFILE%\.horizun\dev-addin.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('2023', '2024', '2025', '2026', '2027')][string]$Year,
    [switch]$Enable,
    [switch]$Restore,
    [string]$Config = 'Release',
    [string]$DevRoot = (Join-Path $env:USERPROFILE '.horizun\dev-addin')
)
$ErrorActionPreference = 'Stop'
if ($Enable -eq $Restore) { throw 'Give exactly one of -Enable or -Restore.' }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
$installed = Join-Path $addins 'Horizun.addin'
$aside = Join-Path $addins 'Horizun.addin.dev-session-aside'
$devManifest = Join-Path $addins 'Horizun-dev-session.addin'
$devDir = Join-Path $DevRoot "$Year\Horizun"

# That Revit must be closed: a manifest is read at startup and a DLL in use
# cannot be replaced. Other years are not our business here - but a Revit whose
# YEAR COULD NOT BE DETERMINED might be this one, and "its MainModule threw" is
# not "there is no Revit". The old filter swallowed that failure inside a
# try/catch and the process simply left the list, which is how a manifest could
# be renamed under a running Revit. The classifier lives in
# year-matrix.session.ps1 so this script and the driver cannot disagree about it,
# and it is re-checked here even when the driver already checked: this script is
# also run by hand, minutes later.
. (Join-Path $PSScriptRoot 'year-matrix.session.ps1')
$probes = New-HzMatrixProbes -Repo $repo -ServerExe $null
$allowed = Test-HzManifestChangeAllowed -Probes $probes -Year $Year
if (-not $allowed.ok) {
    throw ($allowed.why + " Close it first - normally, by whoever owns it; this script neither kills a process nor " +
           "escalates to identify one. Other Revit years are left alone and nothing was changed.")
}

if ($Restore) {
    # VALIDATE BEFORE TOUCHING ANYTHING. The old order removed the development
    # manifest FIRST and only then discovered that both Horizun.addin and the
    # aside copy existed - so it threw "resolve by hand" having already deleted
    # the manifest that was loading the add-in, and the year was left with none.
    if ((Test-Path -LiteralPath $aside) -and (Test-Path -LiteralPath $installed)) {
        Write-Host ("[dev-session] CONFLICT: both $installed and $aside exist. NOTHING WAS CHANGED - resolve by hand: " +
                    "the aside copy is the one this script renamed, so the question is which of the two is the " +
                    "installed manifest, and that is not a guess to make for you.") -ForegroundColor Red
        exit 3
    }
    $changed = @(); $pending = @()
    if (Test-Path -LiteralPath $devManifest) {
        try { Remove-Item -LiteralPath $devManifest -Force -ErrorAction Stop; $changed += "removed $devManifest" }
        catch { $pending += "could not remove $devManifest : $($_.Exception.Message)" }
    }
    if (Test-Path -LiteralPath $aside) {
        try { Rename-Item -LiteralPath $aside -NewName 'Horizun.addin' -ErrorAction Stop; $changed += "restored $installed" }
        catch { $pending += "could not rename $aside back to Horizun.addin : $($_.Exception.Message)" }
    }
    elseif (-not (Test-Path -LiteralPath $installed)) {
        # Not an error HERE: this script cannot know whether the year ever had an
        # installed manifest. It says so, and the caller that holds the snapshot
        # from before the session decides whether that is a finding.
        Write-Host ("[dev-session] NOTE: no aside copy and no $installed - this year has no installed manifest. " +
                    "Nothing was invented; a caller that expected one must treat this as a finding.") -ForegroundColor Yellow
    }
    foreach ($c in $changed) { Write-Host "[dev-session] $c" }
    if ($pending.Count -gt 0) {
        # EXACTLY what changed and what is still pending, so a partial failure is
        # recoverable by hand instead of being a mystery.
        foreach ($p in $pending) { Write-Host "[dev-session] PENDING: $p" -ForegroundColor Red }
        Write-Host ("[dev-session] PARTIAL RESTORE. Changed: " +
                    $(if ($changed.Count -gt 0) { $changed -join '; ' } else { 'nothing' })) -ForegroundColor Red
        exit 4
    }
    if ($changed.Count -eq 0) { Write-Host "[dev-session] nothing to restore for $Year" }
    exit 0
}

# ---- Enable -----------------------------------------------------------------
# Both refusals BEFORE the first file is written: the copy and the signature are
# wasted work if the manifest cannot be swapped afterwards, and a half-prepared
# development directory invites a second run to think it is ready.
if (Test-Path -LiteralPath $devManifest) { throw "A development session is already enabled for $Year ($devManifest). Run -Restore first." }
if ((Test-Path -LiteralPath $installed) -and (Test-Path -LiteralPath $aside)) {
    throw "$aside already exists from an earlier session; resolve by hand - nothing was changed."
}
$bin = Join-Path $repo "src\Horizun.Revit\bin\$Config"
$dll = Join-Path $bin 'Horizun.Revit.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "No build at $dll. Build with: dotnet build src/Horizun.Revit/Horizun.Revit.csproj -c $Config -p:RevitYear=$Year" }

# The output folder is shared across years, so prove the DLL in it was built
# for THIS year before loading it into this Revit.
. (Join-Path $repo 'scripts\horizun-deploy.lib.ps1')
if (Get-Command Assert-HorizunTfm -ErrorAction SilentlyContinue) { Assert-HorizunTfm -DllPath $dll -Year $Year }

if (Test-Path -LiteralPath $devDir) { Remove-Item -LiteralPath $devDir -Recurse -Force }
New-Item -ItemType Directory -Path $devDir -Force | Out-Null
Copy-Item -Path (Join-Path $bin '*') -Destination $devDir -Recurse -Force
Write-Host "[dev-session] copied $bin -> $devDir"

# Sign the copy with the EXISTING trusted certificate, exactly as install.ps1
# does. Nothing is created; without a certificate the copy stays unsigned and
# Revit will ask.
$cert = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object {
    $_.Subject -eq 'CN=Horizun Group (self-signed add-in signing)' -and $_.HasPrivateKey -and
    $_.NotAfter -gt (Get-Date).AddDays(30) -and
    (Test-Path -LiteralPath "Cert:\CurrentUser\TrustedPublisher\$($_.Thumbprint)") -and
    (Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($_.Thumbprint)")
} | Sort-Object NotAfter -Descending | Select-Object -First 1)
if ($cert.Count -eq 1) {
    $sig = Set-AuthenticodeSignature -FilePath (Join-Path $devDir 'Horizun.Revit.dll') -Certificate $cert[0] -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($sig.Status -ne 'Valid') { throw "signing the development DLL returned $($sig.Status): $($sig.StatusMessage)" }
    Write-Host "[dev-session] signed Horizun.Revit.dll with the existing trusted certificate $($cert[0].Thumbprint)"
} else {
    Write-Host "[dev-session] no trusted self-signed certificate on this machine; the development DLL is UNSIGNED and Revit will show its security dialog" -ForegroundColor Yellow
}

if (Test-Path -LiteralPath $installed) {
    # The conflict was already refused at the top, before anything was copied;
    # re-checked here because the rename is the irreversible half.
    if (Test-Path -LiteralPath $aside) { throw "$aside already exists from an earlier session; resolve by hand - nothing was changed." }
    Rename-Item -LiteralPath $installed -NewName 'Horizun.addin.dev-session-aside'
    Write-Host "[dev-session] set aside $installed"
}
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- DEVELOPMENT SESSION manifest written by scripts/live/dev-addin-session.ps1.
     Points at a copy of the build output; the installed add-in is set aside as
     Horizun.addin.dev-session-aside. Run the script with -Restore to undo. -->
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Horizun MCP (development session)</Name>
    <Assembly>$devDir\Horizun.Revit.dll</Assembly>
    <AddInId>b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30</AddInId>
    <FullClassName>Horizun.Revit.App</FullClassName>
    <VendorId>HRZN</VendorId>
    <VendorDescription>Horizun Group</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -LiteralPath $devManifest -Value $manifest -Encoding UTF8
Write-Host "[dev-session] wrote $devManifest"
Write-Host "[dev-session] now: start Revit $Year, then set HORIZUN_SERVER_EXE to the fresh server and HORIZUN_REVIT_YEAR=$Year for the shell that runs the harnesses. Finish with -Restore."
