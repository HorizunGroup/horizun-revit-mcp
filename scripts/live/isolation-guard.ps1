<#
  WHAT THE MACHINE LOOKS LIKE, before and after an offline test run.

  The suite is written to touch nothing real - every file it writes goes under
  %TEMP%, and every call to the real dev-addin-session.ps1 runs with %APPDATA%
  redirected to a temporary folder. That is a claim, and this is how it is
  checked rather than asserted: the five installed add-in manifests and their
  DLLs, the installed server, the owner's settings file and the list of running
  Revit processes are fingerprinted before and after, and any difference is
  printed.

  It reads. It starts nothing, closes nothing and changes nothing.

      pwsh -File isolation-guard.ps1 -Out before.json
      pwsh -File isolation-guard.ps1 -Out after.json -CompareWith before.json
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Out, [string]$CompareWith)
$ErrorActionPreference = 'Stop'

function FileFact([string]$Path) {
    # ORDERED, so two snapshots of an unchanged machine are byte-identical: a
    # plain @{} enumerates in whatever order it likes, and the first version of
    # this guard reported "DIFFERENT" over nothing but key order.
    if (-not (Test-Path -LiteralPath $Path)) { return [ordered]@{ present = $false } }
    try {
        $item = Get-Item -LiteralPath $Path
        return [ordered]@{ present = $true; sha256 = (Get-FileHash -LiteralPath $Path).Hash.ToLower()
                           length = $item.Length; last_write_utc = $item.LastWriteTimeUtc.ToString('o') }
    }
    catch { return [ordered]@{ present = $true; error = $_.Exception.Message } }
}

function Canonical($Value) {
    # The same facts in the same order whatever wrote them, so the verdict is
    # about the machine and never about JSON formatting.
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $parts = @()
        foreach ($name in (@($Value.PSObject.Properties.Name) | Sort-Object)) {
            $parts += ('"' + $name + '":' + (Canonical $Value.$name))
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $parts = @()
        foreach ($name in (@($Value.Keys) | Sort-Object)) { $parts += ('"' + $name + '":' + (Canonical $Value[$name])) }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [string]) { return '"' + $Value + '"' }
    if ($Value -is [System.Collections.IEnumerable]) {
        $parts = @(); foreach ($item in $Value) { $parts += (Canonical $item) }
        return '[' + ($parts -join ',') + ']'
    }
    return ([string]$Value)
}

$snapshot = [ordered]@{
    taken_utc = (Get-Date).ToUniversalTime().ToString('o')
    addins = [ordered]@{}
    installed_server = FileFact (Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe')
    settings = FileFact (Join-Path $env:USERPROFILE '.horizun\settings.json')
    revit_processes = @()
}
foreach ($year in '2023', '2024', '2025', '2026', '2027') {
    $dir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $snapshot.addins[$year] = [ordered]@{
        directory_entries = @(if (Test-Path -LiteralPath $dir) { (Get-ChildItem -LiteralPath $dir -Force | ForEach-Object { $_.Name }) | Sort-Object } else { @() })
        installed_manifest = FileFact (Join-Path $dir 'Horizun.addin')
        dev_manifest = FileFact (Join-Path $dir 'Horizun-dev-session.addin')
        aside_manifest = FileFact (Join-Path $dir 'Horizun.addin.dev-session-aside')
        installed_dll = FileFact (Join-Path $dir 'Horizun\Horizun.Revit.dll')
    }
}
# Read-only: the pid, when it started and what it runs. No window is touched, no
# message is sent, nothing is opened.
foreach ($p in @(Get-Process Revit -ErrorAction SilentlyContinue)) {
    $exe = $null; try { $exe = $p.MainModule.FileName } catch { $exe = '<unreadable>' }
    $start = $null; try { $start = $p.StartTime.ToUniversalTime().ToString('o') } catch { $start = $null }
    $snapshot.revit_processes += [ordered]@{ pid = $p.Id; exe = $exe; started_utc = $start }
}
($snapshot | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $Out -Encoding utf8
Write-Host ("snapshot -> {0}" -f $Out)
Write-Host ("  Revit processes: " + $(if ($snapshot.revit_processes.Count -eq 0) { 'none' } else { ($snapshot.revit_processes | ForEach-Object { "$($_.pid) $($_.exe)" }) -join '; ' }))

if ($CompareWith) {
    # Compare the substance, not the timestamp of the snapshot itself.
    $aObj = Get-Content -LiteralPath $CompareWith -Raw | ConvertFrom-Json
    $bObj = Get-Content -LiteralPath $Out -Raw | ConvertFrom-Json
    $aTxt = Canonical ($aObj | Select-Object -Property * -ExcludeProperty taken_utc)
    $bTxt = Canonical ($bObj | Select-Object -Property * -ExcludeProperty taken_utc)
    if ($aTxt -eq $bTxt) {
        Write-Host "  IDENTICAL to $CompareWith - manifests, DLLs, installed server, settings and running Revit processes all unchanged" -ForegroundColor Green
        exit 0
    }
    Write-Host "  DIFFERENT from $CompareWith :" -ForegroundColor Red
    # Name WHAT differs, field by field, instead of printing two blobs.
    function Report($X, $Y, [string]$Path) {
        if (($null -eq $X) -or ($null -eq $Y)) {
            if ((Canonical $X) -ne (Canonical $Y)) { Write-Host ("   {0}: {1} -> {2}" -f $Path, (Canonical $X), (Canonical $Y)) -ForegroundColor Red }
            return
        }
        if (($X -is [System.Management.Automation.PSCustomObject]) -and ($Y -is [System.Management.Automation.PSCustomObject])) {
            foreach ($name in (@(@($X.PSObject.Properties.Name) + @($Y.PSObject.Properties.Name)) | Sort-Object -Unique)) {
                Report $X.$name $Y.$name ($Path + '/' + $name)
            }
            return
        }
        if ((Canonical $X) -ne (Canonical $Y)) {
            Write-Host ("   {0}: {1} -> {2}" -f $Path, (Canonical $X), (Canonical $Y)) -ForegroundColor Red
        }
    }
    Report ($aObj | Select-Object -Property * -ExcludeProperty taken_utc) ($bObj | Select-Object -Property * -ExcludeProperty taken_utc) ''
    exit 1
}
exit 0
