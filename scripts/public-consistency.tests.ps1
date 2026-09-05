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
#
# THE LIST IS THE EXPORTER'S, NOT A COPY OF IT. This test used to carry its own
# 21-document list, which omitted CHANGELOG.md, DWG-TO-BIM.md, DIMENSIONS.md and
# five other documents the projector exports - so a broken link in any of those
# was invisible here and surfaced only at publication. The allowlists are read
# from publish/make-public-package.ps1 itself. In the exported repository that
# private projector is deliberately absent; there the checked tree is already
# the projection, so the file set on disk is authoritative.
$projectorPath = Join-Path $repo 'publish\make-public-package.ps1'
$projector = if (Test-Path -LiteralPath $projectorPath) { Get-Content $projectorPath -Raw } else { $null }
function Read-AllowList([string]$name) {
    if (-not $projector) { return @() }
    $m = [regex]::Match($projector, "\`$$name\s*=\s*@\(([^)]*)\)")
    if (-not $m.Success) { Fail "publish/make-public-package.ps1 no longer declares `$$name"; return @() }
    [regex]::Matches($m.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value.Replace('\','/') }
}
$allowDocs = @(Read-AllowList 'allowDocs')
$allowRoot = @(Read-AllowList 'allowRoot')
$allowDirs = @(Read-AllowList 'allowDirs')
if ($projector) {
    $exportedDocs = @($allowRoot | Where-Object { $_ -match '\.(md|txt)$' -or $_ -in @('LICENSE','NOTICE') }) +
                    @($allowDocs | Where-Object { $_ -match '\.md$' })
    $publicDocs = @('publish/overlay/README.md','publish/overlay/AGENTS.md') + $exportedDocs |
        Where-Object { -not ($_.StartsWith('publish/')) -or (Test-Path (Join-Path $repo 'publish/overlay')) }
    if ($allowDocs.Count -lt 15) { Fail "the exporter's allowDocs read back only $($allowDocs.Count) entries; the parser or the list changed shape" }
}
else {
    $publicDocs = @(Get-ChildItem -LiteralPath $repo -Recurse -File -Force |
        Where-Object { $_.Extension -in @('.md','.txt') -and $_.FullName -notmatch '[\\/]\.(git)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName).Replace('\','/') })
}

# Is a repository-relative path inside the public projection?
function Test-Exported([string]$relative) {
    $r = $relative.Replace('\','/')
    if ($r.StartsWith('./')) { $r = $r.Substring(2) }
    if (-not $projector) { return Test-Path -LiteralPath (Join-Path $repo $r) }
    if ($allowRoot -contains $r -or $allowDocs -contains $r) { return $true }
    # The overlay supplies the public README, AGENTS, CLAUDE, LICENSE and NOTICE.
    if ($r -notmatch '/' -and (Test-Path (Join-Path $repo ('publish/overlay/' + $r)))) { return $true }
    foreach ($d in $allowDirs) { if ($r.StartsWith($d.TrimEnd('/') + '/')) { return $true } }
    return $false
}

foreach ($path in $publicDocs) {
    $full = Join-Path $repo $path
    if (-not (Test-Path $full)) { Fail "public document is missing: $path"; continue }
    $base = if ($path.StartsWith('publish/overlay/')) { $repo } else { Split-Path -Parent $full }
    $text = Get-Content $full -Raw
    # Markdown links are matched over the prose only: `Array[Byte](sequence)` in a
    # code span is not a link, and the projector strips code the same way.
    $prose = [regex]::Replace([regex]::Replace($text, '(?s)```.*?```', ''), '`[^`\r\n]*`', '')
    foreach ($match in [regex]::Matches($prose, '\]\((?!https?://|mailto:|#)([^)#]+)(?:#[^)]+)?\)')) {
        $target = [Uri]::UnescapeDataString($match.Groups[1].Value).Replace('/','\')
        if ($target -match '^<.*>$') { continue }
        if (-not (Test-Path (Join-Path $base $target))) { Fail "$path links to missing '$target'"; continue }
        # The file exists here; will it exist THERE? A link into docs/evidence or a
        # program-state ledger resolves in the private tree and 404s in the public one.
        $rel = [System.IO.Path]::GetRelativePath($repo, [System.IO.Path]::GetFullPath((Join-Path $base $target))).Replace('\','/')
        if (-not (Test-Exported $rel)) { Fail "$path links to '$rel', which the projector does not export" }
    }
    # A path cited in backticks is a reference too, and the projector's own link
    # scan strips code spans before looking. Every `docs/...` or `scripts/...`
    # citation in an exported document must either be exported or be marked, in
    # the same passage, as private - so a reader of the public repository is told
    # rather than sent looking. CHANGELOG.md is history and is exempt: rewriting
    # released entries to satisfy a checker would falsify the record.
    if ($path -eq 'CHANGELOG.md') { continue }
    foreach ($match in [regex]::Matches($text, '`((?:docs|scripts|publish|tests|src)/[^`\s]+)`')) {
        $cited = $match.Groups[1].Value.TrimEnd('.',',',';',':')
        if ($cited -match '[*?<>]') { continue }   # a glob or a placeholder, not a file
        $start = [Math]::Max(0, $match.Index - 240)
        $window = $text.Substring($start, [Math]::Min(480, $text.Length - $start))
        $exists = Test-Path -LiteralPath (Join-Path $repo $cited)
        $exported = Test-Exported $cited
        if ($exported) {
            if (-not $exists) { Fail "$path cites '$cited', which the public projection promises but which does not exist" }
            continue
        }
        # In the public checkout an explicitly labelled private evidence path is
        # absent by design. The private-side run already checked that it exists
        # before projection and that this explanatory label accompanies it.
        if (-not $exists -and -not $projector -and $window -match '(?i)private|not (in|part of) the public|not exported|kept out of the public') { continue }
        if (-not $exists) { Fail "$path cites '$cited', which does not exist"; continue }
        if ($window -notmatch '(?i)private|not (in|part of) the public|not exported|kept out of the public') {
            Fail "$path cites '$cited', which the projector does not export, without saying it is private"
        }
    }
}

# -----------------------------------------------------------------------------
# THE EVIDENCE DOCUMENTS ARE A DELIVERABLE, and a deliverable that carries a
# viewer's error text or half a command is not one.
#
# This exists because a published reproduction command had lost a path separator
# - C:\hz-live\runs\HZ_M2025.rvt had become C:\hz-liveuns\HZ_M2025.rvt - and
# nothing checked. A command nobody can paste is worse than no command: it looks
# like instructions.
# -----------------------------------------------------------------------------
# THE MATRIX IS A PROJECTION OF THE RECORD. A total edited into the prose must
# not survive: the renderer re-renders from the JSON and refuses a document that
# has drifted from it.
$evidenceRoot = Join-Path $repo 'docs\evidence'
if (Test-Path -LiteralPath $evidenceRoot) {
$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
if (-not $py) {
    Fail 'no Python interpreter on PATH: the acceptance matrix could not be checked against its record'
}
else {
    & $py.Source (Join-Path $repo 'scripts/render-acceptance-matrix.py') --check
    if ($LASTEXITCODE -ne 0) { Fail 'the published acceptance matrix differs from the record it is rendered from' }
}

$evidenceDocs = @(Get-ChildItem -Path (Join-Path $repo 'docs/evidence') -Filter '*.md' -File -ErrorAction SilentlyContinue)
if ($evidenceDocs.Count -eq 0) { Fail 'no evidence documents were found to check' }

# Text that only ever arrives from a renderer, a viewer or a crashed tool.
$foreignText = @(
    'FileRenderer', 'processFileResult', 'Line doesnt exist', "Line doesn't exist",
    'Traceback (most recent call last)', 'System.Management.Automation.',
    'ParserError:', '<<<<<<<', '>>>>>>>'
)

foreach ($doc in $evidenceDocs) {
    $text = Get-Content -LiteralPath $doc.FullName -Raw
    $lines = $text -split "`n"

    foreach ($needle in $foreignText) {
        if ($text.Contains($needle)) {
            Fail ("{0} carries text that belongs to a tool and not to the document: '{1}'" -f $doc.Name, $needle)
        }
    }

    # A carriage return inside a UTF-8 document written with LF endings is what a
    # mangled escape leaves behind, and it hides inside a path.
    if ($text.Contains([char]13)) {
        Fail ("{0} contains a carriage return; a path with a `r in it renders as a broken command" -f $doc.Name)
    }

    # Every fenced block opens and closes.
    $fences = ([regex]::Matches($text, '(?m)^```')).Count
    if ($fences % 2 -ne 0) { Fail ("{0} has an unclosed code fence" -f $doc.Name) }

    # EVERY WINDOWS PATH IN A COMMAND MUST BE A PATH. The ones this delivery uses
    # live under two roots; a separator lost to an escape turns them into names
    # nobody can open, which is exactly the defect this guards.
    foreach ($m in [regex]::Matches($text, 'C:\\[A-Za-z0-9_\-.\\{}<>]+')) {
        $path = $m.Value.TrimEnd('.', ',', ')')
        if ($path -match '^C:\\hz-live' -and $path -notmatch '^C:\\hz-live(\\|$)') {
            Fail ("{0} names '{1}', which is not a path under C:\hz-live - a separator was lost" -f $doc.Name, $path)
        }
        if ($path -match '^C:\\Users\\[A-Za-z0-9._-]+\\' -and $path -notmatch 'placeholder') {
            Fail ("{0} names a personal path: {1}" -f $doc.Name, $path)
        }
    }

    # A POWERSHELL BLOCK MUST PARSE. Placeholders like <head> are the caller's to
    # fill, so they are substituted before parsing rather than excused.
    $inBlock = $false
    $block = New-Object System.Collections.Generic.List[string]
    $blockStart = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimEnd([char]13)
        if (-not $inBlock -and $line -match '^```powershell') { $inBlock = $true; $blockStart = $i + 1; $block.Clear(); continue }
        if ($inBlock -and $line -match '^```') {
            $inBlock = $false
            $source = ($block -join "`n")
            $filled = $source -replace '<[a-z_]+>', 'PLACEHOLDER'
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput($filled, [ref]$null, [ref]$errors)
            if ($errors -and $errors.Count -gt 0) {
                Fail ("{0} line {1}: the published PowerShell block does not parse: {2}" -f
                      $doc.Name, $blockStart, $errors[0].Message)
            }
            # A block that ends on a continuation is a truncated command.
            $lastReal = ($block | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 1)
            if ($lastReal -and $lastReal.TrimEnd().EndsWith('`')) {
                Fail ("{0} line {1}: the published command ends on a line continuation - it is truncated" -f
                      $doc.Name, $blockStart)
            }
            continue
        }
        if ($inBlock) { $block.Add($line) | Out-Null }
    }
    if ($inBlock) { Fail ("{0} ends inside a code fence" -f $doc.Name) }
}
}


# ---- one installer really prepares every supported client --------------------
$universalFailures = $failures.Count
$releaseBootstrap = Get-Content -LiteralPath (Join-Path $repo 'install-release.ps1') -Raw
$sourceInstaller = Get-Content -LiteralPath (Join-Path $repo 'install.ps1') -Raw
$installerIss = Get-Content -LiteralPath (Join-Path $repo 'installer\horizun-mcp.iss') -Raw
if ($releaseBootstrap -notmatch "\[string\]\s*\`$Client\s*=\s*'Both'") {
    Fail 'install-release.ps1 does not default to configuring both Codex and Claude Code.'
}
if ($sourceInstaller -notmatch '(?s)complete-install\.ps1.*?-Client Both') {
    Fail 'install.ps1 does not ask the completion helper to configure both CLI clients.'
}
if ($sourceInstaller -notmatch 'install-claude-desktop-extension\.ps1') {
    Fail 'install.ps1 does not prepare the Claude Desktop extension.'
}
if ($installerIss -notmatch '\{param:HORIZUNCLIENT\|Both\}' -or
    $installerIss -notmatch 'install-claude-desktop-extension\.ps1') {
    Fail 'the Windows Setup does not default to both CLI clients and Claude Desktop preparation.'
}
if ($failures.Count -eq $universalFailures) {
    Write-Host '[PASS] one Setup prepares Codex, Claude Code and Claude Desktop' -ForegroundColor Green
}

# ---- supported ChatGPT Work route and neutral product vocabulary -------------
$integrationFailures = $failures.Count
foreach ($required in @('scripts\chatgpt-tunnel.ps1', 'scripts\chatgpt-secret.lib.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $required))) { Fail "$required is missing." }
}
foreach ($doc in @('README.md', 'docs\CLIENTS.md', 'installer\horizun-mcp.iss',
                   'scripts\pack.ps1', 'scripts\diagnose-integrations.ps1')) {
    $text = Get-Content -LiteralPath (Join-Path $repo $doc) -Raw
    if ($text -notmatch '(?i)ChatGPT Work') { Fail "$doc does not expose ChatGPT Work." }
}
if ($failures.Count -eq $integrationFailures) {
    Write-Host '[PASS] ChatGPT Work is shipped and current product vocabulary is organisation-neutral' -ForegroundColor Green
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "[FAIL] $_" -ForegroundColor Red }
    throw "$($failures.Count) public consistency failure(s)"
}
Write-Host "[PASS] one version source, durable CLI scope, Python default/evidence decision, and public local links" -ForegroundColor Green
