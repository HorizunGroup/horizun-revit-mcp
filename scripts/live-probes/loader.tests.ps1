#Requires -Version 5.1
# Exercises the <probe-modules-section> of verify-live.ps1 WITHOUT Revit: the text
# between its markers runs against fake Add-Write / Invoke-Write and three modules.
$ErrorActionPreference = 'Stop'
$verifyLive = Join-Path (Split-Path -Parent $PSScriptRoot) 'verify-live.ps1'
$text = Get-Content -LiteralPath $verifyLive -Raw
$start = $text.IndexOf('# <probe-modules-section>'); $end = $text.IndexOf('# </probe-modules-section>')
if ($start -lt 0 -or $end -lt 0) { throw 'probe-modules-section markers not found' }
$section = $text.Substring($start, $end - $start)

$dir = Join-Path ([IO.Path]::GetTempPath()) ('hz-probe-loader-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'a-good.probes.ps1') -Encoding UTF8 -Value @'
$script:HzProbeModules += [pscustomobject]@{ Name = 'good'
    Catalog = @(@{ Name = 'good: one'; Tool = 't1' }, @{ Name = 'good: two'; Tool = 't1' })
    Run = { param($Ctx) $r = & $Ctx.Call 't1' @{ x = 1 }; @(@{ Name = 'good: one'; Tool = 't1'; Outcome = $(if ($r.ok) { 'pass' } else { 'fail' }); Detail = "year $($Ctx.Year)" }) } }
'@
Set-Content -LiteralPath (Join-Path $dir 'b-throws.probes.ps1') -Encoding UTF8 -Value @'
$script:HzProbeModules += [pscustomobject]@{ Name = 'throws'
    Catalog = @(@{ Name = 'throws: a'; Tool = 't2' })
    Run = { param($Ctx) throw 'boom' } }
'@
Set-Content -LiteralPath (Join-Path $dir 'c-broken.probes.ps1') -Encoding UTF8 -Value 'this is ( not powershell'

$script:recorded = @()
function Add-Write($name, $tool, $outcome, $detail) { $script:recorded += [pscustomobject]@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = $detail } }
function Invoke-Write($tool, $arguments) { return @{ ok = $true } }
function Invoke-WriteApply($tool, $arguments, $key) { return @{ stage = 'apply' } }
$Year = 2026; $WriteDocument = 'HZ_WRITE'; $scratchDir = $dir; $probeRun = 'r1'; $writeGate = $true
$env:HORIZUN_PROBE_MODULE_DIR = $dir
try { . ([scriptblock]::Create($section)) } finally { $env:HORIZUN_PROBE_MODULE_DIR = $null }

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
$by = @{}; foreach ($r in $script:recorded) { $by[$r.Name] = $r }
Check 'a reported case is recorded with its outcome and sees the context' (($by['good: one'].Outcome -eq 'pass') -and ($by['good: one'].Detail -eq 'year 2026'))
Check 'a catalogued case the module did not report is unverified, never passed' ($by['good: two'].Outcome -eq 'unverified')
Check 'a module that throws records every catalogued case unverified' (($by['throws: a'].Outcome -eq 'unverified') -and ($by['throws: a'].Detail -match 'boom'))
Check 'a module that does not load is recorded, not skipped' (@($script:recorded | Where-Object { $_.Name -eq 'probe module c-broken.probes.ps1 loads' -and $_.Outcome -eq 'unverified' }).Count -eq 1)
Get-ChildItem -LiteralPath $dir -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
Remove-Item -LiteralPath $dir
if ($fails) { "loader tests: $fails FAILED"; exit 1 } else { 'loader tests: ALL PASS'; exit 0 }
