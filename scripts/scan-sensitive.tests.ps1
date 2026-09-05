#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$scanner = Join-Path $PSScriptRoot 'scan-sensitive.ps1'
$engine = (Get-Process -Id $PID).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-sensitive-test-' + [guid]::NewGuid().ToString('N'))
$terms = "$root-terms.txt"
$json = Join-Path $root 'result.json'

try {
    New-Item -ItemType Directory -Force (Join-Path $root '.github') | Out-Null
    Set-Content -LiteralPath $terms -Value 'alice' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $root '.github/CODEOWNERS') `
        -Value '/.github/workflows/ @alice-maintainer' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $root 'CODE-SIGNING-POLICY.md') `
        -Value 'Maintainer: [@alice-maintainer](https://github.com/alice-maintainer)' -Encoding utf8

    & $engine -NoProfile -ExecutionPolicy Bypass -File $scanner `
        -Root $root -AllFiles -TermsFile $terms -RequireTerms -Json $json
    if ($LASTEXITCODE -ne 0) {
        throw 'public GitHub maintainer identities were reported as private client data'
    }
    $clean = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    if ($clean.finding_count -ne 0) { throw 'governance-only fixture was not clean' }

    Set-Content -LiteralPath (Join-Path $root 'README.md') `
        -Value 'Customer delivery for Alice' -Encoding utf8
    & $engine -NoProfile -ExecutionPolicy Bypass -File $scanner `
        -Root $root -AllFiles -TermsFile $terms -RequireTerms -Json $json
    if ($LASTEXITCODE -ne 1) { throw "a private-name leak exited $LASTEXITCODE instead of 1" }
    $leak = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    $rows = @($leak.findings | Where-Object { $_.file -eq 'README.md' -and $_.rule -eq 'sensitive-term' })
    if ($rows.Count -ne 1) { throw 'the same term outside public governance was not reported exactly once' }

    # The scanner is the last line, not the first. Evidence files are WRITTEN by
    # scripts/consolidate-live-session.py, and a personal account name once
    # reached a published one because the redaction there matched a path with
    # separators and missed the same name flattened into a folder name. Run that
    # script's own redaction assertions here, so the gate covers the writer and
    # not only the reader.
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
    if (-not $py) {
        throw 'no Python interpreter on PATH: the evidence-redaction assertions could not be run, and a gate that skips is not a gate'
    }
    $consolidator = Join-Path $PSScriptRoot 'consolidate-live-session.py'
    & $py.Source $consolidator --self-test
    if ($LASTEXITCODE -ne 0) {
        throw "the evidence redaction self-test failed with exit $LASTEXITCODE"
    }

    Write-Host '[PASS] public GitHub governance identities are narrow exceptions; the same term elsewhere still fails' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $terms -Force -ErrorAction SilentlyContinue
}
