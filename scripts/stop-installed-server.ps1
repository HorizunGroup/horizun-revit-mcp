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
$matches = @(
    Get-CimInstance Win32_Process -Filter "Name='horizun-mcp.exe'" -ErrorAction Stop |
        Where-Object {
            $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).TrimEnd('\') -ieq $target
        }
)

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
