#Requires -Version 5.1
<#
  Build a deterministic public working tree from an explicit allowlist.

  This script NEVER creates a repository, commits, pushes or edits an existing
  public checkout. It produces a reviewable tree, runs the mandatory private-name
  and generic-secret gate, then optionally compares that tree with an existing
  public checkout. A maintainer performs the final copy/commit/push separately.

  Usage:
    powershell -File publish\make-public-package.ps1
    powershell -File publish\make-public-package.ps1 -PublicCheckout C:\src\horizun-revit-mcp-public

  A clean committed tree and a new/empty output directory are required by
  default. -AllowDirty and -ReplaceOutput exist only for an explicit local
  review; their output is not release evidence.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$PublicCheckout,
    [string]$TermsFile,
    [switch]$AllowDirty,
    [switch]$ReplaceOutput
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo 'dist\public\horizun-revit-mcp' }
$Output = [IO.Path]::GetFullPath($Output)
$repoFull = [IO.Path]::GetFullPath($repo)
$profile = [IO.Path]::GetFullPath([Environment]::GetFolderPath('UserProfile'))
$driveRoot = [IO.Path]::GetPathRoot($Output)

if ($Output.TrimEnd('\') -in @($repoFull.TrimEnd('\'), $profile.TrimEnd('\'), $driveRoot.TrimEnd('\'))) {
    throw "Unsafe public-package output: $Output"
}
if (Test-Path (Join-Path $Output '.git')) {
    throw "Output is an existing git checkout. Use -PublicCheckout to compare; this script never rewrites a repository: $Output"
}
if ((Test-Path $Output) -and @(Get-ChildItem -LiteralPath $Output -Force).Count -gt 0 -and -not $ReplaceOutput) {
    throw "Output is not empty. Refusing to replace it without the explicit local-review switch -ReplaceOutput: $Output"
}

$allowDirs = @('src', 'tests', 'scripts', 'installer', '.github', 'docs/requirement-sets')
$allowDocs = @(
    'docs\BENCHMARK.md',
    'docs\FAMILY-AUTHORING.md',
    'docs\HORIZUN-HUB.md',
    'docs\live-fixtures.example.json',
    'docs\python-fallback-recipes.md',
    'docs\RELEASE-POLICY.md',
    'docs\requirement-set.md',
    'docs\security-model.md'
)
$allowRoot = @(
    '.gitattributes', '.gitignore', 'AGENTS.md', 'CHANGELOG.md', 'CLAUDE.md',
    'CONTRIBUTING.md', 'Directory.Build.props', 'install-release.ps1',
    'install.ps1', 'llms.txt', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md'
)
$overlay = Join-Path $PSScriptRoot 'overlay'

function Step([string]$message) { Write-Host "[public] $message" -ForegroundColor Cyan }

function Get-TreeMap([string]$root) {
    $map = @{}
    if (-not (Test-Path $root)) { return $map }
    foreach ($file in Get-ChildItem $root -Recurse -File | Where-Object {
        $_.FullName -notlike "*$([IO.Path]::DirectorySeparatorChar).git$([IO.Path]::DirectorySeparatorChar)*"
    }) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        $map[$relative] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

Push-Location $repo
try {
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
    $status = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed' }
}
finally { Pop-Location }

if ($status.Count -gt 0) {
    $message = "The source tree has $($status.Count) uncommitted change(s). A tracked-only public package could omit new files or describe bytes that no commit contains."
    if (-not $AllowDirty) { throw "$message Commit/stash first; -AllowDirty is only for a non-publishable local review." }
    Write-Host "[public] WARNING: $message This output is REVIEW ONLY and must not be published." -ForegroundColor Yellow
}

if (Test-Path $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null

try {
    Step 'copying tracked files from the explicit allowlist'
    $copied = 0
    foreach ($f in $tracked) {
        $take = $false
        foreach ($d in $allowDirs) { if ($f -like "$d/*") { $take = $true; break } }
        $windowsPath = $f.Replace('/','\')
        if (-not $take -and $allowDocs -contains $windowsPath) { $take = $true }
        if (-not $take -and $allowRoot -contains $f) { $take = $true }
        if (-not $take) { continue }

        $dest = Join-Path $Output $windowsPath
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item -LiteralPath (Join-Path $repo $windowsPath) -Destination $dest -Force
        $copied++
    }
    Step "$copied tracked files copied"

    Step 'laying the public overlay on top'
    foreach ($file in Get-ChildItem $overlay -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Output $file.Name) -Force
    }

    # Registry metadata is generated from the canonical product version rather
    # than maintained as another manually bumped source of truth.
    $mcpDir = Join-Path $Output '.mcp'
    & (Join-Path $repo 'scripts\generate-mcp-manifest.ps1') -OutFile (Join-Path $mcpDir 'server.json')

    Step 'running the mandatory public-tree scan'
    $scanJson = Join-Path ([IO.Path]::GetTempPath()) ('horizun-public-scan-' + [guid]::NewGuid().ToString('N') + '.json')
    $scanArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts\scan-sensitive.ps1'),'-Root',$Output,'-AllFiles','-RequireTerms','-Json',$scanJson)
    if ($TermsFile) { $scanArgs += @('-TermsFile',$TermsFile) }
    & powershell @scanArgs
    if ($LASTEXITCODE -ne 0) { throw "public-tree sensitive/secret scan failed (exit $LASTEXITCODE)" }
    Remove-Item $scanJson -Force -ErrorAction SilentlyContinue

    if ($PublicCheckout) {
        $PublicCheckout = [IO.Path]::GetFullPath($PublicCheckout)
        if (-not (Test-Path (Join-Path $PublicCheckout '.git'))) {
            throw "-PublicCheckout is not a git checkout: $PublicCheckout"
        }
        $newMap = Get-TreeMap $Output
        $oldMap = Get-TreeMap $PublicCheckout
        $all = @($newMap.Keys + $oldMap.Keys | Sort-Object -Unique)
        $changes = foreach ($path in $all) {
            if (-not $oldMap.ContainsKey($path)) { "A`t$path" }
            elseif (-not $newMap.ContainsKey($path)) { "D`t$path" }
            elseif ($oldMap[$path] -ne $newMap[$path]) { "M`t$path" }
        }
        Write-Host ''
        Write-Host "[public] diff against $PublicCheckout" -ForegroundColor Cyan
        if (@($changes).Count -eq 0) { Write-Host '  no file changes' -ForegroundColor Green }
        else { $changes | ForEach-Object { Write-Host "  $_" } }
    }

    Write-Host ''
    if ($status.Count -gt 0) {
        Write-Host "[public] dirty-tree review package ready (NOT PUBLISHABLE): $Output" -ForegroundColor Yellow
    } else {
        Write-Host "[public] reviewable package ready: $Output" -ForegroundColor Green
    }
    Write-Host '[public] no repository was initialised, committed, modified or pushed.' -ForegroundColor Green
}
catch {
    # A failed output must not look publishable to the next person who finds it.
    if ($scanJson -and (Test-Path $scanJson)) { Remove-Item -LiteralPath $scanJson -Force -ErrorAction SilentlyContinue }
    if (Test-Path $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
    throw
}
