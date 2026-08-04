#Requires -Version 5.1
<#
  Stop Revit asking "Security - Unsigned Add-In" for THIS add-in, on every year.

  WHY THIS BEATS CLICKING THE DIALOG. Revit records the "Always Load" decision in
  the registry, keyed by the add-in's AddInId:

      HKCU\SOFTWARE\Autodesk\Revit\Autodesk Revit <year>\CodeSigning
          <AddInId GUID> = 1        (REG_DWORD)

  The identity is the GUID in Horizun.addin, and it does not change between
  builds. So this is not a workaround for the dialog - it is the same record the
  dialog writes, written directly, before Revit ever asks. A watcher that clicks a
  button has to be running, has to find the window (it can open on another
  monitor), and has to be granted control of Revit every session. This runs once
  per machine and is finished.

  WHAT IT DOES NOT DO. It does not install a certificate, does not mark anything
  as a trusted publisher, and touches only the add-in named (or, with
  -AllInstalledAddins, only add-ins ALREADY installed for this user). Windows will
  still warn about the unsigned INSTALLER - that is a different dialog and a
  different fix (a code-signing certificate, which is not on this roadmap).

  IT IS PER USER AND EXPLICIT. HKCU only, no elevation, nothing machine-wide: it
  says "this Windows user trusts this add-in", which is the decision the dialog
  was asking that user to make. On a managed fleet, that decision belongs to IT
  via policy, not to a script somebody ran.

    powershell -ExecutionPolicy Bypass -File .\scripts\trust-addin.ps1
    ... -WhatIf      show what would be written, change nothing
    ... -Revoke      remove the trust and let Revit ask again
    ... -Report      list every installed add-in and whether it will prompt
    ... -AllInstalledAddins   trust every add-in already installed for this user
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int[]]$Years,
    [switch]$Revoke,
    # Trust EVERY add-in installed for this user, not only this one. Added because
    # the dialog that actually interrupts people is rarely this add-in's: a working
    # BIM machine carries a dozen unsigned add-ins, and each one asks separately.
    # It reads the AddInId out of every .addin manifest in the user's Addins folders
    # and records the same decision for each - so it can only ever trust add-ins that
    # are ALREADY INSTALLED and that this user could have approved by hand anyway.
    [switch]$AllInstalledAddins,
    # List what is trusted and what is not, and change nothing.
    [switch]$Report
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $repo 'src\Horizun.Revit\Horizun.addin'
if (-not (Test-Path -LiteralPath $manifest)) { throw "Cannot find the add-in manifest at $manifest" }

# The AddInId is READ FROM THE MANIFEST, never typed here. A GUID copied into a
# second place is a GUID that will disagree with the first one after a rename, and
# the symptom would be Revit asking again for reasons nobody could see.
$xml = [xml](Get-Content -LiteralPath $manifest -Raw)
$addInId = ($xml.RevitAddIns.AddIn | Select-Object -First 1).AddInId
if ([string]::IsNullOrWhiteSpace($addInId)) { throw "No AddInId in $manifest" }
$addInId = $addInId.Trim('{', '}', ' ')
Write-Host "[trust] add-in identity from the manifest: $addInId" -ForegroundColor Cyan

if (-not $Years -or $Years.Count -eq 0) {
    # Every year Revit has a profile for on this machine, not a hardcoded list: a
    # machine with 2028 on it should not need this script edited.
    $root = 'HKCU:\SOFTWARE\Autodesk\Revit'
    $Years = @(Get-ChildItem $root -ErrorAction SilentlyContinue |
        ForEach-Object { if ($_.PSChildName -match '^Autodesk Revit (\d{4})$') { [int]$Matches[1] } } |
        Sort-Object -Unique)
}
if (-not $Years -or $Years.Count -eq 0) {
    Write-Host "[trust] no Revit profiles found under HKCU. Start each Revit once, then re-run." -ForegroundColor Yellow
    exit 2
}

