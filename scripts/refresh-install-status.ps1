#Requires -Version 5.1
<#
  Keep %LOCALAPPDATA%\Horizun\install-status.json TRUTHFUL across dev deploys.

  WHY THIS EXISTS. The durable install record is written by the installer's
  completion flow and then never again. A dev deploy (scripts/deploy-both.ps1)
  swaps every binary underneath that record, and the file keeps claiming the
  OLD health - measured on this machine on 2026-08-26: the record said
  live_verified 0.9.6/32baa87 while the running bridge answered 1.0.0/f39c84a.
  A durable state that outlives the bytes it describes is not state, it is a
  stale claim with a timestamp.

  TWO MODES, both honest about what they know:

    -DeployedCommit <sha>   The deploy path just verified new binaries on disk
                            but nobody has asked the live bridge anything yet.
                            The record is DOWNGRADED to deployed_pending_health,
                            names the new commit, and DROPS the old health block
                            - that block described bytes that are no longer
                            installed.

    (no arguments)          Ask the installed server for horizun_health NOW,
                            and check the answer against the bytes on disk
                            before believing it.

                            live_verified is a claim about THIS installation,
                            so "healthy" alone does not earn it. Five things
                            have to agree, and each is recorded with the answer:

                              the server that replied is the one INSTALLED at
                              server_path - compared by SHA-256, because a
                              different horizun-mcp.exe left running from an
                              earlier build answers just as cheerfully;

                              the commit it reports is the commit stamped into
                              that binary - an old add-in paired with a new
                              server reports healthy and is not this build;

                              the contract hash is readable from
                              horizun://build/identity - the two halves publish
                              it and a mismatch means they are not a pair;

                              Revit itself is named - version, build and process
                              - because health without a Revit is health about
                              nothing;

                              and a document is open, because every command in
                              this bridge acts on the active one.

                            Anything missing or disagreeing writes
                            deployed_pending_health with the reason. A health
                            block is never invented, and never kept when it
                            describes bytes that are no longer installed.

  The installer's own completion flow remains the authority during an install:
  it writes generation-guarded records. This refresher touches only the
  top-level status file, atomically, and preserves the installer's identity
  fields (client, generation, verification_path) so the two writers describe
  one lineage.
