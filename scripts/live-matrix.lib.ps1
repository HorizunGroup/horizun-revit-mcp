#Requires -Version 5.1
# Read-only validation of evidence; never starts Revit or changes fixtures.
function Get-HorizunLiveMatrix {
    param(
        [Parameter(Mandatory = $true)][string]$Reports,
        [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit,
        [int[]]$Years = @(2023,2024,2025,2026,2027)
    )
    if ($Years.Count -eq 0 -or @($Years | Select-Object -Unique).Count -ne $Years.Count) {
        throw 'Years must be non-empty and unique.'
    }
    $rows = @()
    $coverage = @{}
    $serverHashes = @()
    foreach ($year in $Years) {
        $issues = New-Object 'System.Collections.Generic.List[string]'
        $path = Join-Path $Reports "live-$year.json"
        $doc = $null
        $counts = @{ pass = 0; fail = 0; unverified = 0; not_covered = 0 }
        try {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'report missing' }
            $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            # verify-live.ps1 has written schema 2 since 1.0.0 (it adds sections such as
            # iso19650 and keeps every field read below); refusing it made this
            # consolidator reject every real report.
            if ($doc.schema -ne 1 -and $doc.schema -ne 2) { $issues.Add('unsupported report schema') }
            if ($doc.revit_year -ne $year) { $issues.Add('report belongs to a different Revit year') }
            if ($doc.release_gate -isnot [bool] -or $doc.release_gate -ne $true) {
                $issues.Add('release_gate must be true')
            }
            if ($doc.expected_commit -ne $ExpectedCommit) { $issues.Add('report belongs to a different commit') }
            if ($doc.server_is_dev_build -isnot [bool] -or $doc.server_is_dev_build -ne $false) {
                $issues.Add('installed server evidence required')
            }
            if ([string]$doc.server_sha256 -notmatch '^[a-fA-F0-9]{64}$') {
                $issues.Add('server SHA-256 missing or malformed')
            } else { $serverHashes += ([string]$doc.server_sha256).ToLowerInvariant() }
            if ($doc.write_tier.requested -isnot [bool] -or $doc.write_tier.requested -ne $true -or
                $doc.write_tier.probes -le 0 -or $doc.write_tier.gate) {
                $issues.Add('write tier did not run')
            }
            $seen = @{}
            if ($null -eq $doc.probes -or @($doc.probes).Count -eq 0) { $issues.Add('no probe results') }
            foreach ($probe in $doc.probes) {
                if ([string]::IsNullOrWhiteSpace([string]$probe.name) -or
                    [string]::IsNullOrWhiteSpace([string]$probe.tool)) {
                    $issues.Add('probe identity missing')
                    continue
                }
                $key = "{0}`n{1}" -f $probe.tool, $probe.name
                if ($seen.ContainsKey($key)) { $issues.Add("duplicate probe: $($probe.name)") }
                $seen[$key] = $true
                if (-not $coverage.ContainsKey($key)) {
                    $coverage[$key] = [pscustomobject]@{ tool = $probe.tool; name = $probe.name; years = @{} }
                }
                $coverage[$key].years["$year"] = [string]$probe.outcome
                if (-not $counts.ContainsKey([string]$probe.outcome)) {
                    $issues.Add("unknown outcome: $($probe.outcome)")
                } else { $counts[[string]$probe.outcome]++ }
            }
            foreach ($pair in @(@('passed','pass'), @('failed','fail'), @('unverified','unverified'))) {
                $value = $doc.summary.($pair[0])
                if ($null -eq $value -or $value -ne $counts[$pair[1]]) {
                    $issues.Add("summary.$($pair[0]) disagrees with probe results")
                }
            }
            # summary.probes counts planned probes; passed also includes two binary
            # checks. They intentionally differ. Compare outcome counters instead.
            if ($doc.summary.not_covered -ne 0 -or @($doc.not_covered).Count -ne 0 -or
                $counts.not_covered -gt 0) { $issues.Add('coverage gaps remain') }
            if ($counts.fail -gt 0 -or $counts.unverified -gt 0) { $issues.Add('non-passing probes remain') }
            if ($counts.pass -eq 0) { $issues.Add('no passing probes') }
            foreach ($binary in @('horizun-mcp.exe', 'Horizun.Revit.dll')) {
                if (@($doc.probes | Where-Object { $_.tool -eq $binary -and $_.outcome -eq 'pass' }).Count -ne 1) {
                    $issues.Add("missing unique binary verification: $binary")
                }
            }
        } catch { $issues.Add($_.Exception.Message) }
        $rows += [pscustomobject]@{
            year = $year; status = $(if ($issues.Count -eq 0) { 'pass' } else { 'incomplete' })
            generated_utc = $doc.generated_utc; passed = $counts.pass
            failed = $counts.fail; unverified = $counts.unverified; not_covered = $counts.not_covered
            issues = @($issues.ToArray())
        }
    }
    $matrixIssues = @()
    if (@($serverHashes | Select-Object -Unique).Count -gt 1) {
        $matrixIssues += 'Revit years were tested against different server binaries'
    }
    # Missing probes stay visible, even if each individual report claims zero gaps.
    foreach ($entry in $coverage.Values) {
        foreach ($year in $Years) {
            if (-not $entry.years.ContainsKey("$year")) {
                $entry.years["$year"] = 'missing'
                $matrixIssues += "Revit ${year}: missing probe $($entry.tool) / $($entry.name)"
            }
        }
    }
    return [pscustomobject]@{
        schema = 1; expected_commit = $ExpectedCommit
        complete = (@($rows | Where-Object { $_.status -ne 'pass' }).Count -eq 0 -and $matrixIssues.Count -eq 0)
        versions = $rows; issues = $matrixIssues
        coverage = @($coverage.Values | Sort-Object tool,name)
    }
}
