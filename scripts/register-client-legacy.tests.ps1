#Requires -Version 5.1
<#
  Registering the current name removes what an earlier name or install of THIS
  product left in the same client, and nothing else.

  Everything runs against a temporary USERPROFILE. The real Claude Code and Codex
  configuration of the machine running this is never read or written, and the
  real `claude` CLI is never called: a fake one (a script that edits the temp
  ~/.claude.json) stands in for it.
#>
$ErrorActionPreference = 'Stop'
$registerScript = Join-Path $PSScriptRoot 'register-client.ps1'
. (Join-Path $PSScriptRoot 'mcp-legacy-registrations.lib.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-legacy-reg-' + [guid]::NewGuid().ToString('N'))
$oldProfile = $env:USERPROFILE
$oldLocal = $env:LOCALAPPDATA
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Fail($m) { throw $m }
function Assert($cond, $m) { if (-not $cond) { Fail $m } }
function Pass($m) { Write-Host "[PASS] $m" -ForegroundColor Green }

function New-Home([string]$name) {
    $h = Join-Path $root $name
    New-Item -ItemType Directory -Path (Join-Path $h '.codex') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $h 'local') -Force | Out-Null
    $env:USERPROFILE = $h
    $env:LOCALAPPDATA = Join-Path $h 'local'
    Assert-Sandboxed
    return $h
}

# The one thing this test must never do is touch the real configuration of the
# machine it runs on. Checked before every child process, not just once.
function Assert-Sandboxed {
    $sep = [string][IO.Path]::DirectorySeparatorChar
    $tmp = [IO.Path]::GetFullPath($root).TrimEnd($sep[0]) + $sep
    foreach ($v in @($env:USERPROFILE, $env:LOCALAPPDATA)) {
        if (-not $v -or -not ([IO.Path]::GetFullPath($v).TrimEnd($sep[0]) + $sep).StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase)) {
            throw "refusing to run: USERPROFILE/LOCALAPPDATA point outside the temporary root ($v)"
        }
    }
    if ($oldProfile -and ([IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd($sep[0]) -ieq [IO.Path]::GetFullPath($oldProfile).TrimEnd($sep[0]))) {
        throw 'refusing to run against the real user profile'
    }
}

function Invoke-Register([string[]]$ExtraArguments) {
    Assert-Sandboxed
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $registerScript, '-ServerPath', $server, '-Force') + $ExtraArguments
    $text = (& powershell @all 2>&1 | Out-String)
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
}

function Backups([string]$path) {
    @(Get-ChildItem (Split-Path -Parent $path) -Filter ((Split-Path -Leaf $path) + '.horizun-bak-*') -ErrorAction SilentlyContinue)
}

