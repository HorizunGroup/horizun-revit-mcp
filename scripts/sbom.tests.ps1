#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo 'dist\stage'
$out = Join-Path ([IO.Path]::GetTempPath()) ('horizun-sbom-' + [guid]::NewGuid().ToString('N') + '.json')

try {
    & (Join-Path $PSScriptRoot 'sbom.ps1') -OutFile $out
    if ($LASTEXITCODE -notin 0,$null) { throw "sbom.ps1 exited $LASTEXITCODE" }
    $doc = Get-Content $out -Raw | ConvertFrom-Json
    if ($doc.bomFormat -ne 'CycloneDX' -or $doc.specVersion -ne '1.6') { throw 'not CycloneDX 1.6' }

    $staged = @(Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\','/') } | Sort-Object)
    $listed = @($doc.components.name | Sort-Object)
    if (($staged -join "`n") -ne ($listed -join "`n")) { throw 'SBOM path set is not exactly the staged path set' }

    $refs = @($doc.components | ForEach-Object { $_.'bom-ref' })
    if (@($refs | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'CycloneDX contains a component without bom-ref'
    }
    $uniqueRefs = @($refs | Sort-Object -Unique)
    if ($uniqueRefs.Count -ne $doc.components.Count) {
        throw "CycloneDX bom-ref values are not unique: $($doc.components.Count) components, $($uniqueRefs.Count) identities"
    }

    $text = Get-Content $out -Raw
    if ($text -match '(?i)proprietary|unknown\s*-') { throw 'SBOM contains a proprietary or unknown licence classification' }
    foreach ($component in $doc.components) {
        if (-not $component.hashes -or -not $component.licenses) { throw "missing hash/licence: $($component.name)" }
    }
    Write-Host "[PASS] CycloneDX inventories all $($listed.Count) staged files with hashes and named licences"
}
finally { Remove-Item $out -Force -ErrorAction SilentlyContinue }
