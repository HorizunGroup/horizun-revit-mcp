#Requires -Version 5.1
<#
  WHAT A MERGE DELETED WITHOUT SAYING SO.

  Git reports a conflict when both sides touched a file. It reports NOTHING
  when one side deleted a file the other never touched - it simply applies the
  deletion. That is correct for source that was genuinely removed, and it is
  how `publish/overlay/LICENSE`, `CLAUDE.md` and `NOTICE` disappeared from this
  integration on the first attempt: the wall branch had modified only two of
  the six overlay files, so those two conflicted and the other four went
  silently. The projector iterates that directory. The package would have
  shipped without its licence, and no gate would have said a word.

  So this compares the tree against a set of REFERENCE refs and lists every
  path that exists in a reference and not here. It does not decide anything -
  restoring a file because another branch had it is exactly as wrong as losing
  one - it makes the deletions VISIBLE so each is decided on purpose.

  CRITICAL SURFACES are the ones where a silent loss is worst: the publication
  machinery, CI, the installer, contracts, manifests and release documentation.
  A deletion there fails this gate unless it appears in -Accept.

  Read-only. Exit 0 when every deletion on a critical surface is accounted for.
#>
[CmdletBinding()]
param(
    # The refs whose files must be accounted for. Defaults to the two branches
    # being integrated and the public release they are being integrated onto.
    [string[]]$Reference = @('feat/wall-layer-decomposition', 'codex/model-doctor-v2',
                             '02ef6aa3546622c1c2bd35f781dd5798ff3d480d'),
    # Paths deliberately absent, each of which SHOULD carry a reason in the
    # integration report. Accepting a deletion is a decision; making it here
    # keeps that decision in the repository rather than in somebody's memory.
    [string[]]$Accept = @(),
    [switch]$AllSurfaces
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A silent loss here costs the most: the ability to publish, to build, to
# install, or to know what the contract is.
$critical = @(
    '^publish/',
    '^\.github/',
    '^\.mcp/',
    '^installer/',
    '^scripts/',
    '^src/Horizun\.Contracts/',
    '^docs/(RELEASE-POLICY|SIGNPATH-ONBOARDING|security-model|PRIVACY|production-readiness)\.md$',
    '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES\.md|CHANGELOG\.md|SECURITY\.md|CODE-SIGNING-POLICY\.md)$',
    '^(Directory\.Build\.props|global\.json|install\.ps1|install-release\.ps1)$',
    'packages\.lock\.json$',
    '\.csproj$',
    '\.iss$'
)

function Is-Critical([string]$path) {
    foreach ($p in $critical) { if ($path -match $p) { return $true } }
    return $false
}

$here = @(git ls-files) | Sort-Object -Unique
$hereSet = [Collections.Generic.HashSet[string]]::new([string[]]$here, [StringComparer]::Ordinal)

$missing = [ordered]@{}
foreach ($ref in $Reference) {
    $theirs = @(git ls-tree -r --name-only $ref)
    if ($LASTEXITCODE -ne 0) { throw "cannot read ref '$ref'" }
    foreach ($f in $theirs) {
        if ($hereSet.Contains($f)) { continue }
        if (-not $missing.Contains($f)) { $missing[$f] = New-Object System.Collections.ArrayList }
        $null = $missing[$f].Add($ref)
    }
}

$acceptSet = [Collections.Generic.HashSet[string]]::new([string[]]$Accept, [StringComparer]::Ordinal)
$criticalHits = New-Object System.Collections.ArrayList
$otherHits = New-Object System.Collections.ArrayList

foreach ($f in $missing.Keys) {
    $row = [pscustomobject]@{
        Path     = $f
        PresentIn = ($missing[$f] -join ', ')
        Accepted = $acceptSet.Contains($f)
    }
    if (Is-Critical $f) { $null = $criticalHits.Add($row) } else { $null = $otherHits.Add($row) }
}

Write-Host ''
Write-Host ('  tracked here: {0}   absent-but-present-in-a-reference: {1}' -f $here.Count, $missing.Count)

$unaccepted = @($criticalHits | Where-Object { -not $_.Accepted })

if ($criticalHits.Count -gt 0) {
    Write-Host ''
    Write-Host '  CRITICAL SURFACES missing from this tree:' -ForegroundColor $(if ($unaccepted.Count) { 'Red' } else { 'Yellow' })
    foreach ($r in $criticalHits) {
        $mark = if ($r.Accepted) { 'accepted' } else { 'UNACCOUNTED' }
        Write-Host ('    {0,-12} {1}   (present in: {2})' -f $mark, $r.Path, $r.PresentIn) `
            -ForegroundColor $(if ($r.Accepted) { 'DarkGray' } else { 'Red' })
    }
}

if ($AllSurfaces -and $otherHits.Count -gt 0) {
    Write-Host ''
    Write-Host ('  other paths absent here ({0}):' -f $otherHits.Count) -ForegroundColor DarkGray
    foreach ($r in $otherHits) { Write-Host ('    {0}   (present in: {1})' -f $r.Path, $r.PresentIn) -ForegroundColor DarkGray }
}
elseif ($otherHits.Count -gt 0) {
    Write-Host ('  ({0} non-critical path(s) also absent; -AllSurfaces to list them)' -f $otherHits.Count) -ForegroundColor DarkGray
}

Write-Host ''
if ($unaccepted.Count -gt 0) {
    Write-Host ('  FAIL: {0} deletion(s) on a critical surface are unaccounted for.' -f $unaccepted.Count) -ForegroundColor Red
    Write-Host '  Decide each one and pass it in -Accept with a reason in the integration report,' -ForegroundColor Red
    Write-Host '  or restore it. An unexplained deletion here is how a release loses its licence.' -ForegroundColor Red
    exit 1
}
Write-Host '  PASS: no unaccounted deletion on a critical surface.' -ForegroundColor Green
exit 0
