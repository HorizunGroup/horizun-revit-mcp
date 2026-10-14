#Requires -Version 5.1
function Find-HzHostSystemType {
    param([Parameter(Mandatory=$true)][scriptblock]$FetchPage)
    $cursor = $null
    $seen = @{}
    for ($page = 0; $page -lt 100; $page++) {
        $arguments = @{ categories = @('OST_Walls','OST_Floors','OST_Roofs','OST_Ceilings')
                        include_links = $false; include_types = $true; max_rows = 500 }
        if ($cursor) { $arguments.cursor = $cursor }
        $data = & $FetchPage $arguments
        if (-not $data) { throw 'The host system type query returned no data.' }
        $row = @($data.rows | Where-Object { $_.is_element_type -eq $true -and $_.source_kind -eq 'host' } | Select-Object -First 1)
        if ($row.Count -eq 1) { return $row[0] }
        $next = [string]$data.next_cursor
        if (-not $next) { return $null }
        if ($seen.ContainsKey($next)) { throw 'The host system type query repeated a cursor.' }
        $seen[$next] = $true
        $cursor = $next
    }
    throw 'The host system type query exceeded 100 pages without completing.'
}
