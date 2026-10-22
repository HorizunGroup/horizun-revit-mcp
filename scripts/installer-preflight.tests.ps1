#Requires -Version 7.0
<#
  THE INSTALLER'S ADDINID PREFLIGHT, RUN - not read.

  deployment-security.tests.ps1 checks the Pascal source by pattern. This script
  compiles a test variant of installer/horizun-mcp.iss (/DPreflightTestRoot=...)
  and runs the real Setup.exe against fixture Addins folders, then reads what the
  preflight wrote to the Setup log. The test variant reads only the fixtures,
  scans every year whether or not Revit is installed, shows no dialog and always
  stops after the preflight: nothing is installed and the real Addins folders are
  never read. pack.ps1 never passes the define, so the release build is unchanged.

  Needs Inno Setup 6 (ISCC.exe). Without it the script says so and exits 0,
  unless -RequireIscc is passed.
#>
[CmdletBinding()]
param([switch]$RequireIscc)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name - $Detail" -ForegroundColor Red; $failures.Add("${Name}: $Detail") }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) {
    if ($RequireIscc) { throw 'ISCC.exe (Inno Setup 6) not found.' }
    Write-Host '  SKIP  Inno Setup 6 is not installed; the installer preflight was not run.' -ForegroundColor Yellow
    exit 0
}

