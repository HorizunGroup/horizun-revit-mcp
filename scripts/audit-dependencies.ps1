#Requires -Version 5.1
<#
  Restore every dependency graph that ships or executes in CI and ask NuGet's
  configured advisory sources for known vulnerabilities. The Revit project is
  conditional by year, so auditing only its default property value is not
  evidence for the five supported binaries.
#>
[CmdletBinding()]
param(
    [ValidateSet(2023, 2024, 2025, 2026, 2027)]
    [int[]]$RevitYears = @(2023, 2024, 2025, 2026, 2027)
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Get-VulnerabilityRecords($node) {
    if ($null -eq $node -or $node -is [string] -or $node.GetType().IsPrimitive) { return }
    if ($node -is [System.Collections.IEnumerable] -and $node -isnot [pscustomobject]) {
        foreach ($item in $node) { Get-VulnerabilityRecords $item }
        return
    }
    foreach ($property in $node.PSObject.Properties) {
        if ($property.Name -eq 'vulnerabilities') {
            foreach ($item in @($property.Value)) { if ($null -ne $item) { $item } }
        } else {
            Get-VulnerabilityRecords $property.Value
        }
    }
}

function Invoke-DependencyAudit([string]$project, [string]$label, [string[]]$restoreProperties) {
    $absolute = Join-Path $repo $project
    if (-not (Test-Path $absolute -PathType Leaf)) { throw "dependency-audit project missing: $project" }

    # Do not offer a no-restore shortcut here. Every Revit year writes a different
    # conditional graph to the same obj/project.assets.json; reusing the previous
    # year would produce a green report while auditing the wrong binary.
    $restoreArgs = @('restore', $absolute, '--locked-mode', '--nologo') + @($restoreProperties)
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "locked restore failed for $label (exit $LASTEXITCODE)" }

    $output = & dotnet list $absolute package --vulnerable --include-transitive --no-restore `
        --format json --output-version 1 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet vulnerability audit failed for $label (exit $LASTEXITCODE):`n$($output -join "`n")"
    }
    try { $document = ($output -join "`n") | ConvertFrom-Json }
    catch { throw "NuGet vulnerability audit returned invalid JSON for ${label}: $($_.Exception.Message)" }

    $vulnerabilities = @(Get-VulnerabilityRecords $document)
    if ($vulnerabilities.Count -gt 0) {
        throw "known vulnerable dependency in ${label}:`n$($output -join "`n")"
    }
    Write-Host "[PASS] $label has no dependency vulnerabilities reported by the configured NuGet sources"
}

$oldYear = [Environment]::GetEnvironmentVariable('RevitYear', 'Process')
try {
    Invoke-DependencyAudit 'src\Horizun.Server\Horizun.Server.csproj' 'server' @()
    Invoke-DependencyAudit 'tests\Horizun.Core.Tests\Horizun.Core.Tests.csproj' 'core tests' @()
    Invoke-DependencyAudit 'tests\Horizun.Server.Tests\Horizun.Server.Tests.csproj' 'server tests' @()

    foreach ($year in $RevitYears) {
        [Environment]::SetEnvironmentVariable('RevitYear', [string]$year, 'Process')
        Invoke-DependencyAudit 'src\Horizun.Revit\Horizun.Revit.csproj' "Revit $year add-in" @("-p:RevitYear=$year")
    }
}
finally {
    [Environment]::SetEnvironmentVariable('RevitYear', $oldYear, 'Process')
}

Write-Host "[PASS] dependency audit covered server/tests and Revit years $($RevitYears -join ', ')" -ForegroundColor Green
