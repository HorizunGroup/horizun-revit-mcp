# Offline checks for scripts/release-gate-exemptions.ps1: a named exemption covers its own
# case with its own reason, and nothing else a report could claim.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-gate-exemptions.ps1')
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
$ex = @(Get-HzReleaseGateExemptions)
Check 'the file names exactly five exemptions' ($ex.Count -eq 5)
$case = [string]$ex[0].case
$dopt = [string]$ex[1].case
$row = { param($n, $o, $d) [pscustomobject]@{ name = $n; outcome = $o; detail = $d } }
Check 'the case with its reason is exempt' (Test-HzExemptNotCovered (& $row $case 'not_covered' 'fixture PointCloudFloor ({min_xy}) is missing') $ex 2024)
Check 'the case with another reason is not exempt' (-not (Test-HzExemptNotCovered (& $row $case 'not_covered' 'no point cloud instance to scan') $ex 2024))
Check 'another case is not exempt' (-not (Test-HzExemptNotCovered (& $row 'links-survey: add kind=point_cloud creates a type' 'not_covered' 'fixture PointCloudFloor x') $ex 2024))
Check 'a failure of the case is never exempt' (-not (Test-HzExemptNotCovered (& $row $case 'fail' 'fixture PointCloudFloor x') $ex 2024))
$dr = & $row $dopt 'not_covered' 'rac_advanced_sample_project (the only sample of this year) carries no design options, so list has none'
Check 'design options are exempt in 2023' (Test-HzExemptNotCovered $dr $ex 2023)
Check 'design options are NOT exempt in 2024' (-not (Test-HzExemptNotCovered $dr $ex 2024))
Check 'no year means no exemption' (-not (Test-HzExemptNotCovered $dr $ex $null))
$accNames = @(
    'cde_cloud issues: issues_list reads the issues with their subtypes and names what it could not read',
    'cde_cloud issues: an issue_create dry run rehearses with a token or refuses with the reason, and creates nothing',
    'cde_cloud issues: issue_create applies, reads the issue back and a keyed retry does not duplicate it'
)
foreach ($i in 2..4) {
    $acc = $ex[$i]
    Check "ACC case $i has its exact probe name" ($acc.case -ceq $accNames[$i - 2])
    Check "ACC case $i has its exact missing-project reason" ($acc.reason_prefix -ceq 'no ACC test project named: set HORIZUN_PROBE_ACC_PROJECT_ID to a test project GUID')
    Check "ACC case $i has the owner approval" ($acc.approved -eq '2026-10-13, project owner')
    foreach ($year in 2023..2027) {
        Check "ACC case $i is exempt only for missing test project in $year" (Test-HzExemptNotCovered (& $row $acc.case 'not_covered' 'no ACC test project named: set HORIZUN_PROBE_ACC_PROJECT_ID to a test project GUID') $ex $year)
    }
    Check "ACC case $i failure is not exempt" (-not (Test-HzExemptNotCovered (& $row $acc.case 'fail' 'ACC request failed') $ex 2027))
    Check "ACC case $i other reason is not exempt" (-not (Test-HzExemptNotCovered (& $row $acc.case 'not_covered' 'ACC token expired') $ex 2027))
}
$report = [pscustomobject]@{ revit_year = 2024; probes = @((& $row $case 'not_covered' 'fixture PointCloudFloor x'), (& $row 'other' 'not_covered' 'no fixture'), (& $row 'ok' 'pass' '')) }
Check 'only the other not_covered row remains a gate gap' (@(Get-HzGateNotCovered $report).Count -eq 1 -and @(Get-HzGateNotCovered $report)[0].name -eq 'other')
if ($fails) { "release-gate-exemptions tests: $fails FAILED"; exit 1 } else { 'release-gate-exemptions tests: ALL PASS'; exit 0 }
