#Requires -Version 5.1
<#
  ISO 19650 INFORMATION MANAGEMENT, LIVE.

  The probes for horizun_export with an information container,
  horizun_project_context and horizun_information_container, dot-sourced by
  scripts/verify-live.ps1 and exercised WITHOUT Revit by
  scripts/live-iso19650.tests.ps1. Every function talks to the bridge only
  through the -Call / -Apply scriptblocks it is handed, so the tests can drive
  the same code with recorded replies before a matrix run pays for Revit.

  WHAT THIS SECTION REFUSES TO ASSUME

    the permission profile  host-resident writes (stamp, transition) need
                            full_write. The probe reads the profile from health
                            and then believes the REPLY: a refusal must name the
                            profile, a success must be a verified, logged copy.
    the model               nothing here writes a parameter. The export cases
                            write files OUTSIDE the model, into the run folder.

  Every temporary file lives in ONE folder per run under the harness scratch
  directory (never under the repository), and every record is sanitised before
  it can reach a report: a temp path carries the account name.
#>

# -----------------------------------------------------------------------------
# Sanitising. A record read by somebody who was not here must not carry the
# machine's profile path, in either of the two shapes it travels in.
# -----------------------------------------------------------------------------
function Protect-HorizunIsoText {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    # Most specific first: the temp folder usually lives under LOCALAPPDATA.
    $roots = @(
        @{ Root = ([IO.Path]::GetTempPath().TrimEnd('\', '/')); Token = '<temp>' },
        @{ Root = $env:TEMP; Token = '<temp>' },
        @{ Root = $env:LOCALAPPDATA; Token = '<localappdata>' },
        @{ Root = $env:APPDATA; Token = '<appdata>' },
        @{ Root = $env:USERPROFILE; Token = '<userprofile>' }
    )
    foreach ($pair in $roots) {
        if (-not [string]::IsNullOrEmpty($pair.Root) -and $pair.Root.Length -ge 3) {
            $out = $out.Replace($pair.Root, $pair.Token)
            # JSON-escaped form of the same path, as it appears inside a quoted reply.
            $out = $out.Replace($pair.Root.Replace('\', '\\'), $pair.Token)
        }
    }
    $out = [regex]::Replace($out, '[A-Za-z]:[\\/]+Users[\\/]+[A-Za-z0-9._-]+', '<user-profile>')
    $out = [regex]::Replace($out, '[A-Za-z]--Users-[A-Za-z0-9._]+-', '<user-profile-dir>-')
    if ($env:USERNAME -and $env:USERNAME.Length -ge 3) {
        $out = [regex]::Replace($out, [regex]::Escape($env:USERNAME), '<account>',
                                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    if ($env:COMPUTERNAME -and $env:COMPUTERNAME.Length -ge 3) {
        $out = [regex]::Replace($out, [regex]::Escape($env:COMPUTERNAME), '<machine>',
                                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    return $out
}

function Protect-HorizunIsoValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return (Protect-HorizunIsoText $Value) }
    $json = $Value | ConvertTo-Json -Depth 40 -Compress
    return ((Protect-HorizunIsoText $json) | ConvertFrom-Json)
}

function Limit-HorizunIsoText {
    param([string]$Text, [int]$Max = 400)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $clean = Protect-HorizunIsoText $Text
    if ($clean.Length -le $Max) { return $clean }
    return ($clean.Substring(0, $Max) + '...')
}

# -----------------------------------------------------------------------------
# The case catalog. Named FIRST, so a gated run reports every case NOT COVERED
# by name instead of quietly shrinking the denominator, and so every Revit year
# reports the same set of rows to the matrix.
# -----------------------------------------------------------------------------
function Get-HorizunIsoCaseCatalog {
    return @(
        [pscustomobject]@{ Id = 'ISO-E1'; Tier = 'write'; Tool = 'horizun_export'
            Name = 'export ifc with information_container names the file after the container and seals it (verify = match)' }
        [pscustomobject]@{ Id = 'ISO-E2'; Tier = 'write'; Tool = 'horizun_export'
            Name = 'a repeated export onto a sealed name is refused whatever overwrite says, and nothing is exported' }
        [pscustomobject]@{ Id = 'ISO-E3'; Tier = 'write'; Tool = 'horizun_export'
            Name = 'export pdf of a real sheet with information_container seals one combined file' }
        [pscustomobject]@{ Id = 'ISO-H1'; Tier = 'host'; Tool = 'horizun_project_context'
            Name = 'project_context validate keeps invalid, inconsistent and incomplete apart' }
        [pscustomobject]@{ Id = 'ISO-H2'; Tier = 'host'; Tool = 'horizun_project_context'
            Name = 'project_context questions returns the ordered bilingual intake and counts an answered field' }
        [pscustomobject]@{ Id = 'ISO-H3'; Tier = 'host'; Tool = 'horizun_project_context'
            Name = 'project_context draft rehearsal returns the drafted document and writes no file' }
        [pscustomobject]@{ Id = 'ISO-H4'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'inspect of a temporary CDE names the bad name, the missing sidecar and the missing and overdue deliverables, and writes nothing' }
        [pscustomobject]@{ Id = 'ISO-H5'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'shared->published without approved_by is refused and nothing is written' }
        [pscustomobject]@{ Id = 'ISO-H6'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'shared->published with approved_by rehearses every check on a sealed container and writes nothing' }
        [pscustomobject]@{ Id = 'ISO-H7'; Tier = 'host'; Tool = 'horizun_information_container'
            Name = 'shared->published apply lands a verified, logged copy that replays as already_transitioned, or the permission profile refuses it with nothing written' }
    )
}

# One place turns a verify-live outcome into the consolidator's status. An
# unknown outcome is a harness bug and throws, rather than becoming a new bucket.
function ConvertTo-HorizunIsoStatus {
    param([string]$Outcome)
    switch ($Outcome) {
        'pass' { return 'passed' }
        'fail' { return 'failed' }
        'unverified' { return 'unverified' }
        'not_covered' { return 'not_covered' }
    }
    throw "HARNESS: unknown outcome '$Outcome'; the ISO section knows pass, fail, unverified and not_covered."
}

function New-HorizunIsoRecorder {
    return [pscustomobject]@{
        Catalog = @(Get-HorizunIsoCaseCatalog)
        Results = @{}
    }
}

# Record one case. A second verdict for the same case is a harness bug (a close
# that ran twice writes every verdict twice), so the FIRST one stands and the
# repetition is reported inside its detail.
function Complete-HorizunIsoCase {
    param($Recorder, [string]$Id, [string]$Outcome, [string]$Detail, $Evidence)
    $entry = $Recorder.Catalog | Where-Object { $_.Id -eq $Id } | Select-Object -First 1
    if (-not $entry) { throw "HARNESS: '$Id' is not in the ISO case catalog." }
    $status = ConvertTo-HorizunIsoStatus $Outcome
    if ($Recorder.Results.ContainsKey($Id)) {
        $Recorder.Results[$Id].detail = $Recorder.Results[$Id].detail +
            (' [HARNESS: a second verdict ({0}) was offered for this case and ignored]' -f $Outcome)
        return
    }
    $Recorder.Results[$Id] = [pscustomobject]@{
        id = $Id; name = $entry.Name; tool = $entry.Tool; tier = $entry.Tier
        outcome = $Outcome; status = $status
        detail = (Limit-HorizunIsoText $Detail 1200)
        evidence = (Protect-HorizunIsoValue $Evidence)
        recorded_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Complete-HorizunIsoTier {
    param($Recorder, [string]$Tier, [string]$Outcome, [string]$Detail)
    foreach ($entry in $Recorder.Catalog) {
        if ($entry.Tier -eq $Tier -and -not $Recorder.Results.ContainsKey($entry.Id)) {
            Complete-HorizunIsoCase $Recorder $entry.Id $Outcome $Detail $null
        }
    }
}

# Every catalog case, in catalog order. One that never received a verdict is
# UNVERIFIED and says so: the section ended before it ran, which is a harness
# defect and must not read like a pass or disappear.
function Close-HorizunIsoRecorder {
    param($Recorder)
    $rows = @()
    foreach ($entry in $Recorder.Catalog) {
        if (-not $Recorder.Results.ContainsKey($entry.Id)) {
            Complete-HorizunIsoCase $Recorder $entry.Id 'unverified' 'the ISO section ended before this case ran - a harness defect, not a product result' $null
        }
        $rows += $Recorder.Results[$entry.Id]
    }
    return $rows
}

# -----------------------------------------------------------------------------
# The run folder. One per run, under the harness scratch directory, never inside
# the repository.
# -----------------------------------------------------------------------------
function New-HorizunIsoRunDirectory {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$RunId, [string]$RepoRoot)
    if ($RunId -notmatch '^[A-Za-z0-9_-]{6,64}$') { throw "HARNESS: run id '$RunId' is not a safe folder name." }
    $rootFull = [IO.Path]::GetFullPath($Root)
    $path = [IO.Path]::GetFullPath((Join-Path $rootFull ('iso19650-' + $RunId)))
    if ($RepoRoot) {
        $repoFull = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($path.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
            throw "HARNESS: refusing to stage ISO probe files inside the repository ($repoFull)."
        }
    }
    if (Test-Path -LiteralPath $path) { throw "HARNESS: the run folder already exists: $path. A run folder is never reused." }
    $null = New-Item -ItemType Directory -Force -Path $path
    return $path
}

# Removes the run folder only when it is exactly the one this run made: under
# the given root, named for the run. Kept (and said so) when any case did not
# pass, because the files are then the evidence somebody needs.
function Remove-HorizunIsoRunDirectory {
    param([string]$Path, [string]$Root, [string]$RunId)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return $false }
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $full) -ne ('iso19650-' + $RunId)) {
        throw "HARNESS: refusing to delete '$full': it is not this run's ISO folder under $rootFull."
    }
    Remove-Item -LiteralPath $full -Recurse -Force
    return $true
}

function Get-HorizunIsoFileStamp {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    $sha = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return ('{0}|{1}|{2}' -f $sha, $item.Length, $item.LastWriteTimeUtc.Ticks)
}

# Every file under a folder with its size and write time. Two equal snapshots
# mean nothing under the folder was created, changed or removed.
function Get-HorizunIsoFolderSnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $base = [IO.Path]::GetFullPath($Path)
    $lines = @(Get-ChildItem -LiteralPath $base -Recurse -Force -File -ErrorAction SilentlyContinue |
        Sort-Object FullName | ForEach-Object {
            '{0}|{1}|{2}' -f $_.FullName.Substring($base.Length), $_.Length, $_.LastWriteTimeUtc.Ticks })
    return ($lines -join "`n")
}

# -----------------------------------------------------------------------------
# Pure helpers: the container and its verification.
# -----------------------------------------------------------------------------

# The seven ISO 19650-2 fields with their default patterns, so the name is
# checked by the SAME defaults the bridge ships.
function New-HorizunIsoContainer {
    param([Parameter(Mandatory = $true)][string]$Number, [string]$Type = 'M3', [string]$Status = 'S2',
          [string]$Revision = 'P01', [string]$Title = 'Horizun live probe')
    return @{
        fields = @{ project = 'HZ01'; originator = 'HRZ'; volume = 'ZZ'; level = 'XX'; type = $Type; role = 'A'; number = $Number }
        field_order = @('project', 'originator', 'volume', 'level', 'type', 'role', 'number')
        separator = '-'
        status = $Status
        revision = $Revision
        title = $Title
    }
}

function Get-HorizunIsoContainerName {
    param([Parameter(Mandatory = $true)]$Container)
    $parts = @()
    foreach ($field in $Container.field_order) { $parts += [string]$Container.fields[$field] }
    return ($parts -join $Container.separator)
}

function Copy-HorizunIsoArgs {
    param([hashtable]$Arguments, [hashtable]$Set)
    $copy = @{}
    foreach ($key in $Arguments.Keys) { $copy[$key] = $Arguments[$key] }
    if ($Set) { foreach ($key in $Set.Keys) { $copy[$key] = $Set[$key] } }
    return $copy
}

function Invoke-HorizunIsoVerify {
    param([scriptblock]$Call, [string]$FilePath)
    $verify = & $Call 'horizun_information_container' @{ operation = 'verify'; file_path = $FilePath }
    if ($verify.isError -or -not $verify.data) {
        return [pscustomobject]@{ ok = $false; verdict = $null; sha256 = $null; text = $verify.text }
    }
    return [pscustomobject]@{
        ok = ([string]$verify.data.verdict -eq 'match' -and $verify.data.matches -eq $true)
        verdict = [string]$verify.data.verdict; sha256 = [string]$verify.data.sha256; text = $null
    }
}

# -----------------------------------------------------------------------------
# 1. horizun_export with an information container.
# -----------------------------------------------------------------------------
function New-HorizunIsoCde {
    param([Parameter(Mandatory = $true)][string]$Root)
    foreach ($state in @('wip', 'shared', 'published', 'archived')) {
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $Root $state)
    }
    return @{ wip = 'wip'; shared = 'shared'; published = 'published'; archived = 'archived' }
}

function Invoke-HorizunIsoExportProbes {
    param([string]$Document, [string]$RunDirectory, [string]$RunId, [scriptblock]$Call, [scriptblock]$Apply, $Recorder)
    $cdeRoot = Join-Path $RunDirectory 'cde-export'
    $states = New-HorizunIsoCde $cdeRoot
    $shared = Join-Path $cdeRoot 'shared'
    $container = New-HorizunIsoContainer -Number '0002' -Type 'M3'
    $name = Get-HorizunIsoContainerName $container
    $requested = Join-Path $shared 'requested-name.ifc'
    $expected = Join-Path $shared ($name + '.ifc')
    $request = @{ target_document = $Document; format = 'ifc'; ifc_version = 'IFC4'; output_path = $requested
                  information_container = $container }
    $sealed = $null

    # ---- E1 ---------------------------------------------------------------------
    $r = & $Apply 'horizun_export' $request 'iso-export-ifc'
    if ($r.stage -ne 'apply') {
        Complete-HorizunIsoCase $Recorder 'ISO-E1' 'unverified' ('the export rehearsal issued no token: ' + (Limit-HorizunIsoText $r.answer.text 300)) $null
    } elseif ($r.answer.isError -or -not $r.answer.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-E1' 'fail' ('the export with a container failed: ' + (Limit-HorizunIsoText $r.answer.text 300)) $null
    } else {
        $ic = $r.answer.data.information_container
        $verify = Invoke-HorizunIsoVerify -Call $Call -FilePath $expected
        $problems = @()
        if (-not $ic) { $problems += 'the reply carries no information_container block' }
        else {
            if ([string]$ic.name -ne $name) { $problems += ('container name is ' + [string]$ic.name) }
            if (-not [string]::Equals([string]$ic.file, $expected, [StringComparison]::OrdinalIgnoreCase)) { $problems += ('file is ' + [string]$ic.file + ', expected the container name beside output_path') }
        }
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) { $problems += 'no file carries the container name' }
        if (Test-Path -LiteralPath $requested) { $problems += 'a file was also written under the requested name' }
        if (-not (Test-Path -LiteralPath ($expected + '.container.json') -PathType Leaf)) { $problems += 'no sidecar beside the file' }
        if (-not $verify.ok) { $problems += ('verify: ' + $(if ($verify.verdict) { $verify.verdict } else { Limit-HorizunIsoText $verify.text 200 })) }
        if ($problems.Count -eq 0) {
            $sealed = $expected
            Complete-HorizunIsoCase $Recorder 'ISO-E1' 'pass' ('the IFC took the container name ' + $name + ' (not the requested one), was sealed, and verify reads match') `
                @{ name = $name; verify = $verify.verdict; sha256 = $verify.sha256 }
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-E1' 'fail' ($problems -join '; ') $null
        }
    }

    # ---- E2 ---------------------------------------------------------------------
    if (-not $sealed) {
        Complete-HorizunIsoCase $Recorder 'ISO-E2' 'unverified' 'no sealed export exists to be refused (ISO-E1 did not pass)' $null
    } else {
        $fileBefore = Get-HorizunIsoFileStamp $sealed
        $folderBefore = Get-HorizunIsoFolderSnapshot $shared
        $again = & $Call 'horizun_export' (Copy-HorizunIsoArgs $request @{ overwrite = $true; dry_run = $false
                                                                          idempotency_key = ('live-iso-export-reseal-' + $RunId) })
        $unchanged = ((Get-HorizunIsoFileStamp $sealed) -eq $fileBefore -and (Get-HorizunIsoFolderSnapshot $shared) -eq $folderBefore)
        if ($again.isError -and $again.text -match 'sidecar' -and $again.text -match 'never overwritten' -and
            $again.text -match 'Nothing was exported' -and $unchanged) {
            Complete-HorizunIsoCase $Recorder 'ISO-E2' 'pass' ('overwrite=true onto the sealed name was refused before export; the file and the folder are unchanged') $null
        } elseif (-not $again.isError) {
            Complete-HorizunIsoCase $Recorder 'ISO-E2' 'fail' 'a second export onto the sealed name was ACCEPTED' $null
        } else {
            Complete-HorizunIsoCase $Recorder 'ISO-E2' 'fail' ('refused=' + $again.isError + ' unchanged=' + $unchanged + ': ' + (Limit-HorizunIsoText $again.text 300)) $null
        }
    }

    # ---- E3: a PDF, only if the model has a real sheet ---------------------------
    $sheets = & $Call 'horizun_query_planimetry' @{ mode = 'sheets'; units = 'mm' }
    if ($sheets.isError -or -not $sheets.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-E3' 'unverified' ('the sheets could not be listed: ' + (Limit-HorizunIsoText $sheets.text 200)) $null
    } else {
        $sheet = @($sheets.data.rows) | Where-Object { $_.placeholder -eq $false -and $null -ne $_.sheet_id } | Select-Object -First 1
        if (-not $sheet) {
            Complete-HorizunIsoCase $Recorder 'ISO-E3' 'not_covered' 'the disposable model has no non-placeholder sheet to print' $null
        } else {
            $pdfContainer = New-HorizunIsoContainer -Number '0003' -Type 'DR'
            $pdfName = Get-HorizunIsoContainerName $pdfContainer
            $pdfExpected = Join-Path $shared ($pdfName + '.pdf')
            $p = & $Apply 'horizun_export' @{ target_document = $Document; format = 'pdf'; view_ids = @([long]$sheet.sheet_id)
                                               output_path = (Join-Path $shared 'requested-sheet.pdf'); information_container = $pdfContainer } 'iso-export-pdf'
            if ($p.stage -ne 'apply') {
                Complete-HorizunIsoCase $Recorder 'ISO-E3' 'unverified' ('the PDF rehearsal issued no token: ' + (Limit-HorizunIsoText $p.answer.text 300)) $null
            } elseif ($p.answer.isError -or -not $p.answer.data -or -not $p.answer.data.information_container) {
                Complete-HorizunIsoCase $Recorder 'ISO-E3' 'fail' ('the PDF export with a container failed: ' + (Limit-HorizunIsoText $p.answer.text 300)) $null
            } else {
                $verify = Invoke-HorizunIsoVerify -Call $Call -FilePath $pdfExpected
                if ((Test-Path -LiteralPath $pdfExpected -PathType Leaf) -and $verify.ok -and
                    [string]$p.answer.data.information_container.name -eq $pdfName) {
                    Complete-HorizunIsoCase $Recorder 'ISO-E3' 'pass' ('sheet ' + [string]$sheet.sheet_number + ' printed as one combined PDF named ' + $pdfName + ' and sealed (verify = match)') `
                        @{ sheet_id = $sheet.sheet_id; verify = $verify.verdict }
                } else {
                    Complete-HorizunIsoCase $Recorder 'ISO-E3' 'fail' ('file present=' + (Test-Path -LiteralPath $pdfExpected) + ' verify=' + $verify.verdict) $null
                }
            }
        }
    }
    return [pscustomobject]@{ cde_root = $cdeRoot; states = $states; sealed_source = $sealed }
}

