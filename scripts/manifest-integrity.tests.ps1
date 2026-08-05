#Requires -Version 5.1
<#
  REGRESSION TEST for the sign/manifest integrity flow.

  The defect: the manifest was written during the build, BEFORE signing. Signing a
  PE changes its bytes, so the manifest then described unsigned files while the
  installer wrapped signed ones, and -InstallerOnly re-checked nothing. This proves
  the fix from both ends:

    1. Test-HorizunStageMatchesManifest - the exact function -InstallerOnly and
       verify-release call - returns CLEAN for a matching stage, and REJECTS a
       stage where a single staged byte changed after the manifest was written.
    2. Update-HorizunManifestToStage - what sign.ps1 runs after signing - heals it,
       so the recomputed manifest matches the changed (i.e. signed) bytes again.
    3. pack.ps1 -InstallerOnly is wired to call the validator before building.

  Run:  powershell -ExecutionPolicy Bypass -File scripts\manifest-integrity.tests.ps1
  Exit: 0 all passed, 1 a check failed.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')

$failed = 0
function Assert($name, $cond, $detail) {
    if ($cond) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; if ($detail) { Write-Host "        $detail" -ForegroundColor DarkRed }; $script:failed++ }
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("hz-manifest-test-" + [guid]::NewGuid().ToString('N'))
try {
    # --- build a minimal but structurally real stage ------------------------
    New-Item -ItemType Directory -Force (Join-Path $stage 'server') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $stage 'plugin\2026') | Out-Null
    Set-Content (Join-Path $stage 'server\horizun-mcp.exe')  'apphost-bytes-v1'      -NoNewline
    Set-Content (Join-Path $stage 'server\horizun-mcp.dll')  'server-code-bytes-v1'  -NoNewline
    Set-Content (Join-Path $stage 'server\Newtonsoft.Json.dll') 'third-party-v1'     -NoNewline
    Set-Content (Join-Path $stage 'plugin\2026\Horizun.Revit.dll') 'plugin-2026-v1'  -NoNewline
    Set-Content (Join-Path $stage 'Horizun.addin') '<addin/>'                        -NoNewline

    $serverListing = Get-HorizunPayloadListing (Join-Path $stage 'server')
    $pluginListing = Get-HorizunPayloadListing (Join-Path $stage 'plugin\2026')

    $doc = [pscustomobject]@{
        Schema        = 2
        Commit        = ('a' * 40)
        CleanTree     = $true
        Server        = [pscustomobject]@{
            File    = 'server/horizun-mcp.exe'
            Sha256  = (Get-HorizunFileHash (Join-Path $stage 'server\horizun-mcp.exe'))
            Size    = (Get-Item (Join-Path $stage 'server\horizun-mcp.exe')).Length
            Payload = $serverListing.Files
        }
        AddinManifest = [pscustomobject]@{ File = 'Horizun.addin'; Sha256 = (Get-HorizunFileHash (Join-Path $stage 'Horizun.addin')) }
        Plugins       = @(
            [pscustomobject]@{
                Year    = 2026
                Sha256  = (Get-HorizunFileHash (Join-Path $stage 'plugin\2026\Horizun.Revit.dll'))
                Payload = $pluginListing.Files
            }
        )
    }
    $doc | ConvertTo-Json -Depth 6 | Out-File (Join-Path $stage 'manifest.json') -Encoding utf8

    # --- 1. a matching stage is clean ---------------------------------------
    $clean = @(Test-HorizunStageMatchesManifest $stage)
    Assert 'a stage that matches its manifest passes validation' ($clean.Count -eq 0) ($clean -join '; ')

    # --- 2. tampering the server CODE dll is caught -------------------------
    Add-Content (Join-Path $stage 'server\horizun-mcp.dll') 'x' -NoNewline
    $afterServer = @(Test-HorizunStageMatchesManifest $stage)
    Assert 'a changed server payload byte is REJECTED' ($afterServer.Count -gt 0) 'validator did not catch the tampered horizun-mcp.dll'
    Assert 'the rejection names the tampered file' ([bool]($afterServer -match 'horizun-mcp\.dll')) ($afterServer -join '; ')

    # --- 3. tampering a plugin dll is caught --------------------------------
    Add-Content (Join-Path $stage 'plugin\2026\Horizun.Revit.dll') 'y' -NoNewline
    $afterPlugin = @(Test-HorizunStageMatchesManifest $stage)
    Assert 'a changed plugin dll is REJECTED' ([bool]($afterPlugin -match 'plugin 2026')) ($afterPlugin -join '; ')

    # --- 4. recompute heals it (this is what sign.ps1 does after signing) ---
    $updated = Update-HorizunManifestToStage $stage
    $healed = @(Test-HorizunStageMatchesManifest $stage)
    Assert 'recomputing the manifest from the changed stage makes it match again' ($healed.Count -eq 0) ($healed -join '; ')
    Assert 'the recomputed manifest carries a signature block' ([bool]$updated.Signature) 'no Signature block after recompute'
    Assert 'unsigned own binaries are honestly reported as NOT signed' ($updated.Signed -eq $false) 'claimed signed on an unsigned stage'

    # --- 5. -InstallerOnly is wired to the validator ------------------------
    $packSrc = Get-Content (Join-Path $PSScriptRoot 'pack.ps1') -Raw
    Assert 'pack.ps1 -InstallerOnly calls the stage/manifest validator before building' `
           ($packSrc -match 'Test-HorizunStageMatchesManifest' -and $packSrc -match 'Refusing to build the installer') `
           'pack.ps1 does not validate the stage against the manifest before wrapping'
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
if ($failed -eq 0) { Write-Host "manifest-integrity: ALL PASSED" -ForegroundColor Green; exit 0 }
Write-Host "manifest-integrity: $failed FAILED" -ForegroundColor Red; exit 1
