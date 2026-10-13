#Requires -Version 5.1

# A TOML table header is matched by its KEYS, never by its text. TOML lets the
# same table be written [mcp_servers.horizun-revit], [mcp_servers."horizun-revit"],
# [ mcp_servers . 'horizun-revit' ] or with a trailing # comment. Matching the
# exact text missed every spelling but one: registration then appended a second
# table of the same name, and Codex refuses a file that defines a table twice -
# every server in it stops loading. The same exact-text test made a commented
# header AFTER ours look like a key line, so a replace deleted that whole table
# (backlog #25).

function ConvertFrom-HorizunTomlHeaderLine([string]$Line) {
    <# $null when the line is not a table header; else its keys (unquoted) and array flag. #>
    $t = $Line.Trim()
    if (-not $t.StartsWith('[')) { return $null }
    $m = [regex]::Match($t, '^(\[\[?)\s*(.+?)\s*(\]\]?)\s*(#.*)?$')
    if (-not $m.Success) { return $null }
    if ($m.Groups[1].Value.Length -ne $m.Groups[3].Value.Length) { return $null }
    $body = $m.Groups[2].Value
    $keys = New-Object System.Collections.Generic.List[string]
    $pos = 0
    $tok = [regex]'\G\s*(?:"((?:[^"\\]|\\.)*)"|''([^'']*)''|([A-Za-z0-9_-]+))\s*(\.|$)'
    $closed = $false
    while ($pos -lt $body.Length) {
        $k = $tok.Match($body, $pos)
        if (-not $k.Success) { return $null }
        if ($k.Groups[1].Success) { $keys.Add(($k.Groups[1].Value -replace '\\(.)', '$1')) }
        elseif ($k.Groups[2].Success) { $keys.Add($k.Groups[2].Value) }
        else { $keys.Add($k.Groups[3].Value) }
        $pos = $k.Index + $k.Length
        if ($k.Groups[4].Value -eq '') { $closed = $true; break }
    }
    if (-not $closed -or $keys.Count -eq 0) { return $null }
    [pscustomobject]@{ Keys = $keys.ToArray(); IsArray = ($m.Groups[1].Value.Length -eq 2) }
}

function Test-HorizunTomlTableHeader([string]$Line) {
    return ($null -ne (ConvertFrom-HorizunTomlHeaderLine $Line))
}

function Test-HorizunTomlKeyPrefix([string[]]$Keys, [string[]]$Prefix) {
    if ($Keys.Count -lt $Prefix.Count) { return $false }
    for ($i = 0; $i -lt $Prefix.Count; $i++) {
        if (-not [string]::Equals($Keys[$i], $Prefix[$i], [StringComparison]::Ordinal)) { return $false }
    }
    return $true
}

function Get-HorizunTomlTableRange([string[]]$Lines, [string]$Header, [string]$Name) {
    <#
      The lines a table owns: its header, its keys and its nested tables
      ([mcp_servers.<name>.env] and the like), up to the next table that is not
      nested under it. $Header names the table in any valid spelling; $Name is
      kept for the callers and is the last key of $Header. A header-looking line
      inside a multi-line string is not a header.
    #>
    $target = ConvertFrom-HorizunTomlHeaderLine $Header
    if (-not $target -or $target.IsArray) { throw "not a table header: $Header" }
    $want = [string[]]$target.Keys

    $start = -1
    $end = $Lines.Count
    $inMulti = $null
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($inMulti) {
            if (([regex]::Matches($line, [regex]::Escape($inMulti))).Count % 2 -eq 1) { $inMulti = $null }
            continue
        }
        $h = ConvertFrom-HorizunTomlHeaderLine $line
        if ($h) {
            $keys = [string[]]$h.Keys
            if ($start -lt 0) {
                if (-not $h.IsArray -and $keys.Count -eq $want.Count -and (Test-HorizunTomlKeyPrefix $keys $want)) { $start = $i }
            }
            elseif (-not ($keys.Count -gt $want.Count -and (Test-HorizunTomlKeyPrefix $keys $want))) {
                $end = $i
                break
            }
            continue
        }
        foreach ($q in @('"""', "'''")) {
            if (([regex]::Matches($line, [regex]::Escape($q))).Count % 2 -eq 1) { $inMulti = $q; break }
        }
    }
    if ($start -lt 0) { return $null }
    [pscustomobject]@{ Start = $start; EndExclusive = $end }
}
