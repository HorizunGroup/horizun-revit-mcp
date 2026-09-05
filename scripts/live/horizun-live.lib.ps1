#Requires -Version 5.1
<#
  THE SHARED LIVE-HARNESS LIBRARY.

  Every DWG harness in scripts/live/ talks to a running Revit through the same
  MCP server a client uses - JSON-RPC over stdio, tools/call, nothing privileged -
  and records what it saw in an artifact somebody who was not here can read.

  IT EXISTS BECAUSE THE FIRST VERSIONS DID NOT.

  DWG-1, DWG-2 and DWG-3 were proved by three PowerShell files in a session
  scratchpad. They passed 50 probes between them, the artifacts named the files,
  and the files were in a temp directory that does not survive the session. An
  artifact that names a harness nobody else can run is a claim, not evidence -
  so the harnesses are versioned now, and so is this.

  WHAT A HARNESS MUST NOT DO, each learned by watching one lie:

    trust an element id's ORDER          ids come back in creation order, which is
                                         the drawing's order, not the building's.
                                         A probe that moved "the second wall"
                                         moved the one the drawing also moved,
                                         and measured the case it existed to
                                         exclude. Locate by GEOMETRY.

    reuse an idempotency key             a completed claim replays. Across a Revit
                                         restart the replay described a session
                                         that had died: three staged opens
                                         answered "opened" into an empty Revit and
                                         57 probes failed as if the product were
                                         broken. Keys are unique per RUN and per
                                         PROCESS, always.

    inherit the last run's model         the fixture DWG is a picture of walls the
                                         fixture itself builds. Leave them in the
                                         model and the next run matches its own
                                         scaffolding: "3 matched before anything
                                         was built" was a TRUE answer to a
                                         question nobody asked.

    believe a staging step               a typed open of an already-open document
                                         does not reload it, and says so. Measure
                                         the state afterwards; do not take the
                                         reply's word for it.

    read a name Revit chose              "Level 1" is English. Nothing here matches
                                         on a name Revit invented; identity comes
                                         from ids the run itself resolved, or from
                                         geometry.

  Dot-source this, then call New-HzRun.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# =============================================================================
# THE RUN
# =============================================================================

<#
  One live run: where it writes, what it is measuring, and the evidence it has
  gathered so far. Everything else in this file takes it as its first argument.
#>
function New-HzRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Harness,       # the harness FILE, so the artifact can name and hash it
        [Parameter(Mandatory)][string]$Name,          # short id: dwg-chain, dwg-audit, ...
        [string]$Document = 'HZ_WRITE',
        [string]$RepoRoot,
        [string]$WorkDir
    )
    if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
    if (-not $WorkDir) { $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('horizun-live-' + [guid]::NewGuid().ToString('N').Substring(0, 12)) }
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

    # UNIQUE PER RUN AND PER PROCESS. A key is a claim that two calls are the
    # same call; two runs are never the same call, and neither are two processes.
    $runId = '{0}-{1}-{2}' -f $Name, (Get-Date).ToString('yyyyMMddHHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8)

    [pscustomobject]@{
        Name        = $Name
        RunId       = $runId
        Harness     = (Resolve-Path -LiteralPath $Harness).Path
        RepoRoot    = (Resolve-Path -LiteralPath $RepoRoot).Path
        WorkDir     = $WorkDir
        Document    = $Document
        Probes      = New-Object System.Collections.ArrayList
        Fixture     = [ordered]@{}
        Expected    = [ordered]@{}
        Notes       = New-Object System.Collections.ArrayList
        Calls       = 0
        StartedUtc  = (Get-Date).ToUniversalTime().ToString('o')
        Health      = $null
        KeySeq      = [ref]0
    }
}

<#
  A key nothing else can have used: the run id (unique per run) plus this
  process's id plus a monotonic sequence. The ledger is durable and shared, so
  "nothing else" has to mean across restarts too.
#>
function New-HzKey {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Purpose)
    $Run.KeySeq.Value = $Run.KeySeq.Value + 1
    '{0}-p{1}-{2}-{3}' -f $Run.RunId, $PID, $Purpose, $Run.KeySeq.Value
}

# =============================================================================
# TALKING TO THE BRIDGE
# =============================================================================

<#
  One tools/call, through the same script a human would use. Returns a uniform
  shape whether the bridge answered, refused, or never replied - a harness that
  has to tell those apart cannot be reading $r.result and hoping.
