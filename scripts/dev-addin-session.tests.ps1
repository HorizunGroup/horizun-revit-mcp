$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dev-addin-session.ps1') -Raw
if ($source -notmatch '\$aside\s*=\s*Join-Path \$root "original-\$Year\.addin"') {
    throw 'The original manifest must be moved outside Revit Addins during a dev session.'
}
'PASS: original manifest is outside the Revit Addins scan directory.'
