#Requires -Version 7.0
[CmdletBinding()]
param([string]$Output)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$privateProjector = Join-Path $repo 'publish\make-public-package.ps1'

if (Test-Path -LiteralPath $privateProjector) {
    # Private source tree: build the exact allowlisted tree, resolve links there
    # and run its mandatory sensitive-name/secret scan. The projector itself is
    # intentionally private and is never referenced by the exported workflow.
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$privateProjector)
    if ($Output) { $args += @('-Output',$Output) }
    & powershell @args
    if ($LASTEXITCODE -ne 0) { throw "public projection failed ($LASTEXITCODE)" }
    exit 0
}

# Public checkout: this tree already IS the projection. Re-run the two properties
# without depending on private publishing machinery that is deliberately absent.
$broken = New-Object System.Collections.Generic.List[string]
foreach ($doc in Get-ChildItem -LiteralPath $repo -Recurse -File -Include *.md) {
    if ($doc.FullName -match '[\\/]\.git[\\/]') { continue }
    $text = Get-Content -LiteralPath $doc.FullName -Raw
    $text = [regex]::Replace($text, '(?s)```.*?```', '')
    $text = [regex]::Replace($text, '`[^`\r\n]*`', '')
    foreach ($match in [regex]::Matches($text, '\]\((?!https?://|mailto:|#)([^)#\s]+)(?:#[^)]+)?\)')) {
        $target = [Uri]::UnescapeDataString($match.Groups[1].Value).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath (Join-Path $doc.DirectoryName $target))) {
            $broken.Add("$($doc.FullName.Substring($repo.Length + 1)) -> $target") | Out-Null
        }
    }
}
if ($broken.Count) { throw "public tree has broken local links:`n  $($broken -join "`n  ")" }

& pwsh (Join-Path $PSScriptRoot 'scan-sensitive.ps1') -Root $repo -AllFiles -RequireTerms
if ($LASTEXITCODE -ne 0) { throw "public-tree sensitive/secret scan failed ($LASTEXITCODE)" }
Write-Host '[PASS] exported public tree has complete links and passed the mandatory scan' -ForegroundColor Green
