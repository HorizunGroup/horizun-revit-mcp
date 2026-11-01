#Requires -Version 7.0
<#
  THE INSTALLER'S FIRST TOOL PROFILE, RUN - not read.

  deployment-security.tests.ps1 checks the Pascal source by pattern. This script
  compiles a test variant of installer/horizun-mcp.iss (/DProfileTestRoot=...) and
  runs the real Setup.exe against fixture folders: the variant reads the home folder,
  LOCALAPPDATA and the uninstall key from under that root, runs DetectFreshInstall and
  WriteFirstToolProfile, logs them and stops. Nothing is installed and the real
  settings.json is never read. pack.ps1 never passes the define.

  Then the server built from this tree reads the settings.json Setup wrote and must
  list the esencial profile.

  Needs Inno Setup 6 (ISCC.exe). Without it the script says so and exits 0, unless
  -RequireIscc is passed.
#>
[CmdletBinding()]
param([switch]$RequireIscc, [string]$Server)
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
    Write-Host '  SKIP  Inno Setup 6 is not installed; the installer profile test was not run.' -ForegroundColor Yellow
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('hz-profile-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'fixtures'
try {
    New-Item -ItemType Directory -Force -Path "$work\installer", "$work\dist\stage\server", $root | Out-Null
    Copy-Item -Recurse -Path (Join-Path $repo 'installer\*') -Destination "$work\installer"
    Set-Content -LiteralPath "$work\dist\stage\server\placeholder.txt" -Value 'placeholder'
    Set-Content -LiteralPath "$work\dist\stage\manifest.json" -Value '{}'
    Set-Content -LiteralPath "$work\dist\stage\Horizun.addin" -Value '<RevitAddIns/>'
    $compile = & $iscc "$work\installer\horizun-mcp.iss" /Q '/DAppVersion=0.0.0-profile' "/DProfileTestRoot=$root" "/O$work\out" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE): $($compile -join ' ')" }
    $setup = Get-ChildItem -LiteralPath "$work\out" -Filter '*.exe' | Select-Object -First 1
    if (-not $setup) { throw 'ISCC produced no Setup.exe' }

    $settings = Join-Path $root 'home\.horizun\settings.json'
    function Invoke-Setup([string]$Case, [scriptblock]$Arrange) {
        Get-ChildItem -LiteralPath $root -Force | Remove-Item -Recurse -Force
        & $Arrange
        $log = Join-Path $work "$Case.log"
        $process = Start-Process -FilePath $setup.FullName -PassThru `
            -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LANG=en', "/LOG=`"$log`"")
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "$Case`: Setup did not stop within 60 s" }
        $text = Get-Content -LiteralPath $log -Raw
        if ($text -notmatch 'tool profile test: done') { throw "$Case`: the test variant did not run: $text" }
        return $text
    }
    function New-File([string]$Path, [string]$Text) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
    }

    # 1. a new install: nothing there -> settings.json with the esencial profile
    $log = Invoke-Setup 'fresh' {}
    $written = if (Test-Path -LiteralPath $settings) { Get-Content -LiteralPath $settings -Raw } else { '' }
    $json = $null
    try { $json = $written | ConvertFrom-Json } catch { }
    Check 'a new install is detected as new' ($log -match 'tool profile test: fresh install') 'it was taken for an existing install'
    Check 'a new install writes tool_profile=esencial as valid JSON' `
        ($json -and $json.tool_profile -eq 'esencial' -and @($json.PSObject.Properties).Count -eq 1) "wrote: $written"
    $freshSettings = Join-Path $work 'fresh-settings.json'
    if ($written) { Copy-Item -LiteralPath $settings -Destination $freshSettings }

    # 2. an existing settings.json is never touched
    $existing = '{"permission_profile":"full_write","tool_packs":["read"]}'
    Invoke-Setup 'settings-exists' { New-File $settings $existing } | Out-Null
    Check 'an existing settings.json is left byte for byte' `
        ((Get-Content -LiteralPath $settings -Raw) -eq $existing) 'Setup changed the owner settings'

    # 3-5. an upgrade, by any of the other three signs, writes nothing
    $signs = [ordered]@{
        'server-folder'  = { New-Item -ItemType Directory -Force -Path (Join-Path $root 'localappdata\Programs\Horizun\MCP\server') | Out-Null }
        'install-status' = { New-File (Join-Path $root 'localappdata\Horizun\install-status.json') '{}' }
        'uninstall-key'  = { New-File (Join-Path $root 'uninstall-key') '' }
    }
    foreach ($case in $signs.Keys) {
        $log = Invoke-Setup $case $signs[$case]
        Check "an upgrade ($case) is not a new install" ($log -match 'tool profile test: existing install') 'taken for new'
        Check "an upgrade ($case) writes no settings.json" (-not (Test-Path -LiteralPath $settings)) 'Setup wrote a profile on an upgrade'
    }

    # 6. the server reads what Setup wrote and lists the esencial profile
    if (-not $Server) {
        $build = & dotnet build (Join-Path $repo 'src\Horizun.Server\Horizun.Server.csproj') -c Release --nologo -v:q 2>&1
        if ($LASTEXITCODE -ne 0) { throw "server build failed: $($build -join ' ')" }
        $Server = Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'
    }
    if (Test-Path -LiteralPath $freshSettings) {
        $data = Join-Path $work 'data'
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        Copy-Item -LiteralPath $freshSettings -Destination (Join-Path $data 'settings.json')
        $psi = [Diagnostics.ProcessStartInfo]::new($Server)
        $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
        foreach ($k in @($psi.Environment.Keys)) { if ($k -like 'HORIZUN_*') { $psi.Environment.Remove($k) | Out-Null } }
        $psi.Environment['HORIZUN_DATA_ROOT'] = $data
        $p = [Diagnostics.Process]::Start($psi)
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"installer-profile-test","version":"1"}}}')
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
        $p.StandardInput.Close()
        $out = $p.StandardOutput.ReadToEnd()
        if (-not $p.WaitForExit(60000)) { $p.Kill() }
        $reply = $out -split "`n" | Where-Object { $_ -match '"id":2' } | Select-Object -First 1 | ConvertFrom-Json
        $names = @($reply.result.tools | ForEach-Object { $_.name })
        Check 'the server lists the esencial profile from the settings.json Setup wrote' `
            ($names.Count -ge 10 -and $names.Count -le 20 -and $names -contains 'horizun_call_tool' -and
             $names -contains 'horizun_health' -and $names -notcontains 'horizun_manage_views') `
            "listed $($names.Count): $($names -join ', ')"
    }
    else { Check 'the server lists the esencial profile from the settings.json Setup wrote' $false 'no settings.json was written' }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { throw "$($failures.Count) installer profile test(s) failed`n$($failures -join "`n")" }
Write-Host 'installer profile: all checks passed' -ForegroundColor Green
