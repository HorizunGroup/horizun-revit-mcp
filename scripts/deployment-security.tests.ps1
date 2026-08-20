#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name - $Detail" -ForegroundColor Red; $failures.Add("${Name}: $Detail") }
}

. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')
$reparseRoot = Join-Path ([IO.Path]::GetTempPath()) ('hz-ancestor-reparse-' + [guid]::NewGuid().ToString('N'))
try {
    $outsideTarget = Join-Path $reparseRoot 'outside\MCP\server'
    $link = Join-Path $reparseRoot 'Programs\Horizun\MCP'
    New-Item -ItemType Directory -Path $outsideTarget,(Split-Path -Parent $link) -Force | Out-Null
    New-Item -ItemType Junction -Path $link -Target (Split-Path -Parent $outsideTarget) | Out-Null
    $reparseRefused = $false
    try { Assert-HorizunNoReparseTree (Join-Path $link 'server') 'ancestor fixture' } catch { $reparseRefused = $_.Exception.Message -match 'link or junction' }
    Check 'deployment confinement rejects a junction in an ancestor component' $reparseRefused 'normal child below a junction was accepted'
    $linkItem = Get-Item -LiteralPath $link -Force
    if (($linkItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { [IO.Directory]::Delete($link) }
}
finally { if (Test-Path -LiteralPath $reparseRoot) { Remove-Item -LiteralPath $reparseRoot -Recurse -Force } }

$projectionRoot = Join-Path ([IO.Path]::GetTempPath()) ('hz-payload-projection-' + [guid]::NewGuid().ToString('N'))
try {
    $projectionSource = Join-Path $projectionRoot 'bin'
    $projectionStage = Join-Path $projectionRoot 'stage'
    New-Item -ItemType Directory -Path (Join-Path $projectionSource 'lib') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $projectionSource 'Resources') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $projectionSource 'Recipes') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $projectionSource 'Horizun.Revit.dll') -Value 'plugin'
    Set-Content -LiteralPath (Join-Path $projectionSource 'dependency.dll') -Value 'dependency'
    Set-Content -LiteralPath (Join-Path $projectionSource 'Horizun.Revit.pdb') -Value 'debug-only'
    Set-Content -LiteralPath (Join-Path $projectionSource 'Horizun.Revit.deps.json') -Value 'build-only'
    Set-Content -LiteralPath (Join-Path $projectionSource 'lib\json.py') -Value 'stdlib'
    Set-Content -LiteralPath (Join-Path $projectionSource 'Resources\icon.png') -Value 'icon'
    Set-Content -LiteralPath (Join-Path $projectionSource 'Recipes\recipe.py') -Value 'recipe'

    Copy-HorizunPluginPayloadToStage -Source $projectionSource -Destination $projectionStage
    Check 'payload projection retains plugin and dependency DLLs' `
        ((Test-Path -LiteralPath (Join-Path $projectionStage 'Horizun.Revit.dll')) -and
         (Test-Path -LiteralPath (Join-Path $projectionStage 'dependency.dll'))) 'a loadable DLL was omitted'
    Check 'payload projection retains runtime content directories' `
        ((Test-Path -LiteralPath (Join-Path $projectionStage 'lib\json.py')) -and
         (Test-Path -LiteralPath (Join-Path $projectionStage 'Resources\icon.png')) -and
         (Test-Path -LiteralPath (Join-Path $projectionStage 'Recipes\recipe.py'))) 'a runtime content directory was omitted'
    Check 'payload projection excludes dotnet build artifacts' `
        ((-not (Test-Path -LiteralPath (Join-Path $projectionStage 'Horizun.Revit.pdb'))) -and
         (-not (Test-Path -LiteralPath (Join-Path $projectionStage 'Horizun.Revit.deps.json')))) 'a PDB or deps.json entered the manifest projection'
    $hiddenPayload = Join-Path $projectionStage 'hidden-runtime.dll'
    Set-Content -LiteralPath $hiddenPayload -Value 'hidden runtime'
    (Get-Item -LiteralPath $hiddenPayload -Force).Attributes = [IO.FileAttributes]::Hidden
    $hiddenListing = Get-HorizunPayloadListing $projectionStage
    Check 'manifest generation inventories hidden payload files' `
        (@($hiddenListing.Files | Where-Object Path -eq 'hidden-runtime.dll').Count -eq 1) 'hidden file was absent from manifest projection'
}
finally {
    if (Test-Path -LiteralPath $projectionRoot) { Remove-Item -LiteralPath $projectionRoot -Recurse -Force }
}

