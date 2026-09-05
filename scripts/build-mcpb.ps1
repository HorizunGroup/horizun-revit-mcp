#Requires -Version 5.1
<#
  Build the Claude Desktop extension: dist/horizun-revit-<version>.mcpb

  WHAT AN .mcpb IS. A ZIP with a manifest.json at its root, per the MCPB manifest
  spec (manifest_version 0.3, github.com/modelcontextprotocol/mcpb). Claude
  Desktop installs one from Settings > Extensions > Advanced settings > Install
  Extension and generates the MCP server entry itself, so the user never edits
  claude_desktop_config.json by hand.

  WHAT THIS ONE DOES NOT BUNDLE: the server. The extension declares the command
  of the ALREADY-INSTALLED horizun-mcp.exe rather than carrying a copy, for one
  measured reason - the server and the Revit add-in share a contract hash and
  refuse to pair across versions. A bundled copy would be frozen at the moment the
  extension was installed, so the next `install.ps1` would leave Claude Desktop
  talking to a server the add-in refuses. Pointing at the installed path means the
  extension follows every update with no second action, which is the behaviour the
  product already guarantees for Codex and Claude Code.

  TWO DISTRIBUTIONS, and the difference is deliberate.

  PUBLISHED (default) writes the command as ${HOME}/AppData/Local/... - one of
  the substitution variables the manifest spec defines for mcp_config - so no
  account name is baked into a file that ships, and the packaging gate reads the
  compressed bytes to prove it. It refuses to write a resolved path.

  LOCAL (-Local) writes the RESOLVED absolute path of the server on this machine.
  This is the copy a user actually installs, and it exists because whether a
  given host substitutes ${HOME} is a host behaviour this project has NOT
  demonstrated. Depending on it would mean shipping an extension that installs
  cleanly and then shows no tools, with nothing saying why. -Local refuses to
  write into dist\, because that copy carries an account name.

    pwsh -File scripts/build-mcpb.ps1
    pwsh -File scripts/build-mcpb.ps1 -Local -Output C:\...\horizun-revit.mcpb
    pwsh -File scripts/build-mcpb.ps1 -NoToolList     # do not launch a server

  Exit codes: 0 built   1 refused
#>
[CmdletBinding()]
param(
    [string]$Output,
    # The server whose tools/list becomes the manifest's advertised tool set. It
    # is launched and asked; the list is never transcribed from a document.
    [string]$ServerPath,
    # Skip launching a server. The manifest then advertises no tools and relies on
    # tools_generated, which is legal but loses the pre-install tool preview.
    [switch]$NoToolList,
    # Emit the RESOLVED path of the installed server instead of ${HOME}. This is
    # the copy a machine installs; it never goes into dist\.
    [switch]$Local,
    [string]$Json
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'mcp-stdio.lib.ps1')
. (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')

function Fail($m) { Write-Host "  $m" -ForegroundColor Red; exit 1 }
function Step($m) { Write-Host "  $m" -ForegroundColor Gray }

# --- identity ------------------------------------------------------------------
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'Directory.Build.props has no Version' }

if (-not $ServerPath) {
    # Prefer the staged server of the package being built; fall back to whatever
    # is installed. Either way the list comes from a real handshake.
    $staged = Join-Path $repo 'dist\stage\server\horizun-mcp.exe'
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
    $ServerPath = if (Test-Path -LiteralPath $staged) { $staged } elseif (Test-Path -LiteralPath $installed) { $installed } else { $null }
}

if (-not $Output) { $Output = Join-Path $repo ("dist\{0}-{1}.mcpb" -f $script:HorizunMcpbName, $version) }
$Output = [IO.Path]::GetFullPath($Output)

Write-Host ""
Write-Host "Building the Claude Desktop extension" -ForegroundColor Cyan
Step "version   $version"
Step "output    $Output"

