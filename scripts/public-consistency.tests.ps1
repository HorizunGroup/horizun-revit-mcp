#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Fail([string]$message) { $failures.Add($message) | Out-Null }

$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { Fail "invalid canonical Version '$version'" }

try { & (Join-Path $repo 'scripts/version-consistency.tests.ps1') }
catch { Fail "effective product version check failed: $($_.Exception.Message)" }

# The registry file is generated rather than checked in so its version cannot
# drift. Exercise the generator here: a syntactically valid script that writes
# stale or misnamed metadata is still a broken release path.
$registryTemp = Join-Path ([IO.Path]::GetTempPath()) ("horizun-mcp-registry-{0}.json" -f [guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $repo 'scripts/generate-mcp-manifest.ps1') -OutFile $registryTemp
    if (-not (Test-Path $registryTemp)) { Fail 'registry manifest generator did not produce a file' }
    else {
        $registryBytes = [IO.File]::ReadAllBytes($registryTemp)
        if ($registryBytes.Length -ge 3 -and $registryBytes[0] -eq 0xEF -and $registryBytes[1] -eq 0xBB -and $registryBytes[2] -eq 0xBF) {
            Fail 'registry manifest has a UTF-8 BOM, which mcp-publisher rejects'
        }
        try { $registry = Get-Content $registryTemp -Raw | ConvertFrom-Json }
        catch { Fail "registry manifest is not JSON: $($_.Exception.Message)"; $registry = $null }
        if ($registry) {
            if ($registry.version -ne $version) { Fail "registry version '$($registry.version)' differs from canonical '$version'" }
            if ($registry.name -ne 'io.github.HorizunGroup/horizun-revit-mcp') { Fail "registry name is wrong: '$($registry.name)'" }
            if ($registry.'$schema' -ne 'https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json') { Fail "registry schema is not the supported pinned schema: '$($registry.'$schema')'" }
            if ($registry.repository.url -ne 'https://github.com/HorizunGroup/horizun-revit-mcp') { Fail "registry repository URL is wrong: '$($registry.repository.url)'" }
        }
    }
}
finally { Remove-Item -LiteralPath $registryTemp -Force -ErrorAction SilentlyContinue }

foreach ($project in 'src/Horizun.Server/Horizun.Server.csproj','src/Horizun.Revit/Horizun.Revit.csproj') {
    $text = Get-Content (Join-Path $repo $project) -Raw
    if ($text -match '<Version>') { Fail "$project declares a second product Version" }
}

$installDocs = @('README.md','AGENTS.md','publish/overlay/README.md','publish/overlay/AGENTS.md','install.ps1','installer/horizun-mcp.iss')
foreach ($path in $installDocs) {
    $text = Get-Content (Join-Path $repo $path) -Raw
    foreach ($match in [regex]::Matches($text, '(?m)^.*claude mcp add.*$')) {
        if ($match.Value -notmatch '--scope\s+user') { Fail "$path has a project-local Claude registration command: $($match.Value.Trim())" }
        if ($match.Value -notmatch 'horizun-revit\s+--\s+') { Fail "$path has a malformed Claude stdio separator: $($match.Value.Trim())" }
    }
    foreach ($match in [regex]::Matches($text, '(?m)^.*codex mcp add.*$')) {
        if ($match.Value -notmatch 'horizun-revit\s+--\s+') { Fail "$path has a malformed Codex stdio separator: $($match.Value.Trim())" }
    }
    if ($text -match '-Version\s+v\d+\.\d+\.\d+') { Fail "$path pins a release number that will drift" }
}

$settings = Get-Content (Join-Path $repo 'src/Horizun.Revit/Core/Settings.cs') -Raw
$evidence = Get-Content (Join-Path $repo 'src/Horizun.Revit/Core/ScriptEvidence.cs') -Raw
$contract = Get-Content (Join-Path $repo 'src/Horizun.Contracts/Contract.cs') -Raw
if ($settings -notmatch 'return\s+"unsafe_code"') { Fail 'execute_python default profile is no longer unsafe_code' }
if ($settings -notmatch 'if\s*\(t\s*==\s*null\s*\|\|\s*t\.Type\s*!=\s*JTokenType\.Boolean\)\s*return\s+true') { Fail 'enable_execute_python no longer defaults to true' }
if ($evidence -notmatch 'HostVerified\s*=>\s*false') { Fail 'Python evidence no longer pins HostVerified=false' }
if ($contract -notmatch 'Enabled by default' -or $contract -notmatch 'host_verified is always false') { Fail 'tool contract no longer states the Python decision/evidence ceiling' }

# Resolve every local link that will appear in the public repository. Anchors are
# deliberately ignored here; missing files are the high-cost publication defect.
$publicDocs = @(
    'README.md','AGENTS.md','CONTRIBUTING.md','SECURITY.md','THIRD-PARTY-NOTICES.md','llms.txt',
    'publish/overlay/README.md','publish/overlay/AGENTS.md',
    'docs/BENCHMARK.md','docs/FAMILY-AUTHORING.md','docs/HORIZUN-HUB.md',
    'docs/RELEASE-POLICY.md','docs/requirement-set.md','docs/security-model.md'
)
foreach ($path in $publicDocs) {
    $full = Join-Path $repo $path
    if (-not (Test-Path $full)) { Fail "public document is missing: $path"; continue }
    $base = if ($path.StartsWith('publish/overlay/')) { $repo } else { Split-Path -Parent $full }
    $text = Get-Content $full -Raw
    foreach ($match in [regex]::Matches($text, '\]\((?!https?://|mailto:|#)([^)#]+)(?:#[^)]+)?\)')) {
        $target = [Uri]::UnescapeDataString($match.Groups[1].Value).Replace('/','\')
        if ($target -match '^<.*>$') { continue }
        if (-not (Test-Path (Join-Path $base $target))) { Fail "$path links to missing '$target'" }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "[FAIL] $_" -ForegroundColor Red }
    throw "$($failures.Count) public consistency failure(s)"
}
Write-Host "[PASS] one version source, durable CLI scope, Python default/evidence decision, and public local links" -ForegroundColor Green