# ---- every installed add-in, when asked ----
# One entry per (year, AddInId). A manifest can declare several AddIns, and a name is
# carried only so the output reads like something a person can check.
$targets = @()
if ($AllInstalledAddins -or $Report) {
    foreach ($y in $Years) {
        $dir = Join-Path $env:APPDATA ("Autodesk\Revit\Addins\" + $y)
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($m in (Get-ChildItem -LiteralPath $dir -Filter '*.addin' -File -ErrorAction SilentlyContinue)) {
            try { $mx = [xml](Get-Content -LiteralPath $m.FullName -Raw) } catch { continue }
            foreach ($a in @($mx.RevitAddIns.AddIn)) {
                if ($null -eq $a) { continue }
                $id = "$($a.AddInId)".Trim('{', '}', ' ')
                if ([string]::IsNullOrWhiteSpace($id)) { continue }
                $nm = "$($a.Name)"; if ([string]::IsNullOrWhiteSpace($nm)) { $nm = $m.Name }
                $targets += [pscustomobject]@{ Year = $y; Id = $id; Name = $nm }
            }
        }
    }
}
else {
    foreach ($y in $Years) { $targets += [pscustomobject]@{ Year = $y; Id = $addInId; Name = 'Horizun MCP' } }
}

if ($Report) {
    Write-Host "[trust] what each Revit will ask about:" -ForegroundColor Cyan
    foreach ($t in ($targets | Sort-Object Year, Name)) {
        $k = "HKCU:\SOFTWARE\Autodesk\Revit\Autodesk Revit $($t.Year)\CodeSigning"
        $v = $null
        if (Test-Path $k) { $v = (Get-ItemProperty -Path $k -Name $t.Id -ErrorAction SilentlyContinue).$($t.Id) }
        $state = if ($v -eq 1) { 'trusted' } else { 'WILL PROMPT' }
        $colour = if ($v -eq 1) { 'DarkGray' } else { 'Yellow' }
        Write-Host ("    {0}  {1,-46} {2}" -f $t.Year, $t.Name, $state) -ForegroundColor $colour
    }
    Write-Host ""
    Write-Host "[trust] re-run with -AllInstalledAddins to trust everything listed as WILL PROMPT." -ForegroundColor Cyan
    exit 0
}

$changed = 0
foreach ($t in $targets) {
    $y = $t.Year; $addInId = $t.Id
    $key = "HKCU:\SOFTWARE\Autodesk\Revit\Autodesk Revit $y\CodeSigning"
    $label = "Revit $y / $($t.Name)"

    if ($Revoke) {
        if ((Test-Path $key) -and $null -ne (Get-ItemProperty -Path $key -Name $addInId -ErrorAction SilentlyContinue)) {
            if ($PSCmdlet.ShouldProcess($label, "remove the Always Load record")) {
                Remove-ItemProperty -Path $key -Name $addInId -Force
                Write-Host "    $label : trust REMOVED - Revit will ask again" -ForegroundColor Yellow
                $changed++
            }
        }
        else { Write-Host "    $label : nothing to remove" -ForegroundColor DarkGray }
        continue
    }

    $existing = $null
    if (Test-Path $key) {
        $existing = (Get-ItemProperty -Path $key -Name $addInId -ErrorAction SilentlyContinue).$addInId
    }
    if ($existing -eq 1) {
        Write-Host "    $label : already trusted, nothing to do" -ForegroundColor DarkGray
        continue
    }
    if ($PSCmdlet.ShouldProcess($label, "record Always Load for $addInId")) {
        # NEVER New-Item -Force on an existing registry KEY: it deletes and recreates
        # it, wiping every value inside - which here means every OTHER add-in's trust
        # record. That is exactly what happened the first time this ran, and it turned
        # 4 pending prompts into 16.
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        # REG_DWORD 1 - the same type and value the dialog writes. A string "1"
        # here would sit in the registry looking correct and Revit would ignore it.
        New-ItemProperty -Path $key -Name $addInId -Value 1 -PropertyType DWord -Force | Out-Null
        Write-Host "    $label : trusted" -ForegroundColor Green
        $changed++
    }
}

Write-Host ""
if ($Revoke) {
    Write-Host "[trust] $changed record(s) revoked. Revit will show the dialog again for those." -ForegroundColor Cyan
}
else {
    Write-Host "[trust] $changed record(s) written across $($Years.Count) Revit year(s). Revit will not ask about these add-ins again." -ForegroundColor Cyan
    Write-Host "        A Revit that is ALREADY OPEN has read the old value - restart it." -ForegroundColor DarkYellow
    Write-Host "        Windows may still warn about the unsigned installer; that is a" -ForegroundColor DarkYellow
    Write-Host "        certificate, not this record, and it is a separate decision." -ForegroundColor DarkYellow
}
