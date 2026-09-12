#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-family.probes.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hz-family-probe-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$template = Join-Path $testRoot 'test.rft'
Set-Content -LiteralPath $template 'synthetic test template; not a Revit file'
$script:scenario = 'pass'
$failed = 0
function Assert($name,$condition) {
    if ($condition) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:failed++ }
}
$apply = {
    param($tool,$spec,$key)
    if ($tool -ne 'horizun_create_family' -or $spec.types.Count -ne 2 -or
        $spec.forms[0].end_parameter -ne 'HZ_TestDepth' -or $spec.overwrite -ne $false -or
        $spec.forms[0].profile.Count -ne 1 -or $spec.forms[0].profile[0].Count -ne 4) {
        throw 'fixture lost its type/geometry association or no-overwrite policy'
    }
    Set-Content -LiteralPath $spec.output_path 'synthetic RFA output; not live evidence'
    if ($script:scenario -eq 'rehearsal') { return @{stage='dry_run';answer=@{text='refused'}} }
    return @{ stage='apply';answer=@{ isError=$false;data=@{
        output_verified=$true; family_document_verification=@{verified=$true}
        loaded_family=@{requested_type_names_verified=$true}; forms_verified=1; types_requested=2
    } } }
}
$call = {
    param($tool,$arguments)
    if ($tool -eq 'horizun_document_session') {
        return @{isError=$false;data=@{file=@{revit_version=$(if ($script:scenario -eq 'version') {'2025'} else {'2026'})};versions_match=$true}}
    }
    if ($tool -ne 'horizun_query_model') { throw "unexpected call: $tool" }
    return @{isError=$false;data=@{
        coverage_complete=($script:scenario -ne 'coverage'); truncated=$false
        rows=@(
            @{family=$arguments.family;type='Small';is_element_type=$true;parameters=@{HZ_TestDepth=150/304.8}},
            @{family=$arguments.family;type='Large';is_element_type=$true;parameters=@{
                HZ_TestDepth=$(if ($script:scenario -eq 'value') {0} else {300/304.8})}}
        )
    }}
}
try {
    foreach ($case in @(@('pass','pass'),@('rehearsal','unverified'),@('version','fail'),@('coverage','unverified'),@('value','fail'))) {
        $script:scenario = $case[0]
        $result = Invoke-HorizunFamilyProbe -Document 'disposable' -Template $template -Year 2026 `
            -OutputDirectory $testRoot -RunId ([guid]::NewGuid().ToString('N')) -Apply $apply -Call $call
        Assert "family oracle $($case[0])" ($result.outcome -eq $case[1])
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'hz-family-probe-*') { throw 'Unsafe cleanup' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failed) { exit 1 }