$horizunId = [guid]'b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30'
$work = Join-Path ([IO.Path]::GetTempPath()) ('hz-preflight-' + [guid]::NewGuid().ToString('N'))
$fixtures = Join-Path $work 'fixtures'
try {
    # ISCC resolves ..\dist\stage against the script's own folder, so the script is
    # compiled from a copy beside a placeholder stage, never from the repository's dist.
    New-Item -ItemType Directory -Force -Path "$work\installer", "$work\dist\stage\server", $fixtures | Out-Null
    Copy-Item -Recurse -Path (Join-Path $repo 'installer\*') -Destination "$work\installer"
    Set-Content -LiteralPath "$work\dist\stage\server\placeholder.txt" -Value 'placeholder'
    Set-Content -LiteralPath "$work\dist\stage\manifest.json" -Value '{}'
    Set-Content -LiteralPath "$work\dist\stage\Horizun.addin" -Value '<RevitAddIns/>'
    $compile = & $iscc "$work\installer\horizun-mcp.iss" /Q '/DAppVersion=0.0.0-preflight' "/DPreflightTestRoot=$fixtures" "/O$work\out" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE): $($compile -join ' ')" }
    $setup = Get-ChildItem -LiteralPath "$work\out" -Filter '*.exe' | Select-Object -First 1
    if (-not $setup) { throw 'ISCC produced no Setup.exe' }

    function Write-Manifest([string]$Path, [string]$Xml) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        [IO.File]::WriteAllText($Path, $Xml, [Text.UTF8Encoding]::new($false))
    }
    function Addin([string]$Id) {
        "<?xml version='1.0' encoding='utf-8'?><RevitAddIns><AddIn Type='Application'><Name>Fixture</Name>" +
        "<Assembly>Fixture.dll</Assembly><AddInId>$Id</AddInId><FullClassName>Fixture.App</FullClassName>" +
        "<VendorId>FIXT</VendorId></AddIn></RevitAddIns>"
    }
    function Invoke-Preflight([string]$Case, [scriptblock]$Arrange) {
        Get-ChildItem -LiteralPath $fixtures -Force | Remove-Item -Recurse -Force
        & $Arrange
        $log = Join-Path $work "$Case.log"
        $process = Start-Process -FilePath $setup.FullName -PassThru `
            -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LANG=en', "/LOG=`"$log`"")
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw "$Case`: Setup did not stop after the preflight within 60 s"
        }
        if (-not (Test-Path -LiteralPath $log)) { throw "$Case`: Setup wrote no log" }
        return (Get-Content -LiteralPath $log -Raw)
    }
    $passed = 'Manifest preflight passed'
    $refused = 'Manifest preflight refused'
    $duplicate = 'already exists'
    $unreadable = 'could not be read'
    $notInstalled = 'This does not mean Horizun is installed'

    $log = Invoke-Preflight 'clean' {}
    Check 'no Addins folders at all passes' ($log -match $passed -and $log -notmatch $refused) 'a missing year folder was treated as a conflict'

    $log = Invoke-Preflight 'foreign-valid' {
        Write-Manifest "$fixtures\machine\2023\Vendor.addin" (Addin ([guid]::NewGuid().ToString()))
        Write-Manifest "$fixtures\user\2023\Other.addin" (Addin ([guid]::NewGuid().ToString('B')))
    }
    Check "another vendor's readable manifest passes" ($log -match $passed) 'a distinct AddInId was refused'

    $log = Invoke-Preflight 'own-user-manifest' {
        Write-Manifest "$fixtures\user\2023\Horizun.addin" (Addin $horizunId.ToString())
    }
    Check 'the per-user Horizun.addin being replaced is not a conflict' ($log -match $passed) 'the manifest Setup itself replaces was refused'

    $renamed = "$fixtures\machine\2023\Renamed.addin"
    $log = Invoke-Preflight 'renamed-duplicate' {
        Write-Manifest $renamed (Addin $horizunId.ToString('B').ToUpperInvariant())
    }
    Check 'a renamed duplicate is reported as a duplicate, with its path' `
        ($log -match $refused -and $log -match $duplicate -and $log.Contains($renamed) -and $log -notmatch $unreadable) `
        'the duplicate was missed, not named, or reported as unreadable'

    $doctype = "$fixtures\user\2023\Vendor.addin"
    $log = Invoke-Preflight 'doctype' {
        Write-Manifest $doctype ((Addin ([guid]::NewGuid().ToString())).Replace(
            "<?xml version='1.0' encoding='utf-8'?>", "<?xml version='1.0'?><!DOCTYPE RevitAddIns SYSTEM 'addin.dtd'>"))
    }
    Check "another vendor's manifest with a DOCTYPE is reported as unreadable, not as Horizun" `
        ($log -match $refused -and $log -match $unreadable -and $log.Contains($doctype) -and
         $log -match $notInstalled -and $log -notmatch $duplicate) `
        'an unreadable manifest is still reported as an existing Horizun install, or is not named'

    $broken = "$fixtures\user\2023\Broken.addin"
    $log = Invoke-Preflight 'malformed' {
        Write-Manifest $broken '<RevitAddIns><AddIn><AddInId>x</AddIn></RevitAddIns>'
    }
    Check 'a malformed manifest is named with the parser reason and line' `
        ($log -match $unreadable -and $log.Contains($broken) -and $log -match "$([regex]::Escape($broken)) - .+\(line \d+\)") `
        'the parser reason or line is missing'

    $log = Invoke-Preflight 'unreadable-and-duplicate' {
        Write-Manifest "$fixtures\machine\2023\Dup.addin" (Addin $horizunId.ToString('N'))
        Write-Manifest "$fixtures\user\2023\Bad.addin" '<RevitAddIns'
    }
    Check 'a duplicate and an unreadable file in different scopes are both reported' `
        ($log -match $duplicate -and $log.Contains("$fixtures\machine\2023\Dup.addin") -and
         $log -match $unreadable -and $log.Contains("$fixtures\user\2023\Bad.addin")) `
        'one of the two findings was dropped'

    $log = Invoke-Preflight 'unreadable-before-duplicate' {
        Write-Manifest "$fixtures\machine\2025\A-bad.addin" '<RevitAddIns'
        Write-Manifest "$fixtures\machine\2025\Z-dup.addin" (Addin $horizunId.ToString())
    }
    Check 'an unreadable file does not stop the scan before a real duplicate' `
        ($log -match $duplicate -and $log.Contains("$fixtures\machine\2025\Z-dup.addin")) `
        'the duplicate after an unreadable file was not found'

    $log = Invoke-Preflight 'two-unreadable' {
        Write-Manifest "$fixtures\user\2026\One.addin" '<RevitAddIns'
        Write-Manifest "$fixtures\user\2026\Two.addin" '<!DOCTYPE x><RevitAddIns/>'
    }
    Check 'every unreadable manifest is listed' `
        ($log.Contains("$fixtures\user\2026\One.addin") -and $log.Contains("$fixtures\user\2026\Two.addin")) `
        'only the first unreadable manifest was named'

    $log = Invoke-Preflight 'year-is-a-file' {
        New-Item -ItemType Directory -Force -Path "$fixtures\user" | Out-Null
        Set-Content -LiteralPath "$fixtures\user\2024" -Value 'not a folder'
    }
    Check 'a year path that is not a folder is reported as unreadable, with its path' `
        ($log -match $unreadable -and $log.Contains("$fixtures\user\2024") -and $log -match 'not a folder') `
        'the year path was not named'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { throw "$($failures.Count) installer preflight test(s) failed" }
Write-Host 'installer preflight: all checks passed' -ForegroundColor Green
