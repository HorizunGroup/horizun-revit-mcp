#Requires -Version 5.1
<#
  Shared helpers for the wall-split live campaign.

  WHY A LIBRARY AND NOT INLINE. The replies this capability produces are large -
  a single dry run over one wall carries a layer plan, a dependency ledger and a
  list of the checks that will run - and a harness that printed them would drown
  its own verdict. Everything here writes the full reply to disk under the run's
  artifact directory and returns only the parsed object, so the transcript stays
  readable and the evidence stays complete.
#>
Set-StrictMode -Version Latest

$script:HzRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:HzCall = Join-Path $script:HzRepo 'scripts\hz-call.ps1'

function New-WsRun {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Document = 'HZ_WRITE',
        [string]$ArtifactDir
    )
    if (-not $ArtifactDir) {
        $ArtifactDir = Join-Path $script:HzRepo ('artifacts\live\wallsplit-' + (Get-Date).ToString('yyyyMMdd-HHmmss'))
    }
    $null = New-Item -ItemType Directory -Force -Path $ArtifactDir
    [ordered]@{
        Name        = $Name
        Document    = $Document
        ArtifactDir = $ArtifactDir
        RunId       = [guid]::NewGuid().ToString('N').Substring(0, 8)
        Probes      = (New-Object System.Collections.ArrayList)
        Notes       = (New-Object System.Collections.ArrayList)
        Calls       = 0
    }
}

<#
  One tool call. The full reply is written to disk; what comes back is the parsed
  object plus whether it was an error. A call that cannot be parsed is reported as
  such rather than being silently treated as an empty result.
