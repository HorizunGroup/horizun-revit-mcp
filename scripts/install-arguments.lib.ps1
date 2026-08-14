#Requires -Version 5.1

function ConvertTo-HorizunRevitYears {
    [CmdletBinding()]
    param([object[]]$InputYears)

    $result = New-Object System.Collections.Generic.List[int]
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($raw in @($InputYears)) {
        foreach ($part in ("$raw" -split ',')) {
            $text = $part.Trim()
            if ($text -eq '') { continue }
            $year = 0
            if (-not [int]::TryParse($text, [ref]$year) -or $year -lt 2023 -or $year -gt 2027) {
                throw "Unsupported Revit year '$text'. Choose one or more of 2023, 2024, 2025, 2026, 2027."
            }
            if ($seen.Add($year)) { $result.Add($year) }
        }
    }
    $result.ToArray()
}
