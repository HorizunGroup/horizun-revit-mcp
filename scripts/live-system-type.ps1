#Requires -Version 5.1
function Find-HzHostSystemType {
    param([Parameter(Mandatory=$true)][scriptblock]$FetchPage)
    # Read one host instance, then resolve its own type by exact ElementId. This
    # avoids a large, unstable cursor over a model with thousands of instances.
    $instances = & $FetchPage @{ categories = @('OST_Walls','OST_Floors','OST_Roofs','OST_Ceilings')
                                include_links = $false; include_types = $false; max_rows = 50 }
    if (-not $instances) { throw 'The host system instance query returned no data.' }
    $instance = @($instances.rows | Where-Object { $_.source_kind -eq 'host' -and [long]$_.type_id -gt 0 } | Select-Object -First 1)
    if ($instance.Count -ne 1) { return $null }
    $typeId = [long]$instance[0].type_id
    $types = & $FetchPage @{ element_ids = @($typeId); include_links = $false; include_types = $true; max_rows = 5 }
    if (-not $types) { throw 'The exact host system type query returned no data.' }
    $type = @($types.rows | Where-Object { [long]$_.element_id -eq $typeId -and $_.is_element_type -eq $true -and $_.source_kind -eq 'host' } | Select-Object -First 1)
    if ($type.Count -eq 1) { return $type[0] }
    return $null
}
