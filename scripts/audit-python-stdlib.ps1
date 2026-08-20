#Requires -Version 5.1
<#
  Deterministic static triage of the IronPython standard library bytes that are
  redistributed in every staged Revit payload.

  This is deliberately narrower than a vulnerability scanner or a sandbox. It
  proves the staged file set, hashes, cross-year equality, package version and
  a small set of high-confidence source-risk rules. Results can be archived as
  JSON and SARIF. Any finding makes the command fail closed.
#>
[CmdletBinding()]
param(
    [string]$StageRoot,
    [string[]]$Years = @('2023', '2024', '2025', '2026', '2027'),
    [ValidateRange(1, 100000)] [int]$ExpectedPyCount = 614,
    [string]$Json,
    [string]$Sarif,
    [string]$NoticesPath,
    [string]$ProjectFile,
    [string]$ExpectedPackageVersion = '3.4.2',
    [string[]]$ExpectedNonPythonPaths
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $StageRoot) { $StageRoot = Join-Path $repo 'dist\stage' }
if (-not $NoticesPath) { $NoticesPath = Join-Path $repo 'THIRD-PARTY-NOTICES.md' }
if (-not $ProjectFile) { $ProjectFile = Join-Path $repo 'src\Horizun.Revit\Horizun.Revit.csproj' }

$rules = @(
    [ordered]@{ id='HZPY001'; level='error'; name='encoded-powershell'; description='PowerShell encoded-command invocation in redistributed Python source.' },
    [ordered]@{ id='HZPY002'; level='error'; name='download-and-execute'; description='A download primitive and a code/shell execution primitive occur in one bounded source region.' },
    [ordered]@{ id='HZPY003'; level='error'; name='decode-and-execute'; description='Decoded Base64 content is passed directly to exec/eval/compile.' },
    [ordered]@{ id='HZPY004'; level='error'; name='persistence-command'; description='A common Windows persistence command or Run key is present.' },
    [ordered]@{ id='HZPY005'; level='error'; name='hidden-bidi-control'; description='A bidirectional Unicode control character can hide the visual order of source.' },
    [ordered]@{ id='HZPY006'; level='error'; name='restricted-license-marker'; description='A strong copyleft/restricted licence marker appears without recognised alternative-licence text.' },
    [ordered]@{ id='HZPY007'; level='error'; name='invalid-utf8-source'; description='A staged Python source file is not valid UTF-8 in this pinned distribution.' },
    [ordered]@{ id='HZPY100'; level='error'; name='unexpected-library-file'; description='The staged standard library contains a non-Python file outside the pinned distribution allowlist.' },
    [ordered]@{ id='HZPY101'; level='error'; name='python-file-count'; description='A year does not contain the pinned number of Python source files.' },
    [ordered]@{ id='HZPY102'; level='error'; name='cross-year-file-set'; description='The relative Python source path set differs between Revit-year payloads.' },
    [ordered]@{ id='HZPY103'; level='error'; name='cross-year-byte-drift'; description='A redistributed library file has different bytes between Revit-year payloads.' },
    [ordered]@{ id='HZPY104'; level='error'; name='dependency-version'; description='The source project does not pin both IronPython packages to the audited version.' },
    [ordered]@{ id='HZPY105'; level='error'; name='licence-evidence'; description='Required IronPython standard-library licence evidence is absent.' },
    [ordered]@{ id='HZPY106'; level='error'; name='missing-payload'; description='An expected Revit-year standard-library payload is absent.' }
)

