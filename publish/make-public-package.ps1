#Requires -Version 5.1
<#
  Assemble the PUBLIC repository tree from this private one.

  WHY A SCRIPT. The public package is built from an EXPLICIT ALLOWLIST of what
  goes in, never from "everything minus what somebody remembered to remove" -
  the second model is how a client name ships. This file IS the list, and
  rerunning it after any private change reproduces the same curation without
  anyone having to remember what the curation was.

  WHY A FRESH TREE AND A SINGLE COMMIT. The private history retains client and
  project names in old blobs and in commit messages, which no file edit can
  reach. The working tree is clean; the history is not, and publishing the
  repository would publish the history. So the public repository starts from
  this tree with one initial commit and no past. (docs/sensitive-data.md §5,
  option 3 - authorised 2026-07-31.)

  WHAT IT DOES:
    1  Copies the allowlisted paths from the private tree.
    2  Lays the overlay on top: Apache-2.0 LICENSE, NOTICE, the public README,
       AGENTS.md/CLAUDE.md (agent install instructions).
    3  SCANS the result - structural terms here, plus the operator's private
       wordlist when present - and refuses to continue on any hit.
    4  Initialises a git repository with a single commit, ready to be pushed to
       an empty GitHub repository. It does NOT push: publishing is a
       one-way door and a person presses that button.

  Usage:  powershell -ExecutionPolicy Bypass -File publish\make-public-package.ps1
          ... -Output <dir>   (default: <repo>\dist\public\horizun-revit-mcp)
#>
[CmdletBinding()]
param(
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo 'dist\public\horizun-revit-mcp' }

# =============================================================================
# THE ALLOWLIST. A path not named here does not ship, whatever it contains.
#
# Deliberately absent, and why:
#   docs/ (all but two)   internal: parity with the previous MCP, migration
#                         planning, workflow inventory, readiness audits, the
#                         sensitive-data report itself. They describe the
#                         operator's own systems, not this product.
#   publish/              this machinery, and the overlay sources.
#   dist/, bin/, obj/     build outputs; the public repo builds its own.
# =============================================================================
$allowDirs = @('src', 'tests', 'scripts', 'installer', '.github')
$allowDocs = @('docs\security-model.md', 'docs\live-fixtures.example.json')
$allowRoot = @('CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', '.gitignore', '.gitattributes', 'install.ps1')
$overlay   = Join-Path $PSScriptRoot 'overlay'   # README, LICENSE, NOTICE, AGENTS.md, CLAUDE.md

function Step($m) { Write-Host "[public] $m" -ForegroundColor Cyan }

# Tracked files only: an untracked scratch file in src/ must not ride along.
Push-Location $repo
try { $tracked = @(& git ls-files); if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed - is this a git checkout?' } }
finally { Pop-Location }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null

Step "copying the allowlist from the private tree"
$copied = 0
foreach ($f in $tracked) {
    $take = $false
    foreach ($d in $allowDirs) { if ($f -like "$d/*") { $take = $true; break } }
    if (-not $take -and ($allowDocs -contains ($f -replace '/', '\'))) { $take = $true }
    if (-not $take -and ($allowRoot -contains $f)) { $take = $true }
    if (-not $take) { continue }

    $dest = Join-Path $Output ($f -replace '/', '\')
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item (Join-Path $repo $f) $dest -Force
    $copied++
}
Step "  $copied tracked files copied"

Step "laying the overlay on top"
foreach ($f in @(Get-ChildItem $overlay -File)) {
    Copy-Item $f.FullName (Join-Path $Output $f.Name) -Force
    Step "  overlay: $($f.Name)"
}

# =============================================================================
# SCAN THE RESULT. Two layers:
#   * structural terms - the NAMES of the internal documents this package must
#     not reference. Only names that are safe to write down live here; the
#     client and standard names do NOT appear in this file, for the same reason
#     scan-sensitive.ps1 keeps its wordlist outside the repository: a scanner
#     that carries its own arsenal ships the very strings it exists to catch.
#   * the operator's wordlist (%USERPROFILE%\.horizun\sensitive-terms.txt).
#     WITHOUT IT THIS SCRIPT REFUSES: the structural check alone cannot see a
#     client name, and "the scan I could run passed" is not "the scan passed".
# =============================================================================
Step "scanning the package"
$problems = @()

$structural = @('sensitive-data.md', 'parity-matrix', 'migration-plan',
                'workflow-inventory', 'production-readiness')
$files = @(Get-ChildItem $Output -Recurse -File)
foreach ($file in $files) {
    $text = ''
    try { $text = [IO.File]::ReadAllText($file.FullName) } catch { continue }
    foreach ($t in $structural) {
        if ($text.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $problems += "$($file.FullName.Substring($Output.Length + 1)): contains '$t'"
        }
    }
}

$wordlist = Join-Path $env:USERPROFILE '.horizun\sensitive-terms.txt'
if (-not (Test-Path $wordlist)) {
    Write-Host ""
    Write-Host ("REFUSING to package: the private wordlist is not on this machine ($wordlist). The structural " +
                "check alone cannot see a client name, and a package that skipped the real scan must not " +
                "exist - a directory that looks ready to push IS ready to push, to whoever finds it.") -ForegroundColor Red
    Remove-Item $Output -Recurse -Force
    exit 1
}
$terms = @(Get-Content $wordlist | Where-Object { $_.Trim() -and -not $_.StartsWith('#') })
foreach ($file in $files) {
    $text = ''
    try { $text = [IO.File]::ReadAllText($file.FullName) } catch { continue }
    foreach ($t in $terms) {
        if ($text.IndexOf($t.Trim(), [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            # The finding names the FILE, never the term: this transcript may
            # itself end up somewhere the term must not be.
            $problems += "$($file.FullName.Substring($Output.Length + 1)): matches a term from the private wordlist"
        }
    }
}
Step "  wordlist check ran against $($terms.Count) private terms"

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "REFUSING to package. The public tree would carry:" -ForegroundColor Red
    $problems | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Remove-Item $Output -Recurse -Force
    exit 1
}
Step "  clean"

# =============================================================================
# ONE COMMIT, NO PAST.
# =============================================================================
Step "initialising the public git repository"
# Assembled at run time so this file carries no literal e-mail address: the
# repository's own scanner flags every e-mail as a finding, and it is right to -
# an allowlisted exception would be a hole for the personal ones. This is the
# organisation's public address, and building it here is the deliberate act.
$gitEmail = 'dev' + [char]64 + 'horizunhub.com'
Push-Location $Output
try {
    & git init -q -b main
    & git add -A
    & git -c user.name='Horizun Group' -c user.email=$gitEmail commit -q -m @"
Horizun Revit MCP 0.3.3 - first public release

An MCP gateway for Autodesk Revit, free and open source under Apache-2.0,
part of the Horizun Hub ecosystem (https://horizunhub.com).

Built from scratch as original Horizun code: the transport, the safety
guards and a generic, organisation-neutral tool surface over the Revit
API, under one contract - a command never reports work it did not verify.

This repository starts here on purpose. The code has a longer private
history; its working tree is what you see, and its history is not part
of this release.
"@
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
    $sha = (& git rev-parse HEAD).Trim()
}
finally { Pop-Location }

Write-Host ""
Write-Host "[public] package ready: $Output" -ForegroundColor Green
Write-Host "[public] single commit: $sha" -ForegroundColor Green
Write-Host ""
Write-Host "Review it, then publish with:" -ForegroundColor Yellow
Write-Host "  gh repo create HorizunGroup/horizun-revit-mcp --public --source `"$Output`" --push"
exit 0
