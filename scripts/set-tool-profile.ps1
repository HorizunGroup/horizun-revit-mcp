#Requires -Version 5.1
<#
  Write the 4.0 tool profile into the installed settings.json - and NOTHING else.

  The tool profile decides which tool groups an MCP client sees in tools/list
  (core + the profile's groups; see docs/TOOL-GROUPS.md). It is VISIBILITY, never
  permission: the permission profile, the Python grant and every other key of
  settings.json are left exactly as they are.

  NEW INSTALL vs UPGRADE - the rule the installer follows:
    -Mode new      writes the chosen profile (the installer's page pre-selects one).
    -Mode upgrade  with -ToolProfile keep (the default for an upgrade) writes NOTHING:
                   a machine without "tool_profile" keeps the 3.x behaviour, every
                   tool listed. Choosing a profile on an upgrade writes it.
  "completo" is written explicitly when chosen, so the choice is visible later.

  A settings.json that is not valid JSON is never rewritten: the owner's other
  choices are in it. The script refuses, says so, and exits 3.

    set-tool-profile.ps1 -ToolProfile auditor -Mode new
    set-tool-profile.ps1 -ToolProfile keep    -Mode upgrade
    set-tool-profile.ps1 -Show

  Exit codes: 0 written or kept   2 bad argument   3 settings unreadable, nothing written
#>
[CmdletBinding()]
param(
    [string]$ToolProfile = 'keep',
    [ValidateSet('new', 'upgrade')]
    [string]$Mode = 'upgrade',
    [string]$SettingsPath,
    [string]$ResultPath,
    [switch]$Show
)
$ErrorActionPreference = 'Stop'

# THE SAME LIST AS ToolGroupCatalog.Profiles (src/Horizun.Contracts/Contract.cs).
# ToolProfileInstallerTests fails when the two differ.
$Profiles = @('nucleo', 'auditor', 'modelador_arquitectura', 'modelador_estructura', 'modelador_mep',
              'modelador', 'documentador', 'coordinador', 'analista_datos', 'completo')

if (-not $SettingsPath) {
    $root = $env:HORIZUN_DATA_ROOT
    if ([string]::IsNullOrWhiteSpace($root)) { $root = Join-Path $env:USERPROFILE '.horizun' }
    $SettingsPath = Join-Path $root 'settings.json'
}

function Write-Result([string]$text) {
    Write-Host $text
    if ($ResultPath) { Set-Content -LiteralPath $ResultPath -Value $text -Encoding UTF8 }
}

function Read-Settings {
    if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) { return [ordered]@{} }
    $raw = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8
    # An EMPTY or non-object file is not "no settings": Settings.cs reads it as malformed
    # and falls closed (read_only). Rewriting it as {} + tool_profile would silently move
    # that machine to safe_write, so it is refused like any other unreadable file.
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try { $obj = $raw | ConvertFrom-Json } catch { return $null }
    if ($null -eq $obj -or $obj -isnot [System.Management.Automation.PSCustomObject]) { return $null }
    $map = [ordered]@{}
    foreach ($p in $obj.PSObject.Properties) { $map[$p.Name] = $p.Value }
    return $map
}

$current = Read-Settings
if ($Show) {
    if ($null -eq $current) { Write-Result "tool_profile=unreadable settings=$SettingsPath"; exit 3 }
    $v = if ($current.Contains('tool_profile')) { $current['tool_profile'] } else { '(none: completo, 3.x behaviour)' }
    Write-Result "tool_profile=$v settings=$SettingsPath"
    exit 0
}

$ToolProfile = ("$ToolProfile").Trim().ToLowerInvariant()
if ($ToolProfile -ne 'keep' -and $Profiles -notcontains $ToolProfile) {
    Write-Result "refused: unknown profile '$ToolProfile'. Profiles: $($Profiles -join ', '), or keep."
    exit 2
}
if ($ToolProfile -eq 'keep') {
    if ($Mode -eq 'new') {
        Write-Result "refused: a new install chooses a profile; 'keep' is for upgrades."
        exit 2
    }
    $v = if ($null -ne $current -and $current.Contains('tool_profile')) { $current['tool_profile'] } else { '(none)' }
    Write-Result "kept: tool_profile=$v (nothing written)"
    exit 0
}
if ($null -eq $current) {
    Write-Result "refused: $SettingsPath is not valid JSON; the owner's settings are in it, so nothing was written."
    exit 3
}

$current['tool_profile'] = $ToolProfile
$current['tool_profile_selected_by'] = "installer ($Mode)"
$current['tool_profile_selected_at_utc'] = (Get-Date).ToUniversalTime().ToString('o')

$dir = Split-Path -Parent $SettingsPath
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$json = [pscustomobject]$current | ConvertTo-Json -Depth 32
$SettingsPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SettingsPath)
$tmp = "$SettingsPath.tool-profile.tmp"
[IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
# File.Replace (ReplaceFile) keeps the replaced file's ACL, owner and attributes; a
# Move-Item -Force would leave the temporary's folder-inherited ACL on settings.json,
# loosening a protected file in a shared HORIZUN_DATA_ROOT.
if (Test-Path -LiteralPath $SettingsPath -PathType Leaf) { [IO.File]::Replace($tmp, $SettingsPath, [NullString]::Value) }
else { [IO.File]::Move($tmp, $SettingsPath) }

# Read back: a write is not reported until the file says it.
$after = Read-Settings
if ($null -eq $after -or $after['tool_profile'] -ne $ToolProfile) {
    Write-Result "failed: $SettingsPath does not read back tool_profile=$ToolProfile"
    exit 3
}
Write-Result "written: tool_profile=$ToolProfile ($Mode) settings=$SettingsPath"
exit 0
