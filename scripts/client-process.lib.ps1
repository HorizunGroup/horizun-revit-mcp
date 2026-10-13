#Requires -Version 5.1
<#
  Horizun MCP - original Horizun code.

  WHICH RUNNING PROCESS IS WHICH CLIENT.

  The helpers that must not edit a configuration underneath a running client used
  to match the process NAME "claude". That name is two products (audit 2026-10-08,
  measured on a real machine):

    Claude Desktop  ...\WindowsApps\Claude_<version>_x64__<publisher>\app\Claude.exe
                    (or %LOCALAPPDATA%\AnthropicClaude\... for the classic installer)
                    - owns claude_desktop_config.json.
    Claude Code     claude.exe anywhere else - including
                    %APPDATA%\Claude\claude-code\<version>\claude.exe, the copy
                    Claude Desktop runs for its Code tab - or node.exe running
                    @anthropic-ai/claude-code. Owns ~/.claude.json.

  So an open Desktop app blocked Claude Code's registration for no reason, and an
  open Claude Code was reported as "Claude Desktop is RUNNING". Classification is
  by executable path and command line instead. A claude.exe whose path cannot be
  read is counted as Claude Code: waiting for a client that is not there costs a
  retry; editing underneath one that is costs the edit.
#>

function Get-HorizunClientKind($Process) {
    # 'ClaudeDesktop', 'ClaudeCode', 'Codex' or $null, for one Win32_Process-shaped row
    # (Name, ExecutablePath, CommandLine).
    $name = ([string]$Process.Name).ToLowerInvariant()
    $path = [string]$Process.ExecutablePath
    $command = [string]$Process.CommandLine
    if ($name -eq 'claude.exe' -or $name -eq 'claude-desktop.exe' -or $name -eq 'anthropicclaude.exe') {
        if ($name -ne 'claude.exe') { return 'ClaudeDesktop' }
        if ($path -match '(?i)\\WindowsApps\\Claude_[^\\]*\\' -or $path -match '(?i)\\AnthropicClaude\\') { return 'ClaudeDesktop' }
        return 'ClaudeCode'
    }
    if ($name -eq 'node.exe' -or $name -eq 'bun.exe') {
        if ($command -match '(?i)@anthropic-ai[\\/]claude-code') { return 'ClaudeCode' }
        return $null
    }
    if ($name -eq 'codex.exe' -or $name -eq 'openai.codex.exe') { return 'Codex' }
    return $null
}

function Get-HorizunProcessRows {
    try {
        return @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
                 Select-Object Name, ExecutablePath, CommandLine)
    }
    catch {
        # No CIM (a locked-down session): names only, which classifies every
        # claude.exe as Claude Code - the side that waits rather than writes.
        return @(Get-Process -ErrorAction SilentlyContinue |
                 ForEach-Object { [pscustomobject]@{ Name = $_.ProcessName + '.exe'; ExecutablePath = $null; CommandLine = $null } })
    }
}

function Test-HorizunClientRunning([string]$Kind, $Processes) {
    # Is a process of this kind running? $Processes is for tests; omitted, the
    # machine's own process list is read.
    if ($null -eq $Processes) { $Processes = Get-HorizunProcessRows }
    foreach ($p in @($Processes)) {
        if ((Get-HorizunClientKind $p) -eq $Kind) { return $true }
    }
    return $false
}