#>
function Invoke-HzTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Arguments = @{},
        [string]$Label,
        [int]$TimeoutSec = 600
    )
    if (-not $Label) { $Label = $Tool }
    $Run.Calls = $Run.Calls + 1
    $safeLabel = ($Label -replace '[^A-Za-z0-9_.-]', '_')
    $stem = Join-Path $Run.WorkDir ('call-{0:0000}-{1}' -f $Run.Calls, $safeLabel)
    $argPath = "$stem.args.json"
    $outPath = "$stem.out.json"

    ($Arguments | ConvertTo-Json -Depth 40 -Compress) | Set-Content -LiteralPath $argPath -Encoding utf8
    $caller = Join-Path $Run.RepoRoot 'scripts\hz-call.ps1'
    & pwsh -NoProfile -File $caller -Tool $Tool -ArgumentsPath $argPath -Json $outPath -Quiet -TimeoutSec $TimeoutSec *> $null

    if (-not (Test-Path $outPath)) {
        return [pscustomobject]@{
            Tool = $Tool; Label = $Label; Ok = $false; Answered = $false; IsError = $true
            Result = $null; Text = 'the bridge produced no reply file at all'; Raw = $null
            ArgumentsPath = $argPath; OutPath = $outPath
        }
    }
    $reply = Get-Content -LiteralPath $outPath -Raw | ConvertFrom-Json
    $isError = [bool](Get-HzProp $reply 'is_error')
    $rawText = Get-HzProp $reply 'raw'
    $text = if ($null -ne $rawText) { [string]$rawText } else { '' }
    [pscustomobject]@{
        Tool = $Tool; Label = $Label; Ok = (-not $isError); Answered = $true; IsError = $isError
        Result = (Get-HzProp $reply 'result'); Text = $text; Raw = $reply
        ArgumentsPath = $argPath; OutPath = $outPath
        DurationMs = (Get-HzProp $reply 'duration_ms')
    }
}

<#
  The same call, where a refusal is a HARNESS failure rather than a finding.
  Staging is the usual caller: if the setup did not happen there is nothing to
  measure, and every probe after it would report the staging failure as a
  product defect. That distinction is the reason this function exists.
#>
function Invoke-HzToolStrict {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Arguments = @{},
        [string]$Label,
        [int]$TimeoutSec = 600
    )
    $r = Invoke-HzTool -Run $Run -Tool $Tool -Arguments $Arguments -Label $Label -TimeoutSec $TimeoutSec
    if (-not $r.Ok) {
        throw ("HARNESS: {0} ({1}) did not do what this run needs: {2}" -f $Tool, $Label, (Limit-HzText $r.Text 400))
    }
    $r
}

<#
  A typed write, both halves: rehearse, take the token that rehearsal issued,
  then apply with it. The token is the point - it is bound to the state the
  rehearsal saw, so an apply that gets one has been checked against the model as
  it was a moment ago rather than as the caller remembers it.
#>
function Invoke-HzWrite {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Tool,
        [Parameter(Mandatory)][hashtable]$Arguments,
        [Parameter(Mandatory)][string]$Label,
        [switch]$AllowRefusal,
        [int]$TimeoutSec = 900
    )
    $dryArgs = Copy-HzArgs $Arguments @{ dry_run = $true }
    $dry = Invoke-HzTool -Run $Run -Tool $Tool -Arguments $dryArgs -Label "$Label-dry" -TimeoutSec $TimeoutSec
    if (-not $dry.Ok) {
        if ($AllowRefusal) { return [pscustomobject]@{ Stage = 'dry_run'; Dry = $dry; Apply = $null; Ok = $false } }
        throw ("HARNESS: {0} ({1}) refused during rehearsal: {2}" -f $Tool, $Label, (Limit-HzText $dry.Text 400))
    }
    $token = Get-HzProp $dry.Result 'confirmation_token'

    $applyArgs = Copy-HzArgs $Arguments @{ dry_run = $false; idempotency_key = (New-HzKey $Run $Label) }
    if ($token) { $applyArgs['confirmation_token'] = $token }
    $apply = Invoke-HzTool -Run $Run -Tool $Tool -Arguments $applyArgs -Label $Label -TimeoutSec $TimeoutSec
    if (-not $apply.Ok -and -not $AllowRefusal) {
        throw ("HARNESS: {0} ({1}) refused the apply: {2}" -f $Tool, $Label, (Limit-HzText $apply.Text 400))
    }
    # The document a write actually landed in, as the gate fingerprinted it. It
    # is picked up here rather than asked for, because every typed write carries
    # it and no read does - and an artifact that names the document by TITLE
    # alone cannot tell two files with the same title apart.
    # FROM THE REHEARSAL, WHICH IS WHERE IT IS.
    #
    # DocumentGate stamps the document and its fingerprint when it ISSUES a
    # confirmation - that is, on the dry run. The apply consumes the token and
    # answers with what it built. Reading only the apply left this field null in
    # every artifact, which is a hole in the evidence rather than a fact about
    # the document.
    if (-not $Run.Fixture['document_fingerprint']) {
        foreach ($source in @($dry, $apply)) {
            if ($null -eq $source -or -not $source.Ok) { continue }
            $fp = Get-HzProp $source.Result 'document_fingerprint'
            if ($fp) { $Run.Fixture['document_fingerprint'] = [string]$fp; break }
        }
    }
    [pscustomobject]@{ Stage = 'apply'; Dry = $dry; Apply = $apply; Ok = $apply.Ok }
}

<#
  Merge two argument sets. PowerShell refuses to '+' two hashtables that share a
  key, which is how a probe once sent 'actions' twice and crashed a run.
#>
function Copy-HzArgs {
    param([Parameter(Mandatory)]$Base, $Overrides = @{})
    $c = @{}
    foreach ($k in $Base.Keys) { $c[$k] = $Base[$k] }
    foreach ($k in $Overrides.Keys) { $c[$k] = $Overrides[$k] }
    $c
}

