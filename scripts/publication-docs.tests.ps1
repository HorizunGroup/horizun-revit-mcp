#Requires -Version 5.1
<# Cross-check public installation claims against the shipped distribution. #>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$checks = 0
function Assert-Doc([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Read-Source([string]$Path) { Get-Content -LiteralPath (Join-Path $repo $Path) -Raw -Encoding UTF8 }

$installer = Read-Source 'installer/horizun-mcp.iss'
$clients = Read-Source 'docs/CLIENTS.md'
$pack = Read-Source 'scripts/pack.ps1'
$protocol = [regex]::Match((Read-Source 'src/Horizun.Server/ProtocolNegotiation.cs'), 'Latest\s*=\s*"([0-9-]+)"').Groups[1].Value
Assert-Doc ($protocol -match '^\d{4}-\d{2}-\d{2}$') 'Could not read the implemented MCP protocol revision.'

foreach ($path in @('README.md', 'README.es.md', 'llms.txt', 'docs/RELEASE-POLICY.md')) {
    $doc = Read-Source $path
    Assert-Doc ($doc.Contains($protocol)) "$path must state the protocol actually implemented in ProtocolNegotiation.cs."
}
foreach ($path in @('README.md', 'README.es.md', 'docs/CLIENTS.md', 'llms.txt')) {
    $doc = Read-Source $path
    Assert-Doc ($doc.Contains('Horizun-Revit-MCP') -and $doc.Contains('.mcpb')) "$path must identify the handed-over Claude Desktop extension."
    Assert-Doc ($doc -notmatch '(?i)60.minutes?|60.minutos|Stable release:\s*v\d') "$path contains an expired permission or a hard-coded stable-version claim."
}
Assert-Doc ($installer -match 'function HandOverDesktopPackage' -and $installer -match '\{userdocs\}') 'Recheck the Desktop instructions: Setup no longer hands the package to Documents.'
Assert-Doc ($clients -match 'Setup does not perform step 3' -and $clients -match 'Settings.*Extensions') 'The guide must identify the in-app Desktop installation step.'
Assert-Doc ($clients -match 'extension does not bundle the server') 'The registry extension must disclose its installed-server prerequisite.'
foreach ($path in @('README.md', 'README.es.md')) {
    $doc = Read-Source $path
    Assert-Doc ($doc.Contains('/releases/latest') -and $doc.Contains('Directory.Build.props') -and $doc.Contains('horizun_health')) "$path must distinguish stable, source and loaded versions."
    Assert-Doc ($doc -match '(?i)(No Git, Visual Studio or \.NET SDK|No necesitas Git, Visual Studio ni el SDK)') "$path must distinguish release installation from source-build prerequisites."
}

# A recovery command must run from the installed client-tools folder, and its
# target must actually be included by pack.ps1; a source-only path is not enough.
$helpers = [regex]::Matches($clients, "Join-Path \`$clientTools '([^']+\.ps1)'")
Assert-Doc ($helpers.Count -ge 5) 'No installed client recovery commands were found.'
foreach ($helper in $helpers) {
    $name = $helper.Groups[1].Value
    Assert-Doc ((Test-Path -LiteralPath (Join-Path $PSScriptRoot $name)) -and $pack.Contains($name)) "Documented recovery helper is not shipped: $name"
}
Assert-Doc ((Read-Source 'llms.txt') -match 'persistently[\s\S]*until that owner revokes') 'The LLM summary must describe the current durable Python grant.'
Assert-Doc ((Read-Source 'docs/BENCHMARK.md') -match '(?i)historical' -and (Read-Source 'docs/BENCHMARK.md') -match '(?is)not\s+a current-release certification or an independently') 'Historical design scores must retain their evidence scope.'
Assert-Doc ((Read-Source 'docs/production-readiness.md') -match '(?i)historical') 'Development checkpoints need their historical scope.'

# The README is also the capability-discovery surface used by people and
# catalogs. Installation guidance alone must not replace the actual product.
foreach ($path in @('README.md', 'README.es.md')) {
    $doc = Read-Source $path
    Assert-Doc ($doc.Contains('https://www.youtube.com/watch?v=tlFs5p3EM4M')) "$path omits the published workflow demonstration."
    Assert-Doc ($doc.Contains('docs/readme-catalog.json') -and $doc.Contains('<!-- BEGIN SUBOPERATIONS -->')) "$path omits the complete capability breakdown."
    Assert-Doc ($doc.Contains('docs/release-evidence.json') -and $doc.Contains('/stargazers')) "$path omits traceable release evidence or the live community metric."
    Assert-Doc ($doc.Contains('safe_write') -and $doc.Contains('full_write') -and $doc.Contains('unsafe_code')) "$path must distinguish session visibility from the complete catalog."
}
$evidence = Read-Source 'docs/release-evidence.json' | ConvertFrom-Json
Assert-Doc (@($evidence.reports).Count -eq 5) 'The release evidence index must identify all five reported Revit years.'
foreach ($report in $evidence.reports) {
    Assert-Doc ($report.sha256 -match '^[a-f0-9]{64}$' -and $report.url.EndsWith("/$($evidence.release)/live-$($report.revit_year).json")) 'Release evidence needs an exact versioned source URL and measured hash.'
    $s = $report.summary
    Assert-Doc ($s.probes -eq $s.passed + $s.failed + $s.unverified + $s.not_covered) 'Release evidence summary counts do not reconcile.'
    $row = "| $($report.revit_year) | $($s.passed) | $($s.failed) | $($s.unverified) | $($s.not_covered) | [JSON]($($report.url)) |"
    foreach ($path in @('README.md', 'README.es.md')) {
        Assert-Doc ((Read-Source $path).Contains($row)) "$path does not reproduce the indexed release evidence for $($report.revit_year)."
    }
}
Write-Host "publication documentation: PASS ($checks checks)"