try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $server = Join-Path $root 'server\horizun-mcp.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $server) -Force | Out-Null
    [IO.File]::WriteAllBytes($server, [byte[]](1, 2, 3))
    $olderExe = 'C:\older\horizun-mcp.exe'
    $oldExe = 'C:\Users\someone\AppData\Local\Programs\Horizun\old\horizun-mcp.exe'
    $otherProduct = 'C:\tools\horizun-pbi-mcp.exe'

    # ----------------------------------------------------------------------------
    # 1. Library: the parser understands what real config files contain.
    # ----------------------------------------------------------------------------
    $h = Get-HorizunTomlHeader '[mcp_servers."horizun-next"]  # note'
    Assert ($h -and ($h.Keys -join '|') -eq 'mcp_servers|horizun-next' -and -not $h.IsArray) 'a quoted name with a trailing comment must parse as a header'
    $h = Get-HorizunTomlHeader "[mcp_servers.'horizun'.env]"
    Assert ($h -and ($h.Keys -join '|') -eq 'mcp_servers|horizun|env') 'a literal-quoted nested header must parse'
    Assert ((Get-HorizunTomlHeader '[[profiles]]').IsArray) 'an array-of-tables header is recognised'
    Assert ($null -eq (Get-HorizunTomlHeader 'key = [1, 2]')) 'a value with brackets is not a header'

    $lines = @(
        '[mcp_servers.horizun]',
        "command = '$server'",
        'notes = """',
        '[mcp_servers.keep-me]',
        '"""',
        '[mcp_servers.keep-me]',
        "command = 'other.exe'"
    )
    $sections = @(Get-HorizunTomlMcpSections -Lines $lines)
    Assert ($sections.Count -eq 2 -and $sections[0].Name -eq 'horizun' -and $sections[0].EndExclusive -eq 5) 'a header inside a multi-line string must not end or start a table'
    Pass 'TOML header parser: quoted names, comments, multi-line strings, arrays of tables'

    # ----------------------------------------------------------------------------
    # 2. Codex: removes exactly the legacy tables, keeps everything else byte for byte.
    # ----------------------------------------------------------------------------
    $h = New-Home 'codex'
    $codexPath = Join-Path $h '.codex\config.toml'
    $original = @(
        'model = "gpt-test"',
        '',
        '[mcp_servers.horizun]',
        "command = '$olderExe'",
        'args = []',
        '',
        '[mcp_servers.horizun.env]',
        'SAMPLE = "one"',
        '',
        '[[profiles]]',
        'name = "array-table-must-survive"',
        '',
        '[mcp_servers.keep-me]',
        "command = 'other.exe'",
        "args = ['x']",
        '',
        '[mcp_servers."horizun-next"] # the retired candidate name',
        ('command = "' + $oldExe.Replace('\','\\') + '"'),
        '',
        '[mcp_servers.renamed-by-hand]',
        "command = '$server'",
        '',
        '[mcp_servers.horizun-pbi]',
        "command = '$otherProduct'",
        '',
        '[mcp_servers.horizun-revit]',
        "command = 'C:\stale\horizun-mcp.exe'",
        'args = []'
    )
    # CRLF and a BOM: what Windows tools write, and what must come back unchanged.
    $crlfText = ($original -join "`r`n") + "`r`n"
    [IO.File]::WriteAllText($codexPath, $crlfText, (New-Object System.Text.UTF8Encoding($true)))
    $beforeBytes = [IO.File]::ReadAllBytes($codexPath)

    $r = Invoke-Register @('-Client', 'Codex')
    Assert ($r.Exit -eq 0) "registration failed: $($r.Text)"
    Assert ($r.Text -match 'removed legacy: horizun \[retired-name\]') "the output must say what it removed: $($r.Text)"
    Assert ($r.Text -match 'renamed-by-hand \[same-executable\]') 'the same executable under another name must be reported as removed'

    $after = [IO.File]::ReadAllBytes($codexPath)
    Assert ($after[0] -eq 0xEF -and $after[1] -eq 0xBB -and $after[2] -eq 0xBF) 'the BOM the file had must be kept'
    $afterText = [IO.File]::ReadAllText($codexPath)
    Assert ($afterText -notmatch "(?<!`r)`n") 'CRLF line endings must be kept throughout'
    $afterLines = @(Split-HorizunTextLines $afterText)
    $names = @(Get-HorizunTomlMcpSections -Lines $afterLines | ForEach-Object { $_.Name })
    Assert (($names -join ',') -eq 'keep-me,horizun-pbi,horizun-revit') "remaining servers were: $($names -join ',')"
    Assert ($afterText -notmatch 'horizun\.env' -and $afterText -notmatch 'horizun-next') 'nested table and quoted legacy table must go'
    Assert ($afterText -match 'model = "gpt-test"' -and $afterText -match '\[\[profiles\]\]' -and $afterText -match 'array-table-must-survive') 'unrelated content changed'
    Assert ($afterText -match [regex]::Escape($otherProduct)) 'another Horizun product must be untouched'
    Assert ($afterText -notmatch 'stale') 'the current name must point at the registered executable'

    # Nothing was ADDED: every line in the result is in the original, or belongs to the new table.
    $allowed = @($original) + @('', '[mcp_servers.horizun-revit]', ('command = ' + (ConvertTo-Json -InputObject $server -Compress)), 'args = []', 'startup_timeout_sec = 120', 'tool_timeout_sec = 600')
    $strangers = @($afterLines | Where-Object { $_ -notin $allowed })
    Assert ($strangers.Count -eq 0) "lines the edit should not have produced: $($strangers -join ' / ')"

    $bk = @(Backups $codexPath)
    Assert ($bk.Count -ge 1) 'a dated backup must exist before the edit'
    $oldest = $bk | Sort-Object Name | Select-Object -First 1
    Assert ((Get-FileHash $oldest.FullName).Hash -eq (Get-FileHash -InputStream ([IO.MemoryStream]::new($beforeBytes))).Hash) 'the backup must be the file as it was'

    $py = Test-HorizunTomlWithPython -Lines $afterLines
    if ($py -eq $false) { Fail 'the edited config does not parse as TOML' }
    Pass ("Codex: legacy tables removed (name + same executable + nested + quoted), rest intact, BOM/CRLF kept, backup taken; TOML check: " + $(if ($null -eq $py) { 'no tomllib here (structural check only)' } else { 'parses' }))

    # Idempotent: a second run changes nothing and takes no new backup.
    $hashBefore = (Get-FileHash $codexPath).Hash
    $countBefore = (Backups $codexPath).Count
    $r = Invoke-Register @('-Client', 'Codex')
    Assert ($r.Exit -eq 0) "second run failed: $($r.Text)"
    Assert ((Get-FileHash $codexPath).Hash -eq $hashBefore) 'a second run must leave the file byte for byte as it was'
    Assert ((Backups $codexPath).Count -eq $countBefore) 'a second run must not take another backup'
    Assert ($r.Text -notmatch 'removed legacy') 'a second run has nothing to remove'
    Pass 'Codex: idempotent (no change, no new backup on a second run)'

    # ----------------------------------------------------------------------------
    # 3. Codex: a retired name that launches something else is not ours.
    # ----------------------------------------------------------------------------
    $h = New-Home 'codex-foreign'
    $codexPath = Join-Path $h '.codex\config.toml'
    [IO.File]::WriteAllText($codexPath, ("[mcp_servers.horizun]`ncommand = 'C:\tools\something-else.exe'`n"), $utf8)
    $r = Invoke-Register @('-Client', 'Codex')
    Assert ($r.Exit -eq 0) "run failed: $($r.Text)"
    $t = [IO.File]::ReadAllText($codexPath)
    Assert ($t -match '\[mcp_servers\.horizun\]' -and $t -match 'something-else\.exe') 'a retired name that runs another program must be kept'
    Assert ($r.Text -match "kept 'horizun'") 'and reported as kept'
    Assert ($t -notmatch "`r") 'LF line endings must stay LF'
    Assert ([IO.File]::ReadAllBytes($codexPath)[0] -ne 0xEF) 'a file without BOM must not gain one'
    Pass 'Codex: a retired name running another program is kept and reported'

    # ----------------------------------------------------------------------------
    # 4. -KeepOtherEntries and -WhatIfOnly.
    # ----------------------------------------------------------------------------
    $h = New-Home 'codex-keep'
    $codexPath = Join-Path $h '.codex\config.toml'
    $seed = "[mcp_servers.horizun]`ncommand = '$server'`n"
    [IO.File]::WriteAllText($codexPath, $seed, $utf8)
    $r = Invoke-Register @('-Client', 'Codex', '-KeepOtherEntries')
    Assert ($r.Exit -eq 0 -and [IO.File]::ReadAllText($codexPath) -match '\[mcp_servers\.horizun\]') '-KeepOtherEntries must keep the other entry'

    [IO.File]::WriteAllText($codexPath, $seed, $utf8)
    $hash = (Get-FileHash $codexPath).Hash
    $n = (Backups $codexPath).Count
    $r = Invoke-Register @('-Client', 'Codex', '-WhatIfOnly')
    Assert ($r.Exit -eq 0 -and $r.Text -match 'would remove legacy: horizun') "-WhatIfOnly must say what it would remove: $($r.Text)"
    Assert ((Get-FileHash $codexPath).Hash -eq $hash -and (Backups $codexPath).Count -eq $n) '-WhatIfOnly must write nothing'
    Pass 'Codex: -KeepOtherEntries keeps, -WhatIfOnly reports and writes nothing'

    # ----------------------------------------------------------------------------
    # 5. Claude Code: removal goes through the CLI, never by editing ~/.claude.json.
    # ----------------------------------------------------------------------------
    $fake = Join-Path $root 'fake-claude.ps1'
    [IO.File]::WriteAllText($fake, @'
param([Parameter(ValueFromRemainingArguments = $true)]$Rest)
$log = Join-Path $env:USERPROFILE 'fake-claude.log'
Add-Content -LiteralPath $log -Value ($Rest -join ' ')
if ($Rest[0] -eq 'mcp' -and $Rest[1] -eq 'remove' -and $Rest[3] -eq '--scope' -and $Rest[4] -eq 'user') {
    $p = Join-Path $env:USERPROFILE '.claude.json'
    $cfg = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $cfg.mcpServers.PSObject.Properties.Remove($Rest[2])
    ($cfg | ConvertTo-Json -Depth 100) | Set-Content -LiteralPath $p -Encoding UTF8
    exit 0
}
exit 3
'@, $utf8)

    $claudeJson = @"
{
  "theme": "dark",
  "mcpServers": {
    "keep-me": { "command": "other.exe", "args": ["x"] },
    "horizun": { "command": "$($olderExe.Replace('\','\\'))", "args": [] },
    "renamed-by-hand": { "command": "$($server.Replace('\','\\'))", "args": [] },
    "horizun-next": { "command": "$($oldExe.Replace('\','\\'))", "args": [] },
    "horizun-pbi": { "command": "$($otherProduct.Replace('\','\\'))", "args": [] }
  },
  "projects": {
    "C:/work/demo": { "mcpServers": { "horizun": { "command": "$($server.Replace('\','\\'))" } } }
  }
}
"@
    $h = New-Home 'claude'
    $claudePath = Join-Path $h '.claude.json'
    [IO.File]::WriteAllText($claudePath, $claudeJson, $utf8)
    $r = Invoke-Register @('-Client', 'Claude', '-ClaudeCli', $fake)
    Assert ($r.Exit -eq 0) "registration failed: $($r.Text)"
    $cfg = Get-Content $claudePath -Raw | ConvertFrom-Json
    $have = @($cfg.mcpServers.PSObject.Properties.Name)
    Assert ('horizun' -notin $have -and 'horizun-next' -notin $have -and 'renamed-by-hand' -notin $have) "legacy entries remained: $($have -join ',')"
    Assert ('keep-me' -in $have -and 'horizun-pbi' -in $have -and 'horizun-revit' -in $have) "an unrelated entry was lost or the current one missing: $($have -join ',')"
    Assert ($cfg.theme -eq 'dark') 'unrelated top-level data changed'
    Assert ($cfg.projects.'C:/work/demo'.mcpServers.horizun) 'a project-scope entry must not be edited by hand'
    Assert ($r.Text -match 'project-scope' -and $r.Text -match 'claude mcp remove horizun --scope') 'the project-scope leftover must be reported with the command'
    $log = @(Get-Content (Join-Path $h 'fake-claude.log'))
    Assert (($log | Where-Object { $_ -match '^mcp remove horizun --scope user$' }).Count -eq 1 -and
            ($log | Where-Object { $_ -match '^mcp remove renamed-by-hand --scope user$' }).Count -eq 1 -and
            ($log | Where-Object { $_ -match '^mcp remove horizun-next --scope user$' }).Count -eq 1) "the CLI calls were: $($log -join ' / ')"
    Assert (($log | Where-Object { $_ -match 'keep-me|horizun-pbi|horizun-revit' }).Count -eq 0) 'the CLI must be asked only about the legacy entries'
    Assert ((Backups $claudePath).Count -ge 1) 'a backup of ~/.claude.json must exist'
    Pass 'Claude Code: legacy user-scope entries removed through the CLI; others, top-level data and project scope untouched'

    # Idempotent: nothing left to remove, so the CLI is not called again.
    Remove-Item (Join-Path $h 'fake-claude.log')
    $r = Invoke-Register @('-Client', 'Claude', '-ClaudeCli', $fake)
    Assert ($r.Exit -eq 0 -and -not (Test-Path (Join-Path $h 'fake-claude.log'))) 'a second run must not call the CLI'
    Pass 'Claude Code: idempotent'

    # No CLI: nothing is removed by hand, and it says so.
    $h = New-Home 'claude-nocli'
    $claudePath = Join-Path $h '.claude.json'
    [IO.File]::WriteAllText($claudePath, $claudeJson, $utf8)
    $r = Invoke-Register @('-Client', 'Claude', '-ClaudeCli', (Join-Path $root 'no-such-claude.exe'))
    Assert ($r.Exit -eq 0) "registration must still succeed without the CLI: $($r.Text)"
    $have = @((Get-Content $claudePath -Raw | ConvertFrom-Json).mcpServers.PSObject.Properties.Name)
    Assert ('horizun' -in $have -and 'horizun-next' -in $have -and 'horizun-revit' -in $have) 'without the CLI the legacy entries stay and the current one is added'
    Assert ($r.Text -match 'claude CLI was not found' -and $r.Text -match 'claude mcp remove horizun --scope user') 'it must tell the person what to run'
    Pass 'Claude Code: without the CLI nothing is edited by hand and the command to run is named'

    # A failing CLI call is a warning, not a half-done registration.
    $failing = Join-Path $root 'failing-claude.ps1'
    [IO.File]::WriteAllText($failing, 'exit 7', $utf8)
    $h = New-Home 'claude-failcli'
    $claudePath = Join-Path $h '.claude.json'
    [IO.File]::WriteAllText($claudePath, $claudeJson, $utf8)
    $r = Invoke-Register @('-Client', 'Claude', '-ClaudeCli', $failing)
    Assert ($r.Exit -eq 0 -and $r.Text -match 'exited 7') "a failing CLI must be reported: $($r.Text)"
    $have = @((Get-Content $claudePath -Raw | ConvertFrom-Json).mcpServers.PSObject.Properties.Name)
    Assert ('horizun-revit' -in $have -and 'keep-me' -in $have) 'registration must complete and keep the others'
    Pass 'Claude Code: a failing CLI call is reported with the manual command and does not break registration'

    # ----------------------------------------------------------------------------
    # 5b. Claude Desktop: same rule over claude_desktop_config.json.
    # ----------------------------------------------------------------------------
    $null = New-Home 'desktop'
    $roaming = Join-Path $root 'desktop-roaming'
    New-Item -ItemType Directory -Path $roaming -Force | Out-Null
    $desktopConfig = Join-Path $roaming 'claude_desktop_config.json'
    @"
{
  "preferences": { "menuBarEnabled": true },
  "mcpServers": {
    "someone-elses": { "command": "C:\\other.exe" },
    "horizun": { "command": "$($olderExe.Replace('\','\\'))" },
    "horizun-pbi": { "command": "$($otherProduct.Replace('\','\\'))" }
  }
}
"@ | Set-Content -LiteralPath $desktopConfig -Encoding UTF8
    $desktopScript = Join-Path $PSScriptRoot 'install-claude-desktop-extension.ps1'
    Assert-Sandboxed
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $desktopScript -ConfigFallback `
        -RoamingOverride $roaming -ServerPath $server -StatusPath (Join-Path $root 'desktop-status.json') -Force 2>&1
    $dcfg = Get-Content -LiteralPath $desktopConfig -Raw | ConvertFrom-Json
    $have = @($dcfg.mcpServers.PSObject.Properties.Name)
    Assert ('horizun' -notin $have) "the retired name must go from the desktop config: $($have -join ',')"
    Assert ('horizun-revit' -in $have -and 'someone-elses' -in $have -and 'horizun-pbi' -in $have) "current entry missing or another server lost: $($have -join ',')"
    Assert ($dcfg.preferences.menuBarEnabled -eq $true) 'unrelated top-level data changed'
    Assert ((Backups $desktopConfig).Count -ge 1) 'a backup must exist'
    Pass 'Claude Desktop: the retired name is removed with the current entry written; other servers and keys intact'

    # ----------------------------------------------------------------------------
    # 6. Both clients in one run, atomic restore still intact.
    # ----------------------------------------------------------------------------
    $h = New-Home 'both'
    $claudePath = Join-Path $h '.claude.json'
    $codexPath = Join-Path $h '.codex\config.toml'
    [IO.File]::WriteAllText($claudePath, $claudeJson, $utf8)
    [IO.File]::WriteAllText($codexPath, "[mcp_servers.horizun]`ncommand = '$server'`n`n[mcp_servers.keep-me]`ncommand = 'o.exe'`n", $utf8)
    $jsonReport = Join-Path $h 'report.json'
    $r = Invoke-Register @('-Client', 'Both', '-ClaudeCli', $fake, '-Json', $jsonReport)
    Assert ($r.Exit -eq 0) "both failed: $($r.Text)"
    $rep = Get-Content $jsonReport -Raw | ConvertFrom-Json
    $paths = @($rep.writes | ForEach-Object { $_.Path })
    Assert (@($paths | Select-Object -Unique).Count -eq $paths.Count) 'each file must appear once in the writes the deferred completion restores from'
    foreach ($w in @($rep.writes)) {
        Assert ((Get-FileHash $w.Path).Hash -eq $w.CurrentHash) 'the recorded hash must be the file as finally written'
    }
    Pass 'Both clients: one write entry per file with the final hash (the deferred restore keeps working)'

    Write-Host 'All legacy-registration tests passed.' -ForegroundColor Green
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:LOCALAPPDATA = $oldLocal
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
