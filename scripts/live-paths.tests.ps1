#Requires -Version 5.1
<#
  Does every CHECK a live harness reads still exist in the product?

  This gate exists because of a defect that cost a live run. The apply's
  post-commit check `positions_inside_host` was split into
  `positions_within_host_extent` and `inside_host_solid`, and two probes in
  verify-rebar.ps1 went on reading the old name. Nothing offline noticed: 2442
  Core tests, 404 Server tests and five clean Revit builds all passed, because
  the harness is PowerShell and the field name is a string on both sides.

  What the harness saw was `Get-HzPath` returning $null for a key that was not
  there - which compares unequal to $true, so the probe failed and read like a
  product regression. It took a live run, a Revit restart and a read of the
  artifact to find out that the product was right and the question was stale.

  So: every check name any live harness asks for must appear as a key the
  command actually writes. A name on one side and not the other is a bug
  whichever side is wrong, and this says which names they are.

  Exit code 0 when every name is accounted for.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$live = Join-Path $repo 'scripts\live'
$apply = Join-Path $repo 'src\Horizun.Revit\Commands\ApplyReinforcementCommand.cs'

$failures = New-Object System.Collections.Generic.List[string]
$checked = 0

if (-not (Test-Path -LiteralPath $apply)) {
    Write-Host "  FAIL  ApplyReinforcementCommand.cs not found at $apply" -ForegroundColor Red
    exit 1
}
$applySource = Get-Content -LiteralPath $apply -Raw
$verifyLivePath = Join-Path $repo 'scripts\verify-live.ps1'
$verifyLiveSource = Get-Content -LiteralPath $verifyLivePath -Raw

# One redirected StreamReader permits one asynchronous read at a time. A former
# tool-pack probe started a raw ReadLineAsync every two seconds while the prior
# read was still pending; the release matrix then died after several minutes
# with "stream is currently in use by a previous operation". Notifications now
# share Read-Rpc's single reader and inbox.
$stdoutReaders = @([regex]::Matches($verifyLiveSource, '\$proc\.StandardOutput\.ReadLineAsync\(\)')).Count
if ($stdoutReaders -ne 1) {
    $failures.Add("verify-live.ps1 must have exactly one owner of server stdout; found $stdoutReaders ReadLineAsync calls")
} else {
    Write-Host '  PASS  verify-live has one owner for redirected server stdout'
}

# A release run isolates state under HORIZUN_DATA_ROOT. The harness must exercise
# that instance's settings, never the owner's default profile file.
if ($verifyLiveSource -notmatch '\$settingsRoot18\s*=\s*if\s*\(\[string\]::IsNullOrWhiteSpace\(\$env:HORIZUN_DATA_ROOT\)\)' -or
    $verifyLiveSource -notmatch 'Join-Path\s+\$settingsRoot18\s+''settings\.json''') {
    $failures.Add('the tool-pack live probe does not resolve settings.json through HORIZUN_DATA_ROOT')
} else {
    Write-Host '  PASS  the tool-pack probe writes the isolated run settings'
}

# audit_model now refuses an unnamed active document. Every call in the release
# harness must therefore bind the exact fixture it is asserting over.
$auditCalls = @([regex]::Matches($verifyLiveSource,
    "Invoke-Write\s+'horizun_audit_model'\s+@\{(?<args>[\s\S]*?)\}"))
$unnamedAudits = @($auditCalls | Where-Object { $_.Groups['args'].Value -notmatch 'target_document\s*=' })
if ($auditCalls.Count -lt 4 -or $unnamedAudits.Count -gt 0) {
    $failures.Add("every audit_model live call must name target_document; calls=$($auditCalls.Count), unnamed=$($unnamedAudits.Count)")
} else {
    Write-Host "  PASS  all $($auditCalls.Count) audit_model live calls name their target document"
}

# Revit 2023 ElementId has IntegerValue, while 2024+ adds Value. Embedded
# IronPython fixture code must select by capability instead of assuming the new
# member. These were the exact three expressions that collapsed nine W14 cases.
foreach ($forbidden in @('c.Id.Value,', '[i.Value for i in fam.GetFamilySymbolIds()]', "'id': st.Id.Value")) {
    if ($verifyLiveSource.Contains($forbidden)) {
        $failures.Add("verify-live embeds a Revit-2024-only ElementId expression: $forbidden")
    }
}

# Matrix fixtures come from the release runner. Hard-coded 2026 model names in
# the S/M/L section made every other Revit year untestable.
$smlStart = $verifyLiveSource.IndexOf('# ---- cases 5-7: S/M/L')
$smlEnd = $verifyLiveSource.IndexOf('# ---- case 8: units and locale', $smlStart)
$smlSource = if ($smlStart -ge 0 -and $smlEnd -gt $smlStart) {
    $verifyLiveSource.Substring($smlStart, $smlEnd - $smlStart)
} else { '' }
if ([string]::IsNullOrWhiteSpace($smlSource) -or
    $smlSource -match 'HZ_WRITE\.rvt|HZ_LIVE_B\.rvt|HZ_TEST_GEOM\.rvt|HZ_WF_MODEL\.rvt') {
    $failures.Add('the S/M/L live section is missing or still hard-codes one Revit year''s model paths')
} else {
    Write-Host '  PASS  S/M/L resolves same-year release fixtures instead of fixed model paths'
}
if ($smlSource -notmatch 'OpenAndActivateDocument\(target\)' -or
    $smlSource -notmatch "Invoke-Write\s+'horizun_health'" -or
    $smlSource -match "Invoke-Write\s+'horizun_document_session'") {
    $failures.Add('S/M/L must activate only an already-open exact path and re-prove the active title through health')
} else {
    Write-Host '  PASS  S/M/L activation is exact-path fixture staging with a health re-read'
}

