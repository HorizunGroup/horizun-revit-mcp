#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-client-remove-' + [guid]::NewGuid().ToString('N'))
$oldProfile = $env:USERPROFILE
$oldLocal = $env:LOCALAPPDATA

try {
    $env:USERPROFILE = $root
    $env:LOCALAPPDATA = Join-Path $root 'local'
    New-Item -ItemType Directory -Path (Join-Path $root '.codex') -Force | Out-Null

    @'
{
  "theme": "dark",
  "mcpServers": {
    "keep-me": { "command": "other.exe", "args": ["x"] },
    "horizun-revit": { "command": "old.exe", "args": [] }
  }
}
'@ | Set-Content (Join-Path $root '.claude.json') -Encoding utf8

    @'
model = "gpt-test"

[mcp_servers.horizun-revit]
command = 'old.exe'
args = []

[mcp_servers.horizun-revit.env]
SAMPLE = "one"

[[profiles]]
name = "array-table-must-survive"

[mcp_servers.keep-me]
command = 'other.exe'
args = ['x']
'@ | Set-Content (Join-Path $root '.codex\config.toml') -Encoding utf8

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') -Remove -Client Both -Force
    if ($LASTEXITCODE -ne 0) { throw "register-client removal exited $LASTEXITCODE" }

    $claude = Get-Content (Join-Path $root '.claude.json') -Raw | ConvertFrom-Json
    if ('horizun-revit' -in @($claude.mcpServers.PSObject.Properties.Name)) { throw 'Claude target remained' }
    if ('keep-me' -notin @($claude.mcpServers.PSObject.Properties.Name) -or $claude.theme -ne 'dark') { throw 'Claude unrelated data changed' }

    $codex = Get-Content (Join-Path $root '.codex\config.toml') -Raw
    if ($codex -match '\[mcp_servers\.horizun-revit(?:\]|\.)') { throw 'Codex target or nested target table remained' }
    if ($codex -notmatch '\[mcp_servers\.keep-me\]' -or $codex -notmatch 'model\s*=\s*"gpt-test"' -or
        $codex -notmatch '\[\[profiles\]\]' -or $codex -notmatch 'array-table-must-survive') {
        throw 'Codex unrelated data changed, including a valid array-of-tables section'
    }
    Write-Host '[PASS] targeted client removal preserves every unrelated JSON/TOML entry' -ForegroundColor Green
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:LOCALAPPDATA = $oldLocal
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