$privateProjector = Join-Path $repo 'publish/make-public-package.ps1'
if (Test-Path -LiteralPath $privateProjector) {
    # The projector is intentionally absent from the exported public tree. Its
    # confinement tests run in the private source tree; all shared deployment
    # tests below still run in both trees.
    $outside = Join-Path ([IO.Path]::GetTempPath()) ('horizun-public-output-refusal-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $outside -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'must survive'
        $refused = $false
        try { & $privateProjector -Output $outside -ReplaceOutput } catch { $refused = $_.Exception.Message -match 'must stay under' }
        Check 'public package cannot recursively replace an arbitrary directory' `
            ($refused -and (Test-Path -LiteralPath (Join-Path $outside 'keep.txt'))) 'outside file was removed or refusal was not explicit'
    }
    finally { if (Test-Path -LiteralPath $outside) { Remove-Item -LiteralPath $outside -Recurse -Force } }

    $junctionTarget = Join-Path ([IO.Path]::GetTempPath()) ('horizun-public-junction-target-' + [guid]::NewGuid().ToString('N'))
    $junctionPath = Join-Path $repo ('dist\public\junction-refusal-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $junctionTarget,(Split-Path -Parent $junctionPath) -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $junctionTarget 'keep.txt') -Value 'must survive'
        New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget | Out-Null
        $refused = $false
        try { & $privateProjector -Output $junctionPath -ReplaceOutput } catch { $refused = $_.Exception.Message -match 'link or junction' }
        Check 'public package cannot escape through an in-root junction' `
            ($refused -and (Test-Path -LiteralPath (Join-Path $junctionTarget 'keep.txt'))) 'junction target was changed or not refused'
    }
    finally {
        if (Test-Path -LiteralPath $junctionPath) { Remove-Item -LiteralPath $junctionPath -Force }
        if (Test-Path -LiteralPath $junctionTarget) { Remove-Item -LiteralPath $junctionTarget -Recurse -Force }
    }
}

$bootstrap = Get-Content (Join-Path $repo 'install-release.ps1') -Raw
$sourceInstall = Get-Content (Join-Path $repo 'install.ps1') -Raw
$sign = Get-Content (Join-Path $repo 'scripts/sign.ps1') -Raw
$selfSign = Get-Content (Join-Path $repo 'scripts/self-sign.ps1') -Raw
$completion = Get-Content (Join-Path $repo 'scripts/complete-install.ps1') -Raw
$installer = Get-Content (Join-Path $repo 'installer/horizun-mcp.iss') -Raw
Check 'release bootstrap allows only explicitly acknowledged unsigned 0.x or independently trusted signing' `
    ($bootstrap -match 'Status -ne ''Valid''' -and $bootstrap -match 'SignPath Foundation' -and
     $bootstrap -match 'Status -eq ''NotSigned''' -and $bootstrap -match 'UnsignedAllowed' -and
     $bootstrap -match "notmatch '\^0\\\.'" -and $bootstrap -match 'CompanyName' -and
     $bootstrap -match 'ProductName' -and $bootstrap -match 'TimeStamperCertificate') 'missing fail-closed trust checks or bounded 0.x exception'
Check 'source install refuses duplicate AddInId under any manifest name or scope' `
    ($sourceInstall -match 'addinIdentityConflicts' -and $sourceInstall -match 'Get-HorizunManifestsByAddInId' -and
     $sourceInstall -match 'same AddInId') 'identity conflict is not a preflight refusal'
$expectedAddInId = [guid]'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30'
$guidSpellings = @(
    $expectedAddInId.ToString('D'), $expectedAddInId.ToString('N'),
    $expectedAddInId.ToString('B').ToUpperInvariant(), $expectedAddInId.ToString('P'))
