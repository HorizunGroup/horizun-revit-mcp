#Requires -Version 5.1
<#
  The Community Setup and bootstrap must never silently replace an installed Pro.
  Both editions share the AppId, install folder, add-in folder and MCP name, so
  the check is on what is INSTALLED. This file:
    * runs the bootstrap's Get-HorizunInstalledEdition against fixture installs
      (explicit stamp, Pro packaging key, Pro-only type in the add-in, legacy
      unmarked Community, nothing installed);
    * holds installer/horizun-mcp.iss, install-release.ps1 and pack.ps1 to the
      same signals, the same markers, the refusal before any change, the silent
      non-zero exit and the absence of an override switch.
  The Inno code itself cannot run here: compile it with ISCC (see CHANGELOG).
#>
$ErrorActionPreference = 'Stop'
$failed = 0
function Assert([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name" -ForegroundColor Red; if ($Detail) { Write-Host "        $Detail" }; $script:failed++ }
}

$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'install-release.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-over-pro-' + [guid]::NewGuid().ToString('N'))

function New-Fixture([string]$Name, $ManifestJson, $DllText) {
    $dir = Join-Path $root $Name
    $app = Join-Path $dir 'app'; $addin = Join-Path $dir 'addin'
    New-Item -ItemType Directory -Path $app, $addin -Force | Out-Null
    if ($null -ne $ManifestJson) { [IO.File]::WriteAllText((Join-Path $app 'manifest.json'), $ManifestJson) }
    if ($null -ne $DllText) { [IO.File]::WriteAllBytes((Join-Path $addin 'Horizun.Revit.dll'), [Text.Encoding]::GetEncoding(28591).GetBytes("MZ`0`0$DllText`0`0")) }
    [pscustomobject]@{ App = $app; Addin = $addin }
}

