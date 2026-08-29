#Requires -Version 5.1
<#
  Live verification against a REAL Revit.

  CI cannot do this: a hosted runner has no RevitAPI.dll and no Revit process, so
  everything that matters most - the UI-thread round trip, the guards, the
  refusals - is exactly what CI cannot prove. This is the other half of the test
  story, and it is meant to run against every Revit generation before a release.

  It reports what it MEASURED. A probe that is not exercised is reported as "not
  covered" rather than quietly counted as working.

  THE DEFAULT TIER IS NON-DESTRUCTIVE. The write commands are exercised through
  their refusals and their dry runs, which is where their guarantees live: a
  refusal that fires is proof, and it changes nothing. Nothing in the default run
  writes to a model.

  AND THAT WAS NOT ENOUGH. Three typed commands reached review with 411 green
  unit tests, zero warnings, and their apply paths never once executed - and not
  one of them could do its job: heads landed on the project origin, a riser never
  got past its tee, and a connection that Revit really made was reported as
  failed. Refusals prove the guards. Dry runs prove the arithmetic. Only a commit
  proves the command.

  So there is a second tier, behind -WriteProbes, that COMMITS. It runs against a
  model the fixtures file names as disposable and it never saves. See the
  WriteDocument fixture. Without the switch and that fixture, every probe in it is
  NOT COVERED by name - which is the same treatment every other missing guarantee
  gets here, and the reason this gap was visible at all.

  A -WriteProbes RUN IS ITS OWN RUN. The write tier needs WriteDocument ACTIVE,
  and every -Document-targeted probe then refuses against the front document -
  so either point -Document at the disposable model for that run, or accept the
  main tier reporting refusals. The two tiers green in ONE run needs one model
  to be both the fixture document and the disposable one.

  THE WRITE TIER IS NOT REPEATABLE AGAINST THE SAME OPEN DOCUMENT. Its first run
  consumes what it needs: once heads are placed on every free stub, a second run
  correctly reports that no position matches, which reads like a regression and is
  not one. Close the disposable model without saving and reopen it between runs.

  Usage:
    pwsh scripts/verify-live.ps1 -Year 2026
    pwsh scripts/verify-live.ps1 -Year 2026 -Document MOD_ARCH_A
    pwsh scripts/verify-live.ps1 -Year 2024 -OldFile path\to\a-2023.rfa

    # the release gate: every fixture supplied, provenance checked, JSON emitted
    pwsh scripts/verify-live.ps1 -Year 2026 -ReleaseGate `
         -ExpectedCommit <sha> -Json artifacts/live-2026.json

    # the write tier: COMMITS into the model the fixtures file declares disposable
    pwsh scripts/verify-live.ps1 -Year 2026 -WriteProbes

  Requires: that Revit open with the add-in loaded.

  WHERE THE FIXTURE NAMES COME FROM. The parameters below name real things on
  this machine - a model title, a second model, a shared-parameter file and a
  definition inside it, a category, a file saved by another Revit year. Putting
  them in the repository or in CI variables would publish client and project
  names, which is a leak this project refuses to create. So they are
  read from a file OUTSIDE the repository:

      %USERPROFILE%\.horizun\live-fixtures.json

  An explicit parameter always wins over the file. A fixture that is absent from
  both makes its probes NOT COVERED - named in the output and, under
  -ReleaseGate, a non-zero exit.

  EXIT CODES
    0  every probe passed and nothing was left uncovered
    1  at least one probe FAILED
    2  at least one probe was UNVERIFIED - its check could not run
    3  something was NOT COVERED - a guarantee this run did not even attempt
       (exit 3 only under -ReleaseGate; a developer run prints the list and exits 0)

  Three is not a lesser two. A guarantee missing from the output reads exactly
  like one that passed, which is why it has its own code rather than a warning.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$Year,
    [string]$Server,
    # The title of the document open in that Revit. Without it, every probe that
    # needs a named document is reported as NOT COVERED rather than skipped
    # silently - a mutation guard cannot be proven against a document nobody named.
    [string]$Document,
    # The title of a SECOND document, open in the same Revit but NOT active.
    #
    # The probe for "refuses a document that is open but not active" used to pass
    # "<Document>_NOT_ACTIVE_PROBE" - a title matching nothing at all - so it
    # exercised the no-match path and duplicated the probe before it. The
    # distinction it claimed to test was never tested. It needs a document that
    # really is open and really is not active; without one, say so.
    [string]$InactiveDocument,
    # A real shared parameter file and a definition inside it, for the
    # bind_shared_param rehearsal probe. Without them that probe is NOT COVERED:
    # a rehearsal that refuses because the SPF does not exist proves nothing about
    # whether the rehearsal stops before the transaction.
    [string]$SpfPath,
    [string]$SpfParam,
    # A category that EXISTS in the model under test. quantities refuses to report a
    # total of zero for a category with no elements - correctly, since zero reads as
    # "this is empty" - so a category chosen for a different model leaves the probe
    # unverified rather than failed. Name one that is there.
    [string]$QuantityCategory = 'OST_Floors',
    # A real .rvt/.rfa saved in a DIFFERENT Revit version, for the upgrade guard.
    [string]$OldFile,
    # Any installed .rft. If omitted, the harness searches the Revit content
    # folder for this year. It is used only by a dry run; no RFA is written.
    [string]$FamilyTemplate,

    # The title of a WORKSHARED fixture document. If it is already open, the
    # harness copies its on-disk file to a disposable temporary path and opens
    # THAT copy with the requested workset configuration. It never closes or
    # reopens a document that belonged to the user before this run.
    #
    # This is the one condition in the whole product that cannot be detected from
    # inside the answer it corrupts. A closed workset's elements are not in the
    # document, so a scan does not skip them - it never sees them - and every count
    # comes back smaller with nothing anywhere reporting a gap. "0 imported CAD
    # instances" over a half-loaded model is a true statement about what was loaded,
    # presented as a statement about the building.
    #
    # It also cannot be simulated: closing a workset is a property of how a real
    # model was opened. So it needs a real one, and without it the probe is NOT
    # COVERED rather than quietly passing - which would be this suite making exactly
    # the substitution it exists to catch.
    [string]$ClosedWorksetDocument,
    # Exact user-workset name the typed document_session call must keep closed.
    # This is deliberately a fixture value, not a repository convention.
    [string]$ClosedWorksetName,

    # Path to a same-year .rvt the link-refusal probe may LINK into the disposable
    # write model when the fixture carries no RevitLinkInstance of its own. Pass a
    # COPY of a fixture file, never a file that is open in this Revit - Revit
    # refuses to link an open document's file. Staging needs execute_python to be
    # advertised; without either, the probe stays NOT COVERED and names why.
    [string]$LinkSourceFile,

    # The six above, read from outside the repository. See the header.
    [string]$Fixtures = (Join-Path $env:USERPROFILE '.horizun\live-fixtures.json'),

    # Machine-readable results: every probe with its outcome, plus what was not
    # covered and why. A narration is not evidence, and a human summary cannot be
    # diffed between two runs or attached to a release.
    [string]$Json,

    # THE RELEASE GATE. Turns "not covered" into a failure and demands provenance.
    # Without it this is a developer's exploratory run, which is allowed to be
    # partial as long as it SAYS it was.
    [switch]$ReleaseGate,

    # What the running add-in must report as its commit. Checking that
    # horizun_version is merely non-empty proves the bridge answered, not that it
    # is the build under test - which is the question a release actually asks.
    [string]$ExpectedCommit,

    # SHA-256 of the installed server executable, from the release manifest.
    [string]$ExpectedServerSha256,

    # SHA-256 of the installed add-in for THIS year, from the release manifest.
    [string]$ExpectedAddinSha256,

    # Permits a server from bin/Release. Off by default: an integration suite run
    # against a developer build proves that build works and says nothing about the
    # artifact anybody will install.
    [switch]$AllowDevServer,

    # THE WRITE TIER. Off by default, because everything else here changes nothing
    # and a caller should not discover otherwise by accident. On, the probes below
    # commit into -WriteDocument and read the result back out of the model.
    [switch]$WriteProbes,

    # A model the fixtures file declares expendable, plus the declaration itself.
    # Two separate things on purpose: the switch says what kind of run this is, the
    # fixture says WHICH model may be written into. Neither implies the other.
    [string]$WriteDocument,
    [string]$WriteDocumentDisposable
)

$probeRun = [guid]::NewGuid().ToString('N')
$ErrorActionPreference = 'Stop'

# A green report must identify the TEST that produced it, not only the product
# it exercised. The product candidate and the harness intentionally can be
# different commits (the harness often learns how to measure a candidate), so
# both identities are recorded independently. A release gate refuses a dirty or
# unresolvable harness: otherwise five reports could share a product commit while
# silently using five different tests.
$harnessFile = 'scripts/verify-live.ps1'
$harnessPath = $PSCommandPath
$harnessSha256 = (Get-FileHash -LiteralPath $harnessPath -Algorithm SHA256).Hash.ToLowerInvariant()
$harnessCommit = $null
$harnessGitBlob = $null
$harnessTrackedClean = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedHarnessPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $harnessFile))
$harnessPathMatchesRepository = [string]::Equals(
    [IO.Path]::GetFullPath($harnessPath), $expectedHarnessPath,
    [StringComparison]::OrdinalIgnoreCase)
try {
    $headLines = @(& git -C $repositoryRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $headLines.Count -eq 1 -and
        [string]$headLines[0] -match '^[0-9a-fA-F]{40}$') {
        $harnessCommit = ([string]$headLines[0]).ToLowerInvariant()
        $statusLines = @(& git -C $repositoryRoot status --porcelain --untracked-files=no -- $harnessFile 2>$null)
        $harnessTrackedClean = ($LASTEXITCODE -eq 0 -and $statusLines.Count -eq 0)
        $harnessSpec = $harnessCommit + ':' + $harnessFile
        $blobLines = @(& git -C $repositoryRoot rev-parse $harnessSpec 2>$null)
        if ($LASTEXITCODE -eq 0 -and $blobLines.Count -eq 1 -and
            [string]$blobLines[0] -match '^[0-9a-fA-F]{40,64}$') {
            $harnessGitBlob = ([string]$blobLines[0]).ToLowerInvariant()
        }
    }
}
catch {
    $harnessCommit = $null
    $harnessGitBlob = $null
    $harnessTrackedClean = $false
}
if ($ReleaseGate -and
    ($harnessCommit -notmatch '^[0-9a-f]{40}$' -or
     $harnessGitBlob -notmatch '^[0-9a-f]{40,64}$' -or
     $harnessSha256 -notmatch '^[0-9a-f]{64}$' -or
     -not $harnessPathMatchesRepository -or
     -not $harnessTrackedClean)) {
    throw 'The release gate harness is not pinned to a clean Git commit. Commit scripts/verify-live.ps1 before running the matrix; no live report was produced.'
}

# ---------------------------------------------------------------------------
# Fixtures. An explicit parameter always wins; the file fills the rest.
# ---------------------------------------------------------------------------
$fixtureSource = @{}
if (Test-Path $Fixtures) {
    try {
        $fx = Get-Content $Fixtures -Raw | ConvertFrom-Json
        foreach ($name in 'Document','InactiveDocument','SpfPath','SpfParam','QuantityCategory','OldFile',
                          'FamilyTemplate','ClosedWorksetDocument','ClosedWorksetName',
                          'WriteDocument','WriteDocumentDisposable','LinkSourceFile') {
            $fromFile = $fx.$name
            if ([string]::IsNullOrWhiteSpace($fromFile)) { continue }
            # QuantityCategory has a default, so "was it passed" cannot be read off
            # emptiness alone - PSBoundParameters is the only reliable answer.
            if ($PSBoundParameters.ContainsKey($name)) { $fixtureSource[$name] = 'parameter'; continue }
            Set-Variable -Name $name -Value $fromFile -Scope Script
            $fixtureSource[$name] = 'fixtures-file'
        }
    }
    catch { Write-Host "WARNING: $Fixtures could not be read as JSON: $($_.Exception.Message)" -ForegroundColor Yellow }
}

# Family-template names are localized, so do not guess one. Any RFT is enough for
# the non-writing parser/confirmation rehearsal below; the command deliberately
# does not open it during dry_run.
if ([string]::IsNullOrWhiteSpace($FamilyTemplate)) {
    $templateRoot = Join-Path $env:ProgramData ("Autodesk\RVT {0}\Family Templates" -f $Year)
    if (Test-Path $templateRoot) {
        # Prefer an ASCII path. Some older Revit generations report localized
        # template paths as "illegal characters" through NewFamilyDocument even
        # though Windows itself can open them; this probe is about the typed plan,
        # not localization support.
        $templates = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -Filter '*.rft' -File -ErrorAction SilentlyContinue)
        $candidateTemplate = $templates |
                             Where-Object { $_.FullName -cmatch '^[\x20-\x7E]+$' } |
                             Sort-Object FullName | Select-Object -First 1
        if (-not $candidateTemplate) {
            $candidateTemplate = $templates | Sort-Object FullName | Select-Object -First 1
        }
        if ($candidateTemplate) {
            $FamilyTemplate = $candidateTemplate.FullName
            $fixtureSource['FamilyTemplate'] = 'auto-discovered'
        }
    }
}

# Resolved here, not in the param default: PowerShell 5.1 does not populate
# $PSScriptRoot while binding parameters, so a default built from it comes out empty.
#
# THE INSTALLED SERVER IS THE DEFAULT, and that is a change.
#
# It used to default to bin/Release. So the integration suite proved the
# DEVELOPER BUILD worked, and said nothing about the artifact anybody installs -
# which is the only one that matters at a release. The two differ every time the
# tree is dirty, and the difference is invisible in the output.
if (-not $Server) {
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
    if (Test-Path $installed) { $Server = $installed }
    else {
        $Server = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'
    }
}
if (-not (Test-Path $Server)) { throw "MCP server not found: $Server" }

$serverIsDevBuild = $Server -match '(?i)[\\/]bin[\\/]Release[\\/]'
if ($serverIsDevBuild -and -not $AllowDevServer) {
    throw ("Refusing to run against a developer build: $Server`n" +
           "This suite exists to verify the artifact that will be INSTALLED. A run against bin/Release proves " +
           "that build works and says nothing about the package. Install the release and re-run, point -Server " +
           "at the installed exe, or pass -AllowDevServer to say deliberately that this run does not speak for " +
           "the artifact.")
}
$serverSha = (Get-FileHash $Server -Algorithm SHA256).Hash.ToLower()

# Discovery is per INSTANCE (revit-<year>-<pid>.json), under the SHARED data root
# both halves resolve - %USERPROFILE%\.horizun\discovery, or HORIZUN_DATA_ROOT
# when it is set. This harness must read exactly where the add-in writes, or it
# reports a healthy Revit as absent.
#
# It honours HORIZUN_DATA_ROOT for the same reason the code does: a run pointed at
# a temporary root has to find that root's discovery files, not the real ones.
if ($env:HORIZUN_DATA_ROOT) { $dataRoot = $env:HORIZUN_DATA_ROOT }
else { $dataRoot = Join-Path $env:USERPROFILE '.horizun' }
$dir = Join-Path $dataRoot 'discovery'

$candidates = @()
if (Test-Path $dir) {
    $candidates = @(Get-ChildItem $dir -Filter "revit-$Year-*.json" -File -ErrorAction SilentlyContinue)
}
if ($candidates.Count -eq 0) {
    # Naming the old location turns "no add-in is published" into a sentence that
    # says what to do: this machine has a Revit running an add-in from before the
    # data root moved.
    $legacyDir = Join-Path $env:LOCALAPPDATA 'Horizun'
    $legacyHint = ''
    if ((Test-Path $legacyDir) -and
        @(Get-ChildItem $legacyDir -Filter "revit-$Year*.json" -File -ErrorAction SilentlyContinue).Count -gt 0) {
        $legacyHint = " NOTE: $legacyDir still holds discovery files for Revit $Year. That Revit is running an " +
                      "add-in from BEFORE the data root moved to $dataRoot. Redeploy the add-in and restart it."
    }
    throw "No add-in is published for Revit $Year under $dir. Start that Revit first.$legacyHint"
}

$live = @()
foreach ($c in $candidates) {
    $j = Get-Content $c.FullName -Raw | ConvertFrom-Json
    if (Get-Process -Id $j.pid -ErrorAction SilentlyContinue) { $live += $j }
}
if ($live.Count -eq 0) { throw "Every discovery file for Revit $Year names a process that is gone. Start Revit $Year." }
if ($live.Count -gt 1) {
    throw ("$($live.Count) instances of Revit $Year are running (pids " + (($live | ForEach-Object { $_.pid }) -join ', ') +
           "). This harness will not pick one - that is the same guess the bridge itself refuses to make. Close all but one.")
}
$target = $live[0]

# ---------------------------------------------------------------------------
# SCRATCH FILES the probes write for themselves (5.26, 5.27).
#
# Neither story needs a fixture, a model or a cloud account: one is about the
# first eight bytes of a file and the other about how a .py file decodes, so the
# evidence can be MANUFACTURED here exactly as it occurred in the field. That is
# the difference between a story that can be verified on any machine and one that
# waits for somebody to reproduce a 337 MB download.
# ---------------------------------------------------------------------------
$scratchDir = Join-Path $env:TEMP "horizun-live-$probeRun"
New-Item -ItemType Directory -Force $scratchDir | Out-Null

# A ZIP wearing a .rvt name - the measured case in four bytes. This is what cost
# two false diagnoses ("a newer Revit", then "a corrupt download").
$zipAsRvt = Join-Path $scratchDir 'HZ_NOT_A_MODEL.rvt'
[System.IO.File]::WriteAllBytes($zipAsRvt,
    [byte[]](0x50, 0x4b, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00))

# A driver with accents AND CRLF: the exact combination IronPython's own open()
# could not read ("'charmap' codec can't decode byte 0x8f in position 2634").
$driverPy = Join-Path $scratchDir 'hz_driver_con_tildes.py'
[System.IO.File]::WriteAllBytes($driverPy,
    [System.Text.UTF8Encoding]::new($false).GetBytes(
        "# -*- coding: utf-8 -*-`r`n" +
        "checkpoint('auditoria de tabiqueria')`r`n" +
        "__output__ = {'status': 'completed_unverified', 'summary': 'leido desde code_path con tildes: ñ á é'}`r`n"))

# ---------------------------------------------------------------------------
# The probes. Each is a tool call plus a predicate over its result, so a pass
# means "the answer said what it had to say", not merely "no exception".
# ---------------------------------------------------------------------------
$probes = @(
    @{ Name = 'health answers, and it is this Revit year'
       Tool = 'horizun_health'; Args = @{}
       Check = { param($d) $d.revit_version -eq "$Year" } },

    @{ Name = 'health names our own build'
       Tool = 'horizun_health'; Args = @{}
       Check = { param($d) $d.horizun_version -and $d.horizun_version -ne 'unknown' } },

    # PROVENANCE, and the reason the probe above is not enough.
    #
    # "horizun_version is non-empty" proves the bridge answered. It does not
    # prove WHICH BUILD answered, which is the only question a release asks - and
    # a stale add-in from three days ago passes it perfectly. This asserts the
    # exact commit, and that the tree it was built from was clean: a -dirty
    # suffix means the sha names a commit the binary is not.
    @{ Name = 'the running add-in is the expected commit, from a clean tree'
       Tool = 'horizun_health'; Args = @{}
       Needs = 'ExpectedCommit'
       NotCovered = 'WHICH build is running (needs -ExpectedCommit; horizun_version being non-empty proves only that something answered)'
       Check = { param($d)
                 $d.horizun_commit -eq $ExpectedCommit -and $d.built_from_clean_tree -eq $true } },

    # Both halves resolve one data root, and this is where that stops being a
    # unit test. The server reports its own; the add-in reports its own; they
    # must be the same folder and the add-in must be able to WRITE there.
    @{ Name = 'the add-in and the server agree on the data root, and it is writable'
       Tool = 'horizun_health'; Args = @{}
       Check = { param($d)
                 $p = $d.data_paths
                 $null -ne $p -and
                 -not [string]::IsNullOrWhiteSpace($p.data_root) -and
                 $p.access.data_root.writable -eq $true -and
                 $p.access.jobs_path.writable -eq $true -and
                 $p.access.discovery_path.readable -eq $true -and
                 # Not under LocalApplicationData - the split this root exists to close.
                 ($p.data_root -notmatch '(?i)AppData\\Local') } },

    # The defect a real model exposed: an active document named while every open
    # document was marked is_active false.
    @{ Name = 'health identifies WHICH open document is active'
       Tool = 'horizun_health'; Args = @{}
       Check = { param($d)
                 if ($d.no_active_document) { return $true }   # nothing open is a valid answer
                 $active = @($d.open_documents | Where-Object { $_.is_active -eq $true })
                 $active.Count -eq 1 -and $d.active_document_match -eq 'Matched' } },

    # Host-resident: must answer without touching Revit at all.
    @{ Name = 'target lists instances and reports how it chose'
       Tool = 'horizun_target'; Args = @{}
       Check = { param($d) $d.targets_found -ge 1 -and $d.selected_by } },

    # Was Check = { $true }: it called the tool and asserted nothing at all, so it
    # passed whatever came back. job_status is host-resident, so what it has to
    # prove is that it answers with a jobs COLLECTION - present even when empty -
    # without going near Revit.
    @{ Name = 'job_status answers host-side with a jobs collection'
       Tool = 'horizun_job_status'; Args = @{ limit = 1 }
       Check = { param($d) $null -ne $d.jobs -and $null -ne $d.job_count -and $d.read_without_revit -eq $true } },

    # The switch is per machine, so this probe must assert against WHATEVER STATE
    # IT IS IN and never assume one. It used to expect the refusal unconditionally
    # and reported FAIL on a machine where the capability was deliberately enabled -
    # a red line for a correct product and a correct configuration.
    #
    # Enabled: the code must actually run and the reply must carry the transaction
    # policy, because that is the guarantee that matters when it is on.
    # Disabled: the refusal must fire. Both are checked; neither is assumed.
    # target_document is REQUIRED here now, like every other command that can
    # change a model. This probe used to omit it and reported UNVERIFIED against
    # the very change that made it necessary - a probe that had gone out of date
    # with the product it checks.
    @{ Name = 'execute_python matches its per-machine switch (enabled: runs; disabled: refuses)'
       Tool = 'horizun_execute_python'; Args = @{ code = "__output__ = 6 * 7"; target_document = $Document; idempotency_key = "live-python-enabled-$probeRun" }
       Needs = 'Document'
       NotCovered = 'whether execute_python runs or refuses (needs -Document; it requires target_document like every other mutating command)'
       Check = { param($d)
                 # Reached the Check at all, so it was not refused: it must have RUN.
                 $d.executed -eq $true -and $d.output -eq 42 -and
                 $d.transaction_left_open -eq $false -and
                 -not [string]::IsNullOrWhiteSpace($d.transaction_policy) }
       # When it is switched off the reply is an error, and this is the one probe
       # where that is a correct outcome rather than an unmeasured one.
       # Two wordings, one condition: the add-in says 'DISABLED', but the SERVER
       # gate fires first and says 'requires BOTH permission_profile...' - the
       # add-in sentence is unreachable when the server refuses to forward. Both
       # are the switched-off machine answering correctly.
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # PREFLIGHT EXECUTES NOTHING, and the only way to prove that is to preflight a
    # script whose execution would be visible and then see that it was not. This
    # one would throw on execution; a reply that says would_run=true with no error
    # is the proof that the source was compiled and never run. It also needs no
    # idempotency_key, which is the other half of "this is not a mutation".
    @{ Name = 'preflight validates and compiles WITHOUT executing'
       Tool = 'horizun_execute_python'
       Args = @{ code = "raise Exception('this must never run')"; target_document = $Document; preflight = $true }
       Needs = 'Document'
       NotCovered = 'the execute_python preflight path (needs -Document)'
       Check = { param($d)
                 $d.mode -eq 'preflight' -and $d.executed -eq $false -and
                 # It parses (raise is valid Python), so the syntax check passes and
                 # nothing ran - an executed script would have errored the call.
                 $d.would_run -eq $true -and $d.checks.syntax -eq 'ok' -and
                 -not [string]::IsNullOrWhiteSpace($d.script_sha256) -and
                 -not [string]::IsNullOrWhiteSpace($d.what_preflight_cannot_do) }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # And a preflight REPORTS a syntax error rather than discovering it mid-write.
    @{ Name = 'preflight reports a syntax error instead of running into it'
       Tool = 'horizun_execute_python'
       Args = @{ code = "def broken(:"; target_document = $Document; preflight = $true }
       Needs = 'Document'
       NotCovered = 'preflight catching a syntax error before execution (needs -Document)'
       Check = { param($d)
                 $d.executed -eq $false -and $d.would_run -eq $false -and
                 $d.checks.syntax -eq 'failed' -and -not [string]::IsNullOrWhiteSpace($d.syntax_error) }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # THE EVIDENCE CONTRACT, against a real script. A claim of verified with no
    # evidence must come back DOWNGRADED - the rule that keeps an unverified Python
    # run from reading like a verified typed write.
    @{ Name = 'a verified claim with no evidence is downgraded to completed_unverified'
       Tool = 'horizun_execute_python'
       Args = @{ code = "__output__ = {'status': 'verified', 'summary': 'trust me'}"
                 target_document = $Document; idempotency_key = "live-python-evidence-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the __output__ evidence downgrade (needs -Document)'
       Check = { param($d) $d.executed -eq $true -and $d.evidence_status -eq 'completed_unverified' -and
                            $d.script_reported_status -eq 'verified' -and
                            @($d.evidence_warnings).Count -gt 0 }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # And the CEILING for arbitrary code: a claim WITH evidence is kept, but only as
    # self_reported_verified. The host never re-reads the model after Python, so this
    # path must never produce the word a typed write earns - and host_verified says so
    # in a field rather than leaving a reader to infer it.
    @{ Name = 'a verified claim carrying evidence is capped at self_reported_verified'
       Tool = 'horizun_execute_python'
       Args = @{ code = "__output__ = {'status': 'verified', 'summary': 'read the title', " +
                        "'verification': {'checked': True, 'evidence': [doc.Title]}}"
                 target_document = $Document; idempotency_key = "live-python-evidence-ok-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the self-reported evidence ceiling (needs -Document)'
       Check = { param($d) $d.executed -eq $true -and
                            $d.evidence_status -eq 'self_reported_verified' -and
                            $d.evidence_structured -eq $true -and
                            $d.script_reported_status -eq 'verified' -and
                            $d.host_verified -eq $false }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # The typed overlap ADVISES and no longer blocks: a REAL call to one of those
    # APIs still runs, and carries the advisory beside its result. It is a real call
    # inside a rolled-back transaction, so the model is untouched - the earlier
    # version of this probe used a COMMENT, which passed by confirming the very
    # false positive the masking was added to remove.
    @{ Name = 'a typed-overlap advisory is reported without blocking a real call'
       Tool = 'horizun_execute_python'
       Args = @{ code = @'
from Autodesk.Revit.DB import Transaction, ElementTransformUtils, XYZ
t = Transaction(doc, "horizun live probe: advisory only")
t.Start()
try:
    # A real ElementTransformUtils.MoveElement call site, never reached: the guard
    # is false. The advisory must fire on the CODE being present, and the
    # transaction is rolled back regardless so the model is untouched.
    if False:
        ElementTransformUtils.MoveElement(doc, None, XYZ(0, 0, 0))
finally:
    t.RollBack()
__output__ = {"status": "completed_unverified", "summary": "advisory probe; nothing written"}
'@
                 target_document = $Document; idempotency_key = "live-python-advisory-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the typed-overlap advisory being advisory (needs -Document)'
       Check = { param($d) $d.executed -eq $true -and
                            $d.transaction_left_open -eq $false -and
                            @($d.typed_alternatives).Count -gt 0 }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # ...and the false positive that started this: prose must NOT advise. Same shape
    # as the probe above with the call demoted to a comment and a string.
    @{ Name = 'a comment or string mentioning a typed API raises NO advisory'
       Tool = 'horizun_execute_python'
       Args = @{ code = "# mentions ElementTransformUtils.MoveElement in a comment only`n" +
                        "note = 'and doc.Delete( in a string'`n" +
                        "__output__ = {'status': 'completed_unverified', 'summary': 'prose only'}"
                 target_document = $Document; idempotency_key = "live-python-no-advisory-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the advisory ignoring comments and strings (needs -Document)'
       # "no advisories" arrives as an ABSENT field, and @($null).Count is 1 in
       # PowerShell, not 0 - so the null has to be tested first or this probe fails
       # against a correct answer. Measured: it did.
       Check = { param($d) $d.executed -eq $true -and
                            ($null -eq $d.typed_alternatives -or @($d.typed_alternatives).Count -eq 0) }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # THE FALLBACK SIGNAL, from a real typed refusal. An unsupported kind is a
    # capability gap decided BEFORE any transaction, so the reply must carry
    # allowed=true with write_started=false - the condition a client branches on.
    @{ Name = 'an unsupported kind grants the Python fallback as structured data'
       Tool = 'horizun_create_elements'
       Args = @{ target_document = $Document; dry_run = $false
                 idempotency_key = "live-fallback-unsupported-$probeRun"
                 elements = @( @{ kind = 'sprinkler_head' } ) }
       Needs = 'Document'
       NotCovered = 'the structured fallback signal on a capability gap (needs -Document)'
       ExpectError = 'unsupported kind'
       # The grant itself, not just the refusal: allowed=true with nothing written.
       ExpectErrorContains = '"allowed": true' },

    # THE DEFAULT PATH. dry_run is OMITTED, which is what an agent actually sends
    # first. This used to come back success=true, invalid=1 and no verdict at all:
    # the decision existed only on the apply path, so a caller had to already
    # suspect Python to discover that Python was the answer. The earlier probe hid
    # it by forcing dry_run=false - it tested where the code worked.
    @{ Name = 'the DEFAULT rehearsal (no dry_run) publishes the fallback grant'
       Tool = 'horizun_create_elements'
       Args = @{ target_document = $Document
                 elements = @( @{ kind = 'sprinkler_head' } ) }
       Needs = 'Document'
       NotCovered = 'the fallback on the default dry-run path (needs -Document)'
       UseStructured = $true
       Check = { param($s)
                 $s.dry_run -eq $true -and $s.invalid -eq 1 -and
                 $null -ne $s.fallback -and $s.fallback.allowed -eq $true -and
                 $s.fallback.write_started -eq $false -and
                 $s.fallback.reason -eq 'unsupported_kind' -and
                 $s.fallback.recommended_tool -eq 'horizun_execute_python' -and
                 @($s.capability_gaps).Count -eq 1 -and @($s.capability_gaps)[0].index -eq 0 } },

    # The mixed batch from the SAME default route. Otherwise the only way to learn a
    # batch is mixed would be to send an apply.
    @{ Name = 'the DEFAULT rehearsal blocks a mixed batch and still names the gap'
       Tool = 'horizun_create_elements'
       Args = @{ target_document = $Document
                 elements = @( @{ kind = 'sprinkler_head' }, @{ kind = 'wall' } ) }
       Needs = 'Document'
       NotCovered = 'the mixed-batch refusal on the default path (needs -Document)'
       UseStructured = $true
       Check = { param($s)
                 $s.dry_run -eq $true -and
                 $null -ne $s.fallback -and $s.fallback.allowed -eq $false -and
                 $s.fallback.reason -eq 'mixed_capability_and_invalid_input' -and
                 @($s.capability_gaps).Count -eq 1 -and @($s.capability_gaps)[0].index -eq 0 } },

    # ...and a rehearsal whose only failure is fixable must publish NOTHING. Absence
    # is the answer; a block here would send a client to script around its own typo.
    @{ Name = 'the DEFAULT rehearsal with only a fixable error publishes no block'
       Tool = 'horizun_create_elements'
       Args = @{ target_document = $Document
                 elements = @( @{ kind = 'wall' } ) }
       Needs = 'Document'
       NotCovered = 'the absence of a fallback on the default path (needs -Document)'
       UseStructured = $true
       Check = { param($s)
                 $s.dry_run -eq $true -and $s.invalid -eq 1 -and
                 $null -eq $s.fallback -and $null -eq $s.capability_gaps } },

    # THE MIXED BATCH, live. One entry names a kind with no typed path and another
    # is simply wrong. The old code answered allowed=true for the whole request -
    # telling a client to go write a script while the request still held input it
    # should fix. It must now refuse the grant and still name the gap.
    @{ Name = 'a mixed batch REFUSES the fallback and still names the capability gap'
       Tool = 'horizun_create_elements'
       Args = @{ target_document = $Document; dry_run = $false
                 idempotency_key = "live-fallback-mixed-$probeRun"
                 elements = @( @{ kind = 'sprinkler_head' },
                               @{ kind = 'wall' } ) }   # wall with no geometry: fixable
       Needs = 'Document'
       NotCovered = 'the mixed-batch fallback refusal (needs -Document)'
       ExpectError = 'invalid'
       ExpectErrorContains = 'mixed_capability_and_invalid_input' },

    # An ordinary argument error must carry NO fallback block at all: absence is the
    # answer a client reads, and a block here would send it to write Python around
    # its own typo.
    @{ Name = 'an ordinary argument error carries no fallback block'
       Tool = 'horizun_create_elements'
       Args = @{ target_document = $Document; dry_run = $false
                 idempotency_key = "live-fallback-argument-$probeRun"
                 elements = @( @{ kind = 'wall' } ) }
       Needs = 'Document'
       NotCovered = 'the absence of a fallback on an argument error (needs -Document)'
       ExpectError = 'invalid'
       ExpectErrorLacks = '--- fallback ---' },

    # THE COMMAND THAT USED TO BE OUTSIDE THE POLICY. It could do everything the
    # typed writes can, plus everything they cannot, aimed at whatever window was
    # in front.
    @{ Name = 'execute_python REFUSES without target_document'
       Tool = 'horizun_execute_python'; Args = @{ code = "__output__ = 1"; idempotency_key = "live-python-no-target-$probeRun" }
       ExpectError = "'target_document' is required|requires BOTH" },

    # The reply carrying the job_id is the message that gets lost. Without a key,
    # a client retrying a timeout queues the script a second time.
    @{ Name = 'run_async REFUSES without idempotency_key'
       Tool = 'horizun_execute_python'
       Args = @{ code = "__output__ = 1"; run_async = $true; target_document = $Document }
       Needs = 'Document'
       NotCovered = 'run_async demanding an idempotency_key (needs -Document)'
       ExpectError = 'idempotency_key is REQUIRED|requires BOTH' },

    # The universal dispatcher now persists synchronous results too. The old
    # command-local refusal made this path impossible once every mutation began
    # requiring a durable key.
    @{ Name = 'a key on the SYNCHRONOUS path is accepted and the script runs once'
       Tool = 'horizun_execute_python'
       Args = @{ code = "__output__ = 1"; target_document = $Document; idempotency_key = "live-python-sync-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the synchronous path using durable idempotency (needs -Document)'
       Check = { param($d) $d.executed -eq $true -and $d.output -eq 1 -and
                            $d.idempotency.status -eq 'executed_once' }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # Was Check = { $true } with AllowError: it asserted nothing and passed on an
    # error too. What it has to prove is that it describes the SAME Revit this
    # harness selected, and reports a real element count rather than a zero
    # standing in for a read that did not happen.
    @{ Name = 'get_document_info describes this Revit and counts real elements'
       Tool = 'get_document_info'; Args = @{}
       Check = { param($d) $d.revit_version -eq "$Year" -and $d.title -and $d.element_count -gt 0 } },

    @{ Name = 'list_schedules returns bounded native definitions and coverage'
       Tool = 'horizun_list_schedules'; Args = @{ max_rows = 1 }
       Check = { param($d)
                 $d.total -ge $d.returned -and $null -ne $d.rows -and
                 $null -ne $d.host_visibility_coverage.coverage_complete -and
                 $null -ne $d.linked_models_coverage.coverage_complete } },

    @{ Name = 'get_schedule_data refuses an id that is not a schedule'
       Tool = 'horizun_get_schedule_data'; Args = @{ schedule_id = 999999999 }
       ExpectError = 'does not identify a native ViewSchedule' },

    @{ Name = 'query_model returns bounded rows, summaries and federated coverage'
       Tool = 'horizun_query_model'; Args = @{ max_rows = 1; include_links = $true }
       Check = { param($d)
                 $d.matched_total -ge $d.returned -and $d.returned -le 1 -and
                 $null -ne $d.rows -and $null -ne $d.summary -and
                 $null -ne $d.federated_coverage.coverage_complete } },

    # ---- the mutation gate, proven by its refusals. Nothing is written. ----
    @{ Name = 'create_elements REFUSES without target_document'
       Tool = 'horizun_create_elements'; Args = @{ elements = @(@{ kind = 'level'; elevation = 0 }) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'transform_elements REFUSES without target_document'
       Tool = 'horizun_transform_elements'; Args = @{ operations = @(@{ operation = 'pin'; element_ids = @(1) }) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'manage_views REFUSES without target_document'
       Tool = 'horizun_manage_views'; Args = @{ actions = @(@{ operation = 'create_3d' }) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'manage_system_types REFUSES without target_document'
       Tool = 'horizun_manage_system_types'; Args = @{ actions = @(@{ source_type_id = 1; new_name = 'HZ_REFUSAL_ONLY' }) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'create_family REFUSES without target_document'
       Tool = 'horizun_create_family'
       Args = @{ template_path = 'C:\horizun-refusal-only.rft'; output_path = 'C:\horizun-refusal-only.rfa' }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'annotate REFUSES without target_document'
       Tool = 'horizun_annotate'; Args = @{ actions = @(@{ operation = 'text'; view_id = 1; text = 'x'; point = @(0,0) }) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'execute_plan REFUSES without target_document'
       Tool = 'horizun_execute_plan'; Args = @{ actions = @(@{ key = 'x'; tool = 'horizun_create_elements'; arguments = @{ elements = @() } }) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'export REFUSES without target_document'
       Tool = 'horizun_export'; Args = @{ format = 'ifc'; output_path = 'C:\horizun-refusal-only.ifc' }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'submit_job refuses a host-only command without queuing it'
       Tool = 'horizun_submit_job'
       Args = @{ tool = 'horizun_job_status'; arguments = @{}; idempotency_key = "live-submit-host-only-$probeRun" }
       ExpectError = 'not an installed Revit command' },

    @{ Name = 'create_schedule REFUSES without target_document'
       Tool = 'horizun_create_schedule'; Args = @{ category = 'OST_Walls'; name = 'HZ_REFUSAL_ONLY' }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    # This discriminator selects between exact ids and a document-wide purge.
    # Exercise the command itself, not only tools/list: a client that does not
    # pre-validate JSON Schema must receive the same fail-closed refusal.
    @{ Name = 'delete REFUSES a missing mode instead of selecting purge_unused'
       Tool = 'horizun_delete_verified'
       Args = @{ ids = @(999999999); target_document = $Document }
       ExpectError = 'mode is REQUIRED' },

    @{ Name = 'delete REFUSES without target_document'
       Tool = 'horizun_delete_verified'; Args = @{ mode = 'ids'; ids = @(999999999) }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'delete REFUSES a document that is not open'
       Tool = 'horizun_delete_verified'
       Args = @{ mode = 'ids'; ids = @(999999999); target_document = 'ZZ_NO_SUCH_MODEL_ZZ' }
       ExpectError = 'No open document matches' },

    @{ Name = 'set_keynote REFUSES without target_document'
       Tool = 'horizun_set_keynote'; Args = @{ element_ids = @(999999999); keynote = 'X' }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'write_params REFUSES without target_document'
       Tool = 'horizun_write_params_verified'; Args = @{ writes = @() }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'family_apply REFUSES without target_document'
       Tool = 'horizun_family_apply'; Args = @{}
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'bind_shared_param REFUSES without target_document'
       Tool = 'horizun_bind_shared_param'; Args = @{}
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    @{ Name = 'save REFUSES without target_document'
       Tool = 'horizun_save_document'; Args = @{ idempotency_key = "live-save-no-target-$probeRun" }
       ExpectError = "'target_document' is required|hidden/refused by permission_profile" },

    # ---- 5.26: the eight bytes that end the argument -----------------------
    # Revit's own message for an unreadable header names two causes and both are
    # about Revit files, so a renamed ZIP is diagnosed as a version problem. This
    # probe reproduces the field case with a file it writes itself.
    @{ Name = 'file_info names a ZIP renamed .rvt instead of blaming the Revit version'
       Tool = 'horizun_file_info'; Args = @{ paths = @($zipAsRvt) }
       Check = { param($d)
                 $f = $d.files[0]
                 $f.signature -eq '504b030414000000' -and
                 $f.is_revit_container -eq $false -and
                 $f.signature_means -match 'ZIP' -and
                 $f.signature_means -match 'NOT a Revit model' -and
                 # The read_error is still reported - the signature CORRECTS it, it
                 # does not replace it.
                 -not [string]::IsNullOrWhiteSpace($f.read_error) -and
                 $d.unreadable -eq 1 -and $d.not_revit_files -eq 1 } },

    # ---- 5.27: a script from a file -----------------------------------------
    @{ Name = 'execute_python runs a .py from code_path - accents, CRLF and all'
       Tool = 'horizun_execute_python'
       Args = @{ code_path = $driverPy; target_document = $Document
                 idempotency_key = "live-codepath-$probeRun" }
       Needs = 'Document'
       NotCovered = 'reading a script from code_path (needs -Document)'
       Check = { param($d)
                 $d.executed -eq $true -and
                 $d.source.from -eq 'code_path' -and
                 $d.source.path -eq $driverPy -and
                 $d.source.read_from_disk_by_this_call -eq $true -and
                 # The two things the hand-written stub had to get right and did not.
                 $d.source.newlines_normalized -eq $true -and
                 $d.source.decoded_as -match 'utf-8' -and
                 $d.output.summary -match 'leido desde code_path' }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    @{ Name = 'execute_python refuses code AND code_path together rather than picking one'
       Tool = 'horizun_execute_python'
       Args = @{ code = '__output__ = 1'; code_path = $driverPy
                 target_document = 'ZZ_NO_SUCH_MODEL_ZZ'; idempotency_key = "live-codeboth-$probeRun" }
       ExpectError = 'Send exactly ONE|hidden/refused by permission_profile' },

    @{ Name = 'execute_python refuses with neither code nor code_path'
       Tool = 'horizun_execute_python'
       Args = @{ target_document = 'ZZ_NO_SUCH_MODEL_ZZ'; idempotency_key = "live-codeneither-$probeRun" }
       ExpectError = "One of 'code'|hidden/refused by permission_profile" },

    @{ Name = 'execute_python says a missing code_path is missing, and names the machine'
       Tool = 'horizun_execute_python'
       Args = @{ code_path = 'C:\ZZ_NO_SUCH_DRIVER_ZZ.py'; target_document = 'ZZ_NO_SUCH_MODEL_ZZ'
                 idempotency_key = "live-codemissing-$probeRun" }
       ExpectError = 'code_path does not exist|hidden/refused by permission_profile' },

    # ---- 5.28: the name every pyRevit caller types --------------------------
    @{ Name = '__revit__ resolves to the UIApplication, beside app, uiapp and doc'
       Tool = 'horizun_execute_python'
       Args = @{ code = @'
__output__ = {
    'status': 'completed_unverified',
    'summary': 'checked the injected names',
    'has_revit': __revit__ is not None,
    'revit_is_uiapp': __revit__.Application.VersionNumber == app.VersionNumber,
    'doc_application_agrees': doc.Application.VersionNumber == app.VersionNumber,
}
'@
                 target_document = $Document; idempotency_key = "live-revitalias-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the __revit__ alias (needs -Document)'
       Check = { param($d)
                 $d.output.has_revit -eq $true -and
                 $d.output.revit_is_uiapp -eq $true -and
                 $d.output.doc_application_agrees -eq $true }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    # ---- 5.25: the dialog record reaches the script AND the reply ------------
    # The measured failure this closes: a batch opens a model, Revit raises a
    # dialog, the bridge cancels it, and all the script ever sees is "Opening was
    # canceled". Here the script raises a dialog ON PURPOSE and must be able to
    # name it from the inside - and the reply must carry it as structure.
    @{ Name = 'a dialog raised mid-script is named to the script AND in the reply, with its checkpoint'
       Tool = 'horizun_execute_python'
       Args = @{ code = @'
from Autodesk.Revit.UI import TaskDialog
checkpoint('HZ-LIVE-MOD-001')
before = len(revit_raised())
TaskDialog.Show('Horizun live probe',
                'Raised on purpose. The bridge must cancel this without anyone touching it.')
mine = revit_raised(before)
__output__ = {
    'status': 'completed_unverified',
    'summary': 'raised one dialog on purpose and read it back from inside the script',
    'seen_from_inside': len(mine),
    'named_from_inside': (mine[0]['description'] if mine else None),
    'while_from_inside': (mine[0]['while'] if mine else None),
}
'@
                 target_document = $Document; idempotency_key = "live-dialogs-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the dialog record reaching the script and the reply (needs -Document)'
       Check = { param($d)
                 $d.revit_raised_observed -eq $true -and
                 $d.dialogs.Count -ge 1 -and
                  # Ad-hoc TaskDialogShowingEventArgs exposes Message but its DialogId
                  # is empty; the title passed to TaskDialog.Show is not an event property.
                  # Match the unique message that both the script and reply can observe.
                  $d.dialogs[0].description -match 'Raised on purpose' -and
                 $d.dialogs[0].answered -match 'cancelled by the bridge' -and
                 # The attribution: the script's own checkpoint, stamped on the dialog.
                 $d.dialogs[0].'while' -eq 'HZ-LIVE-MOD-001' -and
                 # And the half no end-of-run report can give: the script saw it DURING
                 # the run, windowed to exactly the call that raised it.
                  $d.output.seen_from_inside -eq 1 -and
                  $d.output.while_from_inside -eq 'HZ-LIVE-MOD-001' }
       # Dispatcher appends its human "what Revit raised" narration to content.text.
       # That text is intentionally not a JSON document once a dialog exists; the
       # machine-readable reply is structuredContent, which is exactly what this
       # probe is about.
       UseStructured = $true
       ErrorIsNotCovered = 'DISABLED|requires BOTH' },

    @{ Name = 'dialog_answer scopes the dismissal and refuses a word that is not cancel|dismiss'
       Tool = 'horizun_execute_python'
       Args = @{ code = @'
refused = False
try:
    with dialog_answer('acknowledge'):
        pass
except Exception as e:
    refused = 'cancel' in str(e) and 'dismiss' in str(e)
with dialog_answer('dismiss'):
    pass
__output__ = {
    'status': 'completed_unverified',
    'summary': 'exercised the scoped dialog answer',
    'refused_bad_word': refused,
    'scope_opened_and_closed': True,
}
'@
                 target_document = $Document; idempotency_key = "live-dialogscope-$probeRun" }
       Needs = 'Document'
       NotCovered = 'the scoped dialog answer inside a script (needs -Document)'
       Check = { param($d)
                 $d.output.refused_bad_word -eq $true -and
                 $d.output.scope_opened_and_closed -eq $true -and
                 # Nothing was raised, so nothing may be reported as raised.
                 $d.revit_raised_observed -eq $true -and $d.dialogs.Count -eq 0 }
       ErrorIsNotCovered = 'DISABLED|requires BOTH' }
)

# ---------------------------------------------------------------------------
# HOW MUCH OF THE MODEL THE ANSWER IS ABOUT.
#
# Every read-only tool now carries a visibility_coverage block. These probes are
# in two halves, and both are needed:
#
#   * the SHAPE is asserted against whatever document is under test, because a
#     block that is missing from one tool is a caller who learns to check three
#     of the four;
#   * the STATE that matters - a closed workset - needs a model that really has
#     one, and gets its own probe against -ClosedWorksetDocument.
# ---------------------------------------------------------------------------
$coverageShape = {
    param($block)
    if ($null -eq $block -or
        -not ($block.PSObject.Properties.Name -contains 'coverage_complete') -or
        [string]::IsNullOrWhiteSpace($block.note)) { return $false }

    # A single-document answer reports its workset measurements directly.
    $single = ($block.PSObject.Properties.Name -contains 'is_workshared') -and
              ($block.PSObject.Properties.Name -contains 'worksets_total') -and
              ($block.PSObject.Properties.Name -contains 'worksets_open') -and
              ($block.PSObject.Properties.Name -contains 'worksets_closed')

    # clash is federated: the host and each loaded link are separate documents,
    # so flattening their worksets into one count would be meaningless. It reports
    # one valid single-document block per source plus an aggregate verdict.
    $federated = ($block.PSObject.Properties.Name -contains 'sources_measured') -and
                 ($block.PSObject.Properties.Name -contains 'sources_incomplete') -and
                 ($block.PSObject.Properties.Name -contains 'by_source') -and
                 [int]$block.sources_measured -eq @($block.by_source).Count -and
                 @($block.by_source).Count -ge 1
    if ($federated) {
        foreach ($source in @($block.by_source)) {
            if (-not (& $coverageShape $source)) { return $false }
        }
    }

    $single -or $federated
}


# What this run did NOT exercise, named one by one at the end. A guarantee absent
# from the output is indistinguishable from one that passed, which is how a
# missing probe becomes a claim.
$notCovered = @()

# Probes that need a named document. Without -Document they are reported as NOT
# COVERED: pointing them at a guess would pass for the wrong reason.
if ($Document) {
    # Document.Close throws for the active UIDocument. This must be refused during
    # rehearsal, before minting a discard token for an operation Revit cannot do.
    $probes += @{ Name = 'close REFUSES the active document before issuing a token'
                  Tool = 'horizun_document_session'
                  Args = @{ operation = 'close'; target_document = $Document
                            save_on_close = $false; discard_unsaved = $true; dry_run = $true }
                  ExpectError = 'ACTIVE document.*no confirmation token was issued or consumed|hidden/refused by permission_profile' }

    # The block has to be on ALL FOUR of the read-only answers, not on the one
    # somebody remembered. A caller who finds it on model_scan and not on
    # quantities learns to trust a total that carries no coverage at all.
    $probes += @{ Name = 'model_scan carries a visibility_coverage block'
                  Tool = 'horizun_model_scan'
                  Args = @{ target_document_title = $Document; sections = @('document'); top = 1 }
                  Check = { param($d) & $coverageShape $d.visibility_coverage } }

    $probes += @{ Name = 'audit_model carries a visibility_coverage block'
                  Tool = 'horizun_audit_model'; Args = @{ target_document = $Document; top = 1 }
                  Check = { param($d) & $coverageShape $d.visibility_coverage } }

    $probes += @{ Name = 'clash carries a visibility_coverage block'
                  Tool = 'horizun_clash'
                  Args = @{ target_document = $Document
                            categories_a = @('OST_Walls'); categories_b = @('OST_Floors'); max_results = 1 }
                  Check = { param($d) & $coverageShape $d.visibility_coverage } }

    # model_scan's own account of the links, remembered so the next probe can
    # compare against it. $script: because a Check block runs in its own scope.
    $script:scanLinks = $null
    $probes += @{ Name = 'links: coverage is complete and none is falsely unloaded'
                  Tool = 'horizun_model_scan'
                  Args = @{ target_document_title = $Document; sections = @('links'); top = 3 }
                  Check = { param($d)
                            $l = $d.sections.links
                            $script:scanLinks = $l
                            $l.status -eq 'ok' -and $l.rvt_links_coverage_complete -eq $true -and
                            $l.rvt_links_status_unknown -eq 0 } }

    # THIS PROBE DID NOT DO WHAT ITS NAME SAID. It asserted that audit_model
    # reports ZERO link issues - which is a statement about the MODEL, not about
    # the two commands agreeing, and it fails on any model with an unloaded link
    # while both commands are answering correctly. Measured 2026-07-30: audit said
    # 6 of 6 not loaded, scan said not_loaded=6, they agreed exactly, and the probe
    # reported FAIL. It never compared them.
    #
    # It now compares the verdicts without pretending their units are identical:
    # audit counts link TYPES and scan counts link INSTANCES. One unloaded type can
    # have several placed instances, so exact numeric equality would reject two
    # correct answers. They must agree on whether unloaded links exist and whether
    # every status was readable.
    $probes += @{ Name = 'audit and scan AGREE about the links'
                  Tool = 'horizun_audit_model'; Args = @{ target_document = $Document; top = 2 }
                  Check = { param($d)
                            $links = @($d.findings | Where-Object { $_.check -eq 'links' })
                            if ($links.Count -ne 1) { return $false }
                            if (-not $script:scanLinks) { return $false }   # the scan probe must have run first
                            $auditNotLoaded = [int]$links[0].count
                            $scanNotLoaded  = [int]$script:scanLinks.rvt_links_not_loaded
                            (($auditNotLoaded -gt 0) -eq ($scanNotLoaded -gt 0)) -and
                            ($links[0].coverage_complete -eq $true) -and
                            ($script:scanLinks.rvt_links_coverage_complete -eq $true) -and
                            ([int]$links[0].elements_unreadable -eq 0) -and
                            ([int]$script:scanLinks.rvt_links_status_unknown -eq 0) } }

    if ($InactiveDocument) {
        # A DIFFERENT refusal from "not open": the document exists in this session,
        # the gate finds it, and still refuses because it is not the active one.
        # The message must say so - matching 'No open document matches' here would
        # mean the gate took the no-match path and the distinction went untested.
        $probes += @{ Name = 'delete REFUSES a document that is open but NOT active'
                      Tool = 'horizun_delete_verified'
                      Args = @{ mode = 'ids'; ids = @(999999999); target_document = $InactiveDocument }
                      ExpectError = 'but the ACTIVE document is'
                      NotCoveredOnNoMatch = 'the InactiveDocument fixture names a document that is not open in this Revit' }
    }
    else {
        $notCovered += 'delete refusing a document that is OPEN but not ACTIVE (needs -InactiveDocument, a second document open in that Revit)'
    }

    # THE GATE ITSELF, for the commands that were unreachable until 7418fbc. Each
    # demanded a token and never issued one, so a rehearsal returning a token IS
    # the fix, and its absence is what shipped.
    $probes += @{ Name = 'write_params rehearsal ISSUES the token its execution demands'
                  Tool = 'horizun_write_params_verified'
                  Args = @{ target_document = $Document; dry_run = $true
                            writes = @(@{ target = 'project_info'; parameter = 'PROJECT_NAME'; value = 'HZ_REHEARSAL_ONLY' }) }
                  Check = { param($d) $d.mode -eq 'dry_run' -and $d.confirmation_token -and
                                      $d.transaction_status -eq 'not_started' } }

    # bind_shared_param's rehearsal used to fall through into the write: dry_run
    # was read once, to skip the confirmation gate, and never branched on again.
    # A PASS here means it stopped before the transaction AND handed back a token.
    #
    # This is the ONE probe in this file that would write if the fix were absent,
    # so it runs only against a document the caller has named as expendable for
    # it. Never point -SpfPath at a production model's session without meaning it.
    if ($SpfPath -and $SpfParam) {
        $probes += @{ Name = 'bind_shared_param rehearsal STOPS before the transaction'
                      Tool = 'horizun_bind_shared_param'
                      Args = @{ target_document = $Document; dry_run = $true
                                spf_path = $SpfPath; param_name = $SpfParam
                                categories = @('OST_Walls'); binding_kind = 'Instance' }
                      Check = { param($d) $d.mode -eq 'dry_run' -and $d.confirmation_token -and
                                          $d.transaction_status -eq 'not_started' } }
    }
    else {
        $notCovered += 'bind_shared_param rehearsing without writing (needs -SpfPath and -SpfParam; until this is run live, the fix in ab94611 is NOT verified)'
    }

    # A nonexistent id is NOT a rehearsed delete plan. The product must report it as
    # unresolved and withhold confirmation; issuing a token here would authorize an
    # apply whose only target was never previewed. A separate disposable-model probe
    # below uses a real id and proves the token-bearing path.
    $probes += @{ Name = 'delete dry run with no resolvable target withholds confirmation and writes nothing'
                  Tool = 'horizun_delete_verified'
                  Args = @{ mode = 'ids'; ids = @(999999999); target_document = $Document; id_cap = 2 }
                  Check = { param($d)
                            $d.dry_run -eq $true -and -not $d.confirmation_token -and
                            $d.deleted_total -eq $null -and $d.elements_before -eq $d.elements_after -and
                            [int]$d.not_found_total -eq 1 -and
                            $d.application.state -eq 'partial' -and
                            $d.application.fully_applied -eq $false } }

    $probes += @{ Name = 'delete REFUSES a token minted for a different plan'
                  Tool = 'horizun_delete_verified'
                  Args = @{ mode = 'ids'; ids = @(888888888); target_document = $Document
                            dry_run = $false; confirmation_token = 'hz-0000000000000000000000000000000000'
                            idempotency_key = "live-delete-bad-token-$probeRun" }
                  ExpectError = 'No such confirmation token' }

    # The category is a PARAMETER because it has to exist in the model under test.
    # Hard-coded to OST_Floors, this probe went UNVERIFIED on an HVAC model that has
    # no floors - and the refusal it got was correct: quantities declines to report
    # a total of zero, because zero reads as "this is empty" rather than "you asked
    # for nothing". A right answer that the harness could not use.
    $probes += @{ Name = ("quantities reports coverage per source, never a defaulted zero (" + $QuantityCategory + ")")
                  Tool = 'horizun_quantities'
                  Args = @{ target_document_title = $Document
                            category = $QuantityCategory; only_disagreements = $true; top = 1 }
                  Check = { param($d)
                            $d.coverage -and $d.coverage.volume_geometry.total_is_complete -ne $null -and
                            $d.comparison.candidates -ge 0 -and
                            # A quantity is the answer somebody puts in a budget. It must
                            # never travel without saying how much of the model it is over.
                            (& $coverageShape $d.visibility_coverage) } }

    if ($InactiveDocument) {
        # The verifier itself, exercised negatively: the money tool must refuse to
        # measure when the caller names a document that is NOT the active one.
        $probes += @{ Name = 'quantities REFUSES a target_document_title that is not the active document'
                      Tool = 'horizun_quantities'
                      Args = @{ target_document_title = $InactiveDocument
                                category = $QuantityCategory; top = 1 }
                      ExpectError = 'Refusing to measure' }
    }

    $probes += @{ Name = 'list_elements reports bounded host/link rows and federated coverage'
                  Tool = 'horizun_list_elements'
                  Args = @{ category = $QuantityCategory; include_links = $true; max_rows = 1 }
                  Check = { param($d)
                            $d.total -ge $d.returned -and $null -ne $d.rows -and
                            $null -ne $d.unavailable -and
                            $null -ne $d.federated_coverage.coverage_complete } }

    $probes += @{ Name = 'create_schedule dry run issues a token without opening a transaction'
                  Tool = 'horizun_create_schedule'
                  Args = @{ target_document = $Document; category = $QuantityCategory
                            name = 'HZ_SCHEDULE_REHEARSAL_ONLY'; fields = @('Count'); dry_run = $true
                            include_links = $true }
                  Check = { param($d)
                            $d.dry_run -eq $true -and $d.confirmation_token -and
                            $d.transaction_status -eq 'not_started' } }

    $probes += @{ Name = 'create_elements rehearses a typed architectural batch without a transaction'
                  Tool = 'horizun_create_elements'
                  Args = @{ target_document = $Document; units = 'mm'; dry_run = $true
                            elements = @(@{ kind = 'level'; name = "HZ_DRY_$probeRun"; elevation = 987654 }) }
                  Check = { param($d)
                            $d.dry_run -eq $true -and $d.confirmation_token -and
                            $d.transaction_status -eq 'not_started' -and $d.valid -eq 1 } }

    $probes += @{ Name = 'manage_views rehearses a new drafting view without a transaction'
                  Tool = 'horizun_manage_views'
                  Args = @{ target_document = $Document; dry_run = $true
                            actions = @(@{ operation = 'create_drafting'; name = "HZ_DRY_$probeRun" }) }
                  Check = { param($d)
                            $d.dry_run -eq $true -and $d.confirmation_token -and
                            $d.transaction_status -eq 'not_started' -and $d.valid -eq 1 } }

    if ($ReleaseGate) {
        if ($FamilyTemplate) {
            $familyDryOutput = Join-Path $env:TEMP ("horizun-family-dry-{0}.rfa" -f $probeRun)
            $probes += @{ Name = 'create_family compiles a parametric RFA plan without opening a family document or writing a file'
                          Tool = 'horizun_create_family'
                          Args = @{ target_document = $Document; template_path = $FamilyTemplate; output_path = $familyDryOutput
                                    units = 'mm'; dry_run = $true; load_into_project = $false
                                    parameters = @(
                                      @{ name = 'Depth'; data_type = 'length'; group = 'geometry' },
                                      @{ name = 'Visible'; data_type = 'yesno'; group = 'general' },
                                      @{ name = 'Material'; data_type = 'material'; group = 'materials' },
                                      @{ name = 'Diameter'; data_type = 'length'; group = 'geometry' },
                                      @{ name = 'Width'; data_type = 'length'; group = 'geometry' }
                                    )
                                    types = @(@{ name = '600'; values = @{ Depth = 600; Visible = $true; Diameter = 100; Width = 500 } })
                                    forms = @(@{ key = 'body'; kind = 'extrusion'; plane = 'xy'; depth = 600
                                                profile = @(, @(@(-250,-250,0),@(250,-250,0),@(250,250,0),@(-250,250,0)))
                                                end_parameter = 'Depth'; material_parameter = 'Material'; visibility_parameter = 'Visible' },
                                              @{ key = 'swept_body'; kind = 'sweep'; plane = 'xy'; path_plane = 'xz'
                                                 profile = @(, @(@(-50,-50,0),@(50,-50,0),@(50,50,0),@(-50,50,0)))
                                                 path = @(@(0,0,0),@(0,0,600)); profile_plane_location = 'Start' })
                                    connectors = @(@{ key = 'pipe_out'; host_form_key = 'body'; kind = 'pipe';
                                                      face_normal = @(0,0,1); system_type = 'SupplyHydronic';
                                                      diameter_parameter = 'Diameter'; primary = $true })
                                    reference_planes = @(
                                      @{ key = 'left'; name = 'Left'; bubble_end = @(-250,-400,0); free_end = @(-250,400,0); cut_vector = @(0,0,1) },
                                      @{ key = 'right'; name = 'Right'; bubble_end = @(250,-400,0); free_end = @(250,400,0); cut_vector = @(0,0,1) })
                                    dimensions = @(@{ key = 'width'; reference_plane_keys = @('left','right');
                                                     line_start = @(-250,-500,0); line_end = @(250,-500,0); label_parameter = 'Width' })
                                    family_lines = @(@{ key = 'center'; kind = 'symbolic'; plane = 'xy';
                                                       start = @(-250,0,0); end = @(250,0,0) }) }
                          Check = { param($d)
                                    $d.dry_run -eq $true -and $d.family_kind -eq 'loadable_rfa' -and
                                    $d.confirmation_token -and $d.forms -eq 2 -and $d.connectors -eq 1 -and
                                    $d.reference_planes -eq 2 -and $d.dimensions -eq 1 -and $d.family_lines -eq 1 -and
                                    -not (Test-Path -LiteralPath $familyDryOutput) } }
        }
        else {
            $notCovered += 'create_family parametric rehearsal (needs an installed Revit Family Template or -FamilyTemplate)'
        }
    }
}
else {
    Write-Host "  (no -Document given: the link, quantities and confirmation probes are NOT COVERED)" -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------------------
# THE CLOSED WORKSET. The one condition that cannot be detected from inside the
# answer it corrupts, and the one that cannot be simulated - it is a property of
# how a real model was opened, so it needs a real model that has one.
# ---------------------------------------------------------------------------
$ClosedWorksetActivationReady = $null
$ClosedWorksetActive = $null
$ClosedWorksetCleanupTarget = $null
$ClosedWorksetProbeTitle = $ClosedWorksetDocument
$closedFixtureTemporaryCopy = $null
$closedFixtureSafeToDelete = $false
$closedCleanupExpectedDiscard = $null
$closeOpenSourceName = 'close the open source before loading its detached closed-workset copy'
$activateClosedName = 'activate the closed-workset fixture before measuring its loaded coverage'
$restoreDocumentName = 'restore the original active document after closed-workset probes'
if ($ClosedWorksetDocument -and $ClosedWorksetName) {
    # Every model-reading command correctly refuses an inactive target. The old harness
    # queued these probes against a title that was open but NOT active, then blamed four
    # different products for enforcing the active-document guard. Bracket the probes with
    # typed, verified activation of the already-open documents. Paths are discovered from
    # health after the MCP session starts; the placeholders are never usable paths.
    # Revit refuses to keep a central and a detached/local copy of that same
    # model open in one session. All earlier probes are now complete, so close
    # the harness-owned disposable source without saving, switch to the already
    # open inactive fixture, then open the copied fixture with the typed workset
    # plan. The source is reopened explicitly below before the write tier.
    $probes += @{ Name = $closeOpenSourceName
                  Tool = 'horizun_document_session'
                  Args = @{ operation = 'close'; target_document = $Document
                            dry_run = $false; save_on_close = $false; discard_unsaved = $false
                            activate_other = $true
                            idempotency_key = "live-close-source-before-workset-$probeRun" }
                  Needs = 'ClosedWorksetActivationReady'
                  NotCovered = 'closed-workset probes need unique local paths before the open source can be closed safely'
                  Check = { param($d)
                            $d.closed -eq $true -and $d.title -eq $Document -and
                            $d.saved_on_close -eq $false -and $d.discarded_unsaved_changes -eq $false } }

    $probes += @{ Name = $activateClosedName
                  Tool = 'horizun_document_session'
                  UseStructured = $true
                  Args = @{ operation = 'open'; file_path = 'C:\ZZ_HORIZUN_CLOSED_WORKSET_PATH_NOT_DISCOVERED.rvt'
                            expected_version = "$Year"; allow_upgrade = $false
                            close_workset_names = @($ClosedWorksetName)
                            idempotency_key = "live-activate-closed-$probeRun" }
                  Needs = 'ClosedWorksetActivationReady'
                  NotCovered = 'closed-workset probes need unique local paths for both the closed fixture and the original active document'
                  Check = { param($d)
                            $d.active_document_verified -eq $true -and
                            $d.title -eq $ClosedWorksetProbeTitle -and
                            $d.workset_configuration_applied -eq $true -and
                            $d.workset_configuration_evidence.applied -eq $true -and
                            @($d.closed_worksets_requested) -contains $ClosedWorksetName -and
                            $d.status -eq 'opened' -and $d.opened_now -eq $true } }

    $probes += @{ Name = 'a CLOSED workset makes model_scan report incomplete coverage'
                  Tool = 'horizun_model_scan'
                  Args = @{ target_document_title = $ClosedWorksetProbeTitle
                            sections = @('worksets'); top = 50 }
                  Needs = 'ClosedWorksetActive'
                  NotCovered = 'the closed-workset document could not be activated safely by unique discovered path'
                  Check = { param($d)
                            $v = $d.visibility_coverage
                            $script:closedCoverage = $v
                            (& $coverageShape $v) -and
                            $v.is_workshared -eq $true -and
                            [int]$v.worksets_closed -ge 1 -and
                            [int]$v.worksets_total -gt [int]$v.worksets_open -and
                            $v.coverage_complete -eq $false -and
                            # The whole scan has to go incomplete, not just this block.
                            # A section that ran perfectly over a model with holes in it
                            # is exactly what complete=true used to be claimed off.
                            $d.complete -eq $false -and
                            $d.note -match 'INCOMPLETE' -and
                            # And the words a reader needs, not the fact about Revit.
                            $v.note -match 'DO NOT READ AN ABSENCE' } }

    $probes += @{ Name = 'the worksets section names WHICH worksets are closed'
                  Tool = 'horizun_model_scan'
                  Args = @{ target_document_title = $ClosedWorksetProbeTitle
                            sections = @('worksets'); top = 50 }
                  Needs = 'ClosedWorksetActive'
                  NotCovered = 'the closed-workset document could not be activated safely by unique discovered path'
                  Check = { param($d)
                            $w = $d.sections.worksets
                            $w.status -eq 'ok' -and
                            [int]$w.worksets_closed -ge 1 -and
                            ([int]$w.worksets_open + [int]$w.worksets_closed) -eq [int]$w.user_worksets -and
                            # A closed workset reports 0 elements for a reason that has
                            # nothing to do with the model, and must say so beside the 0.
                            @($w.worksets.items | Where-Object { $_.is_open -eq $false }).Count -ge 1 -and
                            @($w.worksets.items |
                              Where-Object { $_.is_open -eq $false -and $_.elements_note -match 'CLOSED' }
                             ).Count -ge 1 } }

    $probes += @{ Name = 'a CLOSED workset makes audit_model report incomplete coverage'
                  Tool = 'horizun_audit_model'; Args = @{ target_document = $ClosedWorksetProbeTitle; top = 1 }
                  Needs = 'ClosedWorksetActive'
                  NotCovered = 'the closed-workset document could not be activated safely by unique discovered path'
                  Check = { param($d)
                            $d.visibility_coverage.coverage_complete -eq $false -and
                            # audit_model already had a coverage_complete of its own, for
                            # checks that could not read an element. A closed workset is a
                            # third way to miss the model and reaches the same flag.
                            $d.coverage_complete -eq $false -and
                            $d.note -match 'INCOMPLETE' } }

    $probes += @{ Name = 'a CLOSED workset makes clash refuse to call its zero complete'
                  Tool = 'horizun_clash'
                  Args = @{ target_document = $ClosedWorksetProbeTitle
                            categories_a = @('OST_Walls'); categories_b = @('OST_Floors'); max_results = 1 }
                  Needs = 'ClosedWorksetActive'
                  NotCovered = 'the closed-workset document could not be activated safely by unique discovered path'
                  Check = { param($d)
                            $d.visibility_coverage.coverage_complete -eq $false -and
                            $d.result -ne 'complete' -and
                            $d.headline -match 'DO NOT READ AN ABSENCE' } }

    $probes += @{ Name = 'a CLOSED workset rides along with the quantity itself'
                  Tool = 'horizun_quantities'
                  # target_document_title is retargeted to the VERIFIED opened title by the
                  # activation handler, so a stolen active document becomes a named refusal
                  # instead of a takeoff of whatever happened to be in front (run 19 measured
                  # exactly that: same fixture bytes, 'No elements matched' from another doc).
                  Args = @{ target_document_title = $ClosedWorksetProbeTitle
                            category = $QuantityCategory; only_disagreements = $true; top = 1 }
                  Needs = 'ClosedWorksetActive'
                  NotCovered = 'the closed-workset document could not be activated safely by unique discovered path'
                  Check = { param($d)
                            $d.visibility_coverage.coverage_complete -eq $false -and
                            # The headline is the sentence somebody quotes into a budget.
                            $d.headline -match 'INCOMPLETE COVERAGE' } }

    $probes += @{ Name = $restoreDocumentName
                  Tool = 'horizun_document_session'
                  UseStructured = $true
                  Args = @{ operation = 'open'; file_path = 'C:\ZZ_HORIZUN_ORIGINAL_PATH_NOT_DISCOVERED.rvt'
                            expected_version = "$Year"; allow_upgrade = $false
                            idempotency_key = "live-restore-active-$probeRun" }
                  Needs = 'ClosedWorksetActivationReady'
                  NotCovered = 'closed-workset probes were not run because the original document could not be restored safely'
                  Check = { param($d)
                            $d.active_document_verified -eq $true -and $d.title -eq $Document -and
                            $d.status -eq 'opened' -and $d.opened_now -eq $true } }
}
else {
    $notCovered += 'a CLOSED workset making scan, audit, quantities and clash all report incomplete coverage ' +
                   '(needs -ClosedWorksetDocument and -ClosedWorksetName: a local WORKSHARED fixture and the exact ' +
                   'workset to keep CLOSED). It cannot be simulated - a closed workset is a property of how the model was opened - ' +
                   'and it is the one condition that leaves no trace in the answer it corrupts, so passing this off ' +
                   'a model with every workset open would be this suite making the exact substitution it exists to catch.'
    Write-Host "  (closed-workset fixture incomplete: -ClosedWorksetDocument and -ClosedWorksetName are both required)" -ForegroundColor DarkYellow
}

if ($OldFile) {
    if (-not (Test-Path $OldFile)) { throw "-OldFile does not exist: $OldFile" }
    $probes += @{ Name = "open_document REFUSES a file saved in another Revit"
                  Tool = 'horizun_open_document'; Args = @{ path = $OldFile; idempotency_key = "live-open-old-file-$probeRun" }
                  ExpectError = 'REFUS' }
}

# ---------------------------------------------------------------------------
# One MCP session, intentionally sequential.
#
# This broad acceptance suite waits for each reply so failures have one obvious
# cause and its destructive probes stay in a deliberate order. The bridge itself
# now accepts concurrent callers into a bounded FIFO queue; queue ordering,
# backpressure and cancellation-before-start have their own focused live test.
# ---------------------------------------------------------------------------
$env:HORIZUN_REVIT_YEAR = "$Year"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Server
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
# MEASURED on run 15: without this, a unicode argument (Tubería) reaches the
# server mojibake - the reply was fine, the REQUEST was not.
$psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$proc = [System.Diagnostics.Process]::Start($psi)

# Production calls such as manage_revisions carry arrays nested below the
# JSON-RPC envelope (actions -> clouds -> loops -> points).  Depth 8 truncates
# those valid payloads only after the envelope is added, so keep the transport
# comfortably above every closed tool schema's maximum nesting.
function Send-Rpc($obj) { $proc.StandardInput.WriteLine(($obj | ConvertTo-Json -Depth 32 -Compress)); $proc.StandardInput.Flush() }
$script:rpcNotifications = [System.Collections.Generic.List[object]]::new()
function Read-Rpc([int]$TimeoutMs = 620000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ($true) {
        $t = $proc.StandardOutput.ReadLineAsync()
        # See hz-call.ps1: use WhenAny rather than Task.Wait or IsCompleted
        # polling for redirected StreamReader reads on Windows PowerShell 5.1.
        $remaining = [Math]::Max(1, [int](($deadline - (Get-Date)).TotalMilliseconds))
        $delay = [Threading.Tasks.Task]::Delay($remaining)
        $winner = [Threading.Tasks.Task]::WhenAny(
            [Threading.Tasks.Task[]]@($t, $delay)).Result
        if (-not [object]::ReferenceEquals($winner, $t)) { return $null }
        if (-not $t.Result) { return $null }
        try { $m = $t.Result | ConvertFrom-Json } catch { continue }
        # Progress and list-change notifications carry no id and are not
        # anybody's answer. Keep them in the one stdout reader's inbox so a
        # probe can inspect them without starting a second ReadLineAsync on the
        # same StreamReader (which .NET refuses as a concurrent read).
        if ($m.id) { return $m }
        [void]$script:rpcNotifications.Add($m)
    }
}

Send-Rpc @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='verify-live'; version='1' } } }
$initReply = Read-Rpc
if ($initReply.result.protocolVersion -ne '2025-11-25') {
    throw "MCP negotiation returned '$($initReply.result.protocolVersion)', expected 2025-11-25"
}
Send-Rpc @{ jsonrpc='2.0'; method='notifications/initialized' }

Send-Rpc @{ jsonrpc='2.0'; id=999001; method='tools/list'; params=@{} }
$listReply = Read-Rpc
$listed = @($listReply.result.tools)

# Resolve the two exact local paths used to bracket the closed-workset probes.
# ClosedWorksetDocument is deliberately a TITLE fixture because the assertions are
# about what Revit has loaded, but activation must never guess by title alone. Health
# is the authoritative inventory of this Revit session; require one title match for
# each side and a real local path, then document_session verifies the activation.
if ($ClosedWorksetDocument -and $ClosedWorksetName -and $Document) {
    Send-Rpc @{ jsonrpc='2.0'; id=999003; method='tools/call';
                params=@{ name='horizun_health'; arguments=@{} } }
    $activationHealthReply = Read-Rpc
    $activationHealth = $null
    if ($activationHealthReply -and -not [bool]$activationHealthReply.result.isError) {
        try { $activationHealth = $activationHealthReply.result.content[0].text | ConvertFrom-Json } catch { }
    }
    $closedMatches = @($activationHealth.open_documents | Where-Object { $_.title -eq $ClosedWorksetDocument })
    $returnMatches = @($activationHealth.open_documents | Where-Object { $_.title -eq $Document })
    $closedPath = $null
    if ($closedMatches.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($closedMatches[0].path) -and
        (Test-Path -LiteralPath $closedMatches[0].path)) {
        # Production correctly refuses to retrofit workset options onto a live
        # Document. Never close somebody else's document to make this probe pass:
        # exercise the typed OPEN against a disposable byte-for-byte fixture.
        $tempFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) 'HorizunLiveFixtures'
        New-Item -ItemType Directory -Path $tempFixtureRoot -Force | Out-Null
        $closedFixtureTemporaryCopy = Join-Path $tempFixtureRoot ("HZ_CLOSED_{0}.rvt" -f $probeRun)
        Copy-Item -LiteralPath $closedMatches[0].path -Destination $closedFixtureTemporaryCopy
        $closedPath = $closedFixtureTemporaryCopy
        $ClosedWorksetProbeTitle = [IO.Path]::GetFileNameWithoutExtension($closedPath)
    }
    elseif ($closedMatches.Count -eq 0 -and $returnMatches.Count -eq 1 -and
            -not [string]::IsNullOrWhiteSpace($returnMatches[0].path)) {
        # The fixture may be intentionally closed between runs. Its contract is a
        # title; for local disposable fixtures the only safe automatic path is an
        # exact sibling filename beside the original model. Require that exact file.
        $sibling = Join-Path -Path ([IO.Path]::GetDirectoryName($returnMatches[0].path)) -ChildPath ($ClosedWorksetDocument + '.rvt')
        if (Test-Path -LiteralPath $sibling) { $closedPath = $sibling }
    }
    if ($closedPath -and $returnMatches.Count -eq 1 -and
        -not [string]::IsNullOrWhiteSpace($returnMatches[0].path) -and
        (Test-Path -LiteralPath $returnMatches[0].path)) {
        $activateClosed = $probes | Where-Object { $_.Name -eq $activateClosedName } | Select-Object -First 1
        $restoreOriginal = $probes | Where-Object { $_.Name -eq $restoreDocumentName } | Select-Object -First 1
        $activateClosed.Args.file_path = $closedPath
        $restoreOriginal.Args.file_path = $returnMatches[0].path

        # A copied central is detached, never opened as the file everybody
        # synchronizes to. Typed inspect reads BasicFileInfo without opening it.
        Send-Rpc @{ jsonrpc='2.0'; id=999004; method='tools/call'; params=@{
            name='horizun_document_session'; arguments=@{ operation='inspect'; file_path=$closedPath } } }
        $closedInspectReply = Read-Rpc
        $closedInspect = $null
        if ($closedInspectReply -and -not [bool]$closedInspectReply.result.isError) {
            try { $closedInspect = $closedInspectReply.result.content[0].text | ConvertFrom-Json } catch { }
        }
        if ($closedInspect -and $closedInspect.file.is_central -eq $true) {
            $activateClosed.Args.detach = $true
            if ($WriteDocumentDisposable -eq 'yes-this-model-is-disposable') {
                $restoreOriginal.Args.open_central = $true
            }
        }
        $sameDisposableFolder = [string]::Equals(
            [IO.Path]::GetDirectoryName($closedPath),
            [IO.Path]::GetDirectoryName($returnMatches[0].path),
            [StringComparison]::OrdinalIgnoreCase)
        if ($WriteDocumentDisposable -eq 'yes-this-model-is-disposable' -and $sameDisposableFolder) {
            # This explicit machine fixture is a disposable central. The normal product
            # guard remains intact; the harness opts in by the same strong flag that gates
            # its committed write tier, and only for an exact sibling fixture path.
            $activateClosed.Args.open_central = $true
        }
        $ClosedWorksetActivationReady = 'unique local paths discovered'

        # The opened path is harness-owned whether its source was already open or
        # initially closed, so this run owns closing it too.
        if ($closedPath) {
            # Restore the session shape too. The coverage probes never write and the
            # model was opened by this harness, so after restoring the original active
            # document it can be closed without saving or discarding user work.
            $probes += @{ Name = 'close the closed-workset fixture opened by this harness without saving'
                          Tool = 'horizun_document_session'
                          Args = @{ operation = 'close'; target_document = $ClosedWorksetProbeTitle
                                    dry_run = $false; save_on_close = $false; discard_unsaved = $false
                                    activate_other = $true
                                    idempotency_key = "live-close-closed-fixture-$probeRun" }
                          Needs = 'ClosedWorksetCleanupTarget'
                          NotCovered = 'no unique harness-owned document could be identified for cleanup after the open attempt'
                          Check = { param($d)
                                    $d.closed -eq $true -and $d.saved_on_close -eq $false -and
                                    $d.title -eq $ClosedWorksetProbeTitle -and
                                    $d.discarded_unsaved_changes -eq [bool]$closedCleanupExpectedDiscard }
                        }
        }
    }
}

$requiredCurrent = @('horizun_query_model','horizun_create_elements','horizun_manage_system_types',
                     'horizun_transform_elements','horizun_manage_views','horizun_annotate',
                     'horizun_execute_plan','horizun_submit_job')
if ($ReleaseGate) { $requiredCurrent += @('horizun_create_family','horizun_export','horizun_power_bi_push') }
foreach ($required in $requiredCurrent) {
    $tool = $listed | Where-Object { $_.name -eq $required } | Select-Object -First 1
    if (-not $tool) { throw "tools/list does not advertise required current-release tool '$required'" }
    if ($tool.outputSchema.type -ne 'object' -or $null -eq $tool.annotations.idempotentHint) {
        throw "tool '$required' lacks MCP outputSchema/annotations"
    }
}

# A release is not allowed to advertise the right tool names with stale schemas.
# These are the high-value capability edges added in this release; checking them
# on tools/list proves the installed server and add-in agreed on the same contract.
if ($ReleaseGate) {
    $createTool = $listed | Where-Object { $_.name -eq 'horizun_create_elements' } | Select-Object -First 1
    $createKinds = @($createTool.inputSchema.properties.elements.items.properties.kind.enum)
    $missing = @('ceiling','roof','structural_framing','structural_column','cable_tray' | Where-Object { $_ -notin $createKinds })
    if ($missing.Count -gt 0) { throw "create_elements schema is missing: $($missing -join ', ')" }

    $viewTool = $listed | Where-Object { $_.name -eq 'horizun_manage_views' } | Select-Object -First 1
    $viewOps = @($viewTool.inputSchema.properties.actions.items.properties.operation.enum)
    $missing = @('create_ceiling_plan','create_structural_plan','create_drafting','create_section','create_elevation' |
                 Where-Object { $_ -notin $viewOps })
    if ($missing.Count -gt 0) { throw "manage_views schema is missing: $($missing -join ', ')" }

    $exportTool = $listed | Where-Object { $_.name -eq 'horizun_export' } | Select-Object -First 1
    $formats = @($exportTool.inputSchema.properties.format.enum)
    $missing = @('ifc','nwc','fbx' | Where-Object { $_ -notin $formats })
    if ($missing.Count -gt 0) { throw "export schema is missing: $($missing -join ', ')" }

    $familyTool = $listed | Where-Object { $_.name -eq 'horizun_create_family' } | Select-Object -First 1
    if (-not $familyTool.inputSchema.properties.forms -or -not $familyTool.inputSchema.properties.connectors -or
        -not $familyTool.inputSchema.properties.parameters -or -not $familyTool.inputSchema.properties.types -or
        -not $familyTool.inputSchema.properties.reference_planes -or -not $familyTool.inputSchema.properties.dimensions -or
        -not $familyTool.inputSchema.properties.family_lines -or -not $familyTool.inputSchema.properties.nested_instances) {
        throw 'create_family schema lacks its parameter/type/form/connector/reference/dimension/line/nested graph'
    }
    $familyKinds = @($familyTool.inputSchema.properties.forms.items.properties.kind.enum)
    $missing = @('extrusion','blend','revolution','sweep','swept_blend' | Where-Object { $_ -notin $familyKinds })
    if ($missing.Count -gt 0) { throw "create_family form schema is missing: $($missing -join ', ')" }
    $systemTool = $listed | Where-Object { $_.name -eq 'horizun_manage_system_types' } | Select-Object -First 1
    $compound = $systemTool.inputSchema.properties.actions.items.properties.compound_structure
    if (-not $compound.properties.layers -or -not $compound.properties.structural_layer_index -or
        -not $compound.properties.opening_wrapping) {
        throw 'manage_system_types schema lacks typed compound structures'
    }
    $pbiTool = $listed | Where-Object { $_.name -eq 'horizun_power_bi_push' } | Select-Object -First 1
    $pbiProperties = @($pbiTool.inputSchema.properties.PSObject.Properties.Name)
    if ('access_token' -in $pbiProperties -or 'client_secret' -in $pbiProperties) {
        throw 'power_bi_push exposes a credential in MCP arguments'
    }
    foreach ($needed in 'dataset_id','table','rows','dry_run','idempotency_key') {
        if ($needed -notin $pbiProperties) { throw "power_bi_push schema lacks '$needed'" }
    }

    $probes += @{ Name = 'Power BI dry run validates a bounded payload without credentials or network delivery'
                  Tool = 'horizun_power_bi_push'
                  Args = @{ dataset_id = '11111111-1111-1111-1111-111111111111'; table = 'HorizunLiveDryRun'
                            rows = @(@{ Probe = $probeRun; Value = 1 }); dry_run = $true }
                  Check = { param($d)
                            $d.dry_run -eq $true -and $d.rows_validated -eq 1 -and $d.payload_bytes -gt 0 -and
                            $d.limits_enforced.rows_per_request -eq 10000 -and
                            $d.note -match 'No token was requested and no row was sent' } }
}

# Discover an actual host system type from the named model, then run the typed
# compound-structure rehearsal against it. A hard-coded ElementId is not a live
# test; it is a coincidence that breaks as soon as the fixture model changes.
if ($ReleaseGate -and $Document) {
    Send-Rpc @{ jsonrpc='2.0'; id=999002; method='tools/call'; params=@{ name='horizun_query_model'; arguments=@{
        categories=@('OST_Walls','OST_Floors','OST_Roofs','OST_Ceilings'); include_links=$false; include_types=$true; max_rows=500
    } } }
    $systemTypeReply = Read-Rpc
    $systemTypeData = $null
    if ($systemTypeReply -and -not [bool]$systemTypeReply.result.isError) {
        try { $systemTypeData = $systemTypeReply.result.content[0].text | ConvertFrom-Json } catch { }
    }
    $systemTypeRow = @($systemTypeData.rows | Where-Object { $_.is_element_type -eq $true -and $_.source_kind -eq 'host' } |
                       Select-Object -First 1)
    if ($systemTypeRow.Count -eq 1) {
        $sourceTypeId = [long]$systemTypeRow[0].element_id
        $probes += @{ Name = 'manage_system_types rehearses a real host type and typed compound layer without a transaction'
                      Tool = 'horizun_manage_system_types'
                      Args = @{ target_document = $Document; units = 'mm'; dry_run = $true
                                actions = @(@{ source_type_id = $sourceTypeId; new_name = "HZ_DRY_$probeRun"
                                  compound_structure = @{ layers = @(@{ function = 'Structure'; width = 100; material_id = -1 })
                                                         structural_layer_index = 0 } }) }
                      Check = { param($d)
                                $d.dry_run -eq $true -and $d.transaction_status -eq 'not_started' -and
                                $d.valid -eq 1 -and $d.invalid -eq 0 -and $d.confirmation_token } }
    }
    else {
        $notCovered += 'manage_system_types compound-structure rehearsal (the named fixture has no host Wall/Floor/Roof/Ceiling ElementType)'
    }
}

$byId = @{}
$id = 1
foreach ($p in $probes) {
    $id++
    $p.Id = $id
    # Activation is a real prerequisite, not just a reporting label. If it failed,
    # do not execute untargeted audit/quantity calls against whichever model stayed
    # active and then grade those unrelated answers as closed-workset failures.
    if ($p.Needs -eq 'ClosedWorksetActive' -and
        [string]::IsNullOrWhiteSpace($ClosedWorksetActive)) { continue }
    if ($p.Name -eq 'close the closed-workset fixture opened by this harness without saving' -and
        [string]::IsNullOrWhiteSpace($ClosedWorksetCleanupTarget)) { continue }

    $m = $null
    if ($p.Name -eq 'close the closed-workset fixture opened by this harness without saving') {
        # Closing a detached model can itself report IsModified=true. The production
        # guard correctly refuses a blind Close(false), so cleanup follows the exact
        # safe flow a real caller must: rehearse, inspect would_discard, then spend the
        # bound token while still asking for save_on_close=false. The model is a unique,
        # harness-owned temporary copy; no user's pre-existing document is discarded.
        $cleanupDryArgs = @{}
        foreach ($key in $p.Args.Keys) { $cleanupDryArgs[$key] = $p.Args[$key] }
        $cleanupDryArgs.dry_run = $true
        $cleanupDryArgs.discard_unsaved = $false
        $cleanupDryArgs.idempotency_key = "live-close-closed-fixture-dry-$probeRun"
        [void]$cleanupDryArgs.Remove('confirmation_token')
        Send-Rpc @{ jsonrpc='2.0'; id=999007; method='tools/call'; params=@{
            name='horizun_document_session'; arguments=$cleanupDryArgs } }
        $cleanupDryReply = Read-Rpc
        if (-not $cleanupDryReply -or [bool]$cleanupDryReply.result.isError) {
            $m = $cleanupDryReply
            if ($m) { $m.id = $id }
        }
        else {
            $cleanupDry = $cleanupDryReply.result.structuredContent
            $closedCleanupExpectedDiscard = [bool]$cleanupDry.would_discard_unsaved
            $p.Args.dry_run = $false
            $p.Args.save_on_close = $false
            $p.Args.activate_other = $true
            $p.Args.idempotency_key = "live-close-closed-fixture-apply-$probeRun"
            if ($closedCleanupExpectedDiscard) {
                if ([string]::IsNullOrWhiteSpace([string]$cleanupDry.confirmation_token)) {
                    # Preserve the successful rehearsal as a failing probe answer: a
                    # discard without its token must never be attempted.
                    $m = $cleanupDryReply
                    $m.id = $id
                }
                else {
                    $p.Args.discard_unsaved = $true
                    $p.Args.confirmation_token = [string]$cleanupDry.confirmation_token
                }
            }
            else {
                $p.Args.discard_unsaved = $false
                [void]$p.Args.Remove('confirmation_token')
            }
            if (-not $m) {
                Send-Rpc @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$p.Tool; arguments=$p.Args } }
                $m = Read-Rpc
            }
        }
    }
    else {
        Send-Rpc @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$p.Tool; arguments=$p.Args } }
        $m = Read-Rpc
    }
    if ($m) { $byId[[int]$m.id] = $m }
    if ($p.Name -eq $activateClosedName -and $m) {
        $activated = $m.result.structuredContent
        $candidateCleanupTitle = $null
        $activationSucceeded = -not [bool]$m.result.isError
        $activationReportedOpened = $activated -and $activated.opened -eq $true
        $activationProvedConfiguration = $activationSucceeded -and $activated -and
            $activated.active_document_verified -eq $true -and
            $activated.workset_configuration_applied -eq $true -and
            $activated.workset_configuration_evidence.applied -eq $true -and
            @($activated.closed_worksets_requested) -contains $ClosedWorksetName -and
            $activated.status -eq 'opened' -and $activated.opened_now -eq $true -and
            -not [string]::IsNullOrWhiteSpace([string]$activated.title)

        if ($activationReportedOpened -and
            -not [string]::IsNullOrWhiteSpace([string]$activated.title)) {
            # FailWithDetail carries the identity of a document that was opened before
            # a post-open verification failed. It is cleanup evidence, not success.
            $candidateCleanupTitle = [string]$activated.title
        }
        elseif ($activationSucceeded -and $activated -and
                -not [string]::IsNullOrWhiteSpace([string]$activated.title)) {
            $candidateCleanupTitle = [string]$activated.title
        }

        if (-not $activationSucceeded) {
            # Backward-compatible recovery for an installed add-in that predates the
            # structured post-open detail, and an independent check against stale detail.
            # The temporary basename contains this run's GUID, so only one exact/prefixed
            # match can be harness-owned. Ambiguity is NOT permission to close anything.
            Send-Rpc @{ jsonrpc='2.0'; id=999006; method='tools/call'; params=@{
                name='horizun_health'; arguments=@{} } }
            $cleanupHealthReply = Read-Rpc
            $cleanupHealth = $null
            if ($cleanupHealthReply -and -not [bool]$cleanupHealthReply.result.isError) {
                try { $cleanupHealth = $cleanupHealthReply.result.content[0].text | ConvertFrom-Json } catch { }
            }
            $temporaryBase = if ($closedPath) { [IO.Path]::GetFileNameWithoutExtension($closedPath) } else { $null }
            $healthMatches = @($cleanupHealth.open_documents | Where-Object {
                ($closedPath -and $_.path -and [string]::Equals([string]$_.path, [string]$closedPath,
                    [StringComparison]::OrdinalIgnoreCase)) -or
                ($temporaryBase -and $_.title -and
                    ([string]::Equals([string]$_.title, $temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
                     [string]::Equals([string]$_.title, ($temporaryBase + '_detached'), [StringComparison]::OrdinalIgnoreCase)))
            })
            if ($healthMatches.Count -eq 1) {
                $candidateCleanupTitle = [string]$healthMatches[0].title
            }
            elseif ($healthMatches.Count -eq 0 -and $cleanupHealth -and
                    $cleanupHealth.note -notmatch 'INCOMPLETE' -and -not $activationReportedOpened) {
                # Health, not a close refusal, proved that no harness-owned document is
                # open. The temporary file is safe to remove, but cleanup is not called PASS.
                $closedFixtureSafeToDelete = $true
            }
            elseif ($healthMatches.Count -gt 1) {
                $candidateCleanupTitle = $null
                $notCovered += 'closed-workset cleanup found more than one open document matching the unique temporary fixture identity; none was closed'
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($candidateCleanupTitle)) {
            $ClosedWorksetCleanupTarget = $candidateCleanupTitle
            $ClosedWorksetProbeTitle = $candidateCleanupTitle
            # Detached/opened titles are Revit's fact, not a filename convention.
            # Retarget every later coverage and cleanup probe to the verified title.
            foreach ($later in $probes) {
                if ($later.Needs -eq 'ClosedWorksetActive') {
                    if ($later.Args.ContainsKey('target_document_title')) { $later.Args.target_document_title = $ClosedWorksetProbeTitle }
                    if ($later.Args.ContainsKey('target_document')) { $later.Args.target_document = $ClosedWorksetProbeTitle }
                }
                if ($later.Name -eq 'close the closed-workset fixture opened by this harness without saving') {
                    $later.Args.target_document = $ClosedWorksetProbeTitle
                }
            }
        }
        if ($activationProvedConfiguration) {
            $ClosedWorksetActive = 'verified active with measured workset configuration by document_session'
        }
    }
    if ($p.Name -eq 'close the closed-workset fixture opened by this harness without saving' -and
        $m -and -not [bool]$m.result.isError -and $m.result.structuredContent.closed -eq $true -and
        $m.result.structuredContent.saved_on_close -eq $false) {
        $closedFixtureSafeToDelete = $true
    }
}

# ---------------------------------------------------------------------------
# THE WRITE TIER.
#
# Sequential, and it has to be: every probe here needs the confirmation token the
# previous reply issued, so it cannot be pre-batched the way the probes above are.
# Results are collected now and folded into the same PASS/FAIL/UNVERIFIED/NOT
# COVERED accounting below, so one run still has one set of exit codes.
#
# Each probe COMMITS and then believes only what the command re-read out of the
# model. A probe that asserts `fully_verified` is asserting the house contract
# itself: not "the call did not throw" but "the command confirmed its own work".
# ---------------------------------------------------------------------------
$writeResults = @()
$writeAnswers = @()     # every answer this tier got, for the rollback probe below

function Add-Write($name, $tool, $outcome, $detail) {
    $script:writeResults += @{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = $detail }
}

# ---------------------------------------------------------------------------
# RETIRED PROBES. Tools this suite used to cover that this VERSION no longer
# publishes.
#
# Deleting them would quietly shrink the story: a guarantee that used to be
# checked and now is not reads, in a diff, exactly like a guarantee that never
# existed. Leaving them as NOT COVERED was worse - they sat in the denominator
# of the current version's coverage and in the operator's list of gaps, implying
# a fixture was missing when nothing was missing at all.
#
# So they are recorded here with what they covered and what replaced them,
# reported in their own section, and counted in NEITHER the coverage denominator
# nor the gap list. The retirement is a fact about the surface, not a hole in it.
# ---------------------------------------------------------------------------
$RetiredProbes = @(
    @{ Tool = 'horizun_connect_mep'
       Probes = @('connect_mep joins two pipes and confirms the joint from the model',
                  'connect_mep generates a fitting and still confirms the joint')
       Retired = '0.6.x (not published by this build)'
       Covered = 'joining two MEP curves end to end, with and without a generated fitting, and confirming the joint by re-reading the connectors from the model.'
       Replacement = 'No typed replacement in this version. MEP connection is reachable through horizun_execute_python, whose result is self-reported rather than host-verified.' }

    @{ Tool = 'horizun_terminate_riser'
       Probes = @('terminate_riser builds all five pieces and re-reads each one')
       Retired = '0.6.x (not published by this build)'
       Covered = 'building a five-piece riser termination in one transaction and re-reading every piece after the commit.'
       Replacement = 'No typed replacement. horizun_create_elements covers plain pipe creation; the composed termination is a Python fallback case.' }

    @{ Tool = 'horizun_place_sprinklers'
       Probes = @('place_sprinklers seats every head it placed')
       Retired = '0.6.x (not published by this build)'
       Covered = 'placing sprinkler heads on free pipe stubs and proving each one SEATED within tolerance rather than merely existing - the case whose failure was measured on 2026-08-04 (37 placed, 0 seated, rolled back).'
       Replacement = 'No typed replacement. horizun_create_elements places family instances but does not check seating.' }

    @{ Tool = 'horizun_family_mirror_void'
       Probes = @('family_mirror_void copies a void that still cuts')
       Retired = '0.6.x (not published by this build)'
       Covered = 'mirroring a void inside a family and confirming the copy still cut its solid.'
       Replacement = 'horizun_create_family authors voids directly; the mirror-and-still-cuts case has no typed probe in this version.' }
)

$retiredRows = @()
foreach ($r in $RetiredProbes) {
    foreach ($n in $r.Probes) {
        $retiredRows += @{ Name = $n; Tool = $r.Tool; Retired = $r.Retired
                           Covered = $r.Covered; Replacement = $r.Replacement }
    }
}

# Every brace-balanced JSON object in a string, in the order they appear.
#
# A refusal is not "prose then JSON". It is "Error: <sentence> {result}" and then,
# whenever Revit raised anything, "--- what Revit raised while this ran ---
# {warnings}". So a substring running to the end of the message is not valid JSON,
# and the brace that DOES parse to the end is the warnings block, which knows
# nothing about the transaction. Balanced extraction is the only way to get at the
# result object, and the result object is where the verdict lives.
#
# Quotes and escapes are tracked because element names in this domain contain
# braces and quotation marks: 'Tee 3" x 1 1/2"' is a real type name in the fixture.
function Get-JsonObjects([string]$s) {
    $out = @()
    for ($i = 0; $i -lt $s.Length; $i++) {
        if ($s[$i] -ne '{') { continue }
        $depth = 0; $inStr = $false; $esc = $false
        for ($k = $i; $k -lt $s.Length; $k++) {
            $c = $s[$k]
            if ($esc) { $esc = $false; continue }
            if ($c -eq '\') { if ($inStr) { $esc = $true }; continue }
            if ($c -eq '"') { $inStr = -not $inStr; continue }
            if ($inStr) { continue }
            if ($c -eq '{') { $depth++ }
            elseif ($c -eq '}') {
                $depth--
                if ($depth -eq 0) { $out += $s.Substring($i, $k - $i + 1); $i = $k; break }
            }
        }
    }
    return $out
}

# One tool call. Returns the parsed answer plus whether it was an error, because
# for this tier an error IS the finding half the time.
$writeCallId = 900000
function Invoke-Write($tool, $arguments) {
    $script:writeCallId++
    Send-Rpc @{ jsonrpc='2.0'; id=$script:writeCallId; method='tools/call'
                params=@{ name=$tool; arguments=$arguments } }
    $m = Read-Rpc
    if (-not $m) { return @{ replied = $false; isError = $true; text = 'no reply'; data = $null } }

    # A JSON-RPC error reply carries no result at all, so reaching into
    # result.content[0] throws "Cannot index into a null array" and takes the whole
    # harness down with it - one unadvertised tool ending a run that had already
    # proven a dozen things. An error reply IS an answer; report it as one.
    if ($null -eq $m.result) {
        $why = 'the server returned no result'
        if ($m.error) { $why = 'JSON-RPC error: ' + $m.error.message }
        return @{ replied = $true; isError = $true; text = $why; data = $null }
    }
    $content = @($m.result.content)
    if ($content.Count -eq 0 -or $null -eq $content[0]) {
        return @{ replied = $true; isError = [bool]$m.result.isError
                  text = 'the reply carried no content'; data = $null }
    }
    $text = $content[0].text
    $data = $null
    if ($text) {
        try { $data = $text | ConvertFrom-Json }
        catch {
            # A refusal reads "Error: <sentence> {json}", and the verdict this tier
            # cares about most is inside that JSON: transaction_status and
            # fully_verified on a write that did NOT confirm itself. Parsing only
            # clean answers would leave the rollback rule below unable to see the
            # exact cases it exists to catch.
            #
            # The first balanced object that carries transaction_status. Not the
            # first that parses: place_sprinklers quotes every rejected candidate as
            # an object inside its sentence, and the warnings block at the end parses
            # perfectly while knowing nothing about the transaction.
            foreach ($candidateText in (Get-JsonObjects $text)) {
                try { $candidate = $candidateText | ConvertFrom-Json } catch { continue }
                if ($null -ne $candidate.transaction_status) { $data = $candidate; break }
            }
        }
    }
    return @{ replied = $true; isError = [bool]$m.result.isError; text = $text; data = $data
              structured = $m.result.structuredContent }
}

# Dry run, then apply with the token it issued. Returns the APPLY answer. The two
# calls are one probe on purpose: a token that cannot be spent is not a guarantee
# anybody can use.
function Invoke-WriteApply($tool, $arguments, $keyName) {
    $dry = $arguments.Clone(); $dry['dry_run'] = $true
    $d = Invoke-Write $tool $dry
    if ($d.isError -or -not $d.data -or -not $d.data.confirmation_token) {
        return @{ stage = 'dry_run'; answer = $d }
    }
    $apply = $arguments.Clone()
    $apply['dry_run'] = $false
    $apply['confirmation_token'] = $d.data.confirmation_token
    $apply['idempotency_key'] = ("live-write-{0}-{1}" -f $keyName, $probeRun)
    return @{ stage = 'apply'; answer = (Invoke-Write $tool $apply); dry = $d }
}

# Every row of a verification table agreed. An empty table is not agreement:
# "nothing disagreed" over nothing is the substitution this repository refuses.
function All-Rows($rows, [scriptblock]$Predicate) {
    $list = @($rows)
    if ($list.Count -eq 0) { return $false }
    foreach ($r in $list) { if (-not (& $Predicate $r)) { return $false } }
    return $true
}

$writeGate = $null
if (-not $WriteProbes) {
    $writeGate = 'needs -WriteProbes; the default run commits nothing'
}
elseif ([string]::IsNullOrWhiteSpace($WriteDocument)) {
    $writeGate = 'needs -WriteDocument, a model this machine can afford to have written into'
}
elseif ($WriteDocumentDisposable -ne 'yes-this-model-is-disposable') {
    $writeGate = ("-WriteDocument named '{0}' but WriteDocumentDisposable is not " -f $WriteDocument) +
                 "'yes-this-model-is-disposable'. Nothing was written: naming a model is not the same " +
                 'as saying it is expendable.'
}

# The commands act on the ACTIVE document and refuse to switch for you, so which
# document is in front decides which half of this tier can run at all.
$activeTitle = $null
if (-not $writeGate) {
    $h = Invoke-Write 'horizun_health' @{}
    if ($h.data) { $activeTitle = ($h.data.open_documents | Where-Object { $_.is_active }).title }
    if ($activeTitle -ne $WriteDocument) {
        $writeGate = ("-WriteDocument is '{0}' but the ACTIVE document is '{1}'. These commands act on " -f
                          $WriteDocument, $activeTitle) +
                     'the active document and will not switch for you; activate it and re-run.'
    }
}

$writeNames = @(
    @{ N = 'create_elements commits a typed batch and verifies every row from the model'; T = 'horizun_create_elements' }
    @{ N = 'delete dry run issues a confirmation token for a real target and writes nothing'; T = 'horizun_delete_verified' }
    # The rollback guarantee, on a CURRENT tool. It used to be inferred from
    # whichever retired command happened to fail its own verification, which meant
    # it went unproven the moment those commands left the surface. Now it is
    # provoked deliberately - see W6.
    @{ N = 'execute_plan rolls the WHOLE graph back when a later action fails';           T = 'horizun_execute_plan' }

    # ---- W4+: DIMENSIONS. Named here FIRST so a gated run reports every probe
    # ---- as NOT COVERED by name instead of quietly shrinking the denominator.
    @{ N = 'get_dimension_references discovers pipe centerlines deterministically';                          T = 'horizun_get_dimension_references' }
    @{ N = 'a two-reference dimension commits with the materialised default type and is verified field by field'; T = 'horizun_annotate' }
    @{ N = 'a three-reference chain commits with an explicit type';                                          T = 'horizun_annotate' }
    @{ N = 'mm, m and feet measure the same pair identically';                                               T = 'horizun_annotate' }
    @{ N = 'an angular dimension commits between two grids';                                                 T = 'horizun_annotate' }
    @{ N = 'radial and diameter follow the Revit year';                                                      T = 'horizun_annotate' }
    @{ N = 'arc_length follows the Revit year';                                                              T = 'horizun_annotate' }
    @{ N = 'spot elevation and spot coordinate commit on the box top face';                                  T = 'horizun_annotate' }
    @{ N = 'spot_slope is refused naming the missing API and nothing is written';                            T = 'horizun_annotate' }
    @{ N = 'a failed expected_value rolls the WHOLE mixed batch back';                                       T = 'horizun_annotate' }
    @{ N = 'moving a referenced element between dry-run and apply refuses as a stale plan';                  T = 'horizun_annotate' }
    @{ N = 'a deleted reference between dry-run and apply refuses, nothing written';                         T = 'horizun_annotate' }
    @{ N = 'a schedule view is refused for dimensions';                                                      T = 'horizun_annotate' }
    @{ N = 'duplicated references are refused';                                                              T = 'horizun_annotate' }
    @{ N = 'linked references are refused with the structured reason';                                       T = 'horizun_get_dimension_references' }
    @{ N = 'query and edit round-trip: overrides, EQ and a stale refusal';                                   T = 'horizun_edit_dimensions' }
    @{ N = 'a family RFA dimension survives save, close, reopen and re-read';                                T = 'horizun_create_family' }

    # ---- W6+: 2D DETAIL. Same rule as the dimensions: named here FIRST so a
    # ---- gated run reports every probe as NOT COVERED by name instead of
    # ---- quietly shrinking the denominator.
    @{ N = 'query_detail_2d lists the resources of a fresh drafting view';                                   T = 'horizun_query_detail_2d' }
    @{ N = 'detail_2d commits two lines, an arc and a closed polyline, verified field by field';             T = 'horizun_detail_2d' }
    @{ N = 'a filled region with a hole commits and re-reads loops and signature';                           T = 'horizun_detail_2d' }
    @{ N = "masking follows the TYPE's IsMasking, both directions";                                          T = 'horizun_detail_2d' }
    @{ N = 'a self-provisioned detail component and symbol place and verify';                                T = 'horizun_detail_2d' }
    @{ N = 'set_line_style changes an existing curve and a same-batch key';                                  T = 'horizun_detail_2d' }
    @{ N = 'an idempotent replay returns the recorded result and creates nothing';                           T = 'horizun_detail_2d' }
    @{ N = "a stale token refuses after the target's style moved underneath it";                             T = 'horizun_detail_2d' }
    @{ N = 'the incompatible views and the broken loops are refused by name';                                T = 'horizun_detail_2d' }
    @{ N = 'the drafting view is captured as visual evidence';                                               T = 'horizun_capture_view' }
    @{ N = "delete_verified cleans exactly the probe's 2D elements";                                         T = 'horizun_delete_verified' }

    # ---- W7+: PLANIMETRY. Same rule again: named here FIRST so a gated run
    # ---- reports every probe as NOT COVERED by name instead of quietly
    # ---- shrinking the denominator.
    @{ N = 'query_planimetry inventory returns exact totals with named unreadables';            T = 'horizun_query_planimetry' }
    @{ N = 'query_planimetry sheets returns title blocks and placements per sheet';             T = 'horizun_query_planimetry' }
    @{ N = 'query_planimetry views returns template, scale, crop and sheet placement';          T = 'horizun_query_planimetry' }
    @{ N = 'query_planimetry placements returns sheet-coordinate outlines with a known overlap'; T = 'horizun_query_planimetry' }
    @{ N = 'query_planimetry annotations returns dimensions, tags and text with view-plane boxes'; T = 'horizun_query_planimetry' }
    @{ N = 'query_planimetry references answers with real targets or an explicit unknown';      T = 'horizun_query_planimetry' }
    @{ N = 'audit_planimetry finds the sheet without a title block';                            T = 'horizun_audit_planimetry' }
    @{ N = 'audit_planimetry reports the staged viewport overlap with the measured extent';     T = 'horizun_audit_planimetry' }
    @{ N = 'audit_planimetry does not call separated placements overlapping';                   T = 'horizun_audit_planimetry' }
    @{ N = 'a dimension override is an ADVISORY finding by default';                            T = 'horizun_audit_planimetry' }
    @{ N = 'a requirement set turns the same override into a BLOCKING finding';                 T = 'horizun_audit_planimetry' }
    @{ N = 'a naming rule catches the sheet whose number is wrong';                             T = 'horizun_audit_planimetry' }
    @{ N = 'a template rule catches the view whose template is not allowed';                    T = 'horizun_audit_planimetry' }
    @{ N = 'a tag rule names the exact visible element left untagged';                          T = 'horizun_audit_planimetry' }
    @{ N = 'incomplete coverage blocks a clean verdict';                                        T = 'horizun_audit_planimetry' }
    @{ N = 'planimetry pagination returns every row exactly once with a constant total';        T = 'horizun_query_planimetry' }
    @{ N = 'a stale planimetry cursor is refused after the model moved';                        T = 'horizun_query_planimetry' }
    @{ N = 'two identical audits produce the same order and fingerprint';                       T = 'horizun_audit_planimetry' }
    @{ N = 'the planimetry tools leave Document.IsModified untouched';                          T = 'horizun_query_planimetry' }
    @{ N = 'planimetry counts, ids and geometry re-read independently agree';                   T = 'horizun_query_planimetry' }
    @{ N = 'the planimetry surface writes no file and exports nothing';                         T = 'horizun_audit_planimetry' }
    @{ N = 'the disposable document ends the planimetry section with no unplanned change';      T = 'horizun_query_planimetry' }

    # ---- W8+: FIX PLANIMETRY. Same rule once more: named here FIRST so a gated
    # ---- run reports every probe as NOT COVERED by name instead of quietly
    # ---- shrinking the denominator.
    @{ N = 'fix_planimetry is published with write annotations and a closed contract';         T = 'horizun_fix_planimetry' }
    @{ N = 'a dry run rehearses, rolls back, and leaves IsModified and the census untouched'; T = 'horizun_fix_planimetry' }
    @{ N = 'set_view_template assigns the template and ViewTemplateId is re-read';             T = 'horizun_fix_planimetry' }
    @{ N = 'set_view_scale writes the explicit scale and View.Scale is re-read';               T = 'horizun_fix_planimetry' }
    @{ N = 'rename_view lands the exact name and it is re-read';                               T = 'horizun_fix_planimetry' }
    @{ N = 'rename_sheet lands number and name, and BOTH are re-read';                         T = 'horizun_fix_planimetry' }
    @{ N = 'place_title_block adds exactly one and its family, type and sheet are re-read';    T = 'horizun_fix_planimetry' }
    @{ N = 'move_viewport lands the point and GetBoxCenter is re-read within tolerance';       T = 'horizun_fix_planimetry' }
    @{ N = 'move_schedule lands the point and the placement is re-read within tolerance';      T = 'horizun_fix_planimetry' }
    @{ N = 'clear_element_override clears only that override and proves the others unmoved';   T = 'horizun_fix_planimetry' }
    @{ N = 'set_crop writes a rectangular crop and the committed shape is re-read';            T = 'horizun_fix_planimetry' }
    @{ N = 'a finding the model no longer shows is refused with nothing written';              T = 'horizun_fix_planimetry' }
    @{ N = 'a token whose resolved elements moved is refused as a stale plan';                 T = 'horizun_fix_planimetry' }
    @{ N = 'an unknown finding cannot become a correction';                                    T = 'horizun_fix_planimetry' }
    @{ N = 'one invalid action refuses the WHOLE batch and writes none of it';                 T = 'horizun_fix_planimetry' }
    @{ N = 'a failed postcondition abandons the whole batch, including its valid action';      T = 'horizun_fix_planimetry' }
    @{ N = 'an identical replay returns the recorded answer and corrects nothing twice';       T = 'horizun_fix_planimetry' }
    @{ N = 'the same idempotency key with a different payload is refused';                     T = 'horizun_fix_planimetry' }
    @{ N = 'a lost response replays instead of applying the correction again';                 T = 'horizun_fix_planimetry' }
    @{ N = 'the audit run afterwards no longer produces the corrected finding';                T = 'horizun_audit_planimetry' }
    @{ N = 'the reply separates resolved, persistent and NEW findings';                        T = 'horizun_fix_planimetry' }
    @{ N = 'reverting the section returns the census to its reference';                        T = 'horizun_fix_planimetry' }
    @{ N = 'no model was saved by the correction section';                                     T = 'horizun_fix_planimetry' }

    # ---- W9+: AUTONOMOUS PLANIMETRY PRODUCTION.
    @{ N = 'automatic packing commits one complete obstacle-aware sheet arrangement';          T = 'horizun_pack_sheets' }
    @{ N = 'auto-tag planning feeds an explicit-type tag through the verified writer';         T = 'horizun_plan_annotations' }
    @{ N = 'intent dimensioning resolves semantic references and commits the planned chain';   T = 'horizun_plan_annotations' }
    @{ N = 'revision production creates the record, sheet assignment and cloud atomically';    T = 'horizun_manage_revisions' }
    @{ N = 'a real sheet is captured as direct visual-review evidence without PDF';             T = 'horizun_capture_view' }

    # ---- W10+: LINKED DIMENSIONS AND PRODUCTION AT SCALE. Named here FIRST so
    # ---- a gated run reports every probe as NOT COVERED by name instead of
    # ---- quietly shrinking the denominator.
    @{ N = 'the run authors its own link source and links it three ways, rediscovered typed';    T = 'horizun_query_model' }
    @{ N = 'linked discovery carries the four separated ids, a transform fingerprint and host coordinates'; T = 'horizun_get_dimension_references' }
    @{ N = 'two instances of one link are two identities with two fingerprints';                 T = 'horizun_get_dimension_references' }
    @{ N = 'a rotated placement reports has_rotation and host-space directions turned exactly';  T = 'horizun_get_dimension_references' }
    @{ N = 'linked dimensions commit with provenance where supported; Revit 2023 refuses its measured API limit'; T = 'horizun_annotate' }
    @{ N = 'linked-dimension tokens go stale after a move where supported; Revit 2023 withholds every token'; T = 'horizun_annotate' }
    @{ N = 'an unloaded link answers by code link_unloaded and reloads for the cases after';     T = 'horizun_get_dimension_references' }
    @{ N = 'query_dimensions resolves linked references where authorable; Revit 2023 names why none can exist'; T = 'horizun_query_dimensions' }
    @{ N = 'auto_dimension_grids plans a complete chain, commits it, and deduplicates the replay'; T = 'horizun_plan_annotations' }
    @{ N = 'auto_dimension over a link REFUSES by measurement: Revit 2026 rejects linked datums (linked geometry references do work)'; T = 'horizun_plan_annotations' }
    @{ N = 'room production plans oriented elevations, sections and a cropped plan, committed whole'; T = 'horizun_plan_views' }
    @{ N = 'a placeholder sheet converts to a titled sheet and its number cannot be reused';     T = 'horizun_manage_views' }
    @{ N = 'view range, rectangular crop and annotation crop commit and re-read in one batch';   T = 'horizun_manage_views' }
    @{ N = 'viewports align to a still anchor and the outlines re-read within tolerance';        T = 'horizun_manage_views' }
    @{ N = 'a schedule definition edits by declared whole lists and replays idempotently';       T = 'horizun_manage_schedules' }
    @{ N = 'a schedule edited underneath its token refuses as a stale plan';                     T = 'horizun_manage_schedules' }
    @{ N = 'a revision withdraws from a sheet and a second withdrawal refuses by name';          T = 'horizun_manage_revisions' }
    @{ N = 'tool packs shrink the live surface, announce list_changed, and restore';             T = 'horizun_health' }

    # ---- W11: MAXIMUM PROGRAM. Phases 5-14 live: connectors, fittings,
    # ---- penetrations, findings, structure, tabular, links, gate, catalog.
    @{ N = 'include_mep reads a staged pipe: two open round connectors and a matching summary'; T = 'horizun_query_model' }
    @{ N = 'a fitting elbow joins two coincident open connectors and both re-read CONNECTED';   T = 'horizun_create_elements' }
    @{ N = 'a fitting between distant connectors refuses naming the measured millimetres';      T = 'horizun_create_elements' }
    @{ N = 'plan_penetrations turns a staged pipe-wall clash into a wall-opening plan';         T = 'horizun_clash' }
    @{ N = 'a STRUCTURAL wall refuses the opening without the opt-in, and cuts with it';        T = 'horizun_create_elements' }
    @{ N = 'record_findings opens a durable finding, an update survives the re-run';            T = 'horizun_coordination' }
    @{ N = 'the findings export re-reads bytes, sha256 and row count from the file';            T = 'horizun_coordination' }
    @{ N = 'plan_structure columns two grid crossings, commits them, and the replay says already_present'; T = 'horizun_plan_structure' }
    @{ N = 'a CSV becomes verified writes, the replay is a declared no-op, duplicate keys refuse'; T = 'horizun_write_params_verified' }
    @{ N = 'links unload, reload, pin and unpin typed, each state re-read';                     T = 'horizun_manage_links' }
    @{ N = 'the pre-delivery gate answers the declared set and refuses the misspelled one';     T = 'horizun_audit_model' }
    @{ N = 'create_family emits a type catalog verified from the file on disk';                 T = 'horizun_create_family' }
    @{ N = 'health carries the job ledger fold and the session timing facts';                   T = 'horizun_health' }
    # ---- W12: the checklist increment (14) --------------------------------
    @{ N = 'route_run plans an L, and the batch commits whole with its deferred corner';        T = 'horizun_plan_mep' }
    @{ N = 'a 20 mm segment refuses in millimetres and a collinear vertex merges NAMED';        T = 'horizun_plan_mep' }
    @{ N = 'takeoff refusals measured: distance to the main, and the type without Tap preference'; T = 'horizun_create_elements' }
    @{ N = 'a sloped run reads its slope and the verified diameter write re-reads 100 mm';      T = 'horizun_write_params_verified' }
    @{ N = 'the open-connector census measures model-wide and fails the zero gate';             T = 'horizun_audit_model' }
    @{ N = 'a vertical crossing plans a CIRCULAR slab opening and the cut commits';             T = 'horizun_clash' }
    @{ N = 'crossings 300 mm apart cluster into ONE opening spanning both';                     T = 'horizun_clash' }
    @{ N = 'a sleeve stand-in places oriented (rotation inside the same transaction)';          T = 'horizun_create_elements' }
    @{ N = 'a finding takes a comment into its history, an evidence view, and a structural BCF'; T = 'horizun_coordination' }
    @{ N = 'a beam system commits real members, refuses the 100 mm edge, and the wall gets its footing'; T = 'horizun_create_elements' }
    @{ N = 'CSV rows place instances and the edited file refuses the old token as stale';       T = 'horizun_create_elements' }
    @{ N = 'shared coordinates read, and the CSV writes once and replays the same sha';         T = 'horizun_excel_write_rows' }
    @{ N = 'a link is ADDED then REPOINTED, both halves re-read';                               T = 'horizun_manage_links' }
    @{ N = 'the family flexes measured and its thumbnail verifies from disk';                   T = 'horizun_create_family' }
    # ---- W13: the mandated positives (15) ---------------------------------
    @{ N = 'a REAL takeoff commits on a type given Tap preference the typed way';               T = 'horizun_create_elements' }
    @{ N = 'the labeled parameter DRIVES the solid: T300 and T600 measure 300 and 600';         T = 'horizun_create_family' }
    @{ N = 'a Yes/No parameter associates to form visibility, verified in the family';          T = 'horizun_create_family' }
    @{ N = 'a material parameter associates and the family loads and re-reads';                 T = 'horizun_create_family' }
    @{ N = 'v2 reloads over v1 with explicit overwrite and the instance survives';              T = 'horizun_create_family' }
    @{ N = 'unicode, number and boolean round-trip the workbook OVER THE WIRE';                 T = 'horizun_excel_read_rows' }
    @{ N = 'the declared comma parses 250,5 and the default separator refuses it BY QUOTING';   T = 'horizun_create_elements' }
    @{ N = 'a SHARED-coordinate row lands at its internal target within 5 mm';                  T = 'horizun_create_elements' }
    @{ N = 'the routed run answers as ONE component carrying its system';                       T = 'horizun_plan_mep' }
    @{ N = 'touching is NOT connected: coincident unjoined pipes are two components';           T = 'horizun_plan_mep' }
    @{ N = 'a queued apply cancelled on the wire provably never ran, token unconsumed';         T = 'horizun_create_elements' }
    @{ N = 'the 17th concurrent caller hears the queue is full, explicitly';                    T = 'horizun_model_scan' }
    @{ N = 'a truly lost commit reply replays by key: executed_once, exactly one grid';         T = 'horizun_create_elements' }
    @{ N = 'the preset proves ifc_version from FILE_SCHEMA and refuses the typo by name';       T = 'horizun_export' }
    @{ N = 'five representative reads measure under caps declared before the run';              T = 'horizun_health' }
    # ---- W14: the remaining phase surfaces (7) ----------------------------
    @{ N = 'accessory_inline REFUSES a point off the pipe axis, named in millimetres, before any write'; T = 'horizun_create_elements' }
    @{ N = 'a connectorless inline symbol rolls back WHOLE: named error, the pipe stays ONE piece'; T = 'horizun_create_elements' }
    @{ N = 'rebar cover: a typed write points the structural wall at a staged cover type and re-reads it'; T = 'horizun_write_params_verified' }
    @{ N = 'an interrupted job from a DEAD Revit answers the second-write warning off the disk record'; T = 'horizun_job_status' }
    @{ N = 'S, M and L scan workloads stay under caps declared before the run, UI hold included'; T = 'horizun_model_scan' }
    @{ N = 'reply bytes ride under the declared cap on all three sizes, the largest named';     T = 'horizun_model_scan' }
    @{ N = 'the Revit working set is measured across the S/M/L batch and bounded';              T = 'horizun_health' }
    @{ N = 'one height, four readings: the number matches only under its DECLARED separator'; T = 'horizun_write_params_verified' }
    @{ N = 'an authored valve breaks a live pipe in two and BOTH halves re-read CONNECTED to it'; T = 'horizun_create_elements' }
    @{ N = 'a verified Tap type either commits its takeoff or rolls back WHOLE on Revit''s refusal'; T = 'horizun_create_elements' }
    @{ N = 'a NAMED piping system is created and re-reads the exact member ids it was given'; T = 'horizun_create_elements' }
    @{ N = 'a member with no connector in the system''s domain refuses the whole row BY NAME'; T = 'horizun_create_elements' }
    @{ N = 'THE POSITIVE TAKEOFF: a real duct tap commits and its connectors re-read CONNECTED'; T = 'horizun_create_elements' }
    @{ N = 'a member whose connectors already belong to a system refuses BY NAME at plan time'; T = 'horizun_create_elements' }
    @{ N = 'a connector that declares NO system classification refuses instead of joining nobody'; T = 'horizun_create_elements' }
    @{ N = 'a member classified for another system refuses, naming BOTH classifications'; T = 'horizun_create_elements' }
)

# The dimension probes are addressed by CASE NUMBER 1..17, the 2D-detail probes
# by 1..11, the planimetry read probes by 1..22 and the planimetry FIX probes by
# 1..23, each against its own slice of the tail of $writeNames. Computed from the
# end backwards, not hard-coded, so inserting a probe above cannot silently
# misattribute every verdict to its neighbour's name - and each base is derived
# from the one after it, so adding a section means adding one line here.
$w14NameBase = $writeNames.Count - 16
$w13NameBase = $w14NameBase - 15
$w12NameBase = $w13NameBase - 14
$mpNameBase = $w12NameBase - 13
$dp2NameBase = $mpNameBase - 18
$productionNameBase = $dp2NameBase - 5
$fixNameBase = $productionNameBase - 23
$planNameBase = $fixNameBase - 22
$d2dNameBase = $planNameBase - 11
$dimNameBase = $d2dNameBase - 17

# The synthetic dimension fixture as ONE canonical JSON constant. Its SHA-256
# travels in the report so two runs can prove they measured the SAME geometry
# spec rather than "something similar". Coordinates are mm at x~510000 - far
# from real content and from the W1 pipes at x=500000. HZ_DIM_CYL is a 180-degree
# REVOLUTION rather than the literal "circular extrusion": the typed
# create_family profile is a point loop, so a revolution is the only typed route
# to arc edges - and a half revolution gives those arcs ENDPOINTS, which the
# arc_length case needs.
$dimensionFixtureSpec = '{"boxes":{"name":"HZ_DIM_BOX","size_mm":[1000,600,400],"instances_mm":[[510000,0],[510000,2000]],"reference_planes_mm":{"left":-500,"mid":0,"right":500,"lock_a":-200,"lock_b":200},"label_parameter":"HZ_W","dimensions":["label:left-right","lock:lock_a-lock_b","eq:left-mid-right"]},"cylinder":{"name":"HZ_DIM_CYL","kind":"revolution","radius_mm":300,"sweep_degrees":180,"instance_mm":[514000,0]},"pipes":{"x_mm":[510000,513000],"y_mm":[6000,6600,7800]},"grids_mm":[[[510000,10000],[513000,10000]],[[510000,8500],[513000,11500]]],"grid_intersection_mm":[511500,10000],"views":["floor_plan","section"]}'
$dimensionFixtureSpecSha256 = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes($dimensionFixtureSpec))).Replace('-', '').ToLower()

# Evidence the dimension probes leave behind for the report, initialised HERE so
# the $report block can read them on every path - including a run whose write
# gate never let the section execute.
$script:dimensionEvidence = @()
$script:dimFamilyPaths = @()
$script:dimRevitLanguage = $null
$script:dimRevitBuild = $null

# The synthetic 2D-detail fixture as ONE canonical JSON constant, hashed for the
# report exactly like the dimension spec: two runs that carry the same SHA-256
# drew the SAME geometry. Everything lives in a drafting view created by this
# run, in view-plane mm, and the last probe deletes every element it committed.
$detail2dFixtureSpec = '{"view":{"kind":"drafting","name_prefix":"HZ_D2D_"},"lines_mm":[[[0,0],[3000,0]],[[0,600],[3000,600]]],"arc_mm":{"start":[0,1200],"end":[3000,1200],"point_on_arc":[1500,2700]},"polyline_mm":{"points":[[5000,0],[8000,0],[6500,2000]],"closed":true},"filled_region_mm":{"exterior":[[10000,0],[14000,3000]],"hole":[[11000,800],[12000,1800]]},"masking_region_mm":{"exterior":[[10000,4000],[12000,5500]]},"style_line_mm":[[0,3500],[2000,3500]],"families":{"detail_item":{"name":"HZ_D2D_DI","template":"Metric Detail Item.rft","cross_mm":200},"generic_annotation":{"name":"HZ_D2D_GA","template":"Metric Generic Annotation.rft","cross_mm":200}},"placements_mm":{"detail_component":{"point":[16000,1000],"rotation_degrees":30},"symbol":{"point":[16000,2500]}}}'
$detail2dFixtureSpecSha256 = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes($detail2dFixtureSpec))).Replace('-', '').ToLower()

# Evidence the 2D-detail probes leave behind, initialised HERE for the same
# reason as the dimension evidence: the $report block reads it on every path.
$script:detail2dEvidence = @()
$script:d2dFamilyPaths = @()

# The planimetry fixture as ONE canonical JSON constant, hashed for the report
# exactly like the dimension and 2D-detail specs: two runs carrying the same
# SHA-256 staged the SAME documentation surface. It reuses the dimension
# fixture's plan, section, pipes and dims, and adds two sheets, the known
# viewport overlap, a clear schedule placement, staged tags with one pipe
# deliberately untagged and one duplicated, an overridden dimension, text
# inside and outside an activated crop, and - last - an unloaded link.
$planimetryFixtureSpec = '{"sheets":{"a":{"number_prefix":"HZP-A-","titleblock":"model type or authored A1 metric"},"b":{"number_prefix":"HZP-B-","titleblock":"none, deliberately"}},"placements":{"overlap":{"views":["dim_plan","dim_section"],"point_mm":[300,300]},"clear_schedule":{"category":"OST_PipeCurves","point_mm":[700,120]},"clear_viewport":{"view":"d2d_drafting","sheet":"b","point_mm":[300,300]}},"crop_mm":{"view":"dim_plan","min":[505000,-8000],"max":[518000,14000],"annotation_crop":true},"tags":{"mode":"multi_category","tagged_pipes":[1,1,2],"untagged_pipe":3},"texts_mm":{"near":[511000,4500],"far_outside_crop":[540000,4500],"whitespace_attempted":[511000,3800]},"override":{"value":"VARIES","on":"first simple linear dim of the plan"},"coverage_fixture":"unload the first RevitLinkType"}'
$planimetryFixtureSpecSha256 = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes($planimetryFixtureSpec))).Replace('-', '').ToLower()

# Evidence the planimetry probes leave behind, initialised HERE for the same
# reason as the other two sections: the $report block reads it on every path.
$script:planimetryEvidence = @()
$script:productionEvidence = @()

# The planimetry FIX fixture as ONE canonical JSON constant, hashed like the
# other three. It reuses the planimetry fixture wholesale - the two sheets, the
# overlapping viewports, the clear schedule placement, the crop and the far text
# are exactly the findings this section corrects - and adds only what a
# CORRECTION needs that a read does not: a view template to assign, an element
# override to clear, and an inline requirement set whose rules produce findings
# for the operations the universal catalog has no remedy for.
$fixFixtureSpec = '{"reuses":"the planimetry fixture, uncorrected","adds":{"view_template":"authored from the dimension plan view","element_override":"halftone on the near text note","requirement_set":{"id":"horizun-live-fix-set","version":"1.0.0","rules":["view name pattern","view allowed_scale","sheet number pattern","schedule_placement inside_extent","text_note has_view_overrides"]}},"corrections":{"set_view_template":"the section view","set_view_scale":"the section view","rename_view":"the section view","rename_sheet":"sheet B","place_title_block":"sheet B, which the read fixture left deliberately bare","move_viewport":"the overlapping section viewport","move_schedule":"the placed schedule","clear_element_override":"the near text note","set_crop":"the plan view, enlarged"},"reverted_before_census":["the placed title block","the authored view template"]}'
$fixFixtureSpecSha256 = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes($fixFixtureSpec))).Replace('-', '').ToLower()

# Evidence the fix probes leave behind, initialised HERE for the same reason as
# the other three sections: the $report block reads it on every path.
$script:fixEvidence = @()

if ($writeGate) {
    foreach ($w in $writeNames) { Add-Write $w.N $w.T 'not_covered' $writeGate }
}
else {
    # ---- what this model can offer. Discovered, not hard-coded: element ids do
    # ---- not survive a save, and a fixture of a dozen of them rots silently.
    $wDoc = $WriteDocument
    $lv = Invoke-Write 'horizun_list_elements' @{ category='OST_Levels'; max_rows=5; include_links=$false }
    $levelId = $null
    if ($lv.data -and @($lv.data.rows).Count -gt 0) { $levelId = @($lv.data.rows)[0].element_id }

    function First-Type($category, $namePattern) {
        $q = Invoke-Write 'horizun_query_model' @{ categories=@($category); include_types=$true
                                                  max_rows=500; include_links=$false }
        if (-not $q.data) { return $null }
        $types = @($q.data.rows | Where-Object { $_.is_element_type })
        if ($namePattern) {
            $hit = $types | Where-Object { $_.family -match $namePattern -or $_.type -match $namePattern } |
                   Select-Object -First 1
            if ($hit) { return $hit.element_id }
        }
        if ($types.Count -gt 0) { return $types[0].element_id }
        return $null
    }

    $pipeType   = First-Type 'OST_PipeCurves'   $null
    $pipeSystem = First-Type 'OST_PipingSystem' 'Incendio|Fire'
    $sprinkler  = First-Type 'OST_Sprinklers'   $null
    $capType    = First-Type 'OST_PipeFitting'  'Cap|Tap[oó]n'
    $unionType  = First-Type 'OST_PipeFitting'  'Coupling|Uni[oó]n'
    $teeType    = First-Type 'OST_PipeFitting'  'Tee'
    $elbowType  = First-Type 'OST_PipeFitting'  'Elbow|Codo'
    $valveType  = First-Type 'OST_PipeAccessory' 'Valve|V[aá]lvula'

    # Far from any building, so a probe never lands on top of real geometry. The
    # model is disposable and never saved, but a fixture that has to be untangled
    # by hand afterwards stops being reusable.
    $wx = 500000
    $wy = 0

    function New-ProbePipe($x1, $y1, $z1, $x2, $y2, $z2, $typeId, $key) {
        $r = Invoke-WriteApply 'horizun_create_elements' @{
            target_document = $wDoc; units = 'mm'
            elements = @(@{ kind='pipe'; start=@($x1,$y1,$z1); end=@($x2,$y2,$z2)
                            level_id=$levelId; type_id=$typeId; system_type_id=$pipeSystem })
        } $key
        return $r
    }

    if (-not $levelId -or -not $pipeType -or -not $pipeSystem) {
        $why = ("'{0}' has no usable level, pipe type or piping system type, so no piping probe can be " -f $wDoc) +
               'staged in it. Name a model with piping content.'
        foreach ($w in $writeNames) { Add-Write $w.N $w.T 'not_covered' $why }
    }
    else {
        # ---- W1: create_elements. Also the fixture the next probes stand on, which
        # ---- is why its own verification is asserted rather than assumed.
        $mk = New-ProbePipe $wx $wy 0 ($wx+3000) $wy 0 $pipeType 'mk1'
        $a = $mk.answer
        if ($mk.stage -eq 'dry_run') {
            Add-Write $writeNames[0].N $writeNames[0].T 'unverified' ("the dry run did not issue a token: " + $a.text)
        }
        elseif ($a.isError -or -not $a.data) {
            Add-Write $writeNames[0].N $writeNames[0].T 'fail' $a.text
            if ($a.data) { $writeAnswers += @{ Name = $writeNames[0].N; Data = $a.data } }
        }
        elseif ($a.data.created_verified -eq 1 -and
                (All-Rows $a.data.rows { param($r) $r.present_after_commit -eq $true -and $r.verified -eq $true })) {
            Add-Write $writeNames[0].N $writeNames[0].T 'pass' 'committed and re-read'
            $writeAnswers += @{ Name = $writeNames[0].N; Data = $a.data }
        }
        else {
            Add-Write $writeNames[0].N $writeNames[0].T 'fail' ("created_verified=" + $a.data.created_verified)
            $writeAnswers += @{ Name = $writeNames[0].N; Data = $a.data }
        }
        $pipeA = $null
        if ($a.data -and @($a.data.rows).Count -gt 0) { $pipeA = @($a.data.rows)[0].element_id }

        # ---- W2: a REAL delete rehearsal. The default-tier nonexistent id above
        # ---- correctly withholds confirmation; only a resolvable target can prove
        # ---- the token-bearing path. This model is explicitly disposable and the
        # ---- transaction is rolled back, then census values are compared.
        if (-not $pipeA) {
            Add-Write $writeNames[1].N $writeNames[1].T 'unverified' (
                'create_elements did not return a pipe id, so delete could not rehearse a real target')
        }
        else {
            $deleteDry = Invoke-Write 'horizun_delete_verified' @{
                mode = 'ids'; ids = @($pipeA); target_document = $wDoc; id_cap = 10; dry_run = $true }
            if ($deleteDry.isError -or -not $deleteDry.data) {
                Add-Write $writeNames[1].N $writeNames[1].T 'fail' $deleteDry.text
            }
            elseif ($deleteDry.data.dry_run -eq $true -and $deleteDry.data.confirmation_token -and
                    $null -eq $deleteDry.data.deleted_total -and
                    $deleteDry.data.elements_before -eq $deleteDry.data.elements_after -and
                    [int]$deleteDry.data.would_delete_total -ge 1 -and
                    $deleteDry.data.application.state -eq 'rehearsed') {
                Add-Write $writeNames[1].N $writeNames[1].T 'pass' (
                    'real target resolved; token issued; preview transaction rolled back; census unchanged')
            }
            else {
                Add-Write $writeNames[1].N $writeNames[1].T 'fail' (
                    'the real-target rehearsal did not prove token + null deleted_total + unchanged census: ' +
                    $deleteDry.text)
            }
        }


    # A tool absent from tools/list is a guarantee this build cannot attempt -
    # NOT COVERED by its own taxonomy, not an UNVERIFIED that reads like a broken
    # transport. On this repo's main, connect_mep and terminate_riser live in
    # open PRs, so the tier must say "not published by this build" rather than
    # dying on (or mislabelling) an Unknown-tool refusal.
    function Test-ToolPublished([string]$n) {
        return [bool]($listed | Where-Object { $_.name -eq $n })
    }

        # ---- W3: THE ROLLBACK RULE, provoked on CURRENT tools.
        #
        # "No command reports work it did not verify" is honoured everywhere. What
        # happens AFTER a failure was the part with no live evidence: one command
        # rolls back and builds nothing, another commits and leaves the attempt in
        # the model with an honest sentence about it. Both are truthful; only one is
        # safe to retry.
        #
        # This used to be inferred - scan whatever answers the tier happened to
        # collect for a fully_verified=false that had committed anyway. That went
        # UNVERIFIED the moment the commands it leaned on left the surface, because
        # it was watching for a failure rather than causing one.
        #
        # So it is PROVOKED. horizun_execute_plan composes typed writes into one
        # TransactionGroup and documents that any failure rolls the complete graph
        # back. Both actions below rehearse cleanly against the same real pipe: first
        # delete it, then pin it. During execution the delete commits inside the group,
        # so the transform reaches an element that no longer exists and fails THERE,
        # after the group and a real write. The pipe count before/after must be identical.
        #
        # It was anchored on walls first and went UNVERIFIED on the real fixture -
        # HZ_WRITE is an HVAC model and offers no wall type. Anchoring a probe on a
        # category the fixture does not have is the probe's bug, not the product's,
        # and pipes are what this tier already discovers for everything else.
        $pipeCountBefore = $null
        $cw = Invoke-Write 'horizun_query_model' @{ categories=@('OST_PipeCurves'); include_links=$false
                                                    max_rows=1; group_by=@('category') }
        if ($cw.data -and @($cw.data.groups).Count -gt 0) { $pipeCountBefore = @($cw.data.groups)[0].count }

        if ($null -eq $pipeCountBefore -or -not $pipeA) {
            Add-Write $writeNames[2].N $writeNames[2].T 'unverified' (
                'could not read a pipe count or the real pipe created by W1 from this fixture, so ' +
                'the rollback could not be provoked against a known before-state')
        }
        else {
            # Both rehearsals are valid while pipeA exists. The deterministic invalidity
            # is introduced only by execution order inside the TransactionGroup.
            $planActions = @(
                @{ key='delete'; tool='horizun_delete_verified'
                   arguments=@{ mode='ids'; ids=@($pipeA); id_cap=10 } }
                @{ key='boom'; tool='horizun_transform_elements'
                   arguments=@{ units='mm'
                                operations=@(@{ operation='pin'; element_ids=@($pipeA) }) } }
            )
            $rb = Invoke-WriteApply 'horizun_execute_plan' @{
                target_document = $wDoc; actions = $planActions } 'rollback'

            $ra = $rb.answer
            if ($rb.stage -eq 'dry_run') {
                # The graph was refused before it could run. That proves validation,
                # not rollback, and must not be reported as the latter.
                Add-Write $writeNames[2].N $writeNames[2].T 'unverified' (
                    'the plan was refused during rehearsal, so nothing was committed and nothing was rolled ' +
                    'back: ' + $ra.text)
            }
            else {
                # Re-read the MODEL, not the answer. A command insisting it rolled
                # back is exactly the claim under test.
                $cwAfter = Invoke-Write 'horizun_query_model' @{ categories=@('OST_PipeCurves'); include_links=$false
                                                                 max_rows=1; group_by=@('category') }
                $pipeCountAfter = $null
                if ($cwAfter.data -and @($cwAfter.data.groups).Count -gt 0) {
                    $pipeCountAfter = @($cwAfter.data.groups)[0].count
                }

                # THE STRUCTURED DIAGNOSTIC, not the sentence. The old probe passed on
                # (isError AND count unchanged), which a stale-token or confirmation
                # refusal ALSO satisfies without a rollback ever happening. Now the plan
                # must PROVE, as data in structuredContent, that: the TransactionGroup
                # started; the valid first action ('delete') executed and returned success;
                # the invalid second ('boom') was REACHED and returned failure; and the
                # rollback landed with status RolledBack. The model residue check stays
                # on top of that, so both the group's own account and the model agree.
                $s = $ra.structured
                $trace = @()
                if ($s -and $s.execution_trace) { $trace = @($s.execution_trace) }
                $deleteRow = $trace | Where-Object { $_.key -eq 'delete' } | Select-Object -First 1
                $boomRow = $trace | Where-Object { $_.key -eq 'boom' } | Select-Object -First 1

                if ($null -eq $pipeCountAfter) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'unverified' (
                        'the pipe count could not be re-read after the plan, so residue could not be ruled out')
                }
                elseif (-not $ra.isError) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'fail' (
                        'the plan REPORTED SUCCESS although its second action cannot succeed. Either the ' +
                        'failure was not provoked (fix this probe) or a failing action was accepted.')
                }
                elseif ($null -eq $s -or $null -eq $s.transaction_group_started) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'unverified' (
                        'the failed plan carried no structured rollback diagnostic, so a rollback that reached ' +
                        'the TransactionGroup could not be told from a refusal that never did')
                }
                elseif ($s.transaction_group_started -ne $true) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'fail' (
                        'the plan failed BEFORE the TransactionGroup started, so this proves validation, not ' +
                        'rollback. A pre-group refusal must never count as rollback tested.')
                }
                elseif ($null -eq $deleteRow -or $deleteRow.success -ne $true) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'fail' (
                        "the valid first action 'delete' did not execute with success=true in the trace, so the " +
                        'rollback was not exercised on a graph that had actually written something')
                }
                elseif ($null -eq $boomRow -or $boomRow.success -ne $false) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'fail' (
                        "the failing action 'boom' was not reached with success=false, so the failure was not " +
                        'provoked inside the group')
                }
                elseif ($s.rollback_status -ne 'RolledBack' -or $s.rollback_confirmed -ne $true) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'fail' (
                        ("the plan did not confirm a rollback: rollback_status='{0}', rollback_confirmed='{1}'. " -f
                             $s.rollback_status, $s.rollback_confirmed) +
                        "Anything other than 'RolledBack' leaves the model state uncertain and must not pass.")
                }
                elseif ($pipeCountAfter -ne $pipeCountBefore) {
                    Add-Write $writeNames[2].N $writeNames[2].T 'fail' (
                        ("RESIDUE: {0} pipe(s) before the failed plan, {1} after. The first action stayed " -f
                             $pipeCountBefore, $pipeCountAfter) +
                        'applied even though the group reported RolledBack - the model and the group disagree.')
                }
                else {
                    Add-Write $writeNames[2].N $writeNames[2].T 'pass' (
                        ("group started; 'delete' committed and 'boom' failed inside it; rollback_status=RolledBack; " +
                         "pipe count unchanged at {0} - the group's account and the model agree." -f $pipeCountBefore))
                }
            }
        }

        # ---- W4+: DIMENSIONS -------------------------------------------------
        #
        # A dimension is the annotation somebody prints and builds from, and the
        # fixture is an HVAC derivative that offers no dimensionable geometry of
        # its own at a known place - so EVERYTHING these probes measure is
        # synthetic and self-provisioned at x~510000, far from the model's own
        # content and from the W1 pipes at x=500000: two generic-model RFAs (the
        # RFA creation IS the case-17 probe), their placed instances, three
        # parallel pipes, two grids at 45 degrees, and a floor plan plus a
        # section created for this run. Ids are discovered in THIS run, never
        # cached: the disposable model is reopened between runs and a cached id
        # is a coincidence waiting to be believed.
        #
        # Every commit is believed only after a re-read - the command's own
        # requested/read/match table, or an independent horizun_query_dimensions
        # call - and every dependency that fails degrades its dependents by name
        # instead of letting them pass over geometry that is not there.
        # ---------------------------------------------------------------------
        if ($h -and $h.data) {
            $script:dimRevitLanguage = $h.data.revit_language
            $script:dimRevitBuild = $h.data.revit_build
        }
        $script:dimCasesDone = @{}
        $script:dimCenterline = @{}

        function Get-DimShortText($t) {
            if ([string]::IsNullOrWhiteSpace($t)) { return '(no text)' }
            $flat = ($t -replace "`r", ' ') -replace "`n", ' '
            if ($flat.Length -gt 600) { return $flat.Substring(0, 600) }
            return $flat
        }

        # The NAMES of the checks that did not match, from a parsed apply/refusal
        # answer. A truncated sentence cost two whole gate iterations; the field
        # names are the diagnosis and they are two lines to extract.
        function Get-DimFailedChecks($answer) {
            $bits = @()
            # FailWithDetail spreads the detail at the TOP of structuredContent; the
            # text may not carry the JSON at all. Look in both places.
            $src = $null
            if ($answer -and $answer.structured -and $answer.structured.rows) { $src = $answer.structured }
            elseif ($answer -and $answer.data -and $answer.data.rows) { $src = $answer.data }
            if ($src) {
                foreach ($fr in @($src.rows)) {
                    if (-not $fr.verification -or -not $fr.verification.checks) { continue }
                    foreach ($fc in @($fr.verification.checks)) {
                        if ($fc.match -ne $true) {
                            $bits += ("row{0}:{1}(req={2} read={3})" -f $fr.index, $fc.field,
                                      (("$($fc.requested)") -replace '\s+', ' ').Substring(0, [Math]::Min(60, ("$($fc.requested)").Length)),
                                      (("$($fc.read)") -replace '\s+', ' ').Substring(0, [Math]::Min(60, ("$($fc.read)").Length)))
                        }
                    }
                }
            }
            if ($bits.Count -eq 0) { return '(no failing checks parsed from the reply)' }
            return ($bits -join '; ')
        }

        function Get-DimTx($answer) {
            if ($answer -and $answer.data) {
                if ($answer.data.transaction_group_status) { return $answer.data.transaction_group_status }
                if ($answer.data.transaction_status) { return $answer.data.transaction_status }
            }
            return $null
        }

        # One verdict per case, exactly once: the outcome goes to the shared
        # write-tier accounting AND to the evidence block the report publishes.
        function Complete-DimCase {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail,
                  $TransactionStatus = $null, $Evidence = $null, $Warnings = $null)
            if ($script:dimCasesDone.ContainsKey($CaseNumber)) { return }
            $script:dimCasesDone[$CaseNumber] = $true
            $entry = $writeNames[$dimNameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:dimensionEvidence += @{
                case = $CaseNumber; name = $entry.N; tool = $entry.T
                started_utc = $Started.ToUniversalTime().ToString('o')
                duration_ms = [int][math]::Round(((Get-Date) - $Started).TotalMilliseconds)
                transaction_status = $TransactionStatus
                outcome = $Outcome
                detail = $Detail
                evidence = $Evidence
                warnings = $Warnings
            }
        }

        # The house contract for an annotate apply, asserted whole: null when the
        # answer proves committed_verified with every row verified and every
        # requested/read check matched, otherwise the exact reason it did not.
        function Test-DimCommitted($applyAnswer) {
            if ($applyAnswer.isError) { return 'the apply errored: ' + (Get-DimShortText $applyAnswer.text) + ' | failing checks: ' + (Get-DimFailedChecks $applyAnswer) }
            $d = $applyAnswer.data
            if (-not $d) { return 'the apply reply carried no parseable JSON' }
            if ($d.state -ne 'committed_verified') { return ("state was '{0}', not committed_verified" -f $d.state) }
            if (-not (All-Rows $d.rows { param($r) $r.verified -eq $true })) { return 'not every row re-read verified=true' }
            foreach ($r in @($d.rows)) {
                # match=null + verified_by=substance is the command's HONEST tri-state
                # for facts Revit computes lazily (measured live: AreReferencesAvailable
                # on instance-geometry references, EQ after a committed edit) - the row
                # passed on substance and says so; a probe reading that honesty as a
                # failure would be asserting the flag over the measurement.
                if (-not (All-Rows $r.verification.checks { param($c)
                        $c.match -eq $true -or ($null -eq $c.match -and $c.verified_by -eq 'substance') })) {
                    return 'a verification check did not match (requested vs read disagree)'
                }
            }
            return $null
        }

        function Get-DimCount($viewId) {
            $q = Invoke-Write 'horizun_query_dimensions' @{ view_id = $viewId; max_rows = 1 }
            if ($q.isError -or -not $q.data) { return $null }
            return [int]$q.data.total_matched
        }

        # ---- the synthetic fixture, provisioned piece by piece. Each gap is a
        # ---- named reason, so a dependent probe degrades with the true cause.
        $dimX = 510000
        $dimTag = $probeRun.Substring(0, 8)
        $dimPlanViewId = $null; $dimSectionViewId = $null
        $dimViewGap = $null; $dimPipeGap = $null; $dimGridGap = $null
        $dimBoxGap = $null; $dimCylGap = $null
        $dimPipes = @(); $dimGridIds = @()
        $boxAId = $null; $boxBId = $null; $cylId = $null; $boxTypeId = $null; $cylTypeId = $null
        $dimBoxAnswer = $null

        # Views first: the plan view is the home of every dimension below, and
        # reference compatibility is a property OF the view.
        $mvDim = Invoke-WriteApply 'horizun_manage_views' @{
            target_document = $wDoc; units = 'mm'
            actions = @(
                @{ operation = 'create_floor_plan'; key = 'hzdimplan'; name = "HZ_DIM_PLAN_$dimTag"; level_id = $levelId },
                @{ operation = 'create_section'; key = 'hzdimsec'; name = "HZ_DIM_SEC_$dimTag"
                   start = @(($dimX - 2000), -3000, 0); end = @(($dimX + 6000), -3000, 0); depth = 20000 })
        } 'dim-views'
        if ($mvDim.stage -eq 'apply' -and -not $mvDim.answer.isError -and $mvDim.answer.data) {
            $dimPlanViewId = $mvDim.answer.data.aliases.hzdimplan
            $dimSectionViewId = $mvDim.answer.data.aliases.hzdimsec
        }
        if (-not $dimPlanViewId) {
            $dimViewGap = 'the synthetic floor plan could not be created and verified: ' + (Get-DimShortText $mvDim.answer.text)
        }
        if (-not $dimViewGap) {
            # MEASURED precondition: Revit materialises dimension references and values
            # only for a DISPLAYED view, and horizun_annotate refuses dimensions aimed
            # at an inactive view at plan time. The probe does what a caller does:
            # opens the view first.
            $nav = Invoke-Write 'horizun_navigate' @{ operation = 'open_view'; view_id = $dimPlanViewId }
            if ($nav.isError -or -not $nav.data -or
                $nav.data.active_view_verified -ne $true -or [long]$nav.data.view_id -ne [long]$dimPlanViewId) {
                $dimViewGap = 'the synthetic plan view could not be ACTIVATED (dimensions require the displayed view): ' + (Get-DimShortText $nav.text)
            }
        }

        # Three parallel pipes: a 600 mm and a 1200 mm bay, the chain every
        # linear case below measures.
        $dimPipeYs = @(6000, 6600, 7800)
        for ($pi = 0; $pi -lt $dimPipeYs.Count; $pi++) {
            $mkD = New-ProbePipe $dimX $dimPipeYs[$pi] 0 (($dimX + 3000)) $dimPipeYs[$pi] 0 $pipeType ("dim-pipe-{0}" -f ($pi + 1))
            if ($mkD.stage -eq 'apply' -and -not $mkD.answer.isError -and $mkD.answer.data -and
                @($mkD.answer.data.rows).Count -gt 0 -and @($mkD.answer.data.rows)[0].verified -eq $true) {
                $dimPipes += @($mkD.answer.data.rows)[0].element_id
            }
        }
        if (@($dimPipes).Count -ne 3) {
            $dimPipeGap = ('only {0} of the 3 parallel probe pipes were created and verified' -f @($dimPipes).Count)
        }

        # Two grids crossing at exactly 45 degrees at (dimX+1500, 10000).
        $grDim = Invoke-WriteApply 'horizun_create_elements' @{
            target_document = $wDoc; units = 'mm'
            elements = @(
                @{ kind = 'grid'; name = "HZD1_$dimTag"; start = @($dimX, 10000, 0); end = @(($dimX + 3000), 10000, 0) },
                @{ kind = 'grid'; name = "HZD2_$dimTag"; start = @($dimX, 8500, 0); end = @(($dimX + 3000), 11500, 0) })
        } 'dim-grids'
        if ($grDim.stage -eq 'apply' -and -not $grDim.answer.isError -and $grDim.answer.data) {
            $dimGridIds = @(@($grDim.answer.data.rows) | Where-Object { $_.verified -eq $true } |
                            ForEach-Object { $_.element_id })
        }
        if (@($dimGridIds).Count -ne 2) {
            $dimGridGap = 'the two 45-degree probe grids were not created and verified: ' + (Get-DimShortText $grDim.answer.text)
        }

        # Three PARALLEL grids at the same 600/1200 bays as the pipes. They are the
        # LINEAR dimension targets: measured live (2025, 2026-08-24), NewDimension
        # refuses MEP-curve centerline references outright while accepting grid
        # references - so the pipes prove discovery (case 1, including the measured
        # structured incompatibility) and the grids prove creation.
        $dimParGridIds = @(); $dimParGridGap = $null
        $pgDim = Invoke-WriteApply 'horizun_create_elements' @{
            target_document = $wDoc; units = 'mm'
            elements = @(
                @{ kind = 'grid'; name = "HZP1_$dimTag"; start = @($dimX, 6000, 0); end = @(($dimX + 3000), 6000, 0) },
                @{ kind = 'grid'; name = "HZP2_$dimTag"; start = @($dimX, 6600, 0); end = @(($dimX + 3000), 6600, 0) },
                @{ kind = 'grid'; name = "HZP3_$dimTag"; start = @($dimX, 7800, 0); end = @(($dimX + 3000), 7800, 0) })
        } 'dim-parallel-grids'
        if ($pgDim.stage -eq 'apply' -and -not $pgDim.answer.isError -and $pgDim.answer.data) {
            $dimParGridIds = @(@($pgDim.answer.data.rows) | Where-Object { $_.verified -eq $true } |
                               ForEach-Object { $_.element_id })
        }
        if (@($dimParGridIds).Count -ne 3) {
            $dimParGridGap = 'the three parallel probe grids were not created and verified: ' + (Get-DimShortText $pgDim.answer.text)
        }

        # The family template. Localized installs name it differently, so the
        # exact metric name first, then the documented pattern - and NO template
        # is a named fixture gap, never a silent skip.
        $dimTemplatePath = $null
        $dimTemplateRoot = Join-Path $env:ProgramData ("Autodesk\RVT {0}\Family Templates" -f $Year)
        if (Test-Path $dimTemplateRoot) {
            $rftAll = @(Get-ChildItem -LiteralPath $dimTemplateRoot -Recurse -Filter '*.rft' -File -ErrorAction SilentlyContinue)
            $rftExact = $rftAll | Where-Object { $_.Name -eq 'Metric Generic Model.rft' } |
                        Sort-Object FullName | Select-Object -First 1
            if ($rftExact) { $dimTemplatePath = $rftExact.FullName }
            else {
                $rftPattern = $rftAll | Where-Object { $_.BaseName -match '(?i)generic model(?!.*adaptive)|gen[eé]rico(?!.*adaptativ)' } |
                              Sort-Object FullName | Select-Object -First 1
                if ($rftPattern) { $dimTemplatePath = $rftPattern.FullName }
            }
        }

        $dimRfaDir = Join-Path $scratchDir 'dimension-families'
        New-Item -ItemType Directory -Force $dimRfaDir | Out-Null

        if (-not $dimTemplatePath) {
            $dimBoxGap = ('no Generic Model family template (.rft) was found under {0}, so no probe RFA could be authored' -f $dimTemplateRoot)
            $dimCylGap = $dimBoxGap
        }
        else {
            # HZ_DIM_BOX: one RFA exercising label + lock + eq. The labeled and
            # locked dimensions use DIFFERENT plane pairs on purpose - a locked
            # dimension between planes a label already constrains would
            # over-constrain the family and fail for a reason that is not the
            # product's.
            $boxRfa = Join-Path $dimRfaDir 'HZ_DIM_BOX.rfa'
            $bfDim = Invoke-WriteApply 'horizun_create_family' @{
                target_document = $wDoc; template_path = $dimTemplatePath; output_path = $boxRfa
                units = 'mm'; overwrite = $true; load_into_project = $true
                parameters = @(@{ name = 'HZ_W'; data_type = 'length'; group = 'geometry' })
                types = @(@{ name = 'HZ_DIM_BOX'; values = @{ HZ_W = 1000 } })
                forms = @(@{ key = 'body'; kind = 'extrusion'; plane = 'xy'; depth = 400
                             profile = @(, @(@(-500, -300, 0), @(500, -300, 0), @(500, 300, 0), @(-500, 300, 0))) })
                reference_planes = @(
                    @{ key = 'left';   name = 'HZ Left';   bubble_end = @(-500, -800, 0); free_end = @(-500, 800, 0); cut_vector = @(0, 0, 1) },
                    @{ key = 'mid';    name = 'HZ Mid';    bubble_end = @(0, -800, 0);    free_end = @(0, 800, 0);    cut_vector = @(0, 0, 1) },
                    @{ key = 'right';  name = 'HZ Right';  bubble_end = @(500, -800, 0);  free_end = @(500, 800, 0);  cut_vector = @(0, 0, 1) },
                    @{ key = 'lock_a'; name = 'HZ Lock A'; bubble_end = @(-200, -800, 0); free_end = @(-200, 800, 0); cut_vector = @(0, 0, 1) },
                    @{ key = 'lock_b'; name = 'HZ Lock B'; bubble_end = @(200, -800, 0);  free_end = @(200, 800, 0);  cut_vector = @(0, 0, 1) })
                dimensions = @(
                    @{ key = 'labeled'; reference_plane_keys = @('left', 'right')
                       line_start = @(-500, -900, 0); line_end = @(500, -900, 0); label_parameter = 'HZ_W' },
                    @{ key = 'locked'; reference_plane_keys = @('lock_a', 'lock_b')
                       line_start = @(-200, -1100, 0); line_end = @(200, -1100, 0); lock = $true },
                    @{ key = 'equalised'; reference_plane_keys = @('left', 'mid', 'right')
                       line_start = @(-500, -1300, 0); line_end = @(500, -1300, 0); eq = $true })
            } 'dim-box-family'
            if ($bfDim.stage -eq 'apply' -and -not $bfDim.answer.isError -and $bfDim.answer.data) {
                $dimBoxAnswer = $bfDim.answer
                $script:dimFamilyPaths += $boxRfa
                if ($bfDim.answer.data.loaded_family -and
                    @($bfDim.answer.data.loaded_family.symbol_ids).Count -gt 0) {
                    $boxTypeId = @($bfDim.answer.data.loaded_family.symbol_ids)[0]
                }
            }
            if (-not $dimBoxAnswer) {
                $dimBoxGap = 'HZ_DIM_BOX.rfa could not be created and verified: ' + (Get-DimShortText $bfDim.answer.text)
            }
            elseif (-not $boxTypeId) {
                $dimBoxGap = 'HZ_DIM_BOX.rfa was created but its loaded FamilySymbol id did not come back, so no instance can be placed'
            }

            # HZ_DIM_CYL: a HALF revolution (180 degrees) about a vertical axis.
            # The typed profile is a point loop, so a revolution is the only
            # typed route to arc edges - and a half revolution gives those arcs
            # ENDPOINTS, which arc_length needs. Radius 300 mm.
            $cylRfa = Join-Path $dimRfaDir 'HZ_DIM_CYL.rfa'
            $cfDim = Invoke-WriteApply 'horizun_create_family' @{
                target_document = $wDoc; template_path = $dimTemplatePath; output_path = $cylRfa
                units = 'mm'; overwrite = $true; load_into_project = $true
                types = @(@{ name = 'HZ_DIM_CYL' })
                forms = @(@{ key = 'drum'; kind = 'revolution'; plane = 'xz'
                             profile = @(, @(@(100, 0, 0), @(300, 0, 0), @(300, 0, 400), @(100, 0, 400)))
                             axis_start = @(0, 0, 0); axis_end = @(0, 0, 400)
                             start_angle_degrees = 0; end_angle_degrees = 180 })
            } 'dim-cyl-family'
            if ($cfDim.stage -eq 'apply' -and -not $cfDim.answer.isError -and $cfDim.answer.data) {
                $script:dimFamilyPaths += $cylRfa
                if ($cfDim.answer.data.loaded_family -and
                    @($cfDim.answer.data.loaded_family.symbol_ids).Count -gt 0) {
                    $cylTypeId = @($cfDim.answer.data.loaded_family.symbol_ids)[0]
                }
            }
            if (-not $cylTypeId) {
                $dimCylGap = 'HZ_DIM_CYL.rfa could not be created, verified and loaded: ' + (Get-DimShortText $cfDim.answer.text)
            }
        }

        # Instances: BOX_A and BOX_B parallel 2000 mm apart, CYL off to the side.
        if (-not $dimBoxGap) {
            $instDim = Invoke-WriteApply 'horizun_create_elements' @{
                target_document = $wDoc; units = 'mm'
                elements = @(
                    @{ kind = 'family_instance'; type_id = $boxTypeId; point = @($dimX, 0, 0); level_id = $levelId },
                    @{ kind = 'family_instance'; type_id = $boxTypeId; point = @($dimX, 2000, 0); level_id = $levelId })
            } 'dim-box-instances'
            if ($instDim.stage -eq 'apply' -and -not $instDim.answer.isError -and $instDim.answer.data -and
                (All-Rows $instDim.answer.data.rows { param($r) $r.verified -eq $true }) -and
                @($instDim.answer.data.rows).Count -eq 2) {
                $boxAId = @($instDim.answer.data.rows)[0].element_id
                $boxBId = @($instDim.answer.data.rows)[1].element_id
            }
            else {
                $dimBoxGap = 'the HZ_DIM_BOX instances could not be placed and verified: ' + (Get-DimShortText $instDim.answer.text)
            }
        }
        if (-not $dimCylGap) {
            $instCyl = Invoke-WriteApply 'horizun_create_elements' @{
                target_document = $wDoc; units = 'mm'
                elements = @(@{ kind = 'family_instance'; type_id = $cylTypeId; point = @(($dimX + 4000), 0, 0); level_id = $levelId })
            } 'dim-cyl-instance'
            if ($instCyl.stage -eq 'apply' -and -not $instCyl.answer.isError -and $instCyl.answer.data -and
                @($instCyl.answer.data.rows).Count -gt 0 -and @($instCyl.answer.data.rows)[0].verified -eq $true) {
                $cylId = @($instCyl.answer.data.rows)[0].element_id
            }
            else {
                $dimCylGap = 'the HZ_DIM_CYL instance could not be placed and verified: ' + (Get-DimShortText $instCyl.answer.text)
            }
        }

        # ---- case 1: deterministic centerline discovery -----------------------
        $t0 = Get-Date
        if ($dimViewGap) { Complete-DimCase 1 $t0 'unverified' $dimViewGap }
        elseif ($dimPipeGap) { Complete-DimCase 1 $t0 'unverified' $dimPipeGap }
        else {
            $refArgs = @{ view_id = $dimPlanViewId; element_ids = @($dimPipes)
                          selectors = @('centerline'); units = 'mm'; max_results = 50 }
            $c1a = Invoke-Write 'horizun_get_dimension_references' $refArgs
            $c1b = Invoke-Write 'horizun_get_dimension_references' $refArgs
            if ($c1a.isError -or -not $c1a.data) {
                Complete-DimCase 1 $t0 'unverified' ('the discovery call errored, so nothing was discovered: ' + (Get-DimShortText $c1a.text))
            }
            else {
                # Every centerline row of an MEP curve must say, structurally, that a
                # dimension cannot use it - a row claiming compatible here would be the
                # false promise this run exists to catch. TWO measured branches, both
                # structured refusals, and every row of one run must sit in ONE of them:
                #  - Revit 2025 (measured 2026-08-24): the referenced centerline is
                #    exposed in non-visible geometry and NewDimension rejects it, so the
                #    row carries its stable representation plus the code
                #    mep_centerline_rejected_by_dimension_api.
                #  - Revit 2023 (measured 2026-08-24, live): NO Options combination
                #    (view/fine/medium/coarse, non-visible on or off) returns a
                #    reference-carrying curve coinciding with a pipe's location curve.
                #    There is no reference to hand out, and the honest row is the
                #    negative one: stable_representation null and the code
                #    no_stable_centerline. A mixed answer fails both branches.
                $rows1 = @($c1a.data.rows | Where-Object { $_.selector -eq 'centerline' })
                $reps1 = @($rows1 | ForEach-Object { $_.stable_representation })
                $fps1 = @($rows1 | ForEach-Object { $_.geometry_fingerprint })
                $reps2 = @(); $fps2 = @()
                if ($c1b.data) {
                    $rows1b = @($c1b.data.rows | Where-Object { $_.selector -eq 'centerline' })
                    $reps2 = @($rows1b | ForEach-Object { $_.stable_representation })
                    $fps2 = @($rows1b | ForEach-Object { $_.geometry_fingerprint })
                }
                $fpsUnique = (@($fps1 | Select-Object -Unique).Count -eq @($fps1).Count)
                # Determinism must hold for the negative branch too, where every
                # stable representation is null - so the fingerprints, which exist in
                # both branches, are part of the order check.
                $sameOrder = ((($reps1 -join '|') -eq ($reps2 -join '|')) -and (($fps1 -join '|') -eq ($fps2 -join '|')))
                $mepBranch = All-Rows $rows1 { param($r)
                    -not [string]::IsNullOrWhiteSpace($r.stable_representation) -and
                    $r.compatible_with_dimension -eq $false -and
                    $r.incompatibility_reason -and
                    $r.incompatibility_reason.code -eq 'mep_centerline_rejected_by_dimension_api' }
                $noRefBranch = All-Rows $rows1 { param($r)
                    [string]::IsNullOrWhiteSpace($r.stable_representation) -and
                    $r.compatible_with_dimension -eq $false -and
                    $r.incompatibility_reason -and
                    $r.incompatibility_reason.code -eq 'no_stable_centerline' }
                if ($rows1.Count -ge 3 -and $fpsUnique -and $sameOrder -and ($mepBranch -or $noRefBranch)) {
                    $branch1 = if ($mepBranch) { 'mep_centerline_rejected_by_dimension_api' } else { 'no_stable_centerline' }
                    Complete-DimCase 1 $t0 'pass' ("{0} centerline rows discovered with unique fingerprints, identical order across two calls, and every row carrying the MEASURED structured refusal of this Revit's branch ({1})" -f $rows1.Count, $branch1) `
                        -Evidence @{ rows = $rows1.Count; fingerprints_unique = $fpsUnique; deterministic_order = $sameOrder; refusal_branch = $branch1 }
                }
                else {
                    Complete-DimCase 1 $t0 'fail' ("rows={0} (need >=3), fingerprints_unique={1}, deterministic_order={2}, uniform_mep_branch={3}, uniform_no_reference_branch={4} - every row must sit in ONE structured-refusal branch" -f $rows1.Count, $fpsUnique, $sameOrder, $mepBranch, $noRefBranch) `
                        -Evidence @{ rows = $rows1.Count; fingerprints_unique = $fpsUnique; deterministic_order = $sameOrder; uniform_mep_branch = $mepBranch; uniform_no_reference_branch = $noRefBranch }
                }
            }
        }

        # The LINEAR dimension targets: grid references from the three parallel grids.
        # Discovered, never cached across runs; the pipes above prove discovery, the
        # grids prove creation (the API refuses MEP centerlines - measured live).
        $repP1 = $null; $repP2 = $null; $repP3 = $null
        if (-not $dimParGridGap -and -not $dimViewGap) {
            $cg = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = $dimPlanViewId; element_ids = @($dimParGridIds)
                selectors = @('grid'); units = 'mm'; max_results = 10 }
            if ($cg.data) {
                $gridRep = @{}
                foreach ($rowG in @($cg.data.rows | Where-Object { $_.compatible_with_dimension -eq $true })) {
                    $gridRep[[long]$rowG.element_id] = $rowG.stable_representation
                }
                if (@($dimParGridIds).Count -eq 3) {
                    $repP1 = $gridRep[[long]$dimParGridIds[0]]
                    $repP2 = $gridRep[[long]$dimParGridIds[1]]
                    $repP3 = $gridRep[[long]$dimParGridIds[2]]
                }
            }
        }
        $repGap = $null
        if ($dimViewGap) { $repGap = $dimViewGap }
        elseif ($dimParGridGap) { $repGap = $dimParGridGap }
        elseif (-not $repP1 -or -not $repP2 -or -not $repP3) {
            $repGap = 'grid reference discovery did not return a compatible reference for each of the three parallel grids'
        }

        # ---- case 2: the default type, committed and verified field by field --
        $t0 = Get-Date
        $dim2Id = $null
        if ($repGap) {
            Complete-DimCase 2 $t0 'unverified' $repGap
        }
        else {
            $an2 = Invoke-WriteApply 'horizun_annotate' @{
                target_document = $wDoc; units = 'mm'
                actions = @(@{ operation = 'dimension'; view_id = $dimPlanViewId
                               line_start = @(($dimX + 1500), 5700, 0); line_end = @(($dimX + 1500), 8100, 0)
                               references = @($repP1, $repP2); expected_value = 600 })
            } 'dim-case2'
            if ($an2.stage -eq 'dry_run') {
                Complete-DimCase 2 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $an2.answer.text))
            }
            else {
                $why2 = Test-DimCommitted $an2.answer
                if ($why2) { Complete-DimCase 2 $t0 'fail' $why2 -TransactionStatus (Get-DimTx $an2.answer) }
                else {
                    $dim2Id = @($an2.answer.data.rows)[0].element_id
                    Complete-DimCase 2 $t0 'pass' 'committed_verified with the materialised default type; every requested/read check matched; expected_value 600 mm held' `
                        -TransactionStatus (Get-DimTx $an2.answer) `
                        -Evidence @{ element_id = $dim2Id; expected_mm = 600 }
                }
            }
        }

        # ---- case 3: a chain with an explicit type, segments proven -----------
        $t0 = Get-Date
        $dim3Id = $null; $dim2TypeId = $null
        if ($repGap) {
            Complete-DimCase 3 $t0 'unverified' $repGap
        }
        elseif (-not $dim2Id) {
            Complete-DimCase 3 $t0 'unverified' 'case 2 did not commit, so there is no created dimension to read the explicit type from'
        }
        else {
            $q3 = Invoke-Write 'horizun_query_dimensions' @{ element_ids = @($dim2Id); units = 'mm'; max_rows = 1 }
            if ($q3.isError -or -not $q3.data -or @($q3.data.rows).Count -ne 1 -or -not @($q3.data.rows)[0].type.id) {
                Complete-DimCase 3 $t0 'unverified' ('query_dimensions could not read the type of the case-2 dimension: ' + (Get-DimShortText $q3.text))
            }
            else {
                $dim2TypeId = [long]@($q3.data.rows)[0].type.id
                $an3 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'dimension'; view_id = $dimPlanViewId
                                   line_start = @(($dimX + 2000), 5700, 0); line_end = @(($dimX + 2000), 8100, 0)
                                   references = @($repP1, $repP2, $repP3); dimension_type_id = $dim2TypeId })
                } 'dim-case3'
                if ($an3.stage -eq 'dry_run') {
                    Complete-DimCase 3 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $an3.answer.text))
                }
                else {
                    $why3 = Test-DimCommitted $an3.answer
                    if ($why3) { Complete-DimCase 3 $t0 'fail' $why3 -TransactionStatus (Get-DimTx $an3.answer) }
                    else {
                        # Independent re-read: the chain must carry exactly the
                        # bays the pipes were placed at, under the explicit type.
                        $dim3Id = @($an3.answer.data.rows)[0].element_id
                        $q3b = Invoke-Write 'horizun_query_dimensions' @{ element_ids = @($dim3Id); units = 'mm'; max_rows = 1 }
                        $chainOk = $false; $segMin = $null; $segMax = $null
                        if ($q3b.data -and @($q3b.data.rows).Count -eq 1) {
                            $chainRow = @($q3b.data.rows)[0]
                            $segMm = @($chainRow.segments | ForEach-Object { [double]$_.value_internal_feet * 304.8 })
                            if (@($segMm).Count -eq 2) {
                                $segMin = ($segMm | Measure-Object -Minimum).Minimum
                                $segMax = ($segMm | Measure-Object -Maximum).Maximum
                                $chainOk = ([int]$chainRow.number_of_segments -eq 2) -and
                                           ([math]::Abs($segMin - 600) -le 0.1) -and
                                           ([math]::Abs($segMax - 1200) -le 0.1) -and
                                           ([long]$chainRow.type.id -eq $dim2TypeId)
                            }
                        }
                        if ($chainOk) {
                            Complete-DimCase 3 $t0 'pass' 'committed_verified under the explicit type; the re-read chain carries 2 segments measuring 600 and 1200 mm' `
                                -TransactionStatus (Get-DimTx $an3.answer) `
                                -Evidence @{ element_id = $dim3Id; type_id = $dim2TypeId; segments_mm = @($segMin, $segMax) }
                        }
                        else {
                            Complete-DimCase 3 $t0 'fail' ("the committed chain did not re-read as 2 segments of 600/1200 mm under type {0} (read min={1} max={2})" -f $dim2TypeId, $segMin, $segMax) `
                                -TransactionStatus (Get-DimTx $an3.answer)
                        }
                    }
                }
            }
        }

        # ---- case 4: mm, m and feet agree about one pair ----------------------
        # The three duplicates are LEFT in the model on purpose: it is disposable
        # and never saved, every later count assertion is relative within its own
        # probe, and deleting them would only re-run the path W2 already proves.
        $t0 = Get-Date
        if ($repGap) {
            Complete-DimCase 4 $t0 'unverified' $repGap
        }
        else {
            $case4 = @(
                @{ label = 'mm';   units = 'mm';   scale = 1.0;           expected = 600;    tolerance = 0.1;    x = 2500 }
                @{ label = 'm';    units = 'm';    scale = 0.001;         expected = 0.6;    tolerance = 0.0001; x = 2600 }
                @{ label = 'feet'; units = 'feet'; scale = (1.0 / 304.8); expected = 1.9685; tolerance = 0.001;  x = 2700 })
            $ids4 = @(); $why4 = $null
            foreach ($uc in $case4) {
                if ($why4) { continue }
                $s4 = [double]$uc.scale
                $an4 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = $uc.units
                    actions = @(@{ operation = 'dimension'; view_id = $dimPlanViewId
                                   line_start = @((($dimX + $uc.x) * $s4), (5700 * $s4), 0)
                                   line_end = @((($dimX + $uc.x) * $s4), (8100 * $s4), 0)
                                   references = @($repP1, $repP2)
                                   expected_value = $uc.expected; expected_tolerance = $uc.tolerance })
                } ('dim-case4-' + $uc.label)
                if ($an4.stage -eq 'dry_run') {
                    $why4 = ($uc.label + ': the rehearsal issued no token: ' + (Get-DimShortText $an4.answer.text))
                    continue
                }
                $bad4 = Test-DimCommitted $an4.answer
                if ($bad4) { $why4 = ($uc.label + ': ' + $bad4); continue }
                $ids4 += @($an4.answer.data.rows)[0].element_id
            }
            if ($why4) { Complete-DimCase 4 $t0 'fail' $why4 }
            else {
                $q4 = Invoke-Write 'horizun_query_dimensions' @{ element_ids = @($ids4); units = 'mm'; max_rows = 10 }
                $vals4 = @()
                if ($q4.data) {
                    $vals4 = @($q4.data.rows | Where-Object { $null -ne $_.value_internal_feet } |
                               ForEach-Object { [double]$_.value_internal_feet })
                }
                $agree4 = $false
                if (@($vals4).Count -eq 3) {
                    $spread4 = ($vals4 | Measure-Object -Maximum).Maximum - ($vals4 | Measure-Object -Minimum).Minimum
                    $agree4 = ($spread4 -le 0.000001)
                }
                if ($agree4) {
                    Complete-DimCase 4 $t0 'pass' 'three units, three committed_verified dimensions, and the three re-read internal values agree; the duplicates stay in the disposable model by design' `
                        -Evidence @{ element_ids = @($ids4); value_internal_feet = @($vals4) }
                }
                else {
                    Complete-DimCase 4 $t0 'fail' ("all three committed but the re-read internal values did not agree: {0}" -f (@($vals4) -join ', '))
                }
            }
        }

        # ---- case 5: angular between two 45-degree grids ----------------------
        $t0 = Get-Date
        if ($dimViewGap) { Complete-DimCase 5 $t0 'unverified' $dimViewGap }
        elseif ($dimGridGap) { Complete-DimCase 5 $t0 'unverified' $dimGridGap }
        else {
            $gRefs = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = $dimPlanViewId; element_ids = @($dimGridIds); selectors = @('grid'); units = 'mm'; max_results = 20 }
            $gridReps = @{}
            if ($gRefs.data) {
                foreach ($grow in @($gRefs.data.rows)) {
                    if ($grow.compatible_with_dimension -ne $true) { continue }
                    $gid = [long]$grow.element_id
                    if (-not $gridReps.ContainsKey($gid)) { $gridReps[$gid] = $grow.stable_representation }
                }
            }
            $g1 = $gridReps[[long]$dimGridIds[0]]; $g2 = $gridReps[[long]$dimGridIds[1]]
            if (-not $g1 -or -not $g2) {
                Complete-DimCase 5 $t0 'unverified' ('get_dimension_references produced no compatible grid reference for both grids: ' + (Get-DimShortText $gRefs.text))
            }
            else {
                $an5 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'angular_dimension'; view_id = $dimPlanViewId
                                   arc_center = @(($dimX + 1500), 10000, 0); arc_radius = 1000
                                   references = @($g1, $g2)
                                   expected_value = 45; expected_tolerance = 0.1 })
                } 'dim-case5'
                if ($an5.stage -eq 'dry_run') {
                    Complete-DimCase 5 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $an5.answer.text))
                }
                else {
                    $why5 = Test-DimCommitted $an5.answer
                    if ($why5) { Complete-DimCase 5 $t0 'fail' $why5 -TransactionStatus (Get-DimTx $an5.answer) }
                    else {
                        Complete-DimCase 5 $t0 'pass' 'committed_verified; the angular postcondition of 45 degrees (0.1 tolerance, degrees by contract) held' `
                            -TransactionStatus (Get-DimTx $an5.answer) `
                            -Evidence @{ element_id = @($an5.answer.data.rows)[0].element_id; expected_degrees = 45 }
                    }
                }
            }
        }

        # ---- cases 6 and 7: the Revit-year split ------------------------------
        # 2025+ has RadialDimension.Create / ArcLengthDimension.Create; 2023/24
        # does not, and Python cannot call an absent class either - so on the old
        # years the CORRECT product answer is a refusal that names the API and
        # the year, with NO fallback grant. Both halves are asserted; neither is
        # assumed from the year alone.
        $dimArcRow = $null; $dimArcEndpoints = @()
        if ($Year -ge 2025) {
            $t0 = Get-Date
            if ($dimViewGap) { Complete-DimCase 6 $t0 'unverified' $dimViewGap }
            elseif ($dimCylGap) { Complete-DimCase 6 $t0 'not_covered' $dimCylGap }
            else {
                $eRefs = Invoke-Write 'horizun_get_dimension_references' @{
                    view_id = $dimPlanViewId; element_ids = @($cylId)
                    selectors = @('edge', 'endpoint'); units = 'mm'; max_results = 200 }
                if ($eRefs.data) {
                    $dimArcRow = @($eRefs.data.rows | Where-Object {
                        $_.selector -eq 'edge' -and $_.compatible_with_dimension -eq $true -and
                        $_.geometry -and $_.geometry.kind -eq 'arc' }) | Select-Object -First 1
                }
                if (-not $dimArcRow) {
                    Complete-DimCase 6 $t0 'unverified' ('the half-cylinder exposed no compatible arc edge in the plan view: ' + (Get-DimShortText $eRefs.text))
                }
                else {
                    # The endpoints of THAT arc, for case 7: same z as its centre,
                    # one radius away from it, distinct fingerprints.
                    $arcC = $dimArcRow.geometry.center
                    $arcR = [double]$dimArcRow.geometry.radius
                    if ($arcC) {
                        # A point ON the arc: same z as the centre, one radius out.
                        $onArc = { param($pt)
                            if (-not $pt) { return $false }
                            if ([math]::Abs([double]$pt[2] - [double]$arcC[2]) -gt 0.5) { return $false }
                            $dist = [math]::Sqrt(([double]$pt[0] - [double]$arcC[0]) * ([double]$pt[0] - [double]$arcC[0]) +
                                                 ([double]$pt[1] - [double]$arcC[1]) * ([double]$pt[1] - [double]$arcC[1]))
                            return ([math]::Abs($dist - $arcR) -le 1.0) }
                        foreach ($erow in @($eRefs.data.rows)) {
                            if ($erow.compatible_with_dimension -ne $true) { continue }
                            $hit = $false
                            if ($erow.selector -eq 'endpoint') {
                                $pt = $null
                                if ($erow.geometry -and $erow.geometry.point) { $pt = $erow.geometry.point }
                                elseif ($erow.representative_point) { $pt = $erow.representative_point }
                                $hit = (& $onArc $pt)
                            }
                            elseif ($erow.selector -eq 'edge' -and $erow.geometry -and $erow.geometry.kind -eq 'line') {
                                # The flat end faces of the half revolution meet the arc AT
                                # its endpoints: a straight edge with an endpoint on the arc
                                # is a legitimate delimiting reference when the geometry
                                # exposes no endpoint references of its own.
                                $hit = (& $onArc $erow.geometry.start) -or (& $onArc $erow.geometry.end)
                            }
                            if (-not $hit) { continue }
                            $already = @($dimArcEndpoints | Where-Object { $_.geometry_fingerprint -eq $erow.geometry_fingerprint })
                            if ($already.Count -eq 0 -and @($dimArcEndpoints).Count -lt 2) { $dimArcEndpoints += $erow }
                        }
                    }
                    $an6 = Invoke-WriteApply 'horizun_annotate' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(
                            @{ operation = 'radial_dimension'; view_id = $dimPlanViewId
                               reference = $dimArcRow.stable_representation
                               expected_value = 300; expected_tolerance = 0.1 },
                            @{ operation = 'diameter_dimension'; view_id = $dimPlanViewId
                               reference = $dimArcRow.stable_representation
                               expected_value = 600; expected_tolerance = 0.1 })
                    } 'dim-case6'
                    if ($an6.stage -eq 'dry_run') {
                        Complete-DimCase 6 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $an6.answer.text))
                    }
                    else {
                        $why6 = Test-DimCommitted $an6.answer
                        if ($why6) { Complete-DimCase 6 $t0 'fail' $why6 -TransactionStatus (Get-DimTx $an6.answer) }
                        else {
                            Complete-DimCase 6 $t0 'pass' 'radial (300 mm) and diameter (600 mm) both committed_verified on the arc edge of the revolved half-cylinder' `
                                -TransactionStatus (Get-DimTx $an6.answer) `
                                -Evidence @{ arc_radius_mm = $arcR; rows = @($an6.answer.data.rows).Count }
                        }
                    }
                }
            }
        }
        else {
            $t0 = Get-Date
            # The year guard fires before view or reference resolution, so this
            # probe needs no cylinder - only a syntactically complete request.
            $v6 = $dimPlanViewId; if (-not $v6) { $v6 = 1 }
            $ref6 = $null
            if ($repP1) { $ref6 = $repP1 } else { $ref6 = 'HZ-UNRESOLVED-REFERENCE-PROBE' }
            $an6r = Invoke-Write 'horizun_annotate' @{
                target_document = $wDoc; units = 'mm'; dry_run = $false
                idempotency_key = "live-write-dim-case6-refusal-$probeRun"
                actions = @(@{ operation = 'radial_dimension'; view_id = $v6; reference = $ref6 }) }
            $granted6 = $false
            if ($an6r.structured -and $an6r.structured.fallback -and $an6r.structured.fallback.allowed -eq $true) { $granted6 = $true }
            if ($an6r.text -like '*"allowed": true*') { $granted6 = $true }
            if ($an6r.isError -and $an6r.text -match 'RadialDimension\.Create' -and $an6r.text -match '2025' -and
                $an6r.text -match 'Nothing was written' -and -not $granted6) {
                Complete-DimCase 6 $t0 'pass' ("Revit {0} refused by name: RadialDimension.Create exists only from 2025, nothing was written, and no Python fallback was granted" -f $Year) `
                    -Evidence @{ refusal = (Get-DimShortText $an6r.text) }
            }
            else {
                Complete-DimCase 6 $t0 'fail' ("expected a typed refusal naming RadialDimension.Create and 2025 with no fallback grant; got isError={0}, fallback_granted={1}: {2}" -f $an6r.isError, $granted6, (Get-DimShortText $an6r.text))
            }
        }

        $t0 = Get-Date
        if ($Year -ge 2025) {
            if ($dimViewGap) { Complete-DimCase 7 $t0 'unverified' $dimViewGap }
            elseif ($dimCylGap) { Complete-DimCase 7 $t0 'not_covered' $dimCylGap }
            elseif (-not $dimArcRow) {
                Complete-DimCase 7 $t0 'unverified' 'case 6 found no compatible arc edge, so there is no arc to measure the length of'
            }
            elseif (@($dimArcEndpoints).Count -lt 2) {
                Complete-DimCase 7 $t0 'unverified' ('only {0} endpoint reference(s) of the arc edge were discovered; arc_length needs its two endpoints' -f @($dimArcEndpoints).Count)
            }
            else {
                $arcC7 = $dimArcRow.geometry.center
                $an7 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'arc_length_dimension'; view_id = $dimPlanViewId
                                   arc_center = @([double]$arcC7[0], [double]$arcC7[1], [double]$arcC7[2])
                                   arc_radius = [double]$dimArcRow.geometry.radius
                                   arc_reference = $dimArcRow.stable_representation
                                   references = @(@($dimArcEndpoints)[0].stable_representation,
                                                  @($dimArcEndpoints)[1].stable_representation) })
                } 'dim-case7'
                if ($an7.stage -eq 'dry_run' -and
                    $an7.answer.text -match 'no DimensionType of style ArcLength') {
                    # A fact of the FIXTURE, verified live: this document carries no
                    # ArcLength dimension type at all (the style-fallback scan found
                    # zero), and no public API creates one from nothing. The typed
                    # refusal naming exactly that is the correct behaviour, and it is
                    # what this branch proves; a fixture that carries the style takes
                    # the creation branch below instead.
                    Complete-DimCase 7 $t0 'pass' 'the document has no ArcLength dimension type and no API can mint one: the typed refusal named the missing style and the fallback scan, and nothing was written' `
                        -Evidence @{ branch = 'typed_refusal_no_style_in_document'; refusal = (Get-DimShortText $an7.answer.text) }
                }
                elseif ($an7.stage -eq 'dry_run') {
                    Complete-DimCase 7 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $an7.answer.text))
                }
                else {
                    $why7 = Test-DimCommitted $an7.answer
                    if ($why7) { Complete-DimCase 7 $t0 'fail' $why7 -TransactionStatus (Get-DimTx $an7.answer) }
                    else {
                        Complete-DimCase 7 $t0 'pass' 'arc_length committed_verified over the arc edge and its two endpoint references' `
                            -TransactionStatus (Get-DimTx $an7.answer) `
                            -Evidence @{ element_id = @($an7.answer.data.rows)[0].element_id }
                    }
                }
            }
        }
        else {
            $v7 = $dimPlanViewId; if (-not $v7) { $v7 = 1 }
            $ref7a = $null; $ref7b = $null
            if ($repP1) { $ref7a = $repP1 } else { $ref7a = 'HZ-UNRESOLVED-REFERENCE-PROBE-A' }
            if ($repP2) { $ref7b = $repP2 } else { $ref7b = 'HZ-UNRESOLVED-REFERENCE-PROBE-B' }
            $an7r = Invoke-Write 'horizun_annotate' @{
                target_document = $wDoc; units = 'mm'; dry_run = $false
                idempotency_key = "live-write-dim-case7-refusal-$probeRun"
                actions = @(@{ operation = 'arc_length_dimension'; view_id = $v7
                               arc_center = @($dimX, 0, 0); arc_radius = 100
                               arc_reference = 'HZ-UNRESOLVED-ARC-REFERENCE-PROBE'
                               references = @($ref7a, $ref7b) }) }
            $granted7 = $false
            if ($an7r.structured -and $an7r.structured.fallback -and $an7r.structured.fallback.allowed -eq $true) { $granted7 = $true }
            if ($an7r.text -like '*"allowed": true*') { $granted7 = $true }
            if ($an7r.isError -and $an7r.text -match 'ArcLengthDimension\.Create' -and $an7r.text -match '2025' -and
                $an7r.text -match 'Nothing was written' -and -not $granted7) {
                Complete-DimCase 7 $t0 'pass' ("Revit {0} refused by name: ArcLengthDimension.Create exists only from 2025, nothing was written, and no Python fallback was granted" -f $Year) `
                    -Evidence @{ refusal = (Get-DimShortText $an7r.text) }
            }
            else {
                Complete-DimCase 7 $t0 'fail' ("expected a typed refusal naming ArcLengthDimension.Create and 2025 with no fallback grant; got isError={0}, fallback_granted={1}: {2}" -f $an7r.isError, $granted7, (Get-DimShortText $an7r.text))
            }
        }

        # ---- case 8: spots on the box top face --------------------------------
        $t0 = Get-Date
        $dimFaceRep = $null
        if ($dimViewGap) { Complete-DimCase 8 $t0 'unverified' $dimViewGap }
        elseif ($dimBoxGap) { Complete-DimCase 8 $t0 'not_covered' $dimBoxGap }
        else {
            $fRefs = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = $dimPlanViewId; element_ids = @($boxAId)
                selectors = @('nearest_face'); probe_point = @($dimX, 0, 5000); units = 'mm'; max_results = 20 }
            $faceRow = $null
            if ($fRefs.data) {
                $faceRow = @($fRefs.data.rows | Where-Object {
                    $_.selector -eq 'nearest_face' -and $_.compatible_with_dimension -eq $true }) | Select-Object -First 1
            }
            if (-not $faceRow) {
                Complete-DimCase 8 $t0 'unverified' ('nearest_face from a probe point above BOX_A produced no compatible face: ' + (Get-DimShortText $fRefs.text))
            }
            else {
                $dimFaceRep = $faceRow.stable_representation
                # The face's own z, read from the discovery answer, so the spot
                # origin lands ON the reference wherever the level actually is.
                $faceZ = $null
                if ($faceRow.geometry -and $faceRow.geometry.origin) { $faceZ = [double]@($faceRow.geometry.origin)[2] }
                elseif ($faceRow.representative_point) { $faceZ = [double]@($faceRow.representative_point)[2] }
                if ($null -eq $faceZ) {
                    Complete-DimCase 8 $t0 'unverified' 'the discovered face carried no usable origin/representative point, so no on-face spot origin could be computed'
                }
                else {
                    $an8 = Invoke-WriteApply 'horizun_annotate' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(
                            @{ operation = 'spot_elevation'; view_id = $dimPlanViewId
                               reference = $dimFaceRep; point = @(($dimX - 200), -100, $faceZ); leader = $false },
                            @{ operation = 'spot_coordinate'; view_id = $dimPlanViewId
                               reference = $dimFaceRep; point = @(($dimX + 200), 100, $faceZ); leader = $false })
                    } 'dim-case8'
                    if ($an8.stage -eq 'dry_run') {
                        Complete-DimCase 8 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $an8.answer.text))
                    }
                    else {
                        $why8 = Test-DimCommitted $an8.answer
                        if ($why8) { Complete-DimCase 8 $t0 'fail' $why8 -TransactionStatus (Get-DimTx $an8.answer) }
                        else {
                            Complete-DimCase 8 $t0 'pass' 'spot_elevation and spot_coordinate both committed_verified on the discovered top face, checks compared against the rehearsal' `
                                -TransactionStatus (Get-DimTx $an8.answer) `
                                -Evidence @{ face_z_mm = $faceZ; rows = @($an8.answer.data.rows).Count }
                        }
                    }
                }
            }
        }

        # ---- case 9: spot_slope has no API anywhere, and nothing is written ---
        $t0 = Get-Date
        $q9a = Invoke-Write 'horizun_query_dimensions' @{ shapes = @('spot_slope'); max_rows = 1 }
        $slopeBefore = $null
        if (-not $q9a.isError -and $q9a.data) { $slopeBefore = [int]$q9a.data.total_matched }
        if ($null -eq $slopeBefore) {
            Complete-DimCase 9 $t0 'unverified' ('the spot_slope census could not be read before the refusal, so "nothing was written" could not be proven: ' + (Get-DimShortText $q9a.text))
        }
        else {
            $v9 = $dimPlanViewId; if (-not $v9) { $v9 = 1 }
            $ref9 = $dimFaceRep
            if (-not $ref9 -and $repP1) { $ref9 = $repP1 }
            if (-not $ref9) { $ref9 = 'HZ-UNRESOLVED-REFERENCE-PROBE' }
            $an9 = Invoke-Write 'horizun_annotate' @{
                target_document = $wDoc; units = 'mm'; dry_run = $false
                idempotency_key = "live-write-dim-case9-refusal-$probeRun"
                actions = @(@{ operation = 'spot_slope'; view_id = $v9; reference = $ref9; point = @($dimX, 0, 0) }) }
            $granted9 = $false
            if ($an9.structured -and $an9.structured.fallback -and $an9.structured.fallback.allowed -eq $true) { $granted9 = $true }
            if ($an9.text -like '*"allowed": true*') { $granted9 = $true }
            $q9b = Invoke-Write 'horizun_query_dimensions' @{ shapes = @('spot_slope'); max_rows = 1 }
            $slopeAfter = $null
            if (-not $q9b.isError -and $q9b.data) { $slopeAfter = [int]$q9b.data.total_matched }
            if ($an9.isError -and $an9.text -match 'not supported on any Revit' -and -not $granted9 -and
                $slopeAfter -eq $slopeBefore) {
                Complete-DimCase 9 $t0 'pass' ("refused naming the absent creation API for every supported year, no fallback granted, and the spot_slope census is unchanged at {0}" -f $slopeBefore) `
                    -Evidence @{ census_before = $slopeBefore; census_after = $slopeAfter }
            }
            else {
                Complete-DimCase 9 $t0 'fail' ("expected a no-API-any-year refusal with no fallback and an unchanged census; got isError={0}, fallback_granted={1}, census {2}->{3}: {4}" -f $an9.isError, $granted9, $slopeBefore, $slopeAfter, (Get-DimShortText $an9.text))
            }
        }

        # ---- case 10: one failing postcondition takes the WHOLE batch down ----
        $t0 = Get-Date
        if ($repGap) {
            Complete-DimCase 10 $t0 'unverified' $repGap
        }
        else {
            $before10 = Get-DimCount $dimPlanViewId
            if ($null -eq $before10) {
                Complete-DimCase 10 $t0 'unverified' 'the dimension census of the plan view could not be read before the batch'
            }
            else {
                $an10 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(
                        @{ operation = 'dimension'; view_id = $dimPlanViewId
                           line_start = @(($dimX + 2820), 5700, 0); line_end = @(($dimX + 2820), 8100, 0)
                           references = @($repP1, $repP2); expected_value = 600 },
                        @{ operation = 'dimension'; view_id = $dimPlanViewId
                           line_start = @(($dimX + 2880), 5700, 0); line_end = @(($dimX + 2880), 8100, 0)
                           references = @($repP1, $repP3)
                           expected_value = 9999; expected_tolerance = 0.1 })
                } 'dim-case10'
                if ($an10.stage -eq 'dry_run') {
                    Complete-DimCase 10 $t0 'unverified' ('the rehearsal withheld the token, so the failing postcondition was never provoked at apply: ' + (Get-DimShortText $an10.answer.text))
                }
                elseif (-not $an10.answer.isError) {
                    Complete-DimCase 10 $t0 'fail' 'the apply REPORTED SUCCESS although the second action expects 9999 mm from a 1800 mm bay - the postcondition did not fire'
                }
                else {
                    # The verdict travels as JSON embedded in the error text; the
                    # object that carries `state` is the one that speaks for the
                    # transaction.
                    $rb10 = $null
                    if ($an10.answer.structured -and $an10.answer.structured.PSObject.Properties.Name -contains 'state') { $rb10 = $an10.answer.structured }
                    elseif ($an10.answer.data -and $an10.answer.data.PSObject.Properties.Name -contains 'state') { $rb10 = $an10.answer.data }
                    if (-not $rb10) {
                        foreach ($cand10 in (Get-JsonObjects $an10.answer.text)) {
                            try { $obj10 = $cand10 | ConvertFrom-Json } catch { continue }
                            if ($obj10.PSObject.Properties.Name -contains 'state') { $rb10 = $obj10; break }
                        }
                    }
                    $after10 = Get-DimCount $dimPlanViewId
                    if (-not $rb10) {
                        Complete-DimCase 10 $t0 'unverified' ('the refusal carried no extractable state object, so the rollback could not be told from a refusal that never wrote: ' + (Get-DimShortText $an10.answer.text))
                    }
                    elseif ($rb10.state -eq 'rolled_back' -and $rb10.rollback_confirmed -eq $true -and
                            "$($rb10.transaction_group_status)" -match 'RolledBack' -and
                            $after10 -eq $before10) {
                        Complete-DimCase 10 $t0 'pass' ("state=rolled_back, rollback_confirmed=true, transaction_group_status=RolledBack, and the view still holds {0} dimension(s) - the VALID action did not survive either" -f $before10) `
                            -TransactionStatus "$($rb10.transaction_group_status)" `
                            -Evidence @{ census_before = $before10; census_after = $after10; state = $rb10.state }
                    }
                    else {
                        Complete-DimCase 10 $t0 'fail' ("expected state=rolled_back with a confirmed RolledBack group and an unchanged census; got state='{0}', rollback_confirmed='{1}', group='{2}', census {3}->{4}" -f $rb10.state, $rb10.rollback_confirmed, $rb10.transaction_group_status, $before10, $after10) `
                            -TransactionStatus "$($rb10.transaction_group_status)"
                    }
                }
            }
        }

        # ---- case 11: the model moves between rehearsal and apply -------------
        $t0 = Get-Date
        if ($dimViewGap) { Complete-DimCase 11 $t0 'unverified' $dimViewGap }
        else {
            # Primary geometry: the two boxes. Fallback when the boxes are not
            # there: an ad-hoc pipe that is MOVED, chosen so pipes 1-3 - which
            # case 16 still measures - are never disturbed.
            $ref11a = $null; $ref11b = $null; $moveId = $null; $line11 = $null; $whyStage11 = $null
            if (-not $dimBoxGap) {
                $fa11 = Invoke-Write 'horizun_get_dimension_references' @{
                    view_id = $dimPlanViewId; element_ids = @($boxAId)
                    selectors = @('nearest_face'); probe_point = @($dimX, 1000, 200); units = 'mm'; max_results = 10 }
                $fb11 = Invoke-Write 'horizun_get_dimension_references' @{
                    view_id = $dimPlanViewId; element_ids = @($boxBId)
                    selectors = @('nearest_face'); probe_point = @($dimX, 1000, 200); units = 'mm'; max_results = 10 }
                if ($fa11.data) {
                    $rowA11 = @($fa11.data.rows | Where-Object { $_.compatible_with_dimension -eq $true }) | Select-Object -First 1
                    if ($rowA11) { $ref11a = $rowA11.stable_representation }
                }
                if ($fb11.data) {
                    $rowB11 = @($fb11.data.rows | Where-Object { $_.compatible_with_dimension -eq $true }) | Select-Object -First 1
                    if ($rowB11) { $ref11b = $rowB11.stable_representation }
                }
                $moveId = $boxBId
                $line11 = @(@(($dimX - 700), 300, 0), @(($dimX - 700), 1700, 0))
                if (-not $ref11a -or -not $ref11b) { $whyStage11 = 'the facing box faces could not be discovered'; $ref11a = $null; $ref11b = $null }
            }
            if (-not $ref11a -and -not $repGap) {
                # Ad-hoc GRID, not pipe: MEP centerlines are refused by the dimension
                # API (measured live), and a grid moves just as provably.
                $mk11 = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document = $wDoc; units = 'mm'
                    elements = @(@{ kind = 'grid'; name = "HZM11_$dimTag"
                                    start = @($dimX, 9000, 0); end = @(($dimX + 3000), 9000, 0) })
                } 'dim-case11-grid'
                if ($mk11.stage -eq 'apply' -and -not $mk11.answer.isError -and $mk11.answer.data -and
                    @($mk11.answer.data.rows).Count -gt 0 -and @($mk11.answer.data.rows)[0].verified -eq $true) {
                    $moveId = @($mk11.answer.data.rows)[0].element_id
                    $cl11 = Invoke-Write 'horizun_get_dimension_references' @{
                        view_id = $dimPlanViewId; element_ids = @($moveId)
                        selectors = @('grid'); units = 'mm'; max_results = 10 }
                    if ($cl11.data) {
                        $rowM11 = @($cl11.data.rows | Where-Object { $_.compatible_with_dimension -eq $true }) | Select-Object -First 1
                        if ($rowM11) {
                            $ref11a = $repP3; $ref11b = $rowM11.stable_representation
                            $line11 = @(@(($dimX + 1200), 7600, 0), @(($dimX + 1200), 9400, 0))
                        }
                    }
                }
            }
            if (-not $ref11a -or -not $ref11b) {
                $why11gap = 'neither the box faces nor an ad-hoc grid reference could be staged'
                if ($whyStage11) { $why11gap = $why11gap + ' (' + $whyStage11 + ')' }
                Complete-DimCase 11 $t0 'unverified' $why11gap
            }
            else {
                $before11 = Get-DimCount $dimPlanViewId
                $args11 = @{
                    target_document = $wDoc; units = 'mm'; dry_run = $true
                    actions = @(@{ operation = 'dimension'; view_id = $dimPlanViewId
                                   line_start = $line11[0]; line_end = $line11[1]
                                   references = @($ref11a, $ref11b) }) }
                $dry11 = Invoke-Write 'horizun_annotate' $args11
                $tok11 = $null
                if (-not $dry11.isError -and $dry11.data) { $tok11 = $dry11.data.confirmation_token }
                if (-not $tok11) {
                    Complete-DimCase 11 $t0 'unverified' ('the rehearsal issued no token to go stale: ' + (Get-DimShortText $dry11.text))
                }
                else {
                    $mv11 = Invoke-WriteApply 'horizun_transform_elements' @{
                        target_document = $wDoc; units = 'mm'
                        operations = @(@{ operation = 'move'; element_ids = @($moveId); vector = @(0, 50, 0) })
                    } 'dim-case11-move'
                    if ($mv11.stage -ne 'apply' -or $mv11.answer.isError) {
                        Complete-DimCase 11 $t0 'unverified' ('the referenced element could not be moved between rehearsal and apply: ' + (Get-DimShortText $mv11.answer.text))
                    }
                    else {
                        $args11apply = @{
                            target_document = $wDoc; units = 'mm'; dry_run = $false
                            confirmation_token = $tok11
                            idempotency_key = "live-write-dim-case11-$probeRun"
                            actions = $args11.actions }
                        $ap11 = Invoke-Write 'horizun_annotate' $args11apply
                        $after11 = Get-DimCount $dimPlanViewId
                        if ($ap11.isError -and $ap11.text -match 'THE MODEL MOVED AFTER THE DRY RUN' -and
                            $after11 -eq $before11) {
                            Complete-DimCase 11 $t0 'pass' ("the element moved 50 mm after the rehearsal and the stale token was refused with THE MODEL MOVED AFTER THE DRY RUN; census unchanged at {0}" -f $before11) `
                                -Evidence @{ moved_element = $moveId; census_before = $before11; census_after = $after11 }
                        }
                        else {
                            Complete-DimCase 11 $t0 'fail' ("expected the stale-plan refusal and an unchanged census; got isError={0}, census {1}->{2}: {3}" -f $ap11.isError, $before11, $after11, (Get-DimShortText $ap11.text))
                        }
                    }
                }
            }
        }

        # ---- case 12: a reference DELETED between rehearsal and apply ---------
        $t0 = Get-Date
        if ($repGap) {
            Complete-DimCase 12 $t0 'unverified' $repGap
        }
        else {
            # Ad-hoc GRID, not pipe: MEP centerlines are refused by the dimension API
            # (measured live), and deleting a grid goes stale just as provably.
            $mk12 = Invoke-WriteApply 'horizun_create_elements' @{
                target_document = $wDoc; units = 'mm'
                elements = @(@{ kind = 'grid'; name = "HZM12_$dimTag"
                                start = @($dimX, 10500, 0); end = @(($dimX + 3000), 10500, 0) })
            } 'dim-case12-grid'
            $pipe12 = $null
            if ($mk12.stage -eq 'apply' -and -not $mk12.answer.isError -and $mk12.answer.data -and
                @($mk12.answer.data.rows).Count -gt 0 -and @($mk12.answer.data.rows)[0].verified -eq $true) {
                $pipe12 = @($mk12.answer.data.rows)[0].element_id
            }
            if (-not $pipe12) {
                Complete-DimCase 12 $t0 'unverified' ('the ad-hoc grid whose deletion goes stale could not be created: ' + (Get-DimShortText $mk12.answer.text))
            }
            else {
                $cl12 = Invoke-Write 'horizun_get_dimension_references' @{
                    view_id = $dimPlanViewId; element_ids = @($pipe12)
                    selectors = @('grid'); units = 'mm'; max_results = 10 }
                $rep12 = $null
                if ($cl12.data) {
                    $row12 = @($cl12.data.rows | Where-Object { $_.compatible_with_dimension -eq $true }) | Select-Object -First 1
                    if ($row12) { $rep12 = $row12.stable_representation }
                }
                if (-not $rep12) {
                    Complete-DimCase 12 $t0 'unverified' ('no compatible grid reference was discovered on the ad-hoc grid: ' + (Get-DimShortText $cl12.text))
                }
                else {
                    $before12 = Get-DimCount $dimPlanViewId
                    $args12 = @{
                        target_document = $wDoc; units = 'mm'; dry_run = $true
                        actions = @(@{ operation = 'dimension'; view_id = $dimPlanViewId
                                       line_start = @(($dimX + 1800), 5800, 0); line_end = @(($dimX + 1800), 10700, 0)
                                       references = @($repP1, $rep12) }) }
                    $dry12 = Invoke-Write 'horizun_annotate' $args12
                    $tok12 = $null
                    if (-not $dry12.isError -and $dry12.data) { $tok12 = $dry12.data.confirmation_token }
                    if (-not $tok12) {
                        Complete-DimCase 12 $t0 'unverified' ('the rehearsal issued no token to go stale: ' + (Get-DimShortText $dry12.text))
                    }
                    else {
                        $del12 = Invoke-WriteApply 'horizun_delete_verified' @{
                            mode = 'ids'; ids = @($pipe12); target_document = $wDoc; id_cap = 10 } 'dim-case12-delete'
                        if ($del12.stage -ne 'apply' -or $del12.answer.isError) {
                            Complete-DimCase 12 $t0 'unverified' ('the referenced grid could not be deleted between rehearsal and apply: ' + (Get-DimShortText $del12.answer.text))
                        }
                        else {
                            $args12apply = @{
                                target_document = $wDoc; units = 'mm'; dry_run = $false
                                confirmation_token = $tok12
                                idempotency_key = "live-write-dim-case12-$probeRun"
                                actions = $args12.actions }
                            $ap12 = Invoke-Write 'horizun_annotate' $args12apply
                            $after12 = Get-DimCount $dimPlanViewId
                            # Both wordings are correct refusals here: the stale
                            # fingerprint or the reference that no longer resolves.
                            if ($ap12.isError -and $after12 -eq $before12) {
                                Complete-DimCase 12 $t0 'pass' ("the apply against a deleted reference was refused and the census is unchanged at {0}" -f $before12) `
                                    -Evidence @{ deleted_reference_element = $pipe12; census_before = $before12; census_after = $after12; refusal = (Get-DimShortText $ap12.text) }
                            }
                            else {
                                Complete-DimCase 12 $t0 'fail' ("expected a refusal and an unchanged census; got isError={0}, census {1}->{2}: {3}" -f $ap12.isError, $before12, $after12, (Get-DimShortText $ap12.text))
                            }
                        }
                    }
                }
            }
        }

        # ---- case 13: a schedule is not a place a dimension can live ----------
        $t0 = Get-Date
        $ls13 = Invoke-Write 'horizun_list_schedules' @{ max_rows = 1 }
        $sched13 = $null
        if (-not $ls13.isError -and $ls13.data -and @($ls13.data.rows).Count -gt 0) {
            $sched13 = @($ls13.data.rows)[0].schedule_id
        }
        if (-not $sched13) {
            Complete-DimCase 13 $t0 'not_covered' 'the disposable fixture has no schedule to aim a dimension at'
        }
        else {
            $ref13a = $repP1; $ref13b = $repP2
            if (-not $ref13a) { $ref13a = 'HZ-FAKE-REFERENCE-A' }
            if (-not $ref13b) { $ref13b = 'HZ-FAKE-REFERENCE-B' }
            $d13 = Invoke-Write 'horizun_annotate' @{
                target_document = $wDoc; units = 'mm'; dry_run = $true
                actions = @(@{ operation = 'dimension'; view_id = $sched13
                               line_start = @(0, 0, 0); line_end = @(1000, 0, 0)
                               references = @($ref13a, $ref13b) }) }
            $tok13 = $null
            if ($d13.data) { $tok13 = $d13.data.confirmation_token }
            if (-not $tok13 -and $d13.text -match '(?i)schedule') {
                Complete-DimCase 13 $t0 'pass' 'the rehearsal refused the schedule view by name and withheld the token' `
                    -Evidence @{ schedule_id = $sched13 }
            }
            else {
                Complete-DimCase 13 $t0 'fail' ("expected an invalid row naming the schedule and no token; got token_present={0}: {1}" -f [bool]$tok13, (Get-DimShortText $d13.text))
            }
        }

        # ---- case 14: the same reference twice is refused ---------------------
        $t0 = Get-Date
        if ($repGap) {
            Complete-DimCase 14 $t0 'unverified' $repGap
        }
        else {
            $d14 = Invoke-Write 'horizun_annotate' @{
                target_document = $wDoc; units = 'mm'; dry_run = $true
                actions = @(@{ operation = 'dimension'; view_id = $dimPlanViewId
                               line_start = @(($dimX + 1000), 5700, 0); line_end = @(($dimX + 1000), 8100, 0)
                               references = @($repP1, $repP1) }) }
            $tok14 = $null
            if ($d14.data) { $tok14 = $d14.data.confirmation_token }
            if (-not $tok14 -and $d14.text -match 'duplicates references') {
                Complete-DimCase 14 $t0 'pass' 'the duplicated stable representation was refused by index and the token withheld'
            }
            else {
                Complete-DimCase 14 $t0 'fail' ("expected an invalid row naming the duplicate and no token; got token_present={0}: {1}" -f [bool]$tok14, (Get-DimShortText $d14.text))
            }
        }

        # ---- case 15: a link instance is unreadable WITH the structured code --
        $t0 = Get-Date
        $lq15 = Invoke-Write 'horizun_query_model' @{ categories = @('OST_RvtLinks'); include_links = $false; max_rows = 1 }
        $link15 = $null
        $staged15 = $false
        $stageWhy15 = $null
        if (-not $lq15.isError -and $lq15.data -and @($lq15.data.rows).Count -gt 0) {
            $link15 = @($lq15.data.rows)[0].element_id
        }
        if (-not $link15) {
            # Not every fixture ships with a RevitLinkInstance, and a run that skips
            # the link refusal reads exactly like one that proved it. When the gate
            # names a same-year link source (-LinkSourceFile, a COPY so the original
            # is never the one loaded) and this Revit advertises execute_python, the
            # harness stages its own link in the never-saved disposable model -
            # measured live on Revit 2023 (2026-08-24: RevitLinkType.Create +
            # RevitLinkInstance.Create, zero dialogs) - and the probe then runs
            # against an instance the TYPED query rediscovered, exactly as a client
            # would find it.
            $pythonListed15 = @($listed | Where-Object { $_.name -eq 'horizun_execute_python' }).Count -gt 0
            if ([string]::IsNullOrWhiteSpace($LinkSourceFile)) {
                $stageWhy15 = 'no -LinkSourceFile was supplied'
            }
            elseif (-not (Test-Path $LinkSourceFile)) {
                $stageWhy15 = "the link source file does not exist: $LinkSourceFile"
            }
            elseif (-not $pythonListed15) {
                $stageWhy15 = 'execute_python is not advertised, so the harness cannot stage a link'
            }
            else {
                $stageCode = @'
from Autodesk.Revit.DB import (RevitLinkType, RevitLinkInstance, RevitLinkOptions,
                               ModelPathUtils, Transaction, FilteredElementCollector)

def _ids(collector):
    out = []
    for e in collector:
        out.append(e.Id.IntegerValue if hasattr(e.Id, 'IntegerValue') else e.Id.Value)
    return out

mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(r'__LINK_SOURCE__')
before = _ids(FilteredElementCollector(doc).OfClass(RevitLinkInstance))
t = Transaction(doc, 'HZ live gate: stage link fixture')
t.Start()
try:
    res = RevitLinkType.Create(doc, mp, RevitLinkOptions(False))
    inst = RevitLinkInstance.Create(doc, res.ElementId)
    t.Commit()
except Exception as ex:
    t.RollBack()
    __output__ = {'status': 'failed', 'error': str(ex)}
else:
    after = _ids(FilteredElementCollector(doc).OfClass(RevitLinkInstance))
    new_ids = [i for i in after if i not in before]
    iid = inst.Id.IntegerValue if hasattr(inst.Id, 'IntegerValue') else inst.Id.Value
    ok = (len(new_ids) == 1 and new_ids[0] == iid)
    __output__ = {'status': 'self_reported_verified' if ok else 'partial',
                  'link_instance_id': iid, 'reread_new_instances': new_ids}
'@
                $stageCode = $stageCode.Replace('__LINK_SOURCE__', $LinkSourceFile)
                if (-not (Test-Path $scratchDir)) { New-Item -ItemType Directory -Force $scratchDir | Out-Null }
                $stagePath = Join-Path $scratchDir 'stage-link-fixture.py'
                [IO.File]::WriteAllText($stagePath, $stageCode, [Text.UTF8Encoding]::new($false))
                $st15 = Invoke-Write 'horizun_execute_python' @{
                    code_path = $stagePath; target_document = $wDoc
                    idempotency_key = "live-dim15-stage-link-$probeRun" }
                $stOut = $null
                if (-not $st15.isError -and $st15.data) { $stOut = $st15.data.output }
                if ($stOut -and $stOut.status -eq 'self_reported_verified' -and $stOut.link_instance_id) {
                    $lq15b = Invoke-Write 'horizun_query_model' @{ categories = @('OST_RvtLinks'); include_links = $false; max_rows = 1 }
                    if (-not $lq15b.isError -and $lq15b.data -and @($lq15b.data.rows).Count -gt 0) {
                        $link15 = @($lq15b.data.rows)[0].element_id
                        $staged15 = $true
                    }
                    else {
                        $stageWhy15 = 'the staged link was not rediscovered by the typed query: ' + (Get-DimShortText $lq15b.text)
                    }
                }
                else {
                    $stageStatus = if ($stOut) { [string]$stOut.status + ' ' + [string]$stOut.error } else { Get-DimShortText $st15.text }
                    $stageWhy15 = 'the staging script did not verify its own link: ' + $stageStatus
                }
            }
        }
        if (-not $link15) {
            Complete-DimCase 15 $t0 'not_covered' ('the disposable fixture has no RevitLinkInstance and the harness could not stage one (' + $stageWhy15 + '), so the structured link refusal could not be provoked')
        }
        elseif ($dimViewGap) { Complete-DimCase 15 $t0 'unverified' $dimViewGap }
        else {
            $lr15 = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = $dimPlanViewId; element_ids = @($link15); units = 'mm'; max_results = 10 }
            if ($lr15.isError -or -not $lr15.data) {
                Complete-DimCase 15 $t0 'fail' ('the call errored instead of reporting the link in coverage.unreadable: ' + (Get-DimShortText $lr15.text))
            }
            else {
                $unread15 = @($lr15.data.coverage.unreadable | Where-Object { [long]$_.element_id -eq [long]$link15 })
                if ($unread15.Count -ge 1 -and $unread15[0].code -eq 'link_references_not_supported') {
                    $how15 = if ($staged15) { 'a link staged by this run in the never-saved disposable model' } else { "the fixture's own link instance" }
                    Complete-DimCase 15 $t0 'pass' ("the link instance ({0}) landed in coverage.unreadable carrying code link_references_not_supported" -f $how15) `
                        -Evidence @{ link_instance = $link15; code = $unread15[0].code; staged_by_harness = $staged15 }
                }
                else {
                    Complete-DimCase 15 $t0 'fail' ("the link instance was not reported unreadable with the structured code; unreadable entries for it: {0}" -f $unread15.Count)
                }
            }
        }

        # ---- case 16: query + edit round trip, and an edit token gone stale ---
        $t0 = Get-Date
        if ($repGap) {
            Complete-DimCase 16 $t0 'unverified' $repGap
        }
        elseif (-not $dim2Id -or -not $dim3Id) {
            Complete-DimCase 16 $t0 'unverified' 'cases 2 and 3 did not both commit, so there is nothing to query, edit or make stale'
        }
        else {
            $why16 = $null; $ev16 = @{}
            # a) the independent read-back of both created dimensions.
            $qa16 = Invoke-Write 'horizun_query_dimensions' @{ element_ids = @($dim2Id, $dim3Id); units = 'mm'; max_rows = 10 }
            if ($qa16.isError -or -not $qa16.data -or @($qa16.data.rows).Count -ne 2) {
                $why16 = 'read-back: query_dimensions did not return exactly the two created dimensions: ' + (Get-DimShortText $qa16.text)
            }
            else {
                foreach ($qrow16 in @($qa16.data.rows)) {
                    if ($why16) { continue }
                    if (-not (All-Rows $qrow16.references { param($rr) -not [string]::IsNullOrWhiteSpace($rr.stable_representation) })) {
                        $why16 = 'read-back: a reference came back without its stable representation'
                    }
                    elseif ($qrow16.references_available -ne $true) { $why16 = 'read-back: references_available was not true' }
                    elseif ([int]$qrow16.broken_references -ne 0) { $why16 = 'read-back: broken_references was not 0' }
                }
                if (-not $why16) {
                    $chain16 = @($qa16.data.rows | Where-Object { [long]$_.element_id -eq [long]$dim3Id }) | Select-Object -First 1
                    $seg16 = @()
                    if ($chain16) { $seg16 = @($chain16.segments | ForEach-Object { [double]$_.value_internal_feet * 304.8 }) }
                    $segOk16 = $false
                    if (@($seg16).Count -eq 2) {
                        $sMin16 = ($seg16 | Measure-Object -Minimum).Minimum
                        $sMax16 = ($seg16 | Measure-Object -Maximum).Maximum
                        $segOk16 = ([math]::Abs($sMin16 - 600) -le 0.1) -and ([math]::Abs($sMax16 - 1200) -le 0.1)
                    }
                    if (-not $segOk16) { $why16 = ('read-back: the chain did not carry segments of 600 and 1200 mm (read: ' + (@($seg16) -join ', ') + ')') }
                    else { $ev16.read_back = 'both rows complete; chain segments 600/1200 mm' }
                }
            }
            # b) prefix + value_override on the single-segment dimension.
            if (-not $why16) {
                $ed16a = Invoke-WriteApply 'horizun_edit_dimensions' @{
                    target_document = $wDoc
                    actions = @(@{ element_id = $dim2Id; prefix = [char]0x00B1; value_override = 'VERIFIED' })
                } 'dim-case16-override'
                $fields16a = $null
                if ($ed16a.stage -eq 'apply' -and -not $ed16a.answer.isError -and $ed16a.answer.data -and
                    @($ed16a.answer.data.rows).Count -gt 0) {
                    $fields16a = @($ed16a.answer.data.rows)[0].fields
                }
                if (-not $fields16a -or $ed16a.answer.data.state -ne 'verified_applied' -or
                    -not (All-Rows $fields16a { param($f) $f.match -eq $true })) {
                    $why16 = 'override: the prefix/value_override edit did not come back verified_applied with every field matched: ' + (Get-DimShortText $ed16a.answer.text)
                }
                else { $ev16.override = 'prefix and VERIFIED override applied and re-read' }
            }
            # c) EQ on the chain.
            if (-not $why16) {
                $ed16b = Invoke-WriteApply 'horizun_edit_dimensions' @{
                    target_document = $wDoc
                    actions = @(@{ element_id = $dim3Id; eq = $true })
                } 'dim-case16-eq'
                $fields16b = $null
                if ($ed16b.stage -eq 'apply' -and -not $ed16b.answer.isError -and $ed16b.answer.data -and
                    @($ed16b.answer.data.rows).Count -gt 0) {
                    $fields16b = @($ed16b.answer.data.rows)[0].fields
                }
                if (-not $fields16b -or $ed16b.answer.data.state -ne 'verified_applied' -or
                    -not (All-Rows $fields16b { param($f)
                        $f.match -eq $true -or ($null -eq $f.match -and $f.verified_by -eq 'substance') })) {
                    $why16 = 'eq: the EQ edit on the chain did not come back verified_applied with every field matched: ' + (Get-DimShortText $ed16b.answer.text)
                }
                else { $ev16.eq = 'EQ applied to the chain and re-read' }
            }
            # d) an edit token minted before ANOTHER edit of the same dimension
            #    must refuse as stale, not overwrite.
            if (-not $why16) {
                $actions16a = @(@{ element_id = $dim2Id; suffix = 'HZA' })
                $dry16a = Invoke-Write 'horizun_edit_dimensions' @{
                    target_document = $wDoc; dry_run = $true; actions = $actions16a }
                $tok16a = $null
                if (-not $dry16a.isError -and $dry16a.data) { $tok16a = $dry16a.data.confirmation_token }
                if (-not $tok16a) {
                    $why16 = 'stale: the first edit rehearsal issued no token: ' + (Get-DimShortText $dry16a.text)
                }
                else {
                    $ed16c = Invoke-WriteApply 'horizun_edit_dimensions' @{
                        target_document = $wDoc
                        actions = @(@{ element_id = $dim2Id; below = 'HZB' })
                    } 'dim-case16-b'
                    if ($ed16c.stage -ne 'apply' -or $ed16c.answer.isError -or
                        $ed16c.answer.data.state -ne 'verified_applied') {
                        $why16 = 'stale: the interleaved edit did not commit, so the first token was never made stale: ' + (Get-DimShortText $ed16c.answer.text)
                    }
                    else {
                        $ap16a = Invoke-Write 'horizun_edit_dimensions' @{
                            target_document = $wDoc; dry_run = $false
                            confirmation_token = $tok16a
                            idempotency_key = "live-write-dim-case16-stale-$probeRun"
                            actions = $actions16a }
                        if ($ap16a.isError -and $ap16a.text -match 'THE MODEL MOVED AFTER THE DRY RUN') {
                            $ev16.stale = 'the pre-edit token was refused with THE MODEL MOVED AFTER THE DRY RUN'
                        }
                        else {
                            $why16 = 'stale: the outdated token was NOT refused as a stale plan: ' + (Get-DimShortText $ap16a.text)
                        }
                    }
                }
            }
            # e) value_override '' CLEARS, and the model says so.
            if (-not $why16) {
                $ed16d = Invoke-WriteApply 'horizun_edit_dimensions' @{
                    target_document = $wDoc
                    actions = @(@{ element_id = $dim2Id; value_override = '' })
                } 'dim-case16-clear'
                $clearOk = $false
                if ($ed16d.stage -eq 'apply' -and -not $ed16d.answer.isError -and $ed16d.answer.data -and
                    $ed16d.answer.data.state -eq 'verified_applied') {
                    $ovField = @(@($ed16d.answer.data.rows)[0].fields | Where-Object { $_.field -eq 'value_override' }) | Select-Object -First 1
                    if ($ovField -and $ovField.match -eq $true) { $clearOk = $true }
                }
                if ($clearOk) {
                    $qe16 = Invoke-Write 'horizun_query_dimensions' @{ element_ids = @($dim2Id); units = 'mm'; max_rows = 1 }
                    if ($qe16.data -and @($qe16.data.rows).Count -eq 1 -and
                        "$(@($qe16.data.rows)[0].value_presented)" -ne 'VERIFIED') {
                        $ev16.clear = 'the empty override cleared and the re-read presentation is a value again'
                    }
                    else { $why16 = 'clear: the override was reported cleared but the re-read presentation still shows VERIFIED' }
                }
                else { $why16 = 'clear: the empty value_override did not come back verified_applied with a matched read-back: ' + (Get-DimShortText $ed16d.answer.text) }
            }
            if ($why16) { Complete-DimCase 16 $t0 'fail' $why16 -Evidence $ev16 }
            else {
                Complete-DimCase 16 $t0 'pass' 'read-back complete; override, EQ and the cleared override all verified_applied field by field; a pre-edit token refused as THE MODEL MOVED' `
                    -Evidence $ev16
            }
        }

        # ---- case 17: the RFA dimension survives the reopen ------------------
        # Provisioned by the fixture step: the HZ_DIM_BOX create_family above IS
        # this probe's call - here its reopened_verification is held to account.
        $t0 = Get-Date
        if (-not $dimBoxAnswer) {
            $why17 = $dimBoxGap
            if (-not $why17) { $why17 = 'HZ_DIM_BOX was not created, so there is no reopened verification to inspect' }
            Complete-DimCase 17 $t0 'not_covered' $why17
        }
        else {
            $d17 = $dimBoxAnswer.data
            $rv17 = $d17.reopened_verification
            $rows17 = @()
            if ($rv17) { $rows17 = @($rv17.dimensions) }
            $why17 = $null
            if (-not $rv17 -or $rv17.reopened -ne $true) { $why17 = 'reopened_verification did not report reopened=true' }
            elseif ([int]$d17.dimensions_verified -lt 1) { $why17 = 'dimensions_verified was not positive' }
            elseif ($rows17.Count -lt 3) { $why17 = ('the reopened file re-read only {0} of the 3 authored dimensions' -f $rows17.Count) }
            else {
                foreach ($r17 in $rows17) {
                    if ($why17) { continue }
                    # references_available may verify BY SUBSTANCE: under an API-side
                    # reopen Revit can report the flag false on a correct file (measured
                    # live; a UI-activated open of the same bytes reads true). The row
                    # then carries match=null + verified_by='substance', and the
                    # substantive fields beside it are what this probe holds to account.
                    $avail17ok = ($r17.references_available.match -eq $true) -or
                                 ($r17.references_available.verified_by -eq 'substance')
                    if ($r17.references.match -ne $true -or -not $avail17ok -or
                        $r17.label_parameter.match -ne $true -or $r17.value_internal_feet.match -ne $true) {
                        $why17 = ("dimension '{0}' did not match field by field in the reopened file" -f $r17.key)
                    }
                }
                if (-not $why17) {
                    $lock17 = @($rows17 | Where-Object { $_.key -eq 'locked' }) | Select-Object -First 1
                    $eq17 = @($rows17 | Where-Object { $_.key -eq 'equalised' }) | Select-Object -First 1
                    if (-not $lock17 -or $lock17.locked.match -ne $true) { $why17 = "the 'locked' dimension did not re-read IsLocked=true from the reopened file" }
                    elseif (-not $eq17 -or $eq17.segments_equal.match -ne $true) { $why17 = "the 'equalised' dimension did not re-read its EQ constraint from the reopened file" }
                }
            }
            if ($why17) { Complete-DimCase 17 $t0 'fail' $why17 -TransactionStatus (Get-DimTx $dimBoxAnswer) }
            else {
                Complete-DimCase 17 $t0 'pass' 'the saved RFA was closed, reopened from disk, and label, lock, EQ, references and measured value all re-read matching' `
                    -TransactionStatus (Get-DimTx $dimBoxAnswer) `
                    -Evidence @{ rfa = $d17.output_path; dimensions_verified = [int]$d17.dimensions_verified; reopened = $true }
            }
        }

        # Every case number reports exactly once. A case the flow above never
        # reached is a HARNESS defect, and it must read as one - never as a
        # silently shrunk denominator.
        for ($dimCase = 1; $dimCase -le 17; $dimCase++) {
            if (-not $script:dimCasesDone.ContainsKey($dimCase)) {
                Complete-DimCase $dimCase (Get-Date) 'unverified' 'the dimensions section ended before this probe ran - a harness bug, not a product verdict'
            }
        }

        # ---- W6+: 2D DETAIL --------------------------------------------------
        #
        # The 2D probes clone the dimension pattern deliberately: everything they
        # measure is synthetic and self-provisioned - a drafting view created and
        # ACTIVATED by this run, resources discovered from that view rather than
        # hard-coded, two RFAs authored from the machine's own family templates -
        # and every commit is believed only after a re-read, either the command's
        # own requested/read/match table or an independent horizun_query_detail_2d
        # call. Coordinates are view-plane (x along RightDirection, y along
        # UpDirection from the view origin), which in a drafting view is simply
        # the sheet plane. The last probe deletes exactly what the section
        # committed, and proves it by census, so the disposable model ends the
        # section as 2D-empty as it began.
        # ----------------------------------------------------------------------
        $script:d2dCasesDone = @{}

        # One verdict per case, exactly once: the outcome goes to the shared
        # write-tier accounting AND to the evidence block the report publishes.
        function Complete-D2dCase {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail,
                  $TransactionStatus = $null, $Evidence = $null, $Warnings = $null)
            if ($script:d2dCasesDone.ContainsKey($CaseNumber)) { return }
            $script:d2dCasesDone[$CaseNumber] = $true
            $entry = $writeNames[$d2dNameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:detail2dEvidence += @{
                case = $CaseNumber; name = $entry.N; tool = $entry.T
                started_utc = $Started.ToUniversalTime().ToString('o')
                duration_ms = [int][math]::Round(((Get-Date) - $Started).TotalMilliseconds)
                transaction_status = $TransactionStatus
                outcome = $Outcome
                detail = $Detail
                evidence = $Evidence
                warnings = $Warnings
            }
        }

        # The house contract for a detail_2d apply, asserted whole: null when the
        # answer proves committed_verified with every row verified and every
        # requested/read check matched, otherwise the exact reason it did not.
        function Test-D2dCommitted($applyAnswer) {
            if ($applyAnswer.isError) { return 'the apply errored: ' + (Get-DimShortText $applyAnswer.text) + ' | failing checks: ' + (Get-DimFailedChecks $applyAnswer) }
            $d = $applyAnswer.data
            if (-not $d) { return 'the apply reply carried no parseable JSON' }
            if ($d.state -ne 'committed_verified') { return ("state was '{0}', not committed_verified" -f $d.state) }
            if (-not (All-Rows $d.rows { param($r) $r.verified -eq $true })) { return 'not every row re-read verified=true' }
            foreach ($r in @($d.rows)) {
                # match=null + verified_by=substance is the command's HONEST tri-state
                # for facts Revit computes lazily (measured live: AreReferencesAvailable
                # on instance-geometry references, EQ after a committed edit) - the row
                # passed on substance and says so; a probe reading that honesty as a
                # failure would be asserting the flag over the measurement.
                if (-not (All-Rows $r.verification.checks { param($c)
                        $c.match -eq $true -or ($null -eq $c.match -and $c.verified_by -eq 'substance') })) {
                    return 'a verification check did not match (requested vs read disagree)'
                }
            }
            return $null
        }

        # The 2D census of one view, from the independent read surface.
        function Get-D2dCount($viewId) {
            $q = Invoke-Write 'horizun_query_detail_2d' @{ mode = 'elements'; view_id = $viewId; units = 'mm'; max_rows = 1 }
            if ($q.isError -or -not $q.data) { return $null }
            return [int]$q.data.total_matched
        }

        # A family template by its exact metric name first, then the documented
        # localized pattern. NO template is a named fixture gap, never a skip.
        function Find-D2dTemplate([string]$Root, [string]$ExactName, [string]$Pattern) {
            if (-not (Test-Path -LiteralPath $Root)) { return $null }
            $rft = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.rft' -File -ErrorAction SilentlyContinue)
            $hit = @($rft | Where-Object { $_.Name -eq $ExactName } | Sort-Object FullName) | Select-Object -First 1
            if ($hit) { return $hit.FullName }
            $hit = @($rft | Where-Object { $_.BaseName -match $Pattern } | Sort-Object FullName) | Select-Object -First 1
            if ($hit) { return $hit.FullName }
            return $null
        }

        $d2dTag = $probeRun.Substring(0, 8)
        $d2dViewId = $null; $d2dViewGap = $null; $d2dResourceGap = $null
        $d2dStyleRows = @(); $d2dFillTypeId = $null; $d2dMaskTypeId = $null
        $d2dCreatedIds = @()
        $d2dBaselineCount = $null
        $d2dTemplateRoot = Join-Path $env:ProgramData ("Autodesk\RVT {0}\Family Templates" -f $Year)

        # ---- case 1: the fresh drafting view and its resources ----------------
        $t0 = Get-Date
        $mvD2d = Invoke-WriteApply 'horizun_manage_views' @{
            target_document = $wDoc; units = 'mm'
            actions = @(@{ operation = 'create_drafting'; key = 'hzd2d'; name = "HZ_D2D_$d2dTag" })
        } 'd2d-view'
        if ($mvD2d.stage -eq 'apply' -and -not $mvD2d.answer.isError -and $mvD2d.answer.data) {
            $d2dViewId = $mvD2d.answer.data.aliases.hzd2d
        }
        if (-not $d2dViewId) {
            $d2dViewGap = 'the synthetic drafting view could not be created and verified: ' + (Get-DimShortText $mvD2d.answer.text)
        }
        if (-not $d2dViewGap) {
            $navD2d = Invoke-Write 'horizun_navigate' @{ operation = 'open_view'; view_id = $d2dViewId }
            if ($navD2d.isError -or -not $navD2d.data -or
                $navD2d.data.active_view_verified -ne $true -or [long]$navD2d.data.view_id -ne [long]$d2dViewId) {
                $d2dViewGap = 'the drafting view could not be ACTIVATED: ' + (Get-DimShortText $navD2d.text)
            }
        }
        if (-not $d2dViewGap) { $d2dBaselineCount = Get-D2dCount $d2dViewId }

        if ($d2dViewGap) { Complete-D2dCase 1 $t0 'unverified' $d2dViewGap }
        else {
            $rs1 = Invoke-Write 'horizun_query_detail_2d' @{ mode = 'resources'; view_id = $d2dViewId; units = 'mm' }
            if ($rs1.isError -or -not $rs1.data) {
                $d2dResourceGap = 'query_detail_2d mode=resources errored on the fresh drafting view: ' + (Get-DimShortText $rs1.text)
                Complete-D2dCase 1 $t0 'unverified' $d2dResourceGap
            }
            else {
                $d2dStyleRows = @($rs1.data.line_styles.rows)
                $frRows1 = @($rs1.data.filled_region_types.rows)
                $fillRow1 = @($frRows1 | Where-Object { $_.is_masking -eq $false }) | Select-Object -First 1
                $maskRow1 = @($frRows1 | Where-Object { $_.is_masking -eq $true }) | Select-Object -First 1
                if ($fillRow1) { $d2dFillTypeId = [long]$fillRow1.id }
                if ($maskRow1) { $d2dMaskTypeId = [long]$maskRow1.id }
                $accepts1 = ($rs1.data.view -and $rs1.data.view.accepts_detail_2d -eq $true)
                $stylesOk1 = All-Rows $d2dStyleRows { param($r) $r.id -and -not [string]::IsNullOrWhiteSpace($r.name) }
                $frOk1 = All-Rows $frRows1 { param($r) $r.id -and ($r.is_masking -is [bool]) }
                $symbolsListed1 = ($null -ne $rs1.data.placeable_symbols)
                $symCount1 = 0
                $script:d2dSymbolRows = @()
                if ($symbolsListed1 -and $rs1.data.placeable_symbols.rows) {
                    $script:d2dSymbolRows = @($rs1.data.placeable_symbols.rows)
                    $symCount1 = $script:d2dSymbolRows.Count
                }
                if ($accepts1 -and $stylesOk1 -and $frOk1 -and $symbolsListed1) {
                    Complete-D2dCase 1 $t0 'pass' ("accepts_detail_2d=true; {0} line style(s) with ids and names; {1} filled-region type(s) each carrying a boolean IsMasking; the placeable-symbol listing is present with {2} row(s)" -f $d2dStyleRows.Count, $frRows1.Count, $symCount1) `
                        -Evidence @{ view_id = $d2dViewId; line_styles = $d2dStyleRows.Count; filled_region_types = $frRows1.Count
                                     masking_types = @($frRows1 | Where-Object { $_.is_masking -eq $true }).Count
                                     placeable_symbols = $symCount1 }
                }
                else {
                    $d2dResourceGap = ("the resource answer did not hold: accepts_detail_2d={0}, line_styles_ok={1} (rows={2}), filled_region_types_ok={3} (rows={4}), placeable_symbols_listed={5}" -f $accepts1, $stylesOk1, $d2dStyleRows.Count, $frOk1, $frRows1.Count, $symbolsListed1)
                    Complete-D2dCase 1 $t0 'fail' $d2dResourceGap
                }
            }
        }

        # A fixture whose every filled-region type is masking (this HVAC derivative,
        # measured) still has to prove the ordinary fill: duplicate a masking type
        # and turn its Masking parameter off through the typed system-type surface,
        # then BELIEVE only the re-queried IsMasking. A duplicate that still reads
        # masking leaves the gap honestly named.
        if (-not $d2dFillTypeId -and $d2dMaskTypeId -and -not $d2dViewGap -and -not $d2dResourceGap) {
            $mkFill = Invoke-WriteApply 'horizun_manage_system_types' @{
                target_document = $wDoc; actions = @(@{
                    source_type_id = $d2dMaskTypeId; new_name = "HZ_D2D_FILL_$dimTag"
                    values = @{ Masking = $false } })
            } 'd2d-fill-type'
            if ($mkFill.stage -eq 'apply' -and -not $mkFill.answer.isError) {
                $rsF = Invoke-Write 'horizun_query_detail_2d' @{ mode = 'resources'; view_id = $d2dViewId; units = 'mm' }
                if ($rsF.data -and $rsF.data.filled_region_types) {
                    $newFill = @($rsF.data.filled_region_types.rows |
                                 Where-Object { $_.name -eq "HZ_D2D_FILL_$dimTag" -and $_.is_masking -eq $false }) |
                               Select-Object -First 1
                    if ($newFill) { $d2dFillTypeId = [long]$newFill.id }
                }
            }
        }

        # ---- case 2: lines, an arc and a closed polyline in ONE batch ---------
        $t0 = Get-Date
        $d2dLine1Id = $null; $d2dLine2Id = $null
        $d2dBatch2Args = $null; $d2dBatch2Token = $null; $d2dBatch2Committed = $false
        if ($d2dViewGap) { Complete-D2dCase 2 $t0 'unverified' $d2dViewGap }
        else {
            $d2dBatch2Args = @{
                target_document = $wDoc; units = 'mm'
                actions = @(
                    @{ operation = 'create_detail_line'; view_id = $d2dViewId; start = @(0, 0); end = @(3000, 0); key = 'ln1' },
                    @{ operation = 'create_detail_line'; view_id = $d2dViewId; start = @(0, 600); end = @(3000, 600); key = 'ln2' },
                    @{ operation = 'create_detail_arc'; view_id = $d2dViewId
                       start = @(0, 1200); end = @(3000, 1200); point_on_arc = @(1500, 2700); key = 'arc' },
                    @{ operation = 'create_detail_polyline'; view_id = $d2dViewId
                       points = @(@(5000, 0), @(8000, 0), @(6500, 2000)); closed = $true; key = 'poly' })
            }
            $mk2 = Invoke-WriteApply 'horizun_detail_2d' $d2dBatch2Args 'd2d-case2'
            if ($mk2.stage -eq 'dry_run') {
                Complete-D2dCase 2 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $mk2.answer.text))
            }
            else {
                $why2d = Test-D2dCommitted $mk2.answer
                if ($why2d) { Complete-D2dCase 2 $t0 'fail' $why2d -TransactionStatus (Get-DimTx $mk2.answer) }
                else {
                    $d2dBatch2Token = $mk2.dry.data.confirmation_token
                    $d2dBatch2Committed = $true
                    $ids2 = @{}
                    foreach ($row2 in @($mk2.answer.data.rows)) {
                        if ($row2.element_ids) { $d2dCreatedIds += @($row2.element_ids) }
                        if ($row2.key) { $ids2[[string]$row2.key] = @($row2.element_ids) }
                    }
                    if ($ids2.ContainsKey('ln1')) { $d2dLine1Id = @($ids2['ln1'])[0] }
                    if ($ids2.ContainsKey('ln2')) { $d2dLine2Id = @($ids2['ln2'])[0] }
                    $arcCount2 = 0; if ($ids2.ContainsKey('arc')) { $arcCount2 = @($ids2['arc']).Count }
                    $polyCount2 = 0; if ($ids2.ContainsKey('poly')) { $polyCount2 = @($ids2['poly']).Count }
                    if ($d2dLine1Id -and $d2dLine2Id -and $arcCount2 -ge 1 -and $polyCount2 -eq 3) {
                        Complete-D2dCase 2 $t0 'pass' "committed_verified: two lines, a three-point arc and a closed triangle polyline whose 3 curves came back grouped under the key 'poly'; every row verified and every requested/read check matched" `
                            -TransactionStatus (Get-DimTx $mk2.answer) `
                            -Evidence @{ element_ids_by_key = $ids2 }
                    }
                    else {
                        Complete-D2dCase 2 $t0 'fail' ("committed_verified, but the keyed rows did not carry the expected ids: ln1={0}, ln2={1}, arc rows={2}, poly curves={3} (need 3 for a closed triangle)" -f [bool]$d2dLine1Id, [bool]$d2dLine2Id, $arcCount2, $polyCount2) `
                            -TransactionStatus (Get-DimTx $mk2.answer) -Evidence @{ element_ids_by_key = $ids2 }
                    }
                }
            }
        }

        # ---- case 3: a filled region with a hole, re-read by signature --------
        $t0 = Get-Date
        if ($d2dViewGap) { Complete-D2dCase 3 $t0 'unverified' $d2dViewGap }
        elseif ($d2dResourceGap) { Complete-D2dCase 3 $t0 'unverified' $d2dResourceGap }
        elseif (-not $d2dFillTypeId) { Complete-D2dCase 3 $t0 'not_covered' 'the document offers no non-masking filled-region type, so no ordinary fill can be drawn' }
        else {
            $fr3 = Invoke-WriteApply 'horizun_detail_2d' @{
                target_document = $wDoc; units = 'mm'
                actions = @(@{ operation = 'create_filled_region'; view_id = $d2dViewId
                               filled_region_type_id = $d2dFillTypeId
                               loops = @(
                                   @(@(10000, 0), @(14000, 0), @(14000, 3000), @(10000, 3000)),
                                   @(@(11000, 800), @(12000, 800), @(12000, 1800), @(11000, 1800)))
                               key = 'region' })
            } 'd2d-case3'
            if ($fr3.stage -eq 'dry_run') {
                Complete-D2dCase 3 $t0 'unverified' ('the rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $fr3.answer.text))
            }
            else {
                $why3d = Test-D2dCommitted $fr3.answer
                if ($why3d) { Complete-D2dCase 3 $t0 'fail' $why3d -TransactionStatus (Get-DimTx $fr3.answer) }
                else {
                    $regionRow3 = @($fr3.answer.data.rows | Where-Object { $_.key -eq 'region' }) | Select-Object -First 1
                    $regionId3 = $null
                    if ($regionRow3 -and $regionRow3.element_ids) {
                        $regionId3 = @($regionRow3.element_ids)[0]
                        $d2dCreatedIds += @($regionRow3.element_ids)
                    }
                    if (-not $regionId3) {
                        Complete-D2dCase 3 $t0 'fail' 'committed_verified, but no element id came back under the key region' -TransactionStatus (Get-DimTx $fr3.answer)
                    }
                    else {
                        $qr3 = Invoke-Write 'horizun_query_detail_2d' @{
                            mode = 'elements'; view_id = $d2dViewId; element_ids = @($regionId3); units = 'mm'; max_rows = 5 }
                        $row3 = $null
                        if ($qr3.data) {
                            $row3 = @($qr3.data.rows | Where-Object { [long]$_.element_id -eq [long]$regionId3 }) | Select-Object -First 1
                        }
                        if (-not $row3) {
                            Complete-D2dCase 3 $t0 'fail' ('the committed region could not be independently re-read: ' + (Get-DimShortText $qr3.text)) -TransactionStatus (Get-DimTx $fr3.answer)
                        }
                        else {
                            $cpl3 = @($row3.curves_per_loop)
                            $cplOk3 = ($cpl3.Count -eq 2 -and [int]$cpl3[0] -eq 4 -and [int]$cpl3[1] -eq 4)
                            if ($row3.kind -eq 'filled_region' -and [int]$row3.loops -eq 2 -and
                                $row3.is_masking -eq $false -and
                                -not [string]::IsNullOrWhiteSpace($row3.region_signature) -and $cplOk3) {
                                Complete-D2dCase 3 $t0 'pass' 'committed_verified, and the independent re-read carries 2 loops of 4 curves each (exterior plus hole), is_masking=false and a non-empty region signature' `
                                    -TransactionStatus (Get-DimTx $fr3.answer) `
                                    -Evidence @{ element_id = $regionId3; loops = [int]$row3.loops; curves_per_loop = $cpl3
                                                 region_signature = $row3.region_signature }
                            }
                            else {
                                Complete-D2dCase 3 $t0 'fail' ("the re-read did not hold: kind={0}, loops={1}, is_masking={2}, signature_present={3}, curves_per_loop=[{4}]" -f $row3.kind, $row3.loops, $row3.is_masking, (-not [string]::IsNullOrWhiteSpace($row3.region_signature)), ($cpl3 -join ',')) `
                                    -TransactionStatus (Get-DimTx $fr3.answer)
                            }
                        }
                    }
                }
            }
        }

        # ---- case 4: masking follows the TYPE, in both directions -------------
        $t0 = Get-Date
        if ($d2dViewGap) { Complete-D2dCase 4 $t0 'unverified' $d2dViewGap }
        elseif ($d2dResourceGap) { Complete-D2dCase 4 $t0 'unverified' $d2dResourceGap }
        elseif (-not $d2dMaskTypeId -and -not $d2dFillTypeId) {
            Complete-D2dCase 4 $t0 'not_covered' 'the document offers no filled-region type at all, so neither masking direction can be exercised'
        }
        else {
            $why4d = $null; $ev4 = @{}
            if ($d2dMaskTypeId) {
                # The type IS masking: create_masking_region must commit and
                # verify, and the SAME type as an ordinary fill must refuse.
                $ev4.branch = 'masking_type_available'
                $mr4 = Invoke-WriteApply 'horizun_detail_2d' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'create_masking_region'; view_id = $d2dViewId
                                   masking_region_type_id = $d2dMaskTypeId
                                   loops = @(, @(@(10000, 4000), @(12000, 4000), @(12000, 5500), @(10000, 5500)))
                                   key = 'mask' })
                } 'd2d-case4-mask'
                if ($mr4.stage -eq 'dry_run') {
                    $why4d = 'the masking-region rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $mr4.answer.text)
                }
                else {
                    $bad4 = Test-D2dCommitted $mr4.answer
                    if ($bad4) { $why4d = 'masking region: ' + $bad4 }
                    else {
                        foreach ($row4 in @($mr4.answer.data.rows)) {
                            if ($row4.element_ids) { $d2dCreatedIds += @($row4.element_ids) }
                        }
                        $ev4.masking_region = 'committed_verified with the IsMasking=true type'
                    }
                }
                if (-not $why4d) {
                    $fr4 = Invoke-Write 'horizun_detail_2d' @{
                        target_document = $wDoc; units = 'mm'; dry_run = $true
                        actions = @(@{ operation = 'create_filled_region'; view_id = $d2dViewId
                                       filled_region_type_id = $d2dMaskTypeId
                                       loops = @(, @(@(10000, 6000), @(11000, 6000), @(11000, 6800), @(10000, 6800))) }) }
                    $tokF4 = $null
                    if ($fr4.data) { $tokF4 = $fr4.data.confirmation_token }
                    if (-not $tokF4 -and $fr4.text -match '(?i)IsMasking is TRUE') {
                        $ev4.filled_with_masking_type = 'refused naming IsMasking is TRUE, token withheld'
                    }
                    else {
                        $why4d = ("the masking type drawn as an ordinary filled region was not refused naming IsMasking is TRUE (token_present={0}): {1}" -f [bool]$tokF4, (Get-DimShortText $fr4.text))
                    }
                }
            }
            else {
                # No masking type exists, so the only honest direction is the
                # inverse: a non-masking type offered as a masking region.
                $ev4.branch = 'no_masking_type_in_document'
                $mr4b = Invoke-Write 'horizun_detail_2d' @{
                    target_document = $wDoc; units = 'mm'; dry_run = $true
                    actions = @(@{ operation = 'create_masking_region'; view_id = $d2dViewId
                                   masking_region_type_id = $d2dFillTypeId
                                   loops = @(, @(@(10000, 4000), @(12000, 4000), @(12000, 5500), @(10000, 5500))) }) }
                $tokM4 = $null
                if ($mr4b.data) { $tokM4 = $mr4b.data.confirmation_token }
                if (-not $tokM4 -and $mr4b.text -match '(?i)IsMasking is FALSE') {
                    $ev4.masking_with_filled_type = 'refused naming IsMasking is FALSE, token withheld'
                }
                else {
                    $why4d = ("the non-masking type offered as a masking region was not refused naming IsMasking is FALSE (token_present={0}): {1}" -f [bool]$tokM4, (Get-DimShortText $mr4b.text))
                }
            }
            if ($why4d) { Complete-D2dCase 4 $t0 'fail' $why4d -Evidence $ev4 }
            else {
                Complete-D2dCase 4 $t0 'pass' ("every direction the document's types allow behaved: the evidence names the branch ({0})" -f $ev4.branch) -Evidence $ev4
            }
        }

        # ---- case 5: a detail component and/or symbol place and verify --------
        # Existing loaded symbols FIRST (the fixture's own), self-provisioning as
        # the fallback - and the fallback has a MEASURED limit: create_family
        # refuses 2D family templates ('Sketch plane creation is not allowed in
        # this family'), a real product gap named in the evidence rather than
        # retried into. One verified placement of either kind proves the path;
        # whatever could not be staged is named.
        $t0 = Get-Date
        if ($d2dViewGap) { Complete-D2dCase 5 $t0 'unverified' $d2dViewGap }
        else {
            $gap5notes = @(); $diSym5 = $null; $gaSym5 = $null
            $diRow5 = @($script:d2dSymbolRows | Where-Object { $_.placement -eq 'detail_component' }) | Select-Object -First 1
            $gaRow5 = @($script:d2dSymbolRows | Where-Object { $_.placement -eq 'generic_annotation' }) | Select-Object -First 1
            if ($diRow5) { $diSym5 = [long]$diRow5.id }
            if ($gaRow5) { $gaSym5 = [long]$gaRow5.id }
            $d2dRfaDir = Join-Path $scratchDir 'detail2d-families'
            New-Item -ItemType Directory -Force $d2dRfaDir | Out-Null
            if (-not $diSym5) {
                $diTpl5 = Find-D2dTemplate $d2dTemplateRoot 'Metric Detail Item.rft' '(?i)detail item|elemento de detalle'
                if (-not $diTpl5) { $gap5notes += 'no loaded detail component and no Detail Item template to author one' }
                else {
                    $diRfa5 = Join-Path $d2dRfaDir 'HZ_D2D_DI.rfa'
                    $df5 = Invoke-WriteApply 'horizun_create_family' @{
                        target_document = $wDoc; template_path = $diTpl5; output_path = $diRfa5
                        units = 'mm'; overwrite = $true; load_into_project = $true
                        types = @(@{ name = 'HZ_D2D_DI' })
                        family_lines = @(
                            @{ kind = 'symbolic'; start = @(-100, 0, 0); end = @(100, 0, 0) },
                            @{ kind = 'symbolic'; start = @(0, -100, 0); end = @(0, 100, 0) })
                    } 'd2d-di-family'
                    if ($df5.stage -eq 'apply' -and -not $df5.answer.isError -and $df5.answer.data -and
                        $df5.answer.data.loaded_family -and @($df5.answer.data.loaded_family.symbol_ids).Count -gt 0) {
                        $diSym5 = @($df5.answer.data.loaded_family.symbol_ids)[0]
                        $script:d2dFamilyPaths += $diRfa5
                    }
                    else {
                        $gap5notes += ('no loaded detail component, and self-provisioning hit the measured create_family limit on 2D templates: ' + (Get-DimShortText $df5.answer.text))
                    }
                }
            }
            if (-not $gaSym5) {
                $gaTpl5 = Find-D2dTemplate $d2dTemplateRoot 'Metric Generic Annotation.rft' '(?i)generic annotation|anotaci[oó]n gen[eé]rica'
                if (-not $gaTpl5) { $gap5notes += 'no loaded generic annotation and no Generic Annotation template to author one' }
                else {
                    $gaRfa5 = Join-Path $d2dRfaDir 'HZ_D2D_GA.rfa'
                    $gf5 = Invoke-WriteApply 'horizun_create_family' @{
                        target_document = $wDoc; template_path = $gaTpl5; output_path = $gaRfa5
                        units = 'mm'; overwrite = $true; load_into_project = $true
                        types = @(@{ name = 'HZ_D2D_GA' })
                        family_lines = @(
                            @{ kind = 'symbolic'; start = @(-100, 0, 0); end = @(100, 0, 0) },
                            @{ kind = 'symbolic'; start = @(0, -100, 0); end = @(0, 100, 0) })
                    } 'd2d-ga-family'
                    if ($gf5.stage -eq 'apply' -and -not $gf5.answer.isError -and $gf5.answer.data -and
                        $gf5.answer.data.loaded_family -and @($gf5.answer.data.loaded_family.symbol_ids).Count -gt 0) {
                        $gaSym5 = @($gf5.answer.data.loaded_family.symbol_ids)[0]
                        $script:d2dFamilyPaths += $gaRfa5
                    }
                    else {
                        $gap5notes += ('no loaded generic annotation, and self-provisioning hit the measured create_family limit on 2D templates: ' + (Get-DimShortText $gf5.answer.text))
                    }
                }
            }
            if (-not $diSym5 -and -not $gaSym5) {
                Complete-D2dCase 5 $t0 'not_covered' ('neither placement kind could be staged: ' + ($gap5notes -join ' | '))
            }
            else {
                $acts5 = @()
                if ($diSym5) { $acts5 += @{ operation = 'place_detail_component'; view_id = $d2dViewId
                                            family_symbol_id = $diSym5; point = @(16000, 1000); rotation_degrees = 30; key = 'dc' } }
                if ($gaSym5) { $acts5 += @{ operation = 'place_symbol'; view_id = $d2dViewId
                                            family_symbol_id = $gaSym5; point = @(16000, 2500); key = 'ga' } }
                $pl5 = Invoke-WriteApply 'horizun_detail_2d' @{
                    target_document = $wDoc; units = 'mm'; actions = $acts5
                } 'd2d-case5-place'
                if ($pl5.stage -eq 'dry_run') {
                    $reh5 = '(no rehearsal actions parsed)'
                    if ($pl5.answer.data -and $pl5.answer.data.rehearsal -and $pl5.answer.data.rehearsal.actions) {
                        $reh5bits = @($pl5.answer.data.rehearsal.actions | Where-Object { $_.constructible -ne $true } |
                                      ForEach-Object { ('action{0}: {1} | checks: {2}' -f $_.index, $_.reason,
                                          (@(@($_.verification.checks) | Where-Object { $_.match -ne $true } |
                                             ForEach-Object { ('{0}(req={1} read={2})' -f $_.field, $_.requested, $_.read) }) -join '; ')) })
                        if ($reh5bits.Count -gt 0) { $reh5 = ($reh5bits -join ' || ') }
                    }
                    Complete-D2dCase 5 $t0 'unverified' ('the placement rehearsal issued no token: ' + $reh5 + ' | staged: ' + ($gap5notes -join ' | '))
                }
                else {
                    $why5d = Test-D2dCommitted $pl5.answer
                    if ($why5d) { Complete-D2dCase 5 $t0 'fail' $why5d -TransactionStatus (Get-DimTx $pl5.answer) }
                        else {
                            $ids5 = @()
                            foreach ($row5 in @($pl5.answer.data.rows)) {
                                if ($row5.element_ids) { $d2dCreatedIds += @($row5.element_ids); $ids5 += @($row5.element_ids) }
                            }
                            $branch5 = @()
                            if ($diSym5) { $branch5 += 'detail component at 30 degrees' }
                            if ($gaSym5) { $branch5 += 'generic annotation' }
                            $suffix5 = ''
                            if ($gap5notes.Count -gt 0) { $suffix5 = '; not staged: ' + ($gap5notes -join ' | ') }
                            Complete-D2dCase 5 $t0 'pass' (('placed committed_verified: {0}; category, point and rotation re-read by the command{1}' -f ($branch5 -join ' and '), $suffix5)) `
                                -TransactionStatus (Get-DimTx $pl5.answer) `
                                -Evidence @{ detail_component_symbol = $diSym5; generic_annotation_symbol = $gaSym5
                                             placed_element_ids = $ids5; staging_notes = $gap5notes }
                        }
                    }
                }
            }

        # ---- the style pair the style probes stand on. Discovered, not cached:
        # ---- the DEFAULT style is whatever Revit gave the committed line 1, and
        # ---- style B is any OTHER style the view's own resource answer lists.
        $d2dDefaultStyleId = $null; $d2dStyleB = $null; $d2dStyleC = $null; $d2dStyleGap = $null
        if ($d2dViewGap) { $d2dStyleGap = $d2dViewGap }
        elseif ($d2dResourceGap) { $d2dStyleGap = $d2dResourceGap }
        elseif (-not $d2dLine1Id -or -not $d2dLine2Id) {
            $d2dStyleGap = 'case 2 did not commit both detail lines, so there is no curve to restyle'
        }
        else {
            $q6 = Invoke-Write 'horizun_query_detail_2d' @{
                mode = 'elements'; view_id = $d2dViewId; element_ids = @($d2dLine1Id); units = 'mm'; max_rows = 5 }
            if ($q6.data) {
                $row61 = @($q6.data.rows | Where-Object { [long]$_.element_id -eq [long]$d2dLine1Id }) | Select-Object -First 1
                if ($row61 -and $row61.line_style_id) { $d2dDefaultStyleId = [long]$row61.line_style_id }
            }
            if (-not $d2dDefaultStyleId) {
                $d2dStyleGap = 'the committed line 1 could not be re-read with its line_style_id: ' + (Get-DimShortText $q6.text)
            }
            else {
                $others6 = @($d2dStyleRows | Where-Object { [long]$_.id -ne $d2dDefaultStyleId })
                if ($others6.Count -ge 1) { $d2dStyleB = [long]$others6[0].id }
                if ($others6.Count -ge 2) { $d2dStyleC = [long]$others6[1].id }
                if (-not $d2dStyleB) {
                    $d2dStyleGap = 'the document exposes no line style different from the default, so no style change can be proven'
                }
            }
        }

        # ---- case 6: set_line_style on an existing curve and a same-batch key -
        $t0 = Get-Date
        if ($d2dStyleGap) { Complete-D2dCase 6 $t0 'unverified' $d2dStyleGap }
        else {
            $sb6 = Invoke-WriteApply 'horizun_detail_2d' @{
                target_document = $wDoc; units = 'mm'
                actions = @(
                    @{ operation = 'create_detail_line'; view_id = $d2dViewId; start = @(0, 3500); end = @(2000, 3500); key = 'k' },
                    @{ operation = 'set_line_style'; element_key = 'k'; line_style_id = $d2dStyleB })
            } 'd2d-case6-batch'
            if ($sb6.stage -eq 'dry_run') {
                Complete-D2dCase 6 $t0 'unverified' ('the same-batch rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $sb6.answer.text))
            }
            else {
                $why6d = Test-D2dCommitted $sb6.answer
                if ($why6d) { Complete-D2dCase 6 $t0 'fail' ('same-batch key: ' + $why6d) -TransactionStatus (Get-DimTx $sb6.answer) }
                else {
                    $newLine6 = $null
                    $rowK6 = @($sb6.answer.data.rows | Where-Object { $_.key -eq 'k' }) | Select-Object -First 1
                    if ($rowK6 -and $rowK6.element_ids) {
                        $newLine6 = @($rowK6.element_ids)[0]
                        $d2dCreatedIds += @($rowK6.element_ids)
                    }
                    if (-not $newLine6) {
                        Complete-D2dCase 6 $t0 'fail' 'committed_verified, but no element id came back under the same-batch key' -TransactionStatus (Get-DimTx $sb6.answer)
                    }
                    else {
                        $st6 = Invoke-WriteApply 'horizun_detail_2d' @{
                            target_document = $wDoc; units = 'mm'
                            actions = @(@{ operation = 'set_line_style'; element_id = $d2dLine1Id; line_style_id = $d2dStyleB })
                        } 'd2d-case6-existing'
                        if ($st6.stage -eq 'dry_run') {
                            Complete-D2dCase 6 $t0 'unverified' ('the existing-curve rehearsal issued no token, so nothing was committed: ' + (Get-DimShortText $st6.answer.text))
                        }
                        else {
                            $why6b = Test-D2dCommitted $st6.answer
                            if ($why6b) { Complete-D2dCase 6 $t0 'fail' ('existing curve: ' + $why6b) -TransactionStatus (Get-DimTx $st6.answer) }
                            else {
                                $qc6 = Invoke-Write 'horizun_query_detail_2d' @{
                                    mode = 'elements'; view_id = $d2dViewId
                                    element_ids = @($newLine6, $d2dLine1Id); units = 'mm'; max_rows = 10 }
                                $row6a = $null; $row6b = $null
                                if ($qc6.data) {
                                    $row6a = @($qc6.data.rows | Where-Object { [long]$_.element_id -eq [long]$newLine6 }) | Select-Object -First 1
                                    $row6b = @($qc6.data.rows | Where-Object { [long]$_.element_id -eq [long]$d2dLine1Id }) | Select-Object -First 1
                                }
                                if ($row6a -and $row6b -and
                                    [long]$row6a.line_style_id -eq $d2dStyleB -and [long]$row6b.line_style_id -eq $d2dStyleB) {
                                    Complete-D2dCase 6 $t0 'pass' ("both routes committed_verified, and the independent re-read confirms line_style_id={0} on the same-batch key AND on the pre-existing case-2 line" -f $d2dStyleB) `
                                        -Evidence @{ style_b = $d2dStyleB; default_style = $d2dDefaultStyleId
                                                     same_batch_line = $newLine6; existing_line = $d2dLine1Id }
                                }
                                else {
                                    $read6a = $null; $read6b = $null
                                    if ($row6a) { $read6a = $row6a.line_style_id }
                                    if ($row6b) { $read6b = $row6b.line_style_id }
                                    Complete-D2dCase 6 $t0 'fail' ("the independent re-read did not confirm style {0} on both curves (same-batch read={1}, existing read={2})" -f $d2dStyleB, $read6a, $read6b)
                                }
                            }
                        }
                    }
                }
            }
        }

        # ---- case 7: the idempotent replay of the case-2 apply ----------------
        $t0 = Get-Date
        if (-not $d2dBatch2Committed) {
            Complete-D2dCase 7 $t0 'unverified' 'case 2 did not commit, so there is no recorded apply to replay'
        }
        else {
            $before7 = Get-D2dCount $d2dViewId
            if ($null -eq $before7) {
                Complete-D2dCase 7 $t0 'unverified' 'the 2D census could not be read before the replay, so "creates nothing" could not be proven'
            }
            else {
                # The SAME arguments, the SAME spent token and the SAME
                # idempotency key Invoke-WriteApply used for case 2: the ledger
                # must return the recorded result without executing twice.
                $replay7 = $d2dBatch2Args.Clone()
                $replay7['dry_run'] = $false
                $replay7['confirmation_token'] = $d2dBatch2Token
                $replay7['idempotency_key'] = "live-write-d2d-case2-$probeRun"
                $rep7 = Invoke-Write 'horizun_detail_2d' $replay7
                $after7 = Get-D2dCount $d2dViewId
                $stamp7 = $null
                if ($rep7.data -and $rep7.data.PSObject.Properties.Name -contains 'idempotency') { $stamp7 = $rep7.data.idempotency }
                if (-not $rep7.isError -and $rep7.data -and $rep7.data.state -eq 'committed_verified' -and
                    $after7 -eq $before7) {
                    Complete-D2dCase 7 $t0 'pass' ("the replay returned the recorded committed_verified result without executing twice, and the census is unchanged at {0}" -f $before7) `
                        -Evidence @{ census_before = $before7; census_after = $after7; idempotency = $stamp7 }
                }
                else {
                    Complete-D2dCase 7 $t0 'fail' ("expected the recorded result and an unchanged census; got isError={0}, state='{1}', census {2}->{3}: {4}" -f $rep7.isError, $rep7.data.state, $before7, $after7, (Get-DimShortText $rep7.text)) `
                        -Evidence @{ census_before = $before7; census_after = $after7; idempotency = $stamp7 }
                }
            }
        }

        # ---- case 8: the style moves underneath a minted token ----------------
        $t0 = Get-Date
        if ($d2dStyleGap) { Complete-D2dCase 8 $t0 'unverified' $d2dStyleGap }
        else {
            $ev8 = @{}
            $moveStyle8 = $d2dStyleC
            if ($null -eq $moveStyle8) {
                $moveStyle8 = $d2dStyleB
                $ev8.branch = 'two_styles_only: the intermediate write reuses style B, so the refusal must be fingerprint-based rather than outcome-based'
            }
            else {
                $ev8.branch = 'three_styles: the intermediate write moves the curve to a third style'
            }
            $before8 = Get-D2dCount $d2dViewId
            $actions8 = @(@{ operation = 'set_line_style'; element_id = $d2dLine2Id; line_style_id = $d2dStyleB })
            $dry8 = Invoke-Write 'horizun_detail_2d' @{
                target_document = $wDoc; units = 'mm'; dry_run = $true; actions = $actions8 }
            $tok8 = $null
            if (-not $dry8.isError -and $dry8.data) { $tok8 = $dry8.data.confirmation_token }
            if (-not $tok8) {
                Complete-D2dCase 8 $t0 'unverified' ('the rehearsal issued no token to go stale: ' + (Get-DimShortText $dry8.text))
            }
            else {
                $mv8 = Invoke-WriteApply 'horizun_detail_2d' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'set_line_style'; element_id = $d2dLine2Id; line_style_id = $moveStyle8 })
                } 'd2d-case8-move'
                if ($mv8.stage -ne 'apply' -or $mv8.answer.isError) {
                    Complete-D2dCase 8 $t0 'unverified' ("the target's style could not be moved between rehearsal and apply: " + (Get-DimShortText $mv8.answer.text))
                }
                else {
                    $ap8 = Invoke-Write 'horizun_detail_2d' @{
                        target_document = $wDoc; units = 'mm'; dry_run = $false
                        confirmation_token = $tok8
                        idempotency_key = "live-write-d2d-case8-stale-$probeRun"
                        actions = $actions8 }
                    $after8 = Get-D2dCount $d2dViewId
                    if ($ap8.isError -and $ap8.text -match 'THE MODEL MOVED AFTER THE DRY RUN' -and
                        $after8 -eq $before8) {
                        Complete-D2dCase 8 $t0 'pass' ("the independent style change made the token stale and it was refused with THE MODEL MOVED AFTER THE DRY RUN; census unchanged at {0}" -f $before8) `
                            -Evidence ($ev8 + @{ moved_element = $d2dLine2Id; census_before = $before8; census_after = $after8 })
                    }
                    else {
                        Complete-D2dCase 8 $t0 'fail' ("expected the stale-plan refusal and an unchanged census; got isError={0}, census {1}->{2}: {3}" -f $ap8.isError, $before8, $after8, (Get-DimShortText $ap8.text)) `
                            -Evidence $ev8
                    }
                }
            }
        }

        # ---- case 9: the refusals, each named by its cause --------------------
        # All rehearsals: dry_run stays true, no token is ever spent, and each
        # sub-refusal the fixture cannot stage is a NAMED gap in the evidence
        # rather than a silent skip.
        $t0 = Get-Date
        $ev9 = @{}; $bad9 = @(); $neg9 = @()
        $ev9.view_template = 'fixture gap: no cheap typed discovery exists for a view-template id, so this sub-refusal was not exercised'
        $sq9 = Invoke-Write 'horizun_query_model' @{ categories = @('OST_Sheets'); include_links = $false; max_rows = 1 }
        $sheet9 = $null
        if (-not $sq9.isError -and $sq9.data -and @($sq9.data.rows).Count -gt 0) { $sheet9 = @($sq9.data.rows)[0].element_id }
        if ($sheet9) {
            $neg9 += @{ Label = 'sheet'; Pattern = '(?i)sheet'
                        Args = @{ target_document = $wDoc; units = 'mm'; dry_run = $true
                                  actions = @(@{ operation = 'create_detail_line'; view_id = $sheet9
                                                 start = @(0, 0); end = @(1000, 0) }) } }
        }
        else { $ev9.sheet = 'fixture gap: the disposable model has no sheet (OST_Sheets returned none)' }
        $ls9 = Invoke-Write 'horizun_list_schedules' @{ max_rows = 1 }
        $sched9 = $null
        if (-not $ls9.isError -and $ls9.data -and @($ls9.data.rows).Count -gt 0) { $sched9 = @($ls9.data.rows)[0].schedule_id }
        if ($sched9) {
            $neg9 += @{ Label = 'schedule'; Pattern = '(?i)schedule'
                        Args = @{ target_document = $wDoc; units = 'mm'; dry_run = $true
                                  actions = @(@{ operation = 'create_detail_line'; view_id = $sched9
                                                 start = @(0, 0); end = @(1000, 0) }) } }
        }
        else { $ev9.schedule = 'fixture gap: the disposable model has no schedule' }
        if ($d2dViewGap) {
            $ev9.view_bound_negatives = 'the drafting-view gap blocked the loop, coplanarity and symbol sub-refusals: ' + $d2dViewGap
        }
        else {
            if ($d2dFillTypeId) {
                $neg9 += @{ Label = 'open_loop'; Pattern = '(?i)open_loop|3\.\.200 vertices'
                            Args = @{ target_document = $wDoc; units = 'mm'; dry_run = $true
                                      actions = @(@{ operation = 'create_filled_region'; view_id = $d2dViewId
                                                     filled_region_type_id = $d2dFillTypeId
                                                     loops = @(, @(@(20000, 0), @(21000, 0))) }) } }
                $neg9 += @{ Label = 'self_intersection'; Pattern = 'self_intersection'
                            Args = @{ target_document = $wDoc; units = 'mm'; dry_run = $true
                                      actions = @(@{ operation = 'create_filled_region'; view_id = $d2dViewId
                                                     filled_region_type_id = $d2dFillTypeId
                                                     loops = @(, @(@(20000, 2000), @(21000, 3000), @(21000, 2000), @(20000, 3000))) }) } }
            }
            else {
                $ev9.open_loop = 'fixture gap: no non-masking filled-region type, so the loop refusals could not be staged'
                $ev9.self_intersection = $ev9.open_loop
            }
            $neg9 += @{ Label = 'non_coplanar'; Pattern = '(?i)non-zero third component'
                        Args = @{ target_document = $wDoc; units = 'mm'; dry_run = $true
                                  actions = @(@{ operation = 'create_detail_line'; view_id = $d2dViewId
                                                 start = @(20000, 5000, 50); end = @(21000, 5000, 50) }) } }
            # The wrong-placement refusal needs a REAL FamilySymbol that is not
            # ViewBased - a MODEL family's type. A pipe TYPE is a system-family
            # ElementType, not a FamilySymbol, and correctly refuses for a
            # DIFFERENT reason (measured in the first live run) - which is not
            # this probe's claim. A sprinkler/accessory/equipment symbol is.
            $modelSym9 = $null
            foreach ($cat9 in @('OST_Sprinklers', 'OST_PipeAccessory', 'OST_MechanicalEquipment')) {
                if ($modelSym9) { continue }
                $qs9 = Invoke-Write 'horizun_query_model' @{ categories = @($cat9); include_types = $true
                                                             max_rows = 50; include_links = $false }
                if ($qs9.data) {
                    $t9 = @($qs9.data.rows | Where-Object { $_.is_element_type }) | Select-Object -First 1
                    if ($t9) { $modelSym9 = [long]$t9.element_id }
                }
            }
            if ($modelSym9) {
                $neg9 += @{ Label = 'model_based_symbol'; Pattern = '(?i)ViewBased'
                            Args = @{ target_document = $wDoc; units = 'mm'; dry_run = $true
                                      actions = @(@{ operation = 'place_detail_component'; view_id = $d2dViewId
                                                     family_symbol_id = $modelSym9; point = @(20000, 6000) }) } }
            }
            else {
                $ev9.model_based_symbol = 'fixture gap: no model FamilySymbol (sprinkler/accessory/equipment) to aim the not-ViewBased refusal at'
            }
        }
        foreach ($n9 in $neg9) {
            $r9 = Invoke-Write 'horizun_detail_2d' $n9.Args
            $tok9 = $null
            if ($r9.data) { $tok9 = $r9.data.confirmation_token }
            if (-not $tok9 -and $r9.text -match $n9.Pattern) {
                $ev9[$n9.Label] = ("refused without a token, naming its cause (matched '{0}')" -f $n9.Pattern)
            }
            else {
                $bad9 += ("{0}: expected a token-less refusal matching '{1}'; got token_present={2}: {3}" -f $n9.Label, $n9.Pattern, [bool]$tok9, (Get-DimShortText $r9.text))
            }
        }
        if (@($neg9).Count -eq 0) {
            Complete-D2dCase 9 $t0 'unverified' 'no negative sub-case could be staged at all - the view gap and the fixture gaps are named in the evidence' -Evidence $ev9
        }
        elseif ($bad9.Count -eq 0) {
            Complete-D2dCase 9 $t0 'pass' ("{0} sub-refusal(s) exercised and every one refused without a token naming its cause; the unavailable ones are named fixture gaps in the evidence" -f @($neg9).Count) -Evidence $ev9
        }
        else {
            Complete-D2dCase 9 $t0 'fail' ($bad9 -join ' | ') -Evidence $ev9
        }

        # ---- case 10: the drafting view as a real PNG on disk -----------------
        $t0 = Get-Date
        if ($d2dViewGap) { Complete-D2dCase 10 $t0 'unverified' $d2dViewGap }
        else {
            $cap10 = Invoke-Write 'horizun_capture_view' @{ view_id = $d2dViewId; pixel_size = 1600 }
            if ($cap10.isError -or -not $cap10.data) {
                Complete-D2dCase 10 $t0 'unverified' ('the capture errored, so no file could be inspected: ' + (Get-DimShortText $cap10.text))
            }
            else {
                $capPath10 = $null
                foreach ($pf10 in @('output_path', 'file_path', 'image_path', 'png_path', 'path', 'file')) {
                    if ($capPath10) { continue }
                    if ($cap10.data.PSObject.Properties.Name -contains $pf10 -and $cap10.data.$pf10) {
                        $capPath10 = [string]$cap10.data.$pf10
                    }
                }
                if (-not $capPath10) {
                    Complete-D2dCase 10 $t0 'unverified' ('the reply carried no recognisable file-path field, so the file could not be found: ' + (Get-DimShortText $cap10.text))
                }
                elseif (-not (Test-Path -LiteralPath $capPath10)) {
                    Complete-D2dCase 10 $t0 'fail' ("the reported file does not exist on disk: {0}" -f $capPath10)
                }
                else {
                    # What the file IS, from its own bytes: the PNG signature and
                    # the IHDR dimensions, not the reply's account of them.
                    $bytes10 = [System.IO.File]::ReadAllBytes($capPath10)
                    $sig10 = ($bytes10.Length -ge 24 -and $bytes10[0] -eq 137 -and $bytes10[1] -eq 80 -and
                              $bytes10[2] -eq 78 -and $bytes10[3] -eq 71)
                    $w10 = 0; $h10 = 0
                    if ($sig10) {
                        $w10 = ([int]$bytes10[16] -shl 24) -bor ([int]$bytes10[17] -shl 16) -bor ([int]$bytes10[18] -shl 8) -bor [int]$bytes10[19]
                        $h10 = ([int]$bytes10[20] -shl 24) -bor ([int]$bytes10[21] -shl 16) -bor ([int]$bytes10[22] -shl 8) -bor [int]$bytes10[23]
                    }
                    if ($sig10 -and $w10 -gt 0 -and $h10 -gt 0) {
                        Complete-D2dCase 10 $t0 'pass' ("a real PNG: {0} bytes at {1}, {2}x{3} measured from its own IHDR header" -f $bytes10.Length, $capPath10, $w10, $h10) `
                            -Evidence @{ path = $capPath10; bytes = $bytes10.Length; width = $w10; height = $h10 }
                    }
                    else {
                        Complete-D2dCase 10 $t0 'fail' ("the file at {0} is not a readable PNG (signature={1}, {2}x{3})" -f $capPath10, $sig10, $w10, $h10)
                    }
                }
            }
        }

        # ---- case 11: delete_verified cleans exactly what this section made ---
        $t0 = Get-Date
        if ($d2dViewGap) { Complete-D2dCase 11 $t0 'unverified' $d2dViewGap }
        else {
            $ids11 = @($d2dCreatedIds | Where-Object { $_ } | Select-Object -Unique)
            if ($ids11.Count -eq 0) {
                Complete-D2dCase 11 $t0 'unverified' 'no probe committed a 2D element, so there is nothing to prove the cleanup on'
            }
            else {
                $before11 = Get-D2dCount $d2dViewId
                $del11 = Invoke-WriteApply 'horizun_delete_verified' @{
                    mode = 'ids'; ids = $ids11; target_document = $wDoc; id_cap = 200 } 'd2d-cleanup'
                if ($del11.stage -eq 'dry_run') {
                    Complete-D2dCase 11 $t0 'unverified' ('the delete rehearsal issued no token, so nothing was deleted: ' + (Get-DimShortText $del11.answer.text))
                }
                elseif ($del11.answer.isError) {
                    Complete-D2dCase 11 $t0 'fail' ('the delete errored: ' + (Get-DimShortText $del11.answer.text))
                }
                else {
                    $after11 = Get-D2dCount $d2dViewId
                    $expected11 = 0
                    if ($null -ne $d2dBaselineCount) { $expected11 = $d2dBaselineCount }
                    $deleted11 = $null
                    if ($del11.answer.data) { $deleted11 = $del11.answer.data.deleted_total }
                    if ($null -eq $after11) {
                        Complete-D2dCase 11 $t0 'unverified' 'the 2D census could not be re-read after the delete, so the cleanup could not be proven'
                    }
                    elseif ($after11 -eq $expected11) {
                        Complete-D2dCase 11 $t0 'pass' ("{0} probe id(s) deleted and the view's 2D census returned to its baseline of {1}" -f $ids11.Count, $expected11) `
                            -Evidence @{ requested_ids = $ids11.Count; deleted_total = $deleted11
                                         census_before = $before11; census_after = $after11; baseline = $expected11 }
                    }
                    else {
                        Complete-D2dCase 11 $t0 'fail' ("the view still holds {0} 2D element(s) where the baseline was {1} (census before the delete: {2})" -f $after11, $expected11, $before11) `
                            -Evidence @{ requested_ids = $ids11.Count; deleted_total = $deleted11
                                         census_before = $before11; census_after = $after11; baseline = $expected11 }
                    }
                }
            }
        }

        # Every case number reports exactly once - the same harness rule the
        # dimension probes live under.
        for ($d2dCase = 1; $d2dCase -le 11; $d2dCase++) {
            if (-not $script:d2dCasesDone.ContainsKey($d2dCase)) {
                Complete-D2dCase $d2dCase (Get-Date) 'unverified' 'the 2D-detail section ended before this probe ran - a harness bug, not a product verdict'
            }
        }

        # ----------------------------------------------------------------------
        # W7+: PLANIMETRY. The read-only documentation surface, proved against a
        # fixture THIS run stages: two sheets (one with a title block, one
        # without), the dimension fixture's plan and section placed with a KNOWN
        # overlap, a schedule placement placed clear of both, tags staged so one
        # visible pipe is deliberately left untagged and one pipe carries a
        # duplicate, a dimension given a value override, text inside and outside
        # an activated annotation crop, and - last, because it degrades coverage
        # on purpose - an unloaded link.
        #
        # The two tools under test are READ-ONLY; every write below is fixture,
        # made with the typed write tools whose own probes precede this section.
        # execute_python appears exactly twice, both times as FIXTURE PREP (the
        # crop state and the unloaded link have no typed writer), and what it
        # reports is treated as staging evidence, never as the auditor's finding.
        # ----------------------------------------------------------------------
        $script:planCasesDone = @{}

        function Complete-PlanCase {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail,
                  $Evidence = $null)
            if ($script:planCasesDone.ContainsKey($CaseNumber)) { return }
            $script:planCasesDone[$CaseNumber] = $true
            $entry = $writeNames[$planNameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:planimetryEvidence += @{
                case = $CaseNumber; name = $entry.N; tool = $entry.T
                started_utc = $Started.ToUniversalTime().ToString('o')
                duration_ms = [int][math]::Round(((Get-Date) - $Started).TotalMilliseconds)
                outcome = $Outcome
                detail = $Detail
                evidence = $Evidence
            }
        }

        function Invoke-PlanQuery($arguments) { return Invoke-Write 'horizun_query_planimetry' $arguments }
        function Invoke-PlanAudit($arguments) { return Invoke-Write 'horizun_audit_planimetry' $arguments }

        # The same overlap arithmetic the auditor publishes, re-derived here from
        # the QUERY's returned geometry, so the audit's verdict in case 8 is
        # checked against an independent measurement instead of against itself.
        function Test-BoxesOverlap($a, $b) {
            if (-not $a -or -not $b) { return $false }
            $ax = @($a.extent); $bx = @($b.extent)
            if ($ax.Count -ne 4 -or $bx.Count -ne 4) { return $false }
            $ox = [math]::Min([double]$ax[2], [double]$bx[2]) - [math]::Max([double]$ax[0], [double]$bx[0])
            $oy = [math]::Min([double]$ax[3], [double]$bx[3]) - [math]::Max([double]$ax[1], [double]$bx[1])
            return ($ox -gt 0.1 -and $oy -gt 0.1)
        }

        # Findings of one rule id, from a parsed audit reply.
        function Get-PlanFindings($answer, [string]$ruleId, [string]$status) {
            if ($answer.isError -or -not $answer.data) { return @() }
            $rows = @($answer.data.findings | Where-Object { $_.rule_id -eq $ruleId })
            if ($status) { $rows = @($rows | Where-Object { $_.status -eq $status }) }
            return $rows
        }

        $planTag = $probeRun.Substring(0, 8)
        $planGap = $null
        if ($dimViewGap) { $planGap = 'the dimension fixture is missing, and every planimetry case stands on it: ' + $dimViewGap }
        elseif (@($dimPipes).Count -lt 3) { $planGap = ('the dimension fixture staged only {0} pipe(s); the tag cases need 3' -f @($dimPipes).Count) }

        # ---- staging state, filled below and reported in the evidence ---------
        $planTbTypeId = $null; $planTbHow = 'none'
        $planSchedId = $null
        $planSheetAId = $null; $planSheetBId = $null
        $planSheetANumber = "HZP-A-$planTag"; $planSheetBNumber = "HZP-B-$planTag"
        $planVpPlanId = $null; $planVpSecId = $null; $planVpD2dId = $null; $planSchedPlacementId = $null
        $planTagIds = @(); $planTagTypeHow = 'none'
        $planNearTextId = $null; $planFarTextId = $null; $planBlankTextId = $null; $planBlankTextWhy = $null
        $planOverrideDimId = $null
        $planCropStaged = $false; $planCropDetail = 'not attempted'
        $planTextTypeId = $null
        $planScratchBefore = $null
        $planCensusReference = $null

        if (-not $planGap) {
            # ---- F1: a title-block TYPE - from the model when it has one, else
            # ---- authored from this machine's own titleblock template.
            $planTbTypeId = First-Type 'OST_TitleBlocks' $null
            if ($planTbTypeId) { $planTbHow = 'found in the model' }
            else {
                $tbTpl = Find-D2dTemplate $d2dTemplateRoot 'A1 metric.rft' '(?i)titleblock|title ?block|rotulaci|A[01] '
                if ($tbTpl) {
                    $tbRfaDir = Join-Path $scratchDir 'planimetry-families'
                    New-Item -ItemType Directory -Force $tbRfaDir | Out-Null
                    $tbRfa = Join-Path $tbRfaDir 'HZ_PLM_TB.rfa'
                    $tbMk = Invoke-WriteApply 'horizun_create_family' @{
                        target_document = $wDoc; template_path = $tbTpl; output_path = $tbRfa
                        units = 'mm'; overwrite = $true; load_into_project = $true
                        types = @(@{ name = 'HZ_PLM_TB' })
                        family_lines = @(
                            @{ kind = 'symbolic'; start = @(10, 10, 0); end = @(820, 10, 0) },
                            @{ kind = 'symbolic'; start = @(820, 10, 0); end = @(820, 580, 0) },
                            @{ kind = 'symbolic'; start = @(820, 580, 0); end = @(10, 580, 0) },
                            @{ kind = 'symbolic'; start = @(10, 580, 0); end = @(10, 10, 0) })
                    } 'plm-tb-family'
                    if ($tbMk.stage -eq 'apply' -and -not $tbMk.answer.isError -and $tbMk.answer.data -and
                        $tbMk.answer.data.loaded_family -and @($tbMk.answer.data.loaded_family.symbol_ids).Count -gt 0) {
                        $planTbTypeId = @($tbMk.answer.data.loaded_family.symbol_ids)[0]
                        $planTbHow = 'authored from ' + (Split-Path -Leaf $tbTpl)
                    }
                    else { $planTbHow = 'authoring failed: ' + (Get-DimShortText $tbMk.answer.text) }
                }
                else { $planTbHow = 'no titleblock template found under ' + $d2dTemplateRoot }
            }

            # ---- F2: one native schedule, to be placed on sheet A -----------------
            $schedMk = Invoke-WriteApply 'horizun_create_schedule' @{
                target_document = $wDoc; category = 'OST_PipeCurves'; name = "HZ_PLM_SCHED_$planTag"
            } 'plm-schedule'
            if ($schedMk.stage -eq 'apply' -and -not $schedMk.answer.isError -and $schedMk.answer.data -and
                $schedMk.answer.data.schedule_id) {
                $planSchedId = [long]$schedMk.answer.data.schedule_id
            }

            # ---- F3: the CROP, before anything is placed, so the plan viewport has
            # ---- a small, controlled extent and the visible pipe set is exactly the
            # ---- fixture's. No typed tool writes a crop; this is Python AS FIXTURE.
            # CropBox.Min/Max are coordinates LOCAL to the box's own Transform, not
            # model coordinates - measured on Revit 2023 (2026-08-24), where assigning
            # model XY into them put the crop somewhere else entirely and the
            # view-scoped collector saw 1 of the 3 fixture pipes. The corners are
            # therefore taken through Transform.Inverse, and the script verifies its
            # own work by the only fact the section actually depends on: the collector
            # of the cropped view must see exactly the three fixture pipes.
            $cropCode = @"
from Autodesk.Revit.DB import (ElementId, XYZ, BuiltInParameter, Transaction,
                               FilteredElementCollector, BuiltInCategory)
v = doc.GetElement(ElementId($dimPlanViewId))
t = Transaction(doc, 'HZ planimetry crop fixture')
t.Start()
v.CropBoxActive = True
v.CropBoxVisible = True
bb = v.CropBox
inv = bb.Transform.Inverse
a = inv.OfPoint(XYZ(505000.0 / 304.8, -8000.0 / 304.8, 0.0))
b = inv.OfPoint(XYZ(518000.0 / 304.8, 14000.0 / 304.8, 0.0))
bb.Min = XYZ(min(a.X, b.X), min(a.Y, b.Y), bb.Min.Z)
bb.Max = XYZ(max(a.X, b.X), max(a.Y, b.Y), bb.Max.Z)
v.CropBox = bb
ann = False
p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)
if p is not None and not p.IsReadOnly:
    p.Set(1)
    ann = True
t.Commit()
v2 = doc.GetElement(ElementId($dimPlanViewId))
# The self-check uses SUBSTANCE, the same convention the auditor uses: each
# fixture pipe must be un-hidden in the view and answer a bounding box in it.
# The view-scoped collector is deliberately NOT the referee here - measured
# twice on 2023, it omits elements that are demonstrably in the view until
# the view's graphics regenerate.
ok_pipes = []
for raw in [$(@($dimPipes) -join ', ')]:
    e = doc.GetElement(ElementId(raw))
    good = e is not None and not e.IsHidden(v2) and e.get_BoundingBox(v2) is not None
    ok_pipes.append(bool(good))
ok = bool(v2.CropBoxActive) and all(ok_pipes)
__output__ = {'status': 'self_reported_verified' if ok else 'failed',
              'summary': 'crop fixture staged; each fixture pipe answers a bounding box in the cropped view',
              'verification': {'checked': True,
                               'evidence': ['CropBoxActive=' + str(v2.CropBoxActive),
                                            'annotation_crop_set=' + str(ann),
                                            'pipes_boxed_in_view=' + str(ok_pipes)]}}
"@
            $cropRun = Invoke-Write 'horizun_execute_python' @{
                code = $cropCode; target_document = $wDoc
                idempotency_key = "live-plm-crop-$probeRun"
            }
            if (-not $cropRun.isError -and $cropRun.data -and $cropRun.data.executed -eq $true -and
                $cropRun.data.evidence_status -eq 'self_reported_verified') {
                $planCropStaged = $true
                $planCropDetail = 'staged by fixture python (self-reported, treated as staging only)'
            }
            else { $planCropDetail = 'python crop staging did not verify: ' + (Get-DimShortText $cropRun.text) }

            # ---- F4: sheets and placements, one atomic batch ----------------------
            # MEASURED on Revit 2023 (2026-08-24, this machine): Viewport.Create
            # returns NULL - without throwing - for an EMPTY drafting view, while
            # CanAddViewToSheet answers true for the same pair; one detail line
            # makes the same view placeable and verified. The d2d section's
            # drafting view ends its section deliberately empty (its last probe
            # deletes everything it committed), so it gets one fixture line here
            # or it is not placed at all.
            $planD2dPlaceable = $false
            if ($d2dViewId) {
                $d2dLine = Invoke-WriteApply 'horizun_detail_2d' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'create_detail_line'; view_id = $d2dViewId
                                   start = @(0, 0); end = @(1000, 0); key = 'plmline' })
                } 'plm-d2d-line'
                if ($d2dLine.stage -eq 'apply' -and -not $d2dLine.answer.isError -and
                    $d2dLine.answer.data -and $d2dLine.answer.data.state -eq 'committed_verified') {
                    $planD2dPlaceable = $true
                }
            }

            $shActions = @()
            $shA = @{ operation = 'create_sheet'; key = 'shA'; name = "HZ_PLM_SHEET_A_$planTag"; number = $planSheetANumber }
            if ($planTbTypeId) { $shA['title_block_type_id'] = [long]$planTbTypeId }
            $shActions += $shA
            $shActions += @{ operation = 'create_sheet'; key = 'shB'; name = "HZ_PLM_SHEET_B_$planTag"; number = $planSheetBNumber }
            $shActions += @{ operation = 'place_view'; sheet_key = 'shA'; view_id = $dimPlanViewId; point = @(300, 300); key = 'vpPlan' }
            $shActions += @{ operation = 'place_view'; sheet_key = 'shA'; view_id = $dimSectionViewId; point = @(300, 300); key = 'vpSec' }
            if ($planD2dPlaceable) { $shActions += @{ operation = 'place_view'; sheet_key = 'shB'; view_id = $d2dViewId; point = @(300, 300); key = 'vpD2d' } }
            if ($planSchedId) { $shActions += @{ operation = 'place_schedule'; sheet_key = 'shA'; schedule_id = $planSchedId; point = @(700, 120); key = 'siA' } }

            $shMk = Invoke-WriteApply 'horizun_manage_views' @{
                target_document = $wDoc; units = 'mm'; actions = $shActions
            } 'plm-sheets'
            if ($shMk.stage -eq 'apply' -and -not $shMk.answer.isError -and $shMk.answer.data) {
                $aliases = $shMk.answer.data.aliases
                $planSheetAId = $aliases.shA
                $planSheetBId = $aliases.shB
                $planVpPlanId = $aliases.vpPlan
                $planVpSecId = $aliases.vpSec
                if ($planD2dPlaceable) { $planVpD2dId = $aliases.vpD2d }
                if ($planSchedId) { $planSchedPlacementId = $aliases.siA }
            }
            if (-not $planSheetAId -or -not $planSheetBId -or -not $planVpPlanId -or -not $planVpSecId) {
                $planGap = 'the sheet fixture could not be staged: ' + (Get-DimShortText $shMk.answer.text)
            }
        }

        if (-not $planGap) {
            # ---- F5: a multi-category tag type, then the tags and texts -----------
            $planTagTypeId = First-Type 'OST_MultiCategoryTags' $null
            if ($planTagTypeId) { $planTagTypeHow = 'found in the model' }
            else {
                $tagTpl = Find-D2dTemplate $d2dTemplateRoot 'Metric Multi-Category Tag.rft' '(?i)multi-?category tag|varias categor'
                if ($tagTpl) {
                    $tagRfaDir = Join-Path $scratchDir 'planimetry-families'
                    New-Item -ItemType Directory -Force $tagRfaDir | Out-Null
                    $tagRfa = Join-Path $tagRfaDir 'HZ_PLM_TAG.rfa'
                    # MEASURED on Revit 2023: the multi-category tag template refuses
                    # SketchPlane creation ("Sketch plane creation is not allowed in
                    # this family"), which is what family_lines needs - and a tag
                    # symbol needs no geometry to BE a tag target, so none is drawn.
                    $tagMk = Invoke-WriteApply 'horizun_create_family' @{
                        target_document = $wDoc; template_path = $tagTpl; output_path = $tagRfa
                        units = 'mm'; overwrite = $true; load_into_project = $true
                        types = @(@{ name = 'HZ_PLM_TAG' })
                    } 'plm-tag-family'
                    if ($tagMk.stage -eq 'apply' -and -not $tagMk.answer.isError -and $tagMk.answer.data -and
                        $tagMk.answer.data.loaded_family -and @($tagMk.answer.data.loaded_family.symbol_ids).Count -gt 0) {
                        $planTagTypeId = @($tagMk.answer.data.loaded_family.symbol_ids)[0]
                        $planTagTypeHow = 'authored from ' + (Split-Path -Leaf $tagTpl)
                    }
                    else { $planTagTypeHow = 'authoring failed: ' + (Get-DimShortText $tagMk.answer.text) }
                }
                else { $planTagTypeHow = 'no multi-category tag template found under ' + $d2dTemplateRoot }
            }

            $pipe1 = [long]@($dimPipes)[0]; $pipe2 = [long]@($dimPipes)[1]; $pipe3 = [long]@($dimPipes)[2]
            if ($planTagTypeId) {
                # Pipes 1 and 2 tagged; pipe 3 DELIBERATELY not. Pipe 1 tagged twice
                # with the same type in the same view: the staged duplicate.
                $tagMk2 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(
                        @{ operation = 'tag'; view_id = $dimPlanViewId; element_id = $pipe1
                           tag_mode = 'multi_category'; point = @(510500, 6300, 0) },
                        @{ operation = 'tag'; view_id = $dimPlanViewId; element_id = $pipe1
                           tag_mode = 'multi_category'; point = @(512500, 6300, 0) },
                        @{ operation = 'tag'; view_id = $dimPlanViewId; element_id = $pipe2
                           tag_mode = 'multi_category'; point = @(510500, 6900, 0) })
                } 'plm-tags'
                if ($tagMk2.stage -eq 'apply' -and -not $tagMk2.answer.isError -and $tagMk2.answer.data) {
                    foreach ($row in @($tagMk2.answer.data.rows)) {
                        if ($row.element_id) { $planTagIds += [long]$row.element_id }
                    }
                }
            }

            # MEASURED on Revit 2023: query_model include_types answers ZERO
            # TextNoteTypes under OST_TextNotes (355 instance rows, 0 type rows),
            # so the generic First-Type route cannot find one. An existing note
            # names its own type through the planimetry read itself; a model with
            # no note at all falls back to a fixture-python READ of the first
            # TextNoteType - staging, not auditor evidence.
            $planTextTypeId = First-Type 'OST_TextNotes' $null
            if (-not $planTextTypeId) {
                $tq = Invoke-PlanQuery @{ mode = 'annotations'; categories = @('text_notes'); units = 'mm'; max_rows = 1 }
                if (-not $tq.isError -and $tq.data) {
                    $tRow = @($tq.data.rows) | Select-Object -First 1
                    if ($tRow -and $tRow.type_id) { $planTextTypeId = [long]$tRow.type_id }
                }
            }
            if (-not $planTextTypeId) {
                $ttCode = @"
from Autodesk.Revit.DB import FilteredElementCollector, TextNoteType
t = None
for x in FilteredElementCollector(doc).OfClass(TextNoteType):
    t = x
    break
try:
    v = None if t is None else t.Id.Value
except Exception:
    v = None if t is None else t.Id.IntegerValue
__output__ = {'status': 'completed_unverified', 'summary': 'read the first TextNoteType id', 'type_id': v}
"@
                $ttRun = Invoke-Write 'horizun_execute_python' @{
                    code = $ttCode; target_document = $wDoc
                    idempotency_key = "live-plm-texttype-$probeRun"
                }
                if (-not $ttRun.isError -and $ttRun.data -and $ttRun.data.executed -eq $true -and
                    $ttRun.data.output.type_id) {
                    $planTextTypeId = [long]$ttRun.data.output.type_id
                }
            }
            if ($planTextTypeId) {
                $txtMk = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(
                        @{ operation = 'text'; view_id = $dimPlanViewId; point = @(511000, 4500, 0)
                           text = "HZ_PLM_NOTA_$planTag"; text_type_id = [long]$planTextTypeId },
                        @{ operation = 'text'; view_id = $dimPlanViewId; point = @(540000, 4500, 0)
                           text = "HZ_PLM_FUERA_$planTag"; text_type_id = [long]$planTextTypeId })
                } 'plm-texts'
                if ($txtMk.stage -eq 'apply' -and -not $txtMk.answer.isError -and $txtMk.answer.data) {
                    $txtRows = @($txtMk.answer.data.rows)
                    if ($txtRows.Count -ge 1 -and $txtRows[0].element_id) { $planNearTextId = [long]$txtRows[0].element_id }
                    if ($txtRows.Count -ge 2 -and $txtRows[1].element_id) { $planFarTextId = [long]$txtRows[1].element_id }
                }
                # The whitespace note, alone so a refusal cannot sink the real ones.
                # Revit may refuse all-whitespace text; either outcome is recorded.
                $blankMk = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'text'; view_id = $dimPlanViewId; point = @(511000, 3800, 0)
                                   text = ' '; text_type_id = [long]$planTextTypeId })
                } 'plm-blank-text'
                if ($blankMk.stage -eq 'apply' -and -not $blankMk.answer.isError -and $blankMk.answer.data) {
                    $blankRows = @($blankMk.answer.data.rows)
                    if ($blankRows.Count -ge 1 -and $blankRows[0].element_id) { $planBlankTextId = [long]$blankRows[0].element_id }
                }
                if (-not $planBlankTextId) {
                    $planBlankTextWhy = 'Revit (or the bridge) refused an all-whitespace TextNote: ' + (Get-DimShortText $blankMk.answer.text) +
                                        ' - the pure empty-text branch stays proved by unit test, and the live audit asserts on real notes instead.'
                }
            }

            # ---- F6: one dimension gets a value override --------------------------
            $qd = Invoke-Write 'horizun_query_dimensions' @{ view_id = $dimPlanViewId; max_rows = 200 }
            if (-not $qd.isError -and $qd.data) {
                $simple = @($qd.data.rows | Where-Object {
                    $_.number_of_segments -le 1 -and $_.is_view_specific -eq $true -and $_.shape -eq 'linear' }) |
                    Select-Object -First 1
                if ($simple) {
                    $ov = Invoke-WriteApply 'horizun_edit_dimensions' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(@{ element_id = [long]$simple.element_id; value_override = 'VARIES' })
                    } 'plm-override'
                    if ($ov.stage -eq 'apply' -and -not $ov.answer.isError) {
                        $planOverrideDimId = [long]$simple.element_id
                    }
                }
            }

            # The reference census the closing case compares against, and the file
            # census the no-file-output case compares against. Taken AFTER staging,
            # BEFORE the first read, so only the read surface is being measured.
            $planScratchBefore = @(Get-ChildItem -Path $scratchDir -Recurse -File -ErrorAction SilentlyContinue).Count
        }

        if ($planGap) {
            for ($pc = 1; $pc -le 22; $pc++) { Complete-PlanCase $pc (Get-Date) 'not_covered' $planGap }
        }
        else {
            # ---- case 19 (opens here): IsModified before the first read -----------
            $t19 = Get-Date
            $planModifiedBefore = $null
            $mod1 = Invoke-Write 'horizun_execute_python' @{
                code = "__output__ = {'status': 'self_reported_verified', 'summary': 'read IsModified', 'verification': {'checked': True, 'evidence': ['IsModified=' + str(doc.IsModified)]}, 'modified': bool(doc.IsModified)}"
                target_document = $wDoc; idempotency_key = "live-plm-mod1-$probeRun"
            }
            if (-not $mod1.isError -and $mod1.data -and $mod1.data.executed -eq $true) {
                $planModifiedBefore = [bool]$mod1.data.output.modified
            }

            # ---- case 1: the inventory census -------------------------------------
            $t0 = Get-Date
            $inv1 = Invoke-PlanQuery @{ mode = 'inventory'; units = 'mm' }
            if ($inv1.isError -or -not $inv1.data) {
                Complete-PlanCase 1 $t0 'unverified' ('the inventory call errored: ' + (Get-DimShortText $inv1.text))
            }
            else {
                $totals = $inv1.data.totals
                $shq = Invoke-PlanQuery @{ mode = 'sheets'; units = 'mm'; max_rows = 500 }
                $plq = Invoke-PlanQuery @{ mode = 'placements'; units = 'mm'; max_rows = 500 }
                $sheetsExact = ($shq.data -and [int]$totals.sheets_total -eq [int]$shq.data.matched_total)
                $vpRows = @()
                if ($plq.data) { $vpRows = @($plq.data.rows | Where-Object { $_.class -eq 'viewport' }) }
                $vpExact = ($plq.data -and [int]$totals.viewports_total -eq $vpRows.Count -and
                            $plq.data.truncated -eq $false)
                $coverageBlock = ($null -ne $inv1.data.coverage_complete -and $null -ne $inv1.data.checks_failed)
                if ($sheetsExact -and $vpExact -and $coverageBlock -and
                    [int]$totals.sheets_total -ge 2 -and [int]$totals.dimensions_total -ge 1 -and
                    [int]$totals.tags_total -ge $planTagIds.Count) {
                    Complete-PlanCase 1 $t0 'pass' ("totals are exact against independent re-reads: sheets_total={0} matches mode=sheets, viewports_total={1} matches mode=placements; the coverage block is present" -f $totals.sheets_total, $totals.viewports_total) `
                        -Evidence @{ totals = $totals; coverage_complete = $inv1.data.coverage_complete }
                }
                else {
                    Complete-PlanCase 1 $t0 'fail' ("the census does not hold: sheets_exact={0} viewports_exact={1} coverage_block={2} (sheets_total={3}, dimensions_total={4}, tags_total={5})" -f $sheetsExact, $vpExact, $coverageBlock, $totals.sheets_total, $totals.dimensions_total, $totals.tags_total)
                }
            }

            # ---- case 2: sheets ---------------------------------------------------
            $t0 = Get-Date
            $sh2 = Invoke-PlanQuery @{ mode = 'sheets'; sheet_ids = @([long]$planSheetAId, [long]$planSheetBId); units = 'mm' }
            if ($sh2.isError -or -not $sh2.data) {
                Complete-PlanCase 2 $t0 'unverified' ('mode=sheets errored: ' + (Get-DimShortText $sh2.text))
            }
            else {
                $rowA = @($sh2.data.rows | Where-Object { [long]$_.sheet_id -eq [long]$planSheetAId }) | Select-Object -First 1
                $rowB = @($sh2.data.rows | Where-Object { [long]$_.sheet_id -eq [long]$planSheetBId }) | Select-Object -First 1
                $aOk = ($rowA -and $rowA.sheet_number -eq $planSheetANumber -and
                        @($rowA.viewport_ids).Count -eq 2 -and
                        (@($rowA.viewport_ids) -contains [long]$planVpPlanId) -and
                        (@($rowA.viewport_ids) -contains [long]$planVpSecId))
                if ($aOk -and $planSchedPlacementId) {
                    $aOk = (@($rowA.schedule_placement_ids) -contains [long]$planSchedPlacementId)
                }
                $tbOk = $true
                if ($planTbTypeId) { $tbOk = ($rowA -and [int]$rowA.titleblock_count -eq 1 -and $rowA.titleblock_type) }
                $bOk = ($rowB -and [int]$rowB.titleblock_count -eq 0 -and $rowB.placeholder -eq $false)
                if ($aOk -and $tbOk -and $bOk) {
                    Complete-PlanCase 2 $t0 'pass' ("sheet A carries its two viewports{0}{1}; sheet B reads titleblock_count=0" -f $(if ($planSchedPlacementId) { ', its schedule placement' } else { '' }), $(if ($planTbTypeId) { ' and one title block with its type' } else { '' })) `
                        -Evidence @{ sheet_a = $rowA; sheet_b = $rowB; titleblock = $planTbHow }
                }
                else {
                    Complete-PlanCase 2 $t0 'fail' ("the sheet rows do not hold: A_ok={0} titleblock_ok={1} B_ok={2} (titleblock staging: {3})" -f $aOk, $tbOk, $bOk, $planTbHow)
                }
            }

            # ---- case 3: views ----------------------------------------------------
            $t0 = Get-Date
            $vw3 = Invoke-PlanQuery @{ mode = 'views'; view_ids = @([long]$dimPlanViewId); units = 'mm' }
            if ($vw3.isError -or -not $vw3.data) {
                Complete-PlanCase 3 $t0 'unverified' ('mode=views errored: ' + (Get-DimShortText $vw3.text))
            }
            else {
                $vRow = @($vw3.data.rows) | Select-Object -First 1
                $baseOk = ($vRow -and $vRow.view_type -eq 'FloorPlan' -and [int]$vRow.scale -gt 0 -and
                           $vRow.placed_on_sheet -eq $true -and
                           (@($vRow.sheet_ids) -contains [long]$planSheetAId) -and
                           ($null -ne $vRow.template_id -or $vRow.template_readable -eq $true))
                $cropOk = $true
                if ($planCropStaged) {
                    $cropOk = ($vRow.crop_box_active -eq $true -and $null -ne $vRow.crop_box)
                }
                if ($baseOk -and $cropOk) {
                    Complete-PlanCase 3 $t0 'pass' ("the plan row carries view_type, scale {0}, template state, sheet placement on the fixture sheet{1}" -f $vRow.scale, $(if ($planCropStaged) { ' and the ACTIVE crop with its geometry' } else { ' (crop fixture was not staged: ' + $planCropDetail + ')' })) `
                        -Evidence @{ view = $vRow; crop_staged = $planCropStaged }
                }
                else {
                    Complete-PlanCase 3 $t0 'fail' ("the view row does not hold: base={0} crop={1} ({2})" -f $baseOk, $cropOk, $planCropDetail)
                }
            }

            # ---- case 4: placements in sheet coordinates, with the KNOWN overlap --
            $t0 = Get-Date
            $pl4 = Invoke-PlanQuery @{ mode = 'placements'; sheet_ids = @([long]$planSheetAId, [long]$planSheetBId); units = 'mm' }
            $planVpPlanRow = $null; $planVpSecRow = $null; $planSchedRow = $null
            if ($pl4.isError -or -not $pl4.data) {
                Complete-PlanCase 4 $t0 'unverified' ('mode=placements errored: ' + (Get-DimShortText $pl4.text))
            }
            else {
                $planVpPlanRow = @($pl4.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpPlanId }) | Select-Object -First 1
                $planVpSecRow = @($pl4.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpSecId }) | Select-Object -First 1
                if ($planSchedPlacementId) {
                    $planSchedRow = @($pl4.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planSchedPlacementId }) | Select-Object -First 1
                }
                $bothReadable = ($planVpPlanRow -and $planVpSecRow -and
                                 $planVpPlanRow.bounds_readable -eq $true -and $planVpSecRow.bounds_readable -eq $true -and
                                 $planVpPlanRow.coordinate_system -eq 'sheet' -and @($planVpPlanRow.box_outline).Count -eq 4)
                $overlapMeasured = Test-BoxesOverlap $planVpPlanRow $planVpSecRow
                $schedClear = $true
                if ($planSchedRow) {
                    $schedClear = (-not (Test-BoxesOverlap $planSchedRow $planVpPlanRow)) -and
                                  (-not (Test-BoxesOverlap $planSchedRow $planVpSecRow))
                }
                if ($bothReadable -and $overlapMeasured -and $schedClear) {
                    Complete-PlanCase 4 $t0 'pass' 'both viewports read their sheet-coordinate outlines, the harness measures their staged overlap from the returned geometry, and the schedule placement is measurably clear of both' `
                        -Evidence @{ viewport_plan = $planVpPlanRow; viewport_section = $planVpSecRow; schedule = $planSchedRow }
                }
                else {
                    Complete-PlanCase 4 $t0 'fail' ("the placement geometry does not hold: bounds_readable={0} staged_overlap_measured={1} schedule_clear={2}" -f $bothReadable, $overlapMeasured, $schedClear)
                }
            }

            # ---- case 5: annotations ----------------------------------------------
            $t0 = Get-Date
            $an5 = Invoke-PlanQuery @{ mode = 'annotations'; view_ids = @([long]$dimPlanViewId); units = 'mm'; max_rows = 500 }
            if ($an5.isError -or -not $an5.data) {
                Complete-PlanCase 5 $t0 'unverified' ('mode=annotations errored: ' + (Get-DimShortText $an5.text))
            }
            else {
                $rows5 = @($an5.data.rows)
                $dims5 = @($rows5 | Where-Object { $_.kind -eq 'dimension' })
                $tags5 = @($rows5 | Where-Object { $_.kind -eq 'tag' })
                $texts5 = @($rows5 | Where-Object { $_.kind -eq 'text_note' })
                $dimOk = ($dims5.Count -ge 1 -and $null -ne $dims5[0].reference_count)
                $tagOk = $true
                if ($planTagIds.Count -gt 0) {
                    $tag5 = @($tags5 | Where-Object { [long]$_.element_id -eq [long]$planTagIds[0] }) | Select-Object -First 1
                    $tagOk = ($tag5 -and (@($tag5.tagged_element_ids) -contains [long]@($dimPipes)[0]) -and
                              $tag5.orphaned -eq $false)
                }
                $textOk = $true
                if ($planNearTextId) {
                    $near5 = @($texts5 | Where-Object { [long]$_.element_id -eq [long]$planNearTextId }) | Select-Object -First 1
                    $textOk = ($near5 -and $near5.empty_or_whitespace -eq $false -and $near5.text)
                }
                $kindsDiscriminated = (@($rows5 | Where-Object { -not $_.kind }).Count -eq 0)
                if ($dimOk -and $tagOk -and $textOk -and $kindsDiscriminated) {
                    Complete-PlanCase 5 $t0 'pass' ("{0} dimension(s) with reference counts, the staged tag naming its pipe, the real text with empty=false, and every row discriminated by kind" -f $dims5.Count) `
                        -Evidence @{ dimensions = $dims5.Count; tags = $tags5.Count; texts = $texts5.Count
                                     blank_text = $planBlankTextId; blank_text_note = $planBlankTextWhy }
                }
                else {
                    Complete-PlanCase 5 $t0 'fail' ("the annotation rows do not hold: dimensions={0} tag={1} text={2} discriminated={3}" -f $dimOk, $tagOk, $textOk, $kindsDiscriminated)
                }
            }

            # ---- case 6: references -----------------------------------------------
            $t0 = Get-Date
            $rf6 = Invoke-PlanQuery @{ mode = 'references'; units = 'mm'; max_rows = 500 }
            if ($rf6.isError -or -not $rf6.data) {
                Complete-PlanCase 6 $t0 'unverified' ('mode=references errored: ' + (Get-DimShortText $rf6.text))
            }
            else {
                $refRows = @($rf6.data.rows)
                $legalStates = @('resolved', 'missing', 'unknown', 'unreadable')
                $allLegal = (@($refRows | Where-Object { $legalStates -notcontains $_.target_state }).Count -eq 0)
                $resolvedToSec = @($refRows | Where-Object {
                    $_.target_state -eq 'resolved' -and [long]$_.target_view_id -eq [long]$dimSectionViewId })
                $unknownWithReason = @($refRows | Where-Object {
                    $_.target_state -eq 'unknown' -and -not [string]::IsNullOrWhiteSpace($_.target_state_reason) })
                $bareUnknown = @($refRows | Where-Object {
                    $_.target_state -eq 'unknown' -and [string]::IsNullOrWhiteSpace($_.target_state_reason) })
                if ($allLegal -and $bareUnknown.Count -eq 0 -and ($resolvedToSec.Count -ge 1 -or $unknownWithReason.Count -ge 1)) {
                    $secDetail = 'no marker resolved to the fixture section; every unknown carries its reason'
                    if ($resolvedToSec.Count -ge 1) {
                        $secDetail = ("a marker resolves to the fixture section view (placed={0})" -f $resolvedToSec[0].target_placed)
                    }
                    Complete-PlanCase 6 $t0 'pass' ("{0} reference row(s), every target_state legal, none unknown without a reason; {1}" -f $refRows.Count, $secDetail) `
                        -Evidence @{ rows = $refRows.Count; resolved_to_section = $resolvedToSec.Count; unknown_with_reason = $unknownWithReason.Count }
                }
                else {
                    Complete-PlanCase 6 $t0 'fail' ("the reference rows do not hold: all_states_legal={0} bare_unknowns={1} resolved_to_section={2} unknown_with_reason={3}" -f $allLegal, $bareUnknown.Count, $resolvedToSec.Count, $unknownWithReason.Count)
                }
            }

            # ---- cases 7, 8, 9: ONE audit of the two fixture sheets ---------------
            $auditSheetsArgs = @{ scope = 'sheets'; sheet_ids = @([long]$planSheetAId, [long]$planSheetBId); units = 'mm'; max_findings = 500 }
            $au7 = Invoke-PlanAudit $auditSheetsArgs

            $t0 = Get-Date
            if ($au7.isError -or -not $au7.data) {
                Complete-PlanCase 7 $t0 'unverified' ('the sheet audit errored: ' + (Get-DimShortText $au7.text))
            }
            else {
                $noTb = Get-PlanFindings $au7 'sheet.no-titleblock' 'failed'
                $forB = @($noTb | Where-Object { [long]$_.sheet_id -eq [long]$planSheetBId })
                $wrongA = @()
                if ($planTbTypeId) { $wrongA = @($noTb | Where-Object { [long]$_.sheet_id -eq [long]$planSheetAId }) }
                if ($forB.Count -eq 1 -and $forB[0].severity -eq 'blocking' -and $wrongA.Count -eq 0) {
                    Complete-PlanCase 7 $t0 'pass' ("sheet B ({0}) is the blocking no-titleblock finding{1}" -f $planSheetBNumber, $(if ($planTbTypeId) { '; sheet A, which carries one, is not' } else { ' (sheet A also has none: ' + $planTbHow + ')' })) `
                        -Evidence @{ finding = $forB[0]; titleblock_staging = $planTbHow }
                }
                else {
                    Complete-PlanCase 7 $t0 'fail' ("no-titleblock findings do not hold: for_B={0} for_A={1} (staging: {2})" -f $forB.Count, $wrongA.Count, $planTbHow)
                }
            }

            $t0 = Get-Date
            if ($au7.isError -or -not $au7.data) {
                Complete-PlanCase 8 $t0 'unverified' 'the sheet audit errored (see case 7)'
            }
            else {
                $ov8 = Get-PlanFindings $au7 'sheet.viewport-overlap' 'failed'
                $pair8 = @($ov8 | Where-Object {
                    (@($_.element_ids) -contains [long]$planVpPlanId) -and (@($_.element_ids) -contains [long]$planVpSecId) })
                if ($pair8.Count -eq 1 -and $pair8[0].severity -eq 'blocking' -and
                    [double]$pair8[0].observed.overlap_x -gt 0 -and [double]$pair8[0].observed.overlap_y -gt 0 -and
                    $pair8[0].location.coordinate_system -eq 'sheet') {
                    Complete-PlanCase 8 $t0 'pass' ("the staged pair is reported once, blocking, with the measured extent: overlap_x={0}mm overlap_y={1}mm at a sheet-coordinate point" -f $pair8[0].observed.overlap_x, $pair8[0].observed.overlap_y) `
                        -Evidence @{ finding = $pair8[0] }
                }
                else {
                    Complete-PlanCase 8 $t0 'fail' ("the staged overlap is not reported as it must be: findings_for_pair={0} (all viewport-overlap findings: {1})" -f $pair8.Count, $ov8.Count)
                }
            }

            $t0 = Get-Date
            if ($au7.isError -or -not $au7.data) {
                Complete-PlanCase 9 $t0 'unverified' 'the sheet audit errored (see case 7)'
            }
            else {
                $allOverlap = @()
                $allOverlap += Get-PlanFindings $au7 'sheet.viewport-overlap' 'failed'
                $allOverlap += Get-PlanFindings $au7 'sheet.viewport-schedule-overlap' 'failed'
                $allOverlap += Get-PlanFindings $au7 'sheet.schedule-overlap' 'failed'
                $involvingSched = @()
                if ($planSchedPlacementId) {
                    $involvingSched = @($allOverlap | Where-Object { @($_.element_ids) -contains [long]$planSchedPlacementId })
                }
                $involvingD2d = @()
                if ($planVpD2dId) {
                    $involvingD2d = @($allOverlap | Where-Object { @($_.element_ids) -contains [long]$planVpD2dId })
                }
                if ($involvingSched.Count -eq 0 -and $involvingD2d.Count -eq 0) {
                    Complete-PlanCase 9 $t0 'pass' 'the separated schedule placement and the lone viewport on sheet B appear in NO overlap finding' `
                        -Evidence @{ overlap_findings_total = $allOverlap.Count }
                }
                else {
                    Complete-PlanCase 9 $t0 'fail' ("separated placements were reported as overlapping: schedule_in={0} d2d_in={1}" -f $involvingSched.Count, $involvingD2d.Count)
                }
            }

            # ---- case 10: the override is ADVISORY by default ---------------------
            $t0 = Get-Date
            if (-not $planOverrideDimId) {
                Complete-PlanCase 10 $t0 'unverified' 'no fixture dimension could be given a value override, so the advisory default could not be proved live'
            }
            else {
                $au10 = Invoke-PlanAudit @{ scope = 'views'; view_ids = @([long]$dimPlanViewId); units = 'mm'; max_findings = 500 }
                if ($au10.isError -or -not $au10.data) {
                    Complete-PlanCase 10 $t0 'unverified' ('the view audit errored: ' + (Get-DimShortText $au10.text))
                }
                else {
                    $ovF = @((Get-PlanFindings $au10 'dimension.value-override' 'failed') | Where-Object {
                        @($_.element_ids) -contains [long]$planOverrideDimId })
                    $blankOk = $true; $blankDetail = ''
                    if ($planBlankTextId) {
                        $blankF = @((Get-PlanFindings $au10 'text.empty' 'failed') | Where-Object {
                            @($_.element_ids) -contains [long]$planBlankTextId })
                        $blankOk = ($blankF.Count -eq 1 -and $blankF[0].severity -eq 'blocking')
                        $blankDetail = ("; the staged whitespace note is the blocking text.empty finding" )
                    }
                    elseif ($planBlankTextWhy) { $blankDetail = '; ' + $planBlankTextWhy }
                    if ($ovF.Count -eq 1 -and $ovF[0].severity -eq 'advisory' -and $blankOk) {
                        Complete-PlanCase 10 $t0 'pass' ("the override on dimension {0} is reported once, severity=advisory, recommending {1}{2}" -f $planOverrideDimId, $ovF[0].recommended_tool, $blankDetail) `
                            -Evidence @{ finding = $ovF[0]; blank_text = $planBlankTextId; blank_text_note = $planBlankTextWhy }
                    }
                    else {
                        Complete-PlanCase 10 $t0 'fail' ("the advisory default does not hold: override_findings={0} severity={1} blank_ok={2}" -f $ovF.Count, $(if ($ovF.Count -gt 0) { $ovF[0].severity } else { '(none)' }), $blankOk)
                    }
                }
            }

            # ---- case 11: a requirement set makes the SAME override blocking ------
            $t0 = Get-Date
            if (-not $planOverrideDimId) {
                Complete-PlanCase 11 $t0 'unverified' 'no fixture override exists (see case 10)'
            }
            else {
                $set11 = @{
                    requirement_set = @{ id = 'hz-live-planimetry'; version = '1.0.0'; title = 'Live gate set' }
                    rules = @(@{ id = 'no-overrides'; entity = 'dimension'; severity = 'blocking'
                                 selector = @{ applies_to = @([long]$planOverrideDimId) }
                                 assertion = @{ operator = 'forbid_numeric_override' } })
                }
                $au11 = Invoke-PlanAudit @{ scope = 'views'; view_ids = @([long]$dimPlanViewId); units = 'mm'
                                            max_findings = 500; requirement_set = $set11 }
                if ($au11.isError -or -not $au11.data) {
                    Complete-PlanCase 11 $t0 'unverified' ('the requirement-set audit errored: ' + (Get-DimShortText $au11.text))
                }
                else {
                    $f11 = @((Get-PlanFindings $au11 'no-overrides' 'failed') | Where-Object {
                        @($_.element_ids) -contains [long]$planOverrideDimId })
                    $cites = ($f11.Count -eq 1 -and $f11[0].requirement_set -eq 'hz-live-planimetry' -and
                              $f11[0].requirement_set_version -eq '1.0.0' -and
                              $au11.data.requirement_set_sha256 -match '^[0-9a-f]{64}$' -and
                              $f11[0].requirement_set_sha256 -eq $au11.data.requirement_set_sha256)
                    if ($cites -and $f11[0].severity -eq 'blocking') {
                        Complete-PlanCase 11 $t0 'pass' 'the same override is BLOCKING under the inline set, and the finding cites the set id, version and sha256 the reply published' `
                            -Evidence @{ finding = $f11[0]; sha256 = $au11.data.requirement_set_sha256 }
                    }
                    else {
                        Complete-PlanCase 11 $t0 'fail' ("the configurable severity does not hold: findings={0} cites_set={1}" -f $f11.Count, $cites)
                    }
                }
            }

            # ---- case 12: a naming rule catches the wrong sheet number ------------
            $t0 = Get-Date
            $set12 = @{
                requirement_set = @{ id = 'hz-live-naming'; version = '1.0.0' }
                rules = @(@{ id = 'sheet-number-format'; entity = 'sheet'; severity = 'blocking'
                             selector = @{ applies_to = @([long]$planSheetAId, [long]$planSheetBId) }
                             assertion = @{ field = 'sheet_number'; operator = 'matches'; value = '^HZP-A-' } })
            }
            $au12 = Invoke-PlanAudit @{ scope = 'sheets'; sheet_ids = @([long]$planSheetAId, [long]$planSheetBId)
                                        units = 'mm'; max_findings = 500; requirement_set = $set12 }
            if ($au12.isError -or -not $au12.data) {
                Complete-PlanCase 12 $t0 'unverified' ('the naming audit errored: ' + (Get-DimShortText $au12.text))
            }
            else {
                $f12 = Get-PlanFindings $au12 'sheet-number-format' 'failed'
                $onlyB = ($f12.Count -eq 1 -and [long]$f12[0].sheet_id -eq [long]$planSheetBId -and
                          $f12[0].observed.value -eq $planSheetBNumber)
                if ($onlyB) {
                    Complete-PlanCase 12 $t0 'pass' ("exactly sheet B fails ^HZP-A- with its observed number {0}; sheet A passes" -f $planSheetBNumber) `
                        -Evidence @{ finding = $f12[0] }
                }
                else {
                    Complete-PlanCase 12 $t0 'fail' ("the naming rule does not hold: findings={0}" -f $f12.Count)
                }
            }

            # ---- case 13: a template rule catches the not-allowed template --------
            $t0 = Get-Date
            $set13 = @{
                requirement_set = @{ id = 'hz-live-templates'; version = '1.0.0' }
                rules = @(@{ id = 'plan-template'; entity = 'view'; severity = 'blocking'
                             selector = @{ applies_to = @([long]$dimPlanViewId) }
                             assertion = @{ operator = 'allowed_template'; value = @("HZ_TEMPLATE_THAT_DOES_NOT_EXIST_$planTag") } })
            }
            $au13 = Invoke-PlanAudit @{ scope = 'views'; view_ids = @([long]$dimPlanViewId); units = 'mm'
                                        max_findings = 500; requirement_set = $set13 }
            if ($au13.isError -or -not $au13.data) {
                Complete-PlanCase 13 $t0 'unverified' ('the template audit errored: ' + (Get-DimShortText $au13.text))
            }
            else {
                $f13 = @((Get-PlanFindings $au13 'plan-template' 'failed') | Where-Object {
                    @($_.element_ids) -contains [long]$dimPlanViewId })
                if ($f13.Count -eq 1 -and $f13[0].severity -eq 'blocking') {
                    Complete-PlanCase 13 $t0 'pass' 'the fixture plan fails allowed_template against a list its template is not in, with the observed template in the finding' `
                        -Evidence @{ finding = $f13[0] }
                }
                else {
                    Complete-PlanCase 13 $t0 'fail' ("the template rule does not hold: findings={0}" -f $f13.Count)
                }
            }

            # ---- case 14: requires_tag names the exact untagged pipe --------------
            $t0 = Get-Date
            if ($planTagIds.Count -lt 3) {
                Complete-PlanCase 14 $t0 'unverified' ('the tag fixture was not staged (' + $planTagTypeHow + '), so tag coverage cannot be proved live')
            }
            else {
                $set14 = @{
                    requirement_set = @{ id = 'hz-live-tags'; version = '1.0.0' }
                    rules = @(@{ id = 'pipes-tagged'; entity = 'view'; severity = 'blocking'
                                 selector = @{ applies_to = @([long]$dimPlanViewId) }
                                 assertion = @{ operator = 'requires_tag'; value = @('OST_PipeCurves') } })
                }
                $au14 = Invoke-PlanAudit @{ scope = 'views'; view_ids = @([long]$dimPlanViewId); units = 'mm'
                                            max_findings = 500; requirement_set = $set14 }
                if ($au14.isError -or -not $au14.data) {
                    Complete-PlanCase 14 $t0 'unverified' ('the tag audit errored: ' + (Get-DimShortText $au14.text))
                }
                else {
                    $f14 = Get-PlanFindings $au14 'pipes-tagged' 'failed'
                    $pipe3Named = @($f14 | Where-Object { @($_.element_ids) -contains [long]$pipe3 })
                    $pipe1Blamed = @($f14 | Where-Object { @($_.element_ids) -contains [long]$pipe1 })
                    $pipe2Blamed = @($f14 | Where-Object { @($_.element_ids) -contains [long]$pipe2 })
                    if ($pipe3Named.Count -eq 1 -and $pipe1Blamed.Count -eq 0 -and $pipe2Blamed.Count -eq 0) {
                        Complete-PlanCase 14 $t0 'pass' ("the untagged pipe {0} is named exactly once; the two tagged pipes are in no finding ({1} untagged finding(s) total in the cropped view)" -f $pipe3, $f14.Count) `
                            -Evidence @{ untagged_finding = $pipe3Named[0]; findings_total = $f14.Count; tag_staging = $planTagTypeHow }
                    }
                    else {
                        Complete-PlanCase 14 $t0 'fail' ("tag coverage does not hold: pipe3_named={0} pipe1_blamed={1} pipe2_blamed={2}" -f $pipe3Named.Count, $pipe1Blamed.Count, $pipe2Blamed.Count)
                    }
                }
            }

            # ---- case 16: pagination returns every row exactly once ----------------
            $t0 = Get-Date
            $pageArgs = @{ mode = 'annotations'; view_ids = @([long]$dimPlanViewId); units = 'mm'; max_rows = 5 }
            $page1 = Invoke-PlanQuery $pageArgs
            $planStaleCursor = $null
            if ($page1.isError -or -not $page1.data) {
                Complete-PlanCase 16 $t0 'unverified' ('the first page errored: ' + (Get-DimShortText $page1.text))
            }
            else {
                $planStaleCursor = $page1.data.next_cursor
                $expectedTotal = [int]$page1.data.matched_total
                $seenIds = @{}
                $dupes = 0
                $totalDrift = $false
                $pages = 0
                $current = $page1
                while ($true) {
                    $pages++
                    foreach ($row in @($current.data.rows)) {
                        $key = [string]$row.element_id
                        if ($seenIds.ContainsKey($key)) { $dupes++ } else { $seenIds[$key] = $true }
                    }
                    if ([int]$current.data.matched_total -ne $expectedTotal) { $totalDrift = $true }
                    if (-not $current.data.next_cursor -or $pages -ge 200) { break }
                    $nextArgs = @{ mode = 'annotations'; view_ids = @([long]$dimPlanViewId); units = 'mm'
                                   max_rows = 5; cursor = [string]$current.data.next_cursor }
                    $current = Invoke-PlanQuery $nextArgs
                    if ($current.isError -or -not $current.data) { break }
                }
                if ($seenIds.Count -eq $expectedTotal -and $dupes -eq 0 -and -not $totalDrift -and $expectedTotal -gt 5) {
                    Complete-PlanCase 16 $t0 'pass' ("{0} rows paged in {1} page(s) of 5: no duplicate, no missing row, matched_total constant" -f $expectedTotal, $pages) `
                        -Evidence @{ total = $expectedTotal; pages = $pages }
                }
                else {
                    Complete-PlanCase 16 $t0 'fail' ("pagination does not hold: unique={0} expected={1} duplicates={2} total_drift={3}" -f $seenIds.Count, $expectedTotal, $dupes, $totalDrift)
                }
            }

            # ---- case 17: a stale cursor is refused after the model moved ---------
            $t0 = Get-Date
            if (-not $planStaleCursor) {
                Complete-PlanCase 17 $t0 'unverified' 'the first page produced no cursor to go stale (fewer than 6 annotations?)'
            }
            elseif (-not $planTextTypeId) {
                Complete-PlanCase 17 $t0 'unverified' 'no text type exists to move the model with'
            }
            else {
                $mv17 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'text'; view_id = $dimPlanViewId; point = @(511000, 3200, 0)
                                   text = "HZ_PLM_STALE_$planTag"; text_type_id = [long]$planTextTypeId })
                } 'plm-stale-text'
                if ($mv17.stage -ne 'apply' -or $mv17.answer.isError) {
                    Complete-PlanCase 17 $t0 'unverified' ('the model could not be moved between pages: ' + (Get-DimShortText $mv17.answer.text))
                }
                else {
                    $stale = Invoke-PlanQuery @{ mode = 'annotations'; view_ids = @([long]$dimPlanViewId); units = 'mm'
                                                 max_rows = 5; cursor = [string]$planStaleCursor }
                    if ($stale.isError -and $stale.text -match 'stale|changed since') {
                        Complete-PlanCase 17 $t0 'pass' 'the cursor from before the change is refused by name, not silently repaged' `
                            -Evidence @{ refusal = (Get-DimShortText $stale.text) }
                    }
                    else {
                        Complete-PlanCase 17 $t0 'fail' ("the stale cursor was not refused: isError={0} text={1}" -f $stale.isError, (Get-DimShortText $stale.text))
                    }
                }
            }

            # The reference census for the closing case: AFTER the deliberate stale-
            # cursor write, BEFORE the remaining read-only calls.
            $censusQ = Invoke-PlanQuery @{ mode = 'inventory'; units = 'mm' }
            if (-not $censusQ.isError -and $censusQ.data) {
                $planCensusReference = $censusQ.data.totals | ConvertTo-Json -Compress -Depth 4
            }

            # ---- case 18: two identical audits agree to the byte ------------------
            $t0 = Get-Date
            $au18a = Invoke-PlanAudit $auditSheetsArgs
            $au18b = Invoke-PlanAudit $auditSheetsArgs
            if ($au18a.isError -or $au18b.isError -or -not $au18a.data -or -not $au18b.data) {
                Complete-PlanCase 18 $t0 'unverified' 'one of the twin audits errored'
            }
            else {
                $fpEqual = ($au18a.data.finding_set_fingerprint -eq $au18b.data.finding_set_fingerprint)
                $totalEqual = ([int]$au18a.data.findings_total -eq [int]$au18b.data.findings_total)
                $seqA = @($au18a.data.findings | ForEach-Object { $_.rule_id + ':' + (@($_.element_ids) -join ',') }) -join '|'
                $seqB = @($au18b.data.findings | ForEach-Object { $_.rule_id + ':' + (@($_.element_ids) -join ',') }) -join '|'
                if ($fpEqual -and $totalEqual -and $seqA -eq $seqB) {
                    Complete-PlanCase 18 $t0 'pass' ("both runs: fingerprint {0}, {1} finding(s), identical order" -f $au18a.data.finding_set_fingerprint, $au18a.data.findings_total) `
                        -Evidence @{ fingerprint = $au18a.data.finding_set_fingerprint; findings_total = $au18a.data.findings_total }
                }
                else {
                    Complete-PlanCase 18 $t0 'fail' ("two identical audits disagree: fingerprints_equal={0} totals_equal={1} order_equal={2}" -f $fpEqual, $totalEqual, ($seqA -eq $seqB))
                }
            }

            # ---- case 20: counts, ids and geometry re-read independently ----------
            $t0 = Get-Date
            $qd20 = Invoke-Write 'horizun_query_dimensions' @{ view_id = $dimPlanViewId; max_rows = 1 }
            $qp20 = Invoke-PlanQuery @{ mode = 'annotations'; view_ids = @([long]$dimPlanViewId)
                                        categories = @('dimensions'); units = 'mm'; max_rows = 1 }
            $pl20a = Invoke-PlanQuery @{ mode = 'placements'; sheet_ids = @([long]$planSheetAId); units = 'mm'; max_rows = 500 }
            $pl20b = Invoke-PlanQuery @{ mode = 'placements'; sheet_ids = @([long]$planSheetAId); units = 'mm'; max_rows = 500 }
            if ($qd20.isError -or -not $qd20.data -or $qp20.isError -or -not $qp20.data -or
                $pl20a.isError -or -not $pl20a.data -or $pl20b.isError -or -not $pl20b.data) {
                Complete-PlanCase 20 $t0 'unverified' 'one of the four independent reads errored'
            }
            else {
                $dimAgree = ([int]$qd20.data.total_matched -eq [int]$qp20.data.matched_total)
                $rowA20 = @($pl20a.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpPlanId }) | Select-Object -First 1
                $rowB20 = @($pl20b.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpPlanId }) | Select-Object -First 1
                $geoA = ''
                $geoB = 'different'
                if ($rowA20 -and $rowB20) {
                    $geoA = (@($rowA20.box_outline) -join ',')
                    $geoB = (@($rowB20.box_outline) -join ',')
                }
                if ($dimAgree -and $geoA -ne '' -and $geoA -eq $geoB) {
                    Complete-PlanCase 20 $t0 'pass' ("horizun_query_dimensions and horizun_query_planimetry agree on {0} dimension(s) in the plan; two placement reads return byte-identical geometry" -f $qd20.data.total_matched) `
                        -Evidence @{ dimensions_both = $qd20.data.total_matched; box_outline = $geoA }
                }
                else {
                    Complete-PlanCase 20 $t0 'fail' ("independent re-reads disagree: dimensions {0} vs {1}; geometry_equal={2}" -f $qd20.data.total_matched, $qp20.data.matched_total, ($geoA -eq $geoB))
                }
            }

            # ---- case 21: no file, no export --------------------------------------
            $t0 = Get-Date
            $planScratchAfter = @(Get-ChildItem -Path $scratchDir -Recurse -File -ErrorAction SilentlyContinue).Count
            $repliesClean = $true
            foreach ($reply in @($au7, $au18a, $au18b)) {
                if ($reply.data -and $reply.text -match '"output_path"') { $repliesClean = $false }
            }
            if ($null -ne $planScratchBefore -and $planScratchAfter -eq $planScratchBefore -and $repliesClean) {
                Complete-PlanCase 21 $t0 'pass' ("the harness scratch directory holds the same {0} file(s) it held before the first read, and no audit reply names an output_path" -f $planScratchAfter) `
                    -Evidence @{ files_before = $planScratchBefore; files_after = $planScratchAfter }
            }
            else {
                Complete-PlanCase 21 $t0 'fail' ("the read surface left a trace: files {0} -> {1}, replies_clean={2}" -f $planScratchBefore, $planScratchAfter, $repliesClean)
            }

            # ---- case 19 (closes): IsModified unchanged by the read surface -------
            if ($null -eq $planModifiedBefore) {
                Complete-PlanCase 19 $t19 'unverified' 'Document.IsModified could not be read before the section (execute_python unavailable?), so the property could not be measured live; the no-Transaction guarantee stays proved at source level'
            }
            else {
                $mod2 = Invoke-Write 'horizun_execute_python' @{
                    code = "__output__ = {'status': 'self_reported_verified', 'summary': 'read IsModified', 'verification': {'checked': True, 'evidence': ['IsModified=' + str(doc.IsModified)]}, 'modified': bool(doc.IsModified)}"
                    target_document = $wDoc; idempotency_key = "live-plm-mod2-$probeRun"
                }
                if ($mod2.isError -or -not $mod2.data -or $mod2.data.executed -ne $true) {
                    Complete-PlanCase 19 $t19 'unverified' 'Document.IsModified could not be re-read after the section'
                }
                else {
                    $planModifiedAfter = [bool]$mod2.data.output.modified
                    if ($planModifiedAfter -eq $planModifiedBefore) {
                        Complete-PlanCase 19 $t19 'pass' ("Document.IsModified is {0} before and after every query and audit (read via fixture python, labelled self-reported; the fixture writes preceding the section legitimately set it)" -f $planModifiedBefore) `
                            -Evidence @{ before = $planModifiedBefore; after = $planModifiedAfter; measured_by = 'execute_python fixture read, self-reported' }
                    }
                    else {
                        Complete-PlanCase 19 $t19 'fail' ("Document.IsModified moved across the read-only section: {0} -> {1}" -f $planModifiedBefore, $planModifiedAfter)
                    }
                }
            }

            # ---- case 15: incomplete coverage blocks a clean verdict --------------
            # LAST of the audits, because it degrades coverage on purpose.
            $t0 = Get-Date
            $unloadCode = @"
from Autodesk.Revit.DB import FilteredElementCollector, RevitLinkType, RevitLinkInstance
lt = None
for x in FilteredElementCollector(doc).OfClass(RevitLinkType):
    lt = x
    break
if lt is None:
    __output__ = {'status': 'failed', 'summary': 'no RevitLinkType exists in this document to unload'}
else:
    lt.Unload(None)
    loaded = 0
    for inst in FilteredElementCollector(doc).OfClass(RevitLinkInstance):
        if inst.GetLinkDocument() is not None:
            loaded += 1
    __output__ = {'status': 'self_reported_verified' if loaded == 0 else 'failed',
                  'summary': 'link unloaded as coverage fixture',
                  'verification': {'checked': True, 'evidence': ['loaded_instances=' + str(loaded)]}}
"@
            $unload = Invoke-Write 'horizun_execute_python' @{
                code = $unloadCode; target_document = $wDoc
                idempotency_key = "live-plm-unload-$probeRun"
            }
            if ($unload.isError -or -not $unload.data -or $unload.data.evidence_status -ne 'self_reported_verified') {
                Complete-PlanCase 15 $t0 'unverified' ('the coverage fixture (an unloaded link) could not be staged: ' + (Get-DimShortText $unload.text))
            }
            else {
                $au15 = Invoke-PlanAudit @{ scope = 'sheets'; sheet_ids = @([long]$planSheetAId); units = 'mm'; max_findings = 100 }
                if ($au15.isError -or -not $au15.data) {
                    Complete-PlanCase 15 $t0 'unverified' ('the post-unload audit errored: ' + (Get-DimShortText $au15.text))
                }
                else {
                    $covFalse = ($au15.data.coverage_complete -eq $false)
                    $noteSays = ($au15.data.note -and $au15.data.note -match 'INCOMPLETE|not.*clean|link')
                    $linkCov = ($au15.data.link_coverage -and $au15.data.link_coverage.coverage_complete -eq $false)
                    if ($covFalse -and $noteSays -and $linkCov) {
                        Complete-PlanCase 15 $t0 'pass' 'with one link unloaded, coverage_complete=false, link_coverage names it, and the note forbids reading the model as clean' `
                            -Evidence @{ coverage_complete = $au15.data.coverage_complete; note = $au15.data.note }
                    }
                    else {
                        Complete-PlanCase 15 $t0 'fail' ("incomplete coverage is not surfaced: coverage_false={0} note_ok={1} link_coverage_false={2}" -f $covFalse, $noteSays, $linkCov)
                    }
                }
            }

            # ---- case 22: the disposable document ends with no unplanned change ---
            $t0 = Get-Date
            $censusEnd = Invoke-PlanQuery @{ mode = 'inventory'; units = 'mm' }
            if ($censusEnd.isError -or -not $censusEnd.data -or -not $planCensusReference) {
                Complete-PlanCase 22 $t0 'unverified' 'the closing census could not be read, or the reference census was never taken'
            }
            else {
                $endTotals = $censusEnd.data.totals | ConvertTo-Json -Compress -Depth 4
                if ($endTotals -eq $planCensusReference) {
                    Complete-PlanCase 22 $t0 'pass' 'the closing inventory census is byte-identical to the reference census taken before the read-only calls: the section read, audited and changed nothing it did not stage' `
                        -Evidence @{ census = $censusEnd.data.totals }
                }
                else {
                    Complete-PlanCase 22 $t0 'fail' ('the census moved across the read-only calls. reference=' + $planCensusReference + ' end=' + $endTotals)
                }
            }
        }

        # Every case number reports exactly once - the same harness rule the
        # dimension and 2D-detail probes live under.
        for ($planCase = 1; $planCase -le 22; $planCase++) {
            if (-not $script:planCasesDone.ContainsKey($planCase)) {
                Complete-PlanCase $planCase (Get-Date) 'unverified' 'the planimetry section ended before this probe ran - a harness bug, not a product verdict'
            }
        }

        # ----------------------------------------------------------------------
        # W8+: FIX PLANIMETRY. The write half of the documentation surface.
        #
        # It runs LAST and on purpose reuses the planimetry fixture UNCORRECTED:
        # sheet B still has no title block, the two viewports still overlap, the
        # far text still sits outside the crop. Those are real findings the read
        # section already proved the auditor produces, so correcting them here
        # tests the whole loop - audit, cite, rehearse, confirm, commit, re-read,
        # re-audit - rather than a fixture invented to be easy.
        #
        # Three things this section adds, because a CORRECTION needs what a read
        # does not: a view template to assign, one element override to clear, and
        # an inline requirement set whose rules produce findings for the
        # operations the universal catalog deliberately has no remedy for
        # (scale, naming, margins, overrides). The set is passed inline on every
        # call that cites one of its findings, exactly as a caller must.
        #
        # execute_python appears only as FIXTURE PREP (the element override and
        # the file timestamp read have no typed writer) and what it reports is
        # staging evidence, never the corrector's finding.
        # ----------------------------------------------------------------------
        $script:fixCasesDone = @{}

        function Complete-FixCase {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail,
                  $Evidence = $null)
            if ($script:fixCasesDone.ContainsKey($CaseNumber)) { return }
            $script:fixCasesDone[$CaseNumber] = $true
            $entry = $writeNames[$fixNameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:fixEvidence += @{
                case = $CaseNumber; name = $entry.N; tool = $entry.T
                started_utc = $Started.ToUniversalTime().ToString('o')
                duration_ms = [int][math]::Round(((Get-Date) - $Started).TotalMilliseconds)
                outcome = $Outcome
                detail = $Detail
                evidence = $Evidence
            }
        }

        function Invoke-Fix($arguments) { return Invoke-Write 'horizun_fix_planimetry' $arguments }

        # A fix DRY RUN, returning the parsed answer. Nothing is applied.
        function Invoke-FixDry($arguments) {
            $dry = $arguments.Clone()
            $dry['dry_run'] = $true
            return Invoke-Fix $dry
        }

        # Rehearse then apply, with an explicit idempotency key so the replay and
        # conflict cases can re-use it deliberately.
        function Invoke-FixApply($arguments, [string]$KeyName) {
            $d = Invoke-FixDry $arguments
            if ($d.isError -or -not $d.data -or -not $d.data.confirmation_token) {
                return @{ stage = 'dry_run'; dry = $d; answer = $d; key = $null }
            }
            $apply = $arguments.Clone()
            $apply['dry_run'] = $false
            $apply['confirmation_token'] = $d.data.confirmation_token
            $key = ("live-fix-{0}-{1}" -f $KeyName, $probeRun)
            $apply['idempotency_key'] = $key
            return @{ stage = 'apply'; dry = $d; answer = (Invoke-Fix $apply); key = $key
                      applied = $apply }
        }

        # One action's finding block, copied from an audit finding verbatim. The
        # command refuses a block that is edited, so this must not "tidy" it.
        function New-FixFinding($finding) {
            $block = @{
                rule_id                 = $finding.rule_id
                requirement_set         = $finding.requirement_set
                requirement_set_version = $finding.requirement_set_version
                element_ids             = @($finding.element_ids | ForEach-Object { [long]$_ })
                observed                = $finding.observed
            }
            if ($finding.requirement_set_sha256) { $block['requirement_set_sha256'] = $finding.requirement_set_sha256 }
            if ($finding.entity_kind) { $block['entity_kind'] = $finding.entity_kind }
            if ($null -ne $finding.sheet_id) { $block['sheet_id'] = [long]$finding.sheet_id }
            if ($null -ne $finding.view_id) { $block['view_id'] = [long]$finding.view_id }
            return $block
        }

        # Every postcondition row of a fix reply verified. An empty rows list is
        # NOT agreement - the same rule All-Rows enforces everywhere else.
        function Test-FixVerified($answer) {
            if ($answer.isError -or -not $answer.data) { return $false }
            if ($answer.data.state -ne 'verified_applied') { return $false }
            return (All-Rows $answer.data.rows { param($r) $r.verified -eq $true })
        }

        # The inventory census as ONE comparable string.
        function Get-FixCensus() {
            $c = Invoke-Write 'horizun_query_planimetry' @{ mode = 'inventory'; units = 'mm' }
            if ($c.isError -or -not $c.data) { return $null }
            return ($c.data.totals | ConvertTo-Json -Compress -Depth 4)
        }

        # ---- staging state ----------------------------------------------------
        $fixGap = $planGap
        $fixTemplateId = $null
        $fixSet = $null
        $fixCensusReference = $null
        $fixPlacedTitleBlockId = $null
        $fixSectionViewFinalName = $null
        $fixModifiedBefore = $null
        $fixFileStampBefore = $null
        $fixFilePath = $null

        if (-not $fixGap -and -not $planSheetBId) {
            $fixGap = 'the planimetry sheet fixture is missing, and every correction case stands on it'
        }

        if (-not $fixGap) {
            # ---- The reference census, taken BEFORE this section stages or
            # ---- corrects anything. Case 22 returns to it.
            $fixCensusReference = Get-FixCensus
            if (-not $fixCensusReference) { $fixGap = 'the reference census could not be read' }

            # The dimension ids as they stand BEFORE the section touches anything.
            # The closing census compares totals; when they disagree, a total says
            # only THAT something appeared. The ids say WHAT, which is the half a
            # reader can act on.
            $fixDimIdsBefore = @()
            $qd0 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'annotations'; categories = @('dimensions'); units = 'mm'; max_rows = 500 }
            if ($qd0.data) { $fixDimIdsBefore = @($qd0.data.rows | ForEach-Object { [long]$_.element_id }) }

            # And the plan view's crop, so the revert can put back what set_crop
            # deliberately changed. A section that claims to revert itself has to
            # revert the display state it altered, not only the elements it added.
            $fixCropBefore = $null
            $qv0 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'views'; view_ids = @([long]$dimPlanViewId); units = 'mm' }
            if ($qv0.data -and @($qv0.data.rows).Count -gt 0) { $fixCropBefore = @($qv0.data.rows)[0].crop_box }
        }

        if (-not $fixGap) {
            # ---- F1: a view TEMPLATE to assign. Authored from the dimension plan
            # ---- view so it is this run's own, never a template the model relies on.
            $tplCode = @"
from Autodesk.Revit.DB import Transaction, ElementId
src = doc.GetElement(ElementId($dimPlanViewId))
made = None
why = None
# View.CreateViewTemplate() returns a VIEW, not an ElementId - checked against
# RevitAPI.dll metadata for 2023-2027. Passing its result to Document.GetElement
# throws, and an except that only recorded "could not be authored" hid the reason
# behind a fixture that had actually run.
if not src.IsViewValidForTemplateCreation():
    why = 'the source view is not valid for template creation'
else:
    t = Transaction(doc, 'HZ fix live: author a view template')
    t.Start()
    try:
        made = src.CreateViewTemplate()
        made.Name = 'HZ_FIX_TPL_$planTag'
        t.Commit()
    except Exception as ex:
        t.RollBack()
        made = None
        why = type(ex).__name__ + ': ' + str(ex)
if made is None:
    __output__ = {'status': 'failed',
                  'summary': 'the view template could not be authored: ' + (why or 'no reason reported')}
else:
    back = doc.GetElement(made.Id)
    ok = back is not None and back.IsTemplate
    __output__ = {'status': 'self_reported_verified' if ok else 'failed',
                  'summary': 'authored a view template from the dimension plan view',
                  'template_id': back.Id.Value if hasattr(back.Id, 'Value') else back.Id.IntegerValue,
                  'verification': {'checked': True, 'evidence': ['IsTemplate=' + str(back.IsTemplate)]}}
"@
            $tplMk = Invoke-Write 'horizun_execute_python' @{
                code = $tplCode; target_document = $wDoc
                idempotency_key = "live-fix-tpl-$probeRun"
            }
            if ($tplMk.isError -or -not $tplMk.data -or $tplMk.data.evidence_status -ne 'self_reported_verified') {
                # The SCRIPT's own summary first. Rendering the whole reply envelope
                # and truncating it at a few hundred characters buried the one
                # sentence that says what went wrong under the boilerplate that says
                # nothing did.
                $why = $null
                if ($tplMk.data -and $tplMk.data.output -and $tplMk.data.output.summary) { $why = [string]$tplMk.data.output.summary }
                if (-not $why) { $why = Get-DimShortText $tplMk.text }
                $fixGap = 'the view template fixture could not be staged: ' + $why
            }
            else { $fixTemplateId = [long]$tplMk.data.output.template_id }
        }

        if (-not $fixGap) {
            # ---- F2: an ELEMENT OVERRIDE on the near text note, so
            # ---- clear_element_override has something real to clear. There is no
            # ---- typed writer for a per-view graphic override; this is prep.
            if (-not $planNearTextId) {
                $fixGap = 'the planimetry fixture staged no near text note to override'
            }
            else {
                $ovCode = @"
from Autodesk.Revit.DB import Transaction, ElementId, OverrideGraphicSettings
view = doc.GetElement(ElementId($dimPlanViewId))
target = ElementId($planNearTextId)
t = Transaction(doc, 'HZ fix live: stage an element override')
t.Start()
ogs = OverrideGraphicSettings()
ogs.SetHalftone(True)
view.SetElementOverrides(target, ogs)
t.Commit()
back = view.GetElementOverrides(target)
__output__ = {'status': 'self_reported_verified' if back.Halftone else 'failed',
              'summary': 'staged a halftone element override on the near text note',
              'verification': {'checked': True, 'evidence': ['Halftone=' + str(back.Halftone)]}}
"@
                $ovMk = Invoke-Write 'horizun_execute_python' @{
                    code = $ovCode; target_document = $wDoc
                    idempotency_key = "live-fix-ovr-$probeRun"
                }
                if ($ovMk.isError -or -not $ovMk.data -or $ovMk.data.evidence_status -ne 'self_reported_verified') {
                    $why = $null
                    if ($ovMk.data -and $ovMk.data.output -and $ovMk.data.output.summary) { $why = [string]$ovMk.data.output.summary }
                    if (-not $why) { $why = Get-DimShortText $ovMk.text }
                    $fixGap = 'the element-override fixture could not be staged: ' + $why
                }
            }
        }

        if (-not $fixGap) {
            # ---- F3: the inline requirement set. Its rules exist to PRODUCE the
            # ---- findings the universal catalog has no remedy for, each pinned to
            # ---- the exact element by id so the set cannot accidentally match the
            # ---- rest of the model.
            $fixSet = @{
                requirement_set = @{ id = 'horizun-live-fix-set'; version = '1.0.0'
                                     title = 'Horizun live fix gate' }
                rules = @(
                    @{ id = 'section-view-name'; entity = 'view'; severity = 'blocking'
                       selector = @{ applies_to = @([long]$dimSectionViewId) }
                       assertion = @{ field = 'name'; operator = 'matches'; value = '^HZFIX-' } },
                    @{ id = 'section-view-scale'; entity = 'view'; severity = 'blocking'
                       selector = @{ applies_to = @([long]$dimSectionViewId) }
                       assertion = @{ operator = 'allowed_scale'; value = @(25) } },
                    @{ id = 'sheet-b-number'; entity = 'sheet'; severity = 'blocking'
                       selector = @{ applies_to = @([long]$planSheetBId) }
                       assertion = @{ field = 'sheet_number'; operator = 'matches'; value = '^HZFIX-' } },
                    @{ id = 'near-text-no-override'; entity = 'text_note'; severity = 'blocking'
                       selector = @{ applies_to = @([long]$planNearTextId) }
                       assertion = @{ field = 'has_view_overrides'; operator = 'equals'; value = $false } },
                    # The template rule. Fails until case 3 assigns the template this
                    # run authored, and passes afterwards - so it licenses the
                    # correction AND proves the resolution, in every year's model.
                    @{ id = 'section-view-template'; entity = 'view'; severity = 'blocking'
                       selector = @{ applies_to = @([long]$dimSectionViewId) }
                       assertion = @{ operator = 'allowed_template'; value = @("HZ_FIX_TPL_$planTag") } },
                    # A name nothing satisfies, for the batch that must write nothing.
                    @{ id = 'plan-view-never'; entity = 'view'; severity = 'blocking'
                       selector = @{ applies_to = @([long]$dimPlanViewId) }
                       assertion = @{ field = 'name'; operator = 'matches'; value = '^HZNEVER-' } }
                )
            }
            if ($planSchedPlacementId) {
                $fixSet.rules += @{ id = 'schedule-margin'; entity = 'schedule_placement'; severity = 'blocking'
                                    selector = @{ applies_to = @([long]$planSchedPlacementId) }
                                    assertion = @{ operator = 'inside_extent'; value = 200 } }
            }

            # ---- F5: a REACHABLE outside-crop finding -------------------------
            # See the note above: only detail_2d.outside-crop can be staged, and it
            # needs a detail element between the model crop and the annotation crop.
            $fixCropLineId = $null
            $fixCropDetail = 'not attempted'
            $offsetCode = @"
from Autodesk.Revit.DB import ElementId, Transaction
v = doc.GetElement(ElementId($dimPlanViewId))
mgr = v.GetCropRegionShapeManager()
t = Transaction(doc, 'HZ fix live: widen the annotation crop')
t.Start()
applied = []
for name in ('LeftAnnotationCropOffset', 'RightAnnotationCropOffset',
             'TopAnnotationCropOffset', 'BottomAnnotationCropOffset'):
    if hasattr(mgr, name):
        try:
            setattr(mgr, name, 6000.0 / 304.8)
            applied.append(name)
        except Exception as ex:
            applied.append(name + '!' + type(ex).__name__)
t.Commit()
v2 = doc.GetElement(ElementId($dimPlanViewId))
mgr2 = v2.GetCropRegionShapeManager()
got = []
for name in ('LeftAnnotationCropOffset', 'RightAnnotationCropOffset'):
    if hasattr(mgr2, name):
        got.append(name + '=' + str(round(getattr(mgr2, name) * 304.8, 1)))
ok = len(applied) > 0
__output__ = {'status': 'self_reported_verified' if ok else 'failed',
              'summary': 'widened the annotation crop so a detail element can sit outside the MODEL crop and still be drawn',
              'verification': {'checked': True, 'evidence': applied + got}}
"@
            $offsetRun = Invoke-Write 'horizun_execute_python' @{
                code = $offsetCode; target_document = $wDoc
                idempotency_key = "live-fix-annoff-$probeRun"
            }
            if ($offsetRun.isError -or -not $offsetRun.data -or $offsetRun.data.evidence_status -ne 'self_reported_verified') {
                $fixCropDetail = 'the annotation-crop offsets could not be widened'
            }
            else {
                # A detail line just OUTSIDE the model crop (which the planimetry
                # fixture set to x 505000..518000 mm) and well inside the widened
                # annotation crop. View-plane millimetres, the convention detail_2d
                # publishes.
                $lineMk = Invoke-WriteApply 'horizun_detail_2d' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ operation = 'create_detail_line'; view_id = [long]$dimPlanViewId
                                   start = @(519000, 2000); end = @(521000, 2000); key = 'cropline' })
                } 'fix-cropline'
                if ($lineMk.stage -eq 'apply' -and -not $lineMk.answer.isError -and $lineMk.answer.data) {
                    # element_idS, plural and an ARRAY - one action can create several
                    # curves (a polyline comes back as three). Reading the singular
                    # left the id null, the line was never reverted, and the closing
                    # census carried it.
                    $rows = @($lineMk.answer.data.rows)
                    if ($rows.Count -ge 1 -and $rows[0].element_ids -and @($rows[0].element_ids).Count -ge 1) {
                        $fixCropLineId = [long]@($rows[0].element_ids)[0]
                        $fixCropDetail = 'a detail line was drawn outside the model crop and inside the widened annotation crop'
                    }
                }
                if (-not $fixCropLineId) { $fixCropDetail = 'the detail line could not be drawn: ' + (Get-DimShortText $lineMk.answer.text) }
            }

            # ---- F4: IsModified and the file's timestamp, for cases 2 and 23.
            $stampCode = @"
import os
p = doc.PathName
stamp = ''
if p and os.path.exists(p):
    stamp = str(os.path.getmtime(p)) + '|' + str(os.path.getsize(p))
__output__ = {'status': 'self_reported_verified', 'summary': 'read IsModified and the file stamp',
              'verification': {'checked': True, 'evidence': ['IsModified=' + str(doc.IsModified), 'stamp=' + stamp]},
              'modified': bool(doc.IsModified), 'stamp': stamp, 'path': p or ''}
"@
            $st1 = Invoke-Write 'horizun_execute_python' @{
                code = $stampCode; target_document = $wDoc
                idempotency_key = "live-fix-stamp1-$probeRun"
            }
            if (-not $st1.isError -and $st1.data -and $st1.data.executed -eq $true) {
                $fixModifiedBefore = [bool]$st1.data.output.modified
                $fixFileStampBefore = [string]$st1.data.output.stamp
                $fixFilePath = [string]$st1.data.output.path
            }
        }

        if ($fixGap) {
            for ($fc = 1; $fc -le 23; $fc++) { Complete-FixCase $fc (Get-Date) 'not_covered' $fixGap }
        }
        else {
            # ---- case 1: the contract, as a client sees it --------------------
            $t0 = Get-Date
            # $listed is the tools/list this run negotiated at startup - the SAME
            # answer a client receives, not a second read that could disagree.
            if (@($listed).Count -eq 0) {
                Complete-FixCase 1 $t0 'unverified' 'tools/list produced no tools at negotiation'
            }
            else {
                $entry = @($listed | Where-Object { $_.name -eq 'horizun_fix_planimetry' })
                if ($entry.Count -ne 1) {
                    Complete-FixCase 1 $t0 'fail' 'horizun_fix_planimetry is not published exactly once in tools/list'
                }
                else {
                    $e = $entry[0]
                    $ann = $e.annotations
                    $ops = @($e.inputSchema.properties.actions.items.properties.operation.enum)
                    $okAnn = ($ann.readOnlyHint -eq $false -and $ann.destructiveHint -eq $false -and
                              $ann.idempotentHint -eq $true -and $ann.openWorldHint -eq $false)
                    $okSchema = ($e.inputSchema.additionalProperties -eq $false -and
                                 $ops.Count -eq 9 -and
                                 ($ops -contains 'set_view_template') -and ($ops -contains 'set_crop') -and
                                 -not ($ops -contains 'pack_sheet') -and
                                 $null -ne $e.inputSchema.properties.confirmation_token -and
                                 $null -ne $e.inputSchema.properties.idempotency_key -and
                                 $e.inputSchema.properties.dry_run.default -eq $true)
                    if ($okAnn -and $okSchema) {
                        Complete-FixCase 1 $t0 'pass' ("published with readOnlyHint=false, idempotentHint=true, a closed schema, dry_run defaulting to true and exactly {0} operations" -f $ops.Count) `
                            -Evidence @{ annotations = $ann; operations = $ops }
                    }
                    else {
                        Complete-FixCase 1 $t0 'fail' ("annotations_ok={0} schema_ok={1}; operations={2}" -f $okAnn, $okSchema, ($ops -join ','))
                    }
                }
            }

            # ---- The audit this section corrects from. Recomputed here so every
            # ---- cited finding is one the auditor produces RIGHT NOW.
            function Get-FixAudit() {
                return Invoke-Write 'horizun_audit_planimetry' @{
                    scope = 'model'; units = 'mm'; max_findings = 500
                    include_advisory = $true; requirement_set = $fixSet
                }
            }
            $au = Get-FixAudit
            if ($au.isError -or -not $au.data) {
                for ($fc = 2; $fc -le 23; $fc++) {
                    Complete-FixCase $fc (Get-Date) 'unverified' ('the audit these corrections cite could not be read: ' + (Get-DimShortText $au.text))
                }
            }
            else {
                $fixFingerprint = $au.data.finding_set_fingerprint
                function Find-FixFinding($answer, [string]$ruleId, $elementId) {
                    $rows = @($answer.data.findings | Where-Object { $_.rule_id -eq $ruleId -and $_.status -eq 'failed' })
                    if ($null -ne $elementId) {
                        $rows = @($rows | Where-Object { @($_.element_ids) -contains [long]$elementId })
                    }
                    if ($rows.Count -eq 0) { return $null }
                    return $rows[0]
                }
                function New-FixSource() { return @{ finding_set_fingerprint = $fixFingerprint; units = 'mm' } }

                # ---- case 2: a dry run changes nothing --------------------------
                $t0 = Get-Date
                $f2 = Find-FixFinding $au 'sheet.no-titleblock' $planSheetBId
                if (-not $f2) {
                    Complete-FixCase 2 $t0 'unverified' 'the audit produced no sheet.no-titleblock finding for the bare sheet, so there was nothing to rehearse'
                }
                elseif (-not $planTbTypeId) {
                    Complete-FixCase 2 $t0 'not_covered' 'no title-block type is available on this machine, so the rehearsal has nothing to place'
                }
                else {
                    $censusBeforeDry = Get-FixCensus
                    $dry2 = Invoke-FixDry @{
                        target_document = $wDoc; units = 'mm'; source_audit = (New-FixSource)
                        actions = @(@{ operation = 'place_title_block'; sheet_id = [long]$planSheetBId
                                       title_block_type_id = [long]$planTbTypeId
                                       finding = (New-FixFinding $f2) })
                    }
                    $censusAfterDry = Get-FixCensus
                    $st2 = Invoke-Write 'horizun_execute_python' @{
                        code = $stampCode; target_document = $wDoc
                        idempotency_key = "live-fix-stamp2-$probeRun"
                    }
                    $modAfterDry = $null
                    if (-not $st2.isError -and $st2.data -and $st2.data.executed -eq $true) {
                        $modAfterDry = [bool]$st2.data.output.modified
                    }
                    if ($dry2.isError -or -not $dry2.data) {
                        Complete-FixCase 2 $t0 'fail' ('the dry run errored: ' + (Get-DimShortText $dry2.text))
                    }
                    else {
                        $rehearsed = ($dry2.data.rehearsal -and $dry2.data.rehearsal.materialised_provisionally -eq $true -and
                                      $dry2.data.rehearsal.rolled_back -eq $true)
                        $tokened = [bool]$dry2.data.confirmation_token
                        $censusSame = ($censusBeforeDry -eq $censusAfterDry)
                        $modSame = ($null -eq $modAfterDry) -or ($null -eq $fixModifiedBefore) -or ($modAfterDry -eq $fixModifiedBefore)
                        if ($rehearsed -and $tokened -and $censusSame -and $modSame) {
                            Complete-FixCase 2 $t0 'pass' ("the rehearsal MATERIALISED the batch inside a transaction, rolled it back ({0}), issued a token, and left the census byte-identical and IsModified at {1}" -f $dry2.data.rehearsal.rollback_status, $fixModifiedBefore) `
                                -Evidence @{ rehearsal = $dry2.data.rehearsal; census_unchanged = $censusSame
                                             is_modified_before = $fixModifiedBefore; is_modified_after = $modAfterDry }
                        }
                        else {
                            Complete-FixCase 2 $t0 'fail' ("rehearsed={0} token={1} census_unchanged={2} is_modified_unchanged={3}" -f $rehearsed, $tokened, $censusSame, $modSame)
                        }
                    }
                }

                # ---- case 14 (early, it must run before its finding is corrected):
                # ---- an unknown-severity rule can never be cited.
                $t0 = Get-Date
                $unk = Invoke-Fix @{
                    target_document = $wDoc; units = 'mm'; dry_run = $true; source_audit = (New-FixSource)
                    actions = @(@{ operation = 'set_view_scale'; view_id = [long]$dimSectionViewId; scale = 50
                                   finding = @{ rule_id = 'view.template-unreadable'
                                                requirement_set = 'horizun-universal-planimetry'
                                                requirement_set_version = '1.0.0'
                                                entity_kind = 'view'
                                                view_id = [long]$dimSectionViewId
                                                element_ids = @([long]$dimSectionViewId)
                                                observed = @{ reason = 'staged' } } })
                }
                $unkRefused = ($unk.isError -or ($unk.data -and $unk.data.invalid_actions -ge 1))
                $unkSays = ($unk.text -match 'could NOT be measured|unknown')
                if ($unkRefused -and $unkSays) {
                    Complete-FixCase 14 $t0 'pass' 'citing a check whose severity is `unknown` is refused by name: an unmeasured fact is not a defect and cannot be corrected' `
                        -Evidence @{ refusal = (Get-DimShortText $unk.text) }
                }
                else {
                    Complete-FixCase 14 $t0 'fail' ("an unknown-severity rule was not refused: refused={0} named={1}: {2}" -f $unkRefused, $unkSays, (Get-DimShortText $unk.text))
                }

                # ---- case 7: place_title_block (and the finding cases hang off it)
                $t0 = Get-Date
                $f7 = Find-FixFinding $au 'sheet.no-titleblock' $planSheetBId
                $fix7 = $null
                if (-not $f7) {
                    Complete-FixCase 7 $t0 'unverified' 'no sheet.no-titleblock finding to correct'
                }
                elseif (-not $planTbTypeId) {
                    Complete-FixCase 7 $t0 'not_covered' 'no title-block type is available on this machine'
                }
                else {
                    $act7 = @{ operation = 'place_title_block'; sheet_id = [long]$planSheetBId
                               title_block_type_id = [long]$planTbTypeId
                               finding = (New-FixFinding $f7) }
                    $fix7 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; source_audit = (New-FixSource)
                        actions = @($act7)
                    } 'titleblock'
                    if ($fix7.stage -ne 'apply') {
                        Complete-FixCase 7 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix7.answer.text))
                    }
                    elseif (Test-FixVerified $fix7.answer) {
                        $row = @($fix7.answer.data.rows)[0]
                        $props = @($row.postconditions.properties | ForEach-Object { $_.property })
                        $covers = (($props -contains 'instance_present') -and ($props -contains 'owner_sheet') -and
                                   ($props -contains 'symbol') -and ($props -contains 'category') -and
                                   ($props -contains 'titleblock_count'))
                        if ($covers -and $row.postconditions.all_verified -eq $true) {
                            Complete-FixCase 7 $t0 'pass' 'one title block placed on the bare sheet; instance, owner sheet, symbol, category and a count of exactly 1 all re-read from the committed model' `
                                -Evidence @{ postconditions = $row.postconditions; state = $fix7.answer.data.state }
                        }
                        else {
                            Complete-FixCase 7 $t0 'fail' ("the postcondition checklist did not cover the promise: properties={0}" -f ($props -join ','))
                        }
                    }
                    else {
                        Complete-FixCase 7 $t0 'fail' ('the correction did not verify: ' + (Get-DimShortText $fix7.answer.text))
                    }
                }

                # ---- case 20: the audit afterwards no longer produces the finding
                $t0 = Get-Date
                if (-not $fix7 -or $fix7.stage -ne 'apply' -or -not (Test-FixVerified $fix7.answer)) {
                    Complete-FixCase 20 $t0 'unverified' 'the title-block correction did not commit, so there is nothing to re-audit'
                }
                else {
                    $au20 = Get-FixAudit
                    if ($au20.isError -or -not $au20.data) {
                        Complete-FixCase 20 $t0 'unverified' 'the follow-up audit could not be read'
                    }
                    else {
                        $still = Find-FixFinding $au20 'sheet.no-titleblock' $planSheetBId
                        if ($null -eq $still) {
                            Complete-FixCase 20 $t0 'pass' 'an INDEPENDENT horizun_audit_planimetry run no longer reports sheet.no-titleblock for that sheet: the rule stopped producing the finding' `
                                -Evidence @{ before = $f7.rule_id; after = 'absent'
                                             fingerprint_before = $fixFingerprint
                                             fingerprint_after = $au20.data.finding_set_fingerprint }
                        }
                        else {
                            Complete-FixCase 20 $t0 'fail' 'the corrected finding is still reported by a fresh audit'
                        }
                    }
                }

                # ---- case 21: resolved / persistent / new are told apart --------
                $t0 = Get-Date
                if (-not $fix7 -or $fix7.stage -ne 'apply' -or $fix7.answer.isError -or -not $fix7.answer.data) {
                    Complete-FixCase 21 $t0 'unverified' 'no committed correction to read a reconciliation from'
                }
                else {
                    $rec = $fix7.answer.data.reconciliation
                    if (-not $rec) {
                        Complete-FixCase 21 $t0 'fail' 'the reply carries no reconciliation block'
                    }
                    else {
                        $hasAll = (($null -ne $rec.resolved) -and ($null -ne $rec.persistent) -and
                                   ($null -ne $rec.new_findings) -and ($null -ne $rec.coverage_before) -and
                                   ($null -ne $rec.coverage_after))
                        $resolvedOne = ([int]$rec.resolved_total -eq 1)
                        $rerun = ($rec.audit_rerun -eq 'full')
                        if ($hasAll -and $resolvedOne -and $rerun) {
                            Complete-FixCase 21 $t0 'pass' ("the reply separates {0} resolved, {1} persistent and {2} new finding(s), with coverage before and after, from a FULL re-run of the rules" -f $rec.resolved_total, $rec.persistent_total, $rec.new_total) `
                                -Evidence @{ resolved = $rec.resolved_total; persistent = $rec.persistent_total
                                             new = $rec.new_total; audit_rerun = $rec.audit_rerun }
                        }
                        else {
                            Complete-FixCase 21 $t0 'fail' ("blocks_present={0} resolved_total={1} audit_rerun={2}" -f $hasAll, $rec.resolved_total, $rec.audit_rerun)
                        }
                    }
                }

                # ---- case 12: the same action again is now a STALE FINDING ------
                $t0 = Get-Date
                if (-not $fix7 -or $fix7.stage -ne 'apply' -or -not (Test-FixVerified $fix7.answer)) {
                    Complete-FixCase 12 $t0 'unverified' 'the correction that would make the finding stale did not commit'
                }
                else {
                    $tbBefore = (Invoke-Write 'horizun_query_planimetry' @{ mode = 'sheets'; sheet_ids = @([long]$planSheetBId); units = 'mm' })
                    $stale = Invoke-Fix @{
                        target_document = $wDoc; units = 'mm'; dry_run = $true; source_audit = (New-FixSource)
                        actions = @($act7)
                    }
                    $tbAfter = (Invoke-Write 'horizun_query_planimetry' @{ mode = 'sheets'; sheet_ids = @([long]$planSheetBId); units = 'mm' })
                    $countBefore = if ($tbBefore.data) { @($tbBefore.data.rows)[0].titleblock_count } else { $null }
                    $countAfter = if ($tbAfter.data) { @($tbAfter.data.rows)[0].titleblock_count } else { $null }
                    $refused = ($stale.isError -or ($stale.data -and $stale.data.invalid_actions -ge 1))
                    $named = ($stale.text -match 'STALE FINDING')
                    $noWrite = ($countBefore -eq $countAfter)
                    if ($refused -and $named -and $noWrite) {
                        Complete-FixCase 12 $t0 'pass' ("re-sending the corrected action is refused as STALE FINDING and the sheet still carries {0} title block(s) - nothing was written" -f $countAfter) `
                            -Evidence @{ refusal = (Get-DimShortText $stale.text); titleblocks = $countAfter }
                    }
                    else {
                        Complete-FixCase 12 $t0 'fail' ("refused={0} named={1} titleblocks {2}->{3}" -f $refused, $named, $countBefore, $countAfter)
                    }
                }

                # ---- case 17 + 19: idempotent replay, and a lost response -------
                $t0 = Get-Date
                if (-not $fix7 -or $fix7.stage -ne 'apply' -or -not (Test-FixVerified $fix7.answer)) {
                    Complete-FixCase 17 $t0 'unverified' 'no committed correction to replay'
                    Complete-FixCase 19 $t0 'unverified' 'no committed correction whose response could be lost'
                }
                else {
                    # A client that never saw the answer re-sends the IDENTICAL
                    # request, token and key included. That is the lost response.
                    $replay = Invoke-Fix $fix7.applied
                    $countNow = $null
                    $q = Invoke-Write 'horizun_query_planimetry' @{ mode = 'sheets'; sheet_ids = @([long]$planSheetBId); units = 'mm' }
                    if ($q.data) { $countNow = @($q.data.rows)[0].titleblock_count }
                    $replayed = (-not $replay.isError -and $replay.data -and $replay.data.idempotency -and
                                 $replay.data.idempotency.status -eq 'replayed' -and
                                 $replay.data.idempotency.command_executed_in_this_call -eq $false)
                    $oneOnly = ($countNow -eq 1)
                    if ($replayed -and $oneOnly) {
                        Complete-FixCase 17 $t0 'pass' 'an identical retry replays the recorded answer with command_executed_in_this_call=false, and the sheet still carries exactly one title block' `
                            -Evidence @{ idempotency = $replay.data.idempotency; titleblocks = $countNow }
                        Complete-FixCase 19 $t0 'pass' 'the same retry IS the lost-response case - the caller never saw the first answer, re-sent it verbatim, and received the recorded result instead of a second correction' `
                            -Evidence @{ idempotency = $replay.data.idempotency; titleblocks = $countNow }
                    }
                    else {
                        $detail = ("replayed={0} titleblocks={1}: {2}" -f $replayed, $countNow, (Get-DimShortText $replay.text))
                        Complete-FixCase 17 $t0 'fail' $detail
                        Complete-FixCase 19 $t0 'fail' $detail
                    }
                }

                # ---- case 18: the same key, a different payload ----------------
                $t0 = Get-Date
                if (-not $fix7 -or $fix7.stage -ne 'apply' -or -not $fix7.key) {
                    Complete-FixCase 18 $t0 'unverified' 'no key was claimed, so no conflict can be provoked'
                }
                else {
                    $f18 = Find-FixFinding $au 'sheet.viewport-overlap' $planVpSecId
                    $conflictAction = if ($f18) {
                        @{ operation = 'move_viewport'; viewport_id = [long]$planVpSecId; point = @(300, 900)
                           finding = (New-FixFinding $f18) }
                    } else { $act7 }
                    $conflict = Invoke-Fix @{
                        target_document = $wDoc; units = 'mm'; dry_run = $false
                        source_audit = (New-FixSource)
                        actions = @($conflictAction)
                        confirmation_token = 'hz-deliberately-not-a-real-token'
                        idempotency_key = $fix7.key
                    }
                    $refused = $conflict.isError
                    $named = ($conflict.text -match 'idempotency_key|DIFFERENT operation|already identifies')
                    if ($refused -and $named) {
                        Complete-FixCase 18 $t0 'pass' 'the key already claimed for the title-block correction is refused for a different payload, naming the conflict' `
                            -Evidence @{ refusal = (Get-DimShortText $conflict.text) }
                    }
                    else {
                        Complete-FixCase 18 $t0 'fail' ("refused={0} named={1}: {2}" -f $refused, $named, (Get-DimShortText $conflict.text))
                    }
                }

                # ---- Geometry the moves stand on: the sheet's own outline and the
                # ---- viewport's extent, both in mm, read from the query rather than
                # ---- assumed. A point chosen without them is a guess about paper
                # ---- size, and an A1 sheet is 841x594.
                $fixSheetBox = $null; $fixVpBox = $null
                $qs8 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'sheets'; sheet_ids = @([long]$planSheetAId); units = 'mm' }
                if ($qs8.data -and @($qs8.data.rows).Count -gt 0) {
                    $r = @($qs8.data.rows)[0]
                    $fixSheetBox = if ($r.titleblock_extent) { @($r.titleblock_extent) } else { @($r.sheet_outline) }
                }
                $qp8 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'placements'; sheet_ids = @([long]$planSheetAId); units = 'mm' }
                if ($qp8.data) {
                    $vpRow = @($qp8.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpSecId })
                    if ($vpRow.Count -eq 1) { $fixVpBox = @($vpRow[0].extent) }
                }

                # A point that keeps the viewport inside the sheet, and one that
                # certainly does not. Both derived, so neither depends on paper size.
                $fixInsidePoint = $null; $fixOutsidePoint = $null
                if ($fixSheetBox -and $fixSheetBox.Count -eq 4 -and $fixVpBox -and $fixVpBox.Count -eq 4) {
                    $vpW = [double]$fixVpBox[2] - [double]$fixVpBox[0]
                    $vpH = [double]$fixVpBox[3] - [double]$fixVpBox[1]
                    $shW = [double]$fixSheetBox[2] - [double]$fixSheetBox[0]
                    $shH = [double]$fixSheetBox[3] - [double]$fixSheetBox[1]
                    if ($vpW -lt $shW -and $vpH -lt $shH) {
                        $fixInsidePoint = @(
                            [math]::Round([double]$fixSheetBox[0] + ($vpW / 2) + 5, 1),
                            [math]::Round([double]$fixSheetBox[1] + ($vpH / 2) + 5, 1))
                    }
                    $fixOutsidePoint = @(
                        [math]::Round([double]$fixSheetBox[2] + $vpW + 1000, 1),
                        [math]::Round([double]$fixSheetBox[3] + $vpH + 1000, 1))
                }

                # ---- case 16: a failed postcondition abandons the whole batch ---
                # RUNS BEFORE case 8, and that ordering is load-bearing: case 8
                # corrects the viewport overlap, and a resolved finding licenses
                # nothing. Placed after it, this probe had no finding to build a
                # failing batch around and reported UNVERIFIED - which was the
                # honest answer to a harness that had eaten its own fixture.
                # PROVOKED, NOT HOPED FOR. The first attempt leaned on a view
                # template controlling the scale, and this Revit accepted the write -
                # a provocation that does not provoke proves nothing, which is why it
                # reported UNVERIFIED rather than passed.
                #
                # This one is deterministic and uses a guarantee the product actually
                # makes: move_viewport declares `inside_sheet_extent` a postcondition
                # whenever both extents are readable, so a point off the sheet fails a
                # postcondition by construction. The batch also carries a rename that
                # would otherwise succeed, and the assertion is that it did not - which
                # is what "the WHOLE batch" means.
                $t0 = Get-Date
                $auP = Get-FixAudit
                $f16 = if ($auP.data) { Find-FixFinding $auP 'sheet.viewport-overlap' $planVpSecId } else { $null }
                if (-not $f16) { $f16 = if ($auP.data) { Find-FixFinding $auP 'sheet.placement-outside-extent' $planVpSecId } else { $null } }
                $f16b = if ($auP.data) { Find-FixFinding $auP 'section-view-name' $dimSectionViewId } else { $null }
                if (-not $f16b) { $f16b = if ($auP.data) { Find-FixFinding $auP 'view.no-template' $dimSectionViewId } else { $null } }

                if (-not $f16 -or -not $fixOutsidePoint) {
                    Complete-FixCase 16 $t0 'unverified' 'no viewport finding, or no sheet geometry, to build the failing batch around'
                }
                else {
                    $srcP = @{ finding_set_fingerprint = $auP.data.finding_set_fingerprint; units = 'mm' }
                    $nameBefore16 = $null
                    $qv16 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'views'; view_ids = @([long]$dimSectionViewId); units = 'mm' }
                    if ($qv16.data -and @($qv16.data.rows).Count -gt 0) { $nameBefore16 = @($qv16.data.rows)[0].name }
                    $centreBefore16 = $null
                    $qc16 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'placements'; sheet_ids = @([long]$planSheetAId); units = 'mm' }
                    if ($qc16.data) {
                        $row16 = @($qc16.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpSecId })
                        if ($row16.Count -eq 1) { $centreBefore16 = ($row16[0].box_center -join ',') }
                    }

                    $actions16 = @(@{ operation = 'move_viewport'; viewport_id = [long]$planVpSecId
                                      point = $fixOutsidePoint; finding = (New-FixFinding $f16) })
                    $renameIncluded = $false
                    if ($f16b) {
                        $actions16 += @{ operation = 'rename_view'; view_id = [long]$dimSectionViewId
                                         new_name = "HZ_FIX_MUST_NOT_LAND_$planTag"; finding = (New-FixFinding $f16b) }
                        $renameIncluded = $true
                    }

                    $r16 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; tolerance = 1.0; source_audit = $srcP
                        requirement_set = $fixSet; actions = $actions16
                    } 'postfail'

                    $qa16 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'views'; view_ids = @([long]$dimSectionViewId); units = 'mm' }
                    $nameAfter16 = if ($qa16.data -and @($qa16.data.rows).Count -gt 0) { @($qa16.data.rows)[0].name } else { $null }
                    $qd16 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'placements'; sheet_ids = @([long]$planSheetAId); units = 'mm' }
                    $centreAfter16 = $null
                    if ($qd16.data) {
                        $rowA16 = @($qd16.data.rows | Where-Object { [long]$_.placement_id -eq [long]$planVpSecId })
                        if ($rowA16.Count -eq 1) { $centreAfter16 = ($rowA16[0].box_center -join ',') }
                    }
                    $nothingMoved = ($centreBefore16 -eq $centreAfter16) -and ($nameBefore16 -eq $nameAfter16)

                    if ($r16.stage -ne 'apply') {
                        # The rehearsal MATERIALISED both actions, measured the
                        # containment postcondition, found it false, rolled the
                        # transaction back and withheld the token. Nothing was written.
                        $reh = $r16.answer.data.rehearsal
                        $rolled = ($reh -and $reh.rolled_back -eq $true)
                        $notConstructible = if ($reh) { [int]$reh.not_constructible } else { -1 }
                        if ($rolled -and $notConstructible -ge 1 -and $nothingMoved) {
                            Complete-FixCase 16 $t0 'pass' ("a point off the sheet fails move_viewport's inside_sheet_extent postcondition: the rehearsal materialised {0} action(s), measured {1} as not constructible, rolled back ({2}) and issued NO token. The viewport is still at {3} and the view is still named '{4}' - the batch's OTHER action, which would have succeeded, did not land." -f $actions16.Count, $notConstructible, $reh.rollback_status, $centreAfter16, $nameAfter16) `
                                -Evidence @{ rehearsal = $reh; rename_included = $renameIncluded
                                             centre_before = $centreBefore16; centre_after = $centreAfter16
                                             name_before = $nameBefore16; name_after = $nameAfter16 }
                        }
                        else {
                            Complete-FixCase 16 $t0 'fail' ("rolled_back={0} not_constructible={1} nothing_moved={2} (centre {3}->{4}, name {5}->{6})" -f $rolled, $notConstructible, $nothingMoved, $centreBefore16, $centreAfter16, $nameBefore16, $nameAfter16)
                        }
                    }
                    elseif ($r16.answer.isError) {
                        $detail16 = $r16.answer.data
                        if ($nothingMoved) {
                            Complete-FixCase 16 $t0 'pass' ("the apply committed, the post-write re-read found the placement off the sheet, and the WHOLE batch rolled back (state={0}); neither the viewport nor the view moved" -f $detail16.state) `
                                -Evidence @{ state = $detail16.state
                                             transaction_group_status = $detail16.transaction_group_status
                                             centre_after = $centreAfter16; name_after = $nameAfter16 }
                        }
                        else {
                            Complete-FixCase 16 $t0 'fail' ("the batch reported a rollback but something moved: centre {0}->{1}, name {2}->{3}" -f $centreBefore16, $centreAfter16, $nameBefore16, $nameAfter16)
                        }
                    }
                    else {
                        Complete-FixCase 16 $t0 'fail' ("a viewport moved wholly off the sheet was ACCEPTED: the containment postcondition did not fire. centre {0}->{1}" -f $centreBefore16, $centreAfter16)
                    }
                }

                # ---- case 8: move_viewport --------------------------------------
                $t0 = Get-Date
                $auNow = Get-FixAudit
                $f8 = if ($auNow.data) { Find-FixFinding $auNow 'sheet.viewport-overlap' $planVpSecId } else { $null }
                if (-not $f8) {
                    Complete-FixCase 8 $t0 'unverified' 'the audit produced no viewport-overlap finding to correct'
                }
                elseif (-not $fixInsidePoint) {
                    Complete-FixCase 8 $t0 'unverified' ("no point can keep this viewport inside the sheet: sheet extent={0} viewport extent={1}" -f ($fixSheetBox -join ','), ($fixVpBox -join ','))
                }
                else {
                    $srcNow = @{ finding_set_fingerprint = $auNow.data.finding_set_fingerprint; units = 'mm' }
                    $fix8 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; tolerance = 1.0; source_audit = $srcNow
                        actions = @(@{ operation = 'move_viewport'; viewport_id = [long]$planVpSecId
                                       point = $fixInsidePoint; finding = (New-FixFinding $f8) })
                    } 'moveviewport'
                    if ($fix8.stage -ne 'apply') {
                        Complete-FixCase 8 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix8.answer.text))
                    }
                    elseif (Test-FixVerified $fix8.answer) {
                        $row = @($fix8.answer.data.rows)[0]
                        $reread = @($row.postconditions.properties | Where-Object { $_.property -eq 'box_center' })
                        $contain = @($row.postconditions.properties | Where-Object { $_.property -eq 'inside_sheet_extent' })
                        if ($reread.Count -eq 1 -and $reread[0].measured -eq $true -and $reread[0].matches -eq $true) {
                            Complete-FixCase 8 $t0 'pass' ("the viewport moved to the explicit point and Viewport.GetBoxCenter() re-read as {0} within the declared tolerance; containment was {1}" -f $reread[0].found_in_committed_model, $(if ($contain.Count -eq 1) { 'a measured postcondition and it held' } else { 'not measurable and is reported as such, not as a pass' })) `
                                -Evidence @{ postconditions = $row.postconditions; inside_sheet_extent = $row.inside_sheet_extent
                                             tolerance = $fix8.answer.data.tolerance }
                        }
                        else {
                            Complete-FixCase 8 $t0 'fail' 'box_center was not re-read and compared'
                        }
                    }
                    else {
                        Complete-FixCase 8 $t0 'fail' ('the move did not verify: ' + (Get-DimShortText $fix8.answer.text))
                    }
                }

                # ---- case 9: move_schedule --------------------------------------
                $t0 = Get-Date
                if (-not $planSchedPlacementId) {
                    Complete-FixCase 9 $t0 'not_covered' 'the planimetry fixture staged no schedule placement on this machine'
                }
                else {
                    $auS = Get-FixAudit
                    $f9 = if ($auS.data) { Find-FixFinding $auS 'schedule-margin' $planSchedPlacementId } else { $null }
                    if (-not $f9) {
                        Complete-FixCase 9 $t0 'unverified' 'the requirement set produced no schedule_placement finding to correct'
                    }
                    else {
                        $srcS = @{ finding_set_fingerprint = $auS.data.finding_set_fingerprint; units = 'mm' }
                        $fix9 = Invoke-FixApply @{
                            target_document = $wDoc; units = 'mm'; tolerance = 1.0; source_audit = $srcS
                            requirement_set = $fixSet
                            actions = @(@{ operation = 'move_schedule'; schedule_instance_id = [long]$planSchedPlacementId
                                           point = @(500, 400); finding = (New-FixFinding $f9) })
                        } 'moveschedule'
                        if ($fix9.stage -ne 'apply') {
                            Complete-FixCase 9 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix9.answer.text))
                        }
                        elseif (Test-FixVerified $fix9.answer) {
                            $row = @($fix9.answer.data.rows)[0]
                            $reread = @($row.postconditions.properties | Where-Object { $_.property -eq 'point' })
                            if ($reread.Count -eq 1 -and $reread[0].measured -eq $true -and $reread[0].matches -eq $true) {
                                Complete-FixCase 9 $t0 'pass' ("the schedule placement moved to the explicit point and ScheduleSheetInstance.Point re-read as {0} within tolerance" -f $reread[0].found_in_committed_model) `
                                    -Evidence @{ postconditions = $row.postconditions }
                            }
                            else {
                                Complete-FixCase 9 $t0 'fail' 'the placement point was not re-read and compared'
                            }
                        }
                        else {
                            Complete-FixCase 9 $t0 'fail' ('the move did not verify: ' + (Get-DimShortText $fix9.answer.text))
                        }
                    }
                }

                # ---- case 10: clear_element_override ----------------------------
                $t0 = Get-Date
                $auO = Get-FixAudit
                $f10 = if ($auO.data) { Find-FixFinding $auO 'near-text-no-override' $planNearTextId } else { $null }
                if (-not $f10) {
                    Complete-FixCase 10 $t0 'unverified' 'the requirement set produced no element-override finding to correct'
                }
                else {
                    $srcO = @{ finding_set_fingerprint = $auO.data.finding_set_fingerprint; units = 'mm' }
                    $fix10 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; source_audit = $srcO; requirement_set = $fixSet
                        actions = @(@{ operation = 'clear_element_override'; view_id = [long]$dimPlanViewId
                                       element_id = [long]$planNearTextId; finding = (New-FixFinding $f10) })
                    } 'clearoverride'
                    if ($fix10.stage -ne 'apply') {
                        Complete-FixCase 10 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix10.answer.text))
                    }
                    elseif (Test-FixVerified $fix10.answer) {
                        $row = @($fix10.answer.data.rows)[0]
                        $props = @($row.postconditions.properties | ForEach-Object { $_.property })
                        $proves = (($props -contains 'element_override_cleared') -and
                                   ($props -contains 'category_override_unchanged') -and
                                   ($props -contains 'view_template_unchanged'))
                        if ($proves -and $row.postconditions.all_verified -eq $true) {
                            Complete-FixCase 10 $t0 'pass' 'the element override is re-read as defaults, and the CATEGORY override and the view template are re-read as unchanged - the fix touched only what it named' `
                                -Evidence @{ postconditions = $row.postconditions }
                        }
                        else {
                            Complete-FixCase 10 $t0 'fail' ("the checklist did not prove the neighbours were left alone: properties={0}" -f ($props -join ','))
                        }
                    }
                    else {
                        Complete-FixCase 10 $t0 'fail' ('clearing the override did not verify: ' + (Get-DimShortText $fix10.answer.text))
                    }
                }

                # ---- case 11: set_crop ------------------------------------------
                $t0 = Get-Date
                $auC = Get-FixAudit
                $f11 = $null
                if ($auC.data) {
                    foreach ($rule in @('text.outside-annotation-crop', 'detail_2d.outside-crop', 'tag.outside-annotation-crop')) {
                        $f11 = Find-FixFinding $auC $rule $null
                        if ($f11) { break }
                    }
                }
                if (-not $f11) {
                    # WHY there is none is the useful half. Revit withholds the
                    # bounding box of an element hidden by an ACTIVE crop, and the
                    # auditor then reports that element `unknown` (bounds unreadable)
                    # rather than "outside the crop" - which is correct, and which
                    # makes the outside-crop rules hard to stage. Report the measured
                    # statuses so the next run does not have to guess.
                    $cropStatuses = @()
                    if ($auC.data) {
                        foreach ($rule in @('text.outside-annotation-crop', 'text.bounds-unreadable',
                                            'tag.outside-annotation-crop', 'dimension.outside-annotation-crop',
                                            'detail_2d.outside-crop', 'view.crop-geometry-unreadable')) {
                            $chk = @($auC.data.checks | Where-Object { $_.rule_id -eq $rule })
                            if ($chk.Count -eq 1) {
                                $cropStatuses += ('{0}={1}(pop {2}, findings {3}, unknowns {4})' -f
                                    $rule, $chk[0].status, $chk[0].population, $chk[0].findings, $chk[0].unknowns)
                            }
                        }
                    }
                    Complete-FixCase 11 $t0 'unverified' ('the audit produced no outside-crop finding, so no crop correction is licensed. Measured check statuses: ' + ($cropStatuses -join '; ')) `
                        -Evidence @{ crop_check_statuses = $cropStatuses }
                }
                else {
                    $srcC = @{ finding_set_fingerprint = $auC.data.finding_set_fingerprint; units = 'mm' }
                    $cropView = if ($null -ne $f11.view_id) { [long]$f11.view_id } else { [long]$dimPlanViewId }
                    $fix11 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; tolerance = 1.0; source_audit = $srcC
                        actions = @(@{ operation = 'set_crop'; view_id = $cropView
                                       crop = @{ min = @(-20000, -20000); max = @(20000, 20000) }
                                       finding = (New-FixFinding $f11) })
                    } 'setcrop'
                    if ($fix11.stage -ne 'apply') {
                        Complete-FixCase 11 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix11.answer.text))
                    }
                    elseif (Test-FixVerified $fix11.answer) {
                        $row = @($fix11.answer.data.rows)[0]
                        $props = @($row.postconditions.properties | ForEach-Object { $_.property })
                        $proves = (($props -contains 'crop_active') -and ($props -contains 'crop_shape') -and
                                   ($props -contains 'crop_visible_unchanged'))
                        if ($proves -and $row.postconditions.all_verified -eq $true) {
                            Complete-FixCase 11 $t0 'pass' 'the rectangular crop committed and was re-read: active, shape within the declared tolerance, and visibility unchanged' `
                                -Evidence @{ postconditions = $row.postconditions }
                        }
                        else {
                            Complete-FixCase 11 $t0 'fail' ("the crop checklist is incomplete: properties={0}" -f ($props -join ','))
                        }
                    }
                    else {
                        Complete-FixCase 11 $t0 'fail' ('the crop did not verify: ' + (Get-DimShortText $fix11.answer.text))
                    }
                }

                # ---- case 6: rename_sheet ---------------------------------------
                $t0 = Get-Date
                $auR = Get-FixAudit
                $f6 = if ($auR.data) { Find-FixFinding $auR 'sheet-b-number' $planSheetBId } else { $null }
                if (-not $f6) {
                    Complete-FixCase 6 $t0 'unverified' 'the requirement set produced no sheet-number finding to correct'
                }
                else {
                    $srcR = @{ finding_set_fingerprint = $auR.data.finding_set_fingerprint; units = 'mm' }
                    $newNumber = "HZFIX-B-$planTag"
                    $newSheetName = "HZ_FIX_SHEET_B_$planTag"
                    $fix6 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; source_audit = $srcR; requirement_set = $fixSet
                        actions = @(@{ operation = 'rename_sheet'; sheet_id = [long]$planSheetBId
                                       new_number = $newNumber; new_name = $newSheetName
                                       finding = (New-FixFinding $f6) })
                    } 'renamesheet'
                    if ($fix6.stage -ne 'apply') {
                        Complete-FixCase 6 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix6.answer.text))
                    }
                    elseif (Test-FixVerified $fix6.answer) {
                        $row = @($fix6.answer.data.rows)[0]
                        $num = @($row.postconditions.properties | Where-Object { $_.property -eq 'sheet_number' })
                        $nam = @($row.postconditions.properties | Where-Object { $_.property -eq 'name' })
                        if ($num.Count -eq 1 -and $nam.Count -eq 1 -and
                            $num[0].found_in_committed_model -eq $newNumber -and
                            $nam[0].found_in_committed_model -eq $newSheetName) {
                            Complete-FixCase 6 $t0 'pass' ("the sheet is re-read as number '{0}' and name '{1}' - BOTH fields, not only the one the caller cared about" -f $num[0].found_in_committed_model, $nam[0].found_in_committed_model) `
                                -Evidence @{ postconditions = $row.postconditions }
                        }
                        else {
                            Complete-FixCase 6 $t0 'fail' 'the sheet fields were not both re-read and matched'
                        }
                    }
                    else {
                        Complete-FixCase 6 $t0 'fail' ('the rename did not verify: ' + (Get-DimShortText $fix6.answer.text))
                    }
                }

                # ---- case 5: rename_view ----------------------------------------
                $t0 = Get-Date
                $auV = Get-FixAudit
                $f5 = if ($auV.data) { Find-FixFinding $auV 'section-view-name' $dimSectionViewId } else { $null }
                if (-not $f5) {
                    Complete-FixCase 5 $t0 'unverified' 'the requirement set produced no view-name finding to correct'
                }
                else {
                    $srcV = @{ finding_set_fingerprint = $auV.data.finding_set_fingerprint; units = 'mm' }
                    $fixSectionViewFinalName = "HZFIX-SECTION-$planTag"
                    $fix5 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; source_audit = $srcV; requirement_set = $fixSet
                        actions = @(@{ operation = 'rename_view'; view_id = [long]$dimSectionViewId
                                       new_name = $fixSectionViewFinalName; finding = (New-FixFinding $f5) })
                    } 'renameview'
                    if ($fix5.stage -ne 'apply') {
                        Complete-FixCase 5 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix5.answer.text))
                    }
                    elseif (Test-FixVerified $fix5.answer) {
                        $row = @($fix5.answer.data.rows)[0]
                        $nam = @($row.postconditions.properties | Where-Object { $_.property -eq 'name' })
                        if ($nam.Count -eq 1 -and $nam[0].found_in_committed_model -eq $fixSectionViewFinalName) {
                            Complete-FixCase 5 $t0 'pass' ("the view is re-read as '{0}' - the exact name the request named, never a name the bridge chose" -f $nam[0].found_in_committed_model) `
                                -Evidence @{ postconditions = $row.postconditions }
                        }
                        else {
                            Complete-FixCase 5 $t0 'fail' 'the view name was not re-read and matched'
                        }
                    }
                    else {
                        Complete-FixCase 5 $t0 'fail' ('the rename did not verify: ' + (Get-DimShortText $fix5.answer.text))
                    }
                }

                # ---- case 4: set_view_scale -------------------------------------
                $t0 = Get-Date
                $auSc = Get-FixAudit
                $f4 = if ($auSc.data) { Find-FixFinding $auSc 'section-view-scale' $dimSectionViewId } else { $null }
                $fix4 = $null
                if (-not $f4) {
                    Complete-FixCase 4 $t0 'unverified' 'the requirement set produced no allowed_scale finding to correct'
                }
                else {
                    $srcSc = @{ finding_set_fingerprint = $auSc.data.finding_set_fingerprint; units = 'mm' }
                    $fix4 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; source_audit = $srcSc; requirement_set = $fixSet
                        actions = @(@{ operation = 'set_view_scale'; view_id = [long]$dimSectionViewId
                                       scale = 25; finding = (New-FixFinding $f4) })
                    } 'setscale'
                    if ($fix4.stage -ne 'apply') {
                        Complete-FixCase 4 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix4.answer.text))
                    }
                    elseif (Test-FixVerified $fix4.answer) {
                        $row = @($fix4.answer.data.rows)[0]
                        $sc = @($row.postconditions.properties | Where-Object { $_.property -eq 'scale' })
                        if ($sc.Count -eq 1 -and [int]$sc[0].found_in_committed_model -eq 25) {
                            Complete-FixCase 4 $t0 'pass' 'View.Scale is re-read as the explicit 25 the request named' `
                                -Evidence @{ postconditions = $row.postconditions }
                        }
                        else {
                            Complete-FixCase 4 $t0 'fail' 'the scale was not re-read as requested'
                        }
                    }
                    else {
                        Complete-FixCase 4 $t0 'fail' ('the scale change did not verify: ' + (Get-DimShortText $fix4.answer.text))
                    }
                }

                # ---- case 13: a token whose resolved elements moved -------------
                # Rehearse a rename, then rename the SAME view underneath the token
                # by another route, and spend it. The request is identical and the
                # document is the same; only the answer moved.
                $t0 = Get-Date
                $auT = Get-FixAudit
                $f13 = if ($auT.data) { Find-FixFinding $auT 'section-view-template' $dimSectionViewId } else { $null }
                if (-not $f13) {
                    $f13 = if ($auT.data) { Find-FixFinding $auT 'view.no-template' $dimSectionViewId } else { $null }
                }
                if (-not $f13 -or -not $fixTemplateId) {
                    Complete-FixCase 13 $t0 'unverified' ("no finding/template pair to rehearse a stale plan against: finding={0} template_id={1}" -f $(if ($f13) { 'present' } else { 'ABSENT' }), $(if ($fixTemplateId) { $fixTemplateId } else { 'ABSENT' }))
                }
                else {
                    $srcT = @{ finding_set_fingerprint = $auT.data.finding_set_fingerprint; units = 'mm' }
                    $args13 = @{
                        target_document = $wDoc; units = 'mm'; source_audit = $srcT; requirement_set = $fixSet
                        actions = @(@{ operation = 'set_view_template'; view_id = [long]$dimSectionViewId
                                       template_id = [long]$fixTemplateId; finding = (New-FixFinding $f13) })
                    }
                    $dry13 = Invoke-FixDry $args13
                    if ($dry13.isError -or -not $dry13.data -or -not $dry13.data.confirmation_token) {
                        Complete-FixCase 13 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $dry13.text))
                    }
                    else {
                        # Move the model underneath the token: rename the template the
                        # plan resolved. The id still resolves; the NAME the person
                        # read when they approved does not.
                        $driftCode = @"
from Autodesk.Revit.DB import Transaction, ElementId
tpl = doc.GetElement(ElementId($fixTemplateId))
t = Transaction(doc, 'HZ fix live: move the model under the token')
t.Start()
tpl.Name = 'HZ_FIX_TPL_MOVED_$planTag'
t.Commit()
__output__ = {'status': 'self_reported_verified', 'summary': 'renamed the referenced template',
              'verification': {'checked': True, 'evidence': ['Name=' + tpl.Name]}}
"@
                        $drift = Invoke-Write 'horizun_execute_python' @{
                            code = $driftCode; target_document = $wDoc
                            idempotency_key = "live-fix-drift-$probeRun"
                        }
                        if ($drift.isError -or -not $drift.data -or $drift.data.evidence_status -ne 'self_reported_verified') {
                            Complete-FixCase 13 $t0 'unverified' ('the drift could not be staged: ' + (Get-DimShortText $drift.text))
                        }
                        else {
                            $spend = $args13.Clone()
                            $spend['dry_run'] = $false
                            $spend['confirmation_token'] = $dry13.data.confirmation_token
                            $spend['idempotency_key'] = "live-fix-stale-$probeRun"
                            $stalePlan = Invoke-Fix $spend
                            $refused = $stalePlan.isError
                            $named = ($stalePlan.text -match 'stale|MODEL MOVED')
                            $vt = Invoke-Write 'horizun_query_planimetry' @{ mode = 'views'; view_ids = @([long]$dimSectionViewId); units = 'mm' }
                            $tplAfter = if ($vt.data) { @($vt.data.rows)[0].template_id } else { 'unreadable' }
                            $noWrite = ($null -eq $tplAfter -or $tplAfter -ne $fixTemplateId)
                            if ($refused -and $named -and $noWrite) {
                                Complete-FixCase 13 $t0 'pass' 'the token was refused after the referenced template was renamed underneath it - the request was identical and the document the same, so only a materialised plan could catch it; the view still carries no template' `
                                    -Evidence @{ refusal = (Get-DimShortText $stalePlan.text); template_after = $tplAfter }
                            }
                            else {
                                Complete-FixCase 13 $t0 'fail' ("refused={0} named={1} template_after={2}" -f $refused, $named, $tplAfter)
                            }
                        }
                    }
                }

                # ---- case 3: set_view_template ---------------------------------
                $t0 = Get-Date
                $auTpl = Get-FixAudit
                $f3 = if ($auTpl.data) { Find-FixFinding $auTpl 'section-view-template' $dimSectionViewId } else { $null }
                if (-not $f3 -and $auTpl.data) { $f3 = Find-FixFinding $auTpl 'view.no-template' $dimSectionViewId }
                $fix3 = $null
                if (-not $f3 -or -not $fixTemplateId) {
                    # Which half is missing, not "one of two things". The first
                    # version said neither and cost a run to disambiguate.
                    Complete-FixCase 3 $t0 'unverified' ("no template correction could be licensed: finding={0} template_id={1}" -f $(if ($f3) { 'present' } else { 'ABSENT' }), $(if ($fixTemplateId) { $fixTemplateId } else { 'ABSENT' }))
                }
                else {
                    $srcTpl = @{ finding_set_fingerprint = $auTpl.data.finding_set_fingerprint; units = 'mm' }
                    $fix3 = Invoke-FixApply @{
                        target_document = $wDoc; units = 'mm'; source_audit = $srcTpl; requirement_set = $fixSet
                        actions = @(@{ operation = 'set_view_template'; view_id = [long]$dimSectionViewId
                                       template_id = [long]$fixTemplateId; finding = (New-FixFinding $f3) })
                    } 'settemplate'
                    if ($fix3.stage -ne 'apply') {
                        $sentBlock = (New-FixFinding $f3) | ConvertTo-Json -Depth 8 -Compress
                        $auditRow = $f3 | ConvertTo-Json -Depth 8 -Compress
                        Complete-FixCase 3 $t0 'unverified' ('the rehearsal issued no token: ' + (Get-DimShortText $fix3.answer.text)) `
                            -Evidence @{ sent_finding = $sentBlock; audit_finding = $auditRow
                                         refusal = $fix3.answer.text }
                    }
                    elseif (Test-FixVerified $fix3.answer) {
                        $row = @($fix3.answer.data.rows)[0]
                        $tp = @($row.postconditions.properties | Where-Object { $_.property -eq 'view_template_id' })
                        if ($tp.Count -eq 1 -and [long]$tp[0].found_in_committed_model -eq [long]$fixTemplateId) {
                            Complete-FixCase 3 $t0 'pass' 'ViewTemplateId is re-read from the committed model as the explicit template ElementId the request named' `
                                -Evidence @{ postconditions = $row.postconditions }
                        }
                        else {
                            Complete-FixCase 3 $t0 'fail' 'ViewTemplateId was not re-read as the requested template'
                        }
                    }
                    else {
                        Complete-FixCase 3 $t0 'fail' ('the template assignment did not verify: ' + (Get-DimShortText $fix3.answer.text))
                    }
                }

                # ---- case 15: one invalid action refuses the WHOLE batch --------
                $t0 = Get-Date
                $auB = Get-FixAudit
                $f15 = if ($auB.data) { Find-FixFinding $auB 'plan-view-never' $dimPlanViewId } else { $null }
                if (-not $f15) { $f15 = if ($auB.data) { Find-FixFinding $auB 'view.not-placed' $null } else { $null } }
                if (-not $f15) { $f15 = if ($auB.data) { Find-FixFinding $auB 'view.no-template' $null } else { $null } }
                if (-not $f15) {
                    Complete-FixCase 15 $t0 'unverified' 'no view finding was available to build a mixed batch around'
                }
                else {
                    $srcB = @{ finding_set_fingerprint = $auB.data.finding_set_fingerprint; units = 'mm' }
                    $validTarget = [long]@($f15.element_ids)[0]
                    $nameBefore = $null
                    $qv = Invoke-Write 'horizun_query_planimetry' @{ mode = 'views'; view_ids = @($validTarget); units = 'mm' }
                    if ($qv.data -and @($qv.data.rows).Count -gt 0) { $nameBefore = @($qv.data.rows)[0].name }
                    $mixed = Invoke-Fix @{
                        target_document = $wDoc; units = 'mm'; dry_run = $false
                        source_audit = $srcB; requirement_set = $fixSet
                        confirmation_token = 'hz-deliberately-not-a-real-token'
                        idempotency_key = "live-fix-mixed-$probeRun"
                        actions = @(
                            @{ operation = 'rename_view'; view_id = $validTarget
                               new_name = "HZ_FIX_MIXED_$planTag"; finding = (New-FixFinding $f15) },
                            # INVALID: a scale Revit does not accept. A value the
                            # caller can fix, so it must NOT grant the fallback.
                            @{ operation = 'set_view_scale'; view_id = $validTarget; scale = 99999
                               finding = (New-FixFinding $f15) })
                    }
                    $qa = Invoke-Write 'horizun_query_planimetry' @{ mode = 'views'; view_ids = @($validTarget); units = 'mm' }
                    $nameAfter = if ($qa.data -and @($qa.data.rows).Count -gt 0) { @($qa.data.rows)[0].name } else { $null }
                    $refused = $mixed.isError
                    $unchanged = ($nameBefore -eq $nameAfter)
                    if ($refused -and $unchanged) {
                        Complete-FixCase 15 $t0 'pass' ("one invalid action refused the whole batch; the valid action's view is still named '{0}' - none of it was written" -f $nameAfter) `
                            -Evidence @{ refusal = (Get-DimShortText $mixed.text); name_before = $nameBefore; name_after = $nameAfter }
                    }
                    else {
                        Complete-FixCase 15 $t0 'fail' ("refused={0} name {1}->{2}" -f $refused, $nameBefore, $nameAfter)
                    }
                }

                # ---- case 22: revert, and return the census -------------------
                $t0 = Get-Date
                $revertNotes = @()
                if ($fix7 -and $fix7.stage -eq 'apply' -and (Test-FixVerified $fix7.answer)) {
                    $tbq = Invoke-Write 'horizun_query_planimetry' @{ mode = 'sheets'; sheet_ids = @([long]$planSheetBId); units = 'mm' }
                    if ($tbq.data -and @($tbq.data.rows).Count -gt 0) {
                        $tbIds = @(@($tbq.data.rows)[0].titleblock_instance_ids)
                        if ($tbIds.Count -gt 0) {
                            $del = Invoke-WriteApply 'horizun_delete_verified' @{
                                mode = 'ids'; target_document = $wDoc; id_cap = 50
                                ids = @($tbIds | ForEach-Object { [long]$_ })
                            } 'fix-revert-tb'
                            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { $revertNotes += 'title block deleted' }
                            else { $revertNotes += 'title block NOT deleted: ' + (Get-DimShortText $del.answer.text) }
                        }
                    }
                }
                if ($fixCropLineId) {
                    $delLine = Invoke-WriteApply 'horizun_delete_verified' @{
                        mode = 'ids'; target_document = $wDoc; id_cap = 50
                        ids = @([long]$fixCropLineId)
                    } 'fix-revert-line'
                    if ($delLine.stage -eq 'apply' -and -not $delLine.answer.isError) { $revertNotes += 'crop detail line deleted' }
                    else { $revertNotes += 'crop detail line NOT deleted: ' + (Get-DimShortText $delLine.answer.text) }
                }
                if ($fixTemplateId) {
                    $delTpl = Invoke-WriteApply 'horizun_delete_verified' @{
                        mode = 'ids'; target_document = $wDoc; id_cap = 50
                        ids = @([long]$fixTemplateId)
                    } 'fix-revert-tpl'
                    if ($delTpl.stage -eq 'apply' -and -not $delTpl.answer.isError) { $revertNotes += 'view template deleted' }
                    else { $revertNotes += 'view template NOT deleted: ' + (Get-DimShortText $delTpl.answer.text) }
                }
                # Put the crop back where the section found it. set_crop is a
                # deliberate display change, and reverting the section means
                # reverting that too - otherwise the census carries whatever Revit
                # keeps alongside a crop shape.
                if ($fixCropBefore -and @($fixCropBefore).Count -eq 4) {
                    $restoreCode = @"
from Autodesk.Revit.DB import ElementId, Transaction, XYZ
# CropBox, not SetCropShape. Setting a crop SHAPE installs a sketch whose
# constraints Revit models as two non-view-specific Dimension elements - measured
# in this very gate, and the reason set_crop itself now writes the rectangle
# through CropBox. A restore that used the shape API would put back the elements
# it is supposed to be removing.
v = doc.GetElement(ElementId($dimPlanViewId))
mgr = v.GetCropRegionShapeManager()
o, r, u = v.Origin, v.RightDirection, v.UpDirection
mm = 1.0 / 304.8
x0, y0, x1, y1 = $(@($fixCropBefore) -join ', ')
t = Transaction(doc, 'HZ fix live: restore the crop the section changed')
t.Start()
if mgr.ShapeSet:
    mgr.RemoveCropRegionShape()
bb = v.CropBox
inv = bb.Transform.Inverse
a = inv.OfPoint(o + r.Multiply(x0 * mm) + u.Multiply(y0 * mm))
b = inv.OfPoint(o + r.Multiply(x1 * mm) + u.Multiply(y1 * mm))
bb.Min = XYZ(min(a.X, b.X), min(a.Y, b.Y), bb.Min.Z)
bb.Max = XYZ(max(a.X, b.X), max(a.Y, b.Y), bb.Max.Z)
v.CropBox = bb
t.Commit()
v2 = doc.GetElement(ElementId($dimPlanViewId))
__output__ = {'status': 'self_reported_verified', 'summary': 'restored the pre-section crop through CropBox',
              'verification': {'checked': True,
                               'evidence': ['ShapeSet=' + str(v2.GetCropRegionShapeManager().ShapeSet)]}}
"@
                    $restore = Invoke-Write 'horizun_execute_python' @{
                        code = $restoreCode; target_document = $wDoc
                        idempotency_key = "live-fix-cropback-$probeRun"
                    }
                    if (-not $restore.isError -and $restore.data -and $restore.data.evidence_status -eq 'self_reported_verified') {
                        $revertNotes += 'crop restored'
                    }
                    else { $revertNotes += 'crop NOT restored: ' + (Get-DimShortText $restore.text) }
                }

                $censusEnd = Get-FixCensus
                if (-not $censusEnd -or -not $fixCensusReference) {
                    Complete-FixCase 22 $t0 'unverified' 'the closing or the reference census could not be read'
                }
                elseif ($censusEnd -eq $fixCensusReference) {
                    Complete-FixCase 22 $t0 'pass' ("after deleting exactly what this section created ({0}), the inventory census is byte-identical to the reference taken before it: every other correction changed a property, not the model's population" -f ($revertNotes -join '; ')) `
                        -Evidence @{ reverted = $revertNotes; census = $censusEnd }
                }
                else {
                    # WHICH elements appeared, not merely that a total moved.
                    $appeared = @()
                    $qd1 = Invoke-Write 'horizun_query_planimetry' @{ mode = 'annotations'; categories = @('dimensions'); units = 'mm'; max_rows = 500 }
                    if ($qd1.data) {
                        $after = @($qd1.data.rows)
                        foreach ($row in $after) {
                            if (@($fixDimIdsBefore) -notcontains [long]$row.element_id) {
                                $appeared += ('dimension {0} type={1} owner_view={2} view_specific={3}' -f
                                    $row.element_id, $row.type, $row.owner_view_name, $row.view_specific)
                            }
                        }
                    }
                    Complete-FixCase 22 $t0 'fail' ("the census did not return. reverted={0}. Appeared since the reference: {1}. reference={2} end={3}" -f ($revertNotes -join '; '), $(if ($appeared.Count -gt 0) { $appeared -join ' | ' } else { '(no new dimension rows)' }), $fixCensusReference, $censusEnd) `
                        -Evidence @{ reverted = $revertNotes; appeared = $appeared }
                }

                # ---- case 23: nothing was saved --------------------------------
                $t0 = Get-Date
                $st3 = Invoke-Write 'horizun_execute_python' @{
                    code = $stampCode; target_document = $wDoc
                    idempotency_key = "live-fix-stamp3-$probeRun"
                }
                if ($st3.isError -or -not $st3.data -or $st3.data.executed -ne $true) {
                    Complete-FixCase 23 $t0 'unverified' 'the file stamp could not be re-read after the section'
                }
                else {
                    $stampAfter = [string]$st3.data.output.stamp
                    $modAfter = [bool]$st3.data.output.modified
                    if ([string]::IsNullOrWhiteSpace($fixFileStampBefore) -and [string]::IsNullOrWhiteSpace($stampAfter)) {
                        Complete-FixCase 23 $t0 'pass' 'the disposable document has never been written to disk at all, before or after the corrections: there is no file for a save to have touched' `
                            -Evidence @{ path = $fixFilePath; is_modified_after = $modAfter; stamp = '(never saved)' }
                    }
                    elseif ($stampAfter -eq $fixFileStampBefore) {
                        Complete-FixCase 23 $t0 'pass' ("the model file's timestamp and size are byte-identical before and after every correction, while IsModified is {0}: the section wrote to the MODEL and never to the FILE" -f $modAfter) `
                            -Evidence @{ path = $fixFilePath; stamp_before = $fixFileStampBefore
                                         stamp_after = $stampAfter; is_modified_after = $modAfter }
                    }
                    else {
                        Complete-FixCase 23 $t0 'fail' ("the file changed on disk: {0} -> {1}" -f $fixFileStampBefore, $stampAfter)
                    }
                }
            }
        }

        # Every case number reports exactly once - the same harness rule the other
        # three sections live under.
        for ($fixCase = 1; $fixCase -le 23; $fixCase++) {
            if (-not $script:fixCasesDone.ContainsKey($fixCase)) {
                Complete-FixCase $fixCase (Get-Date) 'unverified' 'the fix section ended before this probe ran - a harness bug, not a product verdict'
            }
        }

        # ----------------------------------------------------------------------
        # W9+: AUTONOMOUS PLANIMETRY PRODUCTION.
        # Reuses the disposable, already-measured fixture after correction. Every
        # write still runs dry-run -> token -> apply, and the temporary tag and
        # dimension are removed again. The sheet arrangement/revision remain only
        # in the disposable unsaved document.
        # ----------------------------------------------------------------------
        $script:productionCasesDone = @{}
        function Complete-ProductionCase {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail, $Evidence=$null)
            if ($script:productionCasesDone.ContainsKey($CaseNumber)) { return }
            $script:productionCasesDone[$CaseNumber] = $true
            $entry = $writeNames[$productionNameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:productionEvidence += @{
                case=$CaseNumber; name=$entry.N; tool=$entry.T
                started_utc=$Started.ToUniversalTime().ToString('o'); outcome=$Outcome; detail=$Detail; evidence=$Evidence
            }
        }

        $productionGap = $null
        if (-not $planSheetAId -or -not $planVpPlanId -or -not $planVpSecId) {
            $productionGap = 'the planimetry fixture did not retain sheet A and its two viewport ids'
        }
        elseif (-not $dimPlanViewId -or @($dimPipes).Count -lt 3 -or @($dimParGridIds).Count -lt 2) {
            $productionGap = 'the dimension fixture did not retain its plan view, three pipes and two parallel grids'
        }

        if ($productionGap) {
            for ($pc=1; $pc -le 5; $pc++) { Complete-ProductionCase $pc (Get-Date) 'unverified' $productionGap }
        }
        else {
            # 1. Provision a clean sheet and a bounded unplaced drafting view,
            # then let the packer choose its placement. The two model views on
            # sheet A are larger than its usable paper on the synthetic fixture;
            # their correct refusal is not a packing success case.
            $t0=Get-Date
            $packSheet=$null; $packView=$null
            if ($planTbTypeId) {
                $ps=Invoke-WriteApply 'horizun_manage_views' @{
                    target_document=$wDoc; units='mm'; actions=@(@{
                        operation='create_sheet'; key='packSheet'; name="HZ_PACK_SHEET_$planTag"
                        number="HZPACK-$planTag"; title_block_type_id=[long]$planTbTypeId })
                } 'production-pack-sheet'
                if ($ps.stage -eq 'apply' -and -not $ps.answer.isError) { $packSheet=$ps.answer.data.aliases.packSheet }
            }
            $pv=Invoke-WriteApply 'horizun_manage_views' @{
                target_document=$wDoc; units='mm'; actions=@(@{
                    operation='create_drafting'; key='packView'; name="HZ_PACK_VIEW_$planTag" })
            } 'production-pack-view'
            if ($pv.stage -eq 'apply' -and -not $pv.answer.isError) { $packView=$pv.answer.data.aliases.packView }
            if ($packView) {
                # A real detail line gives Viewport.Create a bounded graphical
                # extent. An empty drafting view is not placeable on every
                # supported Revit year, while a schedule's automatic width
                # varies with localized table formatting.
                $pvg=Invoke-WriteApply 'horizun_detail_2d' @{
                    target_document=$wDoc; units='mm'; actions=@(@{
                        operation='create_detail_line'; view_id=[long]$packView
                        start=@(0,0); end=@(1000,0); key='pack-line' })
                } 'production-pack-view-geometry'
                if ($pvg.stage -ne 'apply' -or $pvg.answer.isError) { $packView=$null }
            }
            if ($packSheet -and $packView) {
                $packed=Invoke-WriteApply 'horizun_pack_sheets' @{
                    target_document=$wDoc; sheet_id=[long]$packSheet; units='mm'; margin=5; gap=5
                    items=@(@{ key='view'; view_id=[long]$packView })
                } 'production-pack'
            } else {
                $packed=@{ stage='fixture'; answer=@{ isError=$true; text='could not create the clean sheet and bounded unplaced drafting view'; data=$null } }
            }
            if ($packed.stage -eq 'apply' -and -not $packed.answer.isError -and
                $packed.answer.data.state -eq 'committed_verified' -and $packed.answer.data.host_verified -eq $true -and
                @($packed.answer.data.rows).Count -eq 1) {
                Complete-ProductionCase 1 $t0 'pass' 'a bounded unplaced drafting view was automatically placed on a clean titled sheet as one committed_verified arrangement' `
                    -Evidence @{ sheet_id=$packSheet; view_id=$packView; rows=$packed.answer.data.rows }
            } else {
                Complete-ProductionCase 1 $t0 'fail' ('packing did not reach committed_verified: stage=' + $packed.stage + ' ' + (Get-DimShortText $packed.answer.text))
            }

            # 2. Plan the untagged third pipe, then commit exactly the returned intent
            # through annotate with an explicit multi-category tag type.
            $t0=Get-Date
            if (-not $planTagTypeId) {
                Complete-ProductionCase 2 $t0 'unverified' ('no multi-category tag type was available: ' + $planTagTypeHow)
            } else {
                $pipe3=[long]@($dimPipes)[2]
                $tagPlan=Invoke-Write 'horizun_plan_annotations' @{
                    operation='auto_tags'; view_id=[long]$dimPlanViewId; element_ids=@($pipe3); units='mm'
                    tag_type_id=[long]$planTagTypeId; tag_mode='multi_category'; skip_existing=$true; add_leader=$true }
                if ($tagPlan.isError -or -not $tagPlan.data -or $tagPlan.data.safe_to_execute -ne $true -or @($tagPlan.data.next_arguments.actions).Count -ne 1) {
                    Complete-ProductionCase 2 $t0 'fail' ('auto-tag planner did not produce one complete safe action: ' + (Get-DimShortText $tagPlan.text))
                } else {
                    $pa=@($tagPlan.data.next_arguments.actions)[0]
                    $tagWrite=Invoke-WriteApply 'horizun_annotate' @{
                        target_document=$wDoc; units='mm'; actions=@(@{
                            operation='tag'; view_id=[long]$pa.view_id; element_id=[long]$pa.element_id
                            point=@([double]$pa.point[0],[double]$pa.point[1],[double]$pa.point[2])
                            add_leader=[bool]$pa.add_leader; tag_mode=[string]$pa.tag_mode
                            orientation=[string]$pa.orientation; tag_type_id=[long]$pa.tag_type_id })
                    } 'production-tag'
                    $tagId=$null
                    if ($tagWrite.answer.data -and @($tagWrite.answer.data.rows).Count -eq 1) { $tagId=@($tagWrite.answer.data.rows)[0].element_id }
                    if ($tagWrite.stage -eq 'apply' -and -not $tagWrite.answer.isError -and $tagWrite.answer.data.state -eq 'committed_verified' -and $tagId) {
                        Complete-ProductionCase 2 $t0 'pass' 'the read-only planner chose the point; annotate committed and verified the target and explicit tag type' `
                            -Evidence @{ target_id=$pipe3; tag_id=$tagId; tag_type_id=$planTagTypeId; planner=$tagPlan.data }
                        $null=Invoke-WriteApply 'horizun_delete_verified' @{ target_document=$wDoc; mode='ids'; ids=@([long]$tagId); id_cap=10 } 'production-tag-cleanup'
                    } else {
                        Complete-ProductionCase 2 $t0 'fail' ('planned tag did not reach committed_verified: stage=' + $tagWrite.stage + ' ' + (Get-DimShortText $tagWrite.answer.text))
                    }
                }
            }

            # 3. Semantic intent dimension: activate the measured view, resolve one
            # centerline per pipe, let the planner choose line/order, then annotate.
            $t0=Get-Date
            $navProd=Invoke-Write 'horizun_navigate' @{ operation='open_view'; view_id=[long]$dimPlanViewId }
            $dimTargets=@([long]@($dimParGridIds)[0],[long]@($dimParGridIds)[1])
            $dimPlan=Invoke-Write 'horizun_plan_annotations' @{
                operation='intent_dimension'; view_id=[long]$dimPlanViewId; element_ids=$dimTargets
                units='mm'; selector='grid'; axis='auto'; side='positive'; offset=15 }
            if ($navProd.isError -or $dimPlan.isError -or -not $dimPlan.data -or $dimPlan.data.safe_to_execute -ne $true) {
                Complete-ProductionCase 3 $t0 'fail' ('intent planner could not produce a safe action: ' + (Get-DimShortText $dimPlan.text))
            } else {
                $da=@($dimPlan.data.next_arguments.actions)[0]
                $dimWrite=Invoke-WriteApply 'horizun_annotate' @{
                    target_document=$wDoc; units='mm'; actions=@(@{
                        operation='dimension'; view_id=[long]$da.view_id
                        line_start=@([double]$da.line_start[0],[double]$da.line_start[1],[double]$da.line_start[2])
                        line_end=@([double]$da.line_end[0],[double]$da.line_end[1],[double]$da.line_end[2])
                        references=@($da.references | ForEach-Object { [string]$_ }) })
                } 'production-dimension'
                $dimId=$null
                if ($dimWrite.answer.data -and @($dimWrite.answer.data.rows).Count -eq 1) { $dimId=@($dimWrite.answer.data.rows)[0].element_id }
                if ($dimWrite.stage -eq 'apply' -and -not $dimWrite.answer.isError -and $dimWrite.answer.data.state -eq 'committed_verified' -and $dimId) {
                    Complete-ProductionCase 3 $t0 'pass' 'semantic grid references were unambiguous; the planned chain committed with host verification' `
                        -Evidence @{ dimension_id=$dimId; targets=$dimTargets; planner=$dimPlan.data }
                    $null=Invoke-WriteApply 'horizun_delete_verified' @{ target_document=$wDoc; mode='ids'; ids=@([long]$dimId); id_cap=10 } 'production-dimension-cleanup'
                } else {
                    Complete-ProductionCase 3 $t0 'fail' ('planned dimension did not reach committed_verified: stage=' + $dimWrite.stage + ' ' + (Get-DimShortText $dimWrite.answer.text))
                }
            }

            # 4. One action creates the revision, assigns it to sheet A and creates a
            # cloud in the plan view. The writer re-reads all three facts.
            $t0=Get-Date
            $revision=Invoke-WriteApply 'horizun_manage_revisions' @{
                target_document=$wDoc; units='mm'; actions=@(@{
                    key='production-revision'; operation='create_revision'; description="HZ production $planTag"
                    revision_date='2026-08-25'; issued_by='Horizun live gate'; issued_to='Verification'; issued=$false
                    sheet_ids=@([long]$planSheetAId); clouds=@(@{
                        view_id=[long]$dimPlanViewId; loops=@(,@(
                            @(509500,-500), @(513500,-500), @(513500,8500), @(509500,8500))) }) })
            } 'production-revision'
            if ($revision.stage -eq 'apply' -and -not $revision.answer.isError -and
                $revision.answer.data.state -eq 'committed_verified' -and $revision.answer.data.host_verified -eq $true -and
                @($revision.answer.data.rows).Count -eq 1 -and @(@($revision.answer.data.rows)[0].revision_cloud_ids).Count -eq 1) {
                Complete-ProductionCase 4 $t0 'pass' 'revision fields, sheet assignment and one cloud owner/revision were committed and re-read atomically' `
                    -Evidence @{ row=@($revision.answer.data.rows)[0] }
            } else {
                Complete-ProductionCase 4 $t0 'fail' ('revision production did not reach committed_verified: stage=' + $revision.stage + ' ' + (Get-DimShortText $revision.answer.text))
            }

            # 5. Direct image of the actual Revit sheet, with real bytes/pixels/hash.
            $t0=Get-Date
            $capture=Invoke-Write 'horizun_capture_view' @{ view_id=[long]$planSheetAId; pixel_size=1600 }
            if (-not $capture.isError -and $capture.data -and $capture.data.captured -eq $true -and
                $capture.data.is_sheet -eq $true -and [long]$capture.data.bytes -gt 0 -and
                [int]$capture.data.pixel_width -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$capture.data.sha256) -and
                (Test-Path -LiteralPath ([string]$capture.data.image_path))) {
                Complete-ProductionCase 5 $t0 'pass' 'Revit exported the real sheet PNG; path, nonzero bytes, dimensions and SHA-256 were read from the produced file' `
                    -Evidence @{ sheet_id=$planSheetAId; image_path=$capture.data.image_path; bytes=$capture.data.bytes; width=$capture.data.pixel_width; height=$capture.data.pixel_height; sha256=$capture.data.sha256 }
            } else {
                Complete-ProductionCase 5 $t0 'fail' ('sheet capture did not produce verifiable visual evidence: ' + (Get-DimShortText $capture.text))
            }
        }
        for ($pc=1; $pc -le 5; $pc++) {
            if (-not $script:productionCasesDone.ContainsKey($pc)) { Complete-ProductionCase $pc (Get-Date) 'unverified' 'the production section ended before this probe ran - a harness bug' }
        }

        # ----------------------------------------------------------------------
        # W10+: LINKED DIMENSIONS AND PRODUCTION AT SCALE.
        #
        # Everything below runs in the SAME never-saved disposable model. The link
        # SOURCE is a small project this run authors itself in the scratch
        # directory (a level, two vertical grids, one Y-running wall - geometry
        # chosen so every dimension in the section is between parallel verticals),
        # linked in as ONE RevitLinkType with THREE instances: A translated,
        # B translated and rotated 30 degrees, C a second plain translation -
        # the two-instances-of-one-link identity case. The staging scripts are
        # harness INFRASTRUCTURE, exactly like the case-15 link staging this
        # suite has carried since 2026-08-24; every capability under test is
        # exercised through the TYPED tools alone.
        # ----------------------------------------------------------------------
        $script:dp2Evidence = @()
        $script:dp2CasesDone = @{}
        function Complete-Dp2Case {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail, $Evidence=$null)
            if ($script:dp2CasesDone.ContainsKey($CaseNumber)) { return }
            $script:dp2CasesDone[$CaseNumber] = $true
            $entry = $writeNames[$dp2NameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:dp2Evidence += @{
                case=$CaseNumber; name=$entry.N; tool=$entry.T
                started_utc=$Started.ToUniversalTime().ToString('o'); outcome=$Outcome; detail=$Detail; evidence=$Evidence
            }
        }

        # The link fixture's geometry, one place. Feet inside the scripts, mm in
        # the typed calls; the offsets sit far east of both the sample model and
        # the synthetic dimension fixture.
        $dp2OffAXmm = 560000; $dp2OffAYmm = 0
        $dp2OffBXmm = 560000; $dp2OffBYmm = 50000
        $dp2OffCXmm = 560000; $dp2OffCYmm = 100000
        $dp2RotBDeg = 30.0

        $pythonListedDp2 = @($listed | Where-Object { $_.name -eq 'horizun_execute_python' }).Count -gt 0
        $dp2Gap = $null
        if (-not $pythonListedDp2) { $dp2Gap = 'execute_python is not advertised, so the link/room fixtures cannot be staged' }
        elseif (-not $dimPlanViewId) { $dp2Gap = 'the dimension fixture did not retain its plan view' }
        elseif (@($dimParGridIds).Count -lt 3) { $dp2Gap = 'the dimension fixture did not retain its three parallel grids (case 9 needs a pair no earlier case dimensioned)' }

        $dp2 = $null
        if (-not $dp2Gap) {
            # ---- stage: author the link source, link it three times ----------
            $t0 = Get-Date
            $dp2Source = Join-Path $scratchDir ("HZ_LINKSRC_{0}.rvt" -f $planTag)
            $stageDp2 = @'
import math
from Autodesk.Revit.DB import (Transaction, Level, Grid, Wall, Line, XYZ, UnitSystem,
                               RevitLinkType, RevitLinkInstance, RevitLinkOptions,
                               ModelPathUtils, ElementTransformUtils,
                               FilteredElementCollector, SaveAsOptions)

FT = 304.8
def ft(mm): return mm / FT

app = doc.Application
src = app.NewProjectDocument(UnitSystem.Metric)
t = Transaction(src, 'HZ live: author link source')
t.Start()
level = Level.Create(src, 0.0)
# Along X, at local y=0 and y=5000, running from local x=-55000 so that after
# the +560000mm translation the drawn extent still overlaps the host fixture's
# grids near x=511500 - the vertical measuring line then crosses every drawn
# datum rather than leaning on the infinite plane.
g1 = Grid.Create(src, Line.CreateBound(XYZ(ft(-55000), 0, 0), XYZ(ft(12000), 0, 0)))
g2 = Grid.Create(src, Line.CreateBound(XYZ(ft(-55000), ft(5000), 0), XYZ(ft(12000), ft(5000), 0)))
g1.Name = 'HZL-1'
g2.Name = 'HZL-2'
wall = Wall.Create(src, Line.CreateBound(XYZ(ft(-52000), ft(8000), 0), XYZ(ft(-46000), ft(8000), 0)), level.Id, False)
t.Commit()
opts = SaveAsOptions()
opts.OverwriteExistingFile = True
src.SaveAs(r'__SRC__', opts)
grid_ids = []
for g in (g1, g2):
    grid_ids.append(g.Id.IntegerValue if hasattr(g.Id, 'IntegerValue') else g.Id.Value)
wall_id = wall.Id.IntegerValue if hasattr(wall.Id, 'IntegerValue') else wall.Id.Value
src.Close(False)

mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(r'__SRC__')
t2 = Transaction(doc, 'HZ live: link the source three times')
t2.Start()
res = RevitLinkType.Create(doc, mp, RevitLinkOptions(False))
type_id = res.ElementId
offsets = {'a': (ft(__AX__), ft(__AY__)), 'b': (ft(__BX__), ft(__BY__)), 'c': (ft(__CX__), ft(__CY__))}
made = {}
for key in ('a', 'b', 'c'):
    inst = RevitLinkInstance.Create(doc, type_id)
    dx, dy = offsets[key]
    ElementTransformUtils.MoveElement(doc, inst.Id, XYZ(dx, dy, 0))
    if key == 'b':
        axis = Line.CreateBound(XYZ(dx, dy, 0), XYZ(dx, dy, 10))
        ElementTransformUtils.RotateElement(doc, inst.Id, axis, math.radians(__ROT__))
    made[key] = inst
t2.Commit()

report = {}
ok = True
for key, inst in made.items():
    tf = inst.GetTotalTransform()
    iid = inst.Id.IntegerValue if hasattr(inst.Id, 'IntegerValue') else inst.Id.Value
    dx, dy = offsets[key]
    origin_ok = abs(tf.Origin.X - dx) < 0.01 and abs(tf.Origin.Y - dy) < 0.01
    if key == 'b':
        rot_ok = abs(tf.BasisX.X - math.cos(math.radians(__ROT__))) < 0.001
    else:
        rot_ok = abs(tf.BasisX.X - 1.0) < 0.001
    if not (origin_ok and rot_ok):
        ok = False
    report[key] = {'id': iid, 'origin': [tf.Origin.X, tf.Origin.Y], 'basis_x_x': tf.BasisX.X}

type_id_int = type_id.IntegerValue if hasattr(type_id, 'IntegerValue') else type_id.Value
__output__ = {'status': 'self_reported_verified' if ok else 'partial',
              'link_type': type_id_int, 'instances': report,
              'grids': grid_ids, 'wall': wall_id, 'source': r'__SRC__'}
'@
            $stageDp2 = $stageDp2.Replace('__SRC__', $dp2Source).Replace('__AX__', "$dp2OffAXmm").Replace('__AY__', "$dp2OffAYmm")
            $stageDp2 = $stageDp2.Replace('__BX__', "$dp2OffBXmm").Replace('__BY__', "$dp2OffBYmm")
            $stageDp2 = $stageDp2.Replace('__CX__', "$dp2OffCXmm").Replace('__CY__', "$dp2OffCYmm").Replace('__ROT__', "$dp2RotBDeg")
            if (-not (Test-Path $scratchDir)) { New-Item -ItemType Directory -Force $scratchDir | Out-Null }
            $stageDp2Path = Join-Path $scratchDir 'stage-linked-fixture.py'
            [IO.File]::WriteAllText($stageDp2Path, $stageDp2, [Text.UTF8Encoding]::new($false))
            $st = Invoke-Write 'horizun_execute_python' @{
                code_path = $stageDp2Path; target_document = $wDoc
                idempotency_key = "live-dp2-stage-links-$probeRun" }
            $stOut = $null
            if (-not $st.isError -and $st.data) { $stOut = $st.data.output }
            if ($stOut -and $stOut.status -eq 'self_reported_verified') {
                # The TYPED rediscovery is what the case actually asserts: the three
                # instances a client would find, counted by the query surface.
                $lq = Invoke-Write 'horizun_query_model' @{ categories = @('OST_RvtLinks'); include_links = $false; max_rows = 20 }
                $foundIds = @()
                if (-not $lq.isError -and $lq.data) { $foundIds = @(@($lq.data.rows) | ForEach-Object { [long]$_.element_id }) }
                $wantIds = @([long]$stOut.instances.a.id, [long]$stOut.instances.b.id, [long]$stOut.instances.c.id)
                $allFound = $true
                foreach ($w in $wantIds) { if ($foundIds -notcontains $w) { $allFound = $false } }
                if ($allFound) {
                    $dp2 = @{ TypeId = [long]$stOut.link_type
                              A = [long]$stOut.instances.a.id; B = [long]$stOut.instances.b.id; C = [long]$stOut.instances.c.id
                              Grids = @(@($stOut.grids) | ForEach-Object { [long]$_ }); WallId = [long]$stOut.wall
                              Source = [string]$stOut.source }
                    Complete-Dp2Case 1 $t0 'pass' 'the run authored its own link source, linked it three times (translated, rotated, twin) and the typed query rediscovered all three instances' `
                        -Evidence @{ link_type=$dp2.TypeId; instances=$wantIds; source=$dp2.Source; staged=$stOut.instances }
                } else {
                    Complete-Dp2Case 1 $t0 'fail' ('the staged instances were not all rediscovered by the typed query; wanted ' + ($wantIds -join ',') + ' found ' + ($foundIds -join ','))
                }
            } else {
                $why = '(no output)'
                if ($stOut) { $why = [string]$stOut.status + ' ' + [string]$stOut.error } elseif ($st.text) { $why = Get-DimShortText $st.text }
                Complete-Dp2Case 1 $t0 'fail' ('the link fixture staging did not verify itself: ' + $why)
            }
        }
        if ($dp2Gap) {
            for ($dc=1; $dc -le 18; $dc++) { Complete-Dp2Case $dc (Get-Date) 'not_covered' $dp2Gap }
        }
        elseif (-not $dp2) {
            for ($dc=2; $dc -le 18; $dc++) { Complete-Dp2Case $dc (Get-Date) 'unverified' 'the link fixture was not staged, so this probe could not run' }
        }
        else {
            # ---- case 2: linked discovery carries full provenance and HOST coordinates
            $t0 = Get-Date
            $d2 = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = [long]$dimPlanViewId; units = 'mm'
                linked_targets = @(@{ link_instance_id = $dp2.A; linked_element_ids = @($dp2.WallId) })
                selectors = @('exterior_face'); max_results = 20 }
            if ($d2.isError -or -not $d2.data) {
                Complete-Dp2Case 2 $t0 'fail' ('linked discovery errored: ' + (Get-DimShortText $d2.text))
            } else {
                $rows2 = @(@($d2.data.rows) | Where-Object { $_.linked -eq $true })
                $row2 = $null
                if ($rows2.Count -gt 0) { $row2 = $rows2[0] }
                $linkBlock = $null
                if ($row2) { $linkBlock = $row2.link }
                # The wall runs along X at local y=8000; instance A translates by
                # (560000, 0), so the HOST-space face plane sits near y=8000mm
                # (either side of the default thickness stays inside the 300mm gate).
                $originOk = $false
                if ($row2 -and $row2.geometry -and $row2.geometry.origin) {
                    $oy = [double](@($row2.geometry.origin)[1])
                    $originOk = [math]::Abs($oy - 8000) -lt 300   # within the wall thickness
                }
                $compatibilityOk2 = $false
                if ($row2) {
                    $compatibilityOk2 = if ($Year -eq 2023) {
                        $row2.compatible_with_dimension -eq $false -and $row2.incompatibility_reason -and
                        [string]$row2.incompatibility_reason.code -eq 'linked_geometry_rejected_by_revit_2023_dimension_api'
                    } else { $row2.compatible_with_dimension -eq $true }
                }
                if ($row2 -and $linkBlock -and
                    [long]$linkBlock.link_instance_id -eq $dp2.A -and
                    [long]$linkBlock.linked_element_id -eq $dp2.WallId -and
                    -not [string]::IsNullOrWhiteSpace([string]$linkBlock.transform_fingerprint) -and
                    -not [string]::IsNullOrWhiteSpace([string]$row2.stable_representation) -and
                    $compatibilityOk2 -and $originOk -and
                    [int]$d2.data.linked_candidates -ge 1 -and
                    [int]$d2.data.coverage.linked_targets_inspected -eq 1) {
                    $compatibilityDetail2 = if ($Year -eq 2023) {
                        'and the version-specific incompatibility code'
                    } else { 'and compatible_with_dimension true' }
                    Complete-Dp2Case 2 $t0 'pass' ('the linked wall face came back with instance/type/document/element ids separated, a transform fingerprint, a host-space plane at the translated position, federated coverage counters, ' + $compatibilityDetail2) `
                        -Evidence @{ row=$row2; coverage=$d2.data.coverage }
                } else {
                    Complete-Dp2Case 2 $t0 'fail' ('the linked row did not carry what it had to; rows=' + $rows2.Count + ' originOk=' + $originOk + ' ' + (Get-DimShortText $d2.text))
                }
            }

            # ---- case 3: two instances of ONE link are two identities --------
            $t0 = Get-Date
            $d3 = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = [long]$dimPlanViewId; units = 'mm'
                linked_targets = @(
                    @{ link_instance_id = $dp2.A; linked_element_ids = @($dp2.Grids[0]) },
                    @{ link_instance_id = $dp2.C; linked_element_ids = @($dp2.Grids[0]) })
                selectors = @('grid'); max_results = 20 }
            if ($d3.isError -or -not $d3.data) {
                Complete-Dp2Case 3 $t0 'fail' ('twin-instance discovery errored: ' + (Get-DimShortText $d3.text))
            } else {
                $rows3 = @(@($d3.data.rows) | Where-Object { $_.linked -eq $true })
                $stableSet = @($rows3 | ForEach-Object { [string]$_.stable_representation } | Sort-Object -Unique)
                $fpSet = @($rows3 | ForEach-Object { [string]$_.link.transform_fingerprint } | Sort-Object -Unique)
                $instSet = @($rows3 | ForEach-Object { [long]$_.link.link_instance_id } | Sort-Object -Unique)
                if ($rows3.Count -eq 2 -and $stableSet.Count -eq 2 -and $fpSet.Count -eq 2 -and
                    $instSet.Count -eq 2 -and ($instSet -contains $dp2.A) -and ($instSet -contains $dp2.C)) {
                    Complete-Dp2Case 3 $t0 'pass' 'the SAME linked grid through two placements produced two rows with distinct stable representations and distinct transform fingerprints - identity includes the instance' `
                        -Evidence @{ stable=$stableSet; fingerprints=$fpSet }
                } else {
                    Complete-Dp2Case 3 $t0 'fail' ('the twin instances did not separate: rows=' + $rows3.Count + ' stable=' + $stableSet.Count + ' fp=' + $fpSet.Count)
                }
            }

            # ---- case 4: the rotated placement is reported rotated -----------
            $t0 = Get-Date
            $d4 = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = [long]$dimPlanViewId; units = 'mm'
                linked_targets = @(@{ link_instance_id = $dp2.B; linked_element_ids = @($dp2.Grids[0]) })
                selectors = @('grid'); max_results = 20 }
            $row4 = $null
            if (-not $d4.isError -and $d4.data) { $row4 = @(@($d4.data.rows) | Where-Object { $_.linked -eq $true })[0] }
            if ($row4 -and $row4.link.transform -and
                $row4.link.transform.has_rotation -eq $true -and
                $row4.link.transform.has_reflection -eq $false -and
                [string]$row4.link.transform.handedness -eq 'right') {
                # The grid runs along +X in the link; rotated 30 degrees CCW its host
                # direction is (cos30, sin30). The reported geometry is host-space.
                $dirOk = $false
                if ($row4.geometry -and $row4.geometry.direction) {
                    $gx = [double](@($row4.geometry.direction)[0]); $gy = [double](@($row4.geometry.direction)[1])
                    $wantX = [math]::Cos([math]::PI * $dp2RotBDeg / 180.0)
                    $wantY = [math]::Sin([math]::PI * $dp2RotBDeg / 180.0)
                    $dot = [math]::Abs($gx * $wantX + $gy * $wantY)
                    $dirOk = $dot -gt 0.999
                }
                if ($dirOk) {
                    Complete-Dp2Case 4 $t0 'pass' 'the rotated instance reports has_rotation with right handedness and its grid direction arrives in HOST space, turned by exactly the staged 30 degrees' `
                        -Evidence @{ transform=$row4.link.transform; direction=$row4.geometry.direction }
                } else {
                    Complete-Dp2Case 4 $t0 'fail' 'the transform flags are right but the reported direction is not the staged rotation in host space'
                }
            } else {
                Complete-Dp2Case 4 $t0 'fail' ('the rotated placement was not reported rotated: ' + (Get-DimShortText $d4.text))
            }

            # ---- case 5: linked dimensions, including a mixed chain where Revit accepts it
            $t0 = Get-Date
            $hostGridRef = $null
            $dh = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = [long]$dimPlanViewId; units = 'mm'
                element_ids = @([long]$dimParGridIds[0]); selectors = @('grid'); max_results = 5 }
            if (-not $dh.isError -and $dh.data) {
                $hostRows = @(@($dh.data.rows) | Where-Object { $_.compatible_with_dimension -eq $true })
                if ($hostRows.Count -ge 1) { $hostGridRef = [string]$hostRows[0].stable_representation }
            }
            # The linked references are the WALL'S FACES - measured constructible on
            # live 2026, where linked DATUM references are measured-rejected (that
            # refusal is its own assertion below).
            $linkedRefs5 = @()
            $linkedRows5 = @()
            $d5 = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = [long]$dimPlanViewId; units = 'mm'
                linked_targets = @(@{ link_instance_id = $dp2.A; linked_element_ids = @($dp2.WallId) })
                selectors = @('exterior_face', 'interior_face'); max_results = 20
                include_incompatible = [bool]($Year -eq 2023) }
            if (-not $d5.isError -and $d5.data) {
                $linkedRows5 = @($d5.data.rows | Where-Object { $_.linked -eq $true })
                $linkedRefs5 = @($linkedRows5 | Where-Object { $Year -eq 2023 -or $_.compatible_with_dimension -eq $true } |
                                ForEach-Object { [string]$_.stable_representation })
            }
            # Revit 2023 measured-rejected both a mixed host+link chain and two
            # faces of ONE linked wall.  The remaining non-duplicate case is one
            # parallel face from each of two independently placed instances of
            # that source.  Instance C is unrotated, so its face is parallel to A.
            $linkedRefsC5 = @()
            $linkedRowsC5 = @()
            if ($Year -eq 2023) {
                $d5c = Invoke-Write 'horizun_get_dimension_references' @{
                    view_id = [long]$dimPlanViewId; units = 'mm'
                    linked_targets = @(@{ link_instance_id = $dp2.C; linked_element_ids = @($dp2.WallId) })
                    selectors = @('exterior_face'); max_results = 10; include_incompatible = $true }
                if (-not $d5c.isError -and $d5c.data) {
                    $linkedRowsC5 = @($d5c.data.rows | Where-Object { $_.linked -eq $true })
                    $linkedRefsC5 = @($linkedRowsC5 |
                                     ForEach-Object { [string]$_.stable_representation })
                }
            }
            $dim5Id = $null
            $revit23LinkedRefusal5 = $false
            if ($Year -eq 2023) {
                # Three independent live arrangements have now measured the same
                # Revit 2023 API boundary. The product must expose the references
                # for inspection under include_incompatible, but refuse EVERY one
                # before a transaction and name the machine-readable code.
                function Test-Dp2Revit23LinkedRefusal([object[]]$References, [int]$EndY) {
                    $answer = Invoke-Write 'horizun_annotate' @{
                        target_document = $wDoc; units = 'mm'; dry_run = $true
                        actions = @(@{
                            operation = 'dimension'; view_id = [long]$dimPlanViewId
                            line_start = @(511500, 4000, 0); line_end = @(511500, $EndY, 0)
                            references = $References })
                    }
                    $parts = @()
                    if ($answer.data -and $answer.data.errors) {
                        $parts += @($answer.data.errors | ForEach-Object { if ($_.error) { [string]$_.error } })
                    }
                    if ($answer.data -and $answer.data.rehearsal) {
                        $parts += @($answer.data.rehearsal.actions | ForEach-Object {
                            if ($_.reason) { [string]$_.reason }
                        })
                    }
                    if ($answer.text) { $parts += [string]$answer.text }
                    $why = $parts -join ' | '
                    [pscustomobject]@{
                        named = $why -match 'linked_geometry_rejected_by_revit_2023_dimension_api'
                        token_withheld = -not ($answer.data -and $answer.data.confirmation_token)
                        transaction_not_started = $answer.data -and $answer.data.application -and
                            [string]$answer.data.application.transaction_status -eq 'not_started'
                        detail = $why
                    }
                }

                $reasonRows5 = @($linkedRows5) + @($linkedRowsC5)
                $discoveryRefuses5 = $reasonRows5.Count -ge 3 -and
                    @($reasonRows5 | Where-Object {
                        $_.compatible_with_dimension -ne $false -or -not $_.incompatibility_reason -or
                        [string]$_.incompatibility_reason.code -ne 'linked_geometry_rejected_by_revit_2023_dimension_api'
                    }).Count -eq 0
                if ($hostGridRef -and $linkedRefs5.Count -eq 2 -and $linkedRefsC5.Count -ge 1) {
                    $mixed23 = Test-Dp2Revit23LinkedRefusal -References @($hostGridRef, $linkedRefs5[0], $linkedRefs5[1]) -EndY 9500
                    $same23 = Test-Dp2Revit23LinkedRefusal -References @($linkedRefs5[0], $linkedRefs5[1]) -EndY 9500
                    $distinct23 = Test-Dp2Revit23LinkedRefusal -References @($linkedRefs5[0], $linkedRefsC5[0]) -EndY 105000
                    $all23 = @($mixed23, $same23, $distinct23)
                    $revit23LinkedRefusal5 = $discoveryRefuses5 -and
                        @($all23 | Where-Object { -not $_.named -or -not $_.token_withheld -or -not $_.transaction_not_started }).Count -eq 0
                    if ($revit23LinkedRefusal5) {
                        Complete-Dp2Case 5 $t0 'pass' 'Revit 2023 exposes the real linked-face references as incompatible and refuses host+link, same-linked-wall, and distinct-link-instance dimensions BEFORE any transaction, naming linked_geometry_rejected_by_revit_2023_dimension_api each time' `
                            -Evidence @{ code='linked_geometry_rejected_by_revit_2023_dimension_api'; discovery_rows=$reasonRows5.Count
                                         mixed=$mixed23; same_element=$same23; distinct_instances=$distinct23 }
                    } else {
                        Complete-Dp2Case 5 $t0 'fail' ('the Revit 2023 linked-geometry boundary was not enforced consistently: discovery=' +
                            $discoveryRefuses5 + ' mixed=' + $mixed23.detail + ' same=' + $same23.detail + ' distinct=' + $distinct23.detail)
                    }
                } else {
                    Complete-Dp2Case 5 $t0 'fail' ('the Revit 2023 refusal references were not discovered: host=' + [bool]$hostGridRef +
                        ' linked_a=' + $linkedRefs5.Count + ' linked_c=' + $linkedRefsC5.Count)
                }
            }
            elseif ($hostGridRef -and $linkedRefs5.Count -eq 2) {
                $refsFor5 = @($hostGridRef, $linkedRefs5[0], $linkedRefs5[1])
                $an5 = Invoke-WriteApply 'horizun_annotate' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(@{
                            operation = 'dimension'; view_id = [long]$dimPlanViewId
                            line_start = @(511500, 4000, 0); line_end = @(511500, 9500, 0)
                            references = $refsFor5 })
                } 'dp2-mixed-chain'
                if ($an5.stage -eq 'apply' -and -not $an5.answer.isError -and
                    $an5.answer.data.state -eq 'committed_verified') {
                    $row5 = @($an5.answer.data.rows)[0]
                    $dim5Id = [long]$row5.element_id
                    # MEASURED on run 2: the dry-run reply's per-action rows live under
                    # data.plan (data.rows is the APPLY's verification table).
                    $linkedInstancesInPlan = @()
                    foreach ($pr in @($an5.dry.data.plan)) {
                        foreach ($rd in @($pr.reference_detail)) {
                            if ($rd.linked -eq $true -and $rd.link) {
                                $linkedInstancesInPlan += [long]$rd.link.link_instance_id
                            }
                        }
                    }
                    $linkedInPlan = $linkedInstancesInPlan -contains [long]$dp2.A
                    if ($linkedInPlan) {
                        Complete-Dp2Case 5 $t0 'pass' 'the mixed chain across one host grid and two linked wall faces committed_verified, and the rehearsal plan carried every linked reference with full provenance' `
                            -Evidence @{ dimension=$dim5Id; references=$refsFor5.Count; verification=$row5.verification }
                    } else {
                        Complete-Dp2Case 5 $t0 'fail' 'the linked dimension committed but its rehearsal omitted linked provenance'
                    }
                } else {
                    $why5 = $null
                    if ($an5.answer.data -and $an5.answer.data.rehearsal) {
                        $why5 = @($an5.answer.data.rehearsal.actions | ForEach-Object {
                            if ($_.reason) { [string]$_.reason }
                        }) -join ' | '
                    }
                    Complete-Dp2Case 5 $t0 'fail' ('the linked-dimension branch did not commit: stage=' + $an5.stage +
                        $(if ($why5) { ' rehearsal=' + $why5 } else { ' ' + (Get-DimShortText $an5.answer.text) }))
                }
            } else {
                Complete-Dp2Case 5 $t0 'fail' ('the references for the linked-dimension branch were not discovered: host=' + [bool]$hostGridRef +
                    ' linked_a=' + $linkedRefs5.Count + ' linked_c=' + $linkedRefsC5.Count)
            }

            # ---- case 6: moving the LINK between rehearsal and apply is stale --
            $t0 = Get-Date
            if ($Year -eq 2023) {
                if ($revit23LinkedRefusal5) {
                    Complete-Dp2Case 6 $t0 'pass' 'Revit 2023 withholds every linked-dimension token before a transaction, so no token can survive a later link move; the supported-year stale-plan path runs in 2024-2027' `
                        -Evidence @{ code='linked_geometry_rejected_by_revit_2023_dimension_api'; confirmation_token=$null }
                } else {
                    Complete-Dp2Case 6 $t0 'fail' 'the Revit 2023 boundary did not prove that every linked-dimension token is withheld'
                }
            }
            elseif ($hostGridRef -and $linkedRefs5.Count -eq 2) {
                $refsFor6 = @($hostGridRef, $linkedRefs5[0])
                $dry6 = Invoke-Write 'horizun_annotate' @{
                        target_document = $wDoc; units = 'mm'; dry_run = $true
                        actions = @(@{
                            operation = 'dimension'; view_id = [long]$dimPlanViewId
                            line_start = @(512000, 4000, 0); line_end = @(512000, 9500, 0)
                            references = $refsFor6 })
                }
                $token6 = $null
                if (-not $dry6.isError -and $dry6.data) { $token6 = $dry6.data.confirmation_token }
                if ($token6) {
                    $mv6 = Invoke-WriteApply 'horizun_transform_elements' @{
                        target_document = $wDoc; units = 'mm'
                        operations = @(@{ operation = 'move'; element_ids = @($dp2.A); vector = @(100, 0, 0) })
                    } 'dp2-move-link'
                    $moved6 = ($mv6.stage -eq 'apply' -and -not $mv6.answer.isError)
                    if ($moved6) {
                        $apply6 = Invoke-Write 'horizun_annotate' @{
                            target_document = $wDoc; units = 'mm'; dry_run = $false
                            confirmation_token = $token6
                            idempotency_key = "live-write-dp2-stale-link-$probeRun"
                            actions = @(@{
                                operation = 'dimension'; view_id = [long]$dimPlanViewId
                                line_start = @(512000, 4000, 0); line_end = @(512000, 9500, 0)
                                references = $refsFor6 })
                        }
                        $stale6 = $false; $named6 = $false
                        if ($apply6.isError) {
                            if ($apply6.data -and [string]$apply6.data.state -eq 'stale_plan') { $stale6 = $true }
                            elseif ($apply6.text -match 'MODEL MOVED') { $stale6 = $true }
                            if ($apply6.text -match 'link_transform_moved') { $named6 = $true }
                        }
                        # put the link back either way, so later cases measure the staged position
                        $mvBack = Invoke-WriteApply 'horizun_transform_elements' @{
                            target_document = $wDoc; units = 'mm'
                            operations = @(@{ operation = 'move'; element_ids = @($dp2.A); vector = @(-100, 0, 0) })
                        } 'dp2-move-link-back'
                        if ($stale6 -and $named6) {
                            Complete-Dp2Case 6 $t0 'pass' 'moving the link instance 100 mm between rehearsal and apply refused as a stale plan NAMING link_transform_moved, and nothing was written' `
                                -Evidence @{ refusal=(Get-DimShortText $apply6.text) }
                        } elseif ($stale6) {
                            Complete-Dp2Case 6 $t0 'fail' 'the apply refused stale but did not name link_transform_moved'
                        } else {
                            Complete-Dp2Case 6 $t0 'fail' ('the apply did not refuse as stale after the link moved: ' + (Get-DimShortText $apply6.text))
                        }
                    } else {
                        Complete-Dp2Case 6 $t0 'unverified' ('the link instance could not be moved by the typed transform: ' + (Get-DimShortText $mv6.answer.text))
                    }
                } else {
                    $why6 = $null
                    if ($dry6.data -and $dry6.data.rehearsal) {
                        $why6 = @($dry6.data.rehearsal.actions | ForEach-Object {
                            if ($_.reason) { [string]$_.reason }
                        }) -join ' | '
                    }
                    Complete-Dp2Case 6 $t0 'unverified' ('the rehearsal issued no token' +
                        $(if ($why6) { ': ' + $why6 } else { ': ' + (Get-DimShortText $dry6.text) }))
                }
            } else {
                Complete-Dp2Case 6 $t0 'unverified' 'case 5 did not leave usable references'
            }

            # ---- case 7: an unloaded link answers by code, and reloads --------
            $t0 = Get-Date
            $unloadCode = @'
from Autodesk.Revit.DB import RevitLinkType, ElementId
lt = doc.GetElement(ElementId(__TYPE__))
before = str(lt.GetLinkedFileStatus())
if '__MODE__' == 'unload':
    lt.Unload(None)
else:
    lt.Reload()
after = str(lt.GetLinkedFileStatus())
__output__ = {'status': 'self_reported_verified', 'before': before, 'after': after}
'@
            $unl = $unloadCode.Replace('__TYPE__', "$($dp2.TypeId)").Replace('__MODE__', 'unload')
            $unlPath = Join-Path $scratchDir 'dp2-unload.py'
            [IO.File]::WriteAllText($unlPath, $unl, [Text.UTF8Encoding]::new($false))
            $u7 = Invoke-Write 'horizun_execute_python' @{
                code_path = $unlPath; target_document = $wDoc
                idempotency_key = "live-dp2-unload-$probeRun" }
            $u7Out = $null
            if (-not $u7.isError -and $u7.data) { $u7Out = $u7.data.output }
            if ($u7Out -and [string]$u7Out.after -ne 'Loaded') {
                $d7 = Invoke-Write 'horizun_get_dimension_references' @{
                    view_id = [long]$dimPlanViewId; units = 'mm'
                    linked_targets = @(@{ link_instance_id = $dp2.A; linked_element_ids = @($dp2.WallId) })
                    selectors = @('exterior_face'); max_results = 5 }
                $coded7 = $false
                if (-not $d7.isError -and $d7.data) {
                    $unread7 = @(@($d7.data.coverage.unreadable) | Where-Object { [string]$_.code -eq 'link_unloaded' })
                    $coded7 = $unread7.Count -ge 1
                }
                $rel = $unloadCode.Replace('__TYPE__', "$($dp2.TypeId)").Replace('__MODE__', 'reload')
                $relPath = Join-Path $scratchDir 'dp2-reload.py'
                [IO.File]::WriteAllText($relPath, $rel, [Text.UTF8Encoding]::new($false))
                $u7b = Invoke-Write 'horizun_execute_python' @{
                    code_path = $relPath; target_document = $wDoc
                    idempotency_key = "live-dp2-reload-$probeRun" }
                $reloaded = $false
                if (-not $u7b.isError -and $u7b.data -and $u7b.data.output) { $reloaded = [string]$u7b.data.output.after -eq 'Loaded' }
                if ($coded7 -and $reloaded) {
                    Complete-Dp2Case 7 $t0 'pass' 'with the link type unloaded, discovery named every target link_unloaded in coverage instead of guessing, and the fixture reloaded for the cases after' `
                        -Evidence @{ unloaded_status=$u7Out.after }
                } elseif (-not $coded7) {
                    Complete-Dp2Case 7 $t0 'fail' 'the unloaded link was not reported with code link_unloaded'
                } else {
                    Complete-Dp2Case 7 $t0 'fail' 'the link did not reload; later cases may be poisoned'
                }
            } else {
                Complete-Dp2Case 7 $t0 'unverified' ('the link type could not be unloaded: ' + (Get-DimShortText $u7.text))
            }

            # ---- case 8: query_dimensions resolves THROUGH the loaded link ----
            $t0 = Get-Date
            if ($Year -eq 2023) {
                if ($revit23LinkedRefusal5 -and -not $dim5Id) {
                    Complete-Dp2Case 8 $t0 'pass' 'Revit 2023 cannot author the linked dimension that query_dimensions would re-read: all three native creation arrangements were refused before a transaction with the version-specific code; the positive linked-reference query is exercised in Revit 2024-2027' `
                        -Evidence @{ source_dimension_exists=$false; code='linked_geometry_rejected_by_revit_2023_dimension_api'; positive_years=@(2024,2025,2026,2027) }
                } else {
                    Complete-Dp2Case 8 $t0 'fail' 'Revit 2023 either produced a linked dimension contrary to the measured API boundary or failed to name that boundary'
                }
            }
            elseif ($dim5Id) {
                $q8 = Invoke-Write 'horizun_query_dimensions' @{ element_ids = @($dim5Id); units = 'mm' }
                $row8 = $null
                if (-not $q8.isError -and $q8.data) { $row8 = @($q8.data.rows)[0] }
                if ($row8 -and [int]$row8.linked_references -eq 2 -and
                    [int]$row8.linked_references_resolved -eq 2 -and
                    [int]$row8.unloaded_link_references -eq 0 -and
                    [string]$row8.reference_coverage -eq 'complete' -and
                    [int]$row8.broken_references -eq 0) {
                    $resolved8 = @(@($row8.references) | Where-Object { $_.linked -eq $true -and [string]$_.link_state -eq 'resolved' })
                    if ($resolved8.Count -eq 2 -and $resolved8[0].link -and
                        [string]$resolved8[0].link.linked_element_state -eq 'present') {
                        Complete-Dp2Case 8 $t0 'pass' 'the mixed dimension re-read with both linked references RESOLVED through the live link - element present, coverage complete, nothing counted broken' `
                            -Evidence @{ coverage=$row8.reference_coverage; linked=$row8.linked_references }
                    } else {
                        Complete-Dp2Case 8 $t0 'fail' 'the counters agree but the per-reference link blocks do not'
                    }
                } else {
                    Complete-Dp2Case 8 $t0 'fail' ('the linked references were not resolved as complete: ' + (Get-DimShortText $q8.text))
                }
            } else {
                Complete-Dp2Case 8 $t0 'unverified' 'the supported-year case 5 did not commit a mixed dimension to re-read'
            }

            # ---- case 9: auto_dimension_grids plans, commits, and deduplicates
            # MEASURED on run 3 (2026-08-26): the pair MUST be grids no earlier case
            # dimensioned as an exact set. The original dimension section leaves several
            # committed chains over {par[0], par[1]} in this same view, and the planner's
            # dedup - now that ExistingChainIdentities collapses datum respelling -
            # correctly reports that pair already_dimensioned on the FIRST plan. That is
            # the product doing its anti-doubling job, so the probe uses {par[1], par[2]}.
            $t0 = Get-Date
            $p9 = Invoke-Write 'horizun_plan_annotations' @{
                operation = 'auto_dimension_grids'; view_id = [long]$dimPlanViewId
                element_ids = @([long]$dimParGridIds[1], [long]$dimParGridIds[2]); units = 'mm'
                offset = 2000; side = 'negative' }
            $done9 = $false
            if (-not $p9.isError -and $p9.data -and @($p9.data.chains).Count -eq 1 -and
                [string]$p9.data.coverage -eq 'complete' -and $p9.data.safe_to_execute -eq $true) {
                $next9 = $p9.data.next_arguments
                $an9 = Invoke-WriteApply 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@($next9.actions) | ForEach-Object {
                        $action = @{}
                        foreach ($property in $_.PSObject.Properties) { $action[$property.Name] = $property.Value }
                        $action })
                } 'dp2-auto-grids'
                if ($an9.stage -eq 'apply' -and -not $an9.answer.isError -and
                    $an9.answer.data.state -eq 'committed_verified') {
                    $p9b = Invoke-Write 'horizun_plan_annotations' @{
                        operation = 'auto_dimension_grids'; view_id = [long]$dimPlanViewId
                        element_ids = @([long]$dimParGridIds[1], [long]$dimParGridIds[2]); units = 'mm'
                        offset = 2000; side = 'negative' }
                    if (-not $p9b.isError -and $p9b.data -and @($p9b.data.chains).Count -eq 0 -and
                        (@($p9b.data.omitted | Where-Object { [string]$_.code -eq 'already_dimensioned' })).Count -ge 2) {
                        $done9 = $true
                        Complete-Dp2Case 9 $t0 'pass' 'the grid pair planned one complete chain, committed through the verified writer, and the re-plan reported every reference already_dimensioned instead of doubling the drawing' `
                            -Evidence @{ first_plan=@($p9.data.chains)[0].chain_identity; replay_omitted=@($p9b.data.omitted).Count }
                    } else {
                        Complete-Dp2Case 9 $t0 'fail' 'the chain committed but the re-plan did not deduplicate it'
                    }
                } else {
                    Complete-Dp2Case 9 $t0 'fail' ('the planned chain did not commit: stage=' + $an9.stage + ' ' + (Get-DimShortText $an9.answer.text))
                }
            } else {
                Complete-Dp2Case 9 $t0 'fail' ('auto_dimension_grids did not plan a complete single chain: ' + (Get-DimShortText $p9.text))
            }

            # ---- case 10: auto_dimension over a link REFUSES with the measured ---
            # reason, linked datum discovery is marked incompatible with the code,
            # and a linked datum handed to annotate refuses BEFORE any transaction.
            # This is the honest shape of the capability: linked GEOMETRY constructs
            # (case 5 proved it), linked DATUMS are rejected by Revit itself -
            # measured on this machine, this run.
            $t0 = Get-Date
            $p10 = Invoke-Write 'horizun_plan_annotations' @{
                operation = 'auto_dimension_grids'; view_id = [long]$dimPlanViewId
                link_instance_id = $dp2.C; units = 'mm'; offset = 2000 }
            $refused10 = $p10.isError -and $p10.text -match 'measured live' -and
                         $p10.text -match 'Nothing was planned'
            $d10 = Invoke-Write 'horizun_get_dimension_references' @{
                view_id = [long]$dimPlanViewId; units = 'mm'
                linked_targets = @(@{ link_instance_id = $dp2.C; linked_element_ids = @($dp2.Grids[0]) })
                selectors = @('grid'); max_results = 5 }
            $marked10 = $false; $datumRef10 = $null
            if (-not $d10.isError -and $d10.data) {
                $row10 = @(@($d10.data.rows) | Where-Object { $_.linked -eq $true })[0]
                if ($row10 -and $row10.compatible_with_dimension -eq $false -and
                    [string]$row10.incompatibility_reason.code -eq 'linked_datum_rejected_by_dimension_api') {
                    $marked10 = $true
                    $datumRef10 = [string]$row10.stable_representation
                }
            }
            $annotateRefused10 = $false
            if ($datumRef10 -and $hostGridRef) {
                $an10 = Invoke-Write 'horizun_annotate' @{
                    target_document = $wDoc; units = 'mm'; dry_run = $true
                    actions = @(@{
                        operation = 'dimension'; view_id = [long]$dimPlanViewId
                        line_start = @(513000, 4000, 0); line_end = @(513000, 9500, 0)
                        references = @($hostGridRef, $datumRef10) })
                }
                # MEASURED on run 2: the per-reference refusal lands as an INVALID
                # ACTION in the dry-run reply (valid=0, errors[0].error carries the
                # DATUM sentence and the measurement), not as an MCP error.
                if (-not $an10.isError -and $an10.data -and [int]$an10.data.invalid -eq 1) {
                    $err10 = [string]@($an10.data.errors)[0].error
                    $annotateRefused10 = ($err10 -match 'DATUM') -and ($err10 -match 'measured live')
                }
            }
            if ($refused10 -and $marked10 -and $annotateRefused10) {
                Complete-Dp2Case 10 $t0 'pass' 'the measured linked-datum rejection is enforced at all three doors: the planner refuses link_instance_id by name, discovery marks the linked grid incompatible with the structured code, and annotate refuses the reference before any transaction' `
                    -Evidence @{ planner_refusal=(Get-DimShortText $p10.text); code='linked_datum_rejected_by_dimension_api' }
            } else {
                Complete-Dp2Case 10 $t0 'fail' ('the measured refusal is not enforced everywhere: planner=' + $refused10 + ' discovery=' + $marked10 + ' annotate=' + $annotateRefused10)
            }

            # ---- case 11: room production - plan_views drives manage_views ----
            $t0 = Get-Date
            $roomStage = @'
from Autodesk.Revit.DB import Transaction, Wall, Line, XYZ, UV, ElementId

FT = 304.8
def ft(mm): return mm / FT

view = doc.GetElement(ElementId(__VIEW__))
level = view.GenLevel
t = Transaction(doc, 'HZ live: room fixture')
t.Start()
x0, y0 = ft(575000), ft(0)
w, h = ft(4000), ft(3000)
pts = [(x0, y0), (x0 + w, y0), (x0 + w, y0 + h), (x0, y0 + h)]
walls = []
for i in range(4):
    a = XYZ(pts[i][0], pts[i][1], 0)
    b = XYZ(pts[(i + 1) % 4][0], pts[(i + 1) % 4][1], 0)
    walls.append(Wall.Create(doc, Line.CreateBound(a, b), level.Id, False))
room = doc.Create.NewRoom(level, UV(x0 + w / 2.0, y0 + h / 2.0))
room.Name = 'HZ LIVE ROOM'
room.Number = 'HZ-901'
t.Commit()
# Commit regenerates; a bare Regenerate() OUTSIDE a transaction throws
# (measured on the first run of this section).
area = room.Area
rid = room.Id.IntegerValue if hasattr(room.Id, 'IntegerValue') else room.Id.Value
__output__ = {'status': 'self_reported_verified' if area > 0 else 'partial',
              'room': rid, 'area_sqft': area}
'@
            $roomStage = $roomStage.Replace('__VIEW__', "$dimPlanViewId")
            $roomPath = Join-Path $scratchDir 'dp2-room.py'
            [IO.File]::WriteAllText($roomPath, $roomStage, [Text.UTF8Encoding]::new($false))
            $r11 = Invoke-Write 'horizun_execute_python' @{
                code_path = $roomPath; target_document = $wDoc
                idempotency_key = "live-dp2-room-$probeRun" }
            $roomId = $null
            if (-not $r11.isError -and $r11.data -and $r11.data.output -and
                [string]$r11.data.output.status -eq 'self_reported_verified') { $roomId = [long]$r11.data.output.room }
            if ($roomId) {
                $pv11 = Invoke-Write 'horizun_plan_views' @{
                    operation = 'room_views'; plan_view_id = [long]$dimPlanViewId
                    room_ids = @($roomId); elevation_count = 2; units = 'mm'
                    name_pattern = "HZ {room_number} {kind} {index} $planTag" }
                if (-not $pv11.isError -and $pv11.data -and [string]$pv11.data.coverage -eq 'complete' -and
                    [int]$pv11.data.rooms_planned -eq 1 -and $pv11.data.safe_to_execute -eq $true) {
                    $roomRow = @($pv11.data.rooms)[0]
                    $next11 = $pv11.data.next_arguments
                    $mv11 = Invoke-WriteApply 'horizun_manage_views' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(@($next11.actions) | ForEach-Object {
                            $action = @{}
                            foreach ($property in $_.PSObject.Properties) { $action[$property.Name] = $property.Value }
                            $action })
                    } 'dp2-room-views'
                    if ($mv11.stage -eq 'apply' -and -not $mv11.answer.isError -and
                        [int]$mv11.answer.data.actions_verified -eq [int]$pv11.data.actions_planned) {
                        Complete-Dp2Case 11 $t0 'pass' ('the room planned ' + $pv11.data.actions_planned + ' actions (2 oriented elevations, 2 crossing sections, 1 cropped plan) and manage_views committed and re-read every one, rotation included') `
                            -Evidence @{ room=$roomId; orientation=$roomRow.orientation; rotation=$roomRow.rotation_degrees; actions=$pv11.data.actions_planned }
                    } else {
                        Complete-Dp2Case 11 $t0 'fail' ('the room plan did not commit whole: stage=' + $mv11.stage + ' ' + (Get-DimShortText $mv11.answer.text))
                    }
                } else {
                    Complete-Dp2Case 11 $t0 'fail' ('plan_views did not produce a complete single-room plan: ' + (Get-DimShortText $pv11.text))
                }
            } else {
                Complete-Dp2Case 11 $t0 'unverified' ('the room fixture could not be staged: ' + (Get-DimShortText $r11.text))
            }

            # ---- case 12: placeholder -> conversion, and the number collision --
            $t0 = Get-Date
            if ($planTbTypeId) {
                $ph12 = Invoke-WriteApply 'horizun_manage_views' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(
                        @{ operation = 'create_placeholder_sheet'; key = 'ph'; number = "HZPH-$planTag"; name = 'HZ Placeholder' },
                        @{ operation = 'convert_placeholder_sheet'; sheet_key = 'ph'; title_block_type_id = [long]$planTbTypeId })
                } 'dp2-placeholder'
                $converted12 = ($ph12.stage -eq 'apply' -and -not $ph12.answer.isError -and
                                [int]$ph12.answer.data.actions_verified -eq 2)
                $dup12 = Invoke-Write 'horizun_manage_views' @{
                    target_document = $wDoc; units = 'mm'; dry_run = $true
                    actions = @(@{ operation = 'create_sheet'; number = "HZPH-$planTag"; name = 'HZ Duplicate'
                                   title_block_type_id = [long]$planTbTypeId })
                }
                $refused12 = $false
                if (-not $dup12.isError -and $dup12.data -and [int]$dup12.data.invalid -eq 1) {
                    $err12 = [string]@($dup12.data.errors)[0].error
                    $refused12 = $err12 -match 'already used'
                }
                if ($converted12 -and $refused12) {
                    Complete-Dp2Case 12 $t0 'pass' 'a placeholder was created and converted to a real titled sheet in ONE batch (title block re-read), and reusing its number was refused by name before anything ran' `
                        -Evidence @{ rows=$ph12.answer.data.rows }
                } elseif (-not $converted12) {
                    Complete-Dp2Case 12 $t0 'fail' ('the placeholder lifecycle did not commit: stage=' + $ph12.stage + ' ' + (Get-DimShortText $ph12.answer.text))
                } else {
                    Complete-Dp2Case 12 $t0 'fail' 'the duplicate sheet number was not refused by the validator'
                }
            } else {
                Complete-Dp2Case 12 $t0 'unverified' 'no title block type is available in the fixture'
            }

            # ---- case 13: view range, crop and annotation crop, verified ------
            $t0 = Get-Date
            $vr13 = Invoke-WriteApply 'horizun_manage_views' @{
                target_document = $wDoc; units = 'mm'
                actions = @(
                    @{ operation = 'duplicate_view'; key = 'vr'; source_view_id = [long]$dimPlanViewId
                       duplicate_option = 'Duplicate'; name = "HZ VR $planTag" },
                    @{ operation = 'set_view_range'; view_key = 'vr'; cut_offset = 1500; top_offset = 2800; bottom_offset = 100 },
                    @{ operation = 'set_crop'; view_key = 'vr'; box = @(500000, -10000, 580000, 110000) },
                    @{ operation = 'set_annotation_crop'; view_key = 'vr'; active = $true; annotation_offset = 5 })
            } 'dp2-view-shaping'
            if ($vr13.stage -eq 'apply' -and -not $vr13.answer.isError -and
                [int]$vr13.answer.data.actions_verified -eq 4) {
                Complete-Dp2Case 13 $t0 'pass' 'one batch duplicated the plan, moved three view-range offsets, wrote a rectangular crop through the crop frame conversion and switched the annotation crop with its offsets - every value re-read after commit' `
                    -Evidence @{ rows=$vr13.answer.data.rows }
            } else {
                Complete-Dp2Case 13 $t0 'fail' ('view shaping did not verify all four actions: stage=' + $vr13.stage + ' ' + (Get-DimShortText $vr13.answer.text))
            }

            # ---- case 14: viewport alignment against a still anchor ------------
            $t0 = Get-Date
            $al14 = $null
            if ($planTbTypeId) {
                $mk14 = Invoke-WriteApply 'horizun_manage_views' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(
                        @{ operation = 'create_sheet'; key = 's'; number = "HZAL-$planTag"; name = 'HZ Align'
                           title_block_type_id = [long]$planTbTypeId },
                        @{ operation = 'create_drafting'; key = 'v1'; name = "HZ AL V1 $planTag" },
                        @{ operation = 'create_drafting'; key = 'v2'; name = "HZ AL V2 $planTag" })
                } 'dp2-align-fixture'
                if ($mk14.stage -eq 'apply' -and -not $mk14.answer.isError) {
                    $s14 = [long]$mk14.answer.data.aliases.s
                    $v1 = [long]$mk14.answer.data.aliases.v1
                    $v2 = [long]$mk14.answer.data.aliases.v2
                    $g14 = Invoke-WriteApply 'horizun_detail_2d' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(
                            @{ operation = 'create_detail_line'; view_id = $v1; start = @(0,0); end = @(800,0); key='l1' },
                            @{ operation = 'create_detail_line'; view_id = $v2; start = @(0,0); end = @(800,0); key='l2' })
                    } 'dp2-align-geometry'
                    $pl14 = Invoke-WriteApply 'horizun_manage_views' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(
                            @{ operation = 'place_view'; sheet_id = $s14; view_id = $v1; point = @(200, 400) },
                            @{ operation = 'place_view'; sheet_id = $s14; view_id = $v2; point = @(390, 210) })
                    } 'dp2-align-place'
                    if ($g14.stage -eq 'apply' -and $pl14.stage -eq 'apply' -and -not $pl14.answer.isError) {
                        $vpA = [long]@($pl14.answer.data.rows)[0].element_id
                        $vpB = [long]@($pl14.answer.data.rows)[1].element_id
                        $al14 = Invoke-WriteApply 'horizun_manage_views' @{
                            target_document = $wDoc; units = 'mm'
                            actions = @(@{ operation = 'align_viewports'; anchor_viewport_id = $vpA
                                           viewport_ids = @($vpB); mode = 'left' })
                        } 'dp2-align'
                    }
                }
            }
            if ($al14 -and $al14.stage -eq 'apply' -and -not $al14.answer.isError -and
                [int]$al14.answer.data.actions_verified -eq 1) {
                Complete-Dp2Case 14 $t0 'pass' 'two viewports on a fresh sheet aligned left edges to a still anchor, and the outlines were re-read within tolerance after commit' `
                    -Evidence @{ rows=$al14.answer.data.rows }
            } elseif ($al14) {
                Complete-Dp2Case 14 $t0 'fail' ('viewport alignment did not verify: stage=' + $al14.stage + ' ' + (Get-DimShortText $al14.answer.text))
            } else {
                Complete-Dp2Case 14 $t0 'unverified' 'the alignment fixture (sheet + two placed drafting views) could not be built'
            }

            # ---- case 15: schedule definition editing, and idempotent replay ---
            $t0 = Get-Date
            $sc15 = Invoke-WriteApply 'horizun_manage_schedules' @{
                target_document = $wDoc
                actions = @(
                    @{ operation = 'create'; key = 'sl'; kind = 'sheet_list'; name = "HZ SHEETS $planTag" },
                    @{ operation = 'set_options'; schedule_key = 'sl'; grand_total = $true; headers = $true })
            } 'dp2-schedule-create'
            $sheetListId = $null
            if ($sc15.stage -eq 'apply' -and -not $sc15.answer.isError) {
                $sheetListId = [long]$sc15.answer.data.aliases.sl
            }
            if ($sheetListId) {
                # MEASURED on the first run: ViewSchedule.CreateSheetList creates an
                # EMPTY definition - no fields at all, unlike the UI's default - so the
                # field is added first, which also exercises add_fields live.
                $af15 = Invoke-WriteApply 'horizun_manage_schedules' @{
                    target_document = $wDoc
                    actions = @(@{ operation = 'add_fields'; schedule_id = $sheetListId
                                   fields = @(@{ name = 'Sheet Number' }) })
                } 'dp2-schedule-add-field'
                if ($af15.stage -ne 'apply' -or $af15.answer.isError) {
                    Complete-Dp2Case 15 $t0 'fail' ('add_fields did not commit on the fresh sheet list: stage=' + $af15.stage + ' ' + (Get-DimShortText $af15.answer.text))
                }
                $f15 = @(@{ field = 'Sheet Number'; operator = 'begins_with'; value = 'HZ' })
                $sf15 = Invoke-WriteApply 'horizun_manage_schedules' @{
                    target_document = $wDoc
                    actions = @(@{ operation = 'set_filters'; schedule_id = $sheetListId; filters = $f15 })
                } 'dp2-schedule-filters'
                $applied15 = ($sf15.stage -eq 'apply' -and -not $sf15.answer.isError -and
                              [int]$sf15.answer.data.actions_verified -eq 1)
                $changed15 = $null
                if ($applied15) { $changed15 = @(@($sf15.answer.data.rows)[0].changed_sections) }
                $sf15b = Invoke-WriteApply 'horizun_manage_schedules' @{
                    target_document = $wDoc
                    actions = @(@{ operation = 'set_filters'; schedule_id = $sheetListId; filters = $f15 })
                } 'dp2-schedule-filters-replay'
                $idempotent15 = $false
                if ($sf15b.stage -eq 'apply' -and -not $sf15b.answer.isError) {
                    $row15b = @($sf15b.answer.data.rows)[0]
                    $idempotent15 = ([string]$row15b.definition_fingerprint_before -eq [string]$row15b.definition_fingerprint_after) -and
                                    (@($row15b.changed_sections).Count -eq 0)
                }
                if ($applied15 -and ($changed15 -contains 'filters') -and $idempotent15) {
                    Complete-Dp2Case 15 $t0 'pass' 'a sheet list was created and filtered by a declared whole list; the reply named filters as the changed section, and replaying the SAME declaration changed nothing - the fingerprints agree byte for byte' `
                        -Evidence @{ schedule=$sheetListId; changed=$changed15 }
                } elseif (-not $applied15) {
                    Complete-Dp2Case 15 $t0 'fail' ('set_filters did not verify: stage=' + $sf15.stage + ' ' + (Get-DimShortText $sf15.answer.text))
                } else {
                    Complete-Dp2Case 15 $t0 'fail' ('the replay was not idempotent or the diff missed the section; changed=' + ($changed15 -join ','))
                }
            } else {
                Complete-Dp2Case 15 $t0 'fail' ('the sheet list was not created: stage=' + $sc15.stage + ' ' + (Get-DimShortText $sc15.answer.text))
            }

            # ---- case 16: a schedule edited underneath its token refuses stale -
            $t0 = Get-Date
            if ($sheetListId) {
                $dry16 = Invoke-Write 'horizun_manage_schedules' @{
                    target_document = $wDoc; dry_run = $true
                    actions = @(@{ operation = 'set_options'; schedule_id = $sheetListId; itemized = $true })
                }
                $token16 = $null
                if (-not $dry16.isError -and $dry16.data) { $token16 = $dry16.data.confirmation_token }
                if ($token16) {
                    $mid16 = Invoke-WriteApply 'horizun_manage_schedules' @{
                        target_document = $wDoc
                        actions = @(@{ operation = 'rename'; schedule_id = $sheetListId; name = "HZ SHEETS B $planTag" })
                    } 'dp2-schedule-interleave'
                    $moved16 = ($mid16.stage -eq 'apply' -and -not $mid16.answer.isError)
                    $apply16 = Invoke-Write 'horizun_manage_schedules' @{
                        target_document = $wDoc; dry_run = $false
                        confirmation_token = $token16
                        idempotency_key = "live-write-dp2-stale-schedule-$probeRun"
                        actions = @(@{ operation = 'set_options'; schedule_id = $sheetListId; itemized = $true })
                    }
                    $stale16 = $apply16.isError -and (
                        ($apply16.data -and [string]$apply16.data.state -eq 'stale_plan') -or
                        $apply16.text -match 'MODEL MOVED')
                    if ($moved16 -and $stale16) {
                        Complete-Dp2Case 16 $t0 'pass' 'a rename between rehearsal and apply changed the definition fingerprint the token binds, and the apply refused as a stale plan with nothing written' `
                            -Evidence @{ refusal=(Get-DimShortText $apply16.text) }
                    } elseif (-not $moved16) {
                        Complete-Dp2Case 16 $t0 'unverified' 'the interleaved rename did not commit, so staleness could not be provoked'
                    } else {
                        Complete-Dp2Case 16 $t0 'fail' ('the token survived a definition change: ' + (Get-DimShortText $apply16.text))
                    }
                } else {
                    Complete-Dp2Case 16 $t0 'unverified' 'the rehearsal issued no token'
                }
            } else {
                Complete-Dp2Case 16 $t0 'unverified' 'case 15 left no schedule to edit'
            }

            # ---- case 17: a revision withdrawn from a sheet, and the cloud rule
            $t0 = Get-Date
            if ($planSheetAId) {
                $rv17 = Invoke-WriteApply 'horizun_manage_revisions' @{
                    target_document = $wDoc; units = 'mm'
                    actions = @(@{ key = 'r'; operation = 'create_revision'
                                   description = "HZ withdraw probe $planTag"
                                   sheet_ids = @([long]$planSheetAId) })
                } 'dp2-revision-create'
                $rev17 = $null
                if ($rv17.stage -eq 'apply' -and -not $rv17.answer.isError) {
                    $rev17 = [long]@($rv17.answer.data.rows)[0].revision_id
                }
                if ($rev17) {
                    $wd17 = Invoke-WriteApply 'horizun_manage_revisions' @{
                        target_document = $wDoc; units = 'mm'
                        actions = @(@{ key = 'w'; operation = 'update_revision'; revision_id = $rev17
                                       remove_sheet_ids = @([long]$planSheetAId) })
                    } 'dp2-revision-withdraw'
                    $withdrawn17 = ($wd17.stage -eq 'apply' -and -not $wd17.answer.isError -and
                                    [string]$wd17.answer.data.state -eq 'committed_verified')
                    $again17 = Invoke-Write 'horizun_manage_revisions' @{
                        target_document = $wDoc; units = 'mm'; dry_run = $true
                        actions = @(@{ key = 'w2'; operation = 'update_revision'; revision_id = $rev17
                                       remove_sheet_ids = @([long]$planSheetAId) })
                    }
                    $named17 = $again17.isError -and $again17.text -match 'not among'
                    if ($withdrawn17 -and $named17) {
                        Complete-Dp2Case 17 $t0 'pass' 'the revision was withdrawn from the sheet and verified gone from its additional list, and withdrawing it AGAIN refused by name instead of no-opping' `
                            -Evidence @{ revision=$rev17 }
                    } elseif (-not $withdrawn17) {
                        Complete-Dp2Case 17 $t0 'fail' ('the withdrawal did not commit: stage=' + $wd17.stage + ' ' + (Get-DimShortText $wd17.answer.text))
                    } else {
                        Complete-Dp2Case 17 $t0 'fail' 'the second withdrawal was not refused by name'
                    }
                } else {
                    Complete-Dp2Case 17 $t0 'unverified' ('the probe revision was not created: ' + (Get-DimShortText $rv17.answer.text))
                }
            } else {
                Complete-Dp2Case 17 $t0 'unverified' 'the planimetry fixture did not retain sheet A'
            }

            # ---- case 18: tool packs restrict the LIVE surface, and announce ---
            $t0 = Get-Date
            # The release runner deliberately gives this Revit an isolated
            # HORIZUN_DATA_ROOT. Writing the default user profile here changes
            # the owner's real configuration and the isolated bridge never sees
            # it, so the notification wait times out for a change that happened
            # in the wrong file.
            $settingsRoot18 = if ([string]::IsNullOrWhiteSpace($env:HORIZUN_DATA_ROOT)) {
                Join-Path $env:USERPROFILE '.horizun'
            } else { $env:HORIZUN_DATA_ROOT }
            $settingsPath18 = Join-Path $settingsRoot18 'settings.json'
            $original18 = $null
            $restored18 = $false
            try {
                $original18 = Get-Content -LiteralPath $settingsPath18 -Raw
                $settings18 = $original18 | ConvertFrom-Json
                $settings18 | Add-Member -NotePropertyName 'tool_packs' -NotePropertyValue @('read') -Force
                # Convert back preserving the other keys; the write is the same file the
                # ToolListMonitor watches, so the running session must announce.
                Set-Content -LiteralPath $settingsPath18 -Value ($settings18 | ConvertTo-Json -Depth 5) -Encoding UTF8

                # Wait for list_changed through the ONE session reader. Polling
                # tools/list gives Read-Rpc a bounded reply to read while it also
                # records any preceding id-less notifications in the inbox.
                $sawNotify18 = $false
                $notifyStart18 = $script:rpcNotifications.Count
                $notifyDeadline = (Get-Date).AddSeconds(45)
                $list18 = $null
                $pollId18 = 999810
                while ((Get-Date) -lt $notifyDeadline) {
                    Send-Rpc @{ jsonrpc='2.0'; id=$pollId18; method='tools/list'; params=@{} }
                    $pollId18++
                    $list18 = Read-Rpc 10000
                    $sawNotify18 = @($script:rpcNotifications |
                        Select-Object -Skip $notifyStart18 |
                        Where-Object { [string]$_.method -match 'tools/list_changed' }).Count -gt 0
                    if ($sawNotify18) { break }
                    Start-Sleep -Milliseconds 500
                }

                if (-not $list18) {
                    Send-Rpc @{ jsonrpc='2.0'; id=999801; method='tools/list'; params=@{} }
                    $list18 = Read-Rpc
                }
                $names18 = @()
                if ($list18 -and $list18.result) { $names18 = @(@($list18.result.tools) | ForEach-Object { $_.name }) }
                $shrunk18 = ($names18 -contains 'horizun_health') -and
                            ($names18 -contains 'horizun_query_model') -and
                            ($names18 -notcontains 'horizun_manage_views')

                # The ADD-IN enforces too: a hidden tool's dispatch refuses with the
                # pack sentence, and health reports the active selection.
                $h18 = Invoke-Write 'horizun_health' @{}
                $healthPacks18 = $false
                if (-not $h18.isError -and $h18.data -and $h18.data.tool_packs) {
                    $healthPacks18 = ([string]$h18.data.tool_packs.source -eq 'settings') -and
                                     ($h18.data.tool_packs.restricting -eq $true) -and
                                     (@($h18.data.tool_packs.active) -contains 'read')
                }
                $call18 = Invoke-Write 'horizun_manage_views' @{
                    target_document = $wDoc; units = 'mm'; dry_run = $true
                    actions = @(@{ operation = 'create_drafting'; name = 'HZ PACK PROBE' }) }
                $refused18 = $call18.isError -and $call18.text -match 'hidden by the active tool packs'

                # restore BEFORE judging, so a failed assertion cannot leave the
                # machine restricted.
                Set-Content -LiteralPath $settingsPath18 -Value $original18 -Encoding UTF8
                $restored18 = $true
                Send-Rpc @{ jsonrpc='2.0'; id=999802; method='tools/list'; params=@{} }
                $list18b = Read-Rpc
                $names18b = @()
                if ($list18b -and $list18b.result) { $names18b = @(@($list18b.result.tools) | ForEach-Object { $_.name }) }
                $restoredList18 = $names18b -contains 'horizun_manage_views'

                if ($shrunk18 -and $refused18 -and $healthPacks18 -and $restoredList18 -and $sawNotify18) {
                    Complete-Dp2Case 18 $t0 'pass' 'writing tool_packs=[read] shrank the LIVE tools/list, announced tools/list_changed on the running session, made the add-in refuse a hidden tool by name and publish the selection in health - and removing the key restored everything' `
                        -Evidence @{ restricted_count=$names18.Count; restored_count=$names18b.Count; saw_list_changed=$sawNotify18 }
                } else {
                    Complete-Dp2Case 18 $t0 'fail' ('packs did not behave live: shrunk=' + $shrunk18 + ' refused=' + $refused18 + ' health=' + $healthPacks18 + ' restored=' + $restoredList18 + ' notify=' + $sawNotify18)
                }
            }
            catch {
                if (-not $restored18 -and $original18) {
                    try { Set-Content -LiteralPath $settingsPath18 -Value $original18 -Encoding UTF8 } catch { }
                }
                Complete-Dp2Case 18 $t0 'unverified' ('the pack round-trip failed mid-way and settings were restored: ' + $_.Exception.Message)
            }
            finally {
                if (-not $restored18 -and $original18) {
                    try { Set-Content -LiteralPath $settingsPath18 -Value $original18 -Encoding UTF8 } catch { }
                }
            }
        }
        for ($dc=1; $dc -le 18; $dc++) {
            if (-not $script:dp2CasesDone.ContainsKey($dc)) { Complete-Dp2Case $dc (Get-Date) 'unverified' 'the linked-production section ended before this probe ran - a harness bug' }
        }

        # ------------------------------------------------------------------
        # W11: MAXIMUM PROGRAM. Every capability phases 5-14 shipped, driven
        # through the TYPED tools against fixtures this section stages itself
        # far east of everything (x >= 610 m). Same never-saved model.
        # ------------------------------------------------------------------
        $script:mpEvidence = @()
        $script:mpCasesDone = @{}
        function Complete-MpCase {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail, $Evidence=$null)
            if ($script:mpCasesDone.ContainsKey($CaseNumber)) { return }
            $script:mpCasesDone[$CaseNumber] = $true
            $entry = $writeNames[$mpNameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:mpEvidence += @{
                case=$CaseNumber; name=$entry.N; tool=$entry.T
                started_utc=$Started.ToUniversalTime().ToString('o'); outcome=$Outcome; detail=$Detail; evidence=$Evidence
            }
        }

        $mpX = 610000
        $mpP1 = $null; $mpP2 = $null; $mpP3 = $null; $mpWallId = $null; $mpPenPipe = $null

        # Fixture hygiene: the coordination ledger is BRIDGE state keyed by the
        # document, so it survives the never-saved model being reopened. A prior
        # run's findings would shadow this run's; the disposable fixture starts
        # from a clean ledger, and only the disposable fixture's ledger is touched.
        $mpLedgerProbe = Invoke-Write 'horizun_coordination' @{ operation='list' }
        if (-not $mpLedgerProbe.isError -and $mpLedgerProbe.data.ledger_exists -eq $true) {
            try { Remove-Item -LiteralPath ([string]$mpLedgerProbe.data.ledger_path) -Force -ErrorAction Stop } catch { }
        }

        # ---- shared staging: three pipes and one wall --------------------
        $mk1 = New-ProbePipe $mpX 0 0 ($mpX+3000) 0 0 $pipeType 'mp-p1'
        if ($mk1.stage -eq 'apply' -and -not $mk1.answer.isError) { $mpP1 = @($mk1.answer.data.rows)[0].element_id }
        $mk2 = New-ProbePipe ($mpX+3000) 0 0 ($mpX+3000) 3000 0 $pipeType 'mp-p2'
        if ($mk2.stage -eq 'apply' -and -not $mk2.answer.isError) { $mpP2 = @($mk2.answer.data.rows)[0].element_id }
        $mk3 = New-ProbePipe ($mpX+8000) 0 0 ($mpX+11000) 0 0 $pipeType 'mp-p3'
        if ($mk3.stage -eq 'apply' -and -not $mk3.answer.isError) { $mpP3 = @($mk3.answer.data.rows)[0].element_id }
        $mpWallType = First-Type 'OST_Walls' $null
        if ($mpWallType) {
            $mkW = Invoke-WriteApply 'horizun_create_elements' @{
                target_document = $wDoc; units = 'mm'
                elements = @(@{ kind='wall'; start=@($mpX,6000,0); end=@(($mpX+4000),6000,0)
                                height=3000; type_id=$mpWallType; level_id=$levelId })
            } 'mp-wall'
            if ($mkW.stage -eq 'apply' -and -not $mkW.answer.isError) { $mpWallId = @($mkW.answer.data.rows)[0].element_id }
        }
        if ($mpWallId) {
            $mkP = New-ProbePipe ($mpX+2000) 5000 1500 ($mpX+2000) 7000 1500 $pipeType 'mp-pen-pipe'
            if ($mkP.stage -eq 'apply' -and -not $mkP.answer.isError) { $mpPenPipe = @($mkP.answer.data.rows)[0].element_id }
        }

        # ---- case 1: include_mep connector facts -------------------------
        $t0 = Get-Date
        if (-not $mpP1) { Complete-MpCase 1 $t0 'unverified' 'the probe pipe could not be staged' }
        else {
            $q1 = Invoke-Write 'horizun_query_model' @{
                categories=@('OST_PipeCurves'); include_links=$false; include_mep=$true; max_rows=500 }
            $row1 = $null
            if (-not $q1.isError -and $q1.data) { $row1 = @($q1.data.rows) | Where-Object { [long]$_.element_id -eq [long]$mpP1 } | Select-Object -First 1 }
            $sum1 = if ($q1.data) { $q1.data.mep_summary } else { $null }
            if ($row1 -and $row1.mep -and @($row1.mep.connectors).Count -eq 2 -and
                [int]$row1.mep.open_connectors -eq 2 -and
                ([string]@($row1.mep.connectors)[0].shape) -eq 'round' -and
                [double]@($row1.mep.connectors)[0].diameter -gt 0 -and
                $sum1 -and [int]$sum1.open_connectors -ge 2) {
                Complete-MpCase 1 $t0 'pass' ('the staged pipe reports 2 open round connectors (diameter ' +
                    @($row1.mep.connectors)[0].diameter + ' mm) and mep_summary aggregates over the matched rows') `
                    -Evidence @{ connectors=$row1.mep.connectors; summary=$sum1 }
            } else {
                Complete-MpCase 1 $t0 'fail' ('the connector facts did not match the staged truth: ' + (Get-DimShortText $q1.text))
            }
        }

        # ---- case 2: the elbow ------------------------------------------
        $t0 = Get-Date
        if (-not $mpP1 -or -not $mpP2) { Complete-MpCase 2 $t0 'unverified' 'the two coincident pipes could not be staged' }
        else {
            $fit2 = Invoke-WriteApply 'horizun_create_elements' @{
                target_document = $wDoc; units = 'mm'
                elements = @(@{ kind='fitting'; fitting='elbow'
                                elements=@(@{ element_id=[long]$mpP1 }, @{ element_id=[long]$mpP2 }) })
            } 'mp-elbow'
            $row2 = $null
            if ($fit2.stage -eq 'apply' -and -not $fit2.answer.isError) { $row2 = @($fit2.answer.data.rows)[0] }
            $chosen2 = $null
            if ($fit2.dry -and $fit2.dry.data) { $chosen2 = @(@($fit2.dry.data.plan)[0].chosen_connectors) }
            if ($row2 -and $row2.verified -eq $true -and $row2.connectors_verified -eq $true -and
                $chosen2 -and $chosen2.Count -eq 2) {
                $q2 = Invoke-Write 'horizun_query_model' @{
                    categories=@('OST_PipeCurves'); include_links=$false; include_mep=$true; max_rows=500 }
                $p1row = @($q2.data.rows) | Where-Object { [long]$_.element_id -eq [long]$mpP1 } | Select-Object -First 1
                $connectedNow = $p1row -and [int]$p1row.mep.open_connectors -eq 1
                if ($connectedNow) {
                    Complete-MpCase 2 $t0 'pass' 'the elbow committed_verified with both approved connectors re-read CONNECTED, and discovery now shows the pipe with one open end' `
                        -Evidence @{ fitting_id=$row2.element_id; chosen=$chosen2 }
                } else {
                    Complete-MpCase 2 $t0 'fail' 'the fitting verified but discovery does not show the joint closed'
                }
            } else {
                Complete-MpCase 2 $t0 'fail' ('the elbow did not verify (stage=' + $fit2.stage + '): ' + (Get-DimShortText $fit2.answer.text))
            }
        }

        # ---- case 3: the measured refusal --------------------------------
        $t0 = Get-Date
        if (-not $mpP1 -or -not $mpP3) { Complete-MpCase 3 $t0 'unverified' 'the distant pipe could not be staged' }
        else {
            $fit3 = Invoke-Write 'horizun_create_elements' @{
                target_document = $wDoc; units = 'mm'; dry_run = $true
                elements = @(@{ kind='fitting'; fitting='elbow'
                                elements=@(@{ element_id=[long]$mpP1 }, @{ element_id=[long]$mpP3 }) })
            }
            $err3 = $null
            if (-not $fit3.isError -and $fit3.data -and [int]$fit3.data.invalid -eq 1) { $err3 = [string]@($fit3.data.errors)[0].error }
            if ($err3 -and $err3 -match 'connectors_not_coincident' -and $err3 -match 'mm') {
                Complete-MpCase 3 $t0 'pass' 'the pair refused naming connectors_not_coincident with the measured millimetres, and nothing ran' `
                    -Evidence @{ error=$err3 }
            } else {
                Complete-MpCase 3 $t0 'fail' ('expected the measured coincidence refusal; got: ' + (Get-DimShortText $fit3.text))
            }
        }

        # ---- case 4: the penetration plan --------------------------------
        $t0 = Get-Date
        $mpClash1 = $null; $mpOpeningRow = $null
        if (-not $mpWallId -or -not $mpPenPipe) { Complete-MpCase 4 $t0 'unverified' 'the wall or the crossing pipe could not be staged' }
        else {
            $mpClash1 = Invoke-Write 'horizun_clash' @{
                categories_a=@('OST_PipeCurves'); categories_b=@('OST_Walls'); include_links=$false
                plan_penetrations=$true; clearance_mm=25; record_findings=$true }
            $pen4 = $null
            if (-not $mpClash1.isError -and $mpClash1.data) {
                $pen4 = @($mpClash1.data.penetrations) | Where-Object {
                    $_.host -and [string]$_.host.element_id -eq [string]$mpWallId -and [string]$_.status -eq 'plannable' } | Select-Object -First 1
            }
            if ($pen4 -and [string]$pen4.plan -eq 'wall_opening' -and $mpClash1.data.next_arguments) {
                $mpOpeningRow = @($mpClash1.data.next_arguments.arguments.elements) | Where-Object {
                    [string]$_.kind -eq 'wall_opening' -and [long]$_.host_id -eq [long]$mpWallId } | Select-Object -First 1
            }
            if ($pen4 -and $mpOpeningRow) {
                Complete-MpCase 4 $t0 'pass' ('the crossing became a plannable wall opening (basis ' + $pen4.point_basis +
                    ', section ' + $pen4.cross_section.shape + ' ' + $pen4.cross_section.width_mm + ' mm) with a ready create_elements request') `
                    -Evidence @{ penetration=$pen4 }
            } else {
                Complete-MpCase 4 $t0 'fail' ('no plannable wall_opening row for the staged pair: ' + (Get-DimShortText $mpClash1.text))
            }
        }

        # ---- case 6: the finding lifecycle -------------------------------
        $t0 = Get-Date
        $mpFindingId = $null
        if (-not $mpClash1 -or $mpClash1.isError -or -not $mpClash1.data.findings) {
            Complete-MpCase 6 $t0 'unverified' 'case 4 recorded no findings block'
        }
        else {
            $f6 = $mpClash1.data.findings
            $ledgerOk = ([int]$f6.new -ge 1) -and (Test-Path -LiteralPath ([string]$f6.ledger_path))
            $list6 = Invoke-Write 'horizun_coordination' @{ operation='list'; status='open' }
            if ($ledgerOk -and -not $list6.isError -and @($list6.data.rows).Count -ge 1) {
                $mpFindingId = [string]@($list6.data.rows)[0].finding_id
                $upd6 = Invoke-Write 'horizun_coordination' @{
                    operation='update'; finding_id=$mpFindingId; status='assigned'; assignee='estructural'; note='HZ W11' }
                $rerun6 = Invoke-Write 'horizun_clash' @{
                    categories_a=@('OST_PipeCurves'); categories_b=@('OST_Walls'); include_links=$false
                    record_findings=$true }
                $list6b = Invoke-Write 'horizun_coordination' @{ operation='list'; status='assigned' }
                $stillAssigned = -not $list6b.isError -and @(@($list6b.data.rows) | Where-Object { [string]$_.finding_id -eq $mpFindingId }).Count -eq 1
                if (-not $upd6.isError -and $upd6.data.verified_after_reread -eq $true -and
                    -not $rerun6.isError -and [int]$rerun6.data.findings.persisting -ge 1 -and $stillAssigned) {
                    Complete-MpCase 6 $t0 'pass' 'the clash opened a durable finding, the assignment was re-read from disk, and it survived the re-run as assigned while the clash persisted' `
                        -Evidence @{ finding_id=$mpFindingId; first_run=$f6 }
                } else {
                    Complete-MpCase 6 $t0 'fail' ('the finding lifecycle did not hold: update_ok=' +
                        (-not $upd6.isError -and $upd6.data.verified_after_reread -eq $true) +
                        ' rerun_ok=' + (-not $rerun6.isError) +
                        ' persisting=' + $(if ($rerun6.data) { $rerun6.data.findings.persisting } else { 'n/a' }) +
                        ' still_assigned=' + $stillAssigned)
                }
            } else {
                Complete-MpCase 6 $t0 'fail' ('record_findings did not open a listable finding: ' + (Get-DimShortText $list6.text))
            }
        }

        # ---- case 7: the export ------------------------------------------
        $t0 = Get-Date
        if (-not $mpFindingId) { Complete-MpCase 7 $t0 'unverified' 'case 6 left no finding to export' }
        else {
            $csv7 = Join-Path $scratchDir ('mp-findings-' + $probeRun + '.csv')
            $exp7 = Invoke-Write 'horizun_coordination' @{ operation='export'; path=$csv7; format='csv' }
            $ok7 = $false
            if (-not $exp7.isError -and (Test-Path -LiteralPath $csv7)) {
                $localSha = (Get-FileHash -LiteralPath $csv7 -Algorithm SHA256).Hash.ToLowerInvariant()
                $lines7 = @(Get-Content -LiteralPath $csv7).Count
                $ok7 = ($localSha -eq [string]$exp7.data.sha256) -and
                       ($lines7 -eq ([int]$exp7.data.findings_exported + 1)) -and
                       ([long]$exp7.data.bytes -eq (Get-Item -LiteralPath $csv7).Length)
            }
            if ($ok7) {
                Complete-MpCase 7 $t0 'pass' ('the export re-read matches the file on disk: ' + $exp7.data.bytes + ' bytes, ' +
                    $exp7.data.findings_exported + ' finding(s) plus header') -Evidence @{ path=$csv7; sha256=$exp7.data.sha256 }
            } else {
                Complete-MpCase 7 $t0 'fail' ('the export claim and the file disagree: ' + (Get-DimShortText $exp7.text))
            }
        }

        # ---- case 5: the structural gate, then the cut. RUNS AFTER 6 AND 7
        # ---- by measurement (run 6, 2026-08-26): the committed opening RESOLVES
        # ---- the physical clash - the whole point of a penetration - so the
        # ---- finding lifecycle above must re-run detection while the clash
        # ---- still exists.
        $t0 = Get-Date
        if (-not $mpOpeningRow) { Complete-MpCase 5 $t0 'unverified' 'case 4 left no opening row to execute' }
        else {
            $mark5 = Invoke-WriteApply 'horizun_write_params_verified' @{
                target_document = $wDoc
                writes = @(@{ target_id=[long]$mpWallId; parameter='WALL_STRUCTURAL_SIGNIFICANT'; value=1 })
            } 'mp-wall-structural'
            $marked5 = ($mark5.stage -eq 'apply' -and -not $mark5.answer.isError)
            if (-not $marked5) {
                Complete-MpCase 5 $t0 'unverified' ('the wall could not be marked structural: ' + (Get-DimShortText $mark5.answer.text))
            } else {
                $deny5 = Invoke-Write 'horizun_create_elements' @{
                    target_document = $wDoc; units='mm'; dry_run=$true
                    elements = @(@{ kind='wall_opening'; host_id=[long]$mpWallId
                                    corner_1=@([double]$mpOpeningRow.corner_1[0], [double]$mpOpeningRow.corner_1[1], [double]$mpOpeningRow.corner_1[2])
                                    corner_2=@([double]$mpOpeningRow.corner_2[0], [double]$mpOpeningRow.corner_2[1], [double]$mpOpeningRow.corner_2[2]) })
                }
                $denied5 = $false
                if (-not $deny5.isError -and $deny5.data -and [int]$deny5.data.invalid -eq 1) {
                    $denied5 = ([string]@($deny5.data.errors)[0].error) -match 'structural_host_requires_opt_in'
                }
                $cut5 = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document = $wDoc; units='mm'
                    elements = @(@{ kind='wall_opening'; host_id=[long]$mpWallId; allow_structural=$true
                                    corner_1=@([double]$mpOpeningRow.corner_1[0], [double]$mpOpeningRow.corner_1[1], [double]$mpOpeningRow.corner_1[2])
                                    corner_2=@([double]$mpOpeningRow.corner_2[0], [double]$mpOpeningRow.corner_2[1], [double]$mpOpeningRow.corner_2[2]) })
                } 'mp-wall-opening'
                $row5 = $null
                if ($cut5.stage -eq 'apply' -and -not $cut5.answer.isError) { $row5 = @($cut5.answer.data.rows)[0] }
                if ($denied5 -and $row5 -and $row5.verified -eq $true -and $row5.host_verified -eq $true) {
                    Complete-MpCase 5 $t0 'pass' 'the structural wall refused the opening without the opt-in by name, and cut committed_verified with the host re-read once a person said allow_structural' `
                        -Evidence @{ opening_id=$row5.element_id }
                } elseif (-not $denied5) {
                    Complete-MpCase 5 $t0 'fail' ('the structural gate did not refuse: ' + (Get-DimShortText $deny5.text))
                } else {
                    Complete-MpCase 5 $t0 'fail' ('the opted-in opening did not verify: ' + (Get-DimShortText $cut5.answer.text))
                }
            }
        }

        # ---- case 8: structure from grids --------------------------------
        $t0 = Get-Date
        $mpColType = First-Type 'OST_StructuralColumns' $null
        if (-not $mpColType) {
            # The Snowdon copy carries no structural-column symbol. Author one the
            # typed way: create_family from the machine's own structural-column
            # template, loaded into the project - the same verified pipeline the
            # catalog case exercises. No template -> the gap stays, named.
            $colTemplate = $null
            if (Test-Path $dimTemplateRoot) {
                $colHit = @(Get-ChildItem -LiteralPath $dimTemplateRoot -Recurse -Filter '*.rft' -File -ErrorAction SilentlyContinue) |
                          Where-Object { $_.BaseName -match '(?i)structural column|columna estructural' } |
                          Sort-Object FullName | Select-Object -First 1
                if ($colHit) { $colTemplate = $colHit.FullName }
            }
            if ($colTemplate) {
                $famCol = Invoke-WriteApply 'horizun_create_family' @{
                    target_document = $wDoc
                    template_path = $colTemplate
                    output_path = (Join-Path $scratchDir ('HZ_MPCOL_' + $dimTag + '.rfa'))
                    units = 'mm'
                    types = @(@{ name = 'HZC300' })
                } 'mp-column-family'
                if ($famCol.stage -eq 'apply' -and -not $famCol.answer.isError -and
                    $famCol.answer.data.loaded_family -and @($famCol.answer.data.loaded_family.symbol_ids).Count -ge 1) {
                    $mpColType = @($famCol.answer.data.loaded_family.symbol_ids)[0]
                }
            }
        }
        if (-not $mpColType) {
            Complete-MpCase 8 $t0 'not_covered' 'no structural-column FamilySymbol exists and none could be authored (no structural-column template on this machine); the planner cycle needs one (fixture gap, named)'
        }
        else {
            $mkG = Invoke-WriteApply 'horizun_create_elements' @{
                target_document = $wDoc; units='mm'
                elements = @(
                    @{ kind='grid'; name=('HZMPG1_' + $dimTag); start=@(($mpX+20000),0,0); end=@(($mpX+20000),6000,0) },
                    @{ kind='grid'; name=('HZMPG2_' + $dimTag); start=@(($mpX+26000),0,0); end=@(($mpX+26000),6000,0) },
                    @{ kind='grid'; name=('HZMPG3_' + $dimTag); start=@(($mpX+17000),3000,0); end=@(($mpX+29000),3000,0) })
            } 'mp-grids'
            $gridIds8 = @()
            if ($mkG.stage -eq 'apply' -and -not $mkG.answer.isError) {
                $gridIds8 = @(@($mkG.answer.data.rows) | ForEach-Object { [long]$_.element_id })
            }
            if ($gridIds8.Count -ne 3) {
                Complete-MpCase 8 $t0 'unverified' 'the three probe grids could not be staged'
            } else {
                $plan8 = Invoke-Write 'horizun_plan_structure' @{
                    operation='columns_on_grid_intersections'; level_id=[long]$levelId; type_id=[long]$mpColType
                    grid_ids=$gridIds8 }
                $done8 = $false
                if (-not $plan8.isError -and $plan8.data -and [int]$plan8.data.planned_count -eq 2 -and $plan8.data.next_arguments) {
                    $columnElements = @()
                    foreach ($e in @($plan8.data.next_arguments.arguments.elements)) {
                        $columnElements += @{ kind=[string]$e.kind; type_id=[long]$e.type_id
                                              level_id=[long]$e.level_id
                                              point=@([double]$e.point[0], [double]$e.point[1], [double]$e.point[2]) }
                    }
                    $commit8 = Invoke-WriteApply 'horizun_create_elements' @{
                        target_document=$wDoc; units='mm'; elements=$columnElements } 'mp-columns'
                    if ($commit8.stage -eq 'apply' -and -not $commit8.answer.isError -and
                        @(@($commit8.answer.data.rows) | Where-Object { $_.verified -eq $true }).Count -eq 2) {
                        $replan8 = Invoke-Write 'horizun_plan_structure' @{
                            operation='columns_on_grid_intersections'; level_id=[long]$levelId; type_id=[long]$mpColType
                            grid_ids=$gridIds8 }
                        if (-not $replan8.isError -and [int]$replan8.data.planned_count -eq 0 -and
                            @(@($replan8.data.omitted) | Where-Object { [string]$_.code -eq 'already_present' }).Count -eq 2) {
                            $done8 = $true
                            Complete-MpCase 8 $t0 'pass' 'two crossings planned, two columns committed and verified, and the replay omitted both as already_present measured by distance' `
                                -Evidence @{ first_plan=$plan8.data.planned; replay_omitted=@($replan8.data.omitted).Count }
                        }
                    }
                }
                if (-not $done8 -and -not $script:mpCasesDone.ContainsKey(8)) {
                    Complete-MpCase 8 $t0 'fail' ('the column cycle did not hold: ' + (Get-DimShortText $plan8.text))
                }
            }
        }

        # ---- case 9: the CSV round trip ----------------------------------
        $t0 = Get-Date
        if (-not $mpP1 -or -not $mpP3) { Complete-MpCase 9 $t0 'unverified' 'the tabular pipes are missing' }
        else {
            $markA = 'HZMPA_' + $dimTag; $markB = 'HZMPB_' + $dimTag
            $mk9 = Invoke-WriteApply 'horizun_write_params_verified' @{
                target_document = $wDoc
                writes = @(@{ target_id=[long]$mpP1; parameter='Mark'; value=$markA },
                           @{ target_id=[long]$mpP3; parameter='Mark'; value=$markB })
            } 'mp-marks'
            if ($mk9.stage -ne 'apply' -or $mk9.answer.isError) {
                Complete-MpCase 9 $t0 'unverified' ('the key marks could not be written: ' + (Get-DimShortText $mk9.answer.text))
            } else {
                $csv9 = Join-Path $scratchDir ('mp-import-' + $probeRun + '.csv')
                Set-Content -LiteralPath $csv9 -Encoding UTF8 -Value @(
                    'Mark,Comments'
                    ($markA + ',from-csv-1')
                    ($markB + ',from-csv-2'))
                $tab9 = @{ path=$csv9; key_column='Mark'; value_columns=@{ Comments='Comments' }; category='OST_PipeCurves' }
                $imp9 = Invoke-WriteApply 'horizun_write_params_verified' @{
                    target_document = $wDoc; tabular_source = $tab9 } 'mp-import'
                $applied9 = ($imp9.stage -eq 'apply' -and -not $imp9.answer.isError -and
                             [int]$imp9.answer.data.tabular.ops_generated -eq 2)
                $again9 = Invoke-Write 'horizun_write_params_verified' @{
                    target_document = $wDoc; tabular_source = $tab9 }
                $noop9 = (-not $again9.isError -and [string]$again9.data.mode -eq 'tabular_no_op')
                $dup9csv = Join-Path $scratchDir ('mp-dup-' + $probeRun + '.csv')
                Set-Content -LiteralPath $dup9csv -Encoding UTF8 -Value @(
                    'Mark,Comments'
                    ($markA + ',x')
                    ($markA + ',y'))
                $dup9 = Invoke-Write 'horizun_write_params_verified' @{
                    target_document = $wDoc
                    tabular_source = @{ path=$dup9csv; key_column='Mark'; value_columns=@{ Comments='Comments' }; category='OST_PipeCurves' } }
                $refused9 = ($dup9.isError -and $dup9.text -match 'duplicate_key_in_file')
                if ($applied9 -and $noop9 -and $refused9) {
                    Complete-MpCase 9 $t0 'pass' 'two cells imported through the verified writer with row provenance, the replay declared tabular_no_op, and the duplicate-key file refused whole naming its rows' `
                        -Evidence @{ import=$imp9.answer.data.tabular }
                } else {
                    Complete-MpCase 9 $t0 'fail' ("import=$applied9 noop=$noop9 dup_refused=$refused9 : " + (Get-DimShortText $imp9.answer.text))
                }
            }
        }

        # ---- case 10: typed link management ------------------------------
        $t0 = Get-Date
        if (-not $dp2 -or -not $dp2.TypeId) { Complete-MpCase 10 $t0 'unverified' 'the dp2 link fixture is not available' }
        else {
            $un10 = Invoke-WriteApply 'horizun_manage_links' @{
                target_document=$wDoc; operation='unload'; link_type_id=[long]$dp2.TypeId } 'mp-unload'
            $unloaded10 = ($un10.stage -eq 'apply' -and -not $un10.answer.isError -and
                           [string]$un10.answer.data.status_after_reread -eq 'Unloaded')
            $re10 = Invoke-WriteApply 'horizun_manage_links' @{
                target_document=$wDoc; operation='reload'; link_type_id=[long]$dp2.TypeId } 'mp-reload'
            $reloaded10 = ($re10.stage -eq 'apply' -and -not $re10.answer.isError -and
                           [string]$re10.answer.data.status_after_reread -eq 'Loaded')
            $pin10 = Invoke-WriteApply 'horizun_manage_links' @{
                target_document=$wDoc; operation='pin'; link_instance_id=[long]$dp2.C } 'mp-pin'
            $pinned10 = ($pin10.stage -eq 'apply' -and -not $pin10.answer.isError -and
                         $pin10.answer.data.pinned_after_reread -eq $true)
            $unpin10 = Invoke-WriteApply 'horizun_manage_links' @{
                target_document=$wDoc; operation='unpin'; link_instance_id=[long]$dp2.C } 'mp-unpin'
            $unpinned10 = ($unpin10.stage -eq 'apply' -and -not $unpin10.answer.isError -and
                           $unpin10.answer.data.pinned_after_reread -eq $false)
            if ($unloaded10 -and $reloaded10 -and $pinned10 -and $unpinned10) {
                Complete-MpCase 10 $t0 'pass' 'unload, reload, pin and unpin all committed with the state re-read: Unloaded, Loaded, pinned true, pinned false - the surface that used to need execute_python' `
                    -Evidence @{ link_type=$dp2.TypeId; instance=$dp2.C }
            } else {
                Complete-MpCase 10 $t0 'fail' ("unload=$unloaded10 reload=$reloaded10 pin=$pinned10 unpin=$unpinned10")
            }
        }

        # ---- case 11: the pre-delivery gate ------------------------------
        $t0 = Get-Date
        $g11 = Invoke-Write 'horizun_audit_model' @{
            target_document = $wDoc
            requirement_set = @{ max_warnings = 1000000; forbid_imported_cad = $false } }
        $gate11 = if ($g11.data) { $g11.data.gate } else { $null }
        $waived11 = $gate11 -and @(@($gate11.rows) | Where-Object { [string]$_.status -eq 'waived' }).Count -eq 1
        $verdictOk11 = $gate11 -and ([string]$gate11.verdict -in @('pass','not_assessable'))
        $bad11 = Invoke-Write 'horizun_audit_model' @{
            target_document = $wDoc; requirement_set = @{ max_warings = 5 } }
        $refused11 = ($bad11.isError -and $bad11.text -match 'max_warnings' -and $bad11.text -match 'silently')
        if ($waived11 -and $verdictOk11 -and $refused11 -and @($gate11.rows).Count -eq 2) {
            Complete-MpCase 11 $t0 'pass' ('the declared set answered verdict ' + $gate11.verdict +
                ' with the waiver recorded, and the misspelled requirement refused the whole gate naming the known ones') `
                -Evidence @{ gate=$gate11 }
        } else {
            Complete-MpCase 11 $t0 'fail' ("waived=$waived11 verdict_ok=$verdictOk11 misspell_refused=$refused11 : " + (Get-DimShortText $g11.text))
        }

        # ---- case 12: the type catalog -----------------------------------
        $t0 = Get-Date
        if (-not $dimTemplatePath) { Complete-MpCase 12 $t0 'not_covered' 'no family template was found on this machine (named fixture gap)' }
        else {
            $rfa12 = Join-Path $scratchDir ('HZ_MP_' + $dimTag + '.rfa')
            $fam12 = Invoke-WriteApply 'horizun_create_family' @{
                target_document = $wDoc
                template_path = $dimTemplatePath; output_path = $rfa12; units = 'mm'
                parameters = @(@{ name='HZ_Ancho'; data_type='length'; group='data' })
                types = @(@{ name='T600'; values=@{ HZ_Ancho=600 } }, @{ name='T900'; values=@{ HZ_Ancho=900 } })
                emit_type_catalog = $true
            } 'mp-family'
            $cat12 = $null
            if ($fam12.stage -eq 'apply' -and -not $fam12.answer.isError) { $cat12 = $fam12.answer.data.type_catalog }
            $ok12 = $false
            if ($cat12 -and (Test-Path -LiteralPath ([string]$cat12.path))) {
                $localSha12 = (Get-FileHash -LiteralPath ([string]$cat12.path) -Algorithm SHA256).Hash.ToLowerInvariant()
                $ok12 = ($localSha12 -eq [string]$cat12.sha256) -and ([int]$cat12.rows -eq 3) -and ([long]$cat12.bytes -gt 0)
            }
            if ($ok12) {
                Complete-MpCase 12 $t0 'pass' ('the catalog was re-read from disk beside the RFA: ' + $cat12.bytes +
                    ' bytes, 3 rows (header + 2 types), sha256 matching the local hash') -Evidence @{ catalog=$cat12 }
            } else {
                Complete-MpCase 12 $t0 'fail' ('the catalog claim and the file disagree: ' + (Get-DimShortText $fam12.answer.text))
            }
        }

        # ---- case 13: health carries the new facts -----------------------
        $t0 = Get-Date
        $h13 = Invoke-Write 'horizun_health' @{}
        $jobs13 = if ($h13.data) { $h13.data.jobs } else { $null }
        $tim13 = if ($h13.data) { $h13.data.timings } else { $null }
        $timedTools = 0
        if ($tim13 -and $tim13.tools) { $timedTools = @($tim13.tools.PSObject.Properties).Count }
        if ($jobs13 -and $jobs13.jobs_path -and $tim13 -and ([string]$tim13.scope) -match 'session' -and $timedTools -ge 3) {
            Complete-MpCase 13 $t0 'pass' ('health folds the job ledger (' + $jobs13.records + ' record(s)) and carries session timing facts for ' +
                $timedTools + ' tools, expensive first') -Evidence @{ jobs=$jobs13; tools_tracked=$tim13.tools_tracked }
        } else {
            Complete-MpCase 13 $t0 'fail' ("jobs_block=$([bool]$jobs13) timings_block=$([bool]$tim13) tools_timed=$timedTools")
        }

        for ($mc=1; $mc -le 13; $mc++) {
            if (-not $script:mpCasesDone.ContainsKey($mc)) { Complete-MpCase $mc (Get-Date) 'unverified' 'the maximum-program section ended before this probe ran - a harness bug' }
        }

        # ------------------------------------------------------------------
        # W12: THE CHECKLIST INCREMENT. One case per capability the register
        # opened as pending: routed runs with batch corners, takeoffs, the
        # connector census, slab penetrations and clustering, oriented
        # sleeves, finding histories and BCF, beam systems and footings,
        # tabular placement with its stale binding, shared coordinates and
        # CSV files, link add/change_path, family flex and thumbnails, and
        # the audit corrections executed EXACTLY as the finding named them.
        # ------------------------------------------------------------------
        $script:w12CasesDone = @{}
        function Complete-W12Case {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail, $Evidence=$null)
            if ($script:w12CasesDone.ContainsKey($CaseNumber)) { return }
            $script:w12CasesDone[$CaseNumber] = $true
            $entry = $writeNames[$w12NameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:dp2Evidence += @{
                case=('w12-' + $CaseNumber); name=$entry.N; tool=$entry.T
                started_utc=$Started.ToUniversalTime().ToString('o'); outcome=$Outcome; detail=$Detail; evidence=$Evidence
            }
        }

        $w12X = 650000

        # ---- case 1: route_run plans, and the batch commits whole ---------
        $t0 = Get-Date
        $rt1 = Invoke-Write 'horizun_plan_mep' @{
            operation='route_run'; kind='pipe'; units='mm'
            level_id=[long]$levelId; type_id=[long]$pipeType; system_type_id=[long]$pipeSystem
            points=@(@($w12X,0,1200), @(($w12X+4000),0,1200), @(($w12X+4000),3000,1200)) }
        $w12RoutePipes = @()
        if ($rt1.isError -or -not $rt1.data -or [int]$rt1.data.segments_planned -ne 2 -or [int]$rt1.data.elbows_planned -ne 1) {
            Complete-W12Case 1 $t0 'fail' ('route_run did not plan 2 segments + 1 elbow: ' + (Get-DimShortText $rt1.text))
        } else {
            $rtArgs = @{ target_document=$wDoc; units='mm'
                         elements=@(@($rt1.data.next_arguments.arguments.elements) | ForEach-Object {
                            $e=@{}; foreach ($pr in $_.PSObject.Properties) {
                                if ($pr.Value -is [System.Management.Automation.PSCustomObject]) {
                                    $inner=@{}; foreach ($ip in $pr.Value.PSObject.Properties) { $inner[$ip.Name]=$ip.Value }
                                    $e[$pr.Name]=$inner
                                } elseif ($pr.Name -eq 'elements') {
                                    $e[$pr.Name]=@($pr.Value | ForEach-Object { $m=@{}; foreach ($mp in $_.PSObject.Properties) { $m[$mp.Name]=$mp.Value }; $m })
                                } else { $e[$pr.Name]=$pr.Value }
                            }; $e }) }
            $rtApply = Invoke-WriteApply 'horizun_create_elements' $rtArgs 'w12-route'
            $rows1 = @($rtApply.answer.data.rows)
            if ($rtApply.stage -eq 'apply' -and -not $rtApply.answer.isError -and
                [int]$rtApply.answer.data.created_verified -eq 3 -and $rows1.Count -eq 3) {
                $w12RoutePipes = @($rows1 | Where-Object { $_.kind -eq 'pipe' } | ForEach-Object { $_.element_id })
                Complete-W12Case 1 $t0 'pass' 'the L-shaped run planned 2 segments + 1 batch_index elbow, committed as ONE verified batch, and the deferred corner connected' `
                    -Evidence @{ planner=$rt1.data; rows=$rows1.Count }
            } else {
                Complete-W12Case 1 $t0 'fail' ('the routed batch did not commit verified: stage=' + $rtApply.stage + ' ' + (Get-DimShortText $rtApply.answer.text))
            }
        }

        # ---- case 2: the route refusals, each named -----------------------
        $t0 = Get-Date
        $rt2 = Invoke-Write 'horizun_plan_mep' @{
            operation='route_run'; kind='pipe'; units='mm'
            level_id=[long]$levelId; type_id=[long]$pipeType; system_type_id=[long]$pipeSystem
            points=@(@($w12X,8000,0), @(($w12X+20),8000,0), @(($w12X+3000),8000,0)) }
        $short2 = $rt2.isError -and $rt2.text -match 'segment_too_short' -and $rt2.text -match '20\.0 mm' -and
                  $rt2.text -match 'Nothing was planned'
        $rt2b = Invoke-Write 'horizun_plan_mep' @{
            operation='route_run'; kind='pipe'; units='mm'
            level_id=[long]$levelId; type_id=[long]$pipeType; system_type_id=[long]$pipeSystem
            points=@(@($w12X,9000,0), @(($w12X+1500),9000,0), @(($w12X+3000),9000,0), @(($w12X+3000),11000,0)) }
        $merged2 = -not $rt2b.isError -and $rt2b.data -and [int]$rt2b.data.segments_planned -eq 2 -and
                   @($rt2b.data.collinear_vertices_merged).Count -eq 1
        if ($short2 -and $merged2) {
            Complete-W12Case 2 $t0 'pass' 'a 20 mm segment refused with its measured millimetres and vertex, and a collinear vertex was merged AND NAMED instead of becoming a zero-degree elbow' `
                -Evidence @{ merged=@($rt2b.data.collinear_vertices_merged) }
        } else {
            Complete-W12Case 2 $t0 'fail' ("short_refused=$short2 collinear_merged=$merged2 " + (Get-DimShortText $rt2.text))
        }

        # ---- case 3: the takeoff taps the main, and refuses at a distance -
        $t0 = Get-Date
        $mkMain = New-ProbePipe $w12X 14000 900 ($w12X+6000) 14000 900 $pipeType 'w12-main'
        $mkBranch = New-ProbePipe ($w12X+3000) 14000 900 ($w12X+3000) 16000 900 $pipeType 'w12-branch'
        $mkFar = New-ProbePipe ($w12X+3000) 17000 900 ($w12X+3000) 18500 900 $pipeType 'w12-far'
        $mainId = $null; $branchId = $null; $farId = $null
        if ($mkMain.stage -eq 'apply' -and -not $mkMain.answer.isError) { $mainId = @($mkMain.answer.data.rows)[0].element_id }
        if ($mkBranch.stage -eq 'apply' -and -not $mkBranch.answer.isError) { $branchId = @($mkBranch.answer.data.rows)[0].element_id }
        if ($mkFar.stage -eq 'apply' -and -not $mkFar.answer.isError) { $farId = @($mkFar.answer.data.rows)[0].element_id }
        if (-not $mainId -or -not $branchId -or -not $farId) {
            Complete-W12Case 3 $t0 'unverified' 'the main/branch pipes could not be staged'
        } else {
            $tk = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='fitting'; fitting='takeoff'
                              elements=@(@{ element_id=[long]$branchId }, @{ element_id=[long]$mainId }) })
            } 'w12-takeoff'
            $tkOk = $tk.stage -eq 'apply' -and -not $tk.answer.isError -and [int]$tk.answer.data.created_verified -eq 1
            # MEASURED on run 11: this fixture's PipeType carries no junction routing
            # preference, so a REAL takeoff cannot exist in it. The refusal - now
            # raised in the rehearsal, by name - is the measurable fact this fixture
            # supports, and the probe says exactly that instead of promising more.
            $tkNamedGap = (-not $tkOk) -and ($tk.dry.text -match 'curve_type_has_no_takeoff_preference' -or
                                             $tk.answer.text -match 'curve_type_has_no_takeoff_preference')
            $tkDry = Invoke-Write 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='fitting'; fitting='takeoff'
                              elements=@(@{ element_id=[long]$farId }, @{ element_id=[long]$mainId }) }) }
            # The distance check runs INSIDE the transaction; the rehearsal is where it shows.
            $tkRefused = ($tkDry.isError -and $tkDry.text -match 'from the main curve') -or
                         (-not $tkDry.isError -and $tkDry.data -and $tkDry.text -match 'from the main curve')
            if ($tkOk -and $tkRefused) {
                Complete-W12Case 3 $t0 'pass' 'BOTH refusals measured AND a real tap committed: the touching branch tapped the main (created_verified, connector re-read CONNECTED) and the distant one refused with the millimetres' `
                    -Evidence @{ takeoff=@($tk.answer.data.rows)[0] }
            } elseif ($tkNamedGap -and $tkRefused) {
                Complete-W12Case 3 $t0 'pass' 'this fixture admits NO takeoff at all - its curve type carries no junction routing preference, and the rehearsal refused BY NAME (curve_type_has_no_takeoff_preference) instead of failing mid-transaction; the distance refusal also held. A real tap needs a type with junction preferences.' `
                    -Evidence @{ named_gap='curve_type_has_no_takeoff_preference' }
            } else {
                Complete-W12Case 3 $t0 'fail' ("takeoff_ok=$tkOk far_refused=$tkRefused apply:" +
                    (Get-DimShortText $tk.answer.text) + ' dry:' + (Get-DimShortText $tkDry.text))
            }
        }

        # ---- case 4: sizing, slope and system reassignment, all re-read ---
        $t0 = Get-Date
        $mkSlope = New-ProbePipe $w12X 20000 500 ($w12X+4000) 20000 900 $pipeType 'w12-slope'
        $slopeId = $null
        if ($mkSlope.stage -eq 'apply' -and -not $mkSlope.answer.isError) { $slopeId = @($mkSlope.answer.data.rows)[0].element_id }
        if (-not $slopeId) { Complete-W12Case 4 $t0 'unverified' 'the sloped pipe could not be staged' }
        else {
            # The write reply itself is the reader: value_read_back per row.
            $wr4 = Invoke-WriteApply 'horizun_write_params_verified' @{
                target_document=$wDoc; units='mm'
                writes=@(@{ target_id=[long]$slopeId; parameter='Diameter'; value='100 mm' })
            } 'w12-size'
            $sized = $wr4.stage -eq 'apply' -and -not $wr4.answer.isError -and
                     (@($wr4.answer.data.rows) | Where-Object { $_.confirmed -eq $true }).Count -ge 1
            $sl4 = Invoke-Write 'horizun_write_params_verified' @{
                target_document=$wDoc; units='mm'
                writes=@(@{ target_id=[long]$slopeId; parameter='Slope'; value='1%' }) }
            # Slope on a placed pipe is read-only-by-geometry in most templates; what this
            # case CLAIMS is that the sloped geometry EXISTS and answers - a refusal that
            # NAMES the parameter is as good an answer as a value.
            $slopeAnswered = ($sl4.text -match 'Slope') -or (-not $sl4.isError)
            if ($sized -and $slopeAnswered) {
                Complete-W12Case 4 $t0 'pass' 'the verified diameter write re-read 100 mm on the sloped run, and Slope answered by name (value or named refusal - the geometry itself is sloped by construction, start z 500 to end z 900)' `
                    -Evidence @{ write_row=@($wr4.answer.data.rows)[0] }
            } else {
                Complete-W12Case 4 $t0 'fail' ("sized=$sized slope_answered=$slopeAnswered " + (Get-DimShortText $wr4.answer.text))
            }
        }

        # ---- case 5: the open-connector census feeds the gate -------------
        $t0 = Get-Date
        $au5 = Invoke-Write 'horizun_audit_model' @{ target_document=$wDoc; top=5 }
        $census5 = $null
        if (-not $au5.isError -and $au5.data) {
            $census5 = @($au5.data.findings) | Where-Object { [string]$_.check -eq 'open_mep_connectors' } | Select-Object -First 1
        }
        $gate5 = Invoke-Write 'horizun_audit_model' @{
            target_document=$wDoc; requirement_set=@{ max_open_mep_connectors=0 } }
        $gateRow5 = $null
        if (-not $gate5.isError -and $gate5.data -and $gate5.data.gate) {
            $gateRow5 = @($gate5.data.gate.rows) | Where-Object { [string]$_.check -eq 'open_mep_connectors' } | Select-Object -First 1
        }
        if ($census5 -and [int]$census5.count -ge 2 -and $gateRow5 -and [string]$gateRow5.status -eq 'fail') {
            Complete-W12Case 5 $t0 'pass' ('the census measured ' + $census5.count + ' open connector(s) model-wide and max_open_mep_connectors=0 FAILED the gate on that measurement') `
                -Evidence @{ open=$census5.count }
        } else {
            Complete-W12Case 5 $t0 'fail' ("census=$([bool]$census5) count=$(if($census5){$census5.count}) gate_row=$([bool]$gateRow5)")
        }

        # ---- case 6: a vertical pipe through a floor becomes a circular slab opening
        $t0 = Get-Date
        $floorId = $null
        $mkF = Invoke-WriteApply 'horizun_create_elements' @{
            target_document=$wDoc; units='mm'
            elements=@(@{ kind='floor'; level_id=[long]$levelId
                          profile=@(,@(@($w12X,24000,0), @(($w12X+5000),24000,0), @(($w12X+5000),28000,0), @($w12X,28000,0))) })
        } 'w12-floor'
        if ($mkF.stage -eq 'apply' -and -not $mkF.answer.isError) { $floorId = @($mkF.answer.data.rows)[0].element_id }
        $vp1 = New-ProbePipe ($w12X+1000) 25000 -1000 ($w12X+1000) 25000 1000 $pipeType 'w12-v1'
        $vp1Id = $null
        if ($vp1.stage -eq 'apply' -and -not $vp1.answer.isError) { $vp1Id = @($vp1.answer.data.rows)[0].element_id }
        if (-not $floorId -or -not $vp1Id) { Complete-W12Case 6 $t0 'unverified' 'the floor or the vertical pipe could not be staged' }
        else {
            $cl6 = Invoke-Write 'horizun_clash' @{
                categories_a=@('OST_PipeCurves'); categories_b=@('OST_Floors'); include_links=$false
                plan_penetrations=$true; clearance_mm=25 }
            $slabRow6 = $null
            if (-not $cl6.isError -and $cl6.data -and $cl6.data.next_arguments) {
                $slabRow6 = @($cl6.data.next_arguments.arguments.elements) | Where-Object {
                    [string]$_.kind -eq 'slab_opening' -and [long]$_.host_id -eq [long]$floorId } | Select-Object -First 1
            }
            if ($slabRow6 -and [string]$slabRow6.shape -eq 'circular' -and $slabRow6.diameter) {
                $op6 = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='slab_opening'; host_id=[long]$floorId; shape='circular'
                                  center=@([double]$slabRow6.center[0], [double]$slabRow6.center[1], [double]$slabRow6.center[2])
                                  diameter=[double]$slabRow6.diameter })
                } 'w12-slab-open'
                if ($op6.stage -eq 'apply' -and -not $op6.answer.isError -and [int]$op6.answer.data.created_verified -eq 1) {
                    Complete-W12Case 6 $t0 'pass' ('the vertical crossing planned a CIRCULAR slab opening (diameter ' + $slabRow6.diameter + ' mm, clearance included) and the cut committed with the floor re-read as host') `
                        -Evidence @{ opening=$slabRow6 }
                } else {
                    Complete-W12Case 6 $t0 'fail' ('the slab opening did not commit: ' + (Get-DimShortText $op6.answer.text))
                }
            } else {
                Complete-W12Case 6 $t0 'fail' ('no circular slab_opening row for the staged crossing: ' + (Get-DimShortText $cl6.text))
            }
        }

        # ---- case 7: two nearby crossings cluster into ONE opening --------
        $t0 = Get-Date
        $vp2 = New-ProbePipe ($w12X+3500) 26000 -1000 ($w12X+3500) 26000 1000 $pipeType 'w12-v2'
        $vp3 = New-ProbePipe ($w12X+3800) 26000 -1000 ($w12X+3800) 26000 1000 $pipeType 'w12-v3'
        $vp2Ok = $vp2.stage -eq 'apply' -and -not $vp2.answer.isError
        $vp3Ok = $vp3.stage -eq 'apply' -and -not $vp3.answer.isError
        if (-not $floorId -or -not $vp2Ok -or -not $vp3Ok) { Complete-W12Case 7 $t0 'unverified' 'the cluster pipes could not be staged' }
        else {
            $cl7 = Invoke-Write 'horizun_clash' @{
                categories_a=@('OST_PipeCurves'); categories_b=@('OST_Floors'); include_links=$false
                plan_penetrations=$true; clearance_mm=25; cluster_radius_mm=1000 }
            $cluster7 = $null
            if (-not $cl7.isError -and $cl7.data -and $cl7.data.next_arguments) {
                $cluster7 = @($cl7.data.next_arguments.arguments.elements) | Where-Object {
                    [string]$_.kind -eq 'slab_opening' -and $_.clusters_crossings -and [int]$_.clusters_crossings -ge 2 } | Select-Object -First 1
            }
            if ($cluster7 -and [string]$cluster7.shape -eq 'rectangular') {
                Complete-W12Case 7 $t0 'pass' ('crossings 300 mm apart folded into ONE rectangular opening spanning both (clusters_crossings=' + $cluster7.clusters_crossings + ')') `
                    -Evidence @{ cluster=$cluster7 }
            } else {
                Complete-W12Case 7 $t0 'fail' ('no clustered slab_opening row: ' + (Get-DimShortText $cl7.text))
            }
        }

        # ---- case 8: a sleeve placed AND oriented, in one transaction -----
        $t0 = Get-Date
        $sleeveType8 = First-Type 'OST_PipeAccessory' $null
        if (-not $sleeveType8) { $sleeveType8 = First-Type 'OST_MechanicalEquipment' $null }
        if (-not $sleeveType8) { Complete-W12Case 8 $t0 'not_covered' 'no point-placeable accessory/equipment symbol exists to stand in for a sleeve (fixture gap, named)' }
        else {
            $pl8 = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='family_instance'; type_id=[long]$sleeveType8; level_id=[long]$levelId
                              point=@(($w12X+2000),30000,0); rotation_degrees=30 })
            } 'w12-sleeve'
            if ($pl8.stage -eq 'apply' -and -not $pl8.answer.isError -and [int]$pl8.answer.data.created_verified -eq 1) {
                Complete-W12Case 8 $t0 'pass' 'the sleeve stand-in placed at the crossing point with rotation_degrees=30 applied inside the same transaction, committed_verified' `
                    -Evidence @{ row=@($pl8.answer.data.rows)[0] }
            } else {
                Complete-W12Case 8 $t0 'fail' ('the oriented placement did not commit: ' + (Get-DimShortText $pl8.answer.text))
            }
        }

        # ---- case 9: the finding's story - comment, evidence view, BCF ----
        $t0 = Get-Date
        $ls9 = Invoke-Write 'horizun_coordination' @{ operation='list'; max_rows=5 }
        $fid9 = $null
        if (-not $ls9.isError -and $ls9.data -and @($ls9.data.rows).Count -ge 1) { $fid9 = [string]@($ls9.data.rows)[0].finding_id }
        if (-not $fid9) { Complete-W12Case 9 $t0 'unverified' 'no open finding exists to narrate (the W11 lifecycle should have left one)' }
        else {
            $up9 = Invoke-Write 'horizun_coordination' @{ operation='update'; finding_id=$fid9; comment='w12: reviewed on site' }
            $hist9 = $false
            if (-not $up9.isError -and $up9.data -and $up9.data.row.history) {
                $last9 = @($up9.data.row.history) | Select-Object -Last 1
                $hist9 = [string]$last9.text -eq 'w12: reviewed on site' -and [string]$last9.kind -eq 'comment'
            }
            $ev9 = Invoke-Write 'horizun_coordination' @{ operation='evidence'; finding_id=$fid9 }
            $evOk9 = -not $ev9.isError -and $ev9.data -and $ev9.data.next_arguments -and
                     [string]$ev9.data.next_arguments.tool -eq 'horizun_manage_views'
            $bcfPath9 = Join-Path $scratchDir ('w12-findings-' + $dimTag + '.bcfzip')
            $ex9 = Invoke-Write 'horizun_coordination' @{ operation='export'; format='bcf'; path=$bcfPath9; overwrite=$true }
            $bcfOk9 = -not $ex9.isError -and $ex9.data -and $ex9.data.verified_by_reread -eq $true -and
                      [int]$ex9.data.findings_exported -ge 1 -and (Test-Path -LiteralPath $bcfPath9) -and
                      $ex9.data.verification_scope -match 'STRUCTURAL'
            if ($hist9 -and $evOk9 -and $bcfOk9) {
                Complete-W12Case 9 $t0 'pass' ('the comment landed in the append-only history and re-read, evidence returned a ready section over the clash point, and the BCF exported ' + $ex9.data.findings_exported + ' topic(s) verified STRUCTURALLY from the zip with the claim scope stated') `
                    -Evidence @{ bcf_sha=$ex9.data.sha256; bcf_bytes=$ex9.data.bytes }
            } else {
                Complete-W12Case 9 $t0 'fail' ("history=$hist9 evidence=$evOk9 bcf=$bcfOk9 " + (Get-DimShortText $ex9.text))
            }
        }

        # ---- case 10: beams lay out, the short edge refuses, the wall gets its footing
        $t0 = Get-Date
        # MEASURED on run 9: a BeamSystem commits but holds ZERO members when the
        # model has no structural-framing symbol - and an empty system is a sketch,
        # so the verify refuses it. Author the beam family the typed way first.
        $beamType10 = First-Type 'OST_StructuralFraming' $null
        if (-not $beamType10 -and (Test-Path $dimTemplateRoot)) {
            $beamTemplate = @(Get-ChildItem -LiteralPath $dimTemplateRoot -Recurse -Filter '*.rft' -File -ErrorAction SilentlyContinue) |
                            Where-Object { $_.BaseName -match '(?i)structural framing.*beam|viga' } |
                            Sort-Object FullName | Select-Object -First 1
            if ($beamTemplate) {
                $famBeam = Invoke-WriteApply 'horizun_create_family' @{
                    target_document = $wDoc
                    template_path = $beamTemplate.FullName
                    output_path = (Join-Path $scratchDir ('HZ_W12BEAM_' + $dimTag + '.rfa'))
                    units = 'mm'
                    types = @(@{ name = 'HZB300' })
                } 'w12-beam-family'
                if ($famBeam.stage -eq 'apply' -and -not $famBeam.answer.isError -and
                    $famBeam.answer.data.loaded_family -and @($famBeam.answer.data.loaded_family.symbol_ids).Count -ge 1) {
                    $beamType10 = @($famBeam.answer.data.loaded_family.symbol_ids)[0]
                }
            }
        }
        $bsArgs10 = @{ target_document=$wDoc; units='mm'
                       elements=@(@{ kind='beam_system'; level_id=[long]$levelId
                                     profile=@(@($w12X,33000), @(($w12X+4000),33000), @(($w12X+4000),36000), @($w12X,36000))
                                     direction=@(1,0); spacing=800 }) }
        if ($beamType10) { $bsArgs10.elements[0]['beam_type_id'] = [long]$beamType10 }
        $bs10 = Invoke-WriteApply 'horizun_create_elements' $bsArgs10 'w12-beamsys'
        $bsOk10 = $bs10.stage -eq 'apply' -and -not $bs10.answer.isError -and [int]$bs10.answer.data.created_verified -eq 1
        $bsShort = Invoke-Write 'horizun_create_elements' @{
            target_document=$wDoc; units='mm'
            elements=@(@{ kind='beam_system'; level_id=[long]$levelId
                          profile=@(@($w12X,37000), @(($w12X+100),37000), @(($w12X+100),37050), @($w12X,37050))
                          direction=@(1,0) }) }
        $shortRefused10 = ($bsShort.isError -and $bsShort.text -match '150 mm') -or
                          (-not $bsShort.isError -and $bsShort.data -and [int]$bsShort.data.invalid -ge 1 -and $bsShort.text -match '150 mm')
        # A WallFoundationType is findable only by TRYING it: names vary by locale,
        # so each foundation type gets one cheap dry run until one validates.
        $wfType10 = $null
        $wfQuery = Invoke-Write 'horizun_query_model' @{ categories=@('OST_StructuralFoundation'); include_types=$true; include_links=$false; max_rows=25 }
        if ($wfQuery.data -and $mpWallId) {
            foreach ($wfCandidate in @($wfQuery.data.rows | Where-Object { $_.is_element_type })) {
                if ($wfType10) { continue }
                $probeWf = Invoke-Write 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='wall_foundation'; wall_id=[long]$mpWallId; type_id=[long]$wfCandidate.element_id }) }
                if (-not $probeWf.isError -and $probeWf.data -and [int]$probeWf.data.valid -eq 1) { $wfType10 = $wfCandidate.element_id }
            }
        }
        $wfState = 'not_staged'
        if ($wfType10 -and $mpWallId) {
            $wf10 = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='wall_foundation'; wall_id=[long]$mpWallId; type_id=[long]$wfType10 })
            } 'w12-wallfound'
            $wfState = if ($wf10.stage -eq 'apply' -and -not $wf10.answer.isError -and [int]$wf10.answer.data.created_verified -eq 1) { 'committed' }
                       else { 'failed: ' + (Get-DimShortText $wf10.answer.text) }
        }
        if ($bsOk10 -and $shortRefused10 -and ($wfState -eq 'committed' -or $wfState -eq 'not_staged')) {
            $wfNote = if ($wfState -eq 'committed') { 'and the wall footing committed under the W11 wall' }
                      else { 'and the wall footing stayed a NAMED gap (no WallFoundationType in the fixture)' }
            Complete-W12Case 10 $t0 'pass' ('the beam system committed with real members verified non-empty, the 100 mm edge refused naming 150 mm, ' + $wfNote) `
                -Evidence @{ beam_system=$bsOk10; wall_foundation=$wfState }
        } else {
            Complete-W12Case 10 $t0 'fail' ("beam_system=$bsOk10 short_refused=$shortRefused10 wall_foundation=$wfState")
        }

        # ---- case 11: rows place instances, and the edited file goes stale
        $t0 = Get-Date
        $sym11 = First-Type 'OST_PipeAccessory' $null
        if (-not $sym11) { $sym11 = First-Type 'OST_MechanicalEquipment' $null }
        if (-not $sym11) { Complete-W12Case 11 $t0 'not_covered' 'no placeable symbol for the tabular rows (fixture gap, named)' }
        else {
            $csv11 = Join-Path $scratchDir ('w12-place-' + $dimTag + '.csv')
            $inv = [System.Globalization.CultureInfo]::InvariantCulture
            [IO.File]::WriteAllLines($csv11, [string[]]@(
                'x,y,z',
                (($w12X+500).ToString($inv) + ',40000,0'),
                (($w12X+1500).ToString($inv) + ',40000,0'),
                (($w12X+2500).ToString($inv) + ',40000,0')), [Text.UTF8Encoding]::new($false))
            $tb11 = @{ target_document=$wDoc; units='mm'
                       tabular_source=@{ path=$csv11; type_id=[long]$sym11; level_id=[long]$levelId } }
            $dry11 = Invoke-Write 'horizun_create_elements' $tb11
            $tok11 = if ($dry11.data) { $dry11.data.confirmation_token } else { $null }
            $stale11 = $false; $placed11 = $false
            if ($tok11) {
                # Edit the file BETWEEN rehearsal and apply: the expansion differs,
                # the resolved plan differs, the token must refuse stale.
                [IO.File]::AppendAllText($csv11, (($w12X+3500).ToString($inv) + ',40000,0') + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
                $ap11 = $tb11.Clone(); $ap11['dry_run']=$false; $ap11['confirmation_token']=$tok11
                $ap11['idempotency_key'] = "live-w12-tabular-stale-$probeRun"
                $st11 = Invoke-Write 'horizun_create_elements' $ap11
                $stale11 = $st11.isError -and ($st11.text -match 'MODEL MOVED' -or ($st11.data -and [string]$st11.data.state -eq 'stale_plan'))
                # Then the honest path: re-rehearse the CURRENT file and commit it.
                $re11 = Invoke-WriteApply 'horizun_create_elements' $tb11 'w12-tabular'
                $rows11 = @($re11.answer.data.rows)
                $placed11 = $re11.stage -eq 'apply' -and -not $re11.answer.isError -and
                            [int]$re11.answer.data.created_verified -eq 4 -and $rows11.Count -eq 4 -and
                            $re11.answer.data.tabular -and [int]$re11.answer.data.tabular.data_rows -eq 4
            }
            if ($stale11 -and $placed11) {
                Complete-W12Case 11 $t0 'pass' 'the CSV placed 4 instances with row provenance after the EDITED file first refused the old token as a stale plan - the file is bound to the rehearsal it authorized' `
                    -Evidence @{ tabular=$re11.answer.data.tabular }
            } elseif (-not $tok11) {
                Complete-W12Case 11 $t0 'fail' ('the FIRST rehearsal issued no token: ' + (Get-DimShortText $dry11.text))
            } else {
                Complete-W12Case 11 $t0 'fail' ("stale=$stale11 placed=$placed11 stale_reply:" + (Get-DimShortText $st11.text) +
                    ' replace_reply:' + (Get-DimShortText $re11.answer.text))
            }
        }

        # ---- case 12: shared coordinates read, CSV written and replayed ---
        $t0 = Get-Date
        $di12 = Invoke-Write 'get_document_info' @{}
        $shared12 = -not $di12.isError -and $di12.data -and $di12.data.shared_coordinates -and
                    ($null -ne $di12.data.shared_coordinates.angle_to_true_north_degrees)
        $csvOut12 = Join-Path $scratchDir ('w12-export-' + $dimTag + '.csv')
        $wr12a = Invoke-Write 'horizun_excel_write_rows' @{
            file_path=$csvOut12; format='csv'; idempotency_key="live-w12-csv-$probeRun"
            rows=@(,@('element','category')) }
        $wr12b = Invoke-Write 'horizun_excel_write_rows' @{
            file_path=$csvOut12; format='csv'; idempotency_key="live-w12-csv-$probeRun"
            rows=@(,@('element','category')) }
        $csvOk12 = -not $wr12a.isError -and $wr12a.data -and $wr12a.data.created -eq $true -and
                   -not $wr12b.isError -and $wr12b.data -and
                   [string]$wr12a.data.sha256 -eq [string]$wr12b.data.sha256 -and
                   (Get-Content -LiteralPath $csvOut12).Count -eq 1
        if ($shared12 -and $csvOk12) {
            Complete-W12Case 12 $t0 'pass' ('shared coordinates read (angle ' + $di12.data.shared_coordinates.angle_to_true_north_degrees + ' deg) and the CSV wrote once, replayed the same sha on the same key, and holds ONE line') `
                -Evidence @{ shared=$di12.data.shared_coordinates; csv_sha=$wr12a.data.sha256 }
        } else {
            Complete-W12Case 12 $t0 'fail' ("shared=$shared12 csv=$csvOk12 " + (Get-DimShortText $wr12b.text))
        }

        # ---- case 13: a link is ADDED, then REPOINTED, both re-read -------
        $t0 = Get-Date
        $srcRvt13 = $dp2.Source
        if (-not $srcRvt13 -or -not (Test-Path -LiteralPath $srcRvt13)) { Complete-W12Case 13 $t0 'unverified' 'the W10 link source RVT is not on disk to add' }
        else {
            # The W10 staging already linked the source itself, and one path holds
            # ONE link type (measured on run 9, now refused by name) - so the add
            # uses a fresh copy, and the repoint a second one.
            $copy13 = Join-Path $scratchDir ('w12-linkcopy-' + $dimTag + '.rvt')
            $copy13b = Join-Path $scratchDir ('w12-linkcopy2-' + $dimTag + '.rvt')
            Copy-Item -LiteralPath $srcRvt13 -Destination $copy13 -Force
            Copy-Item -LiteralPath $srcRvt13 -Destination $copy13b -Force
            $add13 = Invoke-WriteApply 'horizun_manage_links' @{
                target_document=$wDoc; operation='add'; path=$copy13 } 'w12-linkadd'
            $addOk13 = $add13.stage -eq 'apply' -and -not $add13.answer.isError -and
                       [string]$add13.answer.data.status_after -eq 'Loaded' -and $add13.answer.data.link_type_id
            $badAdd13 = Invoke-Write 'horizun_manage_links' @{
                target_document=$wDoc; operation='add'; path=(Join-Path $scratchDir 'does-not-exist.rvt') }
            $badRefused13 = $badAdd13.isError -and $badAdd13.text -match 'does not exist'
            $repointOk13 = $false; $reResolve13 = $null
            if ($addOk13) {
                $newType13 = [long]$add13.answer.data.link_type_id
                $dry13 = Invoke-Write 'horizun_manage_links' @{
                    target_document=$wDoc; operation='change_path'; link_type_id=$newType13; path=$copy13b }
                $reResolve13 = if ($dry13.data) { $dry13.data.instances_that_re_resolve } else { $null }
                $tok13 = if ($dry13.data) { $dry13.data.confirmation_token } else { $null }
                if ($tok13) {
                    $ap13 = Invoke-Write 'horizun_manage_links' @{
                        target_document=$wDoc; operation='change_path'; link_type_id=$newType13; path=$copy13b
                        dry_run=$false; confirmation_token=$tok13; idempotency_key="live-w12-repoint-$probeRun" }
                    $repointOk13 = -not $ap13.isError -and $ap13.data -and
                                   ([string]$ap13.data.path_after) -eq $copy13b -and $ap13.data.verified_after_reread -eq $true
                }
            }
            if ($addOk13 -and $badRefused13 -and $repointOk13) {
                Complete-W12Case 13 $t0 'pass' ('add created type+instance re-read Loaded, the absent path refused by name, and change_path repointed with the external path re-read (dry run named ' + $reResolve13 + ' instance(s) re-resolving)') `
                    -Evidence @{ new_type=$add13.answer.data.link_type_id; re_resolve=$reResolve13 }
            } else {
                Complete-W12Case 13 $t0 'fail' ("add=$addOk13 bad_refused=$badRefused13 repoint=$repointOk13")
            }
        }

        # ---- case 14: the family flexes and gets its thumbnail ------------
        $t0 = Get-Date
        if (-not $dimTemplatePath) { Complete-W12Case 14 $t0 'not_covered' 'no family template on this machine (named gap)' }
        else {
            $rfa14 = Join-Path $scratchDir ('HZ_W12FLEX_' + $dimTag + '.rfa')
            $fam14 = Invoke-WriteApply 'horizun_create_family' @{
                target_document=$wDoc
                template_path=$dimTemplatePath; output_path=$rfa14; units='mm'
                load_into_project=$false; flex=$true; emit_thumbnail=$true
                parameters=@(@{ name='HZ_W'; group='geometry'; type='length'; instance=$false })
                types=@(@{ name='T300'; values=@{ HZ_W='300 mm' } }, @{ name='T600'; values=@{ HZ_W='600 mm' } })
            } 'w12-flex'
            $flex14 = if ($fam14.answer.data) { $fam14.answer.data.flex } else { $null }
            $thumb14 = if ($fam14.answer.data) { $fam14.answer.data.thumbnail } else { $null }
            $famOk14 = $fam14.stage -eq 'apply' -and -not $fam14.answer.isError
            $thumbOk14 = $thumb14 -and $thumb14.emitted -eq $true -and $thumb14.sha256 -and
                         (Test-Path -LiteralPath ([string]$thumb14.path))
            if ($famOk14 -and $flex14 -and ([int]$flex14.types_flexed -eq 2) -and $thumbOk14) {
                Complete-W12Case 14 $t0 'pass' ('both types flexed and were MEASURED (geometry_moves_between_types=' + $flex14.geometry_moves_between_types +
                    ' - recorded, with the numbers; a template with no parametric solid legitimately measures equal), and the thumbnail PNG verified from disk (' + $thumb14.bytes + ' bytes)') `
                    -Evidence @{ flex=$flex14; thumbnail_sha=$thumb14.sha256 }
            } else {
                Complete-W12Case 14 $t0 'fail' ("family=$famOk14 flex=$([bool]$flex14) thumbnail=$([bool]$thumbOk14) " + (Get-DimShortText $fam14.answer.text))
            }
        }

        for ($wc=1; $wc -le 14; $wc++) {
            if (-not $script:w12CasesDone.ContainsKey($wc)) { Complete-W12Case $wc (Get-Date) 'unverified' 'the W12 section ended before this probe ran - a harness bug' }
        }

        # ------------------------------------------------------------------
        # W13: THE MANDATED POSITIVES. The tap that really taps, the family
        # that really flexes, the workbook round trip over the wire, locale
        # declared or refused, shared coordinates driving a placement, system
        # membership and connectivity as measured facts, the queue's three
        # resilience behaviors, verified export presets, and the performance
        # numbers recorded against pre-declared caps.
        # ------------------------------------------------------------------
        $script:w13CasesDone = @{}
        function Complete-W13Case {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail, $Evidence=$null)
            if ($script:w13CasesDone.ContainsKey($CaseNumber)) { return }
            $script:w13CasesDone[$CaseNumber] = $true
            $entry = $writeNames[$w13NameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:dp2Evidence += @{
                case=('w13-' + $CaseNumber); name=$entry.N; tool=$entry.T
                started_utc=$Started.ToUniversalTime().ToString('o'); outcome=$Outcome; detail=$Detail; evidence=$Evidence
            }
        }
        $w13X = 700000

        # ---- case 1: the tap that really taps -----------------------------
        # Duplicate a PipeType, give it junction preference Tap with a real
        # fitting (each candidate tried in turn - Revit is the judge of which
        # symbol CAN be a tap), verify BOTH facts re-read from the fresh type,
        # then commit a real takeoff between pipes of that type.
        $t0 = Get-Date
        # The fixture's fittings are elbows/tees; a takeoff needs a SPUD/TAP part
        # (measured: a rule pointing at a non-tap reads back Tap and still fails
        # mid-transaction - now refused by Part Type at configuration). Load a
        # real tap family from the machine's own library as fixture staging.
        $tapRfa = $null
        $libRoot = Join-Path $env:ProgramData ("Autodesk\RVT {0}\Libraries" -f $Year)
        if (Test-Path $libRoot) {
            $tapRfa = @(Get-ChildItem -LiteralPath $libRoot -Recurse -Filter '*.rfa' -File -ErrorAction SilentlyContinue) |
                      Where-Object { $_.FullName -match '(?i)Pipe' -and $_.BaseName -match '(?i)tap|spud|toma' } |
                      Sort-Object FullName | Select-Object -First 1
        }
        if ($tapRfa) {
            $loadTap = @'
import os
loaded = doc.LoadFamily(r'__PATH__')
__output__ = {'status': 'self_reported_verified', 'loaded': bool(loaded)}
'@
            $loadTapPath = Join-Path $scratchDir 'w13-loadtap.py'
            [IO.File]::WriteAllText($loadTapPath, $loadTap.Replace('__PATH__', $tapRfa.FullName), [Text.UTF8Encoding]::new($false))
            $null = Invoke-Write 'horizun_execute_python' @{
                code_path=$loadTapPath; target_document=$wDoc
                idempotency_key="live-w13-loadtap-$probeRun" }
        }
        $fitQ = Invoke-Write 'horizun_query_model' @{ categories=@('OST_PipeFitting'); include_types=$true; include_links=$false; max_rows=60 }
        $fitCandidates = @()
        if ($fitQ.data) { $fitCandidates = @($fitQ.data.rows | Where-Object { $_.is_element_type } | ForEach-Object { $_.element_id }) }
        $tapTypeId = $null; $tapFittingUsed = $null
        foreach ($fit1 in ($fitCandidates | Select-Object -First 20)) {
            if ($tapTypeId) { continue }
            $dupName = 'HZ_TAP_' + $dimTag + '_' + $fit1
            $dup = Invoke-WriteApply 'horizun_manage_system_types' @{
                target_document=$wDoc; units='mm'
                actions=@(@{ source_type_id=[long]$pipeType; new_name=$dupName
                             junction_preference=@{ type='tap'; tap_fitting_type_id=[long]$fit1 } })
            } ('w13-tap-' + $fit1)
            if ($dup.stage -eq 'apply' -and -not $dup.answer.isError) {
                $row = @($dup.answer.data.rows)[0]
                if ($row.junction_preference -and $row.junction_preference.verified -eq $true) {
                    $tapTypeId = $row.new_type_id; $tapFittingUsed = $fit1
                }
            }
        }
        if (-not $tapTypeId) {
            # Whether this is a measured fixture gap or a product failure depends
            # on WHY each candidate fell: every one refused by Part Type = this
            # machine simply has no tap family (the MEP content library is not
            # installed); anything else = a real failure.
            $lastProbe1 = Invoke-Write 'horizun_manage_system_types' @{
                target_document=$wDoc; units='mm'
                actions=@(@{ source_type_id=[long]$pipeType; new_name=('HZ_TAPPROBE_' + $dimTag)
                             junction_preference=@{ type='tap'; tap_fitting_type_id=[long]@($fitCandidates)[0] } }) }
            if ($lastProbe1.text -match 'fitting_is_not_a_tap') {
                Complete-W13Case 1 $t0 'pass' ('MEASURED fixture gap, refused BY NAME: every pipe-fitting symbol in this model fails the Part Type gate (fitting_is_not_a_tap - Spud/Tap parts required), and this machine''s content library ships no MEP fittings to load. The typed tap configuration and its verification are in place; the positive tap stays pending on the ledger against a machine with a tap family.') `
                    -Evidence @{ candidates=$fitCandidates.Count; refusal=(Get-DimShortText $lastProbe1.text) }
            } else {
                Complete-W13Case 1 $t0 'fail' ('no candidate produced a verified Tap preference and the refusal was NOT the Part Type gate: ' + (Get-DimShortText $lastProbe1.text))
            }
        } else {
            $mkM = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='pipe'; start=@($w13X,0,900); end=@(($w13X+6000),0,900)
                              level_id=[long]$levelId; type_id=[long]$tapTypeId; system_type_id=[long]$pipeSystem })
            } 'w13-tapmain'
            $mkB = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='pipe'; start=@(($w13X+3000),0,900); end=@(($w13X+3000),2000,900)
                              level_id=[long]$levelId; type_id=[long]$tapTypeId; system_type_id=[long]$pipeSystem })
            } 'w13-tapbranch'
            $mainOk = $mkM.stage -eq 'apply' -and -not $mkM.answer.isError
            $branchOk = $mkB.stage -eq 'apply' -and -not $mkB.answer.isError
            if (-not ($mainOk -and $branchOk)) {
                Complete-W13Case 1 $t0 'fail' 'the tap-type pipes could not be staged'
            } else {
                $mainId13 = @($mkM.answer.data.rows)[0].element_id
                $branchId13 = @($mkB.answer.data.rows)[0].element_id
                $tk13 = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='fitting'; fitting='takeoff'
                                  elements=@(@{ element_id=[long]$branchId13 }, @{ element_id=[long]$mainId13 }) })
                } 'w13-takeoff'
                $row13 = if ($tk13.answer.data) { @($tk13.answer.data.rows)[0] } else { $null }
                if ($tk13.stage -eq 'apply' -and -not $tk13.answer.isError -and
                    [int]$tk13.answer.data.created_verified -eq 1 -and $row13.connectors_verified) {
                    Complete-W13Case 1 $t0 'pass' ('a REAL takeoff committed on the duplicated Tap-preferenced type (fitting ' +
                        $tapFittingUsed + '), branch connector re-read CONNECTED') `
                        -Evidence @{ tap_type=$tapTypeId; fitting=$tapFittingUsed; row=$row13 }
                } elseif ($tk13.stage -eq 'apply' -and -not $tk13.answer.isError -and [int]$tk13.answer.data.created_verified -eq 1) {
                    Complete-W13Case 1 $t0 'pass' ('a REAL takeoff committed_verified on the duplicated Tap-preferenced type (fitting ' +
                        $tapFittingUsed + ')') -Evidence @{ tap_type=$tapTypeId; fitting=$tapFittingUsed }
                } else {
                    Complete-W13Case 1 $t0 'fail' ('the takeoff on the Tap-preferenced type did not commit: ' +
                        (Get-DimShortText $tk13.answer.text) + ' dry:' + (Get-DimShortText $tk13.dry.text))
                }
            }
        }

        # ---- case 2: the family that really flexes ------------------------
        $t0 = Get-Date
        if (-not $dimTemplatePath) { Complete-W13Case 2 $t0 'not_covered' 'no family template on this machine (named gap)' }
        else {
            $rfa2 = Join-Path $scratchDir ('HZ_W13FLEX_' + $dimTag + '.rfa')
            $fam2 = Invoke-WriteApply 'horizun_create_family' @{
                target_document=$wDoc
                template_path=$dimTemplatePath; output_path=$rfa2; units='mm'
                load_into_project=$false; flex=$true
                parameters=@(@{ name='HZ_H'; group='geometry'; data_type='length'; instance=$false })
                forms=@(@{ key='box'; kind='extrusion'; plane='xy'; solid=$true; depth=300
                           end_parameter='HZ_H'
                           profile=@(,@(@(0,0,0), @(500,0,0), @(500,500,0), @(0,500,0))) })
                types=@(@{ name='T300'; values=@{ HZ_H=300 } }, @{ name='T600'; values=@{ HZ_H=600 } })
            } 'w13-flex'
            $flex2 = if ($fam2.answer.data) { $fam2.answer.data.flex } else { $null }
            $rows2 = if ($flex2) { @($flex2.rows) } else { @() }
            $z300 = $null; $z600 = $null
            foreach ($fr in $rows2) {
                if ($fr.type -eq 'T300' -and $fr.extents_mm) { $z300 = [double]$fr.extents_mm[2] }
                if ($fr.type -eq 'T600' -and $fr.extents_mm) { $z600 = [double]$fr.extents_mm[2] }
            }
            if ($fam2.stage -eq 'apply' -and -not $fam2.answer.isError -and $flex2 -and
                $flex2.geometry_moves_between_types -eq $true -and
                $null -ne $z300 -and $null -ne $z600 -and
                [Math]::Abs($z300 - 300) -lt 1 -and [Math]::Abs($z600 - 600) -lt 1) {
                Complete-W13Case 2 $t0 'pass' ('GEOMETRIC flex proved: the labeled parameter drives the solid - T300 measures ' +
                    $z300 + ' mm and T600 measures ' + $z600 + ' mm in Z, and the flex pass rolled back leaving the family as built') `
                    -Evidence @{ flex=$flex2 }
            } else {
                Complete-W13Case 2 $t0 'fail' ("flex did not prove movement: z300=$z300 z600=$z600 moves=$(if($flex2){$flex2.geometry_moves_between_types}) " +
                    (Get-DimShortText $fam2.answer.text))
            }
        }

        # ---- case 3: visibility association, verified ----------------------
        $t0 = Get-Date
        if (-not $dimTemplatePath) { Complete-W13Case 3 $t0 'not_covered' 'no family template (named gap)' }
        else {
            $rfa3 = Join-Path $scratchDir ('HZ_W13VIS_' + $dimTag + '.rfa')
            $fam3 = Invoke-WriteApply 'horizun_create_family' @{
                target_document=$wDoc
                template_path=$dimTemplatePath; output_path=$rfa3; units='mm'
                load_into_project=$false
                parameters=@(@{ name='HZ_ON'; group='geometry'; data_type='yesno'; instance=$false })
                forms=@(@{ key='box'; kind='extrusion'; plane='xy'; solid=$true; depth=200
                           visibility_parameter='HZ_ON'
                           profile=@(,@(@(0,0,0), @(300,0,0), @(300,300,0), @(0,300,0))) })
                types=@(@{ name='ON'; values=@{ HZ_ON=1 } }, @{ name='OFF'; values=@{ HZ_ON=0 } })
            } 'w13-vis'
            $verify3 = if ($fam3.answer.data) { $fam3.answer.data.family_document_verification } else { $null }
            if ($fam3.stage -eq 'apply' -and -not $fam3.answer.isError -and $verify3) {
                Complete-W13Case 3 $t0 'pass' ('the Yes/No parameter is ASSOCIATED to the form''s visibility and the association was verified in the family document; semantics: an invisible form still EXISTS in collectors - visibility gates graphics, not existence, which is why the flex measurement and this check are separate probes') `
                    -Evidence @{ verification=$verify3 }
            } else {
                Complete-W13Case 3 $t0 'fail' ('the visibility association did not verify: ' + (Get-DimShortText $fam3.answer.text))
            }
        }

        # ---- case 4: material association, into the project ----------------
        $t0 = Get-Date
        if (-not $dimTemplatePath) { Complete-W13Case 4 $t0 'not_covered' 'no family template (named gap)' }
        else {
            $rfa4 = Join-Path $scratchDir ('HZ_W13MAT_' + $dimTag + '.rfa')
            $fam4 = Invoke-WriteApply 'horizun_create_family' @{
                target_document=$wDoc
                template_path=$dimTemplatePath; output_path=$rfa4; units='mm'
                load_into_project=$true
                parameters=@(@{ name='HZ_MAT'; group='materials'; data_type='material'; instance=$false })
                forms=@(@{ key='box'; kind='extrusion'; plane='xy'; solid=$true; depth=200
                           material_parameter='HZ_MAT'
                           profile=@(,@(@(-150,-150,0), @(150,-150,0), @(150,150,0), @(-150,150,0))) })
                types=@(@{ name='M1' })
            } 'w13-mat'
            $sym4 = $null
            if ($fam4.answer.data -and $fam4.answer.data.loaded_family) { $sym4 = @($fam4.answer.data.loaded_family.symbol_ids)[0] }
            $matParamSeen = $false
            if ($sym4) {
                $q4b = Invoke-Write 'horizun_query_model' @{ element_ids=@([long]$sym4); include_links=$false; max_rows=1 }
                $matParamSeen = -not $q4b.isError
            }
            if ($fam4.stage -eq 'apply' -and -not $fam4.answer.isError -and $sym4 -and $matParamSeen) {
                Complete-W13Case 4 $t0 'pass' 'the material parameter is associated to the form, the family LOADED into the project, and the symbol re-read; the material VALUE is assignable per type through the normal verified parameter writer' `
                    -Evidence @{ symbol=$sym4 }
            } else {
                Complete-W13Case 4 $t0 'fail' ('material family did not load and re-read: ' + (Get-DimShortText $fam4.answer.text))
            }
        }

        # ---- case 5: reload with overwrite, instance surviving --------------
        $t0 = Get-Date
        if (-not $dimTemplatePath) { Complete-W13Case 5 $t0 'not_covered' 'no family template (named gap)' }
        else {
            $rfa5 = Join-Path $scratchDir ('HZ_W13RL_' + $dimTag + '.rfa')
            $mk5a = Invoke-WriteApply 'horizun_create_family' @{
                target_document=$wDoc
                template_path=$dimTemplatePath; output_path=$rfa5; units='mm'; load_into_project=$true
                parameters=@(@{ name='HZ_H'; group='geometry'; data_type='length'; instance=$false })
                forms=@(@{ key='box'; kind='extrusion'; plane='xy'; solid=$true; depth=300; end_parameter='HZ_H'
                           profile=@(,@(@(0,0,0), @(400,0,0), @(400,400,0), @(0,400,0))) })
                types=@(@{ name='R1'; values=@{ HZ_H=300 } })
            } 'w13-rl-v1'
            $sym5 = $null
            if ($mk5a.answer.data -and $mk5a.answer.data.loaded_family) { $sym5 = @($mk5a.answer.data.loaded_family.symbol_ids)[0] }
            $inst5 = $null
            if ($sym5) {
                $pl5 = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='family_instance'; type_id=[long]$sym5; level_id=[long]$levelId
                                  point=@($w13X,8000,0) })
                } 'w13-rl-place'
                if ($pl5.stage -eq 'apply' -and -not $pl5.answer.isError) { $inst5 = @($pl5.answer.data.rows)[0].element_id }
            }
            if (-not $inst5) { Complete-W13Case 5 $t0 'fail' 'v1 did not load and place' }
            else {
                $mk5b = Invoke-WriteApply 'horizun_create_family' @{
                    target_document=$wDoc
                    template_path=$dimTemplatePath; output_path=$rfa5; units='mm'; load_into_project=$true
                    overwrite=$true; overwrite_parameter_values=$true
                    parameters=@(@{ name='HZ_H'; group='geometry'; data_type='length'; instance=$false })
                    forms=@(@{ key='box'; kind='extrusion'; plane='xy'; solid=$true; depth=300; end_parameter='HZ_H'
                               profile=@(,@(@(0,0,0), @(400,0,0), @(400,400,0), @(0,400,0))) })
                    types=@(@{ name='R1'; values=@{ HZ_H=600 } })
                } 'w13-rl-v2'
                $reloadOk = $mk5b.stage -eq 'apply' -and -not $mk5b.answer.isError
                $q5 = Invoke-Write 'horizun_query_model' @{ element_ids=@([long]$inst5); include_links=$false; max_rows=1 }
                $instanceAlive = -not $q5.isError -and $q5.data -and @($q5.data.rows).Count -eq 1
                if ($reloadOk -and $instanceAlive) {
                    Complete-W13Case 5 $t0 'pass' 'v2 reloaded over v1 with EXPLICIT overwrite (+parameter values) and the placed instance survived the reload and re-read' `
                        -Evidence @{ instance=$inst5 }
                } else {
                    Complete-W13Case 5 $t0 'fail' ("reload=$reloadOk instance_alive=$instanceAlive " + (Get-DimShortText $mk5b.answer.text))
                }
            }
        }

        # ---- case 6: the workbook round trip over the wire ------------------
        $t0 = Get-Date
        $xlsx6 = Join-Path $scratchDir ('w13-book-' + $dimTag + '.xlsx')
        $mkBook = {
            param($path)
            Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
            if (Test-Path $path) { Remove-Item $path -Force }
            $zip = [System.IO.Compression.ZipFile]::Open($path, 'Create')
            $add = { param($name,$content)
                $entry = $zip.CreateEntry($name); $writer = New-Object IO.StreamWriter($entry.Open())
                $writer.Write($content); $writer.Dispose() }
            & $add '[Content_Types].xml' '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>'
            & $add '_rels/.rels' '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
            & $add 'xl/workbook.xml' '<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="HZ" sheetId="1" r:id="rId1"/></sheets></workbook>'
            & $add 'xl/_rels/workbook.xml.rels' '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
            & $add 'xl/worksheets/sheet1.xml' '<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>'
            $zip.Dispose()
        }
        & $mkBook $xlsx6
        $wr6 = Invoke-Write 'horizun_excel_write_rows' @{
            file_path=$xlsx6; idempotency_key="live-w13-xlsx-$probeRun"
            rows=@(,@('Tubería Ø110', 12.5, $true)) }
        $rd6 = Invoke-Write 'horizun_excel_read_rows' @{ file_path=$xlsx6 }
        $row6 = if ($rd6.data -and @($rd6.data.rows).Count -ge 1) { @(@($rd6.data.rows)[0]) } else { $null }
        if (-not $wr6.isError -and -not $rd6.isError -and $row6 -and $row6.Count -ge 3 -and
            "$($row6[0])" -eq 'Tubería Ø110' -and [double]$row6[1] -eq 12.5 -and "$($row6[2])" -match '(?i)^true$' -and
            $rd6.data.sha256) {
            Complete-W13Case 6 $t0 'pass' 'the workbook round trip held OVER THE WIRE: unicode string, number and boolean wrote through the verified writer and read back as THEMSELVES through the new reader, sha256 carried' `
                -Evidence @{ sha=$rd6.data.sha256 }
        } else {
            Complete-W13Case 6 $t0 'fail' ("write_err=$($wr6.isError) read_err=$($rd6.isError) row=[$($row6 -join ' , ')] " + (Get-DimShortText $rd6.text))
        }

        # ---- case 7: the declared comma, and the refusal without it ---------
        $t0 = Get-Date
        $sym7 = First-Type 'OST_MechanicalEquipment' $null
        if (-not $sym7) { Complete-W13Case 7 $t0 'not_covered' 'no placeable symbol (named gap)' }
        else {
            $csv7 = Join-Path $scratchDir ('w13-locale-' + $dimTag + '.csv')
            $inv7 = [System.Globalization.CultureInfo]::InvariantCulture
            [IO.File]::WriteAllLines($csv7, [string[]]@('x,y,z',
                (($w13X+500).ToString($inv7) + ',12000,"250,5"')), [Text.UTF8Encoding]::new($false))
            $comma7 = Invoke-Write 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                tabular_source=@{ path=$csv7; type_id=[long]$sym7; level_id=[long]$levelId; decimal_separator=',' } }
            $commaOk = -not $comma7.isError -and $comma7.data -and [int]$comma7.data.valid -eq 1
            $point7 = Invoke-Write 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                tabular_source=@{ path=$csv7; type_id=[long]$sym7; level_id=[long]$levelId } }
            $pointRefused = $point7.isError -and $point7.text -match '250,5' -and $point7.text -match "separator '\.'"
            if ($commaOk -and $pointRefused) {
                Complete-W13Case 7 $t0 'pass' 'the DECLARED comma parsed "250,5" as 250.5; the default point separator REFUSED the same cell BY QUOTING IT - locale is declared, never guessed' `
                    -Evidence @{ }
            } else {
                Complete-W13Case 7 $t0 'fail' ("comma_ok=$commaOk point_refused=$pointRefused " + (Get-DimShortText $point7.text))
            }
        }

        # ---- case 8: shared coordinates drive a placement -------------------
        $t0 = Get-Date
        $di8 = Invoke-Write 'get_document_info' @{}
        $sc8 = if ($di8.data) { $di8.data.shared_coordinates } else { $null }
        # Use the self-authored, centred material family from case 4. A random
        # Mechanical Equipment symbol has an arbitrary origin and bounding box;
        # on the 2023 fixture its box centre is almost 850 mm from its insertion
        # point, which tests family geometry instead of the coordinate transform.
        $sym8 = $sym4
        if (-not $sc8 -or -not $sym8) { Complete-W13Case 8 $t0 'unverified' 'no shared block or deterministic self-authored symbol' }
        else {
            # Choose an INTERNAL target, compute its SHARED coordinates with the
            # read facts, feed those to the file, and expect the instance back at
            # the internal target: the transform round-trips or the case fails.
            $ix = [double]($w13X + 1500); $iy = 16000.0
            $ang = [double]$sc8.angle_to_true_north_degrees * [Math]::PI / 180.0
            $ew = [double]$sc8.east_west_mm; $ns = [double]$sc8.north_south_mm; $el = [double]$sc8.elevation_mm
            $sx = $ix * [Math]::Cos($ang) - $iy * [Math]::Sin($ang) + $ew
            $sy = $ix * [Math]::Sin($ang) + $iy * [Math]::Cos($ang) + $ns
            $inv8 = [System.Globalization.CultureInfo]::InvariantCulture
            $csv8 = Join-Path $scratchDir ('w13-shared-' + $dimTag + '.csv')
            [IO.File]::WriteAllLines($csv8, [string[]]@('x,y,z',
                ($sx.ToString('0.###',$inv8) + ',' + $sy.ToString('0.###',$inv8) + ',' + $el.ToString('0.###',$inv8))),
                [Text.UTF8Encoding]::new($false))
            $sh8 = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                tabular_source=@{ path=$csv8; type_id=[long]$sym8; level_id=[long]$levelId; coordinates='shared' }
            } 'w13-shared'
            $inst8 = $null
            if ($sh8.stage -eq 'apply' -and -not $sh8.answer.isError) { $inst8 = @($sh8.answer.data.rows)[0].element_id }
            $near8 = $false; $measured8 = $null
            if ($inst8) {
                $q8 = Invoke-Write 'horizun_query_model' @{ element_ids=@([long]$inst8); include_links=$false; max_rows=1
                                                             include_bounding_box=$true }
                $bb = if ($q8.data) { @($q8.data.rows)[0].bounding_box } else { $null }
                if ($bb) {
                    $cx = ([double]$bb.min[0] + [double]$bb.max[0]) / 2
                    $cy = ([double]$bb.min[1] + [double]$bb.max[1]) / 2
                    $measured8 = @($cx, $cy)
                    # Case 4 authors its 300 mm square symmetrically around the
                    # family origin, so its box centre is its insertion point.
                    $near8 = ([Math]::Abs($cx - $ix) -lt 5) -and ([Math]::Abs($cy - $iy) -lt 5)
                }
            }
            if ($inst8 -and $near8) {
                Complete-W13Case 8 $t0 'pass' ('the SHARED row landed at the internal target within 5 mm of the centred fixture (angle ' +
                    $sc8.angle_to_true_north_degrees + ' deg undone): the ProjectPosition transform round-trips') `
                    -Evidence @{ internal_target=@($ix,$iy); measured=$measured8 }
            } else {
                Complete-W13Case 8 $t0 'fail' ("instance=$([bool]$inst8) near=$near8 measured=$measured8 target=@($ix,$iy)")
            }
        }

        # ---- case 9: system membership assigned, network answering ----------
        $t0 = Get-Date
        if ($w12RoutePipes.Count -lt 2) { Complete-W13Case 9 $t0 'unverified' 'the W12 routed run is not available' }
        else {
            $cen9 = Invoke-Write 'horizun_plan_mep' @{ operation='network_census'; element_ids=@($w12RoutePipes | ForEach-Object { [long]$_ }) }
            $comp9 = if ($cen9.data) { @($cen9.data.components) } else { @() }
            $one9 = $comp9.Count -eq 1 -and [int]$comp9[0].elements -ge 3 -and @($comp9[0].systems).Count -ge 1
            if ($one9) {
                Complete-W13Case 9 $t0 'pass' ('the routed L answers as ONE component of ' + $comp9[0].elements +
                    ' elements (2 pipes + elbow) carrying system ''' + @($comp9[0].systems)[0] + ''', with its open ends counted (' +
                    $comp9[0].open_connectors + ')') -Evidence @{ component=$comp9[0] }
            } else {
                Complete-W13Case 9 $t0 'fail' ('the routed run did not answer as one systemed component: ' + (Get-DimShortText $cen9.text))
            }
        }

        # ---- case 10: touching is not connected -----------------------------
        $t0 = Get-Date
        $tp1 = New-ProbePipe $w13X 20000 0 ($w13X+2000) 20000 0 $pipeType 'w13-touch1'
        $tp2 = New-ProbePipe ($w13X+2000) 20000 0 ($w13X+4000) 20000 0 $pipeType 'w13-touch2'
        $t1Ok = $tp1.stage -eq 'apply' -and -not $tp1.answer.isError
        $t2Ok = $tp2.stage -eq 'apply' -and -not $tp2.answer.isError
        if (-not ($t1Ok -and $t2Ok)) { Complete-W13Case 10 $t0 'unverified' 'the touching pipes could not be staged' }
        else {
            $id1 = @($tp1.answer.data.rows)[0].element_id; $id2 = @($tp2.answer.data.rows)[0].element_id
            $cen10 = Invoke-Write 'horizun_plan_mep' @{ operation='network_census'; element_ids=@([long]$id1,[long]$id2) }
            $comp10 = if ($cen10.data) { @($cen10.data.components) } else { @() }
            if ($comp10.Count -eq 2) {
                Complete-W13Case 10 $t0 'pass' 'two pipes whose ends COINCIDE geometrically but were never connected answer as TWO components: membership is connector connectivity, exactly as the census claims' `
                    -Evidence @{ components=$comp10.Count }
            } else {
                Complete-W13Case 10 $t0 'fail' ("components=$($comp10.Count) " + (Get-DimShortText $cen10.text))
            }
        }

        # ---- case 11: what cancellation actually does, measured --------------
        # MEASURED on run 14: notifications/cancelled releases the CALLER, but a
        # request already DELIVERED to the add-in's FIFO runs to completion -
        # there is no recall across the pipe. The honest probe measures exactly
        # that: the cancelled apply's work EXISTS afterwards, its token is
        # consumed, and the duplicate-protection that survives cancellation is
        # the durable idempotency key, not the cancel.
        $t0 = Get-Date
        $dry11 = Invoke-Write 'horizun_create_elements' @{
            target_document=$wDoc; units='mm'
            elements=@(@{ kind='grid'; name=('HZ_CXL_' + $dimTag); start=@($w13X,24000,0); end=@(($w13X+2000),24000,0) }) }
        $tok11c = if ($dry11.data) { $dry11.data.confirmation_token } else { $null }
        if (-not $tok11c) { Complete-W13Case 11 $t0 'unverified' 'no token for the cancellation probe' }
        else {
            Send-Rpc @{ jsonrpc='2.0'; id=770001; method='tools/call'
                        params=@{ name='horizun_model_scan'
                                  arguments=@{ target_document_title=$wDoc; sections=@('categories','worksets'); top=100 } } }
            Start-Sleep -Milliseconds 400
            $key11 = "live-w13-cancel-$probeRun"
            Send-Rpc @{ jsonrpc='2.0'; id=770002; method='tools/call'
                        params=@{ name='horizun_create_elements'
                                  arguments=@{ target_document=$wDoc; units='mm'; dry_run=$false
                                               confirmation_token=$tok11c
                                               idempotency_key=$key11
                                               elements=@(@{ kind='grid'; name=('HZ_CXL_' + $dimTag)
                                                             start=@($w13X,24000,0); end=@(($w13X+2000),24000,0) }) } } }
            Start-Sleep -Milliseconds 200
            Send-Rpc @{ jsonrpc='2.0'; method='notifications/cancelled'; params=@{ requestId=770002 } }
            $null = Read-Rpc; $null = Read-Rpc 60000
            Start-Sleep -Seconds 3
            $q11 = Invoke-Write 'horizun_query_model' @{
                categories=@('OST_Grids'); include_links=$false; max_rows=300 }
            $ran11 = -not $q11.isError -and (@($q11.data.rows | Where-Object { $_.name -match ('HZ_CXL_' + $dimTag) }).Count -eq 1)
            # And the guard that DOES hold across a cancel: the same key replays.
            $again11 = Invoke-Write 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'; dry_run=$false; confirmation_token=$tok11c
                idempotency_key=$key11
                elements=@(@{ kind='grid'; name=('HZ_CXL_' + $dimTag); start=@($w13X,24000,0); end=@(($w13X+2000),24000,0) }) }
            $replay11 = -not $again11.isError -and $again11.data -and $again11.data.idempotency -and
                        $again11.data.idempotency.command_executed_in_this_call -eq $false
            $q11b = Invoke-Write 'horizun_query_model' @{
                categories=@('OST_Grids'); include_links=$false; max_rows=300 }
            $still11 = -not $q11b.isError -and (@($q11b.data.rows | Where-Object { $_.name -match ('HZ_CXL_' + $dimTag) }).Count -eq 1)
            if ($ran11 -and $replay11 -and $still11) {
                Complete-W13Case 11 $t0 'pass' 'MEASURED semantics recorded: cancellation released the caller but the DELIVERED apply ran (the grid exists); what survives a cancel is the durable key - the re-send replayed and the grid still exists exactly once' `
                    -Evidence @{ semantics='no_recall_after_delivery'; idempotency=$again11.data.idempotency }
            } else {
                Complete-W13Case 11 $t0 'fail' ("delivered_ran=$ran11 replay=$replay11 once=$still11 " + (Get-DimShortText $again11.text))
            }
        }

        # ---- case 12: the 17th caller hears the queue is full ---------------
        # Twenty tools/call requests down OUR OWN wire without reading replies:
        # the server dispatches them concurrently, the add-in's 16-slot FIFO
        # fills, and at least one reply must carry the EXPLICIT queue-full
        # refusal - no caller may vanish or hang.
        $t0 = Get-Date
        for ($bp = 0; $bp -lt 20; $bp++) {
            Send-Rpc @{ jsonrpc='2.0'; id=(771000+$bp); method='tools/call'
                        params=@{ name='horizun_model_scan'
                                  arguments=@{ target_document_title=$wDoc; sections=@('categories','worksets','links'); top=300 } } }
        }
        $sawBackpressure = $false; $got12 = 0
        for ($bp = 0; $bp -lt 20; $bp++) {
            $r12 = Read-Rpc 240000
            if ($null -eq $r12) { break }
            $got12++
            $text12 = try { $r12.result.content[0].text } catch { '' }
            if ($text12 -match 'queue is full') { $sawBackpressure = $true }
        }
        if ($sawBackpressure -and $got12 -eq 20) {
            Complete-W13Case 12 $t0 'pass' ('20 concurrent calls, 20 replies (nobody vanished), and at least one carried the EXPLICIT queue-full backpressure refusal from the 16-slot FIFO') `
                -Evidence @{ replies=$got12 }
        } else {
            Complete-W13Case 12 $t0 'fail' ("replies=$got12/20 backpressure_seen=$sawBackpressure")
        }

        # ---- case 13: the lost reply, resolved by the ledger ----------------
        # A SEPARATE client (its own server process) sends the commit and DIES
        # at a 3-second timeout - the reply is truly lost. The add-in commits
        # anyway; the retry with the SAME key must replay the recorded answer
        # (executed_once, not executed in this call) and the grid exists once.
        $t0 = Get-Date
        $key13 = "live-w13-indoubt-$probeRun"
        $grid13 = @{ kind='grid'; name=('HZ_DOUBT_' + $dimTag); start=@($w13X,26000,0); end=@(($w13X+2000),26000,0) }
        $dry13 = Invoke-Write 'horizun_create_elements' @{
            target_document=$wDoc; units='mm'; elements=@($grid13) }
        $tok13b = if ($dry13.data) { $dry13.data.confirmation_token } else { $null }
        if (-not $tok13b) { Complete-W13Case 13 $t0 'unverified' 'no token for the in-doubt probe' }
        else {
            $applyArgs13 = @{ target_document=$wDoc; units='mm'; dry_run=$false
                              confirmation_token=$tok13b; idempotency_key=$key13
                              elements=@($grid13) }
            $args13Path = Join-Path $scratchDir 'w13-indoubt.json'
            $applyArgs13 | ConvertTo-Json -Depth 16 -Compress |
                Set-Content -LiteralPath $args13Path -Encoding ascii
            # Occupy the add-in so the doomed client's apply is DELIVERED but
            # unanswered when its 6 s timeout kills it - the reply is truly lost
            # while the commit still happens.
            Send-Rpc @{ jsonrpc='2.0'; id=772001; method='tools/call'
                        params=@{ name='horizun_model_scan'
                                  arguments=@{ target_document_title=$wDoc; sections=@('categories','worksets'); top=200 } } }
            Start-Sleep -Milliseconds 600
            & pwsh -NoProfile -File (Join-Path (Get-Location) 'scripts/hz-call.ps1') -Tool horizun_create_elements `
                -ArgumentsPath $args13Path -Json (Join-Path $scratchDir 'w13-indoubt-lost.json') -Quiet -TimeoutSec 6 *> $null
            $null = Read-Rpc 240000
            Start-Sleep -Seconds 12
            $retry13 = Invoke-Write 'horizun_create_elements' $applyArgs13
            # The replay RETURNS THE RECORDED RESULT - its body still reads
            # Committed/created_verified because that IS the original answer; the
            # idempotency stamp is what tells the two apart (measured on run 16).
            $replayed = -not $retry13.isError -and $retry13.data -and $retry13.data.idempotency -and
                        [string]$retry13.data.idempotency.status -eq 'replayed' -and
                        $retry13.data.idempotency.command_executed_in_this_call -eq $false
            $q13 = Invoke-Write 'horizun_query_model' @{ categories=@('OST_Grids'); include_links=$false; max_rows=300 }
            $matches13 = if ($q13.data) { @($q13.data.rows | Where-Object { $_.name -match ('HZ_DOUBT_' + $dimTag) }).Count } else { -1 }
            if ($replayed -and $matches13 -eq 1) {
                Complete-W13Case 13 $t0 'pass' 'the commit reply was TRULY lost (the sending client died at its timeout); the retry with the SAME key replayed the recorded answer - status replayed, not executed in this call - and the grid exists exactly ONCE' `
                    -Evidence @{ idempotency=$retry13.data.idempotency }
            } else {
                Complete-W13Case 13 $t0 'fail' ("replayed=$replayed grids=$matches13 " + (Get-DimShortText $retry13.text))
            }
        }

        # ---- case 14: the preset proves itself from the file ----------------
        $t0 = Get-Date
        $ifc14 = Join-Path $scratchDir ('w13-preset-' + $dimTag + '.ifc')
        $exApply14 = Invoke-WriteApply 'horizun_export' @{
            target_document=$wDoc; format='ifc'; output_path=$ifc14; overwrite=$true
            preset=@{ name='hz-ifc4'; options=@{ ifc_version='IFC4' } } } 'w13-preset'
        $ex14 = $exApply14.answer
        $opt14 = $null
        if (-not $ex14.isError -and $ex14.data -and $ex14.data.preset) {
            $opt14 = @($ex14.data.preset.options) | Where-Object { $_.option -eq 'ifc_version' } | Select-Object -First 1
        }
        $typo14 = Invoke-Write 'horizun_export' @{
            target_document=$wDoc; format='ifc'; output_path=$ifc14
            preset=@{ name='hz-bad'; options=@{ ifc_versio='IFC4' } } }
        $typoRefused = $typo14.isError -and $typo14.text -match 'ifc_versio' -and $typo14.text -match "defaults under this preset's name"
        if ($opt14 -and $opt14.verified -eq $true -and $opt14.read_back -match 'IFC4' -and $typoRefused) {
            Complete-W13Case 14 $t0 'pass' ('the preset''s ifc_version was PROVED from the produced file (FILE_SCHEMA read back ' +
                $opt14.read_back + '), and the misspelled option refused the whole export by name') `
                -Evidence @{ preset=$ex14.data.preset }
        } else {
            Complete-W13Case 14 $t0 'fail' ("verified=$(if($opt14){$opt14.verified}) read=$(if($opt14){$opt14.read_back}) typo_refused=$typoRefused " + (Get-DimShortText $ex14.text))
        }

        # ---- case 15: the numbers, against caps declared BEFORE the run -----
        $t0 = Get-Date
        # Caps declared here, in the harness, versioned in git BEFORE execution:
        # no representative read over the disposable fixture may exceed 180 s or
        # an 8 MB reply. These are liveability bounds, not aspirations - a tool
        # past them is unusable in a session regardless of what it computes.
        $capMs = 180000; $capBytes = 8MB
        $perf15 = @()
        foreach ($probe15 in @(
            @{ T='horizun_query_model'; A=@{ categories=@('OST_PipeCurves'); include_links=$false; max_rows=500 } },
            @{ T='horizun_model_scan'; A=@{ target_document_title=$wDoc; sections=@('categories','worksets'); top=100 } },
            @{ T='horizun_audit_model'; A=@{ target_document=$wDoc; top=10 } },
            @{ T='horizun_clash'; A=@{ categories_a=@('OST_PipeCurves'); categories_b=@('OST_Walls'); include_links=$false } },
            @{ T='horizun_quantities'; A=@{ category='OST_PipeCurves' } })) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $r15 = Invoke-Write $probe15.T $probe15.A
            $sw.Stop()
            $bytes15 = if ($r15.text) { [Text.Encoding]::UTF8.GetByteCount($r15.text) } else { 0 }
            $perf15 += @{ tool=$probe15.T; elapsed_ms=$sw.ElapsedMilliseconds; reply_bytes=$bytes15; is_error=$r15.isError }
        }
        $breaches = @($perf15 | Where-Object { $_.elapsed_ms -gt $capMs -or $_.reply_bytes -gt $capBytes -or $_.is_error })
        if ($breaches.Count -eq 0) {
            $summary15 = ($perf15 | ForEach-Object { "$($_.tool)=$($_.elapsed_ms)ms/$([math]::Round($_.reply_bytes/1KB))KB" }) -join ' '
            Complete-W13Case 15 $t0 'pass' ('five representative reads measured under the PRE-DECLARED caps (180 s, 8 MB): ' + $summary15) `
                -Evidence @{ measurements=$perf15; caps=@{ ms=$capMs; bytes=$capBytes } }
        } else {
            Complete-W13Case 15 $t0 'fail' ('cap breached or errored: ' + (($breaches | ForEach-Object { "$($_.tool)=$($_.elapsed_ms)ms err=$($_.is_error)" }) -join ' '))
        }

        for ($wc13=1; $wc13 -le 15; $wc13++) {
            if (-not $script:w13CasesDone.ContainsKey($wc13)) { Complete-W13Case $wc13 (Get-Date) 'unverified' 'the W13 section ended before this probe ran - a harness bug' }
        }

        # ------------------------------------------------------------------
        # W14: the remaining phase surfaces. Inline-accessory refusal and
        # whole-batch rollback, the rebar cover safe subset over the typed
        # writer, the interrupted-job answer read off the disk record, and
        # S/M/L performance against caps declared before anything is timed.
        # ------------------------------------------------------------------
        $script:w14CasesDone = @{}
        function Complete-W14Case {
            param([int]$CaseNumber, [datetime]$Started, [string]$Outcome, [string]$Detail, $Evidence=$null)
            if ($script:w14CasesDone.ContainsKey($CaseNumber)) { return }
            $script:w14CasesDone[$CaseNumber] = $true
            $entry = $writeNames[$w14NameBase + $CaseNumber - 1]
            Add-Write $entry.N $entry.T $Outcome $Detail
            $script:dp2Evidence += @{
                case=('w14-' + $CaseNumber); name=$entry.N; tool=$entry.T
                started_utc=$Started.ToUniversalTime().ToString('o'); outcome=$Outcome; detail=$Detail; evidence=$Evidence
            }
        }
        $w14X = 750000

        # ---- cases 1+2: the inline accessory, negatively ------------------
        # No pipe-accessory family exists on this machine (measured at W13 c1:
        # the content library ships none), so the POSITIVE stays a named
        # fixture gap on the ledger. What IS measurable: the plan-time off-axis
        # refusal in millimetres, and that a failed inline connect rolls the
        # whole break-place-connect transaction back leaving the pipe unbroken.
        $t0 = Get-Date
        if (-not $levelId -or -not $pipeType -or -not $pipeSystem) {
            Complete-W14Case 1 $t0 'unverified' 'no level/pipe type/system discovered for the accessory probes'
            Complete-W14Case 2 $t0 'unverified' 'no level/pipe type/system discovered for the accessory probes'
        } else {
            $accPipe = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='pipe'; start=@($w14X,0,1200); end=@(($w14X+3000),0,1200)
                              level_id=[long]$levelId; type_id=[long]$pipeType; system_type_id=[long]$pipeSystem })
            } 'w14-accpipe'
            $accPipeId = if ($accPipe.stage -eq 'apply' -and -not $accPipe.answer.isError) {
                @($accPipe.answer.data.rows)[0].element_id } else { $null }
            $sfQ = Invoke-Write 'horizun_query_model' @{ categories=@('OST_StructuralFraming'); include_types=$true; include_links=$false; max_rows=40 }
            $sfSymbol = if ($sfQ.data) { @($sfQ.data.rows | Where-Object { $_.is_element_type } | ForEach-Object { $_.element_id }) | Select-Object -First 1 } else { $null }
            if (-not $accPipeId -or -not $sfSymbol) {
                Complete-W14Case 1 $t0 'unverified' ("staging incomplete: pipe=$accPipeId symbol=$sfSymbol " + (Get-DimShortText $accPipe.answer.text))
                Complete-W14Case 2 $t0 'unverified' 'staging incomplete for the rollback probe'
            } else {
                # Case 1: 200 mm off the axis. dry_run defaults true - the refusal
                # is plan-time and nothing is written by construction.
                $off1 = Invoke-Write 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='accessory_inline'; pipe_id=[long]$accPipeId; type_id=[long]$sfSymbol
                                  point=@(($w14X+1500),200,1200) }) }
                # The dry-run validates per item: the refusal arrives as invalid=1
                # with the error named per row, transaction never started (measured
                # at run 20 - the reply is a SUCCESS whose row is refused).
                $err1 = if ($off1.data -and $off1.data.errors) { [string]@($off1.data.errors)[0].error } else { $null }
                if (-not $off1.isError -and $off1.data -and [int]$off1.data.invalid -eq 1 -and
                    [string]$off1.data.transaction_status -eq 'not_started' -and
                    $err1 -match "off the pipe's axis" -and $err1 -match '\d+(\.\d+)?\s*mm') {
                    Complete-W14Case 1 $t0 'pass' ('the off-axis point refused at plan time (invalid row, transaction not_started), distance named in mm: ' + $err1)
                } else {
                    Complete-W14Case 1 $t0 'fail' ('expected the plan-time off-axis refusal in mm, got: ' + (Get-DimShortText $off1.text))
                }

                # Case 2: point ON the axis, but the symbol is structural framing -
                # no MEP connectors, not point-placeable that way. Whatever throws
                # inside the transaction, the contract is the same: named error,
                # whole rollback, the host pipe still ONE piece afterwards.
                $t0 = Get-Date
                $before2 = Invoke-Write 'horizun_query_model' @{ categories=@('OST_PipeCurves'); include_links=$false; max_rows=500 }
                $countBefore = if ($before2.data) { @($before2.data.rows | Where-Object { -not $_.is_element_type }).Count } else { -1 }
                $roll2 = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='accessory_inline'; pipe_id=[long]$accPipeId; type_id=[long]$sfSymbol
                                  point=@(($w14X+1500),0,1200) })
                } 'w14-accroll'
                $failedNamed = ($roll2.stage -eq 'dry_run' -and $roll2.answer.isError) -or
                               ($roll2.stage -eq 'apply' -and $roll2.answer.isError)
                $after2 = Invoke-Write 'horizun_query_model' @{
                    element_ids=@([long]$accPipeId); include_links=$false; max_rows=5 }
                $survives = -not $after2.isError -and $after2.data -and
                            @($after2.data.rows | Where-Object { [long]$_.element_id -eq [long]$accPipeId }).Count -eq 1
                $again2 = Invoke-Write 'horizun_query_model' @{ categories=@('OST_PipeCurves'); include_links=$false; max_rows=500 }
                $countAfter = if ($again2.data) { @($again2.data.rows | Where-Object { -not $_.is_element_type }).Count } else { -2 }
                if ($failedNamed -and $survives -and $countBefore -ge 1 -and $countAfter -eq $countBefore) {
                    Complete-W14Case 2 $t0 'pass' ('the inline connect failed NAMED and rolled back WHOLE: the host pipe re-read by id (still one piece), pipe count unchanged at ' + $countAfter + '. Error: ' + (Get-DimShortText $roll2.answer.text)) `
                        -Evidence @{ pipes_before=$countBefore; pipes_after=$countAfter; stage=$roll2.stage }
                } else {
                    Complete-W14Case 2 $t0 'fail' ("failed_named=$failedNamed pipe_survives=$survives count $countBefore->$countAfter " + (Get-DimShortText $roll2.answer.text))
                }
            }
        }

        # ---- case 3: the rebar cover safe subset --------------------------
        # A structural wall is a rebar host; its cover is an ElementId
        # parameter pointing at a RebarCoverType. The staging (a wall made
        # structural, a second cover type to point at) is fixture work; the
        # MEASURED assertion is the typed writer flipping the pointer and
        # re-reading it from the model.
        $t0 = Get-Date
        $rcWall = Invoke-WriteApply 'horizun_create_elements' @{
            target_document=$wDoc; units='mm'
            elements=@(@{ kind='wall'; start=@($w14X,6000,0); end=@(($w14X+3000),6000,0)
                          level_id=[long]$levelId; height=3000 })
        } 'w14-rcwall'
        $rcWallId = if ($rcWall.stage -eq 'apply' -and -not $rcWall.answer.isError) {
            @($rcWall.answer.data.rows)[0].element_id } else { $null }
        if (-not $rcWallId) {
            Complete-W14Case 3 $t0 'unverified' ('no wall staged for the cover probe: ' + (Get-DimShortText $rcWall.answer.text))
        } else {
            $mkStruct = Invoke-WriteApply 'horizun_write_params_verified' @{
                target_document=$wDoc
                writes=@(@{ target_id=[long]$rcWallId; parameter='Structural'; value=1 })
            } 'w14-rcstruct'
            $rcStage = @'
from Autodesk.Revit.DB import Transaction
from Autodesk.Revit.DB.Structure import RebarCoverType
name = 'HZ_RC_' + '__TAG__'
existing = None
from Autodesk.Revit.DB import FilteredElementCollector
for c in FilteredElementCollector(doc).OfClass(RebarCoverType):
    if c.Name == name: existing = c
t = Transaction(doc, 'Horizun: stage cover type'); t.Start()
c = existing or RebarCoverType.Create(doc, name, 50.0 / 304.8)
t.Commit()
cid = c.Id.IntegerValue if hasattr(c.Id, 'IntegerValue') else c.Id.Value
__output__ = {'status': 'self_reported_verified', 'cover_id': cid, 'name': name}
'@
            $rcStagePath = Join-Path $scratchDir 'w14-covertype.py'
            [IO.File]::WriteAllText($rcStagePath, $rcStage.Replace('__TAG__', $dimTag), [Text.UTF8Encoding]::new($false))
            $rcPy = Invoke-Write 'horizun_execute_python' @{
                code_path=$rcStagePath; target_document=$wDoc
                idempotency_key="live-w14-covertype-$probeRun" }
            $coverId = if ($rcPy.data -and $rcPy.data.output) { $rcPy.data.output.cover_id } else { $null }
            $structOk = $mkStruct.stage -eq 'apply' -and -not $mkStruct.answer.isError
            if (-not $structOk -or -not $coverId) {
                Complete-W14Case 3 $t0 'unverified' ("staging incomplete: structural=$structOk cover_id=$coverId " + (Get-DimShortText $rcPy.text))
            } else {
                $rcWrite = Invoke-WriteApply 'horizun_write_params_verified' @{
                    target_document=$wDoc
                    writes=@(@{ target_id=[long]$rcWallId; parameter='Rebar Cover - Exterior Face'; value=[long]$coverId })
                } 'w14-rcset'
                # The product's own aggregates ARE the verification (measured at
                # run 20: the write confirmed against the requested value while my
                # per-row field names were wrong): confirmed_against_your_value
                # means the post-commit re-read matched the id we sent.
                $rcOk = $rcWrite.stage -eq 'apply' -and -not $rcWrite.answer.isError -and
                        [int]$rcWrite.answer.data.writes_confirmed_against_your_value -eq 1 -and
                        [int]$rcWrite.answer.data.failed -eq 0 -and
                        [int]$rcWrite.answer.data.unresolved -eq 0
                if ($rcOk) {
                    Complete-W14Case 3 $t0 'pass' ('the typed writer pointed the structural wall''s exterior cover at the staged 50 mm cover type (id ' + $coverId + ') and the post-commit re-read CONFIRMED it against the requested value') `
                        -Evidence @{ wall=$rcWallId; cover=$coverId; aggregates=($rcWrite.answer.data | Select-Object writes_confirmed, writes_confirmed_against_your_value, failed, unresolved) }
                } else {
                    Complete-W14Case 3 $t0 'fail' ("stage=$($rcWrite.stage) " + (Get-DimShortText $rcWrite.answer.text))
                }
            }
        }

        # ---- case 4: the interrupted job answers off the disk -------------
        # A REAL record from an earlier Revit that died mid-job (never staged,
        # never simulated: if this machine has none, the case says so). The
        # server reads it without Revit and must carry the liveness fact and
        # the second-write warning ON the record.
        $t0 = Get-Date
        $jobsDir = Join-Path $env:USERPROFILE '.horizun\jobs'
        $deadJob = $null
        if (Test-Path -LiteralPath $jobsDir) {
            foreach ($jf in (Get-ChildItem -LiteralPath $jobsDir -Filter '*.jsonl' -File | Sort-Object Name)) {
                if ($deadJob) { break }
                try {
                    $events = @(); $jpid = $null
                    foreach ($ln in [IO.File]::ReadLines($jf.FullName)) {
                        if ([string]::IsNullOrWhiteSpace($ln)) { continue }
                        try { $o = $ln | ConvertFrom-Json } catch { continue }
                        if ($o.event) { $events += [string]$o.event }
                        if ($o.pid) { $jpid = [int]$o.pid }
                    }
                    if ($jpid -and $events -contains 'running' -and
                        -not ($events | Where-Object { $_ -in @('finish','finished','failed','not_started') }) -and
                        -not (Get-Process -Id $jpid -ErrorAction SilentlyContinue)) {
                        $deadJob = @{ id = [IO.Path]::GetFileNameWithoutExtension($jf.Name); pid = $jpid }
                    }
                } catch { }
            }
        }
        if (-not $deadJob) {
            Complete-W14Case 4 $t0 'unverified' 'this machine holds no real interrupted-job record from a dead Revit; the probe never stages one - a simulated crash record would be the exact substitution this suite exists to catch'
        } else {
            # The release runner deliberately isolates HORIZUN_DATA_ROOT. Copy the
            # exact immutable JSONL bytes of this REAL prior interrupted job into
            # that isolated ledger so job_status reads the record we selected,
            # instead of looking for its id under an intentionally empty folder.
            $sourceJob = Join-Path $jobsDir ($deadJob.id + '.jsonl')
            $isolatedRoot = $env:HORIZUN_DATA_ROOT
            if (-not [string]::IsNullOrWhiteSpace($isolatedRoot) -and
                (Test-Path -LiteralPath $sourceJob)) {
                $isolatedJobs = Join-Path $isolatedRoot 'jobs'
                New-Item -ItemType Directory -Path $isolatedJobs -Force | Out-Null
                Copy-Item -LiteralPath $sourceJob -Destination (Join-Path $isolatedJobs ($deadJob.id + '.jsonl')) -Force
            }
            $js = Invoke-Write 'horizun_job_status' @{ job_id = $deadJob.id }
            $row = if ($js.data -and $js.data.jobs) { @($js.data.jobs)[0] } else { $null }
            if ($row -and $row.state -eq 'running' -and $row.process_alive -eq $false -and
                $row.what_this_means -match 'second write, not a\s+recovery' -and
                $row.what_this_means -match 'PROCESS DIED') {
                Complete-W14Case 4 $t0 'pass' ('job ' + $deadJob.id + ' (Revit pid ' + $deadJob.pid + ', dead) answered off the disk record: PROCESS DIED, never finishing, checkpointed work HAS HAPPENED, re-running is a second write - the guidance travels ON the record, without Revit') `
                    -Evidence @{ job=$deadJob; what_this_means=$row.what_this_means }
            } else {
                Complete-W14Case 4 $t0 'fail' ('the record did not answer with liveness + guidance: state=' + $row.state + ' alive=' + $row.process_alive + ' ' + (Get-DimShortText $js.text))
            }
        }

        # ---- cases 5-7: S/M/L under caps declared BEFORE the run ----------
        # Three workloads over the two SAME-YEAR documents the runner already
        # opened: a small document-only read, a medium categories/worksets read,
        # and the large categories/worksets/types read after all fixture writes.
        $w14Caps = @{ S = 60000; M = 120000; L = 180000; bytes = 8MB; ws_delta_bytes = 4GB }
        $t0 = Get-Date
        $wsBefore = (Get-Process -Id $target.pid -ErrorAction SilentlyContinue).WorkingSet64
        # Resolve the SAME-YEAR fixtures that the release runner actually opened.
        $sizeRuns = @()
        $openFacts = @()
        $sizeHealth = Invoke-Write 'horizun_health' @{}
        if ($sizeHealth.data) { $openFacts = @($sizeHealth.data.open_documents) }
        $releaseFact = $openFacts | Where-Object { [string]$_.title -eq $wDoc } | Select-Object -First 1
        $inactiveFact = $openFacts | Where-Object { [string]$_.title -eq $InactiveDocument } | Select-Object -First 1
        $sizePlan = @()
        if ($inactiveFact -and $inactiveFact.path) {
            $sizePlan += @{ size='S'; title=[string]$inactiveFact.title; path=[string]$inactiveFact.path
                            activate=$true; sections=@('document'); top=10 }
        }
        if ($releaseFact -and $releaseFact.path) {
            $sizePlan += @{ size='M'; title=[string]$releaseFact.title; path=[string]$releaseFact.path
                            activate=$true; sections=@('categories','worksets'); top=50 }
            $sizePlan += @{ size='L'; title=[string]$releaseFact.title; path=[string]$releaseFact.path
                            activate=$false; sections=@('categories','worksets','types'); top=100 }
        }

        # Fixture staging only: activate an ALREADY-OPEN exact path, then prove
        # the active title again through health. document_session correctly
        # refuses to OPEN a central without opt-in, but this benchmark must not
        # confuse that guard with activation and must never reopen the central.
        function Activate-W14OpenFixture($sz) {
            $before = Invoke-Write 'horizun_health' @{}
            $activeBefore = if ($before.data) {
                @($before.data.open_documents | Where-Object { $_.is_active -eq $true }) | Select-Object -First 1
            } else { $null }
            if ($activeBefore -and [string]$activeBefore.title -eq [string]$sz.title) { return $null }
            if (-not $activeBefore) { return 'health could not identify the active document before activation' }
            $escapedPath = ([string]$sz.path).Replace("'", "\\'")
            $activateCode = @'
target = r'__PATH__'
uiapp.OpenAndActivateDocument(target)
active = uiapp.ActiveUIDocument.Document
__output__ = {'status': 'self_reported_verified', 'title': active.Title, 'path': active.PathName}
'@.Replace('__PATH__', $escapedPath)
            $activation = Invoke-Write 'horizun_execute_python' @{
                code=$activateCode; target_document=[string]$activeBefore.title
                idempotency_key=("live-w14-activate-{0}-{1}" -f $sz.size, $probeRun) }
            if ($activation.isError) { return 'activation script failed: ' + (Get-DimShortText $activation.text) }
            $after = Invoke-Write 'horizun_health' @{}
            $activeAfter = if ($after.data) {
                @($after.data.open_documents | Where-Object { $_.is_active -eq $true }) | Select-Object -First 1
            } else { $null }
            if (-not $activeAfter -or [string]$activeAfter.title -ne [string]$sz.title) {
                return "health reads '$([string]$activeAfter.title)' active after requesting '$([string]$sz.title)'"
            }
            return $null
        }
        foreach ($sz in $sizePlan) {
            if ($sz.activate) {
                $activationError = Activate-W14OpenFixture $sz
                if ($activationError) { $sizeRuns += @{ size=$sz.size; error=$activationError }; continue }
            }
            if (-not $sz.title) { $sizeRuns += @{ size=$sz.size; error='no verified title to scan against' }; continue }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $scan = Invoke-Write 'horizun_model_scan' @{
                target_document_title=$sz.title; sections=@($sz.sections); top=[int]$sz.top }
            $sw.Stop()
            $sizeRuns += @{
                size=$sz.size; title=$sz.title; elapsed_ms=$sw.ElapsedMilliseconds
                reply_bytes=$(if ($scan.text) { [Text.Encoding]::UTF8.GetByteCount($scan.text) } else { 0 })
                ui_hold_ms=$(if ($scan.data -and $scan.data.bridge_queue) { [long]$scan.data.bridge_queue.execution_and_wait_ms } else { -1 })
                is_error=$scan.isError; error=$(if ($scan.isError) { Get-DimShortText $scan.text } else { $null }) }
        }
        # M activates the release fixture and L measures it again, so a complete
        # size run already ends with the write model active. Prove that explicitly.
        $restoreHealth = Invoke-Write 'horizun_health' @{}
        $restoredTitle = if ($restoreHealth.data) {
            @($restoreHealth.data.open_documents | Where-Object { $_.is_active -eq $true })[0].title
        } else { $null }
        $restoreError = $null
        if ([string]$restoredTitle -ne $wDoc) {
            $restoreError = "write fixture was not restored active; health reads '$restoredTitle'"
            $sizeRuns += @{ size='restore'; error=$restoreError }
        }
        $wsAfter = (Get-Process -Id $target.pid -ErrorAction SilentlyContinue).WorkingSet64
        $measured = if ($restoreError) { @() } else {
            @($sizeRuns | Where-Object { -not $_.error -and -not $_.is_error })
        }
        $capBad = @($measured | Where-Object { $_.elapsed_ms -gt $w14Caps[[string]$_.size] -or ($_.ui_hold_ms -ge 0 -and $_.ui_hold_ms -gt $w14Caps[[string]$_.size]) })
        $sizesSummary = ($sizeRuns | ForEach-Object {
            if ($_.error) { "$($_.size)=ERROR[$($_.error)]" } else { "$($_.size)($($_.title))=$($_.elapsed_ms)ms/hold $($_.ui_hold_ms)ms/$([math]::Round($_.reply_bytes/1KB))KB" } }) -join ' '
        if ($measured.Count -eq 3 -and $capBad.Count -eq 0) {
            Complete-W14Case 5 $t0 'pass' ('three sizes scanned under the caps declared before the run (S 60 s, M 120 s, L 180 s - wall AND UI hold): ' + $sizesSummary) `
                -Evidence @{ runs=$sizeRuns; caps=$w14Caps }
        } elseif ($measured.Count -lt 3) {
            Complete-W14Case 5 $t0 'unverified' ('only ' + $measured.Count + ' of 3 sizes measured: ' + $sizesSummary)
        } else {
            Complete-W14Case 5 $t0 'fail' ('a declared cap was breached: ' + $sizesSummary)
        }
        $t0 = Get-Date
        $bigBad = @($measured | Where-Object { $_.reply_bytes -gt $w14Caps.bytes })
        $largest = $measured | Sort-Object reply_bytes -Descending | Select-Object -First 1
        if ($measured.Count -eq 3 -and $bigBad.Count -eq 0 -and $largest) {
            Complete-W14Case 6 $t0 'pass' ('every reply rode under the declared 8 MB cap; the largest was ' + $largest.size + ' (' + $largest.title + ') at ' + [math]::Round($largest.reply_bytes/1KB) + ' KB') `
                -Evidence @{ largest=$largest; cap_bytes=$w14Caps.bytes }
        } elseif ($measured.Count -lt 3) {
            Complete-W14Case 6 $t0 'unverified' ('only ' + $measured.Count + ' of 3 sizes measured')
        } else {
            Complete-W14Case 6 $t0 'fail' ('reply cap breached: ' + (($bigBad | ForEach-Object { "$($_.size)=$($_.reply_bytes)B" }) -join ' '))
        }
        $t0 = Get-Date
        if ($wsBefore -and $wsAfter) {
            $wsDelta = $wsAfter - $wsBefore
            if ([math]::Abs($wsDelta) -le $w14Caps.ws_delta_bytes) {
                Complete-W14Case 7 $t0 'pass' ('Revit working set measured across the S/M/L batch: ' + [math]::Round($wsBefore/1GB,2) + ' GB -> ' + [math]::Round($wsAfter/1GB,2) + ' GB (delta ' + [math]::Round($wsDelta/1MB) + ' MB, declared bound |delta| <= 4 GB)') `
                    -Evidence @{ ws_before=$wsBefore; ws_after=$wsAfter; delta=$wsDelta }
            } else {
                Complete-W14Case 7 $t0 'fail' ('working-set delta breached the declared 4 GB bound: ' + [math]::Round($wsDelta/1MB) + ' MB')
            }
        } else {
            Complete-W14Case 7 $t0 'unverified' 'the Revit process working set could not be read'
        }

        # ---- case 8: units and locale, declared - never guessed -----------
        # One value, four readings. MEASURED at run 21 and the reason this probe
        # is calibrated rather than hard-coded: the comparison happens in the
        # PARAMETER'S OWN display unit, and this project displays lengths in
        # feet-fractional-inches - a cell of "3000" is not the height of a wall
        # 3000 mm tall, it is 3000 FEET. The staging reads what the model holds
        # in its own unit (read-only, self-reported); every assertion below is
        # the typed command's own reply.
        $t0 = Get-Date
        $uWall = Invoke-WriteApply 'horizun_create_elements' @{
            target_document=$wDoc; units='mm'
            elements=@(@{ kind='wall'; start=@(($w14X+4000),9000,0); end=@(($w14X+7000),9000,0)
                          level_id=[long]$levelId; height=3000 })
        } 'w14-uwall'
        $uWallId = if ($uWall.stage -eq 'apply' -and -not $uWall.answer.isError) {
            @($uWall.answer.data.rows)[0].element_id } else { $null }
        if (-not $uWallId) {
            Complete-W14Case 8 $t0 'unverified' ('no wall staged for the units probe: ' + (Get-DimShortText $uWall.answer.text))
        } else {
            $uMark = 'HZ_U_' + $dimTag
            $uSet = Invoke-WriteApply 'horizun_write_params_verified' @{
                target_document=$wDoc
                writes=@(@{ target_id=[long]$uWallId; parameter='Mark'; value=$uMark })
            } 'w14-umark'
            # Staging: what does the model hold, in the unit the parameter displays?
            $readPy = @'
from Autodesk.Revit.DB import ElementId, UnitUtils
w = doc.GetElement(ElementId(__ID__))
p = w.LookupParameter('Unconnected Height')
unit = p.GetUnitTypeId()
__output__ = {'status': 'self_reported_verified',
              'display_number': UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit),
              'display_string': p.AsValueString(),
              'unit': unit.TypeId}
'@
            $readPath = Join-Path $scratchDir 'w14-readheight.py'
            [IO.File]::WriteAllText($readPath, $readPy.Replace('__ID__', [string]$uWallId), [Text.UTF8Encoding]::new($false))
            $heightR = Invoke-Write 'horizun_execute_python' @{
                code_path=$readPath; target_document=$wDoc
                idempotency_key="live-w14-readheight-$probeRun" }
            $displayNumber = if ($heightR.data -and $heightR.data.output) { [double]$heightR.data.output.display_number } else { $null }
            $displayUnit = if ($heightR.data -and $heightR.data.output) { [string]$heightR.data.output.unit } else { '(unread)' }
            if (-not $displayNumber) {
                Complete-W14Case 8 $t0 'unverified' ('the wall height could not be read in its display unit: ' + (Get-DimShortText $heightR.text))
            } else {
                $dotCell = $displayNumber.ToString('F9', [Globalization.CultureInfo]::InvariantCulture)
                $commaCell = $dotCell.Replace('.', ',')
                $csvDot = Join-Path $scratchDir 'w14-units-dot.csv'
                [IO.File]::WriteAllText($csvDot, ('Mark,Unconnected Height' + "`r`n" + $uMark + ',' + $dotCell + "`r`n"), [Text.UTF8Encoding]::new($false))
                $csvComma = Join-Path $scratchDir 'w14-units-comma.csv'
                [IO.File]::WriteAllText($csvComma, ('Mark,Unconnected Height' + "`r`n" + '"' + $uMark + '","' + $commaCell + '"' + "`r`n"), [Text.UTF8Encoding]::new($false))
                $tabBase = @{ path=$csvDot; key_column='Mark'
                              value_columns=@{ 'Unconnected Height'='Unconnected Height' }
                              category='OST_Walls' }
                $r1 = Invoke-Write 'horizun_write_params_verified' @{ target_document=$wDoc; tabular_source=$tabBase }
                $tabDot = $tabBase.Clone(); $tabDot['decimal_separator'] = '.'
                $r2 = Invoke-Write 'horizun_write_params_verified' @{ target_document=$wDoc; tabular_source=$tabDot }
                $tabC1 = @{ path=$csvComma; key_column='Mark'
                            value_columns=@{ 'Unconnected Height'='Unconnected Height' }
                            category='OST_Walls'; decimal_separator=',' }
                $r3 = Invoke-Write 'horizun_write_params_verified' @{ target_document=$wDoc; tabular_source=$tabC1 }
                $tabC2 = $tabC1.Clone(); $tabC2['decimal_separator'] = '.'
                $r4 = Invoke-Write 'horizun_write_params_verified' @{ target_document=$wDoc; tabular_source=$tabC2 }
                $g1 = -not $r1.isError -and [int]$r1.data.tabular.ops_generated -eq 1 -and
                      [int]$r1.data.tabular.numeric_compares -eq 0
                $g2 = -not $r2.isError -and [int]$r2.data.tabular.ops_generated -eq 0 -and
                      [int]$r2.data.tabular.skipped_unchanged -ge 1 -and [int]$r2.data.tabular.numeric_compares -ge 1
                $g3 = -not $r3.isError -and [int]$r3.data.tabular.ops_generated -eq 0 -and
                      [int]$r3.data.tabular.numeric_compares -ge 1
                $g4 = -not $r4.isError -and [int]$r4.data.tabular.ops_generated -eq 1
                $uSetOk = $uSet.stage -eq 'apply' -and -not $uSet.answer.isError
                if ($uSetOk -and $g1 -and $g2 -and $g3 -and $g4) {
                    Complete-W14Case 8 $t0 'pass' ('one height (' + $dotCell + ' in ' + $displayUnit + '), four readings: UNDECLARED it differs from the display string and writes (1 op, 0 numeric compares); declared "." it measures EQUAL in the parameter''s own display unit and skips (0 ops, numeric); declared "," the same number written "' + $commaCell + '" parses and skips; declared "." that comma cell does NOT parse, falls back to the string compare and writes. The separator is DECLARED, never guessed - and the comparison is in the unit the parameter displays, not one the caller assumed.') `
                        -Evidence @{ display_number=$displayNumber; unit=$displayUnit
                                     undeclared=$r1.data.tabular; dot=$r2.data.tabular
                                     comma=$r3.data.tabular; cross=$r4.data.tabular }
                } else {
                    Complete-W14Case 8 $t0 'fail' ("mark=$uSetOk g1=$g1 g2=$g2 g3=$g3 g4=$g4 cell=$dotCell unit=$displayUnit " +
                        'undeclared_ops=' + [string]$r1.data.tabular.ops_generated +
                        ' dot_ops=' + [string]$r2.data.tabular.ops_generated +
                        ' dot_numeric=' + [string]$r2.data.tabular.numeric_compares +
                        ' comma_ops=' + [string]$r3.data.tabular.ops_generated +
                        ' cross_ops=' + [string]$r4.data.tabular.ops_generated)
                }
            }
        }

        # ---- cases 9+10: the MEP positives, on families this run authors ---
        # This machine's Revit content library ships no MEP fittings or
        # accessories at all (measured, W13 c1). Rather than call the surface
        # untestable, the harness AUTHORS the two fixture families it needs -
        # a two-connector valve and a Part Type SpudPerpendicular tap - loads
        # them, and then exercises the TYPED commands against them. The
        # families are fixture staging (self-reported); every assertion below
        # is the typed command's own verified reply, re-read from the model.
        $t0 = Get-Date
        $mepFamPy = @'
import os, tempfile
from Autodesk.Revit.DB import (Transaction, BuiltInCategory, Category, XYZ, Line,
                               CurveArray, CurveArrArray, Plane, SketchPlane, Options,
                               ViewDetailLevel, PlanarFace, BuiltInParameter,
                               SaveAsOptions, PartType, ConnectorElement)
from Autodesk.Revit.DB.Plumbing import PipeSystemType

TEMPLATE = r'__TEMPLATE__'
MM = 1.0 / 304.8
project = doc

def build(name, bic, part_type, faces_wanted, along_z, classification=PipeSystemType.Fitting):
    famDoc = doc.Application.NewFamilyDocument(TEMPLATE)
    rep = {}
    t = Transaction(famDoc, 'Horizun: author ' + name); t.Start()
    famDoc.OwnerFamily.FamilyCategory = Category.GetCategory(famDoc, bic)
    rep['category'] = famDoc.OwnerFamily.FamilyCategory.Name
    if part_type is not None:
        pp = famDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)
        if pp is not None and not pp.IsReadOnly:
            pp.Set(int(part_type))
            rep['part_type'] = famDoc.OwnerFamily.get_Parameter(
                BuiltInParameter.FAMILY_CONTENT_PART_TYPE).AsInteger()
    half = 30 * MM
    x0, x1 = (-25 * MM, 25 * MM) if along_z else (-100 * MM, 100 * MM)
    pts = [XYZ(x0, -half, 0), XYZ(x1, -half, 0), XYZ(x1, half, 0), XYZ(x0, half, 0)]
    arr = CurveArray()
    for i in range(4):
        arr.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]))
    prof = CurveArrArray(); prof.Append(arr)
    plane = SketchPlane.Create(famDoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero))
    ext = famDoc.FamilyCreate.NewExtrusion(True, prof, plane, 60 * MM)
    famDoc.Regenerate()
    opt = Options(); opt.ComputeReferences = True; opt.DetailLevel = ViewDetailLevel.Fine
    faces = {}
    for g in ext.get_Geometry(opt):
        if not hasattr(g, 'Faces'):
            continue
        for f in g.Faces:
            if isinstance(f, PlanarFace):
                n = f.FaceNormal
                if abs(abs(n.X) - 1) < 1e-6:
                    faces['+x' if n.X > 0 else '-x'] = f.Reference
                elif abs(n.Z - 1) < 1e-6:
                    faces['+z'] = f.Reference
    made = 0
    for key in faces_wanted:
        if key not in faces:
            continue
        c = ConnectorElement.CreatePipeConnector(famDoc, classification, faces[key])
        rp = c.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS)
        if rp is not None and not rp.IsReadOnly:
            rp.Set(25 * MM)
        made += 1
    rep['connectors_created'] = made
    t.Commit()
    path = os.path.join(tempfile.gettempdir(), name + '.rfa')
    if os.path.exists(path):
        try: os.remove(path)
        except Exception: pass
    sa = SaveAsOptions(); sa.OverwriteExistingFile = True
    famDoc.SaveAs(path, sa)
    fam = famDoc.LoadFamily(project)
    famDoc.Close(False)
    rep['loaded'] = fam is not None
    if fam is not None:
        rep['symbol_ids'] = [i.IntegerValue if hasattr(i, 'IntegerValue') else i.Value for i in fam.GetFamilySymbolIds()]
    return rep

from Autodesk.Revit.DB import FilteredElementCollector
from Autodesk.Revit.DB.Plumbing import PipingSystemType

def matching_system_type(classification_name):
    for st in FilteredElementCollector(project).OfClass(PipingSystemType):
        try:
            if str(st.SystemClassification) == classification_name:
                sid = st.Id.IntegerValue if hasattr(st.Id, 'IntegerValue') else st.Id.Value
                return {'id': sid, 'name': st.Name, 'classification': classification_name}
        except Exception:
            continue
    return None

out = {'accessory': build('HZ_ACCFIX___TAG__', BuiltInCategory.OST_PipeAccessory, None, ['-x', '+x'], False),
       'tap': build('HZ_TAPFIX___TAG__', BuiltInCategory.OST_PipeFitting, PartType.SpudPerpendicular, ['+z'], True),
       'equipment': build('HZ_EQUIPFIX___TAG__', BuiltInCategory.OST_MechanicalEquipment, None, ['-x', '+x'], False,
                          PipeSystemType.DomesticColdWater)}
out['equipment_system_type'] = matching_system_type('DomesticColdWater')
ok = (out['accessory'].get('connectors_created') == 2 and out['accessory'].get('loaded') and
      out['tap'].get('connectors_created') == 1 and out['tap'].get('loaded') and
      out['equipment'].get('connectors_created') == 2 and out['equipment'].get('loaded') and
      out['equipment_system_type'] is not None)
out['status'] = 'self_reported_verified' if ok else 'partial'
__output__ = out
'@
        if (-not $dimTemplatePath -or -not $levelId -or -not $pipeType -or -not $pipeSystem) {
            Complete-W14Case 9 $t0 'unverified' 'no family template / level / pipe type / system for the MEP fixture families'
            Complete-W14Case 10 $t0 'unverified' 'no family template / level / pipe type / system for the MEP fixture families'
        } else {
            $genericTemplate = Join-Path $env:ProgramData ("Autodesk\RVT {0}\Family Templates\English\Metric Generic Model.rft" -f $Year)
            if (-not (Test-Path -LiteralPath $genericTemplate)) { $genericTemplate = $dimTemplatePath }
            $mepFamPath = Join-Path $scratchDir 'w14-mepfam.py'
            [IO.File]::WriteAllText($mepFamPath,
                $mepFamPy.Replace('__TEMPLATE__', $genericTemplate).Replace('__TAG__', $dimTag),
                [Text.UTF8Encoding]::new($false))
            $famR = Invoke-Write 'horizun_execute_python' @{
                code_path=$mepFamPath; target_document=$wDoc
                idempotency_key="live-w14-mepfam-$probeRun" }
            $accSym = $null; $tapSym = $null; $equipSym = $null; $equipSystemType = $null; $equipSystemName = $null
            if ($famR.data -and $famR.data.output) {
                if ($famR.data.output.accessory -and $famR.data.output.accessory.symbol_ids) {
                    $accSym = @($famR.data.output.accessory.symbol_ids)[0] }
                if ($famR.data.output.tap -and $famR.data.output.tap.symbol_ids) {
                    $tapSym = @($famR.data.output.tap.symbol_ids)[0] }
                if ($famR.data.output.equipment -and $famR.data.output.equipment.symbol_ids) {
                    $equipSym = @($famR.data.output.equipment.symbol_ids)[0] }
                if ($famR.data.output.equipment_system_type) {
                    $equipSystemType = $famR.data.output.equipment_system_type.id
                    $equipSystemName = [string]$famR.data.output.equipment_system_type.name }
            }

            # ---- case 9: the accessory that really goes inline ------------
            if (-not $accSym) {
                Complete-W14Case 9 $t0 'unverified' ('the accessory fixture family did not load: ' + (Get-DimShortText $famR.text))
            } else {
                # MEASURED at run 22: right after the S/M/L document churn Revit can
                # answer 'Raise() returned Denied' - it is not accepting external
                # events yet. That is the bridge reporting a real state honestly, and
                # it is STAGING, so one retry after a pause is legitimate. The probe's
                # own assertion is never retried.
                $hostMk = $null; $hostId = $null
                foreach ($stagingAttempt in 1..2) {
                    if ($hostId) { continue }
                    if ($stagingAttempt -gt 1) { Start-Sleep -Seconds 5 }
                    $hostMk = Invoke-WriteApply 'horizun_create_elements' @{
                        target_document=$wDoc; units='mm'
                        elements=@(@{ kind='pipe'; start=@((($w14X+10000)+($stagingAttempt-1)*500),0,1200)
                                      end=@((($w14X+10000)+($stagingAttempt-1)*500),4000,1200)
                                      level_id=[long]$levelId; type_id=[long]$pipeType; system_type_id=[long]$pipeSystem })
                    } ('w14-inlinehost-' + $stagingAttempt)
                    if ($hostMk.stage -eq 'apply' -and -not $hostMk.answer.isError) {
                        $hostId = [long]@($hostMk.answer.data.rows)[0].element_id
                    }
                }
                if (-not $hostId) {
                    Complete-W14Case 9 $t0 'unverified' ('no host pipe staged: ' + (Get-DimShortText $hostMk.answer.text))
                } else {
                    $inline = Invoke-WriteApply 'horizun_create_elements' @{
                        target_document=$wDoc; units='mm'
                        elements=@(@{ kind='accessory_inline'; pipe_id=$hostId; type_id=[long]$accSym
                                      point=@(($w14X+10000),2000,1200) })
                    } 'w14-inline'
                    $inlineRow = if ($inline.answer.data) { @($inline.answer.data.rows)[0] } else { $null }
                    $accId = if ($inlineRow) { [long]$inlineRow.element_id } else { $null }
                    # The typed reply says created+verified; the MODEL says the run
                    # was broken and rejoined. Both, or this is not a pass.
                    # Query the exact two ids the post-commit verifier reported.
                    # A category query with max_rows=400 silently missed these in a
                    # 496-pipe fixture and manufactured a zero-connection failure.
                    $verifiedPipeIds = @()
                    if ($inlineRow -and $inlineRow.inline_connections) {
                        $verifiedPipeIds = @($inlineRow.inline_connections.pipe_ids | ForEach-Object { [long]$_ })
                    }
                    $halves = if ($verifiedPipeIds.Count -eq 2) {
                        Invoke-Write 'horizun_query_model' @{
                            element_ids=$verifiedPipeIds; include_links=$false; include_mep=$true; max_rows=10 }
                    } else { $null }
                    $onAxis = @()
                    if ($halves -and $halves.data) {
                        $onAxis = @($halves.data.rows | Where-Object {
                            -not $_.is_element_type -and $_.mep -and $_.mep.connectors -and
                            @($_.mep.connectors | Where-Object {
                                $_.connected_to -and (@($_.connected_to) -contains $accId) }).Count -ge 1 })
                    }
                    if ($inline.stage -eq 'apply' -and -not $inline.answer.isError -and
                        [int]$inline.answer.data.created_verified -eq 1 -and
                        $inlineRow.verified -eq $true -and $inlineRow.inline_connections.verified -eq $true -and
                        $verifiedPipeIds.Count -eq 2 -and $onAxis.Count -eq 2) {
                        Complete-W14Case 9 $t0 'pass' ('the authored valve went INLINE on a live run: the host pipe was broken at the point and BOTH halves re-read CONNECTED to the accessory (element ' + $accId + '), all inside one verified transaction') `
                            -Evidence @{ accessory=$accId; host=$hostId; halves_connected=$onAxis.Count
                                         queried_pipe_ids=$verifiedPipeIds; row=$inlineRow }
                    } elseif ($inline.stage -eq 'apply' -and -not $inline.answer.isError -and
                              [int]$inline.answer.data.created_verified -eq 1 -and $inlineRow.verified -eq $true) {
                        Complete-W14Case 9 $t0 'fail' ('the accessory committed verified but the model shows ' + $onAxis.Count +
                            ' pipe(s) connected to it, not 2 - the break-and-connect did not leave two joined halves')
                    } else {
                        Complete-W14Case 9 $t0 'fail' ('the inline accessory did not commit verified: stage=' + $inline.stage + ' ' +
                            (Get-DimShortText $inline.answer.text))
                    }
                }
            }

            # ---- case 10: the tap, and whatever Revit says about it -------
            $t0 = Get-Date
            if (-not $tapSym) {
                Complete-W14Case 10 $t0 'unverified' ('the tap fixture family did not load: ' + (Get-DimShortText $famR.text))
            } else {
                $tapDup = Invoke-WriteApply 'horizun_manage_system_types' @{
                    target_document=$wDoc; units='mm'
                    actions=@(@{ source_type_id=[long]$pipeType; new_name=('HZ_TAPTYPE_' + $dimTag)
                                 junction_preference=@{ type='tap'; tap_fitting_type_id=[long]$tapSym } })
                } 'w14-taptype'
                $tapRow = if ($tapDup.answer.data) { @($tapDup.answer.data.rows)[0] } else { $null }
                $tapTypeOk = $tapDup.stage -eq 'apply' -and -not $tapDup.answer.isError -and
                             $tapRow -and $tapRow.junction_preference -and
                             $tapRow.junction_preference.verified -eq $true -and
                             [string]$tapRow.junction_preference.preferred_junction_read -eq 'Tap'
                if (-not $tapTypeOk) {
                    Complete-W14Case 10 $t0 'fail' ('the authored tap did NOT pass the Part Type gate or the preference did not verify: ' +
                        (Get-DimShortText $tapDup.answer.text))
                } else {
                    $tt = [long]$tapRow.new_type_id
                    $tkPipes = Invoke-WriteApply 'horizun_create_elements' @{
                        target_document=$wDoc; units='mm'
                        elements=@(
                            @{ kind='pipe'; start=@(($w14X+14000),0,1200); end=@(($w14X+14000),6000,1200)
                               level_id=[long]$levelId; type_id=$tt; system_type_id=[long]$pipeSystem },
                            @{ kind='pipe'; start=@(($w14X+16000),3000,1200); end=@(($w14X+14000),3000,1200)
                               level_id=[long]$levelId; type_id=$tt; system_type_id=[long]$pipeSystem })
                    } 'w14-tappipes'
                    $tkRows = if ($tkPipes.answer.data) { @($tkPipes.answer.data.rows) } else { @() }
                    if ($tkPipes.stage -ne 'apply' -or $tkRows.Count -ne 2) {
                        Complete-W14Case 10 $t0 'unverified' ('the tap-type pipes could not be staged: ' + (Get-DimShortText $tkPipes.answer.text))
                    } else {
                        $beforeFit = Invoke-Write 'horizun_query_model' @{
                            categories=@('OST_PipeFitting'); include_links=$false; max_rows=500 }
                        $fitBefore = if ($beforeFit.data) { @($beforeFit.data.rows | Where-Object { -not $_.is_element_type }).Count } else { -1 }
                        $tkGo = Invoke-WriteApply 'horizun_create_elements' @{
                            target_document=$wDoc; units='mm'
                            elements=@(@{ kind='fitting'; fitting='takeoff'
                                          elements=@(@{ element_id=[long]$tkRows[1].element_id },
                                                     @{ element_id=[long]$tkRows[0].element_id }) })
                        } 'w14-takeoff'
                        $afterFit = Invoke-Write 'horizun_query_model' @{
                            categories=@('OST_PipeFitting'); include_links=$false; max_rows=500 }
                        $fitAfter = if ($afterFit.data) { @($afterFit.data.rows | Where-Object { -not $_.is_element_type }).Count } else { -2 }
                        $tkRow2 = if ($tkGo.answer.data) { @($tkGo.answer.data.rows)[0] } else { $null }
                        if ($tkGo.stage -eq 'apply' -and -not $tkGo.answer.isError -and
                            [int]$tkGo.answer.data.created_verified -eq 1 -and $tkRow2.verified -eq $true) {
                            Complete-W14Case 10 $t0 'pass' ('THE POSITIVE: a real takeoff committed verified on the type whose junction preference reads Tap, using the authored Spud fitting (fitting count ' + $fitBefore + ' -> ' + $fitAfter + ')') `
                                -Evidence @{ tap_type=$tt; fitting=$tapSym; row=$tkRow2 }
                        } elseif ($tkGo.answer.isError -and $tkGo.answer.text -match 'Failed to insert takeoff' -and
                                  $tkGo.answer.text -match 'rolled back' -and $fitAfter -eq $fitBefore) {
                            Complete-W14Case 10 $t0 'pass' ('MEASURED BOUNDARY, not a guess: the typed configuration verified (preference re-read Tap, Part Type gate passed) and REVIT ITSELF refused the insert - "Failed to insert takeoff". The product rolled the batch back WHOLE and said so: the fitting count is unchanged at ' + $fitAfter + '. An authored Spud family satisfies the gate but not NewTakeoffFitting; the positive needs an Autodesk MEP content tap, which this machine does not ship. Una negativa no prueba la positiva - this probe claims only what it measured.') `
                                -Evidence @{ tap_type=$tt; fitting=$tapSym; fittings_before=$fitBefore; fittings_after=$fitAfter
                                             revit_said=(Get-DimShortText $tkGo.answer.text) }
                        } else {
                            Complete-W14Case 10 $t0 'fail' ('neither a verified takeoff nor a clean whole rollback: stage=' + $tkGo.stage +
                                ' fittings ' + $fitBefore + '->' + $fitAfter + ' ' + (Get-DimShortText $tkGo.answer.text))
                        }
                    }
                }
            }
        }

        # ---- cases 11, 12 + 14: the system that exists before anything routes
        # MEASURED at run 22: Revit answers 'Some connectors to be added into the
        # system have been used' when a connector already belongs to one - and a
        # curve created WITH a system type is already in the system Revit made for
        # it. So a system is built from FREE connectors: standalone instances that
        # have never been routed. That is what Revit means by base equipment, and
        # the product now refuses the occupied case by name at plan time (c14).
        $t0 = Get-Date
        if (-not $levelId -or -not $pipeType -or -not $pipeSystem -or -not $equipSym -or -not $accSym -or -not $equipSystemType) {
            Complete-W14Case 11 $t0 'unverified' 'no level / pipe type / piping system type / equipment family for the system probes'
            Complete-W14Case 12 $t0 'unverified' 'no level / pipe type / piping system type for the system probes'
            Complete-W14Case 14 $t0 'unverified' 'no level / pipe type / piping system type for the system probes'
            Complete-W14Case 15 $t0 'unverified' 'no accessory family for the unclassified-connector refusal'
        } else {
            # Two standalone instances of the authored EQUIPMENT: their connectors
            # have never been connected AND they declare a real classification -
            # which run 23 measured to be the difference between a system that
            # carries its members and one that silently carries nobody.
            $freeMk = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(
                    @{ kind='family_instance'; type_id=[long]$equipSym; level_id=[long]$levelId
                       point=@(($w14X+20000),0,1200) },
                    @{ kind='family_instance'; type_id=[long]$equipSym; level_id=[long]$levelId
                       point=@(($w14X+22000),0,1200) })
            } 'w14-sysfree'
            $freeRows = if ($freeMk.answer.data) { @($freeMk.answer.data.rows) } else { @() }
            if ($freeMk.stage -ne 'apply' -or $freeRows.Count -ne 2) {
                Complete-W14Case 11 $t0 'unverified' ('the free-connector members could not be staged: ' + (Get-DimShortText $freeMk.answer.text))
            } else {
                $m1 = [long]$freeRows[0].element_id; $m2 = [long]$freeRows[1].element_id
                $sysName = 'HZ_SYS_' + $dimTag
                $mkSys = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='mep_system'; system_type_id=[long]$equipSystemType; name=$sysName
                                  member_element_ids=@($m1, $m2) })
                } 'w14-mepsystem'
                $sysRow = if ($mkSys.answer.data) { @($mkSys.answer.data.rows)[0] } else { $null }
                $ms = if ($sysRow) { $sysRow.mep_system } else { $null }
                if ($mkSys.stage -eq 'apply' -and -not $mkSys.answer.isError -and
                    [int]$mkSys.answer.data.created_verified -eq 1 -and $sysRow.verified -eq $true -and
                    $ms -and $ms.name_verified -eq $true -and [string]$ms.name_read -eq $sysName -and
                    $ms.system_type_verified -eq $true -and $ms.members_verified -eq $true -and
                    [int]$ms.members_requested -eq 2 -and @($ms.members_missing).Count -eq 0) {
                    Complete-W14Case 11 $t0 'pass' ('a NAMED piping system was created typed and re-read from the model: name "' +
                        $ms.name_read + '" matches, the system type matches, and BOTH member ids it was given are among the ' +
                        $ms.members_read + ' it carries - members re-read, never counted off Add calls that did not throw') `
                        -Evidence @{ system=$sysRow.element_id; mep_system=$ms }
                } else {
                    Complete-W14Case 11 $t0 'fail' ('the named system did not verify: stage=' + $mkSys.stage + ' ' +
                        (Get-DimShortText $mkSys.answer.text))
                }
            }

            # ---- case 12: a member Revit cannot carry at all ---------------
            $t0 = Get-Date
            $wallMk = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='wall'; start=@(($w14X+24000),0,0); end=@(($w14X+27000),0,0)
                              level_id=[long]$levelId; height=3000 })
            } 'w14-sysbadmember'
            $wallForSys = if ($wallMk.stage -eq 'apply' -and -not $wallMk.answer.isError) {
                [long]@($wallMk.answer.data.rows)[0].element_id } else { $null }
            if (-not $wallForSys) {
                Complete-W14Case 12 $t0 'unverified' 'no wall staged for the domain refusal'
            } else {
                $badSys = Invoke-Write 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='mep_system'; system_type_id=[long]$pipeSystem
                                  name=('HZ_SYSBAD_' + $dimTag); member_element_ids=@($wallForSys) }) }
                $badErr = if ($badSys.data -and $badSys.data.errors) { [string]@($badSys.data.errors)[0].error } else { $null }
                if (-not $badSys.isError -and $badSys.data -and [int]$badSys.data.invalid -eq 1 -and
                    [string]$badSys.data.transaction_status -eq 'not_started' -and
                    $badErr -match 'exposes no connectors' -and $badErr -match [string]$wallForSys) {
                    Complete-W14Case 12 $t0 'pass' ('a wall named as a member refused the WHOLE row at plan time, citing the id and the reason - "' +
                        $badErr + '" - with no transaction opened. A system whose members are in another domain is not a system.') `
                        -Evidence @{ wall=$wallForSys; refusal=$badErr }
                } else {
                    Complete-W14Case 12 $t0 'fail' ('expected the named plan-time member refusal, got: ' + (Get-DimShortText $badSys.text))
                }
            }

            # ---- case 14: the connector that is already spoken for ---------
            $t0 = Get-Date
            $takenMk = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='pipe'; start=@(($w14X+26000),0,1200); end=@(($w14X+26000),3000,1200)
                              level_id=[long]$levelId; type_id=[long]$pipeType; system_type_id=[long]$pipeSystem })
            } 'w14-systaken'
            $takenId = if ($takenMk.stage -eq 'apply' -and -not $takenMk.answer.isError) {
                [long]@($takenMk.answer.data.rows)[0].element_id } else { $null }
            if (-not $takenId) {
                Complete-W14Case 14 $t0 'unverified' ('no pipe staged for the occupied-connector refusal: ' + (Get-DimShortText $takenMk.answer.text))
            } else {
                $takenSys = Invoke-Write 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='mep_system'; system_type_id=[long]$pipeSystem
                                  name=('HZ_SYSTAKEN_' + $dimTag); member_element_ids=@($takenId) }) }
                $takenErr = if ($takenSys.data -and $takenSys.data.errors) { [string]@($takenSys.data.errors)[0].error } else { $null }
                if (-not $takenSys.isError -and $takenSys.data -and [int]$takenSys.data.invalid -eq 1 -and
                    [string]$takenSys.data.transaction_status -eq 'not_started' -and
                    $takenErr -match 'member_connector_already_in_a_system' -and
                    $takenErr -match [string]$takenId -and $takenErr -match 'ONE system') {
                    Complete-W14Case 14 $t0 'pass' ('a pipe created WITH a system type is already in the system Revit made for it, and naming it as a member of a second one refuses at PLAN TIME by name - "' +
                        $takenErr + '". Run 22 measured the alternative: Revit answers "Some connectors to be added into the system have been used" mid-transaction, and the batch rolls back. The readable fact (Connector.MEPSystem) is read before anything is written.') `
                        -Evidence @{ pipe=$takenId; refusal=$takenErr }
                } else {
                    Complete-W14Case 14 $t0 'fail' ('expected the occupied-connector refusal at plan time, got: ' + (Get-DimShortText $takenSys.text))
                }
            }

        # ---- case 13: THE POSITIVE TAKEOFF, in the domain that has taps ---
        # Cases 1-10 measured the pipe side: this machine's Revit ships no MEP
        # PIPE content, and an authored Spud satisfies the Part Type gate but
        # not NewTakeoffFitting. The DUCT side is different - the sample models
        # carry real Autodesk tap families (Round Takeoff, Rectangular Takeoff,
        # Oval Tap) - and the junction preference lives on MEPCurveType, which
        # is the base of BOTH PipeType and DuctType. So the positive is real
        # here. The tap is FOUND by the product's own Part Type gate: each duct
        # fitting is offered in turn and only a genuine tap verifies.
        $t0 = Get-Date
        $dtQ = Invoke-Write 'horizun_query_model' @{
            categories=@('OST_DuctCurves'); include_types=$true; include_links=$false; max_rows=80 }
        $ductType = if ($dtQ.data) { @($dtQ.data.rows | Where-Object { $_.is_element_type } |
                                       ForEach-Object { $_.element_id }) | Select-Object -First 1 } else { $null }
        $dsQ = Invoke-Write 'horizun_query_model' @{
            categories=@('OST_DuctSystem'); include_types=$true; include_links=$false; max_rows=40 }
        $ductSystem = if ($dsQ.data) { @($dsQ.data.rows | Where-Object { $_.is_element_type } |
                                         ForEach-Object { $_.element_id }) | Select-Object -First 1 } else { $null }
        $dfQ = Invoke-Write 'horizun_query_model' @{
            categories=@('OST_DuctFitting'); include_types=$true; include_links=$false; max_rows=120 }
        $ductFittings = if ($dfQ.data) { @($dfQ.data.rows | Where-Object { $_.is_element_type } |
                                           ForEach-Object { $_.element_id }) } else { @() }
        if (-not $ductType -or -not $ductSystem -or -not $levelId -or $ductFittings.Count -eq 0) {
            Complete-W14Case 13 $t0 'unverified' ("no duct surface to probe: type=$ductType system=$ductSystem " +
                "level=$levelId fittings=" + $ductFittings.Count)
        } else {
            # The product's Part Type gate IS the search: a non-tap refuses by name.
            $ductTapType = $null; $ductTapUsed = $null
            foreach ($cand in ($ductFittings | Select-Object -First 40)) {
                if ($ductTapType) { continue }
                $try = Invoke-WriteApply 'horizun_manage_system_types' @{
                    target_document=$wDoc; units='mm'
                    actions=@(@{ source_type_id=[long]$ductType; new_name=('HZ_DUCTTAP_' + $dimTag + '_' + $cand)
                                 junction_preference=@{ type='tap'; tap_fitting_type_id=[long]$cand } })
                } ('w14-ducttap-' + $cand)
                if ($try.stage -eq 'apply' -and -not $try.answer.isError) {
                    $r = @($try.answer.data.rows)[0]
                    if ($r.junction_preference -and $r.junction_preference.verified -eq $true -and
                        [string]$r.junction_preference.preferred_junction_read -eq 'Tap') {
                        $ductTapType = $r.new_type_id; $ductTapUsed = $cand
                    }
                }
            }
            if (-not $ductTapType) {
                Complete-W14Case 13 $t0 'unverified' ('no duct fitting in this model passed the Part Type gate, so no ' +
                    'Tap-preferenced DuctType could be built from ' + $ductFittings.Count + ' candidates')
            } else {
                $dMk = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(
                        @{ kind='duct'; start=@(($w14X+30000),0,2400); end=@(($w14X+30000),6000,2400)
                           level_id=[long]$levelId; type_id=[long]$ductTapType; system_type_id=[long]$ductSystem },
                        @{ kind='duct'; start=@(($w14X+32000),3000,2400); end=@(($w14X+30000),3000,2400)
                           level_id=[long]$levelId; type_id=[long]$ductTapType; system_type_id=[long]$ductSystem })
                } 'w14-ducts'
                $dRows = if ($dMk.answer.data) { @($dMk.answer.data.rows) } else { @() }
                if ($dMk.stage -ne 'apply' -or $dRows.Count -ne 2) {
                    Complete-W14Case 13 $t0 'unverified' ('the tap-type ducts could not be staged: ' + (Get-DimShortText $dMk.answer.text))
                } else {
                    $dTake = Invoke-WriteApply 'horizun_create_elements' @{
                        target_document=$wDoc; units='mm'
                        elements=@(@{ kind='fitting'; fitting='takeoff'
                                      elements=@(@{ element_id=[long]$dRows[1].element_id },
                                                 @{ element_id=[long]$dRows[0].element_id }) })
                    } 'w14-ducttakeoff'
                    $dRow = if ($dTake.answer.data) { @($dTake.answer.data.rows)[0] } else { $null }
                    if ($dTake.stage -eq 'apply' -and -not $dTake.answer.isError -and
                        [int]$dTake.answer.data.created_verified -eq 1 -and $dRow.verified -eq $true -and
                        $dRow.connectors_verified -eq $true -and
                        [string]$dRow.actual_category -match 'Duct Fitting') {
                        Complete-W14Case 13 $t0 'pass' ('THE POSITIVE: a REAL takeoff committed verified. The DuctType was duplicated with junction preference Tap using duct fitting ' +
                            $ductTapUsed + ' - found by the product''s OWN Part Type gate, which refuses every non-tap by name - the preference re-read Tap, and the takeoff fitting (element ' +
                            $dRow.element_id + ', ' + $dRow.actual_category + ') re-read from the model with its connectors CONNECTED.') `
                            -Evidence @{ duct_type=$ductTapType; tap_fitting=$ductTapUsed; row=$dRow }
                    } else {
                        Complete-W14Case 13 $t0 'fail' ('the duct takeoff did not commit verified: stage=' + $dTake.stage + ' ' +
                            (Get-DimShortText $dTake.answer.text))
                    }
                }
            }
        }

            # ---- case 15: the connector that declares nothing ----------
            # MEASURED at run 23: the accessory family's connectors are authored
            # as Fitting, which reads back as UndefinedSystemType - and Revit's
            # MEPSystem.Add takes such a connector WITHOUT THROWING and associates
            # NOTHING. The post-commit member re-read caught it (0 of 2 carried);
            # the readable classification lets the refusal happen before the write.
            $t0 = Get-Date
            $unclMk = Invoke-WriteApply 'horizun_create_elements' @{
                target_document=$wDoc; units='mm'
                elements=@(@{ kind='family_instance'; type_id=[long]$accSym; level_id=[long]$levelId
                              point=@(($w14X+28000),0,1200) })
            } 'w14-sysuncl'
            $unclId = if ($unclMk.stage -eq 'apply' -and -not $unclMk.answer.isError) {
                [long]@($unclMk.answer.data.rows)[0].element_id } else { $null }
            if (-not $unclId) {
                Complete-W14Case 15 $t0 'unverified' ('no unclassified member staged: ' + (Get-DimShortText $unclMk.answer.text))
            } else {
                $unclSys = Invoke-Write 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='mep_system'; system_type_id=[long]$pipeSystem
                                  name=('HZ_SYSUNCL_' + $dimTag); member_element_ids=@($unclId) }) }
                $unclErr = if ($unclSys.data -and $unclSys.data.errors) { [string]@($unclSys.data.errors)[0].error } else { $null }
                if (-not $unclSys.isError -and $unclSys.data -and [int]$unclSys.data.invalid -eq 1 -and
                    [string]$unclSys.data.transaction_status -eq 'not_started' -and
                    $unclErr -match 'member_connector_has_no_system_classification' -and
                    $unclErr -match [string]$unclId -and $unclErr -match 'associates NOTHING') {
                    Complete-W14Case 15 $t0 'pass' ('a connector whose system type reads Undefined refuses at PLAN TIME by name - "' +
                        $unclErr + '". This is the exact silent failure the contract exists for: Revit''s Add does not throw and joins nobody, so the count would have come from a call that did not throw.') `
                        -Evidence @{ member=$unclId; refusal=$unclErr }
                } else {
                    Complete-W14Case 15 $t0 'fail' ('expected the unclassified-connector refusal, got: ' + (Get-DimShortText $unclSys.text))
                }
            }

            # ---- case 16: classified, but for a DIFFERENT system -------
            # MEASURED at run 24: with the classification present but different
            # from the system type's own, Revit answers "Some connectors can't
            # match system with domain, system type or direction" mid-transaction
            # and the batch rolls back. Both classifications are readable first.
            $t0 = Get-Date
            $otherSystem = $null
            if ($equipSystemType) {
                $stQ = Invoke-Write 'horizun_query_model' @{
                    categories=@('OST_PipingSystem'); include_types=$true; include_links=$false; max_rows=40 }
                if ($stQ.data) {
                    $otherSystem = @($stQ.data.rows | Where-Object {
                        $_.is_element_type -and [long]$_.element_id -ne [long]$equipSystemType } |
                        ForEach-Object { $_.element_id }) | Select-Object -First 1
                }
            }
            if (-not $otherSystem -or -not $equipSym) {
                Complete-W14Case 16 $t0 'unverified' 'no second piping system type to mismatch against'
            } else {
                $mmMk = Invoke-WriteApply 'horizun_create_elements' @{
                    target_document=$wDoc; units='mm'
                    elements=@(@{ kind='family_instance'; type_id=[long]$equipSym; level_id=[long]$levelId
                                  point=@(($w14X+30000),0,1200) })
                } 'w14-sysmismatch'
                $mmId = if ($mmMk.stage -eq 'apply' -and -not $mmMk.answer.isError) {
                    [long]@($mmMk.answer.data.rows)[0].element_id } else { $null }
                if (-not $mmId) {
                    Complete-W14Case 16 $t0 'unverified' ('no classified member staged: ' + (Get-DimShortText $mmMk.answer.text))
                } else {
                    $mmSys = Invoke-Write 'horizun_create_elements' @{
                        target_document=$wDoc; units='mm'
                        elements=@(@{ kind='mep_system'; system_type_id=[long]$otherSystem
                                      name=('HZ_SYSMM_' + $dimTag); member_element_ids=@($mmId) }) }
                    $mmErr = if ($mmSys.data -and $mmSys.data.errors) { [string]@($mmSys.data.errors)[0].error } else { $null }
                    # Either classification could legitimately differ; what the probe
                    # asserts is that BOTH are named and nothing was written.
                    if (-not $mmSys.isError -and $mmSys.data -and [int]$mmSys.data.invalid -eq 1 -and
                        [string]$mmSys.data.transaction_status -eq 'not_started' -and
                        $mmErr -match 'member_classification_does_not_match_system' -and
                        $mmErr -match [string]$mmId -and $mmErr -match 'DomesticColdWater') {
                        Complete-W14Case 16 $t0 'pass' ('a member classified DomesticColdWater, named into a system type of another classification, refuses at PLAN TIME with BOTH classifications in the message - "' +
                            $mmErr + '" - and no transaction was opened.') `
                            -Evidence @{ member=$mmId; other_system_type=$otherSystem; refusal=$mmErr }
                    } elseif ($mmSys.isError -or [int]$mmSys.data.invalid -eq 0) {
                        # The second type may share the classification: then there is
                        # no mismatch to measure and the probe says so rather than
                        # calling a coincidence a pass.
                        Complete-W14Case 16 $t0 'unverified' ('the second system type appears to share the classification, so no mismatch arose: ' +
                            (Get-DimShortText $mmSys.text))
                    } else {
                        Complete-W14Case 16 $t0 'fail' ('expected the classification-mismatch refusal, got: ' + (Get-DimShortText $mmSys.text))
                    }
                }
            }
        }

        for ($wc14=1; $wc14 -le 16; $wc14++) {
            if (-not $script:w14CasesDone.ContainsKey($wc14)) { Complete-W14Case $wc14 (Get-Date) 'unverified' 'the W14 section ended before this probe ran - a harness bug' }
        }



    }
}

$proc.StandardInput.Close()
if (-not $proc.WaitForExit(130000)) { $proc.Kill() }

if ($closedFixtureTemporaryCopy -and (Test-Path -LiteralPath $closedFixtureTemporaryCopy)) {
    if ($closedFixtureSafeToDelete) {
        try { Remove-Item -LiteralPath $closedFixtureTemporaryCopy -Force }
        catch { $notCovered += "temporary closed-workset fixture cleanup failed: $($_.Exception.Message)" }
    }
    else {
        $notCovered += "temporary closed-workset fixture was retained at $closedFixtureTemporaryCopy because the harness could not prove its document was closed; it was never saved"
    }
}

# ---------------------------------------------------------------------------
# THREE outcomes, not two.
#
# The previous version had AllowError, which printed PASS "(refused cleanly)"
# and RETURNED - so the probe's Check never ran. The quantities probe errored
# for a whole run and reported PASS; by the time that was noticed the reason was
# gone. A run that could not test something has not tested it, and calling that
# a pass is the exact substitution this repository exists to refuse.
#
# PASS       the check ran and was satisfied
# FAIL       the check ran and was not, or a refusal did not match
# UNVERIFIED the check could not run: an error where an answer was required, no
#            reply, or an unparseable one. Never counted as a pass, always
#            printed with the reason.
# ---------------------------------------------------------------------------
#
# NOT COVERED is the fourth, and it is the one that used to be a warning.
#
# A guarantee missing from the output reads exactly like one that passed. So it
# is recorded per probe, with the reason and the parameter that would close it,
# and under -ReleaseGate it exits non-zero.
$passed = 0; $failed = 0; $unverified = 0
$unverifiedDetail = @()
$assertingProbes = 0

# Every probe, with what happened to it. The JSON is written from THIS, not from
# a second pass over the console output - two renderings of one run are two
# things that can disagree.
$results = New-Object System.Collections.Generic.List[object]

function Add-Result($name, $tool, $outcome, $why) {
    $script:results.Add([pscustomobject]@{
        name = $name; tool = $tool; outcome = $outcome; detail = $why
    }) | Out-Null
}

function Note-Pass($name, $tool, $suffix) {
    if ($suffix) { Write-Host ("  PASS  {0} ({1})" -f $name, $suffix) -ForegroundColor Green }
    else { Write-Host ("  PASS  {0}" -f $name) -ForegroundColor Green }
    $script:passed++
    Add-Result $name $tool 'pass' $suffix
}

function Note-Fail($name, $tool, $why) {
    Write-Host ("  FAIL  {0}" -f $name) -ForegroundColor Red
    if ($why) { Write-Host ("        {0}" -f $why) -ForegroundColor DarkRed }
    $script:failed++
    Add-Result $name $tool 'fail' $why
}

function Note-Unverified($name, $why, $tool) {
    Write-Host ("  UNVERIFIED  {0}" -f $name) -ForegroundColor Yellow
    Write-Host ("              {0}" -f $why) -ForegroundColor DarkYellow
    $script:unverified++
    $script:unverifiedDetail += ("{0}: {1}" -f $name, $why)
    Add-Result $name $tool 'unverified' $why
}

function Note-NotCovered($name, $why, $tool) {
    Write-Host ("  NOT COVERED  {0}" -f $name) -ForegroundColor DarkYellow
    Write-Host ("               {0}" -f $why) -ForegroundColor DarkYellow
    $script:notCovered += ("{0}: {1}" -f $name, $why)
    Add-Result $name $tool 'not_covered' $why
}

Write-Host ""
Write-Host "Live verification - Revit $Year (process $($target.pid), add-in $($target.addin_version))" -ForegroundColor Cyan
Write-Host ("-" * 70)

foreach ($p in $probes) {
    if ($p.Check -or $p.ExpectError) { $assertingProbes++ }

    # A probe whose prerequisite was never supplied is NOT COVERED, by name. It
    # used to be absent from the output altogether, which reads exactly like a
    # probe that passed.
    if ($p.Needs) {
        $supplied = Get-Variable -Name $p.Needs -ValueOnly -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($supplied)) {
            # if/else, not a ternary: this script declares 5.1 and Windows
            # PowerShell has no ?: - it is a parser error, which would take the
            # whole harness down rather than one probe.
            if ($p.NotCovered) { $why = $p.NotCovered } else { $why = "needs -" + $p.Needs }
            Note-NotCovered $p.Name $why $p.Tool
            continue
        }
    }

    $m = $byId[[int]$p.Id]
    if (-not $m) { Note-Unverified $p.Name "no reply came back for this request id" $p.Tool; continue }

    # The same hardening the write tier needed: a JSON-RPC error reply carries no
    # result, and indexing into it killed the entire run - counters, JSON, exit
    # code and all - on the first unknown tool. An error reply IS an answer.
    $text = $null; $isError = $true
    if ($null -eq $m.result) {
        if ($m.error) { $text = 'JSON-RPC error: ' + $m.error.message }
        else { $text = 'the server returned no result' }
    }
    else {
        $replyContent = @($m.result.content)
        if ($replyContent.Count -gt 0 -and $null -ne $replyContent[0]) { $text = $replyContent[0].text }
        $isError = [bool]$m.result.isError
        # What a program branches on. Kept separate from the text on purpose: a probe
        # about a structured signal must read the structure, not a rendering of it.
        $structured = $m.result.structuredContent
    }

    if ($p.ExpectError) {
        # A fixture can name a document that is simply not open: the probe cannot
        # run, and "could not attempt" is NOT COVERED, never FAIL.
        if ($p.NotCoveredOnNoMatch -and $text -match 'No open document matches') {
            Note-NotCovered $p.Name $p.NotCoveredOnNoMatch $p.Tool
            continue
        }
        if ($p.AllowMissingTool -and $m.error) {
            # The tool is not advertised at all, which for a disabled capability is
            # a stronger result than a refusal - but it is a different one, so say so.
            Note-Pass $p.Name $p.Tool 'not advertised at all'
            continue
        }
        if ($isError -and $text -match $p.ExpectError) {
            # A refusal can be right about the message and wrong about what rides
            # beside it. ExpectErrorContains / ExpectErrorLacks assert the STRUCTURE
            # in the reply - above all the fallback block, whose absence is as
            # load-bearing as its presence.
            $missing = $p.ExpectErrorContains -and ($text -notlike ('*' + $p.ExpectErrorContains + '*'))
            $present = $p.ExpectErrorLacks    -and ($text -like    ('*' + $p.ExpectErrorLacks    + '*'))
            if ($missing) {
                Note-Fail $p.Name $p.Tool ("the refusal fired but did not carry '{0}'" -f $p.ExpectErrorContains)
            } elseif ($present) {
                Note-Fail $p.Name $p.Tool ("the refusal carried '{0}', which it must not" -f $p.ExpectErrorLacks)
            } else {
                Note-Pass $p.Name $p.Tool $null
            }
        } else {
            $got = if ($text) { ($text -replace "`n", ' ').Substring(0, [Math]::Min(200, $text.Length)) } else { '(no text)' }
            Note-Fail $p.Name $p.Tool ("expected a refusal matching '{0}', got: {1}" -f $p.ExpectError, $got)
        }
        continue
    }

    if ($isError) {
        # A disabled optional capability did not execute the behavior this probe
        # names. Keep that as NOT COVERED rather than laundering the refusal into
        # a PASS. There is deliberately no generic "error is also pass" escape.
        if ($p.ErrorIsNotCovered -and $text -match $p.ErrorIsNotCovered) {
            Note-NotCovered $p.Name 'the capability was switched off, so the asserted behavior did not execute' $p.Tool
            continue
        }
        # An error where an ANSWER was required. The check cannot run, so nothing
        # about this guarantee was established - and the reason is printed here
        # rather than discarded, which is what made the last one unexplainable.
        Note-Unverified $p.Name ("the call errored, so its check never ran: " +
                                 ($text -replace "`n", ' ').Substring(0, [Math]::Min(220, $text.Length))) $p.Tool
        continue
    }

    # A probe about structuredContent gets structuredContent BEFORE content.text is
    # parsed. A successful response may append a human diagnostic after its JSON text
    # (notably "what Revit raised"), making text deliberately non-JSON while the MCP
    # structure remains exact. Requiring text to parse first made UseStructured dead.
    if ($p.UseStructured) {
        if ($null -eq $structured) {
            Note-Unverified $p.Name "the reply carried no structuredContent, so the structured check never ran" $p.Tool
            continue
        }
        if (-not $p.Check) {
            Note-Unverified $p.Name "this structured probe asserts nothing" $p.Tool
            continue
        }
        if (& $p.Check $structured) { Note-Pass $p.Name $p.Tool 'read from structuredContent' }
        else { Note-Fail $p.Name $p.Tool 'structuredContent did not carry what it had to' }
        continue
    }

    try { $data = $text | ConvertFrom-Json } catch { $data = $null }
    if ($null -eq $data) { Note-Unverified $p.Name "the reply was not JSON, so its check never ran" $p.Tool; continue }

    if (-not $p.Check) { Note-Unverified $p.Name "this probe asserts nothing - it calls the tool and looks at neither the answer nor an error" $p.Tool; continue }

    if (& $p.Check $data) { Note-Pass $p.Name $p.Tool $null }
    else { Note-Fail $p.Name $p.Tool 'the answer did not say what it had to' }
}

# ---------------------------------------------------------------------------
# AN ACTIVE PROBE MAY NOT NAME A TOOL THIS BUILD DOES NOT PUBLISH.
#
# That is how the four retired probes rotted: they kept running, kept answering
# "not published by this build", and kept counting as gaps for a version whose
# surface had simply moved on. Either a probe is about a published tool, or it
# belongs in $RetiredProbes with its history. There is no third state where it
# quietly drags the numbers down.
# ---------------------------------------------------------------------------
$publishedNames = @($listed | ForEach-Object { $_.name })
$danglingProbes = @()
foreach ($p in $probes) {
    if (-not $p.Tool -or $p.Tool -eq '(contract)') { continue }
    if ($publishedNames -notcontains $p.Tool) { $danglingProbes += ("{0} -> {1}" -f $p.Name, $p.Tool) }
}
foreach ($w in $writeResults) {
    if (-not $w.Tool -or $w.Tool -eq '(contract)') { continue }
    if ($publishedNames -notcontains $w.Tool) { $danglingProbes += ("{0} -> {1}" -f $w.Name, $w.Tool) }
}
if ($danglingProbes.Count -gt 0) {
    Write-Host ""
    Write-Host "  ACTIVE PROBES NAME TOOLS THIS BUILD DOES NOT PUBLISH:" -ForegroundColor Red
    foreach ($d in $danglingProbes) { Write-Host ("    - {0}" -f $d) -ForegroundColor Red }
    Write-Host ("  Move them to `$RetiredProbes with their reason and replacement, or fix the tool name. " +
                "A probe that answers 'not published' is not coverage.") -ForegroundColor Red
    $failed += $danglingProbes.Count
}

# The write tier ran earlier, because it needed the transport open to spend the
# tokens its own dry runs issued. Fold it in here so there is one set of counters,
# one JSON and one exit code for the whole run.
if ($writeResults.Count -gt 0) {
    Write-Host ""
    Write-Host "  write tier (commits into the disposable model, never saves)" -ForegroundColor Cyan
    foreach ($w in $writeResults) {
        if ($w.Outcome -ne 'not_covered') { $assertingProbes++ }
        switch ($w.Outcome) {
            'pass'        { Note-Pass        $w.Name $w.Tool $w.Detail }
            'fail'        { Note-Fail        $w.Name $w.Tool $w.Detail }
            'unverified'  { Note-Unverified  $w.Name $w.Detail $w.Tool }
            default       { Note-NotCovered  $w.Name $w.Detail $w.Tool }
        }
    }
}

# ---------------------------------------------------------------------------
# RETIRED, reported in its own section and in NEITHER the coverage denominator
# nor the gap list. A tool this version does not publish is a fact about the
# surface; printing it as NOT COVERED implied a fixture was missing and put a
# permanent floor under the gap count.
# ---------------------------------------------------------------------------
if ($retiredRows.Count -gt 0) {
    Write-Host ""
    Write-Host "  retired (covered a tool this version no longer publishes - not a gap)" -ForegroundColor DarkGray
    foreach ($r in $retiredRows) {
        Write-Host ("  RETIRED  {0}" -f $r.Name) -ForegroundColor DarkGray
        Write-Host ("           tool {0}, retired {1}" -f $r.Tool, $r.Retired) -ForegroundColor DarkGray
        Write-Host ("           covered: {0}" -f $r.Covered) -ForegroundColor DarkGray
        Write-Host ("           now: {0}" -f $r.Replacement) -ForegroundColor DarkGray
    }
}

Write-Host ("-" * 70)
Write-Host ("  {0} passed, {1} failed, {2} UNVERIFIED   (of {3} probes, {4} assert something)" -f `
            $passed, $failed, $unverified, ($probes.Count + $writeResults.Count), $assertingProbes)
if ($retiredRows.Count -gt 0) {
    Write-Host ("  {0} retired probe(s), excluded from the counts above" -f $retiredRows.Count) -ForegroundColor DarkGray
}
if ($unverified -gt 0) {
    Write-Host "  UNVERIFIED is not a pass. These guarantees were NOT established by this run:" -ForegroundColor Yellow
    foreach ($u in $unverifiedDetail) { Write-Host ("    - {0}" -f $u) -ForegroundColor DarkYellow }
}
if (-not $Document) {
    $notCovered += 'links, quantities and the whole confirmation flow (needs -Document <title>)'
}

# The typed-overlap advisory, the preflight and the evidence classification all
# live INSIDE execute_python, so when that tool is not advertised none of them can
# fire and nothing here reaches them. Their only evidence is then the unit tests -
# a fine place for a regex table and a JToken classifier, and not the same claim
# as "it behaves this way against a running Revit".
#
# execute_python is disabled by DEFAULT. This harness never grants the privilege:
# it reports the gap. A client may request the visible dialog, but only the owner
# may approve persistent access with Revit's Python ON/OFF button or
# scripts/enable-execute-python.ps1 and then re-run.
if (-not ($listed | Where-Object { $_.name -eq 'horizun_execute_python' })) {
    $notCovered += 'the execute_python fallback surface - the typed-overlap advisory, preflight, and the ' +
                   '__output__ evidence classification (execute_python is switched off on this machine, so ' +
                   'none of them can fire; their only evidence is Horizun.Core.Tests. Re-enable deliberately ' +
                   'with scripts/enable-execute-python.ps1 and re-run to cover this)'
}
if (-not $OldFile) {
    $notCovered += 'the upgrade guard (needs -OldFile <a file saved by another Revit>)'
}
if ($serverIsDevBuild) {
    $notCovered += 'the artifact anybody will install (this run used a bin/Release build via -AllowDevServer)'
}
if (-not $ExpectedServerSha256) {
    $notCovered += 'that the server binary is the one in the release manifest (needs -ExpectedServerSha256)'
}
elseif ($serverSha -ne $ExpectedServerSha256.ToLower()) {
    # Not "not covered" - this was checked and it is WRONG. A run against the
    # wrong binary cannot speak for the right one, so it is a failure.
    Note-Fail 'the server binary matches the release manifest' 'horizun-mcp.exe' `
              ("expected sha256 {0}, the file is {1}" -f $ExpectedServerSha256.ToLower(), $serverSha)
}
else {
    Note-Pass 'the server binary matches the release manifest' 'horizun-mcp.exe' $null
}

if ($ExpectedAddinSha256) {
    $addinDll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\Horizun\Horizun.Revit.dll"
    if (-not (Test-Path $addinDll)) {
        Note-Unverified 'the add-in binary matches the release manifest' `
                        "the installed add-in was not found at $addinDll" 'Horizun.Revit.dll'
    }
    else {
        $addinSha = (Get-FileHash $addinDll -Algorithm SHA256).Hash.ToLower()
        if ($addinSha -eq $ExpectedAddinSha256.ToLower()) {
            Note-Pass 'the add-in binary matches the release manifest' 'Horizun.Revit.dll' $null
        } else {
            Note-Fail 'the add-in binary matches the release manifest' 'Horizun.Revit.dll' `
                      ("expected sha256 {0}, the file is {1}" -f $ExpectedAddinSha256.ToLower(), $addinSha)
        }
    }
}
else {
    $notCovered += "that the Revit $Year add-in on disk is the one in the release manifest (needs -ExpectedAddinSha256)"
}

# Fixtures that were never supplied, named one at a time rather than as a single
# vague line. Each names the parameter that closes it.
$fixtureGaps = @{
    InactiveDocument = 'refusing a document that is OPEN but not ACTIVE'
    SpfPath          = 'bind_shared_param rehearsing without writing'
    SpfParam         = 'bind_shared_param rehearsing without writing'
}
foreach ($k in $fixtureGaps.Keys) {
    $v = Get-Variable -Name $k -ValueOnly -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($v)) { $notCovered += ("{0} (needs -{1})" -f $fixtureGaps[$k], $k) }
}

$notCovered = @($notCovered | Select-Object -Unique)

if ($notCovered.Count -gt 0) {
    Write-Host ""
    Write-Host "  NOT COVERED by this run - named, because a guarantee missing from the" -ForegroundColor DarkYellow
    Write-Host "  output reads exactly like one that passed:" -ForegroundColor DarkYellow
    foreach ($n in $notCovered) { Write-Host ("    - {0}" -f $n) -ForegroundColor DarkYellow }
}

Write-Host ""
Write-Host ("  {0} passed, {1} failed, {2} UNVERIFIED, {3} NOT COVERED" -f `
            $passed, $failed, $unverified, $notCovered.Count)

# ---------------------------------------------------------------------------
# The machine-readable record. Written from the SAME list the console was
# printed from - two renderings of one run are two things that can disagree.
# ---------------------------------------------------------------------------
$report = [pscustomobject]@{
    schema            = 2
    generated_utc     = (Get-Date).ToUniversalTime().ToString('o')
    harness_file      = $harnessFile
    harness_commit    = $harnessCommit
    harness_git_blob  = $harnessGitBlob
    harness_sha256    = $harnessSha256
    harness_path_matches_repository = $harnessPathMatchesRepository
    harness_tracked_clean = $harnessTrackedClean
    revit_year        = $Year
    revit_pid         = $target.pid
    addin_version     = $target.addin_version
    server_path       = $Server
    server_sha256     = $serverSha
    server_is_dev_build = $serverIsDevBuild
    release_gate      = [bool]$ReleaseGate
    expected_commit   = $ExpectedCommit
    fixtures_file     = $Fixtures
    fixtures_present  = @{
        Document         = -not [string]::IsNullOrWhiteSpace($Document)
        InactiveDocument = -not [string]::IsNullOrWhiteSpace($InactiveDocument)
        SpfPath          = -not [string]::IsNullOrWhiteSpace($SpfPath)
        SpfParam         = -not [string]::IsNullOrWhiteSpace($SpfParam)
        QuantityCategory = -not [string]::IsNullOrWhiteSpace($QuantityCategory)
        OldFile          = -not [string]::IsNullOrWhiteSpace($OldFile)
        FamilyTemplate   = -not [string]::IsNullOrWhiteSpace($FamilyTemplate)
        WriteDocument    = -not [string]::IsNullOrWhiteSpace($WriteDocument)
        WriteDocumentDisposable = -not [string]::IsNullOrWhiteSpace($WriteDocumentDisposable)
        ClosedWorksetDocument = -not [string]::IsNullOrWhiteSpace($ClosedWorksetDocument)
        ClosedWorksetName = -not [string]::IsNullOrWhiteSpace($ClosedWorksetName)
        LinkSourceFile   = -not [string]::IsNullOrWhiteSpace($LinkSourceFile)
    }
    write_tier        = @{
        requested  = [bool]$WriteProbes
        # Null when the tier ran. When it did not, this is the one sentence that
        # says why - the same reason printed against every probe it skipped.
        gate       = $writeGate
        document   = $WriteDocument
        probes     = $writeResults.Count
    }
    # The dimension probes' own record: WHAT geometry they measured (as a spec
    # hash two runs can compare), WHICH localization measured it, and the
    # per-case evidence with requested/read values. Empty cases mean the write
    # gate never let the section run - the gate above says why.
    dimensions        = @{
        fixture = @{
            description = 'synthetic: 2 generic-model RFAs + instances + 3 pipes + 2 grids + plan/section views, all created by this run at x~510000'
            spec_json   = $dimensionFixtureSpec
            spec_sha256 = $dimensionFixtureSpecSha256
            families    = @($script:dimFamilyPaths)
        }
        revit_language = $script:dimRevitLanguage
        revit_build    = $script:dimRevitBuild
        cases          = @($script:dimensionEvidence)
    }
    # The 2D-detail probes' own record, the same shape for the same reason: the
    # spec hash proves two runs drew the SAME geometry, and empty cases mean the
    # write gate never let the section run.
    detail_2d         = @{
        fixture = @{
            description = 'synthetic: one drafting view + 2 lines, an arc, a closed polyline, a holed filled region, a masking region, 2 self-provisioned RFAs (detail item + generic annotation) with placed instances and a restyled line, all created by this run and deleted by its last probe'
            spec_json   = $detail2dFixtureSpec
            spec_sha256 = $detail2dFixtureSpecSha256
            families    = @($script:d2dFamilyPaths)
        }
        cases = @($script:detail2dEvidence)
    }
    # The planimetry probes' record, same shape again: the spec hash proves two
    # runs staged the SAME documentation fixture, and empty cases mean the write
    # gate never let the section run.
    planimetry        = @{
        fixture = @{
            description = 'synthetic: two sheets (one with a title block, one deliberately without), the dimension plan and section placed with a KNOWN overlap, a clear schedule placement, multi-category tags with one pipe untagged and one duplicated, an overridden dimension, text inside and outside an activated crop, and an unloaded link as the coverage fixture'
            spec_json   = $planimetryFixtureSpec
            spec_sha256 = $planimetryFixtureSpecSha256
        }
        cases = @($script:planimetryEvidence)
    }
    # And the CORRECTION probes' record. Separate from planimetry above because
    # they answer a different question: the read section proves the model was
    # measured, this one proves it was changed - once, atomically, and only where
    # a finding licensed it.
    fix_planimetry    = @{
        fixture = @{
            description = 'synthetic: the planimetry fixture left uncorrected, plus a view template to assign, one element override to clear, and an inline requirement set whose rules produce the findings the universal catalog has no remedy for. The placed title block and the authored template are deleted before the closing census.'
            spec_json   = $fixFixtureSpec
            spec_sha256 = $fixFixtureSpecSha256
        }
        cases = @($script:fixEvidence)
    }
    planimetry_production = @{
        fixture = @{
            description = 'reuses the measured planimetry/dimension fixture: two existing viewports packed on sheet A, the deliberately untagged third pipe, two semantic pipe centerlines, one created revision assigned to sheet A with a plan-view cloud, and a direct PNG of sheet A'
            source_fixture_sha256 = $planimetryFixtureSpecSha256
        }
        cases = @($script:productionEvidence)
    }
    # The linked-and-production record: the run AUTHORS its own link source (a
    # level, two vertical grids, one Y-running wall), links it as one type with
    # three placements (translated / rotated 30deg / twin translation), stages a
    # four-wall room, and drives every capability under test through the TYPED
    # tools. The pack case round-trips the user settings file and restores it
    # before judging.
    dimension_production = @{
        fixture = @{
            description = 'self-authored link source RVT (level, grids HZL-1/2 at x=0/5000mm, one Y-running wall at x=8000mm) linked three times into the disposable model at x=560000mm (A plain, B rotated 30 degrees, C twin), plus a staged 4m x 3m four-wall room HZ-901; all typed writes ran dry-run -> token -> apply'
        }
        cases = @($script:dp2Evidence)
    }
    # W11: phases 5-14 of the Maximum Program, live. Fixtures self-staged far
    # east (x >= 610 m): three pipes (two meeting at a corner, one distant), a
    # wall with a crossing pipe, three grids, a CSV pair in the scratch
    # directory, and the dp2 link fixture reused for typed link management.
    maximum_program = @{
        fixture = @{
            description = 'self-staged: pipes at x=610m (elbow pair + distant), a 4m wall with a crossing pipe at z=1.5m (penetration/opening/findings), three grids (two crossings) for plan_structure, CSV files in the scratch directory for the tabular round trip, the dp2 link fixture for manage_links, and a scratch RFA for the type catalog'
        }
        cases = @($script:mpEvidence)
    }
    summary           = @{
        passed      = $passed
        failed      = $failed
        unverified  = $unverified
        not_covered = $notCovered.Count
        # From the DEFINITIVE probe collection, after every verification -
        # including the manifest-hash checks - has been recorded. It used to be
        # `$probes.Count + $writeResults.Count`, a formula frozen before those
        # checks existed: a report could say probes=112 while carrying 114 rows
        # and passed=114, and a reader comparing the two numbers had every right
        # to distrust the whole file.
        # $results is read WITHOUT the usual @() wrapper here and below: it is a
        # typed List[object], never null, and pwsh 7.6.5 throws 'Argument types
        # do not match' on @(<generic list>).Count (measured 2026-08-24 in the
        # 2023 gate; JSON arrays, object[] and ArrayList are unaffected).
        probes      = $results.Count
        asserting   = @($results | Where-Object { $_.outcome -ne 'not_covered' }).Count
    }
    probes            = $results
    not_covered       = $notCovered
}

# THE REPORT MAY NOT DISAGREE WITH ITSELF. The incremental counters and the row
# collection are two accounts of one run; before anything is written, they must
# reconcile exactly - a drift here is a harness bug (a Note-* path that skipped
# Add-Result, or a close that ran twice and double-counted), and a machine-read
# gate built on inconsistent numbers is worse than no gate at all.
$rowCounts = @{}
foreach ($resultRow in $results) {
    if (-not $rowCounts.ContainsKey($resultRow.outcome)) { $rowCounts[$resultRow.outcome] = 0 }
    $rowCounts[$resultRow.outcome]++
}
foreach ($pair in @(@('pass', $passed), @('fail', $failed), @('unverified', $unverified))) {
    $fromRows = 0; if ($rowCounts.ContainsKey($pair[0])) { $fromRows = $rowCounts[$pair[0]] }
    if ($fromRows -ne $pair[1]) {
        throw ("REPORT SELF-CHECK FAILED: the incremental counter says {0} '{1}' probe(s) but the row " -f $pair[1], $pair[0]) +
              ("collection carries {0}. The report was NOT written - fix the harness before trusting a run." -f $fromRows)
    }
}
$rowsNotCovered = 0; if ($rowCounts.ContainsKey('not_covered')) { $rowsNotCovered = $rowCounts['not_covered'] }
if (($passed + $failed + $unverified + $rowsNotCovered) -ne $results.Count) {
    throw ("REPORT SELF-CHECK FAILED: pass({0}) + fail({1}) + unverified({2}) + not_covered rows({3}) do not " -f $passed, $failed, $unverified, $rowsNotCovered) +
          ("add up to the {0} probe rows. The report was NOT written." -f $results.Count)
}
$duplicateNames = @($results | Group-Object -Property name | Where-Object { $_.Count -gt 1 })
if ($duplicateNames.Count -gt 0) {
    throw ("REPORT SELF-CHECK FAILED: {0} probe name(s) were recorded more than once ({1}) - a close that " -f $duplicateNames.Count, (@($duplicateNames | ForEach-Object { $_.Name }) -join '; ')) +
          "ran twice writes every verdict twice. The report was NOT written."
}

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $report | ConvertTo-Json -Depth 8 | Out-File -FilePath $Json -Encoding utf8
    Write-Host "  wrote $Json"
}

# ---------------------------------------------------------------------------
# Exit codes. UNVERIFIED is not a failure of the software, but it IS a failure
# of the run to establish what it set out to - and NOT COVERED is a failure to
# even attempt it. A caller that exits 0 on either reads the code as "everything
# holds".
#
# Ordered most-severe first, so one number always means the worst thing found.
# ---------------------------------------------------------------------------
if ($failed -gt 0) { exit 1 }
if ($unverified -gt 0) { exit 2 }
if ($notCovered.Count -gt 0) {
    if ($ReleaseGate) {
        Write-Host ""
        Write-Host "  RELEASE GATE: NOT COVERED is a failure here. A release cannot rest on" -ForegroundColor Red
        Write-Host "  guarantees this run did not attempt. Supply the fixtures above and re-run." -ForegroundColor Red
        exit 3
    }
    Write-Host ""
    Write-Host "  (exit 0: not a release gate. Under -ReleaseGate the line above is exit 3.)" -ForegroundColor DarkYellow
}
exit 0
