#Requires -Version 5.1
<#
  THE CAPABILITY INVENTORY, GENERATED - never counted by hand.

  A headline number nobody can reproduce is a claim, not a measurement. This
  script asks the BUILT SERVER what it serves (tools/list over stdio, the same
  call a client makes), reads the schemas it answers with, and emits
  docs/inventory.json. Every number downstream - README, TOOLS.md, the ledger,
  a report - must come from here.

  FOUR THINGS ARE COUNTED, AND THEY ARE NOT THE SAME THING:

    tools                MCP tool names the server lists.
    reads                tools annotated readOnlyHint - they cannot change the
                         model, so they are the safe surface.
    operations           DISPATCHED behaviours: an enum-valued property the
                         command switches on to decide WHAT to do (operation,
                         action, kind, mode, subtype, fitting). Each declared
                         selector is CROSS-CHECKED against the command source:
                         if the code never reads that property by name, the
                         inventory says so instead of trusting the declaration.
    enumerated variants  every (tool, property, enum value) triple in every
                         schema. Mechanical, no judgment - and deliberately NOT
                         the headline, because an enum value is an argument, not
                         a proven behaviour.

  WHAT THIS SCRIPT DOES NOT MEASURE: whether any of it works. Verified
  behaviour is counted by the live harness artifact, never by a schema.
#>
[CmdletBinding()]
param(
    [string]$ServerExe,
    [string]$OutJson = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\inventory.json'),
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# The properties a command switches on. Each is verified against source below;
# an unverified entry is REPORTED, never silently trusted.
$DispatchSelectors = @('operation', 'operations', 'action', 'actions', 'kind', 'mode', 'subtype', 'fitting')

if (-not $ServerExe) {
    $candidates = @(
        (Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'),
        (Join-Path $repo 'src\Horizun.Server\bin\Debug\net8.0\horizun-mcp.exe')
    ) | Where-Object { Test-Path $_ }
    if (-not $candidates) {
        Write-Host '[inventory] building the server (no binary found)...' -ForegroundColor DarkGray
        & dotnet build (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c Release --nologo -v q | Out-Null
        $candidates = @((Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe')) | Where-Object { Test-Path $_ }
    }
    if (-not $candidates) { throw 'no server binary to ask; build src/Horizun.Server first.' }
    # PowerShell unwraps a one-item pipeline into a scalar string. Indexing that
    # scalar directly returns its first CHARACTER ("C" on a hosted Windows
    # runner) instead of the full executable path. Force array semantics so a
    # clean checkout with only the Release build behaves like a developer tree
    # that happens to contain both Release and Debug outputs.
    $ServerExe = @($candidates)[0]
}

# ---- ask the server what it serves -----------------------------------------
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $ServerExe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$inventoryDataRoot = Join-Path ([IO.Path]::GetTempPath()) ('horizun-inventory-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $inventoryDataRoot | Out-Null
$inventorySettings = [ordered]@{
    permission_profile = 'unsafe_code'
    enable_execute_python = $true
}
[IO.File]::WriteAllText(
    (Join-Path $inventoryDataRoot 'settings.json'),
    (($inventorySettings | ConvertTo-Json -Compress) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))
# Inventory measures the complete product surface, not whichever subset this
# Windows user selected for today's modelling session. Isolate BOTH permission
# profile and tool packs in the child process; never read or rewrite the owner's
# settings. Omitting tool_packs means the documented default: every pack.
$psi.EnvironmentVariables['HORIZUN_DATA_ROOT'] = $inventoryDataRoot
$psi.EnvironmentVariables['HORIZUN_TOOL_PACKS'] = 'all'

$proc = $null
$listed = $null
$identity = $null
try {
    $proc = [Diagnostics.Process]::Start($psi)
    function Send-Line($o) { $proc.StandardInput.WriteLine(($o | ConvertTo-Json -Depth 24 -Compress)); $proc.StandardInput.Flush() }
    function Recv-Line([int]$TimeoutMs = 30000) {
        $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
        while ($true) {
            $t = $proc.StandardOutput.ReadLineAsync()
            $remaining = [Math]::Max(1, [int](($deadline - (Get-Date)).TotalMilliseconds))
            $winner = [Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]@($t, [Threading.Tasks.Task]::Delay($remaining))).Result
            if (-not [object]::ReferenceEquals($winner, $t)) { return $null }
            if (-not $t.Result) { return $null }
            try { $m = $t.Result | ConvertFrom-Json } catch { continue }
            if ($m.id) { return $m }
        }
    }
    Send-Line @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='inventory'; version='1' } } }
    $init = Recv-Line
    if (-not $init) { throw 'the server did not answer initialize' }
    Send-Line @{ jsonrpc='2.0'; method='notifications/initialized' }
    Send-Line @{ jsonrpc='2.0'; id=2; method='tools/list'; params=@{} }
    $listed = Recv-Line
    # THE CONTRACT HASH FROM THE SAME SERVER, in the same conversation. Reading it
    # from the source tree would prove nothing about the binary that just listed
    # these tools - and the binary is the thing this file is a measurement of.
    Send-Line @{ jsonrpc='2.0'; id=3; method='resources/read'; params=@{ uri='horizun://build/identity' } }
    $identity = Recv-Line
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(10000)) { $proc.Kill() }
} finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch { } }
    if (Test-Path -LiteralPath $inventoryDataRoot) {
        Remove-Item -LiteralPath $inventoryDataRoot -Recurse -Force
    }
}
if (-not $listed) { throw 'the server did not answer tools/list' }
$contractHash = $null
try {
    $text = @($identity.result.contents)[0].text
    $contractHash = ($text | ConvertFrom-Json).contract_hash
} catch { $contractHash = $null }
$tools = @($listed.result.tools)
if ($tools.Count -eq 0) { throw 'tools/list came back empty' }

