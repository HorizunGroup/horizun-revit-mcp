#Requires -Version 5.1
<#
  Deploy the Horizun MCP Revit add-in to ONE Revit year's Addins folder.
  Copies the plugin DLL + its runtime deps into Addins\<year>\Horizun\ and the
  .addin manifest into Addins\<year>\. Does NOT touch any other add-in.

  For the usual case use deploy-both.ps1 instead: this ships one half of a paired
  contract, and a server that disagrees with the add-in refuses every call. This
  script exists for the times you genuinely mean to move one year on its own.

  The copy itself lives in horizun-deploy.lib.ps1, shared with deploy-both, so
  there is one answer to "how is a payload installed" rather than two that drift.

  Usage: powershell -ExecutionPolicy Bypass -File .\scripts\deploy.ps1 -Year 2026
#>
[CmdletBinding()]
param(
    [int]$Year = 2026,
    [string]$Config = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')

$binDir = Join-Path $repo "src\Horizun.Revit\bin\$Config"
$dll = Join-Path $binDir 'Horizun.Revit.dll'
if (-not (Test-Path $dll)) {
    throw "Build first: $dll not found. dotnet build src/Horizun.Revit -c $Config -p:RevitYear=$Year"
}

$installed = Install-HorizunPayload `
    -Source $binDir `
    -Year $Year `
    -ManifestSource (Join-Path $repo 'src\Horizun.Revit\Horizun.addin')

Write-Host "[Horizun] deployed to $($installed.AddinsDir)"
Write-Host "  plugin : $($installed.Dll)"
if ($installed.StdLibPy -gt 0) { Write-Host "  stdlib : $($installed.PluginDir)\lib ($($installed.StdLibPy) .py files)" }
if ($installed.Recipes -gt 0) { Write-Host "  recipes: $($installed.PluginDir)\Recipes ($($installed.Recipes) .py files)" }
Write-Host "  addin  : $($installed.Manifest)"

$prov = Get-HorizunProvenance $installed.Dll
if ($prov) {
    Write-Host ("  commit : {0}{1}" -f $prov.Sha.Substring(0, 12), $(if ($prov.Dirty) { ' (DIRTY TREE)' } else { '' }))
}

# Said every time, because it is true every time this script is used on its own.
Write-Host ""
Write-Host ("This deployed ONE HALF. The server and the add-in hash the same contract and the server refuses " +
            "an add-in whose hash differs, so if Contract.cs has moved since the installed server was built, " +
            "every call will now be refused. deploy-both.ps1 is the command that keeps them paired.") -ForegroundColor Yellow
Write-Host "Restart Revit $Year to load it."
