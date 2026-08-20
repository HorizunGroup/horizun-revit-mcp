#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$admin = Join-Path $PSScriptRoot 'enable-execute-python.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('horizun-python-permission-' + [Guid]::NewGuid().ToString('N'))
$marker = Join-Path $temp 'mutex-held'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $settingsPath = Join-Path $temp 'settings.json'
    [IO.File]::WriteAllText($settingsPath,
        '{"permission_profile":"unsafe_code","enable_execute_python":true,"execute_python_ui_grant_until_utc":"2099-01-01T00:00:00Z","unrelated":42}',
        (New-Object Text.UTF8Encoding($false)))

    # A different process owns the exact production mutex. The admin operation
    # must wait and then re-read; an in-process Monitor/Task test cannot prove it.
    $escapedMarker = $marker.Replace("'", "''")
    $holderCode = @"
`$m = New-Object Threading.Mutex(`$false, 'Local\Horizun.Revit.Settings.V1')
`$held = `$false
try {
  try { `$held = `$m.WaitOne([TimeSpan]::FromSeconds(10)) } catch [Threading.AbandonedMutexException] { `$held = `$true }
  if (-not `$held) { exit 3 }
  [IO.File]::WriteAllText('$escapedMarker', 'held')
  Start-Sleep -Milliseconds 1200
} finally {
  if (`$held) { `$m.ReleaseMutex() }
  `$m.Dispose()
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($holderCode))
    $onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    $shell = if ($onWindows) {
        Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    } else {
        (Get-Process -Id $PID).Path
    }
    $holderArgs = @('-NoProfile','-EncodedCommand',$encoded)
    $start = @{ FilePath=$shell; PassThru=$true; ArgumentList=$holderArgs }
    if ($onWindows) { $start.WindowStyle = 'Hidden' }
    $holder = Start-Process @start
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    Assert-True (Test-Path -LiteralPath $marker) 'mutex holder did not start'

    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $shell -NoProfile -File $admin -Disable -Yes -DataRoot $temp
    if ($LASTEXITCODE -ne 0) { throw "disable exited $LASTEXITCODE" }
    $sw.Stop()
    $holder.WaitForExit()
    Assert-True ($holder.ExitCode -eq 0) "mutex holder exited $($holder.ExitCode)"
    Assert-True ($sw.ElapsedMilliseconds -ge 750) 'disable did not wait for the inter-process settings mutex'

    $disabled = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Assert-True ($disabled.enable_execute_python -eq $false) 'durable Python switch was not disabled'
    Assert-True ($null -eq $disabled.PSObject.Properties['execute_python_ui_grant_until_utc']) 'temporary grant survived disable'
    Assert-True ($disabled.unrelated -eq 42) 'unrelated setting was not preserved'

    & $shell -NoProfile -File $admin -Yes -DataRoot $temp
    if ($LASTEXITCODE -ne 0) { throw "enable exited $LASTEXITCODE" }
    $enabled = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Assert-True ($enabled.permission_profile -eq 'unsafe_code') 'durable enable did not select unsafe_code'
    Assert-True ($enabled.enable_execute_python -eq $true) 'durable enable did not set explicit true'
    Assert-True ($null -eq $enabled.PSObject.Properties['execute_python_ui_grant_until_utc']) 'durable enable left a temporary grant'
    Assert-True ($enabled.unrelated -eq 42) 'durable enable lost unrelated settings'

    Write-Host '[PASS] Python permission administration is inter-process serialized, reversible and preserving' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
