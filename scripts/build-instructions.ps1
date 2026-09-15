<#
  Render the printed instruction sheets that ship beside the .mcpb.

  WHY AT BUILD TIME. The sheet is the thing somebody reads while installing, so
  it has to exist before the installer runs - not be produced on their machine,
  where there is no renderer, no fonts we chose and no way to check the result.
  These are built here, LOOKED AT once, and shipped as bytes.

  Chrome's headless print is the renderer because it is the one already on a
  build machine that can honour @page and web typography. If it is absent this
  fails loudly: an installer that silently ships no instructions is the bug this
  whole change exists to fix.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'installer\instructions'

if (-not $Version) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
}
if (-not $Version) { throw 'could not determine the product version' }

$chrome = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $chrome) { throw 'no Chrome or Edge found to render the instruction sheets' }

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$made = @()
foreach ($lang in 'es', 'en') {
    $html = Join-Path $source "claude-desktop.$lang.html"
    if (-not (Test-Path -LiteralPath $html)) { throw "missing instruction source: $html" }

    # The version is stamped into a COPY, so the source stays a template and the
    # sheet can never name a version other than the one it ships with.
    $staged = Join-Path ([System.IO.Path]::GetTempPath()) ("horizun-instr-$lang-" + [guid]::NewGuid().ToString('N') + '.html')
    (Get-Content -LiteralPath $html -Raw).Replace('__VERSION__', $Version) |
        Set-Content -LiteralPath $staged -Encoding UTF8

    $pdf = Join-Path $OutputDir "Instalar en Claude Desktop.$lang.pdf"
    & $chrome --headless --disable-gpu --no-pdf-header-footer `
        "--print-to-pdf=$pdf" ("file:///" + $staged.Replace('\', '/')) 2>&1 | Out-Null
    Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path -LiteralPath $pdf)) { throw "the renderer produced no PDF for '$lang'" }
    $bytes = (Get-Item -LiteralPath $pdf).Length
    if ($bytes -lt 3kb) { throw "the '$lang' sheet is only $bytes bytes; that is not a rendered page" }

    Write-Host ("[instructions] {0} -> {1:N0} bytes" -f (Split-Path -Leaf $pdf), $bytes)
    $made += $pdf
}

$made
