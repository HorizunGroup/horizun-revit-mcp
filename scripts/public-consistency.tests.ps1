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
if ($settings -notmatch 'return\s+"safe_write"') { Fail 'fresh-install permission profile is no longer safe_write' }
if ($settings -notmatch 'return\s+t\s*!=\s*null\s*&&\s*t\.Type\s*==\s*JTokenType\.Boolean\s*&&\s*\(bool\)t') { Fail 'enable_execute_python no longer defaults to false' }
if ($evidence -notmatch 'HostVerified\s*=>\s*false') { Fail 'Python evidence no longer pins HostVerified=false' }
if ($contract -notmatch 'Disabled by default' -or $contract -notmatch 'host_verified is always false') { Fail 'tool contract no longer states the Python decision/evidence ceiling' }
$defaultOnPattern = '(?i)execute_python.{0,80}(enabled by default|(?<!des)habilitado por defecto)|default-on change'
foreach ($policyFile in @('AGENTS.md','CHANGELOG.md','CONTRIBUTING.md','SECURITY.md','llms.txt','docs/security-model.md','scripts/verify-live.ps1','src/Horizun.Revit/Commands/ExecutePythonCommand.cs')) {
    $policyText = Get-Content (Join-Path $repo $policyFile) -Raw
    if ($policyText -match $defaultOnPattern) { Fail "$policyFile still describes execute_python as default-on" }
}

# The public CI must own the real Revit lifecycle and opt into the committing
# tier. A direct verify-live invocation against no running Revit was present for
# months and looked like integration coverage while being structurally unable to
# produce it.
$ci = Get-Content (Join-Path $repo '.github/workflows/ci.yml') -Raw
$runnerGate = Get-Content (Join-Path $repo 'scripts/run-release-live-gate.ps1') -Raw
if ($runnerGate -notmatch "ErrorActionPreference\s*=\s*'Continue'" -or
    $runnerGate -notmatch 'PermitFailure') {
    Fail 'the owned live runner cannot tolerate expected startup probe failures under Windows PowerShell 5.1'
}
$hzCall = Get-Content (Join-Path $repo 'scripts/hz-call.ps1') -Raw
if ($hzCall -notmatch 'ArgumentsPath' -or $runnerGate -notmatch '-ArgumentsPath') {
    Fail 'cross-process live calls do not transport JSON arguments through an exact UTF-8 file'
}
if ($runnerGate -match 'OpenAndActivateDocument' -or
    -not $runnerGate.Contains("'-ClosedWorksetDocument', `$activeReleaseTitle") -or
    -not $runnerGate.Contains("'-Document', `$activeReleaseTitle") -or
    $runnerGate -notmatch 'open_all_worksets\s*=\s*\$true') {
    Fail 'the owned runner bypasses the typed closed-workset open or hides its stable source path'
}
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
if ($ci -notmatch 'verify-release\.ps1 -AllowUnsigned -Installed -InstallResult') {
    Fail 'tag-only package CI no longer verifies the exact installed result under the explicit unsigned policy'
}
if ($ci -match 'signpath/|SIGNPATH_|SIGNING_CERT_THUMBPRINT|sign-payload:|sign-installer:' -or
    $ci -notmatch "authenticode = 'unsigned_by_policy'" -or
    $ci -notmatch 'publisher_identity_available = \$false' -or
    $ci -notmatch "Status -ne 'NotSigned'") {
    Fail 'permanent unsigned release boundary or its machine-readable disclosure has drifted'
}
foreach ($handoff in
    'needs.build-stage.outputs.payload-artifact-id',
    'needs.compile-installer.outputs.package-input-artifact-id',
    'needs.package.outputs.package-artifact-id') {
    if ($ci -notmatch [regex]::Escape($handoff)) { Fail "release artifact-id chain is missing $handoff" }
}
if ([regex]::Matches($ci, '(?m)^\s+artifact-ids:\s*\$\{\{').Count -ne
    [regex]::Matches($ci, '(?m)^\s+artifact-ids:\s*\$\{\{[^\r\n]+\r?\n\s+path:[^\r\n]+\r?\n\s+merge-multiple:\s*true\s*$').Count) {
    Fail 'an artifact-id hand-off can extract beneath an unverified artifact-name directory'
}
foreach ($selfHostedJob in 'build-stage','compile-installer') {
    $block = [regex]::Match($ci, "(?ms)^  ${selfHostedJob}:\s*\r?\n(?:(?!^  [a-zA-Z0-9_-]+:\s*$).)*").Value
    if (-not $block -or $block -match 'secrets\.|environment:\s*release-signing') {
        Fail "$selfHostedJob can receive release credentials or a protected secret environment"
    }
}
if ($ci -notmatch '(?s)public-integrity:.*?runs-on:\s*windows-latest.*?unsigned_by_policy' -or
    $ci -notmatch 'needs: \[package, public-integrity, stable-release-evidence\]') {
    Fail 'stable publication no longer proves the permanent unsigned integrity contract on a clean hosted runner'
}
if ($ci -notmatch '(?s)Create or complete the stable GitHub release.*?GH_REPO:\s*\$\{\{\s*github\.repository\s*\}\}.*?gh release') {
    Fail 'stable release publication can no longer identify the repository without a checkout'
}
if ($ci -notmatch '(?s)install-package:.*?startsWith\(github\.ref,\s*''refs/tags/v''\).*?verify-release\.ps1 -AllowUnsigned -Installed -InstallResult') {
    Fail 'installed verification is no longer tag-only and explicitly unsigned'
}
if ($ci -notmatch 'publish-preview-release:' -or $ci -notmatch '--prerelease' -or
    $ci -notmatch 'publish-validation-release:' -or
    $ci -notmatch "contains\(github\.ref_name, '-validation\.'\)" -or
    $ci -notmatch "!contains\(github\.ref_name, '-'\)") {
    Fail 'CI no longer implements distinct stable, preview and validation-only release channels'
}
if ($ci -notmatch 'python-permission\.tests\.ps1' -or
    $ci -notmatch 'validate-public-projection\.ps1 -Output dist/public/ci') {
    Fail 'CI no longer proves inter-process Python revocation and the exact exported public tree'
}
if (Test-Path (Join-Path $repo 'publish/make-public-package.ps1')) {
    $projector = Get-Content (Join-Path $repo 'publish/make-public-package.ps1') -Raw
    foreach ($legalFile in 'LICENSE','NOTICE') {
        if ($projector -notmatch "(?m)'$legalFile'") {
            Fail "the public projection omits required legal file $legalFile"
        }
    }
}

