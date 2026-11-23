#Requires -Version 5.1
<#
  install-status.json is replaced atomically even when the destination exists or is
  held open (Publish-HorizunAtomicFile, integration-status.lib.ps1).

  Field report (Pro 3.7.0, end-to-end install): the status ended in
  completion_failed with "Cannot create a file when that file already exists.
  Attempt 1 of 3", a generation-<guid>.json.tmp-... was left behind, and an earlier
  run had ended the same way with "file in use". These tests reproduce both causes
  in a temporary folder - never the real %LOCALAPPDATA%\Horizun\install-status.json.
#>
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'integration-status.lib.ps1'
. $lib

$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; if ($detail) { Write-Host "        $detail" }; $script:failed++ }
}
function Tmps($dir) { @(Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -like '*.tmp-*' }) }

$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-atomic-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $status = Join-Path $root 'install-status.json'

    # 1. The destination already exists: replaced, nothing left behind.
    Set-Content -LiteralPath $status -Value '{"state":"old"}' -Encoding UTF8
    Write-HorizunAtomicText -Path $status -Text '{"state":"new"}'
    Assert 'existing destination is replaced' ((Get-Content -LiteralPath $status -Raw) -match '"new"')
    Assert 'no temporary file is left after a replace' ((Tmps $root).Count -eq 0) ((Tmps $root) -join ', ')

    # 2. Read-only destination (a copy restored from a backup): still replaced.
    [IO.File]::SetAttributes($status, [IO.FileAttributes]::ReadOnly)
    Write-HorizunAtomicText -Path $status -Text '{"state":"after-readonly"}'
    Assert 'read-only destination is replaced' ((Get-Content -LiteralPath $status -Raw) -match 'after-readonly')

    # 3. The destination is held open by another process and released shortly after:
    #    the publish retries and succeeds (the "file in use" run).
    $job = Start-Job -ArgumentList $status -ScriptBlock {
        param($p)
        $fs = [IO.File]::Open($p, 'Open', 'Read', 'None')
        Start-Sleep -Milliseconds 700
        $fs.Dispose()
    }
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 50
        $locked = $false
        try { $probe = [IO.File]::Open($status, 'Open', 'Read', 'Read'); $probe.Dispose() } catch { $locked = $true }
    } until ($locked -or (Get-Date) -gt $deadline -or $job.State -ne 'Running')
    Assert 'fixture: the destination was locked by another process' $locked
    $error1 = $null
    try { Write-HorizunAtomicText -Path $status -Text '{"state":"after-lock"}' } catch { $error1 = $_.Exception.Message }
    $job | Wait-Job | Remove-Job
    Assert 'a briefly locked destination is replaced after retrying' ($null -eq $error1 -and (Get-Content -LiteralPath $status -Raw) -match 'after-lock') $error1
    Assert 'no temporary file is left after a retried replace' ((Tmps $root).Count -eq 0)

    # 4. Locked for longer than every retry: the call fails, its temporary is removed and
    #    the previous status is intact (never half-written, never a stray .tmp).
    $hold = [IO.File]::Open($status, 'Open', 'Read', 'None')
    $error2 = $null
    try { Publish-HorizunAtomicFile -TempPath ($t = "$status.tmp-held") -Destination $status -Attempts 3 -DelayMs 20 } catch { $error2 = $_.Exception.Message }
    $hold.Dispose()
    Assert 'a destination locked throughout fails loudly' ($null -ne $error2)
    Assert 'the failed publish removed its own temporary file' ((Tmps $root).Count -eq 0) ((Tmps $root) -join ', ')
    Assert 'the previous status survives a failed publish' ((Get-Content -LiteralPath $status -Raw) -match 'after-lock')

    # 5. Two writers at once (the registrar and the detached worker): the race that gave
    #    "Cannot create a file when that file already exists" with Move-Item -Force.
    $writers = 1..2 | ForEach-Object {
        Start-Job -ArgumentList $lib, $status, $_ -ScriptBlock {
            param($l, $p, $n)
            . $l
            $errors = @()
            for ($i = 0; $i -lt 40; $i++) {
                try { Write-HorizunAtomicText -Path $p -Text ('{"writer":' + $n + ',"i":' + $i + '}') } catch { $errors += $_.Exception.Message }
            }
            $errors
        }
    }
    $raceErrors = @($writers | Wait-Job | Receive-Job)
    $writers | Remove-Job
    Assert 'two concurrent writers never fail' ($raceErrors.Count -eq 0) ($raceErrors | Select-Object -First 3 | Out-String)
    $final = $null; try { $final = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json } catch { }
    Assert 'after the race the status is one whole document' ($null -ne $final -and $null -ne $final.writer)
    Assert 'after the race no temporary file is left' ((Tmps $root).Count -eq 0) ((Tmps $root) -join ', ')

    # 6. Orphans from a run that died: only this file's old temporaries are swept.
    $old = @("$status.tmp-dead1", "$status.generation-abc.json.tmp-dead2", "$status.current.tmp-dead3")
    foreach ($o in $old) { Set-Content -LiteralPath $o -Value 'x'; (Get-Item -LiteralPath $o).LastWriteTime = (Get-Date).AddHours(-2) }
    $fresh = "$status.tmp-inflight"; Set-Content -LiteralPath $fresh -Value 'x'
    $other = Join-Path $root 'someone-else.json.tmp-keep'; Set-Content -LiteralPath $other -Value 'x'
    (Get-Item -LiteralPath $other).LastWriteTime = (Get-Date).AddHours(-2)
    $removed = Remove-HorizunOrphanTempFiles -Path $status
    Assert 'old orphaned temporaries of the status file are removed' ($removed -eq 3 -and -not ($old | Where-Object { Test-Path -LiteralPath $_ })) "removed $removed"
    Assert 'an in-flight temporary is kept' (Test-Path -LiteralPath $fresh)
    Assert 'another file''s temporary is never touched' (Test-Path -LiteralPath $other)

    # 7. complete-install.ps1 and refresh-install-status.ps1 publish through the helper and
    #    no longer use a delete-then-move replace for the status files.
    $complete = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'complete-install.ps1') -Raw
    $refresh = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'refresh-install-status.ps1') -Raw
    Assert 'complete-install loads the atomic helper' ($complete -match "integration-status\.lib\.ps1'\)")
    Assert 'complete-install writes status atomically' (($complete -match 'Write-HorizunAtomicText -Path \$generationStatusPath') -and ($complete -match 'Write-HorizunAtomicText -Path \$StatusPath'))
    Assert 'complete-install no longer Move-Item/Copy-Item -Force onto status files' (-not ($complete -match 'Move-Item -LiteralPath \$tmp') -and -not ($complete -match 'Copy-Item -LiteralPath \$generationStatusPath'))
    Assert 'complete-install sweeps orphaned temporaries' ($complete -match 'Remove-HorizunOrphanTempFiles')
    Assert 'refresh-install-status writes atomically' ($refresh -match 'Write-HorizunAtomicText' -and -not ($refresh -match 'Move-Item -LiteralPath \$tmp'))

    # 8. A relative path lands in the current location even when the process directory
    #    differs ([IO.File] and the cmdlets used to resolve it against different folders).
    $sub = Join-Path $root 'rel'
    New-Item -ItemType Directory -Path $sub | Out-Null
    $savedProcessDir = [Environment]::CurrentDirectory
    Push-Location $sub
    try {
        [Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
        Write-HorizunAtomicText -Path 'relative-status.json' -Text '{"state":"relative"}'
    }
    finally { Pop-Location; [Environment]::CurrentDirectory = $savedProcessDir }
    $rel = Join-Path $sub 'relative-status.json'
    Assert 'relative path is written in the current location' ((Test-Path -LiteralPath $rel) -and ((Get-Content -LiteralPath $rel -Raw) -match '"relative"'))
    Assert 'no temporary file is left for a relative path' ((Tmps $sub).Count -eq 0) ((Tmps $sub) -join ', ')
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "$failed failed" -ForegroundColor Red; exit 1 }
Write-Host 'all passed' -ForegroundColor Green
exit 0
