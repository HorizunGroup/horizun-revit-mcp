#Requires -Version 5.1
<#
  Verify the installed Horizun release without trusting the installer's exit code.

  The checks are deliberately layered:
    1. the installed server and every installed Revit payload match manifest.json;
    2. the requested client configuration points at that exact server, preserving
       the long timeouts Revit work needs;
    3. when Revit is running, the installed server answers horizun_health.

  Revit must be closed while Setup replaces the add-in, so a correct fresh install
  normally finishes in `awaiting_revit`. complete-install.ps1 keeps that last check
  pending and completes it automatically after the first Revit start.

  Exit codes: 0 verified for the state currently available
              1 a check failed
              3 live verification was required but Revit is not available yet
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Claude', 'Codex', 'Both', 'None')]
    [string]$Client = 'Auto',
    [string]$Name = 'horizun-revit',
    [string]$ServerPath,
    [string]$ManifestPath,
    [string]$RevitProgramRoot = 'C:\Program Files\Autodesk',
    [string]$UserAddinsRoot,
    [string]$MachineAddinsRoot,
    [switch]$SkipLive,
    [switch]$RequireLive,
    [string]$Json
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toml-section.lib.ps1')

if (-not $ServerPath) {
    $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
}
if (-not $ManifestPath) {
    $ManifestPath = Join-Path (Split-Path -Parent (Split-Path -Parent $ServerPath)) 'manifest.json'
}
if (-not $UserAddinsRoot) { $UserAddinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins' }
if (-not $MachineAddinsRoot) { $MachineAddinsRoot = Join-Path $env:PROGRAMDATA 'Autodesk\Revit\Addins' }

$claudeConfig = Join-Path $env:USERPROFILE '.claude.json'
$codexConfig = Join-Path $env:USERPROFILE '.codex\config.toml'
$checks = New-Object System.Collections.Generic.List[object]
$problems = New-Object System.Collections.Generic.List[string]
$livePending = $false
$health = $null
$horizunAddInId = 'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30'

function Check([string]$Label, [bool]$Ok, [string]$Detail) {
    $script:checks.Add([pscustomobject]@{ check = $Label; ok = $Ok; detail = $Detail }) | Out-Null
    if ($Ok) { Write-Host ("  OK    {0}" -f $Label) -ForegroundColor Green }
    else {
        Write-Host ("  WRONG {0} - {1}" -f $Label, $Detail) -ForegroundColor Red
        $script:problems.Add("$Label : $Detail") | Out-Null
    }
}

function Resolve-RequestedClient([string]$Requested) {
    if ($Requested -ne 'Auto') { return $Requested }
    $hasClaude = Test-Path -LiteralPath $claudeConfig
    $hasCodex = Test-Path -LiteralPath $codexConfig
    if ($hasClaude -and $hasCodex) { return 'Both' }
    if ($hasClaude) { return 'Claude' }
    if ($hasCodex) { return 'Codex' }
    return 'None'
}

function Get-CodexTable([string[]]$Lines, [string]$Header) {
    $range = Get-HorizunTomlTableRange $Lines $Header $Name
    if (-not $range -or $range.EndExclusive -le $range.Start) { return @() }
    return @($Lines[$range.Start..($range.EndExclusive - 1)])
}

function Resolve-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { return [IO.Path]::GetFullPath($Path).TrimEnd('\') }
    catch { return $Path.TrimEnd('\') }
}

function Get-PayloadTreeSnapshot([string]$Root) {
    $files = @()
    $problems = @()
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $volume = [IO.Path]::GetPathRoot($full)
    $current = $volume.TrimEnd('\')
    if (-not $current) { $current = $volume }
    foreach ($component in $full.Substring($volume.Length).Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $component
        if (-not (Test-Path -LiteralPath $current)) { break }
        $ancestor = Get-Item -LiteralPath $current -Force
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $problems += "${current}: link or junction in payload path"
            return [pscustomobject]@{ Files = @(); Problems = @($problems) }
        }
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        return [pscustomobject]@{ Files = @(); Problems = @($problems) }
    }
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue($full)
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $problems += "$($item.FullName): link or junction inside payload"
                continue
            }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
            else { $files += $item }
        }
    }
    return [pscustomobject]@{ Files = @($files); Problems = @($problems) }
}

function Get-AggregatedPayloadSubtree([string]$Root, [string]$Subtree) {
    $directory = Join-Path $Root $Subtree
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return [pscustomobject]@{ Count = 0; Digest = $null; Problems = @() }
    }
    $snapshot = Get-PayloadTreeSnapshot $directory
    $problems = @($snapshot.Problems)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $rows = @()
    foreach ($item in @($snapshot.Files)) {
        $relative = $item.FullName.Substring($rootFull.Length).Replace('\', '/')
        $rows += [pscustomobject]@{ Rel = $relative; Path = $item.FullName }
    }
    $digest = $null
    if ($rows.Count -gt 0) {
        $builder = New-Object Text.StringBuilder
        $orderedRows = New-Object 'System.Collections.Generic.List[object]'
        foreach ($row in $rows) { $orderedRows.Add($row) }
        $orderedRows.Sort([System.Comparison[object]]{
            param($left, $right)
            [StringComparer]::Ordinal.Compare([string]$left.Rel, [string]$right.Rel)
        })
        foreach ($row in $orderedRows) {
            [void]$builder.Append($row.Rel).Append([char]31)
            [void]$builder.Append((Get-FileHash -LiteralPath $row.Path -Algorithm SHA256).Hash.ToLowerInvariant()).Append([char]30)
        }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($builder.ToString()))
            $digest = ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
        }
        finally { $algorithm.Dispose() }
    }
    return [pscustomobject]@{ Count = $rows.Count; Digest = $digest; Problems = @($problems) }
}