# Exact non-.py payload copied by IronPython.StdLib 3.4.2. New executable,
# archive, script or data bytes require review and an explicit baseline update.
$allowedNonPython = @(
    'ctypes/macholib/fetch_macholib',
    'ctypes/macholib/fetch_macholib.bat',
    'ctypes/macholib/README.ctypes',
    'distutils/command/command_template',
    'distutils/command/wininst-10.0-amd64.exe',
    'distutils/command/wininst-10.0.exe',
    'distutils/command/wininst-6.0.exe',
    'distutils/command/wininst-7.1.exe',
    'distutils/command/wininst-8.0.exe',
    'distutils/command/wininst-9.0-amd64.exe',
    'distutils/command/wininst-9.0.exe',
    'distutils/README',
    'email/architecture.rst',
    'ensurepip/_bundled/pip-18.1-py2.py3-none-any.whl',
    'ensurepip/_bundled/setuptools-40.6.2-py2.py3-none-any.whl',
    'lib2to3/Grammar.txt',
    'lib2to3/PatternGrammar.txt',
    'pydoc_data/_pydoc.css',
    'site-packages/README',
    'turtledemo/turtle.cfg',
    'venv/scripts/nt/activate.bat',
    'venv/scripts/nt/Activate.ps1',
    'venv/scripts/nt/deactivate.bat',
    'venv/scripts/posix/activate',
    'venv/scripts/posix/activate.csh',
    'venv/scripts/posix/activate.fish'
)
$expectedNonPythonWasBound = $PSBoundParameters.ContainsKey('ExpectedNonPythonPaths')
if (-not $expectedNonPythonWasBound) { $ExpectedNonPythonPaths = $allowedNonPython }
$allowedNonPythonSet = @{}
foreach ($path in $ExpectedNonPythonPaths) { $allowedNonPythonSet[$path] = $true }

$findings = New-Object System.Collections.ArrayList
function Add-Finding([string]$ruleId, [string]$message, [string]$path = '', [int]$line = 0, [string]$year = '') {
    [void]$findings.Add([ordered]@{
        rule_id = $ruleId
        level = 'error'
        message = $message
        path = $path.Replace('\', '/')
        line = $line
        year = $year
    })
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextLines([string]$path) {
    $encoding = New-Object Text.UTF8Encoding($false, $true)
    return ([IO.File]::ReadAllText($path, $encoding) -split "`r?`n", 0, 'RegexMatch')
}

function Test-SourceRisk([string]$path, [string]$relative, [string]$year) {
    try {
        $lines = @(Get-TextLines $path)
    } catch {
        Add-Finding 'HZPY007' ("Python source is not valid UTF-8: " + $_.Exception.Message) $relative 0 $year
        # ASCII risk tokens still have to be inspected in an invalid source.
        $lines = @([IO.File]::ReadAllText($path, [Text.Encoding]::GetEncoding(28591)) -split "`r?`n", 0, 'RegexMatch')
    }
    $wholeText = $lines -join "`n"
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        $number = $index + 1
        if ($line -match '(?i)\bpowershell(?:\.exe)?\b.{0,160}(?:-encodedcommand\b|-enc\b)') {
            Add-Finding 'HZPY001' 'Encoded PowerShell command detected.' $relative $number $year
        }
        if ($line -match '(?i)CurrentVersion[\\/]+Run(?:Once)?\b|\bschtasks(?:\.exe)?\s+/create\b') {
            Add-Finding 'HZPY004' 'Windows persistence primitive detected.' $relative $number $year
        }
        if ($line -match '[\u202A-\u202E\u2066-\u2069]') {
            Add-Finding 'HZPY005' 'Bidirectional Unicode control character detected.' $relative $number $year
        }
    }
    $download = '(?:DownloadString\s*\(|urlopen\s*\(|requests\.get\s*\(|WebClient\s*\()'
    $execute = '(?:exec\s*\(|eval\s*\(|os\.system\s*\(|subprocess\.|Popen\s*\()'
    if ($wholeText -match ("(?is){0}.{{0,800}}{1}|{1}.{{0,800}}{0}" -f $download, $execute)) {
        Add-Finding 'HZPY002' 'Download and execution primitives detected within a bounded source region.' $relative 1 $year
    }
    $decode = '(?:(?:base64\.)?(?:b64decode|decodebytes)\s*\()'
    $dynamicCode = '(?:(?:exec|eval|compile)\s*\()'
    if ($wholeText -match ("(?is){0}.{{0,800}}{1}|{1}.{{0,800}}{0}" -f $decode, $dynamicCode)) {
        Add-Finding 'HZPY003' 'Base64 decoding and dynamic code execution detected within a bounded source region.' $relative 1 $year
    }
    $restricted = $wholeText -match '(?i)Affero\s+General\s+Public\s+License|\bAGPL(?:v?\d)?\b|Server\s+Side\s+Public\s+License|\bSSPL\b|Commons\s+Clause'
    $gplOnly = $wholeText -match '(?i)(?:GNU\s+)?General\s+Public\s+License|\bGPLv?\d\b'
    $recognisedAlternative = ($wholeText -match '(?i)choose\s+between|dual[- ]licen[cs]e|alternative\s+licen[cs]e') -and
        ($wholeText -match '(?i)PSF\s+licen[cs]e')
    if ($restricted -or ($gplOnly -and -not $recognisedAlternative)) {
        Add-Finding 'HZPY006' 'Restricted/copy-left licence marker requires explicit distribution review.' $relative 1 $year
    }
}

# Licence and dependency declaration evidence are checked as part of the same
# invocation so an audit cannot silently divorce the bytes from their origin.
if (-not (Test-Path -LiteralPath $NoticesPath -PathType Leaf)) {
    Add-Finding 'HZPY105' "Third-party notices file is missing: $NoticesPath"
} else {
    $notice = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NoticesPath).Path)
    if ($notice -notmatch '(?i)IronPython' -or $notice -notmatch '(?i)PSF License' -or $notice -notmatch '(?i)614\s+[`*]*\.py') {
        Add-Finding 'HZPY105' 'Notices do not name IronPython, the PSF License and the 614-file standard-library payload.' $NoticesPath
    }
}

