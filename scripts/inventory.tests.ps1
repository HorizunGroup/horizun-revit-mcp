#Requires -Version 5.1
<#
  The inventory must be GENERATED and must MATCH what the server serves.

  This guards the failure that made a hand-counted number worth distrusting:
  a headline figure in a document drifting away from the surface it describes,
  with nobody able to reproduce either. The generator answers from the built
  binary; this test re-runs it and refuses a mismatch.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$failures = 0
function Check($name, [scriptblock]$body) {
    try {
        $problem = & $body
        if ($problem) { Write-Host "  FAIL  $name - $problem" -ForegroundColor Red; $script:failures++ }
        else { Write-Host "  PASS  $name" -ForegroundColor Green }
    } catch {
        Write-Host "  FAIL  $name - $($_.Exception.Message)" -ForegroundColor Red; $script:failures++
    }
}

$inventoryPath = Join-Path $repo 'docs\inventory.json'

Check 'the generator exists and is referenced by name from the docs it feeds' {
    if (-not (Test-Path (Join-Path $repo 'scripts\generate-inventory.ps1'))) { return 'scripts/generate-inventory.ps1 is missing' }
    $toolsDoc = Get-Content (Join-Path $repo 'docs\TOOLS.md') -Raw
    if ($toolsDoc -notmatch 'generate-inventory') { return 'docs/TOOLS.md does not point at the generator that produces its numbers' }
    $null
}

Check 'a clean checkout with one server binary keeps the complete executable path' {
    $generator = Get-Content (Join-Path $repo 'scripts\generate-inventory.ps1') -Raw
    if ($generator -notmatch '\$ServerExe\s*=\s*@\(\$candidates\)\[0\]') {
        return 'the one-item candidate pipeline can unwrap to a string and indexing it would execute only its first character'
    }
    $null
}

Check 'docs/inventory.json exists and declares what each count means' {
    if (-not (Test-Path $inventoryPath)) { return 'docs/inventory.json is missing - run scripts/generate-inventory.ps1' }
    $inv = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    foreach ($k in 'tools','reads','writes','operations','enumerated_variants') {
        if ($null -eq $inv.counts.$k) { return "counts.$k is absent" }
    }
    foreach ($k in 'tools','reads','operations','enumerated_variants','not_measured_here') {
        if (-not $inv.definitions.$k) { return "definitions.$k is absent - a number without its definition is not reproducible" }
    }
    if ($inv.measurement_profile -notmatch 'isolated data root.*unsafe_code.*all tool packs') {
        return 'measurement_profile does not disclose the isolated complete-surface profile'
    }
    $null
}

Check 'every declared dispatch selector is actually read by the command source' {
    $inv = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    $unread = @($inv.dispatch_selectors_without_source_read)
    if ($unread.Count -gt 0) {
        return "declared selectors nothing reads: $($unread -join ', ') - either the code stopped dispatching on them or the declaration is wrong"
    }
    $null
}