function Check-ManifestPayload([string]$Label, [string]$Root, $Payload, $StdLibFiles, $StdLibDigest) {
    if (-not $Payload) {
        Check "$Label payload inventory exists" $false 'manifest carries no per-file payload inventory'
        return
    }
    $wrong = @()
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $rootPrefix = $rootFull + '\'
    $snapshot = Get-PayloadTreeSnapshot $rootFull
    $wrong += @($snapshot.Problems)
    $expected = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $hasAggregatedStdLib = ($null -ne $StdLibFiles -or -not [string]::IsNullOrWhiteSpace([string]$StdLibDigest))
    if ($hasAggregatedStdLib) {
        if ($null -eq $StdLibFiles -or [string]::IsNullOrWhiteSpace([string]$StdLibDigest)) {
            $wrong += 'lib/: aggregate requires both StdLibFiles and StdLibDigest'
        }
        else {
            $actualStdLib = Get-AggregatedPayloadSubtree $rootFull 'lib'
            $wrong += @($actualStdLib.Problems)
            if ([int]$actualStdLib.Count -ne [int]$StdLibFiles) {
                $wrong += "lib/: file count $($actualStdLib.Count), manifest $StdLibFiles"
            }
            if ([string]$actualStdLib.Digest -ne [string]$StdLibDigest) {
                $wrong += 'lib/: aggregate digest mismatch'
            }
        }
    }
    foreach ($item in @($Payload)) {
        $relative = ([string]$item.Path).Replace('/', '\')
        if (-not $relative -or [IO.Path]::IsPathRooted($relative)) {
            $wrong += "$($item.Path): invalid relative path"
            continue
        }
        $path = [IO.Path]::GetFullPath((Join-Path $rootFull $relative))
        if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $wrong += "$($item.Path): escapes payload root"
            continue
        }
        [void]$expected.Add($path.Substring($rootPrefix.Length))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $wrong += "$($item.Path): missing"
            continue
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne [string]$item.Sha256) { $wrong += "$($item.Path): hash mismatch" }
    }
    if (Test-Path -LiteralPath $rootFull -PathType Container) {
        foreach ($actualFile in @($snapshot.Files)) {
            $actualRelative = $actualFile.FullName.Substring($rootPrefix.Length)
            if ($hasAggregatedStdLib -and $actualRelative.StartsWith('lib\', [StringComparison]::OrdinalIgnoreCase)) { continue }
            if (-not $expected.Contains($actualRelative)) { $wrong += "${actualRelative}: unexpected file" }
        }
    }
    Check "$Label complete payload matches the release manifest" ($wrong.Count -eq 0) ($wrong -join '; ')
}

function Get-ManifestsWithAddInId([string]$Root, [int]$Year, [string]$AddInId) {
    $yearRoot = Join-Path $Root ([string]$Year)
    $snapshot = Get-PayloadTreeSnapshot $yearRoot
    if ($snapshot.Problems.Count -gt 0) { throw ($snapshot.Problems -join '; ') }
    if (-not (Test-Path -LiteralPath $yearRoot -PathType Container)) { return @() }
    $needle = [Guid]::Empty
    if (-not [Guid]::TryParse($AddInId.Trim(), [ref]$needle)) {
        throw "Invalid expected Revit AddInId: $AddInId"
    }
    $found = @()
    foreach ($candidate in @(Get-ChildItem -LiteralPath $yearRoot -Filter '*.addin' -File -Force -ErrorAction Stop)) {
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($candidate.FullName, $settings)
        try {
            while ($reader.Read()) {
                if ($reader.NodeType -eq [Xml.XmlNodeType]::Element -and $reader.LocalName -eq 'AddInId') {
                    $candidateId = [Guid]::Empty
                    $candidateValue = $reader.ReadElementContentAsString().Trim()
                    if ([Guid]::TryParse($candidateValue, [ref]$candidateId) -and $candidateId -eq $needle) {
                        $found += [IO.Path]::GetFullPath($candidate.FullName)
                        break
                    }
                }
            }
        }
        finally { $reader.Dispose() }
    }
    return @($found)
}

Write-Host ''
Write-Host 'Horizun installation verification' -ForegroundColor Cyan
Write-Host ('-' * 72)

# The release manifest is the installer's statement of exactly what it shipped.
$manifest = $null
Check 'the installed MCP server exists' (Test-Path -LiteralPath $ServerPath -PathType Leaf) $ServerPath
Check 'the installed release manifest exists' (Test-Path -LiteralPath $ManifestPath -PathType Leaf) $ManifestPath
if ((Test-Path -LiteralPath $ServerPath) -and (Test-Path -LiteralPath $ManifestPath)) {
    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        Check 'manifest schema is supported' ($manifest.Schema -eq 2) "schema '$($manifest.Schema)'"
        $serverHash = (Get-FileHash -LiteralPath $ServerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Check 'installed server matches the release manifest' ($serverHash -eq [string]$manifest.Server.Sha256) `
            "installed $serverHash; manifest $($manifest.Server.Sha256)"
        Check-ManifestPayload 'server' (Split-Path -Parent $ServerPath) $manifest.Server.Payload $null $null
    }
    catch {
        Check 'release manifest parses' $false $_.Exception.Message
    }
}

# Only a Revit version actually present on this machine should have been installed.
# A release contains all five payloads; requiring all five under AppData on a machine
# with one Revit would incorrectly condemn a correct selective install.
if ($manifest) {
    foreach ($plugin in @($manifest.Plugins)) {
        $year = [int]$plugin.Year
        $revitApi = Join-Path $RevitProgramRoot "Revit $year\RevitAPI.dll"
        if (-not (Test-Path -LiteralPath $revitApi)) { continue }
        $addin = Join-Path $UserAddinsRoot "$year\Horizun.addin"
        $dll = Join-Path $UserAddinsRoot "$year\Horizun\Horizun.Revit.dll"
        Check "Revit $year add-in manifest exists" (Test-Path -LiteralPath $addin -PathType Leaf) $addin
        Check "Revit $year add-in binary exists" (Test-Path -LiteralPath $dll -PathType Leaf) $dll
        if (Test-Path -LiteralPath $dll) {
            $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
            Check "Revit $year binary matches the release manifest" ($hash -eq [string]$plugin.Sha256) `
                "installed $hash; manifest $($plugin.Sha256)"
        }
        if (Test-Path -LiteralPath $addin -PathType Leaf) {
            if ($manifest.AddinManifest -and $manifest.AddinManifest.Sha256) {
                $addinHash = (Get-FileHash -LiteralPath $addin -Algorithm SHA256).Hash.ToLowerInvariant()
                Check "Revit $year .addin manifest matches the release" `
                    ($addinHash -eq [string]$manifest.AddinManifest.Sha256) `
                    "installed $addinHash; manifest $($manifest.AddinManifest.Sha256)"
            }
            else { Check "Revit $year .addin manifest is inventoried" $false 'manifest has no AddinManifest hash' }
        }
        Check-ManifestPayload "Revit $year" (Split-Path -Parent $dll) $plugin.Payload $plugin.StdLibFiles $plugin.StdLibDigest

        $manifests = @()
        try {
            foreach ($root in @($UserAddinsRoot, $MachineAddinsRoot) | Where-Object { $_ }) {
                $manifests += @(Get-ManifestsWithAddInId $root $year $horizunAddInId)
            }
        }
        catch {
            Check "Revit $year add-in manifest discovery is confined" $false $_.Exception.Message
            $manifests = @()
        }
        Check "Revit $year has one loadable Horizun manifest across user and machine scope" `
            ($manifests.Count -eq 1) ($manifests -join '; ')
    }
}

$resolvedClient = Resolve-RequestedClient $Client
$expectedServer = Resolve-FullPath $ServerPath

if ($resolvedClient -eq 'Claude' -or $resolvedClient -eq 'Both') {
    Check 'Claude configuration exists' (Test-Path -LiteralPath $claudeConfig -PathType Leaf) $claudeConfig
    if (Test-Path -LiteralPath $claudeConfig) {
        try {
            $cfg = Get-Content -LiteralPath $claudeConfig -Raw | ConvertFrom-Json
            $entry = $cfg.mcpServers.$Name
            Check "Claude has MCP entry '$Name'" ($null -ne $entry) 'entry not found'
            if ($entry) {
                $actual = Resolve-FullPath ([string]$entry.command)
                Check 'Claude points at the installed server' ($actual -ieq $expectedServer) "$actual"
            }
        }
        catch { Check 'Claude configuration parses' $false $_.Exception.Message }
    }
}

if ($resolvedClient -eq 'Codex' -or $resolvedClient -eq 'Both') {
    Check 'Codex configuration exists' (Test-Path -LiteralPath $codexConfig -PathType Leaf) $codexConfig
    if (Test-Path -LiteralPath $codexConfig) {
        try {
            $lines = @(Get-Content -LiteralPath $codexConfig)
            $table = @(Get-CodexTable $lines "[mcp_servers.$Name]")
            Check "Codex has MCP entry '$Name'" ($table.Count -gt 0) 'entry not found'
            if ($table.Count -gt 0) {
                $command = $null
                foreach ($line in $table) {
                    if ($line -match '^\s*command\s*=\s*("(?:[^"\\]|\\.)*")\s*$') {
                        $command = ($Matches[1] | ConvertFrom-Json)
                    }
                    elseif ($line -match "^\s*command\s*=\s*'([^']*)'\s*$") { $command = $Matches[1] }
                }
                $actual = Resolve-FullPath ([string]$command)
                Check 'Codex points at the installed server' ($actual -ieq $expectedServer) "$actual"
                Check 'Codex startup timeout is 120 seconds' (($table -join "`n") -match '(?m)^\s*startup_timeout_sec\s*=\s*120\s*$') 'missing or different'
                Check 'Codex tool timeout is 600 seconds' (($table -join "`n") -match '(?m)^\s*tool_timeout_sec\s*=\s*600\s*$') 'missing or different'
            }
        }
        catch { Check 'Codex configuration can be inspected' $false $_.Exception.Message }
    }
}

