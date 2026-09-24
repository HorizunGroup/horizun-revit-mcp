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

if ($failed -eq 0) { Write-Host 'hz-call: ALL PASSED' -ForegroundColor Green; exit 0 }
Write-Host "hz-call: $failed FAILED" -ForegroundColor Red; exit 1