Check 'PowerShell GUID comparison accepts D/N/B/P spellings semantically' `
    (@($guidSpellings | Where-Object { ([guid]$_) -ne $expectedAddInId }).Count -eq 0) `
    'a standard GUID spelling was not equivalent to the canonical AddInId'

$entityFixture = Join-Path ([IO.Path]::GetTempPath()) ('hz-addin-entity-' + [guid]::NewGuid().ToString('N') + '.addin')
try {
    $encodedGuid = $expectedAddInId.ToString('D').Replace('-', '&#x2D;')
    $otherGuid = [guid]'11111111-2222-3333-4444-555555555555'
    $fixtures = @(
        [pscustomobject]@{
            Name = 'namespace, UTF-16, entity and multiple AddInId nodes'
            Encoding = 'Unicode'
            Xml = "<r:RevitAddIns xmlns:r='urn:revit'><r:AddIn><r:AddInId>$otherGuid</r:AddInId>" +
                  "<r:AddInId>$encodedGuid</r:AddInId></r:AddIn></r:RevitAddIns>"
            Loads = $true; Conflict = $true
        },
        [pscustomobject]@{
            Name = 'different valid GUID'
            Encoding = 'UTF8'
            Xml = "<RevitAddIns><AddIn><AddInId>$otherGuid</AddInId></AddIn></RevitAddIns>"
            Loads = $true; Conflict = $false
        },
        [pscustomobject]@{
            Name = 'broken XML'
            Encoding = 'UTF8'
            Xml = "<RevitAddIns><AddInId>$expectedAddInId</RevitAddIns>"
            Loads = $false; Conflict = $true
        },
        [pscustomobject]@{
            Name = 'DTD declaration'
            Encoding = 'UTF8'
            Xml = "<!DOCTYPE RevitAddIns [<!ENTITY id '$expectedAddInId'>]>" +
                  '<RevitAddIns><AddInId>&id;</AddInId></RevitAddIns>'
            Loads = $false; Conflict = $true
        })

    foreach ($fixture in $fixtures) {
        Set-Content -LiteralPath $entityFixture -Encoding $fixture.Encoding -Value $fixture.Xml
        $dom = New-Object -ComObject 'Msxml2.DOMDocument.6.0'
        try {
            $dom.async = $false
            $dom.validateOnParse = $false
            $dom.resolveExternals = $false
            $dom.setProperty('ProhibitDTD', $true)
            $loaded = [bool]$dom.load($entityFixture)
            $conflict = -not $loaded
            if ($loaded) {
                foreach ($node in @($dom.selectNodes('//*[local-name()="AddInId"]'))) {
                    $decodedGuid = [guid]::Empty
                    if ([guid]::TryParse(([string]$node.text).Trim(), [ref]$decodedGuid) -and
                        $decodedGuid -eq $expectedAddInId) { $conflict = $true; break }
                }
            }
            Check ("hardened XML fixture: " + $fixture.Name) `
                ($loaded -eq $fixture.Loads -and $conflict -eq $fixture.Conflict) `
                "loaded=$loaded conflict=$conflict"
        }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($dom) | Out-Null
            $dom = $null
        }
    }
}
finally {
    if ($dom) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($dom) | Out-Null }
    Remove-Item -LiteralPath $entityFixture -Force -ErrorAction SilentlyContinue
}
Check 'Inno parses XML fail-closed and compares semantic GUID identity' `
    ((Get-Content (Join-Path $repo 'scripts/horizun-deploy.lib.ps1') -Raw) -match '\[Guid\]::TryParse' -and
     (Get-Content (Join-Path $repo 'scripts/verify-install.ps1') -Raw) -match '\[Guid\]::TryParse' -and
     $installer -match "CreateOleObject\('Msxml2\.DOMDocument\.6\.0'\)" -and
     $installer -match "setProperty\('ProhibitDTD', True\)" -and
     $installer -match 'resolveExternals := False' -and $installer -match 'TryNormalizeGuid' -and
     $installer -notmatch 'CompactGuidSearchText|LoadStringFromFile\(Candidate') `
    'Inno can still compare raw XML bytes or allow DTD/external-entity parsing'