Check 'the recorded counts still match what the server serves' {
    # Prove the generator does NOT inherit a restrictive owner/session profile.
    # A clean hosted runner is safe_write by default; a modeller can be even more
    # restrictive. The product inventory must still measure the complete surface
    # in its own temporary data root without editing either configuration.
    $probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('horizun-inventory-parent-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $probeRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $probeRoot 'settings.json'),
        '{"permission_profile":"read_only","tool_packs":["core"]}',
        [Text.UTF8Encoding]::new($false))
    $priorRoot = $env:HORIZUN_DATA_ROOT
    try {
        $env:HORIZUN_DATA_ROOT = $probeRoot
        # -Check re-asks the built binary and exits 1 on drift, 2 when absent.
        & pwsh -NoProfile -File (Join-Path $repo 'scripts\generate-inventory.ps1') -Check *> $null
        $exit = $LASTEXITCODE
    } finally {
        $env:HORIZUN_DATA_ROOT = $priorRoot
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
    if ($exit -eq 0) { return $null }
    if ($exit -eq 2) { return 'docs/inventory.json is missing' }
    return 'docs/inventory.json no longer matches the isolated complete surface - re-run scripts/generate-inventory.ps1'
}

Check 'every inventory-marked number in the docs matches the generated inventory' {
    # A MARKER, not a heuristic. A document that states an inventory number
    # annotates it - **67 tools** <!--inventory:tools--> - and this test checks
    # the marked ones only. Numbers quoted about OTHER products, and per-tool
    # counts like "24 operations" on one command, are not surface claims and are
    # deliberately out of scope: guessing which is which was the false-positive
    # machine this replaces.
    $inv = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    $offenders = @()
    $marked = 0
    foreach ($doc in (Get-ChildItem -Path (Join-Path $repo 'docs') -Filter *.md -File) +
                     @(Get-Item (Join-Path $repo 'README.md') -ErrorAction SilentlyContinue) +
                     @(Get-Item (Join-Path $repo 'README.es.md') -ErrorAction SilentlyContinue)) {
        if (-not $doc) { continue }
        $text = Get-Content $doc.FullName -Raw
        foreach ($m in [regex]::Matches($text, '\*\*([\d,]+)[^*]*\*\*\s*<!--inventory:([a-z_]+)-->')) {
            $marked++
            $stated = [int]($m.Groups[1].Value -replace ',', '')
            $key = $m.Groups[2].Value
            $actual = $inv.counts.$key
            if ($null -eq $actual) { $offenders += "$($doc.Name) marks unknown counter '$key'"; continue }
            if ($stated -ne [int]$actual) { $offenders += "$($doc.Name) states $stated for '$key' but the inventory says $actual" }
        }
    }
    if ($marked -eq 0) { return 'no document carries an inventory marker - the generated numbers reach nobody' }
    if ($offenders.Count) { return ($offenders -join '; ') }
    Write-Host "        ($marked marked number(s) checked)" -ForegroundColor DarkGray
    $null
}

Check 'suboperation counts deduplicate repeated schema paths' {
    $inv = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    $total = 0
    foreach ($tool in $inv.tools_detail) {
        $unique = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($selector in $tool.operation_detail) {
            foreach ($value in $selector.values) { $null = $unique.Add($selector.property + ':' + $value) }
        }
        if ($tool.operations -ne $unique.Count) { return "$($tool.tool) double-counts a selector value or omits one" }
        $total += $unique.Count
    }
    if ($inv.counts.operations -ne $total) { return 'the headline suboperation count differs from the distinct per-tool choices' }
    $null
}

Check 'both READMEs list every tool and suboperation exactly once in their catalog blocks' {
    $inv = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    $catalog = Get-Content (Join-Path $repo 'docs/readme-catalog.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $catalogNames = @($catalog.groups | ForEach-Object { $_.tools | ForEach-Object { $_[0] } })
    $expectedNames = @($inv.tools_detail.tool | Sort-Object)
    if ($catalogNames.Count -ne $expectedNames.Count -or
        (($catalogNames | Sort-Object) -join '|') -cne ($expectedNames -join '|')) { return 'the bilingual source catalog differs from tools/list' }
    $expectedOps = @{}
    foreach ($tool in $inv.tools_detail) {
        foreach ($selector in $tool.operation_detail) {
            foreach ($value in $selector.values) { $expectedOps[$tool.tool + '|' + $selector.property + '|' + $value] = $true }
        }
    }
    foreach ($name in @('README.md','README.es.md')) {
        $text = Get-Content (Join-Path $repo $name) -Raw -Encoding UTF8
        $block = [regex]::Match($text, '(?s)<!-- BEGIN TOOL CATALOG -->(.*?)<!-- END TOOL CATALOG -->')
        $actualNames = @([regex]::Matches($block.Groups[1].Value, '(?m)^\| `([^`]+)` \|') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        if (($actualNames -join '|') -cne ($expectedNames -join '|')) { return "$name omits, duplicates or invents a tool" }
        $opsBlock = [regex]::Match($text, '(?s)<!-- BEGIN SUBOPERATIONS -->(.*?)<!-- END SUBOPERATIONS -->')
        $actualOps = @{}
        foreach ($row in [regex]::Matches($opsBlock.Groups[1].Value, '(?m)^\| `([^`]+)` \| `([^`]+)` \| ([^\r\n]+) \|\r?$')) {
            foreach ($value in [regex]::Matches($row.Groups[3].Value, '`([^`]+)`')) {
                $key = $row.Groups[1].Value + '|' + $row.Groups[2].Value + '|' + $value.Groups[1].Value
                if ($actualOps.ContainsKey($key)) { return "$name repeats suboperation $key" }
                $actualOps[$key] = $true
            }
        }
        if ((($actualOps.Keys | Sort-Object) -join '|') -cne (($expectedOps.Keys | Sort-Object) -join '|')) { return "$name suboperations differ from the server inventory" }
    }
    $null
}

Check 'rendered bilingual descriptions match their maintained catalog' {
    & (Join-Path $PSScriptRoot 'update-readme-catalog.ps1') -Check
    $null
}

Write-Host ''
if ($failures -gt 0) { Write-Host "inventory tests: $failures FAILED" -ForegroundColor Red; exit 1 }
Check 'the inventory says WHICH CODE it measured, and how that relates to HEAD' {
    $inv = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    foreach ($k in 'generated_from_server_sha','generated_from_contract_hash','code_candidate_commit',
                   'code_candidate_stamp_source','source_tree_head_at_generation',
                   'source_tree_clean_at_generation','code_differs_from_candidate') {
        if ($null -eq $inv.$k) { return "$k is absent - without it nobody can tell whether this file describes the current code" }
    }
    if ($inv.code_candidate_stamp_source -ne 'product_version') {
        return "code_candidate_commit was not read from the binary's own stamp (source: $($inv.code_candidate_stamp_source)); a hash from anywhere else does not name what was measured"
    }
    if ($inv.code_candidate_commit -notmatch '^[0-9a-f]{40}(-dirty)?$') {
        return "code_candidate_commit '$($inv.code_candidate_commit)' is not a commit sha"
    }
    # THE POINT OF THE FILE: it may lag HEAD by DOCUMENTATION, never by code.
    if ($inv.code_differs_from_candidate) {
        return 'a commit after the measured candidate touched src/, tests/ or scripts/, so this inventory describes a binary that is no longer the tree - regenerate it'
    }
    $null
}

Check 'the inventory publishes no personal path' {
    $text = Get-Content $inventoryPath -Raw
    $needles = New-Object System.Collections.ArrayList
    if ($env:USERNAME) { $null = $needles.Add($env:USERNAME) }
    if ($env:USERPROFILE) { $null = $needles.Add($env:USERPROFILE) }
    $null = $needles.Add('C:' + [char]92 + 'Users' + [char]92)
    foreach ($needle in $needles) {
        if ($text.Contains($needle)) {
            return "docs/inventory.json contains '$needle' - this file is committed, and the repository has a public counterpart"
        }
    }
    $null
}

if ($failures -gt 0) { Write-Host "inventory tests: $failures FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'inventory tests: ALL PASS' -ForegroundColor Green
