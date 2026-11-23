function Remove-HorizunOldBackups {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [scriptblock]$InUse)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $ancestor = Get-Item -LiteralPath $rootFull -Force
    while ($ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Backup root traverses a reparse point: $($ancestor.FullName)" }
        $ancestor = $ancestor.Parent
    }
    $backups = @(Get-ChildItem -LiteralPath $rootFull -Directory -Force | Where-Object {
        $_.Name -like '.legacy-backup-*' -or $_.Name -like '.install-rollback-*' -or
        $_.Name -like 'replaced-*' -or $_.Name -like 'server.bak-*'
    } | Sort-Object LastWriteTimeUtc -Descending)
    foreach ($item in @($backups | Select-Object -Skip 2)) {
        $path = [IO.Path]::GetFullPath($item.FullName)
        if (-not $path.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        try {
            $children = @(Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction Stop)
            if (@($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { continue }
            if ($InUse) { if (& $InUse $path) { continue } }
            else {
                $busy = $false
                foreach ($exe in @($children | Where-Object { -not $_.PSIsContainer -and $_.Extension -ieq '.exe' })) {
                    foreach ($process in @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe.Name)) -ErrorAction SilentlyContinue)) {
                        try {
                            $processPath = $process.MainModule.FileName
                            if (-not $processPath -or $processPath.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase)) { $busy = $true }
                        } catch { $busy = $true }
                    }
                    try { $handle = [IO.File]::Open($exe.FullName, 'Open', 'ReadWrite', 'None'); $handle.Dispose() }
                    catch { $busy = $true }
                }
                if ($busy) { continue }
            }
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            Write-Output $path
        } catch { Write-Warning "Backup retained: $path ($($_.Exception.Message))" }
    }
}