if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
    Add-Finding 'HZPY104' "Project file is missing: $ProjectFile"
} else {
    try {
        [xml]$project = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ProjectFile).Path)
        $versions = @{}
        foreach ($node in @($project.SelectNodes("//*[local-name()='PackageReference']"))) {
            $name = [string]$node.Include
            if ($name -in @('IronPython', 'IronPython.StdLib')) {
                if (-not $versions.ContainsKey($name)) { $versions[$name] = @() }
                $versions[$name] = @($versions[$name]) + [string]$node.Version
            }
        }
        foreach ($name in @('IronPython', 'IronPython.StdLib')) {
            $declared = @($versions[$name])
            if ($declared.Count -ne 1 -or $declared[0] -ne $ExpectedPackageVersion) {
                Add-Finding 'HZPY104' "$name must have exactly one declaration pinned to $ExpectedPackageVersion; found '$($declared -join ',')'." $ProjectFile
            }
        }
    } catch {
        Add-Finding 'HZPY104' ("Project package declarations could not be parsed: " + $_.Exception.Message) $ProjectFile
    }
}

$yearRecords = @()
$referencePy = $null
$referenceAll = $null
$referenceYear = ''
foreach ($year in $Years) {
    $lib = Join-Path $StageRoot ("plugin\{0}\lib" -f $year)
    if (-not (Test-Path -LiteralPath $lib -PathType Container)) {
        Add-Finding 'HZPY106' "Expected standard-library directory is missing: $lib" '' 0 $year
        continue
    }
    $lib = (Resolve-Path -LiteralPath $lib).Path
    $files = @(Get-ChildItem -LiteralPath $lib -Recurse -File | Sort-Object FullName)
    $py = @{}
    $all = @{}
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($lib.Length + 1).Replace('\', '/')
        $hash = Get-Sha256 $file.FullName
        $all[$relative] = $hash
        if ($file.Extension -ieq '.py') {
            $py[$relative] = $hash
            Test-SourceRisk $file.FullName $relative $year
        } elseif (-not $allowedNonPythonSet.ContainsKey($relative)) {
            Add-Finding 'HZPY100' "Unexpected non-Python file in standard library: $relative" $relative 0 $year
        }
    }
    foreach ($expectedPath in @($ExpectedNonPythonPaths | Sort-Object)) {
        if (-not $all.ContainsKey($expectedPath)) {
            Add-Finding 'HZPY100' "Expected pinned non-Python distribution file is missing: $expectedPath" $expectedPath 0 $year
        }
    }
    if ($py.Count -ne $ExpectedPyCount) {
        Add-Finding 'HZPY101' "Expected $ExpectedPyCount Python files; found $($py.Count)." '' 0 $year
    }

    if ($null -eq $referencePy) {
        $referencePy = $py
        $referenceAll = $all
        $referenceYear = [string]$year
    } else {
        $referencePaths = @($referencePy.Keys | Sort-Object)
        $currentPaths = @($py.Keys | Sort-Object)
        if (($referencePaths -join "`n") -ne ($currentPaths -join "`n")) {
            Add-Finding 'HZPY102' "Python path set differs from reference year $referenceYear." '' 0 $year
        }
        foreach ($relative in @($referenceAll.Keys | Sort-Object)) {
            if (-not $all.ContainsKey($relative)) {
                Add-Finding 'HZPY103' "File is missing relative to reference year $referenceYear." $relative 0 $year
            } elseif ($all[$relative] -ne $referenceAll[$relative]) {
                Add-Finding 'HZPY103' "Bytes differ from reference year $referenceYear." $relative 0 $year
            }
        }
        foreach ($relative in @($all.Keys | Sort-Object)) {
            if (-not $referenceAll.ContainsKey($relative)) {
                Add-Finding 'HZPY103' "File is absent from reference year $referenceYear." $relative 0 $year
            }
        }
    }

    $yearRecords += [ordered]@{
        year = [string]$year
        python_files = $py.Count
        total_files = $all.Count
    }
}

