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

    # Backlog #25. TOML allows a quoted key and a trailing comment on a table
    # header. The table must be FOUND under either spelling - not appended a second
    # time, which makes Codex refuse the whole file - and a following table whose
    # header carries a comment is another table, never part of ours.
    . (Join-Path $PSScriptRoot 'mcp-legacy-registrations.lib.ps1')
    $hostExe = (Get-Process -Id $PID).Path
    $server = Join-Path $root 'server\horizun-mcp.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $server) -Force | Out-Null
    [IO.File]::WriteAllBytes($server, [byte[]](1, 2, 3))
    $codexPath = Join-Path $root '.codex\config.toml'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $spellings = @(
        '[mcp_servers."horizun-revit"]',
        '[mcp_servers.horizun-revit]   # added by hand',
        "[ mcp_servers . 'horizun-revit' ] # spaced and literal-quoted"
    )
    foreach ($spelling in $spellings) {
        [IO.File]::WriteAllText($codexPath, (@(
            'model = "gpt-test"', '',
            $spelling, "command = 'old.exe'", 'args = []', '',
            '[profiles.fast] # the profile somebody uses', 'model = "fast-model"', '',
            '[mcp_servers.keep-me]', "command = 'other.exe'", ''
        ) -join "`n"), $utf8)
        & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') -Client Codex -ServerPath $server -Force *> $null
        if ($LASTEXITCODE -ne 0) { throw "register-client over '$spelling' exited $LASTEXITCODE" }
        $after = [IO.File]::ReadAllText($codexPath)
        $lines = @($after -split "`r?`n")
        $ours = @(Get-HorizunTomlMcpSections -Lines $lines | Where-Object { $_.Name -eq 'horizun-revit' })
        if ($ours.Count -ne 1) { throw "registering over '$spelling' left $($ours.Count) horizun-revit tables:`n$after" }
        if ($ours[0].Command -ne $server) { throw "registering over '$spelling' did not point the table at the server:`n$after" }
        if ($after -notmatch '(?m)^\[profiles\.fast\] # the profile somebody uses$' -or $after -notmatch 'model = "fast-model"' -or
            $after -notmatch '\[mcp_servers\.keep-me\]') {
            throw "registering over '$spelling' lost the table after ours:`n$after"
        }

        & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') -Remove -Client Codex -Force *> $null
        if ($LASTEXITCODE -ne 0) { throw "removal after '$spelling' exited $LASTEXITCODE" }
        $after = [IO.File]::ReadAllText($codexPath)
        if (@(Get-HorizunTomlMcpSections -Lines @($after -split "`r?`n") | Where-Object { $_.Name -eq 'horizun-revit' }).Count -ne 0) {
            throw "removal after '$spelling' left the table:`n$after"
        }
        if ($after -notmatch '(?m)^\[profiles\.fast\] # the profile somebody uses$' -or $after -notmatch 'model = "fast-model"') {
            throw "removal after '$spelling' took the commented table after ours:`n$after"
        }

        # And the remover finds the quoted/commented spelling as written by hand.
        [IO.File]::WriteAllText($codexPath, (@($spelling, "command = 'old.exe'", '', '[profiles.fast] # c', 'model = "fast-model"', '') -join "`n"), $utf8)
        & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') -Remove -Client Codex -Force *> $null
        $after = [IO.File]::ReadAllText($codexPath)
        if ($LASTEXITCODE -ne 0 -or $after -match 'old\.exe' -or $after -notmatch '(?m)^\[profiles\.fast\] # c$' -or $after -notmatch 'fast-model') {
            throw "removal of the hand-written '$spelling' table failed (exit $LASTEXITCODE):`n$after"
        }
    }
    Write-Host '[PASS] a quoted or commented Codex header is found, replaced once and removed; a commented table after it survives (backlog #25)' -ForegroundColor Green
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:LOCALAPPDATA = $oldLocal
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
