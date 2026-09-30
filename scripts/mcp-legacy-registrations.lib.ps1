#Requires -Version 5.1
<#
  Find, and remove, the registrations of THIS product that an earlier name or an
  earlier install left behind in an MCP client.

  WHY THIS EXISTS. Registering `horizun-revit` adds one entry and, until now,
  looked at nothing else. A machine that once carried the product under its old
  name (`horizun`, earlier `horizun-next`) ended with BOTH entries pointing at the
  same horizun-mcp.exe, so each session could be talking to a different instance.
  Measured on a real machine: Codex with [mcp_servers.horizun] and
  [mcp_servers.horizun-revit] side by side.

  THE RULE, deliberately narrow. An entry is this product's leftover only when it
  is NOT the current name AND one of these holds:
    * its command is the very same executable being registered (any name), or
    * its name is one of the retired names AND its command is horizun-mcp[.exe]
      (a retired name that launches something else is somebody else's server and
      is left alone, and reported).
  Nothing else is ever touched: not other servers, not other Horizun products.

  Pure functions over text or objects: nothing here reads or writes a client's
  real configuration. The callers do the IO, the backups and the verification.
#>

function Get-HorizunRetiredServerNames {
    # `horizun` until this product became `horizun-revit`; `horizun-next` before it.
    return @('horizun', 'horizun-next')
}

