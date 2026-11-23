$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backup-retention.lib.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-retention-test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $names = @('.legacy-backup-1', '.install-rollback-2', 'replaced-3', 'server.bak-4')
    for ($i = 0; $i -lt $names.Count; $i++) {
        $d = New-Item -ItemType Directory -Path (Join-Path $root $names[$i])
        $d.LastWriteTimeUtc = [datetime]::UtcNow.AddMinutes(-$i)
    }
    $removed = @(Remove-HorizunOldBackups -Root $root -InUse { param($path) $path.EndsWith('server.bak-4') })
    if ($removed.Count -ne 1 -or -not $removed[0].EndsWith('replaced-3')) { throw "Unexpected removals: $removed" }
    if (-not (Test-Path (Join-Path $root 'server.bak-4'))) { throw 'In-use backup was removed' }
    if (-not (Test-Path (Join-Path $root '.legacy-backup-1')) -or -not (Test-Path (Join-Path $root '.install-rollback-2'))) { throw 'Newest backups were removed' }
    $locked = Join-Path $root 'server.bak-4\hz-retention-test-image.exe'
    [IO.File]::WriteAllText($locked, 'test image')
    (Get-Item (Join-Path $root 'server.bak-4')).LastWriteTimeUtc = [datetime]::UtcNow.AddHours(-1)
    $stream = [IO.File]::Open($locked, 'Open', 'Read', 'Read')
    try {
        $removed = @(Remove-HorizunOldBackups -Root $root)
        if (-not (Test-Path $locked) -or $removed.Count -ne 0) { throw 'Locked image was removed' }
    } finally { $stream.Dispose() }
    $removed = @(Remove-HorizunOldBackups -Root $root)
    if ($removed.Count -ne 1 -or (Test-Path (Join-Path $root 'server.bak-4'))) { throw 'Unlocked old backup was not removed' }
    Write-Host 'Backup retention tests passed.'
} finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
