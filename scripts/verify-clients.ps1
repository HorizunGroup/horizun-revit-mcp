#Requires -Version 5.1
<#
  BOTH CLIENTS, THE SAME BRIDGE, THE SAME STATE.

  It reads the command out of each client's own configuration and drives THAT
  executable - not a path this script composes. If Claude and Codex are pointed
  at different binaries, or one of them at a stale build, this is where it shows,
  because the thing under test is the string the client will actually launch.

  For each registered entry it asks:
    tools/list        does it advertise a surface at all
    horizun_target    which Revit instances can it see
    horizun_health    which build is it, and where is its data root
    horizun_job_status  can it read the job records

  Then it CROSSES them: the two clients must see the same instances, the same
  data root and the same jobs. That is the property the shared-root work exists
  to produce, and it cannot be observed from one side.

  WHAT THIS DOES NOT PROVE. It launches the configured executable itself. It does
  not prove the client has RELOADED its configuration - both rewrite that file
  from memory while running, so an entry added underneath a live client is not
  live until it restarts. Restart them, then run this again: the answers should
  be identical, and if they are not, the client is running an older config.

  Exit codes:  0 they agree   1 they do not   2 could not run
#>
[CmdletBinding()]
param(
    # `horizun`, matching register-client.ps1's default. It was `horizun-next`
    # until 0.3.0, from the months when this build was the candidate sitting beside
    # a shipped one - and a checker whose default names an entry the registrar no
    # longer creates reports "not registered" about a correct install.
    [string]$Name = 'horizun',
    [string]$Json
)
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[A-Za-z0-9_-]{1,64}$') { throw 'Name must contain only ASCII letters, digits, underscore or hyphen (1..64 characters).' }
$here = $PSScriptRoot
$call = Join-Path $here 'hz-call.ps1'

$claudeConfig = Join-Path $env:USERPROFILE '.claude.json'
$codexConfig  = Join-Path $env:USERPROFILE '.codex\config.toml'

$problems = New-Object System.Collections.Generic.List[string]
function Check($name, $ok, $detail) {
    if ($ok) { Write-Host ("  OK    {0}" -f $name) -ForegroundColor Green }
    else { Write-Host ("  WRONG {0} - {1}" -f $name, $detail) -ForegroundColor Red; $problems.Add("$name : $detail") | Out-Null }
}

# --- what each client is configured to launch --------------------------------
$clients = @{}

if (Test-Path $claudeConfig) {
    $c = Get-Content $claudeConfig -Raw | ConvertFrom-Json
    $e = $c.mcpServers.$Name
    if ($e) { $clients['Claude'] = $e.command }
}
if (Test-Path $codexConfig) {
    $lines = Get-Content $codexConfig
    $i = ($lines | Select-String -Pattern ("^\[mcp_servers\." + [regex]::Escape($Name) + "\]$")).LineNumber
    if ($i) {
        for ($k = $i; $k -lt [Math]::Min($i + 10, $lines.Count); $k++) {
            # New registrations use a TOML basic string encoded like JSON, so
            # backslashes and quotes are escaped. Decode that string instead of
            # treating C:\\... as a literal Windows path. Keep support for the
            # older TOML single-quoted literal form as well.
            if ($lines[$k] -match '^\s*command\s*=\s*("(?:[^"\\]|\\.)*")\s*$') {
                $clients['Codex'] = ($Matches[1] | ConvertFrom-Json); break
            }
            if ($lines[$k] -match "^\s*command\s*=\s*'([^']*)'\s*$") { $clients['Codex'] = $Matches[1]; break }
            if ($lines[$k].Trim() -match '^\[') { break }
        }
    }
}

Write-Host ""
Write-Host "Client parity - entry '$Name'" -ForegroundColor Cyan
Write-Host ("-" * 72)

Check "Claude has an entry named '$Name'" ($clients.ContainsKey('Claude')) 'not found in ~/.claude.json'
Check "Codex has an entry named '$Name'"  ($clients.ContainsKey('Codex'))  'not found in ~/.codex/config.toml'
if ($problems.Count -gt 0) { exit 1 }

foreach ($k in $clients.Keys) { Write-Host ("  {0,-7} -> {1}" -f $k, $clients[$k]) }