<#
  A property that may not be there.

  Under Set-StrictMode -Version Latest - which every harness here runs under, on
  purpose - reading an absent property THROWS. That strictness is what catches a
  typo in a field name instead of silently comparing against $null, and it is
  worth keeping. But a reply legitimately omits fields: a close that discards
  nothing issues no confirmation token, a read carries no document fingerprint,
  an element with no location carries no bounding box.

  ABSENT AND EMPTY ARE DIFFERENT, and this returns $null for absent so a caller
  can tell. It never invents a default.
#>
function Get-HzProp {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    try {
        if ($Object -is [System.Collections.IDictionary]) {
            if ($Object.Contains($Name)) { return $Object[$Name] }
            return $null
        }
        $names = @($Object.PSObject.Properties.Name)
        if ($names -contains $Name) { return $Object.$Name }
    } catch { }
    $null
}

<#
  Walk a path of optional properties: Get-HzPath $r 'result','rehearsal','tokens_by_key'.
#>
function Get-HzPath {
    param($Object, [Parameter(Mandatory)][string[]]$Path)
    $cursor = $Object
    foreach ($step in $Path) {
        $cursor = Get-HzProp $cursor $step
        if ($null -eq $cursor) { return $null }
    }
    $cursor
}

# =============================================================================
# THE HOST
# =============================================================================

function Get-HzHealth {
    param([Parameter(Mandatory)]$Run)
    $h = Invoke-HzToolStrict -Run $Run -Tool 'horizun_health' -Arguments @{} -Label 'health' -TimeoutSec 90
    $Run.Health = $h.Result
    $h.Result
}

<#
  Open or activate one explicitly named disposable fixture, then prove Revit
  really made it active. Opening an already-open file is the typed activation
  path; it does not reload or save the document.
#>
function Set-HzActiveDocument {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Document,
        [string]$FilePath
    )

    $health = Get-HzHealth $Run
    if ([string](Get-HzPath $health 'active_document','title') -eq $Document) { return $health }

    if (-not $FilePath) {
        foreach ($open in @(Get-HzProp $health 'open_documents')) {
            if ([string](Get-HzProp $open 'title') -eq $Document) {
                $FilePath = [string](Get-HzProp $open 'path')
                break
            }
        }
    }
    if (-not $FilePath) {
        throw "HARNESS: '$Document' is not open and no fixture path was supplied."
    }
    if (-not (Test-Path -LiteralPath $FilePath)) {
        throw "HARNESS: the fixture for '$Document' does not exist at '$FilePath'."
    }

    $year = [string](Get-HzProp $health 'revit_version')
    if (-not $year) { throw 'HARNESS: health did not report the Revit year before document activation.' }
    $null = Invoke-HzToolStrict -Run $Run -Tool 'horizun_document_session' -Label ('activate-' + $Document) -Arguments @{
        operation = 'open'; file_path = $FilePath; expected_version = $year; allow_upgrade = $false
        idempotency_key = (New-HzKey $Run ('activate-' + $Document))
    }

    $after = Get-HzHealth $Run
    $actual = [string](Get-HzPath $after 'active_document','title')
    if ($actual -ne $Document) {
        throw "HARNESS: requested '$Document' but Revit made '$actual' active."
    }
    $after
}

<#
  A document nobody has built in.

  A typed open of an ALREADY-OPEN document does not reload it - it answers
  'already_open_and_active', honestly - so the leftovers of the last run stay
  put. Revit's API also cannot close the active document, which is why
  activate_other exists. Both facts were learned by a run that measured its own
  scaffolding and passed.