# ---- walk every schema for enum-valued properties ---------------------------
function Walk-Schema($node, $path, [System.Collections.ArrayList]$acc) {
    if ($null -eq $node) { return }
    if ($node -is [pscustomobject]) {
        foreach ($p in $node.PSObject.Properties) {
            $childPath = if ($p.Name -eq 'properties' -or $p.Name -eq 'items') { $path } else { ($path + '.' + $p.Name).TrimStart('.') }
            if ($p.Value -is [pscustomobject]) {
                $enumValues = $null
                if ($p.Value.PSObject.Properties.Name -contains 'enum') { $enumValues = @($p.Value.enum) }
                elseif ($p.Value.PSObject.Properties.Name -contains 'items' -and
                        $p.Value.items -is [pscustomobject] -and
                        $p.Value.items.PSObject.Properties.Name -contains 'enum') { $enumValues = @($p.Value.items.enum) }
                if ($enumValues) {
                    $null = $acc.Add([pscustomobject]@{ property = $p.Name; path = $childPath; values = $enumValues })
                }
            }
            Walk-Schema $p.Value $childPath $acc
        }
    } elseif ($node -is [System.Collections.IEnumerable] -and $node -isnot [string]) {
        foreach ($it in $node) { Walk-Schema $it $path $acc }
    }
}

# ---- does the command source actually READ that property? -------------------
$sourceText = New-Object Text.StringBuilder
foreach ($dir in @('src\Horizun.Revit\Commands', 'src\Horizun.Server')) {
    $full = Join-Path $repo $dir
    if (-not (Test-Path $full)) { continue }
    foreach ($f in (Get-ChildItem -Path $full -Filter *.cs -File -ErrorAction SilentlyContinue)) {
        $null = $sourceText.AppendLine([IO.File]::ReadAllText($f.FullName))
    }
}
$allSource = $sourceText.ToString()
function Test-DispatchRead([string]$prop) {
    $a = 'Value<string>("' + $prop + '")'
    $b = '["' + $prop + '"]'
    return $allSource.Contains($a) -or $allSource.Contains($b)
}

$rows = @()
$variantTotal = 0
$operationTotal = 0
$unverifiedSelectors = @()
foreach ($t in $tools) {
    $acc = [System.Collections.ArrayList]::new()
    Walk-Schema $t.inputSchema '' $acc
    $enums = @($acc)
    $variants = 0
    foreach ($e in $enums) { $variants += @($e.values).Count }
    $dispatch = @($enums | Where-Object { $DispatchSelectors -contains $_.property })
    $ops = 0
    $opDetail = @()
    foreach ($e in $dispatch) {
        $verified = Test-DispatchRead $e.property
        if (-not $verified) { $unverifiedSelectors += ("{0}.{1}" -f $t.name, $e.property) }
        $ops += @($e.values).Count
        $opDetail += [pscustomobject]@{ property = $e.property; path = $e.path
                                        values = @($e.values); source_read_confirmed = $verified }
    }
    $readOnly = $false
    $destructive = $false
    $openWorld = $false
    if ($t.PSObject.Properties.Name -contains 'annotations' -and $t.annotations) {
        $readOnly = [bool]$t.annotations.readOnlyHint
        $destructive = [bool]$t.annotations.destructiveHint
        $openWorld = [bool]$t.annotations.openWorldHint
    }
    $variantTotal += $variants
    $operationTotal += $ops
    $rows += [pscustomobject]@{
        tool = $t.name
        read_only = $readOnly
        destructive = $destructive
        open_world = $openWorld
        operations = $ops
        enumerated_variants = $variants
        operation_detail = $opDetail
    }
}

