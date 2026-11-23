$ErrorActionPreference = 'Stop'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('s22-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $root 'a.py'), 'x.get_Geometry(o); x.GetWarnings(); Viewport')
    [IO.File]::Copy((Join-Path $root 'a.py'), (Join-Path $root 'duplicate.py'))
    [IO.File]::WriteAllText((Join-Path $root 'b.py'), 'x.GetInstanceGeometry(); x.GetTotalTransform(); JoinGeometryUtils')
    $result = & (Join-Path $PSScriptRoot '../scripts/s22-intent-cluster.ps1') -Path $root | ConvertFrom-Json
    if ($result.files -ne 3 -or $result.distinct_scripts -ne 2) { throw 'Duplicate scripts must count once.' }
    $counts = @{}; foreach ($row in $result.intentions) { $counts[$row.intent] = $row.scripts }
    if ($counts.geometry -ne 2 -or $counts.warnings -ne 1 -or $counts.sheets -ne 1 -or $counts.links -ne 1 -or $counts.join_geometry -ne 1) { throw 'Synthetic intention counts are incorrect.' }
    if ($counts.worksets -ne 0) { throw 'Unmatched intentions must have zero count.' }
    Write-Host 'S22 synthetic tests passed'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
