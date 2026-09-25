#Requires -Version 5.1
# Exercises copy-between-documents-source-path.probes.ps1 WITHOUT Revit. The real
# open+copy+close case is exercised by faking Apply to return the expected shape
# when a fixture library path is present on disk; when it is not, not_covered is
# expected (this is the normal state until -Fixtures LibraryDocument is set).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'copy-between-documents-source-path.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'copy-between-documents-source-path' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [string]$scratchRoot) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string] }
    $call = {
        param($tool, $arguments)
        if ($tool -ne 'horizun_copy_between_documents') { return @{ isError = $true; text = 'unexpected tool ' + $tool } }
        if ($arguments.source_document -and $arguments.source_path) { return @{ isError = $true; text = 'give exactly one of source_document (...) or source_path (...), not both, not neither.' } }
        if ($arguments.source_path -and -not (Test-Path -LiteralPath $arguments.source_path)) { return @{ isError = $true; text = 'File not found: ' + $arguments.source_path } }
        return @{ isError = $true; text = 'unexpected call in this fake' }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{}; text = 'ok' } } }
        $row = [pscustomobject]@{ element_id = 5001; present_after_commit = $true }
        $sourceOpen = [pscustomobject]@{ opened_in_background = $true; will_be_closed_without_saving = $true; detached = $true }
        $data = [pscustomobject]@{ host_verified = $true; rows = @($row); source_open = $sourceOpen; types_that_arrived = @() }
        return @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' } }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratchRoot; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('HzCbdProbeTest_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

# A. write tier closed
$by = Run-Module (New-Ctx $true $tmp)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

# B. no fixtures file at all - HOME points at the scratch dir, so
# .horizun\live-fixtures.json under it does not exist.
$origHome = $env:USERPROFILE
try {
    $env:USERPROFILE = $tmp
    $by = Run-Module (New-Ctx $false $tmp)
    Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
    Check 'mutual exclusivity is refused' ($by[$catalog[0]].Outcome -eq 'pass')
    Check 'a missing source_path is refused before opening' ($by[$catalog[1]].Outcome -eq 'pass')
    Check 'without the fixture the real case is not_covered' ($by[$catalog[2]].Outcome -eq 'not_covered' -and $by[$catalog[2]].Detail -match 'LibraryDocument')

    # C. with a fixtures file naming a library and a type name
    $horizunDir = Join-Path $tmp '.horizun'
    New-Item -ItemType Directory -Path $horizunDir -Force | Out-Null
    $libPath = Join-Path $tmp 'HZ_PROBE_LIBRARY.rvt'
    Set-Content -LiteralPath $libPath -Value 'not a real rvt, just needs to exist for Test-Path'
    $fixtures = [pscustomobject]@{ LibraryDocument = $libPath; LibraryDocumentTypeName = 'Basic Wall - 200mm' }
    $fixtures | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $horizunDir 'live-fixtures.json')
    $ctx = New-Ctx $false $tmp
    $by = Run-Module $ctx
    Check 'with the fixture the real case passes' ($by[$catalog[2]].Outcome -eq 'pass')
    Check 'the copied element is cleaned up' ($ctx.State.applies.Contains('cbd-cleanup'))
}
finally {
    $env:USERPROFILE = $origHome
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fails) { "copy-between-documents-source-path probe tests: $fails FAILED"; exit 1 } else { 'copy-between-documents-source-path probe tests: ALL PASS'; exit 0 }