#>
function Reset-HzDocument {
    param([Parameter(Mandatory)]$Run, [string]$Document)
    if (-not $Document) { $Document = $Run.Document }
    $health = Get-HzHealth $Run
    if ([string]$health.active_document.title -ne $Document) {
        throw ("HARNESS: the active document is '{0}', not '{1}'. This harness will not close a document it did not open." -f $health.active_document.title, $Document)
    }
    $path = [string]$health.active_document.path

    $closeArgs = @{
        operation = 'close'; target_document = $Document
        discard_unsaved = $true; activate_other = $true
    }
    $dry = Invoke-HzToolStrict -Run $Run -Tool 'horizun_document_session' `
        -Arguments (Copy-HzArgs $closeArgs @{ dry_run = $true; idempotency_key = (New-HzKey $Run 'closedry') }) -Label 'reset-close-dry'
    # A CLOSE THAT DISCARDS NOTHING ISSUES NO TOKEN, and says so: the token exists
    # to make destroying unsaved work deliberate, and there is nothing to destroy
    # in a document nobody has touched. Sending an empty one would be refused.
    $closeReal = Copy-HzArgs $closeArgs @{ dry_run = $false; idempotency_key = (New-HzKey $Run 'close') }
    $token = Get-HzProp $dry.Result 'confirmation_token'
    if ($token) { $closeReal['confirmation_token'] = [string]$token }
    $null = Invoke-HzToolStrict -Run $Run -Tool 'horizun_document_session' -Arguments $closeReal -Label 'reset-close'

    # THE YEAR THE HOST ACTUALLY IS, not the one this file was written on.
    # expected_version was hard-coded to '2026', so every harness that resets its
    # document refused to reopen it on any other Revit - which would have made
    # each of the other four years of the multiversion matrix fail for a reason
    # that is in this line rather than in the product.
    $expectedYear = [string]$health.revit_version
    if (-not $expectedYear) {
        throw 'HARNESS: health did not report a Revit version, so there is no year to open this file against.'
    }
    $open = Invoke-HzToolStrict -Run $Run -Tool 'horizun_document_session' -Arguments @{
        operation = 'open'; file_path = $path; expected_version = $expectedYear; allow_upgrade = $false
        idempotency_key = (New-HzKey $Run 'open')
    } -Label 'reset-open'
    if ([string]$open.Result.status -eq 'already_open_and_active') {
        throw 'HARNESS: the document did not actually reopen, so it still holds the last run and every probe below would measure that'
    }
    # DO NOT TAKE THE REPLY'S WORD FOR IT.
    $after = Get-HzHealth $Run
    if ([string]$after.active_document.title -ne $Document) {
        throw ("HARNESS: after the reset the active document is '{0}'" -f $after.active_document.title)
    }
    $after
}

<#
  Every element in a box, with its bounding box and unique id - the typed way to
  find something without knowing its id, its name or its place in a collection.
  Coordinates are millimetres.
#>
function Get-HzElementsIn {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string[]]$Categories,
        [Parameter(Mandatory)][double[]]$Min,     # x,y,z
        [Parameter(Mandatory)][double[]]$Max,
        [int]$MaxRows = 2000,
        [string]$Label = 'in-box'
    )
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_model' -Arguments @{
        categories = $Categories
        include_links = $false
        include_bounding_box = $true
        bounding_box = @{ min = $Min; max = $Max; units = 'mm' }
        max_rows = $MaxRows
    } -Label $Label
    # A PLAIN ARRAY, and every caller wraps the call in @().
    #
    # The comma operator would keep a one-row result an array through the return
    # - and then NEST it when the result was piped, so Where-Object saw a single
    # item that was the whole array and [long]$_.element_id threw. @() at the
    # call site is the idiom that works for .Count and for piping both.
    @($q.Result.rows | Where-Object { -not $_.is_element_type })
}

function Get-HzElements {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string[]]$Categories,
        [int]$MaxRows = 3000,
        [switch]$WithBox,
        [string]$Label = 'elements'
    )
    # NOT $args: that is an automatic variable, and assigning to it is the kind
    # of quiet breakage a harness cannot afford.
    $query = @{ categories = $Categories; include_links = $false; max_rows = $MaxRows }
    if ($WithBox) { $query['include_bounding_box'] = $true }
    $q = Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_model' -Arguments $query -Label $Label
    @($q.Result.rows | Where-Object { -not $_.is_element_type })
}

<#
  HOW MANY ELEMENTS OF THESE CATEGORIES EXIST - exactly, and never the page.

  Get-HzElements returns a PAGE. Counting it is fine for a category a fixture
  just created and catastrophic for one the document was already full of:
  MEASURED, a probe compared 500 ducts before against 500 after and reported a
  delta of nought while the commit had verifiably created two. Both numbers were
  the row cap.

  horizun_query_model publishes matched_total, which is exact and independent of
  max_rows and says so in its own description. That is what a count is.
#>
function Get-HzElementCount {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string[]]$Categories,
        [string]$Label = 'element-count'
    )
    [int](Invoke-HzToolStrict -Run $Run -Tool 'horizun_query_model' -Label $Label -Arguments @{
        categories = $Categories; include_links = $false; max_rows = 1
    }).Result.matched_total
}

<#
  The centre of a row's bounding box, in mm. A wall's box centre is on its
  centreline, which is what a drawing draws - so this is how a probe says "the
  wall the drawing puts HERE" without an id.
#>
function Get-HzBoxCentre {
    param([Parameter(Mandatory)]$Row)
    $b = Get-HzProp $Row 'bounding_box'
    if ($null -eq $b) { return $null }
    @(
        (([double]$b.min[0] + [double]$b.max[0]) / 2.0),
        (([double]$b.min[1] + [double]$b.max[1]) / 2.0),
        (([double]$b.min[2] + [double]$b.max[2]) / 2.0)
    )
}

<#
  The values an enum-valued argument accepts, READ FROM THE SERVER'S OWN
  tools/list. A harness that has to know whether a capability exists in this
  build must ask the contract, not infer it from how a refusal is worded - the
  wording is prose and changes; the schema is the promise.

  Cached per run: tools/list is one round trip and does not change mid-run.
#>
function Get-HzToolEnum {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Tool,
        [Parameter(Mandatory)][string]$Property
    )
    if (-not ($Run.PSObject.Properties.Name -contains 'ToolList')) {
        $listed = $null
        try { $listed = Get-HzToolList $Run } catch { $listed = $null }
        Add-Member -InputObject $Run -NotePropertyName 'ToolList' -NotePropertyValue $listed -Force
    }
    if (-not $Run.ToolList) { return @() }
    $entry = @($Run.ToolList | Where-Object { $_.name -eq $Tool })
    if ($entry.Count -eq 0) { return @() }
    $values = @()
    try {
        $prop = $entry[0].inputSchema.properties.$Property
        if ($prop) {
            if ($prop.PSObject.Properties.Name -contains 'enum') { $values = @($prop.enum) }
            elseif (($prop.PSObject.Properties.Name -contains 'items') -and
                    ($prop.items.PSObject.Properties.Name -contains 'enum')) { $values = @($prop.items.enum) }
        }
    } catch { $values = @() }
    $values
}

<#
  A published MCP RESOURCE, from the server this run is talking to.

  The contract hash lives here - horizun://build/identity - and nowhere a tool
  call can reach it. The first version of this library asked horizun_health for
  it, and health does not publish one, so the branch could never fire and every
  manifest silently fell back to docs/inventory.json: the hash of whatever binary
  the inventory generator last measured, printed under a field name that says it
  came from this run. That is precisely the confusion the inventory's own
  provenance fields were added to end, so it is not repeated here.
#>
function Get-HzResource {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Uri, [string]$Label = 'resource')
    $stem = Join-Path $Run.WorkDir ('resource-' + ($Label -replace '[^A-Za-z0-9_.-]', '_'))
    '{}' | Set-Content -LiteralPath "$stem.args.json" -Encoding utf8
    $out = "$stem.out.json"
    $caller = Join-Path $Run.RepoRoot 'scripts\hz-call.ps1'
    & pwsh -NoProfile -File $caller -Tool 'resource' -Resource $Uri `
        -ArgumentsPath "$stem.args.json" -Json $out -Quiet -TimeoutSec 90 *> $null
    if (-not (Test-Path $out)) { return $null }
    $reply = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    if (Get-HzProp $reply 'is_error') { return $null }
    Get-HzProp $reply 'result'
}

