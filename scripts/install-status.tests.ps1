#Requires -Version 5.1
<#
  live_verified IS A CLAIM, AND THIS IS WHAT MAKES IT ONE.

  install-status.json is the durable record of whether THIS machine's
  installation actually works. The state that says so - live_verified - used to
  need only a "healthy" from whatever server answered. That is not enough, and
  the ways it can be wrong are not hypothetical on a development machine:

    an older horizun-mcp.exe left running from a previous build answers
    horizun_health perfectly and reports its OWN commit;

    an add-in from one build paired with a server from another reports healthy
    right up until the first command fails on a contract mismatch;

    and health with no Revit behind it, or with no document open, is health
    about nothing - every command in this bridge acts on the active document.

  So the refresher checks five things and records each one. These tests drive
  it against a FAKE bridge, because the point is what it refuses, and a machine
  with a working install cannot demonstrate a refusal.
#>
$ErrorActionPreference = 'Stop'

$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $name" -ForegroundColor Red
        if ($detail) { Write-Host "        $detail" }
        $script:failed++
    }
}

$refresher = Join-Path $PSScriptRoot 'refresh-install-status.ps1'
if (-not (Test-Path -LiteralPath $refresher)) { throw "refresh-install-status.ps1 not found beside this test" }

$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-install-status-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

