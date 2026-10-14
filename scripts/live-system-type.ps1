#Requires -Version 5.1
function Find-HzHostSystemType {
    param([Parameter(Mandatory=$true)][scriptblock]$FetchPage)
    # Restrict each query to one system category. Broad model queries scan the
    # entire fixture before paging and may exceed the RPC deadline; listing
    # instances misses models that contain types but no placed instances.
    foreach ($category in @('OST_Walls', 'OST_Floors', 'OST_Roofs', 'OST_Ceilings')) {
        $types = & $FetchPage 'horizun_query_model' @{ categories = @($category); include_links = $false; include_types = $true; max_rows = 50 }
        if (-not $types) { throw "The host system type query for $category returned no data." }
        $type = @($types.rows | Where-Object { $_.is_element_type -eq $true -and $_.source_kind -eq 'host' -and [long]$_.element_id -gt 0 } | Select-Object -First 1)
        if ($type.Count -eq 1) { return $type[0] }
    }
    return $null
}
