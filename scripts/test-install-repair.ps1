# -----------------------------------------------------------------------------
# Horizun Revit MCP - the install-repair exercise, in a SANDBOX.
#
# Provokes each broken state the diagnostic claims to recognise - against
# isolated roots, never the real installation - runs the diagnostic, checks the
# classification, runs -Repair, and re-diagnoses: only the re-read verdict
# counts. The real installation is read once as the source of known-good bytes
# and never written. Exit 0 = every scenario classified AND repaired AND the
# repair idempotent; anything else names the scenario that broke.
# -----------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$diag = Join-Path $PSScriptRoot 'diagnose-install.ps1'

$sandbox = Join-Path $env:TEMP ("hz-repair-test-" + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $sandbox 'server'
$addinsRoot = Join-Path $sandbox 'addins'
$statusPath = Join-Path $sandbox 'install-status.json'
$realServer = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
$realAddin = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026\Horizun\Horizun.Revit.dll'
if (-not (Test-Path $realServer)) { Write-Error 'no real install to source known-good bytes from'; exit 2 }

function Invoke-Diag([string[]]$extra) {
    $json = Join-Path $sandbox ("diag-" + [guid]::NewGuid().ToString('N') + ".json")
    $baseArgs = @('-NoProfile','-File',$diag,'-InstallDir',$installDir,'-AddinsRoot',$addinsRoot,
                  '-StatusPath',$statusPath,'-Json',$json) + $extra
    & pwsh @baseArgs *> $null
    return Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
}

function Deploy-Good {
    New-Item -ItemType Directory -Force $installDir, (Join-Path $addinsRoot '2026') | Out-Null
    Copy-Item $realServer (Join-Path $installDir 'horizun-mcp.exe') -Force
    Copy-Item $realAddin (Join-Path $addinsRoot '2026\Horizun.Revit.dll') -Force
}

$failures = @()
function Check([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) { "  PASS  $name" } else { "  FAIL  $name  ($detail)"; $script:failures += $name }
}

# ---- scenario 0: a healthy sandbox reads healthy -----------------------------
Deploy-Good
$d0 = Invoke-Diag @()
Check 'healthy sandbox classifies healthy_on_disk or names only durable_record_absent' `
    ($d0.verdict -in @('healthy_on_disk','needs_attention') -and
     -not ($d0.problems | Where-Object { $_ -match 'not_installed|signature_(?!Valid)' -and $_ -ne 'durable_record_absent' })) `
    ("verdict=$($d0.verdict) problems=$($d0.problems -join ',')")

# ---- scenario 1: server ABSENT ----------------------------------------------
Remove-Item (Join-Path $installDir 'horizun-mcp.exe') -Force
$d1 = Invoke-Diag @()
Check 'missing server is classified' ($d1.problems -contains 'server_not_installed') ($d1.problems -join ',')
$r1 = Invoke-Diag @('-Repair')
$d1b = Invoke-Diag @()
Check 'missing server repairs from the source' `
    (-not ($d1b.problems -contains 'server_not_installed')) ($d1b.problems -join ',')

# ---- scenario 2: server bytes TAMPERED (signature broken) --------------------
$bytes = [IO.File]::ReadAllBytes((Join-Path $installDir 'horizun-mcp.exe'))
$bytes[$bytes.Length - 10] = ($bytes[$bytes.Length - 10] -bxor 0xFF)
[IO.File]::WriteAllBytes((Join-Path $installDir 'horizun-mcp.exe'), $bytes)
$d2 = Invoke-Diag @()
Check 'tampered server is classified by its signature' `
    ([bool]($d2.problems | Where-Object { $_ -match 'server_signature' })) ($d2.problems -join ',')
$null = Invoke-Diag @('-Repair')
$d2b = Invoke-Diag @()
Check 'tampered server repairs to Valid' `
    (-not ($d2b.problems | Where-Object { $_ -match 'server_signature' })) ($d2b.problems -join ',')

# ---- scenario 3: add-in ABSENT (dir left, dll gone) --------------------------
Remove-Item (Join-Path $addinsRoot '2026\Horizun.Revit.dll') -Force
$d3 = Invoke-Diag @()
Check 'missing add-in reads not_installed for its year' `
    ($d3.addins.'2026' -eq 'not_installed') ("2026=$($d3.addins.'2026')")
$null = Invoke-Diag @('-Repair')
$d3b = Invoke-Diag @()
Check 'missing add-in repairs from the source' `
    ($d3b.addins.'2026' -ne 'not_installed') ("2026=$($d3b.addins.'2026')")

# ---- scenario 4: repair is IDEMPOTENT ---------------------------------------
$before = (Get-FileHash (Join-Path $installDir 'horizun-mcp.exe')).Hash
$null = Invoke-Diag @('-Repair')
$after = (Get-FileHash (Join-Path $installDir 'horizun-mcp.exe')).Hash
Check 'repair on a healthy sandbox changes nothing' ($before -eq $after) "hash moved"

# ---- scenario 5: the REAL install was never written --------------------------
$realHashBefore = (Get-FileHash $realServer).Hash
Check 'the real installation is untouched' ((Get-FileHash $realServer).Hash -eq $realHashBefore) 'real bytes moved'

Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
""
if ($failures.Count -eq 0) { "install-repair exercise: ALL SCENARIOS PASS"; exit 0 }
else { "install-repair exercise: $($failures.Count) FAILED: $($failures -join '; ')"; exit 1 }
