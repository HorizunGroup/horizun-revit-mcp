#Requires -Version 5.1
function Find-HzHostSystemType {
    param([Parameter(Mandatory=$true)][scriptblock]$FetchPage)
    # A broad query_model scans the entire fixture before paging and can exceed
    # its ten-minute RPC wait. List one wall, then resolve its instance and type
    # through exact IDs. Every resulting type is still checked as a host type.
    $listed = & $FetchPage 'horizun_list_elements' @{ category = 'OST_Walls'; include_links = $false; max_rows = 10 }
    if (-not $listed) { throw 'The host wall list returned no data.' }
    $wall = @($listed.rows | Where-Object { $_.source_kind -eq 'host' } | Select-Object -First 1)
    if ($wall.Count -ne 1) { return $null }
    $instanceId = [long]$wall[0].element_id
    $instances = & $FetchPage 'horizun_query_model' @{ element_ids = @($instanceId); include_links = $false; max_rows = 5 }
    if (-not $instances) { throw 'The exact host instance query returned no data.' }
    $instance = @($instances.rows | Where-Object { [long]$_.element_id -eq $instanceId -and $_.source_kind -eq 'host' -and [long]$_.type_id -gt 0 } | Select-Object -First 1)
    if ($instance.Count -ne 1) { return $null }
    $typeId = [long]$instance[0].type_id
    $types = & $FetchPage 'horizun_query_model' @{ element_ids = @($typeId); include_links = $false; include_types = $true; max_rows = 5 }
    if (-not $types) { throw 'The exact host system type query returned no data.' }
    $type = @($types.rows | Where-Object { [long]$_.element_id -eq $typeId -and $_.is_element_type -eq $true -and $_.source_kind -eq 'host' } | Select-Object -First 1)
    if ($type.Count -eq 1) { return $type[0] }
    return $null
}