# --- the command the extension will run ----------------------------------------
$distribution = if ($Local) { 'Local' } else { 'Published' }
if ($Local) {
    $distRoot = [IO.Path]::GetFullPath((Join-Path $repo 'dist')).TrimEnd('\') + '\'
    if ($Output.StartsWith($distRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Fail ("Refusing to write a machine-resolved extension into dist\: its command carries this account's " +
              "home directory, and dist\ is where published artifacts are hashed from. Give -Output a path outside dist\.")
    }
    # THE COMMAND IS THE SERVER THIS BUILD WAS POINTED AT, not a constant.
    # It used to be the default install path regardless of -ServerPath, so a
    # caller building for a server somewhere else got a manifest naming a file it
    # had never looked at - which installs cleanly and shows no tools.
    if (-not $ServerPath) { Fail '-Local needs a server to name: pass -ServerPath, or install Horizun first.' }
    $command = [IO.Path]::GetFullPath($ServerPath)
    if (-not (Test-Path -LiteralPath $command -PathType Leaf)) {
        Fail "-Local was pointed at a server that does not exist: $command"
    }
    Step "command   $command  (resolved for this machine)"
}
else {
    $command = $script:HorizunMcpbPortableCommand
    Step "command   $command"
}

# --- the tools it advertises ---------------------------------------------------
$tools = @()
$toolSource = 'none: -NoToolList'
if (-not $NoToolList) {
    if (-not $ServerPath -or -not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) {
        Fail ("No server to ask for the tool list: neither dist\stage nor the installed path exists. " +
              "Build the package first, or pass -ServerPath, or pass -NoToolList to advertise none.")
    }
    $probe = Invoke-HorizunMcpProbe -Command $ServerPath -ListTools -TimeoutSec 120
    if (-not $probe.ok) { Fail "could not read the tool list from $ServerPath - $($probe.problem)" }
    $tools = @($probe.tool_names | Sort-Object | ForEach-Object { [ordered]@{ name = $_ } })
    $toolSource = "$ServerPath (tools/list over stdio)"
    Step ("tools     {0}, read from a live handshake with {1}" -f $tools.Count, (Split-Path -Leaf $ServerPath))
}

# --- the manifest --------------------------------------------------------------
$manifest = New-HorizunMcpbManifest -Version $version -Command $command -Tools $tools
$problems = @(Test-HorizunMcpbManifest $manifest -Distribution $distribution)
if ($problems.Count -gt 0) {
    Write-Host ""
    foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Red }
    Fail 'the manifest this script just produced does not satisfy its own checks'
}

# --- stage and zip -------------------------------------------------------------
$work = Join-Path ([IO.Path]::GetTempPath()) ("horizun-mcpb-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $manifestJson = $manifest | ConvertTo-Json -Depth 20
    # No BOM. A BOM ahead of `{` is not JSON to a strict parser, and the whole
    # extension would fail to install with a message about the manifest.
    [IO.File]::WriteAllText((Join-Path $work 'manifest.json'), $manifestJson, [Text.UTF8Encoding]::new($false))

    $icons = Join-Path $repo 'integrations\claude-desktop'
    foreach ($n in @('icon-128.png', 'icon-256.png')) {
        $src = Join-Path $icons $n
        if (-not (Test-Path -LiteralPath $src)) { Fail "the extension icon is missing: $src (run integrations/claude-desktop/make-icon.py)" }
        Copy-Item -LiteralPath $src -Destination (Join-Path $work $n) -Force
    }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $work 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $icons 'EXTENSION.md') -Destination (Join-Path $work 'README.md') -Force

    $outDir = Split-Path -Parent $Output
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }

    # Written entry by entry rather than with Compress-Archive, so the archive is
    # REPRODUCIBLE: fixed entry order and a fixed timestamp. Two builds of the same
    # commit then have the same SHA-256, which is what makes the hash in
    # package-hashes.json mean anything.
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $fixed = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $zip = [IO.Compression.ZipFile]::Open($Output, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('manifest.json', 'icon-128.png', 'icon-256.png', 'README.md', 'LICENSE')) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixed
            $stream = $entry.Open()
            try { $bytes = [IO.File]::ReadAllBytes((Join-Path $work $name)); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

$item = Get-Item -LiteralPath $Output
$sha = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLower()
Write-Host ""
Write-Host ("  built {0}" -f (Split-Path -Leaf $Output)) -ForegroundColor Green
Step ("bytes     {0:N0}" -f $item.Length)
Step ("sha256    $sha")

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        path          = $Output
        bytes         = $item.Length
        sha256        = $sha
        version       = $version
        command       = $command
        distribution  = $distribution
        tool_count    = $tools.Count
        tool_source   = $toolSource
    } | ConvertTo-Json -Depth 6 | Out-File -FilePath $Json -Encoding utf8
    Step "wrote $Json"
}
exit 0
