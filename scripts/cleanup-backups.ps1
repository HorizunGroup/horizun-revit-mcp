#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [switch]$PreservePrevious)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backup-retention.lib.ps1')
$mutex = New-Object System.Threading.Mutex($false, 'Local\Horizun.Revit.MCP.Install')
$held = $false
try {
    try { $held = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { Write-Warning 'Another installation owns the rollback ledger; retention is deferred.'; exit 0 }
    if ($PreservePrevious) {
        $previous = Join-Path $Root 'server.previous'
        if (Test-Path -LiteralPath $previous -PathType Container) {
            $dest = Join-Path $Root ('.legacy-backup-' + [guid]::NewGuid().ToString('N'))
            Move-Item -LiteralPath $previous -Destination $dest -ErrorAction Stop
        }
    }
    foreach ($removed in @(Remove-HorizunOldBackups -Root $Root)) {
        $line = "$(Get-Date -Format o) removed backup: $removed"
        Add-Content -LiteralPath (Join-Path $Root 'backup-retention.log') -Value $line -Encoding UTF8
        Write-Host $line
    }
} finally {
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