# -----------------------------------------------------------------------------
# 2. Host-resident: project context and the CDE. No Revit is needed for any of
#    these; they run in every verify-live run, write tier or not.
# -----------------------------------------------------------------------------
function Test-HorizunIsoProfileAllowsExternalWrite {
    param([string]$PermissionProfile)
    if ([string]::IsNullOrWhiteSpace($PermissionProfile)) { return $null }
    return ($PermissionProfile -eq 'full_write' -or $PermissionProfile -eq 'unsafe_code')
}

function Invoke-HorizunIsoHostProbes {
    param([string]$RunDirectory, [string]$RunId, [scriptblock]$Call, $Recorder, [string]$PermissionProfile,
          [string]$SealedSource, [string]$SealedCdeRoot)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $ctxDir = Join-Path $RunDirectory 'context'
    $null = New-Item -ItemType Directory -Force -Path $ctxDir
    $invalid = Join-Path $ctxDir 'invalid.json'
    $inconsistent = Join-Path $ctxDir 'inconsistent.json'
    $incomplete = Join-Path $ctxDir 'incomplete.json'
    [IO.File]::WriteAllText($invalid, '{"schema_version":1,"project":{}}', $utf8)
    [IO.File]::WriteAllText($inconsistent, ('{"schema_version":1,"project":{"code":"HZ01"},"naming":{"status_codes":{"S2":"shared"}},' +
        '"deliverables":[{"container":"HZ01-HRZ-ZZ-XX-M3-A-0001","required_status":"A1"}]}'), $utf8)
    [IO.File]::WriteAllText($incomplete, '{"schema_version":1,"project":{"code":"HZ01"}}', $utf8)

    # ---- H1 -----------------------------------------------------------------
    $states = @{}
    $answers = @{}
    foreach ($pair in @(@('invalid', $invalid), @('inconsistent', $inconsistent), @('incomplete', $incomplete))) {
        $v = & $Call 'horizun_project_context' @{ operation = 'validate'; path = $pair[1] }
        $answers[$pair[0]] = $v
        $states[$pair[0]] = $(if ($v.isError -or -not $v.data) { '(error)' } else { [string]$v.data.state })
    }
    $problems = @()
    if ($states.invalid -ne 'invalid' -or @($answers.invalid.data.errors).Count -eq 0) { $problems += ('invalid file read as ' + $states.invalid) }
    $rules = @()
    if ($answers.inconsistent.data) { $rules = @(@($answers.inconsistent.data.coherence) | ForEach-Object { [string]$_.rule }) }
    if ($states.inconsistent -ne 'inconsistent' -or $rules -notcontains 'unknown_status_code') { $problems += ('inconsistent file read as ' + $states.inconsistent + ' (rules: ' + ($rules -join ',') + ')') }
    if ($states.incomplete -ne 'incomplete' -or @($answers.incomplete.data.missing).Count -eq 0) { $problems += ('incomplete file read as ' + $states.incomplete) }
    $h1Outcome = 'pass'
    if (@($answers.Values | Where-Object { $_.isError -or -not $_.data }).Count -gt 0) { $h1Outcome = 'unverified' }
    elseif ($problems.Count -gt 0) { $h1Outcome = 'fail' }
    Complete-HorizunIsoCase $Recorder 'ISO-H1' $h1Outcome $(if ($problems.Count -eq 0) { 'three files, three verdicts: invalid (schema errors), inconsistent (unknown_status_code), incomplete (open intake questions)' } else { $problems -join '; ' }) $states

    # ---- H2 -----------------------------------------------------------------
    $all = & $Call 'horizun_project_context' @{ operation = 'questions' }
    $partial = & $Call 'horizun_project_context' @{ operation = 'questions'; path = $incomplete }
    if ($all.isError -or -not $all.data -or $partial.isError -or -not $partial.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-H2' 'unverified' ('questions failed: ' + (Limit-HorizunIsoText ($all.text + ' ' + $partial.text) 300)) $null
    } else {
        $qs = @($all.data.questions)
        $problems = @()
        # Without include_answered the list holds the PENDING questions only.
        if ($qs.Count -eq 0 -or [int]$all.data.pending -ne $qs.Count) { $problems += ('pending=' + $all.data.pending + ' but ' + $qs.Count + ' questions listed') }
        if ([int]$all.data.answered + [int]$all.data.not_applicable + [int]$all.data.pending -ne [int]$all.data.total) { $problems += 'answered + not_applicable + pending is not total' }
        if (-not $all.data.next_question_id -or [string]$all.data.next_question_id -ne [string]$qs[0].id) { $problems += 'next_question_id is not the first pending question' }
        $bad = @($qs | Where-Object { -not $_.id -or -not $_.pointer -or -not $_.text -or -not $_.text.es -or -not $_.text.en -or -not $_.why })
        if ($bad.Count -gt 0) { $problems += ([string]$bad.Count + ' question(s) lack id, pointer, or the es/en text and why') }
        $orders = @($qs | ForEach-Object { [int]$_.order })
        for ($i = 1; $i -lt $orders.Count; $i++) { if ($orders[$i] -lt $orders[$i - 1]) { $problems += 'the questions are not in intake order'; break } }
        if ([int]$partial.data.answered -lt 1) { $problems += 'project.code in the incomplete file was not counted as answered' }
        if ([string]$partial.data.source -ne 'file') { $problems += ('the incomplete file was read as source ' + [string]$partial.data.source) }
        Complete-HorizunIsoCase $Recorder 'ISO-H2' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
            $(if ($problems.Count -eq 0) { ('' + $qs.Count + ' ordered bilingual questions; with the incomplete file ' + $partial.data.answered + ' answered and ' + $partial.data.pending + ' pending') } else { $problems -join '; ' }) `
            @{ total = $all.data.total; pending_without_file = $all.data.pending; answered_with_file = $partial.data.answered }
    }

    # ---- H3 -----------------------------------------------------------------
    $draftPath = Join-Path $ctxDir 'drafted-project-context.json'
    $draft = & $Call 'horizun_project_context' @{ operation = 'draft'; path = $draftPath; dry_run = $true
                                                  answers = @{ '/project/code' = 'HZ01'; '/project/name' = 'Horizun live probe' } }
    if ($draft.isError -or -not $draft.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-H3' 'unverified' ('draft failed: ' + (Limit-HorizunIsoText $draft.text 300)) $null
    } else {
        $problems = @()
        if ($draft.data.dry_run -ne $true -or $draft.data.written -ne $false) { $problems += 'the rehearsal did not say dry_run=true, written=false' }
        if ([string]$draft.data.document.project.code -ne 'HZ01') { $problems += 'the drafted document lacks project.code' }
        if ([string]$draft.data.base -ne 'empty') { $problems += ('base is ' + [string]$draft.data.base) }
        if (Test-Path -LiteralPath $draftPath) { $problems += 'the rehearsal CREATED the file' }
        Complete-HorizunIsoCase $Recorder 'ISO-H3' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
            $(if ($problems.Count -eq 0) { ('drafted onto an empty context (state ' + [string]$draft.data.state + '); no file was created') } else { $problems -join '; ' }) $null
    }

    # ---- H4: inspect a temporary CDE -----------------------------------------
    $cdeRoot = Join-Path $RunDirectory 'cde-host'
    $cdeStates = New-HorizunIsoCde $cdeRoot
    [IO.File]::WriteAllText((Join-Path $cdeRoot 'wip\not a container name.txt'), 'probe', $utf8)
    $unsealed = Join-Path $cdeRoot 'shared\HZ01-HRZ-ZZ-XX-M3-A-0005.txt'
    [IO.File]::WriteAllText($unsealed, 'probe', $utf8)
    $asOf = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $deliverables = @(
        @{ container = 'HZ01-HRZ-ZZ-XX-M3-A-0006'; due = '2020-01-01'; required_status = 'S2' },
        @{ container = 'HZ01-HRZ-ZZ-XX-M3-A-0007'; due = '2999-12-31'; required_status = 'A1' }
    )
    $snapBefore = Get-HorizunIsoFolderSnapshot $cdeRoot
    $inspect = & $Call 'horizun_information_container' @{ operation = 'inspect'; root = $cdeRoot; states = $cdeStates
                                                         deliverables = $deliverables; as_of = $asOf }
    $snapAfter = Get-HorizunIsoFolderSnapshot $cdeRoot
    if ($inspect.isError -or -not $inspect.data) {
        Complete-HorizunIsoCase $Recorder 'ISO-H4' 'unverified' ('inspect failed: ' + (Limit-HorizunIsoText $inspect.text 300)) $null
    } else {
        $findings = @($inspect.data.findings)
        $kinds = @($findings | ForEach-Object { [string]$_.kind })
        $problems = @()
        foreach ($k in @('name_noncompliant', 'missing_sidecar', 'deliverable_overdue', 'deliverable_missing')) {
            if ($kinds -notcontains $k) { $problems += ('no ' + $k + ' finding') }
        }
        $overdue = $findings | Where-Object { $_.kind -eq 'deliverable_overdue' } | Select-Object -First 1
        if ($overdue -and [string]$overdue.container -ne 'HZ01-HRZ-ZZ-XX-M3-A-0006') { $problems += 'the overdue finding names the wrong deliverable' }
        $missing = $findings | Where-Object { $_.kind -eq 'deliverable_missing' } | Select-Object -First 1
        if ($missing -and [string]$missing.container -ne 'HZ01-HRZ-ZZ-XX-M3-A-0007') { $problems += 'the missing finding names the wrong deliverable' }
        if ($inspect.data.coverage_complete -ne $true) { $problems += 'coverage_complete is not true over four existing folders' }
        if ($snapBefore -ne $snapAfter) { $problems += 'inspect changed the CDE folders' }
        if (Test-Path -LiteralPath (Join-Path $cdeRoot '.horizun')) { $problems += 'inspect created .horizun' }
        Complete-HorizunIsoCase $Recorder 'ISO-H4' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
            $(if ($problems.Count -eq 0) { ('findings: ' + (($kinds | Sort-Object -Unique) -join ', ') + '; coverage complete; the folders are unchanged') } else { $problems -join '; ' }) `
            @{ finding_counts = $inspect.data.finding_counts; total_findings = $inspect.data.total_findings }
    }

    # ---- H5: the approval rule ------------------------------------------------
    $pubSnapshot = Get-HorizunIsoFolderSnapshot (Join-Path $cdeRoot 'published')
    $noApprover = & $Call 'horizun_information_container' @{ operation = 'transition'; root = $cdeRoot; states = $cdeStates
        file_path = $unsealed; from_state = 'shared'; to_state = 'published'; status = 'A1'; dry_run = $false }
    if ($noApprover.isError -and $noApprover.text -match 'approved_by' -and
        (Get-HorizunIsoFolderSnapshot (Join-Path $cdeRoot 'published')) -eq $pubSnapshot -and
        -not (Test-Path -LiteralPath (Join-Path $cdeRoot '.horizun'))) {
        Complete-HorizunIsoCase $Recorder 'ISO-H5' 'pass' 'an apply without approved_by was refused naming approved_by; published/ and the transition log are untouched' $null
    } elseif (-not $noApprover.isError) {
        Complete-HorizunIsoCase $Recorder 'ISO-H5' 'fail' 'shared->published WITHOUT approved_by was accepted' $null
    } else {
        Complete-HorizunIsoCase $Recorder 'ISO-H5' 'fail' ('refused for another reason, or something was written: ' + (Limit-HorizunIsoText $noApprover.text 300)) $null
    }

    # ---- H6/H7: a sealed source ------------------------------------------------
    $allows = Test-HorizunIsoProfileAllowsExternalWrite $PermissionProfile
    $source = $null; $sourceRoot = $null; $sourceStates = $null; $sourceOrigin = $null
    if ($SealedSource -and (Test-Path -LiteralPath $SealedSource -PathType Leaf) -and $SealedCdeRoot) {
        $source = $SealedSource; $sourceRoot = $SealedCdeRoot
        $sourceStates = @{ wip = 'wip'; shared = 'shared'; published = 'published'; archived = 'archived' }
        $sourceOrigin = 'the IFC sealed by horizun_export in this run'
    } elseif ($allows -ne $false) {
        # No export sealed anything. Seal the unsealed shared file host-side - the
        # stamp is itself a host-resident write, so it needs the same profile.
        $stamp = & $Call 'horizun_information_container' @{ operation = 'stamp'; file_path = $unsealed; dry_run = $false
            information_container = (New-HorizunIsoContainer -Number '0005' -Type 'M3') }
        if (-not $stamp.isError -and $stamp.data.written -eq $true -and $stamp.data.verified -eq $true) {
            $source = $unsealed; $sourceRoot = $cdeRoot; $sourceStates = $cdeStates
            $sourceOrigin = 'a file sealed host-side by operation=stamp in this run'
        }
    }
    if (-not $source) {
        $why = 'no sealed container exists: the export tier did not seal one and the permission profile (' + $PermissionProfile + ') does not allow a host-side stamp'
        Complete-HorizunIsoCase $Recorder 'ISO-H6' 'not_covered' $why $null
        Complete-HorizunIsoCase $Recorder 'ISO-H7' 'not_covered' $why $null
    } else {
        $published = Join-Path $sourceRoot 'published'
        $log = Join-Path $sourceRoot '.horizun\cde-transitions.jsonl'
        $transition = @{ operation = 'transition'; root = $sourceRoot; states = $sourceStates; file_path = $source
                         from_state = 'shared'; to_state = 'published'; status = 'A1'; approved_by = 'horizun-live-probe'
                         note = ('verify-live ' + $RunId) }
        $pubBefore = Get-HorizunIsoFolderSnapshot $published
        $logExisted = Test-Path -LiteralPath $log
        $rehearsal = & $Call 'horizun_information_container' (Copy-HorizunIsoArgs $transition @{ dry_run = $true })
        $h6Problems = @()
        if ($rehearsal.isError -or -not $rehearsal.data) { $h6Problems += ('the rehearsal was refused: ' + (Limit-HorizunIsoText $rehearsal.text 300)) }
        else {
            if ($rehearsal.data.dry_run -ne $true -or $rehearsal.data.written -ne $false) { $h6Problems += 'the rehearsal did not say dry_run=true, written=false' }
            if ([string]$rehearsal.data.approved_by -ne 'horizun-live-probe') { $h6Problems += 'approved_by was not carried into the plan' }
        }
        if ((Get-HorizunIsoFolderSnapshot $published) -ne $pubBefore) { $h6Problems += 'the rehearsal wrote into published/' }
        if (-not $logExisted -and (Test-Path -LiteralPath $log)) { $h6Problems += 'the rehearsal wrote the transition log' }
        $h6Outcome = 'pass'
        if ($rehearsal.isError -or -not $rehearsal.data) { $h6Outcome = 'fail' } elseif ($h6Problems.Count -gt 0) { $h6Outcome = 'fail' }
        Complete-HorizunIsoCase $Recorder 'ISO-H6' $h6Outcome $(if ($h6Problems.Count -eq 0) { ('every check passed on ' + $sourceOrigin + ' and nothing was copied, stamped or logged') } else { $h6Problems -join '; ' }) $null

        $apply = & $Call 'horizun_information_container' (Copy-HorizunIsoArgs $transition @{ dry_run = $false })
        if ($apply.isError) {
            $writtenAnyway = ((Get-HorizunIsoFolderSnapshot $published) -ne $pubBefore) -or (-not $logExisted -and (Test-Path -LiteralPath $log))
            if ($apply.text -match 'profile' -and -not $writtenAnyway -and $allows -ne $true) {
                Complete-HorizunIsoCase $Recorder 'ISO-H7' 'pass' ('the apply was refused by the permission profile (' + $PermissionProfile + ') with nothing copied or logged') $null
            } else {
                Complete-HorizunIsoCase $Recorder 'ISO-H7' 'fail' ('profile=' + $PermissionProfile + ' written=' + $writtenAnyway + ': ' + (Limit-HorizunIsoText $apply.text 300)) $null
            }
        } else {
            $dest = [string]$apply.data.destination
            $problems = @()
            if ($allows -eq $false) { $problems += ('the profile ' + $PermissionProfile + ' should have refused this write') }
            if ($apply.data.written -ne $true -or $apply.data.verified -ne $true) { $problems += 'written/verified are not both true' }
            if (-not $dest -or -not (Test-Path -LiteralPath $dest -PathType Leaf)) { $problems += 'no copy at the destination' }
            elseif ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { $problems += 'the copy is not byte-identical to the source' }
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { $problems += 'the source was moved or deleted' }
            $logged = $false
            if (Test-Path -LiteralPath $log) { $logged = ((Get-Content -LiteralPath $log -Raw) -match [regex]::Escape([string]$apply.data.transition_id)) }
            if (-not $logged) { $problems += 'the transition id is not in .horizun/cde-transitions.jsonl' }
            $replay = & $Call 'horizun_information_container' (Copy-HorizunIsoArgs $transition @{ dry_run = $false })
            if ($replay.isError -or $replay.data.already_transitioned -ne $true -or $replay.data.written -ne $false) { $problems += 'the same transition sent again did not answer already_transitioned' }
            Complete-HorizunIsoCase $Recorder 'ISO-H7' $(if ($problems.Count -eq 0) { 'pass' } else { 'fail' }) `
                $(if ($problems.Count -eq 0) { ('a byte-identical copy landed in published/, sealed, verified and logged; the replay answered already_transitioned (' + $sourceOrigin + ')') } else { $problems -join '; ' }) `
                @{ transition_id = $apply.data.transition_id }
        }
    }
}

