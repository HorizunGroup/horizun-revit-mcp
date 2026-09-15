#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutFile)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$changelog = Get-Content (Join-Path $repo 'CHANGELOG.md') -Raw -Encoding UTF8
$pattern = '(?ms)^## v' + [regex]::Escape($version) + '[ \t]+[^\r\n]*\r?\n(.*?)(?=^## |\z)'
$section = [regex]::Match($changelog, $pattern)
if (-not $section.Success) { throw "CHANGELOG.md needs a release section for v$version before publication." }
$changes = $section.Groups[1].Value.Trim()
if (-not $changes) { throw "The v$version changelog section is empty." }
$tagUrl = "https://github.com/HorizunGroup/horizun-revit-mcp/blob/v$version"
$notes = @"
# Horizun Revit MCP $version

## Download and connect

Install **horizun-mcp-$version-setup.exe** from this release. Requires Windows
x64 and Revit 2023-2027; close Revit first. The server runtime and add-ins are
included. No Git, Visual Studio or .NET SDK is needed.

Releases are unsigned by policy. Verify the setup against SHA256SUMS.txt;
matching hashes do not authenticate a Windows publisher.

- **Codex / Claude Code:** close the client so deferred registration can finish, then reopen it.
- **Claude Desktop:** install the .mcpb delivered in Documents\Horizun-Revit-MCP inside Settings > Extensions, then restart the app.
- **ChatGPT Work:** complete the Secure MCP Tunnel connection.

The additional .mcpb download connects to the installed server. **Run the Windows
installer first.** Its presence in a registry does not install Revit or the add-in.
After connection, call horizun_health from the client and check the loaded version.

## Changes

$changes

## Evidence and known limits

Checksums, the payload manifest, package-hashes.json and SBOM accompany this
release. Stable releases also carry live-2023.json through live-2027.json;
preview releases do not claim a completed stable matrix. Read each report's
version, commit, failed, unverified and not-covered counts.

These are publisher reports, not an independent competitive benchmark or a
clean-machine client-installation success rate. See the
[client guide]($tagUrl/docs/CLIENTS.md), [benchmark scope]($tagUrl/docs/BENCHMARK.md)
and [release policy]($tagUrl/docs/RELEASE-POLICY.md).
"@
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $notes.Trim() + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "[release-notes] generated $OutFile for $version"
