#Requires -Version 7.0
<#
Read-only comparison on one active model. Calls the real installed stdio server.
No installation, model mutation, Python or configuration changes.
Client elapsed time includes hz-call process startup, transport and queue.
Command timings separately exclude these costs. No tokens or speedup fabricated.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [Parameter(Mandatory)][string]$ExpectedServerSha256,
    [Parameter(Mandatory)][string]$ExpectedAddinSha256,
    [Parameter(Mandatory)][ValidateRange(2023,2027)][int]$Year,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(5,100)][int]$Repetitions = 30,
    [string[]]$Categories = @('OST_Walls'),
    [switch]$LocalBuild
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve prior evidence.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output
$samples = [Collections.Generic.List[object]]::new()
$serial = 0
function Call([string]$Tool, [hashtable]$Arguments) {
    $script:serial++
    $stem = Join-Path $output ('{0:D4}' -f $script:serial)
    $Arguments | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$stem.args.json" -Encoding utf8
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'hz-call.ps1') -Tool $Tool -ArgumentsPath "$stem.args.json" -Server $Server -Json "$stem.json" -Quiet
    $exit = $LASTEXITCODE
    $watch.Stop()
    if ($exit -ne 0 -or -not (Test-Path -LiteralPath "$stem.json")) { throw "Call $serial failed; evidence remains in $output" }
    $answer = Get-Content -LiteralPath "$stem.json" -Raw | ConvertFrom-Json -AsHashtable
    if ($answer.is_error) { throw "Call $serial returned is_error." }
    return @{ data = $answer.result; elapsed_ms = $watch.Elapsed.TotalMilliseconds; evidence = "$stem.json" }
}
function Health {
    $h = (Call 'horizun_health' @{}).data
    $stamp = $ExpectedCommit + $(if ($LocalBuild) { '-dirty' } else { '' })
    if ($h.status -ne 'healthy' -or $h.horizun_commit -ne $stamp -or $h.revit_version -ne "$Year" -or
        $h.built_from_clean_tree -ne (-not $LocalBuild) -or
        @($h.open_documents | Where-Object { $_.is_active -and $_.title -eq $Document }).Count -ne 1) {
        throw 'Health did not verify the expected build and active document.'
    }
    return $h
}
function Percentile($values, [double]$p) {
    $ordered = @($values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    return $ordered[[int][Math]::Ceiling($ordered.Count * $p) - 1]
}
$failure = $null
try {
    $serverHash = (Get-FileHash -LiteralPath $Server -Algorithm SHA256).Hash
    $initialHealth = Health
    # Hash the assembly actually loaded by this Revit, including isolated dev
    # sessions. The conventional install directory may contain another build.
    $addin = $initialHealth.addin_assembly.path
    if ([string]::IsNullOrWhiteSpace($addin)) { throw 'Health did not identify the loaded add-in path.' }
    $addinHash = (Get-FileHash -LiteralPath $addin -Algorithm SHA256).Hash
    if ($serverHash -ne $ExpectedServerSha256 -or $addinHash -ne $ExpectedAddinSha256 -or
        $initialHealth.addin_assembly.sha256 -ne $ExpectedAddinSha256) { throw 'Binary SHA256 mismatch.' }
    $modes = @('full','compact','summary')
    $baseline = $null
    # Rotate order each round; retain every sample, including cold/warm state.
    for ($round = 0; $round -lt $Repetitions; $round++) {
        foreach ($offset in 0..2) {
            $mode = $modes[($round + $offset) % 3]
            $answer = Call 'horizun_query_model' @{categories=$Categories;include_links=$false;response_mode=$mode;cache_mode='bypass';include_diagnostics=$true;max_rows=100}
            $d = $answer.data
            if ($d.document -ne $Document -or $null -eq $d.query_diagnostics -or $d.coverage_complete -ne $true) { throw 'Wrong document, unsupported diagnostics or incomplete query coverage.' }
            if ($d.matched_total -lt 1) { throw 'Empty query population: choose categories present in the fixture; this is not a performance sample.' }
            # Ordered JSON summaries preserve canonical casing and ordering.
            $signature = ConvertTo-Json -InputObject ([ordered]@{total=$d.matched_total;summary=$d.summary}) -Depth 20 -Compress
            if ($null -eq $baseline) { $baseline = $signature }
            if ($signature -ne $baseline) { throw 'Summary/total changed across modes or the model changed during the benchmark.' }
            $samples.Add(@{round=$round;mode=$mode;cache=$d.query_diagnostics.cache;client_ms=$answer.elapsed_ms;
                command_ms=$d.query_diagnostics.command_ms;collect_ms=$d.query_diagnostics.collect_ms;
                bytes=$d.query_diagnostics.data_bytes_before_diagnostics;evidence=$answer.evidence})
        }
    }
    $finalHealth = Health
} catch { $failure = $_.Exception.Message }
$summary = @($samples | Group-Object mode | ForEach-Object {
    @{mode=$_.Name;samples=$_.Count;client_p50_ms=(Percentile $_.Group.client_ms .50);client_p95_ms=(Percentile $_.Group.client_ms .95);
      command_p50_ms=(Percentile $_.Group.command_ms .50);command_p95_ms=(Percentile $_.Group.command_ms .95);
      median_bytes=(Percentile $_.Group.bytes .50)}
})
@{schema='horizun.query-benchmark/1';utc=[DateTime]::UtcNow.ToString('o');outcome=$(if($failure){'failed'}else{'passed'});
  failure=$failure;commit=$ExpectedCommit;local_build=[bool]$LocalBuild;server_sha256=$serverHash;addin_sha256=$addinHash;
  categories=$Categories;initial_health=$initialHealth;final_health=$finalHealth;samples=$samples.ToArray();summary=$summary;
  method='nearest-rank; rotating mode order; bypass cache; first observations retained; same live model, no cross-product ranking';
  limitations='Full/compact return the first 100 rows, not full inventory export. No token, geometry, writing or Cortex score is measured.'
} | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $output 'benchmark.json') -Encoding utf8
if ($failure) { throw $failure }
Write-Host "Read-only benchmark completed: $output/benchmark.json"
