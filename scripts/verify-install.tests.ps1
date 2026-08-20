#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('horizun-verify-install-' + [guid]::NewGuid().ToString('N'))
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')
try {
    $server = Join-Path $root 'product\server'
    $userAddins = Join-Path $root 'user-addins'
    $machineAddins = Join-Path $root 'machine-addins'
    $revitRoot = Join-Path $root 'programs'
    $plugin = Join-Path $userAddins '2026\Horizun'
    New-Item -ItemType Directory -Path $server,$plugin,$machineAddins,(Join-Path $revitRoot 'Revit 2026') -Force | Out-Null
    Set-Content (Join-Path $server 'horizun-mcp.exe') 'launcher'
    Set-Content (Join-Path $server 'horizun-mcp.dll') 'real-server-code'
    Set-Content (Join-Path $revitRoot 'Revit 2026\RevitAPI.dll') 'fixture'
    Set-Content (Join-Path $plugin 'Horizun.Revit.dll') 'plugin'
    Set-Content (Join-Path $plugin 'IronPython.dll') 'dependency'
    New-Item -ItemType Directory -Path (Join-Path $plugin 'lib') -Force | Out-Null
    Set-Content (Join-Path $plugin 'lib\json.py') 'stdlib fixture'
    $pluginListing = Get-HorizunPayloadListing $plugin
    $addin = Join-Path $userAddins '2026\Horizun.addin'
    Set-Content $addin '<RevitAddIns><AddIn><Assembly>Horizun\Horizun.Revit.dll</Assembly><AddInId>b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30</AddInId></AddIn></RevitAddIns>'
    $manifestPath = Join-Path $root 'product\manifest.json'
    [pscustomobject]@{
        Schema=2
        Server=[pscustomobject]@{ Sha256=Sha (Join-Path $server 'horizun-mcp.exe'); Payload=@(
            [pscustomobject]@{Path='horizun-mcp.exe';Sha256=Sha (Join-Path $server 'horizun-mcp.exe')},
            [pscustomobject]@{Path='horizun-mcp.dll';Sha256=Sha (Join-Path $server 'horizun-mcp.dll')}) }
        Plugins=@([pscustomobject]@{Year=2026;Sha256=Sha (Join-Path $plugin 'Horizun.Revit.dll');
            StdLibFiles=$pluginListing.StdLibFiles;StdLibDigest=$pluginListing.StdLibDigest;Payload=@(
            [pscustomobject]@{Path='Horizun.Revit.dll';Sha256=Sha (Join-Path $plugin 'Horizun.Revit.dll')},
            [pscustomobject]@{Path='IronPython.dll';Sha256=Sha (Join-Path $plugin 'IronPython.dll')})})
        AddinManifest=[pscustomobject]@{Sha256=Sha $addin}
    } | ConvertTo-Json -Depth 8 | Set-Content $manifestPath

    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'verify-install.ps1'),
        '-Client','None','-SkipLive','-ServerPath',(Join-Path $server 'horizun-mcp.exe'),'-ManifestPath',$manifestPath,
        '-RevitProgramRoot',$revitRoot,'-UserAddinsRoot',$userAddins,'-MachineAddinsRoot',$machineAddins)
    $hostExe = (Get-Process -Id $PID).Path
    & $hostExe @args *> $null
    if ($LASTEXITCODE -ne 0) { throw 'complete fixture did not verify' }
    Write-Host '  PASS  complete server/plugin payload and .addin verify'

    Set-Content (Join-Path $plugin 'lib\json.py') 'tampered stdlib fixture'
    & $hostExe @args *> $null
    if ($LASTEXITCODE -eq 0) { throw 'altered aggregated Python stdlib was accepted' }
    Set-Content (Join-Path $plugin 'lib\json.py') 'stdlib fixture'
    Write-Host '  PASS  aggregated Python stdlib changes are rejected'

    $externalLib = Join-Path $root 'external-lib'
    New-Item -ItemType Directory -Path $externalLib -Force | Out-Null
    Set-Content (Join-Path $externalLib 'json.py') 'stdlib fixture'
    Remove-Item -LiteralPath (Join-Path $plugin 'lib') -Recurse -Force
    New-Item -ItemType Junction -Path (Join-Path $plugin 'lib') -Target $externalLib | Out-Null
    & $hostExe @args *> $null
    if ($LASTEXITCODE -eq 0) { throw 'a junction-backed Python stdlib was accepted' }
    $libJunction = Join-Path $plugin 'lib'
    $junctionItem = Get-Item -LiteralPath $libJunction -Force
    if (($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'test fixture lib is not a junction' }
    [IO.Directory]::Delete($libJunction)
    New-Item -ItemType Directory -Path (Join-Path $plugin 'lib') -Force | Out-Null
    Set-Content (Join-Path $plugin 'lib\json.py') 'stdlib fixture'
    Write-Host '  PASS  junction-backed Python stdlib is rejected'

    $unexpected = Join-Path $plugin 'stale-or-injected.dll'
    Set-Content $unexpected 'not inventoried'
    & $hostExe @args *> $null
    if ($LASTEXITCODE -eq 0) { throw 'unexpected executable payload was accepted' }
    Remove-Item -LiteralPath $unexpected -Force
    Write-Host '  PASS  unexpected payload files are rejected'

    $hiddenUnexpected = Join-Path $plugin 'hidden-injected.dll'
    Set-Content $hiddenUnexpected 'not inventoried but hidden'
    (Get-Item -LiteralPath $hiddenUnexpected -Force).Attributes = [IO.FileAttributes]::Hidden
    & $hostExe @args *> $null
    if ($LASTEXITCODE -eq 0) { throw 'unexpected hidden executable payload was accepted' }
    Remove-Item -LiteralPath $hiddenUnexpected -Force
    Write-Host '  PASS  unexpected hidden payload files are rejected'

    Set-Content $addin '<RevitAddIns><AddIn><Assembly>malicious.dll</Assembly></AddIn></RevitAddIns>'
    & $hostExe @args *> $null
    if ($LASTEXITCODE -eq 0) { throw 'altered .addin was accepted' }
    Write-Host '  PASS  altered load manifest is rejected'

    Set-Content $addin '<RevitAddIns><AddIn><Assembly>Horizun\Horizun.Revit.dll</Assembly><AddInId>b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30</AddInId></AddIn></RevitAddIns>'
    New-Item -ItemType Directory -Path (Join-Path $machineAddins '2026') -Force | Out-Null
    $renamedManifest = Join-Path $machineAddins '2026\Renamed.addin'
    $equivalentIds = @(
        'B8E5A2F0-3C1D-4E6A-9F2B-7A4C8D1E5F30',
        'b8e5a2f03c1d4e6a9f2b7a4c8d1e5f30',
        '{B8E5A2F0-3C1D-4E6A-9F2B-7A4C8D1E5F30}',
        '(b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30)'
    )
    foreach ($equivalentId in $equivalentIds) {
        Set-Content $renamedManifest ("<RevitAddIns><AddIn><AddInId>$equivalentId</AddInId></AddIn></RevitAddIns>")
        $sourceMatches = @(Get-HorizunManifestsByAddInId -AddinsRoot $machineAddins -Year 2026 `
            -AddInId 'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30')
        if ($sourceMatches.Count -ne 1) { throw "source preflight missed equivalent AddInId '$equivalentId'" }
        & $hostExe @args *> $null
        if ($LASTEXITCODE -eq 0) { throw "renamed duplicate AddInId '$equivalentId' was accepted" }
    }
    Write-Host '  PASS  duplicate AddInId D/N/B/P spellings and case are rejected under a renamed manifest'

    Set-Content $renamedManifest '<RevitAddIns><AddIn><AddInId>11111111-2222-4333-8444-555555555555</AddInId></AddIn></RevitAddIns>'
    $sourceMatches = @(Get-HorizunManifestsByAddInId -AddinsRoot $machineAddins -Year 2026 `
        -AddInId 'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30')
    if ($sourceMatches.Count -ne 0) { throw 'source preflight treated a distinct AddInId as Horizun' }
    & $hostExe @args *> $null
    if ($LASTEXITCODE -ne 0) { throw 'a distinct AddInId was treated as a duplicate' }
    Write-Host '  PASS  a distinct valid AddInId remains allowed'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