# -----------------------------------------------------------------------------
# The section, end to end. One call from verify-live; the tests drive the same.
# -----------------------------------------------------------------------------
function Invoke-HorizunIsoSection {
    param([int]$Year, [string]$Document, [Parameter(Mandatory = $true)][string]$ScratchRoot, [string]$RepoRoot,
          [Parameter(Mandatory = $true)][string]$RunId, [string]$WriteGate, [string]$PermissionProfile,
          [Parameter(Mandatory = $true)][scriptblock]$Call, [Parameter(Mandatory = $true)][scriptblock]$Apply)
    $recorder = New-HorizunIsoRecorder
    $runDir = New-HorizunIsoRunDirectory -Root $ScratchRoot -RunId $RunId -RepoRoot $RepoRoot
    $sealed = $null; $sealedRoot = $null
    if ($WriteGate) {
        Complete-HorizunIsoTier $recorder 'write' 'not_covered' $WriteGate
    } else {
        try {
            $export = Invoke-HorizunIsoExportProbes -Document $Document -RunDirectory $runDir -RunId $RunId `
                -Call $Call -Apply $Apply -Recorder $recorder
            if ($export -and $export.sealed_source) { $sealed = $export.sealed_source; $sealedRoot = $export.cde_root }
        } catch {
            foreach ($id in @('ISO-E1', 'ISO-E2', 'ISO-E3')) {
                if (-not $recorder.Results.ContainsKey($id)) {
                    Complete-HorizunIsoCase $recorder $id 'unverified' ('HARNESS: the export probes threw: ' + $_.Exception.Message) $null
                }
            }
        }
    }
    try {
        Invoke-HorizunIsoHostProbes -RunDirectory $runDir -RunId $RunId -Call $Call -Recorder $recorder `
            -PermissionProfile $PermissionProfile -SealedSource $sealed -SealedCdeRoot $sealedRoot
    } catch {
        Complete-HorizunIsoTier $recorder 'host' 'unverified' ('HARNESS: the host-resident probes threw: ' + $_.Exception.Message)
    }
    $cases = Close-HorizunIsoRecorder $recorder
    return [pscustomobject]@{ run_directory = $runDir; cases = $cases }
}

# -----------------------------------------------------------------------------
# The consolidator's record (horizun.live-evidence/2). One case is probe x
# harness x Revit year x document; the statuses are the consolidator's own.
# -----------------------------------------------------------------------------
function New-HorizunIsoIdentity {
    param($Health, $BuildIdentity, [string]$ServerSha256, [string]$HarnessFile = 'scripts/verify-live.ps1',
          [string]$HarnessSha256, [string]$HarnessGitBlob, $HarnessTrackedClean, [string]$RepoHead, $RepoClean,
          [int]$Year, [string]$RunId, [string]$Document)
    $addinSha = $null; $revitBuild = $null; $commit = $null; $version = $null; $revitYear = $null; $language = $null
    if ($Health) {
        if ($Health.addin_assembly) { $addinSha = [string]$Health.addin_assembly.sha256 }
        $revitBuild = [string]$Health.revit_build
        $commit = [string]$Health.horizun_commit
        $version = [string]$Health.horizun_version
        $revitYear = [string]$Health.revit_version
        $language = [string]$Health.revit_language
    }
    if (-not $revitYear) { $revitYear = [string]$Year }
    $contract = $null
    if ($BuildIdentity) { $contract = [string]$BuildIdentity.contract_hash }
    return [ordered]@{
        run_id = ('verify-live-iso19650-' + $RunId); harness_file = $HarnessFile; harness_sha256 = $HarnessSha256
        harness_git_blob = $HarnessGitBlob; harness_tracked_clean = $HarnessTrackedClean
        code_candidate_commit = $commit; repo_head = $RepoHead; repo_tracked_clean = $RepoClean
        revit_year = $revitYear; revit_build = $revitBuild; revit_language = $language; horizun_version = $version
        server_sha256 = $ServerSha256; addin_sha256 = $addinSha; contract_hash = $contract
        document = $Document
    }
}

function ConvertTo-HorizunIsoEvidence {
    param([Parameter(Mandatory = $true)]$Cases, [Parameter(Mandatory = $true)]$Identity)
    $counts = [ordered]@{ passed = 0; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0
                          not_assessable = 0; not_applicable = 0; available = 0; implemented_not_live_verified = 0 }
    $probes = @()
    foreach ($c in @($Cases)) {
        if (-not $counts.Contains([string]$c.status)) { throw "HARNESS: case $($c.id) has status '$($c.status)', which the consolidator does not know." }
        $counts[[string]$c.status] = $counts[[string]$c.status] + 1
        $probes += [ordered]@{
            id = $c.id; name = $c.name; tool = $c.tool; tier = $c.tier; status = $c.status
            observed = $c.detail; because = $(if ($c.status -eq 'passed') { $null } else { $c.detail })
            evidence = $c.evidence; recorded_utc = $c.recorded_utc
        }
    }
    $record = [ordered]@{ schema = 'horizun.live-evidence/2'; generated_utc = (Get-Date).ToUniversalTime().ToString('o') }
    foreach ($key in $Identity.Keys) { $record[$key] = $Identity[$key] }
    foreach ($key in $counts.Keys) { $record[$key] = $counts[$key] }
    $record['section'] = 'iso19650'
    $record['probes'] = $probes
    $record['notes'] = @('The ISO 19650 section of scripts/verify-live.ps1. Every case also appears, by the same name, in that run''s main report.')
    return (Protect-HorizunIsoValue ([pscustomobject]$record))
}

function Write-HorizunIsoEvidence {
    param([Parameter(Mandatory = $true)]$Evidence, [Parameter(Mandatory = $true)][string]$Path)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Force -Path $dir }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, ($Evidence | ConvertTo-Json -Depth 40), $utf8)
    return $Path
}

