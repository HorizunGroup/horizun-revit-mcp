#Requires -Version 5.1
<#
  Live verification against a REAL Revit.

  CI cannot do this: a hosted runner has no RevitAPI.dll and no Revit process, so
  everything that matters most - the UI-thread round trip, the guards, the
  refusals - is exactly what CI cannot prove. This is the other half of the test
  story, and it is meant to run against every Revit generation before a release.

  It reports what it MEASURED. A probe that is not exercised is reported as "not
  covered" rather than quietly counted as working.

  EVERY PROBE HERE IS NON-DESTRUCTIVE. The write commands are exercised through
  their refusals and their dry runs, which is where their guarantees live: a
  refusal that fires is proof, and it changes nothing. Nothing in this script
  writes to a model.

  Usage:
    pwsh scripts/verify-live.ps1 -Year 2026
    pwsh scripts/verify-live.ps1 -Year 2026 -Document MOD_ARCH_A
    pwsh scripts/verify-live.ps1 -Year 2024 -OldFile path\to\a-2023.rfa

    # the release gate: every fixture supplied, provenance checked, JSON emitted
    pwsh scripts/verify-live.ps1 -Year 2026 -ReleaseGate `
         -ExpectedCommit <sha> -Json artifacts/live-2026.json

  Requires: that Revit open with the add-in loaded.

  WHERE THE FIXTURE NAMES COME FROM. Six of the parameters name real things on
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

    # The title of a WORKSHARED document, open in this Revit, with AT LEAST ONE
    # WORKSET CLOSED.
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
    [switch]$AllowDevServer
)
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Fixtures. An explicit parameter always wins; the file fills the rest.
# ---------------------------------------------------------------------------
$fixtureSource = @{}
if (Test-Path $Fixtures) {
    try {
        $fx = Get-Content $Fixtures -Raw | ConvertFrom-Json
        foreach ($name in 'Document','InactiveDocument','SpfPath','SpfParam','QuantityCategory','OldFile',
                          'ClosedWorksetDocument') {
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
       Tool = 'horizun_execute_python'; Args = @{ code = "__output__ = 6 * 7"; target_document = $Document }
       Needs = 'Document'
       NotCovered = 'whether execute_python runs or refuses (needs -Document; it requires target_document like every other mutating command)'
       Check = { param($d)
                 # Reached the Check at all, so it was not refused: it must have RUN.
                 $d.executed -eq $true -and $d.output -eq 42 -and
                 $d.transaction_left_open -eq $false -and
                 -not [string]::IsNullOrWhiteSpace($d.transaction_policy) }
       # When it is switched off the reply is an error, and this is the one probe
       # where that is a correct outcome rather than an unmeasured one.
       ErrorIsAlsoPass = 'DISABLED' },

    # THE COMMAND THAT USED TO BE OUTSIDE THE POLICY. It could do everything the
    # typed writes can, plus everything they cannot, aimed at whatever window was
    # in front.
    @{ Name = 'execute_python REFUSES without target_document'
       Tool = 'horizun_execute_python'; Args = @{ code = "__output__ = 1" }
       ExpectError = "'target_document' is required" },

    # The reply carrying the job_id is the message that gets lost. Without a key,
    # a client retrying a timeout queues the script a second time.
    @{ Name = 'run_async REFUSES without idempotency_key'
       Tool = 'horizun_execute_python'
       Args = @{ code = "__output__ = 1"; run_async = $true; target_document = $Document }
       Needs = 'Document'
       NotCovered = 'run_async demanding an idempotency_key (needs -Document)'
       ExpectError = "'idempotency_key' is required" },

    # Accepting and ignoring it would tell the caller its retry was deduplicated
    # when a second synchronous call is a second execution.
    @{ Name = 'a key on the SYNCHRONOUS path is refused, not ignored'
       Tool = 'horizun_execute_python'
       Args = @{ code = "__output__ = 1"; target_document = $Document; idempotency_key = 'sync-probe' }
       Needs = 'Document'
       NotCovered = 'the synchronous path refusing an idempotency_key (needs -Document)'
       ExpectError = 'without run_async=true' },

    # Was Check = { $true } with AllowError: it asserted nothing and passed on an
    # error too. What it has to prove is that it describes the SAME Revit this
    # harness selected, and reports a real element count rather than a zero
    # standing in for a read that did not happen.
    @{ Name = 'get_document_info describes this Revit and counts real elements'
       Tool = 'get_document_info'; Args = @{}
       Check = { param($d) $d.revit_version -eq "$Year" -and $d.title -and $d.element_count -gt 0 } },

    # ---- the mutation gate, proven by its refusals. Nothing is written. ----
    @{ Name = 'delete REFUSES without target_document'
       Tool = 'horizun_delete_verified'; Args = @{ mode = 'ids'; ids = @(999999999) }
       ExpectError = "'target_document' is required" },

    @{ Name = 'delete REFUSES a document that is not open'
       Tool = 'horizun_delete_verified'
       Args = @{ mode = 'ids'; ids = @(999999999); target_document = 'ZZ_NO_SUCH_MODEL_ZZ' }
       ExpectError = 'No open document matches' },

    @{ Name = 'set_keynote REFUSES without target_document'
       Tool = 'horizun_set_keynote'; Args = @{ element_ids = @(999999999); keynote = 'X' }
       ExpectError = "'target_document' is required" },

    @{ Name = 'write_params REFUSES without target_document'
       Tool = 'horizun_write_params_verified'; Args = @{ writes = @() }
       ExpectError = "'target_document' is required" },

    @{ Name = 'family_apply REFUSES without target_document'
       Tool = 'horizun_family_apply'; Args = @{}
       ExpectError = "'target_document' is required" },

    @{ Name = 'bind_shared_param REFUSES without target_document'
       Tool = 'horizun_bind_shared_param'; Args = @{}
       ExpectError = "'target_document' is required" },

    @{ Name = 'save REFUSES without target_document'
       Tool = 'horizun_save_document'; Args = @{}
       ExpectError = "'target_document' is required" }
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
    $null -ne $block -and
    $block.PSObject.Properties.Name -contains 'coverage_complete' -and
    $block.PSObject.Properties.Name -contains 'is_workshared' -and
    $block.PSObject.Properties.Name -contains 'worksets_total' -and
    $block.PSObject.Properties.Name -contains 'worksets_open' -and
    $block.PSObject.Properties.Name -contains 'worksets_closed' -and
    -not [string]::IsNullOrWhiteSpace($block.note)
}


# What this run did NOT exercise, named one by one at the end. A guarantee absent
# from the output is indistinguishable from one that passed, which is how a
# missing probe becomes a claim.
$notCovered = @()

# Probes that need a named document. Without -Document they are reported as NOT
# COVERED: pointing them at a guess would pass for the wrong reason.
if ($Document) {
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
    # It now compares them: the count audit calls an issue must equal the number
    # scan calls not loaded, and neither may claim coverage the other denies.
    $probes += @{ Name = 'audit and scan AGREE about the links'
                  Tool = 'horizun_audit_model'; Args = @{ top = 2 }
                  Check = { param($d)
                            $links = @($d.findings | Where-Object { $_.check -eq 'links' })
                            if ($links.Count -ne 1) { return $false }
                            if (-not $script:scanLinks) { return $false }   # the scan probe must have run first
                            $auditNotLoaded = [int]$links[0].count
                            $scanNotLoaded  = [int]$script:scanLinks.rvt_links_not_loaded
                            ($auditNotLoaded -eq $scanNotLoaded) -and
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
                      ExpectError = 'but the ACTIVE document is' }
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

    $probes += @{ Name = 'delete dry run issues a confirmation token and writes nothing'
                  Tool = 'horizun_delete_verified'
                  Args = @{ mode = 'ids'; ids = @(999999999); target_document = $Document; id_cap = 2 }
                  Check = { param($d)
                            $d.dry_run -eq $true -and $d.confirmation_token -and
                            $d.deleted_total -eq $null -and $d.elements_before -eq $d.elements_after } }

    $probes += @{ Name = 'delete REFUSES a token minted for a different plan'
                  Tool = 'horizun_delete_verified'
                  Args = @{ mode = 'ids'; ids = @(888888888); target_document = $Document
                            dry_run = $false; confirmation_token = 'hz-0000000000000000000000000000000000' }
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
}
else {
    Write-Host "  (no -Document given: the link, quantities and confirmation probes are NOT COVERED)" -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------------------
# THE CLOSED WORKSET. The one condition that cannot be detected from inside the
# answer it corrupts, and the one that cannot be simulated - it is a property of
# how a real model was opened, so it needs a real model that has one.
# ---------------------------------------------------------------------------
if ($ClosedWorksetDocument) {
    $probes += @{ Name = 'a CLOSED workset makes model_scan report incomplete coverage'
                  Tool = 'horizun_model_scan'
                  Args = @{ target_document_title = $ClosedWorksetDocument
                            sections = @('worksets'); top = 50 }
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
                  Args = @{ target_document_title = $ClosedWorksetDocument
                            sections = @('worksets'); top = 50 }
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
                  Check = { param($d)
                            $d.visibility_coverage.coverage_complete -eq $false -and
                            # audit_model already had a coverage_complete of its own, for
                            # checks that could not read an element. A closed workset is a
                            # third way to miss the model and reaches the same flag.
                            $d.coverage_complete -eq $false -and
                            $d.note -match 'INCOMPLETE' } }

    $probes += @{ Name = 'a CLOSED workset makes clash refuse to call its zero complete'
                  Tool = 'horizun_clash'
                  Args = @{ target_document = $ClosedWorksetDocument
                            categories_a = @('OST_Walls'); categories_b = @('OST_Floors'); max_results = 1 }
                  Check = { param($d)
                            $d.visibility_coverage.coverage_complete -eq $false -and
                            $d.result -ne 'complete' -and
                            $d.headline -match 'DO NOT READ AN ABSENCE' } }

    $probes += @{ Name = 'a CLOSED workset rides along with the quantity itself'
                  Tool = 'horizun_quantities'
                  Args = @{ category = $QuantityCategory; only_disagreements = $true; top = 1 }
                  Check = { param($d)
                            $d.visibility_coverage.coverage_complete -eq $false -and
                            # The headline is the sentence somebody quotes into a budget.
                            $d.headline -match 'INCOMPLETE COVERAGE' } }
}
else {
    $notCovered += 'a CLOSED workset making scan, audit, quantities and clash all report incomplete coverage ' +
                   '(needs -ClosedWorksetDocument: a WORKSHARED model open in this Revit with at least one workset ' +
                   'CLOSED). It cannot be simulated - a closed workset is a property of how the model was opened - ' +
                   'and it is the one condition that leaves no trace in the answer it corrupts, so passing this off ' +
                   'a model with every workset open would be this suite making the exact substitution it exists to catch.'
    Write-Host "  (no -ClosedWorksetDocument given: the closed-workset coverage probes are NOT COVERED)" -ForegroundColor DarkYellow
}

if ($OldFile) {
    if (-not (Test-Path $OldFile)) { throw "-OldFile does not exist: $OldFile" }
    $probes += @{ Name = "open_document REFUSES a file saved in another Revit"
                  Tool = 'horizun_open_document'; Args = @{ path = $OldFile }
                  ExpectError = 'REFUS' }
}

# ---------------------------------------------------------------------------
# One MCP session, ONE REQUEST AT A TIME.
#
# Pipelining every probe at once does not work, and the reason is the design
# rather than a bug: the bridge admits one Revit command at a time and REFUSES
# the rest with "one request at a time" instead of queueing them behind a run
# that may take minutes. Blasting twenty requests therefore produced one answer
# and nineteen refusals - a correct bridge and a wrong client.
#
# So this waits for each reply before sending the next, which is what any caller
# of this bridge has to do.
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
    while ($true) {
        $t = $proc.StandardOutput.ReadLineAsync()
        if (-not $t.Wait($TimeoutMs)) { return $null }
        if (-not $t.Result) { return $null }
        try { $m = $t.Result | ConvertFrom-Json } catch { continue }
        # Progress notifications carry no id and are not anybody's answer.
        if ($m.id) { return $m }
    }
}

Send-Rpc @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; capabilities=@{}; clientInfo=@{ name='verify-live'; version='1' } } }
$null = Read-Rpc
Send-Rpc @{ jsonrpc='2.0'; method='notifications/initialized' }

$byId = @{}
$id = 1
foreach ($p in $probes) {
    $id++
    $p.Id = $id
    Send-Rpc @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$p.Tool; arguments=$p.Args } }
    $m = Read-Rpc
    if ($m) { $byId[[int]$m.id] = $m }
}

$proc.StandardInput.Close()
if (-not $proc.WaitForExit(130000)) { $proc.Kill() }

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

    $text = $m.result.content[0].text
    $isError = [bool]$m.result.isError

    if ($p.ExpectError) {
        if ($p.AllowMissingTool -and $m.error) {
            # The tool is not advertised at all, which for a disabled capability is
            # a stronger result than a refusal - but it is a different one, so say so.
            Note-Pass $p.Name $p.Tool 'not advertised at all'
            continue
        }
        if ($isError -and $text -match $p.ExpectError) {
            Note-Pass $p.Name $p.Tool $null
        } else {
            $got = if ($text) { ($text -replace "`n", ' ').Substring(0, [Math]::Min(200, $text.Length)) } else { '(no text)' }
            Note-Fail $p.Name $p.Tool ("expected a refusal matching '{0}', got: {1}" -f $p.ExpectError, $got)
        }
        continue
    }

    if ($isError) {
        # ONE probe legitimately has two correct outcomes: a capability behind a
        # per-machine switch. ErrorIsAlsoPass names the refusal that counts as one,
        # so the probe asserts in BOTH states instead of assuming either. It is a
        # named pattern, not AllowError returning under another name: the refusal
        # text still has to match, and anything else is still UNVERIFIED.
        if ($p.ErrorIsAlsoPass -and $text -match $p.ErrorIsAlsoPass) {
            Note-Pass $p.Name $p.Tool 'switched off, and it refused'
            continue
        }

        # An error where an ANSWER was required. The check cannot run, so nothing
        # about this guarantee was established - and the reason is printed here
        # rather than discarded, which is what made the last one unexplainable.
        Note-Unverified $p.Name ("the call errored, so its check never ran: " +
                                 ($text -replace "`n", ' ').Substring(0, [Math]::Min(220, $text.Length))) $p.Tool
        continue
    }

    try { $data = $text | ConvertFrom-Json } catch { $data = $null }
    if ($null -eq $data) { Note-Unverified $p.Name "the reply was not JSON, so its check never ran" $p.Tool; continue }

    if (-not $p.Check) { Note-Unverified $p.Name "this probe asserts nothing - it calls the tool and looks at neither the answer nor an error" $p.Tool; continue }

    if (& $p.Check $data) { Note-Pass $p.Name $p.Tool $null }
    else { Note-Fail $p.Name $p.Tool 'the answer did not say what it had to' }
}

Write-Host ("-" * 70)
Write-Host ("  {0} passed, {1} failed, {2} UNVERIFIED   (of {3} probes, {4} assert something)" -f `
            $passed, $failed, $unverified, $probes.Count, $assertingProbes)
if ($unverified -gt 0) {
    Write-Host "  UNVERIFIED is not a pass. These guarantees were NOT established by this run:" -ForegroundColor Yellow
    foreach ($u in $unverifiedDetail) { Write-Host ("    - {0}" -f $u) -ForegroundColor DarkYellow }
}
if (-not $Document) {
    $notCovered += 'links, quantities and the whole confirmation flow (needs -Document <title>)'
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
    }
    summary           = @{
        passed      = $passed
        failed      = $failed
        unverified  = $unverified
        not_covered = $notCovered.Count
        probes      = $probes.Count
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