$readme = Get-Content (Join-Path $repo 'README.md') -Raw
if ($readme -notmatch '\[scriptblock\]::Create\(\$s\)\) -AllowUnsigned' -or
    $readme -notmatch 'raw\.githubusercontent\.com/HorizunGroup/horizun-revit-mcp/main/install-release\.ps1') {
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
if ($readme -notmatch 'CODE-SIGNING-POLICY\.md' -or $readme -notmatch 'intentionally[\s>]*\*\*unsigned\*\*' -or
    $readme -notmatch 'do not authenticate a Windows[\s>]+publisher') {
    Fail 'README no longer exposes the permanent unsigned trust boundary'
}
if ($signingPolicy -notmatch 'including version 1\.0' -or
    $signingPolicy -notmatch 'authenticode: unsigned_by_policy' -or
    $signingPolicy -notmatch 'publisher_identity_available: false' -or
    $signingPolicy -notmatch 'must never describe an invalid, expired, privately trusted or\s+self-signed signature as public trust' -or
    $signingPolicy -notmatch 'docs/PRIVACY\.md') {
    Fail 'unsigned release policy no longer states its trust, integrity and privacy boundaries'
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
    $installerSource -notmatch 'if WriteDeploymentManifests then' -or
    $installerSource -notmatch 'if VerifyInstalledPayload then CommitDeployment' -or
    $installerSource -match 'Source: "\.\.\\dist\\stage\\manifest\.json"; DestDir: "\{app\}"') {
    Fail 'Setup no longer promotes server, add-ins and identity manifests as one transaction'
}

# Resolve every local link that will appear in the public repository. Anchors are
# deliberately ignored here; missing files are the high-cost publication defect.
$publicDocs = @(
    'README.md','AGENTS.md','CONTRIBUTING.md','SECURITY.md','LICENSE','NOTICE','THIRD-PARTY-NOTICES.md','llms.txt',
    'publish/overlay/README.md','publish/overlay/AGENTS.md',
    'CODE_OF_CONDUCT.md','CODE-SIGNING-POLICY.md',
    'docs/BENCHMARK.md','docs/FAMILY-AUTHORING.md','docs/HORIZUN-HUB.md','docs/PRIVACY.md','docs/production-readiness.md',
    'docs/RELEASE-POLICY.md','docs/SIGNPATH-ONBOARDING.md','docs/requirement-set.md','docs/security-model.md','docs/TOOLS.md'
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