$inventory = @()
if ($null -ne $referenceAll) {
    foreach ($relative in @($referenceAll.Keys | Sort-Object)) {
        $inventory += [ordered]@{ path=$relative; sha256=$referenceAll[$relative] }
    }
}

$orderedFindings = @($findings | Sort-Object rule_id, year, path, line, message)
$result = [ordered]@{
    schema = 'horizun-python-stdlib-audit/v1'
    scanner_version = 1
    status = $(if ($orderedFindings.Count -eq 0) { 'pass' } else { 'failed' })
    package = [ordered]@{ name='IronPython.StdLib'; version=$ExpectedPackageVersion; expected_python_files=$ExpectedPyCount }
    years = @($yearRecords)
    reference_year = $referenceYear
    rules = @($rules)
    findings = @($orderedFindings)
    inventory = @($inventory)
    limitations = @(
        'Deterministic static triage is not semantic code review, dependency vulnerability analysis or runtime sandboxing.',
        'A pass means only that the pinned byte/file invariants and the explicit high-confidence rules passed.'
    )
}

function Write-JsonUtf8NoBom([object]$value, [string]$path, [int]$depth = 12) {
    $parent = Split-Path -Parent $path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $text = $value | ConvertTo-Json -Depth $depth
    [IO.File]::WriteAllText($path, $text + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
}

if ($Json) { Write-JsonUtf8NoBom $result $Json }

if ($Sarif) {
    $sarifRules = @()
    foreach ($rule in $rules) {
        $sarifRules += [ordered]@{ id=$rule.id; name=$rule.name; shortDescription=@{ text=$rule.description }; defaultConfiguration=@{ level=$rule.level } }
    }
    $sarifResults = @()
    foreach ($finding in $orderedFindings) {
        $entry = [ordered]@{ ruleId=$finding.rule_id; level=$finding.level; message=@{ text=$finding.message } }
        if ($finding.path) {
            $uri = if ($finding.year) { "plugin/{0}/lib/{1}" -f $finding.year, $finding.path } else { $finding.path }
            $location = [ordered]@{ artifactLocation=@{ uri=$uri } }
            if ($finding.line -gt 0) { $location.region = @{ startLine=$finding.line } }
            $entry.locations = @(@{ physicalLocation=$location })
        }
        $sarifResults += $entry
    }
    $sarifDoc = [ordered]@{
        '$schema'='https://json.schemastore.org/sarif-2.1.0.json'
        version='2.1.0'
        runs=@([ordered]@{
            tool=@{ driver=[ordered]@{ name='Horizun IronPython StdLib Audit'; version='1'; informationUri='https://github.com/HorizunGroup/horizun-mcp'; rules=@($sarifRules) } }
            results=@($sarifResults)
        })
    }
    Write-JsonUtf8NoBom $sarifDoc $Sarif 15
}

if ($orderedFindings.Count -gt 0) {
    $preview = @($orderedFindings | Select-Object -First 8 | ForEach-Object { "[$($_.rule_id)] $($_.year)/$($_.path): $($_.message)" }) -join '; '
    throw "IronPython standard-library audit failed with $($orderedFindings.Count) finding(s): $preview"
}

Write-Host ("[PASS] IronPython.StdLib {0}: {1} Python files x {2} Revit years; byte-identical; no static risk finding" -f $ExpectedPackageVersion, $ExpectedPyCount, $yearRecords.Count) -ForegroundColor Green