# Revit 2023 measured-rejects all three useful linked-geometry arrangements.
# The harness must pin the structured pre-transaction refusal, while later
# years retain the positive mixed three-reference chain.
if ($verifyLiveSource -notmatch 'linked_geometry_rejected_by_revit_2023_dimension_api' -or
    $verifyLiveSource -notmatch 'Test-Dp2Revit23LinkedRefusal' -or
    $verifyLiveSource -notmatch '(?s)\$mixed23\s*=.*?\$same23\s*=.*?\$distinct23\s*=' -or
    $verifyLiveSource -notmatch '@\(\$hostGridRef,\s*\$linkedRefs5\[0\],\s*\$linkedRefs5\[1\]\)' -or
    $verifyLiveSource -notmatch 'link_instance_id\s*=\s*\$dp2\.C' -or
    $verifyLiveSource -notmatch 'positive_years=@\(2024,2025,2026,2027\)') {
    $failures.Add('the linked-dimension live probe no longer carries the measured Revit 2023 branch and mixed-reference refusal')
} else {
    Write-Host '  PASS  linked dimensions retain the measured Revit 2023 and mixed-chain branches'
}

# The server reads jobs under HORIZUN_DATA_ROOT during a release run. A real
# prior dead-job record may be selected from the durable owner ledger, but its
# exact bytes must be staged into the isolated ledger before querying its id.
if ($verifyLiveSource -notmatch 'Copy-Item\s+-LiteralPath\s+\$sourceJob' -or
    $verifyLiveSource -notmatch '\$isolatedRoot\s*=\s*\$env:HORIZUN_DATA_ROOT') {
    $failures.Add('the dead-job live probe does not stage its selected real record into the isolated data root')
} else {
    Write-Host '  PASS  the dead-job probe stages exact record bytes into the isolated ledger'
}

# The inline-valve positive must query the exact pipe ids returned by the
# command's own post-commit verification. A bounded category scan can truncate
# before the newly created halves and turn a real positive into a false zero.
if ($verifyLiveSource -notmatch '\$verifiedPipeIds\s*=\s*@\(' -or
    $verifyLiveSource -notmatch 'element_ids=\$verifiedPipeIds' -or
    $verifyLiveSource -notmatch '\$inlineRow\.inline_connections\.verified') {
    $failures.Add('the inline-valve live probe does not re-query the exact two post-commit pipe ids')
} else {
    Write-Host '  PASS  inline-valve re-read targets the exact post-commit pipe halves'
}

# What the command writes: checks["name"] = ...
$published = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in [regex]::Matches($applySource, 'checks\["([a-z0-9_]+)"\]')) {
    [void]$published.Add($m.Groups[1].Value)
}

# ... and the ones it BUILDS: checks["hook_type_" + endName]. A gate that cannot
# see those reports two false failures, and a gate that cries wolf gets deleted.
$prefixes = New-Object System.Collections.Generic.List[string]
foreach ($m in [regex]::Matches($applySource, 'checks\["([a-z0-9_]+)"\s*\+')) {
    $prefixes.Add($m.Groups[1].Value)
}
if ($published.Count -eq 0) {
    Write-Host '  FAIL  no checks[...] assignments found - has the command been restructured?' -ForegroundColor Red
    exit 1
}
Write-Host ("  the apply publishes {0} check(s): {1}" -f $published.Count,
    (($published | Sort-Object) -join ', '))
if ($prefixes.Count -gt 0) {
    Write-Host ("  and builds {0} more from prefixes: {1}" -f $prefixes.Count,
        (($prefixes | Sort-Object -Unique) -join ', '))
}

# What the harnesses ask for: Get-HzPath ... 'checks', 'name'
foreach ($file in Get-ChildItem -LiteralPath $live -Filter '*.ps1' -ErrorAction SilentlyContinue) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($m in [regex]::Matches($text, "'checks',\s*'([a-z0-9_]+)'")) {
        $name = $m.Groups[1].Value
        $checked++
        $known = $published.Contains($name)
        if (-not $known) {
            foreach ($p in $prefixes) { if ($name.StartsWith($p)) { $known = $true; break } }
        }
        if (-not $known) {
            $failures.Add(("{0} reads checks.{1}, which the apply never writes" -f $file.Name, $name))
        }
    }
}

Write-Host ("  {0} check reference(s) across the live harnesses" -f $checked)

if ($checked -eq 0) {
    Write-Host '  FAIL  no check references found at all - this gate would pass vacuously' -ForegroundColor Red
    exit 1
}

if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host ("  FAIL  " + $f) -ForegroundColor Red }
    Write-Host ''
    Write-Host 'live path tests: FAILED' -ForegroundColor Red
    exit 1
}

Write-Host '  PASS  every check a live harness reads is one the apply writes'
Write-Host ''
Write-Host 'live path tests: ALL PASS' -ForegroundColor Green
exit 0
