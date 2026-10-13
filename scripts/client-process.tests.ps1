#Requires -Version 5.1
<#
  Which running process is which client. Audit 2026-10-08: the helpers matched
  the process NAME "claude", which is both Claude Desktop (Claude.exe in its
  Store package) and Claude Code (claude.exe, including the copy Claude Desktop
  runs for its Code tab) - so an open Desktop app blocked Claude Code's
  registration and an open Claude Code was reported as "Claude Desktop is
  RUNNING". The shapes below are Win32_Process rows measured on a real machine
  on 2026-10-08, with the account name replaced.
#>
$ErrorActionPreference = 'Stop'
$failed = 0
function Assert([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name" -ForegroundColor Red; if ($Detail) { Write-Host "        $Detail" }; $script:failed++ }
}

. (Join-Path $PSScriptRoot 'client-process.lib.ps1')

function Row($Name, $Path, $Cmd) { [pscustomobject]@{ Name = $Name; ExecutablePath = $Path; CommandLine = $Cmd } }

$desktop = Row 'claude.exe' 'C:\Program Files\WindowsApps\Claude_2.26454.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe' '"C:\Program Files\WindowsApps\Claude_2.26454.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe"'
$desktopClassic = Row 'claude.exe' 'C:\Users\someone\AppData\Local\AnthropicClaude\app-1.0.0\claude.exe' $null
$bundledCode = Row 'claude.exe' 'C:\Users\someone\AppData\Roaming\Claude\claude-code\2.1.293\83cb0bd7fed4\claude.exe' 'C:\Users\someone\AppData\Roaming\Claude\claude-code\2.1.293\83cb0bd7fed4\claude.exe --output-format stream-json'
$nativeCode = Row 'claude.exe' 'C:\Users\someone\.local\bin\claude.exe' 'claude'
$npmCode = Row 'node.exe' 'C:\Program Files\nodejs\node.exe' 'node C:\Users\someone\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js'
$otherNode = Row 'node.exe' 'C:\Program Files\nodejs\node.exe' 'node C:\Civil3DMCP\build\index.js'
$codex = Row 'codex.exe' 'C:\Users\someone\AppData\Local\OpenAI\Codex\bin\9691020b546a15b2\codex.exe' 'codex.exe app-server'
$hidden = Row 'claude.exe' $null $null

Assert 'Store Claude Desktop is Claude Desktop' ((Get-HorizunClientKind $desktop) -eq 'ClaudeDesktop') (Get-HorizunClientKind $desktop)
Assert 'classic Claude Desktop is Claude Desktop' ((Get-HorizunClientKind $desktopClassic) -eq 'ClaudeDesktop') (Get-HorizunClientKind $desktopClassic)
Assert 'the Claude Code that Desktop runs is Claude Code' ((Get-HorizunClientKind $bundledCode) -eq 'ClaudeCode') (Get-HorizunClientKind $bundledCode)
Assert 'native claude.exe is Claude Code' ((Get-HorizunClientKind $nativeCode) -eq 'ClaudeCode') (Get-HorizunClientKind $nativeCode)
Assert 'node running @anthropic-ai/claude-code is Claude Code' ((Get-HorizunClientKind $npmCode) -eq 'ClaudeCode') (Get-HorizunClientKind $npmCode)
Assert 'an unrelated node process is no client' ($null -eq (Get-HorizunClientKind $otherNode)) (Get-HorizunClientKind $otherNode)
Assert 'codex.exe is Codex' ((Get-HorizunClientKind $codex) -eq 'Codex') (Get-HorizunClientKind $codex)
Assert 'a claude.exe whose path cannot be read counts as Claude Code (waiting is the safe side)' ((Get-HorizunClientKind $hidden) -eq 'ClaudeCode') (Get-HorizunClientKind $hidden)

Assert 'only Desktop open: Claude Code is not running' (-not (Test-HorizunClientRunning 'ClaudeCode' @($desktop, $otherNode))) $null
Assert 'only Desktop open: Claude Desktop is running' (Test-HorizunClientRunning 'ClaudeDesktop' @($desktop, $otherNode)) $null
Assert 'only Claude Code open: Claude Desktop is not running' (-not (Test-HorizunClientRunning 'ClaudeDesktop' @($nativeCode, $npmCode))) $null
Assert 'Desktop with its Code tab: both are running' ((Test-HorizunClientRunning 'ClaudeCode' @($desktop, $bundledCode)) -and (Test-HorizunClientRunning 'ClaudeDesktop' @($desktop, $bundledCode))) $null
Assert 'live enumeration answers without throwing' ($null -ne (Test-HorizunClientRunning 'Codex')) $null

if ($failed -gt 0) { Write-Host "client-process: $failed FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'client-process: PASS' -ForegroundColor Green
exit 0