function Resolve-HorizunCommandPath([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return $null }
    $expanded = [Environment]::ExpandEnvironmentVariables($Command.Trim())
    try { return [IO.Path]::GetFullPath($expanded).TrimEnd('\').ToLowerInvariant() }
    catch { return $expanded.TrimEnd('\').ToLowerInvariant() }
}

function Test-HorizunOwnExecutableName([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return $false }
    $leaf = ($Command.Trim() -replace '/', '\').Split('\')[-1]
    return ($leaf -match '(?i)^horizun-mcp(\.exe)?$')
}

function Get-HorizunLegacyDecision {
    <#
      Decide about ONE registered server. Returns
        remove = $true/$false, reason = 'same-executable' | 'retired-name' | $null,
        skipped = $true when a retired name was found but launches something else.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Command,
        [Parameter(Mandatory)][string]$CurrentName,
        [string]$ServerPath,
        [string[]]$RetiredNames = (Get-HorizunRetiredServerNames)
    )
    $result = [ordered]@{ name = $Name; command = $Command; remove = $false; reason = $null; skipped = $false }
    if ($Name -ieq $CurrentName) { return [pscustomobject]$result }

    $ours = Resolve-HorizunCommandPath $ServerPath
    $theirs = Resolve-HorizunCommandPath $Command
    if ($ours -and $theirs -and $ours -eq $theirs) {
        $result.remove = $true; $result.reason = 'same-executable'
    }
    elseif ($RetiredNames -contains $Name.ToLowerInvariant()) {
        if (Test-HorizunOwnExecutableName $Command) { $result.remove = $true; $result.reason = 'retired-name' }
        else { $result.skipped = $true }
    }
    return [pscustomobject]$result
}

# --- JSON clients (Claude Code user scope, Claude Desktop) ------------------------

function Get-HorizunJsonLegacyEntries {
    <# $Servers is the parsed `mcpServers` object (or $null). Read-only. #>
    [CmdletBinding()]
    param(
        $Servers,
        [Parameter(Mandatory)][string]$CurrentName,
        [string]$ServerPath,
        [string[]]$RetiredNames = (Get-HorizunRetiredServerNames)
    )
    $out = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Servers) { return @() }
    foreach ($p in @($Servers.PSObject.Properties)) {
        $command = $null
        if ($p.Value -and $p.Value.PSObject.Properties['command']) { $command = [string]$p.Value.command }
        $out.Add((Get-HorizunLegacyDecision -Name $p.Name -Command $command -CurrentName $CurrentName `
                    -ServerPath $ServerPath -RetiredNames $RetiredNames)) | Out-Null
    }
    return $out.ToArray()
}

# --- text files (keeps BOM and line endings as found) -------------------------------

function Read-HorizunTextFile([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
    $crlf = ([regex]::Matches($text, "`r`n")).Count
    $lf = ([regex]::Matches($text, "(?<!`r)`n")).Count
    [pscustomobject]@{
        Text = $text
        Bom = $bom
        NewLine = $(if ($crlf -ge $lf -and $crlf -gt 0) { "`r`n" } else { "`n" })
        EndsWithNewLine = ($text.EndsWith("`n"))
    }
}

function Split-HorizunTextLines([string]$Text) {
    if ($Text.Length -eq 0) { return @() }
    $t = $Text
    if ($t.EndsWith("`n")) { $t = $t.Substring(0, $t.Length - 1); if ($t.EndsWith("`r")) { $t = $t.Substring(0, $t.Length - 1) } }
    return @($t -split "`r?`n")
}

function Write-HorizunTextFile([string]$Path, [string[]]$Lines, [bool]$Bom, [string]$NewLine) {
    $text = ($Lines -join $NewLine) + $NewLine
    $enc = New-Object System.Text.UTF8Encoding($Bom)
    [IO.File]::WriteAllText($Path, $text, $enc)
}

# --- Codex: TOML ------------------------------------------------------------------

function Get-HorizunTomlHeader([string]$Line) {
    <# $null when the line is not a table header; else keys (unquoted) and array flag. #>
    $t = $Line.Trim()
    if (-not $t.StartsWith('[')) { return $null }
    $m = [regex]::Match($t, '^(\[\[?)\s*(.+?)\s*(\]\]?)\s*(#.*)?$')
    if (-not $m.Success) { return $null }
    if (($m.Groups[1].Value.Length) -ne ($m.Groups[3].Value.Length)) { return $null }
    $body = $m.Groups[2].Value
    $keys = New-Object System.Collections.Generic.List[string]
    $pos = 0
    $tok = [regex]'\G\s*(?:"((?:[^"\\]|\\.)*)"|''([^'']*)''|([A-Za-z0-9_-]+))\s*(\.|$)'
    while ($pos -lt $body.Length) {
        $k = $tok.Match($body, $pos)
        if (-not $k.Success) { return $null }
        if ($k.Groups[1].Success) { $keys.Add(($k.Groups[1].Value -replace '\\(.)', '$1')) | Out-Null }
        elseif ($k.Groups[2].Success) { $keys.Add($k.Groups[2].Value) | Out-Null }
        else { $keys.Add($k.Groups[3].Value) | Out-Null }
        $pos = $k.Index + $k.Length
        if ($k.Groups[4].Value -eq '') { break }
    }
    [pscustomobject]@{ Keys = $keys.ToArray(); IsArray = ($m.Groups[1].Value.Length -eq 2) }
}

function Get-HorizunTomlMcpSections {
    <#
      Every top-level [mcp_servers.<name>] table with the lines it owns (its own
      keys plus its nested [mcp_servers.<name>.*] tables), as
      Name / Start / EndExclusive / Command. Header lines inside a multi-line
      string are not headers.
    #>
    [CmdletBinding()]
    param([string[]]$Lines)
    $headers = New-Object System.Collections.Generic.List[object]
    $inMulti = $null
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($inMulti) {
            if (([regex]::Matches($line, [regex]::Escape($inMulti))).Count % 2 -eq 1) { $inMulti = $null }
            continue
        }
        $h = Get-HorizunTomlHeader $line
        if ($h) { $headers.Add([pscustomobject]@{ Index = $i; Header = $h }) | Out-Null; continue }
        foreach ($q in @('"""', "'''")) {
            if (([regex]::Matches($line, [regex]::Escape($q))).Count % 2 -eq 1) { $inMulti = $q; break }
        }
    }

    $sections = New-Object System.Collections.Generic.List[object]
    for ($n = 0; $n -lt $headers.Count; $n++) {
        $h = $headers[$n].Header
        if ($h.IsArray -or $h.Keys.Count -ne 2 -or $h.Keys[0] -ne 'mcp_servers') { continue }
        $name = $h.Keys[1]
        $start = $headers[$n].Index
        $end = $Lines.Count
        $ownEnd = $Lines.Count
        for ($k = $n + 1; $k -lt $headers.Count; $k++) {
            $hk = $headers[$k].Header
            if ($ownEnd -eq $Lines.Count) { $ownEnd = $headers[$k].Index }
            $nested = ($hk.Keys.Count -gt 2 -and $hk.Keys[0] -eq 'mcp_servers' -and $hk.Keys[1] -eq $name)
            if (-not $nested) { $end = $headers[$k].Index; break }
        }
        $command = $null
        for ($j = $start + 1; $j -lt $ownEnd; $j++) {
            $m = [regex]::Match($Lines[$j], '^\s*command\s*=\s*(?:"((?:[^"\\]|\\.)*)"|''([^'']*)'')')
            if ($m.Success) {
                if ($m.Groups[1].Success) { $command = ($m.Groups[1].Value -replace '\\(.)', '$1') } else { $command = $m.Groups[2].Value }
                break
            }
        }
        $sections.Add([pscustomobject]@{ Name = $name; Start = $start; EndExclusive = $end; Command = $command }) | Out-Null
    }
    return $sections.ToArray()
}

function Remove-HorizunTomlLegacySections {
    <#
      Drop the legacy [mcp_servers.<x>] tables (and their nested tables) and
      nothing else. Returns:
        Lines    the new content (equal to the input when nothing was removed)
        Removed  what went, with the reason
        Skipped  retired names that launch another program (kept)
        Error    non-$null when the structural self-check failed: then Lines is
                 the ORIGINAL and the caller must not write.
      The self-check proves the result is the input minus whole removed ranges:
      same remaining lines in the same order, no line added, every untouched
      table header still there.
    #>
    [CmdletBinding()]
    param(
        [string[]]$Lines,
        [Parameter(Mandatory)][string]$CurrentName,
        [string]$ServerPath,
        [string[]]$RetiredNames = (Get-HorizunRetiredServerNames)
    )
    $sections = @(Get-HorizunTomlMcpSections -Lines $Lines)
    $removed = New-Object System.Collections.Generic.List[object]
    $skipped = New-Object System.Collections.Generic.List[object]
    foreach ($s in $sections) {
        $d = Get-HorizunLegacyDecision -Name $s.Name -Command $s.Command -CurrentName $CurrentName `
                -ServerPath $ServerPath -RetiredNames $RetiredNames
        if ($d.remove) {
            $removed.Add([pscustomobject]@{ Name = $s.Name; Reason = $d.reason; Command = $s.Command; Start = $s.Start; EndExclusive = $s.EndExclusive }) | Out-Null
        }
        elseif ($d.skipped) { $skipped.Add([pscustomobject]@{ Name = $s.Name; Command = $s.Command }) | Out-Null }
    }

    $result = [ordered]@{ Lines = $Lines; Removed = $removed.ToArray(); Skipped = $skipped.ToArray(); Error = $null }
    if ($removed.Count -eq 0) { return [pscustomobject]$result }

    $drop = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($r in $removed) { for ($i = $r.Start; $i -lt $r.EndExclusive; $i++) { [void]$drop.Add($i) } }
    $kept = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Lines.Count; $i++) { if (-not $drop.Contains($i)) { $kept.Add($Lines[$i]) | Out-Null } }

    # --- self-check -----------------------------------------------------------
    $problem = $null
    if ($kept.Count -ne ($Lines.Count - $drop.Count)) { $problem = 'line count does not add up' }
    if (-not $problem) {
        $j = 0
        for ($i = 0; $i -lt $Lines.Count -and -not $problem; $i++) {
            if ($drop.Contains($i)) { continue }
            if ($kept[$j] -cne $Lines[$i]) { $problem = "a kept line changed at input line $($i + 1)" }
            $j++
        }
    }
    if (-not $problem) {
        $before = @(Get-HorizunTomlMcpSections -Lines $Lines | ForEach-Object { $_.Name })
        $after = @(Get-HorizunTomlMcpSections -Lines $kept.ToArray() | ForEach-Object { $_.Name })
        $expected = @($before | Where-Object { $_ -notin @($removed | ForEach-Object { $_.Name }) })
        if (($expected -join '|') -cne ($after -join '|')) { $problem = 'the remaining server tables are not the expected ones' }
    }
    if ($problem) { $result.Error = $problem; return [pscustomobject]$result }
    $result.Lines = $kept.ToArray()
    return [pscustomobject]$result
}

function Test-HorizunTomlWithPython {
    <#
      Extra parse check when a Python with tomllib (3.11+) is at hand.
      $true = parses, $false = does not, $null = could not be checked (no Python,
      too old, anything else). Never the reason a write is allowed, only a reason
      to refuse one.
    #>
    [CmdletBinding()]
    param([string[]]$Lines)
    $py = $null
    foreach ($c in @('python', 'python3', 'py')) {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd -and $cmd.CommandType -eq 'Application') { $py = $cmd.Source; break }
    }
    if (-not $py) { return $null }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('horizun-toml-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        $toml = Join-Path $tmp 'c.toml'
        $script = Join-Path $tmp 'c.py'
        [IO.File]::WriteAllText($toml, (($Lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($script, "import sys`ntry:`n    import tomllib`nexcept Exception:`n    print('NA'); sys.exit(0)`ntry:`n    tomllib.load(open(sys.argv[1], 'rb'))`n    print('OK')`nexcept Exception:`n    print('BAD')`n", (New-Object System.Text.UTF8Encoding($false)))
        $out = (& $py $script $toml 2>$null | Out-String).Trim()
        if ($out -eq 'OK') { return $true }
        if ($out -eq 'BAD') { return $false }
        return $null
    }
    catch { return $null }
    finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
