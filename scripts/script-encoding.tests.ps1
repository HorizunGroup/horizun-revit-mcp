#Requires -Version 5.1
<#
  EVERY SCRIPT READS THE SAME UNDER WINDOWS POWERSHELL 5.1 AS UNDER POWERSHELL 7.

  PowerShell 7 reads a script without a byte-order mark as UTF-8; Windows
  PowerShell 5.1 reads it as the ANSI code page. An em dash is E2 80 94 in UTF-8,
  and 0x94 in Windows-1252 is a closing double quote - so inside a double-quoted
  string 5.1 sees the string end early. verify-optimization-live.ps1 declared
  #Requires -Version 5.1 and could not be parsed by 5.1 at all: parameter-shadowing
  reported it as "The string is missing the terminator" (2026-10-10).

  The rule: a script that holds any byte above 0x7F starts with a UTF-8 BOM. Then
  both hosts read the same characters. (install-release.ps1 is the exception that
  proves it: it runs through `irm | iex`, where a BOM is kept as text, so it must be
  pure ASCII - install-release-helpers.tests.ps1 holds that rule.)
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$skip = '\\(\.git|bin|obj|dist|node_modules|artifacts)\\'

$checked = 0
$offenders = New-Object System.Collections.Generic.List[string]
$asciiOnly = @('install-release.ps1')
# -Include is ignored beside -LiteralPath under 5.1, so the extension is filtered here.
foreach ($f in Get-ChildItem -LiteralPath $repo -Recurse -File) {
    if ($f.Extension -notin '.ps1', '.psm1', '.psd1' -or $f.FullName -match $skip) { continue }
    $checked++
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $high = 0
    foreach ($b in $bytes) { if ($b -gt 0x7F) { $high++ } }
    $rel = $f.FullName.Substring($repo.Length + 1)
    if ($asciiOnly -contains $f.Name) {
        if ($high -gt ($(if ($bom) { 3 } else { 0 }))) { $offenders.Add("$rel must be pure ASCII (it runs through irm | iex)") }
        continue
    }
    if ($high -gt 0 -and -not $bom) { $offenders.Add("$rel holds $high non-ASCII byte(s) and no UTF-8 BOM") }
}

Write-Host "  scripts checked: $checked"
if ($checked -lt 50) { Write-Host "  FAIL  fewer than 50 scripts found - the walk is wrong, not the scripts"; exit 1 }
if ($offenders.Count -gt 0) {
    Write-Host '  FAIL  these scripts read differently under Windows PowerShell 5.1:'
    foreach ($o in $offenders) { Write-Host "    $o" }
    Write-Host '  Save each as UTF-8 WITH BOM, or replace its non-ASCII characters.'
    exit 1
}
Write-Host '[PASS] every script with non-ASCII text carries a UTF-8 BOM, so 5.1 and 7 read the same characters'
