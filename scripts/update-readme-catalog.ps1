#Requires -Version 5.1
<# Render the complete bilingual catalog and exact suboperations into GitHub's
   primary discovery surface. Reads documentation only; never starts Revit. #>
[CmdletBinding()]
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$inventory = Get-Content (Join-Path $repo 'docs/inventory.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$catalog = Get-Content (Join-Path $repo 'docs/readme-catalog.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$tools = @{}
foreach ($tool in $inventory.tools_detail) { $tools[$tool.tool] = $tool }
$names = @($catalog.groups | ForEach-Object { $_.tools | ForEach-Object { $_[0] } })
if ((($names | Sort-Object) -join '|') -cne (($tools.Keys | Sort-Object) -join '|')) {
    throw 'The bilingual catalog must describe every inventory tool exactly once.'
}

function New-CatalogBlock([string]$Language) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<!-- BEGIN TOOL CATALOG -->')
    foreach ($group in $catalog.groups) {
        $lines.Add('### ' + $group.$Language)
        $lines.Add('')
        $lines.Add($(if ($Language -eq 'en') { '| Tool | Capability |' } else { '| Herramienta | Capacidad |' }))
        $lines.Add('|---|---|')
        foreach ($entry in $group.tools) {
            $description = $entry[$(if ($Language -eq 'en') { 1 } else { 2 })]
            if ([string]::IsNullOrWhiteSpace($description) -or $description -match '[\r\n|]') {
                throw "Invalid table description for $($entry[0]) / $Language."
            }
            $lines.Add(('| `{0}` | {1} |' -f $entry[0], $description))
        }
        $lines.Add('')
    }
    $lines.Add('<!-- END TOOL CATALOG -->')
    return $lines -join "`n"
}

function New-SuboperationsBlock([string]$Language) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<!-- BEGIN SUBOPERATIONS -->')
    $lines.Add($(if ($Language -eq 'en') { '| Tool | Selector | Named suboperations and modes |' } else { '| Herramienta | Selector | Suboperaciones y modos nombrados |' }))
    $lines.Add('|---|---|---|')
    $count = 0
    foreach ($name in $names) {
        $selectors = [ordered]@{}
        foreach ($detail in $tools[$name].operation_detail) {
            if (-not $selectors.Contains($detail.property)) { $selectors[$detail.property] = [System.Collections.Generic.List[string]]::new() }
            foreach ($value in $detail.values) {
                if (-not $selectors[$detail.property].Contains($value)) { $selectors[$detail.property].Add($value) }
            }
        }
        foreach ($key in $selectors.Keys) {
            $values = $selectors[$key]
            $count += $values.Count
            $formatted = ($values | ForEach-Object { '`' + $_ + '`' }) -join ', '
            $lines.Add(('| `{0}` | `{1}` | {2} |' -f $name, $key, $formatted))
        }
    }
    if ($count -ne $inventory.counts.operations) { throw 'Distinct selector choices do not match the inventory count.' }
    $lines.Add('<!-- END SUBOPERATIONS -->')
    return $lines -join "`n"
}

foreach ($language in @('en', 'es')) {
    $name = if ($language -eq 'en') { 'README.md' } else { 'README.es.md' }
    $path = Join-Path $repo $name
    $before = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $after = $before
    foreach ($section in @('TOOL CATALOG', 'SUBOPERATIONS')) {
        $pattern = '(?s)<!-- BEGIN ' + $section + ' -->.*?<!-- END ' + $section + ' -->'
        if ([regex]::Matches($after, $pattern).Count -ne 1) { throw "$name needs exactly one $section block." }
        $replacement = if ($section -eq 'TOOL CATALOG') { New-CatalogBlock $language } else { New-SuboperationsBlock $language }
        $after = [regex]::Replace($after, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement })
    }
    $after = [regex]::Replace($after, '\*\*[\d,]+([^*]*)\*\*\s*<!--inventory:([a-z_]+)-->', [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $key = $match.Groups[2].Value
        $value = $inventory.counts.$key
        if ($null -eq $value) { throw "Unknown inventory counter $key." }
        '**' + $value + $match.Groups[1].Value + '** <!--inventory:' + $key + '-->'
    })
    if ($Check) {
        if ($after -cne $before) { throw "$name catalog is stale: run scripts/update-readme-catalog.ps1." }
        Write-Host "[readme-catalog] checked $name"
    } else {
        [IO.File]::WriteAllText($path, $after, [Text.UTF8Encoding]::new($false))
        Write-Host "[readme-catalog] updated $name"
    }
}
