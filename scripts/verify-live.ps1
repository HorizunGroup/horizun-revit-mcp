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

# ---------------------------------------------------------------------------
# Fixtures. An explicit parameter always wins; the file fills the rest.
# ---------------------------------------------------------------------------
$fixtureSource = @{}
if (Test-Path $Fixtures) {
    try {
        $fx = Get-Content $Fixtures -Raw | ConvertFrom-Json
        foreach ($name in 'Document','InactiveDocument','SpfPath','SpfParam','QuantityCategory','OldFile',
                          'FamilyTemplate','ClosedWorksetDocument','ClosedWorksetName',
                          'WriteDocument','WriteDocumentDisposable') {
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
        $candidateTemplate = Get-ChildItem -LiteralPath $templateRoot -Recurse -Filter '*.rft' -File -ErrorAction SilentlyContinue |
                             Sort-Object FullName | Select-Object -First 1
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
                  Tool = 'horizun_audit_model'; Args = @{ top = 1 }
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
                  Tool = 'horizun_audit_model'; Args = @{ top = 2 }
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
                  Tool = 'horizun_quantities'; Args = @{ category = $QuantityCategory; only_disagreements = $true; top = 1 }
                  Check = { param($d)
                            $d.coverage -and $d.coverage.volume_geometry.total_is_complete -ne $null -and
                            $d.comparison.candidates -ge 0 -and
                            # A quantity is the answer somebody puts in a budget. It must
                            # never travel without saying how much of the model it is over.
                            (& $coverageShape $d.visibility_coverage) } }

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
                  Tool = 'horizun_audit_model'; Args = @{ top = 1 }
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
                  Args = @{ category = $QuantityCategory; only_disagreements = $true; top = 1 }
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
$proc = [System.Diagnostics.Process]::Start($psi)

function Send-Rpc($obj) { $proc.StandardInput.WriteLine(($obj | ConvertTo-Json -Depth 8 -Compress)); $proc.StandardInput.Flush() }
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
        # Progress notifications carry no id and are not anybody's answer.
        if ($m.id) { return $m }
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
)

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
# it reports the gap. The owner may use Revit's expiring Python ON/OFF button or
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
    schema            = 1
    generated_utc     = (Get-Date).ToUniversalTime().ToString('o')
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
    }
    write_tier        = @{
        requested  = [bool]$WriteProbes
        # Null when the tier ran. When it did not, this is the one sentence that
        # says why - the same reason printed against every probe it skipped.
        gate       = $writeGate
        document   = $WriteDocument
        probes     = $writeResults.Count
    }
    summary           = @{
        passed      = $passed
        failed      = $failed
        unverified  = $unverified
        not_covered = $notCovered.Count
        probes      = ($probes.Count + $writeResults.Count)
        asserting   = $assertingProbes
    }
    probes            = $results
    not_covered       = $notCovered
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
