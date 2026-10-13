#Requires -Version 7.0
<#
  PENDING LIVE. Owner-run read-only probe of the worktree server's facade.
  It neither starts/closes Revit nor changes a model or client configuration.
  Run later with a document deliberately selected by the owner:
    pwsh -NoProfile -File scripts/live/schema-facade.ps1 -ServerExe <exe> -ExpectedDocument <title>
  A write rehearsal/apply belongs to the later owner-selected live fixture; this
  probe deliberately exercises only health, schema lookup and typed reads.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerExe,
    [Parameter(Mandatory)][string]$ExpectedDocument,
    [string]$OutJson = (Join-Path ([IO.Path]::GetTempPath()) 'horizun-schema-facade-live.json')
)
$ErrorActionPreference = 'Stop'
$psi = [Diagnostics.ProcessStartInfo]::new((Resolve-Path $ServerExe).Path)
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$psi.Environment['HORIZUN_SCHEMA_MODE'] = 'facade'
$proc = [Diagnostics.Process]::Start($psi)
$stderr = $proc.StandardError.ReadToEndAsync()
$script:probeId = 0
$evidence = [ordered]@{ state='pending'; expected_document=$ExpectedDocument; calls=@() }
function Invoke-Probe([string]$Method, [hashtable]$Arguments) {
    $script:probeId++
    $id = $script:probeId
    $request = @{ jsonrpc='2.0'; id=$id; method=$Method; params=$Arguments }
    $proc.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 30 -Compress))
    while ($true) {
        $line = $proc.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(30000)) { throw "No response within 30 seconds: $Method" }
        if ($null -eq $line.Result) { throw 'Server closed stdout' }
        $reply = $line.Result | ConvertFrom-Json -AsHashtable
        if ($reply.id -ne $id) { continue }
        $evidence.calls += $reply
        if ($reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
        return $reply.result
    }
}
try {
    $null = Invoke-Probe 'initialize' @{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{name='schema-facade-live';version='1'} }
    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $health = Invoke-Probe 'tools/call' @{name='horizun_health';arguments=@{}}
    if ($health.isError -or $health.structuredContent.status -ne 'healthy') { throw 'Health did not report healthy' }
    if ($health.structuredContent.schema_mode.mode -ne 'facade') { throw 'Health did not identify the facade' }
    $identity = Invoke-Probe 'resources/read' @{uri='horizun://build/identity'}
    $identityData = $identity.contents[0].text | ConvertFrom-Json -AsHashtable
    if ($identityData.contract_hash -ne $health.structuredContent.schema_mode.contract_hash -or
        $identityData.facade_hash -ne $health.structuredContent.schema_mode.facade_hash) { throw 'Health and identity hashes disagree' }
    $direct = Invoke-Probe 'tools/call' @{name='get_document_info';arguments=@{}}
    if ($direct.isError -or $direct.structuredContent.title -ne $ExpectedDocument) { throw 'Active document differs from the owner-selected document' }
    $through = Invoke-Probe 'tools/call' @{name='horizun_call_tool';arguments=@{name='get_document_info';arguments=@{}}}
    if ($through.isError -or $through.structuredContent.title -ne $ExpectedDocument -or
        $through.structuredContent.path -ne $direct.structuredContent.path) { throw 'Facade read did not reach the same document' }
    $described = Invoke-Probe 'tools/call' @{name='horizun_describe_tool';arguments=@{name='get_document_info'}}
    if ($described.isError -or $described.structuredContent.contract_uri -ne 'horizun://contract/tools/get_document_info') { throw 'Exact-schema lookup failed' }
    $bad = Invoke-Probe 'tools/call' @{name='horizun_call_tool';arguments=@{name='get_document_info';arguments=@{bogus_argument=1}}}
    $badText = ($bad.content | Where-Object type -EQ 'text').text -join "`n"
    if (-not $bad.isError -or $badText -notmatch '/bogus_argument' -or
        $badText -notmatch 'horizun://contract/tools/get_document_info') { throw 'Contract refusal lost its path or exact-schema URI' }
    $evidence.state = 'passed'
} catch {
    $evidence.state = 'failed'
    $evidence.error = $_.Exception.Message
    throw
} finally {
    [IO.File]::WriteAllText($OutJson, ($evidence | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
    $proc.Dispose()
}
Write-Host "Read-only facade live probe passed: $OutJson"
