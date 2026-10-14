#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-system-type.ps1')
$calls = New-Object System.Collections.Generic.List[string]
$fake = {
    param($arguments)
    if (-not $arguments.element_ids) {
        $calls.Add('instance')
        return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 10; type_id = 22; is_element_type = $false; source_kind = 'host' }); next_cursor = 'page-2' }
    }
    $calls.Add('type')
    if ($arguments.element_ids[0] -ne 22 -or $arguments.include_types -ne $true) { throw 'The exact type was not queried.' }
    return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 22; is_element_type = $true; source_kind = 'host' }); next_cursor = $null }
}.GetNewClosure()
$row = Find-HzHostSystemType -FetchPage $fake
if (-not $row -or $row.element_id -ne 22 -or ($calls -join ',') -ne 'instance,type') { throw 'A host instance did not lead to its exact system type.' }
$empty = { param($arguments) [pscustomobject]@{ rows = @(); next_cursor = $null } }
if ($null -ne (Find-HzHostSystemType -FetchPage $empty)) { throw 'An absent host type was invented.' }
Write-Host 'live-system-type tests passed'