function Sanitize-Path([string]$Path) {
    if (-not $Path) { return $Path }
    $out = $Path
    $out = $out.Replace($repo, '<repo>')
    foreach ($root in @($env:LOCALAPPDATA, $env:APPDATA, $env:USERPROFILE)) {
        if ($root) { $out = $out.Replace($root, '<' + ($root | Split-Path -Leaf) + '>') }
    }
    if ($env:USERNAME) { $out = $out.Replace($env:USERNAME, '<user>') }
    $out
}

# ---- what was actually measured, and how it relates to the tree --------------
$serverSha = (Get-FileHash -LiteralPath $ServerExe -Algorithm SHA256).Hash.ToLowerInvariant()

# The binary's own stamp: "<version>+<40 hex sha>[-dirty]" in Win32 ProductVersion.
$candidateCommit = $null; $candidateVersion = $null; $candidateSource = 'unavailable'
try {
    $pv = (Get-Item -LiteralPath $ServerExe).VersionInfo.ProductVersion
    if ($pv -match '^(?<ver>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\+(?<sha>[0-9a-f]{40})(?<dirty>-dirty)?$') {
        $candidateVersion = $Matches['ver']
        $candidateCommit = $Matches['sha'] + $Matches['dirty']
        $candidateSource = 'product_version'
    }
} catch { }

$headNow = $null; $treeCleanNow = $null
try {
    $headNow = (& git -C $repo rev-parse HEAD).Trim()
    $treeCleanNow = (@(& git -C $repo status --porcelain --untracked-files=no).Count -eq 0)
} catch { }

# WHICH COMMITS SINCE THE CANDIDATE TOUCHED ONLY DOCUMENTATION.
#
# This is the whole answer to "why does HEAD not match the binary?". A commit
# that changed docs cannot change what the server serves; a commit that changed
# src/ or tests/ can, and that is reported as code_differs_from_candidate so
# nobody has to infer it from two hashes that merely look different.
$docsOnlyAfter = @()
$codeDiffers = $null
$bare = if ($candidateCommit) { $candidateCommit -replace '-dirty$', '' } else { $null }
if ($bare -and $headNow) {
    try {
        $known = (& git -C $repo cat-file -t $bare 2>$null)
        if ($known -eq 'commit') {
            $codeDiffers = $false
            foreach ($line in (& git -C $repo log --format='%H' "$bare..$headNow")) {
                $sha = $line.Trim()
                if (-not $sha) { continue }
                $files = @(& git -C $repo show --pretty=format: --name-only $sha | Where-Object { $_ })
                $touchesCode = @($files | Where-Object {
                    $_ -like 'src/*' -or $_ -like 'tests/*' -or $_ -like 'scripts/*' -or $_ -like '*.csproj' -or
                    $_ -like 'global.json' -or $_ -like 'Directory.Build.props' }).Count -gt 0
                if ($touchesCode) { $codeDiffers = $true }
                else {
                    $subject = (& git -C $repo log -1 --format='%s' $sha).Trim()
                    $docsOnlyAfter += ('{0} {1}' -f $sha.Substring(0, 12), $subject)
                }
            }
        }
    } catch { }
}