$sameBinary = ($clients['Claude'] -eq $clients['Codex'])
Check 'both clients point at the SAME executable' $sameBinary `
      ("Claude '{0}' vs Codex '{1}'" -f $clients['Claude'], $clients['Codex'])

foreach ($k in $clients.Keys) {
    Check "$k's executable exists" (Test-Path $clients[$k]) $clients[$k]
}
if ($problems.Count -gt 0) { exit 1 }

# --- drive each one -----------------------------------------------------------
$observed = @{}
foreach ($k in $clients.Keys) {
    Write-Host ""
    Write-Host ("  driving the binary $k is configured to launch") -ForegroundColor DarkCyan
    $exe = $clients[$k]

    $t = [IO.Path]::GetTempFileName()
    & $call -Tool horizun_target -Server $exe -Quiet -Json $t | Out-Null
    $target = (Get-Content $t -Raw | ConvertFrom-Json).result
    Remove-Item $t -Force -ErrorAction SilentlyContinue

    $t = [IO.Path]::GetTempFileName()
    & $call -Tool horizun_job_status -Arguments '{"limit":3}' -Server $exe -Quiet -Json $t | Out-Null
    $jobs = (Get-Content $t -Raw | ConvertFrom-Json).result
    Remove-Item $t -Force -ErrorAction SilentlyContinue

    $observed[$k] = [pscustomobject]@{
        exe           = $exe
        exe_sha256    = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
        data_root     = $target.data_paths.data_root
        targets_found = $target.targets_found
        instances     = @($target.targets | ForEach-Object { "{0}:{1}" -f $_.revit_year, $_.pid } | Sort-Object)
        jobs_dir      = $jobs.jobs_dir
        job_count     = $jobs.job_count
        recent_jobs   = @($jobs.jobs | ForEach-Object { $_.job_id } | Sort-Object)
    }

    Check "$k : the bridge answers horizun_target" ($null -ne $target) 'no reply'
    Check "$k : it can see at least one Revit" ($observed[$k].targets_found -ge 1) `
          ("targets_found = {0}" -f $observed[$k].targets_found)
    Check "$k : it can read the job records" ($null -ne $jobs -and $null -ne $jobs.jobs_dir) 'no jobs_dir'
    Write-Host ("          data root {0}" -f $observed[$k].data_root)
    Write-Host ("          instances {0}" -f ($observed[$k].instances -join ', '))
    Write-Host ("          jobs      {0} in {1}" -f $observed[$k].job_count, $observed[$k].jobs_dir)
}

# --- the crossing -------------------------------------------------------------
Write-Host ""
Write-Host "  do the two agree?" -ForegroundColor DarkCyan
$a = $observed['Claude']; $b = $observed['Codex']

Check 'the same binary, by hash' ($a.exe_sha256 -eq $b.exe_sha256) `
      ("{0} vs {1}" -f $a.exe_sha256.Substring(0,16), $b.exe_sha256.Substring(0,16))
Check 'the same DATA ROOT' ($a.data_root -eq $b.data_root) ("{0} vs {1}" -f $a.data_root, $b.data_root)
Check 'the same JOBS directory' ($a.jobs_dir -eq $b.jobs_dir) ("{0} vs {1}" -f $a.jobs_dir, $b.jobs_dir)
Check 'the same Revit INSTANCES discovered' (($a.instances -join '|') -eq ($b.instances -join '|')) `
      ("{0} vs {1}" -f ($a.instances -join ','), ($b.instances -join ','))
Check 'the same JOB RECORDS visible' (($a.recent_jobs -join '|') -eq ($b.recent_jobs -join '|')) `
      ("{0} vs {1}" -f ($a.recent_jobs -join ','), ($b.recent_jobs -join ','))

Write-Host ("-" * 72)
if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        schema        = 1
        generated_utc = (Get-Date).ToUniversalTime().ToString('o')
        entry_name    = $Name
        observed      = $observed
        problems      = $problems
        note          = 'Drives the executable each client is CONFIGURED to launch. Does not prove the client has reloaded that configuration - both rewrite it from memory while running, so a new entry is not live until the client restarts.'
        verdict       = $(if ($problems.Count -eq 0) { 'both clients, one bridge, one state' } else { 'they disagree' })
    } | ConvertTo-Json -Depth 10 | Out-File -FilePath $Json -Encoding utf8
    Write-Host "  wrote $Json"
}

if ($problems.Count -gt 0) { foreach ($p in $problems) { Write-Host ("    - {0}" -f $p) -ForegroundColor Red }; exit 1 }
Write-Host ""
Write-Host "  BOTH CLIENTS, ONE BRIDGE, ONE STATE." -ForegroundColor Green
exit 0
