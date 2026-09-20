#Requires -Version 5.1
<#
  The provenance of ONE run, written down before anybody can attribute a result
  to the wrong build.

  G04 of the 2026-09-14 competitive inventory: "informe ata commit, hashes de
  archivos y binarios; ninguna prueba histórica se atribuye al build nuevo". That
  gap was found the hard way - the inventory itself was collected against a
  working tree whose local changes were never committed, so its own HEAD does not
  identify what it measured.

  A COMMIT DOES NOT IDENTIFY A BUILD. Two binaries compiled from the same HEAD
  with different uncommitted changes carry the same version and the same sha.
  What identifies a build is the bytes, so this records the bytes: every binary
  it is pointed at, hashed, alongside the git state at the moment of writing and
  whether that state was clean.

  It WRITES A FILE AND NOTHING ELSE. It does not build, does not install, does
  not start a server, and does not run a test. It is meant to be run immediately
  before a measured campaign and its output filed beside that campaign's results,
  so a later reader can ask "which build produced this?" and get an answer rather
  than an inference.

  Usage:
      pwsh -File scripts/generate-provenance-manifest.ps1 `
           -OutFile artifacts/provenance/run-2026-09-15.json `
           -Binary "src/Horizun.Server/bin/Release/net8.0/horizun-mcp.exe" `
           -Binary "src/Horizun.Revit/bin/2026/Release/Horizun.Revit.dll"

  A binary that is not there is recorded as missing, never omitted: the absence
  of a file is itself part of what a run was measured against.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string[]]$Binary = @(),
    [string]$Label = '',
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-GitState {
    param([string]$Root)

    $state = [ordered]@{
        commit             = $null
        branch             = $null
        clean              = $null
        dirty_paths        = @()
        read_error         = $null
    }

    try {
        Push-Location $Root
        $state.commit = (& git rev-parse HEAD 2>$null)
        $state.branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
        $status = @(& git status --porcelain 2>$null)
        # $status.Count on a List[object] has bitten this repo before; @() plus a
        # direct .Count on the array is the form that does not throw on pwsh 7.6.
        $state.clean = ($status.Count -eq 0)
        $state.dirty_paths = $status
    }
    catch {
        $state.read_error = $_.Exception.Message
    }
    finally {
        Pop-Location
    }
    return $state
}

function Get-BinaryFacts {
    param([string]$Path, [string]$Root)

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $Root $Path }

    if (-not (Test-Path -LiteralPath $resolved)) {
        return [ordered]@{
            path        = $Path
            present     = $false
            sha256      = $null
            bytes       = $null
            written_utc = $null
            note        = 'the file named was not there when the manifest was written.'
        }
    }

    $item = Get-Item -LiteralPath $resolved
    $hash = Get-FileHash -LiteralPath $resolved -Algorithm SHA256
    return [ordered]@{
        path        = $Path
        present     = $true
        sha256      = $hash.Hash.ToLowerInvariant()
        bytes       = $item.Length
        written_utc = $item.LastWriteTimeUtc.ToString('o')
        note        = $null
    }
}

$git = Get-GitState -Root $RepositoryRoot

$binaries = @()
foreach ($b in $Binary) { $binaries += (Get-BinaryFacts -Path $b -Root $RepositoryRoot) }

$manifest = [ordered]@{
    schema      = 'horizun.provenance-manifest/1'
    label       = $Label
    written_utc = (Get-Date).ToUniversalTime().ToString('o')
    machine     = [ordered]@{
        os              = [System.Environment]::OSVersion.VersionString
        powershell      = $PSVersionTable.PSVersion.ToString()
    }
    git         = $git
    binaries    = $binaries
    means       = @(
        'This names what a measured run was run AGAINST. If git.clean is false, the commit does not',
        'identify these bytes and every result must be attributed to the binary sha256 instead.',
        'A binary recorded with present=false was not on disk when this was written; that absence is',
        'part of the record, not an omission.'
    ) -join ' '
}

$outDirectory = Split-Path -Parent $OutFile
if ($outDirectory -and -not (Test-Path -LiteralPath $outDirectory)) {
    New-Item -ItemType Directory -Force -Path $outDirectory | Out-Null
}

$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "provenance manifest written: $OutFile"
if ($git.clean -eq $false) {
    Write-Warning ('the working tree was DIRTY when this manifest was written. The commit ' +
                   $git.commit + ' does not identify these bytes; attribute results to the binary hashes.')
}