$inventory = [ordered]@{
    schema = 'horizun.inventory/1'
    generated_by = 'scripts/generate-inventory.ps1'
    generated_from = 'tools/list answered by the built server binary'
    # RELATIVE TO THE REPO, never the absolute path.
    #
    # This file is committed, and this repository has a public counterpart, so an
    # absolute path here publishes the home directory of whoever last ran the
    # generator. scan-sensitive.ps1 is what caught it; it had been committed three
    # times before that check ran. The sha256 below is what actually identifies
    # the binary, and it is machine-independent.
    # SANITISED AGAINST BOTH ROOTS. The repo path is not the only one that can
    # appear here: pointing the generator at the INSTALLED server - which is the
    # honest thing to do when measuring a deployed candidate - puts the user's
    # LOCALAPPDATA in this field instead. Both are replaced, and the home
    # directory last, so nothing personal survives either route.
    generated_from_server_exe = (Sanitize-Path $ServerExe)
    generated_from_server_sha = $serverSha
    generated_from_contract_hash = $contractHash
    measurement_profile = 'isolated data root; permission_profile=unsafe_code; execute_python enabled; all tool packs'

    # THE COMMIT OF THE CODE THAT WAS MEASURED, read off the binary's own stamp -
    # NOT git HEAD.
    #
    # This file is versioned, so committing it necessarily happens AFTER the
    # commit whose binary it measured, and a later docs-only commit moves HEAD
    # again. Recording HEAD made the inventory look stale against a candidate it
    # had measured perfectly well, and there is no way to write a file that
    # contains its own commit hash. The binary carries the answer, so the binary
    # is asked.
    code_candidate_commit = $candidateCommit
    code_candidate_version = $candidateVersion
    code_candidate_stamp_source = $candidateSource
    source_tree_clean_at_generation = $treeCleanNow
    source_tree_head_at_generation = $headNow
    docs_only_commits_after_candidate = $docsOnlyAfter
    code_differs_from_candidate = $codeDiffers
    provenance_means = @(
        'code_candidate_commit is read from the SERVER BINARY''s own version stamp, so it names the code',
        'that was measured no matter what HEAD has moved on to since.',
        'docs_only_commits_after_candidate lists the commits between that candidate and HEAD that touch',
        'documentation ONLY - they are why HEAD can differ from the candidate without the tools changing.',
        'code_differs_from_candidate is true when a commit after the candidate touched src/ or tests/:',
        'then this inventory describes a binary that is no longer the tree, and must be regenerated.'
    ) -join ' '
    counts = [ordered]@{
        tools = $rows.Count
        reads = @($rows | Where-Object { $_.read_only }).Count
        writes = @($rows | Where-Object { -not $_.read_only }).Count
        destructive = @($rows | Where-Object { $_.destructive }).Count
        operations = $operationTotal
        enumerated_variants = $variantTotal
    }
    definitions = [ordered]@{
        tools = 'MCP tool names the built server lists.'
        reads = 'tools annotated readOnlyHint - they cannot change the model.'
        operations = ('distinct values of the enum properties a command DISPATCHES on (' +
                      ($DispatchSelectors -join ', ') + '), each cross-checked against the command source.')
        enumerated_variants = 'every (tool, property, enum value) triple in every schema. An argument, NOT a proven behaviour.'
        not_measured_here = 'whether any of it works. Verified behaviour is counted by the live harness artifact only.'
    }
    dispatch_selectors_declared = $DispatchSelectors
    dispatch_selectors_without_source_read = @($unverifiedSelectors | Sort-Object -Unique)
    tools_detail = $rows
}

$json = $inventory | ConvertTo-Json -Depth 12
if ($Check) {
    if (-not (Test-Path $OutJson)) { Write-Host '[inventory] docs/inventory.json does not exist yet' -ForegroundColor Red; exit 2 }
    $existing = Get-Content $OutJson -Raw | ConvertFrom-Json
    $drift = @()
    foreach ($k in 'tools','reads','writes','operations','enumerated_variants') {
        if ([int]$existing.counts.$k -ne [int]$inventory.counts.$k) {
            $drift += ("{0}: recorded {1}, served {2}" -f $k, $existing.counts.$k, $inventory.counts.$k)
        }
    }
    if ($drift.Count) {
        Write-Host '[inventory] DRIFT between docs/inventory.json and the served surface:' -ForegroundColor Red
        $drift | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host '[inventory] docs/inventory.json matches the served surface.' -ForegroundColor Green
    exit 0
}

New-Item -ItemType Directory -Path (Split-Path -Parent $OutJson) -Force | Out-Null
[IO.File]::WriteAllText($OutJson, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

Write-Host ('[inventory] {0} tools ({1} read-only, {2} mutating, {3} destructive)' -f
    $inventory.counts.tools, $inventory.counts.reads, $inventory.counts.writes, $inventory.counts.destructive)
Write-Host ('[inventory] {0} dispatched operations, {1} enumerated variants' -f
    $inventory.counts.operations, $inventory.counts.enumerated_variants)
if ($unverifiedSelectors.Count) {
    Write-Host ('[inventory] {0} declared selector(s) NOT found being read in source: {1}' -f
        @($unverifiedSelectors | Sort-Object -Unique).Count, (@($unverifiedSelectors | Sort-Object -Unique) -join ', ')) -ForegroundColor Yellow
}
Write-Host ('[inventory] written to {0}' -f $OutJson)
