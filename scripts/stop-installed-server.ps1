#Requires -Version 5.1
<#
  Stop only MCP server processes running from the exact installed path.

  A release update is intentionally allowed while Claude/Codex is open: those
  clients keep the server image loaded even though Revit is closed, and Windows
  then refuses the directory swap. Setup calls this helper only after its
  Revit-running guard passed. The client itself is never stopped; completion is
  deferred until the client exits normally.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ServerPath,
    [int]$WaitSeconds = 10
)
$ErrorActionPreference = 'Stop'
if ($WaitSeconds -lt 1 -or $WaitSeconds -gt 60) { throw 'WaitSeconds must be between 1 and 60.' }

$target = [IO.Path]::GetFullPath($ServerPath).TrimEnd('\')
$clientTools = Join-Path (Split-Path -Parent $target) 'client-tools'
$completionScript = [IO.Path]::GetFullPath((Join-Path $clientTools 'complete-install.ps1'))
$matches = @(
    Get-CimInstance Win32_Process -Filter "Name='horizun-mcp.exe'" -ErrorAction Stop |
        Where-Object {
            $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).TrimEnd('\') -ieq $target
        }
)

# A detached completion worker can wait for a client to close for up to a day.
# Its current directory/script path keeps client-tools (and therefore the whole
# server directory) from being renamed on Windows. Stop only workers whose
# command line names both this exact complete-install script and this exact
# installed server; the new installation schedules its own generation.
$completionWorkers = @(
    Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object {
            $_.Name -in @('powershell.exe', 'pwsh.exe') -and
            $_.CommandLine -and
            $_.CommandLine.IndexOf($completionScript, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $_.CommandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0
        }
)

foreach ($process in $completionWorkers) {
    Write-Host "[Horizun] stopping superseded completion worker pid $($process.ProcessId) for update"
    Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
}

foreach ($process in $matches) {
    Write-Host "[Horizun] stopping installed MCP server pid $($process.ProcessId) for update"
    Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
}

$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    $remaining = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='horizun-mcp.exe'" -ErrorAction SilentlyContinue)) {
        if ($process.ExecutablePath -and
            [IO.Path]::GetFullPath($process.ExecutablePath).TrimEnd('\') -ieq $target) {
            $remaining += $process.ProcessId
        }
    }
    if ($remaining.Count -eq 0) { exit 0 }
    Start-Sleep -Milliseconds 200
} while ((Get-Date) -lt $deadline)

Write-Error "Installed Horizun MCP server processes did not stop: $($remaining -join ', ')"
exit 1