<#
  tools/list, over stdio, from the installed server - the same conversation a
  client has. Not the source tree: the source tree is not what this run talks to.
#>
function Get-HzToolList {
    param([Parameter(Mandatory)]$Run)
    # The same server hz-call drives: a development session points both at a
    # fresh build through HORIZUN_SERVER_EXE, and the installed one is the default.
    $exe = if ($env:HORIZUN_SERVER_EXE) { $env:HORIZUN_SERVER_EXE } else { Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
    if (-not (Test-Path $exe)) { return $null }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    if ($psi.PSObject.Properties.Name -contains 'StandardInputEncoding') {
        $psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    }
    $proc = [Diagnostics.Process]::Start($psi)
    try {
        $send = {
            param($o)
            $proc.StandardInput.WriteLine(($o | ConvertTo-Json -Depth 24 -Compress))
            $proc.StandardInput.Flush()
        }
        $recv = {
            param([int]$TimeoutMs = 30000)
            $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
            while ($true) {
                $t = $proc.StandardOutput.ReadLineAsync()
                $remaining = [Math]::Max(1, [int](($deadline - (Get-Date)).TotalMilliseconds))
                $winner = [Threading.Tasks.Task]::WhenAny(
                    [Threading.Tasks.Task[]]@($t, [Threading.Tasks.Task]::Delay($remaining))).Result
                if (-not [object]::ReferenceEquals($winner, $t)) { return $null }
                if (-not $t.Result) { return $null }
                try { $m = $t.Result | ConvertFrom-Json } catch { continue }
                if ($m.PSObject.Properties.Name -contains 'id') { return $m }
            }
        }
        & $send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
            protocolVersion = '2025-11-25'; capabilities = @{}
            clientInfo = @{ name = 'horizun-live-harness'; version = '1' } } }
        $null = & $recv
        & $send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        & $send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
        $listed = & $recv
        if (-not $listed) { return $null }
        @($listed.result.tools)
    } finally {
        try { $proc.StandardInput.Close() } catch { }
        if (-not $proc.WaitForExit(8000)) { try { $proc.Kill() } catch { } }
    }
}

# =============================================================================
# PROBES
# =============================================================================

<#
  One measurement, recorded with what it EXPECTED before it looked. A probe that
  only records what happened is a description; the expectation is what makes it
  a test.

  Status is deliberately not a boolean:
    passed          expected and observed agree
    failed          they do not
    unverified      the call errored, so the check never ran
    not_covered     this run does not test it, and says so
    fixture_missing the input this needs is absent - NOT a pass, NOT a product failure
    not_assessable  it ran, but incomplete evidence cannot support a verdict
    not_applicable  the requested capability does not apply to this case
    available       the surface exists; another probe owns its live evidence
    implemented_not_live_verified
                    code and contract exist, but this run did not measure them
#>
function Add-HzProbe {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Expected,
        [string]$Observed,
        [ValidateSet('passed', 'failed', 'unverified', 'not_covered', 'fixture_missing',
                     'not_assessable', 'not_applicable', 'available',
                     'implemented_not_live_verified')][string]$Status,
        [bool]$Ok,
        $Evidence,
        [string]$Because
    )
    if (-not $Status) { $Status = $(if ($Ok) { 'passed' } else { 'failed' }) }
    $null = $Run.Probes.Add([ordered]@{
        id = $Id
        name = $Name
        expected = $Expected
        observed = $Observed
        status = $Status
        because = $Because
        evidence = (Protect-HzValue $Evidence)
        recorded_utc = (Get-Date).ToUniversalTime().ToString('o')
    })
    $mark = switch ($Status) {
        'passed' { 'PASS' } 'failed' { 'FAIL' } 'unverified' { 'UNVERIFIED' }
        'not_covered' { 'NOT COVERED' } 'fixture_missing' { 'FIXTURE MISSING' }
        'not_assessable' { 'NOT ASSESSABLE' } 'not_applicable' { 'NOT APPLICABLE' }
        'available' { 'AVAILABLE' }
        'implemented_not_live_verified' { 'IMPLEMENTED, NOT LIVE VERIFIED' }
    }
    $colour = switch ($Status) {
        'passed' { 'Green' } 'failed' { 'Red' } default { 'Yellow' }
    }
    Write-Host ("  {0,-15} {1,-5} {2}" -f $mark, $Id, $Name) -ForegroundColor $colour
    if ($Status -ne 'passed') {
        Write-Host ("                  expected: {0}" -f $Expected) -ForegroundColor DarkYellow
        if ($Observed) { Write-Host ("                  observed: {0}" -f $Observed) -ForegroundColor DarkYellow }
    }
}

