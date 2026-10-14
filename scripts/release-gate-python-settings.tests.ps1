#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'release-gate-python-settings.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-gate-python-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $owner = Join-Path $root 'owner.json'
    $isolated = Join-Path $root 'isolated.json'
    $original = '{"permission_profile":"unsafe_code","enable_execute_python":false,"owner_key":"untouched"}'
    [IO.File]::WriteAllText($owner, $original)
    & $helper -OwnerSettings $owner -IsolatedSettings $isolated -Mode Start
    $active = Get-Content $isolated -Raw | ConvertFrom-Json
    if ($active.permission_profile -ne 'unsafe_code' -or $active.enable_execute_python -ne $true -or $active.owner_key -ne 'untouched') {
        throw 'Gate Python was not enabled only in the isolated settings.'
    }
    if ([IO.File]::ReadAllText($owner) -cne $original) { throw 'Owner settings changed during the gate.' }
    & $helper -OwnerSettings $owner -IsolatedSettings $isolated -Mode Restore
    $restored = Get-Content $isolated -Raw | ConvertFrom-Json
    if ($restored.enable_execute_python -ne $false -or $restored.owner_key -ne 'untouched') {
        throw 'Gate Python was not restored to the owner setting.'
    }
    if ([IO.File]::ReadAllText($owner) -cne $original) { throw 'Owner settings changed after the gate.' }
    Remove-Item $owner
    & $helper -OwnerSettings $owner -IsolatedSettings $isolated -Mode Start
    $active = Get-Content $isolated -Raw | ConvertFrom-Json
    if ($active.permission_profile -ne 'unsafe_code' -or $active.enable_execute_python -ne $true) {
        throw 'Gate Python did not start in an isolated root without owner settings.'
    }
    & $helper -OwnerSettings $owner -IsolatedSettings $isolated -Mode Restore
    if (Test-Path $isolated) { throw 'Isolated settings were not removed when owner settings were absent.' }
    Write-Host 'release-gate-python-settings tests passed'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
