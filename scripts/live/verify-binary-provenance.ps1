<#
.SYNOPSIS
  Tie every SIGNED add-in that a live campaign loaded back to the source it was
  built from, without trusting the commit field the campaign wrote about itself.

.DESCRIPTION
  A live artifact says "commit 36dd4f8" because the harness asked the running
  add-in and the add-in answered. That is a claim by the thing under test. This
  script produces the independent half:

    1. It checks out the CANDIDATE COMMIT into a throwaway worktree and builds
       the add-in there, per year, with the same command install.ps1 uses.
    2. It reads the MVID - the module version id the compiler writes into the
       metadata - from the freshly built assembly and from the signed file the
       campaign actually loaded. Signing appends to the PE; it does not touch
       the metadata, so the MVID survives it.
    3. Equal MVIDs mean the signed file IS that compilation. Different MVIDs
       mean it is not, whatever either file says about its commit.

  It also records, for each signed file, the SHA-256 the campaign recorded, the
  SHA-256 on disk today, and the Authenticode status - so a file that was
  replaced or resigned since the campaign is visible rather than assumed.

  Nothing here reverses a signature or recomputes a hash that was not saved. A
  year whose signed file no longer exists is reported as unrecoverable, because
  it is.

.PARAMETER Commit
  The candidate the campaign measured.

.PARAMETER Years
  Revit years to verify.

.PARAMETER SignedRoot
  Where the development sessions left their signed copies. Default is the
  dev-addin store dev-addin-session.ps1 writes to.

.PARAMETER Recorded
  Optional acceptance JSON. When given, the add-in SHA-256 each run recorded is
  compared with the file on disk, and a year the record does not mention is
  reported rather than silently skipped.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Commit,
    [string[]]$Years = @('2023', '2024', '2025', '2026', '2027'),
    [string]$SignedRoot = (Join-Path $env:USERPROFILE '.horizun\dev-addin'),
    [string]$Recorded,
    # WHERE THE RUNS KEPT THEIR OWN BINARIES. run-year-matrix.ps1 copies the
    # signed file Revit loaded and the unsigned build it came from into
    # <ArtifactRoot>\<year>\binaries, named by hash, precisely so a later
    # session signing over the development store does not make them unrecoverable.
    [string[]]$KeptRoot = @(),
    [string]$Out
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-Mvid([string]$path) {
    # FileShare.ReadWrite: Revit may have the file open, and a provenance check
    # that needs the machine idle is a provenance check nobody runs.
    $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $pe = New-Object System.Reflection.PortableExecutable.PEReader($fs)
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        return $md.GetGuid($md.GetModuleDefinition().Mvid).ToString()
    }
    finally { $fs.Dispose() }
}

function Get-Sha([string]$path) {
    $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLower()
    }
    finally { $fs.Dispose() }
}

