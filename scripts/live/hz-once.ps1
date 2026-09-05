#Requires -Version 5.1
<#
  ONE CALL, ONE KEY, ONE ARGUMENTS FILE, ONE FRESH ARTIFACT.

  Three ways a live session can measure the past and not notice, all of them met
  during the topology experiment:

  1. REPLAY. Reusing an arguments file reuses its idempotency_key, and the bridge
     answers a repeated key from cache - correctly, that is what the key is for.
     What came back was a probe from an earlier configuration and an inventory
     from before the fixture existed: same shape, same fields, no error. A cached
     reply is indistinguishable from a fresh one unless you notice the content
     answers a question you asked earlier.

  2. A STALE ARTIFACT. When the call fails to launch, hz-call writes nothing and
     the reader picks up whatever JSON was already at that path. The measurement
     then belongs to a previous run and looks perfectly current.

  3. A MANGLED PATH. Windows backslashes do not survive being typed into JSON on
     a command line, so the arguments file silently is not written at all - which
     lands you back in case 2.

  So: the key is generated here, the arguments file is written here under a name
  nothing else uses, any existing artifact is DELETED before the call, and the
  reply is refused unless a new file actually appeared.

    scripts/live/hz-once.ps1 -Tool horizun_health -Out artifacts/live/h.json
    scripts/live/hz-once.ps1 -Tool horizun_execute_python -Script wallsplit-inventory.py -Out ...
    scripts/live/hz-once.ps1 -Tool horizun_split_multilayer_walls -Ids 123 -Dry -Out ...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tool,
    [Parameter(Mandatory)][string]$Out,
    [string]$Script,
    [long[]]$Ids,
    [switch]$Dry,
    [string]$Token,
    [string]$Document = 'HZ_WALLSPLIT',
    [string]$Path,
    [int]$TimeoutSec = 900
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$key = [guid]::NewGuid().ToString()

$args = [ordered]@{}
if ($Tool -ne 'horizun_health') { $args['target_document'] = $Document }
if ($Script) {
    $full = Join-Path $repo (Join-Path 'scripts/live' $Script)
    if (-not (Test-Path -LiteralPath $full)) { throw "script not found: $full" }
    $args['code_path'] = (Resolve-Path -LiteralPath $full).Path
}
if ($Path) { $args['path'] = $Path }
if ($Ids) { $args['element_ids'] = @($Ids) }
# ALWAYS EXPLICIT. This was only sent when -Dry was bound, so an apply that
# omitted the switch inherited the tool's default - which is dry_run TRUE - and
# came back a rehearsal wearing an apply's name. The default saved the model;
# the harness still has to say which one it means.
if ($Ids) { $args['dry_run'] = [bool]$Dry }
if ($Token) { $args['confirmation_token'] = $Token }
$args['idempotency_key'] = $key

# A NAME NOTHING ELSE USES. Sharing a filename across calls is how the key got
# reused in the first place.
$argDir = Join-Path ([System.IO.Path]::GetTempPath()) 'hz-once'
$null = New-Item -ItemType Directory -Force -Path $argDir
$argFile = Join-Path $argDir ("args-" + $key.Substring(0, 8) + ".json")
($args | ConvertTo-Json -Depth 20 -Compress) | Set-Content -LiteralPath $argFile -Encoding UTF8

# DELETE THE OLD ARTIFACT FIRST. If the call does not produce a new one, the
# reader must find nothing rather than find the previous run's answer.
$outFull = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repo $Out }
if (Test-Path -LiteralPath $outFull) { Remove-Item -LiteralPath $outFull -Force }

$hzCall = Join-Path $repo 'scripts/hz-call.ps1'
$null = & $hzCall -Tool $Tool -ArgumentsPath $argFile -Json $outFull -TimeoutSec $TimeoutSec 6>&1 2>&1
$code = $LASTEXITCODE

if (-not (Test-Path -LiteralPath $outFull)) {
    throw "NO ARTIFACT: $Tool wrote nothing to $Out. Nothing is being reported from an older file."
}

# The reply must not be a replay of a previous call. The bridge echoes back what
# it was asked; if the key it answers is not the key just minted, the answer
# belongs to some earlier question.
$raw = Get-Content -LiteralPath $outFull -Raw
if ($raw -match 'idempotenc\w*"\s*:\s*"([0-9a-fA-F-]{36})"') {
    $answered = $Matches[1]
    if ($answered -ne $key) {
        throw ("REPLAY: the reply carries idempotency key $answered and this call minted $key. " +
               "That answer belongs to an earlier question.")
    }
}

Write-Host ("  {0}  key={1}  exit={2}  -> {3}" -f $Tool, $key.Substring(0, 8), $code, (Split-Path -Leaf $outFull)) -ForegroundColor DarkGray
exit $code