try {
    Write-Host "community-over-pro under PowerShell $($PSVersionTable.PSVersion)"

    $f = New-Fixture 'pro-stamped' '{"Schema":2,"Edition":"pro","Commit":"abc"}' 'Horizun.Commands.ElectricalCommand'
    $r = Get-HorizunInstalledEdition $f.App @($f.Addin)
    Assert 'explicit Edition=pro is Pro' ($r.Edition -eq 'pro') ($r.Signals -join '; ')

    $f = New-Fixture 'weird-edition' '{"Schema":2,"Edition":"enterprise"}' $null
    Assert 'any non-community Edition fails closed as Pro' ((Get-HorizunInstalledEdition $f.App @($f.Addin)).Edition -eq 'pro')

    $f = New-Fixture 'community-stamped' '{"Schema":2,"Edition":"community"}' 'PlanMepCommand'
    Assert 'explicit Edition=community wins over the add-in scan' ((Get-HorizunInstalledEdition $f.App @($f.Addin)).Edition -eq 'community')

    $f = New-Fixture 'pro-licensekey' '{"Schema":2,"Commit":"abc","LicenseTestKey":false,"Config":"Release"}' $null
    $r = Get-HorizunInstalledEdition $f.App @($f.Addin)
    Assert 'manifest without Edition but with LicenseTestKey is Pro' ($r.Edition -eq 'pro') ($r.Signals -join '; ')

    $f = New-Fixture 'pro-dll-only' '{"Schema":2,"Commit":"abc","Config":"Release"}' 'x ElectricalCommand y'
    $r = Get-HorizunInstalledEdition $f.App @($f.Addin)
    Assert 'unstamped manifest with a Pro-only type in the add-in is Pro' ($r.Edition -eq 'pro') ($r.Signals -join '; ')

    $f = New-Fixture 'pro-source-install' $null 'ClashCommand PlanMepCommand'
    Assert 'no manifest but a Pro add-in (source install) is Pro' ((Get-HorizunInstalledEdition $f.App @($f.Addin)).Edition -eq 'pro')

    $f = New-Fixture 'legacy-community' '{"Schema":2,"Commit":"abc","Config":"Release"}' 'Horizun.Commands.QueryModelCommand'
    Assert 'unmarked Community 6.0.0 upgrades (Community over Community)' ((Get-HorizunInstalledEdition $f.App @($f.Addin)).Edition -eq 'community')

    $f = New-Fixture 'empty' $null $null
    Assert 'nothing installed is none' ((Get-HorizunInstalledEdition $f.App @($f.Addin)).Edition -eq 'none')

    $f = New-Fixture 'broken-manifest' '{not json' $null
    Assert 'an unreadable manifest alone does not claim Pro' ((Get-HorizunInstalledEdition $f.App @($f.Addin)).Edition -eq 'community')

    $msg = Get-HorizunProInstalledMessage
    Assert 'the refusal is bilingual and says how to switch' ($msg -match 'desinstala primero' -and $msg -match 'first uninstall' -and $msg -match 'Pro ya incluye' -and $msg -match 'already contains')

    # ---- the shipped files agree with each other ----
    $iss = Get-Content (Join-Path $repo 'installer/horizun-mcp.iss') -Raw
    $boot = Get-Content (Join-Path $repo 'install-release.ps1') -Raw
    $pack = Get-Content (Join-Path $repo 'scripts/pack.ps1') -Raw
    foreach ($m in $script:HorizunProMarkers) {
        Assert "Setup scans for the same Pro-only type '$m'" ($iss.Contains("'$m'"))
    }
    Assert 'Setup reads Edition and LicenseTestKey from manifest.json' ($iss -match '"Edition"' -and $iss -match '"LicenseTestKey"')
    Assert 'pack.ps1 stamps Edition community in manifest.json' ($pack -match "Edition\s+=\s+'community'")
    $initAt = $iss.IndexOf('function InitializeSetup')
    $proAt = $iss.IndexOf('InstalledProEvidence', $initAt)
    $revitAt = $iss.IndexOf('RevitIsRunning then', $initAt)
    Assert 'Setup checks for a Pro first thing in InitializeSetup, before any other preflight' ($proAt -gt $initAt -and $proAt -lt $revitAt)
    Assert 'a silent Setup exits 14 without a message box and logs the reason' `
        ($iss -match "(?s)if WizardSilent then.{0,400}Log\('Preflight refused: cause=pro_installed.{0,300}Win32ExitProcess\(14\)")
    Assert 'an interactive Setup refuses through the shared preflight refusal with exit 14' ($iss -match "(?s)RefusePreflight\('pro_installed'.{0,1200}Evidence, 14\)")
    Assert 'there is no override switch' ($iss -notmatch 'REPLACEPRO' -and $boot -notmatch 'ReplacePro')
    $bootCheck = $boot.IndexOf('Get-HorizunInstalledEdition (Get-HorizunInstalledAppDir)')
    Assert 'the bootstrap checks before creating the temp folder or touching the network' `
        ($bootCheck -gt 0 -and $bootCheck -lt $boot.IndexOf('New-Item -ItemType Directory -Path $temporary') -and $bootCheck -lt $boot.IndexOf('Invoke-RestMethod'))
    Assert 'the bootstrap stays pure ASCII' (-not ([IO.File]::ReadAllBytes((Join-Path $repo 'install-release.ps1')) | Where-Object { $_ -gt 127 }))

    # A Community build must not itself carry what marks a Pro (only checkable when a stage exists).
    $dll = Join-Path $repo 'dist/stage/plugin/2026/Horizun.Revit.dll'
    if (Test-Path -LiteralPath $dll) {
        $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
        $hit = @($script:HorizunProMarkers | Where-Object { $text.Contains($_) })
        Assert 'the staged Community add-in does not contain the Pro markers' ($hit.Count -eq 0) ($hit -join ',')
    } else { Write-Host '  SKIP  staged add-in check (no dist/stage)' -ForegroundColor Yellow }
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }

if ($failed) { Write-Host "$failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All community-over-pro checks passed.' -ForegroundColor Green
