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

# publish/ is the export machinery and does NOT travel to the public repository,
# where this same script runs in CI. A check that is meaningful only in the
# private tree must SKIP where the input cannot exist, not fail there - and it
# must not skip silently where the input should exist, which is why the overlay
# identity check below fails on a missing root twin rather than on a missing
# overlay.
$installDocs = @('README.md','AGENTS.md','publish/overlay/README.md','publish/overlay/AGENTS.md','install.ps1','installer/horizun-mcp.iss') |
    Where-Object { Test-Path (Join-Path $repo $_) }
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

# The public CI must own the real Revit lifecycle and opt into the committing
# tier. A direct verify-live invocation against no running Revit was present for
# months and looked like integration coverage while being structurally unable to
# produce it.
$ci = Get-Content (Join-Path $repo '.github/workflows/ci.yml') -Raw
$runnerGate = Get-Content (Join-Path $repo 'scripts/run-release-live-gate.ps1') -Raw
$hzCall = Get-Content (Join-Path $repo 'scripts/hz-call.ps1') -Raw
if ($ci -notmatch 'scan-sensitive\.tests\.ps1') { Fail 'CI no longer tests the narrow public-governance scanner exception' }
if ($ci -notmatch 'run-release-live-gate\.ps1') { Fail 'CI no longer invokes the owned Revit release lifecycle' }
if ($ci -notmatch '(?s)revit-integration:.*?max-parallel:\s*1.*?matrix:') { Fail 'the single interactive Revit integration matrix is no longer serialized' }
if ($ci -notmatch "'stage\.zip'\s*=\s*'dist/stage\.zip'") { Fail 'the package record no longer hashes the complete staged payload archive' }
if ($ci -notmatch '(?s)install-package:.*?Restore the complete staged payload and its build timestamps.*?Expand-Archive') { Fail 'the package install no longer restores the timestamp-preserving stage archive' }
if ($ci -notmatch '(?s)revit-integration:.*?needs:\s*install-package') { Fail 'live Revit jobs can run without the one proven package installation' }
if ([regex]::Matches($ci, '(?m)^\s*- name: Install the packaged artifact\s*$').Count -ne 1) { Fail 'the release workflow must install the immutable package exactly once' }
if ($ci -notmatch '(?s)name:\s*installed-release-chain.*?dist/stage/manifest\.json.*?revit-integration:.*?Get the proven install manifest') { Fail 'clean live jobs no longer receive the proven package manifest' }
if ($runnerGate -notmatch "'-ReleaseGate',\s*'-WriteProbes'") { Fail 'the release lifecycle no longer runs the committing write tier' }
if ($runnerGate -notmatch "Get-Process -Name Revit" -or $runnerGate -notmatch 'preexisting\.Count -gt 0') {
    Fail 'the release lifecycle no longer refuses a pre-existing user Revit session'
}
if ($runnerGate -match 'Stop-Process\s+-Name\s+Revit') { Fail 'the release lifecycle can kill arbitrary Revit processes' }
if ($hzCall -notmatch 'reply\.result\.structuredContent') { Fail 'hz-call no longer reads the machine payload before human Revit diagnostics' }
if ($ci -notmatch '\(\?m\)\^failed\[ \\t\]\*=\[ \\t\]\*\\S') {
    Fail 'CI install-result parsing can again read the line after an empty failed= as a failed year'
}
if ($ci -notmatch 'verify-release\.ps1 -Installed -AllowUnsigned -InstallResult') {
    Fail 'CI no longer verifies the exact per-run install result with an explicit signing policy'
}
if ($ci -notmatch 'SIGNING_CERT_THUMBPRINT' -or
    $ci -notmatch '\.Major -ge 1' -or
    $ci -notmatch 'Horizun 1\.0\+ release contains unsigned, invalid, self-signed or untimestamped') {
    Fail '1.0+ tags no longer fail closed on missing or non-public Authenticode signing'
}
if ($ci -notmatch 'runs-on: windows-latest' -or
    $ci -notmatch 'not publicly trusted on a clean Windows runner' -or
    $ci -notmatch 'needs: \[package, public-signature, stable-release-evidence\]') {
    Fail 'stable publication no longer proves public trust on a clean hosted Windows runner'
}
if ($ci -notmatch '(?s)Create or complete the stable GitHub release.*?GH_REPO:\s*\$\{\{\s*github\.repository\s*\}\}.*?gh release') {
    Fail 'stable release publication can no longer identify the repository without a checkout'
}
if ($ci -notmatch '(?s)requiresPublicSignature.*?\.Major -ge 1.*?verify-release\.ps1 -Installed -InstallResult.*?else.*?-AllowUnsigned') {
    Fail 'installed release verification no longer allows unsigned only before 1.0'
}

$readme = Get-Content (Join-Path $repo 'README.md') -Raw
if ($readme -notmatch 'irm https://raw\.githubusercontent\.com/HorizunGroup/horizun-revit-mcp/main/install-release\.ps1 \| iex') {
    Fail 'the public README no longer offers the one-paste release installer'
}

