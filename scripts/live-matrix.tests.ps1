#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-matrix.lib.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hz-live-matrix-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$commit = 'a' * 40
$script:failed = 0
function Assert($name, $condition) {
    if ($condition) { Write-Host "PASS $name" }
    else { Write-Host "FAIL $name"; $script:failed++ }
}
function New-Report([int]$year) {
    return [pscustomobject]@{
        schema = 1; revit_year = $year; release_gate = $true; expected_commit = $commit
        server_is_dev_build = $false; server_sha256 = ('b' * 64)
        write_tier = @{ requested = $true; probes = 1; gate = $null }
        summary = @{ passed = 3; failed = 0; unverified = 0; not_covered = 0; probes = 1 }
        probes = @(
            @{ name = 'server binary'; tool = 'horizun-mcp.exe'; outcome = 'pass' },
            @{ name = 'addin binary'; tool = 'Horizun.Revit.dll'; outcome = 'pass' },
            @{ name = 'real write'; tool = 'horizun_create_elements'; outcome = 'pass' }
        )
        not_covered = @()
    }
}
function Save-Report($report) {
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot "live-$($report.revit_year).json") -Encoding UTF8
}
function Matrix { Get-HorizunLiveMatrix -Reports $testRoot -ExpectedCommit $commit -Years @(2025,2026) }
try {
    Save-Report (New-Report 2025)
    Save-Report (New-Report 2026)
    Assert 'accepts consistent reports, allowing separate binary checks' (Matrix).complete
    # verify-live.ps1 writes schema 2 (with its iso19650 block); the matrix must
    # accept exactly what the harness writes, or it rejects every real report.
    foreach ($y in 2025, 2026) {
        $r2 = New-Report $y
        $r2.schema = 2
        $r2 | Add-Member -NotePropertyName iso19650 -NotePropertyValue @{ evidence_file = 'iso19650.json'; cases = @() }
        $r2.probes += @{ name = 'shared->published without approved_by is refused and nothing is written'
                         tool = 'horizun_information_container'; outcome = 'pass' }
        $r2.summary.passed++
        Save-Report $r2
    }
    $m2 = Matrix
    Assert 'accepts the schema-2 reports verify-live writes, ISO rows included' ($m2.complete -and
        @($m2.coverage | Where-Object { $_.tool -eq 'horizun_information_container' }).Count -eq 1)
    Save-Report (New-Report 2025)
    Save-Report (New-Report 2026)
    $cases = @(
        @{ name = 'old commit'; change = { param($r) $r.expected_commit = 'c' * 40 } },
        @{ name = 'false green summary'; change = { param($r) $r.probes[2].outcome = 'fail' } },
        @{ name = 'empty results'; change = { param($r) $r.probes = @(); $r.summary.passed = 0 } },
        @{ name = 'skipped write tier'; change = { param($r) $r.write_tier.requested = $false } },
        @{ name = 'zero write probes'; change = { param($r) $r.write_tier.probes = 0 } },
        @{ name = 'blocked write tier'; change = { param($r) $r.write_tier.gate = 'missing fixture' } },
        @{ name = 'string boolean'; change = { param($r) $r.release_gate = 'true' } },
        @{ name = 'hidden gap'; change = { param($r) $r.not_covered = @('family not exercised') } },
        @{ name = 'missing summary counter'; change = { param($r) $r.summary.Remove('failed') } },
        @{ name = 'unknown outcome'; change = { param($r) $r.probes[2].outcome = 'green' } },
        @{ name = 'duplicate probe'; change = { param($r) $r.probes += $r.probes[2]; $r.summary.passed++ } },
        @{ name = 'missing binary proof'; change = { param($r) $r.probes = @($r.probes[0],$r.probes[2]); $r.summary.passed = 2 } },
        @{ name = 'different server bytes'; change = { param($r) $r.server_sha256 = 'c' * 64 } },
        @{ name = 'probe omitted in one year'; change = { param($r) $r.probes = @($r.probes[0],$r.probes[1]); $r.summary.passed = 2 } },
        @{ name = 'unknown report schema'; change = { param($r) $r.schema = 3 } },
        @{ name = 'an ISO 19650 case present in one year only'; change = { param($r)
            $r.probes += @{ name = 'export ifc with information_container names the file after the container and seals it (verify = match)'
                            tool = 'horizun_export'; outcome = 'pass' }
            $r.summary.passed++ } },
        @{ name = 'an ISO 19650 case not covered'; change = { param($r)
            $r.probes[2].outcome = 'not_covered'; $r.summary.passed--; $r.summary.not_covered = 1
            $r.not_covered = @('deliver_ifc: needs -WriteProbes') } }
    )
    foreach ($case in $cases) {
        $r = New-Report 2026
        & $case.change $r
        Save-Report $r
        Assert "rejects $($case.name)" (-not (Matrix).complete)
    }
    Set-Content -LiteralPath (Join-Path $testRoot 'live-2026.json') -Value '{bad json'
    Assert 'malformed report is a finding' (-not (Matrix).complete)
    $absent = Get-HorizunLiveMatrix -Reports $testRoot -ExpectedCommit $commit -Years @(2027)
    Assert 'missing version remains visible' (-not $absent.complete -and $absent.versions[0].year -eq 2027)
    Save-Report (New-Report 2026)
    $out = Join-Path $testRoot 'matrix.json'
    $md = Join-Path $testRoot 'matrix.md'
    # Exercise the CLI, not only the helper; a missing year must produce an
    # incomplete artifact AND a non-zero exit, rather than an exception only.
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-live-matrix.ps1') -Reports $testRoot -ExpectedCommit $commit -Json $out -Markdown $md
    Assert 'CLI fails on missing requested years' ($LASTEXITCODE -eq 1)
    Assert 'CLI writes machine-readable failure' ((Get-Content $out -Raw | ConvertFrom-Json).complete -eq $false)
    Assert 'CLI writes valid coverage table header' ((Get-Content $md -Raw) -match '(?m)^\| Tool / probe \| 2023 \| 2024 \| 2025 \| 2026 \| 2027 \|\r?$')
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'hz-live-matrix-*') { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failed -gt 0) { exit 1 }
exit 0