#>
function Invoke-Ws {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Tool,
        [Parameter(Mandatory)][string]$Label,
        $Arguments,
        [switch]$AllowError,
        [switch]$Mutates
    )
    $Run.Calls++
    # A mutating call needs its own idempotency key: the bridge refuses one without
    # it, deliberately, so that a retry is a decision rather than a second write.
    # A FRESH KEY ON EVERY CALL, mutating or not. It used to be added only for
    # mutating calls and only when absent, so a caller that reused an arguments
    # object reused its key - and the bridge answered from cache. During the
    # topology experiment that returned a probe from an earlier configuration and
    # an inventory from before the fixture existed, both indistinguishable from
    # fresh measurements. There is no call for which replaying an old answer is
    # what the harness wanted.
    if (-not ($Arguments -is [string])) {
        $Arguments = @{} + $Arguments
        $Arguments['idempotency_key'] = [guid]::NewGuid().ToString()
    }
    $script:HzLastKey = if ($Arguments -is [string]) { $null } else { $Arguments['idempotency_key'] }
    $json = if ($Arguments -is [string]) { $Arguments } else { $Arguments | ConvertTo-Json -Depth 40 -Compress }
    $out  = Join-Path $Run.ArtifactDir ("call-{0:d3}-{1}.json" -f $Run.Calls, ($Label -replace '[^\w\-]', '_'))

    # hz-call.ps1 reports with Write-Host, which goes to the INFORMATION stream and
    # not to the pipeline - piping it captured two characters and every reply looked
    # unparseable. Its own -Json writer is the authoritative record; 6>&1 captures
    # the printed form as well so a call whose artifact fails to write still leaves
    # its reply behind.
    # DELETE ANY OLD ARTIFACT FIRST. A call that fails to launch writes nothing, and
    # the reader would then pick up whatever was already at that path and report a
    # previous run's answer as this one's.
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
    $raw  = Join-Path $Run.ArtifactDir ("call-{0:d3}-{1}.txt" -f $Run.Calls, ($Label -replace '[^\w\-]', '_'))
    $text = & $script:HzCall -Tool $Tool -Arguments $json -Json $out 6>&1 2>&1 | Out-String
    $code = $LASTEXITCODE
    Set-Content -LiteralPath $raw -Value $text -Encoding UTF8

    $result = $null
    $parsed = $false

    # The -Json artifact first: it is the reply as hz-call parsed it.
    if (Test-Path -LiteralPath $out) {
        try {
            $envelope = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
            $names = @($envelope.PSObject.Properties.Name)
            if ($names -contains 'structuredContent' -and $envelope.structuredContent) { $result = $envelope.structuredContent }
            elseif ($names -contains 'result' -and $envelope.result) { $result = $envelope.result }
            else { $result = $envelope }
            $parsed = $true
        } catch { $parsed = $false }
    }

    # FALLBACK ONLY. If -Json produced nothing readable, recover the reply from the
    # printed form. The scanner below respects string literals: these replies embed
    # JSON EXAMPLES inside their own prose - the evidence contract note contains
    # {"status":"verified|..."} - so a naive brace count ends the object in the
    # middle of a sentence and parses nothing.
    $start = if ($parsed) { -1 } else { $text.IndexOf('{') }
    if ($start -ge 0) {
        $depth = 0; $end = -1; $inString = $false; $escaped = $false
        for ($i = $start; $i -lt $text.Length; $i++) {
            $c = $text[$i]
            if ($inString) {
                if ($escaped) { $escaped = $false }
                elseif ($c -eq [char]0x5C) { $escaped = $true }
                elseif ($c -eq '"') { $inString = $false }
                continue
            }
            if ($c -eq '"') { $inString = $true }
            elseif ($c -eq '{') { $depth++ }
            elseif ($c -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
        }
        if ($end -gt $start) {
            try { $result = $text.Substring($start, $end - $start + 1) | ConvertFrom-Json; $parsed = $true }
            catch { $parsed = $false }
        }
    }

    # A REPLAYED ANSWER IS NOT A MEASUREMENT. If the reply echoes an idempotency key
    # that is not the one this call minted, it belongs to an earlier question.
    if ($script:HzLastKey -and $text -match 'idempotenc\w*"\s*:\s*"([0-9a-fA-F-]{36})"') {
        if ($Matches[1] -ne $script:HzLastKey) {
            Add-WsNote $Run ("REPLAY REFUSED [{0}] {1}: the reply carries key {2}, this call minted {3}" -f
                $Tool, $Label, $Matches[1], $script:HzLastKey)
            $parsed = $false
            $result = $null
        }
    }

    $isError = ($code -ne 0)
    if (-not $AllowError -and $isError) {
        Add-WsNote $Run ("CALL FAILED [{0}] {1}: {2}" -f $Tool, $Label, (Limit-WsText $text 400))
    }

    [ordered]@{
        Tool = $Tool; Label = $Label; IsError = $isError; Parsed = $parsed
        Result = $result; Text = $text; File = $out; ExitCode = $code
    }
}

function Add-WsNote {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Text)
    $null = $Run.Notes.Add(@{ note = $Text; utc = (Get-Date).ToUniversalTime().ToString('o') })
    Write-Host ("  note  " + $Text) -ForegroundColor DarkGray
}

<#
  CRASH SURVIVAL.

  Revit terminated mid-case in the previous session and took the whole run's
  state with it, because the state lived in memory until the end. Every case now
  appends a checkpoint LINE to its own file BEFORE each dangerous call, and the
  line is flushed as it is written. What survives a termination is the last thing
  written, so the last line of case-NN.jsonl names the stage Revit was in - which
  is the difference between "it died somewhere" and a diagnosis.

  Set-Content per append would rewrite the file; Add-Content appends and closes.
#>
function Start-WsCase {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][int]$Number,
        [Parameter(Mandatory)][string]$Name,
        [long]$WallId = 0,
        [string]$WallUniqueId,
        [string]$Operation = 'none'
    )
    $ctx = [ordered]@{
        Run = $Run; Number = $Number; Name = $Name
        WallId = $WallId; WallUniqueId = $WallUniqueId; Operation = $Operation
        File = (Join-Path $Run.ArtifactDir ("case-{0:d2}.jsonl" -f $Number))
        LastRegenerate = 'none'; LastJoin = 'none'
    }
    if (Test-Path -LiteralPath $ctx.File) { Remove-Item -LiteralPath $ctx.File -Force }
    Write-WsCheckpoint $ctx 'case_started' 'the case has not called Revit yet'
    $ctx
}

