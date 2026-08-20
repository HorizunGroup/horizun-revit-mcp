#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-uninstall-cleanup-' + [guid]::NewGuid().ToString('N'))
$oldProfile = $env:USERPROFILE
$oldDataRoot = $env:HORIZUN_DATA_ROOT
try {
    $profile = Join-Path $root 'profile'
    $outside = Join-Path $root 'documents'
    New-Item -ItemType Directory -Path $profile, $outside -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'keep'
    $env:USERPROFILE = $profile
    $env:HORIZUN_DATA_ROOT = $outside
    $hostExe = (Get-Process -Id $PID).Path
    $cleanupScript = (Join-Path $PSScriptRoot 'uninstall-cleanup.ps1').Replace("'", "''")
    # Native-process argument arrays do not preserve PowerShell's `-Confirm:$false`
    # syntax: Windows PowerShell receives the whole token as a String and cannot
    # bind it to SwitchParameter. Parse the switch expression in the child host so
    # this test reaches the cleanup logic it is meant to exercise.
    $command = "& '$cleanupScript' -PurgeState -Force -Confirm:`$false"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $invoke = @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encodedCommand)
    $process = Start-Process -FilePath $hostExe -ArgumentList $invoke -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $outside 'keep.txt'))) {
        throw 'an unmarked custom data root was not refused intact'
    }
    Write-Host '  PASS  unmarked custom HORIZUN_DATA_ROOT is never recursively deleted'

    Set-Content -LiteralPath (Join-Path $outside '.horizun-data-root') -Value 'Horizun data root v1'
    $externalTarget = Join-Path $root 'must-not-be-traversed'
    New-Item -ItemType Directory -Path $externalTarget -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $externalTarget 'keep.txt') -Value 'keep'
    $junction = Join-Path $outside 'redirect'
    New-Item -ItemType Junction -Path $junction -Target $externalTarget | Out-Null
    $process = Start-Process -FilePath $hostExe -ArgumentList $invoke -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $externalTarget 'keep.txt')) -or
        -not (Test-Path -LiteralPath $outside)) { throw 'a marked root containing a junction was not refused intact' }
    Write-Host '  PASS  marked state cannot redirect recursive purge through a junction'
    # Windows PowerShell 5.1 can throw NullReferenceException for Remove-Item on
    # a junction. This path was created immediately above inside our unique temp
    # root, so delete the link node itself without traversing its target.
    [IO.Directory]::Delete($junction, $false)

    $process = Start-Process -FilePath $hostExe -ArgumentList $invoke -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $outside)) { throw 'marked custom root was not purged' }
    Write-Host '  PASS  explicitly marked custom data root can be purged'
}
finally {
    $env:USERPROFILE = $oldProfile
    $env:HORIZUN_DATA_ROOT = $oldDataRoot
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