<#
  A refusal that was the POINT. An expected refusal and an unexpected error look
  identical in a transcript, so the probe records which one it asked for and
  matches the reason - a command that refuses for the wrong reason has not
  passed.
#>
function Add-HzRefusalProbe {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Call,
        [Parameter(Mandatory)][string]$MustMatch,
        [string]$Expected
    )
    if (-not $Expected) { $Expected = "refused, and the reason matches /$MustMatch/" }
    $refused = $Call.IsError
    $matched = $refused -and ($Call.Text -match $MustMatch)
    Add-HzProbe -Run $Run -Id $Id -Name $Name -Expected $Expected `
        -Observed $(if ($refused) { 'refused: ' + (Limit-HzText $Call.Text 220) } else { 'the call SUCCEEDED - nothing refused it' }) `
        -Status $(if ($matched) { 'passed' } else { 'failed' }) `
        -Evidence @{ refused = $refused; reason_matched = $matched; pattern = $MustMatch; reply = (Limit-HzText $Call.Text 900) }
}

<#
  A write whose own post-commit verification is the evidence. host_verified is
  the bridge's claim that it RE-READ the model after committing; a harness that
  only checks "no error" is trusting the call rather than the model.
#>
function Assert-HzHostVerified {
    param([Parameter(Mandatory)]$Call, [Parameter(Mandatory)][string]$What)
    $result = $Call.Result
    $names = @($result.PSObject.Properties.Name)
    if ($names -contains 'host_verified') {
        if (-not $result.host_verified) { throw "HARNESS: $What reported host_verified=false" }
        return $true
    }
    # Typed commands prove it by re-reading rather than by a flag; the callers
    # below assert on the count they re-read. Say which happened, never guess.
    $false
}

# =============================================================================
# SANITISING, HASHING, TEXT
# =============================================================================

<#
  Nothing personal reaches an artifact. Artifacts are read by people who were not
  here and may be attached to a report; a home directory or a machine name in one
  is a leak that survives every later redaction.
#>
function Protect-HzText {
    param([string]$Text)
    if (-not $Text) { return $Text }
    $out = $Text
    foreach ($pair in @(
        @{ Root = $env:LOCALAPPDATA; Token = '<localappdata>' },
        @{ Root = $env:APPDATA;      Token = '<appdata>' },
        @{ Root = $env:USERPROFILE;  Token = '<userprofile>' },
        @{ Root = [IO.Path]::GetTempPath().TrimEnd('\'); Token = '<temp>' }
    )) {
        if ($pair.Root) { $out = $out.Replace($pair.Root, $pair.Token) }
    }
    if ($env:USERNAME)     { $out = $out.Replace($env:USERNAME, '<user>') }
    if ($env:COMPUTERNAME) { $out = $out.Replace($env:COMPUTERNAME, '<machine>') }
    $out
}

function Protect-HzValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return Protect-HzText $Value }
    $json = $Value | ConvertTo-Json -Depth 20 -Compress
    (Protect-HzText $json) | ConvertFrom-Json
}

function Limit-HzText {
    param([string]$Text, [int]$Max = 300)
    if (-not $Text) { return '' }
    $clean = Protect-HzText $Text
    if ($clean.Length -le $Max) { return $clean }
    $clean.Substring(0, $Max) + '...'
}

function Get-HzSha256 {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    # A FILE REVIT HAS OPEN IS STILL READABLE, AND Get-FileHash WILL NOT READ IT.
    # Measured 2026-09-03: hashing the active .rvt threw "being used by another
    # process" and took a gate probe down with it - the file was fine, the share
    # mode was not. Opened with FileShare.ReadWrite, the same bytes hash cleanly.
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read,
                                     [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
        }
        finally { $fs.Dispose() }
    }
    catch { return $null }
}

# =============================================================================
# THE ARTIFACT
# =============================================================================

<#
  Everything a reader needs to decide whether to believe this run, gathered from
  the machine rather than from the run's own memory:

    the HARNESS  - its path, its git blob, its sha256, whether the working copy
                   matches what is committed. A harness nobody can reproduce is
                   not evidence, and a MODIFIED harness is not the committed one.
    the CODE     - the commit the ADD-IN was built from, read from the add-in;
                   the server's sha256; the contract hash both halves agreed on.
                   Not git HEAD: HEAD moves for documentation.
    the HOST     - Revit year, build, process, and the document, by fingerprint.
    the FIXTURE  - its id, its DWG's sha256, and the requirement set's identity.
#>
function Get-HzManifest {
    param([Parameter(Mandatory)]$Run)

    $repo = $Run.RepoRoot
    $harnessRel = $Run.Harness
    if ($harnessRel.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        $harnessRel = $harnessRel.Substring($repo.Length).TrimStart('\', '/') -replace '\\', '/'
    }

    $blob = $null; $tracked = $null; $matches = $null
    try {
        $blob = (& git -C $repo rev-parse "HEAD:$harnessRel" 2>$null)
        if ($LASTEXITCODE -ne 0) { $blob = $null } else { $blob = $blob.Trim() }
    } catch { $blob = $null }
    if ($blob) {
        # Does the file on disk hash to the blob git has? git hashes the file's
        # own bytes, so this is exact and needs no diff.
        try {
            $onDisk = (& git -C $repo hash-object $Run.Harness).Trim()
            $matches = ($onDisk -eq $blob)
        } catch { $matches = $null }
        try {
            $dirty = @(& git -C $repo status --porcelain --untracked-files=no -- $harnessRel)
            $tracked = ($dirty.Count -eq 0)
        } catch { $tracked = $null }
    }

    $head = $null; $repoClean = $null; $repoModified = @()
    try {
        $head = (& git -C $repo rev-parse HEAD).Trim()
        # WHAT WAS MODIFIED, not only that something was. A bare
        # repo_tracked_clean:false tells a reader the tree was dirty and refuses
        # to say how, so the only safe reading left is the worst one - that
        # product code was uncommitted when the numbers were taken. These are
        # git porcelain lines; a reader can classify them.
        $repoModified = @(& git -C $repo status --porcelain --untracked-files=no)
        $repoClean = ($repoModified.Count -eq 0)
    } catch { }

    # ASK, DO NOT ASSUME SOMEBODY ELSE ASKED.
    #
    # This used to read $Run.Health, which is populated only as a SIDE EFFECT of
    # Reset-HzDocument. Every harness that touches a document happens to fill it
    # early, so the manifest looked reliable - and a harness that touches no
    # document wrote an artifact with no candidate commit, no Revit, no server
    # SHA, and nothing anywhere saying why. An identity block that is blank
    # because nobody asked is indistinguishable from one that is blank because
    # the bridge is down.
    $health = $Run.Health
    $healthSource = 'cached from an earlier call in this run'
    if (-not $health) {
        $healthSource = 'read here, because nothing in this run had asked yet'
        try { $health = Get-HzHealth $Run } catch { $healthSource = 'unavailable: ' + $_.Exception.Message }
    }
    $year = $null; $addinCommit = $null; $addinSha = $null; $serverSha = $null
    $addinPath = $null; $addinShaSource = 'unavailable'
    if ($health) { $year = [string](Get-HzProp $health 'revit_version') }
    # THE ADD-IN HASHES ITSELF. This used to build a deployment path by hand and
    # hash that - and the path it built was wrong (the real one carries \Addins\),
    # so every campaign recorded addin_sha256: null and no result was tied to the
    # bytes that produced it. A development session moves the file again, so no
    # guessed path is right for every run. health.addin_assembly is the loaded
    # file, hashed in the process that loaded it.
    if ($health) {
        $asm = Get-HzProp $health 'addin_assembly'
        if ($asm) {
            $addinSha = [string](Get-HzProp $asm 'sha256')
            $addinPath = [string](Get-HzProp $asm 'path')
            if ($addinSha) { $addinShaSource = 'health.addin_assembly (hashed by the add-in that loaded it)' }
        }
    }
    if (-not $addinSha -and $year) {
        # An older add-in has no addin_assembly block. Fall back to the INSTALLED
        # path, and say that is what was hashed - it is only the same file when
        # the run is against the installed pair.
        $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\Horizun\Horizun.Revit.dll"
        $addinSha = Get-HzSha256 $addin
        if ($addinSha) { $addinPath = $addin; $addinShaSource = 'installed add-in path (this build publishes no addin_assembly block)' }
    }
    if ($health) { $addinCommit = [string](Get-HzProp $health 'horizun_commit') }
    $serverExe = if ($env:HORIZUN_SERVER_EXE) { $env:HORIZUN_SERVER_EXE } else { Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
    $serverSha = Get-HzSha256 $serverExe

    # The contract hash and the tool count from the SERVER that answered, not
    # from the source tree - the source tree is not what this run talked to.
    # EVERY MEASURED FACT SAYS WHERE IT CAME FROM, and an unavailable one stays
    # $null. A number copied out of a file and printed under a field name that
    # implies it was measured is worse than no number: nobody can tell.
    $contractHash = $null; $contractSource = 'unavailable'
    $identity = Get-HzResource -Run $Run -Uri 'horizun://build/identity' -Label 'build-identity'
    if ($identity) {
        $contractHash = [string](Get-HzProp $identity 'contract_hash')
        if ($contractHash) { $contractSource = 'resources/read horizun://build/identity' }
    }

    $toolCount = $null; $toolSource = 'unavailable'
    if (-not ($Run.PSObject.Properties.Name -contains 'ToolList')) {
        $listed = $null
        try { $listed = Get-HzToolList $Run } catch { $listed = $null }
        Add-Member -InputObject $Run -NotePropertyName 'ToolList' -NotePropertyValue $listed -Force
    }
    if ($Run.ToolList) { $toolCount = @($Run.ToolList).Count; $toolSource = 'tools/list' }

    $counts = Get-HzCounts $Run
    [ordered]@{
        schema = 'horizun.live-evidence/2'
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        run_id = $Run.RunId
        started_utc = $Run.StartedUtc

        harness_file = $harnessRel
        harness_git_blob = $blob
        harness_sha256 = (Get-HzSha256 $Run.Harness)
        harness_path_matches_repository = ($null -ne $blob)
        harness_tracked_clean = $tracked
        harness_working_copy_matches_commit = $matches

        code_candidate_commit = $addinCommit
        identity_source = $healthSource
        repo_head = $head
        repo_tracked_clean = $repoClean
        repo_modified_paths = @($repoModified)
        code_candidate_means = 'the commit the RUNNING ADD-IN reports, read from the add-in - not git HEAD, which moves for documentation'

        revit_year = $(if ($health) { Get-HzProp $health 'revit_version' } else { $null })
        revit_build = $(if ($health) { Get-HzProp $health 'revit_build' } else { $null })
        revit_pid = $(if ($health) { Get-HzProp $health 'process_id' } else { $null })
        horizun_version = $(if ($health) { Get-HzProp $health 'horizun_version' } else { $null })
        built_from_clean_tree = $(if ($health) { Get-HzProp $health 'built_from_clean_tree' } else { $null })
        addin_sha256 = $addinSha
        addin_path = $addinPath
        addin_sha256_source = $addinShaSource
        server_sha256 = $serverSha
        contract_hash = $contractHash
        contract_hash_source = $contractSource
        tool_count = $toolCount
        tool_count_source = $toolSource

        document = $(if ($health) { [string](Get-HzPath $health 'active_document','title') } else { $null })
        document_fingerprint = $Run.Fixture['document_fingerprint']
        open_document_count = $(if ($health) { Get-HzProp $health 'open_document_count' } else { $null })

        fixture = (Protect-HzValue $Run.Fixture)
        expected_facts = (Protect-HzValue $Run.Expected)

        calls_made = $Run.Calls
        passed = $counts.passed
        failed = $counts.failed
        unverified = $counts.unverified
        not_covered = $counts.not_covered
        fixture_missing = $counts.fixture_missing
        not_assessable = $counts.not_assessable
        not_applicable = $counts.not_applicable
        available = $counts.available
        implemented_not_live_verified = $counts.implemented_not_live_verified
        probes = @($Run.Probes)
        notes = @($Run.Notes | ForEach-Object { Protect-HzText $_ })
        counting_rule = 'Every published status bucket adds to probes. Only passed is evidence of a working capability; all other states remain named so availability, inapplicability or missing evidence cannot be read as a pass.'
    }
}

function Get-HzCounts {
    param([Parameter(Mandatory)]$Run)
    $byStatus = @{
        passed = 0; failed = 0; unverified = 0; not_covered = 0; fixture_missing = 0
        not_assessable = 0; not_applicable = 0; available = 0
        implemented_not_live_verified = 0
    }
    foreach ($p in $Run.Probes) { $byStatus[$p.status] = $byStatus[$p.status] + 1 }
    [pscustomobject]$byStatus
}

<#
  Write the artifact and print the summary. Returns the exit code the harness
  should use: non-zero when anything failed, so a run that fails cannot be
  mistaken for one that passed by a script reading only the exit status.
#>
function Complete-HzRun {
    param(
        [Parameter(Mandatory)]$Run,
        [string]$ArtifactDir
    )
    if (-not $ArtifactDir) { $ArtifactDir = Join-Path $Run.RepoRoot 'artifacts\live' }
    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $manifest = Get-HzManifest $Run
    $path = Join-Path $ArtifactDir ('{0}.json' -f $Run.RunId)
    ($manifest | ConvertTo-Json -Depth 40) | Set-Content -LiteralPath $path -Encoding utf8

    $c = Get-HzCounts $Run
    $bad = $c.failed
    Write-Host ''
    Write-Host ('  {0} passed, {1} failed, {2} unverified, {3} not covered, {4} fixture missing, {5} not assessable, {6} not applicable, {7} available, {8} implemented not live verified' -f
        $c.passed, $c.failed, $c.unverified, $c.not_covered, $c.fixture_missing,
        $c.not_assessable, $c.not_applicable, $c.available, $c.implemented_not_live_verified) `
        -ForegroundColor $(if ($bad) { 'Red' } else { 'Green' })
    Write-Host ("  artifact: {0}" -f $path) -ForegroundColor DarkGray
    Write-Host ("  harness:  {0}  committed={1}  matches_commit={2}" -f
        $manifest.harness_file, $manifest.harness_path_matches_repository, $manifest.harness_working_copy_matches_commit) -ForegroundColor DarkGray
    Write-Host ("  candidate {0}  server {1}" -f
        (Limit-HzText ([string]$manifest.code_candidate_commit) 12),
        (Limit-HzText ([string]$manifest.server_sha256) 12)) -ForegroundColor DarkGray
    [pscustomobject]@{ Manifest = $manifest; Path = $path; ExitCode = $(if ($bad) { 1 } else { 0 }) }
}

function Add-HzNote {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Text)
    $null = $Run.Notes.Add($Text)
    Write-Host ("  ({0})" -f $Text) -ForegroundColor DarkGray
}
