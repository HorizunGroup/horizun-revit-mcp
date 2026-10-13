#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo 'dist\stage'
$out = Join-Path ([IO.Path]::GetTempPath()) ('horizun-sbom-' + [guid]::NewGuid().ToString('N') + '.json')

try {
    $toolchain = Get-Content (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$toolchain.sdk.version) -or
        $toolchain.sdk.rollForward -ne 'disable' -or $toolchain.sdk.allowPrerelease -ne $false) {
        throw 'global.json must pin one stable SDK exactly (rollForward=disable, allowPrerelease=false)'
    }
    $effectiveSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $effectiveSdk -ne [string]$toolchain.sdk.version) {
        throw "effective SDK '$effectiveSdk' does not match global.json '$($toolchain.sdk.version)'"
    }

    & (Join-Path $PSScriptRoot 'sbom.ps1') -OutFile $out
    if ($LASTEXITCODE -notin 0,$null) { throw "sbom.ps1 exited $LASTEXITCODE" }
    $doc = Get-Content $out -Raw | ConvertFrom-Json
    if ($doc.bomFormat -ne 'CycloneDX' -or $doc.specVersion -ne '1.6') { throw 'not CycloneDX 1.6' }

    $staged = @(Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\','/') } | Sort-Object)
    $fileComponents = @($doc.components | Where-Object { $_.type -eq 'file' })
    $listed = @($fileComponents.name | Sort-Object)
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
    foreach ($component in $fileComponents) {
        if (-not $component.hashes -or -not $component.licenses) { throw "missing hash/licence: $($component.name)" }
    }
    $runtime = @($doc.components | Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' })
    if ($runtime.Count -ne 1 -or $runtime[0].type -ne 'framework' -or
        [string]::IsNullOrWhiteSpace([string]$runtime[0].version) -or
        $runtime[0].purl -ne "pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@$($runtime[0].version)") {
        throw 'SBOM does not carry one versioned/PURL-addressable Microsoft.NETCore.App.Runtime.win-x64 component'
    }
    $appRef = [string]$doc.metadata.component.'bom-ref'
    $appDependency = @($doc.dependencies | Where-Object { $_.ref -eq $appRef })
    if ($appDependency.Count -ne 1 -or $runtime[0].'bom-ref' -notin @($appDependency[0].dependsOn)) {
        throw 'application dependency graph does not bind the shipped runtime component'
    }
    # The server's PDF reader (horizun_read_schedule): its package and every one of its
    # staged assemblies carry Apache-2.0, never the runtime's MIT by default.
    $pdfPig = @($doc.components | Where-Object { $_.type -eq 'library' -and $_.name -eq 'PdfPig' })
    if ($pdfPig.Count -ne 1 -or $pdfPig[0].purl -ne "pkg:nuget/PdfPig@$($pdfPig[0].version)" -or
        $pdfPig[0].licenses[0].license.id -ne 'Apache-2.0' -or $pdfPig[0].'bom-ref' -notin @($appDependency[0].dependsOn)) {
        throw 'SBOM does not carry one PURL-addressable, Apache-2.0 PdfPig package bound to the application'
    }
    $pdfPigFiles = @($fileComponents | Where-Object { $_.name -like 'server/UglyToad.PdfPig*.dll' })
    if ($pdfPigFiles.Count -eq 0) { throw 'the staged server carries no UglyToad.PdfPig assembly' }
    foreach ($file in $pdfPigFiles) {
        if ($file.licenses[0].license.id -ne 'Apache-2.0') { throw "$($file.name) is not classified Apache-2.0" }
    }
    Write-Host "[PASS] SDK $effectiveSdk is exact; CycloneDX inventories all $($listed.Count) staged files and runtime $($runtime[0].version) with PURL/dependency evidence"
}
finally { Remove-Item $out -Force -ErrorAction SilentlyContinue }
