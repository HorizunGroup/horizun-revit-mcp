#Requires -Version 5.1
# Exercises code-checks-4d-federation.probes.ps1 WITHOUT Revit: fake Call/Apply stand in
# for the bridge, so the module's own logic (staging, verdicts, cleanup) is what is tested.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'code-checks-4d-federation.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'code-checks-4d-federation' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-cc4d-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

$script:calls = @(); $script:deleted = @(); $script:bad = @{}
function Ok($data) { [pscustomobject]@{ isError = $false; data = [pscustomobject]$data; text = 'ok' } }
function Err($text) { [pscustomobject]@{ isError = $true; data = $null; text = $text } }
function Rule($id, $verdict, $examined) { [pscustomobject]@{ id = $id; verdict = $verdict; examined = $examined } }

$fakeCall = {
    param($tool, $a)
    $script:calls += $tool
    switch ($tool) {
        'horizun_code_check' {
            if (-not (Test-Path -LiteralPath $a.requirement_set_path)) { return Err 'no file' }
            $nsr = $a.requirement_set_path -match 'nsr10'
            $rules = @((Rule 'r1' 'not_decidable' 0), (Rule 'r2' $(if ($nsr) { 'not_decidable' } else { 'fails' }) 3))
            if ($script:bad.emptyPass -and -not $nsr) { $rules += (Rule 'r3' 'passes' 0) }
            return Ok @{ verdict = 'fails'; rules = $rules
                         totals = [pscustomobject]@{ passes = $(if ($nsr) { 0 } else { 1 }); fails = $(if ($nsr) { 0 } else { 2 }); not_decidable = 4 } }
        }
        'horizun_federation_check' {
            if ($a.rules.ContainsKey('modles')) { return Err "The federation rules were refused: rules: unknown key 'modles'." }
            return Ok @{ verdict = 'fails'; models = @([pscustomobject]@{ host = $true; state = 'clean' })
                         expected_links = @([pscustomobject]@{ state = 'missing' })
                         site = @([pscustomobject]@{ state = 'coherent' }); links = @([pscustomobject]@{ instance_id = 5 })
                         summary = [pscustomobject]@{ links_missing = 1 } }
        }
        'horizun_link_schedule' {
            if ($a.operation -eq 'import') {
                $n = @(Get-Content -LiteralPath $a.schedule_path).Count - 1
                return Ok @{ schedule = [pscustomobject]@{ activities = $n; rejected_rows = 0 } }
            }
            if ($a.operation -eq 'match') {
                $links = @([pscustomobject]@{ element_id = 11 }, [pscustomobject]@{ element_id = 12 })
                if ($script:bad.foreignLink) { $links[1] = [pscustomobject]@{ element_id = 777 } }
                return Ok @{ links = $links; activities_without_elements = 0 }
            }
        }
        'horizun_list_elements' { return Ok @{ rows = @([pscustomobject]@{ element_id = 30 }) } }
        'horizun_query_model' { return Ok @{ rows = @([pscustomobject]@{ element_id = 40; is_element_type = $true }) } }
    }
    return Err "unexpected call $tool"
}
$fakeApply = {
    param($tool, $a, $key)
    $script:calls += "apply:$tool"
    switch ($tool) {
        'horizun_create_elements' { return @{ stage = 'apply'; answer = (Ok @{ rows = @([pscustomobject]@{ element_id = 11 }, [pscustomobject]@{ element_id = 12 }) }) } }
        'horizun_write_params_verified' { return @{ stage = 'apply'; answer = (Ok @{ writes_confirmed = 2 }) } }
        'horizun_manage_views' { return @{ stage = 'apply'; answer = (Ok @{ aliases = [pscustomobject]@{ hz4d = 50 } }) } }
        'horizun_delete_verified' { $script:deleted = @($a.ids); return @{ stage = 'apply'; answer = (Ok @{ }) } }
        'horizun_link_schedule' {
            if ($a.operation -eq 'write') {
                return @{ stage = 'apply'; answer = (Ok @{ rows_verified = 2; application = [pscustomobject]@{ state = 'verified_applied' } }) }
            }
            $dry = Ok @{ status_counts = [pscustomobject]@{ done = 1; future = 1; in_progress = 0; late = 0 } }
            return @{ stage = 'apply'; dry = $dry; answer = (Ok @{ overrides_verified = 2; view_id = 60 }) }
        }
    }
    return @{ stage = 'dry_run'; answer = (Err "unexpected apply $tool") }
}

function Run-Module($gate) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'abcdef123456'; WriteGate = $gate
                              Call = $fakeCall; Apply = $fakeApply }
    $by = @{}
    foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }
    return $by
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$by = Run-Module $false
Check 'every catalogued case is reported' (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0)
Check 'with a healthy bridge every case passes' (@($by.Values | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the probe deletes its two walls, its plan and the 4D view' ((@($script:deleted) -join ',') -eq '11,12,50,60')
Check 'the synthetic CSV is removed' (@(Get-ChildItem -LiteralPath $scratch -Filter '*.csv').Count -eq 0)

$script:bad = @{ emptyPass = $true }
$by = Run-Module $false
Check 'a rule that examined nothing yet passed fails the case' ($by['code-check: the NTC 6047 set runs and every rule reports a verdict'].Outcome -eq 'fail')

$script:bad = @{ foreignLink = $true }
$by = Run-Module $false
Check 'a match that links an element the probe did not create fails' ($by['link-schedule: match links exactly the two probe walls by Mark'].Outcome -eq 'fail')

$script:bad = @{}; $script:calls = @(); $script:deleted = @()
$by = Run-Module $true
Check 'a gated write tier leaves the three write cases not_covered' (@($by.Values | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 3)
Check 'a gated write tier still runs the read-only cases' ($by['link-schedule: import parses the synthetic CSV'].Outcome -eq 'pass')
Check 'a gated write tier writes nothing' (@($script:calls | Where-Object { $_ -like 'apply:*' }).Count -eq 0)

Get-ChildItem -LiteralPath $scratch -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
Remove-Item -LiteralPath $scratch
if ($fails) { "code-checks-4d-federation tests: $fails FAILED"; exit 1 } else { 'code-checks-4d-federation tests: ALL PASS'; exit 0 }