$recordedByYear = @{}
if ($Recorded) {
    if (-not (Test-Path -LiteralPath $Recorded)) { throw "no acceptance record at $Recorded" }
    $rec = Get-Content -LiteralPath $Recorded -Raw | ConvertFrom-Json
    foreach ($run in $rec.harness_runs) {
        $y = [string]$run.revit_year
        if (-not $recordedByYear.ContainsKey($y)) { $recordedByYear[$y] = @() }
        if ($run.addin_sha256 -and $recordedByYear[$y] -notcontains $run.addin_sha256) {
            $recordedByYear[$y] += $run.addin_sha256
        }
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('horizun-provenance-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$rows = @()
$built = @{}

try {
    & git -C $repo worktree add --detach $work $Commit 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not check out $Commit into $work" }
    $resolved = (& git -C $work rev-parse HEAD).Trim()
    Write-Host "[provenance] candidate $resolved checked out at $work"

    # Build OUTSIDE the worktree. src/Directory.Build.props stamps the assembly
    # with `git status --porcelain`, so a single untracked file in the worktree -
    # including this script's own build output - appends `-dirty` to the stamped
    # revision, changes the compiled constant, and the rebuild stops matching the
    # very binary it is supposed to vouch for. That is the stamp doing its job;
    # it is this script that has to stay out of the way.
    $outRoot = "$work-out"
    New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
    foreach ($y in $Years) {
        $dirtyRaw = @(& git -C $work status --porcelain 2>$null)
        $dirty = ($dirtyRaw -join "`n").Trim()
        if ($dirty -ne '') {
            throw "the candidate worktree is dirty before building $y, so the stamp would say -dirty and no rebuild could match a clean binary:`n$dirty"
        }
        $outDir = Join-Path $outRoot $y
        & dotnet build (Join-Path $work 'src\Horizun.Revit\Horizun.Revit.csproj') `
            -c Release -p:RevitYear=$y -o $outDir -v q --nologo 2>&1 | Out-Null
        $dll = Join-Path $outDir 'Horizun.Revit.dll'
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll)) {
            $built[$y] = $null
            Write-Host "[provenance] $y  BUILD FAILED at the candidate" -ForegroundColor Yellow
            continue
        }
        $built[$y] = [ordered]@{ mvid = (Get-Mvid $dll); sha256 = (Get-Sha $dll) }
        Write-Host ("[provenance] {0}  rebuilt  mvid={1}" -f $y, $built[$y].mvid)
    }
}
finally {
    if ($outRoot -and (Test-Path -LiteralPath $outRoot)) {
        Remove-Item -LiteralPath $outRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $work) {
        & git -C $repo worktree remove --force $work 2>&1 | Out-Null
        & git -C $repo worktree prune 2>&1 | Out-Null
    }
}

foreach ($y in $Years) {
    $signed = Join-Path $SignedRoot ("$y\Horizun\Horizun.Revit.dll")
    $row = [ordered]@{
        revit_year = $y
        candidate_commit = $Commit
        rebuilt_mvid = if ($built[$y]) { $built[$y].mvid } else { $null }
        rebuilt_unsigned_sha256 = if ($built[$y]) { $built[$y].sha256 } else { $null }
        signed_path = $signed
        signed_present = (Test-Path -LiteralPath $signed)
        signed_sha256 = $null
        signed_mvid = $null
        authenticode = $null
        signer = $null
        recorded_addin_sha256 = if ($recordedByYear.ContainsKey($y)) { $recordedByYear[$y] } else { @() }
        # A year measured in more than one session has more than one signed file,
        # and only the LAST one is still on disk: the next session signs over it.
        # Naming the ones that are gone is the difference between an incomplete
        # record and a record that looks complete.
        recorded_not_on_disk = @()
        recorded_kept = @()
        verdict = $null
        why = $null
    }
    if (-not $row.signed_present) {
        $row.verdict = 'unrecoverable'
        $row.why = 'the signed file this campaign loaded is no longer on disk; its bytes cannot be re-derived and nothing here invents them'
        $rows += $row; continue
    }
    $row.signed_sha256 = Get-Sha $signed
    $row.signed_mvid = Get-Mvid $signed
    # A recorded hash is only unrecoverable if NOBODY kept the file. Look for a
    # preserved copy before saying so, and check that copy the same way.
    $row.recorded_kept = @()
    $stillMissing = @()
    foreach ($otherSha in @($row.recorded_addin_sha256 | Where-Object { $_ -ne $row.signed_sha256 })) {
        $found = $null
        foreach ($root in $KeptRoot) {
            $candidate = Join-Path $root ("$y\binaries\addin_signed-" + $otherSha.Substring(0, 16) + ".dll")
            if (Test-Path -LiteralPath $candidate) { $found = $candidate; break }
        }
        if (-not $found) { $stillMissing += $otherSha; continue }
        $keptSha = Get-Sha $found
        $keptMvid = Get-Mvid $found
        $row.recorded_kept += [ordered]@{
            sha256 = $otherSha; kept_at = $found; sha256_on_disk = $keptSha; mvid = $keptMvid
            matches_candidate = ($keptMvid -eq $row.rebuilt_mvid)
            intact = ($keptSha -eq $otherSha)
        }
    }
    $row.recorded_not_on_disk = $stillMissing
    $sig = Get-AuthenticodeSignature -FilePath $signed
    $row.authenticode = [string]$sig.Status
    $row.signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { $null }

    if (-not $row.rebuilt_mvid) {
        $row.verdict = 'not_rebuilt'
        $row.why = 'the candidate did not build for this year on this machine'
    }
    elseif ($row.signed_mvid -ne $row.rebuilt_mvid) {
        $row.verdict = 'mismatch'
        $row.why = "the signed file is NOT a build of ${Commit}: mvid $($row.signed_mvid) against $($row.rebuilt_mvid)"
    }
    elseif ($row.recorded_addin_sha256.Count -gt 0 -and ($row.recorded_addin_sha256 -notcontains $row.signed_sha256)) {
        $row.verdict = 'same_source_other_file'
        $row.why = 'the signed file on disk is a build of the candidate, but its SHA-256 is not one the record names: the file was re-signed or replaced after the campaign'
    }
    else {
        $row.verdict = 'verified'
        $row.why = 'the signed file the campaign loaded is a build of the candidate: identical MVID, and its SHA-256 is one the record names'
        if ($row.recorded_kept.Count -gt 0) {
            $kept = @($row.recorded_kept | Where-Object { $_.intact -and $_.matches_candidate }).Count
            $row.why += ('. ' + $row.recorded_kept.Count + ' other signed file(s) this year recorded were KEPT by their run; ' +
                         $kept + ' of them are intact and build from the same candidate.')
        }
        if ($row.recorded_not_on_disk.Count -gt 0) {
            $row.why += ('. ' + $row.recorded_not_on_disk.Count + ' other signed file(s) this year recorded were signed over by a later session, nobody kept a copy, and they can no longer be checked: ' + ($row.recorded_not_on_disk -join ', '))
        }
    }
    $rows += $row
}

$doc = [ordered]@{
    schema = 'horizun.binary-provenance/1'
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    candidate_commit = $Commit
    means = 'MVID equality between a rebuild of the candidate and the signed file the campaign loaded. Signing appends to the PE and leaves the metadata alone, so the MVID crosses it; the file hash does not, because an RFC-3161 timestamp countersigns the moment of signing.'
    binaries = $rows
}
if (-not $Out) { $Out = Join-Path $repo 'artifacts\live\binary-provenance.json' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
($doc | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $Out -Encoding utf8

foreach ($r in $rows) {
    $colour = switch ($r.verdict) {
        'verified' { 'Green' }
        'unrecoverable' { 'Yellow' }
        default { 'Red' }
    }
    Write-Host ("  {0}  {1,-22} {2}" -f $r.revit_year, $r.verdict, $r.why) -ForegroundColor $colour
}
Write-Host "wrote $Out"
$bad = @($rows | Where-Object { $_.verdict -eq 'mismatch' -or $_.verdict -eq 'not_rebuilt' })
if ($bad.Count -gt 0) { exit 1 }
exit 0
