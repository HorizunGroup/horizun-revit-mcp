#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$scanner = Join-Path $PSScriptRoot 'audit-python-stdlib.ps1'
$repo = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('horizun-python-audit-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-TestFile([string]$path, [string]$text) {
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($path, $text, $utf8)
}

function Invoke-Fixture([string]$stage, [string]$json, [string]$sarif) {
    & $scanner -StageRoot $stage -Years @('2023') -ExpectedPyCount 1 -Json $json -Sarif $sarif `
        -NoticesPath (Join-Path $temp 'THIRD-PARTY-NOTICES.md') -ProjectFile (Join-Path $temp 'fixture.csproj') `
        -ExpectedNonPythonPaths @()
}

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Write-TestFile (Join-Path $temp 'THIRD-PARTY-NOTICES.md') 'IronPython standard library: 614 `.py` files, under the PSF License.'
    Write-TestFile (Join-Path $temp 'fixture.csproj') '<Project><ItemGroup><PackageReference Include="IronPython" Version="3.4.2"/><PackageReference Include="IronPython.StdLib" Version="3.4.2"/></ItemGroup></Project>'

    $stage = Join-Path $temp 'stage'
    $source = Join-Path $stage 'plugin\2023\lib\safe.py'
    $json = Join-Path $temp 'benign.json'
    $sarif = Join-Path $temp 'benign.sarif'
    Write-TestFile $source "def add(a, b):`n    return a + b`n"
    Invoke-Fixture $stage $json $sarif
    $benign = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    $benignSarif = Get-Content -LiteralPath $sarif -Raw | ConvertFrom-Json
    if ($benign.status -ne 'pass' -or @($benign.inventory).Count -ne 1) { throw 'benign fixture did not pass with one inventoried source file' }
    if ($benignSarif.version -ne '2.1.0' -or @($benignSarif.runs[0].results).Count -ne 0) { throw 'benign SARIF is invalid or contains findings' }

    # The malicious content uses the real rule path: source bytes are scanned,
    # the process fails, JSON remains machine-readable and SARIF has a location.
    Write-TestFile $source "# AGPL payload`nimport base64`npayload = base64.b64decode('cHJpbnQoMSk=')`n# split across statements to exercise bounded flow triage`nexec(payload)`n"
    $maliciousJson = Join-Path $temp 'malicious.json'
    $maliciousSarif = Join-Path $temp 'malicious.sarif'
    $failed = $false
    try { Invoke-Fixture $stage $maliciousJson $maliciousSarif } catch { $failed = $true }
    if (-not $failed) { throw 'malicious fixture did not fail the scanner' }
    $malicious = Get-Content -LiteralPath $maliciousJson -Raw | ConvertFrom-Json
    if ($malicious.status -ne 'failed' -or 'HZPY003' -notin @($malicious.findings.rule_id)) {
        throw 'malicious fixture did not produce the decode-and-execute finding'
    }
    if ('HZPY006' -notin @($malicious.findings.rule_id)) { throw 'malicious fixture did not produce the restricted-licence finding' }
    $maliciousSarifDoc = Get-Content -LiteralPath $maliciousSarif -Raw | ConvertFrom-Json
    if ('HZPY003' -notin @($maliciousSarifDoc.runs[0].results.ruleId)) { throw 'malicious finding is absent from SARIF' }

    # The file-set gate is independent of source-pattern matching.
    Write-TestFile $source "value = 1`n"
    Write-TestFile (Join-Path $stage 'plugin\2023\lib\dropper.exe') 'not-an-executable-but-an-unexpected-distribution-file'
    $unexpectedJson = Join-Path $temp 'unexpected.json'
    $failed = $false
    try { Invoke-Fixture $stage $unexpectedJson (Join-Path $temp 'unexpected.sarif') } catch { $failed = $true }
    if (-not $failed) { throw 'unexpected payload file did not fail the scanner' }
    $unexpected = Get-Content -LiteralPath $unexpectedJson -Raw | ConvertFrom-Json
    if ('HZPY100' -notin @($unexpected.findings.rule_id)) { throw 'unexpected payload did not produce HZPY100' }

    # Finally audit the actual staged payload, when present, so this test covers
    # both the adversarial fixture and the 614-file distribution it protects.
    $realStage = Join-Path $repo 'dist\stage'
    if (Test-Path -LiteralPath (Join-Path $realStage 'plugin\2027\lib')) {
        $actualJson = Join-Path $temp 'actual.json'
        $actualSarif = Join-Path $temp 'actual.sarif'
        & $scanner -StageRoot $realStage -Json $actualJson -Sarif $actualSarif
        $actual = Get-Content -LiteralPath $actualJson -Raw | ConvertFrom-Json
        if ($actual.status -ne 'pass' -or @($actual.inventory | Where-Object { $_.path -like '*.py' }).Count -ne 614) {
            throw 'actual staged IronPython standard library did not produce the expected clean inventory'
        }
        $secondJson = Join-Path $temp 'actual-second.json'
        $secondSarif = Join-Path $temp 'actual-second.sarif'
        & $scanner -StageRoot $realStage -Json $secondJson -Sarif $secondSarif
        if ((Get-FileHash -LiteralPath $actualJson).Hash -ne (Get-FileHash -LiteralPath $secondJson).Hash -or
            (Get-FileHash -LiteralPath $actualSarif).Hash -ne (Get-FileHash -LiteralPath $secondSarif).Hash) {
            throw 'identical staged input did not produce byte-identical JSON and SARIF evidence'
        }
    }

    Write-Host '[PASS] Python stdlib audit: benign, malicious, unexpected-file, JSON and SARIF paths verified'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
