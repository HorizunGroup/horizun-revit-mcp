#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-system-type.ps1')
$calls = New-Object System.Collections.Generic.List[string]
$fake = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_list_elements') {
        $calls.Add('list')
        return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 10; source_kind = 'host' }) }
    }
    if ($arguments.element_ids[0] -eq 10) {
        $calls.Add('instance')
        return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 10; type_id = 22; is_element_type = $false; source_kind = 'host' }) }
    }
    $calls.Add('type')
    if ($arguments.element_ids[0] -ne 22 -or $arguments.include_types -ne $true) { throw 'The exact type was not queried.' }
    return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 22; is_element_type = $true; source_kind = 'host' }); next_cursor = $null }
}.GetNewClosure()
$row = Find-HzHostSystemType -FetchPage $fake
if (-not $row -or $row.element_id -ne 22 -or ($calls -join ',') -ne 'list,instance,type') { throw 'A listed host instance did not lead to its exact system type.' }
$empty = { param($tool, $arguments) [pscustomobject]@{ rows = @() } }
if ($null -ne (Find-HzHostSystemType -FetchPage $empty)) { throw 'An absent host type was invented.' }
Write-Host 'live-system-type tests passed'
