#Requires -Version 5.1
# hz-call.ps1 argument handling, without a server and without Revit.
#
# The arguments are validated BEFORE any server is looked for, so every case here
# points -Server at a path that does not exist: a case that gets past argument
# validation reports "MCP server not found", one that does not reports why its
# arguments were refused - and nothing is ever started.
$ErrorActionPreference = 'Stop'
$hzCall = Join-Path $PSScriptRoot 'hz-call.ps1'
$noServer = Join-Path ([IO.Path]::GetTempPath()) ('hz-call-tests-no-server-' + [guid]::NewGuid().ToString('N') + '.exe')
$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; if ($detail) { Write-Host "        $detail" }; $script:failed++ }
}
function Invoke-HzCall([hashtable]$Params) {
    try { & $hzCall -Tool horizun_health -Server $noServer -Quiet @Params | Out-Null; return '' }
    catch { return $_.Exception.Message }
}

# The pattern seen in the field: a Windows path typed into hand-built JSON.
$badPath = '{"path":"C:\hz-live\model.rvt"}'
$why = Invoke-HzCall @{ Arguments = $badPath }
Assert 'an unescaped Windows path is refused before any server is sought' ($why -match 'arguments must be a JSON object') $why
Assert 'the refusal names the doubled-backslash fix and -ArgumentsObject' ($why -match 'must be doubled' -and $why -match 'ArgumentsObject') $why
Assert 'the refusal says nothing was sent' ($why -match 'Nothing was sent') $why

$why = Invoke-HzCall @{ Arguments = '{"path":"C:\\hz-live\\model.rvt"}' }
Assert 'a correctly escaped path passes argument validation' ($why -match 'MCP server not found') $why

$why = Invoke-HzCall @{ ArgumentsObject = @{ path = 'C:\hz-live\model.rvt'; ids = @(1, 2) } }
Assert '-ArgumentsObject serializes a hashtable with its Windows path intact' ($why -match 'MCP server not found') $why

$why = Invoke-HzCall @{ ArgumentsObject = @{ a = 1 }; Arguments = '{}' }
Assert 'two argument sources at once are refused' ($why -match 'exactly one of') $why

$why = Invoke-HzCall @{ Arguments = '[1,2]' }
Assert 'a JSON array is refused as not an object' ($why -match 'not Object|\(\{\.\.\.\}\)') $why

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('hz-call-tests-' + [guid]::NewGuid().ToString('N') + '.json')
(@{ path = 'C:\hz-live\model.rvt' } | ConvertTo-Json -Compress) | Set-Content -LiteralPath $tmp -Encoding utf8
$why = Invoke-HzCall @{ ArgumentsPath = $tmp }
Assert 'an -ArgumentsPath file written by ConvertTo-Json passes validation' ($why -match 'MCP server not found') $why
Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue

# ---- a reply PowerShell refuses although it is valid JSON is read, not waited out ----------
# A fake stdio server (a .cmd over a PowerShell script) answers initialize, then a tools/call
# whose structuredContent carries a property named "" and two names differing only in case.
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $fake = Join-Path ([IO.Path]::GetTempPath()) ('hz-call-fake-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $fake | Out-Null
    $init = '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"fake","version":"1"}}}'
    $reply = '{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ok"}],"structuredContent":{"by_load_case":{"":1,"DL1":2},"Mode":"a","mode":"b"},"isError":false}}'
    $serverLines = @(
        'while ($null -ne ($line = [Console]::In.ReadLine())) {',
        '    $m = $line | ConvertFrom-Json',
        ("    if (`$m.method -eq 'initialize') { [Console]::Out.WriteLine('" + $init + "'); [Console]::Out.Flush() }"),
        ("    elseif (`$m.method -eq 'tools/call') { [Console]::Out.WriteLine('" + $reply + "'); [Console]::Out.Flush() }"),
        '}')
    Set-Content -LiteralPath (Join-Path $fake 'server.ps1') -Encoding utf8 -Value $serverLines
    $pwshExe = (Get-Process -Id $PID).Path
    Set-Content -LiteralPath (Join-Path $fake 'server.cmd') -Encoding ascii -Value ('@"' + $pwshExe + '" -NoProfile -File "%~dp0server.ps1"')
    $outJson = Join-Path $fake 'out.json'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $pwshExe -NoProfile -File $hzCall -Tool horizun_health -Server (Join-Path $fake 'server.cmd') -Arguments '{}' -Json $outJson -TimeoutSec 60 -Quiet
    $code = $LASTEXITCODE; $clock.Stop()
    $saved = if (Test-Path -LiteralPath $outJson) { Get-Content -LiteralPath $outJson -Raw | ConvertFrom-Json } else { $null }
    Assert 'a reply with an empty key is read, not waited out (exit 0 well inside the timeout)' ($code -eq 0 -and $clock.Elapsed.TotalSeconds -lt 45) "exit $code after $($clock.Elapsed.TotalSeconds) s"
    Assert 'the saved file parses again, with the empty key renamed and the note set' ($saved -and $saved.result.by_load_case.'(empty key)' -eq 1 -and $saved.result.by_load_case.DL1 -eq 2 -and $saved.reply_parse_note -match 'empty key') ($saved | ConvertTo-Json -Depth 6 -Compress)
    Assert 'a key differing only in case keeps both values' ($saved -and $saved.result.Mode -eq 'a' -and $saved.result.'mode (case duplicate)' -eq 'b') ($saved.result | ConvertTo-Json -Compress)
    Remove-Item -LiteralPath $fake -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- a date-looking argument is SENT as the same text (backlog P0 #1) ------------------------
# hz-call parsed the arguments with ConvertFrom-Json - which, under PowerShell 6+, turns ISO
# 8601 text into a DateTime - and sent that object back out through ConvertTo-Json, so
# "2026-10-10T12:00:00+02:00" left as "2026-10-10T05:00:00-05:00" before the server ever saw
# it. The arguments are still validated, but what is sent is the text as given.
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $fake = Join-Path ([IO.Path]::GetTempPath()) ('hz-call-fake-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $fake | Out-Null
    $seen = Join-Path $fake 'seen.txt'
    $init = '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"fake","version":"1"}}}'
    $reply = '{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ok"}],"isError":false}}'
    $serverLines = @(
        'while ($null -ne ($line = [Console]::In.ReadLine())) {',
        ("    if (`$line -like '*tools/call*') { [IO.File]::WriteAllText('" + $seen + "', `$line) }"),
        '    $m = $line | ConvertFrom-Json',
        ("    if (`$m.method -eq 'initialize') { [Console]::Out.WriteLine('" + $init + "'); [Console]::Out.Flush() }"),
        ("    elseif (`$m.method -eq 'tools/call') { [Console]::Out.WriteLine('" + $reply + "'); [Console]::Out.Flush() }"),
        '}')
    Set-Content -LiteralPath (Join-Path $fake 'server.ps1') -Encoding utf8 -Value $serverLines
    $pwshExe = (Get-Process -Id $PID).Path
    Set-Content -LiteralPath (Join-Path $fake 'server.cmd') -Encoding ascii -Value ('@"' + $pwshExe + '" -NoProfile -File "%~dp0server.ps1"')
    & $pwshExe -NoProfile -File $hzCall -Tool horizun_health -Server (Join-Path $fake 'server.cmd') -Arguments '{"value":"2026-10-10T12:00:00+02:00","n":1.50}' -Json (Join-Path $fake 'out.json') -TimeoutSec 60 -Quiet
    $sent = if (Test-Path -LiteralPath $seen) { Get-Content -LiteralPath $seen -Raw } else { '' }
    Assert 'a date-looking argument leaves hz-call as the same text' ($sent -match [regex]::Escape('"value":"2026-10-10T12:00:00+02:00"')) $sent
    Remove-Item -LiteralPath $fake -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -eq 0) { Write-Host 'hz-call: ALL PASSED' -ForegroundColor Green; exit 0 }
Write-Host "hz-call: $failed FAILED" -ForegroundColor Red; exit 1
