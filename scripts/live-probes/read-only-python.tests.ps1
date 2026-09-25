#Requires -Version 5.1
# Exercises read-only-python.probes.ps1 WITHOUT Revit: a fake Call that plays the
# three shapes ExecutePythonCommand can return for read_only requests (a REAL
# preflight-refusal for Save, a REAL Save() refusal, and a wall committed then
# proven gone), plus the disabled-machine and closed-write-tier paths.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'read-only-python.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'read-only-python' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-Fake([bool]$Disabled) {
    $reply = { param($data, $isError, $text) [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        if ($Disabled) {
            return & $reply $null $true 'horizun_execute_python is DISABLED ON THIS MACHINE. This is the safe default...'
        }
        if ($a.preflight -eq $true) {
            return & $reply ([pscustomobject]@{
                executed = $false; would_run = $false
                checks   = [pscustomobject]@{ read_only_scan = "WOULD BE REFUSED: Document.SaveAs()" }
            }) $false ''
        }
        if ($a.code -eq 'doc.Save()') {
            return & $reply $null $true 'read_only=true REFUSES to run this script: it mentions Document.Save(). Nothing ran.'
        }
        if ($a.code -match 'wid = int') {
            return & $reply ([pscustomobject]@{ output = [pscustomobject]@{ status = 'self_reported_verified'; still_present = $false } }) $false ''
        }
        # the wall-creating script
        return & $reply ([pscustomobject]@{
            output             = [pscustomobject]@{ status = 'self_reported_verified'; wall_id = 999; walls_before = 3 }
            read_only          = $true
            read_only_check    = [pscustomobject]@{ ok = $true; rolled_back = $true }
            transaction_left_open = $false
        }) $false ''
    }.GetNewClosure()
    return $call
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-readonly-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-1'; WriteGate = $false; Call = (New-Fake $false); Apply = { throw 'not used' } }
    $cases = @(& $module.Run $ctx)
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0)
    Check 'the wall case passes against a well-behaved fake' ($by['read_only: a wall committed inside read_only is rolled back and does not persist'].Outcome -eq 'pass')
    Check 'the Save refusal case passes' ($by['read_only: a Save() call is refused before the script runs'].Outcome -eq 'pass')
    Check 'the preflight scan case passes' ($by['read_only: preflight reports the scan without executing'].Outcome -eq 'pass')
    if ($fails) { $cases | ForEach-Object { "    $($_.Name): $($_.Outcome) $($_.Detail)" } }

    $ctxDisabled = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-2'; WriteGate = $false; Call = (New-Fake $true); Apply = { throw 'not used' } }
    $byD = @{}; foreach ($c in @(& $module.Run $ctxDisabled)) { $byD[$c.Name] = $c }
    Check 'a disabled machine reports not_covered for all three, never fail' (
        @($byD.Values | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0)

    $ctxGated = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-3'; WriteGate = $true; Call = (New-Fake $false); Apply = { throw 'not used' } }
    $byG = @{}; foreach ($c in @(& $module.Run $ctxGated)) { $byG[$c.Name] = $c }
    Check 'a closed write tier still runs the preflight case (no document mutation) and skips the rest' (
        $byG['read_only: preflight reports the scan without executing'].Outcome -eq 'pass' -and
        $byG['read_only: a wall committed inside read_only is rolled back and does not persist'].Outcome -eq 'not_covered' -and
        $byG['read_only: a Save() call is refused before the script runs'].Outcome -eq 'not_covered')
}
finally {
    Get-ChildItem -LiteralPath $scratch -File -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Remove-Item -LiteralPath $scratch -ErrorAction SilentlyContinue
}
if ($fails) { "read-only-python probe tests: $fails FAILED"; exit 1 } else { 'read-only-python probe tests: ALL PASS'; exit 0 }