# After the section: the consolidator's record, then the run folder - removed
# only when every case passed, because otherwise its files are the evidence
# somebody needs. The evidence file itself must live OUTSIDE the run folder.
function Complete-HorizunIsoLiveRun {
    param([Parameter(Mandatory = $true)]$Section, [Parameter(Mandatory = $true)]$Identity,
          [Parameter(Mandatory = $true)][string]$EvidencePath, [Parameter(Mandatory = $true)][string]$ScratchRoot,
          [Parameter(Mandatory = $true)][string]$RunId, [switch]$KeepFiles)
    $runFull = [IO.Path]::GetFullPath($Section.run_directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($EvidencePath).StartsWith($runFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'HARNESS: the ISO evidence file may not live inside the run folder that is removed after a green run.'
    }
    $evidence = ConvertTo-HorizunIsoEvidence -Cases $Section.cases -Identity $Identity
    $written = Write-HorizunIsoEvidence -Evidence $evidence -Path $EvidencePath
    $notPassed = @($Section.cases | Where-Object { $_.outcome -ne 'pass' })
    $removed = $false
    if ($notPassed.Count -eq 0 -and -not $KeepFiles) {
        $removed = Remove-HorizunIsoRunDirectory -Path $Section.run_directory -Root $ScratchRoot -RunId $RunId
    }
    return [pscustomobject]@{
        evidence_file = (Protect-HorizunIsoText $written)
        run_directory = (Protect-HorizunIsoText $Section.run_directory)
        run_directory_removed = $removed
        passed = $evidence.passed; failed = $evidence.failed; unverified = $evidence.unverified; not_covered = $evidence.not_covered
        cases = @($Section.cases)
    }
}