# THE OVERLAY IS WHAT THE PUBLIC GETS. publish/overlay/* is laid on top of the
# exported tree after the allowlist copy, and README.md is not even in that
# allowlist - so a change made only at the root never reaches GitHub, and a
# stale overlay silently REVERTS the published file on the next export. Both
# README.md and AGENTS.md had drifted exactly that way: the root carried a
# rewrite and the brand-name rules the overlay had never seen, and publishing
# would have deleted them from the public repository. Byte-identical or fail.
$overlayDir = Join-Path $repo 'publish/overlay'
foreach ($overlayFile in @(if (Test-Path $overlayDir) { Get-ChildItem $overlayDir -File })) {
    $rootTwin = Join-Path $repo $overlayFile.Name
    if (-not (Test-Path $rootTwin)) {
        Fail "publish/overlay/$($overlayFile.Name) has no counterpart at the repository root"
        continue
    }
    $a = [IO.File]::ReadAllBytes($rootTwin)
    $b = [IO.File]::ReadAllBytes($overlayFile.FullName)
    if (-not [Linq.Enumerable]::SequenceEqual($a, $b)) {
        Fail "$($overlayFile.Name) and publish/overlay/$($overlayFile.Name) differ; the overlay is what gets published, so the two must be identical"
    }
}

$signingPolicy = Get-Content (Join-Path $repo 'CODE-SIGNING-POLICY.md') -Raw
$privacyPolicy = Get-Content (Join-Path $repo 'docs/PRIVACY.md') -Raw
$codeowners = Get-Content (Join-Path $repo '.github/CODEOWNERS') -Raw
if ($readme -notmatch 'CODE-SIGNING-POLICY\.md' -or $readme -notmatch 'Free code signing provided by SignPath\.io, certificate\s+by SignPath Foundation') {
    Fail 'README no longer exposes the required SignPath code-signing statement and policy'
}
if ($signingPolicy -notmatch 'application was submitted on 2026-08-15' -or
    $signingPolicy -notmatch 'Version 1\.0\.0 and every later release is blocked' -or
    $signingPolicy -notmatch 'GitHub-hosted runners' -or
    $signingPolicy -notmatch 'docs/PRIVACY\.md') {
    Fail 'code-signing policy no longer states its submitted status, trusted origin and privacy boundary'
}
if ($privacyPolicy -notmatch 'does not automatically upload' -or $privacyPolicy -notmatch 'horizun_power_bi_push' -or $privacyPolicy -notmatch 'horizun_execute_python') {
    Fail 'privacy policy no longer names the automatic and user-requested data boundaries'
}
if ($codeowners -notmatch '/\.github/workflows/' -or $codeowners -notmatch '/CODE-SIGNING-POLICY\.md') {
    Fail 'signing policy and workflows are no longer covered by CODEOWNERS'
}

$sourceInstaller = Get-Content (Join-Path $repo 'install.ps1') -Raw
$clientToolsCopy = [regex]::Escape("Copy-Item (Join-Path `$serverStage 'client-tools') `$installedClientTools -Recurse -Force")
if ($sourceInstaller -notmatch $clientToolsCopy -or
    $sourceInstaller -notmatch 'SourceInstall = \$true' -or
    $sourceInstaller -notmatch 'Move-Item -LiteralPath \$manifestTemp -Destination \$installedManifest') {
    Fail 'the source installer no longer installs the deferred helpers and their on-disk identity manifest transactionally'
}
$stopHelper = Get-Content (Join-Path $repo 'scripts\stop-installed-server.ps1') -Raw
if ($stopHelper -notmatch 'GetFullPath\(\$_\.ExecutablePath\).*?-ieq \$target' -or
    $stopHelper -notmatch 'Get-CimInstance Win32_Process -Filter "Name=''horizun-mcp\.exe''"' -or
    $stopHelper -match 'taskkill|Stop-Process -Name') {
    Fail 'the update helper no longer limits process termination to the exact installed server path'
}
$installerSource = Get-Content (Join-Path $repo 'installer\horizun-mcp.iss') -Raw
if ($installerSource -notmatch 'procedure RollbackDeployment' -or
    $installerSource -notmatch 'function WriteDeploymentManifests' -or
    $installerSource -notmatch 'if WriteDeploymentManifests then CommitDeployment' -or
    $installerSource -match 'Source: "\.\.\\dist\\stage\\manifest\.json"; DestDir: "\{app\}"') {
    Fail 'Setup no longer promotes server, add-ins and identity manifests as one transaction'
}

# Resolve every local link that will appear in the public repository. Anchors are
# deliberately ignored here; missing files are the high-cost publication defect.
$publicDocs = @(
    'README.md','AGENTS.md','CONTRIBUTING.md','SECURITY.md','THIRD-PARTY-NOTICES.md','llms.txt',
    'publish/overlay/README.md','publish/overlay/AGENTS.md',
    'CODE_OF_CONDUCT.md','CODE-SIGNING-POLICY.md',
    'docs/BENCHMARK.md','docs/FAMILY-AUTHORING.md','docs/HORIZUN-HUB.md','docs/PRIVACY.md',
    'docs/RELEASE-POLICY.md','docs/requirement-set.md','docs/security-model.md','docs/TOOLS.md'
) | Where-Object { -not ($_.StartsWith('publish/')) -or (Test-Path (Join-Path $repo 'publish/overlay')) }
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