#>
[CmdletBinding()]
param(
    [string]$DeployedCommit,
    [int]$TimeoutSec = 45,
    # Overridable for tests only.
    [string]$StatusPath = (Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json'),
    [string]$ServerPath = (Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe')
)

$ErrorActionPreference = 'Stop'

$previous = $null
if (Test-Path -LiteralPath $StatusPath) {
    try { $previous = Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json } catch { $previous = $null }
}

function Write-Status($Doc) {
    $dir = Split-Path -Parent $script:StatusPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$script:StatusPath.tmp-$([guid]::NewGuid().ToString('N'))"
    [pscustomobject]$Doc | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $script:StatusPath -Force
}

function New-StatusDoc([string]$State, [string]$Detail) {
    $doc = [ordered]@{
        schema      = 1
        updated_utc = (Get-Date).ToUniversalTime().ToString('o')
        state       = $State
        detail      = $Detail
        client      = if ($previous -and $previous.client) { $previous.client } else { 'unknown' }
        server_path = $script:ServerPath
    }
    if ($previous -and $previous.verification_path) { $doc['verification_path'] = $previous.verification_path }
    if ($previous -and $previous.generation) { $doc['generation'] = $previous.generation }
    $doc['refreshed_by'] = 'scripts/refresh-install-status.ps1'
    return $doc
}

if ($DeployedCommit) {
    $short = if ($DeployedCommit.Length -gt 12) { $DeployedCommit.Substring(0, 12) } else { $DeployedCommit }
    $doc = New-StatusDoc 'deployed_pending_health' `
        ("A dev deploy verified new binaries on disk (commit $short). The previous health block described " +
         "bytes that are no longer installed and was dropped; live verification is the remaining check - " +
         "run this script with no arguments once Revit is up, or let the next deploy cycle do it.")
    $doc['installed'] = [ordered]@{ commit = $DeployedCommit; verified_on_disk = $true }
    Write-Status $doc
    Write-Host "[Horizun] install-status: deployed_pending_health at commit $short (old health block dropped)."
    exit 0
}

# ---- live mode: ask the bridge, then check the answer against the disk -------
$hzCall = Join-Path $PSScriptRoot 'hz-call.ps1'
if (-not (Test-Path -LiteralPath $hzCall)) { throw "hz-call.ps1 not found beside this script." }

function Read-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    $p.Value
}

function Invoke-Bridge([hashtable]$Splat) {
    $reply = Join-Path ([IO.Path]::GetTempPath()) ("hz-status-" + [guid]::NewGuid().ToString('N') + ".json")
    try {
        & pwsh -NoProfile -File $hzCall @Splat -Json $reply -Quiet -TimeoutSec $TimeoutSec *> $null
        if (-not (Test-Path -LiteralPath $reply)) { return $null }
        return Get-Content -LiteralPath $reply -Raw | ConvertFrom-Json
    }
    finally { if (Test-Path -LiteralPath $reply) { Remove-Item -LiteralPath $reply -Force -ErrorAction SilentlyContinue } }
}

# WHAT IS ACTUALLY ON DISK. Everything below is compared against these, so a
# reply from some other build cannot be recorded as this installation's health.
$installedSha = $null
$installedCommit = $null
$installedVersion = $null
if (Test-Path -LiteralPath $ServerPath) {
    $installedSha = (Get-FileHash -LiteralPath $ServerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    try {
        $stamp = (Get-Item -LiteralPath $ServerPath).VersionInfo.ProductVersion
        if ($stamp -match '^(?<ver>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)\+(?<sha>[0-9a-f]{40})') {
            $installedVersion = $Matches['ver']
            $installedCommit = $Matches['sha']
        }
    }
    catch { }
}

$envelope = Invoke-Bridge @{ Tool = 'horizun_health' }
$health = $null
if ($envelope -and -not $envelope.is_error -and $envelope.result -and
    [string]$envelope.result.status -eq 'healthy') { $health = $envelope.result }

$identity = $null
if ($health) {
    $idEnvelope = Invoke-Bridge @{ Tool = 'horizun_health'; Resource = 'horizun://build/identity' }
    if ($idEnvelope -and -not $idEnvelope.is_error) { $identity = $idEnvelope.result }
}

# THE FIVE THINGS, each named so a failure says which one.
$problems = New-Object System.Collections.Generic.List[string]
if (-not $health) {
    $problems.Add('horizun_health did not answer healthy (Revit closed, the bridge warming up, or a modal dialog waiting for somebody)')
}
else {
    $spokeTo = [string](Read-Field $envelope 'server_sha256')
    if (-not $installedSha) {
        $problems.Add("no server is installed at $ServerPath to compare the reply against")
    }
    elseif ($spokeTo -and $spokeTo.ToLowerInvariant() -ne $installedSha) {
        $problems.Add("the server that answered (SHA $($spokeTo.Substring(0,12))) is not the one installed at server_path (SHA $($installedSha.Substring(0,12))); an older horizun-mcp.exe left running answers just as cheerfully")
    }

    $liveCommit = [string](Read-Field $health 'horizun_commit')
    if (-not $liveCommit) { $problems.Add('the bridge named no commit') }
    elseif ($installedCommit -and $liveCommit -ne $installedCommit) {
        $problems.Add("the bridge reports commit $($liveCommit.Substring(0,12)) and the installed binary is stamped $($installedCommit.Substring(0,12))")
    }

    $contract = [string](Read-Field $identity 'contract_hash')
    if (-not $contract) {
        $problems.Add('horizun://build/identity published no contract_hash, so the add-in and the server cannot be shown to be a pair')
    }

    # process_id is the Revit process the add-in lives inside; the field is
    # named for what it IS rather than repeating "revit" in a reply that is
    # already about Revit.
    $revitVersion = [string](Read-Field $health 'revit_version')
    $revitPid = Read-Field $health 'process_id'
    if (-not $revitVersion -or -not $revitPid) {
        $problems.Add('the reply named no Revit version or process; health without a Revit is health about nothing')
    }

    $openDocuments = Read-Field $health 'open_document_count'
    if ($null -eq $openDocuments -or [int]$openDocuments -lt 1) {
        $problems.Add('no document is open, and every command in this bridge acts on the active one')
    }
}

if ($problems.Count -eq 0) {
    $doc = New-StatusDoc 'live_verified' `
        ('horizun_health answered healthy through the SERVER INSTALLED at server_path, reporting the commit ' +
         'stamped into that binary, with a contract hash, a named Revit and an open document. Each is recorded ' +
         'below and each was checked.')
    $doc['health'] = $health
    $doc['verified'] = [ordered]@{
        installed_server_sha256   = $installedSha
        answering_server_sha256   = [string](Read-Field $envelope 'server_sha256')
        installed_binary_commit   = $installedCommit
        installed_binary_version  = $installedVersion
        live_commit               = [string](Read-Field $health 'horizun_commit')
        live_version              = [string](Read-Field $health 'horizun_version')
        contract_hash             = [string](Read-Field $identity 'contract_hash')
        built_from_clean_tree     = Read-Field $health 'built_from_clean_tree'
        revit_version             = [string](Read-Field $health 'revit_version')
        revit_build               = [string](Read-Field $health 'revit_build')
        revit_name                = [string](Read-Field $health 'revit_name')
        revit_process_id          = Read-Field $health 'process_id'
        open_document_count       = Read-Field $health 'open_document_count'
        active_document           = [string](Read-Field (Read-Field $health 'active_document') 'title')
        tools_visible             = Read-Field (Read-Field $health 'tool_packs') 'tools_visible'
        tools_total               = Read-Field (Read-Field $health 'tool_packs') 'tools_total'
        checked_utc               = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-Status $doc
    Write-Host ("[Horizun] install-status: live_verified - version {0}, commit {1}, Revit {2}, {3} document(s) open." -f `
        (Read-Field $health 'horizun_version'), ([string](Read-Field $health 'horizun_commit')).Substring(0, 7),
        (Read-Field $health 'revit_version'), (Read-Field $health 'open_document_count'))
    exit 0
}

$doc = New-StatusDoc 'deployed_pending_health' `
    ('live verification did not hold: ' + ($problems -join '; ') + '. No health block was invented and none was kept.')
$doc['unverified_because'] = @($problems)
if ($installedSha) {
    $doc['installed'] = [ordered]@{
        commit = $installedCommit; server_sha256 = $installedSha; verified_on_disk = $true
    }
}
Write-Status $doc
Write-Host ("[Horizun] install-status: deployed_pending_health - " + ($problems -join '; '))
exit 1
