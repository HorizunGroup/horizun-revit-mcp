#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-system-type.ps1')
$calls = New-Object System.Collections.Generic.List[string]
$fake = {
    param($tool, $arguments)
    $calls.Add($tool)
    if ($tool -ne 'horizun_query_model' -or @($arguments.categories) -notcontains 'OST_Walls' -or
        $arguments.include_links -ne $false -or $arguments.include_types -ne $true -or $arguments.max_rows -gt 50) {
        throw 'A bounded host Wall type query was required.'
    }
    return [pscustomobject]@{ rows = @(
        [pscustomobject]@{ element_id = 10; is_element_type = $false; source_kind = 'host' },
        [pscustomobject]@{ element_id = 11; is_element_type = $true; source_kind = 'link' },
        [pscustomobject]@{ element_id = 22; is_element_type = $true; source_kind = 'host' }) }
}.GetNewClosure()
$row = Find-HzHostSystemType -FetchPage $fake
if (-not $row -or $row.element_id -ne 22 -or ($calls -join ',') -ne 'horizun_query_model') { throw 'The bounded query did not select a real host system type.' }
$empty = { param($tool, $arguments) [pscustomobject]@{ rows = @() } }
if ($null -ne (Find-HzHostSystemType -FetchPage $empty)) { throw 'An absent host type was invented.' }
Write-Host 'live-system-type tests passed'
