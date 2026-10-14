#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'live-system-type.ps1')
$calls = New-Object System.Collections.Generic.List[string]
$fake = {
    param($arguments)
    $calls.Add([string]$arguments.cursor)
    if (-not $arguments.cursor) {
        return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 10; is_element_type = $false; source_kind = 'host' }); next_cursor = 'page-2' }
    }
    return [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 22; is_element_type = $true; source_kind = 'host' }); next_cursor = $null }
}.GetNewClosure()
$row = Find-HzHostSystemType -FetchPage $fake
if (-not $row -or $row.element_id -ne 22 -or $calls.Count -ne 2) { throw 'A host system type on the next page was missed.' }
$empty = { param($arguments) [pscustomobject]@{ rows = @(); next_cursor = $null } }
if ($null -ne (Find-HzHostSystemType -FetchPage $empty)) { throw 'An absent host type was invented.' }
Write-Host 'live-system-type tests passed'