function Write-WsCheckpoint {
    param(
        [Parameter(Mandatory)]$Ctx,
        [Parameter(Mandatory)][string]$Stage,
        [string]$Known,
        [string]$Artifact
    )
    # RELATIVE, ALWAYS. An absolute artifact path carries the machine's user name
    # into evidence that is meant to be shareable.
    $rel = $Artifact
    if ($rel) { $rel = Split-Path -Leaf $rel }
    $line = [ordered]@{
        case            = $Ctx.Number
        name            = $Ctx.Name
        utc             = (Get-Date).ToUniversalTime().ToString('o')
        head            = $script:HzHead
        installed_commit = $script:HzInstalled
        document_fingerprint = $script:HzDocFingerprint
        wall_element_id = $Ctx.WallId
        wall_unique_id  = $Ctx.WallUniqueId
        operation       = $Ctx.Operation
        stage           = $Stage
        last_regenerate = $Ctx.LastRegenerate
        last_join       = $Ctx.LastJoin
        known_result    = $Known
        artifact        = $rel
    }
    Add-Content -LiteralPath $Ctx.File -Value ($line | ConvertTo-Json -Depth 8 -Compress) -Encoding UTF8
}

<#
  The identity every checkpoint stamps. Set once, after health answers, so a
  checkpoint can never claim a commit nobody measured.
#>
function Set-WsIdentity {
    param([string]$Head, [string]$Installed, [string]$DocumentFingerprint)
    $script:HzHead = $Head
    $script:HzInstalled = $Installed
    $script:HzDocFingerprint = $DocumentFingerprint
}
$script:HzHead = 'unset'
$script:HzInstalled = 'unset'
$script:HzDocFingerprint = 'unset'

<#
  One case of the matrix.

  `not_covered` and `fixture_missing` are FIRST-CLASS answers, never folded into
  passed. The whole point of this campaign is that a case nobody could build is
  reported as a case nobody could build.
#>
function Add-WsCase {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][int]$Number,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Expected,
        [string]$Observed,
        [ValidateSet('passed', 'failed', 'unverified', 'not_run', 'blocked_fixture', 'blocked_environment', 'unsupported_api')]
        [Parameter(Mandatory)][string]$Status,
        $Evidence,
        [string]$Because
    )
    # SEVEN ANSWERS, NOT FOUR. 'not_covered' used to carry three different things at
    # once - a case nobody could build a fixture for, a case that needs a second
    # human, and a case the API does not expose - and a reader could not tell which.
    # Only 'passed' is good; 'not_run' means the campaign never reached it, which is
    # a fact about the run and never about the product.
    $row = [ordered]@{
        case         = $Number
        name         = $Name
        expected     = $Expected
        observed     = $Observed
        status       = $Status
        because      = $Because
        tolerance_mm = 0.5
        evidence     = $Evidence
        recorded_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $null = $Run.Probes.Add($row)

    # DURABLE, IMMEDIATELY. The roll-up is rebuilt from these files alone, so a run
    # that dies at case 40 still has 39 answers on disk. Writing them only at the
    # end is how the previous session lost everything after the crash.
    $file = Join-Path $Run.ArtifactDir ("case-{0:d2}-final.json" -f $Number)
    $row | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $file -Encoding UTF8

    $mark = switch ($Status) {
        'passed' { 'PASS' } 'failed' { 'FAIL' } 'unverified' { 'UNVERIFIED' }
        'not_run' { 'NOT RUN' } 'blocked_fixture' { 'BLOCKED FIXTURE' }
        'blocked_environment' { 'BLOCKED ENV' } 'unsupported_api' { 'UNSUPPORTED API' }
    }
    $colour = switch ($Status) { 'passed' { 'Green' } 'failed' { 'Red' } default { 'Yellow' } }
    Write-Host ("  {0,-16} {1,3}  {2}" -f $mark, $Number, $Name) -ForegroundColor $colour
    if ($Status -ne 'passed' -and $Observed) {
        Write-Host ("                       observed: " + (Limit-WsText $Observed 200)) -ForegroundColor DarkYellow
    }
}

