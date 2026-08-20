#Requires -Version 5.1

function Test-HorizunTomlTableHeader([string]$Line) {
    $trim = $Line.Trim()
    return ($trim -match '^\[\[[^\]]+\]\]$' -or $trim -match '^\[[^\]]+\]$')
}

function Test-HorizunTomlNestedTargetHeader([string]$Line, [string]$Name) {
    $escaped = [regex]::Escape($Name)
    $trim = $Line.Trim()
    return ($trim -match "^\[mcp_servers\.$escaped\." -or
            $trim -match "^\[\[mcp_servers\.$escaped\.")
}

function Get-HorizunTomlTableRange([string[]]$Lines, [string]$Header, [string]$Name) {
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -eq $Header) { $start = $i; break }
    }
    if ($start -lt 0) { return $null }

    $end = $Lines.Count
    for ($i = $start + 1; $i -lt $Lines.Count; $i++) {
        if ((Test-HorizunTomlTableHeader $Lines[$i]) -and
            -not (Test-HorizunTomlNestedTargetHeader $Lines[$i] $Name)) {
            $end = $i
            break
        }
    }
    [pscustomobject]@{ Start = $start; EndExclusive = $end }
}