if (-not $SkipLive -and $problems.Count -eq 0) {
    $call = Join-Path $PSScriptRoot 'hz-call.ps1'
    if (-not (Test-Path -LiteralPath $call)) {
        Check 'live verification helper exists' $false $call
    }
    else {
        $targetJson = Join-Path ([IO.Path]::GetTempPath()) ('horizun-target-' + [guid]::NewGuid().ToString('N') + '.json')
        $healthJson = Join-Path ([IO.Path]::GetTempPath()) ('horizun-health-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $call -Tool horizun_target `
                -Server $ServerPath -TimeoutSec 30 -Quiet -Json $targetJson
            $targetExit = $LASTEXITCODE
            $target = if (Test-Path -LiteralPath $targetJson) { Get-Content -LiteralPath $targetJson -Raw | ConvertFrom-Json } else { $null }
            $found = if ($target -and $target.result) { [int]$target.result.targets_found } else { 0 }
            if ($targetExit -ne 0) {
                Check 'the installed server answers horizun_target' $false "exit code $targetExit"
            }
            elseif ($found -lt 1) {
                $livePending = $true
                if ($RequireLive) {
                    Write-Host '  WAIT  Revit is not running yet; horizun_health remains pending.' -ForegroundColor Yellow
                }
                else {
                    Write-Host '  READY Revit is not running; on-disk and client checks are complete.' -ForegroundColor Cyan
                }
            }
            else {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $call -Tool horizun_health `
                    -Server $ServerPath -TimeoutSec 120 -Quiet -Json $healthJson
                $healthExit = $LASTEXITCODE
                $healthReply = if (Test-Path -LiteralPath $healthJson) { Get-Content -LiteralPath $healthJson -Raw | ConvertFrom-Json } else { $null }
                $health = if ($healthReply) { $healthReply.result } else { $null }
                Check 'horizun_health answers through the installed server' ($healthExit -eq 0 -and $null -ne $health) `
                    "exit code $healthExit"
                if ($health) {
                    Check 'horizun_health reports healthy' ([string]$health.status -eq 'healthy') "status '$($health.status)'"
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $targetJson, $healthJson -Force -ErrorAction SilentlyContinue
        }
    }
}

$state = if ($problems.Count -gt 0) { 'failed' }
         elseif ($SkipLive -and $resolvedClient -eq 'None') { 'installed' }
         elseif ($SkipLive) { 'installed_and_registered' }
         elseif ($livePending) { 'awaiting_revit' }
         else { 'live_verified' }

$report = [pscustomobject]@{
    schema = 1
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    state = $state
    client = $resolvedClient
    server_path = $ServerPath
    manifest_path = $ManifestPath
    checks = $checks
    problems = $problems
    health = $health
}
if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$Json.tmp-$([guid]::NewGuid().ToString('N'))"
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $Json -Force
    Write-Host "  wrote $Json"
}

Write-Host ('-' * 72)
if ($problems.Count -gt 0) { exit 1 }
if ($RequireLive -and $livePending) { exit 3 }
Write-Host "  $state" -ForegroundColor Green
exit 0
