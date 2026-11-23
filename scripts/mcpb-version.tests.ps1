#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcpb-manifest.lib.ps1')
foreach ($version in @('4.1.0', '4.1.0-rc1', '4.1.0-rc.1+build.7')) {
    $m = New-HorizunMcpbManifest -Version $version -Command $script:HorizunMcpbPortableCommand
    if (@(Test-HorizunMcpbManifest $m -ExpectedVersion $version).Count) { throw "valid version rejected: $version" }
}
foreach ($version in @('4.1', '4.1.0-', '4.1.0-rc..1', '4.1.0+')) {
    $m = New-HorizunMcpbManifest -Version $version -Command $script:HorizunMcpbPortableCommand
    if (-not (@(Test-HorizunMcpbManifest $m) -match 'not semantic')) { throw "invalid version accepted: $version" }
}
Write-Host 'PASS: release, prerelease and invalid MCPB versions'
