#Requires -Version 5.1
<#
  A client's JSON configuration survives registration byte for byte.

  ~/.claude.json is written by Node as BOM-less UTF-8, two-space indented, and on
  a well-used machine it carries project keys that differ only in case. Under
  Windows PowerShell 5.1 the registration helper used to read it as ANSI (an accented
  name came back as mojibake), write it back with a BOM and re-serialised, and refuse it outright
  when two keys differed only in case (audit 2026-10-08).

  The contract tested here: add then remove leaves the file BYTE-IDENTICAL to the
  original; after the add, the only difference is the horizun-revit member; never
  a BOM. Runs under whichever PowerShell launched it - run it under both.
#>
$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-config-encoding-' + [guid]::NewGuid().ToString('N'))
$oldProfile = $env:USERPROFILE
$hostExe = (Get-Process -Id $PID).Path
$failed = 0

function Assert([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" }
        $script:failed++
    }
}

function Same-Bytes([byte[]]$A, [byte[]]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}

. (Join-Path $PSScriptRoot 'json-member.lib.ps1')

try {
    Write-Host "client-config-encoding under PowerShell $($PSVersionTable.PSVersion)"
    $env:USERPROFILE = Join-Path $root 'profile'
    New-Item -ItemType Directory -Path $env:USERPROFILE -Force | Out-Null
    $server = Join-Path $root 'server dir\horizun-mcp.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $server) -Force | Out-Null
    'fake server' | Set-Content -LiteralPath $server -Encoding ASCII

    # Written exactly as Node writes it: UTF-8, no BOM, LF, two spaces. Non-ASCII in
    # values and keys, and two project keys that differ only in case.
    $original = "{`n  `"userName`": `"Jos" + [char]0xE9 + "`",`n  `"theme`": `"configuraci" + [char]0xF3 + "n oscura`",`n" +
                "  `"projects`": {`n    `"C:/Users/demo/obra`": {`n      `"allowedTools`": []`n    },`n" +
                "    `"c:/Users/demo/obra`": {`n      `"allowedTools`": [`n        `"Bash`"`n      ]`n    }`n  },`n" +
                "  `"mcpServers`": {`n    `"other`": {`n      `"command`": `"other.exe`",`n      `"args`": []`n    }`n  },`n" +
                "  `"tipsHistory`": {`n    `"n`": 3,`n    `"x`": -1.5e2,`n    `"ok`": true,`n    `"none`": null`n  }`n}`n"
    $config = Join-Path $env:USERPROFILE '.claude.json'
    $originalBytes = (New-Object Text.UTF8Encoding($false)).GetBytes($original)
    [IO.File]::WriteAllBytes($config, $originalBytes)

    # ---- the library alone ----------------------------------------------------
    $added = Set-HorizunMcpServerEntry $original 'horizun-revit' ([ordered]@{ command = $server; args = @(); env = @{} })
    Assert 'library: add then remove returns the original text' ((Remove-HorizunMcpServerEntry $added 'horizun-revit') -ceq $original) $null
    Assert 'library: the add keeps the existing server' ((Get-HorizunMcpServerNames $added) -join ',' -eq 'other,horizun-revit') ((Get-HorizunMcpServerNames $added) -join ',')
    $again = Set-HorizunMcpServerEntry $added 'horizun-revit' ([ordered]@{ command = 'C:\other\path.exe'; args = @(); env = @{} })
    Assert 'library: a second set replaces the value in place' (((Get-HorizunMcpServerNames $again) -join ',') -eq 'other,horizun-revit' -and $again.Contains('C:\\other\\path.exe')) $again
    $bare = Set-HorizunMcpServerEntry "{`n  `"a`": 1`n}" 'horizun-revit' ([ordered]@{ command = 'x'; args = @() })
    Assert 'library: mcpServers is created when absent' ((Get-HorizunMcpServerNames $bare) -join ',' -eq 'horizun-revit') $bare
    $bad = $false
    try { [void](Get-HorizunJsonRoot '{"a": [1, 2,]}') } catch { $bad = $true }
    Assert 'library: malformed JSON is refused' $bad $null

    # ---- register-client.ps1 end to end -----------------------------------------
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') `
        -Client Claude -ServerPath $server -Force *> $null
    Assert 'registration exits 0 on a config with case-variant keys' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    $afterBytes = [IO.File]::ReadAllBytes($config)
    Assert 'no BOM was written' (-not ($afterBytes.Length -ge 3 -and $afterBytes[0] -eq 0xEF -and $afterBytes[1] -eq 0xBB)) $null
    $afterText = (New-Object Text.UTF8Encoding($false, $true)).GetString($afterBytes)
    Assert 'non-ASCII text survives' ($afterText.Contains('Jos' + [char]0xE9) -and $afterText.Contains('configuraci' + [char]0xF3 + 'n')) $null
    Assert 'both case-variant project keys survive' ($afterText.Contains('"C:/Users/demo/obra"') -and $afterText.Contains('"c:/Users/demo/obra"')) $null
    Assert 'the entry and the existing server are both there' (((Get-HorizunMcpServerNames $afterText) -join ',') -eq 'other,horizun-revit') ((Get-HorizunMcpServerNames $afterText) -join ',')
    Assert 'the only change is the horizun-revit member' ((Remove-HorizunMcpServerEntry $afterText 'horizun-revit') -ceq $original) $afterText

    # The verifier that runs right after registration reads the same file. It used
    # to ConvertFrom-Json it, refuse the case-variant keys, and so undo the
    # registration it was meant to confirm.
    $report = Join-Path $root 'verify.json'
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-install.ps1') `
        -Client Claude -ServerPath $server -SkipLive -Json $report *> $null
    $checks = @((Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).checks)
    $points = @($checks | Where-Object { $_.check -eq 'Claude points at the installed server' })
    Assert 'verify-install reads the entry from a config with case-variant keys' ($points.Count -eq 1 -and $points[0].ok) `
        (($checks | Where-Object { $_.check -like 'Claude*' } | ForEach-Object { "$($_.check)=$($_.ok) $($_.detail)" }) -join ' | ')

    & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') `
        -Client Claude -Remove -Force *> $null
    Assert 'removal exits 0' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    Assert 'add then remove leaves the file byte-identical' (Same-Bytes $originalBytes ([IO.File]::ReadAllBytes($config))) `
        ((New-Object Text.UTF8Encoding($false)).GetString([IO.File]::ReadAllBytes($config)))

    # The same document WITHOUT the case-variant key: the old helper accepted it, and
    # under 5.1 wrote it back as ANSI-misread text with a BOM.
    $plain = $original.Replace("    `"c:/Users/demo/obra`": {`n      `"allowedTools`": [`n        `"Bash`"`n      ]`n    }`n", '').Replace("    },`n  },", "    }`n  },")
    [IO.File]::WriteAllBytes($config, (New-Object Text.UTF8Encoding($false)).GetBytes($plain))
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') `
        -Client Claude -ServerPath $server -Force *> $null
    $plainAfter = [IO.File]::ReadAllBytes($config)
    $plainText = (New-Object Text.UTF8Encoding($false)).GetString($plainAfter)
    Assert 'plain config: registration exits 0' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    Assert 'plain config: no BOM' (-not ($plainAfter.Length -ge 3 -and $plainAfter[0] -eq 0xEF -and $plainAfter[1] -eq 0xBB)) $null
    Assert 'plain config: accented text is not mangled' ($plainText.Contains('Jos' + [char]0xE9 + '"') -and -not $plainText.Contains('Jos' + [char]0xC3)) $plainText
    Assert 'plain config: only the member changed' ((Remove-HorizunMcpServerEntry $plainText.TrimStart([char]0xFEFF) 'horizun-revit') -ceq $plain) $plainText

    # A file that is not UTF-8 is refused and left alone.
    $ansi = [byte[]](0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xE9, 0x22, 0x7D)   # {"a":"<0xE9>"}
    [IO.File]::WriteAllBytes($config, $ansi)
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'register-client.ps1') `
        -Client Claude -ServerPath $server -Force *> $null
    Assert 'a non-UTF-8 config is refused' ($LASTEXITCODE -ne 0) "exit $LASTEXITCODE"
    Assert 'a non-UTF-8 config is left untouched' (Same-Bytes $ansi ([IO.File]::ReadAllBytes($config))) $null
}
finally {
    $env:USERPROFILE = $oldProfile
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "client-config-encoding: $failed FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'client-config-encoding: PASS' -ForegroundColor Green
exit 0