try {
    # ---------------------------------------------------------------- the source
    $source = Get-Content -LiteralPath $refresher -Raw

    Assert 'the reply is compared against the SHA of the server on disk' `
        ($source -match 'installedSha' -and $source -match "Read-Field \`$envelope 'server_sha256'") $null

    Assert 'the live commit is compared against the commit stamped in the binary' `
        ($source -match 'installedCommit' -and $source -match "Read-Field \`$health 'horizun_commit'") $null

    Assert 'the contract hash is read from the identity resource, not assumed' `
        ($source -match "horizun://build/identity" -and $source -match 'contract_hash') $null

    Assert 'a Revit must be named, and a document must be open' `
        ($source -match "'revit_version'" -and $source -match "'open_document_count'") $null

    Assert 'nothing but the all-clear path writes live_verified' `
        ((([regex]::Matches($source, "'live_verified'")).Count) -eq 1) `
        'live_verified must be written in exactly one place - the branch where every check held'

    Assert 'every refusal names its reason in the record' `
        ($source -match 'unverified_because') $null

    # ------------------------------------------------- driven against a fake bridge
    #
    # hz-call.ps1 is replaced by a stub that writes whatever reply the case
    # under test needs. The refresher takes its own paths as parameters, so
    # nothing here touches the real installation.
    $fakeScripts = Join-Path $root 'scripts'
    New-Item -ItemType Directory -Path $fakeScripts | Out-Null
    Copy-Item -LiteralPath $refresher -Destination (Join-Path $fakeScripts 'refresh-install-status.ps1')

    $serverPath = Join-Path $root 'horizun-mcp.exe'
    Set-Content -LiteralPath $serverPath -Value 'not a real binary' -Encoding ascii -NoNewline
    $serverSha = (Get-FileHash -LiteralPath $serverPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $statusPath = Join-Path $root 'install-status.json'

    # The stub answers from a JSON file the test writes first. Two calls arrive:
    # horizun_health, then the identity resource - told apart by -Resource.
    $stub = @'
param([string]$Tool, [string]$Resource, [string]$Arguments, [string]$ArgumentsPath, [string]$Server,
      [string]$Json, [int]$TimeoutSec, [switch]$Quiet)
$dir = Split-Path -Parent $PSCommandPath
$which = if ($Resource) { 'identity' } else { 'health' }
$src = Join-Path $dir "reply.$which.json"
if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $Json -Force }
'@
    Set-Content -LiteralPath (Join-Path $fakeScripts 'hz-call.ps1') -Value $stub -Encoding UTF8

    function Set-Reply([string]$Which, $Body) {
        ($Body | ConvertTo-Json -Depth 20) |
            Set-Content -LiteralPath (Join-Path $fakeScripts "reply.$Which.json") -Encoding UTF8
    }

    function Invoke-Refresh {
        if (Test-Path -LiteralPath $statusPath) { Remove-Item -LiteralPath $statusPath -Force }
        & pwsh -NoProfile -File (Join-Path $fakeScripts 'refresh-install-status.ps1') `
            -StatusPath $statusPath -ServerPath $serverPath -TimeoutSec 5 *> $null
        if (-not (Test-Path -LiteralPath $statusPath)) { return $null }
        Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    }

    # A binary with no version stamp cannot name a commit, so the commit check
    # cannot fire here; every other check can, and does.
    function New-Health([hashtable]$Override) {
        $health = @{
            status = 'healthy'; horizun_version = '1.1.0-dev'
            horizun_commit = '0123456789abcdef0123456789abcdef01234567'
            built_from_clean_tree = $true
            revit_version = '2026'; revit_build = '26.4.0.32'; revit_name = 'Autodesk Revit 2026'
            process_id = 4242; open_document_count = 2
            active_document = @{ title = 'HZ_WRITE' }
            tool_packs = @{ tools_visible = 74; tools_total = 74 }
        }
        foreach ($k in $Override.Keys) { $health[$k] = $Override[$k] }
        @{ is_error = $false; server_sha256 = $serverSha; result = $health }
    }
    $identityOk = @{ is_error = $false; result = @{ contract_hash = '56adffa29ad1b9f34b091cf7' } }

    Set-Reply 'health' (New-Health @{})
    Set-Reply 'identity' $identityOk
    $ok = Invoke-Refresh
    Assert 'a complete, agreeing answer reaches live_verified' `
        ($ok -and $ok.state -eq 'live_verified') $ok.state
    Assert 'and the record carries what it checked, not just the verdict' `
        ($ok.verified -and $ok.verified.contract_hash -eq '56adffa29ad1b9f34b091cf7' -and
         $ok.verified.revit_process_id -eq 4242 -and $ok.verified.active_document -eq 'HZ_WRITE') `
        ($ok.verified | ConvertTo-Json -Compress)

    # ---- the five refusals -------------------------------------------------
    Set-Reply 'health' @{ is_error = $true; result = $null }
    Set-Reply 'identity' $identityOk
    $r = Invoke-Refresh
    Assert 'no answer at all is refused, and no health block is invented' `
        ($r.state -eq 'deployed_pending_health' -and $null -eq $r.health) $r.state

    $other = New-Health @{}
    $other.server_sha256 = 'f' * 64
    Set-Reply 'health' $other
    $r = Invoke-Refresh
    Assert 'a reply from a DIFFERENT server than the installed one is refused' `
        ($r.state -eq 'deployed_pending_health' -and
         ($r.unverified_because -join ' ') -match 'not the one installed') `
        ($r.unverified_because -join ' ')

    Set-Reply 'health' (New-Health @{ horizun_commit = '' })
    $r = Invoke-Refresh
    Assert 'a bridge that names no commit is refused' `
        ($r.state -eq 'deployed_pending_health' -and ($r.unverified_because -join ' ') -match 'no commit') `
        ($r.unverified_because -join ' ')

    Set-Reply 'health' (New-Health @{})
    Set-Reply 'identity' @{ is_error = $false; result = @{ } }
    $r = Invoke-Refresh
    Assert 'no contract hash means the two halves cannot be shown to be a pair, and it is refused' `
        ($r.state -eq 'deployed_pending_health' -and ($r.unverified_because -join ' ') -match 'contract_hash') `
        ($r.unverified_because -join ' ')

    Set-Reply 'identity' $identityOk
    Set-Reply 'health' (New-Health @{ revit_version = ''; process_id = $null })
    $r = Invoke-Refresh
    Assert 'health with no Revit behind it is refused' `
        ($r.state -eq 'deployed_pending_health' -and ($r.unverified_because -join ' ') -match 'no Revit') `
        ($r.unverified_because -join ' ')

    Set-Reply 'health' (New-Health @{ open_document_count = 0 })
    $r = Invoke-Refresh
    Assert 'health with no document open is refused - every command acts on the active one' `
        ($r.state -eq 'deployed_pending_health' -and ($r.unverified_because -join ' ') -match 'no document') `
        ($r.unverified_because -join ' ')

    # ---- the deploy path still downgrades ----------------------------------
    & pwsh -NoProfile -File (Join-Path $fakeScripts 'refresh-install-status.ps1') `
        -StatusPath $statusPath -ServerPath $serverPath `
        -DeployedCommit '0123456789abcdef0123456789abcdef01234567' *> $null
    $r = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    Assert 'a deploy downgrades to deployed_pending_health and drops the old health block' `
        ($r.state -eq 'deployed_pending_health' -and $null -eq $r.health) $r.state
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'install-status: live_verified is a claim this script can defend.' -ForegroundColor Green
exit 0
