#Requires -Version 5.1
<#
  Horizun MCP - original Horizun code.

  EDIT ONE MEMBER OF A CLIENT'S JSON CONFIGURATION, AND NOTHING ELSE.

  The registration helpers used to read a client's configuration with
  Get-Content -Raw | ConvertFrom-Json and write it back with ConvertTo-Json |
  Set-Content -Encoding UTF8. Under Windows PowerShell 5.1 every step of that
  damages a file the helper does not own (audit 2026-10-08):

    * Get-Content without -Encoding reads a BOM-less UTF-8 file as the ANSI code
      page, so "Jos<e-acute>" comes back as two mojibake characters - and is written back that way.
    * Set-Content -Encoding UTF8 writes a BOM, which a Node client's JSON.parse
      does not accept at the start of the document.
    * ConvertTo-Json reformats and re-escapes the WHOLE document.
    * ConvertFrom-Json refuses keys that differ only in case - which
      ~/.claude.json carries routinely, one project entry per spelling of a path -
      so registration failed on exactly the machines that had used Claude most.

  So the document is never re-serialised. A small strict JSON scanner finds the
  byte span of mcpServers.<name> and only that span is replaced, inserted or
  removed; every other character of the file stays exactly as it was. Text is
  read as strict UTF-8 (a file that is not valid UTF-8 is refused, never guessed
  at) and written as UTF-8 WITHOUT a BOM through a temporary file and a move.
#>

function Read-HorizunUtf8Text([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $offset = 3 }
    $strict = New-Object Text.UTF8Encoding($false, $true)
    try { return $strict.GetString($bytes, $offset, $bytes.Length - $offset) }
    catch [Text.DecoderFallbackException] {
        throw "$Path is not valid UTF-8; it was left exactly as it is rather than rewritten in a guessed encoding."
    }
}