function Limit-WsText {
    param([string]$Text, [int]$Max = 300)
    if (-not $Text) { return $Text }
    $one = ($Text -replace '\s+', ' ').Trim()
    if ($one.Length -le $Max) { return $one }
    $one.Substring(0, $Max) + ' ...'
}

<#
  Run a python fixture script. Fixtures are BUILT with python because a compound
  wall type, a bar type and a wall foundation have no typed create command - this
  is the same route scripts/live/verify-rebar.ps1 already takes, and the machine
  owner granted the permission persistently on 2026-08-21.

  If the permission is not in force, every fixture that needs it becomes
  fixture_missing and the cases that depend on it are reported as such. It is
  never inferred that they would have passed.
#>
function Invoke-WsPython {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Code
    )
    $file = Join-Path $Run.ArtifactDir ("py-{0}.py" -f ($Label -replace '[^\w\-]', '_'))
    Set-Content -LiteralPath $file -Value $Code -Encoding UTF8
    Invoke-Ws -Run $Run -Tool 'horizun_execute_python' -Label $Label -AllowError -Mutates -Arguments @{
        target_document = $Run.Document
        code_path       = $file
    }
}

function Get-WsOutput {
    param($Call)
    if (-not $Call -or -not $Call.Result) { return $null }
    $names = @($Call.Result.PSObject.Properties.Name)
    if ($names -contains '__output__') { return $Call.Result.__output__ }
    if ($names -contains 'output') { return $Call.Result.output }
    $null
}

function Save-WsRun {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)]$Identity)
    $byStatus = @{}
    foreach ($p in $Run.Probes) {
        if (-not $byStatus.ContainsKey($p.status)) { $byStatus[$p.status] = 0 }
        $byStatus[$p.status]++
    }
    $passed = [int]$byStatus['passed']
    $failed = [int]$byStatus['failed']
    $unver  = [int]$byStatus['unverified']
    # EXECUTED means the campaign actually asked Revit and got an answer, good or
    # bad. A case that was blocked was never asked, so counting it in the
    # denominator of a pass RATE would let a blocked case flatter the rate; counting
    # it out of the denominator of COVERAGE would hide it. Both rates are published.
    $executed = $passed + $failed + $unver
    $summary = [ordered]@{
        harness             = $Run.Name
        run_id              = $Run.RunId
        document            = $Run.Document
        identity            = $Identity
        tolerance_mm        = 0.5
        total               = $Run.Probes.Count
        passed              = $passed
        failed              = $failed
        unverified          = $unver
        not_run             = [int]$byStatus['not_run']
        blocked_fixture     = [int]$byStatus['blocked_fixture']
        blocked_environment = [int]$byStatus['blocked_environment']
        unsupported_api     = [int]$byStatus['unsupported_api']
        executed            = $executed
        executed_pass_rate  = $(if ($executed -gt 0) { [math]::Round($passed / $executed, 4) } else { 0 })
        coverage_rate       = [math]::Round($executed / 55.0, 4)
        verified_pass_rate  = [math]::Round($passed / 55.0, 4)
        case_results        = @($Run.Probes)
        notes               = @($Run.Notes)
        recorded_utc        = (Get-Date).ToUniversalTime().ToString('o')
    }
    $path = Join-Path $Run.ArtifactDir ($Run.Name + '.json')
    $summary | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding UTF8

    Write-Host ""
    Write-Host ("  total {0}  passed {1}  failed {2}  unverified {3}  not_run {4}  blocked_fixture {5}  blocked_env {6}  unsupported_api {7}" -f
        $summary.total, $summary.passed, $summary.failed, $summary.unverified, $summary.not_run,
        $summary.blocked_fixture, $summary.blocked_environment, $summary.unsupported_api) -ForegroundColor Cyan
    Write-Host ("  executed {0}/55  executed_pass_rate {1}  coverage_rate {2}  verified_pass_rate {3}" -f
        $summary.executed, $summary.executed_pass_rate, $summary.coverage_rate, $summary.verified_pass_rate) -ForegroundColor Cyan
    Write-Host ("  artifact: " + $path) -ForegroundColor DarkGray
    $summary
}