Check 'Inno distinguishes no matching manifests from enumeration failure' `
    ($installer -match 'YearRootAttributes := Win32GetFileAttributes\(YearRoot\)' -and
     $installer -match '\(EnumerationError = 2\) or \(EnumerationError = 3\) then Result := False' -and
     $installer -match 'EnumerationError := Win32GetLastError\(\)' -and
     $installer -match '\(EnumerationError <> 2\) and \(EnumerationError <> 18\) then exit' -and
     $installer -match 'EnumerationCompleted := EnumerationError = 18' -and
     $installer -match 'if not EnumerationCompleted then exit') `
    'directory probing or FindFirst can still turn access denied or an I/O error into a false absence'
Check 'source deployment refuses reparse-point payload trees before recursive cleanup' `
    ($sourceInstall -match 'Assert-HorizunNoReparseTree \$serverInstall' -and
     (Get-Content (Join-Path $repo 'scripts/horizun-deploy.lib.ps1') -Raw) -match 'Refusing \$Label through a link or junction') 'source cleanup can traverse an attacker-controlled junction'
Check 'source install deletes verified replaced-* backups' `
    ($sourceInstall -match "-Filter 'replaced-\*'" -and $sourceInstall -match 'Remove-Item -LiteralPath \$old\.FullName') 'successful updates retain executable backups'
Check 'locked transaction rollback images are deferred after exact verification' `
    ($sourceInstall -match 'obsolete rollback image is still in use' -and
     $sourceInstall -match "'\.legacy-backup-'" -and
     $completion -notmatch "foreach \(\$pattern in [^\r\n]*\.install-rollback") 'a finisher can delete a concurrent install rollback image'
Check 'source and packaged installs verify exact payloads before discarding rollback images' `
    ($sourceInstall -match 'Exact installed-payload verification failed' -and
     $installer -match 'if VerifyInstalledPayload then CommitDeployment') 'commit can precede exact payload verification'
Check 'source self-signing refreshes the byte manifest Authenticode changes' `
    ($selfSign -match '\[bool\]\$doc\.SourceInstall' -and $selfSign -match 'Get-HorizunPayloadListing' -and
     $selfSign -match 'tmp-\$\(\[guid\]') 'source manifest can become stale after signing'
Check 'release signing never places a PFX password on a child process command line' `
    ($sign -match 'PfxPath is no longer accepted' -and $sign -notmatch "@\('/f'.*'/p'") 'PFX password route still exists'
Check 'completion state and Run values are generation-owned' `
    ($completion -match 'generationStatusPath' -and $completion -match 'runNamePrefix \+ \$Generation' -and
     ([regex]::Matches($completion, 'Test-CurrentGeneration\)\) \{ Clear-Resume; exit 0 \}').Count -ge 4) -and
     $completion -match 'if \(-not \$Detached\) \{ Start-DetachedWorker \$resolved \}' -and
     $completion -match '\$_.Name -eq ''HorizunMCPCompleteInstall''' -and
     $completion -match '\$_.Name -ne \$runName') 'superseded Run cleanup or immediate retry is incomplete'

$locks = @(
    'src/Horizun.Server/packages.lock.json',
    'src/Horizun.Revit/packages.lock.2023.json','src/Horizun.Revit/packages.lock.2024.json',
    'src/Horizun.Revit/packages.lock.2025.json','src/Horizun.Revit/packages.lock.2026.json','src/Horizun.Revit/packages.lock.2027.json',
    'tests/Horizun.Core.Tests/packages.lock.json','tests/Horizun.Server.Tests/packages.lock.json')
$missing = @($locks | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repo $_) -PathType Leaf) })
Check 'every conditional build graph has a committed NuGet lock' ($missing.Count -eq 0) ($missing -join ', ')

if ($failures.Count -gt 0) { throw "$($failures.Count) deployment security test(s) failed`n$($failures -join "`n")" }
Write-Host 'deployment security: ALL PASSED' -ForegroundColor Green