function Write-HorizunUtf8Text([string]$Path, [string]$Text) {
    $tmp = "$Path.horizun-tmp-" + [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($tmp, $Text, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

# ---- the scanner ----------------------------------------------------------------
# Index-based over the string. Each function takes the position of the first
# character of the thing and returns the position just after it, or throws.

function Skip-HorizunJsonWs([string]$T, [int]$I) {
    while ($I -lt $T.Length) {
        $c = $T[$I]
        if ($c -eq ' ' -or $c -eq "`t" -or $c -eq "`n" -or $c -eq "`r") { $I++ } else { break }
    }
    return $I
}

function Skip-HorizunJsonString([string]$T, [int]$I) {
    if ($I -ge $T.Length -or $T[$I] -ne '"') { throw "JSON: expected a string at offset $I." }
    $I++
    $stops = [char[]]@('"', '\')
    while ($true) {
        $j = $T.IndexOfAny($stops, $I)
        if ($j -lt 0) { throw 'JSON: unterminated string.' }
        for ($k = $I; $k -lt $j; $k++) { if ([int]$T[$k] -lt 0x20) { throw "JSON: raw control character in a string at offset $k." } }
        if ($T[$j] -eq '"') { return $j + 1 }
        if ($j + 1 -ge $T.Length) { throw 'JSON: unterminated escape.' }
        $e = $T[$j + 1]
        if ($e -eq 'u') {
            if ($j + 6 -gt $T.Length -or $T.Substring($j + 2, 4) -notmatch '^[0-9A-Fa-f]{4}$') { throw "JSON: bad \u escape at offset $j." }
            $I = $j + 6
        }
        elseif ('"\/bfnrt'.IndexOf($e) -ge 0) { $I = $j + 2 }
        else { throw "JSON: bad escape at offset $j." }
    }
}

function Read-HorizunJsonKey([string]$T, [int]$Start, [int]$End) {
    # The decoded text of a string token spanning [Start, End) - already validated
    # by Skip-HorizunJsonString, so every escape here is well formed.
    $sb = New-Object Text.StringBuilder
    $k = $Start + 1
    $last = $End - 1
    while ($k -lt $last) {
        $c = $T[$k]
        if ($c -ne '\') { [void]$sb.Append($c); $k++; continue }
        $e = $T[$k + 1]
        switch -CaseSensitive ($e) {
            'u' { [void]$sb.Append([char][Convert]::ToInt32($T.Substring($k + 2, 4), 16)); $k += 6; continue }
            'b' { [void]$sb.Append([char]8); $k += 2; continue }
            'f' { [void]$sb.Append([char]12); $k += 2; continue }
            'n' { [void]$sb.Append("`n"); $k += 2; continue }
            'r' { [void]$sb.Append("`r"); $k += 2; continue }
            't' { [void]$sb.Append("`t"); $k += 2; continue }
            default { [void]$sb.Append($e); $k += 2; continue }
        }
    }
    return $sb.ToString()
}

$script:HorizunJsonNumber = New-Object Text.RegularExpressions.Regex('\G-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?')

function Skip-HorizunJsonValue([string]$T, [int]$I) {
    $I = Skip-HorizunJsonWs $T $I
    if ($I -ge $T.Length) { throw 'JSON: unexpected end of document.' }
    $c = $T[$I]
    if ($c -eq '"') { return Skip-HorizunJsonString $T $I }
    if ($c -eq '{') { return (Get-HorizunJsonObject $T $I).End }
    if ($c -eq '[') {
        $I = Skip-HorizunJsonWs $T ($I + 1)
        if ($I -lt $T.Length -and $T[$I] -eq ']') { return $I + 1 }
        while ($true) {
            $I = Skip-HorizunJsonWs $T (Skip-HorizunJsonValue $T $I)
            if ($I -ge $T.Length) { throw 'JSON: unterminated array.' }
            if ($T[$I] -eq ']') { return $I + 1 }
            if ($T[$I] -ne ',') { throw "JSON: expected , or ] at offset $I." }
            $I++
        }
    }
    foreach ($lit in 'true', 'false', 'null') {
        if ([string]::CompareOrdinal($T, $I, $lit, 0, $lit.Length) -eq 0) { return $I + $lit.Length }
    }
    $m = $script:HorizunJsonNumber.Match($T, $I)
    if ($m.Success -and $m.Length -gt 0) { return $I + $m.Length }
    throw "JSON: unexpected character '$c' at offset $I."
}

function Get-HorizunJsonObject([string]$T, [int]$Open) {
    # Members of the object whose '{' is at $Open: name, key/value spans, and the
    # offset of its closing '}'. Duplicate names are listed as they occur.
    if ($T[$Open] -ne '{') { throw "JSON: expected an object at offset $Open." }
    $members = New-Object System.Collections.Generic.List[object]
    $I = Skip-HorizunJsonWs $T ($Open + 1)
    if ($I -lt $T.Length -and $T[$I] -eq '}') {
        return [pscustomobject]@{ Open = $Open; Close = $I; End = $I + 1; Members = $members.ToArray() }
    }
    while ($true) {
        $keyStart = $I
        $keyEnd = Skip-HorizunJsonString $T $I
        $I = Skip-HorizunJsonWs $T $keyEnd
        if ($I -ge $T.Length -or $T[$I] -ne ':') { throw "JSON: expected : at offset $I." }
        $valueStart = Skip-HorizunJsonWs $T ($I + 1)
        $valueEnd = Skip-HorizunJsonValue $T $valueStart
        $members.Add([pscustomobject]@{
            Name = Read-HorizunJsonKey $T $keyStart $keyEnd
            KeyStart = $keyStart; ValueStart = $valueStart; ValueEnd = $valueEnd
        }) | Out-Null
        $I = Skip-HorizunJsonWs $T $valueEnd
        if ($I -ge $T.Length) { throw 'JSON: unterminated object.' }
        if ($T[$I] -eq '}') { return [pscustomobject]@{ Open = $Open; Close = $I; End = $I + 1; Members = $members.ToArray() } }
        if ($T[$I] -ne ',') { throw "JSON: expected , or } at offset $I." }
        $I = Skip-HorizunJsonWs $T ($I + 1)
    }
}

function Get-HorizunJsonRoot([string]$T) {
    # The top-level object, after proving the WHOLE document is one valid value.
    $start = Skip-HorizunJsonWs $T 0
    if ($start -ge $T.Length -or $T[$start] -ne '{') { throw 'JSON: the document is not an object.' }
    $root = Get-HorizunJsonObject $T $start
    if ((Skip-HorizunJsonWs $T $root.End) -ne $T.Length) { throw "JSON: trailing content after offset $($root.End)." }
    return $root
}

function ConvertTo-HorizunJsonString([string]$S) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $S.ToCharArray()) {
        switch ($ch) {
            '"' { [void]$sb.Append('\"'); continue }
            '\' { [void]$sb.Append('\\'); continue }
            default {
                if ([int]$ch -lt 0x20) { [void]$sb.Append(('\u{0:x4}' -f [int]$ch)) } else { [void]$sb.Append($ch) }
            }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Get-HorizunJsonIndent([string]$T, [int]$Pos) {
    # The whitespace that starts the line containing $Pos.
    $lineStart = $T.LastIndexOf("`n", [Math]::Max(0, $Pos - 1)) + 1
    $k = $lineStart
    while ($k -lt $T.Length -and ($T[$k] -eq ' ' -or $T[$k] -eq "`t")) { $k++ }
    return $T.Substring($lineStart, $k - $lineStart)
}

function Get-HorizunJsonNewline([string]$T) { if ($T.Contains("`r`n")) { return "`r`n" } return "`n" }

function Read-HorizunTextLines([string]$Path) {
    # The file's lines, its line ending and whether it ended with one, so writing
    # the lines back reproduces every byte that was not edited.
    $text = Read-HorizunUtf8Text $Path
    $nl = Get-HorizunJsonNewline $text
    $trailing = $text.EndsWith("`n")
    $body = $text
    if ($text.EndsWith("`r`n")) { $body = $text.Substring(0, $text.Length - 2) }
    elseif ($trailing) { $body = $text.Substring(0, $text.Length - 1) }
    $lines = @()
    if ($body.Length -gt 0) { $lines = @($body -split "`r?`n") }
    return [pscustomobject]@{ Lines = $lines; Newline = $nl; Trailing = $trailing }
}

function Write-HorizunTextLines([string]$Path, [string[]]$Lines, $Shape) {
    $text = ($Lines -join $Shape.Newline)
    if ($Shape.Trailing -or $Lines.Count -gt 0) { $text += $Shape.Newline }
    Write-HorizunUtf8Text $Path $text
}

function Format-HorizunJsonEntry([System.Collections.IDictionary]$Fields, [string]$Indent, [string]$Step, [string]$Nl) {
    # A small object of string / empty-array / empty-object fields, pretty-printed
    # in the file's own indentation.
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($k in $Fields.Keys) {
        $v = $Fields[$k]
        $text = if ($v -is [string]) { ConvertTo-HorizunJsonString $v }
                elseif ($v -is [array]) { '[]' }
                else { '{}' }
        $lines.Add($Indent + $Step + (ConvertTo-HorizunJsonString $k) + ': ' + $text) | Out-Null
    }
    return '{' + $Nl + ($lines -join (',' + $Nl)) + $Nl + $Indent + '}'
}

function Get-HorizunMcpServerNames([string]$T) {
    $root = Get-HorizunJsonRoot $T
    $servers = @($root.Members | Where-Object { $_.Name -ceq 'mcpServers' })
    if ($servers.Count -eq 0) { return @() }
    if ($T[$servers[-1].ValueStart] -ne '{') { throw 'JSON: mcpServers is not an object.' }
    return @((Get-HorizunJsonObject $T $servers[-1].ValueStart).Members | ForEach-Object { $_.Name })
}

function Get-HorizunJsonTopNames([string]$T) {
    return @((Get-HorizunJsonRoot $T).Members | ForEach-Object { $_.Name })
}

function Get-HorizunMcpServerCommand([string]$T, [string]$Name) {
    # The decoded "command" string of mcpServers.<Name>, or $null when the entry or
    # its command is absent. Read without ConvertFrom-Json, so a document with keys
    # that differ only in case elsewhere is read like any other.
    $root = Get-HorizunJsonRoot $T
    $servers = @($root.Members | Where-Object { $_.Name -ceq 'mcpServers' })
    if ($servers.Count -eq 0 -or $T[$servers[-1].ValueStart] -ne '{') { return $null }
    $entry = @((Get-HorizunJsonObject $T $servers[-1].ValueStart).Members | Where-Object { $_.Name -ceq $Name })
    if ($entry.Count -eq 0 -or $T[$entry[-1].ValueStart] -ne '{') { return $null }
    $command = @((Get-HorizunJsonObject $T $entry[-1].ValueStart).Members | Where-Object { $_.Name -ceq 'command' })
    if ($command.Count -eq 0 -or $T[$command[-1].ValueStart] -ne '"') { return $null }
    return Read-HorizunJsonKey $T $command[-1].ValueStart $command[-1].ValueEnd
}

function Set-HorizunMcpServerEntry([string]$T, [string]$Name, [System.Collections.IDictionary]$Fields) {
    # Returns the document with mcpServers.<Name> set to $Fields. Only that span
    # changes; mcpServers is created when absent.
    $nl = Get-HorizunJsonNewline $T
    $root = Get-HorizunJsonRoot $T
    $servers = @($root.Members | Where-Object { $_.Name -ceq 'mcpServers' })
    if ($servers.Count -gt 1) { throw 'JSON: mcpServers appears more than once; refusing to guess which one the client reads.' }

    if ($servers.Count -eq 0) {
        $memberIndent = if ($root.Members.Count -gt 0) { Get-HorizunJsonIndent $T $root.Members[0].KeyStart } else { '  ' }
        $step = if ($memberIndent.Length -gt 0) { $memberIndent } else { '  ' }
        $entry = Format-HorizunJsonEntry $Fields ($memberIndent + $step) $step $nl
        $block = (ConvertTo-HorizunJsonString 'mcpServers') + ': {' + $nl + $memberIndent + $step +
                 (ConvertTo-HorizunJsonString $Name) + ': ' + $entry + $nl + $memberIndent + '}'
        if ($root.Members.Count -gt 0) {
            $last = $root.Members[$root.Members.Count - 1]
            return $T.Substring(0, $last.ValueEnd) + ',' + $nl + $memberIndent + $block + $T.Substring($last.ValueEnd)
        }
        return $T.Substring(0, $root.Open + 1) + $nl + $memberIndent + $block + $nl + $T.Substring($root.Close)
    }

    $serversOpen = $servers[0].ValueStart
    if ($T[$serversOpen] -ne '{') { throw 'JSON: mcpServers is not an object.' }
    $obj = Get-HorizunJsonObject $T $serversOpen
    $parentIndent = Get-HorizunJsonIndent $T $servers[0].KeyStart
    $memberIndent = if ($obj.Members.Count -gt 0) { Get-HorizunJsonIndent $T $obj.Members[0].KeyStart } else { $parentIndent + '  ' }
    $step = $memberIndent.Substring([Math]::Min($parentIndent.Length, $memberIndent.Length))
    if (-not $step) { $step = '  ' }
    $entry = Format-HorizunJsonEntry $Fields $memberIndent $step $nl

    $same = @($obj.Members | Where-Object { $_.Name -ceq $Name })
    if ($same.Count -gt 1) { throw "JSON: mcpServers.$Name appears more than once; refusing to guess which one the client reads." }
    if ($same.Count -eq 1) {
        return $T.Substring(0, $same[0].ValueStart) + $entry + $T.Substring($same[0].ValueEnd)
    }
    $member = (ConvertTo-HorizunJsonString $Name) + ': ' + $entry
    if ($obj.Members.Count -gt 0) {
        $last = $obj.Members[$obj.Members.Count - 1]
        return $T.Substring(0, $last.ValueEnd) + ',' + $nl + $memberIndent + $member + $T.Substring($last.ValueEnd)
    }
    return $T.Substring(0, $obj.Open + 1) + $nl + $memberIndent + $member + $nl + $parentIndent + $T.Substring($obj.Close)
}

function Remove-HorizunMcpServerEntry([string]$T, [string]$Name) {
    # Returns the document without mcpServers.<Name>, or the same text when absent.
    $root = Get-HorizunJsonRoot $T
    $servers = @($root.Members | Where-Object { $_.Name -ceq 'mcpServers' })
    if ($servers.Count -eq 0) { return $T }
    if ($servers.Count -gt 1) { throw 'JSON: mcpServers appears more than once; refusing to guess which one the client reads.' }
    if ($T[$servers[0].ValueStart] -ne '{') { throw 'JSON: mcpServers is not an object.' }
    $obj = Get-HorizunJsonObject $T $servers[0].ValueStart
    $members = @($obj.Members)
    for ($n = 0; $n -lt $members.Count; $n++) {
        if ($members[$n].Name -cne $Name) { continue }
        if ($members.Count -eq 1) {
            # The only member: leave an empty object behind.
            return $T.Substring(0, $obj.Open + 1) + $T.Substring($obj.Close)
        }
        if ($n -lt $members.Count - 1) {
            # Remove from this key up to the next key (takes the comma and the line break).
            return $T.Substring(0, $members[$n].KeyStart) + $T.Substring($members[$n + 1].KeyStart)
        }
        # The last member: remove from the end of the previous value.
        return $T.Substring(0, $members[$n - 1].ValueEnd) + $T.Substring($members[$n].ValueEnd)
    }
    return $T
}
